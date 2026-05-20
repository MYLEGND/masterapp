(function () {
  const SESSION_KEY_PREFIX = 'carrier-trust-strip-viewed:';
  const FULL_PIXELS_PER_SECOND = 30;
  const COMPACT_PIXELS_PER_SECOND = 32;
  const DRAG_INTENT_THRESHOLD = 6;

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

  function queueMarqueeSync(element) {
    if (!element || element.dataset.carrierTrustMarqueeQueued === 'true') {
      return;
    }

    element.dataset.carrierTrustMarqueeQueued = 'true';
    window.requestAnimationFrame(() => {
      element.dataset.carrierTrustMarqueeQueued = 'false';
      syncMarqueeMetrics(element);
    });
  }

  function syncMarqueeMetrics(element) {
    if (!element) {
      return;
    }

    const track = element.querySelector('.lq-carrier-strip-track');
    const primaryGroup = element.querySelector('.lq-carrier-strip-group');
    if (!(track instanceof HTMLElement) || !(primaryGroup instanceof HTMLElement)) {
      return;
    }

    const trackStyles = window.getComputedStyle(track);
    const gapValue = trackStyles.columnGap || trackStyles.gap || '0';
    const trackGap = Number.parseFloat(gapValue) || 0;
    const groupWidth = primaryGroup.getBoundingClientRect().width;
    const distance = groupWidth + trackGap;
    if (!Number.isFinite(distance) || distance <= 0) {
      return;
    }

    const pixelsPerSecond = element.classList.contains('lq-carrier-strip-compact')
      ? COMPACT_PIXELS_PER_SECOND
      : FULL_PIXELS_PER_SECOND;
    const duration = Math.max(26, distance / pixelsPerSecond);

    element.style.setProperty('--carrier-marquee-distance', `${distance.toFixed(2)}px`);
    element.style.setProperty('--carrier-marquee-duration', `${duration.toFixed(2)}s`);
  }

  function isScrollableViewport(viewport) {
    if (!(viewport instanceof HTMLElement)) {
      return false;
    }

    return (viewport.scrollWidth - viewport.clientWidth) > 8;
  }

  function bindTouchDragScroll(element) {
    if (!element || element.dataset.carrierTrustTouchBound === 'true') {
      return;
    }

    const viewport = element.querySelector('.lq-carrier-strip-viewport');
    if (!(viewport instanceof HTMLElement)) {
      return;
    }

    element.dataset.carrierTrustTouchBound = 'true';

    const dragState = {
      active: false,
      dragging: false,
      startX: 0,
      startY: 0,
      startScrollLeft: 0
    };

    const resetDragState = () => {
      dragState.active = false;
      dragState.dragging = false;
      dragState.startX = 0;
      dragState.startY = 0;
      dragState.startScrollLeft = 0;
      viewport.classList.remove('is-dragging');
    };

    viewport.addEventListener('touchstart', (event) => {
      if (event.touches.length !== 1 || !isScrollableViewport(viewport)) {
        resetDragState();
        return;
      }

      const touch = event.touches[0];
      dragState.active = true;
      dragState.dragging = false;
      dragState.startX = touch.clientX;
      dragState.startY = touch.clientY;
      dragState.startScrollLeft = viewport.scrollLeft;
    }, { passive: true });

    viewport.addEventListener('touchmove', (event) => {
      if (!dragState.active || event.touches.length !== 1) {
        return;
      }

      const touch = event.touches[0];
      const deltaX = touch.clientX - dragState.startX;
      const deltaY = touch.clientY - dragState.startY;

      if (!dragState.dragging) {
        if (Math.abs(deltaX) < DRAG_INTENT_THRESHOLD && Math.abs(deltaY) < DRAG_INTENT_THRESHOLD) {
          return;
        }

        if (Math.abs(deltaX) <= Math.abs(deltaY)) {
          resetDragState();
          return;
        }

        dragState.dragging = true;
        viewport.classList.add('is-dragging');
      }

      event.preventDefault();
      viewport.scrollLeft = dragState.startScrollLeft - deltaX;
    }, { passive: false });

    viewport.addEventListener('touchend', resetDragState, { passive: true });
    viewport.addEventListener('touchcancel', resetDragState, { passive: true });
  }

  function bindMarquee(element) {
    if (!element || element.dataset.carrierTrustMarqueeBound === 'true') {
      return;
    }

    element.dataset.carrierTrustMarqueeBound = 'true';

    const viewport = element.querySelector('.lq-carrier-strip-viewport');
    const primaryGroup = element.querySelector('.lq-carrier-strip-group');
    const scheduleSync = () => queueMarqueeSync(element);

    bindTouchDragScroll(element);

    if ('ResizeObserver' in window) {
      const observer = new ResizeObserver(() => scheduleSync());
      if (viewport instanceof HTMLElement) {
        observer.observe(viewport);
      }
      if (primaryGroup instanceof HTMLElement) {
        observer.observe(primaryGroup);
      }
      element._carrierTrustResizeObserver = observer;
    }

    element.querySelectorAll('img').forEach((image) => {
      image.draggable = false;
      image.addEventListener('load', () => scheduleSync(), { passive: true });
      image.addEventListener('error', () => scheduleSync(), { passive: true });
    });

    if (document.fonts?.ready) {
      document.fonts.ready.then(() => scheduleSync()).catch(() => {});
    }

    window.setTimeout(() => scheduleSync(), 140);
    window.setTimeout(() => scheduleSync(), 360);
    window.setTimeout(() => scheduleSync(), 900);
    scheduleSync();
  }

  function init(root) {
    const scope = root instanceof Element || root instanceof Document ? root : document;
    const elements = Array.from(scope.querySelectorAll('[data-carrier-trust-strip]'))
      .filter((element) => !element.dataset.carrierTrustInitialized);

    elements.forEach((element) => {
      element.dataset.carrierTrustInitialized = 'true';
      bindMarquee(element);
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
