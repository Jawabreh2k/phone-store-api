using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Store.Application.Abstractions;
using Store.Infrastructure.Identity;
using Store.Infrastructure.Notifications;
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

        services.AddScoped<AuditingInterceptor>();
        services.AddDbContext<AppDbContext>((sp, options) => options
            .UseSqlServer(connectionString, sql =>
            {
                sql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                sql.EnableRetryOnFailure(maxRetryCount: 3);
            })
            .AddInterceptors(sp.GetRequiredService<AuditingInterceptor>()));

        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddScoped<IOrderNumberGenerator, SqlOrderNumberGenerator>();

        services.AddIdentity(configuration, environment);

        services.AddOptions<FileStorageOptions>()
            .Bind(configuration.GetSection(FileStorageOptions.Section))
            .PostConfigure(o => o.RootPath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, o.RootPath)));
        services.AddSingleton<IFileStorage, LocalFileStorage>();

        services.Configure<SeedOptions>(configuration.GetSection(SeedOptions.Section));
        services.Configure<StorefrontOptions>(configuration.GetSection(StorefrontOptions.Section));
        services.AddScoped<IEmailSender, SmtpEmailSender>();
        services.AddScoped<IStaffOrderAlerter, SmtpStaffOrderAlerter>();
        services.AddSingleton<DeferredWorkQueue>();
        services.AddSingleton<IDeferredWork>(sp => sp.GetRequiredService<DeferredWorkQueue>());
        services.AddHostedService(sp => sp.GetRequiredService<DeferredWorkQueue>());
        services.AddScoped<IPasswordResetMailer, PasswordResetMailer>();
        services.AddScoped<DatabaseInitializer>();
        return services;
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
