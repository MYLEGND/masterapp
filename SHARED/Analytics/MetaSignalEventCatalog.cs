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
        new("ViewContent", "page", true, false),
        new("RapidBounce", "friction", false, false),
        new("SessionEngaged5s", "engagement", false, false),
        new("SessionEngaged15s", "engagement", false, false),
        new("MeaningfulScroll", "engagement", false, false),
        new("LeadFormStart", "funnel", true, false),
        new("DiscoveryComplete", "funnel", true, false),
        new("FunnelStepComplete", "funnel", false, false),
        new("RecommendationViewed", "funnel", true, false),
        new("ContactStepReached", "funnel", true, false),
        new("ContactInputStarted", "funnel", false, false),
        new("PhoneFieldCompleted", "funnel", false, false),
        new("RequiredContactFieldsCompleted", "funnel", false, false),
        new("FieldError", "friction", false, false),
        new("SubmitAttempt", "funnel", false, false),
        new("HighIntentLeadSignal", "threshold", true, false),
        new("LeadReadySignal", "threshold", true, false),
        new("Backtrack", "friction", false, false),
        new("DeadClick", "friction", false, false),
        new("RageClick", "friction", false, false),
        new("AbandonedHighIntentLead", "abandon", true, false),
        new("Lead", "conversion", false, true),
        new("QualifiedLead", "conversion", false, true),
        new("AppointmentBooked", "conversion", false, true),
        new("AppointmentCompleted", "conversion", false, true),
        new("ApplicationSubmitted", "conversion", false, true),
        new("PolicyIssued", "conversion", false, true),
        new("PolicyPaid", "conversion", false, true)
    ];

    private static readonly ReadOnlyDictionary<string, MetaSignalEventDefinition> DefinitionsByNameInternal =
        new(
            DefinitionsInternal.ToDictionary(
                definition => definition.Name,
                definition => definition,
                StringComparer.OrdinalIgnoreCase));

    private static readonly IReadOnlyCollection<string> ServerAuthorityEventNamesInternal =
        DefinitionsInternal
            .Where(definition => string.Equals(definition.Category, "conversion", StringComparison.OrdinalIgnoreCase))
            .Select(definition => definition.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IReadOnlyList<MetaSignalEventDefinition> Definitions => DefinitionsInternal;
    public static IReadOnlyDictionary<string, MetaSignalEventDefinition> DefinitionsByName => DefinitionsByNameInternal;
    public static IReadOnlyCollection<string> ServerAuthorityEventNames => ServerAuthorityEventNamesInternal;

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

    public static bool IsServerAuthorityEvent(string? eventName) =>
        !string.IsNullOrWhiteSpace(eventName) &&
        ServerAuthorityEventNamesInternal.Contains(eventName.Trim());

    public static bool IsBrowserSignalEvent(string? eventName) =>
        TryGet(eventName, out _) && !IsServerAuthorityEvent(eventName);

    public static string BuildEventKey(string? eventName, Guid? leadId, string? sessionId)
    {
        var normalizedEventName = Normalize(eventName) ?? "unknown";
        var normalizedLeadId = leadId.HasValue && leadId.Value != Guid.Empty
            ? leadId.Value.ToString("N")
            : "anonymous";
        var normalizedSessionId = Normalize(sessionId) ?? "no_session";

        return $"{normalizedEventName}:{normalizedLeadId}:{normalizedSessionId}";
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
