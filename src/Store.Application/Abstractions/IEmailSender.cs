namespace Store.Application.Abstractions;

/// <summary>A plain-text email.</summary>
public sealed record EmailMessage(IReadOnlyCollection<string> To, string Subject, string Body);

/// <summary>
/// Sends mail through the SMTP server configured in store settings. When no SMTP host is set, or the server
/// fails, it logs and returns: callers never see a mail failure.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
