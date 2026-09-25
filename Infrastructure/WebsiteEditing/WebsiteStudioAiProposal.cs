using System.Text.Json;

namespace Infrastructure.WebsiteEditing;

public sealed class WebsiteStudioAiOperation
{
    public string Kind { get; set; } = string.Empty;
    public string? BreakpointKey { get; set; }
    public string? Text { get; set; }
    public string? Title { get; set; }
    public string? Href { get; set; }
    public string? ImagePrompt { get; set; }
    public WebsiteStyleOverride? Style { get; set; }
    public WebsiteLayoutOverride? Layout { get; set; }
}

public sealed record WebsiteStudioAiAppliedProposal(
    string Mode,
    string Summary,
    WebsiteContentDocument ProposedDocument,
    IReadOnlyList<WebsiteStudioAiOperation> Operations);

/// <summary>
/// Applies bounded AI suggestions to an in-memory website document only. This policy
/// never saves, publishes, dispatches analytics, or chooses an owner/destination.
/// </summary>
public static class WebsiteStudioAiProposalPolicy
{
    private const int MaxOperations = 20;
    private const int MaxPromptText = 12000;

    public static WebsiteStudioAiAppliedProposal Apply(
        WebsiteContentDocument source,
        string mode,
        string summary,
        string pagePath,
        string? selectedElementId,
        string? selectedSectionId,
        IEnumerable<WebsiteStudioAiOperation>? operations)
    {
        mode = (mode ?? string.Empty).Trim().ToLowerInvariant();
        if (mode is not ("responsive" or "create"))
            throw new ArgumentException("Website AI mode must be responsive or create.");

        var route = NormalizePagePath(pagePath)
            ?? throw new ArgumentException("Website AI requires a valid current page route.");
        var cleanSource = WebsiteContentSanitizer.Sanitize(source);
        var document = Clone(cleanSource);
        var operationList = (operations ?? []).Where(operation => operation is not null).Take(MaxOperations).ToList();

        foreach (var operation in operationList)
        {
            var kind = (operation.Kind ?? string.Empty).Trim().ToLowerInvariant();
            if (mode == "responsive" && kind is not ("set_style" or "set_layout"))
                throw new ArgumentException("Responsive AI may propose only responsive style or layout changes.");

            switch (kind)
            {
                case "set_text":
                    RequireCreateMode(mode);
                    ApplyText(document, route, selectedElementId, operation.Text);
                    break;
                case "set_style":
                    ApplyStyle(document, route, selectedElementId, operation.BreakpointKey, operation.Style);
                    break;
                case "set_layout":
                    ApplyLayout(document, route, selectedElementId, operation.BreakpointKey, operation.Layout);
                    break;
                case "add_section":
                    RequireCreateMode(mode);
                    AddSection(document, route, operation);
                    break;
                case "add_text":
                    RequireCreateMode(mode);
                    AddText(document, route, selectedSectionId, operation.Text);
                    break;
                case "add_button":
                    RequireCreateMode(mode);
                    AddButton(document, route, selectedSectionId, operation.Text, operation.Href);
                    break;
                case "suggest_image":
                    RequireCreateMode(mode);
                    // Image assistance is intentionally advisory until an actual scoped
                    // media asset exists. The prompt is returned to the editor, not saved.
                    break;
                default:
                    throw new ArgumentException("Website AI returned an unsupported operation.");
            }
        }

        var proposed = WebsiteContentSanitizer.Sanitize(document);
        return new WebsiteStudioAiAppliedProposal(
            mode,
            Clamp(summary, 1200) ?? "Website Studio AI proposal",
            proposed,
            operationList);
    }

    private static void RequireCreateMode(string mode)
    {
        if (mode != "create")
            throw new ArgumentException("This operation is available only for creation assistance.");
    }

    private static void ApplyText(
        WebsiteContentDocument document,
        string route,
        string? selectedElementId,
        string? text)
    {
        var target = RequireTarget(selectedElementId);
        var value = Clamp(text, MaxPromptText) ?? string.Empty;
        if (TryExtra(document, route, target, out var extra, out var field))
        {
            if (field == "title") extra.Title = value;
            else extra.Text = value;
            return;
        }

        var element = Element(document, route, target, create: true)!;
        element.Text = value;
    }

    private static void ApplyStyle(
        WebsiteContentDocument document,
        string route,
        string? selectedElementId,
        string? breakpointKey,
        WebsiteStyleOverride? style)
    {
        var target = RequireTarget(selectedElementId);
        var targetStyle = style ?? new WebsiteStyleOverride();
        if (TryExtra(document, route, target, out var extra, out _))
        {
            SetStyle(document, extra, breakpointKey, targetStyle);
            return;
        }

        var element = Element(document, route, target, create: true)!;
        SetStyle(document, element, breakpointKey, targetStyle);
    }

    private static void ApplyLayout(
        WebsiteContentDocument document,
        string route,
        string? selectedElementId,
        string? breakpointKey,
        WebsiteLayoutOverride? layout)
    {
        var target = RequireTarget(selectedElementId);
        var targetLayout = layout ?? new WebsiteLayoutOverride();
        if (TryExtra(document, route, target, out var extra, out _))
        {
            SetLayout(document, extra, breakpointKey, targetLayout);
            return;
        }

        var element = Element(document, route, target, create: true)!;
        SetLayout(document, element, breakpointKey, targetLayout);
    }

    private static void SetStyle(
        WebsiteContentDocument document,
        WebsiteElementOverride element,
        string? breakpointKey,
        WebsiteStyleOverride style)
    {
        var key = ValidateBreakpoint(document, breakpointKey);
        if (key is null) element.Style = style;
        else element.BreakpointStyles[key] = style;
    }

    private static void SetStyle(
        WebsiteContentDocument document,
        WebsiteExtraComponent extra,
        string? breakpointKey,
        WebsiteStyleOverride style)
    {
        var key = ValidateBreakpoint(document, breakpointKey);
        if (key is null) extra.Style = style;
        else extra.BreakpointStyles[key] = style;
    }

    private static void SetLayout(
        WebsiteContentDocument document,
        WebsiteElementOverride element,
        string? breakpointKey,
        WebsiteLayoutOverride layout)
    {
        var key = ValidateBreakpoint(document, breakpointKey);
        if (key is null) element.Layout = layout;
        else element.BreakpointLayouts[key] = layout;
    }

    private static void SetLayout(
        WebsiteContentDocument document,
        WebsiteExtraComponent extra,
        string? breakpointKey,
        WebsiteLayoutOverride layout)
    {
        var key = ValidateBreakpoint(document, breakpointKey);
        if (key is null) extra.Layout = layout;
        else extra.BreakpointLayouts[key] = layout;
    }

    private static string? ValidateBreakpoint(WebsiteContentDocument document, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "base") return null;
        var key = value.Trim();
        if (!(document.Breakpoints ?? []).Any(breakpoint => breakpoint.Key == key))
            throw new ArgumentException("Website AI referenced an unavailable breakpoint.");
        return key;
    }

    private static void AddSection(
        WebsiteContentDocument document,
        string route,
        WebsiteStudioAiOperation operation)
    {
        var page = Page(document, route, create: true)!;
        if (page.Extras.Count >= 120) throw new ArgumentException("Website page block limit reached.");
        var id = Guid.NewGuid().ToString("N");
        page.Extras.Add(new WebsiteExtraComponent
        {
            Id = id,
            Type = "section",
            SectionId = "ai.root",
            Style = operation.Style ?? new WebsiteStyleOverride(),
            Layout = operation.Layout ?? new WebsiteLayoutOverride()
        });
        var copy = Clamp(operation.Text ?? operation.Title, MaxPromptText);
        if (!string.IsNullOrWhiteSpace(copy))
        {
            page.Extras.Add(new WebsiteExtraComponent
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = "text",
                SectionId = "extra:" + id,
                Text = copy,
                Style = new WebsiteStyleOverride()
            });
        }
    }

    private static void AddText(
        WebsiteContentDocument document,
        string route,
        string? selectedSectionId,
        string? text)
    {
        var section = RequireSection(selectedSectionId);
        Page(document, route, create: true)!.Extras.Add(new WebsiteExtraComponent
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "text",
            SectionId = section,
            Text = Clamp(text, MaxPromptText) ?? string.Empty,
            Style = new WebsiteStyleOverride()
        });
    }

    private static void AddButton(
        WebsiteContentDocument document,
        string route,
        string? selectedSectionId,
        string? text,
        string? href)
    {
        var section = RequireSection(selectedSectionId);
        Page(document, route, create: true)!.Extras.Add(new WebsiteExtraComponent
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "button",
            SectionId = section,
            Text = Clamp(text, 300) ?? "Learn more",
            Href = WebsiteContentSanitizer.SanitizeUrl(href),
            Target = "_self",
            Style = new WebsiteStyleOverride()
        });
    }

    private static WebsitePageDocument? Page(WebsiteContentDocument document, string route, bool create)
    {
        document.Pages ??= new(StringComparer.Ordinal);
        if (document.Pages.TryGetValue(route, out var page)) return page;
        if (!create) return null;
        page = new WebsitePageDocument
        {
            Navigation = new WebsitePageNavigation
            {
                Label = route == "/" ? "Home" : route.Split('/').LastOrDefault(),
                ShowInNavigation = false
            }
        };
        document.Pages[route] = page;
        return page;
    }

    private static WebsiteElementOverride? Element(
        WebsiteContentDocument document,
        string route,
        string target,
        bool create)
    {
        var page = Page(document, route, create);
        if (page is null) return null;
        if (page.Elements.TryGetValue(target, out var value)) return value;
        if (!create) return null;
        value = new WebsiteElementOverride();
        page.Elements[target] = value;
        return value;
    }

    private static bool TryExtra(
        WebsiteContentDocument document,
        string route,
        string target,
        out WebsiteExtraComponent extra,
        out string field)
    {
        extra = null!;
        field = "text";
        if (!target.StartsWith("extra:", StringComparison.Ordinal)) return false;
        var parts = target.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        var id = parts[1];
        field = parts.Length >= 3 && parts[2] == "title" ? "title" : "text";
        var page = Page(document, route, create: false);
        extra = page?.Extras.FirstOrDefault(item => item.Id == id)!;
        return extra is not null;
    }

    private static string RequireTarget(string? value)
    {
        var id = SanitizeId(value);
        return id.Length == 0
            ? throw new ArgumentException("Select a website element before requesting this AI change.")
            : id;
    }

    private static string RequireSection(string? value)
    {
        var id = SanitizeId(value);
        return id.Length == 0
            ? throw new ArgumentException("Select a website section before requesting new content.")
            : id;
    }

    private static string SanitizeId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return new string(value.Trim().Take(160)
            .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or ':')
            .ToArray());
    }

    private static string? NormalizePagePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var route = value.Trim();
        if (!route.StartsWith('/') || route.StartsWith("//") || route.Contains('?') ||
            route.Contains('#') || route.Contains("..") || route.Contains('\') ||
            route.Any(char.IsControl) || route.Length > 160)
            return null;
        return route.Length > 1 ? route.TrimEnd('/') : route;
    }

    private static string? Clamp(string? value, int maximum)
    {
        if (value is null) return null;
        var clean = value.Replace("\0", string.Empty).Trim();
        return clean.Length <= maximum ? clean : clean[..maximum];
    }

    private static WebsiteContentDocument Clone(WebsiteContentDocument source)
    {
        var json = JsonSerializer.Serialize(source, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return JsonSerializer.Deserialize<WebsiteContentDocument>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? new WebsiteContentDocument();
    }
}
