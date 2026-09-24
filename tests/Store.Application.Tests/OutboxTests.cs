using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Store.Application.Abstractions;
using Store.Application.Carts;
using Store.Application.Notifications;
using Store.Application.Orders;
using Store.Application.Outbox;
using Store.Domain.Common;
using Store.Domain.Orders;
using Store.Infrastructure.Identity;
using Store.Infrastructure.Outbox;
using Store.Infrastructure.Persistence;

namespace Store.Application.Tests;

/// <summary>The transactional outbox end to end: enqueue with the business change, claim, deliver, retry, park.</summary>
public sealed class OutboxTests : IAsyncLifetime
{
    private const string CustomerEmail = "sara@example.com";

    private TestHost _host = null!;

    public async Task InitializeAsync() => _host = await TestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    // ---- enqueue -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PlacingAnOrder_WritesItsMessagesInTheSameSave()
    {
        var placed = await PlaceGuestOrderAsync();

        var messages = await Messages();
        messages.Select(m => m.DeduplicationKey).ShouldBe(
            [$"order:{placed.OrderId}:placed:customer:email", $"order:{placed.OrderId}:placed:staff"],
            ignoreOrder: true);
        messages.ShouldAllBe(m => m.OrderId == placed.OrderId && m.Status == OutboxStatus.Pending && m.Attempts == 0);
        messages.Single(m => m.Type == OutboxTypes.CustomerOrderNotification).Template.ShouldBe(NotificationTemplate.OrderPlaced);
    }

    [Fact]
    public async Task WhenTheOrderSaveFails_NoMessageIsWritten()
    {
        // Every order gets the same number, so the second order's save hits the unique index and rolls back.
        await _host.DisposeAsync();
        _host = await TestHost.CreateAsync(configure: s =>
        {
            s.RemoveAll<IOrderNumberGenerator>();
            s.AddSingleton<IOrderNumberGenerator, ConstantOrderNumber>();
        });
        var first = await PlaceGuestOrderAsync();

        await Should.ThrowAsync<DbUpdateException>(() => PlaceGuestOrderAsync());

        (await _host.Db(db => db.Orders.CountAsync())).ShouldBe(1);
        (await Messages()).ShouldAllBe(m => m.OrderId == first.OrderId);
        (await Messages()).Count.ShouldBe(2);
    }

    [Fact]
    public async Task EnqueueingTheSameKeyTwice_InOneUnitOfWork_WritesOneRow()
    {
        await _host.Run<IServiceProvider, int>(async sp =>
        {
            var outbox = sp.GetRequiredService<IOutbox>();
            outbox.Enqueue(new OutboxEntry("test.type", new { n = 1 }, "test:dup"));
            outbox.Enqueue(new OutboxEntry("test.type", new { n = 2 }, "test:dup"));
            return await sp.GetRequiredService<IAppDbContext>().SaveChangesAsync(CancellationToken.None);
        });

        (await Messages()).ShouldHaveSingleItem().DeduplicationKey.ShouldBe("test:dup");
    }

    [Fact]
    public async Task EnqueueingAKeyThatIsAlreadyStored_IsANoOp_AndTheBusinessChangeStillCommits()
    {
        await EnqueueAsync("test:stored");

        await _host.Run<IServiceProvider, int>(async sp =>
        {
            var db = sp.GetRequiredService<IAppDbContext>();
            var settings = await db.StoreSettings.FirstAsync();
            settings.UpdateCommerce("JOD", flatShippingFee: 3m, freeShippingThreshold: 100m, allowGuestCheckout: true);
            var outbox = sp.GetRequiredService<IOutbox>();
            outbox.Enqueue(new OutboxEntry("test.type", new { }, "test:stored"));
            outbox.Enqueue(new OutboxEntry("test.type", new { }, "test:new"));
            return await db.SaveChangesAsync(CancellationToken.None);
        });

        (await Messages()).Select(m => m.DeduplicationKey).ShouldBe(["test:stored", "test:new"], ignoreOrder: true);
        (await _host.Db(db => db.StoreSettings.Select(s => s.FlatShippingFee).SingleAsync())).ShouldBe(3m);
    }

    [Fact]
    public async Task AdminStatusChanges_EnqueueExactlyOneMessagePerChannel()
    {
        await UseChannelsAsync(NotificationChannel.Email, NotificationChannel.WhatsApp);
        var placed = await PlaceGuestOrderAsync();
        _host.User.UserId = Guid.NewGuid();
        _host.User.Roles.Add(Roles.Admin);

        (await _host.Send(new ChangeOrderStatusCommand(placed.OrderId, OrderStatus.Confirmed, null))).IsSuccess.ShouldBeTrue();
        (await _host.Send(new ChangeOrderStatusCommand(placed.OrderId, OrderStatus.Shipped, null))).IsSuccess.ShouldBeTrue();

        var statusMessages = (await Messages()).Where(m => m.DeduplicationKey.Contains(":status:", StringComparison.Ordinal)).ToList();
        statusMessages.Count.ShouldBe(4);
        statusMessages.Count(m => m.Template == NotificationTemplate.OrderConfirmed && m.Channel == NotificationChannel.Email).ShouldBe(1);
        statusMessages.Count(m => m.Template == NotificationTemplate.OrderConfirmed && m.Channel == NotificationChannel.WhatsApp).ShouldBe(1);
        statusMessages.Count(m => m.Template == NotificationTemplate.OrderShipped && m.Channel == NotificationChannel.Email).ShouldBe(1);
        statusMessages.Count(m => m.Template == NotificationTemplate.OrderShipped && m.Channel == NotificationChannel.WhatsApp).ShouldBe(1);
    }

    [Fact]
    public async Task ACustomerCancel_EnqueuesExactlyOneMessagePerChannel_WordedAsTheCustomersRequest()
    {
        await UseChannelsAsync(NotificationChannel.Email, NotificationChannel.WhatsApp);
        var customerId = await CreateUserAsync("c@test.local", role: null);
        var placed = await PlaceOrderAsync(new CartOwner(customerId, null));

        (await _host.Send(new CancelMyOrderCommand(customerId, placed.OrderNumber, "Changed my mind"))).IsSuccess.ShouldBeTrue();

        var cancelled = (await Messages()).Where(m => m.Template == NotificationTemplate.OrderCancelled).ToList();
        cancelled.Select(m => m.Channel).ShouldBe([NotificationChannel.Email, NotificationChannel.WhatsApp], ignoreOrder: true);

        await _host.ProcessOutboxAsync();
        var mail = _host.Email.Sent.Single(e => e.Subject.Contains("cancelled", StringComparison.Ordinal));
        mail.TextBody.ShouldContain("As you asked");
        mail.TextBody.ShouldContain($"{TestHost.StorefrontUrl}/en/account/orders/{placed.OrderNumber}");
        _host.WhatsApp.Sent.Count(e => e.Subject.Contains("cancelled", StringComparison.Ordinal)).ShouldBe(1);
    }

    // ---- delivery ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheWorker_SendsOnce_AndRecordsIt()
    {
        var placed = await PlaceGuestOrderAsync();

        await _host.ProcessOutboxAsync();
        await _host.ProcessOutboxAsync();

        var mail = _host.Email.Sent.Where(e => e.Recipients.Contains(CustomerEmail)).ShouldHaveSingleItem();
        mail.Subject.ShouldBe($"We received your order {placed.OrderNumber}");
        var message = await CustomerMessage(placed.OrderId);
        message.Status.ShouldBe(OutboxStatus.Sent);
        message.Attempts.ShouldBe(1);
        message.ProcessedAt.ShouldBe(_host.Clock.GetUtcNow());
        message.LockedBy.ShouldBeNull();
        message.LockedUntil.ShouldBeNull();
        message.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task ATemporaryFailure_IsRetriedAfterABackoff()
    {
        var placed = await PlaceGuestOrderAsync();
        _host.Email.Results.Enqueue(DeliveryResult.Temporary("SMTP 421 ServiceNotAvailable."));
        var start = _host.Clock.GetUtcNow();

        await _host.ProcessOutboxAsync();

        var message = await CustomerMessage(placed.OrderId);
        message.Status.ShouldBe(OutboxStatus.Pending);
        message.Attempts.ShouldBe(1);
        message.LastError.ShouldBe("SMTP 421 ServiceNotAvailable.");
        message.NextAttemptAt.ShouldBeInRange(start + TimeSpan.FromSeconds(24), start + TimeSpan.FromSeconds(36));

        // Not due yet.
        await _host.ProcessOutboxAsync();
        _host.Email.Sent.ShouldNotContain(e => e.Recipients.Contains(CustomerEmail));

        _host.Clock.Advance(TimeSpan.FromSeconds(40));
        await _host.ProcessOutboxAsync();

        _host.Email.Sent.Where(e => e.Recipients.Contains(CustomerEmail)).ShouldHaveSingleItem();
        message = await CustomerMessage(placed.OrderId);
        message.Status.ShouldBe(OutboxStatus.Sent);
        message.Attempts.ShouldBe(2);
    }

    [Fact]
    public async Task AMessage_ParksAfterTheMaximumAttempts_AndSaysSoInTheLog()
    {
        await _host.DisposeAsync();
        _host = await TestHost.CreateAsync(configure: s => s.Configure<OutboxOptions>(o => o.MaxAttempts = 3));
        var placed = await PlaceGuestOrderAsync();
        for (var i = 0; i < 3; i++)
        {
            _host.Email.Results.Enqueue(DeliveryResult.Temporary("SMTP connection failed (SocketException)."));
        }

        for (var i = 0; i < 3; i++)
        {
            await _host.ProcessOutboxAsync();
            _host.Clock.Advance(TimeSpan.FromHours(7));
        }

        await _host.ProcessOutboxAsync();
        var message = await CustomerMessage(placed.OrderId);
        message.Status.ShouldBe(OutboxStatus.Parked);
        message.Attempts.ShouldBe(3);
        message.ProtectedSecret.ShouldBeNull();
        _host.Email.Sent.ShouldNotContain(e => e.Recipients.Contains(CustomerEmail));
        _host.Logs.Lines.ShouldContain(l => l.StartsWith("Warning", StringComparison.Ordinal) && l.Contains($"{message.Id}", StringComparison.Ordinal) && l.Contains("parked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APermanentFailure_ParksAtOnce()
    {
        var placed = await PlaceGuestOrderAsync();
        _host.Email.Results.Enqueue(DeliveryResult.Permanent("SMTP 550 RecipientNotAccepted."));

        await _host.ProcessOutboxAsync();

        var message = await CustomerMessage(placed.OrderId);
        message.Status.ShouldBe(OutboxStatus.Parked);
        message.Attempts.ShouldBe(1);
        message.LastError.ShouldBe("SMTP 550 RecipientNotAccepted.");
        message.ProtectedSecret.ShouldBeNull();
    }

    [Fact]
    public async Task ActiveLease_KeepsAnotherWorkerOff()
    {
        var placed = await PlaceGuestOrderAsync();
        await _host.Db(db => db.OutboxMessages.Where(m => m.Type == OutboxTypes.StaffNewOrderAlert).ExecuteDeleteAsync());
        var workerA = _host.NewWorker();
        var workerB = _host.NewWorker();
        var claimedByB = -1;
        _host.Email.DuringSend = async () => claimedByB = await workerB.ProcessBatchAsync(CancellationToken.None);

        await workerA.ProcessBatchAsync(CancellationToken.None);

        claimedByB.ShouldBe(0);
        _host.Email.Sent.Where(e => e.Recipients.Contains(CustomerEmail)).ShouldHaveSingleItem();
        var message = await CustomerMessage(placed.OrderId);
        message.Status.ShouldBe(OutboxStatus.Sent);
        message.Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task ExpiredLease_LetsAnotherWorkerTakeOver_AndTheFirstCannotOverwriteIt()
    {
        var placed = await PlaceGuestOrderAsync();
        await _host.Db(db => db.OutboxMessages.Where(m => m.Type == OutboxTypes.StaffNewOrderAlert).ExecuteDeleteAsync());
        var workerA = _host.NewWorker();
        var workerB = _host.NewWorker();
        _host.Email.DuringSend = async () =>
        {
            // A stalls past its lease; B takes the message and delivers it.
            _host.Clock.Advance(TimeSpan.FromMinutes(3));
            (await workerB.ProcessBatchAsync(CancellationToken.None)).ShouldBe(1);
        };

        await workerA.ProcessBatchAsync(CancellationToken.None);

        // At least once: both sends happened; the row records B's result, and A noticed it had lost the lease.
        _host.Email.Sent.Count(e => e.Recipients.Contains(CustomerEmail)).ShouldBe(2);
        var message = await CustomerMessage(placed.OrderId);
        message.Status.ShouldBe(OutboxStatus.Sent);
        message.Attempts.ShouldBe(2);
        message.LockedBy.ShouldBeNull();
        _host.Logs.Lines.ShouldContain(l => l.Contains(workerA.WorkerId, StringComparison.Ordinal) && l.Contains("no longer held the lease", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheStaffAlert_GoesToEveryAdmin_InEnglish()
    {
        await CreateUserAsync("owner@store.test", Roles.Admin);
        await CreateUserAsync("manager@store.test", Roles.Admin);
        _host.Language.Current = Language.Ar;
        var placed = await PlaceGuestOrderAsync();

        await _host.ProcessOutboxAsync();

        var alert = _host.Email.Sent.Single(e => e.Subject == $"New order {placed.OrderNumber}");
        alert.Recipients.ShouldBe(["owner@store.test", "manager@store.test"], ignoreOrder: true);
        alert.TextBody.ShouldContain("+962791234567");
    }

    // ---- the tracking token ----------------------------------------------------------------------------------------

    [Fact]
    public async Task TheTrackingToken_IsProtectedAtRest_ClearedAfterSending_AndNeverLoggedOrShown()
    {
        var placed = await PlaceGuestOrderAsync();
        var token = placed.TrackingToken;

        var stored = await CustomerMessage(placed.OrderId);
        stored.ProtectedSecret.ShouldNotBeNullOrEmpty();
        stored.ProtectedSecret.ShouldNotContain(token);
        stored.Payload.ShouldNotContain(token);

        await _host.ProcessOutboxAsync();

        _host.Email.Sent.Single(e => e.Recipients.Contains(CustomerEmail)).TextBody
            .ShouldContain($"{TestHost.StorefrontUrl}/en/orders/track/{token}");
        (await CustomerMessage(placed.OrderId)).ProtectedSecret.ShouldBeNull();
        _host.Logs.Lines.ShouldNotContain(l => l.Contains(token, StringComparison.Ordinal));
        _host.Logs.Lines.ShouldNotContain(l => l.Contains(CustomerEmail, StringComparison.Ordinal));

        var admin = await _host.Query<GetOrderNotificationsQuery, IReadOnlyList<AdminNotificationDto>>(new(placed.OrderId));
        var json = JsonSerializer.Serialize(admin.Value);
        json.ShouldNotContain(token);
        json.ShouldNotContain("Secret", Case.Insensitive);
        json.ShouldNotContain("Payload", Case.Insensitive);
    }

    [Fact]
    public async Task ARetriedGuestMessage_WhoseTokenWasCleared_SendsWithoutALink()
    {
        var placed = await PlaceGuestOrderAsync();
        _host.Email.Results.Enqueue(DeliveryResult.Permanent("SMTP 550 MessageNotAccepted."));
        await _host.ProcessOutboxAsync();
        var message = await CustomerMessage(placed.OrderId);

        (await _host.Send(new RetryNotificationCommand(message.Id))).IsSuccess.ShouldBeTrue();
        await _host.ProcessOutboxAsync();

        var mail = _host.Email.Sent.Single(e => e.Recipients.Contains(CustomerEmail));
        mail.TextBody.ShouldNotContain("/orders/track/");
        mail.TextBody.ShouldContain($"Quote order number {placed.OrderNumber}");
    }

    // ---- templates -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnArabicOrder_GetsArabicRightToLeftMail()
    {
        _host.Language.Current = Language.Ar;
        var placed = await PlaceGuestOrderAsync();

        await _host.ProcessOutboxAsync();

        var mail = _host.Email.Sent.Single(e => e.Recipients.Contains(CustomerEmail));
        mail.Language.ShouldBe(Language.Ar);
        mail.Subject.ShouldBe($"استلمنا طلبك رقم {placed.OrderNumber}");
        mail.HtmlBody.ShouldNotBeNull().ShouldContain("dir=\"rtl\"");
        mail.TextBody.ShouldContain($"{TestHost.StorefrontUrl}/ar/orders/track/");
    }

    [Fact]
    public async Task AnEnglishOrder_GetsEnglishLeftToRightMail()
    {
        _host.Language.Current = Language.En;
        await PlaceGuestOrderAsync();

        await _host.ProcessOutboxAsync();

        var mail = _host.Email.Sent.Single(e => e.Recipients.Contains(CustomerEmail));
        mail.Language.ShouldBe(Language.En);
        mail.HtmlBody.ShouldNotBeNull().ShouldContain("dir=\"ltr\"");
    }

    [Fact]
    public async Task Money_IsShownInTheOrdersCurrency_EvenAfterTheStoreChangesIt()
    {
        var placed = await PlaceGuestOrderAsync();
        await _host.Db(async db =>
        {
            var settings = await db.StoreSettings.FirstAsync();
            settings.UpdateCommerce("USD", flatShippingFee: 2m, freeShippingThreshold: 100m, allowGuestCheckout: true);
            return await db.SaveChangesAsync();
        });

        await _host.ProcessOutboxAsync();

        var mail = _host.Email.Sent.Single(e => e.Recipients.Contains(CustomerEmail));
        mail.TextBody.ShouldContain(MessageTemplates.Money(placed.Total, "JOD", Language.En));
        mail.TextBody.ShouldNotContain("USD");
    }

    [Fact]
    public async Task CustomerText_IsEscapedInHtml()
    {
        await PlaceGuestOrderAsync(fullName: "<script>alert(1)</script>");

        await _host.ProcessOutboxAsync();

        var html = _host.Email.Sent.Single(e => e.Recipients.Contains(CustomerEmail)).HtmlBody.ShouldNotBeNull();
        html.ShouldNotContain("<script>");
        html.ShouldContain("&lt;script&gt;");
    }

    // ---- staff actions ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Retry_MovesAParkedMessageBackToPending_WithFreshAttempts()
    {
        var placed = await PlaceGuestOrderAsync();
        _host.Email.Results.Enqueue(DeliveryResult.Permanent("SMTP 550 RecipientNotAccepted."));
        await _host.ProcessOutboxAsync();
        var parked = await CustomerMessage(placed.OrderId);
        _host.Clock.Advance(TimeSpan.FromMinutes(5));

        (await _host.Send(new RetryNotificationCommand(parked.Id))).IsSuccess.ShouldBeTrue();

        var retried = await CustomerMessage(placed.OrderId);
        retried.Status.ShouldBe(OutboxStatus.Pending);
        retried.Attempts.ShouldBe(0);
        retried.NextAttemptAt.ShouldBe(_host.Clock.GetUtcNow());
    }

    [Fact]
    public async Task Retry_OfAMessageThatIsNotParked_IsRejected()
    {
        var placed = await PlaceGuestOrderAsync();
        var pending = await CustomerMessage(placed.OrderId);

        (await _host.Send(new RetryNotificationCommand(pending.Id))).Error.ShouldBe(OutboxErrors.NotParked);
        (await _host.Send(new RetryNotificationCommand(Guid.NewGuid()))).Error.ShouldBe(OutboxErrors.NotFound);

        await _host.ProcessOutboxAsync();
        (await _host.Send(new RetryNotificationCommand(pending.Id))).Error.ShouldBe(OutboxErrors.NotParked);
    }

    [Fact]
    public async Task TheAdminList_ShowsTheOrdersMessagesOldestFirst()
    {
        var placed = await PlaceGuestOrderAsync();
        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        _host.User.UserId = Guid.NewGuid();
        _host.User.Roles.Add(Roles.Admin);
        await _host.Send(new ChangeOrderStatusCommand(placed.OrderId, OrderStatus.Confirmed, null));

        var list = (await _host.Query<GetOrderNotificationsQuery, IReadOnlyList<AdminNotificationDto>>(new(placed.OrderId))).Value;

        list.Count.ShouldBe(3);
        list[^1].Template.ShouldBe(NotificationTemplate.OrderConfirmed);
        (await _host.Query<GetOrderNotificationsQuery, IReadOnlyList<AdminNotificationDto>>(new(Guid.NewGuid()))).Error.ShouldBe(OrderErrors.NotFound);
    }

    // ---- housekeeping ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cleanup_DeletesOldSentMessages_ButNeverParkedOnes()
    {
        var placed = await PlaceGuestOrderAsync(); // the staff alert parks (no admin), the customer mail is sent
        await _host.ProcessOutboxAsync();
        var cleanup = _host.Services.GetRequiredService<OutboxCleanupWorker>();

        _host.Clock.Advance(TimeSpan.FromDays(13));
        (await cleanup.CleanupAsync(CancellationToken.None)).ShouldBe(0);

        _host.Clock.Advance(TimeSpan.FromDays(2));
        (await cleanup.CleanupAsync(CancellationToken.None)).ShouldBe(1);

        var left = (await Messages()).ShouldHaveSingleItem();
        left.Status.ShouldBe(OutboxStatus.Parked);
        left.OrderId.ShouldBe(placed.OrderId);
    }

    [Fact]
    public async Task HealthCheck_TurnsDegraded_WhenDeliveryFallsBehind()
    {
        await EnqueueAsync("test:backlog");
        var options = _host.Services.GetRequiredService<IOptions<OutboxOptions>>();

        (await CheckHealthAsync(options)).Status.ShouldBe(HealthStatus.Healthy);

        _host.Clock.Advance(TimeSpan.FromMinutes(16));
        (await CheckHealthAsync(options)).Status.ShouldBe(HealthStatus.Degraded);
    }

    [Fact]
    public async Task AMessageWithNoDeliverer_Parks()
    {
        await EnqueueAsync("test:orphan");

        await _host.ProcessOutboxAsync();

        var message = (await Messages()).ShouldHaveSingleItem();
        message.Status.ShouldBe(OutboxStatus.Parked);
        message.LastError.ShouldNotBeNull().ShouldContain("No handler");
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private Task<List<OutboxMessage>> Messages() => _host.Db(db => db.OutboxMessages.AsNoTracking().ToListAsync());

    private Task<OutboxMessage> CustomerMessage(Guid orderId) => _host.Db(db => db.OutboxMessages.AsNoTracking()
        .SingleAsync(m => m.OrderId == orderId && m.Type == OutboxTypes.CustomerOrderNotification && m.Template == NotificationTemplate.OrderPlaced && m.Channel == NotificationChannel.Email));

    private Task<int> EnqueueAsync(string key) => _host.Run<IServiceProvider, int>(sp =>
    {
        sp.GetRequiredService<IOutbox>().Enqueue(new OutboxEntry("test.type", new { }, key));
        return sp.GetRequiredService<IAppDbContext>().SaveChangesAsync(CancellationToken.None);
    });

    private async Task<HealthCheckResult> CheckHealthAsync(IOptions<OutboxOptions> options)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var check = new OutboxHealthCheck(scope.ServiceProvider.GetRequiredService<AppDbContext>(), options, _host.Clock);
        return await check.CheckHealthAsync(new HealthCheckContext());
    }

    private async Task UseChannelsAsync(params NotificationChannel[] channels)
    {
        await _host.DisposeAsync();
        _host = await TestHost.CreateAsync(configure: s => s.Configure<NotificationOptions>(o =>
        {
            o.CustomerChannels.Clear();
            o.CustomerChannels.AddRange(channels);
        }));
    }

    private Task<OrderPlacedDto> PlaceGuestOrderAsync(string fullName = "Sara Ahmad") => PlaceOrderAsync(new CartOwner(null, null), fullName);

    private async Task<OrderPlacedDto> PlaceOrderAsync(CartOwner owner, string fullName = "Sara Ahmad")
    {
        var variant = await _host.Db(db => db.ProductVariants.Where(v => v.Sku == "ACC-ANK-30W-WH").Select(v => v.Id).SingleAsync());
        var cart = await _host.Send<AddToCartCommand, CartDto>(new(owner, variant, 1));
        cart.IsSuccess.ShouldBeTrue();
        var checkoutOwner = owner.CustomerId is null ? new CartOwner(null, cart.Value.CartId) : owner;
        var placed = await _host.Send<PlaceOrderCommand, OrderPlacedDto>(new PlaceOrderCommand(
            checkoutOwner, fullName, "0791234567", CustomerEmail,
            new AddressInput("Amman", "Abdoun", "Street 5", "12", null), Note: null, SaveAddress: false));
        placed.IsSuccess.ShouldBeTrue(placed.IsFailure ? placed.Error.Description : null);
        return placed.Value;
    }

    private async Task<Guid> CreateUserAsync(string email, string? role)
    {
        return await _host.Run<IServiceProvider, Guid>(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = email, Email = email, FullName = email, PreferredLanguage = Language.En };
            (await users.CreateAsync(user)).Succeeded.ShouldBeTrue();
            if (role is not null)
            {
                var roles = sp.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
                if (!await roles.RoleExistsAsync(role))
                {
                    (await roles.CreateAsync(new IdentityRole<Guid>(role))).Succeeded.ShouldBeTrue();
                }

                (await users.AddToRoleAsync(user, role)).Succeeded.ShouldBeTrue();
            }

            return user.Id;
        });
    }

    private sealed class ConstantOrderNumber : IOrderNumberGenerator
    {
        public Task<string> NextAsync(CancellationToken cancellationToken) => Task.FromResult("100001");
    }
}

public sealed class MessageTemplateTests
{
    public static TheoryData<NotificationTemplate> AllTemplates() =>
        [.. Enum.GetValues<NotificationTemplate>()];

    [Theory]
    [MemberData(nameof(AllTemplates))]
    public void EveryTemplate_HasEnglishAndArabic(NotificationTemplate template)
    {
        foreach (var language in Enum.GetValues<Language>())
        {
            MessageTemplates.IsDefined(template, language).ShouldBeTrue($"{template} has no {language} copy");
        }
    }

    [Theory]
    [MemberData(nameof(AllTemplates))]
    public void EveryTemplate_RendersTextAndDirectionalHtml(NotificationTemplate template)
    {
        foreach (var language in Enum.GetValues<Language>())
        {
            var rendered = template switch
            {
                NotificationTemplate.StaffNewOrder => MessageTemplates.RenderStaffNewOrder(language, new StaffOrderModel("100001", "Sara", "+962791234567", "Amman", 25m, "JOD", 1)),
                NotificationTemplate.PasswordReset => MessageTemplates.RenderPasswordReset(language, new PasswordResetModel("PhoneStore", "Sara", "https://shop.test/r", 24)),
                _ => MessageTemplates.RenderOrder(template, language, new OrderMessageModel(
                    "PhoneStore", "Sara", "100001", [new OrderMessageLine("Charger", 1, 25m)], 27m, "JOD", "https://shop.test/o", IsGuest: true, CancelledByCustomer: false, Reason: null)),
            };

            rendered.Subject.ShouldNotBeNullOrWhiteSpace();
            rendered.Text.ShouldNotBeNullOrWhiteSpace();
            rendered.ShortText.ShouldNotBeNullOrWhiteSpace();
            rendered.Html.ShouldContain(language == Language.Ar ? "dir=\"rtl\"" : "dir=\"ltr\"");
        }
    }

    [Theory]
    [InlineData("1049.5", "JOD", Language.En, "JOD 1,049.500")]
    [InlineData("1049.5", "JOD", Language.Ar, "1,049.500 JOD")]
    [InlineData("19.9", "USD", Language.En, "USD 19.90")]
    [InlineData("1500", "JPY", Language.En, "JPY 1,500")]
    public void Money_UsesTheCurrencysDecimals_AndTheLanguagesOrder(string amount, string currency, Language language, string expected) =>
        MessageTemplates.Money(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), currency, language).ShouldBe(expected);
}
