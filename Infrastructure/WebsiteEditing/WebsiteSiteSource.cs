using System.Text.Json;
using System.Text.Json.Serialization;

namespace Infrastructure.WebsiteEditing;

public sealed class WebsiteSiteSourceDocument
{
    public string Schema { get; set; } = WebsiteSiteSource.Schema;
    public int Version { get; set; } = WebsiteStudioContract.CurrentDocumentVersion;
    public string? FaviconImageDataUrl { get; set; }
    public WebsiteStoreSettings Store { get; set; } = new();
    public List<WebsiteBreakpointDefinition> Breakpoints { get; set; } = WebsiteStudioContract.DefaultBreakpoints();
    public WebsiteDesignTheme Theme { get; set; } = new();

    public WebsiteSharedShellDocument Shell { get; set; } = new();
    public List<WebsiteSiteSourcePage> Pages { get; set; } = new();
    public SortedDictionary<string, WebsiteReusableComponentDefinition> ReusableComponents { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, WebsiteCollectionDefinition> Collections { get; set; } = new(StringComparer.Ordinal);
}

public sealed class WebsiteSiteSourcePage
{
    public string Path { get; set; } = "/";
    public string? Title { get; set; }
    public string? Description { get; set; }
    public WebsitePageNavigation Navigation { get; set; } = new();
    public WebsiteDynamicPageBinding? DynamicBinding { get; set; }
    public List<WebsiteCompositionNode> Composition { get; set; } = new();
}

public sealed record WebsiteSiteSourceParseResult(
    WebsiteContentDocument Document,
    IReadOnlyDictionary<string, WebsiteSiteSourceLocation> SourceMap);

public sealed record WebsiteSiteSourceLocation(int Line, string? PagePath, string NodeId);

/// <summary>
/// Deterministic source-code projection for WebsiteContentDocument v3.
///
/// The source is not a second persistence model. It is a reversible representation of
/// the existing draft graph. Provider/event bindings are intentionally omitted and are
/// restored from the current canonical document by stable node ID during parse.
/// </summary>
public static class WebsiteSiteSource
{
    public const string Schema = "legend-site-source/v1";
    public const int MaxSourceCharacters = 2_000_000;

    private static readonly JsonSerializerOptions SourceOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static string Serialize(WebsiteContentDocument document)
    {
        if (document.LegacyMigration is not null)
            throw new InvalidOperationException("website_site_source_requires_materialized_v3");
        var canonical = WebsiteContentSanitizer.Sanitize(document);
        var source = Project(canonical);
        return JsonSerializer.Serialize(source, SourceOptions) + "\n";
    }

    public static WebsiteSiteSourceParseResult Parse(
        string sourceText,
        WebsiteContentDocument baseline,
        IReadOnlyList<WebsiteCallToActionOption> ctaCatalog)
    {
        if (string.IsNullOrWhiteSpace(sourceText) || sourceText.Length > MaxSourceCharacters)
            throw new ArgumentException("LEGEND Site Source must contain 1-2,000,000 characters.");

        WebsiteSiteSourceDocument source;
        try
        {
            source = JsonSerializer.Deserialize<WebsiteSiteSourceDocument>(sourceText, SourceOptions)
                ?? throw new ArgumentException("LEGEND Site Source is empty.");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException(
                $"LEGEND Site Source syntax error at line {(ex.LineNumber ?? 0) + 1}, column {(ex.BytePositionInLine ?? 0) + 1}.",
                ex);
        }

        if (!string.Equals(source.Schema, Schema, StringComparison.Ordinal) ||
            source.Version != WebsiteStudioContract.CurrentDocumentVersion)
            throw new ArgumentException("LEGEND Site Source uses an unsupported schema or document version.");

        var current = WebsiteContentSanitizer.Sanitize(baseline);
        if ((source.Store?.Enabled == true) != current.Store.Enabled)
            throw new ArgumentException("Enable or remove the website store through the canonical Store authority, not Site Source.");

        var sourcePages = source.Pages ?? [];
        if (sourcePages.Count > 100) throw new ArgumentException("Website page limit exceeded.");

        var allowedActions = ctaCatalog
            .Select(option => option.Key)
            .ToHashSet(StringComparer.Ordinal);
        var protectedNodes = Flatten(current)
            .ToDictionary(entry => entry.Node.Id, entry => entry, StringComparer.Ordinal);

        var output = new WebsiteContentDocument
        {
            Version = WebsiteStudioContract.CurrentDocumentVersion,
            FaviconImageDataUrl = source.FaviconImageDataUrl ?? current.FaviconImageDataUrl,
            Store = source.Store ?? new WebsiteStoreSettings(),
            Breakpoints = source.Breakpoints ?? WebsiteStudioContract.DefaultBreakpoints(),
            Theme = source.Theme ?? new WebsiteDesignTheme(),
            Shell = Clone(source.Shell ?? new WebsiteSharedShellDocument()),
            Collections = new(
                (source.Collections ?? new(StringComparer.Ordinal))
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                StringComparer.Ordinal)
        };

        var pagePaths = new HashSet<string>(StringComparer.Ordinal);
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);

        ProtectSemantics(output.Shell.Header, "@shell/header", protectedNodes, allowedActions, nodeIds);
        ProtectSemantics(output.Shell.Footer, "@shell/footer", protectedNodes, allowedActions, nodeIds);

        output.ReusableComponents = ProtectReusableComponents(
            source.ReusableComponents,
            current.ReusableComponents,
            protectedNodes,
            allowedActions,
            nodeIds);

        foreach (var page in sourcePages)
        {
            var path = NormalizePagePath(page.Path)
                ?? throw new ArgumentException("LEGEND Site Source contains an invalid page path.");
            if (!pagePaths.Add(path))
                throw new ArgumentException($"LEGEND Site Source contains duplicate page path '{path}'.");

            current.Pages.TryGetValue(path, out var currentPage);
            var next = new WebsitePageDocument
            {
                Title = page.Title,
                Description = page.Description,
                Navigation = page.Navigation ?? new WebsitePageNavigation(),
                DynamicBinding = page.DynamicBinding ?? currentPage?.DynamicBinding,
                Composition = Clone(page.Composition ?? [])
            };

            ProtectSemantics(next.Composition, path, protectedNodes, allowedActions, nodeIds);
            output.Pages[path] = next;
        }

        if (!output.Pages.TryGetValue("/", out var home) || home.Navigation.IsDeleted)
            throw new ArgumentException("LEGEND Site Source must keep one active home page.");

        var proposedNodeIds = Flatten(output).Select(entry => entry.Node.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in protectedNodes.Values)
        {
            var protectedSemantic = !string.IsNullOrWhiteSpace(entry.Node.ActionKey) ||
                !string.IsNullOrWhiteSpace(entry.Node.SystemKey) ||
                !string.IsNullOrWhiteSpace(entry.Node.SystemBinding);
            if (protectedSemantic && !proposedNodeIds.Contains(entry.Node.Id))
                throw new ArgumentException($"Protected component '{entry.Node.Id}' cannot be removed because its canonical behavior is platform-owned.");
        }

        output = WebsiteContentSanitizer.Sanitize(output);
        ValidateCanonical(output, ctaCatalog);

        var serialized = Serialize(output);
        var reparsed = JsonSerializer.Deserialize<WebsiteSiteSourceDocument>(serialized, SourceOptions)
            ?? throw new InvalidOperationException("website_site_source_roundtrip_failed");
        if (!string.Equals(reparsed.Schema, Schema, StringComparison.Ordinal))
            throw new InvalidOperationException("website_site_source_roundtrip_failed");

        return new WebsiteSiteSourceParseResult(output, BuildSourceMap(serialized));
    }

    public static IReadOnlyDictionary<string, WebsiteSiteSourceLocation> BuildSourceMap(string sourceText)
    {
        var result = new Dictionary<string, WebsiteSiteSourceLocation>(StringComparer.Ordinal);
        string? page = null;
        var lines = sourceText.Replace("\r", string.Empty).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.StartsWith("\"path\":", StringComparison.Ordinal))
                page = ReadJsonStringValue(line);
            if (!line.StartsWith("\"id\":", StringComparison.Ordinal)) continue;
            var id = ReadJsonStringValue(line);
            if (string.IsNullOrWhiteSpace(id) || result.ContainsKey(id)) continue;
            result[id] = new WebsiteSiteSourceLocation(index + 1, page, id);
        }
        return result;
    }

    public static void ValidateCanonical(
        WebsiteContentDocument document,
        IReadOnlyList<WebsiteCallToActionOption> ctaCatalog)
    {
        if (document.LegacyMigration is not null || document.Version != WebsiteStudioContract.CurrentDocumentVersion)
            throw new ArgumentException("Website v3 composition is not canonical.");

        var validActions = ctaCatalog.Select(option => option.Key).ToHashSet(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var primaryNavigationCount = 0;

        foreach (var (pagePath, node) in Flatten(document))
        {
            if (!ids.Add(node.Id))
                throw new ArgumentException($"Duplicate website node ID '{node.Id}'.");

            if (!string.IsNullOrWhiteSpace(node.ActionKey) && !validActions.Contains(node.ActionKey))
                throw new ArgumentException($"Website action '{node.ActionKey}' is not available for this website.");

            if (node.Type == "form" &&
                !string.Equals(node.SystemKey, "canonical_inquiry", StringComparison.Ordinal))
                throw new ArgumentException("Website forms must use the canonical inquiry authority.");

            if (string.Equals(node.SystemKey, "primary_navigation", StringComparison.Ordinal))
            {
                primaryNavigationCount++;
                if (!string.Equals(pagePath, "@shell/header", StringComparison.Ordinal) ||
                    node.Type != "container" ||
                    !string.Equals(node.Tag, "nav", StringComparison.Ordinal))
                    throw new ArgumentException("Primary navigation must remain the protected nav component in the shared website header.");
            }

            if (node.Type == "reusable" &&
                (string.IsNullOrWhiteSpace(node.SyncSourceId) ||
                 !document.ReusableComponents.ContainsKey(node.SyncSourceId)))
                throw new ArgumentException($"Reusable component '{node.Id}' must reference an existing synchronized component definition.");

            if (node.Type is "cta" or "link" &&
                string.IsNullOrWhiteSpace(node.ActionKey) &&
                string.IsNullOrWhiteSpace(WebsiteContentSanitizer.SanitizeUrl(node.Href)))
                throw new ArgumentException($"Website link '{node.Id}' requires a canonical action or safe destination.");

            if (node.Type == "cta" && !string.IsNullOrWhiteSpace(node.ActionKey) &&
                !WebsiteCallToActionCatalog.TryResolveBehavior(
                    document.Pages.ContainsKey(pagePath) ? InferSiteKey(node.ActionKey) : string.Empty,
                    node.ActionKey,
                    out _))
            {
                // The scoped catalog above remains the actual acceptance authority.
                // This extra check only prevents malformed keys that happen to collide
                // with an unrelated string; site-specific availability is already exact.
                if (!validActions.Contains(node.ActionKey))
                    throw new ArgumentException($"Website CTA '{node.Id}' has an invalid action.");
            }
        }

        if (primaryNavigationCount > 1)
            throw new ArgumentException("Website v3 may contain only one primary navigation authority.");
    }

    private static string InferSiteKey(string actionKey) =>
        actionKey.StartsWith("legend_", StringComparison.Ordinal) ? WebsiteEditorSiteKeys.Legend :
        actionKey.StartsWith("protect_", StringComparison.Ordinal) ? WebsiteEditorSiteKeys.Protect :
        actionKey.StartsWith("business_", StringComparison.Ordinal) ? WebsiteEditorSiteKeys.Business :
        WebsiteEditorSiteKeys.Business;

    private static WebsiteSiteSourceDocument Project(WebsiteContentDocument document)
    {
        var source = new WebsiteSiteSourceDocument
        {
            FaviconImageDataUrl = document.FaviconImageDataUrl,
            Store = Clone(document.Store),
            Breakpoints = Clone(document.Breakpoints),
            Theme = Clone(document.Theme),
            Shell = new WebsiteSharedShellDocument
            {
                Header = document.Shell.Header.Select(ProjectNode).ToList(),
                Footer = document.Shell.Footer.Select(ProjectNode).ToList()
            },
            ReusableComponents = new(
                document.ReusableComponents
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(pair => pair.Key, pair => ProjectReusable(pair.Value), StringComparer.Ordinal),
                StringComparer.Ordinal),
            Collections = new(
                document.Collections
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                StringComparer.Ordinal)
        };

        foreach (var (path, page) in document.Pages
                     .OrderBy(pair => pair.Value.Navigation?.Order ?? int.MaxValue)
                     .ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            source.Pages.Add(new WebsiteSiteSourcePage
            {
                Path = path,
                Title = page.Title,
                Description = page.Description,
                Navigation = Clone(page.Navigation),
                DynamicBinding = Clone(page.DynamicBinding),
                Composition = page.Composition.Select(ProjectNode).ToList()
            });
        }
        return source;
    }

    private static WebsiteReusableComponentDefinition ProjectReusable(WebsiteReusableComponentDefinition value)
    {
        var copy = Clone(value);
        copy.Composition = value.Composition.Select(ProjectNode).ToList();
        return copy;
    }

    private static Dictionary<string, WebsiteReusableComponentDefinition> ProtectReusableComponents(
        IReadOnlyDictionary<string, WebsiteReusableComponentDefinition>? proposed,
        IReadOnlyDictionary<string, WebsiteReusableComponentDefinition>? baseline,
        IReadOnlyDictionary<string, (string PagePath, WebsiteCompositionNode Node)> protectedNodes,
        IReadOnlySet<string> allowedActions,
        HashSet<string> allIds)
    {
        var result = new Dictionary<string, WebsiteReusableComponentDefinition>(StringComparer.Ordinal);
        foreach (var (key, candidate) in proposed ?? new Dictionary<string, WebsiteReusableComponentDefinition>())
        {
            var id = (key ?? string.Empty).Trim();
            if (id.Length == 0) continue;
            var next = Clone(candidate);
            next.Id = id;
            ProtectSemantics(next.Composition, "@component/" + id, protectedNodes, allowedActions, allIds);
            result[id] = next;
        }

        foreach (var (key, previous) in baseline ?? new Dictionary<string, WebsiteReusableComponentDefinition>())
        {
            if (result.ContainsKey(key)) continue;
            var copy = Clone(previous);
            ProtectSemantics(copy.Composition, "@component/" + key, protectedNodes, allowedActions, allIds);
            result[key] = copy;
        }

        return result;
    }

    private static WebsiteCompositionNode ProjectNode(WebsiteCompositionNode source)
    {
        var copy = Clone(source);
        copy.Signals = [];
        copy.Children = source.Children.Select(ProjectNode).ToList();
        return copy;
    }

    private static void ProtectSemantics(
        IEnumerable<WebsiteCompositionNode> nodes,
        string pagePath,
        IReadOnlyDictionary<string, (string PagePath, WebsiteCompositionNode Node)> baseline,
        IReadOnlySet<string> allowedActions,
        HashSet<string> allIds)
    {
        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id) || !allIds.Add(node.Id))
                throw new ArgumentException("LEGEND Site Source node IDs must be non-empty and globally unique.");

            if (baseline.TryGetValue(node.Id, out var previous))
            {
                var protectedBehavior = !string.IsNullOrWhiteSpace(previous.Node.ActionKey) ||
                    !string.IsNullOrWhiteSpace(previous.Node.SystemKey);
                if (protectedBehavior && !string.Equals(previous.Node.Type, node.Type, StringComparison.Ordinal))
                    throw new ArgumentException($"Protected component '{node.Id}' cannot change component type. Delete it explicitly and add a new free-content node instead.");

                node.Signals = Clone(previous.Node.Signals);

                if (!string.IsNullOrWhiteSpace(previous.Node.SystemKey))
                {
                    if (!string.Equals(previous.Node.SystemKey, node.SystemKey, StringComparison.Ordinal))
                        throw new ArgumentException($"Protected component '{node.Id}' cannot change its system authority.");
                }

                if (!string.IsNullOrWhiteSpace(previous.Node.SystemBinding))
                {
                    if (!string.Equals(previous.Node.SystemBinding, node.SystemBinding, StringComparison.Ordinal))
                        throw new ArgumentException($"Protected component '{node.Id}' cannot change its system data authority.");
                }
                else if (!string.IsNullOrWhiteSpace(node.SystemBinding))
                {
                    throw new ArgumentException($"Free-content component '{node.Id}' cannot invent a system data authority.");
                }

                if (!string.IsNullOrWhiteSpace(previous.Node.ActionKey))
                {
                    if (string.IsNullOrWhiteSpace(node.ActionKey))
                        node.ActionKey = previous.Node.ActionKey;
                    else if (!string.Equals(previous.Node.ActionKey, node.ActionKey, StringComparison.Ordinal))
                        throw new ArgumentException($"Protected component '{node.Id}' cannot change its canonical action identity.");
                }

                if (node.Type is "image" or "video" &&
                    !node.MediaAssetId.HasValue &&
                    !string.Equals(node.MediaUrl, previous.Node.MediaUrl, StringComparison.Ordinal))
                    throw new ArgumentException($"Media component '{node.Id}' must use an asset from this website's media library.");
            }
            else
            {
                node.Signals = [];
                if (node.Type != "form" && !string.IsNullOrWhiteSpace(node.SystemKey))
                    throw new ArgumentException($"Free-content component '{node.Id}' cannot invent a platform system authority.");
                if (node.Type is "image" or "video" &&
                    !node.MediaAssetId.HasValue &&
                    !string.IsNullOrWhiteSpace(node.MediaUrl))
                    throw new ArgumentException($"New media component '{node.Id}' must use an asset from this website's media library.");
            }

            if (!string.IsNullOrWhiteSpace(node.ActionKey) && !allowedActions.Contains(node.ActionKey))
                throw new ArgumentException($"Action '{node.ActionKey}' is not available for this website.");

            if (node.Type == "form")
            {
                if (string.IsNullOrWhiteSpace(node.SystemKey)) node.SystemKey = "canonical_inquiry";
                if (!string.Equals(node.SystemKey, "canonical_inquiry", StringComparison.Ordinal))
                    throw new ArgumentException("Website forms must use the canonical inquiry authority.");
            }

            ProtectSemantics(node.Children, pagePath, baseline, allowedActions, allIds);
        }
    }

    private static IEnumerable<(string PagePath, WebsiteCompositionNode Node)> Flatten(WebsiteContentDocument document)
    {
        foreach (var node in Flatten("@shell/header", document.Shell?.Header ?? []))
            yield return node;
        foreach (var node in Flatten("@shell/footer", document.Shell?.Footer ?? []))
            yield return node;
        foreach (var (path, page) in document.Pages)
            foreach (var node in Flatten(path, page.Composition))
                yield return node;
        foreach (var (id, component) in document.ReusableComponents)
            foreach (var node in Flatten("@component/" + id, component.Composition))
                yield return node;
    }

    private static IEnumerable<(string PagePath, WebsiteCompositionNode Node)> Flatten(
        string pagePath,
        IEnumerable<WebsiteCompositionNode> nodes)
    {
        foreach (var node in nodes ?? [])
        {
            yield return (pagePath, node);
            foreach (var child in Flatten(pagePath, node.Children))
                yield return child;
        }
    }

    private static string? NormalizePagePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var route = value.Trim();
        if (!route.StartsWith('/') || route.StartsWith("//") || route.Contains('?') ||
            route.Contains('#') || route.Contains("..") || route.Contains('\\') ||
            route.Any(char.IsControl) || route.Length > 160)
            return null;
        return route.Length > 1 ? route.TrimEnd('/') : route;
    }

    private static string? ReadJsonStringValue(string line)
    {
        var colon = line.IndexOf(':');
        if (colon < 0) return null;
        var raw = line[(colon + 1)..].Trim().TrimEnd(',');
        try { return JsonSerializer.Deserialize<string>(raw); }
        catch (JsonException) { return null; }
    }

    private static T Clone<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, SourceOptions);
        return JsonSerializer.Deserialize<T>(json, SourceOptions)!;
    }
}
