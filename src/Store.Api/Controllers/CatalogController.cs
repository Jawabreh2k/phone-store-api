using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Store.Api.Common;
using Store.Application.Abstractions;
using Store.Application.Catalog.Storefront;
using Store.Application.Common;
using Store.Domain.Catalog;

namespace Store.Api.Controllers;

/// <summary>Public, read-only catalogue. Text is returned in the language from ?lang=ar|en or Accept-Language.</summary>
[Route("api/catalog")]
[AllowAnonymous]
public sealed class CatalogController : ApiControllerBase
{
    [HttpGet("brands")]
    public async Task<IActionResult> GetBrands(
        ProductType? type,
        [FromServices] IQueryHandler<GetBrandsQuery, IReadOnlyList<BrandDto>> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new GetBrandsQuery(type), ct));

    [HttpGet("categories")]
    public async Task<IActionResult> GetCategories(
        ProductType? type,
        [FromServices] IQueryHandler<GetCategoriesQuery, IReadOnlyList<CategoryDto>> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new GetCategoriesQuery(type), ct));

    [HttpGet("products")]
    public async Task<IActionResult> SearchProducts(
        [FromQuery] SearchProductsQuery query,
        [FromServices] IQueryHandler<SearchProductsQuery, PagedResult<ProductSummaryDto>> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(query, ct));

    [HttpGet("products/{slug}")]
    public async Task<IActionResult> GetProduct(
        string slug,
        [FromServices] IQueryHandler<GetProductBySlugQuery, ProductDetailsDto> handler,
        CancellationToken ct) =>
        FromResult(await handler.Handle(new GetProductBySlugQuery(slug), ct));
}
