using System;
using System.Collections.Generic;

namespace AgentPortal.Models.Analytics;

public sealed class MetaSignalHealthDashboardDto
{
    public string RangeLabel { get; set; } = "Current Range";
    public string ScopeLabel { get; set; } = "";
    public DateTime LastUpdatedUtc { get; set; }
    public int DispatcherGraceMinutes { get; set; }
    public MetaSignalHealthPipelineSummaryDto PipelineHealth { get; set; } = new();
    public List<MetaSignalHealthMetricDto> FlowIntegrity { get; set; } = new();
    public List<MetaSignalHealthIssueDto> FailureDetection { get; set; } = new();
    public List<MetaSignalHealthRecentEventRowDto> RecentEvents { get; set; } = new();
}

public sealed class MetaSignalHealthPipelineSummaryDto
{
    public int AnalyticsEventsLast24Hours { get; set; }
    public int BridgeEligibleAnalyticsEventsLast24Hours { get; set; }
    public int BridgeOwnedMetaSignalEventsLast24Hours { get; set; }
    public int MetaSignalEventsLast24Hours { get; set; }
    public int WebsiteLeadsLast24Hours { get; set; }
    public int MetaServerSentCount { get; set; }
    public int MetaBrowserSentCount { get; set; }
}

public sealed class MetaSignalHealthMetricDto
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public int Numerator { get; set; }
    public int Denominator { get; set; }
    public decimal Rate { get; set; }
    public string Status { get; set; } = "NoData";
    public string Detail { get; set; } = "";
}

public sealed class MetaSignalHealthIssueDto
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public int Count { get; set; }
    public string Status { get; set; } = "Healthy";
    public string Detail { get; set; } = "";
}

public sealed class MetaSignalHealthRecentEventRowDto
{
    public DateTime CreatedUtc { get; set; }
    public string EventType { get; set; } = "";
    public string EventName { get; set; } = "";
    public string SourceLabel { get; set; } = "";
    public string? SessionId { get; set; }
    public Guid? LeadId { get; set; }
    public string FunnelStep { get; set; } = "";
    public bool MetaBrowserSent { get; set; }
    public bool MetaServerSent { get; set; }
    public string DispatcherStatus { get; set; } = "";
    public string AuthorityStatus { get; set; } = "";
    public string MetaServerStatus { get; set; } = "";
}
