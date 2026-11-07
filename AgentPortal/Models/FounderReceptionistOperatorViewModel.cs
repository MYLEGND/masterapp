using System.ComponentModel.DataAnnotations;
using Infrastructure.Reception;
using Shared.Analytics;

namespace AgentPortal.Models;

public sealed record FounderReceptionistOperatorViewModel(
    IReadOnlyList<ReceptionistOperatorPackageSummary> Packages,
    ReceptionistOperatorPlan? Plan = null,
    ReceptionistOperatorReceipt? Receipt = null,
    string? Error = null,
    IReadOnlyList<ReceptionistSavedOperationSummary>? SavedRetirements = null,
    ReceptionistOperationReview? PendingRetirementReview = null);

public sealed class FounderRetirementDraftInput
{
    [Required, StringLength(160)] public string ExpectedOwnerKey { get; set; } = string.Empty;
    [Required] public Guid ConfigurationId { get; set; }
    [Required] public Guid CallId { get; set; }
    [Required, StringLength(200)] public string EvidenceReference { get; set; } = string.Empty;
    [Required, RegularExpression("^[a-f0-9]{64}$")] public string EvidenceSha256 { get; set; } = string.Empty;
    [Required, StringLength(32)] public string EvidenceObservedUtc { get; set; } = string.Empty;
    [Required, StringLength(500)] public string Reason { get; set; } = string.Empty;
}
public sealed class FounderRetirementReviewInput
{
    [Required] public Guid Id { get; set; }
    [Required, RegularExpression("^[a-f0-9]{64}$")] public string Hash { get; set; } = string.Empty;
    public Guid Revision { get; set; }
    public bool Accepted { get; set; }
}

public sealed class FounderReceptionistPlanInput
{
    [Required, StringLength(100)]
    public string PackageKey { get; set; } = string.Empty;

    [Required, RegularExpression("^[a-f0-9]{64}$")]
    public string PackageSha256 { get; set; } = string.Empty;
}

public sealed class FounderReceptionistApplyInput
{
    [Required, StringLength(100)]
    public string PackageKey { get; set; } = string.Empty;

    [Required, RegularExpression("^[a-f0-9]{64}$")]
    public string PackageSha256 { get; set; } = string.Empty;

    [Required, StringLength(4096)]
    public string ExpectedStateToken { get; set; } = string.Empty;
}
