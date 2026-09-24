using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Store.Application.Abstractions;
using Store.Domain.Carts;
using Store.Domain.Catalog;
using Store.Domain.Common;

namespace Store.Application.Carts;

/// <summary>
/// Identifies the cart for a request: the signed-in customer's, or a guest cart from the cart cookie.
/// When a customer is signed in, the guest id is ignored.
/// </summary>
public sealed record CartOwner(Guid? CustomerId, Guid? GuestCartId);

public sealed record CartDto(Guid CartId, IReadOnlyList<CartLineDto> Lines, int ItemCount, decimal Subtotal, decimal ShippingFee, decimal Total, string CurrencyCode, bool HasUnavailableItems);

public sealed record CartLineDto(
    Guid VariantId,
    string ProductSlug,
    string ProductName,
    string VariantLabel,
    string? ImageUrl,
    decimal UnitPrice,
    int Quantity,
    decimal LineTotal,
    bool IsAvailable,
    int AvailableQuantity);

public sealed record GetCartQuery(CartOwner Owner) : IQuery<CartDto?>;

internal sealed class GetCartHandler(IAppDbContext db, CartReader reader) : IQueryHandler<GetCartQuery, CartDto?>
{
    public async Task<Result<CartDto?>> Handle(GetCartQuery query, CancellationToken cancellationToken)
    {
        var cart = await CartStore.FindAsync(db, query.Owner, tracking: false, cancellationToken);
        return cart is null ? Result.Success<CartDto?>(null) : await reader.ReadAsync(cart, cancellationToken);
    }
}

public sealed record AddToCartCommand(CartOwner Owner, Guid VariantId, int Quantity) : ICommand<CartDto>;

public sealed class AddToCartValidator : AbstractValidator<AddToCartCommand>
{
    public AddToCartValidator()
    {
        RuleFor(x => x.VariantId).NotEmpty();
        RuleFor(x => x.Quantity).InclusiveBetween(1, Cart.MaxQuantityPerLine);
    }
}

internal sealed class AddToCartHandler(IAppDbContext db, CartReader reader) : ICommandHandler<AddToCartCommand, CartDto>
{
    public async Task<Result<CartDto>> Handle(AddToCartCommand command, CancellationToken cancellationToken)
    {
        // Only let customers add things they could actually buy right now.
        var sellable = await db.ProductVariants.AsNoTracking()
            .Where(v => v.Id == command.VariantId && v.IsActive)
            .Join(db.Products.Where(p => p.IsPublished), v => v.ProductId, p => p.Id, (v, _) => v)
            .FirstOrDefaultAsync(cancellationToken);

        if (sellable is null)
        {
            return CatalogErrors.VariantNotFound;
        }

        if (!sellable.IsInStock)
        {
            return CatalogErrors.InsufficientStock(sellable.Sku, 0);
        }

        var cart = await CartStore.GetOrCreateAsync(db, command.Owner, cancellationToken);
        var result = cart.AddItem(command.VariantId, command.Quantity);
        if (result.IsFailure)
        {
            return result.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await reader.ReadAsync(cart, cancellationToken);
    }
}

public sealed record UpdateCartItemCommand(CartOwner Owner, Guid VariantId, int Quantity) : ICommand<CartDto>;

public sealed class UpdateCartItemValidator : AbstractValidator<UpdateCartItemCommand>
{
    public UpdateCartItemValidator() => RuleFor(x => x.Quantity).InclusiveBetween(0, Cart.MaxQuantityPerLine);
}

internal sealed class UpdateCartItemHandler(IAppDbContext db, CartReader reader) : ICommandHandler<UpdateCartItemCommand, CartDto>
{
    public async Task<Result<CartDto>> Handle(UpdateCartItemCommand command, CancellationToken cancellationToken)
    {
        var cart = await CartStore.FindAsync(db, command.Owner, tracking: true, cancellationToken);
        if (cart is null)
        {
            return CartErrors.ItemNotFound;
        }

        var result = cart.SetQuantity(command.VariantId, command.Quantity);
        if (result.IsFailure)
        {
            return result.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await reader.ReadAsync(cart, cancellationToken);
    }
}

/// <summary>
/// Runs at sign-in: moves the guest cart's items into the customer's cart, or hands the guest cart over
/// if the customer has none. Afterwards the guest cookie can be dropped.
/// </summary>
public sealed record MergeGuestCartCommand(Guid CustomerId, Guid GuestCartId) : ICommand;

internal sealed class MergeGuestCartHandler(IAppDbContext db) : ICommandHandler<MergeGuestCartCommand>
{
    public async Task<Result> Handle(MergeGuestCartCommand command, CancellationToken cancellationToken)
    {
        var guest = await db.Carts.Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.Id == command.GuestCartId && c.CustomerId == null, cancellationToken);
        if (guest is null)
        {
            return Result.Success();
        }

        var own = await db.Carts.Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.CustomerId == command.CustomerId, cancellationToken);

        if (own is null)
        {
            guest.AssignTo(command.CustomerId);
        }
        else
        {
            own.MergeFrom(guest);
            db.Carts.Remove(guest);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

internal static class CartStore
{
    public static Task<Cart?> FindAsync(IAppDbContext db, CartOwner owner, bool tracking, CancellationToken cancellationToken)
    {
        var carts = tracking ? db.Carts : db.Carts.AsNoTracking();
        carts = carts.Include(c => c.Items);

        if (owner.CustomerId is { } customerId)
        {
            return carts.FirstOrDefaultAsync(c => c.CustomerId == customerId, cancellationToken);
        }

        return owner.GuestCartId is { } cartId
            ? carts.FirstOrDefaultAsync(c => c.Id == cartId && c.CustomerId == null, cancellationToken)
            : Task.FromResult<Cart?>(null);
    }

    public static async Task<Cart> GetOrCreateAsync(IAppDbContext db, CartOwner owner, CancellationToken cancellationToken)
    {
        var cart = await FindAsync(db, owner, tracking: true, cancellationToken);
        if (cart is not null)
        {
            return cart;
        }

        cart = owner.CustomerId is { } customerId ? Cart.ForCustomer(customerId) : Cart.ForGuest();
        db.Carts.Add(cart);
        return cart;
    }
}

/// <summary>Folds the guest cart into the account after a successful sign-in. One copy for every sign-in path.</summary>
internal static class GuestCartMerge
{
    public static async Task AfterSignInAsync(
        ICommandHandler<MergeGuestCartCommand> merge,
        Result<UserProfile> signIn,
        Guid? guestCartId,
        CancellationToken cancellationToken)
    {
        if (signIn.IsSuccess && guestCartId is { } cartId)
        {
            await merge.Handle(new MergeGuestCartCommand(signIn.Value.Id, cartId), cancellationToken);
        }
    }
}
