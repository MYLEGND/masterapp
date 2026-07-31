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
            return await SearchActiveDirectoryAsync(
                query.Actor, searchText, offset, pageSize, SocialDiscoveryScopes.OwnedClients, cancellationToken);
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
            : await SearchActiveDirectoryAsync(
                actor, searchText, offset, pageSize, SocialDiscoveryScopes.Community, cancellationToken);
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
    /// The browse/search directory is deliberately broader than recommendations.
    /// Recommendations honor Journey Circles preferences; this directory exposes
    /// active mobile clients and active agents from their authoritative profiles.
    /// It is the single source for client secondary results and agent-to-agent search.
    /// </summary>
    private async Task<SocialOperationResult<SocialDiscoveryPage>> SearchActiveDirectoryAsync(
        SocialFeedActor actor,
        string? searchText,
        int offset,
        int pageSize,
        string scope,
        CancellationToken cancellationToken)
    {
        var candidates = await ActiveDirectoryCandidatesAsync(actor, cancellationToken);
        var matching = searchText is null
            ? candidates
            : candidates.Where(candidate => MatchesDirectorySearch(candidate, searchText)).ToArray();
        var ordered = matching
            .OrderBy(candidate => DirectoryMatchRank(candidate, searchText))
            .ThenBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.ParticipantType, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.ProfileId)
            .ToArray();
        var page = ordered.Skip(offset).Take(pageSize).ToArray();
        var viewer = actor.Identity.ParticipantType == MessagingParticipantTypes.Client
            ? await FindClientProfileAsync(actor.Identity.UserId, cancellationToken)
            : null;
        var results = await ProjectDirectoryAsync(actor, viewer, page, cancellationToken);

        return SocialOperationResult<SocialDiscoveryPage>.Success(new SocialDiscoveryPage(
            results,
            ordered.Length,
            offset,
            pageSize,
            offset + page.Length < ordered.Length,
            searchText is null ? SocialDiscoverySortModes.Directory : SocialDiscoverySortModes.Relevance,
            scope));
    }

    private async Task<DirectoryCandidate[]> ActiveDirectoryCandidatesAsync(
        SocialFeedActor actor,
        CancellationToken cancellationToken)
    {
        var isClient = actor.Identity.ParticipantType == MessagingParticipantTypes.Client;
        var blockedIds = isClient
            ? await BlockedProfileIdsAsync(actor.ProfileId, cancellationToken)
            : [];

        var clientRows = isClient
            ? await ActiveClientDirectoryRowsAsync(actor.ProfileId, blockedIds, cancellationToken)
            : await OwnedClientDirectoryRowsAsync(actor.Identity.UserId, cancellationToken);
        var clients = clientRows
            .GroupBy(row => row.Client.Id)
            .Select(group => ToDirectoryCandidate(group.First()))
            .ToArray();
        var agents = await _db.AgentProfiles
            .AsNoTracking()
            .Where(profile => profile.IsActive && profile.Id != actor.ProfileId)
            .Select(profile => new AgentDirectoryRow(
                profile.Id,
                profile.AgentUserId,
                profile.FullName,
                profile.AgentUpn,
                profile.Title,
                profile.ShortBio))
            .ToArrayAsync(cancellationToken);

        return clients
            .Concat(agents.Select(ToDirectoryCandidate))
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.UserId))
            .ToArray();
    }

    private Task<ClientDirectoryRow[]> ActiveClientDirectoryRowsAsync(
        Guid viewerProfileId,
        IReadOnlyCollection<Guid> blockedIds,
        CancellationToken cancellationToken) =>
        (from client in _db.ClientProfiles.AsNoTracking()
         join journey in _db.JourneyCircleProfiles.AsNoTracking()
             on client.Id equals journey.ClientProfileId into journeys
         from journey in journeys.DefaultIfEmpty()
         where client.Id != viewerProfileId
               && !blockedIds.Contains(client.Id)
               && (client.CrmStatus == null || client.CrmStatus == "Active")
               && _db.ClientEntitlements.Any(entitlement =>
                   entitlement.ClientProfileId == client.Id
                   && entitlement.EntitlementKey == BillingEntitlementKeys.ClientAppFullAccess
                   && (entitlement.Status == ClientEntitlementStatus.Active
                       || entitlement.Status == ClientEntitlementStatus.GracePeriod))
         select new ClientDirectoryRow(client, journey))
            .ToArrayAsync(cancellationToken);

    private Task<ClientDirectoryRow[]> OwnedClientDirectoryRowsAsync(
        string agentUserId,
        CancellationToken cancellationToken)
    {
        var normalizedAgentId = Normalize(agentUserId);
        return (from link in _db.AgentClients.AsNoTracking()
                join client in _db.ClientProfiles.AsNoTracking()
                    on link.ClientUserId.ToLower() equals client.ClientUserId.ToLower()
                join journey in _db.JourneyCircleProfiles.AsNoTracking()
                    on client.Id equals journey.ClientProfileId into journeys
                from journey in journeys.DefaultIfEmpty()
                where link.AgentUserId.ToLower() == normalizedAgentId
                select new ClientDirectoryRow(client, journey))
            .ToArrayAsync(cancellationToken);
    }

    private static DirectoryCandidate ToDirectoryCandidate(ClientDirectoryRow row)
    {
        var journey = row.Journey;
        var displayName = FirstNonEmpty(
            journey?.DisplayName,
            $"{row.Client.FirstName} {row.Client.LastName}".Trim(),
            "Legend member");
        return new DirectoryCandidate(
            row.Client.Id,
            CanonicalUserId(row.Client),
            MessagingParticipantTypes.Client,
            displayName,
            journey?.Introduction,
            journey?.LocationLabel,
            JourneyCircleCompatibilityScorer.FromJson(journey?.GoalsJson),
            JourneyCircleCompatibilityScorer.FromJson(journey?.InterestsJson),
            JourneyCircleCompatibilityScorer.FromJson(journey?.CircleCodesJson),
            journey?.AllowConnectionRequests == true,
            journey?.Introduction,
            JourneyCircleCompatibilityScorer.FromDelimited(journey?.LifeStage),
            JourneyCircleCompatibilityScorer.FromJson(journey?.ConnectionTypesJson));
    }

    private static DirectoryCandidate ToDirectoryCandidate(AgentDirectoryRow agent)
    {
        var displayName = FirstNonEmpty(agent.FullName, agent.AgentUpn, "Legend Agent");
        return new DirectoryCandidate(
            agent.Id,
            Normalize(agent.AgentUserId),
            MessagingParticipantTypes.Agent,
            displayName,
            agent.Title,
            null,
            [],
            [],
            [],
            false,
            agent.ShortBio,
            [],
            []);
    }

    private async Task<IReadOnlyList<SocialDiscoveryResult>> ProjectDirectoryAsync(
        SocialFeedActor actor,
        ClientProfile? viewer,
        IReadOnlyList<DirectoryCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
            return Array.Empty<SocialDiscoveryResult>();

        var actorKey = new DirectoryIdentity(actor.Identity.UserId, actor.Identity.ParticipantType);
        var userIds = candidates.Select(candidate => candidate.UserId).Distinct(StringComparer.Ordinal).ToArray();
        var targetKeys = candidates
            .Select(candidate => new DirectoryIdentity(candidate.UserId, candidate.ParticipantType))
            .ToHashSet();
        var followingRows = await _db.SocialFollows.AsNoTracking()
            .Where(follow => follow.FollowerUserId == actorKey.UserId
                             && follow.FollowerParticipantType == actorKey.ParticipantType
                             && userIds.Contains(follow.FollowedUserId))
            .Select(follow => new { follow.FollowedUserId, follow.FollowedParticipantType })
            .ToArrayAsync(cancellationToken);
        var followerRows = await _db.SocialFollows.AsNoTracking()
            .Where(follow => follow.FollowedUserId == actorKey.UserId
                             && follow.FollowedParticipantType == actorKey.ParticipantType
                             && userIds.Contains(follow.FollowerUserId))
            .Select(follow => new { follow.FollowerUserId, follow.FollowerParticipantType })
            .ToArrayAsync(cancellationToken);
        var following = followingRows
            .Select(row => new DirectoryIdentity(row.FollowedUserId, row.FollowedParticipantType))
            .Where(targetKeys.Contains)
            .ToHashSet();
        var followers = followerRows
            .Select(row => new DirectoryIdentity(row.FollowerUserId, row.FollowerParticipantType))
            .Where(targetKeys.Contains)
            .ToHashSet();

        var connections = new Dictionary<Guid, JourneyCircleConnection>();
        if (viewer is not null)
        {
            var clientProfileIds = candidates
                .Where(candidate => candidate.ParticipantType == MessagingParticipantTypes.Client)
                .Select(candidate => candidate.ProfileId)
                .ToArray();
            var rows = await _db.JourneyCircleConnections.AsNoTracking()
                .Where(connection =>
                    (connection.RequesterClientProfileId == viewer.Id
                     && clientProfileIds.Contains(connection.RecipientClientProfileId))
                    || (connection.RecipientClientProfileId == viewer.Id
                        && clientProfileIds.Contains(connection.RequesterClientProfileId)))
                .ToArrayAsync(cancellationToken);
            foreach (var row in rows)
            {
                var otherId = row.RequesterClientProfileId == viewer.Id
                    ? row.RecipientClientProfileId
                    : row.RequesterClientProfileId;
                connections[otherId] = row;
            }
        }

        return candidates.Select(candidate =>
        {
            var key = new DirectoryIdentity(candidate.UserId, candidate.ParticipantType);
            var connection = connections.GetValueOrDefault(candidate.ProfileId);
            var connectionStatus = connection?.Status ?? JourneyCircleConnectionStatuses.None;
            return new SocialDiscoveryResult(
                candidate.ProfileId,
                candidate.UserId,
                candidate.ParticipantType,
                candidate.DisplayName,
                candidate.Headline,
                candidate.Location,
                candidate.Goals,
                candidate.Interests,
                candidate.CircleCodes,
                0,
                null,
                new SocialDiscoveryRelationship(
                    following.Contains(key),
                    followers.Contains(key),
                    connectionStatus,
                    connection?.Id,
                    viewer is not null
                        && candidate.ParticipantType == MessagingParticipantTypes.Client
                        && candidate.AllowsConnectionRequests
                        && connectionStatus == JourneyCircleConnectionStatuses.None,
                    key != actorKey));
        }).ToArray();
    }

    private static bool MatchesDirectorySearch(DirectoryCandidate candidate, string searchText)
    {
        var normalized = searchText.Trim().ToLowerInvariant();
        return new[]
        {
            candidate.DisplayName,
            candidate.Headline,
            candidate.Location,
            string.Join(" ", candidate.Goals),
            string.Join(" ", candidate.Interests),
            string.Join(" ", candidate.CircleCodes)
        }
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Any(value => value!.Contains(normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static int DirectoryMatchRank(DirectoryCandidate candidate, string? searchText) =>
        searchText is not null && candidate.DisplayName.StartsWith(searchText, StringComparison.OrdinalIgnoreCase)
            ? 0
            : searchText is not null && candidate.DisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                ? 1
                : 2;

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

        // Reachability is decided by the same active directory that search returns,
        // so a profile can never be opened by URL guessing.
        var candidate = (await ActiveDirectoryCandidatesAsync(actor, cancellationToken))
            .SingleOrDefault(entry => entry.ProfileId == clientProfileId);
        if (candidate is null)
        {
            return SocialOperationResult<SocialDiscoveryProfile>.Failure(
                "social_discovery_profile_unavailable",
                "This Legend member is not available from your Discover scope.");
        }

        var viewer = actor.Identity.ParticipantType == MessagingParticipantTypes.Client
            ? await FindClientProfileAsync(actor.Identity.UserId, cancellationToken)
            : null;
        var summary = (await ProjectDirectoryAsync(actor, viewer, [candidate], cancellationToken))[0];

        return SocialOperationResult<SocialDiscoveryProfile>.Success(new SocialDiscoveryProfile(
            summary,
            candidate.Introduction,
            candidate.LifeStages,
            candidate.ConnectionTypes));
    }

    public async Task<bool> IsDiscoverableByAsync(
        SocialFeedActor actor,
        string targetUserId,
        string targetParticipantType,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(targetUserId);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var targetIdentity = new DirectoryIdentity(normalized, targetParticipantType);
        return (await ActiveDirectoryCandidatesAsync(actor, cancellationToken))
            .Any(candidate => new DirectoryIdentity(candidate.UserId, candidate.ParticipantType) == targetIdentity);
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

    private static string CanonicalUserId(ClientProfile profile) =>
        Normalize(string.IsNullOrWhiteSpace(profile.ExternalIdentityObjectId)
            ? profile.ClientUserId
            : profile.ExternalIdentityObjectId);

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

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private readonly record struct DirectoryIdentity
    {
        public DirectoryIdentity(string? userId, string? participantType)
        {
            UserId = Normalize(userId);
            ParticipantType = participantType?.Trim() ?? string.Empty;
        }

        public string UserId { get; }
        public string ParticipantType { get; }
    }

    private sealed record ClientDirectoryRow(ClientProfile Client, JourneyCircleProfile? Journey);

    private sealed record AgentDirectoryRow(
        Guid Id,
        string AgentUserId,
        string? FullName,
        string? AgentUpn,
        string? Title,
        string? ShortBio);

    private sealed record DirectoryCandidate(
        Guid ProfileId,
        string UserId,
        string ParticipantType,
        string DisplayName,
        string? Headline,
        string? Location,
        IReadOnlyList<string> Goals,
        IReadOnlyList<string> Interests,
        IReadOnlyList<string> CircleCodes,
        bool AllowsConnectionRequests,
        string? Introduction,
        IReadOnlyList<string> LifeStages,
        IReadOnlyList<string> ConnectionTypes);

    private sealed class CommunityCandidate
    {
        public required JourneyCircleProfile Journey { get; init; }
        public required Guid ClientProfileId { get; init; }
        public required string ClientUserId { get; init; }
        public required string? ExternalIdentityObjectId { get; init; }
    }
}
