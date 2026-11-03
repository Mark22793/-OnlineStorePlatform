using Microsoft.EntityFrameworkCore;

namespace Store.Shared.Events.Persistence;

/// <summary>
/// Idempotency guard: one row per (EventId, ConsumerName) that has been applied. The unique index makes a
/// redelivered message a no-op even if two deliveries race.
/// </summary>
public sealed class ProcessedMessage
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string ConsumerName { get; set; } = string.Empty;
    public DateTime ProcessedAtUtc { get; set; }
}

public static class OutboxStatus
{
    public const string Pending = "Pending";
    public const string Published = "Published";
    public const string Failed = "Failed";
}

/// <summary>
/// Transactional outbox row, written in the same SaveChanges as the business change, published afterwards by
/// <see cref="Outbox.OutboxDispatcher{TContext}"/>.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public string? CorrelationId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
    public string Status { get; set; } = OutboxStatus.Pending;
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Implemented by every DbContext of a service that publishes or consumes events.</summary>
public interface IMessagingDbContext
{
    DbSet<ProcessedMessage> ProcessedMessages { get; }
    DbSet<OutboxMessage> OutboxMessages { get; }
}

public static class MessagingModelBuilderExtensions
{
    /// <summary>Maps the ProcessedMessages and OutboxMessages tables. Call from OnModelCreating.</summary>
    public static ModelBuilder ApplyMessagingConfiguration(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProcessedMessage>(e =>
        {
            e.ToTable("ProcessedMessages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.EventType).HasMaxLength(200).IsUnicode(false).IsRequired();
            e.Property(x => x.ConsumerName).HasMaxLength(200).IsUnicode(false).IsRequired();
            e.HasIndex(x => new { x.EventId, x.ConsumerName }).IsUnique();
            e.HasIndex(x => x.ProcessedAtUtc);
        });

        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.ToTable("OutboxMessages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.EventType).HasMaxLength(200).IsUnicode(false).IsRequired();
            e.Property(x => x.Payload).IsRequired(); // nvarchar(max)
            e.Property(x => x.CorrelationId).HasMaxLength(64).IsUnicode(false);
            e.Property(x => x.Status).HasMaxLength(20).IsUnicode(false).IsRequired();
            e.Property(x => x.LastError).HasMaxLength(2000);
            e.HasIndex(x => x.EventId).IsUnique();
            e.HasIndex(x => new { x.Status, x.CreatedAtUtc });
        });

        return modelBuilder;
    }
}

public static class OutboxExtensions
{
    /// <summary>
    /// Queues <paramref name="message"/> for publishing. It is stored by the caller's next SaveChanges, in the same
    /// transaction as the business change, so state and event can never disagree.
    /// </summary>
    public static void AddToOutbox(this IMessagingDbContext db, IntegrationEvent message) =>
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            EventId = message.EventId,
            EventType = message.EventType,
            Payload = EventSerializer.Serialize(message),
            CorrelationId = message.CorrelationId,
            CreatedAtUtc = message.OccurredAtUtc,
            Status = OutboxStatus.Pending,
        });
}
