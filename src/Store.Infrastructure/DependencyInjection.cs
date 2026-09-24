using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Store.Application.Abstractions;
using Store.Application.Notifications;
using Store.Infrastructure.Identity;
using Store.Infrastructure.Notifications;
using Store.Infrastructure.Outbox;
using Store.Infrastructure.Persistence;
using Store.Infrastructure.Persistence.Seeding;
using Store.Infrastructure.Storage;

namespace Store.Infrastructure;

public static class DependencyInjection
{
    public const string AuthCookieName = "store.auth";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var connectionString = configuration.GetConnectionString("Store")
            ?? throw new InvalidOperationException("Connection string 'Store' is not configured.");

        services.AddPersistenceInterceptors();
        services.AddDbContext<AppDbContext>((sp, options) => options
            .UseSqlServer(connectionString, sql =>
            {
                sql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                sql.EnableRetryOnFailure(maxRetryCount: 3);
            })
            .AddPersistenceInterceptors(sp));

        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        // One key ring for every instance: an outbox secret protected by one API instance is sent by another.
        services.AddDataProtection()
            .SetApplicationName("PhoneStore")
            .PersistKeysToDbContext<AppDbContext>();
        services.AddScoped<IOrderNumberGenerator, SqlOrderNumberGenerator>();

        services.AddIdentity(configuration, environment);

        services.AddOptions<FileStorageOptions>()
            .Bind(configuration.GetSection(FileStorageOptions.Section))
            .PostConfigure(o => o.RootPath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, o.RootPath)));
        services.AddSingleton<IFileStorage, LocalFileStorage>();

        services.Configure<SeedOptions>(configuration.GetSection(SeedOptions.Section));
        services.Configure<StorefrontOptions>(configuration.GetSection(StorefrontOptions.Section));
        services.AddNotifications(configuration, environment);
        services.AddScoped<DatabaseInitializer>();
        return services;
    }

    /// <summary>Adds the outbox health check (Degraded when delivery falls behind) to the app's health checks.</summary>
    public static IHealthChecksBuilder AddOutboxHealthCheck(this IHealthChecksBuilder builder) =>
        builder.AddCheck<OutboxHealthCheck>(OutboxHealthCheck.Name, HealthStatus.Degraded, ["outbox"]);

    /// <summary>The save-time interceptors: auditing, and domain events to outbox messages.</summary>
    internal static void AddPersistenceInterceptors(this IServiceCollection services)
    {
        services.AddScoped<AuditingInterceptor>();
        services.AddScoped<DomainEventsInterceptor>();
    }

    internal static DbContextOptionsBuilder AddPersistenceInterceptors(this DbContextOptionsBuilder options, IServiceProvider services) =>
        options.AddInterceptors(services.GetRequiredService<AuditingInterceptor>(), services.GetRequiredService<DomainEventsInterceptor>());

    /// <summary>
    /// The outbox machinery shared by the app and the application tests: secret protection, processing, metrics and the
    /// email channel. The hosted workers and SMS / WhatsApp channels are added by <see cref="AddNotifications"/>.
    /// </summary>
    internal static void AddOutboxCore(this IServiceCollection services)
    {
        services.AddMetrics();
        services.AddSingleton<OutboxMetrics>();
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        services.AddSingleton<OutboxProcessor>();
        services.AddSingleton<OutboxCleanupWorker>();
        services.AddScoped<INotificationChannel, SmtpEmailChannel>();
    }

    private static void AddNotifications(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        MessagingOptions.RejectSecretsInAppSettings(configuration);

        services.AddOptions<OutboxOptions>()
            .Bind(configuration.GetSection(OutboxOptions.Section))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<OutboxOptions>>(new OutboxOptionsValidator());

        services.AddOptions<NotificationOptions>()
            .Bind(configuration.GetSection(NotificationOptions.Section))
            .ValidateOnStart();

        services.AddOptions<MessagingOptions>()
            .Bind(configuration.GetSection(MessagingOptions.Section))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MessagingOptions>>(sp =>
            new MessagingOptionsValidator(sp.GetRequiredService<IOptions<NotificationOptions>>(), environment.IsDevelopment()));

        services.AddOutboxCore();
        services.AddHostedService<OutboxWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<OutboxCleanupWorker>());

        var provider = configuration.GetSection(MessagingOptions.Section).Get<MessagingOptions>()?.Provider ?? MessagingProvider.Log;
        if (provider == MessagingProvider.Twilio)
        {
            services.AddHttpClient(TwilioMessagingChannel.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
            foreach (var channel in (NotificationChannel[])[NotificationChannel.Sms, NotificationChannel.WhatsApp])
            {
                services.AddScoped<INotificationChannel>(sp => new TwilioMessagingChannel(
                    channel, sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<IOptions<MessagingOptions>>()));
            }
        }
        else
        {
            foreach (var channel in (NotificationChannel[])[NotificationChannel.Sms, NotificationChannel.WhatsApp])
            {
                services.AddScoped<INotificationChannel>(sp => new LogMessagingChannel(
                    channel, sp.GetRequiredService<ILogger<LogMessagingChannel>>()));
            }
        }
    }

    private sealed class OutboxOptionsValidator : IValidateOptions<OutboxOptions>
    {
        public ValidateOptionsResult Validate(string? name, OutboxOptions options) =>
            options.Validate().ToList() is { Count: > 0 } errors ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }

    private sealed class MessagingOptionsValidator(IOptions<NotificationOptions> notifications, bool isDevelopment) : IValidateOptions<MessagingOptions>
    {
        public ValidateOptionsResult Validate(string? name, MessagingOptions options) =>
            options.Validate(notifications.Value, isDevelopment).ToList() is { Count: > 0 } errors
                ? ValidateOptionsResult.Fail(errors)
                : ValidateOptionsResult.Success;
    }

    /// <summary>
    /// Accounts, roles, reset tokens and sign-in over the app database, without the HTTP cookie setup,
    /// so the application tests run the real thing.
    /// </summary>
    internal static void AddIdentityStore(this IServiceCollection services)
    {
        services.AddScoped<IIdentityService, IdentityService>();
        services
            .AddIdentityCore<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 8;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireUppercase = false;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();
    }

    private static void AddIdentity(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddIdentityStore();

        // Every cookie the API sets is https-only outside development (the storefront proxies /api, so they are all first-party).
        var securePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;

        var authentication = services.AddAuthentication(IdentityConstants.ApplicationScheme);
        authentication.AddIdentityCookies();
        authentication.AddGoogleSignIn(configuration, securePolicy);

        // Holds the provider's answer between Google's callback and /api/auth/google/complete (Identity gives it 5 minutes).
        services.ConfigureExternalCookie(cookie => cookie.Cookie.SecurePolicy = securePolicy);

        services.ConfigureApplicationCookie(cookie =>
        {
            cookie.Cookie.Name = AuthCookieName;
            cookie.Cookie.HttpOnly = true;

            // Lax (not Strict) so a customer opening a shared product link arrives signed in.
            // Cross-site POSTs don't carry the cookie, and the API only accepts JSON, which blocks classic CSRF.
            cookie.Cookie.SameSite = SameSiteMode.Lax;
            cookie.Cookie.SecurePolicy = securePolicy;
            cookie.ExpireTimeSpan = TimeSpan.FromDays(14);
            cookie.SlidingExpiration = true;

            // An API answers with status codes; the frontend decides where to send the user.
            cookie.Events.OnRedirectToLogin = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            cookie.Events.OnRedirectToAccessDenied = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });
    }
}
