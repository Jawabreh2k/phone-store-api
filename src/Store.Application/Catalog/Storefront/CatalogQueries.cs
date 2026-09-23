using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Store.Application.Abstractions;
using Store.Application.Common;
using Store.Domain.Catalog;
using Store.Domain.Common;

namespace Store.Application.Catalog.Storefront;

public sealed record GetBrandsQuery(ProductType? Type) : IQuery<IReadOnlyList<BrandDto>>;

internal sealed class GetBrandsHandler(IAppDbContext db) : IQueryHandler<GetBrandsQuery, IReadOnlyList<BrandDto>>
{
    public async Task<Result<IReadOnlyList<BrandDto>>> Handle(GetBrandsQuery query, CancellationToken cancellationToken)
    {
        var brands = db.Brands.AsNoTracking().Where(b => b.IsActive);

        // With a type filter, only list brands that actually have something to show in it.
        if (query.Type is { } type)
        {
            brands = brands.Where(b => db.Products.Any(p => p.BrandId == b.Id && p.IsPublished && p.Type == type));
        }

        return await brands
            .OrderBy(b => b.SortOrder).ThenBy(b => b.Name)
            .Select(b => new BrandDto(b.Id, b.Name, b.Slug, b.LogoUrl))
            .ToListAsync(cancellationToken);
    }
}

public sealed record GetCategoriesQuery(ProductType? Type) : IQuery<IReadOnlyList<CategoryDto>>;

internal sealed class GetCategoriesHandler(IAppDbContext db, ILanguageProvider language)
    : IQueryHandler<GetCategoriesQuery, IReadOnlyList<CategoryDto>>
{
    public async Task<Result<IReadOnlyList<CategoryDto>>> Handle(GetCategoriesQuery query, CancellationToken cancellationToken)
    {
        var ar = language.Current == Language.Ar;
        return await db.Categories.AsNoTracking()
            .Where(c => c.IsActive && (query.Type == null || c.ProductType == query.Type))
            .OrderBy(c => c.ProductType).ThenBy(c => c.SortOrder)
            .Select(c => new CategoryDto(c.Id, ar ? c.Name.Ar : c.Name.En, c.Slug, c.ProductType))
            .ToListAsync(cancellationToken);
    }
}

public sealed record SearchProductsQuery(
    ProductType? Type = null,
    string? Brand = null,
    string? Category = null,
    string? Search = null,
    decimal? MinPrice = null,
    decimal? MaxPrice = null,
    int? StorageGb = null,
    bool InStockOnly = false,
    bool FeaturedOnly = false,
    string? CompatibleWith = null,
    ProductSort Sort = ProductSort.Featured,
    int? Page = null,
    int? PageSize = null) : IQuery<PagedResult<ProductSummaryDto>>;

public sealed class SearchProductsValidator : AbstractValidator<SearchProductsQuery>
{
    public SearchProductsValidator()
    {
        RuleFor(x => x.Search).MaximumLength(100);
        RuleFor(x => x.MinPrice).GreaterThanOrEqualTo(0);
        RuleFor(x => x.MaxPrice).GreaterThanOrEqualTo(x => x.MinPrice ?? 0).When(x => x.MaxPrice is not null);
        RuleFor(x => x.PageSize).InclusiveBetween(1, Paging.MaxPageSize).When(x => x.PageSize is not null);
    }
}

internal sealed class SearchProductsHandler(IAppDbContext db, ILanguageProvider language)
    : IQueryHandler<SearchProductsQuery, PagedResult<ProductSummaryDto>>
{
    public async Task<Result<PagedResult<ProductSummaryDto>>> Handle(SearchProductsQuery q, CancellationToken cancellationToken)
    {
        var products = CatalogReadModel.Visible(db);

        if (q.Type is { } type)
        {
            products = products.Where(p => p.Type == type);
        }

        if (!string.IsNullOrWhiteSpace(q.Brand))
        {
            products = products.Where(p => db.Brands.Any(b => b.Id == p.BrandId && b.Slug == q.Brand));
        }

        if (!string.IsNullOrWhiteSpace(q.Category))
        {
            products = products.Where(p => db.Categories.Any(c => c.Id == p.CategoryId && c.Slug == q.Category));
        }

        if (!string.IsNullOrWhiteSpace(q.CompatibleWith))
        {
            products = products.Where(p => p.CompatibleWith.Any(c =>
                db.Products.Any(phone => phone.Id == c.PhoneProductId && phone.Slug == q.CompatibleWith)));
        }

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = q.Search.Trim();
            products = products.Where(p =>
                p.Name.En.Contains(term) ||
                p.Name.Ar.Contains(term) ||
                db.Brands.Any(b => b.Id == p.BrandId && b.Name.Contains(term)) ||
                p.Variants.Any(v => v.Sku == term));
        }

        if (q.StorageGb is { } storage)
        {
            products = products.Where(p => p.Variants.Any(v => v.IsActive && v.StorageGb == storage));
        }

        if (q.InStockOnly)
        {
            products = products.Where(p => p.Variants.Any(v => v.IsActive && v.StockQuantity > 0));
        }

        if (q.FeaturedOnly)
        {
            products = products.Where(p => p.IsFeatured);
        }

        // Price filters and sorting use the cheapest active variant, i.e. the "from" price shown on the card.
        if (q.MinPrice is { } min)
        {
            products = products.Where(p => p.Variants.Where(v => v.IsActive).Min(v => v.Price) >= min);
        }

        if (q.MaxPrice is { } max)
        {
            products = products.Where(p => p.Variants.Where(v => v.IsActive).Min(v => v.Price) <= max);
        }

        products = q.Sort switch
        {
            ProductSort.Newest => products.OrderByDescending(p => p.CreatedAt),
            ProductSort.PriceAsc => products.OrderBy(p => p.Variants.Where(v => v.IsActive).Min(v => v.Price)),
            ProductSort.PriceDesc => products.OrderByDescending(p => p.Variants.Where(v => v.IsActive).Min(v => v.Price)),
            ProductSort.NameAsc => language.Current == Language.Ar
                ? products.OrderBy(p => p.Name.Ar)
                : products.OrderBy(p => p.Name.En),
            _ => products.OrderByDescending(p => p.IsFeatured).ThenByDescending(p => p.CreatedAt),
        };

        var page = await CatalogReadModel.ToCardRows(db, products, language.Current)
            .ToPagedResultAsync(q.Page, q.PageSize, cancellationToken);

        var items = await CatalogReadModel.WithColorsAsync(db, page.Items, cancellationToken);
        return new PagedResult<ProductSummaryDto>(items, page.Page, page.PageSize, page.TotalCount);
    }
}

public sealed record GetProductBySlugQuery(string Slug) : IQuery<ProductDetailsDto>;

internal sealed class GetProductBySlugHandler(IAppDbContext db, ILanguageProvider language)
    : IQueryHandler<GetProductBySlugQuery, ProductDetailsDto>
{
    private const int LowStockThreshold = 5;
    private const int RelatedCount = 8;

    public async Task<Result<ProductDetailsDto>> Handle(GetProductBySlugQuery query, CancellationToken cancellationToken)
    {
        var lang = language.Current;
        var product = await CatalogReadModel.Visible(db)
            .Include(p => p.Variants)
            .Include(p => p.Images)
            .Include(p => p.Specifications)
            .FirstOrDefaultAsync(p => p.Slug == query.Slug, cancellationToken);

        if (product is null)
        {
            return CatalogErrors.ProductNotFound;
        }

        var brand = await db.Brands.AsNoTracking().FirstAsync(b => b.Id == product.BrandId, cancellationToken);
        var category = await db.Categories.AsNoTracking().FirstAsync(c => c.Id == product.CategoryId, cancellationToken);

        // Phones show accessories that fit them; accessories show the phones they fit.
        var related = product.Type == ProductType.Phone
            ? CatalogReadModel.Visible(db).Where(p => p.CompatibleWith.Any(c => c.PhoneProductId == product.Id))
            : CatalogReadModel.Visible(db).Where(p => product.CompatibleWith.Select(c => c.PhoneProductId).Contains(p.Id));

        var relatedRows = await CatalogReadModel.ToCardRows(db, related.OrderByDescending(p => p.IsFeatured).Take(RelatedCount), lang)
            .ToListAsync(cancellationToken);
        var relatedItems = await CatalogReadModel.WithColorsAsync(db, relatedRows, cancellationToken);

        var variants = product.Variants
            .Where(v => v.IsActive)
            .OrderBy(v => v.StorageGb).ThenBy(v => v.Price)
            .Select(v => new VariantDto(
                v.Id,
                v.Sku,
                v.Options.Describe(lang),
                v.Color?.In(lang),
                v.ColorHex,
                v.StorageGb,
                v.RamGb,
                v.Price,
                v.CompareAtPrice,
                v.IsInStock,
                v.StockQuantity is > 0 and <= LowStockThreshold ? v.StockQuantity : null))
            .ToList();

        return new ProductDetailsDto(
            product.Id,
            product.Slug,
            product.Name.In(lang),
            product.Description.In(lang),
            product.Type,
            new BrandDto(brand.Id, brand.Name, brand.Slug, brand.LogoUrl),
            new CategoryDto(category.Id, category.Name.In(lang), category.Slug, category.ProductType),
            variants,
            product.Images.OrderBy(i => i.SortOrder).Select(i => new ImageDto(i.Id, i.Url, i.VariantId)).ToList(),
            product.Specifications.OrderBy(s => s.SortOrder).Select(s => new SpecificationDto(s.Label.In(lang), s.Value.In(lang))).ToList(),
            relatedItems);
    }
}

/// <summary>Shared query building blocks for customer-facing catalogue reads.</summary>
internal static class CatalogReadModel
{
    /// <summary>Products a customer may see: published, with at least one active variant.</summary>
    public static IQueryable<Product> Visible(IAppDbContext db) =>
        db.Products.AsNoTracking().Where(p => p.IsPublished && p.Variants.Any(v => v.IsActive));

    /// <summary>
    /// Flat, scalar-only projection for product cards, so it translates to one simple SQL statement
    /// that pages cleanly. Colour swatches are loaded afterwards for just the rows on the page.
    /// </summary>
    public static IQueryable<ProductSummaryDto> ToCardRows(IAppDbContext db, IQueryable<Product> products, Language language)
    {
        var ar = language == Language.Ar;
        return products.Select(p => new ProductSummaryDto(
            p.Id,
            p.Slug,
            ar ? p.Name.Ar : p.Name.En,
            p.Type,
            db.Brands.Where(b => b.Id == p.BrandId).Select(b => b.Name).First(),
            db.Brands.Where(b => b.Id == p.BrandId).Select(b => b.Slug).First(),
            p.Images.OrderBy(i => i.SortOrder).Select(i => i.Url).FirstOrDefault(),
            p.Variants.Where(v => v.IsActive).Min(v => v.Price),
            p.Variants.Where(v => v.IsActive).OrderBy(v => v.Price).Select(v => v.CompareAtPrice).FirstOrDefault(),
            p.Variants.Where(v => v.IsActive).Max(v => v.Price) > p.Variants.Where(v => v.IsActive).Min(v => v.Price),
            p.Variants.Any(v => v.IsActive && v.StockQuantity > 0),
            p.IsFeatured,
            Array.Empty<string>()));
    }

    public static async Task<IReadOnlyList<ProductSummaryDto>> WithColorsAsync(
        IAppDbContext db, IReadOnlyList<ProductSummaryDto> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return rows;
        }

        var ids = rows.Select(r => r.Id).ToList();
        var colors = (await db.ProductVariants.AsNoTracking()
                .Where(v => ids.Contains(v.ProductId) && v.IsActive && v.ColorHex != null)
                .Select(v => new { v.ProductId, v.ColorHex })
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToLookup(v => v.ProductId, v => v.ColorHex!);

        return rows.Select(r => r with { ColorHexes = colors[r.Id].ToList() }).ToList();
    }
}
