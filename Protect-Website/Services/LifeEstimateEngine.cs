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
        private sealed record AgeRateAnchor(int Age, int ReferenceCoverage, decimal LowMonthly, decimal HighMonthly);
        private sealed record PolicyPair(string PrimaryKey, string SecondaryKey);

        // We keep the same reference rate inputs, but anchor them to approximate ages and
        // interpolate between those points so visitors see a result that changes with the
        // exact age they entered instead of a wide age bucket.
        private const double TailGrowthDampingFactor = 0.75d;

        private static readonly IReadOnlyDictionary<string, IReadOnlyList<AgeRateAnchor>> BaseRateTable =
            new Dictionary<string, IReadOnlyList<AgeRateAnchor>>(StringComparer.OrdinalIgnoreCase)
            {
                ["term"] = new[]
                {
                    new AgeRateAnchor(21, 500000, 30m, 36m),
                    new AgeRateAnchor(30, 500000, 31m, 38m),
                    new AgeRateAnchor(40, 500000, 47m, 59m),
                    new AgeRateAnchor(50, 500000, 102m, 137m),
                    new AgeRateAnchor(60, 500000, 286m, 395m),
                    new AgeRateAnchor(70, 500000, 430m, 600m),
                },
                ["wholelife"] = new[]
                {
                    new AgeRateAnchor(21, 100000, 51m, 58m),
                    new AgeRateAnchor(30, 100000, 66m, 77m),
                    new AgeRateAnchor(40, 100000, 96m, 114m),
                    new AgeRateAnchor(50, 100000, 148m, 179m),
                    new AgeRateAnchor(60, 100000, 249m, 309m),
                    new AgeRateAnchor(70, 100000, 457m, 583m),
                },
                ["finalexpense"] = new[]
                {
                    new AgeRateAnchor(21, 10000, 16m, 22m),
                    new AgeRateAnchor(30, 10000, 18m, 24m),
                    new AgeRateAnchor(40, 10000, 22m, 29m),
                    new AgeRateAnchor(50, 10000, 30m, 38m),
                    new AgeRateAnchor(60, 10000, 42m, 54m),
                    new AgeRateAnchor(70, 10000, 64m, 80m),
                },
                ["mortgage"] = new[]
                {
                    new AgeRateAnchor(21, 250000, 16m, 24m),
                    new AgeRateAnchor(30, 250000, 18m, 28m),
                    new AgeRateAnchor(40, 250000, 25m, 47m),
                    new AgeRateAnchor(50, 250000, 58m, 109m),
                    new AgeRateAnchor(60, 250000, 110m, 190m),
                    new AgeRateAnchor(70, 250000, 175m, 300m),
                },
                ["iul"] = new[]
                {
                    new AgeRateAnchor(21, 500000, 170m, 210m),
                    new AgeRateAnchor(30, 500000, 238m, 271m),
                    new AgeRateAnchor(40, 500000, 341m, 398m),
                    new AgeRateAnchor(50, 500000, 447m, 606m),
                    new AgeRateAnchor(60, 500000, 842m, 1023m),
                    new AgeRateAnchor(70, 500000, 1180m, 1450m),
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
                primary = BuildEstimate(pair.PrimaryKey, age, coverageGoal, protectingWho, tobaccoUse, requestedCoverageAmount, usesComparisonMode);
                secondary = BuildEstimate(pair.SecondaryKey, age, coverageGoal, protectingWho, tobaccoUse, requestedCoverageAmount, usesComparisonMode);
            }
            else
            {
                var primaryPolicyKey = ResolveOfferPolicyKey(normalizedOfferKey);
                primary = BuildEstimate(primaryPolicyKey, age, coverageGoal, protectingWho, tobaccoUse, requestedCoverageAmount, usesComparisonMode);
            }

            return new LifeEstimatePreviewResponse
            {
                Primary = primary,
                Secondary = secondary,
                OfferKey = normalizedOfferKey,
                DisplayMode = usesComparisonMode ? "comparison" : "single",
                Age = age,
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
            string coverageGoal,
            string protectingWho,
            string tobaccoUse,
            int? requestedCoverageAmount,
            bool usesComparisonMode)
        {
            var coverageAmount = ResolveCoverageAmount(policyKey, coverageGoal, protectingWho, age, requestedCoverageAmount);
            var baseRate = ResolveBaseRate(policyKey, age);
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
                RecommendationReason = BuildReasonSummary(policyKey, coverageGoal, requestedCoverageAmount, usesComparisonMode),
                Disclaimer = EstimateDisclaimer,
                Reasons = BuildReasons(policyKey, coverageGoal, protectingWho, age, requestedCoverageAmount, usesComparisonMode)
            };
        }

        private static BaseRate ResolveBaseRate(string policyKey, int age)
        {
            var curve = BaseRateTable[policyKey];
            if (curve.Count == 0)
            {
                throw new InvalidOperationException($"No base-rate anchors configured for policy '{policyKey}'.");
            }

            if (curve.Count == 1)
            {
                var onlyAnchor = curve[0];
                return new BaseRate(onlyAnchor.ReferenceCoverage, onlyAnchor.LowMonthly, onlyAnchor.HighMonthly);
            }

            if (age <= curve[0].Age)
            {
                return InterpolateBaseRate(curve[0], curve[1], age);
            }

            for (var index = 1; index < curve.Count; index++)
            {
                if (age <= curve[index].Age)
                {
                    return InterpolateBaseRate(curve[index - 1], curve[index], age);
                }
            }

            return ExtrapolateTailBaseRate(curve[curve.Count - 2], curve[curve.Count - 1], age);
        }

        private static BaseRate InterpolateBaseRate(AgeRateAnchor lower, AgeRateAnchor upper, int age)
        {
            var span = Math.Max(1, upper.Age - lower.Age);
            var progress = (double)(age - lower.Age) / span;

            return new BaseRate(
                upper.ReferenceCoverage,
                InterpolateMonthly(lower.LowMonthly, upper.LowMonthly, progress),
                InterpolateMonthly(lower.HighMonthly, upper.HighMonthly, progress));
        }

        private static BaseRate ExtrapolateTailBaseRate(AgeRateAnchor lower, AgeRateAnchor upper, int age)
        {
            var yearsBeyondUpper = Math.Max(0, age - upper.Age);
            var lowAnnualLogGrowth = ResolveAnnualLogGrowth(lower.LowMonthly, upper.LowMonthly, lower.Age, upper.Age) * TailGrowthDampingFactor;
            var highAnnualLogGrowth = ResolveAnnualLogGrowth(lower.HighMonthly, upper.HighMonthly, lower.Age, upper.Age) * TailGrowthDampingFactor;

            return new BaseRate(
                upper.ReferenceCoverage,
                (decimal)((double)upper.LowMonthly * Math.Exp(lowAnnualLogGrowth * yearsBeyondUpper)),
                (decimal)((double)upper.HighMonthly * Math.Exp(highAnnualLogGrowth * yearsBeyondUpper)));
        }

        private static decimal InterpolateMonthly(decimal start, decimal end, double progress)
        {
            var startValue = Math.Max(0.01d, (double)start);
            var endValue = Math.Max(0.01d, (double)end);
            var interpolated = startValue * Math.Exp((Math.Log(endValue) - Math.Log(startValue)) * progress);
            return decimal.Round((decimal)interpolated, 2, MidpointRounding.AwayFromZero);
        }

        private static double ResolveAnnualLogGrowth(decimal start, decimal end, int startAge, int endAge)
        {
            var safeStart = Math.Max(0.01d, (double)start);
            var safeEnd = Math.Max(0.01d, (double)end);
            var years = Math.Max(1, endAge - startAge);
            return Math.Log(safeEnd / safeStart) / years;
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

        private static IReadOnlyList<string> BuildReasons(string policyKey, string coverageGoal, string protectingWho, int age, int? requestedCoverageAmount, bool usesComparisonMode)
        {
            var reasons = new List<string>();
            var wantsLargeCoverage = requestedCoverageAmount.HasValue && requestedCoverageAmount.Value >= 500000;
            var wantsSmallCoverage = requestedCoverageAmount.HasValue && requestedCoverageAmount.Value <= 100000;

            if (string.Equals(policyKey, "term", StringComparison.OrdinalIgnoreCase))
            {
                if (usesComparisonMode)
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
                }
                else
                {
                    reasons.Add(string.Equals(coverageGoal, "mortgage_or_bills", StringComparison.OrdinalIgnoreCase)
                        ? "May fit when protecting mortgage years or monthly bills is the priority."
                        : "May fit when protecting income, family needs, or other time-bound responsibilities matters most.");
                    reasons.Add(wantsLargeCoverage
                        ? "Often used to explore larger temporary protection amounts while keeping the structure straightforward."
                        : "Keeps the estimate centered on straightforward temporary coverage for the years you want protected most.");
                    reasons.Add(string.Equals(protectingWho, "family", StringComparison.OrdinalIgnoreCase) || string.Equals(protectingWho, "children", StringComparison.OrdinalIgnoreCase)
                        ? "Often reviewed when multiple people depend on your income or support."
                        : "Often reviewed when you want a clear, practical protection window.");
                }
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

            if (usesComparisonMode)
            {
                reasons.Add(string.Equals(coverageGoal, "leave_something", StringComparison.OrdinalIgnoreCase)
                    ? "May fit when lifelong protection or leaving something behind matters most."
                    : "May fit when permanent protection is worth considering alongside lower-cost term coverage.");
                reasons.Add(wantsLargeCoverage
                    ? "Can illustrate what a larger permanent protection target may look like before a personalized review."
                    : "Designed for a smaller long-term protection need rather than the largest possible face amount.");
                reasons.Add(age >= 60
                    ? "Often reviewed when keeping coverage in place long term matters more than simply lowering monthly cost."
                    : "May be worth considering when you want a lifelong option in addition to temporary protection.");
            }
            else
            {
                reasons.Add(string.Equals(coverageGoal, "leave_something", StringComparison.OrdinalIgnoreCase)
                    ? "May fit when lifelong protection or leaving something behind matters most."
                    : "May fit when keeping protection in place for the long term is the priority.");
                reasons.Add(wantsLargeCoverage
                    ? "Can illustrate what a larger lifelong protection target may look like before a personalized review."
                    : "Keeps the estimate centered on permanent coverage designed to stay in place for the long run.");
                reasons.Add(age >= 60
                    ? "Often reviewed when stability and lifelong coverage matter more than short-term flexibility."
                    : "Often explored when fixed lifelong coverage matters more than short-term flexibility.");
            }
            return reasons;
        }

        private static string BuildReasonSummary(string policyKey, string coverageGoal, int? requestedCoverageAmount, bool usesComparisonMode)
        {
            var wantsLargeCoverage = requestedCoverageAmount.HasValue && requestedCoverageAmount.Value >= 500000;
            return policyKey switch
            {
                "mortgage" =>
                    "Mortgage protection may fit when the priority is keeping the home secure and covering mortgage years.",
                "term" when string.Equals(coverageGoal, "mortgage_or_bills", StringComparison.OrdinalIgnoreCase) =>
                    "Term life may fit when the priority is protecting mortgage years or monthly obligations.",
                "term" when !usesComparisonMode && wantsLargeCoverage =>
                    "Term life may fit when you want to explore higher coverage amounts for the years protection matters most.",
                "term" when wantsLargeCoverage =>
                    "Term life may fit when you want to explore higher coverage amounts with a lower estimated monthly starting point.",
                "term" when !usesComparisonMode =>
                    "Term life may fit when you want protection for the years income, mortgage, or family responsibilities matter most.",
                "term" =>
                    "Term life may fit when you want broader protection at a lower estimated monthly cost.",
                "finalexpense" =>
                    "Final expense coverage may fit when the goal is a smaller permanent benefit for burial or end-of-life costs.",
                "iul" =>
                    "Indexed universal life may fit when you want long-term protection with flexible cash value growth potential.",
                "wholelife" when !usesComparisonMode && string.Equals(coverageGoal, "leave_something", StringComparison.OrdinalIgnoreCase) =>
                    "Whole life may fit when lifelong protection, steady premiums, and leaving something behind are part of the goal.",
                "wholelife" when !usesComparisonMode =>
                    "Whole life may fit when lifelong protection and a permanent coverage path are the focus.",
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
