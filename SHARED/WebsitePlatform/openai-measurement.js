(() => {
  'use strict';

  const SDK_URL = 'https://bzrcdn.openai.com/sdk/oaiq.min.js';
  const initializedPixels = new Set();
  let sdkPromise = null;

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

  async function configure(options) {
    const pixelId = cleanPixelId(options?.pixelId);
    if (!pixelId) return false;

    const q = queueApi();
    const consent = options?.consent !== false && navigator.globalPrivacyControl !== true;
    q('consent', consent);
    if (!initializedPixels.has(pixelId)) {
      q('init', { pixelId, debug: options?.debug === true });
      initializedPixels.add(pixelId);
    }
    window.LEGEND_OPENAI_PIXEL_ID = pixelId;
    window.LegendAnalytics?.subscribe?.(`openai:${pixelId}`, body => trackCanonical(body, pixelId));
    await loadSdk().catch(() => {});
    return true;
  }

  function setConsent(value) {
    queueApi()('consent', value === true);
  }

  function measure(eventName, eventData, eventId, pixelId) {
    const targetPixel = cleanPixelId(pixelId || window.LEGEND_OPENAI_PIXEL_ID);
    if (!targetPixel || !initializedPixels.has(targetPixel)) return false;
    const options = eventId ? { event_id: String(eventId) } : undefined;
    queueApi()('measureSingle', targetPixel, eventName, eventData, options);
    return true;
  }

  function trackCanonical(body, pixelId) {
    if (!body || body.IsInternal === true) return false;
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