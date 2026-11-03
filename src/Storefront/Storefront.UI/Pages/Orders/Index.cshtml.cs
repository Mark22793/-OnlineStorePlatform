using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Storefront.UI.Clients.Orders;
using Storefront.UI.Infrastructure;

namespace Storefront.UI.Pages.Orders;

[Authorize]
public sealed class IndexModel(IOrderClient orders) : StorePageModel
{
    public IReadOnlyList<OrderSummary> Orders { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        try
        {
            Orders = (await orders.ListOrdersAsync(ct)).ToList();
        }
        catch (Exception ex)
        {
            return await HandleApiFailureAsync(ex) ?? Page();
        }

        return Page();
    }
}
