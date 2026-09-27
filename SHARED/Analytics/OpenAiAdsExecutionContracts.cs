using System.Text.Json;

namespace Shared.Analytics;

public static class OpenAiAdsEntityStatuses
{
    public const string Active = "active";
    public const string Paused = "paused";
    public const string Archived = "archived";

    public static string NormalizeCreate(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is Active or Paused
            ? normalized
            : throw new ArgumentException("OpenAI Ads create status must be active or paused.", nameof(value));
    }

    public static string NormalizeUpdate(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is Active or Paused or Archived
            ? normalized
            : throw new ArgumentException("OpenAI Ads status must be active, paused, or archived.", nameof(value));
    }
}

public static class OpenAiAdsBiddingTypes
{
    public const string Impressions = "impressions";
    public const string Clicks = "clicks";
    public const string Conversions = "conversions";

    public static string Normalize(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is Impressions or Clicks or Conversions
            ? normalized
            : throw new ArgumentException("OpenAI Ads bidding type must be impressions, clicks, or conversions.", nameof(value));
    }
}

public static class OpenAiAdsBiddingStrategies
{
    public const string FixedBid = "fixed_bid";
    public const string MaximizeClicks = "maximize_clicks";
    public const string MaximizeConversions = "maximize_conversions";
}

public static class OpenAiAdsBillingEvents
{
    public const string Impression = "impression";
    public const string Click = "click";
}

public static class OpenAiAdsCreativeTypes
{
    public const string ChatCard = "chat_card";
    public const string ProductAdTemplate = "product_ad_template";
}

public sealed record OpenAiAdsBudget(
    long? DailySpendLimitMicros = null,
    long? LifetimeSpendLimitMicros = null)
{
    public void Validate()
    {
        var count = (DailySpendLimitMicros.HasValue ? 1 : 0) + (LifetimeSpendLimitMicros.HasValue ? 1 : 0);
        if (count != 1)
            throw new ArgumentException("Exactly one OpenAI Ads budget type is required.");
        if (DailySpendLimitMicros is <= 0 || LifetimeSpendLimitMicros is <= 0)
            throw new ArgumentException("OpenAI Ads budget must be greater than zero.");
    }
}

public sealed record OpenAiAdsGeoTarget(
    string Id,
    string? Name = null,
    string? Type = null,
    string? CountryCode = null,
    string? RegionCode = null);

public sealed record OpenAiAdsTargeting(
    IReadOnlyList<string>? Countries = null,
    IReadOnlyList<OpenAiAdsGeoTarget>? IncludedLocations = null,
    IReadOnlyList<string>? ExcludedCountries = null,
    IReadOnlyList<OpenAiAdsGeoTarget>? ExcludedLocations = null,
    IReadOnlyList<string>? CustomAudienceIds = null,
    IReadOnlyList<string>? ExcludedCustomAudienceIds = null,
    IReadOnlyList<string>? Platforms = null);

public sealed record OpenAiAdsCampaignCreateRequest(
    string Name,
    string Status,
    OpenAiAdsBudget Budget,
    string BiddingType,
    OpenAiAdsTargeting? Targeting = null,
    string? ConversionEventSettingId = null,
    string? Description = null,
    long? StartTime = null,
    long? EndTime = null,
    string? Mode = null,
    string? ProductFeedId = null,
    string? IdempotencyKey = null);

public sealed record OpenAiAdsCampaignUpdateRequest(
    string CampaignId,
    string? Name = null,
    string? Status = null,
    OpenAiAdsBudget? Budget = null,
    OpenAiAdsTargeting? Targeting = null,
    string? Description = null,
    long? StartTime = null,
    long? EndTime = null);

public sealed record OpenAiAdsBiddingConfig(
    string Strategy,
    string? BillingEventType = null,
    long? MaxBidMicros = null);

public sealed record OpenAiAdsAdGroupCreateRequest(
    string CampaignId,
    string Name,
    string Status,
    OpenAiAdsBiddingConfig Bidding,
    IReadOnlyList<string>? ContextHints = null,
    string? Description = null,
    string? ProductFeedId = null,
    IReadOnlyList<OpenAiAdsProductSetFilter>? ProductFilters = null,
    string? IdempotencyKey = null);

public sealed record OpenAiAdsProductSetFilter(
    string Field,
    string Operator,
    IReadOnlyList<string> Values);

public sealed record OpenAiAdsAdGroupUpdateRequest(
    string AdGroupId,
    string? Name = null,
    string? Status = null,
    OpenAiAdsBiddingConfig? Bidding = null,
    IReadOnlyList<string>? ContextHints = null,
    string? Description = null);

public sealed record OpenAiAdsCreative(
    string Type,
    string Title,
    string Body,
    string? TargetUrl = null,
    string? FileId = null,
    string? Price = null);

public sealed record OpenAiAdsAdCreateRequest(
    string AdGroupId,
    string Name,
    OpenAiAdsCreative Creative,
    string Status,
    string? IdempotencyKey = null);

public sealed record OpenAiAdsAdUpdateRequest(
    string AdId,
    string? Name = null,
    OpenAiAdsCreative? Creative = null,
    string? Status = null);

public sealed record OpenAiAdsProviderPage(
    JsonElement Payload);

public sealed record OpenAiAdsProviderEntity(
    JsonElement Payload);

public sealed record OpenAiAdsImageUploadResult(
    string FileId,
    JsonElement Payload);

public sealed record OpenAiAdsGeoSearchResult(
    JsonElement Payload);

public sealed record OpenAiAdsInsightsQuery(
    DateTime FromUtc,
    DateTime ToUtc,
    string TimeGranularity = "none",
    IReadOnlyList<string>? Fields = null,
    int? Limit = null);

public sealed record OpenAiAdsConversionEventSettingCreateRequest(
    string Name,
    string EventType,
    string SourceId,
    string? CustomEventName = null,
    string? IdempotencyKey = null);

public sealed record OpenAiAdsConversionInsightsQuery(
    DateTime FromUtc,
    DateTime ToUtc,
    string AggregationLevel,
    IReadOnlyList<string>? EntityIds = null,
    string TimeGranularity = "none",
    bool GroupByEntity = true,
    string? Breakdown = null,
    string AttributionTimeBasis = "ad_event_time",
    int AttributionWindowDays = 30,
    int ViewThroughAttributionWindowDays = 1,
    IReadOnlyList<string>? EventNames = null,
    bool IncludeZeroRows = true);

public sealed record OpenAiAdsInsightsResult(
    JsonElement Payload,
    DateTime? EffectiveFromUtc = null,
    DateTime? EffectiveToUtc = null);


public sealed record OpenAiAdsProductFeedCreateRequest(
    string Name,
    string Currency,
    string? IdempotencyKey = null);

public sealed record OpenAiAdsProductFeedItemUpsertRequest(
    string ProductFeedId,
    string ExternalId,
    string Title,
    string Description,
    long PriceMicros,
    string Currency,
    string LandingUrl,
    string ImageUrl,
    string Availability,
    string? IdempotencyKey = null);
