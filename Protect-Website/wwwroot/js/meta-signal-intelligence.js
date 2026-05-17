(() => {
  const STORAGE_VISITOR = 'legend_visitor_id';
  const STORAGE_SESSION = 'legend_session_id';
  const STORAGE_SESSION_TS = 'legend_session_ts';
  const STORAGE_ATTR_SESSION = 'legend_attr_session';
  const SESSION_TIMEOUT_MIN = 30;
  const MEANINGFUL_SCROLL_THRESHOLD = 35;
  const RAPID_BOUNCE_MS = 3000;

  const DEFAULT_WEIGHTS = Object.freeze({
    LandingViewed: 5,
    Stay5Seconds: 5,
    Stay15Seconds: 10,
    MeaningfulScroll: 5,
    FirstQuestionAnswered: 15,
    Step1Completed: 20,
    Step2Completed: 25,
    RecommendationViewed: 30,
    ContactStepReached: 35,
    ContactInputStarted: 20,
    PhoneCompleted: 25,
    RequiredContactCompleted: 35,
    SubmitAttempted: 40,
    SuccessfulLeadSubmitted: 100,
    ProtectingJustMe: 6,
    ProtectingSpouseOrPartner: 12,
    ProtectingChildren: 14,
    ProtectingFamily: 16,
    ProtectingNotSure: 8,
    GoalReplaceIncome: 16,
    GoalFinalExpenses: 12,
    GoalMortgageOrBills: 15,
    GoalLeaveSomething: 14,
    GoalNotSure: 7,
    Age18To24: 6,
    Age25To34: 8,
    Age35To44: 10,
    Age45To54: 9,
    Age55Plus: 7,
    RapidBounce: -15,
    FieldError: -4,
    ContactFriction: -8,
    Backtrack: -3,
    DeadClick: -3,
    RageClick: -5,
    HighIntentAbandon: -12,
    ContactStepAbandon: -8
  });

  const META_BROWSER_EVENTS = new Set([
    'ViewContent',
    'LeadFormStart',
    'FunnelStepComplete',
    'RecommendationViewed',
    'ContactStepReached',
    'HighIntentLeadSignal',
    'LeadReadySignal',
    'AbandonedHighIntentLead'
  ]);

  function uuidNoDash() {
    if (window.crypto && typeof window.crypto.randomUUID === 'function') {
      return window.crypto.randomUUID().replace(/-/g, '');
    }

    return 'xxxxxxxxxxxx4xxxyxxxxxxxxxxxxxxx'.replace(/[xy]/g, (ch) => {
      const buf = new Uint8Array(1);
      window.crypto.getRandomValues(buf);
      const r = buf[0] % 16;
      const v = ch === 'x' ? r : ((r & 0x3) | 0x8);
      return v.toString(16);
    });
  }

  function safeJsonParse(raw, fallback) {
    if (!raw) return fallback;
    try {
      return JSON.parse(raw);
    } catch {
      return fallback;
    }
  }

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

  function asTrimmed(value) {
    return typeof value === 'string' ? value.trim() : '';
  }

  function normalizeAttributionValue(value) {
    const trimmed = asTrimmed(value);
    return trimmed || null;
  }

  function normalizeAttribution(raw) {
    return {
      utmSource: normalizeAttributionValue(raw?.utmSource),
      utmMedium: normalizeAttributionValue(raw?.utmMedium),
      utmCampaign: normalizeAttributionValue(raw?.utmCampaign),
      utmId: normalizeAttributionValue(raw?.utmId),
      utmContent: normalizeAttributionValue(raw?.utmContent),
      fbclid: normalizeAttributionValue(raw?.fbclid),
      metaCampaignId: normalizeAttributionValue(raw?.metaCampaignId),
      metaAdSetId: normalizeAttributionValue(raw?.metaAdSetId),
      metaAdId: normalizeAttributionValue(raw?.metaAdId)
    };
  }

  function hasAttribution(attribution) {
    if (!attribution) return false;

    return Boolean(
      attribution.utmSource ||
      attribution.utmMedium ||
      attribution.utmCampaign ||
      attribution.utmId ||
      attribution.utmContent ||
      attribution.fbclid ||
      attribution.metaCampaignId ||
      attribution.metaAdSetId ||
      attribution.metaAdId
    );
  }

  function readAttributionFromStorage(key) {
    const fromSession = safeJsonParse(safeStorageGet(window.sessionStorage, key), null);
    if (hasAttribution(fromSession)) {
      return normalizeAttribution(fromSession);
    }

    const fromLocal = safeJsonParse(safeStorageGet(window.localStorage, key), null);
    return hasAttribution(fromLocal) ? normalizeAttribution(fromLocal) : null;
  }

  function writeAttributionToStorage(key, attribution) {
    const normalized = normalizeAttribution(attribution);
    if (!hasAttribution(normalized)) return;

    const json = JSON.stringify(normalized);
    safeStorageSet(window.sessionStorage, key, json);
    safeStorageSet(window.localStorage, key, json);
  }

  function readAttributionFromQuery() {
    const params = new URLSearchParams(window.location.search);
    return normalizeAttribution({
      utmSource: params.get('utm_source'),
      utmMedium: params.get('utm_medium'),
      utmCampaign: params.get('utm_campaign'),
      utmId: params.get('utm_id'),
      utmContent: params.get('utm_content'),
      fbclid: params.get('fbclid'),
      metaCampaignId: params.get('meta_campaign_id'),
      metaAdSetId: params.get('meta_adset_id'),
      metaAdId: params.get('meta_ad_id')
    });
  }

  function rememberQueryAttribution() {
    const queryAttribution = readAttributionFromQuery();
    if (hasAttribution(queryAttribution)) {
      writeAttributionToStorage(STORAGE_ATTR_SESSION, queryAttribution);
    }
  }

  function getVisitorId() {
    const ids = window.legendTrackingIds;
    if (ids && typeof ids.getVisitorId === 'function') {
      const visitorId = asTrimmed(ids.getVisitorId());
      if (visitorId) return visitorId;
    }

    let visitorId = safeStorageGet(window.localStorage, STORAGE_VISITOR);
    if (!visitorId) {
      visitorId = uuidNoDash();
      safeStorageSet(window.localStorage, STORAGE_VISITOR, visitorId);
    }
    return visitorId;
  }

  function getSessionId() {
    const ids = window.legendTrackingIds;
    if (ids && typeof ids.getSessionId === 'function') {
      const sessionId = asTrimmed(ids.getSessionId());
      if (sessionId) return sessionId;
    }

    const now = Date.now();
    const lastTs = Number.parseInt(safeStorageGet(window.localStorage, STORAGE_SESSION_TS) || '0', 10);
    let sessionId = safeStorageGet(window.localStorage, STORAGE_SESSION);
    const expired = !sessionId || !Number.isFinite(lastTs) || (now - lastTs) > SESSION_TIMEOUT_MIN * 60 * 1000;

    if (expired) {
      sessionId = uuidNoDash();
      try {
        window.sessionStorage.removeItem(STORAGE_ATTR_SESSION);
        window.localStorage.removeItem(STORAGE_ATTR_SESSION);
      } catch {
        // ignore storage failures
      }
    }

    safeStorageSet(window.localStorage, STORAGE_SESSION, sessionId);
    safeStorageSet(window.localStorage, STORAGE_SESSION_TS, String(now));
    return sessionId;
  }

  function resolveAttribution() {
    rememberQueryAttribution();

    const ids = window.legendTrackingIds;
    if (ids && typeof ids.getAttribution === 'function') {
      const direct = normalizeAttribution(ids.getAttribution() || {});
      if (hasAttribution(direct)) {
        writeAttributionToStorage(STORAGE_ATTR_SESSION, direct);
        return direct;
      }
    }

    const queryAttribution = readAttributionFromQuery();
    if (hasAttribution(queryAttribution)) {
      writeAttributionToStorage(STORAGE_ATTR_SESSION, queryAttribution);
      return queryAttribution;
    }

    const stored = readAttributionFromStorage(STORAGE_ATTR_SESSION);
    return hasAttribution(stored) ? stored : normalizeAttribution({});
  }

  function resolveQuoteType(value) {
    const normalized = asTrimmed(value).toLowerCase();
    switch (normalized) {
      case 'term':
      case 'term_life':
      case 'term-life':
      case 'life_term':
        return 'term';
      case 'wholelife':
      case 'whole_life':
      case 'whole-life':
      case 'life_whole':
        return 'wholelife';
      case 'finalexpense':
      case 'final_expense':
      case 'final-expense':
      case 'life_finalexpense':
        return 'finalexpense';
      case 'mortgage':
      case 'mortgage_protection':
      case 'mortgage-protection':
      case 'life_mp':
        return 'mortgage';
      case 'iul':
      case 'life_iul':
        return 'iul';
      case 'life_general':
      case 'life':
      default:
        return 'life';
    }
  }

  function buildInitialState(config) {
    return {
      startedAt: Date.now(),
      sessionId: getSessionId(),
      visitorId: getVisitorId(),
      quoteType: resolveQuoteType(config.quoteType),
      pageKey: asTrimmed(config.pageKey),
      effectivePageKey: asTrimmed(config.effectivePageKey || config.pageKey),
      pageVariant: asTrimmed(config.pageVariant),
      pageMode: asTrimmed(config.pageMode),
      agentTrackingProfileId: asTrimmed(config.agentTrackingProfileId),
      agentSlug: asTrimmed(config.agentSlug),
      submitted: false,
      landingViewed: false,
      stayed5Seconds: false,
      stayed15Seconds: false,
      meaningfulScroll: false,
      firstQuestionAnswered: false,
      recommendationViewed: false,
      contactStepReached: false,
      contactInputStarted: false,
      phoneCompleted: false,
      requiredContactFieldsCompleted: false,
      submitAttempted: false,
      leadSubmitted: false,
      rapidBounce: false,
      highIntentAbandon: false,
      contactStepAbandon: false,
      protectingWho: null,
      coverageGoal: null,
      ageRange: null,
      completedSteps: {},
      fieldErrorCount: 0,
      backtrackCount: 0,
      deadClickCount: 0,
      rageClickCount: 0,
      fired: {},
      lastDisabledClickKey: null,
      lastDisabledClickAt: 0,
      lastDisabledClickCount: 0
    };
  }

  function createLandingSession(rawConfig) {
    const config = {
      enabled: Boolean(rawConfig?.enabled),
      sendBrowserEvents: rawConfig?.sendBrowserEvents !== false,
      sendServerEvents: rawConfig?.sendServerEvents !== false,
      persistEvents: rawConfig?.persistEvents !== false,
      debugMode: Boolean(rawConfig?.debugMode),
      endpoint: asTrimmed(rawConfig?.endpoint) || '/analytics/meta-signal',
      quoteType: asTrimmed(rawConfig?.quoteType) || 'life',
      pageKey: asTrimmed(rawConfig?.pageKey),
      effectivePageKey: asTrimmed(rawConfig?.effectivePageKey || rawConfig?.pageKey),
      pageVariant: asTrimmed(rawConfig?.pageVariant),
      pageMode: asTrimmed(rawConfig?.pageMode),
      agentTrackingProfileId: asTrimmed(rawConfig?.agentTrackingProfileId),
      agentSlug: asTrimmed(rawConfig?.agentSlug),
      formId: asTrimmed(rawConfig?.formId) || 'lifeWizardForm',
      requiredContactFields: Array.isArray(rawConfig?.requiredContactFields) ? rawConfig.requiredContactFields : ['FirstName', 'LastName', 'Phone', 'Email'],
      highIntentThreshold: Number(rawConfig?.highIntentThreshold || 60),
      leadReadyThreshold: Number(rawConfig?.leadReadyThreshold || 80),
      weights: Object.assign({}, DEFAULT_WEIGHTS, rawConfig?.weights || {})
    };

    const noop = {
      enabled: false,
      trackLeadFormStart() {},
      trackStepComplete() {},
      trackRecommendationViewed() {},
      trackContactStepReached() {},
      trackContactInputStarted() {},
      trackPhoneCompleted() {},
      trackRequiredContactFieldsCompleted() {},
      trackFieldError() {},
      trackSubmitAttempt() {},
      trackBacktrack() {},
      trackDeadClick() {},
      markSubmitted() {},
      getState() {
        return null;
      }
    };

    if (!config.enabled || config.pageMode.toLowerCase() !== 'paid_landing') {
      return noop;
    }

    const form = document.getElementById(config.formId);
    const storageKey = `legend_meta_signal:${resolveQuoteType(config.quoteType)}:${getSessionId()}`;
    const persisted = safeJsonParse(safeStorageGet(window.sessionStorage, storageKey), null);
    const state = Object.assign(buildInitialState(config), persisted || {});
    state.quoteType = resolveQuoteType(config.quoteType);
    state.pageKey = config.pageKey;
    state.effectivePageKey = config.effectivePageKey;
    state.pageVariant = config.pageVariant;
    state.pageMode = config.pageMode;
    state.agentTrackingProfileId = config.agentTrackingProfileId;
    state.agentSlug = config.agentSlug;
    state.sessionId = getSessionId();
    state.visitorId = getVisitorId();
    state.completedSteps = state.completedSteps || {};
    state.fired = state.fired || {};

    let abandonTracked = false;

    function debug(message, payload) {
      if (!config.debugMode) return;
      try {
        if (typeof payload === 'undefined') {
          console.log('[MetaSignal]', message);
        } else {
          console.log('[MetaSignal]', message, payload);
        }
      } catch {
        // ignore console failures
      }
    }

    function saveState() {
      safeStorageSet(window.sessionStorage, storageKey, JSON.stringify(state));
    }

    function normalizeMetadata(extra) {
      return Object.assign(
        {
          protectingWho: state.protectingWho || undefined,
          coverageGoal: state.coverageGoal || undefined,
          ageRange: state.ageRange || undefined,
          contactStepReached: state.contactStepReached,
          contactInputStarted: state.contactInputStarted,
          phoneCompleted: state.phoneCompleted,
          requiredContactFieldsComplete: state.requiredContactFieldsCompleted
        },
        extra || {}
      );
    }

    function applyMetadata(metadata) {
      const normalized = metadata || {};
      if (normalized.protectingWho) state.protectingWho = asTrimmed(normalized.protectingWho);
      if (normalized.coverageGoal) state.coverageGoal = asTrimmed(normalized.coverageGoal);
      if (normalized.ageRange) state.ageRange = asTrimmed(normalized.ageRange);
      if (normalized.contactStepReached) state.contactStepReached = true;
      if (normalized.contactInputStarted) state.contactInputStarted = true;
      if (normalized.phoneCompleted) state.phoneCompleted = true;
      if (normalized.requiredContactFieldsComplete) state.requiredContactFieldsCompleted = true;
      if (normalized.rapidBounce) state.rapidBounce = true;
      if (normalized.contactStepAbandon) state.contactStepAbandon = true;
    }

    function applyEventToState(eventName, stepNumber, metadata) {
      applyMetadata(metadata);

      switch (eventName) {
        case 'ViewContent':
          state.landingViewed = true;
          break;
        case 'RapidBounce':
          state.rapidBounce = true;
          break;
        case 'SessionEngaged5s':
          state.stayed5Seconds = true;
          break;
        case 'SessionEngaged15s':
          state.stayed15Seconds = true;
          break;
        case 'MeaningfulScroll':
          state.meaningfulScroll = true;
          break;
        case 'LeadFormStart':
          state.firstQuestionAnswered = true;
          break;
        case 'FunnelStepComplete':
          if (Number.isFinite(stepNumber)) {
            state.completedSteps[String(stepNumber)] = true;
          }
          break;
        case 'RecommendationViewed':
          state.recommendationViewed = true;
          break;
        case 'ContactStepReached':
          state.contactStepReached = true;
          break;
        case 'ContactInputStarted':
          state.contactInputStarted = true;
          break;
        case 'PhoneFieldCompleted':
          state.phoneCompleted = true;
          break;
        case 'RequiredContactFieldsCompleted':
          state.requiredContactFieldsCompleted = true;
          break;
        case 'SubmitAttempt':
          state.submitAttempted = true;
          break;
        case 'Lead':
        case 'QualifiedLead':
          state.submitted = true;
          state.leadSubmitted = true;
          break;
        case 'FieldError':
          state.fieldErrorCount += 1;
          break;
        case 'Backtrack':
          state.backtrackCount += 1;
          break;
        case 'DeadClick':
          state.deadClickCount += 1;
          break;
        case 'RageClick':
          state.rageClickCount += 1;
          break;
        case 'AbandonedHighIntentLead':
          state.highIntentAbandon = true;
          if (metadata && metadata.contactStepAbandon) {
            state.contactStepAbandon = true;
          }
          break;
      }
    }

    function scoreProtectingWho() {
      switch ((state.protectingWho || '').toLowerCase()) {
        case 'just_me':
          return config.weights.ProtectingJustMe;
        case 'spouse_or_partner':
          return config.weights.ProtectingSpouseOrPartner;
        case 'children':
          return config.weights.ProtectingChildren;
        case 'family':
          return config.weights.ProtectingFamily;
        case 'not_sure':
          return config.weights.ProtectingNotSure;
        default:
          return 0;
      }
    }

    function scoreCoverageGoal() {
      switch ((state.coverageGoal || '').toLowerCase()) {
        case 'replace_income':
          return config.weights.GoalReplaceIncome;
        case 'final_expenses':
          return config.weights.GoalFinalExpenses;
        case 'mortgage_or_bills':
          return config.weights.GoalMortgageOrBills;
        case 'leave_something':
          return config.weights.GoalLeaveSomething;
        case 'not_sure':
          return config.weights.GoalNotSure;
        default:
          return 0;
      }
    }

    function scoreAgeRange() {
      switch (state.ageRange) {
        case '18-24':
          return config.weights.Age18To24;
        case '25-34':
          return config.weights.Age25To34;
        case '35-44':
          return config.weights.Age35To44;
        case '45-54':
          return config.weights.Age45To54;
        case '55+':
          return config.weights.Age55Plus;
        default:
          return 0;
      }
    }

    function resolveScoreTier(totalSignalScore) {
      if (totalSignalScore < 20) return 'ColdVisitor';
      if (totalSignalScore < 40) return 'EngagedVisitor';
      if (totalSignalScore < 60) return 'FunnelStarter';
      if (totalSignalScore < 80) return 'HighIntentVisitor';
      if (totalSignalScore < 100) return 'LeadReadyVisitor';
      return 'SubmittedLead';
    }

    function computeScore() {
      let engagement = 0;
      if (state.landingViewed) engagement += config.weights.LandingViewed;
      if (state.stayed5Seconds) engagement += config.weights.Stay5Seconds;
      if (state.stayed15Seconds) engagement += config.weights.Stay15Seconds;
      if (state.meaningfulScroll) engagement += config.weights.MeaningfulScroll;
      if (state.firstQuestionAnswered) engagement += config.weights.FirstQuestionAnswered;
      if (state.completedSteps['1']) engagement += config.weights.Step1Completed;
      if (state.completedSteps['2']) engagement += config.weights.Step2Completed;
      if (state.recommendationViewed) engagement += config.weights.RecommendationViewed;
      if (state.contactStepReached) engagement += config.weights.ContactStepReached;
      if (state.contactInputStarted) engagement += config.weights.ContactInputStarted;
      if (state.phoneCompleted) engagement += config.weights.PhoneCompleted;
      if (state.requiredContactFieldsCompleted) engagement += config.weights.RequiredContactCompleted;
      if (state.submitAttempted) engagement += config.weights.SubmitAttempted;
      if (state.leadSubmitted) engagement += config.weights.SuccessfulLeadSubmitted;

      const qualification = scoreProtectingWho() + scoreCoverageGoal() + scoreAgeRange();

      let friction = 0;
      if (state.rapidBounce) friction += config.weights.RapidBounce;
      friction += Math.min(state.fieldErrorCount, 3) * config.weights.FieldError;
      friction += Math.min(state.backtrackCount, 3) * config.weights.Backtrack;
      friction += Math.min(state.deadClickCount, 2) * config.weights.DeadClick;
      friction += Math.min(state.rageClickCount, 2) * config.weights.RageClick;

      if (state.contactStepReached && !state.requiredContactFieldsCompleted && (state.fieldErrorCount > 0 || state.contactStepAbandon)) {
        friction += config.weights.ContactFriction;
      }
      if (state.contactStepAbandon) friction += config.weights.ContactStepAbandon;
      if (state.highIntentAbandon) friction += config.weights.HighIntentAbandon;

      const intentScore = Math.max(0, Math.min(120, qualification + Math.round(engagement * 0.4) + friction));
      let totalSignalScore = Math.max(0, Math.min(120, engagement + qualification + friction));
      if (!state.leadSubmitted) {
        totalSignalScore = Math.min(99, totalSignalScore);
      } else {
        totalSignalScore = Math.max(100, totalSignalScore);
      }

      return {
        intentScore,
        engagementScore: engagement,
        qualificationScore: qualification,
        frictionScore: friction,
        totalSignalScore,
        scoreTier: resolveScoreTier(totalSignalScore)
      };
    }

    function buildPixelPayload(eventName, stepNumber, stepName, score) {
      const payload = {
        quote_type: state.quoteType,
        page_key: state.effectivePageKey || state.pageKey,
        traffic_type: document.body?.dataset?.pageMode === 'paid_landing' ? 'PaidAds' : undefined,
        score_tier: score.scoreTier,
        total_signal_score: score.totalSignalScore
      };

      if (Number.isFinite(stepNumber)) payload.funnel_step = stepNumber;
      if (stepName) payload.step_name = stepName;
      return payload;
    }

    function fireBrowserPixel(eventName, eventId, stepNumber, stepName, score) {
      if (!config.sendBrowserEvents || !META_BROWSER_EVENTS.has(eventName) || typeof window.fbq !== 'function') {
        return false;
      }

      try {
        const payload = buildPixelPayload(eventName, stepNumber, stepName, score);
        if (eventName === 'ViewContent') {
          window.fbq('track', 'ViewContent', payload, { eventID: eventId });
        } else {
          window.fbq('trackCustom', eventName, payload, { eventID: eventId });
        }
        return true;
      } catch {
        return false;
      }
    }

    async function postSignal(payload, useBeacon) {
      if (useBeacon) {
        try {
          const json = JSON.stringify(payload);
          const blob = new Blob([json], { type: 'application/json' });
          if (navigator.sendBeacon && navigator.sendBeacon(config.endpoint, blob)) {
            return { accepted: true, queued: true, metaServerStatus: 'beacon_queued' };
          }
        } catch {
          // fall through to fetch
        }
      }

      try {
        const response = await fetch(config.endpoint, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          keepalive: true,
          body: JSON.stringify(payload)
        });

        if (!response.ok) {
          return { accepted: false, metaServerStatus: 'http_error' };
        }

        return await response.json().catch(() => ({ accepted: true }));
      } catch {
        return { accepted: false, metaServerStatus: 'network_error' };
      }
    }

    async function emitSignal(eventName, options = {}) {
      const onceKey = options.onceKey || null;
      if (onceKey && state.fired[onceKey]) {
        debug(`Duplicate prevented for ${eventName}`, { onceKey, eventId: state.fired[onceKey] });
        return state.fired[onceKey];
      }

      const metadata = normalizeMetadata(options.metadata);
      const stepNumber = Number.isFinite(options.stepNumber) ? options.stepNumber : null;
      const stepName = asTrimmed(options.stepName) || null;
      const eventId = uuidNoDash();

      applyEventToState(eventName, stepNumber, metadata);
      const score = computeScore();
      const browserEventSent = fireBrowserPixel(eventName, eventId, stepNumber, stepName, score);
      const payload = {
        eventName,
        eventId,
        quoteType: state.quoteType,
        pageKey: state.pageKey,
        effectivePageKey: state.effectivePageKey,
        pageVariant: state.pageVariant,
        pageMode: state.pageMode,
        stepNumber,
        stepName,
        url: window.location.href,
        referrer: document.referrer || '',
        sessionId: state.sessionId,
        visitorId: state.visitorId,
        agentTrackingProfileId: state.agentTrackingProfileId || null,
        agentSlug: state.agentSlug || null,
        browserEventSent,
        scoreTier: score.scoreTier,
        score: {
          intentScore: score.intentScore,
          engagementScore: score.engagementScore,
          qualificationScore: score.qualificationScore,
          frictionScore: score.frictionScore,
          totalSignalScore: score.totalSignalScore
        },
        attribution: resolveAttribution(),
        metadata
      };

      if (onceKey) {
        state.fired[onceKey] = eventId;
      }
      saveState();

      const result = await postSignal(payload, Boolean(options.useBeacon));
      debug(`Event fired: ${eventName}`, {
        eventId,
        scoreTier: score.scoreTier,
        totalSignalScore: score.totalSignalScore,
        browserEventSent,
        metaServerStatus: result?.metaServerStatus || result?.status || 'unknown'
      });

      if (!options.skipThresholds) {
        maybeFireThresholdEvents(score);
      }

      return eventId;
    }

    function maybeFireThresholdEvents(score) {
      if (!state.submitted && score.totalSignalScore >= config.highIntentThreshold && !state.fired['threshold-high-intent']) {
        void emitSignal('HighIntentLeadSignal', {
          onceKey: 'threshold-high-intent',
          metadata: {
            threshold: config.highIntentThreshold
          },
          skipThresholds: true
        });
      }

      const leadReadySatisfied =
        !state.submitted &&
        (
          score.totalSignalScore >= config.leadReadyThreshold ||
          state.requiredContactFieldsCompleted
        );

      if (leadReadySatisfied && !state.fired['threshold-lead-ready']) {
        void emitSignal('LeadReadySignal', {
          onceKey: 'threshold-lead-ready',
          metadata: {
            threshold: config.leadReadyThreshold,
            requiredContactFieldsComplete: state.requiredContactFieldsCompleted
          },
          skipThresholds: true
        });
      }
    }

    function maybeTrackAbandon() {
      if (abandonTracked || state.submitted) {
        return;
      }

      const dwellMs = Date.now() - Number(state.startedAt || Date.now());
      if (dwellMs < RAPID_BOUNCE_MS && !state.firstQuestionAnswered && !state.fired['rapid-bounce']) {
        abandonTracked = true;
        void emitSignal('RapidBounce', {
          onceKey: 'rapid-bounce',
          useBeacon: true,
          metadata: {
            rapidBounce: true,
            dwellMilliseconds: dwellMs
          },
          skipThresholds: true
        }).finally(() => {
          abandonTracked = false;
        });
      }

      const score = computeScore();
      const shouldTrackAbandon =
        score.totalSignalScore >= config.highIntentThreshold ||
        state.contactStepReached ||
        state.contactInputStarted ||
        state.requiredContactFieldsCompleted;

      if (!shouldTrackAbandon || state.fired['abandon-high-intent']) {
        return;
      }

      const contactStepAbandon = state.contactStepReached || state.contactInputStarted || state.requiredContactFieldsCompleted;
      abandonTracked = true;
      void emitSignal('AbandonedHighIntentLead', {
        onceKey: 'abandon-high-intent',
        useBeacon: true,
        metadata: {
          contactStepAbandon,
          rapidBounce: dwellMs < RAPID_BOUNCE_MS,
          dwellMilliseconds: dwellMs
        },
        skipThresholds: true
      }).finally(() => {
        abandonTracked = false;
      });
    }

    function allRequiredContactFieldsComplete() {
      if (!(form instanceof HTMLFormElement)) return false;

      for (const fieldName of config.requiredContactFields) {
        const el = form.querySelector(`[name="${fieldName}"]`);
        if (!(el instanceof HTMLInputElement || el instanceof HTMLSelectElement || el instanceof HTMLTextAreaElement)) {
          return false;
        }

        const value = asTrimmed(el.value);
        if (!value) return false;

        if (fieldName === 'Email' && el instanceof HTMLInputElement && !el.checkValidity()) {
          return false;
        }

        if (fieldName === 'Phone') {
          const digits = value.replace(/\D/g, '');
          if (digits.length < 10) return false;
        }
      }

      return true;
    }

    function wireContactInputs() {
      if (!(form instanceof HTMLFormElement)) return;

      config.requiredContactFields.forEach((fieldName) => {
        const el = form.querySelector(`[name="${fieldName}"]`);
        if (!(el instanceof HTMLInputElement || el instanceof HTMLSelectElement || el instanceof HTMLTextAreaElement)) {
          return;
        }

        const eventName = el instanceof HTMLSelectElement ? 'change' : 'input';
        el.addEventListener(eventName, () => {
          const value = asTrimmed(el.value);
          if (value && !state.fired['contact-input-started']) {
            void emitSignal('ContactInputStarted', {
              onceKey: 'contact-input-started',
              metadata: {
                contactInputStarted: true,
                fieldName
              }
            });
          }

          if (fieldName === 'Phone') {
            const digits = value.replace(/\D/g, '');
            if (digits.length >= 10 && !state.fired['phone-completed']) {
              void emitSignal('PhoneFieldCompleted', {
                onceKey: 'phone-completed',
                metadata: {
                  phoneCompleted: true
                }
              });
            }
          }

          if (allRequiredContactFieldsComplete() && !state.fired['required-contact-complete']) {
            void emitSignal('RequiredContactFieldsCompleted', {
              onceKey: 'required-contact-complete',
              metadata: {
                requiredContactFieldsComplete: true,
                contactStepReached: true
              }
            });

            if (!state.fired['step-3']) {
              void emitSignal('FunnelStepComplete', {
                onceKey: 'step-3',
                stepNumber: 3,
                stepName: 'contact_complete',
                metadata: {
                  requiredContactFieldsComplete: true,
                  contactStepReached: true
                }
              });
            }
          }
        });
      });
    }

    function wireScrollTracking() {
      if (state.fired['meaningful-scroll']) return;

      const onScroll = () => {
        const doc = document.documentElement;
        const body = document.body;
        const fullHeight = Math.max(doc.scrollHeight, body ? body.scrollHeight : 0);
        const viewport = window.innerHeight || doc.clientHeight || 0;
        const maxScrollable = Math.max(1, fullHeight - viewport);
        const scrolled = Math.max(window.scrollY || window.pageYOffset || doc.scrollTop || 0, 0);
        const percent = Math.min(100, Math.round((scrolled / maxScrollable) * 100));

        if (percent >= MEANINGFUL_SCROLL_THRESHOLD) {
          window.removeEventListener('scroll', onScroll);
          void emitSignal('MeaningfulScroll', {
            onceKey: 'meaningful-scroll',
            metadata: {
              scrollPercent: percent
            }
          });
        }
      };

      window.addEventListener('scroll', onScroll, { passive: true });
    }

    function wireDisabledClickTracking() {
      document.addEventListener('click', (event) => {
        const target = event.target instanceof Element
          ? event.target.closest('button, [role="button"], input[type="submit"]')
          : null;
        if (!(target instanceof HTMLElement)) return;

        const isDisabled =
          target.hasAttribute('disabled') ||
          target.getAttribute('aria-disabled') === 'true';
        if (!isDisabled) return;

        const clickKey = target.id || target.getAttribute('name') || target.textContent?.trim() || 'disabled-control';
        const now = Date.now();
        const isRepeat = state.lastDisabledClickKey === clickKey && (now - Number(state.lastDisabledClickAt || 0)) <= 1200;

        state.lastDisabledClickKey = clickKey;
        state.lastDisabledClickAt = now;
        state.lastDisabledClickCount = isRepeat ? Number(state.lastDisabledClickCount || 0) + 1 : 1;
        saveState();

        void emitSignal('DeadClick', {
          metadata: {
            elementId: target.id || null,
            disabledElement: clickKey
          }
        });

        if (state.lastDisabledClickCount >= 3) {
          void emitSignal('RageClick', {
            metadata: {
              elementId: target.id || null,
              disabledElement: clickKey
            }
          });
        }
      });
    }

    function scheduleEngagementTimers() {
      const elapsed = Date.now() - Number(state.startedAt || Date.now());
      const wait5s = Math.max(0, 5000 - elapsed);
      const wait15s = Math.max(0, 15000 - elapsed);

      if (!state.fired['engaged-5s']) {
        window.setTimeout(() => {
          if (!state.fired['engaged-5s']) {
            void emitSignal('SessionEngaged5s', { onceKey: 'engaged-5s' });
          }
        }, wait5s);
      }

      if (!state.fired['engaged-15s']) {
        window.setTimeout(() => {
          if (!state.fired['engaged-15s']) {
            void emitSignal('SessionEngaged15s', { onceKey: 'engaged-15s' });
          }
        }, wait15s);
      }
    }

    function init() {
      wireContactInputs();
      wireScrollTracking();
      wireDisabledClickTracking();
      scheduleEngagementTimers();

      if (!state.fired['view-content']) {
        void emitSignal('ViewContent', { onceKey: 'view-content' });
      }

      window.addEventListener('pagehide', maybeTrackAbandon);
      document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'hidden') {
          maybeTrackAbandon();
        }
      });

      debug('Initialized', {
        sessionId: state.sessionId,
        visitorId: state.visitorId,
        quoteType: state.quoteType,
        pageMode: state.pageMode
      });
    }

    init();

    return {
      enabled: true,
      trackLeadFormStart(metadata = {}) {
        return emitSignal('LeadFormStart', {
          onceKey: 'lead-form-start',
          metadata
        });
      },
      trackStepComplete(stepNumber, stepName, metadata = {}) {
        return emitSignal('FunnelStepComplete', {
          onceKey: `step-${stepNumber}`,
          stepNumber,
          stepName,
          metadata
        });
      },
      trackRecommendationViewed(metadata = {}) {
        return emitSignal('RecommendationViewed', {
          onceKey: 'recommendation-viewed',
          metadata
        });
      },
      trackContactStepReached(metadata = {}) {
        return emitSignal('ContactStepReached', {
          onceKey: 'contact-step-reached',
          metadata: Object.assign({}, metadata, { contactStepReached: true })
        });
      },
      trackContactInputStarted(metadata = {}) {
        return emitSignal('ContactInputStarted', {
          onceKey: 'contact-input-started',
          metadata: Object.assign({}, metadata, { contactInputStarted: true })
        });
      },
      trackPhoneCompleted(metadata = {}) {
        return emitSignal('PhoneFieldCompleted', {
          onceKey: 'phone-completed',
          metadata: Object.assign({}, metadata, { phoneCompleted: true })
        });
      },
      trackRequiredContactFieldsCompleted(metadata = {}) {
        return emitSignal('RequiredContactFieldsCompleted', {
          onceKey: 'required-contact-complete',
          metadata: Object.assign({}, metadata, { requiredContactFieldsComplete: true, contactStepReached: true })
        });
      },
      trackFieldError(fieldName, metadata = {}) {
        return emitSignal('FieldError', {
          metadata: Object.assign({}, metadata, { fieldName })
        });
      },
      trackSubmitAttempt(metadata = {}) {
        return emitSignal('SubmitAttempt', {
          onceKey: 'submit-attempt',
          metadata
        });
      },
      trackBacktrack(metadata = {}) {
        return emitSignal('Backtrack', { metadata });
      },
      trackDeadClick(metadata = {}) {
        return emitSignal('DeadClick', { metadata });
      },
      markSubmitted(metadata = {}) {
        state.submitted = true;
        state.leadSubmitted = true;
        applyMetadata(metadata);
        saveState();
        debug('Lead marked submitted', computeScore());
      },
      getState() {
        const score = computeScore();
        return {
          sessionId: state.sessionId,
          visitorId: state.visitorId,
          quoteType: state.quoteType,
          score
        };
      }
    };
  }

  window.metaSignalIntelligence = {
    createLandingSession
  };
})();
