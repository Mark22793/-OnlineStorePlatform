using Order.API.Clients.Catalog;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Store.Shared.Common.Exceptions;

namespace Order.API.Clients;

/// <summary>What Order needs from Catalog, in domain terms.</summary>
public sealed record CatalogProduct(Guid Id, string Name, decimal Price, bool IsActive);

public interface ICatalogGateway
{
    /// <summary>The product, or null if Catalog says it does not exist.</summary>
    /// <exception cref="DownstreamServiceException">Catalog failed after retries, timed out, or its circuit is open.</exception>
    Task<CatalogProduct?> FindProductAsync(Guid productId, CancellationToken ct);
}

/// <summary>
/// Wraps the NSwag client generated from contracts/catalog-v1.yaml (registered with the standard resilience
/// handler: retry + backoff + jitter, timeouts, circuit breaker). Translates transport results into domain
/// results: 404 becomes null, anything else becomes <see cref="DownstreamServiceException"/>, never a raw rethrow.
/// </summary>
public sealed class CatalogGateway(ICatalogClient client, ILogger<CatalogGateway> logger) : ICatalogGateway
{
    private const string Service = "Catalog";

    public async Task<CatalogProduct?> FindProductAsync(Guid productId, CancellationToken ct)
    {
        try
        {
            var product = await client.GetProductAsync(productId, ct);
            return new CatalogProduct(product.Id, product.Name, product.Price, product.IsActive);
        }
        catch (ApiException ex) when (ex.StatusCode == StatusCodes.Status404NotFound)
        {
            return null;
        }
        catch (ApiException ex)
        {
            logger.LogWarning("Catalog returned {Status} for product {ProductId}", ex.StatusCode, productId);
            throw new DownstreamServiceException(Service, $"Catalog returned HTTP {ex.StatusCode}.", ex.StatusCode, ex);
        }
        catch (BrokenCircuitException ex)
        {
            logger.LogWarning("Catalog circuit is open; failing fast for product {ProductId}", productId);
            throw new DownstreamServiceException(Service, "circuit breaker is open after repeated failures.", null, ex);
        }
        catch (TimeoutRejectedException ex)
        {
            throw new DownstreamServiceException(Service, "request timed out.", null, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new DownstreamServiceException(Service, ex.Message, null, ex);
        }
    }
}
