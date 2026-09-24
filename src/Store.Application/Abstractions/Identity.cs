using Store.Domain.Common;

namespace Store.Application.Abstractions;

public static class Roles
{
    public const string Admin = "Admin";
    public const string Customer = "Customer";
}

/// <summary>External sign-in providers. Each name is also the provider's authentication scheme.</summary>
public static class ExternalProviders
{
    public const string Google = "Google";
}

/// <summary>The signed-in user. <c>ConnectedLogins</c> lists the external providers (<see cref="ExternalProviders"/>) the account can sign in with.</summary>
public sealed record UserProfile(
    Guid Id,
    string Email,
    string FullName,
    string? PhoneNumber,
    Language PreferredLanguage,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> ConnectedLogins);

public sealed record NewUser(string Email, string Password, string FullName, string? PhoneNumber, Language PreferredLanguage);

/// <summary>What a password reset email needs. <see cref="Token"/> is already URL-safe and stops working after <see cref="ValidFor"/>.</summary>
public sealed record PasswordResetTicket(string Email, string FullName, Language PreferredLanguage, string Token, TimeSpan ValidFor);

/// <summary>
/// Account management and sign-in, implemented with ASP.NET Core Identity in Infrastructure.
/// Signing in issues the auth cookie on the current HTTP response.
/// </summary>
public interface IIdentityService
{
    Task<Result<UserProfile>> RegisterAsync(NewUser user, CancellationToken cancellationToken);
    Task<Result<UserProfile>> SignInAsync(string email, string password, bool rememberMe, CancellationToken cancellationToken);
    Task SignOutAsync();
    Task<UserProfile?> FindAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Distinct email addresses of every admin, for staff alerts.</summary>
    Task<IReadOnlyList<string>> GetAdminEmailsAsync(CancellationToken cancellationToken);
    Task<Result<UserProfile>> UpdateProfileAsync(Guid userId, string fullName, string? phoneNumber, Language preferredLanguage, CancellationToken cancellationToken);
    Task<Result> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken cancellationToken);

    /// <summary>A single-use reset token for the account with this email, or null when there is no such account.</summary>
    Task<PasswordResetTicket?> CreatePasswordResetAsync(string email, CancellationToken cancellationToken);

    Task<Result> ResetPasswordAsync(string email, string token, string newPassword, CancellationToken cancellationToken);

    /// <summary>
    /// Redirects the current response to the provider's sign-in page. The provider returns to the API, which then
    /// sends the browser to <paramref name="returnUrl"/>, where <see cref="ExternalSignInAsync"/> finishes the sign-in.
    /// </summary>
    Task<Result> ChallengeExternalAsync(string provider, string returnUrl);

    /// <summary>
    /// Signs in with the login the provider just returned. A login already stored on an account signs that account in.
    /// A login seen for the first time creates a customer account in <paramref name="newAccountLanguage"/> when the
    /// email is unused. It never attaches itself to an existing password account.
    /// </summary>
    Task<Result<UserProfile>> ExternalSignInAsync(Language newAccountLanguage, CancellationToken cancellationToken);

    /// <summary>
    /// Attaches the login the provider just returned to <paramref name="userId"/>, who is already signed in.
    /// The provider email must be verified and must be that account's email.
    /// </summary>
    Task<Result> LinkExternalLoginAsync(Guid userId, CancellationToken cancellationToken);
}

public static class IdentityErrors
{
    public static readonly Error InvalidCredentials = Error.Unauthorized("auth.invalid_credentials", "The email or password is incorrect.");
    public static readonly Error LockedOut = Error.Unauthorized("auth.locked_out", "Too many failed attempts. Try again in a few minutes.");
    public static readonly Error EmailTaken = Error.Conflict("auth.email_taken", "An account with this email already exists.");
    public static readonly Error UserNotFound = Error.NotFound("auth.user_not_found", "The user was not found.");
    public static readonly Error WeakPassword = Error.Validation("auth.weak_password", "The password needs at least 8 characters, including a lowercase letter and a digit.");

    // Also the answer for an email with no account, so the reset form cannot be used to discover accounts.
    public static readonly Error InvalidResetToken = Error.Validation("auth.invalid_reset_token", "This password reset link is invalid or has expired.");

    public static readonly Error ExternalProviderUnavailable = Error.NotFound("auth.external_provider_unavailable", "This sign-in option is not available.");
    public static readonly Error ExternalLoginFailed = Error.Unauthorized("auth.external_login_failed", "Signing in with the external account did not complete.");
    public static readonly Error ExternalEmailUnverified = Error.Unauthorized("auth.external_email_unverified", "The external account has no verified email address.");
    public static readonly Error ExternalAccountExists = Error.Conflict("auth.external_account_exists", "An account with this email already exists. Sign in with your password, then connect the external account.");
    public static readonly Error ExternalEmailMismatch = Error.Conflict("auth.external_email_mismatch", "The external account uses a different email than the one you are signed in with.");
    public static readonly Error ExternalLoginTaken = Error.Conflict("auth.external_login_taken", "This external account is already connected to another store account.");

    public static Error IdentityFailure(string description) => Error.Validation("auth.identity_error", description);
}
