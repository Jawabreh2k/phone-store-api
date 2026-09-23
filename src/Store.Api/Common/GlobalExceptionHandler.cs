using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Store.Api.Common;

/// <summary>
/// Last line of defence: logs unexpected exceptions and returns a problem response that never
/// leaks internals. Concurrency conflicts are expected under load and map to 409.
/// </summary>
internal sealed partial class GlobalExceptionHandler(IProblemDetailsService problemDetails, ILogger<GlobalExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, code) = exception switch
        {
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "The data was changed by someone else. Reload and try again.", "concurrency_conflict"),
            BadHttpRequestException bad => (bad.StatusCode, "The request was malformed.", "bad_request"),
            OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested => (499, "Request cancelled.", "cancelled"),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.", "unexpected"),
        };

        if (status >= 500)
        {
            LogUnhandled(logger, exception, httpContext.Request.Method, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Extensions = { ["code"] = code },
            },
        });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception on {Method} {Path}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string method, string path);
}
