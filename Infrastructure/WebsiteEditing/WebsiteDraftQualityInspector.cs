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
        if (document.LegacyMigration is not null)
        {
            InspectLegacy(document.LegacyMigration, checks);
            return new WebsiteQualityReport(DateTime.UtcNow, checks);
        }

        var reusableSyncIds = document.ReusableComponents.Keys.ToHashSet(StringComparer.Ordinal);
        InspectComposition(document.Shell.Header, checks, "@shell/header", reusableSyncIds);
        InspectComposition(document.Shell.Footer, checks, "@shell/footer", reusableSyncIds);

        foreach (var (id, definition) in document.ReusableComponents)
            InspectComposition(definition.Composition, checks, "@component/" + id, reusableSyncIds);

        foreach (var (path, page) in document.Pages)
        {
            if (page.Navigation?.IsDeleted == true) continue;
            if (string.IsNullOrWhiteSpace(page.Title))
                checks.Add(new("page_title_missing", "warning", "Add a page title for browser tabs and search results.", path));
            if (string.IsNullOrWhiteSpace(page.Description))
                checks.Add(new("page_description_missing", "info", "Add a search description for this page.", path));
            if (page.Navigation?.ShowInNavigation == true && string.IsNullOrWhiteSpace(page.Navigation.Label))
                checks.Add(new("navigation_label_missing", "warning", "Visible navigation pages need a navigation label.", path));
            if (page.DynamicBinding is not null && !document.Collections.ContainsKey(page.DynamicBinding.CollectionId))
                checks.Add(new("dynamic_collection_missing", "error", "This dynamic page points to a collection that is not available.", path));
            InspectComposition(page.Composition, checks, path, reusableSyncIds);
        }

        InspectDataBindings(document, checks);
        return new WebsiteQualityReport(DateTime.UtcNow, checks);
    }

    private static void InspectDataBindings(WebsiteContentDocument document, List<WebsiteQualityCheck> checks)
    {
        var collections = document.Collections;
        void Check(WebsiteDataBinding? binding, string pagePath, string elementId)
        {
            if (binding is null) return;
            if (!collections.TryGetValue(binding.CollectionId, out var collection))
            {
                checks.Add(new("data_collection_missing", "error", "This content binding points to a collection that is not available.", pagePath, elementId));
                return;
            }
            if (!collection.Fields.Contains(binding.Field, StringComparer.Ordinal))
                checks.Add(new("data_field_missing", "error", "This content binding points to a field that is not exposed by its collection.", pagePath, elementId));
        }

        void Composition(IEnumerable<WebsiteCompositionNode> nodes, string scope)
        {
            foreach (var node in nodes ?? [])
            {
                Check(node.DataBinding, scope, node.Id);
                Composition(node.Children, scope);
            }
        }

        Composition(document.Shell.Header, "@shell/header");
        Composition(document.Shell.Footer, "@shell/footer");
        foreach (var (id, definition) in document.ReusableComponents)
            Composition(definition.Composition, "@component/" + id);

        foreach (var (path, page) in document.Pages)
        {
            if (page.Navigation?.IsDeleted == true) continue;
            Composition(page.Composition, path);

            if (page.DynamicBinding is null) continue;
            if (!collections.TryGetValue(page.DynamicBinding.CollectionId, out var dynamicCollection))
                continue;
            if (!dynamicCollection.Fields.Contains(page.DynamicBinding.ItemKeyField, StringComparer.Ordinal))
                checks.Add(new("dynamic_item_key_missing", "error", "The dynamic page key field is not exposed by its collection.", path));
            if (!WebsiteCollectionSourcePolicy.TryGet(dynamicCollection.Source, out var source) || !source.IsList)
                checks.Add(new("dynamic_collection_not_list", "error", "Dynamic pages require a list collection.", path));
            if (string.IsNullOrWhiteSpace(page.DynamicBinding.RoutePattern))
                checks.Add(new("dynamic_route_pattern_missing", "error", "Dynamic pages need a route pattern such as /products/{item}.", path));
        }
    }

    private static void InspectComposition(
        IEnumerable<WebsiteCompositionNode>? nodes,
        List<WebsiteQualityCheck> checks,
        string scope,
        IReadOnlySet<string> reusableSyncIds)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Visit(IEnumerable<WebsiteCompositionNode>? values)
        {
            foreach (var node in values ?? [])
            {
                if (!seen.Add(node.Id))
                    checks.Add(new("duplicate_node_id", "error", "Two website components share the same stable identity.", scope, node.Id));

                if (node.Hidden != true)
                {
                    if (node.Type == "image" && node.Alt is null)
                        checks.Add(new("image_alt_missing", "warning", "Add alternative text for this image.", scope, node.Id));

                    if (node.Type is "cta" or "link" &&
                        string.IsNullOrWhiteSpace(node.ActionKey) &&
                        !(node.DataBinding is not null && string.Equals(node.DataBinding.Target, "href", StringComparison.Ordinal)) &&
                        (string.IsNullOrWhiteSpace(node.Href) || node.Href == "#"))
                        checks.Add(new("link_destination_missing", node.Type == "cta" ? "error" : "warning",
                            node.Type == "cta" ? "CTAs need a canonical action or working destination." : "Choose a working destination for this link.",
                            scope, node.Id));

                    if (node.Type == "form" &&
                        !string.Equals(node.SystemKey, "canonical_inquiry", StringComparison.Ordinal))
                        checks.Add(new("form_authority_invalid", "error", "Website forms must use the canonical inquiry authority.", scope, node.Id));

                    if (node.Type == "reusable" &&
                        (string.IsNullOrWhiteSpace(node.SyncSourceId) || !reusableSyncIds.Contains(node.SyncSourceId)))
                        checks.Add(new("reusable_component_missing", "error", "Reusable component instances must reference an existing synchronized definition.", scope, node.Id));
                }

                Visit(node.Children);
            }
        }

        Visit(nodes);
    }

    private static void InspectLegacy(LegacyWebsiteContentDocument legacy, List<WebsiteQualityCheck> checks)
    {
        checks.Add(new(
            "legacy_materialization_required",
            "warning",
            "This draft is pre-v3 and is read-only until Website Studio materializes it into the canonical composition graph."));

        foreach (var (path, page) in legacy.Pages)
        {
            if (page.Navigation?.IsDeleted == true) continue;
            if (string.IsNullOrWhiteSpace(page.Title))
                checks.Add(new("page_title_missing", "warning", "Add a page title for browser tabs and search results.", path));
            InspectLegacyElements(page.Elements, page.Extras, checks, path);
        }
        InspectLegacyElements(legacy.Elements, legacy.Extras, checks, "@legacy-shell");
    }

    private static void InspectLegacyElements(
        IDictionary<string, LegacyWebsiteElementRecord>? elements,
        IEnumerable<LegacyWebsiteExtraComponent>? extras,
        List<WebsiteQualityCheck> checks,
        string scope)
    {
        foreach (var (id, value) in elements ?? new Dictionary<string, LegacyWebsiteElementRecord>())
        {
            if (value.Hidden == true) continue;
            if (!string.IsNullOrWhiteSpace(value.ImageDataUrl) && value.Alt is null)
                checks.Add(new("image_alt_missing", "warning", "Add alternative text for this image.", scope, id));
            if (!string.IsNullOrWhiteSpace(value.Href) && value.Href == "#")
                checks.Add(new("link_destination_missing", "warning", "Choose a working destination for this link.", scope, id));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var extra in extras ?? [])
        {
            if (!ids.Add(extra.Id))
                checks.Add(new("duplicate_extra_id", "error", "Two legacy blocks share the same identity.", scope, "extra:" + extra.Id));
            if (extra.Type == "image" && extra.Alt is null)
                checks.Add(new("image_alt_missing", "warning", "Add alternative text for this image.", scope, "extra:" + extra.Id));
            if (extra.Type == "button" && string.IsNullOrWhiteSpace(extra.ActionKey) && string.IsNullOrWhiteSpace(extra.Href))
                checks.Add(new("button_destination_missing", "error", "Legacy buttons need a working action or destination before migration.", scope, "extra:" + extra.Id));
        }
    }
}
