using Microsoft.AspNetCore.Mvc;
using Storefront.UI.Infrastructure;

namespace Storefront.UI.Pages.Cart;

public sealed class IndexModel(CartService cart) : StorePageModel
{
    public IReadOnlyList<CartLine> Lines { get; private set; } = [];
    public decimal Total { get; private set; }

    public void OnGet()
    {
        Lines = cart.Lines;
        Total = cart.Total;
    }

    public IActionResult OnPostUpdate(Guid productId, int quantity)
    {
        cart.Update(productId, quantity);
        return RedirectToPage();
    }

    public IActionResult OnPostRemove(Guid productId)
    {
        cart.Remove(productId);
        Message = "Item removed.";
        return RedirectToPage();
    }

    public IActionResult OnPostClear()
    {
        cart.Clear();
        Message = "Cart cleared.";
        return RedirectToPage();
    }
}
