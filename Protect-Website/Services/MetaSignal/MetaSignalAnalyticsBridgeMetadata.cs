using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Entities;
using Shared.Analytics;

namespace ProtectWebsite.Services.MetaSignal;

internal static class MetaSignalAnalyticsBridgeMetadata
{
    public const string BridgeSource = "analytics_events";
    public const string BridgeSourceMarker = "\"bridgeSource\":\"analytics_events\"";

    public static string Build(
        AnalyticsEvent source,
        string mappedEventName,
        string deduplicationKey,
        string trafficType,
        Guid? resolvedLeadId,
        string? pageVariant,
        string? pageMode,
        string? upstreamMetaEventId = null,
        string? upstreamMetaServerStatus = null,
        string? upstreamMetaServerNote = null)
    {
        var isServerAuthority = MetaSignalEventCatalog.IsServerAuthorityEvent(mappedEventName);
        var isBrowserSignal = MetaSignalEventCatalog.IsBrowserSignalEvent(mappedEventName);
        var eventKey = MetaSignalEventCatalog.BuildEventKey(mappedEventName, resolvedLeadId, source.SessionId);
        var metaServerAuthorityEligible =
            isServerAuthority &&
            MetaSignalSingleTruthPolicy.CanBridgeToServerAuthority(mappedEventName, source.MetadataJson);
        var metaSingleTruthDispatchEligible = isServerAuthority && metaServerAuthorityEligible;

        var root = new JsonObject
        {
            ["bridgeSource"] = BridgeSource,
            ["bridgeVersion"] = 1,
            ["sourceAnalyticsEventId"] = source.Id,
            ["sourceAnalyticsGuid"] = source.EventId.ToString("D"),
            ["sourceAnalyticsEventType"] = source.EventType,
            ["sourceAnalyticsEventUtc"] = source.EventUtc,
            ["sourceAnalyticsReceivedUtc"] = source.ReceivedUtc,
            ["mappedMetaSignalEventName"] = mappedEventName,
            ["resolvedLeadId"] = resolvedLeadId?.ToString("D"),
            ["resolvedTrafficType"] = trafficType,
            ["metaDeduplicationKey"] = deduplicationKey,
            ["eventKey"] = eventKey,
            ["isBrowserSignal"] = isBrowserSignal,
            ["isServerAuthority"] = isServerAuthority,
            ["serverAuthorityWinsConflictResolution"] = true,
            ["browserPayloadCanOverrideServer"] = false,
            ["metaServerAuthorityEligible"] = metaServerAuthorityEligible,
            ["metaSingleTruthDispatchEligible"] = metaSingleTruthDispatchEligible,
            ["metaDispatchOwner"] = MetaSignalSingleTruthPolicy.DispatchOwner,
            ["metaDecisionAuthority"] = MetaSignalSingleTruthPolicy.DecisionAuthority,
            ["metaAuthoritativeSendPath"] = MetaSignalSingleTruthPolicy.AuthoritativeSendPath,
            ["metaPipelineOrigin"] = BridgeSource,
            ["sourceUrl"] = source.Url,
            ["sourcePath"] = source.Path,
            ["sourceReferrer"] = source.Referrer,
            ["sourceClientIpAddress"] = source.IpAddress,
            ["sourceClientUserAgent"] = source.UserAgent,
            ["sourceFbclid"] = source.Fbclid,
            ["sourcePageKey"] = source.PageKey,
            ["sourceQuoteType"] = source.QuoteType,
            ["sourceSessionId"] = source.SessionId,
            ["sourceVisitorId"] = source.VisitorId,
            ["sourcePageVariant"] = pageVariant,
            ["sourcePageMode"] = pageMode,
            ["upstreamMetaEventId"] = upstreamMetaEventId,
            ["upstreamMetaServerStatus"] = upstreamMetaServerStatus,
            ["upstreamMetaServerNote"] = upstreamMetaServerNote,
            ["utmSource"] = source.UtmSource,
            ["utmMedium"] = source.UtmMedium,
            ["utmCampaign"] = source.UtmCampaign,
            ["utmId"] = source.UtmId,
            ["utmContent"] = source.UtmContent,
            ["utmTerm"] = source.UtmTerm,
            ["metaCampaignId"] = source.MetaCampaignId,
            ["metaCampaignName"] = source.MetaCampaignName,
            ["metaAdSetId"] = source.MetaAdSetId,
            ["metaAdSetName"] = source.MetaAdSetName,
            ["metaAdId"] = source.MetaAdId,
            ["metaAdName"] = source.MetaAdName,
            ["placement"] = source.Placement,
            ["formId"] = source.FormId,
            ["fieldName"] = source.FieldName,
            ["elementId"] = source.ElementId
        };

        if (!string.IsNullOrWhiteSpace(source.MetadataJson))
        {
            try
            {
                root["analyticsMetadata"] = JsonNode.Parse(source.MetadataJson);
            }
            catch
            {
                root["analyticsMetadataRaw"] = source.MetadataJson;
            }
        }

        return root.ToJsonString();
    }

    public static bool IsBridgeOwned(string? metadataJson) =>
        string.Equals(ReadString(metadataJson, "bridgeSource"), BridgeSource, StringComparison.OrdinalIgnoreCase);

    public static string? ReadString(string? metadataJson, string propertyName)
    {
        if (TryReadProperty(metadataJson, propertyName, out var element))
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                _ => null
            };
        }

        return null;
    }

    public static long? ReadInt64(string? metadataJson, string propertyName)
    {
        if (!TryReadProperty(metadataJson, propertyName, out var element))
            return null;

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var numeric))
            return numeric;

        if (element.ValueKind == JsonValueKind.String &&
            long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    public static bool TryReadGuid(string? metadataJson, string propertyName, out Guid value)
    {
        value = Guid.Empty;
        var raw = ReadString(metadataJson, propertyName);
        return Guid.TryParse(raw, out value) && value != Guid.Empty;
    }

    private static bool TryReadProperty(string? metadataJson, string propertyName, out JsonElement value)
    {
        value = default;

        if (string.IsNullOrWhiteSpace(metadataJson) || string.IsNullOrWhiteSpace(propertyName))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return false;

            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value.Clone();
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }
}
