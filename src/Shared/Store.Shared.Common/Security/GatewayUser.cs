using Microsoft.AspNetCore.Http;

namespace Store.Shared.Common.Security;

/// <summary>
/// Headers the API gateway adds on protected routes, taken from the validated JWT (ocelot.json AddHeadersToRequest).
/// The gateway strips any client-sent copies first and services are not reachable from outside the Docker
/// network, so services can trust them.
/// </summary>
public static class GatewayHeaders
{
    public const string UserId = "X-User-Id";
    public const string UserRole = "X-User-Role";
    public const string UserEmail = "X-User-Email";
}

public static class StoreRoles
{
    public const string Admin = "Admin";
    public const string Customer = "Customer";
}

public sealed record GatewayUser(Guid Id, string Role, string? Email)
{
    public bool IsAdmin => string.Equals(Role, StoreRoles.Admin, StringComparison.Ordinal);
}

public static class GatewayUserExtensions
{
    /// <summary>The caller forwarded by the gateway, or null on public routes / direct calls.</summary>
    public static GatewayUser? GetGatewayUser(this HttpContext context)
    {
        var headers = context.Request.Headers;
        if (!Guid.TryParse(headers[GatewayHeaders.UserId].ToString(), out var id) || id == Guid.Empty)
        {
            return null;
        }

        var role = headers[GatewayHeaders.UserRole].ToString();
        var email = headers[GatewayHeaders.UserEmail].ToString();
        return new GatewayUser(id, string.IsNullOrEmpty(role) ? StoreRoles.Customer : role, string.IsNullOrEmpty(email) ? null : email);
    }
}
