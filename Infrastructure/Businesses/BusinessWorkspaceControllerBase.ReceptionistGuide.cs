using Microsoft.AspNetCore.Mvc;
using Shared.Analytics;

namespace Infrastructure.Businesses;

public abstract partial class BusinessWorkspaceControllerBase
{
    [HttpGet("receptionist/guide")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ReceptionistGuide(Guid businessId = default, CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        var owner = MarketingOwnerScope.Business(businessId);
        try
        {
            return Json(await ReceptionistManagement.SetupGuideForOwnerAsync(owner, AuthorizeReceptionistManagementAsync, cancellationToken));
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.Text.Json.JsonException or
            TimeoutException or System.Data.Common.DbException)
        { return StatusCode(503, new { message = "Current setup instructions are unavailable. Reload the guide or ask your platform operator to verify the deployment." }); }
    }
}
