using System.Text.Json.Nodes;

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

public static class LegendEstatePlanStatuses
{
    public const string NotSetUp = "NotSetUp";
    public const string BasicWill = "BasicWill";
    public const string FullEstatePlan = "FullEstatePlan";
}

public static class LegendEstateRiskLevels
{
    public const string High = "High";
    public const string Moderate = "Moderate";
    public const string Low = "Low";
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
    public JsonObject CompoundLab { get; set; } = new();
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
    public LegendDualProtectionItem IfSued { get; set; } = new();
    public LegendDualProtectionItem IfSick { get; set; } = new();
    public LegendEstatePlanningItem WillsTrusts { get; set; } = LegendEstatePlanningItem.NotSetUp();
    public LegendDualProtectionItem IfDie { get; set; } = new();
}

// Dual-person protection item — used for IfSued, IfSick, IfDie
public sealed class LegendDualProtectionItem
{
    public LegendProtectionItem Primary { get; set; } = LegendProtectionItem.Exposed();
    public LegendProtectionItem Spouse { get; set; } = LegendProtectionItem.Exposed();
    public string ActivePerson { get; set; } = "primary";
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

public sealed class LegendEstatePlanningItem
{
    public string Status { get; set; } = LegendEstatePlanStatuses.NotSetUp;
    public string RiskLevel { get; set; } = LegendEstateRiskLevels.High;

    public static LegendEstatePlanningItem NotSetUp() => new()
    {
        Status = LegendEstatePlanStatuses.NotSetUp,
        RiskLevel = LegendEstateRiskLevels.High
    };
}

public sealed class LegendBalanceSheetSummary
{
    public decimal AssetsTotal { get; set; }
    public decimal LiabilitiesTotal { get; set; }
    public decimal NetWorth { get; set; }
    public decimal Taxes { get; set; }
    public decimal TaxDrag { get; set; }
    public decimal DebtsAndTaxCosts { get; set; }
    public decimal LifestyleRemaining { get; set; }
    public decimal ProtectionCoverageTotal { get; set; }
    public decimal ProtectionGapTotal { get; set; }
    public int ProtectedCount { get; set; }
    public int PartialCount { get; set; }
    public int ExposedCount { get; set; }
    public decimal CashFlowLeakage { get; set; }
    public decimal DebtPressureRatio { get; set; }
    public string EstatePlanningStatus { get; set; } = LegendEstatePlanStatuses.NotSetUp;
    public string EstatePlanningRiskLevel { get; set; } = LegendEstateRiskLevels.High;
    public string PositionStatus { get; set; } = LegendProtectionStatuses.Exposed;
    public string PositionSummary { get; set; } = string.Empty;
    public string PositionStatement { get; set; } = string.Empty;
    public string TaxBurdenStatement { get; set; } = string.Empty;
    public int HealthScore { get; set; }
    public LegendBalanceSheetSectionCompletion SectionCompletion { get; set; } = new();
}

public sealed class LegendBalanceSheetSectionCompletion
{
    public bool Protection { get; set; }
    public bool Assets { get; set; }
    public bool Liabilities { get; set; }
    public bool Cash { get; set; }
    public bool Tax { get; set; }
}
