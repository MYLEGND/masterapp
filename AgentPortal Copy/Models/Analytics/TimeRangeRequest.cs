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

    public static TimeRangeRequest FromPreset(string? preset, DateTime? fromUtc = null, DateTime? toUtc = null)
    {
        var now = DateTime.UtcNow;
        preset = (preset ?? "30d").ToLowerInvariant();

        DateTime start;
        DateTime end = toUtc ?? now;
        TimeGrouping grouping;
        string label;

        switch (preset)
        {
            case "today":
                start = now.Date;
                grouping = TimeGrouping.Day;
                label = "Today";
                break;
            case "7d":
                start = now.Date.AddDays(-6);
                grouping = TimeGrouping.Day;
                label = "Last 7 Days";
                break;
            case "30d":
                start = now.Date.AddDays(-29);
                grouping = TimeGrouping.Day;
                label = "Last 30 Days";
                break;
            case "month":
                start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                grouping = TimeGrouping.Day;
                label = "This Month";
                break;
            case "year":
                start = new DateTime(now.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
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
                start = now.Date.AddDays(-29);
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
