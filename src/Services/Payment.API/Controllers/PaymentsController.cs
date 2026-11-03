using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Payment.API.Contracts;
using Payment.API.Data;
using Store.Shared.Common.Exceptions;
using Store.Shared.Common.Security;

namespace Payment.API.Controllers;

/// <summary>Implements the payments path of contracts/payment-v1.yaml.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("payment/v{version:apiVersion}/payments")]
[Produces("application/json")]
public sealed class PaymentsController(PaymentDbContext db) : ControllerBase
{
    /// <summary>The payment for an order, with refunds. Customers see only their own; Admins see all.</summary>
    [HttpGet("{orderId:guid}")]
    [ProducesResponseType<PaymentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<PaymentResponse> GetPaymentForOrder(Guid orderId, CancellationToken ct)
    {
        var payment = await db.Payments.AsNoTracking().Include(p => p.Refunds).SingleOrDefaultAsync(p => p.OrderId == orderId, ct);
        var user = HttpContext.GetGatewayUser();

        if (payment is null || (user is not null && !user.IsAdmin && payment.CustomerId != user.Id))
        {
            throw new NotFoundException($"No payment found for order {orderId}.");
        }

        return PaymentResponse.From(payment);
    }
}

/// <summary>
/// The on-demand payment failure switch (contracts/payment-v1.yaml, /payment/v1/simulation). Stored in
/// PaymentDb, so it applies immediately to every new payment and survives restarts.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("payment/v{version:apiVersion}/simulation")]
[Produces("application/json")]
public sealed class SimulationController(PaymentDbContext db, TimeProvider time, ILogger<SimulationController> logger) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<SimulationResponse>(StatusCodes.Status200OK)]
    public async Task<SimulationResponse> GetSimulation(CancellationToken ct)
    {
        var settings = await LoadAsync(ct);
        return new SimulationResponse(settings.FailureMode, settings.UpdatedAtUtc);
    }

    [HttpPut]
    [ProducesResponseType<SimulationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<SimulationResponse> SetSimulation(SetSimulationRequest request, CancellationToken ct)
    {
        var settings = await LoadAsync(ct);
        settings.FailureMode = request.Mode!.Value;
        settings.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
        settings.UpdatedBy = HttpContext.GetGatewayUser()?.Email;
        await db.SaveChangesAsync(ct);

        logger.LogWarning("Payment failure mode set to {Mode} by {User}", settings.FailureMode, settings.UpdatedBy ?? "unknown");
        return new SimulationResponse(settings.FailureMode, settings.UpdatedAtUtc);
    }

    private async Task<PaymentSettings> LoadAsync(CancellationToken ct)
    {
        var settings = await db.Settings.SingleOrDefaultAsync(s => s.Id == PaymentSettings.SingletonId, ct);
        if (settings is null)
        {
            // Normally seeded by the migration; recreate if someone deleted the row.
            settings = new PaymentSettings { Id = PaymentSettings.SingletonId, FailureMode = PaymentFailureMode.None, UpdatedAtUtc = time.GetUtcNow().UtcDateTime };
            db.Settings.Add(settings);
            await db.SaveChangesAsync(ct);
        }

        return settings;
    }
}
