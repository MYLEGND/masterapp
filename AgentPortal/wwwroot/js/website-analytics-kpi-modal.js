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
    let activeMetric = null;
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
                preset:        bridge.preset || 'today',
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
            preset:        shell?.dataset.initialPreset || 'today',
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

        // Agent scoping — include the selected agent scope for founders and agents.
        if (s.agentProfileId) {
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


    

    async function openVisitorTimelineModal(visitorId, sessionId) {
        if (!visitorId && !sessionId) return;

        const params = new URLSearchParams();

        if (visitorId)
            params.set('visitorId', visitorId);

        if (sessionId)
            params.set('sessionId', sessionId);

        params.set('preset', window.currentPreset || 'today');

        const response = await fetch(
            `/WebsiteAnalytics/visitor-timeline?${params.toString()}`
        );

        if (!response.ok)
            throw new Error('Failed loading visitor timeline');

        const data = await response.json();

        let modal = document.getElementById('visitorTimelineModal');

        if (!modal) {
            modal = document.createElement('div');
            modal.id = 'visitorTimelineModal';
            modal.className = 'vc-modal-backdrop';

            modal.innerHTML = `
                <div class="vc-modal-panel">
                    <div class="vc-modal-header">
                        <div>
                            <div class="vc-modal-kicker">
                                Visitor Intelligence
                            </div>
                            <h3>Visitor Timeline</h3>
                        </div>

                        <button type="button" class="vc-modal-close" aria-label="Close visitor timeline modal">
                            &times;
                        </button>
                    </div>

                    <div class="vc-modal-body"></div>
                </div>
            `;

            document.body.appendChild(modal);

            modal.querySelector('.vc-modal-close')
                ?.addEventListener('click', () => {
                    modal.classList.remove('is-open');
                    document.body.style.overflow = '';
                });

            modal.addEventListener('click', e => {
                if (e.target === modal) {
                    modal.classList.remove('is-open');
                    document.body.style.overflow = '';
                }
            });
        }

        const body = modal.querySelector('.vc-modal-body');

        body.innerHTML = `
            <div class="vc-modal-stats">
                <div>
                    <strong>${data.trustScore}</strong>
                    <span>Trust Score</span>
                </div>

                <div>
                    <strong>${data.trustTier}</strong>
                    <span>Trust Tier</span>
                </div>

                <div>
                    <strong>${data.totalEvents}</strong>
                    <span>Events</span>
                </div>

                <div>
                    <strong>${data.sessions}</strong>
                    <span>Sessions</span>
                </div>
            </div>

            <div class="vc-modal-section-title">
                Triggered Signals
            </div>

            <div class="vc-modal-note">
                ${(data.signals || []).join(' · ') || 'None'}
            </div>

            <div class="vc-modal-section-title">
                Timeline
            </div>

            <div class="vc-modal-table-wrap">
                <table class="vc-modal-table">
                    <thead>
                        <tr>
                            <th>When</th>
                            <th>Event</th>
                            <th>Page</th>
                            <th>Element</th>
                            <th>Scroll</th>
                        </tr>
                    </thead>

                    <tbody>
                        ${(data.events || []).map(e => `
                            <tr>
                                <td>${e.eventUtc || ''}</td>
                                <td>${e.eventType || ''}</td>
                                <td>${e.pageKey || e.path || ''}</td>
                                <td>${e.elementText || e.elementId || ''}</td>
                                <td>${e.scrollPercent || 0}%</td>
                            </tr>
                        `).join('')}
                    </tbody>
                </table>
            </div>
        `;

        modal.classList.add('is-open');
        document.body.style.overflow = 'hidden';
    }



    document.addEventListener('click', function (event) {
        const row = event.target.closest('#visitorConcentrationModal [data-visitor-id]');
        if (!row) return;

        event.preventDefault();
        event.stopPropagation();

        openVisitorTimelineModal(
            row.dataset.visitorId || '',
            row.dataset.sessionId || ''
        );
    });

    function openVisitorConcentrationModal(rows) {
        let modal = document.getElementById('visitorConcentrationModal');

        if (!modal) {
            modal = document.createElement('div');
            modal.id = 'visitorConcentrationModal';
            modal.className = 'vc-modal-backdrop';
            modal.innerHTML = `
                <div class="vc-modal-panel" role="dialog" aria-modal="true" aria-labelledby="visitorConcentrationModalTitle">
                    <div class="vc-modal-header">
                        <div>
                            <div class="vc-modal-kicker">Unique Visitor Trust Check</div>
                            <h3 id="visitorConcentrationModalTitle">Visitor Concentration Breakdown</h3>
                            <p>Ranked by recurring visits first, then event volume. Use this to see whether traffic is spread across real visitors or inflated by a few repeat users.</p>
                        </div>
                        <div class="vc-modal-header-actions">
                            <button type="button" class="vc-modal-copy" data-default-label="Copy All">Copy All</button>
                            <button type="button" class="vc-modal-close" aria-label="Close visitor concentration modal">&times;</button>
                        </div>
                    </div>
                    <div class="vc-modal-body" id="visitorConcentrationModalBody"></div>
                </div>
            `;
            document.body.appendChild(modal);

            modal.querySelector('.vc-modal-close')?.addEventListener('click', () => {
                modal.classList.remove('is-open');
                document.body.style.overflow = '';
            });

            modal.addEventListener('click', e => {
                if (e.target === modal) {
                    modal.classList.remove('is-open');
                    document.body.style.overflow = '';
                }
            });

            modal.querySelector('.vc-modal-copy')?.addEventListener('click', async event => {
                const button = event.currentTarget;
                const payload = buildVisitorConcentrationCopyText(
                    modal.__visitorRows || [],
                    modal.__rangeLabel || ''
                );

                if (!payload) {
                    setVisitorConcentrationCopyButtonState(button, 'Nothing To Copy', 'is-error');
                    window.setTimeout(() => {
                        setVisitorConcentrationCopyButtonState(button, button.dataset.defaultLabel || 'Copy All');
                    }, 1400);
                    return;
                }

                setVisitorConcentrationCopyButtonState(button, 'Copying...');
                const copied = await copyTextWithFallback(payload);
                setVisitorConcentrationCopyButtonState(
                    button,
                    copied ? 'Copied All' : 'Copy Failed',
                    copied ? 'is-success' : 'is-error'
                );

                window.setTimeout(() => {
                    setVisitorConcentrationCopyButtonState(button, button.dataset.defaultLabel || 'Copy All');
                }, 1400);
            });
        }

        const body = modal.querySelector('#visitorConcentrationModalBody');
        const recurring = rows.filter(x => Number(x.sessions || 0) >= 2);
        const top = [...rows].sort((a, b) =>
            Number(b.sessions || 0) - Number(a.sessions || 0) ||
            Number(b.events || 0) - Number(a.events || 0)
        );

        const recurringCount = recurring.length;
        const totalEvents = rows.reduce((sum, r) => sum + Number(r.events || 0), 0);
        const topVisitorEvents = top[0] ? Number(top[0].events || 0) : 0;
        const topShare = totalEvents > 0 ? ((topVisitorEvents / totalEvents) * 100).toFixed(1) : '0.0';
        modal.__visitorRows = top;
        modal.__rangeLabel = (elRange?.textContent || '').trim();

        body.innerHTML = `
            <div class="vc-modal-stats">
                <div><span>Total Visitors Shown</span><strong>${formatInt(rows.length)}</strong></div>
                <div><span>Recurring Visitors ≥ 2 Visits</span><strong>${formatInt(recurringCount)}</strong></div>
                <div><span>Top Visitor Events</span><strong>${formatInt(topVisitorEvents)}</strong></div>
                <div><span>Top Visitor Share</span><strong>${topShare}%</strong></div>
            </div>

            <div class="vc-modal-section-title">Recurring Visitors First</div>
            <div class="vc-modal-note">Click any visitor row to open the full Visitor Timeline + Trust Score.</div>

            <div class="vc-modal-table-wrap">
                <table class="vc-modal-table">
                    <thead>
                        <tr>
                            <th>Visitor ID</th>
                            <th>Sessions</th>
                            <th>Events</th>
                            <th>First Seen</th>
                            <th>Last Seen</th>
                            <th>Device</th>
                            <th>Source</th>
                            <th>Trust</th>
                            <th>Signals</th>
                        </tr>
                    </thead>
                    <tbody>
                        ${top.length ? top.map(v => {
                            const sessions = Number(v.sessions || 0);
                            const trustTier = v.trustTier || (v.likelyInternal ? 'Suspicious' : (sessions >= 2 ? 'Review' : 'Trusted'));
                            const trustScore = Number(v.trustScore ?? v.humanConfidence ?? 0);
                            const trustClass = trustTier === 'Trusted'
                                ? 'is-good'
                                : (trustTier === 'Review' ? 'is-review' : 'is-risk');
                            const badge = `<span class="vc-badge ${trustClass}">${escHtml(trustTier)}${trustScore ? ` · ${formatInt(trustScore)}` : ''}</span>`;
                            const signals = Array.isArray(v.trustSignals) && v.trustSignals.length
                                ? v.trustSignals.slice(0, 2).join(' · ')
                                : '—';

                            return `
                                <tr class="${sessions >= 2 ? 'is-recurring' : ''} vc-clickable-row" data-visitor-id="${escHtml(v.visitorId || '')}" data-session-id="${escHtml(v.sessionId || '')}" title="Click to open visitor timeline">
                                    <td><code>${escHtml(v.visitorShortId || v.visitorId || 'unknown')}</code></td>
                                    <td>${formatInt(sessions)}</td>
                                    <td><strong>${formatInt(v.events || 0)}</strong></td>
                                    <td>${escHtml(v.firstSeenLocal || '—')}</td>
                                    <td>${escHtml(v.lastSeenLocal || '—')}</td>
                                    <td>${escHtml(v.device || 'Unknown')}</td>
                                    <td>${escHtml(v.source || 'Direct')}</td>
                                    <td>${badge}</td>
                                    <td>${escHtml(signals)}</td>
                                </tr>`;
                        }).join('') : '<tr><td colspan="9" class="vc-empty">No visitor rows in this range yet.</td></tr>'}
                    </tbody>
                </table>
            </div>
        `;

        modal.classList.add('is-open');
        document.body.style.overflow = 'hidden';
    }

    async function copyTextWithFallback(text) {
        if (!text) return false;

        if (navigator.clipboard?.writeText) {
            try {
                await navigator.clipboard.writeText(text);
                return true;
            } catch {
                // Fall back to the textarea copy path below.
            }
        }

        try {
            const temp = document.createElement('textarea');
            temp.value = text;
            temp.setAttribute('readonly', 'readonly');
            temp.style.position = 'absolute';
            temp.style.left = '-9999px';
            document.body.appendChild(temp);
            temp.select();
            const copied = document.execCommand('copy');
            document.body.removeChild(temp);
            return !!copied;
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

    function setVisitorConcentrationCopyButtonState(button, text, stateClass = '') {
        if (!button) return;
        button.textContent = text;
        button.classList.remove('is-success', 'is-error');
        if (stateClass) {
            button.classList.add(stateClass);
        }
    }

    function sanitizeCopyValue(value, fallback = '—') {
        const normalized = String(value ?? '')
            .replace(/\s+/g, ' ')
            .trim();
        return normalized || fallback;
    }

    }

    function buildVisitorConcentrationCopyText(rows, rangeLabel) {
        const normalizedRows = Array.isArray(rows) ? rows : [];
        const recurringCount = normalizedRows.filter(visitor => Number(visitor.sessions || 0) >= 2).length;
        const totalEvents = normalizedRows.reduce((sum, visitor) => sum + Number(visitor.events || 0), 0);
        const topVisitorEvents = normalizedRows[0] ? Number(normalizedRows[0].events || 0) : 0;
        const topShare = totalEvents > 0 ? ((topVisitorEvents / totalEvents) * 100).toFixed(1) : '0.0';
        const lines = [
            'Unique Visitor Trust Check',
            'Visitor Concentration Breakdown'
        ];

        if (rangeLabel) {
            lines.push(`Range: ${sanitizeCopyValue(rangeLabel, 'Selected Period')}`);
        }

        lines.push('');
        lines.push('Summary');
        lines.push(`Total Visitors Shown: ${formatInt(normalizedRows.length)}`);
        lines.push(`Recurring Visitors ≥ 2 Visits: ${formatInt(recurringCount)}`);
        lines.push(`Top Visitor Events: ${formatInt(topVisitorEvents)}`);
        lines.push(`Top Visitor Share: ${topShare}%`);
        lines.push('');
        lines.push('Recurring Visitors First');
        lines.push('Click any visitor row to open the full Visitor Timeline + Trust Score.');
        lines.push('Visitor ID\tSessions\tEvents\tFirst Seen\tLast Seen\tDevice\tSource\tTrust\tSignals');

        if (!normalizedRows.length) {
            lines.push('No visitor rows in this range yet.');
        } else {
            normalizedRows.forEach(visitor => {
                const sessions = Number(visitor.sessions || 0);
                const trustTier = visitor.trustTier || (visitor.likelyInternal ? 'Suspicious' : (sessions >= 2 ? 'Review' : 'Trusted'));
                const trustScore = Number(visitor.trustScore ?? visitor.humanConfidence ?? 0);
                const trustLabel = `${trustTier}${trustScore ? ` (${formatInt(trustScore)})` : ''}`;
                const signals = Array.isArray(visitor.trustSignals) && visitor.trustSignals.length
                    ? visitor.trustSignals.slice(0, 2).join(' · ')
                    : '—';
                lines.push([
                    sanitizeCopyValue(visitor.visitorShortId || visitor.visitorId || 'unknown', 'unknown'),
                    formatInt(sessions),
                    formatInt(visitor.events || 0),
                    sanitizeCopyValue(visitor.firstSeenLocal || '—'),
                    sanitizeCopyValue(visitor.lastSeenLocal || '—'),
                    sanitizeCopyValue(visitor.device || 'Unknown', 'Unknown'),
                    sanitizeCopyValue(visitor.source || 'Direct', 'Direct'),
                    trustLabel,
                    signals
                ].join('\t'));
            });
        }

        return normalizeCopiedText(lines.join('\n'));
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

                {
                    const visitorRows = breakdown.visitorConcentration || [];
                    const recurringCount = visitorRows.filter(v => Number(v.sessions || 0) >= 2).length;
                    const sortedRows = [...visitorRows].sort((a, b) =>
                        Number(b.sessions || 0) - Number(a.sessions || 0) ||
                        Number(b.events || 0) - Number(a.events || 0)
                    );
                    const topVisitor = sortedRows[0] || null;

                    sections.push(`
                        <button
                            type="button"
                            class="kpi-breakdown-section visitor-concentration-section visitor-concentration-trigger"
                            id="openVisitorConcentrationModal"
                            aria-controls="visitorConcentrationModal"
                            aria-haspopup="dialog"
                            title="Open Visitor Concentration Breakdown">
                            <div class="visitor-concentration-heading">
                                <div class="visitor-concentration-copy">
                                    <div class="kpi-breakdown-title visitor-concentration-kicker">Visitor Concentration</div>
                                    <div class="visitor-concentration-intro">
                                        Verify whether unique visitors are real distributed traffic or inflated by repeat users.
                                    </div>
                                </div>
                                <div class="visitor-concentration-cta" aria-hidden="true">
                                    <span>Open Breakdown</span>
                                    <span class="visitor-concentration-cta-arrow">↗</span>
                                </div>
                            </div>

                            <div class="vc-summary-grid">
                                <div class="vc-summary-card">
                                    <span>Recurring Visitors ≥ 2 Visits</span>
                                    <strong>${formatInt(recurringCount)}</strong>
                                </div>
                                <div class="vc-summary-card">
                                    <span>Top Visitor Sessions</span>
                                    <strong>${formatInt(topVisitor?.sessions || 0)}</strong>
                                </div>
                                <div class="vc-summary-card">
                                    <span>Top Visitor Events</span>
                                    <strong>${formatInt(topVisitor?.events || 0)}</strong>
                                </div>
                            </div>

                            <div class="visitor-concentration-footer">
                                <span class="visitor-concentration-footer-text">Inspect the full visitor list to spot session clustering, repeat-user inflation, and internal traffic patterns.</span>
                                <span class="visitor-concentration-footer-link">Review Visitor Detail</span>
                            </div>

                            ${visitorRows.length
                                ? ''
                                : '<div class="visitor-concentration-empty">No visitor concentration rows are in this range yet. Open the breakdown to confirm the current spread.</div>'}
                        </button>
                    `);

                    setTimeout(() => {
                        const trigger = document.getElementById('openVisitorConcentrationModal');
                        if (trigger && !trigger.dataset.bound) {
                            trigger.dataset.bound = 'true';
                            trigger.addEventListener('click', () => openVisitorConcentrationModal(sortedRows));
                        }
                    }, 0);
                }

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
        activeMetric = metric;

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

        window.addEventListener('wa:scope-changed', () => {
            if (!isOpen || !activeMetric) return;
            openModal(activeMetric);
        });
    }

    document.addEventListener('DOMContentLoaded', init);
})();
