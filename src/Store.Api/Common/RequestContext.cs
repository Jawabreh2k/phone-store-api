using System.Globalization;
using Store.Application.Abstractions;
using Store.Application.Carts;
using Store.Domain.Common;

namespace Store.Api.Common;

/// <summary>Reads the language chosen by the request localization middleware (?lang= or Accept-Language).</summary>
internal sealed class RequestLanguageProvider : ILanguageProvider
{
    public Language Current =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar" ? Language.Ar : Language.En;
}

/// <summary>
/// The guest cart id lives in an HttpOnly cookie. It is only an identifier: a guessed id reveals
/// nothing sensitive, and ids are random UUIDv7 values.
/// </summary>
internal static class CartCookie
{
    public const string Name = "store.cart";

    public static Guid? Read(HttpContext context) =>
        Guid.TryParse(context.Request.Cookies[Name], out var id) ? id : null;

    public static void Write(HttpContext context, Guid cartId) =>
        context.Response.Cookies.Append(Name, cartId.ToString(), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            IsEssential = true,
            MaxAge = TimeSpan.FromDays(30),
        });

    public static void Clear(HttpContext context) => context.Response.Cookies.Delete(Name);

    public static CartOwner Owner(HttpContext context, ICurrentUser user) =>
        new(user.UserId, user.UserId is null ? Read(context) : null);
}
