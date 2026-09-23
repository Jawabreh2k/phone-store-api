using Microsoft.EntityFrameworkCore;
using Store.Application.Carts;
using Store.Application.Orders;
using Store.Domain.Carts;
using Store.Domain.Common;
using Store.Domain.Orders;
using Store.Infrastructure.Identity;

namespace Store.Application.Tests;

public sealed class CheckoutFlowTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async Task InitializeAsync() => _host = await TestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private Task<Guid> VariantId(string sku) =>
        _host.Db(db => db.ProductVariants.Where(v => v.Sku == sku).Select(v => v.Id).SingleAsync());

    private Task<int> Stock(string sku) =>
        _host.Db(db => db.ProductVariants.Where(v => v.Sku == sku).Select(v => v.StockQuantity).SingleAsync());

    private static PlaceOrderCommand Checkout(CartOwner owner, string phone = "0791234567") => new(
        owner, "Sara Ahmad", phone, null,
        new AddressInput("Amman", "Abdoun", "Street 5", "12", null), Note: null, SaveAddress: true);

    private async Task<Guid> CreateCustomerAsync()
    {
        var user = new ApplicationUser { UserName = "c@test.local", Email = "c@test.local", FullName = "Customer" };
        await _host.Db(async db => { db.Users.Add(user); return await db.SaveChangesAsync(); });
        return user.Id;
    }

    [Fact]
    public async Task GuestCheckout_ReservesStock_SnapshotsPrices_AndEmptiesCart()
    {
        var phone = await VariantId("IP16P-256-TI");     // 999, stock 7
        var charger = await VariantId("ACC-ANK-30W-WH"); // 25, stock 35

        var cart = await _host.Send<AddToCartCommand, CartDto>(new(new CartOwner(null, null), phone, 1));
        cart.IsSuccess.ShouldBeTrue();
        var owner = new CartOwner(null, cart.Value.CartId);
        await _host.Send<AddToCartCommand, CartDto>(new(owner, charger, 2));

        var placed = await _host.Send<PlaceOrderCommand, OrderPlacedDto>(Checkout(owner));

        placed.IsSuccess.ShouldBeTrue(placed.IsFailure ? placed.Error.Description : null);
        placed.Value.Total.ShouldBe(999m + 50m); // over the free-shipping threshold
        (await Stock("IP16P-256-TI")).ShouldBe(6);
        (await Stock("ACC-ANK-30W-WH")).ShouldBe(33);

        var order = await _host.Db(db => db.Orders.Include(o => o.Items).SingleAsync());
        order.CustomerId.ShouldBeNull();
        order.CurrencyCode.ShouldBe("JOD");
        placed.Value.CurrencyCode.ShouldBe("JOD");
        order.Contact.Phone.ShouldBe("+962791234567");
        order.Items.Single(i => i.VariantId == phone).VariantDescription.En.ShouldBe("256 GB · 8 GB RAM · Natural Titanium");

        var cartAfter = await _host.Query<GetCartQuery, CartDto?>(new GetCartQuery(owner));
        cartAfter.Value!.Lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task SmallOrder_PaysFlatShipping()
    {
        var glass = await VariantId("ACC-IP16P-GLASS"); // 9
        var cart = await _host.Send<AddToCartCommand, CartDto>(new(new CartOwner(null, null), glass, 1));

        cart.Value.ShippingFee.ShouldBe(2m);
        cart.Value.Total.ShouldBe(11m);

        var placed = await _host.Send<PlaceOrderCommand, OrderPlacedDto>(Checkout(new CartOwner(null, cart.Value.CartId)));
        placed.Value.Total.ShouldBe(11m);
    }

    [Fact]
    public async Task Checkout_WhenStockRanOut_FailsAndChangesNothing()
    {
        var buds = await VariantId("ACC-BUDSFE-WH"); // stock 2
        var cart = await _host.Send<AddToCartCommand, CartDto>(new(new CartOwner(null, null), buds, 2));
        var owner = new CartOwner(null, cart.Value.CartId);

        // Someone else buys one first.
        var other = await _host.Send<AddToCartCommand, CartDto>(new(new CartOwner(null, null), buds, 1));
        (await _host.Send<PlaceOrderCommand, OrderPlacedDto>(Checkout(new CartOwner(null, other.Value.CartId)))).IsSuccess.ShouldBeTrue();

        var placed = await _host.Send<PlaceOrderCommand, OrderPlacedDto>(Checkout(owner));

        placed.Error.Code.ShouldBe("catalog.insufficient_stock");
        (await Stock("ACC-BUDSFE-WH")).ShouldBe(1);
        (await _host.Db(db => db.Orders.CountAsync())).ShouldBe(1);
        (await _host.Query<GetCartQuery, CartDto?>(new GetCartQuery(owner))).Value!.HasUnavailableItems.ShouldBeTrue();
    }

    [Fact]
    public async Task Checkout_WithInvalidInput_ReturnsFieldErrors()
    {
        var placed = await _host.Send<PlaceOrderCommand, OrderPlacedDto>(new PlaceOrderCommand(
            new CartOwner(null, Guid.NewGuid()), "", "abc", "not-an-email",
            new AddressInput("", "Area", "Street", null, null), null, false));

        var error = placed.Error.ShouldBeOfType<ValidationError>();
        error.Errors.Keys.ShouldBe(["fullName", "phone", "email", "address.city"], ignoreOrder: true);
    }

    [Fact]
    public async Task CustomerCancel_ReleasesStock_AndSavesAddressOnce()
    {
        var customerId = await CreateCustomerAsync();
        var owner = new CartOwner(customerId, null);
        var phone = await VariantId("GS24-256-BL"); // stock 4
        await _host.Send<AddToCartCommand, CartDto>(new(owner, phone, 3));

        var placed = await _host.Send<PlaceOrderCommand, OrderPlacedDto>(Checkout(owner));
        (await Stock("GS24-256-BL")).ShouldBe(1);

        var cancelled = await _host.Send(new CancelMyOrderCommand(customerId, placed.Value.OrderNumber, "Changed my mind"));

        cancelled.IsSuccess.ShouldBeTrue();
        (await Stock("GS24-256-BL")).ShouldBe(4);
        var order = await _host.Query<GetMyOrderQuery, OrderDetailsDto>(new GetMyOrderQuery(customerId, placed.Value.OrderNumber));
        order.Value.Status.ShouldBe(OrderStatus.Cancelled);
        (await _host.Db(db => db.CustomerAddresses.CountAsync(a => a.CustomerId == customerId))).ShouldBe(1);
    }

    [Fact]
    public async Task OtherCustomersOrders_AreNotVisible()
    {
        var customerId = await CreateCustomerAsync();
        var owner = new CartOwner(customerId, null);
        await _host.Send<AddToCartCommand, CartDto>(new(owner, await VariantId("RN13-128-BK"), 1));
        var placed = await _host.Send<PlaceOrderCommand, OrderPlacedDto>(Checkout(owner));

        var stranger = await _host.Query<GetMyOrderQuery, OrderDetailsDto>(new GetMyOrderQuery(Guid.NewGuid(), placed.Value.OrderNumber));

        stranger.Error.ShouldBe(OrderErrors.NotFound);
    }

    [Fact]
    public async Task GuestTracking_WorksOnlyWithTheCheckoutToken()
    {
        var cart = await _host.Send<AddToCartCommand, CartDto>(new(new CartOwner(null, null), await VariantId("RN13-128-BK"), 1));
        var placed = await _host.Send<PlaceOrderCommand, OrderPlacedDto>(Checkout(new CartOwner(null, cart.Value.CartId)));
        var token = placed.Value.TrackingToken;

        var tracked = await _host.Query<TrackOrderQuery, OrderDetailsDto>(new(token));
        tracked.Value.OrderNumber.ShouldBe(placed.Value.OrderNumber);
        tracked.Value.CanCancel.ShouldBeFalse();

        // A different valid-looking token, and the (guessable) order number, both fail.
        (await _host.Query<TrackOrderQuery, OrderDetailsDto>(new(OrderTrackingToken.Generate()))).Error.ShouldBe(OrderErrors.NotFound);
        (await _host.Query<TrackOrderQuery, OrderDetailsDto>(new(placed.Value.OrderNumber))).Error.ShouldBeOfType<ValidationError>();

        // Only the hash is persisted.
        var stored = await _host.Db(db => db.Orders.Select(o => o.TrackingTokenHash).SingleAsync());
        stored.ShouldBe(OrderTrackingToken.Hash(token));
        stored.ShouldNotBe(token);
    }

    [Fact]
    public async Task OrderKeepsItsCurrency_WhenStoreCurrencyChanges()
    {
        var customerId = await CreateCustomerAsync();
        var owner = new CartOwner(customerId, null);
        await _host.Send<AddToCartCommand, CartDto>(new(owner, await VariantId("RN13-128-BK"), 1));
        var placed = await _host.Send<PlaceOrderCommand, OrderPlacedDto>(Checkout(owner));

        await _host.Db(async db =>
        {
            var settings = await db.StoreSettings.SingleAsync();
            settings.UpdateCommerce("USD", 3m, null, allowGuestCheckout: true);
            return await db.SaveChangesAsync();
        });

        var details = await _host.Query<GetMyOrderQuery, OrderDetailsDto>(new GetMyOrderQuery(customerId, placed.Value.OrderNumber));
        details.Value.CurrencyCode.ShouldBe("JOD");
        var list = await _host.Query<GetMyOrdersQuery, Store.Application.Common.PagedResult<OrderSummaryDto>>(new GetMyOrdersQuery(customerId, null, null));
        list.Value.Items.Single().CurrencyCode.ShouldBe("JOD");
    }

    [Theory]
    [InlineData("079 123 4567")]
    [InlineData("+962-79-123-4567")]
    [InlineData("+962 079 123 4567")]
    [InlineData("٠٧٩١٢٣٤٥٦٧")]
    public async Task PhonesAreStoredCanonically_AndAdminSearchFindsAnyFormat(string typed)
    {
        var cart = await _host.Send<AddToCartCommand, CartDto>(new(new CartOwner(null, null), await VariantId("RN13-128-BK"), 1));
        (await _host.Send<PlaceOrderCommand, OrderPlacedDto>(Checkout(new CartOwner(null, cart.Value.CartId), typed))).IsSuccess.ShouldBeTrue();

        (await _host.Db(db => db.Orders.Select(o => o.Contact.Phone).SingleAsync())).ShouldBe("+962791234567");

        foreach (var search in new[] { "0791234567", "079 123 4567", "+962 079 123 4567", "+962791234567", "4567" })
        {
            var found = await _host.Query<GetAdminOrdersQuery, Store.Application.Common.PagedResult<AdminOrderListItemDto>>(new GetAdminOrdersQuery(Search: search));
            found.Value.TotalCount.ShouldBe(1, $"search '{search}'");
        }
    }

    [Fact]
    public async Task AdminWorkflow_DeliversAndMarksPaid_CancelRestocks()
    {
        var admin = await CreateCustomerAsync();
        _host.User.UserId = admin;

        async Task<Guid> PlaceAsync()
        {
            var cart = await _host.Send<AddToCartCommand, CartDto>(new(new CartOwner(null, null), await VariantId("GS24-128-BK"), 1));
            var placed = await _host.Send<PlaceOrderCommand, OrderPlacedDto>(Checkout(new CartOwner(null, cart.Value.CartId)));
            return placed.Value.OrderId;
        }

        var delivered = await PlaceAsync();
        foreach (var status in new[] { OrderStatus.Confirmed, OrderStatus.Shipped, OrderStatus.Delivered })
        {
            (await _host.Send(new ChangeOrderStatusCommand(delivered, status, null))).IsSuccess.ShouldBeTrue();
        }

        var details = await _host.Query<GetAdminOrderQuery, AdminOrderDetailsDto>(new GetAdminOrderQuery(delivered));
        details.Value.Order.PaymentStatus.ShouldBe(PaymentStatus.Paid);
        details.Value.NextStatuses.ShouldBeEmpty();
        details.Value.History[^1].ChangedBy.ShouldBe(admin);

        var cancelled = await PlaceAsync();
        (await Stock("GS24-128-BK")).ShouldBe(18);
        (await _host.Send(new ChangeOrderStatusCommand(cancelled, OrderStatus.Confirmed, null))).IsSuccess.ShouldBeTrue();
        (await _host.Send(new ChangeOrderStatusCommand(cancelled, OrderStatus.Cancelled, "Customer unreachable"))).IsSuccess.ShouldBeTrue();
        (await Stock("GS24-128-BK")).ShouldBe(19);

        (await _host.Send(new ChangeOrderStatusCommand(cancelled, OrderStatus.Shipped, null))).Error.Code.ShouldBe("orders.invalid_transition");
    }

    [Fact]
    public async Task MergeGuestCart_OnSignIn_KeepsItems()
    {
        var customerId = await CreateCustomerAsync();
        var shared = await VariantId("RN13-128-BK");
        await _host.Send<AddToCartCommand, CartDto>(new(new CartOwner(customerId, null), shared, 1));
        var guest = await _host.Send<AddToCartCommand, CartDto>(new(new CartOwner(null, null), shared, 2));
        await _host.Send<AddToCartCommand, CartDto>(new(new CartOwner(null, guest.Value.CartId), await VariantId("ACC-IP16P-GLASS"), 1));

        (await _host.Send(new MergeGuestCartCommand(customerId, guest.Value.CartId))).IsSuccess.ShouldBeTrue();

        var cart = await _host.Query<GetCartQuery, CartDto?>(new GetCartQuery(new CartOwner(customerId, null)));
        cart.Value!.Lines.Count.ShouldBe(2);
        cart.Value.Lines.Single(l => l.VariantId == shared).Quantity.ShouldBe(3);
        (await _host.Db(db => db.Carts.CountAsync())).ShouldBe(1);
    }

    [Fact]
    public async Task AddToCart_PastTheLineLimit_ReturnsAnErrorAndKeepsTheSavedQuantity()
    {
        var variant = await VariantId("ACC-ANK-30W-WH");
        var first = await _host.Send<AddToCartCommand, CartDto>(new(new CartOwner(null, null), variant, 6));
        var owner = new CartOwner(null, first.Value.CartId);

        var overflow = await _host.Send<AddToCartCommand, CartDto>(new(owner, variant, 5));

        overflow.Error.ShouldBe(CartErrors.QuantityLimit);
        var cart = await _host.Query<GetCartQuery, CartDto?>(new GetCartQuery(owner));
        cart.Value!.Lines.Single().Quantity.ShouldBe(6);
    }
}
