using Shared.Analytics;
using Domain.Entities;
using System.Text.Json;

namespace Infrastructure.WebsiteEditing;

/// <summary>
/// Server-owned binding between a public Website Studio page and a protected
/// application runtime. WebsiteContentDocument v3 owns presentation/content,
/// while these templates retain form state, validation, submission, results,
/// booking, attribution, and server event semantics.
/// </summary>
public static class WebsiteSystemTemplateAuthority
{
    public const string Prefix = "protect_template:";

    // One route-to-template authority. Known template keys are derived from this
    // map so validation and resolution cannot drift into parallel lists.
    private static readonly IReadOnlyDictionary<string, string> TemplatesByRoute =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/RiskAssessment"] = Prefix + "risk_assessment",
            ["/Quote/Life"] = Prefix + "life_wizard",
            ["/Quote/Whole-Life"] = Prefix + "life_wizard",
            ["/Quote/Term-Life"] = Prefix + "life_wizard",
            ["/Quote/Final-Expense"] = Prefix + "life_wizard",
            ["/Quote/Mortgage-Protection"] = Prefix + "life_wizard",
            ["/Quote/IUL"] = Prefix + "life_wizard",
            ["/Quote/Home"] = Prefix + "home_quote",
            ["/Quote/Auto"] = Prefix + "auto_quote",
            ["/Quote/Commercial"] = Prefix + "commercial_quote",
            ["/Quote/Disability"] = Prefix + "disability_quote",
            ["/Quote/Dental-Vision-Hearing"] = Prefix + "dvh_quote",
            ["/ThankYou"] = Prefix + "quote_thank_you"
        };

    private static readonly HashSet<string> KnownKeys =
        TemplatesByRoute.Values.ToHashSet(StringComparer.Ordinal);

    public static bool IsKnownTemplateKey(string? value) =>
        !string.IsNullOrWhiteSpace(value) && KnownKeys.Contains(value.Trim());

    public static bool IsRuntimeFormSystemKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith("protect_runtime_form:", StringComparison.Ordinal))
            return false;
        var suffix = value["protect_runtime_form:".Length..];
        return suffix.Length is > 0 and <= 120 &&
               suffix.All(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.');
    }

    public static string? Resolve(string siteKey, string? pagePath)
    {
        if (!string.Equals(siteKey, WebsiteEditorSiteKeys.Protect, StringComparison.Ordinal))
            return null;

        var path = NormalizeProtectPath(pagePath);
        return TemplatesByRoute.TryGetValue(path, out var templateKey) ? templateKey : null;
    }

    public static WebsiteContentDocument Apply(string siteKey, WebsiteContentDocument document)
    {
        foreach (var (path, page) in document.Pages)
            page.SystemTemplateKey = Resolve(siteKey, path);

        ApplySharedShellAuthority(siteKey, document);
        return document;
    }

    private static void ApplySharedShellAuthority(string siteKey, WebsiteContentDocument document)
    {
        foreach (var page in document.Pages.Values)
        {
            page.Composition = (page.Composition ?? [])
                .Where(node => !IsPageLocalShellCopy(node))
                .ToList();
        }

        var primarySeen = false;
        document.Shell.Header = CanonicalizeHeader(document.Shell?.Header ?? [], siteKey, ref primarySeen);
        CanonicalizePlatformAttributionTree(document.Shell?.Footer);
    }

    private static List<WebsiteCompositionNode> CanonicalizeHeader(
        IEnumerable<WebsiteCompositionNode> source,
        string siteKey,
        ref bool primarySeen)
    {
        var result = new List<WebsiteCompositionNode>();
        foreach (var node in source)
        {
            var classes = ClassTokens(node.ClassName);
            var tag = (node.Tag ?? string.Empty).Trim().ToLowerInvariant();
            var primary = string.Equals(node.SystemKey, "primary_navigation", StringComparison.Ordinal);
            var templateNavigation = !primary &&
                                     tag == "nav" &&
                                     classes.Contains("nav");

            if (templateNavigation &&
                string.Equals(siteKey, WebsiteEditorSiteKeys.Business, StringComparison.Ordinal))
                continue;

            if (primary)
            {
                if (primarySeen) continue;
                primarySeen = true;
            }

            CanonicalizePlatformAttribution(node);
            CanonicalizeMobileHeaderChrome(node, classes, tag);
            ApplyHeaderTypographyDefaults(node, siteKey, tag);

            if (primary && string.Equals(siteKey, WebsiteEditorSiteKeys.Business, StringComparison.Ordinal))
                node.Children = [];
            else
                node.Children = CanonicalizeHeader(node.Children ?? [], siteKey, ref primarySeen);

            result.Add(node);
        }

        return result;
    }

    private static bool IsPageLocalShellCopy(WebsiteCompositionNode node)
    {
        var tag = (node.Tag ?? string.Empty).Trim().ToLowerInvariant();
        var classes = ClassTokens(node.ClassName);
        return tag is "header" or "footer" ||
               string.Equals(node.SystemKey, "primary_navigation", StringComparison.Ordinal) ||
               classes.Contains("site-header") ||
               classes.Contains("site-footer");
    }

    private static HashSet<string> ClassTokens(string? value) =>
        (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);

    private static void CanonicalizePlatformAttributionTree(IEnumerable<WebsiteCompositionNode>? nodes)
    {
        foreach (var node in nodes ?? [])
        {
            CanonicalizePlatformAttribution(node);
            CanonicalizePlatformAttributionTree(node.Children);
        }
    }

    private static void CanonicalizePlatformAttribution(WebsiteCompositionNode node)
    {
        var classes = ClassTokens(node.ClassName);
        if (classes.Contains("legend-platform-attribution"))
        {
            node.Type = "link";
            node.Tag = "a";
            node.Text = "Legend®";
            node.Href = "https://www.mylegnd.com/";
            node.Target = "_self";
        }
        else if (classes.Contains("legend-platform-attribution-powered-label"))
        {
            node.Text = "Powered by";
        }
        else if (classes.Contains("legend-platform-attribution-designed-label"))
        {
            node.Text = "Website Designed by";
        }
    }

    private static void CanonicalizeMobileHeaderChrome(
        WebsiteCompositionNode node,
        HashSet<string> classes,
        string tag)
    {
        var kind =
            string.Equals(node.SystemKey, "primary_navigation", StringComparison.Ordinal) ||
            (tag == "nav" && classes.Contains("nav"))
                ? "navigation"
                : string.Equals(node.SystemBinding, "business_name", StringComparison.Ordinal) ||
                  classes.Contains("brand") ||
                  classes.Contains("brand-wordmark") ||
                  classes.Contains("business-brand-banner")
                    ? "brand"
                    : tag == "header" || classes.Contains("site-header")
                        ? "frame"
                        : null;

        if (kind is null) return;

        node.BreakpointStyles ??= new Dictionary<string, WebsiteVisualStyle>(StringComparer.Ordinal);
        if (node.BreakpointStyles.TryGetValue("mobile", out var mobile))
        {
            mobile.WidthPercent = null;
            mobile.HeightPx = null;
            mobile.OffsetXPercent = null;
            mobile.OffsetYPx = null;
            mobile.MinWidthPx = null;
            mobile.MaxWidthPx = null;
            mobile.MinHeightPx = null;
            mobile.MaxHeightPx = null;
            mobile.MarginTop = null;
            mobile.MarginBottom = null;
            mobile.MarginLeft = null;
            mobile.MarginRight = null;

            if (kind == "brand" && mobile.FontScale is > 1.35m)
                mobile.FontScale = 1.35m;
            if (kind == "navigation" && mobile.FontScale is > 1m)
                mobile.FontScale = 1m;
        }

        if (kind is "frame" or "navigation")
        {
            node.BreakpointLayouts ??= new Dictionary<string, WebsiteCompositionLayout>(StringComparer.Ordinal);
            node.BreakpointLayouts["mobile"] = new WebsiteCompositionLayout
            {
                Mode = "free",
                Direction = "column"
            };
        }
    }

    private static void ApplyHeaderTypographyDefaults(
        WebsiteCompositionNode node,
        string siteKey,
        string tag)
    {
        node.Style ??= new WebsiteVisualStyle();
        node.BreakpointStyles ??= new Dictionary<string, WebsiteVisualStyle>(StringComparer.Ordinal);

        var isBrandTitle =
            string.Equals(node.SystemBinding, "business_name", StringComparison.Ordinal) ||
            (string.Equals(siteKey, WebsiteEditorSiteKeys.Legend, StringComparison.Ordinal) &&
             tag == "strong" &&
             string.Equals((node.Text ?? string.Empty).Trim(), "LEGEND®", StringComparison.Ordinal));

        if (isBrandTitle)
        {
            if (node.Style.FontScale is null or <= 0) node.Style.FontScale = 3.5m;
            if (node.Style.FontWeight is null or <= 0) node.Style.FontWeight = 800;
            var mobile = GetOrCreateBreakpointStyle(node, "mobile");
            var tablet = GetOrCreateBreakpointStyle(node, "tablet");
            if (mobile.FontScale is null or <= 0) mobile.FontScale = 1.35m;
            if (mobile.FontWeight is null or <= 0) mobile.FontWeight = 800;
            if (tablet.FontScale is null or <= 0) tablet.FontScale = 1.8m;
            if (tablet.FontWeight is null or <= 0) tablet.FontWeight = 800;
        }

        if (string.Equals(node.SystemKey, "primary_navigation", StringComparison.Ordinal))
        {
            if (node.Style.FontScale is null or <= 0) node.Style.FontScale = 1.6m;
            if (node.Style.FontWeight is null or <= 0) node.Style.FontWeight = 800;
            var mobile = GetOrCreateBreakpointStyle(node, "mobile");
            var tablet = GetOrCreateBreakpointStyle(node, "tablet");
            if (mobile.FontScale is null or <= 0) mobile.FontScale = 1m;
            if (mobile.FontWeight is null or <= 0) mobile.FontWeight = 800;
            if (tablet.FontScale is null or <= 0) tablet.FontScale = 1.15m;
            if (tablet.FontWeight is null or <= 0) tablet.FontWeight = 800;
        }
    }

    private static WebsiteVisualStyle GetOrCreateBreakpointStyle(WebsiteCompositionNode node, string key)
    {
        if (!node.BreakpointStyles.TryGetValue(key, out var style))
        {
            style = new WebsiteVisualStyle();
            node.BreakpointStyles[key] = style;
        }

        return style;
    }

    public static string? RuntimeFormKey(string? pagePath)
    {
        var path = NormalizeProtectPath(pagePath);
        if (!TemplatesByRoute.TryGetValue(path, out var template) || template == Prefix + "quote_thank_you")
            return null;
        var route = ProtectRouteCatalog.Routes.Single(route => route.Path == path);
        return "protect_runtime_form:" + route.PageKey +
               (template == Prefix + "life_wizard" ? "" : "_form");
    }

    public static bool IsProtectedRuntimeSource(string? pageKeyOrPath) =>
        ResolveRuntimeRoute(pageKeyOrPath) is not null;

    /// <summary>Resolve the stable protected runtime-form node from one immutable published version.</summary>
    public static string? ResolvePublishedRuntimeFormElementId(WebsiteContentVersion? version, string? pageKeyOrPath)
    {
        var route = ResolveRuntimeRoute(pageKeyOrPath);
        if (version is null || route is null) return null;
        var expected = RuntimeFormKey(route);
        if (expected is null) return null;
        try
        {
            var document = WebsiteContentSanitizer.ReadPersisted(
                version.DocumentJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var matches = WebsiteSiteSource.Flatten(document)
                .Where(item => string.Equals(NormalizeProtectPath(item.PagePath), route, StringComparison.OrdinalIgnoreCase) &&
                               string.Equals(item.Node.SystemKey, expected, StringComparison.Ordinal))
                .Select(item => item.Node.Id)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? ResolveRuntimeRoute(string? pageKeyOrPath)
    {
        if (string.IsNullOrWhiteSpace(pageKeyOrPath)) return null;
        var value = pageKeyOrPath.Trim();
        if (value.StartsWith('/'))
        {
            var path = NormalizeProtectPath(value);
            return RuntimeFormKey(path) is null ? null : path;
        }
        if (value.EndsWith("_landing", StringComparison.OrdinalIgnoreCase))
            value = value[..^"_landing".Length];
        var route = ProtectRouteCatalog.Routes.FirstOrDefault(candidate =>
            string.Equals(candidate.PageKey, value, StringComparison.OrdinalIgnoreCase));
        return route is null || RuntimeFormKey(route.Path) is null ? null : route.Path;
    }

    // A submitted presentation may request only the exact runtime already
    // owned by this route. Never recover an arbitrary submitted system binding.
    public static void RestoreRuntimeForms(string siteKey, WebsiteContentDocument proposed, WebsiteContentDocument normalized)
    {
        foreach (var (path, page) in proposed.Pages)
        {
            var expected = Resolve(siteKey, path) is null ? null : RuntimeFormKey(path);
            var requested = WebsiteSiteSource.Flatten(proposed)
                .Where(item => item.PagePath == path && IsRuntimeFormSystemKey(item.Node.SystemKey))
                .Select(item => item.Node).ToArray();
            if (requested.Length > 1 || requested.Any(node => node.SystemKey != expected || node.Type != "container"))
                throw new WebsiteSiteSourceProtectionException("Protected runtime form does not match the server-owned page route.");
            foreach (var node in requested)
            {
                var target = WebsiteSiteSource.Flatten(normalized).Single(item => item.PagePath == path && item.Node.Id == node.Id).Node;
                target.SystemKey = expected;
            }
        }
    }

    private static string NormalizeProtectPath(string? pagePath)
    {
        var path = string.IsNullOrWhiteSpace(pagePath) ? "/" : pagePath.Trim();
        if (path.StartsWith("/a/", StringComparison.OrdinalIgnoreCase))
        {
            var next = path.IndexOf('/', 3);
            path = next < 0 ? "/" : path[next..];
        }

        path = ProtectRouteCatalog.CanonicalPath(path);
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }
}
