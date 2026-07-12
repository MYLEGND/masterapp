using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shared.Finance;

public static class LegendLivingBalanceSheetCalculator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static LegendLivingBalanceSheetState CreateDefault(Guid? clientId = null)
    {
        var now = DateTime.UtcNow;
        return Calculate(new LegendLivingBalanceSheetState
        {
            ClientId = clientId == Guid.Empty ? null : clientId,
            Version = LegendLivingBalanceSheetConstants.CurrentVersion,
            CreatedUtc = now,
            UpdatedUtc = now
        });
    }

    public static string NormalizeJson(string? json, Guid? clientId = null)
    {
        var state = DeserializeCanonicalState(json, clientId);

        if (clientId.HasValue && clientId.Value != Guid.Empty)
            state.ClientId = clientId.Value;

        state = Calculate(state);
        return JsonSerializer.Serialize(state, JsonOptions);
    }

    public static LegendLivingBalanceSheetState Calculate(LegendLivingBalanceSheetState? state)
    {
        state ??= CreateDefault();

        state.Version = state.Version <= 0
            ? LegendLivingBalanceSheetConstants.CurrentVersion
            : state.Version;

        state.Assets ??= new LegendBalanceSheetAssets();
        state.Liabilities ??= new LegendBalanceSheetLiabilities();
        state.CashFlow ??= new LegendBalanceSheetCashFlow();
        state.TaxProfile ??= new LegendBalanceSheetTaxProfile();
        state.Protection ??= new LegendBalanceSheetProtection();
        state.Summary ??= new LegendBalanceSheetSummary();
        state.Summary.SectionCompletion ??= new LegendBalanceSheetSectionCompletion();
        state.CompoundLab ??= new JsonObject();

        NormalizeProtection(state.Protection);

        var assets = state.Assets;
        assets.PersonalProperty = NonNegative(assets.PersonalProperty);
        assets.Savings = NonNegative(assets.Savings);
        assets.Investments = NonNegative(assets.Investments);
        assets.Retirement = NonNegative(assets.Retirement);
        assets.RealEstate = NonNegative(assets.RealEstate);
        assets.Business = NonNegative(assets.Business);
        assets.Total =
            assets.PersonalProperty +
            assets.Savings +
            assets.Investments +
            assets.Retirement +
            assets.RealEstate +
            assets.Business;

        var tax = state.TaxProfile;
        tax.FilingStatus = NormalizeFilingStatus(tax.FilingStatus);
        tax.FederalTaxRate = NormalizeRate(tax.FederalTaxRate);
        tax.StateTaxRate = NormalizeRate(tax.StateTaxRate);
        tax.FicaRate = NormalizeRate(tax.FicaRate);
        tax.ManualTaxAmount = NonNegative(tax.ManualTaxAmount);
        tax.EffectiveTaxRate = tax.UseCustomTaxOverride
            ? 0
            : ClampRate(tax.FederalTaxRate + tax.StateTaxRate + tax.FicaRate);

        var cashFlow = state.CashFlow;
        cashFlow.Earnings = NonNegative(cashFlow.Earnings);
        cashFlow.InsuranceCosts = NonNegative(cashFlow.InsuranceCosts);
        cashFlow.AnnualSavings = NonNegative(cashFlow.AnnualSavings);
        cashFlow.DebtObligations = NonNegative(cashFlow.DebtObligations);

        tax.CalculatedTaxAmount = tax.UseCustomTaxOverride
            ? tax.ManualTaxAmount
            : Math.Round(cashFlow.Earnings * tax.EffectiveTaxRate, 2, MidpointRounding.AwayFromZero);

        var liabilities = state.Liabilities;
        liabilities.ShortTerm = NonNegative(liabilities.ShortTerm);
        liabilities.Taxes = NonNegative(tax.CalculatedTaxAmount);
        liabilities.Mortgages = NonNegative(liabilities.Mortgages);
        liabilities.BusinessDebt = NonNegative(liabilities.BusinessDebt);
        liabilities.Total =
            liabilities.ShortTerm +
            liabilities.Taxes +
            liabilities.Mortgages +
            liabilities.BusinessDebt;

        cashFlow.DebtsAndTaxCosts = cashFlow.DebtObligations + liabilities.Taxes;
        cashFlow.LifestyleRemaining =
            cashFlow.Earnings -
            cashFlow.InsuranceCosts -
            cashFlow.AnnualSavings -
            cashFlow.DebtsAndTaxCosts;

        var protectionPairs = new[]
        {
            state.Protection.IfSued,
            state.Protection.IfSick,
            state.Protection.IfDie
        };

        var protectionCoverageTotal = protectionPairs.Sum(item =>
            NonNegative(item.Primary.CoverageAmount) + NonNegative(item.Spouse.CoverageAmount));
        var protectionGapTotal = protectionPairs.Sum(item =>
            NonNegative(item.Primary.GapAmount) + NonNegative(item.Spouse.GapAmount));
        var protectedCount = protectionPairs.Count(item => IsStatus(item.Primary.Status, LegendProtectionStatuses.Protected));
        var partialCount = protectionPairs.Count(item => IsStatus(item.Primary.Status, LegendProtectionStatuses.Partial));
        var exposedCount = protectionPairs.Count(item => IsStatus(item.Primary.Status, LegendProtectionStatuses.Exposed));
        var debtPressureRatio = cashFlow.Earnings > 0
            ? ClampRate(cashFlow.DebtObligations / cashFlow.Earnings)
            : (cashFlow.DebtObligations > 0 ? 1 : 0);
        var cashFlowLeakage = cashFlow.InsuranceCosts + cashFlow.DebtsAndTaxCosts;
        var netWorth = assets.Total - liabilities.Total;
        var position = ResolvePositionStatus(netWorth, protectionGapTotal, cashFlow.LifestyleRemaining, debtPressureRatio, exposedCount);
        var estatePlanning = state.Protection.WillsTrusts;
        var taxBurdenStatement = tax.UseCustomTaxOverride
            ? $"Your tax burden is set by custom override at {FormatCurrency(liabilities.Taxes)} annually."
            : $"Your estimated tax burden is {FormatPercent(tax.EffectiveTaxRate)} ({FormatCurrency(liabilities.Taxes)} annually).";
        const int totalProtectionFields = 3;
        var netWorthScore = netWorth <= 0 ? 0 : Math.Min(20, (int)Math.Floor(netWorth / 25000m) * 2);
        var protectionScore = exposedCount == 0 && partialCount == 0 ? 25
            : exposedCount == 0 ? 15
            : (int)Math.Round((decimal)protectedCount / totalProtectionFields * 10m, MidpointRounding.AwayFromZero);
        var debtScore = (int)Math.Round(Math.Max(0m, 1m - (debtPressureRatio / 0.35m)) * 20m, MidpointRounding.AwayFromZero);
        var lifestyleScore = cashFlow.LifestyleRemaining > 0 ? 15 : 0;
        var savingsScore = cashFlow.AnnualSavings > 0 ? 10 : 0;
        var estateScore = estatePlanning.Status switch
        {
            LegendEstatePlanStatuses.FullEstatePlan => 10,
            LegendEstatePlanStatuses.BasicWill => 5,
            _ => 0
        };
        var healthScore = Math.Min(100, netWorthScore + protectionScore + debtScore + lifestyleScore + savingsScore + estateScore);

        state.Summary.AssetsTotal = assets.Total;
        state.Summary.LiabilitiesTotal = liabilities.Total;
        state.Summary.NetWorth = netWorth;
        state.Summary.Taxes = liabilities.Taxes;
        state.Summary.TaxDrag = liabilities.Taxes;
        state.Summary.DebtsAndTaxCosts = cashFlow.DebtsAndTaxCosts;
        state.Summary.LifestyleRemaining = cashFlow.LifestyleRemaining;
        state.Summary.ProtectionCoverageTotal = protectionCoverageTotal;
        state.Summary.ProtectionGapTotal = protectionGapTotal;
        state.Summary.ProtectedCount = protectedCount;
        state.Summary.PartialCount = partialCount;
        state.Summary.ExposedCount = exposedCount;
        state.Summary.CashFlowLeakage = cashFlowLeakage;
        state.Summary.DebtPressureRatio = debtPressureRatio;
        state.Summary.EstatePlanningStatus = estatePlanning.Status;
        state.Summary.EstatePlanningRiskLevel = estatePlanning.RiskLevel;
        state.Summary.PositionStatus = position.Status;
        state.Summary.PositionSummary = position.Summary;
        state.Summary.PositionStatement = $"You are currently operating at a Net Worth of {FormatCurrency(netWorth)}. Based on your current structure, you are {position.Status}.";
        state.Summary.TaxBurdenStatement = taxBurdenStatement;
        state.Summary.HealthScore = healthScore;
        state.Summary.SectionCompletion = new LegendBalanceSheetSectionCompletion
        {
            Protection = protectedCount + partialCount > 0 || protectionCoverageTotal > 0,
            Assets = assets.Total > 0,
            Liabilities = liabilities.ShortTerm > 0 || liabilities.Mortgages > 0 || liabilities.BusinessDebt > 0,
            Cash = cashFlow.Earnings > 0,
            Tax = tax.FederalTaxRate > 0 || tax.UseCustomTaxOverride
        };
        state.UpdatedUtc = DateTime.UtcNow;

        return state;
    }

    private static LegendLivingBalanceSheetState DeserializeCanonicalState(string? json, Guid? clientId)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject ?? new JsonObject();
        }
        catch
        {
            return CreateDefault(clientId);
        }

        NormalizeProtectionJson(root);

        try
        {
            return JsonSerializer.Deserialize<LegendLivingBalanceSheetState>(root.ToJsonString(), JsonOptions)
                ?? CreateDefault(clientId);
        }
        catch
        {
            return CreateDefault(clientId);
        }
    }

    private static void NormalizeProtection(LegendBalanceSheetProtection protection)
    {
        protection.IfSued ??= new LegendDualProtectionItem();
        protection.IfSick ??= new LegendDualProtectionItem();
        protection.WillsTrusts ??= LegendEstatePlanningItem.NotSetUp();
        protection.IfDie ??= new LegendDualProtectionItem();

        foreach (var dual in new[] { protection.IfSued, protection.IfSick, protection.IfDie })
        {
            dual.Primary ??= LegendProtectionItem.Exposed();
            dual.Spouse ??= LegendProtectionItem.Exposed();
            dual.ActivePerson = string.Equals((dual.ActivePerson ?? "").Trim(), "spouse", StringComparison.OrdinalIgnoreCase)
                ? "spouse"
                : "primary";
            NormalizeItem(dual.Primary);
            NormalizeItem(dual.Spouse);
        }

        NormalizeEstatePlanning(protection.WillsTrusts);
    }

    private static void NormalizeProtectionJson(JsonObject root)
    {
        var protection = EnsureObject(root, "protection");
        protection["ifSued"] = NormalizeDualProtectionJson(protection["ifSued"]);
        protection["ifSick"] = NormalizeDualProtectionJson(protection["ifSick"]);
        protection["ifDie"] = NormalizeDualProtectionJson(protection["ifDie"]);
        protection["willsTrusts"] = NormalizeEstatePlanningJson(protection["willsTrusts"]);
    }

    private static JsonObject NormalizeDualProtectionJson(JsonNode? node)
    {
        var source = node as JsonObject;
        var hasNestedShape =
            source?["primary"] is JsonObject ||
            source?["spouse"] is JsonObject ||
            source?.ContainsKey("activePerson") == true;

        var primaryNode = hasNestedShape ? source?["primary"] : source;
        var spouseNode = hasNestedShape ? source?["spouse"] : null;
        var activePerson = string.Equals(ReadString(source, "activePerson"), "spouse", StringComparison.OrdinalIgnoreCase)
            ? "spouse"
            : "primary";

        return new JsonObject
        {
            ["primary"] = NormalizeProtectionItemJson(primaryNode),
            ["spouse"] = NormalizeProtectionItemJson(spouseNode),
            ["activePerson"] = activePerson
        };
    }

    private static JsonObject NormalizeProtectionItemJson(JsonNode? node)
    {
        var source = node as JsonObject;
        var status = NormalizeStatus(ReadString(source, "status"));
        var coverageAmount = NonNegative(ReadDecimal(source, "coverageAmount"));
        var gapAmount = NonNegative(ReadDecimal(source, "gapAmount"));

        return new JsonObject
        {
            ["status"] = status,
            ["coverageAmount"] = coverageAmount,
            ["gapAmount"] = gapAmount
        };
    }

    private static JsonObject NormalizeEstatePlanningJson(JsonNode? node)
    {
        var source = node as JsonObject;
        var status = NormalizeEstateStatus(ReadString(source, "status"));
        return new JsonObject
        {
            ["status"] = status,
            ["riskLevel"] = RiskLevelForEstateStatus(status)
        };
    }

    private static JsonObject EnsureObject(JsonObject root, string propertyName)
    {
        if (root[propertyName] is JsonObject existing)
            return existing;

        var created = new JsonObject();
        root[propertyName] = created;
        return created;
    }

    private static void NormalizeItem(LegendProtectionItem item)
    {
        item.Status = NormalizeStatus(item.Status);
        item.CoverageAmount = NonNegative(item.CoverageAmount);
        item.GapAmount = NonNegative(item.GapAmount);
    }

    private static void NormalizeEstatePlanning(LegendEstatePlanningItem item)
    {
        item.Status = NormalizeEstateStatus(item.Status);
        item.RiskLevel = RiskLevelForEstateStatus(item.Status);
    }

    private static string NormalizeStatus(string? status)
    {
        if (IsStatus(status, LegendProtectionStatuses.Protected)) return LegendProtectionStatuses.Protected;
        if (IsStatus(status, LegendProtectionStatuses.Partial)) return LegendProtectionStatuses.Partial;
        return LegendProtectionStatuses.Exposed;
    }

    private static string NormalizeEstateStatus(string? status)
    {
        var value = (status ?? string.Empty).Trim();
        if (string.Equals(value, LegendEstatePlanStatuses.FullEstatePlan, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Full Estate Plan", StringComparison.OrdinalIgnoreCase)
            || IsStatus(value, LegendProtectionStatuses.Protected))
        {
            return LegendEstatePlanStatuses.FullEstatePlan;
        }

        if (string.Equals(value, LegendEstatePlanStatuses.BasicWill, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Basic Will", StringComparison.OrdinalIgnoreCase)
            || IsStatus(value, LegendProtectionStatuses.Partial))
        {
            return LegendEstatePlanStatuses.BasicWill;
        }

        return LegendEstatePlanStatuses.NotSetUp;
    }

    private static string RiskLevelForEstateStatus(string? status)
        => NormalizeEstateStatus(status) switch
        {
            LegendEstatePlanStatuses.FullEstatePlan => LegendEstateRiskLevels.Low,
            LegendEstatePlanStatuses.BasicWill => LegendEstateRiskLevels.Moderate,
            _ => LegendEstateRiskLevels.High
        };

    private static bool IsStatus(string? actual, string expected)
        => string.Equals((actual ?? "").Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeFilingStatus(string? status)
    {
        var value = (status ?? "").Trim();
        return string.IsNullOrWhiteSpace(value) ? "Single" : value;
    }

    private static decimal NonNegative(decimal value) => value < 0 ? 0 : value;

    private static decimal NormalizeRate(decimal value)
    {
        if (value < 0) return 0;
        if (value > 1) value /= 100;
        return ClampRate(value);
    }

    private static decimal ClampRate(decimal value)
    {
        if (value < 0) return 0;
        return value > 1 ? 1 : value;
    }

    private static decimal ReadDecimal(JsonObject? source, string propertyName)
    {
        if (source?.TryGetPropertyValue(propertyName, out var node) != true || node == null)
            return 0;

        if (node is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<decimal>(out var decimalValue))
                return decimalValue;
            if (jsonValue.TryGetValue<double>(out var doubleValue))
                return (decimal)doubleValue;

            var text = jsonValue.ToString();
            if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out parsed))
                return parsed;
        }

        return 0;
    }

    private static string ReadString(JsonObject? source, string propertyName)
    {
        if (source?.TryGetPropertyValue(propertyName, out var node) != true || node == null)
            return string.Empty;

        if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var value))
            return value ?? string.Empty;

        return node.ToString();
    }

    private static (string Status, string Summary) ResolvePositionStatus(
        decimal netWorth,
        decimal protectionGap,
        decimal lifestyleRemaining,
        decimal debtPressureRatio,
        int exposedCount)
    {
        if (netWorth <= 0 || lifestyleRemaining < 0 || protectionGap > 0 || debtPressureRatio >= 0.35m || exposedCount > 0)
        {
            return (
                LegendProtectionStatuses.Exposed,
                "Gaps are open. Close protection and debt pressure before chasing growth."
            );
        }

        if (netWorth >= 250000m && lifestyleRemaining > 0 && debtPressureRatio <= 0.2m && protectionGap == 0)
        {
            return (
                "Strong",
                "Strong foundation. Focus on tax efficiency and compounding growth."
            );
        }

        return (
            "Stable",
            "Workable structure — tighten remaining gaps for full control."
        );
    }

    private static string FormatCurrency(decimal value)
    {
        var sign = value < 0 ? "-" : string.Empty;
        return $"{sign}${Math.Abs(value):N0}";
    }

    private static string FormatPercent(decimal value)
    {
        return $"{NormalizeRate(value) * 100m:0.##}%";
    }
}
