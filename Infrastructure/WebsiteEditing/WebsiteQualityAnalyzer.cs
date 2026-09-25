namespace Infrastructure.WebsiteEditing;

public sealed record WebsiteQualityIssue(
    string Code,
    string Severity,
    string Category,
    string Scope,
    string Message);

public sealed record WebsiteQualityReport(
    IReadOnlyList<WebsiteQualityIssue> Issues,
    int ErrorCount,
    int WarningCount,
    int InfoCount)
{
    public bool HasBlockingIssues => ErrorCount > 0;
}

public static class WebsiteQualityAnalyzer
{
    private const int RecommendedTitleLength = 60;
    private const int RecommendedDescriptionLength = 160;
    private const int DensePageComponentCount = 150;
    private const int DensePageMotionCount = 16;

    public static WebsiteQualityReport Analyze(WebsiteContentDocument? document)
    {
        document ??= new WebsiteContentDocument();
        var issues = new List<WebsiteQualityIssue>();

        var pages = document.Pages ?? new(StringComparer.Ordinal);
        if (pages.Count == 0)
        {
            issues.Add(new(
                "seo_page_metadata_not_initialized",
                "info",
                "SEO",
                "/",
                "Open this page in Studio and save a title and search description so its SEO metadata is explicit."));
        }
        else
        {
            AnalyzePageMetadata(pages, issues);
            foreach (var pair in pages)
                AnalyzeComponents(pair.Value?.Extras, pair.Key, issues);
        }

        AnalyzeComponents(document.Extras, "Legacy/global content", issues);

        foreach (var definition in (document.ReusableComponents ?? new(StringComparer.Ordinal)).Values)
        {
            if (definition is null) continue;
            var scope = $"Synced · {Clean(definition.Name, 80) ?? definition.Id}";
            AnalyzeComponents(definition.Components, scope, issues);
            AnalyzeMotion(definition.Interactions, scope, issues);
        }

        var ordered = issues
            .OrderBy(issue => SeverityRank(issue.Severity))
            .ThenBy(issue => issue.Category, StringComparer.Ordinal)
            .ThenBy(issue => issue.Scope, StringComparer.Ordinal)
            .ThenBy(issue => issue.Code, StringComparer.Ordinal)
            .ToArray();

        return new WebsiteQualityReport(
            ordered,
            ordered.Count(issue => issue.Severity == "error"),
            ordered.Count(issue => issue.Severity == "warning"),
            ordered.Count(issue => issue.Severity == "info"));
    }

    private static void AnalyzePageMetadata(
        Dictionary<string, WebsitePageDocument> pages,
        List<WebsiteQualityIssue> issues)
    {
        var titles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var descriptions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in pages)
        {
            var path = pair.Key;
            var page = pair.Value ?? new WebsitePageDocument();
            var title = Clean(page.Title, 500);
            var description = Clean(page.Description, 1000);

            if (title is null)
                Add(issues, "seo_title_missing", "warning", "SEO", path, "Add a unique page title for search results and browser tabs.");
            else
            {
                if (title.Length > RecommendedTitleLength)
                    Add(issues, "seo_title_long", "info", "SEO", path, $"Page title is {title.Length} characters; review how it truncates in search results.");
                titles.TryAdd(title, []);
                titles[title].Add(path);
            }

            if (description is null)
                Add(issues, "seo_description_missing", "warning", "SEO", path, "Add a concise search description for this page.");
            else
            {
                if (description.Length > RecommendedDescriptionLength)
                    Add(issues, "seo_description_long", "info", "SEO", path, $"Search description is {description.Length} characters; review how it truncates in search results.");
                descriptions.TryAdd(description, []);
                descriptions[description].Add(path);
            }

            var components = page.Extras?.Count ?? 0;
            if (components > DensePageComponentCount)
                Add(issues, "performance_dense_page", "warning", "Performance", path, $"This page contains {components} added components. Review load cost and whether sections can be simplified.");

            var motionCount = (page.Extras ?? [])
                .Sum(extra => extra?.Interactions?.Count ?? 0) +
                (page.Elements ?? new(StringComparer.Ordinal))
                .Values.Sum(element => element?.Interactions?.Count ?? 0);
            if (motionCount > DensePageMotionCount)
                Add(issues, "motion_dense_page", "warning", "Accessibility", path, $"This page contains {motionCount} motion interactions. Review motion intensity and page performance.");
        }

        foreach (var duplicate in titles.Where(pair => pair.Value.Count > 1))
            foreach (var path in duplicate.Value)
                Add(issues, "seo_title_duplicate", "warning", "SEO", path, $"This title is also used by {duplicate.Value.Count - 1} other page(s). Use a unique page title.");

        foreach (var duplicate in descriptions.Where(pair => pair.Value.Count > 1))
            foreach (var path in duplicate.Value)
                Add(issues, "seo_description_duplicate", "info", "SEO", path, "This search description is reused on another page. Unique descriptions help distinguish pages.");
    }

    private static void AnalyzeComponents(
        IEnumerable<WebsiteExtraComponent>? extras,
        string scope,
        List<WebsiteQualityIssue> issues)
    {
        foreach (var extra in extras ?? [])
        {
            if (extra is null) continue;
            var componentScope = $"{scope} · {Clean(extra.EditorLabel, 80) ?? extra.Type}";

            if (extra.Type == "image" &&
                extra.IsDecorative != true &&
                string.IsNullOrWhiteSpace(extra.Alt))
            {
                Add(issues, "a11y_image_alt_missing", "warning", "Accessibility", componentScope,
                    "Add image description text or mark this image decorative.");
            }

            if (extra.Type is "button" &&
                string.IsNullOrWhiteSpace(extra.Text))
            {
                Add(issues, "a11y_action_name_missing", "warning", "Accessibility", componentScope,
                    "Give this button/link visible text so its purpose is clear.");
            }

            if (extra.Type is "heading" &&
                string.IsNullOrWhiteSpace(extra.Text))
            {
                Add(issues, "a11y_heading_empty", "warning", "Accessibility", componentScope,
                    "Remove this empty heading or add meaningful heading text.");
            }

            if (extra.Type == "code")
            {
                Add(issues, "performance_custom_code_review", "info", "Performance", componentScope,
                    "Custom code runs in an isolated sandbox. Review its accessibility, network requests, and performance before publishing.");
            }

            if (extra.Type == "video")
            {
                Add(issues, "performance_video_review", "info", "Performance", componentScope,
                    "Review video file size, poster/preview behavior, captions, and mobile loading before publishing.");
            }

            AnalyzeMotion(extra.Interactions, componentScope, issues);
        }
    }

    private static void AnalyzeMotion(
        IEnumerable<WebsiteMotionInteraction>? interactions,
        string scope,
        List<WebsiteQualityIssue> issues)
    {
        var items = (interactions ?? []).ToArray();
        if (items.Length >= WebsiteMotionCatalog.MaxInteractionsPerElement)
            Add(issues, "motion_many_on_element", "info", "Accessibility", scope,
                $"This element uses {items.Length} motion interactions. Confirm they remain understandable with Reduced Motion enabled.");

        if (items.Any(item => item?.DurationMs > 2000))
            Add(issues, "motion_long_duration", "info", "Accessibility", scope,
                "One or more animations run longer than two seconds. Confirm the motion is intentional and not distracting.");
    }

    private static void Add(
        ICollection<WebsiteQualityIssue> issues,
        string code,
        string severity,
        string category,
        string scope,
        string message) =>
        issues.Add(new WebsiteQualityIssue(code, severity, category, scope, message));

    private static int SeverityRank(string severity) => severity switch
    {
        "error" => 0,
        "warning" => 1,
        _ => 2
    };

    private static string? Clean(string? value, int max)
    {
        var clean = (value ?? string.Empty).Trim();
        if (clean.Length == 0) return null;
        return clean.Length <= max ? clean : clean[..max];
    }
}
