using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using ProtectWebsite.Controllers;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using AgentPortal.Models;
using Infrastructure.Data;
using Infrastructure.Analytics;
using Infrastructure.WebsiteEditing;
using Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public class WebsiteTrackingIngestTests
{
    internal static TrackingProxyController BuildController(MasterAppDbContext db, bool runtimeScopes = false)
    {
        if (!db.AgentTrackingProfiles.Any())
        {
            db.AgentTrackingProfiles.Add(new Domain.Entities.AgentTrackingProfile
            {
                Id = Guid.NewGuid(), AgentUserId = "founder-test", AgentUpn = "founder@example.test",
                Slug = "founder-test", DisplayName = "Founder", Status = "active"
            });
            db.SaveChanges();
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Founder:Upn"] = "founder@example.test" }).Build();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton(db);
        var resolver = new AgentTrackingResolver(db, NullLogger<AgentTrackingResolver>.Instance);
        services.AddSingleton(resolver);
        if (runtimeScopes)
        {
            var domains = new WebsiteDomainService(db, Moq.Mock.Of<System.Net.Http.IHttpClientFactory>(), config);
            services.AddSingleton(new PublicWebsiteRuntimeScopeResolver(db, domains, config));
        }
        var controller = new TrackingProxyController(Moq.Mock.Of<System.Net.Http.IHttpClientFactory>(), config,
            NullLogger<TrackingProxyController>.Instance,
            resolver);
        controller.ControllerContext = new ControllerContext
            { HttpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() } };
        controller.Request.Host = new HostString("protect.example.test");
        controller.Request.Method = "POST";
        return controller;
    }

    [Fact]
    public async Task UnknownScopedPathCannotFallThroughToFounderOrPayloadOwner()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var controller = BuildController(db);
        var founder = db.AgentTrackingProfiles.Single();
        var request = new TrackingProxyController.AnalyticsEventRequest
        {
            ClientEventId = Guid.NewGuid(), EventType = "page_view", Path = "/a/missing/",
            AgentTrackingProfileId = founder.Id, AgentSlug = founder.Slug
        };
        Assert.IsType<BadRequestObjectResult>(await controller.Ingest(request, default));
        Assert.Empty(db.AnalyticsEvents);
    }

    [Fact]
    public async Task ExplicitMissingProfileCannotFallThroughToValidSlug()
    {
        using var db = ControllerTestHelpers.BuildDb();
        BuildController(db);
        var founder = db.AgentTrackingProfiles.Single();
        var resolver = new Infrastructure.Analytics.AgentTrackingResolver(db,
            NullLogger<Infrastructure.Analytics.AgentTrackingResolver>.Instance);
        Assert.False((await resolver.ResolveAsync(founder.Slug, Guid.NewGuid())).Found);
    }

    private static string? ReadStatus(object? value)
        => value?.GetType().GetProperty("status")?.GetValue(value)?.ToString();

    [Fact]
    public async Task Ingest_DuplicateClientEventId_ReturnsDuplicateIgnored()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var controller = BuildController(db);

        var clientEventId = Guid.NewGuid();

        var first = await controller.Ingest(new TrackingProxyController.AnalyticsEventRequest
        {
            ClientEventId = clientEventId,
            EventType = "page_view",
            Host = "test",
            Path = "/",
            EventUtc = DateTime.UtcNow
        }, default);

        var second = await controller.Ingest(new TrackingProxyController.AnalyticsEventRequest
        {
            ClientEventId = clientEventId,
            EventType = "page_view",
            Host = "test",
            Path = "/",
            EventUtc = DateTime.UtcNow
        }, default);

        var ok1 = Assert.IsType<OkObjectResult>(first);
        var ok2 = Assert.IsType<OkObjectResult>(second);

        Assert.Equal("ok", ReadStatus(ok1.Value));
        Assert.Equal("duplicate_ignored", ReadStatus(ok2.Value));
        Assert.Single(db.AnalyticsEvents);
    }

    [Fact]
    public async Task Ingest_Persists_Fbclid_And_UtmDetailFields()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var controller = BuildController(db);

        var result = await controller.Ingest(new TrackingProxyController.AnalyticsEventRequest
        {
            ClientEventId = Guid.NewGuid(),
            EventType = "page_view",
            Host = "test",
            Path = "/quote/life",
            EventUtc = DateTime.UtcNow,
            UtmSource = "facebook",
            UtmMedium = "cpc",
            UtmCampaign = "120246895965190404",
            UtmTerm = "life+insurance",
            UtmContent = "creative_a",
            Fbclid = "fbclid_test_123"
        }, default);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("ok", ReadStatus(ok.Value));

        var ev = Assert.Single(db.AnalyticsEvents);
        Assert.Equal("facebook", ev.UtmSource);
        Assert.Equal("cpc", ev.UtmMedium);
        Assert.Equal("120246895965190404", ev.UtmCampaign);
        Assert.Equal("life+insurance", ev.UtmTerm);
        Assert.Equal("creative_a", ev.UtmContent);
        Assert.Equal("fbclid_test_123", ev.Fbclid);
    }

    [Fact]
    public async Task Ingest_Stamps_BrowserSingleTruthMetadata()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var controller = BuildController(db);

        var result = await controller.Ingest(new TrackingProxyController.AnalyticsEventRequest
        {
            ClientEventId = Guid.NewGuid(),
            EventType = "lead_form_start",
            Host = "test",
            Path = "/quote/life",
            SessionId = "session-123",
            EventUtc = DateTime.UtcNow,
            MetadataJson = "{\"custom\":\"value\"}"
        }, default);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("ok", ReadStatus(ok.Value));

        var ev = Assert.Single(db.AnalyticsEvents);
        Assert.True(MetaSignalSingleTruthPolicy.ReadBoolean(ev.MetadataJson, "isBrowserSignal"));
        Assert.False(MetaSignalSingleTruthPolicy.ReadBoolean(ev.MetadataJson, "isServerAuthority"));
        Assert.False(MetaSignalSingleTruthPolicy.ReadBoolean(ev.MetadataJson, "metaServerAuthorityEligible"));
        Assert.False(MetaSignalSingleTruthPolicy.ReadBoolean(ev.MetadataJson, "metaSingleTruthDispatchEligible"));
        Assert.Equal(Infrastructure.Analytics.UnifiedAnalyticsWriter.PipelineStamp, ev.PipelineStamp);
        Assert.Equal("lead_form_start:anonymous:session-123", MetaSignalSingleTruthPolicy.ReadString(ev.MetadataJson, "eventKey"));

        using var metadata = JsonDocument.Parse(ev.MetadataJson!);
        Assert.Contains("custom", ev.MetadataJson);
        Assert.Contains("value", ev.MetadataJson);
    }

    [Fact]
    public async Task Ingest_Accepts_LifeCoverageSelect_And_Rejects_ServerOnlyEvents()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var controller = BuildController(db);

        var accepted = await controller.Ingest(new TrackingProxyController.AnalyticsEventRequest
        {
            ClientEventId = Guid.NewGuid(),
            EventType = "life_step1_coverage_select",
            Host = "test",
            Path = "/quote/life",
            PageKey = "quote_term_life_landing",
            QuoteType = "term",
            EventUtc = DateTime.UtcNow
        }, default);

        var rejected = await controller.Ingest(new TrackingProxyController.AnalyticsEventRequest
        {
            ClientEventId = Guid.NewGuid(),
            EventType = "website_lead_submitted",
            Host = "test",
            Path = "/quote/life",
            PageKey = "quote_term_life_landing",
            QuoteType = "term",
            EventUtc = DateTime.UtcNow
        }, default);

        var acceptedOk = Assert.IsType<OkObjectResult>(accepted);
        Assert.Equal("ok", ReadStatus(acceptedOk.Value));
        Assert.IsType<BadRequestObjectResult>(rejected);
    }

    [Theory]
    [InlineData("first_question_view")]
    [InlineData("primary_cta_seen")]
    [InlineData("quote_entry_engaged")]
    [InlineData("cta_clicked")]
    [InlineData("funnel_started")]
    [InlineData("first_question_answered")]
    [InlineData("protecting_who_completed")]
    [InlineData("goal_completed")]
    [InlineData("tobacco_completed")]
    [InlineData("tobaccouse_completed")]
    [InlineData("age_completed")]
    [InlineData("processing_bridge_viewed")]
    [InlineData("recommendation_generated")]
    [InlineData("recommendation_viewed")]
    [InlineData("contact_step_viewed")]
    [InlineData("form_submit_attempt")]
    [InlineData("lead_form_submit_success")]
    [InlineData("form_abandon")]
    public async Task Ingest_Accepts_CatalogedQuoteBrowserEvents(string eventType)
    {
        using var db = ControllerTestHelpers.BuildDb();
        var controller = BuildController(db);

        Assert.True(AnalyticsEventCatalog.TryGet(eventType, out var definition));
        Assert.True(definition.AllowBrowser);

        var result = await controller.Ingest(new TrackingProxyController.AnalyticsEventRequest
        {
            ClientEventId = Guid.NewGuid(),
            EventType = eventType,
            Host = "test",
            Path = "/Quote/Life/landing",
            PageKey = "quote_life_landing",
            QuoteType = "life",
            EventUtc = DateTime.UtcNow
        }, default);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("ok", ReadStatus(ok.Value));
    }
    [Theory]
    [InlineData("unknown", 400)]
    [InlineData(" commerce-other ", 400)]
    [InlineData("LeGeNd", 503)]
    [InlineData(" BUSINESS ", 503)]
    public async Task SuppliedSiteContractCannotFallThroughToProtect(string site, int status)
    {
        using var db = ControllerTestHelpers.BuildDb(); var controller = BuildController(db);
        var result = await controller.Ingest(new TrackingProxyController.AnalyticsEventRequest
            { ClientEventId = Guid.NewGuid(), EventType = "page_view", SiteKey = site, Path = "/" }, default);
        if (status == 400) Assert.IsType<BadRequestObjectResult>(result);
        else Assert.Equal(status, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Empty(db.AnalyticsEvents);
    }

    [Theory]
    [InlineData("protect", "https://foreign.example.test")]
    [InlineData(null, "https://www.mylegnd.com")]
    public async Task ProtectWithExplicitOriginRequiresRuntimeScopeResolver(string? site, string origin)
    {
        using var db = ControllerTestHelpers.BuildDb(); var controller = BuildController(db);
        controller.Request.Headers.Origin = origin;
        var result = await controller.Ingest(new TrackingProxyController.AnalyticsEventRequest
            { ClientEventId = Guid.NewGuid(), EventType = "page_view", SiteKey = site, Path = "/" }, default);
        Assert.Equal(503, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Empty(db.AnalyticsEvents);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("protect")]
    [InlineData(" PROTECT ")]
    public async Task NoOriginLegacyProtectKeepsCanonicalWriter(string? site)
    {
        using var db = ControllerTestHelpers.BuildDb(); var controller = BuildController(db, true);
        var result = await controller.Ingest(new TrackingProxyController.AnalyticsEventRequest
            { ClientEventId = Guid.NewGuid(), EventType = "page_view", SiteKey = site, Path = "/" }, default);
        Assert.IsType<OkObjectResult>(result); Assert.Single(db.AnalyticsEvents);
    }

    [Theory]
    [InlineData("https://www.mylegnd.com")]
    [InlineData("https://foreign.example.test")]
    [InlineData("https://unknown.example.test")]
    [InlineData("http://protect.mylegnd.com")]
    [InlineData("not-an-origin")]
    [InlineData("")]
    [InlineData("https://protect.mylegnd.com, https://foreign.example.test")]
    public async Task ActualRuntimeResolverRejectsForeignUnknownAndInvalidProtectOrigins(string origin)
    {
        using var db = ControllerTestHelpers.BuildDb(); var controller = BuildController(db, true);
        controller.Request.Headers.Origin = origin;
        Assert.True(controller.Request.Headers.ContainsKey("Origin"));
        var result = await controller.Ingest(new TrackingProxyController.AnalyticsEventRequest
            { ClientEventId = Guid.NewGuid(), EventType = "page_view", SiteKey = "protect", Path = "/" }, default);
        Assert.IsType<BadRequestObjectResult>(result); Assert.Empty(db.AnalyticsEvents);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("protect")]
    [InlineData(" ProTeCt ")]
    public async Task ActualProtectOriginUsesResolvedOwnerAndIgnoresPayloadOwner(string? site)
    {
        using var db = ControllerTestHelpers.BuildDb(); var controller = BuildController(db, true);
        var owner = db.AgentTrackingProfiles.Single(); controller.Request.Headers.Origin = "https://protect.mylegnd.com";
        var request = new TrackingProxyController.AnalyticsEventRequest
            { ClientEventId = Guid.NewGuid(), EventType = "page_view", SiteKey = site, Path = "/", AgentTrackingProfileId = Guid.NewGuid(), AgentSlug = "spoofed" };
        Assert.IsType<OkObjectResult>(await controller.Ingest(request, default));
        var row = Assert.Single(db.AnalyticsEvents); Assert.Equal(owner.Id, row.AgentTrackingProfileId);
        Assert.Equal(request.ClientEventId, row.EventId);
    }

    [Theory]
    [InlineData("legend")]
    [InlineData(" LEGEND ")]
    public async Task ActualLegendScopeNeverWritesProtectOwner(string site)
    {
        using var db = ControllerTestHelpers.BuildDb(); var controller = BuildController(db, true);
        controller.Request.Headers.Origin = "https://www.mylegnd.com";
        Assert.IsType<OkObjectResult>(await controller.Ingest(new TrackingProxyController.AnalyticsEventRequest
            { ClientEventId = Guid.NewGuid(), EventType = "page_view", SiteKey = site, Path = "/", AgentTrackingProfileId = db.AgentTrackingProfiles.Single().Id }, default));
        var row = Assert.Single(db.AnalyticsEvents); Assert.Null(row.AgentTrackingProfileId); Assert.Null(row.CommerceBusinessId);
        using var metadata = JsonDocument.Parse(row.MetadataJson!); Assert.Equal("legend", metadata.RootElement.GetProperty("siteKey").GetString());
    }

    [Fact]
    public async Task TwoPublishedBusinessesKeepOriginScopeAndRejectCrossOwnerReplay()
    {
        using var db = ControllerTestHelpers.BuildDb(); var controller = BuildController(db, true);
        var first = await SeedPublishedBusinessAsync(db, "first.example.test");
        var second = await SeedPublishedBusinessAsync(db, "second.example.test");
        var firstEvent = Guid.NewGuid(); var secondEvent = Guid.NewGuid();
        TrackingProxyController.AnalyticsEventRequest Request(Guid id) => new()
            { ClientEventId = id, EventType = "page_view", SiteKey = " BuSiNeSs ", Path = "/", AgentTrackingProfileId = db.AgentTrackingProfiles.Single().Id, AgentSlug = "spoofed-owner", SessionId = "shared-session", VisitorId = "shared-visitor" };
        controller.Request.Headers.Origin = "https://first.example.test";
        Assert.IsType<OkObjectResult>(await controller.Ingest(Request(firstEvent), default));
        var duplicate = Assert.IsType<OkObjectResult>(await controller.Ingest(Request(firstEvent), default));
        Assert.Equal("duplicate_ignored", ReadStatus(duplicate.Value));
        controller.Request.Headers.Origin = "https://second.example.test";
        Assert.IsType<ConflictObjectResult>(await controller.Ingest(Request(firstEvent), default));
        Assert.IsType<OkObjectResult>(await controller.Ingest(Request(secondEvent), default));
        var rows = db.AnalyticsEvents.ToList(); Assert.Equal(2, rows.Count);
        Assert.Equal(first.Id, rows.Single(row => row.ClientEventId == firstEvent).CommerceBusinessId);
        Assert.Equal(second.Id, rows.Single(row => row.ClientEventId == secondEvent).CommerceBusinessId);
        Assert.All(rows, row => Assert.Null(row.AgentTrackingProfileId));
    }

    private static async Task<CommerceBusiness> SeedPublishedBusinessAsync(MasterAppDbContext db, string host)
    {
        var business = new CommerceBusiness { Key = host.Split('.')[0], DisplayName = host };
        var state = new WebsiteContentState { SiteKey = WebsiteEditorSiteKeys.Business,
            OwnerKey = WebsiteEditorSiteKeys.BusinessOwnerKey(business.Id), CommerceBusinessId = business.Id, Revision = 1 };
        var document = new WebsiteContentDocument { Pages = new(StringComparer.Ordinal) { ["/"] = new WebsitePageDocument { Title = host } } };
        var version = new WebsiteContentVersion { StateId = state.Id, Revision = 1,
            DocumentJson = JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CompiledPagesJson = "{\"version\":2,\"pages\":{\"/\":{\"html\":\"<main>Published fixture</main>\"}}}" };
        state.PublishedVersionId = version.Id;
        db.Add(business); db.Add(state); db.Add(version);
        db.Add(new WebsiteDomainBinding { CommerceBusinessId = business.Id, Hostname = host,
            Status = "active", CertificateStatus = "active", LastCheckedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync(); return business;
    }

    public static IEnumerable<object[]> ServerOnlyPublicEvents() => AnalyticsEventCatalog.Definitions
        .Where(definition => definition.AllowServer && !definition.AllowBrowser).Select(definition => new object[] { definition.Name })
        .Concat(new[] { "Lead", "AppointmentBooked", "AddToCart", "InitiateCheckout", "Purchase", "PolicyPaid" }.Select(name => new object[] { name }))
        .DistinctBy(row => row[0]);

    [Theory]
    [MemberData(nameof(ServerOnlyPublicEvents))]
    public async Task PublicIngestCannotForgeServerConfirmedOutcomes(string eventType)
    {
        using var db = ControllerTestHelpers.BuildDb(); var controller = BuildController(db, true);
        controller.Request.Headers.Origin = "https://protect.mylegnd.com";
        Assert.IsType<BadRequestObjectResult>(await controller.Ingest(new TrackingProxyController.AnalyticsEventRequest
            { ClientEventId = Guid.NewGuid(), EventType = eventType, SiteKey = "protect", Path = "/" }, default));
        Assert.Empty(db.AnalyticsEvents);
    }

    [Theory]
    [InlineData("mismatched_id")]
    [InlineData("malformed_id")]
    [InlineData("mismatched_name")]
    public async Task PublicIngestRejectsMalformedSignalIdentityPair(string kind)
    {
        using var db = ControllerTestHelpers.BuildDb(); var controller = BuildController(db, true);
        var id = Guid.NewGuid();
        var signal = new MetaSignalIngestRequest { EventId = kind == "malformed_id" ? "invalid-guid" : (kind == "mismatched_id" ? Guid.NewGuid() : id).ToString("N"),
            EventName = kind == "mismatched_name" ? "cta_click" : "page_view" };
        Assert.IsType<BadRequestObjectResult>(await controller.Ingest(new TrackingProxyController.AnalyticsEventRequest
            { ClientEventId = id, EventType = "page_view", SiteKey = "protect", Path = "/", MetaSignal = signal }, default));
        Assert.Empty(db.AnalyticsEvents);
    }

}
