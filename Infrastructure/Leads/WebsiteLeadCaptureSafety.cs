using System.Linq;
using Domain.Entities;

namespace Infrastructure.Leads;

public static class WebsiteLeadCaptureSafety
{
    public static bool ShouldMarkAsInternalTest(string? host)
        => IsLocalHost(host);

    public static bool ShouldSkipWorkstationCapture(WebsiteLead? lead)
    {
        if (lead == null)
            return false;

        return lead.IsInternal || IsLocalHost(lead.Host);
    }

    public static bool IsLocalHost(string? host)
    {
        var normalized = NormalizeHost(host);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return normalized.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("::1", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeHost(string? host)
    {
        var normalized = (host ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        if (normalized.StartsWith("[", StringComparison.Ordinal) && normalized.Contains(']'))
        {
            var closingBracket = normalized.IndexOf(']');
            if (closingBracket > 1)
                return normalized[1..closingBracket];
        }

        var colonIndex = normalized.IndexOf(':');
        if (colonIndex > 0 && normalized.Count(ch => ch == ':') == 1)
            return normalized[..colonIndex];

        return normalized;
    }
}
