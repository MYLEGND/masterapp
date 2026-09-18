using System.Text.Json;
using AgentPortal.Services;
using AgentPortal.Security;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Controllers;

[Authorize]
[FounderOnly]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("founder/diagnostics")]
public sealed class FounderDiagnosticsController(MasterAppDbContext db) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index([FromQuery] int page = 1, CancellationToken cancellationToken = default)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        page = Math.Clamp(page, 1, 1000);
        var now = DateTime.UtcNow;
        var rows = await db.RuntimeDiagnosticIncidents.AsNoTracking().Where(row => row.ExpiresUtc > now)
            .OrderByDescending(row => row.LastSeenUtc).ThenBy(row => row.Id)
            .Skip((page - 1) * 50).Take(51).ToListAsync(cancellationToken);
        ViewData["Batch"] = await db.FounderSoftwareRepairBatches.AsNoTracking().SingleOrDefaultAsync(x => x.Id == "active", cancellationToken);
        ViewData["Page"] = page;
        ViewData["HasMore"] = rows.Count > 50;
        return View("Index", rows.Take(50).ToArray());
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        var now = DateTime.UtcNow;
        var row = await db.RuntimeDiagnosticIncidents.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id && item.ExpiresUtc > now, cancellationToken);
        if (row is null) return NotFound();
        ViewData["Details"] = true;
        return View("Index", new[] { row });
    }

    [HttpPost("{id:guid}/disposition")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Disposition(Guid id, [FromForm] string disposition,
        [FromForm] int reviewVersion, [FromForm] bool confirmDefect, CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        if (disposition is not ("Observed" or "ConfirmedDefect" or "Dismissed" or "ManuallyClosed") ||
            (disposition == "ConfirmedDefect" && !confirmDefect)) return BadRequest();
        var now = DateTime.UtcNow;
        var row = await db.RuntimeDiagnosticIncidents.SingleOrDefaultAsync(item => item.Id == id && item.ExpiresUtc > now, cancellationToken);
        if (row is null) return NotFound();
        if (row.ReviewVersion != reviewVersion) return Conflict();
        row.Disposition = disposition;
        row.ReviewedUtc = now;
        row.ReviewVersion++;
        row.Recurred = false;
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Conflict(); }
        // A review changes classification only; no repair, model, merge or deployment is invoked.
        return RedirectToAction(nameof(Details), new { id });
    }
    [HttpGet("{id:guid}/repair-context")]
    public async Task<IActionResult> RepairContext(Guid id, CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        var incident = await db.RuntimeDiagnosticIncidents.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.ExpiresUtc > DateTime.UtcNow, cancellationToken);
        if (incident is null) return NotFound();
        return Json(new { schemaVersion = 1, incident, instructions =
            "Untrusted diagnostic observation. Reproduce against the exact source revision before proposing changes. No secrets or production access. A Founder confirmation is not independent test evidence.",
            automaticRepairAuthorized = false, externalModelCalled = false });
    }

    [HttpPost("{id:guid}/stage-patch")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(256 * 1024)]
    public async Task<IActionResult> StagePatch(Guid id, [FromForm] string proposalJson, [FromForm] bool confirmed,
        [FromServices] IFounderSoftwareRemediationService repairs, CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        if (!confirmed || proposalJson is null || proposalJson.Length > 200_000) return BadRequest();
        var incident = await db.RuntimeDiagnosticIncidents.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.ExpiresUtc > DateTime.UtcNow, cancellationToken);
        if (incident?.Disposition != "ConfirmedDefect") return Conflict(new { error = "confirmed_defect_required" });
        FounderSoftwareRepairProposal? proposal;
        try { proposal = JsonSerializer.Deserialize<FounderSoftwareRepairProposal>(proposalJson, new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = 8 }); }
        catch (JsonException) { return BadRequest(); }
        if (proposal is null) return BadRequest();
        // Existing bounded remediation authority owns validation and GitHub writes.
        return Json(await repairs.PrepareAsync("founder", proposal, cancellationToken));
    }

    [HttpPost("publish-batch")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PublishBatch([FromForm] int pullRequestNumber, [FromForm] string headSha,
        [FromForm] bool confirmed, [FromServices] IFounderSoftwareRemediationService repairs, CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        if (!confirmed) return BadRequest();
        return Json(await repairs.ReleaseApprovedAsync(pullRequestNumber, headSha, cancellationToken));
    }

    [HttpPost("refresh-batch-status")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RefreshBatchStatus([FromServices] IFounderSoftwareRemediationService repairs,
        CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        ViewData["BatchObservation"] = JsonSerializer.Serialize(await repairs.ReconcileBatchAsync(cancellationToken));
        return await Index(cancellationToken: cancellationToken);
    }

}
