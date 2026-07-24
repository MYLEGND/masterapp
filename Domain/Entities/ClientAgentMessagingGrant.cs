namespace Domain.Entities;

public class ClientAgentMessagingGrant
{
    public Guid Id { get; set; }

    public string ClientUserId { get; set; } = string.Empty;

    public string AgentUserId { get; set; } = string.Empty;

    public string GrantedByAgentUserId { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public DateTime GrantedUtc { get; set; }

    public DateTime? RevokedUtc { get; set; }

    public string? Reason { get; set; }
}
