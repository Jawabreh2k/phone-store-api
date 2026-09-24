using System.Diagnostics.Metrics;

namespace Store.Infrastructure.Outbox;

/// <summary>
/// The "Store.Outbox" meter: messages sent / failed / parked by type and channel, and gauges for the backlog
/// (undelivered messages and the age of the oldest one), refreshed by the worker on every poll.
/// </summary>
internal sealed class OutboxMetrics
{
    public const string MeterName = "Store.Outbox";

    private readonly Counter<long> _sent;
    private readonly Counter<long> _failed;
    private readonly Counter<long> _parked;
    private long _pending;
    private double _oldestPendingAgeSeconds;

    public OutboxMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _sent = meter.CreateCounter<long>("store.outbox.messages.sent", "{message}", "Messages delivered.");
        _failed = meter.CreateCounter<long>("store.outbox.messages.failed", "{message}", "Delivery attempts that failed.");
        _parked = meter.CreateCounter<long>("store.outbox.messages.parked", "{message}", "Messages that stopped retrying.");
        meter.CreateObservableGauge("store.outbox.pending", () => Interlocked.Read(ref _pending), "{message}", "Messages not yet delivered or parked.");
        meter.CreateObservableGauge("store.outbox.oldest_pending_age", () => Volatile.Read(ref _oldestPendingAgeSeconds), "s", "Age of the oldest undelivered message.");
    }

    public void Sent(string type, string? channel) => _sent.Add(1, Tags(type, channel));

    public void Failed(string type, string? channel, bool permanent) =>
        _failed.Add(1, [.. Tags(type, channel), new("kind", permanent ? "permanent" : "temporary")]);

    public void Parked(string type, string? channel) => _parked.Add(1, Tags(type, channel));

    public void Backlog(long pending, TimeSpan oldestAge)
    {
        Interlocked.Exchange(ref _pending, pending);
        Volatile.Write(ref _oldestPendingAgeSeconds, Math.Max(0, oldestAge.TotalSeconds));
    }

    private static KeyValuePair<string, object?>[] Tags(string type, string? channel) =>
        [new("type", type), new("channel", channel ?? "none")];
}
