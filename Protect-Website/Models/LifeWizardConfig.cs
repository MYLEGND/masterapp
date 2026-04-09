using System.Collections.Generic;

namespace Protect_Website.Models
{
    public record LifeWizardStep(string Question, IReadOnlyList<string> Options);

    public class LifeWizardConfig
    {
        public string ProductType { get; set; } = "life";
        public string Header { get; set; } = "Explore Your Life Insurance Options";
        public string Subheader { get; set; } = "Request a personalized review based on your needs, goals, and budget.";
        public IReadOnlyList<LifeWizardStep> Steps { get; set; } = new List<LifeWizardStep>();
        public string StartEvent { get; set; } = "life_general_form_start";
        public string SubmitEvent { get; set; } = "life_general_submit";
    }
}
