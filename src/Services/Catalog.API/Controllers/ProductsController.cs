using Asp.Versioning;
using Catalog.API.Contracts;
using Catalog.API.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Store.Shared.Common.Exceptions;

namespace Catalog.API.Controllers;

/// <summary>
/// Implements contracts/catalog-v1.yaml. Reads are public; the gateway only forwards POST/PUT/DELETE for Admins.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("catalog/v{version:apiVersion}/products")]
[Produces("application/json")]
public sealed class ProductsController(CatalogDbContext db, TimeProvider time, ILogger<ProductsController> logger) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ProductResponse>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<ProductResponse>> ListProducts([FromQuery] bool includeInactive = false, CancellationToken ct = default)
    {
        var query = db.Products.AsNoTracking();
        if (!includeInactive)
        {
            query = query.Where(p => p.IsActive);
        }

        var products = await query.OrderBy(p => p.Name).ToListAsync(ct);
        return products.Select(ProductResponse.From).ToList();
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<ProductResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ProductResponse> GetProduct(Guid id, CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new NotFoundException($"Product {id} was not found.");
        return ProductResponse.From(product);
    }

    [HttpPost]
    [ProducesResponseType<ProductResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> CreateProduct(ProductRequest request, CancellationToken ct)
    {
        var name = request.Name.Trim();
        await EnsureNameIsFreeAsync(name, exceptId: null, ct);

        var now = time.GetUtcNow().UtcDateTime;
        var product = new Product
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = request.Description?.Trim() ?? string.Empty,
            Price = decimal.Round(request.Price, 2),
            IsActive = request.IsActive,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        db.Products.Add(product);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Created product {ProductId} '{Name}' at {Price}", product.Id, product.Name, product.Price);
        return Created($"/catalog/v1/products/{product.Id}", ProductResponse.From(product));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<ProductResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ProductResponse> UpdateProduct(Guid id, ProductRequest request, CancellationToken ct)
    {
        var product = await db.Products.SingleOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new NotFoundException($"Product {id} was not found.");

        var name = request.Name.Trim();
        await EnsureNameIsFreeAsync(name, exceptId: id, ct);

        product.Name = name;
        product.Description = request.Description?.Trim() ?? string.Empty;
        product.Price = decimal.Round(request.Price, 2);
        product.IsActive = request.IsActive;
        product.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The product was changed by another request. Reload it and try again.");
        }

        logger.LogInformation("Updated product {ProductId}", product.Id);
        return ProductResponse.From(product);
    }

    /// <summary>Soft delete: the product stops being sellable; orders keep their own snapshot of it.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> DeactivateProduct(Guid id, CancellationToken ct)
    {
        var product = await db.Products.SingleOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new NotFoundException($"Product {id} was not found.");

        if (product.IsActive)
        {
            product.IsActive = false;
            product.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Deactivated product {ProductId}", product.Id);
        }

        return NoContent();
    }

    private async Task EnsureNameIsFreeAsync(string name, Guid? exceptId, CancellationToken ct)
    {
        var taken = await db.Products.AnyAsync(p => p.Name == name && (exceptId == null || p.Id != exceptId), ct);
        if (taken)
        {
            throw new ConflictException($"A product named '{name}' already exists.");
        }
    }
}
