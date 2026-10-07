using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Tcp.Api.Diagnostics;

/// <summary>Turns unhandled exceptions into RFC 9457 problem responses without leaking details (FR-001-04/05).</summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var correlationId = CorrelationIdMiddleware.Get(httpContext);
        logger.LogError(exception, "Unhandled exception (correlation {CorrelationId})", correlationId);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "Internal server error",
            Type = "https://tools.ietf.org/html/rfc9110#section-15.6.1",
            Extensions = { ["correlationId"] = correlationId },
        }, options: null, contentType: "application/problem+json", cancellationToken);
        return true;
    }
}
