namespace ParfaitApp.Models;

public sealed class ParfaitAnalyticsEventRequest
{
    public string EventName { get; set; } = "";
    public string? EventId { get; set; }
    public string? VisitorId { get; set; }
    public string? SessionId { get; set; }
    public string? PageKey { get; set; }
    public string? SectionKey { get; set; }
    public string? ElementKey { get; set; }
    public string? ButtonLabel { get; set; }
    public string? ProductId { get; set; }
    public string? ProductName { get; set; }
    public string? ProductSlug { get; set; }
    public string? Size { get; set; }
    public int? Quantity { get; set; }
    public int? ValueCents { get; set; }
    public string? OrderNumber { get; set; }
    public string? Url { get; set; }
    public string? Referrer { get; set; }
    public string? DeviceType { get; set; }
    public string? Browser { get; set; }
    public string? OperatingSystem { get; set; }
    public string? TimeZone { get; set; }
    public string? Language { get; set; }
    public int? ScreenWidth { get; set; }
    public int? ScreenHeight { get; set; }
    public int? ViewportWidth { get; set; }
    public int? ViewportHeight { get; set; }
    public int? ScrollPercent { get; set; }
    public long? DwellMilliseconds { get; set; }
    public long? EngagedMilliseconds { get; set; }
    public bool? IsBounceCandidate { get; set; }
    public bool? IsExitPage { get; set; }
    public bool? WebDriver { get; set; }
    public bool? IsHeadless { get; set; }
    public int? MouseMoveCount { get; set; }
    public int? HumanInteractionCount { get; set; }
    public int? VisibilityChangeCount { get; set; }
    public string? TrackingVersion { get; set; }
    public Dictionary<string, string?> Metadata { get; set; } = new();
}
