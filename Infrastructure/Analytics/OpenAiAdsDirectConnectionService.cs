using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Shared.Analytics;

namespace Infrastructure.Analytics;

public interface IOpenAiAdsDirectConnectionService
{
    Task<OpenAiAdsConnectionSnapshot> ConnectAsync(
        MarketingOwnerScope owner,
        string advertiserApiKey,
        Guid? expectedRevision = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Verifies an account-scoped OpenAI Advertiser API key against the provider,
/// then persists the verified account through the canonical connection authority.
/// Browser-provided account identifiers are never accepted as ownership evidence.
/// </summary>
public sealed class OpenAiAdsDirectConnectionService(
    HttpClient httpClient,
    IOpenAiAdsAccountConnectionAuthority authority) : IOpenAiAdsDirectConnectionService
{
    public async Task<OpenAiAdsConnectionSnapshot> ConnectAsync(
        MarketingOwnerScope owner,
        string advertiserApiKey,
        Guid? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var key = advertiserApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 4096 || key.Any(char.IsControl))
            throw new ArgumentException("Enter a valid OpenAI Advertiser API key.", nameof(advertiserApiKey));

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.ads.openai.com/v1/ad_account");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("OpenAI rejected this Advertiser API key.");

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenAI Ads account verification failed with HTTP {(int)response.StatusCode}.");

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var accountId = Required(root, "id", 100);
        var accountName = Required(root, "name", 300);
        var reviewStatus = ReadNested(root, "review", "status") ?? OpenAiAdsReviewStatuses.InReview;
        if (reviewStatus is not (OpenAiAdsReviewStatuses.Approved or OpenAiAdsReviewStatuses.InReview or OpenAiAdsReviewStatuses.Rejected))
            reviewStatus = OpenAiAdsReviewStatuses.InReview;

        var existing = await authority.GetAsync(owner, cancellationToken);
        var preserveMeasurement = existing.Connected &&
                                  string.Equals(existing.AccountId, accountId, StringComparison.Ordinal) &&
                                  existing.PixelConfigured;

        var verified = new VerifiedOpenAiAdsAccount(
            accountId,
            accountName,
            Role: null,
            ReviewStatus: reviewStatus,
            AuthorizationMethod: OpenAiAdsAuthorizationMethods.ApiKey,
            Permissions: new[] { "ad_account.read" },
            PixelId: preserveMeasurement ? existing.PixelId : null,
            ConversionDataSourceId: preserveMeasurement ? existing.ConversionDataSourceId : null,
            VerifiedUtc: DateTime.UtcNow);

        var existingSecrets = preserveMeasurement
            ? await authority.GetSecretsAsync(owner, cancellationToken)
            : new OpenAiAdsConnectionSecrets();

        return await authority.BindVerifiedAsync(
            owner,
            verified,
            new OpenAiAdsConnectionSecrets(
                ManagementApiKey: key,
                ConversionsApiKey: preserveMeasurement ? existingSecrets.ConversionsApiKey : null),
            expectedRevision,
            cancellationToken);
    }

    private static string Required(JsonElement root, string property, int max)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"OpenAI Ads verification did not return {property}.");
        var text = value.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Length > max || text.Any(char.IsControl))
            throw new InvalidOperationException($"OpenAI Ads verification returned invalid {property}.");
        return text;
    }

    private static string? ReadNested(JsonElement root, string parent, string child)
    {
        if (!root.TryGetProperty(parent, out var parentElement) || parentElement.ValueKind != JsonValueKind.Object)
            return null;
        if (!parentElement.TryGetProperty(child, out var childElement) || childElement.ValueKind != JsonValueKind.String)
            return null;
        return childElement.GetString()?.Trim().ToLowerInvariant();
    }
}
