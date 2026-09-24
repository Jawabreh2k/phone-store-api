using Store.Application.Notifications;

namespace Store.Application.Outbox;

/// <summary>
/// One claimed message, as its deliverer sees it. <see cref="Secret"/> is already decrypted, and is null when the
/// message never had one, or when it was cleared (a parked message that staff retried).
/// </summary>
public sealed record OutboxDelivery(Guid MessageId, string Type, string Payload, string? Secret, NotificationChannel? Channel, Guid? OrderId, int Attempt);

/// <summary>
/// Delivers one <see cref="OutboxTypes">type</see> of outbox message. Runs in a fresh DI scope per message, possibly more
/// than once for the same message (delivery is at least once), so it must be safe to repeat.
/// Report failures as a <see cref="DeliveryResult"/>; an exception counts as a temporary failure.
/// </summary>
public interface IOutboxMessageDeliverer
{
    string Type { get; }

    Task<DeliveryResult> HandleAsync(OutboxDelivery delivery, CancellationToken cancellationToken);
}
