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

        var metaEligible = appointment.Status is LeadAppointmentStatus.Booked
            or LeadAppointmentStatus.Confirmed
            or LeadAppointmentStatus.Completed;

        var context = new UnifiedEventContext
        {
            EventId = $"appointment:{appointment.Id:N}:{eventName}",
            EventName = eventName,
            EventCategory = "appointment",
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
                appointment.LastSyncStatus
            }
        };

        var analytics = UnifiedEventMapper.ToAnalytics(context);
        analytics.ClientEventId = clientEventId;
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
        if (await AlreadyRecordedAsync(eventName, dedupKey, cancellationToken))
            return;

        var websiteLeadId = await ResolveProductionWebsiteLeadIdAsync(
            side,
            leadId,
            clientUserId,
            cancellationToken);

        var trackingProfile = await _db.AgentTrackingProfiles
            .AsNoTracking()
            .Where(x => x.AgentUserId == agentUserId && x.Status == "active")
            .OrderByDescending(x => x.UpdatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        var row = BuildRow(
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
                leadId,
                clientUserId,
                amount,
                personalAmount,
                notes
            });

        UnifiedMetaSignalWriter.Write(_db, row);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "MetaSignal CRM outcome recorded event={EventName} side={Side} contact={ContactKey} amount={Amount}",
            row.EventName,
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

    private async Task<bool> AlreadyRecordedAsync(string eventName, string dedupKey, CancellationToken cancellationToken)
        => await _db.MetaSignalEvents
            .AsNoTracking()
            .AnyAsync(x => x.EventName == eventName && x.MetaDeduplicationKey == dedupKey, cancellationToken);

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

    private static MetaSignalEvent BuildRow(
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
        object metadata)
        => UnifiedMetaSignalWriter.Create(new UnifiedEventContext
        {
            EventId = eventId,
            EventName = eventName,
            EventCategory = "conversion",
            EventUtc = DateTime.UtcNow,
            QuoteType = quoteType,
            AgentTrackingProfileId = agentTrackingProfileId,
            AgentSlug = agentSlug,
            Environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production",
            Host = "AgentPortal",
            IsBrowserSignal = false,
            IsServerAuthority = true,
            MetaServerAuthorityEligible = true,
            Metadata = metadata
        }, row =>
        {
            row.LeadId = websiteLeadId;
            row.TrafficType = "crm";
            row.FunnelStep = funnelStep;
            row.StepName = stepName;
            row.IntentScore = totalScore;
            row.EngagementScore = totalScore;
            row.QualificationScore = totalScore;
            row.FrictionScore = 0;
            row.TotalSignalScore = totalScore;
            row.ScoreTier = scoreTier;
            row.MetaBrowserSent = false;
            row.MetaServerSent = false;
            row.MetaDeduplicationKey = dedupKey;
            row.MetadataJson = BuildMetadataJson(eventName, websiteLeadId, metadata);
        });

    private static string BuildMetadataJson(string eventName, Guid? websiteLeadId, object metadata)
        => MetaSignalSingleTruthPolicy.BuildMetadataJson(
            eventName,
            websiteLeadId,
            sessionId: null,
            payload: metadata,
            isBrowserSignal: false,
            isServerAuthority: true,
            metaServerAuthorityEligible: true,
            metaSingleTruthDispatchEligible: true,
            metaPipelineOrigin: "crm_outcome_service");
}
