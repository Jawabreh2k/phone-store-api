using Microsoft.EntityFrameworkCore;
using Store.Application.Catalog.Admin;
using Store.Application.Catalog.Storefront;
using Store.Application.Common;
using Store.Domain.Catalog;
using Store.Domain.Common;

namespace Store.Application.Tests;

public sealed class CatalogTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async Task InitializeAsync() => _host = await TestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private Task<Result<PagedResult<ProductSummaryDto>>> Search(SearchProductsQuery query) =>
        _host.Query<SearchProductsQuery, PagedResult<ProductSummaryDto>>(query);

    [Fact]
    public async Task Search_FiltersByTypeAndBrand_WithFromPrice()
    {
        var phones = await Search(new SearchProductsQuery(Type: ProductType.Phone, Sort: ProductSort.PriceAsc));

        phones.Value.Items.Select(p => p.Slug).ShouldBe(["redmi-note-13", "galaxy-s24", "iphone-16-pro"]);
        phones.Value.Items[^1].Price.ShouldBe(899m);
        phones.Value.Items.Single(p => p.Slug == "galaxy-s24").CompareAtPrice.ShouldBe(749m);

        var samsung = await Search(new SearchProductsQuery(Brand: "samsung"));
        samsung.Value.TotalCount.ShouldBe(3);
    }

    [Fact]
    public async Task Search_FlagsProductsWhosePriceIsAFromPrice()
    {
        var all = (await Search(new SearchProductsQuery(PageSize: 50))).Value.Items.ToDictionary(p => p.Slug);

        all["iphone-16-pro"].HasPriceRange.ShouldBeTrue();          // 899 … 1199 across storage
        all["iphone-16-pro-silicone-case"].HasPriceRange.ShouldBeFalse(); // both colours 39
        all["iphone-16-pro-tempered-glass"].HasPriceRange.ShouldBeFalse(); // single variant
    }

    [Fact]
    public async Task Search_MatchesArabicNames_AndReturnsArabicText()
    {
        _host.Language.Current = Language.Ar;

        var result = await Search(new SearchProductsQuery(Search: "آيفون"));

        result.Value.Items.Select(p => p.Name).ShouldContain("آيفون 16 برو");
    }

    [Fact]
    public async Task Search_CompatibleWith_ListsAccessoriesForAPhone()
    {
        var result = await Search(new SearchProductsQuery(CompatibleWith: "iphone-16-pro"));

        result.Value.Items.Select(p => p.Slug).ShouldBe(
            ["iphone-16-pro-silicone-case", "iphone-16-pro-tempered-glass", "anker-nano-30w"], ignoreOrder: true);
    }

    [Fact]
    public async Task ProductDetails_HideExactStock_AndIncludeRelatedAccessories()
    {
        var result = await _host.Query<GetProductBySlugQuery, ProductDetailsDto>(new GetProductBySlugQuery("iphone-16-pro"));

        var details = result.Value;
        details.Specifications.Count.ShouldBe(4);
        details.Variants.Single(v => v.Sku == "IP16P-128-TI").LowStockQuantity.ShouldBeNull(); // 12 in stock
        details.Variants.Single(v => v.Sku == "IP16P-256-BK").LowStockQuantity.ShouldBe(3);
        details.Variants.Single(v => v.Sku == "IP16P-512-BK").InStock.ShouldBeFalse();
        details.Related.Count.ShouldBe(3);
    }

    [Fact]
    public async Task UnpublishedProducts_AreHiddenFromStorefront()
    {
        var id = await _host.Db(db => db.Products.Where(p => p.Slug == "galaxy-s24").Select(p => p.Id).SingleAsync());

        (await _host.Send(new SetProductPublishedCommand(id, Published: false))).IsSuccess.ShouldBeTrue();

        (await _host.Query<GetProductBySlugQuery, ProductDetailsDto>(new GetProductBySlugQuery("galaxy-s24"))).Error.ShouldBe(CatalogErrors.ProductNotFound);
        (await Search(new SearchProductsQuery(Brand: "samsung"))).Value.TotalCount.ShouldBe(2);
    }

    [Fact]
    public async Task AdminCreatesProduct_WithVariantsSpecsAndCompatibility()
    {
        var brands = await _host.Query<GetAdminBrandsQuery, IReadOnlyList<AdminBrandDto>>(new GetAdminBrandsQuery());
        var categories = await _host.Query<GetAdminCategoriesQuery, IReadOnlyList<AdminCategoryDto>>(new GetAdminCategoriesQuery());
        var anker = brands.Value.Single(b => b.Slug == "anker").Id;
        var chargers = categories.Value.Single(c => c.Slug == "chargers").Id;
        var smartphones = categories.Value.Single(c => c.Slug == "smartphones").Id;

        // A category from the wrong product type is rejected.
        var mismatch = await _host.Send<SaveProductCommand, Guid>(new(null, ProductType.Accessory, anker, smartphones,
            new("Cable", "كابل"), null, new("d", "د"), false));
        mismatch.Error.ShouldBe(CatalogErrors.CategoryTypeMismatch);

        var created = await _host.Send<SaveProductCommand, Guid>(new(null, ProductType.Accessory, anker, chargers,
            new("USB-C Cable 1m", "كابل USB-C متر"), null, new("Braided", "مجدول"), false));
        created.IsSuccess.ShouldBeTrue();
        var id = created.Value;

        (await _host.Send<AddVariantCommand, Guid>(new(id, new VariantInput("cbl-1m", null, null, null, null, 7m, null), 50))).IsSuccess.ShouldBeTrue();
        (await _host.Send<AddVariantCommand, Guid>(new(id, new VariantInput("IP16P-128-TI", null, null, null, null, 7m, null), 5)))
            .Error.ShouldBe(CatalogErrors.SkuTaken); // SKUs are unique across products

        await _host.Send(new ReplaceSpecificationsCommand(id, [new(new("Length", "الطول"), new("1 m", "1 متر"))]));
        var phoneIds = await _host.Db(db => db.Products.Where(p => p.Type == ProductType.Phone).Select(p => p.Id).ToListAsync());
        (await _host.Send(new SetCompatiblePhonesCommand(id, phoneIds))).IsSuccess.ShouldBeTrue();
        (await _host.Send(new SetCompatiblePhonesCommand(id, phoneIds.Take(1).ToList()))).IsSuccess.ShouldBeTrue();
        (await _host.Send(new SetProductPublishedCommand(id, true))).IsSuccess.ShouldBeTrue();

        var product = await _host.Query<GetAdminProductQuery, AdminProductDto>(new GetAdminProductQuery(id));
        product.Value.Slug.ShouldBe("usb-c-cable-1m");
        product.Value.Variants.Single().Sku.ShouldBe("CBL-1M");
        product.Value.Specifications.Single().Value.Ar.ShouldBe("1 متر");
        product.Value.CompatiblePhoneIds.ShouldBe([phoneIds[0]]);
        product.Value.IsPublished.ShouldBeTrue();
    }

    [Fact]
    public async Task AdminList_FlagsLowStock_ByOption_MatchingTheLowStockFilter()
    {
        var all = await _host.Query<GetAdminProductsQuery, PagedResult<AdminProductListItemDto>>(new GetAdminProductsQuery(PageSize: 50));
        var low = await _host.Query<GetAdminProductsQuery, PagedResult<AdminProductListItemDto>>(new GetAdminProductsQuery(LowStockOnly: true, PageSize: 50));

        // The badge and the filter apply one rule, so the list never shows a badge the filter disagrees with.
        all.Value.Items.Where(p => p.HasLowStock).Select(p => p.Id).ShouldBe(low.Value.Items.Select(p => p.Id), ignoreOrder: true);

        var iphone = all.Value.Items.Single(p => p.Slug == "iphone-16-pro");
        iphone.HasLowStock.ShouldBeTrue(); // 256 GB Black has 3 left…
        iphone.TotalStock.ShouldBeGreaterThan(5); // …though the product as a whole has plenty
    }

    [Fact]
    public async Task ImageUpload_RejectsNonImages_AndStoresValidOnes()
    {
        var id = await _host.Db(db => db.Products.Where(p => p.Slug == "redmi-note-13").Select(p => p.Id).SingleAsync());

        using var text = new MemoryStream("hello world, not an image"u8.ToArray());
        var rejected = await _host.Send<UploadProductImageCommand, ImageDto>(new(id, text, text.Length, null));
        rejected.Error.ShouldBe(ImageFile.UnsupportedType);

        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
        using var image = new MemoryStream(png);
        var uploaded = await _host.Send<UploadProductImageCommand, ImageDto>(new(id, image, image.Length, null));

        uploaded.Value.Url.ShouldEndWith(".png");
        (await Search(new SearchProductsQuery(Brand: "xiaomi"))).Value.Items.Single().ImageUrl.ShouldBe(uploaded.Value.Url);
    }
}
