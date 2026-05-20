(() => {
  if (window.__legendTrackingInitialized) {
    return;
  }
  window.__legendTrackingInitialized = true;

  const INGEST_URL = '/api/tracking/ingest';
  const PAGE_KEY = document.body.dataset.pageKey || '';
  const PAGE_VARIANT = document.body.dataset.pageVariant || '';
  const PAGE_MODE = document.body.dataset.pageMode || '';
  const PAGE_CATEGORY = document.body.dataset.pageCategory || '';
  const PAGE_QUOTE_TYPE = document.body.dataset.quoteType || '';
  const AGENT_ID = window.AGENT_TRACKING_PROFILE_ID || null;
  const AGENT_SLUG = window.AGENT_TRACKING_SLUG || null;
  const DEBUG_TRACKING =
    window.location.hostname === 'localhost' ||
    window.location.hostname === '127.0.0.1' ||
    new URLSearchParams(window.location.search).has('trackingDebug');

  const STORAGE_VISITOR = 'legend_visitor_id';
  const STORAGE_SESSION = 'legend_session_id';
  const STORAGE_SESSION_TS = 'legend_session_ts';
  const STORAGE_ATTR_SESSION = 'legend_attr_session';
  const STORAGE_ATTR_FIRST_TOUCH = 'legend_attr_first_touch';
  const SESSION_TIMEOUT_MIN = 30;
  const DEBOUNCE_MS = 2000;
  const _formTrackStateByKey = new Map();

  function debug(message, details) {
    if (!DEBUG_TRACKING) return;
    try {
      console.debug('[legend-tracking]', message, details || '');
    } catch {
      // Swallow console issues in legacy browsers.
    }
  }

  const allowedEvents = new Set([
    'page_view','cta_click','quote_click','risk_assessment_click',
    'form_start','form_submit','outbound_click',
    'lead_modal_open','lead_modal_close','lead_form_start','lead_form_submit_success','lead_form_submit_failed',
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
    'life_step2_submit_attempt','life_step2_submit_success',
    'mini_results_view',
    'recommendation_generated',
    'results_contact_submit',
    'life_general_form_start','life_general_submit',
    'life_term_form_start','life_term_submit',
    'life_whole_form_start','life_whole_submit',
    'life_finalexpense_form_start','life_finalexpense_submit',
    'life_mp_form_start','life_mp_submit',
    'life_iul_form_start','life_iul_submit'
  ]);

  function uuid() {
    return crypto.randomUUID ? crypto.randomUUID() : ([1e7]+-1e3+-4e3+-8e3+-1e11).replace(/[018]/g,c=>(c ^ crypto.getRandomValues(new Uint8Array(1))[0] & 15 >> c / 4).toString(16));
  }

  const FORM_STAGE_RANK = Object.freeze({
    idle: 0,
    started: 1,
    progressed: 2,
    contact_viewed: 3,
    submitted: 4,
    abandoned: 5
  });

  function createFormTrackState(formKey, quoteType = null) {
    return {
      instanceId: uuid(),
      formKey,
      quoteType,
      currentStage: 'idle',
      started: false,
      submitted: false,
      submitAttempted: false,
      firstInteractionAt: null,
      startedAt: null,
      progressedAt: null,
      contactViewedAt: null,
      submitAttemptedAt: null,
      submittedAt: null,
      abandonedAt: null,
      lastStateTransitionAt: null,
      lastStateTransitionReason: null,
      lastFocusedField: null,
      lastCompletedField: null,
      focusedFields: new Set(),
      completedFields: new Set(),
      errorsSeen: new Set(),
      consentInteracted: false,
      abandonFired: false,
      lastLifecycleSignalAt: null,
      lastLifecycleSignalSource: null
    };
  }

  function stageRank(stage) {
    return FORM_STAGE_RANK[stage] ?? 0;
  }

  function transitionFormState(state, nextStage, reason, details = {}) {
    if (!state || !nextStage) return;

    const now = Date.now();
    const previousStage = state.currentStage || 'idle';

    if (previousStage === 'submitted' && nextStage === 'abandoned') {
      debug('ignored abandon transition after submit', {
        formKey: state.formKey,
        instanceId: state.instanceId,
        reason
      });
      return;
    }

    if (previousStage === 'abandoned' && nextStage !== 'submitted') {
      return;
    }

    if (stageRank(nextStage) < stageRank(previousStage)) {
      return;
    }

    if (nextStage === 'started') {
      state.started = true;
      state.firstInteractionAt = state.firstInteractionAt || now;
      state.startedAt = state.startedAt || now;
    }

    if (nextStage === 'progressed') {
      state.started = true;
      state.firstInteractionAt = state.firstInteractionAt || now;
      state.startedAt = state.startedAt || now;
      state.progressedAt = state.progressedAt || now;
    }

    if (nextStage === 'contact_viewed') {
      state.started = true;
      state.firstInteractionAt = state.firstInteractionAt || now;
      state.startedAt = state.startedAt || now;
      state.progressedAt = state.progressedAt || now;
      state.contactViewedAt = state.contactViewedAt || now;
    }

    if (nextStage === 'submitted') {
      state.started = true;
      state.submitted = true;
      state.submitAttempted = true;
      state.firstInteractionAt = state.firstInteractionAt || now;
      state.startedAt = state.startedAt || now;
      state.progressedAt = state.progressedAt || now;
      state.contactViewedAt = state.contactViewedAt || now;
      state.submitAttemptedAt = state.submitAttemptedAt || now;
      state.submittedAt = state.submittedAt || now;
    }

    if (nextStage === 'abandoned') {
      state.abandonedAt = now;
    }

    if (previousStage === nextStage) {
      state.lastStateTransitionAt = now;
      state.lastStateTransitionReason = reason;
      return;
    }

    state.currentStage = nextStage;
    state.lastStateTransitionAt = now;
    state.lastStateTransitionReason = reason;

    debug('form stage transition', {
      formKey: state.formKey,
      quoteType: state.quoteType,
      instanceId: state.instanceId,
      from: previousStage,
      to: nextStage,
      reason,
      details
    });
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
    const sessionExpired = !sid || isNaN(lastTs) || (now - lastTs) > SESSION_TIMEOUT_MIN * 60 * 1000;
    if (sessionExpired) {
      sid = uuid();
      // Prevent stale attribution from a prior session leaking into a new session.
      try {
        sessionStorage.removeItem(STORAGE_ATTR_SESSION);
        localStorage.removeItem(STORAGE_ATTR_SESSION);
      } catch { /* ignore */ }
    }
    localStorage.setItem(STORAGE_SESSION, sid);
    localStorage.setItem(STORAGE_SESSION_TS, String(now));
    return sid;
  }

  function sanitizeAttributionValue(value) {
    if (typeof value !== 'string') return null;
    const trimmed = value.trim();
    return trimmed ? trimmed : null;
  }

  function normalizeAttribution(raw) {
    return {
      utmSource: sanitizeAttributionValue(raw?.utmSource),
      utmMedium: sanitizeAttributionValue(raw?.utmMedium),
      utmCampaign: sanitizeAttributionValue(raw?.utmCampaign),
      utmId: sanitizeAttributionValue(raw?.utmId),
      utmTerm: sanitizeAttributionValue(raw?.utmTerm),
      utmContent: sanitizeAttributionValue(raw?.utmContent),
      fbclid: sanitizeAttributionValue(raw?.fbclid),
      metaCampaignId: sanitizeAttributionValue(raw?.metaCampaignId),
      metaAdSetId: sanitizeAttributionValue(raw?.metaAdSetId),
      metaAdId: sanitizeAttributionValue(raw?.metaAdId)
    };
  }

  function hasAttribution(attribution) {
    if (!attribution) return false;
    return !!(
      attribution.utmSource ||
      attribution.utmMedium ||
      attribution.utmCampaign ||
      attribution.utmId ||
      attribution.utmTerm ||
      attribution.utmContent ||
      attribution.fbclid ||
      attribution.metaCampaignId ||
      attribution.metaAdSetId ||
      attribution.metaAdId
    );
  }

  function readAttributionFromStorage(storage, key) {
    try {
      const raw = storage.getItem(key);
      if (!raw) return null;
      return normalizeAttribution(JSON.parse(raw));
    } catch {
      return null;
    }
  }

  function writeAttributionToStorage(storage, key, attribution) {
    try {
      storage.setItem(key, JSON.stringify(normalizeAttribution(attribution)));
    } catch { /* ignore */ }
  }

  function getStoredAttribution(key) {
    const sessionValue = readAttributionFromStorage(sessionStorage, key);
    if (hasAttribution(sessionValue)) return sessionValue;
    const localValue = readAttributionFromStorage(localStorage, key);
    return hasAttribution(localValue) ? localValue : null;
  }

  function rememberAttribution(attribution) {
    if (!hasAttribution(attribution)) return;
    writeAttributionToStorage(sessionStorage, STORAGE_ATTR_SESSION, attribution);
    writeAttributionToStorage(localStorage, STORAGE_ATTR_SESSION, attribution);

    const firstTouch = getStoredAttribution(STORAGE_ATTR_FIRST_TOUCH);
    if (!hasAttribution(firstTouch)) {
      writeAttributionToStorage(sessionStorage, STORAGE_ATTR_FIRST_TOUCH, attribution);
      writeAttributionToStorage(localStorage, STORAGE_ATTR_FIRST_TOUCH, attribution);
    }
  }

  function readAttributionFromQuery() {
    const params = new URLSearchParams(window.location.search);
    return normalizeAttribution({
      utmSource: params.get('utm_source'),
      utmMedium: params.get('utm_medium'),
      utmCampaign: params.get('utm_campaign'),
      utmId: params.get('utm_id'),
      utmTerm: params.get('utm_term'),
      utmContent: params.get('utm_content'),
      fbclid: params.get('fbclid'),
      metaCampaignId: params.get('meta_campaign_id'),
      metaAdSetId: params.get('meta_adset_id'),
      metaAdId: params.get('meta_ad_id')
    });
  }

  const queryAttribution = readAttributionFromQuery();
  rememberAttribution(queryAttribution);

  function resolveCurrentSessionAttribution(payload, sessionId) {
    const payloadAttribution = normalizeAttribution({
      utmSource: payload.UtmSource,
      utmMedium: payload.UtmMedium,
      utmCampaign: payload.UtmCampaign,
      utmId: payload.UtmId,
      utmTerm: payload.UtmTerm,
      utmContent: payload.UtmContent,
      fbclid: payload.Fbclid,
      metaCampaignId: payload.MetaCampaignId,
      metaAdSetId: payload.MetaAdSetId,
      metaAdId: payload.MetaAdId
    });

    const sessionAttribution = getStoredAttribution(STORAGE_ATTR_SESSION);
    const currentAttribution = normalizeAttribution({
      utmSource: payloadAttribution.utmSource || queryAttribution.utmSource || sessionAttribution?.utmSource,
      utmMedium: payloadAttribution.utmMedium || queryAttribution.utmMedium || sessionAttribution?.utmMedium,
      utmCampaign: payloadAttribution.utmCampaign || queryAttribution.utmCampaign || sessionAttribution?.utmCampaign,
      utmId: payloadAttribution.utmId || queryAttribution.utmId || sessionAttribution?.utmId,
      utmTerm: payloadAttribution.utmTerm || queryAttribution.utmTerm || sessionAttribution?.utmTerm,
      utmContent: payloadAttribution.utmContent || queryAttribution.utmContent || sessionAttribution?.utmContent,
      fbclid: payloadAttribution.fbclid || queryAttribution.fbclid || sessionAttribution?.fbclid,
      metaCampaignId: payloadAttribution.metaCampaignId || queryAttribution.metaCampaignId || sessionAttribution?.metaCampaignId,
      metaAdSetId: payloadAttribution.metaAdSetId || queryAttribution.metaAdSetId || sessionAttribution?.metaAdSetId,
      metaAdId: payloadAttribution.metaAdId || queryAttribution.metaAdId || sessionAttribution?.metaAdId
    });

    if (hasAttribution(currentAttribution)) {
      rememberAttribution(currentAttribution);
      return currentAttribution;
    }

    return normalizeAttribution({});
  }

  function resolveFirstTouchAttribution() {
    const firstTouchAttribution = getStoredAttribution(STORAGE_ATTR_FIRST_TOUCH);
    return normalizeAttribution({
      utmSource: firstTouchAttribution?.utmSource,
      utmMedium: firstTouchAttribution?.utmMedium,
      utmCampaign: firstTouchAttribution?.utmCampaign,
      utmId: firstTouchAttribution?.utmId,
      utmTerm: firstTouchAttribution?.utmTerm,
      utmContent: firstTouchAttribution?.utmContent,
      fbclid: firstTouchAttribution?.fbclid,
      metaCampaignId: firstTouchAttribution?.metaCampaignId,
      metaAdSetId: firstTouchAttribution?.metaAdSetId,
      metaAdId: firstTouchAttribution?.metaAdId
    });
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
    const sessionId = getSessionId();
    const attribution = resolveCurrentSessionAttribution(payload, sessionId);
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
      SessionId: sessionId,
      VisitorId: getVisitorId(),
      UtmSource: attribution.utmSource || null,
      UtmMedium: attribution.utmMedium || null,
      UtmCampaign: attribution.utmCampaign || null,
      UtmId: attribution.utmId || null,
      UtmTerm: attribution.utmTerm || null,
      UtmContent: attribution.utmContent || null,
      Fbclid: attribution.fbclid || null,
      MetaCampaignId: attribution.metaCampaignId || null,
      MetaAdSetId: attribution.metaAdSetId || null,
      MetaAdId: attribution.metaAdId || null,
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
        keepalive: true,
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
    // Keep quote-type names aligned with the page-level tracking value (offer key).
    if (k.includes('mortgage_protection') || k.includes('mortgageprotection')) return 'mortgage_protection';
    if (k.includes('final_expense')       || k.includes('finalexpense'))       return 'final_expense';
    if (k.includes('whole_life')          || k.includes('wholelife'))          return 'whole_life';
    if (k.includes('term_life')           || k.includes('termlife'))           return 'term_life';
    if (k.includes('iul'))                                                      return 'iul';
    if (k.includes('quote_life'))                                               return 'life';
    if (k.includes('life'))                                                     return 'life';
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
    'utmid','utmcontent','fbclid','metacampaignid','metaadsetid','metaadid',
    'referrerurl','landingpageurl','offerkey','producttype',
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
    const abandonSessionFlag = `form_abandon_${getSessionId()}_${formKey}_${quoteType || 'unknown'}`;
    let abandonAlreadyTrackedForSession = false;
    try {
      abandonAlreadyTrackedForSession = sessionStorage.getItem(abandonSessionFlag) === '1';
    } catch { /* ignore */ }

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
      abandonFired: abandonAlreadyTrackedForSession,
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
      try {
        sessionStorage.setItem(abandonSessionFlag, '1');
      } catch { /* ignore */ }
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

  function ensureFormTrackState(formKey) {
    if (!formKey) return null;
    let state = _formTrackStateByKey.get(formKey);
    if (state) return state;

    state = {
      started: false,
      submitted: false,
      submitAttempted: false,
      firstInteractionAt: null,
      lastFocusedField: null,
      lastCompletedField: null,
      focusedFields: new Set(),
      completedFields: new Set(),
      errorsSeen: new Set(),
      consentInteracted: false,
      abandonFired: false,
    };
    _formTrackStateByKey.set(formKey, state);
    return state;
  }

  function trackCustomFieldError(formKey, fieldName, errorType, quoteTypeOverride) {
    const normalizedField = (fieldName || '').trim();
    const normalizedErrorType = (errorType || 'validation_error').trim();
    if (!formKey || !normalizedField) return;

    const state = ensureFormTrackState(formKey);
    if (state) {
      if (!state.started) {
        state.started = true;
        state.firstInteractionAt = state.firstInteractionAt || Date.now();
      }
      const key = `${normalizedField}:${normalizedErrorType}`;
      if (state.errorsSeen.has(key)) return;
      state.errorsSeen.add(key);
    }

    sendEvent({
      EventType: 'form_field_error',
      FormKey: formKey,
      QuoteType: quoteTypeOverride || formQuoteType(formKey),
      FieldName: normalizedField,
      MetadataJson: JSON.stringify({
        errorType: normalizedErrorType,
        source: 'custom_validation',
      }),
    });
  }

  function clearTrackedFieldError(formKey, fieldName) {
    const normalizedField = (fieldName || '').trim();
    if (!formKey || !normalizedField) return;

    const state = ensureFormTrackState(formKey);
    if (!state) return;

    Array.from(state.errorsSeen).forEach(key => {
      if (key.startsWith(normalizedField + ':')) {
        state.errorsSeen.delete(key);
      }
    });
  }

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
    const currentSessionId = getSessionId();
    let fired = sessionStorage.getItem(sessionFlag) === currentSessionId;
    const handler = () => {
      if (fired) return;
      fired = true;
      sessionStorage.setItem(sessionFlag, currentSessionId);
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
  window.legendFormTracking = {
    trackFieldError: trackCustomFieldError,
    clearFieldError: clearTrackedFieldError,
  };
  function getAttribution() {
    const attribution = resolveCurrentSessionAttribution({}, getSessionId());
    return {
      utmSource: attribution.utmSource || null,
      utmMedium: attribution.utmMedium || null,
      utmCampaign: attribution.utmCampaign || null,
      utmId: attribution.utmId || null,
      utmTerm: attribution.utmTerm || null,
      utmContent: attribution.utmContent || null,
      fbclid: attribution.fbclid || null,
      metaCampaignId: attribution.metaCampaignId || null,
      metaAdSetId: attribution.metaAdSetId || null,
      metaAdId: attribution.metaAdId || null
    };
  }

  function getFirstTouchAttribution() {
    const attribution = resolveFirstTouchAttribution();
    return {
      utmSource: attribution.utmSource || null,
      utmMedium: attribution.utmMedium || null,
      utmCampaign: attribution.utmCampaign || null,
      utmId: attribution.utmId || null,
      utmTerm: attribution.utmTerm || null,
      utmContent: attribution.utmContent || null,
      fbclid: attribution.fbclid || null,
      metaCampaignId: attribution.metaCampaignId || null,
      metaAdSetId: attribution.metaAdSetId || null,
      metaAdId: attribution.metaAdId || null
    };
  }

  window.legendTrackingIds = { getVisitorId, getSessionId, getAttribution, getFirstTouchAttribution };
})();
