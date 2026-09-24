using Store.Domain.Common;

namespace Store.Domain.Catalog;

/// <summary>Manufacturer (Apple, Samsung, Anker…). Brand names are proper nouns, so they are not translated.</summary>
public sealed class Brand : AuditableEntity
{
    public const int NameMaxLength = 80;

    private Brand() { } // EF

    public Brand(string name, string slug, string? logoUrl = null, int sortOrder = 0)
    {
        Rename(name, slug);
        LogoUrl = logoUrl;
        SortOrder = sortOrder;
        IsActive = true;
    }

    public string Name { get; private set; } = null!;
    public string Slug { get; private set; } = null!;
    public string? LogoUrl { get; private set; }
    public int SortOrder { get; private set; }
    public bool IsActive { get; private set; }

    public void Rename(string name, string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Common.Slug.IsValid(slug))
        {
            throw new ArgumentException($"'{slug}' is not a valid slug.", nameof(slug));
        }

        Name = name.Trim();
        Slug = slug;
    }

    public void Update(string? logoUrl, int sortOrder, bool isActive)
    {
        LogoUrl = logoUrl;
        SortOrder = sortOrder;
        IsActive = isActive;
    }

    /// <summary>Position in the store's brand filters (ascending).</summary>
    public void MoveTo(int sortOrder) => SortOrder = sortOrder;
}
