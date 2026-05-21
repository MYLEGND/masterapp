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
            body.EventType === 'lead_form_submit_success' ||
            (body.EventType === 'form_submit' && String(body.SubmitOutcome || '').toLowerCase() === 'success')
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
        transitionFormState(state, 'progressed', eventType);
        break;
      case 'life_step2_view':
      case 'estimate_contact_continue':
        transitionFormState(state, 'contact_viewed', eventType);
        break;
      case 'form_submit_attempt':
      case 'life_step2_submit_attempt':
        state.submitAttempted = true;
        state.submitAttemptedAt = state.submitAttemptedAt || Date.now();
        break;
      case 'form_submit':
        if (typeof payload.SubmitOutcome === 'string' &&
            payload.SubmitOutcome.toLowerCase() === 'success') {
          transitionFormState(state, 'submitted', 'form_submit_success');
        }
        break;
      case 'lead_form_submit_success':
        transitionFormState(state, 'submitted', 'lead_form_submit_success');
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
  [5000, 10000, 15000, 30000, 60000].forEach(function (ms) {
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
    debug('page_exit fired', {
      pageKey: PAGE_KEY,
      dwell,
      engagedMs: _activeMs,
      scrollPct
    });
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

  // Registry of active form abandon callbacks — fired during terminal page lifecycle.
  const _formAbandonCallbacks = new Map();

  function wireFormTracking(formEl) {
    if (formEl.dataset.legendTrackingBound === 'true') {
      return;
    }

    const formKey = formEl.dataset.formKey || '';
    const quoteType = formQuoteType(formKey);
    if (!formKey) return;

    formEl.dataset.legendTrackingBound = 'true';
    const state = createFormTrackState(formKey, quoteType);
    _formTrackStateByKey.set(formKey, state);
    formEl.dataset.legendTrackingInstanceId = state.instanceId;

    function markStarted() {
      transitionFormState(state, 'started', 'field_interaction');
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
      markStarted();
      state.submitAttempted = true;
      state.submitAttemptedAt = state.submitAttemptedAt || Date.now();
      debug('submit attempt', {
        formKey,
        instanceId: state.instanceId,
        validationPassed: !!validationPassed,
        invalidCount: invalidCount || 0
      });
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
      state.submitAttemptedAt = state.submitAttemptedAt || Date.now();
      transitionFormState(state, 'submitted', 'ajax_submit_success');
    };

    // Native submits (non-AJAX forms) should never be counted as abandon.
    // We treat a valid submit dispatch as terminal for abandonment purposes.
    formEl.addEventListener('submit', () => {
      if (typeof formEl.checkValidity === 'function' && !formEl.checkValidity()) return;
      state.submitAttempted = true;
      state.submitted = true;
      state.submitAttemptedAt = state.submitAttemptedAt || Date.now();
      transitionFormState(state, 'submitted', 'native_submit_dispatch');
    }, true);

    // Register abandon callback — fired during terminal page lifecycle events.
    _formAbandonCallbacks.set(state.instanceId, function (lifecycleSource) {
      if (!state.started || state.submitted || state.abandonFired) return;

      const abandonStage = state.currentStage || 'started';
      const abandonAt = Date.now();
      state.abandonFired = true;
      state.lastLifecycleSignalAt = abandonAt;
      state.lastLifecycleSignalSource = lifecycleSource || 'pagehide';
      transitionFormState(state, 'abandoned', `terminal:${lifecycleSource || 'pagehide'}`, {
        abandonStage
      });

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
          formInstanceId: state.instanceId,
          abandonStage,
          lifecycleSource: lifecycleSource || 'pagehide',
          startedAt: state.startedAt ? new Date(state.startedAt).toISOString() : null,
          progressedAt: state.progressedAt ? new Date(state.progressedAt).toISOString() : null,
          contactViewedAt: state.contactViewedAt ? new Date(state.contactViewedAt).toISOString() : null,
          submitAttemptedAt: state.submitAttemptedAt ? new Date(state.submitAttemptedAt).toISOString() : null,
          abandonedAt: new Date(abandonAt).toISOString(),
          stateTransition: state.currentStage,
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

    state = createFormTrackState(formKey, formQuoteType(formKey));
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

  function fireExitSignals(lifecycleSource, event) {
    if (event && event.persisted) {
      debug('skipping lifecycle exit signals for bfcache', {
        lifecycleSource,
        persisted: true
      });
      return;
    }

    debug('fireExitSignals', { lifecycleSource });
    firePageExit();
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
    return url.includes(INGEST_URL) || url.includes('/ThankYou/meta-browser-ack');
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
      debug('visibilitychange:hidden', {
        pageKey: PAGE_KEY,
        activeMs: _activeMs
      });
      void flushQueuedEvents('visibility_hidden');
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
  window.addEventListener('pagehide', (event) => {
    fireExitSignals('pagehide', event);
    void flushQueuedEvents('pagehide', { useBeacon: true, maxItems: 5 });
  });

  // ── Standard tracking ─────────────────────────────────────────────────────

  function trackPageView() {
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
    const sessionFlag = `form_started_${getSessionId()}_${window.location.pathname}_${formKey}`;
    const currentSessionId = getSessionId();
    let fired = safeStorageGet(window.sessionStorage, sessionFlag) === currentSessionId;
    const handler = () => {
      if (fired) return;
      fired = true;
      safeStorageSet(window.sessionStorage, sessionFlag, currentSessionId);
      debug('form_start fired', {
        formKey,
        pageKey: PAGE_KEY
      });
      sendEvent({ EventType: 'form_start', FormKey: formKey });
    };
    form.addEventListener('focusin', handler, { once: true });
    form.addEventListener('change', handler, { once: true });
  }

  installGlobalDiagnostics();
  void flushQueuedEvents('page_load');
  trackPageView();

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
