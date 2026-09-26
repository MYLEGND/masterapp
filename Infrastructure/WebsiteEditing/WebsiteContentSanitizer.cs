namespace Infrastructure.WebsiteEditing;

public static class WebsiteContentSanitizer
{
    private const int MaxElements = 600;
    private const int MaxExtras = 120;
    private const int MaxTextLength = 12000;
    private const int MaxCodeLength = 100000;
    private const int MaxImageDataUrlLength = 3500000;
    public static WebsiteContentDocument Sanitize(WebsiteContentDocument source)
    {
        var breakpoints = SanitizeBreakpoints(source.Breakpoints);
        var breakpointKeys = breakpoints.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var clean = new WebsiteContentDocument
        {
            Version = WebsiteStudioContract.CurrentDocumentVersion,
            FaviconImageDataUrl = SanitizeImage(source.FaviconImageDataUrl),
            Store = new WebsiteStoreSettings
            {
                Enabled = source.Store?.Enabled == true,
                NavigationLabel = SanitizeStoreLabel(source.Store?.NavigationLabel),
                CartIcon = SanitizeCartIcon(source.Store?.CartIcon)
            },
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
            if (id.Length == 0) continue;
            clean.SectionOrder[id] = Math.Clamp(pair.Value, 0, MaxElements);
        }

        foreach (var extra in (source.Extras ?? new()).Take(MaxExtras))
        {
            if (extra is null) continue;
            var id = SanitizeId(extra.Id);
            var sectionId = SanitizeId(extra.SectionId);
            var type = (extra.Type ?? string.Empty).Trim().ToLowerInvariant();
            if (id.Length == 0 || sectionId.Length == 0 || type is not ("text" or "image" or "button" or "video" or "section" or "card" or "form" or "code" or "reusable")) continue;
            clean.Extras.Add(new WebsiteExtraComponent
            {
                Id = id,
                SectionId = sectionId,
                Type = type,
                Signals = type == "reusable" ? new List<WebsiteSignalBinding>() : WebsiteSignalBindingPolicy.Validate(extra.Signals),
                ActionKey = SanitizeActionKey(extra.ActionKey),
                Title = ClampContentText(extra.Title),
                Text = type == "code" ? ClampCodeText(extra.Text) : ClampContentText(extra.Text),
                Href = SanitizeUrl(extra.Href), Target = SanitizeTarget(extra.Target),
                Alt = ClampText(extra.Alt), VideoUrl = SanitizeUrl(extra.VideoUrl, true),
                Placement = SanitizePlacement(extra.Placement),
                ImageDataUrl = type == "image" ? SanitizeImage(extra.ImageDataUrl) : null,
                Style = SanitizeStyle(extra.Style),
                BreakpointStyles = SanitizeStyleMap(extra.BreakpointStyles, breakpointKeys),
                Layout = SanitizeLayout(extra.Layout),
                BreakpointLayouts = SanitizeLayoutMap(extra.BreakpointLayouts, breakpointKeys),
                Animations = SanitizeAnimations(extra.Animations),
                SyncSourceId = NullIfEmpty(SanitizeId(extra.SyncSourceId)),
                DataBinding = SanitizeDataBinding(extra.DataBinding)
            });
        }

        foreach (var page in (source.Pages ?? new()).Take(100))
        {
            var path = page.Key;
            if (page.Value is null || !path.StartsWith('/') || path.StartsWith("//") || path.Contains('?') || path.Contains('#') || path.Contains("..") || path.Length > 2048) continue;
            var body = SanitizePageBody(page.Value, breakpointKeys);
            clean.Pages[path] = new WebsitePageDocument
            {
                Title = ClampText(page.Value.Title),
                Description = ClampText(page.Value.Description),
                TemplatePath = SanitizePagePath(page.Value.TemplatePath),
                Navigation = SanitizeNavigation(path, page.Value.Navigation),
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

    private static WebsiteElementOverride SanitizeElement(WebsiteElementOverride source, HashSet<string> breakpointKeys) => new()
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
        public Dictionary<string, WebsiteElementOverride> Elements { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> SectionOrder { get; } = new(StringComparer.Ordinal);
        public List<WebsiteExtraComponent> Extras { get; } = new();
    }

    private static SanitizedPageBody SanitizePageBody(WebsitePageDocument source, HashSet<string> breakpointKeys)
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
            if (extra is null) continue;
            var id = SanitizeId(extra.Id);
            var sectionId = SanitizeId(extra.SectionId);
            var type = (extra.Type ?? string.Empty).Trim().ToLowerInvariant();
            if (id.Length == 0 || sectionId.Length == 0 || type is not ("text" or "image" or "button" or "video" or "section" or "card" or "form" or "code" or "reusable")) continue;
            clean.Extras.Add(new WebsiteExtraComponent
            {
                Id = id, SectionId = sectionId, Type = type,
                Signals = type == "reusable" ? new List<WebsiteSignalBinding>() : WebsiteSignalBindingPolicy.Validate(extra.Signals),
                ActionKey = SanitizeActionKey(extra.ActionKey),
                Title = ClampContentText(extra.Title),
                Text = type == "code" ? ClampCodeText(extra.Text) : ClampContentText(extra.Text),
                Href = SanitizeUrl(extra.Href), Target = SanitizeTarget(extra.Target),
                Alt = ClampText(extra.Alt), VideoUrl = SanitizeUrl(extra.VideoUrl, true),
                Placement = SanitizePlacement(extra.Placement),
                ImageDataUrl = type == "image" ? SanitizeImage(extra.ImageDataUrl) : null,
                Style = SanitizeStyle(extra.Style),
                BreakpointStyles = SanitizeStyleMap(extra.BreakpointStyles, breakpointKeys),
                Layout = SanitizeLayout(extra.Layout),
                BreakpointLayouts = SanitizeLayoutMap(extra.BreakpointLayouts, breakpointKeys),
                Animations = SanitizeAnimations(extra.Animations),
                SyncSourceId = NullIfEmpty(SanitizeId(extra.SyncSourceId)),
                DataBinding = SanitizeDataBinding(extra.DataBinding)
            });
        }
        return clean;
    }

    private static List<WebsiteBreakpointDefinition> SanitizeBreakpoints(IEnumerable<WebsiteBreakpointDefinition>? source)
    {
        var result = WebsiteStudioContract.DefaultBreakpoints();
        var keys = result.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var item in source ?? [])
        {
            if (item is null || item.IsSystem || result.Count >= 8) continue;
            var key = SanitizeId(item.Key);
            if (key.Length == 0 || !keys.Add(key)) continue;
            var min = Math.Clamp(item.MinWidth, 0, 10000);
            var max = item.MaxWidth.HasValue ? Math.Clamp(item.MaxWidth.Value, min, 10000) : (int?)null;
            var label = ClampText(item.Label);
            label = string.IsNullOrWhiteSpace(label) ? key : label[..Math.Min(label.Length, 80)];
            result.Add(new WebsiteBreakpointDefinition { Key = key, Label = label, MinWidth = min, MaxWidth = max, IsSystem = false });
        }
        return result;
    }

    private static Dictionary<string, WebsiteStyleOverride> SanitizeStyleMap(IDictionary<string, WebsiteStyleOverride>? source, HashSet<string> breakpointKeys)
    {
        var clean = new Dictionary<string, WebsiteStyleOverride>(StringComparer.Ordinal);
        foreach (var pair in (source ?? new Dictionary<string, WebsiteStyleOverride>()).Take(12))
            if (breakpointKeys.Contains(pair.Key) && pair.Value is not null) clean[pair.Key] = SanitizeStyle(pair.Value);
        return clean;
    }

    private static WebsiteLayoutOverride SanitizeLayout(WebsiteLayoutOverride? source)
    {
        source ??= new WebsiteLayoutOverride();
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
        return new WebsiteLayoutOverride
        {
            Mode = mode, Direction = direction,
            GapPx = source.GapPx is >= 0 and <= 240 ? source.GapPx : null,
            Columns = source.Columns is >= 1 and <= 12 ? source.Columns : null,
            MinItemWidthPx = source.MinItemWidthPx is >= 1 and <= 4000 ? source.MinItemWidthPx : null,
            AlignItems = NullIfEmpty(align), JustifyContent = NullIfEmpty(justify), Wrap = NullIfEmpty(wrap)
        };
    }

    private static Dictionary<string, WebsiteLayoutOverride> SanitizeLayoutMap(IDictionary<string, WebsiteLayoutOverride>? source, HashSet<string> breakpointKeys)
    {
        var clean = new Dictionary<string, WebsiteLayoutOverride>(StringComparer.Ordinal);
        foreach (var pair in (source ?? new Dictionary<string, WebsiteLayoutOverride>()).Take(12))
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

    private static WebsitePageNavigation SanitizeNavigation(string pagePath, WebsitePageNavigation? source)
    {
        source ??= new WebsitePageNavigation();
        var parent = SanitizePagePath(source.ParentPath);
        if (string.Equals(parent, pagePath, StringComparison.Ordinal)) parent = null;
        var label = ClampText(source.Label);
        if (label?.Length > 120) label = label[..120];
        return new WebsitePageNavigation { Label = label, ShowInNavigation = source.ShowInNavigation, ParentPath = parent, Order = Math.Clamp(source.Order, -10000, 10000), IsDeleted = source.IsDeleted };
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
        var body = SanitizePageBody(new WebsitePageDocument { Elements = source.Elements, SectionOrder = source.SectionOrder, Extras = source.Extras }, breakpointKeys);
        body.Extras.RemoveAll(extra => extra.Type == "reusable");
        var name = ClampText(source.Name) ?? id;
        if (name.Length > 120) name = name[..120];
        return new WebsiteReusableComponentDefinition { Id = id, Name = name, Kind = kind, Elements = body.Elements, SectionOrder = body.SectionOrder, Extras = body.Extras };
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
    private static WebsiteThemeOverride SanitizeTheme(WebsiteThemeOverride? source)
    {
        source ??= new WebsiteThemeOverride();
        return new WebsiteThemeOverride
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

    private static WebsiteStyleOverride SanitizeStyle(WebsiteStyleOverride? source)
    {
        source ??= new WebsiteStyleOverride();
        var align = (source.TextAlign ?? string.Empty).Trim().ToLowerInvariant();
        if (align is not ("left" or "center" or "right" or "start" or "end" or "justify")) align = string.Empty;
        var objectPosition = (source.ObjectPosition ?? string.Empty).Trim().ToLowerInvariant();
        if (objectPosition is not ("left" or "center" or "right" or "top" or "bottom"))
            objectPosition = string.Empty;

        return new WebsiteStyleOverride
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
    private static WebsitePlacement? SanitizePlacement(WebsitePlacement? value)
    {
        if (value is null) return null;
        var section = SanitizeId(value.SectionId);
        if (section.Length == 0) return null;
        var column = Math.Clamp(value.Column, 1, 12);
        return new WebsitePlacement { SectionId = section, BeforeId = SanitizeId(value.BeforeId), ContainerId = SanitizeId(value.ContainerId), Flow = value.Flow, Column = column, Span = Math.Clamp(value.Span, 1, 13 - column) };
    }
}
