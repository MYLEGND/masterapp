(() => {
  const shell = document.querySelector('.fa-shell');
  const state = {
    preset: shell?.dataset.initialPreset || '30d',
    from: null,
    to: null,
    pollMs: 45000,
    controllers: {},
    openModal: null,
    cache: {
      summary: null,
      conversions: null,
      leads: null,
      agentPerf: null,
      traffic: null
    },
    agentProfileId: null,
    scope: {
      preset: shell?.dataset.initialPreset || '30d',
      from: null,
      to: null,
      agentProfileId: null
    }
  };
  const agentOptions = window.AGENT_OPTIONS || [];
  const isFounder = agentOptions.length > 0;
  const callerProfileId = shell?.dataset.callerProfileId || null;

  // Founder hard override: force global scope and strip any agent id from URL
  if (isFounder) {
    state.scope = state.scope || {};
    state.scope.agentProfileId = null;
    state.agentProfileId = null;
    try {
      const url = new URL(window.location.href);
      if (url.searchParams.has("agentProfileId")) {
        url.searchParams.delete("agentProfileId");
        window.history.replaceState({}, "", url);
      }
    } catch { /* ignore URL parse issues */ }
  }

  if (isFounder) {
    // Founder → always global
    state.agentProfileId = null;
    state.scope.agentProfileId = null;
  } else {
    // Agent → scoped to caller
    state.agentProfileId = callerProfileId;
    state.scope.agentProfileId = callerProfileId;
  }

  const endpoints = {
    summary: '/WebsiteAnalytics/summary',
    traffic: '/WebsiteAnalytics/traffic',
    pagePerf: '/WebsiteAnalytics/page-performance',
    ctaPerf: '/WebsiteAnalytics/cta-performance',
    quote: '/WebsiteAnalytics/quote-funnel',
    conversions: '/WebsiteAnalytics/conversions',
    leads: '/WebsiteAnalytics/leads',
    agentPerf: '/WebsiteAnalytics/agent-performance'
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
      if (!res.ok) throw new Error(`${key} fetch failed`);
      return await res.json();
    } catch (err) {
      if (err.name === 'AbortError') {
        // Expected cancellation; swallow
        return null;
      }
      throw err;
    }
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

  function rangeParams({ team = false } = {}) {
    // Defensive: never allow founder requests to carry an agentProfileId
    if (isFounder) {
      state.scope.agentProfileId = null;
    }
    const p = { preset: state.scope.preset };
    if (state.scope.preset === 'custom' && state.scope.from && state.scope.to) {
      p.fromUtc = state.scope.from;
      p.toUtc = state.scope.to;
    }
    if (team) {
      p.team = true;
      return p;
    }
    // Only send agentProfileId for non-founder agents
    if (!isFounder && state.scope.agentProfileId) {
      p.agentProfileId = state.scope.agentProfileId;
    }
    return p;
  }

  // Render helpers ---------------------------------------------------
  function setText(id, val) {
    const el = document.getElementById(id);
    if (el) el.textContent = val;
  }

  function renderSummary(data) {
    state.cache.summary = data;
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

  function renderTable(bodyId, rows, cols) {
    const body = document.getElementById(bodyId);
    if (!body) return;
    if (!rows || rows.length === 0) {
      body.innerHTML = `<tr><td colspan="${cols.length}" class="fa-empty">No data in range</td></tr>`;
      return;
    }
    body.innerHTML = rows.map(r => {
      return `<tr>${cols.map(c => `<td class="${c.align || ''}">${c.render ? c.render(r) : (r[c.key] ?? '')}</td>`).join('')}</tr>`;
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
    setText('traffic-range-label', data.rangeLabel || '');
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
      { key: 'clicks', align: 'text-end' }
    ]);
    setText('ctaperf-range-label', data.rangeLabel || '');
  }

  function renderQuote(data) {
    setText('quote-starts', data.quoteStarts);
    setText('quote-form-starts', data.quoteFormStarts);
    setText('quote-form-submits', data.quoteFormSubmits);
    const drop = data.dropOffFormStartsToSubmits ?? (data.quoteFormStarts > 0 ? Math.round((1 - (data.quoteFormSubmits / data.quoteFormStarts)) * 100) : null);
    const meta = document.getElementById('mod-quote-meta');
    if (meta && drop !== null) meta.textContent = `Drop-off: ${drop}% between form starts → submits`;
    const meta2 = document.getElementById('quote-dropoff-starts');
    if (meta2 && data.dropOffStartsToFormStarts != null) meta2.textContent = `Starts → Form starts drop-off: ${data.dropOffStartsToFormStarts}%`;
    renderTable('quote-type-body', data.byQuoteType || [], [
      { key: 'key' },
      { key: 'count', align: 'text-end' }
    ]);
    setText('quote-range-label', data.rangeLabel || '');
  }

  function renderConversions(data) {
    state.cache.conversions = data;
    const summary = state.cache.summary;
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

  function renderLeads(data) {
    state.cache.leads = data;
    setText('leads-total', data.total);
    if (data.leads && data.leads.length) {
      const newest = data.leads[0];
      const meta = document.getElementById('mod-leads-meta');
      if (meta) meta.textContent = `Most recent: ${formatDisplayDate(newest.createdUtc)} UTC`;
    }
    renderTable('leads-body', data.leads || [], [
      { render: r => formatDisplayDate(r.createdUtc) },
      { key: 'name' },
      { key: 'email' },
      { key: 'phone' },
      { key: 'interest' },
      { key: 'source' }
    ]);
    setText('leads-range-label', data.rangeLabel || '');
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
    const data = await fetchJson('summary', endpoints.summary, rangeParams());
    if (!data) return;
    renderSummary(data);
  }
  async function loadTraffic() {
    const data = await fetchJson('traffic', endpoints.traffic, rangeParams());
    if (!data) return;
    renderTraffic(data);
    state.cache.traffic = data;
    if (isFounder) {
      renderCampaignInsights(data, state.cache.agentPerf);
    }
  }
  async function loadPagePerf() {
    const data = await fetchJson('pageperf', endpoints.pagePerf, rangeParams());
    if (!data) return;
    renderPagePerf(data);
  }
  async function loadCtaPerf() {
    const data = await fetchJson('ctaperf', endpoints.ctaPerf, rangeParams());
    if (!data) return;
    renderCtaPerf(data);
  }
  async function loadQuote() {
    const data = await fetchJson('quote', endpoints.quote, rangeParams());
    if (!data) return;
    renderQuote(data);
  }
  async function loadConv() {
    const data = await fetchJson('conv', endpoints.conversions, rangeParams());
    if (!data) return;
    renderConversions(data);
  }
  async function loadLeads() {
    const data = await fetchJson('leads', endpoints.leads, rangeParams());
    if (!data) return;
    renderLeads(data);
  }

  async function loadAgentPerf() {
    const data = await fetchJson('agentperf', endpoints.agentPerf, rangeParams());
    if (!data) return;
    renderAgentPerf(data);
    if (isFounder && state.cache.traffic) renderCampaignInsights(state.cache.traffic, data);
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
      loader();
    });
    el.addEventListener('hidden.bs.modal', () => {
      state.openModal = null;
    });
  }

  function initRangeControls() {
    document.querySelectorAll('.btn-range').forEach(btn => {
      btn.addEventListener('click', () => {
        document.querySelectorAll('.btn-range').forEach(b => b.classList.remove('active'));
        btn.classList.add('active');
        state.scope.preset = btn.dataset.range || '30d';
        if (state.scope.preset !== 'custom') {
          state.scope.from = null; state.scope.to = null;
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
        document.querySelectorAll('.btn-range').forEach(b => b.classList.remove('active'));
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
      case 'agentPerfModal': loadAgentPerf(); break;
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
      'mod-leads': 'leadsModal'
    };
    if (isFounder) {
      map['mod-agentperf'] = 'agentPerfModal';
    }
    Object.entries(map).forEach(([cardId, modalId]) => {
      const card = document.getElementById(cardId);
      if (!card) return;
      card.addEventListener('click', () => {
        const modalEl = document.getElementById(modalId);
        if (!modalEl) return;
        const bsModal = new bootstrap.Modal(modalEl);
        bsModal.show();
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

  async function init() {
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
    attachModal('trafficModal', loadTraffic);
    attachModal('pagePerfModal', loadPagePerf);
    attachModal('ctaPerfModal', loadCtaPerf);
    attachModal('quoteModal', loadQuote);
    attachModal('convModal', loadConv);
    attachModal('leadsModal', loadLeads);
    if (isFounder) {
      attachModal('agentPerfModal', loadAgentPerf);
    }
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

    const exportConv = document.getElementById('export-conv');
    if (exportConv) exportConv.addEventListener('click', () => {
      const data = state.cache.conversions;
      if (!data || !data.recent || !data.recent.length) return;
      downloadCsv('conversions.csv', data.recent, [
        { header: 'WhenUtc', selector: r => new Date(r.eventUtc + 'Z').toISOString() },
        { header: 'Event', selector: 'eventType' },
        { header: 'Page', selector: 'pageKey' },
        { header: 'CTA', selector: 'sourceCta' }
      ]);
    });
    const exportLeads = document.getElementById('export-leads');
    if (exportLeads) exportLeads.addEventListener('click', () => {
      const data = state.cache.leads;
      if (!data || !data.leads || !data.leads.length) return;
      downloadCsv('leads.csv', data.leads, [
        { header: 'WhenUtc', selector: r => new Date(r.createdUtc + 'Z').toISOString() },
        { header: 'Name', selector: 'name' },
        { header: 'Email', selector: 'email' },
        { header: 'Phone', selector: 'phone' },
        { header: 'Interest', selector: 'interest' },
        { header: 'Source', selector: 'source' }
      ]);
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
    initPolling();
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

  document.addEventListener('DOMContentLoaded', init);

  function currentBaseLink() {
    const agentId = state.scope.agentProfileId;
    if (agentId && agentOptions && agentOptions.length) {
      const match = agentOptions.find(a => a.id === agentId);
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
  }
})();
