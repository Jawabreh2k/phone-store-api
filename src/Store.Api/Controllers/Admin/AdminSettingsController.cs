using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Store.Api.Common;
using Store.Application.Abstractions;
using Store.Application.Settings;

namespace Store.Api.Controllers.Admin;

/// <summary>Branding, contact numbers, currency, phone country code and shipping rules.</summary>
[Route("api/admin/settings")]
[Authorize(Policy = Policies.Admin)]
public sealed class AdminSettingsController : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(
        [FromServices] IQueryHandler<GetAdminSettingsQuery, AdminSettingsDto> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new GetAdminSettingsQuery(), ct));

    [HttpPut]
    public async Task<IActionResult> Update(
        AdminSettingsDto body,
        [FromServices] ICommandHandler<UpdateSettingsCommand> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new UpdateSettingsCommand(body), ct));
}
