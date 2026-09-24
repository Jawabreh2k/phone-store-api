using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Store.Application.Abstractions;
using Store.Application.Notifications;
using Store.Application.Outbox;
using Store.Infrastructure.Persistence;

namespace Store.Infrastructure.Outbox;

/// <summary>
/// Claims due outbox messages and delivers them, safely alongside any number of other workers on SQL Server or SQLite.
/// <para>
/// A claim is one atomic conditional UPDATE: it takes a lease (<c>LockedUntil</c>, <c>LockedBy</c>) only while the row is
/// still due and not leased to anyone else, and only the worker whose update touched the row delivers it. Completion is
/// conditional on still holding the lease, so a worker whose lease ran out cannot overwrite the next owner's result.
/// A worker that dies mid-send simply lets its lease expire; the message is then claimed again (at least once delivery).
/// </para>
/// </summary>
internal sealed partial class OutboxProcessor(
    IServiceScopeFactory scopes,
    IOptions<OutboxOptions> options,
    ISecretProtector secrets,
    OutboxMetrics metrics,
    TimeProvider clock,
    ILogger<OutboxProcessor> logger)
{
    /// <summary>Identifies this worker in <c>LockedBy</c>: machine, process and a random part, so two workers never share it.</summary>
    public string WorkerId { get; } = CreateWorkerId();

    /// <summary>Claims and delivers up to one batch of due messages. Returns how many this worker claimed.</summary>
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        List<Guid> candidates;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var now = clock.GetUtcNow();
            candidates = await scope.ServiceProvider.GetRequiredService<AppDbContext>().OutboxMessages.AsNoTracking()
                .Where(m => (m.Status == OutboxStatus.Pending || m.Status == OutboxStatus.Processing)
                    && m.NextAttemptAt <= now
                    && (m.LockedUntil == null || m.LockedUntil < now))
                .OrderBy(m => m.NextAttemptAt)
                .Select(m => m.Id)
                .Take(settings.BatchSize)
                .ToListAsync(cancellationToken);
        }

        var claimed = 0;
        foreach (var id in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryProcessAsync(id, cancellationToken))
            {
                claimed++;
            }
        }

        return claimed;
    }

    /// <summary>Publishes the backlog gauges (undelivered count, oldest age).</summary>
    public async Task RefreshBacklogAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var backlog = await OutboxBacklog.ReadAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), cancellationToken);
        metrics.Backlog(backlog.Undelivered, backlog.OldestUndeliveredAt is { } oldest ? clock.GetUtcNow() - oldest : TimeSpan.Zero);
    }

    private async Task<bool> TryProcessAsync(Guid id, CancellationToken cancellationToken)
    {
        // A fresh scope per message: its own DbContext, handler and channel instances.
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = clock.GetUtcNow();
        var leaseEnd = now + options.Value.LeaseDuration;

        var won = await db.OutboxMessages
            .Where(m => m.Id == id
                && (m.Status == OutboxStatus.Pending || m.Status == OutboxStatus.Processing)
                && m.NextAttemptAt <= now
                && (m.LockedUntil == null || m.LockedUntil < now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, OutboxStatus.Processing)
                .SetProperty(m => m.LockedUntil, leaseEnd)
                .SetProperty(m => m.LockedBy, WorkerId)
                .SetProperty(m => m.LastAttemptAt, now)
                .SetProperty(m => m.Attempts, m => m.Attempts + 1), cancellationToken);
        if (won != 1)
        {
            return false;
        }

        var message = await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == id, cancellationToken);
        LogClaimed(logger, message.Id, message.Type, message.Attempts, WorkerId, message.OrderId, message.CorrelationId);

        // Attempts count claims, so a message that keeps killing its worker mid-send still runs out of attempts.
        if (message.Attempts > options.Value.MaxAttempts)
        {
            await ParkAsync(db, message, "Gave up after the maximum number of attempts (the worker stopped during delivery).");
            return true;
        }

        var handler = scope.ServiceProvider.GetServices<IOutboxMessageDeliverer>().FirstOrDefault(h => h.Type == message.Type);
        if (handler is null)
        {
            await ParkAsync(db, message, $"No handler is registered for message type '{message.Type}'.");
            return true;
        }

        var delivery = new OutboxDelivery(
            message.Id,
            message.Type,
            message.Payload,
            message.ProtectedSecret is { } protectedSecret ? secrets.Unprotect(protectedSecret) : null,
            message.Channel,
            message.OrderId,
            message.Attempts);

        DeliveryResult result;
        try
        {
            result = await handler.HandleAsync(delivery, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down: hand the message back now instead of making the next worker wait out the lease.
            await ReleaseAsync(db, message);
            throw;
        }
        catch (Exception ex)
        {
            // Channels report expected failures as results; an exception is a bug or an outage worth retrying.
            LogHandlerThrew(logger, message.Id, message.Type, message.OrderId, message.CorrelationId, ex);
            result = DeliveryResult.Temporary($"Unexpected {ex.GetType().Name} while delivering.");
        }

        switch (result.Outcome)
        {
            case DeliveryOutcome.Sent:
                await MarkSentAsync(db, message);
                break;
            case DeliveryOutcome.PermanentFailure:
                metrics.Failed(message.Type, message.Channel?.ToString(), permanent: true);
                await ParkAsync(db, message, result.Reason ?? "Permanent failure.");
                break;
            default:
                metrics.Failed(message.Type, message.Channel?.ToString(), permanent: false);
                await RetryOrParkAsync(db, message, result.Reason ?? "Temporary failure.");
                break;
        }

        return true;
    }

    // Completion writes ignore the shutdown token: a message that was sent must be recorded as sent, or it goes out twice.
    private async Task MarkSentAsync(AppDbContext db, OutboxMessage message)
    {
        var now = clock.GetUtcNow();
        var updated = await Owned(db, message.Id).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.Status, OutboxStatus.Sent)
            .SetProperty(m => m.ProcessedAt, now)
            .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null)
            .SetProperty(m => m.LockedBy, (string?)null)
            .SetProperty(m => m.ProtectedSecret, (string?)null)
            .SetProperty(m => m.LastError, (string?)null), CancellationToken.None);

        if (updated == 1)
        {
            metrics.Sent(message.Type, message.Channel?.ToString());
            LogSent(logger, message.Id, message.Type, message.Attempts, message.OrderId, message.CorrelationId);
        }
        else
        {
            LogLeaseLost(logger, message.Id, message.Type, WorkerId);
        }
    }

    private async Task RetryOrParkAsync(AppDbContext db, OutboxMessage message, string reason)
    {
        if (message.Attempts >= options.Value.MaxAttempts)
        {
            await ParkAsync(db, message, reason);
            return;
        }

        var nextAttemptAt = clock.GetUtcNow() + BackoffFor(message.Attempts);
        var error = OutboxMessage.TruncateError(reason);
        var updated = await Owned(db, message.Id).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.Status, OutboxStatus.Pending)
            .SetProperty(m => m.NextAttemptAt, nextAttemptAt)
            .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null)
            .SetProperty(m => m.LockedBy, (string?)null)
            .SetProperty(m => m.LastError, error), CancellationToken.None);

        if (updated == 1)
        {
            LogRetryScheduled(logger, message.Id, message.Type, message.Attempts, error, nextAttemptAt, message.OrderId, message.CorrelationId);
        }
        else
        {
            LogLeaseLost(logger, message.Id, message.Type, WorkerId);
        }
    }

    private async Task ParkAsync(AppDbContext db, OutboxMessage message, string reason)
    {
        var error = OutboxMessage.TruncateError(reason);
        var updated = await Owned(db, message.Id).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.Status, OutboxStatus.Parked)
            .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null)
            .SetProperty(m => m.LockedBy, (string?)null)
            .SetProperty(m => m.ProtectedSecret, (string?)null)
            .SetProperty(m => m.LastError, error), CancellationToken.None);

        if (updated == 1)
        {
            metrics.Parked(message.Type, message.Channel?.ToString());
            LogParked(logger, message.Id, message.Type, message.Attempts, error, message.OrderId, message.CorrelationId);
        }
        else
        {
            LogLeaseLost(logger, message.Id, message.Type, WorkerId);
        }
    }

    private Task<int> ReleaseAsync(AppDbContext db, OutboxMessage message) =>
        Owned(db, message.Id).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.Status, OutboxStatus.Pending)
            .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null)
            .SetProperty(m => m.LockedBy, (string?)null), CancellationToken.None);

    private IQueryable<OutboxMessage> Owned(AppDbContext db, Guid id) =>
        db.OutboxMessages.Where(m => m.Id == id && m.LockedBy == WorkerId && m.Status == OutboxStatus.Processing);

    /// <summary>The configured delay for this attempt (the last one repeats), spread by ±20% so retries don't arrive together.</summary>
    internal TimeSpan BackoffFor(int attempt)
    {
        var schedule = options.Value.EffectiveBackoff;
        var delay = schedule[Math.Clamp(attempt - 1, 0, schedule.Count - 1)];
#pragma warning disable CA5394 // Jitter, not security.
        var jitter = 0.8 + (Random.Shared.NextDouble() * 0.4);
#pragma warning restore CA5394
        return TimeSpan.FromTicks((long)(delay.Ticks * jitter));
    }

    private static string CreateWorkerId()
    {
        var id = $"{Environment.MachineName}/{Environment.ProcessId}/{Guid.NewGuid():N}";
        return id.Length <= OutboxMessage.LockedByMaxLength ? id : id[^OutboxMessage.LockedByMaxLength..];
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Outbox message {MessageId} ({Type}) claimed, attempt {Attempt}, by {WorkerId}; order {OrderId}, correlation {CorrelationId}.")]
    private static partial void LogClaimed(ILogger logger, Guid messageId, string type, int attempt, string workerId, Guid? orderId, string? correlationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox message {MessageId} ({Type}) sent on attempt {Attempt}; order {OrderId}, correlation {CorrelationId}.")]
    private static partial void LogSent(ILogger logger, Guid messageId, string type, int attempt, Guid? orderId, string? correlationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox message {MessageId} ({Type}) failed on attempt {Attempt}: {Reason} Next attempt at {NextAttemptAt:o}; order {OrderId}, correlation {CorrelationId}.")]
    private static partial void LogRetryScheduled(ILogger logger, Guid messageId, string type, int attempt, string reason, DateTimeOffset nextAttemptAt, Guid? orderId, string? correlationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} ({Type}) parked after attempt {Attempt}: {Reason} Order {OrderId}, correlation {CorrelationId}.")]
    private static partial void LogParked(ILogger logger, Guid messageId, string type, int attempt, string reason, Guid? orderId, string? correlationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} ({Type}): worker {WorkerId} no longer held the lease when it finished; another worker owns it now.")]
    private static partial void LogLeaseLost(ILogger logger, Guid messageId, string type, string workerId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox handler for message {MessageId} ({Type}) threw; order {OrderId}, correlation {CorrelationId}.")]
    private static partial void LogHandlerThrew(ILogger logger, Guid messageId, string type, Guid? orderId, string? correlationId, Exception exception);
}

internal sealed record OutboxBacklogSnapshot(int Undelivered, DateTimeOffset? OldestUndeliveredAt, int Parked);

internal static class OutboxBacklog
{
    public static async Task<OutboxBacklogSnapshot> ReadAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var undelivered = db.OutboxMessages.AsNoTracking()
            .Where(m => m.Status == OutboxStatus.Pending || m.Status == OutboxStatus.Processing);

        var count = await undelivered.CountAsync(cancellationToken);
        var oldest = await undelivered.OrderBy(m => m.OccurredAt).Select(m => (DateTimeOffset?)m.OccurredAt).FirstOrDefaultAsync(cancellationToken);
        var parked = await db.OutboxMessages.CountAsync(m => m.Status == OutboxStatus.Parked, cancellationToken);
        return new OutboxBacklogSnapshot(count, oldest, parked);
    }
}
