using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Store.Shared.Common.Persistence;
using Store.Shared.Common.SeedData;
using Store.Shared.Events.Persistence;

namespace Payment.API.Data;

/// <summary>PaymentDb. Owned exclusively by Payment.API (login payment_svc). OrderId/CustomerId are references by value.</summary>
public sealed class PaymentDbContext(DbContextOptions<PaymentDbContext> options) : DbContext(options), IMessagingDbContext
{
    public DbSet<PaymentTransaction> Payments => Set<PaymentTransaction>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<PaymentSettings> Settings => Set<PaymentSettings>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.UseUtcDateTimes();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PaymentDbContext).Assembly);
        modelBuilder.ApplyMessagingConfiguration();
    }
}

public sealed class PaymentTransactionConfiguration : IEntityTypeConfiguration<PaymentTransaction>
{
    public void Configure(EntityTypeBuilder<PaymentTransaction> payment)
    {
        payment.ToTable("Payments", t => t.HasCheckConstraint("CK_Payments_Amount_NonNegative", "[Amount] >= 0"));
        payment.HasKey(p => p.Id);
        payment.Property(p => p.Id).ValueGeneratedNever();
        payment.Property(p => p.Amount).HasPrecision(18, 2);
        payment.Property(p => p.Currency).HasMaxLength(3).IsUnicode(false).IsFixedLength().IsRequired();
        payment.Property(p => p.Status).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        payment.Property(p => p.FailureReason).HasMaxLength(500);
        payment.Property(p => p.ProviderReference).HasMaxLength(64).IsUnicode(false);
        payment.Property(p => p.RowVersion).IsRowVersion();

        payment.HasIndex(p => p.OrderId).IsUnique(); // one payment per order: a redelivered request cannot charge twice
        payment.HasIndex(p => p.CustomerId);

        payment.HasMany(p => p.Refunds)
            .WithOne(r => r.Payment)
            .HasForeignKey(r => r.PaymentId)
            .OnDelete(DeleteBehavior.Restrict); // financial records are never cascade-deleted
    }
}

public sealed class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> refund)
    {
        refund.ToTable("Refunds", t => t.HasCheckConstraint("CK_Refunds_Amount_Positive", "[Amount] > 0"));
        refund.HasKey(r => r.Id);
        refund.Property(r => r.Id).ValueGeneratedNever();
        refund.Property(r => r.Amount).HasPrecision(18, 2);
        refund.Property(r => r.Reason).HasMaxLength(500).IsRequired();
        refund.Property(r => r.Status).HasMaxLength(20).IsUnicode(false).IsRequired();
        refund.HasIndex(r => r.PaymentId).IsUnique(); // full refunds only: at most one per payment
        refund.HasIndex(r => r.OrderId);
    }
}

public sealed class PaymentSettingsConfiguration : IEntityTypeConfiguration<PaymentSettings>
{
    public void Configure(EntityTypeBuilder<PaymentSettings> settings)
    {
        settings.ToTable("PaymentSettings", t => t.HasCheckConstraint("CK_PaymentSettings_Singleton", "[Id] = 1"));
        settings.HasKey(s => s.Id);
        settings.Property(s => s.Id).ValueGeneratedNever();
        settings.Property(s => s.FailureMode).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        settings.Property(s => s.UpdatedBy).HasMaxLength(256);
        settings.HasData(new { Id = PaymentSettings.SingletonId, FailureMode = PaymentFailureMode.None, UpdatedAtUtc = DemoProducts.SeededAtUtc });
    }
}

/// <summary>Design-time factory for <c>dotnet ef</c>.</summary>
public sealed class PaymentDbContextFactory : IDesignTimeDbContextFactory<PaymentDbContext>
{
    public PaymentDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("PAYMENT_DB_DESIGN_CONNECTION")
            ?? "Server=localhost,14333;Database=PaymentDb;Integrated Security=false;TrustServerCertificate=True";
        return new PaymentDbContext(new DbContextOptionsBuilder<PaymentDbContext>().UseSqlServer(connection).Options);
    }
}
