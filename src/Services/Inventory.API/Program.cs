using Inventory.API.Consumers;
using Inventory.API.Data;
using Microsoft.EntityFrameworkCore;
using Store.Shared.Common.Chaos;
using Store.Shared.Common.Exceptions;
using Store.Shared.Common.Middleware;
using Store.Shared.Common.Persistence;
using Store.Shared.Common.Web;
using Store.Shared.Events;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("InventoryDb")
    ?? throw new InvalidOperationException("ConnectionStrings:InventoryDb is not set (env var ConnectionStrings__InventoryDb).");

builder.Services.AddDbContext<InventoryDbContext>(o => o.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddStoreControllers();
builder.Services.AddStoreProblemDetails();
builder.Services.AddChaos();
builder.Services.AddHealthChecks().AddDbContextCheck<InventoryDbContext>("inventory-db");

// RabbitMQ: confirmed publishing through the outbox, manual-ack consumers with ProcessedMessages.
builder.Services.AddRabbitMqMessaging(builder.Configuration, "inventory-api");
builder.Services.AddOutboxDispatcher<InventoryDbContext>();
builder.Services.AddEventConsumer<OrderPlaced, OrderPlacedHandler, InventoryDbContext>(QueueNames.InventoryOrderPlaced);
builder.Services.AddEventConsumer<StockReleaseRequested, StockReleaseRequestedHandler, InventoryDbContext>(QueueNames.InventoryStockReleaseRequested);
builder.Services.AddEventConsumer<OrderConfirmed, OrderConfirmedHandler, InventoryDbContext>(QueueNames.InventoryOrderConfirmed);

var app = builder.Build();

app.UseCorrelationId();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment())
{
    app.UseChaos();
    app.MapChaosEndpoints();
}

app.UseContractSwaggerUi(("inventory-v1.yaml", "Inventory API v1"));
app.MapControllers();
app.MapHealthChecks("/health");

// Migrations (InitialInventoryCreate) include stock for the 10 seeded catalog products.
await app.InitializeDatabaseAsync<InventoryDbContext>();
await app.RunAsync();
