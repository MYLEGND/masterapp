using System;
using System.Collections.Generic;

namespace AgentPortal.Models
{
    public static class AdvancedMarketsLabels
    {
        private static readonly IReadOnlyDictionary<StrategyKind, string> _labels =
            new Dictionary<StrategyKind, string>
            {
                { StrategyKind.DefinedBenefit, "Defined Benefit" },
                { StrategyKind.CashBalance, "Cash Balance" },
                { StrategyKind.ComboDb401k, "Combo DB + 401(k)" },
                { StrategyKind.ExecutiveBonus162, "Executive Bonus 162" },
                { StrategyKind.DeferredComp, "Deferred Comp" },
                { StrategyKind.SplitDollar, "Split Dollar" },
                { StrategyKind.TaxDiversification, "Tax Diversification" }
            };

        public static string Display(StrategyKind kind)
        {
            return _labels.TryGetValue(kind, out var label) ? label : kind.ToString();
        }
    }
}
