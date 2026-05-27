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
  const ANALYTICS_CONFIG = window.LEGEND_ANALYTICS_CONFIG || {};
  const DEBUG_TRACKING =
    window.location.hostname === 'localhost' ||
    window.location.hostname === '127.0.0.1' ||
    new URLSearchParams(window.location.search).has('trackingDebug');

  const STORAGE_VISITOR = 'legend_visitor_id';
  const STORAGE_SESSION = 'legend_session_id';
  const STORAGE_SESSION_TS = 'legend_session_ts';
  const STORAGE_ATTR_SESSION = 'legend_attr_session';
  const STORAGE_ATTR_FIRST_TOUCH = 'legend_attr_first_touch';
  const STORAGE_EVENT_QUEUE = 'legend_tracking_event_queue_v1';
  const SESSION_TIMEOUT_MIN = 30;
  const DEBOUNCE_MS = 2000;
  const TRACKING_MAX_RETRIES = 3;
  const TRACKING_BACKOFF_MS = 800;
  const TRACKING_QUEUE_LIMIT = 80;
  const CLIENT_TRACKING_ERROR_EVENT =
    typeof ANALYTICS_CONFIG.clientTrackingErrorEvent === 'string' &&
    ANALYTICS_CONFIG.clientTrackingErrorEvent.trim()
      ? ANALYTICS_CONFIG.clientTrackingErrorEvent.trim()
      : 'client_tracking_error';
  const nativeFetch = typeof window.fetch === 'function'
    ? window.fetch.bind(window)
    : null;
  const _formTrackStateByKey = new Map();

  function debug(message, details) {
    if (!DEBUG_TRACKING) return;
    try {
      console.debug('[legend-tracking]', message, details || '');
    } catch {
      // Swallow console issues in legacy browsers.
    }
  }

  const allowedEvents = new Set(Array.isArray(ANALYTICS_CONFIG.allowedBrowserEvents)
    ? ANALYTICS_CONFIG.allowedBrowserEvents
    : []);
  const criticalEvents = new Set(Array.isArray(ANALYTICS_CONFIG.criticalBrowserEvents)
    ? ANALYTICS_CONFIG.criticalBrowserEvents
    : []);

  function safeStorageGet(storage, key) {
    try {
      return storage.getItem(key);
    } catch {
      return null;
    }
  }

  function safeStorageSet(storage, key, value) {
    try {
      storage.setItem(key, value);
    } catch {
      // ignore storage failures
    }
  }

  function safeStorageRemove(storage, key) {
    try {
      storage.removeItem(key);
    } catch {
      // ignore storage failures
    }
  }

  function safeJsonParse(raw, fallback) {
    if (!raw) return fallback;
    try {
      return JSON.parse(raw);
    } catch {
      return fallback;
    }
  }

  function sleep(ms) {
    return new Promise((resolve) => window.setTimeout(resolve, ms));
  }

  function asTrimmed(value) {
    return typeof value === 'string' ? value.trim() : '';
  }

  function clampErrorText(value) {
    const text = asTrimmed(value);
    return text.length > 280 ? text.slice(0, 280) : text;
  }

  function readQueuedEvents() {
    return safeJsonParse(safeStorageGet(window.localStorage, STORAGE_EVENT_QUEUE), []);
  }

  function writeQueuedEvents(queue) {
    if (!Array.isArray(queue) || queue.length === 0) {
      safeStorageRemove(window.localStorage, STORAGE_EVENT_QUEUE);
      return;
    }

    safeStorageSet(window.localStorage, STORAGE_EVENT_QUEUE, JSON.stringify(queue.slice(-TRACKING_QUEUE_LIMIT)));
  }

  function normalizeFetchUrl(input) {
    if (typeof input === 'string') return input;
    if (input && typeof input.url === 'string') return input.url;
    return '';
  }

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
    let v = safeStorageGet(window.localStorage, STORAGE_VISITOR);
    if (!v) {
      v = uuid();
      safeStorageSet(window.localStorage, STORAGE_VISITOR, v);
    }
    return v;
  }

  function getSessionId() {
    const now = Date.now();
    const lastTs = parseInt(safeStorageGet(window.localStorage, STORAGE_SESSION_TS) || '0', 10);
    let sid = safeStorageGet(window.localStorage, STORAGE_SESSION);
    const sessionExpired = !sid || isNaN(lastTs) || (now - lastTs) > SESSION_TIMEOUT_MIN * 60 * 1000;
    if (sessionExpired) {
      sid = uuid();
      // Prevent stale attribution from a prior session leaking into a new session.
      try {
        window.sessionStorage.removeItem(STORAGE_ATTR_SESSION);
        window.localStorage.removeItem(STORAGE_ATTR_SESSION);
      } catch { /* ignore */ }
    }
    safeStorageSet(window.localStorage, STORAGE_SESSION, sid);
    safeStorageSet(window.localStorage, STORAGE_SESSION_TS, String(now));
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


  const forensicState = {
    mouseMoveCount: 0,
    visibilityChangeCount: 0
  };

  document.addEventListener('mousemove', () => {
    forensicState.mouseMoveCount = Math.min(forensicState.mouseMoveCount + 1, 10000);
  }, { passive: true });

  function recordVisibilityForensics() {
    forensicState.visibilityChangeCount = Math.min(forensicState.visibilityChangeCount + 1, 1000);
  }


  function getClientContext() {
    const ua = navigator.userAgent || "";
    const width = window.innerWidth || document.documentElement.clientWidth || 0;
    const height = window.innerHeight || document.documentElement.clientHeight || 0;

    let deviceType = "desktop";
    if (/Mobi|Android|iPhone|iPod/i.test(ua)) deviceType = "mobile";
    else if (/iPad|Tablet/i.test(ua)) deviceType = "tablet";

    let browser = "unknown";
    if (/Edg\//i.test(ua)) browser = "edge";
    else if (/Chrome\//i.test(ua) && !/Edg\//i.test(ua)) browser = "chrome";
    else if (/Safari\//i.test(ua) && !/Chrome\//i.test(ua)) browser = "safari";
    else if (/Firefox\//i.test(ua)) browser = "firefox";

    let operatingSystem = "unknown";
    if (/Windows/i.test(ua)) operatingSystem = "windows";
    else if (/Mac OS X/i.test(ua)) operatingSystem = "macos";
    else if (/Android/i.test(ua)) operatingSystem = "android";
    else if (/iPhone|iPad|iPod/i.test(ua)) operatingSystem = "ios";
    else if (/Linux/i.test(ua)) operatingSystem = "linux";

    return {
      DeviceType: deviceType,
      Browser: browser,
      OperatingSystem: operatingSystem,
      UserAgent: ua || null,
      ViewportWidth: width || null,
      ViewportHeight: height || null,
      ScreenWidth: window.screen?.width || null,
      ScreenHeight: window.screen?.height || null,
      WebDriver: !!navigator.webdriver,
      IsHeadless: !!navigator.webdriver || /HeadlessChrome|PhantomJS|SlimerJS/i.test(ua) || !!window.callPhantom || !!window._phantom,
      MouseMoveCount: forensicState.mouseMoveCount,
      VisibilityChangeCount: forensicState.visibilityChangeCount,
      Language: navigator.language || null,
      TimeZone: Intl.DateTimeFormat().resolvedOptions().timeZone || null
    };
  }


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
      ...getClientContext(),
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
      IsInternal: detectInternalTrafficFlag(),
      FieldName: payload.FieldName || null,
      DwellMilliseconds: payload.DwellMilliseconds != null ? payload.DwellMilliseconds : null,
      EngagedMilliseconds: payload.EngagedMilliseconds != null ? payload.EngagedMilliseconds : null,
      ScrollPercent: payload.ScrollPercent != null ? payload.ScrollPercent : null,
      IsBounceCandidate: payload.IsBounceCandidate != null ? payload.IsBounceCandidate : null,
      IsExitPage: payload.IsExitPage || null
    };
  }

  async function readResponseError(response) {
    try {
      return clampErrorText(await response.text());
    } catch {
      return '';
    }
  }

  async function postBody(body) {
    if (!nativeFetch) {
      return {
        ok: false,
        statusCode: null,
        errorMessage: 'fetch_unavailable'
      };
    }

    try {
      const response = await nativeFetch(INGEST_URL, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        keepalive: true,
        body: JSON.stringify(body)
      });

      if (!response.ok) {
        return {
          ok: false,
          statusCode: response.status,
          errorMessage: await readResponseError(response)
        };
      }

      return {
        ok: true,
        statusCode: response.status,
        errorMessage: ''
      };
    } catch (error) {
      return {
        ok: false,
        statusCode: null,
        errorMessage: clampErrorText(error && error.message ? error.message : String(error || 'network_error'))
      };
    }
  }

  function queueCriticalEvent(body, failure, retryCount, queueReason) {
    if (!body || !criticalEvents.has(body.EventType)) {
      return;
    }

    const queue = readQueuedEvents();
    const existingIndex = queue.findIndex(item =>
      item &&
      item.body &&
      item.body.EventType === body.EventType &&
      item.body.SessionId === body.SessionId &&
      item.body.ClientEventId === body.ClientEventId);

    const queuedEvent = {
      queuedUtc: new Date().toISOString(),
      retryCount,
      queueReason: queueReason || 'send_failed',
      lastStatusCode: failure?.statusCode ?? null,
      lastErrorMessage: clampErrorText(failure?.errorMessage || 'tracking_send_failed'),
      body
    };

    if (existingIndex >= 0) {
      queue.splice(existingIndex, 1, queuedEvent);
    } else {
      queue.push(queuedEvent);
    }

    writeQueuedEvents(queue);
  }

  async function reportClientTrackingError(details) {
    if (!allowedEvents.has(CLIENT_TRACKING_ERROR_EVENT) || !nativeFetch) {
      return false;
    }

    const errorBody = buildBody({
      EventType: CLIENT_TRACKING_ERROR_EVENT,
      PageKey: details?.pageKey || PAGE_KEY,
      QuoteType: details?.quoteType || PAGE_QUOTE_TYPE,
      MetadataJson: JSON.stringify({
        attemptedEventName: details?.attemptedEventName || '',
        statusCode: details?.statusCode ?? null,
        errorMessage: clampErrorText(details?.errorMessage || ''),
        retryCount: details?.retryCount ?? 0,
        queueReason: details?.queueReason || '',
        route: details?.route || window.location.pathname || '',
        fetchUrl: details?.fetchUrl || '',
        method: details?.method || '',
        timestamp: new Date().toISOString(),
        sessionId: details?.sessionId || getSessionId(),
        visitorId: details?.visitorId || getVisitorId(),
        trigger: details?.trigger || 'tracking'
      })
    });

    const result = await postBody(errorBody);
    return result.ok;
  }

  // ── Async fetch (normal mid-session events) ───────────────────────────────
  async function sendEvent(payload) {
    try {
      const body = buildBody(payload);
      if (!allowedEvents.has(body.EventType)) {
        debug('blocked uncataloged event', {
          eventType: body.EventType,
          pageKey: body.PageKey
        });
        return false;
      }

      const isDiagnosticEvent = body.EventType === CLIENT_TRACKING_ERROR_EVENT;
      if (!isDiagnosticEvent) {
        updateTrackedFormStateFromEvent(body);
      }
      if (body.EventType === 'form_start' ||
          body.EventType === 'form_submit_attempt' ||
          body.EventType === 'lead_form_submit_success' ||
          body.EventType === 'lead_form_submit_failed') {
        body.FormKey = body.FormKey || payload.FormKey;
      }
      if (body.EventType === 'lead_form_submit_success' && body.FormKey) {
        const state = _formTrackStateByKey.get(body.FormKey);
        if (state) {
          state.submitted = true;
          state.submitAttempted = true;
        }
      }
      debug('sendEvent', {
        eventType: body.EventType,
        formKey: body.FormKey,
        pageKey: body.PageKey,
        quoteType: body.QuoteType,
        submitOutcome: body.SubmitOutcome
      });

      const maxAttempts = criticalEvents.has(body.EventType) ? TRACKING_MAX_RETRIES : 1;
      let attempt = 0;
      let lastFailure = null;

      while (attempt < maxAttempts) {
        attempt += 1;
        const result = await postBody(body);
        if (result.ok) {
          if (
            body.EventType === 'thank_you_view' ||
            body.EventType === 'life_step2_submit_success' ||
            body.EventType === 'life_contact_first_submit_success' ||
            body.EventType === 'lead_form_submit_success'
          ) {
            void flushQueuedEvents('successful_event');
          }

          return true;
        }

        lastFailure = result;
        debug('sendEvent failed', {
          eventType: body.EventType,
          attempt,
          statusCode: result.statusCode,
          errorMessage: result.errorMessage
        });

        if (attempt < maxAttempts) {
          await sleep(TRACKING_BACKOFF_MS * attempt);
        }
      }

      if (criticalEvents.has(body.EventType)) {
        queueCriticalEvent(body, lastFailure, attempt, 'send_failed');
      }

      if (!isDiagnosticEvent) {
        void reportClientTrackingError({
          attemptedEventName: body.EventType,
          pageKey: body.PageKey,
          quoteType: body.QuoteType,
          statusCode: lastFailure?.statusCode ?? null,
          errorMessage: lastFailure?.errorMessage || 'tracking_send_failed',
          retryCount: attempt,
          queueReason: criticalEvents.has(body.EventType) ? 'queued_critical_event' : 'send_failed',
          route: body.Path,
          sessionId: body.SessionId,
          visitorId: body.VisitorId,
          trigger: 'send_event'
        });
      }

      return false;
    } catch (error) {
      const errorMessage = clampErrorText(error && error.message ? error.message : String(error || 'tracking_exception'));
      void reportClientTrackingError({
        attemptedEventName: payload?.EventType || '',
        pageKey: payload?.PageKey || PAGE_KEY,
        quoteType: payload?.QuoteType || PAGE_QUOTE_TYPE,
        errorMessage,
        retryCount: 0,
        route: window.location.pathname,
        trigger: 'send_event_exception'
      });
      return false;
    }
  }

  // ── Beacon send (page-exit and form-abandon — survives navigation/close) ──
  function beaconSend(body) {
    debug('beaconSend', {
      eventType: body?.EventType,
      formKey: body?.FormKey,
      pageKey: body?.PageKey,
      quoteType: body?.QuoteType
    });
    const json = JSON.stringify(body);
    const blob = new Blob([json], { type: 'application/json' });
    if (navigator.sendBeacon && navigator.sendBeacon(INGEST_URL, blob)) return true;
    // Sync XHR fallback for Meta in-app / older WebViews
    try {
      const xhr = new XMLHttpRequest();
      xhr.open('POST', INGEST_URL, false);
      xhr.setRequestHeader('Content-Type', 'application/json');
      xhr.send(json);
      return xhr.status >= 200 && xhr.status < 400;
    } catch {
      return false;
    }
  }

  async function flushQueuedEvents(reason, options = {}) {
    const queue = readQueuedEvents();
    if (!Array.isArray(queue) || queue.length === 0) {
      return false;
    }

    const keep = [];
    const maxItems = Number.isFinite(options.maxItems) ? options.maxItems : queue.length;

    for (let index = 0; index < queue.length; index += 1) {
      const queued = queue[index];
      if (!queued || !queued.body) {
        continue;
      }

      if (index >= maxItems) {
        keep.push(queued);
        continue;
      }

      if (options.useBeacon) {
        const sent = beaconSend(queued.body);
        if (!sent) {
          keep.push({
            ...queued,
            retryCount: Number(queued.retryCount || 0) + 1,
            queueReason: reason
          });
        }
        continue;
      }

      const result = await postBody(queued.body);
      if (!result.ok) {
        keep.push({
          ...queued,
          retryCount: Number(queued.retryCount || 0) + 1,
          lastStatusCode: result.statusCode ?? null,
          lastErrorMessage: result.errorMessage || queued.lastErrorMessage || '',
          queueReason: reason
        });
      }
    }

    writeQueuedEvents(keep);
    return keep.length !== queue.length;
  }

  const debounceMap = new Map();
  function shouldFire(key) {
    const now = Date.now();
    const last = debounceMap.get(key) || 0;
    if (now - last < DEBOUNCE_MS) return false;
    debounceMap.set(key, now);
    return true;
  }

  function resolveTrackedFormKey(payload) {
    const explicitFormKey = payload?.FormKey || null;
    if (explicitFormKey && _formTrackStateByKey.has(explicitFormKey)) {
      return explicitFormKey;
    }

    const pageKey = payload?.PageKey || PAGE_KEY;
    if (pageKey && _formTrackStateByKey.has(pageKey)) {
      return pageKey;
    }

    if (_formTrackStateByKey.size === 1) {
      return _formTrackStateByKey.keys().next().value || null;
    }

    return explicitFormKey || null;
  }

  function updateTrackedFormStateFromEvent(payload) {
    const formKey = resolveTrackedFormKey(payload);
    if (!formKey) return;

    const state = _formTrackStateByKey.get(formKey);
    if (!state) return;

    const eventType = payload?.EventType || '';
    if (!eventType) return;

    switch (eventType) {
      case 'form_start':
      case 'lead_form_start':
      case 'life_general_form_start':
      case 'life_term_form_start':
      case 'life_whole_form_start':
      case 'life_finalexpense_form_start':
      case 'life_mp_form_start':
      case 'life_iul_form_start':
      case 'life_step1_protecting_select':
      case 'life_step1_goal_select':
      case 'life_step1_coverage_select':
      case 'life_step1_tobacco_select':
      case 'first_question_view':
      case 'first_question_answered':
      case 'step1_age_entered':
      case 'form_field_focus':
      case 'form_field_complete':
      case 'form_field_error':
        transitionFormState(state, 'started', eventType);
        break;
      case 'life_step1_age_continue':
      case 'life_processing_bridge_complete':
      case 'mini_results_view':
      case 'recommendation_generated':
      case 'estimate_results_viewed':
      case 'quote_step_complete':
        transitionFormState(state, 'progressed', eventType);
        break;
      case 'life_step2_view':
      case 'contact_step_view':
      case 'quote_contact_step_view':
      case 'estimate_contact_continue':
        transitionFormState(state, 'contact_viewed', eventType);
        break;
      case 'form_submit_attempt':
      case 'life_step2_submit_attempt':
        state.submitAttempted = true;
        state.submitAttemptedAt = state.submitAttemptedAt || Date.now();
        break;
      case 'lead_form_submit_success':
        transitionFormState(state, 'submitted', 'lead_form_submit_success');
        break;
      case 'lead_form_submit_failed':
      case 'lead_form_submit_failure':
      case 'submit_failure':
        state.submitAttempted = true;
        state.submitAttemptedAt = state.submitAttemptedAt || Date.now();
        break;
      default:
        break;
    }
  }

  // ── Behavior Intelligence instrumentation ─────────────────────────────────
  const _pageStart = Date.now();
  let _maxScroll = 0;
  let _activeMs = 0;
  let _activeStart = document.visibilityState === 'visible' ? Date.now() : null;
  let _exitFired = false;

let _formAbandonCallbacks = [];

function trackCustomFieldError(formKey, fieldName, errorType, offerKey) {
  const normalizedFormKey = (formKey || '').trim();
  const normalizedField = (fieldName || '').trim();
  const normalizedErrorType = (errorType || '').trim();

  if (!normalizedFormKey || !normalizedField) return;

  const state = ensureFormTrackState(normalizedFormKey);
  const errorKey = `${normalizedField}:${normalizedErrorType || 'unknown'}`;

  if (state && state.errorsSeen.has(errorKey)) return;
  if (state) state.errorsSeen.add(errorKey);

  sendEvent({
    EventType: 'form_field_error',
    FormKey: normalizedFormKey,
    FieldName: normalizedField,
    MetadataJson: JSON.stringify({
      errorType: normalizedErrorType || null,
      offerKey: offerKey || null
    })
  });

  debug('form_field_error tracked', {
    formKey: normalizedFormKey,
    fieldName: normalizedField,
    errorType: normalizedErrorType,
    offerKey
  });
}

  let _scrollTicking = false;
  const _scrollThresholds = [25, 50, 75, 90, 100];
  const _scrollFired = new Set();

  function getScrollPct() {
    const d = document.documentElement;
    const body = document.body;
    const scrollTop = window.scrollY || d.scrollTop || body?.scrollTop || 0;
    const scrollHeight = Math.max(
      d.scrollHeight || 0,
      body?.scrollHeight || 0,
      d.offsetHeight || 0,
      body?.offsetHeight || 0
    );
    const viewport = window.innerHeight || d.clientHeight || 0;
    const scrollable = scrollHeight - viewport;

    if (scrollable <= 0) return 100;
    return Math.max(0, Math.min(100, Math.round((scrollTop / scrollable) * 100)));
  }

  function trackScrollDepth() {
    const pct = Math.max(_maxScroll, getScrollPct());
    _maxScroll = Math.max(_maxScroll, pct);

    _scrollThresholds.forEach(function (milestone) {
      if (_scrollFired.has(milestone) || pct < milestone) {
        return;
      }

      _scrollFired.add(milestone);

      sendEvent({
        EventType: 'scroll_depth_' + milestone,
        ScrollPercent: milestone,
        DwellMilliseconds: Date.now() - _pageStart,
        EngagedMilliseconds: _activeMs + (_activeStart ? Date.now() - _activeStart : 0)
      });

      debug('scroll_depth tracked', {
        pageKey: PAGE_KEY,
        milestone,
        pct
      });
    });
  }

  window.addEventListener('scroll', function () {
    if (_scrollTicking) return;

    _scrollTicking = true;
    requestAnimationFrame(function () {
      trackScrollDepth();
      _scrollTicking = false;
    });
  }, { passive: true });

  window.addEventListener('resize', function () {
    trackScrollDepth();
  }, { passive: true });

  // Capture initial non-scrollable / already-short pages without waiting for manual scroll.
  setTimeout(trackScrollDepth, 750);

  // Engagement checkpoints
  [5000, 10000, 15000, 30000, 60000].forEach(function (ms) {
    setTimeout(function () {
      if (document.visibilityState === 'visible') {
        sendEvent({
          EventType: 'page_engaged_' + (ms / 1000) + 's',
          EngagedMilliseconds: _activeMs + (Date.now() - (_activeStart || Date.now())),
          DwellMilliseconds: Date.now() - _pageStart,
          ScrollPercent: Math.max(_maxScroll, getScrollPct())
        });
      }
    }, ms);
  });

  function firePageExit(reason, event) {
    if (_exitFired) return false;
    _exitFired = true;

    if (_activeStart !== null) {
      _activeMs += Date.now() - _activeStart;
      _activeStart = null;
    }

    trackScrollDepth();

    const dwell = Date.now() - _pageStart;
    const scrollPct = Math.max(_maxScroll, getScrollPct());

    sendEvent({
      EventType: 'page_exit',
      IsExitPage: true,
      IsBounceCandidate: dwell < 10000,
      DwellMilliseconds: dwell,
      EngagedMilliseconds: _activeMs,
      ScrollPercent: scrollPct,
      MetadataJson: JSON.stringify({
        reason: reason || 'unknown',
        persisted: !!event?.persisted
      })
    });

    debug('page_exit tracked', {
      pageKey: PAGE_KEY,
      reason,
      dwell,
      activeMs: _activeMs,
      scrollPct
    });

    return true;
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

  function fireExitSignals(lifecycleSource, event) {
    if (event && event.persisted) {
      debug('skipping lifecycle exit signals for bfcache', {
        lifecycleSource,
        persisted: true
      });
      return;
    }

    debug('fireExitSignals', { lifecycleSource });

    firePageExit(lifecycleSource || 'pagehide', event);

    _formAbandonCallbacks.forEach(function (cb) {
      try {
        cb(lifecycleSource || 'pagehide');
      } catch {
        /* swallow */
      }
    });
  }


  function shouldSkipFetchDiagnostics(url) {
    if (!url) return true;
    if (url.includes(INGEST_URL) || url.includes('/ThankYou/meta-browser-ack')) return true;

    try {
      const parsed = new URL(url, window.location.origin);
      if (parsed.origin !== window.location.origin) return true;
    } catch (_) {
      return true;
    }

    return false;
  }

  function installGlobalDiagnostics() {
    window.addEventListener('error', function (event) {
      const target = event && event.target;
      const sourceUrl =
        target && typeof target.src === 'string' && target.src
          ? target.src
          : target && typeof target.href === 'string' && target.href
            ? target.href
            : '';

      // Resource load failures like avatar/image/CDN misses are not analytics-pipeline failures.
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
      });
    }, true);

    window.addEventListener('unhandledrejection', function (event) {
      const reason = event && event.reason;
      const message =
        reason && reason.message
          ? reason.message
          : typeof reason === 'string'
            ? reason
            : 'unhandled_promise_rejection';

      void reportClientTrackingError({
        attemptedEventName: 'unhandled_rejection',
        pageKey: PAGE_KEY,
        quoteType: PAGE_QUOTE_TYPE,
        errorMessage: clampErrorText(message),
        route: window.location.pathname,
        trigger: 'window.onunhandledrejection'
      });
    });

    if (!nativeFetch) {
      return;
    }

    window.fetch = async function trackedFetch(input, init) {
      const url = normalizeFetchUrl(input);
      try {
        const response = await nativeFetch(input, init);
        if (!response.ok && !shouldSkipFetchDiagnostics(url)) {
          void reportClientTrackingError({
            attemptedEventName: 'fetch_non_ok',
            pageKey: PAGE_KEY,
            quoteType: PAGE_QUOTE_TYPE,
            statusCode: response.status,
            errorMessage: clampErrorText(response.statusText || 'fetch_non_ok'),
            route: window.location.pathname,
            fetchUrl: url,
            method: asTrimmed(init?.method || 'GET'),
            trigger: 'fetch_response'
          });
        }

        return response;
      } catch (error) {
        if (!shouldSkipFetchDiagnostics(url)) {
          void reportClientTrackingError({
            attemptedEventName: 'fetch_failed',
            pageKey: PAGE_KEY,
            quoteType: PAGE_QUOTE_TYPE,
            errorMessage: clampErrorText(error && error.message ? error.message : String(error || 'fetch_failed')),
            route: window.location.pathname,
            fetchUrl: url,
            method: asTrimmed(init?.method || 'GET'),
            trigger: 'fetch_exception'
          });
        }

        throw error;
      }
    };
  }

  // ── Visibility/page lifecycle ──────────────────────────────────────────────
  document.addEventListener('visibilitychange', function () {
    recordVisibilityForensics();

    if (document.visibilityState === 'hidden') {
      if (_activeStart !== null) {
        _activeMs += Date.now() - _activeStart;
        _activeStart = null;
      }

      trackScrollDepth();

      sendEvent({
        EventType: 'page_visibility_hidden',
        DwellMilliseconds: Date.now() - _pageStart,
        EngagedMilliseconds: _activeMs,
        ScrollPercent: Math.max(_maxScroll, getScrollPct())
      });

      debug('visibilitychange:hidden', {
        pageKey: PAGE_KEY,
        activeMs: _activeMs
      });

      void flushQueuedEvents('visibility_hidden', { useBeacon: true, maxItems: 10 });
    } else {
      if (_activeStart === null) {
        _activeStart = Date.now();
      }

      sendEvent({
        EventType: 'page_visibility_return',
        DwellMilliseconds: Date.now() - _pageStart,
        EngagedMilliseconds: _activeMs
      });

      debug('visibilitychange:visible', {
        pageKey: PAGE_KEY,
        activeMs: _activeMs
      });

      void flushQueuedEvents('visibility_visible');
    }
  });

  window.addEventListener('pagehide', function (event) {
    fireExitSignals('pagehide', event);
    void flushQueuedEvents('pagehide', { useBeacon: true, maxItems: 15 });
  });

  window.addEventListener('beforeunload', function (event) {
    fireExitSignals('beforeunload', event);
    void flushQueuedEvents('beforeunload', { useBeacon: true, maxItems: 15 });
  });


  // ── Standard tracking ─────────────────────────────────────────────────────


  function isAnalyticsRouteBlocked() {
    const path = (window.location && window.location.pathname ? window.location.pathname : '').toLowerCase();
    return path.startsWith('/websiteanalytics') || path.startsWith('/api/analytics');
  }

  function trackPageView() {
    if (isAnalyticsRouteBlocked()) return;
    sendEvent({
      EventType: 'page_view',
      MetadataJson: buildPageContextMetadata()
    });

    if (PAGE_CATEGORY === 'quote') {
      sendEvent({
        EventType: 'quote_landing_view',
        MetadataJson: buildPageContextMetadata()
      });
    }

    if (PAGE_KEY && PAGE_KEY.toLowerCase().includes('thank_you')) {
      sendEvent({
        EventType: 'thank_you_view',
        MetadataJson: buildPageContextMetadata()
      });
      void flushQueuedEvents('thank_you_load');
    }
  }

  function normalizePrimaryCtaPlacement(value) {
    const normalized = typeof value === 'string' ? value.trim().toLowerCase() : '';
    return normalized || 'hero';
  }

  function resolvePrimaryCtaElementKey(target, explicitElementKey) {
    const directKey = typeof explicitElementKey === 'string' ? explicitElementKey.trim() : '';
    if (directKey) return directKey;

    if (!(target instanceof HTMLElement)) return 'primary_cta';

    return target.getAttribute('data-cta') ||
      target.getAttribute('data-element-key') ||
      target.id ||
      'primary_cta';
  }

  function resolvePrimaryCtaLabel(target, explicitLabel) {
    const directLabel = typeof explicitLabel === 'string' ? explicitLabel.trim() : '';
    if (directLabel) return directLabel;

    if (!(target instanceof HTMLElement)) return null;

    const text = target.textContent?.replace(/\s+/g, ' ')?.trim();
    return text || null;
  }

  function isPrimaryCtaActuallyVisible(target) {
    if (!(target instanceof HTMLElement)) {
      return false;
    }

    if (!target.isConnected) {
      return false;
    }

    const style = window.getComputedStyle(target);
    if (style.display === 'none' || style.visibility === 'hidden' || Number(style.opacity || 1) <= 0.05) {
      return false;
    }

    const rect = target.getBoundingClientRect();
    return rect.width > 0 &&
      rect.height > 0 &&
      rect.bottom >= 0 &&
      rect.top <= window.innerHeight &&
      rect.right >= 0 &&
      rect.left <= window.innerWidth;
  }

  function markPrimaryCtaSeen(target) {
    if (target instanceof HTMLElement) {
      target.dataset.legendPrimaryCtaSeen = 'true';
    }
  }

  function wasPrimaryCtaSeen(target) {
    return target instanceof HTMLElement && target.dataset.legendPrimaryCtaSeen === 'true';
  }

  function trackPrimaryCtaSeen(target, options = {}) {
    const elementKey = resolvePrimaryCtaElementKey(target, options.elementKey);
    const pageKey = options.pageKey || PAGE_KEY || '';
    const sessionId = getSessionId();
    const sessionFlag = `primary_cta_seen_${sessionId}_${pageKey}_${elementKey}`;

    if (wasPrimaryCtaSeen(target) || safeStorageGet(window.sessionStorage, sessionFlag) === sessionId) {
      markPrimaryCtaSeen(target);
      return false;
    }

    safeStorageSet(window.sessionStorage, sessionFlag, sessionId);
    markPrimaryCtaSeen(target);

    const placement = normalizePrimaryCtaPlacement(
      options.placement ||
      (target instanceof HTMLElement ? target.getAttribute('data-primary-cta') || target.getAttribute('data-placement') : null)
    );
    const label = resolvePrimaryCtaLabel(target, options.label);
    const rect = target instanceof HTMLElement ? target.getBoundingClientRect() : null;
    const metadata = {
      placement,
      ctaId: elementKey,
      ctaText: label || '',
      source: options.source || 'shared_tracker',
      viewportWidth: window.innerWidth,
      viewportHeight: window.innerHeight,
      ...(rect ? {
        ctaTop: Math.round(rect.top),
        ctaBottom: Math.round(rect.bottom)
      } : {}),
      ...(options.metadata && typeof options.metadata === 'object' ? options.metadata : {})
    };

    sendEvent({
      EventType: 'primary_cta_seen',
      ElementKey: elementKey,
      ButtonLabel: label,
      MetadataJson: JSON.stringify(metadata)
    });

    return true;
  }

  const _trackedQuoteEntryEngagedFlags = new Set();

  function buildQuoteEntryEngagedSessionFlag(pageKey) {
    const currentSessionId = getSessionId();
    const normalizedPageKey = asTrimmed(pageKey || PAGE_KEY || 'quote');
    return {
      sessionId: currentSessionId,
      pageKey: normalizedPageKey,
      storageKey: `quote_entry_engaged_${currentSessionId}_${normalizedPageKey || 'quote'}`
    };
  }

  function trackQuoteEntryEngagedOnce(options = {}) {
    if (isAnalyticsRouteBlocked() || PAGE_CATEGORY !== 'quote' || !allowedEvents.has('quote_entry_engaged')) {
      return false;
    }

    const formKey = asTrimmed(options.formKey || resolveTrackedFormKey({ FormKey: options.formKey, PageKey: options.pageKey || PAGE_KEY }) || '');
    const { sessionId, pageKey, storageKey } = buildQuoteEntryEngagedSessionFlag(options.pageKey || PAGE_KEY || formKey);

    if (_trackedQuoteEntryEngagedFlags.has(storageKey) || safeStorageGet(window.sessionStorage, storageKey) === sessionId) {
      _trackedQuoteEntryEngagedFlags.add(storageKey);
      return false;
    }

    _trackedQuoteEntryEngagedFlags.add(storageKey);
    safeStorageSet(window.sessionStorage, storageKey, sessionId);

    const metadata = {
      source: asTrimmed(options.source || 'shared_tracker'),
      pageCategory: PAGE_CATEGORY || '',
      pageKey: pageKey || '',
      quoteType: asTrimmed(options.quoteType || PAGE_QUOTE_TYPE || ''),
      formKey,
      elementKey: asTrimmed(options.elementKey || ''),
      fieldName: asTrimmed(options.fieldName || ''),
      triggerEventType: asTrimmed(options.triggerEventType || '')
    };

    if (options.metadata && typeof options.metadata === 'object') {
      Object.assign(metadata, options.metadata);
    }

    sendEvent({
      EventType: 'quote_entry_engaged',
      PageKey: pageKey || PAGE_KEY || null,
      FormKey: formKey || null,
      ElementKey: metadata.elementKey || null,
      MetadataJson: JSON.stringify(metadata)
    });

    return true;
  }

  function maybeTrackQuoteEntryEngagedFromPayload(payload) {
    if (!payload || PAGE_CATEGORY !== 'quote') {
      return false;
    }

    const eventType = asTrimmed(payload.EventType);
    if (!eventType || eventType === 'quote_entry_engaged') {
      return false;
    }

    switch (eventType) {
      case 'form_start':
      case 'lead_form_start':
      case 'quote_step_complete':
      case 'contact_step_view':
      case 'contact_step_viewed':
      case 'quote_contact_step_view':
        break;
      default:
        return false;
    }

    return trackQuoteEntryEngagedOnce({
      source: eventType,
      triggerEventType: eventType,
      pageKey: payload.PageKey || PAGE_KEY,
      formKey: payload.FormKey || resolveTrackedFormKey(payload) || '',
      elementKey: payload.ElementKey || '',
      metadata: {
        metadataJsonPresent: typeof payload.MetadataJson === 'string' && payload.MetadataJson.length > 0
      }
    });
  }

  function installQuotePrimaryCtaTracking() {
    if (isAnalyticsRouteBlocked() || PAGE_CATEGORY !== 'quote') {
      return;
    }

    const targets = Array.from(document.querySelectorAll('[data-primary-cta]'));
    if (!targets.length) {
      return;
    }

    const trackIfVisible = (target, source) => {
      if (!isPrimaryCtaActuallyVisible(target)) {
        return false;
      }

      return trackPrimaryCtaSeen(target, { source });
    };

    const runCheck = (source) => {
      window.requestAnimationFrame(() => {
        targets.some(target => trackIfVisible(target, source));
      });
    };

    runCheck('initial');
    window.setTimeout(() => runCheck('delayed_150ms'), 150);
    window.setTimeout(() => runCheck('delayed_600ms'), 600);
    window.setTimeout(() => runCheck('delayed_1200ms'), 1200);

    targets.forEach((target) => {
      target.addEventListener('click', () => {
        trackPrimaryCtaSeen(target, { source: 'cta_click_before_start' });
      });
    });

    if (!('IntersectionObserver' in window)) {
      window.setTimeout(() => runCheck('fallback_no_intersection_observer'), 250);
      return;
    }

    const observer = new IntersectionObserver((entries) => {
      const visibleEntry = entries.find((entry) => entry.isIntersecting && isPrimaryCtaActuallyVisible(entry.target));
      if (!visibleEntry) {
        return;
      }

      if (trackPrimaryCtaSeen(visibleEntry.target, {
        source: 'intersection_observer',
        metadata: {
          intersectionRatio: Number(visibleEntry.intersectionRatio || 0).toFixed(2)
        }
      })) {
        observer.disconnect();
      }
    }, {
      root: null,
      rootMargin: '160px 0px 160px 0px',
      threshold: [0, 0.01, 0.1, 0.25]
    });

    targets.forEach((target) => observer.observe(target));

    const passiveCheck = () => {
      if (targets.some(target => trackIfVisible(target, 'passive_visibility_check'))) {
        window.removeEventListener('scroll', passiveCheck);
        window.removeEventListener('resize', passiveCheck);
        window.removeEventListener('pageshow', passiveCheck);
        window.removeEventListener('load', passiveCheck);
      }
    };

    window.addEventListener('scroll', passiveCheck, { passive: true });
    window.addEventListener('resize', passiveCheck);
    window.addEventListener('pageshow', passiveCheck);
    window.addEventListener('load', passiveCheck);
  }

  function wireClick(selector, elementKey, eventType) {
    document.querySelectorAll(selector).forEach(el => {
      el.addEventListener('click', () => {
        const key = `${eventType}:${elementKey}`;
        if (!shouldFire(key)) return;

        if (PAGE_CATEGORY === 'quote' &&
            el instanceof HTMLElement &&
            el.hasAttribute('data-primary-cta')) {
          trackQuoteEntryEngagedOnce({
            source: 'primary_cta_click',
            triggerEventType: eventType,
            pageKey: PAGE_KEY,
            formKey: resolveTrackedFormKey({ FormKey: null, PageKey: PAGE_KEY }) || '',
            elementKey
          });
        }

        sendEvent({
          EventType: eventType,
          ElementKey: elementKey,
          ButtonLabel: el.textContent?.trim() || null
        });

        if (PAGE_CATEGORY === 'quote' && allowedEvents.has('quote_cta_click')) {
          sendEvent({
            EventType: 'quote_cta_click',
            ElementKey: elementKey,
            ButtonLabel: el.textContent?.trim() || null
          });
        }
      });
    });
  }

  function wireFormStart(selector, formKey) {
    const form = document.querySelector(selector);
    if (!form) return;
    const handler = () => {
      fireTrackedFormStartOnce(formKey);
    };
    form.addEventListener('focusin', handler);
    form.addEventListener('change', handler);
  }

  const _trackedFormStartFlags = new Set();

  function fireTrackedFormStartOnce(formKey) {
    if (!formKey) return false;
    const currentSessionId = getSessionId();
    const sessionFlag = `form_started_${currentSessionId}_${window.location.pathname}_${formKey}`;

    if (_trackedFormStartFlags.has(sessionFlag)) {
      return false;
    }

    if (safeStorageGet(window.sessionStorage, sessionFlag) === currentSessionId) {
      _trackedFormStartFlags.add(sessionFlag);
      return false;
    }

    _trackedFormStartFlags.add(sessionFlag);
    safeStorageSet(window.sessionStorage, sessionFlag, currentSessionId);
    trackQuoteEntryEngagedOnce({
      source: 'form_first_focus',
      triggerEventType: 'form_start',
      pageKey: PAGE_KEY || formKey,
      formKey
    });
    debug('form_start fired', {
      formKey,
      pageKey: PAGE_KEY
    });
    sendEvent({ EventType: 'form_start', FormKey: formKey });
    if (PAGE_CATEGORY === 'quote' && allowedEvents.has('lead_form_start')) {
      sendEvent({ EventType: 'lead_form_start', FormKey: formKey });
    }

    return true;
  }

  installGlobalDiagnostics();
  void flushQueuedEvents('page_load');
  trackPageView();
  installQuotePrimaryCtaTracking();

  wireClick('[data-cta="hero_start_assessment"]',    'hero_start_assessment',    'cta_click');
  wireClick('[data-cta="hero_book_call"]',           'hero_book_call',           'cta_click');
  wireClick('[data-cta="hero_start_quote"]',         'hero_start_quote',         'quote_click');
  wireClick('[data-cta="life_funnel_start"]',        'life_funnel_start',        'cta_click');
  wireClick('[data-cta="life_see_estimate"]',        'life_see_estimate',        'cta_click');
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


  // Expose existing tracking helpers for legacy/on-page scripts and diagnostics.
  window._formAbandonCallbacks = window._formAbandonCallbacks || _formAbandonCallbacks;
  window.trackCustomFieldError = window.trackCustomFieldError || trackCustomFieldError;

  window.legendTrack = (payload) => {
    maybeTrackQuoteEntryEngagedFromPayload(payload);
    return sendEvent(payload);
  };

  // Official public analytics API.
  // Page scripts should use this boundary instead of calling internal tracking helpers directly.
  window.LegendAnalytics = {
    ...(window.LegendAnalytics || {}),
    track(payload) {
      return window.legendTrack(payload);
    },
    trackEvent(eventType, metadata = {}, context = {}) {
      return window.legendTrack({
        EventType: eventType,
        ...context,
        MetadataJson: JSON.stringify(metadata || {})
      });
    },
    ids: {
      getVisitorId,
      getSessionId,
      getAttribution,
      getFirstTouchAttribution
    }
  };
  window.legendFormTracking = {
    trackFieldError: trackCustomFieldError,
    clearFieldError: clearTrackedFieldError,
    trackStart: fireTrackedFormStartOnce,
  };
  window.legendQuoteTracking = {
    ...(window.legendQuoteTracking || {}),
    trackPrimaryCtaSeen,
    trackQuoteEntryEngagedOnce,
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
