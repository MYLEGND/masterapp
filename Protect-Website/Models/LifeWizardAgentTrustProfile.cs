namespace Protect_Website.Models;

public sealed class LifeWizardAgentTrustProfile
{
    public Guid AgentTrackingProfileId { get; set; }
    public string AgentSlug { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string? Npn { get; set; }
    public string? ShortBio { get; set; }
    public string? ProfileImageUrl { get; set; }
}
