using System.ComponentModel.DataAnnotations;
using Payment.API.Data;

namespace Payment.API.Contracts;

// Mirrors contracts/payment-v1.yaml.

public sealed record RefundResponse(Guid Id, decimal Amount, string Reason, string Status, DateTime CreatedAtUtc);

public sealed record PaymentResponse(
    Guid Id,
    Guid OrderId,
    Guid CustomerId,
    decimal Amount,
    string Currency,
    PaymentStatus Status,
    string? FailureReason,
    string? ProviderReference,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    IReadOnlyList<RefundResponse> Refunds)
{
    public static PaymentResponse From(PaymentTransaction p) => new(
        p.Id, p.OrderId, p.CustomerId, p.Amount, p.Currency, p.Status, p.FailureReason, p.ProviderReference,
        p.CreatedAtUtc, p.UpdatedAtUtc,
        p.Refunds.OrderBy(r => r.CreatedAtUtc).Select(r => new RefundResponse(r.Id, r.Amount, r.Reason, r.Status, r.CreatedAtUtc)).ToList());
}

public sealed record SimulationResponse(PaymentFailureMode Mode, DateTime UpdatedAtUtc);

public sealed record SetSimulationRequest([Required] PaymentFailureMode? Mode);
