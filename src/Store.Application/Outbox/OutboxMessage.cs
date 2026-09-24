using Store.Application.Notifications;
using Store.Domain.Common;

namespace Store.Application.Outbox;

public enum OutboxStatus
{
    Pending = 1,
    Processing = 2,
    Sent = 3,
    Parked = 4,
}

/// <summary>
/// A side effect outside the database (an email, a WhatsApp message), written in the same transaction as the
/// business change that caused it and delivered afterwards by the outbox worker, at least once.
/// <para>
/// Delivery state (claiming, retries, parking) is changed by the worker with set-based conditional updates, so two
/// workers can never both own a row; see <c>OutboxProcessor</c>. Only the staff "retry" goes through this entity.
/// </para>
/// </summary>
public sealed class OutboxMessage : Entity
{
    public const int TypeMaxLength = 100;
    public const int DeduplicationKeyMaxLength = 200;
    public const int LastErrorMaxLength = 1000;
    public const int LockedByMaxLength = 100;
    public const int CorrelationIdMaxLength = 64;
    public const int TemplateMaxLength = 40;

    private OutboxMessage() { } // EF

    internal OutboxMessage(
        string type,
        string payload,
        string deduplicationKey,
        DateTimeOffset occurredAt,
        Guid? orderId,
        NotificationChannel? channel,
        NotificationTemplate? template,
        string? protectedSecret,
        string? correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(deduplicationKey);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(deduplicationKey.Length, DeduplicationKeyMaxLength);

        Type = type;
        Payload = payload;
        DeduplicationKey = deduplicationKey;
        OccurredAt = occurredAt;
        NextAttemptAt = occurredAt;
        OrderId = orderId;
        Channel = channel;
        Template = template;
        ProtectedSecret = protectedSecret;
        CorrelationId = correlationId is { Length: > CorrelationIdMaxLength } ? correlationId[..CorrelationIdMaxLength] : correlationId;
        Status = OutboxStatus.Pending;
    }

    /// <summary>Which <c>IOutboxMessageDeliverer</c> delivers it (see <see cref="OutboxTypes"/>).</summary>
    public string Type { get; private set; } = null!;

    /// <summary>JSON the handler needs. Never holds secrets or contact details beyond what the handler cannot look up.</summary>
    public string Payload { get; private set; } = null!;

    /// <summary>Unique: enqueueing the same key again is a no-op, which makes producers idempotent.</summary>
    public string DeduplicationKey { get; private set; } = null!;

    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public DateTimeOffset? LastAttemptAt { get; private set; }

    /// <summary>Delivery attempts started, counted when a worker claims the row (so a crash mid-send still counts).</summary>
    public int Attempts { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }
    public string? LockedBy { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }

    /// <summary>Why the last attempt failed, truncated. Never contains the payload, a recipient or a token.</summary>
    public string? LastError { get; private set; }

    public OutboxStatus Status { get; private set; }

    /// <summary>The order the message is about, for the admin order screen.</summary>
    public Guid? OrderId { get; private set; }

    public NotificationChannel? Channel { get; private set; }
    public NotificationTemplate? Template { get; private set; }

    /// <summary>
    /// A credential the message must carry (a guest tracking token, a password reset token), encrypted with
    /// ASP.NET Data Protection. Cleared as soon as the message is Sent or Parked; never returned by any API.
    /// </summary>
    public string? ProtectedSecret { get; private set; }

    /// <summary>Trace id of the request that enqueued it, so logs of the request and of the delivery line up.</summary>
    public string? CorrelationId { get; private set; }

    /// <summary>Staff retry of a parked message: it becomes due now and gets a fresh set of attempts.</summary>
    public Result Requeue(DateTimeOffset now)
    {
        if (Status != OutboxStatus.Parked)
        {
            return OutboxErrors.NotParked;
        }

        Status = OutboxStatus.Pending;
        NextAttemptAt = now;
        Attempts = 0;
        LockedUntil = null;
        LockedBy = null;
        return Result.Success();
    }

    public static string TruncateError(string reason) =>
        reason.Length <= LastErrorMaxLength ? reason : reason[..LastErrorMaxLength];
}

/// <summary>Message types, one per <c>IOutboxMessageDeliverer</c>. Stored in the database: never rename one.</summary>
public static class OutboxTypes
{
    public const string CustomerOrderNotification = "orders.customer_notification";
    public const string StaffNewOrderAlert = "orders.staff_new_order";
    public const string PasswordResetEmail = "account.password_reset";
}

public static class OutboxErrors
{
    public static readonly Error NotFound = Error.NotFound("outbox.not_found", "The message was not found.");
    public static readonly Error NotParked = Error.Conflict("outbox.not_parked", "Only a message that has stopped retrying can be retried.");
}
