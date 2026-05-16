using System;
using System.Collections.Generic;

namespace AgentPortal.Models.Analytics;

public sealed class SummaryKpiDto
{
    public int PageViews { get; set; }
    public int UniqueVisitors { get; set; }
    public int Sessions { get; set; }
    public int VerifiedLeads { get; set; }
    public decimal SessionConversionRate { get; set; }
    public bool SessionLowSample { get; set; }
    public decimal IntentConversionRate { get; set; }
    public string IntentDenominatorLabel { get; set; } = "";
    public bool IntentAvailable { get; set; }
    public bool IntentLowSample { get; set; }
    public string EnvironmentLabel { get; set; } = "";
    public string ScopeLabel { get; set; } = "";
    public int PrevPageViews { get; set; }
    public int PrevUniqueVisitors { get; set; }
    public int PrevSessions { get; set; }
    public int PrevVerifiedLeads { get; set; }
    public List<TrendPointDto> PageViewTrend { get; set; } = new();
    public string? TopPage { get; set; }
    public string? TopCta { get; set; }
    public string? TopSource { get; set; }
    public string? TopCampaign { get; set; }
    public string RangeLabel { get; set; } = "";
}

public sealed class TrendPointDto
{
    public string Label { get; set; } = "";
    public int Value { get; set; }
}

public sealed class KeyCountDto
{
    public string Key { get; set; } = "";
    public int Count { get; set; }
}

public sealed class Key2CountDto
{
    public string Key1 { get; set; } = "";
    public string Key2 { get; set; } = "";
    public int Count { get; set; }
}

public sealed class AgentPerformanceRow
{
    public Guid AgentTrackingProfileId { get; set; }
    public string? AgentName { get; set; }
    public string? Slug { get; set; }
    public int Leads { get; set; }
    public int Conversions { get; set; }
    public int Sessions { get; set; }
    public decimal SessionConversionRate { get; set; }
    public decimal IntentConversionRate { get; set; }
    public string? TopSource { get; set; }
    public bool LowSample { get; set; }
}

public sealed class AgentPerformanceDto
{
    public List<AgentPerformanceRow> Rows { get; set; } = new();
    public string? RangeLabel { get; set; }
}
