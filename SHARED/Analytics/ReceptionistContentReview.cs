namespace Shared.Analytics;

public sealed record ReceptionistContentVersion(Guid Id, long Version, bool WasApplied);
public sealed record ReceptionistContentPreview(Guid ExpectedRevision, Guid DraftId, long DraftVersion,
    Guid BaselineId, long BaselineVersion, string ReviewHash, ReceptionistBusinessConfiguration Draft,
    ReceptionistBusinessConfiguration Baseline, bool CanApply, string? BlockReason,
    IReadOnlyList<ReceptionistContentVersion> History, bool IsRecovery = false);
public sealed record ReceptionistContentApplyInput(Guid ExpectedRevision, string ReviewHash, bool ConfirmContentReview);
public sealed record ReceptionistContentRollbackInput(Guid ExpectedRevision, Guid SourceVersionId, bool RestoreOperatingConfiguration = false);
public sealed record ReceptionistContentReviewPage(ReceptionistContentPreview? Preview, string ApplyUrl,
    string RollbackUrl, string ReturnUrl, ReceptionistContentApplyInput? SubmittedApply = null,
    ReceptionistContentRollbackInput? SubmittedRollback = null, ReceptionistPlanFailure? Failure = null,
    string? ReviewUrl = null);

public sealed record ReceptionistContentApplyResult(ReceptionistConfigurationSnapshot Current,
    Guid AppliedConfigurationId, long AppliedVersion, string ReviewHash, bool Replayed);
