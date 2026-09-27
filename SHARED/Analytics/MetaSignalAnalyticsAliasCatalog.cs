using System.Collections.ObjectModel;

namespace Shared.Analytics;

public sealed record MetaSignalAnalyticsAliasDefinition(
    string AnalyticsEventName,
    string MetaSignalEventName);

public static class MetaSignalAnalyticsAliasCatalog
{
    private static readonly IReadOnlyList<MetaSignalAnalyticsAliasDefinition> DefinitionsInternal =
    [
        new("page_view", "ViewContent"),
        new("ProductViewed", "ViewContent"),
        new("qualified_lead", "QualifiedLead"),
        new("appointment_booked", "AppointmentBooked"),
        new("appointment_completed", "AppointmentCompleted"),
        new("application_submitted", "ApplicationSubmitted"),
        new("policy_issued", "PolicyIssued"),
        new("policy_paid", "PolicyPaid"),
        new("form_field_focus", "ContactInputStarted"),
        new("form_field_error", "FieldError"),
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
        AnalyticsEventCatalog.Definitions.Select(x => x.Name)
            .Concat(DefinitionsInternal.Select(x => x.AnalyticsEventName))
            .Where(name => ResolveSignalName(name) is not null)
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

        if (AnalyticsEventCatalog.TryGet(analyticsEventName, out var canonical))
        {
            var target = canonical.CountsAsConfirmedLead && canonical.AllowServer ? "Lead"
                : canonical.CountsAsLandingView ? "ViewContent"
                : canonical.CountsAsFormStart ? "LeadFormStart"
                : canonical.CountsAsContactStep ? "ContactStepReached"
                : canonical.CountsAsSubmitAttempt ? "SubmitAttempt" : null;
            if (target is not null)
            {
                definition = new(canonical.Name, target);
                return true;
            }
        }
        definition = null!;
        return false;
    }

    public static string? ResolveSignalName(string? eventName) =>
        TryGet(eventName, out var alias) ? alias.MetaSignalEventName
            : MetaSignalEventCatalog.TryGet(eventName, out var signal) ? signal.Name : null;

    public static IReadOnlyDictionary<string, string> BrowserProjectionMap =>
        AnalyticsEventCatalog.Definitions.Where(x => x.AllowBrowser)
            .Select(x => (x.Name, Signal: ResolveSignalName(x.Name)))
            .Where(x => x.Signal is not null)
            .ToDictionary(x => x.Name, x => x.Signal!, StringComparer.OrdinalIgnoreCase);

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
