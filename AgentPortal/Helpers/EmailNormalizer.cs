using System;

namespace AgentPortal.Helpers;

public static class EmailNormalizer
{
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Trim().ToLowerInvariant();
    }
}
