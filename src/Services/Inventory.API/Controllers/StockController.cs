using Asp.Versioning;
using Inventory.API.Contracts;
using Inventory.API.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Store.Shared.Common.Exceptions;

namespace Inventory.API.Controllers;

/// <summary>Implements the stock paths of contracts/inventory-v1.yaml.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("inventory/v{version:apiVersion}/stock")]
[Produces("application/json")]
public sealed class StockController(InventoryDbContext db, TimeProvider time, ILogger<StockController> logger) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<StockLevelResponse>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<StockLevelResponse>> ListStock(CancellationToken ct)
    {
        var items = await db.Inventory.AsNoTracking().OrderBy(i => i.ProductId).ToListAsync(ct);
        return items.Select(StockLevelResponse.From).ToList();
    }

    [HttpGet("{productId:guid}")]
    [ProducesResponseType<StockLevelResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<StockLevelResponse> GetStock(Guid productId, CancellationToken ct)
    {
        var item = await db.Inventory.AsNoTracking().SingleOrDefaultAsync(i => i.ProductId == productId, ct)
            ?? throw new NotFoundException($"No stock record for product {productId}.");
        return StockLevelResponse.From(item);
    }

    /// <summary>Admin: set quantity on hand (creates the record for a new catalog product).</summary>
    [HttpPut("{productId:guid}")]
    [ProducesResponseType<StockLevelResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<StockLevelResponse> SetStock(Guid productId, SetStockRequest request, CancellationToken ct)
    {
        if (productId == Guid.Empty)
        {
            throw new DomainValidationException("productId must not be empty.");
        }

        var now = time.GetUtcNow().UtcDateTime;
        var item = await db.Inventory.SingleOrDefaultAsync(i => i.ProductId == productId, ct);
        if (item is null)
        {
            item = new InventoryItem { ProductId = productId, CreatedAtUtc = now };
            db.Inventory.Add(item);
        }
        else if (request.QuantityOnHand < item.QuantityReserved)
        {
            throw new ConflictException($"{item.QuantityReserved} unit(s) are reserved by open orders; quantity on hand cannot be lower.");
        }

        item.QuantityOnHand = request.QuantityOnHand;
        item.UpdatedAtUtc = now;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("Stock changed while updating (an order was being reserved). Try again.");
        }

        logger.LogInformation("Stock for {ProductId} set to {OnHand} on hand", productId, item.QuantityOnHand);
        return StockLevelResponse.From(item);
    }
}

/// <summary>Admin view of an order's reservations: shows the release after a compensation.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("inventory/v{version:apiVersion}/reservations")]
[Produces("application/json")]
public sealed class ReservationsController(InventoryDbContext db) : ControllerBase
{
    [HttpGet("{orderId:guid}")]
    [ProducesResponseType<IReadOnlyList<ReservationResponse>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<ReservationResponse>> GetReservations(Guid orderId, CancellationToken ct)
    {
        var reservations = await db.StockReservations.AsNoTracking()
            .Where(r => r.OrderId == orderId)
            .OrderBy(r => r.CreatedAtUtc)
            .ToListAsync(ct);
        return reservations.Select(ReservationResponse.From).ToList();
    }
}
