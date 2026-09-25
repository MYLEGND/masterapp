using System.Text.Json.Serialization;

namespace Infrastructure.WebsiteEditing;

public static class WebsiteEditorSiteKeys
{
    public const string Protect = "protect";
    public const string Legend = "legend";
    public const string Business = "business";
    public const string GlobalOwnerKey = "__legend_global__";
    public const string BusinessOwnerPrefix = "business:";
    public const string ToolPrefix = "PublicWebsiteContent:";

    public static string BusinessOwnerKey(Guid businessId) =>
        BusinessOwnerPrefix + businessId.ToString("N");
}

public sealed record WebsiteEditorTicket(
    string SiteKey,
    string OwnerUserId,
    string? AgentSlug,
    bool IsFounder,
    DateTime ExpiresUtc,
    Guid? CommerceBusinessId = null,
    string? ActorUserId = null,
    string? ActorEmail = null,
    Guid? ActorClientProfileId = null);

public sealed class WebsiteContentDocument
{
    public int Version { get; set; } = 1;
    public string? FaviconImageDataUrl { get; set; }
    public Dictionary<string, WebsitePageDocument> Pages { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, WebsiteElementOverride> Elements { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> SectionOrder { get; set; } = new(StringComparer.Ordinal);
    public List<WebsiteExtraComponent> Extras { get; set; } = new();
    public WebsiteThemeOverride Theme { get; set; } = new();
    public List<WebsiteBreakpointDefinition> Breakpoints { get; set; } = WebsiteBreakpointCatalog.Defaults();
    public DateTime? UpdatedUtc { get; set; }
}

public sealed class WebsiteBreakpointDefinition
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public int MaxWidthPx { get; set; }
}

public static class WebsiteBreakpointCatalog
{
    public const int MaxBreakpoints = 6;
    public const int MinBreakpointWidth = 320;
    public const int MaxBreakpointWidth = 2560;

    public static List<WebsiteBreakpointDefinition> Defaults() =>
    [
        new() { Id = "tablet", Label = "Tablet", MaxWidthPx = 1024 },
        new() { Id = "mobile", Label = "Mobile", MaxWidthPx = 640 }
    ];
}

public sealed class WebsiteResponsiveOverride
{
    public bool? Hidden { get; set; }
    public WebsiteStyleOverride Style { get; set; } = new();
    public WebsiteLayoutOverride Layout { get; set; } = new();
}

public sealed class WebsiteLayoutOverride
{
    public string? Mode { get; set; }
    public int? Columns { get; set; }
    public int? Rows { get; set; }
    public decimal? ColumnGap { get; set; }
    public decimal? RowGap { get; set; }
    public string? Direction { get; set; }
    public string? AlignItems { get; set; }
    public string? JustifyContent { get; set; }
    public bool? Wrap { get; set; }
    public string? Overflow { get; set; }
}

public sealed class WebsiteElementOverride
{
    public List<WebsiteSignalBinding> Signals { get; set; } = new();
    public string? Text { get; set; }
    public string? ImageDataUrl { get; set; }
    public bool? Hidden { get; set; }
    public string? ActionKey { get; set; }
    public string? Href { get; set; }
    public string? Target { get; set; }
    public string? Alt { get; set; }
    public string? VideoUrl { get; set; }
    public WebsitePlacement? Placement { get; set; }
    public WebsiteStyleOverride Style { get; set; } = new();
    public WebsiteLayoutOverride Layout { get; set; } = new();
    public Dictionary<string, WebsiteResponsiveOverride> Responsive { get; set; } = new(StringComparer.Ordinal);
}

public sealed class WebsiteStyleOverride
{
    public string? TextAlign { get; set; }
    public decimal? FontScale { get; set; }
    public decimal? WidthPercent { get; set; }
    public decimal? PaddingTop { get; set; }
    public decimal? PaddingBottom { get; set; }
    public string? ObjectPosition { get; set; }
    public string? Color { get; set; }
    public string? BackgroundColor { get; set; }
    public string? FontFamily { get; set; }
    public int? FontWeight { get; set; }
    public decimal? FontSize { get; set; }
    public decimal? LineHeight { get; set; }
    public decimal? LetterSpacing { get; set; }
    public decimal? PaddingLeft { get; set; }
    public decimal? PaddingRight { get; set; }
    public decimal? BorderRadius { get; set; }
    public string? ObjectFit { get; set; }
    public decimal? HeightPx { get; set; }
    public decimal? OffsetXPercent { get; set; }
    public decimal? OffsetYPx { get; set; }
    public decimal? MinWidthPx { get; set; }
    public decimal? MaxWidthPx { get; set; }
    public decimal? MinHeightPx { get; set; }
    public decimal? MaxHeightPx { get; set; }
    public decimal? MarginTop { get; set; }
    public decimal? MarginRight { get; set; }
    public decimal? MarginBottom { get; set; }
    public decimal? MarginLeft { get; set; }
    public decimal? Opacity { get; set; }
    public decimal? RotationDeg { get; set; }
    public decimal? ScaleX { get; set; }
    public decimal? ScaleY { get; set; }
    public int? ZIndex { get; set; }
    public string? PositionMode { get; set; }
    public decimal? AspectRatio { get; set; }

}

public sealed class WebsitePlacement
{
    public string SectionId { get; set; } = "";
    public string? BeforeId { get; set; }
    public string? ContainerId { get; set; }
    public bool Flow { get; set; }
    public int Column { get; set; } = 1;
    public int Span { get; set; } = 12;
}

public sealed class WebsiteExtraComponent
{
    public List<WebsiteSignalBinding> Signals { get; set; } = new();
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SectionId { get; set; } = "";
    public string Type { get; set; } = "text";
    public string? ActionKey { get; set; }
    public string? Href { get; set; }
    public string? Target { get; set; }
    public string? Alt { get; set; }
    public string? VideoUrl { get; set; }
    public WebsitePlacement? Placement { get; set; }
    public string? Title { get; set; }
    public string? Text { get; set; }
    public string? ImageDataUrl { get; set; }
    public WebsiteStyleOverride Style { get; set; } = new();
    public WebsiteLayoutOverride Layout { get; set; } = new();
    public Dictionary<string, WebsiteResponsiveOverride> Responsive { get; set; } = new(StringComparer.Ordinal);
}

public sealed class WebsiteThemeOverride
{
    public string? Navy { get; set; }
    public string? NavyDeep { get; set; }
    public string? Gold { get; set; }
    public string? GoldStrong { get; set; }
    public string? Muted { get; set; }
    public string? Surface { get; set; }
    public string? Text { get; set; }
    public string? FontFamily { get; set; }
    public decimal? FontSize { get; set; }
    public decimal? BorderRadius { get; set; }
}


public sealed record BusinessWebsiteProfileSummary(
    Guid BusinessId,
    string BusinessName,
    string PreviewUrl,
    string? PrimaryDomain);

public sealed class WebsitePageDocument
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public Dictionary<string, WebsiteElementOverride> Elements { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> SectionOrder { get; set; } = new(StringComparer.Ordinal);
    public List<WebsiteExtraComponent> Extras { get; set; } = new();
}

public sealed class WebsiteNamedDraft
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public WebsiteContentDocument Document { get; set; } = new();
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}


public static class WebsiteLayoutModeCatalog
{
    public const string Flow = "flow";
    public const string Grid = "grid";
    public const string Flex = "flex";
    public const string Stack = "stack";
    public const string Free = "free";
    public static IReadOnlyList<string> All { get; } = [Flow, Grid, Flex, Stack, Free];
    public static bool IsAllowed(string? value) => All.Contains(value ?? string.Empty, StringComparer.Ordinal);
}

public sealed record WebsiteComponentCapability(
    string Type,
    string Label,
    string Group,
    bool InlineText,
    bool SupportsMedia,
    bool SupportsAction,
    bool CanContainChildren,
    IReadOnlyList<string> LayoutModes,
    IReadOnlyList<string> Triggers);

public static class WebsiteComponentCatalog
{
    public static IReadOnlyList<WebsiteComponentCapability> Options { get; } =
    [
        new("text", "Text", "Basic", true, false, false, false, [WebsiteLayoutModeCatalog.Flow], ["viewed"]),
        new("image", "Image", "Media", false, true, false, false, ["flow"], ["viewed", "click"]),
        new("button", "Button / link", "Basic", true, false, true, false, ["flow"], ["viewed", "click"]),
        new("video", "Video", "Media", false, true, false, false, ["flow"], ["viewed", "click"]),
        new("card", "Card", "Layout", true, false, false, true, [WebsiteLayoutModeCatalog.Flow, WebsiteLayoutModeCatalog.Grid, WebsiteLayoutModeCatalog.Flex, WebsiteLayoutModeCatalog.Stack], ["viewed", "click"]),
        new("section", "Section", "Layout", false, false, false, true, [WebsiteLayoutModeCatalog.Flow, WebsiteLayoutModeCatalog.Grid, WebsiteLayoutModeCatalog.Flex, WebsiteLayoutModeCatalog.Stack, WebsiteLayoutModeCatalog.Free], ["viewed", "scroll_threshold"]),
        new("code", "Code / embed", "Advanced", false, false, false, false, ["flow"], ["viewed"])
    ];

    public static WebsiteComponentCapability? Find(string? type) =>
        Options.FirstOrDefault(option => string.Equals(option.Type, type, StringComparison.OrdinalIgnoreCase));
}

public sealed record WebsiteCallToActionOption(
    string Key,
    string Group,
    string Label,
    string DefaultText,
    string Href,
    bool OpenInNewTab = false,
    string AnalyticsEventName = "cta_click",
    string? MetaIntentEventName = null);

/// <summary>
/// One scope-aware CTA catalog for the shared LEGEND website editor. Dynamic contact
/// and booking actions are offered only when the existing scoped authority is configured.
/// </summary>
public static class WebsiteCallToActionCatalog
{
    public static IReadOnlyList<WebsiteCallToActionOption> Build(
        string siteKey,
        string? phone = null,
        string? email = null,
        string? bookingUrl = null)
    {
        var options = new List<WebsiteCallToActionOption>();
        void Add(
            string key,
            string group,
            string label,
            string defaultText,
            string? href,
            bool external = false,
            string analytics = "cta_click",
            string? metaIntent = null)
        {
            if (string.IsNullOrWhiteSpace(href) || href == "#") return;
            if (!Shared.Analytics.AnalyticsEventCatalog.TryGet(analytics, out var analyticsDefinition) ||
                !analyticsDefinition.AllowBrowser)
                throw new InvalidOperationException("CTA analytics must use the canonical browser event catalog.");
            if (!string.IsNullOrWhiteSpace(metaIntent) &&
                (!Shared.Analytics.MetaSignalEventCatalog.TryGet(metaIntent, out var metaDefinition) ||
                 Shared.Analytics.MetaSignalEventCatalog.IsServerAuthorityEvent(metaIntent) ||
                 !metaDefinition.AllowBrowserPixel))
                throw new InvalidOperationException("CTA Meta intent must be a canonical browser signal.");
            options.Add(new(key, group, label, defaultText, href, external, analytics, metaIntent));
        }

        if (siteKey == WebsiteEditorSiteKeys.Legend)
        {
            Add("legend_home", "Navigation", "Home", "Home", "/");
            Add("legend_about", "Navigation", "About LEGEND®", "About LEGEND®", "/about");
            Add("legend_contact", "Contact", "Contact LEGEND®", "Contact Us", "/contact", metaIntent: "ContactStepReached");
            Add("legend_protect", "LEGEND®", "Legacy Protection", "Protect What Matters", "https://protect.mylegnd.com/", true);
        }
        else if (siteKey == WebsiteEditorSiteKeys.Protect)
        {
            Add("protect_home", "Navigation", "Home", "Home", "/");
            Add("protect_contact", "Contact", "Contact", "Contact Us", "/Contact");
            Add("protect_quote", "Quotes", "Coverage / quote options", "Get a Quote", "/Quote", analytics: "quote_click");
            foreach (var route in Shared.Analytics.ProtectRouteCatalog.Routes.Where(route => !string.IsNullOrWhiteSpace(route.QuoteType)))
                Add("protect_" + route.PageKey, "Quotes", route.DisplayName + " quote", "Get " + route.DisplayName + " Quote", route.Path, analytics: "quote_click");
            Add("protect_call", "Contact", "Call the attached agent", "Call Now", NormalizePhone(phone), metaIntent: "ContactStepReached");
            Add("protect_schedule", "Scheduling", "Schedule with the attached agent", "Schedule a Meeting", NormalizeHttps(bookingUrl), true, metaIntent: "ContactStepReached");
        }
        else if (siteKey == WebsiteEditorSiteKeys.Business)
        {
            Add("business_home", "Navigation", "Home", "Home", "/");
            Add("business_about", "Navigation", "About", "About Us", "/about");
            Add("business_services", "Navigation", "Services", "View Services", "/services");
            Add("business_contact", "Contact", "Contact form", "Contact Us", "/contact", metaIntent: "ContactStepReached");
            Add("business_quote", "Contact", "Request a quote through the contact form", "Get a Quote", "/contact", metaIntent: "ContactStepReached");
            Add("business_call", "Contact", "Call the business", "Call Now", NormalizePhone(phone), metaIntent: "ContactStepReached");
            Add("business_email", "Contact", "Email the business", "Email Us", NormalizeEmail(email), metaIntent: "ContactStepReached");
            Add("business_schedule", "Scheduling", "Schedule with the business", "Schedule a Meeting", NormalizeHttps(bookingUrl), true, metaIntent: "ContactStepReached");
        }

        return options;
    }

    public static string? PrepareForPublish(
        WebsiteContentDocument document,
        IReadOnlyList<WebsiteCallToActionOption> options)
    {
        var byKey = options.ToDictionary(option => option.Key, StringComparer.Ordinal);
        string? ResolveElement(WebsiteElementOverride element)
        {
            if (string.IsNullOrWhiteSpace(element.ActionKey)) return null;
            if (!byKey.TryGetValue(element.ActionKey, out var option))
                return "A configured button action is no longer available. Reopen the editor and choose an active action.";
            element.Href = option.Href;
            element.Target = option.OpenInNewTab ? "_blank" : "_self";
            return null;
        }
        string? ResolveExtra(WebsiteExtraComponent extra)
        {
            if (!string.IsNullOrWhiteSpace(extra.ActionKey))
            {
                if (!byKey.TryGetValue(extra.ActionKey, out var option))
                    return "A configured button action is no longer available. Reopen the editor and choose an active action.";
                extra.Href = option.Href;
                extra.Target = option.OpenInNewTab ? "_blank" : "_self";
            }
            if (extra.Type == "button" &&
                (string.IsNullOrWhiteSpace(extra.Href) || extra.Href == "#" ||
                 WebsiteContentSanitizer.SanitizeUrl(extra.Href) is null))
                return "Every added button needs a working action or custom destination before publishing.";
            return null;
        }

        foreach (var element in document.Elements.Values)
        {
            var error = ResolveElement(element);
            if (error is not null) return error;
        }
        foreach (var extra in document.Extras)
        {
            var error = ResolveExtra(extra);
            if (error is not null) return error;
        }
        foreach (var page in document.Pages.Values)
        {
            foreach (var element in page.Elements.Values)
            {
                var error = ResolveElement(element);
                if (error is not null) return error;
            }
            foreach (var extra in page.Extras)
            {
                var error = ResolveExtra(extra);
                if (error is not null) return error;
            }
        }
        return null;
    }

    private static string? NormalizePhone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        if (digits.Length < 7 || digits.Length > 15) return null;
        return "tel:" + (trimmed.StartsWith('+') ? "+" : "") + digits;
    }

    private static string? NormalizeEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !System.Net.Mail.MailAddress.TryCreate(value.Trim(), out var address))
            return null;
        return "mailto:" + address.Address;
    }

    private static string? NormalizeHttps(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length != 0)
            return null;
        return uri.ToString();
    }
}
