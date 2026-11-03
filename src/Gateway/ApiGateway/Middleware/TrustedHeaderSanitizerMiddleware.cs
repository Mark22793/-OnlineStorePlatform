namespace ApiGateway.Middleware;

/// <summary>
/// Downstream services trust X-User-* headers because only the gateway sets them (from validated JWT claims,
/// via AddHeadersToRequest in ocelot.json). Strip any copies a caller sends so they cannot be spoofed.
/// </summary>
public sealed class TrustedHeaderSanitizerMiddleware(RequestDelegate next)
{
    public static readonly string[] TrustedHeaders = ["X-User-Id", "X-User-Role", "X-User-Email"];

    public Task InvokeAsync(HttpContext context)
    {
        foreach (var header in TrustedHeaders)
        {
            context.Request.Headers.Remove(header);
        }

        return next(context);
    }
}
