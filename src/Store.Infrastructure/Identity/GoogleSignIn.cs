using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Store.Application.Abstractions;

namespace Store.Infrastructure.Identity;

internal static class GoogleSignIn
{
    /// <summary>
    /// Holds ClientId and ClientSecret. They are deployment secrets: user-secrets in development,
    /// environment variables or a secret store in production, never appsettings.
    /// </summary>
    public const string ConfigurationSection = "Authentication:Google";

    /// <summary>Under /api so the storefront's existing /api rewrite carries Google's redirect through to the API.</summary>
    public const string CallbackPath = "/api/auth/google/callback";

    /// <summary>
    /// Registers Google sign-in when its client id and secret are configured. Without them there is no Google
    /// scheme at all, and starting a Google sign-in answers auth.external_provider_unavailable.
    /// </summary>
    public static AuthenticationBuilder AddGoogleSignIn(this AuthenticationBuilder authentication, IConfiguration configuration, CookieSecurePolicy securePolicy)
    {
        var section = configuration.GetSection(ConfigurationSection);
        var clientId = section["ClientId"];
        var clientSecret = section["ClientSecret"];
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            return authentication;
        }

        // One callback for every way the browser can spell the local site (localhost, 127.0.0.1, a LAN name).
        // Google compares this string exactly with the redirect URI registered for the OAuth client.
        var storefront = configuration["Storefront:BaseUrl"]?.TrimEnd('/');
        var redirectUri = string.IsNullOrEmpty(storefront) ? null : storefront + CallbackPath;

        return authentication.AddGoogle(ExternalProviders.Google, options =>
        {
            options.ClientId = clientId;
            options.ClientSecret = clientSecret;
            options.SignInScheme = IdentityConstants.ExternalScheme;
            options.CallbackPath = CallbackPath;
            options.ClaimActions.MapJsonKey(IdentityService.EmailVerifiedClaim, "email_verified");
            if (redirectUri is not null)
            {
                options.Events.OnRedirectToAuthorizationEndpoint = context =>
                {
                    context.RedirectUri = WithQueryValue(context.RedirectUri, "redirect_uri", redirectUri);
                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                };
            }

            // Google comes back with a top-level GET, which carries Lax cookies, so the correlation cookie needs
            // neither SameSite=None nor, on http://localhost in development, Secure. Same policy as the auth cookie.
            options.CorrelationCookie.SameSite = SameSiteMode.Lax;
            options.CorrelationCookie.SecurePolicy = securePolicy;

            // Declined consent or a failed code exchange: continue to the completion endpoint, which finds no login
            // and sends the shopper back to the store with an error code, rather than ending on a bare 404 or 500.
            // Both URLs come from the data-protected state the API created, so they are always its own local paths.
            options.Events.OnAccessDenied = context =>
            {
                context.Response.Redirect(context.ReturnUrl ?? "/");
                context.HandleResponse();
                return Task.CompletedTask;
            };
            options.Events.OnRemoteFailure = context =>
            {
                context.Response.Redirect(context.Properties?.RedirectUri ?? "/");
                context.HandleResponse();
                return Task.CompletedTask;
            };
        });
    }

    private static string WithQueryValue(string url, string key, string value)
    {
        var split = url.Split('?', 2);
        var pairs = new List<KeyValuePair<string, string?>>();
        var replaced = false;
        foreach (var item in QueryHelpers.ParseQuery(split.Length == 2 ? split[1] : string.Empty))
        {
            if (string.Equals(item.Key, key, StringComparison.Ordinal))
            {
                pairs.Add(new KeyValuePair<string, string?>(key, value));
                replaced = true;
            }
            else
            {
                foreach (var itemValue in item.Value)
                {
                    pairs.Add(new KeyValuePair<string, string?>(item.Key, itemValue));
                }
            }
        }

        if (!replaced)
        {
            pairs.Add(new KeyValuePair<string, string?>(key, value));
        }

        return QueryHelpers.AddQueryString(split[0], pairs);
    }
}
