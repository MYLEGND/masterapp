using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AgentPortal.Models.Analytics;
using AgentPortal.Services.Analytics;
using AgentPortal.Services.Tracking;
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

        var resolver = new AgentTrackingResolver(db, NullLogger<AgentTrackingResolver>.Instance);
        return new AnalyticsQueryService(db, config, resolver);
    }

    private static TimeRangeRequest BuildRange(DateTime nowUtc) => new()
    {
        FromUtc = nowUtc.AddHours(-2),
        ToUtc = nowUtc.AddHours(2),
        Grouping = TimeGrouping.Day,
        Label = "test",
        Preset = "custom"
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
        Assert.Equal(1, dto.InferredFormStarts);
        Assert.Equal(1, dto.MissingStartEventSessions);
        Assert.Equal(1, dto.UnknownAttributedLeads);
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
        Assert.Equal("facebook", mostRecent.Source);
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
}
