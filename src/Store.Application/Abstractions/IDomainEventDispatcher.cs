using Store.Domain.Common;

namespace Store.Application.Abstractions;

/// <summary>
/// Reacts to a domain event inside the unit of work that raised it, by enqueueing outbox messages (never by calling
/// anything outside the database). Persistence calls it for each pending event just before saving.
/// </summary>
public interface IDomainEventDispatcher
{
    void Dispatch(IHasDomainEvents source, IDomainEvent domainEvent);
}
