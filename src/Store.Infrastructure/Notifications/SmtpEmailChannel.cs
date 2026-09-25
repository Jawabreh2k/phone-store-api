using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using Store.Application.Abstractions;
using Store.Application.Notifications;

namespace Store.Infrastructure.Notifications;

/// <summary>
/// Email through the SMTP server in the store settings row (Admin &gt; Settings &gt; Email alerts), read on every send
/// so a change there applies at once. Port 465 uses implicit TLS, 587 STARTTLS, 25 plain; MailKit's Auto picks that from
/// the port (System.Net.Mail cannot speak implicit TLS).
/// <para>
/// Failure reasons name the SMTP status and MailKit error code only, never an address, because they are stored and shown
/// to staff. The Message-Id is derived from the outbox id, so a resend after a crash is recognisable as the same message.
/// </para>
/// </summary>
internal sealed class SmtpEmailChannel(IAppDbContext db, TimeProvider clock, ISecretProtector secrets) : INotificationChannel
{
    public NotificationChannel Channel => NotificationChannel.Email;

    public async Task<DeliveryResult> SendAsync(NotificationEnvelope envelope, CancellationToken cancellationToken)
    {
        var settings = await db.StoreSettings.AsNoTracking().FirstAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.SmtpHost))
        {
            return DeliveryResult.Permanent("Email is not configured (Admin > Settings > Email alerts).");
        }

        if (!MailboxAddress.TryParse(settings.SmtpFrom ?? string.Empty, out var from))
        {
            return DeliveryResult.Permanent("The store's sender address is not a valid email address.");
        }

        var mail = new MimeMessage { Subject = envelope.Subject };
        mail.From.Add(from);
        foreach (var recipient in envelope.Recipients)
        {
            if (!MailboxAddress.TryParse(recipient, out var to))
            {
                return DeliveryResult.Permanent("A recipient address is not a valid email address.");
            }

            mail.To.Add(to);
        }

        var domain = from.Domain is { Length: > 0 } fromDomain ? fromDomain : "localhost";
        mail.MessageId = $"{envelope.MessageId:N}@{domain}";
        mail.Date = clock.GetUtcNow();
        mail.Body = new BodyBuilder { TextBody = envelope.TextBody, HtmlBody = envelope.HtmlBody }.ToMessageBody();
        mail.Headers.Add("Auto-Submitted", "auto-generated");

        try
        {
            using var client = new SmtpClient();
            await client.ConnectAsync(settings.SmtpHost, settings.SmtpPort, SecureSocketOptions.Auto, cancellationToken);
            if (!string.IsNullOrWhiteSpace(settings.SmtpUser))
            {
                var password = settings.SmtpPassword is null ? null : secrets.Unprotect(settings.SmtpPassword);
                await client.AuthenticateAsync(settings.SmtpUser, password ?? string.Empty, cancellationToken);
            }

            await client.SendAsync(mail, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
            return DeliveryResult.Sent;
        }
        catch (SmtpCommandException ex)
        {
            var reason = $"SMTP {(int)ex.StatusCode} {ex.ErrorCode}.";
            return (int)ex.StatusCode >= 500 ? DeliveryResult.Permanent(reason) : DeliveryResult.Temporary(reason);
        }
        catch (AuthenticationException)
        {
            // Usually a changed password; staff fix the settings and the retry goes through.
            return DeliveryResult.Temporary("SMTP authentication failed.");
        }
        catch (SslHandshakeException)
        {
            return DeliveryResult.Temporary("SMTP TLS handshake failed.");
        }
        catch (Exception ex) when (ex is SmtpProtocolException or ServiceNotConnectedException or SocketException or IOException or TimeoutException)
        {
            return DeliveryResult.Temporary($"SMTP connection failed ({ex.GetType().Name}).");
        }
    }
}
