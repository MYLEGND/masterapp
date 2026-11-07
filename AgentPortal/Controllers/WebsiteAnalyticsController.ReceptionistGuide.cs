using Microsoft.AspNetCore.Mvc;
using Shared.Analytics;

namespace AgentPortal.Controllers;

public partial class WebsiteAnalyticsController
{
    [HttpGet("receptionist/guide")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ReceptionistGuide(Guid? agentProfileId = null, CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(agentProfileId, cancellationToken);
        if (owner is null) return Forbid();
        try
        {
            // This scoped route is always the shared business projection. The Founder route
            // independently checks canonical Founder authority before returning platform content.
            return Json(await ReceptionistManagement.SetupGuideForOwnerAsync(owner, AuthorizeReceptionistManagementAsync, cancellationToken));
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.Text.Json.JsonException or
            TimeoutException or System.Data.Common.DbException)
        { return StatusCode(503, new { message = "Current setup instructions are unavailable. Reload the guide or ask your platform operator to verify the deployment." }); }
    }

    private string? ReceptionistGuideUrl(MarketingOwnerScope owner, Guid? agentProfileId) =>
        owner == MarketingOwnerScope.Founder
            ? Url.Action(nameof(FounderEngineeringController.ReceptionistGuide), "FounderEngineering")
            : Url.Action(nameof(ReceptionistGuide), new { agentProfileId });
}
