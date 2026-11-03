using System.ComponentModel.DataAnnotations;
using Inventory.API.Data;

namespace Inventory.API.Contracts;

// Mirrors contracts/inventory-v1.yaml.

public sealed record StockLevelResponse(Guid ProductId, int QuantityOnHand, int QuantityReserved, int QuantityAvailable, DateTime UpdatedAtUtc)
{
    public static StockLevelResponse From(InventoryItem i) =>
        new(i.ProductId, i.QuantityOnHand, i.QuantityReserved, i.QuantityAvailable, i.UpdatedAtUtc);
}

public sealed record SetStockRequest([Range(0, 1_000_000)] int QuantityOnHand);

public sealed record ReservationResponse(
    Guid Id,
    Guid OrderId,
    Guid ProductId,
    int Quantity,
    ReservationStatus Status,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc)
{
    public static ReservationResponse From(StockReservation r) =>
        new(r.Id, r.OrderId, r.ProductId, r.Quantity, r.Status, r.CreatedAtUtc, r.UpdatedAtUtc);
}
