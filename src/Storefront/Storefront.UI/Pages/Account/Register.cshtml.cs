using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Storefront.UI.Clients.Auth;
using Storefront.UI.Infrastructure;

namespace Storefront.UI.Pages.Account;

/// <summary>POST /auth/v1/register, then logs the new customer in.</summary>
public sealed class RegisterModel(IAuthClient auth) : StorePageModel
{
    [BindProperty, Required, MaxLength(100)]
    public string DisplayName { get; set; } = string.Empty;

    [BindProperty, Required, EmailAddress, MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    [BindProperty, Required, DataType(DataType.Password), MinLength(8)]
    public string Password { get; set; } = string.Empty;

    [BindProperty, Required, DataType(DataType.Password), Compare(nameof(Password), ErrorMessage = "The passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;

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
            await auth.RegisterAsync(new RegisterRequest { Email = Email, Password = Password, DisplayName = DisplayName }, ct);
            var login = await auth.LoginAsync(new LoginRequest { Email = Email, Password = Password }, ct);
            await AccountSession.SignInAsync(HttpContext, login);
            Message = $"Welcome, {login.User.DisplayName}! Your account is ready.";
            return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
        }
        catch (Exception ex) when (ApiErrors.StatusOf(ex) == StatusCodes.Status409Conflict)
        {
            Error = "An account with this email already exists. Log in instead.";
            return Page();
        }
        catch (Exception ex)
        {
            await HandleApiFailureAsync(ex);
            return Page();
        }
    }
}
