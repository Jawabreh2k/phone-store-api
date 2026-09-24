using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Store.Api.Common;
using Store.Application.Abstractions;
using Store.Application.Dashboard;

namespace Store.Api.Controllers.Admin;

[Route("api/admin/dashboard")]
[Authorize(Policy = Policies.Admin)]
public sealed class AdminDashboardController : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(
        [FromServices] IQueryHandler<GetAdminDashboardQuery, AdminDashboardDto> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new GetAdminDashboardQuery(), ct));
}
