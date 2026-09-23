(() => {
  'use strict';
  document.querySelectorAll('[data-business-inquiry]:not([data-preview])').forEach(form => {
    let submissionId = null;
    let pendingPayload = null;
    form.addEventListener('submit', async event => {
      event.preventDefault();
      if (!form.reportValidity()) return;
      const button = form.querySelector('[type="submit"]');
      const status = form.querySelector('[role="status"]');
      const fields = new FormData(form);
      const analytics = window.LegendAnalytics;
      const attribution = analytics?.ids?.getAttribution?.() || {};
      const cookie = name => document.cookie.split(';').map(value=>value.trim()).find(value=>value.startsWith(name+'='))?.slice(name.length+1) || null;
      let sourceActionKey = window.LEGEND_LAST_WEBSITE_ACTION_KEY || null;
      try { sourceActionKey ||= sessionStorage.getItem('legend_last_website_action_key'); } catch {}
      const values = {
        name:String(fields.get('name')||''), email:String(fields.get('email')||''), message:String(fields.get('message')||''),
        sourcePath:location.pathname, sourceActionKey, consent:fields.get('consent')==='on',
        sessionId:analytics?.ids?.getSessionId?.() || null, visitorId:analytics?.ids?.getVisitorId?.() || null,
        utmSource:attribution.utmSource||null, utmMedium:attribution.utmMedium||null, utmCampaign:attribution.utmCampaign||null,
        utmId:attribution.utmId||null, utmTerm:attribution.utmTerm||null, utmContent:attribution.utmContent||null,
        fbclid:attribution.fbclid||null, fbp:cookie('_fbp'), fbc:cookie('_fbc'),
        metaCampaignId:attribution.metaCampaignId||null, metaAdSetId:attribution.metaAdSetId||null, metaAdId:attribution.metaAdId||null
      };
      const fingerprint = JSON.stringify(values);
      if (pendingPayload !== fingerprint) { submissionId=crypto.randomUUID(); pendingPayload=fingerprint; }
      button.disabled = true;
      status.textContent = 'Sending your inquiry…';
      try {
        const response = await fetch('/api/website-inquiries/public', {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({submissionId,...values})});
        if (!response.ok || !(await response.json()).accepted) throw new Error('inquiry_failed');
        status.textContent = 'Your inquiry has been sent.';
        window.LEGEND_PUBLIC_META_SESSION?.markSubmitted?.({ websiteLeadSaved: true, sourceActionKey });
        form.reset(); submissionId=null; pendingPayload=null;
      } catch {
        status.textContent = 'Your inquiry could not be confirmed. Please try again.';
      } finally { button.disabled=false; }
    });
  });
})();
