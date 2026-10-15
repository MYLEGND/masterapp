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
    public Dictionary<string, LegacyWebsiteElementRecord> Elements { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> SectionOrder { get; set; } = new(StringComparer.Ordinal);
    public List<LegacyWebsiteExtraComponent> Extras { get; set; } = new();
    public Dictionary<string, LegacyWebsiteReusableComponentDefinition> ReusableComponents { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, WebsiteCollectionDefinition> Collections { get; set; } = new(StringComparer.Ordinal);
    public WebsiteDesignTheme Theme { get; set; } = new();
    public DateTime? UpdatedUtc { get; set; }
}

internal sealed class LegacyWebsitePageDocument
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? TemplatePath { get; set; }
    public WebsitePageNavigation Navigation { get; set; } = new();
    public WebsiteDynamicPageBinding? DynamicBinding { get; set; }
    public Dictionary<string, LegacyWebsiteElementRecord> Elements { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> SectionOrder { get; set; } = new(StringComparer.Ordinal);
    public List<LegacyWebsiteExtraComponent> Extras { get; set; } = new();
}

internal sealed class LegacyWebsiteReusableComponentDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "section";
    public Dictionary<string, LegacyWebsiteElementRecord> Elements { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> SectionOrder { get; set; } = new(StringComparer.Ordinal);
    public List<LegacyWebsiteExtraComponent> Extras { get; set; } = new();
}


internal sealed class LegacyWebsiteElementRecord
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
    public LegacyWebsitePlacement? Placement { get; set; }
    public WebsiteVisualStyle Style { get; set; } = new();
    public Dictionary<string, WebsiteVisualStyle> BreakpointStyles { get; set; } = new(StringComparer.Ordinal);
    public WebsiteCompositionLayout Layout { get; set; } = new();
    public Dictionary<string, WebsiteCompositionLayout> BreakpointLayouts { get; set; } = new(StringComparer.Ordinal);
    public List<WebsiteAnimationBinding> Animations { get; set; } = new();
    public string? SyncSourceId { get; set; }
    public WebsiteDataBinding? DataBinding { get; set; }
}

internal sealed class LegacyWebsitePlacement
{
    public string SectionId { get; set; } = "";
    public string? BeforeId { get; set; }
    public string? ContainerId { get; set; }
    public bool Flow { get; set; }
    public int Column { get; set; } = 1;
    public int Span { get; set; } = 12;
}

internal sealed class LegacyWebsiteExtraComponent
{
    public List<WebsiteSignalBinding> Signals { get; set; } = new();
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SectionId { get; set; } = "";
    public string Type { get; set; } = "text";
    public string? TemplateSectionId { get; set; }
    public string? ActionKey { get; set; }
    public string? Href { get; set; }
    public string? Target { get; set; }
    public string? Alt { get; set; }
    public string? VideoUrl { get; set; }
    public LegacyWebsitePlacement? Placement { get; set; }
    public string? Title { get; set; }
    public string? Text { get; set; }
    public string? ImageDataUrl { get; set; }
    public WebsiteVisualStyle Style { get; set; } = new();
    public Dictionary<string, WebsiteVisualStyle> BreakpointStyles { get; set; } = new(StringComparer.Ordinal);
    public WebsiteCompositionLayout Layout { get; set; } = new();
    public Dictionary<string, WebsiteCompositionLayout> BreakpointLayouts { get; set; } = new(StringComparer.Ordinal);
    public List<WebsiteAnimationBinding> Animations { get; set; } = new();
    public string? SyncSourceId { get; set; }
    public WebsiteDataBinding? DataBinding { get; set; }
}
