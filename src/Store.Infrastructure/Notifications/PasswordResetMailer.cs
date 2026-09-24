using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Store.Application.Abstractions;
using Store.Domain.Common;

namespace Store.Infrastructure.Notifications;

public sealed class StorefrontOptions
{
    public const string Section = "Storefront";

    /// <summary>
    /// Public address of the web storefront (e.g. https://shop.example), used for links in emails.
    /// Configured rather than read from the request, so a forged Host header cannot redirect a reset link.
    /// </summary>
    public string? BaseUrl { get; set; }
}

/// <summary>Writes the reset email in the customer's language, linking to the storefront's reset-password page.</summary>
internal sealed partial class PasswordResetMailer(
    IAppDbContext db,
    IEmailSender email,
    IOptions<StorefrontOptions> storefront,
    IOptions<DataProtectionTokenProviderOptions> tokens,
    ILogger<PasswordResetMailer> logger) : IPasswordResetMailer
{
    public async Task SendResetLinkAsync(PasswordResetTicket ticket, CancellationToken cancellationToken)
    {
        var baseUrl = storefront.Value.BaseUrl?.TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
        {
            LogStorefrontUrlMissing(logger);
            return;
        }

        var settings = await db.StoreSettings.AsNoTracking().FirstAsync(cancellationToken);
        var language = ticket.PreferredLanguage;
        var storeName = settings.StoreName.In(language);
        var hours = Math.Max(1, (int)tokens.Value.TokenLifespan.TotalHours).ToString(CultureInfo.InvariantCulture);
        var link = $"{baseUrl}/{(language == Language.Ar ? "ar" : "en")}/reset-password" +
            $"?email={Uri.EscapeDataString(ticket.Email)}&token={Uri.EscapeDataString(ticket.Token)}";
        var name = ticket.FullName.Trim();

        var (subject, body) = language == Language.Ar
            ? ($"إعادة تعيين كلمة المرور في {storeName}",
               $"""
               {(name.Length > 0 ? $"مرحباً {name}،" : "مرحباً،")}

               تلقّينا طلباً لإعادة تعيين كلمة المرور لحسابك في {storeName}. افتح هذا الرابط لاختيار كلمة مرور جديدة:

               {link}

               يعمل الرابط مرة واحدة فقط، وتنتهي صلاحيته خلال {hours} ساعة. إذا لم تطلب ذلك، فتجاهل هذه الرسالة وستبقى كلمة مرورك كما هي.
               """)
            : ($"Reset your {storeName} password",
               $"""
               {(name.Length > 0 ? $"Hi {name}," : "Hi,")}

               We received a request to reset the password for your {storeName} account. Open this link to choose a new one:

               {link}

               The link works once and expires in {hours} hours. If you didn't ask for this, you can ignore this email; your password stays the same.
               """);

        await email.SendAsync(new EmailMessage([ticket.Email], subject, body), cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Storefront:BaseUrl is not configured; cannot email a password reset link.")]
    private static partial void LogStorefrontUrlMissing(ILogger logger);
}
