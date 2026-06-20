using System.Globalization;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProtectWebsite.Services.Meta;
using Shared.Analytics;

namespace ProtectWebsite.Services.MetaSignal;

public sealed class MetaSignalOutcomeDispatcherHostedService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly HashSet<string> DispatchableEvents =
        new(MetaSignalEventCatalog.ServerForwardEventNames, StringComparer.OrdinalIgnoreCase);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<MetaSignalIntelligenceOptions> _options;
    private readonly ILogger<MetaSignalOutcomeDispatcherHostedService> _logger;

    public MetaSignalOutcomeDispatcherHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<MetaSignalIntelligenceOptions> options,
        ILogger<MetaSignalOutcomeDispatcherHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchBatchAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MetaSignal outcome dispatcher failed");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task DispatchBatchAsync(CancellationToken cancellationToken)
    {
        if (!_options.Value.Enabled || !_options.Value.SendServerEvents)
            return;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        var capi = scope.ServiceProvider.GetRequiredService<IMetaConversionsApiService>();
        var metaPixelResolutionService = scope.ServiceProvider.GetRequiredService<IMetaPixelResolutionService>();

        var rows = await db.MetaSignalEvents
            .Where(x =>
                !x.MetaServerSent &&
                x.MetadataJson != null &&
                !x.MetadataJson.Contains("\"metaServerStatus\":") &&
                (x.TrafficType == "crm" ||
                 x.MetadataJson.Contains(MetaSignalAnalyticsBridgeMetadata.BridgeSourceMarker)) &&
                DispatchableEvents.Contains(x.EventName) &&
                x.MetadataJson.Contains(MetaSignalSingleTruthPolicy.DispatchEligibleMarker))
            .OrderBy(x => x.CreatedUtc)
            .Take(25)
            .ToListAsync(cancellationToken);

        var leadIds = rows
            .Where(x => x.LeadId.HasValue)
            .Select(x => x.LeadId!.Value)
            .Distinct()
            .ToList();

        var workstationLeadIds = leadIds
            .Select(x => x.ToString("N"))
            .ToArray();

        var intakeLinksByWorkstationLeadId = await db.WebsiteLeadIntakeLinks
            .AsNoTracking()
            .Where(x => workstationLeadIds.Contains(x.WorkstationLeadId))
            .ToDictionaryAsync(x => x.WorkstationLeadId, cancellationToken);

        var websiteLeadIds = intakeLinksByWorkstationLeadId.Values
            .Select(x => x.WebsiteLeadPublicId)
            .Distinct()
            .ToArray();

        var leadsById = await db.WebsiteLeads
            .AsNoTracking()
            .Where(x => websiteLeadIds.Contains(x.LeadId))
            .ToDictionaryAsync(x => x.LeadId, cancellationToken);

        foreach (var row in rows)
        {
            if (!MetaSignalEventCatalog.TryGet(row.EventName, out var definition))
            {
                row.MetadataJson = MergeDispatchMetadata(row.MetadataJson, new MetaConversionsApiResult
                {
                    Attempted = false,
                    Sent = false,
                    Status = "skipped_catalog_missing",
                    Note = "catalog_missing"
                });
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }

            if (!definition.AllowServerForward)
            {
                row.MetadataJson = MergeDispatchMetadata(row.MetadataJson, new MetaConversionsApiResult
                {
                    Attempted = false,
                    Sent = false,
                    Status = "skipped_server_forward_not_allowed",
                    Note = "server_forward_not_allowed"
                });
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }

            if (!MetaSignalEventCatalog.IsServerAuthorityEvent(row.EventName))
            {
                row.MetadataJson = MergeDispatchMetadata(row.MetadataJson, new MetaConversionsApiResult
                {
                    Attempted = false,
                    Sent = false,
                    Status = "skipped_not_server_authority_event",
                    Note = "not_server_authority_event"
                });
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }

            if (!MetaSignalSingleTruthPolicy.CanDispatchServerAuthority(row.EventName, row.MetadataJson))
            {
                row.MetadataJson = MergeDispatchMetadata(row.MetadataJson, new MetaConversionsApiResult
                {
                    Attempted = false,
                    Sent = false,
                    Status = "skipped_not_dispatch_eligible",
                    Note = "not_dispatch_eligible"
                });
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }

            var isBridgeOwned = MetaSignalAnalyticsBridgeMetadata.IsBridgeOwned(row.MetadataJson);
            var bridgeClientIp = isBridgeOwned
                ? FirstNonBlank(
                    MetaSignalAnalyticsBridgeMetadata.ReadString(row.MetadataJson, "sourceClientIpAddress"))
                : null;
            var bridgeUserAgent = isBridgeOwned
                ? FirstNonBlank(
                    MetaSignalAnalyticsBridgeMetadata.ReadString(row.MetadataJson, "sourceClientUserAgent"),
                    row.UserAgent)
                : null;
            var bridgeFbclid = isBridgeOwned
                ? FirstNonBlank(MetaSignalAnalyticsBridgeMetadata.ReadString(row.MetadataJson, "sourceFbclid"))
                : null;
            var bridgeFbp = isBridgeOwned
                ? FirstNonBlank(MetaSignalAnalyticsBridgeMetadata.ReadString(row.MetadataJson, "sourceFbp"))
                : null;
            var bridgeFbc = isBridgeOwned
                ? FirstNonBlank(MetaSignalAnalyticsBridgeMetadata.ReadString(row.MetadataJson, "sourceFbc"))
                : null;

            WebsiteLead? websiteLead = null;
            if (row.LeadId.HasValue)
            {
                leadsById.TryGetValue(row.LeadId.Value, out websiteLead);

                if (websiteLead == null &&
                    intakeLinksByWorkstationLeadId.TryGetValue(row.LeadId.Value.ToString("N"), out var intakeLink))
                {
                    leadsById.TryGetValue(intakeLink.WebsiteLeadPublicId, out websiteLead);
                }
            }

            var crmContact = await ResolveCrmContactAsync(db, row, cancellationToken);

            var email = FirstNonBlank(websiteLead?.Email, crmContact.Email);
            var phone = FirstNonBlank(websiteLead?.Phone, crmContact.Phone);
            var firstName = FirstNonBlank(websiteLead?.FirstName, crmContact.FirstName);
            var lastName = FirstNonBlank(websiteLead?.LastName, crmContact.LastName);

            var hasContactData =
                !string.IsNullOrWhiteSpace(email) ||
                !string.IsNullOrWhiteSpace(phone) ||
                !string.IsNullOrWhiteSpace(firstName) ||
                !string.IsNullOrWhiteSpace(lastName) ||
                crmContact.DateOfBirth.HasValue ||
                !string.IsNullOrWhiteSpace(crmContact.Gender) ||
                !string.IsNullOrWhiteSpace(crmContact.City) ||
                !string.IsNullOrWhiteSpace(crmContact.State) ||
                !string.IsNullOrWhiteSpace(crmContact.ZipCode);

            var hasBridgeAttribution =
                !string.IsNullOrWhiteSpace(bridgeFbp) ||
                !string.IsNullOrWhiteSpace(bridgeFbc) ||
                !string.IsNullOrWhiteSpace(bridgeFbclid) ||
                !string.IsNullOrWhiteSpace(bridgeClientIp) ||
                !string.IsNullOrWhiteSpace(bridgeUserAgent);

            if (!hasContactData &&
                string.IsNullOrWhiteSpace(websiteLead?.Fbp) &&
                string.IsNullOrWhiteSpace(websiteLead?.Fbc) &&
                string.IsNullOrWhiteSpace(websiteLead?.ClientIpAddress) &&
                string.IsNullOrWhiteSpace(websiteLead?.ClientUserAgent) &&
                !hasBridgeAttribution)
            {
                row.MetadataJson = MergeDispatchMetadata(row.MetadataJson, new MetaConversionsApiResult
                {
                    Attempted = false,
                    Sent = false,
                    Status = "skipped",
                    Note = "No website attribution or CRM contact identity available for Meta CAPI."
                });

                _logger.LogWarning(
                    "MetaSignal outcome dispatcher skipped event={EventName} rowId={RowId}; no website attribution or CRM contact identity.",
                    row.EventName,
                    row.Id);

                continue;
            }

            var pixelContext = await metaPixelResolutionService.ResolveForLeadAsync(
                row.AgentTrackingProfileId ?? websiteLead?.AgentTrackingProfileId,
                row.AgentSlug ?? websiteLead?.AgentSlug,
                isFounderPath: false,
                cancellationToken);

            var capiRequest = new MetaConversionsApiEventRequest
            {
                LeadId = row.LeadId,
                CorrelationId = Guid.NewGuid(),
                EventName = row.EventName,
                EventId = isBridgeOwned
                    ? FirstNonBlank(
                        MetaSignalAnalyticsBridgeMetadata.ReadString(row.MetadataJson, "upstreamMetaEventId"),
                        row.MetaDeduplicationKey,
                        row.EventId) ?? row.EventId
                    : string.IsNullOrWhiteSpace(row.MetaDeduplicationKey)
                        ? row.EventId
                        : row.MetaDeduplicationKey,
                QuoteType = row.QuoteType ?? "crm",
                PageKey = row.EffectivePageKey ?? row.PageKey ?? websiteLead?.SourcePageKey ?? "crm",
                OfferKey = row.QuoteType ?? websiteLead?.InterestType ?? "crm",
                EventSourceUrl = ResolveEventSourceUrl(row, websiteLead),
                Fbclid = FirstNonBlank(websiteLead?.Fbclid, bridgeFbclid),
                ClientIpAddress = FirstNonBlank(websiteLead?.ClientIpAddress, bridgeClientIp),
                ClientUserAgent = FirstNonBlank(websiteLead?.ClientUserAgent, bridgeUserAgent),
                Fbp = FirstNonBlank(websiteLead?.Fbp, bridgeFbp),
                Fbc = FirstNonBlank(websiteLead?.Fbc, bridgeFbc),
                Email = email,
                Phone = phone,
                FirstName = firstName,
                LastName = lastName,
                DateOfBirth = crmContact.DateOfBirth,
                Gender = crmContact.Gender,
                City = crmContact.City,
                State = crmContact.State,
                ZipCode = crmContact.ZipCode,
                AllowHashedContactData = hasContactData,
                EventUtc = row.CreatedUtc == default ? DateTime.UtcNow : row.CreatedUtc,
                PixelId = pixelContext.PixelId,
                AccessToken = pixelContext.AccessToken,
                TestEventCode = pixelContext.TestEventCode,
                PixelOwnerType = pixelContext.PixelOwnerType,
                AuthoritySource = MetaSendAuthoritySources.MetaSignalOutcomeDispatcherHostedService,
                AuthorityDeduplicationKey = row.MetaDeduplicationKey,
                AuthoritySessionId = row.SessionId,
                AuthorityVisitorId = row.VisitorId,
                CustomData = BuildCustomData(row, websiteLead, pixelContext)
            };

            var result = await capi.SendEventAsync(capiRequest, cancellationToken);

            row.MetaServerSent = result.Sent;
            row.FbclidPresent = !string.IsNullOrWhiteSpace(FirstNonBlank(websiteLead?.Fbclid, bridgeFbclid));
            row.FbcPresent = !string.IsNullOrWhiteSpace(FirstNonBlank(websiteLead?.Fbc, bridgeFbc));
            row.FbpPresent = !string.IsNullOrWhiteSpace(FirstNonBlank(websiteLead?.Fbp, bridgeFbp));
            row.MetadataJson = MergeDispatchMetadata(row.MetadataJson, result);

            _logger.LogInformation(
                "MetaSignal outcome dispatcher event={EventName} rowId={RowId} leadId={LeadId} status={Status} sent={Sent}",
                row.EventName,
                row.Id,
                row.LeadId,
                result.Status,
                result.Sent);
        }

        await db.SaveChangesAsync(cancellationToken);
    }


    private sealed record CrmContactIdentity(
        string? Email,
        string? Phone,
        string? FirstName,
        string? LastName,
        DateTime? DateOfBirth,
        string? Gender,
        string? City,
        string? State,
        string? ZipCode);

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();

    private static string? ReadMetadataString(string? metadataJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            return doc.RootElement.TryGetProperty(propertyName, out var element) &&
                   element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<CrmContactIdentity> ResolveCrmContactAsync(
        MasterAppDbContext db,
        MetaSignalEvent row,
        CancellationToken cancellationToken)
    {
        var side = ReadMetadataString(row.MetadataJson, "side");
        var leadId = ReadMetadataString(row.MetadataJson, "leadId");
        var clientUserId = ReadMetadataString(row.MetadataJson, "clientUserId");

        if (string.Equals(side, "Client", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(clientUserId))
        {
            var client = await db.ClientProfiles
                .AsNoTracking()
                .Where(x => x.ClientUserId == clientUserId)
                .Select(x => new
                {
                    x.Email,
                    x.Phone,
                    x.FirstName,
                    x.LastName,
                    x.DOB,
                    x.CrmNotes
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (client is not null)
            {
                var meta = ReadClientCrmMeta(client.CrmNotes);
                return new CrmContactIdentity(
                    client.Email,
                    client.Phone,
                    client.FirstName,
                    client.LastName,
                    client.DOB,
                    meta.Gender,
                    meta.City,
                    meta.State,
                    meta.ZipCode);
            }
        }

        if (!string.IsNullOrWhiteSpace(leadId))
        {
            var lead = await db.WorkstationLeadProfiles
                .AsNoTracking()
                .Where(x => x.LeadId == leadId)
                .Select(x => new CrmContactIdentity(
                    x.Email,
                    x.Phone,
                    x.FirstName,
                    x.LastName,
                    x.DOB,
                    x.Gender,
                    x.City,
                    x.State,
                    x.ZipCode))
                .FirstOrDefaultAsync(cancellationToken);

            if (lead is not null)
                return lead;
        }

        if (!string.IsNullOrWhiteSpace(clientUserId))
        {
            var convertedLead = await db.WorkstationLeadProfiles
                .AsNoTracking()
                .Where(x => x.LeadId == clientUserId)
                .Select(x => new CrmContactIdentity(
                    x.Email,
                    x.Phone,
                    x.FirstName,
                    x.LastName,
                    x.DOB,
                    x.Gender,
                    x.City,
                    x.State,
                    x.ZipCode))
                .FirstOrDefaultAsync(cancellationToken);

            if (convertedLead is not null)
                return convertedLead;
        }

        return new CrmContactIdentity(null, null, null, null, null, null, null, null, null);
    }

    private sealed record ClientCrmMetaIdentity(string? Gender, string? City, string? State, string? ZipCode);

    private static ClientCrmMetaIdentity ReadClientCrmMeta(string? crmNotes)
    {
        if (string.IsNullOrWhiteSpace(crmNotes))
            return new ClientCrmMetaIdentity(null, null, null, null);

        try
        {
            using var doc = JsonDocument.Parse(crmNotes);
            var root = doc.RootElement;

            return new ClientCrmMetaIdentity(
                ReadJsonString(root, "gender"),
                ReadJsonString(root, "city"),
                ReadJsonString(root, "state"),
                ReadJsonString(root, "zipCode"));
        }
        catch
        {
            return new ClientCrmMetaIdentity(null, null, null, null);
        }
    }

    private static string? ReadJsonString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var element) &&
           element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static Dictionary<string, object?> BuildCustomData(
        MetaSignalEvent row,
        WebsiteLead? websiteLead,
        ResolvedMetaPixelContext pixelContext)
    {
        var isBridgeOwned = MetaSignalAnalyticsBridgeMetadata.IsBridgeOwned(row.MetadataJson);
        var customData = new Dictionary<string, object?>
        {
            ["event_category"] = row.EventCategory,
            ["quote_type"] = row.QuoteType,
            ["traffic_type"] = row.TrafficType,
            ["funnel_step"] = row.FunnelStep,
            ["step_name"] = row.StepName,
            ["score_tier"] = row.ScoreTier,
            ["total_signal_score"] = row.TotalSignalScore,
            ["source"] = isBridgeOwned ? "analytics_bridge" : "crm_outcome_dispatcher",
            ["website_lead_id"] = row.LeadId,
            ["lead_interest_type"] = websiteLead?.InterestType,
            ["lead_source_page_key"] = websiteLead?.SourcePageKey,
            ["lead_utm_source"] = websiteLead?.UtmSource,
            ["lead_utm_medium"] = websiteLead?.UtmMedium,
            ["lead_utm_campaign"] = websiteLead?.UtmCampaign,
            ["agent_tracking_profile_id"] = pixelContext.AgentTrackingProfileId,
            ["agent_slug"] = pixelContext.AgentSlug,
            ["pixel_owner_type"] = pixelContext.PixelOwnerType,
            ["is_browser_signal"] = ReadMetadataBoolean(row.MetadataJson, "isBrowserSignal")
                ?? MetaSignalEventCatalog.IsBrowserSignalEvent(row.EventName),
            ["is_server_authority"] = ReadMetadataBoolean(row.MetadataJson, "isServerAuthority")
                ?? MetaSignalEventCatalog.IsServerAuthorityEvent(row.EventName),
            ["event_key"] = FirstNonBlank(
                ReadMetadataString(row.MetadataJson, "eventKey"),
                MetaSignalEventCatalog.BuildEventKey(row.EventName, row.LeadId, row.SessionId)),
            ["conflict_resolution"] = "server_authority_wins"
        };

        if (isBridgeOwned)
        {
            customData["source_analytics_event_type"] = MetaSignalAnalyticsBridgeMetadata.ReadString(row.MetadataJson, "sourceAnalyticsEventType");
            customData["source_analytics_event_id"] = MetaSignalAnalyticsBridgeMetadata.ReadInt64(row.MetadataJson, "sourceAnalyticsEventId");
        }

        if (IsProductionValueEvent(row.EventName) &&
            TryReadPositiveDecimal(row.MetadataJson, "personalAmount", out var personalAmount))
        {
            customData["value"] = decimal.Round(personalAmount, 2);
            customData["currency"] = "USD";
        }

        return customData;
    }

    private static bool IsProductionValueEvent(string? eventName)
        => string.Equals(eventName, "ApplicationSubmitted", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(eventName, "PolicyIssued", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(eventName, "PolicyPaid", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadPositiveDecimal(string? metadataJson, string propertyName, out decimal value)
    {
        value = 0;

        if (string.IsNullOrWhiteSpace(metadataJson))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (!doc.RootElement.TryGetProperty(propertyName, out var element))
                return false;

            if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out value))
                return value > 0;

            if (element.ValueKind == JsonValueKind.String &&
                decimal.TryParse(element.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out value))
                return value > 0;
        }
        catch
        {
            value = 0;
        }

        return false;
    }

    private static string? ResolveEventSourceUrl(MetaSignalEvent row, WebsiteLead? websiteLead)
    {
        var explicitUrl = MetaSignalAnalyticsBridgeMetadata.ReadString(row.MetadataJson, "sourceUrl");
        if (!string.IsNullOrWhiteSpace(explicitUrl))
            return explicitUrl.Trim();

        if (websiteLead != null && !string.IsNullOrWhiteSpace(websiteLead.Host))
        {
            var host = websiteLead.Host.Trim();
            var scheme = host.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
                ? "http"
                : "https";

            var path = string.IsNullOrWhiteSpace(websiteLead.SourcePageKey)
                ? "/"
                : $"/Quote/{websiteLead.SourcePageKey.Trim().TrimStart('/')}";

            return $"{scheme}://{host}{path}";
        }

        var fallbackHost = FirstNonBlank(row.Host, MetaSignalAnalyticsBridgeMetadata.ReadString(row.MetadataJson, "sourceHost"));
        if (string.IsNullOrWhiteSpace(fallbackHost))
            return null;

        var fallbackScheme = fallbackHost.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
            ? "http"
            : "https";

        var fallbackPath = string.IsNullOrWhiteSpace(row.PageKey)
            ? "/"
            : $"/Quote/{row.PageKey.Trim().TrimStart('/')}";

        return $"{fallbackScheme}://{fallbackHost}{fallbackPath}";
    }

    private static bool? ReadMetadataBoolean(string? metadataJson, string propertyName)
    {
        var raw = ReadMetadataString(metadataJson, propertyName);
        return bool.TryParse(raw, out var value) ? value : null;
    }

    private static string MergeDispatchMetadata(string? existingJson, MetaConversionsApiResult result)
    {
        Dictionary<string, object?> metadata;

        try
        {
            metadata = string.IsNullOrWhiteSpace(existingJson)
                ? new Dictionary<string, object?>()
                : JsonSerializer.Deserialize<Dictionary<string, object?>>(existingJson) ?? new Dictionary<string, object?>();
        }
        catch
        {
            metadata = new Dictionary<string, object?>
            {
                ["original_metadata_json"] = existingJson
            };
        }

        metadata["metaServerStatus"] = result.Status;
        metadata["metaServerNote"] = result.Note;
        metadata["metaServerAttempted"] = result.Attempted;
        metadata["metaServerSent"] = result.Sent;
        metadata["metaServerPixelId"] = result.PixelId;
        metadata["metaServerPixelOwnerType"] = result.PixelOwnerType;
        metadata["metaServerDispatchedUtc"] = DateTime.UtcNow;

        return JsonSerializer.Serialize(metadata);
    }
}
