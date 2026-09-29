namespace Infrastructure.WebsiteEditing;

/// <summary>
/// Read-only DTOs for pre-v3 persisted website JSON. These types are never returned as
/// the canonical editor document and never serialized by v3 save/publish. Their only
/// purpose is one-way materialization of an existing effective site into WebsiteContentDocument v3.
/// </summary>
internal sealed class LegacyWebsiteContentDocument
{
    public int Version { get; set; } = 2;
    public string? FaviconImageDataUrl { get; set; }
    public WebsiteStoreSettings Store { get; set; } = new();
    public List<WebsiteBreakpointDefinition> Breakpoints { get; set; } = WebsiteStudioContract.DefaultBreakpoints();
    public Dictionary<string, LegacyWebsitePageDocument> Pages { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, WebsiteElementOverride> Elements { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> SectionOrder { get; set; } = new(StringComparer.Ordinal);
    public List<WebsiteExtraComponent> Extras { get; set; } = new();
    public Dictionary<string, LegacyWebsiteReusableComponentDefinition> ReusableComponents { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, WebsiteCollectionDefinition> Collections { get; set; } = new(StringComparer.Ordinal);
    public WebsiteThemeOverride Theme { get; set; } = new();
    public DateTime? UpdatedUtc { get; set; }
}

internal sealed class LegacyWebsitePageDocument
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? TemplatePath { get; set; }
    public WebsitePageNavigation Navigation { get; set; } = new();
    public WebsiteDynamicPageBinding? DynamicBinding { get; set; }
    public Dictionary<string, WebsiteElementOverride> Elements { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> SectionOrder { get; set; } = new(StringComparer.Ordinal);
    public List<WebsiteExtraComponent> Extras { get; set; } = new();
}

internal sealed class LegacyWebsiteReusableComponentDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "section";
    public Dictionary<string, WebsiteElementOverride> Elements { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> SectionOrder { get; set; } = new(StringComparer.Ordinal);
    public List<WebsiteExtraComponent> Extras { get; set; } = new();
}
