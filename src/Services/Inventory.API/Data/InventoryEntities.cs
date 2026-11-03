namespace Inventory.API.Data;

/// <summary>Stock for one product. Available = OnHand - Reserved; the check constraints keep both non-negative.</summary>
public sealed class InventoryItem
{
    public Guid ProductId { get; set; }
    public int QuantityOnHand { get; set; }
    public int QuantityReserved { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public List<StockReservation> Reservations { get; set; } = [];

    public int QuantityAvailable => QuantityOnHand - QuantityReserved;

    public void Reserve(int quantity, DateTime now)
    {
        if (quantity <= 0 || quantity > QuantityAvailable)
        {
            throw new InvalidOperationException($"Cannot reserve {quantity} of product {ProductId}; {QuantityAvailable} available.");
        }

        QuantityReserved += quantity;
        UpdatedAtUtc = now;
    }

    /// <summary>Compensation: returns reserved units to available stock.</summary>
    public void Release(int quantity, DateTime now)
    {
        QuantityReserved = Math.Max(0, QuantityReserved - quantity);
        UpdatedAtUtc = now;
    }

    /// <summary>Order confirmed: the reserved units leave the warehouse.</summary>
    public void Commit(int quantity, DateTime now)
    {
        QuantityReserved = Math.Max(0, QuantityReserved - quantity);
        QuantityOnHand = Math.Max(0, QuantityOnHand - quantity);
        UpdatedAtUtc = now;
    }
}

public enum ReservationStatus
{
    Reserved,
    Released,
    Committed,
}

/// <summary>
/// One row per (order, product). The unique index stops the same order reserving twice; the status stops
/// a reservation being released or committed twice.
/// </summary>
public sealed class StockReservation
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid ProductId { get; set; }
    public int Quantity { get; set; }
    public ReservationStatus Status { get; set; } = ReservationStatus.Reserved;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? ReleasedAtUtc { get; set; }
    public DateTime? CommittedAtUtc { get; set; }

    public InventoryItem? Item { get; set; }
}
