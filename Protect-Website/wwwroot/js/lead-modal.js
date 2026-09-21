(() => {
  if (window.__legendLeadModalInitialized) {
    return;
  }
  window.__legendLeadModalInitialized = true;

  const modal = document.getElementById('leadModal');
  if (!modal) return;

  const DEBUG_LEAD_MODAL =
    window.location.hostname === 'localhost' ||
    window.location.hostname === '127.0.0.1' ||
    new URLSearchParams(window.location.search).has('trackingDebug');

  const form = document.getElementById('leadForm');
  const dialog = modal.querySelector('.lead-card');
  const closeBtn = document.getElementById('leadClose');
  const dismissBtn = document.getElementById('leadDismiss');
  const submitBtn = document.getElementById('leadSubmit');
  const errorEl = document.getElementById('leadError');
  const successEl = document.getElementById('leadSuccess');
  const interestSelect = document.getElementById('leadInterest');
  const pageKey = document.body.dataset.pageKey || '';
  const INGEST_URL = '/api/lead/submit';
  const AGENT_ID = window.AGENT_TRACKING_PROFILE_ID || null;
  const AGENT_SLUG = window.AGENT_TRACKING_SLUG || null;
  const SOFT_FLAG = 'lead_soft_seen';

  let pendingCta = null;
  let pendingHref = null;
  let formStarted = false;
  let modalInstanceId = null;
  let modalCloseTracked = false;
  let returnFocus = null;

  const tracking = window.LegendAnalytics?.track || window.legendTrack || (() => {});
  const ids = window.legendTrackingIds || {};

  function debug(message, details) {
    if (!DEBUG_LEAD_MODAL) return;
    try {
      console.debug('[legend-lead-modal]', message, details || '');
    } catch {
      // Ignore console failures.
    }
  }

  function uuid() {
    return crypto.randomUUID ? crypto.randomUUID() : ([1e7]+-1e3+-4e3+-8e3+-1e11).replace(/[018]/g,c=>(c ^ crypto.getRandomValues(new Uint8Array(1))[0] & 15 >> c / 4).toString(16));
  }

  function resolveElementKey(ctaKey) {
    const explicit = typeof ctaKey === 'string' ? ctaKey.trim() : '';
    if (explicit) return explicit;
    if (pageKey) return `${pageKey}_lead_modal`;
    return 'lead_modal_direct';
  }

  function setField(name, value) {
    const el = form.querySelector(`[name="${name}"]`);
    if (el) el.value = value || '';
  }

  function getField(name) {
    const el = form.querySelector(`[name="${name}"]`);
    if (!el) return '';
    if (el.type === 'checkbox') return el.checked;
    return el.value.trim();
  }

  function openModal(options = {}) {
    if (modal.classList.contains('open')) {
      debug('ignored duplicate open while already visible', {
        pageKey,
        ctaKey: options.ctaKey || null
      });
      return;
    }

    pendingCta = resolveElementKey(options.ctaKey);
    pendingHref = options.href || null;
    interestSelect.value = options.interest || interestSelect.value || '';
    errorEl.classList.remove('show');
    successEl.classList.remove('show');
    form.reset();
    if (interestSelect.value === '') {
      interestSelect.value = options.interest || '';
    }
    formStarted = false;
    modalInstanceId = uuid();
    modalCloseTracked = false;
    returnFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    modal.classList.add('open');
    modal.setAttribute('aria-hidden', 'false');
    document.body.classList.add('no-scroll');
    dialog?.focus({ preventScroll: true });
    sessionStorage.setItem(SOFT_FLAG, '1');
    debug('modal open', {
      pageKey,
      elementKey: pendingCta,
      modalInstanceId
    });
    tracking({
      EventType: 'lead_modal_open',
      PageKey: pageKey,
      ElementKey: pendingCta,
      FormKey: 'lead_modal_form',
      MetadataJson: JSON.stringify({
        modalInstanceId
      })
    });
  }

  function closeModal(redirect, reason = 'dismiss') {
    if (!modal.classList.contains('open')) {
      return;
    }

    modal.classList.remove('open');
    modal.setAttribute('aria-hidden', 'true');
    document.body.classList.remove('no-scroll');
    if (!modalCloseTracked) {
      modalCloseTracked = true;
      debug('modal close', {
        pageKey,
        elementKey: pendingCta,
        modalInstanceId,
        reason
      });
      tracking({
        EventType: 'lead_modal_close',
        PageKey: pageKey,
        ElementKey: pendingCta || resolveElementKey(null),
        FormKey: 'lead_modal_form',
        MetadataJson: JSON.stringify({
          modalInstanceId,
          reason,
          formStarted,
          redirected: !!(redirect && pendingHref)
        })
      });
    }
    if (redirect && pendingHref) {
      window.location.href = pendingHref;
    }
    const focusTarget = returnFocus;
    pendingCta = null;
    pendingHref = null;
    formStarted = false;
    modalInstanceId = null;
    returnFocus = null;
    if (!redirect && focusTarget?.isConnected) {
      focusTarget.focus({ preventScroll: true });
    }
  }

  function markFormStart() {
    if (formStarted) return;
    formStarted = true;
    debug('lead form start', {
      pageKey,
      elementKey: pendingCta,
      modalInstanceId
    });
    tracking({
      clientContext: window.LegendAnalytics?.getClientContext?.() || {},
      EventType: 'lead_form_start',
      PageKey: pageKey,
      ElementKey: pendingCta || resolveElementKey(null),
      FormKey: 'lead_modal_form',
      MetadataJson: JSON.stringify({
        modalInstanceId
      })
    });
  }

  // Soft scroll trigger on home once per session
  if (pageKey === 'home' && !sessionStorage.getItem(SOFT_FLAG)) {
    const onScroll = () => {
      const scrolled = window.scrollY + window.innerHeight;
      const docHeight = document.documentElement.scrollHeight;
      if (scrolled >= docHeight * 0.5) {
        openModal({ interest: 'assessment', ctaKey: 'soft_scroll_home' });
        window.removeEventListener('scroll', onScroll);
      }
    };
    window.addEventListener('scroll', onScroll, { passive: true });
  }

  // CTA triggers
  document.querySelectorAll('[data-lead-trigger]').forEach(el => {
    el.addEventListener('click', (e) => {
      const ctaKey = el.getAttribute('data-cta') || 'lead_trigger';
      const interest = el.getAttribute('data-lead-interest') || '';
      const href = el.getAttribute('href');
      e.preventDefault();
      openModal({ interest, ctaKey, href });
    });
  });

  // Form interactions
  form.addEventListener('input', markFormStart, { once: true });
  form.addEventListener('focusin', markFormStart, { once: true });

  form.addEventListener('submit', async (e) => {
    e.preventDefault();
    errorEl.classList.remove('show');
    successEl.classList.remove('show');

    const requiredFields = ['FirstName', 'Email', 'InterestType', 'MarketingEmailConsent'];
    for (const field of requiredFields) {
      if (!getField(field)) {
        errorEl.textContent = 'Please fill required fields.';
        errorEl.classList.add('show');
        tracking({
          EventType: 'lead_form_submit_failure',
          PageKey: pageKey,
          ElementKey: pendingCta || resolveElementKey(null),
          FormKey: 'lead_modal_form'
        });
        return;
      }
    }
    submitBtn.disabled = true;
    submitBtn.textContent = 'Sending...';

    const phone = getField('Phone');
    const query = new URLSearchParams(location.search);
    const attribution = (ids.getAttribution && ids.getAttribution()) || {};
    const payload = {
      FirstName: getField('FirstName'),
      LastName: getField('LastName'),
      Email: getField('Email'),
      Phone: phone,
      PreferredContactMethod: getField('PreferredContactMethod'),
      InterestType: getField('InterestType') || 'general',
      Notes: getField('Notes'),
      MarketingEmailConsent: !!getField('MarketingEmailConsent'),
      CallTextConsent: !!getField('MarketingEmailConsent'), // align single consent to both flags for compatibility
      TermsAccepted: !!getField('MarketingEmailConsent'),
      SourcePageKey: pageKey,
      SourceCtaKey: pendingCta,
      SourcePath: window.location.pathname,
      MetadataJson: JSON.stringify({
        flow: 'lead_modal',
        modalInstanceId,
        sourcePath: window.location.pathname
      }),
      SessionId: (ids.getSessionId && ids.getSessionId()) || null,
      VisitorId: (ids.getVisitorId && ids.getVisitorId()) || null,
      UtmSource: attribution.utmSource || query.get('utm_source'),
      UtmMedium: attribution.utmMedium || query.get('utm_medium'),
      UtmCampaign: attribution.utmCampaign || query.get('utm_campaign'),
      UtmId: attribution.utmId || query.get('utm_id'),
      UtmTerm: attribution.utmTerm || query.get('utm_term'),
      UtmContent: attribution.utmContent || query.get('utm_content'),
      MetaCampaignId: attribution.metaCampaignId || query.get('meta_campaign_id'),
      MetaAdSetId: attribution.metaAdSetId || query.get('meta_adset_id'),
      MetaAdId: attribution.metaAdId || query.get('meta_ad_id'),
      Fbclid: attribution.fbclid || query.get('fbclid'),
      Host: location.host,
      AgentTrackingProfileId: AGENT_ID,
      AgentSlug: AGENT_SLUG
    };

    try {
      const res = await fetch(INGEST_URL, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json'
        },
        body: JSON.stringify(payload)
      });

      const responseBody = await res.json().catch(() => null);
      if (!res.ok || responseBody?.emailSent !== true) {
        const captured = responseBody?.captured === true;
        const message = captured
          ? 'Your inquiry was saved, but we could not confirm the agent notification. Please use Contact if you need immediate help.'
          : 'We could not send this right now. Please try again.';
        const error = new Error(message);
        error.captured = captured;
        error.details = responseBody;
        throw error;
      }

      successEl.classList.add('show');
      tracking({
        EventType: 'lead_form_submit_success',
        PageKey: pageKey,
        ElementKey: pendingCta || resolveElementKey(null),
        FormKey: 'lead_modal_form',
        SubmitOutcome: 'success'
      });
      setTimeout(() => {
        const redirectTarget = pendingHref;
        closeModal(false, 'submit_success');
        successEl.classList.remove('show');
        if (redirectTarget) {
          window.location.href = redirectTarget;
        }
      }, 1400);
    } catch (err) {
      errorEl.textContent = err?.message || 'We could not send this right now. Please try again.';
      errorEl.classList.add('show');
      tracking({
        EventType: 'lead_form_submit_failure',
        PageKey: pageKey,
        ElementKey: pendingCta || resolveElementKey(null),
        FormKey: 'lead_modal_form',
        SubmitOutcome: 'error',
        MetadataJson: JSON.stringify({
          modalInstanceId,
          captured: err?.captured === true,
          error: err?.details?.error || null
        })
      });
    } finally {
      submitBtn.disabled = false;
      submitBtn.textContent = 'Send';
    }
  });

  function wireClose(el, redirect, reason) {
    el?.addEventListener('click', () => {
      closeModal(redirect, reason || (redirect ? 'redirect' : 'dismiss'));
    });
  }
  wireClose(closeBtn, false, 'close_button');
  // Dismiss should only close; no redirect.
  wireClose(dismissBtn, false, 'dismiss_button');
  modal.addEventListener('click', (e) => {
    if (e.target === modal) closeModal(false, 'backdrop');
  });
  document.addEventListener('keydown', (e) => {
    if (!modal.classList.contains('open')) return;

    if (e.key === 'Escape') {
      e.preventDefault();
      closeModal(false, 'escape_key');
      return;
    }

    if (e.key !== 'Tab' || !dialog) return;
    const focusable = [...dialog.querySelectorAll(
      'a[href],button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled]),[tabindex]:not([tabindex="-1"])'
    )].filter(el => !el.hasAttribute('hidden') && el.getClientRects().length > 0);

    if (focusable.length === 0) {
      e.preventDefault();
      dialog.focus({ preventScroll: true });
      return;
    }

    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (e.shiftKey && document.activeElement === first) {
      e.preventDefault();
      last.focus();
    } else if (!e.shiftKey && document.activeElement === last) {
      e.preventDefault();
      first.focus();
    }
  });
})();
