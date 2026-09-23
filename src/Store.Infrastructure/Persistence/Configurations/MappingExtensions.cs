using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Store.Domain.Common;

namespace Store.Infrastructure.Persistence.Configurations;

internal static class MappingExtensions
{
    /// <summary>Maps a <see cref="LocalizedText"/> to two columns: {prefix}En and {prefix}Ar.</summary>
    public static void HasLocalizedText<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        Expression<Func<TEntity, LocalizedText?>> property,
        string prefix,
        int maxLength)
        where TEntity : class =>
        builder.ComplexProperty(property, text =>
        {
            text.IsRequired();
            text.Property(t => t.En).HasColumnName($"{prefix}En").HasMaxLength(maxLength).IsRequired();
            text.Property(t => t.Ar).HasColumnName($"{prefix}Ar").HasMaxLength(maxLength).IsRequired();
        });
}
