(function(){
  if (window.LegendCrmDiagnostics) return;

  const KNOWLEDGE_KEY = "legend_crm_diagnostics_learning_v1";
  const LEGACY_KEYS = ["clients", "leads"].map(page => `legend_qv_diag_${page}_v1`);
  const MAX_SESSION_EVENTS = 18;
  const MAX_BREADCRUMBS = 12;
  const MAX_KNOWN_PATTERNS = 24;

  function nowIso(){
    return new Date().toISOString();
  }

  function inferEnvironment(){
    const host = window.location.hostname.toLowerCase();
    if (host === "localhost" || host === "127.0.0.1" || host.endsWith(".local")) return "Local";
    return "Production";
  }

  function formatStamp(iso){
    try{
      return new Date(iso).toLocaleString();
    }catch{
      return iso;
    }
  }

  function escapeHtml(value){
    return String(value ?? "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  function safeClone(value, depth = 0){
    if (value == null) return value;
    if (value instanceof Error){
      return {
        name: value.name,
        message: value.message,
        stack: value.stack
      };
    }
    if (typeof value === "string" || typeof value === "number" || typeof value === "boolean"){
      return value;
    }
    if (Array.isArray(value)){
      return value.slice(0, 10).map(item => safeClone(item, depth + 1));
    }
    if (typeof value === "object"){
      if (depth >= 2) return String(value);
      const output = {};
      Object.keys(value).slice(0, 18).forEach(key => {
        output[key] = safeClone(value[key], depth + 1);
      });
      return output;
    }
    return String(value);
  }

  function prettyJson(value){
    try{
      return JSON.stringify(value, null, 2);
    }catch{
      return String(value ?? "");
    }
  }

  function normalizeText(value){
    return String(value ?? "").trim();
  }

  function loadKnowledge(){
    try{
      const parsed = JSON.parse(localStorage.getItem(KNOWLEDGE_KEY) || "{}");
      return parsed && typeof parsed === "object" ? parsed : {};
    }catch{
      return {};
    }
  }

  function saveKnowledge(knowledge){
    try{
      localStorage.setItem(KNOWLEDGE_KEY, JSON.stringify(knowledge));
    }catch{}
  }

  function pruneKnowledge(knowledge, pageKey){
    const pageEntries = Object.entries(knowledge[pageKey] || {});
    if (pageEntries.length <= MAX_KNOWN_PATTERNS) return;

    pageEntries
      .sort((a, b) => {
        const countGap = (b[1]?.count || 0) - (a[1]?.count || 0);
        if (countGap !== 0) return countGap;
        return String(b[1]?.lastSeen || "").localeCompare(String(a[1]?.lastSeen || ""));
      })
      .slice(MAX_KNOWN_PATTERNS)
      .forEach(([fingerprint]) => {
        delete knowledge[pageKey][fingerprint];
      });
  }

  function removeLegacyDiagnostics(pageKey){
    LEGACY_KEYS.forEach(key => {
      try{
        sessionStorage.removeItem(key);
      }catch{}
    });

    try{
      document.getElementById(`${pageKey}-qv-diagnostics`)?.remove();
    }catch{}

    try{
      if (window.__legendQuickViewDiagnostics){
        delete window.__legendQuickViewDiagnostics[pageKey];
      }
    }catch{}
  }

  function firstStackFrame(stack){
    const line = String(stack || "").split("\n").map(part => part.trim()).find(part => /^at\s+/i.test(part));
    return line || "";
  }

  function extractErrorLike(detail){
    const source = detail?.error || detail?.reason || detail;
    const name = normalizeText(source?.name);
    const message = normalizeText(source?.message || detail?.message);
    const stack = normalizeText(source?.stack || detail?.stack);
    return { name, message, stack };
  }

  function extractStatus(detail){
    const direct = Number(detail?.status);
    if (Number.isFinite(direct) && direct > 0) return direct;
    const nested = Number(detail?.response?.status);
    if (Number.isFinite(nested) && nested > 0) return nested;
    return 0;
  }

  function extractUrl(detail){
    return normalizeText(detail?.url || detail?.requestUrl || detail?.response?.url || detail?.filename);
  }

  function makeFingerprint(payload){
    return [
      payload.scope,
      payload.errorName || "",
      payload.errorMessage || payload.message || "",
      payload.status || "",
      payload.url || "",
      firstStackFrame(payload.stack || "")
    ].join("|").toLowerCase();
  }

  function classifyIssue(payload){
    const haystack = [
      payload.scope,
      payload.message,
      payload.errorName,
      payload.errorMessage,
      payload.url
    ].join(" ").toLowerCase();

    const status = payload.status;

    if (status === 401){
      return {
        severity: "high",
        title: "Your session expired.",
        summary: "This page could not complete a request because the current sign-in session is no longer valid.",
        action: "Sign back in or refresh this page and try again."
      };
    }

    if (status === 403){
      return {
        severity: "high",
        title: "This action is blocked by permissions.",
        summary: "The page reached the server, but the server rejected the request.",
        action: "Check account access or try the action with a user who has the right permissions."
      };
    }

    if (status === 404){
      return {
        severity: "medium",
        title: "A required endpoint could not be found.",
        summary: "The page asked for something the server does not currently expose at this route.",
        action: "Refresh first. If it repeats, the page and server routes are out of sync."
      };
    }

    if (status >= 500){
      return {
        severity: "critical",
        title: "The server hit an internal error.",
        summary: "The page reached the server, but the server failed while handling the request.",
        action: "Retry once. If it keeps happening, use the technical details below to trace the failing endpoint."
      };
    }

    if (/quick view/.test(haystack)){
      return {
        severity: "high",
        title: "Quick View could not finish opening.",
        summary: "This record did not fully load, so some details or actions may be missing.",
        action: "Try opening the record again. If it repeats, copy the technical details for the exact failing path."
      };
    }

    if (/calendar|busy calendar|meeting/.test(haystack)){
      return {
        severity: "medium",
        title: "Calendar tools did not load correctly.",
        summary: "Scheduling or availability features on this page did not initialize the way they should.",
        action: "Refresh the page. If this continues, inspect the technical details for the missing dependency or failing request."
      };
    }

    if (/boot|init|render|load/.test(haystack) && payload.scope === "boot"){
      return {
        severity: "high",
        title: "Part of the page did not finish loading.",
        summary: "A startup step failed, so this CRM page may be only partially ready.",
        action: "Refresh the page once. If the same startup failure returns, use the technical details to fix the boot step."
      };
    }

    if (/failed to fetch|network request failed|networkerror|load failed/.test(haystack)){
      return {
        severity: "high",
        title: "The page could not reach the server.",
        summary: "A network request failed before the page got a usable response back.",
        action: "Check connection, refresh the page, and review the endpoint details below if it keeps happening."
      };
    }

    if (/referenceerror|typeerror|syntaxerror|is not defined|cannot access/.test(haystack)){
      return {
        severity: "high",
        title: "A page script broke while this screen was running.",
        summary: "The browser hit a JavaScript error, so part of the page logic stopped early.",
        action: "Refresh the page if needed, then use the stack trace below to fix the exact script path."
      };
    }

    return {
      severity: payload.level === "warn" ? "medium" : "high",
      title: "The CRM page hit an unexpected problem.",
      summary: "Something on this screen failed in a way the page did not fully recover from.",
      action: "Review the technical details below and compare the recent breadcrumbs to the failing action."
    };
  }

  function summarizeRequest(input, init){
    const url = typeof input === "string"
      ? input
      : normalizeText(input?.url || "");
    const method = normalizeText(init?.method || input?.method || "GET").toUpperCase();
    return { url, method };
  }

  function copyText(text){
    if (!navigator.clipboard?.writeText) return Promise.reject(new Error("Clipboard unavailable"));
    return navigator.clipboard.writeText(text);
  }

  function createPageDiagnostics(config = {}){
    const pageKey = normalizeText(config.pageKey || "crm").toLowerCase();
    const pageTitle = normalizeText(config.pageTitle || "CRM");
    const environment = normalizeText(config.environment || inferEnvironment());

    removeLegacyDiagnostics(pageKey);

    const state = {
      pageKey,
      pageTitle,
      environment,
      events: [],
      breadcrumbs: [],
      indexByFingerprint: new Map(),
      knowledge: loadKnowledge(),
      ui: null,
      drawerOpen: false,
      globalBound: false
    };

    const api = {
      log(message, detail, scope = "app"){
        addBreadcrumb("log", message, detail, scope);
        return null;
      },
      warn(message, detail, scope = "app"){
        addBreadcrumb("warn", message, detail, scope);
        return record("warn", message, detail, scope);
      },
      error(message, detail, scope = "app"){
        addBreadcrumb("error", message, detail, scope);
        return record("error", message, detail, scope);
      },
      attachGlobalFetch(){
        attachGlobalFetch();
        return api;
      },
      open(){
        ensureUi();
        setDrawerOpen(true);
        return api;
      },
      close(){
        ensureUi();
        setDrawerOpen(false);
        return api;
      },
      clearSession(){
        state.events = [];
        state.indexByFingerprint.clear();
        render();
        return api;
      },
      exportReport(){
        return buildReport();
      }
    };

    bindGlobalHandlers();
    ensureUi();
    render();
    return api;

    function addBreadcrumb(level, message, detail, scope){
      state.breadcrumbs.push({
        at: nowIso(),
        level,
        scope: normalizeText(scope || "app"),
        message: normalizeText(message),
        detail: safeClone(detail)
      });

      if (state.breadcrumbs.length > MAX_BREADCRUMBS){
        state.breadcrumbs.splice(0, state.breadcrumbs.length - MAX_BREADCRUMBS);
      }
    }

    function record(level, message, detail, scope){
      const errorLike = extractErrorLike(detail);
      const payload = {
        level,
        scope: normalizeText(scope || "app"),
        message: normalizeText(message),
        errorName: errorLike.name,
        errorMessage: errorLike.message,
        stack: errorLike.stack,
        status: extractStatus(detail),
        url: extractUrl(detail),
        detail: safeClone(detail),
        occurredAt: nowIso()
      };

      payload.user = classifyIssue(payload);
      payload.fingerprint = makeFingerprint(payload);
      payload.breadcrumbs = state.breadcrumbs.slice(-8);

      let event = state.indexByFingerprint.get(payload.fingerprint);
      if (event){
        event.lastSeen = payload.occurredAt;
        event.sessionCount += 1;
        event.payload = payload;
      }else{
        event = {
          fingerprint: payload.fingerprint,
          firstSeen: payload.occurredAt,
          lastSeen: payload.occurredAt,
          sessionCount: 1,
          payload
        };
        state.indexByFingerprint.set(payload.fingerprint, event);
        state.events.unshift(event);
        if (state.events.length > MAX_SESSION_EVENTS){
          const removed = state.events.pop();
          if (removed) state.indexByFingerprint.delete(removed.fingerprint);
        }
      }

      updateKnowledge(event);
      render();

      if (level === "error" || payload.user.severity === "critical"){
        setDrawerOpen(true);
      }

      const prefix = `[${state.pageKey}] ${payload.scope}: ${payload.message}`;
      if (level === "warn"){
        console.warn(prefix, detail);
      }else{
        console.error(prefix, detail);
      }

      return event;
    }

    function updateKnowledge(event){
      const pageKnowledge = state.knowledge[state.pageKey] || {};
      const current = pageKnowledge[event.fingerprint] || {
        count: 0,
        firstSeen: event.firstSeen
      };

      pageKnowledge[event.fingerprint] = {
        count: (current.count || 0) + 1,
        firstSeen: current.firstSeen || event.firstSeen,
        lastSeen: event.lastSeen,
        title: event.payload.user.title,
        message: event.payload.message,
        scope: event.payload.scope,
        severity: event.payload.user.severity,
        environment: state.environment
      };

      state.knowledge[state.pageKey] = pageKnowledge;
      pruneKnowledge(state.knowledge, state.pageKey);
      saveKnowledge(state.knowledge);
    }

    function bindGlobalHandlers(){
      if (state.globalBound) return;
      state.globalBound = true;

      window.addEventListener("error", (event) => {
        api.error("Unhandled window error", {
          message: event.message,
          filename: event.filename,
          lineno: event.lineno,
          colno: event.colno,
          error: event.error
        }, "global");
      });

      window.addEventListener("unhandledrejection", (event) => {
        api.error("Unhandled promise rejection", {
          reason: event.reason
        }, "global");
      });
    }

    function attachGlobalFetch(){
      if (typeof window.fetch !== "function") return;

      const currentFetch = window.fetch;
      if (currentFetch.__legendCrmDiagnosticsWrappedFor === state.pageKey) return;

      const wrapped = async function(input, init){
        const request = summarizeRequest(input, init);
        const startedAt = Date.now();

        try{
          const response = await currentFetch(input, init);
          if (!response.ok){
            const message = `HTTP ${response.status} from ${request.method} ${request.url}`;
            const detail = {
              url: request.url,
              method: request.method,
              status: response.status,
              statusText: response.statusText,
              durationMs: Date.now() - startedAt
            };
            if (response.status >= 500 || response.status === 401 || response.status === 403){
              api.error(message, detail, "network");
            }else{
              api.warn(message, detail, "network");
            }
          }
          return response;
        }catch(error){
          api.error(`Network request failed for ${request.method} ${request.url}`, {
            url: request.url,
            method: request.method,
            durationMs: Date.now() - startedAt,
            error
          }, "network");
          throw error;
        }
      };

      wrapped.__legendCrmDiagnosticsWrappedFor = state.pageKey;
      window.fetch = wrapped;
    }

    function ensureUi(){
      if (state.ui || !document.body) return;

      const root = document.createElement("div");
      root.className = "crm-diag-root";
      root.innerHTML = `
        <button type="button" class="crm-diag-toggle is-healthy" aria-expanded="false">
          <span class="crm-diag-toggle-dot" aria-hidden="true"></span>
          <span class="crm-diag-toggle-copy">
            <span class="crm-diag-toggle-label">${escapeHtml(pageTitle)} Health</span>
            <span class="crm-diag-toggle-status">No active issues</span>
          </span>
          <span class="crm-diag-toggle-count">0</span>
        </button>
        <div class="crm-diag-backdrop" hidden></div>
        <aside class="crm-diag-drawer" aria-hidden="true">
          <div class="crm-diag-head">
            <div class="crm-diag-head-copy">
              <div class="crm-diag-kicker">${escapeHtml(environment)} Diagnostics</div>
              <h3>${escapeHtml(pageTitle)}</h3>
              <p data-crm-diag-summary>Monitoring this page for current-session failures and learned recurring patterns.</p>
            </div>
            <button type="button" class="crm-diag-close" aria-label="Close diagnostics">Close</button>
          </div>
          <div class="crm-diag-toolbar">
            <button type="button" class="crm-diag-btn" data-crm-diag-copy>Copy Report</button>
            <button type="button" class="crm-diag-btn" data-crm-diag-clear>Clear Session</button>
          </div>
          <div class="crm-diag-body">
            <section class="crm-diag-section">
              <div class="crm-diag-section-title">Current Session</div>
              <div class="crm-diag-section-sub">These are active issues seen since this page instance loaded.</div>
              <div class="crm-diag-events" data-crm-diag-events></div>
            </section>
            <section class="crm-diag-section">
              <div class="crm-diag-section-title">Learned Patterns</div>
              <div class="crm-diag-section-sub">Recurring fingerprints saved in this browser so repeated failures become easier to spot.</div>
              <div class="crm-diag-patterns" data-crm-diag-patterns></div>
            </section>
          </div>
        </aside>
      `;

      document.body.appendChild(root);

      state.ui = {
        root,
        toggle: root.querySelector(".crm-diag-toggle"),
        toggleStatus: root.querySelector(".crm-diag-toggle-status"),
        toggleCount: root.querySelector(".crm-diag-toggle-count"),
        backdrop: root.querySelector(".crm-diag-backdrop"),
        drawer: root.querySelector(".crm-diag-drawer"),
        summary: root.querySelector("[data-crm-diag-summary]"),
        events: root.querySelector("[data-crm-diag-events]"),
        patterns: root.querySelector("[data-crm-diag-patterns]")
      };

      state.ui.toggle?.addEventListener("click", () => setDrawerOpen(!state.drawerOpen));
      state.ui.backdrop?.addEventListener("click", () => setDrawerOpen(false));
      root.querySelector(".crm-diag-close")?.addEventListener("click", () => setDrawerOpen(false));
      root.querySelector("[data-crm-diag-clear]")?.addEventListener("click", () => {
        api.clearSession();
      });
      root.querySelector("[data-crm-diag-copy]")?.addEventListener("click", async () => {
        try{
          await copyText(buildReport());
          root.querySelector("[data-crm-diag-copy]").textContent = "Copied";
          setTimeout(() => {
            const copyBtn = root.querySelector("[data-crm-diag-copy]");
            if (copyBtn) copyBtn.textContent = "Copy Report";
          }, 1200);
        }catch(error){
          console.error("[crm-diag] copy report failed", error);
        }
      });
    }

    function setDrawerOpen(open){
      ensureUi();
      state.drawerOpen = !!open;
      if (!state.ui) return;

      state.ui.toggle?.setAttribute("aria-expanded", String(state.drawerOpen));
      state.ui.drawer?.setAttribute("aria-hidden", String(!state.drawerOpen));
      state.ui.drawer?.classList.toggle("is-open", state.drawerOpen);
      state.ui.backdrop?.classList.toggle("is-open", state.drawerOpen);
      if (state.ui.backdrop) state.ui.backdrop.hidden = !state.drawerOpen;
    }

    function render(){
      ensureUi();
      if (!state.ui) return;

      const errorCount = state.events.filter(event => event.payload.level === "error").length;
      const warningCount = state.events.filter(event => event.payload.level === "warn").length;
      const issueCount = errorCount + warningCount;
      const worstSeverity = errorCount > 0 ? "error" : warningCount > 0 ? "warning" : "healthy";

      state.ui.toggle?.classList.remove("is-healthy", "is-warning", "is-error");
      state.ui.toggle?.classList.add(`is-${worstSeverity}`);
      if (state.ui.toggleStatus){
        state.ui.toggleStatus.textContent = errorCount > 0
          ? `${errorCount} active error${errorCount === 1 ? "" : "s"}`
          : warningCount > 0
            ? `${warningCount} active warning${warningCount === 1 ? "" : "s"}`
            : "No active issues";
      }
      if (state.ui.toggleCount) state.ui.toggleCount.textContent = String(issueCount);
      if (state.ui.summary){
        state.ui.summary.textContent = issueCount
          ? `${issueCount} current issue${issueCount === 1 ? "" : "s"} detected on ${pageTitle}. Current environment: ${environment}.`
          : `Monitoring ${pageTitle}. Current environment: ${environment}. No active issues detected in this session.`;
      }

      state.ui.events.innerHTML = state.events.length
        ? state.events.map(renderEvent).join("")
        : `<div class="crm-diag-empty">No active failures in this session. The button will stay here and auto-open if a new issue is detected.</div>`;

      const knownPatterns = Object.entries(state.knowledge[state.pageKey] || {})
        .sort((a, b) => {
          const countGap = (b[1]?.count || 0) - (a[1]?.count || 0);
          if (countGap !== 0) return countGap;
          return String(b[1]?.lastSeen || "").localeCompare(String(a[1]?.lastSeen || ""));
        })
        .slice(0, 6);

      state.ui.patterns.innerHTML = knownPatterns.length
        ? knownPatterns.map(([fingerprint, item]) => `
            <article class="crm-diag-pattern">
              <div class="crm-diag-pattern-top">
                <strong>${escapeHtml(item.title || "Recurring issue")}</strong>
                <span class="crm-diag-chip is-${escapeHtml(item.severity || "medium")}">${escapeHtml(String(item.count || 0))}x</span>
              </div>
              <div class="crm-diag-pattern-copy">${escapeHtml(item.message || "")}</div>
              <div class="crm-diag-pattern-meta">
                <span>Scope: ${escapeHtml(item.scope || "app")}</span>
                <span>Last seen: ${escapeHtml(formatStamp(item.lastSeen || ""))}</span>
              </div>
              <details class="crm-diag-detail">
                <summary>Fingerprint</summary>
                <pre class="crm-diag-code">${escapeHtml(fingerprint)}</pre>
              </details>
            </article>
          `).join("")
        : `<div class="crm-diag-empty">No recurring issue patterns have been learned in this browser yet.</div>`;
    }

    function renderEvent(event){
      const payload = event.payload;
      const learned = state.knowledge[state.pageKey]?.[event.fingerprint];
      const technical = {
        scope: payload.scope,
        message: payload.message,
        errorName: payload.errorName || undefined,
        errorMessage: payload.errorMessage || undefined,
        status: payload.status || undefined,
        url: payload.url || undefined,
        fingerprint: event.fingerprint,
        firstSeen: event.firstSeen,
        lastSeen: event.lastSeen,
        sessionCount: event.sessionCount,
        learnedCount: learned?.count || event.sessionCount,
        breadcrumbs: payload.breadcrumbs,
        detail: payload.detail,
        stack: payload.stack || undefined
      };

      return `
        <article class="crm-diag-event severity-${escapeHtml(payload.user.severity)}">
          <div class="crm-diag-event-top">
            <span class="crm-diag-chip is-${escapeHtml(payload.user.severity)}">${escapeHtml(payload.user.severity)}</span>
            <span class="crm-diag-event-meta">${escapeHtml(formatStamp(event.lastSeen))}</span>
          </div>
          <h4>${escapeHtml(payload.user.title)}</h4>
          <p>${escapeHtml(payload.user.summary)}</p>
          <p class="crm-diag-action">${escapeHtml(payload.user.action)}</p>
          <div class="crm-diag-meta-row">
            <span>Scope: ${escapeHtml(payload.scope)}</span>
            <span>This session: ${escapeHtml(String(event.sessionCount))}x</span>
            <span>Learned: ${escapeHtml(String(learned?.count || event.sessionCount))}x</span>
          </div>
          <details class="crm-diag-detail">
            <summary>Technical details</summary>
            <pre class="crm-diag-code">${escapeHtml(prettyJson(technical))}</pre>
          </details>
        </article>
      `;
    }

    function buildReport(){
      const payload = {
        page: state.pageTitle,
        environment: state.environment,
        generatedAt: nowIso(),
        currentIssues: state.events.map(event => ({
          title: event.payload.user.title,
          summary: event.payload.user.summary,
          scope: event.payload.scope,
          message: event.payload.message,
          errorName: event.payload.errorName,
          errorMessage: event.payload.errorMessage,
          status: event.payload.status,
          url: event.payload.url,
          fingerprint: event.fingerprint,
          firstSeen: event.firstSeen,
          lastSeen: event.lastSeen,
          sessionCount: event.sessionCount,
          breadcrumbs: event.payload.breadcrumbs,
          detail: event.payload.detail,
          stack: event.payload.stack
        })),
        learnedPatterns: state.knowledge[state.pageKey] || {}
      };

      return prettyJson(payload);
    }
  }

  window.LegendCrmDiagnostics = Object.freeze({
    createPageDiagnostics
  });
})();
