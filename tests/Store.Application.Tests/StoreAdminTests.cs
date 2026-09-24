using Microsoft.EntityFrameworkCore;
using Store.Application.Catalog.Admin;
using Store.Application.Common;
using Store.Application.Settings;
using Store.Domain.Catalog;
using Store.Domain.Common;

namespace Store.Application.Tests;

/// <summary>Brands, categories and store settings as the admin screens use them.</summary>
public sealed class StoreAdminTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async Task InitializeAsync() => _host = await TestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<IReadOnlyList<AdminBrandDto>> Brands() =>
        (await _host.Query<GetAdminBrandsQuery, IReadOnlyList<AdminBrandDto>>(new GetAdminBrandsQuery())).Value;

    private async Task<IReadOnlyList<AdminCategoryDto>> Categories() =>
        (await _host.Query<GetAdminCategoriesQuery, IReadOnlyList<AdminCategoryDto>>(new GetAdminCategoriesQuery())).Value;

    [Fact]
    public async Task Brands_CountProducts_Reorder_AndDeleteOnlyWhenUnused()
    {
        var created = await _host.Send<SaveBrandCommand, Guid>(new(null, "Oppo", null, null, 0, true));
        var before = await Brands();
        before.Single(b => b.Slug == "oppo").ProductCount.ShouldBe(0);
        before.Single(b => b.Slug == "apple").ProductCount.ShouldBeGreaterThan(0);

        var order = before.Select(b => b.Id).Where(id => id != created.Value).Prepend(created.Value).ToList();
        (await _host.Send(new ReorderBrandsCommand(order))).IsSuccess.ShouldBeTrue();
        (await Brands()).Select(b => b.Id).ShouldBe(order);

        var apple = before.Single(b => b.Slug == "apple").Id;
        (await _host.Send(new DeleteBrandCommand(apple))).Error.ShouldBe(CatalogErrors.BrandInUse);
        (await _host.Send(new DeleteBrandCommand(created.Value))).IsSuccess.ShouldBeTrue();
        (await Brands()).ShouldNotContain(b => b.Id == created.Value);
    }

    [Fact]
    public async Task Categories_ReorderWithinOneType_AndDeleteOnlyWhenUnused()
    {
        var all = await Categories();
        var accessories = all.Where(c => c.ProductType == ProductType.Accessory).Select(c => c.Id).Reverse().ToList();

        (await _host.Send(new ReorderCategoriesCommand(accessories))).IsSuccess.ShouldBeTrue();
        (await Categories()).Where(c => c.ProductType == ProductType.Accessory).Select(c => c.Id).ShouldBe(accessories);

        // Each type is ordered on its own, so one list can't mix them.
        var smartphones = all.Single(c => c.Slug == "smartphones").Id;
        (await _host.Send(new ReorderCategoriesCommand([smartphones, accessories[0]]))).Error.ShouldBe(CatalogErrors.CategoryTypeMismatch);

        (await _host.Send(new DeleteCategoryCommand(smartphones))).Error.ShouldBe(CatalogErrors.CategoryInUse);
        var empty = await _host.Send<SaveCategoryCommand, Guid>(new(null, new("Tablets", "الأجهزة اللوحية"), null, ProductType.Phone, 0, false));
        (await _host.Send(new DeleteCategoryCommand(empty.Value))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Settings_NormalizePhones_AndReportABadOneByField()
    {
        var update = new UpdateSettingsCommand(
            new LocalizedTextDto("Jawwal Store", "متجر جوال"), "079 123 4567", "00962791234567", " help@store.jo ",
            "jod", "962", 2.5m, 75m, AllowGuestCheckout: true);

        (await _host.Send(update)).IsSuccess.ShouldBeTrue();
        var saved = (await _host.Query<GetAdminSettingsQuery, AdminSettingsDto>(new GetAdminSettingsQuery())).Value;
        saved.SupportPhone.ShouldBe("+962791234567");
        saved.WhatsAppNumber.ShouldBe("+962791234567");
        saved.SupportEmail.ShouldBe("help@store.jo");
        saved.CurrencyCode.ShouldBe("JOD");

        var bad = await _host.Send(update with { WhatsAppNumber = "12" });
        ((ValidationError)bad.Error).Errors.Keys.ShouldContain("whatsAppNumber");
    }

    [Fact]
    public async Task Logo_UploadReplacesAndDeletesThePreviousFile_RemoveClearsIt()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

        using var first = new MemoryStream(png);
        var one = await _host.Send<UploadStoreLogoCommand, string>(new(first, first.Length));
        using var second = new MemoryStream(png);
        var two = await _host.Send<UploadStoreLogoCommand, string>(new(second, second.Length));

        _host.Files.Deleted.ShouldBe([one.Value]);
        (await _host.Db(db => db.StoreSettings.Select(s => s.LogoUrl).SingleAsync())).ShouldBe(two.Value);

        // Saving the rest of the settings leaves the logo alone.
        (await _host.Send(new UpdateSettingsCommand(new LocalizedTextDto("A", "ب"), null, null, null, "JOD", "962", 0m, null, true))).IsSuccess.ShouldBeTrue();
        (await _host.Db(db => db.StoreSettings.Select(s => s.LogoUrl).SingleAsync())).ShouldBe(two.Value);

        (await _host.Send(new RemoveStoreLogoCommand())).IsSuccess.ShouldBeTrue();
        _host.Files.Deleted.ShouldBe([one.Value, two.Value]);
        (await _host.Db(db => db.StoreSettings.Select(s => s.LogoUrl).SingleAsync())).ShouldBeNull();
    }
}
