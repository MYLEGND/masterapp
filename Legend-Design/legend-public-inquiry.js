(() => {
  'use strict';

  if (window.LEGEND_WEBSITE_STUDIO_MODE === true || new URLSearchParams(location.search).has('legendEdit')) return;

  const context = window.LEGEND_PUBLIC_CMS_CONTEXT || {};
  const configuredBase = typeof context.apiBase === 'string' ? context.apiBase.trim() : '';
  let apiBase = location.origin;
  try {
    if (configuredBase) {
      const candidate = new URL(configuredBase);
      if (candidate.protocol === 'https:') apiBase = candidate.origin;
    }
  } catch {}

  function resolveApiBase() {
    if (configuredBase) return apiBase;
    const analyticsEndpoint = window.LEGEND_ANALYTICS_CONFIG?.endpoint;
    try {
      if (typeof analyticsEndpoint === 'string' && analyticsEndpoint.trim()) {
        const candidate = new URL(analyticsEndpoint, location.origin);
        if (candidate.protocol === 'https:' || candidate.origin === location.origin) return candidate.origin;
      }
    } catch {}
    return location.origin;
  }

  function bindInquiryForm(form) {
    if (!form || form.dataset.legendInquiryBound === 'true') return;
    form.dataset.legendInquiryBound = 'true';
    let submissionId = null;
    let pendingPayload = null;
    let pendingFingerprint = null;
    let sending = false;
    let sent = false;
    const button = form.querySelector('[type="submit"]');
    const status = form.querySelector('[role="status"]');
    if (!button) return;
    const originalButtonMarkup = button.innerHTML;
    if (status) status.dataset.legendInquiryAnnouncement = 'true';

    function showState(state, label, announcement) {
      button.dataset.legendInquiryState = state;
      button.textContent = label;
      button.setAttribute('aria-busy', state === 'sending' ? 'true' : 'false');
      if (status) status.textContent = announcement;
    }
    function restoreOnEdit() {
      if (sending || (button.dataset.legendInquiryState !== 'sent' && button.dataset.legendInquiryState !== 'error')) return;
      sent = false;
      button.disabled = false;
      button.removeAttribute('data-legend-inquiry-state');
      button.removeAttribute('aria-busy');
      button.innerHTML = originalButtonMarkup;
      if (status) status.textContent = '';
    }
    form.addEventListener('input', restoreOnEdit);
    form.addEventListener('change', restoreOnEdit);

    form.addEventListener('submit', async event => {
      event.preventDefault();
      if (sending || sent || !form.reportValidity()) return;

      // The shared tracker owns browser form telemetry. This AJAX runtime only
      // reports the attempt into that existing lifecycle; it does not emit a
      // separate Lead conversion.
      form._trackSubmitAttempt?.(true, 0);

      const fields = new FormData(form);
      const analytics = window.LegendAnalytics;
      const attribution = analytics?.ids?.getAttribution?.() || {};
      const measurementConsent = analytics?.measurementConsent?.get?.() || null;
      const measurementAllowed = measurementConsent?.allowed === true;
      const cookie = name => document.cookie.split(';').map(value => value.trim())
        .find(value => value.startsWith(name + '='))?.slice(name.length + 1) || null;

      let sourceActionKey = window.LEGEND_LAST_WEBSITE_ACTION_KEY || null;
      try { sourceActionKey ||= sessionStorage.getItem('legend_last_website_action_key'); } catch {}

      const experienceId = form.dataset.websiteExperienceId || null;
      const experienceAnswers = {};
      if (experienceId) {
        const seenRadio = new Set();
        form.querySelectorAll('[data-cms-field-key]').forEach(control => {
          const key = control.dataset.cmsFieldKey;
          if (!key || control.matches('button,a')) return;
          if (control.type === 'radio') {
            if (seenRadio.has(key)) return;
            seenRadio.add(key);
            const selected = form.querySelector('input[type="radio"][data-cms-field-key="' + CSS.escape(key) + '"]:checked');
            experienceAnswers[key] = selected?.value || '';
          } else if (control.type === 'checkbox') {
            experienceAnswers[key] = !!control.checked;
          } else {
            experienceAnswers[key] = control.value ?? '';
          }
        });
      }

      const values = {
        firstName: experienceId ? '' : String(fields.get('FirstName') || ''),
        lastName: experienceId ? '' : String(fields.get('LastName') || ''),
        phone: experienceId ? '' : String(fields.get('Phone') || ''),
        email: experienceId ? '' : String(fields.get('Email') || ''),
        message: experienceId ? '' : String(fields.get('Message') || ''),
        sourcePath: location.pathname,
        sourceActionKey,
        sourceFormElementId: form.dataset.cmsExtraId ? `extra:${form.dataset.cmsExtraId}` : (form.dataset.cmsId || null),
        consent: experienceId ? false : fields.get('consent') === 'on',
        experienceId,
        answers: experienceId ? experienceAnswers : null,
        sessionId: analytics?.ids?.getSessionId?.() || null,
        visitorId: analytics?.ids?.getVisitorId?.() || null,
        utmSource: attribution.utmSource || null,
        utmMedium: attribution.utmMedium || null,
        utmCampaign: attribution.utmCampaign || null,
        utmId: attribution.utmId || null,
        utmTerm: attribution.utmTerm || null,
        utmContent: attribution.utmContent || null,
        fbclid: attribution.fbclid || null,
        gclid: attribution.gclid || null,
        ttclid: attribution.ttclid || null,
        oppref: attribution.oppref || null,
        obref: measurementAllowed ? cookie('__obref') : null,
        fbp: measurementAllowed ? cookie('_fbp') : null,
        fbc: measurementAllowed ? cookie('_fbc') : null,
        metaCampaignId: attribution.metaCampaignId || null,
        metaAdSetId: attribution.metaAdSetId || null,
        metaAdId: attribution.metaAdId || null,
        measurementConsent: measurementConsent?.state || null
      };

      // Attribution can change after an uncertain response. Keep the original
      // request and identity for retries of the same visitor-entered inquiry.
      const fingerprint = JSON.stringify([
        values.firstName, values.lastName, values.phone, values.email, values.message,
        values.sourcePath, values.sourceFormElementId, values.consent,
        values.experienceId, values.answers
      ]);
      if (pendingFingerprint !== fingerprint) {
        submissionId = crypto.randomUUID();
        pendingFingerprint = fingerprint;
        pendingPayload = { submissionId, ...values };
      }

      pendingPayload.measurementConsent = values.measurementConsent;
      if (!measurementAllowed) {
        pendingPayload.obref = null;
        pendingPayload.fbp = null;
        pendingPayload.fbc = null;
      }
      sending = true;
      const controller = new AbortController();
      const timeout = setTimeout(() => controller.abort(), 30000);
      button.disabled = true;
      showState('sending', 'Sending inquiry…', 'Sending your inquiry.');
      try {
        const endpoint = new URL('/api/website-inquiries/public', resolveApiBase()).toString();
        const response = await fetch(endpoint, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(pendingPayload),
          signal: controller.signal
        });
        const result = await response.json().catch(() => null);
        if (!response.ok || result?.accepted !== true) throw new Error('inquiry_failed');

        sent = true;
        showState('sent', '✓ Inquiry sent', 'Your inquiry has been received.');
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
        pendingFingerprint = null;
      } catch {
        showState('error', 'Not confirmed — retry', 'Your inquiry could not be confirmed. Please try again.');
      } finally {
        clearTimeout(timeout);
        sending = false;
        button.disabled = sent;
      }
    });
  }

  // This media path has no access to customer messaging, provider keys, tools or
  // data-channel events. The existing server owns tenant selection and admission.
  let activeVoice = null;
  const voiceBase = 'https://portal.mylegnd.com/api/receptionist/browser/';
  async function voiceRequest(action, body, signal, keepalive = false, host = null) {
    const restricted = host?.dataset.receptionistTestGrant;
    const route = restricted ? (action === 'grant' ? restricted : action === 'start' ? host.dataset.receptionistTestStart : '/api/receptionist/browser/' + action) : voiceBase + action;
    const headers = {'Content-Type': 'application/json'};
    if (restricted) {
      const url = new URL(route, location.href);
      if (url.origin !== location.origin) throw new Error('voice_unavailable');
      headers.RequestVerificationToken = host.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
    }
    const response = await fetch(route, {
      method: 'POST', headers, credentials: restricted ? 'same-origin' : 'omit',
      body: JSON.stringify(body), signal, keepalive
    });
    if (!response.ok) throw new Error('voice_unavailable');
    return response.json();
  }
  function finishVoice(call) {
    if (!call || call.finished) return;
    call.finished = true;
    clearTimeout(call.poll); clearTimeout(call.closeDeadline);
    call.observationAbort.abort();
    call.peer?.close();
    call.start.disabled = false;
    if (activeVoice === call) activeVoice = null;
  }
  function endVoice(call, message = 'Conversation ended on this page. You can send an inquiry on this page.') {
    if (!call || call.ended) return;
    call.ended = true;
    call.abort.abort();
    clearTimeout(call.deadline); clearTimeout(call.startup);
    call.stream?.getTracks().forEach(track => track.stop());
    call.audio.pause(); call.audio.srcObject = null; call.audio.hidden = true;
    call.end.hidden = true;
    call.status.textContent = message;
    if (!call.handle) { finishVoice(call); return; }
    // Stop local recording/playback immediately, but keep media/data transports alive
    // for the server's authenticated session.closed/final usage observation. This
    // bounded UI cleanup is not evidence of provider closure or a spending guarantee.
    call.closeDeadline = setTimeout(() => finishVoice(call), 15000);
    void voiceRequest('end', {handle:call.handle}, undefined, true, call.host).catch(() => {});
  }
  async function gatherVoiceCandidates(peer, signal) {
    if (peer.iceGatheringState === 'complete') return;
    await new Promise((resolve, reject) => {
      const finish = error => {
        clearTimeout(timeout);
        peer.removeEventListener('icegatheringstatechange', onState);
        signal.removeEventListener('abort', onAbort);
        error ? reject(error) : resolve();
      };
      const onState = () => { if (peer.iceGatheringState === 'complete') finish(); };
      const onAbort = () => finish(new Error('voice_cancelled'));
      const timeout = setTimeout(() => finish(new Error('voice_ice_timeout')), 10000);
      peer.addEventListener('icegatheringstatechange', onState);
      signal.addEventListener('abort', onAbort, {once:true});
      if (signal.aborted) onAbort(); else onState();
    });
  }
  function bindVoice(host) {
    if (host.dataset.receptionistBound === 'true') return;
    host.dataset.receptionistBound = 'true';
    const start = host.querySelector('[data-receptionist-start]');
    const end = host.querySelector('[data-receptionist-end]');
    const status = host.querySelector('[data-receptionist-status]');
    const audio = host.querySelector('[data-receptionist-audio]');
    if (!start || !end || !status || !audio) return;
    end.addEventListener('click', () => { if (activeVoice?.host === host) endVoice(activeVoice); });
    start.addEventListener('click', async () => {
      if (activeVoice) return;
      if (!navigator.mediaDevices?.getUserMedia || !window.RTCPeerConnection) {
        status.textContent = 'Voice is unavailable in this browser. Please send an inquiry on this page.';
        return;
      }
      const call = {host, start, end, status, audio, abort:new AbortController(), observationAbort:new AbortController(), ended:false, finished:false};
      activeVoice = call; start.disabled = true; end.hidden = false;
      status.textContent = 'Checking voice availability…';
      call.startup = setTimeout(() => endVoice(call, 'Voice could not connect. Please send an inquiry on this page.'), 45000);
      try {
        const grant = await voiceRequest('grant', {sourcePath:location.pathname}, call.abort.signal, false, host);
        if (call.ended) return;
        if (typeof grant.grant !== 'string') throw new Error('voice_unavailable');
        status.textContent = 'Allow microphone access to speak, or send an inquiry on this page.';
        const stream = await navigator.mediaDevices.getUserMedia({audio:true});
        if (call.ended) { stream.getTracks().forEach(track => track.stop()); return; }
        call.stream = stream;
        const peer = new RTCPeerConnection(); call.peer = peer;
        stream.getTracks().forEach(track => peer.addTrack(track, stream));
        peer.ontrack = event => {
          if (call.ended) return;
          audio.srcObject = event.streams[0] || new MediaStream([event.track]); audio.hidden = false;
          void audio.play().catch(() => { if (!call.ended) status.textContent = 'Use the audio player to hear the AI receptionist.'; });
        };
        peer.onconnectionstatechange = () => {
          if (['failed','disconnected','closed'].includes(peer.connectionState))
            endVoice(call, 'The voice connection ended. You can send an inquiry on this page.');
        };
        // Negotiate the documented channel, but neither send nor interpret events.
        // The server additionally enforces empty client/server event allowlists.
        call.events = peer.createDataChannel('oai-events');
        call.events.onmessage = () => {};
        await peer.setLocalDescription(await peer.createOffer());
        await gatherVoiceCandidates(peer, call.abort.signal);
        if (call.ended) return;
        status.textContent = 'Connecting to the AI receptionist…';
        // One start attempt only. An uncertain result is never automatically retried.
        const result = await voiceRequest('start', {grant:grant.grant, offer:peer.localDescription.sdp}, call.abort.signal, false, host);
        if (typeof result.handle === 'string') call.handle = result.handle;
        if (call.ended) {
          if (call.handle) void voiceRequest('end', {handle:call.handle}, undefined, true, call.host).catch(() => {});
          return;
        }
        if (typeof result.answer !== 'string' || !call.handle || !Number.isInteger(result.maximumSeconds) || result.maximumSeconds <= 0)
          throw new Error('voice_unavailable');
        await peer.setRemoteDescription({type:'answer', sdp:result.answer});
        if (call.finished) return;
        if (!call.ended) call.deadline = setTimeout(() => endVoice(call), result.maximumSeconds * 1000);
        const poll = async () => {
          if (call.finished) return;
          try {
            const observation = await voiceRequest('status', {handle:call.handle}, call.observationAbort.signal, false, host);
            if (call.finished) return;
            if (observation.state === 'ended') { endVoice(call); finishVoice(call); return; }
            if (observation.state === 'ending') endVoice(call);
            if (!call.ended && observation.state === 'active' && peer.connectionState === 'connected') {
              clearTimeout(call.startup);
              status.textContent = 'Connected to the AI receptionist. Keep this page open while speaking.';
            }
            call.poll = setTimeout(poll, 2000);
          } catch {
            if (call.finished) return;
            if (!call.ended) endVoice(call, 'The voice connection could not be confirmed. Please send an inquiry on this page.');
            call.poll = setTimeout(poll, 2000);
          }
        };
        void poll();
      } catch {
        endVoice(call, 'Voice is unavailable or microphone access was declined. Please send an inquiry on this page.');
      }
    });
  }
  window.addEventListener('pagehide', () => endVoice(activeVoice));
  document.addEventListener('visibilitychange', () => {
    if (document.hidden) endVoice(activeVoice, 'Voice stopped when this page moved to the background. You can send an inquiry on this page.');
  });

  function bindInquiryForms() {
    if (activeVoice && !activeVoice.host.isConnected) endVoice(activeVoice);
    document.querySelectorAll('[data-website-receptionist]:not([data-preview])').forEach(bindVoice);
    document.querySelectorAll('[data-website-inquiry]:not([data-preview])').forEach(bindInquiryForm);
    document.querySelectorAll(
      '[data-website-experience-form][data-submit-capability="lead_capture"]:not([data-preview])'
    ).forEach(bindInquiryForm);
  }

  bindInquiryForms();
  window.addEventListener('legend:website-content-rendered', bindInquiryForms);
})();
