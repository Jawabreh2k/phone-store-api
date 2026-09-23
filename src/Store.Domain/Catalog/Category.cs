using Store.Domain.Common;

namespace Store.Domain.Catalog;

public enum ProductType
{
    Phone = 1,
    Accessory = 2,
}

/// <summary>
/// Flat grouping within a product type, e.g. "Smartphones" for phones or "Cases" / "Chargers" for accessories.
/// A tree is deliberately not modelled until the catalogue actually needs one.
/// </summary>
public sealed class Category : AuditableEntity
{
    private Category() { } // EF

    public Category(LocalizedText name, string slug, ProductType productType, int sortOrder = 0)
    {
        Update(name, slug, sortOrder, isActive: true);
        ProductType = productType;
    }

    public LocalizedText Name { get; private set; } = null!;
    public string Slug { get; private set; } = null!;
    public ProductType ProductType { get; private set; }
    public int SortOrder { get; private set; }
    public bool IsActive { get; private set; }

    public void Update(LocalizedText name, string slug, int sortOrder, bool isActive)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!Common.Slug.IsValid(slug))
        {
            throw new ArgumentException($"'{slug}' is not a valid slug.", nameof(slug));
        }

        Name = name;
        Slug = slug;
        SortOrder = sortOrder;
        IsActive = isActive;
    }
}
