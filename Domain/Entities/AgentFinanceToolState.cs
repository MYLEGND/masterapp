namespace Domain.Entities;

public class AgentFinanceToolState
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string AgentUserId { get; set; } = "";

    public string ToolId { get; set; } = "";

    public string JsonState { get; set; } = "{}";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
