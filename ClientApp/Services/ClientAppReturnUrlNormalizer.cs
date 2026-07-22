using Microsoft.AspNetCore.WebUtilities;

namespace ClientApp.Services;

/// <summary>
/// Owns ClientApp post-authentication return-target validation. Authorization and
/// activation endpoints are terminal destinations, never continuations.
/// </summary>
public sealed class ClientAppReturnUrlNormalizer
{
    public const string SafeLandingPath = "/Home/Index";
    public const int MaximumReturnUrlLength = 2048;

    public string Normalize(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return SafeLandingPath;

        var target = candidate.Trim();
        if (target.Length > MaximumReturnUrlLength || !IsLocalUrl(target))
            return SafeLandingPath;

        if (!Uri.TryCreate(target, UriKind.Relative, out _))
            return SafeLandingPath;

        if (!TrySplitTarget(target, out var path, out var query) ||
            !IsLocalUrl(path) ||
            IsAuthorizationOrActivationPath(path))
            return SafeLandingPath;

        return ContainsUnsafeNestedReturnUrl(query)
            ? SafeLandingPath
            : target;
    }

    private static bool IsLocalUrl(string target)
    {
        return target.StartsWith("/", StringComparison.Ordinal) &&
               !target.StartsWith("//", StringComparison.Ordinal) &&
               !target.StartsWith("/\\", StringComparison.Ordinal);
    }

    private static bool TrySplitTarget(string target, out string path, out string query)
    {
        var fragmentIndex = target.IndexOf('#');
        var withoutFragment = fragmentIndex >= 0 ? target[..fragmentIndex] : target;
        var queryIndex = withoutFragment.IndexOf('?');
        var rawPath = queryIndex >= 0 ? withoutFragment[..queryIndex] : withoutFragment;

        path = Decode(rawPath);
        query = queryIndex >= 0 ? withoutFragment[(queryIndex + 1)..] : string.Empty;
        return !string.IsNullOrWhiteSpace(path);
    }

    private static bool ContainsUnsafeNestedReturnUrl(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        try
        {
            var parameters = QueryHelpers.ParseQuery(query);
            foreach (var parameter in parameters)
            {
                if (!string.Equals(parameter.Key, "returnUrl", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var value in parameter.Value)
                {
                    if (IsUnsafeNestedTarget(value))
                        return true;
                }
            }
        }
        catch (FormatException)
        {
            return true;
        }

        return false;
    }

    private static bool IsUnsafeNestedTarget(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumReturnUrlLength)
            return true;

        var decoded = Decode(value);
        if (decoded.Length > MaximumReturnUrlLength || !IsLocalUrl(decoded) || !TrySplitTarget(decoded, out var path, out var query))
            return true;

        if (IsAuthorizationOrActivationPath(path))
            return true;

        try
        {
            var parameters = QueryHelpers.ParseQuery(query);
            foreach (var parameter in parameters)
            {
                if (!string.Equals(parameter.Key, "returnUrl", StringComparison.OrdinalIgnoreCase))
                    continue;

                // A return target that carries another return target is recursive by
                // construction and must not be amplified through another redirect.
                return true;
            }
        }
        catch (FormatException)
        {
            return true;
        }

        return false;
    }

    private static bool IsAuthorizationOrActivationPath(string path)
    {
        var normalized = path.TrimEnd('/');
        if (string.IsNullOrEmpty(normalized))
            normalized = "/";

        return string.Equals(normalized, "/Account", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("/Account/", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "/Subscription", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "/Subscription/Index", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("/SubscriptionActivation", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "/activate", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("/activate/", StringComparison.OrdinalIgnoreCase);
    }

    private static string Decode(string value)
    {
        try
        {
            var decoded = value;
            for (var i = 0; i < 4; i++)
            {
                var next = Uri.UnescapeDataString(decoded);
                if (string.Equals(next, decoded, StringComparison.Ordinal))
                    break;

                decoded = next;
            }

            return decoded;
        }
        catch (UriFormatException)
        {
            return string.Empty;
        }
    }
}
