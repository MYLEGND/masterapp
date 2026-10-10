using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.WebsiteRuntime;

/// <summary>
/// Explicit, read-only, single-business storefront preview. Production purchase,
/// checkout, automation, management, and uploads remain on the legacy authority.
/// Releasing this code DOES NOT enable any preview by itself.
/// </summary>
public static class CommerceSharedHostPreviewGate
{
    private const string PreviewKey = "Commerce:SharedHostPreview:Enabled";
    private const string BusinessKey = "Commerce:SharedHostPreview:BusinessId";
    private const string HostKey = "Commerce:SharedHostPreview:Hostname";

    public static bool IsConfigured(IConfiguration configuration) =>
        string.Equals(configuration[PreviewKey], "true", StringComparison.OrdinalIgnoreCase) &&
        Guid.TryParse(configuration[BusinessKey], out var id) && id != Guid.Empty &&
        ValidHostname(configuration[HostKey]);

    public static bool MayRoute(
        IConfiguration configuration, Guid verifiedBusinessId, string host, PathString path, string method)
    {
        if (!IsConfigured(configuration) ||
            !Guid.TryParse(configuration[BusinessKey], out var enabledBusiness) ||
            enabledBusiness != verifiedBusinessId ||
            !string.Equals(host, configuration[HostKey]!.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
            return false;

        var value = path.Value ?? string.Empty;
        if (value is "/store" or "/store/" or "/store/cart" or "/store/terms" or "/store/privacy")
            return true;

        if (value.StartsWith("/store/product/", StringComparison.OrdinalIgnoreCase))
            return value.Length > "/store/product/".Length &&
                !value["/store/product/".Length..].Contains('/');

        // Read-only static files bundled with the exact source Parfait website.
        if (value.StartsWith("/store-assets/", StringComparison.OrdinalIgnoreCase))
            return !value.Contains("..", StringComparison.Ordinal) &&
                   !value.Contains('%');

        const string uploads = "/uploads/parfait-products/";
        if (!value.StartsWith(uploads, StringComparison.OrdinalIgnoreCase))
            return false;

        var relative = value[uploads.Length..];
        var segments = relative.Split('/');
        return segments.Length >= 2 &&
               segments.All(segment => segment.Length is > 0 and <= 255 &&
                   segment is not "." and not ".." &&
                   segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) &&
               (relative.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                relative.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                relative.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                relative.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
                relative.EndsWith(".gif", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ValidHostname(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var host = value.Trim();
        return host.Length <= 253 && host.Contains('.') &&
            Uri.CheckHostName(host) == UriHostNameType.Dns &&
            !host.EndsWith(".mylegnd.com", StringComparison.OrdinalIgnoreCase) &&
            !host.Equals("mylegnd.com", StringComparison.OrdinalIgnoreCase) &&
            !host.EndsWith(".azurewebsites.net", StringComparison.OrdinalIgnoreCase);
    }
}
