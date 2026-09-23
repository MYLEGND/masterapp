using System;

namespace Shared.Analytics;

/// <summary>The sender, receiver and HMAC validator use the same key precedence.</summary>
public static class AnalyticsIngestConfiguration
{
    public static string? ResolveSecret(Func<string, string?> read, Func<string, string?>? readEnvironment = null)
    {
        // The release synchronizes this canonical App Service setting from the
        // receiver. A later Key Vault provider must not revive a stale sender alias.
        var canonical = (readEnvironment ?? Environment.GetEnvironmentVariable)("Analytics__SharedSecret");
        if (!string.IsNullOrWhiteSpace(canonical)) return canonical;
        foreach (var key in new[] { "Analytics:SharedSecret", "LeadIngest:SharedSecret", "Tracking:SharedSecret", "TRACKING_SHARED_SECRET" })
        {
            var value = read(key);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }
}
