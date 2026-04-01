using System;
using System.Collections.Generic;

namespace AgentPortal.Models;

public class AdvancedMarketsPageViewModel
{
    public StrategySelectionVm Strategy { get; set; } = new();
    public ClientProfileVm Client { get; set; } = new();
    public BusinessProfileVm Business { get; set; } = new();
    public TaxAssumptionsVm Tax { get; set; } = new();
    public ProjectionInputsVm Projection { get; set; } = new();
    public DefinedBenefitInputsVm DefinedBenefit { get; set; } = new();
    public CashBalanceInputsVm CashBalance { get; set; } = new();
    public ComboPlanInputsVm Combo { get; set; } = new();
    public ExecutiveBonusInputsVm ExecutiveBonus { get; set; } = new();
    public DeferredCompInputsVm DeferredComp { get; set; } = new();
    public SplitDollarInputsVm SplitDollar { get; set; } = new();
    public AdvancedMarketsResultVm? Result { get; set; }
    public List<SuitabilityFlagVm> Suitability { get; set; } = new();
    public List<ComparisonRowVm> Comparison { get; set; } = new();
    public IllustrationSummaryVm? Summary { get; set; }
}

public enum StrategyKind
{
    DefinedBenefit,
    CashBalance,
    ComboDb401k,
    ExecutiveBonus162,
    DeferredComp,
    SplitDollar,
    TaxDiversification
}

public class StrategySelectionVm
{
    public StrategyKind Selected { get; set; } = StrategyKind.DefinedBenefit;
    public string? Sensitivity { get; set; } = "Base"; // Conservative | Base | Optimistic
}

public class ClientProfileVm
{
    public string? ClientName { get; set; }
    public string? HouseholdName { get; set; }
    public int? OwnerAge { get; set; }
    public int? SpouseAge { get; set; }
    public int? RetirementAge { get; set; }
    public string? State { get; set; }
    public string? BusinessType { get; set; }
    public string? IncomeType { get; set; }
    public List<string> Objectives { get; set; } = new();
    public double? CurrentQualifiedAssets { get; set; }
    public double? CurrentTaxableAssets { get; set; }
    public double? CurrentTaxFreeAssets { get; set; }
}

public class BusinessProfileVm
{
    public string? EntityType { get; set; }
    public double? AnnualBusinessIncome { get; set; }
    public double? OwnerComp { get; set; }
    public int? EmployeeCount { get; set; }
    public int? EligibleEmployeeCount { get; set; }
    public int? AverageEmployeeAge { get; set; }
    public double? AverageEmployeeComp { get; set; }
    public double? OwnershipPct { get; set; }
    public string? CurrentPlanType { get; set; }
    public double? CurrentEmployerRetirementContributions { get; set; }
    public double? CurrentBenefitCosts { get; set; }
}

public class TaxAssumptionsVm
{
    public double? FederalRate { get; set; }
    public double? StateRate { get; set; }
    public double? CapitalGainsRate { get; set; }
    public double? FutureTaxRate { get; set; }
    public string? Mode { get; set; } = "Simplified";
}

public class ProjectionInputsVm
{
    public double? CurrentAssets { get; set; }
    public double? AnnualSavings { get; set; }
    public double? GrowthRate { get; set; }
    public double? InflationRate { get; set; }
    public int? RetirementDurationYears { get; set; }
    public double? DistributionRate { get; set; }
    public double? DiscountRate { get; set; }
}

public class DefinedBenefitInputsVm
{
    public double? AnnualIncome { get; set; }
    public double? TargetContribution { get; set; }
    public double? TargetBenefit { get; set; }
    public double? AdminCost { get; set; }
    public double? EmployeeCostFactor { get; set; }
    public double? GrowthRate { get; set; }
    public double? InflationRate { get; set; }
    public bool IncludeSpouse { get; set; }
    public int? SpouseAge { get; set; }
    public double? SpouseContribution { get; set; }
}

public class CashBalanceInputsVm
{
    public double? Current401kDeferral { get; set; }
    public double? EmployerProfitSharing { get; set; }
    public double? DesiredTotalContribution { get; set; }
    public double? AdminCost { get; set; }
    public double? GrowthRate { get; set; }
    public double? PayCreditPct { get; set; }
    public double? InterestCreditPct { get; set; }
    public double? EmployeeCostFactor { get; set; }
}

public class ComboPlanInputsVm
{
    public double? EmployeeDeferral { get; set; }
    public bool CatchUp { get; set; }
    public double? EmployerPct { get; set; }
    public double? ProfitSharingPct { get; set; }
    public double? SafeHarborPct { get; set; }
    public double? TargetTotal { get; set; }
    public double? EmployeeCostFactor { get; set; }
    public double? TestingBufferPct { get; set; }
}

public class ExecutiveBonusInputsVm
{
    public double? AnnualBonus { get; set; }
    public int? YearsFunded { get; set; }
    public double? PolicyGrowthRate { get; set; }
    public double? DeathBenefitMultiple { get; set; }
    public double? AdminCost { get; set; }
}

public class DeferredCompInputsVm
{
    public double? DeferralAmount { get; set; }
    public int? DeferralYears { get; set; }
    public int? DistributionStartAge { get; set; }
    public int? DistributionYears { get; set; }
    public double? GrowthRate { get; set; }
    public double? CurrentTaxRate { get; set; }
    public double? FutureTaxRate { get; set; }
}

public class SplitDollarInputsVm
{
    public double? AnnualPremium { get; set; }
    public int? FundingYears { get; set; }
    public double? GrowthRate { get; set; }
    public double? DeathBenefit { get; set; }
    public int? ExitYear { get; set; }
}

public class AdvancedMarketsResultVm
{
    public double EstimatedAnnualContribution { get; set; }
    public double EstimatedDeduction { get; set; }
    public string DeductionLabel { get; set; } = "";
    public double FederalTaxSavings { get; set; }
    public double StateTaxSavings { get; set; }
    public double CombinedTaxSavings { get; set; }
    public double NetAnnualCost { get; set; }
    public double ProjectedRetirementValue { get; set; }
    public double ProjectedRetirementIncome { get; set; }
    public double EstimatedLegacyValue { get; set; }
    public double EmployeeCostImpact { get; set; }
    public double EffectiveGrowthRate { get; set; }
    public double EffectiveDistributionRate { get; set; }
    public double CombinedTaxRate { get; set; }
    public string IncomeMethod { get; set; } = "Sustainable draw";
    public List<SuitabilityFlagVm> Suitability { get; set; } = new();
    public List<ComparisonRowVm> Comparison { get; set; } = new();
    public IllustrationSummaryVm Summary { get; set; } = new();
    public List<ChartSeriesVm> Charts { get; set; } = new();
    public List<MetricVm> StrategyMetrics { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    // Optional informational metrics that don't drive core math (e.g., 401k deferral shown in CB)
    public List<MetricVm> InformationalMetrics { get; set; } = new();
}

public class ComparisonRowVm
{
    public string Label { get; set; } = "";
    public double Current { get; set; }
    public double Proposed { get; set; }
    public double Difference => Proposed - Current;
}

public class SuitabilityFlagVm
{
    public string Severity { get; set; } = "info"; // info | warn | bad | good
    public string Message { get; set; } = "";
}

public class IllustrationSummaryVm
{
    public string Headline { get; set; } = "";
    public string Subhead { get; set; } = "";
    public List<string> TalkingPoints { get; set; } = new();
    public List<string> Assumptions { get; set; } = new();
    public List<string> Disclaimers { get; set; } = new();
}

public class ChartSeriesVm
{
    public string Name { get; set; } = "";
    public List<double> Data { get; set; } = new();
    public List<string> Labels { get; set; } = new();
}

public class MetricVm
{
    public string Label { get; set; } = "";
    public double Value { get; set; }
    public string? Note { get; set; }
    public string Format { get; set; } = "currency"; // currency | percent | number | integer | year
}
