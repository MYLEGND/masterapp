namespace Shared.Analytics;

public sealed record ChannelEconomicsRow(
    string Channel,
    decimal Spend,
    long CustomersAcquired,
    decimal CostPerCustomer,
    decimal Revenue,
    decimal Roas,
    decimal PipelineValue,
    string AttributionBasis);

public sealed record BlendedGrowthEconomicsSnapshot(
    MarketingOwnerScope Owner,
    DateTime FromUtc,
    DateTime ToUtc,
    decimal TotalMarketingSpend,
    long CustomersAcquired,
    decimal CostPerCustomer,
    decimal TotalRevenue,
    decimal BlendedRoas,
    decimal PipelineValue,
    IReadOnlyList<ChannelEconomicsRow> Channels,
    IReadOnlyList<string> Notes);

public static class OpenAiProductFeedStatuses
{
    public const string Ready = "ready";
    public const string Published = "published";
    public const string Rejected = "rejected";
    public const string Error = "error";
    public const string Inactive = "inactive";
}

public sealed record OpenAiProductFeedItem(
    Guid CanonicalProductId,
    string ExternalProductKey,
    string Title,
    string Description,
    long PriceMicros,
    string Currency,
    string LandingUrl,
    string ImageUrl,
    bool Available,
    string CanonicalFingerprint);

public sealed record OpenAiProductFeedProjectionRow(
    Guid CanonicalProductId,
    string ProductName,
    string? ProviderFeedId,
    string? ProviderProductId,
    string Status,
    string? Error,
    DateTime? LastPublishedUtc,
    string CanonicalFingerprint);

public sealed record OpenAiProductFeedSnapshot(
    MarketingOwnerScope Owner,
    string? ProviderFeedId,
    int CanonicalProductCount,
    int EligibleProductCount,
    int PublishedProductCount,
    int ErrorCount,
    IReadOnlyList<OpenAiProductFeedProjectionRow> Products,
    IReadOnlyList<string> ValidationErrors);

public sealed record OpenAiProductFeedPublishReceipt(
    string ProviderFeedId,
    int Attempted,
    int Published,
    int Failed,
    IReadOnlyList<OpenAiProductFeedProjectionRow> Products);

public sealed record OpenAiAdsReadinessStep(
    string Key,
    string Label,
    bool Complete,
    string Status,
    string Detail);

public sealed record OpenAiAdsOnboardingSnapshot(
    MarketingOwnerScope Owner,
    string OverallStatus,
    bool ReadyToAdvertise,
    int CompletedSteps,
    int TotalSteps,
    OpenAiAdsConnectionSnapshot Connection,
    OpenAiMeasurementHealthSnapshot Measurement,
    IReadOnlyList<OpenAiAdsReadinessStep> Steps);
