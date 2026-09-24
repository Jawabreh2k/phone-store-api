using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Store.Application.Notifications;

namespace Store.Infrastructure.Notifications;

/// <summary>
/// Development stand-in for SMS / WhatsApp: logs that a message would have gone out and reports it sent.
/// Only the message id, channel and length are logged, never the text or the phone number.
/// </summary>
internal sealed partial class LogMessagingChannel(NotificationChannel channel, ILogger<LogMessagingChannel> logger) : INotificationChannel
{
    public NotificationChannel Channel => channel;

    public Task<DeliveryResult> SendAsync(NotificationEnvelope envelope, CancellationToken cancellationToken)
    {
        LogWouldSend(logger, channel, envelope.MessageId, envelope.Recipients.Count, envelope.TextBody.Length);
        return Task.FromResult(DeliveryResult.Sent);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[dev] {Channel} message {MessageId} would go to {RecipientCount} recipient(s), {Length} characters.")]
    private static partial void LogWouldSend(ILogger logger, NotificationChannel channel, Guid messageId, int recipientCount, int length);
}

/// <summary>
/// SMS and WhatsApp through Twilio's Messages API. Phones arrive already in E.164 and are used as is.
/// <para>
/// Twilio has no idempotency key, so a crash between Twilio accepting a message and the outbox recording it can send
/// it twice; the outbox accepts that (at least once). WhatsApp free text only reaches customers who wrote to the store
/// in the last 24 hours; outside that window Twilio answers 63016 and the message parks (approved templates are a
/// follow-up). Stored reasons carry Twilio's numeric code only, because Twilio's text can echo the phone number.
/// </para>
/// </summary>
internal sealed class TwilioMessagingChannel(
    NotificationChannel channel,
    IHttpClientFactory httpClients,
    IOptions<MessagingOptions> options) : INotificationChannel
{
    public const string HttpClientName = "twilio";

    public NotificationChannel Channel => channel;

    public async Task<DeliveryResult> SendAsync(NotificationEnvelope envelope, CancellationToken cancellationToken)
    {
        var twilio = options.Value.Twilio;
        if (string.IsNullOrWhiteSpace(twilio.AccountSid) || string.IsNullOrWhiteSpace(twilio.AuthToken))
        {
            return DeliveryResult.Permanent($"{channel} is not configured (Twilio credentials missing).");
        }

        if (envelope.Recipients is not [var phone])
        {
            return DeliveryResult.Permanent($"{channel} messages go to exactly one phone number.");
        }

        var form = new List<KeyValuePair<string, string>> { new("Body", envelope.TextBody) };
        if (channel == NotificationChannel.WhatsApp)
        {
            if (string.IsNullOrWhiteSpace(twilio.WhatsAppFrom))
            {
                return DeliveryResult.Permanent("WhatsApp is not configured (no sender).");
            }

            form.Add(new("To", WhatsApp(phone)));
            form.Add(new("From", WhatsApp(twilio.WhatsAppFrom)));
        }
        else
        {
            form.Add(new("To", phone));
            if (!string.IsNullOrWhiteSpace(twilio.MessagingServiceSid))
            {
                form.Add(new("MessagingServiceSid", twilio.MessagingServiceSid));
            }
            else if (!string.IsNullOrWhiteSpace(twilio.SmsFrom))
            {
                form.Add(new("From", twilio.SmsFrom));
            }
            else
            {
                return DeliveryResult.Permanent("SMS is not configured (no sender).");
            }
        }

        var url = $"{twilio.ApiBaseUrl.TrimEnd('/')}/2010-04-01/Accounts/{Uri.EscapeDataString(twilio.AccountSid)}/Messages.json";
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{twilio.AccountSid}:{twilio.AuthToken}")));

        HttpResponseMessage response;
        try
        {
            response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return DeliveryResult.Temporary("Twilio could not be reached.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DeliveryResult.Temporary("Twilio did not answer in time.");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return DeliveryResult.Sent;
            }

            var status = (int)response.StatusCode;
            var code = await ErrorCodeAsync(response, cancellationToken);
            var reason = code is { } c ? $"Twilio HTTP {status}, error {c}." : $"Twilio HTTP {status}.";

            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || status >= 500)
            {
                // Rate limits and outages pass; rejected credentials are fixed by staff and then retried.
                return DeliveryResult.Temporary(reason);
            }

            // Any other 4xx is a request Twilio will never accept as is, e.g. 21211 invalid number, 21610 unsubscribed,
            // 21408 / 21612 / 21614 unreachable, 63016 outside the WhatsApp window.
            return DeliveryResult.Permanent(reason);
        }
    }

    private static string WhatsApp(string number) =>
        number.StartsWith("whatsapp:", StringComparison.Ordinal) ? number : $"whatsapp:{number}";

    private static async Task<int?> ErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            return json.RootElement.TryGetProperty("code", out var code) && code.TryGetInt32(out var value) ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
