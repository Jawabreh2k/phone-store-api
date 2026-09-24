using Microsoft.Extensions.Configuration;
using Store.Application.Notifications;
using Store.Infrastructure.Notifications;
using Store.Infrastructure.Outbox;

namespace Store.Application.Tests;

public sealed class NotificationOptionsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phones-options-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ATwilioAuthTokenInAppSettings_StopsStartup()
    {
        var configuration = Configuration("""{ "Notifications": { "Messaging": { "Twilio": { "AuthToken": "secret" } } } }""");

        Should.Throw<InvalidOperationException>(() => MessagingOptions.RejectSecretsInAppSettings(configuration))
            .Message.ShouldNotContain("secret\"");
    }

    [Fact]
    public void ATwilioAuthTokenFromTheEnvironment_IsAccepted()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Write("appsettings.json", """{ "Notifications": { "Messaging": { "Provider": "Twilio" } } }"""))
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Notifications:Messaging:Twilio:AuthToken"] = "secret" })
            .Build();

        Should.NotThrow(() => MessagingOptions.RejectSecretsInAppSettings(configuration));
    }

    [Fact]
    public void TheLogProvider_IsRefusedOutsideDevelopment_WhenSmsOrWhatsAppIsOn()
    {
        var notifications = new NotificationOptions();
        notifications.CustomerChannels.Add(NotificationChannel.WhatsApp);
        var messaging = new MessagingOptions { Provider = MessagingProvider.Log };

        messaging.Validate(notifications, isDevelopment: true).ShouldBeEmpty();
        messaging.Validate(notifications, isDevelopment: false).ShouldNotBeEmpty();
    }

    [Fact]
    public void Twilio_NeedsCredentialsAndASenderPerEnabledChannel()
    {
        var notifications = new NotificationOptions();
        notifications.CustomerChannels.AddRange([NotificationChannel.Sms, NotificationChannel.WhatsApp]);
        var messaging = new MessagingOptions { Provider = MessagingProvider.Twilio };

        messaging.Validate(notifications, isDevelopment: false).Count().ShouldBe(3);

        messaging.Twilio.AccountSid = "AC123";
        messaging.Twilio.AuthToken = "token";
        messaging.Twilio.MessagingServiceSid = "MG123";
        messaging.Twilio.WhatsAppFrom = "+14155238886";
        messaging.Validate(notifications, isDevelopment: false).ShouldBeEmpty();
    }

    [Fact]
    public void OutboxOptions_RejectNonsense()
    {
        new OutboxOptions().Validate().ShouldBeEmpty();

        var bad = new OutboxOptions { BatchSize = 0, MaxAttempts = 0, RetentionDays = 0, LeaseDuration = TimeSpan.FromSeconds(1) };
        bad.Backoff.Add(TimeSpan.Zero);
        bad.Validate().Count().ShouldBe(5);
    }

    private IConfiguration Configuration(string appSettings) =>
        new ConfigurationBuilder().AddJsonFile(Write("appsettings.Production.json", appSettings)).Build();

    private string Write(string name, string json)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, json);
        return path;
    }
}
