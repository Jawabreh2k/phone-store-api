using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Store.Application.Abstractions;
using Store.Domain.Common;
using Store.Domain.Settings;
using Store.Infrastructure.Identity;

namespace Store.Infrastructure.Persistence.Seeding;

public sealed class SeedOptions
{
    public const string Section = "Seed";

    /// <summary>Applies pending EF migrations at startup. Convenient in development; prefer a deploy step in production.</summary>
    public bool ApplyMigrations { get; set; }

    /// <summary>Loads the demo catalogue when the database has no products yet.</summary>
    public bool SampleData { get; set; }

    /// <summary>First admin account. Keep the password in user-secrets or an environment variable, never in appsettings.</summary>
    public string? AdminEmail { get; set; }
    public string? AdminPassword { get; set; }
}

/// <summary>Brings a database to a usable state: schema, roles, settings row, first admin and optional demo data.</summary>
public sealed partial class DatabaseInitializer(
    AppDbContext db,
    RoleManager<IdentityRole<Guid>> roles,
    UserManager<ApplicationUser> users,
    IOptions<SeedOptions> options,
    TimeProvider clock,
    ILogger<DatabaseInitializer> logger)
{
    private readonly SeedOptions _options = options.Value;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_options.ApplyMigrations)
        {
            await db.Database.MigrateAsync(cancellationToken);
        }

        await EnsureRolesAsync();
        await EnsureSettingsAsync(cancellationToken);
        await EnsureAdminAsync();

        if (_options.SampleData && !await db.Products.AnyAsync(cancellationToken))
        {
            await SampleCatalog.SeedAsync(db, cancellationToken);
            LogSampleDataSeeded(logger);
        }
    }

    private async Task EnsureRolesAsync()
    {
        foreach (var role in new[] { Roles.Admin, Roles.Customer })
        {
            if (!await roles.RoleExistsAsync(role))
            {
                await roles.CreateAsync(new IdentityRole<Guid>(role));
            }
        }
    }

    private async Task EnsureSettingsAsync(CancellationToken cancellationToken)
    {
        if (await db.StoreSettings.AnyAsync(cancellationToken))
        {
            return;
        }

        // Placeholder branding until the client picks a name. Editable from the admin panel.
        var settings = new StoreSettings(new LocalizedText("PhoneStore", "فون ستور"), "JOD", phoneCountryCode: "962");
        settings.UpdateCommerce("JOD", flatShippingFee: 2m, freeShippingThreshold: 100m, allowGuestCheckout: true);
        db.StoreSettings.Add(settings);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureAdminAsync()
    {
        if (string.IsNullOrWhiteSpace(_options.AdminEmail) || string.IsNullOrWhiteSpace(_options.AdminPassword))
        {
            if ((await users.GetUsersInRoleAsync(Roles.Admin)).Count == 0)
            {
                LogNoAdmin(logger);
            }

            return;
        }

        if (await users.FindByEmailAsync(_options.AdminEmail) is not null)
        {
            return;
        }

        var admin = new ApplicationUser
        {
            UserName = _options.AdminEmail,
            Email = _options.AdminEmail,
            EmailConfirmed = true,
            FullName = "Store Admin",
            CreatedAt = clock.GetUtcNow(),
        };

        var result = await users.CreateAsync(admin, _options.AdminPassword);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException("Could not create the admin account: " +
                string.Join(" ", result.Errors.Select(e => e.Description)));
        }

        await users.AddToRoleAsync(admin, Roles.Admin);
        LogAdminCreated(logger, _options.AdminEmail);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded the sample catalogue.")]
    private static partial void LogSampleDataSeeded(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No admin account exists. Set Seed:AdminEmail and Seed:AdminPassword (user-secrets) to create one.")]
    private static partial void LogNoAdmin(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created admin account {Email}.")]
    private static partial void LogAdminCreated(ILogger logger, string email);
}
