using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Shared.Analytics;

namespace Infrastructure.Analytics;

public interface IOpenAiAdsExecutionService
{
    Task<OpenAiAdsProviderPage> ListCampaignsAsync(MarketingOwnerScope owner, CancellationToken ct = default);
    Task<OpenAiAdsProviderEntity> GetCampaignAsync(MarketingOwnerScope owner, string campaignId, CancellationToken ct = default);
    Task<OpenAiAdsProviderEntity> CreateCampaignAsync(MarketingOwnerScope owner, OpenAiAdsCampaignCreateRequest request, CancellationToken ct = default);
    Task<OpenAiAdsProviderEntity> UpdateCampaignAsync(MarketingOwnerScope owner, OpenAiAdsCampaignUpdateRequest request, CancellationToken ct = default);
    Task<OpenAiAdsProviderEntity> SetCampaignStatusAsync(MarketingOwnerScope owner, string campaignId, string status, CancellationToken ct = default);

    Task<OpenAiAdsProviderPage> ListAdGroupsAsync(MarketingOwnerScope owner, string? campaignId = null, CancellationToken ct = default);
    Task<OpenAiAdsProviderEntity> GetAdGroupAsync(MarketingOwnerScope owner, string adGroupId, CancellationToken ct = default);
    Task<OpenAiAdsProviderEntity> CreateAdGroupAsync(MarketingOwnerScope owner, OpenAiAdsAdGroupCreateRequest request, CancellationToken ct = default);
    Task<OpenAiAdsProviderEntity> UpdateAdGroupAsync(MarketingOwnerScope owner, OpenAiAdsAdGroupUpdateRequest request, CancellationToken ct = default);
    Task<OpenAiAdsProviderEntity> SetAdGroupStatusAsync(MarketingOwnerScope owner, string adGroupId, string status, CancellationToken ct = default);

    Task<OpenAiAdsProviderPage> ListAdsAsync(MarketingOwnerScope owner, string? adGroupId = null, CancellationToken ct = default);
    Task<OpenAiAdsProviderEntity> GetAdAsync(MarketingOwnerScope owner, string adId, CancellationToken ct = default);
    Task<OpenAiAdsProviderEntity> CreateAdAsync(MarketingOwnerScope owner, OpenAiAdsAdCreateRequest request, CancellationToken ct = default);
    Task<OpenAiAdsProviderEntity> UpdateAdAsync(MarketingOwnerScope owner, OpenAiAdsAdUpdateRequest request, CancellationToken ct = default);
    Task<OpenAiAdsProviderEntity> SetAdStatusAsync(MarketingOwnerScope owner, string adId, string status, CancellationToken ct = default);
    Task<OpenAiAdsProviderEntity> PreviewAdAsync(MarketingOwnerScope owner, string adId, CancellationToken ct = default);

    Task<OpenAiAdsImageUploadResult> UploadImageUrlAsync(MarketingOwnerScope owner, string imageUrl, CancellationToken ct = default);
    Task<OpenAiAdsImageUploadResult> UploadImageAsync(MarketingOwnerScope owner, Stream content, string fileName, string contentType, CancellationToken ct = default);
    Task<OpenAiAdsGeoSearchResult> SearchGeoAsync(MarketingOwnerScope owner, string query, int limit = 20, CancellationToken ct = default);
    Task<OpenAiAdsProviderPage> ListConversionEventSettingsAsync(MarketingOwnerScope owner, CancellationToken ct = default);
    Task<OpenAiAdsProviderEntity> CreateConversionEventSettingAsync(MarketingOwnerScope owner, OpenAiAdsConversionEventSettingCreateRequest request, CancellationToken ct = default);
    Task<OpenAiAdsInsightsResult> GetConversionInsightsAsync(MarketingOwnerScope owner, OpenAiAdsConversionInsightsQuery query, CancellationToken ct = default);

    Task<OpenAiAdsInsightsResult> GetAccountInsightsAsync(MarketingOwnerScope owner, string aggregationLevel, OpenAiAdsInsightsQuery query, CancellationToken ct = default);
    Task<OpenAiAdsInsightsResult> GetCampaignInsightsAsync(MarketingOwnerScope owner, string campaignId, string aggregationLevel, OpenAiAdsInsightsQuery query, CancellationToken ct = default);
    Task<OpenAiAdsInsightsResult> GetAdGroupInsightsAsync(MarketingOwnerScope owner, string adGroupId, string aggregationLevel, OpenAiAdsInsightsQuery query, CancellationToken ct = default);
    Task<OpenAiAdsInsightsResult> GetAdInsightsAsync(MarketingOwnerScope owner, string adId, OpenAiAdsInsightsQuery query, CancellationToken ct = default);

    Task<OpenAiAdsProviderPage> ListProductFeedsAsync(MarketingOwnerScope owner, CancellationToken ct = default);
    Task<OpenAiAdsProviderEntity> CreateProductFeedAsync(MarketingOwnerScope owner, OpenAiAdsProductFeedCreateRequest request, CancellationToken ct = default);
    Task<OpenAiAdsProviderPage> ListProductFeedItemsAsync(MarketingOwnerScope owner, string productFeedId, CancellationToken ct = default);
    Task<OpenAiAdsProviderEntity> UpsertProductFeedItemAsync(MarketingOwnerScope owner, OpenAiAdsProductFeedItemUpsertRequest request, CancellationToken ct = default);
}

public sealed class OpenAiAdsExecutionService(
    HttpClient httpClient,
    IOpenAiAdsAccountConnectionAuthority authority) : IOpenAiAdsExecutionService
{
    private const string BaseUrl = "https://api.ads.openai.com/v1";

    public Task<OpenAiAdsProviderPage> ListCampaignsAsync(MarketingOwnerScope owner, CancellationToken ct = default) =>
        GetPageAsync(owner, "/campaigns", ct);

    public Task<OpenAiAdsProviderEntity> GetCampaignAsync(MarketingOwnerScope owner, string campaignId, CancellationToken ct = default) =>
        GetEntityAsync(owner, $"/campaigns/{Id(campaignId)}", ct);

    public async Task<OpenAiAdsProviderEntity> CreateCampaignAsync(
        MarketingOwnerScope owner,
        OpenAiAdsCampaignCreateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Budget.Validate();
        var status = OpenAiAdsEntityStatuses.NormalizeCreate(request.Status);
        var biddingType = OpenAiAdsBiddingTypes.Normalize(request.BiddingType);
        if (status == OpenAiAdsEntityStatuses.Active) await RequireActivationReadyAsync(owner, ct);
        await ValidateConversionGoalAsync(owner, biddingType, request.ConversionEventSettingId, ct);

        var payload = new Dictionary<string, object?>
        {
            ["name"] = Text(request.Name, 1000, nameof(request.Name)),
            ["status"] = status,
            ["budget"] = Budget(request.Budget),
            ["bidding_type"] = biddingType
        };
        Add(payload, "description", Optional(request.Description, 4000));
        Add(payload, "start_time", request.StartTime);
        Add(payload, "end_time", request.EndTime);
        Add(payload, "targeting", Targeting(request.Targeting));
        if (biddingType == OpenAiAdsBiddingTypes.Conversions)
            payload["conversion_event_setting_ids"] = new[] { request.ConversionEventSettingId! };

        if (!string.IsNullOrWhiteSpace(request.Mode))
        {
            if (!string.Equals(request.Mode.Trim(), "product_feed", StringComparison.Ordinal))
                throw new ArgumentException("Only product_feed is a supported explicit OpenAI Ads campaign mode.", nameof(request.Mode));
            payload["mode"] = "product_feed";
            payload["product_feed_id"] = Id(request.ProductFeedId);
        }
        else if (!string.IsNullOrWhiteSpace(request.ProductFeedId))
        {
            throw new ArgumentException("A product feed ID requires product_feed campaign mode.", nameof(request.ProductFeedId));
        }

        return await SendEntityAsync(owner, HttpMethod.Post, "/campaigns", payload, request.IdempotencyKey, ct);
    }

    public async Task<OpenAiAdsProviderEntity> UpdateCampaignAsync(
        MarketingOwnerScope owner,
        OpenAiAdsCampaignUpdateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Status is not null && OpenAiAdsEntityStatuses.NormalizeUpdate(request.Status) == OpenAiAdsEntityStatuses.Active)
            await RequireActivationReadyAsync(owner, ct);
        request.Budget?.Validate();

        var payload = new Dictionary<string, object?>();
        Add(payload, "name", Optional(request.Name, 1000));
        Add(payload, "description", request.Description);
        Add(payload, "start_time", request.StartTime);
        Add(payload, "end_time", request.EndTime);
        if (request.Status is not null) payload["status"] = OpenAiAdsEntityStatuses.NormalizeUpdate(request.Status);
        Add(payload, "budget", request.Budget is null ? null : Budget(request.Budget));
        Add(payload, "targeting", Targeting(request.Targeting));
        RequireMutation(payload);
        return await SendEntityAsync(owner, HttpMethod.Post, $"/campaigns/{Id(request.CampaignId)}", payload, null, ct);
    }

    public Task<OpenAiAdsProviderEntity> SetCampaignStatusAsync(MarketingOwnerScope owner, string campaignId, string status, CancellationToken ct = default) =>
        SetStatusAsync(owner, "campaigns", campaignId, status, ct);

    public async Task<OpenAiAdsProviderPage> ListAdGroupsAsync(MarketingOwnerScope owner, string? campaignId = null, CancellationToken ct = default)
    {
        var path = "/ad_groups";
        if (!string.IsNullOrWhiteSpace(campaignId))
            path += "?campaign_id=" + Uri.EscapeDataString(Id(campaignId));
        return await GetPageAsync(owner, path, ct);
    }

    public Task<OpenAiAdsProviderEntity> GetAdGroupAsync(MarketingOwnerScope owner, string adGroupId, CancellationToken ct = default) =>
        GetEntityAsync(owner, $"/ad_groups/{Id(adGroupId)}", ct);

    public async Task<OpenAiAdsProviderEntity> CreateAdGroupAsync(
        MarketingOwnerScope owner,
        OpenAiAdsAdGroupCreateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var status = OpenAiAdsEntityStatuses.NormalizeCreate(request.Status);
        if (status == OpenAiAdsEntityStatuses.Active) await RequireActivationReadyAsync(owner, ct);

        var campaign = await GetCampaignAsync(owner, request.CampaignId, ct);
        var parentBidding = ReadString(campaign.Payload, "bidding_type")
            ?? throw new InvalidOperationException("OpenAI Ads campaign did not return a bidding type.");
        ValidateBidding(request.Bidding, parentBidding);

        var payload = new Dictionary<string, object?>
        {
            ["campaign_id"] = Id(request.CampaignId),
            ["name"] = Text(request.Name, 1000, nameof(request.Name)),
            ["status"] = status,
            ["bidding_config"] = Bidding(request.Bidding)
        };
        Add(payload, "description", Optional(request.Description, 4000));
        Add(payload, "context_hints", CleanList(request.ContextHints, 100, 500));
        if (!string.IsNullOrWhiteSpace(request.ProductFeedId))
        {
            payload["product_set"] = new
            {
                product_feed_id = Id(request.ProductFeedId),
                filters = request.ProductFilters?.Select(x => new
                {
                    field = Text(x.Field, 200, nameof(x.Field)),
                    @operator = Text(x.Operator, 50, nameof(x.Operator)),
                    values = CleanList(x.Values, 100, 500) ?? []
                }).ToArray()
            };
        }
        return await SendEntityAsync(owner, HttpMethod.Post, "/ad_groups", payload, request.IdempotencyKey, ct);
    }

    public async Task<OpenAiAdsProviderEntity> UpdateAdGroupAsync(
        MarketingOwnerScope owner,
        OpenAiAdsAdGroupUpdateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Status is not null && OpenAiAdsEntityStatuses.NormalizeUpdate(request.Status) == OpenAiAdsEntityStatuses.Active)
            await RequireActivationReadyAsync(owner, ct);

        if (request.Bidding is not null)
        {
            var adGroup = await GetAdGroupAsync(owner, request.AdGroupId, ct);
            var campaignId = ReadString(adGroup.Payload, "campaign_id")
                ?? throw new InvalidOperationException("OpenAI Ads ad group did not return its campaign.");
            var campaign = await GetCampaignAsync(owner, campaignId, ct);
            ValidateBidding(request.Bidding, ReadString(campaign.Payload, "bidding_type") ?? string.Empty);
        }

        var payload = new Dictionary<string, object?>();
        Add(payload, "name", Optional(request.Name, 1000));
        Add(payload, "description", request.Description);
        if (request.Status is not null) payload["status"] = OpenAiAdsEntityStatuses.NormalizeUpdate(request.Status);
        Add(payload, "bidding_config", request.Bidding is null ? null : Bidding(request.Bidding));
        if (request.ContextHints is not null) payload["context_hints"] = CleanList(request.ContextHints, 100, 500) ?? [];
        RequireMutation(payload);
        return await SendEntityAsync(owner, HttpMethod.Post, $"/ad_groups/{Id(request.AdGroupId)}", payload, null, ct);
    }

    public Task<OpenAiAdsProviderEntity> SetAdGroupStatusAsync(MarketingOwnerScope owner, string adGroupId, string status, CancellationToken ct = default) =>
        SetStatusAsync(owner, "ad_groups", adGroupId, status, ct);

    public async Task<OpenAiAdsProviderPage> ListAdsAsync(MarketingOwnerScope owner, string? adGroupId = null, CancellationToken ct = default)
    {
        var path = "/ads";
        if (!string.IsNullOrWhiteSpace(adGroupId))
            path += "?ad_group_id=" + Uri.EscapeDataString(Id(adGroupId));
        return await GetPageAsync(owner, path, ct);
    }

    public Task<OpenAiAdsProviderEntity> GetAdAsync(MarketingOwnerScope owner, string adId, CancellationToken ct = default) =>
        GetEntityAsync(owner, $"/ads/{Id(adId)}", ct);

    public async Task<OpenAiAdsProviderEntity> CreateAdAsync(
        MarketingOwnerScope owner,
        OpenAiAdsAdCreateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var status = OpenAiAdsEntityStatuses.NormalizeCreate(request.Status);
        if (status == OpenAiAdsEntityStatuses.Active) await RequireActivationReadyAsync(owner, ct);
        await ValidateCreativeAgainstParentAsync(owner, request.AdGroupId, request.Creative, ct);

        var payload = new Dictionary<string, object?>
        {
            ["ad_group_id"] = Id(request.AdGroupId),
            ["name"] = Text(request.Name, 1000, nameof(request.Name)),
            ["creative"] = Creative(request.Creative),
            ["status"] = status
        };
        return await SendEntityAsync(owner, HttpMethod.Post, "/ads", payload, request.IdempotencyKey, ct);
    }

    public async Task<OpenAiAdsProviderEntity> UpdateAdAsync(
        MarketingOwnerScope owner,
        OpenAiAdsAdUpdateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Status is not null && OpenAiAdsEntityStatuses.NormalizeUpdate(request.Status) == OpenAiAdsEntityStatuses.Active)
            await RequireActivationReadyAsync(owner, ct);

        if (request.Creative is not null)
        {
            var ad = await GetAdAsync(owner, request.AdId, ct);
            var adGroupId = ReadString(ad.Payload, "ad_group_id")
                ?? throw new InvalidOperationException("OpenAI Ads ad did not return its ad group.");
            await ValidateCreativeAgainstParentAsync(owner, adGroupId, request.Creative, ct);
        }

        var payload = new Dictionary<string, object?>();
        Add(payload, "name", Optional(request.Name, 1000));
        if (request.Status is not null) payload["status"] = OpenAiAdsEntityStatuses.NormalizeUpdate(request.Status);
        Add(payload, "creative", request.Creative is null ? null : Creative(request.Creative));
        RequireMutation(payload);
        return await SendEntityAsync(owner, HttpMethod.Post, $"/ads/{Id(request.AdId)}", payload, null, ct);
    }

    public Task<OpenAiAdsProviderEntity> SetAdStatusAsync(MarketingOwnerScope owner, string adId, string status, CancellationToken ct = default) =>
        SetStatusAsync(owner, "ads", adId, status, ct);

    public Task<OpenAiAdsProviderEntity> PreviewAdAsync(MarketingOwnerScope owner, string adId, CancellationToken ct = default) =>
        SendEntityAsync(owner, HttpMethod.Post, $"/ads/{Id(adId)}/preview", new Dictionary<string, object?>(), null, ct);

    public async Task<OpenAiAdsImageUploadResult> UploadImageUrlAsync(
        MarketingOwnerScope owner,
        string imageUrl,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("Use an absolute HTTP(S) image URL.", nameof(imageUrl));

        var entity = await SendEntityAsync(owner, HttpMethod.Post, "/upload", new Dictionary<string, object?>
        {
            ["image_url"] = uri.ToString()
        }, null, ct);
        var fileId = ReadString(entity.Payload, "file_id")
            ?? throw new InvalidOperationException("OpenAI Ads upload did not return a file_id.");
        return new(fileId, entity.Payload);
    }

    public async Task<OpenAiAdsImageUploadResult> UploadImageAsync(
        MarketingOwnerScope owner,
        Stream content,
        string fileName,
        string contentType,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead) throw new ArgumentException("Image stream must be readable.", nameof(content));
        var safeName = Text(fileName, 255, nameof(fileName));
        var mediaType = Text(contentType, 100, nameof(contentType));
        if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("ChatGPT Ads creative upload must be an image.", nameof(contentType));

        var key = await ManagementKeyAsync(owner, ct);
        using var request = Request(HttpMethod.Post, "/upload", key);
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(file, "file", safeName);
        request.Content = form;
        var payload = await SendAsync(request, ct);
        var fileId = ReadString(payload, "file_id")
            ?? throw new InvalidOperationException("OpenAI Ads upload did not return a file_id.");
        return new(fileId, payload);
    }

    public async Task<OpenAiAdsGeoSearchResult> SearchGeoAsync(
        MarketingOwnerScope owner,
        string query,
        int limit = 20,
        CancellationToken ct = default)
    {
        var q = Text(query, 500, nameof(query));
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        var payload = await GetJsonAsync(owner, $"/geo_lookup/search?q={Uri.EscapeDataString(q)}&limit={limit}", ct);
        return new(payload);
    }

    public Task<OpenAiAdsProviderPage> ListConversionEventSettingsAsync(MarketingOwnerScope owner, CancellationToken ct = default) =>
        GetPageAsync(owner, "/conversions/event_settings?limit=500", ct);

    public async Task<OpenAiAdsProviderEntity> CreateConversionEventSettingAsync(
        MarketingOwnerScope owner,
        OpenAiAdsConversionEventSettingCreateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var eventType = Text(request.EventType, 256, nameof(request.EventType));
        var custom = string.Equals(eventType, "custom", StringComparison.OrdinalIgnoreCase);
        if (custom && string.IsNullOrWhiteSpace(request.CustomEventName))
            throw new ArgumentException("Custom conversion settings require custom_event_name.", nameof(request));
        if (!custom && !string.IsNullOrWhiteSpace(request.CustomEventName))
            throw new ArgumentException("custom_event_name is valid only for custom conversion settings.", nameof(request));

        var payload = new Dictionary<string, object?>
        {
            ["name"] = Text(request.Name, 1000, nameof(request.Name)),
            ["event_type"] = eventType,
            ["attribution_window_days"] = 30,
            ["source_ids"] = new[] { Id(request.SourceId) }
        };
        if (custom) payload["custom_event_name"] = Text(request.CustomEventName, 256, nameof(request.CustomEventName));
        return await SendEntityAsync(owner, HttpMethod.Post, "/conversions/event_settings", payload, request.IdempotencyKey, ct);
    }

    public async Task<OpenAiAdsInsightsResult> GetConversionInsightsAsync(
        MarketingOwnerScope owner,
        OpenAiAdsConversionInsightsQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.ToUtc <= query.FromUtc) throw new ArgumentException("Conversion insight end time must be after start time.", nameof(query));
        if ((query.ToUtc - query.FromUtc).TotalDays > 365) throw new ArgumentException("Conversion insight range cannot exceed 365 days.", nameof(query));

        var aggregation = query.AggregationLevel.Trim().ToLowerInvariant();
        if (aggregation is not ("campaign" or "ad_group" or "ad"))
            throw new ArgumentException("Conversion insights aggregate by campaign, ad_group, or ad.", nameof(query));
        var granularity = query.TimeGranularity.Trim().ToLowerInvariant();
        if (granularity is not ("none" or "daily"))
            throw new ArgumentException("Conversion insights support none or daily granularity.", nameof(query));
        var basis = query.AttributionTimeBasis.Trim().ToLowerInvariant();
        if (basis is not ("ad_event_time" or "conversion_time"))
            throw new ArgumentException("Unsupported conversion attribution time basis.", nameof(query));
        if (query.AttributionWindowDays is not (7 or 14 or 30))
            throw new ArgumentException("Click attribution window must be 7, 14, or 30 days.", nameof(query));
        if (query.ViewThroughAttributionWindowDays is not (0 or 1))
            throw new ArgumentException("View-through attribution window must be 0 or 1 day.", nameof(query));
        var breakdown = string.IsNullOrWhiteSpace(query.Breakdown) ? null : query.Breakdown.Trim().ToLowerInvariant();
        if (breakdown is not null and not ("country" or "device"))
            throw new ArgumentException("Conversion insight breakdown must be country or device.", nameof(query));

        var entityIds = CleanList(query.EntityIds, 500, 500);
        if (query.GroupByEntity && (entityIds is null || entityIds.Count == 0))
            throw new ArgumentException("Grouped conversion insights require at least one entity ID.", nameof(query));
        var eventNames = CleanList(query.EventNames, 500, 256);

        var payload = new Dictionary<string, object?>
        {
            ["aggregation_level"] = aggregation,
            ["time_ranges"] = new[]
            {
                JsonSerializer.Serialize(new
                {
                    type = "unix_range",
                    start = new DateTimeOffset(query.FromUtc.ToUniversalTime()).ToUnixTimeSeconds().ToString(),
                    end = new DateTimeOffset(query.ToUtc.ToUniversalTime()).ToUnixTimeSeconds().ToString()
                })
            },
            ["time_granularity"] = granularity,
            ["group_by_entity"] = query.GroupByEntity,
            ["attribution_time_basis"] = basis,
            ["attribution_window_days"] = query.AttributionWindowDays,
            ["view_through_attribution_window_days"] = query.ViewThroughAttributionWindowDays,
            ["include_zero_rows"] = query.IncludeZeroRows
        };
        if (entityIds is not null && entityIds.Count > 0) payload["entity_ids"] = entityIds;
        Add(payload, "breakdown", breakdown);
        if (eventNames is not null && eventNames.Count > 0)
        {
            payload["include"] = new[] { "attributed_events" };
            payload["event_names"] = eventNames;
        }

        var entity = await SendEntityAsync(owner, HttpMethod.Post, "/conversions/insights", payload, null, ct);
        return new(entity.Payload);
    }

    public Task<OpenAiAdsInsightsResult> GetAccountInsightsAsync(MarketingOwnerScope owner, string aggregationLevel, OpenAiAdsInsightsQuery query, CancellationToken ct = default) =>
        GetInsightsAsync(owner, "/ad_account/insights", aggregationLevel, query, ct);

    public Task<OpenAiAdsInsightsResult> GetCampaignInsightsAsync(MarketingOwnerScope owner, string campaignId, string aggregationLevel, OpenAiAdsInsightsQuery query, CancellationToken ct = default) =>
        GetInsightsAsync(owner, $"/campaigns/{Id(campaignId)}/insights", aggregationLevel, query, ct);

    public Task<OpenAiAdsInsightsResult> GetAdGroupInsightsAsync(MarketingOwnerScope owner, string adGroupId, string aggregationLevel, OpenAiAdsInsightsQuery query, CancellationToken ct = default) =>
        GetInsightsAsync(owner, $"/ad_groups/{Id(adGroupId)}/insights", aggregationLevel, query, ct);

    public Task<OpenAiAdsInsightsResult> GetAdInsightsAsync(MarketingOwnerScope owner, string adId, OpenAiAdsInsightsQuery query, CancellationToken ct = default) =>
        GetInsightsAsync(owner, $"/ads/{Id(adId)}/insights", "ad", query, ct);

    private async Task<OpenAiAdsInsightsResult> GetInsightsAsync(
        MarketingOwnerScope owner,
        string path,
        string aggregationLevel,
        OpenAiAdsInsightsQuery query,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.ToUtc <= query.FromUtc) throw new ArgumentException("Insight end time must be after start time.", nameof(query));
        var aggregation = aggregationLevel.Trim().ToLowerInvariant();
        if (aggregation is not ("ad_account" or "campaign" or "ad_group" or "ad"))
            throw new ArgumentException("Unsupported OpenAI Ads aggregation level.", nameof(aggregationLevel));
        var granularity = query.TimeGranularity.Trim().ToLowerInvariant();
        if (granularity is not ("hourly" or "daily" or "monthly" or "none"))
            throw new ArgumentException("Unsupported OpenAI Ads time granularity.", nameof(query));

        // Insights accepts completed full hours in the connected account's timezone.
        // Resolve that timezone through the same owner-bound authority as the report.
        var account = await GetJsonAsync(owner, "/ad_account", ct);
        var timezoneId = ReadString(account, "timezone")
            ?? throw new InvalidOperationException("The scoped ChatGPT Ads account did not return its reporting timezone.");
        TimeZoneInfo timezone;
        try { timezone = TimeZoneInfo.FindSystemTimeZoneById(timezoneId); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new InvalidOperationException("The scoped ChatGPT Ads reporting timezone is unavailable.", ex); }
        var now = DateTime.UtcNow;
        var requestedEnd = query.ToUtc.ToUniversalTime();
        var from = AlignReportingHour(query.FromUtc.ToUniversalTime(), timezone, forward: true);
        var to = AlignReportingHour(requestedEnd < now ? requestedEnd : now, timezone, forward: false);
        if (to <= from)
            throw new ArgumentException("The selected range contains no completed full ChatGPT Ads reporting hour.", nameof(query));

        var parameters = new List<string>
        {
            "aggregation_level=" + Uri.EscapeDataString(aggregation),
            "time_granularity=" + Uri.EscapeDataString(granularity),
            "time_ranges[]=" + Uri.EscapeDataString(JsonSerializer.Serialize(new
            {
                type = "unix_range",
                start = new DateTimeOffset(from).ToUnixTimeSeconds(),
                end = new DateTimeOffset(to).ToUnixTimeSeconds()
            }))
        };
        foreach (var field in CleanList(query.Fields, 100, 200) ?? [])
            parameters.Add("fields[]=" + Uri.EscapeDataString(field));
        if (query.Limit.HasValue)
        {
            if (query.Limit is < 1 or > 2000) throw new ArgumentOutOfRangeException(nameof(query.Limit));
            parameters.Add("limit=" + query.Limit.Value);
        }
        return new(await GetJsonAsync(owner, path + "?" + string.Join("&", parameters), ct), from, to);
    }

    private static DateTime AlignReportingHour(DateTime utc, TimeZoneInfo timezone, bool forward)
    {
        // Walk UTC minutes so DST gaps and repeated hours retain their actual instant;
        // fractional-hour timezone offsets must not be rounded as UTC hours.
        var minute = new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        if (forward && minute < utc) minute = minute.AddMinutes(1);
        for (var i = 0; i < 180; i++, minute = minute.AddMinutes(forward ? 1 : -1))
            if (TimeZoneInfo.ConvertTimeFromUtc(minute, timezone).Minute == 0) return minute;
        throw new InvalidOperationException("No valid ChatGPT Ads reporting-hour boundary was found.");
    }

    private async Task ValidateConversionGoalAsync(
        MarketingOwnerScope owner,
        string biddingType,
        string? conversionSettingId,
        CancellationToken ct)
    {
        if (biddingType != OpenAiAdsBiddingTypes.Conversions)
        {
            if (!string.IsNullOrWhiteSpace(conversionSettingId))
                throw new ArgumentException("Conversion event settings apply only to conversion campaigns.", nameof(conversionSettingId));
            return;
        }
        var requested = Id(conversionSettingId);
        var settings = await ListConversionEventSettingsAsync(owner, ct);
        if (!settings.Payload.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("OpenAI Ads conversion settings are unavailable.");

        foreach (var item in data.EnumerateArray())
        {
            if (!string.Equals(ReadString(item, "id"), requested, StringComparison.Ordinal)) continue;
            var eventType = ReadString(item, "event_type");
            var status = ReadString(item, "status");
            if (string.Equals(eventType, "custom", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Custom OpenAI Ads events are measurement-only and cannot be used as the conversion optimization goal.");
            if (string.Equals(status, "archived", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected OpenAI Ads conversion event setting is archived.");
            return;
        }
        throw new InvalidOperationException("The selected OpenAI Ads conversion event setting is not available to this scoped account.");
    }

    private async Task ValidateCreativeAgainstParentAsync(
        MarketingOwnerScope owner,
        string adGroupId,
        OpenAiAdsCreative creative,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(creative);
        var group = await GetAdGroupAsync(owner, adGroupId, ct);
        var campaignId = ReadString(group.Payload, "campaign_id")
            ?? throw new InvalidOperationException("OpenAI Ads ad group did not return its campaign.");
        var campaign = await GetCampaignAsync(owner, campaignId, ct);
        var mode = ReadString(campaign.Payload, "mode");
        var type = creative.Type.Trim().ToLowerInvariant();

        if (string.Equals(mode, "product_feed", StringComparison.OrdinalIgnoreCase))
        {
            if (type != OpenAiAdsCreativeTypes.ProductAdTemplate)
                throw new InvalidOperationException("Product-feed campaigns require product_ad_template creative.");
            if (!string.IsNullOrWhiteSpace(creative.TargetUrl) || !string.IsNullOrWhiteSpace(creative.FileId))
                throw new InvalidOperationException("Product-feed creative must use feed imagery and destination.");
            return;
        }

        if (type != OpenAiAdsCreativeTypes.ChatCard)
            throw new InvalidOperationException("Standard OpenAI Ads campaigns require chat_card creative.");
        if (string.IsNullOrWhiteSpace(creative.FileId))
            throw new ArgumentException("A provider-uploaded file_id is required for chat_card creative.", nameof(creative));
        ValidateTargetUrl(creative.TargetUrl);
    }

    private async Task RequireActivationReadyAsync(MarketingOwnerScope owner, CancellationToken ct)
    {
        var account = await GetJsonAsync(owner, "/ad_account", ct);
        if (!string.Equals(ReadString(account, "status"), "active", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(ReadNestedString(account, "review", "status"), OpenAiAdsReviewStatuses.Approved, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The scoped ChatGPT Ads account is not active and approved for activation.");
    }

    private async Task<OpenAiAdsProviderEntity> SetStatusAsync(
        MarketingOwnerScope owner,
        string resource,
        string id,
        string status,
        CancellationToken ct)
    {
        var normalized = OpenAiAdsEntityStatuses.NormalizeUpdate(status);
        if (normalized == OpenAiAdsEntityStatuses.Active) await RequireActivationReadyAsync(owner, ct);
        var action = normalized switch
        {
            OpenAiAdsEntityStatuses.Active => "activate",
            OpenAiAdsEntityStatuses.Paused => "pause",
            OpenAiAdsEntityStatuses.Archived => "archive",
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };
        return await SendEntityAsync(owner, HttpMethod.Post, $"/{resource}/{Id(id)}/{action}", new Dictionary<string, object?>(), null, ct);
    }


    public Task<OpenAiAdsProviderPage> ListProductFeedsAsync(MarketingOwnerScope owner, CancellationToken ct = default) =>
        GetPageAsync(owner, "/product_feeds", ct);

    public Task<OpenAiAdsProviderEntity> CreateProductFeedAsync(
        MarketingOwnerScope owner,
        OpenAiAdsProductFeedCreateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payload = new Dictionary<string, object?>
        {
            ["name"] = Text(request.Name, 500, nameof(request.Name)),
            ["currency"] = Text(request.Currency, 3, nameof(request.Currency)).ToUpperInvariant()
        };
        return SendEntityAsync(owner, HttpMethod.Post, "/product_feeds", payload, request.IdempotencyKey, ct);
    }

    public Task<OpenAiAdsProviderPage> ListProductFeedItemsAsync(
        MarketingOwnerScope owner,
        string productFeedId,
        CancellationToken ct = default) =>
        GetPageAsync(owner, $"/product_feeds/{Id(productFeedId)}/products", ct);

    public Task<OpenAiAdsProviderEntity> UpsertProductFeedItemAsync(
        MarketingOwnerScope owner,
        OpenAiAdsProductFeedItemUpsertRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Uri.TryCreate(request.LandingUrl, UriKind.Absolute, out var landing) || landing.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Product landing URL must be HTTPS.", nameof(request));
        if (!Uri.TryCreate(request.ImageUrl, UriKind.Absolute, out var image) || (image.Scheme != Uri.UriSchemeHttps && image.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("Product image URL must be HTTP(S).", nameof(request));
        if (request.PriceMicros < 0) throw new ArgumentOutOfRangeException(nameof(request.PriceMicros));

        var payload = new Dictionary<string, object?>
        {
            ["external_id"] = Text(request.ExternalId, 500, nameof(request.ExternalId)),
            ["title"] = Text(request.Title, 500, nameof(request.Title)),
            ["description"] = Text(request.Description, 4000, nameof(request.Description)),
            ["price_micros"] = request.PriceMicros,
            ["currency"] = Text(request.Currency, 3, nameof(request.Currency)).ToUpperInvariant(),
            ["landing_url"] = request.LandingUrl.Trim(),
            ["image_url"] = request.ImageUrl.Trim(),
            ["availability"] = Text(request.Availability, 32, nameof(request.Availability))
        };
        return SendEntityAsync(owner, HttpMethod.Post, $"/product_feeds/{Id(request.ProductFeedId)}/products:upsert", payload, request.IdempotencyKey, ct);
    }

    private async Task<OpenAiAdsProviderPage> GetPageAsync(MarketingOwnerScope owner, string path, CancellationToken ct) =>
        new(await GetJsonAsync(owner, path, ct));

    private async Task<OpenAiAdsProviderEntity> GetEntityAsync(MarketingOwnerScope owner, string path, CancellationToken ct) =>
        new(await GetJsonAsync(owner, path, ct));

    private async Task<JsonElement> GetJsonAsync(MarketingOwnerScope owner, string path, CancellationToken ct)
    {
        var key = await ManagementKeyAsync(owner, ct);
        using var request = Request(HttpMethod.Get, path, key);
        return await SendAsync(request, ct);
    }

    private async Task<OpenAiAdsProviderEntity> SendEntityAsync(
        MarketingOwnerScope owner,
        HttpMethod method,
        string path,
        object payload,
        string? idempotencyKey,
        CancellationToken ct)
    {
        var key = await ManagementKeyAsync(owner, ct);
        using var request = Request(method, path, key);
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            request.Headers.TryAddWithoutValidation("Idempotency-Key", Text(idempotencyKey, 255, nameof(idempotencyKey)));
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return new(await SendAsync(request, ct));
    }

    private async Task<string> ManagementKeyAsync(MarketingOwnerScope owner, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var connection = await authority.GetAsync(owner, ct);
        if (!connection.Connected)
            throw new InvalidOperationException("ChatGPT Ads is not connected for this scope.");
        var secrets = await authority.GetSecretsAsync(owner, ct);
        if (string.IsNullOrWhiteSpace(secrets.ManagementApiKey))
            throw new InvalidOperationException("The scoped ChatGPT Ads management credential is unavailable.");
        return secrets.ManagementApiKey;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string key)
    {
        var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private async Task<JsonElement> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var retryable = response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
            throw new OpenAiAdsExecutionException((int)response.StatusCode, retryable, SafeProviderError(body));
        }
        if (string.IsNullOrWhiteSpace(body)) return JsonDocument.Parse("{}").RootElement.Clone();
        using var json = JsonDocument.Parse(body);
        return json.RootElement.Clone();
    }

    private static string SafeProviderError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "OpenAI Ads request failed.";
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String) return Clamp(error.GetString());
                var message = ReadString(error, "message");
                if (!string.IsNullOrWhiteSpace(message)) return Clamp(message);
            }
        }
        catch (JsonException) { }
        return "OpenAI Ads request failed.";
    }

    private static Dictionary<string, object> Budget(OpenAiAdsBudget budget)
    {
        budget.Validate();
        return budget.DailySpendLimitMicros.HasValue
            ? new() { ["daily_spend_limit_micros"] = budget.DailySpendLimitMicros.Value }
            : new() { ["lifetime_spend_limit_micros"] = budget.LifetimeSpendLimitMicros!.Value };
    }

    private static object Bidding(OpenAiAdsBiddingConfig bidding)
    {
        ArgumentNullException.ThrowIfNull(bidding);
        var strategy = bidding.Strategy.Trim().ToLowerInvariant();
        if (strategy == OpenAiAdsBiddingStrategies.FixedBid)
        {
            if (bidding.MaxBidMicros is null or <= 0)
                throw new ArgumentException("Fixed bidding requires a positive maximum bid.", nameof(bidding));
            return new
            {
                strategy,
                billing_event_type = NormalizeBilling(bidding.BillingEventType),
                max_bid_micros = bidding.MaxBidMicros.Value
            };
        }
        if (strategy is OpenAiAdsBiddingStrategies.MaximizeClicks or OpenAiAdsBiddingStrategies.MaximizeConversions)
        {
            if (bidding.MaxBidMicros.HasValue)
                throw new ArgumentException("Automatic bidding must omit maximum bid.", nameof(bidding));
            return new { strategy, billing_event_type = OpenAiAdsBillingEvents.Click };
        }
        throw new ArgumentException("Unsupported OpenAI Ads bidding strategy.", nameof(bidding));
    }

    private static void ValidateBidding(OpenAiAdsBiddingConfig bidding, string campaignBiddingType)
    {
        var parent = OpenAiAdsBiddingTypes.Normalize(campaignBiddingType);
        var billing = bidding.Strategy == OpenAiAdsBiddingStrategies.FixedBid
            ? NormalizeBilling(bidding.BillingEventType)
            : OpenAiAdsBillingEvents.Click;

        if (parent == OpenAiAdsBiddingTypes.Impressions && billing != OpenAiAdsBillingEvents.Impression)
            throw new InvalidOperationException("Impressions campaigns require impression billing.");
        if (parent != OpenAiAdsBiddingTypes.Impressions && billing != OpenAiAdsBillingEvents.Click)
            throw new InvalidOperationException("Clicks and conversion campaigns require click billing.");
        if (bidding.Strategy == OpenAiAdsBiddingStrategies.MaximizeClicks && parent != OpenAiAdsBiddingTypes.Clicks)
            throw new InvalidOperationException("Maximize clicks requires a clicks campaign.");
        if (bidding.Strategy == OpenAiAdsBiddingStrategies.MaximizeConversions && parent != OpenAiAdsBiddingTypes.Conversions)
            throw new InvalidOperationException("Maximize conversions requires a conversions campaign.");
    }

    private static object Creative(OpenAiAdsCreative creative)
    {
        var type = creative.Type.Trim().ToLowerInvariant();
        var payload = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["title"] = Text(creative.Title, 50, nameof(creative.Title)),
            ["body"] = Optional(creative.Body, 100) ?? string.Empty
        };
        Add(payload, "price", Optional(creative.Price, 100));
        Add(payload, "target_url", Optional(creative.TargetUrl, 2048));
        Add(payload, "file_id", Optional(creative.FileId, 500));
        return payload;
    }

    private static object? Targeting(OpenAiAdsTargeting? targeting)
    {
        if (targeting is null) return null;
        var payload = new Dictionary<string, object?>();
        var locations = new Dictionary<string, object?>();
        Add(locations, "countries", CleanList(targeting.Countries, 250, 10));
        Add(locations, "include", Geo(targeting.IncludedLocations));
        if (locations.Count > 0) payload["locations"] = locations;

        var excluded = new Dictionary<string, object?>();
        Add(excluded, "countries", CleanList(targeting.ExcludedCountries, 250, 10));
        Add(excluded, "include", Geo(targeting.ExcludedLocations));
        if (excluded.Count > 0) payload["excluded_locations"] = excluded;

        var custom = CleanList(targeting.CustomAudienceIds, 500, 200);
        if (custom is not null) payload["custom_audiences"] = new { ids = custom };
        var excludedCustom = CleanList(targeting.ExcludedCustomAudienceIds, 500, 200);
        if (excludedCustom is not null) payload["excluded_custom_audiences"] = new { ids = excludedCustom };

        if (targeting.Platforms is not null)
        {
            var platforms = CleanList(targeting.Platforms, 3, 20) ?? [];
            if (platforms.Count == 0 || platforms.Any(x => x is not ("ios_app" or "android_app" or "web")))
                throw new ArgumentException("OpenAI Ads platforms must be ios_app, android_app, or web.", nameof(targeting));
            payload["platforms"] = new { included = platforms };
        }
        return payload.Count == 0 ? null : payload;
    }

    private static object[]? Geo(IReadOnlyList<OpenAiAdsGeoTarget>? values) =>
        values is null ? null : values.Select(x => new
        {
            id = Id(x.Id),
            name = Optional(x.Name, 500),
            type = Optional(x.Type, 100),
            country_code = Optional(x.CountryCode, 10),
            region_code = Optional(x.RegionCode, 50)
        }).Cast<object>().ToArray();

    private static void ValidateTargetUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("ChatGPT Ads target URL must be an absolute HTTP(S) URL.", nameof(value));
        var query = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)[0])
            .Select(Uri.UnescapeDataString);
        if (query.Any(key => string.Equals(key, "oppref", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(key, "olref", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("ChatGPT Ads target URL must not contain oppref or olref attribution parameters.", nameof(value));
    }

    private static string NormalizeBilling(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is OpenAiAdsBillingEvents.Click or OpenAiAdsBillingEvents.Impression
            ? normalized
            : throw new ArgumentException("OpenAI Ads billing event must be click or impression.", nameof(value));
    }

    private static string Id(string? value)
    {
        var id = Text(value, 500, nameof(value));
        if (id.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '_' or '-')))
            throw new ArgumentException("Provider identifier contains unsupported characters.", nameof(value));
        return id;
    }

    private static string Text(string? value, int max, string parameter)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > max || normalized.Any(char.IsControl))
            throw new ArgumentException("A valid value is required.", parameter);
        return normalized;
    }

    private static string? Optional(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        if (normalized.Length > max || normalized.Any(char.IsControl))
            throw new ArgumentException("Value is too long or contains control characters.");
        return normalized;
    }

    private static IReadOnlyList<string>? CleanList(IReadOnlyList<string>? values, int maxCount, int maxLength)
    {
        if (values is null) return null;
        if (values.Count > maxCount) throw new ArgumentException("Too many values supplied.");
        return values.Select(x => Text(x, maxLength, nameof(values)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static void Add(IDictionary<string, object?> target, string key, object? value)
    {
        if (value is not null) target[key] = value;
    }

    private static void RequireMutation(IDictionary<string, object?> payload)
    {
        if (payload.Count == 0) throw new ArgumentException("At least one OpenAI Ads field must change.");
    }

    private static string? ReadString(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static string? ReadNestedString(JsonElement root, string parent, string child) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(parent, out var p)
            ? ReadString(p, child)
            : null;

    private static string Clamp(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "OpenAI Ads request failed." : value.Trim();
        return text.Length <= 500 ? text : text[..500];
    }
}

public sealed class OpenAiAdsExecutionException(
    int httpStatusCode,
    bool retryable,
    string message) : Exception(message)
{
    public int HttpStatusCode { get; } = httpStatusCode;
    public bool Retryable { get; } = retryable;
}
