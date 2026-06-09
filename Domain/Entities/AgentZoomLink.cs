namespace Domain.Entities;

public class AgentZoomLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string AgentUserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public int SortOrder { get; set; } = 0;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
