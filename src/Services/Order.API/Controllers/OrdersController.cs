using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Order.API.Clients;
using Order.API.Contracts;
using Order.API.Data;
using Store.Shared.Common.Exceptions;
using Store.Shared.Common.Middleware;
using Store.Shared.Common.Security;
using Store.Shared.Events;
using Store.Shared.Events.Persistence;

namespace Order.API.Controllers;

/// <summary>Implements contracts/order-v1.yaml. Every route is protected at the gateway, which forwards the caller as X-User-*.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("order/v{version:apiVersion}/orders")]
[Produces("application/json")]
public sealed class OrdersController(OrderDbContext db, ICatalogGateway catalog, TimeProvider time, ILogger<OrdersController> logger) : ControllerBase
{
    private const string Currency = "PHP";

    /// <summary>
    /// Creates the order as Pending and starts the saga. The order, its first history entry and the OrderPlaced
    /// event are saved in one transaction (outbox), so the saga can never miss an order.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<OrderResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<IActionResult> PlaceOrder(PlaceOrderRequest request, CancellationToken ct)
    {
        var user = RequireUser();

        var lines = request.Items
            .GroupBy(i => i.ProductId)
            .Select(g => (ProductId: g.Key, Quantity: g.Sum(i => i.Quantity)))
            .ToList();

        if (lines.Any(l => l.ProductId == Guid.Empty))
        {
            throw new DomainValidationException("Every item needs a productId.");
        }

        if (lines.Any(l => l.Quantity > 100))
        {
            throw new DomainValidationException("At most 100 units of one product per order.");
        }

        // Synchronous integration: the current name and price come from Catalog through the resilient typed client.
        var now = time.GetUtcNow().UtcDateTime;
        var order = new CustomerOrder
        {
            Id = Guid.NewGuid(),
            CustomerId = user.Id,
            CustomerEmail = user.Email,
            Status = OrderStatus.Pending,
            SagaStep = SagaStep.AwaitingStock,
            Currency = Currency,
            SimulatePaymentFailure = request.SimulatePaymentFailure,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        foreach (var (productId, quantity) in lines)
        {
            var product = await catalog.FindProductAsync(productId, ct)
                ?? throw new DomainValidationException($"Product {productId} does not exist.");
            if (!product.IsActive)
            {
                throw new ConflictException($"'{product.Name}' is no longer for sale.");
            }

            order.Items.Add(new OrderItem
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                ProductId = product.Id,
                ProductName = product.Name,
                UnitPrice = product.Price,
                Quantity = quantity,
                LineTotal = decimal.Round(product.Price * quantity, 2),
            });
        }

        order.TotalAmount = order.Items.Sum(i => i.LineTotal);
        order.ChangeStatus(
            OrderStatus.Pending,
            request.SimulatePaymentFailure
                ? "Order placed with SimulatePaymentFailure. Waiting for stock reservation."
                : "Order placed. Waiting for stock reservation.",
            now,
            CorrelationId.Current);

        db.Orders.Add(order);
        db.AddToOutbox(new OrderPlaced(order.Id, order.CustomerId, order.Items.Select(i => new OrderLine(i.ProductId, i.Quantity)).ToList()));
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Order {OrderId} placed by {CustomerId}: {Items} item(s), {Total} {Currency}", order.Id, user.Id, order.Items.Count, order.TotalAmount, order.Currency);
        return Accepted($"/order/v1/orders/{order.Id}", OrderResponse.From(order));
    }

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<OrderSummaryResponse>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<OrderSummaryResponse>> ListOrders(CancellationToken ct)
    {
        var user = RequireUser();
        var query = db.Orders.AsNoTracking();
        if (!user.IsAdmin)
        {
            query = query.Where(o => o.CustomerId == user.Id);
        }

        return await query
            .OrderByDescending(o => o.CreatedAtUtc)
            .Take(100)
            .Select(o => new OrderSummaryResponse(o.Id, o.Status, o.TotalAmount, o.Currency, o.Items.Count, o.CreatedAtUtc, o.UpdatedAtUtc))
            .ToListAsync(ct);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<OrderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<OrderResponse> GetOrder(Guid id, CancellationToken ct)
    {
        var user = RequireUser();
        var order = await db.Orders.AsNoTracking()
            .Include(o => o.Items)
            .Include(o => o.StatusHistory)
            .AsSplitQuery()
            .SingleOrDefaultAsync(o => o.Id == id, ct);

        // Another customer's order is reported as not found, so order IDs cannot be probed.
        if (order is null || (!user.IsAdmin && order.CustomerId != user.Id))
        {
            throw new NotFoundException($"Order {id} was not found.");
        }

        return OrderResponse.From(order);
    }

    private GatewayUser RequireUser() =>
        HttpContext.GetGatewayUser() ?? throw new UnauthenticatedException("This endpoint must be called through the gateway with a valid token.");
}
