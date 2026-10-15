using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shared.Analytics;

/// <summary>Canonical OpenAI attribution metadata enrichment for durable lead snapshots.</summary>
public static class PaidAdsClickReference
{
    public static string? NormalizeGoogle(string? value) => Normalize(value);
    public static string? NormalizeTikTok(string? value) => Normalize(value);

    private static string? Normalize(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 512 || text.Any(char.IsControl))
            return null;
        return text;
    }
}

public static class OpenAiAttributionMetadata
{
    public static string? WithBrowserReference(string? existingJson, string? obref) =>
        WithPaidClickReferences(existingJson, obref, null, null);

    public static string? WithPaidClickReferences(
        string? existingJson,
        string? obref,
        string? gclid,
        string? ttclid)
    {
        var browserReference = OpenAiBrowserReference.Normalize(obref);
        var googleClick = PaidAdsClickReference.NormalizeGoogle(gclid);
        var tikTokClick = PaidAdsClickReference.NormalizeTikTok(ttclid);
        if (browserReference is null && googleClick is null && tikTokClick is null)
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

        if (browserReference is not null) root["Obref"] = browserReference;
        if (googleClick is not null) root["Gclid"] = googleClick;
        if (tikTokClick is not null) root["Ttclid"] = tikTokClick;
        return root.ToJsonString();
    }
}
