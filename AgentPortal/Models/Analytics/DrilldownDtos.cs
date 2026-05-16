using System;
using System.Collections.Generic;
using AgentPortal.Services.Analytics;

namespace AgentPortal.Models.Analytics;

public sealed class TrafficOverviewDto
{
    public List<TrendPointDto> PageViewTrend { get; set; } = new();
    public List<TrendPointDto> SessionTrend { get; set; } = new();
    public List<TrendPointDto> VisitorTrend { get; set; } = new();
    public List<KeyCountDto> TopPages { get; set; } = new();
    public List<KeyCountDto> TopCtas { get; set; } = new();
    public List<KeyCountDto> EntryPages { get; set; } = new();
    public List<ActivityItemDto> RecentActivity { get; set; } = new();
    public List<KeyCountDto> TopSources { get; set; } = new();
    public List<KeyCountDto> TopCampaigns { get; set; } = new();
    public string RangeLabel { get; set; } = "";
    public TrafficType TrafficType { get; set; }
}

public sealed class ActivityItemDto
{
    public DateTime EventUtc { get; set; }
    public string EventType { get; set; } = "";
    public string? PageKey { get; set; }
    public string? ElementKey { get; set; }
}

public sealed class PagePerformanceRow
{
    public string PageKey { get; set; } = "";
    public int Views { get; set; }
    public int CtaClicks { get; set; }
    public int Leads { get; set; }
    public decimal ConversionRate { get; set; }
}

public sealed class PagePerformanceDto
{
    public List<PagePerformanceRow> Rows { get; set; } = new();
    public string RangeLabel { get; set; } = "";
    public TrafficType TrafficType { get; set; }
}

public sealed class CtaPerformanceRow
{
    public string PageKey { get; set; } = "";
    public string ElementKey { get; set; } = "";
    public int Clicks { get; set; }
    public int UniqueClickSessions { get; set; }
    public int VerifiedLeads { get; set; }
    public decimal? ClickToLeadRate { get; set; }
}

public sealed class CtaPerformanceDto
{
    public List<CtaPerformanceRow> Rows { get; set; } = new();
    public string RangeLabel { get; set; } = "";
    public TrafficType TrafficType { get; set; }
}

public sealed class QuoteFunnelDto
{
    public int QuoteStarts { get; set; }
    public int QuoteFormStarts { get; set; }
    public int QuoteSubmitAttempts { get; set; }
    public int QuoteFormSubmits { get; set; }
    public List<KeyCountDto> ByQuoteType { get; set; } = new();
    public List<QuoteStageMetricRow> StageMetrics { get; set; } = new();
    public decimal? DropOffStartsToFormStarts { get; set; }
    public decimal? DropOffFormStartsToSubmits { get; set; }
    public string RangeLabel { get; set; } = "";
    public TrafficType TrafficType { get; set; }
}

public sealed class QuoteStageMetricRow
{
    public string StageKey { get; set; } = "";
    public string Label { get; set; } = "";
    public int Count { get; set; }
}

public sealed class ConversionRow
{
    public string EventType { get; set; } = "";
    public string? PageKey { get; set; }
    public string? SourceCta { get; set; }
    public DateTime EventUtc { get; set; }
    public string? QuoteType { get; set; }
    public string? SourceLabel { get; set; }
}

public sealed class ConversionCenterDto
{
    public int TotalConversions { get; set; }
    public List<ConversionRow> Recent { get; set; } = new();
    public string RangeLabel { get; set; } = "";
    public TrafficType TrafficType { get; set; }
}

/// <summary>
/// Pre-classified attribution state for a single lead row.
/// Serialises to { isPaid, isNonPaid, trafficType } for the frontend trafficBadge() function.
/// </summary>
public sealed class LeadAttributionDto
{
    /// <summary>True when the lead arrived via a paid-ads channel (PPC, Meta click, etc.).</summary>
    public bool IsPaid { get; set; }
    /// <summary>True when the lead arrived via any non-paid channel (organic, direct, referral).</summary>
    public bool IsNonPaid { get; set; }
    /// <summary>Granular classification for display/debugging.</summary>
    public TrafficType TrafficType { get; set; }
    /// <summary>lead | session | visitor | unknown</summary>
    public string ResolutionSource { get; set; } = "unknown";
}

public sealed class LeadSnapshotRow
{
    public Guid LeadId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Phone { get; set; }
    public string? Interest { get; set; }
    public string? LeadSource { get; set; }
    public TrafficType TrafficType { get; set; }
    public string? UtmSource { get; set; }
    public string? UtmMedium { get; set; }
    public string? UtmCampaign { get; set; }
    public string? UtmId { get; set; }
    public string? Fbclid { get; set; }
    public string? MetaCampaignId { get; set; }
    public string? MetaAdSetId { get; set; }
    public string? MetaAdId { get; set; }
    public string? ResolvedSource { get; set; }
    public string? ResolvedMedium { get; set; }
    public string? ResolvedCampaign { get; set; }
    public string? ResolvedUtmId { get; set; }
    public string? ResolvedContent { get; set; }
    public string? ResolvedTerm { get; set; }
    public bool ResolvedFbclidPresent { get; set; }
    public string? ResolvedMetaCampaignId { get; set; }
    public string? ResolvedMetaAdSetId { get; set; }
    public string? ResolvedMetaAdId { get; set; }
    public string? LandingPage { get; set; }
    public string? SourcePage { get; set; }
    /// <summary>Server-classified attribution — consumed by trafficBadge() in the frontend.</summary>
    public LeadAttributionDto? Attribution { get; set; }
    public MetaLeadTrackingDto? MetaTracking { get; set; }
}

public sealed class LeadSnapshotDto
{
    public List<LeadSnapshotRow> Leads { get; set; } = new();
    public int Total { get; set; }
    public int ReturnedCount { get; set; }
    public bool IsTruncated { get; set; }
    public string RangeLabel { get; set; } = "";
    public TrafficType TrafficType { get; set; }
}

public sealed class MetaLeadTrackingDto
{
    public string? EventId { get; set; }
    public string? BrowserPixelStatus { get; set; }
    public string? ServerCapiStatus { get; set; }
    public bool BrowserPixelSent { get; set; }
    public bool ServerCapiSent { get; set; }
    public bool DedupReady { get; set; }
}

// ── Form Abandonment ──────────────────────────────────────────────────────────

public sealed class TopAbandonedFieldRow
{
    public string FieldName { get; set; } = "";
    public int AbandonCount { get; set; }
    public string? QuoteType { get; set; }
}

public sealed class LastCompletedFieldRow
{
    public string FieldName { get; set; } = "";
    public int Count { get; set; }
    public string? QuoteType { get; set; }
}

public sealed class ValidationFrictionRow
{
    public string FieldName { get; set; } = "";
    public int ErrorCount { get; set; }
    public string? QuoteType { get; set; }
}

public sealed class FormAbandonSummaryRow
{
    public string QuoteType { get; set; } = "";
    public int Abandons { get; set; }
    public int Starts { get; set; }
    public decimal? AbandonRate { get; set; }
    public bool StartSignalMissing { get; set; }
    public double AvgCompletedFields { get; set; }
    public int SubmitAttemptedAbandonCount { get; set; }
}

public sealed class FormAbandonmentDto
{
    public List<FormAbandonSummaryRow> Summary { get; set; } = new();
    public List<TopAbandonedFieldRow> TopAbandonedFields { get; set; } = new();
    public List<LastCompletedFieldRow> TopLastCompletedFields { get; set; } = new();
    public List<ValidationFrictionRow> ValidationFriction { get; set; } = new();
    /// <summary>Count of abandons where submit was attempted at least once before leaving.</summary>
    public int ConsentFrictionCount { get; set; }
    public int StartSignalGapQuoteTypeCount { get; set; }
    public string? DataQualityNote { get; set; }
    public string RangeLabel { get; set; } = "";
}
