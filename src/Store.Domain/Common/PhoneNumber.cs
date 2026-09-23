using System.Text;

namespace Store.Domain.Common;

/// <summary>
/// A phone number in canonical E.164 form (e.g. +962791234567). Customers type numbers many ways
/// ("079 123 4567", "+962 079…", "00962…", Arabic-Indic digits); storing one form means the same
/// number always matches, whether for lookups, admin search or WhatsApp links.
/// </summary>
public sealed record PhoneNumber
{
    public const int MaxLength = 16; // "+" and up to 15 digits (E.164)
    private const int MinDigits = 8;
    private const int MaxDigits = 15;
    // Subscriber digits after the store's own country code (Jordan: 8 for landlines, 9 for mobiles).
    // Foreign numbers only get the loose E.164 bounds: their national lengths vary too much.
    private const int MinNationalDigits = 8;

    private PhoneNumber(string value) => Value = value;

    /// <summary>E.164 form: "+" followed by country code and subscriber number.</summary>
    public string Value { get; }

    public static readonly Error Invalid = Error.Validation("phone.invalid", "Enter a valid phone number.");

    /// <summary>
    /// Normalizes user input. National numbers ("079…") get <paramref name="defaultCountryCode"/>
    /// (the store's country, e.g. "962"); numbers with + or 00 keep their own country code.
    /// </summary>
    public static Result<PhoneNumber> Create(string? input, string defaultCountryCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultCountryCode);
        if (!TryExtract(input, out var digits, out var international))
        {
            return Invalid;
        }

        string e164Digits;
        if (international)
        {
            e164Digits = digits;
        }
        else if (digits.StartsWith('0'))
        {
            var withoutTrunk = digits.TrimStart('0');
            // "0962 79…" is a trunk zero in front of a number that already has the country code.
            e164Digits = IsCountryCodeWithoutPlus(withoutTrunk, defaultCountryCode)
                ? withoutTrunk
                : defaultCountryCode + withoutTrunk;
        }
        else if (IsCountryCodeWithoutPlus(digits, defaultCountryCode))
        {
            e164Digits = digits;
        }
        else
        {
            e164Digits = defaultCountryCode + digits;
        }

        // "+962 079…" and "962 079…" still carry the national trunk zero. It is not part of the subscriber number.
        e164Digits = DropTrunkZero(e164Digits, defaultCountryCode);

        var tooShortForStoreCountry = e164Digits.StartsWith(defaultCountryCode, StringComparison.Ordinal)
            && e164Digits.Length - defaultCountryCode.Length < MinNationalDigits;

        return e164Digits.Length is >= MinDigits and <= MaxDigits && e164Digits[0] != '0' && !tooShortForStoreCountry
            ? new PhoneNumber("+" + e164Digits)
            : Invalid;
    }

    private static bool IsCountryCodeWithoutPlus(string digits, string countryCode) =>
        digits.StartsWith(countryCode, StringComparison.Ordinal) && digits.Length > countryCode.Length + 6;

    /// <summary>Removes one trunk prefix written after the store's country code: 962079… → 96279….</summary>
    private static string DropTrunkZero(string e164Digits, string countryCode)
    {
        if (countryCode.Length == 0)
        {
            return e164Digits;
        }

        var trunkAt = countryCode.Length;
        return e164Digits.StartsWith(countryCode, StringComparison.Ordinal)
            && e164Digits.Length > trunkAt
            && e164Digits[trunkAt] == '0'
            ? string.Concat(countryCode, e164Digits.AsSpan(trunkAt + 1))
            : e164Digits;
    }

    /// <summary>Structural check for validators, independent of the store's country.</summary>
    public static bool IsPlausible(string? input) =>
        TryExtract(input, out var digits, out _) && digits.Length is >= 7 and <= MaxDigits;

    /// <summary>
    /// Keeps digits only (converting Arabic-Indic and Persian digits), and reports whether the
    /// number was written in international form (leading "+" or "00"). Letters make it invalid.
    /// </summary>
    private static bool TryExtract(string? input, out string digits, out bool international)
    {
        digits = string.Empty;
        international = false;
        if (string.IsNullOrWhiteSpace(input) || input.Length > 30)
        {
            return false;
        }

        var trimmed = input.Trim();
        var builder = new StringBuilder(trimmed.Length);
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (c is >= '0' and <= '9')
            {
                builder.Append(c);
            }
            else if (c is >= '٠' and <= '٩')
            {
                builder.Append((char)('0' + (c - '٠'))); // Arabic-Indic ٠-٩
            }
            else if (c is >= '۰' and <= '۹')
            {
                builder.Append((char)('0' + (c - '۰'))); // Persian ۰-۹
            }
            else if (c == '+' && i == 0)
            {
                international = true;
            }
            else if (c is not (' ' or '-' or '.' or '(' or ')' or ' '))
            {
                return false;
            }
        }

        digits = builder.ToString();
        if (!international && digits.StartsWith("00", StringComparison.Ordinal))
        {
            international = true;
            digits = digits[2..];
        }

        return digits.Length > 0;
    }

    /// <summary>
    /// Digits useful for a "contains" search: "079 123" → "79123", so it matches "+96279123…".
    /// When <paramref name="defaultCountryCode"/> is set, a trunk zero after that code is dropped too,
    /// so "+962 079…" finds the number stored as "+96279…".
    /// </summary>
    public static string? SearchDigits(string? input, string? defaultCountryCode = null)
    {
        if (!TryExtract(input, out var digits, out _))
        {
            return null;
        }

        digits = DropTrunkZero(digits, defaultCountryCode ?? string.Empty);
        var core = digits.TrimStart('0');
        return core.Length >= 4 ? core : null;
    }

    public override string ToString() => Value;
}
