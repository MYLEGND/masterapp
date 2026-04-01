namespace Domain.Entities;

public class AgentClient
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string AgentUserId { get; set; } = "";
    public string ClientUserId { get; set; } = "";

    // NEW: stable identifiers for self-healing
    public string AgentUpn { get; set; } = ""; // e.g., zac.owen@mylegnd.com
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
