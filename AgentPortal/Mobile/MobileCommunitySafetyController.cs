using AgentPortal.Security;
using Domain.Entities;
using Domain.Messaging;
using Domain.Moderation;
using Domain.Social;
using Infrastructure.Mobile;
using Infrastructure.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Mobile;

/// <summary>
/// Typed community controls shared by client and agent mobile identities.
/// The API never trusts a client-supplied profile identifier as authority.
/// </summary>
[ApiController]
[Route("api/v1/mobile/community-safety")]
[Authorize(Policy = MobileApiAuthorization.PolicyName)]
[IgnoreAntiforgeryToken]
[TypeFilter(typeof(MobileApiExceptionFilter))]
public sealed class MobileCommunitySafetyController : MobileApiControllerBase
{
    private readonly ICommunitySafetyService _communitySafety;
    private readonly IControlledResourceAccessService _controlledResources;
    private readonly ISocialFeedService _social;

    public MobileCommunitySafetyController(
        IMobileActorResolver actorResolver,
        ICommunitySafetyService communitySafety,
        IControlledResourceAccessService controlledResources,
        ISocialFeedService social)
        : base(actorResolver)
    {
        _communitySafety = communitySafety;
        _controlledResources = controlledResources;
        _social = social;
    }

    [HttpPost("blocks")]
    public async Task<IActionResult> Block(
        [FromBody] MobileCommunityBlockRequest? request,
        CancellationToken cancellationToken)
    {
        var actor = await ResolveActorAsync(cancellationToken);
        if (actor.Error is not null || actor.Actor is null)
            return actor.Error!;
        if (request is null)
            return Error(StatusCodes.Status400BadRequest, "community_block_target_required", "Choose a Legend profile to block.");

        var result = await _communitySafety.BlockAsync(
            new CommunitySafetyBlockCommand(
                actor.Actor.Actor,
                new MessagingActor(request.TargetUserId ?? string.Empty, request.TargetParticipantType ?? string.Empty)),
            cancellationToken);
        return result.Succeeded
            ? NoContent()
            : Error(StatusCodes.Status400BadRequest, result.ErrorCode!, result.ErrorMessage!);
    }

    [HttpPost("reports")]
    public async Task<IActionResult> Report(
        [FromBody] MobileCommunityReportRequest? request,
        CancellationToken cancellationToken)
    {
        var actor = await ResolveActorAsync(cancellationToken);
        if (actor.Error is not null || actor.Actor is null)
            return actor.Error!;
        if (request is null)
            return Error(StatusCodes.Status400BadRequest, "community_report_required", "Choose content or a Legend profile to report.");

        var result = await _communitySafety.ReportAsync(
            new CommunitySafetyReportCommand(
                actor.Actor.Actor,
                new MessagingActor(request.TargetUserId ?? string.Empty, request.TargetParticipantType ?? string.Empty),
                request.TargetKind ?? string.Empty,
                request.TargetEntityId,
                request.Category ?? string.Empty,
                request.Detail),
            cancellationToken);
        return result.Succeeded
            ? NoContent()
            : Error(StatusCodes.Status400BadRequest, result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>
    /// The review queue is available only to the canonical Founder and members
    /// who hold the explicit CommunityManagement grant. The client never sends
    /// a role claim that could elevate itself.
    /// </summary>
    [HttpGet("reports")]
    public async Task<IActionResult> OpenReports(
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveCommunityReviewerAsync(cancellationToken);
        if (resolved.Error is not null || resolved.Actor is null)
            return resolved.Error!;

        return Ok(await _communitySafety.GetOpenReportsAsync(take, cancellationToken));
    }

    /// <summary>
    /// Community managers may triage reports. Applying a content-removal action
    /// remains Founder-only because it changes another member's public content.
    /// </summary>
    [HttpPost("reports/{reportId:guid}/resolution")]
    public async Task<IActionResult> ResolveReport(
        Guid reportId,
        [FromBody] MobileCommunityReportResolutionRequest? request,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveCommunityReviewerAsync(cancellationToken);
        if (resolved.Error is not null || resolved.Actor is null)
            return resolved.Error!;

        var report = await _communitySafety.GetOpenReportAsync(reportId, cancellationToken);
        if (report is null)
            return Error(StatusCodes.Status404NotFound, "community_report_not_found", "This community report was not found.");

        var resolution = CommunitySafetyReviewResolutions.Normalize(request?.Resolution);
        if (resolution is null)
            return Error(StatusCodes.Status400BadRequest, "community_report_resolution_invalid", "Choose a valid report decision.");

        var isFounder = FounderGuard.IsFounder(User);
        if (resolution == CommunitySafetyReviewResolutions.Actioned && !isFounder)
        {
            return Error(
                StatusCodes.Status403Forbidden,
                "community_report_action_founder_required",
                "Only the Founder can remove reported content.");
        }

        if (resolution == CommunitySafetyReviewResolutions.Actioned &&
            report.TargetKind == CommunitySafetyTargetKinds.SocialPost)
        {
            var removal = await _social.RemoveReportedPostAsync(
                report.TargetEntityId ?? Guid.Empty,
                cancellationToken);
            if (!removal.Succeeded)
            {
                return Error(
                    StatusCodes.Status409Conflict,
                    removal.ErrorCode ?? "community_report_action_failed",
                    removal.ErrorMessage ?? "Legend could not remove the reported content.");
            }
        }

        var result = await _communitySafety.ResolveReportAsync(
            reportId,
            resolved.Actor.Actor.UserId,
            resolution,
            cancellationToken);
        return result.Succeeded
            ? NoContent()
            : Error(
                StatusCodes.Status400BadRequest,
                result.ErrorCode ?? "community_report_resolution_invalid",
                result.ErrorMessage ?? "Legend could not record this report decision.");
    }

    private async Task<MobileActorRequestResolution> ResolveCommunityReviewerAsync(
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null || resolved.Actor is null)
            return resolved;

        var access = await _controlledResources.GetAccessAsync(
            resolved.Actor.Actor,
            ControlledResourceTypes.CommunityManagement,
            cancellationToken);
        if (access.State == ControlledResourceAccessStates.Granted)
            return resolved;

        return new MobileActorRequestResolution(
            null,
            resolved.PermittedActors,
            resolved.RequiresParticipantSelection,
            Error(
                StatusCodes.Status403Forbidden,
                "community_review_forbidden",
                "Community review access is not available for this account."));
    }
}

public sealed record MobileCommunityBlockRequest(string? TargetUserId, string? TargetParticipantType);
public sealed record MobileCommunityReportRequest(
    string? TargetUserId,
    string? TargetParticipantType,
    string? TargetKind,
    Guid? TargetEntityId,
    string? Category,
    string? Detail);

public sealed record MobileCommunityReportResolutionRequest(string? Resolution);
