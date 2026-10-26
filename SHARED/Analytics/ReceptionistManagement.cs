namespace Shared.Analytics;

public sealed record ReceptionistCredentialInput(Guid ExpectedConnectionRevision, string ProviderProjectKey,
    string Purpose, string Secret);
public sealed record ReceptionistCredentialSummary(Guid Id, string ProviderProjectKey, string Purpose,
    DateTime CreatedUtc, bool Revoked);
public sealed record ReceptionistSavedOperationSummary(string Key, string Sha256, string Operation, string ReviewState,
    DateTime ExpiresUtc, Guid Revision);
public sealed record ReceptionistManagementSummary(Guid ConnectionRevision,
    IReadOnlyList<ReceptionistCredentialSummary> Credentials, IReadOnlyList<ReceptionistSavedOperationSummary> Operations, bool CanReview = false);

public sealed record ReceptionistManagementPage(ReceptionistManagementSummary Summary,
    string SaveCredentialUrl, string RevokeCredentialUrl, string ReturnUrl, string? Message = null,
    string? PlanUrl = null, string? ApplyUrl = null, string? ReviewUrl = null, ReceptionistOperationReview? Review = null);

public sealed record ReceptionistOperationFact(string Label, string Value);
public sealed record ReceptionistOperationReview(string Key, string Sha256, string Label, string Operation,
    bool CanApply, IReadOnlyList<string> Blockers, IReadOnlyList<ReceptionistOperationFact> Facts,
    string? ExpectedStateToken, Guid? PendingReviewRevision = null);
