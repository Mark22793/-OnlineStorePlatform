using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Storefront.UI.Clients.Orders;
using Storefront.UI.Clients.Payments;
using Storefront.UI.Infrastructure;

namespace Storefront.UI.Pages.Checkout;

/// <summary>Places the order through POST /order/v1/orders. The failure checkbox sets SimulatePaymentFailure.</summary>
[Authorize]
public sealed class IndexModel(CartService cart, IOrderClient orders, IPaymentClient payments) : StorePageModel
{
    public IReadOnlyList<CartLine> Lines { get; private set; } = [];
    public decimal Total { get; private set; }
    public PaymentFailureMode? GlobalMode { get; private set; }

    [BindProperty]
    public bool SimulatePaymentFailure { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (cart.Lines.Count == 0)
        {
            return RedirectToPage("/Cart/Index");
        }

        Lines = cart.Lines;
        Total = cart.Total;

        try
        {
            GlobalMode = (await payments.GetSimulationAsync(ct)).Mode;
        }
        catch (Exception ex)
        {
            var redirect = await HandleApiFailureAsync(ex);
            if (redirect is not null)
            {
                return redirect;
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var lines = cart.Lines;
        if (lines.Count == 0)
        {
            return RedirectToPage("/Cart/Index");
        }

        var request = new PlaceOrderRequest
        {
            Items = lines.Select(l => new OrderLineRequest { ProductId = l.ProductId, Quantity = l.Quantity }).ToList(),
            SimulatePaymentFailure = SimulatePaymentFailure,
        };

        try
        {
            var order = await orders.PlaceOrderAsync(request, ct);
            cart.Clear();
            Message = SimulatePaymentFailure
                ? "Order placed with simulated payment failure. Watch the saga compensate below."
                : "Order placed. Its status updates below as the saga runs.";
            return RedirectToPage("/Orders/Details", new { id = order.Id });
        }
        catch (Exception ex)
        {
            var redirect = await HandleApiFailureAsync(ex);
            if (redirect is not null)
            {
                return redirect;
            }

            return await OnGetAsync(ct);
        }
    }
}
