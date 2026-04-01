using System;
using System.Linq;
using AgentPortal.Helpers;
using Microsoft.AspNetCore.Http;

namespace AgentPortal.Services;

public interface IAgentTimeZoneResolver
{
    TimeZoneInfo Resolve(HttpContext context);
}

public sealed class AgentTimeZoneResolver : IAgentTimeZoneResolver
{
    private const string DefaultIana = "America/Phoenix";

    public TimeZoneInfo Resolve(HttpContext context)
    {
        if (context == null) return CrmAttemptTracking.ResolveTimeZone(DefaultIana, null, CrmAttemptTracking.DialTimeZone);

        // 1) Saved agent timezone (claims is our persisted spot today)
        var claimTz = context.User?.Claims?
            .FirstOrDefault(c =>
                string.Equals(c.Type, "zoneinfo", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Type, "timezone", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Type, "timeZone", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Type, "tz", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        // 2) Optional header (only as a hint/confirmation)
        var headers = context.Request?.Headers;
        var headerTz = headers?["X-Agent-TimeZone"].FirstOrDefault();
        var offset = headers?["X-Agent-TzOffset"].FirstOrDefault();

        return CrmAttemptTracking.ResolveTimeZone(
            claimTz ?? headerTz,
            offset,
            CrmAttemptTracking.ResolveTimeZone(DefaultIana, null, CrmAttemptTracking.DialTimeZone));
    }
}
