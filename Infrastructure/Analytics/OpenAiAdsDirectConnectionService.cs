using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Shared.Analytics;

namespace Infrastructure.Analytics;

public sealed record OpenAiAdsProviderAccountSnapshot(
    string AccountId,
    string AccountName,
    string? AccountUrl,
    string? PreviewUrl,
    string? Status,
    string? Timezone,
    string? CurrencyCode,
    string ReviewStatus,
    string? ReviewReason);

public sealed record OpenAiAdsMeasurementCapabilitySnapshot(
    string Status,
    int? HttpStatusCode,
    string? Detail)
{
    public bool ApiAvailable => Status is "available" or "configured";
}

public sealed record OpenAiAdsRefreshResult(
    OpenAiAdsConnectionSnapshot Connection,
    OpenAiAdsMeasurementCapabilitySnapshot PixelProvisioning);

public interface IOpenAiAdsDirectConnectionService
{
    Task<OpenAiAdsConnectionSnapshot> ConnectAsync(
        MarketingOwnerScope owner,
        string advertiserApiKey,
        Guid? expectedRevision = null,
        CancellationToken cancellationToken = default);

    Task<OpenAiAdsRefreshResult> RefreshAsync(
        MarketingOwnerScope owner,
        Guid expectedRevision,
        CancellationToken cancellationToken = default);

    Task<OpenAiAdsProviderAccountSnapshot?> InspectAsync(
        MarketingOwnerScope owner,
        CancellationToken cancellationToken = default);

    Task<OpenAiAdsMeasurementCapabilitySnapshot?> InspectMeasurementAsync(
        MarketingOwnerScope owner,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Verifies an account-scoped OpenAI Advertiser API key against the provider,
/// then persists that verified binding through the canonical MarketingConnection
/// authority. Conversion provisioning is intentionally fail-soft: an independently
/// unavailable measurement capability never invalidates a successfully verified ad
/// account or replaces existing measurement secrets.
/// </summary>
public sealed class OpenAiAdsDirectConnectionService(
    HttpClient httpClient,
    IOpenAiAdsAccountConnectionAuthority authority) : IOpenAiAdsDirectConnectionService
{
    private const string BaseUrl = "https://api.ads.openai.com/v1";

    public async Task<OpenAiAdsConnectionSnapshot> ConnectAsync(
        MarketingOwnerScope owner,
        string advertiserApiKey,
        Guid? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        var result = await ConnectCoreAsync(owner, advertiserApiKey, expectedRevision, cancellationToken);
        return result.Connection;
    }

    public async Task<OpenAiAdsRefreshResult> RefreshAsync(
        MarketingOwnerScope owner,
        Guid expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var connection = await authority.GetAsync(owner, cancellationToken);
        if (!connection.Connected)
            throw new InvalidOperationException("ChatGPT Ads is not connected for this scope.");

        var secrets = await authority.GetSecretsAsync(owner, cancellationToken);
        if (string.IsNullOrWhiteSpace(secrets.ManagementApiKey))
            throw new InvalidOperationException("The scoped Advertiser API credential is unavailable.");

        return await ConnectCoreAsync(owner, secrets.ManagementApiKey, expectedRevision, cancellationToken);
    }

    private async Task<OpenAiAdsRefreshResult> ConnectCoreAsync(
        MarketingOwnerScope owner,
        string advertiserApiKey,
        Guid? expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var key = CleanKey(advertiserApiKey);

        var provider = await ReadAccountAsync(key, cancellationToken);
        var existing = await authority.GetAsync(owner, cancellationToken);
        var sameAccount = existing.Connected &&
                          string.Equals(existing.AccountId, provider.AccountId, StringComparison.Ordinal);
        var existingSecrets = sameAccount
            ? await authority.GetSecretsAsync(owner, cancellationToken)
            : new OpenAiAdsConnectionSecrets();

        var measurement = await ResolveMeasurementAsync(
            key,
            provider.AccountName,
            sameAccount ? existing : null,
            existingSecrets,
            cancellationToken);

        var verified = new VerifiedOpenAiAdsAccount(
            provider.AccountId,
            provider.AccountName,
            Role: null,
            ReviewStatus: provider.ReviewStatus,
            AuthorizationMethod: OpenAiAdsAuthorizationMethods.ApiKey,
            Permissions: new[] { "ad_account.read" },
            PixelId: measurement.PixelId,
            ConversionDataSourceId: measurement.DataSourceId,
            VerifiedUtc: DateTime.UtcNow);

        var bound = await authority.BindVerifiedAsync(
            owner,
            verified,
            new OpenAiAdsConnectionSecrets(
                ManagementApiKey: key,
                ConversionsApiKey: measurement.ConversionsApiKey),
            expectedRevision,
            cancellationToken);

        return new OpenAiAdsRefreshResult(
            bound,
            new OpenAiAdsMeasurementCapabilitySnapshot(
                measurement.PixelStatus,
                measurement.PixelHttpStatusCode,
                measurement.PixelDetail));
    }

    public async Task<OpenAiAdsProviderAccountSnapshot?> InspectAsync(
        MarketingOwnerScope owner,
        CancellationToken cancellationToken = default)
    {
        var connection = await authority.GetAsync(owner, cancellationToken);
        if (!connection.Connected) return null;

        var secrets = await authority.GetSecretsAsync(owner, cancellationToken);
        if (string.IsNullOrWhiteSpace(secrets.ManagementApiKey)) return null;

        return await ReadAccountAsync(secrets.ManagementApiKey, cancellationToken);
    }

    public async Task<OpenAiAdsMeasurementCapabilitySnapshot?> InspectMeasurementAsync(
        MarketingOwnerScope owner,
        CancellationToken cancellationToken = default)
    {
        var connection = await authority.GetAsync(owner, cancellationToken);
        if (!connection.Connected) return null;
        if (connection.PixelConfigured && connection.ConversionsApiConfigured)
            return new("configured", null, "Pixel and Conversions API are configured.");

        var secrets = await authority.GetSecretsAsync(owner, cancellationToken);
        if (string.IsNullOrWhiteSpace(secrets.ManagementApiKey)) return null;

        var pixels = await ListPixelsAsync(secrets.ManagementApiKey, cancellationToken);
        if (pixels.Status != "available") return new(pixels.Status, pixels.HttpStatusCode, pixels.Detail);

        return new(
            "available",
            pixels.HttpStatusCode,
            connection.PixelConfigured
                ? "Pixel capability is available; Conversions API setup is incomplete."
                : "Conversion provisioning is available; measurement setup is incomplete.");
    }

    private async Task<(string? PixelId, string? DataSourceId, string? ConversionsApiKey, string PixelStatus, int? PixelHttpStatusCode, string? PixelDetail)> ResolveMeasurementAsync(
        string key,
        string accountName,
        OpenAiAdsConnectionSnapshot? existing,
        OpenAiAdsConnectionSecrets existingSecrets,
        CancellationToken cancellationToken)
    {
        // Preserve anything already working. Provisioning never clears canonical
        // measurement state just because the provider temporarily rejects a setup call.
        string? pixelId = existing?.PixelId;
        string? dataSourceId = existing?.ConversionDataSourceId;
        string? capiKey = existingSecrets.ConversionsApiKey;
        var pixelStatus = !string.IsNullOrWhiteSpace(pixelId) && !string.IsNullOrWhiteSpace(dataSourceId)
            ? "configured"
            : "available";
        int? pixelHttpStatusCode = null;
        string? pixelDetail = pixelStatus == "configured" ? "Pixel is configured." : null;

        if (string.IsNullOrWhiteSpace(pixelId) || string.IsNullOrWhiteSpace(dataSourceId))
        {
            var listed = await ListPixelsAsync(key, cancellationToken);
            pixelStatus = listed.Status;
            pixelHttpStatusCode = listed.HttpStatusCode;
            pixelDetail = listed.Detail;
            if (listed.Source is not null)
            {
                pixelId = listed.Source.Value.PixelId;
                dataSourceId = listed.Source.Value.Id;
                pixelStatus = "configured";
                pixelDetail = "Existing OpenAI Pixel reused.";
            }
            else if (listed.Status == "available")
            {
                var created = await TryCreatePixelAsync(key, $"{accountName} website", cancellationToken);
                pixelStatus = created.Status;
                pixelHttpStatusCode = created.HttpStatusCode;
                pixelDetail = created.Detail;
                if (created.Source is not null)
                {
                    pixelId = created.Source.Value.PixelId;
                    dataSourceId = created.Source.Value.Id;
                    pixelStatus = "configured";
                    pixelDetail = "OpenAI Pixel created.";
                }
            }
        }

        if (string.IsNullOrWhiteSpace(capiKey))
        {
            var createdKey = await TryCreateConversionsApiKeyAsync(
                key,
                $"{accountName} production conversions",
                cancellationToken);
            capiKey = createdKey.ApiKey;
        }

        return (pixelId, dataSourceId, capiKey, pixelStatus, pixelHttpStatusCode, pixelDetail);
    }

    private async Task<OpenAiAdsProviderAccountSnapshot> ReadAccountAsync(string key, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"{BaseUrl}/ad_account", key);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("OpenAI rejected this Advertiser API key.");

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenAI Ads account verification failed with HTTP {(int)response.StatusCode}.");

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var reviewStatus = ReadNested(root, "review", "status") ?? OpenAiAdsReviewStatuses.InReview;
        if (reviewStatus is not (OpenAiAdsReviewStatuses.Approved or OpenAiAdsReviewStatuses.InReview or OpenAiAdsReviewStatuses.Rejected))
            reviewStatus = OpenAiAdsReviewStatuses.InReview;

        return new(
            Required(root, "id", 100),
            Required(root, "name", 300),
            ReadString(root, "url"),
            ReadString(root, "preview_url"),
            ReadString(root, "status"),
            ReadString(root, "timezone"),
            ReadString(root, "currency_code"),
            reviewStatus,
            ReadNested(root, "review", "reason"));
    }

    private async Task<(string Status, int? HttpStatusCode, string? Detail, (string Id, string PixelId)? Source)> ListPixelsAsync(
        string key,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"{BaseUrl}/conversions/pixels", key);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var status = MeasurementStatus(response.StatusCode);
            return (status, (int)response.StatusCode, SafeProviderDetail(body, status), null);
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return ("provider_error", (int)response.StatusCode, "OpenAI returned an invalid Pixel list response.", null);

            foreach (var item in data.EnumerateArray())
            {
                var id = ReadString(item, "id");
                var candidatePixelId = ReadString(item, "pixel_id");
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(candidatePixelId))
                    return ("available", (int)response.StatusCode, null, (id!, candidatePixelId!));
            }

            return ("available", (int)response.StatusCode, null, null);
        }
        catch (JsonException)
        {
            return ("provider_error", (int)response.StatusCode, "OpenAI returned an invalid Pixel list response.", null);
        }
    }

    private async Task<(string Status, int? HttpStatusCode, string? Detail, (string Id, string PixelId)? Source)> TryCreatePixelAsync(
        string key,
        string name,
        CancellationToken cancellationToken)
    {
        using var request = CreateJsonRequest(
            HttpMethod.Post,
            $"{BaseUrl}/conversions/pixels",
            key,
            new { name, client_type = "web" });
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var status = MeasurementStatus(response.StatusCode);
            return (status, (int)response.StatusCode, SafeProviderDetail(body, status), null);
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            var id = Required(json.RootElement, "id", 200);
            var pixelId = Required(json.RootElement, "pixel_id", 200);
            return ("configured", (int)response.StatusCode, null, (id, pixelId));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return ("provider_error", (int)response.StatusCode, "OpenAI returned an invalid Pixel creation response.", null);
        }
    }

    private async Task<(string Status, int? HttpStatusCode, string? Detail, string? ApiKey)> TryCreateConversionsApiKeyAsync(
        string key,
        string name,
        CancellationToken cancellationToken)
    {
        using var request = CreateJsonRequest(
            HttpMethod.Post,
            $"{BaseUrl}/conversions/api_keys",
            key,
            new { name });
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var status = MeasurementStatus(response.StatusCode);
            return (status, (int)response.StatusCode, SafeProviderDetail(body, status), null);
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            return ("configured", (int)response.StatusCode, null, Required(json.RootElement, "api_key", 4096));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return ("provider_error", (int)response.StatusCode, "OpenAI returned an invalid Conversions API key response.", null);
        }
    }

    private static string MeasurementStatus(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.NotFound => "not_enabled",
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "not_authorized",
        _ when (int)statusCode >= 500 => "provider_unavailable",
        _ => "provider_error"
    };

    private static string SafeProviderDetail(string body, string status)
    {
        var fallback = status switch
        {
            "not_enabled" => "OpenAI has not enabled Ads API conversion provisioning for this ad account.",
            "not_authorized" => "This Advertiser API credential cannot manage conversion provisioning for this ad account.",
            "provider_unavailable" => "OpenAI conversion provisioning is temporarily unavailable.",
            _ => "OpenAI rejected the conversion provisioning request."
        };

        if (string.IsNullOrWhiteSpace(body)) return fallback;
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            var detail = ReadString(root, "message")
                         ?? ReadString(root, "detail")
                         ?? (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                             ? ReadString(error, "message")
                             : null);
            if (string.IsNullOrWhiteSpace(detail)) return fallback;
            detail = detail.Trim();
            if (detail.Length > 240) detail = detail[..240];
            return detail.Any(char.IsControl) ? fallback : detail;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string key)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private static HttpRequestMessage CreateJsonRequest(HttpMethod method, string url, string key, object payload)
    {
        var request = CreateRequest(method, url, key);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return request;
    }

    private static string CleanKey(string? value)
    {
        var key = value?.Trim();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 4096 || key.Any(char.IsControl))
            throw new ArgumentException("Enter a valid OpenAI Advertiser API key.", nameof(value));
        return key;
    }

    private static string Required(JsonElement root, string property, int max)
    {
        var text = ReadString(root, property);
        if (string.IsNullOrWhiteSpace(text) || text.Length > max || text.Any(char.IsControl))
            throw new InvalidOperationException($"OpenAI Ads verification returned invalid {property}.");
        return text;
    }

    private static string? ReadString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        return value.GetString()?.Trim();
    }

    private static string? ReadNested(JsonElement root, string parent, string child)
    {
        if (!root.TryGetProperty(parent, out var parentElement) || parentElement.ValueKind != JsonValueKind.Object)
            return null;
        return ReadString(parentElement, child)?.ToLowerInvariant();
    }
}
