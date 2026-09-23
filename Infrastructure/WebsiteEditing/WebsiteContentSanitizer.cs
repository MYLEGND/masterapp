namespace Infrastructure.WebsiteEditing;

public static class WebsiteContentSanitizer
{
    private const int MaxElements = 600;
    private const int MaxExtras = 120;
    private const int MaxTextLength = 12000;
    private const int MaxImageDataUrlLength = 3500000;
    public static WebsiteContentDocument Sanitize(WebsiteContentDocument source)
    {
        var clean = new WebsiteContentDocument { Version = 1 };

        foreach (var pair in (source.Elements ?? new()).Take(MaxElements))
        {
            var id = SanitizeId(pair.Key);
            if (id.Length == 0 || pair.Value is null) continue;
            clean.Elements[id] = SanitizeElement(pair.Value);
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
            if (id.Length == 0 || sectionId.Length == 0 || type is not ("text" or "image" or "button" or "video" or "section" or "card")) continue;
            clean.Extras.Add(new WebsiteExtraComponent
            {
                Id = id,
                SectionId = sectionId,
                Type = type,
                Signals = WebsiteSignalBindingPolicy.Validate(extra.Signals),
                ActionKey = SanitizeActionKey(extra.ActionKey),
                Title = ClampContentText(extra.Title),
                Text = ClampContentText(extra.Text),
                Href = SanitizeUrl(extra.Href), Target = SanitizeTarget(extra.Target),
                Alt = ClampText(extra.Alt), VideoUrl = SanitizeUrl(extra.VideoUrl, true),
                Placement = SanitizePlacement(extra.Placement),
                ImageDataUrl = type == "image" ? SanitizeImage(extra.ImageDataUrl) : null,
                Style = SanitizeStyle(extra.Style)
            });
        }

        foreach (var page in (source.Pages ?? new()).Take(100))
        {
            var path = page.Key;
            if (page.Value is null || !path.StartsWith('/') || path.StartsWith("//") || path.Contains('?') || path.Contains('#') || path.Contains("..") || path.Length > 2048) continue;
            var body = Sanitize(new WebsiteContentDocument { Elements = page.Value.Elements, SectionOrder = page.Value.SectionOrder, Extras = page.Value.Extras });
            clean.Pages[path] = new WebsitePageDocument { Title = ClampText(page.Value.Title), Description = ClampText(page.Value.Description), Elements = body.Elements, SectionOrder = body.SectionOrder, Extras = body.Extras };
        }
        clean.Theme = SanitizeTheme(source.Theme);
        clean.UpdatedUtc = source.UpdatedUtc;
        return clean;
    }

    private static WebsiteElementOverride SanitizeElement(WebsiteElementOverride source) => new()
    {
        Signals = WebsiteSignalBindingPolicy.Validate(source.Signals),
        ActionKey = SanitizeActionKey(source.ActionKey),
        Text = ClampContentText(source.Text),
        ImageDataUrl = SanitizeImage(source.ImageDataUrl),
        Hidden = source.Hidden,
        Href = SanitizeUrl(source.Href), Target = SanitizeTarget(source.Target),
        Alt = ClampText(source.Alt), VideoUrl = SanitizeUrl(source.VideoUrl, true),
        Placement = SanitizePlacement(source.Placement),
        Style = SanitizeStyle(source.Style)
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
            ObjectFit = source.ObjectFit is "cover" or "contain" or "fill" or "none" or "scale-down" ? source.ObjectFit : null
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
