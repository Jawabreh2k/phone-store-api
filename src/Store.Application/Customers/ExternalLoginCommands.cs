using FluentValidation;
using Store.Application.Abstractions;
using Store.Application.Carts;
using Store.Domain.Common;

namespace Store.Application.Customers;

/// <summary>Sends the browser to the provider's sign-in page; it comes back to <paramref name="ReturnUrl"/>.</summary>
public sealed record StartExternalLoginCommand(string Provider, string ReturnUrl) : ICommand;

public sealed class StartExternalLoginValidator : AbstractValidator<StartExternalLoginCommand>
{
    public StartExternalLoginValidator()
    {
        RuleFor(x => x.Provider).NotEmpty().MaximumLength(50);
        RuleFor(x => x.ReturnUrl).NotEmpty().MaximumLength(2048);
    }
}

internal sealed class StartExternalLoginHandler(IIdentityService identity) : ICommandHandler<StartExternalLoginCommand>
{
    public Task<Result> Handle(StartExternalLoginCommand command, CancellationToken cancellationToken) =>
        identity.ChallengeExternalAsync(command.Provider, command.ReturnUrl);
}

public sealed record ExternalLoginCommand(Guid? GuestCartId, Language? NewAccountLanguage) : ICommand<UserProfile>;

/// <summary>
/// Finishes an external sign-in (creating the account when the email is unused) and keeps whatever was in the
/// guest cart, like a password sign-in. A new account uses <see cref="ExternalLoginCommand.NewAccountLanguage"/>
/// when the start URL carried one, otherwise the language of this request.
/// </summary>
internal sealed class ExternalLoginHandler(IIdentityService identity, ILanguageProvider language, ICommandHandler<MergeGuestCartCommand> mergeCart)
    : ICommandHandler<ExternalLoginCommand, UserProfile>
{
    public async Task<Result<UserProfile>> Handle(ExternalLoginCommand command, CancellationToken cancellationToken)
    {
        var result = await identity.ExternalSignInAsync(command.NewAccountLanguage ?? language.Current, cancellationToken);
        await GuestCartMerge.AfterSignInAsync(mergeCart, result, command.GuestCartId, cancellationToken);
        return result;
    }
}

public sealed record LinkExternalLoginCommand(Guid UserId) : ICommand;

public sealed class LinkExternalLoginValidator : AbstractValidator<LinkExternalLoginCommand>
{
    public LinkExternalLoginValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}

/// <summary>Connects the provider login just returned to the customer who is already signed in.</summary>
internal sealed class LinkExternalLoginHandler(IIdentityService identity) : ICommandHandler<LinkExternalLoginCommand>
{
    public Task<Result> Handle(LinkExternalLoginCommand command, CancellationToken cancellationToken) =>
        identity.LinkExternalLoginAsync(command.UserId, cancellationToken);
}
