(() => {
  const INGEST_URL = '/api/tracking/ingest';
  const PAGE_KEY = document.body.dataset.pageKey || '';
  const AGENT_ID = window.AGENT_TRACKING_PROFILE_ID || null;
  const AGENT_SLUG = window.AGENT_TRACKING_SLUG || null;

  const STORAGE_VISITOR = 'legend_visitor_id';
  const STORAGE_SESSION = 'legend_session_id';
  const STORAGE_SESSION_TS = 'legend_session_ts';
  const STORAGE_UTM = 'legend_utm_attribution';
  const SESSION_TIMEOUT_MIN = 30;
  const DEBOUNCE_MS = 2000;
  const RAGE_WINDOW_MS = 600;
  const RAGE_RADIUS_PX = 40;

  const allowedEvents = new Set([
    'page_view','cta_click','quote_click','risk_assessment_click',
    'form_start','form_submit','outbound_click',
    'lead_modal_open','lead_form_start','lead_form_submit_success','lead_form_submit_failed',
    'page_engaged_10s','page_engaged_30s','page_engaged_60s',
    'scroll_depth_25','scroll_depth_50','scroll_depth_75','scroll_depth_90','scroll_depth_100',
    'page_exit','session_end','form_field_focus','form_field_complete','form_field_abandon',
    'rage_click','dead_click','file_download','section_view','page_visibility_hidden','page_visibility_return'
  ]);

  const pageStartMs = Date.now();
  let activeMs = 0;
  let lastActiveTick = Date.now();
  const engagementFired = new Set();
  const scrollFired = new Set();
  const rageQueue = [];

  function readUtm() {
    const qs = new URLSearchParams(window.location.search || '');
    const incoming = {
      source: qs.get('utm_source'),
      medium: qs.get('utm_medium'),
      campaign: qs.get('utm_campaign'),
      term: qs.get('utm_term'),
      content: qs.get('utm_content')
    };
    const hasIncoming = Object.values(incoming).some(Boolean);
    if (hasIncoming) {
      sessionStorage.setItem(STORAGE_UTM, JSON.stringify(incoming));
      return incoming;
    }
    try {
      const cached = JSON.parse(sessionStorage.getItem(STORAGE_UTM) || '{}');
      return {
        source: cached.source || null,
        medium: cached.medium || null,
        campaign: cached.campaign || null,
        term: cached.term || null,
        content: cached.content || null
      };
    } catch {
      return { source: null, medium: null, campaign: null, term: null, content: null };
    }
  }

  const utm = readUtm();

  function parseDevice() {
    const ua = navigator.userAgent || '';
    const platform = navigator.platform || '';
    const deviceType = /Mobi|Android|iPhone|iPad|iPod/i.test(ua)
      ? (/iPad|Tablet/i.test(ua) ? 'tablet' : 'mobile')
      : 'desktop';
    const browser = /Edg\//.test(ua) ? 'Edge'
      : /Chrome\//.test(ua) ? 'Chrome'
      : /Safari\//.test(ua) && !/Chrome\//.test(ua) ? 'Safari'
      : /Firefox\//.test(ua) ? 'Firefox'
      : /MSIE|Trident/.test(ua) ? 'IE'
      : 'Unknown';
    const os = /Windows/i.test(platform) ? 'Windows'
      : /Mac/i.test(platform) ? 'macOS'
      : /Linux/i.test(platform) ? 'Linux'
      : /iPhone|iPad|iPod/i.test(ua) ? 'iOS'
      : /Android/i.test(ua) ? 'Android'
      : 'Unknown';

    return { deviceType, browser, os };
  }

  const device = parseDevice();

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
        UtmSource: payload.UtmSource || utm.source || null,
        UtmMedium: payload.UtmMedium || utm.medium || null,
        UtmCampaign: payload.UtmCampaign || utm.campaign || null,
        UtmTerm: payload.UtmTerm || utm.term || null,
        UtmContent: payload.UtmContent || utm.content || null,
        SubmitOutcome: payload.SubmitOutcome || null,
        MetadataJson: payload.MetadataJson || null,
        AgentTrackingProfileId: AGENT_ID,
        AgentSlug: AGENT_SLUG,
        Environment: payload.Environment || null,
        Host: payload.Host || null,
        EventUtc: new Date().toISOString(),
        IsInternal: false,
        ReferrerHost: payload.ReferrerHost || (document.referrer ? (() => { try { return new URL(document.referrer).host; } catch { return null; } })() : null),
        DeviceType: payload.DeviceType || device.deviceType,
        Browser: payload.Browser || device.browser,
        OperatingSystem: payload.OperatingSystem || device.os,
        ScreenWidth: payload.ScreenWidth ?? window.screen?.width ?? null,
        ScreenHeight: payload.ScreenHeight ?? window.screen?.height ?? null,
        ViewportWidth: payload.ViewportWidth ?? window.innerWidth ?? null,
        ViewportHeight: payload.ViewportHeight ?? window.innerHeight ?? null,
        ScrollPercent: payload.ScrollPercent ?? null,
        DwellMilliseconds: payload.DwellMilliseconds ?? null,
        EngagedMilliseconds: payload.EngagedMilliseconds ?? activeMs,
        IsBounceCandidate: payload.IsBounceCandidate ?? null,
        IsExitPage: payload.IsExitPage ?? null,
        FormId: payload.FormId ?? null,
        FieldName: payload.FieldName ?? null,
        ElementId: payload.ElementId ?? null
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

  function sendBeaconEvent(payload) {
    try {
      if (!allowedEvents.has(payload.EventType)) return;
      const body = {
        ClientEventId: uuid(),
        EventType: payload.EventType,
        PageKey: payload.PageKey || PAGE_KEY,
        ElementKey: payload.ElementKey || null,
        Url: window.location.href,
        Path: window.location.pathname,
        Referrer: document.referrer || null,
        SessionId: getSessionId(),
        VisitorId: getVisitorId(),
        UtmSource: utm.source,
        UtmMedium: utm.medium,
        UtmCampaign: utm.campaign,
        UtmTerm: utm.term,
        UtmContent: utm.content,
        AgentTrackingProfileId: AGENT_ID,
        AgentSlug: AGENT_SLUG,
        EventUtc: new Date().toISOString(),
        IsInternal: false,
        DeviceType: device.deviceType,
        Browser: device.browser,
        OperatingSystem: device.os,
        ScreenWidth: window.screen?.width ?? null,
        ScreenHeight: window.screen?.height ?? null,
        ViewportWidth: window.innerWidth ?? null,
        ViewportHeight: window.innerHeight ?? null,
        ScrollPercent: payload.ScrollPercent ?? currentScrollPercent(),
        DwellMilliseconds: payload.DwellMilliseconds ?? (Date.now() - pageStartMs),
        EngagedMilliseconds: payload.EngagedMilliseconds ?? activeMs,
        IsBounceCandidate: payload.IsBounceCandidate ?? null,
        IsExitPage: payload.IsExitPage ?? null
      };
      const blob = new Blob([JSON.stringify(body)], { type: 'application/json' });
      navigator.sendBeacon?.(INGEST_URL, blob);
    } catch {
      /* swallow */
    }
  }

  function currentScrollPercent() {
    const doc = document.documentElement;
    const total = Math.max(doc.scrollHeight - window.innerHeight, 1);
    const pct = Math.round((window.scrollY / total) * 100);
    return Math.max(0, Math.min(100, pct));
  }

  function engagementTick() {
    const now = Date.now();
    if (document.visibilityState === 'visible') {
      activeMs += (now - lastActiveTick);
    }
    lastActiveTick = now;

    const checkpoints = [10_000, 30_000, 60_000];
    checkpoints.forEach(ms => {
      if (activeMs >= ms && !engagementFired.has(ms)) {
        engagementFired.add(ms);
        sendEvent({ EventType: `page_engaged_${ms / 1000}s`, EngagedMilliseconds: activeMs });
      }
    });
  }

  function wireScrollDepth() {
    const marks = [25, 50, 75, 90, 100];
    const handler = () => {
      const pct = currentScrollPercent();
      marks.forEach(m => {
        if (pct >= m && !scrollFired.has(m)) {
          scrollFired.add(m);
          sendEvent({ EventType: `scroll_depth_${m}`, ScrollPercent: m });
        }
      });
    };
    window.addEventListener('scroll', handler, { passive: true });
    handler();
  }

  function wireVisibility() {
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'hidden') {
        sendEvent({ EventType: 'page_visibility_hidden', EngagedMilliseconds: activeMs });
      } else if (document.visibilityState === 'visible') {
        sendEvent({ EventType: 'page_visibility_return', EngagedMilliseconds: activeMs });
      }
    });
  }

  function wireExit() {
    const handler = () => {
      const dwell = Date.now() - pageStartMs;
      const scroll = currentScrollPercent();
      const bounce = dwell < 10_000 && scroll < 25;
      sendBeaconEvent({ EventType: 'page_exit', DwellMilliseconds: dwell, EngagedMilliseconds: activeMs, ScrollPercent: scroll, IsBounceCandidate: bounce, IsExitPage: true });
      sendBeaconEvent({ EventType: 'session_end', DwellMilliseconds: dwell, EngagedMilliseconds: activeMs, ScrollPercent: scroll, IsBounceCandidate: bounce, IsExitPage: true });
    };
    window.addEventListener('pagehide', handler);
    window.addEventListener('beforeunload', handler);
  }

  function wireRageAndDeadClicks() {
    document.addEventListener('click', (e) => {
      const t = e.target instanceof Element ? e.target : null;
      const now = Date.now();
      const x = e.clientX; const y = e.clientY;

      while (rageQueue.length && (now - rageQueue[0].ts) > RAGE_WINDOW_MS) rageQueue.shift();
      rageQueue.push({ ts: now, x, y });
      const closeHits = rageQueue.filter(c => Math.hypot(c.x - x, c.y - y) <= RAGE_RADIUS_PX).length;
      if (closeHits >= 3) {
        sendEvent({
          EventType: 'rage_click',
          ElementKey: t?.getAttribute('data-cta') || t?.id || t?.className || null,
          ElementId: t?.id || null
        });
        rageQueue.length = 0;
      }

      const clickable = !!t?.closest('a,button,input,select,textarea,label,[role="button"],[data-cta]');
      if (!clickable) {
        sendEvent({
          EventType: 'dead_click',
          ElementKey: t?.id || t?.className || t?.tagName?.toLowerCase() || null,
          ElementId: t?.id || null
        });
      }
    }, true);
  }

  function wireFileDownloads() {
    document.querySelectorAll('a[href]').forEach(a => {
      a.addEventListener('click', () => {
        const href = (a.getAttribute('href') || '').toLowerCase();
        if (!href) return;
        if (!/\.(pdf|doc|docx|xls|xlsx|ppt|pptx|zip|csv)(\?|$)/i.test(href)) return;
        sendEvent({
          EventType: 'file_download',
          ElementKey: a.getAttribute('data-cta') || a.id || href,
          ElementId: a.id || null
        });
      });
    });
  }

  function wireSectionViews() {
    const sections = document.querySelectorAll('[data-section-key]');
    if (!sections.length || typeof IntersectionObserver === 'undefined') return;
    const seen = new Set();
    const io = new IntersectionObserver((entries) => {
      entries.forEach(entry => {
        if (!entry.isIntersecting || entry.intersectionRatio < 0.5) return;
        const el = entry.target;
        const key = el.getAttribute('data-section-key');
        if (!key || seen.has(key)) return;
        seen.add(key);
        sendEvent({ EventType: 'section_view', SectionKey: key, ElementId: el.id || null });
      });
    }, { threshold: [0.5] });
    sections.forEach(s => io.observe(s));
  }

  function trackPageView() {
    sendEvent({
      EventType: 'page_view',
      ScrollPercent: currentScrollPercent(),
      DwellMilliseconds: 0,
      EngagedMilliseconds: 0,
      IsBounceCandidate: true,
      IsExitPage: false
    });
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

  function wireFormFieldBehavior() {
    const fieldState = new WeakMap();
    document.querySelectorAll('form').forEach(form => {
      const formKey = form.getAttribute('data-form-key') || form.id || 'unknown_form';
      form.querySelectorAll('input,select,textarea').forEach(field => {
        field.addEventListener('focus', () => {
          fieldState.set(field, { started: Date.now() });
          sendEvent({
            EventType: 'form_field_focus',
            FormKey: formKey,
            FormId: form.id || null,
            FieldName: field.getAttribute('name') || field.id || 'field',
            ElementId: field.id || null
          });
        });

        field.addEventListener('blur', () => {
          const st = fieldState.get(field);
          const ms = st ? (Date.now() - st.started) : null;
          const value = (field.value || '').trim();
          const common = {
            FormKey: formKey,
            FormId: form.id || null,
            FieldName: field.getAttribute('name') || field.id || 'field',
            ElementId: field.id || null,
            DwellMilliseconds: ms
          };
          if (value) {
            sendEvent({ EventType: 'form_field_complete', ...common });
          } else {
            sendEvent({ EventType: 'form_field_abandon', ...common });
          }
        });
      });
    });
  }

  // Page key mapping via data-page-key on body (set in views).
  trackPageView();
  wireScrollDepth();
  wireVisibility();
  wireExit();
  wireRageAndDeadClicks();
  wireFileDownloads();
  wireSectionViews();
  wireFormFieldBehavior();
  setInterval(engagementTick, 1000);

  // Canonical CTA wiring (selectors per existing markup)
  wireClick('[data-cta=\"hero_start_assessment\"]', 'hero_start_assessment', 'cta_click');
  wireClick('[data-cta=\"hero_book_call\"]', 'hero_book_call', 'cta_click');
  wireClick('[data-cta=\"hero_start_quote\"]', 'hero_start_quote', 'quote_click');
  wireClick('[data-cta=\"footer_book_call\"]', 'footer_book_call', 'cta_click');
  wireClick('[data-cta=\"nav_home\"]', 'nav_home', 'cta_click');
  wireClick('[data-cta=\"nav_risk_assessment\"]', 'nav_risk_assessment', 'risk_assessment_click');
  wireClick('[data-cta=\"nav_quote\"]', 'nav_quote', 'quote_click');
  wireClick('[data-cta=\"nav_contact\"]', 'nav_contact', 'cta_click');

  // Quote tiles/buttons
  wireClick('[data-cta=\"quote_index_auto_start\"]', 'quote_index_auto_start', 'quote_click');
  wireClick('[data-cta=\"quote_index_home_start\"]', 'quote_index_home_start', 'quote_click');
  wireClick('[data-cta=\"quote_index_commercial_start\"]', 'quote_index_commercial_start', 'quote_click');
  wireClick('[data-cta=\"quote_index_life_start\"]', 'quote_index_life_start', 'quote_click');
  wireClick('[data-cta=\"quote_index_disability_start\"]', 'quote_index_disability_start', 'quote_click');
  wireClick('[data-cta=\"quote_index_health_start\"]', 'quote_index_health_start', 'quote_click');

  // Form start wiring (quote/risk forms keyed by data-form-key)
  document.querySelectorAll('form[data-form-key]').forEach(f => {
    const key = f.getAttribute('data-form-key');
    if (!key) return;
    wireFormStart(`form[data-form-key=\"${key}\"]`, key);
  });

  // Expose helpers for other scripts (lead modal)
  window.legendTrack = (payload) => sendEvent(payload);
  window.legendTrackingIds = {
    getVisitorId,
    getSessionId
  };
})();
