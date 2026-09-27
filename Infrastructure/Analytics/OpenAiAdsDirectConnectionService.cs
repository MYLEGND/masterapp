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

public interface IOpenAiAdsDirectConnectionService
{
    Task<OpenAiAdsConnectionSnapshot> ConnectAsync(
        MarketingOwnerScope owner,
        string advertiserApiKey,
        Guid? expectedRevision = null,
        CancellationToken cancellationToken = default);

    Task<OpenAiAdsProviderAccountSnapshot?> InspectAsync(
        MarketingOwnerScope owner,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Verifies an account-scoped OpenAI Advertiser API key against the provider,
/// projects live provider account readiness, and provisions canonical conversion
/// resources through the existing MarketingConnection authority.
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
            Permissions: new[] { "ad_account.read", "conversion_setup" },
            PixelId: measurement.PixelId,
            ConversionDataSourceId: measurement.DataSourceId,
            VerifiedUtc: DateTime.UtcNow);

        return await authority.BindVerifiedAsync(
            owner,
            verified,
            new OpenAiAdsConnectionSecrets(
                ManagementApiKey: key,
                ConversionsApiKey: measurement.ConversionsApiKey),
            expectedRevision,
            cancellationToken);
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

    private async Task<(string? PixelId, string? DataSourceId, string? ConversionsApiKey)> ResolveMeasurementAsync(
        string key,
        string accountName,
        OpenAiAdsConnectionSnapshot? existing,
        OpenAiAdsConnectionSecrets existingSecrets,
        CancellationToken cancellationToken)
    {
        string? pixelId = existing?.PixelId;
        string? dataSourceId = existing?.ConversionDataSourceId;
        string? capiKey = existingSecrets.ConversionsApiKey;

        if (string.IsNullOrWhiteSpace(pixelId) || string.IsNullOrWhiteSpace(dataSourceId))
        {
            var listed = await TryListPixelsAsync(key, cancellationToken);
            if (listed.Available && listed.Source is not null)
            {
                pixelId = listed.Source.Value.PixelId;
                dataSourceId = listed.Source.Value.Id;
            }
            else if (listed.Available)
            {
                var created = await CreatePixelAsync(key, $"{accountName} website", cancellationToken);
                pixelId = created.PixelId;
                dataSourceId = created.Id;
            }
        }

        if (string.IsNullOrWhiteSpace(capiKey))
            capiKey = await TryCreateConversionsApiKeyAsync(key, $"{accountName} production conversions", cancellationToken);

        return (pixelId, dataSourceId, capiKey);
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

    private async Task<(bool Available, (string Id, string PixelId)? Source)> TryListPixelsAsync(
        string key,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"{BaseUrl}/conversions/pixels", key);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return (false, null);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenAI Pixel lookup failed with HTTP {(int)response.StatusCode}.");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return (true, null);

        foreach (var item in data.EnumerateArray())
        {
            var id = ReadString(item, "id");
            var pixelId = ReadString(item, "pixel_id");
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(pixelId))
                return (true, (id!, pixelId!));
        }

        return (true, null);
    }

    private async Task<(string Id, string PixelId)> CreatePixelAsync(
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
            throw new HttpRequestException($"OpenAI Pixel provisioning failed with HTTP {(int)response.StatusCode}.");

        using var json = JsonDocument.Parse(body);
        return (Required(json.RootElement, "id", 200), Required(json.RootElement, "pixel_id", 200));
    }

    private async Task<string?> TryCreateConversionsApiKeyAsync(
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
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenAI Conversions API key provisioning failed with HTTP {(int)response.StatusCode}.");

        using var json = JsonDocument.Parse(body);
        return Required(json.RootElement, "api_key", 4096);
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
