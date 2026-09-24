namespace Store.Application.Abstractions;

/// <summary>Notifies staff when a new order is placed. Failures must never propagate to the caller.</summary>
public interface IStaffOrderAlerter
{
    Task AlertNewOrderAsync(Guid orderId, CancellationToken cancellationToken);
}
