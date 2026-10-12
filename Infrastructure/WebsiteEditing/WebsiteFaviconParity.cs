using Microsoft.Extensions.Configuration;

namespace Infrastructure.WebsiteEditing;

/// <summary>
/// Stable app-shell projection of the published website favicon authority.
/// Future website publishes change the resolved icon without another app release.
/// </summary>
public static class WebsiteFaviconParity
{
    public static string PublicUrl(IConfiguration configuration, string siteKey, string? agentSlug = null)
    {
        if (siteKey is not (WebsiteEditorSiteKeys.Legend or WebsiteEditorSiteKeys.Protect))
            throw new ArgumentOutOfRangeException(nameof(siteKey), siteKey, "Only LEGEND and Protect app-shell favicon sources are supported.");

        var url = ApiBase(configuration) + "/api/website-content/public/" + siteKey + "/favicon";
        if (siteKey == WebsiteEditorSiteKeys.Protect && !string.IsNullOrWhiteSpace(agentSlug))
            url += "?agentSlug=" + Uri.EscapeDataString(agentSlug.Trim());
        return url;
    }

    // One public image-resolution rule for both the company/app favicon endpoint
    // and business custom-domain favicon responses. The document is selected by
    // each caller's immutable published-version lookup, never by its draft.
    public sealed record PublishedFavicon(byte[]? Bytes, string? ContentType, string? RedirectUrl);

    public static PublishedFavicon? ResolvePublished(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        foreach (var mime in new[] { "image/png", "image/jpeg", "image/webp" })
        {
            var prefix = "data:" + mime + ";base64,";
            if (!reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var encoded = reference[prefix.Length..];
                if (encoded.Length > 7_000_000) return null;
                var bytes = Convert.FromBase64String(encoded);
                if (bytes.Length > 5_000_000) return null;
                return new PublishedFavicon(bytes, mime, null);
            }
            catch (FormatException) { return null; }
        }
        return Uri.TryCreate(reference, UriKind.Absolute, out var uri) &&
               uri.Scheme == Uri.UriSchemeHttps
            ? new PublishedFavicon(null, null, reference)
            : null;
    }

    public static string FallbackUrl(IConfiguration configuration) =>
        ApiBase(configuration) + "/images/favicon/legend-favicon.jpg";

    private static string ApiBase(IConfiguration configuration) =>
        (configuration["WebsiteContentApiBaseUrl"] ?? "https://protect.mylegnd.com").TrimEnd('/');
}
