(function () {
  "use strict";
  if (window.__legendSiteToolsInitialized) return;
  window.__legendSiteToolsInitialized = true;

  const modelContext = document.modelContext;
  if (!modelContext || typeof modelContext.registerTool !== "function" || typeof window.fetch !== "function") return;

  const registrationController = new AbortController();
  window.addEventListener("pagehide", () => registrationController.abort(), { once: true });
  let observer = null;
  let application = "";
  let sourceRevision = "";
  let csrf = "";
  let endpoint = "/api/legend-public-site-tools";
  let privateTransport = false;
  try {
    observer = Array.from(document.scripts || []).find(node => {
      try {
        const path = new URL(node.src, window.location.origin).pathname;
        return path === "/_content/Shared/js/page-health.js" || path === "/js/page-health.js";
      } catch { return false; }
    }) || null;
    application = typeof observer?.dataset?.app === "string" ? observer.dataset.app : "";
    sourceRevision = typeof observer?.dataset?.gitCommitHash === "string" ? observer.dataset.gitCommitHash : "";
    privateTransport = application === "AgentPortal" || application === "ClientApp";
    csrf = privateTransport && typeof observer?.dataset?.csrf === "string" ? observer.dataset.csrf : "";
    endpoint = privateTransport ? "/api/legend-site-tools" : "/api/legend-public-site-tools";
    const explicit = document.currentScript?.dataset?.endpoint;
    if (!privateTransport && typeof explicit === "string" && explicit.length > 0) {
      const candidate = new URL(explicit, window.location.origin);
      if (candidate.protocol === "https:" && !candidate.username && !candidate.password &&
          !candidate.search && !candidate.hash && candidate.pathname === "/api/legend-public-site-tools")
        endpoint = candidate.href.replace(/\/$/, "");
    }
  } catch { }

  function breakpoint(width) {
    if (width < 576) return "xs";
    if (width < 768) return "sm";
    if (width < 992) return "md";
    if (width < 1200) return "lg";
    if (width < 1400) return "xl";
    return "xxl";
  }

  function assets() {
    const result = [];
    const push = value => {
      try {
        const url = new URL(value, window.location.origin);
        if (url.origin === window.location.origin && !url.username && !url.password) result.push(url.pathname);
      } catch { }
    };
    try {
      Array.from(document.scripts || []).slice(0, 64).forEach(node => { if (node.src) push(node.src); });
      Array.from(document.querySelectorAll('link[rel="stylesheet"][href]')).slice(0, 64)
        .forEach(node => push(node.href));
    } catch { }
    return result.slice(0, 64);
  }

  function issues() {
    try {
      const report = JSON.parse(window.LegendPageHealth?.current?.exportReport?.() || "{}");
      return Array.isArray(report.currentIssues) ? report.currentIssues.slice(0, 18).map(item => {
        const value = item && typeof item === "object" && item.payload && typeof item.payload === "object" ? item.payload : {};
        return {
          errorName: value.errorName,
          statusCode: value.statusCode,
          category: value.category,
          operation: value.operation,
          sourcePath: value.sourceFilePath
        };
      }) : [];
    } catch { return []; }
  }

  function pageSnapshot() {
    const width = Math.round(window.innerWidth || document.documentElement.clientWidth || 0);
    const height = Math.round(window.innerHeight || document.documentElement.clientHeight || 0);
    const route = typeof observer?.dataset?.route === "string" ? observer.dataset.route : "";
    const structural = window.LegendPageHealth?.current?.structuralSnapshot?.() || {};
    return {
      application,
      sourceRevision,
      path: route,
      viewportWidth: width,
      viewportHeight: height,
      devicePixelRatio: Number(window.devicePixelRatio || 1),
      breakpoint: breakpoint(width),
      componentIds: Array.isArray(structural.componentIds) ? structural.componentIds : [],
      actionKeys: Array.isArray(structural.actionKeys) ? structural.actionKeys : [],
      compositionIds: Array.isArray(structural.compositionIds) ? structural.compositionIds : [],
      modalIds: Array.isArray(structural.modalIds) ? structural.modalIds : [],
      assetPaths: assets(),
      issues: issues()
    };
  }

  async function invoke(name, args) {
    if (privateTransport && !csrf) return { ok: false, error: "legend_site_tool_antiforgery_unavailable" };
    const headers = { "Content-Type": "application/json", "Accept": "application/json" };
    if (privateTransport) headers.RequestVerificationToken = csrf;
    const response = await window.fetch(endpoint + "/execute", {
      method: "POST",
      credentials: privateTransport ? "same-origin" : "omit",
      redirect: "error",
      headers,
      body: JSON.stringify({ name, arguments: args || {}, page: pageSnapshot() })
    });
    if (!response.ok) return { ok: false, error: "legend_site_tool_http_" + response.status };
    const contentType = response.headers.get("content-type") || "";
    if (!contentType.toLowerCase().includes("application/json")) return { ok: false, error: "legend_site_tool_response_invalid" };
    return await response.json();
  }

  async function register() {
    let response;
    try {
      response = await window.fetch(endpoint + "/catalog", {
        method: "GET", credentials: privateTransport ? "same-origin" : "omit", redirect: "error",
        headers: { "Accept": "application/json" }
      });
    } catch { return; }
    if (!response.ok) return;
    let payload;
    try { payload = await response.json(); } catch { return; }
    if (!payload || !Array.isArray(payload.tools)) return;

    for (const tool of payload.tools.slice(0, 24)) {
      if (!tool || tool.type !== "function" || typeof tool.name !== "string" ||
          typeof tool.description !== "string" || !tool.parameters) continue;
      try {
        const recordsEngineeringProof = tool.name === "legend_verify_current_page_repair";
        await modelContext.registerTool({
          name: tool.name,
          description: tool.description,
          inputSchema: tool.parameters,
          annotations: { readOnlyHint: !recordsEngineeringProof, untrustedContentHint: true, consequentialHint: recordsEngineeringProof, debugging: true },
          execute: async (args, options) => {
            if (options?.signal?.aborted) throw new DOMException("Aborted", "AbortError");
            return await invoke(tool.name, args);
          }
        }, { signal: registrationController.signal });
      } catch { }
    }
  }

  void register();
})();