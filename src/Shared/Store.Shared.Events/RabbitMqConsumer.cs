using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Store.Shared.Common.Middleware;
using Store.Shared.Events.Persistence;

namespace Store.Shared.Events;

/// <summary>
/// Applies one event to the service's own database. Do not call SaveChanges: the consumer saves the handler's
/// changes, its outbox messages and the ProcessedMessages row together, in one transaction.
/// </summary>
public interface IIntegrationEventHandler<in TEvent> where TEvent : IntegrationEvent
{
    Task HandleAsync(TEvent message, CancellationToken ct);
}

/// <summary>
/// BackgroundService consuming one queue with manual acknowledgements:
/// <list type="number">
/// <item>restores the correlation ID from the message headers into the logging scope;</item>
/// <item>skips the message if (EventId, queue) is already in ProcessedMessages, so duplicates are acked without effect;</item>
/// <item>runs the handler and saves its changes plus the ProcessedMessages row atomically, then acks;</item>
/// <item>retries transient failures (e.g. optimistic-concurrency conflicts) up to <see cref="MaxAttempts"/> times;</item>
/// <item>otherwise nacks with requeue:false, so the broker dead-letters it to <c>&lt;queue&gt;.dlq</c>. Nothing is dropped silently.</item>
/// </list>
/// </summary>
public sealed class RabbitMqConsumer<TEvent, THandler, TContext>(
    string queue,
    RabbitMqConnection connection,
    IServiceScopeFactory scopes,
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqConsumer<TEvent, THandler, TContext>> logger) : BackgroundService
    where TEvent : IntegrationEvent
    where THandler : class, IIntegrationEventHandler<TEvent>
    where TContext : DbContext, IMessagingDbContext
{
    private const int MaxAttempts = 4;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IConnection conn;
        try
        {
            conn = await connection.GetAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var channel = conn.CreateModel();
        channel.BasicQos(prefetchSize: 0, prefetchCount: options.Value.PrefetchCount, global: false);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.Received += (_, delivery) => OnReceivedAsync(channel, delivery, stoppingToken);
        var consumerTag = channel.BasicConsume(queue, autoAck: false, consumer: consumer);
        logger.LogInformation("Consuming {Queue} ({EventType})", queue, typeof(TEvent).Name);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }

        if (channel.IsOpen)
        {
            channel.BasicCancel(consumerTag);
            channel.Close();
        }
    }

    private async Task OnReceivedAsync(IModel channel, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        var body = delivery.Body.ToArray(); // the delivery buffer is only valid during this callback
        using var _ = CorrelationId.BeginScope(logger, ReadCorrelationId(delivery.BasicProperties));

        TEvent? message;
        try
        {
            message = EventSerializer.Deserialize<TEvent>(body);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Malformed {EventType} on {Queue}; dead-lettering", typeof(TEvent).Name, queue);
            channel.BasicNack(delivery.DeliveryTag, multiple: false, requeue: false);
            return;
        }

        if (message is null || message.EventId == Guid.Empty)
        {
            logger.LogError("Empty or id-less {EventType} on {Queue}; dead-lettering", typeof(TEvent).Name, queue);
            channel.BasicNack(delivery.DeliveryTag, multiple: false, requeue: false);
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var applied = await ProcessAsync(message, ct);
                channel.BasicAck(delivery.DeliveryTag, multiple: false);

                if (applied)
                {
                    logger.LogInformation("Handled {EventType} {EventId} from {Queue}", message.EventType, message.EventId, queue);
                }
                else
                {
                    logger.LogInformation("Duplicate {EventType} {EventId} on {Queue} skipped (already processed)", message.EventType, message.EventId, queue);
                }

                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts && !ct.IsCancellationRequested)
            {
                var delay = TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1));
                logger.LogWarning(ex, "Handling {EventType} {EventId} failed (attempt {Attempt}/{Max}); retrying in {Delay}", message.EventType, message.EventId, attempt, MaxAttempts, delay);
                await Task.Delay(delay, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Handling {EventType} {EventId} failed permanently; dead-lettering to {Queue}.dlq", message.EventType, message.EventId, queue);
                if (channel.IsOpen)
                {
                    channel.BasicNack(delivery.DeliveryTag, multiple: false, requeue: false);
                }

                return;
            }
        }
    }

    /// <returns>true when applied, false when it was a duplicate.</returns>
    private async Task<bool> ProcessAsync(TEvent message, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();

        if (await IsProcessedAsync(db, message.EventId, ct))
        {
            return false;
        }

        var handler = scope.ServiceProvider.GetRequiredService<THandler>();
        await handler.HandleAsync(message, ct);

        db.ProcessedMessages.Add(new ProcessedMessage
        {
            Id = Guid.NewGuid(),
            EventId = message.EventId,
            EventType = message.EventType,
            ConsumerName = queue,
            ProcessedAtUtc = DateTime.UtcNow,
        });

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            // A concurrent delivery of the same message may have won the unique (EventId, ConsumerName) index.
            await using var checkScope = scopes.CreateAsyncScope();
            var checkDb = checkScope.ServiceProvider.GetRequiredService<TContext>();
            if (await IsProcessedAsync(checkDb, message.EventId, ct))
            {
                return false;
            }

            throw;
        }
    }

    private Task<bool> IsProcessedAsync(TContext db, Guid eventId, CancellationToken ct) =>
        db.ProcessedMessages.AsNoTracking().AnyAsync(p => p.EventId == eventId && p.ConsumerName == queue, ct);

    private static string? ReadCorrelationId(IBasicProperties? props)
    {
        if (props?.Headers is not null &&
            props.Headers.TryGetValue(MessageHeaders.CorrelationId, out var raw) &&
            raw is byte[] bytes)
        {
            return Encoding.UTF8.GetString(bytes);
        }

        return string.IsNullOrEmpty(props?.CorrelationId) ? null : props.CorrelationId;
    }
}
