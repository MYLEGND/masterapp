(() => {
  const OPEN_MODAL_STORAGE_KEY = 'websiteAnalytics.openModal';

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
      traffic: null,
      metaCampaigns: null,
      behaviorSources: null,
      aiSnapshot: null
    },
    agentProfileId: null,
    scope: {
      preset: shell?.dataset.initialPreset || '30d',
      from: null,
      to: null,
      agentProfileId: null
    },
    trafficType: {
      trafficModal: 'all',
      pagePerfModal: 'all',
      ctaPerfModal: 'all',
      quoteModal: 'all',
      convModal: 'all',
      leadsModal: 'all',
      aiReviewSnapshotModal: 'all',
      behaviorModal: 'all'
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

  function rangeParams({ team = false, modal = null } = {}) {
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
    // Add trafficType if modal context is provided
    if (modal && state.trafficType && state.trafficType[modal]) {
      let t = state.trafficType[modal];
      if (t === 'paid') p.trafficType = 'PaidAds';
      else if (t === 'non_paid') p.trafficType = 'NonPaid';
      else p.trafficType = 'All';
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
      return `<tr>${cols.map(c => {
        const alignClass = c.align || '';
        const extraClass = typeof c.className === 'function' ? (c.className(r) || '') : (c.className || '');
        const cellClass = `${alignClass} ${extraClass}`.trim();
        return `<td class="${cellClass}">${c.render ? c.render(r) : (r[c.key] ?? '')}</td>`;
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

  function renderAbandonment(data) {
    if (!data) return;
    renderTable('abandon-summary-body', data.summary || [], [
      { key: 'quoteType' },
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
      sampleNote.textContent = `Timing metrics use page_exit samples. Page views: ${totalViews} · Timing samples: ${timingSamples}.`;
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
      { key: 'sessions',       align: 'text-end' },
      { key: 'engagedSessions', align: 'text-end' },
      { key: 'verifiedLeads',  align: 'text-end' },
      { render: r => formatPct(r.sessionConversionRate), align: 'text-end' },
      { render: r => formatMs(r.avgDwellMs),             align: 'text-end' }
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
    if (attribution.isPaid) {
      return '<span class="badge badge-paid">Ads</span>';
    } else if (attribution.isNonPaid) {
      return '<span class="badge badge-nonpaid">Non-Ads</span>';
    }
    return '';
  }

  function renderLeads(data) {
    state.cache.leads = data;
    setText('leads-total', data.total);
    if (data.leads && data.leads.length) {
      const newest = data.leads[0];
      const meta = document.getElementById('mod-leads-meta');
      if (meta) meta.textContent = `Most recent (local): ${formatDisplayDate(newest.createdUtc)}`;
    }
    renderTable('leads-body', data.leads || [], [
      { render: r => formatDisplayDate(r.createdUtc) },
      { key: 'name' },
      { key: 'email' },
      { key: 'phone' },
      { key: 'interest' },
      { key: 'source' },
      { render: r => trafficBadge(r.attribution), align: 'text-center' }
    ]);
    setText('leads-range-label', data.rangeLabel || '');
  }
  // Maps each modal id to the badge <span> id that shows "Viewing: ..."
  const trafficTypeBadgeIds = {
    trafficModal:  'traffic-active-mode',
    pagePerfModal: 'pageperf-active-mode',
    ctaPerfModal:  'ctaperf-active-mode',
    quoteModal:    'quote-active-mode',
    convModal:     'conv-active-mode',
    leadsModal:    'leads-active-mode'
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

  function renderAiReviewSnapshot(data) {
    state.cache.aiSnapshot = data;
    setText('ai-snapshot-generated', data.generatedAtLocal || '—');
    setText('ai-snapshot-range', data.rangeLabel || '—');
    setText('ai-snapshot-scope', data.scopeLabel || '—');

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
    const data = await fetchJson('summary', endpoints.summary, rangeParams());
    if (!data) return;
    renderSummary(data);
  }
  async function loadTraffic() {
    const data = await fetchJson('traffic', endpoints.traffic, rangeParams({ modal: 'trafficModal' }));
    if (!data) return;
    renderTraffic(data);
    state.cache.traffic = data;
    if (isFounder) {
      renderCampaignInsights(data, state.cache.agentPerf);
    }
  }
  async function loadPagePerf() {
    const data = await fetchJson('pageperf', endpoints.pagePerf, rangeParams({ modal: 'pagePerfModal' }));
    if (!data) return;
    renderPagePerf(data);
  }
  async function loadCtaPerf() {
    const data = await fetchJson('ctaperf', endpoints.ctaPerf, rangeParams({ modal: 'ctaPerfModal' }));
    if (!data) return;
    renderCtaPerf(data);
  }
  async function loadQuote() {
    const [data, abandon] = await Promise.all([
      fetchJson('quote', endpoints.quote, rangeParams({ modal: 'quoteModal' })),
      fetchJson('quote-abandon', endpoints.quoteFunnelAbandonment, rangeParams({ modal: 'quoteModal' }))
    ]);
    if (data) renderQuote(data);
    if (abandon) renderAbandonment(abandon);
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
      console.error(err);
    }
  }
  async function loadConv() {
    const params = rangeParams({ modal: 'convModal' });
    const t = state.trafficType.convModal || 'all';
    let convData, summaryData;
    if (t === 'all') {
      // No filter active — use the cached all-traffic summary (no extra fetch needed).
      convData = await fetchJson('conv', endpoints.conversions, params);
      summaryData = state.cache.summary;
    } else {
      // Filter is active — fetch conversion data AND a matching filtered summary in parallel
      // so Intent Conversion Rate and Session Conversion Rate reflect the same traffic population
      // as Total Conversions. Without this, rates would silently use an all-traffic denominator.
      [convData, summaryData] = await Promise.all([
        fetchJson('conv', endpoints.conversions, params),
        fetchJson('conv-summary', endpoints.summary, params)
      ]);
    }
    if (!convData) return;
    renderConversions(convData, summaryData);
  }
  async function loadLeads() {
    const data = await fetchJson('leads', endpoints.leads, rangeParams({ modal: 'leadsModal' }));
    if (!data) return;
    renderLeads(data);
  }

  async function loadAgentPerf() {
    const data = await fetchJson('agentperf', endpoints.agentPerf, rangeParams());
    if (!data) return;
    renderAgentPerf(data);
    if (isFounder && state.cache.traffic) renderCampaignInsights(state.cache.traffic, data);
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
    const num = Number(ms || 0);
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
    setText('meta-campaigns-account', data.accountId || '—');
    setText('meta-campaigns-synced', data.syncedUtc ? formatDisplayDate(data.syncedUtc) : '—');
    setMetaAccountChip(data.accountName || data.accountId || 'Connected');

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
      { render: r => pill(formatInt(r.leads), metaLeadsClass(r.leads)), align: 'text-end' }
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
        body.innerHTML = `<tr><td colspan="11" class="text-danger">${message}</td></tr>`;
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
    if (!statusEl) return;

    try {
      const data = await fetchJson('meta-connection-status', endpoints.metaConnectionStatus, {});
      if (!data || !data.connected) {
        statusEl.className = 'small mt-2 text-warning';
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
      statusEl.className = 'small mt-2 text-success';
      statusEl.textContent = `Connected: ${acct}${user}${exp}`;
      if (connectBtn) connectBtn.textContent = 'Reconnect Meta Ads';
      if (disconnectBtn) disconnectBtn.style.display = '';
      setMetaCampaignsEnabled(true);
      setMetaAccountChip(acct || 'Connected', true);
    } catch (err) {
      statusEl.className = 'small mt-2 text-danger';
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
      if (body) body.innerHTML = '<tr><td colspan="11" class="fa-empty">Disconnected. Reconnect Meta Ads to load campaigns.</td></tr>';
    } catch (err) {
      const statusEl = document.getElementById('meta-connection-status');
      if (statusEl) {
        statusEl.className = 'small mt-2 text-danger';
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
        statusEl.className = 'small mt-2 text-success';
        statusEl.textContent = 'Meta Ads connected successfully.';
      } else if (meta === 'error') {
        const msg = url.searchParams.get('message') || 'Meta Ads connection failed.';
        statusEl.className = 'small mt-2 text-danger';
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
      'mod-behavior': 'behaviorModal'
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
    showMetaCallbackBanner();
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
    attachModal('trafficModal', () => { updateTrafficTypeHeader('trafficModal'); loadTraffic(); });
    attachModal('pagePerfModal', () => { updateTrafficTypeHeader('pagePerfModal'); loadPagePerf(); });
    attachModal('ctaPerfModal', () => { updateTrafficTypeHeader('ctaPerfModal'); loadCtaPerf(); });
    attachModal('quoteModal', () => { updateTrafficTypeHeader('quoteModal'); loadQuote(); });
    attachModal('convModal', () => { updateTrafficTypeHeader('convModal'); loadConv(); });
    attachModal('leadsModal', () => { updateTrafficTypeHeader('leadsModal'); loadLeads(); });
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

    const exportBehavior = document.getElementById('export-behavior');
    if (exportBehavior) exportBehavior.addEventListener('click', () => {
      const data = state.cache.behaviorSources;
      if (!data || !data.rows || !data.rows.length) return;
      downloadCsv('behavior-sources.csv', data.rows, [
        { header: 'Source',    selector: 'source' },
        { header: 'Medium',    selector: r => r.medium   || '' },
        { header: 'Campaign',  selector: r => r.campaign || '' },
        { header: 'Sessions',  selector: 'sessions' },
        { header: 'Engaged',   selector: 'engagedSessions' },
        { header: 'Leads',     selector: 'verifiedLeads' },
        { header: 'SessConv',  selector: r => formatPct(r.sessionConversionRate) },
        { header: 'AvgDwell',  selector: r => formatMs(r.avgDwellMs) }
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
      get preset()      { return state.scope.preset || '30d'; },
      get from()        { return state.scope.from || null; },
      get to()          { return state.scope.to || null; },
      get trafficType() {
        // Return the active traffic filter for the currently open modal,
        // or 'all' when no modal filter is engaged.
        const t = state.trafficType;
        if (!t) return 'all';
        // Find the most recently active modal traffic type (non-'all' wins).
        const active = Object.values(t).find(v => v && v !== 'all');
        return active || 'all';
      },
      get agentProfileId() { return state.scope.agentProfileId || null; },
      get isFounder()      { return isFounder; }
    };
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

  // ── Product-specific links (website + paid landing) ───────────────────────
  const PRODUCT_ROUTE_GROUPS = [
    { key: 'life', label: 'Life Insurance', websiteRoute: 'Quote/Life', adLandingRoute: 'Quote/Life/landing' },
    { key: 'mortgage', label: 'Mortgage Protection', websiteRoute: 'Quote/Mortgage-Protection', adLandingRoute: 'Quote/Mortgage-Protection/landing' },
    { key: 'term', label: 'Term Life', websiteRoute: 'Quote/Term-Life', adLandingRoute: 'Quote/Term-Life/landing' },
    { key: 'wholelife', label: 'Whole Life', websiteRoute: 'Quote/Whole-Life', adLandingRoute: 'Quote/Whole-Life/landing' },
    { key: 'finalexpense', label: 'Final Expense', websiteRoute: 'Quote/Final-Expense', adLandingRoute: 'Quote/Final-Expense/landing' },
    { key: 'iul', label: 'Indexed Universal Life (IUL)', websiteRoute: 'Quote/IUL', adLandingRoute: 'Quote/IUL/landing' }
  ];

  const PRODUCT_LINK_VARIANTS = PRODUCT_ROUTE_GROUPS.reduce((rows, group) => {
    rows.push({
      id: `${group.key}_website`,
      label: `${group.label} — Website`,
      intentLabel: 'Website Link',
      route: group.websiteRoute,
      variantType: 'website',
      isAdLanding: false
    });
    if (group.adLandingRoute) {
      rows.push({
        id: `${group.key}_ad_landing`,
        label: `${group.label} — Ad Landing`,
        intentLabel: 'Ad Landing Link',
        route: group.adLandingRoute,
        variantType: 'landing',
        isAdLanding: true
      });
    }
    return rows;
  }, []);

  function buildProductUrl(baseLink, route) {
    if (!baseLink) return '';
    if (!route) return '';
    try {
      const base = baseLink.endsWith('/') ? baseLink : baseLink + '/';
      return new URL(route, base).toString();
    } catch {
      const clean = baseLink.replace(/\/$/, '');
      const normalizedRoute = String(route || '').replace(/^\/+/, '');
      return normalizedRoute ? `${clean}/${normalizedRoute}` : clean;
    }
  }

  function wireProductLinks() {
    const toggle = document.getElementById('product-links-toggle');
    const section = document.getElementById('product-links-section');
    const list = document.getElementById('product-links-list');
    const display = document.getElementById('product-link-display');
    if (!toggle || !section || !list) return;

    // Render product rows
    list.innerHTML = PRODUCT_LINK_VARIANTS.map(v => {
      const toneClass = v.isAdLanding ? 'link-landing' : 'link-website';
      const badge = v.isAdLanding ? `<span class="badge-landing">AD</span>` : '';
      const helper = v.isAdLanding ? `<span class="product-link-helper">Optimized for campaigns</span>` : '';
      return (
      `<div class="product-link-row ${toneClass}" data-link-tone="${v.variantType}">` +
        `<div class="product-link-copyblock">` +
          `<span class="product-link-label">${v.label}${badge}</span>` +
          `<span class="product-link-intent">${v.intentLabel}</span>` +
          `${helper}` +
        `</div>` +
        `<div class="product-link-actions">` +
          `<button type="button" class="product-link-open ${toneClass}" data-link-id="${v.id}">Open</button>` +
          `<button type="button" class="product-link-copy ${toneClass}" data-link-id="${v.id}">Copy</button>` +
        `</div>` +
      `</div>`
      );
    }).join('');

    // Toggle expand/collapse
    toggle.addEventListener('click', () => {
      const isOpen = !section.hidden;
      section.hidden = isOpen;
      toggle.setAttribute('aria-expanded', isOpen ? 'false' : 'true');
    });

    // Copy on click — URL built fresh from current base link so agent scope changes are reflected
    list.addEventListener('click', e => {
      const copyBtn = e.target.closest('.product-link-copy');
      const openBtn = e.target.closest('.product-link-open');
      if (!copyBtn && !openBtn) return;
      const linkId = (copyBtn || openBtn).dataset.linkId;
      const selectedLink = PRODUCT_LINK_VARIANTS.find(v => v.id === linkId);
      if (!selectedLink) return;
      const url = buildProductUrl(currentBaseLink(), selectedLink.route);
      if (display) display.value = url;
      if (copyBtn) {
        copyToClipboard(url);
        copyBtn.textContent = 'Copied!';
        setTimeout(() => { copyBtn.textContent = 'Copy'; }, 1500);
      }
      if (openBtn) {
        window.open(url, '_blank', 'noopener');
      }
    });
  }

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
