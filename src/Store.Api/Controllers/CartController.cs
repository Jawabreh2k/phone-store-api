using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Store.Api.Common;
using Store.Api.Contracts;
using Store.Application.Abstractions;
using Store.Application.Carts;

namespace Store.Api.Controllers;

/// <summary>The shopper's cart: the signed-in customer's, or a guest cart identified by the cart cookie.</summary>
[Route("api/cart")]
[AllowAnonymous]
public sealed class CartController(ICurrentUser user) : ApiControllerBase
{
    private CartOwner Owner => CartCookie.Owner(HttpContext, user);

    /// <summary>Returns 204 when the shopper has no cart yet.</summary>
    [HttpGet]
    public async Task<IActionResult> Get([FromServices] IQueryHandler<GetCartQuery, CartDto?> handler, CancellationToken ct)
    {
        var result = await handler.Handle(new GetCartQuery(Owner), ct);
        return result.IsSuccess && result.Value is null ? NoContent() : FromResult(result);
    }

    [HttpPost("items")]
    public async Task<IActionResult> AddItem(
        AddCartItemRequest body,
        [FromServices] ICommandHandler<AddToCartCommand, CartDto> handler,
        CancellationToken ct)
    {
        var result = await handler.Handle(new AddToCartCommand(Owner, body.VariantId, body.Quantity), ct);
        if (result.IsSuccess && user.UserId is null)
        {
            CartCookie.Write(HttpContext, result.Value.CartId); // first item creates the guest cart
        }

        return FromResult(result);
    }

    [HttpPut("items/{variantId:guid}")]
    public async Task<IActionResult> UpdateItem(
        Guid variantId,
        UpdateCartItemRequest body,
        [FromServices] ICommandHandler<UpdateCartItemCommand, CartDto> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new UpdateCartItemCommand(Owner, variantId, body.Quantity), ct));

    [HttpDelete("items/{variantId:guid}")]
    public async Task<IActionResult> RemoveItem(
        Guid variantId,
        [FromServices] ICommandHandler<UpdateCartItemCommand, CartDto> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new UpdateCartItemCommand(Owner, variantId, 0), ct));
}
