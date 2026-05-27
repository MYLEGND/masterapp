using System.Globalization;
using AgentPortal.Models.Analytics;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Services.Analytics;

public sealed class VisitorConcentrationService : IVisitorConcentrationService
{
    private readonly Infrastructure.Data.MasterAppDbContext _db;
    private readonly IVisitorTrustScoringService _trustScoring;
    private readonly IAnalyticsQueryService _analytics;

    public VisitorConcentrationService(
        Infrastructure.Data.MasterAppDbContext db,
        IVisitorTrustScoringService trustScoring,
        IAnalyticsQueryService analytics)
    {
        _db = db;
        _trustScoring = trustScoring;
        _analytics = analytics;
    }

    public async Task<List<VisitorConcentrationDto>> GetVisitorConcentrationAsync(
        TimeRangeRequest range,
        CancellationToken ct = default)
    {
        var payload = await GetVisitorConcentrationPayloadAsync(
            range,
            new ScopeContext(),
            TrafficType.All,
            ct);

        return payload.Rows
            
            .Select(x => new VisitorConcentrationDto
            {
                VisitorId = x.VisitorId,
                VisitorShortId = x.VisitorShortId,
                Sessions = x.Sessions,
                Events = x.Events,
                Device = x.Device,
                Source = x.Source,
                FirstSeenLocal = x.FirstSeenLocal,
                LastSeenLocal = x.LastSeenLocal,
                LikelyInternal = x.LikelyInternal
            })
            .ToList();
    }

    public async Task<VisitorConcentrationPayload> GetVisitorConcentrationPayloadAsync(
        TimeRangeRequest range,
        ScopeContext scope,
        TrafficType trafficType,
        CancellationToken ct = default)
    {
        var eventsQuery = _analytics.ScopedEvents(range, scope);

        var events = await eventsQuery
            .OrderBy(e => e.EventUtc)
            .ToListAsync(ct);

        events = trafficType switch
        {
            TrafficType.PaidAds => events
                .Where(e => Classify(e) == TrafficType.PaidAds)
                .ToList(),

            TrafficType.NonPaid => events
                .Where(e => Classify(e) != TrafficType.PaidAds)
                .ToList(),

            _ => events
        };

        var visitorIds = events
            .Select(e => e.VisitorId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sessionIds = events
            .Select(e => e.SessionId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var metaSignals = await _db.MetaSignalEvents
            .AsNoTracking()
            .Where(x => x.CreatedUtc >= range.FromUtc && x.CreatedUtc < range.ToUtc)
            .Where(x =>
                (!string.IsNullOrWhiteSpace(x.VisitorId) && visitorIds.Contains(x.VisitorId!)) ||
                (!string.IsNullOrWhiteSpace(x.SessionId) && sessionIds.Contains(x.SessionId!)))
            .ToListAsync(ct);

        var visitorGroups = events
            .Where(e => !string.IsNullOrWhiteSpace(e.VisitorId))
            .GroupBy(e => e.VisitorId!)
            .Select(g =>
            {
                var groupEvents = g.OrderBy(x => x.EventUtc).ToList();

                var groupSessionIds = groupEvents
                    .Select(x => x.SessionId)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var groupSignals = metaSignals
                    .Where(x =>
                        string.Equals(x.VisitorId, g.Key, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(x.SessionId) && groupSessionIds.Contains(x.SessionId!)))
                    .ToList();

                var trust = _trustScoring.Calculate(groupEvents, groupSignals);

                var first = groupEvents.Min(x => x.EventUtc);
                var last = groupEvents.Max(x => x.EventUtc);
                var sessions = groupSessionIds.Count;
                var totalEvents = groupEvents.Count;
                var internalEvents = groupEvents.Count(x => x.IsInternal);

                var topPage = groupEvents
                    .Where(x => !string.IsNullOrWhiteSpace(x.PageKey))
                    .GroupBy(x => x.PageKey!)
                    .OrderByDescending(x => x.Count())
                    .ThenBy(x => x.Key)
                    .Select(x => x.Key)
                    .FirstOrDefault() ?? "Unknown";

                var source = ResolveTop(groupEvents.Select(x => x.UtmSource), "Direct");
                var medium = ResolveTop(groupEvents.Select(x => x.UtmMedium), "");
                var campaign = ResolveTop(groupEvents.Select(x => x.UtmCampaign), "");
                var device = ResolveTop(groupEvents.Select(x => x.DeviceType), "Unknown");
                var browser = ResolveTop(groupEvents.Select(x => x.Browser), "Unknown");
                var os = ResolveTop(groupEvents.Select(x => x.OperatingSystem), "Unknown");
                var tz = ResolveTop(groupEvents.Select(x => x.TimeZone), "Unknown");
                var lang = ResolveTop(groupEvents.Select(x => x.Language), "Unknown");

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
                    LikelyInternal: likelyInternal,
                    TrustScore: trust.TrustScore,
                    TrustTier: trust.TrustTier,
                    HumanConfidence: trust.HumanConfidence,
                    TrustSignals: trust.Signals.Take(3).ToList()
                );
            })
            .OrderByDescending(x => x.Sessions)
            .ThenByDescending(x => x.Events)
            .ToList();

        var totalEventCount = visitorGroups.Sum(x => x.Events);
        var topVisitorEvents = visitorGroups.FirstOrDefault()?.Events ?? 0;

        return new VisitorConcentrationPayload(
            TotalVisitors: visitorGroups.Count,
            TotalEvents: totalEventCount,
            VisitorsOneSession: visitorGroups.Count(x => x.Sessions == 1),
            VisitorsTwoPlusSessions: visitorGroups.Count(x => x.Sessions >= 2),
            VisitorsFivePlusSessions: visitorGroups.Count(x => x.Sessions >= 5),
            LikelyInternalVisitors: visitorGroups.Count(x => x.LikelyInternal),
            InternalEventShare: totalEventCount == 0
                ? 0
                : Math.Round(visitorGroups.Where(x => x.LikelyInternal).Sum(x => x.Events) * 100m / totalEventCount, 1),
            TopVisitorEvents: topVisitorEvents,
            TopVisitorShare: totalEventCount == 0
                ? 0
                : Math.Round(topVisitorEvents * 100m / totalEventCount, 1),
            Rows: visitorGroups
        );
    }

    private static TrafficType Classify(AnalyticsEvent e) =>
        TrafficAttribution.Classify(
            e.UtmSource,
            e.UtmMedium,
            e.UtmCampaign,
            e.Fbclid,
            referrerHost: e.ReferrerHost,
            metaCampaignId: e.MetaCampaignId,
            metaAdSetId: e.MetaAdSetId,
            metaAdId: e.MetaAdId,
            isInternal: e.IsInternal,
            environment: e.Environment,
            host: e.Host);

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
}
