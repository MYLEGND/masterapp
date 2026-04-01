using System;
using Domain.Enums;

namespace Domain.Entities;

public class Commitment
{
    public Guid Id { get; set; }
    public RelatedEntityType RelatedEntityType { get; set; }
    public string RelatedEntityId { get; set; } = string.Empty;
    public ActionOwnerType PromisedByType { get; set; }
    public string PromisedById { get; set; } = string.Empty;
    public ActionOwnerType PromisedToType { get; set; }
    public string PromisedToId { get; set; } = string.Empty;
    public string PromiseText { get; set; } = string.Empty;
    public DateTimeOffset DueDateUtc { get; set; }
    public CommitmentStatus Status { get; set; } = CommitmentStatus.Open;
    public Guid? LinkedActionId { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? FulfilledAtUtc { get; set; }
}
