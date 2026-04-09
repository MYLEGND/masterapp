namespace Protect_Website.Models
{
    public static class LifeOfferKeys
    {
        public const string Life         = "life";
        public const string Mortgage     = "mortgage";
        public const string FinalExpense = "finalexpense";
        public const string Term         = "term";
        public const string WholeLife    = "wholelife";
        public const string Iul          = "iul";
    }

    public sealed record LifeOfferContent(
        string Key,
        string DisplayName,
        string HeroHeadline,
        string HeroSubheadline,
        string FormSectionTitle,
        string SubmitButtonText,
        string PageTitle
    );

    public static class LifeOfferResolver
    {
        private static readonly Dictionary<string, LifeOfferContent> Map =
            new(StringComparer.OrdinalIgnoreCase)
        {
            [LifeOfferKeys.Life] = new(
                Key:               LifeOfferKeys.Life,
                DisplayName:       "Life Insurance",
                HeroHeadline:      "Explore Your Life Insurance Options",
                HeroSubheadline:   "Request a personalized review based on your needs, goals, and budget.",
                FormSectionTitle:  "Tell Us About You",
                SubmitButtonText:  "Request My Review",
                PageTitle:         "Get Your Personalized Life Insurance Review"
            ),
            [LifeOfferKeys.Mortgage] = new(
                Key:               LifeOfferKeys.Mortgage,
                DisplayName:       "Mortgage Protection",
                HeroHeadline:      "Protect What You’ve Built",
                HeroSubheadline:   "Review coverage options designed to help protect your mortgage and family.",
                FormSectionTitle:  "Start Your Mortgage Protection Review",
                SubmitButtonText:  "See My Options",
                PageTitle:         "Mortgage Protection Review"
            ),
            [LifeOfferKeys.FinalExpense] = new(
                Key:               LifeOfferKeys.FinalExpense,
                DisplayName:       "Final Expense",
                HeroHeadline:      "Explore Final Expense Coverage Options",
                HeroSubheadline:   "Review coverage options designed to help with burial and final expense planning.",
                FormSectionTitle:  "Start Your Final Expense Review",
                SubmitButtonText:  "See My Options",
                PageTitle:         "Final Expense Coverage Review"
            ),
            [LifeOfferKeys.Term] = new(
                Key:               LifeOfferKeys.Term,
                DisplayName:       "Term Life",
                HeroHeadline:      "Explore Term Life Insurance Options",
                HeroSubheadline:   "Review temporary coverage options designed to help protect your income and family.",
                FormSectionTitle:  "Start Your Term Life Review",
                SubmitButtonText:  "See My Options",
                PageTitle:         "Term Life Insurance Review"
            ),
            [LifeOfferKeys.WholeLife] = new(
                Key:               LifeOfferKeys.WholeLife,
                DisplayName:       "Whole Life",
                HeroHeadline:      "Explore Whole Life Insurance Options",
                HeroSubheadline:   "Review permanent coverage options built around long-term protection and legacy goals.",
                FormSectionTitle:  "Start Your Whole Life Review",
                SubmitButtonText:  "See My Options",
                PageTitle:         "Whole Life Insurance Review"
            ),
            [LifeOfferKeys.Iul] = new(
                Key:               LifeOfferKeys.Iul,
                DisplayName:       "Indexed Universal Life (IUL)",
                HeroHeadline:      "Explore Indexed Universal Life Options",
                HeroSubheadline:   "Review protection strategies designed for long-term goals and cash value potential.",
                FormSectionTitle:  "Start Your IUL Review",
                SubmitButtonText:  "See My Options",
                PageTitle:         "Indexed Universal Life Review"
            ),
        };

        /// <summary>
        /// Normalizes raw offer param to a canonical key. Always returns a valid key (falls back to "life").
        /// </summary>
        public static string Normalize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return LifeOfferKeys.Life;
            return raw.Trim().ToLowerInvariant() switch
            {
                "life" or "life-insurance" or "lifeinsurance" or "general" => LifeOfferKeys.Life,
                "mortgage" or "mortgage-protection" or "mortgageprotection" or "mp" => LifeOfferKeys.Mortgage,
                "finalexpense" or "final-expense" or "final_expense" or "fe" or "burial" => LifeOfferKeys.FinalExpense,
                "term" or "term-life" or "termlife" or "term_life" => LifeOfferKeys.Term,
                "wholelife" or "whole-life" or "whole_life" or "wl" => LifeOfferKeys.WholeLife,
                "iul" or "indexed-universal-life" or "indexeduniversallife" or "indexed_universal_life" => LifeOfferKeys.Iul,
                _ => LifeOfferKeys.Life  // safe fallback for unknown values
            };
        }

        /// <summary>
        /// Returns the LifeOfferContent for the given raw offer param (or default life content).
        /// </summary>
        public static LifeOfferContent GetContent(string? raw)
        {
            var key = Normalize(raw);
            return Map.TryGetValue(key, out var content) ? content : Map[LifeOfferKeys.Life];
        }

        /// <summary>All supported offer variants in display order.</summary>
        public static IReadOnlyList<LifeOfferContent> AllVariants =>
            [Map[LifeOfferKeys.Life], Map[LifeOfferKeys.Mortgage], Map[LifeOfferKeys.FinalExpense],
             Map[LifeOfferKeys.Term], Map[LifeOfferKeys.WholeLife], Map[LifeOfferKeys.Iul]];
    }
}
