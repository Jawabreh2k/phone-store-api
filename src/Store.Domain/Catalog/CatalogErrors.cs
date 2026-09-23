using Store.Domain.Common;

namespace Store.Domain.Catalog;

public static class CatalogErrors
{
    public static readonly Error ProductNotFound = Error.NotFound("catalog.product_not_found", "The product was not found.");
    public static readonly Error VariantNotFound = Error.NotFound("catalog.variant_not_found", "The product variant was not found.");
    public static readonly Error BrandNotFound = Error.NotFound("catalog.brand_not_found", "The brand was not found.");
    public static readonly Error CategoryNotFound = Error.NotFound("catalog.category_not_found", "The category was not found.");
    public static readonly Error SlugTaken = Error.Conflict("catalog.slug_taken", "Another item already uses this slug.");
    public static readonly Error SkuTaken = Error.Conflict("catalog.sku_taken", "Another variant already uses this SKU.");
    public static readonly Error CategoryTypeMismatch = Error.Validation("catalog.category_type_mismatch", "The category belongs to a different product type.");
    public static readonly Error CannotPublishWithoutVariants = Error.Validation("catalog.no_active_variants", "A product needs at least one active variant before it can be published.");
    public static readonly Error CompatibilityOnlyForAccessories = Error.Validation("catalog.compatibility_accessories_only", "Only accessories can declare compatible phones.");
    public static readonly Error CompatibilityTargetsMustBePhones = Error.Validation("catalog.compatibility_targets_phones", "Compatible products must be existing phones.");
    public static readonly Error ImageNotFound = Error.NotFound("catalog.image_not_found", "The image was not found.");
    public static readonly Error TooManyImages = Error.Validation("catalog.too_many_images", $"A product can have at most {Product.MaxImages} images.");
    public static readonly Error InvalidPrice = Error.Validation("catalog.invalid_price", "Price must be positive, and the compare-at price must be higher than the price.");
    public static readonly Error InvalidStockAdjustment = Error.Validation("catalog.invalid_stock", "Stock cannot go below zero.");

    public static Error InsufficientStock(string sku, int available) =>
        Error.Conflict("catalog.insufficient_stock", $"Only {available} unit(s) of {sku} are in stock.");
}
