using System;

namespace AgentPortal.Services;

public enum FilingStatus
{
    Single,
    MarriedFilingJoint,
    HeadOfHousehold,
    MarriedFilingSeparate
}

public sealed record TaxEstimate(
    decimal Profit,
    decimal SeTax,
    decimal HalfSeDeduction,
    decimal StandardDeduction,
    decimal TaxableIncome,
    decimal FederalIncomeTax,
    decimal ArizonaTax,
    decimal TotalEstimatedTax,
    decimal EffectiveRate // TotalEstimatedTax / Profit
);

public static class TaxEstimator2026
{
    // 2026 standard deductions (IRS IR-2025-103)
    // Single/MFS: 16,100 | MFJ: 32,200 | HOH: 24,150
    public static decimal StandardDeduction(FilingStatus status) => status switch
    {
        FilingStatus.MarriedFilingJoint => 32200m,
        FilingStatus.HeadOfHousehold => 24150m,
        FilingStatus.Single => 16100m,
        FilingStatus.MarriedFilingSeparate => 16100m,
        _ => 16100m
    };

    // Federal marginal brackets (2026)
    // (cap, rate) where cap is top of bracket for TAXABLE income
    private static (decimal cap, decimal rate)[] FederalBrackets2026(FilingStatus status) => status switch
    {
        FilingStatus.MarriedFilingJoint => new[]
        {
            (24800m,  0.10m),
            (100800m, 0.12m),
            (211400m, 0.22m),
            (403550m, 0.24m),
            (512450m, 0.32m),
            (768700m, 0.35m),
            (decimal.MaxValue, 0.37m)
        },

        FilingStatus.HeadOfHousehold => new[]
        {
            (17700m,  0.10m),
            (67450m,  0.12m),
            (105700m, 0.22m),
            (201750m, 0.24m),
            (256200m, 0.32m),
            (640600m, 0.35m),
            (decimal.MaxValue, 0.37m)
        },

        FilingStatus.MarriedFilingSeparate => new[]
        {
            (12400m,  0.10m),
            (50400m,  0.12m),
            (105700m, 0.22m),
            (201775m, 0.24m),
            (256225m, 0.32m),
            (384350m, 0.35m),
            (decimal.MaxValue, 0.37m)
        },

        _ => new[] // Single
        {
            (12400m,  0.10m),
            (50400m,  0.12m),
            (105700m, 0.22m),
            (201775m, 0.24m),
            (256225m, 0.32m),
            (640600m, 0.35m),
            (decimal.MaxValue, 0.37m)
        }
    };

    // AZ flat tax (2.5%)
    private const decimal ArizonaFlatRate = 0.025m;

    // SE tax constants
    private const decimal SeAdjustment = 0.9235m; // SE earnings = profit * 92.35%
    private const decimal SocialSecurityRate = 0.124m;
    private const decimal MedicareRate = 0.029m;

    // Additional Medicare Tax (0.9%) — kicks in above threshold
    private const decimal AdditionalMedicareRate = 0.009m;
    private static decimal AdditionalMedicareThreshold(FilingStatus status) => status switch
    {
        FilingStatus.MarriedFilingJoint => 250000m,
        FilingStatus.MarriedFilingSeparate => 125000m,
        FilingStatus.Single => 200000m,
        FilingStatus.HeadOfHousehold => 200000m,
        _ => 200000m
    };

    // 2026 Social Security wage base
    private const decimal SocialSecurityWageBase2026 = 184500m;

    public static TaxEstimate EstimateAzSelfEmployedReserve(
        decimal profit,
        FilingStatus status,
        bool includeArizona = true,
        bool includeStandardDeduction = true,
        bool includeHalfSeDeduction = true)
    {
        profit = Math.Max(0m, profit);

        // 1) SE tax (compute precisely; round once at the end)
        var seEarnings = profit * SeAdjustment;

        var ssTaxable = Math.Min(seEarnings, SocialSecurityWageBase2026);
        var ssTax = ssTaxable * SocialSecurityRate;

        var medicareTax = seEarnings * MedicareRate;

        // ✅ Add'l Medicare (0.9%) above threshold (approx using SE earnings only)
        var addlThreshold = AdditionalMedicareThreshold(status);
        var addlMedicareBase = Math.Max(0m, seEarnings - addlThreshold);
        var addlMedicareTax = addlMedicareBase * AdditionalMedicareRate;

        var seTaxRaw = ssTax + medicareTax + addlMedicareTax;
        var seTax = Round2(seTaxRaw);

        // Half SE deduction (based on accurate SE tax)
        var halfSe = includeHalfSeDeduction ? Round2(seTaxRaw / 2m) : 0m;

        // 2) Taxable income estimate
        var stdDed = includeStandardDeduction ? StandardDeduction(status) : 0m;

        var taxableIncome = profit - halfSe - stdDed;
        if (taxableIncome < 0m) taxableIncome = 0m;

        // 3) Federal income tax (true marginal)
        var fedTax = Round2(CalcMarginalTax(taxableIncome, FederalBrackets2026(status)));

        // 4) AZ flat tax (reserve-style estimate)
        var azTax = includeArizona ? Round2(taxableIncome * ArizonaFlatRate) : 0m;

        var total = Round2(seTax + fedTax + azTax);
        var eff = profit > 0m ? Round4(total / profit) : 0m;

        return new TaxEstimate(
            Profit: profit,
            SeTax: seTax,
            HalfSeDeduction: halfSe,
            StandardDeduction: stdDed,
            TaxableIncome: taxableIncome,
            FederalIncomeTax: fedTax,
            ArizonaTax: azTax,
            TotalEstimatedTax: total,
            EffectiveRate: eff
        );
    }

    // ✅ Progressive “stairs not cliff”
    private static decimal CalcMarginalTax(decimal taxableIncome, (decimal cap, decimal rate)[] brackets)
    {
        decimal tax = 0m;
        decimal prevCap = 0m;

        foreach (var (cap, rate) in brackets)
        {
            if (taxableIncome <= prevCap) break;

            var layerIncome = Math.Min(taxableIncome, cap) - prevCap;
            if (layerIncome > 0m)
                tax += layerIncome * rate;

            prevCap = cap;
        }

        return tax;
    }

    private static decimal Round2(decimal x) => Math.Round(x, 2, MidpointRounding.AwayFromZero);
    private static decimal Round4(decimal x) => Math.Round(x, 4, MidpointRounding.AwayFromZero);
}
