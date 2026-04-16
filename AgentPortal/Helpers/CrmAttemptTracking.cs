using System;
using System.Collections.Generic;
using System.Globalization;
using AgentPortal.Models;
using Domain.Entities;

namespace AgentPortal.Helpers;

public readonly record struct CrmAttemptCounts(int Today, int Week, int Month, int Year, int Lifetime);

public readonly record struct AttemptAnchors(
    DateTime LocalToday,
    DateTime LocalWeekStart,
    DateTime LocalMonthStart,
    DateTime LocalYearStart,
    DateTime LocalNow,
    DateTime DayStartUtc,
    DateTime WeekStartUtc,
    DateTime MonthStartUtc,
    DateTime YearStartUtc);

public static class CrmAttemptTracking
{
    private static readonly Dictionary<string, string> IanaToWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        ["America/New_York"] = "Eastern Standard Time",
        ["America/Chicago"] = "Central Standard Time",
        ["America/Denver"] = "Mountain Standard Time",
        ["America/Phoenix"] = "US Mountain Standard Time",
        ["America/Los_Angeles"] = "Pacific Standard Time",
        ["America/Anchorage"] = "Alaskan Standard Time",
        ["Pacific/Honolulu"] = "Hawaiian Standard Time"
    };

    private static readonly Lazy<TimeZoneInfo> DialTimeZoneLazy = new(() =>
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("LEGEND_DIAL_TIMEZONE"),
            "America/Phoenix",           // IANA (Linux/macOS)
            "US Mountain Standard Time", // Windows equivalent
            TimeZoneInfo.Local.Id,
            "UTC"
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try { return TimeZoneInfo.FindSystemTimeZoneById(candidate); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        return TimeZoneInfo.Utc;
    });

    public static TimeZoneInfo DialTimeZone => DialTimeZoneLazy.Value;

    public static TimeZoneInfo ResolveTimeZone(string? timeZoneId, string? offsetMinutes, TimeZoneInfo? fallback = null)
    {
        fallback ??= DialTimeZone;

        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            if (TryFindTimeZone(timeZoneId, out var tz)) return tz;
            if (IanaToWindows.TryGetValue(timeZoneId, out var windowsId) && TryFindTimeZone(windowsId, out tz))
                return tz;
        }

        if (int.TryParse(offsetMinutes, out var minutes))
        {
            // JS getTimezoneOffset returns minutes behind UTC (e.g., -420 for UTC+7 ahead), so invert.
            var offset = TimeSpan.FromMinutes(-minutes);
            return TimeZoneInfo.CreateCustomTimeZone(
                $"UTC{(offset >= TimeSpan.Zero ? "+" : "")}{offset:hh\\:mm}",
                offset,
                $"UTC{(offset >= TimeSpan.Zero ? "+" : "")}{offset:hh\\:mm}",
                $"UTC{(offset >= TimeSpan.Zero ? "+" : "")}{offset:hh\\:mm}");
        }

        return fallback;
    }

    private static bool TryFindTimeZone(string id, out TimeZoneInfo timeZone)
    {
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }

        timeZone = DialTimeZone;
        return false;
    }

    // Shared anchor builder — single source of truth for window calculations.
    private static AttemptAnchors BuildAnchors(DateTime utcNow, TimeZoneInfo tz)
    {
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), tz);
        var localToday = localNow.Date;
        var localWeekStart = StartOfLocalWeekMonday(localToday);
        var localMonthStart = new DateTime(localToday.Year, localToday.Month, 1);
        var localYearStart = new DateTime(localToday.Year, 1, 1);

        return new AttemptAnchors(
            localToday,
            localWeekStart,
            localMonthStart,
            localYearStart,
            localNow,
            ToUtcStart(localToday, tz),
            ToUtcStart(localWeekStart, tz),
            ToUtcStart(localMonthStart, tz),
            ToUtcStart(localYearStart, tz));
    }

    private static DateTime ToUtcStart(DateTime localDate, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTimeToUtc(new DateTime(localDate.Year, localDate.Month, localDate.Day, 0, 0, 0, DateTimeKind.Unspecified), tz);

    // Monday-Sunday week window
    private static DateTime StartOfLocalWeekMonday(DateTime localDay)
    {
        var offset = ((int)localDay.DayOfWeek + 6) % 7; // Mon=0 ... Sun=6
        return localDay.AddDays(-offset);
    }

    private static bool IsSameWeek(DateTime? storedUtc, AttemptAnchors anchors, TimeZoneInfo tz)
    {
        if (!storedUtc.HasValue) return false;
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(storedUtc.Value, DateTimeKind.Utc), tz).Date;
        var localWeekStart = StartOfLocalWeekMonday(local);
        return localWeekStart == anchors.LocalWeekStart;
    }

    private static bool IsSameMonth(DateTime? storedUtc, AttemptAnchors anchors, TimeZoneInfo tz)
    {
        if (!storedUtc.HasValue) return false;
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(storedUtc.Value, DateTimeKind.Utc), tz).Date;
        if (local.Year == anchors.LocalMonthStart.Year && local.Month == anchors.LocalMonthStart.Month) return true;

        var utc = DateTime.SpecifyKind(storedUtc.Value, DateTimeKind.Utc);
        return utc.Year == anchors.LocalMonthStart.Year && utc.Month == anchors.LocalMonthStart.Month;
    }

    private static bool IsSameYear(DateTime? storedUtc, AttemptAnchors anchors, TimeZoneInfo tz)
    {
        if (!storedUtc.HasValue) return false;
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(storedUtc.Value, DateTimeKind.Utc), tz).Date;
        if (local.Year == anchors.LocalYearStart.Year) return true;

        var utc = DateTime.SpecifyKind(storedUtc.Value, DateTimeKind.Utc);
        return utc.Year == anchors.LocalYearStart.Year;
    }

    public static void RollLeadAttemptWindows(WorkstationLeadProfile lead, DateTime utcNow, TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? DialTimeZone;
        var anchors = BuildAnchors(utcNow, tz);

        DateTime LocalDate(DateTime? utc) => utc.HasValue
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc), tz).Date
            : DateTime.MinValue;

        var storedDay = LocalDate(lead.CallsTodayDateUtc);
        var storedWeek = LocalDate(lead.CallsWeekStartUtc);
        var storedMonth = LocalDate(lead.CallsMonthStartUtc);
        var storedYear = LocalDate(lead.CallsYearStartUtc);

        // Day
        if (storedDay == DateTime.MinValue)
        {
            lead.CallsTodayDateUtc = anchors.DayStartUtc;
            // preserve existing CallsToday if any; do not zero
        }
        else if (storedDay != anchors.LocalToday)
        {
            lead.CallsTodayDateUtc = anchors.DayStartUtc;
            lead.CallsToday = 0;
        }
        else if (lead.CallsTodayDateUtc != anchors.DayStartUtc)
        {
            lead.CallsTodayDateUtc = anchors.DayStartUtc;
        }

        // Week
        if (storedWeek == DateTime.MinValue)
        {
            lead.CallsWeekStartUtc = anchors.WeekStartUtc;
        }
        else if (!IsSameWeek(lead.CallsWeekStartUtc, anchors, tz))
        {
            lead.CallsWeekStartUtc = anchors.WeekStartUtc;
            lead.CallsWeek = 0;
        }
        else if (lead.CallsWeekStartUtc != anchors.WeekStartUtc)
        {
            lead.CallsWeekStartUtc = anchors.WeekStartUtc;
        }

        // Month
        if (storedMonth == DateTime.MinValue)
        {
            lead.CallsMonthStartUtc = anchors.MonthStartUtc;
        }
        else if (!IsSameMonth(lead.CallsMonthStartUtc, anchors, tz))
        {
            lead.CallsMonthStartUtc = anchors.MonthStartUtc;
            lead.CallsMonth = 0;
        }
        else if (lead.CallsMonthStartUtc != anchors.MonthStartUtc)
        {
            lead.CallsMonthStartUtc = anchors.MonthStartUtc;
        }

        // Year
        if (storedYear == DateTime.MinValue)
        {
            lead.CallsYearStartUtc = anchors.YearStartUtc;
        }
        else if (!IsSameYear(lead.CallsYearStartUtc, anchors, tz))
        {
            lead.CallsYearStartUtc = anchors.YearStartUtc;
            lead.CallsYear = 0;
        }
        else if (lead.CallsYearStartUtc != anchors.YearStartUtc)
        {
            lead.CallsYearStartUtc = anchors.YearStartUtc;
        }
    }

    public static CrmAttemptCounts GetLeadAttemptCounts(WorkstationLeadProfile lead, DateTime utcNow, TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? DialTimeZone;

        // Ensure the window is rolled forward for accurate read-only display without forcing persistence.
        RollLeadAttemptWindows(lead, utcNow, tz);

        var anchors = BuildAnchors(utcNow, tz);

        DateTime LocalDate(DateTime? utc) => utc.HasValue
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc), tz).Date
            : DateTime.MinValue;

        var storedDay = LocalDate(lead.CallsTodayDateUtc);
        var storedWeek = LocalDate(lead.CallsWeekStartUtc);
        var today = storedDay == DateTime.MinValue || storedDay == anchors.LocalToday ? lead.CallsToday : 0;
        var week = storedWeek == DateTime.MinValue || IsSameWeek(lead.CallsWeekStartUtc, anchors, tz) ? lead.CallsWeek : 0;
        var month = lead.CallsMonthStartUtc == null || IsSameMonth(lead.CallsMonthStartUtc, anchors, tz) ? lead.CallsMonth : 0;
        var year = lead.CallsYearStartUtc == null || IsSameYear(lead.CallsYearStartUtc, anchors, tz) ? lead.CallsYear : 0;

        return new CrmAttemptCounts(today, week, month, year, lead.CallCount);
    }

    public static CrmAttemptCounts CountClientActivityAttempts(
        IEnumerable<ClientCrmActivity> activities,
        Func<ClientCrmActivity, bool> isAttempt,
        DateTime utcNow,
        TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? DialTimeZone;

        // Activity dates are stored as local date strings ("yyyy-MM-dd") — compare as local dates
        // against local period anchors. Do NOT parse as UTC; midnight UTC != start-of-local-day.
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), tz);
        var localToday      = localNow.Date;
        var localWeekStart  = StartOfLocalWeekMonday(localToday);
        var localMonthStart = new DateTime(localToday.Year, localToday.Month, 1);
        var localYearStart  = new DateTime(localToday.Year, 1, 1);

        var today = 0;
        var week = 0;
        var month = 0;
        var year = 0;
        var lifetime = 0;

        foreach (var activity in activities ?? Array.Empty<ClientCrmActivity>())
        {
            if (activity == null) continue;
            if (!isAttempt(activity)) continue;

            // Parse as local date — no timezone assumption
            if (!DateTime.TryParseExact(
                    activity.Date,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsed)) continue;

            var actDate = parsed.Date;
            lifetime++;

            if (actDate == localToday)      today++;
            if (actDate >= localWeekStart)  week++;
            if (actDate >= localMonthStart) month++;
            if (actDate >= localYearStart)  year++;
        }

        return new CrmAttemptCounts(today, week, month, year, lifetime);
    }

    // Legacy helpers (kept for other consumers)
    public static DateTime StartOfUtcDay(DateTime utcNow, TimeZoneInfo? timeZone = null) =>
        timeZone == null
            ? DateTime.SpecifyKind(utcNow.Date, DateTimeKind.Utc)
            : ToUtcStart(TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZone).Date, timeZone);

    public static DateTime StartOfUtcWeek(DateTime utcNow, TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? TimeZoneInfo.Utc;
        var localDay = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz).Date;
        var localWeekStart = StartOfLocalWeekMonday(localDay);
        return ToUtcStart(localWeekStart, tz);
    }

    public static DateTime StartOfUtcMonth(DateTime utcNow, TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? TimeZoneInfo.Utc;
        var localDay = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz).Date;
        var localMonthStart = new DateTime(localDay.Year, localDay.Month, 1);
        return ToUtcStart(localMonthStart, tz);
    }

    public static DateTime StartOfUtcYear(DateTime utcNow, TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? TimeZoneInfo.Utc;
        var localDay = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz).Date;
        var localYearStart = new DateTime(localDay.Year, 1, 1);
        return ToUtcStart(localYearStart, tz);
    }

    public static bool TryParseIsoDate(string? value, out DateTime dateUtc)
    {
        if (DateTime.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            dateUtc = DateTime.SpecifyKind(parsed.Date, DateTimeKind.Utc);
            return true;
        }

        dateUtc = default;
        return false;
    }
}
