using Domain.Enums;

namespace Domain.Entities;

public class ActionItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public RelatedEntityType RelatedEntityType { get; set; }
    public string RelatedEntityId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public ActionOwnerType OwnerType { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public string EffectiveAgentOid { get; set; } = string.Empty;

    public DateTime? DueDateUtc { get; set; }

    public ActionSurface ActionSurface { get; set; } = ActionSurface.CrmOnly;
    public ActionCategory ActionCategory { get; set; } = ActionCategory.Other;
    public string? PipelineStage { get; set; }
    public bool IsEscalated { get; set; } = false;

    public ActionStatus Status { get; set; } = ActionStatus.Planned;
    public ActionPriority Priority { get; set; } = ActionPriority.P2;

    public Guid? DecisionId { get; set; }
    public Guid? BlockerId { get; set; }

    public string? Source { get; set; }
    public string? SourceRef { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? DismissedReason { get; set; }
}
