using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Infrastructure.Data;
using Infrastructure.Leads;
using Infrastructure.WebsiteEditing;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.EntityFrameworkCore;
using Moq;
using Shared.Analytics;
using Shared.Diagnostics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CanonicalMeasurementTruthTests
{
    [Fact]
    public void EngineeringAttributeRouteCannotResolveToConventionalLogin()
    {
        var login = Action("Account", "Login");
        var engineering = Action("FounderEngineering", "Index");
        engineering.AttributeRouteInfo = new AttributeRouteInfo { Template = "founder/engineering" };
        var source = new DefaultEndpointDataSource(
            Endpoint("{controller=Home}/{action=Index}/{id?}", login, 1),
            Endpoint("founder/engineering", engineering, 0));
        var route = LegendSiteToolDisclosureAuthority.DiagnosticRoute(engineering);
        Assert.Equal("/founder/engineering", route);
        var authority = LegendSiteToolDisclosureAuthority.ResolveRouteAuthority(route, new[] { source });
        Assert.Equal("Index", authority.Action);
        Assert.Equal(route, authority.Route);
        Assert.NotEqual("Login", authority.Action);
    }

    [Fact]
    public void ConventionalRouteVerifiesActionAndDoesNotDiscloseIdentifier()
    {
        var source = new DefaultEndpointDataSource(
            Endpoint("{controller=Home}/{action=Index}/{id?}", Action("Account", "Login"), 1),
            Endpoint("{controller=Home}/{action=Index}/{id?}", Action("Reports", "Detail"), 1));
        var result = LegendSiteToolDisclosureAuthority.ResolveRouteAuthority("/Reports/Detail/private-id", new[] { source });
        Assert.Equal("Detail", result.Action);
        Assert.Equal("/{controller}/{action}/{id}", result.Route);
        Assert.Equal("/unmatched", LegendSiteToolDisclosureAuthority.ResolveRouteAuthority("/Reports/Detail?secret=x", new[] { source }).Route);
    }

    [Fact]
    public void AmbiguousAuthorityFailsClosed()
    {
        var source = new DefaultEndpointDataSource(Endpoint("reports", Action("A", "Index"), 0),
            Endpoint("reports", Action("B", "Index"), 0));
        Assert.Equal("/unmatched", LegendSiteToolDisclosureAuthority.ResolveRouteAuthority("/reports", new[] { source }).Route);
    }

    [Fact]
    public async Task EmptyHealthHasNoSuccessfulDeliveryClaims()
    {
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var analytics = new Mock<IAnalyticsQueryService>();
        analytics.Setup(x => x.LoadFilteredEventsAsync(It.IsAny<TimeRangeRequest>(), It.IsAny<ScopeContext>(),
            It.IsAny<Guid[]>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<AnalyticsEvent>());
        var health = await new MetaSignalAnalyticsService(db, analytics.Object)
            .GetHealthDashboardAsync(TimeRangeRequest.FromPreset("7d"), ScopeContext.Global);
        Assert.Equal(0, health.PipelineHealth.BridgeEligibleAnalyticsEventsLast24Hours);
        Assert.All(health.FailureDetection, issue => Assert.Equal("NoData", issue.Status));
        Assert.All(health.FlowIntegrity, metric => Assert.Equal("NoData", metric.Status));
    }

    [Theory]
    [InlineData("quote_landing_view", false, true)]
    [InlineData("quote_landing_view", true, false)]
    [InlineData("Lead", false, false)]
    [InlineData("Purchase", false, false)]
    public async Task HealthEligibilityMatchesActualBridgeDecision(string eventType, bool isInternal, bool expected)
    {
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var source = new AnalyticsEvent { EventId = Guid.NewGuid(), EventType = eventType, IsInternal = isInternal,
            EventUtc = DateTime.UtcNow, ReceivedUtc = DateTime.UtcNow, SessionId = "test-session", VisitorId = "test-visitor" };
        db.AnalyticsEvents.Add(source);
        await db.SaveChangesAsync();
        Assert.Equal(expected, MetaSignalAnalyticsBridge.IsEligibleSource(source));
        Assert.Equal(expected, await MetaSignalAnalyticsBridge.PersistAsync(db, source));
    }


    [Fact]
    public void NinetyDayPresetUsesTheSharedCalendarRangeAuthority()
    {
        var range = TimeRangeRequest.FromPreset("90d", viewerTz: TimeZoneInfo.Utc);
        var span = range.ToUtc - range.FromUtc;

        Assert.Equal("90d", range.Preset);
        Assert.Equal("Last 90 Days", range.Label);
        Assert.Equal(TimeGrouping.Week, range.Grouping);
        Assert.InRange(span.TotalDays, 89d, 90.1d);
    }

    [Fact]
    public void UnifiedServerEventPersistsResolvedMeasurementConsent()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers["Sec-GPC"] = "1";

        var row = UnifiedEventMapper.ToAnalytics(UnifiedEventContextBuilder.Build(
            http,
            eventId: "lead_test",
            eventName: "website_lead_submitted",
            sessionId: "session",
            visitorId: "visitor",
            isBrowserSignal: false,
            isServerAuthority: true,
            metaServerAuthorityEligible: true));

        Assert.False(CanonicalAdvertisingEventProjection.ReadBoolean(
            row.MetadataJson, "measurementConsentAllowed") == true);
        Assert.Equal("denied", CanonicalAdvertisingEventProjection.ReadString(
            row.MetadataJson, "measurementConsentState"));
        Assert.Equal("gpc", CanonicalAdvertisingEventProjection.ReadString(
            row.MetadataJson, "measurementConsentSource"));
    }

    [Fact]
    public async Task ProtectLeadPersistenceAttachesPublishedRuntimeFormLineageOnce()
    {
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var profile = new AgentTrackingProfile
        {
            AgentUserId = "agent-user",
            AgentUpn = "agent@example.org",
            Slug = "agent"
        };
        var document = new WebsiteContentDocument
        {
            Pages =
            {
                ["/Quote/Life"] = new WebsitePageDocument()
            }
        };
        WebsiteSystemTemplateAuthority.Apply(WebsiteEditorSiteKeys.Protect, document);
        var state = new WebsiteContentState
        {
            OwnerKey = profile.AgentUserId,
            SiteKey = WebsiteEditorSiteKeys.Protect,
            DraftJson = JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        var version = new WebsiteContentVersion
        {
            StateId = state.Id,
            Revision = 1,
            DocumentJson = state.DraftJson,
            ActorUserId = profile.AgentUserId
        };
        state.PublishedVersionId = version.Id;
        db.AddRange(profile, state, version);
        await db.SaveChangesAsync();

        var lead = new WebsiteLead
        {
            LeadId = Guid.NewGuid(),
            AgentTrackingProfileId = profile.Id,
            AgentSlug = profile.Slug,
            SourcePageKey = "quote_life",
            FirstName = "Taylor",
            Email = "taylor@example.org",
            Environment = "production",
            Host = "protect.mylegnd.com",
            CreatedUtc = DateTime.UtcNow
        };

        Assert.True(await WebsiteLeadSubmission.TryCreateAsync(
            db, lead, Guid.NewGuid().ToString("D"), CancellationToken.None));

        var saved = Assert.Single(await db.WebsiteLeads.ToListAsync());
        Assert.Equal(version.Id, saved.WebsiteContentVersionId);
        Assert.False(string.IsNullOrWhiteSpace(saved.WebsiteBindingId));
        Assert.Equal(
            WebsiteSystemTemplateAuthority.ResolvePublishedRuntimeFormElementId(version, "quote_life"),
            saved.WebsiteBindingId);
    }

    private static ControllerActionDescriptor Action(string controller, string action) => new()
    {
        ControllerName = controller, ActionName = action, ControllerTypeInfo = typeof(CanonicalMeasurementTruthTests).GetTypeInfo(),
        RouteValues = new Dictionary<string, string?> { ["controller"] = controller, ["action"] = action }
    };

    private static RouteEndpoint Endpoint(string template, ControllerActionDescriptor action, int order) =>
        new(_ => Task.CompletedTask, RoutePatternFactory.Parse(template), order, new EndpointMetadataCollection(action), template);
}
