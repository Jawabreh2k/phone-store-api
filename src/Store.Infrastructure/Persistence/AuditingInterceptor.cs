using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Store.Domain.Common;

namespace Store.Infrastructure.Persistence;

/// <summary>Stamps CreatedAt / UpdatedAt on auditable entities from the injected clock.</summary>
internal sealed class AuditingInterceptor(TimeProvider clock) : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = clock.GetUtcNow();
        foreach (var entry in context.ChangeTracker.Entries<IAuditable>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Property(nameof(IAuditable.CreatedAt)).CurrentValue = now;
            }
            else if (entry.State == EntityState.Modified || HasChangedChildren(entry))
            {
                entry.Property(nameof(IAuditable.UpdatedAt)).CurrentValue = now;
            }
        }
    }

    // A cart whose items changed is itself "updated", even if none of its own columns did.
    private static bool HasChangedChildren(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry) =>
        entry.Collections.Any(c => c.CurrentValue is not null && c.CurrentValue.Cast<object>()
            .Any(child => entry.Context.Entry(child).State is EntityState.Added or EntityState.Modified or EntityState.Deleted));
}
