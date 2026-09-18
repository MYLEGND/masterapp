(function () {
  if (window.LegendPageHealth) return;

  // This is the existing observer, not an error classifier or repair authority.
  // Never retain caller messages/details, response bodies, query strings, or IDs.
  const endpoint = "/api/runtime-diagnostics";
  const metadata = document.currentScript?.dataset || {};
  const originalFetch = typeof window.fetch === "function" ? window.fetch.bind(window) : null;
  const csrf = typeof metadata.csrf === "string" ? metadata.csrf : "";
  const appIdentifier = ["AgentPortal", "ClientApp", "ParfaitApp", "Protect-Website", "ProtectWebsite"].includes(metadata.app)
    ? metadata.app : "Web";
  const route = typeof metadata.route === "string" && /^\/(?:[A-Za-z][A-Za-z0-9_]*\/[A-Za-z][A-Za-z0-9_]*)?$/.test(metadata.route)
    && metadata.route.length <= 256
    ? metadata.route : "/";
  const canManage = metadata.founder === "true";
  const observedRequestErrors = new WeakSet();
  const state = { events: [], queue: [], recent: new Map(), timer: null, inFlight: null,
    generation: 0, retired: false, disabled: !originalFetch || !csrf, submissions: 0 };
  const maximumQueue = 24;
  const maximumEvents = 18;
  const maximumAttempts = 3;
  const maximumSubmissions = 60;
  const maximumAge = 300000;
  const duplicateWindow = 30000;

  // Remove only the obsolete diagnostic cache; do not read it or touch app data.
  try { window.localStorage?.removeItem("legend_page_health_learning_v1"); } catch { }

  const current = Object.freeze({
    log() { return null; },
    warn(_message, detail, scope = "app") { return observe(detail, scope, "warning"); },
    error(_message, detail, scope = "app") { return observe(detail, scope, "error"); },
    open() { if (canManage) window.location.assign("/founder/diagnostics"); },
    close() { },
    clearSession() { clear(false); },
    exportReport() {
      return JSON.stringify({ route, generatedAt: new Date().toISOString(), currentIssues: state.events, learnedPatterns: {} });
    }
  });
  window.LegendPageHealth = Object.freeze({ current });

  function property(value, key) { try { return value?.[key]; } catch { return undefined; } }
  function knownErrorName(error) {
    const name = property(error, "name");
    return ["Error", "TypeError", "ReferenceError", "SyntaxError", "RangeError", "URIError", "EvalError",
      "AggregateError", "TimeoutError", "NetworkError", "AbortError", "SecurityError", "NotAllowedError", "NotFoundError"]
      .includes(name) ? name : "Error";
  }
  function staticScriptPath(value) {
    if (typeof value !== "string" || value.length > 2048) return "";
    try {
      const url = new URL(value, window.location.origin);
      if (url.origin !== window.location.origin || !/^\/(?:js|_content\/[A-Za-z0-9_.-]+\/js)\/[A-Za-z0-9_./-]+\.m?js$/.test(url.pathname)) return "";
      return Array.from(document.scripts || []).slice(0, 128).some(script => {
        try { const known = new URL(script.src, window.location.origin); return known.origin === url.origin && known.pathname === url.pathname; }
        catch { return false; }
      }) ? url.pathname.slice(0, 256) : "";
    } catch { return ""; }
  }
  function structuralStack(error, detail) {
    const frames = [];
    const filename = staticScriptPath(property(detail, "filename"));
    const line = Number(property(detail, "lineno"));
    const column = Number(property(detail, "colno"));
    if (filename && Number.isSafeInteger(line) && line > 0 && line <= 10000000)
      frames.push(`${filename}:${line}:${Number.isSafeInteger(column) && column >= 0 && column <= 10000000 ? column : 0}`);
    const stack = property(error, "stack");
    if (typeof stack === "string") {
      for (const candidate of stack.slice(0, 8192).split("\n").slice(1, 12)) {
        const match = /(https?:\/\/[^\s)]+):(\d{1,8}):(\d{1,8})\)?$/.exec(candidate.trim());
        const path = match ? staticScriptPath(match[1]) : "";
        if (path) frames.push(`${path}:${Number(match[2])}:${Number(match[3])}`);
        if (frames.length === 6) break;
      }
    }
    return [...new Set(frames)].slice(0, 6);
  }
  function observe(detail, scope, level) {
    try {
      if (state.retired) return null;
      const error = property(detail, "error") || property(detail, "reason") || detail;
      const errorName = knownErrorName(error);
      if (errorName === "AbortError") return null;
      const status = Number(property(detail, "status"));
      const statusCode = Number.isInteger(status) && status >= 400 && status <= 599 ? status : null;
      const method = property(detail, "method");
      const operation = scope === "network"
        ? "fetch_" + (["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"].includes(method) ? method : "OTHER")
        : (["global", "boot", "route", "ui"].includes(scope) ? scope : "app") + "_" + level;
      const frames = structuralStack(error, detail);
      const offline = scope === "network" && navigator.onLine === false;
      const payload = {
        appIdentifier, platform: "Web", route, sourceFilePath: frames[0]?.replace(/:\d+:\d+$/, "") || "",
        errorName: statusCode ? "HttpError" : offline ? "OfflineError"
          : scope === "network" && errorName !== "TimeoutError" ? "NetworkError" : errorName,
        errorMessage: statusCode ? "An HTTP request returned an unsuccessful status."
          : offline ? "The browser reported an offline network state."
          : scope === "network" ? "A browser request ended without a response." : "A browser script reported an error.",
        stackTrace: frames.join("\n"), gitCommitHash: "", timestamp: new Date().toISOString(),
        operation, correlationId: "",
        category: scope === "network" ? "Network" : "SuspectedDefect", statusCode, appVersion: ""
      };
      const key = [route, payload.errorName, payload.sourceFilePath, operation, statusCode].join("|");
      const now = Date.now();
      const existing = state.events.find(event => event.key === key);
      if (existing) { existing.sessionCount += 1; existing.payload = payload; }
      else { state.events.unshift({ key, sessionCount: 1, payload }); state.events.length = Math.min(state.events.length, maximumEvents); }
      if (!state.disabled && state.submissions < maximumSubmissions && now - (state.recent.get(key) ?? -Infinity) >= duplicateWindow) {
        state.recent.set(key, now);
        if (state.recent.size > 64) state.recent.delete(state.recent.keys().next().value);
        if (state.queue.length >= maximumQueue) state.queue.splice(state.inFlight ? 1 : 0, 1);
        state.queue.push({ payload, queuedAt: now, attempts: 0 });
        schedule(1000);
      }
      return existing || state.events[0];
    } catch { return null; } // Diagnostic collection must never change the application result.
  }
  function schedule(delay) {
    if (state.timer !== null || state.inFlight || state.disabled || state.retired || !state.queue.length || navigator.onLine === false) return;
    state.timer = window.setTimeout(() => { state.timer = null; void submit(); }, delay);
  }
  function clear(retired) {
    state.generation += 1;
    state.retired = retired;
    if (state.timer !== null) window.clearTimeout(state.timer);
    state.timer = null;
    state.queue = [];
    state.events = [];
    state.recent.clear();
    state.inFlight?.controller.abort();
    state.inFlight = null;
  }
  async function submit() {
    if (state.disabled || state.retired || state.inFlight || navigator.onLine === false) return;
    while (state.queue.length && Date.now() - state.queue[0].queuedAt >= maximumAge) state.queue.shift();
    const item = state.queue[0];
    if (!item || state.submissions >= maximumSubmissions) { state.queue = []; return; }
    const request = { controller: new AbortController(), generation: state.generation };
    state.inFlight = request;
    item.attempts += 1;
    state.submissions += 1;
    const timeout = window.setTimeout(() => request.controller.abort(), 8000);
    let retryDelay = item.attempts === 1 ? 5000 : 30000;
    let accepted = false;
    let terminal = false;
    try {
      const response = await originalFetch(endpoint, { method: "POST", credentials: "same-origin", redirect: "manual",
        headers: { "Content-Type": "application/json", "RequestVerificationToken": csrf },
        body: JSON.stringify(item.payload), signal: request.controller.signal });
      if (request.generation !== state.generation) return;
      accepted = response.status === 202;
      terminal = response.type === "opaqueredirect" || (response.status >= 300 && response.status < 500 && response.status !== 408 && response.status !== 429);
      if (terminal) { state.disabled = true; state.queue = []; }
      if (response.status === 429) {
        const value = response.headers?.get?.("Retry-After");
        const seconds = /^\d{1,6}$/.test(value || "") ? Number(value) : NaN;
        if (Number.isFinite(seconds)) retryDelay = Math.max(retryDelay, seconds * 1000);
      }
    } catch { /* Bounded retry; never feed an ingestion failure back into this observer. */ }
    finally {
      window.clearTimeout(timeout);
      if (state.inFlight === request) state.inFlight = null;
      if (request.generation === state.generation && !terminal) {
        if (state.queue[0] === item && (accepted || item.attempts >= maximumAttempts ||
            Date.now() + retryDelay - item.queuedAt >= maximumAge)) state.queue.shift();
        schedule(accepted ? 1000 : retryDelay);
      }
    }
  }

  window.addEventListener("error", event => observe({ error: event.error, filename: event.filename,
    lineno: event.lineno, colno: event.colno }, "global", "error"));
  window.addEventListener("unhandledrejection", event => {
    if (event.reason && typeof event.reason === "object" && observedRequestErrors.has(event.reason)) return;
    observe({ reason: event.reason }, "global", "error");
  });
  window.addEventListener("online", () => schedule(1000));
  window.addEventListener("pagehide", () => clear(true));
  window.addEventListener("pageshow", () => { state.retired = false; });
  if (originalFetch) window.fetch = async function (input, init) {
    const generation = state.generation;
    let method = "GET", signal, isIngestion = false;
    try {
      const value = typeof input === "string" || input instanceof URL ? String(input) : input?.url;
      const url = new URL(value, window.location.origin);
      isIngestion = url.origin === window.location.origin && url.pathname.replace(/\/+$/, "") === endpoint;
      method = String(init?.method || input?.method || "GET").toUpperCase();
      signal = init?.signal ?? input?.signal;
    } catch { }
    try {
      const response = await originalFetch(input, init);
      if (generation === state.generation && !isIngestion && !response.ok)
        observe({ method, status: response.status }, "network", "error");
      return response;
    } catch (error) {
      if (error && typeof error === "object") observedRequestErrors.add(error);
      const cancelled = signal?.aborted && signal.reason?.name !== "TimeoutError"
        && (error === signal.reason || error?.name === "AbortError");
      if (generation === state.generation && !isIngestion && !cancelled) observe({ error: signal?.aborted && signal.reason?.name === "TimeoutError"
        && error?.name === "AbortError" ? signal.reason : error, method }, "network", "error");
      throw error;
    }
  };
})();
