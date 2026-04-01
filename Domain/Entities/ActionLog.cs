namespace Domain.Entities;

public class ActionLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ActionId { get; set; }
    public string ActorId { get; set; } = string.Empty;
    public string Verb { get; set; } = string.Empty;
    public string? PayloadJson { get; set; }
    public DateTime OccurredUtc { get; set; } = DateTime.UtcNow;
}
