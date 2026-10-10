using System.ComponentModel.DataAnnotations;

namespace Legend.Commerce;

public sealed record CommerceBusinessTeamMember(
    Guid Id, string Email, string DisplayName, string Role, string Status,
    bool IsLinked, bool CanManageStorefront, bool CanManageCatalog,
    bool CanManageOrders, bool CanManageAnalytics, bool CanManageTeam,
    long UpdatedTicks);

public sealed record CommerceBusinessTeamViewModel(
    Guid BusinessId, string StoreName, IReadOnlyList<CommerceBusinessTeamMember> Members);

public sealed class CommerceBusinessTeamPermissionsInput
{
    public Guid MemberId { get; set; }
    public long ExpectedUpdatedTicks { get; set; }
    public bool CanManageStorefront { get; set; }
    public bool CanManageCatalog { get; set; }
    public bool CanManageOrders { get; set; }
    public bool CanManageAnalytics { get; set; }
    public bool CanManageTeam { get; set; }
}

public sealed record CommerceBusinessProfileViewModel(
    Guid BusinessId, string Name, string BusinessType, string OwnerEmail, string? PrimaryDomain);

public sealed class CommerceBusinessProfileInput
{
    [Required, StringLength(160, MinimumLength = 2)]
    public string Name { get; set; } = "";
    [Required, StringLength(160, MinimumLength = 2)]
    public string BusinessType { get; set; } = "";
}
