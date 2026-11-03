using System.Text.Json;
using System.Text.Json.Serialization;

namespace Store.Shared.Events;

/// <summary>
/// Base for every message on the bus. EventId is the idempotency key (ProcessedMessages), CorrelationId ties the
/// message to the HTTP request that started the flow. The routing key is the record's type name.
/// </summary>
public abstract record IntegrationEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();

    public DateTime OccurredAtUtc { get; init; } = DateTime.UtcNow;

    public string? CorrelationId { get; init; } = global::Store.Shared.Common.Middleware.CorrelationId.Current;

    [JsonIgnore]
    public string EventType => GetType().Name;
}

// ---------------------------------------------------------------------------------------------------------------
// Order saga (orchestrated by Order.API)
//
//   Order     --OrderPlaced-------------> Inventory   reserve stock
//   Inventory --StockReserved / StockReservationFailed --> Order
//   Order     --PaymentRequested--------> Payment     charge (or decline when simulated)
//   Payment   --PaymentCharged / PaymentFailed --> Order
//   Order     --OrderConfirmed----------> Inventory   commit the reservation (stock leaves the warehouse)
//
// Compensation (payment declined, or FailAfterCharge simulation):
//   Order     --StockReleaseRequested---> Inventory   --StockReleased--> Order
//   Order     --RefundRequested---------> Payment     --PaymentRefunded--> Order   (only if already charged)
//   Order becomes Cancelled once every requested compensation has completed.
// ---------------------------------------------------------------------------------------------------------------

public sealed record OrderLine(Guid ProductId, int Quantity);

public sealed record OrderPlaced(Guid OrderId, Guid CustomerId, IReadOnlyList<OrderLine> Lines) : IntegrationEvent;

public sealed record StockReserved(Guid OrderId) : IntegrationEvent;

public sealed record StockReservationFailed(Guid OrderId, string Reason) : IntegrationEvent;

public sealed record PaymentRequested(Guid OrderId, Guid CustomerId, decimal Amount, string Currency, bool SimulatePaymentFailure) : IntegrationEvent;

/// <param name="FailAfterCharge">
/// Set when the payment simulation mode is FailAfterCharge: the charge succeeded, but the order must then fail so
/// the saga demonstrates a refund as well as a stock release.
/// </param>
public sealed record PaymentCharged(Guid OrderId, Guid PaymentId, decimal Amount, bool FailAfterCharge) : IntegrationEvent;

public sealed record PaymentFailed(Guid OrderId, string Reason) : IntegrationEvent;

public sealed record StockReleaseRequested(Guid OrderId, string Reason) : IntegrationEvent;

public sealed record StockReleased(Guid OrderId, int ReleasedUnits) : IntegrationEvent;

public sealed record RefundRequested(Guid OrderId, Guid PaymentId, decimal Amount, string Reason) : IntegrationEvent;

public sealed record PaymentRefunded(Guid OrderId, Guid PaymentId, Guid RefundId, decimal Amount) : IntegrationEvent;

public sealed record OrderConfirmed(Guid OrderId) : IntegrationEvent;

/// <summary>JSON used on the wire and in OutboxMessages.Payload.</summary>
public static class EventSerializer
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(IntegrationEvent message) => JsonSerializer.Serialize(message, message.GetType(), Options);

    public static TEvent? Deserialize<TEvent>(ReadOnlySpan<byte> utf8Json) where TEvent : IntegrationEvent =>
        JsonSerializer.Deserialize<TEvent>(utf8Json, Options);
}
