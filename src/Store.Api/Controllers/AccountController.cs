using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Store.Api.Common;
using Store.Api.Contracts;
using Store.Application.Abstractions;
using Store.Application.Common;
using Store.Application.Customers;
using Store.Application.Orders;

namespace Store.Api.Controllers;

/// <summary>
/// The signed-in customer's own data. Every action scopes by the user id from the auth cookie,
/// never by an id from the request, so one customer cannot reach another's data.
/// </summary>
[Route("api/account")]
[Authorize]
public sealed class AccountController(ICurrentUser user) : ApiControllerBase
{
    private Guid UserId => user.RequiredUserId;

    // ---- Profile ----

    [HttpPut("profile")]
    public async Task<IActionResult> UpdateProfile(
        UpdateProfileRequest body,
        [FromServices] ICommandHandler<UpdateProfileCommand, UserProfile> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new UpdateProfileCommand(UserId, body.FullName, body.PhoneNumber, body.PreferredLanguage), ct));

    [HttpPost("password")]
    [EnableRateLimiting(Policies.AuthRateLimit)]
    public async Task<IActionResult> ChangePassword(
        ChangePasswordRequest body,
        [FromServices] ICommandHandler<ChangePasswordCommand> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new ChangePasswordCommand(UserId, body.CurrentPassword, body.NewPassword), ct));

    // ---- Addresses ----

    [HttpGet("addresses")]
    public async Task<IActionResult> GetAddresses(
        [FromServices] IQueryHandler<GetMyAddressesQuery, IReadOnlyList<AddressDto>> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new GetMyAddressesQuery(UserId), ct));

    [HttpPost("addresses")]
    public async Task<IActionResult> AddAddress(
        SaveAddressRequest body,
        [FromServices] ICommandHandler<SaveAddressCommand, AddressDto> handler,
        CancellationToken ct) =>
        CreatedFromResult(
            await handler.Handle(new SaveAddressCommand(UserId, null, body.Label, body.RecipientName, body.Phone, body.Address, body.IsDefault), ct),
            a => $"/api/account/addresses/{a.Id}");

    [HttpPut("addresses/{id:guid}")]
    public async Task<IActionResult> UpdateAddress(
        Guid id,
        SaveAddressRequest body,
        [FromServices] ICommandHandler<SaveAddressCommand, AddressDto> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new SaveAddressCommand(UserId, id, body.Label, body.RecipientName, body.Phone, body.Address, body.IsDefault), ct));

    [HttpDelete("addresses/{id:guid}")]
    public async Task<IActionResult> DeleteAddress(
        Guid id,
        [FromServices] ICommandHandler<DeleteAddressCommand> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new DeleteAddressCommand(UserId, id), ct));

    // ---- Orders (by order number; the account itself is the credential) ----

    [HttpGet("orders")]
    public async Task<IActionResult> GetOrders(
        int? page,
        int? pageSize,
        [FromServices] IQueryHandler<GetMyOrdersQuery, PagedResult<OrderSummaryDto>> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new GetMyOrdersQuery(UserId, page, pageSize), ct));

    [HttpGet("orders/{orderNumber}")]
    public async Task<IActionResult> GetOrder(
        string orderNumber,
        [FromServices] IQueryHandler<GetMyOrderQuery, OrderDetailsDto> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new GetMyOrderQuery(UserId, orderNumber), ct));

    [HttpPost("orders/{orderNumber}/cancel")]
    public async Task<IActionResult> CancelOrder(
        string orderNumber,
        CancelOrderRequest body,
        [FromServices] ICommandHandler<CancelMyOrderCommand> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new CancelMyOrderCommand(UserId, orderNumber, body.Reason), ct));
}
