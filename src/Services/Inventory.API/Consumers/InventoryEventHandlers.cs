using Inventory.API.Data;
using Microsoft.EntityFrameworkCore;
using Store.Shared.Events;
using Store.Shared.Events.Persistence;

namespace Inventory.API.Consumers;

// Handlers run inside RabbitMqConsumer: it checks ProcessedMessages first and saves the handler's changes,
// its outbox events and the ProcessedMessages row in one transaction. Handlers never call SaveChanges.

/// <summary>Saga step 1: reserve every line of the order, or none of them.</summary>
public sealed class OrderPlacedHandler(InventoryDbContext db, TimeProvider time, ILogger<OrderPlacedHandler> logger)
    : IIntegrationEventHandler<OrderPlaced>
{
    public async Task HandleAsync(OrderPlaced message, CancellationToken ct)
    {
        if (await db.StockReservations.AnyAsync(r => r.OrderId == message.OrderId, ct))
        {
            // Already reserved under a different message: answer again rather than reserving twice.
            logger.LogInformation("Order {OrderId} already has reservations; re-sending StockReserved", message.OrderId);
            db.AddToOutbox(new StockReserved(message.OrderId));
            return;
        }

        var lines = message.Lines
            .GroupBy(l => l.ProductId)
            .Select(g => (ProductId: g.Key, Quantity: g.Sum(l => l.Quantity)))
            .ToList();
        var productIds = lines.Select(l => l.ProductId).ToList();
        var items = await db.Inventory.Where(i => productIds.Contains(i.ProductId)).ToDictionaryAsync(i => i.ProductId, ct);

        var problems = new List<string>();
        foreach (var (productId, quantity) in lines)
        {
            if (quantity <= 0)
            {
                problems.Add($"product {productId}: invalid quantity {quantity}");
            }
            else if (!items.TryGetValue(productId, out var item))
            {
                problems.Add($"product {productId}: no stock record");
            }
            else if (item.QuantityAvailable < quantity)
            {
                problems.Add($"product {productId}: requested {quantity}, available {item.QuantityAvailable}");
            }
        }

        if (problems.Count > 0)
        {
            var reason = "Insufficient stock - " + string.Join("; ", problems);
            logger.LogWarning("Cannot reserve stock for order {OrderId}: {Reason}", message.OrderId, reason);
            db.AddToOutbox(new StockReservationFailed(message.OrderId, reason.Length > 450 ? reason[..450] : reason));
            return;
        }

        var now = time.GetUtcNow().UtcDateTime;
        foreach (var (productId, quantity) in lines)
        {
            items[productId].Reserve(quantity, now);
            db.StockReservations.Add(new StockReservation
            {
                Id = Guid.NewGuid(),
                OrderId = message.OrderId,
                ProductId = productId,
                Quantity = quantity,
                Status = ReservationStatus.Reserved,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
        }

        logger.LogInformation("Reserved {Units} unit(s) across {Lines} product(s) for order {OrderId}", lines.Sum(l => l.Quantity), lines.Count, message.OrderId);
        db.AddToOutbox(new StockReserved(message.OrderId));
    }
}

/// <summary>Compensation: put the order's reserved units back. Releasing twice is impossible (status check).</summary>
public sealed class StockReleaseRequestedHandler(InventoryDbContext db, TimeProvider time, ILogger<StockReleaseRequestedHandler> logger)
    : IIntegrationEventHandler<StockReleaseRequested>
{
    public async Task HandleAsync(StockReleaseRequested message, CancellationToken ct)
    {
        var reservations = await db.StockReservations
            .Include(r => r.Item)
            .Where(r => r.OrderId == message.OrderId && r.Status == ReservationStatus.Reserved)
            .ToListAsync(ct);

        var now = time.GetUtcNow().UtcDateTime;
        var units = 0;
        foreach (var reservation in reservations)
        {
            reservation.Item!.Release(reservation.Quantity, now);
            reservation.Status = ReservationStatus.Released;
            reservation.ReleasedAtUtc = now;
            reservation.UpdatedAtUtc = now;
            units += reservation.Quantity;
        }

        logger.LogInformation("Released {Units} reserved unit(s) for order {OrderId} ({Reason})", units, message.OrderId, message.Reason);

        // Always answer, even if nothing was reserved, so the saga can finish.
        db.AddToOutbox(new StockReleased(message.OrderId, units));
    }
}

/// <summary>Order confirmed: reserved units are taken out of stock on hand.</summary>
public sealed class OrderConfirmedHandler(InventoryDbContext db, TimeProvider time, ILogger<OrderConfirmedHandler> logger)
    : IIntegrationEventHandler<OrderConfirmed>
{
    public async Task HandleAsync(OrderConfirmed message, CancellationToken ct)
    {
        var reservations = await db.StockReservations
            .Include(r => r.Item)
            .Where(r => r.OrderId == message.OrderId && r.Status == ReservationStatus.Reserved)
            .ToListAsync(ct);

        var now = time.GetUtcNow().UtcDateTime;
        foreach (var reservation in reservations)
        {
            reservation.Item!.Commit(reservation.Quantity, now);
            reservation.Status = ReservationStatus.Committed;
            reservation.CommittedAtUtc = now;
            reservation.UpdatedAtUtc = now;
        }

        logger.LogInformation("Committed {Count} reservation(s) for confirmed order {OrderId}", reservations.Count, message.OrderId);
    }
}
