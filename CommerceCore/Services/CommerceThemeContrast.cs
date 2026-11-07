using System.Globalization;

namespace ParfaitApp.Services;

/// <summary>Readable foregrounds for the canonical editor's opaque hex theme colors.</summary>
public static class CommerceThemeContrast
{
    public static string Foreground(string? background, string? preferred, string fallbackBackground)
    {
        // Existing non-hex CSS expressions remain supported. Preserve the
        // author's foreground; contrast is only guaranteed for opaque hex colors.
        if (!string.IsNullOrWhiteSpace(background) && Luminance(background) is null && !string.IsNullOrWhiteSpace(preferred)) return preferred;
        var luminance = Luminance(background) ?? Luminance(fallbackBackground)
            ?? throw new ArgumentException("A canonical opaque hex fallback is required.", nameof(fallbackBackground));
        if (Luminance(preferred) is double candidate && Contrast(luminance, candidate) >= 4.5)
            return preferred!;
        return Contrast(luminance, 1) >= Contrast(luminance, 0) ? "#ffffff" : "#000000";
    }

    private static double Contrast(double a, double b) => (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);

    private static double? Luminance(string? hex)
    {
        if (hex is null || hex.Length != 7 || hex[0] != '#' ||
            !int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return null;
        static double Channel(int value) { var x = value / 255d; return x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4); }
        return .2126 * Channel((rgb >> 16) & 255) + .7152 * Channel((rgb >> 8) & 255) + .0722 * Channel(rgb & 255);
    }
}
