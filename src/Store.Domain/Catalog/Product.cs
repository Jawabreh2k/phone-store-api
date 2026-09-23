using Store.Domain.Common;

namespace Store.Domain.Catalog;

/// <summary>
/// Aggregate root for a catalogue item (a phone model or an accessory). Variants, images,
/// specifications and compatibility are only changed through this class so its rules hold.
/// </summary>
public sealed class Product : AuditableEntity
{
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 8000;
    public const int MaxImages = 12;
    public const int MaxVariants = 50;
    public const int MaxSpecifications = 60;

    private readonly List<ProductVariant> _variants = [];
    private readonly List<ProductImage> _images = [];
    private readonly List<ProductSpecification> _specifications = [];
    private readonly List<ProductCompatibility> _compatibleWith = [];

    private Product() { } // EF

    public Product(ProductType type, Guid brandId, Guid categoryId, LocalizedText name, string slug, LocalizedText description)
    {
        Type = type;
        UpdateDetails(brandId, categoryId, name, slug, description);
    }

    public ProductType Type { get; private set; }
    public Guid BrandId { get; private set; }
    public Guid CategoryId { get; private set; }
    public LocalizedText Name { get; private set; } = null!;
    public string Slug { get; private set; } = null!;
    public LocalizedText Description { get; private set; } = null!;
    public bool IsPublished { get; private set; }
    public bool IsFeatured { get; private set; }

    public IReadOnlyList<ProductVariant> Variants => _variants;
    public IReadOnlyList<ProductImage> Images => _images;
    public IReadOnlyList<ProductSpecification> Specifications => _specifications;
    public IReadOnlyList<ProductCompatibility> CompatibleWith => _compatibleWith;

    public void UpdateDetails(Guid brandId, Guid categoryId, LocalizedText name, string slug, LocalizedText description)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(description);
        if (!Common.Slug.IsValid(slug))
        {
            throw new ArgumentException($"'{slug}' is not a valid slug.", nameof(slug));
        }

        BrandId = brandId;
        CategoryId = categoryId;
        Name = name;
        Slug = slug;
        Description = description;
    }

    public Result Publish()
    {
        if (!_variants.Any(v => v.IsActive))
        {
            return CatalogErrors.CannotPublishWithoutVariants;
        }

        IsPublished = true;
        return Result.Success();
    }

    public void Unpublish() => IsPublished = false;

    public void SetFeatured(bool featured) => IsFeatured = featured;

    public ProductVariant? FindVariant(Guid variantId) => _variants.Find(v => v.Id == variantId);

    public Result<ProductVariant> AddVariant(string sku, VariantOptions options, decimal price, decimal? compareAtPrice, int stockQuantity)
    {
        if (_variants.Count >= MaxVariants)
        {
            return Error.Validation("catalog.too_many_variants", $"A product can have at most {MaxVariants} variants.");
        }

        if (!ProductVariant.IsValidPrice(price, compareAtPrice))
        {
            return CatalogErrors.InvalidPrice;
        }

        if (stockQuantity is < 0 or > ProductVariant.MaxStock)
        {
            return CatalogErrors.InvalidStockAdjustment;
        }

        var normalizedSku = ProductVariant.NormalizeSku(sku);
        if (_variants.Exists(v => v.Sku == normalizedSku))
        {
            return CatalogErrors.SkuTaken;
        }

        var variant = new ProductVariant(Id, normalizedSku, options, price, compareAtPrice, stockQuantity);
        _variants.Add(variant);
        return variant;
    }

    public Result UpdateVariant(Guid variantId, string sku, VariantOptions options, decimal price, decimal? compareAtPrice, bool isActive)
    {
        var variant = FindVariant(variantId);
        if (variant is null)
        {
            return CatalogErrors.VariantNotFound;
        }

        if (!ProductVariant.IsValidPrice(price, compareAtPrice))
        {
            return CatalogErrors.InvalidPrice;
        }

        var normalizedSku = ProductVariant.NormalizeSku(sku);
        if (_variants.Exists(v => v.Id != variantId && v.Sku == normalizedSku))
        {
            return CatalogErrors.SkuTaken;
        }

        variant.Update(normalizedSku, options, price, compareAtPrice, isActive);

        // A published product must always have something to sell.
        if (IsPublished && !_variants.Any(v => v.IsActive))
        {
            IsPublished = false;
        }

        return Result.Success();
    }

    public Result AdjustStock(Guid variantId, int delta)
    {
        var variant = FindVariant(variantId);
        return variant is null ? CatalogErrors.VariantNotFound : variant.AdjustStock(delta);
    }

    /// <summary>Takes units out of stock for an order. Fails without side effects if not enough are left.</summary>
    public Result ReserveStock(Guid variantId, int quantity)
    {
        var variant = FindVariant(variantId);
        return variant is null ? CatalogErrors.VariantNotFound : variant.Reserve(quantity);
    }

    /// <summary>Returns units reserved by an order that was cancelled.</summary>
    public void ReleaseStock(Guid variantId, int quantity) => FindVariant(variantId)?.Release(quantity);

    public void ReplaceSpecifications(IEnumerable<SpecificationInput> specifications)
    {
        ArgumentNullException.ThrowIfNull(specifications);
        var list = specifications.ToList();
        if (list.Count > MaxSpecifications)
        {
            throw new ArgumentException($"A product can have at most {MaxSpecifications} specifications.", nameof(specifications));
        }

        _specifications.Clear();
        _specifications.AddRange(list.Select((s, i) => new ProductSpecification(Id, s.Label, s.Value, i)));
    }

    public Result<ProductImage> AddImage(string url, Guid? variantId = null)
    {
        if (_images.Count >= MaxImages)
        {
            return CatalogErrors.TooManyImages;
        }

        if (variantId is { } id && FindVariant(id) is null)
        {
            return CatalogErrors.VariantNotFound;
        }

        var image = new ProductImage(Id, url, _images.Count, variantId);
        _images.Add(image);
        return image;
    }

    /// <summary>Removes an image and returns it so the caller can delete the underlying file.</summary>
    public Result<ProductImage> RemoveImage(Guid imageId)
    {
        var image = _images.Find(i => i.Id == imageId);
        if (image is null)
        {
            return CatalogErrors.ImageNotFound;
        }

        _images.Remove(image);
        Renumber();
        return image;
    }

    /// <summary>Applies a new order. Ids not listed keep their relative order after the listed ones.</summary>
    public void ReorderImages(IReadOnlyList<Guid> orderedIds)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);
        var position = new Dictionary<Guid, int>(orderedIds.Count);
        for (var i = 0; i < orderedIds.Count; i++)
        {
            position.TryAdd(orderedIds[i], i);
        }

        var ordered = _images
            .OrderBy(i => position.GetValueOrDefault(i.Id, int.MaxValue))
            .ThenBy(i => i.SortOrder)
            .ToList();
        _images.Clear();
        _images.AddRange(ordered);
        Renumber();
    }

    /// <summary>
    /// Replaces the phones this accessory fits. The caller must pass ids it has verified are phones;
    /// that check needs the database, so it lives in the application layer.
    /// </summary>
    public Result SetCompatiblePhones(IEnumerable<Guid> phoneProductIds)
    {
        ArgumentNullException.ThrowIfNull(phoneProductIds);
        if (Type != ProductType.Accessory)
        {
            return CatalogErrors.CompatibilityOnlyForAccessories;
        }

        if (phoneProductIds.Contains(Id))
        {
            return CatalogErrors.CompatibilityTargetsMustBePhones;
        }

        // Diff rather than clear-and-add, so unchanged links keep their rows.
        var target = phoneProductIds.ToHashSet();
        _compatibleWith.RemoveAll(c => !target.Contains(c.PhoneProductId));
        foreach (var phoneId in target.Where(id => !_compatibleWith.Exists(c => c.PhoneProductId == id)))
        {
            _compatibleWith.Add(new ProductCompatibility(Id, phoneId));
        }

        return Result.Success();
    }

    private void Renumber()
    {
        for (var i = 0; i < _images.Count; i++)
        {
            _images[i].SortOrder = i;
        }
    }
}
