(() => {
  const OPEN_MODAL_STORAGE_KEY = 'websiteAnalytics.openModal';
  const initialPreset = document.querySelector('.fa-shell')?.dataset.initialPreset || 'today';
  const initialFrom = document.querySelector('.fa-shell')?.dataset.initialFrom || null;
  const initialTo = document.querySelector('.fa-shell')?.dataset.initialTo || null;

  const viewerTz = (() => {
    try {
      return {
        id: Intl.DateTimeFormat().resolvedOptions().timeZone || '',
        offsetMinutes: new Date().getTimezoneOffset()
      };
    } catch {
      return { id: '', offsetMinutes: 0 };
    }
  })();

  const shell = document.querySelector('.fa-shell');
  const canDeleteLeads = shell?.dataset.canDeleteLeads === 'true';
  const landingRoutes = (() => {
    const raw = shell?.dataset.landingRoutes || '[]';
    try {
      const parsed = JSON.parse(raw);
      return Array.isArray(parsed) ? parsed : [];
    } catch {
      return [];
    }
  })();
  const landingRoutesBaseUrl = shell?.dataset.landingRoutesBaseUrl || '';
  const state = {
    preset: initialPreset,
    from: initialFrom,
    to: initialTo,
    pollMs: 45000,
    controllers: {},
    openModal: null,
    cache: {
      summary: null,
      marketingHealth: null,
      conversions: null,
      leads: null,
      agentPerf: null,
      traffic: null,
      metaSignal: null,
      metaCampaigns: null,
      behaviorSources: null,
      aiSnapshot: null
    },
    canDeleteLeads,
    agentProfileId: null,
    scope: {
      preset: initialPreset,
      from: initialFrom,
      to: initialTo,
      agentProfileId: null
    },
    trafficType: {
      trafficModal: 'all',
      pagePerfModal: 'all',
      ctaPerfModal: 'all',
      quoteModal: 'all',
      convModal: 'all',
      leadsModal: 'all',
      metaSignalModal: 'paid',
      aiReviewSnapshotModal: 'all',
      behaviorModal: 'all'
    },
    metaSignalFilters: {
      quoteType: '',
      campaign: '',
      pageMode: '',
      scoreTier: ''
    }
  };
  const agentOptions = (() => {
    const raw = shell?.dataset.agentOptions || '';
    if (raw) {
      try {
        return JSON.parse(raw);
      } catch {
        // fall through
      }
    }
    return window.AGENT_OPTIONS || [];
  })();
  const isFounder = agentOptions.length > 0;
  const callerProfileId = shell?.dataset.callerProfileId || null;
  const initialScopeProfileId = shell?.dataset.initialScopeProfileId || null;
  const initialFounderAgentProfileId = (() => {
    if (!isFounder) return null;
    if (initialScopeProfileId) return initialScopeProfileId;
    try {
      const url = new URL(window.location.href);
      return url.searchParams.get('agentProfileId') || null;
    } catch {
      return null;
    }
  })();

  if (isFounder) {
    // Founder default is global unless a personal or agent scope is explicitly selected.
    state.agentProfileId = initialFounderAgentProfileId;
    state.scope.agentProfileId = initialFounderAgentProfileId;
  } else {
    // Agent → scoped to caller
    state.agentProfileId = callerProfileId;
    state.scope.agentProfileId = callerProfileId;
  }
  state.scope.scopeLabel = shell?.dataset.initialScopeLabel || (isFounder ? 'Global' : 'Agent Scope');

  const endpoints = {
    summary: '/WebsiteAnalytics/summary',
    traffic: '/WebsiteAnalytics/traffic',
    pagePerf: '/WebsiteAnalytics/page-performance',
    ctaPerf: '/WebsiteAnalytics/cta-performance',
    quote: '/WebsiteAnalytics/quote-funnel',
    marketingHealth: '/WebsiteAnalytics/marketing-health',
    conversions: '/WebsiteAnalytics/conversions',
    leads: '/WebsiteAnalytics/leads',
    metaSignal: '/WebsiteAnalytics/meta-signal',
    deleteLead: '/WebsiteAnalytics/DeleteLead',
    agentPerf: '/WebsiteAnalytics/agent-performance',
    metaCampaigns: '/WebsiteAnalytics/meta-campaigns',
    metaConnectionStatus: '/WebsiteAnalytics/meta-connection-status',
    metaDisconnect: '/WebsiteAnalytics/meta-disconnect',
    behaviorSummary: '/WebsiteAnalytics/behavior/summary',
    behaviorTime: '/WebsiteAnalytics/behavior/time-on-page',
    behaviorExit: '/WebsiteAnalytics/behavior/exit-analysis',
    behaviorJourney: '/WebsiteAnalytics/behavior/journey',
    behaviorSources: '/WebsiteAnalytics/behavior/source-performance',
    quoteFunnelAbandonment: '/WebsiteAnalytics/quote-funnel/abandonment',
    aiReviewSnapshot: '/WebsiteAnalytics/ai-review-snapshot'
  };

  function abort(key) {
    if (state.controllers[key]) {
      state.controllers[key].abort();
    }
    state.controllers[key] = new AbortController();
    return state.controllers[key];
  }

  async function fetchJson(key, url, params = {}) {
    const ctrl = abort(key);
    const qs = new URLSearchParams(params).toString();
    try {
      const res = await fetch(`${url}?${qs}`, { signal: ctrl.signal });
      if (!res.ok) {
        let detail = '';
        try {
          const contentType = res.headers.get('content-type') || '';
          if (contentType.includes('application/json')) {
            const payload = await res.json();
            detail = payload?.message || payload?.error || '';
          } else {
            detail = (await res.text()) || '';
          }
        } catch {
          detail = '';
        }
        const msg = detail ? `${key} fetch failed: ${detail}` : `${key} fetch failed`;
        throw new Error(msg);
      }
      return await res.json();
    } catch (err) {
      if (err.name === 'AbortError') {
        // Expected cancellation; swallow
        return null;
      }
      throw err;
    }
  }

  async function fetchPostJson(key, url, body = null) {
    const ctrl = abort(key);
    const token = getRequestVerificationToken();
    const headers = { 'Content-Type': 'application/json' };
    if (token) headers['RequestVerificationToken'] = token;

    const res = await fetch(url, {
      method: 'POST',
      signal: ctrl.signal,
      headers,
      body: body == null ? null : JSON.stringify(body)
    });

    if (!res.ok) {
      let detail = '';
      try {
        const payload = await res.json();
        detail = payload?.message || payload?.error || '';
      } catch {
        detail = '';
      }
      throw new Error(detail ? `${key} failed: ${detail}` : `${key} failed`);
    }

    try {
      return await res.json();
    } catch {
      return null;
    }
  }

  function getRequestVerificationToken() {
    return document.querySelector('#meta-disconnect-form input[name="__RequestVerificationToken"]')?.value
      || document.querySelector('input[name="__RequestVerificationToken"]')?.value
      || '';
  }

  function parseInitialSummary(str) {
    if (!str) return null;
    try {
      return JSON.parse(str);
    } catch (_) {
      try {
        return JSON.parse(str.replace(/&quot;/g, '"'));
      } catch {
        return null;
      }
    }
  }

  function asTrimmed(value) {
    return typeof value === 'string' ? value.trim() : '';
  }

  function findAgentOption(agentId) {
    if (!agentId) return null;
    return (agentOptions || []).find(a => String(a.id || '') === String(agentId || '')) || null;
  }

  function resolveScopeLabel(agentId) {
    if (!agentId) return 'Global';
    if (callerProfileId && String(agentId) === String(callerProfileId)) return 'Founder Personal';
    const agent = findAgentOption(agentId);
    const name = agent?.name || agent?.slug || 'Selected Agent';
    return name;
  }

  function isGlobalScope() {
    return !state.scope.agentProfileId;
  }

  function syncScopeQueryParam(agentId) {
    if (!isFounder) return;
    try {
      const url = new URL(window.location.href);
      if (agentId) url.searchParams.set('agentProfileId', agentId);
      else url.searchParams.delete('agentProfileId');
      window.history.replaceState({}, '', url);
    } catch {
      // ignore URL parse issues
    }
  }

  function syncRangeInputsFromState() {
    const fromInput = document.getElementById('range-from');
    const toInput = document.getElementById('range-to');
    const isCustom = state.scope.preset === 'custom';

    if (fromInput) {
      fromInput.value = isCustom ? (state.scope.from || '') : '';
    }
    if (toInput) {
      toInput.value = isCustom ? (state.scope.to || '') : '';
    }
  }

  function syncRangeQueryParams() {
    try {
      const url = new URL(window.location.href);
      url.searchParams.set('preset', state.scope.preset || 'today');

      if (viewerTz.id) url.searchParams.set('timezoneId', viewerTz.id);
      else url.searchParams.delete('timezoneId');

      if (Number.isFinite(viewerTz.offsetMinutes)) {
        url.searchParams.set('timezoneOffsetMinutes', String(viewerTz.offsetMinutes));
      } else {
        url.searchParams.delete('timezoneOffsetMinutes');
      }

      if (state.scope.preset === 'custom' && state.scope.from && state.scope.to) {
        const [fy, fm, fd] = state.scope.from.split('-').map(Number);
        const [ty, tm, td] = state.scope.to.split('-').map(Number);
        const fromUtc = new Date(fy, fm - 1, fd, 0, 0, 0).toISOString();
        const toUtc = new Date(ty, tm - 1, td, 23, 59, 59).toISOString();
        url.searchParams.set('fromUtc', fromUtc);
        url.searchParams.set('toUtc', toUtc);
      } else {
        url.searchParams.delete('fromUtc');
        url.searchParams.delete('toUtc');
      }

      window.history.replaceState({}, '', url);
    } catch {
      // ignore URL parse issues
    }
  }

  function updateFounderScopeUi() {
    setText('wa-scope-label', state.scope.scopeLabel || 'Global');

    const globalScope = isGlobalScope();
    const teamBtn = document.getElementById('team-rollup-btn');
    if (teamBtn) {
      teamBtn.disabled = !globalScope;
      teamBtn.title = globalScope
        ? 'View agency-wide team performance'
        : 'Switch back to Global scope to compare the full agency';
      teamBtn.setAttribute('aria-disabled', globalScope ? 'false' : 'true');
      teamBtn.style.opacity = globalScope ? '1' : '.6';
    }

    const agentPerfCard = document.getElementById('mod-agentperf');
    if (agentPerfCard) {
      agentPerfCard.style.pointerEvents = globalScope ? '' : 'none';
      agentPerfCard.style.opacity = globalScope ? '1' : '.55';
      agentPerfCard.setAttribute('aria-disabled', globalScope ? 'false' : 'true');
      agentPerfCard.title = globalScope
        ? 'Open Agent Performance'
        : 'Switch back to Global scope to compare agents';
    }

    const agentPerfMeta = document.getElementById('mod-agentperf-meta');
    if (agentPerfMeta) {
      agentPerfMeta.textContent = globalScope
        ? 'Top performers · founder only'
        : 'Global scope only · switch back to compare agents';
    }
  }

  function initScopeControls() {
    if (!isFounder) return;
    const select = document.getElementById('wa-scope-select');
    if (!select) return;

    select.innerHTML = '';

    const globalOption = document.createElement('option');
    globalOption.value = '';
    globalOption.textContent = 'Global';
    select.appendChild(globalOption);

    if (callerProfileId) {
      const founderOption = document.createElement('option');
      founderOption.value = callerProfileId;
      founderOption.textContent = 'Founder Personal';
      select.appendChild(founderOption);
    }

    const agentScopeOptions = (agentOptions || [])
      .slice()
      .filter(agent => {
        const id = String(agent?.id || '');
        if (!id) return false;
        return !(callerProfileId && id === String(callerProfileId));
      })
      .sort((a, b) => String(a?.name || a?.slug || '').localeCompare(String(b?.name || b?.slug || '')));

    if (agentScopeOptions.length) {
      const agentGroup = document.createElement('optgroup');
      agentGroup.label = 'Agents';
      agentScopeOptions.forEach(agent => {
        const id = String(agent?.id || '');
        const opt = document.createElement('option');
        opt.value = id;
        opt.textContent = `${agent?.name || agent?.slug || id}`;
        agentGroup.appendChild(opt);
      });
      select.appendChild(agentGroup);
    }

    const requestedScopeId = state.scope.agentProfileId || '';
    select.value = requestedScopeId;
    if (select.value !== requestedScopeId) {
      state.scope.agentProfileId = select.value || null;
      state.agentProfileId = state.scope.agentProfileId;
      syncScopeQueryParam(state.scope.agentProfileId);
    }
    state.scope.scopeLabel = resolveScopeLabel(state.scope.agentProfileId);
    updateFounderScopeUi();

    select.addEventListener('change', () => {
      state.scope.agentProfileId = select.value || null;
      state.agentProfileId = state.scope.agentProfileId;
      state.scope.scopeLabel = resolveScopeLabel(state.scope.agentProfileId);
      syncScopeQueryParam(state.scope.agentProfileId);
      updateFounderScopeUi();
      notifyScopeChange();
      updateGrowthBaseLink();
      loadSummary();
      refreshOpenModal();
    });
  }

  function notifyScopeChange() {
    if (!isFounder) return;

    if (isGlobalScope() === false) {
      const teamRollupModalEl = document.getElementById('teamRollupModal');
      if (teamRollupModalEl && typeof bootstrap !== 'undefined' && bootstrap?.Modal) {
        const teamRollupModal = bootstrap.Modal.getInstance(teamRollupModalEl);
        teamRollupModal?.hide();
      }
    }

    window.dispatchEvent(new CustomEvent('wa:scope-changed', {
      detail: {
        agentProfileId: state.scope.agentProfileId || null,
        scopeLabel: state.scope.scopeLabel || 'Global',
        isGlobal: isGlobalScope()
      }
    }));
  }

  function rangeParams({ team = false, modal = null } = {}) {
    const p = { preset: state.scope.preset, timezoneOffsetMinutes: viewerTz.offsetMinutes };
    if (viewerTz.id) p.timezoneId = viewerTz.id;
    const customRange = resolveCustomRangeUtc();
    if (customRange) {
      p.fromUtc = customRange.fromUtc;
      p.toUtc = customRange.toUtc;
    }
    if (team) {
      p.team = true;
      return p;
    }
    if (state.scope.agentProfileId) {
      p.agentProfileId = state.scope.agentProfileId;
    }
    // Add trafficType if modal context is provided
    if (modal && state.trafficType && state.trafficType[modal]) {
      let t = state.trafficType[modal];
      if (t === 'paid') p.trafficType = 'PaidAds';
      else if (t === 'non_paid') p.trafficType = 'NonPaid';
      else p.trafficType = 'All';
    }
    return p;
  }

  function resolveCustomRangeUtc() {
    if (state.scope.preset !== 'custom' || !state.scope.from || !state.scope.to) {
      return null;
    }

    // Interpret the date-picker values ("YYYY-MM-DD") as viewer-local midnight/EOD
    // so the server receives UTC instants that correspond to the viewer's day boundaries.
    const [fy, fm, fd] = state.scope.from.split('-').map(Number);
    const [ty, tm, td] = state.scope.to.split('-').map(Number);
    return {
      fromUtc: new Date(fy, fm - 1, fd, 0, 0, 0).toISOString(),
      toUtc: new Date(ty, tm - 1, td, 23, 59, 59).toISOString()
    };
  }

  // Render helpers ---------------------------------------------------
  function setText(id, val) {
    const el = document.getElementById(id);
    if (el) el.textContent = val;
  }

  function setTableMessage(bodyId, colspan, message, cssClass = 'fa-empty') {
    const body = document.getElementById(bodyId);
    if (!body) return;
    body.innerHTML = `<tr><td colspan="${colspan}" class="${cssClass}">${message}</td></tr>`;
  }

  function setSummaryRefreshStatus(message, isError) {
    const note = document.getElementById('wa-methodology-note');
    if (!note) return;
    if (!note.dataset.baseText) note.dataset.baseText = note.textContent || '';
    if (!note.dataset.baseClass) note.dataset.baseClass = note.className || '';
    if (!message) {
      note.textContent = note.dataset.baseText;
      note.className = note.dataset.baseClass;
      return;
    }

    note.className = `alert ${isError ? 'alert-danger' : 'alert-warning'} small py-2 px-3 mb-3`;
    note.textContent = `${note.dataset.baseText} ${message}`.trim();
  }

  function setLeadDeleteFeedback(message, tone = 'success') {
    const el = document.getElementById('leads-delete-feedback');
    if (!el) return;
    if (!message) {
      el.textContent = '';
      el.className = 'wa-inline-feedback d-none mb-2';
      return;
    }

    el.textContent = message;
    el.className = `wa-inline-feedback mb-2 ${tone === 'error' ? 'is-error' : 'is-success'}`;
  }

  function closeLeadContextMenu() {
    const menu = document.getElementById('leads-row-context-menu');
    if (!menu) return;
    menu.hidden = true;
    menu.style.left = '';
    menu.style.top = '';
    delete menu.dataset.leadId;
    delete menu.dataset.leadLabel;
  }

  function showLeadContextMenu(x, y, leadId, leadLabel) {
    const menu = document.getElementById('leads-row-context-menu');
    if (!menu || !leadId) return;

    menu.dataset.leadId = leadId;
    menu.dataset.leadLabel = leadLabel || '';
    menu.hidden = false;
    menu.style.left = '0px';
    menu.style.top = '0px';

    const bounds = menu.getBoundingClientRect();
    const safeLeft = Math.max(8, Math.min(x, window.innerWidth - bounds.width - 8));
    const safeTop = Math.max(8, Math.min(y, window.innerHeight - bounds.height - 8));
    menu.style.left = `${safeLeft}px`;
    menu.style.top = `${safeTop}px`;
  }

  function removeLeadFromLocalCache(leadId) {
    if (!state.cache.leads?.leads?.length || !leadId) return;
    const currentRows = state.cache.leads.leads;
    const remaining = currentRows.filter(row => String(row.leadId || '') !== String(leadId));
    if (remaining.length === currentRows.length) return;

    const nextTotal = Math.max(0, Number(state.cache.leads.total || currentRows.length) - 1);
    state.cache.leads = {
      ...state.cache.leads,
      leads: remaining,
      total: nextTotal,
      returnedCount: remaining.length,
      isTruncated: nextTotal > remaining.length
    };
    renderLeads(state.cache.leads);
  }

  function leadDisplayLabel(lead) {
    if (!lead) return 'Selected lead';
    const name = asTrimmed(lead.name);
    if (name) return name;
    const email = asTrimmed(lead.email);
    if (email) return email;
    const phone = asTrimmed(lead.phone);
    if (phone) return phone;
    return 'Selected lead';
  }

  function renderSummary(data) {
    state.cache.summary = data;
    state.scope.scopeLabel = data.scopeLabel || state.scope.scopeLabel || 'Global';
    setText('kpi-pageviews', data.pageViews);
    setText('kpi-visitors', data.uniqueVisitors);
    setText('kpi-sessions', data.sessions);
    setText('kpi-leads', data.verifiedLeads);
    setText('kpi-intent', data.intentAvailable ? `${data.intentConversionRate}%` : '—');
    setText('kpi-intent-sub', data.intentAvailable ? data.intentDenominatorLabel : 'Intent conversion unavailable');
    setWarning('kpi-intent-warning', data.intentLowSample);
    setText('kpi-session', `${data.sessionConversionRate}%`);
    setWarning('kpi-session-warning', data.sessionLowSample);
    setText('kpi-top-page', data.topPage || '—');
    setText('kpi-top-cta', data.topCta || '—');
    const label = document.getElementById('fa-range-label');
    if (label) label.textContent = data.rangeLabel || '';
    const env = document.getElementById('fa-env-label');
    if (env) env.textContent = data.environmentLabel || '';
    updateFounderScopeUi();
    setText('ai-drawer-scope-label', `AI reviewing: All Traffic · ${data.rangeLabel || 'Current Range'} · ${state.scope.scopeLabel}`);

    applyDelta('kpi-pv-delta', data.pageViews, data.prevPageViews);
    applyDelta('kpi-vis-delta', data.uniqueVisitors, data.prevUniqueVisitors);
    applyDelta('kpi-ses-delta', data.sessions, data.prevSessions);
    applyDelta('kpi-leads-delta', data.verifiedLeads, data.prevVerifiedLeads);
    renderSpark('kpi-pv-spark', data.pageViewTrend || []);
    renderSyntheticSpark('kpi-vis-spark', data.uniqueVisitors, data.prevUniqueVisitors);
    renderSyntheticSpark('kpi-ses-spark', data.sessions, data.prevSessions);
    renderSyntheticSpark('kpi-leads-spark', data.verifiedLeads, data.prevVerifiedLeads);

    // quick meta hints
    const trafficMeta = document.getElementById('mod-traffic-meta');
    if (trafficMeta) {
      const dir = trendDirection(data.pageViewTrend);
      const hint = data.topSource ? `Top source: ${data.topSource}` : 'Views, sessions, visitors · click to drill in';
      trafficMeta.textContent = dir ? `Traffic ${dir} · ${hint}` : hint;
    }
    const tp = document.getElementById('mod-page-meta');
    if (tp && data.topPage) tp.textContent = `Top page: ${data.topPage}`;
    const tc = document.getElementById('mod-cta-meta');
    if (tc && data.topCta) tc.textContent = `Top CTA: ${data.topCta}`;
  }

  function calculateMarketingHealthScore(data) {
    if (!data) return 0;
    let score = 100;
    score -= Math.min(30, Number(data.clientTrackingErrors || 0) * 5);
    score -= Math.min(20, Number(data.inferredFormStarts || 0) * 4);
    score -= Math.min(20, Number(data.workstationCaptureFailures || 0) * 6);
    score -= Math.min(15, Number(data.workstationNoOwnerFailures || 0) * 8);
    score -= Math.min(10, Number(data.unknownAttributedLeads || 0) * 2);
    score -= Math.min(5, Number(data.botSuspiciousSessions || 0));
    return Math.max(0, Math.min(100, score));
  }

  function marketingHealthVerdict(score, data) {
    if (!data) return 'Unknown';
    if ((data.clientTrackingErrors || 0) > 0 || (data.workstationCaptureFailures || 0) > 0 || (data.inferredFormStarts || 0) > 0) {
      return score >= 80 ? 'Stabilize' : 'Critical';
    }
    return score >= 90 ? 'Healthy' : 'Watch';
  }

  function renderMarketingHealth(data) {
    state.cache.marketingHealth = data;
    const score = calculateMarketingHealthScore(data);
    const verdict = marketingHealthVerdict(score, data);
    setText('mh-score', `${score}`);
    setText('mh-verdict', verdict);
    setText('mh-range-label', data.rangeLabel || '');
    setText('mh-client-errors', data.clientTrackingErrors || 0);
    setText('mh-inferred-starts', data.inferredFormStarts || 0);
    setText('mh-lead-persisted', data.leadPersistedEvents || 0);
    setText('mh-workstation-success', data.workstationCaptureSuccesses || 0);
    setText('mh-workstation-failures', data.workstationCaptureFailures || 0);
    setText('mh-unknown-attribution', data.unknownAttributedLeads || 0);
    setText('mh-no-owner', data.workstationNoOwnerFailures || 0);

    const verdictEl = document.getElementById('mh-verdict');
    if (verdictEl) {
      verdictEl.className = `wa-health-verdict ${verdict.toLowerCase()}`;
    }

    const warningsEl = document.getElementById('mh-warning-list');
    if (warningsEl) {
      const warnings = Array.isArray(data.warnings) ? data.warnings : [];
      warningsEl.innerHTML = warnings.length
        ? warnings.map(w => `<li>${renderMarketingHealthCopyableText(w, 'wa-health-warning-text')}</li>`).join('')
        : '<li><span class="wa-health-warning-text is-static">No active health warnings in the selected range.</span></li>';
    }

    renderMarketingHealthTrackingErrors(data.recentTrackingErrors || []);
  }

  function trackingErrorSeverityBadge(severity) {
    const label = asTrimmed(severity) || 'Unknown';
    const tone = label.toLowerCase();
    return `<span class="wa-health-severity severity-${escapeHtml(tone)}">${escapeHtml(label)}</span>`;
  }

  function trackingErrorRecoveredBadge(recovered) {
    if (recovered === true) {
      return '<span class="wa-health-recovered is-yes">Recovered</span>';
    }
    if (recovered === false) {
      return '<span class="wa-health-recovered is-no">Not Recovered</span>';
    }
    return '<span class="wa-health-recovered is-unknown">Unknown</span>';
  }

  function renderMarketingHealthCopyableText(value, className = '') {
    const text = asTrimmed(value);
    if (!text) return '';
    const safeText = escapeHtml(text);
    const classes = ['wa-health-copyable'];
    if (className) classes.push(className);
    return `<button type="button" class="${classes.join(' ')}" data-copy-value="${safeText}" title="${safeText}">${safeText}</button>`;
  }

  function renderMarketingHealthTrackingErrors(rows) {
    const body = document.getElementById('mh-errors-body');
    if (!body) return;

    if (!Array.isArray(rows) || rows.length === 0) {
      body.innerHTML = '<tr><td colspan="8" class="fa-empty">No client tracking errors in the selected range.</td></tr>';
      return;
    }

    body.innerHTML = rows.map((row, index) => {
      const severityTone = asTrimmed(row?.severity).toLowerCase() || 'unknown';
      const recoveredTone = row?.recovered === true ? 'yes' : row?.recovered === false ? 'no' : 'unknown';
      const timeLabel = escapeHtml(row?.localDisplayTime || formatDisplayDate(row?.eventUtc) || '—');
      const pageLabel = escapeHtml(row?.pageKey || row?.pagePath || row?.quoteType || '—');
      const pagePathValue = asTrimmed(row?.pagePath || row?.pageUrl || '');
      const quoteLabel = asTrimmed(row?.quoteType) ? escapeHtml(prettifyQuoteType(row.quoteType)) : '';
      const eventLabel = escapeHtml(row?.attemptedEventName || '—');
      const sessionLabel = row?.sessionIdShort ? `Session ${escapeHtml(row.sessionIdShort)}` : '';
      const visitorLabel = row?.visitorIdShort ? `Visitor ${escapeHtml(row.visitorIdShort)}` : '';
      const eventMeta = [sessionLabel, visitorLabel].filter(Boolean).join(' · ');
      const errorValue = asTrimmed(row?.errorMessage || '');
      const errorLabel = errorValue
        ? renderMarketingHealthCopyableText(errorValue, 'wa-health-cell-ellipsis')
        : '—';
      const statusLabel = row?.statusCode != null ? `HTTP ${escapeHtml(String(row.statusCode))}` : '—';
      const retryLabel = escapeHtml(String(row?.retryCount ?? 0));
      const hasMatchedLead = !!row?.matchedLead;

      return `
        <tr data-severity="${escapeHtml(severityTone)}" data-recovered="${escapeHtml(recoveredTone)}">
          <td>
            <div class="wa-health-cell-primary">${timeLabel}</div>
          </td>
          <td>
            <div class="wa-health-cell-primary">${pageLabel}</div>
            ${pagePathValue ? `<div class="wa-health-cell-sub">${renderMarketingHealthCopyableText(pagePathValue, 'wa-health-cell-ellipsis')}</div>` : ''}
            ${quoteLabel ? `<div class="wa-health-cell-sub">${quoteLabel}</div>` : ''}
          </td>
          <td>
            <div class="wa-health-cell-primary">${eventLabel}</div>
            ${eventMeta ? `<div class="wa-health-cell-sub">${eventMeta}</div>` : ''}
          </td>
          <td>
            <div class="wa-health-cell-primary">${errorLabel}</div>
          </td>
          <td>
            <div class="wa-health-cell-primary">${trackingErrorSeverityBadge(row?.severity)}</div>
            <div class="wa-health-cell-sub">${statusLabel}</div>
          </td>
          <td>
            <div class="wa-health-cell-primary">${retryLabel}</div>
          </td>
          <td>
            <div class="wa-health-cell-primary">${trackingErrorRecoveredBadge(row?.recovered)}</div>
          </td>
          <td>
            <div class="wa-health-row-actions">
              <button type="button" class="btn btn-outline-light wa-health-detail-btn" data-mh-error-index="${index}">Inspect</button>
              ${hasMatchedLead ? '<span class="wa-health-inline-flag">Lead linked</span>' : ''}
            </div>
          </td>
        </tr>
      `;
    }).join('');
  }

  function renderHealthDetailMetaItem(label, value, extraClass = '') {
    const itemClass = ['wa-modal-meta-item', extraClass].filter(Boolean).join(' ');
    return `
      <div class="${itemClass}">
        <span class="wa-modal-meta-label">${escapeHtml(label)}</span>
        <span class="wa-modal-meta-value">${escapeHtml(value || '—')}</span>
      </div>
    `;
  }

  function renderHealthDetailField(label, value, { monospace = false } = {}) {
    const text = asTrimmed(value) || '—';
    return `
      <div class="wa-health-detail-field">
        <div class="wa-health-detail-label">${escapeHtml(label)}</div>
        <div class="wa-health-detail-value${monospace ? ' is-monospace' : ''}">${escapeHtml(text)}</div>
      </div>
    `;
  }

  function renderHealthDetailCard(title, fieldsHtml, extraClass = '') {
    return `
      <article class="wa-health-detail-card ${extraClass}">
        <div class="wa-health-detail-card-title">${escapeHtml(title)}</div>
        <div class="wa-health-detail-fields">${fieldsHtml}</div>
      </article>
    `;
  }

  function openMarketingHealthErrorDetail(index) {
    const rows = state.cache.marketingHealth?.recentTrackingErrors;
    const row = Array.isArray(rows) ? rows[index] : null;
    const modalEl = document.getElementById('mhErrorDetailModal');
    if (!row || !modalEl || typeof bootstrap === 'undefined' || !bootstrap?.Modal) return;

    const pageLabel = row?.pageKey || row?.pagePath || row?.quoteType || 'Unknown page';
    const titleEl = document.getElementById('mh-error-detail-title');
    const subtitleEl = document.getElementById('mh-error-detail-subtitle');
    const metaEl = document.getElementById('mh-error-detail-meta');
    const summaryEl = document.getElementById('mh-error-detail-summary');
    const gridEl = document.getElementById('mh-error-detail-grid');

    if (titleEl) {
      titleEl.textContent = row?.attemptedEventName || 'Tracking event';
    }

    if (subtitleEl) {
      const recoveredCopy = row?.recovered === true
        ? 'Recovered after retry.'
        : row?.recovered === false
          ? 'Did not recover.'
          : 'Recovery state is unknown.';
      subtitleEl.textContent = `${pageLabel} • ${recoveredCopy}`;
    }

    if (metaEl) {
      metaEl.innerHTML = [
        renderHealthDetailMetaItem('Time', row?.localDisplayTime || formatDisplayDate(row?.eventUtc) || '—'),
        renderHealthDetailMetaItem('Severity', row?.severity || 'Unknown'),
        renderHealthDetailMetaItem('Status', row?.statusCode != null ? `HTTP ${row.statusCode}` : 'No status'),
        renderHealthDetailMetaItem('Retry', String(row?.retryCount ?? 0))
      ].join('');
    }

    if (summaryEl) {
      const endpoint = asTrimmed(row?.attemptedEndpoint) || asTrimmed(row?.rawFetchUrl) || '/api/analytics/ingest';
      const failureMessage = asTrimmed(row?.errorMessage) || 'tracking_error';
      summaryEl.innerHTML = `
        <div class="wa-health-detail-summary">
          <div class="wa-health-detail-summary-block">
            <div class="wa-health-detail-summary-kicker">Raw Failure</div>
            <div class="wa-health-detail-summary-value">${escapeHtml(failureMessage)}</div>
            <div class="wa-health-detail-summary-copy">Endpoint ${escapeHtml(endpoint)}</div>
          </div>
          <div class="wa-health-detail-summary-block">
            <div class="wa-health-detail-summary-kicker">Next Check</div>
            <div class="wa-health-detail-summary-value">${escapeHtml(row?.suggestedAction || 'Inspect browser console and server ingest logs')}</div>
            <div class="wa-health-detail-summary-copy">Trigger ${escapeHtml(row?.requestTrigger || 'Browser send')}</div>
          </div>
        </div>
      `;
    }

    if (gridEl) {
      const matchedLead = row?.matchedLead;
      const leadFields = matchedLead
        ? [
            renderHealthDetailField('Match Type', matchedLead.matchType || 'session'),
            renderHealthDetailField('Submitted', matchedLead.localDisplayTime || '—'),
            renderHealthDetailField('Delay From Error', matchedLead.delayFromErrorLabel || '—'),
            renderHealthDetailField('Name', matchedLead.name || 'Submitted lead'),
            renderHealthDetailField('Email', matchedLead.email || '—'),
            renderHealthDetailField('Phone', matchedLead.phone || '—'),
            renderHealthDetailField('Interest', matchedLead.interest || '—'),
            renderHealthDetailField('Source Page', matchedLead.sourcePageKey || '—')
          ].join('')
        : [
            renderHealthDetailField('Lead Match', 'No later lead matched this session/visitor in scope.'),
            renderHealthDetailField('Checked Against', 'Later submissions tied to the same session or visitor.')
          ].join('');

      gridEl.innerHTML = [
        renderHealthDetailCard('Tracking Identity', [
          renderHealthDetailField('Full Session ID', row?.sessionId || '—', { monospace: true }),
          renderHealthDetailField('Full Visitor ID', row?.visitorId || '—', { monospace: true })
        ].join('')),
        renderHealthDetailCard('Browser Context', [
          renderHealthDetailField('Browser', row?.browser || '—'),
          renderHealthDetailField('Device', row?.deviceType || '—'),
          renderHealthDetailField('Operating System', row?.operatingSystem || '—')
        ].join('')),
        renderHealthDetailCard('Page Context', [
          renderHealthDetailField('Page Key', row?.pageKey || '—'),
          renderHealthDetailField('Exact Path', row?.pagePath || '—', { monospace: true }),
          renderHealthDetailField('URL', row?.pageUrl || '—', { monospace: true })
        ].join('')),
        renderHealthDetailCard('Request Trace', [
          renderHealthDetailField('Method', row?.requestMethod || 'POST'),
          renderHealthDetailField('Attempted Endpoint', row?.attemptedEndpoint || '—', { monospace: true }),
          renderHealthDetailField('Route', row?.requestRoute || '—', { monospace: true }),
          renderHealthDetailField('Raw Fetch URL', row?.rawFetchUrl || '—', { monospace: true }),
          renderHealthDetailField('Trigger', row?.requestTrigger || 'Browser send')
        ].join('')),
        renderHealthDetailCard('Lead Correlation', leadFields, matchedLead ? 'is-success' : 'is-muted')
      ].join('');
    }

    bootstrap.Modal.getOrCreateInstance(modalEl).show();
  }

  function initMarketingHealthInspector() {
    document.addEventListener('click', (event) => {
      const button = event.target.closest('[data-mh-error-index]');
      if (!button) return;
      const index = Number.parseInt(button.dataset.mhErrorIndex || '', 10);
      if (!Number.isFinite(index)) return;
      openMarketingHealthErrorDetail(index);
    });
  }

  function initMarketingHealthCopyable() {
    document.addEventListener('click', async (event) => {
      const copyable = event.target.closest('.wa-health-copyable[data-copy-value]');
      if (!copyable) return;
      const value = copyable.dataset.copyValue || '';
      if (!value) return;

      const copied = await copyTextWithFallback(value);
      if (!copied) return;

      const originalTitle = copyable.dataset.copyOriginalTitle || copyable.getAttribute('title') || value;
      copyable.dataset.copyOriginalTitle = originalTitle;
      copyable.classList.add('is-copied');
      copyable.setAttribute('title', 'Copied full value');

      window.setTimeout(() => {
        copyable.classList.remove('is-copied');
        copyable.setAttribute('title', originalTitle);
      }, 1200);
    });
  }

  function renderTable(bodyId, rows, cols) {
    const body = document.getElementById(bodyId);
    if (!body) return;
    if (!rows || rows.length === 0) {
      body.innerHTML = `<tr><td colspan="${cols.length}" class="fa-empty">No data in range</td></tr>`;
      return;
    }
    body.innerHTML = rows.map(r => {
      return `<tr>${cols.map(c => {
        const alignClass = c.align || '';
        const extraClass = typeof c.className === 'function' ? (c.className(r) || '') : (c.className || '');
        const cellClass = `${alignClass} ${extraClass}`.trim();
        const value = c.render
          ? c.render(r)
          : c.fmt
            ? c.fmt(c.key ? r[c.key] : undefined, r)
            : (r[c.key] ?? '');
        return `<td class="${cellClass}">${value}</td>`;
      }).join('')}</tr>`;
    }).join('');
  }

  function applyDelta(elId, current, previous) {
    const el = document.getElementById(elId);
    if (!el) return;
    if (previous == null || previous <= 0) { el.textContent = '–'; el.className = 'fa-kpi-delta'; return; }
    const delta = current - previous;
    const pct = Math.round((delta / previous) * 100);
    const dir = delta > 0 ? 'up' : delta < 0 ? 'down' : '';
    el.className = `fa-kpi-delta ${dir}`;
    const arrow = dir === 'up' ? '▲' : dir === 'down' ? '▼' : '•';
    el.textContent = `${arrow} ${Math.abs(pct)}% vs prior`;
  }

  function renderSpark(elId, points) {
    const el = document.getElementById(elId);
    if (!el) return;
    if (!points || points.length === 0) { el.innerHTML = ''; return; }
    const values = points.map(p => p.value);
    const max = Math.max(...values);
    const min = Math.min(...values);
    const w = 100, h = 24;
    const dx = points.length > 1 ? w / (points.length - 1) : w;
    const scale = v => max === min ? h / 2 : h - ((v - min) / (max - min)) * h;
    let d = '';
    points.forEach((p, i) => {
      const x = i * dx;
      const y = scale(p.value);
      d += (i === 0 ? 'M' : 'L') + x + ' ' + y + ' ';
    });
    const gradId = `grad-${elId}`;
    el.innerHTML = `<svg width="${w}" height="${h}" viewBox="0 0 ${w} ${h}" preserveAspectRatio="none">
      <path d="${d}" fill="none" stroke="url(#${gradId})" stroke-width="2"/>
      <defs>
        <linearGradient id="${gradId}" x1="0" y1="0" x2="1" y2="0">
          <stop offset="0%" stop-color="var(--legend-gold)"/>
          <stop offset="100%" stop-color="var(--legend-silver)"/>
        </linearGradient>
      </defs>
    </svg>`;
  }

  function setWarning(elId, show) {
    const el = document.getElementById(elId);
    if (!el) return;
    el.textContent = show ? 'Low sample size' : '';
  }

  function renderSyntheticSpark(elId, current, previous) {
    if (previous == null || previous < 0) {
      renderSpark(elId, []);
      return;
    }
    renderSpark(elId, [{ value: previous }, { value: current }]);
  }

  function trendDirection(points) {
    if (!points || points.length < 2) return '';
    const delta = points[points.length - 1].value - points[0].value;
    if (delta > 0) return 'rising';
    if (delta < 0) return 'softening';
    return 'flat';
  }

  // Drilldown renders
  function renderTraffic(data) {
    const breakdown = formatAttributionBreakdown(
      data?.paidSessionCount,
      data?.nonPaidSessionCount,
      data?.unknownSessionCount,
      'session');
    renderTable('traffic-top-pages-body', data.topPages || [], [
      { key: 'key' },
      { key: 'count', align: 'text-end' }
    ]);
    renderTable('traffic-entry-pages-body', data.entryPages || [], [
      { key: 'key' },
      { key: 'count', align: 'text-end' }
    ]);
    renderTable('traffic-activity-body', data.recentActivity || [], [
      { render: r => formatDisplayDate(r.eventUtc) },
      { key: 'eventType' },
      { key: 'pageKey' },
      { key: 'elementKey' }
    ]);
    setText('traffic-range-label', breakdown
      ? `${data.rangeLabel || ''} · ${breakdown.replace('Attribution split: ', '')}`
      : (data.rangeLabel || ''));
    const trafficMeta = document.getElementById('mod-traffic-meta');
    if (trafficMeta) {
      trafficMeta.textContent = breakdown || 'Views, sessions, visitors · click to drill in';
    }
    renderTable('traffic-top-sources-body', data.topSources || [], [
      { key: 'key' },
      { key: 'count', align: 'text-end' }
    ]);
    renderTable('traffic-top-campaigns-body', data.topCampaigns || [], [
      { key: 'key' },
      { key: 'count', align: 'text-end' }
    ]);
  }

  function renderPagePerf(data) {
    renderTable('pageperf-body', data.rows || [], [
      { key: 'pageKey' },
      { key: 'views', align: 'text-end' },
      { key: 'ctaClicks', align: 'text-end' },
      { key: 'leads', align: 'text-end' },
      { render: r => `${r.conversionRate}%`, align: 'text-end' }
    ]);
    setText('pageperf-range-label', data.rangeLabel || '');
  }

  function renderCtaPerf(data) {
    renderTable('ctaperf-body', data.rows || [], [
      { key: 'pageKey' },
      { key: 'elementKey' },
      { key: 'clicks', align: 'text-end' },
      { key: 'uniqueClickSessions', align: 'text-end' },
      { key: 'verifiedLeads', align: 'text-end' },
      { render: r => formatPct(r.clickToLeadRate), align: 'text-end' }
    ]);
    setText('ctaperf-range-label', data.rangeLabel || '');
  }

  function prettifyQuoteType(value) {
    const raw = String(value || '').trim();
    if (!raw) return 'Unknown';
    const normalized = raw.toLowerCase();
    const map = {
      life: 'Life',
      term_life: 'Term Life',
      whole_life: 'Whole Life',
      final_expense: 'Final Expense',
      mortgage_protection: 'Mortgage Protection',
      disability: 'Disability',
      auto: 'Auto',
      iul: 'IUL',
      home: 'Home',
      commercial: 'Commercial',
      health: 'Health'
    };
    return map[normalized] || raw.replace(/_/g, ' ');
  }

  function renderQuote(data) {
    setText('quote-starts', data.quoteStarts);
    setText('quote-form-starts', data.quoteFormStarts);
    setText('quote-form-submits', data.quoteFormSubmits);
    const drop = data.dropOffFormStartsToSubmits ?? (data.quoteFormStarts > 0 ? Math.round((1 - (data.quoteFormSubmits / data.quoteFormStarts)) * 100) : null);
    const startMix = formatQuoteStartMix(data?.ctaStartCount, data?.directFormStartCount);
    const meta = document.getElementById('mod-quote-meta');
    if (meta) {
      const breakdown = formatAttributionBreakdown(
        data?.paidStartCount,
        data?.nonPaidStartCount,
        data?.unknownStartCount,
        'start');
      if (drop !== null && startMix && breakdown) {
        meta.textContent = `Drop-off: ${drop}% between form starts → confirmed leads · ${startMix} · ${breakdown}`;
      } else if (drop !== null && startMix) {
        meta.textContent = `Drop-off: ${drop}% between form starts → confirmed leads · ${startMix}`;
      } else if (drop !== null && breakdown) {
        meta.textContent = `Drop-off: ${drop}% between form starts → confirmed leads · ${breakdown}`;
      } else if (drop !== null) {
        meta.textContent = `Drop-off: ${drop}% between form starts → confirmed leads`;
      } else if (startMix && breakdown) {
        meta.textContent = `${startMix} · ${breakdown}`;
      } else if (startMix) {
        meta.textContent = startMix;
      } else if (breakdown) {
        meta.textContent = breakdown;
      } else {
        meta.textContent = 'Starts → form starts → submits';
      }
    }
    const meta2 = document.getElementById('quote-dropoff-starts');
    if (meta2) {
      const breakdown = formatAttributionBreakdown(
        data?.paidStartCount,
        data?.nonPaidStartCount,
        data?.unknownStartCount,
        'start');
      const definition = startMix
        ? `Starts = explicit CTA clicks or form-entry starts. ${startMix}.`
        : 'Starts = explicit CTA clicks or form-entry starts.';
      if (data.dropOffStartsToFormStarts != null && breakdown) {
        meta2.textContent = `Starts → Form starts drop-off: ${data.dropOffStartsToFormStarts}% · ${definition} ${breakdown}`;
      } else if (data.dropOffStartsToFormStarts != null && startMix) {
        meta2.textContent = `Starts → Form starts drop-off: ${data.dropOffStartsToFormStarts}% · ${definition}`;
      } else if (data.dropOffStartsToFormStarts != null) {
        meta2.textContent = `Starts → Form starts drop-off: ${data.dropOffStartsToFormStarts}% · ${definition}`;
      } else if (breakdown) {
        meta2.textContent = `${definition} ${breakdown}`;
      } else {
        meta2.textContent = definition;
      }
    }
    renderTable('quote-stage-body', data.stageMetrics || [], [
      { key: 'label' },
      { key: 'count', align: 'text-end' }
    ]);
    renderTable('quote-type-body', data.byQuoteType || [], [
      { render: r => prettifyQuoteType(r.key) },
      { key: 'count', align: 'text-end' }
    ]);
    setText('quote-range-label', data.rangeLabel || '');
  }

  function renderAbandonment(data) {
    if (!data) return;
    setText('abandon-bounce-count', data.bounceBeforeFunnelStartCount ?? 0);
    setText('abandon-funnel-count', data.funnelAbandonCount ?? 0);
    setText('abandon-contact-count', data.contactStepAbandonCount ?? 0);
    setText('abandon-validation-count', data.validationFrictionAbandonCount ?? 0);
    setText('abandon-qualification-note', data.qualificationNote || '');
    renderTable('abandon-bounce-body', data.bounceBeforeFunnelStart || [], [
      { render: r => prettifyQuoteType(r.quoteType) },
      { key: 'exitCount', align: 'text-end' },
      { key: 'engaged5sPlusCount', align: 'text-end' },
      { key: 'engaged15sPlusCount', align: 'text-end' },
      { key: 'avgDwellMs', align: 'text-end', fmt: v => formatMs(v) }
    ]);
    renderTable('abandon-summary-body', data.summary || [], [
      { render: r => prettifyQuoteType(r.quoteType) },
      { key: 'abandons', align: 'text-end' },
      { key: 'starts', align: 'text-end' },
      { key: 'abandonRate', align: 'text-end', fmt: v => formatPct(v) },
      { key: 'avgCompletedFields', align: 'text-end' },
      { key: 'submitAttemptedAbandonCount', align: 'text-end' }
    ]);
    renderTable('abandon-fields-body', data.topAbandonedFields || [], [
      { key: 'fieldName' },
      { key: 'quoteType' },
      { key: 'abandonCount', align: 'text-end' }
    ]);
    renderTable('abandon-last-completed-body', data.topLastCompletedFields || [], [
      { key: 'fieldName' },
      { key: 'quoteType' },
      { key: 'count', align: 'text-end' }
    ]);
    renderTable('abandon-validation-body', data.validationFriction || [], [
      { key: 'fieldName' },
      { key: 'quoteType' },
      { key: 'errorCount', align: 'text-end' }
    ]);
    const note = document.getElementById('abandon-consent-note');
    if (note) {
      const notes = [];
      if (data.dataQualityNote) notes.push(data.dataQualityNote);
      if ((data.summary || []).length === 0 && (data.bounceBeforeFunnelStartCount || 0) > 0) {
        notes.push('These sessions exited before any tracked form interaction, so they are shown as bounces rather than form abandons.');
      }
      if (data.consentFrictionCount > 0) {
        notes.push(`Consent friction: ${data.consentFrictionCount} session(s) attempted submit without interacting with the consent checkbox.`);
      }
      note.textContent = notes.join(' ');
    }
  }

  function renderBehavior(summary, time, exit, journey, sources) {
    const rangeLabel = summary?.rangeLabel || time?.rangeLabel || exit?.rangeLabel || journey?.rangeLabel || sources?.rangeLabel || '';
    setText('bhvr-range-label', rangeLabel);

    // ── Overview KPIs ──────────────────────────────────────────────
    setText('bhvr-total-sessions', summary?.totalSessions != null ? formatInt(summary.totalSessions) : '—');
    setText('bhvr-avg-session',    formatMs(summary?.avgSessionDurationMs));
    setText('bhvr-med-session',    formatMs(summary?.medianSessionDurationMs));
    setText('bhvr-avg-page',       formatMs(summary?.avgTimeOnPageMs));
    setText('bhvr-quick-exit',     formatPct(summary?.quickExitRate));
    setText('bhvr-engaged-rate',   formatPct(summary?.engagedSessionRate));

    // ── Highlights bar ─────────────────────────────────────────────
    const highlights = document.getElementById('bhvr-highlights');
    const parts = [];
    if (summary?.topExitPage)                  parts.push(`Top exit: <strong>${summary.topExitPage}</strong>`);
    if (summary?.topLongDwellPage)             parts.push(`Highest dwell: <strong>${summary.topLongDwellPage}</strong>`);
    if (summary?.highestScrollCompletionPage)  parts.push(`Top scroll: <strong>${summary.highestScrollCompletionPage}</strong>`);
    if (highlights) {
      if (parts.length) {
        highlights.innerHTML = parts.join(' &nbsp;·&nbsp; ');
        highlights.style.display = '';
      } else {
        highlights.style.display = 'none';
      }
    }
    const sampleNote = document.getElementById('bhvr-time-sample-note');
    if (sampleNote) {
      const totalViews = time?.totalPageViews != null ? formatInt(time.totalPageViews) : '—';
      const timingSamples = time?.totalTimingSamples != null ? formatInt(time.totalTimingSamples) : '—';
      sampleNote.textContent = `Timing metrics use page_exit samples. Avg session excludes single-event sessions. Page views: ${totalViews} · Timing samples: ${timingSamples}.`;
    }

    // ── Dwell table columns ────────────────────────────────────────
    const dwellCols = [
      { key: 'pageKey' },
      { key: 'views', align: 'text-end' },
      { key: 'timingSamples', align: 'text-end' },
      { render: r => formatMs(r.avgDwellMs),    align: 'text-end' },
      { render: r => formatMs(r.medianDwellMs), align: 'text-end' }
    ];
    const exitCols = [
      { key: 'pageKey' },
      { key: 'views', align: 'text-end' },
      { key: 'exits', align: 'text-end' },
      { render: r => formatPct(r.exitRate), align: 'text-end' }
    ];
    const keyCountCols = [
      { key: 'key' },
      { key: 'count', align: 'text-end' }
    ];

    // ── Overview tab tables ────────────────────────────────────────
    renderTable('bhvr-short-body',         time?.shortVisitProblemPages || [], dwellCols);
    renderTable('bhvr-exit-body-overview', exit?.topExitPages           || [], exitCols);

    // ── Time on Page tab ───────────────────────────────────────────
    renderTable('bhvr-long-avg-body',   time?.longestAvgDwell        || [], dwellCols);
    renderTable('bhvr-short-body-time', time?.shortVisitProblemPages || [], dwellCols);

    // ── Exit Analysis tab ──────────────────────────────────────────
    renderTable('bhvr-exit-body',       exit?.topExitPages  || [], exitCols);
    renderTable('bhvr-quick-exit-body', exit?.quickExitPages || [], keyCountCols);

    // ── Journey tab ────────────────────────────────────────────────
    renderTable('bhvr-landing-body', journey?.topLandingPages    || [], keyCountCols);
    renderTable('bhvr-prelead-body', journey?.pagesBeforeLead    || [], keyCountCols);
    renderTable('bhvr-dropoff-body', journey?.commonDropOffPages || [], keyCountCols);

    // ── Sources tab ────────────────────────────────────────────────
    state.cache.behaviorSources = sources;
    renderTable('bhvr-source-body', sources?.rows || [], [
      { key: 'source' },
      { render: r => r.medium   || '—' },
      { render: r => r.campaign || '—' },
      { render: r => r.landingPage || '—' },
      { key: 'sessions',       align: 'text-end' },
      { key: 'engagedSessions', align: 'text-end' },
      { key: 'verifiedLeads',  align: 'text-end' },
      { render: r => formatPct(r.sessionConversionRate), align: 'text-end' },
      { render: r => formatMs(r.avgDwellMs),             align: 'text-end' },
      { key: 'avgDwellSampleCount', align: 'text-end' }
    ]);
  }

  function renderConversions(data, summaryOverride) {
    state.cache.conversions = data;
    // summaryOverride is a traffic-filtered summary fetched alongside conversion data.
    // Falls back to the all-traffic summary cache so behaviour is unchanged when no filter is active.
    const summary = summaryOverride || state.cache.summary;
    if (summary) {
      setText('conv-intent', summary.intentAvailable ? `${summary.intentConversionRate}%` : '—');
      setText('conv-intent-sub', summary.intentAvailable ? summary.intentDenominatorLabel : 'Intent conversion unavailable');
      setWarning('conv-intent-warning', summary.intentLowSample);
      setText('conv-session', `${summary.sessionConversionRate}%`);
      setWarning('conv-session-warning', summary.sessionLowSample);
    }
    setText('conv-total', data.totalConversions);
    const meta = document.getElementById('mod-conv-meta');
    if (meta) {
      const top = data.recent && data.recent.length
        ? (data.recent.reduce((acc, r) => {
            const k = `${r.pageKey ?? 'unknown'}/${r.sourceCta ?? 'unknown'}`;
            acc[k] = (acc[k] || 0) + 1; return acc;
          }, {}))
        : null;
      if (top) {
        const best = Object.entries(top).sort((a,b)=>b[1]-a[1])[0];
        if (best) meta.textContent = `Top source: ${best[0]}`;
      }
    }
    renderTable('conv-body', data.recent || [], [
      { render: r => formatDisplayDate(r.eventUtc) },
      { key: 'eventType' },
      { key: 'pageKey' },
      { key: 'sourceCta' }
    ]);
    setText('conv-range-label', data.rangeLabel || '');
  }

  function trafficBadge(attribution) {
    if (!attribution) return '';
    if (attribution.isMetaAttributedPaid) return '<span class="badge badge-paid">Meta Paid</span>';
    switch (attribution.trafficType) {
      case 'PaidAds': return '<span class="badge badge-paid">Paid Ads</span>';
      case 'Direct': return '<span class="badge badge-nonpaid">Direct</span>';
      case 'Organic': return '<span class="badge badge-nonpaid">Organic</span>';
      case 'Referral': return '<span class="badge badge-nonpaid">Referral</span>';
      default: return '<span class="badge bg-secondary">Unknown</span>';
    }
  }

  function metaLearningBadge(attribution) {
    if (!attribution) return '<span class="text-muted small">Meta learning: unknown</span>';
    const label = attribution.excludedFromMetaLearningReadiness ? 'Excluded' : 'Included';
    const tone = attribution.excludedFromMetaLearningReadiness ? 'text-warning' : 'text-success';
    const reason = attribution.metaLearningReason ? `<div class="text-muted small">${escapeHtml(attribution.metaLearningReason)}</div>` : '';
    return `<div class="${tone} small fw-semibold">Meta learning: ${label}</div>${reason}`;
  }

  function renderLeads(data) {
    state.cache.leads = data;
    setText('leads-total', data.total);
    setText('leads-cap-note', data.isTruncated ? `Showing latest ${data.returnedCount} leads.` : `Showing all ${data.returnedCount} leads in range.`);
    const meta = document.getElementById('mod-leads-meta');
    if (data.leads && data.leads.length) {
      const newest = data.leads[0];
      if (meta) meta.textContent = `Most recent (local): ${formatDisplayDate(newest.createdUtc)}`;
    } else if (meta) {
      meta.textContent = 'No leads in range';
    }
    const body = document.getElementById('leads-body');
    if (!body) return;
    if (!data.leads || data.leads.length === 0) {
      body.innerHTML = '<tr><td colspan="9" class="fa-empty">No data in range</td></tr>';
      closeLeadContextMenu();
      setText('leads-range-label', data.rangeLabel || '');
      return;
    }

    body.innerHTML = (data.leads || []).map((lead) => {
      const rowClasses = ['wa-lead-row'];
      const rowAttrs = [];
      if (state.canDeleteLeads && lead.leadId) {
        rowClasses.push('wa-lead-row-admin');
        rowAttrs.push(`data-lead-id="${escapeHtml(lead.leadId)}"`);
        rowAttrs.push(`data-lead-label="${escapeHtml(leadDisplayLabel(lead))}"`);
      }

      return `
        <tr class="${rowClasses.join(' ')}" ${rowAttrs.join(' ')}>
          <td>${escapeHtml(formatDisplayDate(lead.createdUtc) || '—')}</td>
          <td>${escapeHtml(lead.name || '—')}</td>
          <td>${escapeHtml(lead.email || '—')}</td>
          <td>${escapeHtml(lead.phone || '—')}</td>
          <td>${escapeHtml(lead.interest || '—')}</td>
          <td>${escapeHtml(lead.leadSource || '—')}</td>
          <td>${escapeHtml(lead.resolvedSource || '—')}${leadAttributionIdSummary(lead)}</td>
          <td class="text-center">${trafficBadge(lead.attribution)} <span class="text-muted small">${escapeHtml(lead.attribution?.resolutionSource || 'unknown')}</span>${metaLearningBadge(lead.attribution)}</td>
          <td class="text-center">${metaTrackingBadge(lead.metaTracking)}</td>
        </tr>
      `;
    }).join('');
    setText('leads-range-label', data.rangeLabel || '');
  }

  function leadAttributionIdSummary(lead) {
    if (!lead) return '';
    const parts = [];
    const utmId = lead.resolvedUtmId || lead.utmId;
    const metaCampaignId = lead.resolvedMetaCampaignId || lead.metaCampaignId;
    const metaAdSetId = lead.resolvedMetaAdSetId || lead.metaAdSetId;
    const metaAdId = lead.resolvedMetaAdId || lead.metaAdId;

    if (utmId) parts.push(`utm_id ${escapeHtml(utmId)}`);
    if (metaCampaignId) parts.push(`campaign ${escapeHtml(metaCampaignId)}`);
    if (metaAdSetId) parts.push(`adset ${escapeHtml(metaAdSetId)}`);
    if (metaAdId) parts.push(`ad ${escapeHtml(metaAdId)}`);

    if (!parts.length) return '';
    return `<div class="text-muted small">${parts.join(' · ')}</div>`;
  }

  function metaTrackingBadge(metaTracking) {
    if (!metaTracking) {
      return '<span class="text-muted">—</span>';
    }

    const shortEventId = metaTracking.metaLeadEventId ? escapeHtml(String(metaTracking.metaLeadEventId).slice(0, 8)) : '—';
    const shortPixelId = metaTracking.resolvedMetaPixelId ? escapeHtml(String(metaTracking.resolvedMetaPixelId).slice(-6)) : '—';
    const pixelOwnerType = escapeHtml(metaTracking.pixelOwnerType || 'none');
    let badgeClass = 'bg-secondary';
    let label = 'Pending';

    if (metaTracking.dedupReady) {
      badgeClass = 'bg-success';
      label = 'Browser + Server';
    } else if (metaTracking.browserPixelSent) {
      badgeClass = 'bg-primary';
      label = 'Browser Only';
    } else if (metaTracking.serverCapiSent) {
      badgeClass = 'bg-info text-dark';
      label = 'Server Only';
    } else if (metaTracking.serverCapiStatus === 'skipped_not_configured' || metaTracking.serverCapiStatus === 'skipped_agent_token_missing') {
      badgeClass = 'bg-secondary';
      label = 'Server Skipped';
    } else if (metaTracking.serverCapiStatus === 'failed') {
      badgeClass = 'bg-danger';
      label = 'Server Failed';
    } else if (metaTracking.browserPixelStatus === 'unavailable') {
      badgeClass = 'bg-warning text-dark';
      label = 'Browser Unavailable';
    } else if (metaTracking.browserPixelStatus === 'error') {
      badgeClass = 'bg-danger';
      label = 'Browser Error';
    }

    const browserStatus = escapeHtml(metaTracking.browserPixelStatus || 'unknown');
    const serverStatus = escapeHtml(metaTracking.serverCapiStatus || 'unknown');
    const dedupLabel = metaTracking.metaLeadEventId ? 'yes' : 'no';
    return `<span class="badge ${badgeClass}">${label}</span><div class="text-muted small">eid ${shortEventId} · dedup id ${dedupLabel}</div><div class="text-muted small">pixel ${shortPixelId} · ${pixelOwnerType}</div><div class="text-muted small">b:${browserStatus} / s:${serverStatus}</div>`;
  }
  // Maps each modal id to the badge <span> id that shows "Viewing: ..."
  const trafficTypeBadgeIds = {
    trafficModal:  'traffic-active-mode',
    pagePerfModal: 'pageperf-active-mode',
    ctaPerfModal:  'ctaperf-active-mode',
    quoteModal:    'quote-active-mode',
    convModal:     'conv-active-mode',
    leadsModal:    'leads-active-mode',
    metaSignalModal: 'metasignal-active-mode'
  };

  // Traffic type UI controls and modal header update
  function updateTrafficTypeHeader(modalId) {
    const t = state.trafficType[modalId] || 'all';
    let label = 'All Traffic';
    if (t === 'paid') label = 'Ads Only';
    else if (t === 'non_paid') label = 'Non-Ads Only';

    // Update the badge text
    const badgeId = trafficTypeBadgeIds[modalId];
    if (badgeId) {
      const badge = document.getElementById(badgeId);
      if (badge) badge.textContent = `Viewing: ${label}`;
    }

    // Sync active class on the buttons
    document.querySelectorAll(`#${modalId} .traffic-type-btn`).forEach(btn => {
      btn.classList.toggle('active', btn.dataset.type === t);
    });
  }

  function initTrafficTypeControls() {
    Object.keys(trafficTypeBadgeIds).forEach(modalId => {
      document.querySelectorAll(`#${modalId} .traffic-type-btn`).forEach(btn => {
        btn.addEventListener('click', () => {
          state.trafficType[modalId] = btn.dataset.type;
          updateTrafficTypeHeader(modalId);
          refreshOpenModal();
        });
      });
    });
  }

  function initMetaSignalFilterControls() {
    [
      ['metasignal-quote-filter', 'quoteType'],
      ['metasignal-campaign-filter', 'campaign'],
      ['metasignal-page-mode-filter', 'pageMode'],
      ['metasignal-score-tier-filter', 'scoreTier']
    ].forEach(([id, key]) => {
      const select = document.getElementById(id);
      if (!select) return;
      select.addEventListener('change', () => {
        state.metaSignalFilters[key] = select.value || '';
        if (state.openModal === 'metaSignalModal') {
          loadMetaSignal();
        }
      });
    });
  }

  function initLeadDeleteControls() {
    if (!state.canDeleteLeads) return;

    const leadsBody = document.getElementById('leads-body');
    const contextMenu = document.getElementById('leads-row-context-menu');
    const deleteAction = document.getElementById('leads-context-delete');
    const confirmModalEl = document.getElementById('deleteLeadConfirmModal');
    const confirmBtn = document.getElementById('confirm-delete-lead-btn');
    const reasonInput = document.getElementById('delete-lead-reason');
    const targetEl = document.getElementById('delete-lead-target');
    const leadsModalEl = document.getElementById('leadsModal');
    if (!leadsBody || !contextMenu || !deleteAction || !confirmModalEl || !confirmBtn) return;

    leadsBody.addEventListener('contextmenu', (event) => {
      const target = event.target;
      if (!(target instanceof Element)) return;
      const row = target.closest('tr[data-lead-id]');
      if (!row) return;
      event.preventDefault();
      setLeadDeleteFeedback('');
      showLeadContextMenu(
        event.clientX,
        event.clientY,
        row.dataset.leadId || '',
        row.dataset.leadLabel || ''
      );
    });

    deleteAction.addEventListener('click', (event) => {
      event.preventDefault();
      event.stopPropagation();

      const leadId = contextMenu.dataset.leadId || '';
      const leadLabel = contextMenu.dataset.leadLabel || '';
      if (!leadId) {
        closeLeadContextMenu();
        return;
      }

      confirmModalEl.dataset.leadId = leadId;
      confirmModalEl.dataset.leadLabel = leadLabel;

      if (targetEl) {
        if (leadLabel) {
          targetEl.textContent = `Lead: ${leadLabel}`;
          targetEl.classList.remove('d-none');
        } else {
          targetEl.textContent = '';
          targetEl.classList.add('d-none');
        }
      }

      if (reasonInput) {
        reasonInput.value = '';
      }

      closeLeadContextMenu();
      bootstrap.Modal.getOrCreateInstance(confirmModalEl).show();
    });

    confirmBtn.addEventListener('click', async () => {
      const leadId = confirmModalEl.dataset.leadId || '';
      if (!leadId) {
        setLeadDeleteFeedback('Lead selection expired. Please try again.', 'error');
        bootstrap.Modal.getInstance(confirmModalEl)?.hide();
        return;
      }

      const reason = asTrimmed(reasonInput?.value) || 'Test lead cleanup';
      const originalLabel = confirmBtn.textContent || 'Yes, delete lead';
      confirmBtn.disabled = true;
      confirmBtn.textContent = 'Deleting...';

      try {
        await fetchPostJson('delete-lead', endpoints.deleteLead, { leadId, reason });
        removeLeadFromLocalCache(leadId);
        bootstrap.Modal.getInstance(confirmModalEl)?.hide();
        setLeadDeleteFeedback('Lead deleted from analytics.');
        await Promise.allSettled([loadSummary(), loadLeads()]);
      } catch (err) {
        const message = (err && err.message) ? err.message : 'Unable to delete lead.';
        setLeadDeleteFeedback(message, 'error');
      } finally {
        confirmBtn.disabled = false;
        confirmBtn.textContent = originalLabel;
      }
    });

    confirmModalEl.addEventListener('hidden.bs.modal', () => {
      if (reasonInput) {
        reasonInput.value = '';
      }
      delete confirmModalEl.dataset.leadId;
      delete confirmModalEl.dataset.leadLabel;
      if (targetEl) {
        targetEl.textContent = '';
        targetEl.classList.add('d-none');
      }
    });

    leadsModalEl?.addEventListener('hidden.bs.modal', () => {
      closeLeadContextMenu();
      setLeadDeleteFeedback('');
    });

    document.addEventListener('click', (event) => {
      if (contextMenu.hidden) return;
      if (contextMenu.contains(event.target)) return;
      closeLeadContextMenu();
    });

    document.addEventListener('keydown', (event) => {
      if (event.key === 'Escape') {
        closeLeadContextMenu();
      }
    });

    window.addEventListener('resize', closeLeadContextMenu);
    document.addEventListener('scroll', closeLeadContextMenu, true);
  }

  function setAiSnapshotStatus(message, tone = 'muted') {
    const el = document.getElementById('ai-snapshot-status');
    if (!el) return;
    el.classList.remove('text-muted', 'text-success', 'text-warning', 'text-danger');
    if (tone === 'success') el.classList.add('text-success');
    else if (tone === 'warning') el.classList.add('text-warning');
    else if (tone === 'error') el.classList.add('text-danger');
    else el.classList.add('text-muted');
    el.textContent = message || '';
  }

  function escapeHtml(value) {
    return String(value || '')
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  }

  function aiSectionClass(title) {
    const match = /^SECTION\s+([A-J])\s+—/i.exec(String(title || '').trim());
    if (!match) return 'ai-sec-default';
    return `ai-sec-${match[1].toLowerCase()}`;
  }

  function aiNumberClass(token) {
    const raw = String(token || '').replace(/,/g, '').replace('%', '');
    const n = Number(raw);
    if (!Number.isFinite(n)) return 'ai-num';
    if (n === 0) return 'ai-num ai-num-zero';
    if (n > 0) return 'ai-num ai-num-pos';
    return 'ai-num';
  }

  function decorateAiNumbers(text) {
    const rx = /(^|[^A-Za-z0-9])(\d{1,3}(?:,\d{3})*(?:\.\d+)?%?)(?=[^A-Za-z0-9]|$)/g;
    return text.replace(rx, (full, prefix, token) => {
      return `${prefix}<span class="${aiNumberClass(token)}">${token}</span>`;
    });
  }

  function renderAiSnapshotLine(line) {
    const raw = String(line || '');
    const trimmed = raw.trim();
    if (!trimmed) return '<div class="ai-line ai-empty"></div>';

    if (/^no data in range\./i.test(trimmed)) {
      return `<div class="ai-line ai-no-data">${escapeHtml(trimmed)}</div>`;
    }

    const bullet = /^-\s+(.*)$/.exec(trimmed);
    if (bullet) {
      const content = decorateAiNumbers(escapeHtml(bullet[1]));
      return `<div class="ai-line ai-bullet"><span class="ai-bullet-mark">•</span><span>${content}</span></div>`;
    }

    const numbered = /^(\d+)\.\s+(.*)$/.exec(trimmed);
    if (numbered) {
      const content = decorateAiNumbers(escapeHtml(numbered[2]));
      return `<div class="ai-line ai-numbered"><span class="ai-step-no">${numbered[1]}.</span><span>${content}</span></div>`;
    }

    if (trimmed.endsWith(':')) {
      return `<div class="ai-line ai-subhead">${escapeHtml(trimmed)}</div>`;
    }

    const colonIdx = trimmed.indexOf(':');
    if (colonIdx > 0 && colonIdx < trimmed.length - 1) {
      const label = trimmed.substring(0, colonIdx);
      const value = trimmed.substring(colonIdx + 1).trim();
      return `<div class="ai-line ai-kv"><span class="ai-label">${escapeHtml(label)}:</span> <span class="ai-value">${decorateAiNumbers(escapeHtml(value))}</span></div>`;
    }

    return `<div class="ai-line">${decorateAiNumbers(escapeHtml(trimmed))}</div>`;
  }

  function renderAiSnapshotFormatted(snapshotText) {
    const renderEl = document.getElementById('ai-snapshot-render');
    if (!renderEl) return;

    if (!snapshotText) {
      renderEl.innerHTML = '<div class="fa-empty">No snapshot data.</div>';
      return;
    }

    const lines = String(snapshotText).split(/\r?\n/);
    const sections = [];
    let current = null;

    for (const line of lines) {
      const trimmed = String(line || '').trim();
      if (/^SECTION [A-J] —/i.test(trimmed)) {
        if (current) sections.push(current);
        current = { title: trimmed, lines: [] };
      } else {
        if (!current) current = { title: 'SNAPSHOT', lines: [] };
        current.lines.push(line);
      }
    }
    if (current) sections.push(current);

    renderEl.innerHTML = sections.map(section => {
      const lineHtml = (section.lines || []).map(renderAiSnapshotLine).join('');
      return `<section class="ai-snapshot-section ${aiSectionClass(section.title)}"><div class="ai-section-title">${escapeHtml(section.title)}</div><div class="ai-section-body">${lineHtml}</div></section>`;
    }).join('');

    renderEl.scrollTop = 0;
  }

  async function copyTextWithFallback(text) {
    if (!text) return false;
    if (navigator.clipboard?.writeText) {
      try {
        await navigator.clipboard.writeText(text);
        return true;
      } catch {
        // fall through to execCommand fallback
      }
    }
    try {
      const tmp = document.createElement('textarea');
      tmp.value = text;
      tmp.setAttribute('readonly', 'readonly');
      tmp.style.position = 'absolute';
      tmp.style.left = '-9999px';
      document.body.appendChild(tmp);
      tmp.select();
      const ok = document.execCommand('copy');
      document.body.removeChild(tmp);
      return !!ok;
    } catch {
      return false;
    }
  }

  function normalizeCopiedText(text) {
    return String(text || '')
      .replace(/\r\n/g, '\n')
      .replace(/[ \t]+\n/g, '\n')
      .replace(/\n{3,}/g, '\n\n')
      .trim();
  }

  function labelForTrafficType(type) {
    if (type === 'paid') return 'Ads Only';
    if (type === 'non_paid') return 'Non-Ads Only';
    return 'All Traffic';
  }

  function formatAttributionBreakdown(paidCount, nonPaidCount, unknownCount, noun) {
    const paid = Number(paidCount ?? 0);
    const nonPaid = Number(nonPaidCount ?? 0);
    const unknown = Number(unknownCount ?? 0);
    if (paid <= 0 && nonPaid <= 0 && unknown <= 0) {
      return '';
    }

    const plural = (value) => Number(value) === 1 ? noun : `${noun}s`;
    return `Attribution split: Paid ${paid} · Non-Ads ${nonPaid} · Unknown ${unknown} ${plural(unknown)}`;
  }

  function formatQuoteStartMix(ctaStarts, directFormStarts) {
    const cta = Number(ctaStarts ?? 0);
    const direct = Number(directFormStarts ?? 0);
    if (cta <= 0 && direct <= 0) {
      return '';
    }
    return `Start mix: CTA ${cta} · Direct/Form-entry ${direct}`;
  }

  function replaceElementWithTextBlock(element, text) {
    const block = document.createElement('div');
    block.className = 'wa-copy-text-block';
    block.textContent = normalizeCopiedText(text);
    element.replaceWith(block);
  }

  function prependTextBlock(element, text) {
    if (!(element instanceof HTMLElement)) return;
    const normalized = normalizeCopiedText(text);
    if (!normalized) return;

    const block = document.createElement('div');
    block.className = 'wa-copy-text-block';
    block.textContent = normalized;
    element.prepend(block);
  }

  function isMeaningfulSelection(select) {
    if (!(select instanceof HTMLSelectElement)) return false;
    return asTrimmed(select.value).length > 0;
  }

  function resolveTableLabel(table) {
    const fromPanel = asTrimmed(
      table.closest('.meta-signal-panel')?.querySelector('.meta-signal-panel-head h6')?.textContent
    );
    if (fromPanel) return fromPanel;

    const anchor = table.closest('.table-responsive') || table;
    let sibling = anchor.previousElementSibling;
    while (sibling) {
      const ownText = sibling.matches('h6, .bhvr-section-label')
        ? sibling.textContent
        : sibling.querySelector('h6, .bhvr-section-label')?.textContent;
      const normalized = asTrimmed(ownText);
      if (normalized) return normalized;
      sibling = sibling.previousElementSibling;
    }

    const fromContainer = asTrimmed(
      table.closest('.col, [class*="col-"], .wa-modal-section, .tab-pane')
        ?.querySelector(':scope > h6, :scope > .bhvr-section-label')
        ?.textContent
    );
    return fromContainer;
  }

  function serializeTableForCopy(table) {
    if (!(table instanceof HTMLTableElement)) return '';

    const lines = [];
    const label = resolveTableLabel(table);
    if (label) lines.push(label);

    const headerCells = Array.from(table.querySelectorAll('thead th'))
      .map(cell => asTrimmed(cell.textContent))
      .filter(Boolean);
    if (headerCells.length) {
      lines.push(headerCells.join(' | '));
    }

    const bodyRows = Array.from(table.querySelectorAll('tbody tr'))
      .map(row => Array.from(row.querySelectorAll('th, td'))
        .map(cell => asTrimmed(cell.textContent))
        .filter(value => value.length > 0))
      .filter(row => row.length > 0);

    bodyRows.forEach(row => {
      lines.push(row.join(' | '));
    });

    return normalizeCopiedText(lines.join('\n'));
  }

  function resolveTabPaneLabel(modal, pane) {
    if (!(modal instanceof HTMLElement) || !(pane instanceof HTMLElement) || !pane.id) return '';

    const trigger = modal.querySelector(`[data-bs-target="#${pane.id}"], [href="#${pane.id}"]`);
    return asTrimmed(trigger?.textContent);
  }

  function serializeMetricCardForCopy(element) {
    if (!(element instanceof HTMLElement)) return '';

    const fallbackLabel = element.matches('.wa-kpi-card')
      ? asTrimmed(element.querySelector(':scope > span')?.textContent)
      : '';
    const fallbackValue = element.matches('.wa-kpi-card')
      ? asTrimmed(element.querySelector(':scope > strong')?.textContent)
      : '';

    const label = asTrimmed(
      element.querySelector('.fa-kpi-title, .meta-signal-stat-label, .wa-modal-meta-label, .wa-health-score-label')?.textContent
    ) || fallbackLabel;
    const value = asTrimmed(
      element.querySelector('.fa-kpi-value, .meta-signal-stat-value, .wa-modal-meta-value, .wa-health-score')?.textContent
    ) || fallbackValue;
    const extraLines = Array.from(
      element.querySelectorAll('.fa-kpi-sub, .fa-kpi-warning, .meta-signal-stat-note, .wa-health-metric-kicker, .wa-health-verdict, .wa-health-summary-note')
    )
      .map(node => asTrimmed(node.textContent))
      .filter(Boolean);

    if (!label && !value && !extraLines.length) {
      return '';
    }

    const lines = [];
    if (label && value) {
      lines.push(`${label}: ${value}`);
    } else {
      if (label) lines.push(label);
      if (value) lines.push(value);
    }
    if (extraLines.length) {
      lines.push(...extraLines);
    }
    return normalizeCopiedText(lines.join('\n'));
  }

  function isLooseMetricBlock(element) {
    if (!(element instanceof HTMLElement)) return false;
    if (element.matches('.kpi-card, .meta-signal-stat-card, .wa-modal-meta-item, .wa-copy-text-block')) return false;
    if (element.querySelector('table, ul, ol, .table-responsive')) return false;

    const hasTitle = Array.from(element.children).some(child =>
      child instanceof HTMLElement && child.classList.contains('fa-kpi-title')
    );
    const hasValue = Array.from(element.children).some(child =>
      child instanceof HTMLElement && child.classList.contains('fa-kpi-value')
    );

    return hasTitle && hasValue;
  }

  function prepareMetricBlocksForCopy(root) {
    if (!(root instanceof HTMLElement)) return;

    root.querySelectorAll('.kpi-card, .meta-signal-stat-card, .wa-modal-meta-item, .wa-kpi-card, .wa-health-summary-card').forEach(element => {
      const text = serializeMetricCardForCopy(element);
      if (text) {
        replaceElementWithTextBlock(element, text);
      }
    });

    const looseBlocks = Array.from(root.querySelectorAll('.fa-kpi-title'))
      .map(title => title.parentElement)
      .filter(parent => isLooseMetricBlock(parent));

    Array.from(new Set(looseBlocks)).forEach(element => {
      const text = serializeMetricCardForCopy(element);
      if (text) {
        replaceElementWithTextBlock(element, text);
      }
    });
  }

  function replaceCopyableButtonsForCopy(root) {
    if (!(root instanceof HTMLElement)) return;

    root.querySelectorAll('.wa-health-copyable[data-copy-value]').forEach(button => {
      const text = normalizeCopiedText(button.dataset.copyValue || button.textContent || '');
      const replacement = document.createElement('span');
      replacement.className = 'wa-copy-inline-text';
      replacement.textContent = text;
      button.replaceWith(replacement);
    });
  }

  function extractRootCopyText(root, { removeSelectors = [] } = {}) {
    if (!(root instanceof HTMLElement)) return '';

    const clone = root.cloneNode(true);
    removeSelectors.forEach(selector => {
      clone.querySelectorAll(selector).forEach(node => node.remove());
    });

    replaceCopyableButtonsForCopy(clone);
    clone.querySelectorAll('script, style, button, select, input, textarea').forEach(el => el.remove());
    clone.querySelectorAll('[hidden], .d-none').forEach(el => el.remove());
    prepareMetricBlocksForCopy(clone);

    clone.querySelectorAll('table').forEach(table => {
      replaceElementWithTextBlock(table, serializeTableForCopy(table));
    });

    const temp = document.createElement('div');
    temp.setAttribute('aria-hidden', 'true');
    temp.style.position = 'fixed';
    temp.style.left = '-9999px';
    temp.style.top = '0';
    temp.style.width = '1100px';
    temp.style.opacity = '0';
    temp.style.pointerEvents = 'none';
    temp.appendChild(clone);
    document.body.appendChild(temp);

    const text = normalizeCopiedText(temp.innerText || temp.textContent || '');
    document.body.removeChild(temp);
    return text;
  }

  function prepareTabPanesForCopy(root, modal) {
    if (!(root instanceof HTMLElement)) return;

    root.querySelectorAll('.tab-pane').forEach(pane => {
      if (!(pane instanceof HTMLElement)) return;

      const paneLabel = resolveTabPaneLabel(modal, pane);
      if (paneLabel) {
        prependTextBlock(pane, paneLabel);
      }

      pane.classList.remove('fade');
      pane.classList.add('show', 'active');
      pane.removeAttribute('hidden');
      pane.style.display = 'block';
      pane.style.visibility = 'visible';
      pane.style.opacity = '1';
    });
  }

  function extractModalBodyCopyText(modal) {
    const body = modal?.querySelector('.modal-body');
    if (!(body instanceof HTMLElement)) return '';

    const clone = body.cloneNode(true);

    clone.querySelectorAll('script, style, button, select, input, textarea').forEach(el => el.remove());
    clone.querySelectorAll('.meta-signal-filter-panel').forEach(el => el.remove());
    clone.querySelectorAll('.wa-modal-range-chip').forEach(el => el.remove());
    clone.querySelectorAll('[hidden], .d-none').forEach(el => el.remove());
    prepareTabPanesForCopy(clone, modal);
    clone.querySelectorAll('.nav-tabs, [role="tablist"]').forEach(el => el.remove());
    prepareMetricBlocksForCopy(clone);

    clone.querySelectorAll('table').forEach(table => {
      replaceElementWithTextBlock(table, serializeTableForCopy(table));
    });

    const temp = document.createElement('div');
    temp.setAttribute('aria-hidden', 'true');
    temp.style.position = 'fixed';
    temp.style.left = '-9999px';
    temp.style.top = '0';
    temp.style.width = '1100px';
    temp.style.opacity = '0';
    temp.style.pointerEvents = 'none';
    temp.appendChild(clone);
    document.body.appendChild(temp);

    const text = normalizeCopiedText(temp.innerText || temp.textContent || '');
    document.body.removeChild(temp);
    return text;
  }

  function collectModalCopyMeta(modalId, modal) {
    const lines = [];
    const rangeValue = asTrimmed(
      modal.querySelector('.wa-modal-range-value, .meta-signal-range-value, #deviceIntelligenceRangeLabel')?.textContent
    );
    if (rangeValue) {
      lines.push(rangeValue.toLowerCase().startsWith('range') ? rangeValue : `Range: ${rangeValue}`);
    }

    if (Object.prototype.hasOwnProperty.call(state.trafficType, modalId)) {
      lines.push(`Traffic View: ${labelForTrafficType(state.trafficType[modalId])}`);
    }

    if (modalId === 'deviceIntelligenceModal') {
      const deviceBridge = window.websiteAnalyticsDeviceIntelligence;
      const trafficLabel = typeof deviceBridge?.getTrafficLabel === 'function'
        ? deviceBridge.getTrafficLabel()
        : '';
      if (trafficLabel) {
        lines.push(`Traffic View: ${trafficLabel}`);
      }
    }

    const allTabs = Array.from(new Set(Array.from(modal.querySelectorAll('[data-bs-toggle="tab"]'))
      .map(tab => asTrimmed(tab.textContent))
      .filter(Boolean)));
    if (allTabs.length) {
      lines.push(`Views Included: ${allTabs.join(' · ')}`);
    }

    const activeTabs = Array.from(new Set(Array.from(modal.querySelectorAll('[data-bs-toggle="tab"].active'))
      .map(tab => asTrimmed(tab.textContent))
      .filter(Boolean)));
    if (activeTabs.length && activeTabs.length < allTabs.length) {
      lines.push(`Current Tab: ${activeTabs.join(' · ')}`);
    } else if (activeTabs.length && !allTabs.length) {
      lines.push(`Active View: ${activeTabs.join(' · ')}`);
    }

    const selectedFilters = Array.from(modal.querySelectorAll('select'))
      .filter(select => select instanceof HTMLSelectElement && isMeaningfulSelection(select))
      .map(select => {
        const label = asTrimmed(modal.querySelector(`label[for="${select.id}"]`)?.textContent) || select.name || select.id;
        const selectedOption = select.options[select.selectedIndex];
        const value = asTrimmed(selectedOption?.textContent) || asTrimmed(select.value);
        return label && value ? `${label}: ${value}` : '';
      })
      .filter(Boolean);
    if (selectedFilters.length) {
      lines.push(`Filters: ${selectedFilters.join(' · ')}`);
    }

    if (state.scope?.scopeLabel) {
      lines.push(`Scope: ${state.scope.scopeLabel}`);
    }

    return lines;
  }

  function getModalCopyPayload(modalId) {
    const modal = document.getElementById(modalId);
    if (!(modal instanceof HTMLElement)) {
      return {
        title: '',
        subtitle: '',
        metaLines: [],
        bodyText: ''
      };
    }

    return {
      title: asTrimmed(modal.querySelector('.modal-title')?.textContent),
      subtitle: asTrimmed(
      modal.querySelector('.wa-modal-subtitle, .meta-signal-subtitle')?.textContent
      ),
      metaLines: collectModalCopyMeta(modalId, modal),
      bodyText: extractModalBodyCopyText(modal)
    };
  }

  function buildModalCopyText(modalId) {
    const payload = getModalCopyPayload(modalId);
    const lines = [];

    if (payload.title) lines.push(payload.title);
    if (payload.subtitle) lines.push(payload.subtitle);

    if (payload.metaLines.length) {
      lines.push('');
      lines.push(...payload.metaLines);
    }

    if (payload.bodyText) {
      lines.push('');
      lines.push(payload.bodyText);
    }

    return normalizeCopiedText(lines.join('\n'));
  }

  function setModalCopyButtonState(button, text) {
    if (!(button instanceof HTMLButtonElement)) return;
    button.textContent = text;
  }

  function initModalCopyButtons() {
    document.querySelectorAll('[data-copy-modal-view]').forEach(button => {
      if (!(button instanceof HTMLButtonElement) || button.dataset.wired === 'true') return;
      button.dataset.wired = 'true';
      button.dataset.defaultLabel = button.textContent || 'Copy Form';

      button.addEventListener('click', async () => {
        const modalId = button.dataset.copyModalView || '';
        const payload = buildModalCopyText(modalId);
        if (!payload) {
          setModalCopyButtonState(button, 'Nothing To Copy');
          window.setTimeout(() => setModalCopyButtonState(button, button.dataset.defaultLabel || 'Copy Form'), 1400);
          return;
        }

        const copied = await copyTextWithFallback(payload);
        setModalCopyButtonState(button, copied ? 'Copied' : 'Copy Failed');
        window.setTimeout(() => {
          setModalCopyButtonState(button, button.dataset.defaultLabel || 'Copy Form');
        }, 1400);
      });
    });
  }

  function resolveAnalysisModuleDescriptor(sectionKey) {
    switch (sectionKey) {
      case 'metaSignal':
        return 'High-intent tiers, abandon points, and optimization readiness';
      case 'behavior':
        return 'Dwell time, exits, journey & source attribution';
      case 'quote':
        return 'Starts → form starts → submits';
      case 'conv':
        return 'Lead submits & successes';
      case 'device':
        return 'Device, browser, OS, viewport, timezone, language, and conversion behavior.';
      case 'traffic':
        return state.cache.summary?.topSource
          ? `Top source: ${state.cache.summary.topSource}`
          : 'Views, sessions, visitors · click to drill in';
      case 'leads':
        return 'Recent captured leads';
      case 'page':
        return state.cache.summary?.topPage
          ? `Top page: ${state.cache.summary.topPage}`
          : 'Top pages and conversions';
      case 'cta':
        return state.cache.summary?.topCta
          ? `Top CTA: ${state.cache.summary.topCta}`
          : 'Clicks by CTA';
      case 'agentperf':
        return isFounder && isGlobalScope()
          ? 'Top performers · founder only'
          : 'Global scope only · switch back to compare agents';
      default:
        return '';
    }
  }

  function buildAnalysisModuleCopySection(section) {
    const lines = [];
    if (section.title) lines.push(section.title);
    if (section.summary) lines.push(section.summary);

    if (section.rootSelector) {
      const root = document.querySelector(section.rootSelector);
      const bodyText = extractRootCopyText(root, { removeSelectors: section.removeSelectors || [] });
      if (bodyText) {
        lines.push('');
        lines.push(bodyText);
      } else if (section.body) {
        lines.push('');
        lines.push(section.body);
      }
      return normalizeCopiedText(lines.join('\n'));
    }

    if (!section.modalId) {
      if (section.body) {
        lines.push('');
        lines.push(section.body);
      }
      return normalizeCopiedText(lines.join('\n'));
    }

    const payload = getModalCopyPayload(section.modalId);
    if (payload.metaLines.length) {
      lines.push('');
      lines.push(...payload.metaLines);
    }

    if (payload.bodyText) {
      lines.push('');
      lines.push(payload.bodyText);
    } else if (section.body) {
      lines.push('');
      lines.push(section.body);
    }

    return normalizeCopiedText(lines.join('\n'));
  }

  function buildAnalysisModuleCopyPlan() {
    const supportLabel = asTrimmed(document.querySelector('.wa-module-band-secondary .wa-module-band-label')?.textContent)
      || 'Supporting Drill-Ins';
    const supportCopy = asTrimmed(document.querySelector('.wa-module-band-secondary .wa-module-band-copy')?.textContent)
      || 'Traffic, leads, destinations, and CTA reviews';

    return [
      {
        key: 'coreTraffic',
        title: 'Traffic Pulse',
        summary: 'High-signal top-line readouts',
        rootSelector: '.wa-kpi-band-primary',
        removeSelectors: ['.wa-kpi-band-head']
      },
      {
        key: 'coreConversion',
        title: 'Conversion + Context',
        summary: 'Supporting reads for quality and intent',
        rootSelector: '.wa-kpi-band-secondary',
        removeSelectors: ['.wa-kpi-band-head']
      },
      {
        key: 'metaSignal',
        title: 'Meta Signal Intelligence',
        summary: resolveAnalysisModuleDescriptor('metaSignal'),
        modalId: 'metaSignalModal',
        loader: async () => { await loadMetaSignal(); }
      },
      {
        key: 'behavior',
        title: 'Behavior Intelligence',
        summary: resolveAnalysisModuleDescriptor('behavior'),
        modalId: 'behaviorModal',
        loader: async () => { await loadBehavior(); }
      },
      {
        key: 'quote',
        title: 'Quote Funnel',
        summary: resolveAnalysisModuleDescriptor('quote'),
        modalId: 'quoteModal',
        loader: async () => { await loadQuote(); }
      },
      {
        key: 'conv',
        title: 'Conversion Center',
        summary: resolveAnalysisModuleDescriptor('conv'),
        modalId: 'convModal',
        loader: async () => { await loadConv(); }
      },
      {
        key: 'supporting',
        title: supportLabel,
        summary: supportCopy
      },
      {
        key: 'device',
        title: 'Device Intelligence',
        summary: resolveAnalysisModuleDescriptor('device'),
        modalId: 'deviceIntelligenceModal',
        loader: async () => {
          const deviceBridge = window.websiteAnalyticsDeviceIntelligence;
          if (typeof deviceBridge?.loadCurrentView === 'function') {
            await deviceBridge.loadCurrentView();
          } else {
            const content = document.getElementById('deviceIntelligenceContent');
            if (content) content.innerHTML = '<div class="fa-empty">Device Intelligence is unavailable right now.</div>';
          }
        }
      },
      {
        key: 'traffic',
        title: 'Traffic Overview',
        summary: resolveAnalysisModuleDescriptor('traffic'),
        modalId: 'trafficModal',
        loader: async () => { await loadTraffic(); }
      },
      {
        key: 'leads',
        title: 'Leads Snapshot',
        summary: resolveAnalysisModuleDescriptor('leads'),
        modalId: 'leadsModal',
        loader: async () => { await loadLeads(); }
      },
      {
        key: 'page',
        title: 'Page Performance',
        summary: resolveAnalysisModuleDescriptor('page'),
        modalId: 'pagePerfModal',
        loader: async () => { await loadPagePerf(); }
      },
      {
        key: 'cta',
        title: 'CTA Performance',
        summary: resolveAnalysisModuleDescriptor('cta'),
        modalId: 'ctaPerfModal',
        loader: async () => { await loadCtaPerf(); }
      },
      {
        key: 'agentperf',
        title: 'Agent Performance',
        summary: resolveAnalysisModuleDescriptor('agentperf'),
        modalId: isFounder ? 'agentPerfModal' : '',
        body: 'Agent Performance is available in Global scope only. Switch back to Global to compare agents.',
        loader: isFounder ? async () => { await loadAgentPerf(); } : null
      },
      {
        key: 'marketingHealth',
        title: 'Marketing Health Center',
        summary: 'Tracking integrity, attribution clarity, and lead-pipeline reliability',
        rootSelector: '.wa-health-surface',
        removeSelectors: ['.wa-health-kicker-row', '.wa-health-heading', '.wa-health-subcopy'],
        loader: async () => { await loadMarketingHealth(); }
      }
    ];
  }

  async function buildAllAnalysisModulesCopyText() {
    await loadSummary();

    const plan = buildAnalysisModuleCopyPlan();
    const loadSteps = plan
      .filter(section => typeof section.loader === 'function')
      .map(section => section.loader());

    await Promise.allSettled(loadSteps);

    return normalizeCopiedText(
      plan
        .map(section => buildAnalysisModuleCopySection(section))
        .filter(Boolean)
        .join('\n\n')
    );
  }

  function setCopyAllModulesButtonState(button, text, disabled) {
    if (!(button instanceof HTMLButtonElement)) return;
    button.textContent = text;
    button.disabled = !!disabled;
  }

  function initCopyAllModulesButton() {
    const button = document.getElementById('copy-all-analysis-modules');
    if (!(button instanceof HTMLButtonElement) || button.dataset.wired === 'true') return;
    button.dataset.wired = 'true';
    button.dataset.defaultLabel = button.textContent || 'Copy All Modules';

    button.addEventListener('click', async () => {
      const defaultLabel = button.dataset.defaultLabel || 'Copy All Modules';
      setCopyAllModulesButtonState(button, 'Copying All...', true);

      try {
        const payload = await buildAllAnalysisModulesCopyText();
        if (!payload) {
          setCopyAllModulesButtonState(button, 'Nothing To Copy', true);
          window.setTimeout(() => setCopyAllModulesButtonState(button, defaultLabel, false), 1400);
          return;
        }

        const copied = await copyTextWithFallback(payload);
        setCopyAllModulesButtonState(button, copied ? 'Copied All' : 'Copy Failed', true);
      } catch (err) {
        console.error(err);
        setCopyAllModulesButtonState(button, 'Copy Failed', true);
      }

      window.setTimeout(() => setCopyAllModulesButtonState(button, defaultLabel, false), 1400);
    });
  }

  function renderAiReviewSnapshot(data) {
    state.cache.aiSnapshot = data;
    setText('ai-snapshot-generated', data.generatedAtLocal ? formatDisplayDate(data.generatedAtLocal) : '—');
    setText('ai-snapshot-range', data.rangeLabel || '—');
    setText('ai-snapshot-scope', data.scopeLabel || '—');
    setText('ai-snapshot-traffic', data.trafficFilterLabel || 'All Traffic');

    const textEl = document.getElementById('ai-snapshot-text');
    if (textEl) textEl.value = data.snapshotText || '';
    renderAiSnapshotFormatted(data.snapshotText || '');

    const warnings = Array.isArray(data.warnings) ? data.warnings : [];
    if (warnings.length) {
      setAiSnapshotStatus(`Snapshot ready with warnings: ${warnings.join(' ')}`, 'warning');
    } else {
      setAiSnapshotStatus('Snapshot ready to copy.', 'success');
    }

    const copyBtn = document.getElementById('ai-snapshot-copy');
    if (copyBtn) copyBtn.disabled = !(data.snapshotText && data.snapshotText.length);
  }

  async function loadAiReviewSnapshot() {
    const textEl = document.getElementById('ai-snapshot-text');
    const copyBtn = document.getElementById('ai-snapshot-copy');
    const renderEl = document.getElementById('ai-snapshot-render');
    if (textEl) textEl.value = '';
    if (copyBtn) copyBtn.disabled = true;
    if (renderEl) renderEl.innerHTML = '<div class="fa-loading">Generating snapshot...</div>';
    setText('ai-snapshot-generated', '—');
    setText('ai-snapshot-range', '—');
    setText('ai-snapshot-scope', '—');
    setText('ai-snapshot-traffic', '—');
    setAiSnapshotStatus('Generating snapshot…');

    try {
      const data = await fetchJson('ai-review-snapshot', endpoints.aiReviewSnapshot, rangeParams({ modal: 'aiReviewSnapshotModal' }));
      if (!data) return;
      renderAiReviewSnapshot(data);
    } catch (err) {
      if (textEl) textEl.value = '';
      if (renderEl) renderEl.innerHTML = '<div class="fa-empty">Unable to render snapshot.</div>';
      setAiSnapshotStatus((err && err.message) ? err.message : 'Unable to generate snapshot.', 'error');
      console.error(err);
    }
  }

  function formatDisplayDate(utcString) {
    const d = new Date(utcString);
    if (isNaN(d)) return '';
    return d.toLocaleString('en-US', {
      month: '2-digit',
      day: '2-digit',
      year: 'numeric',
      hour: 'numeric',
      minute: '2-digit',
      hour12: true
    });
  }

  // Fetch wrappers ---------------------------------------------------
  async function loadSummary() {
    try {
      const data = await fetchJson('summary', endpoints.summary, rangeParams());
      if (!data) return;
      setSummaryRefreshStatus('', false);
      renderSummary(data);
      void loadMarketingHealth();
    } catch (err) {
      const message = (err && err.message) ? err.message : 'Unable to refresh the current summary.';
      setSummaryRefreshStatus(`Live refresh warning: ${message} Showing the last successfully loaded summary.`, true);
      console.error(err);
    }
  }

  async function loadMarketingHealth() {
    try {
      const data = await fetchJson('marketing-health', endpoints.marketingHealth, rangeParams());
      if (!data) return;
      renderMarketingHealth(data);
    } catch (err) {
      const warningsEl = document.getElementById('mh-warning-list');
      if (warningsEl) {
        warningsEl.innerHTML = `<li>${escapeHtml((err && err.message) ? err.message : 'Unable to load marketing health.')}</li>`;
      }
      setTableMessage('mh-errors-body', 8, (err && err.message) ? err.message : 'Unable to load marketing health.', 'text-danger');
      setText('mh-verdict', 'Unavailable');
      setText('mh-score', '—');
      console.error(err);
    }
  }
  async function loadTraffic() {
    try {
      const data = await fetchJson('traffic', endpoints.traffic, rangeParams({ modal: 'trafficModal' }));
      if (!data) return;
      renderTraffic(data);
      state.cache.traffic = data;
    } catch (err) {
      const message = (err && err.message) ? err.message : 'Unable to load Traffic Overview.';
      setText('traffic-range-label', 'Unavailable');
      setTableMessage('traffic-top-pages-body', 2, message, 'text-danger');
      setTableMessage('traffic-entry-pages-body', 2, message, 'text-danger');
      setTableMessage('traffic-top-sources-body', 2, message, 'text-danger');
      setTableMessage('traffic-top-campaigns-body', 2, message, 'text-danger');
      setTableMessage('traffic-activity-body', 4, message, 'text-danger');
      console.error(err);
    }
  }
  async function loadPagePerf() {
    try {
      const data = await fetchJson('pageperf', endpoints.pagePerf, rangeParams({ modal: 'pagePerfModal' }));
      if (!data) return;
      renderPagePerf(data);
    } catch (err) {
      const message = (err && err.message) ? err.message : 'Unable to load page performance.';
      setText('pageperf-range-label', 'Unavailable');
      setTableMessage('pageperf-body', 5, message, 'text-danger');
      console.error(err);
    }
  }
  async function loadCtaPerf() {
    try {
      const data = await fetchJson('ctaperf', endpoints.ctaPerf, rangeParams({ modal: 'ctaPerfModal' }));
      if (!data) return;
      renderCtaPerf(data);
    } catch (err) {
      const message = (err && err.message) ? err.message : 'Unable to load CTA performance.';
      setText('ctaperf-range-label', 'Unavailable');
      setTableMessage('ctaperf-body', 7, message, 'text-danger');
      console.error(err);
    }
  }
  async function loadQuote() {
    try {
      const [data, abandon] = await Promise.all([
        fetchJson('quote', endpoints.quote, rangeParams({ modal: 'quoteModal' })),
        fetchJson('quote-abandon', endpoints.quoteFunnelAbandonment, rangeParams({ modal: 'quoteModal' }))
      ]);
      if (data) renderQuote(data);
      if (abandon) renderAbandonment(abandon);
    } catch (err) {
      const message = (err && err.message) ? err.message : 'Unable to load quote funnel.';
      setText('quote-range-label', 'Unavailable');
      setTableMessage('quote-type-body', 2, message, 'text-danger');
      setTableMessage('quote-stage-body', 2, message, 'text-danger');
      setTableMessage('abandon-bounce-body', 5, message, 'text-danger');
      setTableMessage('abandon-summary-body', 6, message, 'text-danger');
      setTableMessage('abandon-fields-body', 3, message, 'text-danger');
      setTableMessage('abandon-last-completed-body', 3, message, 'text-danger');
      setTableMessage('abandon-validation-body', 3, message, 'text-danger');
      console.error(err);
    }
  }
  async function loadBehavior() {
    try {
      setText('bhvr-range-label', 'Loading…');
      const params = rangeParams({ modal: 'behaviorModal' });
      const [summary, time, exit, journey, sources] = await Promise.all([
        fetchJson('bhvr-summary', endpoints.behaviorSummary, params),
        fetchJson('bhvr-time',    endpoints.behaviorTime,    params),
        fetchJson('bhvr-exit',    endpoints.behaviorExit,    params),
        fetchJson('bhvr-journey', endpoints.behaviorJourney, params),
        fetchJson('bhvr-source',  endpoints.behaviorSources, params)
      ]);
      renderBehavior(summary || {}, time || {}, exit || {}, journey || {}, sources || {});
    } catch (err) {
      const message = (err && err.message) ? err.message : 'Unable to load Behavior Intelligence.';
      setText('bhvr-range-label', 'Unavailable');
      setText('bhvr-total-sessions', '—');
      setText('bhvr-avg-session', '—');
      setText('bhvr-med-session', '—');
      setText('bhvr-avg-page', '—');
      setText('bhvr-quick-exit', '—');
      setText('bhvr-engaged-rate', '—');
      setText('bhvr-time-sample-note', `${message} Existing numbers were cleared to avoid showing stale data.`);
      [
        ['bhvr-short-body', 5],
        ['bhvr-exit-body-overview', 4],
        ['bhvr-long-avg-body', 5],
        ['bhvr-short-body-time', 5],
        ['bhvr-exit-body', 4],
        ['bhvr-quick-exit-body', 2],
        ['bhvr-landing-body', 2],
        ['bhvr-prelead-body', 2],
        ['bhvr-dropoff-body', 2],
        ['bhvr-source-body', 10]
      ].forEach(([id, colspan]) => setTableMessage(id, colspan, message, 'text-danger'));
      console.error(err);
    }
  }
  async function loadConv() {
    try {
      const params = rangeParams({ modal: 'convModal' });
      const t = state.trafficType.convModal || 'all';
      let convData, summaryData;
      if (t === 'all') {
        convData = await fetchJson('conv', endpoints.conversions, params);
        summaryData = state.cache.summary;
      } else {
        [convData, summaryData] = await Promise.all([
          fetchJson('conv', endpoints.conversions, params),
          fetchJson('conv-summary', endpoints.summary, params)
        ]);
      }
      if (!convData) return;
      renderConversions(convData, summaryData);
    } catch (err) {
      const message = (err && err.message) ? err.message : 'Unable to load Conversion Center.';
      setText('conv-range-label', 'Unavailable');
      setText('conv-total', '—');
      setText('conv-session-rate', '—');
      setText('conv-intent-rate', '—');
      setTableMessage('conv-body', 6, message, 'text-danger');
      console.error(err);
    }
  }
  async function loadLeads() {
    try {
      const data = await fetchJson('leads', endpoints.leads, rangeParams({ modal: 'leadsModal' }));
      if (!data) return;
      renderLeads(data);
    } catch (err) {
      const message = (err && err.message) ? err.message : 'Unable to load leads.';
      setText('leads-range-label', 'Unavailable');
      setText('leads-total', '—');
      setText('leads-cap-note', message);
      setTableMessage('leads-body', 9, message, 'text-danger');
      console.error(err);
    }
  }

  function populateSelectOptions(id, options, selectedValue, allLabel) {
    const select = document.getElementById(id);
    if (!select) return;

    const currentValue = selectedValue ?? '';
    const normalizedOptions = Array.isArray(options) ? options.filter(Boolean) : [];
    select.innerHTML = '';

    const allOption = document.createElement('option');
    allOption.value = '';
    allOption.textContent = allLabel;
    select.appendChild(allOption);

    normalizedOptions.forEach((value) => {
      const option = document.createElement('option');
      option.value = value;
      option.textContent = value;
      if (String(value) === String(currentValue)) {
        option.selected = true;
      }
      select.appendChild(option);
    });
  }

  function clampMetaSignalScore(value, min = 0, max = 1) {
    const numericValue = Number(value);
    if (!Number.isFinite(numericValue)) {
      return min;
    }
    return Math.min(max, Math.max(min, numericValue));
  }

  function scorePositiveCount(value, target) {
    const numericValue = Number(value || 0);
    const numericTarget = Math.max(Number(target || 1), 1);
    if (!Number.isFinite(numericValue) || numericValue <= 0) {
      return 0;
    }
    return clampMetaSignalScore(Math.log1p(numericValue) / Math.log1p(numericTarget));
  }

  function scorePositiveRate(value, target) {
    const numericValue = Number(value || 0);
    const numericTarget = Math.max(Number(target || 0), 0.0001);
    if (!Number.isFinite(numericValue) || numericValue <= 0) {
      return 0;
    }
    return clampMetaSignalScore(numericValue / numericTarget);
  }

  function scoreNegativeRate(value, denominator, badAtRatio) {
    const numericValue = Number(value || 0);
    const numericDenominator = Number(denominator || 0);
    const numericBadAtRatio = Math.max(Number(badAtRatio || 0), 0.0001);

    if ((!Number.isFinite(numericValue) || numericValue <= 0) && (!Number.isFinite(numericDenominator) || numericDenominator <= 0)) {
      return null;
    }

    if (!Number.isFinite(numericValue) || numericValue <= 0) {
      return 1;
    }

    if (!Number.isFinite(numericDenominator) || numericDenominator <= 0) {
      return 0;
    }

    const ratio = numericValue / numericDenominator;
    return 1 - clampMetaSignalScore(ratio / numericBadAtRatio);
  }

  function averageMetaSignalScores(...scores) {
    const validScores = scores.filter((score) => Number.isFinite(score));
    if (!validScores.length) {
      return null;
    }
    return validScores.reduce((sum, score) => sum + score, 0) / validScores.length;
  }

  function interpolateMetaSignalColor(score) {
    const normalizedScore = clampMetaSignalScore(score);
    const stops = [
      { at: 0, rgb: [248, 113, 113] },
      { at: 0.5, rgb: [245, 158, 11] },
      { at: 1, rgb: [74, 222, 128] }
    ];

    const upperStopIndex = stops.findIndex((stop) => normalizedScore <= stop.at);
    const upperStop = upperStopIndex === -1 ? stops[stops.length - 1] : stops[upperStopIndex];
    const lowerStop = upperStopIndex <= 0 ? stops[0] : stops[upperStopIndex - 1];
    const range = Math.max(upperStop.at - lowerStop.at, 0.0001);
    const progress = clampMetaSignalScore((normalizedScore - lowerStop.at) / range);
    const rgb = lowerStop.rgb.map((value, index) => Math.round(value + (upperStop.rgb[index] - value) * progress));

    return {
      accent: `rgb(${rgb.join(', ')})`,
      accentRgb: rgb.join(', '),
      accentSoft: `rgba(${rgb.join(', ')}, 0.17)`
    };
  }

  function applyMetaSignalMetricTone(metricId, score) {
    const valueEl = document.getElementById(metricId);
    const card = valueEl?.closest('.meta-signal-stat-card');
    if (!card) {
      return;
    }

    card.classList.remove('metric-tone-active');
    card.style.removeProperty('--meta-signal-accent');
    card.style.removeProperty('--meta-signal-accent-rgb');
    card.style.removeProperty('--meta-signal-accent-soft');
    card.removeAttribute('data-health-score');

    if (!Number.isFinite(score)) {
      return;
    }

    const normalizedScore = clampMetaSignalScore(score);
    const tone = interpolateMetaSignalColor(normalizedScore);
    card.classList.add('metric-tone-active');
    card.style.setProperty('--meta-signal-accent', tone.accent);
    card.style.setProperty('--meta-signal-accent-rgb', tone.accentRgb);
    card.style.setProperty('--meta-signal-accent-soft', tone.accentSoft);
    card.setAttribute('data-health-score', String(Math.round(normalizedScore * 100)));
  }

  function applyMetaSignalHealthTones(data) {
    const totalEvents = Number(data.totalSignalEvents || 0);
    const totalVisitors = Number(data.totalVisitors || 0);
    const highIntentVisitors = Number(data.highIntentVisitors || 0);
    const leadReadyVisitors = Number(data.leadReadyVisitors || 0);
    const submittedLeads = Number(data.submittedLeads || 0);
    const submitAttemptsWithoutLead = Number(data.submitAttemptsWithoutLead || 0);
    const highIntentAbandons = Number(data.highIntentAbandons || 0);
    const contactStepAbandons = Number(data.contactStepAbandons || 0);
    const excludedSignalEvents = Number(data.excludedSignalEvents || 0);
    const excludedSignalVisitors = Number(data.excludedSignalVisitors || 0);
    const signalToLeadRate = Number(data.signalToLeadConversionRate || 0) / 100;
    const submitAttemptContext = submittedLeads + submitAttemptsWithoutLead;
    const contactStepContext = leadReadyVisitors + contactStepAbandons;

    const metricScores = {
      'metasignal-total-events': scorePositiveCount(totalEvents, 300),
      'metasignal-total-visitors': scorePositiveCount(totalVisitors, 140),
      'metasignal-high-intent': averageMetaSignalScores(
        scorePositiveCount(highIntentVisitors, 70),
        totalVisitors > 0 ? scorePositiveRate(highIntentVisitors / totalVisitors, 0.45) : null
      ),
      'metasignal-lead-ready': averageMetaSignalScores(
        scorePositiveCount(leadReadyVisitors, 36),
        totalVisitors > 0 ? scorePositiveRate(leadReadyVisitors / totalVisitors, 0.28) : null
      ),
      'metasignal-submitted': averageMetaSignalScores(
        scorePositiveCount(submittedLeads, 20),
        totalVisitors > 0 ? scorePositiveRate(submittedLeads / totalVisitors, 0.12) : null
      ),
      'metasignal-submit-attempts-no-lead': scoreNegativeRate(submitAttemptsWithoutLead, submitAttemptContext, 0.45),
      'metasignal-signal-conv': totalVisitors > 0 ? scorePositiveRate(signalToLeadRate, 0.12) : null,
      'metasignal-high-intent-abandons': scoreNegativeRate(highIntentAbandons, highIntentVisitors, 0.45),
      'metasignal-contact-abandons': scoreNegativeRate(contactStepAbandons, contactStepContext, 0.45),
      'metasignal-excluded-events': scoreNegativeRate(excludedSignalEvents, totalEvents, 0.18),
      'metasignal-excluded-visitors': scoreNegativeRate(excludedSignalVisitors, totalVisitors, 0.18)
    };

    Object.entries(metricScores).forEach(([metricId, score]) => applyMetaSignalMetricTone(metricId, score));
  }

  function renderMetaSignal(data) {
    state.cache.metaSignal = data;
    setText('metasignal-range-label', data.rangeLabel || '');
    setText('metasignal-scope-note', data.learningScopeNote || '');
    setText('metasignal-total-events', data.totalSignalEvents ?? 0);
    setText('metasignal-total-visitors', data.totalVisitors ?? 0);
    setText('metasignal-high-intent', data.highIntentVisitors ?? 0);
    setText('metasignal-lead-ready', data.leadReadyVisitors ?? 0);
    setText('metasignal-submitted', data.submittedLeads ?? 0);
    setText('metasignal-submit-attempts-no-lead', data.submitAttemptsWithoutLead ?? 0);
    setText('metasignal-high-intent-abandons', data.highIntentAbandons ?? 0);
    setText('metasignal-contact-abandons', data.contactStepAbandons ?? 0);
    setText('metasignal-excluded-events', data.excludedSignalEvents ?? 0);
    setText('metasignal-excluded-visitors', data.excludedSignalVisitors ?? 0);
    setText('metasignal-signal-conv', `${data.signalToLeadConversionRate ?? 0}%`);
    setText('metasignal-optimize', data.recommendedOptimizationEvent || '—');
    setText('metasignal-best-variant', data.bestPerformingLandingPageVersion || '—');
    setText('metasignal-worst-friction', data.worstFrictionStep || '—');
    applyMetaSignalHealthTones(data);

    populateSelectOptions('metasignal-quote-filter', data.availableQuoteTypes, state.metaSignalFilters.quoteType, 'All Quote Types');
    populateSelectOptions('metasignal-campaign-filter', data.availableCampaigns, state.metaSignalFilters.campaign, 'All Campaigns');
    populateSelectOptions('metasignal-page-mode-filter', data.availablePageModes, state.metaSignalFilters.pageMode, 'All Page Modes');
    populateSelectOptions('metasignal-score-tier-filter', data.availableScoreTiers, state.metaSignalFilters.scoreTier, 'All Score Tiers');

    renderTable('metasignal-quote-body', data.eventsByQuoteType || [], [
      { key: 'label' },
      { key: 'value', align: 'text-end' }
    ]);
    renderTable('metasignal-campaign-body', data.eventsByCampaign || [], [
      { key: 'label' },
      { key: 'value', align: 'text-end' }
    ]);
    renderTable('metasignal-tier-body', data.visitorsByScoreTier || [], [
      { key: 'scoreTier' },
      { key: 'visitors', align: 'text-end' }
    ]);
    renderTable('metasignal-campaign-score-body', data.averageScoreByCampaign || [], [
      { key: 'label' },
      { render: row => `${row.averageScore}`, align: 'text-end' }
    ]);
    renderTable('metasignal-pagevariant-score-body', data.averageScoreByPageVariant || [], [
      { key: 'label' },
      { render: row => `${row.averageScore}`, align: 'text-end' }
    ]);
    if (!data.hasEligiblePaidMetaTraffic) {
      setTableMessage(
        'metasignal-ladder-body',
        3,
        'No paid Meta-attributed funnel progression exists in this slice. General website funnel activity may still appear in Quote Funnel and Conversion Center.',
        'fa-empty'
      );
    } else {
      renderTable('metasignal-ladder-body', data.eventLadder || [], [
        { key: 'stepLabel' },
        { key: 'visitors', align: 'text-end' },
        { render: row => row.progressionRate == null ? '—' : `${row.progressionRate}%`, align: 'text-end' }
      ]);
    }
    renderTable('metasignal-friction-body', data.frictionHotspots || [], [
      { key: 'label' },
      { key: 'count', align: 'text-end' }
    ]);
    renderTable('metasignal-diagnostics-body', data.recentDiagnostics || [], [
      { render: row => escapeHtml(formatDisplayDate(row.createdUtc) || '—') },
      { render: row => `${escapeHtml(row.eventName || '—')}<div class="text-muted small">${escapeHtml(row.quoteType || '—')}</div>` },
      { render: row => `${escapeHtml(row.trafficType || 'Unknown')}${row.campaignLabel ? `<div class="text-muted small">${escapeHtml(row.campaignLabel)}</div>` : ''}` },
      { render: row => row.excludedFromMetaLearningReadiness ? '<span class="badge bg-warning text-dark">Excluded</span>' : '<span class="badge bg-success">Included</span>' },
      { render: row => escapeHtml(row.learningReason || '—') },
      { render: row => row.browserPixelSent ? '<span class="badge bg-primary">Sent</span>' : '<span class="badge bg-secondary">No</span>', align: 'text-center' },
      { render: row => row.serverCapiSent ? '<span class="badge bg-info text-dark">Sent</span>' : '<span class="badge bg-secondary">No</span>', align: 'text-center' },
      { render: row => row.deduplicationEventIdPresent ? '<span class="badge bg-success">Yes</span>' : '<span class="badge bg-secondary">No</span>', align: 'text-center' }
    ]);
  }

  async function loadMetaSignal() {
    try {
      const params = {
        ...rangeParams({ modal: 'metaSignalModal' }),
        ...(state.metaSignalFilters.quoteType ? { quoteType: state.metaSignalFilters.quoteType } : {}),
        ...(state.metaSignalFilters.campaign ? { campaign: state.metaSignalFilters.campaign } : {}),
        ...(state.metaSignalFilters.pageMode ? { pageMode: state.metaSignalFilters.pageMode } : {}),
        ...(state.metaSignalFilters.scoreTier ? { scoreTier: state.metaSignalFilters.scoreTier } : {})
      };
      const data = await fetchJson('metaSignal', endpoints.metaSignal, params);
      if (!data) return;
      renderMetaSignal(data);
    } catch (err) {
      const message = (err && err.message) ? err.message : 'Unable to load Meta Signal Intelligence.';
      setText('metasignal-range-label', 'Unavailable');
      setText('metasignal-scope-note', message);
      [
        ['metasignal-quote-body', 2],
        ['metasignal-campaign-body', 2],
        ['metasignal-tier-body', 2],
        ['metasignal-campaign-score-body', 2],
        ['metasignal-pagevariant-score-body', 2],
        ['metasignal-ladder-body', 3],
        ['metasignal-friction-body', 2],
        ['metasignal-diagnostics-body', 8]
      ].forEach(([id, colspan]) => setTableMessage(id, colspan, message, 'text-danger'));
      console.error(err);
    }
  }

  function renderAgentPerfDisabledState() {
    setText('agentperf-range-label', state.cache.summary?.rangeLabel || 'Current Range');
    setTableMessage(
      'agentperf-body',
      8,
      'Agent Performance is available in Global scope only. Switch back to Global to compare agents.',
      'fa-empty'
    );
  }

  async function loadAgentPerf() {
    if (isFounder && !isGlobalScope()) {
      renderAgentPerfDisabledState();
      return;
    }

    try {
      const data = await fetchJson('agentperf', endpoints.agentPerf, rangeParams());
      if (!data) return;
      renderAgentPerf(data);
    } catch (err) {
      const message = (err && err.message) ? err.message : 'Unable to load Agent Performance.';
      setText('agentperf-range-label', 'Unavailable');
      setTableMessage('agentperf-body', 8, message, 'text-danger');
      console.error(err);
    }
  }

  function formatMoney(v) {
    const num = Number(v || 0);
    return Number.isFinite(num)
      ? num.toLocaleString('en-US', { style: 'currency', currency: 'USD', maximumFractionDigits: 2 })
      : '$0.00';
  }

  function formatInt(v) {
    const num = Number(v || 0);
    return Number.isFinite(num) ? num.toLocaleString('en-US') : '0';
  }

  function formatPct(v) {
    if (v == null) return '—';
    const num = Number(v);
    return Number.isFinite(num) ? `${num.toFixed(2)}%` : '—';
  }

  function formatMs(ms) {
    if (ms == null) return '—';
    const num = Number(ms);
    if (!Number.isFinite(num) || num <= 0) return '—';
    const totalSeconds = Math.round(num / 1000);
    const minutes = Math.floor(totalSeconds / 60);
    const seconds = totalSeconds % 60;
    if (minutes <= 0) return `${seconds}s`;
    return `${minutes}m ${seconds.toString().padStart(2, '0')}s`;
  }

  function toNumber(v) {
    const n = Number(v);
    return Number.isFinite(n) ? n : 0;
  }

  function toUtcIso(value) {
    if (!value) return '';
    const dt = new Date(value);
    return Number.isNaN(dt.getTime()) ? '' : dt.toISOString();
  }

  function pill(text, cls) {
    return `<span class="meta-pill ${cls || 'meta-neutral'}">${text ?? '—'}</span>`;
  }

  function metaStatusClass(status) {
    const s = String(status || '').toUpperCase();
    if (s === 'ACTIVE') return 'meta-good';
    if (s === 'PAUSED' || s === 'LIMITED') return 'meta-warn';
    if (s === 'ARCHIVED' || s === 'DELETED' || s === 'DISAPPROVED') return 'meta-bad';
    return 'meta-neutral';
  }

  function metaCampaignNameClass(row) {
    const statusClass = metaStatusClass(row?.status);
    const leads = toNumber(row?.leads);
    const ctr = toNumber(row?.ctr);

    if (statusClass === 'meta-bad') return 'meta-bad';
    if (statusClass === 'meta-warn') return 'meta-warn';
    if (statusClass === 'meta-good') {
      if (leads > 0 || ctr >= 1.5) return 'meta-good';
      return 'meta-warn';
    }

    return leads > 0 ? 'meta-good' : 'meta-neutral';
  }

  function metaObjectiveClass(objective) {
    const o = String(objective || '').toUpperCase();
    if (o.includes('LEAD') || o.includes('CONVERSION') || o.includes('OUTCOME')) return 'meta-good';
    if (o.includes('TRAFFIC') || o.includes('AWARENESS') || o.includes('ENGAGEMENT')) return 'meta-warn';
    return 'meta-neutral';
  }

  function metaVolumeClass(v) {
    const n = toNumber(v);
    if (n < 0) return 'meta-bad';
    if (n === 0) return 'meta-warn';
    return 'meta-good';
  }

  function metaImpressionsClass(v) {
    const n = toNumber(v);
    if (n <= 0) return 'meta-bad';
    if (n < 1000) return 'meta-warn';
    return 'meta-good';
  }

  function metaReachClass(v) {
    const n = toNumber(v);
    if (n <= 0) return 'meta-bad';
    if (n < 500) return 'meta-warn';
    return 'meta-good';
  }

  function metaClicksClass(v) {
    const n = toNumber(v);
    if (n <= 0) return 'meta-bad';
    if (n < 10) return 'meta-warn';
    return 'meta-good';
  }

  function metaLeadsClass(v) {
    const n = toNumber(v);
    if (n <= 0) return 'meta-bad';
    if (n < 3) return 'meta-warn';
    return 'meta-good';
  }

  function metaLeadGapClass(v) {
    const n = Math.abs(toNumber(v));
    if (n === 0) return 'meta-good';
    if (n <= 2) return 'meta-warn';
    return 'meta-bad';
  }

  function metaSpendClass(row) {
    const spend = toNumber(row?.spend);
    const leads = toNumber(row?.leads);

    if (spend <= 0) return 'meta-neutral';
    if (leads <= 0) return 'meta-bad';

    const cpl = spend / leads;
    if (cpl <= 5) return 'meta-good';
    if (cpl <= 15) return 'meta-warn';
    return 'meta-bad';
  }

  function metaCtrClass(v) {
    const n = toNumber(v);
    if (n < 1) return 'meta-bad';
    if (n < 2.5) return 'meta-warn';
    return 'meta-good';
  }

  function metaCpcClass(v) {
    const n = toNumber(v);
    if (n <= 0) return 'meta-warn';
    if (n <= 2) return 'meta-good';
    if (n <= 5) return 'meta-warn';
    return 'meta-bad';
  }

  function metaCpmClass(v) {
    const n = toNumber(v);
    if (n <= 0) return 'meta-warn';
    if (n <= 15) return 'meta-good';
    if (n <= 30) return 'meta-warn';
    return 'meta-bad';
  }

  function renderMetaCampaigns(data) {
    state.cache.metaCampaigns = data;
    setText('meta-campaigns-range-label', data.rangeLabel || '');
    setText('meta-campaigns-account', data.accountName || data.accountId || '—');
    setText('meta-campaigns-synced', data.syncedUtc ? formatDisplayDate(data.syncedUtc) : '—');
    setMetaAccountChip(data.accountName || data.accountId || 'Connected');
    const note = document.getElementById('meta-campaigns-note');
    if (note) {
      note.textContent = data.comparisonNote
        || 'Meta Leads = reported by Meta Ads API. Website Leads = server-confirmed leads captured on your site. These may differ due to attribution windows, browser restrictions, CAPI config, or reporting delay.';
    }

    renderTable('meta-campaigns-body', data.rows || [], [
      {
        render: r => `${pill(r.campaignName || '—', `${metaCampaignNameClass(r)} meta-campaign-name-pill`)}<div class="fa-muted small mt-1">${r.campaignId || ''}</div>`
      },
      { render: r => pill(r.status || '—', metaStatusClass(r.status)) },
      { render: r => pill(r.objective || '—', metaObjectiveClass(r.objective)) },
      { render: r => pill(formatMoney(r.spend), metaSpendClass(r)), align: 'text-end' },
      { render: r => pill(formatInt(r.impressions), metaImpressionsClass(r.impressions)), align: 'text-end' },
      { render: r => pill(formatInt(r.reach), metaReachClass(r.reach)), align: 'text-end' },
      { render: r => pill(formatInt(r.clicks), metaClicksClass(r.clicks)), align: 'text-end' },
      { render: r => pill(formatPct(r.ctr), metaCtrClass(r.ctr)), align: 'text-end' },
      { render: r => pill(formatMoney(r.cpc), metaCpcClass(r.cpc)), align: 'text-end' },
      { render: r => pill(formatMoney(r.cpm), metaCpmClass(r.cpm)), align: 'text-end' },
      { render: r => pill(formatInt(r.leads), metaLeadsClass(r.leads)), align: 'text-end' },
      { render: r => pill(formatInt(r.websiteLeads), metaLeadsClass(r.websiteLeads)), align: 'text-end' },
      { render: r => pill(formatInt(r.websiteLeadGap), metaLeadGapClass(r.websiteLeadGap)), align: 'text-end' }
    ]);
  }

  function setMetaCampaignsEnabled(enabled) {
    const btn = document.getElementById('meta-campaigns-open');
    if (!btn) return;
    btn.disabled = !enabled;
    btn.setAttribute('aria-disabled', enabled ? 'false' : 'true');
    btn.title = enabled ? 'View Meta campaigns' : 'Connect Meta Ads to view campaigns';
  }

  function setMetaAccountChip(text, connected = true) {
    const chip = document.getElementById('meta-campaigns-account-chip');
    if (!chip) return;
    chip.classList.remove('d-none');
    chip.textContent = text || (connected ? 'Connected' : 'Not connected');
    chip.style.opacity = connected ? '1' : '.75';
  }

  async function loadMetaCampaigns() {
    try {
      const data = await fetchJson('metacampaigns', endpoints.metaCampaigns, rangeParams());
      if (!data) return;
      renderMetaCampaigns(data);
    } catch (err) {
      const body = document.getElementById('meta-campaigns-body');
      const message = (err && err.message) ? err.message : 'Unable to load Meta campaigns.';
      if (body) {
        body.innerHTML = `<tr><td colspan="13" class="text-danger">${message}</td></tr>`;
      }
      console.error(err);
    }
  }

  function formatShortDate(iso) {
    if (!iso) return '—';
    try {
      return new Date(iso).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' });
    } catch {
      return '—';
    }
  }

  async function loadMetaConnectionStatus() {
    const statusEl = document.getElementById('meta-connection-status');
    const connectBtn = document.getElementById('meta-connect-btn');
    const disconnectBtn = document.getElementById('meta-disconnect-btn');
    const statusBaseClass = 'wa-kpi-meta-status small';
    if (!statusEl) return;

    try {
      const data = await fetchJson('meta-connection-status', endpoints.metaConnectionStatus, {});
      if (!data || !data.connected) {
        statusEl.className = `${statusBaseClass} text-warning`;
        statusEl.textContent = 'Meta Ads not connected for this agent.';
        if (connectBtn) connectBtn.textContent = 'Connect Meta Ads';
        if (disconnectBtn) disconnectBtn.style.display = 'none';
        setMetaCampaignsEnabled(false);
        setMetaAccountChip('Not connected', false);
        return;
      }

      const acct = data.accountName || data.accountId || 'Meta account connected';
      const user = data.metaUserName ? ` as ${data.metaUserName}` : '';
      const exp = data.accessTokenExpiresUtc ? ` · expires ${formatShortDate(data.accessTokenExpiresUtc)}` : '';
      statusEl.className = `${statusBaseClass} text-success`;
      statusEl.textContent = `Connected: ${acct}${user}${exp}`;
      if (connectBtn) connectBtn.textContent = 'Reconnect Meta Ads';
      if (disconnectBtn) disconnectBtn.style.display = '';
      setMetaCampaignsEnabled(true);
      setMetaAccountChip(acct || 'Connected', true);
      const openAdsLink = document.getElementById('openMetaAdsManagerLink');
      if (openAdsLink && data && data.accountId) {
        const accountId = String(data.accountId).replace(/^act_/, '');
        const businessId = data.businessId ? String(data.businessId).trim() : '';
        const params = new URLSearchParams();
        if (businessId) {
          params.set('business_id', businessId);
          params.set('global_scope_id', businessId);
        }
        params.set('act', accountId);
        openAdsLink.href = `https://adsmanager.facebook.com/adsmanager/manage/campaigns?${params.toString()}`;
      }

    } catch (err) {
      statusEl.className = `${statusBaseClass} text-danger`;
      statusEl.textContent = 'Unable to read Meta Ads connection status.';
      if (disconnectBtn) disconnectBtn.style.display = 'none';
      setMetaCampaignsEnabled(false);
      setMetaAccountChip('Status unavailable', false);
      console.error(err);
    }
  }

  async function handleMetaDisconnect() {
    try {
      await fetchPostJson('meta-disconnect', endpoints.metaDisconnect);
      await loadMetaConnectionStatus();
      const body = document.getElementById('meta-campaigns-body');
      if (body) body.innerHTML = '<tr><td colspan="13" class="fa-empty">Disconnected. Reconnect Meta Ads to load campaigns.</td></tr>';
    } catch (err) {
      const statusEl = document.getElementById('meta-connection-status');
      if (statusEl) {
        statusEl.className = 'wa-kpi-meta-status small text-danger';
        statusEl.textContent = (err && err.message) ? err.message : 'Failed to disconnect Meta Ads.';
      }
      console.error(err);
    }
  }

  function showMetaCallbackBanner() {
    let url;
    try {
      url = new URL(window.location.href);
    } catch {
      return;
    }
    const meta = url.searchParams.get('meta');
    if (!meta) return;

    const statusEl = document.getElementById('meta-connection-status');
    if (statusEl) {
      if (meta === 'connected') {
        statusEl.className = 'wa-kpi-meta-status small text-success';
        statusEl.textContent = 'Meta Ads connected successfully.';
      } else if (meta === 'error') {
        const msg = url.searchParams.get('message') || 'Meta Ads connection failed.';
        statusEl.className = 'wa-kpi-meta-status small text-danger';
        statusEl.textContent = msg;
      }
    }

    url.searchParams.delete('meta');
    url.searchParams.delete('message');
    window.history.replaceState({}, '', url.toString());
  }

  // Team rollup (founder-only)
  function renderList(id, items, formatter) {
    const el = document.getElementById(id);
    if (!el) return;
    if (!items || !items.length) {
      el.innerHTML = `<li class=\"fa-empty\">No data in range</li>`;
      return;
    }
    el.innerHTML = items.map(formatter).join('');
  }

  async function loadTeamRollup() {
    const params = rangeParams({ team: true });
    const [summary, traffic, quote, conv, agentPerf] = await Promise.all([
      fetchJson('team-summary', endpoints.summary, params),
      fetchJson('team-traffic', endpoints.traffic, params),
      fetchJson('team-quote', endpoints.quote, params),
      fetchJson('team-conv', endpoints.conversions, params),
      fetchJson('team-agentperf', endpoints.agentPerf, params)
    ]);
    if (!summary) return;

    // Top KPI row
    setText('team-kpi-pageviews', summary.pageViews);
    setText('team-kpi-visitors', summary.uniqueVisitors);
    setText('team-kpi-sessions', summary.sessions);
    setText('team-kpi-leads', summary.verifiedLeads);
    setText('team-kpi-session', `${summary.sessionConversionRate}%`);
    setText('team-kpi-intent', summary.intentAvailable ? `${summary.intentConversionRate}%` : '—');
    setText('team-range-label', summary.rangeLabel || '');

    // Secondary row
    setText('team-top-page', summary.topPage || '—');
    setText('team-top-cta', summary.topCta || '—');
    if (quote) {
      setText('team-quote-starts', quote.quoteStarts ?? 0);
      setText('team-quote-form-starts', quote.quoteFormStarts ?? 0);
      setText('team-quote-submits', quote.quoteFormSubmits ?? 0);
    } else {
      setText('team-quote-starts', 0);
      setText('team-quote-form-starts', 0);
      setText('team-quote-submits', 0);
    }
    setText('team-total-conv', conv?.totalConversions ?? 0);

    // Lists
    if (traffic) {
      renderList('team-top-pages-list', (traffic.topPages || []).slice(0, 6), p => `<li class="d-flex justify-content-between"><span>${p.key}</span><span class="text-silver">${p.count}</span></li>`);
      renderList('team-top-ctas-list', (traffic.topCtas || []).slice(0, 6), p => `<li class="d-flex justify-content-between"><span>${p.key}</span><span class="text-silver">${p.count}</span></li>`);
      renderList('team-top-sources-list', (traffic.topSources || []).slice(0, 6), p => `<li class="d-flex justify-content-between"><span>${p.key}</span><span class="text-silver">${p.count}</span></li>`);
      renderList('team-top-campaigns-list', (traffic.topCampaigns || []).slice(0, 6), p => `<li class="d-flex justify-content-between"><span>${p.key}</span><span class="text-silver">${p.count}</span></li>`);
      renderList('team-recent-activity', (traffic.recentActivity || []).slice(0, 10), a => `<li class="mb-1"><span class="text-silver">${formatDisplayDate(a.eventUtc)}</span> · <strong>${a.eventType}</strong> · ${a.pageKey || ''}${a.elementKey ? ' / ' + a.elementKey : ''}</li>`);
    }

    // Agent leaderboard
    if (agentPerf && agentPerf.rows) {
      renderTable('team-agent-rows', agentPerf.rows, [
        { key: 'agentName' },
        { key: 'slug' },
        { key: 'leads', align: 'text-end' },
        { key: 'conversions', align: 'text-end' },
        { key: 'sessions', align: 'text-end' },
        { render: r => `${r.intentConversionRate}%`, align: 'text-end' },
        { key: 'topSource' }
      ]);
    } else {
      renderTable('team-agent-rows', [], [{ key: 'agentName' }]);
    }
  }
// Modal wiring -----------------------------------------------------
  function attachModal(id, loader) {
    const el = document.getElementById(id);
    if (!el) return;
    el.addEventListener('show.bs.modal', () => {
      state.openModal = id;
      try {
        window.sessionStorage.setItem(OPEN_MODAL_STORAGE_KEY, id);
      } catch {
        // ignore storage issues
      }
      loader();
    });
    el.addEventListener('hidden.bs.modal', () => {
      state.openModal = null;
      try {
        const current = window.sessionStorage.getItem(OPEN_MODAL_STORAGE_KEY);
        if (current === id) {
          window.sessionStorage.removeItem(OPEN_MODAL_STORAGE_KEY);
        }
      } catch {
        // ignore storage issues
      }
    });
  }

  function restoreOpenModalFromSession() {
    let modalId = null;
    try {
      modalId = window.sessionStorage.getItem(OPEN_MODAL_STORAGE_KEY);
    } catch {
      modalId = null;
    }

    if (!modalId) return;

    const modalEl = document.getElementById(modalId);
    if (!modalEl || typeof bootstrap === 'undefined' || !bootstrap?.Modal) return;

    const bsModal = bootstrap.Modal.getOrCreateInstance(modalEl);
    bsModal.show();
  }

  function initRangeControls() {
    syncRangeInputsFromState();

    document.querySelectorAll('.btn-range[data-range]').forEach(btn => {
      btn.addEventListener('click', () => {
        document.querySelectorAll('.btn-range[data-range]').forEach(b => b.classList.remove('active'));
        btn.classList.add('active');
        state.scope.preset = btn.dataset.range || 'today';
        if (state.scope.preset !== 'custom') {
          state.scope.from = null;
          state.scope.to = null;
          syncRangeInputsFromState();
          syncRangeQueryParams();
          loadSummary();
          refreshOpenModal();
        }
      });
    });

    const applyCustom = document.getElementById('apply-custom-range');
    if (applyCustom) {
      applyCustom.addEventListener('click', () => {
        const from = document.getElementById('range-from')?.value;
        const to = document.getElementById('range-to')?.value;
        const notice = document.getElementById('fa-range-error');
        if (notice) notice.textContent = '';
        if (!from || !to) {
          if (notice) notice.textContent = 'Select both start and end dates.';
          return;
        }
        const parsedFrom = parseIsoInput(from);
        const parsedTo = parseIsoInput(to);
        if (!parsedFrom || !parsedTo) {
          if (notice) notice.textContent = 'Dates must be valid.';
          return;
        }
        if (parsedFrom.date > parsedTo.date) {
          if (notice) notice.textContent = 'Start date must be before end date.';
          return;
        }
        state.preset = 'custom';
        state.scope.preset = 'custom';
        state.scope.from = parsedFrom.iso;
        state.scope.to = parsedTo.iso;
        document.querySelectorAll('.btn-range[data-range]').forEach(b => b.classList.remove('active'));
        syncRangeInputsFromState();
        syncRangeQueryParams();
        loadSummary();
        refreshOpenModal();
      });
    }
  }

  function parseIsoInput(str) {
    const parts = str.split('-');
    if (parts.length !== 3) return null;
    const [yyyy, mm, dd] = parts.map(p => parseInt(p, 10));
    if (!yyyy || !mm || !dd || mm < 1 || mm > 12 || dd < 1 || dd > 31) return null;
    const date = new Date(Date.UTC(yyyy, mm - 1, dd));
    if (isNaN(date.getTime())) return null;
    const iso = `${yyyy.toString().padStart(4, '0')}-${mm.toString().padStart(2, '0')}-${dd.toString().padStart(2, '0')}`;
    return { date, iso };
  }

  function refreshOpenModal() {
    switch (state.openModal) {
      case 'trafficModal': loadTraffic(); break;
      case 'pagePerfModal': loadPagePerf(); break;
      case 'ctaPerfModal': loadCtaPerf(); break;
      case 'quoteModal': loadQuote(); break;
      case 'convModal': loadConv(); break;
      case 'leadsModal': loadLeads(); break;
      case 'metaSignalModal': loadMetaSignal(); break;
      case 'agentPerfModal': loadAgentPerf(); break;
      case 'metaCampaignsModal': loadMetaCampaigns(); break;
      case 'behaviorModal': loadBehavior(); break;
      case 'aiReviewSnapshotModal': loadAiReviewSnapshot(); break;
      default: loadSummary(); break;
    }
  }

  function initPolling() {
    setInterval(() => {
      refreshOpenModal();
    }, state.pollMs);
  }

  function initModules() {
    const map = {
      'mod-traffic': 'trafficModal',
      'mod-page': 'pagePerfModal',
      'mod-cta': 'ctaPerfModal',
      'mod-quote': 'quoteModal',
      'mod-conv': 'convModal',
      'mod-leads': 'leadsModal',
      'mod-meta-signal': 'metaSignalModal',
      'mod-behavior': 'behaviorModal'
    };
    if (isFounder) {
      map['mod-agentperf'] = 'agentPerfModal';
    }
    Object.entries(map).forEach(([cardId, modalId]) => {
      const card = document.getElementById(cardId);
      if (!card) return;
      const openModal = () => {
        const modalEl = document.getElementById(modalId);
        if (!modalEl) return;
        const bsModal = new bootstrap.Modal(modalEl);
        bsModal.show();
      };
      card.addEventListener('click', openModal);
      card.addEventListener('keydown', (event) => {
        if (event.key !== 'Enter' && event.key !== ' ') {
          return;
        }
        event.preventDefault();
        openModal();
      });
    });
  }

  function downloadCsv(filename, rows, columns) {
    if (!rows || !rows.length) return;
    const header = columns.map(c => `"${c.header}"`).join(',');
    const body = rows.map(r => columns.map(c => {
      const val = typeof c.selector === 'function' ? c.selector(r) : (r[c.selector] ?? '');
      return `"${String(val).replace(/\"/g,'\"\"')}"`;
    }).join(',')).join('\\n');
    const blob = new Blob([header + '\\n' + body], { type: 'text/csv;charset=utf-8;' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url; a.download = filename;
    document.body.appendChild(a); a.click(); document.body.removeChild(a);
    URL.revokeObjectURL(url);
  }

  function syncWebsiteAnalyticsBridge() {
    window.websiteAnalyticsBridge = {
      getState() {
        const customRange = resolveCustomRangeUtc();
        return {
          preset: state.scope.preset || state.preset || 'today',
          scope: {
            preset: state.scope.preset || state.preset || 'today',
            from: state.scope.from || null,
            to: state.scope.to || null,
            fromUtc: customRange?.fromUtc || null,
            toUtc: customRange?.toUtc || null,
            agentProfileId: state.scope.agentProfileId || null,
            scopeLabel: state.scope.scopeLabel || 'Global'
          },
          trafficType: { ...state.trafficType }
        };
      },
      getRangeParams(options) {
        return rangeParams(options || {});
      }
    };
  }

  async function init() {
    const tzTextEl = document.getElementById('wa-tz-text');
    if (tzTextEl) {
      tzTextEl.textContent = viewerTz.id || 'Local Timezone';
    }
    showMetaCallbackBanner();
    initScopeControls();
    updateGrowthBaseLink();
    // load initial summary from server-provided JSON if present
    const initial = shell?.dataset.initialSummary;
    const parsed = initial ? parseInitialSummary(initial) : null;
    if (parsed) {
      renderSummary(parsed);
    }
    // Always fetch to ensure fresh data and to cover cases where initial hydration was absent/invalid
    try {
      await loadSummary();
    } catch (err) {
      console.error('Initial load failed', err);
    }
    initRangeControls();
    initModules();
    initTrafficTypeControls();
    initMetaSignalFilterControls();
    initLeadDeleteControls();
    initModalCopyButtons();
    initCopyAllModulesButton();
    initMarketingHealthInspector();
    initMarketingHealthCopyable();
    attachModal('trafficModal', () => { updateTrafficTypeHeader('trafficModal'); loadTraffic(); });
    attachModal('pagePerfModal', () => { updateTrafficTypeHeader('pagePerfModal'); loadPagePerf(); });
    attachModal('ctaPerfModal', () => { updateTrafficTypeHeader('ctaPerfModal'); loadCtaPerf(); });
    attachModal('quoteModal', () => { updateTrafficTypeHeader('quoteModal'); loadQuote(); });
    attachModal('convModal', () => { updateTrafficTypeHeader('convModal'); loadConv(); });
    attachModal('leadsModal', () => { updateTrafficTypeHeader('leadsModal'); loadLeads(); });
    attachModal('metaSignalModal', () => { updateTrafficTypeHeader('metaSignalModal'); loadMetaSignal(); });
    attachModal('metaCampaignsModal', loadMetaCampaigns);
    attachModal('behaviorModal', loadBehavior);
    attachModal('aiReviewSnapshotModal', loadAiReviewSnapshot);
    if (isFounder) {
      attachModal('agentPerfModal', loadAgentPerf);
    }
    restoreOpenModalFromSession();

    const teamBtn = document.getElementById('team-rollup-btn');
    const teamModal = document.getElementById('teamRollupModal');
    if (teamBtn && teamModal) {
      teamBtn.addEventListener('click', () => {
        loadTeamRollup();
      });
      teamModal.addEventListener('show.bs.modal', () => {
        loadTeamRollup();
      });
    }

    const aiSnapshotRefresh = document.getElementById('ai-snapshot-refresh');
    if (aiSnapshotRefresh) {
      aiSnapshotRefresh.addEventListener('click', () => {
        loadAiReviewSnapshot();
      });
    }

    const aiSnapshotCopy = document.getElementById('ai-snapshot-copy');
    if (aiSnapshotCopy) {
      aiSnapshotCopy.addEventListener('click', async () => {
        const textEl = document.getElementById('ai-snapshot-text');
        const snapshotText = (textEl && textEl.value) ? textEl.value : (state.cache.aiSnapshot?.snapshotText || '');
        if (!snapshotText) {
          setAiSnapshotStatus('No snapshot text available to copy.', 'warning');
          return;
        }
        const copied = await copyTextWithFallback(snapshotText);
        if (copied) {
          const original = aiSnapshotCopy.textContent;
          aiSnapshotCopy.textContent = 'Copied';
          setAiSnapshotStatus('Snapshot copied to clipboard.', 'success');
          setTimeout(() => {
            aiSnapshotCopy.textContent = original || 'Copy';
          }, 1200);
        } else {
          setAiSnapshotStatus('Clipboard copy failed in this browser context.', 'error');
        }
      });
    }

    const exportConv = document.getElementById('export-conv');
    if (exportConv) exportConv.addEventListener('click', async () => {
      const original = exportConv.textContent;
      exportConv.disabled = true;
      exportConv.textContent = 'Exporting…';
      try {
        const params = rangeParams({ modal: 'convModal' });
        params.recentTake = 5000;
        const data = await fetchJson('export-conv', endpoints.conversions, params);
        if (!data || !data.recent || !data.recent.length) return;
        downloadCsv('conversions.csv', data.recent, [
          { header: 'WhenUtc', selector: r => toUtcIso(r.eventUtc) },
          { header: 'ReportTimezone', selector: () => 'UTC' },
          { header: 'Event', selector: 'eventType' },
          { header: 'QuoteType', selector: r => r.quoteType || '' },
          { header: 'Page', selector: r => r.pageKey || '' },
          { header: 'CTA', selector: r => r.sourceCta || '' },
          { header: 'ResolvedSource', selector: r => r.sourceLabel || '' }
        ]);
      } catch (err) {
        console.error(err);
      } finally {
        exportConv.disabled = false;
        exportConv.textContent = original || 'Export';
      }
    });
    const exportLeads = document.getElementById('export-leads');
    if (exportLeads) exportLeads.addEventListener('click', async () => {
      const original = exportLeads.textContent;
      exportLeads.disabled = true;
      exportLeads.textContent = 'Exporting…';
      try {
        const params = rangeParams({ modal: 'leadsModal' });
        params.limit = 5000;
        const data = await fetchJson('export-leads', endpoints.leads, params);
        if (!data || !data.leads || !data.leads.length) return;
        downloadCsv('leads.csv', data.leads, [
          { header: 'CreatedUtc', selector: r => toUtcIso(r.createdUtc) },
          { header: 'ReportTimezone', selector: () => 'UTC' },
          { header: 'Name', selector: 'name' },
          { header: 'Email', selector: 'email' },
          { header: 'Phone', selector: 'phone' },
          { header: 'Interest', selector: 'interest' },
          { header: 'LeadSource', selector: r => r.leadSource || '' },
          { header: 'UtmSource', selector: r => r.utmSource || '' },
          { header: 'UtmMedium', selector: r => r.utmMedium || '' },
          { header: 'UtmCampaign', selector: r => r.utmCampaign || '' },
          { header: 'UtmId', selector: r => r.utmId || '' },
          { header: 'MetaCampaignId', selector: r => r.metaCampaignId || '' },
          { header: 'MetaAdSetId', selector: r => r.metaAdSetId || '' },
          { header: 'MetaAdId', selector: r => r.metaAdId || '' },
          { header: 'ResolvedSource', selector: r => r.resolvedSource || '' },
          { header: 'ResolvedMedium', selector: r => r.resolvedMedium || '' },
          { header: 'ResolvedCampaign', selector: r => r.resolvedCampaign || '' },
          { header: 'ResolvedUtmId', selector: r => r.resolvedUtmId || '' },
          { header: 'ResolvedContent', selector: r => r.resolvedContent || '' },
          { header: 'ResolvedTerm', selector: r => r.resolvedTerm || '' },
          { header: 'ResolvedMetaCampaignId', selector: r => r.resolvedMetaCampaignId || '' },
          { header: 'ResolvedMetaAdSetId', selector: r => r.resolvedMetaAdSetId || '' },
          { header: 'ResolvedMetaAdId', selector: r => r.resolvedMetaAdId || '' },
          { header: 'ResolvedFbclidPresent', selector: r => !!r.resolvedFbclidPresent },
          { header: 'AttributionResolution', selector: r => r.attribution?.resolutionSource || 'unknown' },
          { header: 'MetaLeadEventId', selector: r => r.metaTracking?.metaLeadEventId || '' },
          { header: 'ResolvedMetaPixelId', selector: r => r.metaTracking?.resolvedMetaPixelId || '' },
          { header: 'PixelOwnerType', selector: r => r.metaTracking?.pixelOwnerType || '' },
          { header: 'BrowserPixelStatus', selector: r => r.metaTracking?.browserPixelStatus || '' },
          { header: 'ServerCapiStatus', selector: r => r.metaTracking?.serverCapiStatus || '' },
          { header: 'MetaDedupReady', selector: r => !!r.metaTracking?.dedupReady }
        ]);
      } catch (err) {
        console.error(err);
      } finally {
        exportLeads.disabled = false;
        exportLeads.textContent = original || 'Export';
      }
    });

    const exportBehavior = document.getElementById('export-behavior');
    if (exportBehavior) exportBehavior.addEventListener('click', async () => {
      const original = exportBehavior.textContent;
      exportBehavior.disabled = true;
      exportBehavior.textContent = 'Exporting…';
      try {
        const data = await fetchJson('export-behavior', endpoints.behaviorSources, rangeParams({ modal: 'behaviorModal' }));
        if (!data || !data.rows || !data.rows.length) return;
        downloadCsv('behavior-sources.csv', data.rows, [
          { header: 'ViewerTimezone', selector: () => viewerTz.id || 'Local Time' },
          { header: 'Source', selector: 'source' },
          { header: 'Medium', selector: r => r.medium || '' },
          { header: 'Campaign', selector: r => r.campaign || '' },
          { header: 'LandingPage', selector: r => r.landingPage || '' },
          { header: 'Sessions', selector: 'sessions' },
          { header: 'EngagedSessions', selector: 'engagedSessions' },
          { header: 'VerifiedLeads', selector: 'verifiedLeads' },
          { header: 'SessionConversionRate', selector: r => formatPct(r.sessionConversionRate) },
          { header: 'AvgDwell', selector: r => formatMs(r.avgDwellMs) },
          { header: 'DwellSamples', selector: r => r.avgDwellSampleCount ?? 0 }
        ]);
      } catch (err) {
        console.error(err);
      } finally {
        exportBehavior.disabled = false;
        exportBehavior.textContent = original || 'Export';
      }
    });

    const copyLink = document.getElementById('fa-copy-link');
    if (copyLink) {
      copyLink.addEventListener('click', () => {
        const val = copyLink.dataset.link;
        if (!val) return;
        if (navigator.clipboard?.writeText) {
          navigator.clipboard.writeText(val);
        } else {
          const tmp = document.createElement('textarea');
          tmp.value = val; document.body.appendChild(tmp); tmp.select(); document.execCommand('copy'); document.body.removeChild(tmp);
        }
        copyLink.textContent = 'Copied';
        setTimeout(() => copyLink.textContent = 'Copy', 1200);
      });
    }
    wireGrowthCopyButtons();
    wireProductLinks();

    const metaDisconnectBtn = document.getElementById('meta-disconnect-btn');
    if (metaDisconnectBtn) {
      metaDisconnectBtn.addEventListener('click', handleMetaDisconnect);
    }
    await loadMetaConnectionStatus();

    initPolling();

    // ── KPI modal state bridge ───────────────────────────────────────────────
    // Expose current page state so website-analytics-kpi-modal.js can read
    // the live preset/from/to/trafficType without duplicating internal state.
    // The modal JS reads window.__waState on each card click, so it always
    // reflects the most recently applied filter (not a stale snapshot).
    window.__waState = {
      get preset()      { return state.scope.preset || 'today'; },
      get from()        { return state.scope.from || null; },
      get to()          { return state.scope.to || null; },
      get trafficType() { return 'all'; },
      get agentProfileId() { return state.scope.agentProfileId || null; },
      get isFounder()      { return isFounder; },
      get scopeLabel()     { return state.scope.scopeLabel || 'Global'; },
      rangeParams(options) { return rangeParams(options || {}); }
    };
    syncWebsiteAnalyticsBridge();
  }

  function renderAgentPerf(data) {
    state.cache.agentPerf = data;
    setText('agentperf-range-label', data.rangeLabel || '');
    renderTable('agentperf-body', data.rows || [], [
      { key: 'agentName' },
      { key: 'slug' },
      { key: 'leads', align: 'text-end' },
      { key: 'conversions', align: 'text-end' },
      { render: r => `${r.sessionConversionRate}%`, align: 'text-end' },
      { render: r => `${r.intentConversionRate}%`, align: 'text-end' },
      { key: 'topSource' },
      { render: r => r.lowSample ? 'Low sample' : '', align: 'text-end' }
    ]);
  }

  syncWebsiteAnalyticsBridge();
  document.addEventListener('DOMContentLoaded', init);

  // ── Product-specific links (discovered paid landing routes) ───────────────
  function normalizeLandingRoutes(routes) {
    const activeBaseLink = resolveLandingRouteBaseLink();

    return (Array.isArray(routes) ? routes : [])
      .filter(route => route && route.isActive !== false)
      .map(route => {
        const normalizedRoute = {
          key: route.key || '',
          displayName: route.displayName || route.key || 'Landing Route',
          basePath: route.basePath || '',
          controlPath: route.controlPath || route.basePath || '',
          quoteType: route.quoteType || '',
          pageMode: route.pageMode || '',
          defaultPageVariant: route.defaultPageVariant || 'landing',
          effectivePageKeys: Array.isArray(route.effectivePageKeys) ? route.effectivePageKeys : [],
          notes: route.notes || '',
          comparisonHelperText: route.comparisonHelperText || '',
          isPaidLanding: route.isPaidLanding !== false
        };

        const availableVariants = Array.isArray(route.availableVariants)
          ? route.availableVariants
            .filter(variant => variant && variant.isActive !== false)
            .map(variant => {
              const scopedUrl = buildScopedLandingVariantUrl(activeBaseLink, normalizedRoute, variant);
              return {
                ...variant,
                url: scopedUrl,
                suggestedMetaDestinationUrl: scopedUrl
              };
            })
          : [];

        const controlVariant = availableVariants.find(variant => variant.isControl)
          || availableVariants[0]
          || null;

        return {
          ...normalizedRoute,
          availableVariants,
          controlUrl: buildScopedLandingRouteUrl(activeBaseLink, normalizedRoute.controlPath) || route.controlUrl || controlVariant?.url || ''
        };
      })
      .filter(route => route.controlUrl || route.availableVariants.length > 0);
  }

  function resolveLandingRouteBaseLink() {
    return currentBaseLink() || landingRoutesBaseUrl || '';
  }

  function extractAgentPathPrefix(baseLink) {
    if (!baseLink) return '';

    try {
      const { pathname } = new URL(baseLink);
      const segments = pathname.split('/').filter(Boolean);
      if (segments.length >= 2 && segments[0].toLowerCase() === 'a' && segments[1]) {
        return `/a/${segments[1]}`;
      }
    } catch {
      // ignore invalid URL and fall back below
    }

    return '';
  }

  function buildScopedLandingRouteUrl(baseLink, basePath) {
    const normalizedBasePath = String(basePath || '').trim();
    const effectiveBaseLink = String(baseLink || '').trim();
    if (!normalizedBasePath || !effectiveBaseLink) return '';

    const routePath = normalizedBasePath.startsWith('/') ? normalizedBasePath : `/${normalizedBasePath}`;

    try {
      const url = new URL(effectiveBaseLink);
      const agentPrefix = extractAgentPathPrefix(effectiveBaseLink);
      url.pathname = `${agentPrefix}${routePath}`.replace(/\/{2,}/g, '/');
      url.search = '';
      url.hash = '';
      return url.toString();
    } catch {
      return '';
    }
  }

  function appendLandingVariantQuery(url, variantToken) {
    const absoluteUrl = String(url || '').trim();
    const normalizedVariant = String(variantToken || '').trim();
    if (!absoluteUrl || !normalizedVariant) return absoluteUrl;

    try {
      const builder = new URL(absoluteUrl);
      builder.searchParams.set('variant', normalizedVariant);
      return builder.toString();
    } catch {
      const joiner = absoluteUrl.includes('?') ? '&' : '?';
      return `${absoluteUrl}${joiner}variant=${encodeURIComponent(normalizedVariant)}`;
    }
  }

  function buildScopedLandingVariantUrl(baseLink, route, variant) {
    const controlUrl = buildScopedLandingRouteUrl(baseLink, route?.basePath || '');
    if (!controlUrl) {
      return variant?.url || variant?.suggestedMetaDestinationUrl || '';
    }

    const defaultVariant = String(route?.defaultPageVariant || 'landing').trim().toLowerCase();
    const variantToken = String(variant?.variant || route?.defaultPageVariant || 'landing').trim();
    const isControl = !!variant?.isControl || variantToken.toLowerCase() === defaultVariant;

    return isControl ? controlUrl : appendLandingVariantQuery(controlUrl, variantToken);
  }

  function truncateUrl(url) {
    const value = String(url || '');
    if (value.length <= 72) return value;
    return `${value.slice(0, 69)}...`;
  }

  function renderLandingVariantRows(route) {
    const variants = route.availableVariants;
    if (!variants.length) {
      return `
        <tr>
          <td colspan="6">
            <div class="landing-route-empty">No active variants are configured for this route yet.</div>
          </td>
        </tr>
      `;
    }

    return variants.map((variant) => {
      const toneClass = variant.isControl ? 'link-website' : 'link-landing';
      const badge = variant.isControl
        ? '<span class="badge-landing badge-control">CONTROL</span>'
        : '<span class="badge-landing">TEST</span>';
      const description = variant.description
        ? `<div class="landing-route-row-description">${escapeHtml(variant.description)}</div>`
        : '';
      const landingUrl = escapeHtml(variant.url || '');
      const suggestedUrl = escapeHtml(variant.suggestedMetaDestinationUrl || variant.url || '');
      const buttonUrl = escapeHtml(variant.url || '');

      return `
        <tr>
          <td>
            <div class="landing-route-variant-name">${escapeHtml(variant.displayName || variant.variant || 'Variant')}${badge}</div>
            ${description}
          </td>
          <td>
            <span class="landing-route-pill">${escapeHtml(variant.variant || route.defaultPageVariant || 'landing')}</span>
          </td>
          <td>
            <div class="landing-route-key">${escapeHtml(variant.effectivePageKey || '—')}</div>
          </td>
          <td>
            <a class="landing-route-url" href="${landingUrl}" target="_blank" rel="noopener">${escapeHtml(variant.url || '—')}</a>
          </td>
          <td>
            <a class="landing-route-url" href="${suggestedUrl}" target="_blank" rel="noopener">${escapeHtml(variant.suggestedMetaDestinationUrl || variant.url || '—')}</a>
          </td>
          <td>
            <div class="product-link-actions landing-route-actions">
              <button type="button" class="product-link-copy ${toneClass}" data-link-url="${buttonUrl}">Copy URL</button>
              <button type="button" class="product-link-open ${toneClass}" data-link-url="${buttonUrl}">Open URL</button>
            </div>
          </td>
        </tr>
      `;
    }).join('');
  }

  function renderLandingRouteCards(routes) {
    if (!routes.length) {
      return `
        <div class="landing-route-empty">
          No active paid landing routes were discovered. Add routes under <code>LandingRoutes</code> in app settings to populate this panel.
        </div>
      `;
    }

    return routes.map((route) => {
      const routeMeta = [
        route.quoteType ? `Quote Type: ${escapeHtml(route.quoteType)}` : '',
        route.pageMode ? `Page Mode: ${escapeHtml(route.pageMode)}` : '',
        route.basePath ? `Base Path: ${escapeHtml(route.basePath)}` : ''
      ].filter(Boolean).join(' • ');
      const notes = route.notes
        ? `<div class="landing-route-notes">${escapeHtml(route.notes)}</div>`
        : '';
      const comparison = route.comparisonHelperText
        ? `<div class="landing-route-compare">${escapeHtml(route.comparisonHelperText)}</div>`
        : '';
      const controlButtonUrl = escapeHtml(route.controlUrl || '');
      const controlLabel = route.controlPath && route.controlPath !== route.basePath
        ? 'Website Control URL'
        : 'Control URL';

      return `
        <article class="landing-route-card">
          <div class="landing-route-card-head">
            <div>
              <div class="landing-route-title">${escapeHtml(route.displayName)}</div>
              <div class="landing-route-meta">${routeMeta}</div>
            </div>
            <div class="landing-route-badge">${route.isPaidLanding ? 'Paid Landing' : 'Landing Route'}</div>
          </div>
          <div class="landing-route-control-shell">
            <div class="landing-route-control-copy">
              <div class="landing-route-control-label">${controlLabel}</div>
              <a class="landing-route-url" href="${escapeHtml(route.controlUrl || '')}" target="_blank" rel="noopener">${escapeHtml(route.controlUrl || '—')}</a>
            </div>
            <div class="product-link-actions landing-route-actions">
              <button type="button" class="product-link-copy link-website" data-link-url="${controlButtonUrl}">Copy URL</button>
              <button type="button" class="product-link-open link-website" data-link-url="${controlButtonUrl}">Open URL</button>
            </div>
          </div>
          ${notes}
          ${comparison}
          <div class="landing-route-table-wrap">
            <table class="landing-route-table">
              <thead>
                <tr>
                  <th>Offer / Variant</th>
                  <th>Page Variant</th>
                  <th>Effective Page Key</th>
                  <th>Landing URL</th>
                  <th>Suggested Meta Destination URL</th>
                  <th>Actions</th>
                </tr>
              </thead>
              <tbody>
                ${renderLandingVariantRows(route)}
              </tbody>
            </table>
          </div>
        </article>
      `;
    }).join('');
  }

  function wireProductLinks() {
    const modal = document.getElementById('landingRoutesModal');
    const list = document.getElementById('product-links-list');
    const display = document.getElementById('product-link-display');
    const baseUrlEl = document.getElementById('landing-routes-base-url');
    if (!list) return;

    const renderProductLinks = () => {
      const activeBaseLink = resolveLandingRouteBaseLink() || landingRoutesBaseUrl || '';
      const activeRoutes = normalizeLandingRoutes(landingRoutes);
      list.innerHTML = renderLandingRouteCards(activeRoutes);
      if (baseUrlEl) {
        baseUrlEl.textContent = activeBaseLink || '—';
      }
    };

    renderProductLinks();

    if (!list.dataset.wired) {
      list.addEventListener('click', e => {
        const copyBtn = e.target.closest('.product-link-copy');
        const openBtn = e.target.closest('.product-link-open');
        if (!copyBtn && !openBtn) return;
        const url = (copyBtn || openBtn).dataset.linkUrl || '';
        if (!url) return;
        if (display) display.value = url;
        if (copyBtn) {
          copyToClipboard(url);
          copyBtn.textContent = 'Copied!';
          setTimeout(() => { copyBtn.textContent = 'Copy URL'; }, 1500);
        }
        if (openBtn) {
          window.open(url, '_blank', 'noopener');
        }
      });

      modal?.addEventListener('hidden.bs.modal', () => {
        if (display) {
          display.value = '';
        }
      });

      list.dataset.wired = 'true';
    }

    list._rerenderProductLinks = renderProductLinks;
  }

  function rerenderProductLinks() {
    const list = document.getElementById('product-links-list');
    const renderProductLinks = list?._rerenderProductLinks;
    if (typeof renderProductLinks === 'function') {
      renderProductLinks();
    }
  }

  function currentBaseLink() {
    const agentId = state.scope.agentProfileId;
    if (agentId && agentOptions && agentOptions.length) {
      const match = agentOptions.find(a => String(a?.id || '') === String(agentId));
      if (match?.primaryUrl) return match.primaryUrl;
    }
    return shell?.dataset.personalLink || '';
  }

  function copyToClipboard(val) {
    if (!val) return;
    if (navigator.clipboard?.writeText) {
      navigator.clipboard.writeText(val);
    } else {
      const tmp = document.createElement('textarea');
      tmp.value = val; document.body.appendChild(tmp); tmp.select(); document.execCommand('copy'); document.body.removeChild(tmp);
    }
  }

  function wireGrowthCopyButtons() {
    const baseBtn = document.getElementById('growth-copy-base');
    if (baseBtn) baseBtn.addEventListener('click', () => { copyToClipboard(currentBaseLink()); baseBtn.textContent = 'Copied'; setTimeout(() => baseBtn.textContent='Copy', 1200); });
  }

  function updateGrowthBaseLink() {
    const base = currentBaseLink();
    const baseEl = document.getElementById('growth-base-link');
    if (baseEl) baseEl.textContent = base;
    const baseCopy = document.getElementById('growth-copy-base');
    if (baseCopy) baseCopy.dataset.link = base;
    const baseOpen = document.getElementById('growth-open-base');
    if (baseOpen) baseOpen.href = base;
    rerenderProductLinks();
  }
})();



(function () {
  const modalId = 'deviceIntelligenceModal';
  const moduleBtn = document.getElementById('mod-device-intelligence');
  const modalEl = document.getElementById(modalId);
  if (!moduleBtn || !modalEl || !window.bootstrap) return;

  let localTraffic = 'all';

  function mapTraffic(value) {
    if (value === 'paid') return 'PaidAds';
    if (value === 'non_paid') return 'NonPaid';
    return 'All';
  }

  function deviceTrafficLabel(value = localTraffic) {
    if (value === 'paid') return 'Ads Only';
    if (value === 'non_paid') return 'Non-Ads Only';
    return 'All Traffic';
  }

  function currentRangeParams() {
    const bridge = window.websiteAnalyticsBridge || {};
    const params = new URLSearchParams();
    const baseParams = typeof bridge.getRangeParams === 'function'
      ? bridge.getRangeParams()
      : {};

    Object.entries(baseParams || {}).forEach(([key, value]) => {
      if (value == null || value === '') return;
      params.set(key, String(value));
    });

    if (!params.has('preset')) {
      params.set('preset', 'today');
    }

    params.set('trafficType', mapTraffic(localTraffic));
    return params;
  }

  function esc(v) {
    return String(v ?? '').replace(/[&<>"']/g, s => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[s]));
  }

  function renderDeviceLoadError(err) {
    const content = document.getElementById('deviceIntelligenceContent');
    if (content) {
      content.innerHTML = `<div class="alert alert-warning">${esc(err?.message || 'Unable to load Device Intelligence.')}</div>`;
    }
  }

  function table(title, rows) {
    rows = Array.isArray(rows) ? rows : [];
    if (!rows.length) {
      return `<section class="wa-panel"><h6>${esc(title)}</h6><div class="text-muted small">No data in this slice yet.</div></section>`;
    }

    return `
      <section class="wa-panel">
        <h6>${esc(title)}</h6>
        <div class="table-responsive">
          <table class="table table-sm wa-table align-middle">
            <thead>
              <tr>
                <th>Segment</th>
                <th>Sessions</th>
                <th>CTA</th>
                <th>Starts</th>
                <th>Leads</th>
                <th>Start %</th>
                <th>Lead %</th>
              </tr>
            </thead>
            <tbody>
              ${rows.map(r => `
                <tr>
                  <td>${esc(r.label)}</td>
                  <td>${r.sessions ?? 0}</td>
                  <td>${r.ctaClicks ?? 0}</td>
                  <td>${r.formStarts ?? 0}</td>
                  <td>${r.confirmedLeads ?? 0}</td>
                  <td>${r.startRate ?? 0}%</td>
                  <td>${r.leadRate ?? 0}%</td>
                </tr>`).join('')}
            </tbody>
          </table>
        </div>
      </section>`;
  }

  async function loadDeviceIntelligence() {
    const content = document.getElementById('deviceIntelligenceContent');
    if (content) content.innerHTML = '<div class="wa-loading">Loading device intelligence...</div>';

    const res = await fetch(`/WebsiteAnalytics/DeviceIntelligence?${currentRangeParams().toString()}`, {
      headers: { 'Accept': 'application/json' }
    });

    if (!res.ok) throw new Error('Unable to load Device Intelligence.');
    const data = await res.json();

    const setText = (id, value) => {
      const el = document.getElementById(id);
      if (el) el.textContent = value ?? '—';
    };

    setText('deviceIntelligenceRangeLabel', `Range: ${data.rangeLabel || 'Current'}`);
    setText('deviceSessions', data.sessions ?? 0);
    setText('deviceEvents', data.events ?? 0);
    setText('deviceFormStarts', data.formStarts ?? 0);
    setText('deviceConfirmedLeads', data.confirmedLeads ?? 0);

    if (content) {
      content.innerHTML = [
        table('Device Type', data.devices),
        table('Browser', data.browsers),
        table('Operating System', data.operatingSystems),
        table('Viewport Size', data.viewports),
        table('Screen Size', data.screens),
        table('Prospect Timezone', data.timeZones),
        table('Language', data.languages)
      ].join('');
    }
  }

  window.websiteAnalyticsDeviceIntelligence = {
    async loadCurrentView() {
      try {
        await loadDeviceIntelligence();
        return true;
      } catch (err) {
        renderDeviceLoadError(err);
        return false;
      }
    },
    getTrafficLabel() {
      return deviceTrafficLabel();
    }
  };

  moduleBtn.addEventListener('click', () => {
    bootstrap.Modal.getOrCreateInstance(modalEl).show();
    window.websiteAnalyticsDeviceIntelligence.loadCurrentView();
  });

  moduleBtn.addEventListener('keydown', e => {
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      moduleBtn.click();
    }
  });

  document.querySelectorAll('[data-device-traffic]').forEach(btn => {
    btn.addEventListener('click', () => {
      localTraffic = btn.dataset.deviceTraffic || 'all';
      document.querySelectorAll('[data-device-traffic]').forEach(x => x.classList.toggle('is-active', x === btn));
      window.websiteAnalyticsDeviceIntelligence.loadCurrentView();
    });
  });
})();
