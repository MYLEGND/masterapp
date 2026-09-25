using System;
using System.Text.Json;

namespace Shared.Analytics;

/// <summary>Interprets browser invocation evidence without asserting delivery to Meta.</summary>
public static class MetaSignalBrowserDispatch
{
    public static bool IsFailure(string status) => status is "pixel_unavailable" or "invocation_failed";

    public static string Resolve(bool browserInvoked, string? metadataJson)
    {
        if (browserInvoked) return "invoked";
        if (string.IsNullOrWhiteSpace(metadataJson)) return "unverified";
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            var root = document.RootElement;
            var status = ReadStatus(root, 0);
            if (status is "disabled" or "not_required" or "human_gate" or "pixel_unavailable" or "invocation_failed")
                return status;
            // A derived landing row is not evidence that a browser pixel was attempted.
            if (root.TryGetProperty("bridgeSource", out var source) && source.GetString() == "analytics_events" &&
                root.TryGetProperty("sourceAnalyticsEventType", out var eventType) &&
                eventType.ValueKind == JsonValueKind.String &&
                AnalyticsEventCatalog.TryGet(eventType.GetString() ?? "", out var definition) && definition.CountsAsLandingView)
                return "derived_analytics";
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        return "unverified";
    }

    private static string? ReadStatus(JsonElement node, int depth)
    {
        if (depth > 4 || node.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in node.EnumerateObject())
        {
            if (string.Equals(property.Name, "browserDispatchStatus", StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        }
        foreach (var property in node.EnumerateObject())
        {
            if (property.Name.Equals("analyticsMetadata", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("metadata", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("browserMetadata", StringComparison.OrdinalIgnoreCase))
            {
                var status = ReadStatus(property.Value, depth + 1);
                if (status is not null) return status;
            }
        }
        return null;
    }
}
