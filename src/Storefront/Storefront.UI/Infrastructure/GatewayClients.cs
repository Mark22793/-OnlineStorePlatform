using System.Net.Http.Headers;
using System.Reflection;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Store.Shared.Common.Resilience;
using Storefront.UI.Clients.Auth;
using Storefront.UI.Clients.Catalog;
using Storefront.UI.Clients.Inventory;
using Storefront.UI.Clients.Orders;
using Storefront.UI.Clients.Payments;

namespace Storefront.UI.Infrastructure;

public static class StoreClaims
{
    /// <summary>The gateway JWT, kept inside the encrypted, HttpOnly auth cookie (never exposed to the browser's JS).</summary>
    public const string AccessToken = "access_token";
}

/// <summary>Adds the logged-in user's gateway token to every outgoing API call.</summary>
public sealed class BearerTokenHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = accessor.HttpContext?.User.FindFirst(StoreClaims.AccessToken)?.Value;
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return base.SendAsync(request, cancellationToken);
    }
}

public static class GatewayClientRegistration
{
    /// <summary>
    /// Registers the NSwag-generated clients. Every one points at the API gateway (Gateway__BaseUrl): the
    /// Storefront never calls a service directly.
    /// </summary>
    public static IServiceCollection AddGatewayClients(this IServiceCollection services, IConfiguration configuration)
    {
        var baseUrl = configuration["Gateway:BaseUrl"]
            ?? throw new InvalidOperationException("Gateway:BaseUrl is not set (env var Gateway__BaseUrl).");
        var gateway = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");

        services.AddHttpContextAccessor();
        services.AddTransient<BearerTokenHandler>();

        void Add<TClient, TImplementation>()
            where TClient : class
            where TImplementation : class, TClient =>
            services.AddHttpClient<TClient, TImplementation>(c => c.BaseAddress = gateway)
                .AddHttpMessageHandler<BearerTokenHandler>()
                .AddStoreResilience();

        Add<IAuthClient, AuthClient>();
        Add<ICatalogClient, CatalogClient>();
        Add<IInventoryClient, InventoryClient>();
        Add<IOrderClient, OrderClient>();
        Add<IPaymentClient, PaymentClient>();
        return services;
    }
}

/// <summary>
/// Reads status and Problem Details from the generated ApiException types (each client has its own, in its own
/// namespace, so they are read by shape rather than by type).
/// </summary>
public static class ApiErrors
{
    public static bool IsApiException(Exception ex) => ex.GetType().Name.StartsWith("ApiException", StringComparison.Ordinal);

    public static int? StatusOf(Exception ex) =>
        IsApiException(ex) ? ex.GetType().GetProperty("StatusCode", BindingFlags.Public | BindingFlags.Instance)?.GetValue(ex) as int? : null;

    public static string Describe(Exception ex)
    {
        if (ex is BrokenCircuitException or TimeoutRejectedException or HttpRequestException or TaskCanceledException)
        {
            return "The store is not reachable right now. Please try again in a moment.";
        }

        if (!IsApiException(ex))
        {
            return "Something went wrong. Please try again.";
        }

        var result = ex.GetType().GetProperty("Result")?.GetValue(ex);
        var detail = result?.GetType().GetProperty("Detail")?.GetValue(result) as string;
        var title = result?.GetType().GetProperty("Title")?.GetValue(result) as string;

        if (result?.GetType().GetProperty("Errors")?.GetValue(result) is System.Collections.IDictionary errors && errors.Count > 0)
        {
            var messages = new List<string>();
            foreach (System.Collections.DictionaryEntry entry in errors)
            {
                if (entry.Value is IEnumerable<string> values)
                {
                    messages.AddRange(values);
                }
            }

            if (messages.Count > 0)
            {
                return string.Join(" ", messages);
            }
        }

        return StatusOf(ex) switch
        {
            401 => "Please log in again.",
            403 => "You are not allowed to do that.",
            429 => detail ?? "Too many attempts. Please wait a minute and try again.",
            _ => detail ?? title ?? $"Request failed (HTTP {StatusOf(ex)}).",
        };
    }
}
