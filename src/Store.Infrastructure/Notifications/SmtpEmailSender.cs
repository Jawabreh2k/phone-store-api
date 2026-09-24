using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MimeKit;
using Store.Application.Abstractions;

namespace Store.Infrastructure.Notifications;

/// <summary>
/// Sends through the SMTP server in the store settings row (Admin &gt; Settings &gt; Email alerts), read on every send
/// so a change there applies at once. No host means mail is off: log and skip, never throw.
/// Port 465 uses implicit TLS, 587 uses STARTTLS, and 25 stays plain — MailKit's Auto picks that from the port.
/// System.Net.Mail.SmtpClient cannot speak implicit TLS, so a provider's documented SSL port would never deliver.
/// </summary>
internal sealed partial class SmtpEmailSender(IAppDbContext db, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var settings = await db.StoreSettings.AsNoTracking().FirstAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.SmtpHost))
        {
            LogSmtpNotConfigured(logger, message.Subject);
            return;
        }

        try
        {
            var mail = new MimeMessage();
            mail.From.Add(MailboxAddress.Parse(settings.SmtpFrom ?? string.Empty));
            foreach (var to in message.To)
            {
                mail.To.Add(MailboxAddress.Parse(to));
            }

            mail.Subject = message.Subject;
            mail.Body = new TextPart("plain") { Text = message.Body };

            using var client = new SmtpClient();
            await client.ConnectAsync(settings.SmtpHost, settings.SmtpPort, SecureSocketOptions.Auto, cancellationToken);
            if (!string.IsNullOrWhiteSpace(settings.SmtpUser))
            {
                await client.AuthenticateAsync(settings.SmtpUser, settings.SmtpPassword ?? string.Empty, cancellationToken);
            }

            await client.SendAsync(mail, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception ex)
        {
            LogSendFailed(logger, message.Subject, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "SMTP host is not configured; skipping email \"{Subject}\".")]
    private static partial void LogSmtpNotConfigured(ILogger logger, string subject);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to send email \"{Subject}\".")]
    private static partial void LogSendFailed(ILogger logger, string subject, Exception exception);
}
