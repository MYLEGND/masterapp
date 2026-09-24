using Microsoft.Extensions.Configuration;

namespace Infrastructure.WebsiteEditing;

/// <summary>
/// Stable app-shell projection of the published website favicon authority.
/// Future website publishes change the resolved icon without another app release.
/// </summary>
public static class WebsiteFaviconParity
{
    public static string PublicUrl(IConfiguration configuration, string siteKey)
    {
        if (siteKey is not (WebsiteEditorSiteKeys.Legend or WebsiteEditorSiteKeys.Protect))
            throw new ArgumentOutOfRangeException(nameof(siteKey), siteKey, "Only LEGEND and Protect app-shell favicon sources are supported.");
        return ApiBase(configuration) + "/api/website-content/public/" + siteKey + "/favicon";
    }

    public static string FallbackUrl(IConfiguration configuration) =>
        ApiBase(configuration) + "/images/favicon/legend-favicon.svg";

    private static string ApiBase(IConfiguration configuration) =>
        (configuration["WebsiteContentApiBaseUrl"] ?? "https://protect.mylegnd.com").TrimEnd('/');
}
