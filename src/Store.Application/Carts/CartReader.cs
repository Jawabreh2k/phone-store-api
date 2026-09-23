using Microsoft.EntityFrameworkCore;
using Store.Application.Abstractions;
using Store.Domain.Carts;

namespace Store.Application.Carts;

/// <summary>
/// Prices a cart against the live catalogue. Lines whose product was unpublished, deactivated
/// or sold out stay visible but are flagged, so the customer understands why checkout is blocked.
/// </summary>
internal sealed class CartReader(IAppDbContext db, ILanguageProvider language)
{
    public async Task<CartDto> ReadAsync(Cart cart, CancellationToken cancellationToken)
    {
        var lines = await LoadLinesAsync(cart, cancellationToken);
        var settings = await db.StoreSettings.AsNoTracking().FirstAsync(cancellationToken);

        var available = lines.Where(l => l.IsAvailable).ToList();
        var subtotal = available.Sum(l => l.LineTotal);
        var shipping = available.Count == 0 ? 0m : settings.ShippingFeeFor(subtotal);

        return new CartDto(
            cart.Id,
            lines,
            available.Sum(l => l.Quantity),
            subtotal,
            shipping,
            subtotal + shipping,
            settings.CurrencyCode,
            lines.Any(l => !l.IsAvailable));
    }

    private async Task<List<CartLineDto>> LoadLinesAsync(Cart cart, CancellationToken cancellationToken)
    {
        if (cart.Items.Count == 0)
        {
            return [];
        }

        var lang = language.Current;
        var variantIds = cart.Items.Select(i => i.VariantId).ToList();

        var rows = await db.ProductVariants.AsNoTracking()
            .Where(v => variantIds.Contains(v.Id))
            .Join(db.Products, v => v.ProductId, p => p.Id, (v, p) => new
            {
                Variant = v,
                p.Slug,
                p.Name,
                p.IsPublished,
                Image = p.Images
                    .OrderBy(i => i.VariantId == v.Id ? 0 : 1).ThenBy(i => i.SortOrder)
                    .Select(i => i.Url).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var byVariant = rows.ToDictionary(r => r.Variant.Id);
        var lines = new List<CartLineDto>(cart.Items.Count);
        foreach (var item in cart.Items)
        {
            // A variant that was deleted entirely just disappears from the cart view.
            if (!byVariant.TryGetValue(item.VariantId, out var row))
            {
                continue;
            }

            var v = row.Variant;
            var available = row.IsPublished && v.IsActive && v.StockQuantity >= item.Quantity;
            lines.Add(new CartLineDto(
                v.Id,
                row.Slug,
                row.Name.In(lang),
                v.Options.Describe(lang),
                row.Image,
                v.Price,
                item.Quantity,
                v.Price * item.Quantity,
                available,
                row.IsPublished && v.IsActive ? Math.Min(v.StockQuantity, Cart.MaxQuantityPerLine) : 0));
        }

        return lines;
    }
}
