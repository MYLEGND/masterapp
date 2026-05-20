using System;
using System.Collections.Generic;
using System.Globalization;
using Protect_Website.Models;

namespace ProtectWebsite.Services
{
    public static class LifeEstimateEngine
    {
        public const string EstimateDisclaimer = "Estimates are illustrative only and not a final quote. Actual pricing depends on underwriting, carrier approval, health history, state availability, and coverage details.";

        private sealed record BaseRate(int ReferenceCoverage, decimal LowMonthly, decimal HighMonthly);
        private sealed record PolicyPair(string PrimaryKey, string SecondaryKey);

        private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, BaseRate>> BaseRateTable =
            new Dictionary<string, IReadOnlyDictionary<string, BaseRate>>(StringComparer.OrdinalIgnoreCase)
            {
                ["term"] = new Dictionary<string, BaseRate>(StringComparer.OrdinalIgnoreCase)
                {
                    ["18-24"] = new(500000, 30m, 36m),
                    ["25-34"] = new(500000, 31m, 38m),
                    ["35-44"] = new(500000, 47m, 59m),
                    ["45-54"] = new(500000, 102m, 137m),
                    ["55-64"] = new(500000, 286m, 395m),
                    ["65+"] = new(500000, 430m, 600m),
                },
                ["wholelife"] = new Dictionary<string, BaseRate>(StringComparer.OrdinalIgnoreCase)
                {
                    ["18-24"] = new(100000, 51m, 58m),
                    ["25-34"] = new(100000, 66m, 77m),
                    ["35-44"] = new(100000, 96m, 114m),
                    ["45-54"] = new(100000, 148m, 179m),
                    ["55-64"] = new(100000, 249m, 309m),
                    ["65+"] = new(100000, 457m, 583m),
                },
                ["finalexpense"] = new Dictionary<string, BaseRate>(StringComparer.OrdinalIgnoreCase)
                {
                    ["18-24"] = new(10000, 16m, 22m),
                    ["25-34"] = new(10000, 18m, 24m),
                    ["35-44"] = new(10000, 22m, 29m),
                    ["45-54"] = new(10000, 30m, 38m),
                    ["55-64"] = new(10000, 42m, 54m),
                    ["65+"] = new(10000, 64m, 80m),
                },
                ["mortgage"] = new Dictionary<string, BaseRate>(StringComparer.OrdinalIgnoreCase)
                {
                    ["18-24"] = new(250000, 16m, 24m),
                    ["25-34"] = new(250000, 18m, 28m),
                    ["35-44"] = new(250000, 25m, 47m),
                    ["45-54"] = new(250000, 58m, 109m),
                    ["55-64"] = new(250000, 110m, 190m),
                    ["65+"] = new(250000, 175m, 300m),
                },
                ["iul"] = new Dictionary<string, BaseRate>(StringComparer.OrdinalIgnoreCase)
                {
                    ["18-24"] = new(500000, 170m, 210m),
                    ["25-34"] = new(500000, 238m, 271m),
                    ["35-44"] = new(500000, 341m, 398m),
                    ["45-54"] = new(500000, 447m, 606m),
                    ["55-64"] = new(500000, 842m, 1023m),
                    ["65+"] = new(500000, 1180m, 1450m),
                },
            };

        public static LifeEstimatePreviewResponse BuildPreview(LifeQuoteFormModel model, string? offerKey = null)
        {
            var normalizedOfferKey = LifeOfferResolver.Normalize(offerKey);
            var age = ResolveAge(model);
            var ageBand = ResolveAgeBand(age);
            var coverageGoal = NormalizeGoal(model.CoverageGoal);
            var protectingWho = NormalizeProtectingWho(model.ProtectingWho);
            var requestedCoverageAmount = ResolveRequestedCoverageAmount(model);
            var tobaccoUse = NormalizeTobaccoUse(model.TobaccoUse);
            var usesComparisonMode = string.Equals(normalizedOfferKey, LifeOfferKeys.Life, StringComparison.OrdinalIgnoreCase);
            LifeEstimateResult? secondary = null;
            LifeEstimateResult primary;

            if (usesComparisonMode)
            {
                var pair = ResolvePolicyPair(coverageGoal, protectingWho, age, requestedCoverageAmount);
                primary = BuildEstimate(pair.PrimaryKey, age, ageBand, coverageGoal, protectingWho, tobaccoUse, requestedCoverageAmount);
                secondary = BuildEstimate(pair.SecondaryKey, age, ageBand, coverageGoal, protectingWho, tobaccoUse, requestedCoverageAmount);
            }
            else
            {
                var primaryPolicyKey = ResolveOfferPolicyKey(normalizedOfferKey);
                primary = BuildEstimate(primaryPolicyKey, age, ageBand, coverageGoal, protectingWho, tobaccoUse, requestedCoverageAmount);
            }

            return new LifeEstimatePreviewResponse
            {
                Primary = primary,
                Secondary = secondary,
                OfferKey = normalizedOfferKey,
                DisplayMode = usesComparisonMode ? "comparison" : "single",
                AgeBand = ageBand,
                RequestedCoverageAmount = requestedCoverageAmount ?? primary.CoverageAmount,
                TobaccoUse = tobaccoUse,
                CoverageGoal = coverageGoal,
                ProtectingWho = protectingWho,
                HealthAssumption = "Average Health",
                Disclaimer = EstimateDisclaimer
            };
        }

        private static LifeEstimateResult BuildEstimate(
            string policyKey,
            int age,
            string ageBand,
            string coverageGoal,
            string protectingWho,
            string tobaccoUse,
            int? requestedCoverageAmount)
        {
            var coverageAmount = ResolveCoverageAmount(policyKey, coverageGoal, protectingWho, age, requestedCoverageAmount);
            var baseRate = BaseRateTable[policyKey][ageBand];
            var coverageMultiplier = ResolveCoverageMultiplier(policyKey, coverageAmount, baseRate.ReferenceCoverage);
            var tobaccoMultiplier = ResolveTobaccoMultiplier(policyKey, tobaccoUse);
            var healthMultiplier = 1.00m; // Average health assumption.

            var low = RoundMonthly(baseRate.LowMonthly * coverageMultiplier * tobaccoMultiplier * healthMultiplier);
            var high = RoundMonthly(baseRate.HighMonthly * coverageMultiplier * tobaccoMultiplier * healthMultiplier);
            if (high <= low)
            {
                high = low + 8m;
            }

            return new LifeEstimateResult
            {
                PolicyKey = policyKey,
                PolicyType = ResolvePolicyTitle(policyKey),
                CoverageAmount = coverageAmount,
                EstimatedLowMonthly = low,
                EstimatedHighMonthly = high,
                RecommendationReason = BuildReasonSummary(policyKey, coverageGoal, requestedCoverageAmount),
                Disclaimer = EstimateDisclaimer,
                Reasons = BuildReasons(policyKey, coverageGoal, protectingWho, age, requestedCoverageAmount)
            };
        }

        private static PolicyPair ResolvePolicyPair(string coverageGoal, string protectingWho, int age, int? requestedCoverageAmount)
        {
            var wantsSmallCoverage = requestedCoverageAmount.HasValue && requestedCoverageAmount.Value <= 100000;
            var wantsLargeCoverage = requestedCoverageAmount.HasValue && requestedCoverageAmount.Value >= 500000;

            if (string.Equals(coverageGoal, "replace_income", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(coverageGoal, "mortgage_or_bills", StringComparison.OrdinalIgnoreCase))
            {
                return new PolicyPair("term", "wholelife");
            }

            if (string.Equals(coverageGoal, "final_expenses", StringComparison.OrdinalIgnoreCase))
            {
                if (wantsLargeCoverage)
                {
                    return new PolicyPair("wholelife", "term");
                }

                return (age >= 50 || wantsSmallCoverage)
                    ? new PolicyPair("finalexpense", "wholelife")
                    : new PolicyPair("wholelife", "term");
            }

            if (string.Equals(coverageGoal, "leave_something", StringComparison.OrdinalIgnoreCase))
            {
                return age >= 65 && wantsSmallCoverage
                    ? new PolicyPair("wholelife", "finalexpense")
                    : new PolicyPair("wholelife", "term");
            }

            if (string.Equals(protectingWho, "children", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(protectingWho, "family", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(protectingWho, "spouse_or_partner", StringComparison.OrdinalIgnoreCase) ||
                wantsLargeCoverage)
            {
                return new PolicyPair("term", "wholelife");
            }

            if (age >= 60)
            {
                return new PolicyPair("wholelife", "term");
            }

            return new PolicyPair("term", "wholelife");
        }

        private static string ResolveOfferPolicyKey(string normalizedOfferKey)
        {
            return normalizedOfferKey switch
            {
                LifeOfferKeys.Term => "term",
                LifeOfferKeys.WholeLife => "wholelife",
                LifeOfferKeys.FinalExpense => "finalexpense",
                LifeOfferKeys.Mortgage => "mortgage",
                LifeOfferKeys.Iul => "iul",
                _ => "term"
            };
        }

        private static int ResolveCoverageAmount(string policyKey, string coverageGoal, string protectingWho, int age, int? requestedCoverageAmount)
        {
            if (requestedCoverageAmount.HasValue && requestedCoverageAmount.Value > 0)
            {
                if (string.Equals(policyKey, "finalexpense", StringComparison.OrdinalIgnoreCase))
                {
                    return Math.Min(requestedCoverageAmount.Value, 100000);
                }

                return requestedCoverageAmount.Value;
            }

            if (string.Equals(policyKey, "finalexpense", StringComparison.OrdinalIgnoreCase))
            {
                return age >= 65 ? 15000 : 20000;
            }

            if (string.Equals(policyKey, "wholelife", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(coverageGoal, "final_expenses", StringComparison.OrdinalIgnoreCase))
                    return 50000;
                if (string.Equals(coverageGoal, "leave_something", StringComparison.OrdinalIgnoreCase))
                    return 100000;
                return age >= 60 ? 50000 : 75000;
            }

            if (string.Equals(policyKey, "mortgage", StringComparison.OrdinalIgnoreCase))
            {
                return protectingWho switch
                {
                    "family" => 350000,
                    "children" => 350000,
                    "spouse_or_partner" => 300000,
                    _ => 250000
                };
            }

            if (string.Equals(policyKey, "iul", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(coverageGoal, "leave_something", StringComparison.OrdinalIgnoreCase))
                    return 250000;
                if (string.Equals(coverageGoal, "replace_income", StringComparison.OrdinalIgnoreCase))
                    return 250000;
                return protectingWho switch
                {
                    "family" => 250000,
                    "children" => 250000,
                    _ => 150000
                };
            }

            if (string.Equals(coverageGoal, "replace_income", StringComparison.OrdinalIgnoreCase))
            {
                return protectingWho switch
                {
                    "family" => 500000,
                    "children" => 500000,
                    "spouse_or_partner" => 400000,
                    "just_me" => 250000,
                    _ => 350000
                };
            }

            if (string.Equals(coverageGoal, "mortgage_or_bills", StringComparison.OrdinalIgnoreCase))
            {
                return protectingWho switch
                {
                    "family" => 350000,
                    "spouse_or_partner" => 300000,
                    "just_me" => 200000,
                    _ => 250000
                };
            }

            if (string.Equals(coverageGoal, "leave_something", StringComparison.OrdinalIgnoreCase))
            {
                return 250000;
            }

            return protectingWho switch
            {
                "family" => 350000,
                "children" => 350000,
                _ => 250000
            };
        }

        private static IReadOnlyList<string> BuildReasons(string policyKey, string coverageGoal, string protectingWho, int age, int? requestedCoverageAmount)
        {
            var reasons = new List<string>();
            var wantsLargeCoverage = requestedCoverageAmount.HasValue && requestedCoverageAmount.Value >= 500000;
            var wantsSmallCoverage = requestedCoverageAmount.HasValue && requestedCoverageAmount.Value <= 100000;

            if (string.Equals(policyKey, "term", StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add(string.Equals(coverageGoal, "mortgage_or_bills", StringComparison.OrdinalIgnoreCase)
                    ? "May fit when protecting mortgage years or monthly bills is the priority."
                    : "May fit when protecting income, family needs, or larger temporary responsibilities matters most.");
                reasons.Add(wantsLargeCoverage
                    ? "Often the cleanest way to explore larger coverage amounts before comparing permanent designs."
                    : "Estimated coverage can often go further at a lower monthly cost than permanent coverage.");
                reasons.Add(string.Equals(protectingWho, "family", StringComparison.OrdinalIgnoreCase) || string.Equals(protectingWho, "children", StringComparison.OrdinalIgnoreCase)
                    ? "Often worth considering when multiple people depend on your income or support."
                    : "Often reviewed first when straightforward protection is the main goal.");
                return reasons;
            }

            if (string.Equals(policyKey, "finalexpense", StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("May fit when the goal is helping with burial, funeral, or other final costs.");
                reasons.Add(wantsSmallCoverage
                    ? "Keeps the estimate focused on a smaller permanent coverage need."
                    : "May be worth considering when a modest permanent benefit matters more than a larger face amount.");
                reasons.Add(age >= 55
                    ? "Often worth considering later in life when simplicity matters more than larger coverage amounts."
                    : "May be worth considering when permanent coverage matters more than maximizing face amount.");
                return reasons;
            }

            if (string.Equals(policyKey, "mortgage", StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("May fit when keeping your family in the home is the priority.");
                reasons.Add("Keeps the estimate focused on mortgage balance coverage or key household obligations.");
                reasons.Add(age >= 55
                    ? "Often worth reviewing when protecting payment continuity matters more than maximizing extra coverage."
                    : "Often reviewed when protecting mortgage years and household stability matters most.");
                return reasons;
            }

            if (string.Equals(policyKey, "iul", StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("May fit when lifelong protection and cash value flexibility are both priorities.");
                reasons.Add(wantsLargeCoverage
                    ? "Can illustrate what a larger long-term protection target may look like with a flexible cash value design."
                    : "Keeps the estimate centered on protection plus flexible long-term value growth potential.");
                reasons.Add(string.Equals(coverageGoal, "leave_something", StringComparison.OrdinalIgnoreCase)
                    ? "Often reviewed in legacy planning conversations where long-range value and access matter."
                    : "Often reviewed when long-term growth potential and access to cash value matter.");
                return reasons;
            }

            reasons.Add(string.Equals(coverageGoal, "leave_something", StringComparison.OrdinalIgnoreCase)
                ? "May fit when lifelong protection or leaving something behind matters most."
                : "May fit when permanent protection is worth considering alongside lower-cost term coverage.");
            reasons.Add(wantsLargeCoverage
                ? "Can illustrate what a larger permanent protection target may look like before a personalized review."
                : "Designed for a smaller long-term protection need rather than the largest possible face amount.");
            reasons.Add(age >= 60
                ? "Often reviewed when keeping coverage in place long term matters more than simply lowering monthly cost."
                : "May be worth considering when you want a lifelong option in addition to temporary protection.");
            return reasons;
        }

        private static string BuildReasonSummary(string policyKey, string coverageGoal, int? requestedCoverageAmount)
        {
            var wantsLargeCoverage = requestedCoverageAmount.HasValue && requestedCoverageAmount.Value >= 500000;
            return policyKey switch
            {
                "mortgage" =>
                    "Mortgage protection may fit when the priority is keeping the home secure and covering mortgage years.",
                "term" when string.Equals(coverageGoal, "mortgage_or_bills", StringComparison.OrdinalIgnoreCase) =>
                    "Term life may fit when the priority is protecting mortgage years or monthly obligations.",
                "term" when wantsLargeCoverage =>
                    "Term life may fit when you want to explore higher coverage amounts with a lower estimated monthly starting point.",
                "term" =>
                    "Term life may fit when you want broader protection at a lower estimated monthly cost.",
                "finalexpense" =>
                    "Final expense coverage may fit when the goal is a smaller permanent benefit for burial or end-of-life costs.",
                "iul" =>
                    "Indexed universal life may fit when you want long-term protection with flexible cash value growth potential.",
                _ =>
                    "Whole life may fit when lifelong protection or a permanent coverage path is worth considering."
            };
        }

        private static decimal ResolveCoverageMultiplier(string policyKey, int coverageAmount, int referenceCoverage)
        {
            var ratio = Math.Max(0.5d, (double)coverageAmount / Math.Max(1, referenceCoverage));
            var exponent = policyKey switch
            {
                "term" => 0.80d,
                "mortgage" => 0.80d,
                "finalexpense" => 0.90d,
                "iul" => 0.97d,
                _ => 0.96d
            };
            return (decimal)Math.Pow(ratio, exponent);
        }

        private static decimal ResolveTobaccoMultiplier(string policyKey, string tobaccoUse)
        {
            if (!string.Equals(tobaccoUse, "smoker", StringComparison.OrdinalIgnoreCase))
            {
                return 1.00m;
            }

            return policyKey switch
            {
                "term" => 3.00m,
                "mortgage" => 2.75m,
                "wholelife" => 1.45m,
                "finalexpense" => 1.28m,
                "iul" => 1.68m,
                _ => 1.50m
            };
        }

        private static int? ResolveRequestedCoverageAmount(LifeQuoteFormModel model)
        {
            if (model.CoverageAmount.HasValue && model.CoverageAmount.Value > 0)
            {
                return model.CoverageAmount.Value;
            }

            if (int.TryParse(model.CoverageAmountOption, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCoverageAmount) &&
                parsedCoverageAmount > 0)
            {
                return parsedCoverageAmount;
            }

            return null;
        }

        private static decimal RoundMonthly(decimal amount)
        {
            var rounded = Math.Round(amount, 0, MidpointRounding.AwayFromZero);
            return rounded < 10m ? 10m : rounded;
        }

        private static int ResolveAge(LifeQuoteFormModel model)
        {
            if (model.Age.HasValue && model.Age.Value >= 18)
                return model.Age.Value;

            var ageRange = (model.AgeRange ?? "").Trim();
            return ageRange switch
            {
                "18-24" => 21,
                "25-34" => 30,
                "35-44" => 40,
                "45-54" => 50,
                "55-64" => 60,
                "65-74" => 70,
                "75+" => 78,
                "55+" => 60,
                _ when int.TryParse(ageRange, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRangeAge) => parsedRangeAge,
                _ when int.TryParse(model.Answer4, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedAnswerAge) => parsedAnswerAge,
                _ => 35
            };
        }

        private static string ResolveAgeBand(int age)
        {
            return age switch
            {
                <= 24 => "18-24",
                <= 34 => "25-34",
                <= 44 => "35-44",
                <= 54 => "45-54",
                <= 64 => "55-64",
                _ => "65+"
            };
        }

        private static string NormalizeGoal(string? value)
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "not_sure" : value.Trim().ToLowerInvariant();
            return normalized switch
            {
                "protect_term_years" => "replace_income",
                "keep_costs_affordable" => "replace_income",
                "burial_costs" => "final_expenses",
                "final_bills" => "final_expenses",
                "ease_family_burden" => "final_expenses",
                "mortgage_balance" => "mortgage_or_bills",
                "monthly_payment" => "mortgage_or_bills",
                "stay_in_home" => "mortgage_or_bills",
                "household_bills" => "mortgage_or_bills",
                "lifelong_protection" => "leave_something",
                "cash_value_growth" => "leave_something",
                "future_access" => "leave_something",
                "leave_legacy" => "leave_something",
                "leave_small_benefit" => "leave_something",
                _ => normalized
            };
        }

        private static string NormalizeProtectingWho(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? "not_sure" : value.Trim().ToLowerInvariant();
        }

        private static string NormalizeTobaccoUse(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "non_smoker";

            return value.Trim().ToLowerInvariant() switch
            {
                "smoker" => "smoker",
                _ => "non_smoker"
            };
        }

        private static string ResolvePolicyTitle(string policyKey)
        {
            return policyKey switch
            {
                "term" => "Term Life Insurance",
                "wholelife" => "Whole Life Insurance",
                "finalexpense" => "Final Expense Insurance",
                "mortgage" => "Mortgage Protection Insurance",
                "iul" => "Indexed Universal Life (IUL)",
                _ => "Life Insurance"
            };
        }
    }
}
