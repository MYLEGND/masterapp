using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Infrastructure.Commerce;

/// <summary>First-touch storefront campaign adapter; ownership never comes from attribution.</summary>
public static class CommerceSignalAttribution
{
    public const string CookieName = "pf_attribution";

    public static CommerceSignalContext Apply(CommerceSignalContext context, HttpRequest request)
    {
        Dictionary<string, string>? firstTouch = null;
        if (request.Cookies.TryGetValue(CookieName, out var encoded) && encoded.Length <= 4096)
        {
            try { firstTouch = JsonSerializer.Deserialize<Dictionary<string, string>>(Uri.UnescapeDataString(encoded)); }
            catch (Exception ex) when (ex is JsonException or UriFormatException) { }
        }
        string? Read(string key)
        {
            var value = firstTouch?.GetValueOrDefault(key);
            if (string.IsNullOrWhiteSpace(value)) value = request.Query[key].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(value)) return null;
            return value.Trim()[..Math.Min(value.Trim().Length, 256)];
        }
        return context with
        {
            Fbclid = Read("fbclid") ?? context.Fbclid,
            UtmSource = Read("utm_source") ?? context.UtmSource,
            UtmMedium = Read("utm_medium") ?? context.UtmMedium,
            UtmCampaign = Read("utm_campaign") ?? context.UtmCampaign,
            UtmId = Read("utm_id") ?? context.UtmId,
            UtmContent = Read("utm_content") ?? context.UtmContent,
            MetaCampaignId = Read("meta_campaign_id") ?? context.MetaCampaignId,
            MetaAdSetId = Read("meta_adset_id") ?? context.MetaAdSetId,
            MetaAdId = Read("meta_ad_id") ?? context.MetaAdId
        };
    }
}
