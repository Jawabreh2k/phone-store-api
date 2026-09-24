using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Store.Domain.Carts;
using Store.Domain.Catalog;
using Store.Domain.Common;
using Store.Domain.Customers;
using Store.Domain.Orders;
using Store.Domain.Settings;
using Store.Infrastructure.Identity;

namespace Store.Infrastructure.Persistence.Configurations;

internal sealed class CartConfiguration : IEntityTypeConfiguration<Cart>
{
    public void Configure(EntityTypeBuilder<Cart> builder)
    {
        // One cart per customer; any number of guest carts (null owner).
        builder.HasIndex(c => c.CustomerId).IsUnique().HasFilter("[CustomerId] IS NOT NULL");
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(c => c.CustomerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(c => c.UpdatedAt); // for purging abandoned guest carts

        builder.HasMany(c => c.Items).WithOne().HasForeignKey(i => i.CartId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(c => c.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class CartItemConfiguration : IEntityTypeConfiguration<CartItem>
{
    public void Configure(EntityTypeBuilder<CartItem> builder)
    {
        builder.HasKey(i => new { i.CartId, i.VariantId });
        builder.HasOne<ProductVariant>().WithMany().HasForeignKey(i => i.VariantId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.Property(o => o.OrderNumber).HasMaxLength(20);
        builder.HasIndex(o => o.OrderNumber).IsUnique();
        // ASCII-only columns (hex hash, ISO code, E.164 phone) are non-Unicode: half the bytes and index size.
        builder.Property(o => o.TrackingTokenHash).HasMaxLength(OrderTrackingToken.HashLength).IsFixedLength().IsUnicode(false);
        builder.HasIndex(o => o.TrackingTokenHash).IsUnique();
        builder.Property(o => o.CurrencyCode).HasMaxLength(3).IsFixedLength().IsUnicode(false);
        builder.HasIndex(o => new { o.CustomerId, o.PlacedAt });
        builder.HasIndex(o => new { o.Status, o.PlacedAt });
        builder.Property(o => o.CustomerNote).HasMaxLength(Order.NoteMaxLength);
        builder.Property(o => o.CancellationReason).HasMaxLength(500);

        // Accounts with orders cannot be hard-deleted; order history must survive.
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(o => o.CustomerId).OnDelete(DeleteBehavior.Restrict);

        builder.ComplexProperty(o => o.Contact, c =>
        {
            c.Property(x => x.FullName).HasColumnName("ContactName").HasMaxLength(120);
            c.Property(x => x.Phone).HasColumnName("ContactPhone").HasMaxLength(PhoneNumber.MaxLength).IsUnicode(false);
            c.Property(x => x.Email).HasColumnName("ContactEmail").HasMaxLength(256);
        });
        builder.ComplexProperty(o => o.ShippingAddress, a => a.MapAddress("Ship"));

        builder.HasMany(o => o.Items).WithOne().HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(o => o.History).WithOne().HasForeignKey(h => h.OrderId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(o => o.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(o => o.History).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Ignore(o => o.NextStatuses);
    }
}

internal sealed class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        builder.HasLocalizedText(i => i.ProductName, "ProductName", Product.NameMaxLength);
        builder.HasLocalizedText(i => i.VariantDescription, "Variant", 150);
        builder.Property(i => i.Sku).HasMaxLength(ProductVariant.SkuMaxLength);
        builder.Property(i => i.ImageUrl).HasMaxLength(ProductImage.UrlMaxLength);
        builder.HasIndex(i => i.ProductId);
    }
}

internal sealed class OrderStatusChangeConfiguration : IEntityTypeConfiguration<OrderStatusChange>
{
    public void Configure(EntityTypeBuilder<OrderStatusChange> builder)
    {
        builder.ToTable("OrderStatusHistory");
        builder.Property(h => h.Note).HasMaxLength(500);
    }
}

internal sealed class CustomerAddressConfiguration : IEntityTypeConfiguration<CustomerAddress>
{
    public void Configure(EntityTypeBuilder<CustomerAddress> builder)
    {
        builder.Property(a => a.Label).HasMaxLength(40);
        builder.Property(a => a.RecipientName).HasMaxLength(120);
        builder.Property(a => a.Phone).HasMaxLength(PhoneNumber.MaxLength).IsUnicode(false);
        builder.ComplexProperty(a => a.Address, a => a.MapAddress(prefix: string.Empty));
        builder.HasIndex(a => a.CustomerId);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(a => a.CustomerId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class StoreSettingsConfiguration : IEntityTypeConfiguration<StoreSettings>
{
    public void Configure(EntityTypeBuilder<StoreSettings> builder)
    {
        builder.HasLocalizedText(s => s.StoreName, "StoreName", 80);
        builder.Property(s => s.LogoUrl).HasMaxLength(ProductImage.UrlMaxLength);
        builder.Property(s => s.SupportPhone).HasMaxLength(PhoneNumber.MaxLength).IsUnicode(false);
        builder.Property(s => s.WhatsAppNumber).HasMaxLength(PhoneNumber.MaxLength).IsUnicode(false);
        builder.Property(s => s.PhoneCountryCode).HasMaxLength(3).IsUnicode(false);
        builder.Property(s => s.SupportEmail).HasMaxLength(256);
        builder.Property(s => s.CurrencyCode).HasMaxLength(3).IsFixedLength().IsUnicode(false);
        builder.Property(s => s.TimeZoneId).HasMaxLength(64).IsUnicode(false);
    }
}

internal static class AddressMapping
{
    public static void MapAddress(this ComplexPropertyBuilder<ShippingAddress> a, string prefix)
    {
        a.Property(x => x.City).HasColumnName($"{prefix}City").HasMaxLength(80);
        a.Property(x => x.Area).HasColumnName($"{prefix}Area").HasMaxLength(120);
        a.Property(x => x.Street).HasColumnName($"{prefix}Street").HasMaxLength(200);
        a.Property(x => x.Building).HasColumnName($"{prefix}Building").HasMaxLength(100);
        a.Property(x => x.Details).HasColumnName($"{prefix}Details").HasMaxLength(300);
    }
}
