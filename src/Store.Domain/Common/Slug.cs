using System.Text;
using System.Text.RegularExpressions;

namespace Store.Domain.Common;

/// <summary>URL-safe identifiers for public pages (/products/iphone-16-pro).</summary>
public static partial class Slug
{
    public const int MaxLength = 120;

    public static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= MaxLength && ValidSlug().IsMatch(value);

    /// <summary>Builds a slug from English text. Arabic-only input yields an empty string, so callers must supply English.</summary>
    public static string From(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var normalized = text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(c);
            }
            else if (char.IsWhiteSpace(c) || c is '-' or '_' or '.' or '/' or '+')
            {
                builder.Append('-');
            }
        }

        var slug = CollapseDashes().Replace(builder.ToString(), "-").Trim('-');
        return slug.Length > MaxLength ? slug[..MaxLength].TrimEnd('-') : slug;
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex ValidSlug();

    [GeneratedRegex("-{2,}")]
    private static partial Regex CollapseDashes();
}
