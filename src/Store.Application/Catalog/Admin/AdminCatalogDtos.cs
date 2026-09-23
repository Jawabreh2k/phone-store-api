using Store.Application.Catalog.Storefront;
using Store.Application.Common;
using Store.Domain.Catalog;

namespace Store.Application.Catalog.Admin;

// Admin DTOs carry both languages so the editor can show them side by side.

public sealed record AdminBrandDto(Guid Id, string Name, string Slug, string? LogoUrl, int SortOrder, bool IsActive);

public sealed record AdminCategoryDto(Guid Id, LocalizedTextDto Name, string Slug, ProductType ProductType, int SortOrder, bool IsActive);

public sealed record AdminProductListItemDto(
    Guid Id,
    LocalizedTextDto Name,
    string Slug,
    ProductType Type,
    string BrandName,
    bool IsPublished,
    bool IsFeatured,
    int VariantCount,
    int TotalStock,
    decimal? MinPrice,
    string? ImageUrl,
    DateTimeOffset? UpdatedAt);

public sealed record AdminProductDto(
    Guid Id,
    ProductType Type,
    Guid BrandId,
    Guid CategoryId,
    LocalizedTextDto Name,
    string Slug,
    LocalizedTextDto Description,
    bool IsPublished,
    bool IsFeatured,
    IReadOnlyList<AdminVariantDto> Variants,
    IReadOnlyList<ImageDto> Images,
    IReadOnlyList<SpecificationInputDto> Specifications,
    IReadOnlyList<Guid> CompatiblePhoneIds);

public sealed record AdminVariantDto(
    Guid Id,
    string Sku,
    LocalizedTextDto? Color,
    string? ColorHex,
    int? StorageGb,
    int? RamGb,
    decimal Price,
    decimal? CompareAtPrice,
    int StockQuantity,
    bool IsActive);

public sealed record SpecificationInputDto(LocalizedTextDto Label, LocalizedTextDto Value);

public sealed record VariantInput(
    string Sku,
    LocalizedTextDto? Color,
    string? ColorHex,
    int? StorageGb,
    int? RamGb,
    decimal Price,
    decimal? CompareAtPrice)
{
    public VariantOptions ToOptions() => new(Color?.ToDomain(), ColorHex, StorageGb, RamGb);
}
