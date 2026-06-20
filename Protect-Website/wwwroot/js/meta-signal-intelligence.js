(() => {
  const STORAGE_VISITOR = 'legend_visitor_id';
  const STORAGE_SESSION = 'legend_session_id';
  const STORAGE_SESSION_TS = 'legend_session_ts';
  const STORAGE_ATTR_SESSION = 'legend_attr_session';
  const STORAGE_SESSION_ENTRY_SOURCE = 'legend_meta_entry_source';
  const STORAGE_SESSION_PAGE_CLUSTERS = 'legend_meta_page_clusters';
  const STORAGE_PAGE_INIT_PREFIX = 'legend_meta_page_init';
  const STORAGE_PAGE_VISIT_PREFIX = 'legend_meta_page_visits';
  const SESSION_TIMEOUT_MIN = 30;
  const MEANINGFUL_SCROLL_THRESHOLD = 35;
  const MID_INTENT_SCROLL_THRESHOLD = 50;
  const HIGH_INTENT_SCROLL_THRESHOLD = 75;
  const CTA_HOVER_THRESHOLD = 2;
  const TIMESTAMP_BUCKET_MINUTES = 5;
  const RAPID_BOUNCE_MS = 3000;
  const CTA_SELECTOR = '[data-primary-cta], [data-cta], [data-life-funnel-start], button[type="submit"], a[href*="/Quote/"]';

  const LEARNING_WEIGHT_BY_EVENT = Object.freeze({
    ViewContent: 0.1,
    MeaningfulScroll: 0.14,
    DiscoveryComplete: 0.18,
    RecommendationViewed: 0.22,
    LeadFormStart: 0.24,
    ContactStepReached: 0.26,
    HighIntentLeadSignal: 0.3,
    LeadReadySignal: 0.28,
    AbandonedHighIntentLead: 0.18
  });

  const FUNNEL_DEPTH_BY_EVENT = Object.freeze({
    ViewContent: 1,
    MeaningfulScroll: 2,
    SessionEngaged5s: 3,
    SessionEngaged15s: 3,
    DiscoveryComplete: 3,
    RecommendationViewed: 3,
    LeadFormStart: 4,
    ContactStepReached: 5,
    ContactInputStarted: 5,
    PhoneFieldCompleted: 5,
    RequiredContactFieldsCompleted: 5,
    SubmitAttempt: 5,
    HighIntentLeadSignal: 5,
    LeadReadySignal: 5,
    AbandonedHighIntentLead: 5
  });

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

  const DEFAULT_META_BROWSER_EVENTS = [
    'LeadFormStart',
    'DiscoveryComplete',
    'RecommendationViewed',
    'ContactStepReached',
    'HighIntentLeadSignal',
    'LeadReadySignal',
    'AbandonedHighIntentLead'
  ];

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

  function readInteger(value, fallback = 0) {
    const parsed = Number.parseInt(String(value || ''), 10);
    return Number.isFinite(parsed) ? parsed : fallback;
  }

  function clamp(value, min, max) {
    const numeric = Number.isFinite(value) ? value : min;
    return Math.min(max, Math.max(min, numeric));
  }

  function round(value, digits = 2) {
    const factor = 10 ** digits;
    return Math.round((Number(value) || 0) * factor) / factor;
  }

  function asTrimmed(value) {
    return typeof value === 'string' ? value.trim() : '';
  }

  function resolvePageClusterId(value) {
    const normalized = asTrimmed(value || window.location.pathname)
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, '_')
      .replace(/^_+|_+$/g, '')
      .replace(/_landing$/, '');
    return normalized || 'page';
  }

  function resolveEntrySource(attribution) {
    const source = asTrimmed(attribution?.utmSource).toLowerCase();
    const medium = asTrimmed(attribution?.utmMedium).toLowerCase();
    const fbclid = asTrimmed(attribution?.fbclid);
    const hasMetaIds = Boolean(
      asTrimmed(attribution?.metaCampaignId) ||
      asTrimmed(attribution?.metaAdSetId) ||
      asTrimmed(attribution?.metaAdId)
    );

    if (fbclid || hasMetaIds) return 'meta';
    if (source && medium) return `${source}:${medium}`;
    if (source) return source;
    if (medium) return medium;
    return 'direct';
  }

  function buildTimestampBucket(timestampMs = Date.now()) {
    const bucket = new Date(timestampMs);
    bucket.setUTCSeconds(0, 0);
    bucket.setUTCMinutes(Math.floor(bucket.getUTCMinutes() / TIMESTAMP_BUCKET_MINUTES) * TIMESTAMP_BUCKET_MINUTES);
    return bucket.toISOString();
  }

  function normalizeEventKeyPart(value, fallback) {
    const normalized = asTrimmed(value);
    return normalized || fallback;
  }

  function buildBrowserEventKey(eventName, leadId) {
    const normalizedEventName = normalizeEventKeyPart(eventName, 'unknown');
    const normalizedLeadId = normalizeEventKeyPart(leadId, 'anonymous');
    const normalizedSessionId = normalizeEventKeyPart(getSessionId(), 'no_session');
    return `${normalizedEventName}:${normalizedLeadId}:${normalizedSessionId}`;
  }

  function normalizeAttributionValue(value) {
    const trimmed = asTrimmed(value);
    return trimmed || null;
  }

  function preserveFbclidValue(value) {
    return typeof value === 'string' && value.length > 0 ? value : null;
  }

  function normalizeAttribution(raw) {
    return {
      utmSource: normalizeAttributionValue(raw?.utmSource),
      utmMedium: normalizeAttributionValue(raw?.utmMedium),
      utmCampaign: normalizeAttributionValue(raw?.utmCampaign),
      utmId: normalizeAttributionValue(raw?.utmId),
      utmContent: normalizeAttributionValue(raw?.utmContent),
      fbclid: preserveFbclidValue(raw?.fbclid),
      fbc: asTrimmed(raw?.fbc),
      fbp: asTrimmed(raw?.fbp),
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

  function readCookieValue(name) {
    try {
      const prefix = `${encodeURIComponent(name)}=`;
      const parts = String(document.cookie || '').split(';');
      for (const part of parts) {
        const trimmed = part.trim();
        if (trimmed.startsWith(prefix)) {
          return decodeURIComponent(trimmed.slice(prefix.length));
        }
      }
    } catch {
      return null;
    }

    return null;
  }

  function writeCookieValue(name, value, maxAgeSeconds) {
    if (!name || !value) return;

    try {
      const secure = window.location.protocol === 'https:' ? '; Secure' : '';
      document.cookie = `${encodeURIComponent(name)}=${encodeURIComponent(value)}; Max-Age=${maxAgeSeconds}; Path=/; SameSite=Lax${secure}`;
    } catch {
      // Cookie writes can fail in restricted browsers; server-side CAPI still proceeds with IP/UA.
    }
  }

  function ensureFbpCookie() {
    const existing = readCookieValue('_fbp');
    if (existing) return existing;

    const timestamp = Date.now();
    const randomPart = Math.floor(Math.random() * 2147483647);
    const generated = `fb.1.${timestamp}.${randomPart}`;
    writeCookieValue('_fbp', generated, 90 * 24 * 60 * 60);
    return generated;
  }

  function ensureFbcCookie(attribution) {
    const existing = readCookieValue('_fbc');
    if (existing) return existing;

    const fbclid = attribution?.fbclid;
    if (typeof fbclid !== 'string' || fbclid.length === 0) return null;

    const generated = `fb.1.${Date.now()}.${fbclid}`;
    writeCookieValue('_fbc', generated, 90 * 24 * 60 * 60);
    return generated;
  }

  function ensureEarlyMetaCookies() {
    const attribution = resolveAttribution();
    const fbp = ensureFbpCookie();
    const fbc = ensureFbcCookie(attribution);

    if (attribution) {
      attribution.fbp = attribution.fbp || fbp || '';
      attribution.fbc = attribution.fbc || fbc || '';
    }
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
      fbc: readCookieValue('_fbc'),
      fbp: readCookieValue('_fbp'),
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
      case 'health':
      case 'health_insurance':
      case 'quote_dvh':
        return 'health';
      case 'disability':
      case 'disability_insurance':
      case 'quote_disability':
        return 'disability';
      case 'commercial':
      case 'commercial_insurance':
      case 'quote_commercial':
        return 'commercial';
      case 'home':
      case 'home_insurance':
      case 'quote_home':
        return 'home';
      case 'auto':
      case 'auto_insurance':
      case 'quote_auto':
        return 'auto';
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
      pageClusterId: resolvePageClusterId(config.effectivePageKey || config.pageKey || window.location.pathname),
      pageVariant: asTrimmed(config.pageVariant),
      pageMode: asTrimmed(config.pageMode),
      agentTrackingProfileId: asTrimmed(config.agentTrackingProfileId),
      agentSlug: asTrimmed(config.agentSlug),
      entrySource: 'direct',
      repeatVisitCount: 1,
      navigationDepth: 1,
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
      maxScrollPercent: 0,
      interactionCount: 0,
      formEngagementCount: 0,
      ctaHoverCount: 0,
      midIntentCandidate: false,
      highIntentCandidate: false,
      ctaIntentBoost: false,
      conversionIntentObserved: false,
      fired: {},
      engagedFields: {},
      lastInteractionAt: 0,
      lastCtaHoverKey: null,
      lastCtaHoverAt: 0,
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
      highIntentThreshold: Number(rawConfig?.highIntentThreshold || 70),
      leadReadyThreshold: Number(rawConfig?.leadReadyThreshold || 90),
      browserEventNames: new Set(Array.isArray(rawConfig?.browserEventNames) ? rawConfig.browserEventNames : DEFAULT_META_BROWSER_EVENTS),
      leadSignalRules: Object.assign({
        leadReadyRequiresContactStep: true,
        leadReadyRequiresValidPhone: true,
        leadReadyRequiresValidEmail: false,
        qualifiedLeadRequiresLeadReady: true,
        qualifiedLeadMinimumTotalScore: 80
      }, rawConfig?.leadSignalRules || {}),
      weights: Object.assign({}, DEFAULT_WEIGHTS, rawConfig?.weights || {})
    };

    const noop = {
      enabled: false,
      trackLeadFormStart() {},
      trackDiscoveryComplete() {},
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

    if (!config.enabled) {
      return noop;
    }

    const form = document.getElementById(config.formId);
    const storageKey = `legend_meta_signal:${resolveQuoteType(config.quoteType)}:${getSessionId()}`;
    const persisted = safeJsonParse(safeStorageGet(window.sessionStorage, storageKey), null);
    const state = Object.assign(buildInitialState(config), persisted || {});
    state.quoteType = resolveQuoteType(config.quoteType);
    state.pageKey = config.pageKey;
    state.effectivePageKey = config.effectivePageKey;
    state.pageClusterId = resolvePageClusterId(config.effectivePageKey || config.pageKey || window.location.pathname);
    state.pageVariant = config.pageVariant;
    state.pageMode = config.pageMode;
    state.agentTrackingProfileId = config.agentTrackingProfileId;
    state.agentSlug = config.agentSlug;
    state.sessionId = getSessionId();
    state.visitorId = getVisitorId();
    state.completedSteps = state.completedSteps || {};
    state.fired = state.fired || {};
    state.engagedFields = state.engagedFields || {};

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

    function initializeSessionProfile() {
      const attribution = resolveAttribution();
      const pageClusterId = resolvePageClusterId(state.effectivePageKey || state.pageKey || window.location.pathname);
      const pageVisitKey = `${STORAGE_PAGE_VISIT_PREFIX}:${pageClusterId}`;
      const pageInitKey = `${STORAGE_PAGE_INIT_PREFIX}:${state.sessionId}:${pageClusterId}:${window.location.pathname}`;
      const sessionPagesKey = `${STORAGE_SESSION_PAGE_CLUSTERS}:${state.sessionId}`;
      let sessionPages = safeJsonParse(safeStorageGet(window.sessionStorage, sessionPagesKey), []);

      if (!Array.isArray(sessionPages)) {
        sessionPages = [];
      }

      sessionPages = sessionPages
        .map((value) => asTrimmed(value))
        .filter(Boolean);

      if (!safeStorageGet(window.sessionStorage, pageInitKey)) {
        if (!sessionPages.includes(pageClusterId)) {
          sessionPages.push(pageClusterId);
        }

        safeStorageSet(window.sessionStorage, sessionPagesKey, JSON.stringify(sessionPages));
        safeStorageSet(window.sessionStorage, pageInitKey, '1');
        safeStorageSet(
          window.localStorage,
          pageVisitKey,
          String(readInteger(safeStorageGet(window.localStorage, pageVisitKey), 0) + 1)
        );
      }

      state.pageClusterId = pageClusterId;
      state.entrySource = resolveEntrySource(attribution);
      state.repeatVisitCount = Math.max(1, readInteger(safeStorageGet(window.localStorage, pageVisitKey), 1));
      state.navigationDepth = Math.max(1, sessionPages.length || 1);

      safeStorageSet(window.sessionStorage, STORAGE_SESSION_ENTRY_SOURCE, state.entrySource);
      saveState();
    }

    initializeSessionProfile();

    function normalizeMetadata(extra) {
      return Object.assign(
        {
          protectingWho: state.protectingWho || undefined,
          coverageGoal: state.coverageGoal || undefined,
          ageRange: state.ageRange || undefined,
          contactStepReached: state.contactStepReached,
          contactInputStarted: state.contactInputStarted,
          phoneCompleted: state.phoneCompleted,
          requiredContactFieldsComplete: state.requiredContactFieldsCompleted,
          midIntentCandidate: state.midIntentCandidate,
          highIntentCandidate: state.highIntentCandidate,
          ctaIntentBoost: state.ctaIntentBoost,
          conversionIntentObserved: state.conversionIntentObserved
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
      if (normalized.midIntentCandidate) state.midIntentCandidate = true;
      if (normalized.highIntentCandidate) state.highIntentCandidate = true;
      if (normalized.ctaIntentBoost) state.ctaIntentBoost = true;
      if (normalized.conversionIntentObserved) state.conversionIntentObserved = true;
      if (Number.isFinite(normalized.scrollPercent)) {
        state.maxScrollPercent = Math.max(state.maxScrollPercent || 0, Math.round(normalized.scrollPercent));
      }
    }

    function getSessionDurationMs() {
      return Math.max(0, Date.now() - Number(state.startedAt || Date.now()));
    }

    function registerInteraction() {
      const now = Date.now();
      if ((now - Number(state.lastInteractionAt || 0)) < 150) {
        return;
      }

      state.lastInteractionAt = now;
      state.interactionCount = Number(state.interactionCount || 0) + 1;
      saveState();
    }

    function registerFormFieldEngagement(fieldName) {
      const normalizedFieldName = asTrimmed(fieldName);
      if (normalizedFieldName && !state.engagedFields[normalizedFieldName]) {
        state.engagedFields[normalizedFieldName] = true;
        state.formEngagementCount = Number(state.formEngagementCount || 0) + 1;
      }

      state.conversionIntentObserved = true;
      saveState();
    }

    function updateScrollSignals(percent) {
      const normalizedPercent = clamp(Math.round(Number(percent) || 0), 0, 100);
      let changed = false;

      if (normalizedPercent > Number(state.maxScrollPercent || 0)) {
        state.maxScrollPercent = normalizedPercent;
        changed = true;
      }

      if (normalizedPercent >= MID_INTENT_SCROLL_THRESHOLD && !state.midIntentCandidate) {
        state.midIntentCandidate = true;
        changed = true;
      }

      if (normalizedPercent >= HIGH_INTENT_SCROLL_THRESHOLD && !state.highIntentCandidate) {
        state.highIntentCandidate = true;
        changed = true;
      }

      if (changed) {
        saveState();
      }
    }

    function resolveFunnelDepthIndex(eventName) {
      const explicit = FUNNEL_DEPTH_BY_EVENT[eventName];
      if (Number.isFinite(explicit)) {
        return explicit;
      }

      if (state.highIntentCandidate || state.contactStepReached || state.contactInputStarted) return 5;
      if (state.firstQuestionAnswered) return 4;
      if (state.recommendationViewed || state.stayed5Seconds || state.stayed15Seconds) return 3;
      if (state.maxScrollPercent >= MEANINGFUL_SCROLL_THRESHOLD) return 2;
      return 1;
    }

    function resolveLearningWeight(eventName, funnelDepthIndex) {
      const explicit = LEARNING_WEIGHT_BY_EVENT[eventName];
      if (Number.isFinite(explicit)) {
        return explicit;
      }

      return round(clamp(0.08 + (funnelDepthIndex * 0.04), 0.1, 0.3), 2);
    }

    function computeEngagementIntensityScore() {
      const scrollFactor = clamp(Number(state.maxScrollPercent || 0), 0, 100);
      const timeFactor = clamp((getSessionDurationMs() / 90000) * 100, 0, 100);
      const interactionFactor = clamp((Number(state.interactionCount || 0) / 12) * 100, 0, 100);
      const formFactor = clamp(
        (state.firstQuestionAnswered ? 12 : 0) +
        (state.contactInputStarted ? 15 : 0) +
        (state.contactStepReached ? 20 : 0) +
        (state.phoneCompleted ? 10 : 0) +
        (state.requiredContactFieldsCompleted ? 18 : 0) +
        (state.submitAttempted ? 20 : 0) +
        Math.min(Number(state.formEngagementCount || 0) * 5, 15),
        0,
        100
      );

      return Math.round(clamp(
        (scrollFactor * 0.35) +
        (timeFactor * 0.25) +
        (interactionFactor * 0.2) +
        (formFactor * 0.2),
        0,
        100
      ));
    }

    function computeSessionConfidenceScore() {
      const repeatVisitFactor = clamp((Math.max(1, Number(state.repeatVisitCount || 1)) - 1) / 3, 0, 1);
      const durationFactor = clamp(getSessionDurationMs() / 120000, 0, 1);
      const navigationFactor = clamp((Math.max(1, Number(state.navigationDepth || 1)) - 1) / 3, 0, 1);

      return round(clamp(
        (repeatVisitFactor * 0.4) +
        (durationFactor * 0.35) +
        (navigationFactor * 0.25),
        0,
        1
      ), 2);
    }

    function buildPredictionMarkers() {
      return {
        midIntentCandidate: Boolean(state.midIntentCandidate),
        highIntentCandidate: Boolean(state.highIntentCandidate),
        ctaIntentBoost: Boolean(state.ctaIntentBoost),
        conversionIntentObserved: Boolean(state.conversionIntentObserved)
      };
    }

    function buildSessionFingerprint(attribution) {
      return {
        sessionId: state.sessionId,
        visitorId: state.visitorId,
        pageClusterId: state.pageClusterId,
        entrySource: state.entrySource || resolveEntrySource(attribution),
        timestampBucket: buildTimestampBucket()
      };
    }

    function resolveTrafficQualityHint(eventName, score, engagementIntensityScore) {
      if (
        eventName === 'HighIntentLeadSignal' ||
        score.totalSignalScore >= config.highIntentThreshold ||
        state.highIntentCandidate ||
        state.ctaIntentBoost ||
        state.conversionIntentObserved ||
        state.contactStepReached ||
        state.requiredContactFieldsCompleted
      ) {
        return 'high-intent';
      }

      if (
        engagementIntensityScore >= 40 ||
        state.midIntentCandidate ||
        state.repeatVisitCount > 1 ||
        state.recommendationViewed ||
        state.firstQuestionAnswered
      ) {
        return 'warm';
      }

      return 'cold';
    }

    function buildLearningEnrichment(eventName, score, clientContext, attribution, metadata) {
      const funnelDepthIndex = resolveFunnelDepthIndex(eventName);
      const engagementIntensityScore = computeEngagementIntensityScore();
      const sessionConfidenceScore = computeSessionConfidenceScore();
      const sessionFingerprint = buildSessionFingerprint(attribution);
      const leadId =
        asTrimmed(metadata?.leadId) ||
        asTrimmed(metadata?.LeadId) ||
        asTrimmed(metadata?.websiteLeadId) ||
        asTrimmed(metadata?.WebsiteLeadId) ||
        null;

      return {
        eventKey: buildBrowserEventKey(eventName, leadId),
        isBrowserSignal: true,
        isServerAuthority: false,
        serverAuthorityWinsConflictResolution: true,
        browserPayloadCanOverrideServer: false,
        engagementIntensityScore,
        sessionConfidenceScore,
        funnelDepthIndex,
        deviceContextTag: asTrimmed(clientContext?.deviceType) || 'desktop',
        trafficQualityHint: resolveTrafficQualityHint(eventName, score, engagementIntensityScore),
        metaSignalBoost: {
          isBrowserLearningSignal: true,
          learningWeight: resolveLearningWeight(eventName, funnelDepthIndex),
          attributionAssist: true
        },
        predictionMarkers: buildPredictionMarkers(),
        sessionFingerprint,
        learningContext: {
          repeatVisitCount: Math.max(1, Number(state.repeatVisitCount || 1)),
          sessionDurationMs: getSessionDurationMs(),
          navigationDepth: Math.max(1, Number(state.navigationDepth || 1)),
          interactionCount: Number(state.interactionCount || 0),
          formEngagementCount: Number(state.formEngagementCount || 0),
          ctaHoverCount: Number(state.ctaHoverCount || 0),
          maxScrollPercent: Number(state.maxScrollPercent || 0)
        }
      };
    }

    function backfillProgressState(eventName) {
      const impliesLeadFormStart = new Set([
        'LeadFormStart',
        'DiscoveryComplete',
        'FunnelStepComplete',
        'RecommendationViewed',
        'ContactStepReached',
        'ContactInputStarted',
        'PhoneFieldCompleted',
        'RequiredContactFieldsCompleted',
        'SubmitAttempt',
        'Lead',
        'QualifiedLead'
      ]);
      const impliesDiscoveryComplete = new Set([
        'DiscoveryComplete',
        'FunnelStepComplete',
        'RecommendationViewed',
        'ContactStepReached',
        'ContactInputStarted',
        'PhoneFieldCompleted',
        'RequiredContactFieldsCompleted',
        'SubmitAttempt',
        'Lead',
        'QualifiedLead'
      ]);
      const impliesContactStepReached = new Set([
        'ContactStepReached',
        'ContactInputStarted',
        'PhoneFieldCompleted',
        'RequiredContactFieldsCompleted',
        'SubmitAttempt',
        'Lead',
        'QualifiedLead'
      ]);

      if (impliesLeadFormStart.has(eventName)) {
        state.firstQuestionAnswered = true;
      }

      if (impliesDiscoveryComplete.has(eventName)) {
        state.completedSteps['1'] = true;
      }

      if (impliesContactStepReached.has(eventName)) {
        state.contactStepReached = true;
      }
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
        case 'DiscoveryComplete':
          state.firstQuestionAnswered = true;
          state.completedSteps['1'] = true;
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

      backfillProgressState(eventName);
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

    function resolveScoreTier() {
      if (state.leadSubmitted) return 'SubmittedLead';
      if (state.submitAttempted || state.requiredContactFieldsCompleted) return 'SubmitAttempter';
      if (state.contactStepReached || state.contactInputStarted || state.phoneCompleted) return 'ContactStepViewer';
      if (state.recommendationViewed) return 'RecommendationViewer';
      if (state.completedSteps['1'] || state.firstQuestionAnswered) return 'FunnelStarter';
      if (state.stayed5Seconds || state.stayed15Seconds || state.meaningfulScroll) return 'EngagedVisitor';
      return 'ColdVisitor';
    }

    function applyBehaviorScoreCap(totalSignalScore) {
      if (state.leadSubmitted) return Math.max(100, totalSignalScore);
      if (state.submitAttempted || state.requiredContactFieldsCompleted) return Math.min(99, totalSignalScore);
      if (state.contactStepReached || state.contactInputStarted || state.phoneCompleted) return Math.min(89, totalSignalScore);
      if (state.recommendationViewed) return Math.min(79, totalSignalScore);
      if (state.completedSteps['1'] || state.firstQuestionAnswered) return Math.min(64, totalSignalScore);
      if (state.stayed5Seconds || state.stayed15Seconds || state.meaningfulScroll) return Math.min(39, totalSignalScore);
      return Math.min(19, totalSignalScore);
    }

    function resolveTrafficType(attribution) {
      const source = asTrimmed(attribution?.utmSource).toLowerCase();
      const medium = asTrimmed(attribution?.utmMedium).toLowerCase();
      const campaign = asTrimmed(attribution?.utmCampaign).toLowerCase();
      const hasMetaIds = Boolean(
        asTrimmed(attribution?.metaCampaignId) ||
        asTrimmed(attribution?.metaAdSetId) ||
        asTrimmed(attribution?.metaAdId)
      );

      if (asTrimmed(attribution?.fbclid) || hasMetaIds) return 'PaidAds';
      if (['cpc', 'ppc', 'paid', 'paidsearch', 'display', 'paid_social', 'social_paid', 'remarketing', 'retargeting', 'paid_search', 'paid-social'].includes(medium)) return 'PaidAds';
      if (['adwords', 'googleads', 'google_ads', 'gads', 'bingads', 'meta_ads', 'facebook_ads', 'instagram_ads', 'paidsearch', 'display', 'paid_social', 'cpc', 'ppc', 'remarketing', 'retargeting'].includes(source)) return 'PaidAds';
      if (['organic', 'seo', 'organic_search'].includes(medium)) return 'Organic';
      if (['(none)', 'direct'].includes(medium)) return 'Direct';
      if (['referral', 'partner'].includes(medium)) return 'Referral';
      if (['google', 'bing', 'yahoo', 'duckduckgo', 'brave', 'ecosia', 'search'].includes(source)) return 'Organic';
      if (['facebook', 'fb', 'meta', 'instagram', 'tiktok', 'youtube', 'linkedin', 'reddit', 'x', 'twitter', 'pinterest', 'nextdoor', 'partner', 'newsletter'].includes(source)) return 'Referral';
      if (!source && !medium && !campaign) return 'Direct';
      return 'Unknown';
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
      const totalSignalScore = applyBehaviorScoreCap(Math.max(0, Math.min(120, engagement + qualification + friction)));

      return {
        intentScore,
        engagementScore: engagement,
        qualificationScore: qualification,
        frictionScore: friction,
        totalSignalScore,
        scoreTier: resolveScoreTier()
      };
    }

    function buildPixelPayload(stepNumber, stepName, score, enrichment, attribution) {
      const predictionMarkers = enrichment.predictionMarkers || {};
      const sessionFingerprint = enrichment.sessionFingerprint || {};
      const metaSignalBoost = enrichment.metaSignalBoost || {};
      const payload = {
        quote_type: state.quoteType,
        page_key: state.effectivePageKey || state.pageKey,
        page_variant: state.pageVariant || undefined,
        page_mode: state.pageMode || undefined,
        traffic_type: resolveTrafficType(attribution),
        campaign_key: attribution.utmCampaign || attribution.metaCampaignId || undefined,
        score_tier: score.scoreTier,
        total_signal_score: score.totalSignalScore,
        event_key: enrichment.eventKey,
        engagement_intensity_score: enrichment.engagementIntensityScore,
        session_confidence_score: enrichment.sessionConfidenceScore,
        funnel_depth_index: enrichment.funnelDepthIndex,
        device_context_tag: enrichment.deviceContextTag,
        traffic_quality_hint: enrichment.trafficQualityHint,
        is_browser_signal: enrichment.isBrowserSignal === true,
        is_server_authority: enrichment.isServerAuthority === true,
        server_authority_wins_conflict_resolution: enrichment.serverAuthorityWinsConflictResolution === true,
        browser_payload_can_override_server: enrichment.browserPayloadCanOverrideServer === true,
        browser_learning_signal: metaSignalBoost.isBrowserLearningSignal === true,
        learning_weight: metaSignalBoost.learningWeight,
        attribution_assist: metaSignalBoost.attributionAssist === true,
        mid_intent_candidate: predictionMarkers.midIntentCandidate === true,
        high_intent_candidate: predictionMarkers.highIntentCandidate === true,
        cta_intent_boost: predictionMarkers.ctaIntentBoost === true,
        conversion_intent_observed: predictionMarkers.conversionIntentObserved === true,
        page_cluster_id: sessionFingerprint.pageClusterId,
        entry_source: sessionFingerprint.entrySource,
        timestamp_bucket: sessionFingerprint.timestampBucket
      };

      if (Number.isFinite(stepNumber)) payload.funnel_step = stepNumber;
      if (stepName) payload.step_name = stepName;
      return payload;
    }

    function hasHumanBehaviorForMetaBrowserEvent(eventName) {
      if (eventName === 'ViewContent') return false;

      return Boolean(
        state.stayed5Seconds ||
        state.meaningfulScroll ||
        state.firstQuestionAnswered ||
        state.completedSteps['1'] ||
        state.recommendationViewed ||
        state.contactStepReached ||
        state.contactInputStarted ||
        state.submitAttempted ||
        state.leadSubmitted
      );
    }

    function fireBrowserPixel(eventName, eventId, pixelPayload) {
      if (!config.enabled || !config.sendBrowserEvents || !config.browserEventNames.has(eventName) || typeof window.fbq !== 'function') {
        return false;
      }

      if (!hasHumanBehaviorForMetaBrowserEvent(eventName)) {
        return false;
      }

      try {
        window.fbq('trackCustom', eventName, pixelPayload, { eventID: eventId });
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

        function resolveClientContext() {
          function parseBrowser(userAgent) {
            const ua = String(userAgent || '').toLowerCase();
            if (ua.includes('edg/')) return 'edge';
            if (ua.includes('opr/') || ua.includes('opera')) return 'opera';
            if (ua.includes('chrome/') || ua.includes('crios/')) return 'chrome';
            if (ua.includes('firefox/') || ua.includes('fxios/')) return 'firefox';
            if (ua.includes('safari/') || ua.includes('version/')) return 'safari';
            return 'unknown';
          }

          function parseOperatingSystem(userAgent) {
            const ua = String(userAgent || '').toLowerCase();
            if (ua.includes('windows nt')) return 'windows';
            if (ua.includes('iphone') || ua.includes('ipad') || ua.includes('ios')) return 'ios';
            if (ua.includes('android')) return 'android';
            if (ua.includes('mac os x') || ua.includes('macintosh')) return 'macos';
            if (ua.includes('linux')) return 'linux';
            return 'unknown';
          }

          function parseDeviceType(userAgent) {
            const ua = String(userAgent || '').toLowerCase();
            const width = window.innerWidth || document.documentElement?.clientWidth || 0;
            if (ua.includes('ipad') || ua.includes('tablet')) return 'tablet';
            if (ua.includes('mobi') || ua.includes('iphone') || ua.includes('android')) return 'mobile';
            if (width > 0 && width < 768) return 'mobile';
            return 'desktop';
          }

          const fallbackUserAgent = navigator.userAgent || '';
          const fallback = {
            deviceType: parseDeviceType(fallbackUserAgent),
            browser: parseBrowser(fallbackUserAgent),
            operatingSystem: parseOperatingSystem(fallbackUserAgent),
            userAgent: fallbackUserAgent,
            viewportWidth: window.innerWidth || document.documentElement?.clientWidth || null,
            viewportHeight: window.innerHeight || document.documentElement?.clientHeight || null,
            screenWidth: window.screen?.width || null,
            screenHeight: window.screen?.height || null,
            webDriver: navigator.webdriver === true,
            isHeadless: /headless|phantomjs|selenium|puppeteer|playwright/i.test(fallbackUserAgent),
            language: navigator.language || null,
            timeZone: Intl.DateTimeFormat?.().resolvedOptions?.().timeZone || null
          };

          try {
            const api = window.LegendAnalytics;
            if (!api || typeof api.getClientContext !== 'function') {
              return fallback;
            }

            const context = api.getClientContext();
            if (!context || typeof context !== 'object') {
              return fallback;
            }

            return {
              ...fallback,
              ...context,
              deviceType: context.deviceType || fallback.deviceType,
              browser: context.browser || fallback.browser,
              operatingSystem: context.operatingSystem || fallback.operatingSystem,
              userAgent: context.userAgent || fallback.userAgent
            };
          } catch {
            return fallback;
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
      const clientContext = resolveClientContext();
      const attribution = resolveAttribution();
      const fbp = ensureFbpCookie();
      const fbc = ensureFbcCookie(attribution);
      attribution.fbp = attribution.fbp || fbp || '';
      attribution.fbc = attribution.fbc || fbc || '';

      const enrichedMetadata = Object.assign(
        {},
        metadata,
        buildLearningEnrichment(eventName, score, clientContext, attribution, metadata)
      );
      const browserPixelPayload = buildPixelPayload(stepNumber, stepName, score, enrichedMetadata, attribution);
      const browserEventSent = fireBrowserPixel(eventName, eventId, browserPixelPayload);
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
        clientContext,
        attribution,
        metadata: enrichedMetadata
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
      const highIntentSatisfied =
        !state.submitted &&
        !state.rapidBounce &&
        score.totalSignalScore >= config.highIntentThreshold &&
        (
          state.recommendationViewed ||
          state.contactStepReached ||
          state.contactInputStarted ||
          state.requiredContactFieldsCompleted ||
          state.submitAttempted
        );

      if (highIntentSatisfied && !state.fired['threshold-high-intent']) {
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
        (!config.leadSignalRules.leadReadyRequiresContactStep || state.contactStepReached) &&
        hasLeadReadyContactData();

      if (leadReadySatisfied && !state.fired['threshold-lead-ready']) {
        void emitSignal('LeadReadySignal', {
          onceKey: 'threshold-lead-ready',
          metadata: {
            requiredContactFieldsComplete: state.requiredContactFieldsCompleted,
            leadReadyRules: config.leadSignalRules
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

      const shouldTrackAbandon =
        state.recommendationViewed ||
        state.contactStepReached ||
        state.contactInputStarted ||
        state.requiredContactFieldsCompleted ||
        state.submitAttempted;

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

    function getFormFieldValue(fieldName) {
      if (!(form instanceof HTMLFormElement)) return '';
      const el = form.querySelector(`[name="${fieldName}"]`);
      if (!(el instanceof HTMLInputElement || el instanceof HTMLSelectElement || el instanceof HTMLTextAreaElement)) {
        return '';
      }

      return asTrimmed(el.value);
    }

    function hasLeadReadyContactData() {
      if (!allRequiredContactFieldsComplete()) {
        return false;
      }

      if (config.leadSignalRules.leadReadyRequiresValidPhone) {
        const phoneDigits = getFormFieldValue('Phone').replace(/\D/g, '');
        if (phoneDigits.length < 10) {
          return false;
        }
      }

      if (config.leadSignalRules.leadReadyRequiresValidEmail) {
        const email = getFormFieldValue('Email');
        if (!email || !email.includes('@')) {
          return false;
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

        el.addEventListener('focus', () => {
          registerInteraction();
          registerFormFieldEngagement(fieldName);
        });

        const eventName = el instanceof HTMLSelectElement ? 'change' : 'input';
        el.addEventListener(eventName, () => {
          registerInteraction();
          const value = asTrimmed(el.value);
          if (value && !state.fired['contact-input-started']) {
            registerFormFieldEngagement(fieldName);
            void emitSignal('ContactInputStarted', {
              onceKey: 'contact-input-started',
              metadata: {
                contactInputStarted: true,
                fieldName,
                conversionIntentObserved: true
              }
            });
          } else if (value) {
            registerFormFieldEngagement(fieldName);
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
      const onScroll = () => {
        const doc = document.documentElement;
        const body = document.body;
        const fullHeight = Math.max(doc.scrollHeight, body ? body.scrollHeight : 0);
        const viewport = window.innerHeight || doc.clientHeight || 0;
        const maxScrollable = Math.max(1, fullHeight - viewport);
        const scrolled = Math.max(window.scrollY || window.pageYOffset || doc.scrollTop || 0, 0);
        const percent = Math.min(100, Math.round((scrolled / maxScrollable) * 100));
        updateScrollSignals(percent);

        if (percent >= MEANINGFUL_SCROLL_THRESHOLD && !state.fired['meaningful-scroll']) {
          void emitSignal('MeaningfulScroll', {
            onceKey: 'meaningful-scroll',
            metadata: {
              scrollPercent: percent,
              midIntentCandidate: percent >= MID_INTENT_SCROLL_THRESHOLD,
              highIntentCandidate: percent >= HIGH_INTENT_SCROLL_THRESHOLD
            }
          });
        }

        if (percent >= HIGH_INTENT_SCROLL_THRESHOLD && state.fired['meaningful-scroll']) {
          window.removeEventListener('scroll', onScroll);
        }
      };

      window.addEventListener('scroll', onScroll, { passive: true });
    }

    function wireGeneralInteractionTracking() {
      document.addEventListener('pointerdown', registerInteraction, { passive: true });
      document.addEventListener('touchstart', registerInteraction, { passive: true });
      document.addEventListener('keydown', registerInteraction);
    }

    function wireCtaHoverTracking() {
      document.addEventListener('mouseover', (event) => {
        const target = event.target instanceof Element
          ? event.target.closest(CTA_SELECTOR)
          : null;
        if (!(target instanceof HTMLElement)) return;

        const hoverKey =
          target.dataset.primaryCta ||
          target.dataset.cta ||
          target.dataset.lifeFunnelStart ||
          target.id ||
          target.getAttribute('name') ||
          asTrimmed(target.textContent) ||
          target.tagName.toLowerCase();

        if (!hoverKey) return;

        const now = Date.now();
        if (
          state.lastCtaHoverKey === hoverKey &&
          (now - Number(state.lastCtaHoverAt || 0)) < 800
        ) {
          return;
        }

        state.lastCtaHoverKey = hoverKey;
        state.lastCtaHoverAt = now;
        state.ctaHoverCount = Number(state.ctaHoverCount || 0) + 1;
        if (state.ctaHoverCount >= CTA_HOVER_THRESHOLD) {
          state.ctaIntentBoost = true;
        }

        registerInteraction();
        saveState();
      }, { passive: true });
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
      wireGeneralInteractionTracking();
      wireCtaHoverTracking();
      wireDisabledClickTracking();
      scheduleEngagementTimers();

      if (!state.fired['view-content']) {
        ensureEarlyMetaCookies();
        void emitSignal('ViewContent', { onceKey: 'view-content' });
      }

      window.addEventListener('pagehide', maybeTrackAbandon);

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
        ensureEarlyMetaCookies();
        return emitSignal('LeadFormStart', {
          onceKey: 'lead-form-start',
          metadata
        });
      },
      trackDiscoveryComplete(metadata = {}) {
        return emitSignal('DiscoveryComplete', {
          onceKey: 'discovery-complete',
          stepNumber: 1,
          stepName: 'discovery_complete',
          metadata
        });
      },
      trackStepComplete(stepNumber, stepName, metadata = {}) {
        const normalizedStepName = asTrimmed(stepName).toLowerCase();
        if (Number(stepNumber) === 1 || normalizedStepName === 'discovery_complete') {
          return emitSignal('DiscoveryComplete', {
            onceKey: 'discovery-complete',
            stepNumber: 1,
            stepName: 'discovery_complete',
            metadata
          });
        }

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
        const clientContext = resolveClientContext();
        const attribution = resolveAttribution();
        const enrichment = buildLearningEnrichment('ViewContent', score, clientContext, attribution, null);
        return {
          sessionId: state.sessionId,
          visitorId: state.visitorId,
          quoteType: state.quoteType,
          pageClusterId: state.pageClusterId,
          entrySource: state.entrySource,
          score,
          enrichment
        };
      }
    };
  }

  window.metaSignalIntelligence = {
    createLandingSession
  };
})();
