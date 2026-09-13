using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Domain.Messaging;
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

    Task<AzureTranslatorSubscriptionCapacity> GetCurrentAsync(
        CancellationToken cancellationToken,
        LegendConnectExternalProviderPolicy? providerPolicy) =>
        LegendConnectExternalProviderPolicy.Resolve(providerPolicy).ForbidsExternalProviders
            ? Task.FromException<AzureTranslatorSubscriptionCapacity>(new InvalidOperationException(
                "native_only_capacity_snapshot_policy_unavailable"))
            : GetCurrentAsync(cancellationToken);
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
    public long? MonthlyAzureReportedCharacters { get; init; }
    public DateTime? AzureUsageRetrievedUtc { get; init; }
    // Requested query end, used only as a ledger accounting anchor. Azure
    // telemetry is delayed and is not guaranteed complete through this instant.
    public DateTime? AzureUsageQueryEndUtc { get; init; }
    public string? UsageDetail { get; init; }

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
    // Capacity is an operational safety signal, not a page-load dependency.
    // Bound a cold Azure AD/ARM refresh so Founder operations fail closed
    // promptly when Azure cannot be reached.
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(3);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AzureTranslatorSubscriptionCapacitySource> _logger;
    private readonly TokenCredential _credential;
    private readonly TimeSpan _refreshTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private AzureTranslatorSubscriptionCapacity? _cached;

    public AzureTranslatorSubscriptionCapacitySource(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<AzureTranslatorSubscriptionCapacitySource> logger,
        TokenCredential? credential = null,
        TimeSpan? refreshTimeout = null,
        TimeProvider? timeProvider = null)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
        _credential = credential ?? new DefaultAzureCredential();
        _refreshTimeout = refreshTimeout ?? RefreshTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<AzureTranslatorSubscriptionCapacity> GetCurrentAsync(
        CancellationToken cancellationToken = default) =>
        GetCurrentAsync(cancellationToken, providerPolicy: null);

    public async Task<AzureTranslatorSubscriptionCapacity> GetCurrentAsync(
        CancellationToken cancellationToken,
        LegendConnectExternalProviderPolicy? providerPolicy)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (LegendConnectExternalProviderPolicy.Resolve(providerPolicy).ForbidsExternalProviders)
        {
            // A local diagnostic may inspect an already synchronized, fresh
            // snapshot. It never waits for or starts an external refresh, and
            // its result cannot replace the provider-enabled shared cache.
            return _cached is { } local && now - local.RefreshedUtc < MinimumRefreshInterval && local.RefreshedUtc.Year == now.Year && local.RefreshedUtc.Month == now.Month
                ? local with
                {
                    Status = local.IsAvailable ? "Cached" : local.Status,
                    Detail = "Native-only cached Azure capacity observation; external refresh was not attempted. " + local.Detail
                }
                : Unavailable(now,
                    "native_only_capacity_refresh_forbidden: no fresh cached Azure capacity observation is available.");
        }
        if (_cached is { } cached && now - cached.RefreshedUtc < MinimumRefreshInterval && cached.RefreshedUtc.Year == now.Year && cached.RefreshedUtc.Month == now.Month)
            return cached;

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            now = _timeProvider.GetUtcNow().UtcDateTime;
            if (_cached is { } refreshed && now - refreshed.RefreshedUtc < MinimumRefreshInterval && refreshed.RefreshedUtc.Year == now.Year && refreshed.RefreshedUtc.Month == now.Month)
                return refreshed;

            var resourceId = NormalizeResourceId(_configuration["AzureTranslator:ResourceId"]);
            if (resourceId is null)
                return _cached = Unavailable(now, "Azure Translator resource ID is not configured.");

            try
            {
                using var refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                refreshCancellation.CancelAfter(_refreshTimeout);
                var refreshToken = refreshCancellation.Token;
                var token = await _credential.GetTokenAsync(
                    new TokenRequestContext([ResourceManagerScope]), refreshToken);
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    resourceId + "?api-version=2024-10-01");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                using var response = await _httpClientFactory.CreateClient("AzureResourceManager")
                    .SendAsync(request, refreshToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Azure Translator subscription capacity lookup failed. StatusCode={StatusCode}",
                        (int)response.StatusCode);
                    var detail = response.StatusCode switch
                    {
                        System.Net.HttpStatusCode.Unauthorized => "Azure could not authenticate the application identity for the Translator resource lookup.",
                        System.Net.HttpStatusCode.Forbidden => "The application identity is not authorized to read the Azure Translator resource. Verify its resource permissions.",
                        System.Net.HttpStatusCode.NotFound => "The configured Azure Translator resource was not found. Verify the resource ID.",
                        System.Net.HttpStatusCode.TooManyRequests => "Azure temporarily throttled the Translator resource lookup. Capacity is unavailable until a later refresh succeeds.",
                        _ when (int)response.StatusCode >= 500 => "Azure Resource Manager is temporarily unavailable. Capacity could not be verified.",
                        _ => $"Azure rejected the Translator resource lookup (HTTP {(int)response.StatusCode}). Verify the resource configuration."
                    };
                    return _cached = Unavailable(now, detail, resourceId);
                }

                using var document = JsonDocument.Parse(
                    await response.Content.ReadAsStreamAsync(refreshToken));
                var resourceName = document.RootElement.TryGetProperty("name", out var name)
                    ? name.GetString()?.Trim()
                    : null;
                var sku = document.RootElement.TryGetProperty("sku", out var skuElement) &&
                          skuElement.TryGetProperty("name", out var skuName)
                    ? skuName.GetString()?.Trim().ToUpperInvariant()
                    : null;
                var limits = LimitsForTier(sku);
                var capacity = limits is null
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
                            ? $"Azure resource SKU is synchronized. The F0 tier includes {monthlyAllowance:N0} free characters per month and allows {limits.HourlyCharacterLimit:N0} characters per rolling hour. Provider character telemetry is queried from Azure Monitor; live reservations are measured separately in the canonical Legend ledger."
                            : $"Azure resource SKU is synchronized. This tier has an Azure hourly velocity limit of {limits.HourlyCharacterLimit:N0} characters and no fixed monthly included-character allowance in the resource SKU.");
                if (capacity.IsAvailable)
                    capacity = await ObserveUsageAsync(capacity, token.Token, now, refreshToken);
                cancellationToken.ThrowIfCancellationRequested();
                return _cached = capacity;
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

    private async Task<AzureTranslatorSubscriptionCapacity> ObserveUsageAsync(
        AzureTranslatorSubscriptionCapacity capacity, string accessToken, DateTime now, CancellationToken cancellationToken)
    {
        // Monitor is delayed telemetry, not a real-time billing balance. Keep it
        // separate from the reservation ledger to avoid double counting requests.
        try
        {
            var start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var timespan = Uri.EscapeDataString($"{start:O}/{now:O}");
            using var request = new HttpRequestMessage(HttpMethod.Get,
                capacity.ResourceId + "/providers/microsoft.insights/metrics?api-version=2023-10-01" +
                "&metricnames=TextCharactersTranslated&aggregation=Total&interval=FULL&timespan=" + timespan);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await _httpClientFactory.CreateClient("AzureResourceManager").SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return capacity with { UsageDetail = "Azure tier connected; Azure Monitor usage is unavailable. Legend ledger usage and remaining capacity are estimates." };
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var total = ReadMonthlyTranslatedCharacters(document.RootElement);
            return capacity with
            {
                MonthlyAzureReportedCharacters = total,
                AzureUsageRetrievedUtc = total.HasValue ? _timeProvider.GetUtcNow().UtcDateTime : null,
                AzureUsageQueryEndUtc = total.HasValue ? now : null,
                UsageDetail = total.HasValue
                    ? "Azure Monitor reports delayed month-to-date text-character telemetry separately from live Legend reservations. It is not an invoice balance. Monthly protection uses the larger of completed Legend usage and Azure reported consumption plus Legend completions after the Azure query end, then adds in-flight reservations. The query end is an accounting anchor, not verified telemetry coverage; overlap can conservatively reduce availability and pre-anchor reporting lag remains unknown. Hourly protection uses the rolling Legend ledger. Delayed or external usage may still differ."
                    : "Azure Monitor returned no character observations. Usage is unknown, not zero; Legend ledger remaining capacity is an estimate."
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException or OverflowException or InvalidOperationException)
        {
            return capacity with { UsageDetail = "Azure tier connected; Azure Monitor usage could not be refreshed. Legend ledger usage and remaining capacity are estimates." };
        }
    }

    internal static long? ReadMonthlyTranslatedCharacters(JsonElement root)
    {
        if (!root.TryGetProperty("value", out var metrics) || metrics.ValueKind != JsonValueKind.Array)
            return null;
        decimal total = 0;
        var observed = false;
        foreach (var metric in metrics.EnumerateArray())
        {
            if (metric.ValueKind != JsonValueKind.Object ||
                !metric.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.Object ||
                !name.TryGetProperty("value", out var metricName) || metricName.ValueKind != JsonValueKind.String)
                return null;
            if (metricName.GetString() != "TextCharactersTranslated")
                continue;
            if (!metric.TryGetProperty("timeseries", out var series) ||
                series.ValueKind != JsonValueKind.Array || series.GetArrayLength() == 0)
                return null;
            foreach (var item in series.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                    return null;
                foreach (var point in data.EnumerateArray())
                {
                    // A partial sum would masquerade as a complete monthly
                    // observation. Missing, fractional or invalid points make
                    // the provider total unknown, even if other points are valid.
                    if (point.ValueKind != JsonValueKind.Object ||
                        !point.TryGetProperty("total", out var value) || value.ValueKind != JsonValueKind.Number ||
                        !value.TryGetDecimal(out var count) || count < 0 || count != decimal.Truncate(count))
                        return null;
                    if (count > long.MaxValue - total)
                        return null;
                    total += count;
                    observed = true;
                }
            }
        }
        return observed ? checked((long)total) : null;
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
