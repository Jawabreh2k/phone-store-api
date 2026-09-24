using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Store.Application.Abstractions;
using Store.Application.Outbox;
using Store.Domain.Common;
using Store.Domain.Orders;

namespace Store.Application.Notifications;

internal sealed record PasswordResetPayload(string Email, string FullName, Language Language, DateTimeOffset ExpiresAt);

/// <summary>Customer messages about an order, rendered at send time in the order's language and currency.</summary>
internal sealed class CustomerOrderNotificationHandler(
    IAppDbContext db,
    IEnumerable<INotificationChannel> channels,
    IOptions<StorefrontOptions> storefront) : IOutboxMessageDeliverer
{
    public string Type => OutboxTypes.CustomerOrderNotification;

    public async Task<DeliveryResult> HandleAsync(OutboxDelivery delivery, CancellationToken cancellationToken)
    {
        if (JsonSerializer.Deserialize<CustomerOrderPayload>(delivery.Payload, Outbox.Outbox.PayloadJson) is not { } payload)
        {
            return DeliveryResult.Permanent("The message payload is unreadable.");
        }

        if (channels.FirstOrDefault(c => c.Channel == delivery.Channel) is not { } channel)
        {
            return DeliveryResult.Permanent($"The {delivery.Channel} channel is not enabled.");
        }

        var order = await db.Orders.AsNoTracking().Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == payload.OrderId, cancellationToken);
        if (order is null)
        {
            return DeliveryResult.Permanent("The order no longer exists.");
        }

        var recipient = channel.Channel == NotificationChannel.Email ? order.Contact.Email : order.Contact.Phone;
        if (string.IsNullOrWhiteSpace(recipient))
        {
            return DeliveryResult.Permanent($"The order has no address for {channel.Channel}.");
        }

        var settings = await db.StoreSettings.AsNoTracking().FirstAsync(cancellationToken);
        var language = order.Language;
        var model = new OrderMessageModel(
            settings.StoreName.In(language),
            order.Contact.FullName,
            order.OrderNumber,
            order.Items.Select(i => new OrderMessageLine(i.ProductName.In(language), i.Quantity, i.LineTotal)).ToList(),
            order.Total,
            order.CurrencyCode,
            OrderLinks.For(storefront.Value.BaseUrl, order, delivery.Secret),
            IsGuest: order.CustomerId is null,
            payload.ByCustomer,
            payload.Template == NotificationTemplate.OrderCancelled ? order.CancellationReason : null);

        var rendered = MessageTemplates.RenderOrder(payload.Template, language, model);
        var isEmail = channel.Channel == NotificationChannel.Email;
        return await channel.SendAsync(
            new NotificationEnvelope(
                delivery.MessageId,
                channel.Channel,
                NotificationPolicy.CategoryOf(payload.Template),
                [recipient],
                language,
                rendered.Subject,
                isEmail ? rendered.Text : rendered.ShortText,
                isEmail ? rendered.Html : null),
            cancellationToken);
    }
}

/// <summary>The new-order alert to every admin with an email address.</summary>
internal sealed class StaffNewOrderAlertHandler(
    IAppDbContext db,
    IIdentityService identity,
    IEnumerable<INotificationChannel> channels) : IOutboxMessageDeliverer
{
    public string Type => OutboxTypes.StaffNewOrderAlert;

    public async Task<DeliveryResult> HandleAsync(OutboxDelivery delivery, CancellationToken cancellationToken)
    {
        if (JsonSerializer.Deserialize<StaffNewOrderPayload>(delivery.Payload, Outbox.Outbox.PayloadJson) is not { } payload)
        {
            return DeliveryResult.Permanent("The message payload is unreadable.");
        }

        if (channels.FirstOrDefault(c => c.Channel == NotificationChannel.Email) is not { } email)
        {
            return DeliveryResult.Permanent("The Email channel is not enabled.");
        }

        var order = await db.Orders.AsNoTracking().Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == payload.OrderId, cancellationToken);
        if (order is null)
        {
            return DeliveryResult.Permanent("The order no longer exists.");
        }

        var recipients = await identity.GetAdminEmailsAsync(cancellationToken);
        if (recipients.Count == 0)
        {
            return DeliveryResult.Permanent("No admin account has an email address.");
        }

        // Staff alerts stay in English, as they always were; the store's customers get their own language.
        const Language language = Language.En;
        var rendered = MessageTemplates.RenderStaffNewOrder(language, new StaffOrderModel(
            order.OrderNumber, order.Contact.FullName, order.Contact.Phone, order.ShippingAddress.City,
            order.Total, order.CurrencyCode, order.Items.Sum(i => i.Quantity)));

        return await email.SendAsync(
            new NotificationEnvelope(delivery.MessageId, NotificationChannel.Email, NotificationCategory.Transactional, recipients, language, rendered.Subject, rendered.Text, rendered.Html),
            cancellationToken);
    }
}

/// <summary>The password reset link. The token travels encrypted in the outbox row and is dropped once sent.</summary>
internal sealed class PasswordResetEmailHandler(
    IAppDbContext db,
    IEnumerable<INotificationChannel> channels,
    IOptions<StorefrontOptions> storefront,
    TimeProvider clock) : IOutboxMessageDeliverer
{
    public string Type => OutboxTypes.PasswordResetEmail;

    public async Task<DeliveryResult> HandleAsync(OutboxDelivery delivery, CancellationToken cancellationToken)
    {
        if (JsonSerializer.Deserialize<PasswordResetPayload>(delivery.Payload, Outbox.Outbox.PayloadJson) is not { } payload)
        {
            return DeliveryResult.Permanent("The message payload is unreadable.");
        }

        if (channels.FirstOrDefault(c => c.Channel == NotificationChannel.Email) is not { } email)
        {
            return DeliveryResult.Permanent("The Email channel is not enabled.");
        }

        var baseUrl = storefront.Value.BaseUrl?.TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
        {
            return DeliveryResult.Permanent("Storefront:BaseUrl is not configured, so there is no page to link to.");
        }

        if (delivery.Secret is not { } token)
        {
            return DeliveryResult.Permanent("The reset token is no longer available; the customer can request a new link.");
        }

        var remaining = payload.ExpiresAt - clock.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            return DeliveryResult.Permanent("The reset link expired before it could be sent.");
        }

        var settings = await db.StoreSettings.AsNoTracking().FirstAsync(cancellationToken);
        var language = payload.Language;
        var link = $"{baseUrl}/{(language == Language.Ar ? "ar" : "en")}/reset-password" +
            $"?email={Uri.EscapeDataString(payload.Email)}&token={Uri.EscapeDataString(token)}";
        var rendered = MessageTemplates.RenderPasswordReset(language, new PasswordResetModel(
            settings.StoreName.In(language), payload.FullName.Trim(), link, Math.Max(1, (int)Math.Ceiling(remaining.TotalHours))));

        return await email.SendAsync(
            new NotificationEnvelope(delivery.MessageId, NotificationChannel.Email, NotificationCategory.Transactional, [payload.Email], language, rendered.Subject, rendered.Text, rendered.Html),
            cancellationToken);
    }
}

internal static class OrderLinks
{
    /// <summary>
    /// The account order page for signed-in customers; the tracking page for guests, which needs the raw token.
    /// Null when the storefront address is not configured or a guest's token is gone (cleared after a park).
    /// </summary>
    public static string? For(string? baseUrl, Order order, string? trackingToken)
    {
        var root = baseUrl?.TrimEnd('/');
        if (!Uri.TryCreate(root, UriKind.Absolute, out _))
        {
            return null;
        }

        var lang = order.Language == Language.Ar ? "ar" : "en";
        if (order.CustomerId is not null)
        {
            return $"{root}/{lang}/account/orders/{Uri.EscapeDataString(order.OrderNumber)}";
        }

        return trackingToken is null ? null : $"{root}/{lang}/orders/track/{Uri.EscapeDataString(trackingToken)}";
    }
}
