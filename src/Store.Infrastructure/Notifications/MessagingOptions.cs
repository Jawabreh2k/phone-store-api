using Microsoft.Extensions.Configuration;
using Store.Application.Notifications;

namespace Store.Infrastructure.Notifications;

public enum MessagingProvider
{
    /// <summary>Development only: records that a message would have gone out, without its text or recipient.</summary>
    Log = 1,
    Twilio = 2,
}

/// <summary>
/// SMS / WhatsApp delivery (<c>Notifications:Messaging</c>). Credentials come from user-secrets or environment variables
/// (<c>Notifications__Messaging__Twilio__AuthToken</c>); startup fails if the auth token is found in an appsettings file.
/// </summary>
public sealed class MessagingOptions
{
    public const string Section = "Notifications:Messaging";

    public MessagingProvider Provider { get; set; } = MessagingProvider.Log;

    public TwilioOptions Twilio { get; set; } = new();

    public IEnumerable<string> Validate(NotificationOptions notifications, bool isDevelopment)
    {
        var sms = notifications.CustomerChannels.Contains(NotificationChannel.Sms);
        var whatsApp = notifications.CustomerChannels.Contains(NotificationChannel.WhatsApp);
        if (!sms && !whatsApp)
        {
            yield break;
        }

        if (Provider == MessagingProvider.Log)
        {
            if (!isDevelopment)
            {
                yield return "Notifications:Messaging:Provider is Log, which delivers nothing; set it to Twilio or remove Sms/WhatsApp from Notifications:CustomerChannels.";
            }

            yield break;
        }

        if (string.IsNullOrWhiteSpace(Twilio.AccountSid) || string.IsNullOrWhiteSpace(Twilio.AuthToken))
        {
            yield return "Twilio needs Notifications:Messaging:Twilio:AccountSid and :AuthToken (from user-secrets or environment variables).";
        }

        if (!Uri.TryCreate(Twilio.ApiBaseUrl, UriKind.Absolute, out var api) || api.Scheme != Uri.UriSchemeHttps)
        {
            yield return "Notifications:Messaging:Twilio:ApiBaseUrl must be an absolute https URL.";
        }

        if (sms && string.IsNullOrWhiteSpace(Twilio.SmsFrom) && string.IsNullOrWhiteSpace(Twilio.MessagingServiceSid))
        {
            yield return "SMS needs Notifications:Messaging:Twilio:SmsFrom (E.164) or :MessagingServiceSid.";
        }

        if (whatsApp && string.IsNullOrWhiteSpace(Twilio.WhatsAppFrom))
        {
            yield return "WhatsApp needs Notifications:Messaging:Twilio:WhatsAppFrom (the approved sender, E.164).";
        }
    }

    /// <summary>Throws when a secret is set in an appsettings*.json file instead of user-secrets or the environment.</summary>
    public static void RejectSecretsInAppSettings(IConfiguration configuration)
    {
        if (configuration is not IConfigurationRoot root)
        {
            return;
        }

        const string key = Section + ":Twilio:AuthToken";
        foreach (var provider in root.Providers.OfType<FileConfigurationProvider>())
        {
            var file = Path.GetFileName(provider.Source.Path ?? string.Empty);
            if (file.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
                && provider.TryGet(key, out var value)
                && !string.IsNullOrEmpty(value))
            {
                throw new InvalidOperationException(
                    $"{key} must not be stored in {file}. Use user-secrets in development or an environment variable in production.");
            }
        }
    }
}

public sealed class TwilioOptions
{
    public string? AccountSid { get; set; }

    public string? AuthToken { get; set; }

    /// <summary>The SMS sender (E.164). Ignored when <see cref="MessagingServiceSid"/> is set.</summary>
    public string? SmsFrom { get; set; }

    public string? MessagingServiceSid { get; set; }

    /// <summary>The approved WhatsApp sender (E.164, without the <c>whatsapp:</c> prefix).</summary>
    public string? WhatsAppFrom { get; set; }

    public string ApiBaseUrl { get; set; } = "https://api.twilio.com";
}
