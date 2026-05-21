using System;
using System.Collections.Generic;
using System.Linq;
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
        Assert.Contains(dto.Warnings, warning => warning.Contains("Client tracking errors detected", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(dto.Warnings, warning => warning.Contains("Missing funnel start events suspected", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(dto.Warnings, warning => warning.Contains("no-owner failures", StringComparison.OrdinalIgnoreCase));
    }
}
