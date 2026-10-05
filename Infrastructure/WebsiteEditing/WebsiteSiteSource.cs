using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

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

public sealed class WebsiteSiteSourceProtectionException : ArgumentException
{
    public WebsiteSiteSourceProtectionException(string message) : base(message) { }
}

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
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { info =>
            {
                if (info.Type == typeof(WebsiteCompositionNode))
                {
                    foreach (var property in info.Properties)
                    {
                        if (property.Name is "signals" or "fieldSignals")
                            property.ShouldSerialize = (_, _) => false;
                        else if (property.Name is "children" or "animations")
                            property.ShouldSerialize = (_, value) => value is System.Collections.ICollection collection && collection.Count > 0;
                        else if (property.Name is "breakpointStyles" or "breakpointLayouts" or "fieldPresentations" or "fieldLabels")
                            property.ShouldSerialize = (_, value) => value is System.Collections.IDictionary dictionary && dictionary.Count > 0;
                        else if (property.Name == "style")
                            property.ShouldSerialize = (_, value) => value is WebsiteVisualStyle style && !IsDefaultStyle(style);
                        else if (property.Name == "layout")
                            property.ShouldSerialize = (_, value) => value is WebsiteCompositionLayout layout && !IsDefaultLayout(layout);
                    }
                }
                else if (info.Type == typeof(WebsiteSiteSourceDocument))
                {
                    foreach (var property in info.Properties)
                        if (property.Name == "breakpoints")
                            property.ShouldSerialize = (_, value) => value is not List<WebsiteBreakpointDefinition> breakpoints || !AreDefaultBreakpoints(breakpoints);
                }
            } }
        }
    };

    // Cloning canonical state must retain protected fields. Only the outward
    // Source projection omits them; parsing must still detect attempted writes.
    private static readonly JsonSerializerOptions CanonicalCloneOptions = new(SourceOptions)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private static bool IsDefaultStyle(WebsiteVisualStyle style) =>
        WebsiteCreativeFingerprint.For(style) == WebsiteCreativeFingerprint.For(new WebsiteVisualStyle());

    private static bool IsDefaultLayout(WebsiteCompositionLayout layout) =>
        string.Equals(layout.Mode, "free", StringComparison.Ordinal) &&
        string.Equals(layout.Direction, "column", StringComparison.Ordinal) &&
        layout.GapPx is null && layout.Columns is null && layout.MinItemWidthPx is null &&
        layout.AlignItems is null && layout.JustifyContent is null && layout.Wrap is null;

    private static bool AreDefaultBreakpoints(IReadOnlyList<WebsiteBreakpointDefinition> values)
    {
        var defaults = WebsiteStudioContract.DefaultBreakpoints();
        if (values.Count != defaults.Count) return false;
        for (var index = 0; index < values.Count; index++)
        {
            var left = values[index];
            var right = defaults[index];
            if (left.Key != right.Key || left.Label != right.Label || left.MinWidth != right.MinWidth ||
                left.MaxWidth != right.MaxWidth || left.IsSystem != right.IsSystem)
                return false;
        }
        return true;
    }

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
        IReadOnlyList<WebsiteCallToActionOption> ctaCatalog,
        bool validateCanonical = true,
        bool requireActiveHomePage = true)
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
            throw new WebsiteSiteSourceProtectionException("Enable or remove the website store through the canonical Store authority, not Site Source.");

        var sourcePages = source.Pages ?? [];
        if (sourcePages.Count > 100) throw new ArgumentException("Website page limit exceeded.");

        var actionCatalog = ctaCatalog
            .GroupBy(option => option.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
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

        ProtectSemantics(output.Shell.Header, "@shell/header", protectedNodes, actionCatalog, nodeIds);
        ProtectSemantics(output.Shell.Footer, "@shell/footer", protectedNodes, actionCatalog, nodeIds);

        output.ReusableComponents = ProtectReusableComponents(
            source.ReusableComponents,
            current.ReusableComponents,
            protectedNodes,
            actionCatalog,
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
                DynamicBinding = page.DynamicBinding,
                SystemTemplateKey = currentPage?.SystemTemplateKey,
                Composition = Clone(page.Composition ?? [])
            };

            ProtectSemantics(next.Composition, path, protectedNodes, actionCatalog, nodeIds);
            output.Pages[path] = next;
        }

        if (requireActiveHomePage &&
            (!output.Pages.TryGetValue("/", out var home) || home.Navigation.IsDeleted))
            throw new ArgumentException("LEGEND Site Source must keep one active home page.");

        var proposedNodeIds = Flatten(output).Select(entry => entry.Node.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in protectedNodes.Values)
        {
            if (HasProtectedSemantics(entry.Node) && !proposedNodeIds.Contains(entry.Node.Id))
                throw new WebsiteSiteSourceProtectionException($"Protected component '{entry.Node.Id}' cannot be removed because its canonical behavior is platform-owned.");
        }

        output = WebsiteContentSanitizer.Sanitize(output);
        if (validateCanonical)
            ValidateCanonical(output, ctaCatalog);

        var serialized = Serialize(output);
        var reparsed = JsonSerializer.Deserialize<WebsiteSiteSourceDocument>(serialized, SourceOptions)
            ?? throw new InvalidOperationException("website_site_source_roundtrip_failed");
        if (!string.Equals(reparsed.Schema, Schema, StringComparison.Ordinal))
            throw new InvalidOperationException("website_site_source_roundtrip_failed");

        return new WebsiteSiteSourceParseResult(output, BuildSourceMap(serialized));
    }

    public static void EnsureSelectedNodeOnly(
        WebsiteContentDocument baseline,
        WebsiteContentDocument proposed,
        string? selectedNodeId)
    {
        selectedNodeId = selectedNodeId?.Trim();
        if (string.IsNullOrWhiteSpace(selectedNodeId))
            throw new ArgumentException("Selected Source requires one stable selected component ID. Master Source is read only.");

        var before = WebsiteContentSanitizer.Sanitize(Clone(baseline));
        var after = WebsiteContentSanitizer.Sanitize(Clone(proposed));
        var markerNode = new WebsiteCompositionNode
        {
            Id = selectedNodeId,
            Type = "text",
            Tag = "span",
            Text = "__selected_source_boundary__"
        };

        static int ReplaceIn(
            List<WebsiteCompositionNode>? nodes,
            string id,
            WebsiteCompositionNode marker)
        {
            var count = 0;
            if (nodes is null) return count;
            for (var index = 0; index < nodes.Count; index++)
            {
                var node = nodes[index];
                if (string.Equals(node.Id, id, StringComparison.Ordinal))
                {
                    nodes[index] = Clone(marker);
                    count++;
                    continue;
                }
                count += ReplaceIn(node.Children, id, marker);
            }
            return count;
        }

        static int ReplaceSelection(
            WebsiteContentDocument document,
            string id,
            WebsiteCompositionNode marker)
        {
            var count = ReplaceIn(document.Shell.Header, id, marker) +
                        ReplaceIn(document.Shell.Footer, id, marker);
            foreach (var page in document.Pages.Values)
                count += ReplaceIn(page.Composition, id, marker);
            foreach (var component in document.ReusableComponents.Values)
                count += ReplaceIn(component.Composition, id, marker);
            return count;
        }

        if (ReplaceSelection(before, selectedNodeId, markerNode) != 1 ||
            ReplaceSelection(after, selectedNodeId, markerNode) != 1)
            throw new WebsiteSiteSourceProtectionException(
                $"Selected Source component '{selectedNodeId}' must keep one stable canonical identity.");

        if (!string.Equals(Serialize(before), Serialize(after), StringComparison.Ordinal))
            throw new ArgumentException(
                "Selected Source may modify only the selected canonical component. Master Source, page metadata, theme, shell siblings, and unrelated components are read only from this surface.");
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

        foreach (var page in document.Pages.Values)
            if (page.SystemTemplateKey is not null &&
                !WebsiteSystemTemplateAuthority.IsKnownTemplateKey(page.SystemTemplateKey))
                throw new WebsiteSiteSourceProtectionException("Website runtime template binding is invalid.");

        var validActions = ctaCatalog.Select(option => option.Key).ToHashSet(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var primaryNavigationCount = 0;

        foreach (var (pagePath, node) in Flatten(document))
        {
            if (!ids.Add(node.Id))
                throw new ArgumentException($"Duplicate website node ID '{node.Id}'.");

            if (!string.IsNullOrWhiteSpace(node.ActionKey) && !validActions.Contains(node.ActionKey))
                throw new WebsiteSiteSourceProtectionException($"Website action '{node.ActionKey}' is not available for this website.");

            if (node.Type == "experience")
            {
                if (node.Experience is null)
                    throw new ArgumentException($"Website experience '{node.Id}' requires a native experience definition.");
                WebsiteExperiencePolicy.ValidateForPublish(node.Experience, validActions);
                ValidateExperienceFieldSignalTargets(node);
            }

            if (node.Type == "form" &&
                !string.Equals(node.SystemKey, "canonical_inquiry", StringComparison.Ordinal))
                throw new WebsiteSiteSourceProtectionException("Website forms must use the canonical inquiry authority.");

            if (WebsiteSystemTemplateAuthority.IsRuntimeFormSystemKey(node.SystemKey))
            {
                if (!document.Pages.TryGetValue(pagePath, out var runtimePage) ||
                    !WebsiteSystemTemplateAuthority.IsKnownTemplateKey(runtimePage.SystemTemplateKey) ||
                    node.SystemKey != WebsiteSystemTemplateAuthority.RuntimeFormKey(pagePath))
                    throw new WebsiteSiteSourceProtectionException("Protected runtime forms are allowed only on server-bound Protect template pages.");
            }

            if (string.Equals(node.SystemKey, "primary_navigation", StringComparison.Ordinal))
            {
                primaryNavigationCount++;
                if (!string.Equals(pagePath, "@shell/header", StringComparison.Ordinal) ||
                    node.Type != "container" ||
                    !string.Equals(node.Tag, "nav", StringComparison.Ordinal))
                    throw new WebsiteSiteSourceProtectionException("Primary navigation must remain the protected nav component in the shared website header.");
            }

            if (node.Type == "reusable" &&
                (string.IsNullOrWhiteSpace(node.SyncSourceId) ||
                 !document.ReusableComponents.ContainsKey(node.SyncSourceId)))
                throw new WebsiteSiteSourceProtectionException($"Reusable component '{node.Id}' must reference an existing synchronized component definition.");

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
                    throw new WebsiteSiteSourceProtectionException($"Website CTA '{node.Id}' has an invalid action.");
            }
        }

        foreach (var (path, page) in document.Pages)
        {
            if (page.Navigation?.IsDeleted == true || page.SystemTemplateKey is null) continue;
            var expected = WebsiteSystemTemplateAuthority.RuntimeFormKey(path);
            if (expected is not null && Flatten(path, page.Composition).Count(item => item.Node.SystemKey == expected) != 1)
                throw new WebsiteSiteSourceProtectionException("Materialize the protected runtime form before publishing this page.");
        }

        if (primaryNavigationCount > 1)
            throw new WebsiteSiteSourceProtectionException("Website v3 may contain only one primary navigation authority.");
    }

    private static void ProtectMappedExperienceControls(
        WebsiteCompositionNode proposed,
        WebsiteCompositionNode previous)
    {
        if (previous.Experience is null) return;

        var mappedKeys = (previous.FieldSignals ?? new Dictionary<string, List<WebsiteSignalBinding>>(StringComparer.Ordinal))
            .Where(pair => (pair.Value?.Count ?? 0) > 0)
            .Select(pair => pair.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (mappedKeys.Length == 0) return;

        if (proposed.Experience is null)
            throw new WebsiteSiteSourceProtectionException(
                $"Interactive experience '{previous.Id}' cannot remove its definition while mapped controls are attached.");

        foreach (var mappedKey in mappedKeys)
        {
            var before = previous.Experience.Controls
                .SingleOrDefault(control => string.Equals(control.Key, mappedKey, StringComparison.OrdinalIgnoreCase));
            if (before is null)
                throw new WebsiteSiteSourceProtectionException(
                    $"Interactive experience '{previous.Id}' contains an orphan protected mapping for control '{mappedKey}'.");

            var after = proposed.Experience.Controls
                .SingleOrDefault(control => string.Equals(control.Key, mappedKey, StringComparison.OrdinalIgnoreCase));
            if (after is null)
                throw new WebsiteSiteSourceProtectionException(
                    $"Mapped control '{mappedKey}' cannot be removed or renamed while its Analytics mapping is attached. Remove the custom mapping through Analytics first.");

            if (!string.Equals(before.Type, after.Type, StringComparison.Ordinal))
                throw new WebsiteSiteSourceProtectionException(
                    $"Mapped control '{mappedKey}' cannot change control type while its Analytics mapping is attached. Remove the custom mapping through Analytics first.");

            var beforeAction = before.Action?.ActionKey;
            if (!string.IsNullOrWhiteSpace(beforeAction) &&
                !string.Equals(beforeAction, after.Action?.ActionKey, StringComparison.Ordinal))
                throw new WebsiteSiteSourceProtectionException(
                    $"Mapped control '{mappedKey}' cannot retarget its canonical action while its Analytics mapping is attached.");
        }
    }

    private static void ValidateExperienceFieldSignalTargets(WebsiteCompositionNode node)
    {
        if (!string.Equals(node.Type, "experience", StringComparison.Ordinal) ||
            node.Experience is null ||
            node.FieldSignals is null ||
            node.FieldSignals.Count == 0)
            return;

        foreach (var (fieldKey, bindings) in node.FieldSignals)
        {
            if ((bindings?.Count ?? 0) == 0) continue;
            var controls = node.Experience.Controls.Where(control =>
                string.Equals(control.Key, fieldKey, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (controls.Length != 1)
                throw new WebsiteSiteSourceProtectionException(
                    $"Interactive experience '{node.Id}' must contain exactly one control for mapped field '{fieldKey}'.");
            try
            {
                WebsiteSignalBindingPolicy.ValidateExperienceControl(controls[0], bindings!);
            }
            catch (ArgumentException ex)
            {
                throw new WebsiteSiteSourceProtectionException(
                    $"Interactive experience '{node.Id}' has an invalid mapping for field '{fieldKey}': {ex.Message}");
            }
        }
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
        IReadOnlyDictionary<string, WebsiteCallToActionOption> actionCatalog,
        HashSet<string> allIds)
    {
        var result = new Dictionary<string, WebsiteReusableComponentDefinition>(StringComparer.Ordinal);
        foreach (var (key, candidate) in proposed ?? new Dictionary<string, WebsiteReusableComponentDefinition>())
        {
            var id = (key ?? string.Empty).Trim();
            if (id.Length == 0) continue;
            var next = Clone(candidate);
            next.Id = id;
            ProtectSemantics(next.Composition, "@component/" + id, protectedNodes, actionCatalog, allIds);
            result[id] = next;
        }

        foreach (var (key, previous) in baseline ?? new Dictionary<string, WebsiteReusableComponentDefinition>())
        {
            if (result.ContainsKey(key)) continue;
            var copy = Clone(previous);
            ProtectSemantics(copy.Composition, "@component/" + key, protectedNodes, actionCatalog, allIds);
            result[key] = copy;
        }

        return result;
    }

    private static readonly HashSet<string> ReservedRuntimeClasses = new(StringComparer.Ordinal)
    {
        "site-header",
        "site-footer",
        "nav-toggle",
        "public-form",
        "legend-cms-inquiry-form",
        "legend-cms-embed",
        "legend-store-nav-cluster",
        "legend-store-cart",
        "pf-cart-count",
        "cms-reusable-instance"
    };

    private static bool IsReservedRuntimeClass(string token) =>
        ReservedRuntimeClasses.Contains(token) ||
        token.StartsWith("legend-cms-", StringComparison.Ordinal) ||
        token.StartsWith("legend-store-", StringComparison.Ordinal) ||
        token.StartsWith("pf-", StringComparison.Ordinal);

    private static HashSet<string> ClassTokens(string? value) =>
        (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);

    private static void ProtectRuntimeClasses(
        WebsiteCompositionNode node,
        WebsiteCompositionNode? previous)
    {
        var previousTokens = ClassTokens(previous?.ClassName);
        foreach (var token in ClassTokens(node.ClassName))
        {
            if (IsReservedRuntimeClass(token) && !previousTokens.Contains(token))
                throw new WebsiteSiteSourceProtectionException(
                    $"Component '{node.Id}' cannot invent platform runtime class '{token}'. Use author-owned presentation classes instead.");
        }
    }

    internal static bool HasProtectedSemantics(WebsiteCompositionNode node) =>
        !string.IsNullOrWhiteSpace(node.SystemKey) ||
        !string.IsNullOrWhiteSpace(node.SystemBinding) ||
        string.Equals(node.Type, "form", StringComparison.Ordinal) ||
        (node.Signals?.Count ?? 0) > 0 ||
        (node.FieldSignals?.Values.Sum(value => value?.Count ?? 0) ?? 0) > 0;

    private static WebsiteCompositionNode ProjectNode(WebsiteCompositionNode source)
    {
        var copy = Clone(source);
        copy.Signals = [];
        copy.FieldSignals = new Dictionary<string, List<WebsiteSignalBinding>>(StringComparer.Ordinal);

        // Source is the public authoring projection, not a backend wiring dump.
        // Server-owned authority is restored by stable node ID during parse.
        copy.SystemKey = null;
        copy.SystemBinding = null;

        // Managed actions expose the approved catalog identity but never the
        // resolved destination/window behavior. Retargeting is allowed only by
        // selecting another exact catalog ActionKey.
        if (!string.IsNullOrWhiteSpace(source.ActionKey))
        {
            copy.Href = null;
            copy.Target = null;
        }

        // Data binding is authorable for free content. It is hidden only when
        // the current node's backend/system/signal contract owns that binding.
        if (HasProtectedSemantics(source))
            copy.DataBinding = null;

        copy.Children = source.Children.Select(ProjectNode).ToList();
        return copy;
    }

    private static void ProtectSemantics(
        IEnumerable<WebsiteCompositionNode> nodes,
        string pagePath,
        IReadOnlyDictionary<string, (string PagePath, WebsiteCompositionNode Node)> baseline,
        IReadOnlyDictionary<string, WebsiteCallToActionOption> actionCatalog,
        HashSet<string> allIds)
    {
        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id) || !allIds.Add(node.Id))
                throw new ArgumentException("LEGEND Site Source node IDs must be non-empty and globally unique.");

            if (baseline.TryGetValue(node.Id, out var previous))
            {
                ProtectRuntimeClasses(node, previous.Node);
                var protectedBehavior = HasProtectedSemantics(previous.Node);
                if (protectedBehavior &&
                    !string.Equals(previous.Node.Type, node.Type, StringComparison.Ordinal))
                    throw new WebsiteSiteSourceProtectionException($"Protected component '{node.Id}' cannot change component type while platform-owned behavior is attached.");

                // Signal mappings are managed only through the canonical signal/
                // analytics controls. Source may redesign the signal-bearing node,
                // but cannot add, remove, or rewrite its mappings.
                if (string.Equals(previous.Node.Type, "experience", StringComparison.Ordinal))
                    ProtectMappedExperienceControls(node, previous.Node);

                node.Signals = Clone(previous.Node.Signals);
                node.FieldSignals = Clone(previous.Node.FieldSignals);

                if (!string.IsNullOrWhiteSpace(previous.Node.SystemKey))
                {
                    if (string.IsNullOrWhiteSpace(node.SystemKey))
                        node.SystemKey = previous.Node.SystemKey;
                    else if (!string.Equals(previous.Node.SystemKey, node.SystemKey, StringComparison.Ordinal))
                        throw new WebsiteSiteSourceProtectionException($"Protected component '{node.Id}' cannot change its system authority.");
                }
                else if (!string.IsNullOrWhiteSpace(node.SystemKey) && node.Type != "form")
                {
                    throw new WebsiteSiteSourceProtectionException($"Free-content component '{node.Id}' cannot invent a platform system authority.");
                }

                if (!string.IsNullOrWhiteSpace(previous.Node.SystemBinding))
                {
                    if (string.IsNullOrWhiteSpace(node.SystemBinding))
                        node.SystemBinding = previous.Node.SystemBinding;
                    else if (!string.Equals(previous.Node.SystemBinding, node.SystemBinding, StringComparison.Ordinal))
                        throw new WebsiteSiteSourceProtectionException($"Protected component '{node.Id}' cannot change its system data authority.");
                }
                else if (!string.IsNullOrWhiteSpace(node.SystemBinding))
                {
                    throw new WebsiteSiteSourceProtectionException($"Free-content component '{node.Id}' cannot invent a system data authority.");
                }

                // A signal/system-owned ActionKey is identity-protected. A normal
                // managed CTA instance is authorable: it may select another exact
                // server catalog action or become a safe free link.
                if (protectedBehavior && !string.IsNullOrWhiteSpace(previous.Node.ActionKey))
                {
                    if (string.IsNullOrWhiteSpace(node.ActionKey))
                        throw new WebsiteSiteSourceProtectionException($"Protected component '{node.Id}' cannot remove its canonical action identity.");
                    if (!string.Equals(previous.Node.ActionKey, node.ActionKey, StringComparison.Ordinal))
                        throw new WebsiteSiteSourceProtectionException($"Protected component '{node.Id}' cannot change its canonical action identity.");
                }

                if (protectedBehavior)
                    node.DataBinding = Clone(previous.Node.DataBinding);

                if (node.Type is "image" or "video" &&
                    !node.MediaAssetId.HasValue &&
                    !string.Equals(node.MediaUrl, previous.Node.MediaUrl, StringComparison.Ordinal))
                    throw new ArgumentException($"Media component '{node.Id}' must use an asset from this website's media library.");
            }
            else
            {
                ProtectRuntimeClasses(node, null);
                node.Signals = [];
                node.FieldSignals = new Dictionary<string, List<WebsiteSignalBinding>>(StringComparer.Ordinal);
                if (node.Type != "form" && !string.IsNullOrWhiteSpace(node.SystemKey))
                    throw new WebsiteSiteSourceProtectionException($"Free-content component '{node.Id}' cannot invent a platform system authority.");
                if (!string.IsNullOrWhiteSpace(node.SystemBinding))
                    throw new WebsiteSiteSourceProtectionException($"Free-content component '{node.Id}' cannot invent a system data authority.");
                if (node.Type is "image" or "video" &&
                    !node.MediaAssetId.HasValue &&
                    !string.IsNullOrWhiteSpace(node.MediaUrl))
                    throw new ArgumentException($"New media component '{node.Id}' must use an asset from this website's media library.");
            }

            if (!string.IsNullOrWhiteSpace(node.ActionKey))
            {
                if (!actionCatalog.TryGetValue(node.ActionKey, out var action))
                    throw new WebsiteSiteSourceProtectionException($"Action '{node.ActionKey}' is not available for this website.");
                node.Href = action.Href;
                node.Target = action.OpenInNewTab ? "_blank" : "_self";
            }

            if (node.Type == "form")
            {
                if (string.IsNullOrWhiteSpace(node.SystemKey)) node.SystemKey = "canonical_inquiry";
                if (!string.Equals(node.SystemKey, "canonical_inquiry", StringComparison.Ordinal))
                    throw new WebsiteSiteSourceProtectionException("Website forms must use the canonical inquiry authority.");
            }

            ProtectSemantics(node.Children, pagePath, baseline, actionCatalog, allIds);
        }
    }

    internal static IEnumerable<(string PagePath, WebsiteCompositionNode Node)> Flatten(WebsiteContentDocument document)
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
        var json = JsonSerializer.Serialize(value, CanonicalCloneOptions);
        return JsonSerializer.Deserialize<T>(json, CanonicalCloneOptions)!;
    }
}
