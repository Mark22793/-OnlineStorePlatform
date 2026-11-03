using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Storefront.UI.Clients.Auth;
using Storefront.UI.Infrastructure;

namespace Storefront.UI.Pages.Account;

public sealed class LoginModel(IAuthClient auth, ILogger<LoginModel> logger) : StorePageModel
{
    [BindProperty, Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [BindProperty, Required, DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var login = await auth.LoginAsync(new LoginRequest { Email = Email, Password = Password }, ct);
            await AccountSession.SignInAsync(HttpContext, login);
            logger.LogInformation("User {UserId} logged in to the Storefront", login.User.Id);
            Message = $"Welcome back, {login.User.DisplayName}.";
            return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
        }
        catch (Exception ex) when (ApiErrors.StatusOf(ex) == StatusCodes.Status401Unauthorized)
        {
            Error = ApiErrors.Describe(ex) is { } d && d != "Please log in again." ? d : "Email or password is incorrect.";
            return Page();
        }
        catch (Exception ex)
        {
            await HandleApiFailureAsync(ex);
            return Page();
        }
    }
}
