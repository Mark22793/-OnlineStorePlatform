using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Store.Shared.Common.Middleware;

namespace Store.Shared.Common.Exceptions;

// Domain exceptions. Throw these from services and clients; the handler maps them to Problem Details.

/// <summary>404: the requested resource does not exist.</summary>
public class NotFoundException(string message) : Exception(message);

/// <summary>409: the request conflicts with current state (duplicate, insufficient stock, wrong status...).</summary>
public class ConflictException(string message) : Exception(message);

/// <summary>401: the caller is not identified (e.g. a protected endpoint reached without the gateway's X-User-Id).</summary>
public class UnauthenticatedException(string message) : Exception(message);

/// <summary>400: the request is well-formed but breaks a business rule.</summary>
public class DomainValidationException(string message) : Exception(message);

/// <summary>
/// A downstream service failed after resilience gave up (5xx, timeout, open circuit). Typed clients throw this
/// instead of rethrowing raw HTTP exceptions, so callers deal with one failure type.
/// </summary>
public sealed class DownstreamServiceException(string service, string message, int? downstreamStatusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public string Service { get; } = service;
    public int? DownstreamStatusCode { get; } = downstreamStatusCode;
}

/// <summary>Turns any unhandled exception into an RFC 9457 Problem Details response.</summary>
public sealed class GlobalExceptionHandler(IProblemDetailsService problemDetails, ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, detail) = exception switch
        {
            NotFoundException e => (StatusCodes.Status404NotFound, "Not found", e.Message),
            ConflictException e => (StatusCodes.Status409Conflict, "Conflict", e.Message),
            DomainValidationException e => (StatusCodes.Status400BadRequest, "Invalid request", e.Message),
            UnauthenticatedException e => (StatusCodes.Status401Unauthorized, "Unauthorized", e.Message),
            BadHttpRequestException e => (e.StatusCode, "Bad request", e.Message),
            UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "Forbidden", "You are not allowed to perform this action."),
            DownstreamServiceException e => (StatusCodes.Status503ServiceUnavailable, "Dependency unavailable", $"{e.Service} is unavailable: {e.Message}"),
            BrokenCircuitException => (StatusCodes.Status503ServiceUnavailable, "Dependency unavailable", "A downstream service is failing; its circuit breaker is open."),
            TimeoutRejectedException => (StatusCodes.Status504GatewayTimeout, "Dependency timeout", "A downstream service did not respond in time."),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred", (string?)null),
        };

        if (status >= 500)
        {
            logger.LogError(exception, "Request failed with {StatusCode}", status);
        }
        else
        {
            logger.LogWarning("Request failed with {StatusCode}: {Message}", status, exception.Message);
        }

        context.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = new ProblemDetails { Status = status, Title = title, Detail = detail },
        });
    }
}

public static class ProblemDetailsServiceCollectionExtensions
{
    /// <summary>
    /// Problem Details for every error response, with traceId and correlationId added, plus
    /// <see cref="GlobalExceptionHandler"/>. In the pipeline call <c>app.UseExceptionHandler()</c> and
    /// <c>app.UseStatusCodePages()</c> so bare 401/403/404 responses get a body too.
    /// </summary>
    public static IServiceCollection AddStoreProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails(options => options.CustomizeProblemDetails = ctx =>
        {
            ctx.ProblemDetails.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier;
            if (ctx.HttpContext.Items[CorrelationId.ItemKey] is string correlationId)
            {
                ctx.ProblemDetails.Extensions["correlationId"] = correlationId;
            }
        });
        services.AddExceptionHandler<GlobalExceptionHandler>();
        return services;
    }
}
