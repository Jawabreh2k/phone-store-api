using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Store.Application.Abstractions;
using Store.Application.Common;
using Store.Domain.Catalog;
using Store.Domain.Common;

namespace Store.Application.Catalog.Admin;

// ---------- Create / update product details ----------

public sealed record SaveProductCommand(
    Guid? Id,
    ProductType Type,
    Guid BrandId,
    Guid CategoryId,
    LocalizedTextDto Name,
    string? Slug,
    LocalizedTextDto Description,
    bool IsFeatured) : ICommand<Guid>;

public sealed class SaveProductValidator : AbstractValidator<SaveProductCommand>
{
    public SaveProductValidator()
    {
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.BrandId).NotEmpty();
        RuleFor(x => x.CategoryId).NotEmpty();
        RuleFor(x => x.Name).NotNull().SetValidator(new LocalizedTextValidator(Product.NameMaxLength));
        RuleFor(x => x.Description).NotNull().SetValidator(new LocalizedTextValidator(Product.DescriptionMaxLength));
        RuleFor(x => x.Slug!).ValidSlug().When(x => !string.IsNullOrEmpty(x.Slug));
    }
}

/// <summary>Creates a product (unpublished, no variants) or updates its details. The type is fixed after creation.</summary>
internal sealed class SaveProductHandler(IAppDbContext db) : ICommandHandler<SaveProductCommand, Guid>
{
    public async Task<Result<Guid>> Handle(SaveProductCommand command, CancellationToken cancellationToken)
    {
        var slug = string.IsNullOrEmpty(command.Slug) ? Slug.From(command.Name.En) : command.Slug;
        if (!Slug.IsValid(slug))
        {
            return Error.Validation("catalog.slug_required", "Enter a slug using English letters and numbers.");
        }

        if (!await db.Brands.AnyAsync(b => b.Id == command.BrandId, cancellationToken))
        {
            return CatalogErrors.BrandNotFound;
        }

        var category = await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == command.CategoryId, cancellationToken);
        if (category is null)
        {
            return CatalogErrors.CategoryNotFound;
        }

        if (await db.Products.AnyAsync(p => p.Slug == slug && p.Id != command.Id, cancellationToken))
        {
            return CatalogErrors.SlugTaken;
        }

        Product? product;
        if (command.Id is { } id)
        {
            product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
            if (product is null)
            {
                return CatalogErrors.ProductNotFound;
            }
        }
        else
        {
            product = null;
        }

        var type = product?.Type ?? command.Type;
        if (category.ProductType != type)
        {
            return CatalogErrors.CategoryTypeMismatch;
        }

        if (product is null)
        {
            product = new Product(type, command.BrandId, command.CategoryId, command.Name.ToDomain(), slug, command.Description.ToDomain());
            db.Products.Add(product);
        }
        else
        {
            product.UpdateDetails(command.BrandId, command.CategoryId, command.Name.ToDomain(), slug, command.Description.ToDomain());
        }

        product.SetFeatured(command.IsFeatured);
        await db.SaveChangesAsync(cancellationToken);
        return product.Id;
    }
}

public sealed record SetProductPublishedCommand(Guid ProductId, bool Published) : ICommand;

internal sealed class SetProductPublishedHandler(IAppDbContext db) : ICommandHandler<SetProductPublishedCommand>
{
    public async Task<Result> Handle(SetProductPublishedCommand command, CancellationToken cancellationToken)
    {
        var product = await ProductLoader.LoadAsync(db, command.ProductId, cancellationToken);
        if (product is null)
        {
            return CatalogErrors.ProductNotFound;
        }

        if (command.Published)
        {
            var result = product.Publish();
            if (result.IsFailure)
            {
                return result;
            }
        }
        else
        {
            product.Unpublish();
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

// ---------- Variants & stock ----------

public sealed record AddVariantCommand(Guid ProductId, VariantInput Variant, int StockQuantity) : ICommand<Guid>;

public sealed record UpdateVariantCommand(Guid ProductId, Guid VariantId, VariantInput Variant, bool IsActive) : ICommand;

public sealed class VariantInputValidator : AbstractValidator<VariantInput>
{
    public VariantInputValidator()
    {
        RuleFor(x => x.Sku).NotEmpty().MaximumLength(ProductVariant.SkuMaxLength).Matches("^[A-Za-z0-9._-]+$")
            .WithMessage("SKU may contain letters, numbers, dots, dashes and underscores.");
        RuleFor(x => x.Color!).SetValidator(new LocalizedTextValidator(50)).When(x => x.Color is not null);
        RuleFor(x => x.ColorHex).Matches("^#[0-9A-Fa-f]{6}$").When(x => x.ColorHex is not null);
        RuleFor(x => x.StorageGb).InclusiveBetween(1, 4096).When(x => x.StorageGb is not null);
        RuleFor(x => x.RamGb).InclusiveBetween(1, 64).When(x => x.RamGb is not null);
        RuleFor(x => x.Price).GreaterThan(0).PrecisionScale(18, 3, ignoreTrailingZeros: true);
        RuleFor(x => x.CompareAtPrice).GreaterThan(x => x.Price).When(x => x.CompareAtPrice is not null);
    }
}

public sealed class AddVariantValidator : AbstractValidator<AddVariantCommand>
{
    public AddVariantValidator()
    {
        RuleFor(x => x.Variant).NotNull().SetValidator(new VariantInputValidator());
        RuleFor(x => x.StockQuantity).InclusiveBetween(0, ProductVariant.MaxStock);
    }
}

public sealed class UpdateVariantValidator : AbstractValidator<UpdateVariantCommand>
{
    public UpdateVariantValidator() => RuleFor(x => x.Variant).NotNull().SetValidator(new VariantInputValidator());
}

internal sealed class AddVariantHandler(IAppDbContext db) : ICommandHandler<AddVariantCommand, Guid>
{
    public async Task<Result<Guid>> Handle(AddVariantCommand command, CancellationToken cancellationToken)
    {
        var product = await ProductLoader.LoadAsync(db, command.ProductId, cancellationToken);
        if (product is null)
        {
            return CatalogErrors.ProductNotFound;
        }

        if (await ProductLoader.SkuUsedElsewhereAsync(db, command.Variant.Sku, product.Id, cancellationToken))
        {
            return CatalogErrors.SkuTaken;
        }

        var v = command.Variant;
        var result = product.AddVariant(v.Sku, v.ToOptions(), v.Price, v.CompareAtPrice, command.StockQuantity);
        if (result.IsFailure)
        {
            return result.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        return result.Value.Id;
    }
}

internal sealed class UpdateVariantHandler(IAppDbContext db) : ICommandHandler<UpdateVariantCommand>
{
    public async Task<Result> Handle(UpdateVariantCommand command, CancellationToken cancellationToken)
    {
        var product = await ProductLoader.LoadAsync(db, command.ProductId, cancellationToken);
        if (product is null)
        {
            return CatalogErrors.ProductNotFound;
        }

        if (await ProductLoader.SkuUsedElsewhereAsync(db, command.Variant.Sku, product.Id, cancellationToken))
        {
            return CatalogErrors.SkuTaken;
        }

        var v = command.Variant;
        var result = product.UpdateVariant(command.VariantId, v.Sku, v.ToOptions(), v.Price, v.CompareAtPrice, command.IsActive);
        if (result.IsFailure)
        {
            return result;
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

/// <summary>Adds (positive) or removes (negative) stock, e.g. after receiving a shipment or a stock count.</summary>
public sealed record AdjustStockCommand(Guid ProductId, Guid VariantId, int Delta) : ICommand;

public sealed class AdjustStockValidator : AbstractValidator<AdjustStockCommand>
{
    public AdjustStockValidator() =>
        RuleFor(x => x.Delta).NotEqual(0).InclusiveBetween(-ProductVariant.MaxStock, ProductVariant.MaxStock);
}

internal sealed class AdjustStockHandler(IAppDbContext db) : ICommandHandler<AdjustStockCommand>
{
    public async Task<Result> Handle(AdjustStockCommand command, CancellationToken cancellationToken)
    {
        var product = await ProductLoader.LoadAsync(db, command.ProductId, cancellationToken);
        if (product is null)
        {
            return CatalogErrors.ProductNotFound;
        }

        var result = product.AdjustStock(command.VariantId, command.Delta);
        if (result.IsFailure)
        {
            return result;
        }

        // A checkout may reserve the same variant concurrently; the row version turns that race into a retryable conflict.
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

// ---------- Specifications & compatibility ----------

public sealed record ReplaceSpecificationsCommand(Guid ProductId, IReadOnlyList<SpecificationInputDto> Specifications) : ICommand;

public sealed class ReplaceSpecificationsValidator : AbstractValidator<ReplaceSpecificationsCommand>
{
    public ReplaceSpecificationsValidator()
    {
        RuleFor(x => x.Specifications).NotNull().Must(s => s.Count <= Product.MaxSpecifications)
            .WithMessage($"A product can have at most {Product.MaxSpecifications} specifications.");
        RuleForEach(x => x.Specifications).ChildRules(spec =>
        {
            spec.RuleFor(s => s.Label).NotNull().SetValidator(new LocalizedTextValidator(100));
            spec.RuleFor(s => s.Value).NotNull().SetValidator(new LocalizedTextValidator(500));
        });
    }
}

internal sealed class ReplaceSpecificationsHandler(IAppDbContext db) : ICommandHandler<ReplaceSpecificationsCommand>
{
    public async Task<Result> Handle(ReplaceSpecificationsCommand command, CancellationToken cancellationToken)
    {
        var product = await db.Products.Include(p => p.Specifications)
            .FirstOrDefaultAsync(p => p.Id == command.ProductId, cancellationToken);
        if (product is null)
        {
            return CatalogErrors.ProductNotFound;
        }

        product.ReplaceSpecifications(command.Specifications.Select(s => new SpecificationInput(s.Label.ToDomain(), s.Value.ToDomain())));
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public sealed record SetCompatiblePhonesCommand(Guid ProductId, IReadOnlyList<Guid> PhoneProductIds) : ICommand;

public sealed class SetCompatiblePhonesValidator : AbstractValidator<SetCompatiblePhonesCommand>
{
    public SetCompatiblePhonesValidator() => RuleFor(x => x.PhoneProductIds).NotNull().Must(ids => ids.Count <= 200);
}

internal sealed class SetCompatiblePhonesHandler(IAppDbContext db) : ICommandHandler<SetCompatiblePhonesCommand>
{
    public async Task<Result> Handle(SetCompatiblePhonesCommand command, CancellationToken cancellationToken)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == command.ProductId, cancellationToken);
        if (product is null)
        {
            return CatalogErrors.ProductNotFound;
        }

        var ids = command.PhoneProductIds.Distinct().ToList();
        var phoneCount = await db.Products.CountAsync(p => ids.Contains(p.Id) && p.Type == ProductType.Phone, cancellationToken);
        if (phoneCount != ids.Count)
        {
            return CatalogErrors.CompatibilityTargetsMustBePhones;
        }

        var result = product.SetCompatiblePhones(ids);
        if (result.IsFailure)
        {
            return result;
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

internal static class ProductLoader
{
    /// <summary>Loads a product with its variants, which most write operations need.</summary>
    public static Task<Product?> LoadAsync(IAppDbContext db, Guid productId, CancellationToken cancellationToken) =>
        db.Products.Include(p => p.Variants).FirstOrDefaultAsync(p => p.Id == productId, cancellationToken);

    /// <summary>SKUs are unique across the whole catalogue, not just within a product.</summary>
    public static Task<bool> SkuUsedElsewhereAsync(IAppDbContext db, string sku, Guid productId, CancellationToken cancellationToken)
    {
        var normalized = sku.Trim().ToUpperInvariant();
        return db.ProductVariants.AnyAsync(v => v.Sku == normalized && v.ProductId != productId, cancellationToken);
    }
}
