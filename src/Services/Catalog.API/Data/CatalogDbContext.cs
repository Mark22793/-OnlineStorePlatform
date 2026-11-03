using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Store.Shared.Common.Persistence;
using Store.Shared.Common.SeedData;

namespace Catalog.API.Data;

/// <summary>CatalogDb. Owned exclusively by Catalog.API (login catalog_svc).</summary>
public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.UseUtcDateTimes();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CatalogDbContext).Assembly);
}

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> product)
    {
        product.ToTable("Products", t => t.HasCheckConstraint("CK_Products_Price_NonNegative", "[Price] >= 0"));
        product.HasKey(p => p.Id);
        product.Property(p => p.Id).ValueGeneratedNever();
        product.Property(p => p.Name).HasMaxLength(200).IsRequired();
        product.Property(p => p.Description).HasMaxLength(2000).IsRequired();
        product.Property(p => p.Price).HasPrecision(18, 2);
        product.Property(p => p.IsActive).HasDefaultValue(true);
        product.Property(p => p.CreatedAtUtc).HasColumnType("datetime2");
        product.Property(p => p.UpdatedAtUtc).HasColumnType("datetime2");
        product.Property(p => p.RowVersion).IsRowVersion();

        product.HasIndex(p => p.Name).IsUnique();
        product.HasIndex(p => p.IsActive);

        // 10 demo products, part of the InitialCatalogCreate migration. Re-running never duplicates them.
        product.HasData(DemoProducts.All.Select(p => new
        {
            p.Id,
            p.Name,
            p.Description,
            p.Price,
            IsActive = true,
            CreatedAtUtc = DemoProducts.SeededAtUtc,
            UpdatedAtUtc = DemoProducts.SeededAtUtc,
        }));
    }
}

/// <summary>
/// Used only by <c>dotnet ef</c> at design time (migrations add/script). It does not connect to a database.
/// Set CATALOG_DB_DESIGN_CONNECTION to point <c>dotnet ef database update</c> at a real server.
/// </summary>
public sealed class CatalogDbContextFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("CATALOG_DB_DESIGN_CONNECTION")
            ?? "Server=localhost,14333;Database=CatalogDb;Integrated Security=false;TrustServerCertificate=True";
        return new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>().UseSqlServer(connection).Options);
    }
}
