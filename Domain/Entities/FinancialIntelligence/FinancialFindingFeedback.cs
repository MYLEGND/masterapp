namespace Domain.Entities.FinancialIntelligence;

/// <summary>
/// An append-only client or agent outcome event. It supports transparent,
/// bounded prioritization changes without changing a finding's factual basis.
/// </summary>
public sealed class FinancialFindingFeedback
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FinancialFindingId { get; set; }

    public Guid ClientProfileId { get; set; }

    public string ActorType { get; set; } = "";

    public string ActorUserId { get; set; } = "";

    public string FeedbackType { get; set; } = "";

    public string? ReasonCode { get; set; }

    public string? Note { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
