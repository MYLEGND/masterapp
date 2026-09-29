using System;
using System.Linq;

namespace Infrastructure.Analytics;

/// <summary>
/// Canonical Meta endpoint authority.
///
/// Meta's platform version is intentionally not pinned in application code. Versionless Graph
/// requests are resolved by Meta against the API version configured for the owning Meta app,
/// keeping campaigns, insights, OAuth/account discovery, and CAPI on one provider-controlled
/// version authority instead of allowing independent runtime constants to drift or expire.
///
/// Do not add per-service Graph API versions. All Meta HTTP paths must be built here.
/// </summary>
public static class MetaGraphEndpointAuthority
{
    private static readonly Uri GraphOrigin = new("https://graph.facebook.com/", UriKind.Absolute);
    private static readonly Uri LoginOrigin = new("https://www.facebook.com/", UriKind.Absolute);

    public static string Graph(string relativePath)
    {
        var path = NormalizeRelativePath(relativePath);
        return new Uri(GraphOrigin, path).AbsoluteUri;
    }

    public static string OAuthDialog() =>
        new Uri(LoginOrigin, "dialog/oauth").AbsoluteUri;

    private static string NormalizeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A Meta Graph relative path is required.", nameof(value));

        var path = value.Trim().TrimStart('/');
        if (Uri.TryCreate(path, UriKind.Absolute, out _) ||
            path.StartsWith("//", StringComparison.Ordinal) ||
            path.Contains('\\') ||
            path.Any(char.IsControl))
            throw new ArgumentException("Meta Graph paths must be relative.", nameof(value));

        return path;
    }
}
