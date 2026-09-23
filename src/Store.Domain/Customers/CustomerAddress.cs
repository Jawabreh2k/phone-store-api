using Store.Domain.Common;
using Store.Domain.Orders;

namespace Store.Domain.Customers;

/// <summary>A delivery address saved to a customer's account for faster checkout.</summary>
public sealed class CustomerAddress : AuditableEntity
{
    public const int MaxPerCustomer = 10;

    private CustomerAddress() { } // EF

    public CustomerAddress(Guid customerId, string label, string recipientName, PhoneNumber phone, ShippingAddress address)
    {
        CustomerId = customerId;
        Update(label, recipientName, phone, address);
    }

    public Guid CustomerId { get; private set; }

    /// <summary>Customer's own name for it, e.g. "Home" or "Work".</summary>
    public string Label { get; private set; } = null!;

    public string RecipientName { get; private set; } = null!;

    /// <summary>Canonical E.164 (see <see cref="PhoneNumber"/>).</summary>
    public string Phone { get; private set; } = null!;

    public ShippingAddress Address { get; private set; } = null!;
    public bool IsDefault { get; private set; }

    public void Update(string label, string recipientName, PhoneNumber phone, ShippingAddress address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientName);
        ArgumentNullException.ThrowIfNull(phone);
        ArgumentNullException.ThrowIfNull(address);
        Label = label.Trim();
        RecipientName = recipientName.Trim();
        Phone = phone.Value;
        Address = address;
    }

    // Only one address may be the default; the application layer clears the others in the same transaction.
    public void MarkDefault() => IsDefault = true;
    public void ClearDefault() => IsDefault = false;
}

public static class CustomerErrors
{
    public static readonly Error AddressNotFound = Error.NotFound("customers.address_not_found", "The address was not found.");
    public static readonly Error TooManyAddresses = Error.Validation("customers.too_many_addresses", $"You can save up to {CustomerAddress.MaxPerCustomer} addresses.");
}
