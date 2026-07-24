using System.Text.Json;
using Domain.Entities;
using Domain.JourneyCircles;
using Domain.Moderation;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.JourneyCircles;

internal sealed class JourneyCirclesService : IJourneyCirclesService
{
    private const string PolicyVersion = "2026.07";
    private readonly MasterAppDbContext _db;
    private readonly ICommunityTextModerationService _moderation;
    private readonly ILogger<JourneyCirclesService> _logger;

    public JourneyCirclesService(MasterAppDbContext db, ICommunityTextModerationService moderation, ILogger<JourneyCirclesService> logger)
    {
        _db = db;
        _moderation = moderation;
        _logger = logger;
    }

    public async Task<JourneyCircleDashboard> GetDashboardAsync(string clientUserId, CancellationToken cancellationToken = default)
    {
        var client = await FindClientAsync(clientUserId, cancellationToken);
        if (client is null)
            return EmptyDashboard();

        var profile = await _db.JourneyCircleProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.ClientProfileId == client.Id, cancellationToken);
        if (profile is null)
            return EmptyDashboard();

        var requests = await ConnectionSummariesAsync(client, JourneyCircleConnectionStatuses.Pending, true, cancellationToken);
        var connections = await ConnectionSummariesAsync(client, JourneyCircleConnectionStatuses.Accepted, false, cancellationToken);
        var recommendations = profile.IsOptedIn && profile.IsDiscoverable && profile.AllowSuggestions
            ? await RecommendationsAsync(client, profile, cancellationToken)
            : Array.Empty<JourneyCircleRecommendation>();
        return Dashboard(ToPublic(profile, client), Preferences(profile), recommendations, connections, requests);
    }

    public async Task<JourneyCircleOperationResult> SaveProfileAsync(string clientUserId, JourneyCircleProfileInput input, CancellationToken cancellationToken = default)
    {
        var client = await FindClientAsync(clientUserId, cancellationToken);
        if (client is null) return JourneyCircleOperationResult.Failure("JOURNEY_ACTOR_INVALID", "Journey Circles is not available for this account.");
        if (input.IsOptedIn && !input.ConsentAffirmed) return JourneyCircleOperationResult.Failure("JOURNEY_CONSENT_REQUIRED", "Please affirm consent before joining Journey Circles.");

        var moderation = _moderation.Evaluate(input.Introduction, "JourneyProfile");
        if (!moderation.IsAllowed)
        {
            AddModeration(client.ClientUserId, "JourneyProfile", moderation, null);
            await _db.SaveChangesAsync(cancellationToken);
            return JourneyCircleOperationResult.Failure("JOURNEY_CONTENT_BLOCKED", "This content cannot be saved because it violates Legend Legacy Protection’s respectful-communication policy. Please revise it and try again.");
        }

        var lifeStages = NormalizeControlled(input.LifeStages, JourneyCircleTaxonomy.LifeStages, 3);
        var locations = NormalizeControlled(input.Locations, JourneyCircleTaxonomy.Locations, 3);
        var goals = NormalizeControlled(input.Goals, JourneyCircleTaxonomy.Goals, 6);
        var interests = NormalizeControlled(input.Interests, JourneyCircleTaxonomy.Interests, 6);
        var circles = NormalizeControlled(input.CircleCodes, JourneyCircleTaxonomy.Circles, 4);
        var connectionTypes = NormalizeControlled(input.ConnectionTypes, JourneyCircleTaxonomy.ConnectionTypes, 4);
        var communicationStyles = NormalizeControlled(input.CommunicationStyles, JourneyCircleTaxonomy.CommunicationStyles, 3);
        var accountabilityFrequencies = NormalizeControlled(input.AccountabilityFrequencies, JourneyCircleTaxonomy.AccountabilityFrequencies, 3);
        if (lifeStages is null || locations is null || goals is null || interests is null || circles is null || connectionTypes is null || communicationStyles is null || accountabilityFrequencies is null)
            return JourneyCircleOperationResult.Failure("JOURNEY_TAXONOMY_INVALID", "Choose Journey Circles options from the available selections.");

        var now = DateTime.UtcNow;
        var profile = await _db.JourneyCircleProfiles.FirstOrDefaultAsync(x => x.ClientProfileId == client.Id, cancellationToken);
        if (profile is null)
        {
            profile = new JourneyCircleProfile { Id = Guid.NewGuid(), ClientProfileId = client.Id, CreatedUtc = now };
            _db.JourneyCircleProfiles.Add(profile);
        }

        profile.IsOptedIn = input.IsOptedIn;
        profile.IsDiscoverable = input.IsOptedIn && input.IsDiscoverable;
        profile.AllowSuggestions = input.IsOptedIn && input.AllowSuggestions;
        profile.AllowConnectionRequests = input.IsOptedIn && input.AllowConnectionRequests;
        profile.DisplayName = DisplayName(client);
        profile.LifeStage = ToDelimited(lifeStages, 80);
        profile.LocationLabel = ToDelimited(locations, 100);
        profile.Introduction = Limit(input.Introduction, 600);
        profile.GoalsJson = ToJson(goals);
        profile.InterestsJson = ToJson(interests);
        profile.CircleCodesJson = ToJson(circles);
        profile.ConnectionTypesJson = ToJson(connectionTypes);
        profile.CommunicationStyle = ToDelimited(communicationStyles, 80);
        profile.AccountabilityFrequency = ToDelimited(accountabilityFrequencies, 80);
        profile.CommunityAccessState = "Active";
        profile.ConsentAffirmedUtc = input.IsOptedIn ? now : profile.ConsentAffirmedUtc;
        profile.UpdatedUtc = now;
        AddAudit(client.ClientUserId, input.IsOptedIn ? "JourneyProfileSaved" : "JourneyOptedOut", null, null);
        await _db.SaveChangesAsync(cancellationToken);
        return JourneyCircleOperationResult.Success();
    }

    public async Task<JourneyCircleOperationResult> RequestConnectionAsync(string clientUserId, Guid targetClientProfileId, string? reason, string? introduction, CancellationToken cancellationToken = default)
    {
        var sender = await FindClientAsync(clientUserId, cancellationToken);
        if (sender is null || sender.Id == targetClientProfileId) return JourneyCircleOperationResult.Failure("JOURNEY_REQUEST_INVALID", "This connection request is not available.");
        var target = await _db.ClientProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == targetClientProfileId, cancellationToken);
        var senderProfile = await _db.JourneyCircleProfiles.FirstOrDefaultAsync(x => x.ClientProfileId == sender.Id, cancellationToken);
        var targetProfile = await _db.JourneyCircleProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.ClientProfileId == targetClientProfileId, cancellationToken);
        if (target is null || !Eligible(senderProfile) || targetProfile is null || !Eligible(targetProfile) || !targetProfile.AllowConnectionRequests || await IsBlockedAsync(sender.Id, targetClientProfileId, cancellationToken))
            return JourneyCircleOperationResult.Failure("JOURNEY_REQUEST_FORBIDDEN", "This connection request is not available.");
        var moderation = _moderation.Evaluate(reason, "JourneyConnectionRequest");
        if (moderation.IsAllowed)
            moderation = _moderation.Evaluate(introduction, "JourneyConnectionRequest");
        if (!moderation.IsAllowed) { AddModeration(sender.ClientUserId, "JourneyConnectionRequest", moderation, null); await _db.SaveChangesAsync(cancellationToken); return JourneyCircleOperationResult.Failure("JOURNEY_CONTENT_BLOCKED", "This content cannot be saved because it violates Legend Legacy Protection’s respectful-communication policy. Please revise it and try again."); }

        var key = PairKey(sender.Id, targetClientProfileId);
        var current = await _db.JourneyCircleConnections.FirstOrDefaultAsync(x => x.ConnectionKey == key, cancellationToken);
        if (current is not null && current.Status == JourneyCircleConnectionStatuses.Declined && current.UpdatedUtc > DateTime.UtcNow.AddDays(-30))
            return JourneyCircleOperationResult.Failure("JOURNEY_REQUEST_COOLDOWN", "This connection is not available right now.");
        if (current is not null && current.Status is JourneyCircleConnectionStatuses.Pending or JourneyCircleConnectionStatuses.Accepted)
            return JourneyCircleOperationResult.Failure("JOURNEY_REQUEST_EXISTS", "A connection request already exists.");
        if (await _db.JourneyCircleConnections.CountAsync(x => x.RequesterClientProfileId == sender.Id && x.CreatedUtc >= DateTime.UtcNow.AddDays(-1), cancellationToken) >= 10)
            return JourneyCircleOperationResult.Failure("JOURNEY_REQUEST_LIMITED", "Please wait before sending another connection request.");
        var now = DateTime.UtcNow;
        if (current is null) { current = new JourneyCircleConnection { Id = Guid.NewGuid(), ConnectionKey = key, CreatedUtc = now }; _db.JourneyCircleConnections.Add(current); }
        current.RequesterClientProfileId = sender.Id; current.RecipientClientProfileId = targetClientProfileId; current.Status = JourneyCircleConnectionStatuses.Pending;
        current.ConnectionReason = Limit(reason, 160); current.Introduction = Limit(introduction, 600); current.UpdatedUtc = now; current.RespondedUtc = null;
        AddAudit(sender.ClientUserId, "JourneyConnectionRequested", current.Id, targetClientProfileId); await _db.SaveChangesAsync(cancellationToken); return JourneyCircleOperationResult.Success();
    }

    public async Task<JourneyCircleOperationResult> RespondToConnectionAsync(string clientUserId, Guid connectionId, bool accept, CancellationToken cancellationToken = default)
    {
        var client = await FindClientAsync(clientUserId, cancellationToken);
        var connection = client is null ? null : await _db.JourneyCircleConnections.FirstOrDefaultAsync(x => x.Id == connectionId && x.RecipientClientProfileId == client.Id && x.Status == JourneyCircleConnectionStatuses.Pending, cancellationToken);
        if (connection is null) return JourneyCircleOperationResult.Failure("JOURNEY_REQUEST_NOT_FOUND", "This connection request is not available.");
        if (await IsBlockedAsync(connection.RequesterClientProfileId, client!.Id, cancellationToken)) return JourneyCircleOperationResult.Failure("JOURNEY_REQUEST_FORBIDDEN", "This connection request is not available.");
        connection.Status = accept ? JourneyCircleConnectionStatuses.Accepted : JourneyCircleConnectionStatuses.Declined;
        connection.UpdatedUtc = DateTime.UtcNow;
        connection.RespondedUtc = connection.UpdatedUtc;
        AddAudit(client.ClientUserId, accept ? "JourneyConnectionAccepted" : "JourneyConnectionDeclined", connection.Id, connection.RequesterClientProfileId); await _db.SaveChangesAsync(cancellationToken); return JourneyCircleOperationResult.Success();
    }

    public async Task<JourneyCircleOperationResult> DisconnectAsync(string clientUserId, Guid connectionId, CancellationToken cancellationToken = default)
    {
        var client = await FindClientAsync(clientUserId, cancellationToken);
        var connection = client is null ? null : await _db.JourneyCircleConnections.FirstOrDefaultAsync(x => x.Id == connectionId && (x.RequesterClientProfileId == client.Id || x.RecipientClientProfileId == client.Id) && x.Status == JourneyCircleConnectionStatuses.Accepted, cancellationToken);
        if (connection is null) return JourneyCircleOperationResult.Failure("JOURNEY_CONNECTION_NOT_FOUND", "This connection is not available.");
        connection.Status = JourneyCircleConnectionStatuses.Disconnected; connection.UpdatedUtc = DateTime.UtcNow; AddAudit(client!.ClientUserId, "JourneyConnectionDisconnected", connection.Id, null); await _db.SaveChangesAsync(cancellationToken); return JourneyCircleOperationResult.Success();
    }

    public async Task<JourneyCircleOperationResult> BlockAsync(string clientUserId, Guid targetClientProfileId, CancellationToken cancellationToken = default)
    {
        var client = await FindClientAsync(clientUserId, cancellationToken);
        if (client is null || client.Id == targetClientProfileId) return JourneyCircleOperationResult.Failure("JOURNEY_BLOCK_INVALID", "This community control is not available.");
        if (!await _db.JourneyCircleBlocks.AnyAsync(x => x.BlockerClientProfileId == client.Id && x.BlockedClientProfileId == targetClientProfileId, cancellationToken)) _db.JourneyCircleBlocks.Add(new JourneyCircleBlock { Id = Guid.NewGuid(), BlockerClientProfileId = client.Id, BlockedClientProfileId = targetClientProfileId, CreatedUtc = DateTime.UtcNow });
        var connection = await _db.JourneyCircleConnections.FirstOrDefaultAsync(x => x.ConnectionKey == PairKey(client.Id, targetClientProfileId), cancellationToken); if (connection is not null) { connection.Status = JourneyCircleConnectionStatuses.Blocked; connection.UpdatedUtc = DateTime.UtcNow; }
        AddAudit(client.ClientUserId, "JourneyClientBlocked", connection?.Id, targetClientProfileId); await _db.SaveChangesAsync(cancellationToken); return JourneyCircleOperationResult.Success();
    }

    public async Task<JourneyCircleOperationResult> ReportAsync(string clientUserId, Guid targetClientProfileId, string category, string? detail, CancellationToken cancellationToken = default)
    {
        var client = await FindClientAsync(clientUserId, cancellationToken); if (client is null || client.Id == targetClientProfileId || string.IsNullOrWhiteSpace(category)) return JourneyCircleOperationResult.Failure("JOURNEY_REPORT_INVALID", "This report is not available.");
        _db.JourneyCircleReports.Add(new JourneyCircleReport { Id = Guid.NewGuid(), ReporterClientProfileId = client.Id, ReportedClientProfileId = targetClientProfileId, Category = Limit(category, 80)!, Detail = Limit(detail, 600), CreatedUtc = DateTime.UtcNow }); AddAudit(client.ClientUserId, "JourneyClientReported", null, targetClientProfileId); await _db.SaveChangesAsync(cancellationToken); return JourneyCircleOperationResult.Success();
    }

    public async Task<bool> CanMessageAsync(string firstClientUserId, string secondClientUserId, CancellationToken cancellationToken = default)
    {
        var ids = await _db.ClientProfiles.AsNoTracking().Where(x => x.ClientUserId.ToLower() == firstClientUserId.ToLower() || x.ClientUserId.ToLower() == secondClientUserId.ToLower() || (x.ExternalIdentityObjectId != null && (x.ExternalIdentityObjectId.ToLower() == firstClientUserId.ToLower() || x.ExternalIdentityObjectId.ToLower() == secondClientUserId.ToLower()))).Select(x => x.Id).Distinct().ToListAsync(cancellationToken);
        if (ids.Count != 2 || await IsBlockedAsync(ids[0], ids[1], cancellationToken)) return false;
        if (await _db.JourneyCircleProfiles.AsNoTracking().CountAsync(x => ids.Contains(x.ClientProfileId) && x.IsOptedIn && x.CommunityAccessState == "Active", cancellationToken) != 2) return false;
        return await _db.JourneyCircleConnections.AsNoTracking().AnyAsync(x => x.ConnectionKey == PairKey(ids[0], ids[1]) && x.Status == JourneyCircleConnectionStatuses.Accepted, cancellationToken);
    }

    public async Task<IReadOnlyList<(string UserId, string DisplayName)>> ListConnectedPeersAsync(string clientUserId, CancellationToken cancellationToken = default)
    {
        var client = await FindClientAsync(clientUserId, cancellationToken); if (client is null) return Array.Empty<(string, string)>();
        var connections = await _db.JourneyCircleConnections.AsNoTracking().Where(x => x.Status == JourneyCircleConnectionStatuses.Accepted && (x.RequesterClientProfileId == client.Id || x.RecipientClientProfileId == client.Id)).ToListAsync(cancellationToken);
        var peerIds = connections
            .Select(x => x.RequesterClientProfileId == client.Id ? x.RecipientClientProfileId : x.RequesterClientProfileId)
            .Distinct()
            .ToArray();
        var profiles = await _db.JourneyCircleProfiles.AsNoTracking().Include(x => x.ClientProfile).Where(x => peerIds.Contains(x.ClientProfileId) && x.IsOptedIn && x.CommunityAccessState == "Active").ToListAsync(cancellationToken);
        var blockedPeerIds = await _db.JourneyCircleBlocks.AsNoTracking()
            .Where(x => x.BlockerClientProfileId == client.Id || x.BlockedClientProfileId == client.Id)
            .Select(x => x.BlockerClientProfileId == client.Id ? x.BlockedClientProfileId : x.BlockerClientProfileId)
            .ToListAsync(cancellationToken);
        return profiles
            .Where(x => !blockedPeerIds.Contains(x.ClientProfileId) &&
                        !string.IsNullOrWhiteSpace(x.ClientProfile.ClientUserId))
            .GroupBy(x => x.ClientProfile.ClientUserId, StringComparer.OrdinalIgnoreCase)
            .Select(group => (group.First().ClientProfile.ClientUserId, SafeDisplayName(group.First(), group.First().ClientProfile)))
            .ToArray();
    }

    private async Task<IReadOnlyList<JourneyCircleRecommendation>> RecommendationsAsync(ClientProfile client, JourneyCircleProfile source, CancellationToken ct)
    {
        var candidates = await _db.JourneyCircleProfiles.AsNoTracking().Include(x => x.ClientProfile).Where(x => x.ClientProfileId != client.Id && x.IsOptedIn && x.IsDiscoverable && x.AllowSuggestions && x.CommunityAccessState == "Active").Take(200).ToListAsync(ct);
        var sourceGoals = FromJson(source.GoalsJson);
        var sourceInterests = FromJson(source.InterestsJson);
        var sourceCircles = FromJson(source.CircleCodesJson);
        var sourceStages = FromDelimited(source.LifeStage);
        var sourceLocations = FromDelimited(source.LocationLabel);
        var sourceConnectionTypes = FromJson(source.ConnectionTypesJson);
        var sourceCommunicationStyles = FromDelimited(source.CommunicationStyle);
        var sourceFrequencies = FromDelimited(source.AccountabilityFrequency);
        var rows = new List<(JourneyCircleProfile Profile, int Score, string Explanation)>();
        foreach (var candidate in candidates)
        {
            if (await IsBlockedAsync(client.Id, candidate.ClientProfileId, ct)) continue;
            var link = await _db.JourneyCircleConnections.AsNoTracking().FirstOrDefaultAsync(x => x.ConnectionKey == PairKey(client.Id, candidate.ClientProfileId), ct);
            if (link is not null && link.Status is JourneyCircleConnectionStatuses.Accepted or JourneyCircleConnectionStatuses.Pending or JourneyCircleConnectionStatuses.Declined) continue;
            var sharedGoals = sourceGoals.Intersect(FromJson(candidate.GoalsJson), StringComparer.OrdinalIgnoreCase).ToArray();
            var sharedInterests = sourceInterests.Intersect(FromJson(candidate.InterestsJson), StringComparer.OrdinalIgnoreCase).ToArray();
            var sharedCircles = sourceCircles.Intersect(FromJson(candidate.CircleCodesJson), StringComparer.OrdinalIgnoreCase).ToArray();
            var sharedStages = sourceStages.Intersect(FromDelimited(candidate.LifeStage), StringComparer.OrdinalIgnoreCase).ToArray();
            var sharedLocations = sourceLocations.Intersect(FromDelimited(candidate.LocationLabel), StringComparer.OrdinalIgnoreCase).ToArray();
            var sharedConnectionTypes = sourceConnectionTypes.Intersect(FromJson(candidate.ConnectionTypesJson), StringComparer.OrdinalIgnoreCase).ToArray();
            var sharedCommunicationStyles = sourceCommunicationStyles.Intersect(FromDelimited(candidate.CommunicationStyle), StringComparer.OrdinalIgnoreCase).ToArray();
            var sharedFrequencies = sourceFrequencies.Intersect(FromDelimited(candidate.AccountabilityFrequency), StringComparer.OrdinalIgnoreCase).ToArray();
            var score = sharedGoals.Length * 6 + sharedCircles.Length * 4 + sharedInterests.Length * 3 + sharedStages.Length * 3 + sharedConnectionTypes.Length * 2 + sharedCommunicationStyles.Length * 2 + sharedLocations.Length + sharedFrequencies.Length;
            if (score == 0) continue;
            var explanation = sharedGoals.FirstOrDefault() is { } goal ? $"You both selected {goal}."
                : sharedCircles.FirstOrDefault() is { } circle ? $"You are both in {circle}."
                : sharedStages.FirstOrDefault() is { } stage ? $"You share the {stage} life stage."
                : sharedConnectionTypes.FirstOrDefault() is { } connectionType ? $"You are both looking for a {connectionType.ToLowerInvariant()}."
                : $"You share an interest in {sharedInterests.FirstOrDefault() ?? sharedCommunicationStyles.FirstOrDefault() ?? sharedLocations.FirstOrDefault() ?? sharedFrequencies.FirstOrDefault()}.";
            rows.Add((candidate, score, explanation));
        }
        return rows.OrderByDescending(x => x.Score).ThenBy(x => x.Profile.UpdatedUtc).Take(12).Select(x => new JourneyCircleRecommendation(ToPublic(x.Profile, x.Profile.ClientProfile), x.Explanation)).ToArray();
    }

    private async Task<IReadOnlyList<JourneyCircleConnectionSummary>> ConnectionSummariesAsync(ClientProfile client, string status, bool recipientOnly, CancellationToken ct)
    {
        var connections = await _db.JourneyCircleConnections.AsNoTracking().Where(x => x.Status == status && (recipientOnly ? x.RecipientClientProfileId == client.Id : (x.RequesterClientProfileId == client.Id || x.RecipientClientProfileId == client.Id))).ToListAsync(ct);
        var ids = connections.Select(x => x.RequesterClientProfileId == client.Id ? x.RecipientClientProfileId : x.RequesterClientProfileId).ToArray(); var profiles = await _db.JourneyCircleProfiles.AsNoTracking().Include(x => x.ClientProfile).Where(x => ids.Contains(x.ClientProfileId)).ToDictionaryAsync(x => x.ClientProfileId, ct);
        return connections.Where(x => profiles.ContainsKey(x.RequesterClientProfileId == client.Id ? x.RecipientClientProfileId : x.RequesterClientProfileId)).Select(x => { var id = x.RequesterClientProfileId == client.Id ? x.RecipientClientProfileId : x.RequesterClientProfileId; return new JourneyCircleConnectionSummary(x.Id, ToPublic(profiles[id], profiles[id].ClientProfile), x.Status, x.ConnectionReason, x.Introduction, x.CreatedUtc); }).ToArray();
    }

    private async Task<ClientProfile?> FindClientAsync(string userId, CancellationToken ct) => await _db.ClientProfiles.FirstOrDefaultAsync(x => x.ClientUserId.ToLower() == userId.ToLower() || (x.ExternalIdentityObjectId != null && x.ExternalIdentityObjectId.ToLower() == userId.ToLower()), ct);
    private Task<bool> IsBlockedAsync(Guid first, Guid second, CancellationToken ct) => _db.JourneyCircleBlocks.AsNoTracking().AnyAsync(x => (x.BlockerClientProfileId == first && x.BlockedClientProfileId == second) || (x.BlockerClientProfileId == second && x.BlockedClientProfileId == first), ct);
    private static bool Eligible(JourneyCircleProfile? p) => p is { IsOptedIn: true, IsDiscoverable: true, CommunityAccessState: "Active" };
    private static string PairKey(Guid first, Guid second) => string.CompareOrdinal(first.ToString("N"), second.ToString("N")) < 0 ? $"{first:N}|{second:N}" : $"{second:N}|{first:N}";
    private static JourneyCircleDashboard EmptyDashboard() => Dashboard(null, null, Array.Empty<JourneyCircleRecommendation>(), Array.Empty<JourneyCircleConnectionSummary>(), Array.Empty<JourneyCircleConnectionSummary>());
    private static JourneyCircleDashboard Dashboard(JourneyCirclePublicProfile? profile, JourneyCircleProfilePreferences? preferences, IReadOnlyList<JourneyCircleRecommendation> recommendations, IReadOnlyList<JourneyCircleConnectionSummary> connections, IReadOnlyList<JourneyCircleConnectionSummary> requests) => new(
        profile, preferences, recommendations, connections, requests,
        JourneyCircleTaxonomy.Goals.OrderBy(x => x).ToArray(), JourneyCircleTaxonomy.Circles.OrderBy(x => x).ToArray(),
        JourneyCircleTaxonomy.LifeStages.OrderBy(x => x).ToArray(), JourneyCircleTaxonomy.Locations.OrderBy(x => x).ToArray(),
        JourneyCircleTaxonomy.Interests.OrderBy(x => x).ToArray(), JourneyCircleTaxonomy.ConnectionTypes.OrderBy(x => x).ToArray(),
        JourneyCircleTaxonomy.CommunicationStyles.OrderBy(x => x).ToArray(), JourneyCircleTaxonomy.AccountabilityFrequencies.OrderBy(x => x).ToArray());
    private static JourneyCirclePublicProfile ToPublic(JourneyCircleProfile p, ClientProfile c) => new(
        p.ClientProfileId, SafeDisplayName(p, c), p.Introduction, FromDelimited(p.LifeStage), FromDelimited(p.LocationLabel),
        FromJson(p.GoalsJson), FromJson(p.InterestsJson), FromJson(p.CircleCodesJson), FromJson(p.ConnectionTypesJson),
        FromDelimited(p.CommunicationStyle), FromDelimited(p.AccountabilityFrequency), $"/JourneyCircles/Profiles/{p.ClientProfileId}/Avatar");
    private static JourneyCircleProfilePreferences Preferences(JourneyCircleProfile profile) => new(
        profile.ConsentAffirmedUtc is not null, profile.IsOptedIn, profile.IsDiscoverable, profile.AllowSuggestions, profile.AllowConnectionRequests);
    private static string SafeDisplayName(JourneyCircleProfile profile, ClientProfile client) => string.IsNullOrWhiteSpace(profile.DisplayName) ? DisplayName(client) : profile.DisplayName;
    private static string DisplayName(ClientProfile p) => string.Join(' ', new[] { p.FirstName, p.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim() is { Length: > 0 } name ? name : "Journey member";
    private static IReadOnlyList<string>? NormalizeControlled(IReadOnlyList<string>? values, IReadOnlySet<string> allowed, int maximum)
    {
        var normalized = NormalizeOpen(values, maximum, 120);
        return normalized.All(allowed.Contains) ? normalized : null;
    }
    private static IReadOnlyList<string> NormalizeOpen(IReadOnlyList<string>? values, int maximum, int length) => (values ?? Array.Empty<string>()).SelectMany(x => (x ?? string.Empty).Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries)).Select(x => Limit(x, length)).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).Take(maximum).ToArray();
    private static string ToJson(IReadOnlyList<string> values) => JsonSerializer.Serialize(values);
    private static IReadOnlyList<string> FromJson(string? json) { try { return JsonSerializer.Deserialize<string[]>(json ?? "[]")?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToArray() ?? Array.Empty<string>(); } catch { return Array.Empty<string>(); } }
    private static string? ToDelimited(IReadOnlyList<string> values, int maximum)
    {
        var value = string.Join('|', values);
        return value.Length <= maximum ? value : null;
    }
    private static IReadOnlyList<string> FromDelimited(string? value) => string.IsNullOrWhiteSpace(value)
        ? Array.Empty<string>()
        : value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static string? Limit(string? value, int maximum) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= maximum ? value.Trim() : value.Trim()[..maximum];
    private void AddAudit(string actor, string action, Guid? connectionId, Guid? target) => _db.JourneyCircleModerationEvents.Add(new JourneyCircleModerationEvent { Id = Guid.NewGuid(), ActorUserId = actor, Surface = "JourneyCircles", Category = "Audit", Severity = "Info", Action = action, PolicyVersion = PolicyVersion, ConnectionId = connectionId, RequiresReview = false, CreatedUtc = DateTime.UtcNow });
    private void AddModeration(string actor, string surface, CommunityTextModerationResult result, Guid? connectionId) => _db.JourneyCircleModerationEvents.Add(new JourneyCircleModerationEvent { Id = Guid.NewGuid(), ActorUserId = actor, Surface = surface, Category = result.Category ?? "Policy", Severity = result.Severity ?? "Medium", Action = "Blocked", PolicyVersion = PolicyVersion, ConnectionId = connectionId, RequiresReview = result.RequiresReview, CreatedUtc = DateTime.UtcNow });
}
