using System;

namespace AgentPortal.Models;

public class AssistantWorkspaceViewModel
{
    public Guid Id { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Email { get; set; } = "";
    public string? AssistantUserId { get; set; }
    public string ParentAgentUserId { get; set; } = "";
    public bool IsActive { get; set; }
    public bool HasLoggedIn { get; set; }
    public DateTime InvitedAtUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public bool IsSelfView { get; set; }

    public string FullName => $"{FirstName} {LastName}".Trim();
    public string StatusLabel => !IsActive ? "Disabled" : (HasLoggedIn ? "Active" : "Invited");
}
