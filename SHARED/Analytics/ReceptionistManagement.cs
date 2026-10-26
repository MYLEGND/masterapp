namespace Shared.Analytics;

public sealed record ReceptionistCredentialInput(Guid ExpectedConnectionRevision, string ProviderProjectKey,
    string Purpose, string Secret);
public sealed record ReceptionistCredentialSummary(Guid Id, string ProviderProjectKey, string Purpose,
    DateTime CreatedUtc, bool Revoked);
public sealed record ReceptionistSavedOperationSummary(string Key, string Sha256, string Operation, string ReviewState,
    DateTime ExpiresUtc, Guid Revision);
public sealed record ReceptionistManagementSummary(Guid ConnectionRevision,
    IReadOnlyList<ReceptionistCredentialSummary> Credentials, IReadOnlyList<ReceptionistSavedOperationSummary> Operations, bool CanReview = false, IReadOnlyList<ReceptionistFundingChoice>? FundingChoices = null, bool CanRunVoiceTest = false);

public sealed record ReceptionistManagementPage(ReceptionistManagementSummary Summary,
    string SaveCredentialUrl, string RevokeCredentialUrl, string ReturnUrl, string? Message = null,
    string? PlanUrl = null, string? ApplyUrl = null, string? ReviewUrl = null, ReceptionistOperationReview? Review = null, string? PrepareUrl = null, string? TestGrantUrl = null, string? TestStartUrl = null, string? TestControlPlaneUrl = null);

public sealed record ReceptionistOperationFact(string Label, string Value);
public sealed record ReceptionistOperationReview(string Key, string Sha256, string Label, string Operation,
    bool CanApply, IReadOnlyList<string> Blockers, IReadOnlyList<ReceptionistOperationFact> Facts,
    string? ExpectedStateToken, Guid? PendingReviewRevision = null);

// Typed setup requests carry proposed facts, never review identity or canonical state.
public sealed class ReceptionistOperationPreparationInput
{
    public Guid ExpectedConnectionRevision { get; set; }
    public string Operation { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public Guid ApiCredentialVersionId { get; set; }
    public DateTime? WindowStartsUtc { get; set; }
    public DateTime? WindowExpiresUtc { get; set; }
    public string? Model { get; set; }
    public string? Voice { get; set; }
    public bool ControlledTestOnly { get; set; } = true;
    public int MaximumSupportedCallSeconds { get; set; }
    public List<ReceptionistPreparationCostInput> Costs { get; set; } = [];
    public List<ReceptionistPreparationEvidenceInput> Evidence { get; set; } = [];
}
public sealed class ReceptionistPreparationCostInput
{
    public string Key { get; set; } = string.Empty;
    public Guid FundingAccountId { get; set; }
    public decimal CashFixedMaximum { get; set; }
    public decimal CashPerSecondMaximum { get; set; }
    public decimal ConsumptionFixedMaximum { get; set; }
    public decimal ConsumptionPerSecondMaximum { get; set; }
}
public sealed class ReceptionistPreparationEvidenceInput
{
    public string Kind { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public string? Sha256 { get; set; }
    public DateTime? ObservedUtc { get; set; }
}
public sealed record ReceptionistFundingChoice(Guid Id, string Label);
