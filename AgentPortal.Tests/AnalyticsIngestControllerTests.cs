using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using AgentPortal.Controllers.Api;
using AgentPortal.Models;
using AgentPortal.Security;
using AgentPortal.Services.Tracking;
using Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public class AnalyticsIngestControllerTests
{
    private static AnalyticsIngestController BuildController(MasterAppDbContext db, string secret = "secret")
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Analytics:SharedSecret"] = secret
            })
            .Build();

        var resolver = new AgentTrackingResolver(db, NullLogger<AgentTrackingResolver>.Instance);
        var flags = Options.Create(new AppFeatureFlags { IngestHmacEnabled = false });
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var signatureValidator = new IngestSignatureValidator(memoryCache, config, NullLogger<IngestSignatureValidator>.Instance);
        var controller = new AnalyticsIngestController(
            db,
            config,
            resolver,
            NullLogger<AnalyticsIngestController>.Instance,
            flags,
            signatureValidator);

        var http = new DefaultHttpContext();
        http.Request.Headers["X-Shared-Secret"] = secret;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static string? ReadStatus(object? value)
        => value?.GetType().GetProperty("status")?.GetValue(value)?.ToString();

    [Fact]
    public async Task Ingest_DuplicateClientEventId_ReturnsDuplicateIgnored()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var controller = BuildController(db);

        var clientEventId = Guid.NewGuid();

        var first = await controller.Ingest(new AnalyticsIngestController.AnalyticsEventRequest
        {
            ClientEventId = clientEventId,
            EventType = "page_view",
            Host = "test",
            Path = "/",
            EventUtc = DateTime.UtcNow
        });

        var second = await controller.Ingest(new AnalyticsIngestController.AnalyticsEventRequest
        {
            ClientEventId = clientEventId,
            EventType = "page_view",
            Host = "test",
            Path = "/",
            EventUtc = DateTime.UtcNow
        });

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

        var result = await controller.Ingest(new AnalyticsIngestController.AnalyticsEventRequest
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
        });

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

        var result = await controller.Ingest(new AnalyticsIngestController.AnalyticsEventRequest
        {
            ClientEventId = Guid.NewGuid(),
            EventType = "lead_form_start",
            Host = "test",
            Path = "/quote/life",
            SessionId = "session-123",
            EventUtc = DateTime.UtcNow,
            MetadataJson = "{\"custom\":\"value\"}"
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("ok", ReadStatus(ok.Value));

        var ev = Assert.Single(db.AnalyticsEvents);
        Assert.True(MetaSignalSingleTruthPolicy.ReadBoolean(ev.MetadataJson, "isBrowserSignal"));
        Assert.False(MetaSignalSingleTruthPolicy.ReadBoolean(ev.MetadataJson, "isServerAuthority"));
        Assert.False(MetaSignalSingleTruthPolicy.ReadBoolean(ev.MetadataJson, "metaServerAuthorityEligible"));
        Assert.False(MetaSignalSingleTruthPolicy.ReadBoolean(ev.MetadataJson, "metaSingleTruthDispatchEligible"));
        Assert.Equal("browser_analytics_ingest", MetaSignalSingleTruthPolicy.ReadString(ev.MetadataJson, "metaPipelineOrigin"));
        Assert.Equal("lead_form_start:anonymous:session-123", MetaSignalSingleTruthPolicy.ReadString(ev.MetadataJson, "eventKey"));

        using var metadata = JsonDocument.Parse(ev.MetadataJson!);
        Assert.Equal("value", metadata.RootElement.GetProperty("custom").GetString());
    }

    [Fact]
    public async Task Ingest_Accepts_LifeCoverageSelect_And_Rejects_ServerOnlyEvents()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var controller = BuildController(db);

        var accepted = await controller.Ingest(new AnalyticsIngestController.AnalyticsEventRequest
        {
            ClientEventId = Guid.NewGuid(),
            EventType = "life_step1_coverage_select",
            Host = "test",
            Path = "/quote/life",
            PageKey = "quote_term_life_landing",
            QuoteType = "term",
            EventUtc = DateTime.UtcNow
        });

        var rejected = await controller.Ingest(new AnalyticsIngestController.AnalyticsEventRequest
        {
            ClientEventId = Guid.NewGuid(),
            EventType = "website_lead_submitted",
            Host = "test",
            Path = "/quote/life",
            PageKey = "quote_term_life_landing",
            QuoteType = "term",
            EventUtc = DateTime.UtcNow
        });

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

        var result = await controller.Ingest(new AnalyticsIngestController.AnalyticsEventRequest
        {
            ClientEventId = Guid.NewGuid(),
            EventType = eventType,
            Host = "test",
            Path = "/Quote/Life/landing",
            PageKey = "quote_life_landing",
            QuoteType = "life",
            EventUtc = DateTime.UtcNow
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("ok", ReadStatus(ok.Value));
    }
}
