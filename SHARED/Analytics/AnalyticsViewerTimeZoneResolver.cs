namespace Shared.Analytics;

public static class AnalyticsViewerTimeZoneResolver
{
    public static TimeZoneInfo Resolve(string? timezoneId, int? timezoneOffsetMinutes)
    {
        if (!string.IsNullOrWhiteSpace(timezoneId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timezoneId.Trim());
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        if (timezoneOffsetMinutes is >= -840 and <= 840)
        {
            try
            {
                return TimeZoneInfo.CreateCustomTimeZone(
                    $"viewer-offset-{timezoneOffsetMinutes.Value}",
                    TimeSpan.FromMinutes(-timezoneOffsetMinutes.Value),
                    "Viewer Local",
                    "Viewer Local");
            }
            catch (ArgumentException)
            {
            }
        }

        return TimeZoneInfo.Utc;
    }
}
