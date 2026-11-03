using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Store.Shared.Common.Chaos;

/// <summary>
/// Development-only fault injector used to demonstrate retry and the circuit breaker (Evidence 03).
/// When enabled, a fraction of requests fail with 503 and/or are delayed before reaching the controllers.
/// /chaos and /health are never affected. Not routed by the gateway; call it inside the Docker network:
/// <code>docker compose exec catalog-api curl -s -X PUT localhost:8080/chaos -H "Content-Type: application/json" -d '{"failureRate":0.5,"delayMs":0}'</code>
/// </summary>
public sealed class ChaosState
{
    private readonly Lock gate = new();
    private double failureRate;
    private int delayMs;

    public ChaosSettings Current
    {
        get { lock (gate) { return new ChaosSettings(failureRate, delayMs); } }
    }

    public void Set(ChaosSettings settings)
    {
        lock (gate)
        {
            failureRate = Math.Clamp(settings.FailureRate, 0, 1);
            delayMs = Math.Clamp(settings.DelayMs, 0, 30_000);
        }
    }
}

public sealed record ChaosSettings(double FailureRate, int DelayMs);

public sealed class ChaosMiddleware(RequestDelegate next, ChaosState state, ILogger<ChaosMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/chaos") || path.StartsWithSegments("/health"))
        {
            await next(context);
            return;
        }

        var settings = state.Current;
        if (settings.DelayMs > 0)
        {
            await Task.Delay(settings.DelayMs, context.RequestAborted);
        }

        if (settings.FailureRate > 0 && Random.Shared.NextDouble() < settings.FailureRate)
        {
            logger.LogWarning("Chaos: injected 503 for {Method} {Path} (failure rate {Rate:P0})", context.Request.Method, path.Value, settings.FailureRate);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; // UseStatusCodePages adds the Problem Details body
            return;
        }

        await next(context);
    }
}

public static class ChaosExtensions
{
    public static IServiceCollection AddChaos(this IServiceCollection services) => services.AddSingleton<ChaosState>();

    public static IApplicationBuilder UseChaos(this IApplicationBuilder app) => app.UseMiddleware<ChaosMiddleware>();

    public static IEndpointRouteBuilder MapChaosEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/chaos", (ChaosState state) => Results.Ok(state.Current));
        endpoints.MapPut("/chaos", (ChaosSettings settings, ChaosState state, ILoggerFactory loggers) =>
        {
            state.Set(settings);
            loggers.CreateLogger("Chaos").LogWarning("Chaos settings changed to {@Settings}", state.Current);
            return Results.Ok(state.Current);
        });
        endpoints.MapDelete("/chaos", (ChaosState state) =>
        {
            state.Set(new ChaosSettings(0, 0));
            return Results.Ok(state.Current);
        });
        return endpoints;
    }
}
