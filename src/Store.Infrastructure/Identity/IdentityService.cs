using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Store.Application.Abstractions;
using Store.Domain.Common;
using Store.Infrastructure.Notifications;

namespace Store.Infrastructure.Identity;

internal sealed class IdentityService(
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn,
    IOptions<StorefrontOptions> storefront,
    TimeProvider clock) : IIdentityService
{
    /// <summary>OpenID Connect claim an external provider uses to vouch that the user owns the email address.</summary>
    public const string EmailVerifiedClaim = "email_verified";

    private static readonly HashSet<string> PasswordPolicyErrors =
    [
        nameof(IdentityErrorDescriber.PasswordTooShort),
        nameof(IdentityErrorDescriber.PasswordRequiresDigit),
        nameof(IdentityErrorDescriber.PasswordRequiresLower),
        nameof(IdentityErrorDescriber.PasswordRequiresUpper),
        nameof(IdentityErrorDescriber.PasswordRequiresNonAlphanumeric),
        nameof(IdentityErrorDescriber.PasswordRequiresUniqueChars),
    ];

    public async Task<Result<UserProfile>> RegisterAsync(NewUser newUser, CancellationToken cancellationToken)
    {
        var email = newUser.Email.Trim();
        if (await users.FindByEmailAsync(email) is not null)
        {
            return IdentityErrors.EmailTaken;
        }

        var created = await CreateCustomerAsync(email, newUser.FullName, newUser.PhoneNumber, newUser.PreferredLanguage, newUser.Password, emailConfirmed: false);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var user = created.Value;
        await signIn.SignInAsync(user, isPersistent: true);
        return await ToProfileAsync(user);
    }

    public async Task<Result<UserProfile>> SignInAsync(string email, string password, bool rememberMe, CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null)
        {
            // Same error as a wrong password, so the endpoint cannot be used to discover accounts.
            return IdentityErrors.InvalidCredentials;
        }

        var result = await signIn.PasswordSignInAsync(user, password, rememberMe, lockoutOnFailure: true);
        if (result.IsLockedOut)
        {
            return IdentityErrors.LockedOut;
        }

        return result.Succeeded ? await ToProfileAsync(user) : IdentityErrors.InvalidCredentials;
    }

    public Task SignOutAsync() => signIn.SignOutAsync();

    public async Task<UserProfile?> FindAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await users.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        return user is null ? null : await ToProfileAsync(user);
    }

    public async Task<Result<UserProfile>> UpdateProfileAsync(Guid userId, string fullName, string? phoneNumber, Language preferredLanguage, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return IdentityErrors.UserNotFound;
        }

        user.FullName = fullName.Trim();
        user.PhoneNumber = string.IsNullOrWhiteSpace(phoneNumber) ? null : phoneNumber.Trim();
        user.PreferredLanguage = preferredLanguage;
        var updated = await users.UpdateAsync(user);
        return updated.Succeeded ? await ToProfileAsync(user) : ToError(updated);
    }

    public async Task<Result> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return IdentityErrors.UserNotFound;
        }

        var changed = await users.ChangePasswordAsync(user, currentPassword, newPassword);
        if (!changed.Succeeded)
        {
            return changed.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.PasswordMismatch))
                ? IdentityErrors.InvalidCredentials
                : ToError(changed);
        }

        // Refresh the cookie: changing the password rotates the security stamp.
        await signIn.RefreshSignInAsync(user);
        return Result.Success();
    }

    public async Task<PasswordResetTicket?> CreatePasswordResetAsync(string email, CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(email.Trim());
        if (user?.Email is null)
        {
            return null;
        }

        var token = await users.GeneratePasswordResetTokenAsync(user);
        return new PasswordResetTicket(user.Email, user.FullName, user.PreferredLanguage, EncodeToken(token));
    }

    public async Task<Result> ResetPasswordAsync(string email, string token, string newPassword, CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null || DecodeToken(token) is not { } identityToken)
        {
            return IdentityErrors.InvalidResetToken;
        }

        // Checks the token (lifespan, and the security stamp, which this rotates, so each token works once) before the password.
        var reset = await users.ResetPasswordAsync(user, identityToken, newPassword);
        if (!reset.Succeeded)
        {
            return reset.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.InvalidToken))
                ? IdentityErrors.InvalidResetToken
                : ToError(reset);
        }

        // Proving the mailbox is as good as the old password, so lift a lockout left by failed sign-ins.
        if (await users.IsLockedOutAsync(user))
        {
            await users.SetLockoutEndDateAsync(user, null);
        }

        await users.ResetAccessFailedCountAsync(user);
        return Result.Success();
    }

    public async Task<Result> ChallengeExternalAsync(string provider, string returnUrl)
    {
        // A provider is registered only when its client id and secret are configured.
        var available = await signIn.GetExternalAuthenticationSchemesAsync();
        if (!available.Any(s => s.Name == provider))
        {
            return IdentityErrors.ExternalProviderUnavailable;
        }

        // The correlation cookie is tied to the host that serves this response. Google later returns to the
        // storefront address, so start the challenge there or the cookie will not come back with it.
        var canonical = storefront.Value.BaseUrl?.TrimEnd('/');
        var request = signIn.Context.Request;
        if (Uri.TryCreate(canonical, UriKind.Absolute, out var origin) && !SameOrigin(request, origin))
        {
            signIn.Context.Response.Redirect(canonical + request.Path + request.QueryString);
            return Result.Success();
        }

        // Also records the provider in the round-tripped state, which GetExternalLoginInfoAsync requires.
        var properties = signIn.ConfigureExternalAuthenticationProperties(provider, returnUrl);
        await signIn.Context.ChallengeAsync(provider, properties);
        return Result.Success();
    }

    private static bool SameOrigin(HttpRequest request, Uri origin)
    {
        var port = request.Host.Port ?? (string.Equals(request.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);
        var originPort = origin.IsDefaultPort ? (origin.Scheme == "https" ? 443 : 80) : origin.Port;
        return string.Equals(request.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(request.Host.Host, origin.Host, StringComparison.OrdinalIgnoreCase)
            && port == originPort;
    }

    public async Task<Result<UserProfile>> ExternalSignInAsync(Language newAccountLanguage, CancellationToken cancellationToken)
    {
        // What the provider returned, held in the short-lived external cookie by its callback.
        var login = await signIn.GetExternalLoginInfoAsync();
        if (login is null)
        {
            return IdentityErrors.ExternalLoginFailed;
        }

        var result = await SignInWithLoginAsync(login, newAccountLanguage);
        if (result.IsFailure)
        {
            // A successful sign-in clears the external cookie itself.
            await signIn.Context.SignOutAsync(IdentityConstants.ExternalScheme);
        }

        return result;
    }

    public async Task<Result> LinkExternalLoginAsync(Guid userId, CancellationToken cancellationToken)
    {
        var login = await signIn.GetExternalLoginInfoAsync();
        try
        {
            if (login is null)
            {
                return IdentityErrors.ExternalLoginFailed;
            }

            if (VerifiedEmail(login) is not { } email)
            {
                return IdentityErrors.ExternalEmailUnverified;
            }

            var user = await users.FindByIdAsync(userId.ToString());
            if (user?.Email is null)
            {
                return IdentityErrors.UserNotFound;
            }

            if (!string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase))
            {
                return IdentityErrors.ExternalEmailMismatch;
            }

            var owner = await users.FindByLoginAsync(login.LoginProvider, login.ProviderKey);
            if (owner is not null)
            {
                return owner.Id == user.Id ? Result.Success() : Result.Failure(IdentityErrors.ExternalLoginTaken);
            }

            var added = await users.AddLoginAsync(user, LoginInfo(login));
            return added.Succeeded ? Result.Success() : ToError(added);
        }
        finally
        {
            await signIn.Context.SignOutAsync(IdentityConstants.ExternalScheme);
        }
    }

    private async Task<Result<UserProfile>> SignInWithLoginAsync(ExternalLoginInfo login, Language newAccountLanguage)
    {
        var user = await users.FindByLoginAsync(login.LoginProvider, login.ProviderKey);
        if (user is null)
        {
            var linked = await LinkLoginAsync(login, newAccountLanguage);
            if (linked.IsFailure)
            {
                return linked.Error;
            }

            user = linked.Value;
        }

        var signedIn = await signIn.ExternalLoginSignInAsync(login.LoginProvider, login.ProviderKey, isPersistent: true, bypassTwoFactor: true);
        if (signedIn.IsLockedOut)
        {
            return IdentityErrors.LockedOut;
        }

        return signedIn.Succeeded ? await ToProfileAsync(user) : IdentityErrors.ExternalLoginFailed;
    }

    /// <summary>
    /// Creates a password-less customer for a first-time external login. An email that already has an account
    /// is refused: connecting Google happens only after that person signs in with their password.
    /// </summary>
    private async Task<Result<ApplicationUser>> LinkLoginAsync(ExternalLoginInfo login, Language newAccountLanguage)
    {
        // Matching by email trusts the provider's word that this person owns the mailbox, so it must have checked.
        if (VerifiedEmail(login) is not { } email)
        {
            return IdentityErrors.ExternalEmailUnverified;
        }

        if (await users.FindByEmailAsync(email) is not null)
        {
            return IdentityErrors.ExternalAccountExists;
        }

        var created = await CreateCustomerAsync(email, FullNameOf(login.Principal, email), phoneNumber: null, newAccountLanguage, password: null, emailConfirmed: true);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var added = await users.AddLoginAsync(created.Value, LoginInfo(login));
        return added.Succeeded ? created.Value : ToError(added);
    }

    /// <summary>
    /// The one place a customer account is created. <paramref name="password"/> is null for a provider sign-in
    /// (a password can be added later through "forgot password"). <paramref name="emailConfirmed"/> is true only
    /// when the provider has already verified the mailbox.
    /// </summary>
    private async Task<Result<ApplicationUser>> CreateCustomerAsync(
        string email, string fullName, string? phoneNumber, Language preferredLanguage, string? password, bool emailConfirmed)
    {
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = emailConfirmed,
            FullName = fullName.Trim(),
            PhoneNumber = string.IsNullOrWhiteSpace(phoneNumber) ? null : phoneNumber.Trim(),
            PreferredLanguage = preferredLanguage,
            CreatedAt = clock.GetUtcNow(),
        };

        var created = password is null ? await users.CreateAsync(user) : await users.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            return ToError(created);
        }

        var roled = await users.AddToRoleAsync(user, Roles.Customer);
        return roled.Succeeded ? user : Result.Failure<ApplicationUser>(ToError(roled));
    }

    private static string? VerifiedEmail(ExternalLoginInfo login)
    {
        var email = login.Principal.FindFirstValue(ClaimTypes.Email)?.Trim();
        var verified = bool.TryParse(login.Principal.FindFirstValue(EmailVerifiedClaim), out var isVerified) && isVerified;
        return string.IsNullOrEmpty(email) || !verified ? null : email;
    }

    private static UserLoginInfo LoginInfo(ExternalLoginInfo login) =>
        new(login.LoginProvider, login.ProviderKey, login.ProviderDisplayName);

    private static string FullNameOf(ClaimsPrincipal principal, string email)
    {
        var name = principal.FindFirstValue(ClaimTypes.Name)?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            name = $"{principal.FindFirstValue(ClaimTypes.GivenName)} {principal.FindFirstValue(ClaimTypes.Surname)}".Trim();
        }

        if (string.IsNullOrEmpty(name))
        {
            name = email.Split('@')[0];
        }

        return name.Length <= 120 ? name : name[..120];
    }

    // Identity's tokens are base64 (+, / and =). Links carry them base64url-encoded so query strings and mail clients leave them intact.
    private static string EncodeToken(string token) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

    private static string? DecodeToken(string token)
    {
        try
        {
            return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private async Task<UserProfile> ToProfileAsync(ApplicationUser user) =>
        new(user.Id, user.Email!, user.FullName, user.PhoneNumber, user.PreferredLanguage, [.. await users.GetRolesAsync(user)]);

    private static Error ToError(IdentityResult result) =>
        result.Errors.Any() && result.Errors.All(e => PasswordPolicyErrors.Contains(e.Code))
            ? IdentityErrors.WeakPassword
            : IdentityErrors.IdentityFailure(string.Join(" ", result.Errors.Select(e => e.Description)));
}
