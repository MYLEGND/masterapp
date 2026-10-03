namespace Shared.Analytics;

public enum MarketingConversionEventKind
{
    Standard,
    Custom
}

public sealed record MarketingProviderConversionDestination(
    string EventName,
    MarketingConversionEventKind Kind,
    bool OptimizationEligible,
    string? PayloadType = null);

public sealed record MarketingConversionDestinationDefinition(
    string CanonicalEventName,
    MarketingProviderConversionDestination Meta,
    MarketingProviderConversionDestination OpenAi);

public static class MarketingConversionDestinationCatalog
{
    private static readonly IReadOnlyList<MarketingConversionDestinationDefinition> DefinitionsInternal =
    [
        new(
            "Lead",
            new("Lead", MarketingConversionEventKind.Standard, true),
            new(OpenAiMeasurementEventNames.LeadCreated, MarketingConversionEventKind.Standard, true, "customer_action")),
        new(
            "QualifiedLead",
            new("QualifiedLead", MarketingConversionEventKind.Custom, false),
            new("qualifiedlead", MarketingConversionEventKind.Custom, false, "custom")),
        new(
            "AppointmentBooked",
            new("AppointmentBooked", MarketingConversionEventKind.Custom, false),
            new(OpenAiMeasurementEventNames.AppointmentScheduled, MarketingConversionEventKind.Standard, true, "customer_action")),
        new(
            "AppointmentCompleted",
            new("AppointmentCompleted", MarketingConversionEventKind.Custom, false),
            new("appointmentcompleted", MarketingConversionEventKind.Custom, false, "custom")),
        new(
            "ApplicationSubmitted",
            new("ApplicationSubmitted", MarketingConversionEventKind.Custom, false),
            new("applicationsubmitted", MarketingConversionEventKind.Custom, false, "custom")),
        new(
            "PolicyIssued",
            new("PolicyIssued", MarketingConversionEventKind.Custom, false),
            new("policyissued", MarketingConversionEventKind.Custom, false, "custom")),
        new(
            "PolicyPaid",
            new("Purchase", MarketingConversionEventKind.Standard, true),
            new(OpenAiMeasurementEventNames.OrderCreated, MarketingConversionEventKind.Standard, true, "contents")),
        new(
            "AddToCart",
            new("AddToCart", MarketingConversionEventKind.Standard, true),
            new(OpenAiMeasurementEventNames.ItemsAdded, MarketingConversionEventKind.Standard, true, "contents")),
        new(
            "InitiateCheckout",
            new("InitiateCheckout", MarketingConversionEventKind.Standard, true),
            new(OpenAiMeasurementEventNames.CheckoutStarted, MarketingConversionEventKind.Standard, true, "contents")),
        new(
            "Purchase",
            new("Purchase", MarketingConversionEventKind.Standard, true),
            new(OpenAiMeasurementEventNames.OrderCreated, MarketingConversionEventKind.Standard, true, "contents"))
    ];

    private static readonly IReadOnlyDictionary<string, MarketingConversionDestinationDefinition> DefinitionsByCanonicalNameInternal =
        DefinitionsInternal.ToDictionary(
            definition => definition.CanonicalEventName,
            definition => definition,
            StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<MarketingConversionDestinationDefinition> Definitions => DefinitionsInternal;
    public static IReadOnlyDictionary<string, MarketingConversionDestinationDefinition> DefinitionsByCanonicalName =>
        DefinitionsByCanonicalNameInternal;

    public static bool TryGet(string? canonicalEventName, out MarketingConversionDestinationDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(canonicalEventName) &&
            DefinitionsByCanonicalNameInternal.TryGetValue(canonicalEventName.Trim(), out definition!))
        {
            return true;
        }

        definition = null!;
        return false;
    }

    public static MarketingProviderConversionDestination? ResolveMeta(string? canonicalEventName) =>
        TryGet(canonicalEventName, out var definition) ? definition.Meta : null;

    public static MarketingProviderConversionDestination? ResolveOpenAi(string? canonicalEventName) =>
        TryGet(canonicalEventName, out var definition) ? definition.OpenAi : null;
}
