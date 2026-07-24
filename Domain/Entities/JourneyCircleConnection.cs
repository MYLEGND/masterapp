namespace Domain.Entities;

public sealed class JourneyCircleConnection
{
    public Guid Id { get; set; }
    public string ConnectionKey { get; set; } = string.Empty;
    public Guid RequesterClientProfileId { get; set; }
    public Guid RecipientClientProfileId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ConnectionReason { get; set; }
    public string? Introduction { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? RespondedUtc { get; set; }
}
