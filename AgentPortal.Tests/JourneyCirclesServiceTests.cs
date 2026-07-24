using System;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.JourneyCircles;
using Infrastructure.JourneyCircles;
using Infrastructure.Moderation;
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
            new JourneyCircleProfile { Id = Guid.NewGuid(), ClientProfileId = first.Id, IsOptedIn = true, IsDiscoverable = true, AllowSuggestions = true, AllowConnectionRequests = true, DisplayName = "One", CommunityAccessState = "Active" },
            new JourneyCircleProfile { Id = Guid.NewGuid(), ClientProfileId = second.Id, IsOptedIn = true, IsDiscoverable = true, AllowSuggestions = true, AllowConnectionRequests = true, DisplayName = "Two", CommunityAccessState = "Active" });
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
}
