using AgentPortal.Services.Tracking;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Controllers.Admin;

[Authorize(Policy = "FounderOnly")]
[Route("admin/tracking")]
public class TrackingMaintenanceController : Controller
{
    private readonly AgentTrackingBackfillService _backfill;

    public TrackingMaintenanceController(AgentTrackingBackfillService backfill)
    {
        _backfill = backfill;
    }

    // POST /admin/tracking/backfill?dryRun=true
    [HttpPost("backfill")]
    public async Task<IActionResult> Backfill([FromQuery] bool dryRun = false, CancellationToken ct = default)
    {
        var result = await _backfill.RunAsync(dryRun, ct);
        return Ok(new
        {
            dryRun,
            created = result.Created,
            skippedExisting = result.SkippedExisting
        });
    }
}
