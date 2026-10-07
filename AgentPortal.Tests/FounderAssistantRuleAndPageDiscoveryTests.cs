using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentPortal.Tests;

public sealed class FounderAssistantRuleAndPageDiscoveryTests
{
    [Fact]
    public async Task DurableFounderRulePersistsAcrossServiceInstancesAndSupersedesByScopeAndKey()
    {
        var previous = Environment.GetEnvironmentVariable("FOUNDER_OID");
        var founderId = Guid.NewGuid().ToString();
        Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);
        try
        {
            await using var db = ControllerTestHelpers.BuildDb();
            var profile = new AgentProfile
            {
                Id = Guid.NewGuid(),
                AgentUserId = founderId,
                AgentUpn = "founder@example.com",
                IsActive = true
            };
            db.AgentProfiles.Add(profile);
            await db.SaveChangesAsync();

            var actor = new MessagingActor(founderId, MessagingParticipantTypes.Agent);
            var firstService = new ControlledResourceAccessService(db);
            const string first = "Never use overrides or stacked patches.";
            var saved = await firstService.UpsertFounderAssistantRuleAsync(
                actor, "engineering.no-overrides", "engineering", first,
                "Remember this forever: Never use overrides or stacked patches.");
            Assert.True(saved.Succeeded, saved.ReasonCode);

            var secondService = new ControlledResourceAccessService(db);
            var loaded = await secondService.GetFounderAssistantRulesAsync(actor);
            Assert.Equal(first, Assert.Single(loaded).RuleText);

            const string replacement = "Never use overrides, stacked patches, or duplicate authorities.";
            var replaced = await secondService.UpsertFounderAssistantRuleAsync(
                actor, "engineering.no-overrides", "engineering", replacement,
                "Correction: Never use overrides, stacked patches, or duplicate authorities.");
            Assert.True(replaced.Succeeded, replaced.ReasonCode);

            var current = await new ControlledResourceAccessService(db).GetFounderAssistantRulesAsync(actor);
            Assert.Equal(replacement, Assert.Single(current).RuleText);

            var json = await db.MobileProfileSettings.AsNoTracking()
                .Where(row => row.ProfileId == profile.Id)
                .Select(row => row.FounderAssistantRulesJson)
                .SingleAsync();
            var stored = JsonSerializer.Deserialize<FounderAssistantRule[]>(json);
            Assert.NotNull(stored);
            Assert.Equal(2, stored!.Length);
            Assert.Equal(1, stored.Count(rule => rule.SupersededUtc is not null));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", previous);
        }
    }

    [Theory]
    [InlineData("password=super-secret", "Remember password=super-secret")]
    [InlineData("zac@example.com", "Remember zac@example.com")]
    [InlineData("602-555-1212", "Remember 602-555-1212")]
    [InlineData("Use concise answers.", "This message does not contain the requested literal rule.")]
    public async Task DurableFounderRuleRejectsPrivateSecretOrNonLiteralContent(string rule, string message)
    {
        var previous = Environment.GetEnvironmentVariable("FOUNDER_OID");
        var founderId = Guid.NewGuid().ToString();
        Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);
        try
        {
            await using var db = ControllerTestHelpers.BuildDb();
            db.AgentProfiles.Add(new AgentProfile
            {
                Id = Guid.NewGuid(),
                AgentUserId = founderId,
                AgentUpn = "founder@example.com",
                IsActive = true
            });
            await db.SaveChangesAsync();

            var result = await new ControlledResourceAccessService(db).UpsertFounderAssistantRuleAsync(
                new MessagingActor(founderId, MessagingParticipantTypes.Agent),
                "communication.test", "communication", rule, message);

            Assert.False(result.Succeeded);
            Assert.Empty(db.MobileProfileSettings);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", previous);
        }
    }

    [Fact]
    public void MasterAppCatalogIncludesLiveRuntimeRouteMetadataWithoutHardcodedPageRegistry()
    {
        RequestDelegate request = _ => Task.CompletedTask;
        var page = new RouteEndpointBuilder(
            request,
            RoutePatternFactory.Parse("/founder/legend-ai"),
            order: 0)
        {
            DisplayName = "Founder Legend AI"
        };
        page.Metadata.Add(new HttpMethodMetadata(new[] { "GET" }));
        var api = new RouteEndpointBuilder(
            request,
            RoutePatternFactory.Parse("/api/private-write"),
            order: 0);
        api.Metadata.Add(new HttpMethodMetadata(new[] { "POST" }));

        var source = new DefaultEndpointDataSource(page.Build(), api.Build());
        var authority = new LegendMasterAppReadAuthority(
            Array.Empty<ILegendMasterAppReadProjection>(),
            new[] { source });

        var catalog = JsonSerializer.SerializeToElement(authority.Catalog());
        var routes = catalog.GetProperty("structuralPageAuthority").GetProperty("runtimeRoutes");

        var found = Assert.Single(routes.EnumerateArray());
        Assert.Equal("/founder/legend-ai", found.GetProperty("route").GetString());
        Assert.Equal("GET", Assert.Single(found.GetProperty("methods").EnumerateArray()).GetString());
        Assert.Equal("runtime_route_metadata_only", found.GetProperty("access").GetString());
        Assert.Equal("canonical_page_service_projections", catalog.GetProperty("sourceOfTruth").GetString());
        Assert.False(catalog.GetProperty("providerSpecificRegistry").GetBoolean());
    }
}
