(() => {
  const INGEST_URL = '/api/tracking/ingest';
  const PAGE_KEY = document.body.dataset.pageKey || '';
  const AGENT_ID = window.AGENT_TRACKING_PROFILE_ID || null;
  const AGENT_SLUG = window.AGENT_TRACKING_SLUG || null;

  const STORAGE_VISITOR = 'legend_visitor_id';
  const STORAGE_SESSION = 'legend_session_id';
  const STORAGE_SESSION_TS = 'legend_session_ts';
  const SESSION_TIMEOUT_MIN = 30;
  const DEBOUNCE_MS = 2000;

  const allowedEvents = new Set([
    'page_view','cta_click','quote_click','risk_assessment_click',
    'form_start','form_submit','outbound_click',
    'lead_modal_open','lead_form_start','lead_form_submit_success','lead_form_submit_failed',
    // Behavior Intelligence
    'page_exit',
    'page_engaged_10s','page_engaged_30s','page_engaged_60s',
    'scroll_depth_25','scroll_depth_50','scroll_depth_75','scroll_depth_90','scroll_depth_100'
  ]);

  function uuid() {
    return crypto.randomUUID ? crypto.randomUUID() : ([1e7]+-1e3+-4e3+-8e3+-1e11).replace(/[018]/g,c=>(c ^ crypto.getRandomValues(new Uint8Array(1))[0] & 15 >> c / 4).toString(16));
  }

  function getVisitorId() {
    let v = localStorage.getItem(STORAGE_VISITOR);
    if (!v) {
      v = uuid();
      localStorage.setItem(STORAGE_VISITOR, v);
    }
    return v;
  }

  function getSessionId() {
    const now = Date.now();
    const lastTs = parseInt(localStorage.getItem(STORAGE_SESSION_TS) || '0', 10);
    let sid = localStorage.getItem(STORAGE_SESSION);
    if (!sid || isNaN(lastTs) || (now - lastTs) > SESSION_TIMEOUT_MIN * 60 * 1000) {
      sid = uuid();
    }
    localStorage.setItem(STORAGE_SESSION, sid);
    localStorage.setItem(STORAGE_SESSION_TS, String(now));
    return sid;
  }

  const debounceMap = new Map();
  function shouldFire(key) {
    const now = Date.now();
    const last = debounceMap.get(key) || 0;
    if (now - last < DEBOUNCE_MS) return false;
    debounceMap.set(key, now);
    return true;
  }

  async function sendEvent(payload) {
    try {
      const body = {
        ClientEventId: uuid(),
        EventType: payload.EventType,
        PageKey: payload.PageKey || PAGE_KEY,
        SectionKey: payload.SectionKey || null,
        ElementKey: payload.ElementKey || null,
        ButtonLabel: payload.ButtonLabel || null,
        FormKey: payload.FormKey || null,
        QuoteType: payload.QuoteType || null,
        Url: window.location.href,
        Path: window.location.pathname,
        Referrer: document.referrer || null,
        SessionId: getSessionId(),
        VisitorId: getVisitorId(),
        UtmSource: payload.UtmSource || null,
        UtmMedium: payload.UtmMedium || null,
        UtmCampaign: payload.UtmCampaign || null,
        SubmitOutcome: payload.SubmitOutcome || null,
        MetadataJson: payload.MetadataJson || null,
        AgentTrackingProfileId: AGENT_ID,
        AgentSlug: AGENT_SLUG,
        Environment: payload.Environment || null,
        Host: payload.Host || null,
        EventUtc: new Date().toISOString(),
        IsInternal: false,
        // Behavior fields
        DwellMilliseconds: payload.DwellMilliseconds != null ? payload.DwellMilliseconds : null,
        EngagedMilliseconds: payload.EngagedMilliseconds != null ? payload.EngagedMilliseconds : null,
        ScrollPercent: payload.ScrollPercent != null ? payload.ScrollPercent : null,
        IsBounceCandidate: payload.IsBounceCandidate != null ? payload.IsBounceCandidate : null,
        IsExitPage: payload.IsExitPage || null
      };

      if (!allowedEvents.has(body.EventType)) return;
      if (body.EventType === 'form_start' || body.EventType === 'form_submit') {
        body.FormKey = body.FormKey || payload.FormKey;
      }

      await fetch(INGEST_URL, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json'
        },
        body: JSON.stringify(body)
      });
    } catch {
      /* swallow */
    }
  }

  // ── Behavior Intelligence instrumentation ─────────────────────────────────
  //
  // All lifecycle tracking uses visibilitychange (not unload/beforeunload) because:
  // - visibilitychange fires reliably on mobile Safari, iOS Chrome, and Meta/Facebook
  //   in-app browsers where unload/beforeunload are suppressed or unreliable.
  // - page_exit uses navigator.sendBeacon so the request survives navigation/close.

  const _pageStart = Date.now();
  let _maxScroll = 0;
  let _activeMs = 0;          // accumulated engaged ms while page was visible
  let _activeStart = document.visibilityState === 'visible' ? Date.now() : null;
  let _exitFired = false;

  // Snapshot current scroll depth (0–100)
  function getScrollPct() {
    const d = document.documentElement;
    const scrollable = d.scrollHeight - d.clientHeight;
    if (scrollable <= 0) return 100;
    return Math.round((d.scrollTop / scrollable) * 100);
  }

  // ── Scroll milestone events ───────────────────────────────────────────────
  // Each milestone fires once per page load. Passive listener for mobile perf.
  const _scrollFired = new Set();
  window.addEventListener('scroll', function () {
    const pct = getScrollPct();
    if (pct > _maxScroll) _maxScroll = pct;
    [25, 50, 75, 90, 100].forEach(function (milestone) {
      if (!_scrollFired.has(milestone) && pct >= milestone) {
        _scrollFired.add(milestone);
        sendEvent({ EventType: 'scroll_depth_' + milestone, ScrollPercent: milestone });
      }
    });
  }, { passive: true });

  // ── Engagement checkpoint timers ──────────────────────────────────────────
  // Fire page_engaged_Xs only if the page is still visible at that time.
  // These drive the "Engaged Sessions" metric in Behavior Intelligence.
  [10000, 30000, 60000].forEach(function (ms) {
    setTimeout(function () {
      if (document.visibilityState === 'visible') {
        sendEvent({
          EventType: 'page_engaged_' + (ms / 1000) + 's',
          EngagedMilliseconds: _activeMs + (Date.now() - (_activeStart || Date.now()))
        });
      }
    }, ms);
  });

  // ── page_exit beacon ──────────────────────────────────────────────────────
  // Sent via sendBeacon (survives tab close / navigation / app switch).
  // Falls back to synchronous XHR for environments that lack sendBeacon
  // (some older Meta/Facebook in-app WebView versions).
  function firePageExit() {
    if (_exitFired) return;
    _exitFired = true;

    // Flush any accumulated active time from the current visibility window
    if (_activeStart !== null) {
      _activeMs += Date.now() - _activeStart;
      _activeStart = null;
    }

    const dwell = Date.now() - _pageStart;
    const scrollPct = Math.max(_maxScroll, getScrollPct());

    const body = {
      ClientEventId: uuid(),
      EventType: 'page_exit',
      PageKey: PAGE_KEY,
      Url: window.location.href,
      Path: window.location.pathname,
      Referrer: document.referrer || null,
      SessionId: getSessionId(),
      VisitorId: getVisitorId(),
      AgentTrackingProfileId: AGENT_ID,
      AgentSlug: AGENT_SLUG,
      EventUtc: new Date().toISOString(),
      IsInternal: false,
      DwellMilliseconds: dwell,
      EngagedMilliseconds: _activeMs,
      ScrollPercent: scrollPct,
      IsBounceCandidate: dwell < 10000,
      IsExitPage: true
    };

    const json = JSON.stringify(body);
    const blob = new Blob([json], { type: 'application/json' });

    if (navigator.sendBeacon && navigator.sendBeacon(INGEST_URL, blob)) {
      return;
    }
    // Synchronous XHR fallback (Meta in-app browser / older WebViews)
    try {
      const xhr = new XMLHttpRequest();
      xhr.open('POST', INGEST_URL, false);
      xhr.setRequestHeader('Content-Type', 'application/json');
      xhr.send(json);
    } catch { /* swallow */ }
  }

  // ── Visibility lifecycle ──────────────────────────────────────────────────
  document.addEventListener('visibilitychange', function () {
    if (document.visibilityState === 'hidden') {
      if (_activeStart !== null) {
        _activeMs += Date.now() - _activeStart;
        _activeStart = null;
      }
      firePageExit();
    } else {
      // Page became visible again (tab switch back, app foreground)
      _activeStart = Date.now();
      _exitFired = false; // allow a new exit beacon on next hide
    }
  });

  // ── Standard tracking ─────────────────────────────────────────────────────

  function trackPageView() {
    sendEvent({ EventType: 'page_view' });
  }

  function wireClick(selector, elementKey, eventType) {
    document.querySelectorAll(selector).forEach(el => {
      el.addEventListener('click', () => {
        const key = `${eventType}:${elementKey}`;
        if (!shouldFire(key)) return;
        sendEvent({
          EventType: eventType,
          ElementKey: elementKey,
          ButtonLabel: el.textContent?.trim() || null
        });
      });
    });
  }

  function wireFormStart(selector, formKey) {
    const form = document.querySelector(selector);
    if (!form) return;
    const sessionFlag = `form_started_${formKey}`;
    let fired = sessionStorage.getItem(sessionFlag) === '1';
    const handler = () => {
      if (fired) return;
      fired = true;
      sessionStorage.setItem(sessionFlag, '1');
      sendEvent({ EventType: 'form_start', FormKey: formKey });
    };
    form.addEventListener('focusin', handler, { once: true });
    form.addEventListener('change', handler, { once: true });
  }

  // Page key mapping via data-page-key on body (set in views).
  trackPageView();

  // Canonical CTA wiring (selectors per existing markup)
  wireClick('[data-cta="hero_start_assessment"]', 'hero_start_assessment', 'cta_click');
  wireClick('[data-cta="hero_book_call"]', 'hero_book_call', 'cta_click');
  wireClick('[data-cta="hero_start_quote"]', 'hero_start_quote', 'quote_click');
  wireClick('[data-cta="footer_book_call"]', 'footer_book_call', 'cta_click');
  wireClick('[data-cta="nav_home"]', 'nav_home', 'cta_click');
  wireClick('[data-cta="nav_risk_assessment"]', 'nav_risk_assessment', 'risk_assessment_click');
  wireClick('[data-cta="nav_quote"]', 'nav_quote', 'quote_click');
  wireClick('[data-cta="nav_contact"]', 'nav_contact', 'cta_click');

  // Quote tiles/buttons
  wireClick('[data-cta="quote_index_auto_start"]', 'quote_index_auto_start', 'quote_click');
  wireClick('[data-cta="quote_index_home_start"]', 'quote_index_home_start', 'quote_click');
  wireClick('[data-cta="quote_index_commercial_start"]', 'quote_index_commercial_start', 'quote_click');
  wireClick('[data-cta="quote_index_life_start"]', 'quote_index_life_start', 'quote_click');
  wireClick('[data-cta="quote_index_disability_start"]', 'quote_index_disability_start', 'quote_click');
  wireClick('[data-cta="quote_index_health_start"]', 'quote_index_health_start', 'quote_click');

  // Form start wiring (quote/risk forms keyed by data-form-key)
  document.querySelectorAll('form[data-form-key]').forEach(f => {
    const key = f.getAttribute('data-form-key');
    if (!key) return;
    wireFormStart(`form[data-form-key="${key}"]`, key);
  });

  // Expose helpers for other scripts (lead modal)
  window.legendTrack = (payload) => sendEvent(payload);
  window.legendTrackingIds = {
    getVisitorId,
    getSessionId
  };
})();
