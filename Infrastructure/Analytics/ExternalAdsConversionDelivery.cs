using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Analytics;

namespace Infrastructure.Analytics;

public sealed record ExternalAdsConversionApiResult(
    bool Sent,
    bool Retryable,
    int? HttpStatusCode,
    string Status,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    string? ProviderReceiptJson = null);

public interface IExternalAdsConversionApiService
{
    Task<ExternalAdsConversionApiResult> SendAsync(
        MarketingOwnerScope owner,
        MarketingProviderConnectionSnapshot connection,
        MarketingProviderMeasurementConfiguration measurement,
        MarketingProviderEventMapping mapping,
        AnalyticsEvent source,
        string clickReference,
        CancellationToken ct = default);
}

/// <summary>
/// Provider transports only. Canonical conversion truth stays in AnalyticsEvents and
/// delivery state stays in MarketingDestinationDelivery.
/// </summary>
public sealed class ExternalAdsConversionApiService(
    HttpClient httpClient,
    MarketingExternalAdsOAuthService oauth,
    MarketingConnectionStore connections,
    IConfiguration configuration) : IExternalAdsConversionApiService
{
    public Task<ExternalAdsConversionApiResult> SendAsync(
        MarketingOwnerScope owner,
        MarketingProviderConnectionSnapshot connection,
        MarketingProviderMeasurementConfiguration measurement,
        MarketingProviderEventMapping mapping,
        AnalyticsEvent source,
        string clickReference,
        CancellationToken ct = default) =>
        measurement.Provider switch
        {
            MarketingDestinationKeys.Google => SendGoogleAsync(owner, connection, mapping, source, clickReference, ct),
            MarketingDestinationKeys.TikTok => SendTikTokAsync(owner, measurement, mapping, source, clickReference, ct),
            _ => Task.FromResult(Invalid("unsupported_provider"))
        };

    private async Task<ExternalAdsConversionApiResult> SendGoogleAsync(
        MarketingOwnerScope owner,
        MarketingProviderConnectionSnapshot connection,
        MarketingProviderEventMapping mapping,
        AnalyticsEvent source,
        string gclid,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connection.AccountId))
            return Invalid("google_account_required");
        if (string.IsNullOrWhiteSpace(mapping.DestinationId))
            return Invalid("google_conversion_action_required");
        if (PaidAdsClickReference.NormalizeGoogle(gclid) is not { } click)
            return Invalid("google_click_reference_required");

        var version = Clean(configuration["GoogleAds:ApiVersion"]) ?? "v25";
        if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^v\d{1,3}$"))
            return Invalid("google_api_version_invalid");

        var access = await oauth.GetGoogleAccessAsync(owner, ct);
        var canonicalId = CanonicalAdvertisingEventProjection.ResolveEventId(source);
        var value = CanonicalConversionValueProjection.Resolve(source.MetadataJson);
        var amount = value?.AmountMinorUnits / 100m ?? 0m;
        var currency = value?.Currency ?? "USD";
        var eventUtc = DateTime.SpecifyKind(source.EventUtc, DateTimeKind.Utc);
        var conversionDateTime = eventUtc.ToString("yyyy-MM-dd HH:mm:ss+00:00", CultureInfo.InvariantCulture);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://googleads.googleapis.com/{version}/customers/{Digits(connection.AccountId)}:uploadClickConversions")
        {
            Content = JsonContent.Create(new
            {
                conversions = new[]
                {
                    new
                    {
                        conversionAction = mapping.DestinationId,
                        gclid = click,
                        conversionValue = amount,
                        conversionDateTime,
                        currencyCode = currency,
                        orderId = canonicalId
                    }
                },
                partialFailure = true
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.AccessToken);
        request.Headers.TryAddWithoutValidation("developer-token",
            Required("GoogleAds:DeveloperToken"));
        var loginCustomer = Digits(configuration["GoogleAds:LoginCustomerId"]);
        if (!string.IsNullOrWhiteSpace(loginCustomer))
            request.Headers.TryAddWithoutValidation("login-customer-id", loginCustomer);

        return await SendProviderAsync(
            request,
            "google",
            responseBody =>
            {
                if (string.IsNullOrWhiteSpace(responseBody))
                    return null;
                try
                {
                    using var json = JsonDocument.Parse(responseBody);
                    if (json.RootElement.TryGetProperty("partialFailureError", out var partial) &&
                        partial.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) &&
                        partial.GetRawText() != "{}")
                        return "google_partial_failure";
                    return null;
                }
                catch (JsonException)
                {
                    return "google_invalid_response";
                }
            },
            ct);
    }

    private async Task<ExternalAdsConversionApiResult> SendTikTokAsync(
        MarketingOwnerScope owner,
        MarketingProviderMeasurementConfiguration measurement,
        MarketingProviderEventMapping mapping,
        AnalyticsEvent source,
        string ttclid,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(measurement.EventSourceId))
            return Invalid("tiktok_event_source_required");
        if (PaidAdsClickReference.NormalizeTikTok(ttclid) is not { } click)
            return Invalid("tiktok_click_reference_required");

        var accessToken = await connections.GetProviderMeasurementAccessTokenAsync(owner, MarketingDestinationKeys.TikTok, ct);
        if (string.IsNullOrWhiteSpace(accessToken))
            return Invalid("tiktok_events_access_token_required");
        var endpoint = Clean(configuration["TikTokAds:EventsEndpoint"])
            ?? "https://business-api.tiktok.com/open_api/v1.3/event/track/";
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return Invalid("tiktok_events_endpoint_invalid");

        var canonicalId = CanonicalAdvertisingEventProjection.ResolveEventId(source);
        var value = CanonicalConversionValueProjection.Resolve(source.MetadataJson);
        object? properties = value is null ? null : new
        {
            currency = value.Currency,
            value = value.AmountMinorUnits / 100m,
            order_id = canonicalId
        };
        object? page = null;
        if (measurement.EventSourceType == "web")
        {
            var url = SourceUrl(source);
            if (url is null)
                return Invalid("tiktok_web_source_url_required");
            page = new { url };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(new
            {
                event_source = measurement.EventSourceType,
                event_source_id = measurement.EventSourceId,
                data = new[]
                {
                    new
                    {
                        @event = mapping.ProviderEventName,
                        event_time = new DateTimeOffset(
                            DateTime.SpecifyKind(source.EventUtc, DateTimeKind.Utc)).ToUnixTimeSeconds(),
                        event_id = canonicalId,
                        user = new { ttclid = click },
                        properties,
                        page
                    }
                }
            })
        };
        request.Headers.TryAddWithoutValidation("Access-Token", accessToken);

        return await SendProviderAsync(
            request,
            "tiktok",
            responseBody =>
            {
                if (string.IsNullOrWhiteSpace(responseBody))
                    return "tiktok_empty_response";
                try
                {
                    using var json = JsonDocument.Parse(responseBody);
                    if (!json.RootElement.TryGetProperty("code", out var code))
                        return "tiktok_response_code_missing";
                    var ok = code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var number)
                        ? number == 0
                        : code.ValueKind == JsonValueKind.String &&
                          string.Equals(code.GetString(), "0", StringComparison.Ordinal);
                    return ok ? null : "tiktok_provider_rejected";
                }
                catch (JsonException)
                {
                    return "tiktok_invalid_response";
                }
            },
            ct);
    }

    private async Task<ExternalAdsConversionApiResult> SendProviderAsync(
        HttpRequestMessage request,
        string provider,
        Func<string?, string?> providerError,
        CancellationToken ct)
    {
        try
        {
            using var response = await httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            var receipt = Clamp(body);
            if (response.IsSuccessStatusCode)
            {
                var error = providerError(body);
                if (error is null)
                    return new(true, false, (int)response.StatusCode, "sent",
                        ProviderReceiptJson: receipt);
                return new(false, false, (int)response.StatusCode, "permanent_failure",
                    error, error, receipt);
            }

            var retryable = response.StatusCode is HttpStatusCode.RequestTimeout or
                HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
            return new(false, retryable, (int)response.StatusCode,
                retryable ? "retryable_failure" : "permanent_failure",
                $"{provider}_http_{(int)response.StatusCode}",
                $"{provider} conversion delivery returned HTTP {(int)response.StatusCode}.",
                receipt);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, true, null, "retryable_failure",
                $"{provider}_timeout", $"{provider} conversion delivery timed out.");
        }
        catch (HttpRequestException ex)
        {
            return new(false, true, null, "retryable_failure",
                $"{provider}_network_error", Clamp(ex.Message));
        }
    }

    private string Required(string key) =>
        Clean(configuration[key]) ?? throw new InvalidOperationException($"{key} is required.");

    private static string? SourceUrl(AnalyticsEvent row)
    {
        var explicitUrl = row.Url ??
            CanonicalAdvertisingEventProjection.ReadString(row.MetadataJson, "sourceUrl");
        if (Uri.TryCreate(explicitUrl, UriKind.Absolute, out var uri) &&
            (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            return uri.ToString();

        if (string.IsNullOrWhiteSpace(row.Host)) return null;
        var host = row.Host.Trim();
        var scheme = host.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
            ? "http" : "https";
        var path = row.Path ??
            CanonicalAdvertisingEventProjection.ReadString(row.MetadataJson, "sourcePath") ?? "/";
        if (!path.StartsWith('/')) path = "/" + path;
        return $"{scheme}://{host}{path}";
    }

    private static string Digits(string? value) =>
        new((value ?? string.Empty).Where(char.IsDigit).ToArray());

    private static string? Clean(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? Clamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Length <= 4000 ? value : value[..4000];
    }

    private static ExternalAdsConversionApiResult Invalid(string code) =>
        new(false, false, null, "invalid_request", code, code);
}

/// <summary>
/// Projects only canonical, owner-resolved, human/consent-eligible outcomes into
/// configured external ad destinations. It never creates business outcomes.
/// </summary>
public sealed class ExternalAdsConversionDispatcherHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<ExternalAdsConversionDispatcherHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly string[] Providers =
        [MarketingDestinationKeys.Google, MarketingDestinationKeys.TikTok];
    private long _scanAfterId;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DispatchBatchAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "External ads conversion dispatcher failed"); }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    internal async Task DispatchBatchAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        var connections = scope.ServiceProvider.GetRequiredService<MarketingConnectionStore>();
        var transport = scope.ServiceProvider.GetRequiredService<IExternalAdsConversionApiService>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var now = DateTime.UtcNow;
        var cutoff = now.AddDays(-7);

        await db.MarketingDestinationDeliveries
            .Where(row => Providers.Contains(row.Provider) &&
                row.Channel == "server" &&
                row.CanonicalSource == nameof(AnalyticsEvent) &&
                row.Status != "sent" &&
                row.Status != "permanent_failure" &&
                row.AnalyticsEventId.HasValue &&
                db.AnalyticsEvents.Any(source =>
                    source.Id == row.AnalyticsEventId && source.EventUtc < cutoff))
            .ExecuteUpdateAsync(update => update
                .SetProperty(row => row.Status, "permanent_failure")
                .SetProperty(row => row.ErrorCode, "event_delivery_window_expired")
                .SetProperty(row => row.NextAttemptUtc, (DateTime?)null)
                .SetProperty(row => row.UpdatedUtc, now), ct);

        var candidates = await db.AnalyticsEvents.AsNoTracking()
            .Where(row => row.Id > _scanAfterId &&
                row.EventUtc >= cutoff &&
                row.MetadataJson != null &&
                (row.MetadataJson.Contains("measurementServerAuthorityEligible") ||
                 row.MetadataJson.Contains("metaServerAuthorityEligible")))
            .OrderBy(row => row.Id)
            .Take(100)
            .ToListAsync(ct);
        _scanAfterId = candidates.Count == 100 ? candidates[^1].Id : 0;

        foreach (var source in candidates)
        {
            if (!CanonicalAdvertisingEventProjection.CanProjectServer(source))
                continue;

            var owner = await CanonicalAdvertisingEventProjection.ResolveOwnerAsync(
                db, configuration, source, ct);
            if (owner is null)
            {
                foreach (var provider in Providers)
                    await CanonicalMarketingEligibility.RecordBlockAsync(
                        db, source, provider, "owner_unresolved", ct);
                continue;
            }

            var canonicalEvent = CanonicalAdvertisingEventProjection.ResolveEventName(source);
            if (string.IsNullOrWhiteSpace(canonicalEvent))
                continue;
            var canonicalId = CanonicalAdvertisingEventProjection.ResolveEventId(source);
            foreach (var provider in Providers)
            {
                var measurement =
                    await connections.GetProviderMeasurementConfigurationAsync(owner, provider, ct);
                if (!measurement.MappingReady)
                    continue;

                var mapping = measurement.Mappings.SingleOrDefault(row =>
                    string.Equals(row.CanonicalEventName, canonicalEvent,
                        StringComparison.OrdinalIgnoreCase));
                if (mapping is null)
                    continue;

                var clickReference = provider == MarketingDestinationKeys.Google
                    ? PaidAdsClickReference.NormalizeGoogle(
                        CanonicalAdvertisingEventProjection.ReadString(source.MetadataJson, "gclid"))
                    : PaidAdsClickReference.NormalizeTikTok(
                        CanonicalAdvertisingEventProjection.ReadString(source.MetadataJson, "ttclid"));
                if (clickReference is null)
                    continue;

                var connection = await connections.GetProviderConnectionAsync(owner, provider, ct);
                if (!connection.Ready || string.IsNullOrWhiteSpace(connection.AccountId))
                    continue;

                var destinationPin = provider == MarketingDestinationKeys.Google
                    ? mapping.DestinationId
                    : measurement.EventSourceId;
                if (string.IsNullOrWhiteSpace(destinationPin))
                    continue;

                var delivery = await db.MarketingDestinationDeliveries
                    .Where(row =>
                        row.OwnerKey == owner.Key &&
                        row.Provider == provider &&
                        row.Channel == "server" &&
                        row.CanonicalSource == nameof(AnalyticsEvent) &&
                        row.AnalyticsEventId == source.Id)
                    .OrderBy(row => row.CreatedUtc)
                    .FirstOrDefaultAsync(ct);

                if (delivery is null)
                {
                    delivery = new MarketingDestinationDelivery
                    {
                        OwnerKey = owner.Key,
                        OwnerType = owner.OwnerType,
                        AgentTrackingProfileId = owner.AgentTrackingProfileId,
                        CommerceBusinessId = owner.CommerceBusinessId,
                        Provider = provider,
                        Channel = "server",
                        CanonicalSource = nameof(AnalyticsEvent),
                        AnalyticsEventId = source.Id,
                        AdvertiserAccountId = connection.AccountId,
                        ConversionDataSourceId = destinationPin,
                        CanonicalEventId = canonicalId,
                        CanonicalEventName = canonicalEvent,
                        ProviderEventName = mapping.ProviderEventName,
                        PixelId = destinationPin,
                        Status = "pending",
                        CreatedUtc = now,
                        UpdatedUtc = now
                    };
                    db.MarketingDestinationDeliveries.Add(delivery);
                    try { await db.SaveChangesAsync(ct); }
                    catch (DbUpdateException)
                    {
                        db.Entry(delivery).State = EntityState.Detached;
                        continue;
                    }
                }

                if (delivery.Status is "sent" or "permanent_failure")
                    continue;
                if (delivery.NextAttemptUtc > now)
                    continue;

                if (!MatchesCurrentDestination(
                        delivery, connection, measurement, mapping, destinationPin))
                {
                    delivery.Status = "blocked_destination_changed";
                    delivery.ErrorCode = "destination_binding_changed";
                    delivery.UpdatedUtc = now;
                    await db.SaveChangesAsync(ct);
                    continue;
                }

                var humanEligibility =
                    await CanonicalMarketingEligibility.ResolveAsync(db, source, ct);
                if (!humanEligibility.Eligible)
                {
                    delivery.Status = humanEligibility.Reason.StartsWith(
                            "measurement_consent", StringComparison.Ordinal)
                        ? "blocked_consent"
                        : humanEligibility.Reason.StartsWith("production_", StringComparison.Ordinal)
                            ? "blocked_outcome_reconciliation"
                            : "blocked_human_evidence";
                    delivery.ErrorCode = humanEligibility.Reason;
                    delivery.UpdatedUtc = now;
                    await db.SaveChangesAsync(ct);
                    continue;
                }

                var claim = Guid.NewGuid().ToString("N");
                var claimed = await db.MarketingDestinationDeliveries
                    .Where(row =>
                        row.Id == delivery.Id &&
                        row.Status != "sent" &&
                        row.Status != "permanent_failure" &&
                        (row.ClaimExpiresUtc == null || row.ClaimExpiresUtc < now) &&
                        (row.NextAttemptUtc == null || row.NextAttemptUtc <= now))
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(row => row.ClaimToken, claim)
                        .SetProperty(row => row.ClaimExpiresUtc, now.AddMinutes(2))
                        .SetProperty(row => row.UpdatedUtc, now), ct);
                if (claimed != 1)
                    continue;

                delivery = await db.MarketingDestinationDeliveries
                    .SingleAsync(row => row.Id == delivery.Id && row.ClaimToken == claim, ct);
                await db.Entry(delivery).ReloadAsync(ct);

                ExternalAdsConversionApiResult result;
                try
                {
                    result = await transport.SendAsync(
                        owner, connection, measurement, mapping, source, clickReference, ct);
                }
                catch (Exception ex) when (
                    ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex,
                        "{Provider} conversion delivery failed for canonical analytics event {AnalyticsEventId}",
                        provider, source.Id);
                    result = new(false, true, null, "retryable_failure",
                        "provider_exception", "Provider delivery failed.");
                }

                delivery.AttemptCount++;
                delivery.LastAttemptUtc = DateTime.UtcNow;
                delivery.LastHttpStatusCode = result.HttpStatusCode;
                delivery.ProviderReceiptJson = result.ProviderReceiptJson;
                delivery.ErrorCode = result.ErrorCode;
                delivery.ErrorMessage = result.ErrorMessage;
                delivery.ClaimToken = null;
                delivery.ClaimExpiresUtc = null;
                delivery.UpdatedUtc = DateTime.UtcNow;

                if (result.Sent)
                {
                    delivery.Status = "sent";
                    delivery.SentUtc = DateTime.UtcNow;
                    delivery.NextAttemptUtc = null;
                }
                else if (result.Retryable && source.EventUtc > DateTime.UtcNow.AddDays(-7))
                {
                    delivery.Status = "retryable";
                    delivery.NextAttemptUtc = DateTime.UtcNow.Add(
                        RetryDelay(delivery.AttemptCount));
                }
                else
                {
                    delivery.Status = "permanent_failure";
                    delivery.NextAttemptUtc = null;
                }

                await db.SaveChangesAsync(ct);
            }
        }
    }

    internal static bool MatchesCurrentDestination(
        MarketingDestinationDelivery receipt,
        MarketingProviderConnectionSnapshot connection,
        MarketingProviderMeasurementConfiguration measurement,
        MarketingProviderEventMapping mapping,
        string destinationPin) =>
        connection.Connected &&
        measurement.MappingReady &&
        receipt.OwnerKey == connection.Owner.Key &&
        receipt.Provider == connection.Provider &&
        string.Equals(receipt.AdvertiserAccountId, connection.AccountId,
            StringComparison.Ordinal) &&
        string.Equals(receipt.ConversionDataSourceId, destinationPin,
            StringComparison.Ordinal) &&
        string.Equals(receipt.PixelId, destinationPin, StringComparison.Ordinal) &&
        string.Equals(receipt.ProviderEventName, mapping.ProviderEventName,
            StringComparison.Ordinal) &&
        string.Equals(receipt.CanonicalEventName, mapping.CanonicalEventName,
            StringComparison.OrdinalIgnoreCase);

    private static TimeSpan RetryDelay(int attempts) => attempts switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        3 => TimeSpan.FromMinutes(15),
        4 => TimeSpan.FromHours(1),
        _ => TimeSpan.FromHours(6)
    };
}
