using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Store.Application.Outbox;

namespace Store.Infrastructure.Persistence.Configurations;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.Property(m => m.Type).HasMaxLength(OutboxMessage.TypeMaxLength).IsUnicode(false);
        builder.Property(m => m.DeduplicationKey).HasMaxLength(OutboxMessage.DeduplicationKeyMaxLength).IsUnicode(false);
        builder.Property(m => m.LastError).HasMaxLength(OutboxMessage.LastErrorMaxLength);
        builder.Property(m => m.LockedBy).HasMaxLength(OutboxMessage.LockedByMaxLength);
        builder.Property(m => m.CorrelationId).HasMaxLength(OutboxMessage.CorrelationIdMaxLength).IsUnicode(false);
        builder.Property(m => m.ProtectedSecret).IsUnicode(false);
        builder.Property(m => m.Status).IsUnicode(false);
        builder.Property(m => m.Channel).IsUnicode(false);
        builder.Property(m => m.Template).IsUnicode(false);

        // A second enqueue of the same logical message is dropped (see AppDbContext.SaveChangesAsync).
        builder.HasIndex(m => m.DeduplicationKey).IsUnique();

        // The worker's scan: due rows by status, oldest first.
        builder.HasIndex(m => new { m.Status, m.NextAttemptAt });

        // The admin order screen.
        builder.HasIndex(m => m.OrderId);
    }
}
