using System.Collections.Generic;

namespace Protect_Website.Models
{
    public sealed class LifeEstimateResult
    {
        public string PolicyKey { get; set; } = "";
        public string PolicyType { get; set; } = "";
        public int CoverageAmount { get; set; }
        public decimal EstimatedLowMonthly { get; set; }
        public decimal EstimatedHighMonthly { get; set; }
        public string RecommendationReason { get; set; } = "";
        public string Disclaimer { get; set; } = "";
        public IReadOnlyList<string> Reasons { get; set; } = new List<string>();
    }

    public sealed class LifeEstimatePreviewResponse
    {
        public LifeEstimateResult Primary { get; set; } = new();
        public LifeEstimateResult Secondary { get; set; } = new();
        public string AgeBand { get; set; } = "";
        public int RequestedCoverageAmount { get; set; }
        public string TobaccoUse { get; set; } = "";
        public string CoverageGoal { get; set; } = "";
        public string ProtectingWho { get; set; } = "";
        public string HealthAssumption { get; set; } = "Average Health";
        public string Disclaimer { get; set; } = "";
    }
}
