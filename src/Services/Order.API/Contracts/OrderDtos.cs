using System.ComponentModel.DataAnnotations;
using Order.API.Data;

namespace Order.API.Contracts;

// Mirrors contracts/order-v1.yaml.

public sealed record OrderLineRequest(Guid ProductId, [Range(1, 100)] int Quantity);

public sealed record PlaceOrderRequest(
    [Required, MinLength(1), MaxLength(20)] IReadOnlyList<OrderLineRequest> Items,
    bool SimulatePaymentFailure = false);

public sealed record OrderItemResponse(Guid ProductId, string ProductName, decimal UnitPrice, int Quantity, decimal LineTotal);

public sealed record StatusHistoryEntryResponse(OrderStatus Status, string Note, DateTime OccurredAtUtc);

public sealed record OrderResponse(
    Guid Id,
    Guid CustomerId,
    OrderStatus Status,
    SagaStep SagaStep,
    decimal TotalAmount,
    string Currency,
    bool SimulatePaymentFailure,
    Guid? PaymentId,
    string? CancellationReason,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    IReadOnlyList<OrderItemResponse> Items,
    IReadOnlyList<StatusHistoryEntryResponse> StatusHistory)
{
    public static OrderResponse From(CustomerOrder o) => new(
        o.Id, o.CustomerId, o.Status, o.SagaStep, o.TotalAmount, o.Currency, o.SimulatePaymentFailure, o.PaymentId,
        o.CancellationReason, o.CreatedAtUtc, o.UpdatedAtUtc,
        o.Items.OrderBy(i => i.ProductName).Select(i => new OrderItemResponse(i.ProductId, i.ProductName, i.UnitPrice, i.Quantity, i.LineTotal)).ToList(),
        o.StatusHistory.OrderBy(h => h.OccurredAtUtc).Select(h => new StatusHistoryEntryResponse(h.Status, h.Note, h.OccurredAtUtc)).ToList());
}

public sealed record OrderSummaryResponse(
    Guid Id,
    OrderStatus Status,
    decimal TotalAmount,
    string Currency,
    int ItemCount,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);
