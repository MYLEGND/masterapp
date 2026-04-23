using System.Text.Json.Serialization;

namespace Shared.Finance;

public static class LegendLivingBalanceSheetConstants
{
    public const string ToolId = "LegendLivingBalanceSheet";
    public const int CurrentVersion = 1;
}

public static class LegendProtectionStatuses
{
    public const string Exposed = "Exposed";
    public const string Partial = "Partial";
    public const string Protected = "Protected";
}

public sealed class LegendLivingBalanceSheetState
{
    public Guid? ClientId { get; set; }
    public int Version { get; set; } = LegendLivingBalanceSheetConstants.CurrentVersion;
    public LegendBalanceSheetAssets Assets { get; set; } = new();
    public LegendBalanceSheetLiabilities Liabilities { get; set; } = new();
    public LegendBalanceSheetCashFlow CashFlow { get; set; } = new();
    public LegendBalanceSheetTaxProfile TaxProfile { get; set; } = new();
    public LegendBalanceSheetProtection Protection { get; set; } = new();
    public LegendBalanceSheetSummary Summary { get; set; } = new();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class LegendBalanceSheetAssets
{
    public decimal PersonalProperty { get; set; }
    public decimal Savings { get; set; }
    public decimal Investments { get; set; }
    public decimal Retirement { get; set; }
    public decimal RealEstate { get; set; }
    public decimal Business { get; set; }
    public decimal Total { get; set; }
}

public sealed class LegendBalanceSheetLiabilities
{
    public decimal ShortTerm { get; set; }
    public decimal Taxes { get; set; }
    public decimal Mortgages { get; set; }
    public decimal BusinessDebt { get; set; }
    public decimal Total { get; set; }
}

public sealed class LegendBalanceSheetCashFlow
{
    public decimal Earnings { get; set; }
    public decimal InsuranceCosts { get; set; }
    public decimal AnnualSavings { get; set; }
    public decimal DebtObligations { get; set; }
    public decimal DebtsAndTaxCosts { get; set; }
    public decimal LifestyleRemaining { get; set; }
}

public sealed class LegendBalanceSheetTaxProfile
{
    public string FilingStatus { get; set; } = "Single";
    public decimal FederalTaxRate { get; set; }
    public decimal StateTaxRate { get; set; }
    public decimal FicaRate { get; set; }
    public bool UseCustomTaxOverride { get; set; }
    public decimal ManualTaxAmount { get; set; }
    public decimal EffectiveTaxRate { get; set; }
    public decimal CalculatedTaxAmount { get; set; }
}

public sealed class LegendBalanceSheetProtection
{
    public LegendProtectionItem IfSued { get; set; } = LegendProtectionItem.Exposed();
    public LegendProtectionItem IfSick { get; set; } = LegendProtectionItem.Exposed();
    public LegendProtectionItem WillsTrusts { get; set; } = LegendProtectionItem.Exposed();
    public LegendProtectionItem IfDie { get; set; } = LegendProtectionItem.Exposed();
}

public sealed class LegendProtectionItem
{
    public string Status { get; set; } = LegendProtectionStatuses.Exposed;
    public decimal CoverageAmount { get; set; }
    public decimal GapAmount { get; set; }

    public static LegendProtectionItem Exposed() => new()
    {
        Status = LegendProtectionStatuses.Exposed,
        CoverageAmount = 0,
        GapAmount = 0
    };
}

public sealed class LegendBalanceSheetSummary
{
    public decimal AssetsTotal { get; set; }
    public decimal LiabilitiesTotal { get; set; }
    public decimal NetWorth { get; set; }
    public decimal Taxes { get; set; }
    public decimal DebtsAndTaxCosts { get; set; }
    public decimal LifestyleRemaining { get; set; }
    public decimal ProtectionCoverageTotal { get; set; }
    public decimal ProtectionGapTotal { get; set; }
    public int ProtectedCount { get; set; }
    public int PartialCount { get; set; }
    public int ExposedCount { get; set; }
}
