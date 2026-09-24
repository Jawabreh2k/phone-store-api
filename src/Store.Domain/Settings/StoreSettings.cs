using Store.Domain.Common;

namespace Store.Domain.Settings;

/// <summary>
/// Store-wide configuration the owner edits from the admin panel: branding, contact details,
/// currency and shipping. A single row. The store name is data, so the unnamed store can launch
/// with a placeholder and be renamed without a deploy.
/// </summary>
public sealed class StoreSettings : AuditableEntity
{
    /// <summary>Fixed id of the single settings row.</summary>
    public static readonly Guid SingletonId = new("00000000-0000-0000-0000-000000000001");

    private StoreSettings() { } // EF

    public StoreSettings(LocalizedText storeName, string currencyCode, string phoneCountryCode)
    {
        Id = SingletonId;
        StoreName = storeName;
        CurrencyCode = currencyCode;
        AllowGuestCheckout = true;
        TimeZoneId = "Asia/Amman";
        UpdateRegion(phoneCountryCode);
    }

    /// <summary>
    /// Calling code (digits only, e.g. "962") given to phone numbers typed in national form ("079…").
    /// </summary>
    public string PhoneCountryCode { get; private set; } = null!;

    public Result<PhoneNumber> NormalizePhone(string? input) => PhoneNumber.Create(input, PhoneCountryCode);

    public void UpdateRegion(string phoneCountryCode)
    {
        if (phoneCountryCode is not { Length: >= 1 and <= 3 } || !phoneCountryCode.All(char.IsAsciiDigit) || phoneCountryCode[0] == '0')
        {
            throw new ArgumentException("Country calling code must be 1-3 digits, e.g. 962.", nameof(phoneCountryCode));
        }

        PhoneCountryCode = phoneCountryCode;
    }

    public LocalizedText StoreName { get; private set; } = null!;
    public string? LogoUrl { get; private set; }
    public string? SupportPhone { get; private set; }
    public string? WhatsAppNumber { get; private set; }
    public string? SupportEmail { get; private set; }

    /// <summary>ISO 4217 code. Prices are stored with 3 decimals so fils-based currencies (JOD, KWD) fit.</summary>
    public string CurrencyCode { get; private set; } = null!;

    public decimal FlatShippingFee { get; private set; }

    /// <summary>Orders with a subtotal at or above this ship free. Null disables free shipping.</summary>
    public decimal? FreeShippingThreshold { get; private set; }

    public bool AllowGuestCheckout { get; private set; }

    /// <summary>IANA time zone id (e.g. "Asia/Amman") used for "today" in the admin dashboard.</summary>
    public string TimeZoneId { get; private set; } = null!;

    public decimal ShippingFeeFor(decimal subtotal) =>
        FreeShippingThreshold is { } threshold && subtotal >= threshold ? 0m : FlatShippingFee;

    public void Rename(LocalizedText storeName) =>
        StoreName = storeName ?? throw new ArgumentNullException(nameof(storeName));

    /// <summary>Sets (or with null, removes) the uploaded logo shown in the header instead of the store name.</summary>
    public void SetLogo(string? logoUrl) => LogoUrl = logoUrl;

    public void UpdateContact(PhoneNumber? supportPhone, PhoneNumber? whatsAppNumber, string? supportEmail)
    {
        SupportPhone = supportPhone?.Value;
        WhatsAppNumber = whatsAppNumber?.Value;
        SupportEmail = string.IsNullOrWhiteSpace(supportEmail) ? null : supportEmail.Trim();
    }

    public void UpdateCommerce(string currencyCode, decimal flatShippingFee, decimal? freeShippingThreshold, bool allowGuestCheckout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currencyCode);
        ArgumentOutOfRangeException.ThrowIfNegative(flatShippingFee);
        if (freeShippingThreshold is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(freeShippingThreshold));
        }

        CurrencyCode = currencyCode.ToUpperInvariant();
        FlatShippingFee = flatShippingFee;
        FreeShippingThreshold = freeShippingThreshold;
        AllowGuestCheckout = allowGuestCheckout;
    }

    public void UpdateTimeZone(string timeZoneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);
        var id = timeZoneId.Trim();
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(id, out _))
        {
            throw new ArgumentException($"Unknown time zone id: {id}.", nameof(timeZoneId));
        }

        TimeZoneId = id;
    }
}
