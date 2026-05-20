// website-analytics-ai.js
// AI Insights drawer for Website Analytics — reads current page state from window.__waState.
// No API keys. No PII. All calls go server-side only.
(function () {
  'use strict';

  // ── Constants ─────────────────────────────────────────────────────────────
  var REVIEW_ENDPOINT = '/website-analytics/ai/review';
  var FOLLOWUP_ENDPOINT = '/website-analytics/ai/followup';
  var DRAWER_ID = 'aiInsightsDrawer';
  var BACKDROP_ID = 'aiInsightsBackdrop';

  // Severity colour mapping (matches .ai-breakpoint-card variants in CSS)
  var SEVERITY_CLASS = {
    Critical: 'severity-critical',
    High: 'severity-high',
    Medium: 'severity-medium',
    Low: 'severity-low'
  };

  var OWNER_LABEL = {
    Ad: 'Ad',
    LandingPage: 'Landing Page',
    Form: 'Form',
    Tracking: 'Tracking',
    FollowUp: 'Follow-Up',
    Unknown: 'General'
  };

  // ── State ─────────────────────────────────────────────────────────────────
  var drawerOpen = false;
  var currentAbortController = null;
  var lastResult = null;

  // ── DOM references (resolved lazily) ─────────────────────────────────────
  function drawer() { return document.getElementById(DRAWER_ID); }
  function backdrop() { return document.getElementById(BACKDROP_ID); }

  // ── Antiforgery token ─────────────────────────────────────────────────────
  function getToken() {
    // Search in all known locations the existing page uses
    return (
      document.querySelector('#ai-antiforgery input[name="__RequestVerificationToken"]')?.value ||
      document.querySelector('#meta-disconnect-form input[name="__RequestVerificationToken"]')?.value ||
      document.querySelector('input[name="__RequestVerificationToken"]')?.value ||
      document.querySelector('meta[name="RequestVerificationToken"]')?.getAttribute('content') ||
      ''
    );
  }

  // ── State bridge ──────────────────────────────────────────────────────────
  // Reads from window.__waState if available, falls back to page state object.
  function getCurrentState() {
    var st = window.__waState || {};
    var shell = document.querySelector('.fa-shell');
    var tzId = '';
    var tzOffset = 0;
    try {
      tzId = Intl.DateTimeFormat().resolvedOptions().timeZone || '';
      tzOffset = new Date().getTimezoneOffset();
    } catch (_) { }
    return {
      preset: st.preset || shell?.dataset.initialPreset || 'today',
      from: st.from || null,
      to: st.to || null,
      agentProfileId: st.agentProfileId || null,
      team: st.team || false,
      trafficType: typeof st.trafficType === 'string' ? st.trafficType : 'all',
      scopeLabel: st.scopeLabel || shell?.dataset.initialScopeLabel || 'Global',
      timezoneId: tzId,
      timezoneOffsetMinutes: tzOffset
    };
  }

  function trafficLabel(trafficType) {
    if (trafficType === 'paid') return 'Paid Ads';
    if (trafficType === 'non_paid') return 'Non-Ads';
    return 'All Traffic';
  }

  function presetLabel(preset) {
    if (preset === 'today') return 'Today';
    if (preset === '7d') return 'Last 7 Days';
    if (preset === '30d') return 'Last 30 Days';
    if (preset === 'month') return 'This Month';
    if (preset === 'year') return 'This Year';
    return preset || 'Selected Range';
  }

  function updateDrawerScopeLabel(st) {
    var el = document.getElementById('ai-drawer-scope-label');
    if (!el) return;
    var rangeLabel = st.preset === 'custom' && st.from && st.to
      ? `${st.from} → ${st.to}`
      : presetLabel(st.preset);
    el.textContent = `AI reviewing: ${trafficLabel(st.trafficType)} · ${rangeLabel} · ${st.scopeLabel || 'Global'}`;
  }

  // ── POST helper ───────────────────────────────────────────────────────────
  async function postJson(url, body) {
    if (currentAbortController) {
      currentAbortController.abort();
    }
    currentAbortController = new AbortController();

    var token = getToken();
    var headers = { 'Content-Type': 'application/json' };
    if (token) headers['RequestVerificationToken'] = token;

    var res = await fetch(url, {
      method: 'POST',
      signal: currentAbortController.signal,
      headers: headers,
      body: JSON.stringify(body)
    });

    if (!res.ok) {
      var detail = '';
      try {
        var errBody = await res.json();
        detail = errBody?.message || errBody?.error || '';
      } catch (_) { }
      throw new Error(detail || ('Request failed with status ' + res.status));
    }

    return res.json();
  }

  // ── Open / close ──────────────────────────────────────────────────────────
  function openDrawer() {
    var d = drawer();
    var b = backdrop();
    if (!d) return;
    updateDrawerScopeLabel(getCurrentState());
    d.classList.add('open');
    if (b) b.classList.add('visible');
    drawerOpen = true;
    document.body.style.overflow = 'hidden';
    // Focus close button for accessibility
    var closeBtn = d.querySelector('.ai-drawer-close');
    if (closeBtn) closeBtn.focus();
  }

  function closeDrawer() {
    var d = drawer();
    var b = backdrop();
    if (!d) return;
    d.classList.remove('open');
    if (b) b.classList.remove('visible');
    drawerOpen = false;
    document.body.style.overflow = '';
    if (currentAbortController) {
      currentAbortController.abort();
      currentAbortController = null;
    }
  }

  // ── Loading / error states ────────────────────────────────────────────────
  function showLoading() {
    var d = drawer();
    if (!d) return;
    d.querySelector('.ai-drawer-loading')?.removeAttribute('hidden');
    d.querySelector('.ai-drawer-error')?.setAttribute('hidden', '');
    d.querySelector('.ai-drawer-result')?.setAttribute('hidden', '');
    d.querySelector('.ai-drawer-followup-section')?.setAttribute('hidden', '');
  }

  function showError(msg) {
    var d = drawer();
    if (!d) return;
    d.querySelector('.ai-drawer-loading')?.setAttribute('hidden', '');
    var errEl = d.querySelector('.ai-drawer-error');
    if (errEl) {
      errEl.removeAttribute('hidden');
      var msgEl = errEl.querySelector('.ai-error-message');
      if (msgEl) msgEl.textContent = msg || 'An error occurred. Please try again.';
    }
    d.querySelector('.ai-drawer-result')?.setAttribute('hidden', '');
  }

  function showResult(result) {
    var d = drawer();
    if (!d) return;
    d.querySelector('.ai-drawer-loading')?.setAttribute('hidden', '');
    d.querySelector('.ai-drawer-error')?.setAttribute('hidden', '');

    var resultEl = d.querySelector('.ai-drawer-result');
    if (!resultEl) return;
    resultEl.removeAttribute('hidden');

    renderResult(resultEl, result);
    d.querySelector('.ai-drawer-followup-section')?.removeAttribute('hidden');
  }

  // ── Render helpers ────────────────────────────────────────────────────────
  function esc(str) {
    if (!str) return '';
    return String(str)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;');
  }

  function renderResult(container, result) {
    container.innerHTML = '';

    // Summary section
    if (result.summary) {
      var sumDiv = document.createElement('div');
      sumDiv.className = 'ai-section';
      sumDiv.innerHTML =
        '<div class="ai-section-title">Summary</div>' +
        '<div class="ai-section-body">' + esc(result.summary) + '</div>';
      container.appendChild(sumDiv);
    }

    // Primary breakpoints
    if (result.primaryBreakpoints && result.primaryBreakpoints.length > 0) {
      var bpDiv = document.createElement('div');
      bpDiv.className = 'ai-section';
      var bpHtml = '<div class="ai-section-title">Breakpoints</div>';
      result.primaryBreakpoints.forEach(function (bp) {
        var sev = (bp.severity || 'Low');
        var sevClass = SEVERITY_CLASS[sev] || 'severity-low';
        var ownerLabel = OWNER_LABEL[bp.owner] || bp.owner || '';
        var evidenceHtml = (bp.evidence || []).map(function (e) {
          return '<li>' + esc(e) + '</li>';
        }).join('');
        bpHtml +=
          '<div class="ai-breakpoint-card ' + sevClass + '">' +
            '<div class="ai-bp-header">' +
              '<span class="ai-bp-title">' + esc(bp.title) + '</span>' +
              '<span class="ai-bp-badges">' +
                '<span class="ai-badge ai-badge-sev ai-badge-' + sev.toLowerCase() + '">' + esc(sev) + '</span>' +
                (ownerLabel ? '<span class="ai-badge ai-badge-owner">' + esc(ownerLabel) + '</span>' : '') +
              '</span>' +
            '</div>' +
            (evidenceHtml ? '<ul class="ai-bp-evidence">' + evidenceHtml + '</ul>' : '') +
            (bp.likelyCause ? '<div class="ai-bp-cause"><strong>Likely cause:</strong> ' + esc(bp.likelyCause) + '</div>' : '') +
          '</div>';
      });
      bpDiv.innerHTML = bpHtml;
      container.appendChild(bpDiv);
    }

    // Recommended actions
    if (result.recommendedActions && result.recommendedActions.length > 0) {
      var actDiv = document.createElement('div');
      actDiv.className = 'ai-section';
      var actHtml = '<div class="ai-section-title">Recommended Actions</div>';
      result.recommendedActions.forEach(function (action) {
        actHtml +=
          '<div class="ai-action-row">' +
            '<div class="ai-action-priority">#' + (action.priority || '') + '</div>' +
            '<div class="ai-action-body">' +
              '<div class="ai-action-text">' + esc(action.action) + '</div>' +
              (action.why ? '<div class="ai-action-why">' + esc(action.why) + '</div>' : '') +
              (action.expectedImpact ? '<div class="ai-action-impact">Expected: ' + esc(action.expectedImpact) + '</div>' : '') +
            '</div>' +
          '</div>';
      });
      actDiv.innerHTML = actHtml;
      container.appendChild(actDiv);
    }

    // Tests to run
    if (result.testsToRun && result.testsToRun.length > 0) {
      var testDiv = document.createElement('div');
      testDiv.className = 'ai-section';
      var testHtml = '<div class="ai-section-title">Tests to Run</div>';
      result.testsToRun.forEach(function (test) {
        testHtml +=
          '<div class="ai-test-row">' +
            '<div class="ai-test-name">' + esc(test.name) + '</div>' +
            (test.hypothesis ? '<div class="ai-test-hyp">' + esc(test.hypothesis) + '</div>' : '') +
            (test.metric ? '<div class="ai-test-metric">Metric: ' + esc(test.metric) + '</div>' : '') +
          '</div>';
      });
      testDiv.innerHTML = testHtml;
      container.appendChild(testDiv);
    }

    // Confidence notes
    if (result.confidenceNotes && result.confidenceNotes.length > 0) {
      var confDiv = document.createElement('div');
      confDiv.className = 'ai-section';
      var confHtml = '<div class="ai-section-title">Confidence Notes</div>';
      result.confidenceNotes.forEach(function (note) {
        confHtml += '<div class="ai-confidence-note">' + esc(note) + '</div>';
      });
      confDiv.innerHTML = confHtml;
      container.appendChild(confDiv);
    }
  }

  // ── Review request ────────────────────────────────────────────────────────
  async function runReview() {
    var st = getCurrentState();
    openDrawer();
    showLoading();

    var body = {
      preset: st.preset,
      fromUtc: st.from || null,
      toUtc: st.to || null,
      agentProfileId: st.agentProfileId || null,
      team: st.team || false,
      trafficType: st.trafficType || 'all',
      timezoneId: st.timezoneId || null,
      timezoneOffsetMinutes: st.timezoneOffsetMinutes
    };

    try {
      var result = await postJson(REVIEW_ENDPOINT, body);
      lastResult = result;
      if (result && result.isError) {
        showError(result.errorMessage || result.summary || 'AI review returned an error.');
      } else {
        showResult(result);
      }
    } catch (err) {
      if (err.name === 'AbortError') return; // cancelled — drawer closed
      showError(err.message || 'Failed to load AI review. Please try again.');
    }
  }

  // ── Follow-up request ─────────────────────────────────────────────────────
  async function runFollowUp(question) {
    var st = getCurrentState();
    var d = drawer();

    // Swap result area to loading
    d?.querySelector('.ai-drawer-result')?.setAttribute('hidden', '');
    d?.querySelector('.ai-drawer-loading')?.removeAttribute('hidden');
    d?.querySelector('.ai-drawer-error')?.setAttribute('hidden', '');

    var body = {
      preset: st.preset,
      fromUtc: st.from || null,
      toUtc: st.to || null,
      agentProfileId: st.agentProfileId || null,
      team: st.team || false,
      trafficType: st.trafficType || 'all',
      timezoneId: st.timezoneId || null,
      timezoneOffsetMinutes: st.timezoneOffsetMinutes,
      followUpQuestion: question,
      priorSummary: lastResult?.summary || ''
    };

    try {
      var result = await postJson(FOLLOWUP_ENDPOINT, body);
      lastResult = result;
      if (result && result.isError) {
        showError(result.errorMessage || result.summary || 'Follow-up returned an error.');
      } else {
        showResult(result);
      }
    } catch (err) {
      if (err.name === 'AbortError') return;
      showError(err.message || 'Follow-up failed. Please try again.');
    }
  }

  // ── Wire up events ────────────────────────────────────────────────────────
  function init() {
    // Trigger button — may appear anywhere with data-ai-drawer-trigger
    document.addEventListener('click', function (e) {
      var trigger = e.target.closest('[data-ai-drawer-trigger]');
      if (trigger) {
        e.preventDefault();
        runReview();
        return;
      }

      // Close button inside drawer
      var closeBtn = e.target.closest('.ai-drawer-close');
      if (closeBtn) {
        closeDrawer();
        return;
      }
    });

    // Backdrop click
    document.addEventListener('click', function (e) {
      if (e.target.id === BACKDROP_ID) closeDrawer();
    });

    // ESC key
    document.addEventListener('keydown', function (e) {
      if (e.key === 'Escape' && drawerOpen) closeDrawer();
    });

    // Follow-up form submit
    document.addEventListener('submit', function (e) {
      var form = e.target.closest('#ai-followup-form');
      if (!form) return;
      e.preventDefault();

      var textarea = form.querySelector('#ai-followup-input');
      var question = (textarea?.value || '').trim();
      if (!question) return;

      runFollowUp(question);
      textarea.value = '';
    });

    // Retry button
    document.addEventListener('click', function (e) {
      var retryBtn = e.target.closest('.ai-retry-btn');
      if (retryBtn) {
        e.preventDefault();
        runReview();
      }
    });

    window.addEventListener('wa:scope-changed', function () {
      if (!drawerOpen) return;
      updateDrawerScopeLabel(getCurrentState());
      runReview();
    });
  }

  // ── Bootstrap ─────────────────────────────────────────────────────────────
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
