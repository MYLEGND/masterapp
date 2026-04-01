namespace Domain.Entities;

/// <summary>
/// Tracks playbook executions to enforce idempotency.
/// ExecutionKey should be unique per event-source instance.
/// </summary>
public class PlaybookExecution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ExecutionKey { get; set; } = string.Empty; // e.g., proposal-finalized:{proposalId}
    public string? MetadataJson { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
