using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Store.Shared.Common.Middleware;

/// <summary>
/// One correlation ID per request or message, end to end:
/// the gateway creates it at the edge, every service reads it in <see cref="CorrelationIdMiddleware"/>,
/// <c>CorrelationIdDelegatingHandler</c> adds it to outgoing HTTP calls, publishers copy
/// <see cref="Current"/> into message headers, and consumers restore it with <see cref="BeginScope"/>.
/// </summary>
public static partial class CorrelationId
{
    public const string HeaderName = "X-Correlation-ID";
    public const string ItemKey = "CorrelationId";
    public const string LogScopeKey = "CorrelationId";

    private static readonly AsyncLocal<string?> CurrentId = new();

    /// <summary>The ID for the request or message being handled on this async flow, if any.</summary>
    public static string? Current
    {
        get => CurrentId.Value;
        set => CurrentId.Value = value;
    }

    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>Accepts caller-supplied IDs only if they are short and safe to log.</summary>
    public static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 64 && SafeId().IsMatch(value);

    /// <summary>
    /// Makes <paramref name="correlationId"/> (or a new ID if it is missing or malformed) current and opens a
    /// logging scope with it. Use in RabbitMQ consumers:
    /// <c>using var _ = CorrelationId.BeginScope(logger, headerValue);</c>
    /// </summary>
    public static IDisposable BeginScope(ILogger logger, string? correlationId)
    {
        var previous = Current;
        var id = IsValid(correlationId) ? correlationId! : NewId();
        Current = id;
        var scope = logger.BeginScope(new Dictionary<string, object> { [LogScopeKey] = id });
        return new RestoreOnDispose(previous, scope);
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeId();

    private sealed class RestoreOnDispose(string? previous, IDisposable? scope) : IDisposable
    {
        public void Dispose()
        {
            scope?.Dispose();
            Current = previous;
        }
    }
}

/// <summary>
/// Reads X-Correlation-ID from the request, or creates one if missing (at the gateway this is the edge).
/// Echoes it on the response, keeps it on the request so Ocelot forwards it, and logs one line per request
/// inside the correlation scope.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[CorrelationId.HeaderName].ToString();
        var id = CorrelationId.IsValid(incoming) ? incoming : CorrelationId.NewId();

        context.Items[CorrelationId.ItemKey] = id;
        context.Request.Headers[CorrelationId.HeaderName] = id;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[CorrelationId.HeaderName] = id;
            return Task.CompletedTask;
        });

        using var scope = CorrelationId.BeginScope(logger, id);
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            if (!context.Request.Path.StartsWithSegments("/health"))
            {
                logger.LogInformation(
                    "HTTP {Method} {Path} -> {StatusCode} in {ElapsedMs:0} ms",
                    context.Request.Method,
                    context.Request.Path.Value,
                    context.Response.StatusCode,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
    }
}

public static class CorrelationIdApplicationBuilderExtensions
{
    /// <summary>Register first in the pipeline so every later log line carries the ID.</summary>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) =>
        app.UseMiddleware<CorrelationIdMiddleware>();
}
