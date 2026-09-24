using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Store.Application.Abstractions;
using Store.Application.Common;
using Store.Domain.Catalog;
using Store.Domain.Common;

namespace Store.Application.Catalog.Admin;

// ---------- Brands ----------

public sealed record GetAdminBrandsQuery : IQuery<IReadOnlyList<AdminBrandDto>>;

internal sealed class GetAdminBrandsHandler(IAppDbContext db) : IQueryHandler<GetAdminBrandsQuery, IReadOnlyList<AdminBrandDto>>
{
    public async Task<Result<IReadOnlyList<AdminBrandDto>>> Handle(GetAdminBrandsQuery query, CancellationToken cancellationToken) =>
        await db.Brands.AsNoTracking()
            .OrderBy(b => b.SortOrder).ThenBy(b => b.Name)
            .Select(b => new AdminBrandDto(b.Id, b.Name, b.Slug, b.LogoUrl, b.SortOrder, b.IsActive, db.Products.Count(p => p.BrandId == b.Id)))
            .ToListAsync(cancellationToken);
}

public sealed record SaveBrandCommand(Guid? Id, string Name, string? Slug, string? LogoUrl, int SortOrder, bool IsActive) : ICommand<Guid>;

public sealed class SaveBrandValidator : AbstractValidator<SaveBrandCommand>
{
    public SaveBrandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Brand.NameMaxLength);
        RuleFor(x => x.Slug!).ValidSlug().When(x => !string.IsNullOrEmpty(x.Slug));
        RuleFor(x => x.LogoUrl).MaximumLength(ProductImage.UrlMaxLength);
    }
}

/// <summary>Creates a brand when <see cref="SaveBrandCommand.Id"/> is null, otherwise updates it.</summary>
internal sealed class SaveBrandHandler(IAppDbContext db) : ICommandHandler<SaveBrandCommand, Guid>
{
    public async Task<Result<Guid>> Handle(SaveBrandCommand command, CancellationToken cancellationToken)
    {
        var slug = string.IsNullOrEmpty(command.Slug) ? Slug.From(command.Name) : command.Slug;
        if (!Slug.IsValid(slug))
        {
            return Error.Validation("catalog.slug_required", "Enter a slug using English letters and numbers.");
        }

        if (await db.Brands.AnyAsync(b => b.Slug == slug && b.Id != command.Id, cancellationToken))
        {
            return CatalogErrors.SlugTaken;
        }

        Brand? brand;
        if (command.Id is { } id)
        {
            brand = await db.Brands.FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
            if (brand is null)
            {
                return CatalogErrors.BrandNotFound;
            }

            brand.Rename(command.Name, slug);
        }
        else
        {
            brand = new Brand(command.Name, slug);
            db.Brands.Add(brand);
        }

        brand.Update(command.LogoUrl, command.SortOrder, command.IsActive);
        await db.SaveChangesAsync(cancellationToken);
        return brand.Id;
    }
}

/// <summary>The store's brand filter order: the brands listed take positions 1, 2, 3… in that order.</summary>
public sealed record ReorderBrandsCommand(IReadOnlyList<Guid> BrandIds) : ICommand;

public sealed class ReorderBrandsValidator : AbstractValidator<ReorderBrandsCommand>
{
    public ReorderBrandsValidator() =>
        RuleFor(x => x.BrandIds).NotEmpty().Must(ids => ids.Count <= 500 && ids.Distinct().Count() == ids.Count)
            .WithMessage("List each brand once, at most 500.");
}

internal sealed class ReorderBrandsHandler(IAppDbContext db) : ICommandHandler<ReorderBrandsCommand>
{
    public async Task<Result> Handle(ReorderBrandsCommand command, CancellationToken cancellationToken)
    {
        var ids = command.BrandIds;
        var brands = await db.Brands.Where(b => ids.Contains(b.Id)).ToDictionaryAsync(b => b.Id, cancellationToken);
        if (brands.Count != ids.Count)
        {
            return CatalogErrors.BrandNotFound;
        }

        for (var i = 0; i < ids.Count; i++)
        {
            brands[ids[i]].MoveTo(i + 1);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

/// <summary>Deletes a brand no product uses (e.g. one created by mistake). Brands in use are hidden instead.</summary>
public sealed record DeleteBrandCommand(Guid BrandId) : ICommand;

internal sealed class DeleteBrandHandler(IAppDbContext db) : ICommandHandler<DeleteBrandCommand>
{
    public async Task<Result> Handle(DeleteBrandCommand command, CancellationToken cancellationToken)
    {
        var brand = await db.Brands.FirstOrDefaultAsync(b => b.Id == command.BrandId, cancellationToken);
        if (brand is null)
        {
            return CatalogErrors.BrandNotFound;
        }

        if (await db.Products.AnyAsync(p => p.BrandId == brand.Id, cancellationToken))
        {
            return CatalogErrors.BrandInUse;
        }

        db.Brands.Remove(brand);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

// ---------- Categories ----------

public sealed record GetAdminCategoriesQuery : IQuery<IReadOnlyList<AdminCategoryDto>>;

internal sealed class GetAdminCategoriesHandler(IAppDbContext db) : IQueryHandler<GetAdminCategoriesQuery, IReadOnlyList<AdminCategoryDto>>
{
    public async Task<Result<IReadOnlyList<AdminCategoryDto>>> Handle(GetAdminCategoriesQuery query, CancellationToken cancellationToken) =>
        await db.Categories.AsNoTracking()
            .OrderBy(c => c.ProductType).ThenBy(c => c.SortOrder)
            .Select(c => new AdminCategoryDto(
                c.Id, new LocalizedTextDto(c.Name.En, c.Name.Ar), c.Slug, c.ProductType, c.SortOrder, c.IsActive,
                db.Products.Count(p => p.CategoryId == c.Id)))
            .ToListAsync(cancellationToken);
}

public sealed record SaveCategoryCommand(Guid? Id, LocalizedTextDto Name, string? Slug, ProductType ProductType, int SortOrder, bool IsActive) : ICommand<Guid>;

public sealed class SaveCategoryValidator : AbstractValidator<SaveCategoryCommand>
{
    public SaveCategoryValidator()
    {
        RuleFor(x => x.Name).NotNull().SetValidator(new LocalizedTextValidator(100));
        RuleFor(x => x.Slug!).ValidSlug().When(x => !string.IsNullOrEmpty(x.Slug));
        RuleFor(x => x.ProductType).IsInEnum();
    }
}

internal sealed class SaveCategoryHandler(IAppDbContext db) : ICommandHandler<SaveCategoryCommand, Guid>
{
    public async Task<Result<Guid>> Handle(SaveCategoryCommand command, CancellationToken cancellationToken)
    {
        var slug = string.IsNullOrEmpty(command.Slug) ? Slug.From(command.Name.En) : command.Slug;
        if (!Slug.IsValid(slug))
        {
            return Error.Validation("catalog.slug_required", "Enter a slug using English letters and numbers.");
        }

        if (await db.Categories.AnyAsync(c => c.Slug == slug && c.Id != command.Id, cancellationToken))
        {
            return CatalogErrors.SlugTaken;
        }

        Category? category;
        if (command.Id is { } id)
        {
            category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
            if (category is null)
            {
                return CatalogErrors.CategoryNotFound;
            }

            // Moving a category between phones and accessories would orphan its products' type.
            if (category.ProductType != command.ProductType)
            {
                return CatalogErrors.CategoryTypeMismatch;
            }

            category.Update(command.Name.ToDomain(), slug, command.SortOrder, command.IsActive);
        }
        else
        {
            category = new Category(command.Name.ToDomain(), slug, command.ProductType, command.SortOrder);
            if (!command.IsActive)
            {
                category.Update(category.Name, slug, command.SortOrder, isActive: false);
            }

            db.Categories.Add(category);
        }

        await db.SaveChangesAsync(cancellationToken);
        return category.Id;
    }
}

/// <summary>
/// Display order within one product type: the categories listed take positions 1, 2, 3… in that order.
/// Mixing phone and accessory categories is rejected, since each type is ordered on its own.
/// </summary>
public sealed record ReorderCategoriesCommand(IReadOnlyList<Guid> CategoryIds) : ICommand;

public sealed class ReorderCategoriesValidator : AbstractValidator<ReorderCategoriesCommand>
{
    public ReorderCategoriesValidator() =>
        RuleFor(x => x.CategoryIds).NotEmpty().Must(ids => ids.Count <= 500 && ids.Distinct().Count() == ids.Count)
            .WithMessage("List each category once, at most 500.");
}

internal sealed class ReorderCategoriesHandler(IAppDbContext db) : ICommandHandler<ReorderCategoriesCommand>
{
    public async Task<Result> Handle(ReorderCategoriesCommand command, CancellationToken cancellationToken)
    {
        var ids = command.CategoryIds;
        var categories = await db.Categories.Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, cancellationToken);
        if (categories.Count != ids.Count)
        {
            return CatalogErrors.CategoryNotFound;
        }

        if (categories.Values.Select(c => c.ProductType).Distinct().Count() > 1)
        {
            return CatalogErrors.CategoryTypeMismatch;
        }

        for (var i = 0; i < ids.Count; i++)
        {
            categories[ids[i]].MoveTo(i + 1);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

/// <summary>Deletes a category no product uses. Categories in use are hidden instead.</summary>
public sealed record DeleteCategoryCommand(Guid CategoryId) : ICommand;

internal sealed class DeleteCategoryHandler(IAppDbContext db) : ICommandHandler<DeleteCategoryCommand>
{
    public async Task<Result> Handle(DeleteCategoryCommand command, CancellationToken cancellationToken)
    {
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == command.CategoryId, cancellationToken);
        if (category is null)
        {
            return CatalogErrors.CategoryNotFound;
        }

        if (await db.Products.AnyAsync(p => p.CategoryId == category.Id, cancellationToken))
        {
            return CatalogErrors.CategoryInUse;
        }

        db.Categories.Remove(category);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
