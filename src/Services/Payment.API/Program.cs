using Microsoft.EntityFrameworkCore;
using Payment.API.Consumers;
using Payment.API.Data;
using Store.Shared.Common.Chaos;
using Store.Shared.Common.Exceptions;
using Store.Shared.Common.Middleware;
using Store.Shared.Common.Persistence;
using Store.Shared.Common.Web;
using Store.Shared.Events;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("PaymentDb")
    ?? throw new InvalidOperationException("ConnectionStrings:PaymentDb is not set (env var ConnectionStrings__PaymentDb).");

builder.Services.AddDbContext<PaymentDbContext>(o => o.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddStoreControllers();
builder.Services.AddStoreProblemDetails();
builder.Services.AddChaos();
builder.Services.AddHealthChecks().AddDbContextCheck<PaymentDbContext>("payment-db");

builder.Services.AddRabbitMqMessaging(builder.Configuration, "payment-api");
builder.Services.AddOutboxDispatcher<PaymentDbContext>();
builder.Services.AddEventConsumer<PaymentRequested, PaymentRequestedHandler, PaymentDbContext>(QueueNames.PaymentPaymentRequested);
builder.Services.AddEventConsumer<RefundRequested, RefundRequestedHandler, PaymentDbContext>(QueueNames.PaymentRefundRequested);

var app = builder.Build();

app.UseCorrelationId();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment())
{
    app.UseChaos();
    app.MapChaosEndpoints();
}

app.UseContractSwaggerUi(("payment-v1.yaml", "Payment API v1"));
app.MapControllers();
app.MapHealthChecks("/health");

// Migrations (InitialPaymentCreate) include the PaymentSettings row (failure mode None).
await app.InitializeDatabaseAsync<PaymentDbContext>();
await app.RunAsync();
