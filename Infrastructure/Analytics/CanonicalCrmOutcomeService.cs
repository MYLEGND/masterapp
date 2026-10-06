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

public sealed class CanonicalCrmOutcomeService
{
    private readonly MasterAppDbContext _db;
    private readonly ILogger<CanonicalCrmOutcomeService> _logger;

    public CanonicalCrmOutcomeService(
        MasterAppDbContext db,
        ILogger<CanonicalCrmOutcomeService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>One transaction owns every production mutation and its immutable reporting facts.
    /// Controller authorization remains in the caller; no caller writes production directly.</summary>
    public static async Task SaveProductionChangesAsync(MasterAppDbContext db, CancellationToken ct = default)
    {
        db.ChangeTracker.DetectChanges();
        var changes = db.ChangeTracker.Entries<ProductionRecord>()
            .Where(e => e.State is EntityState.Added or EntityState.Deleted || e.State == EntityState.Modified &&
                (e.Property(x => x.Status).IsModified || e.Property(x => x.Amount).IsModified ||
                 e.Property(x => x.PersonalAmount).IsModified || e.Property(x => x.LeadId).IsModified ||
                 e.Property(x => x.ClientUserId).IsModified))
            .Select(e => (Record: e.Entity, Deleted: e.State == EntityState.Deleted)).ToArray();
        foreach (var change in changes)
            if (!Enum.IsDefined(change.Record.Status) || change.Record.Amount < 0 || change.Record.PersonalAmount < 0)
                throw new ArgumentException("Production status and monetary amounts must be valid.");
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct) : null;
        var authority = new CanonicalCrmOutcomeService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<CanonicalCrmOutcomeService>.Instance);
        // Stage deletion snapshots while the owned record and its lineage still exist.
        foreach (var change in changes.Where(c => c.Deleted))
            await authority.StageProductionSnapshotAsync(change.Record, true, ct);
        await db.SaveChangesAsync(ct);
        foreach (var change in changes.Where(c => !c.Deleted))
        {
            var r = change.Record;
            await authority.RecordProductionOutcomeAsync(r.Id, r.AgentUserId, r.Side, r.Status, r.LeadId,
                r.ClientUserId, r.Amount, r.PersonalAmount, r.Notes, ct);
            await authority.StageProductionSnapshotAsync(r, false, ct);
        }
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
    }


    /// <summary>
    /// Persists CRM lead mutations and their canonical qualification truth in one transaction.
    /// Callers remain responsible for authorization and for staging the intended CRM change;
    /// this is the single persistence boundary for WorkstationLeadProfile stage mutations.
    /// </summary>
    public static async Task SaveLeadChangesAsync(MasterAppDbContext db, CancellationToken ct = default)
    {
        db.ChangeTracker.DetectChanges();
        var changes = db.ChangeTracker.Entries<WorkstationLeadProfile>()
            .Where(e => e.State == EntityState.Added ||
                e.State == EntityState.Modified && e.Property(x => x.CrmStage).IsModified)
            .Select(e => (
                Lead: e.Entity,
                PreviousStage: e.State == EntityState.Added ? null : e.Property(x => x.CrmStage).OriginalValue,
                CurrentStage: e.Entity.CrmStage))
            .Where(x => !string.Equals(x.PreviousStage, x.CurrentStage, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct) : null;

        await db.SaveChangesAsync(ct);

        if (changes.Length != 0)
        {
            var authority = new CanonicalCrmOutcomeService(
                db,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<CanonicalCrmOutcomeService>.Instance);
            foreach (var change in changes)
                await authority.RecordQualificationTransitionAsync(
                    change.Lead,
                    change.PreviousStage,
                    change.CurrentStage,
                    ct);

            await db.SaveChangesAsync(ct);
        }

        if (transaction is not null)
            await transaction.CommitAsync(ct);
    }

    private async Task RecordQualificationTransitionAsync(
        WorkstationLeadProfile lead,
        string? previousStage,
        string? currentStage,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(lead.LeadId) ||
            string.Equals(previousStage, currentStage, StringComparison.OrdinalIgnoreCase))
            return;

        var now = lead.UpdatedUtc == default ? DateTime.UtcNow : lead.UpdatedUtc;
        var identity = QualificationIdentity(lead.LeadId);
        var authorityClientEventId = StableTextEventId($"qualified:v1|{identity}");
        var identityMarker = "\"qualificationIdentity\":\"" + identity + "\"";
        var persistedQualificationFacts = await _db.AnalyticsEvents.AsNoTracking()
            .Where(x =>
                (x.TrackingVersion == "crm-qualification-authority-v1" ||
                 x.TrackingVersion == "crm-qualification-state-v1") &&
                x.MetadataJson != null &&
                x.MetadataJson.Contains(identityMarker))
            .ToListAsync(ct);
        var localQualificationFacts = _db.AnalyticsEvents.Local
            .Where(x =>
                (x.TrackingVersion == "crm-qualification-authority-v1" ||
                 x.TrackingVersion == "crm-qualification-state-v1") &&
                x.MetadataJson != null &&
                x.MetadataJson.Contains(identityMarker))
            .ToArray();
        var qualificationFacts = persistedQualificationFacts
            .Concat(localQualificationFacts)
            .DistinctBy(x => x.ClientEventId)
            .OrderByDescending(x => x.EventUtc)
            .ThenByDescending(x => x.Id)
            .ToArray();
        var existingAuthority = qualificationFacts.Any(x =>
            x.ClientEventId == authorityClientEventId ||
            x.TrackingVersion == "crm-qualification-authority-v1");
        var previousActive = existingAuthority &&
            (CanonicalAdvertisingEventProjection.ReadBoolean(
                qualificationFacts.First().MetadataJson, "qualificationActive") ?? true);

        var desiredActive = IsQualifiedStage(currentStage)
            ? true
            : IsQualificationReversalStage(currentStage)
                ? false
                : previousActive;

        // A downstream stage does not manufacture qualification. Once a real
        // qualification exists, forward progression preserves that historical
        // truth. Only an explicit move back into a pre-qualification stage
        // reconciles it inactive.
        if (!existingAuthority && !desiredActive)
            return;
        if (existingAuthority && desiredActive == previousActive)
            return;

        var active = desiredActive;

        var intake = await _db.WebsiteLeadIntakeLinks.AsNoTracking()
            .Where(x => x.WorkstationLeadId == lead.LeadId)
            .OrderByDescending(x => x.SubmittedUtc)
            .ThenByDescending(x => x.CapturedUtc)
            .FirstOrDefaultAsync(ct);
        WebsiteLead? websiteLead = null;
        if (intake is not null)
            websiteLead = await _db.WebsiteLeads.AsNoTracking()
                .FirstOrDefaultAsync(x => x.LeadId == intake.WebsiteLeadPublicId, ct);

        var trackingProfile = lead.CommerceBusinessId.HasValue
            ? null
            : await _db.AgentTrackingProfiles.AsNoTracking()
                .Where(x => x.AgentUserId == lead.AgentUserId && x.Status == "active")
                .OrderByDescending(x => x.UpdatedUtc)
                .FirstOrDefaultAsync(ct);
        var qualificationPaidRefs = await ResolvePaidClickReferencesAsync(websiteLead, intake, now, ct);

        var lineage = new UnifiedEventContext
        {
            SiteKey = MetaSignalSingleTruthPolicy.ReadString(websiteLead?.MetadataJson, "siteKey")
                ?? (lead.CommerceBusinessId.HasValue ? "BusinessWebsite" : "ProtectWebsite"),
            CommerceBusinessId = lead.CommerceBusinessId ?? websiteLead?.CommerceBusinessId ?? intake?.CommerceBusinessId,
            AgentTrackingProfileId = lead.CommerceBusinessId.HasValue
                ? null
                : websiteLead?.AgentTrackingProfileId ?? trackingProfile?.Id,
            AgentSlug = lead.CommerceBusinessId.HasValue
                ? null
                : websiteLead?.AgentSlug ?? trackingProfile?.Slug,
            WebsiteContentVersionId = websiteLead?.WebsiteContentVersionId,
            WebsiteBindingId = websiteLead?.WebsiteBindingId,
            EventUtc = now,
            SessionId = intake?.SessionId ?? websiteLead?.SessionId,
            VisitorId = intake?.VisitorId ?? websiteLead?.VisitorId,
            PageKey = intake?.SourcePageKey ?? websiteLead?.SourcePageKey,
            Referrer = intake?.ReferrerUrl,
            UtmSource = intake?.UtmSource ?? websiteLead?.UtmSource,
            UtmMedium = intake?.UtmMedium ?? websiteLead?.UtmMedium,
            UtmCampaign = intake?.UtmCampaign ?? websiteLead?.UtmCampaign,
            UtmId = intake?.UtmId ?? websiteLead?.UtmId,
            UtmTerm = intake?.UtmTerm,
            UtmContent = intake?.UtmContent,
            MetaCampaignId = intake?.MetaCampaignId ?? websiteLead?.MetaCampaignId,
            MetaAdSetId = intake?.MetaAdSetId ?? websiteLead?.MetaAdSetId,
            MetaAdId = intake?.MetaAdId ?? websiteLead?.MetaAdId,
            Fbclid = intake?.Fbclid ?? websiteLead?.Fbclid,
            Gclid = qualificationPaidRefs.Gclid,
            Ttclid = qualificationPaidRefs.Ttclid,
            Oppref = OpenAiClickReference.Normalize(intake?.Oppref ?? websiteLead?.Oppref),
            Fbc = intake?.Fbc ?? websiteLead?.Fbc,
            Fbp = intake?.Fbp ?? websiteLead?.Fbp,
            PageVariant = intake?.PageVariant,
            PageMode = intake?.PageMode,
            Environment = websiteLead?.Environment,
            Host = websiteLead?.Host,
            Url = intake?.LandingPageUrl,
            UserAgent = intake?.ClientUserAgent ?? websiteLead?.ClientUserAgent,
            IpAddress = intake?.ClientIpAddress ?? websiteLead?.ClientIpAddress
        };

        var metadata = new
        {
            workstationLeadId = lead.LeadId,
            agentUserId = lead.AgentUserId,
            commerceBusinessId = lead.CommerceBusinessId,
            qualificationIdentity = identity,
            qualificationActive = active,
            previousCrmStage = previousStage,
            currentCrmStage = currentStage
        };

        if (active && !existingAuthority)
        {
            var authority = BuildAnalyticsOutcome(
                eventName: "QualifiedLead",
                eventId: $"qualified_{identity}",
                dedupKey: $"qualifiedlead:workstation:{identity}",
                websiteLeadId: intake?.WebsiteLeadPublicId,
                agentTrackingProfileId: trackingProfile?.Id,
                agentSlug: trackingProfile?.Slug,
                quoteType: intake?.InterestType ?? intake?.ProductType ?? websiteLead?.InterestType ?? "crm",
                funnelStep: 5,
                stepName: "crm_qualified",
                scoreTier: "QualifiedLead",
                totalScore: 180,
                metadata: metadata,
                lineage: lineage);
            authority.ClientEventId = authorityClientEventId;
            authority.EventId = authorityClientEventId;
            authority.TrackingVersion = "crm-qualification-authority-v1";
            UnifiedAnalyticsWriter.Write(_db, authority);
            return;
        }

        var stateEventId = StableTextEventId(
            $"qualified-state:v1|{identity}|{now.Ticks}|{active}|{previousStage}|{currentStage}");
        if (_db.AnalyticsEvents.Local.Any(x => x.ClientEventId == stateEventId) ||
            await _db.AnalyticsEvents.AsNoTracking().AnyAsync(x => x.ClientEventId == stateEventId, ct))
            return;

        var state = BuildAnalyticsOutcome(
            eventName: "QualifiedLead",
            eventId: $"qualified_state_{identity}_{now.Ticks}",
            dedupKey: $"qualifiedlead-state:{identity}:{now.Ticks}",
            websiteLeadId: intake?.WebsiteLeadPublicId,
            agentTrackingProfileId: trackingProfile?.Id,
            agentSlug: trackingProfile?.Slug,
            quoteType: intake?.InterestType ?? intake?.ProductType ?? websiteLead?.InterestType ?? "crm",
            funnelStep: 5,
            stepName: active ? "crm_requalified" : "crm_qualification_reversed",
            scoreTier: active ? "QualifiedLead" : "QualificationReversed",
            totalScore: active ? 180 : 0,
            metadata: metadata,
            lineage: lineage);
        state.ClientEventId = stateEventId;
        state.EventId = stateEventId;
        state.TrackingVersion = "crm-qualification-state-v1";
        var stateMetadata = JsonNode.Parse(state.MetadataJson ?? "{}")!.AsObject();
        stateMetadata["measurementServerAuthorityEligible"] = false;
        stateMetadata["metaServerAuthorityEligible"] = false;
        stateMetadata["metaSingleTruthDispatchEligible"] = false;
        stateMetadata["reportingOnlyReason"] = "qualification_state_reconciliation";
        state.MetadataJson = stateMetadata.ToJsonString();
        UnifiedAnalyticsWriter.Write(_db, state);
    }

    private static bool IsQualifiedStage(string? stage) =>
        string.Equals(stage?.Trim(), "Qualified", StringComparison.OrdinalIgnoreCase);

    private static bool IsQualificationReversalStage(string? stage)
    {
        var value = stage?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Equals("New", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("NewLead", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("Opportunities", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("Contacted", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("CallBack", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("Voicemail", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("NoAnswer", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("AIReception", StringComparison.OrdinalIgnoreCase) ||
               Infrastructure.Leads.WorkstationLeadBuckets.ProductBuckets.Any(bucket =>
                   bucket.Equals(value, StringComparison.OrdinalIgnoreCase));
    }

    private static string QualificationIdentity(string leadId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(leadId.Trim().ToLowerInvariant())))[..24].ToLowerInvariant();

    private static Guid StableTextEventId(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private async Task StageProductionSnapshotAsync(ProductionRecord record, bool deleted, CancellationToken ct)
    {
        var recordKey = record.Id.ToString();
        var exactRecordProperty = "\"productionRecordId\":\"" + recordKey + "\"";
        var prior = await _db.AnalyticsEvents.AsNoTracking()
            .Where(e => (e.TrackingVersion == "crm-production-authority-v1" || e.TrackingVersion == "crm-production-state-v1") && e.MetadataJson != null && e.MetadataJson.Contains(exactRecordProperty))
            .OrderByDescending(e => e.EventUtc).ThenByDescending(e => e.Id).FirstOrDefaultAsync(ct);
        if (prior is null) return; // No fabricated acquisition identity for historical untracked production.
        var eventName = record.Status switch { ProductionStatus.Submitted => "ApplicationSubmitted", ProductionStatus.Issued => "PolicyIssued", _ => "PolicyPaid" };
        if (!deleted && CanonicalAdvertisingEventProjection.ReadBoolean(prior.MetadataJson, "productionDeleted") != true &&
            prior.EventType == eventName && CanonicalMarketingOutcomeProjection.ReadMoney(prior.MetadataJson) == record.PersonalAmount &&
            CanonicalAdvertisingEventProjection.ReadString(prior.MetadataJson, "clientUserId") == record.ClientUserId &&
            CanonicalAdvertisingEventProjection.ReadString(prior.MetadataJson, "workstationLeadId") == record.LeadId)
            return; // The newly written canonical fact already represents the current state.
        var snapshot = UnifiedEventMapper.ToAnalytics(new UnifiedEventContext
        {
            EventName = eventName,
            EventUtc = prior.EventUtc, IsServerAuthority = true,
            AgentTrackingProfileId = prior.AgentTrackingProfileId, AgentSlug = prior.AgentSlug, CommerceBusinessId = prior.CommerceBusinessId,
            WebsiteContentVersionId = prior.WebsiteContentVersionId, WebsiteBindingId = prior.WebsiteBindingId,
            SessionId = prior.SessionId, VisitorId = prior.VisitorId, Host = prior.Host, Environment = prior.Environment, IsInternal = prior.IsInternal,
            Url = prior.Url, PageKey = prior.PageKey, ElementKey = prior.ElementKey, Referrer = prior.Referrer, ReferrerHost = prior.ReferrerHost,
            UtmSource = prior.UtmSource, UtmMedium = prior.UtmMedium, UtmCampaign = prior.UtmCampaign, UtmId = prior.UtmId,
            UtmTerm = prior.UtmTerm, UtmContent = prior.UtmContent, MetaCampaignId = prior.MetaCampaignId, MetaAdSetId = prior.MetaAdSetId,
            MetaAdId = prior.MetaAdId, Oppref = prior.Oppref, Fbclid = prior.Fbclid, UserAgent = prior.UserAgent, IpAddress = prior.IpAddress,
            WebDriver = prior.WebDriver, IsHeadless = prior.IsHeadless, HumanInteractionCount = prior.HumanInteractionCount,
            EngagedMilliseconds = prior.EngagedMilliseconds, DwellMilliseconds = prior.DwellMilliseconds,
            MouseMoveCount = prior.MouseMoveCount, ScrollPercent = prior.ScrollPercent
        });
        snapshot.ClientEventId = snapshot.EventId;
        snapshot.Path = prior.Path;
        snapshot.TrackingVersion = "crm-production-state-v1";
        var metadata = JsonNode.Parse(prior.MetadataJson ?? "{}")!.AsObject();
        // Top-level canonical fields override historical nested values in the read projection.
        metadata["productionRecordId"] = recordKey;
        metadata["productionSnapshot"] = true; metadata["productionDeleted"] = deleted;
        metadata["productionStatus"] = record.Status.ToString();
        metadata["clientUserId"] = record.ClientUserId; metadata["workstationLeadId"] = record.LeadId;
        metadata["valueCents"] = decimal.ToInt64(decimal.Round(record.PersonalAmount * 100m, 0, MidpointRounding.AwayFromZero));
        metadata["currency"] = "USD";
        metadata["canonicalOutcomeEventId"] = "production-state:" + snapshot.EventId.ToString("N");
        metadata["measurementServerAuthorityEligible"] = false;
        metadata["metaServerAuthorityEligible"] = false;
        metadata["reportingOnlyReason"] = "production_state_reconciliation";
        metadata["providerCorrectionStatus"] = "requires_reconciliation_if_previously_delivered";
        snapshot.MetadataJson = metadata.ToJsonString();
        UnifiedAnalyticsWriter.Write(_db, snapshot);
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
        var appointmentEventUtc = appointment.UpdatedUtc == default ? DateTime.UtcNow : appointment.UpdatedUtc;
        var appointmentPaidRefs = await ResolvePaidClickReferencesAsync(
            websiteLead, intakeLink, appointmentEventUtc, cancellationToken);

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
            EventUtc = appointmentEventUtc,
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
            UtmTerm = intakeLink?.UtmTerm,
            UtmContent = intakeLink?.UtmContent,
            MetaCampaignId = intakeLink?.MetaCampaignId ?? websiteLead?.MetaCampaignId,
            MetaAdSetId = intakeLink?.MetaAdSetId ?? websiteLead?.MetaAdSetId,
            MetaAdId = intakeLink?.MetaAdId ?? websiteLead?.MetaAdId,
            Fbclid = intakeLink?.Fbclid ?? websiteLead?.Fbclid,
            Gclid = appointmentPaidRefs.Gclid,
            Ttclid = appointmentPaidRefs.Ttclid,
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

        var productionEventUtc = productionRecord?.UpdatedUtc ?? DateTime.UtcNow;
        var productionPaidRefs = await ResolvePaidClickReferencesAsync(
            productionWebsiteLead, productionIntake, productionEventUtc, cancellationToken);

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
                EventUtc = productionEventUtc,
                SessionId = productionIntake?.SessionId ?? productionWebsiteLead?.SessionId,
                VisitorId = productionIntake?.VisitorId ?? productionWebsiteLead?.VisitorId,
                PageKey = productionIntake?.SourcePageKey ?? productionWebsiteLead?.SourcePageKey,
                Referrer = productionIntake?.ReferrerUrl,
                UtmSource = productionIntake?.UtmSource ?? productionWebsiteLead?.UtmSource,
                UtmMedium = productionIntake?.UtmMedium ?? productionWebsiteLead?.UtmMedium,
                UtmCampaign = productionIntake?.UtmCampaign ?? productionWebsiteLead?.UtmCampaign,
                UtmId = productionIntake?.UtmId ?? productionWebsiteLead?.UtmId,
                UtmTerm = productionIntake?.UtmTerm,
                UtmContent = productionIntake?.UtmContent,
                MetaCampaignId = productionIntake?.MetaCampaignId ?? productionWebsiteLead?.MetaCampaignId,
                MetaAdSetId = productionIntake?.MetaAdSetId ?? productionWebsiteLead?.MetaAdSetId,
                MetaAdId = productionIntake?.MetaAdId ?? productionWebsiteLead?.MetaAdId,
                Fbclid = productionIntake?.Fbclid ?? productionWebsiteLead?.Fbclid,
                Gclid = productionPaidRefs.Gclid,
                Ttclid = productionPaidRefs.Ttclid,
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
            "Canonical CRM outcome recorded event={EventName} side={Side} contact={ContactKey} amount={Amount}",
            row.EventType,
            side,
            contactKey,
            amount);
    }

    private async Task<(string? Gclid, string? Ttclid)> ResolvePaidClickReferencesAsync(
        WebsiteLead? websiteLead,
        WebsiteLeadIntakeLink? intake,
        DateTime eventUtc,
        CancellationToken ct)
    {
        var gclid = PaidAdsClickReference.NormalizeGoogle(
            CanonicalAdvertisingEventProjection.ReadString(websiteLead?.MetadataJson, "Gclid") ??
            CanonicalAdvertisingEventProjection.ReadString(intake?.SnapshotJson, "Gclid"));
        var ttclid = PaidAdsClickReference.NormalizeTikTok(
            CanonicalAdvertisingEventProjection.ReadString(websiteLead?.MetadataJson, "Ttclid") ??
            CanonicalAdvertisingEventProjection.ReadString(intake?.SnapshotJson, "Ttclid"));
        if (gclid is not null && ttclid is not null)
            return (gclid, ttclid);

        var sessionId = intake?.SessionId ?? websiteLead?.SessionId;
        var visitorId = intake?.VisitorId ?? websiteLead?.VisitorId;
        if (string.IsNullOrWhiteSpace(sessionId) && string.IsNullOrWhiteSpace(visitorId))
            return (gclid, ttclid);

        var commerceBusinessId = websiteLead?.CommerceBusinessId ?? intake?.CommerceBusinessId;
        var agentTrackingProfileId = commerceBusinessId.HasValue ? null : websiteLead?.AgentTrackingProfileId;
        var from = (websiteLead?.CreatedUtc ?? intake?.SubmittedUtc ?? eventUtc).AddDays(-1);
        var to = eventUtc.AddDays(1);

        var query = _db.AnalyticsEvents.AsNoTracking()
            .Where(x => x.EventUtc >= from && x.EventUtc <= to &&
                        x.CommerceBusinessId == commerceBusinessId);
        if (commerceBusinessId.HasValue)
            query = query.Where(x => x.AgentTrackingProfileId == null);
        else if (agentTrackingProfileId.HasValue)
            query = query.Where(x => x.AgentTrackingProfileId == agentTrackingProfileId);
        if (!string.IsNullOrWhiteSpace(sessionId))
            query = query.Where(x => x.SessionId == sessionId);
        else
            query = query.Where(x => x.VisitorId == visitorId);

        var metadataRows = await query
            .OrderBy(x => x.EventUtc)
            .ThenBy(x => x.Id)
            .Select(x => x.MetadataJson)
            .Take(250)
            .ToListAsync(ct);

        foreach (var metadataJson in metadataRows)
        {
            gclid ??= PaidAdsClickReference.NormalizeGoogle(
                CanonicalAdvertisingEventProjection.ReadString(metadataJson, "gclid"));
            ttclid ??= PaidAdsClickReference.NormalizeTikTok(
                CanonicalAdvertisingEventProjection.ReadString(metadataJson, "ttclid"));
            if (gclid is not null && ttclid is not null)
                break;
        }

        return (gclid, ttclid);
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
        payload["oppref"] = OpenAiClickReference.Normalize(
            payload["oppref"]?.GetValue<string>() ?? lineage?.Oppref);
        payload["gclid"] = PaidAdsClickReference.NormalizeGoogle(lineage?.Gclid);
        payload["ttclid"] = PaidAdsClickReference.NormalizeTikTok(lineage?.Ttclid);
        payload["fbc"] = lineage?.Fbc;
        payload["fbp"] = lineage?.Fbp;
        payload["gclid"] = PaidAdsClickReference.NormalizeGoogle(lineage?.Gclid);
        payload["ttclid"] = PaidAdsClickReference.NormalizeTikTok(lineage?.Ttclid);
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
            Oppref = OpenAiClickReference.Normalize(payload["oppref"]?.GetValue<string>() ?? lineage?.Oppref),
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
