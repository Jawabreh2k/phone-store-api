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
            .Select(b => new AdminBrandDto(b.Id, b.Name, b.Slug, b.LogoUrl, b.SortOrder, b.IsActive))
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

// ---------- Categories ----------

public sealed record GetAdminCategoriesQuery : IQuery<IReadOnlyList<AdminCategoryDto>>;

internal sealed class GetAdminCategoriesHandler(IAppDbContext db) : IQueryHandler<GetAdminCategoriesQuery, IReadOnlyList<AdminCategoryDto>>
{
    public async Task<Result<IReadOnlyList<AdminCategoryDto>>> Handle(GetAdminCategoriesQuery query, CancellationToken cancellationToken) =>
        await db.Categories.AsNoTracking()
            .OrderBy(c => c.ProductType).ThenBy(c => c.SortOrder)
            .Select(c => new AdminCategoryDto(c.Id, new LocalizedTextDto(c.Name.En, c.Name.Ar), c.Slug, c.ProductType, c.SortOrder, c.IsActive))
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
