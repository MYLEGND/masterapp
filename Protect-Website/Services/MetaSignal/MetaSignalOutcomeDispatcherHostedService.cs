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
    private static readonly HashSet<string> DispatchableEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppointmentBooked",
        "AppointmentCompleted",
        "ApplicationSubmitted",
        "PolicyIssued",
        "PolicyPaid"
    };

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
                x.TrafficType == "crm" &&
                DispatchableEvents.Contains(x.EventName))
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
            if (!MetaSignalEventCatalog.TryGet(row.EventName, out var definition) || !definition.AllowServerForward)
                continue;

            WebsiteLead? websiteLead = null;
            if (row.LeadId.HasValue &&
                intakeLinksByWorkstationLeadId.TryGetValue(row.LeadId.Value.ToString("N"), out var intakeLink))
            {
                leadsById.TryGetValue(intakeLink.WebsiteLeadPublicId, out websiteLead);
            }

            var crmContact = await ResolveCrmContactAsync(db, row, cancellationToken);

            var email = FirstNonBlank(websiteLead?.Email, crmContact.Email);
            var phone = FirstNonBlank(websiteLead?.Phone, crmContact.Phone);

            var hasContactData =
                !string.IsNullOrWhiteSpace(email) ||
                !string.IsNullOrWhiteSpace(phone);

            if (!hasContactData &&
                string.IsNullOrWhiteSpace(websiteLead?.Fbp) &&
                string.IsNullOrWhiteSpace(websiteLead?.Fbc) &&
                string.IsNullOrWhiteSpace(websiteLead?.ClientIpAddress) &&
                string.IsNullOrWhiteSpace(websiteLead?.ClientUserAgent))
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

            var result = await capi.SendEventAsync(
                new MetaConversionsApiEventRequest
                {
                    LeadId = row.LeadId,
                    CorrelationId = Guid.NewGuid(),
                    EventName = row.EventName,
                    EventId = string.IsNullOrWhiteSpace(row.MetaDeduplicationKey) ? row.EventId : row.MetaDeduplicationKey,
                    QuoteType = row.QuoteType ?? "crm",
                    PageKey = row.EffectivePageKey ?? row.PageKey ?? websiteLead?.SourcePageKey ?? "crm",
                    OfferKey = row.QuoteType ?? websiteLead?.InterestType ?? "crm",
                    EventSourceUrl = BuildEventSourceUrl(websiteLead),
                    Fbclid = websiteLead?.Fbclid,
                    ClientIpAddress = websiteLead?.ClientIpAddress,
                    ClientUserAgent = websiteLead?.ClientUserAgent,
                    Fbp = websiteLead?.Fbp,
                    Fbc = websiteLead?.Fbc,
                    Email = email,
                    Phone = phone,
                    AllowHashedContactData = hasContactData,
                    EventUtc = row.CreatedUtc == default ? DateTime.UtcNow : row.CreatedUtc,
                    PixelId = pixelContext.PixelId,
                    AccessToken = pixelContext.AccessToken,
                    TestEventCode = pixelContext.TestEventCode,
                    PixelOwnerType = pixelContext.PixelOwnerType,
                    CustomData = BuildCustomData(row, websiteLead, pixelContext)
                },
                cancellationToken);

            row.MetaServerSent = result.Sent;
            row.FbclidPresent = !string.IsNullOrWhiteSpace(websiteLead?.Fbclid);
            row.FbcPresent = !string.IsNullOrWhiteSpace(websiteLead?.Fbc);
            row.FbpPresent = !string.IsNullOrWhiteSpace(websiteLead?.Fbp);
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


    private sealed record CrmContactIdentity(string? Email, string? Phone);

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
                .Select(x => new CrmContactIdentity(x.Email, x.Phone))
                .FirstOrDefaultAsync(cancellationToken);

            if (client is not null)
                return client;
        }

        if (!string.IsNullOrWhiteSpace(leadId))
        {
            var lead = await db.WorkstationLeadProfiles
                .AsNoTracking()
                .Where(x => x.LeadId == leadId)
                .Select(x => new CrmContactIdentity(x.Email, x.Phone))
                .FirstOrDefaultAsync(cancellationToken);

            if (lead is not null)
                return lead;
        }

        if (!string.IsNullOrWhiteSpace(clientUserId))
        {
            var convertedLead = await db.WorkstationLeadProfiles
                .AsNoTracking()
                .Where(x => x.LeadId == clientUserId)
                .Select(x => new CrmContactIdentity(x.Email, x.Phone))
                .FirstOrDefaultAsync(cancellationToken);

            if (convertedLead is not null)
                return convertedLead;
        }

        return new CrmContactIdentity(null, null);
    }

    private static Dictionary<string, object?> BuildCustomData(
        MetaSignalEvent row,
        WebsiteLead? websiteLead,
        ResolvedMetaPixelContext pixelContext)
    {
        var customData = new Dictionary<string, object?>
        {
            ["event_category"] = row.EventCategory,
            ["quote_type"] = row.QuoteType,
            ["traffic_type"] = row.TrafficType,
            ["funnel_step"] = row.FunnelStep,
            ["step_name"] = row.StepName,
            ["score_tier"] = row.ScoreTier,
            ["total_signal_score"] = row.TotalSignalScore,
            ["source"] = "crm_outcome_dispatcher",
            ["website_lead_id"] = row.LeadId,
            ["lead_interest_type"] = websiteLead?.InterestType,
            ["lead_source_page_key"] = websiteLead?.SourcePageKey,
            ["lead_utm_source"] = websiteLead?.UtmSource,
            ["lead_utm_medium"] = websiteLead?.UtmMedium,
            ["lead_utm_campaign"] = websiteLead?.UtmCampaign,
            ["agent_tracking_profile_id"] = pixelContext.AgentTrackingProfileId,
            ["agent_slug"] = pixelContext.AgentSlug,
            ["pixel_owner_type"] = pixelContext.PixelOwnerType
        };

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

    private static string? BuildEventSourceUrl(WebsiteLead? websiteLead)
    {
        if (websiteLead == null || string.IsNullOrWhiteSpace(websiteLead.Host))
            return null;

        var host = websiteLead.Host.Trim();
        var scheme = host.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
            ? "http"
            : "https";

        var path = string.IsNullOrWhiteSpace(websiteLead.SourcePageKey)
            ? "/"
            : $"/Quote/{websiteLead.SourcePageKey.Trim().TrimStart('/')}";

        return $"{scheme}://{host}{path}";
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
