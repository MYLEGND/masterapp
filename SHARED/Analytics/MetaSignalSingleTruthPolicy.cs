using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Shared.Analytics;

public static class MetaSignalSingleTruthPolicy
{
    public const string DispatchOwner = "MetaSignalOutcomeDispatcherHostedService";
    public const string DecisionAuthority = "MetaSendAuthority";
    public const string AuthoritativeSendPath = "MetaSignalOutcomeDispatcherHostedService>MetaConversionsApiService";
    public const string DispatchEligibleMarker = "\"metaSingleTruthDispatchEligible\":true";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string BuildMetadataJson(
        string? eventName,
        Guid? leadId,
        string? sessionId,
        object? payload,
        bool isBrowserSignal,
        bool isServerAuthority,
        bool metaServerAuthorityEligible,
        bool metaSingleTruthDispatchEligible,
        string? metaPipelineOrigin = null)
    {
        var root = ToJsonObject(payload);
        var resolvedLeadId = leadId ?? TryReadLeadId(root);
        var normalizedEventName = Normalize(eventName) ?? "unknown";
        var normalizedSessionId = Normalize(sessionId);

        root["isBrowserSignal"] = isBrowserSignal;
        root["isServerAuthority"] = isServerAuthority;
        root["eventKey"] = MetaSignalEventCatalog.BuildEventKey(normalizedEventName, resolvedLeadId, normalizedSessionId);
        root["serverAuthorityWinsConflictResolution"] = true;
        root["browserPayloadCanOverrideServer"] = false;
        root["metaServerAuthorityEligible"] = metaServerAuthorityEligible;
        root["metaSingleTruthDispatchEligible"] = metaSingleTruthDispatchEligible;
        root["metaDispatchOwner"] = DispatchOwner;
        root["metaDecisionAuthority"] = DecisionAuthority;
        root["metaAuthoritativeSendPath"] = AuthoritativeSendPath;
        root["metaPipelineOrigin"] = Normalize(metaPipelineOrigin) ?? (isBrowserSignal ? "browser" : "server");

        return root.ToJsonString(JsonOptions);
    }

    public static bool CanBridgeToServerAuthority(string? mappedEventName, string? analyticsMetadataJson)
    {
        if (!MetaSignalEventCatalog.IsServerAuthorityEvent(mappedEventName))
            return true;

        return ReadBoolean(analyticsMetadataJson, "metaServerAuthorityEligible") == true;
    }

    public static bool CanDispatchServerAuthority(string? eventName, string? metadataJson)
    {
        if (!MetaSignalEventCatalog.IsServerAuthorityEvent(eventName))
            return false;

        return ReadBoolean(metadataJson, "isServerAuthority") == true &&
               ReadBoolean(metadataJson, "metaSingleTruthDispatchEligible") == true &&
               ReadBoolean(metadataJson, "serverAuthorityWinsConflictResolution") == true &&
               ReadBoolean(metadataJson, "browserPayloadCanOverrideServer") != true &&
               string.Equals(ReadString(metadataJson, "metaDispatchOwner"), DispatchOwner, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(ReadString(metadataJson, "metaDecisionAuthority"), DecisionAuthority, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAuthorizedCapiSource(string? eventName, string? authoritySource) =>
        !MetaSignalEventCatalog.IsServerAuthorityEvent(eventName) ||
        string.Equals(Normalize(authoritySource), DispatchOwner, StringComparison.OrdinalIgnoreCase);

    public static string? ReadString(string? metadataJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(metadataJson) || string.IsNullOrWhiteSpace(propertyName))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            foreach (var property in root.EnumerateObject())
            {
                if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    continue;

                return property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.GetRawText(),
                    JsonValueKind.True => bool.TrueString,
                    JsonValueKind.False => bool.FalseString,
                    _ => null
                };
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    public static bool? ReadBoolean(string? metadataJson, string propertyName)
    {
        var raw = ReadString(metadataJson, propertyName);
        return bool.TryParse(raw, out var parsed) ? parsed : null;
    }

    private static JsonObject ToJsonObject(object? payload)
    {
        if (payload is JsonObject jsonObject)
            return jsonObject.DeepClone().AsObject();

        if (payload is JsonNode jsonNode)
        {
            return jsonNode switch
            {
                JsonObject nodeObject => nodeObject.DeepClone().AsObject(),
                _ => new JsonObject { ["payload"] = jsonNode.DeepClone() }
            };
        }

        if (payload is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Object => JsonNode.Parse(element.GetRawText())?.AsObject() ?? new JsonObject(),
                JsonValueKind.Undefined or JsonValueKind.Null => new JsonObject(),
                _ => new JsonObject { ["payload"] = JsonValue.Create(element.GetRawText()) }
            };
        }

        return JsonSerializer.SerializeToNode(payload, JsonOptions) as JsonObject ?? new JsonObject();
    }

    private static Guid? TryReadLeadId(JsonObject root)
    {
        foreach (var propertyName in new[] { "LeadId", "leadId", "WebsiteLeadId", "websiteLeadId" })
        {
            if (!root.TryGetPropertyValue(propertyName, out var node) || node is null)
                continue;

            var raw = node.GetValueKind() == JsonValueKind.String
                ? node.GetValue<string>()
                : node.ToJsonString();

            if (Guid.TryParse(raw, out var value) && value != Guid.Empty)
                return value;
        }

        return null;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
