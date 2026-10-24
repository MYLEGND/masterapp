namespace Infrastructure.Reception;

// Explicit nonsecret projection; never serialize entities, bindings or configuration policy.
public sealed record ReceptionistPreparationAccount(Guid Id, Guid Revision, string Kind, string ScopeKey,
    string Currency, string TimeZoneId);
public sealed record ReceptionistPreparationHistory(Guid ConfigurationId, long Version, string ConfigurationSha256);
public sealed record ReceptionistOperatorPreparation(DateTime ObservedUtc, string Status,
    Guid MarketingConnectionId, string OwnerKey, string Provider, Guid ConnectionRevision, Guid ControlRevision,
    Guid ConfigurationId, long ConfigurationVersion, string ConfigurationSha256, string? ConnectionProviderProjectKey,
    bool AdmissionEnabled, bool CurrentBindingAccepted, IReadOnlyList<ReceptionistPreparationAccount> Accounts,
    ReceptionistPreparationHistory? HistoricalAcceptedConfiguration, IReadOnlyList<string> RemainingRequirements);

public sealed record ReceptionistOperatorPackageSummary(string PackageKey, string PackageSha256, string Label);
public sealed record ReceptionistOperatorApplyRequest(string PackageKey, string PackageSha256, string ExpectedStateToken);
public sealed record ReceptionistOperatorFact(string Label, string Value);
public sealed record ReceptionistOperatorPlan(string PackageKey, string PackageSha256, string Label, string Operation,
    bool CanApply, IReadOnlyList<string> Blockers, IReadOnlyList<ReceptionistOperatorFact> Facts, string? ExpectedStateToken);
public sealed record ReceptionistOperatorReceipt(string PackageKey, string PackageSha256, string Operation,
    string Outcome, bool CommitKnown, bool ReadBackVerified, string? DurableReference, IReadOnlyList<ReceptionistOperatorFact> Facts);
