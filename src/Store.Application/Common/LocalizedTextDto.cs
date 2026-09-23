using FluentValidation;
using Store.Domain.Common;

namespace Store.Application.Common;

/// <summary>Both translations, used by admin screens that edit them side by side.</summary>
public sealed record LocalizedTextDto(string En, string Ar)
{
    public static LocalizedTextDto From(LocalizedText text) => new(text.En, text.Ar);

    public LocalizedText ToDomain() => new(En, Ar);
}

public sealed class LocalizedTextValidator : AbstractValidator<LocalizedTextDto>
{
    public LocalizedTextValidator(int maxLength)
    {
        RuleFor(x => x.En).NotEmpty().MaximumLength(maxLength);
        RuleFor(x => x.Ar).NotEmpty().MaximumLength(maxLength);
    }
}

public static class ValidationRules
{
    /// <summary>
    /// Structural check only (digits, optional +/00, separators, Arabic-Indic digits). Handlers turn the
    /// value into canonical E.164 with the store's country code via <see cref="PhoneInput"/>.
    /// </summary>
    public static IRuleBuilderOptions<T, string?> Phone<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(PhoneNumber.IsPlausible).WithMessage("Enter a valid phone number.");

    public static IRuleBuilderOptions<T, string> ValidSlug<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().Must(Slug.IsValid).WithMessage("Use lowercase letters, numbers and single dashes only.");
}
