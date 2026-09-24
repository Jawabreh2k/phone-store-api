using FluentValidation;
using Store.Application.Abstractions;
using Store.Application.Notifications;
using Store.Application.Outbox;
using Store.Domain.Common;

namespace Store.Application.Customers;

public sealed record ForgotPasswordCommand(string Email) : ICommand;

public sealed class ForgotPasswordValidator : AbstractValidator<ForgotPasswordCommand>
{
    public ForgotPasswordValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
    }
}

/// <summary>
/// Emails a reset link when an account has this email. The answer is the same when none does,
/// so the endpoint cannot be used to discover accounts.
/// </summary>
internal sealed class ForgotPasswordHandler(IIdentityService identity, IAppDbContext db, IOutbox outbox, TimeProvider clock) : ICommandHandler<ForgotPasswordCommand>
{
    /// <summary>
    /// Both answers wait out at least this long. Token generation and the outbox write are local; the email itself
    /// goes out later, so a registered address is not the one that sits through an SMTP handshake.
    /// </summary>
    private static readonly TimeSpan MinimumResponse = TimeSpan.FromMilliseconds(200);

    public async Task<Result> Handle(ForgotPasswordCommand command, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (await identity.CreatePasswordResetAsync(command.Email, cancellationToken) is { } ticket)
        {
            // Every request is its own message, so the key is unique; the token rides encrypted, never in the payload.
            outbox.Enqueue(new OutboxEntry(
                OutboxTypes.PasswordResetEmail,
                new PasswordResetPayload(ticket.Email, ticket.FullName, ticket.PreferredLanguage, clock.GetUtcNow() + ticket.ValidFor),
                $"account:password-reset:{Guid.CreateVersion7()}",
                Channel: NotificationChannel.Email,
                Template: NotificationTemplate.PasswordReset,
                Secret: ticket.Token));
            await db.SaveChangesAsync(cancellationToken);
        }

        var remaining = MinimumResponse - System.Diagnostics.Stopwatch.GetElapsedTime(started);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken);
        }

        return Result.Success();
    }
}

public sealed record ResetPasswordCommand(string Email, string Token, string NewPassword) : ICommand;

public sealed class ResetPasswordValidator : AbstractValidator<ResetPasswordCommand>
{
    public ResetPasswordValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(256);
        RuleFor(x => x.Token).NotEmpty().MaximumLength(2048);
        RuleFor(x => x.NewPassword).NotEmpty().MinimumLength(8).MaximumLength(128);
    }
}

/// <summary>Sets a new password with the token from the reset email. The token works once and expires.</summary>
internal sealed class ResetPasswordHandler(IIdentityService identity) : ICommandHandler<ResetPasswordCommand>
{
    public Task<Result> Handle(ResetPasswordCommand command, CancellationToken cancellationToken) =>
        identity.ResetPasswordAsync(command.Email, command.Token, command.NewPassword, cancellationToken);
}
