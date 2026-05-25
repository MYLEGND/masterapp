from pathlib import Path
import shutil
from datetime import datetime
import re

stamp = datetime.now().strftime("%Y%m%d-%H%M%S")

def backup(path):
    p = Path(path)
    if not p.exists():
        raise SystemExit(f"Missing required file: {p}")
    b = p.with_suffix(p.suffix + f".bak-{stamp}")
    shutil.copy2(p, b)
    print(f"BACKUP {p} -> {b}")
    return p

# ============================================================
# 1) SOURCE ATTRIBUTION NORMALIZATION
# Exact verified seam: AnalyticsQueryService.SourceBucketLabel(...)
# ============================================================

p = backup("AgentPortal/Services/Analytics/AnalyticsQueryService.cs")
text = p.read_text()

helper = r'''
    private static string NormalizeAnalyticsSourceLabel(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return "Unknown";

        var raw = source.Trim();
        var s = raw.ToLowerInvariant();

        return s switch
        {
            "ig" or "instagram" or "instagram.com" => "Instagram",
            "fb" or "facebook" or "facebook.com" or "meta" => "Facebook / Meta",
            "messenger" or "m.me" or "facebook_messenger" => "Messenger",
            "google" => "Google",
            "gads" or "googleads" or "google_ads" or "adwords" => "Google Ads",
            "bing" or "microsoft" or "microsoft_ads" => "Microsoft Ads",
            "chatgpt" or "openai" => "ChatGPT / OpenAI",
            "claude" or "anthropic" => "Claude / Anthropic",
            "perplexity" => "Perplexity",
            "direct" or "(direct)" or "none" or "(none)" => "Direct",
            _ when s.Contains("instagram") => "Instagram",
            _ when s.Contains("facebook") || s.Contains("meta") => "Facebook / Meta",
            _ when s.Contains("google") => "Google",
            _ when s.Contains("bing") || s.Contains("microsoft") => "Microsoft Ads",
            _ when s.Contains("chatgpt") || s.Contains("openai") => "ChatGPT / OpenAI",
            _ when s.Contains("claude") || s.Contains("anthropic") => "Claude / Anthropic",
            _ => raw
        };
    }

'''

anchor = "    private static string SourceBucketLabel(EventAttributionSnapshot attribution, TrafficType trafficType)"
if "NormalizeAnalyticsSourceLabel" not in text:
    if anchor not in text:
        raise SystemExit("SourceBucketLabel anchor not found.")
    text = text.replace(anchor, helper + "\n" + anchor, 1)

old = '''        if (!string.IsNullOrWhiteSpace(attribution.UtmSource))
            return attribution.UtmSource!.Trim();
'''
new = '''        if (!string.IsNullOrWhiteSpace(attribution.UtmSource))
            return NormalizeAnalyticsSourceLabel(attribution.UtmSource);
'''
if old not in text:
    raise SystemExit("Expected raw UtmSource return block not found. Not patching blind.")
text = text.replace(old, new, 1)
p.write_text(text)

# ============================================================
# 2) TRACKING.JS INTERNAL/QA TAGGING + SPA ROUTE CHANGE ATTRIBUTION
# Exact verified seam: IsInternal false + no pushState/replaceState found
# ============================================================

p = backup("Protect-Website/wwwroot/js/tracking.js")
text = p.read_text()

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
    # Put it before the first sendEvent function if available, otherwise after strict wrapper area.
    send_anchor = "function sendEvent"
    if send_anchor in text:
        text = text.replace(send_anchor, internal_helper + "\n  " + send_anchor, 1)
    else:
        raise SystemExit("sendEvent anchor not found in tracking.js. Not patching blind.")

text = re.sub(r'IsInternal\s*:\s*false', 'IsInternal: detectInternalTrafficFlag()', text, count=1)

spa_helper = r'''
  function trackRouteChangeForAttribution(reason) {
    try {
      if (typeof sendEvent !== 'function') return;
      sendEvent({
        EventType: 'page_view',
        MetadataJson: JSON.stringify({
          reason: reason || 'route_change',
          virtualRoute: window.location.pathname + window.location.search
        })
      });
    } catch (_) {
      // Never let analytics route tracking break the user experience.
    }
  }

  if (!window.__legendRouteTrackingPatched) {
    window.__legendRouteTrackingPatched = true;

    ['pushState', 'replaceState'].forEach(function (methodName) {
      const original = history[methodName];
      if (typeof original !== 'function') return;

      history[methodName] = function () {
        const result = original.apply(this, arguments);
        setTimeout(function () {
          trackRouteChangeForAttribution(methodName);
        }, 0);
        return result;
      };
    });

    window.addEventListener('popstate', function () {
      trackRouteChangeForAttribution('popstate');
    });
  }

'''

if "window.__legendRouteTrackingPatched" not in text:
    # Insert before final wireClick block area, which we verified exists.
    route_anchor = "  wireClick('[data-cta=\"hero_start_assessment\"]'"
    if route_anchor not in text:
        route_anchor = "  wireClick("
    if route_anchor not in text:
        raise SystemExit("wireClick anchor not found for SPA route patch. Not patching blind.")
    text = text.replace(route_anchor, spa_helper + "\n" + route_anchor, 1)

p.write_text(text)

# ============================================================
# 3) VISITOR TIMELINE ENDPOINT
# Exact verified seam: WebsiteAnalyticsController has [HttpGet("kpi-detail")]
# ============================================================

p = backup("AgentPortal/Controllers/WebsiteAnalyticsController.cs")
text = p.read_text()

endpoint = r'''
    [HttpGet("visitor-timeline")]
    public async Task<IActionResult> VisitorTimeline(
        string visitorId,
        string? sessionId = null,
        string preset = "today",
        string? trafficType = null)
    {
        visitorId = (visitorId ?? string.Empty).Trim();
        sessionId = (sessionId ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(visitorId) && string.IsNullOrWhiteSpace(sessionId))
            return BadRequest(new { error = "visitorId or sessionId is required." });

        var range = ResolveRange(preset);
        var scopedAgentIds = await ResolveScopedAgentIdsAsync();

        var eventsQuery = _db.AnalyticsEvents
            .AsNoTracking()
            .Where(e => e.EventUtc >= range.FromUtc && e.EventUtc <= range.ToUtc);

        if (scopedAgentIds != null && scopedAgentIds.Length > 0)
            eventsQuery = eventsQuery.Where(e => e.AgentTrackingProfileId != null && scopedAgentIds.Contains(e.AgentTrackingProfileId.Value));

        if (!string.IsNullOrWhiteSpace(visitorId))
            eventsQuery = eventsQuery.Where(e => e.VisitorId == visitorId);
        else if (!string.IsNullOrWhiteSpace(sessionId))
            eventsQuery = eventsQuery.Where(e => e.SessionId == sessionId);

        var events = await eventsQuery
            .OrderBy(e => e.EventUtc)
            .Take(500)
            .Select(e => new
            {
                e.EventUtc,
                e.EventType,
                e.Path,
                e.PageKey,
                e.FormKey,
                e.ElementId,
                e.ElementText,
                e.SessionId,
                e.VisitorId,
                e.DeviceType,
                e.Browser,
                e.OperatingSystem,
                e.UtmSource,
                e.UtmMedium,
                e.UtmCampaign,
                e.ReferrerHost,
                e.ScrollPercent,
                e.DwellMilliseconds,
                e.EngagedMilliseconds,
                e.SubmitOutcome,
                e.IsInternal,
                e.MetadataJson
            })
            .ToListAsync();

        var totalEvents = events.Count;
        var sessions = events.Select(e => e.SessionId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var firstSeen = events.FirstOrDefault()?.EventUtc;
        var lastSeen = events.LastOrDefault()?.EventUtc;
        var durationSeconds = firstSeen.HasValue && lastSeen.HasValue
            ? Math.Max(0, (int)(lastSeen.Value - firstSeen.Value).TotalSeconds)
            : 0;

        var ctaClicks = events.Count(e => string.Equals(e.EventType, "cta_click", StringComparison.OrdinalIgnoreCase) || string.Equals(e.EventType, "quote_click", StringComparison.OrdinalIgnoreCase));
        var formStarts = events.Count(e => string.Equals(e.EventType, "form_start", StringComparison.OrdinalIgnoreCase) || string.Equals(e.EventType, "lead_form_start", StringComparison.OrdinalIgnoreCase));
        var abandons = events.Count(e => string.Equals(e.EventType, "form_abandon", StringComparison.OrdinalIgnoreCase));
        var exits = events.Count(e => string.Equals(e.EventType, "page_exit", StringComparison.OrdinalIgnoreCase));
        var scrollSignals = events.Count(e => (e.EventType ?? "").StartsWith("scroll_depth_", StringComparison.OrdinalIgnoreCase));
        var maxScroll = events.Where(e => e.ScrollPercent != null).Select(e => e.ScrollPercent!.Value).DefaultIfEmpty(0).Max();

        var signals = new List<string>();
        var trustScore = 100;

        if (totalEvents >= 120) { trustScore -= 20; signals.Add("High event volume"); }
        if (durationSeconds > 0 && totalEvents / Math.Max(durationSeconds / 60.0, 1) > 40) { trustScore -= 20; signals.Add("High event velocity"); }
        if (ctaClicks >= 12) { trustScore -= 15; signals.Add("Repeated CTA clicks"); }
        if (formStarts >= 4) { trustScore -= 15; signals.Add("Repeated form starts"); }
        if (maxScroll < 10 && totalEvents >= 10) { trustScore -= 10; signals.Add("Low/no scroll with activity"); }
        if (events.Any(e => e.IsInternal)) { trustScore -= 25; signals.Add("Internal/test traffic present"); }

        trustScore = Math.Max(0, Math.Min(100, trustScore));
        var trustTier = trustScore >= 85 ? "Trusted" :
                        trustScore >= 65 ? "Review" :
                        trustScore >= 40 ? "Suspicious" :
                        "Likely Bot";

        return Ok(new
        {
            visitorId = visitorId,
            sessionId = sessionId,
            firstSeen,
            lastSeen,
            durationSeconds,
            sessions,
            totalEvents,
            trustScore,
            trustTier,
            signals,
            summary = new
            {
                ctaClicks,
                formStarts,
                abandons,
                exits,
                scrollSignals,
                maxScroll
            },
            events = events.Select(e => new
            {
                when = e.EventUtc,
                type = e.EventType,
                page = e.PageKey ?? e.Path,
                path = e.Path,
                form = e.FormKey,
                element = e.ElementText ?? e.ElementId,
                sessionId = e.SessionId,
                visitorId = e.VisitorId,
                device = e.DeviceType,
                browser = e.Browser,
                os = e.OperatingSystem,
                source = e.UtmSource,
                medium = e.UtmMedium,
                campaign = e.UtmCampaign,
                referrer = e.ReferrerHost,
                scroll = e.ScrollPercent,
                dwellMs = e.DwellMilliseconds,
                engagedMs = e.EngagedMilliseconds,
                outcome = e.SubmitOutcome,
                isInternal = e.IsInternal,
                metadata = e.MetadataJson
            })
        });
    }

'''

if "HttpGet(\"visitor-timeline\")" not in text:
    anchor = "    [HttpGet(\"kpi-detail\")]"
    if anchor not in text:
        raise SystemExit("kpi-detail endpoint anchor not found. Not patching blind.")
    text = text.replace(anchor, endpoint + "\n" + anchor, 1)

p.write_text(text)

# ============================================================
# 4) VISITOR CONCENTRATION MODAL ROW CLICK → TIMELINE
# Exact verified seam: openVisitorConcentrationModal(rows)
# ============================================================

p = backup("AgentPortal/wwwroot/js/website-analytics-kpi-modal.js")
text = p.read_text()

js_helper = r'''
    async function openVisitorTimelineModal(row) {
        if (!row) return;

        let modal = document.getElementById('visitorTimelineModal');
        if (!modal) {
            modal = document.createElement('div');
            modal.id = 'visitorTimelineModal';
            modal.className = 'vc-modal-backdrop';
            modal.innerHTML = `
                <div class="vc-modal-panel" role="dialog" aria-modal="true" aria-labelledby="visitorTimelineModalTitle">
                    <div class="vc-modal-header">
                        <div>
                            <div class="vc-modal-kicker">Visitor Intelligence</div>
                            <h3 id="visitorTimelineModalTitle">Visitor Timeline</h3>
                            <p>Full ordered event stream, trust score, behavior signals, and attribution context.</p>
                        </div>
                        <button type="button" class="vc-modal-close" aria-label="Close visitor timeline modal">&times;</button>
                    </div>
                    <div class="vc-modal-body" id="visitorTimelineModalBody"></div>
                </div>`;
            document.body.appendChild(modal);

            modal.querySelector('.vc-modal-close')?.addEventListener('click', () => modal.classList.remove('is-open'));
            modal.addEventListener('click', e => {
                if (e.target === modal) modal.classList.remove('is-open');
            });
        }

        const body = modal.querySelector('#visitorTimelineModalBody');
        body.innerHTML = `<div class="vc-modal-section-title">Loading timeline…</div>`;
        modal.classList.add('is-open');

        try {
            const state = window.__waState || {};
            const params = new URLSearchParams();
            params.set('visitorId', row.visitorId || row.VisitorId || '');
            if (row.sessionId || row.SessionId) params.set('sessionId', row.sessionId || row.SessionId);
            params.set('preset', state.preset || 'today');

            const res = await fetch(`/WebsiteAnalytics/visitor-timeline?${params.toString()}`, {
                headers: { 'Accept': 'application/json' }
            });

            if (!res.ok) throw new Error(`Timeline request failed: ${res.status}`);
            const data = await res.json();

            const events = Array.isArray(data.events) ? data.events : [];
            const signals = Array.isArray(data.signals) ? data.signals : [];

            body.innerHTML = `
                <div class="vc-modal-stats">
                    <div><strong>${escapeHtml(String(data.trustScore ?? '—'))}</strong><span>Trust Score</span></div>
                    <div><strong>${escapeHtml(data.trustTier || '—')}</strong><span>Trust Tier</span></div>
                    <div><strong>${escapeHtml(String(data.totalEvents ?? 0))}</strong><span>Events</span></div>
                    <div><strong>${escapeHtml(String(data.sessions ?? 0))}</strong><span>Sessions</span></div>
                </div>

                <div class="vc-modal-section-title">Triggered Signals</div>
                <div class="vc-modal-note">${signals.length ? signals.map(escapeHtml).join(' · ') : 'No major risk signals triggered.'}</div>

                <div class="vc-modal-section-title">Timeline</div>
                <div class="vc-modal-table-wrap">
                    <table class="vc-modal-table">
                        <thead>
                            <tr>
                                <th>When</th>
                                <th>Event</th>
                                <th>Page</th>
                                <th>Element/Form</th>
                                <th>Outcome</th>
                                <th>Scroll</th>
                            </tr>
                        </thead>
                        <tbody>
                            ${events.map(e => `
                                <tr>
                                    <td>${escapeHtml(formatWhen(e.when))}</td>
                                    <td>${escapeHtml(e.type || '—')}</td>
                                    <td>${escapeHtml(e.page || e.path || '—')}</td>
                                    <td>${escapeHtml(e.element || e.form || '—')}</td>
                                    <td>${escapeHtml(e.outcome || '—')}</td>
                                    <td>${escapeHtml(e.scroll == null ? '—' : `${e.scroll}%`)}</td>
                                </tr>
                            `).join('')}
                        </tbody>
                    </table>
                </div>`;
        } catch (err) {
            body.innerHTML = `<div class="vc-modal-note">Timeline could not be loaded. ${escapeHtml(err.message || String(err))}</div>`;
        }
    }

'''

if "async function openVisitorTimelineModal" not in text:
    anchor = "    function openVisitorConcentrationModal(rows) {"
    if anchor not in text:
        raise SystemExit("openVisitorConcentrationModal anchor not found. Not patching blind.")
    text = text.replace(anchor, js_helper + "\n" + anchor, 1)

# Add clickable row behavior by replacing table row map if recognizable.
# This intentionally uses a broad-but-guarded enhancement: after modal body render,
# bind rows with data-visitor-id if present; otherwise add data attributes to rows.
if "data-visitor-id" not in text:
    text = text.replace(
        "<tr>",
        "<tr data-visitor-id=\"${escapeHtml(row.visitorId || row.VisitorId || '')}\">",
        1
    )

bind_code = r'''
        body.querySelectorAll('[data-visitor-id]').forEach(tr => {
            tr.style.cursor = 'pointer';
            tr.title = 'Click to open visitor timeline';
            tr.addEventListener('click', () => {
                const visitorId = tr.getAttribute('data-visitor-id');
                const found = (rows || []).find(r => (r.visitorId || r.VisitorId || '') === visitorId);
                openVisitorTimelineModal(found || { visitorId });
            });
        });
'''

if "Click to open visitor timeline" not in text:
    modal_open_anchor = "        modal.classList.add('is-open');"
    if modal_open_anchor not in text:
        raise SystemExit("visitor concentration modal open anchor not found.")
    text = text.replace(modal_open_anchor, bind_code + "\n" + modal_open_anchor, 1)

p.write_text(text)

print("PATCH COMPLETE")
print("Now run: dotnet build AgentPortal/AgentPortal.csproj && dotnet build Protect-Website/ProtectWebsite.csproj")
