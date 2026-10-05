using System.Text.Json;

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
        "Make the next step obvious.",
        "Built for what comes next",
        "Clear value, confident presentation, and one obvious next step.",
        "Proof that earns attention.",
        "Focused evidence, presented without noise.",
        "Trusted expertise",
        "Responsive service",
        "Built around results",
        "What we do",
        "A focused set of services with a clear path forward.",
        "Focused expertise",
        "Premium execution",
        "Clear next steps",
        "Core advantage",
        "Fast and focused",
        "Built to scale",
        "Clear by design",
        "Why it works",
        "Purposeful hierarchy keeps attention moving toward the right decision.",
        "01 · Start with what matters",
        "02 · Get a clear recommendation",
        "03 · Move forward confidently",
        "What clients say",
        "What should I expect?",
        "A clear process, direct communication, and a focused next step.",
        "How do we get started?",
        "Use the primary action on this page and we’ll take it from there.",
        "What makes this different?",
        "The experience is built around clarity, fit, and real outcomes—not unnecessary complexity.",
        "Start here",
        "Tell us what you need and we’ll follow up with a clear path forward.",
        "Your next move",
        "A strong close removes friction and gives the visitor one clear action."
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
                .Where(value => (value.Type is "cta" or "link") && !string.IsNullOrWhiteSpace(value.ActionKey))
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
                ((value.Type is "cta" or "link") && !string.IsNullOrWhiteSpace(value.ActionKey)) ||
                value.Type == "form" ||
                (value.Type == "experience" && value.Experience?.SubmitCapability is not null)).ToArray();

            var firstSectionIds = page.Composition
                .Where(value => value.Hidden != true)
                .Take(2)
                .Select(value => value.Id)
                .ToHashSet(StringComparer.Ordinal);
            var early = conversionNodes.Any(value => IsWithinRoots(value.Id, page.Composition, firstSectionIds));

            var distinctActions = actionKeys
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();

            conversions.Add(new(
                path,
                distinctActions,
                protectedForms,
                leadCapture,
                conversionNodes.Length,
                early));

            if (protectedForms > 1)
                checks.Add(new("conversion_duplicate_forms", "warning",
                    $"This page contains {protectedForms} protected forms. Keep one clear primary form unless the journey explicitly requires more.", path));

            if ((actionKeys.Contains("form_start", StringComparer.Ordinal) ||
                 actionKeys.Contains("submit", StringComparer.Ordinal)) &&
                protectedForms == 0 &&
                leadCapture == 0)
                checks.Add(new("conversion_orphan_form_action", "warning",
                    "This page uses a form-start or submit action without a canonical form or lead-capture experience on the page.", path));

            var contactLike =
                string.Equals(path.TrimEnd('/'), "/contact", StringComparison.OrdinalIgnoreCase) ||
                (page.Navigation?.Label?.Contains("contact", StringComparison.OrdinalIgnoreCase) ?? false);
            var inquiryAvailable = capabilities.Capabilities.Any(value =>
                string.Equals(value.Key, "contact.inquiry.submit", StringComparison.Ordinal));
            if (contactLike && inquiryAvailable && protectedForms == 0 && leadCapture == 0)
                checks.Add(new("conversion_contact_capture_missing", "warning",
                    "This Contact page has no canonical inquiry form or native lead-capture experience.", path));

            if (distinctActions.Length > 3)
                checks.Add(new("conversion_competing_actions", "info",
                    $"This page presents {distinctActions.Length} different canonical actions. Confirm the hierarchy still makes one primary next step obvious.", path));

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
        return node.Children?.Any(ContainsMeaningfulContent) == true;
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
            foreach (var child in Flatten(node.Children ?? []))
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
        string.Equals(node.Id, id, StringComparison.Ordinal) || (node.Children?.Any(child => ContainsId(child, id)) ?? false);
}

public sealed record WebsiteSafeQualityRepair(
    string Code,
    string PagePath,
    string ElementId,
    string Description,
    WebsiteMutationOperation Operation);

public sealed record WebsiteSafeQualityRepairPlan(
    string Schema,
    IReadOnlyList<WebsiteSafeQualityRepair> Repairs)
{
    public IReadOnlyList<WebsiteMutationOperation> Operations =>
        Repairs.Select(value => value.Operation).ToArray();
}

public static class WebsiteDesignQualityRepairPlanner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static WebsiteSafeQualityRepairPlan Plan(WebsiteContentDocument document)
    {
        var repairs = new List<WebsiteSafeQualityRepair>();

        foreach (var (path, page) in document.Pages.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            if (page.Navigation?.IsDeleted == true || page.SystemTemplateKey is not null)
                continue;

            var headings = new List<WebsiteCompositionNode>();
            CollectSafeHeadings(page.Composition, protectedAncestor: false, headings);
            if (headings.Count == 0) continue;

            var h1 = headings.Where(value => string.Equals(value.Tag, "h1", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (h1.Length == 0)
            {
                AddTagRepair(repairs, path, headings[0], "h1", "design_h1_missing",
                    "Promote the first safe page heading to H1.");
            }
            else if (h1.Length > 1)
            {
                foreach (var duplicate in h1.Skip(1))
                    AddTagRepair(repairs, path, duplicate, "h2", "design_h1_multiple",
                        "Demote an additional H1 to H2 while preserving its content and presentation.");
            }

            var effectiveLevels = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var heading in headings)
            {
                var tag = repairs.LastOrDefault(value => value.ElementId == heading.Id)?.Operation.Node?.Tag ?? heading.Tag;
                if (!TryHeadingLevel(tag, out var level)) continue;
                effectiveLevels[heading.Id] = level;
            }

            int? previous = null;
            foreach (var heading in headings)
            {
                if (!effectiveLevels.TryGetValue(heading.Id, out var level)) continue;
                if (previous.HasValue && level > previous.Value + 1)
                {
                    var corrected = previous.Value + 1;
                    AddTagRepair(repairs, path, heading, "h" + corrected, "design_heading_skip",
                        $"Normalize heading hierarchy to H{corrected}.");
                    effectiveLevels[heading.Id] = corrected;
                    level = corrected;
                }
                previous = level;
            }
        }

        var collapsed = repairs
            .GroupBy(value => value.ElementId, StringComparer.Ordinal)
            .Select(group =>
            {
                var final = group.Last();
                return final with
                {
                    Code = string.Join("+", group.Select(value => value.Code).Distinct(StringComparer.Ordinal)),
                    Description = string.Join(" ", group.Select(value => value.Description).Distinct(StringComparer.Ordinal))
                };
            })
            .ToArray();

        return new("legend-design-safe-repairs/v1", collapsed);
    }

    private static void AddTagRepair(
        List<WebsiteSafeQualityRepair> repairs,
        string pagePath,
        WebsiteCompositionNode node,
        string tag,
        string code,
        string description)
    {
        var current = repairs.LastOrDefault(value => value.ElementId == node.Id)?.Operation.Node ?? Clone(node);
        current.Tag = tag;
        repairs.Add(new(
            code,
            pagePath,
            node.Id,
            description,
            new WebsiteMutationOperation
            {
                Type = "replaceNode",
                NodeId = node.Id,
                ExpectedFingerprint = WebsiteCreativeFingerprint.Node(node),
                Node = current
            }));
    }

    private static void CollectSafeHeadings(
        IEnumerable<WebsiteCompositionNode>? nodes,
        bool protectedAncestor,
        List<WebsiteCompositionNode> result)
    {
        foreach (var node in nodes ?? [])
        {
            var protectedHere = protectedAncestor ||
                node.Type == "form" ||
                !string.IsNullOrWhiteSpace(node.SystemKey) ||
                !string.IsNullOrWhiteSpace(node.SystemBinding) ||
                (node.Signals?.Count ?? 0) > 0 ||
                (node.FieldSignals?.Values.Any(value => value?.Count > 0) ?? false);

            if (!protectedHere && node.Hidden != true && node.Type == "heading" && TryHeadingLevel(node.Tag, out _))
                result.Add(node);

            CollectSafeHeadings(node.Children, protectedHere, result);
        }
    }

    private static bool TryHeadingLevel(string? tag, out int level)
    {
        level = 0;
        return tag is { Length: 2 } &&
               (tag[0] is 'h' or 'H') &&
               int.TryParse(tag[1].ToString(), out level) &&
               level is >= 1 and <= 6;
    }

    private static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(
            JsonSerializer.Serialize(value, JsonOptions),
            JsonOptions)!;
}
