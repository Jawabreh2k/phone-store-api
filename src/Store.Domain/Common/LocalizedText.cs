namespace Store.Domain.Common;

public enum Language
{
    En,
    Ar,
}

/// <summary>
/// Text shown to customers in both store languages. Stored as two columns (…En / …Ar);
/// with only two languages a translation table would add joins for no benefit.
/// </summary>
public sealed record LocalizedText
{
    public LocalizedText(string en, string ar)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(en);
        ArgumentException.ThrowIfNullOrWhiteSpace(ar);
        En = en.Trim();
        Ar = ar.Trim();
    }

    public string En { get; private init; }
    public string Ar { get; private init; }

    public string In(Language language) => language == Language.Ar ? Ar : En;
}
