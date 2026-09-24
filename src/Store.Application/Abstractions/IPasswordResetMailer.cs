namespace Store.Application.Abstractions;

/// <summary>Emails the customer a link to the storefront's reset-password page. Failures must never propagate to the caller.</summary>
public interface IPasswordResetMailer
{
    Task SendResetLinkAsync(PasswordResetTicket ticket, CancellationToken cancellationToken);
}
