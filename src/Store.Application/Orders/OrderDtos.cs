using Store.Domain.Common;
using Store.Domain.Orders;

namespace Store.Application.Orders;

// Every money-bearing order DTO carries the order's own CurrencyCode, never the store's current one.

public sealed record OrderSummaryDto(
    Guid Id,
    string OrderNumber,
    OrderStatus Status,
    DateTimeOffset PlacedAt,
    int ItemCount,
    decimal Total,
    string CurrencyCode,
    string? FirstItemImageUrl);

public sealed record OrderDetailsDto(
    Guid Id,
    string OrderNumber,
    OrderStatus Status,
    PaymentMethod PaymentMethod,
    PaymentStatus PaymentStatus,
    DateTimeOffset PlacedAt,
    ContactDto Contact,
    AddressInput ShippingAddress,
    string? CustomerNote,
    string? CancellationReason,
    IReadOnlyList<OrderItemDto> Items,
    decimal Subtotal,
    decimal ShippingFee,
    decimal Total,
    string CurrencyCode,
    IReadOnlyList<OrderStatusChangeDto> History,
    bool CanCancel);

public sealed record ContactDto(string FullName, string Phone, string? Email);

public sealed record OrderItemDto(Guid ProductId, Guid VariantId, string Sku, string ProductName, string VariantLabel, string? ImageUrl, decimal UnitPrice, int Quantity, decimal LineTotal);

public sealed record OrderStatusChangeDto(OrderStatus Status, DateTimeOffset OccurredAt, string? Note);

/// <summary>Admin view: adds the customer id, the actor of each change and the allowed next statuses.</summary>
public sealed record AdminOrderDetailsDto(OrderDetailsDto Order, Guid? CustomerId, IReadOnlyList<AdminStatusChangeDto> History, IReadOnlyList<OrderStatus> NextStatuses);

public sealed record AdminStatusChangeDto(OrderStatus Status, DateTimeOffset OccurredAt, Guid? ChangedBy, string? Note);

public sealed record AdminOrderListItemDto(
    Guid Id,
    string OrderNumber,
    OrderStatus Status,
    PaymentStatus PaymentStatus,
    DateTimeOffset PlacedAt,
    string CustomerName,
    string Phone,
    string City,
    int ItemCount,
    decimal Total,
    string CurrencyCode,
    bool IsGuest);

internal static class OrderMapping
{
    public static OrderDetailsDto ToDetails(Order o, Language lang, bool customerCanCancel) => new(
        o.Id,
        o.OrderNumber,
        o.Status,
        o.PaymentMethod,
        o.PaymentStatus,
        o.PlacedAt,
        new ContactDto(o.Contact.FullName, o.Contact.Phone, o.Contact.Email),
        new AddressInput(o.ShippingAddress.City, o.ShippingAddress.Area, o.ShippingAddress.Street, o.ShippingAddress.Building, o.ShippingAddress.Details),
        o.CustomerNote,
        o.CancellationReason,
        o.Items.Select(i => new OrderItemDto(
            i.ProductId, i.VariantId, i.Sku, i.ProductName.In(lang), i.VariantDescription.In(lang),
            i.ImageUrl, i.UnitPrice, i.Quantity, i.LineTotal)).ToList(),
        o.Subtotal,
        o.ShippingFee,
        o.Total,
        o.CurrencyCode,
        o.History.OrderBy(h => h.OccurredAt).Select(h => new OrderStatusChangeDto(h.Status, h.OccurredAt, h.Note)).ToList(),
        customerCanCancel && o.Status == OrderStatus.Pending);
}
