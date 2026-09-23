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
    public Dictionary<string, WebsitePageDocument> Pages { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, WebsiteElementOverride> Elements { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> SectionOrder { get; set; } = new(StringComparer.Ordinal);
    public List<WebsiteExtraComponent> Extras { get; set; } = new();
    public WebsiteThemeOverride Theme { get; set; } = new();
    public DateTime? UpdatedUtc { get; set; }
}

public sealed class WebsiteElementOverride
{
    public List<WebsiteSignalBinding> Signals { get; set; } = new();
    public string? Text { get; set; }
    public string? ImageDataUrl { get; set; }
    public bool? Hidden { get; set; }
    public string? Href { get; set; }
    public string? Target { get; set; }
    public string? Alt { get; set; }
    public string? VideoUrl { get; set; }
    public WebsitePlacement? Placement { get; set; }
    public WebsiteStyleOverride Style { get; set; } = new();
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

}

public sealed class WebsitePlacement
{
    public string SectionId { get; set; } = "";
    public string? BeforeId { get; set; }
    public int Column { get; set; } = 1;
    public int Span { get; set; } = 12;
}

public sealed class WebsiteExtraComponent
{
    public List<WebsiteSignalBinding> Signals { get; set; } = new();
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SectionId { get; set; } = "";
    public string Type { get; set; } = "text";
    public string? Href { get; set; }
    public string? Target { get; set; }
    public string? Alt { get; set; }
    public string? VideoUrl { get; set; }
    public WebsitePlacement? Placement { get; set; }
    public string? Text { get; set; }
    public string? ImageDataUrl { get; set; }
    public WebsiteStyleOverride Style { get; set; } = new();
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
