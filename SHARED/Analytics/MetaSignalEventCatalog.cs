using System.Collections.ObjectModel;

namespace Shared.Analytics;

public sealed record MetaSignalEventDefinition(
    string Name,
    string Category,
    bool AllowBrowserPixel,
    bool AllowServerForward);

public static class MetaSignalEventCatalog
{
    private static readonly IReadOnlyList<MetaSignalEventDefinition> DefinitionsInternal =
    [
        new("ViewContent", "page", true, true),
        new("RapidBounce", "friction", false, false),
        new("SessionEngaged5s", "engagement", false, false),
        new("SessionEngaged15s", "engagement", false, false),
        new("MeaningfulScroll", "engagement", false, false),
        new("LeadFormStart", "funnel", true, true),
        new("DiscoveryComplete", "funnel", true, true),
        new("FunnelStepComplete", "funnel", false, false),
        new("RecommendationViewed", "funnel", true, true),
        new("ContactStepReached", "funnel", true, true),
        new("ContactInputStarted", "funnel", false, false),
        new("PhoneFieldCompleted", "funnel", false, false),
        new("RequiredContactFieldsCompleted", "funnel", false, false),
        new("FieldError", "friction", false, false),
        new("SubmitAttempt", "funnel", false, false),
        new("HighIntentLeadSignal", "threshold", true, true),
        new("LeadReadySignal", "threshold", true, true),
        new("Backtrack", "friction", false, false),
        new("DeadClick", "friction", false, false),
        new("RageClick", "friction", false, false),
        new("AbandonedHighIntentLead", "abandon", true, true),
        new("Lead", "conversion", false, true),
        new("QualifiedLead", "conversion", false, true),
        new("AppointmentBooked", "conversion", false, true)
    ];

    private static readonly ReadOnlyDictionary<string, MetaSignalEventDefinition> DefinitionsByNameInternal =
        new(
            DefinitionsInternal.ToDictionary(
                definition => definition.Name,
                definition => definition,
                StringComparer.OrdinalIgnoreCase));

    public static IReadOnlyList<MetaSignalEventDefinition> Definitions => DefinitionsInternal;
    public static IReadOnlyDictionary<string, MetaSignalEventDefinition> DefinitionsByName => DefinitionsByNameInternal;

    public static IReadOnlyCollection<string> BrowserPixelEventNames =>
        DefinitionsInternal
            .Where(definition => definition.AllowBrowserPixel)
            .Select(definition => definition.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IReadOnlyCollection<string> ServerForwardEventNames =>
        DefinitionsInternal
            .Where(definition => definition.AllowServerForward)
            .Select(definition => definition.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static bool TryGet(string? eventName, out MetaSignalEventDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(eventName) &&
            DefinitionsByNameInternal.TryGetValue(eventName.Trim(), out definition!))
        {
            return true;
        }

        definition = null!;
        return false;
    }
}
