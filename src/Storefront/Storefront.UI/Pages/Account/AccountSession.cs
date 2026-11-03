using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Storefront.UI.Clients.Auth;
using Storefront.UI.Infrastructure;

namespace Storefront.UI.Pages.Account;

/// <summary>Signs the user into the UI cookie with the token returned by POST /auth/v1/login.</summary>
public static class AccountSession
{
    public static Task SignInAsync(HttpContext context, LoginResponse login)
    {
        var role = login.User.Role.ToString();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, login.User.Id.ToString()),
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(login.User.DisplayName) ? login.User.Email : login.User.DisplayName),
            new(ClaimTypes.Email, login.User.Email),
            new(ClaimTypes.Role, role),
            new(StoreClaims.AccessToken, login.AccessToken),
        };

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        return context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties
        {
            IsPersistent = false,
            ExpiresUtc = login.ExpiresAtUtc, // the cookie never outlives the gateway token
        });
    }
}
