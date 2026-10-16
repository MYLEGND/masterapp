using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AgentPortal.Models.Analytics;
using Infrastructure.Analytics;
using Shared.Analytics;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public class AnalyticsQueryServiceMarketingHealthTests
{
    private static AnalyticsQueryService BuildService(MasterAppDbContext db)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Analytics:EnvironmentFilter"] = "production",
                ["Analytics:ExcludeLocalHosts"] = "true"
            })
            .Build();

        return new AnalyticsQueryService(db, config);
    }

    private static TimeRangeRequest BuildRange(DateTime nowUtc) => new()
    {
        FromUtc = nowUtc.AddHours(-2),
        ToUtc = nowUtc.AddHours(2),
        Grouping = TimeGrouping.Day,
        Label = "test",
        Preset = "custom",
        QualityMode = TrafficQualityMode.AllTraffic
    };

    private static AnalyticsEvent E(
        string eventType,
        DateTime eventUtc,
        string sessionId,
        string? formKey = null,
        string? quoteType = null,
        string? metadataJson = null)
        => new()
        {
            EventId = Guid.NewGuid(),
            EventType = eventType,
            EventUtc = eventUtc,
            ReceivedUtc = eventUtc,
            SessionId = sessionId,
            FormKey = formKey,
            QuoteType = quoteType,
            MetadataJson = metadataJson,
            Environment = "production",
            Host = "portal.mylegnd.com"
        };

    private static string TrackingErrorMetadata(
        string? attemptedEventName = null,
        int? statusCode = null,
        string? errorMessage = null,
        int? retryCount = null,
        string? route = null,
        string? fetchUrl = null,
        string? queueReason = null,
        string? visitorId = null)
        => JsonSerializer.Serialize(new
        {
            attemptedEventName,
            statusCode,
            errorMessage,
            retryCount,
            route,
            fetchUrl,
            queueReason,
            sessionId = (string?)null,
            visitorId,
            trigger = "send_event"
        });

    private static WebsiteLead Lead(
        DateTime createdUtc,
        string sessionId,
        string? utmSource = null,
        string? utmMedium = null,
        string? utmCampaign = null)
        => new()
        {
            LeadId = Guid.NewGuid(),
            FirstName = "Taylor",
            LastName = "Verifier",
            Email = $"taylor+{Guid.NewGuid():N}@example.com",
            Status = "New",
            CreatedUtc = createdUtc,
            SessionId = sessionId,
            SourcePageKey = "quote_life_form",
            UtmSource = utmSource,
            UtmMedium = utmMedium,
            UtmCampaign = utmCampaign,
            Environment = "production",
            Host = "portal.mylegnd.com"
        };

    [Fact]
    public async Task GetMarketingHealthAsync_CountsActualLeadPersistedEvents_NotGenericSuccessAliases()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;

        db.AnalyticsEvents.AddRange(
            E("quote_landing_view", now.AddMinutes(-20), "s-success", formKey: "quote_life_form", quoteType: "life"),
            E("lead_form_submit_success", now.AddMinutes(-19), "s-success", formKey: "quote_life_form", quoteType: "life"),
            E("website_lead_submitted", now.AddMinutes(-18), "s-success", formKey: "quote_life_form", quoteType: "life"),
            E("lead_persisted", now.AddMinutes(-17), "s-success", formKey: "quote_life_form", quoteType: "life"),
            E("workstation_capture_attempt", now.AddMinutes(-16), "s-success", formKey: "quote_life_form", quoteType: "life"),
            E("workstation_capture_success", now.AddMinutes(-15), "s-success", formKey: "quote_life_form", quoteType: "life"),

            E("estimate_results_viewed", now.AddMinutes(-14), "s-inferred", formKey: "quote_term_life_landing", quoteType: "term"),
            E("client_tracking_error", now.AddMinutes(-13), "s-diagnostic", formKey: "quote_life_form", quoteType: "life"),

            E("workstation_capture_attempt", now.AddMinutes(-12), "s-no-owner", formKey: "quote_life_form", quoteType: "life"),
            E("workstation_capture_failure", now.AddMinutes(-11), "s-no-owner", formKey: "quote_life_form", quoteType: "life", metadataJson: "{\"Reason\":\"NoAgentOwner\"}")
        );

        db.WebsiteLeads.AddRange(
            Lead(now.AddMinutes(-10), "s-success", utmSource: "facebook", utmMedium: "cpc", utmCampaign: "life-scale"),
            Lead(now.AddMinutes(-9), "s-unknown"));

        await db.SaveChangesAsync();

        var service = BuildService(db);
        var dto = await service.GetMarketingHealthAsync(BuildRange(now), ScopeContext.Global);

        Assert.Equal(1, dto.ClientTrackingErrors);
        Assert.Equal(1, dto.ClientTrackingErrorSessions);
        Assert.Equal(1, dto.LeadPersistedEvents);
        Assert.Equal(2, dto.WorkstationCaptureAttempts);
        Assert.Equal(1, dto.WorkstationCaptureSuccesses);
        Assert.Equal(1, dto.WorkstationCaptureFailures);
        Assert.Equal(1, dto.WorkstationNoOwnerFailures);
        Assert.Equal(2, dto.InferredFormStarts);
        Assert.Equal(2, dto.MissingStartEventSessions);
        Assert.Equal(0, dto.UnknownAttributedLeads);
        Assert.Single(dto.RecentTrackingErrors);
        Assert.Contains(dto.Warnings, warning => warning.Contains("tracking errors detected", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(dto.Warnings, warning => warning.Contains("Missing funnel start events suspected", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(dto.Warnings, warning => warning.Contains("no-owner failures", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetMarketingHealthAsync_IncludesTrackingErrorDiagnostics_WithSeverityRecoveryAndActions()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;

        var criticalUnrecovered = E(
            "client_tracking_error",
            now.AddMinutes(-4),
            "s-critical",
            formKey: "quote_life_form",
            quoteType: "life",
            metadataJson: TrackingErrorMetadata(
                attemptedEventName: "lead_form_start",
                statusCode: 400,
                errorMessage: "invalid_event_type",
                retryCount: 3,
                route: "/Quote/Life/landing"));
        criticalUnrecovered.PageKey = "quote_life_landing";
        criticalUnrecovered.Path = "/Quote/Life/landing";
        criticalUnrecovered.UtmSource = "facebook";
        criticalUnrecovered.UtmCampaign = "life-launch";

        var criticalRecovered = E(
            "client_tracking_error",
            now.AddMinutes(-8),
            "s-medium",
            formKey: "quote_life_form",
            quoteType: "life",
            metadataJson: TrackingErrorMetadata(
                attemptedEventName: "quote_cta_click",
                statusCode: null,
                errorMessage: "Failed to fetch",
                retryCount: 1,
                route: "/Quote/Life/landing"));
        criticalRecovered.PageKey = "quote_life_landing";
        criticalRecovered.Path = "/Quote/Life/landing";

        var nonCriticalRecovered = E(
            "client_tracking_error",
            now.AddMinutes(-12),
            "s-low",
            formKey: "quote_life_form",
            quoteType: "life",
            metadataJson: TrackingErrorMetadata(
                attemptedEventName: "page_engaged_5s",
                statusCode: null,
                errorMessage: "NetworkError when attempting to fetch resource",
                retryCount: 1,
                route: "/Quote/Life/landing"));
        nonCriticalRecovered.PageKey = "quote_life_landing";
        nonCriticalRecovered.Path = "/Quote/Life/landing";

        db.AnalyticsEvents.AddRange(
            criticalUnrecovered,
            criticalRecovered,
            nonCriticalRecovered,
            E("quote_cta_click", now.AddMinutes(-7), "s-medium", formKey: "quote_life_form", quoteType: "life"),
            E("page_engaged_5s", now.AddMinutes(-11), "s-low", formKey: "quote_life_form", quoteType: "life"));

        await db.SaveChangesAsync();

        var service = BuildService(db);
        var dto = await service.GetMarketingHealthAsync(BuildRange(now), ScopeContext.Global);

        Assert.Equal(3, dto.ClientTrackingErrors);
        Assert.Equal(3, dto.RecentTrackingErrors.Count);

        var mostRecent = dto.RecentTrackingErrors[0];
        Assert.Equal("lead_form_start", mostRecent.AttemptedEventName);
        Assert.Equal("Critical", mostRecent.Severity);
        Assert.False(mostRecent.Recovered);
        Assert.Equal(400, mostRecent.StatusCode);
        Assert.Equal("Check event catalog / payload schema", mostRecent.SuggestedAction);
        Assert.Equal("s-critic", mostRecent.SessionIdShort);
        Assert.Equal("Facebook / Meta", mostRecent.Source);
        Assert.Equal("life-launch", mostRecent.Campaign);

        var recoveredCritical = dto.RecentTrackingErrors.Single(detail => detail.AttemptedEventName == "quote_cta_click");
        Assert.Equal("Medium", recoveredCritical.Severity);
        Assert.True(recoveredCritical.Recovered);
        Assert.Equal("Check connectivity or retry queue", recoveredCritical.SuggestedAction);

        var recoveredLow = dto.RecentTrackingErrors.Single(detail => detail.AttemptedEventName == "page_engaged_5s");
        Assert.Equal("Low", recoveredLow.Severity);
        Assert.True(recoveredLow.Recovered);
        Assert.Equal("Check connectivity or retry queue", recoveredLow.SuggestedAction);

        Assert.Contains(dto.Warnings, warning => warning.Contains("Most recent: lead_form_start failed on quote_life_landing with HTTP 400", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(dto.Warnings, warning => warning.Contains("Check event catalog / payload schema", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(dto.Warnings, warning => warning.Contains("Critical event retry recovered successfully for quote_cta_click", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(dto.Warnings, warning => warning.Contains("Critical event failed after retries for lead_form_start", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetMarketingHealthAsync_HandlesTrackingErrorDiagnosticsWithMissingMetadataSafely()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;

        var diagnostic = E("client_tracking_error", now.AddMinutes(-2), "s-null", formKey: "quote_life_form", quoteType: "life");
        diagnostic.PageKey = "quote_life_landing";
        diagnostic.Path = "/Quote/Life/landing";
        db.AnalyticsEvents.Add(diagnostic);

        await db.SaveChangesAsync();

        var service = BuildService(db);
        var dto = await service.GetMarketingHealthAsync(BuildRange(now), ScopeContext.Global);

        var detail = Assert.Single(dto.RecentTrackingErrors);
        Assert.Equal(string.Empty, detail.AttemptedEventName);
        Assert.Equal("tracking_error", detail.ErrorMessage);
        Assert.Null(detail.StatusCode);
        Assert.Null(detail.Recovered);
        Assert.Equal("Inspect browser console and server ingest logs", detail.SuggestedAction);
    }

    [Theory]
    [InlineData("{\"siteKey\":\"protect\",\"analyticsMetadata\":{\"siteKey\":\"legend\"}}", false)]
    [InlineData("{ \"siteKey\" : \"legend\", \"analyticsMetadata\" : { \"siteKey\" : \"protect\" } }", true)]
    public async Task ReportingSiteQueriesUseOnlyCanonicalRootValues(string metadata, bool matches)
    {
        using var db = ControllerTestHelpers.BuildDb();
        db.AnalyticsEvents.Add(new AnalyticsEvent { EventId = Guid.NewGuid(), EventType = "page_view", MetadataJson = metadata });
        db.MetaSignalEvents.Add(new MetaSignalEvent { EventId = Guid.NewGuid().ToString("N"), EventName = "page_view", MetadataJson = metadata });
        await db.SaveChangesAsync();
        var scope = new ScopeContext { ScopeType = ScopeType.Global, SiteKey = "legend" };
        Assert.Equal(matches ? 1 : 0, await SiteScoped(db.AnalyticsEvents.AsQueryable(), scope).CountAsync());
        Assert.Equal(matches ? 1 : 0, await SiteScoped(db.MetaSignalEvents.AsQueryable(), scope).CountAsync());
    }

    [Theory]
    [InlineData("{\"siteKey\":\"protect\",\"reportingOwner\":\"agent\",\"browserMetadata\":{\"siteKey\":\"legend\",\"reportingOwner\":\"founder\"}}", false)]
    [InlineData("{ \"reportingOwner\" : \"founder\", \"siteKey\" : \"legend\" }", true)]
    public async Task FounderOwnerQueriesDoNotTrustNestedBrowserMarkers(string metadata, bool matches)
    {
        using var db = ControllerTestHelpers.BuildDb();
        var scope = new ScopeContext { ScopeType = ScopeType.Founder, AgentTrackingProfileId = Guid.NewGuid() };
        db.AnalyticsEvents.Add(new AnalyticsEvent { EventId = Guid.NewGuid(), EventType = "page_view", MetadataJson = metadata });
        db.MetaSignalEvents.Add(new MetaSignalEvent { EventId = Guid.NewGuid().ToString("N"), EventName = "page_view", MetadataJson = metadata });
        await db.SaveChangesAsync();
        Assert.Equal(matches ? 1 : 0, await db.AnalyticsEvents.Where(OwnerPredicate<AnalyticsEvent>("ScopePredicateEvents", scope)).CountAsync());
        Assert.Equal(matches ? 1 : 0, await db.MetaSignalEvents.Where(OwnerPredicate<MetaSignalEvent>("ScopePredicateMetaEvents", scope)).CountAsync());
    }

    [Theory]
    [InlineData("{\"SiteKey\":\"protect\",\"ReportingOwner\":\"agent\",\"analyticsMetadata\":{\"SiteKey\":\"legend\",\"ReportingOwner\":\"founder\"}}", false)]
    [InlineData("{ \"ReportingOwner\" : \"founder\", \"SiteKey\" : \"legend\" }", true)]
    public async Task FounderLeadQueriesUseOnlyLegacyCanonicalRootKeys(string metadata, bool matches)
    {
        using var db = ControllerTestHelpers.BuildDb();
        db.WebsiteLeads.Add(new WebsiteLead { LeadId = Guid.NewGuid(), FirstName = "Fixture", Email = "fixture@example.test", MetadataJson = metadata });
        await db.SaveChangesAsync();
        var scope = new ScopeContext { ScopeType = ScopeType.Founder, AgentTrackingProfileId = Guid.NewGuid(), SiteKey = "legend" };
        Assert.Equal(matches ? 1 : 0, await db.WebsiteLeads.Where(OwnerPredicate<WebsiteLead>("ScopePredicateLeads", scope)).CountAsync());
    }

    public static IEnumerable<object?[]> RootMetadataCases()
    {
        yield return new object?[] { null, false };
        yield return new object?[] { "", false };
        yield return new object?[] { "{broken", false };
        yield return new object?[] { "{\"siteKey\":\"legend\",\"reportingOwner\":\"founder\"} trailing", false };
        yield return new object?[] { "[]", false };
        yield return new object?[] { "[{\"siteKey\":\"legend\",\"reportingOwner\":\"founder\"}]", false };
        yield return new object?[] { "\"legend\"", false };
        yield return new object?[] { "{}", false };
        yield return new object?[] { "{\"SITEKEY\":\"legend\",\"REPORTINGOWNER\":\"founder\"}", false };
        yield return new object?[] { "{\"siteKey\":null,\"reportingOwner\":null}", false };
        yield return new object?[] { "{\"siteKey\":{\"value\":\"legend\"},\"reportingOwner\":[\"founder\"]}", false };
        yield return new object?[] { "{\"siteKey\":\"protect\",\"siteKey\":\"legend\",\"reportingOwner\":\"agent\",\"reportingOwner\":\"founder\"}", false };
        yield return new object?[] { "{\"siteKey\":\"legend\",\"siteKey\":\"protect\",\"reportingOwner\":\"founder\",\"reportingOwner\":\"agent\"}", true };
        yield return new object?[] { "{\"siteKey\":\"legend\",\"reportingOwner\":\"founder\",\"extra\":{\"siteKey\":\"protect\"}}", true };
        yield return new object?[] { "{ \"extra\" : {}, \"reportingOwner\" : \"founder\", \"siteKey\" : \"legend\" }", true };
    }

    [Theory]
    [MemberData(nameof(RootMetadataCases))]
    public async Task CanonicalRootQueriesHandleHistoricalJsonWithoutNestedOrLaterKeyAuthority(string? metadata, bool matches)
    {
        using var db = ControllerTestHelpers.BuildDb();
        db.AnalyticsEvents.Add(new AnalyticsEvent { EventId = Guid.NewGuid(), EventType = "page_view", MetadataJson = metadata });
        db.MetaSignalEvents.Add(new MetaSignalEvent { EventId = Guid.NewGuid().ToString("N"), EventName = "page_view", MetadataJson = metadata });
        // WebsiteLead's persisted contract uses uppercase root keys.
        var leadMetadata = metadata?.Replace("\"siteKey\"", "\"SiteKey\"", StringComparison.Ordinal)
            .Replace("\"reportingOwner\"", "\"ReportingOwner\"", StringComparison.Ordinal);
        db.WebsiteLeads.Add(new WebsiteLead { LeadId = Guid.NewGuid(), FirstName = "Fixture", Email = "fixture@example.test", MetadataJson = leadMetadata });
        await db.SaveChangesAsync();
        var founder = new ScopeContext { ScopeType = ScopeType.Founder, AgentTrackingProfileId = Guid.NewGuid(), SiteKey = "legend" };
        var site = new ScopeContext { ScopeType = ScopeType.Global, SiteKey = "legend" };
        var owner = new ScopeContext { ScopeType = ScopeType.Global, ReportingOwner = "founder" };
        var expected = matches ? 1 : 0;
        Assert.Equal(expected, await SiteScoped(db.AnalyticsEvents.AsQueryable(), site).CountAsync());
        Assert.Equal(expected, await SiteScoped(db.MetaSignalEvents.AsQueryable(), site).CountAsync());
        Assert.Equal(expected, await SiteScoped(db.AnalyticsEvents.AsQueryable(), owner).CountAsync());
        Assert.Equal(expected, await SiteScoped(db.MetaSignalEvents.AsQueryable(), owner).CountAsync());
        Assert.Equal(expected, await db.AnalyticsEvents.Where(OwnerPredicate<AnalyticsEvent>("ScopePredicateEvents", founder)).CountAsync());
        Assert.Equal(expected, await db.MetaSignalEvents.Where(OwnerPredicate<MetaSignalEvent>("ScopePredicateMetaEvents", founder)).CountAsync());
        Assert.Equal(expected, await db.WebsiteLeads.Where(OwnerPredicate<WebsiteLead>("ScopePredicateLeads", founder)).CountAsync());
    }

    [Fact]
    public async Task SiteSwitchingPreservesTypedOwnerAliasesBusinessIsolationAndBroadLegacyScopes()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var selected = Guid.NewGuid();
        var alias = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var business = Guid.NewGuid();
        var rows = new (Guid? Agent, Guid? Business, string? Site, string? Metadata)[]
        {
            (selected, null, "legend", "{\"siteKey\":\"legend\",\"reportingOwner\":\"founder\"}"),
            (alias, null, "protect", "{\"siteKey\":\"protect\",\"reportingOwner\":\"founder\"}"),
            (foreign, null, "legend", "{\"siteKey\":\"legend\",\"reportingOwner\":\"founder\"}"),
            (null, business, "legend", "{\"siteKey\":\"legend\",\"reportingOwner\":\"founder\"}"),
            (selected, business, "legend", "{\"siteKey\":\"legend\",\"reportingOwner\":\"founder\"}"),
            (selected, null, null, null),
            (null, null, "legend", "{\"siteKey\":\"legend\",\"reportingOwner\":\"founder\"}")
        };
        foreach (var row in rows)
        {
            db.AnalyticsEvents.Add(new AnalyticsEvent { EventId = Guid.NewGuid(), EventType = "page_view", AgentTrackingProfileId = row.Agent, CommerceBusinessId = row.Business, MetadataJson = row.Metadata });
            db.MetaSignalEvents.Add(new MetaSignalEvent { EventId = Guid.NewGuid().ToString("N"), EventName = "page_view", AgentTrackingProfileId = row.Agent, CommerceBusinessId = row.Business, MetadataJson = row.Metadata });
            db.WebsiteLeads.Add(new WebsiteLead { LeadId = Guid.NewGuid(), FirstName = "Fixture", Email = "fixture@example.test", AgentTrackingProfileId = row.Agent, CommerceBusinessId = row.Business,
                MetadataJson = row.Metadata?.Replace("\"siteKey\"", "\"SiteKey\"").Replace("\"reportingOwner\"", "\"ReportingOwner\"") });
        }
        await db.SaveChangesAsync();
        var ids = new[] { selected, alias };
        foreach (var (site, expected) in new (string?, int)[] { ("legend", 2), ("protect", 1), (null, 4) })
        {
            var scope = new ScopeContext { ScopeType = ScopeType.Founder, AgentTrackingProfileId = selected, SiteKey = site };
            var events = await SiteScoped(db.AnalyticsEvents.Where(OwnerPredicate<AnalyticsEvent>("ScopePredicateEvents", scope, ids)), scope).ToListAsync();
            var signals = await SiteScoped(db.MetaSignalEvents.Where(OwnerPredicate<MetaSignalEvent>("ScopePredicateMetaEvents", scope, ids)), scope).ToListAsync();
            var leads = await db.WebsiteLeads.Where(OwnerPredicate<WebsiteLead>("ScopePredicateLeads", scope, ids)).ToListAsync();
            Assert.Equal(expected, events.Count);
            Assert.Equal(expected, signals.Count);
            Assert.Equal(expected, leads.Count);
            Assert.All(events, row => { Assert.Null(row.CommerceBusinessId); Assert.True(row.AgentTrackingProfileId == null || ids.Contains(row.AgentTrackingProfileId.Value)); });
            Assert.All(signals, row => { Assert.Null(row.CommerceBusinessId); Assert.True(row.AgentTrackingProfileId == null || ids.Contains(row.AgentTrackingProfileId.Value)); });
            Assert.All(leads, row => { Assert.Null(row.CommerceBusinessId); Assert.True(row.AgentTrackingProfileId == null || ids.Contains(row.AgentTrackingProfileId.Value)); });
        }
        var businessScope = new ScopeContext { ScopeType = ScopeType.Business, CommerceBusinessId = business };
        Assert.Single(await SiteScoped(db.AnalyticsEvents.Where(OwnerPredicate<AnalyticsEvent>("ScopePredicateEvents", businessScope)), businessScope).ToListAsync());
        Assert.Single(await SiteScoped(db.MetaSignalEvents.Where(OwnerPredicate<MetaSignalEvent>("ScopePredicateMetaEvents", businessScope)), businessScope).ToListAsync());
        Assert.Single(await db.WebsiteLeads.Where(OwnerPredicate<WebsiteLead>("ScopePredicateLeads", businessScope)).ToListAsync());
        var mixed = new ScopeContext { ScopeType = ScopeType.Business, CommerceBusinessId = business, AgentTrackingProfileId = selected };
        Assert.Empty(await SiteScoped(db.AnalyticsEvents.AsQueryable(), mixed).ToListAsync());
        Assert.Empty(await db.WebsiteLeads.Where(OwnerPredicate<WebsiteLead>("ScopePredicateLeads", mixed)).ToListAsync());
        Assert.Equal(rows.Length, await SiteScoped(db.AnalyticsEvents.AsQueryable(), ScopeContext.Global).CountAsync());
        Assert.Equal(rows.Length, await db.WebsiteLeads.Where(OwnerPredicate<WebsiteLead>("ScopePredicateLeads", ScopeContext.Global)).CountAsync());
        // Global site+owner retains the established OR contract, reading only root values.
        var union = new ScopeContext { ScopeType = ScopeType.Global, SiteKey = "missing", ReportingOwner = "founder" };
        Assert.Equal(6, await SiteScoped(db.AnalyticsEvents.AsQueryable(), union).CountAsync());
        Assert.Equal(6, await SiteScoped(db.MetaSignalEvents.AsQueryable(), union).CountAsync());
        Assert.Equal(2, await db.AnalyticsEvents.CountAsync(row => row.CommerceBusinessId == business));
        Assert.Equal(2, await db.WebsiteLeads.CountAsync(row => row.CommerceBusinessId == business));
    }

    [Fact]
    public void ActualScopedQueriesTranslateGuardedRootReadsForSqlServer()
    {
        using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlServer("Server=(local);Database=translation_only;Integrated Security=true;TrustServerCertificate=true").Options);
        var founder = new ScopeContext { ScopeType = ScopeType.Founder, AgentTrackingProfileId = Guid.NewGuid(), SiteKey = "legend" };
        var global = new ScopeContext { ScopeType = ScopeType.Global, SiteKey = "legend", ReportingOwner = "founder" };
        var queries = new Dictionary<string, string>
        {
            ["analytics-site"] = SiteScoped(db.AnalyticsEvents.AsQueryable(), global).ToQueryString(),
            ["meta-site"] = SiteScoped(db.MetaSignalEvents.AsQueryable(), global).ToQueryString(),
            ["analytics-founder"] = SiteScoped(db.AnalyticsEvents.Where(OwnerPredicate<AnalyticsEvent>("ScopePredicateEvents", founder)), founder).ToQueryString(),
            ["meta-founder"] = SiteScoped(db.MetaSignalEvents.Where(OwnerPredicate<MetaSignalEvent>("ScopePredicateMetaEvents", founder)), founder).ToQueryString(),
            ["lead-founder"] = db.WebsiteLeads.Where(OwnerPredicate<WebsiteLead>("ScopePredicateLeads", founder)).ToQueryString()
        };
        foreach (var (name, sql) in queries)
        {
            Assert.Matches(@"(?is)JSON_VALUE\(\s*CAST\(\s*CASE\b.*?\bEND\s+AS\s+nvarchar\(max\)\s*\)\s*,", sql);
            Assert.Matches(@"(?is)ISJSON\(\s*CAST\(\s*\[[^\]]+\]\.\[MetadataJson\]\s+AS\s+nvarchar\(max\)\s*\)\s*\)", sql);
            foreach (var function in new[] { "ISJSON", "JSON_VALUE" })
            {
                var calls = System.Text.RegularExpressions.Regex.Matches(sql, function + @"\(").Count;
                var casts = System.Text.RegularExpressions.Regex.Matches(sql, function + @"\(\s*CAST\(").Count;
                Assert.True(calls > 0);
                Assert.Equal(calls, casts);
            }
            Assert.Contains("= 1 THEN", sql);
            Assert.Contains("ELSE N'{}'", sql);
            Assert.DoesNotContain("CHARINDEX", sql);
            Assert.DoesNotContain(" LIKE ", sql);
            Assert.Contains(name == "lead-founder" ? "'$.SiteKey'" : "'$.siteKey'", sql);
            Assert.Contains(name == "lead-founder" ? "'$.ReportingOwner'" : "'$.reportingOwner'", sql);
            if (name.Contains("founder"))
            {
                Assert.Contains("[CommerceBusinessId] IS NULL", sql);
                Assert.Contains("[AgentTrackingProfileId]", sql);
            }
            Console.WriteLine($"SQL translation only: {name}\n{sql}");
        }
    }

    [Fact]
    public void SqlServerJsonScalarComparisonPreservesUnicodeLiteral()
    {
        using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlServer("Server=(local);Database=translation_only;Integrated Security=true;TrustServerCertificate=true").Options);
        var sql = db.AnalyticsEvents.Where(row => MasterAppDbContext.JsonValue(
            MasterAppDbContext.IsJson(row.MetadataJson) == 1 ? row.MetadataJson : "{}", "$.siteKey") == "legénd東京").ToQueryString();
        Assert.True(sql.Contains("N'legénd東京'", StringComparison.Ordinal), sql);
        Assert.Contains("nvarchar(max)", sql);
    }

    private static IQueryable<T> SiteScoped<T>(IQueryable<T> query, ScopeContext scope)
    {
        var owner = typeof(AnalyticsQueryService).Assembly.GetType("Infrastructure.Analytics.AnalyticsScopeQueryExtensions")!;
        var method = owner.GetMethods(BindingFlags.Public | BindingFlags.Static).Single(candidate => candidate.Name == "ApplySiteScope" &&
            candidate.GetParameters()[0].ParameterType == typeof(IQueryable<T>));
        return (IQueryable<T>)method.Invoke(null, new object[] { query, scope })!;
    }

    private static Expression<Func<T, bool>> OwnerPredicate<T>(string name, ScopeContext scope, Guid[]? ids = null) =>
        (Expression<Func<T, bool>>)typeof(AnalyticsQueryService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object?[] { scope, ids })!;
}
