using Microsoft.AspNetCore.Http;

namespace AgentPortal.Security;

internal static class OidcTransientCookieCleanup
{
    private static readonly string[] Prefixes =
    {
        ".AspNetCore.Correlation.",
        ".AspNetCore.OpenIdConnect.Nonce"
    };

    public static void Clear(HttpContext httpContext, string? callbackPath = "/signin-oidc")
    {
        if (httpContext == null) return;

        var keys = httpContext.Request.Cookies.Keys
            .Where(IsTransientOidcCookie)
            .ToArray();

        if (keys.Length == 0) return;

        var normalizedCallbackPath = NormalizeCallbackPath(callbackPath);
        var paths = new HashSet<string>(StringComparer.Ordinal)
        {
            "/",
            normalizedCallbackPath
        };

        var pathBase = httpContext.Request.PathBase.Value?.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(pathBase))
        {
            paths.Add(pathBase);
            paths.Add($"{pathBase}{normalizedCallbackPath}");
        }

        foreach (var key in keys)
        {
            httpContext.Response.Cookies.Delete(key);

            foreach (var path in paths)
            {
                httpContext.Response.Cookies.Delete(key, new CookieOptions
                {
                    Path = path,
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.None
                });
            }
        }
    }

    private static bool IsTransientOidcCookie(string key) =>
        Prefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeCallbackPath(string? callbackPath)
    {
        if (string.IsNullOrWhiteSpace(callbackPath))
            return "/signin-oidc";

        return callbackPath.StartsWith("/", StringComparison.Ordinal)
            ? callbackPath
            : $"/{callbackPath}";
    }
}
