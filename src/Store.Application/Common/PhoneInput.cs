using Store.Domain.Common;
using Store.Domain.Settings;

namespace Store.Application.Common;

/// <summary>Normalizes phone fields with the store's country code, reporting failures against the form field.</summary>
internal static class PhoneInput
{
    public static Result<PhoneNumber> Normalize(StoreSettings settings, string? input, string field)
    {
        var result = settings.NormalizePhone(input);
        return result.IsSuccess ? result : FieldError(field);
    }

    /// <summary>Optional field: blank means "no number".</summary>
    public static Result<PhoneNumber?> NormalizeOptional(StoreSettings settings, string? input, string field)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return Result.Success<PhoneNumber?>(null);
        }

        var result = settings.NormalizePhone(input);
        return result.IsSuccess ? result.Value : Result.Failure<PhoneNumber?>(FieldError(field));
    }

    private static ValidationError FieldError(string field) =>
        new(new Dictionary<string, string[]> { [field] = [PhoneNumber.Invalid.Description] });
}
