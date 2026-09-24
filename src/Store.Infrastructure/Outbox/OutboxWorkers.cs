using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Store.Application.Outbox;
using Store.Infrastructure.Persistence;

namespace Store.Infrastructure.Outbox;

/// <summary>Polls the outbox and delivers due messages until the host stops.</summary>
internal sealed partial class OutboxWorker(
    OutboxProcessor processor,
    IOptions<OutboxOptions> options,
    TimeProvider clock,
    ILogger<OutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger, processor.WorkerId);
        using var timer = new PeriodicTimer(options.Value.PollInterval, clock);
        do
        {
            try
            {
                // A full batch means more may be waiting: keep going without waiting for the next tick.
                int claimed;
                do
                {
                    claimed = await processor.ProcessBatchAsync(stoppingToken);
                }
                while (claimed >= options.Value.BatchSize);

                await processor.RefreshBacklogAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The database may be briefly unreachable; the next tick tries again.
                LogPollFailed(logger, ex);
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox worker {WorkerId} started.")]
    private static partial void LogStarted(ILogger logger, string workerId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox poll failed; retrying on the next tick.")]
    private static partial void LogPollFailed(ILogger logger, Exception exception);
}

/// <summary>Deletes sent messages past the retention period. Parked messages are never deleted here.</summary>
internal sealed partial class OutboxCleanupWorker(
    IServiceScopeFactory scopes,
    IOptions<OutboxOptions> options,
    TimeProvider clock,
    ILogger<OutboxCleanupWorker> logger) : BackgroundService
{
    public async Task<int> CleanupAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cutoff = clock.GetUtcNow().AddDays(-options.Value.RetentionDays);
        var deleted = await db.OutboxMessages
            .Where(m => m.Status == OutboxStatus.Sent && m.ProcessedAt != null && m.ProcessedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted > 0)
        {
            LogDeleted(logger, deleted, options.Value.RetentionDays);
        }

        return deleted;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.CleanupInterval, clock);
        try
        {
            do
            {
                try
                {
                    await CleanupAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogCleanupFailed(logger, ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} sent outbox messages older than {RetentionDays} days.")]
    private static partial void LogDeleted(ILogger logger, int count, int retentionDays);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox cleanup failed; retrying on the next run.")]
    private static partial void LogCleanupFailed(ILogger logger, Exception exception);
}
