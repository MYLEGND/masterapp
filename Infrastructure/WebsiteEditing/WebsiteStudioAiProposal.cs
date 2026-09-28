using System.Text.Json;

namespace Infrastructure.WebsiteEditing;

public sealed class WebsiteStudioAiOperation
{
    public string Kind { get; set; } = string.Empty;
    public string? PagePath { get; set; }
    public string? NodeId { get; set; }
    public string? ParentId { get; set; }
    public string? BeforeNodeId { get; set; }
    public string? BreakpointKey { get; set; }
    public string? NodeType { get; set; }
    public string? Tag { get; set; }
    public string? ClassName { get; set; }
    public string? Text { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? NavigationLabel { get; set; }
    public string? ActionKey { get; set; }
    public string? Href { get; set; }
    public string? Alt { get; set; }
    public Guid? MediaAssetId { get; set; }
    public string? ImagePrompt { get; set; }
    public bool? Enabled { get; set; }
    public WebsiteStyleOverride? Style { get; set; }
    public WebsiteLayoutOverride? Layout { get; set; }
    public WebsiteThemeOverride? Theme { get; set; }
}

public sealed record WebsiteStudioAiAppliedProposal(
    string Mode,
    string Summary,
    WebsiteContentDocument ProposedDocument,
    IReadOnlyList<WebsiteStudioAiOperation> Operations);

/// <summary>
/// Applies bounded AI suggestions to an in-memory website document only. This policy
/// never saves, publishes, dispatches analytics, or chooses an owner/destination.
/// </summary>
public static class WebsiteStudioAiProposalPolicy
{
    private const int MaxOperations = 20;
    private const int MaxPromptText = 12000;

    public static WebsiteStudioAiAppliedProposal Apply(
        WebsiteContentDocument source,
        string mode,
        string summary,
        string pagePath,
        string? selectedElementId,
        string? selectedSectionId,
        IEnumerable<WebsiteStudioAiOperation>? operations) =>
        Apply(source, mode, summary, pagePath, selectedElementId, selectedSectionId, operations, null, null);

    public static WebsiteStudioAiAppliedProposal Apply(
        WebsiteContentDocument source,
        string mode,
        string summary,
        string pagePath,
        string? selectedElementId,
        string? selectedSectionId,
        IEnumerable<WebsiteStudioAiOperation>? operations,
        IReadOnlySet<string>? allowedActions,
        IReadOnlySet<Guid>? allowedMediaAssets)
    {
        mode = (mode ?? string.Empty).Trim().ToLowerInvariant();
        if (mode is not ("responsive" or "create" or "build" or "transform" or "selection" or "fix"))
            throw new ArgumentException("Website AI mode is unsupported.");

        var route = NormalizePagePath(pagePath)
            ?? throw new ArgumentException("Website AI requires a valid current page route.");
        var cleanSource = WebsiteContentSanitizer.Sanitize(source);
        var document = Clone(cleanSource);
        var operationList = (operations ?? []).Where(operation => operation is not null).Take(MaxOperations).ToList();

        if (string.Equals(document.CompositionMode, "canonical", StringComparison.Ordinal))
            return ApplyComposition(document, mode, summary, route, selectedElementId, selectedSectionId, operationList,
                allowedActions ?? new HashSet<string>(StringComparer.Ordinal),
                allowedMediaAssets ?? new HashSet<Guid>());

        if (mode is not ("responsive" or "create"))
            throw new ArgumentException("Full-site AI requires a canonical v3 website composition.");

        foreach (var operation in operationList)
        {
            var kind = (operation.Kind ?? string.Empty).Trim().ToLowerInvariant();
            if (mode == "responsive" && kind is not ("set_style" or "set_layout"))
                throw new ArgumentException("Responsive AI may propose only responsive style or layout changes.");

            switch (kind)
            {
                case "set_text":
                    RequireCreateMode(mode);
                    ApplyText(document, route, selectedElementId, operation.Text);
                    break;
                case "set_style":
                    ApplyStyle(document, route, selectedElementId, operation.BreakpointKey, operation.Style);
                    break;
                case "set_layout":
                    ApplyLayout(document, route, selectedElementId, operation.BreakpointKey, operation.Layout);
                    break;
                case "add_section":
                    RequireCreateMode(mode);
                    AddSection(document, route, operation);
                    break;
                case "add_text":
                    RequireCreateMode(mode);
                    AddText(document, route, selectedSectionId, operation.Text);
                    break;
                case "add_button":
                    RequireCreateMode(mode);
                    AddButton(document, route, selectedSectionId, operation.Text, operation.Href);
                    break;
                case "suggest_image":
                    RequireCreateMode(mode);
                    // Image assistance is intentionally advisory until an actual scoped
                    // media asset exists. The prompt is returned to the editor, not saved.
                    break;
                default:
                    throw new ArgumentException("Website AI returned an unsupported operation.");
            }
        }

        var proposed = WebsiteContentSanitizer.Sanitize(document);
        return new WebsiteStudioAiAppliedProposal(
            mode,
            Clamp(summary, 1200) ?? "Website Studio AI proposal",
            proposed,
            operationList);
    }

    private static WebsiteStudioAiAppliedProposal ApplyComposition(
        WebsiteContentDocument document,
        string mode,
        string summary,
        string currentRoute,
        string? selectedElementId,
        string? selectedSectionId,
        IReadOnlyList<WebsiteStudioAiOperation> operations,
        IReadOnlySet<string> allowedActions,
        IReadOnlySet<Guid> allowedMediaAssets)
    {
        var selectionRoot = selectedElementId ?? selectedSectionId;

        foreach (var operation in operations)
        {
            var kind = (operation.Kind ?? string.Empty).Trim().ToLowerInvariant();
            if (mode == "responsive" && kind is not ("set_style" or "set_layout"))
                throw new ArgumentException("Responsive AI may propose only responsive style or layout changes.");

            var route = NormalizePagePath(operation.PagePath) ?? currentRoute;
            if (mode == "selection")
            {
                route = currentRoute;
                var target = operation.NodeId ?? selectedElementId ?? selectedSectionId;
                if (!string.IsNullOrWhiteSpace(target) && !IsWithinSelection(document, currentRoute, selectionRoot, target))
                    throw new ArgumentException("Selection AI cannot modify content outside the selected website subtree.");
                if (kind is "create_page" or "delete_page" or "set_page" or "set_theme" or "enable_store")
                    throw new ArgumentException("Selection AI cannot modify site-level settings or pages.");
            }

            switch (kind)
            {
                case "set_text":
                    CompositionNode(document, route, operation.NodeId ?? selectedElementId, required: true).Text =
                        Clamp(operation.Text, MaxPromptText) ?? string.Empty;
                    break;

                case "set_style":
                    SetCompositionStyle(document,
                        CompositionNode(document, route, operation.NodeId ?? selectedElementId, required: true),
                        operation.BreakpointKey,
                        operation.Style ?? new WebsiteStyleOverride());
                    break;

                case "set_layout":
                    SetCompositionLayout(document,
                        CompositionNode(document, route, operation.NodeId ?? selectedElementId, required: true),
                        operation.BreakpointKey,
                        operation.Layout ?? new WebsiteLayoutOverride());
                    break;

                case "create_page":
                    if (mode is "responsive" or "selection") throw new ArgumentException("This AI mode cannot create pages.");
                    CreateCompositionPage(document, route, operation);
                    break;

                case "delete_page":
                    if (mode is "responsive" or "selection") throw new ArgumentException("This AI mode cannot delete pages.");
                    DeleteCompositionPage(document, route);
                    break;

                case "set_page":
                case "set_seo":
                    if (mode is "responsive" or "selection") throw new ArgumentException("This AI mode cannot edit page metadata.");
                    SetCompositionPage(document, route, operation);
                    break;

                case "add_section":
                    AddCompositionNode(document, route, null, operation.WithDefaults("section", "section"), beforeNodeId: operation.BeforeNodeId);
                    break;

                case "add_text":
                    AddCompositionNode(document, route, operation.ParentId ?? selectedSectionId,
                        operation.WithDefaults("text", "p"), operation.BeforeNodeId);
                    break;

                case "add_button":
                    AddCompositionNode(document, route, operation.ParentId ?? selectedSectionId,
                        operation.WithDefaults("cta", "a"), operation.BeforeNodeId, allowedActions);
                    break;

                case "add_node":
                    AddCompositionNode(document, route, operation.ParentId ?? selectedSectionId, operation, operation.BeforeNodeId, allowedActions, allowedMediaAssets);
                    break;

                case "delete_node":
                    DeleteCompositionNode(document, route, operation.NodeId ?? selectedElementId);
                    break;

                case "move_node":
                    MoveCompositionNode(document, route, operation.NodeId ?? selectedElementId, operation.ParentId, operation.BeforeNodeId);
                    break;

                case "set_action":
                    {
                        var node = CompositionNode(document, route, operation.NodeId ?? selectedElementId, required: true);
                        if (node.Type is not ("cta" or "link"))
                            throw new ArgumentException("Canonical actions can only be assigned to CTA/link nodes.");
                        var action = (operation.ActionKey ?? string.Empty).Trim();
                        if (!allowedActions.Contains(action))
                            throw new ArgumentException("Website AI selected an unavailable canonical action.");
                        node.ActionKey = action;
                        node.Href = null;
                        break;
                    }

                case "bind_media":
                    {
                        var node = CompositionNode(document, route, operation.NodeId ?? selectedElementId, required: true);
                        if (node.Type is not ("image" or "video"))
                            throw new ArgumentException("Media can only bind to image/video nodes.");
                        if (!operation.MediaAssetId.HasValue || !allowedMediaAssets.Contains(operation.MediaAssetId.Value))
                            throw new ArgumentException("Website AI referenced media outside the authorized website library.");
                        node.MediaAssetId = operation.MediaAssetId;
                        node.MediaUrl = null;
                        if (!string.IsNullOrWhiteSpace(operation.Alt)) node.Alt = Clamp(operation.Alt, 500);
                        break;
                    }

                case "set_theme":
                    if (mode is "responsive" or "selection") throw new ArgumentException("This AI mode cannot edit the site theme.");
                    document.Theme = operation.Theme ?? new WebsiteThemeOverride();
                    break;

                case "enable_store":
                    if (mode is "responsive" or "selection") throw new ArgumentException("This AI mode cannot change store availability.");
                    document.Store.Enabled = operation.Enabled == true;
                    break;

                case "suggest_image":
                    // Advisory only. Actual media binding requires an existing scoped asset ID.
                    break;

                default:
                    throw new ArgumentException("Website AI returned an unsupported operation.");
            }
        }

        var proposed = WebsiteContentSanitizer.Sanitize(document);
        return new WebsiteStudioAiAppliedProposal(
            mode,
            Clamp(summary, 1200) ?? "Website Studio AI proposal",
            proposed,
            operations);
    }

    private static WebsiteStudioAiOperation WithDefaults(
        this WebsiteStudioAiOperation operation,
        string type,
        string tag)
    {
        operation.NodeType ??= type;
        operation.Tag ??= tag;
        return operation;
    }

    private static WebsiteCompositionNode CompositionNode(
        WebsiteContentDocument document,
        string route,
        string? id,
        bool required)
    {
        var target = SanitizeId(id);
        WebsiteCompositionNode? Find(IEnumerable<WebsiteCompositionNode> nodes)
        {
            foreach (var node in nodes ?? [])
            {
                if (node.Id == target) return node;
                var child = Find(node.Children);
                if (child is not null) return child;
            }
            return null;
        }

        var page = Page(document, route, create: false);
        var found = page is null || target.Length == 0 ? null : Find(page.Composition);
        if (found is null && required)
            throw new ArgumentException("Website AI referenced a component that is not available on this page.");
        return found!;
    }

    private static bool IsWithinSelection(
        WebsiteContentDocument document,
        string route,
        string? rootId,
        string? targetId)
    {
        if (string.IsNullOrWhiteSpace(rootId) || string.IsNullOrWhiteSpace(targetId)) return false;
        var root = CompositionNode(document, route, rootId, required: false);
        if (root is null) return false;
        if (root.Id == targetId) return true;
        bool Contains(IEnumerable<WebsiteCompositionNode> nodes) =>
            (nodes ?? []).Any(node => node.Id == targetId || Contains(node.Children));
        return Contains(root.Children);
    }

    private static void SetCompositionStyle(
        WebsiteContentDocument document,
        WebsiteCompositionNode node,
        string? breakpointKey,
        WebsiteStyleOverride style)
    {
        var key = ValidateBreakpoint(document, breakpointKey);
        if (key is null) node.Style = style;
        else node.BreakpointStyles[key] = style;
    }

    private static void SetCompositionLayout(
        WebsiteContentDocument document,
        WebsiteCompositionNode node,
        string? breakpointKey,
        WebsiteLayoutOverride layout)
    {
        var key = ValidateBreakpoint(document, breakpointKey);
        if (key is null) node.Layout = layout;
        else node.BreakpointLayouts[key] = layout;
    }

    private static void CreateCompositionPage(
        WebsiteContentDocument document,
        string route,
        WebsiteStudioAiOperation operation)
    {
        if (document.Pages.ContainsKey(route))
            throw new ArgumentException("Website AI cannot replace an existing page through create_page.");
        document.Pages[route] = new WebsitePageDocument
        {
            Title = Clamp(operation.Title, 200),
            Description = Clamp(operation.Description, 500),
            Navigation = new WebsitePageNavigation
            {
                Label = Clamp(operation.NavigationLabel ?? operation.Title ?? route.Split('/').LastOrDefault(), 120),
                ShowInNavigation = true,
                Order = document.Pages.Count * 10
            },
            Composition = []
        };
    }

    private static void DeleteCompositionPage(WebsiteContentDocument document, string route)
    {
        if (route == "/") throw new ArgumentException("Website AI cannot delete the home page.");
        if (!document.Pages.TryGetValue(route, out var page))
            throw new ArgumentException("Website AI referenced a page that is not available.");
        page.Navigation.IsDeleted = true;
        page.Navigation.ShowInNavigation = false;
        page.Composition.Clear();
    }

    private static void SetCompositionPage(
        WebsiteContentDocument document,
        string route,
        WebsiteStudioAiOperation operation)
    {
        var page = Page(document, route, create: false)
            ?? throw new ArgumentException("Website AI referenced a page that is not available.");
        if (operation.Title is not null) page.Title = Clamp(operation.Title, 200);
        if (operation.Description is not null) page.Description = Clamp(operation.Description, 500);
        if (operation.NavigationLabel is not null)
            page.Navigation.Label = Clamp(operation.NavigationLabel, 120);
    }

    private static void AddCompositionNode(
        WebsiteContentDocument document,
        string route,
        string? parentId,
        WebsiteStudioAiOperation operation,
        string? beforeNodeId = null,
        IReadOnlySet<string>? allowedActions = null,
        IReadOnlySet<Guid>? allowedMediaAssets = null)
    {
        var type = (operation.NodeType ?? string.Empty).Trim().ToLowerInvariant();
        if (type.Length == 0) throw new ArgumentException("Website AI add_node requires a component type.");
        if (type is "form")
            throw new ArgumentException("Website AI cannot create an alternate form authority.");

        var id = string.IsNullOrWhiteSpace(operation.NodeId)
            ? Guid.NewGuid().ToString("N")
            : SanitizeId(operation.NodeId);
        if (id.Length == 0) throw new ArgumentException("Website AI returned an invalid component ID.");
        if (CompositionNode(document, route, id, required: false) is not null)
            throw new ArgumentException("Website AI returned a duplicate component ID.");

        if (!string.IsNullOrWhiteSpace(operation.ActionKey) &&
            !(allowedActions ?? new HashSet<string>()).Contains(operation.ActionKey))
            throw new ArgumentException("Website AI selected an unavailable canonical action.");
        if (operation.MediaAssetId.HasValue &&
            !(allowedMediaAssets ?? new HashSet<Guid>()).Contains(operation.MediaAssetId.Value))
            throw new ArgumentException("Website AI referenced media outside the authorized website library.");

        var node = new WebsiteCompositionNode
        {
            Id = id,
            Type = type,
            Tag = operation.Tag,
            ClassName = Clamp(operation.ClassName, 500),
            Text = Clamp(operation.Text, MaxPromptText),
            Title = Clamp(operation.Title, MaxPromptText),
            ActionKey = operation.ActionKey,
            Href = WebsiteContentSanitizer.SanitizeUrl(operation.Href),
            Alt = Clamp(operation.Alt, 500),
            MediaAssetId = operation.MediaAssetId,
            Style = operation.Style ?? new WebsiteStyleOverride(),
            Layout = operation.Layout ?? new WebsiteLayoutOverride()
        };

        var page = Page(document, route, create: false)
            ?? throw new ArgumentException("Website AI referenced a page that is not available.");
        List<WebsiteCompositionNode> list;
        if (string.IsNullOrWhiteSpace(parentId)) list = page.Composition;
        else
        {
            var parent = CompositionNode(document, route, parentId, required: true);
            parent.Children ??= [];
            list = parent.Children;
        }

        var before = SanitizeId(beforeNodeId);
        var index = before.Length == 0 ? -1 : list.FindIndex(item => item.Id == before);
        if (index >= 0) list.Insert(index, node); else list.Add(node);
    }

    private static void DeleteCompositionNode(
        WebsiteContentDocument document,
        string route,
        string? nodeId)
    {
        var id = SanitizeId(nodeId);
        if (id.Length == 0) throw new ArgumentException("Website AI requires a component to delete.");
        var page = Page(document, route, create: false)
            ?? throw new ArgumentException("Website AI referenced a page that is not available.");

        bool Remove(List<WebsiteCompositionNode> nodes)
        {
            var index = nodes.FindIndex(node => node.Id == id);
            if (index >= 0)
            {
                if (nodes[index].Type == "form" || !string.IsNullOrWhiteSpace(nodes[index].SystemKey))
                    throw new ArgumentException("Website AI cannot delete a protected system component.");
                nodes.RemoveAt(index);
                return true;
            }
            return nodes.Any(node => Remove(node.Children));
        }

        if (!Remove(page.Composition))
            throw new ArgumentException("Website AI referenced a component that is not available.");
    }

    private static void MoveCompositionNode(
        WebsiteContentDocument document,
        string route,
        string? nodeId,
        string? parentId,
        string? beforeNodeId)
    {
        var id = SanitizeId(nodeId);
        var page = Page(document, route, create: false)
            ?? throw new ArgumentException("Website AI referenced a page that is not available.");
        WebsiteCompositionNode? moving = null;

        bool Remove(List<WebsiteCompositionNode> nodes)
        {
            var index = nodes.FindIndex(node => node.Id == id);
            if (index >= 0)
            {
                moving = nodes[index];
                nodes.RemoveAt(index);
                return true;
            }
            return nodes.Any(node => Remove(node.Children));
        }

        if (id.Length == 0 || !Remove(page.Composition) || moving is null)
            throw new ArgumentException("Website AI referenced a component that is not available.");

        List<WebsiteCompositionNode> destination;
        if (string.IsNullOrWhiteSpace(parentId)) destination = page.Composition;
        else
        {
            var parent = CompositionNode(document, route, parentId, required: true);
            if (IsDescendant(moving, parent.Id))
                throw new ArgumentException("Website AI cannot move a component inside itself.");
            destination = parent.Children;
        }

        var before = SanitizeId(beforeNodeId);
        var index = before.Length == 0 ? -1 : destination.FindIndex(node => node.Id == before);
        if (index >= 0) destination.Insert(index, moving); else destination.Add(moving);
    }

    private static bool IsDescendant(WebsiteCompositionNode node, string id) =>
        node.Children.Any(child => child.Id == id || IsDescendant(child, id));

    private static void RequireCreateMode(string mode)
    {
        if (mode != "create")
            throw new ArgumentException("This operation is available only for creation assistance.");
    }

    private static void ApplyText(
        WebsiteContentDocument document,
        string route,
        string? selectedElementId,
        string? text)
    {
        var target = RequireTarget(selectedElementId);
        var value = Clamp(text, MaxPromptText) ?? string.Empty;
        if (TryExtra(document, route, target, out var extra, out var field))
        {
            if (field == "title") extra.Title = value;
            else extra.Text = value;
            return;
        }

        var element = Element(document, route, target, create: true)!;
        element.Text = value;
    }

    private static void ApplyStyle(
        WebsiteContentDocument document,
        string route,
        string? selectedElementId,
        string? breakpointKey,
        WebsiteStyleOverride? style)
    {
        var target = RequireTarget(selectedElementId);
        var targetStyle = style ?? new WebsiteStyleOverride();
        if (TryExtra(document, route, target, out var extra, out _))
        {
            SetStyle(document, extra, breakpointKey, targetStyle);
            return;
        }

        var element = Element(document, route, target, create: true)!;
        SetStyle(document, element, breakpointKey, targetStyle);
    }

    private static void ApplyLayout(
        WebsiteContentDocument document,
        string route,
        string? selectedElementId,
        string? breakpointKey,
        WebsiteLayoutOverride? layout)
    {
        var target = RequireTarget(selectedElementId);
        var targetLayout = layout ?? new WebsiteLayoutOverride();
        if (TryExtra(document, route, target, out var extra, out _))
        {
            SetLayout(document, extra, breakpointKey, targetLayout);
            return;
        }

        var element = Element(document, route, target, create: true)!;
        SetLayout(document, element, breakpointKey, targetLayout);
    }

    private static void SetStyle(
        WebsiteContentDocument document,
        WebsiteElementOverride element,
        string? breakpointKey,
        WebsiteStyleOverride style)
    {
        var key = ValidateBreakpoint(document, breakpointKey);
        if (key is null) element.Style = style;
        else element.BreakpointStyles[key] = style;
    }

    private static void SetStyle(
        WebsiteContentDocument document,
        WebsiteExtraComponent extra,
        string? breakpointKey,
        WebsiteStyleOverride style)
    {
        var key = ValidateBreakpoint(document, breakpointKey);
        if (key is null) extra.Style = style;
        else extra.BreakpointStyles[key] = style;
    }

    private static void SetLayout(
        WebsiteContentDocument document,
        WebsiteElementOverride element,
        string? breakpointKey,
        WebsiteLayoutOverride layout)
    {
        var key = ValidateBreakpoint(document, breakpointKey);
        if (key is null) element.Layout = layout;
        else element.BreakpointLayouts[key] = layout;
    }

    private static void SetLayout(
        WebsiteContentDocument document,
        WebsiteExtraComponent extra,
        string? breakpointKey,
        WebsiteLayoutOverride layout)
    {
        var key = ValidateBreakpoint(document, breakpointKey);
        if (key is null) extra.Layout = layout;
        else extra.BreakpointLayouts[key] = layout;
    }

    private static string? ValidateBreakpoint(WebsiteContentDocument document, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "base") return null;
        var key = value.Trim();
        if (!(document.Breakpoints ?? []).Any(breakpoint => breakpoint.Key == key))
            throw new ArgumentException("Website AI referenced an unavailable breakpoint.");
        return key;
    }

    private static void AddSection(
        WebsiteContentDocument document,
        string route,
        WebsiteStudioAiOperation operation)
    {
        var page = Page(document, route, create: true)!;
        if (page.Extras.Count >= 120) throw new ArgumentException("Website page block limit reached.");
        var id = Guid.NewGuid().ToString("N");
        page.Extras.Add(new WebsiteExtraComponent
        {
            Id = id,
            Type = "section",
            SectionId = "ai.root",
            Style = operation.Style ?? new WebsiteStyleOverride(),
            Layout = operation.Layout ?? new WebsiteLayoutOverride()
        });
        var copy = Clamp(operation.Text ?? operation.Title, MaxPromptText);
        if (!string.IsNullOrWhiteSpace(copy))
        {
            page.Extras.Add(new WebsiteExtraComponent
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = "text",
                SectionId = "extra:" + id,
                Text = copy,
                Style = new WebsiteStyleOverride()
            });
        }
    }

    private static void AddText(
        WebsiteContentDocument document,
        string route,
        string? selectedSectionId,
        string? text)
    {
        var section = RequireSection(selectedSectionId);
        Page(document, route, create: true)!.Extras.Add(new WebsiteExtraComponent
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "text",
            SectionId = section,
            Text = Clamp(text, MaxPromptText) ?? string.Empty,
            Style = new WebsiteStyleOverride()
        });
    }

    private static void AddButton(
        WebsiteContentDocument document,
        string route,
        string? selectedSectionId,
        string? text,
        string? href)
    {
        var section = RequireSection(selectedSectionId);
        Page(document, route, create: true)!.Extras.Add(new WebsiteExtraComponent
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "button",
            SectionId = section,
            Text = Clamp(text, 300) ?? "Learn more",
            Href = WebsiteContentSanitizer.SanitizeUrl(href),
            Target = "_self",
            Style = new WebsiteStyleOverride()
        });
    }

    private static WebsitePageDocument? Page(WebsiteContentDocument document, string route, bool create)
    {
        document.Pages ??= new(StringComparer.Ordinal);
        if (document.Pages.TryGetValue(route, out var page)) return page;
        if (!create) return null;
        page = new WebsitePageDocument
        {
            Navigation = new WebsitePageNavigation
            {
                Label = route == "/" ? "Home" : route.Split('/').LastOrDefault(),
                ShowInNavigation = false
            }
        };
        document.Pages[route] = page;
        return page;
    }

    private static WebsiteElementOverride? Element(
        WebsiteContentDocument document,
        string route,
        string target,
        bool create)
    {
        var page = Page(document, route, create);
        if (page is null) return null;
        if (page.Elements.TryGetValue(target, out var value)) return value;
        if (!create) return null;
        value = new WebsiteElementOverride();
        page.Elements[target] = value;
        return value;
    }

    private static bool TryExtra(
        WebsiteContentDocument document,
        string route,
        string target,
        out WebsiteExtraComponent extra,
        out string field)
    {
        extra = null!;
        field = "text";
        if (!target.StartsWith("extra:", StringComparison.Ordinal)) return false;
        var parts = target.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        var id = parts[1];
        field = parts.Length >= 3 && parts[2] == "title" ? "title" : "text";
        var page = Page(document, route, create: false);
        extra = page?.Extras.FirstOrDefault(item => item.Id == id)!;
        return extra is not null;
    }

    private static string RequireTarget(string? value)
    {
        var id = SanitizeId(value);
        return id.Length == 0
            ? throw new ArgumentException("Select a website element before requesting this AI change.")
            : id;
    }

    private static string RequireSection(string? value)
    {
        var id = SanitizeId(value);
        return id.Length == 0
            ? throw new ArgumentException("Select a website section before requesting new content.")
            : id;
    }

    private static string SanitizeId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return new string(value.Trim().Take(160)
            .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or ':')
            .ToArray());
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

    private static string? Clamp(string? value, int maximum)
    {
        if (value is null) return null;
        var clean = value.Replace("\0", string.Empty).Trim();
        return clean.Length <= maximum ? clean : clean[..maximum];
    }

    private static WebsiteContentDocument Clone(WebsiteContentDocument source)
    {
        var json = JsonSerializer.Serialize(source, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return JsonSerializer.Deserialize<WebsiteContentDocument>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? new WebsiteContentDocument();
    }
}
