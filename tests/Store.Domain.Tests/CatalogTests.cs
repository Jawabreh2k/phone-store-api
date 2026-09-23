using Store.Domain.Catalog;
using Store.Domain.Common;

namespace Store.Domain.Tests;

public class CatalogTests
{
    private static Product NewPhone() =>
        new(ProductType.Phone, Guid.NewGuid(), Guid.NewGuid(), new("Phone", "هاتف"), "phone", new("Desc", "وصف"));

    [Fact]
    public void Publish_WithoutActiveVariants_Fails()
    {
        var product = NewPhone();

        var result = product.Publish();

        result.Error.ShouldBe(CatalogErrors.CannotPublishWithoutVariants);
        product.IsPublished.ShouldBeFalse();
    }

    [Fact]
    public void DeactivatingLastActiveVariant_UnpublishesProduct()
    {
        var product = NewPhone();
        var variant = product.AddVariant("sku-1", VariantOptions.None, 100m, null, 5).Value;
        product.Publish().IsSuccess.ShouldBeTrue();

        product.UpdateVariant(variant.Id, "SKU-1", VariantOptions.None, 100m, null, isActive: false).IsSuccess.ShouldBeTrue();

        product.IsPublished.ShouldBeFalse();
    }

    [Fact]
    public void AddVariant_NormalizesSkuAndRejectsDuplicates()
    {
        var product = NewPhone();

        product.AddVariant(" ab-1 ", VariantOptions.None, 10m, null, 1).Value.Sku.ShouldBe("AB-1");
        product.AddVariant("AB-1", VariantOptions.None, 10m, null, 1).Error.ShouldBe(CatalogErrors.SkuTaken);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(100, 100)]
    [InlineData(100, 90)]
    public void AddVariant_RejectsInvalidPrices(decimal price, int? compareAt)
    {
        var result = NewPhone().AddVariant("X", VariantOptions.None, price, compareAt, 1);

        result.Error.ShouldBe(CatalogErrors.InvalidPrice);
    }

    [Fact]
    public void ReserveStock_BeyondAvailable_FailsWithoutChangingStock()
    {
        var product = NewPhone();
        var variant = product.AddVariant("X", VariantOptions.None, 10m, null, 2).Value;

        var result = product.ReserveStock(variant.Id, 3);

        result.Error.Code.ShouldBe("catalog.insufficient_stock");
        variant.StockQuantity.ShouldBe(2);
    }

    [Fact]
    public void ReserveThenRelease_RestoresStock()
    {
        var product = NewPhone();
        var variant = product.AddVariant("X", VariantOptions.None, 10m, null, 5).Value;

        product.ReserveStock(variant.Id, 3).IsSuccess.ShouldBeTrue();
        variant.StockQuantity.ShouldBe(2);

        product.ReleaseStock(variant.Id, 3);
        variant.StockQuantity.ShouldBe(5);
    }

    [Fact]
    public void AdjustStock_CannotGoNegative()
    {
        var product = NewPhone();
        var variant = product.AddVariant("X", VariantOptions.None, 10m, null, 1).Value;

        product.AdjustStock(variant.Id, -2).Error.ShouldBe(CatalogErrors.InvalidStockAdjustment);
        variant.StockQuantity.ShouldBe(1);
    }

    [Fact]
    public void SetCompatiblePhones_OnlyForAccessories()
    {
        NewPhone().SetCompatiblePhones([Guid.NewGuid()]).Error.ShouldBe(CatalogErrors.CompatibilityOnlyForAccessories);
    }

    [Fact]
    public void SetCompatiblePhones_DiffsAgainstExistingLinks()
    {
        var accessory = new Product(ProductType.Accessory, Guid.NewGuid(), Guid.NewGuid(), new("Case", "غطاء"), "case", new("d", "د"));
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        accessory.SetCompatiblePhones([a, b]);
        var keptLink = accessory.CompatibleWith.Single(x => x.PhoneProductId == b);

        accessory.SetCompatiblePhones([b, c]);

        accessory.CompatibleWith.Select(x => x.PhoneProductId).ShouldBe([b, c], ignoreOrder: true);
        accessory.CompatibleWith.ShouldContain(keptLink); // same instance, not re-created
    }

    [Fact]
    public void ReorderImages_AppliesOrderAndRenumbers()
    {
        var product = NewPhone();
        var first = product.AddImage("/1.jpg").Value;
        var second = product.AddImage("/2.jpg").Value;
        var third = product.AddImage("/3.jpg").Value;

        product.ReorderImages([third.Id, first.Id]);

        product.Images.ShouldBe([third, first, second]);
        product.Images.Select(i => i.SortOrder).ShouldBe([0, 1, 2]);
    }

    [Theory]
    [InlineData("iPhone 16 Pro", "iphone-16-pro")]
    [InlineData("  Galaxy  S24+ Ultra ", "galaxy-s24-ultra")]
    [InlineData("Café 5G/LTE", "cafe-5g-lte")]
    [InlineData("آيفون", "")]
    public void Slug_From_ProducesUrlSafeValues(string input, string expected)
    {
        Slug.From(input).ShouldBe(expected);
    }

    [Fact]
    public void VariantOptions_Describe_FormatsStorageAndColor()
    {
        var options = new VariantOptions(new LocalizedText("Black", "أسود"), "#000000", 1024, 8);

        options.Describe(Language.En).ShouldBe("1 TB · 8 GB RAM · Black");
        options.Describe(Language.Ar).ShouldBe("1 TB · 8 GB رام · أسود");
    }
}
