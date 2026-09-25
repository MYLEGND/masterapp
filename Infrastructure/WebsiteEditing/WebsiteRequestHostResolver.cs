using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.WebsiteEditing;

/// <summary>
/// Resolves the effective public website host. Direct requests use Request.Host.
/// Requests arriving through the single Cloudflare website-routing bridge may
/// supply the original customer hostname only when the bridge secret matches
/// and the transport reached the configured Protect origin.
/// </summary>
public static class WebsiteRequestHostResolver
{
    public const string OriginalHostHeader = "X-Legend-Original-Host";
    public const string BridgeSecretHeader = "X-Legend-Website-Bridge";

    public static string Resolve(
        HttpContext context,
        IConfiguration? configuration,
        bool allowLegendCommerceHost = false)
    {
        ArgumentNullException.ThrowIfNull(context);

        var directHost = NormalizeDirectHost(context.Request.Host.Host);
        if (configuration is null)
            return directHost;

        var expectedSecret = configuration["WebsiteRouting:BridgeSecret"];
        if (string.IsNullOrWhiteSpace(expectedSecret))
            return directHost;

        var originalHost = context.Request.Headers[OriginalHostHeader].ToString();
        var presentedSecret = context.Request.Headers[BridgeSecretHeader].ToString();
        if (string.IsNullOrWhiteSpace(originalHost) ||
            string.IsNullOrWhiteSpace(presentedSecret) ||
            !SecretEquals(expectedSecret, presentedSecret))
            return directHost;

        var bridgeOriginHost = ResolveBridgeOriginHost(configuration);
        if (string.IsNullOrWhiteSpace(bridgeOriginHost) ||
            !string.Equals(directHost, bridgeOriginHost, StringComparison.OrdinalIgnoreCase))
            return directHost;

        try
        {
            // Business-domain normalization rejects LEGEND-owned hostnames. The commerce
            // transport may explicitly allow only the public LEGEND storefront hosts;
            // portal/client hosts remain ineligible even with a valid bridge secret.
            return WebsiteDomainService.NormalizeHostname(originalHost);
        }
        catch (ArgumentException)
        {
            var normalizedOriginal = NormalizeDirectHost(originalHost);
            return allowLegendCommerceHost && IsLegendCommerceHost(normalizedOriginal)
                ? normalizedOriginal
                : directHost;
        }
    }

    private static string NormalizeDirectHost(string? host)
    {
        var value = (host ?? string.Empty).Trim().TrimEnd('.');
        if (value.Length == 0)
            return string.Empty;

        try
        {
            return new IdnMapping().GetAscii(value).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return value.ToLowerInvariant();
        }
    }

    private static string ResolveBridgeOriginHost(IConfiguration configuration)
    {
        var explicitHost = NormalizeDirectHost(configuration["WebsiteRouting:BridgeOriginHost"]);
        if (!string.IsNullOrWhiteSpace(explicitHost))
            return explicitHost;

        var configured = configuration["WebsiteContentApiBaseUrl"];
        if (Uri.TryCreate(configured, UriKind.Absolute, out var configuredUri) &&
            configuredUri.Scheme == Uri.UriSchemeHttps &&
            !string.IsNullOrWhiteSpace(configuredUri.IdnHost))
            return configuredUri.IdnHost.ToLowerInvariant();

        return "masterapp-protect.azurewebsites.net";
    }

    private static bool IsLegendCommerceHost(string host) =>
        host is "mylegnd.com" or "www.mylegnd.com" or "protect.mylegnd.com";

    private static bool SecretEquals(string expected, string presented)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        return expectedBytes.Length == presentedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, presentedBytes);
    }
}
