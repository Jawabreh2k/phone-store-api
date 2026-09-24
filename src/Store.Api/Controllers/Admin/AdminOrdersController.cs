using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Store.Api.Common;
using Store.Api.Contracts;
using Store.Application.Abstractions;
using Store.Application.Common;
using Store.Application.Orders;

namespace Store.Api.Controllers.Admin;

[Route("api/admin/orders")]
[Authorize(Policy = Policies.Admin)]
public sealed class AdminOrdersController : ApiControllerBase
{
    [HttpGet("pending-count")]
    public async Task<IActionResult> PendingCount(
        [FromServices] IQueryHandler<GetPendingOrderCountQuery, int> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new GetPendingOrderCountQuery(), ct));

    /// <summary>Search matches order number, customer name, or phone in any format ("079 123…").</summary>
    [HttpGet]
    public async Task<IActionResult> Search(
        [FromQuery] GetAdminOrdersQuery query,
        [FromServices] IQueryHandler<GetAdminOrdersQuery, PagedResult<AdminOrderListItemDto>> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(query, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(
        Guid id,
        [FromServices] IQueryHandler<GetAdminOrderQuery, AdminOrderDetailsDto> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new GetAdminOrderQuery(id), ct));

    /// <summary>Moves the order along its workflow; cancelling returns its stock.</summary>
    [HttpPost("{id:guid}/status")]
    public async Task<IActionResult> ChangeStatus(
        Guid id,
        ChangeOrderStatusRequest body,
        [FromServices] ICommandHandler<ChangeOrderStatusCommand> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new ChangeOrderStatusCommand(id, body.Status, body.Note), ct));
}
