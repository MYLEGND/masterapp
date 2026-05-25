from pathlib import Path
import shutil
from datetime import datetime
import re

stamp = datetime.now().strftime("%Y%m%d-%H%M%S")

def backup(path):
    p = Path(path)
    if not p.exists():
        raise SystemExit(f"Missing file: {p}")
    b = p.with_suffix(p.suffix + f".bak-integrity-{stamp}")
    shutil.copy2(p, b)
    print(f"BACKUP {p} -> {b}")
    return p

# ============================================================
# 1) HEALTH / DISABILITY: replace uncataloged one-off events
#    with canonical events that tracking.js already allows.
# ============================================================

for file_path, prefix in [
    ("Protect-Website/Views/Quote/Health.cshtml", "health"),
    ("Protect-Website/Views/Quote/Disability.cshtml", "disability")
]:
    p = backup(file_path)
    text = p.read_text()

    replacements = {
        f"EventType: '{prefix}_quote_processing_view'": "EventType: 'form_start'",
        f"EventType: '{prefix}_quote_started'": "EventType: 'form_start'",
        f"EventType: '{prefix}_quote_thank_you_view'": "EventType: 'thank_you_view'",
        "EventType: 'quote_thank_you_view'": "EventType: 'lead_form_submit_success'"
    }

    changed = 0
    for old, new in replacements.items():
        if old in text:
            text = text.replace(old, new)
            changed += 1

    if changed == 0:
        raise SystemExit(f"No Health/Disability event replacements applied in {file_path}")

    p.write_text(text)
    print(f"PATCHED canonical events in {file_path}: {changed} replacements")

# ============================================================
# 2) TRACKING.JS: reduce diagnostic noise + internal detection
# ============================================================

p = backup("Protect-Website/wwwroot/js/tracking.js")
text = p.read_text()

# Add internal traffic detector before buildBody.
internal_helper = r'''
  function detectInternalTrafficFlag() {
    try {
      const host = (window.location.hostname || '').toLowerCase();
      const path = (window.location.pathname || '').toLowerCase();
      const search = (window.location.search || '').toLowerCase();

      if (host === 'localhost' || host === '127.0.0.1' || host === '::1') return true;
      if (host.includes('azurewebsites.net') && (host.includes('staging') || host.includes('dev') || host.includes('test'))) return true;
      if (search.includes('internal=1') || search.includes('qa=1') || search.includes('test=1') || search.includes('debug=1')) return true;
      if (path.includes('/admin') || path.includes('/workstation') || path.includes('/websiteanalytics')) return true;

      return false;
    } catch (_) {
      return false;
    }
  }

'''

if "function detectInternalTrafficFlag()" not in text:
    anchor = "  function buildBody(payload)"
    if anchor not in text:
        raise SystemExit("buildBody anchor not found in tracking.js")
    text = text.replace(anchor, internal_helper + anchor, 1)

if "IsInternal: false" not in text and "IsInternal: detectInternalTrafficFlag()" not in text:
    raise SystemExit("Could not find IsInternal field in tracking.js")

text = text.replace("IsInternal: false", "IsInternal: detectInternalTrafficFlag()", 1)

# Reduce Marketing Health noise: do not report passive resource-load failures as analytics failures.
old_window_error = r'''      void reportClientTrackingError({
        attemptedEventName: 'window_error',
        pageKey: PAGE_KEY,
        quoteType: PAGE_QUOTE_TYPE,
        errorMessage: clampErrorText(event?.message || sourceUrl || 'window_error'),
        route: window.location.pathname,
        fetchUrl: sourceUrl,
        trigger: 'window.onerror'
      });'''

new_window_error = r'''      // Resource load failures like avatar/image/CDN misses are not analytics-pipeline failures.
      if (sourceUrl && target && target !== window) {
        debug('skipped resource-load diagnostic', { sourceUrl });
        return;
      }

      void reportClientTrackingError({
        attemptedEventName: 'window_error',
        pageKey: PAGE_KEY,
        quoteType: PAGE_QUOTE_TYPE,
        errorMessage: clampErrorText(event?.message || sourceUrl || 'window_error'),
        route: window.location.pathname,
        fetchUrl: sourceUrl,
        trigger: 'window.onerror'
      });'''

if old_window_error not in text:
    raise SystemExit("window_error diagnostic block not found exactly")
text = text.replace(old_window_error, new_window_error, 1)

# Reduce third-party fetch noise while keeping analytics send failures handled by sendEvent().
old_skip = r'''  function shouldSkipFetchDiagnostics(url) {
    if (!url) return true;
    return url.includes(INGEST_URL) || url.includes('/ThankYou/meta-browser-ack');
  }'''

new_skip = r'''  function shouldSkipFetchDiagnostics(url) {
    if (!url) return true;
    if (url.includes(INGEST_URL) || url.includes('/ThankYou/meta-browser-ack')) return true;

    try {
      const parsed = new URL(url, window.location.origin);
      if (parsed.origin !== window.location.origin) return true;
    } catch (_) {
      return true;
    }

    return false;
  }'''

if old_skip not in text:
    raise SystemExit("shouldSkipFetchDiagnostics block not found exactly")
text = text.replace(old_skip, new_skip, 1)

p.write_text(text)
print("PATCHED tracking.js internal flag + diagnostic noise filtering")

# ============================================================
# 3) WEBSITE ANALYTICS JS: fix session activity timezone parsing
# ============================================================

p = backup("AgentPortal/wwwroot/js/website-analytics.js")
text = p.read_text()

helper = r'''
  function parseAnalyticsDate(value) {
    if (!value) return null;

    let raw = String(value).trim();

    if (/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?$/.test(raw)) {
      raw += 'Z';
    }

    const d = new Date(raw);
    return isNaN(d.getTime()) ? null : d;
  }

'''

if "function parseAnalyticsDate(value)" not in text:
    anchor = "  function formatActivityTimeRange(row) {"
    if anchor not in text:
        raise SystemExit("formatActivityTimeRange anchor not found")
    text = text.replace(anchor, helper + "\n" + anchor, 1)

text = text.replace(
    "const startDate = row?.eventUtc ? new Date(row.eventUtc) : null;",
    "const startDate = parseAnalyticsDate(row?.eventUtc);",
    1
)
text = text.replace(
    "const endDate = row?.endUtc ? new Date(row.endUtc) : null;",
    "const endDate = parseAnalyticsDate(row?.endUtc);",
    1
)

p.write_text(text)
print("PATCHED session activity timezone parsing")

# ============================================================
# 4) ANALYTICS QUERY SERVICE:
#    A) strongest attribution wins
#    B) Device Intelligence groups by resolved session profile
# ============================================================

p = backup("AgentPortal/Services/Analytics/AnalyticsQueryService.cs")
text = p.read_text()

# Insert attribution strength helpers.
attrib_helper = r'''
    private static int AttributionStrength(EventAttributionSnapshot snapshot)
    {
        if (!HasAttributionSignal(snapshot))
            return -1;

        if (IsMetaAttributedPaid(snapshot))
            return 500;

        if (!string.IsNullOrWhiteSpace(snapshot.Fbclid) ||
            !string.IsNullOrWhiteSpace(snapshot.MetaCampaignId) ||
            !string.IsNullOrWhiteSpace(snapshot.MetaAdSetId) ||
            !string.IsNullOrWhiteSpace(snapshot.MetaAdId))
            return 450;

        if (!string.IsNullOrWhiteSpace(snapshot.UtmSource) &&
            !string.IsNullOrWhiteSpace(snapshot.UtmCampaign))
            return 400;

        if (!string.IsNullOrWhiteSpace(snapshot.UtmSource))
            return 300;

        if (!string.IsNullOrWhiteSpace(snapshot.ReferrerHost))
            return 200;

        return 100;
    }

    private static EventAttributionSnapshot? SelectStrongestAttribution(IEnumerable<AnalyticsEvent> events)
    {
        return events
            .Select(e => new { e.EventUtc, Snapshot = SnapshotFromEvent(e) })
            .Where(x => HasAttributionSignal(x.Snapshot))
            .OrderByDescending(x => AttributionStrength(x.Snapshot))
            .ThenBy(x => x.EventUtc)
            .Select(x => x.Snapshot)
            .FirstOrDefault();
    }

'''

if "private static int AttributionStrength" not in text:
    anchor = "    private static Dictionary<string, EventAttributionSnapshot> BuildSessionAttributionMap"
    if anchor not in text:
        raise SystemExit("BuildSessionAttributionMap anchor not found")
    text = text.replace(anchor, attrib_helper + "\n" + anchor, 1)

old_session = r'''    private static Dictionary<string, EventAttributionSnapshot> BuildSessionAttributionMap(List<AnalyticsEvent> events)
    {
        var map = new Dictionary<string, EventAttributionSnapshot>(StringComparer.OrdinalIgnoreCase);
        var groups = events
            .Where(e => !string.IsNullOrWhiteSpace(e.SessionId))
            .GroupBy(e => e.SessionId!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var selected =
                group.Where(e => e.EventType == "page_view")
                    .OrderBy(e => e.EventUtc)
                    .Select(SnapshotFromEvent)
                    .FirstOrDefault(HasAttributionSignal)
                ?? group.OrderBy(e => e.EventUtc)
                    .Select(SnapshotFromEvent)
                    .FirstOrDefault(HasAttributionSignal);

            if (selected != null && HasAttributionSignal(selected))
                map[group.Key] = selected;
        }

        return map;
    }'''

new_session = r'''    private static Dictionary<string, EventAttributionSnapshot> BuildSessionAttributionMap(List<AnalyticsEvent> events)
    {
        var map = new Dictionary<string, EventAttributionSnapshot>(StringComparer.OrdinalIgnoreCase);
        var groups = events
            .Where(e => !string.IsNullOrWhiteSpace(e.SessionId))
            .GroupBy(e => e.SessionId!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var selected = SelectStrongestAttribution(group);
            if (selected != null && HasAttributionSignal(selected))
                map[group.Key] = selected;
        }

        return map;
    }'''

old_visitor = r'''    private static Dictionary<string, EventAttributionSnapshot> BuildVisitorAttributionMap(List<AnalyticsEvent> events)
    {
        var map = new Dictionary<string, EventAttributionSnapshot>(StringComparer.OrdinalIgnoreCase);
        var groups = events
            .Where(e => !string.IsNullOrWhiteSpace(e.VisitorId))
            .GroupBy(e => e.VisitorId!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var selected =
                group.Where(e => e.EventType == "page_view")
                    .OrderBy(e => e.EventUtc)
                    .Select(SnapshotFromEvent)
                    .FirstOrDefault(HasAttributionSignal)
                ?? group.OrderBy(e => e.EventUtc)
                    .Select(SnapshotFromEvent)
                    .FirstOrDefault(HasAttributionSignal);

            if (selected != null && HasAttributionSignal(selected))
                map[group.Key] = selected;
        }

        return map;
    }'''

new_visitor = r'''    private static Dictionary<string, EventAttributionSnapshot> BuildVisitorAttributionMap(List<AnalyticsEvent> events)
    {
        var map = new Dictionary<string, EventAttributionSnapshot>(StringComparer.OrdinalIgnoreCase);
        var groups = events
            .Where(e => !string.IsNullOrWhiteSpace(e.VisitorId))
            .GroupBy(e => e.VisitorId!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var selected = SelectStrongestAttribution(group);
            if (selected != null && HasAttributionSignal(selected))
                map[group.Key] = selected;
        }

        return map;
    }'''

if old_session not in text:
    raise SystemExit("Exact old BuildSessionAttributionMap block not found")
if old_visitor not in text:
    raise SystemExit("Exact old BuildVisitorAttributionMap block not found")

text = text.replace(old_session, new_session, 1)
text = text.replace(old_visitor, new_visitor, 1)

# Replace Device Intelligence BuildRows with session-resolved grouping.
old_buildrows = r'''        List<DeviceIntelligenceRowDto> BuildRows(Func<AnalyticsEvent, string> selector)
        {
            return rows
                .GroupBy(selector)
                .Select(g =>
                {
                    var events = g.ToList();
                    var sessions = events.Select(e => e.SessionId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Count();
                    var ctas = events.Count(e => e.EventType == "cta_click" || e.EventType == "quote_click");
                    var starts = events.Count(e => e.EventType == "form_start");
                    var submits = events.Count(e => e.EventType == "form_submit");
                    var leads = events.Count(e =>
                        e.EventType == "form_submit" &&
                        (e.SubmitOutcome ?? "").ToLower() == "success");

                    return new DeviceIntelligenceRowDto
                    {
                        Label = string.IsNullOrWhiteSpace(g.Key) ? "Unknown" : g.Key,
                        Sessions = sessions,
                        Events = events.Count,
                        CtaClicks = ctas,
                        FormStarts = starts,
                        SubmitAttempts = submits,
                        ConfirmedLeads = leads,
                        StartRate = sessions <= 0 ? 0 : Math.Round((decimal)starts / sessions * 100, 1),
                        LeadRate = sessions <= 0 ? 0 : Math.Round((decimal)leads / sessions * 100, 1)
                    };
                })
                .OrderByDescending(x => x.Sessions)
                .ThenBy(x => x.Label)
                .Take(12)
                .ToList();
        }'''

new_buildrows = r'''        string ResolveSessionLabel(IEnumerable<AnalyticsEvent> sessionEvents, Func<AnalyticsEvent, string> selector)
        {
            var labels = sessionEvents
                .OrderBy(e => e.EventUtc)
                .Select(selector)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            var best = labels.LastOrDefault(x => !x.Equals("Unknown", StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrWhiteSpace(best) ? "Unknown" : best;
        }

        List<DeviceIntelligenceRowDto> BuildRows(Func<AnalyticsEvent, string> selector)
        {
            var sessionProfiles = rows
                .Where(e => !string.IsNullOrWhiteSpace(e.SessionId))
                .GroupBy(e => e.SessionId!, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var events = g.OrderBy(e => e.EventUtc).ToList();
                    var ctas = events.Count(e => e.EventType == "cta_click" || e.EventType == "quote_click");
                    var starts = events.Any(IsQuoteFunnelStartSignalEvent) ? 1 : 0;
                    var submits = events.Count(e => e.EventType == "form_submit");
                    var leads = events.Any(e =>
                        e.EventType == "form_submit" &&
                        (e.SubmitOutcome ?? "").ToLower() == "success") ? 1 : 0;

                    return new
                    {
                        SessionId = g.Key,
                        Label = ResolveSessionLabel(events, selector),
                        Events = events.Count,
                        CtaClicks = ctas,
                        FormStarts = starts,
                        SubmitAttempts = submits,
                        ConfirmedLeads = leads
                    };
                })
                .ToList();

            return sessionProfiles
                .GroupBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var sessions = g.Count();
                    var events = g.Sum(x => x.Events);
                    var starts = g.Sum(x => x.FormStarts);
                    var leads = g.Sum(x => x.ConfirmedLeads);

                    return new DeviceIntelligenceRowDto
                    {
                        Label = string.IsNullOrWhiteSpace(g.Key) ? "Unknown" : g.Key,
                        Sessions = sessions,
                        Events = events,
                        CtaClicks = g.Sum(x => x.CtaClicks),
                        FormStarts = starts,
                        SubmitAttempts = g.Sum(x => x.SubmitAttempts),
                        ConfirmedLeads = leads,
                        StartRate = sessions <= 0 ? 0 : Math.Round((decimal)starts / sessions * 100, 1),
                        LeadRate = sessions <= 0 ? 0 : Math.Round((decimal)leads / sessions * 100, 1)
                    };
                })
                .OrderByDescending(x => x.Sessions)
                .ThenBy(x => x.Label)
                .Take(12)
                .ToList();
        }'''

if old_buildrows not in text:
    raise SystemExit("Exact old Device Intelligence BuildRows block not found")

text = text.replace(old_buildrows, new_buildrows, 1)

text = text.replace(
    "FormStarts = rows.Count(e => e.EventType == \"form_start\"),",
    "FormStarts = rows.Where(IsQuoteFunnelStartSignalEvent).Select(e => !string.IsNullOrWhiteSpace(e.SessionId) ? e.SessionId : $\"event:{e.EventId:D}\").Distinct(StringComparer.OrdinalIgnoreCase).Count(),",
    1
)

p.write_text(text)
print("PATCHED attribution priority + session-resolved Device Intelligence")

print("PATCH COMPLETE")
