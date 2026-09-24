using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Store.Api.Common;
using Store.Api.Contracts;
using Store.Application.Abstractions;
using Store.Application.Catalog.Admin;

namespace Store.Api.Controllers.Admin;

[Route("api/admin/categories")]
[Authorize(Policy = Policies.Admin)]
public sealed class AdminCategoriesController : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromServices] IQueryHandler<GetAdminCategoriesQuery, IReadOnlyList<AdminCategoryDto>> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new GetAdminCategoriesQuery(), ct));

    [HttpPost]
    public async Task<IActionResult> Create(
        SaveCategoryRequest c,
        [FromServices] ICommandHandler<SaveCategoryCommand, Guid> handler,
        CancellationToken ct) =>
        CreatedFromResult(
            await handler.Handle(new SaveCategoryCommand(null, c.Name, c.Slug, c.ProductType, c.SortOrder, c.IsActive), ct),
            id => $"/api/admin/categories/{id}");

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(
        Guid id,
        SaveCategoryRequest c,
        [FromServices] ICommandHandler<SaveCategoryCommand, Guid> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new SaveCategoryCommand(id, c.Name, c.Slug, c.ProductType, c.SortOrder, c.IsActive), ct));

    /// <summary>Body: the category ids of one product type, in the new display order.</summary>
    [HttpPut("order")]
    public async Task<IActionResult> Reorder(
        IReadOnlyList<Guid> categoryIds,
        [FromServices] ICommandHandler<ReorderCategoriesCommand> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new ReorderCategoriesCommand(categoryIds), ct));

    /// <summary>Only for categories no product uses; others are hidden instead (409 catalog.category_in_use).</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, [FromServices] ICommandHandler<DeleteCategoryCommand> handler, CancellationToken ct) =>
        FromResult(await handler.Handle(new DeleteCategoryCommand(id), ct));
}
