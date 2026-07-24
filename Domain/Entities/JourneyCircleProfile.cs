namespace Domain.Entities;

/// <summary>Voluntary, privacy-controlled community information; never a second client identity.</summary>
public sealed class JourneyCircleProfile
{
    public Guid Id { get; set; }
    public Guid ClientProfileId { get; set; }
    public bool IsOptedIn { get; set; }
    public bool IsDiscoverable { get; set; }
    public bool AllowSuggestions { get; set; }
    public bool AllowConnectionRequests { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? LifeStage { get; set; }
    public string? LocationLabel { get; set; }
    public string? Introduction { get; set; }
    public string GoalsJson { get; set; } = "[]";
    public string InterestsJson { get; set; } = "[]";
    public string CircleCodesJson { get; set; } = "[]";
    public string ConnectionTypesJson { get; set; } = "[]";
    public string? CommunicationStyle { get; set; }
    public string? AccountabilityFrequency { get; set; }
    public string? CommunityAccessState { get; set; } = "Active";
    public DateTime? ConsentAffirmedUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public ClientProfile ClientProfile { get; set; } = null!;
}
