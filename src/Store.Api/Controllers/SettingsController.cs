using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Store.Api.Common;
using Store.Application.Abstractions;
using Store.Application.Settings;

namespace Store.Api.Controllers;

/// <summary>Store name, contact details and shipping rules for the storefront header, footer and checkout.</summary>
[Route("api/settings")]
[AllowAnonymous]
public sealed class SettingsController : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(
        [FromServices] IQueryHandler<GetPublicSettingsQuery, PublicSettingsDto> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new GetPublicSettingsQuery(), ct));
}
