using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shared.Meta;

public sealed class MetaLeadTrackingState
{
    public string? EventId { get; set; }
    public string? ResolvedMetaPixelId { get; set; }
    public string? PixelOwnerType { get; set; }
    public string? BrowserPixelStatus { get; set; }
    public DateTime? BrowserPixelUpdatedUtc { get; set; }
    public string? BrowserPixelNote { get; set; }
    public string? ServerCapiStatus { get; set; }
    public DateTime? ServerCapiUpdatedUtc { get; set; }
    public string? ServerCapiNote { get; set; }
}

public static class MetaLeadTrackingJson
{
    private const string MetaTrackingPropertyName = "MetaTracking";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static MetaLeadTrackingState? Read(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return null;

        try
        {
            var root = JsonNode.Parse(metadataJson) as JsonObject;
            return ReadFromRoot(root);
        }
        catch
        {
            return null;
        }
    }

    public static string Upsert(string? metadataJson, Action<MetaLeadTrackingState> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        var root = ParseRoot(metadataJson);
        var state = ReadFromRoot(root) ?? new MetaLeadTrackingState();
        mutate(state);
        root[MetaTrackingPropertyName] = JsonSerializer.SerializeToNode(state, JsonOptions);
        return root.ToJsonString(JsonOptions);
    }

    private static JsonObject ParseRoot(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return new JsonObject();

        try
        {
            return JsonNode.Parse(metadataJson) as JsonObject ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    private static MetaLeadTrackingState? ReadFromRoot(JsonObject? root)
    {
        if (root == null || root[MetaTrackingPropertyName] == null)
            return null;

        try
        {
            return root[MetaTrackingPropertyName]?.Deserialize<MetaLeadTrackingState>(JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
