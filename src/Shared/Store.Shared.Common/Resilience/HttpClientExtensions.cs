using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Store.Shared.Common.Middleware;

namespace Store.Shared.Common.Resilience;

/// <summary>Copies the current correlation ID onto every outgoing HTTP request.</summary>
public sealed class CorrelationIdDelegatingHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var id = CorrelationId.Current;
        if (id is not null && !request.Headers.Contains(CorrelationId.HeaderName))
        {
            request.Headers.TryAddWithoutValidation(CorrelationId.HeaderName, id);
        }

        return base.SendAsync(request, cancellationToken);
    }
}

public static class HttpClientExtensions
{
    /// <summary>
    /// Registers a typed client (for example an NSwag-generated client) through IHttpClientFactory with the
    /// store's resilience pipeline. <paramref name="baseAddressKey"/> is a configuration key such as
    /// "Services:CatalogApi" (env var Services__CatalogApi, set in docker-compose.yml).
    /// <code>builder.Services.AddStoreTypedClient&lt;ICatalogClient, CatalogClient&gt;(builder.Configuration, "Services:CatalogApi");</code>
    /// The generated client needs a constructor that takes only HttpClient (NSwag: UseBaseUrl=false).
    /// </summary>
    public static IHttpClientBuilder AddStoreTypedClient<TClient, TImplementation>(
        this IServiceCollection services,
        IConfiguration configuration,
        string baseAddressKey)
        where TClient : class
        where TImplementation : class, TClient
    {
        var baseAddress = configuration[baseAddressKey]
            ?? throw new InvalidOperationException($"Configuration '{baseAddressKey}' is not set (env var {baseAddressKey.Replace(":", "__")}).");

        return services
            .AddHttpClient<TClient, TImplementation>(client => client.BaseAddress = new Uri(baseAddress))
            .AddStoreResilience();
    }

    /// <summary>
    /// Correlation ID propagation plus the standard resilience handler:
    /// retry (3 attempts, exponential backoff with jitter), a 3 s per-attempt timeout, a 15 s total timeout,
    /// and a circuit breaker that opens for 15 s when at least half of 5+ calls in 30 s fail.
    /// </summary>
    public static IHttpClientBuilder AddStoreResilience(this IHttpClientBuilder builder)
    {
        builder.Services.TryAddTransient<CorrelationIdDelegatingHandler>();
        builder.AddHttpMessageHandler<CorrelationIdDelegatingHandler>();

        builder.AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 3;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.Retry.Delay = TimeSpan.FromMilliseconds(300);
            options.Retry.DisableForUnsafeHttpMethods(); // never replay POST/PUT/DELETE (e.g. placing an order twice)

            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(3);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(15);

            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30); // must be >= 2x attempt timeout
            options.CircuitBreaker.FailureRatio = 0.5;
            options.CircuitBreaker.MinimumThroughput = 5;
            options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(15);
        });

        return builder;
    }
}
