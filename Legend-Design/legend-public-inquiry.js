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

  const endpoint = new URL('/api/website-inquiries/public', apiBase).toString();

  document.querySelectorAll('[data-website-inquiry]:not([data-preview])').forEach(form => {
    let submissionId = null;
    let pendingPayload = null;

    form.addEventListener('submit', async event => {
      event.preventDefault();
      if (!form.reportValidity()) return;

      // The shared tracker owns browser form telemetry. This AJAX runtime only
      // reports the attempt into that existing lifecycle; it does not emit a
      // separate Lead conversion.
      form._trackSubmitAttempt?.(true, 0);

      const button = form.querySelector('[type="submit"]');
      const status = form.querySelector('[role="status"]');
      const fields = new FormData(form);
      const analytics = window.LegendAnalytics;
      const attribution = analytics?.ids?.getAttribution?.() || {};
      const cookie = name => document.cookie.split(';').map(value => value.trim())
        .find(value => value.startsWith(name + '='))?.slice(name.length + 1) || null;

      let sourceActionKey = window.LEGEND_LAST_WEBSITE_ACTION_KEY || null;
      try { sourceActionKey ||= sessionStorage.getItem('legend_last_website_action_key'); } catch {}

      const values = {
        firstName: String(fields.get('FirstName') || ''),
        lastName: String(fields.get('LastName') || ''),
        phone: String(fields.get('Phone') || ''),
        email: String(fields.get('Email') || ''),
        message: String(fields.get('Message') || ''),
        sourcePath: location.pathname,
        sourceActionKey,
        consent: fields.get('consent') === 'on',
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

      const fingerprint = JSON.stringify(values);
      if (pendingPayload !== fingerprint) {
        submissionId = crypto.randomUUID();
        pendingPayload = fingerprint;
      }

      button.disabled = true;
      status.textContent = 'Sending your inquiry…';
      try {
        const response = await fetch(endpoint, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ submissionId, ...values })
        });
        const result = await response.json().catch(() => null);
        if (!response.ok || !result?.accepted) throw new Error('inquiry_failed');

        status.textContent = 'Your inquiry has been sent.';
        window.LEGEND_PUBLIC_META_SESSION?.markSubmitted?.({
          websiteLeadSaved: true,
          sourceActionKey
        });
        // Server persistence remains the authoritative Lead event. Mark only
        // the browser lifecycle complete so page exit cannot become a false
        // form_abandon signal after a confirmed save.
        window.legendFormTracking?.markSubmitted?.(
          form.dataset.formKey || 'website_inquiry',
          'server_confirmed_inquiry'
        );
        form.reset();
        submissionId = null;
        pendingPayload = null;
      } catch {
        status.textContent = 'Your inquiry could not be confirmed. Please try again.';
      } finally {
        button.disabled = false;
      }
    });
  });
})();
