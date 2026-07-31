using Domain.Billing;
using Domain.Entities;
using Domain.JourneyCircles;
using Domain.Messaging;
using Domain.Social;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Social;

/// <summary>
/// The centralized Legend discovery engine.
///
/// Discovery reads the same ClientProfile, ClientEntitlement, JourneyCircleProfile and
/// SocialFollow tables the rest of the platform uses. It owns no directory of its own.
///
/// Two rules shape everything here:
///
/// 1. Compatibility ranks, it never restricts. The Journey Circles suggestion feed
///    applies a minimum score and returns a dozen people; Discover applies no score
///    floor at all, so every consented member stays reachable through search and
///    directory browsing.
///
/// 2. Consent is the outer boundary. Only members who affirmed the Journey Circles
///    consent and left themselves discoverable appear in the community scope. A client
///    who never opted into community discovery is never listed, no matter who searches.
/// </summary>
public sealed class SocialDiscoveryService : ISocialDiscoveryService
{
    private const int MaximumPageSize = 30;
    private const int DefaultPageSize = 20;
    private const int MaximumSearchTextLength = 120;

    /// <summary>
    /// How many candidates the Recommended sort scores in memory. Beyond this window a
    /// caller pages through Directory or Relevance instead, both of which are fully
    /// server-side and reach the entire directory.
    /// </summary>
    private const int RankingWindow = 500;

    private const string CommunityAccessActive = "Active";

    private readonly MasterAppDbContext _db;

    public SocialDiscoveryService(MasterAppDbContext db)
    {
        _db = db;
    }

    public async Task<SocialOperationResult<SocialDiscoveryPage>> SearchAsync(
        SocialDiscoveryQuery query,
        CancellationToken cancellationToken = default)
    {
        var searchText = NormalizeSearchText(query.SearchText);
        if (searchText is not null && searchText.Length > MaximumSearchTextLength)
        {
            return SocialOperationResult<SocialDiscoveryPage>.Failure(
                "social_discovery_query_invalid",
                $"Keep the search under {MaximumSearchTextLength} characters.");
        }

        var pageSize = Math.Clamp(
            query.PageSize <= 0 ? DefaultPageSize : query.PageSize,
            1,
            MaximumPageSize);
        var offset = Math.Max(0, query.Offset);

        var participantType = query.Actor.Identity.ParticipantType;
        if (participantType == MessagingParticipantTypes.Agent)
        {
            return await SearchOwnedClientsAsync(
                query.Actor, searchText, offset, pageSize, cancellationToken);
        }

        if (participantType == MessagingParticipantTypes.Client)
        {
            return await SearchCommunityAsync(
                query.Actor, searchText, offset, pageSize, query.SortMode, cancellationToken);
        }

        return SocialOperationResult<SocialDiscoveryPage>.Failure(
            "social_discovery_scope_unavailable",
            "Discover is not available for this identity.");
    }

    // ---------------------------------------------------------------- community

    private async Task<SocialOperationResult<SocialDiscoveryPage>> SearchCommunityAsync(
        SocialFeedActor actor,
        string? searchText,
        int offset,
        int pageSize,
        string? requestedSortMode,
        CancellationToken cancellationToken)
    {
        var viewer = await FindClientProfileAsync(actor.Identity.UserId, cancellationToken);
        if (viewer is null)
        {
            return SocialOperationResult<SocialDiscoveryPage>.Failure(
                "social_actor_invalid",
                "Your mobile identity is not available for Discover.");
        }

        var blockedIds = await BlockedProfileIdsAsync(viewer.Id, cancellationToken);

        var candidates = DiscoverableCommunityProfiles(viewer.Id, blockedIds);
        candidates = ApplyCommunitySearch(candidates, searchText);

        // Ranking only makes sense with no search text. Once someone types, what they
        // typed is the ranking signal.
        var sortMode = ResolveSortMode(requestedSortMode, searchText);

        return sortMode == SocialDiscoverySortModes.Recommended
            ? await RecommendedPageAsync(actor, viewer, candidates, offset, pageSize, cancellationToken)
            : await OrderedPageAsync(actor, viewer, candidates, offset, pageSize, sortMode, searchText, cancellationToken);
    }

    /// <summary>
    /// Consented, discoverable members whose ClientApp entitlement is currently good.
    /// The entitlement join reuses the materialized ClientEntitlements rows that the
    /// billing orchestrator maintains, so Discover never re-derives billing rules.
    /// </summary>
    private IQueryable<CommunityCandidate> DiscoverableCommunityProfiles(
        Guid viewerProfileId,
        IReadOnlyCollection<Guid> blockedIds) =>
        from journey in _db.JourneyCircleProfiles.AsNoTracking()
        join client in _db.ClientProfiles.AsNoTracking()
            on journey.ClientProfileId equals client.Id
        where journey.ClientProfileId != viewerProfileId
              && journey.ConsentAffirmedUtc != null
              && journey.IsOptedIn
              && journey.IsDiscoverable
              && journey.CommunityAccessState == CommunityAccessActive
              && !blockedIds.Contains(journey.ClientProfileId)
              && _db.ClientEntitlements.Any(entitlement =>
                  entitlement.ClientProfileId == client.Id
                  && entitlement.EntitlementKey == BillingEntitlementKeys.ClientAppFullAccess
                  && (entitlement.Status == ClientEntitlementStatus.Active
                      || entitlement.Status == ClientEntitlementStatus.GracePeriod))
        select new CommunityCandidate
        {
            Journey = journey,
            ClientProfileId = client.Id,
            ClientUserId = client.ClientUserId,
            ExternalIdentityObjectId = client.ExternalIdentityObjectId
        };

    /// <summary>
    /// Community search deliberately never matches the legal name or the email on the
    /// client record. Members are found by the community identity they chose to publish.
    /// </summary>
    private static IQueryable<CommunityCandidate> ApplyCommunitySearch(
        IQueryable<CommunityCandidate> candidates,
        string? searchText)
    {
        if (searchText is null)
            return candidates;

        // Both sides are lowercased explicitly. SQL Server's default collation is
        // case-insensitive while other providers are not, and search must not behave
        // differently depending on where it runs.
        var pattern = $"%{EscapeLike(searchText).ToLowerInvariant()}%";
        return candidates.Where(candidate =>
            EF.Functions.Like(candidate.Journey.DisplayName.ToLower(), pattern)
            || (candidate.Journey.Introduction != null
                && EF.Functions.Like(candidate.Journey.Introduction.ToLower(), pattern))
            || (candidate.Journey.LocationLabel != null
                && EF.Functions.Like(candidate.Journey.LocationLabel.ToLower(), pattern))
            || (candidate.Journey.LifeStage != null
                && EF.Functions.Like(candidate.Journey.LifeStage.ToLower(), pattern))
            || EF.Functions.Like(candidate.Journey.GoalsJson.ToLower(), pattern)
            || EF.Functions.Like(candidate.Journey.InterestsJson.ToLower(), pattern)
            || EF.Functions.Like(candidate.Journey.CircleCodesJson.ToLower(), pattern));
    }

    /// <summary>
    /// Compatibility-ordered suggestions over a bounded window. No score floor is
    /// applied: a zero-compatibility member still appears, just last.
    /// </summary>
    private async Task<SocialOperationResult<SocialDiscoveryPage>> RecommendedPageAsync(
        SocialFeedActor actor,
        ClientProfile viewer,
        IQueryable<CommunityCandidate> candidates,
        int offset,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var viewerJourney = await _db.JourneyCircleProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(profile => profile.ClientProfileId == viewer.Id, cancellationToken);

        var window = await candidates
            .OrderByDescending(candidate => candidate.Journey.UpdatedUtc)
            .ThenBy(candidate => candidate.ClientProfileId)
            .Take(RankingWindow)
            .ToArrayAsync(cancellationToken);

        var viewerTraits = viewerJourney is null
            ? null
            : JourneyCircleCompatibilityScorer.Traits(viewerJourney);

        var scored = window
            .Select(candidate =>
            {
                var compatibility = viewerTraits is null
                    ? JourneyCircleCompatibility.None
                    : JourneyCircleCompatibilityScorer.Evaluate(
                        viewerTraits,
                        JourneyCircleCompatibilityScorer.Traits(candidate.Journey));
                return (Candidate: candidate, Compatibility: compatibility);
            })
            .OrderByDescending(entry => entry.Compatibility.Score)
            .ThenByDescending(entry => entry.Candidate.Journey.UpdatedUtc)
            .ThenBy(entry => entry.Candidate.ClientProfileId)
            .ToArray();

        var page = scored.Skip(offset).Take(pageSize).ToArray();
        var results = await ProjectAsync(
            actor,
            viewer,
            page.Select(entry => (entry.Candidate, entry.Compatibility)).ToArray(),
            cancellationToken);

        return SocialOperationResult<SocialDiscoveryPage>.Success(new SocialDiscoveryPage(
            results,
            scored.Length,
            offset,
            pageSize,
            offset + page.Length < scored.Length,
            SocialDiscoverySortModes.Recommended,
            SocialDiscoveryScopes.Community));
    }

    /// <summary>
    /// Fully server-side paging across the entire consented directory.
    /// </summary>
    private async Task<SocialOperationResult<SocialDiscoveryPage>> OrderedPageAsync(
        SocialFeedActor actor,
        ClientProfile viewer,
        IQueryable<CommunityCandidate> candidates,
        int offset,
        int pageSize,
        string sortMode,
        string? searchText,
        CancellationToken cancellationToken)
    {
        var totalCount = await candidates.CountAsync(cancellationToken);

        IOrderedQueryable<CommunityCandidate> ordered;
        if (sortMode == SocialDiscoverySortModes.Relevance && searchText is not null)
        {
            var lowered = EscapeLike(searchText).ToLowerInvariant();
            var prefix = $"{lowered}%";
            var contains = $"%{lowered}%";
            // Name matches outrank bio and tag matches; a display name that starts with
            // the query outranks one that merely contains it.
            ordered = candidates
                .OrderBy(candidate =>
                    EF.Functions.Like(candidate.Journey.DisplayName.ToLower(), prefix) ? 0
                    : EF.Functions.Like(candidate.Journey.DisplayName.ToLower(), contains) ? 1
                    : 2)
                .ThenBy(candidate => candidate.Journey.DisplayName)
                .ThenBy(candidate => candidate.ClientProfileId);
        }
        else
        {
            ordered = candidates
                .OrderBy(candidate => candidate.Journey.DisplayName)
                .ThenBy(candidate => candidate.ClientProfileId);
        }

        var page = await ordered
            .Skip(offset)
            .Take(pageSize)
            .ToArrayAsync(cancellationToken);

        var viewerJourney = await _db.JourneyCircleProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(profile => profile.ClientProfileId == viewer.Id, cancellationToken);
        var viewerTraits = viewerJourney is null
            ? null
            : JourneyCircleCompatibilityScorer.Traits(viewerJourney);

        // Scoring the returned page only: it annotates results, it does not order them.
        var scoredPage = page
            .Select(candidate => (
                Candidate: candidate,
                Compatibility: viewerTraits is null
                    ? JourneyCircleCompatibility.None
                    : JourneyCircleCompatibilityScorer.Evaluate(
                        viewerTraits,
                        JourneyCircleCompatibilityScorer.Traits(candidate.Journey))))
            .ToArray();

        var results = await ProjectAsync(actor, viewer, scoredPage, cancellationToken);

        return SocialOperationResult<SocialDiscoveryPage>.Success(new SocialDiscoveryPage(
            results,
            totalCount,
            offset,
            pageSize,
            offset + page.Length < totalCount,
            sortMode,
            SocialDiscoveryScopes.Community));
    }

    // ------------------------------------------------------------- agent scope

    /// <summary>
    /// An agent searches the clients they already own through the AgentClient
    /// relationship. Community members who are not this agent's clients are never
    /// returned: those people consented to peer discovery, not to agent discovery.
    /// </summary>
    private async Task<SocialOperationResult<SocialDiscoveryPage>> SearchOwnedClientsAsync(
        SocialFeedActor actor,
        string? searchText,
        int offset,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var agentUserId = Normalize(actor.Identity.UserId);

        var owned = from link in _db.AgentClients.AsNoTracking()
                    join client in _db.ClientProfiles.AsNoTracking()
                        on link.ClientUserId.ToLower() equals client.ClientUserId.ToLower()
                    where link.AgentUserId.ToLower() == agentUserId
                    select client;

        if (searchText is not null)
        {
            var pattern = $"%{EscapeLike(searchText).ToLowerInvariant()}%";
            // The agent already holds this CRM record, so name and email are in scope here.
            owned = owned.Where(client =>
                EF.Functions.Like(client.FirstName.ToLower(), pattern)
                || EF.Functions.Like(client.LastName.ToLower(), pattern)
                || EF.Functions.Like(client.Email.ToLower(), pattern));
        }

        var totalCount = await owned
            .Select(client => client.Id)
            .Distinct()
            .CountAsync(cancellationToken);

        var page = await owned
            .Distinct()
            .OrderBy(client => client.LastName)
            .ThenBy(client => client.FirstName)
            .ThenBy(client => client.Id)
            .Skip(offset)
            .Take(pageSize)
            .ToArrayAsync(cancellationToken);

        var pageIds = page.Select(client => client.Id).ToArray();
        var journeyByProfileId = await _db.JourneyCircleProfiles
            .AsNoTracking()
            .Where(profile => pageIds.Contains(profile.ClientProfileId))
            .ToDictionaryAsync(profile => profile.ClientProfileId, cancellationToken);

        var candidates = page
            .Select(client => (
                Candidate: new CommunityCandidate
                {
                    Journey = journeyByProfileId.GetValueOrDefault(client.Id)
                        ?? PlaceholderJourney(client),
                    ClientProfileId = client.Id,
                    ClientUserId = client.ClientUserId,
                    ExternalIdentityObjectId = client.ExternalIdentityObjectId
                },
                Compatibility: JourneyCircleCompatibility.None))
            .ToArray();

        var results = await ProjectAsync(actor, viewer: null, candidates, cancellationToken);

        return SocialOperationResult<SocialDiscoveryPage>.Success(new SocialDiscoveryPage(
            results,
            totalCount,
            offset,
            pageSize,
            offset + page.Length < totalCount,
            SocialDiscoverySortModes.Directory,
            SocialDiscoveryScopes.OwnedClients));
    }

    /// <summary>
    /// A client of this agent who never joined Journey Circles still needs a name to
    /// render. This stands in for the community profile they do not have; it is never
    /// persisted and never leaves the agent's own scope.
    /// </summary>
    private static JourneyCircleProfile PlaceholderJourney(ClientProfile client) => new()
    {
        ClientProfileId = client.Id,
        DisplayName = string.IsNullOrWhiteSpace($"{client.FirstName} {client.LastName}".Trim())
            ? client.Email
            : $"{client.FirstName} {client.LastName}".Trim(),
        IsOptedIn = false,
        IsDiscoverable = false
    };

    // ------------------------------------------------------------- projection

    private async Task<IReadOnlyList<SocialDiscoveryResult>> ProjectAsync(
        SocialFeedActor actor,
        ClientProfile? viewer,
        IReadOnlyList<(CommunityCandidate Candidate, JourneyCircleCompatibility Compatibility)> page,
        CancellationToken cancellationToken)
    {
        if (page.Count == 0)
            return Array.Empty<SocialDiscoveryResult>();

        var actorUserId = Normalize(actor.Identity.UserId);
        var actorParticipantType = actor.Identity.ParticipantType;
        var targetUserIds = page
            .Select(entry => CanonicalUserId(entry.Candidate))
            .Where(userId => !string.IsNullOrWhiteSpace(userId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var following = await _db.SocialFollows
            .AsNoTracking()
            .Where(follow => follow.FollowerUserId == actorUserId
                             && follow.FollowerParticipantType == actorParticipantType
                             && follow.FollowedParticipantType == MessagingParticipantTypes.Client
                             && targetUserIds.Contains(follow.FollowedUserId))
            .Select(follow => follow.FollowedUserId)
            .ToHashSetAsync(StringComparer.Ordinal, cancellationToken);

        var followers = await _db.SocialFollows
            .AsNoTracking()
            .Where(follow => follow.FollowedUserId == actorUserId
                             && follow.FollowedParticipantType == actorParticipantType
                             && follow.FollowerParticipantType == MessagingParticipantTypes.Client
                             && targetUserIds.Contains(follow.FollowerUserId))
            .Select(follow => follow.FollowerUserId)
            .ToHashSetAsync(StringComparer.Ordinal, cancellationToken);

        var connectionsByProfileId = new Dictionary<Guid, JourneyCircleConnection>();
        if (viewer is not null)
        {
            var profileIds = page.Select(entry => entry.Candidate.ClientProfileId).ToArray();
            var connections = await _db.JourneyCircleConnections
                .AsNoTracking()
                .Where(connection =>
                    (connection.RequesterClientProfileId == viewer.Id
                     && profileIds.Contains(connection.RecipientClientProfileId))
                    || (connection.RecipientClientProfileId == viewer.Id
                        && profileIds.Contains(connection.RequesterClientProfileId)))
                .ToArrayAsync(cancellationToken);

            foreach (var connection in connections)
            {
                var otherId = connection.RequesterClientProfileId == viewer.Id
                    ? connection.RecipientClientProfileId
                    : connection.RequesterClientProfileId;
                connectionsByProfileId[otherId] = connection;
            }
        }

        return page.Select(entry =>
        {
            var candidate = entry.Candidate;
            var userId = CanonicalUserId(candidate);
            var connection = connectionsByProfileId.GetValueOrDefault(candidate.ClientProfileId);
            var connectionStatus = connection?.Status ?? JourneyCircleConnectionStatuses.None;

            return new SocialDiscoveryResult(
                candidate.ClientProfileId,
                userId,
                MessagingParticipantTypes.Client,
                candidate.Journey.DisplayName,
                candidate.Journey.Introduction,
                candidate.Journey.LocationLabel,
                JourneyCircleCompatibilityScorer.FromJson(candidate.Journey.GoalsJson),
                JourneyCircleCompatibilityScorer.FromJson(candidate.Journey.InterestsJson),
                JourneyCircleCompatibilityScorer.FromJson(candidate.Journey.CircleCodesJson),
                entry.Compatibility.Score,
                entry.Compatibility.DiscoveryExplanation,
                new SocialDiscoveryRelationship(
                    following.Contains(userId),
                    followers.Contains(userId),
                    connectionStatus,
                    connection?.Id,
                    viewer is not null
                        && candidate.Journey.AllowConnectionRequests
                        && connectionStatus == JourneyCircleConnectionStatuses.None,
                    !string.IsNullOrWhiteSpace(userId)));
        }).ToArray();
    }

    // ---------------------------------------------------------------- profile

    public async Task<SocialOperationResult<SocialDiscoveryProfile>> GetProfileAsync(
        SocialFeedActor actor,
        Guid clientProfileId,
        CancellationToken cancellationToken = default)
    {
        if (clientProfileId == Guid.Empty)
        {
            return SocialOperationResult<SocialDiscoveryProfile>.Failure(
                "social_discovery_profile_invalid",
                "Choose a Legend member to open.");
        }

        // Reachability is decided by the same scope the search uses, so a profile can
        // never be opened by URL guessing.
        var reachable = await ReachableCandidateAsync(actor, clientProfileId, cancellationToken);
        if (reachable is null)
        {
            return SocialOperationResult<SocialDiscoveryProfile>.Failure(
                "social_discovery_profile_unavailable",
                "This Legend member is not available from your Discover scope.");
        }

        var (candidate, viewer) = reachable.Value;
        var viewerJourney = viewer is null
            ? null
            : await _db.JourneyCircleProfiles
                .AsNoTracking()
                .FirstOrDefaultAsync(profile => profile.ClientProfileId == viewer.Id, cancellationToken);

        var compatibility = viewerJourney is null
            ? JourneyCircleCompatibility.None
            : JourneyCircleCompatibilityScorer.Evaluate(
                JourneyCircleCompatibilityScorer.Traits(viewerJourney),
                JourneyCircleCompatibilityScorer.Traits(candidate.Journey));

        var summaries = await ProjectAsync(
            actor,
            viewer,
            [(candidate, compatibility)],
            cancellationToken);

        return SocialOperationResult<SocialDiscoveryProfile>.Success(new SocialDiscoveryProfile(
            summaries[0],
            candidate.Journey.Introduction,
            JourneyCircleCompatibilityScorer.FromDelimited(candidate.Journey.LifeStage),
            JourneyCircleCompatibilityScorer.FromJson(candidate.Journey.ConnectionTypesJson)));
    }

    public async Task<bool> IsDiscoverableByAsync(
        SocialFeedActor actor,
        string targetUserId,
        string targetParticipantType,
        CancellationToken cancellationToken = default)
    {
        if (targetParticipantType != MessagingParticipantTypes.Client)
            return false;

        var normalized = Normalize(targetUserId);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var target = await FindClientProfileAsync(normalized, cancellationToken);
        if (target is null)
            return false;

        var reachable = await ReachableCandidateAsync(actor, target.Id, cancellationToken);
        return reachable is not null;
    }

    private async Task<(CommunityCandidate Candidate, ClientProfile? Viewer)?> ReachableCandidateAsync(
        SocialFeedActor actor,
        Guid clientProfileId,
        CancellationToken cancellationToken)
    {
        if (actor.Identity.ParticipantType == MessagingParticipantTypes.Agent)
        {
            var agentUserId = Normalize(actor.Identity.UserId);
            var owned = await (from link in _db.AgentClients.AsNoTracking()
                               join client in _db.ClientProfiles.AsNoTracking()
                                   on link.ClientUserId.ToLower() equals client.ClientUserId.ToLower()
                               where link.AgentUserId.ToLower() == agentUserId && client.Id == clientProfileId
                               select client)
                .FirstOrDefaultAsync(cancellationToken);

            if (owned is null)
                return null;

            var journey = await _db.JourneyCircleProfiles
                .AsNoTracking()
                .FirstOrDefaultAsync(profile => profile.ClientProfileId == owned.Id, cancellationToken);

            return (new CommunityCandidate
            {
                Journey = journey ?? PlaceholderJourney(owned),
                ClientProfileId = owned.Id,
                ClientUserId = owned.ClientUserId,
                ExternalIdentityObjectId = owned.ExternalIdentityObjectId
            }, null);
        }

        if (actor.Identity.ParticipantType != MessagingParticipantTypes.Client)
            return null;

        var viewer = await FindClientProfileAsync(actor.Identity.UserId, cancellationToken);
        if (viewer is null)
            return null;

        var blockedIds = await BlockedProfileIdsAsync(viewer.Id, cancellationToken);
        var candidate = await DiscoverableCommunityProfiles(viewer.Id, blockedIds)
            .FirstOrDefaultAsync(entry => entry.ClientProfileId == clientProfileId, cancellationToken);

        return candidate is null ? null : (candidate, viewer);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<List<Guid>> BlockedProfileIdsAsync(Guid viewerProfileId, CancellationToken cancellationToken) =>
        await _db.JourneyCircleBlocks
            .AsNoTracking()
            .Where(block => block.BlockerClientProfileId == viewerProfileId
                            || block.BlockedClientProfileId == viewerProfileId)
            .Select(block => block.BlockerClientProfileId == viewerProfileId
                ? block.BlockedClientProfileId
                : block.BlockerClientProfileId)
            .Distinct()
            .ToListAsync(cancellationToken);

    private Task<ClientProfile?> FindClientProfileAsync(string userId, CancellationToken cancellationToken)
    {
        var normalized = Normalize(userId);
        return _db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(client =>
                client.ClientUserId.ToLower() == normalized
                || (client.ExternalIdentityObjectId != null
                    && client.ExternalIdentityObjectId.ToLower() == normalized),
                cancellationToken);
    }

    /// <summary>
    /// The same canonical rule the rest of the platform uses: the Entra object ID when
    /// present, otherwise the legacy ClientUserId.
    /// </summary>
    private static string CanonicalUserId(CommunityCandidate candidate) =>
        Normalize(string.IsNullOrWhiteSpace(candidate.ExternalIdentityObjectId)
            ? candidate.ClientUserId
            : candidate.ExternalIdentityObjectId);

    private static string ResolveSortMode(string? requested, string? searchText)
    {
        if (searchText is not null)
            return SocialDiscoverySortModes.Relevance;

        return requested?.Trim() switch
        {
            SocialDiscoverySortModes.Directory => SocialDiscoverySortModes.Directory,
            _ => SocialDiscoverySortModes.Recommended
        };
    }

    private static string? NormalizeSearchText(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string EscapeLike(string value) => value
        .Replace("[", "[[]")
        .Replace("%", "[%]")
        .Replace("_", "[_]");

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;

    private sealed class CommunityCandidate
    {
        public required JourneyCircleProfile Journey { get; init; }
        public required Guid ClientProfileId { get; init; }
        public required string ClientUserId { get; init; }
        public required string? ExternalIdentityObjectId { get; init; }
    }
}
