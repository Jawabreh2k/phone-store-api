using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Store.Api.Common;
using Store.Api.Contracts;
using Store.Application.Abstractions;
using Store.Application.Catalog.Admin;
using Store.Application.Catalog.Storefront;
using Store.Application.Common;

namespace Store.Api.Controllers.Admin;

/// <summary>Product editor: details, publishing, variants and stock, specifications, compatibility and photos.</summary>
[Route("api/admin/products")]
[Authorize(Policy = Policies.Admin)]
public sealed class AdminProductsController : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Search(
        [FromQuery] GetAdminProductsQuery query,
        [FromServices] IQueryHandler<GetAdminProductsQuery, PagedResult<AdminProductListItemDto>> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(query, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(
        Guid id,
        [FromServices] IQueryHandler<GetAdminProductQuery, AdminProductDto> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new GetAdminProductQuery(id), ct));

    [HttpPost]
    public async Task<IActionResult> Create(
        SaveProductRequest p,
        [FromServices] ICommandHandler<SaveProductCommand, Guid> handler,
        CancellationToken ct) =>
        CreatedFromResult(
            await handler.Handle(new SaveProductCommand(null, p.Type, p.BrandId, p.CategoryId, p.Name, p.Slug, p.Description, p.IsFeatured), ct),
            id => $"/api/admin/products/{id}");

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(
        Guid id,
        SaveProductRequest p,
        [FromServices] ICommandHandler<SaveProductCommand, Guid> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new SaveProductCommand(id, p.Type, p.BrandId, p.CategoryId, p.Name, p.Slug, p.Description, p.IsFeatured), ct));

    [HttpPost("{id:guid}/publish")]
    public async Task<IActionResult> Publish(Guid id, [FromServices] ICommandHandler<SetProductPublishedCommand> handler, CancellationToken ct) =>
        FromResult(await handler.Handle(new SetProductPublishedCommand(id, Published: true), ct));

    [HttpPost("{id:guid}/unpublish")]
    public async Task<IActionResult> Unpublish(Guid id, [FromServices] ICommandHandler<SetProductPublishedCommand> handler, CancellationToken ct) =>
        FromResult(await handler.Handle(new SetProductPublishedCommand(id, Published: false), ct));

    // ---- Variants & stock ----

    [HttpPost("{id:guid}/variants")]
    public async Task<IActionResult> AddVariant(
        Guid id,
        AddVariantRequest v,
        [FromServices] ICommandHandler<AddVariantCommand, Guid> handler,
        CancellationToken ct) =>
        CreatedFromResult(await handler.Handle(new AddVariantCommand(id, v.Variant, v.StockQuantity), ct), _ => $"/api/admin/products/{id}");

    [HttpPut("{id:guid}/variants/{variantId:guid}")]
    public async Task<IActionResult> UpdateVariant(
        Guid id,
        Guid variantId,
        UpdateVariantRequest v,
        [FromServices] ICommandHandler<UpdateVariantCommand> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new UpdateVariantCommand(id, variantId, v.Variant, v.IsActive), ct));

    /// <summary>Adds (positive) or removes (negative) units, e.g. after a delivery or a stock count.</summary>
    [HttpPost("{id:guid}/variants/{variantId:guid}/stock")]
    public async Task<IActionResult> AdjustStock(
        Guid id,
        Guid variantId,
        AdjustStockRequest s,
        [FromServices] ICommandHandler<AdjustStockCommand> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new AdjustStockCommand(id, variantId, s.Delta), ct));

    // ---- Specifications & compatibility ----

    [HttpPut("{id:guid}/specifications")]
    public async Task<IActionResult> ReplaceSpecifications(
        Guid id,
        IReadOnlyList<SpecificationInputDto> specifications,
        [FromServices] ICommandHandler<ReplaceSpecificationsCommand> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new ReplaceSpecificationsCommand(id, specifications), ct));

    [HttpPut("{id:guid}/compatibility")]
    public async Task<IActionResult> SetCompatibility(
        Guid id,
        IReadOnlyList<Guid> phoneProductIds,
        [FromServices] ICommandHandler<SetCompatiblePhonesCommand> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new SetCompatiblePhonesCommand(id, phoneProductIds), ct));

    // ---- Images ----

    /// <summary>
    /// Multipart upload (field "file"). No antiforgery token: the auth cookie is SameSite=Lax, so a
    /// cross-site form post arrives unauthenticated and the Admin policy rejects it.
    /// </summary>
    [HttpPost("{id:guid}/images")]
    [RequestSizeLimit(ImageFile.MaxBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = ImageFile.MaxBytes + 64 * 1024)]
    public async Task<IActionResult> UploadImage(
        Guid id,
        IFormFile file,
        [FromQuery] Guid? variantId,
        [FromServices] ICommandHandler<UploadProductImageCommand, ImageDto> handler,
        CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        return FromResult(await handler.Handle(new UploadProductImageCommand(id, stream, file.Length, variantId), ct));
    }

    [HttpDelete("{id:guid}/images/{imageId:guid}")]
    public async Task<IActionResult> RemoveImage(
        Guid id,
        Guid imageId,
        [FromServices] ICommandHandler<RemoveProductImageCommand> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new RemoveProductImageCommand(id, imageId), ct));

    [HttpPut("{id:guid}/images/order")]
    public async Task<IActionResult> ReorderImages(
        Guid id,
        IReadOnlyList<Guid> imageIds,
        [FromServices] ICommandHandler<ReorderProductImagesCommand> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new ReorderProductImagesCommand(id, imageIds), ct));
}
