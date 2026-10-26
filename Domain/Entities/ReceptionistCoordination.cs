namespace Domain.Entities;

/// <summary>Immutable private configuration under the existing owner/provider connection.</summary>
public sealed class ReceptionistConfigurationVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MarketingConnectionId { get; set; }
    public long Version { get; set; }
    public string ConfigurationJson { get; set; } = string.Empty;
    public string ConfigurationSha256 { get; set; } = string.Empty;
    public string? AcceptedBindingJson { get; set; }
    public string CreatedByActorId { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
}

/// <summary>A budget dimension; owner keys come from WorkspaceKey, never a second tenant registry.</summary>
public sealed class ReceptionistBudgetAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = string.Empty;
    public string ScopeKey { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = string.Empty;
    public bool AdmissionEnabled { get; set; }
    public Guid ControlRevision { get; set; } = Guid.NewGuid();
    public Guid Revision { get; set; } = Guid.NewGuid();
}

/// <summary>Reconciled monthly opening and accumulated facts. Unknown openings never mean zero.</summary>
public sealed class ReceptionistBudgetPeriod
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BudgetAccountId { get; set; }
    public int MonthKey { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public decimal CashLimit { get; set; }
    public decimal ConsumptionLimit { get; set; }
    public decimal CashCommitted { get; set; }
    public decimal ConsumptionCommitted { get; set; }
    public long VoiceSecondsCommitted { get; set; }
    public bool OpeningReconciled { get; set; }
    public string OpeningReceipt { get; set; } = string.Empty;
    public string? CeilingApprovalReceipt { get; set; }
    public DateTime ReconciledThroughUtc { get; set; }
    public decimal OpeningCashCommitted { get; set; }
    public decimal OpeningConsumptionCommitted { get; set; }
    public decimal OpeningCashCeiling { get; set; }
    public decimal OpeningConsumptionCeiling { get; set; }
    public long OpeningVoiceSeconds { get; set; }
    public Guid Revision { get; set; } = Guid.NewGuid();
}

public sealed class ReceptionistCall
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MarketingConnectionId { get; set; }
    public Guid ConfigurationVersionId { get; set; }
    public Guid OwnerBudgetAccountId { get; set; }
    public Guid OwnerBudgetPeriodId { get; set; }
    public Guid ConnectionControlRevision { get; set; }
    public Guid OwnerControlRevision { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string IngressKey { get; set; } = string.Empty;
    public string ProviderProjectKey { get; set; } = string.Empty;
    // Null only while a browser creation attempt has no verified provider identity.
    public string? ProviderSessionId { get; set; }
    public Guid? BrowserCreationId { get; set; }
    public string? BrowserOfferSha256 { get; set; }
    public string State { get; set; } = "reserved";
    public bool ControlledTest { get; set; }
    public Guid? ProviderAcceptAttemptId { get; set; }
    public string? ProviderAcceptWorkerId { get; set; }
    public DateTime? ProviderAcceptAttemptedUtc { get; set; }
    public string? SidebandWorkerId { get; set; }
    public Guid? SidebandLeaseToken { get; set; }
    public long SidebandFence { get; set; }
    public bool SidebandObservationOnly { get; set; }
    public DateTime? SidebandLeaseExpiresUtc { get; set; }
    public DateTime ReservedUtc { get; set; }
    public DateTime MustStopUtc { get; set; }
    public string? ProviderStateReceipt { get; set; }
    public DateTime? ProviderClosedUtc { get; set; }
    public long ReservedVoiceSeconds { get; set; }
    public long ObservedVoiceSeconds { get; set; }
    public bool VoiceUsageFinal { get; set; }
    public bool VoiceAllocationUncertain { get; set; }
    public Guid Revision { get; set; } = Guid.NewGuid();
}

/// <summary>The first signed provider-session decision is durable, including refusal.</summary>
public sealed class ReceptionistSessionDecision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Provider { get; set; } = string.Empty;
    public string ProviderProjectKey { get; set; } = string.Empty;
    public string ProviderSessionId { get; set; } = string.Empty;
    public string IngressKey { get; set; } = string.Empty;
    public Guid? CallId { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime DecidedUtc { get; set; }
}

public sealed class ReceptionistIngressEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DecisionId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ProviderProjectKey { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public DateTime ReceivedUtc { get; set; }
}

/// <summary>One cost component reserves both owner and shared funding allowance.</summary>
public sealed class ReceptionistCostReservation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CallId { get; set; }
    public Guid OwnerBudgetAccountId { get; set; }
    public Guid OwnerBudgetPeriodId { get; set; }
    public Guid FundingBudgetAccountId { get; set; }
    public Guid FundingBudgetPeriodId { get; set; }
    public Guid FundingControlRevision { get; set; }
    public string ComponentKey { get; set; } = string.Empty;
    public decimal CashReserved { get; set; }
    public decimal ConsumptionReserved { get; set; }
    public decimal CashObserved { get; set; }
    public decimal ConsumptionObserved { get; set; }
    public bool CashFinal { get; set; }
    public bool ConsumptionFinal { get; set; }
    public Guid Revision { get; set; } = Guid.NewGuid();
}

/// <summary>Provider receipt identity is distinct from its possibly duplicated webhook deliveries.</summary>
public sealed class ReceptionistUsageReceipt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? CallId { get; set; }
    public Guid? AccountChargeId { get; set; }
    public string ScopeKey { get; set; } = string.Empty;
    public string ComponentKey { get; set; } = string.Empty;
    public string ProviderReceiptKey { get; set; } = string.Empty;
    public string PayloadSha256 { get; set; } = string.Empty;
    public DateTime RecordedUtc { get; set; }
}

/// <summary>Cumulative call-component posting in its actual owner/funding calendar pair, separately for cash and consumption.</summary>
public sealed class ReceptionistCostPosting
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CostReservationId { get; set; }
    public Guid OwnerBudgetAccountId { get; set; }
    public Guid OwnerBudgetPeriodId { get; set; }
    public Guid FundingBudgetAccountId { get; set; }
    public Guid FundingBudgetPeriodId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public decimal CumulativeAmount { get; set; }
    public DateTime FirstEffectiveUtc { get; set; }
    public Guid Revision { get; set; } = Guid.NewGuid();
}

/// <summary>Verified owner-period duration postings; finality never supplies an inferred monthly split.</summary>
public sealed class ReceptionistVoiceUsagePosting
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CallId { get; set; }
    public Guid OwnerBudgetAccountId { get; set; }
    public Guid OwnerBudgetPeriodId { get; set; }
    public long CumulativeSeconds { get; set; }
    public DateTime ServicePeriodEvidenceUtc { get; set; }
    public Guid Revision { get; set; } = Guid.NewGuid();
}

/// <summary>Verified non-call charges, including funding, rental, tax and adjustments.</summary>
public sealed class ReceptionistAccountCharge
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerBudgetAccountId { get; set; }
    public Guid FundingBudgetAccountId { get; set; }
    public Guid? CashOwnerBudgetPeriodId { get; set; }
    public Guid? CashFundingBudgetPeriodId { get; set; }
    public Guid? ConsumptionOwnerBudgetPeriodId { get; set; }
    public Guid? ConsumptionFundingBudgetPeriodId { get; set; }
    public string ProviderChargeKey { get; set; } = string.Empty;
    public string ComponentKey { get; set; } = string.Empty;
    public decimal CashObserved { get; set; }
    public decimal ConsumptionObserved { get; set; }
    public bool CashFinal { get; set; }
    public bool ConsumptionFinal { get; set; }
    public DateTime? CashDebitedUtc { get; set; }
    public DateTime? ConsumptionIncurredUtc { get; set; }
    public Guid Revision { get; set; } = Guid.NewGuid();
}

public sealed class ReceptionistActionIntent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CallId { get; set; }
    public string LogicalActionKey { get; set; } = string.Empty;
    public string CapabilityKey { get; set; } = string.Empty;
    public string RouteKey { get; set; } = string.Empty;
    public string ArgumentsSha256 { get; set; } = string.Empty;
    public string ConfirmationReceipt { get; set; } = string.Empty;
    public Guid? ExecutionLeaseToken { get; set; }
    public long ExecutionFence { get; set; }
    public string? WorkerId { get; set; }
    public Guid AttemptId { get; set; } = Guid.NewGuid();
    public int AttemptNumber { get; set; } = 1;
    public string State { get; set; } = "pending";
    public string? ActualRecordReference { get; set; }
    public string? VerificationReceipt { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public Guid Revision { get; set; } = Guid.NewGuid();
}
