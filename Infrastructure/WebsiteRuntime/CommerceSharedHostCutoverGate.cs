using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.WebsiteRuntime;

/// <summary>
/// Explicit, fail-closed admission for one verified business storefront.
/// Legacy commerce stays authoritative when settings or file-parity proof are
/// missing. This never approves Cloudflare routing or production deployment.
/// </summary>
public static class CommerceSharedHostCutoverGate
{
    private const string Prefix = "Commerce:SharedHostCutover:";

    public static bool IsConfigured(IConfiguration config) =>
        string.Equals(config[Prefix + "Enabled"], "true", StringComparison.OrdinalIgnoreCase) &&
        Guid.TryParse(config[Prefix + "BusinessId"], out var businessId) &&
        businessId != Guid.Empty &&
        ValidHost(config[Prefix + "Hostname"]) &&
        Path.IsPathFullyQualified(config["Parfait:StorageRoot"] ?? "") &&
        IsLowerHex(config[Prefix + "ReconciledManifestSha256"], 64);

    public static bool IsAdmitted(IConfiguration config, Guid businessId, string hostname) =>
        IsConfigured(config) &&
        Guid.TryParse(config[Prefix + "BusinessId"], out var id) &&
        id == businessId &&
        string.Equals(config[Prefix + "Hostname"]?.Trim(), hostname, StringComparison.OrdinalIgnoreCase);

    public static bool MayRoute(IConfiguration config, Guid id, string host, PathString path, string method)
    {
        if (!IsAdmitted(config, id, host)) return false;
        var value = path.Value ?? "";
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method))
        {
            if (value is "/store" or "/store/" or "/store/cart" or "/store/checkout" or "/store/success" or "/store/privacy" or "/store/terms")
                return true;
            if (value.StartsWith("/store/product/", StringComparison.OrdinalIgnoreCase))
                return value.Length > 15 && !value[15..].Contains('/') &&
                       !value.Contains('%') && !value.Contains("..");
            if (value.StartsWith("/store-assets/", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("/uploads/parfait-products/", StringComparison.OrdinalIgnoreCase))
                return CommerceSharedHostPreviewGate.MayRouteAssetsOnly(value);
        }
        if (HttpMethods.IsPost(method))
            return value is "/store/cart/items" or "/store/checkout/quote" or
                "/store/checkout/lead" or "/store/checkout/pay";
        return false;
    }

    private static bool IsLowerHex(string? value, int length) =>
        value is not null && value.Length == length &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidHost(string? hostname)
    {
        var host = hostname?.Trim();
        return !string.IsNullOrWhiteSpace(host) &&
            Uri.CheckHostName(host) == UriHostNameType.Dns &&
            host.Contains('.') &&
            !host.EndsWith(".mylegnd.com", StringComparison.OrdinalIgnoreCase) &&
            !host.Equals("mylegnd.com", StringComparison.OrdinalIgnoreCase) &&
            !host.EndsWith(".azurewebsites.net", StringComparison.OrdinalIgnoreCase);
    }
}
