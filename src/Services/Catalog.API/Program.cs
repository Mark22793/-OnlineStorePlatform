using Catalog.API.Data;
using Microsoft.EntityFrameworkCore;
using Store.Shared.Common.Chaos;
using Store.Shared.Common.Exceptions;
using Store.Shared.Common.Middleware;
using Store.Shared.Common.Persistence;
using Store.Shared.Common.Web;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("CatalogDb")
    ?? throw new InvalidOperationException("ConnectionStrings:CatalogDb is not set (env var ConnectionStrings__CatalogDb).");

builder.Services.AddDbContext<CatalogDbContext>(o => o.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddStoreControllers();
builder.Services.AddStoreProblemDetails();
builder.Services.AddChaos();
builder.Services.AddHealthChecks().AddDbContextCheck<CatalogDbContext>("catalog-db");

var app = builder.Build();

app.UseCorrelationId();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment())
{
    app.UseChaos(); // dev-only fault injector: PUT /chaos (see Store.Shared.Common/Chaos)
    app.MapChaosEndpoints();
}

app.UseContractSwaggerUi(("catalog-v1.yaml", "Catalog API v1"));
app.MapControllers();
app.MapHealthChecks("/health");

// Migrations (InitialCatalogCreate) include the 10 seeded products.
await app.InitializeDatabaseAsync<CatalogDbContext>();
await app.RunAsync();
