namespace Store.Domain.Common;

/// <summary>Something that happened inside an aggregate that other parts of the system react to after it is saved.</summary>
public interface IDomainEvent;

/// <summary>
/// An aggregate that records domain events. Persistence turns them into outbox messages inside the same
/// <c>SaveChanges</c> as the change that raised them, then clears them.
/// </summary>
public interface IHasDomainEvents
{
    IReadOnlyList<IDomainEvent> DomainEvents { get; }

    void ClearDomainEvents();
}
