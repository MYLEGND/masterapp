namespace Shared.Analytics;

/// <summary>Canonical sanitizer for OpenAI Pixel browser-reference matching.</summary>
public static class OpenAiBrowserReference
{
    public const int MaxLength = 1024;

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        if (normalized.Length > MaxLength || normalized.Any(char.IsControl))
            return null;
        return normalized;
    }
}
