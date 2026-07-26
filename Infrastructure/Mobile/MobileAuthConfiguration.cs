using Microsoft.Extensions.Configuration;

namespace Infrastructure.Mobile;

/// <summary>
/// Non-secret configuration for the public native client API. Missing values
/// intentionally leave bearer authentication unable to validate a token.
/// </summary>
public sealed record MobileAuthConfiguration(
    string? TenantId,
    string? Authority,
    string? Audience,
    string? RequiredScope)
{
    public const string SectionName = "MobileAuth";

    public static MobileAuthConfiguration FromConfiguration(IConfiguration configuration) => new(
        Normalize(configuration[$"{SectionName}:TenantId"] ?? configuration[$"{SectionName}__TenantId"]),
        Normalize(configuration[$"{SectionName}:Authority"] ?? configuration[$"{SectionName}__Authority"]),
        Normalize(configuration[$"{SectionName}:Audience"] ?? configuration[$"{SectionName}__Audience"]),
        Normalize(configuration[$"{SectionName}:RequiredScope"] ?? configuration[$"{SectionName}__RequiredScope"]));

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(TenantId) &&
        IsHttpsUrl(Authority) &&
        !string.IsNullOrWhiteSpace(TokenAudience) &&
        IsRequiredScopeForApplication(RequiredScope, TokenAudience);

    /// <summary>
    /// The exact <c>aud</c> claim expected from a Microsoft Entra v2 access
    /// token. Entra v2 emits the resource application's client ID, not its
    /// Application ID URI. Deployment configuration can retain the existing
    /// <c>api://&lt;application-id&gt;</c> value, but it is normalized here before
    /// the bearer handler evaluates a token.
    /// </summary>
    public string? TokenAudience => TryGetApplicationId(Audience);

    /// <summary>
    /// Entra access tokens carry the final scope segment in <c>scp</c>, while
    /// configuration retains the complete, reviewable delegated-scope URI.
    /// </summary>
    public string? RequiredScopeName => RequiredScope?
        .Trim()
        .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .LastOrDefault();

    private static bool IsHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(uri.Host);

    private static string? TryGetApplicationId(string? value)
    {
        var normalized = Normalize(value);
        if (normalized is null)
            return null;

        if (Guid.TryParse(normalized, out var applicationId))
            return applicationId.ToString("D");

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "api", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.AbsolutePath.Trim('/')) ||
            !Guid.TryParse(uri.Host, out applicationId))
        {
            return null;
        }

        return applicationId.ToString("D");
    }

    private static bool IsRequiredScopeForApplication(string? scope, string? applicationId)
    {
        if (string.IsNullOrWhiteSpace(scope) ||
            string.IsNullOrWhiteSpace(applicationId) ||
            !Uri.TryCreate(scope, UriKind.Absolute, out var scopeUri) ||
            !string.Equals(scopeUri.Scheme, "api", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(scopeUri.AbsolutePath.Trim('/')) ||
            !Guid.TryParse(scopeUri.Host, out var scopeApplicationId))
        {
            return false;
        }

        return string.Equals(scopeApplicationId.ToString("D"), applicationId, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
