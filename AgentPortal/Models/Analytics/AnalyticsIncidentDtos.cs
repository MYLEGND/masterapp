using System;
using System.Collections.Generic;

namespace AgentPortal.Models.Analytics;

public sealed class AnalyticsIncidentMonitorDto
{
    public string ScopeLabel { get; set; } = "System-wide";
    public string RangeLabel { get; set; } = "Last 24 Hours";
    public DateTime LastUpdatedUtc { get; set; }
    public int ActiveIncidentCount { get; set; }
    public int ActiveCriticalCount { get; set; }
    public int ActiveHighCount { get; set; }
    public List<AnalyticsIncidentAlertDto> ActiveIncidents { get; set; } = new();
    public List<AnalyticsIncidentTimelineItemDto> Timeline { get; set; } = new();
    public List<AnalyticsIncidentFocusMetricDto> FocusMetrics { get; set; } = new();
    public AnalyticsIncidentAttributionHealthDto AttributionHealth { get; set; } = new();
    public AnalyticsIncidentFunnelHealthDto FunnelHealth { get; set; } = new();
}

public sealed class AnalyticsIncidentAlertDto
{
    public string EventType { get; set; } = "";
    public string Severity { get; set; } = "Low";
    public decimal CurrentValue { get; set; }
    public decimal BaselineValue { get; set; }
    public decimal DeviationPercent { get; set; }
    public string MetricUnit { get; set; } = "count";
    public string Category { get; set; } = "";
    public string? Summary { get; set; }
    public DateTime TimestampUtc { get; set; }
    public bool IsActive { get; set; }
}

public sealed class AnalyticsIncidentTimelineItemDto
{
    public string EventType { get; set; } = "";
    public string Severity { get; set; } = "Low";
    public string StatusLabel { get; set; } = "Detected";
    public decimal CurrentValue { get; set; }
    public decimal BaselineValue { get; set; }
    public decimal DeviationPercent { get; set; }
    public string MetricUnit { get; set; } = "count";
    public string? Summary { get; set; }
    public DateTime TimestampUtc { get; set; }
}

public sealed class AnalyticsIncidentFocusMetricDto
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public decimal CurrentValue { get; set; }
    public decimal PreviousValue { get; set; }
    public decimal DeltaPercent { get; set; }
    public string MetricUnit { get; set; } = "count";
}

public sealed class AnalyticsIncidentAttributionHealthDto
{
    public int EligibleEvents { get; set; }
    public int BrowserSentEvents { get; set; }
    public int ServerSentEvents { get; set; }
    public int MatchedEvents { get; set; }
    public decimal ServerBrowserMatchRate { get; set; }
    public int MissingAttributionEvents { get; set; }
    public decimal MissingAttributionRate { get; set; }
}

public sealed class AnalyticsIncidentFunnelHealthDto
{
    public int PageViews { get; set; }
    public int LeadFormStarts { get; set; }
    public int Leads { get; set; }
    public int Purchases { get; set; }
    public decimal PageViewToLeadStartRate { get; set; }
    public decimal LeadStartToLeadRate { get; set; }
    public decimal LeadToPurchaseRate { get; set; }
}
