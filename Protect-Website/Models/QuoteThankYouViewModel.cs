namespace Protect_Website.Models;

public sealed class QuoteThankYouViewModel
{
    public string QuoteKey { get; set; } = "";
    public string BookingLink { get; set; } = "";
    public LifeWizardAgentTrustProfile? AgentTrustProfile { get; set; }
}
