using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Store.Shared.Common.Persistence;
using Store.Shared.Common.SeedData;
using Store.Shared.Events.Persistence;

namespace Inventory.API.Data;

/// <summary>
/// InventoryDb. Owned exclusively by Inventory.API (login inventory_svc). ProductId is a reference to Catalog by
/// value only; there is no cross-database foreign key.
/// </summary>
public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options), IMessagingDbContext
{
    public DbSet<InventoryItem> Inventory => Set<InventoryItem>();
    public DbSet<StockReservation> StockReservations => Set<StockReservation>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.UseUtcDateTimes();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(InventoryDbContext).Assembly);
        modelBuilder.ApplyMessagingConfiguration();
    }
}

public sealed class InventoryItemConfiguration : IEntityTypeConfiguration<InventoryItem>
{
    public void Configure(EntityTypeBuilder<InventoryItem> item)
    {
        item.ToTable("Inventory", t =>
        {
            t.HasCheckConstraint("CK_Inventory_OnHand_NonNegative", "[QuantityOnHand] >= 0");
            t.HasCheckConstraint("CK_Inventory_Reserved_NonNegative", "[QuantityReserved] >= 0");
            t.HasCheckConstraint("CK_Inventory_Reserved_LTE_OnHand", "[QuantityReserved] <= [QuantityOnHand]");
        });
        item.HasKey(i => i.ProductId);
        item.Property(i => i.ProductId).ValueGeneratedNever();
        item.Property(i => i.RowVersion).IsRowVersion();
        item.Ignore(i => i.QuantityAvailable);

        item.HasMany(i => i.Reservations)
            .WithOne(r => r.Item)
            .HasForeignKey(r => r.ProductId)
            .OnDelete(DeleteBehavior.Restrict); // never lose reservation history by deleting stock

        // Stock for the 10 demo products (same IDs as CatalogDb), part of InitialInventoryCreate.
        item.HasData(DemoProducts.All.Select(p => new
        {
            ProductId = p.Id,
            QuantityOnHand = p.InitialStock,
            QuantityReserved = 0,
            CreatedAtUtc = DemoProducts.SeededAtUtc,
            UpdatedAtUtc = DemoProducts.SeededAtUtc,
        }));
    }
}

public sealed class StockReservationConfiguration : IEntityTypeConfiguration<StockReservation>
{
    public void Configure(EntityTypeBuilder<StockReservation> reservation)
    {
        reservation.ToTable("StockReservations", t => t.HasCheckConstraint("CK_StockReservations_Quantity_Positive", "[Quantity] > 0"));
        reservation.HasKey(r => r.Id);
        reservation.Property(r => r.Id).ValueGeneratedNever();
        reservation.Property(r => r.Status).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        reservation.HasIndex(r => new { r.OrderId, r.ProductId }).IsUnique();
        reservation.HasIndex(r => new { r.OrderId, r.Status });
    }
}

/// <summary>Design-time factory for <c>dotnet ef</c>. Does not connect when adding migrations.</summary>
public sealed class InventoryDbContextFactory : IDesignTimeDbContextFactory<InventoryDbContext>
{
    public InventoryDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("INVENTORY_DB_DESIGN_CONNECTION")
            ?? "Server=localhost,14333;Database=InventoryDb;Integrated Security=false;TrustServerCertificate=True";
        return new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>().UseSqlServer(connection).Options);
    }
}
