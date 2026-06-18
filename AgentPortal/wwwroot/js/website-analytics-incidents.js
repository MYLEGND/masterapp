(() => {
  const modalEl = document.getElementById('analyticsIncidentModal');
  if (!modalEl) return;

  const endpoint = '/website-analytics/incident-monitor';
  const pollMs = 90000;
  const staleMs = 60000;

  const statusEl = document.getElementById('incident-monitor-status');
  const scopeEl = document.getElementById('incident-monitor-scope-label');
  const rangeEl = document.getElementById('incident-monitor-range-label');
  const activeSummaryEl = document.getElementById('incident-monitor-active-summary');
  const activeBodyEl = document.getElementById('incident-monitor-active-body');
  const timelineEl = document.getElementById('incident-monitor-timeline');
  const focusGridEl = document.getElementById('incident-monitor-focus-grid');
  const matchRateEl = document.getElementById('incident-monitor-match-rate');
  const matchSubEl = document.getElementById('incident-monitor-match-sub');
  const missingRateEl = document.getElementById('incident-monitor-missing-rate');
  const missingSubEl = document.getElementById('incident-monitor-missing-sub');
  const dispatchSubEl = document.getElementById('incident-monitor-dispatch-sub');
  const pageViewsEl = document.getElementById('incident-monitor-page-views');
  const leadStartsEl = document.getElementById('incident-monitor-lead-starts');
  const leadsEl = document.getElementById('incident-monitor-leads');
  const purchasesEl = document.getElementById('incident-monitor-purchases');
  const pageToStartRateEl = document.getElementById('incident-monitor-page-to-start-rate');
  const startToLeadRateEl = document.getElementById('incident-monitor-start-to-lead-rate');
  const leadToPurchaseRateEl = document.getElementById('incident-monitor-lead-to-purchase-rate');
  const buttonCountEl = document.getElementById('wa-incident-monitor-count');
  const bannerEl = document.getElementById('wa-critical-incident-banner');
  const bannerCopyEl = document.getElementById('wa-critical-incident-banner-copy');

  const state = {
    controller: null,
    loading: false,
    lastLoadedAt: 0,
    snapshot: null
  };

  function abortInFlight() {
    if (state.controller) {
      state.controller.abort();
    }
    state.controller = new AbortController();
    return state.controller;
  }

  function escapeHtml(value) {
    return String(value ?? '')
      .replaceAll('&', '&amp;')
      .replaceAll('<', '&lt;')
      .replaceAll('>', '&gt;')
      .replaceAll('"', '&quot;')
      .replaceAll("'", '&#39;');
  }

  function severityRank(value) {
    switch (String(value || '').trim().toLowerCase()) {
      case 'critical': return 4;
      case 'high': return 3;
      case 'medium': return 2;
      case 'low': return 1;
      default: return 0;
    }
  }

  function formatCount(value) {
    const number = Number(value || 0);
    return Number.isFinite(number) ? number.toLocaleString('en-US', { maximumFractionDigits: 0 }) : '0';
  }

  function formatPercent(value) {
    const number = Number(value || 0);
    return Number.isFinite(number) ? `${number.toFixed(1)}%` : '0.0%';
  }

  function formatMetric(value, unit) {
    return String(unit || '').toLowerCase() === 'percent'
      ? formatPercent(value)
      : formatCount(value);
  }

  function formatTimestamp(value) {
    if (!value) return '—';
    let normalized = String(value).trim();
    if (/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?$/.test(normalized)) {
      normalized += 'Z';
    }

    const date = new Date(normalized);
    if (Number.isNaN(date.getTime())) return '—';

    return date.toLocaleString('en-US', {
      month: 'short',
      day: 'numeric',
      hour: 'numeric',
      minute: '2-digit'
    });
  }

  function setStatus(message, tone = 'neutral') {
    if (!statusEl) return;
    statusEl.className = `wa-incident-status ${tone ? `is-${tone}` : ''}`.trim();
    statusEl.textContent = message;
  }

  function renderBanner(snapshot) {
    const activeIncidents = Array.isArray(snapshot?.activeIncidents) ? snapshot.activeIncidents : [];
    const activeCount = Number(snapshot?.activeIncidentCount || activeIncidents.length || 0);
    const criticalIncidents = activeIncidents.filter((item) => severityRank(item?.severity) >= 4);
    const topIncident = activeIncidents
      .slice()
      .sort((left, right) => severityRank(right?.severity) - severityRank(left?.severity))[0];

    if (buttonCountEl) {
      buttonCountEl.textContent = String(activeCount);
      buttonCountEl.className = `wa-incident-monitor-count severity-${String(topIncident?.severity || 'none').toLowerCase()}`;
    }

    if (!bannerEl || !bannerCopyEl) return;

    if (criticalIncidents.length === 0) {
      bannerEl.hidden = true;
      return;
    }

    const highest = criticalIncidents[0];
    bannerCopyEl.textContent = criticalIncidents.length === 1
      ? (highest?.summary || 'A critical analytics incident is active.')
      : `${criticalIncidents.length} critical analytics incidents are active. Open Incident Monitor for details.`;
    bannerEl.hidden = false;
  }

  function renderActiveIncidents(snapshot) {
    if (!activeBodyEl) return;
    const rows = Array.isArray(snapshot?.activeIncidents) ? snapshot.activeIncidents : [];
    if (!rows.length) {
      activeBodyEl.innerHTML = '<tr><td colspan="6" class="fa-empty">No active analytics incidents right now.</td></tr>';
      return;
    }

    activeBodyEl.innerHTML = rows.map((row) => {
      const severity = String(row?.severity || 'low').toLowerCase();
      return `
        <tr class="wa-incident-row severity-${escapeHtml(severity)}">
          <td>
            <div class="wa-incident-table-title">${escapeHtml(row?.eventType || 'Unknown')}</div>
            <div class="wa-incident-table-sub">${escapeHtml(row?.summary || '')}</div>
          </td>
          <td><span class="wa-incident-pill severity-${escapeHtml(severity)}">${escapeHtml(row?.severity || 'Low')}</span></td>
          <td>${escapeHtml(formatMetric(row?.currentValue, row?.metricUnit))}</td>
          <td>${escapeHtml(formatMetric(row?.baselineValue, row?.metricUnit))}</td>
          <td>${escapeHtml(formatPercent(row?.deviationPercent))}</td>
          <td>${escapeHtml(formatTimestamp(row?.timestampUtc))}</td>
        </tr>
      `;
    }).join('');
  }

  function renderTimeline(snapshot) {
    if (!timelineEl) return;
    const rows = Array.isArray(snapshot?.timeline) ? snapshot.timeline : [];
    if (!rows.length) {
      timelineEl.innerHTML = '<div class="wa-incident-empty">No drift incidents were recorded in the last 24 hours.</div>';
      return;
    }

    timelineEl.innerHTML = rows.map((row) => {
      const severity = String(row?.severity || 'low').toLowerCase();
      return `
        <article class="wa-incident-timeline-item severity-${escapeHtml(severity)}">
          <div class="wa-incident-timeline-top">
            <div class="wa-incident-timeline-title">${escapeHtml(row?.eventType || 'Unknown')}</div>
            <div class="wa-incident-timeline-meta">
              <span class="wa-incident-pill severity-${escapeHtml(severity)}">${escapeHtml(row?.severity || 'Low')}</span>
              <span class="wa-incident-pill is-status">${escapeHtml(row?.statusLabel || 'Detected')}</span>
              <span class="wa-incident-timeline-time">${escapeHtml(formatTimestamp(row?.timestampUtc))}</span>
            </div>
          </div>
          <div class="wa-incident-timeline-copy">${escapeHtml(row?.summary || '')}</div>
          <div class="wa-incident-timeline-values">
            <span>Current ${escapeHtml(formatMetric(row?.currentValue, row?.metricUnit))}</span>
            <span>Baseline ${escapeHtml(formatMetric(row?.baselineValue, row?.metricUnit))}</span>
            <span>Deviation ${escapeHtml(formatPercent(row?.deviationPercent))}</span>
          </div>
        </article>
      `;
    }).join('');
  }

  function renderFocusMetrics(snapshot) {
    if (!focusGridEl) return;
    const metrics = Array.isArray(snapshot?.focusMetrics) ? snapshot.focusMetrics : [];
    if (!metrics.length) {
      focusGridEl.innerHTML = '<div class="wa-incident-empty">Focus metrics are not available yet.</div>';
      return;
    }

    focusGridEl.innerHTML = metrics.map((metric) => {
      const delta = Number(metric?.deltaPercent || 0);
      const tone = delta >= 0 ? 'up' : 'down';
      const deltaLabel = `${delta >= 0 ? '+' : ''}${formatPercent(delta)}`;

      return `
        <article class="wa-incident-focus-card">
          <div class="wa-incident-focus-label">${escapeHtml(metric?.label || 'Metric')}</div>
          <div class="wa-incident-focus-value">${escapeHtml(formatMetric(metric?.currentValue, metric?.metricUnit))}</div>
          <div class="wa-incident-focus-sub">Prior 24h ${escapeHtml(formatMetric(metric?.previousValue, metric?.metricUnit))}</div>
          <div class="wa-incident-focus-delta is-${tone}">${escapeHtml(deltaLabel)}</div>
        </article>
      `;
    }).join('');
  }

  function renderAttribution(snapshot) {
    const attribution = snapshot?.attributionHealth || {};
    if (matchRateEl) matchRateEl.textContent = formatPercent(attribution.serverBrowserMatchRate);
    if (matchSubEl) matchSubEl.textContent = `${formatCount(attribution.matchedEvents)} matched / ${formatCount(attribution.eligibleEvents)} eligible`;
    if (missingRateEl) missingRateEl.textContent = formatPercent(attribution.missingAttributionRate);
    if (missingSubEl) missingSubEl.textContent = `${formatCount(attribution.missingAttributionEvents)} missing / ${formatCount(attribution.eligibleEvents)} eligible`;
    if (dispatchSubEl) dispatchSubEl.textContent = `${formatCount(attribution.browserSentEvents)} browser · ${formatCount(attribution.serverSentEvents)} server`;
  }

  function renderFunnel(snapshot) {
    const funnel = snapshot?.funnelHealth || {};
    if (pageViewsEl) pageViewsEl.textContent = formatCount(funnel.pageViews);
    if (leadStartsEl) leadStartsEl.textContent = formatCount(funnel.leadFormStarts);
    if (leadsEl) leadsEl.textContent = formatCount(funnel.leads);
    if (purchasesEl) purchasesEl.textContent = formatCount(funnel.purchases);
    if (pageToStartRateEl) pageToStartRateEl.textContent = formatPercent(funnel.pageViewToLeadStartRate);
    if (startToLeadRateEl) startToLeadRateEl.textContent = formatPercent(funnel.leadStartToLeadRate);
    if (leadToPurchaseRateEl) leadToPurchaseRateEl.textContent = formatPercent(funnel.leadToPurchaseRate);
  }

  function render(snapshot) {
    state.snapshot = snapshot;
    state.lastLoadedAt = Date.now();

    if (scopeEl) scopeEl.textContent = snapshot?.scopeLabel || 'System-wide';
    if (rangeEl) rangeEl.textContent = snapshot?.rangeLabel || 'Last 24 Hours';
    if (activeSummaryEl) {
      const active = Number(snapshot?.activeIncidentCount || 0);
      const critical = Number(snapshot?.activeCriticalCount || 0);
      activeSummaryEl.textContent = critical > 0
        ? `${active} Active · ${critical} Critical`
        : `${active} Active`;
    }

    renderBanner(snapshot);
    renderActiveIncidents(snapshot);
    renderTimeline(snapshot);
    renderFocusMetrics(snapshot);
    renderAttribution(snapshot);
    renderFunnel(snapshot);

    const lastUpdated = formatTimestamp(snapshot?.lastUpdatedUtc);
    setStatus(`System snapshot refreshed ${lastUpdated}.`, 'success');
  }

  async function fetchSnapshot(force = false) {
    if (state.loading) return;
    if (!force && state.snapshot && (Date.now() - state.lastLoadedAt) < staleMs) {
      return;
    }

    state.loading = true;
    const controller = abortInFlight();
    setStatus('Refreshing incident response snapshot…');

    try {
      const response = await fetch(endpoint, { signal: controller.signal });
      if (!response.ok) {
        throw new Error(`Incident monitor request failed (${response.status})`);
      }

      const payload = await response.json();
      render(payload);
    } catch (error) {
      if (error?.name === 'AbortError') return;
      console.error(error);
      setStatus(error?.message || 'Unable to refresh incident monitor.', 'error');
    } finally {
      state.loading = false;
    }
  }

  modalEl.addEventListener('show.bs.modal', () => {
    void fetchSnapshot(true);
  });

  void fetchSnapshot(false);
  window.setInterval(() => {
    void fetchSnapshot(false);
  }, pollMs);
})();
