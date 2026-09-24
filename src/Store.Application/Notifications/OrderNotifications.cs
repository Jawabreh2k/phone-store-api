using Microsoft.Extensions.Options;
using Store.Application.Abstractions;
using Store.Application.Outbox;
using Store.Domain.Common;
using Store.Domain.Orders;

namespace Store.Application.Notifications;

internal sealed record CustomerOrderPayload(Guid OrderId, NotificationTemplate Template, Guid? HistoryEntryId, bool ByCustomer);

internal sealed record StaffNewOrderPayload(Guid OrderId);

/// <summary>
/// Decides which messages an order event produces and enqueues them: one outbox row per channel, each with its own
/// deduplication key, so a failing channel never holds back another and a repeated event never sends twice.
/// </summary>
internal sealed class OrderNotifications(IOutbox outbox, IOptions<NotificationOptions> options)
{
    /// <summary>
    /// Called by <c>PlaceOrderHandler</c> before it saves: the raw tracking token exists only there, so the guest message
    /// takes it now (encrypted into the outbox row) instead of from a domain event.
    /// </summary>
    public void Placed(Order order, string trackingToken)
    {
        const NotificationTemplate template = NotificationTemplate.OrderPlaced;
        foreach (var channel in CustomerChannels(order, template))
        {
            outbox.Enqueue(new OutboxEntry(
                OutboxTypes.CustomerOrderNotification,
                new CustomerOrderPayload(order.Id, template, HistoryEntryId: null, ByCustomer: false),
                $"order:{order.Id}:placed:customer:{Key(channel)}",
                order.Id,
                channel,
                template,
                Secret: order.CustomerId is null ? trackingToken : null));
        }

        outbox.Enqueue(new OutboxEntry(
            OutboxTypes.StaffNewOrderAlert,
            new StaffNewOrderPayload(order.Id),
            $"order:{order.Id}:placed:staff",
            order.Id,
            NotificationChannel.Email,
            NotificationTemplate.StaffNewOrder));
    }

    public void StatusChanged(Order order, OrderStatusChanged change)
    {
        NotificationTemplate? template = change.Status switch
        {
            OrderStatus.Confirmed => NotificationTemplate.OrderConfirmed,
            OrderStatus.Shipped => NotificationTemplate.OrderShipped,
            OrderStatus.Delivered => NotificationTemplate.OrderDelivered,
            OrderStatus.Cancelled => NotificationTemplate.OrderCancelled,
            OrderStatus.Returned => NotificationTemplate.OrderReturned,
            _ => null,
        };
        if (template is not { } chosen)
        {
            return;
        }

        foreach (var channel in CustomerChannels(order, chosen))
        {
            outbox.Enqueue(new OutboxEntry(
                OutboxTypes.CustomerOrderNotification,
                new CustomerOrderPayload(order.Id, chosen, change.HistoryEntryId, change.ByCustomer),
                $"order:{order.Id}:status:{change.Status}:{change.HistoryEntryId}:customer:{Key(channel)}",
                order.Id,
                channel,
                chosen));
        }
    }

    private IEnumerable<NotificationChannel> CustomerChannels(Order order, NotificationTemplate template)
    {
        // Order messages are transactional; a marketing template would need the customer's recorded consent here.
        if (!NotificationPolicy.MaySend(NotificationPolicy.CategoryOf(template), hasMarketingConsent: false))
        {
            return [];
        }

        return options.Value.CustomerChannels
            .Distinct()
            .Where(c => c != NotificationChannel.Email || order.Contact.Email is not null);
    }

    private static string Key(NotificationChannel channel) => channel.ToString().ToLowerInvariant();
}

/// <summary>Turns domain events into outbox messages; called for every event while the unit of work saves.</summary>
internal sealed class DomainEventDispatcher(OrderNotifications orders) : IDomainEventDispatcher
{
    public void Dispatch(IHasDomainEvents source, IDomainEvent domainEvent)
    {
        switch (domainEvent)
        {
            case OrderStatusChanged changed when source is Order order:
                orders.StatusChanged(order, changed);
                break;
        }
    }
}
