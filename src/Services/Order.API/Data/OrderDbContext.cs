using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Store.Shared.Common.Persistence;
using Store.Shared.Events.Persistence;

namespace Order.API.Data;

/// <summary>
/// OrderDb. Owned exclusively by Order.API (login order_svc). ProductId and PaymentId are references by value
/// to other services' data; there are no cross-database foreign keys.
/// </summary>
public sealed class OrderDbContext(DbContextOptions<OrderDbContext> options) : DbContext(options), IMessagingDbContext
{
    public DbSet<CustomerOrder> Orders => Set<CustomerOrder>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OrderStatusHistory> OrderStatusHistory => Set<OrderStatusHistory>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.UseUtcDateTimes();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OrderDbContext).Assembly);
        modelBuilder.ApplyMessagingConfiguration();
    }
}

public sealed class CustomerOrderConfiguration : IEntityTypeConfiguration<CustomerOrder>
{
    public void Configure(EntityTypeBuilder<CustomerOrder> order)
    {
        order.ToTable("Orders", t => t.HasCheckConstraint("CK_Orders_TotalAmount_NonNegative", "[TotalAmount] >= 0"));
        order.HasKey(o => o.Id);
        order.Property(o => o.Id).ValueGeneratedNever();
        order.Property(o => o.CustomerEmail).HasMaxLength(256);
        order.Property(o => o.Status).HasConversion<string>().HasMaxLength(32).IsUnicode(false);
        order.Property(o => o.SagaStep).HasConversion<string>().HasMaxLength(32).IsUnicode(false);
        order.Property(o => o.TotalAmount).HasPrecision(18, 2);
        order.Property(o => o.Currency).HasMaxLength(3).IsUnicode(false).IsFixedLength().IsRequired();
        order.Property(o => o.CancellationReason).HasMaxLength(500);
        order.Property(o => o.RowVersion).IsRowVersion(); // two saga events for the same order cannot overwrite each other

        order.HasIndex(o => new { o.CustomerId, o.CreatedAtUtc });
        order.HasIndex(o => o.Status);

        order.HasMany(o => o.Items)
            .WithOne()
            .HasForeignKey(i => i.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        order.HasMany(o => o.StatusHistory)
            .WithOne()
            .HasForeignKey(h => h.OrderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> item)
    {
        item.ToTable("OrderItems", t =>
        {
            t.HasCheckConstraint("CK_OrderItems_Quantity_Positive", "[Quantity] > 0");
            t.HasCheckConstraint("CK_OrderItems_UnitPrice_NonNegative", "[UnitPrice] >= 0");
        });
        item.HasKey(i => i.Id);
        item.Property(i => i.Id).ValueGeneratedNever();
        item.Property(i => i.ProductName).HasMaxLength(200).IsRequired();
        item.Property(i => i.UnitPrice).HasPrecision(18, 2);
        item.Property(i => i.LineTotal).HasPrecision(18, 2);
        item.HasIndex(i => new { i.OrderId, i.ProductId }).IsUnique();
    }
}

public sealed class OrderStatusHistoryConfiguration : IEntityTypeConfiguration<OrderStatusHistory>
{
    public void Configure(EntityTypeBuilder<OrderStatusHistory> history)
    {
        history.ToTable("OrderStatusHistory");
        history.HasKey(h => h.Id);
        history.Property(h => h.Id).ValueGeneratedNever();
        history.Property(h => h.Status).HasConversion<string>().HasMaxLength(32).IsUnicode(false);
        history.Property(h => h.Note).HasMaxLength(CustomerOrder.MaxNoteLength).IsRequired();
        history.Property(h => h.CorrelationId).HasMaxLength(64).IsUnicode(false);
        history.HasIndex(h => new { h.OrderId, h.OccurredAtUtc });
    }
}

/// <summary>Design-time factory for <c>dotnet ef</c>.</summary>
public sealed class OrderDbContextFactory : IDesignTimeDbContextFactory<OrderDbContext>
{
    public OrderDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ORDER_DB_DESIGN_CONNECTION")
            ?? "Server=localhost,14333;Database=OrderDb;Integrated Security=false;TrustServerCertificate=True";
        return new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>().UseSqlServer(connection).Options);
    }
}
