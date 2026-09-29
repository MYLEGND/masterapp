namespace Infrastructure.WebsiteEditing;

public static class WebsiteContentSanitizer
{
    /// <summary>
    /// Read-only compatibility for persisted documents. Retired browser mappings
    /// of verified outcomes are inactive; their original JSON remains available
    /// to the Event Map. New saves always use strict Sanitize validation.
    /// </summary>
    public static WebsiteContentDocument ReadPersisted(string json, System.Text.Json.JsonSerializerOptions options)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(json);
        void RemoveRetiredMappings(System.Text.Json.Nodes.JsonNode? node)
        {
            if (node is System.Text.Json.Nodes.JsonObject obj)
            {
                foreach (var property in obj.ToArray())
                {
                    if (property.Key.Equals("signals", StringComparison.OrdinalIgnoreCase) &&
                        property.Value is System.Text.Json.Nodes.JsonArray bindings)
                    {
                        for (var i = bindings.Count - 1; i >= 0; i--)
                        {
                            var name = bindings[i]?["eventName"]?.ToString() ?? bindings[i]?["EventName"]?.ToString();
                            if (Shared.Analytics.AnalyticsEventCatalog.RequiresServerAuthority(name)) bindings.RemoveAt(i);
                        }
                    }
                    RemoveRetiredMappings(property.Value);
                }
            }
            else if (node is System.Text.Json.Nodes.JsonArray array)
                foreach (var child in array) RemoveRetiredMappings(child);
        }

        RemoveRetiredMappings(root);
        var raw = root?.ToJsonString() ?? "{}";
        var version = 0;
        if (root is System.Text.Json.Nodes.JsonObject rootObject &&
            rootObject.TryGetPropertyValue("version", out var versionNode) &&
            int.TryParse(versionNode?.ToString(), out var parsedVersion))
            version = parsedVersion;

        if (version >= WebsiteStudioContract.CurrentDocumentVersion)
        {
            if (ContainsLegacyAuthority(root))
                throw new InvalidOperationException("website_v3_parallel_authority_detected");
            var canonical = System.Text.Json.JsonSerializer.Deserialize<WebsiteContentDocument>(raw, options) ?? new();
            return Sanitize(canonical);
        }

        var legacy = System.Text.Json.JsonSerializer.Deserialize<LegacyWebsiteContentDocument>(raw, options) ?? new();
        return ProjectLegacyForOneWayMigration(SanitizeLegacy(legacy));
    }

    internal static WebsiteContentDocument DeserializeCanonicalUntrusted(
        string json,
        System.Text.Json.JsonSerializerOptions options)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)
            ?? throw new ArgumentException("Canonical website document is empty.");
        if (ContainsLegacyAuthority(root))
            throw new ArgumentException("Pre-v3 website mutation fields are not valid in a canonical v3 document.");

        var version = 0;
        if (root is System.Text.Json.Nodes.JsonObject rootObject &&
            rootObject.TryGetPropertyValue("version", out var versionNode))
            int.TryParse(versionNode?.ToString(), out version);
        if (version != WebsiteStudioContract.CurrentDocumentVersion)
            throw new ArgumentException("Portable website documents must use the canonical v3 schema.");

        var document = System.Text.Json.JsonSerializer.Deserialize<WebsiteContentDocument>(root.ToJsonString(), options)
            ?? throw new ArgumentException("Canonical website document is invalid.");
        return Sanitize(document);
    }

    private static bool ContainsLegacyAuthority(System.Text.Json.Nodes.JsonNode? root)
    {
        if (root is not System.Text.Json.Nodes.JsonObject obj) return false;
        static bool Has(System.Text.Json.Nodes.JsonObject value, string key) =>
            value.Any(property => property.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

        if (Has(obj, "elements") || Has(obj, "extras") || Has(obj, "sectionOrder") || Has(obj, "compositionMode"))
            return true;

        if (obj["pages"] is System.Text.Json.Nodes.JsonObject pages)
            foreach (var page in pages.Select(property => property.Value).OfType<System.Text.Json.Nodes.JsonObject>())
                if (Has(page, "elements") || Has(page, "extras") || Has(page, "sectionOrder") || Has(page, "templatePath"))
                    return true;

        if (obj["reusableComponents"] is System.Text.Json.Nodes.JsonObject components)
            foreach (var component in components.Select(property => property.Value).OfType<System.Text.Json.Nodes.JsonObject>())
                if (Has(component, "elements") || Has(component, "extras") || Has(component, "sectionOrder"))
                    return true;

        return false;
    }

    private const int MaxElements = 600;
    private const int MaxExtras = 120;
    private const int MaxTextLength = 12000;
    private const int MaxCodeLength = 100000;
    private const int MaxImageDataUrlLength = 3500000;
    private const int MaxCompositionNodesPerPage = 1200;
    private const int MaxCompositionDepth = 16;

    public static WebsiteContentDocument Sanitize(WebsiteContentDocument source)
    {
        if (source.LegacyMigration is not null)
            throw new InvalidOperationException("website_legacy_migration_cannot_be_saved");

        RejectUnexpectedCanonicalFields(source);

        var breakpoints = SanitizeBreakpoints(source.Breakpoints);
        var breakpointKeys = breakpoints.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var clean = new WebsiteContentDocument
        {
            Version = WebsiteStudioContract.CurrentDocumentVersion,
            FaviconImageDataUrl = SanitizeImage(source.FaviconImageDataUrl),
            Store = SanitizeStore(source.Store, breakpointKeys),
            Breakpoints = breakpoints,
            Shell = new WebsiteSharedShellDocument
            {
                Header = SanitizeComposition(source.Shell?.Header, breakpointKeys),
                Footer = SanitizeComposition(source.Shell?.Footer, breakpointKeys)
            }
        };

        foreach (var page in (source.Pages ?? new()).Take(100))
        {
            var path = page.Key;
            if (page.Value is null || !path.StartsWith('/') || path.StartsWith("//") || path.Contains('?') ||
                path.Contains('#') || path.Contains("..") || path.Length > 2048) continue;
            clean.Pages[path] = new WebsitePageDocument
            {
                Title = ClampText(page.Value.Title),
                Description = ClampText(page.Value.Description),
                Navigation = SanitizeNavigation(path, page.Value.Navigation, page.Value.Title),
                DynamicBinding = SanitizeDynamicBinding(page.Value.DynamicBinding),
                SystemTemplateKey = WebsiteSystemTemplateAuthority.IsKnownTemplateKey(page.Value.SystemTemplateKey)
                    ? page.Value.SystemTemplateKey!.Trim()
                    : null,
                Composition = SanitizeComposition(page.Value.Composition, breakpointKeys)
            };
        }

        foreach (var pair in (source.ReusableComponents ?? new()).Take(60))
        {
            var id = SanitizeId(pair.Key);
            if (id.Length == 0 || pair.Value is null) continue;
            var component = SanitizeReusableComponent(pair.Value, breakpointKeys, id);
            if (component is not null) clean.ReusableComponents[id] = component;
        }

        foreach (var pair in (source.Collections ?? new()).Take(24))
        {
            var id = SanitizeId(pair.Key);
            if (id.Length == 0 || pair.Value is null) continue;
            var collection = SanitizeCollection(pair.Value, id);
            if (collection is not null) clean.Collections[id] = collection;
        }

        clean.Theme = SanitizeTheme(source.Theme);
        clean.UpdatedUtc = source.UpdatedUtc;
        return clean;
    }

    private static void RejectUnexpectedCanonicalFields(WebsiteContentDocument source)
    {
        static void Reject(
            IReadOnlyDictionary<string, System.Text.Json.JsonElement>? fields,
            string location)
        {
            if (fields is null || fields.Count == 0) return;
            throw new ArgumentException(
                $"Unsupported website field(s) at {location}: {string.Join(", ", fields.Keys.OrderBy(value => value, StringComparer.Ordinal))}");
        }

        void Visit(IEnumerable<WebsiteCompositionNode>? nodes, string location)
        {
            foreach (var node in nodes ?? [])
            {
                Reject(node.UnexpectedFields, location + "/" + node.Id);
                Visit(node.Children, location + "/" + node.Id);
            }
        }

        Reject(source.UnexpectedFields, "document");
        Reject(source.Shell?.UnexpectedFields, "shell");
        Visit(source.Shell?.Header, "shell/header");
        Visit(source.Shell?.Footer, "shell/footer");

        foreach (var (path, page) in source.Pages ?? new())
        {
            Reject(page?.UnexpectedFields, "page:" + path);
            Visit(page?.Composition, "page:" + path);
        }

        foreach (var (id, component) in source.ReusableComponents ?? new())
        {
            Reject(component?.UnexpectedFields, "component:" + id);
            Visit(component?.Composition, "component:" + id);
        }
    }

    private static WebsiteStoreSettings SanitizeStore(
        WebsiteStoreSettings? source,
        HashSet<string> breakpointKeys) => new()
    {
        Enabled = source?.Enabled == true,
        NavigationLabel = SanitizeStoreLabel(source?.NavigationLabel),
        CartIcon = SanitizeCartIcon(source?.CartIcon),
        CartIconSizePx = Math.Clamp(source?.CartIconSizePx ?? 28, 16, 96),
        StoreNavigation = SanitizeControlPresentation(source?.StoreNavigation, breakpointKeys),
        CartNavigation = SanitizeControlPresentation(source?.CartNavigation, breakpointKeys)
    };

    private static WebsiteControlPresentation SanitizeControlPresentation(
        WebsiteControlPresentation? source,
        HashSet<string> breakpointKeys) => new()
    {
        Style = SanitizeStyle(source?.Style),
        BreakpointStyles = SanitizeStyleMap(source?.BreakpointStyles, breakpointKeys),
        Layout = SanitizeLayout(source?.Layout),
        BreakpointLayouts = SanitizeLayoutMap(source?.BreakpointLayouts, breakpointKeys),
        Animations = SanitizeAnimations(source?.Animations)
    };

    private static WebsiteContentDocument ProjectLegacyForOneWayMigration(LegacyWebsiteContentDocument legacy)
    {
        var projected = new WebsiteContentDocument
        {
            Version = WebsiteStudioContract.CurrentDocumentVersion,
            FaviconImageDataUrl = legacy.FaviconImageDataUrl,
            Store = legacy.Store,
            Breakpoints = legacy.Breakpoints,
            Collections = legacy.Collections,
            Theme = legacy.Theme,
            UpdatedUtc = legacy.UpdatedUtc,
            LegacyMigration = legacy
        };

        foreach (var (path, page) in legacy.Pages)
            projected.Pages[path] = new WebsitePageDocument
            {
                Title = page.Title,
                Description = page.Description,
                Navigation = page.Navigation,
                DynamicBinding = page.DynamicBinding
            };

        return projected;
    }

    private static LegacyWebsiteContentDocument SanitizeLegacy(LegacyWebsiteContentDocument source)
    {
        var breakpoints = SanitizeBreakpoints(source.Breakpoints);
        var breakpointKeys = breakpoints.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var clean = new LegacyWebsiteContentDocument
        {
            Version = Math.Min(source.Version <= 0 ? 2 : source.Version, 2),
            FaviconImageDataUrl = SanitizeImage(source.FaviconImageDataUrl),
            Store = SanitizeStore(source.Store, breakpointKeys),
            Breakpoints = breakpoints
        };

        foreach (var pair in (source.Elements ?? new()).Take(MaxElements))
        {
            var id = SanitizeId(pair.Key);
            if (id.Length == 0 || pair.Value is null) continue;
            clean.Elements[id] = SanitizeElement(pair.Value, breakpointKeys);
        }
        foreach (var pair in (source.SectionOrder ?? new()).Take(MaxElements))
        {
            var id = SanitizeId(pair.Key);
            if (id.Length != 0) clean.SectionOrder[id] = Math.Clamp(pair.Value, 0, MaxElements);
        }
        foreach (var extra in (source.Extras ?? new()).Take(MaxExtras))
        {
            var cleanExtra = SanitizeLegacyExtra(extra, breakpointKeys);
            if (cleanExtra is not null) clean.Extras.Add(cleanExtra);
        }

        foreach (var page in (source.Pages ?? new()).Take(100))
        {
            var path = page.Key;
            if (page.Value is null || SanitizePagePath(path) is null) continue;
            var body = SanitizeLegacyPageBody(page.Value, breakpointKeys);
            clean.Pages[path] = new LegacyWebsitePageDocument
            {
                Title = ClampText(page.Value.Title),
                Description = ClampText(page.Value.Description),
                TemplatePath = SanitizePagePath(page.Value.TemplatePath),
                Navigation = SanitizeNavigation(path, page.Value.Navigation, page.Value.Title),
                DynamicBinding = SanitizeDynamicBinding(page.Value.DynamicBinding),
                Elements = body.Elements,
                SectionOrder = body.SectionOrder,
                Extras = body.Extras
            };
        }

        foreach (var pair in (source.ReusableComponents ?? new()).Take(60))
        {
            var id = SanitizeId(pair.Key);
            if (id.Length == 0 || pair.Value is null) continue;
            var component = SanitizeLegacyReusableComponent(pair.Value, breakpointKeys, id);
            if (component is not null) clean.ReusableComponents[id] = component;
        }

        foreach (var pair in (source.Collections ?? new()).Take(24))
        {
            var id = SanitizeId(pair.Key);
            if (id.Length == 0 || pair.Value is null) continue;
            var collection = SanitizeCollection(pair.Value, id);
            if (collection is not null) clean.Collections[id] = collection;
        }

        clean.Theme = SanitizeTheme(source.Theme);
        clean.UpdatedUtc = source.UpdatedUtc;
        return clean;
    }

    private static LegacyWebsiteExtraComponent? SanitizeLegacyExtra(LegacyWebsiteExtraComponent? extra, HashSet<string> breakpointKeys)
    {
        if (extra is null) return null;
        var id = SanitizeId(extra.Id);
        var sectionId = SanitizeId(extra.SectionId);
        var type = (extra.Type ?? string.Empty).Trim().ToLowerInvariant();
        if (id.Length == 0 || sectionId.Length == 0 ||
            type is not ("text" or "image" or "button" or "video" or "section" or "card" or "form" or "code" or "reusable"))
            return null;
        return new LegacyWebsiteExtraComponent
        {
            Id = id,
            SectionId = sectionId,
            Type = type,
            TemplateSectionId = type == "section" ? NullIfEmpty(SanitizeId(extra.TemplateSectionId)) : null,
            Signals = type == "reusable" ? [] : WebsiteSignalBindingPolicy.Validate(extra.Signals),
            ActionKey = SanitizeActionKey(extra.ActionKey),
            Title = ClampContentText(extra.Title),
            Text = type == "code" ? ClampCodeText(extra.Text) : ClampContentText(extra.Text),
            Href = SanitizeUrl(extra.Href),
            Target = SanitizeTarget(extra.Target),
            Alt = ClampText(extra.Alt),
            VideoUrl = SanitizeUrl(extra.VideoUrl, true),
            Placement = SanitizePlacement(extra.Placement),
            ImageDataUrl = type == "image" ? SanitizeImage(extra.ImageDataUrl) : null,
            Style = SanitizeStyle(extra.Style),
            BreakpointStyles = SanitizeStyleMap(extra.BreakpointStyles, breakpointKeys),
            Layout = SanitizeLayout(extra.Layout),
            BreakpointLayouts = SanitizeLayoutMap(extra.BreakpointLayouts, breakpointKeys),
            Animations = SanitizeAnimations(extra.Animations),
            SyncSourceId = NullIfEmpty(SanitizeId(extra.SyncSourceId)),
            DataBinding = SanitizeDataBinding(extra.DataBinding)
        };
    }

    private static LegacyWebsiteElementRecord SanitizeElement(LegacyWebsiteElementRecord source, HashSet<string> breakpointKeys) => new()
    {
        Signals = WebsiteSignalBindingPolicy.Validate(source.Signals),
        ActionKey = SanitizeActionKey(source.ActionKey),
        Text = ClampContentText(source.Text),
        ImageDataUrl = SanitizeImage(source.ImageDataUrl),
        Hidden = source.Hidden,
        Href = SanitizeUrl(source.Href), Target = SanitizeTarget(source.Target),
        Alt = ClampText(source.Alt), VideoUrl = SanitizeUrl(source.VideoUrl, true),
        Placement = SanitizePlacement(source.Placement),
        Style = SanitizeStyle(source.Style),
        BreakpointStyles = SanitizeStyleMap(source.BreakpointStyles, breakpointKeys),
        Layout = SanitizeLayout(source.Layout),
        BreakpointLayouts = SanitizeLayoutMap(source.BreakpointLayouts, breakpointKeys),
        Animations = SanitizeAnimations(source.Animations),
        SyncSourceId = NullIfEmpty(SanitizeId(source.SyncSourceId)),
        DataBinding = SanitizeDataBinding(source.DataBinding)
    };

    private sealed class SanitizedPageBody
    {
        public Dictionary<string, LegacyWebsiteElementRecord> Elements { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> SectionOrder { get; } = new(StringComparer.Ordinal);
        public List<LegacyWebsiteExtraComponent> Extras { get; } = new();
    }

    private static SanitizedPageBody SanitizeLegacyPageBody(LegacyWebsitePageDocument source, HashSet<string> breakpointKeys)
    {
        var clean = new SanitizedPageBody();
        foreach (var pair in (source.Elements ?? new()).Take(MaxElements))
        {
            var id = SanitizeId(pair.Key);
            if (id.Length == 0 || pair.Value is null) continue;
            clean.Elements[id] = SanitizeElement(pair.Value, breakpointKeys);
        }
        foreach (var pair in (source.SectionOrder ?? new()).Take(MaxElements))
        {
            var id = SanitizeId(pair.Key);
            if (id.Length != 0) clean.SectionOrder[id] = Math.Clamp(pair.Value, 0, MaxElements);
        }
        foreach (var extra in (source.Extras ?? new()).Take(MaxExtras))
        {
            var cleanExtra = SanitizeLegacyExtra(extra, breakpointKeys);
            if (cleanExtra is not null) clean.Extras.Add(cleanExtra);
        }
        return clean;
    }

    private static List<WebsiteCompositionNode> SanitizeComposition(
        IEnumerable<WebsiteCompositionNode>? source,
        HashSet<string> breakpointKeys)
    {
        var remaining = MaxCompositionNodesPerPage;
        return SanitizeCompositionChildren(source, breakpointKeys, 0, ref remaining);
    }

    private static List<WebsiteCompositionNode> SanitizeCompositionChildren(
        IEnumerable<WebsiteCompositionNode>? source,
        HashSet<string> breakpointKeys,
        int depth,
        ref int remaining)
    {
        var result = new List<WebsiteCompositionNode>();
        if (depth > MaxCompositionDepth || remaining <= 0) return result;

        foreach (var node in source ?? [])
        {
            if (node is null || remaining-- <= 0) break;
            var id = SanitizeId(node.Id);
            var type = (node.Type ?? string.Empty).Trim().ToLowerInvariant();
            if (id.Length == 0 || !WebsiteCompositionSchema.IsAllowedType(type))
                continue;

            var tag = WebsiteCompositionSchema.NormalizeTag(type, node.Tag);
            var systemKey = type switch
            {
                "form" when string.Equals(node.SystemKey, "canonical_inquiry", StringComparison.Ordinal) =>
                    "canonical_inquiry",
                "container" when string.Equals(tag, "nav", StringComparison.Ordinal) &&
                                 string.Equals(node.SystemKey, "primary_navigation", StringComparison.Ordinal) =>
                    "primary_navigation",
                "container" when WebsiteSystemTemplateAuthority.IsRuntimeFormSystemKey(node.SystemKey) =>
                    node.SystemKey!.Trim(),
                _ => null
            };
            if (type == "form" && systemKey is null) continue;

            var mediaUrl = type is "image" or "video"
                ? SanitizeCompositionMediaUrl(node.MediaUrl, type)
                : null;

            var clean = new WebsiteCompositionNode
            {
                Id = id,
                Type = type,
                Tag = tag,
                ClassName = SanitizeClassName(node.ClassName),
                Text = type == "embed" ? ClampCodeText(node.Text) : ClampContentText(node.Text),
                Title = ClampContentText(node.Title),
                ActionKey = type is "cta" or "link" ? SanitizeActionKey(node.ActionKey) : null,
                Href = type is "cta" or "link" ? SanitizeUrl(node.Href) : null,
                Target = type is "cta" or "link" ? SanitizeTarget(node.Target) : null,
                Alt = type is "image" or "video" ? SanitizeMediaAlt(node.Alt, node.Title, node.Text, mediaUrl, type) : null,
                MediaAssetId = type is "image" or "video" ? node.MediaAssetId : null,
                MediaUrl = mediaUrl,
                SystemKey = systemKey,
                SystemBinding = SanitizeSystemBinding(node.SystemBinding),
                SyncSourceId = type == "reusable" ? NullIfEmpty(SanitizeId(node.SyncSourceId)) : null,
                Hidden = node.Hidden,
                Signals = WebsiteSignalBindingPolicy.Validate(node.Signals),
                Style = SanitizeStyle(node.Style),
                BreakpointStyles = SanitizeStyleMap(node.BreakpointStyles, breakpointKeys),
                Layout = SanitizeLayout(node.Layout),
                BreakpointLayouts = SanitizeLayoutMap(node.BreakpointLayouts, breakpointKeys),
                Animations = SanitizeAnimations(node.Animations),
                DataBinding = SanitizeDataBinding(node.DataBinding)
            };
            clean.Children = SanitizeCompositionChildren(node.Children, breakpointKeys, depth + 1, ref remaining);
            CanonicalizePassiveLink(clean);
            CanonicalizePlatformBrandGeometry(clean);
            if (IsRetiredTemplateDecoration(clean))
                continue;
            result.Add(clean);
        }
        return result;
    }

    private static string? SanitizeMediaAlt(string? value, string? title, string? text, string? mediaUrl, string type)
    {
        if (!string.Equals(type, "image", StringComparison.Ordinal))
            return ClampText(value);

        // An explicit empty alt is a valid decorative-image decision and must
        // remain distinct from a missing alt attribute.
        if (value is not null)
            return ClampText(value) ?? string.Empty;

        foreach (var candidate in new[] { ClampText(title), ClampText(text) })
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate;

        if (!string.IsNullOrWhiteSpace(mediaUrl) &&
            !mediaUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var path = mediaUrl;
            if (Uri.TryCreate(mediaUrl, UriKind.Absolute, out var absolute))
                path = absolute.AbsolutePath;

            var file = System.IO.Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrWhiteSpace(file))
            {
                try { file = Uri.UnescapeDataString(file); } catch (UriFormatException) { }
                file = file.Replace('-', ' ').Replace('_', ' ').Trim();
                if (file.Length > 0)
                {
                    var words = file.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Select(word => word.Length == 1
                            ? word.ToUpperInvariant()
                            : char.ToUpperInvariant(word[0]) + word[1..])
                        .ToArray();
                    var derived = string.Join(' ', words);
                    return derived.Length <= 160 ? derived : derived[..160];
                }
            }
        }

        return "Website image";
    }

    private static void CanonicalizePassiveLink(WebsiteCompositionNode node)
    {
        if (!string.Equals(node.Type, "link", StringComparison.Ordinal) ||
            !string.IsNullOrWhiteSpace(node.ActionKey) ||
            (node.DataBinding is not null && string.Equals(node.DataBinding.Target, "href", StringComparison.Ordinal)))
            return;

        var href = node.Href?.Trim();
        if (!string.IsNullOrWhiteSpace(href) && href != "#")
            return;

        // A persisted anchor with no destination is not a website link. Early
        // materialization retained template/runtime placeholder anchors as links,
        // which created readiness drift. Canonical v3 stores these as presentation
        // text so startup state cannot contain dead interactive controls.
        node.Type = "text";
        node.Tag = "span";
        node.ActionKey = null;
        node.Href = null;
        node.Target = null;
        node.Signals = [];
    }

    private static void CanonicalizePlatformBrandGeometry(WebsiteCompositionNode node)
    {
        var classes = (node.ClassName ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        if (!classes.Contains("brand") && !classes.Contains("brand-wordmark"))
            return;

        ClearHorizontalGeometry(node.Style);
        foreach (var style in node.BreakpointStyles.Values)
            ClearHorizontalGeometry(style);
    }

    private static void ClearHorizontalGeometry(WebsiteVisualStyle style)
    {
        style.WidthPercent = null;
        style.OffsetXPercent = null;
    }

    private static bool IsRetiredTemplateDecoration(WebsiteCompositionNode node)
    {
        var classes = (node.ClassName ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);

        var hasMeaning =
            !string.IsNullOrWhiteSpace(node.Text) ||
            !string.IsNullOrWhiteSpace(node.Title) ||
            !string.IsNullOrWhiteSpace(node.ActionKey) ||
            !string.IsNullOrWhiteSpace(node.Href) ||
            !string.IsNullOrWhiteSpace(node.Alt) ||
            node.MediaAssetId.HasValue ||
            !string.IsNullOrWhiteSpace(node.MediaUrl) ||
            !string.IsNullOrWhiteSpace(node.SystemKey) ||
            !string.IsNullOrWhiteSpace(node.SystemBinding) ||
            !string.IsNullOrWhiteSpace(node.SyncSourceId) ||
            node.DataBinding is not null ||
            (node.Signals?.Count ?? 0) > 0 ||
            (node.Children?.Count ?? 0) > 0;

        if (hasMeaning) return false;

        // These were presentation-only wrappers in the old static templates.
        // Early v3 materialization stripped their SVG/pseudo-element content but
        // persisted the empty wrapper, creating colored blank icon/halo boxes and
        // empty hero columns. Delete only semantically empty known artifacts.
        if (classes.Contains("icon") || classes.Contains("halo") || classes.Contains("hero-mark"))
            return true;

        // An unclassed empty text span has no visual/content authority and was
        // produced by the same old hero wrapper path.
        return node.Type == "text" &&
               string.Equals(node.Tag, "span", StringComparison.Ordinal) &&
               classes.Count == 0;
    }

    private static string? SanitizeSystemBinding(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var key = value.Trim();
        if (key == "business_name") return key;
        if (!key.StartsWith("business_field:", StringComparison.Ordinal)) return null;
        var field = key["business_field:".Length..];
        return field is "displayName" or "legalName" or "businessType" or "contactEmail" or "contactPhone"
            ? key
            : null;
    }

    private static string? SanitizeClassName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var tokens = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Take(24)
            .Select(token => new string(token.Take(80)
                .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_')
                .ToArray()))
            .Where(token => token.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return tokens.Length == 0 ? null : string.Join(' ', tokens);
    }

    private static List<WebsiteBreakpointDefinition> SanitizeBreakpoints(IEnumerable<WebsiteBreakpointDefinition>? source)
    {
        var result = WebsiteStudioContract.DefaultBreakpoints();
        var keys = result.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var item in source ?? [])
        {
            if (item is null || item.IsSystem || result.Count >= 8) continue;
            var key = SanitizeId(item.Key);
            if (key.Length == 0 || !string.Equals(key, item.Key, StringComparison.Ordinal) ||
                item.MinWidth < 0 || item.MinWidth > 10000 ||
                (item.MaxWidth.HasValue && (item.MaxWidth.Value < item.MinWidth || item.MaxWidth.Value > 10000)) ||
                !keys.Add(key)) continue;
            var min = Math.Clamp(item.MinWidth, 0, 10000);
            var max = item.MaxWidth.HasValue ? Math.Clamp(item.MaxWidth.Value, min, 10000) : (int?)null;
            var label = ClampText(item.Label);
            label = string.IsNullOrWhiteSpace(label) ? key : label[..Math.Min(label.Length, 80)];
            result.Add(new WebsiteBreakpointDefinition { Key = key, Label = label, MinWidth = min, MaxWidth = max, IsSystem = false });
        }
        return result;
    }

    private static Dictionary<string, WebsiteVisualStyle> SanitizeStyleMap(IDictionary<string, WebsiteVisualStyle>? source, HashSet<string> breakpointKeys)
    {
        var clean = new Dictionary<string, WebsiteVisualStyle>(StringComparer.Ordinal);
        foreach (var pair in (source ?? new Dictionary<string, WebsiteVisualStyle>()).Take(12))
            if (breakpointKeys.Contains(pair.Key) && pair.Value is not null) clean[pair.Key] = SanitizeStyle(pair.Value);
        return clean;
    }

    private static WebsiteCompositionLayout SanitizeLayout(WebsiteCompositionLayout? source)
    {
        source ??= new WebsiteCompositionLayout();
        var mode = (source.Mode ?? "free").Trim().ToLowerInvariant();
        if (mode is not ("free" or "stack" or "grid" or "flex")) mode = "free";
        var direction = (source.Direction ?? "column").Trim().ToLowerInvariant();
        if (direction is not ("row" or "column")) direction = "column";
        var align = (source.AlignItems ?? "").Trim().ToLowerInvariant();
        if (align is not ("start" or "center" or "end" or "stretch")) align = string.Empty;
        var justify = (source.JustifyContent ?? "").Trim().ToLowerInvariant();
        if (justify is not ("start" or "center" or "end" or "space-between" or "space-around" or "space-evenly")) justify = string.Empty;
        var wrap = (source.Wrap ?? "").Trim().ToLowerInvariant();
        if (wrap is not ("nowrap" or "wrap")) wrap = string.Empty;
        return new WebsiteCompositionLayout
        {
            Mode = mode, Direction = direction,
            GapPx = source.GapPx is >= 0 and <= 240 ? source.GapPx : null,
            Columns = source.Columns is >= 1 and <= 12 ? source.Columns : null,
            MinItemWidthPx = source.MinItemWidthPx is >= 1 and <= 4000 ? source.MinItemWidthPx : null,
            AlignItems = NullIfEmpty(align), JustifyContent = NullIfEmpty(justify), Wrap = NullIfEmpty(wrap)
        };
    }

    private static Dictionary<string, WebsiteCompositionLayout> SanitizeLayoutMap(IDictionary<string, WebsiteCompositionLayout>? source, HashSet<string> breakpointKeys)
    {
        var clean = new Dictionary<string, WebsiteCompositionLayout>(StringComparer.Ordinal);
        foreach (var pair in (source ?? new Dictionary<string, WebsiteCompositionLayout>()).Take(12))
            if (breakpointKeys.Contains(pair.Key) && pair.Value is not null) clean[pair.Key] = SanitizeLayout(pair.Value);
        return clean;
    }

    private static List<WebsiteAnimationBinding> SanitizeAnimations(IEnumerable<WebsiteAnimationBinding>? source)
    {
        var clean = new List<WebsiteAnimationBinding>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in source ?? [])
        {
            if (item is null || clean.Count >= 8 || !Guid.TryParseExact(item.Id, "N", out _) || !ids.Add(item.Id)) continue;
            var trigger = (item.Trigger ?? "").Trim().ToLowerInvariant();
            var effect = (item.Effect ?? "").Trim().ToLowerInvariant();
            if (trigger is not ("load" or "view" or "hover" or "click")) continue;
            if (effect is not ("fade" or "slide-up" or "slide-down" or "slide-left" or "slide-right" or "scale" or "rotate")) continue;
            var easing = (item.Easing ?? "ease").Trim().ToLowerInvariant();
            if (easing is not ("linear" or "ease" or "ease-in" or "ease-out" or "ease-in-out")) easing = "ease";
            clean.Add(new WebsiteAnimationBinding
            {
                Id = item.Id, Trigger = trigger, Effect = effect,
                DurationMs = Math.Clamp(item.DurationMs, 50, 5000), DelayMs = Math.Clamp(item.DelayMs, 0, 5000),
                DistancePx = item.DistancePx is >= -2000 and <= 2000 ? item.DistancePx : null,
                Easing = easing, Once = item.Once
            });
        }
        return clean;
    }

    private static WebsitePageNavigation SanitizeNavigation(string pagePath, WebsitePageNavigation? source, string? pageTitle)
    {
        source ??= new WebsitePageNavigation();
        var parent = SanitizePagePath(source.ParentPath);
        if (string.Equals(parent, pagePath, StringComparison.Ordinal)) parent = null;
        var label = ClampText(source.Label);
        if (source.ShowInNavigation && !source.IsDeleted && string.IsNullOrWhiteSpace(label))
            label = DefaultNavigationLabel(pagePath, pageTitle);
        if (label?.Length > 120) label = label[..120];
        return new WebsitePageNavigation { Label = label, ShowInNavigation = source.ShowInNavigation, ParentPath = parent, Order = Math.Clamp(source.Order, -10000, 10000), IsDeleted = source.IsDeleted };
    }

    private static string DefaultNavigationLabel(string pagePath, string? pageTitle)
    {
        var title = ClampText(pageTitle);
        if (!string.IsNullOrWhiteSpace(title))
            return title.Length <= 120 ? title : title[..120];

        if (string.Equals(pagePath, "/", StringComparison.Ordinal))
            return "Home";

        var segment = pagePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "Page";
        try { segment = Uri.UnescapeDataString(segment); } catch (UriFormatException) { }
        segment = segment.Replace('-', ' ').Replace('_', ' ').Trim();
        if (segment.Length == 0) return "Page";
        var words = segment.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Length == 1
                ? word.ToUpperInvariant()
                : char.ToUpperInvariant(word[0]) + word[1..])
            .ToArray();
        var label = string.Join(' ', words);
        return label.Length <= 120 ? label : label[..120];
    }

    private static WebsiteDynamicPageBinding? SanitizeDynamicBinding(WebsiteDynamicPageBinding? source)
    {
        if (source is null) return null;
        var collectionId = SanitizeId(source.CollectionId);
        var itemKeyField = SanitizeId(source.ItemKeyField);
        var routePattern = SanitizeDynamicRoutePattern(source.RoutePattern);
        return collectionId.Length == 0 || itemKeyField.Length == 0 ? null : new WebsiteDynamicPageBinding
        {
            CollectionId = collectionId,
            ItemKeyField = itemKeyField,
            RoutePattern = routePattern
        };
    }

    private static WebsiteReusableComponentDefinition? SanitizeReusableComponent(WebsiteReusableComponentDefinition source, HashSet<string> breakpointKeys, string id)
    {
        var kind = (source.Kind ?? "").Trim().ToLowerInvariant();
        if (kind is not ("section" or "block")) return null;
        var name = ClampText(source.Name) ?? id;
        if (name.Length > 120) name = name[..120];
        return new WebsiteReusableComponentDefinition
        {
            Id = id,
            Name = name,
            Kind = kind,
            Composition = SanitizeComposition(source.Composition, breakpointKeys)
        };
    }

    private static LegacyWebsiteReusableComponentDefinition? SanitizeLegacyReusableComponent(
        LegacyWebsiteReusableComponentDefinition source,
        HashSet<string> breakpointKeys,
        string id)
    {
        var kind = (source.Kind ?? "").Trim().ToLowerInvariant();
        if (kind is not ("section" or "block")) return null;
        var body = SanitizeLegacyPageBody(new LegacyWebsitePageDocument
        {
            Elements = source.Elements,
            SectionOrder = source.SectionOrder,
            Extras = source.Extras
        }, breakpointKeys);
        body.Extras.RemoveAll(extra => extra.Type == "reusable");
        var name = ClampText(source.Name) ?? id;
        if (name.Length > 120) name = name[..120];
        return new LegacyWebsiteReusableComponentDefinition
        {
            Id = id,
            Name = name,
            Kind = kind,
            Elements = body.Elements,
            SectionOrder = body.SectionOrder,
            Extras = body.Extras
        };
    }

    private static WebsiteCollectionDefinition? SanitizeCollection(WebsiteCollectionDefinition source, string id)
    {
        var sourceKey = (source.Source ?? string.Empty).Trim().ToLowerInvariant();
        if (!WebsiteCollectionSourcePolicy.TryGet(sourceKey, out var sourceDefinition)) return null;
        var fields = (source.Fields ?? []).Where(sourceDefinition.Fields.Contains).Distinct(StringComparer.Ordinal).Take(20).ToList();
        if (fields.Count == 0) return null;
        var name = ClampText(source.Name) ?? id;
        if (name.Length > 120) name = name[..120];
        return new WebsiteCollectionDefinition { Id = id, Name = name, Source = sourceKey, Fields = fields };
    }

    private static WebsiteDataBinding? SanitizeDataBinding(WebsiteDataBinding? source)
    {
        if (source is null) return null;
        var collectionId = SanitizeId(source.CollectionId);
        var field = SanitizeId(source.Field);
        var target = (source.Target ?? "text").Trim().ToLowerInvariant();
        if (target is not ("text" or "image" or "href")) target = "text";
        return collectionId.Length == 0 || field.Length == 0 ? null : new WebsiteDataBinding
        {
            CollectionId = collectionId,
            Field = field,
            Target = target
        };
    }

    private static string SanitizeStoreLabel(string? value)
    {
        var label = (ClampText(value) ?? "Store").Trim();
        if (label.Length == 0) label = "Store";
        if (label.Length > 40) label = label[..40];
        return label;
    }

    private static string SanitizeCartIcon(string? value)
    {
        var icon = (value ?? "cart").Trim().ToLowerInvariant();
        return icon is "cart" or "bag" or "basket" ? icon : "cart";
    }

    private static string? SanitizeDynamicRoutePattern(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var pattern = value.Trim().ToLowerInvariant();
        if (pattern.Length > 160 || !pattern.StartsWith('/') || pattern.StartsWith("//") ||
            pattern.Contains('?') || pattern.Contains('#') || pattern.Contains("..") || pattern.Contains('\\') ||
            pattern.Count(c => c == '{') != 1 || pattern.Count(c => c == '}') != 1 ||
            !pattern.Contains("{item}", StringComparison.Ordinal) ||
            pattern.Replace("{item}", "sample").Any(c => char.IsControl(c)) ||
            !System.Text.RegularExpressions.Regex.IsMatch(pattern.Replace("{item}", "sample"), @"^/(?:[a-z0-9_-]+/?)*$"))
            return null;
        return pattern;
    }

    private static string? SanitizePagePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var path = value.Trim();
        if (!path.StartsWith('/') || path.StartsWith("//") || path.Contains('?') || path.Contains('#') || path.Contains("..") || path.Contains('\\') || path.Any(char.IsControl) || path.Length > 2048) return null;
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
    private static WebsiteDesignTheme SanitizeTheme(WebsiteDesignTheme? source)
    {
        source ??= new WebsiteDesignTheme();
        return new WebsiteDesignTheme
        {
            Navy = SanitizeHex(source.Navy),
            NavyDeep = SanitizeHex(source.NavyDeep),
            Gold = SanitizeHex(source.Gold),
            GoldStrong = SanitizeHex(source.GoldStrong),
            Surface = SanitizeHex(source.Surface),
            Muted = SanitizeHex(source.Muted),
            Text = SanitizeHex(source.Text), FontFamily = SanitizeFont(source.FontFamily),
            FontSize = source.FontSize > 0 ? source.FontSize : null,
            BorderRadius = source.BorderRadius >= 0 ? source.BorderRadius : null
        };
    }

    private static string? SanitizeHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim();
        if (candidate.Length != 7 || candidate[0] != '#') return null;
        for (var i = 1; i < candidate.Length; i++)
        {
            if (!Uri.IsHexDigit(candidate[i])) return null;
        }
        return candidate.ToLowerInvariant();
    }

    private static WebsiteVisualStyle SanitizeStyle(WebsiteVisualStyle? source)
    {
        source ??= new WebsiteVisualStyle();
        var align = (source.TextAlign ?? string.Empty).Trim().ToLowerInvariant();
        if (align is not ("left" or "center" or "right" or "start" or "end" or "justify")) align = string.Empty;
        var objectPosition = (source.ObjectPosition ?? string.Empty).Trim().ToLowerInvariant();
        if (objectPosition is not ("left" or "center" or "right" or "top" or "bottom"))
            objectPosition = string.Empty;

        return new WebsiteVisualStyle
        {
            TextAlign = align.Length == 0 ? null : align,
            FontScale = source.FontScale > 0 ? source.FontScale : null,
            WidthPercent = source.WidthPercent > 0 ? source.WidthPercent : null,
            PaddingTop = source.PaddingTop >= 0 ? source.PaddingTop : null,
            PaddingBottom = source.PaddingBottom >= 0 ? source.PaddingBottom : null,
            ObjectPosition = objectPosition.Length == 0 ? null : objectPosition,
            Color = SanitizeHex(source.Color), BackgroundColor = SanitizeHex(source.BackgroundColor),
            FontFamily = SanitizeFont(source.FontFamily),
            FontWeight = source.FontWeight is >= 100 and <= 900 ? source.FontWeight : null,
            FontSize = source.FontSize > 0 ? source.FontSize : null,
            LineHeight = source.LineHeight > 0 ? source.LineHeight : null,
            LetterSpacing = source.LetterSpacing,
            PaddingLeft = source.PaddingLeft >= 0 ? source.PaddingLeft : null,
            PaddingRight = source.PaddingRight >= 0 ? source.PaddingRight : null,
            BorderRadius = source.BorderRadius >= 0 ? source.BorderRadius : null,
            ObjectFit = source.ObjectFit is "cover" or "contain" or "fill" or "none" or "scale-down" ? source.ObjectFit : null,
            HeightPx = source.HeightPx > 0 ? source.HeightPx : null,
            OffsetXPercent = source.OffsetXPercent,
            OffsetYPx = source.OffsetYPx
        };
    }

    private static string? ClampContentText(string? value)
    {
        if (value is null) return null;
        var normalized = value.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\0", string.Empty);
        var filtered = new string(normalized.Where(ch => ch is '\n' or '\t' || !char.IsControl(ch)).ToArray());
        return filtered.Length <= MaxTextLength
            ? filtered
            : filtered[..MaxTextLength];
    }

    private static string? ClampCodeText(string? value)
    {
        if (value is null) return null;
        var normalized = value.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\0", string.Empty);
        var filtered = new string(normalized.Where(ch => ch is '\n' or '\t' || !char.IsControl(ch)).ToArray());
        return filtered.Length <= MaxCodeLength ? filtered : filtered[..MaxCodeLength];
    }

    private static string? ClampText(string? value)
    {
        if (value is null) return null;
        var normalized = value.Replace("\0", string.Empty).Trim();
        return normalized.Length <= MaxTextLength
            ? normalized
            : normalized[..MaxTextLength];
    }

    private static string? SanitizeImage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        if (normalized.Length > MaxImageDataUrlLength) return null;
        if (normalized.StartsWith("data:image/jpeg;base64,", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("data:image/png;base64,", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("data:image/webp;base64,", StringComparison.OrdinalIgnoreCase))
            return normalized;
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps)
            return normalized;
        return null;
    }

    private static string? SanitizeActionKey(string? value)
    {
        var key = SanitizeId(value);
        return key.Length == 0 ? null : key;
    }

    private static string SanitizeId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var chars = value.Trim().Take(160)
            .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ':')
            .ToArray();
        return new string(chars);
    }
    private static string? SanitizeTarget(string? value) => value is "_blank" or "_self" ? value : null;
    private static string? SanitizeFont(string? value) => value is "inherit" or "system-ui" or "serif" or "sans-serif" or "monospace" or "Georgia" or "Arial" ? value : null;
    private static string? SanitizeCompositionMediaUrl(string? value, string type)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var url = value.Trim();
        if (url.Length > 2048 || url.Any(char.IsControl) || url.Contains('\\') ||
            url.Contains("legendEdit=", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("ticket=", StringComparison.OrdinalIgnoreCase))
            return null;

        // Existing first-party/static template assets must survive the one-time
        // pre-v3 -> v3 materialization exactly. New/changed user media is separately
        // required to use an owner-scoped WebsiteMediaAsset ID by Site Source.
        if (url.StartsWith('/') && !url.StartsWith("//")) return url;

        if (type == "image")
        {
            var inline = SanitizeImage(url);
            if (inline is not null) return inline;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        return uri.Scheme == Uri.UriSchemeHttps ? url : null;
    }

    public static string? SanitizeUrl(string? value, bool media = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var url = value.Trim();
        if (url.Contains("legendEdit=", StringComparison.OrdinalIgnoreCase) || url.Contains("ticket=", StringComparison.OrdinalIgnoreCase)) return null;
        if (url.Length > 2048 || url.Any(char.IsControl) || url.Contains('\\')) return null;
        if (!media && (url.StartsWith('#') || (url.StartsWith('/') && !url.StartsWith("//")))) return url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        return uri.Scheme == "https" || (!media && uri.Scheme is "mailto" or "tel") ? url : null;
    }
    private static LegacyWebsitePlacement? SanitizePlacement(LegacyWebsitePlacement? value)
    {
        if (value is null) return null;
        var section = SanitizeId(value.SectionId);
        if (section.Length == 0) return null;
        var column = Math.Clamp(value.Column, 1, 12);
        return new LegacyWebsitePlacement { SectionId = section, BeforeId = SanitizeId(value.BeforeId), ContainerId = SanitizeId(value.ContainerId), Flow = value.Flow, Column = column, Span = Math.Clamp(value.Span, 1, 13 - column) };
    }
}
