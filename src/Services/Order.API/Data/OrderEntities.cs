namespace Order.API.Data;

/// <summary>Order statuses required by the capstone, persisted as strings.</summary>
public enum OrderStatus
{
    Pending,
    StockReserved,
    PaymentCharged,
    Confirmed,
    Cancelled,
}

/// <summary>Where the saga is waiting. Lets an operator see a stuck order at a glance.</summary>
public enum SagaStep
{
    AwaitingStock,
    AwaitingPayment,
    Compensating,
    Completed,
}

/// <summary>
/// An order (table Orders) and its saga state. Named CustomerOrder because a type called "Order" inside the
/// Order.API namespace would be shadowed by the namespace itself.
/// </summary>
public sealed class CustomerOrder
{
    public const int MaxNoteLength = 500;

    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public string? CustomerEmail { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Pending;
    public SagaStep SagaStep { get; set; } = SagaStep.AwaitingStock;
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "PHP";

    /// <summary>Chosen at checkout: when true, Payment declines this order so the saga compensates.</summary>
    public bool SimulatePaymentFailure { get; set; }

    public Guid? PaymentId { get; set; }
    public string? CancellationReason { get; set; }

    /// <summary>Compensation bookkeeping: the order becomes Cancelled when both are false.</summary>
    public bool AwaitingStockRelease { get; set; }
    public bool AwaitingRefund { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public List<OrderItem> Items { get; set; } = [];
    public List<OrderStatusHistory> StatusHistory { get; set; } = [];

    /// <summary>Moves to <paramref name="status"/> and records it in OrderStatusHistory.</summary>
    public void ChangeStatus(OrderStatus status, string note, DateTime now, string? correlationId)
    {
        Status = status;
        UpdatedAtUtc = now;
        AddHistory(note, now, correlationId);
    }

    /// <summary>Records a saga event without changing the status (e.g. "Compensation started").</summary>
    public void AddHistory(string note, DateTime now, string? correlationId)
    {
        UpdatedAtUtc = now;
        StatusHistory.Add(new OrderStatusHistory
        {
            Id = Guid.NewGuid(),
            OrderId = Id,
            Status = Status,
            Note = note.Length > MaxNoteLength ? note[..MaxNoteLength] : note,
            OccurredAtUtc = now,
            CorrelationId = correlationId,
        });
    }
}

/// <summary>Order line with the product name and unit price as they were at purchase time.</summary>
public sealed class OrderItem
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
    public decimal LineTotal { get; set; }
}

public sealed class OrderStatusHistory
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public OrderStatus Status { get; set; }
    public string Note { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
    public string? CorrelationId { get; set; }
}
