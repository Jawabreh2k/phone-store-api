using Store.Domain.Common;

namespace Store.Domain.Orders;

public enum OrderStatus
{
    Pending = 1,
    Confirmed = 2,
    Shipped = 3,
    Delivered = 4,
    Cancelled = 5,
    Returned = 6,
}

/// <summary>Only cash on delivery exists today. The field is here so a card gateway can be added without migrating orders.</summary>
public enum PaymentMethod
{
    CashOnDelivery = 1,
}

public enum PaymentStatus
{
    Unpaid = 1,
    Paid = 2,
}

/// <summary>Who to call about the order. Copied onto the order so later profile edits don't rewrite history.</summary>
public sealed record ContactInfo
{
    public ContactInfo(string fullName, PhoneNumber phone, string? email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        ArgumentNullException.ThrowIfNull(phone);
        FullName = fullName.Trim();
        Phone = phone.Value;
        Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
    }

    // EF materialization: values come from the database, already canonical.
    private ContactInfo(string fullName, string phone, string? email)
    {
        FullName = fullName;
        Phone = phone;
        Email = email;
    }

    public string FullName { get; private init; }

    /// <summary>Canonical E.164 (see <see cref="PhoneNumber"/>).</summary>
    public string Phone { get; private init; }

    public string? Email { get; private init; }
}

/// <summary>Delivery address snapshot.</summary>
public sealed record ShippingAddress
{
    public ShippingAddress(string city, string area, string street, string? building, string? details)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(city);
        ArgumentException.ThrowIfNullOrWhiteSpace(area);
        ArgumentException.ThrowIfNullOrWhiteSpace(street);
        City = city.Trim();
        Area = area.Trim();
        Street = street.Trim();
        Building = string.IsNullOrWhiteSpace(building) ? null : building.Trim();
        Details = string.IsNullOrWhiteSpace(details) ? null : details.Trim();
    }

    public string City { get; private init; }
    public string Area { get; private init; }
    public string Street { get; private init; }
    public string? Building { get; private init; }
    public string? Details { get; private init; }
}

/// <summary>
/// Raised whenever the order moves to a new status (not for the initial Pending entry written at checkout).
/// <paramref name="HistoryEntryId"/> identifies the change, so reactions to it can be made idempotent.
/// </summary>
public sealed record OrderStatusChanged(Guid OrderId, Guid HistoryEntryId, OrderStatus Status, bool ByCustomer) : IDomainEvent;

/// <summary>What the customer is buying, priced at the moment of checkout.</summary>
public sealed record OrderLineInput(
    Guid ProductId,
    Guid VariantId,
    string Sku,
    LocalizedText ProductName,
    LocalizedText VariantDescription,
    string? ImageUrl,
    decimal UnitPrice,
    int Quantity);

public static class OrderErrors
{
    public static readonly Error NotFound = Error.NotFound("orders.not_found", "The order was not found.");
    public static readonly Error NoItems = Error.Validation("orders.no_items", "An order needs at least one item.");
    public static readonly Error InvalidLine = Error.Validation("orders.invalid_line", "Every order line needs a positive price and quantity.");
    public static readonly Error InvalidCurrency = Error.Validation("orders.invalid_currency", "The order currency must be a 3-letter ISO 4217 code.");
    public static readonly Error CannotCancelAfterShipping = Error.Conflict("orders.cannot_cancel", "The order can no longer be cancelled.");
    public static readonly Error CannotReturn = Error.Conflict("orders.cannot_return", "The order can only be returned after delivery.");
    public static readonly Error CustomerCanOnlyCancelPending = Error.Conflict("orders.customer_cancel_pending_only", "You can only cancel an order before the store confirms it. Please contact us.");
    public static readonly Error GuestCheckoutDisabled = Error.Unauthorized("orders.guest_checkout_disabled", "Please sign in to place an order.");

    public static Error InvalidTransition(OrderStatus from, OrderStatus to) =>
        Error.Conflict("orders.invalid_transition", $"An order cannot move from {from} to {to}.");
}
