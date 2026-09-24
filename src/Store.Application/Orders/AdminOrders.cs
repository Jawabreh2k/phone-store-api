using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Store.Application.Abstractions;
using Store.Application.Common;
using Store.Domain.Common;
using Store.Domain.Orders;

namespace Store.Application.Orders;

public sealed record GetPendingOrderCountQuery : IQuery<int>;

internal sealed class GetPendingOrderCountHandler(IAppDbContext db) : IQueryHandler<GetPendingOrderCountQuery, int>
{
    public async Task<Result<int>> Handle(GetPendingOrderCountQuery query, CancellationToken cancellationToken) =>
        await db.Orders.AsNoTracking().CountAsync(o => o.Status == OrderStatus.Pending, cancellationToken);
}

public sealed record GetAdminOrdersQuery(
    OrderStatus? Status = null,
    string? Search = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int? Page = null,
    int? PageSize = null) : IQuery<PagedResult<AdminOrderListItemDto>>;

internal sealed class GetAdminOrdersHandler(IAppDbContext db) : IQueryHandler<GetAdminOrdersQuery, PagedResult<AdminOrderListItemDto>>
{
    public async Task<Result<PagedResult<AdminOrderListItemDto>>> Handle(GetAdminOrdersQuery q, CancellationToken cancellationToken)
    {
        var orders = db.Orders.AsNoTracking();

        if (q.Status is { } status)
        {
            orders = orders.Where(o => o.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = q.Search.Trim();

            // Phones are stored as E.164, so match on the significant digits: "079 123 4567" and "+962 079…" both find "+962791234567".
            var countryCode = await db.StoreSettings.AsNoTracking()
                .Select(s => s.PhoneCountryCode)
                .FirstAsync(cancellationToken);
            var phoneDigits = PhoneNumber.SearchDigits(term, countryCode);
            orders = orders.Where(o =>
                o.OrderNumber == term ||
                o.Contact.FullName.Contains(term) ||
                (phoneDigits != null && o.Contact.Phone.Contains(phoneDigits)));
        }

        if (q.From is { } from)
        {
            orders = orders.Where(o => o.PlacedAt >= from);
        }

        if (q.To is { } to)
        {
            orders = orders.Where(o => o.PlacedAt < to);
        }

        return await orders
            .OrderByDescending(o => o.PlacedAt)
            .Select(o => new AdminOrderListItemDto(
                o.Id, o.OrderNumber, o.Status, o.PaymentStatus, o.PlacedAt,
                o.Contact.FullName, o.Contact.Phone, o.ShippingAddress.City,
                o.Items.Sum(i => i.Quantity), o.Total, o.CurrencyCode, o.CustomerId == null))
            .ToPagedResultAsync(q.Page, q.PageSize, cancellationToken);
    }
}

public sealed record GetAdminOrderQuery(Guid OrderId) : IQuery<AdminOrderDetailsDto>;

internal sealed class GetAdminOrderHandler(IAppDbContext db, ILanguageProvider language) : IQueryHandler<GetAdminOrderQuery, AdminOrderDetailsDto>
{
    public async Task<Result<AdminOrderDetailsDto>> Handle(GetAdminOrderQuery query, CancellationToken cancellationToken)
    {
        var order = await OrderQueries.WithDetails(db).FirstOrDefaultAsync(o => o.Id == query.OrderId, cancellationToken);
        if (order is null)
        {
            return OrderErrors.NotFound;
        }

        return new AdminOrderDetailsDto(
            OrderMapping.ToDetails(order, language.Current, customerCanCancel: false),
            order.CustomerId,
            order.History.OrderBy(h => h.OccurredAt).Select(h => new AdminStatusChangeDto(h.Status, h.OccurredAt, h.ChangedBy, h.Note)).ToList(),
            order.NextStatuses);
    }
}

/// <summary>Moves an order along its workflow. Cancelling returns its stock to the shelf.</summary>
public sealed record ChangeOrderStatusCommand(Guid OrderId, OrderStatus Status, string? Note) : ICommand;

public sealed class ChangeOrderStatusValidator : AbstractValidator<ChangeOrderStatusCommand>
{
    public ChangeOrderStatusValidator()
    {
        RuleFor(x => x.Status).IsInEnum().NotEqual(OrderStatus.Pending);
        RuleFor(x => x.Note).MaximumLength(500);
    }
}

internal sealed class ChangeOrderStatusHandler(IAppDbContext db, ICurrentUser user, TimeProvider clock) : ICommandHandler<ChangeOrderStatusCommand>
{
    public async Task<Result> Handle(ChangeOrderStatusCommand command, CancellationToken cancellationToken)
    {
        var order = await db.Orders.Include(o => o.Items).Include(o => o.History)
            .FirstOrDefaultAsync(o => o.Id == command.OrderId, cancellationToken);
        if (order is null)
        {
            return OrderErrors.NotFound;
        }

        var actor = user.RequiredUserId;
        var now = clock.GetUtcNow();
        var result = command.Status switch
        {
            OrderStatus.Confirmed => order.Confirm(actor, now, command.Note),
            OrderStatus.Shipped => order.Ship(actor, now, command.Note),
            OrderStatus.Delivered => order.Deliver(actor, now, command.Note),
            OrderStatus.Cancelled => order.Cancel(actor, byCustomer: false, command.Note, now),
            OrderStatus.Returned => order.Return(actor, now, command.Note),
            _ => OrderErrors.InvalidTransition(order.Status, command.Status),
        };

        if (result.IsFailure)
        {
            return result;
        }

        if (command.Status is OrderStatus.Cancelled or OrderStatus.Returned)
        {
            await StockRelease.ReleaseAsync(db, order, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
