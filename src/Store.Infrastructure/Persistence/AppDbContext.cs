using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Store.Application.Abstractions;
using Store.Application.Notifications;
using Store.Application.Outbox;
using Store.Domain.Carts;
using Store.Domain.Catalog;
using Store.Domain.Common;
using Store.Domain.Customers;
using Store.Domain.Orders;
using Store.Domain.Settings;
using Store.Infrastructure.Identity;

namespace Store.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options), IAppDbContext, IDataProtectionKeyContext
{
    public const string OrderNumberSequence = "OrderNumbers";

    public DbSet<Brand> Brands => Set<Brand>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();
    public DbSet<Cart> Carts => Set<Cart>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<CustomerAddress> CustomerAddresses => Set<CustomerAddress>();
    public DbSet<StoreSettings> StoreSettings => Set<StoreSettings>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>The Data Protection key ring, shared by every API instance so each can read what another protected.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    /// <summary>
    /// Saves, and makes a duplicate outbox message a no-op: when the save fails and some new outbox rows carry a
    /// <see cref="OutboxMessage.DeduplicationKey"/> that is already stored, those rows are dropped and the save runs once
    /// more, so the business change still commits. Any other failure is rethrown untouched.
    /// </summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        catch (DbUpdateException)
        {
            if (!await DetachStoredDuplicatesAsync(cancellationToken))
            {
                throw;
            }
        }

        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private async Task<bool> DetachStoredDuplicatesAsync(CancellationToken cancellationToken)
    {
        var added = ChangeTracker.Entries<OutboxMessage>().Where(e => e.State == EntityState.Added).ToList();
        if (added.Count == 0)
        {
            return false;
        }

        var keys = added.Select(e => e.Entity.DeduplicationKey).ToList();
        var stored = await OutboxMessages.AsNoTracking()
            .Where(m => keys.Contains(m.DeduplicationKey))
            .Select(m => m.DeduplicationKey)
            .ToListAsync(cancellationToken);
        if (stored.Count == 0)
        {
            return false;
        }

        foreach (var entry in added.Where(e => stored.Contains(e.Entity.DeduplicationKey)))
        {
            entry.State = EntityState.Detached;
        }

        return true;
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Ids are assigned by the domain (UUIDv7) when an entity is constructed. Without this, EF treats a
        // new child with a non-empty key (e.g. a variant added to a loaded product) as existing and UPDATEs it.
        foreach (var entity in builder.Model.GetEntityTypes().Where(e => typeof(Entity).IsAssignableFrom(e.ClrType)))
        {
            builder.Entity(entity.ClrType).Property(nameof(Entity.Id)).ValueGeneratedNever();
        }

        // SQL Server-only features. Tests run on SQLite, which has neither sequences nor rowversion.
        if (Database.IsSqlServer())
        {
            builder.HasSequence<long>(OrderNumberSequence).StartsAt(100_001).IncrementsBy(1);
            builder.Entity<ProductVariant>().Property<byte[]>("RowVersion").IsRowVersion();
            builder.Entity<Order>().Property<byte[]>("RowVersion").IsRowVersion();
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Three decimals so currencies with fils (JOD, KWD, BHD) are exact.
        configurationBuilder.Properties<decimal>().HavePrecision(18, 3);

        // Enums as readable strings: the database stays understandable without the code.
        configurationBuilder.Properties<ProductType>().HaveConversion<string>().HaveMaxLength(20);
        configurationBuilder.Properties<OrderStatus>().HaveConversion<string>().HaveMaxLength(20);
        configurationBuilder.Properties<PaymentMethod>().HaveConversion<string>().HaveMaxLength(30);
        configurationBuilder.Properties<PaymentStatus>().HaveConversion<string>().HaveMaxLength(20);
        configurationBuilder.Properties<Domain.Common.Language>().HaveConversion<string>().HaveMaxLength(5);
        configurationBuilder.Properties<OutboxStatus>().HaveConversion<string>().HaveMaxLength(20);
        configurationBuilder.Properties<NotificationChannel>().HaveConversion<string>().HaveMaxLength(20);
        configurationBuilder.Properties<NotificationTemplate>().HaveConversion<string>().HaveMaxLength(OutboxMessage.TemplateMaxLength);

        // SQLite (tests only) cannot order DateTimeOffset or aggregate decimal; use sortable/aggregatable stand-ins there.
        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            configurationBuilder.Properties<DateTimeOffset>()
                .HaveConversion<Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter>();
            configurationBuilder.Properties<decimal>().HaveConversion<double>();
        }
    }
}
