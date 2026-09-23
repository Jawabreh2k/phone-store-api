using Store.Domain.Catalog;

namespace Store.Application.Catalog.Storefront;

// Storefront DTOs carry text already resolved to the request language, so the frontend never picks translations.

public sealed record BrandDto(Guid Id, string Name, string Slug, string? LogoUrl);

public sealed record CategoryDto(Guid Id, string Name, string Slug, ProductType ProductType);

public sealed record ProductSummaryDto(
    Guid Id,
    string Slug,
    string Name,
    ProductType Type,
    string BrandName,
    string BrandSlug,
    string? ImageUrl,
    decimal Price, // cheapest active variant: a "from" price when HasPriceRange is true
    decimal? CompareAtPrice,
    bool HasPriceRange, // active variants differ in price, so the card must say "From"
    bool InStock,
    bool IsFeatured,
    IReadOnlyList<string> ColorHexes);

public sealed record ProductDetailsDto(
    Guid Id,
    string Slug,
    string Name,
    string Description,
    ProductType Type,
    BrandDto Brand,
    CategoryDto Category,
    IReadOnlyList<VariantDto> Variants,
    IReadOnlyList<ImageDto> Images,
    IReadOnlyList<SpecificationDto> Specifications,
    IReadOnlyList<ProductSummaryDto> Related);

public sealed record VariantDto(
    Guid Id,
    string Sku,
    string Label,
    string? Color,
    string? ColorHex,
    int? StorageGb,
    int? RamGb,
    decimal Price,
    decimal? CompareAtPrice,
    bool InStock,
    int? LowStockQuantity); // exact stock is hidden; only set below a threshold for "only N left"

public sealed record ImageDto(Guid Id, string Url, Guid? VariantId);

public sealed record SpecificationDto(string Label, string Value);

public enum ProductSort
{
    Featured,
    Newest,
    PriceAsc,
    PriceDesc,
    NameAsc,
}
