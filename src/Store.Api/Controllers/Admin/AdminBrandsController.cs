using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Store.Api.Common;
using Store.Api.Contracts;
using Store.Application.Abstractions;
using Store.Application.Catalog.Admin;

namespace Store.Api.Controllers.Admin;

[Route("api/admin/brands")]
[Authorize(Policy = Policies.Admin)]
public sealed class AdminBrandsController : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromServices] IQueryHandler<GetAdminBrandsQuery, IReadOnlyList<AdminBrandDto>> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new GetAdminBrandsQuery(), ct));

    [HttpPost]
    public async Task<IActionResult> Create(
        SaveBrandRequest b,
        [FromServices] ICommandHandler<SaveBrandCommand, Guid> handler,
        CancellationToken ct) =>
        CreatedFromResult(
            await handler.Handle(new SaveBrandCommand(null, b.Name, b.Slug, b.LogoUrl, b.SortOrder, b.IsActive), ct),
            id => $"/api/admin/brands/{id}");

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(
        Guid id,
        SaveBrandRequest b,
        [FromServices] ICommandHandler<SaveBrandCommand, Guid> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new SaveBrandCommand(id, b.Name, b.Slug, b.LogoUrl, b.SortOrder, b.IsActive), ct));
}
