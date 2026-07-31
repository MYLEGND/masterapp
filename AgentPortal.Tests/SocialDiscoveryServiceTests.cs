using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Billing;
using Domain.Entities;
using Domain.JourneyCircles;
using Domain.Messaging;
using Domain.Social;
using Infrastructure.Data;
using Infrastructure.Social;
using Xunit;

namespace AgentPortal.Tests;

public sealed class SocialDiscoveryServiceTests
{
    [Fact]
    public async Task CommunitySearch_ReturnsEveryConsentedMember_EvenWithNoCompatibility()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var viewer = Member(db, "viewer", "Vera", goals: ["Retire early"]);

        // Deliberately shares nothing with the viewer. The Journey Circles suggestion
        // feed would score this member below its bar and drop them; Discover must not.
        var stranger = Member(db, "stranger", "Sam", goals: ["Buy a boat"], interests: ["Sailing"]);
        var match = Member(db, "match", "Mia", goals: ["Retire early"]);
        await db.SaveChangesAsync();

        var page = await new SocialDiscoveryService(db).SearchAsync(
            new SocialDiscoveryQuery(Actor(viewer), null, 0, 20));

        Assert.True(page.Succeeded);
        var ids = page.Value!.Results.Select(result => result.ClientProfileId).ToArray();
        Assert.Contains(match.Id, ids);
        Assert.Contains(stranger.Id, ids);
        Assert.DoesNotContain(viewer.Id, ids);

        // Ranking still applies: the compatible member is ordered ahead of the stranger.
        Assert.True(Array.IndexOf(ids, match.Id) < Array.IndexOf(ids, stranger.Id));
        Assert.True(
            page.Value.Results.Single(r => r.ClientProfileId == match.Id).CompatibilityScore >
            page.Value.Results.Single(r => r.ClientProfileId == stranger.Id).CompatibilityScore);
    }

    [Fact]
    public async Task CommunitySearch_ExcludesMembersWhoDidNotConsentOrOptedOut()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var viewer = Member(db, "viewer", "Vera");

        var notDiscoverable = Member(db, "hidden", "Hank");
        notDiscoverable.Journey.IsDiscoverable = false;

        var notOptedIn = Member(db, "opted-out", "Olive");
        notOptedIn.Journey.IsOptedIn = false;

        var neverConsented = Member(db, "no-consent", "Noah");
        neverConsented.Journey.ConsentAffirmedUtc = null;

        var suspended = Member(db, "suspended", "Sara");
        suspended.Journey.CommunityAccessState = "Suspended";

        var visible = Member(db, "visible", "Vince");
        await db.SaveChangesAsync();

        var page = await new SocialDiscoveryService(db).SearchAsync(
            new SocialDiscoveryQuery(Actor(viewer), null, 0, 20));

        Assert.True(page.Succeeded);
        var only = Assert.Single(page.Value!.Results);
        Assert.Equal(visible.Id, only.ClientProfileId);
    }

    [Fact]
    public async Task CommunitySearch_ExcludesLapsedAndBlockedMembers()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var viewer = Member(db, "viewer", "Vera");

        var lapsed = Member(db, "lapsed", "Lena", entitlement: ClientEntitlementStatus.Revoked);
        var grace = Member(db, "grace", "Gus", entitlement: ClientEntitlementStatus.GracePeriod);
        var blocked = Member(db, "blocked", "Bella");
        var visible = Member(db, "visible", "Vince");

        db.JourneyCircleBlocks.Add(new JourneyCircleBlock
        {
            Id = Guid.NewGuid(),
            BlockerClientProfileId = viewer.Id,
            BlockedClientProfileId = blocked.Id,
            CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var page = await new SocialDiscoveryService(db).SearchAsync(
            new SocialDiscoveryQuery(Actor(viewer), null, 0, 20));

        Assert.True(page.Succeeded);
        var ids = page.Value!.Results.Select(result => result.ClientProfileId).ToArray();
        Assert.DoesNotContain(lapsed.Id, ids);
        Assert.DoesNotContain(blocked.Id, ids);
        // A grace-period subscription still has ClientApp access, so they stay listed.
        Assert.Contains(grace.Id, ids);
        Assert.Contains(visible.Id, ids);
    }

    [Fact]
    public async Task CommunitySearch_MatchesTextAndPagesDeterministically()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var viewer = Member(db, "viewer", "Vera");

        for (var index = 0; index < 7; index++)
            Member(db, $"runner-{index}", $"Runner {index:D2}", interests: ["Marathon"]);

        Member(db, "other", "Quiet Quinn", interests: ["Chess"]);
        await db.SaveChangesAsync();

        var service = new SocialDiscoveryService(db);

        var first = await service.SearchAsync(
            new SocialDiscoveryQuery(Actor(viewer), "Runner", 0, 3));
        var second = await service.SearchAsync(
            new SocialDiscoveryQuery(Actor(viewer), "Runner", 3, 3));

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(7, first.Value!.TotalCount);
        Assert.Equal(3, first.Value.Results.Count);
        Assert.True(first.Value.HasMore);
        Assert.Equal(SocialDiscoverySortModes.Relevance, first.Value.SortMode);

        // Pages must not overlap or skip.
        var firstIds = first.Value.Results.Select(r => r.ClientProfileId).ToArray();
        var secondIds = second.Value!.Results.Select(r => r.ClientProfileId).ToArray();
        Assert.Empty(firstIds.Intersect(secondIds));

        // Interest text is searchable too.
        var byInterest = await service.SearchAsync(
            new SocialDiscoveryQuery(Actor(viewer), "Chess", 0, 20));
        Assert.True(byInterest.Succeeded);
        Assert.Single(byInterest.Value!.Results);

        // Search must be case-insensitive regardless of the database provider.
        var lowercased = await service.SearchAsync(
            new SocialDiscoveryQuery(Actor(viewer), "chess", 0, 20));
        Assert.True(lowercased.Succeeded);
        Assert.Single(lowercased.Value!.Results);

        var uppercased = await service.SearchAsync(
            new SocialDiscoveryQuery(Actor(viewer), "RUNNER", 0, 20));
        Assert.True(uppercased.Succeeded);
        Assert.Equal(7, uppercased.Value!.TotalCount);
    }

    [Fact]
    public async Task CommunitySearch_NeverMatchesLegalNameOrEmail()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var viewer = Member(db, "viewer", "Vera");

        // Community-facing display name differs from the legal name on the CRM record.
        var member = Member(db, "private-person", "Coach K");
        member.Client.FirstName = "Bartholomew";
        member.Client.LastName = "Quimby";
        member.Client.Email = "bartholomew.quimby@example.test";
        await db.SaveChangesAsync();

        var service = new SocialDiscoveryService(db);

        Assert.Empty((await service.SearchAsync(
            new SocialDiscoveryQuery(Actor(viewer), "Bartholomew", 0, 20))).Value!.Results);
        Assert.Empty((await service.SearchAsync(
            new SocialDiscoveryQuery(Actor(viewer), "quimby@example", 0, 20))).Value!.Results);
        Assert.Single((await service.SearchAsync(
            new SocialDiscoveryQuery(Actor(viewer), "Coach", 0, 20))).Value!.Results);
    }

    [Fact]
    public async Task AgentScope_ReturnsOwnedClientsAndActivePeerAgents()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var agent = new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "agent-one",
            AgentUpn = "agent.one@example.test",
            FullName = "Agent One",
            IsActive = true
        };
        db.AgentProfiles.Add(agent);
        var peerAgent = new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "agent-peer",
            AgentUpn = "peer@example.test",
            FullName = "Peer Agent",
            IsActive = true
        };
        db.AgentProfiles.Add(peerAgent);

        var ownedClient = Member(db, "owned", "Owned Olivia");
        var someoneElsesClient = Member(db, "stranger", "Stranger Steve");

        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = agent.AgentUserId,
            AgentUpn = agent.AgentUpn,
            ClientUserId = ownedClient.Client.ClientUserId
        });
        await db.SaveChangesAsync();

        var agentActor = new SocialFeedActor(
            new MessagingActor(agent.AgentUserId, MessagingParticipantTypes.Agent),
            agent.Id,
            "Agent One");

        var page = await new SocialDiscoveryService(db).SearchAsync(
            new SocialDiscoveryQuery(agentActor, null, 0, 20));

        Assert.True(page.Succeeded);
        Assert.Equal(SocialDiscoveryScopes.OwnedClients, page.Value!.Scope);
        var ownedResult = Assert.Single(page.Value.Results, result => result.ClientProfileId == ownedClient.Id);
        Assert.Equal(MessagingParticipantTypes.Client, ownedResult.ParticipantType);
        var peerResult = Assert.Single(page.Value.Results, result => result.ClientProfileId == peerAgent.Id);
        Assert.Equal(MessagingParticipantTypes.Agent, peerResult.ParticipantType);
        Assert.True(peerResult.Relationship.CanFollow);

        // The community member who is not this agent's client stays invisible: they
        // consented to peer discovery, not to agent discovery.
        Assert.DoesNotContain(page.Value.Results, r => r.ClientProfileId == someoneElsesClient.Id);
    }

    [Fact]
    public async Task DirectorySearch_IncludesActiveMobileProfilesWithoutJourneyRecommendations()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var viewer = Member(db, "viewer", "Vera");
        var directoryOnly = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "directory-only",
            ExternalIdentityObjectId = "directory-only",
            FirstName = "Casey",
            LastName = "Directory",
            Email = "casey@example.test",
            CrmStatus = "Active",
            CrmNotes = "{\"recordType\":\"Client\",\"pipelineStage\":\"Client\"}"
        };
        db.ClientProfiles.Add(directoryOnly);
        db.ClientEntitlements.Add(new ClientEntitlement
        {
            Id = Guid.NewGuid(),
            ClientProfileId = directoryOnly.Id,
            EntitlementKey = BillingEntitlementKeys.ClientAppFullAccess,
            Status = ClientEntitlementStatus.Active,
            SourceType = ClientEntitlementSourceType.Subscription,
            SourceId = Guid.NewGuid().ToString(),
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        var agent = new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "directory-agent",
            AgentUpn = "directory-agent@example.test",
            FullName = "Directory Agent",
            IsActive = true
        };
        db.AgentProfiles.Add(agent);
        db.MobileProfileSettings.Add(new MobileProfileSettings
        {
            Id = Guid.NewGuid(),
            ProfileId = agent.Id,
            ParticipantType = MessagingParticipantTypes.Agent,
            Username = "directory.legend",
            NormalizedUsername = "directory.legend",
            Bio = "Serving the Legend community.",
            Website = "https://legend.example/directory",
            PublicEmail = "directory@example.test",
            IsEmailVisible = true,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var discovery = new SocialDiscoveryService(db);
        var page = await discovery.SearchAsync(new SocialDiscoveryQuery(
            Actor(viewer), null, 0, 20, SocialDiscoverySortModes.Directory));

        Assert.True(page.Succeeded);
        Assert.Contains(page.Value!.Results, result =>
            result.ClientProfileId == directoryOnly.Id &&
            result.ParticipantType == MessagingParticipantTypes.Client);
        Assert.Contains(page.Value.Results, result =>
            result.ClientProfileId == agent.Id &&
            result.ParticipantType == MessagingParticipantTypes.Agent);
        Assert.True(await discovery.IsDiscoverableByAsync(
            Actor(viewer), agent.AgentUserId, MessagingParticipantTypes.Agent));

        var usernameSearch = await discovery.SearchAsync(new SocialDiscoveryQuery(
            Actor(viewer), "@directory.legend", 0, 20));
        var found = Assert.Single(usernameSearch.Value!.Results, result => result.ClientProfileId == agent.Id);
        Assert.Equal("directory.legend", found.Username);
        Assert.Equal("Serving the Legend community.", found.Bio);
        Assert.Equal("https://legend.example/directory", found.Website);
        Assert.Equal("directory@example.test", found.PublicEmail);
    }

    [Fact]
    public async Task ProfileOpen_AllowsAnActiveDirectoryMemberEvenWhenTheyAreNotRecommended()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var viewer = Member(db, "viewer", "Vera");
        var hidden = Member(db, "hidden", "Hank");
        hidden.Journey.IsDiscoverable = false;
        var visible = Member(db, "visible", "Vince");
        await db.SaveChangesAsync();

        var service = new SocialDiscoveryService(db);

        var directoryMember = await service.GetProfileAsync(Actor(viewer), hidden.Id);
        Assert.True(directoryMember.Succeeded);
        Assert.Equal("Hank", directoryMember.Value!.Summary.DisplayName);

        var allowed = await service.GetProfileAsync(Actor(viewer), visible.Id);
        Assert.True(allowed.Succeeded);
        Assert.Equal("Vince", allowed.Value!.Summary.DisplayName);
    }

    [Fact]
    public async Task DiscoveredMembers_AreFollowableAndRelationshipStateRoundTrips()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var viewer = Member(db, "viewer", "Vera");
        var target = Member(db, "target", "Tina");
        await db.SaveChangesAsync();

        var discovery = new SocialDiscoveryService(db);

        // A discovered member is reachable for follow even though they are not a
        // messaging recipient of the viewer.
        Assert.True(await discovery.IsDiscoverableByAsync(
            Actor(viewer), CanonicalUserId(target.Client), MessagingParticipantTypes.Client));

        db.SocialFollows.Add(new SocialFollow
        {
            Id = Guid.NewGuid(),
            FollowerUserId = CanonicalUserId(viewer.Client),
            FollowerParticipantType = MessagingParticipantTypes.Client,
            FollowedUserId = CanonicalUserId(target.Client),
            FollowedParticipantType = MessagingParticipantTypes.Client,
            CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var page = await discovery.SearchAsync(
            new SocialDiscoveryQuery(Actor(viewer), null, 0, 20));

        Assert.True(page.Succeeded);
        var result = Assert.Single(page.Value!.Results, r => r.ClientProfileId == target.Id);
        Assert.True(result.Relationship.FollowedByCurrentActor);
        Assert.False(result.Relationship.FollowsCurrentActor);
        Assert.Equal(JourneyCircleConnectionStatuses.None, result.Relationship.ConnectionStatus);
        Assert.True(result.Relationship.CanRequestConnection);
    }

    [Fact]
    public async Task ExistingConnections_StayVisibleInSearchResults()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var viewer = Member(db, "viewer", "Vera");
        var connected = Member(db, "connected", "Connie");

        db.JourneyCircleConnections.Add(new JourneyCircleConnection
        {
            Id = Guid.NewGuid(),
            ConnectionKey = "viewer:connected",
            RequesterClientProfileId = viewer.Id,
            RecipientClientProfileId = connected.Id,
            Status = JourneyCircleConnectionStatuses.Accepted,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var page = await new SocialDiscoveryService(db).SearchAsync(
            new SocialDiscoveryQuery(Actor(viewer), null, 0, 20));

        Assert.True(page.Succeeded);

        // The suggestion feed hides people you are already connected to. Search must
        // still find them, with their connection state reported.
        var result = Assert.Single(page.Value!.Results, r => r.ClientProfileId == connected.Id);
        Assert.Equal(JourneyCircleConnectionStatuses.Accepted, result.Relationship.ConnectionStatus);
        Assert.False(result.Relationship.CanRequestConnection);
    }

    // ------------------------------------------------------------------ helpers

    private sealed record TestMember(ClientProfile Client, JourneyCircleProfile Journey)
    {
        public Guid Id => Client.Id;
    }

    private static TestMember Member(
        MasterAppDbContext db,
        string userId,
        string displayName,
        string[]? goals = null,
        string[]? interests = null,
        ClientEntitlementStatus entitlement = ClientEntitlementStatus.Active)
    {
        var client = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = userId,
            ExternalIdentityObjectId = userId,
            FirstName = displayName.Split(' ')[0],
            LastName = "Member",
            Email = $"{userId}@example.test",
            CrmNotes = "{\"recordType\":\"Client\",\"pipelineStage\":\"Client\"}"
        };

        var journey = new JourneyCircleProfile
        {
            Id = Guid.NewGuid(),
            ClientProfileId = client.Id,
            IsOptedIn = true,
            IsDiscoverable = true,
            AllowSuggestions = true,
            AllowConnectionRequests = true,
            DisplayName = displayName,
            GoalsJson = System.Text.Json.JsonSerializer.Serialize(goals ?? []),
            InterestsJson = System.Text.Json.JsonSerializer.Serialize(interests ?? []),
            CircleCodesJson = "[]",
            ConnectionTypesJson = "[]",
            CommunityAccessState = "Active",
            ConsentAffirmedUtc = DateTime.UtcNow,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        db.ClientProfiles.Add(client);
        db.JourneyCircleProfiles.Add(journey);
        db.ClientEntitlements.Add(new ClientEntitlement
        {
            Id = Guid.NewGuid(),
            ClientProfileId = client.Id,
            EntitlementKey = BillingEntitlementKeys.ClientAppFullAccess,
            Status = entitlement,
            SourceType = ClientEntitlementSourceType.Subscription,
            SourceId = Guid.NewGuid().ToString(),
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });

        return new TestMember(client, journey);
    }

    private static SocialFeedActor Actor(TestMember member) => new(
        new MessagingActor(
            member.Client.ExternalIdentityObjectId ?? member.Client.ClientUserId,
            MessagingParticipantTypes.Client),
        member.Id,
        member.Journey.DisplayName);

    private static string CanonicalUserId(ClientProfile client) =>
        (string.IsNullOrWhiteSpace(client.ExternalIdentityObjectId)
            ? client.ClientUserId
            : client.ExternalIdentityObjectId).ToLowerInvariant();
}
