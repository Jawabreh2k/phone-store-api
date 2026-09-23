using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Store.Application.Abstractions;
using Store.Application.Common;
using Store.Domain.Common;
using Store.Domain.Orders;

namespace Store.Application.Orders;

public sealed record GetMyOrdersQuery(Guid CustomerId, int? Page, int? PageSize) : IQuery<PagedResult<OrderSummaryDto>>;

internal sealed class GetMyOrdersHandler(IAppDbContext db) : IQueryHandler<GetMyOrdersQuery, PagedResult<OrderSummaryDto>>
{
    public async Task<Result<PagedResult<OrderSummaryDto>>> Handle(GetMyOrdersQuery query, CancellationToken cancellationToken) =>
        await db.Orders.AsNoTracking()
            .Where(o => o.CustomerId == query.CustomerId)
            .OrderByDescending(o => o.PlacedAt)
            .Select(o => new OrderSummaryDto(
                o.Id, o.OrderNumber, o.Status, o.PlacedAt,
                o.Items.Sum(i => i.Quantity), o.Total, o.CurrencyCode,
                o.Items.Select(i => i.ImageUrl).FirstOrDefault()))
            .ToPagedResultAsync(query.Page, query.PageSize, cancellationToken);
}

public sealed record GetMyOrderQuery(Guid CustomerId, string OrderNumber) : IQuery<OrderDetailsDto>;

internal sealed class GetMyOrderHandler(IAppDbContext db, ILanguageProvider language) : IQueryHandler<GetMyOrderQuery, OrderDetailsDto>
{
    public async Task<Result<OrderDetailsDto>> Handle(GetMyOrderQuery query, CancellationToken cancellationToken)
    {
        // Filtering by customer id means another customer's order number simply reads as "not found".
        var order = await OrderQueries.WithDetails(db)
            .FirstOrDefaultAsync(o => o.OrderNumber == query.OrderNumber && o.CustomerId == query.CustomerId, cancellationToken);

        return order is null
            ? OrderErrors.NotFound
            : OrderMapping.ToDetails(order, language.Current, customerCanCancel: true);
    }
}

/// <summary>
/// Guest order tracking by the random token handed out at checkout. The sequential order number
/// plus a phone number is not a credential: both are guessable, and the response contains an address.
/// </summary>
public sealed record TrackOrderQuery(string Token) : IQuery<OrderDetailsDto>;

public sealed class TrackOrderValidator : AbstractValidator<TrackOrderQuery>
{
    public TrackOrderValidator() =>
        RuleFor(x => x.Token).Must(OrderTrackingToken.LooksValid).WithMessage("Invalid tracking link.");
}

internal sealed class TrackOrderHandler(IAppDbContext db, ILanguageProvider language) : IQueryHandler<TrackOrderQuery, OrderDetailsDto>
{
    public async Task<Result<OrderDetailsDto>> Handle(TrackOrderQuery query, CancellationToken cancellationToken)
    {
        var hash = OrderTrackingToken.Hash(query.Token);
        var order = await OrderQueries.WithDetails(db)
            .FirstOrDefaultAsync(o => o.TrackingTokenHash == hash, cancellationToken);

        // Tracking is read-only: cancelling needs an account, or a call to the store.
        return order is null
            ? OrderErrors.NotFound
            : OrderMapping.ToDetails(order, language.Current, customerCanCancel: false);
    }
}

public sealed record CancelMyOrderCommand(Guid CustomerId, string OrderNumber, string? Reason) : ICommand;

public sealed class CancelMyOrderValidator : AbstractValidator<CancelMyOrderCommand>
{
    public CancelMyOrderValidator() => RuleFor(x => x.Reason).MaximumLength(500);
}

internal sealed class CancelMyOrderHandler(IAppDbContext db, TimeProvider clock) : ICommandHandler<CancelMyOrderCommand>
{
    public async Task<Result> Handle(CancelMyOrderCommand command, CancellationToken cancellationToken)
    {
        var order = await db.Orders.Include(o => o.Items).Include(o => o.History)
            .FirstOrDefaultAsync(o => o.OrderNumber == command.OrderNumber && o.CustomerId == command.CustomerId, cancellationToken);
        if (order is null)
        {
            return OrderErrors.NotFound;
        }

        var result = order.Cancel(command.CustomerId, byCustomer: true, command.Reason, clock.GetUtcNow());
        if (result.IsFailure)
        {
            return result;
        }

        await StockRelease.ReleaseAsync(db, order, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

internal static class OrderQueries
{
    public static IQueryable<Order> WithDetails(IAppDbContext db) =>
        db.Orders.AsNoTracking().Include(o => o.Items).Include(o => o.History);
}

internal static class StockRelease
{
    /// <summary>Puts a cancelled order's units back on sale. Products deleted since then are skipped.</summary>
    public static async Task ReleaseAsync(IAppDbContext db, Order order, CancellationToken cancellationToken)
    {
        var productIds = order.Items.Select(i => i.ProductId).Distinct().ToList();
        var products = await db.Products.Include(p => p.Variants)
            .Where(p => productIds.Contains(p.Id))
            .ToListAsync(cancellationToken);

        foreach (var item in order.Items)
        {
            products.Find(p => p.Id == item.ProductId)?.ReleaseStock(item.VariantId, item.Quantity);
        }
    }
}
