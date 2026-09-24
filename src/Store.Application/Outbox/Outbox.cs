using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Store.Application.Abstractions;
using Store.Application.Notifications;

namespace Store.Application.Outbox;

/// <summary>
/// What to deliver. <paramref name="Payload"/> is serialized to JSON; <paramref name="Secret"/> (a token the message must carry)
/// is encrypted into its own column instead, so the payload never holds a credential.
/// </summary>
public sealed record OutboxEntry(
    string Type,
    object Payload,
    string DeduplicationKey,
    Guid? OrderId = null,
    NotificationChannel? Channel = null,
    NotificationTemplate? Template = null,
    string? Secret = null);

/// <summary>
/// Records side effects in the current unit of work. <see cref="Enqueue"/> only adds to the DbContext: the message is
/// committed by the caller's own <c>SaveChangesAsync</c>, together with the business change, or not at all.
/// </summary>
public interface IOutbox
{
    void Enqueue(OutboxEntry entry);
}

internal sealed partial class Outbox(IAppDbContext db, ISecretProtector secrets, TimeProvider clock, ILogger<Outbox> logger) : IOutbox
{
    internal static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    public void Enqueue(OutboxEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // A key already in the database is dropped at save time (AppDbContext); one already in this unit of work, here.
        if (db.OutboxMessages.Local.Any(m => m.DeduplicationKey == entry.DeduplicationKey))
        {
            LogDuplicateSkipped(logger, entry.Type, entry.OrderId);
            return;
        }

        var message = new OutboxMessage(
            entry.Type,
            JsonSerializer.Serialize(entry.Payload, entry.Payload.GetType(), PayloadJson),
            entry.DeduplicationKey,
            clock.GetUtcNow(),
            entry.OrderId,
            entry.Channel,
            entry.Template,
            entry.Secret is null ? null : secrets.Protect(entry.Secret),
            Activity.Current?.TraceId.ToString());

        db.OutboxMessages.Add(message);
        LogEnqueued(logger, message.Id, message.Type, message.OrderId, message.CorrelationId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox message {MessageId} ({Type}) enqueued for order {OrderId}, correlation {CorrelationId}.")]
    private static partial void LogEnqueued(ILogger logger, Guid messageId, string type, Guid? orderId, string? correlationId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Duplicate outbox message ({Type}) for order {OrderId} skipped.")]
    private static partial void LogDuplicateSkipped(ILogger logger, string type, Guid? orderId);
}
