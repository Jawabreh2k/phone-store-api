using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Store.Application.Abstractions;
using Store.Domain.Common;
using Store.Domain.Settings;
using Store.Infrastructure;
using Store.Infrastructure.Notifications;
using Store.Infrastructure.Persistence;
using Store.Infrastructure.Persistence.Seeding;

namespace Store.Application.Tests;

/// <summary>
/// Real application + persistence wiring over an in-memory SQLite database, so tests exercise
/// handlers, validation decorators and EF mappings together. Only edge services are faked.
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    /// <summary>Storefront address that password reset emails link to.</summary>
    public const string StorefrontUrl = "https://shop.test";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private int _orderNumber = 100_000;

    private TestHost(SqliteConnection connection, ServiceProvider provider, DateTimeOffset clockStart)
    {
        _connection = connection;
        _provider = provider;
        Clock = new FakeTimeProvider(clockStart);
    }

    public FakeTimeProvider Clock { get; }
    public FakeCurrentUser User { get; } = new();
    public FakeLanguage Language { get; } = new();
    public FakeFileStorage Files { get; } = new();
    public FakeStaffOrderAlerter StaffAlerts { get; } = new();
    public FakeEmailSender Emails { get; } = new();

    /// <summary>A fresh host and database. <paramref name="configure"/> has the last word on the services, e.g. to change an option for one test.</summary>
    public static async Task<TestHost> CreateAsync(bool withSampleCatalog = true, DateTimeOffset? clockStart = null, Action<IServiceCollection>? configure = null)
    {
        var start = clockStart ?? new DateTimeOffset(2026, 9, 23, 9, 0, 0, TimeSpan.Zero);
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        TestHost? host = null;
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddLogging();
        services.AddScoped<AuditingInterceptor>();
        services.AddDbContext<AppDbContext>((sp, o) => o
            .UseSqlite(connection)
            .AddInterceptors(sp.GetRequiredService<AuditingInterceptor>()));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddSingleton<TimeProvider>(_ => host!.Clock);
        services.AddSingleton<ICurrentUser>(_ => host!.User);
        services.AddSingleton<ILanguageProvider>(_ => host!.Language);
        services.AddSingleton<IOrderNumberGenerator>(_ => new SequentialOrderNumbers(host!));
        services.AddSingleton<IFileStorage>(_ => host!.Files);
        services.AddSingleton<IStaffOrderAlerter>(_ => host!.StaffAlerts);
        services.AddSingleton<IEmailSender>(_ => host!.Emails);
        services.AddSingleton<IDeferredWork, InlineDeferredWork>();

        // Real ASP.NET Identity over the same database, so password reset runs end to end. Signing in needs an HTTP
        // request and stays untested here; AddAuthentication only supplies what SignInManager is built from.
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddAuthentication();
        services.AddIdentityStore();
        services.AddScoped<IPasswordResetMailer, PasswordResetMailer>();
        services.Configure<StorefrontOptions>(o => o.BaseUrl = StorefrontUrl);
        configure?.Invoke(services);

        host = new TestHost(connection, services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }), start);

        await using var scope = host._provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();

        var settings = new StoreSettings(new LocalizedText("PhoneStore", "فون ستور"), "JOD", "962");
        settings.UpdateCommerce("JOD", flatShippingFee: 2m, freeShippingThreshold: 100m, allowGuestCheckout: true);
        db.StoreSettings.Add(settings);
        await db.SaveChangesAsync();

        if (withSampleCatalog)
        {
            await SampleCatalog.SeedAsync(db, CancellationToken.None);
        }

        return host;
    }

    /// <summary>Runs a query or command in its own scope, like one HTTP request.</summary>
    public async Task<TResult> Run<THandler, TResult>(Func<THandler, Task<TResult>> action)
        where THandler : notnull
    {
        await using var scope = _provider.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<THandler>());
    }

    public Task<Result<TResponse>> Send<TCommand, TResponse>(TCommand command) where TCommand : ICommand<TResponse> =>
        Run<ICommandHandler<TCommand, TResponse>, Result<TResponse>>(h => h.Handle(command, CancellationToken.None));

    public Task<Result> Send<TCommand>(TCommand command) where TCommand : ICommand =>
        Run<ICommandHandler<TCommand>, Result>(h => h.Handle(command, CancellationToken.None));

    public Task<Result<TResponse>> Query<TQuery, TResponse>(TQuery query) where TQuery : IQuery<TResponse> =>
        Run<IQueryHandler<TQuery, TResponse>, Result<TResponse>>(h => h.Handle(query, CancellationToken.None));

    /// <summary>Direct database access for arranging data and asserting on state.</summary>
    public async Task<T> Db<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private sealed class SequentialOrderNumbers(TestHost host) : IOrderNumberGenerator
    {
        public Task<string> NextAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Interlocked.Increment(ref host._orderNumber).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}

public sealed class FakeCurrentUser : ICurrentUser
{
    public Guid? UserId { get; set; }
    public HashSet<string> Roles { get; } = [];
    public bool IsAuthenticated => UserId is not null;
    public bool IsInRole(string role) => Roles.Contains(role);
}

public sealed class FakeLanguage : ILanguageProvider
{
    public Language Current { get; set; } = Language.En;
}

/// <summary>Runs deferred mail and alerts before the handler returns, and never lets them fail the request.</summary>
internal sealed class InlineDeferredWork(IServiceScopeFactory scopes) : IDeferredWork
{
    public void Run(Func<IServiceProvider, CancellationToken, Task> work)
    {
        try
        {
            using var scope = scopes.CreateScope();
            work(scope.ServiceProvider, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Same rule as production: a failed alert or email does not fail checkout or password reset.
        }
    }
}

public sealed class FakeStaffOrderAlerter : IStaffOrderAlerter
{
    public bool ThrowOnAlert { get; set; }
    public List<Guid> AlertedOrderIds { get; } = [];

    public Task AlertNewOrderAsync(Guid orderId, CancellationToken cancellationToken)
    {
        if (ThrowOnAlert)
        {
            throw new InvalidOperationException("Simulated alert failure.");
        }

        AlertedOrderIds.Add(orderId);
        return Task.CompletedTask;
    }
}

/// <summary>Records mail instead of sending it.</summary>
public sealed class FakeEmailSender : IEmailSender
{
    public List<EmailMessage> Sent { get; } = [];

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        Sent.Add(message);
        return Task.CompletedTask;
    }
}

public sealed class FakeFileStorage : IFileStorage
{
    public List<string> Saved { get; } = [];
    public List<string> Deleted { get; } = [];

    public Task<string> SaveAsync(Stream content, string extension, string folder, CancellationToken cancellationToken)
    {
        var url = $"/uploads/{folder}/{Guid.NewGuid():N}{extension}";
        Saved.Add(url);
        return Task.FromResult(url);
    }

    public Task DeleteAsync(string url, CancellationToken cancellationToken)
    {
        Deleted.Add(url);
        return Task.CompletedTask;
    }
}
