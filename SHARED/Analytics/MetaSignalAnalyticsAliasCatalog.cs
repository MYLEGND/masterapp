using System.Collections.ObjectModel;

namespace Shared.Analytics;

public sealed record MetaSignalAnalyticsAliasDefinition(
    string AnalyticsEventName,
    string MetaSignalEventName);

public static class MetaSignalAnalyticsAliasCatalog
{
    private static readonly IReadOnlyList<MetaSignalAnalyticsAliasDefinition> DefinitionsInternal =
    [
        new("page_engaged_5s", "SessionEngaged5s"),
        new("page_engaged_10s", "SessionEngaged5s"),
        new("page_engaged_15s", "SessionEngaged15s"),
        new("page_engaged_30s", "SessionEngaged15s"),
        new("page_engaged_60s", "SessionEngaged15s"),
        new("scroll_depth_50", "MeaningfulScroll"),
        new("scroll_depth_75", "MeaningfulScroll"),
        new("scroll_depth_90", "MeaningfulScroll"),
        new("scroll_depth_100", "MeaningfulScroll"),
        new("page_exit", "RapidBounce"),
        new("dead_click", "DeadClick"),
        new("rage_click", "RageClick"),
        new("AddToCart", "AddToCart"),
        new("CheckoutStarted", "InitiateCheckout")
    ];

    private static readonly ReadOnlyDictionary<string, MetaSignalAnalyticsAliasDefinition> DefinitionsByNameInternal =
        new(
            DefinitionsInternal.ToDictionary(
                definition => definition.AnalyticsEventName,
                definition => definition,
                StringComparer.OrdinalIgnoreCase));

    public static IReadOnlyList<MetaSignalAnalyticsAliasDefinition> Definitions => DefinitionsInternal;

    public static IReadOnlyCollection<string> AnalyticsEventNames =>
        DefinitionsInternal
            .Select(x => x.AnalyticsEventName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static bool TryGet(string? analyticsEventName, out MetaSignalAnalyticsAliasDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(analyticsEventName) &&
            DefinitionsByNameInternal.TryGetValue(analyticsEventName.Trim(), out definition!))
        {
            return true;
        }

        definition = null!;
        return false;
    }

    public static bool IsBridgeEligibleAnalyticsSource(
        string? analyticsEventName,
        int? scrollPercent = null,
        long? dwellMilliseconds = null,
        long? engagedMilliseconds = null,
        bool? isBounceCandidate = null)
    {
        if (!TryGet(analyticsEventName, out var definition))
            return false;

        if (!string.Equals(definition.MetaSignalEventName, "RapidBounce", StringComparison.OrdinalIgnoreCase))
            return true;

        var dwellMs = dwellMilliseconds ?? long.MaxValue;
        var engagedMs = engagedMilliseconds ?? 0;
        var scrollPct = scrollPercent ?? 0;
        var bounceCandidate = isBounceCandidate == true || dwellMs < 10_000;

        return bounceCandidate &&
               dwellMs < 10_000 &&
               engagedMs < 5_000 &&
               scrollPct < 35;
    }
}
