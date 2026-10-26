using Shared.Analytics;

namespace Infrastructure.Leads;

/// <summary>Bounded caller-confirmed observations, never an owner, route, consent grant or account identity.</summary>
internal sealed record ConfirmedReceptionistInquiry
{
    public string AcquisitionClass { get; init; } = "unknown";
    public string? ServiceKey { get; init; }
    public string? RequestSummary { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    // Preserve an explicitly stated full name without guessing given/family-name boundaries.
    // Omit when absent so historical confirmed-facts hashes remain unchanged.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? CallerName { get; init; }
    public string? BusinessName { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string ContactStatus { get; init; } = "not_requested";
    public string RequestedContactMethod { get; init; } = "inbound_voice_only";
    public string? ContactPurpose { get; init; }
    public string ContactPermissionState { get; init; } = "not_requested";
    public string? SourceStatement { get; init; }
    public string? RequestedNextAction { get; init; }
    public string? Timing { get; init; }
}

/// <summary>Read only from the pinned owner's private IntakeRulesJson, never from caller/tool arguments.</summary>
internal sealed record ReceptionistInquiryServiceRoute(string ServiceKey, string CrmBucket);

internal sealed record ReceptionistInquiryIntakeRules
{
    public IReadOnlyList<ReceptionistInquiryServiceRoute> Services { get; init; } = [];
    public bool NotifyOwner { get; init; }
}

internal sealed record ReceptionistInquiryCounting(
    bool GenuineServiceInterestObserved,
    bool Contactable,
    string DeduplicationState,
    int? NewGenuineInquiryCount,
    int? NewVerifiedOrganicInquiryCount);

/// <summary>A commit receipt is not a readback or delivery receipt.</summary>
internal sealed record ReceptionistInquiryPersistence(
    string State,
    Guid? InquiryId,
    string? RecordReference,
    bool Created,
    string? ExclusionReason,
    ReceptionistInquiryCounting Counting);

/// <summary>Returned only after a fresh database read of the owner's inquiry and canonical CRM intake link.</summary>
internal sealed record ReceptionistInquiryReadback(
    Guid InquiryId,
    string RecordReference,
    Guid IntakeLinkId,
    string CrmRecordReference,
    string OwnerKey,
    Guid CallId,
    Guid ActionId,
    ConfirmedReceptionistInquiry ConfirmedFacts,
    string FactsSha256,
    string VerificationReceipt,
    string State,
    string DeliveryState,
    ReceptionistInquiryCounting Counting);

/// <summary>Channel-neutral identity used by the existing WebsiteLead submission authority.</summary>
internal sealed record ConfirmedInquirySubmissionIdentity(
    MarketingOwnerScope Owner,
    string Channel,
    string Provider,
    string ProviderProjectKey,
    string ProviderSessionId,
    Guid CallId,
    Guid SubmissionToken);

internal sealed record ConfirmedInquiryCapturePolicy(MarketingOwnerScope Owner, string CrmBucket, string AgentUserId);
