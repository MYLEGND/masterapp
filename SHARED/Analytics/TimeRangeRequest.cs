using System;

namespace AgentPortal.Models.Analytics;

public enum TimeGrouping
{
    Day,
    Week,
    Month,
    Year
}

public sealed class TimeRangeRequest
{
    public DateTime FromUtc { get; init; }
    public DateTime ToUtc { get; init; }
    public TimeGrouping Grouping { get; init; }
    public string Label { get; init; } = "Last 30 Days";
    public string Preset { get; init; } = "30d";
    public TimeZoneInfo ViewerTimeZone { get; init; } = TimeZoneInfo.Utc;
    public TrafficQualityMode QualityMode { get; init; } = TrafficQualityMode.RealHumanTraffic;

    public static TimeRangeRequest FromPreset(string? preset, DateTime? fromUtc = null, DateTime? toUtc = null, TimeZoneInfo? viewerTz = null, TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic)
    {
        var tz = viewerTz ?? TimeZoneInfo.Utc;
        var now = DateTime.UtcNow;

        // Convert UTC now to viewer-local time (DST-aware via TimeZoneInfo).
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(now, tz);

        // Helper: convert a viewer-local midnight DateTime to UTC safely.
        // Wraps ConvertTimeToUtc which can throw for times in a DST gap (extremely
        // unlikely at midnight, but we guard anyway).
        static DateTime LocalMidnightToUtc(DateTime localMidnight, TimeZoneInfo tzInfo)
        {
            try
            {
                return TimeZoneInfo.ConvertTimeToUtc(
                    DateTime.SpecifyKind(localMidnight, DateTimeKind.Unspecified), tzInfo);
            }
            catch
            {
                // DST gap edge-case: shift one hour forward and retry.
                var shifted = localMidnight.AddHours(1);
                return TimeZoneInfo.ConvertTimeToUtc(
                    DateTime.SpecifyKind(shifted, DateTimeKind.Unspecified), tzInfo);
            }
        }

        var localTodayMidnight = new DateTime(localNow.Year, localNow.Month, localNow.Day, 0, 0, 0);
        var localTodayUtc = LocalMidnightToUtc(localTodayMidnight, tz);

        preset = (preset ?? "30d").ToLowerInvariant();

        DateTime start;
        DateTime end = toUtc ?? now;
        TimeGrouping grouping;
        string label;

        switch (preset)
        {
            case "today":
                start = localTodayUtc;
                grouping = TimeGrouping.Day;
                label = "Today";
                break;
            case "7d":
                start = localTodayUtc.AddDays(-6);
                grouping = TimeGrouping.Day;
                label = "Last 7 Days";
                break;
            case "30d":
                start = localTodayUtc.AddDays(-29);
                grouping = TimeGrouping.Day;
                label = "Last 30 Days";
                break;
            case "month":
                start = LocalMidnightToUtc(new DateTime(localNow.Year, localNow.Month, 1, 0, 0, 0), tz);
                grouping = TimeGrouping.Day;
                label = "This Month";
                break;
            case "year":
                start = LocalMidnightToUtc(new DateTime(localNow.Year, 1, 1, 0, 0, 0), tz);
                grouping = TimeGrouping.Month;
                label = "This Year";
                break;
            case "custom":
                if (fromUtc == null || toUtc == null)
                    throw new ArgumentException("Custom range requires fromUtc and toUtc");
                start = fromUtc.Value;
                end = toUtc.Value;
                var span = (end - start).TotalDays;
                grouping = span <= 30 ? TimeGrouping.Day :
                           span <= 90 ? TimeGrouping.Week :
                           span <= 730 ? TimeGrouping.Month : TimeGrouping.Year;
                label = "Custom";
                break;
            default:
                start = localTodayUtc.AddDays(-29);
                grouping = TimeGrouping.Day;
                label = "Last 30 Days";
                break;
        }

        return new TimeRangeRequest
        {
            FromUtc = start,
            ToUtc = end,
            Grouping = grouping,
            Label = label,
            Preset = preset,
            ViewerTimeZone = tz,
            QualityMode = qualityMode
        };
    }
}
