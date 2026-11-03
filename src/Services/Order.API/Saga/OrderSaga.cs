using Microsoft.EntityFrameworkCore;
using Order.API.Data;
using Store.Shared.Common.Middleware;
using Store.Shared.Events;
using Store.Shared.Events.Persistence;

namespace Order.API.Saga;

/// <summary>
/// Orchestrated saga for one order. Each method is one local transaction: it updates the order, appends to
/// OrderStatusHistory and queues the next command in the outbox. The consumer then saves all of it atomically.
///
/// Happy path:   Pending --StockReserved--> StockReserved --PaymentCharged--> PaymentCharged --> Confirmed
/// Out of stock: Pending --StockReservationFailed--> Cancelled
/// Declined:     StockReserved --PaymentFailed--> (release stock) --StockReleased--> Cancelled
/// Charged, then failed (FailAfterCharge): PaymentCharged --> (release stock + refund)
///               --StockReleased + PaymentRefunded--> Cancelled
///
/// Out-of-order or repeated events are ignored by the status checks; duplicates never reach here (ProcessedMessages).
/// </summary>
public sealed class OrderSaga(OrderDbContext db, TimeProvider time, ILogger<OrderSaga> logger)
{
    public async Task OnStockReservedAsync(StockReserved message, CancellationToken ct)
    {
        var order = await LoadAsync(message.OrderId, ct);
        if (order is null || !Expect(order, OrderStatus.Pending, nameof(StockReserved)))
        {
            return;
        }

        order.ChangeStatus(OrderStatus.StockReserved, "Inventory reserved stock for every item. Requesting payment.", Now, CorrelationId.Current);
        order.SagaStep = SagaStep.AwaitingPayment;
        db.AddToOutbox(new PaymentRequested(order.Id, order.CustomerId, order.TotalAmount, order.Currency, order.SimulatePaymentFailure));
    }

    public async Task OnStockReservationFailedAsync(StockReservationFailed message, CancellationToken ct)
    {
        var order = await LoadAsync(message.OrderId, ct);
        if (order is null || !Expect(order, OrderStatus.Pending, nameof(StockReservationFailed)))
        {
            return;
        }

        // Nothing was reserved or charged, so there is nothing to compensate.
        order.CancellationReason = Truncate(message.Reason);
        order.SagaStep = SagaStep.Completed;
        order.ChangeStatus(OrderStatus.Cancelled, $"Order cancelled: {message.Reason}", Now, CorrelationId.Current);
    }

    public async Task OnPaymentChargedAsync(PaymentCharged message, CancellationToken ct)
    {
        var order = await LoadAsync(message.OrderId, ct);
        if (order is null || !Expect(order, OrderStatus.StockReserved, nameof(PaymentCharged)))
        {
            return;
        }

        order.PaymentId = message.PaymentId;
        order.ChangeStatus(OrderStatus.PaymentCharged, $"Payment {message.PaymentId} charged {message.Amount:0.00} {order.Currency}.", Now, CorrelationId.Current);

        if (message.FailAfterCharge)
        {
            BeginCompensation(order, "Order confirmation failed after the payment was charged (simulated FailAfterCharge).", refund: true);
            return;
        }

        order.SagaStep = SagaStep.Completed;
        order.ChangeStatus(OrderStatus.Confirmed, "Order confirmed.", Now, CorrelationId.Current);
        db.AddToOutbox(new OrderConfirmed(order.Id));
    }

    public async Task OnPaymentFailedAsync(PaymentFailed message, CancellationToken ct)
    {
        var order = await LoadAsync(message.OrderId, ct);
        if (order is null || !Expect(order, OrderStatus.StockReserved, nameof(PaymentFailed)))
        {
            return;
        }

        BeginCompensation(order, $"Payment failed: {message.Reason}", refund: false);
    }

    public async Task OnStockReleasedAsync(StockReleased message, CancellationToken ct)
    {
        var order = await LoadAsync(message.OrderId, ct);
        if (order is null || !order.AwaitingStockRelease)
        {
            logger.LogInformation("Ignoring StockReleased for order {OrderId}: no release pending", message.OrderId);
            return;
        }

        order.AwaitingStockRelease = false;
        order.AddHistory($"Compensation: inventory released {message.ReleasedUnits} reserved unit(s).", Now, CorrelationId.Current);
        CompleteCompensationIfDone(order);
    }

    public async Task OnPaymentRefundedAsync(PaymentRefunded message, CancellationToken ct)
    {
        var order = await LoadAsync(message.OrderId, ct);
        if (order is null || !order.AwaitingRefund)
        {
            logger.LogInformation("Ignoring PaymentRefunded for order {OrderId}: no refund pending", message.OrderId);
            return;
        }

        order.AwaitingRefund = false;
        order.AddHistory($"Compensation: payment refunded {message.Amount:0.00} {order.Currency} (refund {message.RefundId}).", Now, CorrelationId.Current);
        CompleteCompensationIfDone(order);
    }

    private void BeginCompensation(CustomerOrder order, string reason, bool refund)
    {
        var needsRefund = refund && order.PaymentId is not null;

        order.CancellationReason = Truncate(reason);
        order.SagaStep = SagaStep.Compensating;
        order.AwaitingStockRelease = true;
        order.AwaitingRefund = needsRefund;
        order.AddHistory(
            $"Compensation started: {reason} Releasing reserved stock{(needsRefund ? " and refunding the payment" : " (no refund needed, nothing was charged)")}.",
            Now,
            CorrelationId.Current);

        db.AddToOutbox(new StockReleaseRequested(order.Id, Truncate(reason)));
        if (needsRefund)
        {
            db.AddToOutbox(new RefundRequested(order.Id, order.PaymentId!.Value, order.TotalAmount, Truncate(reason)));
        }

        logger.LogWarning("Order {OrderId}: compensation started ({Reason}); refund={Refund}", order.Id, reason, needsRefund);
    }

    private void CompleteCompensationIfDone(CustomerOrder order)
    {
        if (order.SagaStep != SagaStep.Compensating || order.AwaitingStockRelease || order.AwaitingRefund)
        {
            return;
        }

        order.SagaStep = SagaStep.Completed;
        order.ChangeStatus(OrderStatus.Cancelled, $"Order cancelled after compensation. {order.CancellationReason}", Now, CorrelationId.Current);
        logger.LogInformation("Order {OrderId} cancelled; compensation complete", order.Id);
    }

    private async Task<CustomerOrder?> LoadAsync(Guid orderId, CancellationToken ct)
    {
        var order = await db.Orders.SingleOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null)
        {
            logger.LogWarning("Saga event for unknown order {OrderId} ignored", orderId);
        }

        return order;
    }

    private bool Expect(CustomerOrder order, OrderStatus expected, string eventName)
    {
        if (order.Status == expected)
        {
            return true;
        }

        logger.LogWarning("Ignoring {Event} for order {OrderId}: status is {Status}, expected {Expected}", eventName, order.Id, order.Status, expected);
        return false;
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private static string Truncate(string value) => value.Length > 450 ? value[..450] : value;
}
