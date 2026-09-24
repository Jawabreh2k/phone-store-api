using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Store.Api.Common;
using Store.Application.Abstractions;
using Store.Application.Outbox;

namespace Store.Api.Controllers.Admin;

[Route("api/admin/notifications")]
[Authorize(Policy = Policies.Admin)]
public sealed class AdminNotificationsController : ApiControllerBase
{
    /// <summary>Sends a parked message again, starting now with a fresh set of attempts.</summary>
    [HttpPost("{id:guid}/retry")]
    public async Task<IActionResult> Retry(
        Guid id,
        [FromServices] ICommandHandler<RetryNotificationCommand> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new RetryNotificationCommand(id), ct));
}
