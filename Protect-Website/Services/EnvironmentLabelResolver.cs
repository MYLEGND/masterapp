namespace ProtectWebsite.Services;

public static class EnvironmentLabelResolver
{
    public static string Resolve(string? incoming = null)
    {
        var raw = string.IsNullOrWhiteSpace(incoming)
            ? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"
            : incoming!;

        var normalized = raw.Trim();
        if (normalized.StartsWith("prod", StringComparison.OrdinalIgnoreCase)) return "production";
        if (normalized.StartsWith("dev", StringComparison.OrdinalIgnoreCase)) return "development";
        return normalized;
    }
}
