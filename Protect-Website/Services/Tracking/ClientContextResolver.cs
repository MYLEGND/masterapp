namespace ProtectWebsite.Services.Tracking;

public sealed record ClientContextResolution
{
    public string? DeviceType { get; init; }
    public string? Browser { get; init; }
    public string? OperatingSystem { get; init; }
    public string? UserAgent { get; init; }
    public int? ViewportWidth { get; init; }
    public int? ViewportHeight { get; init; }
    public int? ScreenWidth { get; init; }
    public int? ScreenHeight { get; init; }
    public bool? WebDriver { get; init; }
    public bool? IsHeadless { get; init; }
    public int? MouseMoveCount { get; init; }
    public int? HumanInteractionCount { get; init; }
    public int? VisibilityChangeCount { get; init; }
    public string? Language { get; init; }
    public string? TimeZone { get; init; }
}

public static class ClientContextResolver
{
    public static ClientContextResolution Resolve(
        string? deviceType,
        string? browser,
        string? operatingSystem,
        string? userAgent,
        int? viewportWidth = null,
        int? viewportHeight = null,
        int? screenWidth = null,
        int? screenHeight = null,
        bool? webDriver = null,
        bool? isHeadless = null,
        int? mouseMoveCount = null,
        int? humanInteractionCount = null,
        int? visibilityChangeCount = null,
        string? language = null,
        string? timeZone = null)
    {
        var ua = FirstMeaningful(userAgent);
        var parsed = ParseUserAgent(ua);

        return new ClientContextResolution
        {
            DeviceType = FirstMeaningful(deviceType, parsed.DeviceType),
            Browser = FirstMeaningful(browser, parsed.Browser),
            OperatingSystem = FirstMeaningful(operatingSystem, parsed.OperatingSystem),
            UserAgent = ua,
            ViewportWidth = viewportWidth,
            ViewportHeight = viewportHeight,
            ScreenWidth = screenWidth,
            ScreenHeight = screenHeight,
            WebDriver = webDriver,
            IsHeadless = isHeadless ?? ContainsAny(ua ?? string.Empty, "Headless", "PhantomJS", "Selenium", "Puppeteer", "Playwright"),
            MouseMoveCount = mouseMoveCount,
            HumanInteractionCount = humanInteractionCount,
            VisibilityChangeCount = visibilityChangeCount,
            Language = FirstMeaningful(language),
            TimeZone = FirstMeaningful(timeZone)
        };
    }

    public static string? FirstMeaningful(params string?[] values)
    {
        foreach (var value in values)
        {
            var trimmed = value?.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed) &&
                !string.Equals(trimmed, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }
        }

        return FirstNonBlank(values);
    }

    public static string? FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    public static string? NormalizeAcceptLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var first = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(first)) return null;

        var semi = first.IndexOf(';');
        return semi >= 0 ? first[..semi].Trim() : first.Trim();
    }

    public static (string DeviceType, string Browser, string OperatingSystem) ParseUserAgent(string? userAgent)
    {
        var ua = userAgent ?? string.Empty;

        var deviceType = "desktop";
        if (ContainsAny(ua, "Mobi", "Android", "iPhone", "iPod")) deviceType = "mobile";
        else if (ContainsAny(ua, "iPad", "Tablet")) deviceType = "tablet";

        var browser = "unknown";
        if (ContainsAny(ua, "Edg/", "EdgiOS", "EdgA")) browser = "edge";
        else if (ContainsAny(ua, "CriOS", "Chrome/", "Chromium/")) browser = "chrome";
        else if (ContainsAny(ua, "FxiOS", "Firefox/")) browser = "firefox";
        else if (ContainsAny(ua, "FBAN", "FBAV", "Instagram")) browser = "in_app";
        else if (ua.Contains("Safari/", StringComparison.OrdinalIgnoreCase)) browser = "safari";

        var os = "unknown";
        if (ua.Contains("Windows", StringComparison.OrdinalIgnoreCase)) os = "windows";
        else if (ContainsAny(ua, "Android")) os = "android";
        else if (ContainsAny(ua, "iPhone", "iPad", "iPod")) os = "ios";
        else if (ContainsAny(ua, "Mac OS X", "Macintosh")) os = "macos";
        else if (ua.Contains("Linux", StringComparison.OrdinalIgnoreCase)) os = "linux";

        return (deviceType, browser, os);
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (value.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
