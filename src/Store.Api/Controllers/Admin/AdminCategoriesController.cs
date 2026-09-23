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
}
