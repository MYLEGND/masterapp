using Shared.Analytics;

namespace Infrastructure.Analytics;

/// <summary>Non-routable enrichment adapter. Ownership, identity, persistence and dedupe
/// are exclusively owned by the canonical public ingest pipeline.</summary>
public static class WebsiteAnalyticsIngestAuthority
{
    public static object? ParseMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { using var document = System.Text.Json.JsonDocument.Parse(json); return document.RootElement.Clone(); }
        catch (System.Text.Json.JsonException) { return new { malformedMetadataRaw = json }; }
    }

    public static UnifiedEventContext ApplySignalMetadata(UnifiedEventContext context, MetaSignalIngestRequest? signal)
    {
        if (signal is null) return context;
        var metadata = System.Text.Json.JsonSerializer.SerializeToNode(context.Metadata) as System.Text.Json.Nodes.JsonObject
            ?? new System.Text.Json.Nodes.JsonObject();
        // Preserve original canonical keys at their original depth. Browser
        // enrichment is namespaced so it cannot overwrite source authority.
        metadata["browserMetadata"] = signal.Metadata.ValueKind is System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined
            ? null : System.Text.Json.Nodes.JsonNode.Parse(signal.Metadata.GetRawText());
        metadata["UpstreamMetaEventId"] = context.EventId;
        metadata["Fbc"] = signal.Attribution?.Fbc;
        metadata["Fbp"] = signal.Attribution?.Fbp;
        metadata["ScoreTier"] = signal.ScoreTier;
        metadata["IntentScore"] = signal.Score?.IntentScore;
        metadata["EngagementScore"] = signal.Score?.EngagementScore;
        metadata["QualificationScore"] = signal.Score?.QualificationScore;
        metadata["FrictionScore"] = signal.Score?.FrictionScore;
        metadata["TotalSignalScore"] = signal.Score?.TotalSignalScore;
        return context with
        {
            EffectivePageKey = signal.EffectivePageKey ?? context.PageKey,
            PageVariant = signal.PageVariant,
            PageMode = signal.PageMode,
            StepNumber = signal.StepNumber,
            StepName = signal.StepName,
            BrowserEventSent = signal.BrowserEventSent,
            Metadata = metadata
        };
    }
}
