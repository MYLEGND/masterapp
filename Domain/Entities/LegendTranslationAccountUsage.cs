namespace Domain.Entities;

/// <summary>
/// The server-owned translation allowance for one typed LEGEND account.
/// Permission remains in ControlledResourceGrants and consumption remains in
/// LegendTranslationUsagePeriods; this record intentionally contains neither.
/// </summary>
public sealed class LegendTranslationEntitlement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public string ParticipantType { get; set; } = string.Empty;
    public long MonthlyCharacterAllowance { get; set; }
    public bool IsUnlimited { get; set; }
    public string EntitlementSource { get; set; } = "FounderManaged";
    /// <summary>True when the canonical entitlement was last set by the Founder.</summary>
    public bool IsFounderOverride { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string? UpdatedByUserId { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Fast current-period account aggregate. It is updated conditionally during
/// reservation so concurrent requests cannot exceed a finite allowance.
/// </summary>
public sealed class LegendTranslationUsagePeriod
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public string ParticipantType { get; set; } = string.Empty;
    public DateOnly PeriodStart { get; set; }
    public long ConsumedCharacters { get; set; }
    public long ReservedCharacters { get; set; }
    public long ProviderBillableCharacters { get; set; }
    public long ProviderOperationCount { get; set; }
    public long SameLanguageCharactersAvoided { get; set; }
    public long TranslationMemoryCharactersAvoided { get; set; }
    public long ContextualCharactersAvoided { get; set; }
    public long QuotaDeniedRequestCount { get; set; }
    public long ProviderFailureCount { get; set; }
    public long GroupUniqueTargetReuseCount { get; set; }
    public DateTime? LastTranslationActivityUtc { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Durable, privacy-safe audit of one billable translation reservation. The
/// request reference is a one-way server-generated reference, never message
/// body text. Its unique constraint supplies retry idempotency.
/// </summary>
public sealed class LegendTranslationUsageLedger
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string RequestReference { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string ParticipantType { get; set; } = string.Empty;
    public DateOnly PeriodStart { get; set; }
    public string SourceLanguageCode { get; set; } = string.Empty;
    public string TargetLanguageCode { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public long BillableCharacters { get; set; }
    public bool ProviderExecuted { get; set; }
    public bool Succeeded { get; set; }
    public string State { get; set; } = "Reserving";
    public string? FailureCode { get; set; }
    public DateTime? ReservationExpiresUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; set; }
}
