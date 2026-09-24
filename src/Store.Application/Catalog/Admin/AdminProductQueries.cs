using Microsoft.EntityFrameworkCore;
using Store.Application.Abstractions;
using Store.Application.Catalog.Storefront;
using Store.Application.Common;
using Store.Domain.Catalog;
using Store.Domain.Common;

namespace Store.Application.Catalog.Admin;

public sealed record GetAdminProductsQuery(
    string? Search = null,
    ProductType? Type = null,
    Guid? BrandId = null,
    bool? Published = null,
    bool LowStockOnly = false,
    bool NoPhotosOnly = false,
    bool OutOfStockOnly = false,
    int? Page = null,
    int? PageSize = null) : IQuery<PagedResult<AdminProductListItemDto>>;

internal sealed class GetAdminProductsHandler(IAppDbContext db)
    : IQueryHandler<GetAdminProductsQuery, PagedResult<AdminProductListItemDto>>
{
    public const int LowStockThreshold = 5;

    public async Task<Result<PagedResult<AdminProductListItemDto>>> Handle(GetAdminProductsQuery q, CancellationToken cancellationToken)
    {
        var products = db.Products.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = q.Search.Trim();
            products = products.Where(p =>
                p.Name.En.Contains(term) || p.Name.Ar.Contains(term) || p.Slug.Contains(term) ||
                p.Variants.Any(v => v.Sku.Contains(term)));
        }

        if (q.Type is { } type)
        {
            products = products.Where(p => p.Type == type);
        }

        if (q.BrandId is { } brandId)
        {
            products = products.Where(p => p.BrandId == brandId);
        }

        if (q.Published is { } published)
        {
            products = products.Where(p => p.IsPublished == published);
        }

        if (q.LowStockOnly)
        {
            products = products.Where(p => p.Variants.Any(v => v.IsActive && v.StockQuantity <= LowStockThreshold));
        }

        if (q.NoPhotosOnly)
        {
            products = products.Where(p => p.IsPublished && !p.Images.Any());
        }

        if (q.OutOfStockOnly)
        {
            products = products.Where(p => p.IsPublished && !p.Variants.Any(v => v.IsActive && v.StockQuantity > 0));
        }

        return await products
            .OrderByDescending(p => p.UpdatedAt ?? p.CreatedAt)
            .Select(p => new AdminProductListItemDto(
                p.Id,
                new LocalizedTextDto(p.Name.En, p.Name.Ar),
                p.Slug,
                p.Type,
                db.Brands.Where(b => b.Id == p.BrandId).Select(b => b.Name).First(),
                p.IsPublished,
                p.IsFeatured,
                p.Variants.Count,
                p.Variants.Sum(v => v.StockQuantity),
                p.Variants.Any(v => v.IsActive && v.StockQuantity <= LowStockThreshold),
                p.Variants.Where(v => v.IsActive).Min(v => (decimal?)v.Price),
                p.Images.OrderBy(i => i.SortOrder).Select(i => i.Url).FirstOrDefault(),
                p.UpdatedAt ?? p.CreatedAt))
            .ToPagedResultAsync(q.Page, q.PageSize, cancellationToken);
    }
}

public sealed record GetAdminProductQuery(Guid Id) : IQuery<AdminProductDto>;

internal sealed class GetAdminProductHandler(IAppDbContext db) : IQueryHandler<GetAdminProductQuery, AdminProductDto>
{
    public async Task<Result<AdminProductDto>> Handle(GetAdminProductQuery query, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking()
            .Include(p => p.Variants)
            .Include(p => p.Images)
            .Include(p => p.Specifications)
            .FirstOrDefaultAsync(p => p.Id == query.Id, cancellationToken);

        return product is null ? CatalogErrors.ProductNotFound : ToDto(product);
    }

    internal static AdminProductDto ToDto(Product p) => new(
        p.Id,
        p.Type,
        p.BrandId,
        p.CategoryId,
        LocalizedTextDto.From(p.Name),
        p.Slug,
        LocalizedTextDto.From(p.Description),
        p.IsPublished,
        p.IsFeatured,
        p.Variants
            .OrderBy(v => v.StorageGb).ThenBy(v => v.Price)
            .Select(v => new AdminVariantDto(
                v.Id, v.Sku, v.Color is null ? null : LocalizedTextDto.From(v.Color), v.ColorHex,
                v.StorageGb, v.RamGb, v.Price, v.CompareAtPrice, v.StockQuantity, v.IsActive))
            .ToList(),
        p.Images.OrderBy(i => i.SortOrder).Select(i => new ImageDto(i.Id, i.Url, i.VariantId)).ToList(),
        p.Specifications.OrderBy(s => s.SortOrder)
            .Select(s => new SpecificationInputDto(LocalizedTextDto.From(s.Label), LocalizedTextDto.From(s.Value)))
            .ToList(),
        p.CompatibleWith.Select(c => c.PhoneProductId).ToList());
}
