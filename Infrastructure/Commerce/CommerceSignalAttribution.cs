using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Shared.Analytics;
using Infrastructure.Analytics;

namespace Infrastructure.Commerce;

/// <summary>Current-session storefront campaign adapter; ownership never comes from attribution.</summary>
public static class CommerceSignalAttribution
{
    public const string CookieName = "pf_attribution";
    private static readonly string[] CampaignKeys =
    ["utm_source", "utm_medium", "utm_campaign", "utm_id", "utm_content", "fbclid", "oppref", "meta_campaign_id", "meta_adset_id", "meta_ad_id"];

    public static CommerceSignalContext Apply(CommerceSignalContext context, HttpRequest request)
    {
        Dictionary<string, string>? campaign = null;
        // The validated domain context owns the store. Never infer its identity from a cookie.
        if (request.Cookies.TryGetValue(CookieName, out var encoded) && encoded.Length <= 8192)
        {
            try
            {
                using var document = JsonDocument.Parse(Uri.UnescapeDataString(encoded));
                var envelope = document.RootElement;
                if (envelope.ValueKind == JsonValueKind.Object &&
                    envelope.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.String &&
                    Guid.TryParse(scope.GetString(), out var storeId) && storeId == context.CommerceBusinessId &&
                    envelope.TryGetProperty("sessionId", out var session) && session.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(context.SessionId) && session.GetString() == context.SessionId &&
                    envelope.TryGetProperty("updatedAt", out var updated) && updated.ValueKind == JsonValueKind.Number && updated.TryGetInt64(out var milliseconds) &&
                    milliseconds <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() &&
                    milliseconds >= DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeMilliseconds() &&
                    envelope.TryGetProperty("attribution", out var attribution) && attribution.ValueKind == JsonValueKind.Object)
                    campaign = JsonSerializer.Deserialize<Dictionary<string, string>>(attribution.GetRawText());
            }
            catch (Exception ex) when (ex is JsonException or UriFormatException) { }
        }
        // A fresh query is a whole campaign, not a field-wise blend with another ad.
        if (CampaignKeys.Any(key => !string.IsNullOrWhiteSpace(request.Query[key].FirstOrDefault())))
            campaign = CampaignKeys.ToDictionary(key => key, key => request.Query[key].FirstOrDefault() ?? string.Empty);
        string? Read(string key)
        {
            var value = campaign?.GetValueOrDefault(key);
            if (string.IsNullOrWhiteSpace(value)) return null;
            var trimmed = value.Trim();
            return trimmed[..Math.Min(trimmed.Length, 256)];
        }
        return context with
        {
            Fbclid = Read("fbclid"),
            Oppref = OpenAiClickReference.Normalize(Read("oppref")),
            Obref = UnifiedEventContextBuilder.ResolveOpenAiBrowserReference(request),
            UtmSource = Read("utm_source"),
            UtmMedium = Read("utm_medium"),
            UtmCampaign = Read("utm_campaign"),
            UtmId = Read("utm_id"),
            UtmContent = Read("utm_content"),
            MetaCampaignId = Read("meta_campaign_id"),
            MetaAdSetId = Read("meta_adset_id"),
            MetaAdId = Read("meta_ad_id")
        };
    }
}
