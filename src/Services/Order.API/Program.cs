using Microsoft.EntityFrameworkCore;
using Order.API.Clients;
using Order.API.Clients.Catalog;
using Order.API.Consumers;
using Order.API.Data;
using Order.API.Saga;
using Store.Shared.Common.Chaos;
using Store.Shared.Common.Exceptions;
using Store.Shared.Common.Middleware;
using Store.Shared.Common.Persistence;
using Store.Shared.Common.Resilience;
using Store.Shared.Common.Web;
using Store.Shared.Events;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("OrderDb")
    ?? throw new InvalidOperationException("ConnectionStrings:OrderDb is not set (env var ConnectionStrings__OrderDb).");

builder.Services.AddDbContext<OrderDbContext>(o => o.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddStoreControllers();
builder.Services.AddStoreProblemDetails();
builder.Services.AddChaos();
builder.Services.AddHealthChecks().AddDbContextCheck<OrderDbContext>("order-db");

// Synchronous integration: NSwag client generated from contracts/catalog-v1.yaml, through IHttpClientFactory with
// retry + exponential backoff + jitter, per-attempt and total timeouts, circuit breaker and correlation ID.
builder.Services.AddStoreTypedClient<ICatalogClient, CatalogClient>(builder.Configuration, "Services:CatalogApi");
builder.Services.AddScoped<ICatalogGateway, CatalogGateway>();

// Saga orchestration over RabbitMQ.
builder.Services.AddScoped<OrderSaga>();
builder.Services.AddRabbitMqMessaging(builder.Configuration, "order-api");
builder.Services.AddOutboxDispatcher<OrderDbContext>();
builder.Services.AddEventConsumer<StockReserved, StockReservedHandler, OrderDbContext>(QueueNames.OrderStockReserved);
builder.Services.AddEventConsumer<StockReservationFailed, StockReservationFailedHandler, OrderDbContext>(QueueNames.OrderStockReservationFailed);
builder.Services.AddEventConsumer<PaymentCharged, PaymentChargedHandler, OrderDbContext>(QueueNames.OrderPaymentCharged);
builder.Services.AddEventConsumer<PaymentFailed, PaymentFailedHandler, OrderDbContext>(QueueNames.OrderPaymentFailed);
builder.Services.AddEventConsumer<StockReleased, StockReleasedHandler, OrderDbContext>(QueueNames.OrderStockReleased);
builder.Services.AddEventConsumer<PaymentRefunded, PaymentRefundedHandler, OrderDbContext>(QueueNames.OrderPaymentRefunded);

var app = builder.Build();

app.UseCorrelationId();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment())
{
    app.UseChaos();
    app.MapChaosEndpoints();
}

app.UseContractSwaggerUi(("order-v1.yaml", "Order API v1"));
app.MapControllers();
app.MapHealthChecks("/health");

await app.InitializeDatabaseAsync<OrderDbContext>();
await app.RunAsync();
