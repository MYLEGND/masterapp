using Domain.Messaging;
using Domain.Social;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Mobile;

/// <summary>
/// The mobile surface for the centralized discovery engine.
///
/// The scope a caller gets is derived from their resolved participant type on the
/// server. Recommendations remain consent-aware; the active directory lets clients
/// browse Legend members and lets agents browse both their clients and peer agents.
/// The client app cannot ask for a scope it is not entitled to.
/// </summary>
[ApiController]
[Route("api/v1/mobile/discovery")]
[Authorize(Policy = MobileApiAuthorization.PolicyName)]
[IgnoreAntiforgeryToken]
[TypeFilter(typeof(MobileApiExceptionFilter))]
public sealed class MobileDiscoveryController : MobileApiControllerBase
{
    private readonly ISocialDiscoveryService _discovery;
    private readonly ISocialFeedService _social;
    private readonly IMessagingProfileImageResolver _profiles;
    private readonly MasterAppDbContext? _db;

    public MobileDiscoveryController(
        IMobileActorResolver actorResolver,
        ISocialDiscoveryService discovery,
        ISocialFeedService social,
        IMessagingProfileImageResolver profiles,
        MasterAppDbContext? db = null)
        : base(actorResolver)
    {
        _discovery = discovery;
        _social = social;
        _profiles = profiles;
        _db = db;
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] string? query,
        [FromQuery] int offset,
        [FromQuery] int pageSize,
        [FromQuery] string? sort,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveSocialActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _discovery.SearchAsync(
            new SocialDiscoveryQuery(resolved.Actor!, query, offset, pageSize, sort),
            cancellationToken);

        return result.Succeeded && result.Value is not null
            ? Ok(await ToPageDtoAsync(result.Value, cancellationToken))
            : DiscoveryFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpGet("profiles/{clientProfileId:guid}")]
    public async Task<IActionResult> Profile(
        Guid clientProfileId,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveSocialActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _discovery.GetProfileAsync(
            resolved.Actor!,
            clientProfileId,
            cancellationToken);

        if (!result.Succeeded || result.Value is null)
            return DiscoveryFailure(result.ErrorCode, result.ErrorMessage);

        var profile = result.Value;

        // Content statistics come from the social feed authority, which enforces its own
        // visibility rules. A member can be discoverable while their posts are not
        // visible to this viewer, and that is a valid, non-error state.
        var metrics = await _social.GetProfileMetricsAsync(
            resolved.Actor!,
            new SocialAuthor(
                profile.Summary.UserId,
                profile.Summary.ParticipantType,
                profile.Summary.ClientProfileId,
                profile.Summary.DisplayName),
            cancellationToken);

        var contentVisible = metrics.Succeeded && metrics.Value is not null;

        return Ok(new MobileDiscoveryProfileDto(
            await ToResultDtoAsync(profile.Summary, cancellationToken),
            profile.Introduction,
            profile.LifeStages,
            profile.ConnectionTypes,
            contentVisible,
            contentVisible ? metrics.Value!.FollowerCount : 0,
            contentVisible ? metrics.Value!.FollowingCount : 0,
            contentVisible ? metrics.Value!.PostCount : 0,
            contentVisible ? metrics.Value!.VideoCount : 0,
            contentVisible ? metrics.Value!.StoryCount : 0));
    }

    private async Task<(SocialFeedActor? Actor, IActionResult? Error)> ResolveSocialActorAsync(
        CancellationToken cancellationToken)
    {
        var resolution = await ResolveActorAsync(cancellationToken);
        if (resolution.Error is not null || resolution.Actor is null)
        {
            return (null, resolution.Error ?? Error(
                StatusCodes.Status403Forbidden,
                "mobile_discovery_unavailable",
                "Your mobile identity is not available."));
        }

        return (new SocialFeedActor(
            resolution.Actor.Actor,
            resolution.Actor.ProfileId,
            resolution.Actor.DisplayName), null);
    }

    private async Task<MobileDiscoveryPageDto> ToPageDtoAsync(
        SocialDiscoveryPage page,
        CancellationToken cancellationToken)
    {
        // Avatar resolution shares the request-scoped DbContext, so projection stays
        // sequential exactly as it does in the social controller.
        var results = new List<MobileDiscoveryResultDto>(page.Results.Count);
        foreach (var result in page.Results)
            results.Add(await ToResultDtoAsync(result, cancellationToken));

        return new MobileDiscoveryPageDto(
            results,
            page.TotalCount,
            page.Offset,
            page.PageSize,
            page.HasMore,
            page.SortMode,
            page.Scope);
    }

    private async Task<MobileDiscoveryResultDto> ToResultDtoAsync(
        SocialDiscoveryResult result,
        CancellationToken cancellationToken)
    {
        var identity = new MessagingParticipantIdentity(
            result.UserId,
            result.ParticipantType,
            result.ClientProfileId,
            result.DisplayName,
            null,
            string.Empty);

        return new MobileDiscoveryResultDto(
            result.ClientProfileId,
            new MobileLogicalIdentityDto(result.UserId, result.ParticipantType),
            result.DisplayName,
            result.Headline,
            result.Location,
            result.Goals,
            result.Interests,
            result.CircleCodes,
            result.CompatibilityScore,
            result.MatchExplanation,
            new MobileDiscoveryRelationshipDto(
                result.Relationship.FollowedByCurrentActor,
                result.Relationship.FollowRequestPending,
                result.Relationship.FollowsCurrentActor,
                result.Relationship.ConnectionStatus,
                result.Relationship.ConnectionId,
                result.Relationship.CanRequestConnection,
                result.Relationship.CanFollow),
            await MobileAvatarProjection.ResolveAsync(_profiles, identity, cancellationToken),
            result.Username,
            result.Bio,
            result.Website,
            result.PublicEmail,
            result.IsPrivate,
            await IsVerifiedProfileAsync(
                result.ParticipantType,
                result.ClientProfileId,
                cancellationToken),
            result.RoleLabel);
    }

    private async Task<bool> IsVerifiedProfileAsync(
        string participantType,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        if (_db is null || profileId == Guid.Empty)
        {
            return false;
        }

        if (string.Equals(participantType, MessagingParticipantTypes.Client, StringComparison.Ordinal))
        {
            return await _db.ClientProfiles
                .AsNoTracking()
                .Where(profile => profile.Id == profileId)
                .Select(profile => profile.IsVerified)
                .SingleOrDefaultAsync(cancellationToken);
        }

        if (!string.Equals(participantType, MessagingParticipantTypes.Agent, StringComparison.Ordinal))
            return false;

        var profile = await _db.AgentProfiles
            .AsNoTracking()
            .Where(candidate => candidate.Id == profileId && candidate.IsActive)
            .Select(candidate => new { candidate.IsVerified, Email = candidate.NormalizedEmail ?? candidate.AgentUpn })
            .SingleOrDefaultAsync(cancellationToken);
        return profile?.IsVerified == true || LegendVerifiedIdentity.IsVerifiedAgentEmail(profile?.Email);
    }

    private IActionResult DiscoveryFailure(string? errorCode, string? errorMessage)
    {
        var status = errorCode switch
        {
            "social_discovery_query_invalid" or
            "social_discovery_profile_invalid" => StatusCodes.Status400BadRequest,
            "social_discovery_profile_unavailable" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status403Forbidden
        };

        return Error(
            status,
            errorCode ?? "mobile_discovery_rejected",
            errorMessage ?? "Discover is not available right now.");
    }
}

public sealed record MobileDiscoveryRelationshipDto(
    bool FollowedByCurrentActor,
    bool FollowRequestPending,
    bool FollowsCurrentActor,
    string ConnectionStatus,
    Guid? ConnectionId,
    bool CanRequestConnection,
    bool CanFollow);

public sealed record MobileDiscoveryResultDto(
    Guid ClientProfileId,
    MobileLogicalIdentityDto Identity,
    string DisplayName,
    string? Headline,
    string? Location,
    IReadOnlyList<string> Goals,
    IReadOnlyList<string> Interests,
    IReadOnlyList<string> CircleCodes,
    int CompatibilityScore,
    string? MatchExplanation,
    MobileDiscoveryRelationshipDto Relationship,
    MobileAvatarDto? Avatar,
    string? Username = null,
    string? Bio = null,
    string? Website = null,
    string? PublicEmail = null,
    bool IsPrivate = false,
    bool IsVerified = false,
    string? RoleLabel = null);

public sealed record MobileDiscoveryPageDto(
    IReadOnlyList<MobileDiscoveryResultDto> Results,
    int TotalCount,
    int Offset,
    int PageSize,
    bool HasMore,
    string SortMode,
    string Scope);

public sealed record MobileDiscoveryProfileDto(
    MobileDiscoveryResultDto Summary,
    string? Introduction,
    IReadOnlyList<string> LifeStages,
    IReadOnlyList<string> ConnectionTypes,
    bool ContentVisibleToCurrentActor,
    int FollowerCount,
    int FollowingCount,
    int PostCount,
    int ReelCount,
    int StoryCount);
