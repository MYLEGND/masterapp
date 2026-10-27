namespace Shared.Analytics;

/// <summary>Private owner-authored configuration. Values are never platform-wide pilot defaults.</summary>
public sealed record ReceptionistBusinessConfiguration
{
    public string BusinessName { get; init; } = string.Empty;
    public string ConversationInstructions { get; init; } = string.Empty;
    public string BusinessPolicy { get; init; } = string.Empty;
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? CompactVoicePolicy { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? CompactVoicePolicySourceSha256 { get; init; }
    public string ApprovedKnowledgeJson { get; init; } = "{}";
    public string ServiceDefinitionsJson { get; init; } = "[]";
    public string IntakeRulesJson { get; init; } = "{}";
    public string ContactAndRetentionPolicyJson { get; init; } = "{}";
    public string TimeZoneId { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public decimal MonthlyCashLimit { get; init; }
    public decimal MonthlyConsumptionLimit { get; init; }
    public long MonthlyVoiceSecondsLimit { get; init; }
    public int MaximumConcurrentCalls { get; init; }
    public int MaximumCallSeconds { get; init; }
    public int AdmissionWindowSeconds { get; init; }
    public int MaximumCallsPerWindow { get; init; }
    public IReadOnlyList<ReceptionistHoursWindow> Hours { get; init; } = [];
    public IReadOnlyList<string> RequestedCapabilities { get; init; } = [];
    // Omitted for historical configurations. Saving a policy never accepts a capability or enables calls.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ReceptionistBookingPolicy? BookingPolicy { get; init; }
}

public sealed record ReceptionistHoursWindow(DayOfWeek Day, TimeOnly Start, TimeOnly End);

public sealed record ReceptionistConfigurationSnapshot(
    MarketingOwnerScope Owner,
    string Provider,
    Guid ConnectionRevision,
    Guid? ConfigurationId,
    long ConfigurationVersion,
    string? ConfigurationSha256,
    bool AdmissionEnabled,
    bool BindingAccepted,
    ReceptionistBusinessConfiguration? Configuration,
    IReadOnlyList<ReceptionistCapabilityStatus> Capabilities,
    ReceptionistAccountingStatus? Accounting = null);

public sealed record ReceptionistAccountingStatus(bool CurrentOpeningReconciled, bool AdmissionEnabled,
    decimal AcceptedCashCeiling, decimal AcceptedConsumptionCeiling, bool PolicyLimitsAccepted,
    decimal CashCommitted, decimal ConsumptionCommitted, decimal CashReserved, decimal ConsumptionReserved);

public sealed record ReceptionistCapabilityStatus(string Key, string State, string? AcceptanceReceipt);

public static class ReceptionistCapabilityStates
{
    public const string Disabled = "disabled";
    public const string Unverified = "unverified";
    public const string Accepted = "accepted";
}

/// <summary>Semantic capability identity, not a URL, permission grant or fabricated tool.</summary>
public static class ReceptionistCapabilities
{
    public const string CanonicalIntake = "canonical_intake";
    public const string CanonicalBooking = "canonical_booking";
}

/// <summary>Human-reviewed mapping under the existing configuration/version/binding authority.</summary>
public sealed record ReceptionistBookingPolicy
{
    public bool Enabled { get; init; }
    public bool QaOnly { get; init; } = true;
    public string ExpectedCalendarConfigurationSha256 { get; init; } = string.Empty;
    public string ExpectedCalendarTargetSha256 { get; init; } = string.Empty;
    public bool AllowConsentedCustomerEmail { get; init; }
    public string PrivacyAcceptanceReference { get; init; } = string.Empty;
    public Guid? BookingAgentProfileId { get; init; }
    public int MinimumNoticeMinutes { get; init; }
    // Omit the default to preserve historical configuration fingerprints.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int MinimumNoticeBusinessDays { get; init; }
    public int MaximumAdvanceDays { get; init; }
    public IReadOnlyList<ReceptionistBookingService> Services { get; init; } = [];
}

public sealed record ReceptionistBookingService
{
    public string ServiceKey { get; init; } = string.Empty;
    public string BookingServiceId { get; init; } = string.Empty;
    public string TimeZoneId { get; init; } = string.Empty;
    public int DurationMinutes { get; init; }
}

/// <summary>Allowlisted metadata only. No account identity, token, calendar event or client record.</summary>
public sealed record ReceptionistBookingReadiness(
    string State, bool CalendarRecorded, bool CalendarConnectedRecorded, Guid? CalendarRevision, string? CalendarConfigurationSha256,
    bool? BookingAgentEnabled, string? CalendarTargetSha256,
    string AuthorizationKind, bool AppointmentPermissionRecorded, DateTime? LastVerifiedUtc,
    bool MappingConfigured, bool Requested, bool QaOnly, string InvitationPolicy,
    IReadOnlyList<ReceptionistBookingServiceStatus> Services, IReadOnlyList<string> Reasons);

public sealed record ReceptionistBookingServiceStatus(string ServiceKey, string PublicLabel, string TimeZoneId, int DurationMinutes);
