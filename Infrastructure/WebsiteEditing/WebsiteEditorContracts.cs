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
    Guid? CommerceBusinessId = null);

public sealed class WebsiteContentDocument
{
    public int Version { get; set; } = 1;
    public Dictionary<string, WebsiteElementOverride> Elements { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> SectionOrder { get; set; } = new(StringComparer.Ordinal);
    public List<WebsiteExtraComponent> Extras { get; set; } = new();
    public WebsiteThemeOverride Theme { get; set; } = new();
    public DateTime? UpdatedUtc { get; set; }
}

public sealed class WebsiteElementOverride
{
    public string? Text { get; set; }
    public string? ImageDataUrl { get; set; }
    public bool? Hidden { get; set; }
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
}

public sealed class WebsiteExtraComponent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SectionId { get; set; } = "";
    public string Type { get; set; } = "text";
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
    public string? Surface { get; set; }
}
