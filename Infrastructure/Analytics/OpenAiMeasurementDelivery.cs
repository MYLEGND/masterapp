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

    public static bool TryMap(MetaSignalEvent row, out OpenAiConversionEvent conversion)
    {
        conversion = null!;
        if (row is null || string.IsNullOrWhiteSpace(row.EventId)) return false;

        var destination = MarketingConversionDestinationCatalog.ResolveOpenAi(row.EventName);
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

        conversion = new(
            Id: row.EventId.Trim(),
            Type: providerEvent,
            TimestampMs: new DateTimeOffset(DateTime.SpecifyKind(row.CreatedUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
            SourceUrl: sourceUrl,
            ActionSource: "web",
            Data: new(destination.PayloadType ?? "customer_action", amount, amount.HasValue ? currency : null, contents),
            Oppref: ReadString(row.MetadataJson, "oppref"));
        return true;
    }

    public static MarketingOwnerScope? ResolveOwner(MetaSignalEvent row)
    {
        if (row.CommerceBusinessId is Guid businessId && businessId != Guid.Empty)
            return MarketingOwnerScope.Business(businessId);
        if (row.AgentTrackingProfileId is Guid agentId && agentId != Guid.Empty)
            return MarketingOwnerScope.Agent(agentId);

        var siteKey = ReadString(row.MetadataJson, "siteKey");
        if (string.Equals(siteKey, WebsiteEditorSiteKeys.Legend, StringComparison.OrdinalIgnoreCase))
            return MarketingOwnerScope.Founder;

        return null;
    }

    private static string? SourceUrl(MetaSignalEvent row)
    {
        var explicitUrl = ReadString(row.MetadataJson, "sourceUrl");
        if (Uri.TryCreate(explicitUrl, UriKind.Absolute, out var explicitUri) &&
            (explicitUri.Scheme == Uri.UriSchemeHttps || explicitUri.Scheme == Uri.UriSchemeHttp))
            return explicitUri.ToString();

        if (string.IsNullOrWhiteSpace(row.Host)) return null;
        var host = row.Host.Trim();
        var scheme = host.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ? "http" : "https";
        var path = ReadString(row.MetadataJson, "sourcePath");
        if (string.IsNullOrWhiteSpace(path)) path = "/";
        if (!path.StartsWith('/')) path = "/" + path;
        return $"{scheme}://{host}{path}";
    }

    internal static string? ReadString(string? json, string property)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(property, out var value)) return null;
            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        }
        catch (JsonException) { return null; }
    }

    private static bool TryReadLong(string? json, string property, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(property, out var element) &&
                   ((element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value)) ||
                    (element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)));
        }
        catch (JsonException) { return false; }
    }

    private static bool TryReadInt(string? json, string property, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(property, out var element) &&
                   ((element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value)) ||
                    (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)));
        }
        catch (JsonException) { return false; }
    }
}

public sealed class OpenAiConversionDispatcherHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<OpenAiConversionDispatcherHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

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
        var supported = OpenAiMeasurementEventMapper.SupportedCanonicalServerEvents.ToArray();

        var candidates = await db.MetaSignalEvents.AsNoTracking()
            .Where(row => row.CreatedUtc >= cutoff &&
                          supported.Contains(row.EventName) &&
                          row.MetadataJson != null &&
                          row.MetadataJson.Contains(MetaSignalSingleTruthPolicy.DispatchEligibleMarker))
            .OrderBy(row => row.CreatedUtc)
            .Take(100)
            .ToListAsync(ct);

        foreach (var source in candidates)
        {
            if (!MetaSignalSingleTruthPolicy.CanDispatchServerAuthority(source.EventName, source.MetadataJson))
                continue;
            var owner = OpenAiMeasurementEventMapper.ResolveOwner(source);
            if (owner is null) continue;
            if (!OpenAiMeasurementEventMapper.TryMap(source, out var conversion)) continue;

            var connection = await connections.GetAsync(owner, ct);
            if (!connection.Connected || string.IsNullOrWhiteSpace(connection.PixelId) || !connection.HasConversionsApiCredential)
                continue;

            var delivery = await db.Set<MarketingDestinationDelivery>().SingleOrDefaultAsync(row =>
                row.OwnerKey == owner.Key &&
                row.Provider == MarketingDestinationKeys.OpenAi &&
                row.Channel == "server" &&
                row.CanonicalEventId == conversion.Id &&
                row.ProviderEventName == conversion.Type, ct);

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
                    CanonicalSource = nameof(MetaSignalEvent),
                    CanonicalEventId = conversion.Id,
                    CanonicalEventName = source.EventName,
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

            var result = await capi.SendAsync(connection.PixelId!, secrets.ConversionsApiKey!, conversion, false, ct);
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
            else if (result.Retryable && source.CreatedUtc > DateTime.UtcNow.AddDays(-7))
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
        var rows = await db.Set<MarketingDestinationDelivery>().AsNoTracking()
            .Where(row => row.OwnerKey == owner.Key && row.Provider == MarketingDestinationKeys.OpenAi)
            .ToListAsync(cancellationToken);

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
                catch (JsonException) { }
            }
        }

        var status = !connection.Connected ? "not_connected"
            : !connection.PixelConfigured ? "pixel_not_configured"
            : !connection.ConversionsApiConfigured ? "conversions_api_not_configured"
            : rows.Any(x => x.Status == "permanent_failure") ? "delivery_failures"
            : rows.Any(x => x.Status == "retryable") ? "retrying"
            : "ready";

        return new(
            owner,
            connection.Connected,
            connection.PixelConfigured,
            connection.ConversionsApiConfigured,
            connection.PixelId,
            rows.Count(x => x.Status is "pending" or "blocked_not_configured"),
            rows.Count(x => x.Status == "retryable"),
            rows.Count(x => x.Status == "permanent_failure"),
            rows.Count(x => x.Status == "sent"),
            rows.Where(x => x.SentUtc.HasValue).Max(x => x.SentUtc),
            providerAvailable,
            recentProviderEvents,
            status);
    }
}
