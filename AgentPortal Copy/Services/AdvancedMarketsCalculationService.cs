using System;
using System.Collections.Generic;
using System.Linq;
using AgentPortal.Models;

namespace AgentPortal.Services;

public interface IAdvancedMarketsCalculationService
{
    AdvancedMarketsResultVm Calculate(AdvancedMarketsPageViewModel request);
}

public class AdvancedMarketsCalculationService : IAdvancedMarketsCalculationService
{
    public AdvancedMarketsResultVm Calculate(AdvancedMarketsPageViewModel request)
    {
        NormalizeDefaults(request);

        if (request.Strategy.Selected == StrategyKind.DefinedBenefit)
        {
            var dbResult = DefinedBenefitCalculator.Calculate(request);

            var suitabilityDb = SuitabilityEngine.Build(request, dbResult.Contribution, dbResult.EmployeeCost, dbResult.FundYears);
            var comparisonDb = BuildComparison(
                request,
                dbResult.Deductible,
                dbResult.CombinedTaxSavings,
                dbResult.NetCost,
                dbResult.ProjectedValueTotal,
                dbResult.RetirementIncome,
                dbResult.Legacy,
                dbResult.CombinedTaxRate,
                dbResult.BaselineValue,
                dbResult.BaselineIncome,
                dbResult.BaselineLegacy);

            var summaryDb = BuildSummary(
                request,
                dbResult.Contribution,
                dbResult.Deductible,
                dbResult.CombinedTaxSavings,
                dbResult.NetCost,
                dbResult.EffectiveGrowth,
                dbResult.EffectiveDistribution,
                dbResult.CombinedTaxRate,
                SensitivityProfile.From(request.Strategy.Sensitivity));

            var chartsDb = ChartBuilder.BuildCharts(dbResult.FundYears, dbResult.CombinedTaxSavings, dbResult.EffectiveGrowth, dbResult.RetirementIncome, request, dbResult.FvCurrent, dbResult.FvDb, dbResult.FvOutside, dbResult.Contribution, dbResult.EffectiveGrowth);

            var strategyMetricsDb = StrategyMetricsBuilder.BuildDefinedBenefit(dbResult);

            return new AdvancedMarketsResultVm
            {
                EstimatedAnnualContribution = dbResult.Contribution,
                EstimatedDeduction = dbResult.Deductible,
                DeductionLabel = "Illustrative deductible contribution (subject to IRS/actuarial limits)",
                FederalTaxSavings = dbResult.FederalTaxSavings,
                StateTaxSavings = dbResult.StateTaxSavings,
                CombinedTaxSavings = dbResult.CombinedTaxSavings,
                NetAnnualCost = dbResult.NetCost,
                ProjectedRetirementValue = dbResult.ProjectedValueTotal,
                ProjectedRetirementIncome = dbResult.RetirementIncome,
                EstimatedLegacyValue = dbResult.Legacy,
                EmployeeCostImpact = dbResult.EmployeeCost,
                EffectiveGrowthRate = dbResult.EffectiveGrowth,
                EffectiveDistributionRate = dbResult.EffectiveDistribution,
                CombinedTaxRate = dbResult.CombinedTaxRate,
                IncomeMethod = "Modeled retirement draw from projected accumulation; not an actuarial pension benefit estimate.",
                Suitability = suitabilityDb,
                Comparison = comparisonDb,
                Summary = summaryDb,
                Charts = chartsDb,
                StrategyMetrics = strategyMetricsDb,
                Warnings = dbResult.Warnings
            };
        }

        if (request.Strategy.Selected == StrategyKind.CashBalance)
        {
            var cbResult = CashBalanceCalculator.Calculate(request);
            var suitabilityCb = SuitabilityEngine.Build(request, cbResult.Contribution, cbResult.EmployeeCost, cbResult.FundYears);
            var comparisonCb = BuildComparison(
                request,
                cbResult.Deductible,
                cbResult.CombinedTaxSavings,
                cbResult.NetCost,
                cbResult.ProjectedValueTotal,
                cbResult.RetirementIncome,
                cbResult.Legacy,
                cbResult.CombinedTaxRate,
                cbResult.BaselineValue,
                cbResult.BaselineIncome,
                cbResult.BaselineLegacy);

            var summaryCb = BuildSummary(
                request,
                cbResult.Contribution,
                cbResult.Deductible,
                cbResult.CombinedTaxSavings,
                cbResult.NetCost,
                cbResult.EffectiveGrowth,
                cbResult.EffectiveDistribution,
                cbResult.CombinedTaxRate,
                SensitivityProfile.From(request.Strategy.Sensitivity));

            var chartsCb = ChartBuilder.BuildCharts(cbResult.FundYears, cbResult.CombinedTaxSavings, cbResult.EffectiveGrowth, cbResult.RetirementIncome, request, cbResult.FvCurrent, cbResult.FvCb, cbResult.FvOutside, cbResult.Contribution, cbResult.EffectiveGrowth);

            var (metricsCb, infoCb) = StrategyMetricsBuilder.BuildCashBalance(cbResult, request);

            return new AdvancedMarketsResultVm
            {
                EstimatedAnnualContribution = cbResult.Contribution,
                EstimatedDeduction = cbResult.Deductible,
                DeductionLabel = "Illustrative CB deductible contribution (subject to plan design/testing)",
                FederalTaxSavings = cbResult.FederalTaxSavings,
                StateTaxSavings = cbResult.StateTaxSavings,
                CombinedTaxSavings = cbResult.CombinedTaxSavings,
                NetAnnualCost = cbResult.NetCost,
                ProjectedRetirementValue = cbResult.ProjectedValueTotal,
                ProjectedRetirementIncome = cbResult.RetirementIncome,
                EstimatedLegacyValue = cbResult.Legacy,
                EmployeeCostImpact = cbResult.EmployeeCost,
                EffectiveGrowthRate = cbResult.EffectiveGrowth,
                EffectiveDistributionRate = cbResult.EffectiveDistribution,
                CombinedTaxRate = cbResult.CombinedTaxRate,
                IncomeMethod = "Modeled retirement draw from projected accumulation; not an actuarial pension benefit estimate.",
                Suitability = suitabilityCb,
                Comparison = comparisonCb,
                Summary = summaryCb,
                Charts = chartsCb,
                StrategyMetrics = metricsCb,
                Warnings = cbResult.Warnings,
                InformationalMetrics = infoCb
            };
        }

        if (request.Strategy.Selected == StrategyKind.ComboDb401k)
        {
            var combo = ComboDb401kCalculator.Calculate(request);
            var suitabilityCombo = SuitabilityEngine.Build(request, combo.Contribution, combo.EmployeeCost, combo.FundYears);
            var comparisonCombo = BuildComparison(
                request,
                combo.Deductible,
                combo.CombinedTaxSavings,
                combo.NetCost,
                combo.ProjectedValueTotal,
                combo.RetirementIncome,
                combo.Legacy,
                combo.CombinedTaxRate,
                combo.BaselineValue,
                combo.BaselineIncome,
                combo.BaselineLegacy);

            var summaryCombo = BuildSummary(
                request,
                combo.Contribution,
                combo.Deductible,
                combo.CombinedTaxSavings,
                combo.NetCost,
                combo.EffectiveGrowth,
                combo.EffectiveDistribution,
                combo.CombinedTaxRate,
                SensitivityProfile.From(request.Strategy.Sensitivity));

            var chartsCombo = ChartBuilder.BuildCharts(combo.FundYears, combo.CombinedTaxSavings, combo.EffectiveGrowth, combo.RetirementIncome, request, combo.FvCurrent, combo.FvCombo, combo.FvOutside, combo.Contribution, combo.EffectiveGrowth);

            var (metricsCombo, infoCombo) = StrategyMetricsBuilder.BuildCombo(combo);

            return new AdvancedMarketsResultVm
            {
                EstimatedAnnualContribution = combo.Contribution,
                EstimatedDeduction = combo.Deductible,
                DeductionLabel = "Illustrative combo deductible contribution (subject to plan design/testing)",
                FederalTaxSavings = combo.FederalTaxSavings,
                StateTaxSavings = combo.StateTaxSavings,
                CombinedTaxSavings = combo.CombinedTaxSavings,
                NetAnnualCost = combo.NetCost,
                ProjectedRetirementValue = combo.ProjectedValueTotal,
                ProjectedRetirementIncome = combo.RetirementIncome,
                EstimatedLegacyValue = combo.Legacy,
                EmployeeCostImpact = combo.EmployeeCost,
                EffectiveGrowthRate = combo.EffectiveGrowth,
                EffectiveDistributionRate = combo.EffectiveDistribution,
                CombinedTaxRate = combo.CombinedTaxRate,
                IncomeMethod = "Modeled retirement draw; not an actuarial pension estimate.",
                Suitability = suitabilityCombo,
                Comparison = comparisonCombo,
                Summary = summaryCombo,
                Charts = chartsCombo,
                StrategyMetrics = metricsCombo,
                InformationalMetrics = infoCombo,
                Warnings = combo.Warnings
            };
        }

        if (request.Strategy.Selected == StrategyKind.ExecutiveBonus162)
        {
            var bonus = ExecutiveBonusCalculator.Calculate(request);
            var suitabilityBonus = SuitabilityEngine.Build(request, bonus.AnnualBonusFunding, 0, bonus.FundYears);
            var comparisonBonus = BuildComparison(
                request,
                bonus.DeductibleAmount,
                bonus.EmployerTaxSavings,
                bonus.EmployerAfterTaxCost,
                bonus.ProjectedValueTotal,
                bonus.PolicyIncome,
                bonus.EstimatedLegacyValue,
                bonus.CombinedTaxRate,
                bonus.BaselineValue,
                bonus.BaselineIncome,
                bonus.BaselineLegacy);

            var summaryBonus = BuildSummary(
                request,
                bonus.AnnualBonusFunding,
                bonus.DeductibleAmount,
                bonus.EmployerTaxSavings,
                bonus.EmployerAfterTaxCost,
                bonus.PolicyGrowthRate,
                bonus.EffectiveDistribution,
                bonus.CombinedTaxRate,
                SensitivityProfile.From(request.Strategy.Sensitivity));

            var chartsBonus = ChartBuilder.BuildCharts(
                bonus.FundYears,
                bonus.EmployerTaxSavings,
                bonus.PolicyGrowthRate,
                bonus.PolicyIncome,
                request,
                bonus.FvCurrent,
                bonus.Fv162,
                bonus.FvOutside,
                bonus.NetToPolicy,
                bonus.ProjectionGrowthRate);

            var metricsBonus = StrategyMetricsBuilder.BuildExecutiveBonus(bonus);

            return new AdvancedMarketsResultVm
            {
                EstimatedAnnualContribution = bonus.AnnualBonusFunding,
                EstimatedDeduction = bonus.DeductibleAmount,
                DeductionLabel = "Bonus is deductible compensation; taxable to employee",
                FederalTaxSavings = bonus.FederalTaxSavings,
                StateTaxSavings = bonus.StateTaxSavings,
                CombinedTaxSavings = bonus.EmployerTaxSavings,
                NetAnnualCost = bonus.EmployerAfterTaxCost,
                ProjectedRetirementValue = bonus.ProjectedValueTotal,
                ProjectedRetirementIncome = bonus.PolicyIncome,
                EstimatedLegacyValue = bonus.EstimatedLegacyValue,
                EmployeeCostImpact = 0,
                EffectiveGrowthRate = bonus.PolicyGrowthRate,
                EffectiveDistributionRate = bonus.EffectiveDistribution,
                CombinedTaxRate = bonus.CombinedTaxRate,
                IncomeMethod = "Modeled policy-based retirement access (withdrawals/loans, illustrative)",
                Suitability = suitabilityBonus,
                Comparison = comparisonBonus,
                Summary = summaryBonus,
                Charts = chartsBonus,
                StrategyMetrics = metricsBonus,
                Warnings = bonus.Warnings
            };
        }

        if (request.Strategy.Selected == StrategyKind.DeferredComp)
        {
            var dc = DeferredCompCalculator.Calculate(request);
            var suitabilityDc = SuitabilityEngine.Build(request, dc.AnnualDeferral, 0, dc.FundYears);
            var comparisonDc = BuildComparison(
                request,
                0,
                0,
                0,
                dc.ProjectedValueTotal,
                dc.NetAnnualPayout,
                dc.DeferredBalanceAtPayoutStart,
                dc.FutureTaxRate,
                dc.BaselineValue,
                dc.BaselineIncome,
                dc.BaselineLegacy);

            var summaryDc = BuildDeferredCompSummary(
                request,
                dc,
                SensitivityProfile.From(request.Strategy.Sensitivity));

            var chartsDc = ChartBuilder.BuildCharts(
                dc.FundYears,
                0,
                dc.DeferralGrowthRate,
                dc.NetAnnualPayout,
                request,
                dc.FvCurrent,
                dc.FvStrategy,
                dc.FvOutside,
                dc.AnnualDeferral,
                dc.ProjectionGrowthRate);

            var metricsDc = StrategyMetricsBuilder.BuildDeferredComp(dc);

            return new AdvancedMarketsResultVm
            {
                EstimatedAnnualContribution = dc.AnnualDeferral,
                EstimatedDeduction = 0,
                DeductionLabel = "No current employer deduction; payout is taxable when received",
                FederalTaxSavings = 0,
                StateTaxSavings = 0,
                CombinedTaxSavings = 0,
                NetAnnualCost = 0,
                ProjectedRetirementValue = dc.ProjectedValueTotal,
                ProjectedRetirementIncome = dc.NetAnnualPayout,
                EstimatedLegacyValue = dc.DeferredBalanceAtPayoutStart,
                EmployeeCostImpact = 0,
                EffectiveGrowthRate = dc.DeferralGrowthRate,
                EffectiveDistributionRate = dc.FutureTaxRate,
                CombinedTaxRate = dc.FutureTaxRate,
                IncomeMethod = "Illustrative straight-line annual payout; taxable when received",
                Suitability = suitabilityDc,
                Comparison = comparisonDc,
                Summary = summaryDc,
                Charts = chartsDc,
                StrategyMetrics = metricsDc,
                Warnings = dc.Warnings
            };
        }

        if (request.Strategy.Selected == StrategyKind.SplitDollar)
        {
            var sd = SplitDollarCalculator.Calculate(request);
            var suitabilitySd = SuitabilityEngine.Build(request, sd.AnnualPremium, 0, sd.FundYears);
            var comparisonSd = BuildComparison(
                request,
                0,
                0,
                sd.AnnualPremium,
                sd.ProjectedValueTotal,
                sd.ModeledAccessIncome,
                sd.AccessibleEquityAtExit,
                0,
                sd.BaselineValue,
                sd.BaselineIncome,
                sd.BaselineLegacy);

            var summarySd = BuildSplitDollarSummary(request, sd, SensitivityProfile.From(request.Strategy.Sensitivity));

            var chartsSd = ChartBuilder.BuildCharts(
                sd.FundYears,
                0,
                sd.PolicyGrowthRate,
                sd.ModeledAccessIncome,
                request,
                sd.FvCurrent,
                sd.AccessibleEquityAtExit,
                sd.FvOutside,
                sd.AnnualPremium,
                sd.ProjectionGrowthRate);

            var metricsSd = StrategyMetricsBuilder.BuildSplitDollar(sd);

            return new AdvancedMarketsResultVm
            {
                EstimatedAnnualContribution = sd.AnnualPremium,
                EstimatedDeduction = 0,
                DeductionLabel = "No current employer deduction modeled (structure-dependent)",
                FederalTaxSavings = 0,
                StateTaxSavings = 0,
                CombinedTaxSavings = 0,
                NetAnnualCost = sd.AnnualPremium,
                ProjectedRetirementValue = sd.ProjectedValueTotal,
                ProjectedRetirementIncome = sd.ModeledAccessIncome,
                EstimatedLegacyValue = sd.AccessibleEquityAtExit,
                EmployeeCostImpact = 0,
                EffectiveGrowthRate = sd.PolicyGrowthRate,
                EffectiveDistributionRate = sd.DistributionRate,
                CombinedTaxRate = 0,
                IncomeMethod = "Modeled policy-based access from projected exit value (illustrative)",
                Suitability = suitabilitySd,
                Comparison = comparisonSd,
                Summary = summarySd,
                Charts = chartsSd,
                StrategyMetrics = metricsSd,
                Warnings = sd.Warnings
            };
        }

        if (request.Strategy.Selected == StrategyKind.TaxDiversification)
        {
            var td = TaxDiversificationCalculator.Calculate(request);
            var suitabilityTd = SuitabilityEngine.Build(request, td.AnnualSavingsCommitment, 0, td.Years);
            var comparisonTd = BuildComparison(
                request,
                0,
                0,
                td.AnnualSavingsCommitment,
                td.TotalProjectedValue,
                td.AfterTaxIncome,
                td.AfterTaxValueAtRetirementStart,
                0,
                td.BaselineValue,
                td.BaselineIncome,
                td.BaselineLegacy);

            var summaryTd = BuildTaxDiversificationSummary(request, td, SensitivityProfile.From(request.Strategy.Sensitivity));

            var chartsTd = ChartBuilder.BuildCharts(
                td.Years,
                0,
                td.GrossGrowthRate,
                td.AfterTaxIncome,
                request,
                td.FvCurrent,
                td.TotalProjectedValue,
                td.FvOutside,
                td.AnnualSavingsCommitment,
                td.GrossGrowthRate);

            var metricsTd = StrategyMetricsBuilder.BuildTaxDiversification(td);

            return new AdvancedMarketsResultVm
            {
                EstimatedAnnualContribution = td.AnnualSavingsCommitment,
                EstimatedDeduction = 0,
                DeductionLabel = "Modeled annual savings commitment allocated across tax buckets",
                FederalTaxSavings = 0,
                StateTaxSavings = 0,
                CombinedTaxSavings = 0,
                NetAnnualCost = td.AnnualSavingsCommitment,
                ProjectedRetirementValue = td.TotalProjectedValue,
                ProjectedRetirementIncome = td.AfterTaxIncome,
                EstimatedLegacyValue = td.AfterTaxValueAtRetirementStart,
                EmployeeCostImpact = 0,
                EffectiveGrowthRate = td.GrossGrowthRate,
                EffectiveDistributionRate = td.DistributionRateUsed,
                CombinedTaxRate = 0,
                IncomeMethod = "Modeled after-tax retirement draw (illustrative)",
                Suitability = suitabilityTd,
                Comparison = comparisonTd,
                Summary = summaryTd,
                Charts = chartsTd,
                StrategyMetrics = metricsTd,
                Warnings = td.Warnings
            };
        }

        var sensitivity = SensitivityProfile.From(request.Strategy.Sensitivity);
        var ownerAge = request.Client.OwnerAge ?? 0;
        var retireAge = request.Client.RetirementAge ?? ownerAge;
        var projectionYears = Math.Max(1, retireAge - ownerAge);

        var effectiveGrowth = Math.Max(0, N(request.Projection.GrowthRate) + sensitivity.GrowthDelta);
        var effectiveDistribution = Math.Max(0.02, N(request.Projection.DistributionRate) + sensitivity.DistributionDelta);

        var rawContribution = EstimateContribution(request, projectionYears, effectiveGrowth);
        var contribution = rawContribution * sensitivity.ContributionFactor;

        var (deduction, deductionLabel) = EstimateDeduction(request, contribution);

        var taxImpact = new TaxImpactCalculator().ComputeCombinedRate(N(request.Tax.FederalRate), N(request.Tax.StateRate));
        var federalSavings = deduction * N(request.Tax.FederalRate);
        var stateSavings = deduction * N(request.Tax.StateRate);
        var combinedSavings = deduction * taxImpact.CombinedRate;

        var employeeCost = EstimateEmployeeCost(request, contribution);
        var admin = EstimateAdminCost(request);
        var netCost = contribution + employeeCost + admin - combinedSavings;

        var fvCurrent = ProjectionEngine.FutureValueLump(N(request.Projection.CurrentAssets), effectiveGrowth, projectionYears);
        var fvStrategy = ProjectionEngine.FutureValueSeries(contribution, effectiveGrowth, projectionYears);
        var fvOutside = ProjectionEngine.FutureValueSeries(N(request.Projection.AnnualSavings), effectiveGrowth, projectionYears);
        var projectedValue = fvCurrent + fvStrategy + fvOutside;

        var retirementIncome = RetirementIncomeCalculator.EstimateIncome(projectedValue, effectiveGrowth, effectiveDistribution, NI(request.Projection.RetirementDurationYears));
        var legacy = LegacyValueCalculator.EstimateRemaining(projectedValue, retirementIncome, effectiveGrowth, NI(request.Projection.RetirementDurationYears));

        var strategyMetrics = StrategyMetricsBuilder.Build(request, contribution, employeeCost, admin, deduction, fvStrategy, retirementIncome, legacy);

        var baselineValue = fvCurrent + fvOutside;
        var baselineIncome = RetirementIncomeCalculator.EstimateIncome(baselineValue, effectiveGrowth, effectiveDistribution, NI(request.Projection.RetirementDurationYears));
        var baselineLegacy = LegacyValueCalculator.EstimateRemaining(baselineValue, baselineIncome, effectiveGrowth, NI(request.Projection.RetirementDurationYears));
        var comparison = BuildComparison(request, deduction, combinedSavings, netCost, projectedValue, retirementIncome, legacy, taxImpact.CombinedRate, baselineValue, baselineIncome, baselineLegacy);

        var summary = BuildSummary(request, contribution, deduction, combinedSavings, netCost, effectiveGrowth, effectiveDistribution, taxImpact.CombinedRate, sensitivity);

        var charts = ChartBuilder.BuildCharts(projectionYears, combinedSavings, effectiveGrowth, retirementIncome, request, fvCurrent, fvStrategy, fvOutside, contribution, effectiveGrowth);

        var suitability = SuitabilityEngine.Build(request, contribution, employeeCost, projectionYears);

        return new AdvancedMarketsResultVm
        {
            EstimatedAnnualContribution = contribution,
            EstimatedDeduction = deduction,
            DeductionLabel = deductionLabel,
            FederalTaxSavings = federalSavings,
            StateTaxSavings = stateSavings,
            CombinedTaxSavings = combinedSavings,
            NetAnnualCost = netCost,
            ProjectedRetirementValue = projectedValue,
            ProjectedRetirementIncome = retirementIncome,
            EstimatedLegacyValue = legacy,
            EmployeeCostImpact = employeeCost,
            EffectiveGrowthRate = effectiveGrowth,
            EffectiveDistributionRate = effectiveDistribution,
            CombinedTaxRate = taxImpact.CombinedRate,
            IncomeMethod = "Sustainable withdrawal / PMT-equivalent",
            Suitability = suitability,
            Comparison = comparison,
            Summary = summary,
            Charts = charts,
            StrategyMetrics = strategyMetrics
        };
    }

    private static double EstimateContribution(AdvancedMarketsPageViewModel req, int yearsToRetirement, double growthRate)
    {
        return req.Strategy.Selected switch
        {
            StrategyKind.DefinedBenefit => EstimateDbContribution(req, yearsToRetirement, growthRate),
            StrategyKind.CashBalance => Math.Max(N(req.CashBalance.DesiredTotalContribution), N(req.Business.OwnerComp) * 0.25),
            StrategyKind.ComboDb401k => Math.Max(N(req.Combo.TargetTotal), N(req.Business.OwnerComp) * 0.30),
            StrategyKind.ExecutiveBonus162 => N(req.ExecutiveBonus.AnnualBonus),
            StrategyKind.DeferredComp => N(req.DeferredComp.DeferralAmount),
            StrategyKind.SplitDollar => N(req.SplitDollar.AnnualPremium),
            StrategyKind.TaxDiversification => N(req.Projection.AnnualSavings),
            _ => N(req.Projection.AnnualSavings)
        };
    }

    private static double EstimateDbContribution(AdvancedMarketsPageViewModel req, int yearsToRetirement, double growthRate)
    {
        if (N(req.DefinedBenefit.TargetContribution) > 0)
            return N(req.DefinedBenefit.TargetContribution);

        var distributionRate = Math.Max(0.035, N(req.Projection.DistributionRate));
        var targetBenefit = Math.Max(0, N(req.DefinedBenefit.TargetBenefit));
        if (targetBenefit <= 0 && N(req.Business.OwnerComp) > 0)
        {
            // fallback: aim for ~60% income replacement for owner comp
            targetBenefit = N(req.Business.OwnerComp) * 0.60;
        }

        // required corpus to support target benefit at retirement
        var requiredCorpus = targetBenefit / distributionRate;

        // solve for annual contribution to reach required corpus in yearsToRetirement at growthRate
        if (yearsToRetirement <= 0) return requiredCorpus;
        if (Math.Abs(growthRate) < 1e-9) return requiredCorpus / yearsToRetirement;

        var annuityFactor = (Math.Pow(1 + growthRate, yearsToRetirement) - 1) / growthRate;
        var annualContribution = requiredCorpus / annuityFactor;

        // modest buffer for volatility
        annualContribution *= 1.05;

        // include spouse contribution if flagged
        if (req.DefinedBenefit.IncludeSpouse && N(req.DefinedBenefit.SpouseContribution) > 0)
            annualContribution += N(req.DefinedBenefit.SpouseContribution);

        return annualContribution;
    }

    private static (double deduction, string label) EstimateDeduction(AdvancedMarketsPageViewModel req, double contribution)
    {
        return req.Strategy.Selected switch
        {
            StrategyKind.DefinedBenefit => (contribution, "Qualified plan contribution (illustrative, subject to actuarial limits)"),
            StrategyKind.CashBalance => (contribution, "Qualified plan contribution (illustrative, subject to actuarial limits)"),
            StrategyKind.ComboDb401k => (contribution, "Combined DB/401(k)/PS deductible estimate"),
            StrategyKind.ExecutiveBonus162 => (contribution, "Bonus is deductible compensation to employer; taxable to executive"),
            StrategyKind.DeferredComp => (0, "Nonqualified deferral — no current deduction"),
            StrategyKind.SplitDollar => (0, "Split-dollar typically not currently deductible"),
            StrategyKind.TaxDiversification => (0, "Asset mix / tax diversification — no current deduction"),
            _ => (contribution, "Estimated deductible amount")
        };
    }

    private static double EstimateEmployeeCost(AdvancedMarketsPageViewModel req, double contribution)
    {
        var headcount = Math.Max(0, NI(req.Business.EligibleEmployeeCount));
        var factor = req.Strategy.Selected switch
        {
            StrategyKind.DefinedBenefit => N(req.DefinedBenefit.EmployeeCostFactor),
            StrategyKind.CashBalance => N(req.CashBalance.EmployeeCostFactor),
            StrategyKind.ComboDb401k => N(req.Combo.EmployeeCostFactor),
            _ => 0.0
        };

        return headcount == 0 ? 0 : contribution * factor;
    }

    private static double EstimateAdminCost(AdvancedMarketsPageViewModel req)
    {
        return req.Strategy.Selected switch
        {
            StrategyKind.DefinedBenefit => N(req.DefinedBenefit.AdminCost),
            StrategyKind.CashBalance => N(req.CashBalance.AdminCost),
            StrategyKind.ComboDb401k => N(req.DefinedBenefit.AdminCost) + (N(req.CashBalance.AdminCost) * 0.5),
            _ => 0
        };
    }

    private static List<ComparisonRowVm> BuildComparison(
        AdvancedMarketsPageViewModel req,
        double deduction,
        double taxSavings,
        double netCost,
        double projValue,
        double income,
        double legacy,
        double combinedTaxRate,
        double baselineValue,
        double baselineIncome,
        double baselineLegacy)
    {
        var currentDeductible = N(req.Business.CurrentEmployerRetirementContributions);
        var currentTaxSavings = currentDeductible * combinedTaxRate;
        var currentNet = currentDeductible - currentTaxSavings + N(req.Business.CurrentBenefitCosts);

        return new List<ComparisonRowVm>
        {
            new() { Label = "Estimated Current-Year Tax Savings", Current = currentTaxSavings, Proposed = taxSavings },
            new() { Label = "Estimated After-Tax Annual Cost", Current = currentNet, Proposed = netCost },
            new() { Label = "Estimated Deductible Contribution", Current = currentDeductible, Proposed = deduction },
            new() { Label = "Projected Retirement Value", Current = baselineValue, Proposed = projValue },
            new() { Label = "Projected Retirement Income", Current = baselineIncome, Proposed = income },
            new() { Label = "Estimated Legacy Value", Current = baselineLegacy, Proposed = legacy }
        };
    }

    private static IllustrationSummaryVm BuildSummary(
        AdvancedMarketsPageViewModel req,
        double contribution,
        double deduction,
        double taxSavings,
        double netCost,
        double effectiveGrowth,
        double effectiveDistribution,
        double combinedTaxRate,
        SensitivityProfile sensitivity)
    {
        return new IllustrationSummaryVm
        {
            Headline = "Advanced Markets Planning Illustration",
            Subhead = $"Mode: {sensitivity.Name} — hypothetical, educational use only",
            TalkingPoints = new List<string>
            {
                $"Estimated deductible contribution: {deduction:C0}",
                $"Estimated first-year tax savings: {taxSavings:C0}",
                $"Estimated net annual cost after tax savings: {netCost:C0}"
            },
            Assumptions = new List<string>
            {
                $"Effective growth rate: {effectiveGrowth:P1} (sensitivity {sensitivity.Name})",
                $"Effective distribution rate: {effectiveDistribution:P2}",
                $"Combined marginal tax rate: {combinedTaxRate:P1}",
                $"Retirement duration: {NI(req.Projection.RetirementDurationYears)} yrs"
            },
            Disclaimers = DisclaimerLibrary.Standard()
        };
    }

    private static IllustrationSummaryVm BuildDeferredCompSummary(
        AdvancedMarketsPageViewModel req,
        DeferredCompResult dc,
        SensitivityProfile sensitivity)
    {
        return new IllustrationSummaryVm
        {
            Headline = "Deferred Compensation Illustration",
            Subhead = $"Mode: {sensitivity.Name} — hypothetical, educational use only",
            TalkingPoints = new List<string>
            {
                $"Annual deferral: {dc.AnnualDeferral:C0}",
                "No current employer deduction modeled; benefits are taxable when received.",
                $"Illustrative straight-line annual payout (after future tax): {dc.NetAnnualPayout:C0}"
            },
            Assumptions = new List<string>
            {
                $"Deferral crediting rate: {dc.DeferralGrowthRate:P1}",
                $"Future tax rate (illustrative): {dc.FutureTaxRate:P1}",
                $"Funding years: {dc.DeferralYears}, payout years: {dc.DistributionYears}",
                $"Retirement duration: {NI(req.Projection.RetirementDurationYears)} yrs"
            },
            Disclaimers = DisclaimerLibrary.Standard()
        };
    }

    private static IllustrationSummaryVm BuildSplitDollarSummary(
        AdvancedMarketsPageViewModel req,
        SplitDollarResult sd,
        SensitivityProfile sensitivity)
    {
        return new IllustrationSummaryVm
        {
            Headline = "Split-Dollar Illustration",
            Subhead = $"Mode: {sensitivity.Name} — hypothetical, educational use only",
            TalkingPoints = new List<string>
            {
                $"Annual premium (illustrative): {sd.AnnualPremium:C0}",
                "No current employer deduction modeled (structure-dependent).",
                $"Policy access value at exit (illustrative): {sd.AccessibleEquityAtExit:C0}"
            },
            Assumptions = new List<string>
            {
                $"Policy crediting rate (illustrative): {sd.PolicyGrowthRate:P1}",
                $"Funding years: {sd.FundingYears}, exit/unwind year: {sd.ExitYear}",
                $"Modeled distribution rate for access: {sd.DistributionRate:P2}",
                $"Retirement duration: {NI(req.Projection.RetirementDurationYears)} yrs"
            },
            Disclaimers = DisclaimerLibrary.Standard()
        };
    }

    private static IllustrationSummaryVm BuildTaxDiversificationSummary(
        AdvancedMarketsPageViewModel req,
        TaxDiversificationResult td,
        SensitivityProfile sensitivity)
    {
        return new IllustrationSummaryVm
        {
            Headline = "Tax Diversification Illustration",
            Subhead = $"Mode: {sensitivity.Name} — hypothetical, educational use only",
            TalkingPoints = new List<string>
            {
                $"Modeled annual savings commitment: {td.AnnualSavingsCommitment:C0}",
                $"Projected qualified/taxable/tax-free buckets at retirement: {td.QualifiedBucket:C0} / {td.TaxableBucket:C0} / {td.TaxFreeBucket:C0}",
                $"After-tax value at retirement start (illustrative): {td.AfterTaxValueAtRetirementStart:C0}"
            },
            Assumptions = new List<string>
            {
                $"Gross growth rate: {td.GrossGrowthRate:P1}",
                $"Taxable drag used: {td.TaxableDragUsed:P2}",
                $"Future tax rates (qualified / taxable): {td.FutureTaxRateQ:P1} / {td.FutureTaxRateT:P1}",
                $"Horizon: {td.Years} years; Distribution rate: {td.DistributionRateUsed:P2}; Retirement duration: {NI(req.Projection.RetirementDurationYears)} yrs"
            },
            Disclaimers = DisclaimerLibrary.Standard()
        };
    }

    private static void NormalizeDefaults(AdvancedMarketsPageViewModel req)
    {
        static double? Rate(double? v)
        {
            if (v == null) return null;
            var val = Math.Max(0, v.Value);
            return val > 1 ? val / 100.0 : val;
        }

        // Client
        req.Client.OwnerAge ??= 50;
        req.Client.RetirementAge ??= req.Client.OwnerAge + 15;
        req.Client.CurrentQualifiedAssets ??= 0;
        req.Client.CurrentTaxableAssets ??= 0;
        req.Client.CurrentTaxFreeAssets ??= 0;

        // Business
        req.Business.AnnualBusinessIncome ??= 0;
        req.Business.OwnerComp ??= 0;
        req.Business.EmployeeCount ??= 0;
        req.Business.EligibleEmployeeCount ??= 0;
        req.Business.AverageEmployeeAge ??= 0;
        req.Business.AverageEmployeeComp ??= 0;
        req.Business.OwnershipPct ??= 100;
        req.Business.CurrentEmployerRetirementContributions ??= 0;
        req.Business.CurrentBenefitCosts ??= 0;

        // Tax
        req.Tax.FederalRate = Rate(req.Tax.FederalRate) ?? 0;
        req.Tax.StateRate = Rate(req.Tax.StateRate) ?? 0;
        req.Tax.CapitalGainsRate = Rate(req.Tax.CapitalGainsRate) ?? 0;
        req.Tax.FutureTaxRate = Rate(req.Tax.FutureTaxRate) ?? 0;

        // Projection
        req.Projection.CurrentAssets ??= 0;
        req.Projection.AnnualSavings ??= 0;
        req.Projection.GrowthRate = Rate(req.Projection.GrowthRate) ?? 0.06;
        req.Projection.InflationRate = Rate(req.Projection.InflationRate) ?? 0.02;
        req.Projection.RetirementDurationYears ??= 25;
        req.Projection.DistributionRate = Rate(req.Projection.DistributionRate) ?? 0.045;
        req.Projection.DiscountRate = Rate(req.Projection.DiscountRate) ?? 0;

        // Defined Benefit
        req.DefinedBenefit.AnnualIncome ??= 0;
        req.DefinedBenefit.TargetContribution ??= 0;
        req.DefinedBenefit.TargetBenefit ??= 0;
        req.DefinedBenefit.AdminCost ??= 0;
        req.DefinedBenefit.EmployeeCostFactor = Rate(req.DefinedBenefit.EmployeeCostFactor) ?? 0;
        req.DefinedBenefit.GrowthRate = Rate(req.DefinedBenefit.GrowthRate) ?? req.Projection.GrowthRate;
        req.DefinedBenefit.InflationRate = Rate(req.DefinedBenefit.InflationRate) ?? req.Projection.InflationRate;
        req.DefinedBenefit.SpouseContribution ??= 0;

        // Cash Balance
        req.CashBalance.Current401kDeferral ??= 0;
        req.CashBalance.EmployerProfitSharing ??= 0;
        req.CashBalance.DesiredTotalContribution ??= 0;
        req.CashBalance.AdminCost ??= 0;
        req.CashBalance.GrowthRate = Rate(req.CashBalance.GrowthRate) ?? req.Projection.GrowthRate;
        req.CashBalance.PayCreditPct = Rate(req.CashBalance.PayCreditPct) ?? 0;
        req.CashBalance.InterestCreditPct = Rate(req.CashBalance.InterestCreditPct) ?? 0.04;
        req.CashBalance.EmployeeCostFactor = Rate(req.CashBalance.EmployeeCostFactor) ?? 0;

        // Combo
        req.Combo.EmployeeDeferral ??= 0;
        req.Combo.EmployerPct = Rate(req.Combo.EmployerPct) ?? 0;
        req.Combo.ProfitSharingPct = Rate(req.Combo.ProfitSharingPct) ?? 0;
        req.Combo.SafeHarborPct = Rate(req.Combo.SafeHarborPct) ?? 0;
        req.Combo.TargetTotal ??= 0;
        req.Combo.EmployeeCostFactor = Rate(req.Combo.EmployeeCostFactor) ?? 0;
        req.Combo.TestingBufferPct = Rate(req.Combo.TestingBufferPct) ?? 0.03;

        // Executive Bonus
        req.ExecutiveBonus.AnnualBonus ??= 0;
        req.ExecutiveBonus.YearsFunded ??= 0;
        req.ExecutiveBonus.PolicyGrowthRate = Rate(req.ExecutiveBonus.PolicyGrowthRate) ?? req.Projection.GrowthRate;
        req.ExecutiveBonus.DeathBenefitMultiple ??= 0;
        req.ExecutiveBonus.AdminCost ??= 0;

        // Deferred Comp
        req.DeferredComp.DeferralAmount ??= 0;
        req.DeferredComp.DeferralYears ??= 0;
        req.DeferredComp.DistributionStartAge ??= 0;
        req.DeferredComp.DistributionYears ??= 0;
        req.DeferredComp.GrowthRate = Rate(req.DeferredComp.GrowthRate) ?? req.Projection.GrowthRate;
        req.DeferredComp.CurrentTaxRate = Rate(req.DeferredComp.CurrentTaxRate) ?? req.Tax.FederalRate;
        req.DeferredComp.FutureTaxRate = Rate(req.DeferredComp.FutureTaxRate) ?? req.Tax.FutureTaxRate ?? req.Tax.FederalRate;

        // Split Dollar
        req.SplitDollar.AnnualPremium ??= 0;
        req.SplitDollar.FundingYears ??= 0;
        req.SplitDollar.GrowthRate = Rate(req.SplitDollar.GrowthRate) ?? req.Projection.GrowthRate;
        req.SplitDollar.DeathBenefit ??= 0;
        req.SplitDollar.ExitYear ??= 0;
    }

internal record SensitivityProfile(string Name, double GrowthDelta, double DistributionDelta, double ContributionFactor)
{
    public static SensitivityProfile From(string? mode)
    {
        return mode?.ToLowerInvariant() switch
        {
            "conservative" => new SensitivityProfile("Conservative", -0.01, -0.005, 0.95),
            "optimistic" => new SensitivityProfile("Optimistic", 0.01, 0.005, 1.05),
            _ => new SensitivityProfile("Base", 0, 0, 1.0)
        };
    }
}

internal sealed class DefinedBenefitCalculator
{
    private const double MaxFed = 0.40;
    private const double MaxState = 0.15;
    private const double MaxCombined = 0.50;
    private const double MaxGrowth = 0.12;
    private const double MinDistribution = 0.03;
    private const double MaxDistribution = 0.08;
    private const double MaxEmployeeFactor = 1.0;
    private const double MaxTargetIncome = 5_000_000;
    private const double WarnContribution = 500_000;
    private const double StrongWarnContribution = 1_000_000;
    private const double CapContribution = 2_000_000;

    internal static DbResult Calculate(AdvancedMarketsPageViewModel req)
    {
        var warnings = new List<string>();

        var ownerAge = req.Client.OwnerAge ?? 0;
        var retireAge = req.Client.RetirementAge ?? ownerAge;
        var yearsToRet = Math.Max(1, retireAge - ownerAge);
        if (yearsToRet <= 1)
            warnings.Add("Short funding horizon; results are highly sensitive.");

        var g = Clamp(N(req.Projection.GrowthRate), 0, MaxGrowth);
        var dist = Clamp(N(req.Projection.DistributionRate), MinDistribution, MaxDistribution);

        var fed = Clamp(N(req.Tax.FederalRate), 0, MaxFed);
        var state = Clamp(N(req.Tax.StateRate), 0, MaxState);
        var combined = Clamp(fed + state - (fed * state), 0, MaxCombined);

        var targetIncome = ComputeTargetIncome(req);
        targetIncome = Clamp(targetIncome, 0, MaxTargetIncome);

        var requiredCorpus = targetIncome / Math.Max(dist, MinDistribution);
        var annuityFactor = Math.Abs(g) < 1e-9 ? yearsToRet : ((Math.Pow(1 + g, yearsToRet) - 1) / g);
        var baseContrib = annuityFactor <= 0 ? requiredCorpus : requiredCorpus / annuityFactor;
        var contribution = N(req.DefinedBenefit.TargetContribution) > 0
            ? N(req.DefinedBenefit.TargetContribution)
            : baseContrib * 1.05;

        if (req.DefinedBenefit.IncludeSpouse && N(req.DefinedBenefit.SpouseContribution) > 0)
            contribution += N(req.DefinedBenefit.SpouseContribution);

        // Contribution clamps and warnings
        if (contribution > WarnContribution)
            warnings.Add("Large illustrative contribution; confirm affordability and actuarial limits.");
        if (contribution > StrongWarnContribution)
            warnings.Add("Very large illustrative contribution; likely constrained by real-world limits—use actuarial design.");
        if (contribution > CapContribution)
        {
            contribution = CapContribution;
            warnings.Add("Contribution capped at illustration limit (2,000,000).");
        }

        var employeeFactor = Clamp(N(req.DefinedBenefit.EmployeeCostFactor), 0, MaxEmployeeFactor);
        var admin = N(req.DefinedBenefit.AdminCost);

        var deductible = contribution;
        var federalSavings = deductible * fed;
        var stateSavings = deductible * state;
        var combinedSavings = deductible * combined;

        if (combinedSavings > contribution * 0.6)
        {
            combinedSavings = contribution * 0.6;
            warnings.Add("Illustrative tax savings were limited by internal guardrails. Verify rates and contribution assumptions.");
        }

        var employeeCost = contribution * employeeFactor;
        var netCost = contribution + employeeCost + admin - combinedSavings;
        if (employeeCost > 0)
            warnings.Add("Employee cost factor is illustrative only; actual employee cost depends on census/testing and is not discrimination-tested.");

        var fundYears = yearsToRet;
        var fvDb = ProjectionEngine.FutureValueSeries(contribution, g, fundYears);

        var fvCurrent = ProjectionEngine.FutureValueLump(N(req.Projection.CurrentAssets), g, fundYears)
                        + ProjectionEngine.FutureValueSeries(N(req.Business.CurrentEmployerRetirementContributions), g, fundYears);

        var fvOutside = ProjectionEngine.FutureValueSeries(N(req.Projection.AnnualSavings), g, fundYears);
        var projectedTotal = fvDb + fvCurrent + fvOutside;

        var retirementIncome = RetirementIncomeCalculator.EstimateIncome(projectedTotal, g, dist, NI(req.Projection.RetirementDurationYears));
        var legacy = LegacyValueCalculator.EstimateRemaining(projectedTotal, retirementIncome, g, NI(req.Projection.RetirementDurationYears));

        // Baseline path includes current employer contributions
        var baselineValue = fvCurrent + fvOutside; // fvCurrent already includes current contributions stream
        var baselineIncome = RetirementIncomeCalculator.EstimateIncome(baselineValue, g, dist, NI(req.Projection.RetirementDurationYears));
        var baselineLegacy = LegacyValueCalculator.EstimateRemaining(baselineValue, baselineIncome, g, NI(req.Projection.RetirementDurationYears));

        return new DbResult
        {
            Contribution = contribution,
            Deductible = deductible,
            FederalTaxSavings = federalSavings,
            StateTaxSavings = stateSavings,
            CombinedTaxSavings = combinedSavings,
            EmployeeCost = employeeCost,
            Admin = admin,
            NetCost = netCost,
            FvDb = fvDb,
            FvCurrent = fvCurrent,
            FvOutside = fvOutside,
            ProjectedValueTotal = projectedTotal,
            RetirementIncome = retirementIncome,
            Legacy = legacy,
            FundYears = fundYears,
            EffectiveGrowth = g,
            EffectiveDistribution = dist,
            CombinedTaxRate = combined,
            BaselineValue = baselineValue,
            BaselineIncome = baselineIncome,
            BaselineLegacy = baselineLegacy,
            Warnings = warnings
        };
    }

    private static double ComputeTargetIncome(AdvancedMarketsPageViewModel req)
    {
        if (N(req.DefinedBenefit.TargetBenefit) > 0) return N(req.DefinedBenefit.TargetBenefit);
        if (N(req.DefinedBenefit.AnnualIncome) > 0) return N(req.DefinedBenefit.AnnualIncome);
        return N(req.Business.OwnerComp) * 0.60;
    }

    private static double Clamp(double value, double min, double max)
        => Math.Min(Math.Max(value, min), max);
}

internal sealed class DbResult
{
    public double Contribution { get; set; }
    public double Deductible { get; set; }
    public double FederalTaxSavings { get; set; }
    public double StateTaxSavings { get; set; }
    public double CombinedTaxSavings { get; set; }
    public double EmployeeCost { get; set; }
    public double Admin { get; set; }
    public double NetCost { get; set; }
    public double FvDb { get; set; }
    public double FvCurrent { get; set; }
    public double FvOutside { get; set; }
    public double ProjectedValueTotal { get; set; }
    public double RetirementIncome { get; set; }
    public double Legacy { get; set; }
    public int FundYears { get; set; }
    public double EffectiveGrowth { get; set; }
    public double EffectiveDistribution { get; set; }
    public double CombinedTaxRate { get; set; }
    public double BaselineValue { get; set; }
    public double BaselineIncome { get; set; }
    public double BaselineLegacy { get; set; }
    public List<string> Warnings { get; set; } = new();
}

internal sealed class CashBalanceCalculator
{
    private const double MaxFed = 0.40;
    private const double MaxState = 0.15;
    private const double MaxCombined = 0.50;
    private const double MaxGrowth = 0.12;
    private const double MinDistribution = 0.03;
    private const double MaxDistribution = 0.08;
    private const double MaxPayCreditPct = 0.25;
    private const double MaxInterestCreditPct = 0.08;
    private const double DefaultInterestCredit = 0.04;
    private const double MaxEmployeeFactor = 1.0;
    private const double WarnContribution = 500_000;
    private const double StrongWarnContribution = 1_000_000;
    private const double CapContribution = 2_000_000;

    internal static CashBalanceResult Calculate(AdvancedMarketsPageViewModel req)
    {
        var warnings = new List<string>();

        var ownerAge = req.Client.OwnerAge ?? 0;
        var retireAge = req.Client.RetirementAge ?? ownerAge;
        var yearsToRet = Math.Max(1, retireAge - ownerAge);
        if (yearsToRet <= 2) warnings.Add("Short funding runway—cash balance results are highly sensitive.");

        var g = Clamp(N(req.Projection.GrowthRate), 0, MaxGrowth);
        var dist = Clamp(N(req.Projection.DistributionRate), MinDistribution, MaxDistribution);

        var fed = Clamp(N(req.Tax.FederalRate), 0, MaxFed);
        var state = Clamp(N(req.Tax.StateRate), 0, MaxState);
        var combined = Clamp(fed + state - (fed * state), 0, MaxCombined);

        var payCreditPct = Clamp(N(req.CashBalance.PayCreditPct), 0, MaxPayCreditPct);
        var interestCreditPct = N(req.CashBalance.InterestCreditPct) > 0
            ? Clamp(N(req.CashBalance.InterestCreditPct), 0, MaxInterestCreditPct)
            : DefaultInterestCredit;

        var employerProfitSharing = N(req.CashBalance.EmployerProfitSharing);
        var desiredTotal = N(req.CashBalance.DesiredTotalContribution);

        var payCredit = N(req.Business.OwnerComp) * payCreditPct;
        var baseContribution = payCredit + employerProfitSharing;
        var contribution = desiredTotal > 0
            ? desiredTotal
            : Math.Max(baseContribution, N(req.Business.OwnerComp) * 0.25);

        if (contribution > WarnContribution) warnings.Add("Large illustrative contribution—confirm affordability and plan limits.");
        if (contribution > StrongWarnContribution) warnings.Add("Very large illustrative contribution—likely constrained by testing/limits.");
        if (contribution > CapContribution)
        {
            contribution = CapContribution;
            warnings.Add("Contribution capped at illustration limit (2,000,000).");
        }

        var employeeFactor = Clamp(N(req.CashBalance.EmployeeCostFactor), 0, MaxEmployeeFactor);
        var admin = N(req.CashBalance.AdminCost);

        var deductible = contribution;
        var federalSavings = deductible * fed;
        var stateSavings = deductible * state;
        var combinedSavings = deductible * combined;

        if (combinedSavings > contribution * 0.6)
        {
            combinedSavings = contribution * 0.6;
            warnings.Add("Illustrative tax savings were limited by guardrails. Verify rates and contribution assumptions.");
        }

        var employeeCost = contribution * employeeFactor;
        var netCost = contribution + employeeCost + admin - combinedSavings;

        var fundYears = yearsToRet;
        var fvCb = ProjectionEngine.FutureValueSeries(contribution, interestCreditPct, fundYears);
        var fvCurrent = ProjectionEngine.FutureValueLump(N(req.Projection.CurrentAssets), g, fundYears)
                        + ProjectionEngine.FutureValueSeries(N(req.Business.CurrentEmployerRetirementContributions), g, fundYears);
        var fvOutside = ProjectionEngine.FutureValueSeries(N(req.Projection.AnnualSavings), g, fundYears);
        var projectedTotal = fvCb + fvCurrent + fvOutside;

        var retirementIncome = RetirementIncomeCalculator.EstimateIncome(projectedTotal, g, dist, NI(req.Projection.RetirementDurationYears));
        var legacy = LegacyValueCalculator.EstimateRemaining(projectedTotal, retirementIncome, g, NI(req.Projection.RetirementDurationYears));

        var baselineValue = fvCurrent + fvOutside;
        var baselineIncome = RetirementIncomeCalculator.EstimateIncome(baselineValue, g, dist, NI(req.Projection.RetirementDurationYears));
        var baselineLegacy = LegacyValueCalculator.EstimateRemaining(baselineValue, baselineIncome, g, NI(req.Projection.RetirementDurationYears));

        return new CashBalanceResult
        {
            Contribution = contribution,
            Deductible = deductible,
            FederalTaxSavings = federalSavings,
            StateTaxSavings = stateSavings,
            CombinedTaxSavings = combinedSavings,
            EmployeeCost = employeeCost,
            Admin = admin,
            NetCost = netCost,
            FvCb = fvCb,
            FvCurrent = fvCurrent,
            FvOutside = fvOutside,
            ProjectedValueTotal = projectedTotal,
            RetirementIncome = retirementIncome,
            Legacy = legacy,
            FundYears = fundYears,
            EffectiveGrowth = g,
            EffectiveDistribution = dist,
            CombinedTaxRate = combined,
            BaselineValue = baselineValue,
            BaselineIncome = baselineIncome,
            BaselineLegacy = baselineLegacy,
            Warnings = warnings
        };
    }

    private static double Clamp(double value, double min, double max) => Math.Min(Math.Max(value, min), max);
}

internal sealed class CashBalanceResult
{
    public double Contribution { get; set; }
    public double Deductible { get; set; }
    public double FederalTaxSavings { get; set; }
    public double StateTaxSavings { get; set; }
    public double CombinedTaxSavings { get; set; }
    public double EmployeeCost { get; set; }
    public double Admin { get; set; }
    public double NetCost { get; set; }
    public double FvCb { get; set; }
    public double FvCurrent { get; set; }
    public double FvOutside { get; set; }
    public double ProjectedValueTotal { get; set; }
    public double RetirementIncome { get; set; }
    public double Legacy { get; set; }
    public int FundYears { get; set; }
    public double EffectiveGrowth { get; set; }
    public double EffectiveDistribution { get; set; }
    public double CombinedTaxRate { get; set; }
    public double BaselineValue { get; set; }
    public double BaselineIncome { get; set; }
    public double BaselineLegacy { get; set; }
    public List<string> Warnings { get; set; } = new();
}

internal sealed class ExecutiveBonusCalculator
{
    private const double MaxFed = 0.40;
    private const double MaxState = 0.15;
    private const double MaxCombined = 0.50;
    private const double MaxPolicyGrowth = 0.12;
    private const double MaxProjectionGrowth = 0.12;
    private const double MinDistribution = 0.03;
    private const double MaxDistribution = 0.08;

    internal static ExecutiveBonusResult Calculate(AdvancedMarketsPageViewModel req)
    {
        var warnings = new List<string>();

        var ownerComp = N(req.Business.OwnerComp);
        var annualBonus = N(req.ExecutiveBonus.AnnualBonus);
        var yearsFundedRaw = Math.Max(0, NI(req.ExecutiveBonus.YearsFunded, 0));
        var yearsFunded = yearsFundedRaw < 1 ? 1 : yearsFundedRaw;
        if (yearsFundedRaw < 1)
            warnings.Add("Funding duration adjusted to 1 year for illustration; original years funded was below 1.");
        else if (yearsFunded == 1)
            warnings.Add("Single-pay illustration; values are highly sensitive.");

        var rFg = Clamp(N(req.Tax.FederalRate), 0, MaxFed);
        var rSt = Clamp(N(req.Tax.StateRate), 0, MaxState);
        var rCombined = Clamp(rFg + rSt - (rFg * rSt), 0, MaxCombined);

        var rPol = Clamp(N(req.ExecutiveBonus.PolicyGrowthRate), 0, MaxPolicyGrowth);
        var rProj = Clamp(N(req.Projection.GrowthRate), 0, MaxProjectionGrowth);
        var rDist = Clamp(N(req.Projection.DistributionRate), MinDistribution, MaxDistribution);

        var employeeTaxDrag = annualBonus * rCombined;
        var netToPolicy = Math.Max(0, annualBonus - employeeTaxDrag);
        var employerTaxSavings = annualBonus * rCombined;
        var employerAfterTaxCost = annualBonus - employerTaxSavings;
        const double admin = 0;

        var fv162 = ProjectionEngine.FutureValueSeries(netToPolicy, rPol, yearsFunded);
        var fvCurrent = ProjectionEngine.FutureValueLump(N(req.Projection.CurrentAssets), rProj, yearsFunded)
                        + ProjectionEngine.FutureValueSeries(N(req.Business.CurrentEmployerRetirementContributions), rProj, yearsFunded);
        var fvOutside = ProjectionEngine.FutureValueSeries(N(req.Projection.AnnualSavings), rProj, yearsFunded);
        var projectedTotal = fv162 + fvCurrent + fvOutside;

        var policyIncome = RetirementIncomeCalculator.EstimateIncome(fv162, rPol, rDist, NI(req.Projection.RetirementDurationYears));
        var estimatedLegacy = fv162;
        var deathBenefit = annualBonus * N(req.ExecutiveBonus.DeathBenefitMultiple);

        if (annualBonus > ownerComp * 0.5)
            warnings.Add("Large bonus relative to compensation—confirm reasonableness/affordability.");
        if (annualBonus * yearsFunded > ownerComp * 5)
            warnings.Add("Very large total bonus relative to compensation—high risk of impractical/unsustainable design.");

        warnings.Add("Employer deduction assumes compensation is ordinary and timely paid; employee tax drag is illustrative only.");

        var baselineValue = fvCurrent + fvOutside;
        var baselineIncome = RetirementIncomeCalculator.EstimateIncome(baselineValue, rProj, rDist, NI(req.Projection.RetirementDurationYears));
        var baselineLegacy = LegacyValueCalculator.EstimateRemaining(baselineValue, baselineIncome, rProj, NI(req.Projection.RetirementDurationYears));

        return new ExecutiveBonusResult
        {
            AnnualBonusFunding = annualBonus,
            DeductibleAmount = annualBonus,
            EmployerTaxSavings = employerTaxSavings,
            FederalTaxSavings = annualBonus * rFg,
            StateTaxSavings = annualBonus * rSt,
            EmployeeTaxDrag = employeeTaxDrag,
            NetToPolicy = netToPolicy,
            Admin = admin,
            EmployerAfterTaxCost = employerAfterTaxCost,
            Fv162 = fv162,
            FvCurrent = fvCurrent,
            FvOutside = fvOutside,
            ProjectedValueTotal = projectedTotal,
            PolicyIncome = policyIncome,
            EstimatedLegacyValue = estimatedLegacy,
            DeathBenefit = deathBenefit,
            FundYears = (int)yearsFunded,
            PolicyGrowthRate = rPol,
            ProjectionGrowthRate = rProj,
            EffectiveDistribution = rDist,
            CombinedTaxRate = rCombined,
            BaselineValue = baselineValue,
            BaselineIncome = baselineIncome,
            BaselineLegacy = baselineLegacy,
            Warnings = warnings
        };
    }

    private static double Clamp(double value, double min, double max) => Math.Min(Math.Max(value, min), max);
}

internal sealed class ExecutiveBonusResult
{
    public double AnnualBonusFunding { get; set; }
    public double DeductibleAmount { get; set; }
    public double EmployerTaxSavings { get; set; }
    public double FederalTaxSavings { get; set; }
    public double StateTaxSavings { get; set; }
    public double EmployeeTaxDrag { get; set; }
    public double NetToPolicy { get; set; }
    public double Admin { get; set; }
    public double EmployerAfterTaxCost { get; set; }
    public double Fv162 { get; set; }
    public double FvCurrent { get; set; }
    public double FvOutside { get; set; }
    public double ProjectedValueTotal { get; set; }
    public double PolicyIncome { get; set; }
    public double EstimatedLegacyValue { get; set; }
    public double DeathBenefit { get; set; }
    public int FundYears { get; set; }
    public double PolicyGrowthRate { get; set; }
    public double ProjectionGrowthRate { get; set; }
    public double EffectiveDistribution { get; set; }
    public double CombinedTaxRate { get; set; }
    public double BaselineValue { get; set; }
    public double BaselineIncome { get; set; }
    public double BaselineLegacy { get; set; }
    public List<string> Warnings { get; set; } = new();
}

internal sealed class DeferredCompCalculator
{
    private const double MaxGrowth = 0.12;
    private const double MaxProjGrowth = 0.12;
    private const double MaxTax = 0.60;

    internal static DeferredCompResult Calculate(AdvancedMarketsPageViewModel req)
    {
        var warnings = new List<string>();

        var deferral = N(req.DeferredComp.DeferralAmount);
        var deferralYearsRaw = Math.Max(0, NI(req.DeferredComp.DeferralYears, 0));
        var payoutYearsRaw = Math.Max(0, NI(req.DeferredComp.DistributionYears, 0));
        var deferralYears = deferralYearsRaw < 1 ? 1 : deferralYearsRaw;
        var payoutYears = payoutYearsRaw < 1 ? 1 : payoutYearsRaw;

        if (deferralYearsRaw < 1) warnings.Add("Deferral years adjusted to 1 for illustration.");
        if (payoutYearsRaw < 1) warnings.Add("Payout years adjusted to 1 for illustration.");

        var r_g = Clamp(N(req.DeferredComp.GrowthRate), 0, MaxGrowth);
        var r_proj = Clamp(N(req.Projection.GrowthRate), 0, MaxProjGrowth);
        var r_tax_future = Clamp(N(req.DeferredComp.FutureTaxRate), 0, MaxTax);

        var ownerComp = N(req.Business.OwnerComp);
        if (deferral > ownerComp * 0.5)
            warnings.Add("Large deferral relative to compensation—confirm affordability/retention intent.");
        if (deferral * deferralYears > ownerComp * 5)
            warnings.Add("Very large cumulative deferral relative to compensation—high concentration/credit-risk exposure.");

        warnings.Add("Nonqualified deferral is unsecured and subject to employer credit risk.");
        warnings.Add("No current employer deduction modeled; deduction/tax effects are illustrated at payout.");
        warnings.Add("Payouts are illustrative straight-line; not actuarial and not plan-document specific.");

        var deferredBalance = ProjectionEngine.FutureValueSeries(deferral, r_g, deferralYears);
        var grossAnnualPayout = deferredBalance / Math.Max(1, payoutYears);
        var employeeTaxDuePerYear = grossAnnualPayout * r_tax_future;
        var netAnnualPayout = grossAnnualPayout * (1 - r_tax_future);

        var employerTaxSavingsPerYear = grossAnnualPayout * r_tax_future;
        var employerNetCashPerYear = grossAnnualPayout - employerTaxSavingsPerYear;

        var fvStrategy = deferredBalance;
        var fvCurrent = ProjectionEngine.FutureValueLump(N(req.Projection.CurrentAssets), r_proj, deferralYears)
                        + ProjectionEngine.FutureValueSeries(N(req.Business.CurrentEmployerRetirementContributions), r_proj, deferralYears);
        var fvOutside = ProjectionEngine.FutureValueSeries(N(req.Projection.AnnualSavings), r_proj, deferralYears);
        var projectedTotal = fvStrategy + fvCurrent + fvOutside;

        var baselineValue = fvCurrent + fvOutside;
        var baselineIncome = RetirementIncomeCalculator.EstimateIncome(baselineValue, r_proj, Math.Max(0.03, Math.Min(0.08, N(req.Projection.DistributionRate))), NI(req.Projection.RetirementDurationYears));
        var baselineLegacy = LegacyValueCalculator.EstimateRemaining(baselineValue, baselineIncome, r_proj, NI(req.Projection.RetirementDurationYears));

        return new DeferredCompResult
        {
            AnnualDeferral = deferral,
            DeferralYears = (int)deferralYears,
            DistributionYears = (int)payoutYears,
            DeferredBalanceAtPayoutStart = deferredBalance,
            GrossAnnualPayout = grossAnnualPayout,
            EmployeeTaxDuePerYear = employeeTaxDuePerYear,
            NetAnnualPayout = netAnnualPayout,
            EmployerTaxSavingsPerYear = employerTaxSavingsPerYear,
            EmployerNetCashPerYear = employerNetCashPerYear,
            FvStrategy = fvStrategy,
            FvCurrent = fvCurrent,
            FvOutside = fvOutside,
            ProjectedValueTotal = projectedTotal,
            FundYears = (int)deferralYears,
            DeferralGrowthRate = r_g,
            ProjectionGrowthRate = r_proj,
            FutureTaxRate = r_tax_future,
            BaselineValue = baselineValue,
            BaselineIncome = baselineIncome,
            BaselineLegacy = baselineLegacy,
            Warnings = warnings
        };
    }

    private static double Clamp(double value, double min, double max) => Math.Min(Math.Max(value, min), max);
}

internal sealed class DeferredCompResult
{
    public double AnnualDeferral { get; set; }
    public int DeferralYears { get; set; }
    public int DistributionYears { get; set; }
    public double DeferredBalanceAtPayoutStart { get; set; }
    public double GrossAnnualPayout { get; set; }
    public double EmployeeTaxDuePerYear { get; set; }
    public double NetAnnualPayout { get; set; }
    public double EmployerTaxSavingsPerYear { get; set; }
    public double EmployerNetCashPerYear { get; set; }
    public double FvStrategy { get; set; }
    public double FvCurrent { get; set; }
    public double FvOutside { get; set; }
    public double ProjectedValueTotal { get; set; }
    public int FundYears { get; set; }
    public double DeferralGrowthRate { get; set; }
    public double ProjectionGrowthRate { get; set; }
    public double FutureTaxRate { get; set; }
    public double BaselineValue { get; set; }
    public double BaselineIncome { get; set; }
    public double BaselineLegacy { get; set; }
    public List<string> Warnings { get; set; } = new();
}

internal sealed class SplitDollarCalculator
{
    private const double MaxPolicyGrowth = 0.12;
    private const double MaxProjectionGrowth = 0.12;

    internal static SplitDollarResult Calculate(AdvancedMarketsPageViewModel req)
    {
        var warnings = new List<string>();

        var premium = N(req.SplitDollar.AnnualPremium);
        var fundingYearsRaw = Math.Max(0, NI(req.SplitDollar.FundingYears, 0));
        var exitYearRaw = Math.Max(0, NI(req.SplitDollar.ExitYear, 0));
        var fundingYears = fundingYearsRaw < 1 ? 1 : fundingYearsRaw;
        var exitYear = exitYearRaw < fundingYears ? fundingYears : exitYearRaw;
        if (fundingYearsRaw < 1) warnings.Add("Funding years adjusted to 1 for illustration.");
        if (exitYearRaw < fundingYearsRaw) warnings.Add("Exit cannot precede end of funding; set to funding years.");

        var rPol = Clamp(N(req.SplitDollar.GrowthRate), 0, MaxPolicyGrowth);
        var rProj = Clamp(N(req.Projection.GrowthRate), 0, MaxProjectionGrowth);

        var ownerComp = N(req.Business.OwnerComp);
        if (premium > ownerComp * 0.5)
            warnings.Add("Large premium relative to compensation—confirm affordability.");
        if (premium * fundingYears > ownerComp * 5)
            warnings.Add("Very large cumulative premium relative to compensation—high concentration/credit/collateral risk.");

        warnings.Add("Split-dollar requires legal/tax review; structure (loan vs endorsement) not illustrated.");
        warnings.Add("Policy performance, charges, and loan interest not modeled; values are illustrative only.");
        warnings.Add("No current employer deduction modeled.");
        warnings.Add("Access/equity value at exit is not guaranteed and depends on policy performance and terms.");

        var accessValueFunded = ProjectionEngine.FutureValueSeries(premium, rPol, fundingYears);
        var yearsToExit = Math.Max(1, exitYear);
        var accessValueExit = accessValueFunded * Math.Pow(1 + rPol, Math.Max(0, yearsToExit - fundingYears));
        var accessibleEquityAtExit = accessValueExit;

        var fvCurrent = ProjectionEngine.FutureValueLump(N(req.Projection.CurrentAssets), rProj, yearsToExit)
                        + ProjectionEngine.FutureValueSeries(N(req.Business.CurrentEmployerRetirementContributions), rProj, yearsToExit);
        var fvOutside = ProjectionEngine.FutureValueSeries(N(req.Projection.AnnualSavings), rProj, yearsToExit);
        var projectedTotal = accessibleEquityAtExit + fvCurrent + fvOutside;

        var distRate = Clamp(N(req.Projection.DistributionRate), 0.03, 0.08);
        var modeledAccessIncome = RetirementIncomeCalculator.EstimateIncome(accessibleEquityAtExit, rPol, distRate, NI(req.Projection.RetirementDurationYears));

        var baselineValue = fvCurrent + fvOutside;
        var baselineIncome = RetirementIncomeCalculator.EstimateIncome(baselineValue, rProj, distRate, NI(req.Projection.RetirementDurationYears));
        var baselineLegacy = LegacyValueCalculator.EstimateRemaining(baselineValue, baselineIncome, rProj, NI(req.Projection.RetirementDurationYears));

        return new SplitDollarResult
        {
            AnnualPremium = premium,
            FundingYears = (int)fundingYears,
            ExitYear = (int)exitYear,
            AccessibleEquityAtExit = accessibleEquityAtExit,
            ModeledAccessIncome = modeledAccessIncome,
            DeathBenefitMetric = N(req.SplitDollar.DeathBenefit),
            FvCurrent = fvCurrent,
            FvOutside = fvOutside,
            ProjectedValueTotal = projectedTotal,
            FundYears = (int)yearsToExit,
            PolicyGrowthRate = rPol,
            ProjectionGrowthRate = rProj,
            DistributionRate = distRate,
            BaselineValue = baselineValue,
            BaselineIncome = baselineIncome,
            BaselineLegacy = baselineLegacy,
            Warnings = warnings
        };
    }

    private static double Clamp(double value, double min, double max) => Math.Min(Math.Max(value, min), max);
}

internal sealed class SplitDollarResult
{
    public double AnnualPremium { get; set; }
    public int FundingYears { get; set; }
    public int ExitYear { get; set; }
    public double AccessibleEquityAtExit { get; set; }
    public double ModeledAccessIncome { get; set; }
    public double DeathBenefitMetric { get; set; }
    public double FvCurrent { get; set; }
    public double FvOutside { get; set; }
    public double ProjectedValueTotal { get; set; }
    public int FundYears { get; set; }
    public double PolicyGrowthRate { get; set; }
    public double ProjectionGrowthRate { get; set; }
    public double DistributionRate { get; set; }
    public double BaselineValue { get; set; }
    public double BaselineIncome { get; set; }
    public double BaselineLegacy { get; set; }
    public List<string> Warnings { get; set; } = new();
}

internal sealed class TaxDiversificationCalculator
{
    private const double MaxGrowth = 0.12;
    private const double MaxDrag = 0.03;
    private const double MaxTaxQ = 0.60;
    private const double MaxTaxT = 0.40;

    internal static TaxDiversificationResult Calculate(AdvancedMarketsPageViewModel req)
    {
        var warnings = new List<string>();

        int years;
        if (req.Client.OwnerAge == null || req.Client.RetirementAge == null || req.Client.RetirementAge <= req.Client.OwnerAge)
        {
            years = 15;
            warnings.Add("Retirement ages not provided/invalid — using 15-year horizon for illustration.");
        }
        else
        {
            years = Math.Max(1, (req.Client.RetirementAge ?? 0) - (req.Client.OwnerAge ?? 0));
        }

        var r_g = Clamp(N(req.Projection.GrowthRate), 0, MaxGrowth);
        var dragT = req.Projection.DiscountRate.HasValue ? Clamp(N(req.Projection.DiscountRate), 0, MaxDrag) : 0.015;
        if (!req.Projection.DiscountRate.HasValue)
            warnings.Add("Tax drag defaulted to 1.5% for taxable assets; illustrative only.");
        var rT_net = Math.Max(0, r_g - dragT);

        var futureTaxQ = Clamp(req.Tax.FutureTaxRate ?? req.Tax.FederalRate ?? 0, 0, MaxTaxQ);
        var futureTaxT = Clamp(req.Tax.CapitalGainsRate ?? req.Tax.FutureTaxRate ?? req.Tax.FederalRate ?? 0, 0, MaxTaxT);

        var wQ = 1.0 / 3.0;
        var wT = 1.0 / 3.0;
        var wF = 1.0 / 3.0;
        warnings.Add("Allocation weights defaulted equally across tax buckets for illustration.");

        var S = N(req.Projection.AnnualSavings);
        var Q0 = N(req.Client.CurrentQualifiedAssets);
        var T0 = N(req.Client.CurrentTaxableAssets);
        var F0 = N(req.Client.CurrentTaxFreeAssets);

        var Q = ProjectionEngine.FutureValueSeries(S * wQ, r_g, years) + ProjectionEngine.FutureValueLump(Q0, r_g, years);
        var T = ProjectionEngine.FutureValueSeries(S * wT, rT_net, years) + ProjectionEngine.FutureValueLump(T0, rT_net, years);
        var F = ProjectionEngine.FutureValueSeries(S * wF, r_g, years) + ProjectionEngine.FutureValueLump(F0, r_g, years);
        var total = Q + T + F;

        if (total <= 0)
            warnings.Add("No assets/savings to illustrate.");

        var afterTaxValue = Q * (1 - futureTaxQ) + T * (1 - futureTaxT) + F;

        var distRate = Clamp(N(req.Projection.DistributionRate), 0.03, 0.08);
        var grossIncome = RetirementIncomeCalculator.EstimateIncome(total, r_g, distRate, NI(req.Projection.RetirementDurationYears));
        var sQ = total <= 0 ? 0 : Q / total;
        var sT = total <= 0 ? 0 : T / total;
        var sF = total <= 0 ? 0 : F / total;
        var afterTaxIncome = grossIncome * (sQ * (1 - futureTaxQ) + sT * (1 - futureTaxT) + sF);

        var fvCurrent = ProjectionEngine.FutureValueLump(N(req.Projection.CurrentAssets), r_g, years)
                        + ProjectionEngine.FutureValueSeries(N(req.Business.CurrentEmployerRetirementContributions), r_g, years);
        var fvOutside = ProjectionEngine.FutureValueSeries(N(req.Projection.AnnualSavings), r_g, years);

        var baselineValue = fvCurrent + fvOutside;
        var baselineIncome = RetirementIncomeCalculator.EstimateIncome(baselineValue, r_g, distRate, NI(req.Projection.RetirementDurationYears));
        var baselineLegacy = LegacyValueCalculator.EstimateRemaining(baselineValue, baselineIncome, r_g, NI(req.Projection.RetirementDurationYears));

        warnings.Add("Tax rates and tax drag are illustrative; sequencing risk not modeled.");

        return new TaxDiversificationResult
        {
            AnnualSavingsCommitment = S,
            Years = years,
            QualifiedBucket = Q,
            TaxableBucket = T,
            TaxFreeBucket = F,
            TotalProjectedValue = total,
            AfterTaxValueAtRetirementStart = afterTaxValue,
            AfterTaxIncome = afterTaxIncome,
            AllocationWeightQ = wQ,
            AllocationWeightT = wT,
            AllocationWeightF = wF,
            TaxableDragUsed = dragT,
            GrossGrowthRate = r_g,
            DistributionRateUsed = distRate,
            FutureTaxRateQ = futureTaxQ,
            FutureTaxRateT = futureTaxT,
            FvCurrent = fvCurrent,
            FvOutside = fvOutside,
            BaselineValue = baselineValue,
            BaselineIncome = baselineIncome,
            BaselineLegacy = baselineLegacy,
            Warnings = warnings
        };
    }

    private static double Clamp(double value, double min, double max) => Math.Min(Math.Max(value, min), max);
}

internal sealed class TaxDiversificationResult
{
    public double AnnualSavingsCommitment { get; set; }
    public int Years { get; set; }
    public double QualifiedBucket { get; set; }
    public double TaxableBucket { get; set; }
    public double TaxFreeBucket { get; set; }
    public double TotalProjectedValue { get; set; }
    public double AfterTaxValueAtRetirementStart { get; set; }
    public double AfterTaxIncome { get; set; }
    public double AllocationWeightQ { get; set; }
    public double AllocationWeightT { get; set; }
    public double AllocationWeightF { get; set; }
    public double TaxableDragUsed { get; set; }
    public double GrossGrowthRate { get; set; }
    public double DistributionRateUsed { get; set; }
    public double FutureTaxRateQ { get; set; }
    public double FutureTaxRateT { get; set; }
    public double FvCurrent { get; set; }
    public double FvOutside { get; set; }
    public double BaselineValue { get; set; }
    public double BaselineIncome { get; set; }
    public double BaselineLegacy { get; set; }
    public List<string> Warnings { get; set; } = new();
}

internal sealed class ComboDb401kCalculator
{
    private const double MaxFed = 0.40;
    private const double MaxState = 0.15;
    private const double MaxCombined = 0.50;
    private const double MaxGrowth = 0.12;
    private const double MinDistribution = 0.03;
    private const double MaxDistribution = 0.08;
    private const double MaxPct = 1.0;
    private const double WarnContribution = 500_000;
    private const double StrongWarnContribution = 1_000_000;
    private const double CapContribution = 2_000_000;

    internal static ComboResult Calculate(AdvancedMarketsPageViewModel req)
    {
        var warnings = new List<string>();

        var ownerAge = req.Client.OwnerAge ?? 0;
        var retireAge = req.Client.RetirementAge ?? ownerAge;
        var yearsToRet = Math.Max(1, retireAge - ownerAge);
        if (yearsToRet <= 2) warnings.Add("Short funding runway—combo results are highly sensitive.");

        var g = Clamp(N(req.Projection.GrowthRate), 0, MaxGrowth);
        var dist = Clamp(N(req.Projection.DistributionRate), MinDistribution, MaxDistribution);

        var fed = Clamp(N(req.Tax.FederalRate), 0, MaxFed);
        var state = Clamp(N(req.Tax.StateRate), 0, MaxState);
        var combined = Clamp(fed + state - (fed * state), 0, MaxCombined);

        var employerPct = Clamp(N(req.Combo.EmployerPct), 0, MaxPct);
        var safeHarborPct = Clamp(N(req.Combo.SafeHarborPct), 0, MaxPct);
        var profitSharingPct = Clamp(N(req.Combo.ProfitSharingPct), 0, MaxPct);
        var testingBufferPct = Clamp(N(req.Combo.TestingBufferPct), 0, MaxPct);
        var employeeDeferral = N(req.Combo.EmployeeDeferral);
        var targetTotal = N(req.Combo.TargetTotal);
        var ownerComp = N(req.Business.OwnerComp);

        var employerContribution = ownerComp * employerPct;
        var safeHarborContribution = ownerComp * safeHarborPct;
        var profitSharing = ownerComp * profitSharingPct;
        var testingBuffer = ownerComp * testingBufferPct;

        var baseCombo = employeeDeferral + employerContribution + safeHarborContribution + profitSharing + testingBuffer;
        var contribution = targetTotal > 0 ? targetTotal : Math.Max(baseCombo, ownerComp * 0.25);

        if (contribution > WarnContribution) warnings.Add("Large illustrative contribution—confirm affordability and plan limits.");
        if (contribution > StrongWarnContribution) warnings.Add("Very large illustrative contribution—likely constrained by testing/limits.");
        if (contribution > CapContribution)
        {
            contribution = CapContribution;
            warnings.Add("Contribution capped at illustration limit (2,000,000).");
        }

        var employeeCostFactor = Clamp(N(req.Combo.EmployeeCostFactor), 0, MaxPct);
        var admin = 0.0;

        var deductible = contribution;
        var federalSavings = deductible * fed;
        var stateSavings = deductible * state;
        var combinedSavings = deductible * combined;

        if (combinedSavings > contribution * 0.6)
        {
            combinedSavings = contribution * 0.6;
            warnings.Add("Illustrative tax savings were limited by guardrails. Verify rates and contribution assumptions.");
        }

        var employeeCost = contribution * employeeCostFactor;
        var netCost = contribution + employeeCost + admin - combinedSavings;

        var fundYears = yearsToRet;
        var fvCombo = ProjectionEngine.FutureValueSeries(contribution, g, fundYears);
        var fvCurrent = ProjectionEngine.FutureValueLump(N(req.Projection.CurrentAssets), g, fundYears)
                        + ProjectionEngine.FutureValueSeries(N(req.Business.CurrentEmployerRetirementContributions), g, fundYears);
        var fvOutside = ProjectionEngine.FutureValueSeries(N(req.Projection.AnnualSavings), g, fundYears);
        var projectedTotal = fvCombo + fvCurrent + fvOutside;

        var retirementIncome = RetirementIncomeCalculator.EstimateIncome(projectedTotal, g, dist, NI(req.Projection.RetirementDurationYears));
        var legacy = LegacyValueCalculator.EstimateRemaining(projectedTotal, retirementIncome, g, NI(req.Projection.RetirementDurationYears));

        var baselineValue = fvCurrent + fvOutside;
        var baselineIncome = RetirementIncomeCalculator.EstimateIncome(baselineValue, g, dist, NI(req.Projection.RetirementDurationYears));
        var baselineLegacy = LegacyValueCalculator.EstimateRemaining(baselineValue, baselineIncome, g, NI(req.Projection.RetirementDurationYears));

        return new ComboResult
        {
            Contribution = contribution,
            Deductible = deductible,
            FederalTaxSavings = federalSavings,
            StateTaxSavings = stateSavings,
            CombinedTaxSavings = combinedSavings,
            EmployeeCost = employeeCost,
            Admin = admin,
            NetCost = netCost,
            FvCombo = fvCombo,
            FvCurrent = fvCurrent,
            FvOutside = fvOutside,
            ProjectedValueTotal = projectedTotal,
            RetirementIncome = retirementIncome,
            Legacy = legacy,
            FundYears = fundYears,
            EffectiveGrowth = g,
            EffectiveDistribution = dist,
            CombinedTaxRate = combined,
            BaselineValue = baselineValue,
            BaselineIncome = baselineIncome,
            BaselineLegacy = baselineLegacy,
            Warnings = warnings,
            EmployerPct = employerPct,
            SafeHarborPct = safeHarborPct,
            ProfitSharingPct = profitSharingPct,
            TestingBufferPct = testingBufferPct,
            EmployeeDeferral = employeeDeferral
        };
    }

    private static double Clamp(double value, double min, double max) => Math.Min(Math.Max(value, min), max);
}

internal sealed class ComboResult
{
    public double Contribution { get; set; }
    public double Deductible { get; set; }
    public double FederalTaxSavings { get; set; }
    public double StateTaxSavings { get; set; }
    public double CombinedTaxSavings { get; set; }
    public double EmployeeCost { get; set; }
    public double Admin { get; set; }
    public double NetCost { get; set; }
    public double FvCombo { get; set; }
    public double FvCurrent { get; set; }
    public double FvOutside { get; set; }
    public double ProjectedValueTotal { get; set; }
    public double RetirementIncome { get; set; }
    public double Legacy { get; set; }
    public int FundYears { get; set; }
    public double EffectiveGrowth { get; set; }
    public double EffectiveDistribution { get; set; }
    public double CombinedTaxRate { get; set; }
    public double BaselineValue { get; set; }
    public double BaselineIncome { get; set; }
    public double BaselineLegacy { get; set; }
    public List<string> Warnings { get; set; } = new();
    public double EmployerPct { get; set; }
    public double SafeHarborPct { get; set; }
    public double ProfitSharingPct { get; set; }
    public double TestingBufferPct { get; set; }
    public double EmployeeDeferral { get; set; }
}

internal static double N(double? v, double fallback = 0) => v ?? fallback;
internal static int NI(int? v, int fallback = 0) => v ?? fallback;

internal static class ProjectionEngine
{
    public static double FutureValueLump(double presentValue, double growthRate, int years)
    {
        return presentValue * Math.Pow(1 + growthRate, years);
    }

    public static double FutureValueSeries(double annualContribution, double growthRate, int years)
    {
        if (years <= 0 || annualContribution == 0) return 0;
        if (Math.Abs(growthRate) < 1e-9) return annualContribution * years;
        var fv = annualContribution * (Math.Pow(1 + growthRate, years) - 1) / growthRate;
        return fv;
    }
}

internal static class RetirementIncomeCalculator
{
    public static double EstimateIncome(double balanceAtRetirement, double growthRate, double distributionRate, int durationYears)
    {
        // Use PMT style sustainable withdrawal that amortizes over durationYears with growthRate
        var r = growthRate;
        var g = distributionRate <= 0 ? 0.04 : distributionRate;

        if (durationYears <= 0)
            return balanceAtRetirement * g;

        // real annuity with growth r and withdrawal g approximated by amortization:
        var effectiveRate = Math.Max(0.0001, r);
        var pmt = balanceAtRetirement * effectiveRate / (1 - Math.Pow(1 + effectiveRate, -durationYears));

        // blend toward requested distribution rate
        var target = balanceAtRetirement * g;
        return (pmt * 0.6) + (target * 0.4);
    }
}

internal static class LegacyValueCalculator
{
    public static double EstimateRemaining(double startingBalance, double annualWithdrawal, double growthRate, int durationYears)
    {
        double bal = startingBalance;
        for (int i = 0; i < durationYears; i++)
        {
            bal = (bal - annualWithdrawal);
            if (bal <= 0) return 0;
            bal *= (1 + growthRate);
        }
        return bal;
    }
}

internal class TaxImpactResult
{
    public double CombinedRate { get; set; }
}

internal class TaxImpactCalculator
{
    public TaxImpactResult ComputeCombinedRate(double federal, double state)
    {
        var combined = federal + state - (federal * state);
        return new TaxImpactResult { CombinedRate = combined };
    }
}

internal static class StrategyMetricsBuilder
{
    public static List<MetricVm> Build(
        AdvancedMarketsPageViewModel req,
        double contribution,
        double employeeCost,
        double admin,
        double deduction,
        double fvStrategy,
        double retirementIncome,
        double legacy)
    {
        var list = new List<MetricVm>();

        switch (req.Strategy.Selected)
        {
            case StrategyKind.DefinedBenefit:
                list.Add(new MetricVm { Label = "Annual contribution (est.)", Value = contribution });
                list.Add(new MetricVm { Label = "Employee cost (est.)", Value = employeeCost });
                list.Add(new MetricVm { Label = "Admin/actuarial cost", Value = admin });
                list.Add(new MetricVm { Label = "Deductible amount", Value = deduction, Note = "Subject to actuarial/IRS limits" });
                list.Add(new MetricVm { Label = "Projected value from DB funding", Value = fvStrategy });
                break;
            case StrategyKind.CashBalance:
                list.Add(new MetricVm { Label = "Cash balance contribution", Value = contribution });
                list.Add(new MetricVm { Label = "Employee cost (est.)", Value = employeeCost });
                list.Add(new MetricVm { Label = "Admin cost", Value = admin });
                list.Add(new MetricVm { Label = "Deductible amount", Value = deduction });
                list.Add(new MetricVm { Label = "Projected CB account value", Value = fvStrategy });
                break;
            case StrategyKind.ComboDb401k:
                list.Add(new MetricVm { Label = "Employee deferral", Value = N(req.Combo.EmployeeDeferral) });
                list.Add(new MetricVm { Label = "Employer %", Value = N(req.Combo.EmployerPct), Format = "percent" });
                list.Add(new MetricVm { Label = "Profit sharing %", Value = N(req.Combo.ProfitSharingPct), Format = "percent" });
                list.Add(new MetricVm { Label = "Safe harbor %", Value = N(req.Combo.SafeHarborPct), Format = "percent" });
                list.Add(new MetricVm { Label = "Testing buffer %", Value = N(req.Combo.TestingBufferPct), Format = "percent" });
                list.Add(new MetricVm { Label = "Total contribution target", Value = contribution });
                break;
            case StrategyKind.ExecutiveBonus162:
                list.Add(new MetricVm { Label = "Illustrative annual bonus funding", Value = contribution });
                list.Add(new MetricVm { Label = "Projected policy value (illustrative)", Value = fvStrategy });
                list.Add(new MetricVm { Label = "Illustrative death benefit (not guaranteed)", Value = N(req.ExecutiveBonus.AnnualBonus) * N(req.ExecutiveBonus.DeathBenefitMultiple) });
                break;
            case StrategyKind.DeferredComp:
                var dcBalance = fvStrategy;
                var dcAnnualDist = retirementIncome;
                var futureTax = dcAnnualDist * N(req.DeferredComp.FutureTaxRate);
                list.Add(new MetricVm { Label = "Annual deferral", Value = contribution });
                list.Add(new MetricVm { Label = "Projected deferred balance at payout start", Value = dcBalance });
                list.Add(new MetricVm { Label = "Illustrative straight-line annual payout (gross)", Value = dcAnnualDist });
                list.Add(new MetricVm { Label = "Illustrative annual payout (after future tax)", Value = dcAnnualDist * (1 - N(req.DeferredComp.FutureTaxRate)) });
                list.Add(new MetricVm { Label = "Illustrative employer tax savings at payout", Value = futureTax });
                list.Add(new MetricVm { Label = "Employer net cash per payout year", Value = dcAnnualDist - futureTax });
                break;
            case StrategyKind.SplitDollar:
                var accessValue = fvStrategy;
                list.Add(new MetricVm { Label = "Annual premium", Value = contribution });
                list.Add(new MetricVm { Label = "Projected access value at exit", Value = accessValue, Note = $"Exit year {req.SplitDollar.ExitYear}" });
                list.Add(new MetricVm { Label = "Estimated death benefit", Value = N(req.SplitDollar.DeathBenefit) });
                list.Add(new MetricVm { Label = "Exit/unwind year", Value = NI(req.SplitDollar.ExitYear), Format = "year" });
                break;
            case StrategyKind.TaxDiversification:
                list.Add(new MetricVm { Label = "Projected qualified bucket at retirement", Value = fvStrategy }); // placeholder; overwritten by dedicated builder
                break;
        }

        // Common
        list.Add(new MetricVm { Label = "Projected retirement income (annual)", Value = retirementIncome });
        list.Add(new MetricVm { Label = "Estimated legacy value", Value = legacy });
        return list;
    }

    public static List<MetricVm> BuildDefinedBenefit(DbResult db)
    {
        var list = new List<MetricVm>
        {
            new MetricVm { Label = "Annual contribution (est.)", Value = db.Contribution },
            new MetricVm { Label = "Employee cost (est.)", Value = db.EmployeeCost, Note = "Estimated staff cost (not discrimination-tested)." },
            new MetricVm { Label = "Admin/actuarial cost", Value = db.Admin },
            new MetricVm { Label = "Deductible amount", Value = db.Deductible, Note = "Subject to IRS/actuarial limits" },
            new MetricVm { Label = "Projected value from DB funding", Value = db.FvDb },
            new MetricVm { Label = "Projected retirement income (modeled draw)", Value = db.RetirementIncome },
            new MetricVm { Label = "Estimated legacy value", Value = db.Legacy }
        };
        return list;
    }

    public static (List<MetricVm> metrics, List<MetricVm> informational) BuildCashBalance(CashBalanceResult cb, AdvancedMarketsPageViewModel req)
    {
        var list = new List<MetricVm>
        {
            new MetricVm { Label = "Cash balance contribution (illustrative)", Value = cb.Contribution },
            new MetricVm { Label = "Employee cost (est.)", Value = cb.EmployeeCost, Note = "Estimated staff cost (not discrimination-tested)." },
            new MetricVm { Label = "Admin cost", Value = cb.Admin },
            new MetricVm { Label = "Deductible amount", Value = cb.Deductible, Note = "Subject to plan design/testing" },
            new MetricVm { Label = "Projected cash balance account value (illustrative crediting)", Value = cb.FvCb },
            new MetricVm { Label = "Projected retirement income (modeled draw)", Value = cb.RetirementIncome },
            new MetricVm { Label = "Estimated legacy value", Value = cb.Legacy }
        };

        var informational = new List<MetricVm>();
        if (N(req.CashBalance.Current401kDeferral) > 0)
        {
            informational.Add(new MetricVm
            {
                Label = "Current 401(k) deferral (informational; not included in CB contribution)",
                Value = N(req.CashBalance.Current401kDeferral)
            });
        }

        return (list, informational);
    }

    public static (List<MetricVm> metrics, List<MetricVm> informational) BuildCombo(ComboResult combo)
    {
        var list = new List<MetricVm>
        {
            new MetricVm { Label = "Owner 401(k) deferral", Value = combo.EmployeeDeferral },
            new MetricVm { Label = "Employer contribution %", Value = combo.EmployerPct, Format = "percent" },
            new MetricVm { Label = "Safe harbor %", Value = combo.SafeHarborPct, Format = "percent" },
            new MetricVm { Label = "Profit sharing %", Value = combo.ProfitSharingPct, Format = "percent" },
            new MetricVm { Label = "Testing buffer %", Value = combo.TestingBufferPct, Format = "percent", Note = "Illustrative testing buffer; not a compliance result" },
            new MetricVm { Label = "Total combo contribution", Value = combo.Contribution },
            new MetricVm { Label = "Projected combo funding value (illustrative)", Value = combo.FvCombo },
            new MetricVm { Label = "Projected retirement income (modeled draw)", Value = combo.RetirementIncome },
            new MetricVm { Label = "Estimated legacy value", Value = combo.Legacy }
        };

        var informational = new List<MetricVm>();
        return (list, informational);
    }

    public static List<MetricVm> BuildExecutiveBonus(ExecutiveBonusResult bonus)
    {
        var list = new List<MetricVm>
        {
            new MetricVm { Label = "Illustrative annual bonus funding", Value = bonus.AnnualBonusFunding },
            new MetricVm { Label = "Illustrative employer tax savings", Value = bonus.EmployerTaxSavings, Note = "Bonus is deductible compensation" },
            new MetricVm { Label = "Illustrative employee tax drag", Value = bonus.EmployeeTaxDrag, Note = "Bonus is taxable income to employee; no gross-up assumed" },
            new MetricVm { Label = "Net to policy after employee tax", Value = bonus.NetToPolicy },
            new MetricVm { Label = "Employer after-tax cost", Value = bonus.EmployerAfterTaxCost },
            new MetricVm { Label = "Projected policy value (illustrative)", Value = bonus.Fv162 },
            new MetricVm { Label = "Illustrative death benefit (not guaranteed)", Value = bonus.DeathBenefit },
            new MetricVm { Label = "Modeled policy-based retirement access", Value = bonus.PolicyIncome },
            new MetricVm { Label = "Estimated legacy value (policy value)", Value = bonus.EstimatedLegacyValue }
        };

        return list;
    }

    public static List<MetricVm> BuildDeferredComp(DeferredCompResult dc)
    {
        var list = new List<MetricVm>
        {
            new MetricVm { Label = "Annual deferral", Value = dc.AnnualDeferral },
            new MetricVm { Label = "Projected deferred balance at payout start", Value = dc.DeferredBalanceAtPayoutStart },
            new MetricVm { Label = "Illustrative straight-line annual payout (gross)", Value = dc.GrossAnnualPayout },
            new MetricVm { Label = "Illustrative annual payout (after future tax)", Value = dc.NetAnnualPayout },
            new MetricVm { Label = "Illustrative employer tax savings at payout", Value = dc.EmployerTaxSavingsPerYear },
            new MetricVm { Label = "Employer net cash per payout year", Value = dc.EmployerNetCashPerYear }
        };

        return list;
    }

    public static List<MetricVm> BuildSplitDollar(SplitDollarResult sd)
    {
        var list = new List<MetricVm>
        {
            new MetricVm { Label = "Annual premium (illustrative)", Value = sd.AnnualPremium },
            new MetricVm { Label = "Projected access value at exit (illustrative)", Value = sd.AccessibleEquityAtExit, Note = $"Exit year {sd.ExitYear}" },
            new MetricVm { Label = "Illustrative death benefit (not guaranteed)", Value = sd.DeathBenefitMetric },
            new MetricVm { Label = "Exit/unwind year", Value = sd.ExitYear, Format = "year" },
            new MetricVm { Label = "Illustrative policy-based access (annual)", Value = sd.ModeledAccessIncome }
        };

        return list;
    }

    public static List<MetricVm> BuildTaxDiversification(TaxDiversificationResult td)
    {
        var list = new List<MetricVm>
        {
            new MetricVm { Label = "Projected qualified bucket at retirement", Value = td.QualifiedBucket },
            new MetricVm { Label = "Projected taxable bucket at retirement (net of tax drag)", Value = td.TaxableBucket },
            new MetricVm { Label = "Projected tax-free bucket at retirement", Value = td.TaxFreeBucket },
            new MetricVm { Label = "After-tax value at retirement start (illustrative)", Value = td.AfterTaxValueAtRetirementStart },
            new MetricVm { Label = "Modeled after-tax income (illustrative)", Value = td.AfterTaxIncome },
            new MetricVm { Label = "Bucket mix at retirement: Qualified %", Value = td.QualifiedBucket / Math.Max(1, td.TotalProjectedValue), Format = "percent" },
            new MetricVm { Label = "Bucket mix at retirement: Taxable %", Value = td.TaxableBucket / Math.Max(1, td.TotalProjectedValue), Format = "percent" },
            new MetricVm { Label = "Bucket mix at retirement: Tax-free %", Value = td.TaxFreeBucket / Math.Max(1, td.TotalProjectedValue), Format = "percent" },
            new MetricVm { Label = "Modeled annual savings commitment", Value = td.AnnualSavingsCommitment }
        };
        return list;
    }
}

internal static class ChartBuilder
{
    public static List<ChartSeriesVm> BuildCharts(
        int years,
        double annualTaxSavings,
        double strategyGrowthRate,
        double retirementIncome,
        AdvancedMarketsPageViewModel req,
        double fvCurrent,
        double fvStrategy,
        double fvOutside,
        double annualStrategyContribution,
        double outsideGrowthRate)
    {
        var charts = new List<ChartSeriesVm>();

        // Annual tax savings (only if non-zero/applicable)
        if (annualTaxSavings > 0)
        {
            var taxSavingsSeries = new ChartSeriesVm
            {
                Name = "Annual Tax Savings (illustrative)",
                Data = Enumerable.Range(1, Math.Max(1, years)).Select(_ => annualTaxSavings).ToList(),
                Labels = Enumerable.Range(1, Math.Max(1, years)).Select(i => $"Year {i}").ToList()
            };
            charts.Add(taxSavingsSeries);
        }

        // Cumulative value with separate growth rates for strategy vs outside/current
        var cum = new List<double>();
        double strat = 0;
        double other = N(req.Projection.CurrentAssets);
        for (int i = 0; i < years; i++)
        {
            strat = strat * (1 + strategyGrowthRate) + annualStrategyContribution;
            other = other * (1 + outsideGrowthRate) + N(req.Projection.AnnualSavings);
            cum.Add(strat + other);
        }
        charts.Add(new ChartSeriesVm
        {
            Name = "Total projected value (strategy + current + outside)",
            Data = cum,
            Labels = Enumerable.Range(1, years).Select(i => $"Yr {i}").ToList()
        });

        // Retirement income sources split proportionally
        var totalFv = fvCurrent + fvStrategy + fvOutside;
        var strategyPortion = totalFv <= 0 ? 0 : fvStrategy / totalFv;
        var existingPortion = totalFv <= 0 ? 0 : fvCurrent / totalFv;
        var outsidePortion = totalFv <= 0 ? 0 : fvOutside / totalFv;

        charts.Add(new ChartSeriesVm
        {
            Name = "Retirement Income Sources",
            Data = new List<double> { retirementIncome * strategyPortion, retirementIncome * existingPortion, retirementIncome * outsidePortion },
            Labels = new List<string> { "Strategy", "Current Assets", "Outside Savings" }
        });

        // Asset buckets (current mix) with defensible fallback across all strategies
        var bucketQualified = N(req.Client.CurrentQualifiedAssets);
        var bucketTaxable = N(req.Client.CurrentTaxableAssets);
        var bucketTaxFree = N(req.Client.CurrentTaxFreeAssets);
        var bucketLabels = new List<string> { "Qualified", "Taxable", "Tax-Free" };
        var bucketData = new List<double>();
        var fallbackUsed = false;

        var bucketSum = bucketQualified + bucketTaxable + bucketTaxFree;

        if (bucketSum <= 0)
        {
            // Fallback sources (ordered)
            var fallback = N(req.Projection.CurrentAssets);

            // Extendable hook: add other strategy-specific current-asset sources here if needed.
            if (fallback > 0)
            {
                bucketQualified = fallback;
                bucketTaxable = 0;
                bucketTaxFree = 0;
                bucketLabels = new List<string> { "Unclassified current assets (entered)" };
                fallbackUsed = true;
            }
        }

        // Recalculate after any fallback
        bucketSum = bucketQualified + bucketTaxable + bucketTaxFree;

        bucketData = bucketLabels.Count == 3
            ? new List<double> { bucketQualified, bucketTaxable, bucketTaxFree }
            : new List<double> { bucketQualified };

        charts.Add(new ChartSeriesVm
        {
            Name = fallbackUsed ? "Current Asset Buckets (unclassified assets shown)" : "Current Asset Buckets",
            Data = bucketData,
            Labels = bucketLabels
        });

        return charts;
    }
}

internal static class SuitabilityEngine
{
    public static List<SuitabilityFlagVm> Build(AdvancedMarketsPageViewModel req, double contribution, double employeeCost, int runwayYears)
    {
        var list = new List<SuitabilityFlagVm>();

        if (req.Business.EmployeeCount > 30)
            list.Add(new SuitabilityFlagVm { Severity = "warn", Message = "Many employees — verify feasibility and cost testing." });

        if (runwayYears < 5 && req.Strategy.Selected == StrategyKind.DefinedBenefit)
            list.Add(new SuitabilityFlagVm { Severity = "warn", Message = "Short timeline to retirement — consider cash balance or executive carve-out instead of pure DB." });

        if (employeeCost > contribution * 0.35)
            list.Add(new SuitabilityFlagVm { Severity = "bad", Message = "High projected employee cost — may fail affordability or nondiscrimination goals." });

        if (contribution <= 0)
            list.Add(new SuitabilityFlagVm { Severity = "bad", Message = "Contribution not provided — cannot illustrate strategy." });

        if (req.Strategy.Selected == StrategyKind.SplitDollar)
            list.Add(new SuitabilityFlagVm { Severity = "warn", Message = "Split-dollar requires legal/tax review; collateral/loan terms not illustrated here." });

        if (list.Count == 0)
            list.Add(new SuitabilityFlagVm { Severity = "good", Message = "Profile appears suitable subject to actuarial and legal review." });

        return list;
    }
}

internal static class DisclaimerLibrary
{
    public static List<string> Standard() => new()
    {
        "Hypothetical illustration only. Not tax, legal, or actuarial advice.",
        "Actual plan design requires CPA/ERISA/actuarial/carrier review.",
        "Values are non-guaranteed and depend on assumptions and future tax law.",
        "Employee cost estimates are illustrative and not discrimination testing.",
        "Split-dollar and nonqualified arrangements require legal review.",
        "Insurance-based values, if any, are non-guaranteed unless stated otherwise."
    };
}
}
