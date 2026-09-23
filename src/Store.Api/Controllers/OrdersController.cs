using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Store.Api.Common;
using Store.Api.Contracts;
using Store.Application.Abstractions;
using Store.Application.Orders;

namespace Store.Api.Controllers;

/// <summary>Checkout and guest order tracking. Signed-in customers also use /api/account/orders.</summary>
[Route("api/orders")]
[AllowAnonymous]
public sealed class OrdersController(ICurrentUser user) : ApiControllerBase
{
    /// <summary>
    /// Places an order from the current cart. The response carries the guest tracking token;
    /// it is not stored in readable form, so this is the only time it is returned.
    /// </summary>
    [HttpPost]
    [EnableRateLimiting(Policies.CheckoutRateLimit)]
    public async Task<IActionResult> Place(
        PlaceOrderRequest body,
        [FromServices] ICommandHandler<PlaceOrderCommand, OrderPlacedDto> handler,
        CancellationToken ct)
    {
        var command = new PlaceOrderCommand(
            CartCookie.Owner(HttpContext, user), body.FullName, body.Phone, body.Email, body.Address, body.Note, body.SaveAddress);

        return CreatedFromResult(await handler.Handle(command, ct), o => $"/api/orders/track/{o.TrackingToken}");
    }

    /// <summary>Order status for whoever holds the tracking link from checkout.</summary>
    [HttpGet("track/{token}")]
    [EnableRateLimiting(Policies.TrackingRateLimit)]
    public async Task<IActionResult> Track(
        string token,
        [FromServices] IQueryHandler<TrackOrderQuery, OrderDetailsDto> handler,
        CancellationToken ct)
    {
        // The token is a credential: keep responses out of shared caches.
        Response.Headers.CacheControl = "no-store";
        return FromResult(await handler.Handle(new TrackOrderQuery(token), ct));
    }
}
