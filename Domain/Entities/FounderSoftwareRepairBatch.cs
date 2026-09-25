namespace Domain.Entities;

// GitHub owns source content; this row owns the serialized Founder batch operation.
// An uncertain remote write is never retried as though it had not happened.
public sealed class FounderSoftwareRepairBatch
{
    public string Id { get; set; } = "active";
    public string BaseSha { get; set; } = string.Empty;
    public string PreviewBranch { get; set; } = "hotfix/staging-batch";
    public string? ReviewedHeadSha { get; set; }
    public string? HeadSha { get; set; }
    public int? PullRequestNumber { get; set; }
    public string State { get; set; } = "Empty";
    public string? OperationId { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }
    public string Revision { get; set; } = Guid.NewGuid().ToString("N");
    public string? MergedSha { get; set; }
    public string? DeployedTreeSha { get; set; }
    public DateTime? CompletionVerifiedUtc { get; set; }
    public long? DeploymentRunId { get; set; }
    public string? DeploymentEvidenceJson { get; set; }
    public string? CandidateValidationEvidenceJson { get; set; }
}
