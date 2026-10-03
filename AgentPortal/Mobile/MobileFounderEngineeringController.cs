using System.Text.Json;
using AgentPortal.Security;
using AgentPortal.Services.Engineering;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Mobile;

/// <summary>
/// Founder-only mobile projection of the canonical engineering work-item state.
/// Native apps receive plain-language action cards; engineering state and raw
/// diagnostics stay server-owned.
/// </summary>
[ApiController]
[Route("api/v1/mobile/founder/engineering")]
[Authorize(Policy = MobileApiAuthorization.PolicyName)]
[FounderOnly]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None, Duration = 0)]
[IgnoreAntiforgeryToken]
[TypeFilter(typeof(MobileApiExceptionFilter))]
public sealed class MobileFounderEngineeringController : MobileApiControllerBase
{
    private readonly LegendEngineeringStateStore _store;
    private readonly ILegendEngineeringOrchestrator _orchestrator;

    public MobileFounderEngineeringController(
        IMobileActorResolver actorResolver,
        LegendEngineeringStateStore store,
        ILegendEngineeringOrchestrator orchestrator) : base(actorResolver)
    {
        _store = store;
        _orchestrator = orchestrator;
    }

    [HttpGet("actions")]
    public async Task<IActionResult> Actions(CancellationToken cancellationToken)
    {
        if (!FounderGuard.IsFounder(User)) return Forbid();
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null || resolved.Actor is null) return resolved.Error!;

        var items = (await _store.GetOpenWorkItemsAsync(100, cancellationToken))
            .Where(LegendEngineeringFounderPresentation.ShouldSurface)
            .Select(LegendEngineeringFounderPresentation.Present)
            .OrderByDescending(item => item.RequiresFounderAction)
            .ThenByDescending(item => item.UpdatedUtc)
            .Take(25)
            .ToArray();
        return Ok(items);
    }

    [HttpPost("actions/{workItemId:guid}/decision")]
    public async Task<IActionResult> Decide(
        Guid workItemId,
        [FromBody] MobileFounderEngineeringDecisionRequest? request,
        CancellationToken cancellationToken)
    {
        if (!FounderGuard.IsFounder(User)) return Forbid();
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null || resolved.Actor is null) return resolved.Error!;

        object result;
        if (string.Equals(request?.Decision, "approve_release", StringComparison.Ordinal))
            result = await _orchestrator.ApproveReleaseAsync(User, workItemId, cancellationToken);
        else if (string.Equals(request?.Decision, "deny_release", StringComparison.Ordinal))
            result = await _orchestrator.DeclineReleaseAsync(User, workItemId, cancellationToken);
        else
            return Error(
                StatusCodes.Status400BadRequest,
                "founder_engineering_decision_invalid",
                "That engineering action is not available for this work item.");

        var payload = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var ok = payload.TryGetProperty("ok", out var okValue) && okValue.ValueKind == JsonValueKind.True;
        if (ok) return Ok(result);

        var code = payload.TryGetProperty("error", out var errorValue) &&
                   errorValue.ValueKind == JsonValueKind.String
            ? errorValue.GetString() ?? "founder_engineering_decision_rejected"
            : "founder_engineering_decision_rejected";
        return Error(
            StatusCodes.Status409Conflict,
            code,
            code switch
            {
                "work_item_not_found" => "That engineering work item no longer exists.",
                "founder_release_approval_state_invalid" =>
                    "This release is no longer waiting for approval. Refresh to see its current state.",
                "founder_release_denial_state_invalid" =>
                    "This release is no longer waiting for a deny decision. Refresh to see its current state.",
                _ => "The engineering decision was rejected by the canonical release authority."
            });
    }
}

public sealed record MobileFounderEngineeringDecisionRequest(string Decision);
