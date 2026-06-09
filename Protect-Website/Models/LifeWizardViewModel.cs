namespace Protect_Website.Models
{
    /// <summary>
    /// Composite view model for the shared Life wizard view.
    /// </summary>
    public class LifeWizardViewModel
    {
        public LifeWizardConfig Config { get; set; } = new();
        public LifeQuoteFormModel Form { get; set; } = new() { FirstName = "", LastName = "", Email = "", Phone = "" };
        public bool IsLandingPage { get; set; }
        public string PageVariant { get; set; } = "website";
        public string PageMode { get; set; } = "site_mode";
        public string EffectivePageKey { get; set; } = "quote_life";
        public LifeWizardAgentTrustProfile? AgentTrustProfile { get; set; }
    }
}
