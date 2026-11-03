using Microsoft.EntityFrameworkCore;
using Payment.API.Data;
using Store.Shared.Events;
using Store.Shared.Events.Persistence;

namespace Payment.API.Consumers;

/// <summary>Saga step 2: charge the order (simulated provider), or decline it when failure is simulated.</summary>
public sealed class PaymentRequestedHandler(PaymentDbContext db, TimeProvider time, ILogger<PaymentRequestedHandler> logger)
    : IIntegrationEventHandler<PaymentRequested>
{
    public async Task HandleAsync(PaymentRequested message, CancellationToken ct)
    {
        var existing = await db.Payments.SingleOrDefaultAsync(p => p.OrderId == message.OrderId, ct);
        if (existing is not null)
        {
            // Already processed under another message: repeat the original outcome, never charge twice.
            logger.LogInformation("Order {OrderId} already has payment {PaymentId} ({Status}); repeating the outcome", message.OrderId, existing.Id, existing.Status);
            IntegrationEvent outcome = existing.Status == PaymentStatus.Failed
                ? new PaymentFailed(message.OrderId, existing.FailureReason ?? "Payment failed.")
                : new PaymentCharged(message.OrderId, existing.Id, existing.Amount, FailAfterCharge: false);
            db.AddToOutbox(outcome);
            return;
        }

        var settings = await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == PaymentSettings.SingletonId, ct);
        var mode = settings?.FailureMode ?? PaymentFailureMode.None;
        var now = time.GetUtcNow().UtcDateTime;

        var payment = new PaymentTransaction
        {
            Id = Guid.NewGuid(),
            OrderId = message.OrderId,
            CustomerId = message.CustomerId,
            Amount = message.Amount,
            Currency = message.Currency,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        string? declineReason = message switch
        {
            { SimulatePaymentFailure: true } => "Card declined (simulated: the order was placed with SimulatePaymentFailure).",
            _ when mode == PaymentFailureMode.Decline => "Card declined (simulated: payment failure mode is Decline).",
            { Amount: <= 0m } => "Invalid payment amount.",
            _ => null,
        };

        if (declineReason is not null)
        {
            payment.Status = PaymentStatus.Failed;
            payment.FailureReason = declineReason;
            db.Payments.Add(payment);
            db.AddToOutbox(new PaymentFailed(message.OrderId, declineReason));
            logger.LogWarning("Payment for order {OrderId} declined: {Reason}", message.OrderId, declineReason);
            return;
        }

        payment.Status = PaymentStatus.Charged;
        payment.ProviderReference = "SIM-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        db.Payments.Add(payment);

        var failAfterCharge = mode == PaymentFailureMode.FailAfterCharge;
        db.AddToOutbox(new PaymentCharged(message.OrderId, payment.Id, payment.Amount, failAfterCharge));
        logger.LogInformation("Charged {Amount} {Currency} for order {OrderId} as payment {PaymentId}{Note}",
            payment.Amount, payment.Currency, message.OrderId, payment.Id, failAfterCharge ? " (FailAfterCharge simulation active)" : string.Empty);
    }
}

/// <summary>Compensation: refund a charged payment. The unique index on Refunds.PaymentId prevents double refunds.</summary>
public sealed class RefundRequestedHandler(PaymentDbContext db, TimeProvider time, ILogger<RefundRequestedHandler> logger)
    : IIntegrationEventHandler<RefundRequested>
{
    public async Task HandleAsync(RefundRequested message, CancellationToken ct)
    {
        var payment = await db.Payments.Include(p => p.Refunds).SingleOrDefaultAsync(p => p.Id == message.PaymentId, ct)
            ?? throw new InvalidOperationException($"Refund requested for unknown payment {message.PaymentId} (order {message.OrderId}).");

        var refund = payment.Refunds.FirstOrDefault();
        if (refund is null)
        {
            if (payment.Status != PaymentStatus.Charged)
            {
                throw new InvalidOperationException($"Payment {payment.Id} is {payment.Status}; only charged payments can be refunded.");
            }

            var now = time.GetUtcNow().UtcDateTime;
            refund = new Refund
            {
                Id = Guid.NewGuid(),
                PaymentId = payment.Id,
                OrderId = payment.OrderId,
                Amount = payment.Amount,
                Reason = message.Reason.Length > 500 ? message.Reason[..500] : message.Reason,
                Status = "Completed",
                CreatedAtUtc = now,
            };
            payment.Refunds.Add(refund);
            payment.Status = PaymentStatus.Refunded;
            payment.UpdatedAtUtc = now;
            logger.LogInformation("Refunded {Amount} {Currency} for order {OrderId} (payment {PaymentId})", refund.Amount, payment.Currency, payment.OrderId, payment.Id);
        }
        else
        {
            logger.LogInformation("Payment {PaymentId} was already refunded ({RefundId}); repeating the outcome", payment.Id, refund.Id);
        }

        db.AddToOutbox(new PaymentRefunded(payment.OrderId, payment.Id, refund.Id, refund.Amount));
    }
}
