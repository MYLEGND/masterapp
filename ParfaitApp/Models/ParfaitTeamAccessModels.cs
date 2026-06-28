using System.ComponentModel.DataAnnotations;

namespace ParfaitApp.Models;

public sealed class ParfaitInternalPageDefinition
{
    public required string Key { get; init; }
    public required string Route { get; init; }
    public required string Title { get; init; }
    public required string Group { get; init; }
    public required string Description { get; init; }
    public int GroupOrder { get; init; }
    public int Order { get; init; }
    public bool ShowInNavigation { get; init; } = true;
    public bool FounderOnly { get; init; }
}

public static class ParfaitTeamRoles
{
    public const string FullControl = "full-control";
    public const string Manager = "manager";
    public const string Support = "support";
    public const string Analyst = "analyst";
}

public sealed class ParfaitTeamRoleDefinition
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Description { get; init; }
    public bool CanManageTeam { get; init; }
    public List<string> DefaultPageKeys { get; init; } = [];
}

public sealed class ParfaitTeamManagementViewModel
{
    public string FounderEmail { get; set; } = string.Empty;
    public string FounderIdentityStatus { get; set; } = string.Empty;
    public int FounderCount { get; set; }
    public List<ParfaitInternalPageDefinition> AssignablePages { get; set; } = [];
    public List<ParfaitInternalPageDefinition> FounderOnlyPages { get; set; } = [];
    public List<ParfaitTeamRoleDefinition> RoleOptions { get; set; } = [];
    public List<ParfaitTeamMemberViewModel> Members { get; set; } = [];
    public ParfaitTeamCreateMemberInput NewMember { get; set; } = new()
    {
        RoleKey = ParfaitTeamRoles.Support
    };
}

public sealed class ParfaitTeamMemberViewModel
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string RoleKey { get; set; } = ParfaitTeamRoles.Support;
    public string RoleLabel { get; set; } = "Support";
    public string RoleDescription { get; set; } = string.Empty;
    public bool CanManageTeam { get; set; }
    public bool IsActive { get; set; }
    public string StatusLabel { get; set; } = "Inactive";
    public string IdentityStatusLabel { get; set; } = "Waiting for first Microsoft sign-in.";
    public bool HasLinkedIdentity { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? InviteSentUtc { get; set; }
    public DateTime? LastSignInUtc { get; set; }
    public List<string> AllowedPageKeys { get; set; } = [];
    public List<string> AllowedPageTitles { get; set; } = [];
}

public sealed class ParfaitTeamCreateMemberInput
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [StringLength(120)]
    public string? DisplayName { get; set; }

    [Required]
    public string RoleKey { get; set; } = ParfaitTeamRoles.Support;
}

public sealed class ParfaitTeamUpdateMemberInput
{
    [StringLength(120)]
    public string? DisplayName { get; set; }

    [Required]
    public string RoleKey { get; set; } = ParfaitTeamRoles.Support;

    public bool IsActive { get; set; }

    public List<string> AllowedPageKeys { get; set; } = [];
}

public sealed class ParfaitTeamSignInResult
{
    public bool Allowed { get; init; }
    public bool IsFounder { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class ParfaitPageAccessResult
{
    public bool Allowed { get; init; }
    public bool IsFounder { get; init; }
    public string Message { get; init; } = string.Empty;
    public ParfaitInternalPageDefinition? Page { get; init; }
}
