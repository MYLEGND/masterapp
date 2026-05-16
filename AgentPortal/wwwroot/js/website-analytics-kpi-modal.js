/**
 * website-analytics-kpi-modal.js
 * Handles the clickable KPI card → centered detail modal flow.
 * Reads page state from the existing website-analytics.js `state` object
 * (exposed via window.__waState) or directly from DOM data attributes.
 * Uses Chart.js already loaded on the page — does NOT add a second chart library.
 */
(() => {
    'use strict';

    // ── State ──────────────────────────────────────────────────────────────────
    let activeChart = null;
    let activeFetchController = null;
    let isOpen = false;
    let closing = false;

    // ── DOM refs (resolved once on init) ──────────────────────────────────────
    let backdrop, modalRoot, panel, btnClose;
    let elTitle, elRange, elLoading, elError, elErrorMsg, elEmpty, elContent;
    let elStatTiles, elChartCanvas, elBreakdown;

    // ── Page state reader ──────────────────────────────────────────────────────
    // The existing website-analytics.js IIFE stores state internally, but
    // the page shell data-attributes carry the initial preset, and the
    // window.CALLER_PROFILE_ID / window.AGENT_OPTIONS globals carry identity.
    // We also expose a thin bridge via window.__waGetState() which website-analytics.js
    // populates at init time (see bridge below — we attach it there, not here).

    function readPageState() {
        // Primary source: bridge object populated by website-analytics.js init
        const bridge = window.__waState;
        if (bridge) {
            return {
                preset:        bridge.preset || '30d',
                fromUtc:       bridge.from   || null,
                toUtc:         bridge.to     || null,
                trafficType:   bridge.trafficType || 'all',
                agentProfileId: bridge.agentProfileId || null,
                isFounder:     bridge.isFounder || false,
                rangeParams:   typeof bridge.rangeParams === 'function' ? bridge.rangeParams.bind(bridge) : null
            };
        }

        // Fallback: read from shell data-attributes + globals
        const shell = document.querySelector('.fa-shell');
        return {
            preset:        shell?.dataset.initialPreset || '30d',
            fromUtc:       null,
            toUtc:         null,
            trafficType:   'all',
            agentProfileId: (window.CALLER_PROFILE_ID && window.CALLER_PROFILE_ID !== '') ? window.CALLER_PROFILE_ID : null,
            isFounder:     (window.AGENT_OPTIONS && window.AGENT_OPTIONS.length > 0) || false
        };
    }

    function buildParams(metric) {
        const s = readPageState();
        if (s.rangeParams) {
            const params = new URLSearchParams(s.rangeParams());
            params.set('metric', metric);
            params.set('trafficType', 'All');
            return params.toString();
        }

        const p = new URLSearchParams();
        p.set('metric', metric);
        p.set('preset', s.preset);

        if (s.preset === 'custom' && s.fromUtc && s.toUtc) {
            p.set('fromUtc', s.fromUtc);
            p.set('toUtc', s.toUtc);
        }

        // Traffic filter — map local keys to server enum names
        const tt = s.trafficType || 'all';
        if (tt === 'paid')     p.set('trafficType', 'PaidAds');
        else if (tt === 'non_paid') p.set('trafficType', 'NonPaid');
        else                   p.set('trafficType', 'All');

        // Agent scoping — founders never send agentProfileId (global scope)
        if (!s.isFounder && s.agentProfileId) {
            p.set('agentProfileId', s.agentProfileId);
        }

        return p.toString();
    }

    // ── Modal open / close ────────────────────────────────────────────────────
    function showModal() {
        if (!backdrop || !modalRoot || !panel) return;
        closing = false;
        backdrop.hidden = false;
        modalRoot.hidden = false;
        backdrop.classList.remove('kpi-closing');
        panel.classList.remove('kpi-closing');
        document.body.style.overflow = 'hidden';
        isOpen = true;
        // Focus the close button for keyboard users
        setTimeout(() => btnClose && btnClose.focus(), 60);
    }

    function hideModal(animate = true) {
        if (!isOpen || closing) return;
        closing = true;

        if (animate) {
            backdrop.classList.add('kpi-closing');
            panel.classList.add('kpi-closing');
            setTimeout(() => {
                backdrop.hidden = true;
                modalRoot.hidden = true;
                document.body.style.overflow = '';
                isOpen = false;
                closing = false;
            }, 160);
        } else {
            backdrop.hidden = true;
            modalRoot.hidden = true;
            document.body.style.overflow = '';
            isOpen = false;
            closing = false;
        }

        // Cancel any in-flight fetch
        if (activeFetchController) {
            activeFetchController.abort();
            activeFetchController = null;
        }

        // Destroy chart to release memory
        destroyChart();
    }

    function destroyChart() {
        if (activeChart) {
            try { activeChart.destroy(); } catch { /* ignore */ }
            activeChart = null;
        }
    }

    // ── State views ───────────────────────────────────────────────────────────
    function showLoading() {
        elLoading.hidden = false;
        elError.hidden   = true;
        elEmpty.hidden   = true;
        elContent.hidden = true;
    }

    function showError(msg) {
        elLoading.hidden = true;
        elError.hidden   = false;
        elEmpty.hidden   = true;
        elContent.hidden = true;
        if (elErrorMsg) elErrorMsg.textContent = msg || 'Unable to load data.';
    }

    function showEmpty() {
        elLoading.hidden = true;
        elError.hidden   = true;
        elEmpty.hidden   = false;
        elContent.hidden = true;
    }

    function showContent() {
        elLoading.hidden = true;
        elError.hidden   = true;
        elEmpty.hidden   = true;
        elContent.hidden = false;
    }

    // ── Fetch ─────────────────────────────────────────────────────────────────
    async function fetchDetail(metric) {
        // Cancel prior fetch
        if (activeFetchController) {
            activeFetchController.abort();
        }
        activeFetchController = new AbortController();

        const qs = buildParams(metric);
        const res = await fetch(`/website-analytics/kpi-detail?${qs}`, {
            signal: activeFetchController.signal
        });

        if (!res.ok) {
            let detail = '';
            try {
                const ct = res.headers.get('content-type') || '';
                if (ct.includes('application/json')) {
                    const payload = await res.json();
                    detail = payload?.message || payload?.error || '';
                } else {
                    detail = (await res.text()) || '';
                }
            } catch { detail = ''; }
            throw new Error(detail || `Request failed (${res.status})`);
        }

        return res.json();
    }

    // ── Render ────────────────────────────────────────────────────────────────
    function renderStatTiles(totals, label) {
        if (!elStatTiles) return;

        const { total, previousTotal, deltaCount, deltaPct, avgPerDay } = totals;

        const direction = deltaCount > 0 ? 'up' : deltaCount < 0 ? 'down' : 'flat';
        const arrow     = direction === 'up' ? '▲' : direction === 'down' ? '▼' : '•';
        const deltaClass = `kpi-stat-delta-${direction}`;
        const absPct    = Math.abs(deltaPct);
        const absDelta  = Math.abs(deltaCount);

        const prevLabel = previousTotal != null && previousTotal > 0
            ? formatInt(previousTotal)
            : '—';

        elStatTiles.innerHTML = `
            <div class="kpi-stat-tile">
                <div class="kpi-stat-tile-label">Total ${escHtml(label)}</div>
                <div class="kpi-stat-tile-value">${formatInt(total)}</div>
            </div>
            <div class="kpi-stat-tile">
                <div class="kpi-stat-tile-label">vs Prior Period</div>
                <div class="kpi-stat-tile-value ${deltaClass}">${arrow} ${formatInt(absDelta)}</div>
                <div class="kpi-stat-tile-sub ${deltaClass}">${absPct}% change</div>
            </div>
            <div class="kpi-stat-tile">
                <div class="kpi-stat-tile-label">Prior Period</div>
                <div class="kpi-stat-tile-value">${prevLabel}</div>
            </div>
            <div class="kpi-stat-tile">
                <div class="kpi-stat-tile-label">Avg / Day</div>
                <div class="kpi-stat-tile-value">${formatDecimal(avgPerDay)}</div>
            </div>
        `;
    }

    function renderChart(series, label) {
        destroyChart();

        if (!elChartCanvas || !series || series.length === 0) return;

        const chartLib = window.Chart;
        if (!chartLib) {
            // Chart.js not loaded — silently skip chart; content is still visible
            elChartCanvas.closest('.kpi-detail-chart-container').style.display = 'none';
            return;
        }

        const labels = series.map(p => p.label);
        const values = series.map(p => p.value);

        const ctx = elChartCanvas.getContext('2d');

        // Gold gradient fill
        const gradient = ctx.createLinearGradient(0, 0, 0, 200);
        gradient.addColorStop(0,   'rgba(199,153,49,.35)');
        gradient.addColorStop(1,   'rgba(199,153,49,0)');

        activeChart = new chartLib(ctx, {
            type: 'line',
            data: {
                labels,
                datasets: [{
                    label: label,
                    data: values,
                    borderColor: '#c79931',
                    borderWidth: 2.5,
                    backgroundColor: gradient,
                    fill: true,
                    tension: 0.35,
                    pointRadius: values.length <= 14 ? 4 : 2,
                    pointBackgroundColor: '#c79931',
                    pointBorderColor: '#0b1530',
                    pointBorderWidth: 1.5,
                    pointHoverRadius: 6,
                    pointHoverBackgroundColor: '#e6c078'
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                animation: { duration: 300 },
                interaction: { mode: 'index', intersect: false },
                plugins: {
                    legend: { display: false },
                    tooltip: {
                        backgroundColor: '#0b1530',
                        borderColor: 'rgba(199,153,49,.6)',
                        borderWidth: 1,
                        titleColor: '#c79931',
                        bodyColor: '#d8deea',
                        padding: 10,
                        callbacks: {
                            title: items => items[0]?.label || '',
                            label: item => ` ${label}: ${formatInt(item.parsed.y)}`
                        }
                    }
                },
                scales: {
                    x: {
                        grid: { color: 'rgba(199,153,49,.1)', drawBorder: false },
                        ticks: {
                            color: 'rgba(216,222,234,.6)',
                            font: { size: 11 },
                            maxTicksLimit: 10,
                            maxRotation: 0
                        },
                        border: { display: false }
                    },
                    y: {
                        grid: { color: 'rgba(199,153,49,.08)', drawBorder: false },
                        ticks: {
                            color: 'rgba(216,222,234,.6)',
                            font: { size: 11 },
                            callback: v => formatInt(v)
                        },
                        border: { display: false },
                        beginAtZero: true
                    }
                }
            }
        });
    }

    function renderBreakdown(breakdown, metric) {
        if (!elBreakdown) return;

        const sections = [];

        function addSection(title, items) {
            if (!items || items.length === 0) return;
            const rows = items.map(item => {
                const metaHtml = item.meta
                    ? `<div class="kpi-breakdown-meta">${escHtml(item.meta)}</div>`
                    : '';
                return `
                    <div class="kpi-breakdown-row">
                        <div class="kpi-breakdown-label" title="${escHtml(item.label)}">${escHtml(item.label)}</div>
                        <div class="kpi-breakdown-value">${formatInt(item.value)}</div>
                    </div>${metaHtml}`;
            }).join('');
            sections.push(`
                <div class="kpi-breakdown-section">
                    <div class="kpi-breakdown-title">${escHtml(title)}</div>
                    ${rows}
                </div>`);
        }

        // Recent leads renders slightly differently (meta is the source string)
        function addLeadSection(title, items) {
            if (!items || items.length === 0) return;
            const rows = items.map(item => {
                const meta = item.meta ? `<div class="kpi-breakdown-meta">${escHtml(item.meta)}</div>` : '';
                return `
                    <div class="kpi-breakdown-row" style="flex-direction:column;align-items:flex-start;">
                        <div class="kpi-breakdown-label">${escHtml(item.label)}</div>
                        ${meta}
                    </div>`;
            }).join('');
            sections.push(`
                <div class="kpi-breakdown-section">
                    <div class="kpi-breakdown-title">${escHtml(title)}</div>
                    ${rows}
                </div>`);
        }

        switch (metric) {
            case 'pageviews':
                addSection('Top Pages', breakdown.topPages);
                addSection('Top Sources', breakdown.topSources);
                addSection('Top Campaigns', breakdown.topCampaigns);
                break;
            case 'visitors':
                addSection('Top Landing Pages', breakdown.topLandingPages);
                addSection('Top Sources', breakdown.topSources);
                break;
            case 'sessions':
                addSection('Top Entry Pages', breakdown.topLandingPages);
                addSection('Top Sources', breakdown.topSources);
                addSection('Top Campaigns', breakdown.topCampaigns);
                break;
            case 'leads':
                addSection('By Page', breakdown.topPages);
                addSection('By Source', breakdown.topSources);
                addSection('By Campaign', breakdown.topCampaigns);
                addLeadSection('Recent Leads', breakdown.recentLeads);
                break;
        }

        elBreakdown.innerHTML = sections.length > 0
            ? sections.join('')
            : '<div style="color:var(--legend-silver);font-size:.9rem;padding:8px 0;">No breakdown data available.</div>';
    }

    // ── Main open handler ─────────────────────────────────────────────────────
    async function openModal(metric) {
        if (!panel) return;

        // Header
        const metricLabels = {
            pageviews: 'Page Views',
            visitors:  'Unique Visitors',
            sessions:  'Sessions',
            leads:     'Leads'
        };
        const label = metricLabels[metric] || metric;
        if (elTitle) elTitle.textContent = label + ' — Detail';
        if (elRange) {
            const s = readPageState();
            const rangeText = buildRangeLabel(s);
            elRange.textContent = rangeText;
        }

        showModal();
        showLoading();

        try {
            const data = await fetchDetail(metric);
            if (!data) return; // aborted

            const isEmpty = !data.series || data.series.length === 0;

            if (isEmpty && data.totals && data.totals.total === 0) {
                showEmpty();
                return;
            }

            showContent();
            renderStatTiles(data.totals, label);
            renderChart(data.series, label);
            renderBreakdown(data.breakdown, metric);

            // Update range subtitle with server-resolved dates
            if (elRange && data.startDateLocal && data.endDateLocal) {
                elRange.textContent = `${data.startDateLocal} – ${data.endDateLocal}`;
            }

        } catch (err) {
            if (err && err.name === 'AbortError') return;
            console.error('[KpiModal]', err);
            showError(err && err.message ? err.message : 'Unable to load data. Please try again.');
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    function buildRangeLabel(s) {
        if (s.preset === 'custom' && s.fromUtc && s.toUtc) {
            return `${s.fromUtc} – ${s.toUtc}`;
        }
        const presetLabels = {
            'today': 'Today',
            '7d':    'Last 7 Days',
            '30d':   'Last 30 Days',
            'month': 'This Month',
            'year':  'This Year'
        };
        return presetLabels[s.preset] || s.preset || 'Selected Period';
    }

    function formatInt(v) {
        const n = Number(v);
        if (!Number.isFinite(n)) return '0';
        return Math.round(n).toLocaleString('en-US');
    }

    function formatDecimal(v) {
        const n = Number(v);
        if (!Number.isFinite(n)) return '0';
        return n.toLocaleString('en-US', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
    }

    function escHtml(v) {
        return String(v || '')
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    // ── Init ──────────────────────────────────────────────────────────────────
    function init() {
        backdrop    = document.getElementById('kpiDetailBackdrop');
        modalRoot   = document.getElementById('kpiDetailModal');
        btnClose    = document.getElementById('kpiDetailClose');
        elTitle     = document.getElementById('kpiDetailTitle');
        elRange     = document.getElementById('kpiDetailRange');
        elLoading   = document.getElementById('kpiDetailLoading');
        elError     = document.getElementById('kpiDetailError');
        elErrorMsg  = document.getElementById('kpiDetailErrorMsg');
        elEmpty     = document.getElementById('kpiDetailEmpty');
        elContent   = document.getElementById('kpiDetailContent');
        elStatTiles = document.getElementById('kpiDetailStatTiles');
        elChartCanvas = document.getElementById('kpiDetailChart');
        elBreakdown   = document.getElementById('kpiDetailBreakdown');

        if (!modalRoot) return; // guard: markup not present

        // Build the panel wrapper inside modalRoot (replaces the raw children layout)
        // The modal HTML in the view has header + body as direct children of #kpiDetailModal.
        // Wrap them in .kpi-detail-panel for flex layout and animation target.
        const header = modalRoot.querySelector('.kpi-detail-header');
        const body   = modalRoot.querySelector('.kpi-detail-body');
        if (header && body) {
            const panelEl = document.createElement('div');
            panelEl.className = 'kpi-detail-panel';
            panelEl.setAttribute('role', 'document');
            modalRoot.insertBefore(panelEl, header);
            panelEl.appendChild(header);
            panelEl.appendChild(body);
            panel = panelEl;
        } else {
            panel = modalRoot;
        }

        // KPI card click handlers
        document.querySelectorAll('[data-kpi-card]').forEach(card => {
            const metric = card.dataset.metric;
            if (!metric) return;

            card.addEventListener('click', () => openModal(metric));

            // Keyboard: Enter / Space
            card.addEventListener('keydown', e => {
                if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault();
                    openModal(metric);
                }
            });
        });

        // Close button
        if (btnClose) {
            btnClose.addEventListener('click', () => hideModal(true));
        }

        // Backdrop click closes
        if (backdrop) {
            backdrop.addEventListener('click', () => hideModal(true));
        }

        // ESC key closes
        document.addEventListener('keydown', e => {
            if (e.key === 'Escape' && isOpen) {
                e.preventDefault();
                hideModal(true);
            }
        });

        // Trap focus inside the panel
        if (modalRoot) {
            modalRoot.addEventListener('keydown', e => {
                if (!isOpen || e.key !== 'Tab') return;
                const focusable = panel
                    ? panel.querySelectorAll('button, [href], input, [tabindex]:not([tabindex="-1"])')
                    : [];
                if (!focusable.length) return;
                const first = focusable[0];
                const last  = focusable[focusable.length - 1];
                if (e.shiftKey) {
                    if (document.activeElement === first) {
                        e.preventDefault();
                        last.focus();
                    }
                } else {
                    if (document.activeElement === last) {
                        e.preventDefault();
                        first.focus();
                    }
                }
            });
        }

        // Expose a bridge so website-analytics.js state is readable here.
        // website-analytics.js sets window.__waState at the end of its init()
        // if not already set — we polyfill it here; the main JS overrides when ready.
        if (!window.__waState) {
            window.__waState = null;
        }
    }

    document.addEventListener('DOMContentLoaded', init);
})();
