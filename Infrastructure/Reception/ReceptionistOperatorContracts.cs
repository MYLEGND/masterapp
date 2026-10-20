namespace Infrastructure.Reception;

public sealed record ReceptionistOperatorPackageSummary(string PackageKey, string PackageSha256, string Label);
public sealed record ReceptionistOperatorApplyRequest(string PackageKey, string PackageSha256, string ExpectedStateToken);
public sealed record ReceptionistOperatorFact(string Label, string Value);
public sealed record ReceptionistOperatorPlan(string PackageKey, string PackageSha256, string Label, string Operation,
    bool CanApply, IReadOnlyList<string> Blockers, IReadOnlyList<ReceptionistOperatorFact> Facts, string? ExpectedStateToken);
public sealed record ReceptionistOperatorReceipt(string PackageKey, string PackageSha256, string Operation,
    string Outcome, bool CommitKnown, bool ReadBackVerified, string? DurableReference, IReadOnlyList<ReceptionistOperatorFact> Facts);
