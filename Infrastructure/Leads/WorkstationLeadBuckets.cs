using System;
using System.Collections.Generic;
using System.Linq;

namespace Infrastructure.Leads;

public static class WorkstationLeadBuckets
{
    public const string MortgageProtection = "MortgageProtection";
    public const string LifeInsurance = "LifeInsurance";
    public const string TermLife = "TermLife";
    public const string WholeLife = "WholeLife";
    public const string Iul = "IUL";
    public const string FinalExpense = "FinalExpense";
    public const string DisabilityInsurance = "DisabilityInsurance";

    public static readonly string[] ProductBuckets =
    {
        MortgageProtection,
        LifeInsurance,
        TermLife,
        WholeLife,
        Iul,
        FinalExpense,
        DisabilityInsurance
    };

    public static readonly string[] LifeWorkstationQueueBuckets =
    {
        LifeInsurance,
        TermLife,
        WholeLife,
        Iul
    };

    private static readonly HashSet<string> RequestedAmountBuckets = new(StringComparer.OrdinalIgnoreCase)
    {
        LifeInsurance,
        TermLife,
        WholeLife,
        Iul,
        FinalExpense
    };

    private static readonly IReadOnlyDictionary<string, string> BucketAliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["mortgageprotection"] = MortgageProtection,
        ["mortgageprotectionleads"] = MortgageProtection,
        ["mortgageprotectionrebuttals"] = MortgageProtection,
        ["medicare"] = MortgageProtection,
        ["medicareleads"] = MortgageProtection,
        ["lifeinsurance"] = LifeInsurance,
        ["lifeinsuranceleads"] = LifeInsurance,
        ["lifeinsurancerebuttals"] = LifeInsurance,
        ["termlife"] = TermLife,
        ["termlifeleads"] = TermLife,
        ["termliferebuttals"] = TermLife,
        ["wholelife"] = WholeLife,
        ["wholelifeleads"] = WholeLife,
        ["wholeliferebuttals"] = WholeLife,
        ["iul"] = Iul,
        ["iulleads"] = Iul,
        ["iulrebuttals"] = Iul,
        ["indexeduniversallife"] = Iul,
        ["indexeduniversallifeleads"] = Iul,
        ["indexeduniversalliferebuttals"] = Iul,
        ["finalexpense"] = FinalExpense,
        ["finalexpenseleads"] = FinalExpense,
        ["finalexpenserebuttals"] = FinalExpense,
        ["disabilityinsurance"] = DisabilityInsurance,
        ["disabilityinsuranceleads"] = DisabilityInsurance,
        ["disabilityinsurancerebuttals"] = DisabilityInsurance
    };

    public static string? NormalizeBucket(string? bucket)
    {
        if (string.IsNullOrWhiteSpace(bucket))
            return null;

        var key = bucket.Trim()
            .Replace(" ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("-", "", StringComparison.OrdinalIgnoreCase)
            .Replace("_", "", StringComparison.OrdinalIgnoreCase);

        if (BucketAliasMap.TryGetValue(key, out var normalized))
            return normalized;

        return ProductBuckets.FirstOrDefault(x => x.Equals(bucket.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static string[] ExpandProductBucketValues(string normalizedBucket)
    {
        if (string.Equals(normalizedBucket, MortgageProtection, StringComparison.OrdinalIgnoreCase))
            return new[] { MortgageProtection, "Medicare" };

        return new[] { normalizedBucket };
    }

    public static string[] ExpandLifeWorkstationQueueValues(string normalizedQueue)
    {
        if (string.Equals(normalizedQueue, LifeInsurance, StringComparison.OrdinalIgnoreCase))
            return LifeWorkstationQueueBuckets;

        return ExpandProductBucketValues(normalizedQueue);
    }

    public static bool UsesRequestedAmountField(string? bucket)
    {
        var normalized = NormalizeBucket(bucket);
        return normalized != null && RequestedAmountBuckets.Contains(normalized);
    }

    public static string ResolveWebsiteLifeBucket(string? productType, string? offerKey = null)
    {
        var normalizedProductType = NormalizeWebsiteKey(productType);
        if (normalizedProductType == "lifeterm")
            return TermLife;
        if (normalizedProductType == "lifewhole")
            return WholeLife;
        if (normalizedProductType == "lifeiul")
            return Iul;
        if (normalizedProductType == "lifefinalexpense")
            return FinalExpense;
        if (normalizedProductType == "lifemp")
            return MortgageProtection;
        if (normalizedProductType == "lifegeneral")
            return LifeInsurance;

        var normalizedOfferKey = NormalizeWebsiteKey(offerKey);
        return normalizedOfferKey switch
        {
            "term" => TermLife,
            "wholelife" => WholeLife,
            "iul" => Iul,
            "finalexpense" => FinalExpense,
            "mortgage" => MortgageProtection,
            _ => LifeInsurance
        };
    }

    private static string NormalizeWebsiteKey(string? raw)
    {
        return (raw ?? string.Empty)
            .Trim()
            .Replace("_", "", StringComparison.OrdinalIgnoreCase)
            .Replace("-", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" ", "", StringComparison.OrdinalIgnoreCase)
            .ToLowerInvariant();
    }
}
