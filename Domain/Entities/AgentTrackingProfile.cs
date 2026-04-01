using System;

namespace Domain.Entities;

public class AgentTrackingProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string AgentUserId { get; set; } = null!; // OID
    public string AgentUpn { get; set; } = null!;    // email/UPN
    public string Slug { get; set; } = null!;
    public string? DisplayName { get; set; }
    public string Status { get; set; } = "active";   // active, disabled
    public string? PreferredEnvironment { get; set; } // prod/dev future-proof
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public ICollection<AgentTrackingAlias> Aliases { get; set; } = new List<AgentTrackingAlias>();
}
