namespace Store.Infrastructure.Outbox;

/// <summary>How the outbox worker runs. Validated at startup.</summary>
public sealed class OutboxOptions
{
    public const string Section = "Outbox";

    /// <summary>The worker's backoff schedule when none is configured.</summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultBackoff =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6),
    ];

    /// <summary>Messages claimed per poll. A full batch is followed at once by another, so a backlog drains quickly.</summary>
    public int BatchSize { get; set; } = 20;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a claim lasts. A worker that dies mid-send loses the message to another worker after this.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Attempts before a message is parked for staff to look at.</summary>
    public int MaxAttempts { get; set; } = 8;

    /// <summary>Delay after the 1st, 2nd… failure; the last entry repeats. ±20% jitter is added so retries spread out.</summary>
    public List<TimeSpan> Backoff { get; } = [];

    /// <summary>Sent messages are deleted after this many days. Parked messages are kept until staff deal with them.</summary>
    public int RetentionDays { get; set; } = 14;

    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>The health check reports Degraded once the oldest undelivered message is older than this.</summary>
    public TimeSpan DegradedAfter { get; set; } = TimeSpan.FromMinutes(15);

    public IReadOnlyList<TimeSpan> EffectiveBackoff => Backoff.Count > 0 ? Backoff : DefaultBackoff;

    public IEnumerable<string> Validate()
    {
        if (BatchSize is < 1 or > 500)
        {
            yield return "Outbox:BatchSize must be between 1 and 500.";
        }

        if (PollInterval < TimeSpan.FromMilliseconds(100))
        {
            yield return "Outbox:PollInterval must be at least 100 ms.";
        }

        if (LeaseDuration < TimeSpan.FromSeconds(10))
        {
            yield return "Outbox:LeaseDuration must be at least 10 seconds (longer than the slowest send).";
        }

        if (MaxAttempts is < 1 or > 50)
        {
            yield return "Outbox:MaxAttempts must be between 1 and 50.";
        }

        if (Backoff.Any(d => d <= TimeSpan.Zero))
        {
            yield return "Outbox:Backoff delays must be positive.";
        }

        if (RetentionDays < 1)
        {
            yield return "Outbox:RetentionDays must be at least 1.";
        }

        if (CleanupInterval < TimeSpan.FromMinutes(1))
        {
            yield return "Outbox:CleanupInterval must be at least 1 minute.";
        }

        if (DegradedAfter <= TimeSpan.Zero)
        {
            yield return "Outbox:DegradedAfter must be positive.";
        }
    }
}
