namespace Domain.Entities;

/// <summary>
/// An exact-action approval and terminal execution receipt owned by the existing
/// Founder tool authority. It stores no independent role/permission policy.
/// </summary>
public sealed class FounderAiActionAuthorization
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ActionDigest { get; set; } = string.Empty;
    public string ScopeDigest { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public Guid ConversationId { get; set; }
    public Guid RequestId { get; set; }
    public string Environment { get; set; } = string.Empty;
    public string AuthorizationVersion { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public string CanonicalArgumentsJson { get; set; } = string.Empty;
    public DateTime ApprovedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public string State { get; set; } = "Approved";
    public string? IdempotencyKey { get; set; }
    public DateTime? ExecutionStartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string? ResultJson { get; set; }
    public string Revision { get; set; } = Guid.NewGuid().ToString("N");
}
