(() => {
  const modal = document.getElementById('leadModal');
  if (!modal) return;

  const form = document.getElementById('leadForm');
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

  const tracking = window.legendTrack || (() => {});
  const ids = window.legendTrackingIds || {};

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
    pendingCta = options.ctaKey || null;
    pendingHref = options.href || null;
    interestSelect.value = options.interest || interestSelect.value || '';
    errorEl.classList.remove('show');
    successEl.classList.remove('show');
    form.reset();
    if (interestSelect.value === '') {
      interestSelect.value = options.interest || '';
    }
    formStarted = false;
    modal.classList.add('open');
    modal.setAttribute('aria-hidden', 'false');
    form.querySelector('input[name="FirstName"]')?.focus();
    sessionStorage.setItem(SOFT_FLAG, '1');
    tracking({
      EventType: 'lead_modal_open',
      PageKey: pageKey,
      ElementKey: pendingCta || 'unknown_modal_open'
    });
  }

  function closeModal(redirect) {
    modal.classList.remove('open');
    modal.setAttribute('aria-hidden', 'true');
    if (redirect && pendingHref) {
      window.location.href = pendingHref;
    }
  }

  function markFormStart() {
    if (formStarted) return;
    formStarted = true;
    tracking({
      EventType: 'lead_form_start',
      PageKey: pageKey,
      ElementKey: pendingCta || 'unknown_modal_open',
      FormKey: 'lead_modal_form'
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
          EventType: 'lead_form_submit_failed',
          PageKey: pageKey,
          ElementKey: pendingCta || 'lead_modal',
          FormKey: 'lead_modal_form'
        });
        return;
      }
    }
    if (!getField('TermsAccepted')) {
      errorEl.textContent = 'Please accept the terms.';
      errorEl.classList.add('show');
      tracking({
        EventType: 'lead_form_submit_failed',
        PageKey: pageKey,
        ElementKey: pendingCta || 'lead_modal',
        FormKey: 'lead_modal_form'
      });
      return;
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
      TermsAccepted: !!getField('TermsAccepted'),
      SourcePageKey: pageKey,
      SourceCtaKey: pendingCta,
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
      if (!res.ok) throw new Error('Submit failed');
      successEl.classList.add('show');
      tracking({
        EventType: 'lead_form_submit_success',
        PageKey: pageKey,
        ElementKey: pendingCta || 'lead_modal',
        FormKey: 'lead_modal_form',
        SubmitOutcome: 'success'
      });
      setTimeout(() => {
        closeModal();
        successEl.classList.remove('show');
        if (pendingHref) {
          window.location.href = pendingHref;
        }
      }, 1400);
    } catch (err) {
      errorEl.textContent = 'We could not send this right now. Please try again.';
      errorEl.classList.add('show');
      tracking({
        EventType: 'lead_form_submit_failed',
        PageKey: pageKey,
        ElementKey: pendingCta || 'lead_modal',
        FormKey: 'lead_modal_form',
        SubmitOutcome: 'error'
      });
    } finally {
      submitBtn.disabled = false;
      submitBtn.textContent = 'Send';
    }
  });

  function wireClose(el, redirect) {
    el?.addEventListener('click', () => {
      closeModal(redirect);
    });
  }
  wireClose(closeBtn, false);
  // Dismiss should only close; no redirect.
  wireClose(dismissBtn, false);
  modal.addEventListener('click', (e) => {
    if (e.target === modal) closeModal();
  });
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && modal.classList.contains('open')) closeModal();
  });
})();
