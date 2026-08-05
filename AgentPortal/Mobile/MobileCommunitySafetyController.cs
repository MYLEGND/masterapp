using Domain.Messaging;
using Domain.Moderation;
using Infrastructure.Mobile;
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

    public MobileCommunitySafetyController(
        IMobileActorResolver actorResolver,
        ICommunitySafetyService communitySafety)
        : base(actorResolver)
    {
        _communitySafety = communitySafety;
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
}

public sealed record MobileCommunityBlockRequest(string? TargetUserId, string? TargetParticipantType);
public sealed record MobileCommunityReportRequest(
    string? TargetUserId,
    string? TargetParticipantType,
    string? TargetKind,
    Guid? TargetEntityId,
    string? Category,
    string? Detail);
