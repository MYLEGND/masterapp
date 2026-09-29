(() => {
  'use strict';

  const websiteStudioParams = new URLSearchParams(window.location?.search || '');
  if (window.LEGEND_WEBSITE_STUDIO_MODE === true ||
      websiteStudioParams.has('legendEdit') ||
      websiteStudioParams.has('legendMaterialize')) {
    window.__legendOpenAiMeasurementSuppressedForWebsiteStudio = true;
    return;
  }

  const SDK_URL = 'https://bzrcdn.openai.com/sdk/oaiq.min.js';
  const initializedPixels = new Set();
  const configuredPixels = new Map();
  let sdkPromise = null;
  let consentListenerInstalled = false;

  function measurementConsentAllowed() {
    return window.LegendAnalytics?.measurementConsent?.isAllowed?.() === true;
  }

  function queueApi() {
    if (typeof window.oaiq === 'function') return window.oaiq;
    const q = function () { q.q.push(arguments); };
    q.q = [];
    window.oaiq = q;
    return q;
  }

  function loadSdk() {
    if (sdkPromise) return sdkPromise;
    const q = queueApi();
    sdkPromise = new Promise((resolve, reject) => {
      const existing = [...document.scripts].find(script => script.src === SDK_URL);
      if (existing) {
        if (existing.dataset.legendOpenAiLoaded === 'true') resolve();
        else {
          existing.addEventListener('load', resolve, { once: true });
          existing.addEventListener('error', reject, { once: true });
        }
        return;
      }
      const script = document.createElement('script');
      script.async = true;
      script.src = SDK_URL;
      script.addEventListener('load', () => {
        script.dataset.legendOpenAiLoaded = 'true';
        resolve();
      }, { once: true });
      script.addEventListener('error', reject, { once: true });
      document.head.appendChild(script);
    });
    return sdkPromise;
  }

  function cleanPixelId(value) {
    const pixelId = String(value || '').trim();
    return pixelId && pixelId.length <= 200 ? pixelId : '';
  }

  function ensureInitialized(pixelId, debug = false) {
    const q = queueApi();
    const consent = measurementConsentAllowed();
    q('consent', consent);
    if (!consent) return false;

    if (!initializedPixels.has(pixelId)) {
      q('init', { pixelId, debug: debug === true });
      initializedPixels.add(pixelId);
    }
    // Queueing init/measure is synchronous. SDK transport loads independently
    // so a late canonical subscriber can replay the accepted page envelope
    // after initialization without waiting on the network script.
    void loadSdk().catch(() => {});
    return true;
  }

  function installConsentListener() {
    if (consentListenerInstalled) return;
    consentListenerInstalled = true;
    window.addEventListener('legend:measurement-consent-changed', event => {
      const allowed = event?.detail?.allowed === true && measurementConsentAllowed();
      queueApi()('consent', allowed);
      if (!allowed) return;
      for (const [pixelId, debug] of configuredPixels) {
        void ensureInitialized(pixelId, debug);
      }
    });
  }

  async function configure(options) {
    const pixelId = cleanPixelId(options?.pixelId);
    if (!pixelId) return false;

    configuredPixels.set(pixelId, options?.debug === true);
    window.LEGEND_OPENAI_PIXEL_ID = pixelId;
    installConsentListener();
    const initialized = ensureInitialized(pixelId, options?.debug === true);
    window.LegendAnalytics?.subscribe?.(`openai:${pixelId}`, body => trackCanonical(body, pixelId));
    return initialized;
  }

  function setConsent(value) {
    const authority = window.LegendAnalytics?.measurementConsent;
    if (!authority?.set) return false;
    const state = authority.set(value === true, 'openai_compatibility_adapter');
    queueApi()('consent', state?.allowed === true);
    return state?.allowed === true;
  }

  function measure(eventName, eventData, eventId, pixelId) {
    const targetPixel = cleanPixelId(pixelId || window.LEGEND_OPENAI_PIXEL_ID);
    if (!measurementConsentAllowed() || !targetPixel || !initializedPixels.has(targetPixel)) return false;
    const options = eventId ? { event_id: String(eventId) } : undefined;
    queueApi()('measureSingle', targetPixel, eventName, eventData, options);
    return true;
  }

  function trackCanonical(body, pixelId) {
    if (!body || body.IsInternal === true) return false;
    let metadata = {}; try { metadata = JSON.parse(body.MetadataJson || '{}'); } catch {}
    const bindings = metadata.configuredSignalBindings;
    if (Array.isArray(bindings) && bindings.length &&
        !bindings.some(binding => ['destinations', 'meta'].includes(binding.deliveryMode) && !binding.duplicateBinding)) return false;
    if (body.MetaSignal?.metadata?.configuredDeliveryMode === 'analytics') return false;
    const eventId = body.ClientEventId || body.EventId || null;
    switch (body.EventType) {
      case 'page_view':
        return measure(
          'page_viewed',
          {
            type: 'contents',
            contents: [{
              id: body.PageKey || body.Path || 'page',
              name: body.PageKey || body.Path || 'Page',
              content_type: 'page'
            }]
          },
          eventId, pixelId);
      default:
        return false;
    }
  }

  window.LegendOpenAiMeasurement = Object.freeze({
    configure,
    setConsent,
    measure,
    trackCanonical
  });
})();