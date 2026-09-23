using Store.Domain.Common;

namespace Store.Domain.Catalog;

/// <summary>A product photo. <see cref="VariantId"/> ties it to a colour so the gallery can switch with the selection.</summary>
public sealed class ProductImage : Entity
{
    public const int UrlMaxLength = 500;

    private ProductImage() { } // EF

    internal ProductImage(Guid productId, string url, int sortOrder, Guid? variantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ProductId = productId;
        Url = url;
        SortOrder = sortOrder;
        VariantId = variantId;
    }

    public Guid ProductId { get; private set; }
    public string Url { get; private set; } = null!;
    public int SortOrder { get; internal set; }
    public Guid? VariantId { get; internal set; }
}

/// <summary>A row in the specification table (e.g. "Screen" → "6.3-inch OLED").</summary>
public sealed class ProductSpecification : Entity
{
    private ProductSpecification() { } // EF

    internal ProductSpecification(Guid productId, LocalizedText label, LocalizedText value, int sortOrder)
    {
        ProductId = productId;
        Label = label;
        Value = value;
        SortOrder = sortOrder;
    }

    public Guid ProductId { get; private set; }
    public LocalizedText Label { get; private set; } = null!;
    public LocalizedText Value { get; private set; } = null!;
    public int SortOrder { get; private set; }
}

/// <summary>Links an accessory to a phone it fits, so a phone page can show "cases for this phone".</summary>
public sealed class ProductCompatibility
{
    private ProductCompatibility() { } // EF

    internal ProductCompatibility(Guid accessoryId, Guid phoneProductId)
    {
        AccessoryId = accessoryId;
        PhoneProductId = phoneProductId;
    }

    public Guid AccessoryId { get; private set; }
    public Guid PhoneProductId { get; private set; }
}

public sealed record SpecificationInput(LocalizedText Label, LocalizedText Value);
