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
    private readonly IFounderEngineeringCommandCenterService _commandCenter;

    public MobileFounderEngineeringController(
        IMobileActorResolver actorResolver,
        IFounderEngineeringCommandCenterService commandCenter) : base(actorResolver)
    {
        _commandCenter = commandCenter;
    }

    [HttpGet("actions")]
    public async Task<IActionResult> Actions(CancellationToken cancellationToken)
    {
        if (!FounderGuard.IsFounder(User)) return Forbid();
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null || resolved.Actor is null) return resolved.Error!;

        return Ok(await _commandCenter.GetActionItemsAsync(cancellationToken));
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

        var decision = request?.Decision ?? string.Empty;
        var result = await _commandCenter.DecideWorkItemAsync(
            User,
            workItemId,
            decision,
            cancellationToken);
        if (result.Succeeded)
            return Ok(new { ok = true });

        var code = result.ErrorCode ?? "founder_engineering_decision_rejected";
        return Error(
            code == "founder_engineering_decision_invalid"
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status409Conflict,
            code,
            code switch
            {
                "founder_engineering_decision_invalid" =>
                    "That engineering action is not available for this work item.",
                "work_item_not_found" =>
                    "That engineering work item no longer exists.",
                "founder_release_approval_state_invalid" =>
                    "This release is no longer waiting for approval. Refresh to see its current state.",
                "founder_release_denial_state_invalid" =>
                    "This release is no longer waiting for a deny decision. Refresh to see its current state.",
                _ => "The engineering decision was rejected by the canonical release authority."
            });
    }
}

public sealed record MobileFounderEngineeringDecisionRequest(string Decision);
