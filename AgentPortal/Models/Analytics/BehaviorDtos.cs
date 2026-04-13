using System;
using System.Collections.Generic;
using AgentPortal.Services.Analytics;

namespace AgentPortal.Models.Analytics;

// ── Engagement Summary ────────────────────────────────────────────────────────

public sealed class EngagementSummaryDto
{
    public double AvgSessionDurationMs { get; set; }
    public double MedianSessionDurationMs { get; set; }
    public double AvgTimeOnPageMs { get; set; }
    public double MedianTimeOnPageMs { get; set; }
    /// <summary>% of sessions whose final page_exit appears to be a quick exit (dwell < 10 s).
    /// Null when no final page_exit data exists yet (instrumentation not yet producing data).</summary>
    public decimal? QuickExitRate { get; set; }
    /// <summary>% of sessions with ≥30 s of accumulated engaged time.
    /// Null when no engagement event or EngagedMilliseconds data exists yet.</summary>
    public decimal? EngagedSessionRate { get; set; }
    public string? TopExitPage { get; set; }
    public string? TopLongDwellPage { get; set; }
    public string? HighestScrollCompletionPage { get; set; }
    /// <summary>Total page views in range (for context).</summary>
    public int TotalPageViews { get; set; }
    /// <summary>Total sessions in range.</summary>
    public int TotalSessions { get; set; }
    public string RangeLabel { get; set; } = "";
    public TrafficType TrafficType { get; set; }
}

// ── Page Engagement Table ─────────────────────────────────────────────────────

public sealed class PageEngagementRow
{
    public string PageKey { get; set; } = "";
    public int Views { get; set; }
    public int UniqueVisitors { get; set; }
    public double AvgTimeMs { get; set; }
    public double MedianTimeMs { get; set; }
    public decimal Scroll50Rate { get; set; }
    public decimal Scroll90Rate { get; set; }
    public decimal ExitRate { get; set; }
    public decimal CtaClickRate { get; set; }
    public decimal FormStartRate { get; set; }
    public decimal LeadRate { get; set; }
    public DateTime? LastActivity { get; set; }
}

public sealed class PageEngagementDto
{
    public List<PageEngagementRow> Rows { get; set; } = new();
    public string RangeLabel { get; set; } = "";
}

// ── Time on Page Breakdown ────────────────────────────────────────────────────

public sealed class DwellPageRow
{
    public string PageKey { get; set; } = "";
    public int Views { get; set; }
    /// <summary>Number of page_exit rows with valid dwell timing used in avg/median calculations.</summary>
    public int TimingSamples { get; set; }
    public double AvgDwellMs { get; set; }
    public double MedianDwellMs { get; set; }
}

public sealed class TimeOnPageDto
{
    public List<DwellPageRow> LongestAvgDwell { get; set; } = new();
    public List<DwellPageRow> LongestMedianDwell { get; set; } = new();
    public List<DwellPageRow> ShortVisitProblemPages { get; set; } = new();
    public int TotalPageViews { get; set; }
    public int TotalTimingSamples { get; set; }
    public string RangeLabel { get; set; } = "";
}

// ── Exit Analysis ─────────────────────────────────────────────────────────────

public sealed class ExitPageRow
{
    public string PageKey { get; set; } = "";
    public int Views { get; set; }
    public int Exits { get; set; }
    public decimal ExitRate { get; set; }
}

public sealed class ExitAnalysisDto
{
    public List<ExitPageRow> TopExitPages { get; set; } = new();
    public List<KeyCountDto> QuickExitPages { get; set; } = new();
    public string RangeLabel { get; set; } = "";
}

// ── Scroll Analysis ───────────────────────────────────────────────────────────

public sealed class ScrollPageRow
{
    public string PageKey { get; set; } = "";
    public int Views { get; set; }
    public decimal Scroll25Rate { get; set; }
    public decimal Scroll50Rate { get; set; }
    public decimal Scroll75Rate { get; set; }
    public decimal Scroll90Rate { get; set; }
    public decimal Scroll100Rate { get; set; }
}

public sealed class ScrollAnalysisDto
{
    public List<ScrollPageRow> Rows { get; set; } = new();
    public string RangeLabel { get; set; } = "";
}

// ── Journey / Path Analysis ───────────────────────────────────────────────────

public sealed class JourneyAnalysisDto
{
    /// <summary>Most common entry pages.</summary>
    public List<KeyCountDto> TopLandingPages { get; set; } = new();
    /// <summary>Pages commonly visited just before a verified lead conversion.</summary>
    public List<KeyCountDto> PagesBeforeLead { get; set; } = new();
    /// <summary>Most common exit pages (last page before session end).</summary>
    public List<KeyCountDto> CommonDropOffPages { get; set; } = new();
    public string RangeLabel { get; set; } = "";
}

// ── Source / Attribution ──────────────────────────────────────────────────────

public sealed class SourcePerformanceRow
{
    public string Source { get; set; } = "unattributed";
    public string? Medium { get; set; }
    public string? Campaign { get; set; }
    public string? LandingPage { get; set; }
    public int Sessions { get; set; }
    public int EngagedSessions { get; set; }
    public int VerifiedLeads { get; set; }
    public decimal SessionConversionRate { get; set; }
    public decimal LandingPageConversionRate { get; set; }
    public double AvgDwellMs { get; set; }
}

public sealed class SourcePerformanceDto
{
    public List<SourcePerformanceRow> Rows { get; set; } = new();
    public string RangeLabel { get; set; } = "";
    public TrafficType TrafficType { get; set; }
}

// ── Landing Page Performance ──────────────────────────────────────────────────

public sealed class LandingPagePerformanceRow
{
    public string PageKey { get; set; } = "";
    public int Sessions { get; set; }
    public int EngagedSessions { get; set; }
    public double AvgDwellMs { get; set; }
    public int VerifiedLeads { get; set; }
    public decimal ConversionRate { get; set; }
    public string? TopSource { get; set; }
    public string? TopCampaign { get; set; }
}

public sealed class LandingPagePerformanceDto
{
    public List<LandingPagePerformanceRow> Rows { get; set; } = new();
    public string RangeLabel { get; set; } = "";
}

// ── Form Friction ─────────────────────────────────────────────────────────────

public sealed class FormFrictionRow
{
    public string FormKey { get; set; } = "";
    public int Starts { get; set; }
    public int Submits { get; set; }
    public int Abandons { get; set; }
    public decimal CompletionRate { get; set; }
    public int FieldFocuses { get; set; }
    /// <summary>Count of form_field_complete events (unique field interactions that produced a value).</summary>
    public int FieldCompletes { get; set; }
    /// <summary>Count of form_field_error events (validation failures). Replaces the old form_field_abandon metric which was never emitted.</summary>
    public int FieldErrors { get; set; }
}

public sealed class FormFrictionDto
{
    public List<FormFrictionRow> Rows { get; set; } = new();
    /// <summary>Fields with the most validation errors (form_field_error), across all forms.</summary>
    public List<KeyCountDto> TopErrorFields { get; set; } = new();
    public string RangeLabel { get; set; } = "";
}
