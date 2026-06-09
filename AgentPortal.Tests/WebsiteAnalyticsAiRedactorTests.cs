using System.Collections.Generic;
using AgentPortal.Models.Analytics;
using AgentPortal.Services.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public class WebsiteAnalyticsAiRedactorTests
{
    // ── Helper: build a minimal valid payload ─────────────────────────────────
    private static AiSafeAnalyticsPayload MinimalPayload() => new()
    {
        RangeLabel = "Last 30 Days",
        ScopeLabel = "Agent: Test Agent",
        TrafficFilter = "All",
        PageViews = 1200,
        UniqueVisitors = 800,
        Sessions = 950,
        VerifiedLeads = 42,
        SessionConversionRate = 4.42m,
        IntentConversionRate = 18.5m,
        IntentAvailable = true,
        TopPage = "/home",
        TopCta = "get-quote",
        TopSource = "facebook",
        TopCampaign = "summer-2024",
        QuoteStarts = 100,
        QuoteFormStarts = 80,
        QuoteFormSubmits = 52,
        TotalConversions = 42,
        AvgSessionDurationMs = 92000
    };

    // ── Test: aggregate numeric fields pass through unchanged ─────────────────
    [Fact]
    public void Redact_NumericAggregates_PassThrough()
    {
        var payload = MinimalPayload();
        payload.PageViews = 5432;
        payload.VerifiedLeads = 77;
        payload.SessionConversionRate = 3.14m;

        var result = WebsiteAnalyticsAiRedactor.Redact(payload);

        Assert.Equal(5432, result.PageViews);
        Assert.Equal(77, result.VerifiedLeads);
        Assert.Equal(3.14m, result.SessionConversionRate);
    }

    // ── Test: string containing @ is stripped from label fields ───────────────
    [Fact]
    public void Redact_EmailInLabelField_IsRedacted()
    {
        var payload = MinimalPayload();
        payload.TopSource = "john.smith@example.com";   // PII — must be stripped

        var result = WebsiteAnalyticsAiRedactor.Redact(payload);

        Assert.Equal("[redacted]", result.TopSource);
    }

    [Fact]
    public void Redact_EmailInLabelCount_EntryIsDropped()
    {
        var payload = MinimalPayload();
        payload.TopSources = new List<LabelCount>
        {
            new() { Label = "organic", Count = 100 },
            new() { Label = "lead@example.com", Count = 5 }  // PII row
        };

        var result = WebsiteAnalyticsAiRedactor.Redact(payload);

        // PII row must be dropped; clean row must remain
        Assert.Single(result.TopSources);
        Assert.Equal("organic", result.TopSources[0].Label);
    }

    // ── Test: phone number in label is stripped ───────────────────────────────
    [Fact]
    public void Redact_PhoneInLabel_IsRedacted()
    {
        var payload = MinimalPayload();
        payload.TopCampaign = "555-867-5309";

        var result = WebsiteAnalyticsAiRedactor.Redact(payload);

        Assert.Equal("[redacted]", result.TopCampaign);
    }

    // ── Test: all PII fields are stripped from source rows ───────────────────
    [Fact]
    public void Redact_EmailInSourceRow_IsRedacted()
    {
        var payload = MinimalPayload();
        payload.SourcePerformance = new List<SourceRow>
        {
            new()
            {
                Source = "facebook",
                Medium = "cpc",
                Campaign = "campaign-2024",
                Sessions = 200,
                VerifiedLeads = 10,
                SessionConversionRate = 5.0m
            },
            new()
            {
                Source = "user@example.com",  // PII — campaign attribution leak scenario
                Medium = null,
                Campaign = null,
                Sessions = 3,
                VerifiedLeads = 0,
                SessionConversionRate = 0m
            }
        };

        var result = WebsiteAnalyticsAiRedactor.Redact(payload);

        Assert.Equal(2, result.SourcePerformance.Count);
        Assert.Equal("facebook", result.SourcePerformance[0].Source);
        Assert.Equal("[redacted]", result.SourcePerformance[1].Source);
    }

    // ── Test: empty payload does not crash ────────────────────────────────────
    [Fact]
    public void Redact_EmptyPayload_DoesNotThrow()
    {
        var payload = new AiSafeAnalyticsPayload();
        var ex = Record.Exception(() => WebsiteAnalyticsAiRedactor.Redact(payload));
        Assert.Null(ex);
    }

    // ── Test: null payload throws ArgumentNullException ───────────────────────
    [Fact]
    public void Redact_NullPayload_ThrowsArgumentNull()
    {
        Assert.Throws<System.ArgumentNullException>(() =>
            WebsiteAnalyticsAiRedactor.Redact(null!));
    }

    // ── Test: valid page key passes through ───────────────────────────────────
    [Fact]
    public void Redact_ValidPageKeys_PassThrough()
    {
        var payload = MinimalPayload();
        payload.PagePerformance = new List<PagePerfRow>
        {
            new() { PageKey = "/home", Views = 500, CtaClicks = 20, Leads = 5, ConversionRate = 1.0m },
            new() { PageKey = "/auto-insurance", Views = 300, CtaClicks = 15, Leads = 8, ConversionRate = 2.67m }
        };

        var result = WebsiteAnalyticsAiRedactor.Redact(payload);

        Assert.Equal(2, result.PagePerformance.Count);
        Assert.Equal("/home", result.PagePerformance[0].PageKey);
        Assert.Equal(500, result.PagePerformance[0].Views);
    }

    // ── Test: LooksPii detection ──────────────────────────────────────────────
    [Theory]
    [InlineData("john@example.com", true)]
    [InlineData("555-867-5309", true)]
    [InlineData("(555) 867-5309", true)]
    [InlineData("facebook", false)]
    [InlineData("campaign-2024", false)]
    [InlineData("/home", false)]
    [InlineData("organic", false)]
    [InlineData("", false)]
    public void LooksPii_Detects_Correctly(string value, bool expectedPii)
    {
        Assert.Equal(expectedPii, WebsiteAnalyticsAiRedactor.LooksPii(value));
    }

    // ── Test: collections with all-clean data pass through intact ────────────
    [Fact]
    public void Redact_CleanCollections_PassThroughIntact()
    {
        var payload = MinimalPayload();
        payload.TopPages = new List<LabelCount>
        {
            new() { Label = "/home", Count = 400 },
            new() { Label = "/auto", Count = 200 }
        };
        payload.TopAbandonedFields = new List<LabelCount>
        {
            new() { Label = "zip_code", Count = 30 },
            new() { Label = "vehicle_year", Count = 18 }
        };

        var result = WebsiteAnalyticsAiRedactor.Redact(payload);

        Assert.Equal(2, result.TopPages.Count);
        Assert.Equal(2, result.TopAbandonedFields.Count);
        Assert.Equal("/home", result.TopPages[0].Label);
        Assert.Equal("zip_code", result.TopAbandonedFields[0].Label);
    }
}
