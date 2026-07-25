(function () {
  if (window.LegendPageHealth) return;

  const KNOWLEDGE_KEY = "legend_page_health_learning_v1";
  const MAX_SESSION_EVENTS = 18;
  const MAX_BREADCRUMBS = 12;
  const MAX_KNOWN_PATTERNS = 24;

  const pageTitle = resolvePageTitle();
  const pageKey = `${window.location.host}${window.location.pathname}`.toLowerCase();
  const environment = resolveEnvironment();
  const state = {
    events: [],
    breadcrumbs: [],
    indexByFingerprint: new Map(),
    knowledge: loadKnowledge(),
    ui: null,
    drawerOpen: false,
    placementFrame: 0,
    placementObserver: null
  };
  pruneTransientNetworkKnowledge();

  const current = Object.freeze({
    log(message, detail, scope = "app") {
      addBreadcrumb("log", message, detail, scope);
      return null;
    },
    warn(message, detail, scope = "app") {
      addBreadcrumb("warn", message, detail, scope);
      return record("warn", message, detail, scope);
    },
    error(message, detail, scope = "app") {
      addBreadcrumb("error", message, detail, scope);
      return record("error", message, detail, scope);
    },
    open() {
      ensureUi();
      setDrawerOpen(true);
    },
    close() {
      ensureUi();
      setDrawerOpen(false);
    },
    clearSession() {
      state.events = [];
      state.indexByFingerprint.clear();
      render();
    },
    exportReport() {
      return buildReport();
    }
  });

  window.LegendPageHealth = Object.freeze({ current });
  bindGlobalDiagnostics();
  wrapFetch();
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", render, { once: true });
  } else {
    render();
  }

  function resolvePageTitle() {
    const title = String(document.title || "").trim();
    return title.replace(/\s+-\s+Legend™?$/i, "").trim() || "Current page";
  }

  function resolveEnvironment() {
    const host = window.location.hostname.toLowerCase();
    return host === "localhost" || host === "127.0.0.1" || host.endsWith(".local")
      ? "Local"
      : "Production";
  }

  function nowIso() {
    return new Date().toISOString();
  }

  function normalizeText(value) {
    return String(value ?? "").trim();
  }

  function escapeHtml(value) {
    return String(value ?? "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/\"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  function safeClone(value, depth) {
    const level = depth || 0;
    if (value == null || typeof value === "string" || typeof value === "number" || typeof value === "boolean") return value;
    if (value instanceof Error) {
      return { name: value.name, message: value.message, stack: value.stack };
    }
    if (Array.isArray(value)) return value.slice(0, 10).map(item => safeClone(item, level + 1));
    if (typeof value === "object") {
      if (level >= 2) return String(value);
      const output = {};
      Object.keys(value).slice(0, 18).forEach(key => {
        output[key] = safeClone(value[key], level + 1);
      });
      return output;
    }
    return String(value);
  }

  function loadKnowledge() {
    try {
      const value = JSON.parse(localStorage.getItem(KNOWLEDGE_KEY) || "{}");
      return value && typeof value === "object" ? value : {};
    } catch {
      return {};
    }
  }

  function saveKnowledge() {
    try {
      localStorage.setItem(KNOWLEDGE_KEY, JSON.stringify(state.knowledge));
    } catch {
      // Storage can be unavailable in private browsing; diagnostics remain session-scoped.
    }
  }

  function firstStackFrame(stack) {
    return String(stack || "").split("\n").map(line => line.trim()).find(line => /^at\s+/i.test(line)) || "";
  }

  function extractErrorLike(detail) {
    const source = detail?.error || detail?.reason || detail;
    return {
      name: normalizeText(source?.name),
      message: normalizeText(source?.message || detail?.message),
      stack: normalizeText(source?.stack || detail?.stack)
    };
  }

  function extractStatus(detail) {
    const direct = Number(detail?.status);
    if (Number.isFinite(direct) && direct > 0) return direct;
    const nested = Number(detail?.response?.status);
    return Number.isFinite(nested) && nested > 0 ? nested : 0;
  }

  function extractUrl(detail) {
    return normalizeText(detail?.url || detail?.requestUrl || detail?.response?.url || detail?.filename);
  }

  function normalizeNetworkTarget(value) {
    const raw = normalizeText(value);
    if (!raw) return "";
    try {
      const parsed = new URL(raw, window.location.origin);
      const path = parsed.pathname.replace(/\/+$/, "") || "/";
      return `${parsed.origin.toLowerCase()}${path.toLowerCase()}`;
    } catch {
      return raw.replace(/[?#].*$/, "").replace(/\/+$/, "").toLowerCase();
    }
  }

  function normalizeMethod(value) {
    return normalizeText(value).toUpperCase();
  }

  function isTransientNetworkFailure(payload) {
    if (payload.scope !== "network" || payload.status !== 0) return false;
    const text = [payload.message, payload.errorName, payload.errorMessage].join(" ").toLowerCase();
    return /failed to fetch|network request failed|networkerror|load failed/.test(text);
  }

  function getStoredNetworkRequest(item, fingerprint) {
    const match = /^network request failed for\s+([a-z]+)\s+(.+)$/i.exec(normalizeText(item?.message));
    if (!match) return { method: normalizeMethod(item?.method), target: normalizeNetworkTarget(item?.url) };
    return { method: normalizeMethod(item?.method || match[1]), target: normalizeNetworkTarget(item?.url || match[2]) };
  }

  function isTransientNetworkKnowledge(item, fingerprint) {
    if (normalizeText(item?.scope) !== "network") return false;
    const text = `${normalizeText(item?.message)}|${normalizeText(fingerprint)}`.toLowerCase();
    return text.includes("|0|") && /failed to fetch|network request failed|networkerror|load failed/.test(text);
  }

  function pruneTransientNetworkKnowledge() {
    let changed = false;
    Object.keys(state.knowledge).forEach(knownPageKey => {
      const pageKnowledge = state.knowledge[knownPageKey];
      if (!pageKnowledge || typeof pageKnowledge !== "object") return;
      Object.entries(pageKnowledge).forEach(([fingerprint, item]) => {
        if (!isTransientNetworkKnowledge(item, fingerprint)) return;
        delete pageKnowledge[fingerprint];
        changed = true;
      });
      if (Object.keys(pageKnowledge).length === 0) delete state.knowledge[knownPageKey];
    });
    if (changed) saveKnowledge();
  }

  function resolveTransientNetworkFailures(url, method) {
    const target = normalizeNetworkTarget(url);
    const normalizedMethod = normalizeMethod(method);
    if (!target || !normalizedMethod) return;

    const resolved = state.events.filter(event => {
      const payload = event.payload;
      return payload.isTransientNetworkFailure
        && normalizeMethod(payload.method) === normalizedMethod
        && normalizeNetworkTarget(payload.url) === target;
    });
    if (resolved.length === 0) return;

    const resolvedFingerprints = new Set(resolved.map(event => event.fingerprint));
    state.events = state.events.filter(event => !resolvedFingerprints.has(event.fingerprint));
    resolvedFingerprints.forEach(fingerprint => state.indexByFingerprint.delete(fingerprint));

    const pageKnowledge = state.knowledge[pageKey];
    let knowledgeChanged = false;
    if (pageKnowledge) {
      Object.entries(pageKnowledge).forEach(([fingerprint, item]) => {
        const request = getStoredNetworkRequest(item, fingerprint);
        if (!isTransientNetworkKnowledge(item, fingerprint) || request.method !== normalizedMethod || request.target !== target) return;
        delete pageKnowledge[fingerprint];
        knowledgeChanged = true;
      });
      if (Object.keys(pageKnowledge).length === 0) delete state.knowledge[pageKey];
    }
    if (knowledgeChanged) saveKnowledge();
    render();
  }

  function classifyIssue(payload) {
    const text = [payload.scope, payload.message, payload.errorName, payload.errorMessage, payload.url].join(" ").toLowerCase();
    if (payload.isTransientNetworkFailure) return issue("high", "The page is temporarily unable to reach the server.", "This request ended before the server returned a response.", "Page Health removes this issue automatically when the same request succeeds.");
    if (payload.status === 401) return issue("high", "Your session expired.", "The current sign-in session is no longer valid.", "Sign back in or refresh this page and try again.");
    if (payload.status === 403) return issue("high", "This action is blocked by permissions.", "The server rejected this request.", "Check account access or try the action with an authorized user.");
    if (payload.status === 404) return issue("medium", "A required endpoint could not be found.", "The page requested a route the server does not expose.", "Refresh once. If it repeats, inspect the route in Technical details.");
    if (payload.status >= 500) return issue("critical", "The server hit an internal error.", "The server failed while handling this request.", "Retry once, then use Technical details to trace the endpoint.");
    if (/failed to fetch|network request failed|networkerror|load failed/.test(text)) return issue("high", "The page could not reach the server.", "A request failed before receiving a usable response.", "Check the connection and inspect the endpoint details if it repeats.");
    if (/referenceerror|typeerror|syntaxerror|is not defined|cannot access/.test(text)) return issue("high", "A page script broke while this screen was running.", "A browser error stopped part of this page's logic.", "Use the stack trace in Technical details to fix the exact script path.");
    if (/boot|init|render|load/.test(text) && payload.scope === "boot") return issue("high", "Part of the page did not finish loading.", "A startup step failed, so this page may be partially ready.", "Refresh once, then inspect the failed startup step if it repeats.");
    return issue(payload.level === "warn" ? "medium" : "high", "This page hit an unexpected problem.", "The page did not fully recover from an operation.", "Review Technical details and the recent breadcrumbs.");
  }

  function issue(severity, title, summary, action) {
    return { severity, title, summary, action };
  }

  function addBreadcrumb(level, message, detail, scope) {
    state.breadcrumbs.push({ at: nowIso(), level, scope: normalizeText(scope || "app"), message: normalizeText(message), detail: safeClone(detail) });
    if (state.breadcrumbs.length > MAX_BREADCRUMBS) state.breadcrumbs.splice(0, state.breadcrumbs.length - MAX_BREADCRUMBS);
  }

  function record(level, message, detail, scope) {
    const error = extractErrorLike(detail);
    const payload = {
      level,
      scope: normalizeText(scope || "app"),
      message: normalizeText(message),
      errorName: error.name,
      errorMessage: error.message,
      stack: error.stack,
      status: extractStatus(detail),
      url: extractUrl(detail),
      method: normalizeMethod(detail?.method),
      detail: safeClone(detail),
      occurredAt: nowIso()
    };
    payload.isTransientNetworkFailure = isTransientNetworkFailure(payload);
    payload.user = classifyIssue(payload);
    payload.fingerprint = [payload.scope, payload.errorName, payload.errorMessage || payload.message, payload.status, payload.url, firstStackFrame(payload.stack)].join("|").toLowerCase();
    payload.breadcrumbs = state.breadcrumbs.slice(-8);

    let event = state.indexByFingerprint.get(payload.fingerprint);
    if (event) {
      event.lastSeen = payload.occurredAt;
      event.sessionCount += 1;
      event.payload = payload;
    } else {
      event = { fingerprint: payload.fingerprint, firstSeen: payload.occurredAt, lastSeen: payload.occurredAt, sessionCount: 1, payload };
      state.indexByFingerprint.set(payload.fingerprint, event);
      state.events.unshift(event);
      if (state.events.length > MAX_SESSION_EVENTS) state.indexByFingerprint.delete(state.events.pop()?.fingerprint);
    }

    if (!payload.isTransientNetworkFailure) updateKnowledge(event);
    render();
    if (level === "error" || payload.user.severity === "critical") setDrawerOpen(true);
    const prefix = `[page-health:${pageKey}] ${payload.scope}: ${payload.message}`;
    (level === "warn" ? console.warn : console.error)(prefix, detail);
    return event;
  }

  function updateKnowledge(event) {
    const pageKnowledge = state.knowledge[pageKey] || {};
    const prior = pageKnowledge[event.fingerprint] || { count: 0, firstSeen: event.firstSeen };
    pageKnowledge[event.fingerprint] = {
      count: prior.count + 1,
      firstSeen: prior.firstSeen || event.firstSeen,
      lastSeen: event.lastSeen,
      title: event.payload.user.title,
      message: event.payload.message,
      scope: event.payload.scope,
      severity: event.payload.user.severity,
      status: event.payload.status,
      url: event.payload.url,
      method: event.payload.method,
      environment
    };
    const entries = Object.entries(pageKnowledge);
    if (entries.length > MAX_KNOWN_PATTERNS) {
      entries.sort((left, right) => (right[1].count - left[1].count) || String(right[1].lastSeen).localeCompare(String(left[1].lastSeen))).slice(MAX_KNOWN_PATTERNS).forEach(([fingerprint]) => delete pageKnowledge[fingerprint]);
    }
    state.knowledge[pageKey] = pageKnowledge;
    saveKnowledge();
  }

  function bindGlobalDiagnostics() {
    window.addEventListener("error", event => {
      current.error("Unhandled window error", { message: event.message, filename: event.filename, lineno: event.lineno, colno: event.colno, error: event.error }, "global");
    });
    window.addEventListener("unhandledrejection", event => {
      current.error("Unhandled promise rejection", { reason: event.reason }, "global");
    });
  }

  function wrapFetch() {
    if (typeof window.fetch !== "function") return;
    const fetchWithPageHealth = window.fetch.bind(window);
    window.fetch = async function (input, init) {
      const url = typeof input === "string" ? input : normalizeText(input?.url);
      const method = normalizeText(init?.method || input?.method || "GET").toUpperCase();
      const startedAt = Date.now();
      try {
        const response = await fetchWithPageHealth(input, init);
        if (response.ok) {
          resolveTransientNetworkFailures(url, method);
        } else {
          const detail = { url, method, status: response.status, statusText: response.statusText, durationMs: Date.now() - startedAt };
          (response.status >= 500 || response.status === 401 || response.status === 403 ? current.error : current.warn)(`HTTP ${response.status} from ${method} ${url}`, detail, "network");
        }
        return response;
      } catch (error) {
        current.error(`Network request failed for ${method} ${url}`, { url, method, durationMs: Date.now() - startedAt, error }, "network");
        throw error;
      }
    };
  }

  function ensureUi() {
    if (state.ui || !document.body) return;
    const root = document.createElement("div");
    root.className = "legend-page-health-root";
    root.innerHTML = `
      <button type="button" class="legend-page-health-toggle is-healthy" aria-expanded="false">
        <span class="legend-page-health-dot" aria-hidden="true"></span>
        <span class="legend-page-health-copy"><span class="legend-page-health-label">Page Health</span><span class="legend-page-health-status">No active issues</span></span>
        <span class="legend-page-health-count">0</span>
      </button>
      <div class="legend-page-health-backdrop" hidden></div>
      <aside class="legend-page-health-drawer" aria-hidden="true">
        <div class="legend-page-health-head"><div class="legend-page-health-head-copy"><div class="legend-page-health-kicker">${escapeHtml(environment)} diagnostics</div><h3>${escapeHtml(pageTitle)}</h3><p data-page-health-summary></p></div><button type="button" class="legend-page-health-close" aria-label="Close Page Health">Close</button></div>
        <div class="legend-page-health-toolbar"><button type="button" class="legend-page-health-btn" data-page-health-copy>Copy Report</button><button type="button" class="legend-page-health-btn" data-page-health-clear>Clear Session</button></div>
        <div class="legend-page-health-body"><section class="legend-page-health-section"><div class="legend-page-health-section-title">Current Session</div><div class="legend-page-health-section-sub">Issues observed since this page loaded.</div><div class="legend-page-health-events"></div></section><section class="legend-page-health-section"><div class="legend-page-health-section-title">Learned Patterns</div><div class="legend-page-health-section-sub">Recurring failure fingerprints stored in this browser for this page.</div><div class="legend-page-health-patterns"></div></section></div>
      </aside>`;
    document.body.appendChild(root);
    state.ui = {
      root,
      toggle: root.querySelector(".legend-page-health-toggle"),
      status: root.querySelector(".legend-page-health-status"),
      count: root.querySelector(".legend-page-health-count"),
      backdrop: root.querySelector(".legend-page-health-backdrop"),
      drawer: root.querySelector(".legend-page-health-drawer"),
      summary: root.querySelector("[data-page-health-summary]"),
      events: root.querySelector(".legend-page-health-events"),
      patterns: root.querySelector(".legend-page-health-patterns")
    };
    state.ui.toggle.addEventListener("click", () => setDrawerOpen(!state.drawerOpen));
    state.ui.backdrop.addEventListener("click", () => setDrawerOpen(false));
    root.querySelector(".legend-page-health-close").addEventListener("click", () => setDrawerOpen(false));
    root.querySelector("[data-page-health-clear]").addEventListener("click", current.clearSession);
    root.querySelector("[data-page-health-copy]").addEventListener("click", copyReport);
    setDrawerOpen(state.drawerOpen);
    beginPlacementTracking();
  }

  function beginPlacementTracking() {
    if (state.placementObserver) return;

    const schedulePlacement = () => {
      if (state.placementFrame) window.cancelAnimationFrame(state.placementFrame);
      state.placementFrame = window.requestAnimationFrame(syncPlacement);
    };

    window.addEventListener("resize", schedulePlacement, { passive: true });
    window.addEventListener("load", schedulePlacement, { once: true });
    if (typeof ResizeObserver === "function") {
      state.placementObserver = new ResizeObserver(schedulePlacement);
      const main = getPrimaryMain();
      if (main) state.placementObserver.observe(main);
    } else {
      state.placementObserver = true;
    }

    schedulePlacement();
  }

  function getPrimaryMain() {
    return Array.from(document.querySelectorAll('main[role="main"], main'))
      .find(element => element.getBoundingClientRect().width > 0) || null;
  }

  function syncPlacement() {
    state.placementFrame = 0;
    if (!state.ui || !document.body) return;

    const main = getPrimaryMain();
    const mainRect = main?.getBoundingClientRect();
    const rightGutter = mainRect && mainRect.width > 0
      ? Math.max(0, Math.floor(window.innerWidth - Math.min(window.innerWidth, mainRect.right)))
      : 0;
    const gap = 12;
    const fullWidth = 190;
    const compactWidth = 44;

    if (rightGutter >= fullWidth + gap) {
      state.ui.root.dataset.placement = "rail";
      state.ui.root.dataset.mode = "full";
      state.ui.root.style.setProperty("--legend-page-health-right", `${Math.max(12, rightGutter - fullWidth - gap)}px`);
      document.body.classList.remove("legend-page-health-bottom-reserved");
      return;
    }

    if (rightGutter >= compactWidth + gap) {
      state.ui.root.dataset.placement = "rail";
      state.ui.root.dataset.mode = "compact";
      state.ui.root.style.setProperty("--legend-page-health-right", `${Math.max(12, rightGutter - compactWidth - gap)}px`);
      document.body.classList.remove("legend-page-health-bottom-reserved");
      return;
    }

    state.ui.root.dataset.placement = "bottom";
    state.ui.root.dataset.mode = "compact";
    state.ui.root.style.removeProperty("--legend-page-health-right");
    document.body.classList.add("legend-page-health-bottom-reserved");
  }

  function setDrawerOpen(open) {
    ensureUi();
    state.drawerOpen = !!open;
    if (!state.ui) return;
    state.ui.toggle.setAttribute("aria-expanded", String(state.drawerOpen));
    state.ui.drawer.setAttribute("aria-hidden", String(!state.drawerOpen));
    state.ui.drawer.classList.toggle("is-open", state.drawerOpen);
    state.ui.backdrop.classList.toggle("is-open", state.drawerOpen);
    state.ui.backdrop.hidden = !state.drawerOpen;
  }

  function render() {
    ensureUi();
    if (!state.ui) return;
    const errors = state.events.filter(event => event.payload.level === "error").length;
    const warnings = state.events.filter(event => event.payload.level === "warn").length;
    const issueCount = errors + warnings;
    const severity = errors ? "error" : warnings ? "warning" : "healthy";
    state.ui.toggle.classList.remove("is-healthy", "is-warning", "is-error");
    state.ui.toggle.classList.add(`is-${severity}`);
    state.ui.status.textContent = errors ? `${errors} active error${errors === 1 ? "" : "s"}` : warnings ? `${warnings} active warning${warnings === 1 ? "" : "s"}` : "No active issues";
    state.ui.count.textContent = String(issueCount);
    state.ui.summary.textContent = issueCount ? `${issueCount} current issue${issueCount === 1 ? "" : "s"} detected on ${pageTitle}.` : `Monitoring ${pageTitle}. No active issues detected in this session.`;
    state.ui.events.innerHTML = state.events.length ? state.events.map(renderEvent).join("") : '<div class="legend-page-health-empty">No active failures in this session. Page Health opens automatically when a new error is detected.</div>';
    const patterns = Object.entries(state.knowledge[pageKey] || {}).sort((left, right) => (right[1].count - left[1].count) || String(right[1].lastSeen).localeCompare(String(left[1].lastSeen))).slice(0, 6);
    state.ui.patterns.innerHTML = patterns.length ? patterns.map(renderPattern).join("") : '<div class="legend-page-health-empty">No recurring issue patterns have been recorded for this page.</div>';
  }

  function renderEvent(event) {
    const payload = event.payload;
    const learned = state.knowledge[pageKey]?.[event.fingerprint];
    const detail = { scope: payload.scope, message: payload.message, errorName: payload.errorName || undefined, errorMessage: payload.errorMessage || undefined, status: payload.status || undefined, url: payload.url || undefined, fingerprint: event.fingerprint, firstSeen: event.firstSeen, lastSeen: event.lastSeen, sessionCount: event.sessionCount, learnedCount: learned?.count || 0, breadcrumbs: payload.breadcrumbs, detail: payload.detail, stack: payload.stack || undefined };
    return `<article class="legend-page-health-event severity-${escapeHtml(payload.user.severity)}"><div class="legend-page-health-event-top"><span class="legend-page-health-chip is-${escapeHtml(payload.user.severity)}">${escapeHtml(payload.user.severity)}</span><span class="legend-page-health-meta">${escapeHtml(formatStamp(event.lastSeen))}</span></div><h4>${escapeHtml(payload.user.title)}</h4><p>${escapeHtml(payload.user.summary)}</p><p class="legend-page-health-action">${escapeHtml(payload.user.action)}</p><div class="legend-page-health-meta"><span>Scope: ${escapeHtml(payload.scope)}</span><span>Session: ${event.sessionCount}x</span><span>Learned: ${learned?.count || 0}x</span></div><details class="legend-page-health-detail"><summary>Technical details</summary><pre class="legend-page-health-code">${escapeHtml(JSON.stringify(detail, null, 2))}</pre></details></article>`;
  }

  function renderPattern([fingerprint, item]) {
    return `<article class="legend-page-health-pattern"><div class="legend-page-health-pattern-top"><strong>${escapeHtml(item.title || "Recurring issue")}</strong><span class="legend-page-health-chip is-${escapeHtml(item.severity || "medium")}">${escapeHtml(item.count)}x</span></div><div class="legend-page-health-pattern-copy">${escapeHtml(item.message || "")}</div><div class="legend-page-health-meta"><span>Scope: ${escapeHtml(item.scope || "app")}</span><span>Last seen: ${escapeHtml(formatStamp(item.lastSeen))}</span></div><details class="legend-page-health-detail"><summary>Fingerprint</summary><pre class="legend-page-health-code">${escapeHtml(fingerprint)}</pre></details></article>`;
  }

  function formatStamp(value) {
    try { return new Date(value).toLocaleString(); } catch { return value; }
  }

  async function copyReport() {
    const button = state.ui?.root.querySelector("[data-page-health-copy]");
    try {
      await navigator.clipboard.writeText(buildReport());
      button.textContent = "Copied";
      window.setTimeout(() => { button.textContent = "Copy Report"; }, 1200);
    } catch (error) {
      current.error("Could not copy the Page Health report", { error }, "diagnostics");
    }
  }

  function buildReport() {
    return JSON.stringify({ page: pageTitle, route: window.location.pathname, environment, generatedAt: nowIso(), currentIssues: state.events, learnedPatterns: state.knowledge[pageKey] || {} }, null, 2);
  }
})();
