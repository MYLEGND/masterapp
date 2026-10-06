using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Shared.Analytics;

namespace Infrastructure.Analytics;

public interface IMarketingExternalAdsReportingService
{
    Task<IReadOnlyList<ProviderDeliveryMetricRow>> GetCampaignsAsync(
        MarketingOwnerScope owner,
        string provider,
        TimeRangeRequest range,
        CancellationToken ct = default);
}

public sealed class MarketingExternalAdsReportingService(
    MarketingConnectionStore connections,
    MarketingExternalAdsOAuthService oauth,
    IHttpClientFactory clients,
    IConfiguration configuration) : IMarketingExternalAdsReportingService
{
    public async Task<IReadOnlyList<ProviderDeliveryMetricRow>> GetCampaignsAsync(
        MarketingOwnerScope owner,
        string provider,
        TimeRangeRequest range,
        CancellationToken ct = default)
    {
        var key = MarketingDestinationKeys.Normalize(provider);
        return key switch
        {
            MarketingDestinationKeys.Google => await GetGoogleCampaignsAsync(owner, range, ct),
            MarketingDestinationKeys.TikTok => await GetTikTokCampaignsAsync(owner, range, ct),
            _ => throw new ArgumentException("Provider must be google or tiktok.", nameof(provider))
        };
    }

    private async Task<IReadOnlyList<ProviderDeliveryMetricRow>> GetGoogleCampaignsAsync(
        MarketingOwnerScope owner,
        TimeRangeRequest range,
        CancellationToken ct)
    {
        var connection = await connections.GetProviderConnectionAsync(owner, MarketingDestinationKeys.Google, ct);
        if (!connection.Ready || string.IsNullOrWhiteSpace(connection.AccountId))
            throw new InvalidOperationException(connection.RequiresAccountSelection
                ? "Google Ads is connected but an ad account must be selected."
                : "Google Ads is not connected.");

        var access = await oauth.GetGoogleAccessAsync(owner, ct);
        var version = Clean(configuration["GoogleAds:ApiVersion"]) ?? "v25";
        if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^v\d{1,3}$"))
            throw new InvalidOperationException("GoogleAds:ApiVersion is invalid.");

        var from = range.FromUtc.ToUniversalTime().Date;
        var to = range.ToUtc.ToUniversalTime().AddTicks(-1).Date;
        var query =
            "SELECT campaign.id, campaign.name, campaign.status, " +
            "metrics.cost_micros, metrics.impressions, metrics.clicks, metrics.conversions " +
            $"FROM campaign WHERE segments.date BETWEEN '{from:yyyy-MM-dd}' AND '{to:yyyy-MM-dd}'";

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://googleads.googleapis.com/{version}/customers/{connection.AccountId}/googleAds:searchStream")
        {
            Content = JsonContent.Create(new { query })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.AccessToken);
        request.Headers.TryAddWithoutValidation("developer-token", Required("GoogleAds:DeveloperToken"));
        var loginCustomer = Digits(configuration["GoogleAds:LoginCustomerId"]);
        if (!string.IsNullOrWhiteSpace(loginCustomer))
            request.Headers.TryAddWithoutValidation("login-customer-id", loginCustomer);

        using var response = await clients.CreateClient("MarketingExternalAds").SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw ProviderFailure("Google Ads reporting failed.", response.StatusCode, json);

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        var output = new List<ProviderDeliveryMetricRow>();
        foreach (var batch in document.RootElement.EnumerateArray())
        {
            if (!batch.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var row in results.EnumerateArray())
            {
                if (!row.TryGetProperty("campaign", out var campaign) || campaign.ValueKind != JsonValueKind.Object)
                    continue;
                var id = Text(campaign, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var metrics = row.TryGetProperty("metrics", out var metricNode) &&
                    metricNode.ValueKind == JsonValueKind.Object ? metricNode : default;
                var costMicros = Decimal(metrics, "costMicros", "cost_micros");
                output.Add(new(
                    MarketingChannels.GoogleAds,
                    "campaign",
                    id!,
                    Text(campaign, "name") ?? id!,
                    Text(campaign, "status") ?? "unknown",
                    id,
                    null,
                    null,
                    costMicros / 1_000_000m,
                    Long(metrics, "impressions"),
                    Long(metrics, "clicks"),
                    (long)Math.Round(Decimal(metrics, "conversions"), 0, MidpointRounding.AwayFromZero),
                    row.Clone()));
            }
        }
        return output;
    }

    private async Task<IReadOnlyList<ProviderDeliveryMetricRow>> GetTikTokCampaignsAsync(
        MarketingOwnerScope owner,
        TimeRangeRequest range,
        CancellationToken ct)
    {
        var connection = await connections.GetProviderConnectionAsync(owner, MarketingDestinationKeys.TikTok, ct);
        if (!connection.Ready || string.IsNullOrWhiteSpace(connection.AccountId))
            throw new InvalidOperationException(connection.RequiresAccountSelection
                ? "TikTok Ads is connected but an advertiser account must be selected."
                : "TikTok Ads is not connected.");

        var accessToken = await oauth.GetTikTokAccessTokenAsync(owner, ct);
        var endpoint = Clean(configuration["TikTokAds:ReportingEndpoint"])
            ?? "https://business-api.tiktok.com/open_api/v1.3/report/integrated/get/";
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("TikTokAds:ReportingEndpoint is invalid.");

        var from = range.FromUtc.ToUniversalTime().Date;
        var to = range.ToUtc.ToUniversalTime().AddTicks(-1).Date;
        var output = new List<ProviderDeliveryMetricRow>();
        for (var page = 1; page <= 20; page++)
        {
            var url = QueryHelpers.AddQueryString(baseUri.ToString(),
                new Dictionary<string, string?>
                {
                    ["advertiser_id"] = connection.AccountId,
                    ["report_type"] = "BASIC",
                    ["data_level"] = "AUCTION_CAMPAIGN",
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "campaign_id" }),
                    ["metrics"] = JsonSerializer.Serialize(new[] { "campaign_name", "spend", "impressions", "clicks", "conversion" }),
                    ["start_date"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["end_date"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["page"] = page.ToString(CultureInfo.InvariantCulture),
                    ["page_size"] = "1000"
                });

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Access-Token", accessToken);
            using var response = await clients.CreateClient("MarketingExternalAds").SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw ProviderFailure("TikTok Ads reporting failed.", response.StatusCode, json);

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number &&
                code.TryGetInt32(out var providerCode) && providerCode != 0)
                throw new InvalidOperationException("TikTok Ads reporting request was rejected.");

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
                break;

            foreach (var row in list.EnumerateArray())
            {
                var dimensions = row.TryGetProperty("dimensions", out var d) && d.ValueKind == JsonValueKind.Object
                    ? d : default;
                var metrics = row.TryGetProperty("metrics", out var m) && m.ValueKind == JsonValueKind.Object
                    ? m : default;
                var id = Text(dimensions, "campaign_id") ?? Text(row, "campaign_id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                output.Add(new(
                    MarketingChannels.TikTokAds,
                    "campaign",
                    id!,
                    Text(metrics, "campaign_name") ?? Text(dimensions, "campaign_name") ?? id!,
                    "reported",
                    id,
                    null,
                    null,
                    Decimal(metrics, "spend"),
                    Long(metrics, "impressions"),
                    Long(metrics, "clicks"),
                    Long(metrics, "conversion", "conversions"),
                    row.Clone()));
            }

            var totalPages = data.TryGetProperty("page_info", out var pageInfo) &&
                pageInfo.ValueKind == JsonValueKind.Object
                ? (int)Long(pageInfo, "total_page", "total_pages")
                : page;
            if (totalPages <= page) break;
        }
        return output;
    }

    private string Required(string key) =>
        Clean(configuration[key]) ?? throw new InvalidOperationException($"{key} is required.");

    private static string? Digits(string? value)
    {
        var text = Clean(value)?.Replace("-", "", StringComparison.Ordinal);
        return !string.IsNullOrWhiteSpace(text) && text.All(char.IsDigit) ? text : null;
    }

    private static string? Text(JsonElement row, string name)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static long Long(JsonElement row, params string[] names)
    {
        foreach (var name in names)
        {
            var text = Text(row, name);
            if (long.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
                return value;
        }
        return 0;
    }

    private static decimal Decimal(JsonElement row, params string[] names)
    {
        foreach (var name in names)
        {
            var text = Text(row, name);
            if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
                return value;
        }
        return 0m;
    }

    private static InvalidOperationException ProviderFailure(
        string message,
        System.Net.HttpStatusCode status,
        string body)
    {
        return new InvalidOperationException($"{message} HTTP {(int)status}.");
    }

    private static string? Clean(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
