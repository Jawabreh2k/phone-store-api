using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Store.Application.Abstractions;
using Store.Application.Common;
using Store.Domain.Common;

namespace Store.Application.Settings;

/// <summary>What the storefront needs for its header, footer and checkout: name, contact and shipping rules.</summary>
public sealed record PublicSettingsDto(
    string StoreName,
    string? LogoUrl,
    string? SupportPhone,
    string? WhatsAppNumber,
    string? SupportEmail,
    string CurrencyCode,
    decimal FlatShippingFee,
    decimal? FreeShippingThreshold,
    bool AllowGuestCheckout);

public sealed record GetPublicSettingsQuery : IQuery<PublicSettingsDto>;

internal sealed class GetPublicSettingsHandler(IAppDbContext db, ILanguageProvider language) : IQueryHandler<GetPublicSettingsQuery, PublicSettingsDto>
{
    public async Task<Result<PublicSettingsDto>> Handle(GetPublicSettingsQuery query, CancellationToken cancellationToken)
    {
        var s = await db.StoreSettings.AsNoTracking().FirstAsync(cancellationToken);
        return new PublicSettingsDto(
            s.StoreName.In(language.Current), s.LogoUrl, s.SupportPhone, s.WhatsAppNumber, s.SupportEmail,
            s.CurrencyCode, s.FlatShippingFee, s.FreeShippingThreshold, s.AllowGuestCheckout);
    }
}

public sealed record AdminSettingsDto(
    LocalizedTextDto StoreName,
    string? LogoUrl,
    string? SupportPhone,
    string? WhatsAppNumber,
    string? SupportEmail,
    string CurrencyCode,
    string PhoneCountryCode,
    string TimeZoneId,
    decimal FlatShippingFee,
    decimal? FreeShippingThreshold,
    bool AllowGuestCheckout,
    string? SmtpHost,
    int SmtpPort,
    string? SmtpFrom,
    string? SmtpUser,
    bool HasSmtpPassword);

public sealed record GetAdminSettingsQuery : IQuery<AdminSettingsDto>;

internal sealed class GetAdminSettingsHandler(IAppDbContext db) : IQueryHandler<GetAdminSettingsQuery, AdminSettingsDto>
{
    public async Task<Result<AdminSettingsDto>> Handle(GetAdminSettingsQuery query, CancellationToken cancellationToken)
    {
        var s = await db.StoreSettings.AsNoTracking().FirstAsync(cancellationToken);
        return new AdminSettingsDto(
            LocalizedTextDto.From(s.StoreName), s.LogoUrl, s.SupportPhone, s.WhatsAppNumber, s.SupportEmail,
            s.CurrencyCode, s.PhoneCountryCode, s.TimeZoneId, s.FlatShippingFee, s.FreeShippingThreshold, s.AllowGuestCheckout,
            s.SmtpHost, s.SmtpPort, s.SmtpFrom, s.SmtpUser, !string.IsNullOrEmpty(s.SmtpPassword));
    }
}

/// <summary>
/// Everything on the settings page except the logo, which has its own upload / remove commands, so saving the
/// form can never put back a logo that was replaced a moment earlier.
/// </summary>
public sealed record UpdateSettingsCommand(
    LocalizedTextDto StoreName,
    string? SupportPhone,
    string? WhatsAppNumber,
    string? SupportEmail,
    string CurrencyCode,
    string PhoneCountryCode,
    string TimeZoneId,
    decimal FlatShippingFee,
    decimal? FreeShippingThreshold,
    bool AllowGuestCheckout,
    string? SmtpHost = null,
    int SmtpPort = 587,
    string? SmtpFrom = null,
    string? SmtpUser = null,
    string? SmtpPassword = null) : ICommand;

public sealed class UpdateSettingsValidator : AbstractValidator<UpdateSettingsCommand>
{
    public UpdateSettingsValidator()
    {
        RuleFor(x => x.StoreName).NotNull().SetValidator(new LocalizedTextValidator(80));
        RuleFor(x => x.CurrencyCode).NotEmpty().Matches("^[A-Za-z]{3}$");
        RuleFor(x => x.PhoneCountryCode).NotEmpty().Matches("^[1-9][0-9]{0,2}$").WithMessage("Enter the country calling code without + (e.g. 962).");
        RuleFor(x => x.FlatShippingFee).GreaterThanOrEqualTo(0).PrecisionScale(18, 3, ignoreTrailingZeros: true);
        RuleFor(x => x.FreeShippingThreshold).GreaterThanOrEqualTo(0).PrecisionScale(18, 3, ignoreTrailingZeros: true)
            .When(x => x.FreeShippingThreshold is not null);
        RuleFor(x => x.SupportPhone).Phone().When(x => !string.IsNullOrEmpty(x.SupportPhone));
        RuleFor(x => x.WhatsAppNumber).Phone().When(x => !string.IsNullOrEmpty(x.WhatsAppNumber));
        RuleFor(x => x.SupportEmail).EmailAddress().MaximumLength(254).When(x => !string.IsNullOrEmpty(x.SupportEmail));
        // Stop at the first failure: the lookup below throws on a null id.
        RuleFor(x => x.TimeZoneId).Cascade(CascadeMode.Stop).NotEmpty().MaximumLength(64)
            .Must(id => TimeZoneInfo.TryFindSystemTimeZoneById(id, out _))
            .WithMessage("Choose a valid time zone.");
        RuleFor(x => x.SmtpHost).MaximumLength(255);
        RuleFor(x => x.SmtpPort).InclusiveBetween(1, 65535);
        RuleFor(x => x.SmtpUser).MaximumLength(255);
        RuleFor(x => x.SmtpPassword).MaximumLength(255);
        // A host with no From address can't send: SmtpClient rejects an empty From at send time.
        RuleFor(x => x.SmtpFrom).NotEmpty().EmailAddress().MaximumLength(254)
            .When(x => !string.IsNullOrWhiteSpace(x.SmtpHost));
    }
}

internal sealed class UpdateSettingsHandler(IAppDbContext db, ISecretProtector secrets) : ICommandHandler<UpdateSettingsCommand>
{
    public async Task<Result> Handle(UpdateSettingsCommand command, CancellationToken cancellationToken)
    {
        var settings = await db.StoreSettings.FirstAsync(cancellationToken);

        // Region first: the store's own numbers are normalized with the (possibly new) country code.
        settings.UpdateRegion(command.PhoneCountryCode);
        var supportPhone = PhoneInput.NormalizeOptional(settings, command.SupportPhone, "supportPhone");
        if (supportPhone.IsFailure)
        {
            return supportPhone.Error;
        }

        var whatsApp = PhoneInput.NormalizeOptional(settings, command.WhatsAppNumber, "whatsAppNumber");
        if (whatsApp.IsFailure)
        {
            return whatsApp.Error;
        }

        settings.Rename(command.StoreName.ToDomain());
        settings.UpdateContact(supportPhone.Value, whatsApp.Value, command.SupportEmail);
        settings.UpdateCommerce(command.CurrencyCode, command.FlatShippingFee, command.FreeShippingThreshold, command.AllowGuestCheckout);
        settings.UpdateTimeZone(command.TimeZoneId);
        // Encrypted at rest, like OutboxEntry.Secret; null keeps the saved one, empty clears it, so only a real
        // password value is protected.
        var protectedPassword = string.IsNullOrEmpty(command.SmtpPassword) ? command.SmtpPassword : secrets.Protect(command.SmtpPassword);
        settings.UpdateSmtp(command.SmtpHost, command.SmtpPort, command.SmtpFrom, command.SmtpUser, protectedPassword);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

// ---------- Logo ----------

/// <summary>Stores an uploaded logo (JPEG, PNG or WebP) and makes it the store's; the previous file is deleted.</summary>
public sealed record UploadStoreLogoCommand(Stream Content, long Length) : ICommand<string>;

internal sealed class UploadStoreLogoHandler(IAppDbContext db, IFileStorage storage) : ICommandHandler<UploadStoreLogoCommand, string>
{
    public async Task<Result<string>> Handle(UploadStoreLogoCommand command, CancellationToken cancellationToken)
    {
        var extension = ImageFile.Validate(command.Content, command.Length, out var error);
        if (extension is null)
        {
            return error!;
        }

        var settings = await db.StoreSettings.FirstAsync(cancellationToken);
        var previous = settings.LogoUrl;
        var url = await storage.SaveAsync(command.Content, extension, "store", cancellationToken);
        settings.SetLogo(url);
        await db.SaveChangesAsync(cancellationToken);

        // Only once nothing points at it any more.
        if (previous is not null)
        {
            await storage.DeleteAsync(previous, cancellationToken);
        }

        return url;
    }
}

/// <summary>Goes back to the store name as text in the header.</summary>
public sealed record RemoveStoreLogoCommand : ICommand;

internal sealed class RemoveStoreLogoHandler(IAppDbContext db, IFileStorage storage) : ICommandHandler<RemoveStoreLogoCommand>
{
    public async Task<Result> Handle(RemoveStoreLogoCommand command, CancellationToken cancellationToken)
    {
        var settings = await db.StoreSettings.FirstAsync(cancellationToken);
        if (settings.LogoUrl is not { } previous)
        {
            return Result.Success();
        }

        settings.SetLogo(null);
        await db.SaveChangesAsync(cancellationToken);
        await storage.DeleteAsync(previous, cancellationToken);
        return Result.Success();
    }
}
