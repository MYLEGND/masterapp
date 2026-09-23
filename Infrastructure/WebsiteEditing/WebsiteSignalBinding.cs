using Shared.Analytics;

namespace Infrastructure.WebsiteEditing;

public sealed class WebsiteSignalBinding
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Trigger { get; set; } = "click";
    public string EventName { get; set; } = string.Empty;
    public string DeliveryMode { get; set; } = "off";
    public bool OncePerSession { get; set; } = true;
    public List<string> MatchingFields { get; set; } = new();
}

public sealed record WebsiteSignalOption(string Name, string Category, bool MetaEligible,
    bool RequiresServerOutcome, IReadOnlyList<string> Triggers);

/// <summary>Editor capabilities derived from the existing event authority. No second dispatch catalog.</summary>
public static class WebsiteSignalBindingPolicy
{
    public static readonly IReadOnlyList<string> ApprovedMatchingFields = Array.AsReadOnly(
        new[] { "email", "phone", "firstName", "lastName", "city", "state", "postalCode" });

    public static IReadOnlyList<WebsiteSignalOption> Options => MetaSignalEventCatalog.Definitions
        .Select(e => new WebsiteSignalOption(e.Name, e.Category, e.AllowBrowserPixel || e.AllowServerForward,
            MetaSignalEventCatalog.IsServerAuthorityEvent(e.Name), Triggers(e.Name)))
        .Where(e => e.Triggers.Count != 0).ToArray();

    private static IReadOnlyList<string> Triggers(string name) => name switch
    {
        "ViewContent" => ["viewed"],
        "LeadFormStart" => ["click", "form_started"],
        "ContactInputStarted" => ["field_started"],
        "PhoneFieldCompleted" => ["field_completed"],
        "FieldError" => ["validation_failed"],
        "SubmitAttempt" => ["submit_attempt"],
        "MeaningfulScroll" => ["scroll_threshold"],
        "Lead" => ["submission_saved"],
        "AppointmentBooked" => ["booking_confirmed"],
        "Purchase" => ["payment_confirmed"],
        _ => []
    };

    public static List<WebsiteSignalBinding> Validate(IEnumerable<WebsiteSignalBinding>? bindings)
    {
        var clean = new List<WebsiteSignalBinding>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var triggers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in bindings ?? [])
        {
            if (binding is null || clean.Count >= 8 || !Guid.TryParseExact(binding.Id, "N", out _) || !ids.Add(binding.Id))
                throw new ArgumentException("Signal mappings require unique IDs and at most eight mappings per element.");
            if (binding.DeliveryMode is not ("off" or "analytics" or "meta"))
                throw new ArgumentException("Choose Off, Analytics, or Meta and analytics.");
            var option = Options.SingleOrDefault(x => x.Name == binding.EventName);
            if (option is null || !option.Triggers.Contains(binding.Trigger) || !triggers.Add(binding.Trigger))
                throw new ArgumentException("Choose one supported event for each trigger.");
            if (binding.DeliveryMode == "meta" && !option.MetaEligible)
                throw new ArgumentException("This event is available for analytics only.");
            var fields = (binding.MatchingFields ?? []).Distinct(StringComparer.Ordinal).ToList();
            if (fields.Any(x => !ApprovedMatchingFields.Contains(x)) || (fields.Count > 0 && !option.RequiresServerOutcome))
                throw new ArgumentException("Customer matching is available only from approved fields on verified server outcomes.");
            clean.Add(new()
            {
                Id = binding.Id, Trigger = binding.Trigger, EventName = option.Name,
                DeliveryMode = binding.DeliveryMode, OncePerSession = binding.OncePerSession, MatchingFields = fields
            });
        }
        return clean;
    }
}
