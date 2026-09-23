using Store.Domain.Common;

namespace Store.Application.Abstractions;

public static class Roles
{
    public const string Admin = "Admin";
    public const string Customer = "Customer";
}

public sealed record UserProfile(Guid Id, string Email, string FullName, string? PhoneNumber, Language PreferredLanguage, IReadOnlyList<string> Roles);

public sealed record NewUser(string Email, string Password, string FullName, string? PhoneNumber, Language PreferredLanguage);

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
    Task<Result<UserProfile>> UpdateProfileAsync(Guid userId, string fullName, string? phoneNumber, Language preferredLanguage, CancellationToken cancellationToken);
    Task<Result> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken cancellationToken);
}

public static class IdentityErrors
{
    public static readonly Error InvalidCredentials = Error.Unauthorized("auth.invalid_credentials", "The email or password is incorrect.");
    public static readonly Error LockedOut = Error.Unauthorized("auth.locked_out", "Too many failed attempts. Try again in a few minutes.");
    public static readonly Error EmailTaken = Error.Conflict("auth.email_taken", "An account with this email already exists.");
    public static readonly Error UserNotFound = Error.NotFound("auth.user_not_found", "The user was not found.");

    public static Error IdentityFailure(string description) => Error.Validation("auth.identity_error", description);
}
