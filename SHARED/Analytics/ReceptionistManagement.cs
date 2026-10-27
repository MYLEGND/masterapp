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
    string? PlanUrl = null, string? ApplyUrl = null, string? ReviewUrl = null, ReceptionistOperationReview? Review = null, string? PrepareUrl = null, string? TestGrantUrl = null, string? TestStartUrl = null, string? TestControlPlaneUrl = null,
    ReceptionistOperationPreparationInput? SubmittedPreparation = null, ReceptionistCredentialDraft? SubmittedCredential = null, string? SelfUrl = null, string? AdvancedUrl = null, ReceptionistOperationReconciliation? Reconciliation = null);

// Deliberately excludes Secret: it is never returned to a view after a failed save.
public sealed record ReceptionistCredentialDraft(Guid ExpectedConnectionRevision, string ProviderProjectKey, string Purpose);

// Only these fixed, non-secret messages are shown for operation planning failures.
public sealed record ReceptionistPlanFailure(string Message, string ActionLabel, string TargetFragment)
{
    public static ReceptionistPlanFailure CurrentProjectRequired { get; } = new(
        "The current voice connection must be reviewed and applied before call permission can be enabled.",
        "Prepare or review the voice connection", "voice-connection");
    public static ReceptionistPlanFailure StaleOrUnavailable { get; } = new(
        "This review is expired, unavailable, or no longer matches the current setup. Prepare a current review before trying again.",
        "Review current operations", "operation-reviews");
    public static ReceptionistPlanFailure ChooseOperation { get; } = new(
        "Choose a valid saved operation before reviewing it.", "Review current operations", "operation-reviews");
    public static ReceptionistPlanFailure InvalidPreparation { get; } = new(
        "The proposed setup is incomplete or inconsistent. Check the saved credential, funding and dated evidence; your entries remain below.",
        "Check voice setup", "voice-connection");
    public static ReceptionistPlanFailure StalePreparation { get; } = new(
        "Setup changed while you were editing. Your entries and original revision remain below. Copy them, reload, and prepare a current review.",
        "Check voice setup", "voice-connection");
    public static ReceptionistPlanFailure InvalidCredential { get; } = new(
        "Check the project and credential type, and save receptionist settings first. For security, enter the credential again.",
        "Check voice connection", "voice-connection");
    public static ReceptionistPlanFailure StaleCredential { get; } = new(
        "Setup changed. Reload before saving a credential; for security, enter it again.", "Check voice connection", "voice-connection");
    public static ReceptionistPlanFailure InvalidWithdrawal { get; } = new(
        "This saved credential is unavailable in this workspace. Review the current saved versions.", "Check saved credentials", "voice-connection");
    public static ReceptionistPlanFailure StaleWithdrawal { get; } = new(
        "Setup changed. Reload and review the saved credential before withdrawing it.", "Check saved credentials", "voice-connection");
    public static ReceptionistPlanFailure UnconfirmedOperation { get; } = new(
        "The current review is unavailable, but the earlier result is still unverified. Keep the original operation details and ask an authorized operator to reconcile them; do not prepare a replacement or repeat the change.",
        "Keep the same operation details", "operation-reviews");
    public static ReceptionistPlanFailure InvalidApply { get; } = new(
        "A current exact-change preview is required. If an earlier result is unconfirmed, reconcile that operation before retrying.",
        "Review current operations", "operation-reviews");
    public static ReceptionistPlanFailure StaleApply { get; } = new(
        "The preview or its authorization changed. Review the current operation; if an earlier result is unconfirmed, reconcile it before retrying.",
        "Review current operations", "operation-reviews");
    public static ReceptionistPlanFailure InvalidAdmission { get; } = new(
        "Call permission could not be changed. Review the current voice connection and required inquiry acceptance first.", "Review voice connection", "voice-connection");
    public static ReceptionistPlanFailure StaleAdmission { get; } = new(
        "The setup changed. Reload the receptionist home and review permission again before making a change.", "Reload receptionist home", "reload");
    public static ReceptionistPlanFailure MissingContent { get; } = new(
        "Save a valid receptionist draft and establish an accepted baseline before reviewing content.", "Open conversation settings", "conversation");
    public static ReceptionistPlanFailure InvalidContent { get; } = new(
        "This content could not be applied or restored with the current protected settings and service routes. Review the current draft before trying again.", "Review saved content", "content-review");
    public static ReceptionistPlanFailure StaleContent { get; } = new(
        "The settings changed. Review the current content and selected version again before applying or restoring it.", "Review saved content", "content-review");
}

// An unresolved receipt identity, not permission to retry or proof of completion.
public sealed record ReceptionistOperationReconciliation(string Key, string Sha256);

public sealed record ReceptionistOperationFact(string Label, string Value);
public sealed record ReceptionistOperationReview(string Key, string Sha256, string Label, string Operation,
    bool CanApply, IReadOnlyList<string> Blockers, IReadOnlyList<ReceptionistOperationFact> Facts,
    string? ExpectedStateToken, Guid? PendingReviewRevision = null)
{
    public static bool IsSavedIdentity(string? key, string? sha256) =>
        key is not null && key.StartsWith("saved-", StringComparison.Ordinal) &&
        Guid.TryParseExact(key[6..], "N", out var id) && id != Guid.Empty &&
        sha256 is { Length: 64 } && sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

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
    public int? MaximumSupportedCallSeconds { get; set; }
    public string? BudgetPolicyMode { get; set; }
    public bool AcceptDelayedBudgetOverrun { get; set; }
    public decimal? ObservedProviderMonthlyBudget { get; set; }
    public DateTime? ProviderBudgetPeriodStartUtc { get; set; }
    public DateTime? ProviderBudgetPeriodEndUtc { get; set; }
    public DateTime? BudgetPolicyExpiresUtc { get; set; }
    public List<ReceptionistPreparationCostInput> Costs { get; set; } = [];
    public List<ReceptionistPreparationEvidenceInput> Evidence { get; set; } = [];
}
public sealed class ReceptionistPreparationCostInput
{
    public string Key { get; set; } = string.Empty;
    public Guid FundingAccountId { get; set; }
    public decimal? CashFixedMaximum { get; set; }
    public decimal? CashPerSecondMaximum { get; set; }
    public decimal? ConsumptionFixedMaximum { get; set; }
    public decimal? ConsumptionPerSecondMaximum { get; set; }
    // Nullable inputs distinguish deliberately entered zero from omitted estimate components.
    public decimal? CashFixedEstimate { get; set; }
    public decimal? CashPerSecondEstimate { get; set; }
    public decimal? ConsumptionFixedEstimate { get; set; }
    public decimal? ConsumptionPerSecondEstimate { get; set; }
    public string? ZeroCashJustification { get; set; }
    public string? ZeroConsumptionJustification { get; set; }
}
public sealed class ReceptionistPreparationEvidenceInput
{
    public string Kind { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public string? Sha256 { get; set; }
    public DateTime? ObservedUtc { get; set; }
}
public sealed record ReceptionistFundingChoice(Guid Id, string Label);
