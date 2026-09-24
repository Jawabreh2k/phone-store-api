using Microsoft.EntityFrameworkCore;
using Store.Application.Carts;
using Store.Application.Common;
using Store.Application.Dashboard;
using Store.Application.Orders;
using Store.Application.Settings;
using Store.Domain.Catalog;
using Store.Domain.Common;
using Store.Domain.Orders;

namespace Store.Application.Tests;

public sealed class AdminDashboardTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async Task InitializeAsync() =>
        _host = await TestHost.CreateAsync(clockStart: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private Task<Guid> VariantId(string sku) =>
        _host.Db(db => db.ProductVariants.Where(v => v.Sku == sku).Select(v => v.Id).SingleAsync());

    private static PlaceOrderCommand Checkout(CartOwner owner) => new(
        owner, "Sara Ahmad", "0791234567", null,
        new AddressInput("Amman", "Abdoun", "Street 5", "12", null), Note: null, SaveAddress: false);

    private async Task PlaceAtAsync(string sku)
    {
        var cart = await _host.Send<AddToCartCommand, CartDto>(new(new CartOwner(null, null), await VariantId(sku), 1));
        (await _host.Send<PlaceOrderCommand, OrderPlacedDto>(Checkout(new CartOwner(null, cart.Value.CartId)))).IsSuccess.ShouldBeTrue();
    }

    private async Task<AdminDashboardDto> Dashboard() =>
        (await _host.Query<GetAdminDashboardQuery, AdminDashboardDto>(new GetAdminDashboardQuery())).Value;

    [Fact]
    public async Task LocalDayBoundary_AsiaAmman_CountsTodayAndYesterday()
    {
        _host.Clock.SetUtcNow(new DateTimeOffset(2026, 9, 22, 20, 30, 0, TimeSpan.Zero));
        await PlaceAtAsync("GS24-128-BK");
        _host.Clock.SetUtcNow(new DateTimeOffset(2026, 9, 22, 22, 30, 0, TimeSpan.Zero));
        await PlaceAtAsync("GS24-128-BK");
        _host.Clock.SetUtcNow(new DateTimeOffset(2026, 9, 23, 9, 0, 0, TimeSpan.Zero));

        var dash = await Dashboard();
        dash.TodaySummary.Orders.ShouldBe(1);
        dash.TodaySummary.YesterdayOrders.ShouldBe(1);
    }

    [Fact]
    public async Task CancelledOrders_AreExcludedFromSalesCountsAndChart()
    {
        _host.User.UserId = Guid.NewGuid();
        _host.Clock.SetUtcNow(new DateTimeOffset(2026, 9, 23, 9, 0, 0, TimeSpan.Zero));
        await PlaceAtAsync("RN13-128-BK");

        var orderId = await _host.Db(db => db.Orders.OrderByDescending(o => o.PlacedAt).Select(o => o.Id).FirstAsync());
        (await _host.Send(new ChangeOrderStatusCommand(orderId, OrderStatus.Confirmed, null))).IsSuccess.ShouldBeTrue();
        (await _host.Send(new ChangeOrderStatusCommand(orderId, OrderStatus.Cancelled, "test"))).IsSuccess.ShouldBeTrue();

        var dash = await Dashboard();
        dash.TodaySummary.Orders.ShouldBe(0);
        dash.TodaySummary.Revenue.ShouldBeEmpty();
        dash.Last14Days[^1].Orders.ShouldBe(0);
        dash.TopSellers.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReturnedOrders_AreExcludedFromSalesCountsAndChart()
    {
        _host.User.UserId = Guid.NewGuid();
        _host.Clock.SetUtcNow(new DateTimeOffset(2026, 9, 23, 9, 0, 0, TimeSpan.Zero));
        await PlaceAtAsync("RN13-128-BK");

        var orderId = await _host.Db(db => db.Orders.OrderByDescending(o => o.PlacedAt).Select(o => o.Id).FirstAsync());
        (await _host.Send(new ChangeOrderStatusCommand(orderId, OrderStatus.Confirmed, null))).IsSuccess.ShouldBeTrue();
        (await _host.Send(new ChangeOrderStatusCommand(orderId, OrderStatus.Shipped, null))).IsSuccess.ShouldBeTrue();
        (await _host.Send(new ChangeOrderStatusCommand(orderId, OrderStatus.Delivered, null))).IsSuccess.ShouldBeTrue();
        (await _host.Send(new ChangeOrderStatusCommand(orderId, OrderStatus.Returned, "Defective"))).IsSuccess.ShouldBeTrue();

        var dash = await Dashboard();
        dash.TodaySummary.Orders.ShouldBe(0);
        dash.TodaySummary.Revenue.ShouldBeEmpty();
        dash.Last14Days[^1].Orders.ShouldBe(0);
        dash.TopSellers.ShouldBeEmpty();
    }

    [Fact]
    public async Task MultiCurrency_RevenueIsNotSummedAcrossCurrencies()
    {
        _host.Clock.SetUtcNow(new DateTimeOffset(2026, 9, 23, 9, 0, 0, TimeSpan.Zero));
        await PlaceAtAsync("ACC-ANK-30W-WH");

        (await _host.Send(new UpdateSettingsCommand(
            new LocalizedTextDto("Store", "متجر"), null, null, null, "USD", "962", "Asia/Amman", 0m, null, true))).IsSuccess.ShouldBeTrue();

        await PlaceAtAsync("ACC-ANK-30W-WH");

        var dash = await Dashboard();
        dash.TodaySummary.Revenue.Count.ShouldBe(2);
        dash.TodaySummary.Revenue[0].CurrencyCode.ShouldBe("USD");
        dash.TodaySummary.Revenue.ShouldContain(r => r.CurrencyCode == "JOD");
        dash.TodaySummary.Revenue.ShouldContain(r => r.CurrencyCode == "USD");
    }

    [Fact]
    public async Task Queue_CountsAndOldestPendingAt()
    {
        _host.User.UserId = Guid.NewGuid();
        _host.Clock.SetUtcNow(new DateTimeOffset(2026, 9, 23, 8, 0, 0, TimeSpan.Zero));
        await PlaceAtAsync("ACC-IP16P-GLASS");

        _host.Clock.Advance(TimeSpan.FromHours(1));
        await PlaceAtAsync("ACC-IP16P-GLASS");
        var second = await _host.Db(db => db.Orders.OrderByDescending(o => o.PlacedAt).Select(o => o.Id).FirstAsync());
        (await _host.Send(new ChangeOrderStatusCommand(second, OrderStatus.Confirmed, null))).IsSuccess.ShouldBeTrue();
        (await _host.Send(new ChangeOrderStatusCommand(second, OrderStatus.Shipped, null))).IsSuccess.ShouldBeTrue();

        var dash = await Dashboard();
        dash.Queue.PendingCount.ShouldBe(1);
        dash.Queue.ConfirmedCount.ShouldBe(0);
        dash.Queue.ShippedCount.ShouldBe(1);
        dash.Queue.OldestPendingAt.ShouldBe(new DateTimeOffset(2026, 9, 23, 8, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task LowStock_IncludesZeroAndThreshold_SortedAndCapped()
    {
        await using var host = await TestHost.CreateAsync(withSampleCatalog: false, clockStart: new DateTimeOffset(2026, 9, 23, 9, 0, 0, TimeSpan.Zero));
        await host.Db(async db =>
        {
            db.Brands.Add(new Brand("Test", "test"));
            db.Categories.Add(new Category(new LocalizedText("Phones", "هواتف"), "phones", ProductType.Phone));
            await db.SaveChangesAsync();

            var brand = await db.Brands.FirstAsync();
            var category = await db.Categories.FirstAsync();
            var product = new Product(ProductType.Phone, brand.Id, category.Id, new LocalizedText("Low stock test", "مخزون"), "low-stock-test", new LocalizedText("d", "د"));
            product.AddVariant("LOW-0", VariantOptions.None, 10m, null, 0);
            product.AddVariant("LOW-1", VariantOptions.None, 10m, null, 1);
            product.AddVariant("LOW-2A", VariantOptions.None, 10m, null, 2);
            product.AddVariant("LOW-2B", VariantOptions.None, 10m, null, 2);
            product.AddVariant("LOW-2C", VariantOptions.None, 10m, null, 2);
            product.AddVariant("LOW-3", VariantOptions.None, 10m, null, 3);
            product.AddVariant("LOW-4", VariantOptions.None, 10m, null, 4);
            product.AddVariant("LOW-5", VariantOptions.None, 10m, null, 5);
            product.AddVariant("LOW-6", VariantOptions.None, 10m, null, 6);
            var inactive = product.AddVariant("LOW-OFF", VariantOptions.None, 10m, null, 0).Value;
            product.UpdateVariant(inactive.Id, "LOW-OFF", VariantOptions.None, 10m, null, isActive: false);
            product.Publish();
            db.Products.Add(product);
            await db.SaveChangesAsync();
            return true;
        });

        var dash = (await host.Query<GetAdminDashboardQuery, AdminDashboardDto>(new GetAdminDashboardQuery())).Value;
        dash.LowStock.Count.ShouldBe(8);
        dash.LowStock[0].StockQuantity.ShouldBe(0);
        dash.LowStock[^1].StockQuantity.ShouldBe(5);
        dash.LowStock.ShouldNotContain(i => i.Sku == "LOW-6");
        dash.LowStock.ShouldNotContain(i => i.Sku == "LOW-OFF");
    }

    [Fact]
    public async Task Chart_Has14ZeroFilledDays_OldestFirstTodayLast()
    {
        _host.Clock.SetUtcNow(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        await PlaceAtAsync("ACC-BUDSFE-WH");

        var dash = await Dashboard();
        dash.Last14Days.Count.ShouldBe(14);
        dash.Last14Days[0].Day.ShouldBe(new DateOnly(2026, 9, 10));
        dash.Last14Days[^1].Day.ShouldBe(new DateOnly(2026, 9, 23));
        dash.Last14Days[^1].Orders.ShouldBe(1);
        dash.Last14Days[0].Orders.ShouldBe(0);
    }

    [Fact]
    public void DayBoundaries_SurviveDaylightSavingStartingAtMidnight()
    {
        // Like Cairo or Beirut: clocks jump from 00:00 to 01:00, so that local day has no midnight and only 23 hours.
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2020, 1, 1), new DateTime(2030, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 4, 24),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 10, 30));
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test/MidnightDst", TimeSpan.FromHours(2), "Midnight DST", "Standard", "Daylight", [rule]);

        var (start, end) = StoreCalendar.DayRangeUtc(new DateOnly(2026, 4, 24), zone);

        start.ShouldBe(new DateTimeOffset(2026, 4, 23, 22, 0, 0, TimeSpan.Zero)); // 01:00 local, the day's first real time
        end.ShouldBe(new DateTimeOffset(2026, 4, 24, 21, 0, 0, TimeSpan.Zero)); // next midnight, now at UTC+3
        (end - start).ShouldBe(TimeSpan.FromHours(23));
    }

    [Fact]
    public async Task Settings_InvalidTimeZoneRejected_ValidRoundTrips()
    {
        var bad = await _host.Send(new UpdateSettingsCommand(
            new LocalizedTextDto("Store", "متجر"), null, null, null, "JOD", "962", "Not/AZone", 0m, null, true));
        ((ValidationError)bad.Error).Errors.Keys.ShouldContain("timeZoneId");

        (await _host.Send(new UpdateSettingsCommand(
            new LocalizedTextDto("Store", "متجر"), null, null, null, "JOD", "962", "Asia/Dubai", 0m, null, true))).IsSuccess.ShouldBeTrue();

        var saved = (await _host.Query<GetAdminSettingsQuery, AdminSettingsDto>(new GetAdminSettingsQuery())).Value;
        saved.TimeZoneId.ShouldBe("Asia/Dubai");
    }
}
