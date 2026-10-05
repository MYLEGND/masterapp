namespace Infrastructure.WebsiteEditing;

public sealed record WebsiteConversionPathSummary(
    string PagePath,
    IReadOnlyList<string> ActionKeys,
    int ProtectedFormCount,
    int LeadCaptureExperienceCount,
    int TotalConversionPoints,
    bool HasEarlyConversionPoint);

public sealed record WebsiteDesignQualityReport(
    DateTime CheckedUtc,
    IReadOnlyList<WebsiteQualityCheck> Checks,
    IReadOnlyList<WebsiteConversionPathSummary> ConversionPaths)
{
    public int ErrorCount => Checks.Count(value => value.Severity == "error");
    public int WarningCount => Checks.Count(value => value.Severity == "warning");
}

public static class WebsiteDesignQualityInspector
{
    private static readonly HashSet<string> PlaceholderCopy = new(StringComparer.OrdinalIgnoreCase)
    {
        "Your text",
        "Button",
        "A sharper way forward.",
        "Designed around what matters most.",
        "Everything important, without the clutter.",
        "A better experience from the first interaction.",
        "A simple path forward.",
        "Trusted when it matters.",
        "Questions, answered clearly.",
        "Ready for the next step?",
        "Make the next step obvious."
    };

    public static WebsiteDesignQualityReport Inspect(
        WebsiteContentDocument document,
        WebsiteCapabilityManifest capabilities)
    {
        var checks = new List<WebsiteQualityCheck>();
        var conversions = new List<WebsiteConversionPathSummary>();

        InspectTheme(document.Theme, checks);

        foreach (var (path, page) in document.Pages.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            if (page.Navigation?.IsDeleted == true) continue;
            var nodes = Flatten(page.Composition).Where(value => value.Hidden != true).ToArray();
            var headings = nodes.Where(value => value.Type == "heading").ToArray();
            var h1Count = headings.Count(value => string.Equals(value.Tag, "h1", StringComparison.OrdinalIgnoreCase));
            if (h1Count == 0)
                checks.Add(new("design_h1_missing", "warning", "This page has no visible H1.", path));
            else if (h1Count > 1)
                checks.Add(new("design_h1_multiple", "warning", $"This page has {h1Count} visible H1 headings.", path));

            InspectHeadingOrder(headings, path, checks);

            foreach (var section in page.Composition.Where(value => value.Type == "section" && value.Hidden != true))
            {
                if (!ContainsMeaningfulContent(section))
                    checks.Add(new("design_empty_section", "warning", "Remove or complete this empty section.", path, section.Id));
            }

            foreach (var node in nodes)
            {
                if (!string.IsNullOrWhiteSpace(node.Text) && PlaceholderCopy.Contains(node.Text.Trim()))
                    checks.Add(new("design_placeholder_copy", "warning", "Replace starter copy with business-specific content.", path, node.Id));

                if (node.Type == "heading" && !string.IsNullOrWhiteSpace(node.Text) && node.Text.Length > 110)
                    checks.Add(new("design_heading_too_long", "info", "This heading is unusually long and may lose visual impact.", path, node.Id));

                if (node.Type == "text" && !string.IsNullOrWhiteSpace(node.Text) && node.Text.Length > 700)
                    checks.Add(new("design_copy_density", "info", "This text block is dense; consider splitting it for scanning.", path, node.Id));
            }

            var animationCount = nodes.Sum(value => value.Animations?.Count ?? 0);
            if (animationCount > 18)
                checks.Add(new("design_animation_excess", "warning", $"This page has {animationCount} animation bindings. Reduce motion to keep attention focused.", path));

            var styled = nodes.Count(HasStrongNodeOverride);
            if (nodes.Length >= 8 && styled > Math.Max(6, (int)Math.Ceiling(nodes.Length * 0.45m)))
                checks.Add(new("design_override_drift", "warning",
                    "Many nodes carry direct visual overrides. Move repeated decisions into semantic theme/shared component treatment.",
                    path));

            var actionKeys = nodes
                .Where(value => value.Type is "cta" or "link" && !string.IsNullOrWhiteSpace(value.ActionKey))
                .Select(value => value.ActionKey!)
                .ToArray();
            foreach (var group in actionKeys.GroupBy(value => value, StringComparer.Ordinal).Where(group => group.Count() > 4))
                checks.Add(new("design_repeated_cta", "info",
                    $"Action '{group.Key}' appears {group.Count()} times on this page. Confirm each repetition supports the conversion journey.",
                    path));

            var protectedForms = nodes.Count(value =>
                value.Type == "form" && string.Equals(value.SystemKey, "canonical_inquiry", StringComparison.Ordinal) ||
                WebsiteSystemTemplateAuthority.IsRuntimeFormSystemKey(value.SystemKey));
            var leadCapture = nodes.Count(value =>
                value.Type == "experience" &&
                string.Equals(value.Experience?.SubmitCapability, WebsiteExperiencePolicy.LeadCaptureCapability, StringComparison.Ordinal));
            var conversionNodes = nodes.Where(value =>
                value.Type is "cta" or "link" && !string.IsNullOrWhiteSpace(value.ActionKey) ||
                value.Type == "form" ||
                value.Type == "experience" && value.Experience?.SubmitCapability is not null).ToArray();

            var firstSectionIds = page.Composition
                .Where(value => value.Hidden != true)
                .Take(2)
                .Select(value => value.Id)
                .ToHashSet(StringComparer.Ordinal);
            var early = conversionNodes.Any(value => IsWithinRoots(value.Id, page.Composition, firstSectionIds));

            conversions.Add(new(
                path,
                actionKeys.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                protectedForms,
                leadCapture,
                conversionNodes.Length,
                early));

            if (path == "/" && conversionNodes.Length == 0)
                checks.Add(new("conversion_home_missing", "warning",
                    "The home page has no approved action, protected form, or native lead-capture experience.", path));
            else if (conversionNodes.Length > 0 && !early)
                checks.Add(new("conversion_late_only", "info",
                    "Conversion points appear only deeper in the page. Confirm the first decision moment has a clear next step.", path));
        }

        return new(DateTime.UtcNow, checks, conversions);
    }

    private static void InspectTheme(WebsiteDesignTheme theme, List<WebsiteQualityCheck> checks)
    {
        var semantic = new object?[]
        {
            theme.DisplaySize, theme.H1Size, theme.H2Size, theme.H3Size,
            theme.BodySize, theme.SectionSpace, theme.ContentMaxWidth,
            theme.Gutter, theme.CardRadius, theme.ButtonRadius
        };
        if (semantic.Count(value => value is not null) < 6)
            checks.Add(new("design_theme_shallow", "info",
                "The site uses only part of the semantic theme system. Define typography, rhythm, width, and component tokens to reduce node-level drift."));
    }

    private static void InspectHeadingOrder(
        IReadOnlyList<WebsiteCompositionNode> headings,
        string path,
        List<WebsiteQualityCheck> checks)
    {
        int? previous = null;
        foreach (var heading in headings)
        {
            if (heading.Tag is null || heading.Tag.Length != 2 || heading.Tag[0] is not ('h' or 'H') ||
                !int.TryParse(heading.Tag[1].ToString(), out var level))
                continue;
            if (previous.HasValue && level > previous.Value + 1)
                checks.Add(new("design_heading_skip", "info",
                    $"Heading hierarchy jumps from H{previous.Value} to H{level}.", path, heading.Id));
            previous = level;
        }
    }

    private static bool ContainsMeaningfulContent(WebsiteCompositionNode node)
    {
        if (node.Hidden == true) return false;
        if (node.Type is "image" or "video" or "form" or "experience" or "cta" or "link") return true;
        if (!string.IsNullOrWhiteSpace(node.Text) || !string.IsNullOrWhiteSpace(node.Title)) return true;
        if (!string.IsNullOrWhiteSpace(node.SystemKey)) return true;
        return node.Children.Any(ContainsMeaningfulContent);
    }

    private static bool HasStrongNodeOverride(WebsiteCompositionNode node)
    {
        var style = node.Style;
        return !string.IsNullOrWhiteSpace(style.Color) ||
               !string.IsNullOrWhiteSpace(style.BackgroundColor) ||
               !string.IsNullOrWhiteSpace(style.BackgroundGradient) ||
               !string.IsNullOrWhiteSpace(style.FontFamily) ||
               style.FontSize.HasValue ||
               style.BorderRadius.HasValue ||
               !string.IsNullOrWhiteSpace(style.BoxShadow);
    }

    private static IEnumerable<WebsiteCompositionNode> Flatten(IEnumerable<WebsiteCompositionNode> roots)
    {
        foreach (var node in roots ?? [])
        {
            yield return node;
            foreach (var child in Flatten(node.Children))
                yield return child;
        }
    }

    private static bool IsWithinRoots(
        string nodeId,
        IReadOnlyList<WebsiteCompositionNode> roots,
        IReadOnlySet<string> rootIds)
    {
        foreach (var root in roots)
        {
            if (!rootIds.Contains(root.Id)) continue;
            if (ContainsId(root, nodeId)) return true;
        }
        return false;
    }

    private static bool ContainsId(WebsiteCompositionNode node, string id) =>
        string.Equals(node.Id, id, StringComparison.Ordinal) || node.Children.Any(child => ContainsId(child, id));
}
