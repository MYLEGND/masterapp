using System;

namespace Domain.Entities;

public class AgentTrackingAlias
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AgentTrackingProfileId { get; set; }
    public string Slug { get; set; } = null!;
    public bool IsCanonical { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public AgentTrackingProfile? Profile { get; set; }
}
