using Store.Domain.Common;

namespace Store.Domain.Orders;

/// <summary>
/// A placed order. Everything needed to fulfil it is copied in at checkout (names, prices, address),
/// so editing or deleting a product later never changes what the customer ordered.
///
/// Status flow:  Pending → Confirmed → Shipped → Delivered
///               Pending / Confirmed → Cancelled
///               Delivered → Returned
/// </summary>
public sealed class Order : AuditableEntity
{
    public const int NoteMaxLength = 1000;

    private static readonly Dictionary<OrderStatus, OrderStatus[]> AllowedTransitions = new()
    {
        [OrderStatus.Pending] = [OrderStatus.Confirmed, OrderStatus.Cancelled],
        [OrderStatus.Confirmed] = [OrderStatus.Shipped, OrderStatus.Cancelled],
        [OrderStatus.Shipped] = [OrderStatus.Delivered],
        [OrderStatus.Delivered] = [OrderStatus.Returned],
        [OrderStatus.Cancelled] = [],
        [OrderStatus.Returned] = [],
    };

    private readonly List<OrderItem> _items = [];
    private readonly List<OrderStatusChange> _history = [];

    private Order() { } // EF

    private Order(string orderNumber, string trackingTokenHash, string currencyCode, Guid? customerId, ContactInfo contact, ShippingAddress address, string? customerNote, decimal shippingFee, DateTimeOffset placedAt)
    {
        OrderNumber = orderNumber;
        TrackingTokenHash = trackingTokenHash;
        CurrencyCode = currencyCode;
        CustomerId = customerId;
        Contact = contact;
        ShippingAddress = address;
        CustomerNote = string.IsNullOrWhiteSpace(customerNote) ? null : customerNote.Trim();
        ShippingFee = shippingFee;
        PaymentMethod = PaymentMethod.CashOnDelivery;
        PaymentStatus = PaymentStatus.Unpaid;
        Status = OrderStatus.Pending;
        PlacedAt = placedAt;
    }

    /// <summary>Short human-friendly number customers quote on the phone. Sequential, so never a credential.</summary>
    public string OrderNumber { get; private set; } = null!;

    /// <summary>SHA-256 of the guest tracking token (see <see cref="OrderTrackingToken"/>).</summary>
    public string TrackingTokenHash { get; private set; } = null!;

    /// <summary>
    /// ISO 4217 code the amounts are in, fixed at checkout like the prices themselves,
    /// so changing the store currency later never relabels past totals.
    /// </summary>
    public string CurrencyCode { get; private set; } = null!;

    /// <summary>Null for guest orders.</summary>
    public Guid? CustomerId { get; private set; }

    public ContactInfo Contact { get; private set; } = null!;
    public ShippingAddress ShippingAddress { get; private set; } = null!;
    public string? CustomerNote { get; private set; }
    public OrderStatus Status { get; private set; }
    public PaymentMethod PaymentMethod { get; private set; }
    public PaymentStatus PaymentStatus { get; private set; }
    public decimal Subtotal { get; private set; }
    public decimal ShippingFee { get; private set; }
    public decimal Total { get; private set; }
    public DateTimeOffset PlacedAt { get; private set; }
    public string? CancellationReason { get; private set; }

    public IReadOnlyList<OrderItem> Items => _items;
    public IReadOnlyList<OrderStatusChange> History => _history;

    public static Result<Order> Place(
        string orderNumber,
        string trackingTokenHash,
        string currencyCode,
        Guid? customerId,
        ContactInfo contact,
        ShippingAddress address,
        IReadOnlyCollection<OrderLineInput> lines,
        decimal shippingFee,
        string? customerNote,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(trackingTokenHash);
        ArgumentNullException.ThrowIfNull(contact);
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(lines);

        if (currencyCode is not { Length: 3 } || !currencyCode.All(char.IsAsciiLetterUpper))
        {
            return OrderErrors.InvalidCurrency;
        }

        if (lines.Count == 0)
        {
            return OrderErrors.NoItems;
        }

        if (lines.Any(l => l.Quantity <= 0 || l.UnitPrice <= 0) || shippingFee < 0)
        {
            return OrderErrors.InvalidLine;
        }

        var order = new Order(orderNumber, trackingTokenHash, currencyCode, customerId, contact, address, customerNote, shippingFee, now);
        order._items.AddRange(lines.Select(l => new OrderItem(order.Id, l)));
        order.Subtotal = order._items.Sum(i => i.LineTotal);
        order.Total = order.Subtotal + shippingFee;
        order._history.Add(new OrderStatusChange(order.Id, OrderStatus.Pending, now, customerId, note: null));
        return order;
    }

    public Result Confirm(Guid byUserId, DateTimeOffset now, string? note = null) =>
        TransitionTo(OrderStatus.Confirmed, byUserId, now, note);

    public Result Ship(Guid byUserId, DateTimeOffset now, string? note = null) =>
        TransitionTo(OrderStatus.Shipped, byUserId, now, note);

    /// <summary>Cash on delivery is collected at the door, so delivery also settles payment.</summary>
    public Result Deliver(Guid byUserId, DateTimeOffset now, string? note = null)
    {
        var result = TransitionTo(OrderStatus.Delivered, byUserId, now, note);
        if (result.IsSuccess && PaymentMethod == PaymentMethod.CashOnDelivery)
        {
            PaymentStatus = PaymentStatus.Paid;
        }

        return result;
    }

    /// <summary>
    /// Marks a delivered order as returned. Cash was already collected, so payment stays paid.
    /// The caller must release the stock back to the shelf.
    /// </summary>
    public Result Return(Guid byUserId, DateTimeOffset now, string? note = null)
    {
        if (Status != OrderStatus.Delivered)
        {
            return OrderErrors.CannotReturn;
        }

        return TransitionTo(OrderStatus.Returned, byUserId, now, note);
    }

    /// <summary>
    /// Cancels the order. Customers may only cancel while it is still pending; staff may also cancel
    /// a confirmed order. The caller must release the reserved stock (it belongs to the Product aggregate).
    /// </summary>
    public Result Cancel(Guid byUserId, bool byCustomer, string? reason, DateTimeOffset now)
    {
        if (byCustomer && Status != OrderStatus.Pending)
        {
            return OrderErrors.CustomerCanOnlyCancelPending;
        }

        if (!CanTransitionTo(OrderStatus.Cancelled))
        {
            return OrderErrors.CannotCancelAfterShipping;
        }

        CancellationReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        return TransitionTo(OrderStatus.Cancelled, byUserId, now, CancellationReason);
    }

    public bool CanTransitionTo(OrderStatus next) => AllowedTransitions[Status].Contains(next);

    public IReadOnlyList<OrderStatus> NextStatuses => AllowedTransitions[Status];

    private Result TransitionTo(OrderStatus next, Guid byUserId, DateTimeOffset now, string? note)
    {
        if (!CanTransitionTo(next))
        {
            return OrderErrors.InvalidTransition(Status, next);
        }

        Status = next;
        _history.Add(new OrderStatusChange(Id, next, now, byUserId, note));
        return Result.Success();
    }
}

public sealed class OrderItem : Entity
{
    private OrderItem() { } // EF

    internal OrderItem(Guid orderId, OrderLineInput line)
    {
        OrderId = orderId;
        ProductId = line.ProductId;
        VariantId = line.VariantId;
        Sku = line.Sku;
        ProductName = line.ProductName;
        VariantDescription = line.VariantDescription;
        ImageUrl = line.ImageUrl;
        UnitPrice = line.UnitPrice;
        Quantity = line.Quantity;
        LineTotal = line.UnitPrice * line.Quantity;
    }

    public Guid OrderId { get; private set; }

    // Kept for reporting and stock release; not foreign keys, so deleting a product never breaks an order.
    public Guid ProductId { get; private set; }
    public Guid VariantId { get; private set; }

    public string Sku { get; private set; } = null!;
    public LocalizedText ProductName { get; private set; } = null!;
    public LocalizedText VariantDescription { get; private set; } = null!;
    public string? ImageUrl { get; private set; }
    public decimal UnitPrice { get; private set; }
    public int Quantity { get; private set; }
    public decimal LineTotal { get; private set; }
}

/// <summary>Audit trail entry: who moved the order to which status, and when.</summary>
public sealed class OrderStatusChange : Entity
{
    private OrderStatusChange() { } // EF

    internal OrderStatusChange(Guid orderId, OrderStatus status, DateTimeOffset occurredAt, Guid? changedBy, string? note)
    {
        OrderId = orderId;
        Status = status;
        OccurredAt = occurredAt;
        ChangedBy = changedBy;
        Note = note;
    }

    public Guid OrderId { get; private set; }
    public OrderStatus Status { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public Guid? ChangedBy { get; private set; }
    public string? Note { get; private set; }
}
