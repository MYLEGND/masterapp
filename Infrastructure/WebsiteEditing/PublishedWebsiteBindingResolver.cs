using System.Text.Json;
using Domain.Entities;
using Shared.Analytics;

namespace Infrastructure.WebsiteEditing;

public sealed record PublishedWebsiteBindingResolution(
    WebsiteSignalBinding Binding,
    string ElementId,
    string? FieldKey);

/// <summary>
/// Re-resolves browser-supplied binding identity against the immutable published
/// website version. Browser payloads may reference a binding; they never define it.
/// </summary>
public static class PublishedWebsiteBindingResolver
{
    public static PublishedWebsiteBindingResolution? Resolve(
        WebsiteContentVersion? version,
        string siteKey,
        string? path,
        string? bindingId)
    {
        if (version is null || string.IsNullOrWhiteSpace(bindingId))
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

        var route = NormalizeRoute(siteKey, path);
        var matches = new List<PublishedWebsiteBindingResolution>();

        if (document.LegacyMigration is { } legacy)
        {
            var page = legacy.Pages.FirstOrDefault(pair =>
                string.Equals(NormalizeRoute(siteKey, pair.Key), route, StringComparison.OrdinalIgnoreCase)).Value;
            if (page is null) return null;
            foreach (var (elementId, element) in page.Elements)
                Add(matches, elementId, null, element.Signals, bindingId);
            foreach (var extra in page.Extras)
                Add(matches, "extra:" + extra.Id, null, extra.Signals, bindingId);
        }
        else
        {
            var page = document.Pages.FirstOrDefault(pair =>
                string.Equals(NormalizeRoute(siteKey, pair.Key), route, StringComparison.OrdinalIgnoreCase)).Value;
            if (page is null) return null;
            Walk(page.Composition, node =>
            {
                Add(matches, node.Id, null, node.Signals, bindingId);
                foreach (var (fieldKey, bindings) in node.FieldSignals ?? new())
                    Add(matches, node.Id, fieldKey, bindings, bindingId);
            });
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    public static bool ClaimsConfiguredBinding(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            var root = document.RootElement;
            return root.TryGetProperty("configuredWebsiteSignal", out var configured) &&
                       configured.ValueKind == JsonValueKind.True ||
                   root.TryGetProperty("configuredSignalBindings", out var bindings) &&
                       bindings.ValueKind == JsonValueKind.Array && bindings.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void Add(
        List<PublishedWebsiteBindingResolution> matches,
        string elementId,
        string? fieldKey,
        IEnumerable<WebsiteSignalBinding>? bindings,
        string bindingId)
    {
        foreach (var binding in bindings ?? [])
            if (string.Equals(binding.Id, bindingId, StringComparison.Ordinal))
                matches.Add(new(binding, elementId, fieldKey));
    }

    private static void Walk(
        IEnumerable<WebsiteCompositionNode>? nodes,
        Action<WebsiteCompositionNode> visit)
    {
        foreach (var node in nodes ?? [])
        {
            visit(node);
            Walk(node.Children, visit);
        }
    }

    private static string NormalizeRoute(string siteKey, string? value)
    {
        var route = string.IsNullOrWhiteSpace(value) ? "/" : value.Trim();
        if (siteKey == WebsiteEditorSiteKeys.Protect)
        {
            if (route.StartsWith("/a/", StringComparison.OrdinalIgnoreCase))
            {
                var segments = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
                route = segments.Length > 2 ? "/" + string.Join('/', segments.Skip(2)) : "/";
            }
            route = ProtectRouteCatalog.CanonicalPath(route);
        }

        route = route.Length > 1 ? route.TrimEnd('/') : route;
        return route.Length == 0 ? "/" : route;
    }
}
