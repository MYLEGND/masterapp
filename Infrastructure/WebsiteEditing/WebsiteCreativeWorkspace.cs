using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Infrastructure.WebsiteEditing;

public sealed record WebsiteCapabilityDescriptor(
    string Key,
    string Kind,
    string Label,
    string Purpose,
    bool Protected,
    string? ActionKey = null,
    string? NodeId = null,
    string? PagePath = null,
    IReadOnlyList<string>? AllowedPlacements = null,
    string? RuntimeAction = null);

public sealed record WebsiteCapabilityManifest(
    string Schema,
    string SiteKey,
    string OwnerKey,
    string Hash,
    IReadOnlyList<WebsiteCapabilityDescriptor> Capabilities);

public static class WebsiteCreativeCapabilityResolver
{
    public const string Schema = "legend-website-capabilities/v1";

    public static WebsiteCapabilityManifest Resolve(
        string siteKey,
        string ownerKey,
        WebsiteContentDocument document,
        IReadOnlyList<WebsiteCallToActionOption> actions)
    {
        var capabilities = new List<WebsiteCapabilityDescriptor>();
        foreach (var action in actions.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            capabilities.Add(new(
                "action." + action.Key,
                "action",
                action.Label,
                action.DefaultText,
                true,
                ActionKey: action.Key,
                AllowedPlacements: ["cta", "link", "experience_cta"],
                RuntimeAction: action.RuntimeAction));
        }

        var hasCanonicalInquiry = WebsiteSiteSource.Flatten(document)
            .Any(entry => entry.Node.Type == "form" &&
                          string.Equals(entry.Node.SystemKey, "canonical_inquiry", StringComparison.Ordinal));
        if ((siteKey is WebsiteEditorSiteKeys.Business or WebsiteEditorSiteKeys.Legend) || hasCanonicalInquiry)
        {
            capabilities.Add(new(
                "contact.inquiry.submit",
                "protected_form",
                "Canonical inquiry",
                "Collect contact details through the canonical website inquiry authority.",
                true,
                AllowedPlacements: ["section", "container"]));
        }

        capabilities.Add(new(
            "experience.lead_capture",
            "experience_submit",
            "Canonical lead capture",
            "Submit an authorable native experience through the server-owned scoped lead-capture authority.",
            true,
            AllowedPlacements: ["experience"]));

        foreach (var entry in WebsiteSiteSource.Flatten(document))
        {
            if (WebsiteSystemTemplateAuthority.IsRuntimeFormSystemKey(entry.Node.SystemKey))
            {
                capabilities.Add(new(
                    "runtime." + entry.Node.SystemKey,
                    "protected_runtime",
                    "Protected runtime form",
                    "Use the existing server-owned Protect runtime while freely designing around it.",
                    true,
                    NodeId: entry.Node.Id,
                    PagePath: entry.PagePath,
                    AllowedPlacements: ["same_page_section", "same_page_container"]));
            }
        }

        var distinct = capabilities
            .GroupBy(value => value.Key + "|" + value.NodeId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(value => value.Key, StringComparer.Ordinal)
            .ThenBy(value => value.PagePath, StringComparer.Ordinal)
            .ToArray();
        var hash = WebsiteCreativeFingerprint.For(distinct);
        return new(Schema, siteKey, ownerKey, hash, distinct);
    }

    public static WebsiteCapabilityDescriptor Require(
        WebsiteCapabilityManifest manifest,
        string? key,
        string? nodeId = null)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("A canonical capability key is required.");
        var match = manifest.Capabilities.FirstOrDefault(value =>
            string.Equals(value.Key, key, StringComparison.Ordinal) &&
            (nodeId is null || string.Equals(value.NodeId, nodeId, StringComparison.Ordinal)));
        return match ?? throw new ArgumentException($"Capability '{key}' is not available in this website scope.");
    }
}

public static class WebsiteCreativeFingerprint
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static string For<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string Node(WebsiteCompositionNode node) => For(WebsiteCreativeProjection.Node(node));
    public static string Page(WebsitePageDocument page) => For(WebsiteCreativeProjection.Page(page));
}

public sealed record WebsiteCreativeNodeLocation(
    string Scope,
    string? PagePath,
    string? ReusableComponentId,
    string? ParentId,
    int Index);

public static class WebsiteDocumentIndex
{
    public static bool TryFind(
        WebsiteContentDocument document,
        string nodeId,
        out WebsiteCompositionNode node,
        out WebsiteCreativeNodeLocation location)
    {
        if (TryFindInList(document.Shell.Header, nodeId, "shell.header", null, null, null, out node, out location))
            return true;
        if (TryFindInList(document.Shell.Footer, nodeId, "shell.footer", null, null, null, out node, out location))
            return true;

        foreach (var (path, page) in document.Pages)
            if (TryFindInList(page.Composition, nodeId, "page", path, null, null, out node, out location))
                return true;

        foreach (var (id, component) in document.ReusableComponents)
            if (TryFindInList(component.Composition, nodeId, "component", null, id, null, out node, out location))
                return true;

        node = null!;
        location = null!;
        return false;
    }

    public static List<WebsiteCompositionNode> ResolveChildren(
        WebsiteContentDocument document,
        string scope,
        string? pagePath,
        string? reusableComponentId,
        string? parentId)
    {
        if (!string.IsNullOrWhiteSpace(parentId))
        {
            if (!TryFind(document, parentId, out var parent, out _))
                throw new ArgumentException($"Parent node '{parentId}' was not found.");
            return parent.Children;
        }

        return scope switch
        {
            "shell.header" => document.Shell.Header,
            "shell.footer" => document.Shell.Footer,
            "page" when pagePath is not null && document.Pages.TryGetValue(pagePath, out var page) => page.Composition,
            "component" when reusableComponentId is not null && document.ReusableComponents.TryGetValue(reusableComponentId, out var component) => component.Composition,
            _ => throw new ArgumentException("A valid mutation scope is required.")
        };
    }

    public static bool Remove(
        WebsiteContentDocument document,
        string nodeId,
        out WebsiteCompositionNode removed,
        out WebsiteCreativeNodeLocation location)
    {
        if (!TryFind(document, nodeId, out removed, out location)) return false;
        var siblings = ResolveChildren(document, location.Scope, location.PagePath, location.ReusableComponentId, location.ParentId);
        var index = siblings.FindIndex(value => value.Id == nodeId);
        if (index < 0) return false;
        siblings.RemoveAt(index);
        return true;
    }

    private static bool TryFindInList(
        List<WebsiteCompositionNode> nodes,
        string nodeId,
        string scope,
        string? pagePath,
        string? reusableComponentId,
        string? parentId,
        out WebsiteCompositionNode node,
        out WebsiteCreativeNodeLocation location)
    {
        for (var index = 0; index < nodes.Count; index++)
        {
            var candidate = nodes[index];
            if (string.Equals(candidate.Id, nodeId, StringComparison.Ordinal))
            {
                node = candidate;
                location = new(scope, pagePath, reusableComponentId, parentId, index);
                return true;
            }

            if (TryFindInList(candidate.Children, nodeId, scope, pagePath, reusableComponentId, candidate.Id, out node, out location))
                return true;
        }

        node = null!;
        location = null!;
        return false;
    }
}

public static class WebsiteCreativeProjection
{
    public static object Node(WebsiteCompositionNode node) => new
    {
        node.Id,
        node.Type,
        node.Tag,
        node.ClassName,
        node.Text,
        node.Title,
        node.ActionKey,
        node.Href,
        node.Target,
        node.Alt,
        node.MediaAssetId,
        node.MediaUrl,
        node.VideoLoop,
        node.Hidden,
        node.Style,
        node.BreakpointStyles,
        node.Layout,
        node.BreakpointLayouts,
        node.Animations,
        node.DataBinding,
        node.Experience,
        node.FieldPresentations,
        node.FieldLabels,
        Children = node.Children.Select(Node).ToArray()
    };

    public static object Page(WebsitePageDocument page) => new
    {
        page.Title,
        page.Description,
        page.Navigation,
        page.DynamicBinding,
        page.SystemTemplateKey,
        Composition = page.Composition.Select(Node).ToArray()
    };

    public static object SiteSummary(
        string siteKey,
        long revision,
        WebsiteContentDocument document,
        object? facts,
        WebsiteCapabilityManifest capabilities,
        WebsiteQualityReport quality,
        IEnumerable<object>? media = null)
    {
        var pages = document.Pages
            .OrderBy(pair => pair.Value.Navigation?.Order ?? 0)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new
            {
                path = pair.Key,
                pair.Value.Title,
                pair.Value.Description,
                navigation = pair.Value.Navigation,
                systemTemplateKey = pair.Value.SystemTemplateKey,
                sectionCount = pair.Value.Composition.Count,
                sections = pair.Value.Composition.Select(node => new
                {
                    node.Id,
                    node.Type,
                    node.ClassName,
                    label = node.Title ?? node.Text,
                    fingerprint = WebsiteCreativeFingerprint.Node(node)
                }).ToArray(),
                fingerprint = WebsiteCreativeFingerprint.Page(pair.Value)
            }).ToArray();

        return new
        {
            schema = "legend-creative-workspace/v1",
            siteKey,
            revision,
            facts,
            theme = document.Theme,
            pages,
            shell = new
            {
                header = document.Shell.Header.Select(node => new { node.Id, node.Type, node.ClassName }).ToArray(),
                footer = document.Shell.Footer.Select(node => new { node.Id, node.Type, node.ClassName }).ToArray()
            },
            reusableComponents = document.ReusableComponents.Values
                .Select(value => new { value.Id, value.Name, value.Kind })
                .OrderBy(value => value.Name, StringComparer.Ordinal)
                .ToArray(),
            capabilities,
            quality = new { quality.ErrorCount, quality.WarningCount, quality.Checks },
            media = media ?? Array.Empty<object>()
        };
    }
}

public sealed class WebsiteMutationOperation
{
    public string Type { get; set; } = string.Empty;
    public string? Scope { get; set; }
    public string? PagePath { get; set; }
    public string? ReusableComponentId { get; set; }
    public string? NodeId { get; set; }
    public string? ParentId { get; set; }
    public int? Index { get; set; }
    public string? ExpectedFingerprint { get; set; }
    public string? TargetPath { get; set; }
    public string? CapabilityKey { get; set; }
    public string? RecipeKey { get; set; }
    public string? InstanceKey { get; set; }
    public WebsiteCompositionNode? Node { get; set; }
    public WebsitePageDocument? Page { get; set; }
    public WebsitePageNavigation? Navigation { get; set; }
    public WebsiteDesignTheme? Theme { get; set; }
    public WebsiteStoreSettings? Store { get; set; }
    public List<WebsiteBreakpointDefinition>? Breakpoints { get; set; }
    public WebsiteReusableComponentDefinition? ReusableComponent { get; set; }
    public string? Text { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? FaviconImageDataUrl { get; set; }
    public Guid? MediaAssetId { get; set; }
    public Dictionary<string, string>? Content { get; set; }
}

public sealed record WebsiteMutationApplyResult(
    WebsiteContentDocument Document,
    IReadOnlyList<string> ChangedScopes,
    IReadOnlyDictionary<string, string> Fingerprints);

public static class WebsiteDocumentMutationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static WebsiteMutationApplyResult Apply(
        WebsiteContentDocument baseline,
        string siteKey,
        IReadOnlyList<WebsiteCallToActionOption> actions,
        WebsiteCapabilityManifest capabilities,
        IEnumerable<WebsiteMutationOperation>? operations)
    {
        if (baseline.LegacyMigration is not null)
            throw new InvalidOperationException("website_materialization_required");

        var document = Clone(WebsiteContentSanitizer.Sanitize(baseline));
        WebsiteSystemTemplateAuthority.Apply(siteKey, document);
        var changed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var operation in operations ?? [])
        {
            if (operation is null) throw new ArgumentException("Website mutation entries cannot be null.");
            ApplyOne(document, siteKey, actions, capabilities, operation, changed);
        }

        var proposed = Clone(document);
        document = WebsiteContentSanitizer.Sanitize(document);
        WebsiteSystemTemplateAuthority.Apply(siteKey, document);
        WebsiteSystemTemplateAuthority.RestoreRuntimeForms(siteKey, proposed, document);
        WebsiteSiteSource.ValidateCanonical(document, actions);

        var fingerprints = changed
            .Select(scope => (scope, hash: FingerprintScope(document, scope)))
            .Where(value => value.hash is not null)
            .ToDictionary(value => value.scope, value => value.hash!, StringComparer.Ordinal);

        return new(document, changed.OrderBy(value => value, StringComparer.Ordinal).ToArray(), fingerprints);
    }

    private static void ApplyOne(
        WebsiteContentDocument document,
        string siteKey,
        IReadOnlyList<WebsiteCallToActionOption> actions,
        WebsiteCapabilityManifest capabilities,
        WebsiteMutationOperation operation,
        HashSet<string> changed)
    {
        switch (operation.Type)
        {
            case "setTheme":
                document.Theme = operation.Theme ?? new WebsiteDesignTheme();
                changed.Add("@theme");
                return;

            case "setFavicon":
                document.FaviconImageDataUrl = operation.FaviconImageDataUrl;
                changed.Add("@favicon");
                return;

            case "setBreakpoints":
                document.Breakpoints = operation.Breakpoints ?? WebsiteStudioContract.DefaultBreakpoints();
                changed.Add("@breakpoints");
                return;

            case "setStorePresentation":
                if (operation.Store is null) throw new ArgumentException("Store presentation is required.");
                document.Store.NavigationLabel = operation.Store.NavigationLabel;
                document.Store.CartIcon = operation.Store.CartIcon;
                document.Store.CartIconSizePx = operation.Store.CartIconSizePx;
                document.Store.StoreNavigation = operation.Store.StoreNavigation;
                document.Store.CartNavigation = operation.Store.CartNavigation;
                changed.Add("@store-presentation");
                return;

            case "createPage":
                CreatePage(document, siteKey, operation, actions, changed);
                return;

            case "updatePage":
                UpdatePage(document, operation, changed);
                return;

            case "movePageRoute":
                MovePage(document, siteKey, operation, changed);
                return;

            case "removePage":
                RemovePage(document, siteKey, operation, changed);
                return;

            case "replaceShellHeader":
                document.Shell.Header = PrepareNewNodes(operation.Node?.Children ?? [], actions);
                changed.Add("@shell/header");
                return;

            case "replaceShellFooter":
                document.Shell.Footer = PrepareNewNodes(operation.Node?.Children ?? [], actions);
                changed.Add("@shell/footer");
                return;

            case "upsertReusable":
                UpsertReusable(document, operation, actions, changed);
                return;

            case "removeReusable":
                RemoveReusable(document, operation, changed);
                return;

            case "insertNode":
                InsertNode(document, operation, actions, changed);
                return;

            case "replaceNode":
                ReplaceNode(document, operation, actions, changed);
                return;

            case "removeNode":
                RemoveNode(document, operation, changed);
                return;

            case "moveNode":
                MoveNode(document, operation, changed);
                return;

            case "setApprovedCapability":
                SetApprovedCapability(document, operation, capabilities, actions, changed);
                return;

            case "insertCapability":
                InsertCapability(document, operation, capabilities, actions, changed);
                return;

            case "insertRecipe":
                InsertRecipe(document, operation, capabilities, actions, changed);
                return;

            default:
                throw new ArgumentException($"Unsupported website mutation '{operation.Type}'.");
        }
    }

    private static void CreatePage(
        WebsiteContentDocument document,
        string siteKey,
        WebsiteMutationOperation operation,
        IReadOnlyList<WebsiteCallToActionOption> actions,
        HashSet<string> changed)
    {
        var path = NormalizePath(operation.PagePath);
        if (path == "/") throw new ArgumentException("The home page already exists.");
        if (siteKey != WebsiteEditorSiteKeys.Business)
            throw new WebsiteSiteSourceProtectionException("Arbitrary route creation is available only for scoped Business websites.");
        if (document.Pages.ContainsKey(path))
            throw new ArgumentException($"Page '{path}' already exists.");
        var page = operation.Page ?? new WebsitePageDocument();
        page.SystemTemplateKey = null;
        page.Navigation ??= new WebsitePageNavigation();
        page.Navigation.IsDeleted = false;
        page.Composition = PrepareNewNodes(page.Composition, actions);
        document.Pages[path] = page;
        changed.Add(path);
    }

    private static void UpdatePage(
        WebsiteContentDocument document,
        WebsiteMutationOperation operation,
        HashSet<string> changed)
    {
        var path = NormalizePath(operation.PagePath);
        if (!document.Pages.TryGetValue(path, out var page))
            throw new ArgumentException($"Page '{path}' was not found.");
        VerifyFingerprint(operation.ExpectedFingerprint, WebsiteCreativeFingerprint.Page(page), "page", path);
        if (operation.Page is not null)
        {
            page.Title = operation.Page.Title;
            page.Description = operation.Page.Description;
            page.Navigation = operation.Page.Navigation ?? new WebsitePageNavigation();
            page.DynamicBinding = operation.Page.DynamicBinding;
        }
        else
        {
            if (operation.Title is not null) page.Title = operation.Title;
            if (operation.Description is not null) page.Description = operation.Description;
            if (operation.Navigation is not null) page.Navigation = operation.Navigation;
        }
        changed.Add(path);
    }

    private static void MovePage(
        WebsiteContentDocument document,
        string siteKey,
        WebsiteMutationOperation operation,
        HashSet<string> changed)
    {
        if (siteKey != WebsiteEditorSiteKeys.Business)
            throw new WebsiteSiteSourceProtectionException("Route moves are available only for scoped Business websites.");
        var source = NormalizePath(operation.PagePath);
        var target = NormalizePath(operation.TargetPath);
        if (source == "/" || target == "/") throw new ArgumentException("The home page route cannot be moved.");
        if (!document.Pages.Remove(source, out var page)) throw new ArgumentException($"Page '{source}' was not found.");
        if (document.Pages.ContainsKey(target)) throw new ArgumentException($"Page '{target}' already exists.");
        if (page.SystemTemplateKey is not null) throw new WebsiteSiteSourceProtectionException("System template routes cannot be moved.");
        document.Pages[target] = page;
        changed.Add(source);
        changed.Add(target);
    }

    private static void RemovePage(
        WebsiteContentDocument document,
        string siteKey,
        WebsiteMutationOperation operation,
        HashSet<string> changed)
    {
        var path = NormalizePath(operation.PagePath);
        if (path == "/") throw new ArgumentException("The home page cannot be removed.");
        if (!document.Pages.TryGetValue(path, out var page)) throw new ArgumentException($"Page '{path}' was not found.");
        if (page.SystemTemplateKey is not null || siteKey != WebsiteEditorSiteKeys.Business)
            throw new WebsiteSiteSourceProtectionException("Protected or fixed-route pages cannot be removed from Website Studio.");
        page.Navigation.IsDeleted = true;
        changed.Add(path);
    }

    private static void InsertNode(
        WebsiteContentDocument document,
        WebsiteMutationOperation operation,
        IReadOnlyList<WebsiteCallToActionOption> actions,
        HashSet<string> changed)
    {
        if (operation.Node is null) throw new ArgumentException("A node is required.");
        if (WebsiteDocumentIndex.TryFind(document, operation.Node.Id, out _, out _))
            throw new ArgumentException($"Node '{operation.Node.Id}' already exists.");
        var node = PrepareNewNode(operation.Node, actions);
        var siblings = WebsiteDocumentIndex.ResolveChildren(
            document,
            operation.Scope ?? "page",
            operation.PagePath,
            operation.ReusableComponentId,
            operation.ParentId);
        var index = Math.Clamp(operation.Index ?? siblings.Count, 0, siblings.Count);
        siblings.Insert(index, node);
        changed.Add(ScopeKey(operation.Scope ?? "page", operation.PagePath, operation.ReusableComponentId, operation.ParentId ?? node.Id));
    }

    private static void ReplaceNode(
        WebsiteContentDocument document,
        WebsiteMutationOperation operation,
        IReadOnlyList<WebsiteCallToActionOption> actions,
        HashSet<string> changed)
    {
        if (operation.Node is null || string.IsNullOrWhiteSpace(operation.NodeId))
            throw new ArgumentException("Node identity and replacement are required.");
        if (!WebsiteDocumentIndex.TryFind(document, operation.NodeId, out var existing, out var location))
            throw new ArgumentException($"Node '{operation.NodeId}' was not found.");
        VerifyFingerprint(operation.ExpectedFingerprint, WebsiteCreativeFingerprint.Node(existing), "node", existing.Id);
        var replacement = MergeAuthorable(existing, operation.Node, actions);
        var siblings = WebsiteDocumentIndex.ResolveChildren(document, location.Scope, location.PagePath, location.ReusableComponentId, location.ParentId);
        siblings[location.Index] = replacement;
        changed.Add(ScopeKey(location.Scope, location.PagePath, location.ReusableComponentId, replacement.Id));
    }

    private static void RemoveNode(
        WebsiteContentDocument document,
        WebsiteMutationOperation operation,
        HashSet<string> changed)
    {
        if (string.IsNullOrWhiteSpace(operation.NodeId))
            throw new ArgumentException("Node identity is required.");
        if (!WebsiteDocumentIndex.TryFind(document, operation.NodeId, out var existing, out var location))
            throw new ArgumentException($"Node '{operation.NodeId}' was not found.");
        VerifyFingerprint(operation.ExpectedFingerprint, WebsiteCreativeFingerprint.Node(existing), "node", existing.Id);
        if (HasProtectedSemantics(existing))
            throw new WebsiteSiteSourceProtectionException($"Protected component '{existing.Id}' cannot be removed by the creative mutation authority.");
        WebsiteDocumentIndex.Remove(document, existing.Id, out _, out _);
        changed.Add(ScopeKey(location.Scope, location.PagePath, location.ReusableComponentId, location.ParentId ?? existing.Id));
    }

    private static void MoveNode(
        WebsiteContentDocument document,
        WebsiteMutationOperation operation,
        HashSet<string> changed)
    {
        if (string.IsNullOrWhiteSpace(operation.NodeId))
            throw new ArgumentException("Node identity is required.");
        if (!WebsiteDocumentIndex.TryFind(document, operation.NodeId, out var existing, out var oldLocation))
            throw new ArgumentException($"Node '{operation.NodeId}' was not found.");
        VerifyFingerprint(operation.ExpectedFingerprint, WebsiteCreativeFingerprint.Node(existing), "node", existing.Id);

        if (HasProtectedSemantics(existing) &&
            (!string.Equals(oldLocation.PagePath, operation.PagePath, StringComparison.Ordinal) ||
             oldLocation.Scope != (operation.Scope ?? oldLocation.Scope)))
            throw new WebsiteSiteSourceProtectionException("Protected runtime components may be repositioned only within their existing page/scope.");

        if (!WebsiteDocumentIndex.Remove(document, existing.Id, out var removed, out _))
            throw new ArgumentException("The component could not be moved.");
        var siblings = WebsiteDocumentIndex.ResolveChildren(
            document,
            operation.Scope ?? oldLocation.Scope,
            operation.PagePath ?? oldLocation.PagePath,
            operation.ReusableComponentId ?? oldLocation.ReusableComponentId,
            operation.ParentId);
        var index = Math.Clamp(operation.Index ?? siblings.Count, 0, siblings.Count);
        siblings.Insert(index, removed);
        changed.Add(ScopeKey(oldLocation.Scope, oldLocation.PagePath, oldLocation.ReusableComponentId, existing.Id));
        changed.Add(ScopeKey(operation.Scope ?? oldLocation.Scope, operation.PagePath ?? oldLocation.PagePath, operation.ReusableComponentId ?? oldLocation.ReusableComponentId, operation.ParentId ?? existing.Id));
    }

    private static void SetApprovedCapability(
        WebsiteContentDocument document,
        WebsiteMutationOperation operation,
        WebsiteCapabilityManifest capabilities,
        IReadOnlyList<WebsiteCallToActionOption> actions,
        HashSet<string> changed)
    {
        if (string.IsNullOrWhiteSpace(operation.NodeId))
            throw new ArgumentException("Node identity is required.");
        var capability = WebsiteCreativeCapabilityResolver.Require(capabilities, operation.CapabilityKey);
        if (!WebsiteDocumentIndex.TryFind(document, operation.NodeId, out var node, out var location))
            throw new ArgumentException($"Node '{operation.NodeId}' was not found.");

        if (capability.Key == "experience.lead_capture")
        {
            if (node.Type != "experience" || node.Experience is null)
                throw new ArgumentException("Canonical lead capture may be assigned only to a native experience.");
            node.Experience.SubmitCapability = WebsiteExperiencePolicy.LeadCaptureCapability;
            changed.Add(ScopeKey(location.Scope, location.PagePath, location.ReusableComponentId, node.Id));
            return;
        }

        if (capability.ActionKey is null)
            throw new ArgumentException("This capability is not an action capability.");
        if (node.Type is not ("cta" or "link"))
            throw new ArgumentException("Approved action capabilities may be assigned only to CTA/link nodes.");
        var action = actions.SingleOrDefault(value => value.Key == capability.ActionKey)
            ?? throw new ArgumentException("The approved action is no longer available.");
        node.ActionKey = action.Key;
        node.Href = action.Href;
        node.Target = action.OpenInNewTab ? "_blank" : "_self";
        changed.Add(ScopeKey(location.Scope, location.PagePath, location.ReusableComponentId, node.Id));
    }

    private static void InsertCapability(
        WebsiteContentDocument document,
        WebsiteMutationOperation operation,
        WebsiteCapabilityManifest capabilities,
        IReadOnlyList<WebsiteCallToActionOption> actions,
        HashSet<string> changed)
    {
        var capability = WebsiteCreativeCapabilityResolver.Require(capabilities, operation.CapabilityKey);
        if (capability.Kind == "action")
        {
            var action = actions.Single(value => value.Key == capability.ActionKey);
            var node = new WebsiteCompositionNode
            {
                Id = operation.InstanceKey ?? FreshId("capability.action"),
                Type = "cta",
                Tag = "a",
                ClassName = "btn primary legend-recipe-action",
                Text = operation.Content?.GetValueOrDefault("label") ?? action.DefaultText,
                ActionKey = action.Key,
                Href = action.Href,
                Target = action.OpenInNewTab ? "_blank" : "_self"
            };
            InsertNode(document, new WebsiteMutationOperation
            {
                Type = "insertNode",
                Scope = operation.Scope,
                PagePath = operation.PagePath,
                ReusableComponentId = operation.ReusableComponentId,
                ParentId = operation.ParentId,
                Index = operation.Index,
                Node = node
            }, actions, changed);
            return;
        }

        if (capability.Key == "contact.inquiry.submit")
        {
            var node = new WebsiteCompositionNode
            {
                Id = operation.InstanceKey ?? FreshId("form.canonical_inquiry"),
                Type = "form",
                Tag = "form",
                ClassName = "public-form legend-canonical-inquiry",
                Text = operation.Content?.GetValueOrDefault("submit") ?? "Send inquiry",
                Title = operation.Content?.GetValueOrDefault("title") ?? "Send an inquiry",
                SystemKey = "canonical_inquiry",
                Style = new WebsiteVisualStyle { WidthPercent = 100 },
                Layout = new WebsiteCompositionLayout { Mode = "stack", Direction = "column", GapPx = 14 }
            };
            if (operation.Node is not null)
                node = MergeAuthorable(node, operation.Node, actions);
            var siblings = WebsiteDocumentIndex.ResolveChildren(
                document,
                operation.Scope ?? "page",
                operation.PagePath,
                operation.ReusableComponentId,
                operation.ParentId);
            var index = Math.Clamp(operation.Index ?? siblings.Count, 0, siblings.Count);
            siblings.Insert(index, node);
            changed.Add(ScopeKey(operation.Scope ?? "page", operation.PagePath, operation.ReusableComponentId, node.Id));
            return;
        }

        if (capability.Kind == "protected_runtime" && capability.NodeId is not null)
        {
            MoveNode(document, new WebsiteMutationOperation
            {
                Type = "moveNode",
                NodeId = capability.NodeId,
                Scope = operation.Scope ?? "page",
                PagePath = operation.PagePath ?? capability.PagePath,
                ParentId = operation.ParentId,
                Index = operation.Index
            }, changed);
            return;
        }

        throw new ArgumentException($"Capability '{capability.Key}' cannot be inserted by the creative workspace.");
    }

    private static void InsertRecipe(
        WebsiteContentDocument document,
        WebsiteMutationOperation operation,
        WebsiteCapabilityManifest capabilities,
        IReadOnlyList<WebsiteCallToActionOption> actions,
        HashSet<string> changed)
    {
        var node = WebsiteRecipeCatalog.Build(
            operation.RecipeKey ?? throw new ArgumentException("Recipe key is required."),
            operation.InstanceKey ?? FreshId("section"),
            operation.Content ?? new Dictionary<string, string>(StringComparer.Ordinal),
            operation.MediaAssetId);
        InsertNode(document, new WebsiteMutationOperation
        {
            Type = "insertNode",
            Scope = operation.Scope,
            PagePath = operation.PagePath,
            ReusableComponentId = operation.ReusableComponentId,
            ParentId = operation.ParentId,
            Index = operation.Index,
            Node = node
        }, actions, changed);

        if (!string.IsNullOrWhiteSpace(operation.CapabilityKey))
        {
            var slot = node.Children.FirstOrDefault(value => value.ClassName?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("legend-capability-slot") == true);
            InsertCapability(document, new WebsiteMutationOperation
            {
                Type = "insertCapability",
                Scope = operation.Scope,
                PagePath = operation.PagePath,
                ReusableComponentId = operation.ReusableComponentId,
                ParentId = slot?.Id ?? node.Id,
                CapabilityKey = operation.CapabilityKey,
                Content = operation.Content,
                InstanceKey = operation.InstanceKey + ".capability"
            }, capabilities, actions, changed);
        }
    }

    private static void UpsertReusable(
        WebsiteContentDocument document,
        WebsiteMutationOperation operation,
        IReadOnlyList<WebsiteCallToActionOption> actions,
        HashSet<string> changed)
    {
        var source = operation.ReusableComponent ?? throw new ArgumentException("Reusable component is required.");
        if (string.IsNullOrWhiteSpace(source.Id)) throw new ArgumentException("Reusable component identity is required.");
        if (source.Composition.Any(ContainsProtectedSemantics))
            throw new WebsiteSiteSourceProtectionException("Protected runtime behavior cannot be copied into reusable creative components.");
        source.Composition = PrepareNewNodes(source.Composition, actions);
        document.ReusableComponents[source.Id] = source;
        changed.Add("@component/" + source.Id);
    }

    private static void RemoveReusable(
        WebsiteContentDocument document,
        WebsiteMutationOperation operation,
        HashSet<string> changed)
    {
        var id = operation.ReusableComponentId ?? throw new ArgumentException("Reusable component identity is required.");
        if (WebsiteSiteSource.Flatten(document).Any(entry => entry.Node.Type == "reusable" && entry.Node.SyncSourceId == id))
            throw new ArgumentException("Remove reusable component instances before deleting the definition.");
        document.ReusableComponents.Remove(id);
        changed.Add("@component/" + id);
    }

    private static WebsiteCompositionNode MergeAuthorable(
        WebsiteCompositionNode current,
        WebsiteCompositionNode proposed,
        IReadOnlyList<WebsiteCallToActionOption> actions)
    {
        var protectedIdentity = HasProtectedSemantics(current);
        var next = Clone(current);

        next.ClassName = proposed.ClassName;
        next.Text = proposed.Text;
        next.Title = proposed.Title;
        next.Alt = proposed.Alt;
        next.MediaAssetId = proposed.MediaAssetId;
        next.MediaUrl = proposed.MediaUrl;
        next.VideoLoop = proposed.VideoLoop;
        next.Hidden = proposed.Hidden;
        next.Style = proposed.Style ?? new WebsiteVisualStyle();
        next.BreakpointStyles = proposed.BreakpointStyles ?? new(StringComparer.Ordinal);
        next.Layout = proposed.Layout ?? new WebsiteCompositionLayout();
        next.BreakpointLayouts = proposed.BreakpointLayouts ?? new(StringComparer.Ordinal);
        next.Animations = proposed.Animations ?? [];
        next.FieldPresentations = proposed.FieldPresentations ?? new(StringComparer.Ordinal);
        next.FieldLabels = proposed.FieldLabels ?? new(StringComparer.Ordinal);

        if (!protectedIdentity)
        {
            next.Type = proposed.Type;
            next.Tag = proposed.Tag;
            next.DataBinding = proposed.DataBinding;
            if (proposed.Experience?.SubmitCapability is not null &&
                !string.Equals(proposed.Experience.SubmitCapability, current.Experience?.SubmitCapability, StringComparison.Ordinal))
                throw new WebsiteSiteSourceProtectionException("Native experience submission capabilities must be selected through the canonical capability authority.");
            next.Experience = proposed.Experience;
            if (next.Experience is not null && current.Experience?.SubmitCapability is not null)
                next.Experience.SubmitCapability = current.Experience.SubmitCapability;
            next.SyncSourceId = proposed.SyncSourceId;
            next.Href = proposed.Href;
            next.Target = proposed.Target;
            next.ActionKey = proposed.ActionKey;
            if (!string.IsNullOrWhiteSpace(next.ActionKey))
            {
                var action = actions.SingleOrDefault(value => value.Key == next.ActionKey)
                    ?? throw new ArgumentException($"Action '{next.ActionKey}' is not available in this scope.");
                next.Href = action.Href;
                next.Target = action.OpenInNewTab ? "_blank" : "_self";
            }
        }

        if ((proposed.Children?.Count ?? 0) > 0 || (current.Children?.Count ?? 0) == 0)
            next.Children = PrepareReplacementChildren(current.Children ?? [], proposed.Children ?? [], actions);

        next.Id = current.Id;
        next.SystemKey = current.SystemKey;
        next.SystemBinding = current.SystemBinding;
        next.Signals = Clone(current.Signals);
        next.FieldSignals = Clone(current.FieldSignals);
        if (protectedIdentity)
        {
            next.Type = current.Type;
            next.Tag = current.Tag;
            next.ActionKey = current.ActionKey;
            next.Href = current.Href;
            next.Target = current.Target;
            next.DataBinding = current.DataBinding;
            next.Experience = current.Experience;
            next.SyncSourceId = current.SyncSourceId;
        }
        return next;
    }

    private static List<WebsiteCompositionNode> PrepareReplacementChildren(
        IReadOnlyList<WebsiteCompositionNode> current,
        IReadOnlyList<WebsiteCompositionNode> proposed,
        IReadOnlyList<WebsiteCallToActionOption> actions)
    {
        var currentById = current.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var next = new List<WebsiteCompositionNode>();
        foreach (var child in proposed)
        {
            if (currentById.TryGetValue(child.Id, out var existing))
                next.Add(MergeAuthorable(existing, child, actions));
            else
                next.Add(PrepareNewNode(child, actions));
        }

        foreach (var existing in current)
            if (HasProtectedSemantics(existing) && next.All(value => value.Id != existing.Id))
                throw new WebsiteSiteSourceProtectionException($"Protected component '{existing.Id}' cannot be removed by replacing its parent.");
        return next;
    }

    private static List<WebsiteCompositionNode> PrepareNewNodes(
        IEnumerable<WebsiteCompositionNode> nodes,
        IReadOnlyList<WebsiteCallToActionOption> actions) =>
        nodes.Select(node => PrepareNewNode(node, actions)).ToList();

    private static WebsiteCompositionNode PrepareNewNode(
        WebsiteCompositionNode source,
        IReadOnlyList<WebsiteCallToActionOption> actions)
    {
        var node = Clone(source);
        if (string.IsNullOrWhiteSpace(node.Id)) node.Id = FreshId("node");
        node.Signals ??= [];
        node.FieldSignals ??= new(StringComparer.Ordinal);
        node.Children ??= [];
        if (node.Type == "form" || !string.IsNullOrWhiteSpace(node.SystemKey) || !string.IsNullOrWhiteSpace(node.SystemBinding) ||
            node.Signals.Count > 0 || node.FieldSignals.Values.Any(value => value?.Count > 0) ||
            node.Experience?.SubmitCapability is not null)
            throw new WebsiteSiteSourceProtectionException("Protected capabilities must be inserted through the server capability authority, not as free nodes.");

        if (!string.IsNullOrWhiteSpace(node.ActionKey))
        {
            var action = actions.SingleOrDefault(value => value.Key == node.ActionKey)
                ?? throw new ArgumentException($"Action '{node.ActionKey}' is not available in this website scope.");
            node.Href = action.Href;
            node.Target = action.OpenInNewTab ? "_blank" : "_self";
        }

        node.Children = PrepareNewNodes(node.Children ?? [], actions);
        return node;
    }

    private static bool HasProtectedSemantics(WebsiteCompositionNode node) =>
        node.Type == "form" ||
        !string.IsNullOrWhiteSpace(node.SystemKey) ||
        !string.IsNullOrWhiteSpace(node.SystemBinding) ||
        (node.Signals?.Count ?? 0) > 0 ||
        (node.FieldSignals?.Values.Any(value => value?.Count > 0) ?? false);

    private static bool ContainsProtectedSemantics(WebsiteCompositionNode node) =>
        HasProtectedSemantics(node) || (node.Children?.Any(ContainsProtectedSemantics) ?? false);

    private static void VerifyFingerprint(string? expected, string actual, string kind, string id)
    {
        if (!string.IsNullOrWhiteSpace(expected) && !string.Equals(expected, actual, StringComparison.Ordinal))
            throw new WebsiteMutationConflictException(kind, id, actual);
    }

    private static string? FingerprintScope(WebsiteContentDocument document, string scope)
    {
        if (scope == "@theme") return WebsiteCreativeFingerprint.For(document.Theme);
        if (scope == "@favicon") return WebsiteCreativeFingerprint.For(document.FaviconImageDataUrl);
        if (scope == "@breakpoints") return WebsiteCreativeFingerprint.For(document.Breakpoints);
        if (scope == "@shell/header") return WebsiteCreativeFingerprint.For(document.Shell.Header.Select(WebsiteCreativeProjection.Node));
        if (scope == "@shell/footer") return WebsiteCreativeFingerprint.For(document.Shell.Footer.Select(WebsiteCreativeProjection.Node));
        if (scope.StartsWith("@component/", StringComparison.Ordinal))
        {
            var remainder = scope["@component/".Length..];
            var separator = remainder.IndexOf('/');
            var id = separator < 0 ? remainder : remainder[..separator];
            return document.ReusableComponents.TryGetValue(id, out var component)
                ? WebsiteCreativeFingerprint.For(component.Composition.Select(WebsiteCreativeProjection.Node))
                : null;
        }

        var marker = scope.IndexOf('#');
        var page = marker < 0 ? scope : scope[..marker];
        return document.Pages.TryGetValue(page, out var value) ? WebsiteCreativeFingerprint.Page(value) : null;
    }

    private static string ScopeKey(string scope, string? pagePath, string? componentId, string? nodeId) =>
        scope switch
        {
            "page" => NormalizePath(pagePath) + (nodeId is null ? "" : "#" + nodeId),
            "component" => "@component/" + componentId + (nodeId is null ? "" : "#" + nodeId),
            _ => "@" + scope.Replace('.', '/') + (nodeId is null ? "" : "#" + nodeId)
        };

    private static string NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A page path is required.");
        var path = value.Trim();
        if (!path.StartsWith('/')) path = "/" + path;
        path = path.Length > 1 ? path.TrimEnd('/') : path;
        if (path.Contains("..", StringComparison.Ordinal) || path.Contains('?') || path.Contains('#'))
            throw new ArgumentException("The page path is invalid.");
        return path;
    }

    private static string FreshId(string prefix) => prefix + "." + Guid.NewGuid().ToString("N");

    private static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!;
}

public sealed class WebsiteMutationConflictException(string kind, string id, string actualFingerprint)
    : InvalidOperationException($"The {kind} '{id}' changed in another editing scope.")
{
    public string Kind { get; } = kind;
    public string TargetId { get; } = id;
    public string ActualFingerprint { get; } = actualFingerprint;
}

public sealed record WebsiteRecipeDefinition(
    string Key,
    string Label,
    string Category,
    string Purpose,
    IReadOnlyList<string> ContentSlots,
    IReadOnlyList<string> OptionalCapabilities);

public static class WebsiteRecipeCatalog
{
    public static readonly IReadOnlyList<WebsiteRecipeDefinition> Definitions =
    [
        new("hero.cinematic", "Cinematic hero", "hero", "High-impact opening with strong headline, proof and action.", ["eyebrow","headline","body","proof"], ["primary"]),
        new("hero.split", "Editorial split hero", "hero", "Two-column opening for copy plus strong media.", ["eyebrow","headline","body"], ["primary"]),
        new("proof.stats", "Proof and metrics", "proof", "Compact evidence band for trust and measurable outcomes.", ["headline","body","stat1","stat2","stat3"], []),
        new("services.grid", "Service grid", "services", "Premium service/feature grid with clean hierarchy.", ["eyebrow","headline","body","item1","item2","item3"], ["primary"]),
        new("features.bento", "Bento features", "features", "Asymmetric modern feature grid.", ["headline","item1","item2","item3","item4"], []),
        new("feature.split", "Editorial feature split", "features", "Media and copy split with a focused action.", ["eyebrow","headline","body"], ["primary"]),
        new("process.steps", "Process", "process", "Simple three-step conversion-supporting process.", ["headline","step1","step2","step3"], []),
        new("comparison", "Comparison", "proof", "Clear differentiator/comparison section.", ["headline","body","item1","item2","item3"], ["primary"]),
        new("testimonials", "Testimonials", "proof", "Social-proof section.", ["headline","quote1","quote2","quote3"], []),
        new("faq", "FAQ", "objection", "Objection-handling FAQ section.", ["headline","q1","a1","q2","a2","q3","a3"], []),
        new("contact.inquiry", "Inquiry conversion", "conversion", "Closing conversion section designed around the canonical inquiry capability.", ["eyebrow","headline","body","title","submit"], ["contact.inquiry.submit"]),
        new("cta.closing", "Closing CTA", "conversion", "Focused closing call to action.", ["eyebrow","headline","body"], ["primary"])
    ];

    public static WebsiteCompositionNode Build(
        string key,
        string instanceKey,
        IReadOnlyDictionary<string, string> content,
        Guid? mediaAssetId = null)
    {
        if (!Definitions.Any(value => value.Key == key))
            throw new ArgumentException($"Unknown website recipe '{key}'.");

        return key switch
        {
            "hero.cinematic" => Hero(instanceKey, content, mediaAssetId, split: false),
            "hero.split" => Hero(instanceKey, content, mediaAssetId, split: true),
            "proof.stats" => Stats(instanceKey, content),
            "services.grid" => Grid(instanceKey, content, "legend-recipe-services"),
            "features.bento" => Bento(instanceKey, content),
            "feature.split" => FeatureSplit(instanceKey, content, mediaAssetId),
            "process.steps" => Steps(instanceKey, content),
            "comparison" => Grid(instanceKey, content, "legend-recipe-comparison"),
            "testimonials" => Testimonials(instanceKey, content),
            "faq" => Faq(instanceKey, content),
            "contact.inquiry" => Contact(instanceKey, content),
            "cta.closing" => Closing(instanceKey, content),
            _ => throw new ArgumentException($"Unknown website recipe '{key}'.")
        };
    }

    private static WebsiteCompositionNode Hero(string id, IReadOnlyDictionary<string,string> c, Guid? media, bool split)
    {
        var children = new List<WebsiteCompositionNode>
        {
            Text(id+".eyebrow","text","p",Get(c,"eyebrow","Built for what comes next"),"legend-recipe-eyebrow"),
            Text(id+".headline","heading","h1",Get(c,"headline","A sharper way forward."),"legend-recipe-display"),
            Text(id+".body","text","p",Get(c,"body","Clear value, confident presentation, and one obvious next step."),"legend-recipe-lead"),
            Slot(id+".capability")
        };
        if (media.HasValue)
            children.Add(new WebsiteCompositionNode { Id=id+".media", Type="image", Tag="img", MediaAssetId=media, Alt=Get(c,"alt",""), ClassName="legend-recipe-hero-media" });
        return Section(id, split ? "legend-recipe-hero legend-recipe-hero-split" : "legend-recipe-hero legend-recipe-hero-cinematic", children,
            split ? new WebsiteCompositionLayout { Mode="grid", Columns=2, GapPx=48, AlignItems="center" } : new WebsiteCompositionLayout { Mode="stack", Direction="column", GapPx=20, AlignItems="start" });
    }

    private static WebsiteCompositionNode Stats(string id, IReadOnlyDictionary<string,string> c) =>
        Section(id,"legend-recipe-section legend-recipe-proof",
        [
            Text(id+".headline","heading","h2",Get(c,"headline","Proof that earns attention."),"legend-recipe-heading"),
            Text(id+".body","text","p",Get(c,"body","Focused evidence, presented without noise."),"legend-recipe-copy"),
            Container(id+".stats","legend-recipe-stat-grid",[
                Text(id+".stat1","text","p",Get(c,"stat1","Trusted expertise"),"legend-recipe-stat"),
                Text(id+".stat2","text","p",Get(c,"stat2","Responsive service"),"legend-recipe-stat"),
                Text(id+".stat3","text","p",Get(c,"stat3","Built around results"),"legend-recipe-stat")
            ], new WebsiteCompositionLayout { Mode="grid", Columns=3, GapPx=18 })
        ]);

    private static WebsiteCompositionNode Grid(string id, IReadOnlyDictionary<string,string> c, string className) =>
        Section(id,"legend-recipe-section "+className,
        [
            Text(id+".eyebrow","text","p",Get(c,"eyebrow","What we do"),"legend-recipe-eyebrow"),
            Text(id+".headline","heading","h2",Get(c,"headline","Designed around what matters most."),"legend-recipe-heading"),
            Text(id+".body","text","p",Get(c,"body","A focused set of services with a clear path forward."),"legend-recipe-copy"),
            Container(id+".grid","legend-recipe-card-grid",[
                Card(id+".item1",Get(c,"item1","Focused expertise")),
                Card(id+".item2",Get(c,"item2","Premium execution")),
                Card(id+".item3",Get(c,"item3","Clear next steps"))
            ],new WebsiteCompositionLayout { Mode="grid", Columns=3, GapPx=18 }),
            Slot(id+".capability")
        ]);

    private static WebsiteCompositionNode Bento(string id, IReadOnlyDictionary<string,string> c) =>
        Section(id,"legend-recipe-section legend-recipe-bento",
        [
            Text(id+".headline","heading","h2",Get(c,"headline","Everything important, without the clutter."),"legend-recipe-heading"),
            Container(id+".grid","legend-recipe-bento-grid",[
                Card(id+".item1",Get(c,"item1","Core advantage")),
                Card(id+".item2",Get(c,"item2","Fast and focused")),
                Card(id+".item3",Get(c,"item3","Built to scale")),
                Card(id+".item4",Get(c,"item4","Clear by design"))
            ],new WebsiteCompositionLayout { Mode="grid", Columns=2, GapPx=18 })
        ]);

    private static WebsiteCompositionNode FeatureSplit(string id, IReadOnlyDictionary<string,string> c, Guid? media)
    {
        var children = new List<WebsiteCompositionNode>
        {
            Container(id+".copy","legend-recipe-feature-copy",[
                Text(id+".eyebrow","text","p",Get(c,"eyebrow","Why it works"),"legend-recipe-eyebrow"),
                Text(id+".headline","heading","h2",Get(c,"headline","A better experience from the first interaction."),"legend-recipe-heading"),
                Text(id+".body","text","p",Get(c,"body","Purposeful hierarchy keeps attention moving toward the right decision."),"legend-recipe-copy"),
                Slot(id+".capability")
            ],new WebsiteCompositionLayout { Mode="stack", Direction="column", GapPx=18 })
        };
        if (media.HasValue)
            children.Add(new WebsiteCompositionNode { Id=id+".media",Type="image",Tag="img",MediaAssetId=media,Alt=Get(c,"alt",""),ClassName="legend-recipe-feature-media" });
        return Section(id,"legend-recipe-section legend-recipe-feature-split",children,new WebsiteCompositionLayout { Mode="grid", Columns=2, GapPx=44, AlignItems="center" });
    }

    private static WebsiteCompositionNode Steps(string id, IReadOnlyDictionary<string,string> c) =>
        Section(id,"legend-recipe-section legend-recipe-process",
        [
            Text(id+".headline","heading","h2",Get(c,"headline","A simple path forward."),"legend-recipe-heading"),
            Container(id+".steps","legend-recipe-step-grid",[
                Card(id+".step1",Get(c,"step1","01 · Start with what matters")),
                Card(id+".step2",Get(c,"step2","02 · Get a clear recommendation")),
                Card(id+".step3",Get(c,"step3","03 · Move forward confidently"))
            ],new WebsiteCompositionLayout { Mode="grid", Columns=3, GapPx=18 })
        ]);

    private static WebsiteCompositionNode Testimonials(string id, IReadOnlyDictionary<string,string> c) =>
        Section(id,"legend-recipe-section legend-recipe-testimonials",
        [
            Text(id+".headline","heading","h2",Get(c,"headline","Trusted when it matters."),"legend-recipe-heading"),
            Container(id+".quotes","legend-recipe-card-grid",[
                Quote(id+".quote1",Get(c,"quote1","“Clear, responsive, and easy to work with.”")),
                Quote(id+".quote2",Get(c,"quote2","“A noticeably better experience from start to finish.”")),
                Quote(id+".quote3",Get(c,"quote3","“Professional, thoughtful, and focused on the right outcome.”"))
            ],new WebsiteCompositionLayout { Mode="grid", Columns=3, GapPx=18 })
        ]);

    private static WebsiteCompositionNode Faq(string id, IReadOnlyDictionary<string,string> c) =>
        Section(id,"legend-recipe-section legend-recipe-faq",
        [
            Text(id+".headline","heading","h2",Get(c,"headline","Questions, answered clearly."),"legend-recipe-heading"),
            Qa(id+".q1",Get(c,"q1","What should I expect?"),Get(c,"a1","A clear process, direct communication, and a focused next step.")),
            Qa(id+".q2",Get(c,"q2","How do we get started?"),Get(c,"a2","Use the primary action on this page and we’ll take it from there.")),
            Qa(id+".q3",Get(c,"q3","What makes this different?"),Get(c,"a3","The experience is built around clarity, fit, and real outcomes—not unnecessary complexity."))
        ]);

    private static WebsiteCompositionNode Contact(string id, IReadOnlyDictionary<string,string> c) =>
        Section(id,"legend-recipe-section legend-recipe-contact",
        [
            Text(id+".eyebrow","text","p",Get(c,"eyebrow","Start here"),"legend-recipe-eyebrow"),
            Text(id+".headline","heading","h2",Get(c,"headline","Ready for the next step?"),"legend-recipe-heading"),
            Text(id+".body","text","p",Get(c,"body","Tell us what you need and we’ll follow up with a clear path forward."),"legend-recipe-copy"),
            Slot(id+".capability")
        ]);

    private static WebsiteCompositionNode Closing(string id, IReadOnlyDictionary<string,string> c) =>
        Section(id,"legend-recipe-section legend-recipe-closing",
        [
            Text(id+".eyebrow","text","p",Get(c,"eyebrow","Your next move"),"legend-recipe-eyebrow"),
            Text(id+".headline","heading","h2",Get(c,"headline","Make the next step obvious."),"legend-recipe-heading"),
            Text(id+".body","text","p",Get(c,"body","A strong close removes friction and gives the visitor one clear action."),"legend-recipe-copy"),
            Slot(id+".capability")
        ]);

    private static WebsiteCompositionNode Section(string id,string className,IEnumerable<WebsiteCompositionNode> children,WebsiteCompositionLayout? layout=null) =>
        new() { Id=id,Type="section",Tag="section",ClassName=className,Style=new WebsiteVisualStyle{WidthPercent=100},Layout=layout ?? new WebsiteCompositionLayout{Mode="stack",Direction="column",GapPx=20},Children=children.ToList() };

    private static WebsiteCompositionNode Container(string id,string className,IEnumerable<WebsiteCompositionNode> children,WebsiteCompositionLayout layout) =>
        new() { Id=id,Type="container",Tag="div",ClassName=className,Style=new WebsiteVisualStyle{WidthPercent=100},Layout=layout,Children=children.ToList() };

    private static WebsiteCompositionNode Text(string id,string type,string tag,string text,string className) =>
        new() { Id=id,Type=type,Tag=tag,Text=text,ClassName=className };

    private static WebsiteCompositionNode Card(string id,string text) =>
        Container(id,"legend-recipe-card",[Text(id+".title","heading","h3",text,"legend-recipe-card-title")],new WebsiteCompositionLayout{Mode="stack",Direction="column",GapPx=10});

    private static WebsiteCompositionNode Quote(string id,string text) =>
        Text(id,"text","blockquote",text,"legend-recipe-quote");

    private static WebsiteCompositionNode Qa(string id,string q,string a) =>
        Container(id,"legend-recipe-faq-item",[Text(id+".q","heading","h3",q,"legend-recipe-faq-q"),Text(id+".a","text","p",a,"legend-recipe-copy")],new WebsiteCompositionLayout{Mode="stack",Direction="column",GapPx=8});

    private static WebsiteCompositionNode Slot(string id) =>
        new() { Id=id,Type="container",Tag="div",ClassName="legend-capability-slot",Style=new WebsiteVisualStyle{WidthPercent=100},Layout=new WebsiteCompositionLayout{Mode="stack",Direction="column",GapPx=12} };

    private static string Get(IReadOnlyDictionary<string,string> content,string key,string fallback) =>
        content.TryGetValue(key,out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : fallback;
}

public sealed class WebsiteDesignPlan
{
    public string? ArtDirection { get; set; }
    public string? PrimaryCapabilityKey { get; set; }
    public WebsiteDesignTheme? Theme { get; set; }
    public bool ReplaceBusinessPages { get; set; }
    public List<WebsiteDesignPlanPage> Pages { get; set; } = [];
}

public sealed class WebsiteDesignPlanPage
{
    public string Path { get; set; } = "/";
    public string? Recipe { get; set; }
    public string? PrimaryCapabilityKey { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? NavigationLabel { get; set; }
    public int NavigationOrder { get; set; }
    public bool ShowInNavigation { get; set; } = true;
    public bool ReplaceFreeComposition { get; set; } = true;
    public List<WebsiteDesignPlanSection> Sections { get; set; } = [];
}

public sealed class WebsiteDesignPlanSection
{
    public string Recipe { get; set; } = string.Empty;
    public string? Key { get; set; }
    public Dictionary<string,string> Content { get; set; } = new(StringComparer.Ordinal);
    public Guid? MediaAssetId { get; set; }
    public string? CapabilityKey { get; set; }
    public string? CapabilityNodeId { get; set; }
}

public sealed record WebsitePageRecipeDefinition(
    string Key,
    string Label,
    string Purpose,
    IReadOnlyList<string> SectionRecipes);

public static class WebsitePageRecipeCatalog
{
    public static readonly IReadOnlyList<WebsitePageRecipeDefinition> Definitions =
    [
        new("home", "Home", "Primary brand, trust, offer, proof, and conversion narrative.",
            ["hero.cinematic","proof.stats","services.grid","feature.split","testimonials","cta.closing"]),
        new("services", "Services", "Clarify the service architecture, differentiators, process, proof, and next step.",
            ["hero.split","services.grid","comparison","process.steps","testimonials","cta.closing"]),
        new("service-detail", "Service detail", "Focus one offer with benefits, proof, process, objections, and conversion.",
            ["hero.split","feature.split","proof.stats","process.steps","faq","cta.closing"]),
        new("about", "About", "Build authority and affinity through story, principles, proof, and a confident next step.",
            ["hero.split","feature.split","proof.stats","testimonials","cta.closing"]),
        new("contact", "Contact", "Establish trust and expectations before the canonical inquiry conversion.",
            ["hero.split","proof.stats","contact.inquiry"]),
        new("landing", "Lead landing", "Paid-traffic message match with concentrated proof and minimal competing choices.",
            ["hero.cinematic","proof.stats","feature.split","testimonials","faq","contact.inquiry"]),
        new("offer", "Offer", "Present one focused offer, value comparison, proof, objections, and decisive conversion.",
            ["hero.cinematic","proof.stats","comparison","testimonials","faq","cta.closing"]),
        new("faq", "FAQ", "Resolve objections with a concise opening, focused answers, and closing conversion.",
            ["hero.split","faq","cta.closing"]),
        new("team", "Team", "Introduce people and trust signals without losing the business conversion path.",
            ["hero.split","proof.stats","features.bento","cta.closing"]),
        new("gallery", "Gallery / portfolio", "Lead with visual proof, context, credibility, and a clear next action.",
            ["hero.split","features.bento","proof.stats","testimonials","cta.closing"]),
        new("case-study", "Case study", "Tell a problem-to-result story with evidence and a relevant conversion close.",
            ["hero.split","proof.stats","feature.split","process.steps","testimonials","cta.closing"])
    ];

    public static IReadOnlyList<WebsiteDesignPlanSection> Build(
        string key,
        string pageKey,
        string? primaryCapabilityKey = null)
    {
        var definition = Definitions.SingleOrDefault(value => value.Key == key)
            ?? throw new ArgumentException($"Unknown website page recipe '{key}'.");
        var result = new List<WebsiteDesignPlanSection>(definition.SectionRecipes.Count);
        for (var index = 0; index < definition.SectionRecipes.Count; index++)
        {
            var recipe = definition.SectionRecipes[index];
            var capability = recipe == "contact.inquiry"
                ? primaryCapabilityKey == "contact.inquiry.submit" ? primaryCapabilityKey : "contact.inquiry.submit"
                : (index == 0 || index == definition.SectionRecipes.Count - 1) ? primaryCapabilityKey : null;
            result.Add(new WebsiteDesignPlanSection
            {
                Recipe = recipe,
                Key = $"{pageKey}.{recipe.Replace('.', '-')}.{index + 1}",
                CapabilityKey = capability,
                Content = new Dictionary<string, string>(StringComparer.Ordinal)
            });
        }
        return result;
    }
}

public static class WebsiteArtDirectionPresets
{
    public static WebsiteDesignTheme? Resolve(string? key) =>
        key?.Trim().ToLowerInvariant() switch
        {
            "roadster" or "roadster-precision" => new WebsiteDesignTheme
            {
                Navy="#07152d", NavyDeep="#040b18", Gold="#bc8e10", GoldStrong="#d4ad45",
                Surface="#ffffff", Muted="#8b98aa", Text="#101a35", FontFamily="Inter, system-ui, sans-serif",
                FontSize=16, BorderRadius=18,
                DisplaySize=72, H1Size=60, H2Size=44, H3Size=26, BodySize=17, SmallSize=13,
                BodyLineHeight=1.6m, SectionSpace=112, ContentGap=24, ContentMaxWidth=1180, WideMaxWidth=1440,
                NarrowMaxWidth=760, Gutter=28, CardRadius=22, ButtonRadius=999, InputRadius=14,
                SurfaceElevated="#ffffff", SurfaceMuted="#f5f7fa", BorderColor="#d9e0ea", BorderWidth=1,
                ShadowSoft="0 18px 50px rgba(4,11,24,.10)", ShadowStrong="0 28px 90px rgba(4,11,24,.18)",
                NavHeight=76, MotionFastMs=160, MotionStandardMs=320, MotionSlowMs=560
            },
            "editorial-luxe" => new WebsiteDesignTheme
            {
                Navy="#181512", NavyDeep="#0d0b09", Gold="#a47b3c", GoldStrong="#c79b54",
                Surface="#fbfaf6", SurfaceElevated="#ffffff", SurfaceMuted="#f1eee6", Text="#211d19", Muted="#786f65",
                FontFamily="Georgia, 'Times New Roman', serif", FontSize=17, DisplaySize=78, H1Size=64, H2Size=46,
                H3Size=27, BodySize=18, SmallSize=13, BodyLineHeight=1.68m, SectionSpace=124, ContentGap=28,
                ContentMaxWidth=1160, WideMaxWidth=1420, NarrowMaxWidth=720, Gutter=32, BorderRadius=12, CardRadius=16,
                ButtonRadius=4, InputRadius=6, BorderColor="#ded7ca", BorderWidth=1,
                ShadowSoft="0 16px 44px rgba(24,21,18,.08)", ShadowStrong="0 30px 80px rgba(24,21,18,.14)",
                NavHeight=82, MotionFastMs=180, MotionStandardMs=380, MotionSlowMs=650
            },
            "modern-minimal" => new WebsiteDesignTheme
            {
                Navy="#111315", NavyDeep="#070809", Gold="#6b7280", GoldStrong="#111315",
                Surface="#ffffff", SurfaceElevated="#ffffff", SurfaceMuted="#f6f7f8", Text="#111315", Muted="#697079",
                FontFamily="Inter, system-ui, sans-serif", FontSize=16, DisplaySize=68, H1Size=58, H2Size=42, H3Size=24,
                BodySize=16, SmallSize=12, BodyLineHeight=1.62m, SectionSpace=104, ContentGap=22, ContentMaxWidth=1120,
                WideMaxWidth=1360, NarrowMaxWidth=700, Gutter=28, BorderRadius=10, CardRadius=14, ButtonRadius=10,
                InputRadius=10, BorderColor="#e4e7eb", BorderWidth=1, ShadowSoft="0 12px 36px rgba(0,0,0,.06)",
                ShadowStrong="0 24px 64px rgba(0,0,0,.10)", NavHeight=72, MotionFastMs=140, MotionStandardMs=260, MotionSlowMs=460
            },
            "warm-craft" => new WebsiteDesignTheme
            {
                Navy="#25322a", NavyDeep="#17201b", Gold="#b36f3b", GoldStrong="#d08a52",
                Surface="#fffdf8", SurfaceElevated="#ffffff", SurfaceMuted="#f5efe5", Text="#2d2a25", Muted="#756d62",
                FontFamily="Inter, system-ui, sans-serif", FontSize=17, DisplaySize=66, H1Size=56, H2Size=42, H3Size=25,
                BodySize=17, SmallSize=13, BodyLineHeight=1.68m, SectionSpace=104, ContentGap=24, ContentMaxWidth=1140,
                WideMaxWidth=1380, NarrowMaxWidth=740, Gutter=26, BorderRadius=20, CardRadius=24, ButtonRadius=16,
                InputRadius=14, BorderColor="#e1d7c9", BorderWidth=1, ShadowSoft="0 16px 40px rgba(37,50,42,.08)",
                ShadowStrong="0 26px 70px rgba(37,50,42,.14)", NavHeight=76, MotionFastMs=170, MotionStandardMs=340, MotionSlowMs=600
            },
            "clinical-precision" => new WebsiteDesignTheme
            {
                Navy="#0c2742", NavyDeep="#061728", Gold="#4f8aa8", GoldStrong="#2f6e91",
                Surface="#ffffff", SurfaceElevated="#ffffff", SurfaceMuted="#f2f7fa", Text="#14212d", Muted="#667887",
                FontFamily="Inter, system-ui, sans-serif", FontSize=16, DisplaySize=64, H1Size=54, H2Size=40, H3Size=24,
                BodySize=16, SmallSize=12, BodyLineHeight=1.62m, SectionSpace=96, ContentGap=20, ContentMaxWidth=1120,
                WideMaxWidth=1360, NarrowMaxWidth=720, Gutter=28, BorderRadius=12, CardRadius=16, ButtonRadius=10,
                InputRadius=10, BorderColor="#d9e5ec", BorderWidth=1, ShadowSoft="0 12px 34px rgba(6,23,40,.07)",
                ShadowStrong="0 22px 60px rgba(6,23,40,.12)", NavHeight=74, MotionFastMs=140, MotionStandardMs=280, MotionSlowMs=480
            },
            "performance" or "high-energy-performance" => new WebsiteDesignTheme
            {
                Navy="#111317", NavyDeep="#050607", Gold="#ef5b2a", GoldStrong="#ff7548",
                Surface="#ffffff", SurfaceElevated="#ffffff", SurfaceMuted="#f3f4f6", Text="#111317", Muted="#69717c",
                FontFamily="Inter, system-ui, sans-serif", FontSize=16, DisplaySize=76, H1Size=62, H2Size=46, H3Size=27,
                BodySize=17, SmallSize=12, BodyLineHeight=1.56m, SectionSpace=100, ContentGap=20, ContentMaxWidth=1200,
                WideMaxWidth=1480, NarrowMaxWidth=720, Gutter=24, BorderRadius=14, CardRadius=18, ButtonRadius=10,
                InputRadius=10, BorderColor="#dfe2e7", BorderWidth=1, ShadowSoft="0 16px 42px rgba(5,6,7,.08)",
                ShadowStrong="0 28px 72px rgba(5,6,7,.18)", NavHeight=74, MotionFastMs=110, MotionStandardMs=230, MotionSlowMs=420
            },
            _ => null
        };
}

public static class WebsiteDesignPlanResolver
{
    public static IReadOnlyList<WebsiteMutationOperation> Resolve(
        WebsiteContentDocument baseline,
        string siteKey,
        WebsiteCapabilityManifest capabilities,
        WebsiteDesignPlan plan)
    {
        var operations = new List<WebsiteMutationOperation>();

        if (plan.ReplaceBusinessPages && siteKey == WebsiteEditorSiteKeys.Business)
        {
            var planned = (plan.Pages ?? [])
                .Select(value => NormalizePath(value.Path))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var (path, page) in baseline.Pages)
            {
                if (path == "/" || planned.Contains(path) || page.Navigation?.IsDeleted == true || page.SystemTemplateKey is not null)
                    continue;
                operations.Add(new WebsiteMutationOperation { Type = "removePage", PagePath = path });
            }
        }

        var preset = WebsiteArtDirectionPresets.Resolve(plan.ArtDirection);
        if (preset is not null || plan.Theme is not null)
            operations.Add(new WebsiteMutationOperation { Type="setTheme", Theme=MergeTheme(preset, plan.Theme) });

        foreach (var pagePlan in plan.Pages ?? [])
        {
            var path = NormalizePath(pagePlan.Path);
            var exists = baseline.Pages.TryGetValue(path, out var existing);
            if (!exists)
            {
                if (siteKey != WebsiteEditorSiteKeys.Business)
                    throw new WebsiteSiteSourceProtectionException($"Page '{path}' is not part of this fixed-route website scope.");
                operations.Add(new WebsiteMutationOperation
                {
                    Type="createPage", PagePath=path,
                    Page=new WebsitePageDocument
                    {
                        Title=pagePlan.Title,
                        Description=pagePlan.Description,
                        Navigation=new WebsitePageNavigation
                        {
                            Label=pagePlan.NavigationLabel ?? pagePlan.Title,
                            ShowInNavigation=pagePlan.ShowInNavigation,
                            Order=pagePlan.NavigationOrder,
                            IsDeleted=false
                        },
                        Composition=[]
                    }
                });
            }
            else
            {
                operations.Add(new WebsiteMutationOperation
                {
                    Type="updatePage", PagePath=path, Title=pagePlan.Title, Description=pagePlan.Description,
                    Navigation=new WebsitePageNavigation
                    {
                        Label=pagePlan.NavigationLabel ?? existing!.Navigation?.Label ?? pagePlan.Title,
                        ShowInNavigation=pagePlan.ShowInNavigation,
                        ParentPath=existing.Navigation?.ParentPath,
                        Order=pagePlan.NavigationOrder,
                        IsDeleted=false
                    }
                });

                if (pagePlan.ReplaceFreeComposition)
                {
                    foreach (var root in existing!.Composition.Where(root => !ContainsProtected(root)).ToArray())
                        operations.Add(new WebsiteMutationOperation { Type="removeNode", NodeId=root.Id });
                }
            }

            var primaryCapability = pagePlan.PrimaryCapabilityKey ?? plan.PrimaryCapabilityKey;
            if (!string.IsNullOrWhiteSpace(primaryCapability))
                WebsiteCreativeCapabilityResolver.Require(capabilities, primaryCapability);

            IReadOnlyList<WebsiteDesignPlanSection> plannedSections =
                (pagePlan.Sections?.Count ?? 0) > 0
                    ? pagePlan.Sections
                    : !string.IsNullOrWhiteSpace(pagePlan.Recipe)
                        ? WebsitePageRecipeCatalog.Build(
                            pagePlan.Recipe,
                            path == "/" ? "home" : string.Join('.', path.Split('/', StringSplitOptions.RemoveEmptyEntries)),
                            primaryCapability)
                        : Array.Empty<WebsiteDesignPlanSection>();
            var sectionIndex = 0;
            foreach (var section in plannedSections)
            {
                if (!string.IsNullOrWhiteSpace(section.CapabilityKey) &&
                    !string.IsNullOrWhiteSpace(section.CapabilityNodeId))
                    throw new ArgumentException("A design section may reference either one approved capability or one existing protected capability node, not both.");

                var id = string.IsNullOrWhiteSpace(section.Key)
                    ? StableSectionId(path, section.Recipe, ++sectionIndex)
                    : section.Key!;
                operations.Add(new WebsiteMutationOperation
                {
                    Type="insertRecipe", Scope="page", PagePath=path, Index=sectionIndex-1,
                    RecipeKey=section.Recipe, InstanceKey=id, Content=section.Content ?? new Dictionary<string,string>(StringComparer.Ordinal),
                    MediaAssetId=section.MediaAssetId, CapabilityKey=section.CapabilityKey
                });

                if (!string.IsNullOrWhiteSpace(section.CapabilityNodeId))
                {
                    var capability = capabilities.Capabilities.FirstOrDefault(value =>
                        value.NodeId == section.CapabilityNodeId && value.PagePath == path)
                        ?? throw new ArgumentException($"Protected capability node '{section.CapabilityNodeId}' is unavailable on '{path}'.");
                    operations.Add(new WebsiteMutationOperation
                    {
                        Type="moveNode", NodeId=capability.NodeId, Scope="page", PagePath=path,
                        ParentId=id+".capability"
                    });
                }
            }
        }

        return operations;
    }

    private static WebsiteDesignTheme MergeTheme(WebsiteDesignTheme? basis, WebsiteDesignTheme? overlay)
    {
        if (basis is null) return overlay ?? new WebsiteDesignTheme();
        if (overlay is null) return basis;
        var json = JsonSerializer.Serialize(basis, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var merged = JsonSerializer.Deserialize<WebsiteDesignTheme>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        foreach (var property in typeof(WebsiteDesignTheme).GetProperties())
        {
            var value = property.GetValue(overlay);
            if (value is not null) property.SetValue(merged, value);
        }
        return merged;
    }

    private static bool ContainsProtected(WebsiteCompositionNode node) =>
        node.Type=="form" || !string.IsNullOrWhiteSpace(node.SystemKey) || !string.IsNullOrWhiteSpace(node.SystemBinding) ||
        node.Signals.Count>0 || node.FieldSignals.Values.Any(value=>value.Count>0) || node.Children.Any(ContainsProtected);

    private static string StableSectionId(string path,string recipe,int index)
    {
        var route = path=="/" ? "home" : string.Join('.',path.Split('/',StringSplitOptions.RemoveEmptyEntries));
        var clean = new string(recipe.Select(character => char.IsLetterOrDigit(character) ? character : '.').ToArray()).Trim('.');
        return $"{route}.{clean}.{index}";
    }

    private static string NormalizePath(string value)
    {
        var path = string.IsNullOrWhiteSpace(value) ? "/" : value.Trim();
        if (!path.StartsWith('/')) path="/"+path;
        return path.Length>1 ? path.TrimEnd('/') : path;
    }
}
