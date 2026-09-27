using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shared.Analytics;
using Shared.Crm;

namespace Infrastructure.Analytics;

public sealed class MetaSignalCrmOutcomeService
{
    private readonly MasterAppDbContext _db;
    private readonly ILogger<MetaSignalCrmOutcomeService> _logger;

    public MetaSignalCrmOutcomeService(
        MasterAppDbContext db,
        ILogger<MetaSignalCrmOutcomeService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task RecordAppointmentOutcomeAsync(
        LeadAppointment appointment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(appointment);

        var eventName = appointment.Status switch
        {
            LeadAppointmentStatus.Booked or LeadAppointmentStatus.Confirmed => AppointmentAnalyticsEventCatalog.Booked,
            LeadAppointmentStatus.Rescheduled => AppointmentAnalyticsEventCatalog.Rescheduled,
            LeadAppointmentStatus.Cancelled => AppointmentAnalyticsEventCatalog.Cancelled,
            LeadAppointmentStatus.Completed => AppointmentAnalyticsEventCatalog.Completed,
            LeadAppointmentStatus.NoShow => AppointmentAnalyticsEventCatalog.NoShow,
            _ => null
        };
        if (eventName is null)
            return;

        var clientEventId = StableAppointmentEventId(appointment.Id, eventName);
        if (_db.AnalyticsEvents.Local.Any(x => x.ClientEventId == clientEventId) ||
            await _db.AnalyticsEvents.AsNoTracking().AnyAsync(x => x.ClientEventId == clientEventId, cancellationToken))
            return;

        var intakeLink = appointment.WebsiteLeadIntakeLinkId.HasValue
            ? await _db.WebsiteLeadIntakeLinks.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == appointment.WebsiteLeadIntakeLinkId.Value, cancellationToken)
            : null;

        if (intakeLink is null && !string.IsNullOrWhiteSpace(appointment.WorkstationLeadId))
        {
            intakeLink = await _db.WebsiteLeadIntakeLinks.AsNoTracking()
                .Where(x => x.WorkstationLeadId == appointment.WorkstationLeadId)
                .OrderByDescending(x => x.SubmittedUtc)
                .ThenByDescending(x => x.CapturedUtc)
                .FirstOrDefaultAsync(cancellationToken);
        }

        WebsiteLead? websiteLead = null;
        if (intakeLink is not null)
        {
            websiteLead = await _db.WebsiteLeads.AsNoTracking()
                .FirstOrDefaultAsync(x => x.LeadId == intakeLink.WebsiteLeadPublicId, cancellationToken);
        }

        appointment.Oppref ??= OpenAiClickReference.Normalize(intakeLink?.Oppref ?? websiteLead?.Oppref);

        var metaEligible = appointment.Status is LeadAppointmentStatus.Booked
            or LeadAppointmentStatus.Confirmed
            or LeadAppointmentStatus.Completed;

        var context = new UnifiedEventContext
        {
            EventId = $"appointment:{appointment.Id:N}:{eventName}",
            EventName = eventName,
            EventCategory = "appointment",
            SiteKey = MetaSignalSingleTruthPolicy.ReadString(websiteLead?.MetadataJson, "siteKey")
                ?? (websiteLead?.CommerceBusinessId.HasValue == true ? "BusinessWebsite" : "ProtectWebsite"),
            Referrer = intakeLink?.ReferrerUrl,
            Fbc = intakeLink?.Fbc ?? websiteLead?.Fbc,
            Fbp = intakeLink?.Fbp ?? websiteLead?.Fbp,
            UserAgent = intakeLink?.ClientUserAgent ?? websiteLead?.ClientUserAgent,
            IpAddress = intakeLink?.ClientIpAddress ?? websiteLead?.ClientIpAddress,
            EventUtc = appointment.UpdatedUtc == default ? DateTime.UtcNow : appointment.UpdatedUtc,
            SessionId = intakeLink?.SessionId ?? websiteLead?.SessionId,
            VisitorId = intakeLink?.VisitorId ?? websiteLead?.VisitorId,
            PageKey = intakeLink?.SourcePageKey ?? websiteLead?.SourcePageKey,
            EffectivePageKey = intakeLink?.SourcePageKey ?? websiteLead?.SourcePageKey,
            PageVariant = intakeLink?.PageVariant,
            PageMode = intakeLink?.PageMode,
            UtmSource = intakeLink?.UtmSource ?? websiteLead?.UtmSource,
            UtmMedium = intakeLink?.UtmMedium ?? websiteLead?.UtmMedium,
            UtmCampaign = intakeLink?.UtmCampaign ?? websiteLead?.UtmCampaign,
            UtmId = intakeLink?.UtmId ?? websiteLead?.UtmId,
            UtmContent = intakeLink?.UtmContent,
            MetaCampaignId = intakeLink?.MetaCampaignId ?? websiteLead?.MetaCampaignId,
            MetaAdSetId = intakeLink?.MetaAdSetId ?? websiteLead?.MetaAdSetId,
            MetaAdId = intakeLink?.MetaAdId ?? websiteLead?.MetaAdId,
            Fbclid = intakeLink?.Fbclid ?? websiteLead?.Fbclid,
            Oppref = OpenAiClickReference.Normalize(appointment.Oppref ?? intakeLink?.Oppref ?? websiteLead?.Oppref),
            AgentTrackingProfileId = websiteLead?.CommerceBusinessId.HasValue == true
                ? null
                : websiteLead?.AgentTrackingProfileId,
            AgentSlug = websiteLead?.CommerceBusinessId.HasValue == true
                ? null
                : websiteLead?.AgentSlug,
            CommerceBusinessId = websiteLead?.CommerceBusinessId ?? intakeLink?.CommerceBusinessId,
            WebsiteContentVersionId = websiteLead?.WebsiteContentVersionId,
            WebsiteBindingId = websiteLead?.WebsiteBindingId,
            Environment = websiteLead?.Environment,
            Host = websiteLead?.Host,
            QuoteType = intakeLink?.InterestType ?? intakeLink?.ProductType ?? websiteLead?.InterestType ?? "crm",
            IsBrowserSignal = false,
            IsServerAuthority = true,
            MetaServerAuthorityEligible = metaEligible,
            Metadata = new
            {
                LeadId = websiteLead?.LeadId ?? intakeLink?.WebsiteLeadPublicId,
                AppointmentId = appointment.Id,
                appointment.WorkstationLeadId,
                appointment.OwnerAgentUserId,
                appointment.CalendarEventId,
                appointment.CalendarEventWebLink,
                appointment.ScheduledStartUtc,
                appointment.ScheduledEndUtc,
                appointment.BookingSource,
                appointment.ConfirmationSource,
                AppointmentStatus = appointment.Status.ToString(),
                Oppref = OpenAiClickReference.Normalize(appointment.Oppref ?? intakeLink?.Oppref ?? websiteLead?.Oppref),
                appointment.LastSyncStatus
            }
        };

        var analytics = UnifiedEventMapper.ToAnalytics(context);
        analytics.ClientEventId = clientEventId;
        analytics.EventId = clientEventId;
        analytics.Url = intakeLink?.LandingPageUrl;
        analytics.Path = intakeLink?.PagePath;
        UnifiedAnalyticsWriter.Write(_db, analytics);
    }

    private static Guid StableAppointmentEventId(Guid appointmentId, string eventName)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"appointment:v1|{appointmentId:N}|{eventName}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    public async Task RecordAppointmentCompletedAsync(LeadAppointment appointment, CancellationToken cancellationToken = default)
    {
        if (appointment.Status != LeadAppointmentStatus.Completed)
            return;

        await RecordAppointmentOutcomeAsync(appointment, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordProductionOutcomeAsync(
        Guid productionRecordId,
        string agentUserId,
        ProductionSide side,
        ProductionStatus status,
        string? leadId,
        string? clientUserId,
        decimal amount,
        decimal personalAmount,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        var eventName = status switch
        {
            ProductionStatus.Submitted => "ApplicationSubmitted",
            ProductionStatus.Issued => "PolicyIssued",
            ProductionStatus.Paid => "PolicyPaid",
            _ => null
        };

        if (eventName == null)
            return;

        var contactKey = side == ProductionSide.Lead ? leadId : clientUserId;
        if (string.IsNullOrWhiteSpace(contactKey))
            return;

        if (productionRecordId == Guid.Empty)
            throw new ArgumentException("A canonical production record id is required.", nameof(productionRecordId));

        var dedupKey = $"{eventName}:production:{productionRecordId:N}";
        var clientEventId = StableAppointmentEventId(productionRecordId, eventName);
        var existing = await _db.AnalyticsEvents.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ClientEventId == clientEventId, cancellationToken);
        if (existing is not null)
        {
            return;
        }

        var websiteLeadId = await ResolveProductionWebsiteLeadIdAsync(
            side,
            leadId,
            clientUserId,
            cancellationToken);

        WebsiteLead? productionWebsiteLead = null;
        WebsiteLeadIntakeLink? productionIntake = null;
        if (websiteLeadId.HasValue)
        {
            productionWebsiteLead = await _db.WebsiteLeads.AsNoTracking()
                .FirstOrDefaultAsync(x => x.LeadId == websiteLeadId.Value, cancellationToken);
            productionIntake = await _db.WebsiteLeadIntakeLinks.AsNoTracking()
                .Where(x => x.WebsiteLeadPublicId == websiteLeadId.Value)
                .OrderByDescending(x => x.SubmittedUtc)
                .ThenByDescending(x => x.CapturedUtc)
                .FirstOrDefaultAsync(cancellationToken);
        }
        var productionOppref = OpenAiClickReference.Normalize(productionIntake?.Oppref ?? productionWebsiteLead?.Oppref);
        var productionRecord = await _db.ProductionRecords
            .FirstOrDefaultAsync(x => x.Id == productionRecordId, cancellationToken);
        if (productionRecord is not null && string.IsNullOrWhiteSpace(productionRecord.Oppref) && productionOppref is not null)
            productionRecord.Oppref = productionOppref;

        var trackingProfile = await _db.AgentTrackingProfiles
            .AsNoTracking()
            .Where(x => x.AgentUserId == agentUserId && x.Status == "active")
            .OrderByDescending(x => x.UpdatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        var row = BuildAnalyticsOutcome(
            eventName: eventName,
            eventId: $"{eventName.ToLowerInvariant()}_{productionRecordId:N}",
            dedupKey: dedupKey,
            websiteLeadId: websiteLeadId,
            agentTrackingProfileId: trackingProfile?.Id,
            agentSlug: trackingProfile?.Slug,
            quoteType: "crm",
            funnelStep: status switch
            {
                ProductionStatus.Submitted => 6,
                ProductionStatus.Issued => 7,
                ProductionStatus.Paid => 8,
                _ => 0
            },
            stepName: status switch
            {
                ProductionStatus.Submitted => "application_submitted",
                ProductionStatus.Issued => "policy_issued",
                ProductionStatus.Paid => "policy_paid",
                _ => "production_outcome"
            },
            scoreTier: eventName,
            totalScore: status switch
            {
                ProductionStatus.Submitted => 220,
                ProductionStatus.Issued => 320,
                ProductionStatus.Paid => 500,
                _ => 0
            },
            metadata: new
            {
                productionRecordId,
                agentUserId,
                side = side.ToString(),
                status = status.ToString(),
                workstationLeadId = leadId,
                clientUserId,
                amount,
                personalAmount,
                notes,
                currency = "USD",
                valueCents = decimal.ToInt64(decimal.Round(personalAmount * 100m, 0, MidpointRounding.AwayFromZero)),
                oppref = productionOppref
            },
            lineage: new UnifiedEventContext
            {
                SiteKey = MetaSignalSingleTruthPolicy.ReadString(productionWebsiteLead?.MetadataJson, "SiteKey")
                    ?? (productionWebsiteLead?.CommerceBusinessId.HasValue == true ? "BusinessWebsite" : "ProtectWebsite"),
                CommerceBusinessId = productionWebsiteLead?.CommerceBusinessId ?? productionIntake?.CommerceBusinessId,
                AgentTrackingProfileId = productionWebsiteLead?.AgentTrackingProfileId ?? trackingProfile?.Id,
                AgentSlug = productionWebsiteLead?.AgentSlug ?? trackingProfile?.Slug,
                WebsiteContentVersionId = productionWebsiteLead?.WebsiteContentVersionId,
                WebsiteBindingId = productionWebsiteLead?.WebsiteBindingId,
                EventUtc = productionRecord?.UpdatedUtc ?? DateTime.UtcNow,
                SessionId = productionIntake?.SessionId ?? productionWebsiteLead?.SessionId,
                VisitorId = productionIntake?.VisitorId ?? productionWebsiteLead?.VisitorId,
                PageKey = productionIntake?.SourcePageKey ?? productionWebsiteLead?.SourcePageKey,
                Referrer = productionIntake?.ReferrerUrl,
                UtmSource = productionIntake?.UtmSource ?? productionWebsiteLead?.UtmSource,
                UtmMedium = productionIntake?.UtmMedium ?? productionWebsiteLead?.UtmMedium,
                UtmCampaign = productionIntake?.UtmCampaign ?? productionWebsiteLead?.UtmCampaign,
                UtmId = productionIntake?.UtmId ?? productionWebsiteLead?.UtmId,
                UtmContent = productionIntake?.UtmContent,
                MetaCampaignId = productionIntake?.MetaCampaignId ?? productionWebsiteLead?.MetaCampaignId,
                MetaAdSetId = productionIntake?.MetaAdSetId ?? productionWebsiteLead?.MetaAdSetId,
                MetaAdId = productionIntake?.MetaAdId ?? productionWebsiteLead?.MetaAdId,
                Fbclid = productionIntake?.Fbclid ?? productionWebsiteLead?.Fbclid,
                Fbc = productionIntake?.Fbc ?? productionWebsiteLead?.Fbc,
                Fbp = productionIntake?.Fbp ?? productionWebsiteLead?.Fbp,
                PageVariant = productionIntake?.PageVariant,
                PageMode = productionIntake?.PageMode,
                Environment = productionWebsiteLead?.Environment,
                Host = productionWebsiteLead?.Host,
                Url = productionIntake?.LandingPageUrl ?? (!string.IsNullOrWhiteSpace(productionWebsiteLead?.Host)
                    ? "https://" + productionWebsiteLead.Host + (productionIntake?.PagePath ?? "/") : null),
                UserAgent = productionIntake?.ClientUserAgent ?? productionWebsiteLead?.ClientUserAgent,
                IpAddress = productionIntake?.ClientIpAddress ?? productionWebsiteLead?.ClientIpAddress
            });

        row.ClientEventId = clientEventId;
        row.EventId = clientEventId;
        UnifiedAnalyticsWriter.Write(_db, row);
        try { await _db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            _db.Entry(row).State = EntityState.Detached;
            existing = await _db.AnalyticsEvents.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ClientEventId == clientEventId, cancellationToken);
            if (existing is null) throw;
            return;
        }

        _logger.LogInformation(
            "MetaSignal CRM outcome recorded event={EventName} side={Side} contact={ContactKey} amount={Amount}",
            row.EventType,
            side,
            contactKey,
            amount);
    }

    private async Task<Guid?> ResolveProductionWebsiteLeadIdAsync(
        ProductionSide side,
        string? leadId,
        string? clientUserId,
        CancellationToken cancellationToken)
    {
        if (side == ProductionSide.Lead)
            return await ResolveWebsiteLeadIdAsync(leadId, null, cancellationToken);

        // Converted clients preserve the canonical source lead in CRM metadata.
        // ClientUserId itself is not required to equal the workstation lead id.
        if (!string.IsNullOrWhiteSpace(clientUserId))
        {
            var client = await _db.ClientProfiles
                .AsNoTracking()
                .Where(x => x.ClientUserId == clientUserId)
                .Select(x => new { x.ClientUserId, x.CrmNotes })
                .FirstOrDefaultAsync(cancellationToken);

            if (client is not null)
            {
                var meta = ClientCrmMetaSerializer.Deserialize(client.CrmNotes);
                var sourceLeadId = meta?.SourceWorkstationLeadId;
                var bySourceLead = await ResolveWebsiteLeadIdAsync(sourceLeadId, null, cancellationToken);
                if (bySourceLead.HasValue)
                    return bySourceLead.Value;
            }

            // Preserve compatibility for historical clients whose ClientUserId was
            // itself the workstation lead id.
            var byClientUserId = await ResolveWebsiteLeadIdAsync(clientUserId, null, cancellationToken);
            if (byClientUserId.HasValue)
                return byClientUserId.Value;
        }

        // Defensive fallback for mixed caller paths where leadId may still be populated.
        return await ResolveWebsiteLeadIdAsync(leadId, null, cancellationToken);
    }

    private async Task<Guid?> ResolveWebsiteLeadIdAsync(string? workstationLeadId, Guid? intakeLinkId, CancellationToken cancellationToken)
    {
        if (intakeLinkId.HasValue)
        {
            var byIntake = await _db.WebsiteLeadIntakeLinks
                .AsNoTracking()
                .Where(x => x.Id == intakeLinkId.Value)
                .Select(x => (Guid?)x.WebsiteLeadPublicId)
                .FirstOrDefaultAsync(cancellationToken);

            if (byIntake.HasValue)
                return byIntake.Value;
        }

        if (string.IsNullOrWhiteSpace(workstationLeadId))
            return null;

        return await _db.WebsiteLeadIntakeLinks
            .AsNoTracking()
            .Where(x => x.WorkstationLeadId == workstationLeadId)
            .OrderByDescending(x => x.SubmittedUtc)
            .ThenByDescending(x => x.CapturedUtc)
            .Select(x => (Guid?)x.WebsiteLeadPublicId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static AnalyticsEvent BuildAnalyticsOutcome(
        string eventName,
        string eventId,
        string dedupKey,
        Guid? websiteLeadId,
        Guid? agentTrackingProfileId,
        string? agentSlug,
        string quoteType,
        int funnelStep,
        string stepName,
        string scoreTier,
        int totalScore,
        object metadata, UnifiedEventContext? lineage = null)
    {
        var payload = JsonSerializer.SerializeToNode(metadata)!.AsObject();
        payload["LeadId"] = websiteLeadId?.ToString("D");
        payload["fbc"] = lineage?.Fbc;
        payload["fbp"] = lineage?.Fbp;
        payload["pageVariant"] = lineage?.PageVariant;
        payload["pageMode"] = lineage?.PageMode;
        payload["canonicalOutcomeEventId"] = eventId;
        payload["canonicalDeduplicationKey"] = dedupKey;
        payload["upstreamMetaEventId"] = eventId;
        payload["siteKey"] = lineage?.SiteKey;
        payload["metaDeduplicationKey"] = dedupKey;
        payload["stepNumber"] = funnelStep;
        payload["stepName"] = stepName;
        payload["scoreTier"] = scoreTier;
        payload["intentScore"] = totalScore;
        payload["engagementScore"] = totalScore;
        payload["qualificationScore"] = totalScore;
        payload["frictionScore"] = 0;
        payload["totalSignalScore"] = totalScore;
        var row = UnifiedEventMapper.ToAnalytics((lineage ?? new UnifiedEventContext()) with
        {
            EventId = eventId, EventName = eventName, EventCategory = "conversion",
            EventUtc = lineage?.EventUtc ?? DateTime.UtcNow, QuoteType = quoteType,
            AgentTrackingProfileId = lineage?.CommerceBusinessId.HasValue == true ? null : lineage?.AgentTrackingProfileId ?? agentTrackingProfileId,
            AgentSlug = lineage?.CommerceBusinessId.HasValue == true ? null : lineage?.AgentSlug ?? agentSlug,
            Oppref = OpenAiClickReference.Normalize(payload["oppref"]?.GetValue<string>()),
            Environment = lineage?.Environment ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production",
            Host = lineage?.Host ?? "AgentPortal", IsBrowserSignal = false, IsServerAuthority = true,
            MetaServerAuthorityEligible = true, Metadata = payload
        });
        row.Url = lineage?.Url;
        row.Path = Uri.TryCreate(lineage?.Url, UriKind.Absolute, out var sourceUrl) ? sourceUrl.AbsolutePath : null;
        row.TrackingVersion = "crm-production-authority-v1";
        row.SchemaVersion = 2;
        row.MetadataJson = BuildMetadataJson(eventName, websiteLeadId, payload);
        return row;
    }

    private static string BuildMetadataJson(string eventName, Guid? websiteLeadId, object metadata)
        => MetaSignalSingleTruthPolicy.BuildMetadataJson(
            eventName,
            websiteLeadId,
            sessionId: null,
            payload: metadata,
            isBrowserSignal: false,
            isServerAuthority: true,
            metaServerAuthorityEligible: true,
            metaSingleTruthDispatchEligible: false,
            metaPipelineOrigin: "crm_outcome_service");
}
