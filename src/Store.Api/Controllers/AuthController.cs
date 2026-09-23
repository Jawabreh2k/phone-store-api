using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Store.Api.Common;
using Store.Api.Contracts;
using Store.Application.Abstractions;
using Store.Application.Customers;

namespace Store.Api.Controllers;

/// <summary>Cookie-based sign-in. Registering or signing in folds the guest cart into the account.</summary>
[Route("api/auth")]
public sealed class AuthController : ApiControllerBase
{
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(Policies.AuthRateLimit)]
    public async Task<IActionResult> Register(
        RegisterRequest body,
        [FromServices] ICommandHandler<RegisterCommand, UserProfile> handler,
        CancellationToken ct)
    {
        var command = new RegisterCommand(body.Email, body.Password, body.FullName, body.PhoneNumber, body.PreferredLanguage, CartCookie.Read(HttpContext));
        var result = await handler.Handle(command, ct);
        if (result.IsSuccess)
        {
            CartCookie.Clear(HttpContext);
        }

        return FromResult(result);
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(Policies.AuthRateLimit)]
    public async Task<IActionResult> Login(
        LoginRequest body,
        [FromServices] ICommandHandler<LoginCommand, UserProfile> handler,
        CancellationToken ct)
    {
        var result = await handler.Handle(new LoginCommand(body.Email, body.Password, body.RememberMe, CartCookie.Read(HttpContext)), ct);
        if (result.IsSuccess)
        {
            CartCookie.Clear(HttpContext);
        }

        return FromResult(result);
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout([FromServices] IIdentityService identity)
    {
        await identity.SignOutAsync();
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me([FromServices] ICurrentUser user, [FromServices] IIdentityService identity, CancellationToken ct) =>
        await identity.FindAsync(user.RequiredUserId, ct) is { } profile ? Ok(profile) : Unauthorized();
}
