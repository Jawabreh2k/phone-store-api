using Store.Domain.Common;

namespace Store.Application.Notifications;

/// <summary>How a message reaches someone. Stored as text on outbox rows: never rename a value.</summary>
public enum NotificationChannel
{
    Email = 1,
    Sms = 2,
    WhatsApp = 3,
}

/// <summary>
/// Why a message may be sent. Transactional messages (about something the customer did) need no opt-in.
/// A future marketing category must check explicit consent before it is enqueued (see <see cref="NotificationPolicy"/>).
/// </summary>
public enum NotificationCategory
{
    Transactional = 1,
}

/// <summary>Every message the store sends. Stored as text on outbox rows: never rename a value.</summary>
public enum NotificationTemplate
{
    OrderPlaced = 1,
    OrderConfirmed = 2,
    OrderShipped = 3,
    OrderDelivered = 4,
    OrderCancelled = 5,
    OrderReturned = 6,
    StaffNewOrder = 7,
    PasswordReset = 8,
}

public static class NotificationPolicy
{
    public static NotificationCategory CategoryOf(NotificationTemplate template) => template switch
    {
        _ => NotificationCategory.Transactional,
    };

    /// <summary>Whether a message of this category may go to someone with the given marketing consent.</summary>
    public static bool MaySend(NotificationCategory category, bool hasMarketingConsent) =>
        category == NotificationCategory.Transactional || hasMarketingConsent;
}

/// <summary>
/// A rendered message, ready for one channel. <see cref="MessageId"/> is the outbox row id: channels that can
/// deduplicate (SMTP Message-Id) use it, since delivery is at least once.
/// </summary>
public sealed record NotificationEnvelope(
    Guid MessageId,
    NotificationChannel Channel,
    NotificationCategory Category,
    IReadOnlyList<string> Recipients,
    Language Language,
    string Subject,
    string TextBody,
    string? HtmlBody);

public enum DeliveryOutcome
{
    Sent = 1,

    /// <summary>Worth retrying later (server down, rate limited, timeout).</summary>
    TemporaryFailure = 2,

    /// <summary>Retrying cannot help (bad address, not configured, missing template): park at once.</summary>
    PermanentFailure = 3,
}

/// <summary>Result of one delivery attempt. <see cref="Reason"/> is stored and shown to staff: never put a recipient, token or message text in it.</summary>
public sealed record DeliveryResult(DeliveryOutcome Outcome, string? Reason)
{
    public static readonly DeliveryResult Sent = new(DeliveryOutcome.Sent, null);

    public static DeliveryResult Temporary(string reason) => new(DeliveryOutcome.TemporaryFailure, reason);

    public static DeliveryResult Permanent(string reason) => new(DeliveryOutcome.PermanentFailure, reason);
}

/// <summary>Sends a rendered message over one channel. Implementations report failures as results; they do not throw for them.</summary>
public interface INotificationChannel
{
    NotificationChannel Channel { get; }

    Task<DeliveryResult> SendAsync(NotificationEnvelope envelope, CancellationToken cancellationToken);
}

public sealed class NotificationOptions
{
    public const string Section = "Notifications";

    /// <summary>
    /// Channels customers are messaged on. Email is used only when the order has an email address;
    /// Sms / WhatsApp go to the order's phone number and need a provider (see Infrastructure's MessagingOptions).
    /// </summary>
    public List<NotificationChannel> CustomerChannels { get; } = [];
}

public sealed class StorefrontOptions
{
    public const string Section = "Storefront";

    /// <summary>
    /// Public address of the web storefront (e.g. https://shop.example), used for links in messages.
    /// Configured rather than read from the request, so a forged Host header cannot redirect a link.
    /// </summary>
    public string? BaseUrl { get; set; }
}
