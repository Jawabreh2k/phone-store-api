using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Store.Application.Abstractions;
using Store.Application.Carts;
using Store.Application.Common;
using Store.Domain.Carts;
using Store.Domain.Common;
using Store.Domain.Customers;
using Store.Domain.Orders;

namespace Store.Application.Orders;

public sealed record AddressInput(string City, string Area, string Street, string? Building, string? Details)
{
    public ShippingAddress ToDomain() => new(City, Area, Street, Building, Details);
}

public sealed record PlaceOrderCommand(
    CartOwner Owner,
    string FullName,
    string Phone,
    string? Email,
    AddressInput Address,
    string? Note,
    bool SaveAddress) : ICommand<OrderPlacedDto>;

/// <summary>
/// <see cref="TrackingToken"/> is returned only here (the database keeps its hash). The storefront builds
/// the guest tracking link from it; the order number is for talking to staff.
/// </summary>
public sealed record OrderPlacedDto(Guid OrderId, string OrderNumber, string TrackingToken, decimal Total, string CurrencyCode);

public sealed class AddressInputValidator : AbstractValidator<AddressInput>
{
    public AddressInputValidator()
    {
        RuleFor(x => x.City).NotEmpty().MaximumLength(80);
        RuleFor(x => x.Area).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Street).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Building).MaximumLength(100);
        RuleFor(x => x.Details).MaximumLength(300);
    }
}

public sealed class PlaceOrderValidator : AbstractValidator<PlaceOrderCommand>
{
    public PlaceOrderValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Phone).NotEmpty().Phone();
        RuleFor(x => x.Email).EmailAddress().MaximumLength(256).When(x => !string.IsNullOrEmpty(x.Email));
        RuleFor(x => x.Address).NotNull().SetValidator(new AddressInputValidator());
        RuleFor(x => x.Note).MaximumLength(Order.NoteMaxLength);
    }
}

/// <summary>
/// Turns the cart into an order in one transaction: re-prices every line from the catalogue,
/// reserves stock, snapshots names and prices onto the order and empties the cart.
/// </summary>
internal sealed class PlaceOrderHandler(
    IAppDbContext db,
    IOrderNumberGenerator orderNumbers,
    TimeProvider clock) : ICommandHandler<PlaceOrderCommand, OrderPlacedDto>
{
    public static readonly Error StockChanged = Error.Conflict(
        "orders.stock_changed", "Stock changed while you were checking out. Please review your cart and try again.");

    public async Task<Result<OrderPlacedDto>> Handle(PlaceOrderCommand command, CancellationToken cancellationToken)
    {
        var settings = await db.StoreSettings.AsNoTracking().FirstAsync(cancellationToken);
        if (command.Owner.CustomerId is null && !settings.AllowGuestCheckout)
        {
            return OrderErrors.GuestCheckoutDisabled;
        }

        var phone = PhoneInput.Normalize(settings, command.Phone, "phone");
        if (phone.IsFailure)
        {
            return phone.Error;
        }

        var cart = await CartStore.FindAsync(db, command.Owner, tracking: true, cancellationToken);
        if (cart is null || cart.Items.Count == 0)
        {
            return CartErrors.Empty;
        }

        var linesResult = await BuildLinesAndReserveStockAsync(cart, cancellationToken);
        if (linesResult.IsFailure)
        {
            return linesResult.Error;
        }

        var lines = linesResult.Value;
        var subtotal = lines.Sum(l => l.UnitPrice * l.Quantity);
        var address = command.Address.ToDomain();
        var trackingToken = OrderTrackingToken.Generate();

        var orderResult = Order.Place(
            await orderNumbers.NextAsync(cancellationToken),
            OrderTrackingToken.Hash(trackingToken),
            settings.CurrencyCode,
            command.Owner.CustomerId,
            new ContactInfo(command.FullName, phone.Value, command.Email),
            address,
            lines,
            settings.ShippingFeeFor(subtotal),
            command.Note,
            clock.GetUtcNow());

        if (orderResult.IsFailure)
        {
            return orderResult.Error;
        }

        var order = orderResult.Value;
        db.Orders.Add(order);
        cart.Clear();

        if (command.SaveAddress && command.Owner.CustomerId is { } customerId)
        {
            await SaveAddressAsync(customerId, command.FullName, phone.Value, address, cancellationToken);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another checkout or a stock adjustment touched the same variant between our read and write.
            // Nothing was saved; the client re-reads the cart and retries.
            return StockChanged;
        }

        return new OrderPlacedDto(order.Id, order.OrderNumber, trackingToken, order.Total, order.CurrencyCode);
    }

    private async Task<Result<List<OrderLineInput>>> BuildLinesAndReserveStockAsync(Cart cart, CancellationToken cancellationToken)
    {
        var variantIds = cart.Items.Select(i => i.VariantId).ToList();
        var products = await db.Products
            .Include(p => p.Variants)
            .Include(p => p.Images)
            .Where(p => p.Variants.Any(v => variantIds.Contains(v.Id)))
            .ToListAsync(cancellationToken);

        var lines = new List<OrderLineInput>(cart.Items.Count);
        foreach (var item in cart.Items)
        {
            var product = products.Find(p => p.FindVariant(item.VariantId) is not null);
            var variant = product?.FindVariant(item.VariantId);
            if (product is null || variant is null || !product.IsPublished || !variant.IsActive)
            {
                return CartErrors.ItemUnavailable;
            }

            var reserved = product.ReserveStock(variant.Id, item.Quantity);
            if (reserved.IsFailure)
            {
                return reserved.Error;
            }

            var image = product.Images
                .OrderBy(i => i.VariantId == variant.Id ? 0 : 1).ThenBy(i => i.SortOrder)
                .FirstOrDefault()?.Url;

            lines.Add(new OrderLineInput(
                product.Id,
                variant.Id,
                variant.Sku,
                product.Name,
                new LocalizedText(Fallback(variant.Options.Describe(Language.En)), Fallback(variant.Options.Describe(Language.Ar))),
                image,
                variant.Price,
                item.Quantity));
        }

        return lines;

        static string Fallback(string label) => string.IsNullOrWhiteSpace(label) ? "-" : label;
    }

    private async Task SaveAddressAsync(Guid customerId, string fullName, PhoneNumber phone, ShippingAddress address, CancellationToken cancellationToken)
    {
        var existing = await db.CustomerAddresses.Where(a => a.CustomerId == customerId).ToListAsync(cancellationToken);
        if (existing.Count >= CustomerAddress.MaxPerCustomer || existing.Exists(a => a.Address == address))
        {
            return;
        }

        var saved = new CustomerAddress(customerId, $"{address.City} - {address.Area}", fullName, phone, address);
        if (existing.Count == 0)
        {
            saved.MarkDefault();
        }

        db.CustomerAddresses.Add(saved);
    }
}
