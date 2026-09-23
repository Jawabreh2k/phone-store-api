using Store.Domain.Carts;
using Store.Domain.Common;
using Store.Domain.Orders;
using Store.Domain.Settings;

namespace Store.Domain.Tests;

public class OrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Staff = Guid.NewGuid();

    private static readonly PhoneNumber Phone = PhoneNumber.Create("0790000000", "962").Value;
    private static readonly string TokenHash = OrderTrackingToken.Hash(OrderTrackingToken.Generate());

    private static Order PlaceOrder(decimal shipping = 2m) => Order.Place(
        "100001",
        TokenHash,
        "JOD",
        customerId: Guid.NewGuid(),
        new ContactInfo("Sara", Phone, null),
        new ShippingAddress("Amman", "Abdoun", "Street 1", null, null),
        [
            new OrderLineInput(Guid.NewGuid(), Guid.NewGuid(), "A", new("Phone", "هاتف"), new("128 GB", "128 GB"), null, 100m, 2),
            new OrderLineInput(Guid.NewGuid(), Guid.NewGuid(), "B", new("Case", "غطاء"), new("-", "-"), null, 15.5m, 1),
        ],
        shipping,
        customerNote: "  ring twice ",
        Now).Value;

    [Fact]
    public void Place_ComputesTotalsAndStartsPendingUnpaid()
    {
        var order = PlaceOrder();

        order.Subtotal.ShouldBe(215.5m);
        order.Total.ShouldBe(217.5m);
        order.Status.ShouldBe(OrderStatus.Pending);
        order.PaymentStatus.ShouldBe(PaymentStatus.Unpaid);
        order.CustomerNote.ShouldBe("ring twice");
        order.History.Single().Status.ShouldBe(OrderStatus.Pending);
        order.CurrencyCode.ShouldBe("JOD");
        order.Contact.Phone.ShouldBe("+962790000000");
    }

    [Fact]
    public void Place_WithoutLines_Fails()
    {
        var result = Order.Place("1", TokenHash, "JOD", null, new ContactInfo("a", Phone, null),
            new ShippingAddress("c", "a", "s", null, null), [], 0m, null, Now);

        result.Error.ShouldBe(OrderErrors.NoItems);
    }

    [Theory]
    [InlineData("jod")]
    [InlineData("JD")]
    [InlineData("")]
    public void Place_RequiresIsoCurrency(string currency)
    {
        var result = Order.Place("1", TokenHash, currency, null, new ContactInfo("a", Phone, null),
            new ShippingAddress("c", "a", "s", null, null),
            [new OrderLineInput(Guid.NewGuid(), Guid.NewGuid(), "A", new("P", "ه"), new("-", "-"), null, 1m, 1)],
            0m, null, Now);

        result.Error.ShouldBe(OrderErrors.InvalidCurrency);
    }

    [Fact]
    public void HappyPath_DeliveryMarksCashOrderPaid()
    {
        var order = PlaceOrder();

        order.Confirm(Staff, Now).IsSuccess.ShouldBeTrue();
        order.Ship(Staff, Now).IsSuccess.ShouldBeTrue();
        order.Deliver(Staff, Now).IsSuccess.ShouldBeTrue();

        order.Status.ShouldBe(OrderStatus.Delivered);
        order.PaymentStatus.ShouldBe(PaymentStatus.Paid);
        order.History.Select(h => h.Status).ShouldBe([OrderStatus.Pending, OrderStatus.Confirmed, OrderStatus.Shipped, OrderStatus.Delivered]);
    }

    [Fact]
    public void CannotSkipSteps()
    {
        var order = PlaceOrder();

        order.Ship(Staff, Now).Error.Code.ShouldBe("orders.invalid_transition");
        order.Status.ShouldBe(OrderStatus.Pending);
    }

    [Fact]
    public void Customer_CanCancelOnlyWhilePending()
    {
        var order = PlaceOrder();
        order.Confirm(Staff, Now);

        order.Cancel(Guid.NewGuid(), byCustomer: true, "changed mind", Now).Error.ShouldBe(OrderErrors.CustomerCanOnlyCancelPending);
        order.Cancel(Staff, byCustomer: false, "out of stock", Now).IsSuccess.ShouldBeTrue();
        order.CancellationReason.ShouldBe("out of stock");
    }

    [Fact]
    public void Staff_CannotCancelShippedOrder()
    {
        var order = PlaceOrder();
        order.Confirm(Staff, Now);
        order.Ship(Staff, Now);

        order.Cancel(Staff, byCustomer: false, null, Now).Error.ShouldBe(OrderErrors.CannotCancelAfterShipping);
    }
}

public class CartTests
{
    [Fact]
    public void AddItem_SameVariant_AddsUntilTheCap()
    {
        var cart = Cart.ForGuest();
        var variant = Guid.NewGuid();

        cart.AddItem(variant, 4);
        cart.AddItem(variant, 6);

        cart.Items.Single().Quantity.ShouldBe(Cart.MaxQuantityPerLine);
    }

    [Fact]
    public void AddItem_PastTheLineLimit_FailsAndLeavesTheSavedQuantity()
    {
        var cart = Cart.ForGuest();
        var variant = Guid.NewGuid();

        cart.AddItem(variant, 7).IsSuccess.ShouldBeTrue();
        var overflow = cart.AddItem(variant, 4);

        overflow.Error.ShouldBe(CartErrors.QuantityLimit);
        cart.Items.Single().Quantity.ShouldBe(7);
    }

    [Fact]
    public void AddItem_QuantityAboveTheCap_IsRejected()
    {
        var cart = Cart.ForGuest();

        cart.AddItem(Guid.NewGuid(), 11).Error.ShouldBe(CartErrors.InvalidQuantity);

        cart.Items.ShouldBeEmpty();
    }

    [Fact]
    public void SetQuantity_Zero_RemovesLine()
    {
        var cart = Cart.ForGuest();
        var variant = Guid.NewGuid();
        cart.AddItem(variant, 1);

        cart.SetQuantity(variant, 0).IsSuccess.ShouldBeTrue();

        cart.Items.ShouldBeEmpty();
    }

    [Fact]
    public void MergeFrom_CombinesQuantities()
    {
        var shared = Guid.NewGuid();
        var own = Cart.ForCustomer(Guid.NewGuid());
        own.AddItem(shared, 1);
        var guest = Cart.ForGuest();
        guest.AddItem(shared, 2);
        guest.AddItem(Guid.NewGuid(), 1);

        own.MergeFrom(guest);

        own.Items.Count.ShouldBe(2);
        own.Items.Single(i => i.VariantId == shared).Quantity.ShouldBe(3);
    }

    [Fact]
    public void MergeFrom_CapsASharedLineAtTheMaximum()
    {
        var shared = Guid.NewGuid();
        var own = Cart.ForCustomer(Guid.NewGuid());
        own.AddItem(shared, 8);
        var guest = Cart.ForGuest();
        guest.AddItem(shared, 5);

        own.MergeFrom(guest);

        own.Items.Single().Quantity.ShouldBe(Cart.MaxQuantityPerLine);
    }

    [Fact]
    public void AssignTo_CannotStealAnotherCustomersCart()
    {
        var cart = Cart.ForCustomer(Guid.NewGuid());

        Should.Throw<InvalidOperationException>(() => cart.AssignTo(Guid.NewGuid()));
    }
}

public class StoreSettingsTests
{
    [Theory]
    [InlineData(50, 2)]
    [InlineData(100, 0)]
    [InlineData(150, 0)]
    public void ShippingFee_FreeAboveThreshold(decimal subtotal, decimal expected)
    {
        var settings = new StoreSettings(new LocalizedText("S", "س"), "JOD", "962");
        settings.UpdateCommerce("JOD", 2m, 100m, allowGuestCheckout: true);

        settings.ShippingFeeFor(subtotal).ShouldBe(expected);
    }
}
