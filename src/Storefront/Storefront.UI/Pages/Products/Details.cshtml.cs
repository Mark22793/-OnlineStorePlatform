using Microsoft.AspNetCore.Mvc;
using Storefront.UI.Clients.Catalog;
using Storefront.UI.Clients.Inventory;
using Storefront.UI.Infrastructure;

namespace Storefront.UI.Pages.Products;

public sealed class DetailsModel(ICatalogClient catalog, IInventoryClient inventory, CartService cart) : StorePageModel
{
    public Product? Product { get; private set; }
    public StockLevel? Stock { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        try
        {
            Product = await catalog.GetProductAsync(id, ct);
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
            Stock = await inventory.GetStockAsync(id, ct);
        }
        catch (Exception ex) when (ApiErrors.StatusOf(ex) == StatusCodes.Status404NotFound)
        {
            Stock = null; // no stock record yet: shown as out of stock
        }
        catch (Exception ex)
        {
            return await HandleApiFailureAsync(ex) ?? Page();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, int quantity, CancellationToken ct)
    {
        try
        {
            var product = await catalog.GetProductAsync(id, ct);
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

        return RedirectToPage(new { id });
    }
}
