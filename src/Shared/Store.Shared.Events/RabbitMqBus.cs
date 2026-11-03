using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Store.Shared.Events.Outbox;
using Store.Shared.Events.Persistence;

namespace Store.Shared.Events;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string Username { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";
    public ushort PrefetchCount { get; set; } = 10;
    public string ClientName { get; set; } = "store-service";
}

public static class MessageHeaders
{
    public const string CorrelationId = "x-correlation-id";
}

/// <summary>Queue names. One queue per (consuming service, event); each has a matching ".dlq".</summary>
public static class QueueNames
{
    public const string InventoryOrderPlaced = "inventory.order-placed";
    public const string InventoryStockReleaseRequested = "inventory.stock-release-requested";
    public const string InventoryOrderConfirmed = "inventory.order-confirmed";

    public const string PaymentPaymentRequested = "payment.payment-requested";
    public const string PaymentRefundRequested = "payment.refund-requested";

    public const string OrderStockReserved = "order.stock-reserved";
    public const string OrderStockReservationFailed = "order.stock-reservation-failed";
    public const string OrderPaymentCharged = "order.payment-charged";
    public const string OrderPaymentFailed = "order.payment-failed";
    public const string OrderStockReleased = "order.stock-released";
    public const string OrderPaymentRefunded = "order.payment-refunded";
}

/// <summary>
/// The whole broker topology. Every service declares all of it on connect (declarations are idempotent), so no
/// message is ever published before its queue exists:
/// <list type="bullet">
/// <item>topic exchange <c>store.events</c>, routing key = event type name</item>
/// <item>direct dead-letter exchange <c>store.events.dlx</c></item>
/// <item>each queue has x-dead-letter-exchange set, and a <c>&lt;queue&gt;.dlq</c> bound to the DLX</item>
/// </list>
/// </summary>
public static class Topology
{
    public const string Exchange = "store.events";
    public const string DeadLetterExchange = "store.events.dlx";

    public sealed record QueueBinding(string Queue, string EventType);

    public static readonly IReadOnlyList<QueueBinding> Bindings =
    [
        new(QueueNames.InventoryOrderPlaced, nameof(OrderPlaced)),
        new(QueueNames.InventoryStockReleaseRequested, nameof(StockReleaseRequested)),
        new(QueueNames.InventoryOrderConfirmed, nameof(OrderConfirmed)),
        new(QueueNames.PaymentPaymentRequested, nameof(PaymentRequested)),
        new(QueueNames.PaymentRefundRequested, nameof(RefundRequested)),
        new(QueueNames.OrderStockReserved, nameof(StockReserved)),
        new(QueueNames.OrderStockReservationFailed, nameof(StockReservationFailed)),
        new(QueueNames.OrderPaymentCharged, nameof(PaymentCharged)),
        new(QueueNames.OrderPaymentFailed, nameof(PaymentFailed)),
        new(QueueNames.OrderStockReleased, nameof(StockReleased)),
        new(QueueNames.OrderPaymentRefunded, nameof(PaymentRefunded)),
    ];

    public static void Declare(IModel channel)
    {
        channel.ExchangeDeclare(Exchange, ExchangeType.Topic, durable: true, autoDelete: false);
        channel.ExchangeDeclare(DeadLetterExchange, ExchangeType.Direct, durable: true, autoDelete: false);

        foreach (var binding in Bindings)
        {
            var dlq = binding.Queue + ".dlq";
            channel.QueueDeclare(dlq, durable: true, exclusive: false, autoDelete: false, arguments: null);
            channel.QueueBind(dlq, DeadLetterExchange, routingKey: binding.Queue);

            channel.QueueDeclare(binding.Queue, durable: true, exclusive: false, autoDelete: false, arguments: new Dictionary<string, object>
            {
                ["x-dead-letter-exchange"] = DeadLetterExchange,
                ["x-dead-letter-routing-key"] = binding.Queue,
            });
            channel.QueueBind(binding.Queue, Exchange, routingKey: binding.EventType);
        }
    }
}

/// <summary>
/// One AMQP connection per service, opened lazily with retries, with automatic recovery after broker restarts.
/// Declares the topology the first time it connects.
/// </summary>
public sealed class RabbitMqConnection(IOptions<RabbitMqOptions> options, ILogger<RabbitMqConnection> logger) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IConnection? connection;

    public bool IsConnected => connection is { IsOpen: true };

    public async Task<IConnection> GetAsync(CancellationToken ct)
    {
        if (connection is not null)
        {
            return connection; // automatic recovery takes care of temporary drops
        }

        await gate.WaitAsync(ct);
        try
        {
            if (connection is not null)
            {
                return connection;
            }

            var o = options.Value;
            var factory = new ConnectionFactory
            {
                HostName = o.Host,
                Port = o.Port,
                UserName = o.Username,
                Password = o.Password,
                VirtualHost = o.VirtualHost,
                ClientProvidedName = o.ClientName,
                DispatchConsumersAsync = true,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
            };

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var created = factory.CreateConnection(o.ClientName);
                    using (var channel = created.CreateModel())
                    {
                        Topology.Declare(channel);
                    }

                    logger.LogInformation("Connected to RabbitMQ at {Host}:{Port} as {Client}; topology declared", o.Host, o.Port, o.ClientName);
                    connection = created;
                    return created;
                }
                catch (Exception ex) when (attempt < 30 && !ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "RabbitMQ not reachable (attempt {Attempt}); retrying", attempt);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(attempt * 2, 10)), ct);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        try
        {
            connection?.Close(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error closing RabbitMQ connection");
        }

        connection?.Dispose();
        gate.Dispose();
    }
}

public sealed record OutboundMessage(Guid EventId, string EventType, string Payload, string? CorrelationId, DateTime OccurredAtUtc);

public interface IEventPublisher
{
    /// <summary>Publishes and waits for the broker's publisher confirm; throws if the broker nacks or times out.</summary>
    Task PublishAsync(OutboundMessage message, CancellationToken ct);
}

/// <summary>
/// Publisher with confirms on a dedicated channel. Persistent messages; MessageId = EventId; Type = event type;
/// correlation ID in both the AMQP CorrelationId property and the x-correlation-id header.
/// Used by the outbox dispatcher, so a failed publish is retried from the OutboxMessages table.
/// </summary>
public sealed class RabbitMqPublisher(RabbitMqConnection connection, ILogger<RabbitMqPublisher> logger) : IEventPublisher, IDisposable
{
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim gate = new(1, 1);
    private IModel? channel;

    public async Task PublishAsync(OutboundMessage message, CancellationToken ct)
    {
        var conn = await connection.GetAsync(ct);
        await gate.WaitAsync(ct);
        try
        {
            if (channel is null || channel.IsClosed)
            {
                channel?.Dispose();
                channel = conn.CreateModel();
                channel.ConfirmSelect();
            }

            var props = channel.CreateBasicProperties();
            props.Persistent = true;
            props.ContentType = "application/json";
            props.MessageId = message.EventId.ToString();
            props.Type = message.EventType;
            props.Timestamp = new AmqpTimestamp(new DateTimeOffset(DateTime.SpecifyKind(message.OccurredAtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds());
            if (!string.IsNullOrEmpty(message.CorrelationId))
            {
                props.CorrelationId = message.CorrelationId;
                props.Headers = new Dictionary<string, object> { [MessageHeaders.CorrelationId] = message.CorrelationId };
            }

            channel.BasicPublish(Topology.Exchange, message.EventType, mandatory: false, basicProperties: props, body: Encoding.UTF8.GetBytes(message.Payload));
            channel.WaitForConfirmsOrDie(ConfirmTimeout);

            logger.LogInformation("Published {EventType} {EventId} (confirmed)", message.EventType, message.EventId);
        }
        catch
        {
            channel?.Dispose();
            channel = null;
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        channel?.Dispose();
        gate.Dispose();
    }
}

/// <summary>Reports Degraded (not Unhealthy) while disconnected, so the HTTP API stays available.</summary>
public sealed class RabbitMqHealthCheck(RabbitMqConnection connection) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(connection.IsConnected
            ? HealthCheckResult.Healthy("Connected to RabbitMQ")
            : HealthCheckResult.Degraded("Not connected to RabbitMQ yet"));
}

public static class MessagingServiceCollectionExtensions
{
    /// <summary>Connection, confirmed publisher and health check. Reads the RabbitMq section (RabbitMq__Host etc.).</summary>
    public static IServiceCollection AddRabbitMqMessaging(this IServiceCollection services, IConfiguration configuration, string clientName)
    {
        services.Configure<RabbitMqOptions>(configuration.GetSection(RabbitMqOptions.SectionName));
        services.PostConfigure<RabbitMqOptions>(o => o.ClientName = clientName);
        services.AddSingleton<RabbitMqConnection>();
        services.AddSingleton<IEventPublisher, RabbitMqPublisher>();
        services.AddHealthChecks().AddCheck<RabbitMqHealthCheck>("rabbitmq");
        return services;
    }

    /// <summary>Background publisher for the service's OutboxMessages table (transactional outbox).</summary>
    public static IServiceCollection AddOutboxDispatcher<TContext>(this IServiceCollection services)
        where TContext : DbContext, IMessagingDbContext =>
        services.AddHostedService<OutboxDispatcher<TContext>>();

    /// <summary>
    /// A BackgroundService consuming <paramref name="queue"/> with manual acks and the ProcessedMessages guard.
    /// <typeparamref name="THandler"/> is resolved per message in its own DI scope.
    /// </summary>
    public static IServiceCollection AddEventConsumer<TEvent, THandler, TContext>(this IServiceCollection services, string queue)
        where TEvent : IntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
        where TContext : DbContext, IMessagingDbContext
    {
        services.AddScoped<THandler>();
        services.AddHostedService(sp => ActivatorUtilities.CreateInstance<RabbitMqConsumer<TEvent, THandler, TContext>>(sp, queue));
        return services;
    }
}
