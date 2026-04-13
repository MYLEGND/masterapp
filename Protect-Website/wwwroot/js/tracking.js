(() => {
  const INGEST_URL = '/api/tracking/ingest';
  const PAGE_KEY = document.body.dataset.pageKey || '';
  const PAGE_VARIANT = document.body.dataset.pageVariant || '';
  const PAGE_MODE = document.body.dataset.pageMode || '';
  const PAGE_CATEGORY = document.body.dataset.pageCategory || '';
  const PAGE_QUOTE_TYPE = document.body.dataset.quoteType || '';
  const AGENT_ID = window.AGENT_TRACKING_PROFILE_ID || null;
  const AGENT_SLUG = window.AGENT_TRACKING_SLUG || null;

  const STORAGE_VISITOR = 'legend_visitor_id';
  const STORAGE_SESSION = 'legend_session_id';
  const STORAGE_SESSION_TS = 'legend_session_ts';
  const SESSION_TIMEOUT_MIN = 30;
  const DEBOUNCE_MS = 2000;
  const _formTrackStateByKey = new Map();

  const allowedEvents = new Set([
    'page_view','cta_click','quote_click','risk_assessment_click',
    'form_start','form_submit','outbound_click',
    'lead_modal_open','lead_form_start','lead_form_submit_success','lead_form_submit_failed',
    // Behavior Intelligence
    'page_exit',
    'page_engaged_10s','page_engaged_30s','page_engaged_60s',
    'scroll_depth_25','scroll_depth_50','scroll_depth_75','scroll_depth_90','scroll_depth_100',
    'page_visibility_hidden','page_visibility_return',
    // Form Abandonment Intelligence
    'form_field_focus','form_field_complete','form_field_error',
    'form_submit_attempt','form_abandon',
    // Life quote funnel micro-step + bridge events
    'life_step1_intro_view',
    'life_step1_protecting_view','life_step1_protecting_select',
    'life_step1_goal_view','life_step1_goal_select',
    'life_step1_tobacco_view','life_step1_tobacco_select',
    'life_step1_age_view','life_step1_age_continue',
    'step1_age_entered',
    'life_processing_bridge_view','life_processing_bridge_complete',
    'life_value_bridge_view','life_value_bridge_continue',
    'life_step2_view','life_step2_back',
    'life_step2_submit_attempt','life_step2_submit_success'
  ]);

  function uuid() {
    return crypto.randomUUID ? crypto.randomUUID() : ([1e7]+-1e3+-4e3+-8e3+-1e11).replace(/[018]/g,c=>(c ^ crypto.getRandomValues(new Uint8Array(1))[0] & 15 >> c / 4).toString(16));
  }

  function getVisitorId() {
    let v = localStorage.getItem(STORAGE_VISITOR);
    if (!v) { v = uuid(); localStorage.setItem(STORAGE_VISITOR, v); }
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

  // ── Shared body builder (used by both sendEvent and beaconSend) ───────────
  function buildPageContextMetadata() {
    const metadata = {
      pageVariant: PAGE_VARIANT || null,
      pageMode: PAGE_MODE || null,
      pageCategory: PAGE_CATEGORY || null,
      pagePath: window.location.pathname || null
    };
    const hasMetadata = Object.values(metadata).some(v => v !== null && v !== '');
    return hasMetadata ? JSON.stringify(metadata) : null;
  }

  function buildBody(payload) {
    return {
      ClientEventId: uuid(),
      EventType: payload.EventType,
      PageKey: payload.PageKey || PAGE_KEY,
      SectionKey: payload.SectionKey || null,
      ElementKey: payload.ElementKey || null,
      ButtonLabel: payload.ButtonLabel || null,
      FormKey: payload.FormKey || null,
      QuoteType: payload.QuoteType || PAGE_QUOTE_TYPE || null,
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
      FieldName: payload.FieldName || null,
      DwellMilliseconds: payload.DwellMilliseconds != null ? payload.DwellMilliseconds : null,
      EngagedMilliseconds: payload.EngagedMilliseconds != null ? payload.EngagedMilliseconds : null,
      ScrollPercent: payload.ScrollPercent != null ? payload.ScrollPercent : null,
      IsBounceCandidate: payload.IsBounceCandidate != null ? payload.IsBounceCandidate : null,
      IsExitPage: payload.IsExitPage || null
    };
  }

  // ── Async fetch (normal mid-session events) ───────────────────────────────
  async function sendEvent(payload) {
    try {
      const body = buildBody(payload);
      if (!allowedEvents.has(body.EventType)) return;
      if (body.EventType === 'form_start' || body.EventType === 'form_submit') {
        body.FormKey = body.FormKey || payload.FormKey;
      }
      if (body.EventType === 'form_submit' &&
          typeof body.SubmitOutcome === 'string' &&
          body.SubmitOutcome.toLowerCase() === 'success' &&
          body.FormKey) {
        const state = _formTrackStateByKey.get(body.FormKey);
        if (state) {
          state.submitted = true;
          state.submitAttempted = true;
        }
      }
      await fetch(INGEST_URL, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body)
      });
    } catch { /* swallow */ }
  }

  // ── Beacon send (page-exit and form-abandon — survives navigation/close) ──
  function beaconSend(body) {
    const json = JSON.stringify(body);
    const blob = new Blob([json], { type: 'application/json' });
    if (navigator.sendBeacon && navigator.sendBeacon(INGEST_URL, blob)) return;
    // Sync XHR fallback for Meta in-app / older WebViews
    try {
      const xhr = new XMLHttpRequest();
      xhr.open('POST', INGEST_URL, false);
      xhr.setRequestHeader('Content-Type', 'application/json');
      xhr.send(json);
    } catch { /* swallow */ }
  }

  const debounceMap = new Map();
  function shouldFire(key) {
    const now = Date.now();
    const last = debounceMap.get(key) || 0;
    if (now - last < DEBOUNCE_MS) return false;
    debounceMap.set(key, now);
    return true;
  }

  // ── Behavior Intelligence instrumentation ─────────────────────────────────
  const _pageStart = Date.now();
  let _maxScroll = 0;
  let _activeMs = 0;
  let _activeStart = document.visibilityState === 'visible' ? Date.now() : null;
  let _exitFired = false;

  function getScrollPct() {
    const d = document.documentElement;
    const scrollable = d.scrollHeight - d.clientHeight;
    if (scrollable <= 0) return 100;
    return Math.round((d.scrollTop / scrollable) * 100);
  }

  // Scroll milestones
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

  // Engagement checkpoints
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

  function firePageExit() {
    if (_exitFired) return;
    _exitFired = true;
    if (_activeStart !== null) {
      _activeMs += Date.now() - _activeStart;
      _activeStart = null;
    }
    const dwell = Date.now() - _pageStart;
    const scrollPct = Math.max(_maxScroll, getScrollPct());
    beaconSend(buildBody({
      EventType: 'page_exit',
      DwellMilliseconds: dwell,
      EngagedMilliseconds: _activeMs,
      ScrollPercent: scrollPct,
      IsBounceCandidate: dwell < 10000,
      IsExitPage: true
    }));
  }

  // ── Form Abandonment Intelligence ─────────────────────────────────────────
  //
  // Auto-instruments every form[data-form-key] on the page.
  // Fires field_focus, field_complete, field_error mid-session via async fetch.
  // Fires form_abandon via sendBeacon during terminal page lifecycle events.
  // Never sends raw field values. All payloads use MetadataJson for extra context.

  // Canonical field name normalization
  const FIELD_NORM = {
    firstname: 'first_name',         last_name: 'last_name',
    lastname: 'last_name',
    email: 'email',                  emailaddress: 'email',    businessemail: 'email',
    phone: 'phone',                  phonenumber: 'phone',     businessphone: 'phone',
    state: 'state',                  addressstate: 'state',
    marketingemailconsent: 'consent_contact',
    acknowledgeddisclaimer: 'consent_contact',
    householdsize: 'household_size',
    primaryconcern: 'primary_concern',
    contactmethod: 'contact_method',
    besttimetocontact: 'best_time_to_contact',
    coveragetype: 'coverage_type',
    drivercount: 'driver_count',     vehiclecount: 'vehicle_count',
    priorcarrier: 'prior_carrier',
    policyformtype: 'policy_type',   dwellingtype: 'dwelling_type',
    businessname: 'business_name',
    insuredfirstname: 'first_name',  insuredlastname: 'last_name',
    employmenttype: 'employment_type', occupation: 'occupation',
    age: 'age',
  };

  function normField(name) {
    if (!name) return 'unknown';
    // Life wizard step choices: "step-0-option" → "step_1_choice"
    const stepMatch = name.match(/^step-?(\d+)-?option$/i);
    if (stepMatch) return 'step_' + (parseInt(stepMatch[1], 10) + 1) + '_choice';
    const k = name.toLowerCase().replace(/[^a-z0-9]/g, '');
    return FIELD_NORM[k] || name.toLowerCase().replace(/\s+/g,'_').replace(/[^a-z0-9_]/g,'').replace(/^_+|_+$/g,'') || 'unknown';
  }

  function formQuoteType(formKey) {
    if (!formKey) return null;
    const k = formKey.toLowerCase();
    // Specific multi-word life products must be checked before the generic 'life' catch-all
    if (k.includes('mortgage_protection') || k.includes('mortgageprotection')) return 'mortgage_protection';
    if (k.includes('final_expense')       || k.includes('finalexpense'))       return 'final_expense';
    if (k.includes('iul'))                                                      return 'iul';
    if (k.includes('life'))        return 'life';
    if (k.includes('auto'))        return 'auto';
    if (k.includes('home'))        return 'home';
    if (k.includes('commercial'))  return 'commercial';
    if (k.includes('health'))      return 'health';
    if (k.includes('disability'))  return 'disability';
    return k;
  }

  // Hidden / attribution fields we must never track
  const SKIP_NAMES = new Set([
    'sessionid','visitorid','utmsource','utmmedium','utmcampaign','utmterm',
    'utmcontent','fbclid','referrerurl','landingpageurl','offerkey','producttype',
    'pagekeyvalue','protectfocus','answer1','answer2','answer3','answer4',
    'agerange','agentslug','requestverificationtoken','pagekeyfield',
  ]);

  function isTrackable(el) {
    if (!el) return false;
    if (el.type === 'hidden') return false;
    if (el.type === 'submit' || el.type === 'button' || el.type === 'reset') return false;
    const n = (el.name || el.id || '').toLowerCase().replace(/[^a-z0-9]/g, '');
    return !SKIP_NAMES.has(n);
  }

  // Registry of active form abandon callbacks — fired in visibilitychange handler
  const _formAbandonCallbacks = [];

  function wireFormTracking(formEl) {
    const formKey = formEl.dataset.formKey || '';
    const quoteType = formQuoteType(formKey);
    if (!formKey) return;

    const state = {
      started: false,
      submitted: false,
      submitAttempted: false,
      firstInteractionAt: null,
      lastFocusedField: null,
      lastCompletedField: null,
      focusedFields: new Set(),
      completedFields: new Set(),
      errorsSeen: new Set(),     // "fieldName:errorType" deduplication
      consentInteracted: false,
      abandonFired: false,
    };
    _formTrackStateByKey.set(formKey, state);

    function markStarted() {
      if (!state.started) {
        state.started = true;
        state.firstInteractionAt = Date.now();
      }
    }

    function onFocus(fieldName, fieldType, required) {
      markStarted();
      state.lastFocusedField = fieldName;
      if (state.focusedFields.has(fieldName)) return; // once per page load
      state.focusedFields.add(fieldName);
      sendEvent({
        EventType: 'form_field_focus',
        FormKey: formKey,
        QuoteType: quoteType,
        FieldName: fieldName,
        MetadataJson: JSON.stringify({ fieldType, required: !!required }),
      });
    }

    function onComplete(fieldName, fieldType, required, extra) {
      markStarted();
      state.lastCompletedField = fieldName;
      state.completedFields.add(fieldName);
      const meta = Object.assign({ fieldType, required: !!required }, extra || {});
      sendEvent({
        EventType: 'form_field_complete',
        FormKey: formKey,
        QuoteType: quoteType,
        FieldName: fieldName,
        MetadataJson: JSON.stringify(meta),
      });
    }

    function onError(fieldName, errorType) {
      const key = fieldName + ':' + errorType;
      if (state.errorsSeen.has(key)) return; // dedupe per field/error combo
      state.errorsSeen.add(key);
      sendEvent({
        EventType: 'form_field_error',
        FormKey: formKey,
        QuoteType: quoteType,
        FieldName: fieldName,
        MetadataJson: JSON.stringify({ errorType }),
      });
    }

    function onBlur(el, fieldName, fieldType, required) {
      const val = (el.value || '').trim();
      if (val !== '' && !state.completedFields.has(fieldName)) {
        onComplete(fieldName, fieldType, required);
      }
      if (state.focusedFields.has(fieldName)) {
        if (required && val === '') onError(fieldName, 'required_missing');
        if (fieldType === 'email' && val !== '' && !val.includes('@')) onError(fieldName, 'invalid_email');
        if (fieldType === 'tel'   && val !== '' && val.replace(/\D/g,'').length < 7) onError(fieldName, 'invalid_phone');
      }
    }

    // Wire all visible user-facing fields
    formEl.querySelectorAll('input, select, textarea').forEach(el => {
      if (!isTrackable(el)) return;
      const rawName = el.name || el.id || '';
      const fieldName = normField(rawName);
      const fieldType = el.type || el.tagName.toLowerCase();
      const required = el.required || el.getAttribute('required') !== null;

      if (el.type === 'checkbox') {
        el.addEventListener('change', () => {
          markStarted();
          state.lastFocusedField = fieldName;
          const isConsent = /consent|disclaimer/i.test(rawName);
          if (isConsent) state.consentInteracted = true;
          onComplete(fieldName, 'checkbox', required, { checked: el.checked });
        });
      } else if (el.type === 'radio') {
        el.addEventListener('change', () => {
          markStarted();
          state.lastFocusedField = fieldName;
          onComplete(fieldName, 'radio', required);
        });
      } else if (el.tagName === 'SELECT') {
        el.addEventListener('focus', () => onFocus(fieldName, 'select', required));
        el.addEventListener('change', () => {
          if (el.value && el.value !== '') onComplete(fieldName, 'select', required);
        });
      } else {
        el.addEventListener('focus', () => onFocus(fieldName, fieldType, required));
        el.addEventListener('blur', () => onBlur(el, fieldName, fieldType, required));
      }
    });

    // Expose for the form's own submit handler to call
    formEl._trackSubmitAttempt = function (validationPassed, invalidCount) {
      state.submitAttempted = true;
      sendEvent({
        EventType: 'form_submit_attempt',
        FormKey: formKey,
        QuoteType: quoteType,
        MetadataJson: JSON.stringify({
          validationPassed: !!validationPassed,
          invalidFieldCount: invalidCount || 0,
        }),
      });
    };

    formEl._trackSubmitSuccess = function () {
      state.submitted = true;
      state.submitAttempted = true;
    };

    // Native submits (non-AJAX forms) should never be counted as abandon.
    // We treat a valid submit dispatch as terminal for abandonment purposes.
    formEl.addEventListener('submit', () => {
      if (typeof formEl.checkValidity === 'function' && !formEl.checkValidity()) return;
      state.submitAttempted = true;
      state.submitted = true;
    }, true);

    // Register abandon callback — fired during terminal page lifecycle events (pagehide/beforeunload)
    _formAbandonCallbacks.push(function () {
      if (!state.started || state.submitted || state.abandonFired) return;
      state.abandonFired = true;
      beaconSend(buildBody({
        EventType: 'form_abandon',
        FormKey: formKey,
        QuoteType: quoteType,
        MetadataJson: JSON.stringify({
          lastFocusedField: state.lastFocusedField,
          lastCompletedField: state.lastCompletedField,
          submitAttempted: state.submitAttempted,
          completedFieldCount: state.completedFields.size,
          errorCount: state.errorsSeen.size,
          consentInteracted: state.consentInteracted,
          timeOnFormMs: state.firstInteractionAt ? Date.now() - state.firstInteractionAt : 0,
          quoteType,
        }),
      }));
    });
  }

  // Instrument all quote forms on the page
  document.querySelectorAll('form[data-form-key]').forEach(wireFormTracking);

  function fireExitSignals() {
    firePageExit();
    _formAbandonCallbacks.forEach(function (cb) { try { cb(); } catch { /* swallow */ } });
  }

  // ── Visibility/page lifecycle ──────────────────────────────────────────────
  document.addEventListener('visibilitychange', function () {
    if (document.visibilityState === 'hidden') {
      if (_activeStart !== null) {
        _activeMs += Date.now() - _activeStart;
        _activeStart = null;
      }
      sendEvent({
        EventType: 'page_visibility_hidden',
        DwellMilliseconds: Date.now() - _pageStart,
        EngagedMilliseconds: _activeMs,
        ScrollPercent: Math.max(_maxScroll, getScrollPct())
      });
    } else {
      if (_activeStart === null) {
        _activeStart = Date.now();
      }
      sendEvent({
        EventType: 'page_visibility_return',
        DwellMilliseconds: Date.now() - _pageStart,
        EngagedMilliseconds: _activeMs
      });
    }
  });
  window.addEventListener('pagehide', fireExitSignals);
  window.addEventListener('beforeunload', fireExitSignals);

  // ── Standard tracking ─────────────────────────────────────────────────────

  function trackPageView() {
    sendEvent({
      EventType: 'page_view',
      MetadataJson: buildPageContextMetadata()
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

  trackPageView();

  wireClick('[data-cta="hero_start_assessment"]',    'hero_start_assessment',    'cta_click');
  wireClick('[data-cta="hero_book_call"]',           'hero_book_call',           'cta_click');
  wireClick('[data-cta="hero_start_quote"]',         'hero_start_quote',         'quote_click');
  wireClick('[data-cta="footer_book_call"]',         'footer_book_call',         'cta_click');
  wireClick('[data-cta="nav_home"]',                 'nav_home',                 'cta_click');
  wireClick('[data-cta="nav_risk_assessment"]',      'nav_risk_assessment',      'risk_assessment_click');
  wireClick('[data-cta="nav_quote"]',                'nav_quote',                'quote_click');
  wireClick('[data-cta="nav_contact"]',              'nav_contact',              'cta_click');

  wireClick('[data-cta="quote_index_auto_start"]',       'quote_index_auto_start',       'quote_click');
  wireClick('[data-cta="quote_index_home_start"]',       'quote_index_home_start',       'quote_click');
  wireClick('[data-cta="quote_index_commercial_start"]', 'quote_index_commercial_start', 'quote_click');
  wireClick('[data-cta="quote_index_life_start"]',       'quote_index_life_start',       'quote_click');
  wireClick('[data-cta="quote_index_disability_start"]', 'quote_index_disability_start', 'quote_click');
  wireClick('[data-cta="quote_index_health_start"]',     'quote_index_health_start',     'quote_click');

  document.querySelectorAll('form[data-form-key]').forEach(f => {
    const key = f.getAttribute('data-form-key');
    if (!key) return;
    wireFormStart(`form[data-form-key="${key}"]`, key);
  });

  window.legendTrack = (payload) => sendEvent(payload);
  window.legendTrackingIds = { getVisitorId, getSessionId };
})();
