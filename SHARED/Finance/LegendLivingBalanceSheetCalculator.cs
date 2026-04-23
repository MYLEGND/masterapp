using System.Text.Json;

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
        LegendLivingBalanceSheetState state;
        try
        {
            state = JsonSerializer.Deserialize<LegendLivingBalanceSheetState>(json ?? "{}", JsonOptions)
                ?? CreateDefault(clientId);
        }
        catch
        {
            state = CreateDefault(clientId);
        }

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

        // Summary uses primary person's values for dual items; WillsTrusts is flat/shared
        var summaryItems = new[]
        {
            state.Protection.IfSued.Primary,
            state.Protection.IfSick.Primary,
            state.Protection.WillsTrusts,
            state.Protection.IfDie.Primary
        };

        state.Summary.AssetsTotal = assets.Total;
        state.Summary.LiabilitiesTotal = liabilities.Total;
        state.Summary.NetWorth = assets.Total - liabilities.Total;
        state.Summary.Taxes = liabilities.Taxes;
        state.Summary.DebtsAndTaxCosts = cashFlow.DebtsAndTaxCosts;
        state.Summary.LifestyleRemaining = cashFlow.LifestyleRemaining;
        state.Summary.ProtectionCoverageTotal = summaryItems.Sum(x => NonNegative(x.CoverageAmount));
        state.Summary.ProtectionGapTotal = summaryItems.Sum(x => NonNegative(x.GapAmount));
        state.Summary.ProtectedCount = summaryItems.Count(x => IsStatus(x.Status, LegendProtectionStatuses.Protected));
        state.Summary.PartialCount = summaryItems.Count(x => IsStatus(x.Status, LegendProtectionStatuses.Partial));
        state.Summary.ExposedCount = summaryItems.Count(x => IsStatus(x.Status, LegendProtectionStatuses.Exposed));
        state.UpdatedUtc = DateTime.UtcNow;

        return state;
    }

    private static void NormalizeProtection(LegendBalanceSheetProtection protection)
    {
        protection.IfSued ??= new LegendDualProtectionItem();
        protection.IfSick ??= new LegendDualProtectionItem();
        protection.WillsTrusts ??= LegendProtectionItem.Exposed();
        protection.IfDie ??= new LegendDualProtectionItem();

        foreach (var dual in new[] { protection.IfSued, protection.IfSick, protection.IfDie })
        {
            dual.Primary ??= LegendProtectionItem.Exposed();
            dual.Spouse ??= LegendProtectionItem.Exposed();
            dual.ActivePerson = string.Equals((dual.ActivePerson ?? "").Trim(), "spouse", StringComparison.OrdinalIgnoreCase)
                ? "spouse" : "primary";
            NormalizeItem(dual.Primary);
            NormalizeItem(dual.Spouse);
        }

        NormalizeItem(protection.WillsTrusts);
    }

    private static void NormalizeItem(LegendProtectionItem item)
    {
        item.Status = NormalizeStatus(item.Status);
        item.CoverageAmount = NonNegative(item.CoverageAmount);
        item.GapAmount = NonNegative(item.GapAmount);
    }

    private static string NormalizeStatus(string? status)
    {
        if (IsStatus(status, LegendProtectionStatuses.Protected)) return LegendProtectionStatuses.Protected;
        if (IsStatus(status, LegendProtectionStatuses.Partial)) return LegendProtectionStatuses.Partial;
        return LegendProtectionStatuses.Exposed;
    }

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
}
