using System;
using System.Collections.Generic;

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
}

public sealed class CtaPerformanceRow
{
    public string PageKey { get; set; } = "";
    public string ElementKey { get; set; } = "";
    public int Clicks { get; set; }
}

public sealed class CtaPerformanceDto
{
    public List<CtaPerformanceRow> Rows { get; set; } = new();
    public string RangeLabel { get; set; } = "";
}

public sealed class QuoteFunnelDto
{
    public int QuoteStarts { get; set; }
    public int QuoteFormStarts { get; set; }
    public int QuoteFormSubmits { get; set; }
    public List<KeyCountDto> ByQuoteType { get; set; } = new();
    public decimal? DropOffStartsToFormStarts { get; set; }
    public decimal? DropOffFormStartsToSubmits { get; set; }
    public string RangeLabel { get; set; } = "";
}

public sealed class ConversionRow
{
    public string EventType { get; set; } = "";
    public string? PageKey { get; set; }
    public string? SourceCta { get; set; }
    public DateTime EventUtc { get; set; }
}

public sealed class ConversionCenterDto
{
    public int TotalConversions { get; set; }
    public List<ConversionRow> Recent { get; set; } = new();
    public string RangeLabel { get; set; } = "";
}

public sealed class LeadSnapshotRow
{
    public DateTime CreatedUtc { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Phone { get; set; }
    public string? Interest { get; set; }
    public string? Source { get; set; }
}

public sealed class LeadSnapshotDto
{
    public List<LeadSnapshotRow> Leads { get; set; } = new();
    public int Total { get; set; }
    public string RangeLabel { get; set; } = "";
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
    public decimal AbandonRate { get; set; }
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
    public string RangeLabel { get; set; } = "";
}
