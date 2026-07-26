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
        IsAudienceUri(Audience) &&
        IsRequiredScopeForAudience(RequiredScope, Audience);

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

    private static bool IsAudienceUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        !string.IsNullOrWhiteSpace(uri.Host) &&
        (string.Equals(uri.Scheme, "api", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    private static bool IsRequiredScopeForAudience(string? scope, string? audience) =>
        !string.IsNullOrWhiteSpace(scope) &&
        !string.IsNullOrWhiteSpace(audience) &&
        scope.StartsWith($"{audience.TrimEnd('/')}/", StringComparison.Ordinal);

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
