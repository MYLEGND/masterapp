using Microsoft.AspNetCore.Http;

namespace AgentPortal.Services;

/// <summary>
/// Owns the AgentPortal SignalR route boundary used by the global request
/// limiter. StartsWithSegments keeps hub sub-routes exempt without allowing a
/// similarly prefixed ordinary HTTP route to bypass limiting.
/// </summary>
internal static class RealtimeHubRateLimitAuthority
{
    private static readonly PathString[] HubPaths =
    [
        new("/livesync"),
        new("/leadbridgehub"),
        new("/messaginghub")
    ];

    internal static bool IsHubPath(PathString path) =>
        HubPaths.Any(path.StartsWithSegments);
}
