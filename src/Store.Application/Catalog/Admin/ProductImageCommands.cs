using Microsoft.EntityFrameworkCore;
using Store.Application.Abstractions;
using Store.Application.Catalog.Storefront;
using Store.Application.Common;
using Store.Domain.Catalog;
using Store.Domain.Common;

namespace Store.Application.Catalog.Admin;

public sealed record UploadProductImageCommand(Guid ProductId, Stream Content, long Length, Guid? VariantId) : ICommand<ImageDto>;

internal sealed class UploadProductImageHandler(IAppDbContext db, IFileStorage storage) : ICommandHandler<UploadProductImageCommand, ImageDto>
{
    public async Task<Result<ImageDto>> Handle(UploadProductImageCommand command, CancellationToken cancellationToken)
    {
        var extension = ImageFile.Validate(command.Content, command.Length, out var error);
        if (extension is null)
        {
            return error!;
        }

        var product = await db.Products.Include(p => p.Variants).Include(p => p.Images)
            .FirstOrDefaultAsync(p => p.Id == command.ProductId, cancellationToken);
        if (product is null)
        {
            return CatalogErrors.ProductNotFound;
        }

        // Check the domain rules before writing the file, so a rejected upload leaves nothing behind.
        if (product.Images.Count >= Product.MaxImages)
        {
            return CatalogErrors.TooManyImages;
        }

        var url = await storage.SaveAsync(command.Content, extension, $"products/{product.Id:N}", cancellationToken);
        var result = product.AddImage(url, command.VariantId);
        if (result.IsFailure)
        {
            await storage.DeleteAsync(url, cancellationToken);
            return result.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new ImageDto(result.Value.Id, url, result.Value.VariantId);
    }
}

public sealed record RemoveProductImageCommand(Guid ProductId, Guid ImageId) : ICommand;

internal sealed class RemoveProductImageHandler(IAppDbContext db, IFileStorage storage) : ICommandHandler<RemoveProductImageCommand>
{
    public async Task<Result> Handle(RemoveProductImageCommand command, CancellationToken cancellationToken)
    {
        var product = await db.Products.Include(p => p.Images)
            .FirstOrDefaultAsync(p => p.Id == command.ProductId, cancellationToken);
        if (product is null)
        {
            return CatalogErrors.ProductNotFound;
        }

        var result = product.RemoveImage(command.ImageId);
        if (result.IsFailure)
        {
            return result.Error;
        }

        await db.SaveChangesAsync(cancellationToken);

        // Delete the file only once the database no longer points at it. Order items keep their own
        // copy of the URL, so historic orders may show a missing image; that is acceptable for now.
        await storage.DeleteAsync(result.Value.Url, cancellationToken);
        return Result.Success();
    }
}

public sealed record ReorderProductImagesCommand(Guid ProductId, IReadOnlyList<Guid> ImageIds) : ICommand;

internal sealed class ReorderProductImagesHandler(IAppDbContext db) : ICommandHandler<ReorderProductImagesCommand>
{
    public async Task<Result> Handle(ReorderProductImagesCommand command, CancellationToken cancellationToken)
    {
        var product = await db.Products.Include(p => p.Images)
            .FirstOrDefaultAsync(p => p.Id == command.ProductId, cancellationToken);
        if (product is null)
        {
            return CatalogErrors.ProductNotFound;
        }

        product.ReorderImages(command.ImageIds);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
