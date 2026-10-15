using Shared.Analytics;

namespace Infrastructure.WebsiteEditing;

public sealed class WebsiteSignalBinding
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Trigger { get; set; } = "click";
    public string EventName { get; set; } = string.Empty;
    public string? ActionKey { get; set; }
    public string DeliveryMode { get; set; } = "off";
    public bool OncePerSession { get; set; } = true;
    public List<string> MatchingFields { get; set; } = new();
}

public sealed record WebsiteSignalOption(string Name, string Category, bool MetaEligible,
    bool RequiresServerOutcome, IReadOnlyList<string> Triggers, string ActionKey, string DisplayLabel,
    string? MetaEventName, string? OpenAiEventName, string AutomaticTrigger);

/// <summary>Editor capabilities derived from the existing event authority. No second dispatch catalog.</summary>
public static class WebsiteSignalBindingPolicy
{
    public static readonly IReadOnlyList<string> ApprovedMatchingFields = Array.AsReadOnly(
        new[] { "email", "phone", "firstName", "lastName", "city", "state", "postalCode" });

    public static IReadOnlyList<WebsiteSignalOption> Options => AnalyticsEventCatalog.Definitions
        .Select(e => AnalyticsEventCatalog.TryGetBehavior(e.Name, out var behavior) ? behavior : null)
        .OfType<AnalyticsBehaviorContract>().Concat(AnalyticsEventCatalog.Behaviors)
        .DistinctBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
        .Where(e => e.EditorTriggers.Count != 0 || e.ConversionEventName is not null)
        .Select(e =>
        {
            // Provider capabilities are downstream annotations, never the available behavior list.
            var meta = MetaSignalAnalyticsAliasCatalog.ResolveSignalName(e.EventName);
            var metaEligible = meta is not null && MetaSignalEventCatalog.TryGet(meta, out var destination) &&
                (destination.AllowBrowserPixel || destination.AllowServerForward);
            return new WebsiteSignalOption(e.EventName,
                AnalyticsEventCatalog.TryGet(e.EventName, out var definition) ? definition.Category : "behavior",
                metaEligible, e.RequiresServerAuthority, e.EditorTriggers, e.Key, e.DisplayLabel, meta,
                e.Key == "page_view" ? OpenAiMeasurementEventNames.PageViewed
                    : MarketingConversionDestinationCatalog.ResolveOpenAi(e.ConversionEventName)?.EventName,
                e.AutomaticTrigger);
        }).ToArray();

    public static List<WebsiteSignalBinding> Validate(IEnumerable<WebsiteSignalBinding>? bindings)
    {
        var clean = new List<WebsiteSignalBinding>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var triggers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in bindings ?? [])
        {
            if (binding is null || clean.Count >= 8 || !Guid.TryParseExact(binding.Id, "N", out _) || !ids.Add(binding.Id))
                throw new ArgumentException("Signal mappings require unique IDs and at most eight mappings per element.");
            if (binding.DeliveryMode is not ("off" or "analytics" or "destinations" or "meta"))
                throw new ArgumentException("Choose Off, Analytics only, or Analytics and configured destinations.");
            var behavior = AnalyticsEventCatalog.TryGetBehavior(binding.EventName, out var resolved) ? resolved : null;
            var option = behavior is null ? null : Options.SingleOrDefault(x => x.ActionKey == behavior.Key);
            if (option?.RequiresServerOutcome == true)
                throw new ArgumentException("Verified outcomes are automatic backend events and cannot be manually bound to browser actions.");
            if (!string.IsNullOrWhiteSpace(binding.ActionKey) && binding.ActionKey != option?.ActionKey)
                throw new ArgumentException("The action key must match its immutable canonical behavior.");
            if (option is null || !option.Triggers.Contains(binding.Trigger) || !triggers.Add(binding.Trigger))
                throw new ArgumentException("Choose one supported event for each trigger.");
            var fields = (binding.MatchingFields ?? []).Distinct(StringComparer.Ordinal).ToList();
            if (fields.Any(x => !ApprovedMatchingFields.Contains(x)) || (fields.Count > 0 && !option.RequiresServerOutcome))
                throw new ArgumentException("Customer matching is available only from approved fields on verified server outcomes.");
            clean.Add(new()
            {
                Id = binding.Id, Trigger = binding.Trigger, EventName = option.Name, ActionKey = option.ActionKey,
                DeliveryMode = binding.DeliveryMode == "meta" ? "destinations" : binding.DeliveryMode, OncePerSession = binding.OncePerSession, MatchingFields = fields
            });
        }
        return clean;
    }

    private static readonly IReadOnlySet<string> CanonicalInquiryFieldKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "firstname", "lastname", "phone", "email", "message", "consent", "submit"
        };

    private static readonly IReadOnlySet<string> CanonicalContactFieldKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "firstname", "lastname", "phone", "email", "message"
        };

    public static bool IsKnownProtectedFormField(
        WebsiteCompositionNode node,
        string fieldKey)
    {
        if (string.IsNullOrWhiteSpace(fieldKey)) return false;
        if (node.Type == "form" &&
            string.Equals(node.SystemKey, "canonical_inquiry", StringComparison.Ordinal))
            return CanonicalInquiryFieldKeys.Contains(fieldKey);

        if (!WebsiteSystemTemplateAuthority.IsRuntimeFormSystemKey(node.SystemKey))
            return false;

        return node.FieldPresentations.ContainsKey(fieldKey) ||
               node.FieldLabels.ContainsKey(fieldKey) ||
               node.FieldSignals.ContainsKey(fieldKey);
    }

    public static void ValidateProtectedFormField(
        WebsiteCompositionNode node,
        string fieldKey,
        IReadOnlyList<WebsiteSignalBinding> signals)
    {
        if (!IsKnownProtectedFormField(node, fieldKey))
            throw new ArgumentException(
                $"Protected form field '{fieldKey}' is not a canonical field on component '{node.Id}'.");

        foreach (var binding in signals ?? [])
        {
            if (binding.ActionKey == "phone_field_completed" &&
                !string.Equals(fieldKey, "phone", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "PhoneFieldCompleted may be connected only to the canonical phone field.");

            if (binding.ActionKey == "contact_input_started" &&
                !CanonicalContactFieldKeys.Contains(fieldKey))
                throw new ArgumentException(
                    "ContactInputStarted may be connected only to a canonical contact-input field.");
        }
    }

    public static void ValidateExperienceControl(
        WebsiteExperienceControl control,
        IReadOnlyList<WebsiteSignalBinding> signals)
    {
        ArgumentNullException.ThrowIfNull(control);
        var buttonLike = control.Type is "button" or "cta";

        foreach (var binding in signals ?? [])
        {
            if (buttonLike)
            {
                if (binding.Trigger != "click")
                    throw new ArgumentException(
                        $"Interactive button '{control.Key}' may map only a canonical click behavior.");
            }
            else if (binding.Trigger is not ("field_started" or "field_completed" or "validation_failed"))
            {
                throw new ArgumentException(
                    $"Interactive input '{control.Key}' may map only field start, field completion, or validation behaviors.");
            }

            if (binding.ActionKey == "phone_field_completed" &&
                !string.Equals(control.ContactRole, "phone", StringComparison.Ordinal))
                throw new ArgumentException(
                    "PhoneFieldCompleted may be connected only to the control carrying the canonical phone contact role.");

            if (binding.ActionKey == "contact_input_started" &&
                control.ContactRole is not ("first_name" or "last_name" or "phone" or "email" or "message"))
                throw new ArgumentException(
                    "ContactInputStarted may be connected only to a canonical contact-input control.");
        }
    }
}
