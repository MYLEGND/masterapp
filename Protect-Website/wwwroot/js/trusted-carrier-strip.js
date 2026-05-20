(function () {
  const SESSION_KEY_PREFIX = 'carrier-trust-strip-viewed:';

  function asTrimmed(value) {
    return typeof value === 'string' ? value.trim() : '';
  }

  function buildStorageKey(element) {
    const explicitKey = asTrimmed(element.dataset.stripKey);
    if (explicitKey) {
      return `${SESSION_KEY_PREFIX}${explicitKey}`;
    }

    return `${SESSION_KEY_PREFIX}${[
      element.dataset.placement,
      element.dataset.mode,
      element.dataset.pageKey,
      element.dataset.quoteType,
      element.dataset.variant
    ].map(asTrimmed).join(':')}`;
  }

  function readAttribution() {
    const params = new URLSearchParams(window.location.search);
    const tracker = window.legendTrackingIds;
    const attribution = tracker && typeof tracker.getAttribution === 'function'
      ? (tracker.getAttribution() || {})
      : {};

    return {
      utmSource: asTrimmed(attribution.utmSource || params.get('utm_source') || ''),
      utmMedium: asTrimmed(attribution.utmMedium || params.get('utm_medium') || ''),
      utmCampaign: asTrimmed(attribution.utmCampaign || params.get('utm_campaign') || ''),
      hasFbclid: Boolean(asTrimmed(attribution.fbclid || params.get('fbclid') || ''))
    };
  }

  function markSeen(element) {
    const storageKey = buildStorageKey(element);
    try {
      sessionStorage.setItem(storageKey, '1');
    } catch {
      // Ignore storage failures.
    }
  }

  function hasSeen(element) {
    const storageKey = buildStorageKey(element);
    try {
      return sessionStorage.getItem(storageKey) === '1';
    } catch {
      return false;
    }
  }

  function trackVisible(element) {
    if (!element || hasSeen(element)) {
      return;
    }

    markSeen(element);

    if (typeof window.legendTrack !== 'function') {
      return;
    }

    const attribution = readAttribution();

    window.legendTrack({
      EventType: 'carrier_trust_strip_view',
      PageKey: asTrimmed(element.dataset.pageKey),
      Offer: asTrimmed(element.dataset.quoteType),
      MetadataJson: JSON.stringify({
        placement: asTrimmed(element.dataset.placement),
        mode: asTrimmed(element.dataset.mode),
        pageKey: asTrimmed(element.dataset.pageKey),
        quoteType: asTrimmed(element.dataset.quoteType),
        variant: asTrimmed(element.dataset.variant),
        trafficType: asTrimmed(element.dataset.trafficType),
        utmSource: attribution.utmSource,
        utmMedium: attribution.utmMedium,
        utmCampaign: attribution.utmCampaign,
        hasFbclid: attribution.hasFbclid
      })
    });
  }

  function observeElements(elements) {
    if (!elements.length) {
      return;
    }

    if (!('IntersectionObserver' in window)) {
      elements.forEach(trackVisible);
      return;
    }

    const observer = new IntersectionObserver((entries) => {
      entries.forEach((entry) => {
        if (!entry.isIntersecting) {
          return;
        }

        trackVisible(entry.target);
        observer.unobserve(entry.target);
      });
    }, {
      threshold: 0.35
    });

    elements.forEach((element) => observer.observe(element));
  }

  function init(root) {
    const scope = root instanceof Element || root instanceof Document ? root : document;
    const elements = Array.from(scope.querySelectorAll('[data-carrier-trust-strip]'))
      .filter((element) => !element.dataset.carrierTrustInitialized);

    elements.forEach((element) => {
      element.dataset.carrierTrustInitialized = 'true';
    });

    observeElements(elements);
  }

  window.trustedCarrierStrip = Object.freeze({
    init
  });

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', () => init(document), { once: true });
  } else {
    init(document);
  }
})();
