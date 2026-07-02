namespace Domain.Entities;

public sealed class CommerceBusinessMember
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CommerceBusinessId { get; set; }
    public CommerceBusiness? CommerceBusiness { get; set; }

    public string Email { get; set; } = string.Empty;
    public string NormalizedEmail { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    public string RoleKey { get; set; } = "owner";
    public string Status { get; set; } = "Active";

    public bool CanManageStorefront { get; set; } = true;
    public bool CanManageCatalog { get; set; } = true;
    public bool CanManageOrders { get; set; } = true;
    public bool CanManageAnalytics { get; set; } = true;
    public bool CanManageTeam { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
