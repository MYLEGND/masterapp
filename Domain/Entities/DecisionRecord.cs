using Domain.Enums;

namespace Domain.Entities;

public class DecisionRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public RelatedEntityType RelatedEntityType { get; set; }
    public string RelatedEntityId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string Rationale { get; set; } = string.Empty;
    public DecisionType RecommendationType { get; set; } = DecisionType.Unknown;

    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedUtc { get; set; }
}
