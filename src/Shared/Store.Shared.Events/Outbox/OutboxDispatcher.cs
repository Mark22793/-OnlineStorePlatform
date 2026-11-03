using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Store.Shared.Common.Middleware;
using Store.Shared.Events.Persistence;

namespace Store.Shared.Events.Outbox;

/// <summary>
/// Publishes pending OutboxMessages in creation order, waiting for the publisher confirm before marking each
/// one Published. If the process dies between publish and save, the message is published again; consumers are
/// idempotent (ProcessedMessages), so at-least-once delivery is safe. After <see cref="MaxRetries"/> failed
/// attempts a message is marked Failed and left in the table for inspection.
/// </summary>
public sealed class OutboxDispatcher<TContext>(
    IServiceScopeFactory scopes,
    IEventPublisher publisher,
    ILogger<OutboxDispatcher<TContext>> logger) : BackgroundService
    where TContext : DbContext, IMessagingDbContext
{
    private const int BatchSize = 50;
    private const int MaxRetries = 10;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(500);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Outbox dispatcher started for {Context}", typeof(TContext).Name);

        while (!stoppingToken.IsCancellationRequested)
        {
            var published = 0;
            try
            {
                published = await DispatchBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Outbox dispatch cycle failed; will retry");
            }

            if (published == 0)
            {
                try
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<int> DispatchBatchAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();

        var batch = await db.OutboxMessages
            .Where(m => m.Status == OutboxStatus.Pending)
            .OrderBy(m => m.CreatedAtUtc)
            .Take(BatchSize)
            .ToListAsync(ct);

        var published = 0;
        foreach (var message in batch)
        {
            using var _ = CorrelationId.BeginScope(logger, message.CorrelationId);
            try
            {
                await publisher.PublishAsync(
                    new OutboundMessage(message.EventId, message.EventType, message.Payload, message.CorrelationId, message.CreatedAtUtc),
                    ct);

                message.Status = OutboxStatus.Published;
                message.ProcessedAtUtc = DateTime.UtcNow;
                message.LastError = null;
                await db.SaveChangesAsync(ct);
                published++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                message.RetryCount++;
                message.LastError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                if (message.RetryCount >= MaxRetries)
                {
                    message.Status = OutboxStatus.Failed;
                    logger.LogError(ex, "Giving up on {EventType} {EventId} after {Retries} attempts", message.EventType, message.EventId, message.RetryCount);
                }
                else
                {
                    logger.LogWarning(ex, "Publishing {EventType} {EventId} failed (attempt {Retries}); will retry", message.EventType, message.EventId, message.RetryCount);
                }

                await db.SaveChangesAsync(ct);
                return 0; // broker is probably unavailable: back off before the next cycle, keep ordering
            }
        }

        return published;
    }
}
