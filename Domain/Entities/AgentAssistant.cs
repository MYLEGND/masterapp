namespace Domain.Entities;

/// <summary>
/// Represents a restricted sub-user (assistant) created by an agent.
/// Assistants authenticate via Azure AD B2B invite and are scoped to
/// their parent agent's data (Leads + Workstation only).
/// </summary>
public class AgentAssistant
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Azure AD OID of the agent who created this assistant.</summary>
    public string ParentAgentUserId { get; set; } = "";

    /// <summary>Azure AD OID of the assistant (populated after B2B invite accepted).</summary>
    public string? AssistantUserId { get; set; }

    public string FirstName { get; set; } = "";
    public string LastName  { get; set; } = "";

    /// <summary>Email the invitation was sent to.</summary>
    public string Email { get; set; } = "";
    public string? NormalizedEmail { get; set; }

    /// <summary>Whether this assistant can currently log in.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime InvitedAt  { get; set; } = DateTime.UtcNow;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
