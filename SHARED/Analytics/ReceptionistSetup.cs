namespace Shared.Analytics;

public sealed record ReceptionistSetupInput
{
    public Guid ExpectedRevision { get; init; }
    public string BusinessName { get; init; } = string.Empty;
    public string ConversationInstructions { get; init; } = string.Empty;
    public string BusinessPolicy { get; init; } = string.Empty;
    public string? CompactVoicePolicy { get; init; }
    public string? ContactAndRetentionNotes { get; init; }
    public string TimeZoneId { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public decimal MonthlyCashLimit { get; init; }
    public decimal MonthlyConsumptionLimit { get; init; }
    public decimal MonthlyVoiceMinutesLimit { get; init; }
    public int MaximumConcurrentCalls { get; init; }
    public int MaximumCallSeconds { get; init; }
    public int AdmissionWindowSeconds { get; init; }
    public int MaximumCallsPerWindow { get; init; }
    public bool RequestIntake { get; init; }
    public bool NotifyOwner { get; init; }
    public bool BookingFieldsPresent { get; init; }
    public bool BookingEnabled { get; init; }
    public bool BookingQaOnly { get; init; } = true;
    public bool BookingAllowConsentedCustomerEmail { get; init; }
    public string? BookingCalendarConfigurationSha256 { get; init; }
    public string? BookingCalendarTargetSha256 { get; init; }
    public string? BookingPrivacyAcceptanceReference { get; init; }
    public Guid? BookingAgentProfileId { get; init; }
    public int? BookingMinimumNoticeMinutes { get; init; }
    public int? BookingMaximumAdvanceDays { get; init; }
    public List<ReceptionistHoursInput> Hours { get; init; } = [];
    public List<ReceptionistServiceInput> Services { get; init; } = [];
    public List<ReceptionistAnswerInput> Answers { get; init; } = [];
}

public sealed record ReceptionistHoursInput
{
    public DayOfWeek Day { get; init; }
    public bool Enabled { get; init; }
    public TimeOnly? Start { get; init; }
    public TimeOnly? End { get; init; }
}

// New rows and optional fields bind empty form values as null. Canonical setup
// validation runs after blank rows are removed and generated identities assigned.
public sealed record ReceptionistServiceInput
{
    public bool Remove { get; init; }
    public string? ServiceKey { get; init; }
    public string? DisplayName { get; init; }
    public string? Aliases { get; init; }
    public string? DiscoveryQuestion { get; init; }
    public string? PublicSummary { get; init; }
    public string? CrmBucket { get; init; }
    public string? BookingServiceId { get; init; }
    public string? BookingTimeZoneId { get; init; }
    public int? BookingDurationMinutes { get; init; }
}

public sealed record ReceptionistAnswerInput
{
    public bool Remove { get; init; }
    public string? Key { get; init; }
    public string? ServiceKey { get; init; }
    public string? Questions { get; init; }
    public string? Answer { get; init; }
    public string? Status { get; init; }
    public string? AsOfDate { get; init; }
}

public sealed record ReceptionistSetupPage(ReceptionistConfigurationSnapshot Snapshot, ReceptionistSetupInput Input,
    string PostUrl, string AdmissionUrl, string ReturnUrl, IReadOnlyList<string> CrmBuckets, string? Message = null, ReceptionistReadinessProjection? Readiness = null, string? ContentReviewUrl = null, ReceptionistConfigurationSnapshot? OperatingSnapshot = null);

public sealed record ReceptionistAdmissionInput(Guid ExpectedRevision, bool Enabled);

/// <summary>A read-only observation, never call authorization or provider/audio acceptance.</summary>
public sealed record ReceptionistReadinessProjection
{
    public DateTimeOffset? ObservedUtc { get; init; }
    public long? OperatingConfigurationVersion { get; init; }
    public bool? OwnerPermissionEnabled { get; init; }
    public bool CanAllowOwnerPermission { get; init; }
    public string RuntimeState { get; init; } = "unknown";
    public string BindingScope { get; init; } = "unknown";
    public string ApplicationWindowState { get; init; } = "unknown";
    public string AdmissionReason { get; init; } = "unknown";
    public int? OpenCalls { get; init; }
    public int? OpenCallsWithoutStopCondition { get; init; }
    public int? AwaitingStopOrClosure { get; init; }
    // Existing binding/provider receipts cannot prove the current carrier route or human-heard audio.
    public string CarrierRouteState { get; init; } = "unverified";
    public DateTimeOffset? CarrierRouteObservedUtc { get; init; }
    public string RestrictedAiAcceptanceState { get; init; } = "unverified";
    public string PublicAiAcceptanceState { get; init; } = "unverified";
    public string CallReadiness { get; init; } = "Unverified";
}
