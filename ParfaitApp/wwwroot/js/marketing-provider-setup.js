(() => {
  'use strict';
  const root = document.getElementById('marketing-provider-setup');
  if (!root) return;
  let revision = null;
  const text = (selector, value) => { const node = root.querySelector(selector); if (node) node.textContent = value ?? '—'; };
  const evidenceText = item => !item ? 'Evidence unavailable' : `Attempts ${item?.attempted || 0} · Accepted ${item?.accepted || 0} (${item?.acceptanceEvidence || 'not observed'}) · Pending ${item?.pending || 0} · Retrying ${item?.retrying || 0} · Failed ${item?.failed || 0} · Attribution reference ${item?.attributionObserved ? 'observed' : 'not observed'}`;
  function render(data) {
    const meta = data.meta || {}, ai = data.openAi || {}, evidence = data.evidence || {};
    revision = ai.exists ? ai.revision || null : null;
    text('[data-setup-owner]', `Owner: ${data.ownerKey}`);
    text('[data-meta-account]', meta.available === false ? 'Status unavailable' : meta.connected ? `${meta.accountName || meta.accountId || 'Connected account'}` : 'Not connected');
    text('[data-meta-pixel]', meta.pixelId || 'Not configured');
    text('[data-meta-config]', `CAPI ${meta.capiConfigured ? 'configured' : 'not configured'} · Test mode ${meta.testModeConfigured ? 'configured' : 'off'}`);
    text('[data-meta-evidence]', evidenceText(evidence.meta));
    text('[data-openai-account]', ai.connected ? `${ai.accountName || ai.accountId || 'Connected account'} (${ai.accountId || 'ID unavailable'})` : 'Not connected');
    text('[data-openai-pixel]', `Pixel: ${ai.pixelId || 'not configured'} · Data source: ${ai.conversionDataSourceId || 'not configured'}`);
    text('[data-openai-config]', `CAPI ${ai.conversionsApiConfigured ? 'configured' : 'not configured'} · Review: ${ai.reviewStatus || 'unavailable'} · Account: ${ai.accountStatus || 'unavailable'}`);
    text('[data-openai-evidence]', evidenceText(evidence.openAi));
    text('[data-provider-readiness]', `${ai.providerError || `Measurement status: ${ai.health?.status || 'unavailable'}`} · First-party events ${data.evidenceError ? 'unavailable' : evidence.receivingEvents ? 'received' : 'not observed'}. A click reference does not establish campaign credit.`);
    root.querySelector('[data-openai-refresh]').disabled = !ai.connected || !revision;
    root.querySelector('[data-openai-disconnect]').disabled = !ai.connected || !revision;
  }
  async function request(url, body) {
    const options = { credentials: 'same-origin', headers: { Accept: 'application/json' } };
    if (body) {
      options.method = 'POST';
      options.headers['Content-Type'] = 'application/json';
      options.headers.RequestVerificationToken = root.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
      options.body = JSON.stringify(body);
    }
    const response = await fetch(url, options);
    if (!response.ok) throw new Error(response.status === 409 ? 'Connection changed. Refresh before retrying.' : `Provider setup request failed (${response.status}).`);
    return response.json();
  }
  async function execute(url, body) {
    text('[data-setup-status]', 'Loading scoped provider state…');
    try {
      const result = await request(url, body);
      if (result.ok === true) {
        text('[data-setup-status]', 'Connection change saved. Refreshing status…');
        try { render(await request(root.dataset.statusUrl)); text('[data-setup-status]', 'Connection change saved; scoped status refreshed.'); }
        catch { text('[data-setup-status]', 'Connection change saved. Status refresh is unavailable; refresh later.'); }
      } else { render(result); text('[data-setup-status]', 'Scoped provider state loaded. Delivery evidence is shown separately.'); }
    }
    catch (error) { text('[data-setup-status]', error.message); }
  }
  root.querySelector('[data-openai-connect-form]').addEventListener('submit', event => {
    event.preventDefault();
    const input = root.querySelector('[data-openai-key]');
    const advertiserApiKey = input.value.trim();
    if (!advertiserApiKey) return;
    input.value = '';
    void execute(root.dataset.connectUrl, { advertiserApiKey, expectedRevision: revision });
  });
  root.querySelector('[data-openai-refresh]').addEventListener('click', () => { if (revision) void execute(root.dataset.refreshUrl, { connectionRevision: revision }); });
  root.querySelector('[data-openai-disconnect]').addEventListener('click', () => { if (revision) void execute(root.dataset.disconnectUrl, { connectionRevision: revision }); });
  void execute(root.dataset.statusUrl);
})();
