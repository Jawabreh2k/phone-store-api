using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Store.Api.Common;
using Store.Api.Contracts;
using Store.Application.Abstractions;
using Store.Application.Common;
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
        UpdateSettingsRequest s,
        [FromServices] ICommandHandler<UpdateSettingsCommand> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(
            new UpdateSettingsCommand(s.StoreName, s.SupportPhone, s.WhatsAppNumber, s.SupportEmail, s.CurrencyCode, s.PhoneCountryCode, s.FlatShippingFee, s.FreeShippingThreshold, s.AllowGuestCheckout),
            ct));

    /// <summary>Multipart upload (field "file"); returns the logo's URL. Same antiforgery reasoning as product images.</summary>
    [HttpPost("logo")]
    [RequestSizeLimit(ImageFile.MaxBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = ImageFile.MaxBytes + 64 * 1024)]
    public async Task<IActionResult> UploadLogo(
        IFormFile file,
        [FromServices] ICommandHandler<UploadStoreLogoCommand, string> handler,
        CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        return FromResult(await handler.Handle(new UploadStoreLogoCommand(stream, file.Length), ct));
    }

    [HttpDelete("logo")]
    public async Task<IActionResult> RemoveLogo([FromServices] ICommandHandler<RemoveStoreLogoCommand> handler, CancellationToken ct) =>
        FromResult(await handler.Handle(new RemoveStoreLogoCommand(), ct));
}
