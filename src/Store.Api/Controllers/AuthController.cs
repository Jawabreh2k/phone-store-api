using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Store.Api.Common;
using Store.Api.Contracts;
using Store.Application.Abstractions;
using Store.Application.Customers;
using Store.Domain.Common;

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

    /// <summary>Always 204. An already-signed-out browser still needs the cookie cleared, so this is anonymous.</summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout([FromServices] IIdentityService identity)
    {
        await identity.SignOutAsync();
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me([FromServices] ICurrentUser user, [FromServices] IIdentityService identity, CancellationToken ct) =>
        await identity.FindAsync(user.RequiredUserId, ct) is { } profile ? Ok(profile) : Unauthorized();

    /// <summary>Always 204, whether or not the email has an account, so it cannot be used to discover accounts.</summary>
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [EnableRateLimiting(Policies.AuthRateLimit)]
    public async Task<IActionResult> ForgotPassword(
        ForgotPasswordRequest body,
        [FromServices] ICommandHandler<ForgotPasswordCommand> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new ForgotPasswordCommand(body.Email), ct));

    [HttpPost("reset-password")]
    [AllowAnonymous]
    [EnableRateLimiting(Policies.AuthRateLimit)]
    public async Task<IActionResult> ResetPassword(
        ResetPasswordRequest body,
        [FromServices] ICommandHandler<ResetPasswordCommand> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new ResetPasswordCommand(body.Email, body.Token, body.NewPassword), ct));

    // Google sign-in is a chain of full-page browser redirects, not fetch calls, so these two actions answer with
    // redirects (errors travel back to the store as ?authError=<code>) instead of JSON.

    /// <summary>
    /// Sends the browser to Google. Google returns to /api/auth/google/callback (handled by the authentication
    /// middleware), which continues to <see cref="GoogleComplete"/>.
    /// </summary>
    [HttpGet("google/start")]
    [AllowAnonymous]
    [EnableRateLimiting(Policies.ExternalAuthRateLimit)]
    public async Task<IActionResult> GoogleStart(
        string? returnUrl,
        string? lang,
        [FromServices] ICommandHandler<StartExternalLoginCommand> handler,
        CancellationToken ct)
    {
        var back = LocalOrHome(returnUrl);
        var complete = Url.Action(nameof(GoogleComplete), new { returnUrl = back, lang })!;
        var result = await handler.Handle(new StartExternalLoginCommand(ExternalProviders.Google, complete), ct);

        // On success the challenge has already written the redirect to Google.
        return result.IsSuccess ? new EmptyResult() : LocalRedirect(WithAuthError(back, result.Error));
    }

    [HttpGet("google/complete")]
    [AllowAnonymous]
    [EnableRateLimiting(Policies.ExternalAuthRateLimit)]
    public async Task<IActionResult> GoogleComplete(
        string? returnUrl,
        string? lang,
        [FromServices] ICommandHandler<ExternalLoginCommand, UserProfile> handler,
        CancellationToken ct)
    {
        var back = LocalOrHome(returnUrl);
        var result = await handler.Handle(new ExternalLoginCommand(CartCookie.Read(HttpContext), ParseLanguage(lang)), ct);
        if (result.IsFailure)
        {
            return LocalRedirect(WithAuthError(back, result.Error));
        }

        CartCookie.Clear(HttpContext);
        return LocalRedirect(back);
    }

    /// <summary>
    /// Signed-in customer connects Google to the account they are already in. The provider email must match theirs.
    /// </summary>
    [HttpGet("google/connect")]
    [Authorize]
    [EnableRateLimiting(Policies.ExternalAuthRateLimit)]
    public async Task<IActionResult> GoogleConnect(
        string? returnUrl,
        [FromServices] ICommandHandler<StartExternalLoginCommand> handler,
        CancellationToken ct)
    {
        var back = LocalOrHome(returnUrl);
        var complete = Url.Action(nameof(GoogleConnectComplete), new { returnUrl = back })!;
        var result = await handler.Handle(new StartExternalLoginCommand(ExternalProviders.Google, complete), ct);
        return result.IsSuccess ? new EmptyResult() : LocalRedirect(WithAuthError(back, result.Error));
    }

    [HttpGet("google/connect/complete")]
    [Authorize]
    [EnableRateLimiting(Policies.ExternalAuthRateLimit)]
    public async Task<IActionResult> GoogleConnectComplete(
        string? returnUrl,
        [FromServices] ICurrentUser user,
        [FromServices] ICommandHandler<LinkExternalLoginCommand> handler,
        CancellationToken ct)
    {
        var back = LocalOrHome(returnUrl);
        var result = await handler.Handle(new LinkExternalLoginCommand(user.RequiredUserId), ct);
        return result.IsFailure ? LocalRedirect(WithAuthError(back, result.Error)) : LocalRedirect(back);
    }

    /// <summary>Only a path on this site: anything else (another host, //host, /\host) would make this an open redirect.</summary>
    private string LocalOrHome(string? returnUrl) => Url.IsLocalUrl(returnUrl) ? returnUrl : "/";

    private static string WithAuthError(string url, Error error) => QueryHelpers.AddQueryString(url, "authError", error.Code);

    /// <summary>The lang the storefront put on the start URL. Anything else leaves the choice to request localization.</summary>
    private static Language? ParseLanguage(string? lang) => lang switch
    {
        "en" => Language.En,
        "ar" => Language.Ar,
        _ => null,
    };
}
