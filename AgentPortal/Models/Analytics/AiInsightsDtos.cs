using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AgentPortal.Models.Analytics;

// ── Request DTOs ──────────────────────────────────────────────────────────────

public sealed class AiReviewRequestDto
{
    public string? Metric { get; set; }
    public string? Preset { get; set; }
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
    public string? TrafficType { get; set; }
    public Guid? AgentProfileId { get; set; }
    public bool Team { get; set; }
    public string? TimezoneId { get; set; }
    public int? TimezoneOffsetMinutes { get; set; }
}

public sealed class AiFollowUpRequestDto
{
    public string? Metric { get; set; }
    public string? Preset { get; set; }
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
    public string? TrafficType { get; set; }
    public Guid? AgentProfileId { get; set; }
    public bool Team { get; set; }
    public string? TimezoneId { get; set; }
    public int? TimezoneOffsetMinutes { get; set; }
    /// <summary>The follow-up question from the user. Max 500 chars; no PII; no HTML.</summary>
    public string FollowUpQuestion { get; set; } = "";
    /// <summary>The summary text from the prior AI response, included for context.</summary>
    public string? PriorSummary { get; set; }
}

// ── Result DTOs ───────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BreakpointSeverity
{
    Low,
    Medium,
    High,
    Critical
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BreakpointOwner
{
    Ad,
    LandingPage,
    Form,
    Tracking,
    FollowUp,
    Unknown
}

public sealed class BreakpointDto
{
    public string Title { get; set; } = "";
    public BreakpointSeverity Severity { get; set; } = BreakpointSeverity.Low;
    public List<string> Evidence { get; set; } = new();
    public string LikelyCause { get; set; } = "";
    public BreakpointOwner Owner { get; set; } = BreakpointOwner.Unknown;
}

public sealed class RecommendedActionDto
{
    public int Priority { get; set; }
    public string Action { get; set; } = "";
    public string Why { get; set; } = "";
    public string ExpectedImpact { get; set; } = "";
}

public sealed class TestToRunDto
{
    public string Name { get; set; } = "";
    public string Hypothesis { get; set; } = "";
    public string Metric { get; set; } = "";
}

public sealed class AiInsightsResultDto
{
    public string Summary { get; set; } = "";
    public List<BreakpointDto> PrimaryBreakpoints { get; set; } = new();
    public List<RecommendedActionDto> RecommendedActions { get; set; } = new();
    public List<TestToRunDto> TestsToRun { get; set; } = new();
    public List<string> ConfidenceNotes { get; set; } = new();
    /// <summary>True when the result represents an error/failure state rather than real analysis.</summary>
    public bool IsError { get; set; }
    /// <summary>Human-readable error message when IsError is true.</summary>
    public string? ErrorMessage { get; set; }
}

// ── Redacted payload sent to OpenAI (NO PII) ─────────────────────────────────

public sealed class AiSafeAnalyticsPayload
{
    public string RangeLabel { get; set; } = "";
    public string ScopeLabel { get; set; } = "";
    public string TrafficFilter { get; set; } = "";
    public List<string> Warnings { get; set; } = new();

    // Summary KPIs — aggregate counts only
    public int PageViews { get; set; }
    public int UniqueVisitors { get; set; }
    public int Sessions { get; set; }
    public int VerifiedLeads { get; set; }
    public decimal SessionConversionRate { get; set; }
    public decimal IntentConversionRate { get; set; }
    public bool IntentAvailable { get; set; }
    public string? TopPage { get; set; }
    public string? TopCta { get; set; }
    public string? TopSource { get; set; }
    public string? TopCampaign { get; set; }

    // Traffic breakdowns — page/source/campaign labels with counts
    public List<LabelCount> TopPages { get; set; } = new();
    public List<LabelCount> TopSources { get; set; } = new();
    public List<LabelCount> TopCampaigns { get; set; } = new();
    public List<LabelCount> EntryPages { get; set; } = new();

    // Page performance
    public List<PagePerfRow> PagePerformance { get; set; } = new();

    // CTA performance
    public List<CtaPerfRow> CtaPerformance { get; set; } = new();

    // Quote funnel
    public int QuoteStarts { get; set; }
    public int QuoteFormStarts { get; set; }
    public int QuoteFormSubmits { get; set; }
    public decimal? DropOffStartsToFormStarts { get; set; }
    public decimal? DropOffFormStartsToSubmits { get; set; }

    // Conversions
    public int TotalConversions { get; set; }

    // Behavior
    public double AvgSessionDurationMs { get; set; }
    public decimal? QuickExitRate { get; set; }
    public decimal? EngagedSessionRate { get; set; }
    public List<DwellRow> TopDwellPages { get; set; } = new();
    public List<ExitRow> TopExitPages { get; set; } = new();

    // Source performance
    public List<SourceRow> SourcePerformance { get; set; } = new();

    // Form abandonment
    public List<AbandonRow> FormAbandonment { get; set; } = new();
    public List<LabelCount> TopAbandonedFields { get; set; } = new();

    // Meta Ads — active campaigns only (Status == ACTIVE from Meta API), ordered by spend desc
    public List<AiCampaignRow> ActiveCampaigns { get; set; } = new();
}

// ── Nested safe row types ─────────────────────────────────────────────────────

public sealed class LabelCount
{
    public string Label { get; set; } = "";
    public int Count { get; set; }
}

public sealed class PagePerfRow
{
    public string PageKey { get; set; } = "";
    public int Views { get; set; }
    public int CtaClicks { get; set; }
    public int Leads { get; set; }
    public decimal ConversionRate { get; set; }
}

public sealed class CtaPerfRow
{
    public string PageKey { get; set; } = "";
    public string ElementKey { get; set; } = "";
    public int Clicks { get; set; }
}

public sealed class DwellRow
{
    public string PageKey { get; set; } = "";
    public double AvgDwellMs { get; set; }
    public int Samples { get; set; }
}

public sealed class ExitRow
{
    public string PageKey { get; set; } = "";
    public int Exits { get; set; }
    public decimal ExitRate { get; set; }
}

public sealed class SourceRow
{
    public string Source { get; set; } = "";
    public string? Medium { get; set; }
    public string? Campaign { get; set; }
    public int Sessions { get; set; }
    public int VerifiedLeads { get; set; }
    public decimal SessionConversionRate { get; set; }
}

public sealed class AbandonRow
{
    public string QuoteType { get; set; } = "";
    public int Abandons { get; set; }
    public int Starts { get; set; }
    public decimal? AbandonRate { get; set; }
}

/// <summary>
/// A single active Meta Ads campaign row — safe for AI consumption.
/// Only aggregate ad-delivery metrics; no PII.
/// </summary>
public sealed class AiCampaignRow
{
    public string CampaignName { get; set; } = "";
    public decimal Spend { get; set; }
    public long Impressions { get; set; }
    public long Clicks { get; set; }
    public decimal Ctr { get; set; }
    public decimal Cpc { get; set; }
    public long Leads { get; set; }
}
