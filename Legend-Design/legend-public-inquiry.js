(() => {
  'use strict';

  if (new URLSearchParams(location.search).has('legendEdit')) return;

  const context = window.LEGEND_PUBLIC_CMS_CONTEXT || {};
  const configuredBase = typeof context.apiBase === 'string' ? context.apiBase.trim() : '';
  let apiBase = location.origin;
  try {
    if (configuredBase) {
      const candidate = new URL(configuredBase);
      if (candidate.protocol === 'https:') apiBase = candidate.origin;
    }
  } catch {}

  const fixedEndpoint = new URL('/api/website-inquiries/public', apiBase).toString();
  const customEndpoint = new URL('/api/website-inquiries/public/form', apiBase).toString();
  const submissionState = new WeakMap();

  function cookie(name) {
    return document.cookie.split(';').map(value => value.trim())
      .find(value => value.startsWith(name + '='))?.slice(name.length + 1) || null;
  }

  function attributionValues() {
    const analytics = window.LegendAnalytics;
    const attribution = analytics?.ids?.getAttribution?.() || {};
    let sourceActionKey = window.LEGEND_LAST_WEBSITE_ACTION_KEY || null;
    try { sourceActionKey ||= sessionStorage.getItem('legend_last_website_action_key'); } catch {}
    return {
      sourcePath: location.pathname,
      sourceActionKey,
      sessionId: analytics?.ids?.getSessionId?.() || null,
      visitorId: analytics?.ids?.getVisitorId?.() || null,
      utmSource: attribution.utmSource || null,
      utmMedium: attribution.utmMedium || null,
      utmCampaign: attribution.utmCampaign || null,
      utmId: attribution.utmId || null,
      utmTerm: attribution.utmTerm || null,
      utmContent: attribution.utmContent || null,
      fbclid: attribution.fbclid || null,
      fbp: cookie('_fbp'),
      fbc: cookie('_fbc'),
      metaCampaignId: attribution.metaCampaignId || null,
      metaAdSetId: attribution.metaAdSetId || null,
      metaAdId: attribution.metaAdId || null
    };
  }

  function fieldsetControls(fieldset) {
    return [...fieldset.querySelectorAll('input,select,textarea')].filter(control => !control.disabled);
  }

  function validateStep(fieldset) {
    for (const control of fieldsetControls(fieldset)) {
      if (!control.checkValidity()) {
        control.reportValidity?.();
        control.focus?.();
        return false;
      }
    }
    return true;
  }

  function showStep(form, nextIndex) {
    const steps = [...form.querySelectorAll('[data-form-step]')];
    if (!steps.length) return;
    const index = Math.max(0, Math.min(steps.length - 1, nextIndex));
    steps.forEach((step, position) => { step.hidden = position !== index; });
    const target = steps[index].querySelector('input,select,textarea,button');
    target?.focus?.({ preventScroll: true });
  }

  document.addEventListener('click', event => {
    const next = event.target.closest?.('[data-form-next]');
    const back = event.target.closest?.('[data-form-back]');
    const button = next || back;
    if (!button) return;
    const form = button.closest('form[data-website-custom-form]');
    if (!form || form.hasAttribute('data-preview')) return;
    const steps = [...form.querySelectorAll('[data-form-step]')];
    const current = steps.findIndex(step => !step.hidden);
    if (current < 0) return;
    event.preventDefault();
    if (next && !validateStep(steps[current])) return;
    showStep(form, current + (next ? 1 : -1));
  });

  function fixedPayload(form) {
    const fields = new FormData(form);
    return {
      firstName: String(fields.get('FirstName') || ''),
      lastName: String(fields.get('LastName') || ''),
      phone: String(fields.get('Phone') || ''),
      email: String(fields.get('Email') || ''),
      message: String(fields.get('Message') || ''),
      consent: fields.get('consent') === 'on',
      ...attributionValues()
    };
  }

  function customPayload(form) {
    const fields = {};
    form.querySelectorAll('[data-form-field-id]').forEach(control => {
      if (!control.matches('input,select,textarea')) return;
      const id = control.dataset.formFieldId;
      if (!id) return;
      fields[id] = control.type === 'checkbox'
        ? (control.checked ? 'true' : 'false')
        : String(control.value || '');
    });
    return {
      formDefinitionId: form.dataset.formDefinitionId || '',
      fields,
      consent: form.querySelector('input[name="__consent"]')?.checked === true,
      ...attributionValues()
    };
  }

  async function submitForm(form, endpoint, values) {
    let state = submissionState.get(form) || { submissionId: null, fingerprint: null };
    const fingerprint = JSON.stringify(values);
    if (state.fingerprint !== fingerprint) {
      state = { submissionId: crypto.randomUUID(), fingerprint };
      submissionState.set(form, state);
    }

    const button = form.querySelector('[type="submit"]');
    const status = form.querySelector('[data-form-status],[role="status"]');
    if (button) button.disabled = true;
    if (status) status.textContent = 'Sending your inquiry…';

    try {
      const response = await fetch(endpoint, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ submissionId: state.submissionId, ...values })
      });
      const result = await response.json().catch(() => null);
      if (!response.ok || !result?.accepted) throw new Error(result?.message || 'inquiry_failed');

      if (status) status.textContent = form.dataset.successMessage || 'Your inquiry has been sent.';
      window.LEGEND_PUBLIC_META_SESSION?.markSubmitted?.({
        websiteLeadSaved: true,
        sourceActionKey: values.sourceActionKey || null
      });
      form.reset();
      submissionState.delete(form);
      if (form.matches('[data-website-custom-form]')) showStep(form, 0);
    } catch {
      if (status) status.textContent = 'Your inquiry could not be confirmed. Please try again.';
    } finally {
      if (button) button.disabled = false;
    }
  }

  document.addEventListener('submit', event => {
    const form = event.target;
    if (!(form instanceof HTMLFormElement) || form.hasAttribute('data-preview')) return;
    const custom = form.matches('[data-website-custom-form]');
    const fixed = form.matches('[data-website-inquiry]');
    if (!custom && !fixed) return;

    event.preventDefault();
    if (!form.reportValidity()) return;

    if (custom) {
      const steps = [...form.querySelectorAll('[data-form-step]')];
      if (steps.some(step => !validateStep(step))) return;
      void submitForm(form, customEndpoint, customPayload(form));
    } else {
      void submitForm(form, fixedEndpoint, fixedPayload(form));
    }
  }, true);
})();
