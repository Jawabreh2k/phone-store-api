namespace Store.Domain.Common;

/// <summary>
/// Base type for every persisted entity. Ids are UUIDv7 so they are time-ordered,
/// which keeps clustered indexes append-only instead of fragmenting like random GUIDs.
/// </summary>
public abstract class Entity
{
    protected Entity() => Id = Guid.CreateVersion7();

    public Guid Id { get; protected init; }
}

/// <summary>Entity whose creation and last-modification times are stamped by the persistence layer.</summary>
public interface IAuditable
{
    DateTimeOffset CreatedAt { get; }
    DateTimeOffset? UpdatedAt { get; }
}

/// <summary>
/// Convenience base for auditable entities. Timestamps are set by an EF interceptor
/// (see Infrastructure), never by domain code, so they cannot drift from the clock abstraction.
/// </summary>
public abstract class AuditableEntity : Entity, IAuditable
{
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
}
