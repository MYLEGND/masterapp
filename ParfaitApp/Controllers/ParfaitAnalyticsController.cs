using Microsoft.AspNetCore.Mvc;
using ParfaitApp.Models;
using ParfaitApp.Services;

namespace ParfaitApp.Controllers;

[Route("parfait-analytics")]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("public-ingest")]
public sealed class ParfaitAnalyticsController(
    ParfaitAnalyticsService analytics,
    CommerceStoreContextService stores) : Controller
{
    [HttpPost("track")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Track([FromBody] ParfaitAnalyticsEventRequest request, CancellationToken ct)
    {
        await analytics.TrackAsync(request, HttpContext, ct);
        return Ok(new { success = true });
    }

    [HttpPost("s/{businessKey}/track")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> TrackScoped(
        string businessKey,
        [FromBody] ParfaitAnalyticsEventRequest request,
        CancellationToken ct)
    {
        var store = await stores.ResolvePublicAsync(businessKey, ct);
        if (store is null || store.IsParfait) return NotFound();

        await analytics.TrackScopedAsync(
            store.CommerceBusinessId,
            store.WebsiteContentVersionId,
            store.WebsiteSiteKey,
            store.BusinessKey,
            request,
            HttpContext,
            ct);

        return Ok(new { success = true });
    }
}
