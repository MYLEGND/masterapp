using Domain.Enums;

namespace Domain.Entities;

public class Blocker
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public RelatedEntityType RelatedEntityType { get; set; }
    public string RelatedEntityId { get; set; } = string.Empty;

    public BlockerType BlockerType { get; set; } = BlockerType.Unknown;
    public string BlockerReason { get; set; } = string.Empty;
    public BlockerOwnerType BlockerOwnerType { get; set; } = BlockerOwnerType.Agent;
    public string BlockerOwnerId { get; set; } = string.Empty;
    public BlockerStatus Status { get; set; } = BlockerStatus.Open;

    public DateTime? UnblockDueDateUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedUtc { get; set; }
    public DateTime? ResolvedUtc { get; set; }
    public string? Notes { get; set; }
}
