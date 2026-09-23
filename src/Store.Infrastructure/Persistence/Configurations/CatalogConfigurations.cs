using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Store.Domain.Catalog;
using Store.Domain.Common;

namespace Store.Infrastructure.Persistence.Configurations;

internal sealed class BrandConfiguration : IEntityTypeConfiguration<Brand>
{
    public void Configure(EntityTypeBuilder<Brand> builder)
    {
        builder.Property(b => b.Name).HasMaxLength(Brand.NameMaxLength);
        builder.Property(b => b.Slug).HasMaxLength(Slug.MaxLength);
        builder.Property(b => b.LogoUrl).HasMaxLength(ProductImage.UrlMaxLength);
        builder.HasIndex(b => b.Slug).IsUnique();
    }
}

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.HasLocalizedText(c => c.Name, "Name", 100);
        builder.Property(c => c.Slug).HasMaxLength(Slug.MaxLength);
        builder.HasIndex(c => c.Slug).IsUnique();
    }
}

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.HasLocalizedText(p => p.Name, "Name", Product.NameMaxLength);
        builder.HasLocalizedText(p => p.Description, "Description", Product.DescriptionMaxLength);
        builder.Property(p => p.Slug).HasMaxLength(Slug.MaxLength);
        builder.HasIndex(p => p.Slug).IsUnique();
        builder.HasIndex(p => new { p.IsPublished, p.Type });

        builder.HasOne<Brand>().WithMany().HasForeignKey(p => p.BrandId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Category>().WithMany().HasForeignKey(p => p.CategoryId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(p => p.Variants).WithOne().HasForeignKey(v => v.ProductId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(p => p.Images).WithOne().HasForeignKey(i => i.ProductId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(p => p.Specifications).WithOne().HasForeignKey(s => s.ProductId).OnDelete(DeleteBehavior.Cascade);

        // Owned so it always loads with the product: it is small and the domain diffs against it.
        builder.OwnsMany(p => p.CompatibleWith, c =>
        {
            c.ToTable("ProductCompatibility");
            c.WithOwner().HasForeignKey(x => x.AccessoryId);
            c.HasKey(x => new { x.AccessoryId, x.PhoneProductId });
            c.HasOne<Product>().WithMany().HasForeignKey(x => x.PhoneProductId).OnDelete(DeleteBehavior.Restrict);
            c.HasIndex(x => x.PhoneProductId);
        });

        foreach (var navigation in new[] { nameof(Product.Variants), nameof(Product.Images), nameof(Product.Specifications), nameof(Product.CompatibleWith) })
        {
            builder.Navigation(navigation).UsePropertyAccessMode(PropertyAccessMode.Field);
        }
    }
}

internal sealed class ProductVariantConfiguration : IEntityTypeConfiguration<ProductVariant>
{
    public void Configure(EntityTypeBuilder<ProductVariant> builder)
    {
        builder.Property(v => v.Sku).HasMaxLength(ProductVariant.SkuMaxLength);
        builder.HasIndex(v => v.Sku).IsUnique();
        builder.Property(v => v.ColorHex).HasMaxLength(7);
        builder.Property<string?>("ColorEn").HasMaxLength(50);
        builder.Property<string?>("ColorAr").HasMaxLength(50);
        builder.Ignore(v => v.Color);
        builder.Ignore(v => v.Options);
        builder.Ignore(v => v.IsInStock);
        builder.ToTable(t => t.HasCheckConstraint("CK_ProductVariants_Stock", "[StockQuantity] >= 0"));
    }
}

internal sealed class ProductImageConfiguration : IEntityTypeConfiguration<ProductImage>
{
    public void Configure(EntityTypeBuilder<ProductImage> builder)
    {
        builder.Property(i => i.Url).HasMaxLength(ProductImage.UrlMaxLength);

        // Variants are deactivated rather than deleted, so no cascade is needed (and SQL Server forbids a second path).
        builder.HasOne<ProductVariant>().WithMany().HasForeignKey(i => i.VariantId).OnDelete(DeleteBehavior.ClientSetNull);
    }
}

internal sealed class ProductSpecificationConfiguration : IEntityTypeConfiguration<ProductSpecification>
{
    public void Configure(EntityTypeBuilder<ProductSpecification> builder)
    {
        builder.HasLocalizedText(s => s.Label, "Label", 100);
        builder.HasLocalizedText(s => s.Value, "Value", 500);
    }
}
