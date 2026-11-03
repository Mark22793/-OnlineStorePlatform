using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Storefront.UI.Clients.Payments;
using Storefront.UI.Infrastructure;

namespace Storefront.UI.Pages.Simulation;

/// <summary>The on-demand payment failure switch (PUT /payment/v1/simulation), so no code change or restart is needed.</summary>
[Authorize]
public sealed class IndexModel(IPaymentClient payments) : StorePageModel
{
    public PaymentFailureMode CurrentMode { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        try
        {
            var current = await payments.GetSimulationAsync(ct);
            CurrentMode = current.Mode;
            UpdatedAt = current.UpdatedAtUtc;
        }
        catch (Exception ex)
        {
            return await HandleApiFailureAsync(ex) ?? Page();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string mode, CancellationToken ct)
    {
        if (!Enum.TryParse<PaymentFailureMode>(mode, ignoreCase: false, out var parsed))
        {
            Error = $"Unknown mode '{mode}'.";
            return RedirectToPage();
        }

        try
        {
            var updated = await payments.SetSimulationAsync(new SetSimulationRequest { Mode = parsed }, ct);
            Message = $"Payment simulation set to {updated.Mode}.";
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
