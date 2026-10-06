namespace Shared.Analytics;

public sealed class AiSafeAnalyticsPayload
{
    public string SchemaVersion { get; set; } = "marketing-context.v1";
    public DateTime GeneratedUtc { get; set; }
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    public string QualityMode { get; set; } = "";
    public List<AiChannelRow> Channels { get; set; } = [];
    public List<AiCampaignRow> ChatGptCampaigns { get; set; } = [];
    public List<string> ChannelCoverageNotes { get; set; } = [];
    public List<AiDeviceRow> Devices { get; set; } = [];
    public List<AiDeviceRow> OperatingSystems { get; set; } = [];
    public List<AiDeviceRow> Viewports { get; set; } = [];
    public List<AiDeviceRow> Languages { get; set; } = [];
    public List<AiDeviceRow> Browsers { get; set; } = [];
    public List<LabelCount> PagesBeforeLead { get; set; } = [];
    public List<LabelCount> CommonDropOffPages { get; set; } = [];
    public List<PromotionSourceOption> PublishedSources { get; set; } = [];

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

    // Meta Signal Intelligence
    public MetaSignalAiPayload? MetaSignal { get; set; }

    // Tracking + pipeline health
    public MarketingHealthAiPayload? MarketingHealth { get; set; }

    // Outcome calibration — observed browser intent is a feature; canonical
    // server outcomes are the labels. This is historical selected-window
    // evidence, never a claim that a heuristic score itself is a conversion.
    public OutcomeCalibrationAiPayload? OutcomeCalibration { get; set; }
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

public sealed class MetaSignalAiPayload
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
    public string RecommendedOptimizationEvent { get; set; } = "";
    public string BestPerformingLandingPageVersion { get; set; } = "";
    public string WorstFrictionStep { get; set; } = "";
    public List<MetaSignalTierAiRow> VisitorsByScoreTier { get; set; } = new();
    public List<MetaSignalAverageAiRow> AverageScoreByCampaign { get; set; } = new();
    public List<MetaSignalAverageAiRow> AverageScoreByPageVariant { get; set; } = new();
    public List<MetaSignalLadderAiRow> EventLadder { get; set; } = new();
}

public sealed class MarketingHealthAiPayload
{
    public string MetaHealthStatus { get; set; } = "Unverified";
    public int ClientTrackingErrors { get; set; }
    public int ClientTrackingErrorSessions { get; set; }
    public int InferredFormStarts { get; set; }
    public int MissingStartEventSessions { get; set; }
    public int LeadPersistedEvents { get; set; }
    public int WorkstationCaptureAttempts { get; set; }
    public int WorkstationCaptureSuccesses { get; set; }
    public int WorkstationCaptureFailures { get; set; }
    public int WorkstationNoOwnerFailures { get; set; }
    public int UnknownAttributedLeads { get; set; }
    public int InternalTrafficSessions { get; set; }
    public int TestTrafficSessions { get; set; }
    public int BotSuspiciousSessions { get; set; }
    public List<string> Warnings { get; set; } = new();
}

public sealed class MetaSignalTierAiRow
{
    public string ScoreTier { get; set; } = "";
    public int Visitors { get; set; }
}

public sealed class MetaSignalAverageAiRow
{
    public string Label { get; set; } = "";
    public decimal AverageScore { get; set; }
}

public sealed class MetaSignalLadderAiRow
{
    public string StepLabel { get; set; } = "";
    public int Visitors { get; set; }
    public decimal? ProgressionRate { get; set; }
}

public sealed class OutcomeCalibrationAiPayload
{
    public string LearningScopeNote { get; set; } = "";
    public List<SignalOutcomeCalibrationAiRow> Signals { get; set; } = new();
}

public sealed class SignalOutcomeCalibrationAiRow
{
    public string Signal { get; set; } = "";
    public int ObservedVisitors { get; set; }
    public int QualifiedLeads { get; set; }
    public int Appointments { get; set; }
    public int Applications { get; set; }
    public int PoliciesIssued { get; set; }
    public int PaidCustomers { get; set; }
    public decimal QualifiedRate { get; set; }
    public decimal AppointmentRate { get; set; }
    public decimal ApplicationRate { get; set; }
    public decimal IssuedRate { get; set; }
    public decimal PaidRate { get; set; }
    public decimal ObservedRevenue { get; set; }
    public decimal ExpectedRevenuePerObservedVisitor { get; set; }
}

public sealed record AiChannelRow(string Channel, decimal? Spend, long Impressions, long Clicks,
    long Leads, long QualifiedLeads, long Appointments, long Customers, decimal Revenue, decimal? Roas, string AttributionConfidence);
public sealed record AiDeviceRow(string Label, int Sessions, int Events, int CtaClicks, int FormStarts, int SubmitAttempts, int ConfirmedLeads);
