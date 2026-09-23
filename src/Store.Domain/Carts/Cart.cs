using Store.Domain.Common;

namespace Store.Domain.Carts;

/// <summary>
/// A shopping cart. Guests get one keyed by a cookie; signed-in customers own one by <see cref="CustomerId"/>.
/// The cart stores only what and how many. Prices are read live, so a cart never shows a stale price.
/// </summary>
public sealed class Cart : AuditableEntity
{
    public const int MaxLines = 30;
    public const int MaxQuantityPerLine = 10;

    private readonly List<CartItem> _items = [];

    private Cart() { } // EF

    private Cart(Guid? customerId) => CustomerId = customerId;

    public Guid? CustomerId { get; private set; }
    public IReadOnlyList<CartItem> Items => _items;

    public static Cart ForGuest() => new(null);
    public static Cart ForCustomer(Guid customerId) => new(customerId);

    public Result AddItem(Guid variantId, int quantity)
    {
        if (quantity <= 0 || quantity > MaxQuantityPerLine)
        {
            return CartErrors.InvalidQuantity;
        }

        var existing = _items.Find(i => i.VariantId == variantId);
        if (existing is not null)
        {
            if (existing.Quantity + quantity > MaxQuantityPerLine)
            {
                return CartErrors.QuantityLimit;
            }

            existing.Quantity += quantity;
            return Result.Success();
        }

        if (_items.Count >= MaxLines)
        {
            return CartErrors.TooManyLines;
        }

        _items.Add(new CartItem(Id, variantId, quantity));
        return Result.Success();
    }

    /// <summary>Sets a line's quantity; zero removes it.</summary>
    public Result SetQuantity(Guid variantId, int quantity)
    {
        if (quantity is < 0 or > MaxQuantityPerLine)
        {
            return CartErrors.InvalidQuantity;
        }

        var existing = _items.Find(i => i.VariantId == variantId);
        if (existing is null)
        {
            return CartErrors.ItemNotFound;
        }

        if (quantity == 0)
        {
            _items.Remove(existing);
        }
        else
        {
            existing.Quantity = quantity;
        }

        return Result.Success();
    }

    public void RemoveItem(Guid variantId) => _items.RemoveAll(i => i.VariantId == variantId);

    public void Clear() => _items.Clear();

    /// <summary>
    /// Folds a guest cart into this one at sign-in. Quantities add up to the per-line cap;
    /// a line that does not fit is dropped. Sign-in does not fail because the two carts overlap.
    /// </summary>
    public void MergeFrom(Cart other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (var item in other.Items)
        {
            var existing = _items.Find(i => i.VariantId == item.VariantId);
            var room = existing is null ? MaxQuantityPerLine : MaxQuantityPerLine - existing.Quantity;
            if (room <= 0)
            {
                continue;
            }

            _ = AddItem(item.VariantId, Math.Min(item.Quantity, room));
        }
    }

    /// <summary>Makes a guest cart the customer's cart when they sign in and have none yet.</summary>
    public void AssignTo(Guid customerId)
    {
        if (CustomerId is not null && CustomerId != customerId)
        {
            throw new InvalidOperationException("A cart cannot change owner.");
        }

        CustomerId = customerId;
    }
}

public sealed class CartItem
{
    private CartItem() { } // EF

    internal CartItem(Guid cartId, Guid variantId, int quantity)
    {
        CartId = cartId;
        VariantId = variantId;
        Quantity = quantity;
    }

    public Guid CartId { get; private set; }
    public Guid VariantId { get; private set; }
    public int Quantity { get; internal set; }
}

public static class CartErrors
{
    public static readonly Error InvalidQuantity = Error.Validation("cart.invalid_quantity", $"Quantity must be between 1 and {Cart.MaxQuantityPerLine}.");
    public static readonly Error QuantityLimit = Error.Validation("cart.quantity_limit", $"You can only have {Cart.MaxQuantityPerLine} of each item in your cart.");
    public static readonly Error TooManyLines = Error.Validation("cart.too_many_lines", $"A cart can hold at most {Cart.MaxLines} different items.");
    public static readonly Error ItemNotFound = Error.NotFound("cart.item_not_found", "The item is not in the cart.");
    public static readonly Error Empty = Error.Validation("cart.empty", "The cart is empty.");
    public static readonly Error ItemUnavailable = Error.Conflict("cart.item_unavailable", "One or more items are no longer available.");
}
