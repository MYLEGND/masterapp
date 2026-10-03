using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shared.Analytics;

/// <summary>Canonical OpenAI attribution metadata enrichment for durable lead snapshots.</summary>
public static class OpenAiAttributionMetadata
{
    public static string? WithBrowserReference(string? existingJson, string? obref)
    {
        var browserReference = OpenAiBrowserReference.Normalize(obref);
        if (browserReference is null)
            return string.IsNullOrWhiteSpace(existingJson) ? null : existingJson.Trim();

        JsonObject root;
        try
        {
            root = string.IsNullOrWhiteSpace(existingJson)
                ? new JsonObject()
                : JsonNode.Parse(existingJson)?.AsObject() ?? new JsonObject();
        }
        catch (JsonException)
        {
            root = new JsonObject { ["legacyMetadataRaw"] = existingJson };
        }

        root["Obref"] = browserReference;
        return root.ToJsonString();
    }
}
