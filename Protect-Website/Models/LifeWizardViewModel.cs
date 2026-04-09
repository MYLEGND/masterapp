namespace Protect_Website.Models
{
    /// <summary>
    /// Composite view model for the shared Life wizard view.
    /// </summary>
    public class LifeWizardViewModel
    {
        public LifeWizardConfig Config { get; set; } = new();
        public LifeQuoteFormModel Form { get; set; } = new() { FirstName = "", LastName = "", Email = "", Phone = "" };
    }
}
