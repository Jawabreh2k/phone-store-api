using System.Collections.Concurrent;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Store.Application.Abstractions;
using Store.Application.Notifications;
using Store.Domain.Common;
using Store.Domain.Settings;
using Store.Infrastructure;
using Store.Infrastructure.Outbox;
using Store.Infrastructure.Persistence;
using Store.Infrastructure.Persistence.Seeding;

namespace Store.Application.Tests;

/// <summary>
/// Real application + persistence wiring over an in-memory SQLite database, so tests exercise
/// handlers, validation decorators, EF mappings and the outbox together. Only edge services are faked:
/// notification channels record what they were asked to send instead of sending it.
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    /// <summary>Storefront address that emails link to.</summary>
    public const string StorefrontUrl = "https://shop.test";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private int _orderNumber = 100_000;

    private TestHost(SqliteConnection connection, ServiceProvider provider, DateTimeOffset clockStart, CapturingLoggerProvider logs)
    {
        _connection = connection;
        _provider = provider;
        Clock = new FakeTimeProvider(clockStart);
        Logs = logs;
    }

    public FakeTimeProvider Clock { get; }
    public FakeCurrentUser User { get; } = new();
    public FakeLanguage Language { get; } = new();
    public FakeFileStorage Files { get; } = new();
    public FakeNotificationChannel Email { get; } = new(NotificationChannel.Email);
    public FakeNotificationChannel WhatsApp { get; } = new(NotificationChannel.WhatsApp);

    /// <summary>Every log line written through the app's loggers, formatted.</summary>
    public CapturingLoggerProvider Logs { get; }

    internal IServiceProvider Services => _provider;

    /// <summary>A fresh host and database. <paramref name="configure"/> has the last word on the services, e.g. to change an option for one test.</summary>
    public static async Task<TestHost> CreateAsync(bool withSampleCatalog = true, DateTimeOffset? clockStart = null, Action<IServiceCollection>? configure = null)
    {
        var start = clockStart ?? new DateTimeOffset(2026, 9, 23, 9, 0, 0, TimeSpan.Zero);
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var logs = new CapturingLoggerProvider();

        TestHost? host = null;
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        services.AddPersistenceInterceptors();
        services.AddDbContext<AppDbContext>((sp, o) => o
            .UseSqlite(connection)
            .AddPersistenceInterceptors(sp));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddSingleton<TimeProvider>(_ => host!.Clock);
        services.AddSingleton<ICurrentUser>(_ => host!.User);
        services.AddSingleton<ILanguageProvider>(_ => host!.Language);
        services.AddSingleton<IOrderNumberGenerator>(_ => new SequentialOrderNumbers(host!));
        services.AddSingleton<IFileStorage>(_ => host!.Files);

        // Real ASP.NET Identity over the same database, so password reset runs end to end. Signing in needs an HTTP
        // request and stays untested here; AddAuthentication only supplies what SignInManager is built from.
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddAuthentication();
        services.AddIdentityStore();
        services.Configure<StorefrontOptions>(o => o.BaseUrl = StorefrontUrl);

        // The real outbox (protection, processor, metrics) with recording channels in place of SMTP / Twilio.
        services.AddOutboxCore();
        services.RemoveAll<INotificationChannel>();
        services.AddSingleton<INotificationChannel>(_ => host!.Email);
        services.AddSingleton<INotificationChannel>(_ => host!.WhatsApp);
        services.Configure<NotificationOptions>(o => o.CustomerChannels.Add(NotificationChannel.Email));
        services.AddOptions<OutboxOptions>();
        configure?.Invoke(services);

        host = new TestHost(connection, services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }), start, logs);

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

    /// <summary>Delivers everything due now (the fake clock does not move, so retries wait for the test to advance it).</summary>
    public async Task<int> ProcessOutboxAsync()
    {
        var processor = _provider.GetRequiredService<OutboxProcessor>();
        var total = 0;
        int claimed;
        do
        {
            claimed = await processor.ProcessBatchAsync(CancellationToken.None);
            total += claimed;
        }
        while (claimed > 0);

        return total;
    }

    /// <summary>A second, independent worker (its own <see cref="OutboxProcessor.WorkerId"/>), as on another instance.</summary>
    internal OutboxProcessor NewWorker() => ActivatorUtilities.CreateInstance<OutboxProcessor>(_provider);

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

/// <summary>Records what it is asked to send. <see cref="Results"/> scripts the answers (Sent once it runs out).</summary>
public sealed class FakeNotificationChannel(NotificationChannel channel) : INotificationChannel
{
    public NotificationChannel Channel => channel;
    public List<NotificationEnvelope> Sent { get; } = [];
    public Queue<DeliveryResult> Results { get; } = new();

    /// <summary>Runs (once) during the next send, e.g. to let another worker try the message while this one holds the lease.</summary>
    public Func<Task>? DuringSend { get; set; }

    public async Task<DeliveryResult> SendAsync(NotificationEnvelope envelope, CancellationToken cancellationToken)
    {
        if (DuringSend is { } during)
        {
            DuringSend = null;
            await during();
        }

        var result = Results.Count > 0 ? Results.Dequeue() : DeliveryResult.Sent;
        if (result.Outcome == DeliveryOutcome.Sent)
        {
            Sent.Add(envelope);
        }

        return result;
    }
}

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines;

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var structured = state is IEnumerable<KeyValuePair<string, object?>> values
                ? string.Join(" ", values.Select(v => $"{v.Key}={v.Value}"))
                : string.Empty;
            lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} | {structured} {exception}");
        }
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
