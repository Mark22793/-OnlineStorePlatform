using Order.API.Saga;
using Store.Shared.Events;

namespace Order.API.Consumers;

// One handler per saga reply. Each delegates to OrderSaga; RabbitMqConsumer provides manual ack, the
// ProcessedMessages guard, retries on concurrency conflicts, and dead-lettering.

public sealed class StockReservedHandler(OrderSaga saga) : IIntegrationEventHandler<StockReserved>
{
    public Task HandleAsync(StockReserved message, CancellationToken ct) => saga.OnStockReservedAsync(message, ct);
}

public sealed class StockReservationFailedHandler(OrderSaga saga) : IIntegrationEventHandler<StockReservationFailed>
{
    public Task HandleAsync(StockReservationFailed message, CancellationToken ct) => saga.OnStockReservationFailedAsync(message, ct);
}

public sealed class PaymentChargedHandler(OrderSaga saga) : IIntegrationEventHandler<PaymentCharged>
{
    public Task HandleAsync(PaymentCharged message, CancellationToken ct) => saga.OnPaymentChargedAsync(message, ct);
}

public sealed class PaymentFailedHandler(OrderSaga saga) : IIntegrationEventHandler<PaymentFailed>
{
    public Task HandleAsync(PaymentFailed message, CancellationToken ct) => saga.OnPaymentFailedAsync(message, ct);
}

public sealed class StockReleasedHandler(OrderSaga saga) : IIntegrationEventHandler<StockReleased>
{
    public Task HandleAsync(StockReleased message, CancellationToken ct) => saga.OnStockReleasedAsync(message, ct);
}

public sealed class PaymentRefundedHandler(OrderSaga saga) : IIntegrationEventHandler<PaymentRefunded>
{
    public Task HandleAsync(PaymentRefunded message, CancellationToken ct) => saga.OnPaymentRefundedAsync(message, ct);
}
