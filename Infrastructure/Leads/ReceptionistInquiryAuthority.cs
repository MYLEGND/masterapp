using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Reception;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Shared.Analytics;

namespace Infrastructure.Leads;

/// <summary>
/// Confirmed voice adapter into the existing inquiry, CRM intake and notification owners.
/// It neither authenticates a customer account nor sends messages, claims deduplication or invents browser facts.
/// </summary>
internal sealed partial class ReceptionistInquiryAuthority(
    DbContextOptions<MasterAppDbContext> options,
    ReceptionistAdmissionService admission,
    IConfiguration configuration,
    ILogger<WebsiteLifeLeadCaptureService> captureLogger,
    TimeProvider clock)
{
    internal const string RouteKey = "leads.confirmed_inquiry.v1";
    internal const string InterestType = "VoiceInquiry";
    private const string Channel = "inbound_voice";
    private const string Genuine = "observed_genuine_inquiry_pending_reconciliation";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static string CanonicalArgumentsSha256(ConfirmedReceptionistInquiry facts) =>
        Hash(JsonSerializer.Serialize(ValidateFacts(facts), Json));

    internal static ReceptionistInquiryIntakeRules ReadIntakeRules(ReceptionistCallContext call)
    {
        if (!call.Configuration.RequestedCapabilities.Contains(ReceptionistCapabilities.CanonicalIntake, StringComparer.Ordinal) ||
            !call.Binding.Capabilities.Any(x => x.Key == ReceptionistCapabilities.CanonicalIntake &&
                x.State == ReceptionistCapabilityStates.Accepted && x.RouteKey == RouteKey))
            throw new InvalidOperationException("The pinned owner has not accepted this canonical inquiry adapter.");
        using var document = JsonDocument.Parse(call.Configuration.IntakeRulesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("canonicalInquiry", out var value))
            throw new InvalidOperationException("The pinned owner has no accepted canonical inquiry route map.");
        var rules = value.Deserialize<ReceptionistInquiryIntakeRules>(Json)
            ?? throw new InvalidOperationException("The canonical inquiry route map is absent.");
        if (rules.Services is null || rules.Services.Count is < 1 or > 100 || rules.Services.Any(x => x is null) ||
            rules.Services.Select(x => x.ServiceKey).Distinct(StringComparer.Ordinal).Count() != rules.Services.Count)
            throw new ArgumentException("Canonical inquiry service routes must be explicit and unique.");
        foreach (var route in rules.Services)
        {
            ReceptionistConfigurationCodec.Identifier(route.ServiceKey, 80, "inquiry service key");
            if (!WorkstationLeadBuckets.ProductBuckets.Contains(route.CrmBucket, StringComparer.Ordinal))
                throw new ArgumentException("The pinned inquiry route must select a supported canonical CRM bucket.");
        }
        return rules;
    }

    internal async Task<ReceptionistInquiryPersistence> PersistConfirmedAsync(
        ReceptionistCallContext call,
        Guid actionId,
        ConfirmedReceptionistInquiry facts,
        string confirmationReceipt,
        CancellationToken ct = default)
    {
        facts = ValidateFacts(facts);
        ReceptionistConfigurationCodec.Required(confirmationReceipt, 512, nameof(confirmationReceipt));
        if (actionId == Guid.Empty) throw new ArgumentException("A claimed stable action is required.");
        // Re-resolve the trusted owner and pinned configuration; no model/caller can supply authority overrides.
        var current = await admission.GetCallContextAsync(call.CallId, ct)
            ?? throw new InvalidOperationException("The claimed call does not exist.");
        RequireSameCall(call, current);
        var rules = ReadIntakeRules(current);
        var factsHash = CanonicalArgumentsSha256(facts);
        await using var db = new MasterAppDbContext(options);
        if (!db.Database.IsRelational())
            throw new InvalidOperationException("Confirmed inquiry persistence requires the canonical relational transaction and unique submission constraint.");
        var action = await db.Set<ReceptionistActionIntent>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == actionId && x.CallId == current.CallId, ct);
        if (action is null || action.CapabilityKey != ReceptionistCapabilities.CanonicalIntake ||
            action.RouteKey != RouteKey || action.ArgumentsSha256 != factsHash ||
            action.ConfirmationReceipt != confirmationReceipt || !HasClaimEvidence(action))
            throw new InvalidOperationException("Inquiry facts do not match the caller-confirmed, owner-bound claimed action.");

        var identity = Identity(current, actionId);
        var lead = new WebsiteLead
        {
            AgentTrackingProfileId = current.Owner.AgentTrackingProfileId,
            CommerceBusinessId = current.Owner.CommerceBusinessId
        };
        lead.LeadId = WebsiteLeadSubmission.ResolveConfirmedId(lead, identity);
        var prior = await ScopedLeads(db, current.Owner).SingleOrDefaultAsync(x => x.LeadId == lead.LeadId, ct);
        if (prior is not null)
        {
            RequirePersistedFacts(prior, current, action);
            return new("canonical_intake_saved", prior.LeadId, Reference(prior.LeadId), false, null, Counting(facts));
        }
        // An action already marked uncertain is read back separately; it is never blindly executed again.
        // A pending action claimed by the fenced sideband owner may finish after that connection is interrupted.
        if (action.State != "pending")
            throw new InvalidOperationException("Only a pending claimed action may create an inquiry; reconcile other action states through readback.");
        if (current.ControlledTest || facts.AcquisitionClass != Genuine)
            return new("not_attempted", null, null, false,
                current.ControlledTest ? "qa_test" : facts.AcquisitionClass,
                new(false, false, "not_required_excluded", 0, 0));

        var route = rules.Services.SingleOrDefault(x => x.ServiceKey == facts.ServiceKey)
            ?? throw new InvalidOperationException("The confirmed service has no route in this owner's pinned inquiry configuration.");
        var recipients = new WebsiteIntakeRecipientResolver(db, configuration);
        var actor = await recipients.ResolveCrmOwnerAsync(current.Owner, ct);
        if (actor is null) throw new InvalidOperationException("The inquiry's canonical CRM owner is unresolved.");
        if (rules.NotifyOwner && string.IsNullOrWhiteSpace(await recipients.ResolveAsync(current.Owner, ct)))
            throw new InvalidOperationException("The approved owner notification has no current scoped recipient.");

        var envelope = new InquiryEnvelope(1, current.Owner.Key, current.CallId, actionId,
            current.Provider, current.ProviderProjectKey, current.ProviderSessionId, current.ConfigurationId,
            factsHash, confirmationReceipt, route.CrmBucket, actor, rules.NotifyOwner, facts, Counting(facts));
        // The canonical display name can retain the caller's unsplit name; provenance
        // records CallerName separately and never asserts an inferred family name.
        lead.FirstName = facts.FirstName ?? facts.CallerName ?? string.Empty;
        lead.LastName = facts.LastName;
        lead.Email = facts.Email ?? string.Empty;
        lead.Phone = facts.Phone;
        lead.PreferredContactMethod = facts.RequestedContactMethod;
        lead.InterestType = InterestType;
        lead.Notes = facts.RequestSummary;
        lead.CreatedUtc = clock.GetUtcNow().UtcDateTime;
        // These booleans retain their existing specific web/marketing meanings. Inbound contact is not consent.
        lead.MarketingEmailConsent = false;
        lead.CallTextConsent = false;
        lead.TermsAccepted = false;
        lead.MetadataJson = JsonSerializer.Serialize(new
        {
            Channel,
            ReportingOwner = current.Owner.OwnerType,
            ProductType = route.CrmBucket,
            facts.BusinessName,
            facts.RequestSummary,
            facts.ContactStatus,
            facts.ContactPurpose,
            facts.RequestedNextAction,
            facts.Timing,
            AcquisitionOrigin = "unknown",
            DeduplicationState = "pending",
            ReceptionistInquiry = envelope
        }, Json);

        var created = await WebsiteLeadSubmission.TryCreateConfirmedAsync(db, lead, identity, ct, async token =>
        {
            var captured = await new WebsiteLifeLeadCaptureService(db, captureLogger).UpsertAsync(new()
            {
                WebsiteLeadId = lead.LeadId,
                SubmittedUtc = lead.CreatedUtc,
                ProductType = route.CrmBucket,
                ConfirmedInquiry = new(current.Owner, route.CrmBucket, actor)
            }, token);
            if (!captured.Captured || string.IsNullOrWhiteSpace(captured.WorkstationLeadId))
                throw new InvalidOperationException("The inquiry CRM handoff did not persist.");
        });
        if (!created)
        {
            prior = await ScopedLeads(db, current.Owner).SingleAsync(x => x.LeadId == lead.LeadId, ct);
            RequirePersistedFacts(prior, current, action);
        }
        return new("canonical_intake_saved", lead.LeadId, Reference(lead.LeadId), created, null, envelope.Counting);
    }

    internal async Task<ReceptionistInquiryReadback?> ReadBackAsync(
        MarketingOwnerScope owner, Guid callId, Guid actionId, CancellationToken ct = default)
    {
        if (owner is null || callId == Guid.Empty || actionId == Guid.Empty) return null;
        var call = await admission.GetCallContextAsync(callId, ct);
        if (call is null || call.Owner != owner || call.ControlledTest) return null;
        // A new context and AsNoTracking queries prove what committed independently of the writer's tracked objects.
        await using var read = new MasterAppDbContext(options);
        var action = await read.Set<ReceptionistActionIntent>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == actionId && x.CallId == callId, ct);
        if (action is null || action.CapabilityKey != ReceptionistCapabilities.CanonicalIntake || action.RouteKey != RouteKey ||
            !HasClaimEvidence(action))
            return null;
        var identity = Identity(call, actionId);
        var id = WebsiteLeadSubmission.ResolveConfirmedId(new()
        {
            AgentTrackingProfileId = owner.AgentTrackingProfileId,
            CommerceBusinessId = owner.CommerceBusinessId
        }, identity);
        var lead = await ScopedLeads(read, owner).SingleOrDefaultAsync(x => x.LeadId == id, ct);
        if (lead is null) return null;
        var envelope = RequirePersistedFacts(lead, call, action);
        var link = await read.WebsiteLeadIntakeLinks.AsNoTracking().SingleOrDefaultAsync(x =>
            x.WebsiteLeadRowId == lead.Id && x.WebsiteLeadPublicId == lead.LeadId &&
            x.CommerceBusinessId == owner.CommerceBusinessId && x.AgentUserId == envelope.AgentUserId, ct);
        if (link is null || link.WorkstationLeadId != lead.LeadId.ToString("N") ||
            link.Bucket != envelope.CrmBucket || link.ProductType != envelope.CrmBucket || link.SnapshotJson != lead.MetadataJson)
            throw new InvalidOperationException("The committed inquiry intake-link readback is incomplete or inconsistent.");
        var crm = await read.WorkstationLeadProfiles.AsNoTracking().SingleOrDefaultAsync(x =>
            x.LeadId == link.WorkstationLeadId && x.CommerceBusinessId == owner.CommerceBusinessId &&
            x.AgentUserId == envelope.AgentUserId, ct);
        if (crm is null || crm.FirstName != lead.FirstName || crm.LastName != (lead.LastName ?? "") ||
            crm.Email != lead.Email || crm.Phone != WebsiteLifeLeadCaptureService.NormalizePhoneKey(lead.Phone) ||
            crm.OriginalLeadType != envelope.CrmBucket || crm.Bucket != envelope.CrmBucket)
            throw new InvalidOperationException("The committed inquiry CRM contact, product or owner readback is incomplete or inconsistent.");
        var reference = Reference(lead.LeadId);
        var receipt = "inquiry-readback:v1:" + Hash(JsonSerializer.Serialize(new
        {
            reference, owner = owner.Key, callId, actionId, envelope.FactsSha256,
            intake = link.Id, crm = crm.LeadId, lead.CreatedUtc,
            observedUtc = clock.GetUtcNow().UtcDateTime
        }, Json));
        var delivery = lead.NotificationSentUtc.HasValue ? "email_transport_accepted" :
            !envelope.NotifyOwner ? "owner_notification_disabled" :
            lead.NotificationAttemptUtc.HasValue ? "owner_email_retry_pending" : "owner_notification_pending";
        return new(lead.LeadId, reference, link.Id, crm.LeadId, owner.Key, callId, actionId,
            envelope.Facts, envelope.FactsSha256, receipt, "crm_readback_verified", delivery, envelope.Counting);
    }

    internal static bool OwnerNotificationsEnabled(WebsiteLead lead)
    {
        if (lead.InterestType != InterestType) return true;
        try
        {
            var envelope = ReadEnvelope(lead);
            return envelope.Version == 1 && envelope.NotifyOwner && envelope.Facts.AcquisitionClass == Genuine &&
                envelope.FactsSha256 == CanonicalArgumentsSha256(envelope.Facts);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static IQueryable<WebsiteLead> ScopedLeads(MasterAppDbContext db, MarketingOwnerScope owner) =>
        db.WebsiteLeads.AsNoTracking().Where(x => !x.IsDeleted && x.InterestType == InterestType &&
            x.AgentTrackingProfileId == owner.AgentTrackingProfileId && x.CommerceBusinessId == owner.CommerceBusinessId);

    private static ConfirmedInquirySubmissionIdentity Identity(ReceptionistCallContext call, Guid actionId) =>
        new(call.Owner, Channel, call.Provider, call.ProviderProjectKey, call.ProviderSessionId, call.CallId, actionId);

    private static void RequireSameCall(ReceptionistCallContext supplied, ReceptionistCallContext actual)
    {
        if (supplied.Owner != actual.Owner || supplied.Provider != actual.Provider ||
            supplied.ProviderProjectKey != actual.ProviderProjectKey || supplied.ProviderSessionId != actual.ProviderSessionId ||
            supplied.ConfigurationId != actual.ConfigurationId || supplied.ControlledTest != actual.ControlledTest)
            throw new InvalidOperationException("The inquiry context does not match the trusted provider call binding.");
    }

    private static bool HasClaimEvidence(ReceptionistActionIntent action) =>
        action.ExecutionLeaseToken is { } token && token != Guid.Empty && action.ExecutionFence > 0 &&
        !string.IsNullOrWhiteSpace(action.WorkerId) && action.AttemptId != Guid.Empty && action.AttemptNumber > 0;

    private static InquiryEnvelope RequirePersistedFacts(WebsiteLead lead, ReceptionistCallContext call, ReceptionistActionIntent action)
    {
        var value = ReadEnvelope(lead);
        var rules = ReadIntakeRules(call);
        var route = rules.Services.SingleOrDefault(x => x.ServiceKey == value.Facts.ServiceKey);
        if (value.Version != 1 || value.OwnerKey != call.Owner.Key || value.CallId != call.CallId || value.ActionId != action.Id ||
            value.Provider != call.Provider || value.ProviderProjectKey != call.ProviderProjectKey ||
            value.ProviderSessionId != call.ProviderSessionId || value.ConfigurationId != call.ConfigurationId ||
            value.FactsSha256 != action.ArgumentsSha256 || value.ConfirmationReceipt != action.ConfirmationReceipt ||
            value.FactsSha256 != CanonicalArgumentsSha256(value.Facts) || value.Facts.AcquisitionClass != Genuine ||
            route is null || route.CrmBucket != value.CrmBucket || rules.NotifyOwner != value.NotifyOwner ||
            (!call.Owner.CommerceBusinessId.HasValue && string.IsNullOrWhiteSpace(value.AgentUserId)) ||
            (call.Owner.CommerceBusinessId.HasValue && value.AgentUserId != string.Empty) ||
            value.Counting != Counting(value.Facts) ||
            lead.FirstName != (value.Facts.FirstName ?? value.Facts.CallerName ?? "") || (lead.LastName ?? "") != (value.Facts.LastName ?? "") ||
            lead.Email != (value.Facts.Email ?? "") || lead.Phone != value.Facts.Phone || lead.Notes != value.Facts.RequestSummary ||
            lead.PreferredContactMethod != value.Facts.RequestedContactMethod || lead.IsInternal ||
            lead.MarketingEmailConsent || lead.CallTextConsent || lead.TermsAccepted ||
            lead.SourcePageKey is not null || lead.SourceCtaKey is not null || lead.Host is not null ||
            lead.WebsiteContentVersionId is not null || lead.WebsiteBindingId is not null)
            throw new InvalidOperationException("The persisted inquiry does not match its confirmed owner-bound action.");
        return value;
    }

    private static InquiryEnvelope ReadEnvelope(WebsiteLead lead)
    {
        if (lead.MetadataJson is null) throw new InvalidOperationException("The inquiry provenance is absent.");
        using var document = JsonDocument.Parse(lead.MetadataJson);
        if (!document.RootElement.TryGetProperty("receptionistInquiry", out var value))
            throw new InvalidOperationException("The inquiry provenance is absent.");
        var envelope = value.Deserialize<InquiryEnvelope>(Json);
        if (envelope?.Facts is null || envelope.Counting is null)
            throw new InvalidOperationException("The inquiry provenance is absent or incomplete.");
        return envelope;
    }

    private static ConfirmedReceptionistInquiry ValidateFacts(ConfirmedReceptionistInquiry facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.AcquisitionClass is not (Genuine or "existing_opportunity_followup" or "existing_customer_service" or
            "engagement_only" or "qa_test" or "spam_or_vendor" or "wrong_number" or "unknown"))
            throw new ArgumentException("A bounded observed acquisition class is required; new-person and organic claims are not caller inputs.");
        facts = facts with
        {
            ServiceKey = Clean(facts.ServiceKey, 80), RequestSummary = Clean(facts.RequestSummary, 1500, true),
            FirstName = Clean(facts.FirstName, 120), LastName = Clean(facts.LastName, 120), CallerName = Clean(facts.CallerName, 120),
            BusinessName = Clean(facts.BusinessName, 240), Email = Clean(facts.Email, 320), Phone = Clean(facts.Phone, 80),
            ContactPurpose = Clean(facts.ContactPurpose, 300), SourceStatement = Clean(facts.SourceStatement, 300),
            RequestedNextAction = Clean(facts.RequestedNextAction, 400), Timing = Clean(facts.Timing, 240)
        };
        if (facts.AcquisitionClass == Genuine && (facts.ServiceKey is null || facts.RequestSummary is null))
            throw new ArgumentException("A genuine inquiry requires an identifiable confirmed service and stated need.");
        if (facts.CallerName is not null && (facts.FirstName is not null || facts.LastName is not null))
            throw new ArgumentException("Use the caller's stated name or their explicit name parts, never an inferred combination.");
        if (facts.ContactStatus is not ("not_requested" or "declined" or "caller_confirmed_for_requested_purpose" or "uncertain") ||
            facts.RequestedContactMethod is not ("none" or "inbound_voice_only" or "callback_requested" or "email_requested" or "sms_requested" or "unknown") ||
            facts.ContactPermissionState is not ("not_requested" or "declined" or "caller_requested_purpose_only" or "uncertain"))
            throw new ArgumentException("Contact verification and requested purpose must remain explicit and bounded.");
        if (facts.Email is not null && !new EmailAddressAttribute().IsValid(facts.Email))
            throw new ArgumentException("The caller's email requires correction and confirmation.");
        if (facts.Phone is not null && (facts.Phone.Count(char.IsAsciiDigit) is < 7 or > 15 ||
            facts.Phone.Any(ch => !(char.IsAsciiDigit(ch) || ch is '+' or '(' or ')' or '-' or '.' or ' '))))
            throw new ArgumentException("The caller's phone requires correction and confirmation.");
        if ((facts.Email is not null || facts.Phone is not null) && facts.ContactStatus != "caller_confirmed_for_requested_purpose")
            throw new ArgumentException("Contact details cannot be committed before caller confirmation.");
        if (facts.ContactPermissionState == "caller_requested_purpose_only" &&
            (facts.ContactPurpose is null || facts.ContactStatus != "caller_confirmed_for_requested_purpose" ||
             (facts.RequestedContactMethod == "email_requested" ? facts.Email is null :
              facts.RequestedContactMethod is "callback_requested" or "sms_requested" ? facts.Phone is null : true)))
            throw new ArgumentException("A requested follow-up needs the caller-confirmed route and specific purpose; it does not enable a sending capability.");
        return facts;
    }

    private static ReceptionistInquiryCounting Counting(ConfirmedReceptionistInquiry facts) => new(
        true,
        facts.ContactStatus == "caller_confirmed_for_requested_purpose" && facts.ContactPermissionState == "caller_requested_purpose_only",
        "pending", null, null);

    private static string? Clean(string? value, int max, bool allowLines = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        var forbiddenControl = value.Any(ch => char.IsControl(ch) && (!allowLines || (ch != '\r' && ch != '\n')));
        if (value.Length > max || forbiddenControl)
            throw new ArgumentException("Confirmed inquiry facts exceed the permitted bounded minimal data.");
        return value;
    }

    private static string Reference(Guid id) => "website-lead:" + id.ToString("D");
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record InquiryEnvelope(int Version, string OwnerKey, Guid CallId, Guid ActionId,
        string Provider, string ProviderProjectKey, string ProviderSessionId, Guid ConfigurationId,
        string FactsSha256, string ConfirmationReceipt, string CrmBucket, string AgentUserId, bool NotifyOwner,
        ConfirmedReceptionistInquiry Facts, ReceptionistInquiryCounting Counting);
}
