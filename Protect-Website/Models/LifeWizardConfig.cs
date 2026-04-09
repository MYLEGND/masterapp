using System.Collections.Generic;

namespace Protect_Website.Models
{
    public record LifeWizardOption(string Code, string Label);

    /// <summary>
    /// Step definition with an optional alias so we can mirror legacy hidden fields (AgeRange/ProtectFocus) when needed.
    /// </summary>
    public record LifeWizardStep(string Question, IReadOnlyList<LifeWizardOption> Options, string FieldAlias = "");

    /// <summary>
    /// Runtime configuration for the shared Life wizard view. Each product route supplies its own instance.
    /// </summary>
    public class LifeWizardConfig
    {
        public string OfferKey { get; set; } = "life";
        public string ProductType { get; set; } = "life"; // stored in payload
        public string PageKey { get; set; } = "quote_life";
        public string DisplayName { get; set; } = "Life Insurance";
        public string Header { get; set; } = "Explore Your Life Insurance Options";
        public string Subheader { get; set; } = "Request a personalized review based on your needs, goals, and budget.";
        public string PageTitle { get; set; } = "Get Your Personalized Life Insurance Review";
        public string SubmitButtonText { get; set; } = "Get My Personalized Review";
        public string PostAction { get; set; } = "Life"; // controller action name
        public IReadOnlyList<LifeWizardStep> Steps { get; set; } = new List<LifeWizardStep>();
        public string StartEvent { get; set; } = "life_general_form_start";
        public string SubmitEvent { get; set; } = "life_general_submit";
    }
}
