using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Messaging;

/// <summary>
/// Read-only Azure Resource Manager projection of the Translator resource that
/// the application already calls. It owns no local tier setting: the resource
/// SKU is the source, and the documented per-tier service limit is derived
/// from that SKU. Unknown SKUs fail closed rather than inventing capacity.
/// </summary>
internal interface IAzureTranslatorSubscriptionCapacitySource
{
    Task<AzureTranslatorSubscriptionCapacity> GetCurrentAsync(
        CancellationToken cancellationToken = default);
}

internal sealed record AzureTranslatorSubscriptionCapacity(
    bool IsAvailable,
    string Status,
    string? ResourceId,
    string? ResourceName,
    string? Tier,
    long? MonthlyIncludedCharacterAllowance,
    long? HourlyCharacterLimit,
    DateTime RefreshedUtc,
    string? Detail)
{
    public const int CapacityWindowMinutes = 60;
    public const int LiveReservePercent = 5;

    public long? MonthlyLiveReserveCharacters => MonthlyIncludedCharacterAllowance is { } capacity
        ? capacity * LiveReservePercent / 100
        : null;

    public long? MaximumSafeMonthlyCorpusCharacters => MonthlyIncludedCharacterAllowance is { } capacity
        ? Math.Max(0, capacity - (MonthlyLiveReserveCharacters ?? 0))
        : null;

    public long? HourlyLiveReserveCharacters => HourlyCharacterLimit is { } capacity
        ? capacity * LiveReservePercent / 100
        : null;

    public long? MaximumSafeHourlyCorpusCharacters => HourlyCharacterLimit is { } capacity
        ? Math.Max(0, capacity - (HourlyLiveReserveCharacters ?? 0))
        : null;
}

internal sealed class AzureTranslatorSubscriptionCapacitySource : IAzureTranslatorSubscriptionCapacitySource
{
    private const string ResourceManagerScope = "https://management.azure.com/.default";
    private static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromMinutes(2);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AzureTranslatorSubscriptionCapacitySource> _logger;
    private readonly TokenCredential _credential;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private AzureTranslatorSubscriptionCapacity? _cached;

    public AzureTranslatorSubscriptionCapacitySource(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<AzureTranslatorSubscriptionCapacitySource> logger,
        TokenCredential? credential = null)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
        _credential = credential ?? new DefaultAzureCredential();
    }

    public async Task<AzureTranslatorSubscriptionCapacity> GetCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        if (_cached is { } cached && now - cached.RefreshedUtc < MinimumRefreshInterval)
            return cached;

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            now = DateTime.UtcNow;
            if (_cached is { } refreshed && now - refreshed.RefreshedUtc < MinimumRefreshInterval)
                return refreshed;

            var resourceId = NormalizeResourceId(_configuration["AzureTranslator:ResourceId"]);
            if (resourceId is null)
                return _cached = Unavailable(now, "Azure Translator resource ID is not configured.");

            try
            {
                var token = await _credential.GetTokenAsync(
                    new TokenRequestContext([ResourceManagerScope]), cancellationToken);
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    resourceId + "?api-version=2024-10-01");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                using var response = await _httpClientFactory.CreateClient("AzureResourceManager")
                    .SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Azure Translator subscription capacity lookup failed. StatusCode={StatusCode}",
                        (int)response.StatusCode);
                    return _cached = Unavailable(now, "Azure did not authorize the Translator resource capacity lookup.");
                }

                using var document = JsonDocument.Parse(
                    await response.Content.ReadAsStreamAsync(cancellationToken));
                var resourceName = document.RootElement.TryGetProperty("name", out var name)
                    ? name.GetString()?.Trim()
                    : null;
                var sku = document.RootElement.TryGetProperty("sku", out var skuElement) &&
                          skuElement.TryGetProperty("name", out var skuName)
                    ? skuName.GetString()?.Trim().ToUpperInvariant()
                    : null;
                var limits = LimitsForTier(sku);
                return _cached = limits is null
                    ? Unavailable(now, $"Azure Translator tier '{sku ?? "unknown"}' has no recognized capacity contract.", resourceId, resourceName, sku)
                    : new AzureTranslatorSubscriptionCapacity(
                        true,
                        "Synchronized",
                        resourceId,
                        resourceName,
                        sku,
                        limits.MonthlyIncludedCharacterAllowance,
                        limits.HourlyCharacterLimit,
                        now,
                        limits.MonthlyIncludedCharacterAllowance is { } monthlyAllowance
                            ? $"Azure resource SKU is synchronized. The F0 tier includes {monthlyAllowance:N0} free characters per month and allows {limits.HourlyCharacterLimit:N0} characters per rolling hour. Character usage is measured from the canonical Legend reservation ledger because Azure does not expose an F0 character-usage metric."
                            : $"Azure resource SKU is synchronized. This tier has an Azure hourly velocity limit of {limits.HourlyCharacterLimit:N0} characters and no fixed monthly included-character allowance in the resource SKU.");
            }
            catch (CredentialUnavailableException exception)
            {
                _logger.LogWarning(exception, "Azure Translator capacity synchronization credential is unavailable.");
                return _cached = Unavailable(now, "The application identity cannot read the Azure Translator resource.", resourceId);
            }
            catch (AuthenticationFailedException exception)
            {
                _logger.LogWarning(exception, "Azure Translator capacity synchronization authentication failed.");
                return _cached = Unavailable(now, "The application identity is not authorized to read the Azure Translator resource.", resourceId);
            }
            catch (HttpRequestException exception)
            {
                _logger.LogWarning(exception, "Azure Translator capacity synchronization request failed.");
                return _cached = Unavailable(now, "Azure capacity synchronization is temporarily unavailable.", resourceId);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Azure Translator capacity synchronization timed out.");
                return _cached = Unavailable(now, "Azure capacity synchronization timed out.", resourceId);
            }
            catch (JsonException exception)
            {
                _logger.LogWarning(exception, "Azure Translator capacity synchronization response was invalid.");
                return _cached = Unavailable(now, "Azure returned an invalid Translator resource response.", resourceId);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Azure Translator capacity synchronization failed unexpectedly.");
                return _cached = Unavailable(now, "Azure capacity synchronization is temporarily unavailable.", resourceId);
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static AzureTranslatorSubscriptionCapacity Unavailable(
        DateTime refreshedUtc,
        string detail,
        string? resourceId = null,
        string? resourceName = null,
        string? tier = null) => new(
            false,
            "Unavailable",
            resourceId,
            resourceName,
            tier,
            null,
            null,
            refreshedUtc,
            detail);

    private static string? NormalizeResourceId(string? value)
    {
        var normalized = value?.Trim().TrimEnd('/');
        return !string.IsNullOrWhiteSpace(normalized) && normalized.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : null;
    }

    // Azure Translator standard-model capacity contracts. The F0 resource has
    // two independent constraints: its monthly free allowance and its rolling
    // hourly service-rate ceiling. Paid standard tiers are metered rather than
    // assigned a fixed monthly included-character allowance by their SKU.
    // The active SKU comes from Azure Resource Manager on every cache refresh;
    // this catalog translates Microsoft's documented tier identity into that
    // contract and deliberately refuses unknown tiers.
    private static TranslatorTierLimits? LimitsForTier(string? tier) => tier switch
    {
        "F0" => new TranslatorTierLimits(2_000_000, 2_000_000),
        "S1" or "S2" or "C2" => new TranslatorTierLimits(null, 40_000_000),
        "S3" or "C3" => new TranslatorTierLimits(null, 120_000_000),
        "S4" or "C4" => new TranslatorTierLimits(null, 200_000_000),
        _ => null
    };

    private sealed record TranslatorTierLimits(
        long? MonthlyIncludedCharacterAllowance,
        long HourlyCharacterLimit);
}
