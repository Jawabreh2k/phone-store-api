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
    decimal FlatShippingFee,
    decimal? FreeShippingThreshold,
    bool AllowGuestCheckout);

public sealed record GetAdminSettingsQuery : IQuery<AdminSettingsDto>;

internal sealed class GetAdminSettingsHandler(IAppDbContext db) : IQueryHandler<GetAdminSettingsQuery, AdminSettingsDto>
{
    public async Task<Result<AdminSettingsDto>> Handle(GetAdminSettingsQuery query, CancellationToken cancellationToken)
    {
        var s = await db.StoreSettings.AsNoTracking().FirstAsync(cancellationToken);
        return new AdminSettingsDto(
            LocalizedTextDto.From(s.StoreName), s.LogoUrl, s.SupportPhone, s.WhatsAppNumber, s.SupportEmail,
            s.CurrencyCode, s.PhoneCountryCode, s.FlatShippingFee, s.FreeShippingThreshold, s.AllowGuestCheckout);
    }
}

public sealed record UpdateSettingsCommand(AdminSettingsDto Settings) : ICommand;

public sealed class AdminSettingsValidator : AbstractValidator<AdminSettingsDto>
{
    public AdminSettingsValidator()
    {
        RuleFor(x => x.StoreName).NotNull().SetValidator(new LocalizedTextValidator(80));
        RuleFor(x => x.CurrencyCode).NotEmpty().Matches("^[A-Za-z]{3}$");
        RuleFor(x => x.PhoneCountryCode).NotEmpty().Matches("^[1-9][0-9]{0,2}$").WithMessage("Enter the country calling code without + (e.g. 962).");
        RuleFor(x => x.FlatShippingFee).GreaterThanOrEqualTo(0);
        RuleFor(x => x.FreeShippingThreshold).GreaterThanOrEqualTo(0).When(x => x.FreeShippingThreshold is not null);
        RuleFor(x => x.SupportPhone).Phone().When(x => !string.IsNullOrEmpty(x.SupportPhone));
        RuleFor(x => x.WhatsAppNumber).Phone().When(x => !string.IsNullOrEmpty(x.WhatsAppNumber));
        RuleFor(x => x.SupportEmail).EmailAddress().When(x => !string.IsNullOrEmpty(x.SupportEmail));
        RuleFor(x => x.LogoUrl).MaximumLength(500);
    }
}

public sealed class UpdateSettingsValidator : AbstractValidator<UpdateSettingsCommand>
{
    public UpdateSettingsValidator() => RuleFor(x => x.Settings).NotNull().SetValidator(new AdminSettingsValidator());
}

internal sealed class UpdateSettingsHandler(IAppDbContext db) : ICommandHandler<UpdateSettingsCommand>
{
    public async Task<Result> Handle(UpdateSettingsCommand command, CancellationToken cancellationToken)
    {
        var dto = command.Settings;
        var settings = await db.StoreSettings.FirstAsync(cancellationToken);

        // Region first: the store's own numbers are normalized with the (possibly new) country code.
        settings.UpdateRegion(dto.PhoneCountryCode);
        var supportPhone = PhoneInput.NormalizeOptional(settings, dto.SupportPhone, "settings.supportPhone");
        if (supportPhone.IsFailure)
        {
            return supportPhone.Error;
        }

        var whatsApp = PhoneInput.NormalizeOptional(settings, dto.WhatsAppNumber, "settings.whatsAppNumber");
        if (whatsApp.IsFailure)
        {
            return whatsApp.Error;
        }

        settings.UpdateBranding(dto.StoreName.ToDomain(), dto.LogoUrl);
        settings.UpdateContact(supportPhone.Value, whatsApp.Value, dto.SupportEmail);
        settings.UpdateCommerce(dto.CurrencyCode, dto.FlatShippingFee, dto.FreeShippingThreshold, dto.AllowGuestCheckout);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
