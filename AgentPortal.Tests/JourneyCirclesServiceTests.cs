using System;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.JourneyCircles;
using Infrastructure.JourneyCircles;
using Infrastructure.Moderation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class JourneyCirclesServiceTests
{
    [Fact]
    public async Task PeerMessaging_RequiresMutualAcceptance_AndBlockRevokesItImmediately()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var first = new ClientProfile { Id = Guid.NewGuid(), ClientUserId = "client-one", FirstName = "One", LastName = "Client", Email = "one@example.test" };
        var second = new ClientProfile { Id = Guid.NewGuid(), ClientUserId = "client-two", FirstName = "Two", LastName = "Client", Email = "two@example.test" };
        db.ClientProfiles.AddRange(first, second);
        db.JourneyCircleProfiles.AddRange(
            new JourneyCircleProfile
            {
                Id = Guid.NewGuid(),
                ClientProfileId = first.Id,
                ConsentAffirmedUtc = DateTime.UtcNow,
                IsOptedIn = true,
                IsDiscoverable = true,
                AllowSuggestions = true,
                AllowConnectionRequests = true,
                DisplayName = "One",
                CommunityAccessState = "Active"
            },
            new JourneyCircleProfile
            {
                Id = Guid.NewGuid(),
                ClientProfileId = second.Id,
                ConsentAffirmedUtc = DateTime.UtcNow,
                IsOptedIn = true,
                IsDiscoverable = true,
                AllowSuggestions = true,
                AllowConnectionRequests = true,
                DisplayName = "Two",
                CommunityAccessState = "Active"
            });
        await db.SaveChangesAsync();
        var moderation = new CommunityTextModerationService(new ConfigurationBuilder().Build());
        var service = new JourneyCirclesService(db, moderation, NullLogger<JourneyCirclesService>.Instance);

        Assert.False(await service.CanMessageAsync("client-one", "client-two"));
        Assert.True((await service.RequestConnectionAsync("client-one", second.Id, "Shared goals", "Hello there")).Succeeded);
        var request = Assert.Single(db.JourneyCircleConnections);
        Assert.True((await service.RespondToConnectionAsync("client-two", request.Id, true)).Succeeded);
        Assert.True(await service.CanMessageAsync("client-one", "client-two"));
        Assert.True((await service.BlockAsync("client-one", second.Id)).Succeeded);
        Assert.False(await service.CanMessageAsync("client-one", "client-two"));
        Assert.Empty(await service.ListConnectedPeersAsync("client-one"));
    }

    [Theory]
    [InlineData("This is f.u.c.k")]
    [InlineData("This is f  u  c  k")]
    public void Moderation_BlocksCommonDisguisedProfanity(string content)
    {
        var moderation = new CommunityTextModerationService(new ConfigurationBuilder().Build());
        var result = moderation.Evaluate(content, "MessagingMessage");
        Assert.False(result.IsAllowed);
        Assert.Equal("Profanity", result.Category);
    }

    [Fact]
    public void Moderation_DoesNotBlockBenignSubstring()
    {
        var moderation = new CommunityTextModerationService(new ConfigurationBuilder().Build());
        Assert.True(moderation.Evaluate("I am building a class schedule.", "JourneyProfile").IsAllowed);
    }

    [Fact]
    public async Task Recommendations_UseOneComparableCategory_AndKeepPrivacyPreferencesIndependent()
    {
        await using var db = ControllerTestHelpers.BuildDb();

        var viewer = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "single-category-viewer",
            FirstName = "Single",
            LastName = "Viewer",
            Email = "single.viewer@example.test"
        };
        var visibleCandidate = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "single-category-candidate",
            FirstName = "Visible",
            LastName = "Candidate",
            Email = "visible.candidate@example.test"
        };

        db.ClientProfiles.AddRange(viewer, visibleCandidate);
        await db.SaveChangesAsync();

        var service = new JourneyCirclesService(
            db,
            new CommunityTextModerationService(
                new ConfigurationBuilder().Build()),
            NullLogger<JourneyCirclesService>.Instance);

        var viewerInput = new JourneyCircleProfileInput(
            ConsentAffirmed: true,
            IsOptedIn: true,
            IsDiscoverable: false,
            AllowSuggestions: true,
            AllowConnectionRequests: false,
            Introduction: "Looking for someone with the same primary goal.",
            LifeStages: [],
            Locations: [],
            Goals: ["Growing a business"],
            Interests: [],
            CircleCodes: [],
            ConnectionTypes: [],
            CommunicationStyles: [],
            AccountabilityFrequencies: []);

        var candidateInput = new JourneyCircleProfileInput(
            ConsentAffirmed: true,
            IsOptedIn: true,
            IsDiscoverable: true,
            AllowSuggestions: false,
            AllowConnectionRequests: false,
            Introduction: "Working toward the same financial goal.",
            LifeStages: [],
            Locations: [],
            Goals: ["Growing a business"],
            Interests: [],
            CircleCodes: [],
            ConnectionTypes: [],
            CommunicationStyles: [],
            AccountabilityFrequencies: []);

        Assert.True(
            (await service.SaveProfileAsync(
                viewer.ClientUserId,
                viewerInput)).Succeeded);

        Assert.True(
            (await service.SaveProfileAsync(
                visibleCandidate.ClientUserId,
                candidateInput)).Succeeded);

        var dashboard = await service.GetDashboardAsync(
            viewer.ClientUserId);

        var recommendation = Assert.Single(dashboard.Recommendations);

        Assert.Equal(
            visibleCandidate.Id,
            recommendation.Profile.ClientProfileId);
        Assert.Equal(
            $"/JourneyCircles/Profiles/{visibleCandidate.Id}/Avatar",
            recommendation.Profile.AvatarUrl);

        Assert.Contains(
            "shared goal: Growing a business",
            recommendation.Explanation,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Recommendations_RespectConsentJoinDiscoverabilityAndViewerSuggestionControlsIndependently()
    {
        await using var db = ControllerTestHelpers.BuildDb();

        var viewer = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "preference-viewer",
            FirstName = "Preference",
            LastName = "Viewer",
            Email = "preference.viewer@example.test"
        };
        var hiddenCandidate = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "hidden-candidate",
            FirstName = "Hidden",
            LastName = "Candidate",
            Email = "hidden.candidate@example.test"
        };
        var noConsentCandidate = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "no-consent-candidate",
            FirstName = "No Consent",
            LastName = "Candidate",
            Email = "no.consent@example.test"
        };
        var visibleCandidate = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "visible-candidate",
            FirstName = "Visible",
            LastName = "Candidate",
            Email = "visible@example.test"
        };

        db.ClientProfiles.AddRange(
            viewer,
            hiddenCandidate,
            noConsentCandidate,
            visibleCandidate);

        await db.SaveChangesAsync();

        var service = new JourneyCirclesService(
            db,
            new CommunityTextModerationService(
                new ConfigurationBuilder().Build()),
            NullLogger<JourneyCirclesService>.Instance);

        JourneyCircleProfileInput Input(
            bool consent,
            bool joined,
            bool discoverable,
            bool suggestions,
            bool requests) =>
            new(
                ConsentAffirmed: consent,
                IsOptedIn: joined,
                IsDiscoverable: discoverable,
                AllowSuggestions: suggestions,
                AllowConnectionRequests: requests,
                Introduction: null,
                LifeStages: [],
                Locations: [],
                Goals: ["Growing a business"],
                Interests: [],
                CircleCodes: [],
                ConnectionTypes: [],
                CommunicationStyles: [],
                AccountabilityFrequencies: []);

        Assert.True(
            (await service.SaveProfileAsync(
                viewer.ClientUserId,
                Input(
                    consent: true,
                    joined: true,
                    discoverable: false,
                    suggestions: true,
                    requests: false))).Succeeded);

        Assert.True(
            (await service.SaveProfileAsync(
                hiddenCandidate.ClientUserId,
                Input(
                    consent: true,
                    joined: true,
                    discoverable: false,
                    suggestions: false,
                    requests: true))).Succeeded);

        Assert.True(
            (await service.SaveProfileAsync(
                noConsentCandidate.ClientUserId,
                Input(
                    consent: false,
                    joined: true,
                    discoverable: true,
                    suggestions: false,
                    requests: true))).Succeeded);

        Assert.True(
            (await service.SaveProfileAsync(
                visibleCandidate.ClientUserId,
                Input(
                    consent: true,
                    joined: true,
                    discoverable: true,
                    suggestions: false,
                    requests: false))).Succeeded);

        var dashboard = await service.GetDashboardAsync(
            viewer.ClientUserId);

        var recommendation = Assert.Single(dashboard.Recommendations);

        Assert.Equal(
            visibleCandidate.Id,
            recommendation.Profile.ClientProfileId);

        var viewerProfile = await db.JourneyCircleProfiles
            .SingleAsync(x => x.ClientProfileId == viewer.Id);

        viewerProfile.AllowSuggestions = false;
        await db.SaveChangesAsync();

        var disabledDashboard = await service.GetDashboardAsync(
            viewer.ClientUserId);

        var discoveryProfile =
            Assert.Single(disabledDashboard.Recommendations);

        Assert.Equal(
            visibleCandidate.Id,
            discoveryProfile.Profile.ClientProfileId);

        Assert.Equal(
            "Discover someone new in the Legend community.",
            discoveryProfile.Explanation);
    }

    [Fact]
    public async Task ProfileSelections_AreControlledMultiSelectValues_AndImproveRecommendations()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var first = new ClientProfile { Id = Guid.NewGuid(), ClientUserId = "client-one", FirstName = "One", LastName = "Client", Email = "one@example.test" };
        var second = new ClientProfile { Id = Guid.NewGuid(), ClientUserId = "client-two", FirstName = "Two", LastName = "Client", Email = "two@example.test" };
        db.ClientProfiles.AddRange(first, second);
        await db.SaveChangesAsync();
        var moderation = new CommunityTextModerationService(new ConfigurationBuilder().Build());
        var service = new JourneyCirclesService(db, moderation, NullLogger<JourneyCirclesService>.Instance);

        var firstInput = new JourneyCircleProfileInput(true, true, true, true, true, "Looking for practical accountability.",
            ["Career transition", "Business ownership"], ["Southwest"], ["Growing a business"], ["Small business", "Leadership"],
            ["Entrepreneurs Circle"], ["Business peer"], ["Detailed planning"], ["Weekly"]);
        var secondInput = new JourneyCircleProfileInput(true, true, true, true, true, "Building with other founders.",
            ["Career transition"], ["Southwest"], ["Growing a business"], ["Small business"],
            ["Entrepreneurs Circle"], ["Business peer"], ["Detailed planning"], ["Weekly"]);

        Assert.True((await service.SaveProfileAsync(first.ClientUserId, firstInput)).Succeeded);
        Assert.True((await service.SaveProfileAsync(second.ClientUserId, secondInput)).Succeeded);

        var dashboard = await service.GetDashboardAsync(first.ClientUserId);
        Assert.Equal(["Career transition", "Business ownership"], dashboard.Profile!.LifeStages);
        Assert.Equal(["Southwest"], dashboard.Profile.Locations);
        Assert.Equal("One Client", dashboard.Profile.DisplayName);
        Assert.Single(dashboard.Recommendations);
        Assert.Equal(second.Id, dashboard.Recommendations[0].Profile.ClientProfileId);

        var invalid = firstInput with { Locations = ["Unverified free-form location"] };
        var rejected = await service.SaveProfileAsync(first.ClientUserId, invalid);
        Assert.False(rejected.Succeeded);
        Assert.Equal("JOURNEY_TAXONOMY_INVALID", rejected.ErrorCode);
    }

    [Fact]
    public async Task DiscoverFeed_ReturnsEligibleProfiles_WhenNoRecommendationQualifies()
    {
        await using var db = ControllerTestHelpers.BuildDb();

        var viewer = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "discover-fallback-viewer",
            FirstName = "Fallback",
            LastName = "Viewer",
            Email = "fallback.viewer@example.test"
        };

        var candidate = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "discover-fallback-candidate",
            FirstName = "Discoverable",
            LastName = "Member",
            Email = "discoverable.member@example.test"
        };

        db.ClientProfiles.AddRange(viewer, candidate);
        await db.SaveChangesAsync();

        var service = new JourneyCirclesService(
            db,
            new CommunityTextModerationService(
                new ConfigurationBuilder().Build()),
            NullLogger<JourneyCirclesService>.Instance);

        var viewerInput = new JourneyCircleProfileInput(
            ConsentAffirmed: true,
            IsOptedIn: true,
            IsDiscoverable: false,
            AllowSuggestions: true,
            AllowConnectionRequests: true,
            Introduction: "Focused on building a business.",
            LifeStages: ["Business ownership"],
            Locations: [],
            Goals: ["Growing a business"],
            Interests: ["Leadership"],
            CircleCodes: ["Entrepreneurs Circle"],
            ConnectionTypes: ["Business peer"],
            CommunicationStyles: ["Detailed planning"],
            AccountabilityFrequencies: ["Weekly"]);

        var candidateInput = new JourneyCircleProfileInput(
            ConsentAffirmed: true,
            IsOptedIn: true,
            IsDiscoverable: true,
            AllowSuggestions: false,
            AllowConnectionRequests: true,
            Introduction: "Focused on family wellness.",
            LifeStages: ["Growing family"],
            Locations: [],
            Goals: ["Personal growth"],
            Interests: ["Fitness"],
            CircleCodes: ["Accountability Circle"],
            ConnectionTypes: ["Accountability partner"],
            CommunicationStyles: ["Encouragement-focused"],
            AccountabilityFrequencies: ["Monthly"]);

        Assert.True(
            (await service.SaveProfileAsync(
                viewer.ClientUserId,
                viewerInput)).Succeeded);

        Assert.True(
            (await service.SaveProfileAsync(
                candidate.ClientUserId,
                candidateInput)).Succeeded);

        var dashboard = await service.GetDashboardAsync(
            viewer.ClientUserId);

        var result = Assert.Single(dashboard.Recommendations);

        Assert.Equal(
            candidate.Id,
            result.Profile.ClientProfileId);

        Assert.Equal(
            "Discover someone new in the Legend community.",
            result.Explanation);
    }

    [Fact]
    public async Task DiscoverFeed_PrioritizesRecommendations_BeforeGeneralProfiles()
    {
        await using var db = ControllerTestHelpers.BuildDb();

        var viewer = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "discover-order-viewer",
            FirstName = "Order",
            LastName = "Viewer",
            Email = "order.viewer@example.test"
        };

        var recommended = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "discover-order-recommended",
            FirstName = "Recommended",
            LastName = "Member",
            Email = "recommended.member@example.test"
        };

        var general = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "discover-order-general",
            FirstName = "General",
            LastName = "Member",
            Email = "general.member@example.test"
        };

        db.ClientProfiles.AddRange(viewer, recommended, general);
        await db.SaveChangesAsync();

        var service = new JourneyCirclesService(
            db,
            new CommunityTextModerationService(
                new ConfigurationBuilder().Build()),
            NullLogger<JourneyCirclesService>.Instance);

        JourneyCircleProfileInput Input(
            string goal,
            string circle,
            string connectionType) =>
            new(
                ConsentAffirmed: true,
                IsOptedIn: true,
                IsDiscoverable: true,
                AllowSuggestions: true,
                AllowConnectionRequests: true,
                Introduction: null,
                LifeStages: [],
                Locations: [],
                Goals: [goal],
                Interests: [],
                CircleCodes: [circle],
                ConnectionTypes: [connectionType],
                CommunicationStyles: [],
                AccountabilityFrequencies: []);

        Assert.True(
            (await service.SaveProfileAsync(
                viewer.ClientUserId,
                Input(
                    "Growing a business",
                    "Entrepreneurs Circle",
                    "Business peer"))).Succeeded);

        Assert.True(
            (await service.SaveProfileAsync(
                recommended.ClientUserId,
                Input(
                    "Growing a business",
                    "Entrepreneurs Circle",
                    "Business peer"))).Succeeded);

        Assert.True(
            (await service.SaveProfileAsync(
                general.ClientUserId,
                Input(
                    "Health and wellness",
                    "Wellness Circle",
                    "Accountability partner"))).Succeeded);

        var dashboard = await service.GetDashboardAsync(
            viewer.ClientUserId);

        Assert.Equal(2, dashboard.Recommendations.Count);

        Assert.Equal(
            recommended.Id,
            dashboard.Recommendations[0].Profile.ClientProfileId);

        Assert.StartsWith(
            "Recommended for you.",
            dashboard.Recommendations[0].Explanation);

        Assert.Equal(
            general.Id,
            dashboard.Recommendations[1].Profile.ClientProfileId);

        Assert.Equal(
            "Discover someone new in the Legend community.",
            dashboard.Recommendations[1].Explanation);
    }

}
