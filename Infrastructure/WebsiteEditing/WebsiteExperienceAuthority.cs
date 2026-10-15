using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Entities;
using Shared.Analytics;

namespace Infrastructure.WebsiteEditing;

/// <summary>
/// Declarative, authorable website interaction model. It intentionally contains no
/// endpoint, owner, provider, analytics-event, credential, or verified-outcome fields.
/// Those authorities remain server-owned and are resolved from the published website.
/// </summary>
public sealed class WebsiteExperienceDefinition
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnexpectedFields { get; set; }

    public string Kind { get; set; } = "form";
    public string? SubmitCapability { get; set; }
    public List<WebsiteExperienceStep> Steps { get; set; } = new();
    public List<WebsiteExperienceControl> Controls { get; set; } = new();
    public Dictionary<string, WebsiteExperienceExpression> Calculations { get; set; } = new(StringComparer.Ordinal);
    public List<WebsiteExperienceResult> Results { get; set; } = new();
}

public sealed class WebsiteExperienceStep
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnexpectedFields { get; set; }

    public string Key { get; set; } = "";
    public string? Title { get; set; }
    public string? Description { get; set; }
    public List<string> ControlKeys { get; set; } = new();
    public WebsiteExperienceExpression? VisibleWhen { get; set; }
}

public sealed class WebsiteExperienceControl
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnexpectedFields { get; set; }

    public string Key { get; set; } = "";
    public string Type { get; set; } = "text";
    public string? Label { get; set; }
    public string? HelpText { get; set; }
    public string? Placeholder { get; set; }
    public bool Required { get; set; }
    public JsonElement? DefaultValue { get; set; }
    public decimal? Min { get; set; }
    public decimal? Max { get; set; }
    public decimal? Step { get; set; }
    public int? MaxLength { get; set; }
    public List<WebsiteExperienceOption> Options { get; set; } = new();
    public string? ContactRole { get; set; }
    public bool IncludeInNotification { get; set; } = true;
    public WebsiteExperienceExpression? VisibleWhen { get; set; }
    public WebsiteExperienceAction? Action { get; set; }
}

public sealed class WebsiteExperienceOption
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnexpectedFields { get; set; }

    public string Value { get; set; } = "";
    public string Label { get; set; } = "";
}

public sealed class WebsiteExperienceAction
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnexpectedFields { get; set; }

    public string Type { get; set; } = "next";
    public string? TargetStep { get; set; }
    public string? ActionKey { get; set; }
}

public sealed class WebsiteExperienceResult
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnexpectedFields { get; set; }

    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public WebsiteExperienceExpression Expression { get; set; } = new();
    public string? Format { get; set; }
}

public sealed class WebsiteExperienceExpression
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnexpectedFields { get; set; }

    public string Op { get; set; } = "value";
    public string? Ref { get; set; }
    public JsonElement? Value { get; set; }
    public List<WebsiteExperienceExpression> Values { get; set; } = new();
    public Dictionary<string, JsonElement> Map { get; set; } = new(StringComparer.Ordinal);
}

public sealed record WebsiteExperienceSubmission(
    string FirstName,
    string LastName,
    string Phone,
    string Email,
    string Message,
    bool Consent,
    string ExperienceId,
    IReadOnlyDictionary<string, object?> NormalizedAnswers);

/// <summary>
/// One safety authority for GPT-authored interactive experiences. It is intentionally
/// permissive about presentation/questions/calculations and restrictive about authority:
/// no arbitrary code, endpoints, provider names, owner identifiers, or server outcomes.
/// </summary>
public static class WebsiteExperiencePolicy
{
    public const string LeadCaptureCapability = "lead_capture";

    public static readonly IReadOnlyList<string> AuthorableKinds = Array.AsReadOnly(
        new[] { "form", "calculator", "quiz", "assessment", "configurator", "survey" });

    public static readonly IReadOnlyList<string> AuthorableControlTypes = Array.AsReadOnly(
        new[] { "text", "textarea", "email", "tel", "number", "currency", "range",
            "select", "radio", "choice", "checkbox", "date", "button", "cta" });

    public static readonly IReadOnlyList<string> AuthorableContactRoles = Array.AsReadOnly(
        new[] { "first_name", "last_name", "phone", "email", "message", "consent" });

    public static readonly IReadOnlyList<string> AuthorableActionTypes = Array.AsReadOnly(
        new[] { "next", "back", "submit", "reset", "cta" });

    public static readonly IReadOnlyList<string> AuthorableExpressionOps = Array.AsReadOnly(
        new[] { "value", "ref", "add", "subtract", "multiply", "divide", "min", "max",
            "round", "percent", "equals", "not_equals", "greater_than", "less_than",
            "greater_or_equal", "less_or_equal", "and", "or", "not", "if",
            "coalesce", "concat", "lookup" });

    public static readonly IReadOnlyList<string> AuthorableSubmitCapabilities = Array.AsReadOnly(
        new[] { LeadCaptureCapability });

    private static readonly HashSet<string> Kinds = new(AuthorableKinds, StringComparer.Ordinal);
    private static readonly HashSet<string> ControlTypes = new(AuthorableControlTypes, StringComparer.Ordinal);
    private static readonly HashSet<string> ContactRoles = new(AuthorableContactRoles, StringComparer.Ordinal);
    private static readonly HashSet<string> ActionTypes = new(AuthorableActionTypes, StringComparer.Ordinal);
    private static readonly HashSet<string> ExpressionOps = new(AuthorableExpressionOps, StringComparer.Ordinal);

    public static WebsiteExperienceDefinition? Sanitize(WebsiteExperienceDefinition? source)
    {
        if (source is null) return null;
        RejectUnexpected(source.UnexpectedFields, "experience");

        var kind = CleanToken(source.Kind, 40);
        if (!Kinds.Contains(kind))
            throw new ArgumentException("Website experience kind is not supported.");

        var capability = string.IsNullOrWhiteSpace(source.SubmitCapability)
            ? null
            : CleanToken(source.SubmitCapability, 80);
        if (capability is not null && capability != LeadCaptureCapability)
            throw new ArgumentException("Website experience submit capability is not available.");

        var clean = new WebsiteExperienceDefinition
        {
            Kind = kind,
            SubmitCapability = capability
        };

        var controlKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var control in (source.Controls ?? []).Take(80))
        {
            RejectUnexpected(control.UnexpectedFields, "experience.control");
            var key = CleanKey(control.Key, 80);
            if (key.Length == 0 || !controlKeys.Add(key))
                throw new ArgumentException("Website experience control keys must be non-empty and unique.");

            var type = CleanToken(control.Type, 40);
            if (!ControlTypes.Contains(type))
                throw new ArgumentException($"Website experience control '{key}' uses an unsupported type.");

            var role = string.IsNullOrWhiteSpace(control.ContactRole) ? null : CleanToken(control.ContactRole, 40);
            if (role is not null && (!ContactRoles.Contains(role) || type is "button" or "cta"))
                throw new ArgumentException($"Website experience control '{key}' has an invalid contact role.");

            var action = SanitizeAction(control.Action);
            if (type is "button" or "cta")
            {
                action ??= new WebsiteExperienceAction { Type = type == "cta" ? "cta" : "next" };
                if (type == "cta" && action.Type != "cta")
                    throw new ArgumentException($"Website experience CTA '{key}' must use a CTA action.");
            }
            else if (action is not null)
            {
                throw new ArgumentException($"Website experience input '{key}' cannot carry a button action.");
            }

            var maxLength = Math.Clamp(control.MaxLength ?? (type == "textarea" ? 4000 : 500), 1, type == "textarea" ? 8000 : 2000);
            var options = new List<WebsiteExperienceOption>();
            var optionValues = new HashSet<string>(StringComparer.Ordinal);
            foreach (var option in (control.Options ?? []).Take(50))
            {
                RejectUnexpected(option.UnexpectedFields, $"experience.control:{key}.option");
                var value = CleanScalar(option.Value, 120);
                var label = CleanScalar(option.Label, 240);
                if (value.Length == 0 || label.Length == 0 || !optionValues.Add(value))
                    throw new ArgumentException($"Website experience control '{key}' has invalid or duplicate options.");
                options.Add(new() { Value = value, Label = label });
            }

            if (type is "select" or "radio" or "choice" && options.Count == 0)
                throw new ArgumentException($"Website experience control '{key}' requires options.");

            var expressionBudget = 128;
            clean.Controls.Add(new WebsiteExperienceControl
            {
                Key = key,
                Type = type,
                Label = CleanText(control.Label, 300),
                HelpText = CleanText(control.HelpText, 800),
                Placeholder = CleanText(control.Placeholder, 300),
                Required = control.Required,
                DefaultValue = SanitizeLiteral(control.DefaultValue, maxLength),
                Min = ClampNumber(control.Min),
                Max = ClampNumber(control.Max),
                Step = ClampPositiveNumber(control.Step),
                MaxLength = maxLength,
                Options = options,
                ContactRole = role,
                IncludeInNotification = control.IncludeInNotification,
                VisibleWhen = SanitizeExpression(control.VisibleWhen, 0, ref expressionBudget),
                Action = action
            });
        }

        if ((source.Controls?.Count ?? 0) > 80)
            throw new ArgumentException("Website experience control limit exceeded.");

        var stepKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in (source.Steps ?? []).Take(24))
        {
            RejectUnexpected(step.UnexpectedFields, "experience.step");
            var key = CleanKey(step.Key, 80);
            if (key.Length == 0 || !stepKeys.Add(key))
                throw new ArgumentException("Website experience step keys must be non-empty and unique.");
            var controls = (step.ControlKeys ?? []).Select(value => CleanKey(value, 80))
                .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).Take(80).ToList();
            var budget = 128;
            clean.Steps.Add(new()
            {
                Key = key,
                Title = CleanText(step.Title, 300),
                Description = CleanText(step.Description, 1000),
                ControlKeys = controls,
                VisibleWhen = SanitizeExpression(step.VisibleWhen, 0, ref budget)
            });
        }

        if ((source.Steps?.Count ?? 0) > 24)
            throw new ArgumentException("Website experience step limit exceeded.");

        foreach (var (rawKey, expression) in (source.Calculations ?? new()).Take(32))
        {
            var key = CleanKey(rawKey, 80);
            if (key.Length == 0 || clean.Calculations.ContainsKey(key))
                throw new ArgumentException("Website experience calculation keys must be non-empty and unique.");
            var budget = 256;
            clean.Calculations[key] = SanitizeExpression(expression, 0, ref budget)
                ?? throw new ArgumentException($"Website experience calculation '{key}' requires an expression.");
        }

        if ((source.Calculations?.Count ?? 0) > 32)
            throw new ArgumentException("Website experience calculation limit exceeded.");

        var resultKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var result in (source.Results ?? []).Take(24))
        {
            RejectUnexpected(result.UnexpectedFields, "experience.result");
            var key = CleanKey(result.Key, 80);
            if (key.Length == 0 || !resultKeys.Add(key))
                throw new ArgumentException("Website experience result keys must be non-empty and unique.");
            var budget = 256;
            clean.Results.Add(new()
            {
                Key = key,
                Label = CleanScalar(result.Label, 300),
                Expression = SanitizeExpression(result.Expression, 0, ref budget)
                    ?? throw new ArgumentException($"Website experience result '{key}' requires an expression."),
                Format = SanitizeFormat(result.Format)
            });
        }

        if ((source.Results?.Count ?? 0) > 24)
            throw new ArgumentException("Website experience result limit exceeded.");

        ValidateStructure(clean);
        return clean;
    }

    public static void ValidateForPublish(
        WebsiteExperienceDefinition definition,
        IReadOnlySet<string> availableActionKeys)
    {
        ValidateStructure(definition);

        var stepKeys = definition.Steps.Select(step => step.Key).ToHashSet(StringComparer.Ordinal);
        var controlKeys = definition.Controls.Select(control => control.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var step in definition.Steps)
        {
            if (step.ControlKeys.Any(key => !controlKeys.Contains(key)))
                throw new ArgumentException($"Website experience step '{step.Key}' references an unavailable control.");
        }

        if (definition.Steps.Count > 0)
        {
            var placements = definition.Steps
                .SelectMany(step => step.ControlKeys)
                .GroupBy(key => key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            foreach (var control in definition.Controls)
            {
                if (!placements.TryGetValue(control.Key, out var count) || count != 1)
                    throw new ArgumentException(
                        $"Website experience control '{control.Key}' must appear exactly once across explicit steps.");
            }
        }

        foreach (var control in definition.Controls.Where(control => control.Action is not null))
        {
            var action = control.Action!;
            if (!string.IsNullOrWhiteSpace(action.TargetStep) && !stepKeys.Contains(action.TargetStep))
                throw new ArgumentException($"Website experience action '{control.Key}' references an unavailable step.");
            if (action.Type == "cta" &&
                (string.IsNullOrWhiteSpace(action.ActionKey) || !availableActionKeys.Contains(action.ActionKey)))
                throw new WebsiteSiteSourceProtectionException(
                    $"Website experience CTA '{control.Key}' must use an available canonical action.");
        }

        if (definition.SubmitCapability == LeadCaptureCapability)
        {
            var roles = definition.Controls
                .Where(control => !string.IsNullOrWhiteSpace(control.ContactRole))
                .GroupBy(control => control.ContactRole!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            foreach (var role in new[] { "first_name", "last_name", "phone", "email", "consent" })
            {
                if (!roles.TryGetValue(role, out var count) || count != 1)
                    throw new ArgumentException(
                        $"Lead capture experiences require exactly one '{role}' contact-role control.");
            }

            var firstName = definition.Controls.Single(control => control.ContactRole == "first_name");
            var lastName = definition.Controls.Single(control => control.ContactRole == "last_name");
            var phone = definition.Controls.Single(control => control.ContactRole == "phone");
            var email = definition.Controls.Single(control => control.ContactRole == "email");
            var consent = definition.Controls.Single(control => control.ContactRole == "consent");

            if (firstName.Type != "text" || lastName.Type != "text")
                throw new ArgumentException("Lead capture first and last name controls must use text inputs.");
            if (phone.Type != "tel")
                throw new ArgumentException("Lead capture phone controls must use the telephone input type.");
            if (email.Type != "email")
                throw new ArgumentException("Lead capture email controls must use the email input type.");
            if (consent.Type != "checkbox" || !consent.Required)
                throw new ArgumentException("Lead capture consent must be a required checkbox.");
            if (consent.DefaultValue is JsonElement consentDefault &&
                consentDefault.ValueKind == JsonValueKind.True)
                throw new ArgumentException("Lead capture consent cannot be preselected.");

            var message = definition.Controls.SingleOrDefault(control => control.ContactRole == "message");
            if (message is not null && message.Type is not ("text" or "textarea"))
                throw new ArgumentException("Lead capture message controls must use text or textarea.");

            if (!definition.Controls.Any(control => control.Action?.Type == "submit"))
                throw new ArgumentException("Lead capture experiences require a submit action.");
        }
    }

    public static WebsiteExperienceSubmission? ResolvePublishedSubmission(
        WebsiteContentVersion? version,
        string siteKey,
        string path,
        string? experienceId,
        IReadOnlyDictionary<string, JsonElement>? answers)
    {
        if (version is null || string.IsNullOrWhiteSpace(experienceId))
            return null;

        WebsiteContentDocument document;
        try
        {
            document = WebsiteContentSanitizer.ReadPersisted(
                version.DocumentJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }

        var route = NormalizePublishedPath(siteKey, path);
        var page = document.Pages.FirstOrDefault(pair =>
            string.Equals(NormalizePublishedPath(siteKey, pair.Key), route, StringComparison.OrdinalIgnoreCase)).Value;
        if (page is null) return null;

        var node = FindNode(page.Composition, experienceId.Trim());
        if (node?.Type != "experience" || node.Experience is null ||
            node.Experience.SubmitCapability != LeadCaptureCapability)
            return null;

        return ValidateSubmission(node.Id, node.Experience, answers ?? new Dictionary<string, JsonElement>());
    }

    private static WebsiteExperienceSubmission ValidateSubmission(
        string experienceId,
        WebsiteExperienceDefinition definition,
        IReadOnlyDictionary<string, JsonElement> supplied)
    {
        var controls = definition.Controls.ToDictionary(control => control.Key, StringComparer.Ordinal);
        if (supplied.Keys.Any(key => !controls.ContainsKey(key)))
            throw new ArgumentException("Website experience submission contains an unknown control.");

        var normalized = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var control in definition.Controls)
        {
            if (control.Type is "button" or "cta") continue;

            var visible = control.VisibleWhen is null ||
                ToBoolean(Evaluate(control.VisibleWhen, normalized, supplied, definition.Calculations, new HashSet<string>(StringComparer.Ordinal)));
            if (!visible) continue;

            supplied.TryGetValue(control.Key, out var raw);
            object? value = raw.ValueKind == JsonValueKind.Undefined
                ? LiteralToObject(control.DefaultValue)
                : JsonToObject(raw);

            value = NormalizeAnswer(control, value);
            if (control.Required && IsEmpty(value))
                throw new ArgumentException($"Website experience control '{control.Key}' is required.");
            if (!IsEmpty(value)) normalized[control.Key] = value;
        }

        var roleValues = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var control in definition.Controls.Where(control => !string.IsNullOrWhiteSpace(control.ContactRole)))
            if (normalized.TryGetValue(control.Key, out var value))
                roleValues[control.ContactRole!] = value;

        var consent = roleValues.TryGetValue("consent", out var consentValue) && ToBoolean(consentValue);
        var firstName = ToText(roleValues.GetValueOrDefault("first_name"));
        var lastName = ToText(roleValues.GetValueOrDefault("last_name"));
        var phone = ToText(roleValues.GetValueOrDefault("phone"));
        var email = ToText(roleValues.GetValueOrDefault("email"));
        var message = ToText(roleValues.GetValueOrDefault("message"));

        var summary = BuildNotificationSummary(definition, normalized);
        if (!string.IsNullOrWhiteSpace(summary))
            message = string.IsNullOrWhiteSpace(message)
                ? summary
                : message.Trim() + "\n\nAdditional responses:\n" + summary;
        if (string.IsNullOrWhiteSpace(message))
            message = "Website inquiry";
        if (message.Length > 12000)
            message = message[..12000];

        return new(
            firstName,
            lastName,
            phone,
            email,
            message,
            consent,
            experienceId,
            normalized);
    }

    private static string BuildNotificationSummary(
        WebsiteExperienceDefinition definition,
        IReadOnlyDictionary<string, object?> normalized)
    {
        var lines = new List<string>();
        foreach (var control in definition.Controls)
        {
            if (!control.IncludeInNotification ||
                control.Type is "button" or "cta" ||
                control.ContactRole is "first_name" or "last_name" or "phone" or "email" or "consent" or "message" ||
                !normalized.TryGetValue(control.Key, out var value) ||
                IsEmpty(value))
                continue;

            lines.Add($"{control.Label ?? control.Key}: {DisplayValue(control, value)}");
        }

        var calculationStack = new HashSet<string>(StringComparer.Ordinal);
        foreach (var result in definition.Results)
        {
            var value = Evaluate(
                result.Expression,
                normalized,
                EmptySuppliedAnswers,
                definition.Calculations,
                calculationStack);
            if (IsEmpty(value)) continue;
            lines.Add($"{result.Label}: {FormatResult(value, result.Format)}");
        }

        var text = string.Join("\n", lines);
        return text.Length <= 8000 ? text : text[..8000];
    }

    private static object? NormalizeAnswer(WebsiteExperienceControl control, object? value)
    {
        if (value is null) return null;

        if (control.Type == "checkbox")
            return ToBoolean(value);

        if (control.Type is "number" or "currency" or "range")
        {
            if (!TryDecimal(value, out var number))
                throw new ArgumentException($"Website experience control '{control.Key}' requires a number.");
            if (control.Min.HasValue && number < control.Min.Value ||
                control.Max.HasValue && number > control.Max.Value)
                throw new ArgumentException($"Website experience control '{control.Key}' is outside its allowed range.");
            return number;
        }

        var text = ToText(value).Trim();
        if (text.Length > (control.MaxLength ?? 2000) || text.Any(char.IsControl))
            throw new ArgumentException($"Website experience control '{control.Key}' is invalid.");

        if (control.Type is "select" or "radio" or "choice")
        {
            if (!control.Options.Any(option => option.Value == text))
                throw new ArgumentException($"Website experience control '{control.Key}' has an invalid option.");
        }
        else if (control.Type == "date" && text.Length > 0 &&
                 !DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw new ArgumentException($"Website experience control '{control.Key}' requires a valid date.");
        }

        return text;
    }

    private static void ValidateStructure(WebsiteExperienceDefinition definition)
    {
        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var control in definition.Controls.Where(control => !string.IsNullOrWhiteSpace(control.ContactRole)))
            if (!roles.Add(control.ContactRole!))
                throw new ArgumentException($"Website experience contact role '{control.ContactRole}' can be assigned only once.");
    }

    private static WebsiteExperienceAction? SanitizeAction(WebsiteExperienceAction? source)
    {
        if (source is null) return null;
        RejectUnexpected(source.UnexpectedFields, "experience.action");
        var type = CleanToken(source.Type, 40);
        if (!ActionTypes.Contains(type))
            throw new ArgumentException("Website experience action is not supported.");
        var target = string.IsNullOrWhiteSpace(source.TargetStep) ? null : CleanKey(source.TargetStep, 80);
        var actionKey = string.IsNullOrWhiteSpace(source.ActionKey) ? null : CleanKey(source.ActionKey, 160);
        if (type == "cta" && string.IsNullOrWhiteSpace(actionKey))
            throw new ArgumentException("Website experience CTA actions require a canonical ActionKey.");
        if (type != "cta" && !string.IsNullOrWhiteSpace(actionKey))
            throw new ArgumentException("Only Website experience CTA actions may carry an ActionKey.");
        return new() { Type = type, TargetStep = target, ActionKey = actionKey };
    }

    private static WebsiteExperienceExpression? SanitizeExpression(
        WebsiteExperienceExpression? source,
        int depth,
        ref int remaining)
    {
        if (source is null) return null;
        if (depth > 12 || --remaining < 0)
            throw new ArgumentException("Website experience expression complexity limit exceeded.");
        RejectUnexpected(source.UnexpectedFields, "experience.expression");

        var op = CleanToken(source.Op, 40);
        if (!ExpressionOps.Contains(op))
            throw new ArgumentException($"Website experience expression operation '{op}' is not supported.");

        var clean = new WebsiteExperienceExpression
        {
            Op = op,
            Ref = string.IsNullOrWhiteSpace(source.Ref) ? null : CleanKey(source.Ref, 120),
            Value = SanitizeLiteral(source.Value, 500)
        };

        foreach (var value in (source.Values ?? []).Take(24))
            clean.Values.Add(SanitizeExpression(value, depth + 1, ref remaining)
                ?? throw new ArgumentException("Website experience expression value is invalid."));

        foreach (var (key, value) in (source.Map ?? new()).Take(50))
        {
            var mapKey = CleanScalar(key, 120);
            if (mapKey.Length == 0) continue;
            clean.Map[mapKey] = SanitizeLiteral(value, 500)
                ?? JsonSerializer.SerializeToElement<object?>(null);
        }

        if (op == "ref" && string.IsNullOrWhiteSpace(clean.Ref))
            throw new ArgumentException("Website experience ref expressions require a reference.");
        if (op == "value" && !clean.Value.HasValue)
            throw new ArgumentException("Website experience value expressions require a literal.");
        if (op == "lookup" && clean.Map.Count == 0)
            throw new ArgumentException("Website experience lookup expressions require a map.");

        return clean;
    }

    private static readonly IReadOnlyDictionary<string, JsonElement> EmptySuppliedAnswers =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    private static object? Evaluate(
        WebsiteExperienceExpression expression,
        IReadOnlyDictionary<string, object?> normalized,
        IReadOnlyDictionary<string, JsonElement> supplied,
        IReadOnlyDictionary<string, WebsiteExperienceExpression> calculations,
        HashSet<string> calculationStack)
    {
        object? Eval(int index) => index < expression.Values.Count
            ? Evaluate(expression.Values[index], normalized, supplied, calculations, calculationStack)
            : null;
        var values = expression.Values
            .Select(value => Evaluate(value, normalized, supplied, calculations, calculationStack))
            .ToArray();

        switch (expression.Op)
        {
            case "value": return LiteralToObject(expression.Value);
            case "ref":
            {
                var key = expression.Ref ?? "";
                if (key.StartsWith("answer.", StringComparison.Ordinal)) key = key["answer.".Length..];
                if (key.StartsWith("calc.", StringComparison.Ordinal))
                {
                    var calculation = key["calc.".Length..];
                    if (!calculations.TryGetValue(calculation, out var calc) || !calculationStack.Add(calculation))
                        return null;
                    try { return Evaluate(calc, normalized, supplied, calculations, calculationStack); }
                    finally { calculationStack.Remove(calculation); }
                }
                if (normalized.TryGetValue(key, out var normalizedValue)) return normalizedValue;
                return supplied.TryGetValue(key, out var raw) ? JsonToObject(raw) : null;
            }
            case "add": return values.Sum(ToDecimal);
            case "subtract": return values.Length == 0 ? 0m : values.Skip(1).Aggregate(ToDecimal(values[0]), (current, value) => current - ToDecimal(value));
            case "multiply": return values.Length == 0 ? 0m : values.Aggregate(1m, (current, value) => current * ToDecimal(value));
            case "divide":
            {
                if (values.Length == 0) return 0m;
                var result = ToDecimal(values[0]);
                foreach (var value in values.Skip(1))
                {
                    var divisor = ToDecimal(value);
                    if (divisor == 0) return null;
                    result /= divisor;
                }
                return result;
            }
            case "min": return values.Length == 0 ? 0m : values.Min(ToDecimal);
            case "max": return values.Length == 0 ? 0m : values.Max(ToDecimal);
            case "round":
            {
                var digits = values.Length > 1 ? Math.Clamp((int)ToDecimal(values[1]), 0, 6) : 0;
                return Math.Round(ToDecimal(Eval(0)), digits, MidpointRounding.AwayFromZero);
            }
            case "percent": return ToDecimal(Eval(0)) / 100m;
            case "equals": return Compare(values, 0) == 0;
            case "not_equals": return Compare(values, 0) != 0;
            case "greater_than": return Compare(values, 0) > 0;
            case "less_than": return Compare(values, 0) < 0;
            case "greater_or_equal": return Compare(values, 0) >= 0;
            case "less_or_equal": return Compare(values, 0) <= 0;
            case "and": return values.All(ToBoolean);
            case "or": return values.Any(ToBoolean);
            case "not": return !ToBoolean(Eval(0));
            case "if": return ToBoolean(Eval(0)) ? Eval(1) : Eval(2);
            case "coalesce": return values.FirstOrDefault(value => !IsEmpty(value));
            case "concat": return string.Concat(values.Select(ToText));
            case "lookup":
            {
                var key = ToText(Eval(0));
                return expression.Map.TryGetValue(key, out var mapped) ? JsonToObject(mapped) : null;
            }
            default: return null;
        }
    }

    private static int Compare(IReadOnlyList<object?> values, int fallback)
    {
        if (values.Count < 2) return fallback;
        if (TryDecimal(values[0], out var left) && TryDecimal(values[1], out var right))
            return left.CompareTo(right);
        return string.Compare(ToText(values[0]), ToText(values[1]), StringComparison.OrdinalIgnoreCase);
    }

    private static decimal ToDecimal(object? value) => TryDecimal(value, out var parsed) ? parsed : 0m;

    private static bool TryDecimal(object? value, out decimal result)
    {
        if (value is decimal direct) { result = direct; return true; }
        if (value is int integer) { result = integer; return true; }
        if (value is long longValue) { result = longValue; return true; }
        if (value is double doubleValue && double.IsFinite(doubleValue))
        {
            result = (decimal)doubleValue;
            return true;
        }
        return decimal.TryParse(ToText(value), NumberStyles.Number, CultureInfo.InvariantCulture, out result);
    }

    private static bool ToBoolean(object? value)
    {
        if (value is bool boolean) return boolean;
        if (TryDecimal(value, out var number)) return number != 0;
        var text = ToText(value);
        return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "on", StringComparison.OrdinalIgnoreCase);
    }

    private static string ToText(object? value) => value switch
    {
        null => "",
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        bool boolean => boolean ? "true" : "false",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    };

    private static bool IsEmpty(object? value) =>
        value is null || value is string text && string.IsNullOrWhiteSpace(text);

    private static object? JsonToObject(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => value.ToString()
    };

    private static object? LiteralToObject(JsonElement? value) =>
        value.HasValue ? JsonToObject(value.Value) : null;

    private static string DisplayValue(WebsiteExperienceControl control, object? value)
    {
        var text = ToText(value);
        var option = control.Options.FirstOrDefault(candidate => candidate.Value == text);
        return option?.Label ?? (value is bool boolean ? (boolean ? "Yes" : "No") : text);
    }

    private static string FormatResult(object? value, string? format)
    {
        if (!TryDecimal(value, out var number)) return ToText(value);
        return format switch
        {
            "currency" => number.ToString("C0", CultureInfo.GetCultureInfo("en-US")),
            "percent" => number.ToString("0.##", CultureInfo.InvariantCulture) + "%",
            "integer" => Math.Round(number, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture),
            _ => number.ToString("0.##", CultureInfo.InvariantCulture)
        };
    }

    private static WebsiteCompositionNode? FindNode(IEnumerable<WebsiteCompositionNode>? nodes, string id)
    {
        foreach (var node in nodes ?? [])
        {
            if (node.Id == id) return node;
            var child = FindNode(node.Children, id);
            if (child is not null) return child;
        }
        return null;
    }

    private static string NormalizePublishedPath(string siteKey, string path)
    {
        var route = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
        if (siteKey == WebsiteEditorSiteKeys.Protect)
        {
            if (route.StartsWith("/a/", StringComparison.OrdinalIgnoreCase))
            {
                var next = route.IndexOf('/', 3);
                route = next < 0 ? "/" : route[next..];
            }
            route = ProtectRouteCatalog.CanonicalPath(route);
        }
        route = route.Length > 1 ? route.TrimEnd('/') : route;
        return route.Length == 0 ? "/" : route;
    }

    private static JsonElement? SanitizeLiteral(JsonElement? value, int maxText)
    {
        if (!value.HasValue) return null;
        var element = value.Value;
        return element.ValueKind switch
        {
            JsonValueKind.String => JsonSerializer.SerializeToElement(CleanScalar(element.GetString(), maxText)),
            JsonValueKind.Number when element.TryGetDecimal(out var number) =>
                JsonSerializer.SerializeToElement(Math.Clamp(number, -1_000_000_000_000m, 1_000_000_000_000m)),
            JsonValueKind.True => JsonSerializer.SerializeToElement(true),
            JsonValueKind.False => JsonSerializer.SerializeToElement(false),
            JsonValueKind.Null => JsonSerializer.SerializeToElement<object?>(null),
            _ => throw new ArgumentException("Website experience literals must be scalar values.")
        };
    }

    private static decimal? ClampNumber(decimal? value) =>
        value.HasValue ? Math.Clamp(value.Value, -1_000_000_000_000m, 1_000_000_000_000m) : null;

    private static decimal? ClampPositiveNumber(decimal? value) =>
        value.HasValue ? Math.Clamp(value.Value, 0.000001m, 1_000_000_000_000m) : null;

    private static string? SanitizeFormat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = CleanToken(value, 40);
        return clean is "currency" or "percent" or "integer" or "number" ? clean : null;
    }

    private static string CleanKey(string? value, int max)
    {
        var chars = (value ?? "").Trim().Take(max)
            .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or ':')
            .ToArray();
        return chars.Length == 0 ? "" : new string(chars);
    }

    private static string CleanToken(string? value, int max) =>
        CleanKey(value, max).ToLowerInvariant();

    private static string CleanScalar(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var clean = value.Trim();
        if (clean.Length > max || clean.Any(char.IsControl))
            throw new ArgumentException("Website experience text is invalid.");
        return clean;
    }

    private static string? CleanText(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : CleanScalar(value, max);

    private static void RejectUnexpected(
        IReadOnlyDictionary<string, JsonElement>? fields,
        string location)
    {
        if (fields is null || fields.Count == 0) return;
        throw new ArgumentException(
            $"Unsupported website experience field(s) at {location}: {string.Join(", ", fields.Keys.OrderBy(value => value, StringComparer.Ordinal))}");
    }
}
