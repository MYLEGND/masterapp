using System.ComponentModel.DataAnnotations;
using Infrastructure.Reception;

namespace AgentPortal.Models;

public sealed record FounderReceptionistOperatorViewModel(
    IReadOnlyList<ReceptionistOperatorPackageSummary> Packages,
    ReceptionistOperatorPlan? Plan = null,
    ReceptionistOperatorReceipt? Receipt = null,
    string? Error = null);

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
