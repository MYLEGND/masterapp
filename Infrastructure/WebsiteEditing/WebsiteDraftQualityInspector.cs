namespace Infrastructure.WebsiteEditing;

public sealed record WebsiteQualityCheck(
    string Code,
    string Severity,
    string Message,
    string? PagePath = null,
    string? ElementId = null);

public sealed record WebsiteQualityReport(
    DateTime CheckedUtc,
    IReadOnlyList<WebsiteQualityCheck> Checks)
{
    public int ErrorCount => Checks.Count(x => x.Severity == "error");
    public int WarningCount => Checks.Count(x => x.Severity == "warning");
}

/// <summary>
/// Server-side quality inspection of the persisted website draft. This is diagnostic
/// evidence only; publish authorization remains owned by the existing publish pipeline.
/// </summary>
public static class WebsiteDraftQualityInspector
{
    public static WebsiteQualityReport Inspect(WebsiteContentDocument document)
    {
        var checks = new List<WebsiteQualityCheck>();
        foreach (var (path, page) in document.Pages ?? new Dictionary<string, WebsitePageDocument>())
        {
            if (page.Navigation?.IsDeleted == true) continue;
            if (string.IsNullOrWhiteSpace(page.Title))
                checks.Add(new("page_title_missing", "warning", "Add a page title for browser tabs and search results.", path));
            if (string.IsNullOrWhiteSpace(page.Description))
                checks.Add(new("page_description_missing", "info", "Add a search description for this page.", path));
            if (page.Navigation?.ShowInNavigation == true && string.IsNullOrWhiteSpace(page.Navigation.Label))
                checks.Add(new("navigation_label_missing", "warning", "Visible navigation pages need a navigation label.", path));
            if (page.DynamicBinding is not null && !(document.Collections?.ContainsKey(page.DynamicBinding.CollectionId) ?? false))
                checks.Add(new("dynamic_collection_missing", "error", "This dynamic page points to a collection that is not available.", path));
            InspectElements(page.Elements, page.Extras, checks, path);
        }
        InspectElements(document.Elements, document.Extras, checks, null);

        var reusableSyncIds = (document.ReusableComponents ?? new Dictionary<string, WebsiteReusableComponentDefinition>())
            .Values.SelectMany(component => component.Elements.Values.Select(x => x.SyncSourceId)
                .Concat(component.Extras.Select(x => x.SyncSourceId)))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var (path, page) in document.Pages ?? new Dictionary<string, WebsitePageDocument>())
        {
            foreach (var (id, value) in page.Elements)
                if (!string.IsNullOrWhiteSpace(value.SyncSourceId) && !reusableSyncIds.Contains(value.SyncSourceId))
                    checks.Add(new("sync_source_missing", "warning", "This block has a sync source that no reusable definition owns.", path, id));
            foreach (var extra in page.Extras)
                if (!string.IsNullOrWhiteSpace(extra.SyncSourceId) && !reusableSyncIds.Contains(extra.SyncSourceId))
                    checks.Add(new("sync_source_missing", "warning", "This added block has a sync source that no reusable definition owns.", path, "extra:" + extra.Id));
        }
        return new WebsiteQualityReport(DateTime.UtcNow, checks);
    }

    private static void InspectElements(
        IDictionary<string, WebsiteElementOverride>? elements,
        IEnumerable<WebsiteExtraComponent>? extras,
        List<WebsiteQualityCheck> checks,
        string? pagePath)
    {
        foreach (var (id, value) in elements ?? new Dictionary<string, WebsiteElementOverride>())
        {
            if (value.Hidden == true) continue;
            if (!string.IsNullOrWhiteSpace(value.ImageDataUrl) && string.IsNullOrWhiteSpace(value.Alt))
                checks.Add(new("image_alt_missing", "warning", "Add alternative text for this image.", pagePath, id));
            if (!string.IsNullOrWhiteSpace(value.Href) && value.Href == "#")
                checks.Add(new("link_destination_missing", "warning", "Choose a working destination for this link.", pagePath, id));
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var extra in extras ?? [])
        {
            if (!ids.Add(extra.Id))
                checks.Add(new("duplicate_extra_id", "error", "Two added blocks share the same identity.", pagePath, "extra:" + extra.Id));
            if (extra.Type == "image" && string.IsNullOrWhiteSpace(extra.Alt))
                checks.Add(new("image_alt_missing", "warning", "Add alternative text for this image.", pagePath, "extra:" + extra.Id));
            if (extra.Type == "button" && string.IsNullOrWhiteSpace(extra.ActionKey) && string.IsNullOrWhiteSpace(extra.Href))
                checks.Add(new("button_destination_missing", "error", "Added buttons need a working action or destination.", pagePath, "extra:" + extra.Id));
        }
    }
}
