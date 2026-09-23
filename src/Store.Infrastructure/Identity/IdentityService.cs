using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Store.Application.Abstractions;
using Store.Domain.Common;

namespace Store.Infrastructure.Identity;

internal sealed class IdentityService(
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn,
    TimeProvider clock) : IIdentityService
{
    public async Task<Result<UserProfile>> RegisterAsync(NewUser newUser, CancellationToken cancellationToken)
    {
        var email = newUser.Email.Trim();
        if (await users.FindByEmailAsync(email) is not null)
        {
            return IdentityErrors.EmailTaken;
        }

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            FullName = newUser.FullName.Trim(),
            PhoneNumber = string.IsNullOrWhiteSpace(newUser.PhoneNumber) ? null : newUser.PhoneNumber.Trim(),
            PreferredLanguage = newUser.PreferredLanguage,
            CreatedAt = clock.GetUtcNow(),
        };

        var created = await users.CreateAsync(user, newUser.Password);
        if (!created.Succeeded)
        {
            return ToError(created);
        }

        var roled = await users.AddToRoleAsync(user, Roles.Customer);
        if (!roled.Succeeded)
        {
            return ToError(roled);
        }

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

    private async Task<UserProfile> ToProfileAsync(ApplicationUser user) =>
        new(user.Id, user.Email!, user.FullName, user.PhoneNumber, user.PreferredLanguage, [.. await users.GetRolesAsync(user)]);

    private static Error ToError(IdentityResult result) =>
        IdentityErrors.IdentityFailure(string.Join(" ", result.Errors.Select(e => e.Description)));
}
