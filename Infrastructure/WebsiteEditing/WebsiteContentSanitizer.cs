namespace Infrastructure.WebsiteEditing;

public static class WebsiteContentSanitizer
{
    private const int MaxElements = 600;
    private const int MaxExtras = 120;
    private const int MaxReusableDefinitions = 40;
    private const int MaxReusableComponents = 80;
    private const int MaxTextLength = 12000;
    private const int MaxCodeLength = 100000;
    private const int MaxImageDataUrlLength = 3500000;
    public static WebsiteContentDocument Sanitize(WebsiteContentDocument source)
    {
        var clean = new WebsiteContentDocument
        {
            Version = 1,
            FaviconImageDataUrl = SanitizeImage(source.FaviconImageDataUrl),
            Breakpoints = SanitizeBreakpoints(source.Breakpoints)
        };
        var breakpointIds = clean.Breakpoints.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var pair in (source.ReusableComponents ?? new()).Take(MaxReusableDefinitions))
        {
            if (pair.Value is null) continue;
            var id = SanitizeId(string.IsNullOrWhiteSpace(pair.Key) ? pair.Value.Id : pair.Key);
            if (id.Length == 0 || clean.ReusableComponents.ContainsKey(id)) continue;

            var definition = new WebsiteReusableComponentDefinition
            {
                Id = id,
                Name = ClampText(pair.Value.Name) ?? "Reusable component",
                Style = SanitizeStyle(pair.Value.Style),
                Layout = SanitizeLayout(pair.Value.Layout),
                Responsive = SanitizeResponsive(pair.Value.Responsive, breakpointIds),
                UpdatedUtc = pair.Value.UpdatedUtc == default ? DateTime.UtcNow : pair.Value.UpdatedUtc
            };

            foreach (var component in (pair.Value.Components ?? new()).Take(MaxReusableComponents))
            {
                var cleanComponent = SanitizeExtra(component, breakpointIds, reusableDefinitionIds: null, allowReusable: false);
                if (cleanComponent is null || cleanComponent.Type == "section") continue;
                cleanComponent.SectionId = "__reusable__";
                definition.Components.Add(cleanComponent);
            }

            var definitionIds = definition.Components.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var component in definition.Components)
            {
                if (component.Placement is null) continue;
                component.Placement.SectionId = "__reusable__";
                if (!string.IsNullOrWhiteSpace(component.Placement.ContainerId))
                {
                    var container = component.Placement.ContainerId!;
                    if (!container.StartsWith("extra:", StringComparison.Ordinal) ||
                        !definitionIds.Contains(container["extra:".Length..]))
                        component.Placement.ContainerId = null;
                }
                if (!string.IsNullOrWhiteSpace(component.Placement.BeforeId) &&
                    !definitionIds.Contains(component.Placement.BeforeId!.StartsWith("extra:", StringComparison.Ordinal)
                        ? component.Placement.BeforeId["extra:".Length..]
                        : component.Placement.BeforeId))
                    component.Placement.BeforeId = null;
            }

            clean.ReusableComponents[id] = definition;
        }
        var reusableDefinitionIds = clean.ReusableComponents.Keys.ToHashSet(StringComparer.Ordinal);

        foreach (var pair in (source.Elements ?? new()).Take(MaxElements))
        {
            var id = SanitizeId(pair.Key);
            if (id.Length == 0 || pair.Value is null) continue;
            clean.Elements[id] = SanitizeElement(pair.Value, breakpointIds);
        }

        foreach (var pair in (source.SectionOrder ?? new()).Take(MaxElements))
        {
            var id = SanitizeId(pair.Key);
            if (id.Length == 0) continue;
            clean.SectionOrder[id] = Math.Clamp(pair.Value, 0, MaxElements);
        }

        foreach (var extra in (source.Extras ?? new()).Take(MaxExtras))
        {
            var cleanExtra = SanitizeExtra(extra, breakpointIds, reusableDefinitionIds, allowReusable: true);
            if (cleanExtra is not null) clean.Extras.Add(cleanExtra);
        }

        foreach (var page in (source.Pages ?? new()).Take(100))
        {
            var path = page.Key;
            if (page.Value is null || !path.StartsWith('/') || path.StartsWith("//") || path.Contains('?') || path.Contains('#') || path.Contains("..") || path.Length > 2048) continue;

            var cleanPage = new WebsitePageDocument
            {
                Title = ClampText(page.Value.Title),
                Description = ClampText(page.Value.Description)
            };
            foreach (var pair in (page.Value.Elements ?? new()).Take(MaxElements))
            {
                var id = SanitizeId(pair.Key);
                if (id.Length == 0 || pair.Value is null) continue;
                cleanPage.Elements[id] = SanitizeElement(pair.Value, breakpointIds);
            }
            foreach (var pair in (page.Value.SectionOrder ?? new()).Take(MaxElements))
            {
                var id = SanitizeId(pair.Key);
                if (id.Length == 0) continue;
                cleanPage.SectionOrder[id] = Math.Clamp(pair.Value, 0, MaxElements);
            }
            foreach (var extra in (page.Value.Extras ?? new()).Take(MaxExtras))
            {
                var cleanExtra = SanitizeExtra(extra, breakpointIds, reusableDefinitionIds, allowReusable: true);
                if (cleanExtra is not null) cleanPage.Extras.Add(cleanExtra);
            }
            clean.Pages[path] = cleanPage;
        }

        clean.Theme = SanitizeTheme(source.Theme);
        clean.UpdatedUtc = source.UpdatedUtc;
        return clean;
    }

    private static WebsiteExtraComponent? SanitizeExtra(
        WebsiteExtraComponent? extra,
        HashSet<string> breakpointIds,
        HashSet<string>? reusableDefinitionIds,
        bool allowReusable)
    {
        if (extra is null) return null;
        var id = SanitizeId(extra.Id);
        var sectionId = SanitizeId(extra.SectionId);
        var type = (extra.Type ?? string.Empty).Trim().ToLowerInvariant();
        var capability = WebsiteComponentCatalog.Find(type);
        if (id.Length == 0 || sectionId.Length == 0 || capability is null) return null;
        if (type == "reusable" && !allowReusable) return null;

        string? reusableDefinitionId = null;
        if (type == "reusable")
        {
            reusableDefinitionId = SanitizeId(extra.ReusableDefinitionId);
            if (reusableDefinitionId.Length == 0 || reusableDefinitionIds is null ||
                !reusableDefinitionIds.Contains(reusableDefinitionId))
                return null;
        }

        return new WebsiteExtraComponent
        {
            Id = id,
            SectionId = sectionId,
            Type = type,
            ReusableDefinitionId = reusableDefinitionId,
            EditorLocked = extra.EditorLocked,
            EditorLabel = ClampText(extra.EditorLabel),
            Signals = WebsiteSignalBindingPolicy.Validate(extra.Signals),
            Interactions = SanitizeInteractions(extra.Interactions),
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
            Layout = SanitizeLayout(extra.Layout, capability.LayoutModes),
            Responsive = SanitizeResponsive(extra.Responsive, breakpointIds, capability.LayoutModes)
        };
    }

    private static WebsiteElementOverride SanitizeElement(WebsiteElementOverride source, HashSet<string> breakpointIds) => new()
    {
        Signals = WebsiteSignalBindingPolicy.Validate(source.Signals),
        Interactions = SanitizeInteractions(source.Interactions),
        ActionKey = SanitizeActionKey(source.ActionKey),
        Text = ClampContentText(source.Text),
        ImageDataUrl = SanitizeImage(source.ImageDataUrl),
        Hidden = source.Hidden,
        EditorLocked = source.EditorLocked,
        EditorLabel = ClampText(source.EditorLabel),
        Href = SanitizeUrl(source.Href), Target = SanitizeTarget(source.Target),
        Alt = ClampText(source.Alt), VideoUrl = SanitizeUrl(source.VideoUrl, true),
        Placement = SanitizePlacement(source.Placement),
        Style = SanitizeStyle(source.Style),
        Layout = SanitizeLayout(source.Layout),
        Responsive = SanitizeResponsive(source.Responsive, breakpointIds)
    };

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
            HeightPx = BoundedPositive(source.HeightPx, 10000),
            OffsetXPercent = BoundedSigned(source.OffsetXPercent, 500),
            OffsetYPx = BoundedSigned(source.OffsetYPx, 10000),
            MinWidthPx = BoundedNonNegative(source.MinWidthPx, 10000),
            MaxWidthPx = BoundedPositive(source.MaxWidthPx, 10000),
            MinHeightPx = BoundedNonNegative(source.MinHeightPx, 10000),
            MaxHeightPx = BoundedPositive(source.MaxHeightPx, 10000),
            MarginTop = BoundedSigned(source.MarginTop, 2000),
            MarginRight = BoundedSigned(source.MarginRight, 2000),
            MarginBottom = BoundedSigned(source.MarginBottom, 2000),
            MarginLeft = BoundedSigned(source.MarginLeft, 2000),
            Opacity = source.Opacity is >= 0 and <= 1 ? source.Opacity : null,
            RotationDeg = BoundedSigned(source.RotationDeg, 3600),
            ScaleX = BoundedPositive(source.ScaleX, 20),
            ScaleY = BoundedPositive(source.ScaleY, 20),
            ZIndex = source.ZIndex is >= -10000 and <= 10000 ? source.ZIndex : null,
            PositionMode = source.PositionMode is "flow" or "relative" or "absolute" or "sticky" or "fixed" ? source.PositionMode : null,
            HorizontalAnchor = source.HorizontalAnchor is "left" or "center" or "right" or "stretch" ? source.HorizontalAnchor : null,
            VerticalAnchor = source.VerticalAnchor is "top" or "center" or "bottom" or "stretch" ? source.VerticalAnchor : null,
            InsetLeftPx = BoundedSigned(source.InsetLeftPx, 10000),
            InsetRightPx = BoundedSigned(source.InsetRightPx, 10000),
            InsetTopPx = BoundedSigned(source.InsetTopPx, 10000),
            InsetBottomPx = BoundedSigned(source.InsetBottomPx, 10000),
            AspectRatio = source.AspectRatio is > 0 and <= 20 ? source.AspectRatio : null
        };
    }

    private static List<WebsiteMotionInteraction> SanitizeInteractions(IEnumerable<WebsiteMotionInteraction>? source)
    {
        var result = new List<WebsiteMotionInteraction>();
        foreach (var item in (source ?? []).Take(WebsiteMotionCatalog.MaxInteractionsPerElement))
        {
            if (item is null ||
                !WebsiteMotionCatalog.AllowsTrigger(item.Trigger) ||
                !WebsiteMotionCatalog.AllowsEffect(item.Effect) ||
                !WebsiteMotionCatalog.AllowsEasing(item.Easing))
                continue;

            var direction = string.IsNullOrWhiteSpace(item.Direction) ? null : item.Direction.Trim().ToLowerInvariant();
            if (direction is not null && !WebsiteMotionCatalog.AllowsDirection(direction))
                direction = null;

            result.Add(new WebsiteMotionInteraction
            {
                Id = SanitizeId(item.Id) is { Length: > 0 } id ? id : Guid.NewGuid().ToString("N"),
                Trigger = item.Trigger,
                Effect = item.Effect,
                DurationMs = Math.Clamp(item.DurationMs, 50, 10000),
                DelayMs = Math.Clamp(item.DelayMs, 0, 10000),
                Easing = item.Easing,
                Once = item.Once,
                Direction = direction,
                DistancePx = BoundedNonNegative(item.DistancePx, 2000),
                Amount = item.Amount is >= -20 and <= 20 ? item.Amount : null
            });
        }
        return result;
    }

    private static List<WebsiteBreakpointDefinition> SanitizeBreakpoints(IEnumerable<WebsiteBreakpointDefinition>? source)
    {
        var values = (source ?? WebsiteBreakpointCatalog.Defaults())
            .Where(item => item is not null)
            .Select(item => new WebsiteBreakpointDefinition
            {
                Id = SanitizeId(item.Id),
                Label = ClampText(item.Label) ?? "",
                MaxWidthPx = Math.Clamp(item.MaxWidthPx, WebsiteBreakpointCatalog.MinBreakpointWidth, WebsiteBreakpointCatalog.MaxBreakpointWidth)
            })
            .Where(item => item.Id.Length > 0)
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(WebsiteBreakpointCatalog.MaxBreakpoints)
            .OrderByDescending(item => item.MaxWidthPx)
            .ToList();

        return values.Count == 0 ? WebsiteBreakpointCatalog.Defaults() : values;
    }

    private static Dictionary<string, WebsiteResponsiveOverride> SanitizeResponsive(
        Dictionary<string, WebsiteResponsiveOverride>? source,
        HashSet<string> breakpointIds,
        IReadOnlyList<string>? allowedLayoutModes = null)
    {
        var result = new Dictionary<string, WebsiteResponsiveOverride>(StringComparer.Ordinal);
        foreach (var pair in source ?? new())
        {
            var id = SanitizeId(pair.Key);
            if (!breakpointIds.Contains(id) || pair.Value is null) continue;
            result[id] = new WebsiteResponsiveOverride
            {
                Hidden = pair.Value.Hidden,
                Style = SanitizeStyle(pair.Value.Style),
                Layout = SanitizeLayout(pair.Value.Layout, allowedLayoutModes)
            };
        }
        return result;
    }

    private static WebsiteLayoutOverride SanitizeLayout(WebsiteLayoutOverride? source, IReadOnlyList<string>? allowedModes = null)
    {
        source ??= new WebsiteLayoutOverride();
        var mode = (source.Mode ?? string.Empty).Trim().ToLowerInvariant();
        if (!WebsiteLayoutModeCatalog.IsAllowed(mode) ||
            allowedModes is not null && !allowedModes.Contains(mode, StringComparer.Ordinal))
            mode = string.Empty;
        var direction = (source.Direction ?? string.Empty).Trim().ToLowerInvariant();
        if (direction is not ("row" or "column")) direction = string.Empty;
        var align = (source.AlignItems ?? string.Empty).Trim().ToLowerInvariant();
        if (align is not ("start" or "center" or "end" or "stretch" or "baseline")) align = string.Empty;
        var justify = (source.JustifyContent ?? string.Empty).Trim().ToLowerInvariant();
        if (justify is not ("start" or "center" or "end" or "space-between" or "space-around" or "space-evenly")) justify = string.Empty;
        var overflow = (source.Overflow ?? string.Empty).Trim().ToLowerInvariant();
        if (overflow is not ("visible" or "hidden" or "clip" or "auto" or "scroll")) overflow = string.Empty;

        return new WebsiteLayoutOverride
        {
            Mode = mode.Length == 0 ? null : mode,
            Columns = source.Columns is >= 1 and <= 24 ? source.Columns : null,
            Rows = source.Rows is >= 1 and <= 24 ? source.Rows : null,
            ColumnGap = BoundedNonNegative(source.ColumnGap, 500),
            RowGap = BoundedNonNegative(source.RowGap, 500),
            Direction = direction.Length == 0 ? null : direction,
            AlignItems = align.Length == 0 ? null : align,
            JustifyContent = justify.Length == 0 ? null : justify,
            Wrap = source.Wrap,
            Overflow = overflow.Length == 0 ? null : overflow
        };
    }

    private static decimal? BoundedPositive(decimal? value, decimal max) =>
        value is > 0 && value <= max ? value : null;

    private static decimal? BoundedNonNegative(decimal? value, decimal max) =>
        value is >= 0 && value <= max ? value : null;

    private static decimal? BoundedSigned(decimal? value, decimal max) =>
        value.HasValue && value.Value >= -max && value.Value <= max ? value : null;

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
