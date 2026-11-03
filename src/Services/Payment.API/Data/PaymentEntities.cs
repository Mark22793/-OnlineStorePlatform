namespace Payment.API.Data;

public enum PaymentStatus
{
    Charged,
    Failed,
    Refunded,
}

/// <summary>Switch for demonstrating compensation without code changes or restarts.</summary>
public enum PaymentFailureMode
{
    /// <summary>Payments succeed (unless the order itself asked for SimulatePaymentFailure).</summary>
    None,

    /// <summary>Every payment is declined: the saga releases the stock and cancels the order.</summary>
    Decline,

    /// <summary>The charge succeeds, then the order fails: the saga refunds the payment and releases the stock.</summary>
    FailAfterCharge,
}

/// <summary>One payment attempt per order (table Payments). Named to avoid clashing with the Payment.API namespace.</summary>
public sealed class PaymentTransaction
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid CustomerId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "PHP";
    public PaymentStatus Status { get; set; }
    public string? FailureReason { get; set; }
    public string? ProviderReference { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public List<Refund> Refunds { get; set; } = [];
}

/// <summary>Compensation record: a full refund of a charged payment. At most one per payment.</summary>
public sealed class Refund
{
    public Guid Id { get; set; }
    public Guid PaymentId { get; set; }
    public Guid OrderId { get; set; }
    public decimal Amount { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Status { get; set; } = "Completed";
    public DateTime CreatedAtUtc { get; set; }

    public PaymentTransaction? Payment { get; set; }
}

/// <summary>Single-row table (Id = 1) holding the payment failure mode, so the switch survives restarts.</summary>
public sealed class PaymentSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public PaymentFailureMode FailureMode { get; set; } = PaymentFailureMode.None;
    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}
