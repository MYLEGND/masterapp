using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Shared.Analytics;

namespace Infrastructure.Analytics;

public interface IOpenAiConversionsApiService
{
    Task<OpenAiConversionsApiResult> SendAsync(
        string pixelId,
        string conversionsApiKey,
        OpenAiConversionEvent conversion,
        bool validateOnly = false,
        CancellationToken cancellationToken = default);
}

public sealed class OpenAiConversionsApiService(HttpClient httpClient) : IOpenAiConversionsApiService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<OpenAiConversionsApiResult> SendAsync(
        string pixelId,
        string conversionsApiKey,
        OpenAiConversionEvent conversion,
        bool validateOnly = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pixelId))
            return Invalid("pixel_id_required");
        if (string.IsNullOrWhiteSpace(conversionsApiKey))
            return Invalid("conversions_api_key_required");
        if (conversion is null || string.IsNullOrWhiteSpace(conversion.Id) || string.IsNullOrWhiteSpace(conversion.Type))
            return Invalid("conversion_event_invalid");

        var requestBody = new OpenAiConversionsRequest
        {
            ValidateOnly = validateOnly,
            IntegrationSource = "legend_growth_os",
            Events =
            [
                new()
                {
                    Id = conversion.Id,
                    Type = conversion.Type,
                    TimestampMs = conversion.TimestampMs,
                    Oppref = conversion.Oppref,
                    SourceUrl = conversion.SourceUrl,
                    ActionSource = conversion.ActionSource,
                    Data = new()
                    {
                        Type = conversion.Data.Type,
                        Amount = conversion.Data.Amount,
                        Currency = conversion.Data.Currency,
                        Contents = conversion.Data.Contents?.Select(x => new OpenAiContentPayload
                        {
                            Id = x.Id,
                            Name = x.Name,
                            ContentType = x.ContentType,
                            Quantity = x.Quantity,
                            Amount = x.Amount,
                            Currency = x.Currency
                        }).ToArray()
                    }
                }
            ]
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://bzr.openai.com/v1/events?pid={Uri.EscapeDataString(pixelId.Trim())}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", conversionsApiKey.Trim());
        request.Content = new StringContent(JsonSerializer.Serialize(requestBody, JsonOptions), Encoding.UTF8, "application/json");

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var body = await ReadBodyAsync(response, cancellationToken);
            if (response.IsSuccessStatusCode)
                return new(true, true, false, (int)response.StatusCode, "sent", ProviderReceiptJson: body);

            var retryable = response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
                            (int)response.StatusCode >= 500;
            return new(
                true,
                false,
                retryable,
                (int)response.StatusCode,
                retryable ? "retryable_failure" : "permanent_failure",
                ErrorCode: "openai_http_" + (int)response.StatusCode,
                ErrorMessage: body,
                ProviderReceiptJson: body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(true, false, true, null, "retryable_failure", "openai_timeout", "OpenAI Conversions API request timed out.");
        }
        catch (HttpRequestException ex)
        {
            return new(true, false, true, null, "retryable_failure", "openai_network_error", Clamp(ex.Message));
        }
    }

    private static OpenAiConversionsApiResult Invalid(string code) =>
        new(false, false, false, null, "invalid_request", code, code);

    private static async Task<string?> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var value = await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(value) ? null : Clamp(value);
    }

    private static string Clamp(string value) => value.Length <= 4000 ? value : value[..4000];

    private sealed class OpenAiConversionsRequest
    {
        [JsonPropertyName("validate_only")] public bool ValidateOnly { get; set; }
        [JsonPropertyName("integration_source")] public string IntegrationSource { get; set; } = string.Empty;
        [JsonPropertyName("events")] public OpenAiEventPayload[] Events { get; set; } = [];
    }

    private sealed class OpenAiEventPayload
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
        [JsonPropertyName("timestamp_ms")] public long TimestampMs { get; set; }
        [JsonPropertyName("oppref")] public string? Oppref { get; set; }
        [JsonPropertyName("source_url")] public string SourceUrl { get; set; } = string.Empty;
        [JsonPropertyName("action_source")] public string ActionSource { get; set; } = "web";
        [JsonPropertyName("data")] public OpenAiDataPayload Data { get; set; } = new();
    }

    private sealed class OpenAiDataPayload
    {
        [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
        [JsonPropertyName("amount")] public long? Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("contents")] public OpenAiContentPayload[]? Contents { get; set; }
    }

    private sealed class OpenAiContentPayload
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("content_type")] public string? ContentType { get; set; }
        [JsonPropertyName("quantity")] public int? Quantity { get; set; }
        [JsonPropertyName("amount")] public long? Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
    }
}

public static class OpenAiMeasurementEventMapper
{
    public static readonly IReadOnlyCollection<string> SupportedCanonicalServerEvents =
        MarketingConversionDestinationCatalog.Definitions
            .Select(definition => definition.CanonicalEventName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static bool TryMap(AnalyticsEvent row, out OpenAiConversionEvent conversion)
    {
        conversion = null!;
        if (row is null || row.EventId == Guid.Empty) return false;

        var destination = MarketingConversionDestinationCatalog.ResolveOpenAi(CanonicalAdvertisingEventProjection.ResolveEventName(row));
        if (destination is null) return false;

        var providerEvent = destination.EventName;

        var sourceUrl = SourceUrl(row);
        if (sourceUrl is null) return false;

        var contentEvent = string.Equals(destination.PayloadType, "contents", StringComparison.OrdinalIgnoreCase);

        long? amount = null;
        var currency = ReadString(row.MetadataJson, "currency");
        if (contentEvent && TryReadLong(row.MetadataJson, "valueCents", out var cents) && cents >= 0)
        {
            amount = cents;
            currency = string.IsNullOrWhiteSpace(currency) ? "USD" : currency.Trim().ToUpperInvariant();
        }

        IReadOnlyList<OpenAiConversionContent>? contents = null;
        if (contentEvent)
        {
            var productId = ReadString(row.MetadataJson, "productId");
            var productName = ReadString(row.MetadataJson, "productName");
            var quantity = TryReadInt(row.MetadataJson, "quantity", out var q) && q > 0 ? q : (int?)null;
            if (!string.IsNullOrWhiteSpace(productId) || !string.IsNullOrWhiteSpace(productName))
            {
                contents =
                [
                    new(
                        Id: productId,
                        Name: productName,
                        ContentType: "product",
                        Quantity: quantity,
                        Amount: amount,
                        Currency: amount.HasValue ? currency : null)
                ];
            }
        }

        if (contentEvent && ReadString(row.MetadataJson, "items") is { } itemJson)
        {
            try
            {
                using var items = JsonDocument.Parse(itemJson);
                if (items.RootElement.ValueKind == JsonValueKind.Array)
                    contents = items.RootElement.EnumerateArray().Select(item =>
                    {
                        var json = item.GetRawText();
                        var itemAmount = CanonicalAdvertisingEventProjection.ReadInt64(json, "valueCents");
                        var itemQuantity = CanonicalAdvertisingEventProjection.ReadInt64(json, "quantity");
                        return new OpenAiConversionContent(ReadString(json, "productId"), ReadString(json, "productName"), "product",
                            itemQuantity is > 0 and <= int.MaxValue ? (int)itemQuantity.Value : null,
                            itemAmount is >= 0 ? itemAmount : null, currency);
                    }).ToArray();
            }
            catch (JsonException) { }
        }

        conversion = new(
            Id: CanonicalAdvertisingEventProjection.ResolveEventId(row),
            Type: providerEvent,
            TimestampMs: new DateTimeOffset(DateTime.SpecifyKind(row.EventUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
            SourceUrl: sourceUrl,
            ActionSource: "web",
            Data: new(destination.PayloadType ?? "customer_action", amount, amount.HasValue ? currency : null, contents),
            Oppref: row.Oppref ?? ReadString(row.MetadataJson, "oppref"));
        return true;
    }

    private static string? SourceUrl(AnalyticsEvent row)
    {
        var explicitUrl = row.Url ?? ReadString(row.MetadataJson, "sourceUrl");
        if (Uri.TryCreate(explicitUrl, UriKind.Absolute, out var explicitUri) &&
            (explicitUri.Scheme == Uri.UriSchemeHttps || explicitUri.Scheme == Uri.UriSchemeHttp))
            return explicitUri.ToString();

        if (string.IsNullOrWhiteSpace(row.Host)) return null;
        var host = row.Host.Trim();
        var scheme = host.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ? "http" : "https";
        var path = row.Path ?? ReadString(row.MetadataJson, "sourcePath");
        if (string.IsNullOrWhiteSpace(path)) path = "/";
        if (!path.StartsWith('/')) path = "/" + path;
        return $"{scheme}://{host}{path}";
    }

    internal static string? ReadString(string? json, string property) => CanonicalAdvertisingEventProjection.ReadString(json, property);
    private static bool TryReadLong(string? json, string property, out long value)
    {
        var number = CanonicalAdvertisingEventProjection.ReadInt64(json, property);
        value = number ?? 0;
        return number.HasValue;
    }
    private static bool TryReadInt(string? json, string property, out int value)
    {
        var number = CanonicalAdvertisingEventProjection.ReadInt64(json, property);
        value = number is >= int.MinValue and <= int.MaxValue ? (int)number.Value : 0;
        return number is >= int.MinValue and <= int.MaxValue;
    }

}

public sealed class OpenAiConversionDispatcherHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<OpenAiConversionDispatcherHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private long _scanAfterId;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DispatchBatchAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "OpenAI conversion dispatcher failed"); }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    internal async Task DispatchBatchAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        var connections = scope.ServiceProvider.GetRequiredService<IOpenAiAdsAccountConnectionAuthority>();
        var capi = scope.ServiceProvider.GetRequiredService<IOpenAiConversionsApiService>();
        var now = DateTime.UtcNow;
        var cutoff = now.AddDays(-7);
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var candidates = await db.AnalyticsEvents.AsNoTracking()
            .Where(row => row.Id > _scanAfterId && row.EventUtc >= cutoff && row.MetadataJson != null &&
                (row.MetadataJson.Contains("measurementServerAuthorityEligible") || row.MetadataJson.Contains("metaServerAuthorityEligible")))
            .OrderBy(row => row.Id).Take(100).ToListAsync(ct);
        _scanAfterId = candidates.Count == 100 ? candidates[^1].Id : 0;

        foreach (var source in candidates)
        {
            if (!CanonicalAdvertisingEventProjection.CanProjectServer(source)) continue;
            var owner = await CanonicalAdvertisingEventProjection.ResolveOwnerAsync(db, configuration, source, ct);
            if (owner is null) continue;
            if (!OpenAiMeasurementEventMapper.TryMap(source, out var conversion)) continue;

            var connection = await connections.GetAsync(owner, ct);
            if (connection.Owner != owner || !connection.Connected || string.IsNullOrWhiteSpace(connection.AccountId) ||
                string.IsNullOrWhiteSpace(connection.PixelId) || string.IsNullOrWhiteSpace(connection.ConversionDataSourceId) || !connection.HasConversionsApiCredential)
                continue;

            var delivery = await db.Set<MarketingDestinationDelivery>().SingleOrDefaultAsync(row =>
                row.OwnerKey == owner.Key &&
                row.Provider == MarketingDestinationKeys.OpenAi &&
                row.Channel == "server" && row.CanonicalSource == nameof(AnalyticsEvent) &&
                (row.AnalyticsEventId == source.Id || row.CanonicalEventId == conversion.Id) &&
                row.ProviderEventName == conversion.Type, ct);

            // Historical delivery identities are read-only resend fences. They
            // never become active AnalyticsEvent receipts or a second dispatcher.
            if (delivery is null && await FindHistoricalReceiptAsync(db, owner, source, conversion.Type, ct) is not null)
                continue;
            if (delivery is not null) conversion = conversion with { Id = delivery.CanonicalEventId };

            if (delivery?.Status == "sent") continue;
            if (delivery?.Status == "permanent_failure") continue;
            if (delivery?.NextAttemptUtc > now) continue;

            if (delivery is null)
            {
                delivery = new()
                {
                    OwnerKey = owner.Key,
                    OwnerType = owner.OwnerType,
                    AgentTrackingProfileId = owner.AgentTrackingProfileId,
                    CommerceBusinessId = owner.CommerceBusinessId,
                    Provider = MarketingDestinationKeys.OpenAi,
                    Channel = "server",
                    CanonicalSource = nameof(AnalyticsEvent),
                    AnalyticsEventId = source.Id,
                    AdvertiserAccountId = connection.AccountId,
                    ConversionDataSourceId = connection.ConversionDataSourceId,
                    CanonicalEventId = conversion.Id,
                    CanonicalEventName = source.EventType,
                    ProviderEventName = conversion.Type,
                    PixelId = connection.PixelId!,
                    Status = "pending",
                    CreatedUtc = now,
                    UpdatedUtc = now
                };
                db.Add(delivery);
                try { await db.SaveChangesAsync(ct); }
                catch (DbUpdateException)
                {
                    db.Entry(delivery).State = EntityState.Detached;
                    continue;
                }
            }

            if (string.IsNullOrWhiteSpace(delivery.AdvertiserAccountId) || string.IsNullOrWhiteSpace(delivery.ConversionDataSourceId))
            {
                // Legacy receipts did not pin these identifiers. Preserve them until an explicit
                // verification/migration establishes the original destination; never guess from today's connection.
                delivery.Status = "blocked_requires_destination_verification";
                delivery.ErrorCode = "historical_destination_binding_unverified";
                await db.SaveChangesAsync(ct);
                continue;
            }

            // Never move a queued conversion to a newly connected account or data source.
            if (!MatchesCurrentDestination(delivery, connection))
            {
                delivery.Status = "blocked_destination_changed";
                delivery.ErrorCode = "destination_binding_changed";
                await db.SaveChangesAsync(ct);
                continue;
            }

            var claim = Guid.NewGuid().ToString("N");
            var claimed = await db.Set<MarketingDestinationDelivery>()
                .Where(row => row.Id == delivery.Id &&
                              row.Status != "sent" &&
                              row.Status != "permanent_failure" &&
                              (row.ClaimExpiresUtc == null || row.ClaimExpiresUtc < now) &&
                              (row.NextAttemptUtc == null || row.NextAttemptUtc <= now))
                .ExecuteUpdateAsync(update => update
                    .SetProperty(row => row.ClaimToken, claim)
                    .SetProperty(row => row.ClaimExpiresUtc, now.AddMinutes(2))
                    .SetProperty(row => row.UpdatedUtc, now), ct);
            if (claimed != 1) continue;

            delivery = await db.Set<MarketingDestinationDelivery>()
                .SingleAsync(row => row.Id == delivery.Id && row.ClaimToken == claim, ct);
            await db.Entry(delivery).ReloadAsync(ct);

            var secrets = await connections.GetSecretsAsync(owner, ct);
            if (string.IsNullOrWhiteSpace(secrets.ConversionsApiKey))
            {
                delivery.Status = "blocked_not_configured";
                delivery.ErrorCode = "conversions_api_key_required";
                delivery.ClaimToken = null;
                delivery.ClaimExpiresUtc = null;
                delivery.UpdatedUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                continue;
            }

            OpenAiConversionsApiResult result;
            try { result = await capi.SendAsync(connection.PixelId!, secrets.ConversionsApiKey!, conversion, false, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "OpenAI delivery failed for canonical analytics event {AnalyticsEventId}", source.Id);
                result = new(true, false, true, null, "retryable_failure", "provider_exception", "Provider delivery failed.");
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
                delivery.NextAttemptUtc = DateTime.UtcNow.Add(RetryDelay(delivery.AttemptCount));
            }
            else
            {
                delivery.Status = "permanent_failure";
                delivery.NextAttemptUtc = null;
            }

            await db.SaveChangesAsync(ct);
        }
    }

    internal static bool MatchesCurrentDestination(MarketingDestinationDelivery receipt, OpenAiAdsConnectionSnapshot connection) =>
        connection.Connected && receipt.OwnerKey == connection.Owner.Key && receipt.Provider == MarketingDestinationKeys.OpenAi &&
        !string.IsNullOrWhiteSpace(connection.PixelId) && !string.IsNullOrWhiteSpace(connection.AccountId) &&
        !string.IsNullOrWhiteSpace(connection.ConversionDataSourceId) &&
        receipt.PixelId == connection.PixelId && receipt.AdvertiserAccountId == connection.AccountId &&
        receipt.ConversionDataSourceId == connection.ConversionDataSourceId;

    internal static async Task<MarketingDestinationDelivery?> FindHistoricalReceiptAsync(MasterAppDbContext db,
        MarketingOwnerScope owner, AnalyticsEvent source, string providerEventName, CancellationToken ct)
    {
        var receipts = await db.MarketingDestinationDeliveries.AsNoTracking().Where(x => x.OwnerKey == owner.Key &&
            x.Provider == MarketingDestinationKeys.OpenAi && x.Channel == "server" && x.CanonicalSource == nameof(MetaSignalEvent) &&
            x.ProviderEventName == providerEventName).ToListAsync(ct);
        var linked = receipts.FirstOrDefault(x => x.AnalyticsEventId == source.Id);
        if (linked is not null) return linked;
        var ids = receipts.Where(x => x.AnalyticsEventId == null).Select(x => x.CanonicalEventId).ToArray();
        if (ids.Length == 0) return null;
        var historical = await db.MetaSignalEvents.AsNoTracking().Where(x => ids.Contains(x.EventId) &&
            x.CommerceBusinessId == source.CommerceBusinessId && x.AgentTrackingProfileId == source.AgentTrackingProfileId).ToListAsync(ct);
        var match = historical.FirstOrDefault(x => CanonicalAdvertisingEventProjection.ReadInt64(x.MetadataJson, "sourceAnalyticsEventId") == source.Id);
        return match is null ? null : receipts.First(x => x.CanonicalEventId == match.EventId);
    }

    private static TimeSpan RetryDelay(int attempts) => attempts switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        3 => TimeSpan.FromMinutes(15),
        4 => TimeSpan.FromHours(1),
        _ => TimeSpan.FromHours(6)
    };
}

public interface IOpenAiMeasurementHealthService
{
    Task<OpenAiMeasurementHealthSnapshot> GetAsync(MarketingOwnerScope owner, CancellationToken cancellationToken = default);
}

public sealed class OpenAiMeasurementHealthService(
    MasterAppDbContext db,
    IOpenAiAdsAccountConnectionAuthority connections,
    HttpClient httpClient) : IOpenAiMeasurementHealthService
{
    public async Task<OpenAiMeasurementHealthSnapshot> GetAsync(MarketingOwnerScope owner, CancellationToken cancellationToken = default)
    {
        var connection = await connections.GetAsync(owner, cancellationToken);
        var from = DateTime.UtcNow.AddDays(-30);
        var receipts = await db.Set<MarketingDestinationDelivery>().AsNoTracking()
            .Where(row => row.OwnerKey == owner.Key && row.Provider == MarketingDestinationKeys.OpenAi && row.CreatedUtc >= from)
            .ToListAsync(cancellationToken);
        var rows = receipts.Where(row => OpenAiConversionDispatcherHostedService.MatchesCurrentDestination(row, connection)).ToArray();

        var providerAvailable = false;
        var recentProviderEvents = 0;
        if (connection.Connected && !string.IsNullOrWhiteSpace(connection.PixelId))
        {
            var secrets = await connections.GetSecretsAsync(owner, cancellationToken);
            if (!string.IsNullOrWhiteSpace(secrets.ManagementApiKey))
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"https://api.ads.openai.com/v1/conversions/events?pid={Uri.EscapeDataString(connection.PixelId)}&limit=50");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secrets.ManagementApiKey);
                try
                {
                    using var response = await httpClient.SendAsync(request, cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        providerAvailable = true;
                        var json = await response.Content.ReadAsStringAsync(cancellationToken);
                        using var document = JsonDocument.Parse(json);
                        if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                            recentProviderEvents = data.GetArrayLength();
                    }
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                catch (JsonException) { }
            }
        }

        var status = !connection.Connected ? "not_connected"
            : !connection.PixelConfigured ? "pixel_not_configured"
            : string.IsNullOrWhiteSpace(connection.ConversionDataSourceId) ? "data_source_not_configured"
            : !connection.ConversionsApiConfigured ? "conversions_api_not_configured"
            : rows.Any(x => x.Status.StartsWith("blocked_", StringComparison.Ordinal)) ? "delivery_blocked"
            : rows.Any(x => x.Status == "permanent_failure") ? "delivery_failures"
            : rows.Any(x => x.Status == "retryable") ? "retrying"
            : rows.Any(x => x.Status == "sent" && x.LastHttpStatusCode >= 200 && x.LastHttpStatusCode < 300) ? "provider_accepted"
            : "configured_no_delivery_evidence";

        return new(
            owner,
            connection.Connected,
            connection.PixelConfigured,
            connection.ConversionsApiConfigured,
            connection.PixelId,
            rows.Count(x => x.Status == "pending" || x.Status.StartsWith("blocked_", StringComparison.Ordinal)),
            rows.Count(x => x.Status == "retryable"),
            rows.Count(x => x.Status == "permanent_failure"),
            rows.Count(x => x.Status == "sent"),
            rows.Where(x => x.SentUtc.HasValue).Max(x => x.SentUtc),
            providerAvailable,
            recentProviderEvents,
            status);
    }
}
