using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Store.Infrastructure.Persistence;

namespace Store.Infrastructure.Outbox;

/// <summary>Degraded when the oldest undelivered message has waited longer than <see cref="OutboxOptions.DegradedAfter"/>.</summary>
internal sealed class OutboxHealthCheck(AppDbContext db, IOptions<OutboxOptions> options, TimeProvider clock) : IHealthCheck
{
    public const string Name = "outbox";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var backlog = await OutboxBacklog.ReadAsync(db, cancellationToken);
        var age = backlog.OldestUndeliveredAt is { } oldest ? clock.GetUtcNow() - oldest : TimeSpan.Zero;
        var data = new Dictionary<string, object>
        {
            ["undelivered"] = backlog.Undelivered,
            ["oldestUndeliveredAgeSeconds"] = (long)age.TotalSeconds,
            ["parked"] = backlog.Parked,
        };

        return age > options.Value.DegradedAfter
            ? HealthCheckResult.Degraded($"The oldest undelivered outbox message is {(long)age.TotalMinutes} minutes old.", data: data)
            : HealthCheckResult.Healthy(data: data);
    }
}
