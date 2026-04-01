(() => {
  const SECRET = window.TRACKING_SECRET || '';
  const API_BASE = (window.TRACKING_API_BASE || '').replace(/\/$/, '');
  const INGEST_URL = (API_BASE ? `${API_BASE}` : '') + '/api/analytics/ingest';
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
    'lead_modal_open','lead_form_start','lead_form_submit_success','lead_form_submit_failed'
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
      if (!SECRET) return;
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
        IsInternal: false
      };

      if (!allowedEvents.has(body.EventType)) return;
      if (body.EventType === 'form_start' || body.EventType === 'form_submit') {
        body.FormKey = body.FormKey || payload.FormKey;
      }

      await fetch(INGEST_URL, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'X-Shared-Secret': SECRET
        },
        body: JSON.stringify(body)
      });
    } catch {
      /* swallow */
    }
  }

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
