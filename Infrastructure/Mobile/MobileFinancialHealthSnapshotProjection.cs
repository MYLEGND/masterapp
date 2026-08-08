using Shared.Finance;

namespace Infrastructure.Mobile;

/// <summary>
/// Maps an already-calculated <see cref="LegendLivingBalanceSheetState"/> to
/// the native Financial Health Snapshot transport contract. This class does
/// not calculate, persist, or reinterpret any financial values.
/// </summary>
public static class MobileFinancialHealthSnapshotProjection
{
    public static MobileFinancialHealthSnapshot Create(
        LegendLivingBalanceSheetState state,
        DateTime updatedUtc)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new MobileFinancialHealthSnapshot(
            updatedUtc,
            [
                Assets(state),
                Liabilities(state),
                CashFlow(state),
                Protection(state),
                TaxProfile(state)
            ]);
    }

    private static MobileFinancialHealthSection Assets(
        LegendLivingBalanceSheetState state) => new(
        Key: "assets",
        Title: "Assets",
        Semantic: "assets",
        Period: null,
        Groups:
        [
            new MobileFinancialHealthGroup(
                "asset-components",
                null,
                [
                    Currency("personal-property", "Personal Property", state.Assets.PersonalProperty),
                    Currency("savings", "Savings", state.Assets.Savings),
                    Currency("investments", "Investments", state.Assets.Investments),
                    Currency("retirement", "Retirement", state.Assets.Retirement),
                    Currency("real-estate", "Real Estate", state.Assets.RealEstate),
                    Currency("business", "Business", state.Assets.Business)
                ])
        ],
        Total: Currency("total-assets", "Total Assets", state.Assets.Total));

    private static MobileFinancialHealthSection Liabilities(
        LegendLivingBalanceSheetState state) => new(
        Key: "liabilities",
        Title: "Liabilities",
        Semantic: "liabilities",
        Period: null,
        Groups:
        [
            new MobileFinancialHealthGroup(
                "liability-components",
                null,
                [
                    Currency("short-term", "Short Term", state.Liabilities.ShortTerm),
                    Currency("taxes", "Taxes", state.Liabilities.Taxes),
                    Currency("mortgages", "Mortgages", state.Liabilities.Mortgages),
                    Currency("business-debt", "Business Debt", state.Liabilities.BusinessDebt)
                ])
        ],
        Total: Currency("total-liabilities", "Total Liabilities", state.Liabilities.Total));

    private static MobileFinancialHealthSection CashFlow(
        LegendLivingBalanceSheetState state) => new(
        Key: "cash-flow",
        Title: "Cash Flow",
        Semantic: "cash-flow",
        Period: "Annual",
        Groups:
        [
            new MobileFinancialHealthGroup(
                "cash-flow-components",
                null,
                [
                    Currency("earnings", "Earnings", state.CashFlow.Earnings),
                    Currency("insurance-costs", "Insurance Costs", state.CashFlow.InsuranceCosts),
                    Currency("annual-savings", "Annual Savings", state.CashFlow.AnnualSavings),
                    Currency("debt-obligations", "Debt Obligations", state.CashFlow.DebtObligations),
                    Currency("debts-and-tax-costs", "Debts & Tax Costs", state.CashFlow.DebtsAndTaxCosts)
                ])
        ],
        Total: Currency(
            "lifestyle-remaining",
            "What's Left for Lifestyle",
            state.CashFlow.LifestyleRemaining));

    private static MobileFinancialHealthSection Protection(
        LegendLivingBalanceSheetState state) => new(
        Key: "protection",
        Title: "Protection",
        Semantic: "protection",
        Period: null,
        Groups:
        [
            DualProtection("if-sick", "If You Get Sick", state.Protection.IfSick),
            DualProtection("if-sued", "If You Are Sued", state.Protection.IfSued),
            DualProtection("if-die", "If You Die", state.Protection.IfDie),
            new MobileFinancialHealthGroup(
                "wills-trusts",
                "Wills & Trusts",
                [
                    Text("estate-plan-status", "Status", state.Protection.WillsTrusts.Status),
                    Text("estate-plan-risk", "Risk Level", state.Protection.WillsTrusts.RiskLevel)
                ]),
            new MobileFinancialHealthGroup(
                "protection-summary",
                "Protection Summary",
                [
                    Currency(
                        "total-protection-gap",
                        "Total Protection Gap",
                        state.Summary.ProtectionGapTotal)
                ])
        ],
        Total: Currency(
            "total-protection-coverage",
            "Total Protection Coverage",
            state.Summary.ProtectionCoverageTotal));

    private static MobileFinancialHealthSection TaxProfile(
        LegendLivingBalanceSheetState state) => new(
        Key: "tax-profile",
        Title: "Tax Profile",
        Semantic: "tax-profile",
        Period: "Annual",
        Groups:
        [
            new MobileFinancialHealthGroup(
                "tax-profile-details",
                null,
                [
                    Text("filing-status", "Filing Status", state.TaxProfile.FilingStatus),
                    Percentage("federal-tax-rate", "Federal Tax Rate", state.TaxProfile.FederalTaxRate),
                    Percentage("state-tax-rate", "State Tax Rate", state.TaxProfile.StateTaxRate),
                    Percentage("fica-rate", "FICA Rate", state.TaxProfile.FicaRate),
                    Percentage("effective-tax-rate", "Effective Tax Rate", state.TaxProfile.EffectiveTaxRate),
                    Text(
                        "tax-calculation",
                        "Tax Calculation",
                        state.TaxProfile.UseCustomTaxOverride ? "Custom" : "Calculated")
                ])
        ],
        Total: Currency("annual-tax-cost", "Annual Tax Cost", state.TaxProfile.CalculatedTaxAmount));

    private static MobileFinancialHealthGroup DualProtection(
        string key,
        string title,
        LegendDualProtectionItem item) => new(
        key,
        title,
        [
            Text("active-person", "Active Person", item.ActivePerson),
            Text("primary-status", "Primary Status", item.Primary.Status, item.Primary.Status),
            Currency("primary-coverage", "Primary Coverage", item.Primary.CoverageAmount),
            Currency("primary-gap", "Primary Gap", item.Primary.GapAmount),
            Text("spouse-status", "Spouse Status", item.Spouse.Status, item.Spouse.Status),
            Currency("spouse-coverage", "Spouse Coverage", item.Spouse.CoverageAmount),
            Currency("spouse-gap", "Spouse Gap", item.Spouse.GapAmount)
        ]);

    private static MobileFinancialHealthMetric Currency(
        string key,
        string label,
        decimal amount) => new(
        key,
        label,
        "Currency",
        ToCents(amount),
        null,
        null,
        null);

    private static MobileFinancialHealthMetric Percentage(
        string key,
        string label,
        decimal value) => new(
        key,
        label,
        "Percentage",
        null,
        value,
        null,
        null);

    private static MobileFinancialHealthMetric Text(
        string key,
        string label,
        string value,
        string? status = null) => new(
        key,
        label,
        "Text",
        null,
        null,
        value,
        status);

    private static long ToCents(decimal amount) => checked((long)decimal.Round(
        amount * 100m,
        0,
        MidpointRounding.AwayFromZero));
}
