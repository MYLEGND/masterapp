using System.ComponentModel.DataAnnotations;

namespace AgentPortal.Models;

public class AssistantPanelIndexViewModel
{
    public List<AssistantRowViewModel> Assistants { get; set; } = new();
}

public class AssistantDetailViewModel
{
    public Guid Id { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Email { get; set; } = "";
    public bool IsActive { get; set; }
    public bool HasLoggedIn { get; set; }
    public string? AssistantUserId { get; set; }
    public string ParentAgentUserId { get; set; } = "";
    public DateTime InvitedAt { get; set; }
    public DateTime CreatedUtc { get; set; }

    public string FullName => $"{FirstName} {LastName}".Trim();
    public string StatusLabel => !IsActive ? "Disabled" : (HasLoggedIn ? "Active" : "Invited");
}

public class AssistantRowViewModel
{
    public Guid Id { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Email { get; set; } = "";
    public bool IsActive { get; set; }
    public bool HasLoggedIn { get; set; }
    public string InvitedAt { get; set; } = "";

    public string FullName => $"{FirstName} {LastName}".Trim();
    public string StatusLabel => !IsActive ? "Disabled" : (HasLoggedIn ? "Active" : "Invited");
}

public class CreateAssistantViewModel
{
    [Required, StringLength(100)]
    [Display(Name = "First Name")]
    public string FirstName { get; set; } = "";

    [Required, StringLength(100)]
    [Display(Name = "Last Name")]
    public string LastName { get; set; } = "";

    [Required, EmailAddress, StringLength(320)]
    [Display(Name = "Email")]
    public string Email { get; set; } = "";
}
