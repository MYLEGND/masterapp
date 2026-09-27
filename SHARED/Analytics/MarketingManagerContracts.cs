using System.Text.Json;

namespace Shared.Analytics;

public static class MarketingChannels
{
    public const string ChatGptAds = "chatgpt_ads";
    public const string MetaAds = "meta_ads";
    public const string Organic = "organic";
    public const string Direct = "direct";
    public const string Referral = "referral";
    public const string Unknown = "unknown";
}

public sealed record MarketingManagerGoalRequest(
    string Goal,
    int? TargetIncrement = null,
    string? TargetOutcome = null,
    decimal? MonthlyBudget = null,
    string? Notes = null);

public sealed record MarketingManagerEvidence(
    string Key,
    string Label,
    string Value,
    string Source,
    bool Verified = true);

public sealed record MarketingManagerRecommendation(
    int Priority,
    string Lane,
    string Action,
    string Why,
    string ExpectedImpact,
    bool RequiresApproval,
    string? Provider = null,
    string? ProposalKind = null);

public sealed record MarketingManagerPlan(
    MarketingOwnerScope Owner,
    string Goal,
    DateTime GeneratedUtc,
    IReadOnlyList<PromotionSourceOption> PromotableSources,
    IReadOnlyList<MarketingManagerEvidence> Evidence,
    IReadOnlyList<MarketingManagerRecommendation> Recommendations,
    AdvertisingCommandCenterSummary Advertising,
    UnifiedChannelPerformanceSnapshot ChannelPerformance,
    IReadOnlyList<string> Guardrails);

public sealed record AdvertisingCommandCenterSummary(
    bool ChatGptAdsConnected,
    bool ChatGptAdsManagementReady,
    int CampaignCount,
    int PendingProposalCount,
    int ApprovedProposalCount);

public sealed record ProviderDeliveryMetricRow(
    string Provider,
    string Level,
    string ProviderId,
    string Name,
    string Status,
    string? CampaignId,
    string? AdGroupId,
    string? AdId,
    decimal Spend,
    long Impressions,
    long Clicks,
    long Conversions,
    JsonElement Raw);

public sealed record CanonicalOutcomeTotals(
    long Leads,
    long QualifiedLeads,
    long Appointments,
    long Customers,
    decimal Revenue);

public sealed record ChannelPerformanceRow(
    string Channel,
    decimal Spend,
    long Impressions,
    long Clicks,
    long Leads,
    long QualifiedLeads,
    long Appointments,
    long Customers,
    decimal Revenue,
    decimal Roas,
    string AttributionConfidence,
    string AttributionBasis);

public sealed record UnifiedChannelPerformanceSnapshot(
    MarketingOwnerScope Owner,
    DateTime FromUtc,
    DateTime ToUtc,
    DateTime GeneratedUtc,
    IReadOnlyList<ProviderDeliveryMetricRow> ChatGptAdsDelivery,
    CanonicalOutcomeTotals ChatGptAdsOutcomes,
    IReadOnlyList<ChannelPerformanceRow> Channels,
    IReadOnlyList<string> DataQualityNotes);
