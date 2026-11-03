using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Storefront.UI.Infrastructure;

/// <summary>Shared error handling for pages that call the gateway.</summary>
public abstract class StorePageModel : PageModel
{
    [TempData]
    public string? Message { get; set; }

    [TempData]
    public string? Error { get; set; }

    /// <summary>
    /// Turns a failed gateway call into something the user can act on. A 401 means the token expired or is
    /// invalid: sign out and send the user to the login page. Anything else becomes a message on the page.
    /// </summary>
    protected async Task<IActionResult?> HandleApiFailureAsync(Exception ex)
    {
        if (ApiErrors.StatusOf(ex) == StatusCodes.Status401Unauthorized)
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            Error = "Your session has expired. Please log in again.";
            return RedirectToPage("/Account/Login", new { returnUrl = Request.Path + Request.QueryString });
        }

        if (!ApiErrors.IsApiException(ex) && ex is not HttpRequestException and not Polly.CircuitBreaker.BrokenCircuitException and not Polly.Timeout.TimeoutRejectedException and not TaskCanceledException)
        {
            ExceptionDispatchInfo.Throw(ex); // a bug, not a gateway failure: let the error page handle it
        }

        Error = ApiErrors.Describe(ex);
        return null;
    }
}
