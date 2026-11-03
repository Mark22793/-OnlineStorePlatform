using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Storefront.UI.Infrastructure;

namespace Storefront.UI.Pages.Account;

public sealed class LogoutModel : StorePageModel
{
    public IActionResult OnGet() => RedirectToPage("/Index");

    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        Message = "You have been logged out.";
        return RedirectToPage("/Index");
    }
}
