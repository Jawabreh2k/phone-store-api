using Microsoft.EntityFrameworkCore;
using Store.Application.Abstractions;
using Store.Application.Catalog.Admin;
using Store.Application.Common;
using Store.Application.Orders;
using Store.Domain.Common;
using Store.Domain.Orders;

namespace Store.Application.Dashboard;

public sealed record GetAdminDashboardQuery : IQuery<AdminDashboardDto>;

/// <summary>
/// Booked order value placed in the period, excluding cancelled orders.
/// Cash on delivery: this is order totals, not cash collected.
/// Amounts are never summed across currencies.
/// </summary>
public sealed record CurrencyAmountDto(string CurrencyCode, decimal Amount);

public sealed record TodaySummaryDto(
    int Orders,
    int YesterdayOrders,
    IReadOnlyList<CurrencyAmountDto> Revenue,
    IReadOnlyList<CurrencyAmountDto> YesterdayRevenue);

public sealed record OrderQueueDto(
    int PendingCount,
    DateTimeOffset? OldestPendingAt,
    int ConfirmedCount,
    int ShippedCount);

public sealed record DailySalesDto(DateOnly Day, int Orders, decimal Revenue);

public sealed record LowStockItemDto(
    Guid ProductId,
    Guid VariantId,
    LocalizedTextDto ProductName,
    LocalizedTextDto VariantDescription,
    string Sku,
    int StockQuantity,
    string? ImageUrl);

public sealed record TopSellerDto(
    Guid ProductId,
    LocalizedTextDto ProductName,
    int Units,
    decimal Revenue,
    string? ImageUrl);

public sealed record CatalogHealthDto(int Drafts, int PublishedWithoutPhotos, int PublishedOutOfStock);

public sealed record AdminDashboardDto(
    DateOnly Today,
    string TimeZoneId,
    string StoreCurrencyCode,
    TodaySummaryDto TodaySummary,
    OrderQueueDto Queue,
    IReadOnlyList<DailySalesDto> Last14Days,
    IReadOnlyList<LowStockItemDto> LowStock,
    IReadOnlyList<TopSellerDto> TopSellers,
    IReadOnlyList<AdminOrderListItemDto> RecentOrders,
    CatalogHealthDto Catalog);

internal sealed class GetAdminDashboardHandler(IAppDbContext db, TimeProvider clock)
    : IQueryHandler<GetAdminDashboardQuery, AdminDashboardDto>
{
    private sealed record OrderRow(DateTimeOffset PlacedAt, decimal Total, string CurrencyCode);

    private sealed record ItemRow(
        DateTimeOffset PlacedAt,
        string CurrencyCode,
        Guid ProductId,
        string ProductNameEn,
        string ProductNameAr,
        string? ImageUrl,
        int Quantity,
        decimal LineTotal);

    public async Task<Result<AdminDashboardDto>> Handle(GetAdminDashboardQuery query, CancellationToken cancellationToken)
    {
        var settings = await db.StoreSettings.AsNoTracking()
            .Select(s => new { s.TimeZoneId, s.CurrencyCode })
            .FirstAsync(cancellationToken);

        var tz = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var today = StoreCalendar.Today(clock.GetUtcNow(), tz);
        var yesterday = today.AddDays(-1);
        var chartStart = today.AddDays(-13);

        var todayRange = StoreCalendar.DayRangeUtc(today, tz);
        var yesterdayRange = StoreCalendar.DayRangeUtc(yesterday, tz);
        var chartRangeStart = StoreCalendar.DayStartUtc(chartStart, tz);
        var topSellerRangeStart = StoreCalendar.DayStartUtc(today.AddDays(-29), tz);

        var salesWindowStart = topSellerRangeStart < chartRangeStart ? topSellerRangeStart : chartRangeStart;
        var salesOrders = await db.Orders.AsNoTracking()
            .Where(o => o.PlacedAt >= salesWindowStart && o.Status != OrderStatus.Cancelled)
            .Select(o => new OrderRow(o.PlacedAt, o.Total, o.CurrencyCode))
            .ToListAsync(cancellationToken);

        var todayOrders = salesOrders.Where(o => o.PlacedAt >= todayRange.StartUtc && o.PlacedAt < todayRange.EndUtc).ToList();
        var yesterdayOrders = salesOrders.Where(o => o.PlacedAt >= yesterdayRange.StartUtc && o.PlacedAt < yesterdayRange.EndUtc).ToList();

        var todaySummary = new TodaySummaryDto(
            todayOrders.Count,
            yesterdayOrders.Count,
            ToRevenueList(todayOrders, settings.CurrencyCode),
            ToRevenueList(yesterdayOrders, settings.CurrencyCode));

        var chartOrders = salesOrders.Where(o => o.PlacedAt >= chartRangeStart).ToList();
        var last14Days = Enumerable.Range(0, 14)
            .Select(i => chartStart.AddDays(i))
            .Select(day =>
            {
                var range = StoreCalendar.DayRangeUtc(day, tz);
                var dayOrders = chartOrders.Where(o => o.PlacedAt >= range.StartUtc && o.PlacedAt < range.EndUtc).ToList();
                var revenue = dayOrders.Where(o => o.CurrencyCode == settings.CurrencyCode).Sum(o => o.Total);
                return new DailySalesDto(day, dayOrders.Count, revenue);
            })
            .ToList();

        var pendingCount = await db.Orders.AsNoTracking().CountAsync(o => o.Status == OrderStatus.Pending, cancellationToken);
        var confirmedCount = await db.Orders.AsNoTracking().CountAsync(o => o.Status == OrderStatus.Confirmed, cancellationToken);
        var shippedCount = await db.Orders.AsNoTracking().CountAsync(o => o.Status == OrderStatus.Shipped, cancellationToken);
        var oldestPending = pendingCount == 0
            ? null
            : await db.Orders.AsNoTracking()
                .Where(o => o.Status == OrderStatus.Pending)
                .MinAsync(o => (DateTimeOffset?)o.PlacedAt, cancellationToken);

        var queue = new OrderQueueDto(pendingCount, oldestPending, confirmedCount, shippedCount);

        // Only the item columns ranking needs, not whole orders with their contact and address. The two-argument
        // SelectMany becomes a plain JOIN (a nested Select would need APPLY, which SQLite can't run).
        var itemRows = await db.Orders.AsNoTracking()
            .Where(o => o.PlacedAt >= topSellerRangeStart && o.Status != OrderStatus.Cancelled)
            .SelectMany(o => o.Items, (o, i) => new ItemRow(
                o.PlacedAt,
                o.CurrencyCode,
                i.ProductId,
                i.ProductName.En,
                i.ProductName.Ar,
                i.ImageUrl,
                i.Quantity,
                i.LineTotal))
            .ToListAsync(cancellationToken);

        var topSellers = itemRows
            .GroupBy(i => i.ProductId)
            .Select(g =>
            {
                var latest = g.OrderByDescending(i => i.PlacedAt).First();
                return new TopSellerDto(
                    g.Key,
                    new LocalizedTextDto(latest.ProductNameEn, latest.ProductNameAr),
                    g.Sum(i => i.Quantity),
                    g.Where(i => i.CurrencyCode == settings.CurrencyCode).Sum(i => i.LineTotal),
                    g.Where(i => i.ImageUrl != null).OrderByDescending(i => i.PlacedAt).Select(i => i.ImageUrl).FirstOrDefault());
            })
            .OrderByDescending(t => t.Units)
            .Take(5)
            .ToList();

        var lowStockVariants = await db.ProductVariants.AsNoTracking()
            .Where(v => v.IsActive && v.StockQuantity <= GetAdminProductsHandler.LowStockThreshold)
            .OrderBy(v => v.StockQuantity)
            .ThenBy(v => v.Sku)
            .Take(8)
            .ToListAsync(cancellationToken);

        var lowStockProductIds = lowStockVariants.Select(v => v.ProductId).Distinct().ToList();
        var lowStockProducts = await db.Products.AsNoTracking()
            .Where(p => lowStockProductIds.Contains(p.Id))
            .Select(p => new
            {
                p.Id,
                p.Name,
                ProductImage = p.Images.OrderBy(i => i.SortOrder).Select(i => i.Url).FirstOrDefault(),
                VariantImages = p.Images.Where(i => i.VariantId != null).Select(i => new { i.VariantId, i.Url }).ToList(),
            })
            .ToDictionaryAsync(p => p.Id, cancellationToken);

        var lowStock = lowStockVariants.Select(v =>
        {
            var product = lowStockProducts[v.ProductId];
            var variantImage = product.VariantImages.FirstOrDefault(i => i.VariantId == v.Id)?.Url;
            var options = v.Options;
            return new LowStockItemDto(
                v.ProductId,
                v.Id,
                LocalizedTextDto.From(product.Name),
                new LocalizedTextDto(options.Describe(Language.En), options.Describe(Language.Ar)),
                v.Sku,
                v.StockQuantity,
                variantImage ?? product.ProductImage);
        }).ToList();

        var recentOrders = await db.Orders.AsNoTracking()
            .OrderByDescending(o => o.PlacedAt)
            .Take(5)
            .Select(o => new AdminOrderListItemDto(
                o.Id, o.OrderNumber, o.Status, o.PaymentStatus, o.PlacedAt,
                o.Contact.FullName, o.Contact.Phone, o.ShippingAddress.City,
                o.Items.Sum(i => i.Quantity), o.Total, o.CurrencyCode, o.CustomerId == null))
            .ToListAsync(cancellationToken);

        var drafts = await db.Products.AsNoTracking().CountAsync(p => !p.IsPublished, cancellationToken);
        var publishedWithoutPhotos = await db.Products.AsNoTracking()
            .CountAsync(p => p.IsPublished && !p.Images.Any(), cancellationToken);
        var publishedOutOfStock = await db.Products.AsNoTracking()
            .CountAsync(p => p.IsPublished && !p.Variants.Any(v => v.IsActive && v.StockQuantity > 0), cancellationToken);

        return new AdminDashboardDto(
            today,
            settings.TimeZoneId,
            settings.CurrencyCode,
            todaySummary,
            queue,
            last14Days,
            lowStock,
            topSellers,
            recentOrders,
            new CatalogHealthDto(drafts, publishedWithoutPhotos, publishedOutOfStock));
    }

    private static List<CurrencyAmountDto> ToRevenueList(IEnumerable<OrderRow> orders, string storeCurrency) =>
        orders
            .GroupBy(o => o.CurrencyCode)
            .Select(g => new CurrencyAmountDto(g.Key, g.Sum(o => o.Total)))
            .OrderBy(r => r.CurrencyCode == storeCurrency ? 0 : 1)
            .ThenBy(r => r.CurrencyCode)
            .ToList();
}

/// <summary>The store's local calendar days, as UTC ranges the database can filter on.</summary>
internal static class StoreCalendar
{
    public static DateOnly Today(DateTimeOffset utcNow, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, zone).DateTime);

    /// <summary>
    /// The first instant of a local day. Where daylight saving starts at midnight (Cairo, Beirut) that day has no
    /// 00:00, and converting it would throw, so the day starts at its first local time that exists.
    /// </summary>
    public static DateTimeOffset DayStartUtc(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(15);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
    }

    /// <summary>[start, end) of a local day; 23 or 25 hours long on daylight-saving changes.</summary>
    public static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) DayRangeUtc(DateOnly day, TimeZoneInfo zone) =>
        (DayStartUtc(day, zone), DayStartUtc(day.AddDays(1), zone));
}
