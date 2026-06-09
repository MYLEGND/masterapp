using System;
using System.Collections.Generic;

namespace AgentPortal.Models.Analytics;

public sealed class MetaSignalDashboardDto
{
    public string RangeLabel { get; set; } = "";
    public string ScopeLabel { get; set; } = "";
    public string TrafficFilterLabel { get; set; } = "";
    public string LearningScopeNote { get; set; } = "";
    public bool HasEligiblePaidMetaTraffic { get; set; }
    public int TotalSignalEvents { get; set; }
    public int TotalVisitors { get; set; }
    public int HighIntentVisitors { get; set; }
    public int LeadReadyVisitors { get; set; }
    public int SubmittedLeads { get; set; }
    public int SubmitAttemptsWithoutLead { get; set; }
    public int HighIntentAbandons { get; set; }
    public int ContactStepAbandons { get; set; }
    public int ExcludedSignalEvents { get; set; }
    public int ExcludedSignalVisitors { get; set; }
    public decimal SignalToLeadConversionRate { get; set; }
    public string RecommendedOptimizationEvent { get; set; } = "LeadFormStart";
    public string BestPerformingLandingPageVersion { get; set; } = "—";
    public string WorstFrictionStep { get; set; } = "—";
    public List<string> AvailableQuoteTypes { get; set; } = new();
    public List<string> AvailableCampaigns { get; set; } = new();
    public List<string> AvailablePageModes { get; set; } = new();
    public List<string> AvailableScoreTiers { get; set; } = new();
    public List<MetaSignalValueRowDto> EventsByQuoteType { get; set; } = new();
    public List<MetaSignalValueRowDto> EventsByCampaign { get; set; } = new();
    public List<MetaSignalTierRowDto> VisitorsByScoreTier { get; set; } = new();
    public List<MetaSignalAverageRowDto> AverageScoreByCampaign { get; set; } = new();
    public List<MetaSignalAverageRowDto> AverageScoreByPageVariant { get; set; } = new();
    public List<MetaSignalLadderRowDto> EventLadder { get; set; } = new();
    public List<MetaSignalFrictionRowDto> FrictionHotspots { get; set; } = new();
    public List<MetaSignalDiagnosticEventRowDto> RecentDiagnostics { get; set; } = new();
}

public sealed class MetaSignalValueRowDto
{
    public string Label { get; set; } = "";
    public int Value { get; set; }
}

public sealed class MetaSignalTierRowDto
{
    public string ScoreTier { get; set; } = "";
    public int Visitors { get; set; }
}

public sealed class MetaSignalAverageRowDto
{
    public string Label { get; set; } = "";
    public decimal AverageScore { get; set; }
}

public sealed class MetaSignalLadderRowDto
{
    public string StepKey { get; set; } = "";
    public string StepLabel { get; set; } = "";
    public int Visitors { get; set; }
    public decimal? ProgressionRate { get; set; }
}

public sealed class MetaSignalFrictionRowDto
{
    public string Label { get; set; } = "";
    public int Count { get; set; }
}

public sealed class MetaSignalDiagnosticEventRowDto
{
    public DateTime CreatedUtc { get; set; }
    public string EventName { get; set; } = "";
    public string QuoteType { get; set; } = "";
    public string? CampaignLabel { get; set; }
    public string TrafficType { get; set; } = "";
    public bool IsPaidMetaAttributed { get; set; }
    public bool IsNonPaidOrManual { get; set; }
    public bool ExcludedFromMetaLearningReadiness { get; set; }
    public bool BrowserPixelSent { get; set; }
    public bool ServerCapiSent { get; set; }
    public bool DeduplicationEventIdPresent { get; set; }
    public string? MetaServerStatus { get; set; }
    public string LearningReason { get; set; } = "";
}

public sealed class MetaSignalAiSummaryDto
{
    public string LearningScopeNote { get; set; } = "";
    public int TotalSignalEvents { get; set; }
    public int TotalVisitors { get; set; }
    public int HighIntentVisitors { get; set; }
    public int LeadReadyVisitors { get; set; }
    public int SubmittedLeads { get; set; }
    public int SubmitAttemptsWithoutLead { get; set; }
    public int HighIntentAbandons { get; set; }
    public int ContactStepAbandons { get; set; }
    public decimal SignalToLeadConversionRate { get; set; }
    public string RecommendedOptimizationEvent { get; set; } = "LeadFormStart";
    public string BestPerformingLandingPageVersion { get; set; } = "—";
    public string WorstFrictionStep { get; set; } = "—";
    public List<MetaSignalTierRowDto> VisitorsByScoreTier { get; set; } = new();
    public List<MetaSignalAverageRowDto> AverageScoreByCampaign { get; set; } = new();
    public List<MetaSignalAverageRowDto> AverageScoreByPageVariant { get; set; } = new();
    public List<MetaSignalLadderRowDto> EventLadder { get; set; } = new();
}
