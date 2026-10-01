using System.Text.Json;
using AgentPortal.Services;
using AgentPortal.Services.Engineering;
using AgentPortal.Security;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared.Diagnostics;

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
        ViewData["CompletedBatches"] = await db.FounderSoftwareRepairBatches.AsNoTracking()
            .Where(row => row.Id != "active" && row.State == "WebDeploymentVerified")
            .OrderByDescending(row => row.CompletionVerifiedUtc).Take(20).ToArrayAsync(cancellationToken);
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

    [HttpPost("archive-deployed-batch")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ArchiveDeployedBatch([FromForm] int pullRequestNumber, [FromForm] string headSha,
        [FromForm] string expectedRevision, [FromForm] bool confirmed,
        [FromServices] IFounderSoftwareRemediationService repairs, CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        if (!confirmed) return BadRequest();
        ViewData["BatchCompletion"] = JsonSerializer.Serialize(
            await repairs.ArchiveDeployedBatchAsync(pullRequestNumber, headSha, expectedRevision, cancellationToken));
        return await Index(cancellationToken: cancellationToken);
    }

    [HttpGet("candidate-validation/review")]
    public async Task<IActionResult> ReviewCandidateValidation(int pullRequestNumber, string headSha,
        string baseSha, string patchSha256, string trustedWorkflowSha,
        [FromServices] IFounderSoftwareRemediationService repairs, CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        var review = await repairs.GetCandidateValidationReviewAsync(pullRequestNumber, headSha, baseSha,
            patchSha256, trustedWorkflowSha, cancellationToken);
        return View("CandidateValidation", JsonSerializer.SerializeToElement(review));
    }

    [HttpPost("candidate-validation/request")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestCandidateValidation(int pullRequestNumber, string headSha,
        string baseSha, string patchSha256, string trustedWorkflowSha, string approvalActionDigest,
        [FromServices] IFounderSoftwareRemediationService repairs, CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        return Json(await repairs.RequestCandidateValidationAsync("founder", pullRequestNumber, headSha,
            baseSha, patchSha256, trustedWorkflowSha, approvalActionDigest, cancellationToken));
    }

    [HttpGet("candidate-validation/{runId:long}")]
    public async Task<IActionResult> CandidateValidation(long runId, string headSha,
        [FromServices] IFounderSoftwareRemediationService repairs, CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        return Json(await repairs.GetCandidateValidationAsync(runId, headSha, cancellationToken));
    }

    [HttpGet("~/api/legend-site-tools/catalog")]
    public IActionResult SiteToolCatalog(
        [FromServices] FounderLegendConnectService legend,
        [FromServices] IFounderSoftwareRemediationService remediation,
        [FromServices] AgencyCommandService agencyCommand,
        [FromServices] IServiceScopeFactory scopes)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        var authority = new LegendFounderToolAuthority(legend, remediation, agencyCommand, authorizationScopes: scopes);
        var tools = new List<object>
        {
            LegendSiteToolDisclosureAuthority.CurrentPageTool,
            LegendSiteToolDisclosureAuthority.VerifyCurrentPageRepairTool
        };
        tools.AddRange(authority
            .GetAvailableSiteReadTools()
            .Where(tool =>
            {
                var element = JsonSerializer.SerializeToElement(tool);
                return element.TryGetProperty("name", out var name) &&
                    name.GetString() is { } value &&
                    authority.IsReadOnly(value);
            }));

        return Json(new
        {
            schemaVersion = 1,
            authority = nameof(LegendFounderToolAuthority),
            disclosureAuthority = nameof(LegendSiteToolDisclosureAuthority),
            authentication = "server_session_founder",
            mutationToolsExposed = false,
            tools
        });
    }

    [HttpPost("~/api/legend-site-tools/execute")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(64 * 1024)]
    public async Task<IActionResult> ExecuteSiteTool(
        [FromBody] LegendSiteToolExecutionRequest request,
        [FromServices] FounderLegendConnectService legend,
        [FromServices] IFounderSoftwareRemediationService remediation,
        [FromServices] AgencyCommandService agencyCommand,
        [FromServices] IServiceScopeFactory scopes,
        [FromServices] IHostEnvironment environment,
        [FromServices] IEnumerable<EndpointDataSource> endpointSources,
        CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        if (request is null || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 96)
            return BadRequest(new { error = "legend_site_tool_request_invalid" });

        if (string.Equals(request.Name, LegendSiteToolDisclosureAuthority.CurrentPageToolName, StringComparison.Ordinal))
        {
            var route = LegendSiteToolDisclosureAuthority.ResolveRouteAuthority(request.Page?.Path, endpointSources);
            return Json(LegendSiteToolDisclosureAuthority.SanitizePage(
                request.Page,
                environment.ApplicationName,
                "founder_system",
                LegendSiteToolDisclosureAuthority.EntryAssemblyRevision(),
                route));
        }

        if (string.Equals(request.Name, LegendSiteToolDisclosureAuthority.VerifyCurrentPageRepairToolName, StringComparison.Ordinal))
        {
            if (request.Arguments.ValueKind != JsonValueKind.Object)
                return BadRequest(new { error = "live_repair_proof_arguments_invalid" });
            var root = request.Arguments;
            static string? ReadString(JsonElement value, string name) =>
                value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            static bool TryReadArray(JsonElement value, string name, int maximumItems, out string[] result)
            {
                result = Array.Empty<string>();
                if (!value.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.Array ||
                    item.GetArrayLength() > maximumItems)
                    return false;

                var values = new List<string>();
                foreach (var entry in item.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.String || entry.GetString() is not { } text ||
                        string.IsNullOrWhiteSpace(text))
                        return false;
                    values.Add(text);
                }
                result = values.ToArray();
                return true;
            }

            if (!Guid.TryParse(ReadString(root, "engineering_work_item_id"), out var engineeringWorkItemId) ||
                !TryReadArray(root, "required_component_ids", 24, out var requiredComponents) ||
                !TryReadArray(root, "required_action_keys", 24, out var requiredActions) ||
                !TryReadArray(root, "required_composition_ids", 24, out var requiredCompositions) ||
                !TryReadArray(root, "required_modal_ids", 16, out var requiredModals) ||
                !TryReadArray(root, "forbidden_error_names", 12, out var forbiddenErrors))
                return BadRequest(new { error = "live_repair_proof_arguments_invalid" });

            var route = LegendSiteToolDisclosureAuthority.ResolveRouteAuthority(request.Page?.Path, endpointSources);
            var expectedRevision = ReadString(root, "expected_revision");
            var expectedRoute = ReadString(root, "expected_route");
            var proof = LegendSiteToolDisclosureAuthority.VerifyCurrentPageRepair(
                request.Page,
                environment.ApplicationName,
                "founder_system",
                LegendSiteToolDisclosureAuthority.EntryAssemblyRevision(),
                route,
                expectedRevision,
                expectedRoute,
                requiredComponents,
                requiredActions,
                requiredCompositions,
                requiredModals,
                forbiddenErrors);
            var proofJson = JsonSerializer.SerializeToElement(proof);
            var verified = proofJson.TryGetProperty("repairVerified", out var repairVerified) &&
                           repairVerified.ValueKind == JsonValueKind.True;
            if (verified && expectedRevision is not null && expectedRoute is not null)
            {
                await using var engineeringScope = scopes.CreateAsyncScope();
                var orchestrator = engineeringScope.ServiceProvider.GetRequiredService<ILegendEngineeringOrchestrator>();
                try
                {
                    await orchestrator.RecordBrowserFunctionalProofAsync(
                        engineeringWorkItemId,
                        expectedRevision,
                        expectedRoute,
                        requiredComponents,
                        requiredActions,
                        requiredCompositions,
                        requiredModals,
                        forbiddenErrors,
                        cancellationToken);
                }
                catch (InvalidOperationException exception)
                {
                    return Conflict(new { error = exception.Message });
                }
            }
            return Json(proof);
        }

        var authority = new LegendFounderToolAuthority(legend, remediation, agencyCommand, authorizationScopes: scopes);
        var allowed = authority
            .GetAvailableSiteReadTools()
            .Any(tool =>
            {
                var element = JsonSerializer.SerializeToElement(tool);
                return element.TryGetProperty("name", out var name) &&
                    string.Equals(name.GetString(), request.Name, StringComparison.Ordinal) &&
                    authority.IsReadOnly(request.Name);
            });
        if (!allowed)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "legend_site_tool_not_exposed" });

        var arguments = request.Arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? "{}"
            : request.Arguments.GetRawText();
        if (System.Text.Encoding.UTF8.GetByteCount(arguments) > 32 * 1024)
            return BadRequest(new { error = "legend_site_tool_arguments_too_large" });

        var output = await authority.ExecuteAsync(
            User,
            new FounderAiToolCall(Guid.NewGuid().ToString("N"), request.Name, arguments),
            "legend",
            cancellationToken,
            LegendConnectExternalProviderPolicy.CloudflareFoundation);

        try
        {
            using var document = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 64 });
            return Json(document.RootElement.Clone());
        }
        catch (JsonException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "legend_site_tool_output_invalid" });
        }
    }

}

public sealed record LegendSiteToolExecutionRequest(
    string? Name,
    JsonElement Arguments,
    LegendSitePageSnapshot? Page);
