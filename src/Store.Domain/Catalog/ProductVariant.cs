using Store.Domain.Common;

namespace Store.Domain.Catalog;

/// <summary>
/// One sellable item: a product in a specific configuration (e.g. iPhone 16 / 256 GB / Black).
/// Price and stock live here, not on the product, because they differ per configuration.
/// </summary>
public sealed class ProductVariant : AuditableEntity
{
    public const int SkuMaxLength = 64;
    public const int MaxStock = 100_000;

    private ProductVariant() { } // EF

    internal ProductVariant(Guid productId, string sku, VariantOptions options, decimal price, decimal? compareAtPrice, int stockQuantity)
    {
        ProductId = productId;
        Sku = NormalizeSku(sku);
        SetOptions(options);
        Price = price;
        CompareAtPrice = compareAtPrice;
        StockQuantity = stockQuantity;
        IsActive = true;
    }

    public Guid ProductId { get; private set; }
    public string Sku { get; private set; } = null!;
    public decimal Price { get; private set; }

    /// <summary>Original price shown struck-through when the variant is on sale.</summary>
    public decimal? CompareAtPrice { get; private set; }

    /// <summary>Units available to sell. Placing an order decrements it; cancelling restores it.</summary>
    public int StockQuantity { get; private set; }

    public bool IsActive { get; private set; }

    public int? StorageGb { get; private set; }
    public int? RamGb { get; private set; }
    public string? ColorHex { get; private set; }

    // Colour name is optional, and EF Core 9 complex types cannot be null, so it is stored as two columns.
    private string? ColorEn { get; set; }
    private string? ColorAr { get; set; }

    public LocalizedText? Color => ColorEn is null || ColorAr is null ? null : new LocalizedText(ColorEn, ColorAr);

    public VariantOptions Options => new(Color, ColorHex, StorageGb, RamGb);

    public bool IsInStock => StockQuantity > 0;

    internal static string NormalizeSku(string sku)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        var normalized = sku.Trim().ToUpperInvariant();
        return normalized.Length <= SkuMaxLength
            ? normalized
            : throw new ArgumentException($"SKU cannot exceed {SkuMaxLength} characters.", nameof(sku));
    }

    internal static bool IsValidPrice(decimal price, decimal? compareAtPrice) =>
        price > 0 && (compareAtPrice is null || compareAtPrice > price);

    internal void Update(string sku, VariantOptions options, decimal price, decimal? compareAtPrice, bool isActive)
    {
        Sku = NormalizeSku(sku);
        SetOptions(options);
        Price = price;
        CompareAtPrice = compareAtPrice;
        IsActive = isActive;
    }

    internal Result AdjustStock(int delta)
    {
        var next = (long)StockQuantity + delta;
        if (next is < 0 or > MaxStock)
        {
            return CatalogErrors.InvalidStockAdjustment;
        }

        StockQuantity = (int)next;
        return Result.Success();
    }

    internal Result Reserve(int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        if (quantity > StockQuantity)
        {
            return CatalogErrors.InsufficientStock(Sku, StockQuantity);
        }

        StockQuantity -= quantity;
        return Result.Success();
    }

    internal void Release(int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        StockQuantity = Math.Min(MaxStock, StockQuantity + quantity);
    }

    private void SetOptions(VariantOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ColorEn = options.Color?.En;
        ColorAr = options.Color?.Ar;
        ColorHex = options.ColorHex;
        StorageGb = options.StorageGb;
        RamGb = options.RamGb;
    }
}

/// <summary>The attributes that distinguish one variant from its siblings. All optional: a charger may have none.</summary>
public sealed record VariantOptions(LocalizedText? Color = null, string? ColorHex = null, int? StorageGb = null, int? RamGb = null)
{
    public static readonly VariantOptions None = new();

    /// <summary>Human label such as "256 GB · 8 GB RAM · Black", used in carts and order snapshots.</summary>
    public string Describe(Language language)
    {
        var parts = new List<string>(3);
        if (StorageGb is { } storage)
        {
            parts.Add(storage >= 1024 && storage % 1024 == 0 ? $"{storage / 1024} TB" : $"{storage} GB");
        }

        if (RamGb is { } ram)
        {
            parts.Add(language == Language.Ar ? $"{ram} GB رام" : $"{ram} GB RAM");
        }

        if (Color is not null)
        {
            parts.Add(Color.In(language));
        }

        return string.Join(" · ", parts);
    }
}
