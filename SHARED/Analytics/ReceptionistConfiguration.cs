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
}
