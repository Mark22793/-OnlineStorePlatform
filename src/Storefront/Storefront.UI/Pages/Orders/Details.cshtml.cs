using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Storefront.UI.Clients.Orders;
using Storefront.UI.Clients.Payments;
using Storefront.UI.Infrastructure;

namespace Storefront.UI.Pages.Orders;

/// <summary>Order tracking: status, saga step, status history, and the payment/refund record.</summary>
[Authorize]
public sealed class DetailsModel(IOrderClient orders, IPaymentClient payments) : StorePageModel
{
    public Order? Order { get; private set; }
    public Payment? Payment { get; private set; }

    /// <summary>Confirmed and Cancelled are final; until then the page refreshes itself.</summary>
    public bool IsFinal => Order is { Status: OrderStatus.Confirmed or OrderStatus.Cancelled };

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        try
        {
            Order = await orders.GetOrderAsync(id, ct);
        }
        catch (Exception ex) when (ApiErrors.StatusOf(ex) == StatusCodes.Status404NotFound)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            return await HandleApiFailureAsync(ex) ?? Page();
        }

        try
        {
            Payment = await payments.GetPaymentForOrderAsync(id, ct);
        }
        catch (Exception ex) when (ApiErrors.StatusOf(ex) == StatusCodes.Status404NotFound)
        {
            Payment = null; // not processed yet, or the order never reached payment
        }
        catch (Exception ex)
        {
            return await HandleApiFailureAsync(ex) ?? Page();
        }

        return Page();
    }
}
