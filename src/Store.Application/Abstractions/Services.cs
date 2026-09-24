using Microsoft.EntityFrameworkCore;
using Store.Application.Outbox;
using Store.Domain.Carts;
using Store.Domain.Catalog;
using Store.Domain.Common;
using Store.Domain.Customers;
using Store.Domain.Orders;
using Store.Domain.Settings;

namespace Store.Application.Abstractions;

/// <summary>
/// Unit of work over the store's data. Handlers query and change aggregates through it;
/// the EF Core implementation lives in Infrastructure.
/// </summary>
public interface IAppDbContext
{
    DbSet<Brand> Brands { get; }
    DbSet<Category> Categories { get; }
    DbSet<Product> Products { get; }
    DbSet<ProductVariant> ProductVariants { get; }
    DbSet<Cart> Carts { get; }
    DbSet<Order> Orders { get; }
    DbSet<CustomerAddress> CustomerAddresses { get; }
    DbSet<StoreSettings> StoreSettings { get; }
    DbSet<OutboxMessage> OutboxMessages { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>The signed-in user for the current request, if any.</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
    bool IsAuthenticated { get; }
    bool IsInRole(string role);

    /// <summary>User id or an exception; use only on endpoints that already require authentication.</summary>
    Guid RequiredUserId => UserId ?? throw new InvalidOperationException("No authenticated user on this request.");
}

/// <summary>Language the current request should be answered in (from the route or Accept-Language).</summary>
public interface ILanguageProvider
{
    Language Current { get; }
}

/// <summary>Stores uploaded files (product photos, logos). Local disk now; blob storage later.</summary>
public interface IFileStorage
{
    /// <summary>Saves the file and returns the public URL path it is served from.</summary>
    Task<string> SaveAsync(Stream content, string extension, string folder, CancellationToken cancellationToken);

    Task DeleteAsync(string url, CancellationToken cancellationToken);
}

/// <summary>Produces short, unique, human-friendly order numbers (e.g. 100245).</summary>
public interface IOrderNumberGenerator
{
    Task<string> NextAsync(CancellationToken cancellationToken);
}
