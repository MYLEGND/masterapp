namespace ParfaitApp.Models;

public sealed class ParfaitAnalyticsEventRequest
{
    public string EventName { get; set; } = "";
    public string? EventId { get; set; }
    public string? VisitorId { get; set; }
    public string? SessionId { get; set; }
    public string? ProductId { get; set; }
    public string? ProductName { get; set; }
    public string? ProductSlug { get; set; }
    public string? Size { get; set; }
    public int? Quantity { get; set; }
    public int? ValueCents { get; set; }
    public string? OrderNumber { get; set; }
    public string? Url { get; set; }
    public string? Referrer { get; set; }
    public Dictionary<string, string?> Metadata { get; set; } = new();
}
