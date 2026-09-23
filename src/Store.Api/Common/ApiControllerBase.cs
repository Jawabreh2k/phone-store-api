using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Store.Domain.Common;

namespace Store.Api.Common;

/// <summary>
/// Base for all API controllers. Controllers stay thin: bind the request, call one application handler,
/// and turn its <see cref="Result"/> into HTTP with <see cref="FromResult(Result)"/>.
/// Failures become RFC 9457 problem details carrying the stable error <c>code</c> the frontend translates.
/// </summary>
[ApiController]
[Produces("application/json")]
public abstract class ApiControllerBase : ControllerBase
{
    protected IActionResult FromResult(Result result) =>
        result.IsSuccess ? NoContent() : ProblemFor(result.Error);

    protected IActionResult FromResult<T>(Result<T> result) =>
        result.IsSuccess ? Ok(result.Value) : ProblemFor(result.Error);

    protected IActionResult CreatedFromResult<T>(Result<T> result, Func<T, string> location) =>
        result.IsSuccess ? Created(location(result.Value), result.Value) : ProblemFor(result.Error);

    protected IActionResult ProblemFor(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (error is ValidationError validation)
        {
            var modelState = new ModelStateDictionary();
            foreach (var (field, messages) in validation.Errors)
            {
                foreach (var message in messages)
                {
                    modelState.AddModelError(field, message);
                }
            }

            var validationProblem = ProblemDetailsFactory.CreateValidationProblemDetails(
                HttpContext, modelState, StatusCodes.Status400BadRequest, validation.Description);
            validationProblem.Extensions["code"] = validation.Code;
            return Problem(validationProblem);
        }

        var status = error.Type switch
        {
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status422UnprocessableEntity,
        };

        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, status, error.Description);
        problem.Extensions["code"] = error.Code;
        return Problem(problem);
    }

    private static ObjectResult Problem(Microsoft.AspNetCore.Mvc.ProblemDetails problem) => new(problem)
    {
        StatusCode = problem.Status,
        ContentTypes = { "application/problem+json" },
    };
}
