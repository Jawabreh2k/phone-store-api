using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Store.Application.Abstractions;

namespace Store.Infrastructure.Notifications;

/// <summary>
/// Queues work onto a single reader. Each item runs in its own scope with the host shutdown token,
/// so a finished HTTP request cannot cancel the send or dispose the database context it needs.
/// </summary>
internal sealed partial class DeferredWorkQueue(IServiceScopeFactory scopes, ILogger<DeferredWorkQueue> logger) : BackgroundService, IDeferredWork
{
    private readonly Channel<Func<IServiceProvider, CancellationToken, Task>> _channel =
        Channel.CreateUnbounded<Func<IServiceProvider, CancellationToken, Task>>(new UnboundedChannelOptions { SingleReader = true });

    public void Run(Func<IServiceProvider, CancellationToken, Task> work) => _channel.Writer.TryWrite(work);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var work in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await work(scope.ServiceProvider, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                LogWorkFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Deferred work failed.")]
    private static partial void LogWorkFailed(ILogger logger, Exception exception);
}
