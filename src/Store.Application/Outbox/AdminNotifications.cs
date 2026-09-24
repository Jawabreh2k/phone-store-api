using Microsoft.EntityFrameworkCore;
using Store.Application.Abstractions;
using Store.Application.Notifications;
using Store.Domain.Common;
using Store.Domain.Orders;

namespace Store.Application.Outbox;

/// <summary>
/// One outbox message as staff see it on the order screen: delivery state only. The payload and the protected secret
/// are never part of it, so neither a tracking token nor the rendered text can leak through the admin API.
/// </summary>
public sealed record AdminNotificationDto(
    Guid Id,
    string Type,
    NotificationChannel? Channel,
    NotificationTemplate? Template,
    OutboxStatus Status,
    int Attempts,
    string? LastError,
    DateTimeOffset OccurredAt,
    DateTimeOffset NextAttemptAt,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? ProcessedAt);

public sealed record GetOrderNotificationsQuery(Guid OrderId) : IQuery<IReadOnlyList<AdminNotificationDto>>;

internal sealed class GetOrderNotificationsHandler(IAppDbContext db) : IQueryHandler<GetOrderNotificationsQuery, IReadOnlyList<AdminNotificationDto>>
{
    public async Task<Result<IReadOnlyList<AdminNotificationDto>>> Handle(GetOrderNotificationsQuery query, CancellationToken cancellationToken)
    {
        if (!await db.Orders.AnyAsync(o => o.Id == query.OrderId, cancellationToken))
        {
            return OrderErrors.NotFound;
        }

        var messages = await db.OutboxMessages.AsNoTracking()
            .Where(m => m.OrderId == query.OrderId)
            .Select(m => new AdminNotificationDto(
                m.Id, m.Type, m.Channel, m.Template, m.Status, m.Attempts, m.LastError,
                m.OccurredAt, m.NextAttemptAt, m.LastAttemptAt, m.ProcessedAt))
            .ToListAsync(cancellationToken);

        // Sorted here: UUIDv7 ids break ties in the order the rows were written, which SQL Server's uniqueidentifier order does not follow.
        return messages.OrderBy(m => m.OccurredAt).ThenBy(m => m.Id).ToList();
    }
}

/// <summary>Staff retry of a message that stopped retrying on its own: it is sent again as soon as a worker picks it up.</summary>
public sealed record RetryNotificationCommand(Guid NotificationId) : ICommand;

internal sealed class RetryNotificationHandler(IAppDbContext db, TimeProvider clock) : ICommandHandler<RetryNotificationCommand>
{
    public async Task<Result> Handle(RetryNotificationCommand command, CancellationToken cancellationToken)
    {
        var message = await db.OutboxMessages.FirstOrDefaultAsync(m => m.Id == command.NotificationId, cancellationToken);
        if (message is null)
        {
            return OutboxErrors.NotFound;
        }

        var requeued = message.Requeue(clock.GetUtcNow());
        if (requeued.IsFailure)
        {
            return requeued;
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
