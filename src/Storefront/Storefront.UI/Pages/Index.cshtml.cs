using Microsoft.AspNetCore.Mvc;
using Storefront.UI.Clients.Catalog;
using Storefront.UI.Clients.Inventory;
using Storefront.UI.Infrastructure;

namespace Storefront.UI.Pages;

/// <summary>Public product list (no login needed): GET /catalog/v1/products and /inventory/v1/stock via the gateway.</summary>
public sealed class IndexModel(ICatalogClient catalog, IInventoryClient inventory, CartService cart) : StorePageModel
{
    public IReadOnlyList<Product> Products { get; private set; } = [];
    public IReadOnlyDictionary<Guid, int> Available { get; private set; } = new Dictionary<Guid, int>();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        try
        {
            Products = (await catalog.ListProductsAsync(false, ct)).ToList();
            var stock = await inventory.ListStockAsync(ct);
            Available = stock.ToDictionary(s => s.ProductId, s => s.QuantityAvailable);
        }
        catch (Exception ex)
        {
            return await HandleApiFailureAsync(ex) ?? Page();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAddAsync(Guid productId, int quantity, CancellationToken ct)
    {
        try
        {
            var product = await catalog.GetProductAsync(productId, ct);
            cart.Add(product.Id, product.Name, product.Price, Math.Max(1, quantity));
            Message = $"Added {Math.Max(1, quantity)} x {product.Name} to your cart.";
        }
        catch (Exception ex)
        {
            var redirect = await HandleApiFailureAsync(ex);
            if (redirect is not null)
            {
                return redirect;
            }
        }

        return RedirectToPage();
    }
}
