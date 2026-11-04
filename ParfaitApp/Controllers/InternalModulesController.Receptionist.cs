using Infrastructure.Analytics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ParfaitApp.Security;
using Shared.Analytics;

namespace ParfaitApp.Controllers;

public sealed partial class InternalModulesController
{
    [HttpGet("settings/receptionist")]
    [ParfaitInternalPage("AI Receptionist", "Settings", "Set up your receptionist or pause new conversations.", 2, 4)]
    public async Task<IActionResult> Receptionist(CancellationToken ct)
    {
        var business = await _parfaitScope.GetParfaitAsync(ct);
        var snapshot = await _marketingConnections.GetReceptionistConfigurationAsync(
            MarketingOwnerScope.Business(business.Id), MarketingDestinationKeys.OpenAi, ct);
        var configured = HttpContext.RequestServices.GetRequiredService<IConfiguration>()["AgentPortalBaseUrl"];
        string? workspaceUrl = null;
        if (Uri.TryCreate(configured, UriKind.Absolute, out var portal) && portal.Scheme == "https" && string.IsNullOrEmpty(portal.UserInfo))
            workspaceUrl = new Uri(portal, $"/business/{business.Id:D}/receptionist").AbsoluteUri;
        ViewData["ReceptionistWorkspaceUrl"] = workspaceUrl;
        return View("~/Views/InternalModules/Receptionist.cshtml", snapshot);
    }

    [HttpPost("settings/receptionist/pause")]
    [ParfaitInternalPageAccess("/internal/settings/receptionist")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PauseReceptionist(Guid expectedRevision, CancellationToken ct)
    {
        var business = await _parfaitScope.GetParfaitAsync(ct);
        try
        {
            await _marketingConnections.SetReceptionistAdmissionAsync(MarketingOwnerScope.Business(business.Id),
                MarketingDestinationKeys.OpenAi, expectedRevision, false, ct);
            TempData["ReceptionistNotice"] = "New AI conversations are paused. Your saved setup is preserved.";
        }
        catch (DbUpdateConcurrencyException)
        { TempData["ReceptionistNotice"] = "The setup changed. Reload and review its current status before trying again."; }
        catch (InvalidOperationException)
        { TempData["ReceptionistNotice"] = "No saved receptionist connection is available to pause. Open setup to review it."; }
        return RedirectToAction(nameof(Receptionist));
    }
}
