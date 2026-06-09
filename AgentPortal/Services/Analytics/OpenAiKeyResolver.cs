using Microsoft.Extensions.Configuration;

namespace AgentPortal.Services.Analytics;

/// <summary>
/// Single source of truth for resolving the OpenAI API key.
/// Resolution order (first non-whitespace value wins):
///   1. OpenAI:ApiKey  — IConfiguration (Azure App Settings, appsettings overrides, user secrets)
///   2. OPENAI_API_KEY — environment variable (standard OpenAI convention)
///   3. OpenAI__ApiKey — environment variable (ASP.NET Core double-underscore config key form)
///
/// The key is NEVER logged, returned to clients, or stored beyond the call site.
/// </summary>
internal static class OpenAiKeyResolver
{
    /// <summary>
    /// Returns the resolved API key, or <see langword="null"/> if no key is configured.
    /// Callers should treat a null/empty return as "feature not configured" and surface
    /// a safe error message — never expose this value in responses or logs.
    /// </summary>
    public static string? Resolve(IConfiguration config)
    {
        var fromConfig = config["OpenAI:ApiKey"];
        if (!string.IsNullOrWhiteSpace(fromConfig)) return fromConfig;

        var fromEnv = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;

        var fromEnvAlt = Environment.GetEnvironmentVariable("OpenAI__ApiKey");
        if (!string.IsNullOrWhiteSpace(fromEnvAlt)) return fromEnvAlt;

        return null;
    }

    /// <summary>Returns true if a non-empty key can be resolved.</summary>
    public static bool IsConfigured(IConfiguration config) =>
        !string.IsNullOrWhiteSpace(Resolve(config));
}
