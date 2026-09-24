using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Store.Application.Abstractions;
using Store.Domain.Common;

namespace Store.Infrastructure.Persistence;

/// <summary>
/// The one place domain events become outbox messages: just before a save, every pending event on a tracked aggregate
/// goes to <see cref="IDomainEventDispatcher"/>, whose outbox rows join the same save (and so the same transaction).
/// Events are cleared as they are dispatched, so a retried save does not enqueue them twice.
/// </summary>
internal sealed class DomainEventsInterceptor(IServiceProvider services) : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Dispatch(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Dispatch(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    private void Dispatch(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var sources = context.ChangeTracker.Entries<IHasDomainEvents>()
            .Select(e => e.Entity)
            .Where(e => e.DomainEvents.Count > 0)
            .ToList();
        if (sources.Count == 0)
        {
            return;
        }

        // Resolved per save rather than injected: the dispatcher writes through the DbContext this interceptor is built into.
        var dispatcher = services.GetRequiredService<IDomainEventDispatcher>();
        foreach (var source in sources)
        {
            var events = source.DomainEvents.ToList();
            source.ClearDomainEvents();
            foreach (var domainEvent in events)
            {
                dispatcher.Dispatch(source, domainEvent);
            }
        }
    }
}
