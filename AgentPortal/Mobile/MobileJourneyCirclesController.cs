using Domain.Entities;
using Domain.JourneyCircles;
using Domain.Messaging;
using Infrastructure.Messaging;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Mobile;

[ApiController]
[Route("api/v1/mobile/journey-circles")]
[Authorize(Policy = MobileApiAuthorization.PolicyName)]
[IgnoreAntiforgeryToken]
[TypeFilter(typeof(MobileApiExceptionFilter))]
public sealed class MobileJourneyCirclesController : MobileApiControllerBase
{
    private readonly IJourneyCirclesService _journeyCircles;
    private readonly IMessagingProfileImageResolver _profiles;

    public MobileJourneyCirclesController(
        IMobileActorResolver actorResolver,
        IJourneyCirclesService journeyCircles,
        IMessagingProfileImageResolver profiles)
        : base(actorResolver)
    {
        _journeyCircles = journeyCircles;
        _profiles = profiles;
    }

    [HttpGet]
    public async Task<IActionResult> Dashboard(CancellationToken cancellationToken)
    {
        var actor = await ResolveClientActorAsync(cancellationToken);
        if (actor.Error is not null)
            return actor.Error;

        var dashboard = await _journeyCircles.GetDashboardAsync(actor.Actor!.Actor.UserId, cancellationToken);
        return Ok(await ToDashboardAsync(dashboard, cancellationToken));
    }

    [HttpPut("profile")]
    public async Task<IActionResult> SaveProfile(
        [FromBody] MobileJourneyProfileInput? request,
        CancellationToken cancellationToken)
    {
        var actor = await ResolveClientActorAsync(cancellationToken);
        if (actor.Error is not null)
            return actor.Error;
        if (request is null)
            return Error(StatusCodes.Status400BadRequest, "mobile_journey_input_required", "Journey Circles selections are required.");

        var result = await _journeyCircles.SaveProfileAsync(
            actor.Actor!.Actor.UserId,
            new JourneyCircleProfileInput(
                request.ConsentAffirmed,
                request.IsOptedIn,
                request.IsDiscoverable,
                request.AllowSuggestions,
                request.AllowConnectionRequests,
                request.Introduction,
                request.LifeStages,
                request.Locations,
                request.Goals,
                request.Interests,
                request.CircleCodes,
                request.ConnectionTypes,
                request.CommunicationStyles,
                request.AccountabilityFrequencies),
            cancellationToken);
        return result.Succeeded
            ? NoContent()
            : JourneyFailure(result);
    }

    [HttpPost("connections")]
    public async Task<IActionResult> RequestConnection(
        [FromBody] MobileJourneyConnectionRequest? request,
        CancellationToken cancellationToken)
    {
        var actor = await ResolveClientActorAsync(cancellationToken);
        if (actor.Error is not null)
            return actor.Error;
        if (request is null || request.TargetClientProfileId == Guid.Empty)
            return Error(StatusCodes.Status400BadRequest, "mobile_journey_target_required", "Choose an authorized Journey Circles connection.");

        var result = await _journeyCircles.RequestConnectionAsync(
            actor.Actor!.Actor.UserId,
            request.TargetClientProfileId,
            request.ConnectionReason,
            request.Introduction,
            cancellationToken);
        return result.Succeeded
            ? NoContent()
            : JourneyFailure(result);
    }

    [HttpPost("connections/{connectionId:guid}/response")]
    public async Task<IActionResult> RespondToConnection(
        Guid connectionId,
        [FromBody] MobileJourneyConnectionResponse? request,
        CancellationToken cancellationToken)
    {
        var actor = await ResolveClientActorAsync(cancellationToken);
        if (actor.Error is not null)
            return actor.Error;
        if (request is null)
            return Error(StatusCodes.Status400BadRequest, "mobile_journey_response_required", "Choose whether to accept the connection.");

        var result = await _journeyCircles.RespondToConnectionAsync(
            actor.Actor!.Actor.UserId,
            connectionId,
            request.Accept,
            cancellationToken);
        return result.Succeeded
            ? NoContent()
            : JourneyFailure(result);
    }

    [HttpPost("connections/{connectionId:guid}/disconnect")]
    public async Task<IActionResult> Disconnect(Guid connectionId, CancellationToken cancellationToken)
    {
        var actor = await ResolveClientActorAsync(cancellationToken);
        if (actor.Error is not null)
            return actor.Error;

        var result = await _journeyCircles.DisconnectAsync(actor.Actor!.Actor.UserId, connectionId, cancellationToken);
        return result.Succeeded
            ? NoContent()
            : JourneyFailure(result);
    }

    private async Task<MobileActorRequestResolution> ResolveClientActorAsync(CancellationToken cancellationToken)
    {
        var resolution = await ResolveActorAsync(cancellationToken);
        if (resolution.Error is not null || resolution.Actor is null)
            return resolution;

        return string.Equals(resolution.Actor.Actor.ParticipantType, MessagingParticipantTypes.Client, StringComparison.Ordinal)
            ? resolution
            : new MobileActorRequestResolution(
                null,
                resolution.PermittedActors,
                false,
                Error(
                    StatusCodes.Status403Forbidden,
                    "mobile_journey_client_role_required",
                    "Journey Circles is available from a client mobile identity."));
    }

    private async Task<MobileJourneyDashboard> ToDashboardAsync(
        JourneyCircleDashboard dashboard,
        CancellationToken cancellationToken)
    {
        var sourceProfiles = EnumerateProfiles(dashboard).ToArray();
        var sourceIds = sourceProfiles
            .Select(profile => profile.ClientProfileId)
            .Distinct()
            .ToArray();
        var identitiesByProfileId =
            await _profiles.ResolveClientIdentitiesByProfileIdAsync(
                sourceIds,
                cancellationToken);

        return new MobileJourneyDashboard(
            dashboard.Profile is null ? null : await ToProfileAsync(dashboard.Profile, identitiesByProfileId, cancellationToken),
            dashboard.Preferences is null ? null : new MobileJourneyPreferences(
                dashboard.Preferences.ConsentAffirmed,
                dashboard.Preferences.IsOptedIn,
                dashboard.Preferences.IsDiscoverable,
                dashboard.Preferences.AllowSuggestions,
                dashboard.Preferences.AllowConnectionRequests),
            await Task.WhenAll(dashboard.Recommendations.Select(async recommendation => new MobileJourneyRecommendation(
                await ToProfileAsync(recommendation.Profile, identitiesByProfileId, cancellationToken),
                recommendation.Explanation))),
            await Task.WhenAll(dashboard.Connections.Select(async connection => new MobileJourneyConnection(
                connection.Id,
                await ToProfileAsync(connection.Profile, identitiesByProfileId, cancellationToken),
                connection.Status,
                connection.ConnectionReason,
                connection.Introduction,
                connection.CreatedUtc))),
            await Task.WhenAll(dashboard.Requests.Select(async request => new MobileJourneyConnection(
                request.Id,
                await ToProfileAsync(request.Profile, identitiesByProfileId, cancellationToken),
                request.Status,
                request.ConnectionReason,
                request.Introduction,
                request.CreatedUtc))),
            new MobileJourneyTaxonomy(
                dashboard.Goals,
                dashboard.Circles,
                dashboard.LifeStages,
                dashboard.Locations,
                dashboard.Interests,
                dashboard.ConnectionTypes,
                dashboard.CommunicationStyles,
                dashboard.AccountabilityFrequencies));
    }

    private async Task<MobileJourneyProfile> ToProfileAsync(
        JourneyCirclePublicProfile profile,
        IReadOnlyDictionary<Guid, MessagingParticipantIdentity> identitiesByProfileId,
        CancellationToken cancellationToken)
    {
        MobileAvatarDto? avatar = null;
        if (identitiesByProfileId.TryGetValue(
                profile.ClientProfileId,
                out var identity))
        {
            avatar = await MobileAvatarProjection.ResolveAsync(
                _profiles,
                identity,
                cancellationToken);
        }

        return new MobileJourneyProfile(
            profile.ClientProfileId,
            profile.DisplayName,
            profile.Introduction,
            profile.LifeStages,
            profile.Locations,
            profile.Goals,
            profile.Interests,
            profile.CircleCodes,
            profile.ConnectionTypes,
            profile.CommunicationStyles,
            profile.AccountabilityFrequencies,
            avatar);
    }

    private static IEnumerable<JourneyCirclePublicProfile> EnumerateProfiles(JourneyCircleDashboard dashboard)
    {
        if (dashboard.Profile is not null)
            yield return dashboard.Profile;
        foreach (var recommendation in dashboard.Recommendations)
            yield return recommendation.Profile;
        foreach (var connection in dashboard.Connections)
            yield return connection.Profile;
        foreach (var request in dashboard.Requests)
            yield return request.Profile;
    }

    private IActionResult JourneyFailure(JourneyCircleOperationResult result) => Error(
        StatusCodes.Status403Forbidden,
        result.ErrorCode ?? "mobile_journey_rejected",
        result.ErrorMessage ?? "This Journey Circles action is not available.");
}

public sealed record MobileJourneyProfileInput(
    bool ConsentAffirmed,
    bool IsOptedIn,
    bool IsDiscoverable,
    bool AllowSuggestions,
    bool AllowConnectionRequests,
    string? Introduction,
    IReadOnlyList<string>? LifeStages,
    IReadOnlyList<string>? Locations,
    IReadOnlyList<string>? Goals,
    IReadOnlyList<string>? Interests,
    IReadOnlyList<string>? CircleCodes,
    IReadOnlyList<string>? ConnectionTypes,
    IReadOnlyList<string>? CommunicationStyles,
    IReadOnlyList<string>? AccountabilityFrequencies);

public sealed record MobileJourneyConnectionRequest(Guid TargetClientProfileId, string? ConnectionReason, string? Introduction);
public sealed record MobileJourneyConnectionResponse(bool Accept);
public sealed record MobileJourneyDashboard(
    MobileJourneyProfile? Profile,
    MobileJourneyPreferences? Preferences,
    IReadOnlyList<MobileJourneyRecommendation> Recommendations,
    IReadOnlyList<MobileJourneyConnection> Connections,
    IReadOnlyList<MobileJourneyConnection> Requests,
    MobileJourneyTaxonomy Taxonomy);
public sealed record MobileJourneyProfile(
    Guid ClientProfileId,
    string DisplayName,
    string? Introduction,
    IReadOnlyList<string> LifeStages,
    IReadOnlyList<string> Locations,
    IReadOnlyList<string> Goals,
    IReadOnlyList<string> Interests,
    IReadOnlyList<string> CircleCodes,
    IReadOnlyList<string> ConnectionTypes,
    IReadOnlyList<string> CommunicationStyles,
    IReadOnlyList<string> AccountabilityFrequencies,
    MobileAvatarDto? Avatar);
public sealed record MobileJourneyPreferences(bool ConsentAffirmed, bool IsOptedIn, bool IsDiscoverable, bool AllowSuggestions, bool AllowConnectionRequests);
public sealed record MobileJourneyRecommendation(MobileJourneyProfile Profile, string Explanation);
public sealed record MobileJourneyConnection(Guid Id, MobileJourneyProfile Profile, string Status, string? ConnectionReason, string? Introduction, DateTime CreatedUtc);
public sealed record MobileJourneyTaxonomy(IReadOnlyList<string> Goals, IReadOnlyList<string> Circles, IReadOnlyList<string> LifeStages, IReadOnlyList<string> Locations, IReadOnlyList<string> Interests, IReadOnlyList<string> ConnectionTypes, IReadOnlyList<string> CommunicationStyles, IReadOnlyList<string> AccountabilityFrequencies);
