using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Storefront.UI.Pages;

[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public sealed class ErrorModel : PageModel
{
    public string? CorrelationId { get; private set; }

    public void OnGet() => CorrelationId = HttpContext.Items["CorrelationId"] as string ?? HttpContext.TraceIdentifier;
}
