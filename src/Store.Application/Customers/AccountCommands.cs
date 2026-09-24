using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Store.Application.Abstractions;
using Store.Application.Carts;
using Store.Application.Common;
using Store.Domain.Common;

namespace Store.Application.Customers;

public sealed record RegisterCommand(string Email, string Password, string FullName, string? PhoneNumber, Language PreferredLanguage, Guid? GuestCartId)
    : ICommand<UserProfile>;

public sealed class RegisterValidator : AbstractValidator<RegisterCommand>
{
    public RegisterValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MinimumLength(8).MaximumLength(128);
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(120);
        RuleFor(x => x.PhoneNumber).Phone().When(x => !string.IsNullOrEmpty(x.PhoneNumber));
        RuleFor(x => x.PreferredLanguage).IsInEnum();
    }
}

/// <summary>Creates a customer account, signs them in and keeps whatever they had in their guest cart.</summary>
internal sealed class RegisterHandler(IAppDbContext db, IIdentityService identity, ICommandHandler<MergeGuestCartCommand> mergeCart)
    : ICommandHandler<RegisterCommand, UserProfile>
{
    public async Task<Result<UserProfile>> Handle(RegisterCommand command, CancellationToken cancellationToken)
    {
        var settings = await db.StoreSettings.AsNoTracking().FirstAsync(cancellationToken);
        var phone = PhoneInput.NormalizeOptional(settings, command.PhoneNumber, "phoneNumber");
        if (phone.IsFailure)
        {
            return phone.Error;
        }

        var result = await identity.RegisterAsync(
            new NewUser(command.Email, command.Password, command.FullName, phone.Value?.Value, command.PreferredLanguage),
            cancellationToken);

        await GuestCartMerge.AfterSignInAsync(mergeCart, result, command.GuestCartId, cancellationToken);
        return result;
    }
}

public sealed record LoginCommand(string Email, string Password, bool RememberMe, Guid? GuestCartId) : ICommand<UserProfile>;

public sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
    }
}

internal sealed class LoginHandler(IIdentityService identity, ICommandHandler<MergeGuestCartCommand> mergeCart)
    : ICommandHandler<LoginCommand, UserProfile>
{
    public async Task<Result<UserProfile>> Handle(LoginCommand command, CancellationToken cancellationToken)
    {
        var result = await identity.SignInAsync(command.Email, command.Password, command.RememberMe, cancellationToken);

        await GuestCartMerge.AfterSignInAsync(mergeCart, result, command.GuestCartId, cancellationToken);
        return result;
    }
}

public sealed record UpdateProfileCommand(Guid UserId, string FullName, string? PhoneNumber, Language PreferredLanguage) : ICommand<UserProfile>;

public sealed class UpdateProfileValidator : AbstractValidator<UpdateProfileCommand>
{
    public UpdateProfileValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(120);
        RuleFor(x => x.PhoneNumber).Phone().When(x => !string.IsNullOrEmpty(x.PhoneNumber));
        RuleFor(x => x.PreferredLanguage).IsInEnum();
    }
}

internal sealed class UpdateProfileHandler(IAppDbContext db, IIdentityService identity) : ICommandHandler<UpdateProfileCommand, UserProfile>
{
    public async Task<Result<UserProfile>> Handle(UpdateProfileCommand command, CancellationToken cancellationToken)
    {
        var settings = await db.StoreSettings.AsNoTracking().FirstAsync(cancellationToken);
        var phone = PhoneInput.NormalizeOptional(settings, command.PhoneNumber, "phoneNumber");
        if (phone.IsFailure)
        {
            return phone.Error;
        }

        return await identity.UpdateProfileAsync(command.UserId, command.FullName, phone.Value?.Value, command.PreferredLanguage, cancellationToken);
    }
}

public sealed record ChangePasswordCommand(Guid UserId, string CurrentPassword, string NewPassword) : ICommand;

public sealed class ChangePasswordValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty();
        RuleFor(x => x.NewPassword).NotEmpty().MinimumLength(8).MaximumLength(128).NotEqual(x => x.CurrentPassword);
    }
}

internal sealed class ChangePasswordHandler(IIdentityService identity) : ICommandHandler<ChangePasswordCommand>
{
    public Task<Result> Handle(ChangePasswordCommand command, CancellationToken cancellationToken) =>
        identity.ChangePasswordAsync(command.UserId, command.CurrentPassword, command.NewPassword, cancellationToken);
}
