using System.Text.Json.Nodes;
using Shared.Analytics;

namespace Infrastructure.Reception;

/// <summary>Owner setup edits known configuration fields while retaining other saved configuration members.</summary>
public static class ReceptionistSetupForms
{
    // Presentation eligibility only: retain the existing button behavior. The owner setter and
    // every actual incoming call still enforce their own fresh canonical server checks.
    public static bool CanAllowOwnerPermission(ReceptionistConfigurationSnapshot snapshot) =>
        snapshot.BindingAccepted && snapshot.Accounting is
            { CurrentOpeningReconciled: true, AdmissionEnabled: true, PolicyLimitsAccepted: true };

    public static ReceptionistSetupInput Read(ReceptionistConfigurationSnapshot snapshot)
    {
        var config = snapshot.Configuration ?? new ReceptionistBusinessConfiguration();
        var services = Array(config.ServiceDefinitionsJson);
        var answers = Object(config.ApprovedKnowledgeJson)["answers"] as JsonArray ?? [];
        var intake = Object(config.IntakeRulesJson)["canonicalInquiry"] as JsonObject ?? [];
        var routes = intake["services"] as JsonArray ?? [];
        var hours = config.Hours.Select(x => new ReceptionistHoursInput { Enabled = true, Day = x.Day, Start = x.Start, End = x.End }).ToList();
        foreach (var day in Enum.GetValues<DayOfWeek>())
            if (!hours.Any(x => x.Day == day)) hours.Add(new ReceptionistHoursInput { Day = day });
        return new ReceptionistSetupInput
        {
            ExpectedRevision = snapshot.ConnectionRevision, BusinessName = config.BusinessName,
            ConversationInstructions = config.ConversationInstructions, BusinessPolicy = config.BusinessPolicy, CompactVoicePolicy = config.CompactVoicePolicy,
            ContactAndRetentionNotes = Text(Object(config.ContactAndRetentionPolicyJson), "policyText"),
            Currency = config.Currency, TimeZoneId = config.TimeZoneId, MonthlyCashLimit = config.MonthlyCashLimit,
            MonthlyConsumptionLimit = config.MonthlyConsumptionLimit, MonthlyVoiceMinutesLimit = config.MonthlyVoiceSecondsLimit / 60m,
            MaximumConcurrentCalls = config.MaximumConcurrentCalls, MaximumCallSeconds = config.MaximumCallSeconds,
            AdmissionWindowSeconds = config.AdmissionWindowSeconds, MaximumCallsPerWindow = config.MaximumCallsPerWindow,
            RequestIntake = config.RequestedCapabilities.Contains(ReceptionistCapabilities.CanonicalIntake),
            NotifyOwner = intake["notifyOwner"]?.GetValue<bool>() ?? false, Hours = hours,
            Services = services.Select(node => node as JsonObject ?? throw new ArgumentException("A saved service cannot be represented by this setup form."))
                .Select(node => new ReceptionistServiceInput
                {
                    ServiceKey = Text(node, "serviceKey"), DisplayName = Text(node, "displayName"),
                    Aliases = Lines(node["aliases"]), DiscoveryQuestion = Text(node, "discoveryQuestion"), PublicSummary = Text(node, "publicSummary"),
                    CrmBucket = Text(routes.OfType<JsonObject>().SingleOrDefault(x => Text(x, "serviceKey") == Text(node, "serviceKey")), "crmBucket")
                }).Append(new ReceptionistServiceInput()).ToList(),
            Answers = answers.Select(node => node as JsonObject ?? throw new ArgumentException("A saved answer cannot be represented by this setup form."))
                .Select(node => new ReceptionistAnswerInput
                {
                    Key = Text(node, "key"), ServiceKey = Text(node, "serviceKey"), Questions = Lines(node["questions"]),
                    Answer = Text(node, "answer"), Status = Text(node, "status"), AsOfDate = Text(node, "asOfDate")
                }).Append(new ReceptionistAnswerInput()).ToList()
        };
    }

    public static ReceptionistBusinessConfiguration Apply(ReceptionistConfigurationSnapshot snapshot, ReceptionistSetupInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.ExpectedRevision != snapshot.ConnectionRevision) throw new Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException();
        var previous = snapshot.Configuration ?? new ReceptionistBusinessConfiguration();
        if (input.Hours is null || input.Services is null || input.Answers is null ||
            input.Hours.Any(x => x is null) || input.Services.Any(x => x is null) || input.Answers.Any(x => x is null) ||
            input.Hours.Count > 168 || input.Services.Count > 100 || input.Answers.Count > 100 ||
            input.MonthlyVoiceMinutesLimit < 0 || input.MonthlyVoiceMinutesLimit * 60m != decimal.Truncate(input.MonthlyVoiceMinutesLimit * 60m))
            throw new ArgumentException("Choose bounded hours, services, answers and a whole number of voice seconds.");
        var hours = input.Hours.Where(x => x.Enabled).Select(x =>
            new ReceptionistHoursWindow(x.Day, x.Start ?? throw new ArgumentException("An enabled day needs a start time."),
                x.End ?? throw new ArgumentException("An enabled day needs an end time."))).ToArray();
        var services = input.Services.Where(x => !x.Remove && !Blank(x))
            .Select(x => string.IsNullOrWhiteSpace(x.ServiceKey) ? x with { ServiceKey = "service-" + Guid.NewGuid().ToString("N") } : x).ToArray();
        var answers = input.Answers.Where(x => !x.Remove && !Blank(x))
            .Select(x => string.IsNullOrWhiteSpace(x.Key) ? x with { Key = "answer-" + Guid.NewGuid().ToString("N") } : x).ToArray();
        if (services.Select(x => x.ServiceKey).Distinct(StringComparer.Ordinal).Count() != services.Length ||
            answers.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != answers.Length)
            throw new ArgumentException("Service and answer references must be unique.");
        var oldServices = Array(previous.ServiceDefinitionsJson);
        var oldKnowledge = Object(previous.ApprovedKnowledgeJson);
        var oldAnswers = oldKnowledge["answers"] as JsonArray ?? [];
        if (oldServices.OfType<JsonObject>().Any(x => !input.Services.Any(y => y.ServiceKey == Text(x, "serviceKey"))) ||
            oldAnswers.OfType<JsonObject>().Any(x => !input.Answers.Any(y => y.Key == Text(x, "key"))))
            throw new ArgumentException("Every saved service and answer must remain represented or be explicitly marked for removal.");
        var oldIntake = Object(previous.IntakeRulesJson);
        var canonical = oldIntake["canonicalInquiry"] as JsonObject ?? new JsonObject();
        var oldRoutes = canonical["services"] as JsonArray ?? [];
        var newServices = new JsonArray(); var newRoutes = new JsonArray(); var newAnswers = new JsonArray();
        var allowedBuckets = CrmBuckets(snapshot.Owner);
        foreach (var service in services)
        {
            if (!allowedBuckets.Contains(service.CrmBucket ?? string.Empty, StringComparer.Ordinal)) throw new ArgumentException("Choose a supported inquiry destination.");
            var node = Existing(oldServices, "serviceKey", service.ServiceKey);
            node["serviceKey"] = service.ServiceKey; node["displayName"] = service.DisplayName;
            node["aliases"] = Phrases(service.Aliases); node["discoveryQuestion"] = service.DiscoveryQuestion; node["publicSummary"] = service.PublicSummary;
            newServices.Add(node);
            var route = Existing(oldRoutes, "serviceKey", service.ServiceKey);
            route["serviceKey"] = service.ServiceKey; route["crmBucket"] = service.CrmBucket; newRoutes.Add(route);
        }
        foreach (var answer in answers)
        {
            var node = Existing(oldAnswers, "key", answer.Key);
            node["key"] = answer.Key; node["serviceKey"] = string.IsNullOrWhiteSpace(answer.ServiceKey) ? null : answer.ServiceKey;
            node["questions"] = Phrases(answer.Questions); node["answer"] = answer.Answer;
            node["status"] = answer.Status; node["asOfDate"] = string.IsNullOrWhiteSpace(answer.AsOfDate) ? null : answer.AsOfDate;
            newAnswers.Add(node);
        }
        oldKnowledge["answers"] = newAnswers;
        canonical["services"] = newRoutes; canonical["notifyOwner"] = input.NotifyOwner;
        if (canonical.Parent is null) oldIntake["canonicalInquiry"] = canonical;
        var contact = Object(previous.ContactAndRetentionPolicyJson); contact["policyText"] = input.ContactAndRetentionNotes ?? string.Empty;
        var capabilities = previous.RequestedCapabilities.Where(x => x != ReceptionistCapabilities.CanonicalIntake).ToList();
        if (input.RequestIntake) capabilities.Add(ReceptionistCapabilities.CanonicalIntake);
        var configuration = previous with
        {
            BusinessName = input.BusinessName, ConversationInstructions = input.ConversationInstructions, BusinessPolicy = input.BusinessPolicy,
            CompactVoicePolicy = string.IsNullOrWhiteSpace(input.CompactVoicePolicy) ? null : input.CompactVoicePolicy.Trim(),
            CompactVoicePolicySourceSha256 = string.IsNullOrWhiteSpace(input.CompactVoicePolicy) ? null : ReceptionistConfigurationCodec.Hash(input.BusinessPolicy),
            ApprovedKnowledgeJson = oldKnowledge.ToJsonString(), ServiceDefinitionsJson = newServices.ToJsonString(),
            IntakeRulesJson = oldIntake.ToJsonString(), ContactAndRetentionPolicyJson = contact.ToJsonString(),
            TimeZoneId = input.TimeZoneId, Currency = input.Currency, MonthlyCashLimit = input.MonthlyCashLimit,
            MonthlyConsumptionLimit = input.MonthlyConsumptionLimit, MonthlyVoiceSecondsLimit = checked((long)(input.MonthlyVoiceMinutesLimit * 60m)),
            MaximumConcurrentCalls = input.MaximumConcurrentCalls, MaximumCallSeconds = input.MaximumCallSeconds,
            AdmissionWindowSeconds = input.AdmissionWindowSeconds, MaximumCallsPerWindow = input.MaximumCallsPerWindow,
            Hours = hours, RequestedCapabilities = capabilities
        };
        // Inactive owner drafts remain editable. Provider acceptance separately validates the exact supported adapter payload and backend.
        ReceptionistConfigurationCodec.Validate(configuration);
        return configuration;
    }

    public static IReadOnlyList<string> CrmBuckets(MarketingOwnerScope owner) => owner.OwnerType == "business"
        ? [Infrastructure.Leads.WorkstationLeadBuckets.ServiceInquiry] : Infrastructure.Leads.WorkstationLeadBuckets.ProductBuckets;

    private static JsonArray Array(string value) => JsonNode.Parse(value) as JsonArray ?? throw new ArgumentException("Saved service configuration must be an array.");
    private static JsonObject Object(string value) => JsonNode.Parse(value) as JsonObject ?? throw new ArgumentException("Saved configuration must be an object.");
    private static string Text(JsonObject? node, string key) => node?[key]?.GetValue<string>() ?? string.Empty;
    private static string Lines(JsonNode? node) => node is JsonArray values ? string.Join('\n', values.Select(x => x?.GetValue<string>() ?? string.Empty)) : string.Empty;
    private static JsonArray Phrases(string? value) => new((value ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => JsonValue.Create(x) as JsonNode).ToArray());
    private static JsonObject Existing(JsonArray values, string key, string? identity) =>
        values.OfType<JsonObject>().SingleOrDefault(x => Text(x, key) == identity)?.DeepClone() as JsonObject ?? new JsonObject();
    private static bool Blank(ReceptionistServiceInput x) => string.IsNullOrWhiteSpace(x.ServiceKey) && string.IsNullOrWhiteSpace(x.DisplayName) &&
        string.IsNullOrWhiteSpace(x.Aliases) && string.IsNullOrWhiteSpace(x.DiscoveryQuestion) && string.IsNullOrWhiteSpace(x.PublicSummary);
    private static bool Blank(ReceptionistAnswerInput x) => string.IsNullOrWhiteSpace(x.Key) && string.IsNullOrWhiteSpace(x.ServiceKey) &&
        string.IsNullOrWhiteSpace(x.Questions) && string.IsNullOrWhiteSpace(x.Answer) && string.IsNullOrWhiteSpace(x.AsOfDate);
}
