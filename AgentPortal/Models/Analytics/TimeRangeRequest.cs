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

    public static TimeRangeRequest FromPreset(string? preset, DateTime? fromUtc = null, DateTime? toUtc = null, int timezoneOffsetMinutes = 0)
    {
        var now = DateTime.UtcNow;
        // Clamp offset to a sane range to prevent abuse; 0 = UTC fallback.
        var safeOffset = (timezoneOffsetMinutes >= -840 && timezoneOffsetMinutes <= 840) ? timezoneOffsetMinutes : 0;
        // Derive the viewer's local "now" by applying the browser UTC offset.
        // getTimezoneOffset() is positive for zones west of UTC (e.g. UTC-7 = +420).
        var localNow = now.AddMinutes(-safeOffset);
        // Local "today" midnight expressed as a UTC instant.
        var localTodayUtc = new DateTime(localNow.Year, localNow.Month, localNow.Day, 0, 0, 0, DateTimeKind.Utc)
                                .AddMinutes(safeOffset);

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
                start = new DateTime(localNow.Year, localNow.Month, 1, 0, 0, 0, DateTimeKind.Utc)
                            .AddMinutes(safeOffset);
                grouping = TimeGrouping.Day;
                label = "This Month";
                break;
            case "year":
                start = new DateTime(localNow.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                            .AddMinutes(safeOffset);
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
            Preset = preset
        };
    }
}
