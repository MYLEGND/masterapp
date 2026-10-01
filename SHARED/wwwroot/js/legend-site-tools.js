(function () {
  "use strict";
  if (window.__legendSiteToolsInitialized) return;
  window.__legendSiteToolsInitialized = true;

  const modelContext = document.modelContext;
  if (!modelContext || typeof modelContext.registerTool !== "function" || typeof window.fetch !== "function") return;

  const endpoint = "/api/legend-site-tools";

  function breakpoint(width) {
    if (width < 576) return "xs";
    if (width < 768) return "sm";
    if (width < 992) return "md";
    if (width < 1200) return "lg";
    if (width < 1400) return "xl";
    return "xxl";
  }

  function values(selector, attribute, maximum) {
    try {
      return Array.from(document.querySelectorAll(selector)).slice(0, maximum)
        .map(node => node.getAttribute(attribute))
        .filter(value => typeof value === "string" && value.length > 0 && value.length <= 128);
    } catch { return []; }
  }

  function assets() {
    const result = [];
    const push = value => {
      try {
        const url = new URL(value, window.location.origin);
        if (url.origin === window.location.origin && !url.search && !url.hash) result.push(url.pathname);
      } catch { }
    };
    try {
      Array.from(document.scripts || []).slice(0, 64).forEach(node => { if (node.src) push(node.src); });
      Array.from(document.querySelectorAll('link[rel="stylesheet"][href]')).slice(0, 64)
        .forEach(node => push(node.href));
    } catch { }
    return [...new Set(result)].slice(0, 64);
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
    let route = "";
    try {
      const observer = Array.from(document.scripts || []).find(node => {
        try { return new URL(node.src, window.location.origin).pathname === "/_content/Shared/js/page-health.js"; }
        catch { return false; }
      });
      route = typeof observer?.dataset?.route === "string" ? observer.dataset.route : "";
    } catch { }
    return {
      path: route,
      viewportWidth: width,
      viewportHeight: height,
      devicePixelRatio: Number(window.devicePixelRatio || 1),
      breakpoint: breakpoint(width),
      componentIds: [
        ...values("[data-canonical-id]", "data-canonical-id", 64),
        ...values("[data-component-id]", "data-component-id", 64),
        ...values("[data-system-key]", "data-system-key", 64)
      ],
      actionKeys: values("[data-action-key]", "data-action-key", 64),
      modalIds: Array.from(document.querySelectorAll('[role="dialog"][id],.modal[id]')).slice(0, 32)
        .filter(node => {
          try { const style = window.getComputedStyle(node); return style.display !== "none" && style.visibility !== "hidden"; }
          catch { return false; }
        }).map(node => node.id),
      assetPaths: assets(),
      issues: issues()
    };
  }

  async function invoke(name, args) {
    const response = await window.fetch(endpoint + "/execute", {
      method: "POST",
      credentials: "same-origin",
      redirect: "error",
      headers: { "Content-Type": "application/json", "Accept": "application/json" },
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
        method: "GET", credentials: "same-origin", redirect: "error", headers: { "Accept": "application/json" }
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
        await modelContext.registerTool({
          name: tool.name,
          description: tool.description,
          inputSchema: tool.parameters,
          annotations: { readOnlyHint: true, consequentialHint: false, untrustedContentHint: true },
          execute: async (args, context) => {
            if (context?.signal?.aborted) throw new DOMException("Aborted", "AbortError");
            return await invoke(tool.name, args);
          }
        });
      } catch { }
    }
  }

  void register();
})();