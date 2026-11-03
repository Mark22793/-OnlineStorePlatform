using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Store.Shared.Common.Middleware;

namespace ApiGateway.Middleware;

/// <summary>
/// Ocelot and the rate limiter answer their own rejections (401, 403, unmatched route 404, 429, 502/503
/// when a service is down) with an empty body. This fills those in with Problem Details so clients see the same error model as the services.
/// Responses that already have a body (anything a downstream service or the auth controller wrote) pass
/// through untouched.
/// </summary>
public sealed class GatewayProblemDetailsMiddleware(RequestDelegate next)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InvokeAsync(HttpContext context)
    {
        await next(context);

        var response = context.Response;
        if (response.HasStarted || response.StatusCode < 400 || !string.IsNullOrEmpty(response.ContentType))
        {
            return;
        }

        var (title, detail) = response.StatusCode switch
        {
            StatusCodes.Status401Unauthorized => ("Unauthorized", "This route requires a valid bearer token. Log in at POST /auth/v1/login."),
            StatusCodes.Status403Forbidden => ("Forbidden", "Your token is valid, but your role is not allowed to call this route."),
            StatusCodes.Status404NotFound => ("Not found", "No gateway route matches this method and path, or the resource does not exist."),
            StatusCodes.Status429TooManyRequests => ("Too many requests", "Rate limit exceeded. Retry later."),
            StatusCodes.Status502BadGateway => ("Bad gateway", "The downstream service could not be reached."),
            StatusCodes.Status503ServiceUnavailable => ("Service unavailable", "The downstream service timed out or is unavailable."),
            _ => ("Request failed", (string?)null),
        };

        var problem = new ProblemDetails
        {
            Type = $"https://httpstatuses.io/{response.StatusCode}",
            Title = title,
            Status = response.StatusCode,
            Detail = detail,
            Instance = context.Request.Path,
        };
        problem.Extensions["correlationId"] = context.Items[CorrelationId.ItemKey];
        problem.Extensions["source"] = "gateway";

        if (response.StatusCode == StatusCodes.Status401Unauthorized)
        {
            response.Headers.WWWAuthenticate = "Bearer";
        }

        response.ContentLength = null; // Ocelot may have set Content-Length: 0
        response.ContentType = "application/problem+json";
        await response.WriteAsync(JsonSerializer.Serialize(problem, Json));
    }
}
