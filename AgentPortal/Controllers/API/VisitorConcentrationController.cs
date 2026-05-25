using System.Globalization;
using AgentPortal.Models.Analytics;
using AgentPortal.Services.Analytics;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Controllers.API;

[Authorize]
[ApiController]
[Route("WebsiteAnalytics")]
public sealed class VisitorConcentrationController : ControllerBase
{
    private readonly MasterAppDbContext _db;

    public VisitorConcentrationController(MasterAppDbContext db)
    {
        _db = db;
    }

    [HttpGet("VisitorConcentration")]
    public async Task<IActionResult> Get(
        [FromQuery] string preset = "today",
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        [FromQuery] string? timezoneId = null,
        [FromQuery] int? timezoneOffsetMinutes = null,
        [FromQuery] Guid? agentProfileId = null,
        [FromQuery] TrafficType trafficType = TrafficType.All,
        CancellationToken ct = default)
    {
        var range = ResolveRange(preset, fromUtc, toUtc, timezoneId, timezoneOffsetMinutes);

        var eventsQuery = _db.AnalyticsEvents
            .AsNoTracking()
            .Where(e => e.EventUtc >= range.FromUtc && e.EventUtc < range.ToUtc);

        eventsQuery = eventsQuery.Where(e =>
            e.Environment != null &&
            (e.Environment.ToLower() == "production" || e.Environment.ToLower() == "prod"));

        if (agentProfileId.HasValue)
        {
            eventsQuery = eventsQuery.Where(e => e.AgentTrackingProfileId == agentProfileId.Value);
        }

        if (trafficType == TrafficType.PaidAds)
        {
            eventsQuery = eventsQuery.Where(e =>
                (e.UtmMedium != null && e.UtmMedium.ToLower().Contains("paid")) ||
                (e.UtmSource != null && (
                    e.UtmSource.ToLower().Contains("facebook") ||
                    e.UtmSource.ToLower().Contains("instagram") ||
                    e.UtmSource.ToLower() == "ig" ||
                    e.UtmSource.ToLower() == "meta")) ||
                !string.IsNullOrWhiteSpace(e.Fbclid) ||
                !string.IsNullOrWhiteSpace(e.MetaCampaignId));
        }
        else if (trafficType == TrafficType.NonPaid)
        {
            eventsQuery = eventsQuery.Where(e =>
                string.IsNullOrWhiteSpace(e.Fbclid) &&
                string.IsNullOrWhiteSpace(e.MetaCampaignId) &&
                !(e.UtmMedium != null && e.UtmMedium.ToLower().Contains("paid")));
        }

        var events = await eventsQuery
            .Select(e => new
            {
                e.VisitorId,
                e.SessionId,
                e.EventUtc,
                e.PageKey,
                e.DeviceType,
                e.Browser,
                e.OperatingSystem,
                e.TimeZone,
                e.Language,
                e.UtmSource,
                e.UtmMedium,
                e.UtmCampaign,
                e.IsInternal,
                e.EventType
            })
            .ToListAsync(ct);

        var visitorGroups = events
            .Where(e => !string.IsNullOrWhiteSpace(e.VisitorId))
            .GroupBy(e => e.VisitorId!)
            .Select(g =>
            {
                var first = g.Min(x => x.EventUtc);
                var last = g.Max(x => x.EventUtc);
                var sessions = g.Select(x => x.SessionId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Count();
                var totalEvents = g.Count();
                var internalEvents = g.Count(x => x.IsInternal);
                var topPage = g.Where(x => !string.IsNullOrWhiteSpace(x.PageKey))
                    .GroupBy(x => x.PageKey!)
                    .OrderByDescending(x => x.Count())
                    .ThenBy(x => x.Key)
                    .Select(x => x.Key)
                    .FirstOrDefault() ?? "Unknown";

                var source = ResolveTop(g.Select(x => x.UtmSource), "Direct");
                var medium = ResolveTop(g.Select(x => x.UtmMedium), "");
                var campaign = ResolveTop(g.Select(x => x.UtmCampaign), "");

                var device = ResolveTop(g.Select(x => x.DeviceType), "Unknown");
                var browser = ResolveTop(g.Select(x => x.Browser), "Unknown");
                var os = ResolveTop(g.Select(x => x.OperatingSystem), "Unknown");
                var tz = ResolveTop(g.Select(x => x.TimeZone), "Unknown");
                var lang = ResolveTop(g.Select(x => x.Language), "Unknown");

                var likelyInternal =
                    internalEvents > 0 ||
                    sessions >= 5 ||
                    totalEvents >= 100 ||
                    (source.Equals("Direct", StringComparison.OrdinalIgnoreCase) && totalEvents >= 75);

                return new VisitorConcentrationRow(
                    VisitorId: g.Key,
                    VisitorShortId: ShortId(g.Key),
                    Sessions: sessions,
                    Events: totalEvents,
                    FirstSeenLocal: ToLocalDisplay(first, range.ViewerTimeZone),
                    LastSeenLocal: ToLocalDisplay(last, range.ViewerTimeZone),
                    TopPage: topPage,
                    Source: source,
                    Medium: medium,
                    Campaign: campaign,
                    Device: device,
                    Browser: browser,
                    OperatingSystem: os,
                    TimeZone: tz,
                    Language: lang,
                    InternalEvents: internalEvents,
                    LikelyInternal: likelyInternal
                );
            })
            .OrderByDescending(x => x.Events)
            .ThenByDescending(x => x.Sessions)
            .Take(50)
            .ToList();

        var uniqueVisitors = visitorGroups.Count;
        var totalEventCount = visitorGroups.Sum(x => x.Events);
        var topVisitorEvents = visitorGroups.FirstOrDefault()?.Events ?? 0;

        var payload = new VisitorConcentrationPayload(
            TotalVisitors: uniqueVisitors,
            TotalEvents: totalEventCount,
            VisitorsOneSession: visitorGroups.Count(x => x.Sessions == 1),
            VisitorsTwoPlusSessions: visitorGroups.Count(x => x.Sessions >= 2),
            VisitorsFivePlusSessions: visitorGroups.Count(x => x.Sessions >= 5),
            LikelyInternalVisitors: visitorGroups.Count(x => x.LikelyInternal),
            InternalEventShare: totalEventCount == 0 ? 0 : Math.Round(visitorGroups.Where(x => x.LikelyInternal).Sum(x => x.Events) * 100m / totalEventCount, 1),
            TopVisitorEvents: topVisitorEvents,
            TopVisitorShare: totalEventCount == 0 ? 0 : Math.Round(topVisitorEvents * 100m / totalEventCount, 1),
            Rows: visitorGroups
        );

        return Ok(payload);
    }

    private static string ResolveTop(IEnumerable<string?> values, string fallback)
    {
        return values
            .Select(v => string.IsNullOrWhiteSpace(v) ? null : v.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .GroupBy(v => v!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .Select(g => g.Key)
            .FirstOrDefault() ?? fallback;
    }

    private static string ShortId(string value)
    {
        var clean = value.Trim();
        return clean.Length <= 10 ? clean : clean[..8] + "…";
    }

    private static string ToLocalDisplay(DateTime utc, TimeZoneInfo tz)
    {
        var safeUtc = utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(safeUtc, tz).ToString("M/d h:mm tt", CultureInfo.InvariantCulture);
    }

    private static VisitorRange ResolveRange(string preset, DateTime? fromUtc, DateTime? toUtc, string? timezoneId, int? timezoneOffsetMinutes)
    {
        var tz = ResolveTimeZone(timezoneId, timezoneOffsetMinutes);
        var nowUtc = DateTime.UtcNow;

        if (fromUtc.HasValue && toUtc.HasValue)
        {
            return new VisitorRange(DateTime.SpecifyKind(fromUtc.Value, DateTimeKind.Utc), DateTime.SpecifyKind(toUtc.Value, DateTimeKind.Utc), tz);
        }

        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var todayLocal = nowLocal.Date;
        var key = string.IsNullOrWhiteSpace(preset) ? "today" : preset.Trim().ToLowerInvariant();

        DateTime startLocal;
        DateTime endLocal;

        switch (key)
        {
            case "7d":
            case "last7":
            case "last_7_days":
                startLocal = todayLocal.AddDays(-6);
                endLocal = todayLocal.AddDays(1);
                break;
            case "30d":
            case "last30":
            case "last_30_days":
                startLocal = todayLocal.AddDays(-29);
                endLocal = todayLocal.AddDays(1);
                break;
            case "yesterday":
                startLocal = todayLocal.AddDays(-1);
                endLocal = todayLocal;
                break;
            default:
                startLocal = todayLocal;
                endLocal = todayLocal.AddDays(1);
                break;
        }

        return new VisitorRange(
            TimeZoneInfo.ConvertTimeToUtc(startLocal, tz),
            TimeZoneInfo.ConvertTimeToUtc(endLocal, tz),
            tz);
    }

    private static TimeZoneInfo ResolveTimeZone(string? timezoneId, int? timezoneOffsetMinutes)
    {
        if (!string.IsNullOrWhiteSpace(timezoneId))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(timezoneId.Trim()); }
            catch { }
        }

        if (timezoneOffsetMinutes.HasValue)
        {
            try
            {
                return TimeZoneInfo.CreateCustomTimeZone(
                    "Viewer offset",
                    TimeSpan.FromMinutes(-timezoneOffsetMinutes.Value),
                    "Viewer offset",
                    "Viewer offset");
            }
            catch { }
        }

        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Phoenix"); }
        catch { return TimeZoneInfo.Utc; }
    }

    private sealed record VisitorRange(DateTime FromUtc, DateTime ToUtc, TimeZoneInfo ViewerTimeZone);

    private sealed record VisitorConcentrationPayload(
        int TotalVisitors,
        int TotalEvents,
        int VisitorsOneSession,
        int VisitorsTwoPlusSessions,
        int VisitorsFivePlusSessions,
        int LikelyInternalVisitors,
        decimal InternalEventShare,
        int TopVisitorEvents,
        decimal TopVisitorShare,
        IReadOnlyList<VisitorConcentrationRow> Rows);

    private sealed record VisitorConcentrationRow(
        string VisitorId,
        string VisitorShortId,
        int Sessions,
        int Events,
        string FirstSeenLocal,
        string LastSeenLocal,
        string TopPage,
        string Source,
        string Medium,
        string Campaign,
        string Device,
        string Browser,
        string OperatingSystem,
        string TimeZone,
        string Language,
        int InternalEvents,
        bool LikelyInternal);
}
