using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Infrastructure.Analytics;
using System.Globalization;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shared.Analytics;

namespace Infrastructure.Analytics;

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
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        var candidates = db.MetaSignalEvents.AsNoTracking()
            .Where(x =>
                !x.MetaServerSent &&
                (x.MetaDispatchClaimToken == null || x.MetaDispatchClaimExpiresUtc == null || x.MetaDispatchClaimExpiresUtc <= DateTime.UtcNow) &&
                x.MetadataJson != null &&
                (!x.MetadataJson.Contains("\"metaServerStatus\":") ||
                 x.MetadataJson.Contains("\"metaServerRetryable\":true")) &&
                (x.TrafficType == "crm" || x.TrafficType == "ecommerce" ||
                 x.MetadataJson.Contains(MetaSignalAnalyticsBridgeMetadata.BridgeSourceMarker)) &&
                DispatchableEvents.Contains(x.EventName) &&
                x.MetadataJson.Contains(MetaSignalSingleTruthPolicy.DispatchEligibleMarker))
            .OrderBy(x => x.CreatedUtc)
            .AsAsyncEnumerable();

        var rows = new List<MetaSignalEvent>();
        var rejected = new List<(MetaSignalEvent Row, MetaConversionsApiResult Decision)>();
        await foreach (var row in candidates.WithCancellation(cancellationToken))
        {
            if (row.CommerceBusinessId.HasValue && (row.CommerceBusinessId == Guid.Empty || row.AgentTrackingProfileId.HasValue || !string.IsNullOrWhiteSpace(row.AgentSlug)))
            {
                rejected.Add((row, new MetaConversionsApiResult
                {
                    Status = "skipped_owner_conflict", Note = "business_and_agent_owner_conflict"
                }));
                continue;
            }
            if (IsBlockedAutomatedTraffic(row) ||
                !(string.Equals(row.TrafficType, "crm", StringComparison.OrdinalIgnoreCase) ||
                  MetaSignalAnalyticsBridgeMetadata.IsBridgeOwned(row.MetadataJson) ||
                  MetaSignalSingleTruthPolicy.IsTrustedCommerceBridgeProducer(row.TrafficType, row.MetadataJson)))
            {
                rejected.Add((row, new MetaConversionsApiResult
                {
                    Status = "skipped_traffic_or_producer", Note = "untrusted_or_automated"
                }));
                continue;
            }
            if (DateTime.TryParse(ReadMetadataString(row.MetadataJson, "metaServerNextAttemptUtc"),
                    CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var nextAttempt) && nextAttempt > DateTime.UtcNow)
                continue;
            rows.Add(row);
            if (rows.Count == 25) break;
        }

        // Close the candidate reader before guarded writes (SQL Server need not enable MARS).
        foreach (var item in rejected)
            await MetaSendAuthority.PersistPreDispatchDecisionAsync(db, item.Row, item.Decision, cancellationToken);

        var leadIds = rows
            .Where(x => x.LeadId.HasValue)
            .Select(x => x.LeadId!.Value)
            .Distinct()
            .ToList();

        var workstationLeadIds = leadIds
            .Select(x => x.ToString("N"))
            .ToArray();

        var linkedIntakes = await db.WebsiteLeadIntakeLinks.AsNoTracking()
            .Where(x => x.WorkstationLeadId != null && workstationLeadIds.Contains(x.WorkstationLeadId))
            .ToListAsync(cancellationToken);
        // An explicitly associated relationship can have multiple submissions.
        // Do not guess which submission a legacy relationship-only outcome means.
        // Exact public submission IDs still resolve through the canonical lookup below.
        var intakeLinksByWorkstationLeadId = linkedIntakes.GroupBy(x => x.WorkstationLeadId!, StringComparer.Ordinal)
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);

        // A canonical website conversion may carry either the public WebsiteLead ID
        // directly (business/website intake) or a workstation lead ID (legacy Protect
        // bridge). Load both through this one lookup; owner checks below remain mandatory.
        var websiteLeadIds = intakeLinksByWorkstationLeadId.Values
            .Select(x => x.WebsiteLeadPublicId)
            .Concat(leadIds)
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
                await MetaSendAuthority.PersistPreDispatchDecisionAsync(db, row, new MetaConversionsApiResult
                {
                    Attempted = false,
                    Sent = false,
                    Status = "skipped_catalog_missing",
                    Note = "catalog_missing"
                }, cancellationToken);
                continue;
            }

            if (!definition.AllowServerForward)
            {
                await MetaSendAuthority.PersistPreDispatchDecisionAsync(db, row, new MetaConversionsApiResult
                {
                    Attempted = false,
                    Sent = false,
                    Status = "skipped_server_forward_not_allowed",
                    Note = "server_forward_not_allowed"
                }, cancellationToken);
                continue;
            }

            if (!MetaSignalEventCatalog.IsServerAuthorityEvent(row.EventName))
            {
                await MetaSendAuthority.PersistPreDispatchDecisionAsync(db, row, new MetaConversionsApiResult
                {
                    Attempted = false,
                    Sent = false,
                    Status = "skipped_not_server_authority_event",
                    Note = "not_server_authority_event"
                }, cancellationToken);
                continue;
            }

            if (!MetaSignalSingleTruthPolicy.CanDispatchServerAuthority(row.EventName, row.MetadataJson))
            {
                await MetaSendAuthority.PersistPreDispatchDecisionAsync(db, row, new MetaConversionsApiResult
                {
                    Attempted = false,
                    Sent = false,
                    Status = "skipped_not_dispatch_eligible",
                    Note = "not_dispatch_eligible"
                }, cancellationToken);
                continue;
            }

            var isBridgeOwned = MetaSignalAnalyticsBridgeMetadata.IsBridgeOwned(row.MetadataJson);
            var bridgeClientIp = FirstNonBlank(
                MetaSignalAnalyticsBridgeMetadata.ReadString(row.MetadataJson, "sourceClientIpAddress"),
                ReadMetadataString(row.MetadataJson, "sourceClientIpAddress"));
            var bridgeUserAgent = FirstNonBlank(
                MetaSignalAnalyticsBridgeMetadata.ReadString(row.MetadataJson, "sourceClientUserAgent"),
                ReadMetadataString(row.MetadataJson, "sourceClientUserAgent"),
                row.UserAgent);
            var bridgeFbclid = FirstNonBlank(
                MetaSignalAnalyticsBridgeMetadata.ReadString(row.MetadataJson, "sourceFbclid"),
                ReadMetadataString(row.MetadataJson, "sourceFbclid"),
                ReadMetadataString(row.MetadataJson, "fbclid"));
            var bridgeFbp = FirstNonBlank(
                MetaSignalAnalyticsBridgeMetadata.ReadString(row.MetadataJson, "sourceFbp"),
                ReadMetadataString(row.MetadataJson, "sourceFbp"),
                ReadMetadataString(row.MetadataJson, "fbp"));
            var bridgeFbc = FirstNonBlank(
                MetaSignalAnalyticsBridgeMetadata.ReadString(row.MetadataJson, "sourceFbc"),
                ReadMetadataString(row.MetadataJson, "sourceFbc"),
                ReadMetadataString(row.MetadataJson, "fbc"));

            var sourceId = CanonicalAdvertisingEventProjection.ReadInt64(row.MetadataJson, "sourceAnalyticsEventId");
            AnalyticsEvent? source = sourceId.HasValue
                ? await db.AnalyticsEvents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sourceId.Value, cancellationToken)
                : null;
            var humanEligibility = source is null ? null : await CanonicalMarketingEligibility.ResolveAsync(db, source, cancellationToken);
            if (humanEligibility?.Eligible != true)
            {
                await MetaSendAuthority.PersistPreDispatchDecisionAsync(db, row, new MetaConversionsApiResult {
                    Attempted = false, Sent = false, Retryable = true, Status = humanEligibility?.Reason.StartsWith("measurement_consent", StringComparison.Ordinal) == true ? "blocked_consent" : humanEligibility?.Reason.StartsWith("production_", StringComparison.Ordinal) == true ? "blocked_outcome_reconciliation" : "blocked_human_evidence",
                    Note = humanEligibility?.Reason ?? "canonical_source_missing"
                }, cancellationToken);
                continue;
            }
            CanonicalMarketingIdentity? canonicalIdentity = null;
            if (source is not null &&
                source.AgentTrackingProfileId == row.AgentTrackingProfileId &&
                source.CommerceBusinessId == row.CommerceBusinessId &&
                CanonicalAdvertisingEventProjection.CanProjectServer(source))
            {
                canonicalIdentity = await CanonicalMarketingIdentityResolver.ResolveAsync(
                    db, source, row.LeadId, cancellationToken);
                bridgeClientIp = FirstNonBlank(canonicalIdentity.ClientIpAddress, bridgeClientIp);
                bridgeUserAgent = FirstNonBlank(canonicalIdentity.ClientUserAgent, bridgeUserAgent);
                bridgeFbclid = FirstNonBlank(canonicalIdentity.Fbclid, bridgeFbclid);
                bridgeFbp = FirstNonBlank(canonicalIdentity.Fbp, bridgeFbp);
                bridgeFbc = FirstNonBlank(canonicalIdentity.Fbc, bridgeFbc);
            }

            WebsiteLead? websiteLead = canonicalIdentity?.WebsiteLead;
            CrmContactIdentity crmContact;
            string? email;
            string? phone;
            string? firstName;
            string? lastName;
            string? city;
            string? state;
            string? zipCode;
            bool hasContactData;

            if (canonicalIdentity is not null)
            {
                email = canonicalIdentity.Email;
                phone = canonicalIdentity.Phone;
                firstName = canonicalIdentity.FirstName;
                lastName = canonicalIdentity.LastName;
                city = canonicalIdentity.City;
                state = canonicalIdentity.State;
                zipCode = canonicalIdentity.PostalCode;
                hasContactData = canonicalIdentity.HasContactData;
                crmContact = new(
                    canonicalIdentity.Email,
                    canonicalIdentity.Phone,
                    canonicalIdentity.FirstName,
                    canonicalIdentity.LastName,
                    canonicalIdentity.DateOfBirth,
                    canonicalIdentity.Gender,
                    canonicalIdentity.City,
                    canonicalIdentity.State,
                    canonicalIdentity.PostalCode);
            }
            else
            {
                // Historical queue adapter only. New bridge rows always use the shared
                // CanonicalMarketingIdentityResolver above.
                if (row.LeadId.HasValue)
                {
                    leadsById.TryGetValue(row.LeadId.Value, out websiteLead);
                    if (websiteLead == null &&
                        intakeLinksByWorkstationLeadId.TryGetValue(row.LeadId.Value.ToString("N"), out var intakeLink))
                        leadsById.TryGetValue(intakeLink.WebsiteLeadPublicId, out websiteLead);
                }

                if (websiteLead is not null && websiteLead.CommerceBusinessId != row.CommerceBusinessId)
                {
                    await MetaSendAuthority.PersistPreDispatchDecisionAsync(db, row, new MetaConversionsApiResult
                    {
                        Status = "skipped_owner_conflict", Note = "website_lead_owner_mismatch"
                    }, cancellationToken);
                    continue;
                }

                crmContact = await ResolveCrmContactAsync(db, row, cancellationToken);
                var metadataEmail = ReadNestedMetadataString(row.MetadataJson, "customer", "email");
                var metadataPhone = ReadNestedMetadataString(row.MetadataJson, "customer", "phone");
                var metadataFirstName = ReadNestedMetadataString(row.MetadataJson, "customer", "firstName");
                var metadataLastName = ReadNestedMetadataString(row.MetadataJson, "customer", "lastName");
                var metadataCity = ReadNestedMetadataString(row.MetadataJson, "customer", "city");
                var metadataState = ReadNestedMetadataString(row.MetadataJson, "customer", "state");
                var metadataZipCode = FirstNonBlank(
                    ReadNestedMetadataString(row.MetadataJson, "customer", "zipCode"),
                    ReadNestedMetadataString(row.MetadataJson, "customer", "postalCode"));

                email = FirstNonBlank(websiteLead?.Email, crmContact.Email, metadataEmail);
                phone = FirstNonBlank(websiteLead?.Phone, crmContact.Phone, metadataPhone);
                firstName = FirstNonBlank(websiteLead?.FirstName, crmContact.FirstName, metadataFirstName);
                lastName = FirstNonBlank(websiteLead?.LastName, crmContact.LastName, metadataLastName);
                city = FirstNonBlank(crmContact.City, metadataCity);
                state = FirstNonBlank(crmContact.State, metadataState);
                zipCode = FirstNonBlank(crmContact.ZipCode, metadataZipCode);
                hasContactData =
                    !string.IsNullOrWhiteSpace(email) ||
                    !string.IsNullOrWhiteSpace(phone) ||
                    !string.IsNullOrWhiteSpace(firstName) ||
                    !string.IsNullOrWhiteSpace(lastName) ||
                    crmContact.DateOfBirth.HasValue ||
                    !string.IsNullOrWhiteSpace(crmContact.Gender) ||
                    !string.IsNullOrWhiteSpace(city) ||
                    !string.IsNullOrWhiteSpace(state) ||
                    !string.IsNullOrWhiteSpace(zipCode);
            }

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
                await MetaSendAuthority.PersistPreDispatchDecisionAsync(db, row, new MetaConversionsApiResult
                {
                    Attempted = false,
                    Sent = false,
                    Status = "skipped",
                    Note = "No website attribution or CRM contact identity available for Meta CAPI."
                }, cancellationToken);

                _logger.LogWarning(
                    "MetaSignal outcome dispatcher skipped event={EventName} rowId={RowId}; no website attribution or CRM contact identity.",
                    row.EventName,
                    row.Id);

                continue;
            }

            MarketingOwnerScope? owner = source is not null &&
                source.AgentTrackingProfileId == row.AgentTrackingProfileId &&
                source.CommerceBusinessId == row.CommerceBusinessId &&
                CanonicalAdvertisingEventProjection.CanProjectServer(source)
                ? await CanonicalAdvertisingEventProjection.ResolveOwnerAsync(db, configuration, source, cancellationToken)
                : null;
            if (owner is null)
            {
                await MetaSendAuthority.PersistPreDispatchDecisionAsync(db, row, new MetaConversionsApiResult
                {
                    Attempted = false, Sent = false, Status = "skipped_owner_unresolved", Note = "canonical_source_owner_unresolved"
                }, cancellationToken);
                continue;
            }

            // New bridge rows never choose their external Meta event name independently.
            // The same provider-neutral conversion catalog used by OpenAI owns translation
            // for both destinations. Historical queue rows without a canonical source retain
            // their recorded event name as a read/dispatch compatibility adapter only.
            var providerEventName = row.EventName;
            if (source is not null)
            {
                var canonicalEventName = CanonicalAdvertisingEventProjection.ResolveEventName(source);
                var destination = MarketingConversionDestinationCatalog.ResolveMeta(canonicalEventName);
                if (destination is null)
                {
                    await MetaSendAuthority.PersistPreDispatchDecisionAsync(db, row, new MetaConversionsApiResult
                    {
                        Attempted = false, Sent = false, Status = "skipped_destination_mapping_missing",
                        Note = "canonical_meta_destination_mapping_missing"
                    }, cancellationToken);
                    continue;
                }
                providerEventName = destination.EventName;
            }

            var pixelContext = await metaPixelResolutionService.ResolveForOwnerAsync(owner, cancellationToken);

            var capiRequest = new MetaConversionsApiEventRequest
            {
                LeadId = canonicalIdentity?.WebsiteLeadId ?? row.LeadId,
                CommerceBusinessId = row.CommerceBusinessId,
                AgentTrackingProfileId = row.AgentTrackingProfileId,
                CorrelationId = Guid.NewGuid(),
                EventName = providerEventName,
                EventId = MetaSignalSingleTruthPolicy.ReadBoolean(row.MetadataJson, "canonicalSourceIdentity") == true
                    ? row.EventId
                    : isBridgeOwned
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
                City = city,
                State = state,
                ZipCode = zipCode,
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

            _logger.LogInformation(
                "MetaSignal outcome dispatcher event={EventName} rowId={RowId} leadId={LeadId} status={Status} sent={Sent}",
                row.EventName,
                row.Id,
                row.LeadId,
                result.Status,
                result.Sent);
        }

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

    private static bool IsBlockedAutomatedTraffic(MetaSignalEvent row)
    {
        return row.WebDriver == true ||
               row.IsHeadless == true ||
               ReadMetadataBoolean(row.MetadataJson, "sourceWebDriver") == true ||
               ReadMetadataBoolean(row.MetadataJson, "sourceIsHeadless") == true ||
               ReadMetadataBoolean(row.MetadataJson, "webDriver") == true ||
               ReadMetadataBoolean(row.MetadataJson, "isHeadless") == true;
    }

    private static string? ReadMetadataString(string? metadataJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            return doc.RootElement.TryGetProperty(propertyName, out var element) &&
                   element.ValueKind != JsonValueKind.Null
                ? element.ToString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadNestedMetadataString(string? metadataJson, string objectName, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);

            if (!doc.RootElement.TryGetProperty(objectName, out var parent) ||
                parent.ValueKind != JsonValueKind.Object ||
                !parent.TryGetProperty(propertyName, out var value) ||
                value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return value.ToString();
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

        if (!row.CommerceBusinessId.HasValue && string.Equals(side, "Client", StringComparison.OrdinalIgnoreCase) &&
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
                .Where(x => x.LeadId == leadId && x.CommerceBusinessId == row.CommerceBusinessId)
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
                .Where(x => x.LeadId == clientUserId && x.CommerceBusinessId == row.CommerceBusinessId)
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
            ["source"] = isBridgeOwned
                ? "analytics_bridge"
                : MetaSignalSingleTruthPolicy.IsTrustedCommerceBridgeProducer(row.TrafficType, row.MetadataJson)
                    ? "commerce_bridge"
                    : "crm_outcome_dispatcher",
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

        if (CanonicalConversionValueProjection.Resolve(row.MetadataJson) is { } canonicalValue)
        {
            customData["value"] = decimal.Round(canonicalValue.AmountMinorUnits / 100m, 2);
            customData["currency"] = canonicalValue.Currency;
        }

        return customData;
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

}
