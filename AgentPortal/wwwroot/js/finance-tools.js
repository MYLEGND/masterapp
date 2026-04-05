document.addEventListener("DOMContentLoaded", async function () {
    const dropdown = document.getElementById("budgetDropdown");
    const embedContainer = document.getElementById("budget-embed");
    const financeRoot = document.getElementById("financeRoot");
    const clientProfileId = financeRoot?.dataset.clientProfileId?.trim() || "";
    const clientUserId = financeRoot?.dataset.clientUserId?.trim() || "";
    const workspaceScope =
        clientUserId ||
        clientProfileId ||
        "agent";
    const plannerUserScope = (clientUserId || "").trim();
    // Local safe fallback for dev/non-auth cases to keep persistence per browser session without cross-user leakage on server
    const localFallbackUserKey = 'legend-finance:planner-fallback-user';
    const getLocalFallbackUser = () => {
        const existing = localStorage.getItem(localFallbackUserKey);
        if (existing) return existing;
        const gen = `localdev-${Math.random().toString(36).slice(2,10)}`;
        localStorage.setItem(localFallbackUserKey, gen);
        return gen;
    };
    const effectiveUserScope = plannerUserScope || getLocalFallbackUser();
    const scopeKey = (key) => `legend-finance:${workspaceScope}:${key}`;
    const plannerScopeKey = (key) => `legend-finance:user:${effectiveUserScope}:${key}`;
    const selectedToolStateId = "__workspace__";
    const disableLocalForWF = true; // Phase 2B: Wealth Forecast server-only
    const disableLocalForDP = true; // Phase 2C: Distribution Planner server-only
    const storageGet = (key) => localStorage.getItem(scopeKey(key));
    const storageSet = (key, value) => localStorage.setItem(scopeKey(key), value);
    const storageRemove = (key) => localStorage.removeItem(scopeKey(key));
    const canUseServerState = clientUserId.length > 0 || clientProfileId.length > 0;
    const toolStateIds = new Set([
        "WealthForecast",
        "SavingsAccelerator",
        "ExpenseLens",
        "NetWorth",
        "CashFlow",
        "DebtClarity",
        "FinancialBuffer",
        "WealthProjection",
        "FreedomIndex",
        "DebtAssetPulse"
    ]);

    const getAntiForgeryToken = () =>
        document.querySelector('#__af input[name="__RequestVerificationToken"]')?.value
        || document.querySelector('input[name="__RequestVerificationToken"]')?.value
        || "";

    function getStateKeys(key) {
        if (!key) return [];
        if (key === selectedToolStateId || key === "ActionTracker" || key.startsWith("toolState-")) {
            return [key];
        }

        if (toolStateIds.has(key)) {
            return [`toolState-${key}`, key];
        }

        return [key];
    }

    function getPrimaryStateKey(key) {
        const keys = getStateKeys(key);
        return keys.length > 0 ? keys[0] : key;
    }

    // Lazy-load Chart.js when needed (Wealth Forecast graph)
    let chartJsPromise = null;
    async function ensureChartJs() {
        if (typeof Chart !== "undefined") return;
        if (chartJsPromise) return chartJsPromise;
        chartJsPromise = new Promise((resolve, reject) => {
            const script = document.createElement("script");
            script.src = "https://cdn.jsdelivr.net/npm/chart.js";
            script.async = true;
            script.onload = () => resolve();
            script.onerror = reject;
            document.head.appendChild(script);
        });
        return chartJsPromise;
    }
    const buildQuery = (key) => {
        const params = new URLSearchParams({ toolId: key });
        if (clientUserId) params.set("clientUserId", clientUserId);
        if (clientProfileId) params.set("clientProfileId", clientProfileId);
        return params.toString();
    };

    async function loadPersistedState(key) {
        const keys = getStateKeys(key);

        if ((disableLocalForWF && keys.some(k => (k || "").includes("WealthForecast"))) ||
            (disableLocalForDP && keys.some(k => (k || "").includes("DistributionPlanner")))) {
            return {};
        }

        if (canUseServerState) {
            for (const candidateKey of keys) {
                try {
                    const url = `/api/finance-state/load?${buildQuery(candidateKey)}`;
                    const res = await fetch(url, { credentials: "include" });
                    if (res.ok) {
                        const payload = await res.json();
                        if (payload?.found) {
                            return JSON.parse(payload?.jsonState || "{}");
                        }
                    }
                } catch (_) { }
            }
        }

        // Fallback to local cache if server empty/unavailable
        for (const candidateKey of keys) {
                const localKey = (candidateKey && candidateKey.startsWith('DistributionPlanner')) ? plannerScopeKey(candidateKey) : scopeKey(candidateKey);
                const raw = localStorage.getItem(localKey);
                if (raw) {
                    return JSON.parse(raw || "{}");
                }
            }

        return {};
    }

                function savePersistedState(key, state) {
                    if ((disableLocalForWF && (key || "").includes("WealthForecast")) ||
                        (disableLocalForDP && (key || "").includes("DistributionPlanner"))) return;
                    const jsonState = JSON.stringify(state || {});
                    const primaryKey = getPrimaryStateKey(key);
                    // Always cache locally for instant restores and offline/dev use
                    const localKey = (primaryKey && primaryKey.startsWith('DistributionPlanner')) ? plannerScopeKey(primaryKey) : scopeKey(primaryKey);
                    localStorage.setItem(localKey, jsonState);

                    if (!canUseServerState) return;

                    const payload = {
                        clientProfileId,
                        clientUserId,
                        toolId: primaryKey,
                        jsonState
                    };

                    const token = getAntiForgeryToken();
                    const buildHeaders = (contentType) => {
                        const headers = contentType ? { "Content-Type": contentType } : {};
                        if (token) headers["RequestVerificationToken"] = token;
                        return headers;
                    };

                    const attempt = async (headers, body) => fetch("/api/finance-state/save", {
                        method: "POST",
                        credentials: "include",
                        headers,
                        body
                    });

                    attempt(buildHeaders("application/json"), JSON.stringify(payload))
                        .catch(() => attempt(buildHeaders("application/x-www-form-urlencoded; charset=UTF-8"), new URLSearchParams(payload).toString()))
                        .catch(() => { });
                }

    function clearPersistedState(key) {
        const keys = getStateKeys(key);
        if (!canUseServerState) {
            keys.forEach(k => {
                const localKey = (k && k.startsWith('DistributionPlanner')) ? plannerScopeKey(k) : scopeKey(k);
                localStorage.removeItem(localKey);
            });
        }

        if (!canUseServerState) return;

        keys.forEach((candidateKey) => {
            const url = `/api/finance-state/clear?${buildQuery(candidateKey)}`;
            const token = getAntiForgeryToken();
            const headers = token ? { "RequestVerificationToken": token } : {};
            fetch(url, {
                method: "DELETE",
                credentials: "include",
                headers
            }).catch(() => { });
        });
    }

    window.LegendFinancePersistence = {
        loadState: loadPersistedState,
        saveState: savePersistedState,
        clearState: clearPersistedState,
        scopeKey,
        usesServerState: canUseServerState
    };

    async function loadSelectedToolId() {
        if (!canUseServerState) {
            return storageGet("selected-tool") || "";
        }

        const state = await loadPersistedState(selectedToolStateId);
        return typeof state?.selectedToolId === "string" ? state.selectedToolId : "";
    }

    function saveSelectedToolId(toolId) {
        if (!canUseServerState) {
            if (toolId) storageSet("selected-tool", toolId);
            else storageRemove("selected-tool");
            return;
        }

        savePersistedState(selectedToolStateId, { selectedToolId: toolId || "" });
    }

    // ------------------- Persistence Helpers (UPDATED) -------------------
    function saveToolState(toolId) {
        if ((disableLocalForWF && toolId === 'WealthForecast') || (disableLocalForDP && toolId === 'DistributionPlanner')) return; // server-backed only
        const container = embedContainer.querySelector('.networth-tool');
        if (!container) return;

        const state = {};

        // Save all inputs
        container.querySelectorAll('input').forEach(input => state[input.id] = input.value);

        // Save all outputs (span, td)
        container.querySelectorAll('span, td').forEach(el => {
            if (el.id) state[el.id] = el.textContent;
        });

        // Save tips/advice/recommendations
        container.querySelectorAll('.advice, [id$="Advice"], [id$="Tip"], p.text-muted').forEach(el => {
            if (el.id) state[el.id] = el.textContent;
        });

        savePersistedState(`toolState-${toolId}`, state);
    }

    async function loadToolState(toolId) {
        if ((disableLocalForWF && toolId === 'WealthForecast') || (disableLocalForDP && toolId === 'DistributionPlanner')) return; // server-backed only
        const saved = await loadPersistedState(`toolState-${toolId}`);
        const container = embedContainer.querySelector('.networth-tool');
        if (!container) return;

        Object.keys(saved).forEach(id => {
            const el = document.getElementById(id);
            if (el) {
                if (el.tagName === 'INPUT') el.value = saved[id];
                else el.textContent = saved[id];
            }
        });

        // Re-apply saved tips/advice
        container.querySelectorAll('.advice, [id$="Advice"], [id$="Tip"], p.text-muted').forEach(el => {
            if (el.id && saved[el.id]) el.textContent = saved[el.id];
        });
    }

    function clearToolState(toolId) {
        clearPersistedState(`toolState-${toolId}`);
    }

    // ------------------- Clear Button -------------------
    function addClearButton(container, onClear, host) {
        if (!container) return;
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.textContent = 'Clear';
        btn.className = 'btn btn-outline-secondary btn-sm clear-btn';
        if (host) {
            btn.classList.add('wf-action-btn');
            btn.style.position = '';
            btn.style.top = '';
            btn.style.right = '';
            btn.style.zIndex = '';
            host.appendChild(btn);
        } else {
            container.style.position = 'relative';
            btn.style.position = 'absolute';
            btn.style.top = '20px';
            btn.style.right = '10px';
            btn.style.zIndex = '10';
            container.appendChild(btn);
        }
        btn.addEventListener('click', onClear);
    }

    // ------------------- Tool Box Sizing -------------------
    // ⚡ Adjust these values if you want a different default size
    const TOOL_WIDTH = 700;   // width in pixels
    const TOOL_HEIGHT = 550;  // height in pixels
    const TOOL_PADDING = 100; // padding inside the box

    function applyToolBoxStyles(container) {
        if (!container) return;

        // Visual styling only, no width/height
        container.style.boxSizing = 'border-box';
        container.style.overflow = 'visible';
        container.style.border = '1px solid #d6c48a';
        container.style.borderRadius = '16px';
        container.style.backgroundColor = '#ffffff'; // pure white
        container.style.boxShadow = '0 10px 28px rgba(166,128,35,0.12)'; // soft gold shadow
        container.style.margin = '0 auto 50px auto';
    }

    // ------------------- Global Tooltip Hide (bind once) -------------------
    // Active tool assigns: window.__LegendHideActiveTip = () => { ... }
    window.__LegendHideActiveTip = window.__LegendHideActiveTip || null;

    if (!window.__LegendTipHideBound) {
        document.addEventListener('click', () => {
            if (typeof window.__LegendHideActiveTip === "function") {
                window.__LegendHideActiveTip();
            }
        }, { passive: true });

        window.__LegendTipHideBound = true;
    }

    // ------------------- Tools -------------------
    const tools = [
        { id: "WealthForecast", name: "Wealth Forecast" },
        { id: "SavingsAccelerator", name: "Savings Accelerator" },
        { id: "ExpenseLens", name: "Expense Lens" },
        { id: "NetWorth", name: "Net Worth Tracker" },
        { id: "CashFlow", name: "Cash Flow Map" },
        { id: "DebtClarity", name: "Debt Clarity" },
        { id: "FinancialBuffer", name: "Financial Buffer" },
        { id: "WealthProjection", name: "Wealth Projection" },
        { id: "FreedomIndex", name: "Freedom Index" },
        { id: "DebtAssetPulse", name: "Debt vs Asset Pulse" }
    ];

    // Populate dropdown
    tools.forEach(tool => {
        const option = document.createElement("option");
        option.value = tool.id;
        option.textContent = tool.name;
        dropdown.appendChild(option);
    });


    function parsePercent(value) {
        return parseFloat(value.replace('%', '')) / 100 || 0;
    }
    function formatDollar(value) {
        return `$${(+value || 0).toLocaleString()}`;
    }

// ------------------- Financial Meaning Colors -------------------
const COLOR_INCOME  = "#1f9d55";  // green
const COLOR_EXPENSE = "#d64545";  // red
const COLOR_NEUTRAL = "#a68023";     // gold (for neutral inputs and tips)

function paint(el, color, weight = "800") {
  if (!el) return;
  el.style.setProperty("color", color, "important");
  el.style.setProperty("font-weight", weight, "important");
}

function markIncome(el)  { paint(el, COLOR_INCOME); }
function markExpense(el) { paint(el, COLOR_EXPENSE); }
function markNeutral(el) { paint(el, COLOR_NEUTRAL, "700"); }

// Safe toast helper for contexts where global toast may not be present
const toast = typeof window.toast === "function" ? window.toast : (msg => console.log(msg || ""));


    // ------------------- Tool Renderer -------------------
    const wfSearchHost = document.getElementById("wfClientSearchHost");

    dropdown.addEventListener("change", async function () {
        const t = tools.find(x => x.id === this.value);
        saveSelectedToolId(this.value || "");

        // clear UI
        embedContainer.innerHTML = '';

        // close any active tooltip cleanly
        if (typeof window.__LegendHideActiveTip === "function") window.__LegendHideActiveTip();

        // Toggle WF search host visibility
        if (wfSearchHost) {
            const show = !!t && t.id === "WealthForecast";
            wfSearchHost.classList.toggle("d-none", !show);
            if (!show) {
                const statusEl = document.getElementById("wfPlanStatus");
                if (statusEl) statusEl.textContent = "Type to search.";
                const resultsEl = document.getElementById("wfClientResults");
                if (resultsEl) { resultsEl.style.display = "none"; resultsEl.innerHTML = ""; }
                const inputEl = document.getElementById("wfClientSearch");
                if (inputEl) inputEl.value = "";
            }
        }

        if (!t) return;

        // shared WF plan state
        let wfActiveClientId = null;
        let wfPlanVersion = 0;
        let wfPlanLoaded = false;
        let wfSaveTimer = null;
        // shared DP plan state
        let dpActiveClientId = null;
        let dpPlanVersion = 0;
        let dpPlanCache = {}; // preserve WF section when saving from DP
        let dpSaveTimer = null;

        // ==========================================================
        // 1️⃣ WEALTH FORECAST (ELEVATED) + Tooltips
        // ==========================================================
        if (t.id === "WealthForecast") {
            await ensureChartJs();
            embedContainer.innerHTML = `
<div class="networth-tool" style="
    background:#ffffff; 
    padding:40px; 
    border-radius:20px; 
    box-shadow:0 12px 35px rgba(166,128,35,0.15); 
    max-width:1200px; 
    margin:0 auto;
    font-family: 'Inter', sans-serif;
">
    <!-- Tooltip styles (safe + isolated) -->
    <style>
        .wb-label{
            display:inline-flex;
            align-items:center;
            gap:8px;
            margin-top:15px;
            font-weight:500;
            font-size:1rem;
        }
        .wb-i{
            display:inline-flex;
            align-items:center;
            justify-content:center;
            width:18px;
            height:18px;
            border-radius:999px;
            background:#fff;
            border:1px solid rgba(210,31,43,.9);
            color:#d21f2b;
            font-weight:900;
            font-size:12px;
            line-height:1;
            cursor:pointer;
            user-select:none;
            transform: translateY(-1px);
            box-shadow:0 6px 18px rgba(0,0,0,.08);
        }
        .wb-i:focus{
            outline:none;
            box-shadow:0 0 0 3px rgba(210,31,43,.18), 0 10px 25px rgba(0,0,0,.10);
        }
        #wbTipLayer{
            position:fixed;
            inset:0;
            pointer-events:none;
            z-index:2147483647;
        }
        .wb-tipbox{
            position:absolute;
            max-width:min(360px, 86vw);
            background:#fff;
            color:#111;
            border:1px solid rgba(0,0,0,.12);
            border-left:4px solid #d21f2b;
            padding:12px 12px;
            border-radius:14px;
            font-size:12.8px;
            font-weight:650;
            line-height:1.35;
            box-shadow:0 18px 45px rgba(0,0,0,.18);
            opacity:0;
            transform:translateY(6px);
            transition:opacity .12s ease, transform .12s ease;
            pointer-events:none;
            white-space:normal;
        }
        .wb-tipbox b{ font-weight:900; }
        .wb-tipbox.show{ opacity:1; transform:translateY(0); }
        .wf-chart-wrap{
            width:100%;
            min-height:280px;
            padding:10px 4px 4px;
        }
        .wf-chart-wrap canvas{
            width:100% !important;
            height:320px !important;
        }
        .wf-output-col{
            display:flex;
            flex-direction:column;
            gap:14px;
            align-items:flex-end;
        }
        .wf-summary-box{
            width: min(420px, 100%);
            background: rgba(15,23,42,.92);
            border:1.5px solid #d1a034;
            border-radius:14px;
            box-shadow:0 12px 28px rgba(0,0,0,.25);
            padding:12px 14px;
            color:#eaf2ff;
        }
        .wf-summary-box table{
            color:#eaf2ff;
            background:transparent;
        }
        .wf-summary-box th{
            color:#f1f5f9;
            font-weight:800;
            width: 52%;
        }
        .wf-summary-box td{
            color:#eaf2ff;
            font-weight:900;
        }
        .wf-tip-text{
            color:#c9d7ff !important;
            font-style:italic;
        }
        .wfd-pos{color:#22c55e;}
        .wfd-neg{color:#ef4444;}
        .wfd-grow{color:#38bdf8;}
        .wf-header-row{
            display:grid;
            grid-template-columns:1fr auto;
            align-items:center;
            gap:12px;
            width:100%;
            margin-bottom:20px;
            position:relative;
            z-index:6;
        }
        .wf-title-stack h3{
            margin:0;
        }
        .wf-title-stack{flex:1;}
        .wf-actions{
            display:flex;
            align-items:center;
            gap:16px;
            flex-wrap:nowrap;
            justify-content:flex-end;
            flex-shrink:0;
            align-self:start;
            position:relative;
            z-index:7;
        }
        .wf-actions > button{flex:0 0 auto;margin:0; border-radius:12px !important;}
        .wf-actions .clear-btn{
            display:inline-flex;
            align-items:center;
            justify-content:center;
            height:36px;
            padding:6px 14px;
            border-radius:12px !important;
            font-weight:700;
            font-size:.82rem;
            margin:0;
        }
        .wf-action-btn{
            background:linear-gradient(135deg,#0f172a 0%,#1e293b 100%);
            color:#d9b35a;
            border:1.5px solid #a68023;
            font-weight:700;
            letter-spacing:.3px;
            border-radius:8px;
            padding:6px 14px;
            box-shadow:0 4px 12px rgba(166,128,35,.18);
            cursor:pointer;
            font-family:Inter,sans-serif;
            font-size:.82rem;
            transition:background .15s;
            min-width:110px;
            pointer-events:auto;
            position:relative;
            z-index:5;
        }
        .wf-action-btn:hover{background:linear-gradient(135deg,#1e293b 0%,#2d3f5c 100%);}

        /* ── Distribution Planner Launch Button ── */
        @keyframes wfd-launch-pulse {
            0%,100% { box-shadow: 0 4px 18px rgba(166,128,35,.35), 0 0 0 0 rgba(217,179,90,.45); }
            60%      { box-shadow: 0 6px 28px rgba(166,128,35,.55), 0 0 0 8px rgba(217,179,90,0); }
        }
        .wf-dist-launch-btn {
            background: linear-gradient(135deg, #c08a1f 0%, #d9b35a 50%, #a87820 100%);
            color: #0f172a;
            border: none;
            font-weight: 900;
            letter-spacing: .4px;
            border-radius: 12px;
            padding: 10px 22px;
            cursor: pointer;
            font-family: Inter, sans-serif;
            font-size: .92rem;
            min-width: 200px;
            pointer-events: auto;
            position: relative;
            z-index: 5;
            display: inline-flex;
            align-items: center;
            gap: 8px;
            animation: wfd-launch-pulse 2.8s ease-in-out infinite;
            transition: transform .15s, filter .15s;
            text-shadow: 0 1px 2px rgba(0,0,0,.18);
        }
        .wf-dist-launch-btn:hover {
            filter: brightness(1.10);
            transform: translateY(-1px);
            animation: none;
            box-shadow: 0 8px 32px rgba(166,128,35,.65);
        }
        .wf-dist-launch-btn:active { transform: translateY(0); filter: brightness(.95); }
        .wf-dist-launch-btn .wfd-btn-icon { font-size: 1rem; line-height: 1; }
        /* ── Wealth Forecast input grid (left side only) ── */
        .wf-input-grid{
            display:flex;
            flex-direction:column;
            gap:13px;
            width:100%;
        }
        .wf-row{
            display:grid;
            column-gap:14px;
            row-gap:6px;
            align-items:end;
        }
        .wf-row .wb-label{ margin-bottom:4px; }
        .wf-row.row-primary{ grid-template-columns: 1.5fr 1.5fr 1fr; }
        .wf-row.row-duo{ grid-template-columns: 1fr 1fr; }
        .wf-row.row-trio{ grid-template-columns: 1fr 1fr 1fr; }
        .wf-disrupt-card{
            border:1px solid rgba(166,128,35,0.35);
            border-radius:14px;
            padding:18px 18px 16px;
            background:rgba(166,128,35,0.06);
            box-shadow:0 8px 18px rgba(0,0,0,0.06);
            margin-top:30px;
        }
        .wf-disrupt-head{
            display:flex;
            flex-direction:column;
            gap:2px;
            margin-bottom:10px;
        }
        .wf-disrupt-title{
            font-weight:800;
            color:#a68023;
            text-transform:uppercase;
            letter-spacing:0.5px;
            font-size:.9rem;
        }
        .wf-disrupt-sub{
            color:#475569;
            font-size:.82rem;
            line-height:1.25;
        }
        .wf-disrupt-row{
            display:grid;
            grid-template-columns: 1fr 1fr;
            column-gap:14px;
            row-gap:12px;
        }
    </style>

    <div id="wbTipLayer"></div>
    <div class="wf-header-row">
      <div class="wf-title-stack">
        <h3 style="color:#a68023; font-weight:900; font-size:2.2rem; letter-spacing:0.5px;">
            ${t.name}
        </h3>
      </div>
      <div id="wfActions" class="wf-actions"></div>
    </div>
    <div style="display:flex; flex-wrap:wrap; gap:50px;">
        <!-- Inputs Column -->
        <div style="flex:1; min-width:400px;">
            <div class="wf-input-grid">
                <div class="wf-row row-primary">
                    <div>
                        <label class="wb-label">
                            Starting Balance
                            <span class="wb-i" tabindex="0" data-tip="<b>Examples:</b> 25,000 • 100,000 • 250,000 (existing investable assets at start)">i</span>
                        </label>
                        <div style="position:relative;">
                            <input id="wbStartingBalance" type="text" class="form-control" style="font-weight:700; font-size:1.1rem; color:#a68023; padding-right:30px;" />
                            <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">$</span>
                        </div>
                    </div>
                    <div>
                        <label class="wb-label">
                            Annual Income
                            <span class="wb-i" tabindex="0" data-tip="<b>Examples:</b> 60,000 • 85,500 • 120,000 (gross annual pay)">i</span>
                        </label>
                        <div style="position:relative;">
                            <input id="wbIncome" type="text" class="form-control" style="font-weight:700; font-size:1.1rem; color:#a68023; padding-right:30px;" />
                            <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">$</span>
                        </div>
                    </div>
                    <div>
                        <label class="wb-label">
                            Work Period
                            <span class="wb-i" tabindex="0" data-tip="<b>Examples:</b> 10 • 20 • 30 (years you plan to keep earning/saving)">i</span>
                        </label>
                        <input id="wbYears" type="text" class="form-control" style="font-weight:700; font-size:1.1rem; color:#a68023;" />
                    </div>
                </div>

                <div class="wf-row row-duo">
                    <div>
                        <label class="wb-label">
                            Inflation
                            <span class="wb-i" tabindex="0" data-tip="<b>Examples:</b> 2.5 • 3 • 4 (average annual inflation %)">i</span>
                        </label>
                        <div style="position:relative;">
                            <input id="wbInflation" type="text" class="form-control" style="font-weight:700; font-size:1.1rem; color:#a68023; padding-right:30px;" />
                            <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">%</span>
                        </div>
                    </div>
                    <div>
                        <label class="wb-label">
                            After-Tax Rate of Return
                            <span class="wb-i" tabindex="0" data-tip="<b>Examples:</b> 5 • 7 • 9 (after-tax investment return %)">i</span>
                        </label>
                        <div style="position:relative;">
                            <input id="wbReturn" type="text" class="form-control" style="font-weight:700; font-size:1.1rem; color:#a68023; padding-right:30px;" />
                            <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">%</span>
                        </div>
                    </div>
                </div>

                <div class="wf-row row-trio">
                    <div>
                        <label class="wb-label">
                            Tax Bracket
                            <span class="wb-i" tabindex="0" data-tip="<b>Examples:</b> 12 • 22 • 24 (effective/estimated rate %)">i</span>
                        </label>
                        <div style="position:relative;">
                            <input id="wbTax" type="text" class="form-control" style="font-weight:700; font-size:1.1rem; color:#a68023; padding-right:30px;" />
                            <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">%</span>
                        </div>
                    </div>
                    <div>
                        <label class="wb-label">
                            Fixed Liabilities
                            <span class="wb-i" tabindex="0" data-tip="<b>Examples:</b> 10 • 18 • 25 (debt payments as % of income)">i</span>
                        </label>
                        <div style="position:relative;">
                            <input id="wbLiabilities" type="text" class="form-control" style="font-weight:700; font-size:1.1rem; color:#a68023; padding-right:30px;" />
                            <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">%</span>
                        </div>
                    </div>
                    <div>
                        <label class="wb-label">
                            Lifestyle Spending
                            <span class="wb-i" tabindex="0" data-tip="<b>Examples:</b> 35 • 45 • 55 (living costs + wants as % of income)">i</span>
                        </label>
                        <div style="position:relative;">
                            <input id="wbLifestyle" type="text" class="form-control" style="font-weight:700; font-size:1.1rem; color:#a68023; padding-right:30px;" />
                            <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">%</span>
                        </div>
                    </div>
                </div>

                <div class="wf-disrupt-card">
                    <div class="wf-disrupt-head">
                        <div class="wf-disrupt-title">Income Disruption / Disability Income</div>
                        <div class="wf-disrupt-sub">Model a temporary income loss and disability income replacement during accumulation.</div>
                    </div>
                    <div class="wf-disrupt-row" style="margin-bottom:12px;">
                        <div>
                            <label class="wb-label">Disruption Start Year</label>
                            <input id="wbDisruptStartYear" type="text" class="form-control" style="font-weight:700; font-size:1.05rem; color:#0f172a;" placeholder="1" />
                        </div>
                        <div>
                            <label class="wb-label">Years of Income Disruption</label>
                            <input id="wbDisruptYears" type="text" class="form-control" style="font-weight:700; font-size:1.05rem; color:#0f172a;" placeholder="0" />
                        </div>
                    </div>
                    <div class="wf-disrupt-row">
                        <div>
                            <label class="wb-label">Months of Income Disruption</label>
                            <input id="wbDisruptMonths" type="text" class="form-control" style="font-weight:700; font-size:1.05rem; color:#0f172a;" placeholder="0" />
                        </div>
                        <div>
                            <label class="wb-label">Disability Income Replacement %</label>
                            <div style="position:relative;">
                                <input id="wbDisabilityPct" type="text" class="form-control" style="font-weight:700; font-size:1.05rem; color:#a68023; padding-right:30px;" placeholder="0" />
                                <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">%</span>
                            </div>
                        </div>
                    </div>
                </div>
            </div>

        </div>

        <!-- Outputs + Chart -->
        <div style="flex:1; min-width:420px;" class="wf-output-col">
            <div style="display:flex;gap:12px;align-items:center;justify-content:flex-end;margin-bottom:8px;flex-wrap:wrap;">
                <label style="display:flex;align-items:center;gap:6px;font-weight:800;color:#22c55e;font-size:.9rem;">
                    <input id="wf_toggleWealth" type="checkbox" checked style="width:16px;height:16px;"> Projected Wealth
                </label>
                <label style="display:flex;align-items:center;gap:6px;font-weight:800;color:#f87171;font-size:.9rem;">
                    <input id="wf_toggleSpend" type="checkbox" style="width:16px;height:16px;"> Cumulative Spending
                </label>
            </div>
            <div class="wf-chart-wrap">
                <canvas id="wfChart" aria-label="Wealth forecast chart" role="img"></canvas>
            </div>
            <div class="wf-summary-box">
                <table class="table table-sm mb-2">
                    <tr><th>Real Growth Rate</th><td id="wbRealGrowth">0%</td></tr>
                    <tr><th>Savings</th><td id="wbSavingsPercent">0%</td></tr>
                    <tr><th>Annual Savings</th><td id="wbActualSavings">$0</td></tr>
                </table>
                <table class="table table-sm mb-0">
                    <tr><th>Tips & Suggestions</th><td id="wbSavingsTips" class="wf-tip-text">
                        Enter your profile above to calculate savings.
                    </td></tr>
                </table>
                <!-- hidden holders to keep IDs for logic -->
                <span id="wbEarnings" style="display:none">$0</span>
                <span id="wbWealth" style="display:none">$0</span>
            </div>
        </div>
    </div>
</div>`;

            // Grab container and elements
            const container = embedContainer.querySelector('.networth-tool');
            const incomeEl = document.getElementById("wbIncome");
            const yearsEl = document.getElementById("wbYears");
            const inflEl = document.getElementById("wbInflation");
            const retEl = document.getElementById("wbReturn");
            const taxEl = document.getElementById("wbTax");
            const liabEl = document.getElementById("wbLiabilities");
            const lifeEl = document.getElementById("wbLifestyle");
            const startingBalEl = document.getElementById("wbStartingBalance");
            const disruptStartEl = document.getElementById("wbDisruptStartYear");
            const disruptYearsEl = document.getElementById("wbDisruptYears");
            const disruptMonthsEl = document.getElementById("wbDisruptMonths");
            const disabilityPctEl = document.getElementById("wbDisabilityPct");

            const earningsOut = document.getElementById("wbEarnings");
            const wealthOut = document.getElementById("wbWealth");
            const realGrowthOut = document.getElementById("wbRealGrowth");
            const savingsPercentOut = document.getElementById("wbSavingsPercent");

            const actualSavingsOut = document.getElementById("wbActualSavings");
            const savingsTipsOut = document.getElementById("wbSavingsTips");
            const chartEl = document.getElementById("wfChart");
            let wfChart = null;
            const wealthToggle = document.getElementById('wf_toggleWealth');
            const spendToggle  = document.getElementById('wf_toggleSpend');

            function applyChartVisibility(update=true){
                if (!wfChart) return;
                wfChart.getDatasetMeta(0).hidden = wealthToggle && !wealthToggle.checked;
                wfChart.getDatasetMeta(1).hidden = spendToggle && !spendToggle.checked;
                if (update) wfChart.update();
            }
            const wfLabelPlugin = {
                id: "wfLabelPlugin",
                afterDatasetsDraw(chart){
                    const {ctx, data} = chart;
                    const area = chart.chartArea;
                    const slots = [
                        { x: area.right - 8, y: area.top + 14 },
                        { x: area.right - 8, y: area.bottom - 14 }
                    ];
                    ctx.save();
                    data.datasets.forEach((ds, i) => {
                        const val = ds.data?.[ds.data.length - 1];
                        const meta = chart.getDatasetMeta(i);
                        if (val == null || meta.hidden) return;
                        const label = `$${Number(val).toLocaleString()}`;
                        const slot = slots[i % slots.length];
                        const padX = 6;
                        ctx.font = "bold 13px 'Inter', sans-serif";
                        const textW = ctx.measureText(label).width;
                        const boxW = textW + padX * 2;
                        const boxH = 20;
                        const boxX = slot.x - boxW;
                        const boxY = slot.y - boxH / 2;
                        const r = 6;
                        ctx.fillStyle = "rgba(15,23,42,0.85)";
                        ctx.strokeStyle = ds.borderColor || "#d1a034";
                        ctx.lineWidth = 1.2;
                        ctx.beginPath();
                        ctx.moveTo(boxX + r, boxY);
                        ctx.lineTo(boxX + boxW - r, boxY);
                        ctx.quadraticCurveTo(boxX + boxW, boxY, boxX + boxW, boxY + r);
                        ctx.lineTo(boxX + boxW, boxY + boxH - r);
                        ctx.quadraticCurveTo(boxX + boxW, boxY + boxH, boxX + boxW - r, boxY + boxH);
                        ctx.lineTo(boxX + r, boxY + boxH);
                        ctx.quadraticCurveTo(boxX, boxY + boxH, boxX, boxY + boxH - r);
                        ctx.lineTo(boxX, boxY + r);
                        ctx.quadraticCurveTo(boxX, boxY, boxX + r, boxY);
                        ctx.closePath();
                        ctx.fill();
                        ctx.stroke();
                        ctx.fillStyle = "#eaf2ff";
                        ctx.textAlign = "center";
                        ctx.textBaseline = "middle";
                        ctx.fillText(label, boxX + boxW / 2, boxY + boxH / 2);
                    });
                    ctx.restore();
                }
            };

            // Apply visual styles
            applyToolBoxStyles(container);

            // Load saved state AFTER DOM exists
            const TOOL_KEY = "WealthForecast";
            await loadToolState(TOOL_KEY);

            // ----- Tooltip engine (overlay) -----
            const tipLayer = document.getElementById('wbTipLayer');
            const tipBox = document.createElement('div');
            tipBox.className = 'wb-tipbox';
            tipLayer.appendChild(tipBox);

            const showTip = (el) => {
                const html = el.getAttribute('data-tip') || '';
                if (!html) return;

                tipBox.innerHTML = html;

                const r = el.getBoundingClientRect();
                const pad = 10;
                const boxW = Math.min(360, Math.floor(window.innerWidth * 0.86));

                let left = Math.min(window.innerWidth - boxW - pad, Math.max(pad, r.left - 10));
                tipBox.style.maxWidth = boxW + 'px';
                tipBox.style.left = left + 'px';

                tipBox.classList.add('show');
                const h = tipBox.getBoundingClientRect().height;

                let desiredTop = (r.top - h - 12);
                if (desiredTop < pad) desiredTop = (r.bottom + 12);

                tipBox.style.top = desiredTop + 'px';
            };

            const hideTip = () => tipBox.classList.remove('show');

            // Register for global click binder
            window.__LegendHideActiveTip = hideTip;

            container.querySelectorAll('.wb-i').forEach(el => {
                el.addEventListener('mouseenter', () => showTip(el));
                el.addEventListener('mouseleave', hideTip);
                el.addEventListener('focus', () => showTip(el));
                el.addEventListener('blur', hideTip);
                el.addEventListener('click', (e) => {
                    e.stopPropagation();
                    if (tipBox.classList.contains('show')) hideTip();
                    else showTip(el);
                });
            });

            // ==============================
            // Format inputs with commas on blur
            // ==============================
            [startingBalEl, incomeEl, yearsEl, inflEl, retEl, taxEl, liabEl, lifeEl, disruptStartEl, disruptYearsEl, disruptMonthsEl, disabilityPctEl].forEach(el => {
                el.addEventListener("blur", () => {
                    let val = el.value.replace(/,/g, '').replace('%', '');
                    if (!isNaN(val) && val !== '') {
                        el.value = Number(val).toLocaleString();
                    }
                });
            });

            const wfActionsEl = document.getElementById("wfActions");
            if (wfActionsEl){
                wfActionsEl.innerHTML = "";
            }

            const wfSearchInput = document.getElementById("wfClientSearch");
            let wfResultsEl = document.getElementById("wfClientResults");

            let wfSearchAbort = null;
            let wfSearchToken = 0;
            // Shared client selector for WF + DP
            let dpSearchInputRef = null;
            let dpResultsRef = null;
            const selectActiveClient = async (item) => {
                if (!item || !item.clientUserId) return;
                const name = item.displayName || item.clientUserId;
                if (wfSearchInput) wfSearchInput.value = name;
                if (dpSearchInputRef) dpSearchInputRef.value = name;
                if (wfResultsEl){ wfResultsEl.style.display = "none"; wfResultsEl.innerHTML = ""; }
                if (dpResultsRef){ dpResultsRef.style.display = "none"; dpResultsRef.innerHTML = ""; }
                const statusEl = document.getElementById("wfPlanStatus");
                if (statusEl){ statusEl.textContent = "Loading plan…"; statusEl.classList.remove("text-danger"); }
                wfActiveClientId = item.clientUserId;
                dpActiveClientId = item.clientUserId;
                wfPlanVersion = 0; dpPlanVersion = 0;
                wfPlanLoaded = false; dpPlanLoaded = false;
                await loadWfPlan(item.clientUserId);
            };
            async function searchWfClients(q){
                const statusEl = document.getElementById("wfPlanStatus");
                const qTrim = (q || "").trim();
                // cancel any in-flight request to keep typing snappy
                if (wfSearchAbort){ wfSearchAbort.abort(); wfSearchAbort = null; }
                wfSearchToken++;
                const token = wfSearchToken;

                if (qTrim.length === 0){
                    if (statusEl){ statusEl.textContent = "Type to search."; statusEl.classList.remove("text-danger"); }
                    if (wfResultsEl){ wfResultsEl.style.display = "none"; wfResultsEl.innerHTML = ""; }
                    wfActiveClientId = null;
                    return;
                }
                if (statusEl){ statusEl.textContent = "Searching…"; statusEl.classList.remove("text-danger"); }
                // keep current list visible to avoid flash while new results load
                try{
                    wfSearchAbort = new AbortController();
                    const res = await fetch(`/Clients/FinancialPlanClients?q=${encodeURIComponent(qTrim)}`, { credentials:"include", signal: wfSearchAbort.signal });
                    let list = [];
                    if (!res.ok){
                        const txt = await res.text().catch(()=> "");
                        throw new Error(txt || `Search failed (${res.status})`);
                    }
                    try { list = await res.json(); }
                    catch(parseErr){
                        throw new Error("Search response invalid.");
                    }
                    // ignore stale responses
                    if (token !== wfSearchToken) return;
                    if (!list || list.length === 0){
                        wfActiveClientId = null;
                        if (statusEl){ statusEl.textContent = "No results."; statusEl.classList.add("text-danger"); }
                        return;
                    }
                    // Render result list for selection
                    if (wfResultsEl){
                        const frag = document.createDocumentFragment();
                        list.forEach(item => {
                            const div = document.createElement("button");
                            div.type = "button";
                            div.className = "list-group-item list-group-item-action";
                            div.style.display = "flex";
                            div.style.flexDirection = "column";
                            div.style.alignItems = "flex-start";
                            div.innerHTML = `
                                <span style="font-weight:800;">${item.displayName || "Client"}</span>
                                <span style="font-size:12px;color:#6b7280;">${item.email || "—"}${item.phone ? " · " + item.phone : ""}</span>
                                <span style="font-size:11px;color:${item.hasSavedPlan ? '#16a34a' : '#9ca3af'};">${item.hasSavedPlan ? 'Plan saved' : 'No plan yet'}</span>
                            `;
                            div.addEventListener("click", async () => { await selectActiveClient(item); });
                            frag.appendChild(div);
                        });
                        wfResultsEl.replaceChildren(frag);
                        wfResultsEl.style.display = "block";
                    }
                    if (statusEl){ statusEl.textContent = `Found ${list.length}. Select to load.`; statusEl.classList.remove("text-danger"); }
                } catch(err){
                    if (token !== wfSearchToken) return; // stale/aborted
                    if (statusEl){ statusEl.textContent = err?.name === 'AbortError' ? "Searching…" : (err?.message || "Search failed."); statusEl.classList.add("text-danger"); }
                    if (err?.name !== 'AbortError') toast(err?.message || "Search failed.");
                }
            }

            function hydrateWfInputs(payload){
                const wf = (payload && payload.wealthForecast && payload.wealthForecast.inputs) || {};
                const map = {
                    wbStartingBalance: startingBalEl,
                    wbIncome: incomeEl,
                    wbYears: yearsEl,
                    wbInflation: inflEl,
                    wbReturn: retEl,
                    wbTax: taxEl,
                    wbLiabilities: liabEl,
                    wbLifestyle: lifeEl,
                    wbDisruptStartYear: disruptStartEl,
                    wbDisruptYears: disruptYearsEl,
                    wbDisruptMonths: disruptMonthsEl,
                    wbDisabilityPct: disabilityPctEl
                };
                Object.keys(map).forEach(id => { if (map[id] && wf[id] !== undefined) map[id].value = wf[id]; });

                const defaults = {
                    wbStartingBalance: "0",
                    wbDisruptStartYear: wf.wbDisruptStartYear ?? "1",
                    wbDisruptYears: wf.wbDisruptYears ?? "0",
                    wbDisruptMonths: wf.wbDisruptMonths ?? "0",
                    wbDisabilityPct: wf.wbDisabilityPct ?? "0"
                };
                Object.entries(defaults).forEach(([id, val]) => {
                    const el = map[id];
                    if (el && (el.value === undefined || el.value === null || el.value === "")) {
                        el.value = val;
                    }
                });
            }

            function wfPayload(){
                return {
                    version: wfPlanVersion,
                    wealthForecast: {
                        inputs: {
                            wbStartingBalance: startingBalEl.value || "",
                            wbIncome: incomeEl.value || "",
                            wbYears: yearsEl.value || "",
                            wbInflation: inflEl.value || "",
                            wbReturn: retEl.value || "",
                            wbTax: taxEl.value || "",
                            wbLiabilities: liabEl.value || "",
                            wbLifestyle: lifeEl.value || "",
                            wbDisruptStartYear: disruptStartEl.value || "",
                            wbDisruptYears: disruptYearsEl.value || "",
                            wbDisruptMonths: disruptMonthsEl.value || "",
                            wbDisabilityPct: disabilityPctEl.value || ""
                        }
                    }
                };
            }

            const wfPlanUrl = (cid) => `/clients/${encodeURIComponent(cid)}/financial-plan?clientUserId=${encodeURIComponent(cid)}`;
            // DP helpers are assigned after the DP module initializes
            let loadDpPlan = async function(){ console.warn("Distribution planner not ready yet."); };
            let normalizeDistributionPayload = null;

            async function loadWfPlan(clientUserId){
                const statusEl = document.getElementById("wfPlanStatus");
                if (statusEl) statusEl.textContent = "Loading plan…";
                wfPlanLoaded = false;
                try{
                    const res = await fetch(wfPlanUrl(clientUserId), { credentials:"include" });
                    if (!res.ok) throw new Error(`Load failed (${res.status})`);
                    const data = await res.json();
                    wfPlanVersion = data.version || 0;
                    hydrateWfInputs(JSON.parse(data.jsonData || "{}"));
                    if (statusEl) statusEl.textContent = data.updatedUtc ? `Loaded (updated ${new Date(data.updatedUtc).toLocaleString()})` : "Loaded";
                    wfPlanLoaded = true;
                    calcWealthForecast();
                    // Mirror selection into Distribution Planner
                    dpActiveClientId = clientUserId;
                    dpPlanVersion = 0;
                    dpPlanLoaded = false;
                    await loadDpPlan(clientUserId, true);
                }catch(err){
                    if (statusEl) statusEl.textContent = err?.message || "Load failed.";
                    toast(err?.message || "Failed to load plan.");
                }
            }

            function showWfError(msg){
                const statusEl = document.getElementById("wfPlanStatus");
                if (statusEl) statusEl.textContent = msg || "Error";
                toast(msg || "Save failed.");
            }

            async function saveWfPlan(){
                if (!wfActiveClientId) return;
                if (!wfPlanLoaded) {
                    showWfError("Plan not loaded — select/reload client before saving.");
                    return;
                }
                const payload = wfPayload();
                const res = await fetch(wfPlanUrl(wfActiveClientId), {
                    method:"POST",
                    credentials:"include",
                    headers:{ "Content-Type":"application/json" },
                    body: JSON.stringify({ clientUserId: wfActiveClientId, jsonData: JSON.stringify(payload), version: payload.version })
                });
                if (!res.ok){
                    if (res.status === 409){
                        showWfError("Version conflict — reload the latest plan before saving.");
                        toast("Version conflict — reload the latest plan before saving.");
                    } else showWfError(`Save failed (${res.status}).`);
                    return;
                }
                const data = await res.json();
                wfPlanVersion = data.version || wfPlanVersion;
                const statusEl = document.getElementById("wfPlanStatus");
                if (statusEl) statusEl.textContent = data.updatedUtc ? `Saved ${new Date(data.updatedUtc).toLocaleString()}` : "Saved";
            }

            function saveWfPlanDebounced(){
                if (!wfActiveClientId) return;
                if (!wfPlanLoaded) return;
                if (wfSaveTimer) clearTimeout(wfSaveTimer);
                wfSaveTimer = setTimeout(() => { void saveWfPlan(); }, 700);
            }

                const searchBtn = document.getElementById("wfClientSearchBtn");
                const searchInput = document.getElementById("wfClientSearch");
                searchBtn?.addEventListener("click", (e) => { e.preventDefault(); searchWfClients(searchInput?.value || ""); });
                searchInput?.addEventListener("keypress", (e) => { if (e.key === 'Enter'){ e.preventDefault(); searchWfClients(searchInput.value || ""); } });
                // live search on input (light debounce)
                let wfSearchTimer = null;
                searchInput?.addEventListener("input", (e) => {
                    if (wfSearchTimer) clearTimeout(wfSearchTimer);
                    wfSearchTimer = setTimeout(() => searchWfClients(searchInput.value || ""), 250);
                });

            // Main calculation function
            function calcWealthForecast() {
                const toNumber = (el, def = 0) => {
                    const raw = (el?.value || "").toString().replace(/,/g, '').replace('%', '');
                    const num = parseFloat(raw);
                    return Number.isFinite(num) ? num : def;
                };
                const clamp = (val, min, max) => Math.min(Math.max(val, min), max);

                const income = Math.max(0, toNumber(incomeEl, 0));
                const startingBalance = Math.max(0, toNumber(startingBalEl, 0));
                const years = Math.max(0, Math.floor(toNumber(yearsEl, 0)));
                const inflationRaw = toNumber(inflEl, 0) / 100;
                const nominalReturnRaw = toNumber(retEl, 0) / 100;
                const tax = clamp(toNumber(taxEl, 0) / 100, 0, 1);
                const liabilities = clamp(toNumber(liabEl, 0) / 100, 0, 1);
                const lifestyle = clamp(toNumber(lifeEl, 0) / 100, 0, 1);

                let disruptStart = Math.max(1, Math.floor(toNumber(disruptStartEl, 1)));
                let disruptYears = Math.max(0, Math.floor(toNumber(disruptYearsEl, 0)));
                let disruptMonths = clamp(Math.floor(toNumber(disruptMonthsEl, 0)), 0, 11);
                const disabilityPct = clamp(toNumber(disabilityPctEl, 0), 0, 60) / 100;

                // Clamp disruption to working window
                if (years > 0) disruptStart = clamp(disruptStart, 1, years);
                const startTime = Math.max(0, disruptStart - 1);
                let disruptDuration = disruptYears + (disruptMonths / 12);
                const maxDuration = Math.max(0, years - startTime);
                if (disruptDuration > maxDuration) disruptDuration = maxDuration;

                // Reflect clamped values in UI for clarity
                if (disruptStartEl && disruptStartEl.value) disruptStartEl.value = disruptStart.toLocaleString();
                if (disruptYearsEl && disruptYearsEl.value) disruptYearsEl.value = Math.floor(disruptYears).toLocaleString();
                if (disruptMonthsEl && disruptMonthsEl.value) disruptMonthsEl.value = Math.floor(disruptMonths).toLocaleString();
                if (disabilityPctEl && disabilityPctEl.value) disabilityPctEl.value = (disabilityPct * 100).toLocaleString();

                // Guard against divide-by-zero / runaway inflation inputs
                const inflation = Math.max(-0.95, inflationRaw);
                const nominalReturn = Math.max(-0.95, nominalReturnRaw);
                const realGrowthRate = (1 + nominalReturn) / (1 + inflation) - 1;

                // Baseline annual expense anchors (do not shrink during disruption)
                const baselineLiabAmt = income * liabilities;
                const baselineLifeAmt = income * lifestyle;

                let investedBalance = startingBalance;
                let cumulativeSpend = 0;
                let totalSavings = 0;
                let totalIncome = 0;

                const wealthPoints = [investedBalance];
                const spendPoints = [0];
                const labels = ["Year 0"];

                for (let y = 1; y <= years; y++) {
                    const yearStart = y - 1;
                    const yearEnd = y;
                    const overlap = Math.max(0, Math.min(yearEnd, startTime + disruptDuration) - Math.max(yearStart, startTime));
                    const disruptionFraction = clamp(overlap, 0, 1);

                    const lostIncome = income * disruptionFraction;
                    const replacementIncome = lostIncome * disabilityPct;
                    const earnedIncome = income - lostIncome;
                    const effectiveIncome = earnedIncome + replacementIncome;
                    const taxAmt = effectiveIncome * tax;
                    const annualExpenses = taxAmt + baselineLiabAmt + baselineLifeAmt;
                    const annualSavings = effectiveIncome - annualExpenses; // allow negative to reflect shortfall
                    const annualSpend = annualExpenses; // track true expense outflow

                    investedBalance = investedBalance * (1 + realGrowthRate) + annualSavings;
                    cumulativeSpend += annualSpend;
                    totalSavings += annualSavings;
                    totalIncome += effectiveIncome;

                    labels.push(`Year ${y}`);
                    wealthPoints.push(investedBalance);
                    spendPoints.push(-cumulativeSpend);
                }

                const avgSavingsRate = totalIncome > 0 ? totalSavings / totalIncome : 0;
                const totalSpend = cumulativeSpend;
                const avgAnnualSavings = years > 0 ? totalSavings / years : totalSavings;
                const avgAnnualSpend = years > 0 ? totalSpend / years : totalSpend;

                // Update outputs
                earningsOut.textContent = `$${Math.round(totalIncome).toLocaleString()}`;
                wealthOut.textContent = `$${Math.round(investedBalance).toLocaleString()}`;
                window.__wfFinalBalance = investedBalance > 0 ? investedBalance : null;
                if (typeof window.__wfOnBalanceUpdate === 'function') window.__wfOnBalanceUpdate(window.__wfFinalBalance);
                window.__wfState = {
                    annualIncome: income,
                    startingBalance,
                    workingYears: years,
                    inflationPct: inflation * 100,
                    returnPct: nominalReturn * 100,
                    taxPct: tax * 100,
                    liabilitiesPct: liabilities * 100,
                    lifestylePct: lifestyle * 100,
                    annualSavings: avgAnnualSavings,
                    annualSpend: avgAnnualSpend,
                    realGrowthPct: realGrowthRate * 100,
                    disruptionStartYear: disruptStart,
                    disruptionYears: disruptYears,
                    disruptionMonths: disruptMonths,
                    disabilityReplacementPct: disabilityPct * 100,
                    finalBalance: investedBalance
                };
                if (typeof window.__wfUpdateDistributionDefaults === 'function') {
                    window.__wfUpdateDistributionDefaults(window.__wfState);
                }
                realGrowthOut.textContent = `${(realGrowthRate * 100).toFixed(2)}%`;
                savingsPercentOut.textContent = `${(avgSavingsRate * 100).toFixed(2)}%`;
                actualSavingsOut.textContent = `$${Math.round(avgAnnualSavings).toLocaleString()}`;

// Inputs: income = green, % drains = red, years/return/inflation neutral
markIncome(incomeEl);
markNeutral(startingBalEl);
markExpense(taxEl);
markExpense(liabEl);
markExpense(lifeEl);

markNeutral(yearsEl);
markNeutral(inflEl);
markNeutral(retEl);
markNeutral(disruptStartEl);
markNeutral(disruptYearsEl);
markNeutral(disruptMonthsEl);
markNeutral(disabilityPctEl);

// Outputs
markIncome(earningsOut);
markIncome(wealthOut);
markIncome(actualSavingsOut);

// Savings percent is good if > 0, otherwise red
if (avgSavingsRate > 0) markIncome(savingsPercentOut);
else markExpense(savingsPercentOut);

// Real growth: green if positive, red if negative
if (realGrowthRate >= 0) markIncome(realGrowthOut);
else markExpense(realGrowthOut);

// Tips cell neutral
markNeutral(savingsTipsOut);

            // Chart update
            if (chartEl && typeof Chart !== "undefined"){
                    if (!wfChart){
                        wfChart = new Chart(chartEl, {
                            type: "line",
                            data: {
                                labels,
                                datasets: [{
                                    label: "Projected Wealth (toggle)",
                                    data: wealthPoints,
                                    borderWidth: 3,
                                    tension: 0.25,
                                    fill: false,
                                    borderColor: "#16a34a",
                                    pointRadius: ctx => ctx.dataIndex === ctx.dataset.data.length - 1 ? 5 : 0,
                                    pointHoverRadius: ctx => ctx.dataIndex === ctx.dataset.data.length - 1 ? 8 : 0,
                                    pointHitRadius: ctx => ctx.dataIndex === ctx.dataset.data.length - 1 ? 12 : 0
                                },{
                                    label: "Cumulative Spending (toggle)",
                                    data: spendPoints,
                                    borderWidth: 3,
                                    tension: 0.25,
                                    fill: false,
                                    borderColor: "#dc2626",
                                    pointRadius: ctx => ctx.dataIndex === ctx.dataset.data.length - 1 ? 5 : 0,
                                    pointHoverRadius: ctx => ctx.dataIndex === ctx.dataset.data.length - 1 ? 8 : 0,
                                    pointHitRadius: ctx => ctx.dataIndex === ctx.dataset.data.length - 1 ? 12 : 0
                                }]
                            },
                            options: {
                                responsive: true,
                                maintainAspectRatio: false,
                                plugins: {
                                    legend: { 
                                        display: true, 
                                        labels:{ color:"#eaf2ff", usePointStyle:true, boxWidth:14, padding:18 },
                                        onHover: (e) => { e.native.target.style.cursor = 'pointer'; },
                                        onLeave: (e) => { e.native.target.style.cursor = 'default'; },
                                        onClick: (e, legendItem, legend) => {
                                            const index = legendItem.datasetIndex;
                                            const ci = legend.chart;
                                            const meta = ci.getDatasetMeta(index);
                                            meta.hidden = meta.hidden === null ? !ci.data.datasets[index].hidden : null;
                                            ci.update();
                                        }
                                    },
                                    tooltip: {
                                        callbacks: {
                                            label: ctx => ` ${ctx.dataset.label}: ${ctx.formattedValue}`
                                        }
                                    }
                                },
                                scales: {
                                    x: {
                                        title: { display: true, text: "Year", color: "#eaf2ff" },
                                        grid: { color: "rgba(255,255,255,.08)" },
                                        ticks: { color: "#eaf2ff" }
                                    },
                                    y: {
                                        title: { display: true, text: "Projected Wealth / Spend ($)", color: "#eaf2ff" },
                                        grid: { color: "rgba(255,255,255,.08)" },
                                        ticks: {
                                            color: "#eaf2ff",
                                            callback: v => `$${Number(v).toLocaleString()}`
                                        }
                                    }
                                }
                            },
                            plugins: [wfLabelPlugin]
                        });
                        [wealthToggle, spendToggle].forEach(el => {
                            if (el) el.addEventListener('change', () => applyChartVisibility());
                        });
                        applyChartVisibility(false);
                    } else {
                        wfChart.data.labels = labels;
                        wfChart.data.datasets[0].data = wealthPoints;
                        wfChart.data.datasets[1].data = spendPoints;
                        applyChartVisibility(false);
                        wfChart.update("none");
                    }
                }

                const sTips = avgSavingsRate < 0.2
                    ? 'Savings potential is low; reduce lifestyle/fixed liabilities or raise replacement coverage.'
                    : 'Savings rate is strong; maximize to grow wealth.';
                savingsTipsOut.textContent = sTips;

                saveToolState(TOOL_KEY);
            }

            calcWealthForecast();

            // Attach input listeners for calculation
            [incomeEl, yearsEl, inflEl, retEl, taxEl, liabEl, lifeEl].forEach(el => {
                el.addEventListener("input", calcWealthForecast);
            });

            // Clear button
            const wfActionsHost = document.getElementById('wfActions');

            addClearButton(container, () => {
                [startingBalEl, incomeEl, yearsEl, inflEl, retEl, taxEl, liabEl, lifeEl].forEach(el => el.value = '');
                if (disruptStartEl) disruptStartEl.value = '1';
                if (disruptYearsEl) disruptYearsEl.value = '0';
                if (disruptMonthsEl) disruptMonthsEl.value = '0';
                if (disabilityPctEl) disabilityPctEl.value = '0';
                earningsOut.textContent = '$0';
                wealthOut.textContent = '$0';
                realGrowthOut.textContent = '0%';
                savingsPercentOut.textContent = '0%';
                actualSavingsOut.textContent = '$0';
                savingsTipsOut.textContent = 'Enter your profile above to calculate savings.';
                if (wfChart){
                    wfChart.data.labels = ["Year 0"];
                    wfChart.data.datasets[0].data = [0];
                    wfChart.data.datasets[1].data = [0];
                    applyChartVisibility(false);
                    wfChart.update();
                }
                window.__wfFinalBalance = null;
                if (typeof window.__wfOnBalanceUpdate === 'function') window.__wfOnBalanceUpdate(null);
                clearToolState(TOOL_KEY);
                hideTip();
            }, wfActionsHost);

            // ========================
            // DISTRIBUTION BUTTON (left of Clear)
            // ========================
            const distBtn = document.createElement('button');
            distBtn.type = 'button';
            distBtn.innerHTML = '<span class="wfd-btn-icon">&#9654;</span> Distribution Planner';
            distBtn.className = 'wf-dist-launch-btn';
            if (wfActionsHost) {
                const clearBtn = wfActionsHost.querySelector('.clear-btn');
                if (clearBtn) wfActionsHost.insertBefore(distBtn, clearBtn);
                else wfActionsHost.appendChild(distBtn);
            } else {
                container.appendChild(distBtn);
            }

            // Validation helpers (hoisted so they are always available)
            function validateDist(){
                const errs = [];
                const base          = pf(document.getElementById('wfd_base')?.value);
                const retAge        = pf(document.getElementById('wfd_retAge')?.value);
                const endAge        = pf(document.getElementById('wfd_endAge')?.value);
                const years         = Math.floor(endAge - retAge);
                const desiredInc    = pf(document.getElementById('wfd_desiredIncome')?.value);
                const invAllocPct   = pf(document.getElementById('wfd_invAlloc')?.value);
                const liAllocPct    = pf(document.getElementById('wfd_liAlloc')?.value);
                const annAllocPct   = pf(document.getElementById('wfd_annAlloc')?.value);
                const totalAlloc    = invAllocPct + liAllocPct + annAllocPct;
                if (desiredInc <= 0) errs.push('Desired annual income is required.');
                if (!base || base <= 0)             errs.push('Retirement Base is required. Run Wealth Forecast or enable Manual Override.');
                if (retAge <= 0 || endAge <= 0)     errs.push('Retirement Age and Plan End Age are required.');
                if (retAge >= endAge)               errs.push('Retirement Age must be less than Plan End Age.');
                if (years <= 0)                     errs.push('Distribution period must be at least 1 year.');
                if (Math.abs(totalAlloc - 100) > 0.11) errs.push(`Bucket allocations must total 100%. Current total: ${totalAlloc.toFixed(1)}%.`);
                return errs;
            }
            function showBlock(errs){
                // Use a single visible warning box; prefer the top box if present.
                const primary = document.getElementById('wfd_block_top') || document.getElementById('wfd_block');
                const secondary = document.getElementById('wfd_block');
                const apply = (el) => {
                    if (!el) return;
                    if (!errs.length){ el.style.display='none'; el.innerHTML=''; return; }
                    el.style.display='block';
                    el.innerHTML = errs.map(e=>`⚠️ ${e}`).join('<br>');
                };
                apply(primary);
                // Ensure no duplicate render in the secondary container
                if (secondary && secondary !== primary) { secondary.style.display = 'none'; secondary.innerHTML = ''; }
                lastValidationErrors = errs;
            }
            function validateAndGate(){
                const errs = validateDist();
                showBlock(errs);
            }

            // Priority-row toggler (hoisted so it exists even if modal already exists)
            function togglePriorityRow(){
                const row = document.getElementById('wfd_priorityRow');
                const strat = document.getElementById('wfd_strategy');
                if (!row || !strat) return;
                const show = ['priority','guardrail'].includes(strat.value);
                row.style.display = show ? 'block' : 'none';
            }

            // ========================
            // DISTRIBUTION MODAL — built once, lives in body
            // ========================
            const DIST_OVR_ID = 'wfDist_overlay';
            if (!document.getElementById(DIST_OVR_ID)) {
                const ovr = document.createElement('div');
                ovr.id = DIST_OVR_ID;
                ovr.setAttribute('role', 'dialog');
                ovr.setAttribute('aria-modal', 'true');
                ovr.setAttribute('aria-label', 'Distribution Planner');
                document.body.appendChild(ovr);

                ovr.innerHTML = `
<style>
#wfDist_overlay{
    display:none;position:fixed;inset:0;z-index:99999;
    background:rgba(5,10,20,.80);
    align-items:flex-start;justify-content:center;
    padding:20px 16px 48px;overflow-y:auto;
}
#wfDist_overlay.wfd-open{display:flex;}
#wfDist_panel{
    background:linear-gradient(145deg,#0b1529 0%,#0d1c36 100%);
    color:#e2e8f0;
    border-radius:20px;
    box-shadow:0 28px 70px rgba(0,0,0,.55);
    border:1.5px solid rgba(166,128,35,.55);
    width:100%;max-width:980px;
    font-family:'Inter',sans-serif;
    position:relative;margin:auto;
    overflow:hidden;
}
.wfd-hdr{
    background:linear-gradient(135deg,#0b1529 0%,#0f2040 100%);
    border-bottom:1.5px solid #a68023;
    border-radius:20px 20px 0 0;
    padding:22px 22px 18px;
}
.wfd-steps{
    display:flex;gap:8px;margin-top:14px;flex-wrap:wrap;
}
.wfd-step-chip{
    padding:9px 12px;border-radius:10px;
    background:rgba(255,255,255,.06);color:#cbd5e1;
    font-weight:800;font-size:.83rem;border:1px solid rgba(166,128,35,.35);
    cursor:pointer;display:flex;align-items:center;gap:8px;
}
.wfd-step-chip.active{background:#d9b35a;color:#0f172a;border-color:#d9b35a;}
.wfd-step-chip .step-num{width:22px;height:22px;display:inline-flex;align-items:center;justify-content:center;border-radius:50%;background:rgba(0,0,0,.25);font-weight:900;}
.wfd-step-chip.active .step-num{background:#0f172a;color:#d9b35a;}
.wfd-body{padding:18px 22px 22px;}
.wfd-step-wrap{display:none;min-height:320px;}
.wfd-step-wrap.active{display:block;}
.wfd-step-clear{
    margin-left:auto;
    background:transparent;
    border:1px solid #b08d2f;
    color:#d9b35a;
    font-weight:700;
    font-size:.8rem;
    padding:6px 10px;
    border-radius:8px;
    cursor:pointer;
}
.wfd-step-clear:hover{background:rgba(185,141,47,.1);}
.wfd-footer{
    position:sticky;bottom:0;left:0;right:0;
    padding:12px 18px;
    background:linear-gradient(180deg, rgba(11,21,41,.94) 0%, rgba(11,21,41,1) 100%);
    border-top:1px solid rgba(166,128,35,.35);
    display:flex;gap:10px;flex-wrap:wrap;justify-content:flex-end;
    box-shadow:0 -10px 30px rgba(0,0,0,.25);
}
.wfd-footer .wfd-calc-btn{margin-top:0;flex:1 1 160px;max-width:240px;}
.wfd-footer .wfd-secondary{background:#0f172a;border-color:rgba(166,128,35,.6);color:#d9b35a;}
.wfd-sec{
    margin-bottom:28px;
    padding-bottom:24px;
    border-bottom:1px solid rgba(166,128,35,.25);
    background:rgba(255,255,255,.02);
    border-radius:12px;
    padding:20px;
}
.wfd-sec:last-child{border-bottom:none;margin-bottom:0;}
.wfd-sec-title{color:#d9b35a;font-weight:900;font-size:1.1rem;margin:0 0 16px;letter-spacing:.4px;}
.wfd-lbl{display:block;font-weight:650;font-size:.82rem;color:#e2e8f0;margin:12px 0 3px;}
        .wfd-inp{
    width:100%;padding:8px 11px;
    border:1.5px solid rgba(217,179,90,.7);border-radius:8px;
    font-size:.92rem;font-weight:700;color:#d9b35a;
    background:#0f172a;box-sizing:border-box;
    transition:border-color .15s, box-shadow .15s;
}
.wfd-inp::placeholder{color:#94a3b8;}
.wfd-inp[readonly]{color:#d9b35a;}
.wfd-inp:focus{outline:none;border-color:#d9b35a;background:#111e3a;box-shadow:0 0 0 2px rgba(217,179,90,.22);}
.wfd-inp[readonly]{background:#0d1a30;color:#94a3b8;cursor:default;}
.wfd-inp.wfd-good{border-color:#16a34a;color:#4ade80;}
.wfd-inp.wfd-bad{border-color:#dc2626;color:#f87171;}
.wfd-row{display:flex;gap:14px;flex-wrap:wrap;}
.wfd-col{flex:1;min-width:130px;}
.wfd-half{flex:0 0 calc(50% - 7px);min-width:130px;}
.wfd-bkt-grid{display:flex;gap:14px;flex-wrap:wrap;margin-top:8px;}
.wfd-bkt{flex:1;min-width:230px;background:#0f172a;border:1.5px solid rgba(166,128,35,.45);border-radius:14px;padding:16px 14px;box-shadow:0 8px 24px rgba(0,0,0,.22);}
.wfd-bkt-title{font-weight:800;font-size:.97rem;color:#e2e8f0;margin:0 0 2px;}
.wfd-bkt-sub{font-size:.73rem;color:#cbd5e1;margin:0 0 8px;line-height:1.4;}
.wfd-tog-wrap{display:flex;align-items:center;gap:10px;margin-top:12px;}
.wfd-tog{position:relative;width:38px;height:20px;display:inline-block;flex-shrink:0;}
.wfd-tog input{opacity:0;width:0;height:0;}
        .wfd-tog-sl{position:absolute;inset:0;background:#1f2937;border-radius:99px;cursor:pointer;transition:background .2s;border:1px solid rgba(217,179,90,.5);}
        .wfd-tog input:checked + .wfd-tog-sl{background:#d9b35a;}
.wfd-tog-sl:before{content:'';position:absolute;width:14px;height:14px;background:#fff;border-radius:50%;left:3px;top:3px;transition:transform .2s;box-shadow:0 1px 4px rgba(0,0,0,.2);}
.wfd-tog input:checked + .wfd-tog-sl:before{transform:translateX(18px);}
.wfd-tog-lbl{font-size:.8rem;font-weight:600;color:#475569;}
.wfd-alloc-row{display:flex;align-items:center;gap:10px;margin-top:10px;flex-wrap:wrap;}
.wfd-alloc-good{color:#16a34a;font-weight:800;}
.wfd-alloc-bad{color:#dc2626;font-weight:800;}
        .wfd-bkt-vis{display:flex;gap:10px;align-items:flex-end;height:110px;margin:12px 0 0;justify-content:center;color:#e5e7eb;}
.wfd-bkt-bar-wrap{flex:1;display:flex;flex-direction:column;align-items:center;gap:4px;height:100%;justify-content:flex-end;}
.wfd-bkt-bar{width:100%;border-radius:6px 6px 0 0;min-height:3px;transition:height .3s ease;}
        .wfd-bkt-bar-lbl{font-size:.7rem;font-weight:700;text-align:center;color:#e5e7eb;line-height:1.3;}
.wfd-calc-btn{
    display:block;width:100%;padding:13px;
    background:linear-gradient(135deg,#0b1529 0%,#1e3a5f 100%);
    color:#d9b35a;border:1.5px solid #a68023;
    border-radius:12px;font-weight:800;font-size:1rem;
    cursor:pointer;letter-spacing:.4px;
    box-shadow:0 6px 20px rgba(166,128,35,.18);
    transition:background .15s;margin-top:6px;
}
.wfd-calc-btn:hover{background:linear-gradient(135deg,#162540 0%,#264a75 100%);}
.wfd-calc-btn:disabled{background:#e2e8f0;color:#94a3b8;border-color:#cbd5e1;box-shadow:none;cursor:not-allowed;}
.wfd-res-grid{display:flex;gap:12px;flex-wrap:wrap;margin-bottom:20px;}
.wfd-res-card{flex:1;min-width:140px;background:linear-gradient(145deg,#0f172a 0%,#111f38 100%);border:1px solid rgba(166,128,35,.5);border-radius:11px;padding:12px 14px;box-shadow:0 10px 30px rgba(0,0,0,.18);}
.wfd-res-lbl{font-size:.7rem;font-weight:700;color:#cbd5e1;margin:0 0 3px;text-transform:uppercase;letter-spacing:.5px;}
.wfd-res-val{font-size:1.08rem;font-weight:900;color:#f8fafc;margin:0;}
.wfd-res-val.green{color:#4ade80;}
.wfd-res-val.gold{color:#d9b35a;}
.wfd-res-val.red{color:#f87171;}
.wfd-return-pos{color:#22c55e;font-weight:800;}
.wfd-return-flat{color:#94a3b8;font-weight:700;}
.wfd-return-neg{color:#ef4444;font-weight:800;}
.wfd-badge{
    display:inline-flex;
    align-items:center;
    justify-content:center;
    gap:6px;
    padding:5px 12px;
    border-radius:999px;
    font-size:.85rem;
    font-weight:800;
    letter-spacing:.3px;
    line-height:1.1;
    white-space:nowrap;
    border:1.25px solid rgba(217,179,90,.5);
    box-shadow:0 4px 12px rgba(0,0,0,.12);
}
.wfd-hlthy{background:#dcfce7;color:#15803d;}
.wfd-tight{background:#fef9c3;color:#a16207;}
.wfd-risk{
    background:rgba(239,68,68,.12);
    color:#f87171;
    border-color:rgba(248,113,113,.45);
}
.wfd-warn-box{background:#fff7ed;border:1px solid #fdba74;border-left:4px solid #f97316;border-radius:8px;padding:9px 13px;font-size:.82rem;color:#7c2d12;font-weight:600;margin-top:8px;}
.wfd-info-box{background:#eff6ff;border:1px solid #93c5fd;border-left:4px solid #3b82f6;border-radius:8px;padding:9px 13px;font-size:.82rem;color:#1e3a5f;font-weight:600;margin-top:8px;}
.wfd-chart-wrap{width:100%;height:240px;margin-top:6px;}
.wfd-chart-wrap canvas{width:100%!important;height:240px!important;}
        .wfd-priority-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:10px;margin-top:10px;}
        .wfd-pri-label{font-size:.8rem;font-weight:700;color:#334155;margin-bottom:4px;}
.wfd-mini-note{font-size:.75rem;color:#e2e8f0;font-weight:600;margin-top:6px;}
.wfd-inline-note{font-size:.76rem;color:#d9b35a;font-weight:700;margin-top:4px;}
        /* Summary strip */
        .wfd-summary{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:12px;background:#0b1529;border:1px solid rgba(166,128,35,.55);border-radius:12px;padding:12px 14px;box-shadow:0 10px 26px rgba(0,0,0,.2);}
        .wfd-sum-card{background:rgba(255,255,255,.04);border:1px solid rgba(166,128,35,.45);border-radius:10px;padding:10px 12px;min-height:78px;display:flex;flex-direction:column;justify-content:center;}
        .wfd-sum-label{margin:0;color:#e5e7eb;font-size:.78rem;font-weight:700;letter-spacing:.4px;}
        .wfd-sum-value{margin:2px 0 0;color:#d9b35a;font-size:1.15rem;font-weight:900;}
        .wfd-sum-good{color:#4ade80 !important;}
        .wfd-sum-warn{color:#fbbf24 !important;}
        .wfd-sum-bad{color:#f87171 !important;}
        /* Down-market state */
        .wfd-dm-badge{padding:5px 10px;border-radius:999px;font-size:.72rem;font-weight:800;border:1px solid rgba(217,179,90,.35);background:#ecfdf3;color:#166534;}
        .wfd-dm-badge.off{background:#fef2f2;color:#b91c1c;border-color:#fecaca;}
        .wfd-bkt.wfd-dm-off{opacity:.8;border-style:dashed;}
        /* Emergency reserve card */
        .wfd-em-card{display:flex;align-items:center;gap:12px;flex-wrap:wrap;background:#0b1529;border:1px solid rgba(166,128,35,.55);border-radius:12px;padding:10px 12px;box-shadow:0 6px 18px rgba(0,0,0,.18);}
.wfd-em-card .wfd-res-val{color:#eaf2ff;}
.wfd-em-card .wfd-sum-value{color:#d9b35a;}
/* Inputs de-emphasis */
.wfd-sec input.wfd-inp, .wfd-sec select.wfd-inp{background:#0f172a;border-color:rgba(217,179,90,.55);color:#f8fafc;}
.wfd-sec input.wfd-inp:focus, .wfd-sec select.wfd-inp:focus{background:#111e3a;border-color:#d9b35a;}
.wfd-acc{border:1px solid rgba(217,179,90,.35);border-radius:12px;overflow:hidden;background:rgba(255,255,255,.02);}
.wfd-acc-btn{width:100%;text-align:left;padding:12px 14px;border:none;background:linear-gradient(135deg,#0f172a 0%,#111f2f 100%);color:#e2e8f0;font-weight:800;font-size:.9rem;display:flex;align-items:center;justify-content:space-between;cursor:pointer;}
.wfd-acc-btn:after{content:'▾';font-size:.9rem;color:#d9b35a;}
.wfd-acc-body{padding:12px 14px;display:block;}
.wfd-acc.collapsed .wfd-acc-body{display:none;}
.wfd-acc.collapsed .wfd-acc-btn:after{content:'▸';}
.wfd-step-wrap{display:none;min-height:320px;}
.wfd-step-wrap.active{display:block;}
/* Dark gold border standardization (scoped to planner) */
#wfDist_panel .wfd-inp,
#wfDist_panel .wfd-bkt,
#wfDist_panel .wfd-res-card,
#wfDist_panel .wfd-bkt-tile,
#wfDist_panel .wfd-acc,
#wfDist_panel .wfd-acc-body,
#wfDist_panel .wfd-sec,
#wfDist_panel .wfd-alloc-row,
#wfDist_panel .wfd-warn-box,
#wfDist_panel .wfd-info-box,
#wfDist_panel #wfd_tipsWrap > div,
#wfDist_panel #wfd_chartWrapAcc,
#wfDist_panel #wfd_sourceBreak,
#wfDist_panel #wfd_bktDrill_panel,
#wfDist_panel #wfd_warnWrap,
#wfDist_panel .wfd-bkt-bar-wrap{
    border-color:#b08d2f !important;
}
#wfDist_panel .wfd-step-chip,
#wfDist_panel .wfd-footer,
#wfDist_panel .wfd-acc-btn{
    border-color:#b08d2f !important;
}
@media(max-width:640px){
    #wfDist_panel{border-radius:16px;}
    .wfd-hdr{padding:18px 18px 16px;border-radius:16px 16px 0 0;}
    .wfd-body{padding:18px;}
    .wfd-bkt-grid,.wfd-res-grid{flex-direction:column;}
}
</style>

<div id="wfDist_panel">
  <!-- HEADER -->
    <div class="wfd-hdr">
      <button id="wfd_close" type="button" aria-label="Close"
        style="position:absolute;top:14px;right:14px;background:transparent;border:1.5px solid rgba(166,128,35,.5);color:#d9b35a;font-size:1.2rem;font-weight:900;width:34px;height:34px;border-radius:50%;cursor:pointer;display:flex;align-items:center;justify-content:center;z-index:2;">×</button>
      <h2 style="color:#d9b35a;font-weight:900;font-size:1.75rem;margin:0 0 4px;">Distribution Planner</h2>
      <p style="color:#94a3b8;margin:0;font-size:.88rem;">Retirement income strategy — coming down the mountain</p>
      <p style="color:#64748b;margin:5px 0 0;font-size:.76rem;">Auto-populated from your Wealth Forecast final projected balance.</p>
      <div id="dpClientSearchRow" style="display:flex;gap:8px;align-items:center;flex-wrap:wrap;margin-top:10px;">
        <input id="dpClientSearch" class="form-control form-control-sm" style="width:220px;" placeholder="Search client" />
        <button id="dpClientSearchBtn" class="btn btn-ghost btn-sm" type="button">Search</button>
        <span id="dpPlanStatus" class="text-muted small">No client selected.</span>
      </div>
      <div id="dpClientResults" class="list-group" style="display:none;margin-top:8px;"></div>
      <div class="wfd-steps" id="wfd_stepsNav">
        <div class="wfd-step-chip active" data-step="1"><span class="step-num">1</span> Foundation</div>
        <div class="wfd-step-chip" data-step="2"><span class="step-num">2</span> Buckets</div>
      <div class="wfd-step-chip" data-step="3"><span class="step-num">3</span> Strategy</div>
      <div class="wfd-step-chip" data-step="4"><span class="step-num">4</span> Results</div>
    </div>
  </div>

  <!-- BODY -->
  <div class="wfd-body">
    <div id="wfd_block" class="wfd-warn-box" style="display:none;margin-bottom:12px;"></div>

    <!-- STEP 1: Foundation -->
    <div class="wfd-step-wrap active" data-step="1">
      <div id="wfd_block_top" class="wfd-warn-box" style="display:none;margin-bottom:12px;"></div>
    <div id="wfd_noBaseWarn" class="wfd-warn-box" style="display:none;margin-bottom:16px;">
      ⚠️ Wealth Forecast has no valid result yet. Complete the Wealth Forecast inputs above first, or enable <strong>Manual Override</strong> below to enter a base manually.
    </div>

    <!-- No-base warning -->
    <!-- SECTION 1: Retirement Foundation -->
    <div class="wfd-sec">
      <div style="display:flex;align-items:center;gap:10px;">
        <p class="wfd-sec-title" style="margin:0;">1 — Retirement Foundation</p>
        <button type="button" class="wfd-step-clear" id="wfd_clearStep1">Clear Step</button>
      </div>
      <div class="wfd-row">
        <div class="wfd-col">
          <label class="wfd-lbl" for="wfd_base">Retirement Base (from Wealth Forecast) <span style="color:#94a3b8;font-weight:400;font-size:.75rem;">read-only</span></label>
          <input id="wfd_base" class="wfd-inp" type="text" readonly placeholder="Run Wealth Forecast above" />
        </div>
        <div class="wfd-col" style="display:flex;flex-direction:column;justify-content:flex-end;padding-bottom:2px;">
          <div class="wfd-tog-wrap" style="margin-top:20px;">
            <label class="wfd-tog"><input type="checkbox" id="wfd_manualOverride" /><span class="wfd-tog-sl"></span></label>
            <span class="wfd-tog-lbl" style="font-size:.82rem;">Manual Override (what-if)</span>
          </div>
        </div>
      </div>
      <div class="wfd-row">
        <div class="wfd-half">
          <label class="wfd-lbl" for="wfd_retAge">Retirement Age</label>
          <input id="wfd_retAge" class="wfd-inp" type="number" min="40" max="90" placeholder="65" />
        </div>
        <div class="wfd-half">
          <label class="wfd-lbl" for="wfd_endAge">Plan End Age / Life Expectancy</label>
          <input id="wfd_endAge" class="wfd-inp" type="number" min="41" max="120" placeholder="90" />
        </div>
      </div>
      <div class="wfd-row">
        <div class="wfd-half">
          <label class="wfd-lbl" for="wfd_yrsInDist">Years in Distribution <span style="color:#94a3b8;font-weight:400;font-size:.75rem;">auto-calc</span></label>
          <input id="wfd_yrsInDist" class="wfd-inp" type="text" readonly placeholder="—" />
        </div>
        <div class="wfd-half">
          <label class="wfd-lbl" for="wfd_emergency">Emergency Savings Reserve ($)</label>
          <input id="wfd_emergency" class="wfd-inp" type="text" placeholder="0" />
        </div>
      </div>
      <div class="wfd-row">
        <div class="wfd-col">
          <label class="wfd-lbl" for="wfd_desiredIncome">Desired Annual Retirement Income ($, after-tax target)</label>
          <input id="wfd_desiredIncome" class="wfd-inp" type="text" placeholder="80,000" />
        </div>
        <div class="wfd-col">
          <label class="wfd-lbl" for="wfd_guaranteedIncome">Other Guaranteed Income ($, after-tax) <span style="color:#94a3b8;font-weight:400;font-size:.72rem;">Social Security, pension, rental</span></label>
          <input id="wfd_guaranteedIncome" class="wfd-inp" type="text" placeholder="20,000" />
        </div>
        <div class="wfd-col">
          <label class="wfd-lbl" for="wfd_incomeGap">Net Income Gap to Fund From Assets <span style="color:#94a3b8;font-weight:400;font-size:.72rem;">auto-calc</span></label>
          <input id="wfd_incomeGap" class="wfd-inp" type="text" readonly placeholder="$0" />
        </div>
      </div>
    </div><!-- end foundation -->

    </div><!-- end step 1 -->

    <!-- STEP 2: Three Bucket Allocation -->
    <div class="wfd-step-wrap" data-step="2">
      <div class="wfd-sec">
      <div style="display:flex;align-items:center;gap:10px;">
        <p class="wfd-sec-title" style="margin:0;">2 — Three Bucket Allocation</p>
        <button type="button" class="wfd-step-clear" id="wfd_clearStep2">Clear Step</button>
      </div>
      <p style="font-size:.8rem;color:#64748b;margin:0 0 10px;">Allocations must total exactly 100%. Dollar amounts are auto-calculated from the Retirement Base.</p>

      <div class="wfd-alloc-row">
        <span style="font-size:.85rem;font-weight:600;color:#475569;">Total Allocated:</span>
        <span id="wfd_allocTotal" class="wfd-alloc-bad">0%</span>
        <span id="wfd_allocStatus" style="font-size:.78rem;font-weight:600;color:#dc2626;">— must equal 100%</span>
      </div>

      <!-- Allocation bar visual -->
      <div class="wfd-bkt-vis" id="wfd_allocVis">
        <div class="wfd-bkt-bar-wrap">
          <div id="wfd_invBar" class="wfd-bkt-bar" style="height:3px;background:#3b82f6;"></div>
          <div class="wfd-bkt-bar-lbl">Investments</div>
        </div>
        <div class="wfd-bkt-bar-wrap">
          <div id="wfd_liBar" class="wfd-bkt-bar" style="height:3px;background:#a68023;"></div>
          <div class="wfd-bkt-bar-lbl">Life Ins</div>
        </div>
        <div class="wfd-bkt-bar-wrap">
          <div id="wfd_annBar" class="wfd-bkt-bar" style="height:3px;background:#16a34a;"></div>
          <div class="wfd-bkt-bar-lbl">Annuities</div>
        </div>
      </div>

      <div class="wfd-bkt-grid">

        <!-- A: Investments -->
        <div id="wfd_invCard" class="wfd-bkt" style="border-color:rgba(59,130,246,.4);">
          <p class="wfd-bkt-title" style="color:#1d4ed8;">A — Investments</p>
          <p class="wfd-bkt-sub">Growth Engine — Stocks, bonds, ETFs, mutual funds, brokerage, retirement accounts</p>
          <div class="wfd-tog-wrap" style="margin-top:4px;margin-bottom:6px;">
            <span id="wfd_invDmBadge" class="wfd-dm-badge">Down-Market: Off</span>
          </div>
          <label class="wfd-lbl" for="wfd_invAlloc">Allocation %</label>
          <input id="wfd_invAlloc" class="wfd-inp" type="number" min="0" max="100" step="1" placeholder="60" />
          <label class="wfd-lbl" for="wfd_invAmt">Starting Dollar Amount</label>
          <input id="wfd_invAmt" class="wfd-inp" type="text" readonly placeholder="auto-calc" />
          <label class="wfd-lbl" for="wfd_invReturn">Expected Annual Return %</label>
          <input id="wfd_invReturn" class="wfd-inp" type="number" step="0.1" placeholder="7.0" />
          <label class="wfd-lbl" for="wfd_invTax">Tax Rate %</label>
          <input id="wfd_invTax" class="wfd-inp" type="number" step="0.1" placeholder="22" />
          <div class="wfd-tog-wrap">
            <label class="wfd-tog"><input type="checkbox" id="wfd_invDownMkt" /><span class="wfd-tog-sl"></span></label>
            <span class="wfd-tog-lbl">Use in Down Market?</span>
          </div>
        </div>

        <!-- B: Life Insurance -->
        <div id="wfd_liCard" class="wfd-bkt" style="border-color:rgba(166,128,35,.45);">
          <p class="wfd-bkt-title" style="color:#a68023;">B — Life Insurance / Equivalent</p>
          <p class="wfd-bkt-sub">Stability Buffer — Cash value life insurance, overfunded permanent insurance, protected strategies</p>
          <div class="wfd-tog-wrap" style="margin-top:4px;margin-bottom:6px;">
            <span id="wfd_liDmBadge" class="wfd-dm-badge">Down-Market: On</span>
          </div>
          <label class="wfd-lbl" for="wfd_liType">Policy Type</label>
          <select id="wfd_liType" class="wfd-inp" style="cursor:pointer;">
            <option value="whole">Whole Life</option>
            <option value="iul">Indexed UL</option>
            <option value="vul">Variable UL</option>
            <option value="legacy_rpu">Legacy / Reduced Paid-Up</option>
          </select>
          <label class="wfd-lbl" for="wfd_liAccess">Access Method</label>
          <select id="wfd_liAccess" class="wfd-inp" style="cursor:pointer;">
            <option value="withdrawal">Withdrawals</option>
            <option value="loan">Policy Loans</option>
            <option value="none">No Distributions</option>
          </select>
          <label class="wfd-lbl" for="wfd_liAlloc">Allocation %</label>
          <input id="wfd_liAlloc" class="wfd-inp" type="number" min="0" max="100" step="1" placeholder="20" />
          <label class="wfd-lbl" for="wfd_liDeath">Death Benefit</label>
          <input id="wfd_liDeath" class="wfd-inp" type="text" placeholder="e.g., 500,000" />
          <label class="wfd-lbl" for="wfd_liAmt">Whole Life Cash Value</label>
          <input id="wfd_liAmt" class="wfd-inp" type="text" readonly placeholder="auto-calc from allocation" />
          <label class="wfd-lbl" for="wfd_liGrowth">Growth / Credited Rate %</label>
          <input id="wfd_liGrowth" class="wfd-inp" type="number" step="0.1" placeholder="5.0" />
          <label class="wfd-lbl" for="wfd_liTax">Tax Rate %</label>
          <input id="wfd_liTax" class="wfd-inp" type="number" step="0.1" placeholder="0" />
          <label class="wfd-lbl" for="wfd_liEfficiency">Access / Efficiency Factor % <span style="color:#94a3b8;font-weight:400;font-size:.72rem;">optional, default 100</span></label>
          <input id="wfd_liEfficiency" class="wfd-inp" type="number" step="0.1" placeholder="100" />
          <div class="wfd-tog-wrap">
            <label class="wfd-tog"><input type="checkbox" id="wfd_liDownMkt" checked /><span class="wfd-tog-sl"></span></label>
            <span class="wfd-tog-lbl">Use in Down Market?</span>
          </div>
        </div>

        <!-- C: Annuities -->
        <div id="wfd_annCard" class="wfd-bkt" style="border-color:rgba(22,163,74,.4);">
          <p class="wfd-bkt-title" style="color:#15803d;">C — Annuities</p>
          <p class="wfd-bkt-sub">Income Floor — Protected income / accumulation hybrid</p>
          <div class="wfd-tog-wrap" style="margin-top:4px;margin-bottom:6px;">
            <span id="wfd_annDmBadge" class="wfd-dm-badge">Down-Market: On</span>
          </div>
          <label class="wfd-lbl" for="wfd_annDesign">Annuity Design</label>
          <select id="wfd_annDesign" class="wfd-inp" style="cursor:pointer;">
            <option value="fixed">Fixed Annuity</option>
            <option value="fixedIndexed">Fixed Indexed Annuity</option>
            <option value="variable">Variable Annuity</option>
          </select>
          <label class="wfd-lbl" for="wfd_annAlloc">Allocation %</label>
          <input id="wfd_annAlloc" class="wfd-inp" type="number" min="0" max="100" step="1" placeholder="20" />
          <label class="wfd-lbl" for="wfd_annDeath">Annuity Death Benefit (optional)</label>
          <input id="wfd_annDeath" class="wfd-inp" type="text" placeholder="e.g., 250,000" />
          <label class="wfd-lbl" for="wfd_annAmt">Starting Annuity Value</label>
          <input id="wfd_annAmt" class="wfd-inp" type="text" readonly placeholder="auto-calc from allocation" />
          <!-- Removed legacy fixed/variable toggle; dropdown is source of truth -->
          <div class="wfd-tog-wrap" style="margin-top:4px;">
            <label class="wfd-tog"><input type="checkbox" id="wfd_annIncomeRider" /><span class="wfd-tog-sl"></span></label>
            <span class="wfd-tog-lbl">Income Rider</span>
          </div>
          <div id="wfd_annRollupWrap" style="display:none;">
            <label class="wfd-lbl" for="wfd_annRollup">Income Rider Rollup Rate (%)</label>
            <input id="wfd_annRollup" class="wfd-inp" type="number" step="0.1" placeholder="5.0" value="5.0" />
          </div>
          <div class="wfd-tog-wrap" style="margin-top:4px;">
            <label class="wfd-tog"><input type="checkbox" id="wfd_annDbRider" /><span class="wfd-tog-sl"></span></label>
            <span class="wfd-tog-lbl">Death Benefit Rider</span>
          </div>
          <label class="wfd-lbl" for="wfd_annReturn">Credited / Expected Return %</label>
          <input id="wfd_annReturn" class="wfd-inp" type="number" step="0.1" placeholder="4.0" />
          <label class="wfd-lbl" for="wfd_annTax">Tax Rate %</label>
          <input id="wfd_annTax" class="wfd-inp" type="number" step="0.1" placeholder="22" />
          <div class="wfd-tog-wrap">
            <label class="wfd-tog"><input type="checkbox" id="wfd_annDownMkt" checked /><span class="wfd-tog-sl"></span></label>
            <span class="wfd-tog-lbl">Use in Down Market?</span>
          </div>
        </div>

      </div>
      </div><!-- end sec -->
    </div><!-- end buckets -->

    <!-- STEP 3: Strategy Controls -->
    <div class="wfd-step-wrap" data-step="3">
      <div class="wfd-sec">
      <div style="display:flex;align-items:center;gap:10px;">
        <p class="wfd-sec-title" style="margin:0;">3 — Strategy Controls</p>
        <button type="button" class="wfd-step-clear" id="wfd_clearStep3">Clear Step</button>
      </div>
      <div class="wfd-row" style="gap:10px;flex-wrap:wrap;">
        <button type="button" class="wfd-calc-btn" id="wfd_strat_prop" style="flex:1;max-width:220px;background:#0f172a;border-color:rgba(217,179,90,.6);">Proportional</button>
        <button type="button" class="wfd-calc-btn" id="wfd_strat_pri" style="flex:1;max-width:220px;background:#0f172a;border-color:rgba(217,179,90,.6);">Priority Order</button>
        <button type="button" class="wfd-calc-btn" id="wfd_strat_guard" style="flex:1;max-width:220px;background:#0f172a;border-color:rgba(217,179,90,.6);">Protect Investments</button>
      </div>
      <input type="hidden" id="wfd_strategy" value="proportional" />
      <div id="wfd_priorityRow" class="wfd-row" style="margin-top:12px;display:none;">
        <div class="wfd-col" style="flex:1 1 100%;">
          <label class="wfd-lbl" for="wfd_pri1" style="margin-bottom:6px;">Withdrawal Priority (1 = first)</label>
          <div class="wfd-priority-grid">
            <div>
              <div class="wfd-pri-label">1st</div>
              <select id="wfd_pri1" class="wfd-inp"></select>
            </div>
            <div>
              <div class="wfd-pri-label">2nd</div>
              <select id="wfd_pri2" class="wfd-inp"></select>
            </div>
            <div>
              <div class="wfd-pri-label">3rd</div>
              <select id="wfd_pri3" class="wfd-inp"></select>
            </div>
            <div>
              <div class="wfd-pri-label">4th</div>
              <select id="wfd_pri4" class="wfd-inp"></select>
            </div>
          </div>
        </div>
      </div>
      <div class="wfd-row" style="margin-top:14px;gap:14px;flex-wrap:wrap;align-items:flex-end;">
        <div class="wfd-col">
          <div class="wfd-tog-wrap" style="margin-top:0;">
            <label class="wfd-tog"><input type="checkbox" id="wfd_protectInvest" checked /><span class="wfd-tog-sl"></span></label>
            <span class="wfd-tog-lbl" style="font-size:.88rem;font-weight:700;color:#fff;">Protect Investments During Down Markets</span>
          </div>
          <p class="wfd-mini-note" style="margin-top:6px;">When on, investments pause in down years unless fallback is required.</p>
        </div>
        <div class="wfd-col">
          <label class="wfd-lbl" for="wfd_downThreshold" style="margin-top:0;">Down-Market Threshold % <span style="color:#94a3b8;font-weight:400;font-size:.72rem;">e.g. 0 = negative years only</span></label>
          <input id="wfd_downThreshold" class="wfd-inp" type="number" step="0.1" placeholder="0" value="0" />
        </div>
      </div>

      <div class="wfd-row" style="margin-top:10px;gap:14px;flex-wrap:wrap;align-items:flex-end;">
        <div class="wfd-col">
          <label class="wfd-lbl" for="wfd_gapSource">Gap Funding Source (Down Years)</label>
          <select id="wfd_gapSource" class="wfd-inp" style="cursor:pointer;">
            <option value="life">Life Insurance first</option>
            <option value="annuities">Annuities first</option>
            <option value="lifeThenAnnuities">Life then Annuities</option>
            <option value="annThenLife">Annuities then Life</option>
            <option value="split">Split Life + Annuities</option>
            <option value="custom">Use Custom Priority Order</option>
          </select>
        </div>
        <div class="wfd-col">
          <label class="wfd-lbl" for="wfd_scenarioMode">Market Scenario Mode</label>
          <select id="wfd_scenarioMode" class="wfd-inp" style="cursor:pointer;">
            <option value="fixed">Fixed return each year</option>
            <option value="random">Randomized yearly path</option>
            <option value="manual">Manual yearly returns</option>
          </select>
        </div>
      </div>

      <div class="wfd-row" style="margin-top:10px;gap:12px;flex-wrap:wrap;">
        <div class="wfd-col" style="flex:2 1 340px;">
          <label class="wfd-lbl" for="wfd_manualReturns" style="margin-top:0;">Manual / Scenario Returns (% per year, comma or line separated)</label>
          <textarea id="wfd_manualReturns" class="wfd-inp" style="height:86px;resize:vertical;" placeholder="7, 6.5, -12, 8, 5, ..."></textarea>
          <p class="wfd-mini-note" style="margin-top:4px;">Illustration only — randomized paths are not predictions or guarantees.</p>
        </div>
        <div class="wfd-col" style="flex:1 1 200px;display:flex;align-items:flex-end;">
          <button id="wfd_genScenario" class="wfd-calc-btn" type="button" style="margin-top:0;">Generate Market Scenario</button>
        </div>
      </div>
    </div><!-- end strat -->
    </div>

    <!-- STEP 4: RESULTS -->
    <div class="wfd-step-wrap" data-step="4" id="wfd_results">
      <div class="wfd-sec" style="border-bottom:none;margin-bottom:12px;padding-bottom:0;">
        <div style="display:flex;flex-wrap:wrap;gap:10px;align-items:center;margin-bottom:12px;">
          <button class="wfd-calc-btn" id="wfd_editFoundation" type="button" style="flex:1;min-width:140px;max-width:200px;background:#0f172a;border-color:rgba(217,179,90,.55);">Edit Foundation</button>
          <button class="wfd-calc-btn" id="wfd_editBuckets" type="button" style="flex:1;min-width:140px;max-width:200px;background:#0f172a;border-color:rgba(217,179,90,.55);">Edit Buckets</button>
          <button class="wfd-calc-btn" id="wfd_editStrategy" type="button" style="flex:1;min-width:140px;max-width:200px;background:#0f172a;border-color:rgba(217,179,90,.55);">Edit Strategy</button>
          <button class="wfd-calc-btn" id="wfd_recalcBtn" type="button" style="flex:1;min-width:140px;max-width:200px;">Recalculate</button>
        </div>
        <div style="display:flex;flex-wrap:wrap;gap:8px;margin-bottom:12px;">
          <button class="wfd-calc-btn" type="button" id="wfd_runBase" style="flex:1;min-width:150px;background:#0f172a;border-color:rgba(217,179,90,.5);">Run Base Case</button>
          <button class="wfd-calc-btn" type="button" id="wfd_runDown" style="flex:1;min-width:150px;background:#0f172a;border-color:rgba(217,179,90,.5);">Simulate Down Market</button>
          <button class="wfd-calc-btn" type="button" id="wfd_runScenario" style="flex:1;min-width:150px;background:#0f172a;border-color:rgba(217,179,90,.5);">Generate Market Scenario</button>
        </div>
        <div class="accordion" style="display:grid;gap:10px;">
          <div class="wfd-acc">
            <button class="wfd-acc-btn" data-target="wfd_summaryWrap">Summary</button>
            <div id="wfd_summaryWrap" class="wfd-acc-body">
              <div id="wfd_summary" class="wfd-summary" style="margin-bottom:12px;">
                    <div class="wfd-sum-card">
                      <p class="wfd-sum-label">After-Tax Annual Income</p>
                      <p id="wfd_sumIncome" class="wfd-sum-value">—</p>
                    </div>
                <div class="wfd-sum-card">
                  <p class="wfd-sum-label">Plan Health</p>
                  <p id="wfd_sumHealth" class="wfd-sum-value">—</p>
                </div>
                <div class="wfd-sum-card">
                  <p class="wfd-sum-label">Longevity</p>
                  <p id="wfd_sumLongevity" class="wfd-sum-value">—</p>
                </div>
                <div class="wfd-sum-card">
                  <p class="wfd-sum-label">Income Sufficiency</p>
                  <p id="wfd_sumIncomeSuff" class="wfd-sum-value">—</p>
                </div>
              </div>
              <div style="display:flex;align-items:center;gap:12px;margin-bottom:14px;flex-wrap:wrap;">
                <span style="font-size:.88rem;font-weight:700;color:#475569;">Plan Health:</span>
                <span id="wfd_healthBadge" class="wfd-badge">—</span>
              </div>
            </div>
          </div>
          <div class="wfd-acc">
            <button class="wfd-acc-btn" data-target="wfd_fundingWrap">Funding Breakdown</button>
            <div id="wfd_fundingWrap" class="wfd-acc-body">
              <div class="wfd-res-grid" id="wfd_resGrid"></div>
              <div id="wfd_sourceBreak" class="wfd-mini-note" style="margin-top:6px;"></div>
              <div class="wfd-bkt-vis" id="wfd_wdrlVis" style="height:90px;margin:12px 0 10px;">
                <div class="wfd-bkt-bar-wrap">
                  <div id="wfd_emWBar" class="wfd-bkt-bar" style="background:#0f172a;height:3px;"></div>
                  <div id="wfd_emWLbl" class="wfd-bkt-bar-lbl">Emergency<br>$0</div>
                </div>
                <div class="wfd-bkt-bar-wrap">
                  <div id="wfd_invWBar" class="wfd-bkt-bar" style="background:#3b82f6;height:3px;"></div>
                  <div id="wfd_invWLbl" class="wfd-bkt-bar-lbl">Investments<br>$0</div>
                </div>
                <div class="wfd-bkt-bar-wrap">
                  <div id="wfd_liWBar" class="wfd-bkt-bar" style="background:#a68023;height:3px;"></div>
                  <div id="wfd_liWLbl" class="wfd-bkt-bar-lbl">Life Ins<br>$0</div>
                </div>
                <div class="wfd-bkt-bar-wrap">
                  <div id="wfd_annWBar" class="wfd-bkt-bar" style="background:#16a34a;height:3px;"></div>
                  <div id="wfd_annWLbl" class="wfd-bkt-bar-lbl">Annuities<br>$0</div>
                </div>
              </div>
              <div id="wfd_emCard" class="wfd-em-card" style="margin-bottom:10px;">
                <div>
                  <p class="wfd-res-lbl" style="margin:0;">Emergency Reserve</p>
                  <p id="wfd_emNow" class="wfd-sum-value" style="font-size:1rem;margin:0;">—</p>
                </div>
                <div>
                  <p class="wfd-mini-note" style="margin:0;">Year 1 Used</p>
                  <p id="wfd_emUsed" class="wfd-res-val" style="margin:0;">—</p>
                </div>
                <div>
                  <p class="wfd-mini-note" style="margin:0;">Total Used (Plan)</p>
                  <p id="wfd_emTotal" class="wfd-res-val" style="margin:0;">—</p>
                </div>
                <div>
                  <p class="wfd-mini-note" style="margin:0;">Remaining</p>
                  <p id="wfd_emRemain" class="wfd-res-val" style="margin:0;">—</p>
                </div>
                <div>
                  <p class="wfd-mini-note" style="margin:0;">Depletion</p>
                  <p id="wfd_emDeplete" class="wfd-res-val" style="margin:0;">—</p>
                </div>
                <div id="wfd_emStatus" class="wfd-badge" style="margin-left:auto;">—</div>
              </div>
            </div>
          </div>
          <div class="wfd-acc">
            <button class="wfd-acc-btn" data-target="wfd_chartWrapAcc">Longevity Chart</button>
            <div id="wfd_chartWrapAcc" class="wfd-acc-body">
              <p style="font-weight:700;color:#334155;font-size:.86rem;margin:0 0 6px;">Asset Longevity Over Distribution Period</p>
              <div class="wfd-chart-wrap"><canvas id="wfd_chart"></canvas></div>
            </div>
          </div>
          <div class="wfd-acc collapsed">
            <button class="wfd-acc-btn" data-target="wfd_tipsWrap">Year-by-Year Audit</button>
            <div id="wfd_tipsWrap" class="wfd-acc-body">
              <div id="wfd_bktTiles" style="display:none;margin-bottom:14px;"></div>
              <div id="wfd_tips" style="margin-top:0;"></div>
            </div>
          </div>
          <div class="wfd-acc collapsed">
            <button class="wfd-acc-btn" data-target="wfd_warnWrap">Warnings / Stress Points</button>
            <div id="wfd_warnWrap" class="wfd-acc-body">
              <div id="wfd_warnArea"></div>
            </div>
          </div>
        </div>
      </div>
    </div><!-- end results -->

    <!-- HIDDEN legacy calc button -->
    <button id="wfd_calcBtn" type="button" style="display:none;">Calculate</button>

  </div><!-- end body -->

  <!-- STICKY FOOTER NAV -->
  <div class="wfd-footer">
    <button id="wfd_clearBtn" class="wfd-calc-btn wfd-secondary" type="button" style="max-width:120px;">Clear</button>
    <button id="wfd_prev" class="wfd-calc-btn wfd-secondary" type="button" style="max-width:160px;">Back</button>
    <button id="wfd_next" class="wfd-calc-btn" type="button" style="max-width:200px;">Continue</button>
    <button id="wfd_run" class="wfd-calc-btn" type="button" style="max-width:220px;">Run Plan</button>
  </div>
</div><!-- end panel -->`;

                // ========================
                // Wire up modal interactivity
                // ========================
                const gid = id => document.getElementById(id);
                let lastValidationErrors = [];

                let lastActiveEl = null;
                let focusTrapHandler = null;
                const focusableSelector = 'a[href], area[href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), button:not([disabled]), [tabindex="0"]';
                function trapFocus(modal){
                    const nodes = modal.querySelectorAll(focusableSelector);
                    if (!nodes.length) return;
                    let first = nodes[0], last = nodes[nodes.length -1];
                    focusTrapHandler = (e)=>{
                        if (e.key !== 'Tab') return;
                        if (e.shiftKey && document.activeElement === first){ e.preventDefault(); last.focus(); }
                        else if (!e.shiftKey && document.activeElement === last){ e.preventDefault(); first.focus(); }
                    };
                    modal.addEventListener('keydown', focusTrapHandler);
                    first.focus();
                }

                const closeDistModal = () => {
                    const modal = gid(DIST_OVR_ID);
                    modal.classList.remove('wfd-open');
                    document.body.style.overflow = '';
                    if (focusTrapHandler) modal.removeEventListener('keydown', focusTrapHandler);
                    if (lastActiveEl) lastActiveEl.focus();
                    distMeta.open = false; saveMeta();
                };
                gid('wfd_close').addEventListener('click', closeDistModal);
                const showDistModal = (stepToOpen='1') => {
                    const modal = gid(DIST_OVR_ID);
                    modal.classList.add('wfd-open');
                    document.body.style.overflow = 'hidden';
                    trapFocus(modal);
                    updateDMState();
                    document.getElementById('wfd_invAlloc').dispatchEvent(new Event('input'));
                    document.getElementById('wfd_retAge').dispatchEvent(new Event('input'));
                    document.getElementById('wfd_desiredIncome').dispatchEvent(new Event('input'));
                    const reopenStep = stepToOpen || '1';
                    setStep(reopenStep);
                    if (reopenStep === '4') hydrateResultsFromMeta();
                    distMeta.open = true; saveMeta();
                };
                // Step navigation + meta
                const steps = ['1','2','3','4'];
                let activeStep = '1';
                var distMeta = { hasValidResults:false, lastStep:'1', stale:false, result:null };
                function syncStepVisibility() {
                    document.querySelectorAll('.wfd-step-wrap').forEach(w=>{
                        const isActive = w.dataset.step === activeStep;
                        w.classList.toggle('active', isActive);
                        w.style.display = isActive ? 'block' : 'none';
                    });
                }
                function setStep(step, { skipHydrate = false } = {}){
                    activeStep = step;
                    document.querySelectorAll('.wfd-step-chip').forEach(chip=>{
                        chip.classList.toggle('active', chip.dataset.step === step);
                    });
                    syncStepVisibility();
                    distMeta.lastStep = step; saveMeta(); saveDistState();
                    gid('wfd_prev').style.visibility = step === '1' ? 'hidden' : 'visible';
                    const next = gid('wfd_next');
                    const run  = gid('wfd_run');
                    const nextLabels = { '1':'Next: Build Buckets', '2':'Next: Choose Strategy', '3':'View Results', '4':'View Results' };
                    if (next) {
                        next.textContent = nextLabels[step] || 'Continue';
                        next.style.display = step === '4' ? 'none' : 'inline-flex';
                    }
                    if (run) {
                        if (step === '3' || step === '4') {
                            run.style.display = 'inline-flex';
                            run.textContent = step === '4' ? 'Run Again' : 'Run Plan';
                        } else {
                            run.style.display = 'none';
                        }
                    }
                    if (step === '4' && !skipHydrate) {
                        hydrateResultsFromMeta();
                    }
                }
                document.querySelectorAll('.wfd-step-chip').forEach(chip=>{
                    chip.addEventListener('click', ()=>setStep(chip.dataset.step));
                });

                // Accordions
                document.querySelectorAll('.wfd-acc-btn').forEach(btn=>{
                    btn.addEventListener('click', ()=>{
                        const parent = btn.closest('.wfd-acc');
                        if (!parent) return;
                        parent.classList.toggle('collapsed');
                    });
                });

                // Parse float helper — strips $, %, commas
                function pf(str) {
                    const v = parseFloat(String(str || '').replace(/[$%]/g, '').replace(/,/g, ''));
                    return isNaN(v) ? 0 : v;
                }
                function fmtD(n) { return '$' + Math.round(n || 0).toLocaleString(); }
                function netFromGross(gross, taxRate){ return (gross || 0) * (1 - (taxRate || 0)); }

                // Persistence + defaults
                const plannerScoped = !!effectiveUserScope;
                const DIST_KEY = plannerScoped ? `DistributionPlanner:user:${effectiveUserScope}` : null;
                // UI inputs we manage (includes transient/derived values)
                const distInputIds = [
        'wfd_base','wfd_retAge','wfd_endAge','wfd_emergency','wfd_desiredIncome','wfd_guaranteedIncome','wfd_incomeGap','wfd_yrsInDist',
        'wfd_invAlloc','wfd_invReturn','wfd_invTax','wfd_invAmt',
        'wfd_liAlloc','wfd_liGrowth','wfd_liTax','wfd_liEfficiency','wfd_liDeath','wfd_liAmt',
        'wfd_annAlloc','wfd_annReturn','wfd_annTax','wfd_annDeath','wfd_annAmt','wfd_annRollup',
        'wfd_downThreshold','wfd_manualReturns'
                ];
                // Inputs that are allowed to persist to the server (derived fields excluded)
                const distPersistInputs = [
        'wfd_retAge','wfd_endAge','wfd_emergency','wfd_desiredIncome','wfd_guaranteedIncome',
        'wfd_invAlloc','wfd_invReturn','wfd_invTax',
        'wfd_liAlloc','wfd_liGrowth','wfd_liTax','wfd_liEfficiency','wfd_liDeath',
        'wfd_annAlloc','wfd_annReturn','wfd_annTax','wfd_annDeath','wfd_annRollup',
        'wfd_downThreshold','wfd_manualReturns'
                ];
                const distCheckIds = ['wfd_manualOverride','wfd_invDownMkt','wfd_liDownMkt','wfd_annDownMkt','wfd_annIncomeRider','wfd_annDbRider','wfd_protectInvest'];
                const distSelectIds = ['wfd_strategy','wfd_pri1','wfd_pri2','wfd_pri3','wfd_pri4','wfd_gapSource','wfd_scenarioMode','wfd_liType','wfd_liAccess','wfd_annDesign'];
                const DIST_META_KEY = plannerScoped ? `DistributionPlannerMeta:user:${effectiveUserScope}` : null;
                let dpPlanLoaded = false;

                function dpCollectInputs(){
                    const inputs = {};
                    distPersistInputs.forEach(id => { const el = gid(id); if (el) inputs[id] = el.value; });
                    // Manual override base is intentionally persisted only when enabled
                    if (gid('wfd_manualOverride')?.checked) {
                        const baseEl = gid('wfd_base');
                        if (baseEl) inputs['wfd_base'] = baseEl.value;
                    }
                    const checks = {};
                    distCheckIds.forEach(id => { const el = gid(id); if (el) checks[id] = !!el.checked; });
                    const selects = {};
                    distSelectIds.forEach(id => { const el = gid(id); if (el) selects[id] = el.value; });
                    return { inputs, checks, selects };
                }
                function dpPayload(){
                    const dist = dpCollectInputs();
                    dist.meta = { ...(dist.meta || {}), source:'finance' };
                    const payload = {
                        version: dpPlanVersion,
                        distribution: dist
                    };
                    if (Object.prototype.hasOwnProperty.call(dpPlanCache, 'wealthForecast')) {
                        payload.wealthForecast = dpPlanCache.wealthForecast;
                    }
                    return payload;
                }
                const stepFieldSets = {
                    step1: {
                        inputs: ['wfd_base','wfd_retAge','wfd_endAge','wfd_emergency','wfd_desiredIncome','wfd_guaranteedIncome','wfd_incomeGap'],
                        checks: ['wfd_manualOverride'],
                        selects: []
                    },
                    step2: {
                        inputs: ['wfd_invAlloc','wfd_invReturn','wfd_invTax','wfd_invAmt',
                                 'wfd_liAlloc','wfd_liGrowth','wfd_liTax','wfd_liEfficiency','wfd_liDeath','wfd_liAmt',
                                 'wfd_annAlloc','wfd_annReturn','wfd_annTax','wfd_annDeath','wfd_annAmt','wfd_annRollup'],
                        checks: ['wfd_invDownMkt','wfd_liDownMkt','wfd_annDownMkt','wfd_annIncomeRider','wfd_annDbRider'],
                        selects: ['wfd_liType','wfd_liAccess','wfd_annDesign']
                    },
                    step3: {
                        inputs: ['wfd_downThreshold','wfd_manualReturns'],
                        checks: ['wfd_protectInvest'],
                        selects: ['wfd_strategy','wfd_pri1','wfd_pri2','wfd_pri3','wfd_pri4','wfd_gapSource','wfd_scenarioMode']
                    }
                };
                let hydrating = false;
                function saveMeta(){ if (DIST_META_KEY) savePersistedState(DIST_META_KEY, distMeta); }
                async function loadMeta(){
                    if (!DIST_META_KEY) return;
                    const m = await loadPersistedState(DIST_META_KEY);
                    if (m && typeof m === 'object') distMeta = { hasValidResults:!!m.hasValidResults, lastStep:m.lastStep || '1', stale:!!m.stale, result:m.result || null };
                }

                // Market scenario helpers
                let wfdScenarioCache = [];
                let wfdScenarioMeta = { mode:'fixed', years:0 };
                function parseManualReturns(txt){
                    return (txt || '').split(/[\n,]+/).map(pf).filter(v => !isNaN(v));
                }
                function generateRandomReturns(years, meanPct){
                    const arr = [];
                    const mean = isFinite(meanPct) ? meanPct : 6;
                    for (let i=0; i<Math.max(years,1); i++){
                        const drift = (Math.random() * 12) - 6; // +/-6%
                        const shock = (Math.random() < 0.2) ? (Math.random()*-15 - 5) : 0; // occasional drawdown
                        arr.push(Math.max(-40, mean + drift + shock));
                    }
                    return arr;
                }
                function buildScenarioReturns(years, mode, baseReturnDec, manualTxt){
                    if (years <= 0) return [];
                    const basePct = (baseReturnDec || 0) * 100;
                    if (mode === 'manual'){
                        const vals = parseManualReturns(manualTxt);
                        if (vals.length === 0) return Array(years).fill(baseReturnDec);
                        while (vals.length < years) vals.push(vals[vals.length-1]);
                        return vals.slice(0, years).map(v => v / 100);
                    }
                    if (mode === 'random'){
                        if (wfdScenarioCache.length === years && wfdScenarioMeta.mode === 'random') {
                            return wfdScenarioCache.map(v => v / 100);
                        }
                        const gen = generateRandomReturns(years, basePct);
                        wfdScenarioCache = gen;
                        wfdScenarioMeta = { mode:'random', years };
                        const txtArea = document.getElementById('wfd_manualReturns');
                        if (txtArea) txtArea.value = gen.map(v=>v.toFixed(1)).join(', ');
                        saveDistState();
                        return gen.map(v => v / 100);
                    }
                    // fixed
                    return Array(years).fill(baseReturnDec);
                }

                const priorityOptions = [
                    { v:'emergency',  l:'Emergency Savings' },
                    { v:'investments',l:'Investments' },
                    { v:'life',       l:'Life Insurance / Equivalent' },
                    { v:'annuities',  l:'Annuities' }
                ];
                const defaultPriority = ['emergency','investments','life','annuities'];

                function populatePrioritySelects() {
                    ['wfd_pri1','wfd_pri2','wfd_pri3','wfd_pri4'].forEach(id => {
                        const sel = gid(id);
                        if (!sel || sel.options.length) return;
                        priorityOptions.forEach(opt => {
                            const o = document.createElement('option');
                            o.value = opt.v; o.textContent = opt.l;
                            sel.appendChild(o);
                        });
                    });
                }

                function normalizePriority(order) {
                    const filled = [];
                    order.forEach(o => { if (o && !filled.includes(o)) filled.push(o); });
                    defaultPriority.forEach(o => { if (!filled.includes(o)) filled.push(o); });
                    return filled.slice(0,4);
                }

                function setPriorityOrder(order){
                    const norm = normalizePriority(order || []);
                    ['wfd_pri1','wfd_pri2','wfd_pri3','wfd_pri4'].forEach((id, idx) => {
                        const sel = gid(id);
                        if (sel) sel.value = norm[idx];
                    });
                }

                function getPriorityOrder(){
                    return normalizePriority([
                        gid('wfd_pri1')?.value,
                        gid('wfd_pri2')?.value,
                        gid('wfd_pri3')?.value,
                        gid('wfd_pri4')?.value
                    ]);
                }

                function distState() {
                    const obj = { step1:{}, step2:{}, step3:{}, meta:{ lastStep: activeStep } };
                    const applyInputs = (ids, target) => ids.forEach(id => { const el = gid(id); if (el) target[id] = el.value; });
                    const applyChecks = (ids, target) => ids.forEach(id => { const el = gid(id); if (el) target[id] = !!el.checked; });
                    const applySelects = (ids, target) => ids.forEach(id => { const el = gid(id); if (el) target[id] = el.value; });
                    applyInputs(stepFieldSets.step1.inputs, obj.step1);
                    applyChecks(stepFieldSets.step1.checks, obj.step1);
                    applySelects(stepFieldSets.step1.selects, obj.step1);
                    applyInputs(stepFieldSets.step2.inputs, obj.step2);
                    applyChecks(stepFieldSets.step2.checks, obj.step2);
                    applySelects(stepFieldSets.step2.selects, obj.step2);
                    applyInputs(stepFieldSets.step3.inputs, obj.step3);
                    applyChecks(stepFieldSets.step3.checks, obj.step3);
                    applySelects(stepFieldSets.step3.selects, obj.step3);
                    return obj;
                }

                let saveDistTimer = null;
                function saveDistState() {
                    if (disableLocalForDP) { dpSaveDebounced(); return; }
                    if (!DIST_KEY) return;
                    savePersistedState(DIST_KEY, distState());
                    if (!hydrating && distMeta.hasValidResults) { distMeta.stale = true; saveMeta(); }
                }
                function saveDistStateDebounced(){
                    if (disableLocalForDP) { dpSaveDebounced(); return; }
                    if (!DIST_KEY) return;
                    if (saveDistTimer) clearTimeout(saveDistTimer);
                    saveDistTimer = setTimeout(saveDistState, 300);
                }
                function applyStepState(stepKey, data){
                    if (!data) return;
                    const setVals = (ids, source) => ids.forEach(id => { if (source[id] !== undefined && gid(id)) gid(id).value = source[id]; });
                    const setChecks = (ids, source) => ids.forEach(id => { if (source[id] !== undefined && gid(id)) gid(id).checked = !!source[id]; });
                    const setSelects = setVals;
                    setVals(stepFieldSets[stepKey].inputs, data);
                    setChecks(stepFieldSets[stepKey].checks, data);
                    setSelects(stepFieldSets[stepKey].selects, data);
                }
                async function loadDistState() {
                    if (!DIST_KEY) return;
                    const state = disableLocalForDP ? {} : await loadPersistedState(DIST_KEY);
                    const hasState = state && Object.keys(state).length > 0;
                    const mapLegacyDesign = (val) => {
                        if (!val) return null;
                        if (val === 'whole_withdrawal') return { wfd_liType:'whole', wfd_liAccess:'withdrawal' };
                        if (val === 'whole_loan')        return { wfd_liType:'whole', wfd_liAccess:'loan' };
                        if (val === 'iul')               return { wfd_liType:'iul', wfd_liAccess:'withdrawal' };
                        if (val === 'vul')               return { wfd_liType:'vul', wfd_liAccess:'withdrawal' };
                        if (val === 'legacy_rpu')        return { wfd_liType:'legacy_rpu', wfd_liAccess:'none' };
                        return null;
                    };
                    if (hasState && state.step1 && state.step2 && state.step3) {
                        const legacy = mapLegacyDesign(state.step2?.wfd_liDesign || state.wfd_liDesign);
                        if (legacy) { state.step2 = { ...state.step2, ...legacy }; }
                        applyStepState('step1', state.step1);
                        applyStepState('step2', state.step2);
                        applyStepState('step3', state.step3);
                        if (state.meta && state.meta.lastStep) distMeta.lastStep = state.meta.lastStep;
                    } else if (hasState) {
                        // backward compatibility with flat shape
                        const legacy = mapLegacyDesign(state.wfd_liDesign);
                        if (legacy) { Object.assign(state, legacy); }
                        distInputIds.forEach(id => { if (state[id] !== undefined && gid(id)) gid(id).value = state[id]; });
                        distCheckIds.forEach(id => { if (state[id] !== undefined && gid(id)) gid(id).checked = !!state[id]; });
                        distSelectIds.forEach(id => { if (state[id] !== undefined && gid(id)) gid(id).value = state[id]; });
                    } else {
                        // Apply defaults when no saved state exists
                        const invDm = gid('wfd_invDownMkt'); if (invDm) invDm.checked = false;
                        const liDm  = gid('wfd_liDownMkt');  if (liDm) liDm.checked = true;
                        const annDm = gid('wfd_annDownMkt'); if (annDm) annDm.checked = true;
                        const prot  = gid('wfd_protectInvest'); if (prot) prot.checked = true;
                    }
                    const stratEl = gid('wfd_strategy');
                    if (stratEl && stratEl.value === 'downmarket') stratEl.value = 'guardrail';
                    if (gid('wfd_gapSource') && !gid('wfd_gapSource').value) gid('wfd_gapSource').value = 'life';
                    if (gid('wfd_scenarioMode') && !gid('wfd_scenarioMode').value) gid('wfd_scenarioMode').value = 'fixed';
                    if (gid('wfd_downThreshold') && gid('wfd_downThreshold').value === '') gid('wfd_downThreshold').value = '0';
                    if (gid('wfd_liType') && !gid('wfd_liType').value) gid('wfd_liType').value = 'whole';
                    if (gid('wfd_liAccess') && !gid('wfd_liAccess').value) gid('wfd_liAccess').value = 'withdrawal';
                }

                // Integration from Wealth Forecast
                window.__wfUpdateDistributionDefaults = function(st){
                    if (!st) return;
                    const setIfEmpty = (id, val, fmt=true) => {
                        const el = gid(id);
                        if (!el || (el.value && el.value.trim() !== '')) return;
                        el.value = fmt ? Math.round(val || 0).toLocaleString() : val;
                    };
                    if (st.annualSpend > 0) {
                        setIfEmpty('wfd_desiredIncome', st.annualSpend);
                    }
                    if (st.taxPct > 0) {
                        setIfEmpty('wfd_invTax', st.taxPct, false);
                        setIfEmpty('wfd_annTax', st.taxPct, false);
                    }
                };

                // Sync retirement base from WF result
                function syncBase() {
                    const manualOn = gid('wfd_manualOverride').checked;
                    const baseInp = gid('wfd_base');
                    const warnEl = gid('wfd_noBaseWarn');
                    if (!manualOn) {
                        const bal = window.__wfFinalBalance;
                        if (bal && bal > 0) {
                            // WF has a live balance — use it
                            baseInp.value = Math.round(bal).toLocaleString();
                            baseInp.readOnly = true;
                            baseInp.classList.add('wfd-good'); baseInp.classList.remove('wfd-bad');
                            warnEl.style.display = 'none';
                        } else if (!baseInp.value || baseInp.value.trim() === '') {
                            // No WF balance AND field is already empty — show the warning but do not wipe a saved value
                            baseInp.readOnly = true;
                            baseInp.classList.add('wfd-bad'); baseInp.classList.remove('wfd-good');
                            warnEl.style.display = 'block';
                        } else {
                            // No WF balance but field has a persisted value — keep it, just lock it readonly
                            baseInp.readOnly = true;
                            baseInp.classList.remove('wfd-good', 'wfd-bad');
                            warnEl.style.display = 'none';
                        }
                    } else {
                        baseInp.readOnly = false;
                        baseInp.classList.remove('wfd-good', 'wfd-bad');
                        warnEl.style.display = 'none';
                    }
                    saveDistState();
                    updateBktAmounts();
                }

                // Called by calcWealthForecast whenever it recalculates
                window.__wfOnBalanceUpdate = function(bal) {
                    if (!gid('wfd_manualOverride').checked) syncBase();
                };

                gid('wfd_manualOverride').addEventListener('change', syncBase);
                gid('wfd_base').addEventListener('input', () => { updateBktAmounts(); dpSaveDebounced(); });

                // Auto-calc: years in distribution
                function updateYrs() {
                    const ret = pf(gid('wfd_retAge').value);
                    const end = pf(gid('wfd_endAge').value);
                    const el = gid('wfd_yrsInDist');
                    if (ret > 0 && end > 0 && end > ret) {
                        el.value = (end - ret).toFixed(0);
                        el.classList.add('wfd-good'); el.classList.remove('wfd-bad');
                    } else if (ret > 0 && end > 0) {
                        el.value = '';
                        el.classList.add('wfd-bad'); el.classList.remove('wfd-good');
                    } else {
                        el.value = '';
                        el.classList.remove('wfd-good', 'wfd-bad');
                    }
                    saveDistStateDebounced();
                }
                gid('wfd_retAge').addEventListener('input', updateYrs);
                gid('wfd_endAge').addEventListener('input', updateYrs);

                // Auto-calc: income gap
                function updateGap() {
                    const desired = pf(gid('wfd_desiredIncome').value);
                    const guar = pf(gid('wfd_guaranteedIncome').value);
                    const gap = Math.max(desired - guar, 0);
                    gid('wfd_incomeGap').value = fmtD(gap);
                    const el = gid('wfd_incomeGap');
                    if (gap === 0) { el.classList.add('wfd-good'); el.classList.remove('wfd-bad'); }
                    else if (desired > 0 && gap > desired * 0.85) { el.classList.add('wfd-bad'); el.classList.remove('wfd-good'); }
                    else { el.classList.remove('wfd-good', 'wfd-bad'); }
                    saveDistStateDebounced();
                }
                gid('wfd_desiredIncome').addEventListener('input', updateGap);
                gid('wfd_guaranteedIncome').addEventListener('input', updateGap);

                // Bucket dollar amounts + allocation bar visual
                function updateBktAmounts() {
                    const base = pf(gid('wfd_base').value);
                    let inv = pf(gid('wfd_invAlloc').value);
                    let li  = pf(gid('wfd_liAlloc').value);
                    let ann = pf(gid('wfd_annAlloc').value);

                    // Convenience: if Investments set to 100%, zero other buckets automatically
                    if (inv >= 100) {
                        inv = 100;
                        if (li !== 0 || ann !== 0) {
                            li = 0; ann = 0;
                            gid('wfd_liAlloc').value = '0';
                            gid('wfd_annAlloc').value = '0';
                        }
                    }
                    const total = inv + li + ann;

                    const totEl = gid('wfd_allocTotal');
                    const stEl  = gid('wfd_allocStatus');
                    totEl.textContent = total.toFixed(1) + '%';
                    if (Math.abs(total - 100) < 0.11) {
                        totEl.className = 'wfd-alloc-good';
                        stEl.textContent = '✓ Ready'; stEl.style.color = '#16a34a';
                    } else {
                        totEl.className = 'wfd-alloc-bad';
                        stEl.textContent = '— must equal 100%'; stEl.style.color = '#dc2626';
                    }

                    if (base > 0) {
                        gid('wfd_invAmt').value = fmtD(base * inv / 100);
                        gid('wfd_liAmt').value  = fmtD(base * li  / 100);
                        gid('wfd_annAmt').value = fmtD(base * ann / 100);
                    } else {
                        ['wfd_invAmt','wfd_liAmt','wfd_annAmt'].forEach(id => { gid(id).value = 'Enter Retirement Base'; });
                    }

                    // Proportional bar heights
                    const mx = Math.max(inv, li, ann, 1);
                    gid('wfd_invBar').style.height = Math.max(inv / mx * 100, 3) + '%';
                    gid('wfd_liBar').style.height  = Math.max(li  / mx * 100, 3) + '%';
                    gid('wfd_annBar').style.height = Math.max(ann / mx * 100, 3) + '%';
                }
                ['wfd_invAlloc','wfd_liAlloc','wfd_annAlloc'].forEach(id => {
                    gid(id).addEventListener('input', () => { updateBktAmounts(); dpSaveDebounced(); });
                });
                ['wfd_invDownMkt','wfd_liDownMkt','wfd_annDownMkt'].forEach(id => {
                    const el = gid(id);
                    if (el) el.addEventListener('change', () => { updateDMState(); dpSaveDebounced(); });
                });
                const toggleAnnRollup = () => {
                    const wrap = gid('wfd_annRollupWrap');
                    const riderOn = gid('wfd_annIncomeRider')?.checked;
                    if (wrap) wrap.style.display = riderOn ? 'block' : 'none';
                };
                const annIncomeChk = gid('wfd_annIncomeRider');
                if (annIncomeChk) annIncomeChk.addEventListener('change', () => { toggleAnnRollup(); dpSaveDebounced(); });

                // --- DP Client Search / Load / Save ---
                let dpSearchAbort = null;
                let dpSearchToken = 0;
                let dpSearchTimer = null;
                dpResultsRef = document.getElementById('dpClientResults');
                async function searchDpClients(q){
                    const statusEl = document.getElementById('dpPlanStatus');
                    const qTrim = (q || "").trim();
                    if (dpSearchAbort){ dpSearchAbort.abort(); dpSearchAbort = null; }
                    dpSearchToken++;
                    const token = dpSearchToken;
                    if (qTrim.length === 0){
                        if (statusEl){ statusEl.textContent = "Type to search."; statusEl.classList.remove('text-danger'); }
                        if (dpResultsRef){ dpResultsRef.style.display = "none"; dpResultsRef.innerHTML = ""; }
                        return;
                    }
                    if (statusEl){ statusEl.textContent = "Searching…"; statusEl.classList.remove('text-danger'); }
                    try{
                        dpSearchAbort = new AbortController();
                        const res = await fetch(`/Clients/FinancialPlanClients?q=${encodeURIComponent(qTrim)}`, { credentials:"include", signal: dpSearchAbort.signal });
                        let list = [];
                        if (!res.ok){
                            const txt = await res.text().catch(()=> "");
                            throw new Error(txt || `Search failed (${res.status})`);
                        }
                        try { list = await res.json(); }
                        catch { throw new Error("Search response invalid."); }
                        if (token !== dpSearchToken) return; // stale
                        if (!list || list.length === 0){
                            if (statusEl){ statusEl.textContent = "No results."; statusEl.classList.add('text-danger'); }
                            if (dpResultsRef){ dpResultsRef.style.display = "none"; dpResultsRef.innerHTML = ""; }
                            return;
                        }
                        if (dpResultsRef){
                            const frag = document.createDocumentFragment();
                            list.forEach(item => {
                                const btn = document.createElement('button');
                                btn.type = "button";
                                btn.className = "list-group-item list-group-item-action";
                                btn.style.display = "flex";
                                btn.style.flexDirection = "column";
                                btn.style.alignItems = "flex-start";
                                btn.innerHTML = `
                                    <span style="font-weight:800;">${item.displayName || "Client"}</span>
                                    <span style="font-size:12px;color:#6b7280;">${item.email || "—"}${item.phone ? " · " + item.phone : ""}</span>
                                    <span style="font-size:11px;color:${item.hasSavedPlan ? '#16a34a' : '#9ca3af'};">${item.hasSavedPlan ? 'Plan saved' : 'No plan yet'}</span>
                                `;
                                btn.addEventListener('click', async ()=>{ await selectActiveClient(item); });
                                frag.appendChild(btn);
                            });
                            dpResultsRef.replaceChildren(frag);
                            dpResultsRef.style.display = "block";
                        }
                        if (statusEl){ statusEl.textContent = `Found ${list.length}. Select to load.`; statusEl.classList.remove('text-danger'); }
                    }catch(err){
                        // AbortError is expected when the user keeps typing; suppress noise.
                        if (err?.name === 'AbortError') return;
                        if (statusEl){ statusEl.textContent = err?.message || "Search failed."; statusEl.classList.add('text-danger'); }
                        if (dpResultsRef){ dpResultsRef.style.display = "none"; }
                        toast(err?.message || "Search failed.");
                    }
                }

               function hydrateDistribution(distribution){
                   const dist = distribution || {};
                   const inputs = dist.inputs || {};
                   const checks = dist.checks || {};
                   const selects = dist.selects || {};
                   const fromCrm = (dist.meta && dist.meta.source === 'crm');
                   hydrating = true;

                    // checks first (manual override state)
                    Object.keys(checks).forEach(id => { const el = gid(id); if (el) el.checked = !!checks[id]; });

                    Object.keys(inputs).forEach(id => {
                        const el = gid(id);
                        if (!el) return;
                        // skip derived values that must be recalculated locally
                        if (['wfd_invAmt','wfd_liAmt','wfd_annAmt','wfd_incomeGap','wfd_yrsInDist'].includes(id)) return;
                        if (id === 'wfd_base' && !gid('wfd_manualOverride')?.checked) return; // only honor base when manual override is on
                        el.value = inputs[id];
                    });
                    Object.keys(selects).forEach(id => {
                        const el = gid(id);
                        if (!el) return;
                        const legacyBlock = ['wfd_strategy','wfd_pri1','wfd_pri2','wfd_pri3','wfd_pri4','wfd_gapSource','wfd_scenarioMode'];
                        if (fromCrm && legacyBlock.includes(id)) return; // CRM cannot override strategy/scenario
                        el.value = selects[id];
                    });
                    // Refresh derived UI
                    updateBktAmounts();
                    updateGap();
                    togglePriorityRow();
                    hydrating = false;
                    distMeta.hasValidResults = false;
                    distMeta.result = null;
                    distMeta.lastStep = '1';
                    setStep('1');
                }

                function distInitAfterHydrate(){
                    updateDMState();
                    document.getElementById('wfd_retAge').dispatchEvent(new Event('input'));
                    document.getElementById('wfd_desiredIncome').dispatchEvent(new Event('input'));
                }

                const dpPlanUrl = (cid) => `/clients/${encodeURIComponent(cid)}/financial-plan?clientUserId=${encodeURIComponent(cid)}`;

                normalizeDistributionPayload = (payload) => {
                    // accept JSON string payloads
                    if (typeof payload === 'string') {
                        try { payload = JSON.parse(payload); } catch { payload = {}; }
                    }
                    let dist = payload?.distribution
                        || payload?.distributionPlanner
                        || payload?.distributionPlan
                        || payload?.wealthDistribution
                        || payload?.wfd
                        || {};
                    // legacy may serialize the distribution block as a string
                    if (typeof dist === 'string') {
                        try { dist = JSON.parse(dist); } catch { dist = {}; }
                    }
                    // If already shaped with inputs/checks/selects, return as-is
                    if (dist.inputs || dist.checks || dist.selects) return dist;

                    const built = { inputs:{}, checks:{}, selects:{}, meta: dist.meta || {} };
                    const checkSet = new Set(distCheckIds);
                    const selectSet = new Set(distSelectIds);

                    const absorbFlat = (flatObj) => {
                        Object.keys(flatObj || {}).forEach(k=>{
                            const v = flatObj[k];
                            if (checkSet.has(k)) built.checks[k] = !!v;
                            else if (selectSet.has(k)) built.selects[k] = v;
                            else if (k.startsWith('wfd_')) built.inputs[k] = v;
                        });
                    };

                    // Legacy step-based saves
                    ['step1','step2','step3'].forEach(step=>{
                        if (dist[step] && typeof dist[step] === 'object') absorbFlat(dist[step]);
                    });

                    // Flat legacy keys
                    absorbFlat(dist);

                    return built;
                };

                loadDpPlan = async function loadDpPlan(clientUserId, initAfter){
                    const statusEl = document.getElementById('dpPlanStatus');
                    if (statusEl) statusEl.textContent = "Loading plan…";
                    dpPlanLoaded = false;
                    try{
                        const res = await fetch(dpPlanUrl(clientUserId), { credentials:"include" });
                        if (!res.ok) throw new Error(`Load failed (${res.status})`);
                        const data = await res.json();
                        dpPlanVersion = data.version || 0;
                        let payload = {};
                        try { payload = JSON.parse(data.jsonData || "{}"); } catch { payload = {}; }
                        // preserve WF section if present on server; never null it out
                        if (payload.wealthForecast !== undefined) {
                            dpPlanCache.wealthForecast = payload.wealthForecast;
                        }
                        const distPayload = normalizeDistributionPayload(payload);
                        dpPlanCache.distribution = distPayload;
                        hydrateDistribution(distPayload);
                        if (statusEl) statusEl.textContent = data.updatedUtc ? `Loaded (updated ${new Date(data.updatedUtc).toLocaleString()})` : "Loaded";
                        dpPlanLoaded = true;
                        // re-sync base/buckets once WF balance is known
                        syncBase();
                        updateBktAmounts();
                        if (initAfter) distInitAfterHydrate();
                    }catch(err){
                        if (statusEl) statusEl.textContent = err?.message || "Load failed.";
                        toast(err?.message || "Failed to load plan.");
                    }
                }

                function showDpError(msg){
                    const statusEl = document.getElementById('dpPlanStatus');
                    if (statusEl) statusEl.textContent = msg || "Error";
                    toast(msg || "Save failed.");
                }

                async function saveDpPlan(){
                    if (!dpActiveClientId) return;
                    if (!dpPlanLoaded) {
                        showDpError("Plan not loaded — select and load a client first.");
                        return;
                    }
                    const payload = dpPayload();
                    const res = await fetch(dpPlanUrl(dpActiveClientId), {
                        method:"POST",
                        credentials:"include",
                        headers:{ "Content-Type":"application/json" },
                        body: JSON.stringify({ clientUserId: dpActiveClientId, jsonData: JSON.stringify(payload), version: payload.version })
                    });
                    if (!res.ok){
                        if (res.status === 409) {
                            showDpError("Version conflict — reload the latest plan before saving.");
                            toast("Version conflict — reload the latest plan before saving.");
                        } else showDpError(`Save failed (${res.status}).`);
                        return;
                    }
                    const data = await res.json();
                    dpPlanVersion = data.version || dpPlanVersion;
                    const statusEl = document.getElementById('dpPlanStatus');
                    if (statusEl) statusEl.textContent = data.updatedUtc ? `Saved ${new Date(data.updatedUtc).toLocaleString()}` : "Saved";
                }

                function dpSaveDebounced(){
                    if (!dpActiveClientId) return;
                    if (!dpPlanLoaded) return;
                    if (dpSaveTimer) clearTimeout(dpSaveTimer);
                    dpSaveTimer = setTimeout(() => { void saveDpPlan(); }, 700);
                }

                const dpSearchBtn = document.getElementById('dpClientSearchBtn');
                const dpSearchInput = document.getElementById('dpClientSearch');
                dpSearchInputRef = dpSearchInput;
                const dpSearchRow = document.getElementById('dpClientSearchRow');
                if (dpSearchRow) dpSearchRow.style.display = 'flex';

                if (dpSearchBtn) {
                    dpSearchBtn.addEventListener('click', (e) => {
                        e.preventDefault();
                        searchDpClients(dpSearchInput?.value || "");
                    });
                }
                if (dpSearchInput) {
                    dpSearchInput.addEventListener('keypress', (e) => {
                        if (e.key === 'Enter') {
                            e.preventDefault();
                            searchDpClients(dpSearchInput.value || "");
                        }
                    });
                    dpSearchInput.addEventListener('input', () => {
                        if (dpSearchTimer) clearTimeout(dpSearchTimer);
                        dpSearchTimer = setTimeout(()=>searchDpClients(dpSearchInput.value || ""), 250);
                    });
                }

                // Annuity type label
                // Removed legacy annType toggle listener (dropdown is source of truth)

                // Down-market badge + dim state
                function updateDMState(){
                    const rows = [
                        {chk:'wfd_invDownMkt', badge:'wfd_invDmBadge', card:'wfd_invCard'},
                        {chk:'wfd_liDownMkt',  badge:'wfd_liDmBadge',  card:'wfd_liCard'},
                        {chk:'wfd_annDownMkt', badge:'wfd_annDmBadge', card:'wfd_annCard'}
                    ];
                    rows.forEach(r => {
                        const on = gid(r.chk)?.checked;
                        const badge = gid(r.badge);
                        const card = gid(r.card);
                        if (!badge) return;
                        if (on) {
                            badge.textContent = 'Down-Market: On';
                            badge.classList.remove('off');
                            if (card) card.classList.remove('wfd-dm-off');
                        } else {
                            badge.textContent = 'Down-Market: Off';
                            badge.classList.add('off');
                            if (card) card.classList.add('wfd-dm-off');
                        }
                    });
                }

                // Strategy change
                const togglePriorityRow = () => {
                    const show = ['priority','guardrail'].includes(gid('wfd_strategy').value);
                    gid('wfd_priorityRow').style.display = show ? 'block' : 'none';
                };
                const markStrategyButtons = () => {
                    const strat = gid('wfd_strategy').value;
                    [['wfd_strat_prop','proportional'],['wfd_strat_pri','priority'],['wfd_strat_guard','guardrail']].forEach(([id,val])=>{
                        const btn = gid(id);
                        if (!btn) return;
                        btn.style.background = strat===val ? 'linear-gradient(135deg,#d9b35a 0%,#c08a1f 100%)' : '#0f172a';
                        btn.style.color = strat===val ? '#0f172a' : '#d9b35a';
                    });
                };
                ['wfd_strat_prop','wfd_strat_pri','wfd_strat_guard'].forEach(id=>{
                    const btn = gid(id);
                    if (!btn) return;
                    btn.addEventListener('click', ()=>{ gid('wfd_strategy').value = id==='wfd_strat_prop'?'proportional':id==='wfd_strat_pri'?'priority':'guardrail'; togglePriorityRow(); markStrategyButtons(); saveDistState(); });
                });
                gid('wfd_strategy').addEventListener('change', () => { togglePriorityRow(); markStrategyButtons(); saveDistState(); });
                function clearDistribution(){
                    const manualOn = document.getElementById('wfd_manualOverride')?.checked;
                    const keepIds = new Set(['wfd_desiredIncome','wfd_invTax','wfd_annTax']);
                    if (!manualOn) keepIds.add('wfd_base');
                    distInputIds.forEach(id=>{
                        if (keepIds.has(id)) return;
                        const el = gid(id); if (el) el.value = '';
                    });
                    distCheckIds.forEach(id=>{
                        const el = gid(id); if (el) el.checked = false;
                    });
                    // Re-apply default toggle states after clear
                    const invDm = gid('wfd_invDownMkt'); if (invDm) invDm.checked = false;
                    const liDm  = gid('wfd_liDownMkt');  if (liDm) liDm.checked = true;
                    const annDm = gid('wfd_annDownMkt'); if (annDm) annDm.checked = true;
                    const prot  = gid('wfd_protectInvest'); if (prot) prot.checked = true;
                    gid('wfd_strategy').value = 'proportional';
                    togglePriorityRow();
                    wfdScenarioCache = []; wfdScenarioMeta = { mode:'fixed', years:0 };
                    setPriorityOrder(defaultPriority);
                    gid('wfd_warnArea').innerHTML = '';
                    syncBase();
                    updateDMState();
                    validateAndGate();
                    distMeta.hasValidResults = false;
                    distMeta.stale = false;
                    distMeta.result = null;
                    distMeta.lastStep = '1';
                    saveMeta();
                    renderEmptyResults();
                    saveDistState();
                }
                gid('wfd_clearBtn').addEventListener('click', clearDistribution);
                gid('wfd_clearStep1')?.addEventListener('click', () => clearStep('step1'));
                gid('wfd_clearStep2')?.addEventListener('click', () => clearStep('step2'));
                gid('wfd_clearStep3')?.addEventListener('click', () => clearStep('step3'));

                function clearStep(stepKey){
                    const sets = stepFieldSets[stepKey];
                    if (!sets) return;
                    sets.inputs.forEach(id => { const el = gid(id); if (el) el.value = ''; });
                    sets.checks.forEach(id => { const el = gid(id); if (el) el.checked = false; });
                    sets.selects.forEach(id => { const el = gid(id); if (el) el.value = ''; });
                    // Restore defaults for specific toggles when clearing step context
                    if (stepKey === 'step2') {
                        const invDm = gid('wfd_invDownMkt'); if (invDm) invDm.checked = false;
                        const liDm  = gid('wfd_liDownMkt');  if (liDm) liDm.checked = true;
                        const annDm = gid('wfd_annDownMkt'); if (annDm) annDm.checked = true;
                        const prot  = gid('wfd_protectInvest'); if (prot) prot.checked = true;
                    }
                    if (stepKey === 'step3') {
                        gid('wfd_strategy').value = 'proportional';
                        setPriorityOrder(defaultPriority);
                        togglePriorityRow();
                        markStrategyButtons();
                        const prot  = gid('wfd_protectInvest'); if (prot) prot.checked = true;
                        const gap = gid('wfd_gapSource'); if (gap && !gap.value) gap.value = 'life';
                        const scen = gid('wfd_scenarioMode'); if (scen && !scen.value) scen.value = 'fixed';
                    }
                    updateGap();
                    updateYrs();
                    updateBktAmounts();
                    updateDMState();
                    validateAndGate();
                    distMeta.hasValidResults = false;
                    distMeta.stale = false;
                    distMeta.result = null;
                    saveMeta();
                    saveDistState();
                }

                // Priority selectors
                populatePrioritySelects();
                setPriorityOrder(defaultPriority);
                ['wfd_pri1','wfd_pri2','wfd_pri3','wfd_pri4'].forEach(id => {
                    const el = gid(id);
                    if (el) el.addEventListener('change', () => {
                        setPriorityOrder(getPriorityOrder());
                        saveDistState();
                    });
                });

                // Persist on input/changes
                distInputIds.forEach(id => {
                    const el = gid(id);
                    if (!el) return;
                    ['input','change','blur'].forEach(evt => el.addEventListener(evt, () => { saveDistStateDebounced(); validateAndGate(); }));
                });
                distCheckIds.forEach(id => {
                    const el = gid(id);
                    if (!el) return;
                    el.addEventListener('change', () => { saveDistStateDebounced(); validateAndGate(); });
                });

                (async () => {
                    hydrating = true;
                    await loadMeta();
                    await loadDistState();
                    togglePriorityRow();
                    markStrategyButtons();
                    setPriorityOrder(getPriorityOrder());
                    // Ensure default toggles are respected on first open when no saved state
                    updateDMState();
                    updateYrs();
                    updateGap();
                    updateBktAmounts();
                    updateDMState();
                    toggleAnnRollup();
                    syncBase();
                    validateAndGate();
                    const startStep = distMeta.lastStep || '1';
                    setStep(startStep); // internally calls hydrateResultsFromMeta if step === '4'
                    if (distMeta.open) {
                        showDistModal(startStep);
                    }
                    hydrating = false;
                })();

                // ========================
                // Main Distribution Calculation
                // ========================
                let distChart = null;

                // Scenario generator button + controls
                const genBtn = gid('wfd_genScenario');
                if (genBtn) genBtn.addEventListener('click', () => {
                    const retVal = pf(gid('wfd_retAge').value);
                    const endVal = pf(gid('wfd_endAge').value);
                    const yrs = Math.max(1, Math.floor(endVal - retVal || 0));
                    const basePct = pf(gid('wfd_invReturn').value);
                    const list = generateRandomReturns(yrs, basePct);
                    wfdScenarioCache = list;
                    wfdScenarioMeta = { mode:'random', years: yrs };
                    const area = gid('wfd_manualReturns');
                    if (area) area.value = list.map(v=>v.toFixed(1)).join(', ');
                    gid('wfd_scenarioMode').value = 'random';
                    saveDistState();
                });
                const manualArea = gid('wfd_manualReturns');
                if (manualArea) manualArea.addEventListener('input', saveDistStateDebounced);
                ['wfd_gapSource','wfd_scenarioMode'].forEach(id=>{
                    const el = gid(id); if (el) el.addEventListener('change', saveDistStateDebounced);
                });

                const goResults = () => setStep('4', { skipHydrate: true });

                function renderEmptyResults(){
                    const ctaHtml = `
                        <div style="display:flex;gap:10px;flex-wrap:wrap;margin-top:10px;">
                          <button id="wfd_emptyRun" class="wfd-calc-btn" type="button" style="flex:1;min-width:140px;max-width:200px;">Run Plan</button>
                          <button id="wfd_emptyStrategy" class="wfd-calc-btn wfd-secondary" type="button" style="flex:1;min-width:140px;max-width:200px;">Go to Strategy</button>
                        </div>`;
                    const msg = `<div style="padding:12px;border:1px dashed rgba(217,179,90,.6);border-radius:10px;background:rgba(255,255,255,.03);color:#cbd5e1;font-weight:700;">Run the plan to view results, funding analysis, and stress-test outputs.${ctaHtml}</div>`;
                    const resGrid = gid('wfd_resGrid'); if (resGrid) resGrid.innerHTML = msg;
                    const src = gid('wfd_sourceBreak'); if (src) src.innerHTML = '';
                    const emCard = gid('wfd_emCard'); if (emCard) emCard.style.display = 'none';
                    const warn = gid('wfd_warnArea'); if (warn) warn.innerHTML = '';
                    const tips = gid('wfd_tips'); if (tips) tips.innerHTML = msg;
                    const chart = gid('wfd_chart');
                    if (chart && chart.tagName.toLowerCase() === 'canvas') {
                        const ctx = chart.getContext('2d'); ctx && ctx.clearRect(0,0,chart.width, chart.height);
                    }
                    const summaryIds = ['wfd_sumIncome','wfd_sumHealth','wfd_sumLongevity','wfd_sumIncomeSuff'];
                    summaryIds.forEach(id=>{ const el = gid(id); if (el){ el.textContent='—'; el.className='wfd-sum-value'; }});
                    const hb = gid('wfd_healthBadge'); if (hb){ hb.textContent='—'; hb.className='wfd-badge'; }
                    if (gid('wfd_results')) gid('wfd_results').style.display = 'block';

                    const runBtn = gid('wfd_emptyRun');
                    if (runBtn) runBtn.onclick = () => gid('wfd_calcBtn').click();
                    const stratBtn = gid('wfd_emptyStrategy');
                    if (stratBtn) stratBtn.onclick = () => setStep('3');
                }

                function renderResults(result, isStale=false){
                    if (!result) { renderEmptyResults(); return; }
                    const { summary, cards, sourceParts, barValues, active, emCard, warns, audit, chart } = result;
                    const annDesign   = result.annDesign || 'fixed';
                    const annuityType = annDesign === 'variable' ? 'Variable' : annDesign === 'fixedIndexed' ? 'Fixed Indexed' : 'Fixed';
                    const annRiderLabels = [];
                    const hasIncRider = !!result.annIncomeRider;
                    const hasDbRider  = !!result.annDbRider;
                    const annRollupPct = result.annRollupRate ?? null;
                    if (hasIncRider) annRiderLabels.push('Income Rider');
                    if (hasDbRider)  annRiderLabels.push('Death Benefit Rider');
                    const annDesignDisplay = annRiderLabels.length ? `${annuityType}${annuityType.includes('Annuity') ? '' : ' Annuity'} + ${annRiderLabels.join(' + ')}` : `${annuityType}${annuityType.includes('Annuity') ? '' : ' Annuity'}`;
                    const liType      = result.liType || 'Life';
                    const liAccess    = result.liAccess || 'Access';
                    const lifeDesignLabel = result.lifeDesignLabel || `${liType} — ${liAccess}`;

                    // Summary
                    const setSum = (id, val, cls) => {
                        const el = gid(id); if (!el) return;
                        el.textContent = val;
                        el.className = 'wfd-sum-value';
                        if (cls) el.classList.add(cls);
                    };
                    setSum('wfd_sumIncome', fmtD(summary.atSpend), summary.incomeSufficient ? 'wfd-sum-good' : 'wfd-sum-bad');
                    setSum('wfd_sumHealth', summary.health, summary.healthCls);
                    setSum('wfd_sumLongevity', summary.depAge ? `Depletes @ Age ${summary.depAge}` : `Lasts to Age ${summary.endAge}`, summary.depAge ? 'wfd-sum-bad' : 'wfd-sum-good');
                    setSum('wfd_sumIncomeSuff',
                        summary.incomeSufficient ? `Fully funded to Age ${summary.endAge}` :
                        summary.failAge ? `Income fails @ Age ${summary.failAge}` : `Underfunded (${fmtD(summary.cumulativeShortfall)})`,
                        summary.incomeSufficient ? 'wfd-sum-good' : 'wfd-sum-bad');
                    const hb = gid('wfd_healthBadge');
                    if (hb){ hb.textContent = summary.health; hb.className = 'wfd-badge ' + summary.healthCls; }

                    // Cards
                    const startBalances = result.startBalances || {};
                    const resGrid = gid('wfd_resGrid');
                    if (resGrid) resGrid.innerHTML = (cards||[]).map(c =>
                        `<div class="wfd-res-card"><p class="wfd-res-lbl">${c.l}</p><p class="wfd-res-val ${c.c}">${c.v}</p></div>`
                    ).join('') || '<div class="wfd-res-card"><p class="wfd-res-lbl">No data</p><p class="wfd-res-val">—</p></div>';

                    // Source line
                    const src = gid('wfd_sourceBreak');
                    if (src) src.innerHTML = (sourceParts && sourceParts.length) ? sourceParts.join(' • ') : '';

                    // Bars
                    const barSet = [
                        active.em  ? { bar:'wfd_emWBar',  lbl:'wfd_emWLbl',  txt:'Emergency',   val:barValues.em } : null,
                        active.inv ? { bar:'wfd_invWBar', lbl:'wfd_invWLbl', txt:'Investments', val:barValues.inv } : null,
                        active.li  ? { bar:'wfd_liWBar',  lbl:'wfd_liWLbl',  txt:'Life Ins',    val:barValues.li } : null,
                        active.ann ? { bar:'wfd_annWBar', lbl:'wfd_annWLbl', txt:'Annuities',   val:barValues.ann } : null,
                    ].filter(Boolean);
                    const mxW = Math.max(...barSet.map(b=>b.val), 1);
                    barSet.forEach(b=>{
                        gid(b.bar).style.height = Math.max(b.val / mxW * 100, 3) + '%';
                        gid(b.lbl).innerHTML = `${b.txt}<br>${fmtD(b.val)}`;
                        gid(b.bar).parentElement.style.display = '';
                    });
                    ['wfd_emWBar','wfd_invWBar','wfd_liWBar','wfd_annWBar'].forEach(id=>{
                        const el = gid(id)?.parentElement;
                        if (el && !barSet.some(b=>b.bar===id)) el.style.display='none';
                    });

                    // Emergency card
                    const emWrap = gid('wfd_emCard');
                    if (emWrap){
                        emWrap.style.display = active.em ? '' : 'none';
                        const setVal = (id,val)=>{ const el=gid(id); if (el) el.textContent = val; };
                        setVal('wfd_emNow', fmtD(emCard.emergencyBal));
                        setVal('wfd_emUsed', fmtD(emCard.fy_emW));
                        setVal('wfd_emTotal', fmtD(emCard.totalEmUsed));
                        setVal('wfd_emRemain', fmtD(emCard.emBal));
                        setVal('wfd_emDeplete', emCard.depletionEmergAge ? `Depletes @ Age ${emCard.depletionEmergAge}` : `Active to Age ${summary.endAge}`);
                        const badge = gid('wfd_emStatus');
                        if (badge){
                            const emHealthy = emCard.emBal > 0;
                            badge.textContent = emHealthy ? 'Reserve Active' : 'Reserve Exhausted';
                            badge.className = 'wfd-badge ' + (emHealthy ? 'wfd-hlthy' : 'wfd-risk');
                        }
                    }

                    // Warnings
                    const warn = gid('wfd_warnArea');
                    const staleNote = isStale ? [{type:'info', msg:'Inputs changed. Re-run the plan to refresh results.'}] : [];
                    if (warn) warn.innerHTML = [...staleNote, ...(warns||[])].map(w =>
                        `<div class="${w.type === 'warn' ? 'wfd-warn-box' : 'wfd-info-box'}">${w.type === 'warn' ? '⚠️' : 'ℹ️'} ${w.msg}</div>`
                    ).join('');

                    // Audit
                    const auditEl = gid('wfd_tips');
                    if (auditEl){
                        const rtnClass = (pct) => {
                            if (pct < -0.001) return 'wfd-return-neg';
                            if (pct <= 0.001) return 'wfd-return-flat';
                            return 'wfd-return-pos';
                        };
                        // Build per-bucket detail chips — only for buckets with actual withdrawals
                        const bktDetail = (r) => {
                            const chips = [];
                            if (r.inv && r.inv.w > 0)  chips.push(`<span class="wfd-bkt-chip wfd-bkt-inv"><b>Investments</b> &nbsp;${fmtD(r.inv.start ?? 0)} → <span class="wfd-neg">-${fmtD(r.inv.w)}</span> → ${fmtD(r.inv.end ?? 0)}</span>`);
                            if (r.life && r.life.w > 0) {
                                const loanTxt = r.life.loanBal !== null && r.life.loanBal !== undefined ? ` | Loan ${fmtD(r.life.loanBal)}` : '';
                                const netTxt = r.life.deathEndNet !== undefined ? ` | Net DB ${fmtD(r.life.deathEndNet)}` : '';
                                const chargeTxt = r.life.charges ? ` | Charges ${fmtD(r.life.charges)}` : '';
                                const statusTxt = r.life.status ? ` | Status ${r.life.status}` : '';
                                chips.push(`<span class="wfd-bkt-chip wfd-bkt-li"><b>Life Ins</b> &nbsp;Cash ${fmtD(r.life.cashStart ?? r.life.start ?? 0)} → <span class="wfd-neg">-${fmtD(r.life.w)}</span> → ${fmtD(r.life.cashEnd ?? r.life.end ?? 0)} | DB ${fmtD(r.life.deathStart ?? 0)} → ${fmtD(r.life.deathEndGross ?? r.life.deathEnd ?? 0)}${loanTxt}${netTxt}${chargeTxt}${statusTxt}</span>`);
                            }
                            const annUsedFromAcct = r.ann ? (r.ann.w + (r.ann.riderPaidFromAccount || 0)) : 0;
                            const annIncome = r.ann?.riderIncome || 0;
                            if (r.ann && (annUsedFromAcct > 0 || annIncome > 0)) {
                                const acctPart = annUsedFromAcct > 0 ? ` → <span class="wfd-neg">-${fmtD(annUsedFromAcct)}</span>` : '';
                                const riderPart = annIncome > 0 ? ` | Rider Income ${fmtD(annIncome)}` : '';
                                const chargePart = r.ann.charges ? ` | Charges ${fmtD(r.ann.charges)}` : '';
                                const netPlan = r.ann.fundedNet ? ` | Net to Plan ${fmtD(r.ann.fundedNet)}` : '';
                                chips.push(`<span class="wfd-bkt-chip wfd-bkt-ann"><b>Annuities</b> &nbsp;${fmtD(r.ann.start ?? 0)}${acctPart} → ${fmtD(r.ann.end ?? 0)}${riderPart}${chargePart}${netPlan}</span>`);
                            }
                            if (r.em && r.em.w > 0)   chips.push(`<span class="wfd-bkt-chip wfd-bkt-em"><b>Emergency</b> &nbsp;${fmtD(r.em.start)} → <span class="wfd-neg">-${fmtD(r.em.w)}</span> → ${fmtD(r.em.end)}</span>`);
                            return chips.length ? chips.join('') : '';
                        };
                        const rows = (audit.rows||[]).map(r => {
                            const detail = bktDetail(r);
                            return `
                            <tr class="wfd-audit-main">
                              <td>${r.age}</td>
                              <td>${fmtD(r.startTotal)}</td>
                              <td class="${rtnClass(r.invReturnPct)}">${(r.invReturnPct).toFixed(1)}%</td>
                              <td>${r.marketState === 'down' ? '⬇ Down' : 'Normal'}</td>
                              <td><strong>${r.sourceFunded || '—'}</strong></td>
                              <td class="wfd-neg">${fmtD(r.withdrawTotal)}</td>
                              <td class="wfd-pos">${fmtD(r.netIncome)}</td>
                              <td class="${r.shortfall > 0 ? 'wfd-neg' : ''}">${r.shortfall > 0 ? fmtD(r.shortfall) : '—'}</td>
                              <td class="wfd-grow">${fmtD(r.endTotal)}</td>
                            </tr>${detail ? `<tr class="wfd-audit-detail"><td colspan="9"><div class="wfd-bkt-chips">${detail}</div></td></tr>` : ''}`;
                        }).join('');
                        auditEl.innerHTML = `
                          <style>
                            .wfd-audit-main td { padding: 5px 7px; border-bottom: 1px solid rgba(217,179,90,.12); vertical-align: middle; }
                            .wfd-audit-main th { padding: 6px 7px; }
                            .wfd-audit-detail td { padding: 0 7px 7px; border-bottom: 1px solid rgba(217,179,90,.18); }
                            .wfd-bkt-chips { display: flex; flex-wrap: wrap; gap: 6px; padding: 4px 0 2px; }
                            .wfd-bkt-chip { font-size: .7rem; font-weight: 600; padding: 3px 8px; border-radius: 6px; white-space: nowrap; }
                            .wfd-bkt-inv  { background: rgba(59,130,246,.15); border: 1px solid rgba(59,130,246,.4); color: #93c5fd; }
                            .wfd-bkt-li   { background: rgba(166,128,35,.15); border: 1px solid rgba(166,128,35,.4); color: #d9b35a; }
                            .wfd-bkt-ann  { background: rgba(22,163,74,.15);  border: 1px solid rgba(22,163,74,.4);  color: #86efac; }
                            .wfd-bkt-em   { background: rgba(148,163,184,.12);border: 1px solid rgba(148,163,184,.3);color: #cbd5e1; }
                            .wfd-bkt-chip .wfd-neg { color: #f87171; }
                          </style>
                          <div style="max-height:380px; overflow:auto; border:1px solid rgba(217,179,90,.4); border-radius:10px; background:#0f172a;">
                            <table style="width:100%; min-width:820px; font-size:.75rem; color:#e2e8f0; border-collapse:collapse;">
                              <thead style="position:sticky;top:0;background:#0b1529;z-index:1;">
                                <tr>
                                  <th style="padding:6px 7px;">Age</th>
                                  <th>Start Bal</th>
                                  <th>Inv Return</th>
                                  <th>Market</th>
                                  <th>Source Funded</th>
                                  <th>Withdrawals (Gross)</th>
                                  <th>Net Income</th>
                                  <th>Shortfall</th>
                                  <th>End Bal</th>
                                </tr>
                              </thead>
                              <tbody>${rows || `<tr><td colspan="9" style="text-align:center;padding:8px;">No data</td></tr>`}</tbody>
                            </table>
                          </div>`;
                    }

                    // Bucket drill-down tiles + modal
                    const tilesEl = gid('wfd_bktTiles');
                    if (tilesEl) {
                        const rows = audit.rows || [];
                        const annuityTypeLabel = annDesignDisplay;
                        const bktDefs = [
                            {
                                key: 'inv',  label: 'Investments',    color: '#3b82f6', bg: 'rgba(59,130,246,.12)',
                                border: 'rgba(59,130,246,.45)', rateLabel: 'Return %',
                                rateOf: r => r.invReturnPct,
                                startOf: r => r.inv ? (r.inv.start ?? null) : null,
                                wOf:     r => r.inv ? r.inv.w : 0,
                                endOf:   r => r.inv ? (r.inv.end ?? null) : null,
                                growthOf: r => r.inv ? (r.inv.growth ?? null) : null,
                                usedOf:   r => r.inv ? !!r.inv.used : false,
                                seriesKey: 'inv'
                            },
                            {
                                key: 'li', label: result.liType === 'legacy_rpu' ? 'Legacy / Preservation' : 'Life Insurance', color: '#d9b35a', bg: 'rgba(166,128,35,.12)',
                                border: 'rgba(166,128,35,.55)', rateLabel: 'Credited %',
                                rateOf: r => (typeof r.liRatePct === 'number' ? r.liRatePct : null),
                                startOf: r => r.life ? (r.life.cashStart ?? r.life.start ?? null) : null,
                                wOf:     r => r.life ? r.life.w : 0,
                                endOf:   r => r.life ? (r.life.cashEnd ?? r.life.end ?? null) : null,
                                deathStartOf: r => r.life ? (r.life.deathStart ?? null) : null,
                                deathEndOf:   r => r.life ? (r.life.deathEndGross ?? null) : null,
                                netDeathOf:   r => r.life ? (r.life.deathEndNet ?? null) : null,
                                loanOf:       r => r.life ? (r.life.loanBal ?? null) : null,
                                growthOf: r => r.life ? (r.life.growth ?? null) : null,
                                deathGrowthOf: r => r.life ? (r.life.deathGrowth ?? null) : null,
                                usedOf:   r => r.life ? !!r.life.used : false,
                                seriesKey: 'li'
                            },
                            {
                                key: 'ann',  label: 'Annuities',      color: '#22c55e', bg: 'rgba(22,163,74,.12)',
                                border: 'rgba(22,163,74,.45)',  rateLabel: 'Rate %',
                                rateOf: r => (typeof r.annRatePct === 'number' ? r.annRatePct : null),
                                startOf: r => r.ann ? (r.ann.start ?? null) : null,
                                wOf:     r => r.ann ? (r.ann.w + (r.ann.riderPaidFromAccount || 0)) : 0,
                                endOf:   r => r.ann ? (r.ann.end ?? null) : null,
                                deathStartOf: r => r.ann ? (r.ann.deathStart ?? null) : null,
                                deathEndOf:   r => r.ann ? (r.ann.deathEnd ?? null) : null,
                                growthOf: r => r.ann ? (r.ann.growth ?? null) : null,
                                deathGrowthOf: r => r.ann ? (r.ann.deathGrowth ?? null) : null,
                                usedOf:   r => r.ann ? !!r.ann.used : false,
                                seriesKey: 'ann'
                            }
                        ];

                        // Compute per-bucket aggregates
                        const bktStats = {};
                        bktDefs.forEach(def => {
                            let totalW = 0, yearsUsed = 0, lastEnd = 0, firstStart = startBalances[def.key] ?? null, depAge = null;
                            let firstDeath = def.key === 'li' ? startBalances.liDeath : def.key === 'ann' ? startBalances.annDeath : null;
                            let firstNetDeath = firstDeath;
                            let firstLoan = 0;
                            let lastDeath = firstDeath || 0;
                            let lastNetDeath = firstNetDeath || 0;
                            let lastLoan = 0;
                            let lastStatus = 'Active';
                            rows.forEach(r => {
                                const w   = def.wOf(r);
                                const end = def.endOf(r);
                                const st  = def.startOf(r);
                                const dSt = def.deathStartOf ? def.deathStartOf(r) : null;
                                const dEnd = def.deathEndOf ? def.deathEndOf(r) : null;
                                const netEnd = def.netDeathOf ? def.netDeathOf(r) : dEnd;
                                const loan   = def.loanOf ? def.loanOf(r) : null;
                                if (firstStart === null && st !== null) firstStart = st;
                                if (firstDeath === null && dSt !== null) firstDeath = dSt;
                                if (firstNetDeath === null && netEnd !== null) firstNetDeath = netEnd;
                                if (firstLoan === null && loan !== null) firstLoan = loan;
                                totalW   += w;
                                const used = def.usedOf ? def.usedOf(r) : (w > 0);
                                if (used) yearsUsed++;
                                if (end !== null) lastEnd = end;
                                if (dEnd !== null) lastDeath = dEnd;
                                if (netEnd !== null) lastNetDeath = netEnd;
                                if (loan !== null) lastLoan = loan;
                                if (def.key === 'li' && r.life && r.life.status) lastStatus = r.life.status;
                                if (lastEnd <= 0 && depAge === null && firstStart !== null) depAge = r.age;
                            });
                            bktStats[def.key] = { totalW, yearsUsed, lastEnd, firstStart: firstStart || 0, depAge, firstDeath: firstDeath || 0, lastDeath: lastDeath || 0, firstNetDeath: firstNetDeath || 0, lastNetDeath: lastNetDeath || 0, lastLoan: lastLoan || 0, lastStatus, annType: def.key === 'ann' ? annuityType : null, annDesign };
                        });

                        // Build tile HTML
                        const activeDefs = bktDefs.filter(d => active[d.key]);
                        if (activeDefs.length) {
                            tilesEl.style.display = '';
                            tilesEl.innerHTML = `
                              <div style="display:flex;gap:10px;flex-wrap:wrap;margin-bottom:6px;">
                                ${activeDefs.map(def => {
                                    const st = bktStats[def.key];
                                      const longevity = st.depAge ? `Depletes Age ${st.depAge}` : `Lasts to Age ${summary.endAge}`;
                                      const longevityColor = st.depAge ? '#f87171' : '#4ade80';
                                      return `<button
                                    class="wfd-bkt-tile"
                                    data-bkt="${def.key}"
                                    style="flex:1;min-width:200px;max-width:280px;
                                           background:${def.bg};border:1.5px solid ${def.border};
                                           border-radius:12px;padding:12px 14px;cursor:pointer;
                                           text-align:left;color:#e2e8f0;font-family:inherit;">
                                    <div style="font-weight:800;font-size:.82rem;color:${def.color};margin-bottom:6px;letter-spacing:.3px;">${def.label}</div>
                                    ${def.key === 'li' && result.liType === 'legacy_rpu' ? `<div style="font-size:.68rem;font-weight:700;color:#94a3b8;background:rgba(148,163,184,.1);border:1px solid rgba(148,163,184,.25);border-radius:4px;padding:2px 7px;margin-bottom:6px;display:inline-block;">Legacy only — not used for income</div>` : ''}
                                    ${def.key === 'li' ? `<div style="font-size:.7rem;font-weight:700;color:${st.lastStatus === 'Lapsed' ? '#f87171' : st.lastStatus === 'At Risk' ? '#fbbf24' : '#4ade80'};margin-bottom:6px;">Status: ${st.lastStatus || 'Active'}</div>` : ''}
                                    ${def.key === 'ann' ? `<div style="font-size:.7rem;font-weight:700;color:#fbbf24;margin-bottom:6px;">Design: ${annDesignDisplay}${hasIncRider && annRollupPct !== null ? ` · Rollup ${annRollupPct.toFixed(1)}%` : ''}</div>` : ''}
                                      <div style="font-size:.72rem;color:#94a3b8;font-weight:600;">Start</div>
                                      <div style="font-size:.97rem;font-weight:900;color:#f8fafc;">${fmtD(st.firstStart)}</div>
                                      ${(def.key === 'li' || def.key === 'ann') && ((st.firstDeath ?? 0) > 0 || (st.lastDeath ?? 0) > 0) ? `
                                      <div style="display:flex;gap:12px;flex-wrap:wrap;margin-top:6px;">
                                        <div>
                                          <div style="font-size:.68rem;color:#94a3b8;font-weight:600;">Death Benefit Start</div>
                                          <div style="font-size:.85rem;font-weight:800;color:${def.color};">${fmtD(st.firstDeath)}</div>
                                        </div>
                                        <div>
                                          <div style="font-size:.68rem;color:#94a3b8;font-weight:600;">Death Benefit End</div>
                                          <div style="font-size:.85rem;font-weight:800;color:${def.color};">${fmtD(st.lastDeath)}</div>
                                        </div>
                                      </div>` : ''}
                                      <div style="display:flex;gap:14px;margin-top:6px;flex-wrap:wrap;">
                                        <div>
                                          <div style="font-size:.68rem;color:#94a3b8;font-weight:600;">Total Gross W/D</div>
                                          <div style="font-size:.85rem;font-weight:800;color:#f87171;">${fmtD(st.totalW)}</div>
                                        </div>
                                        <div>
                                          <div style="font-size:.68rem;color:#94a3b8;font-weight:600;">Remaining</div>
                                          <div style="font-size:.85rem;font-weight:800;color:#4ade80;">${fmtD(st.lastEnd)}</div>
                                        </div>
                                        <div>
                                          <div style="font-size:.68rem;color:#94a3b8;font-weight:600;">Yrs Used</div>
                                          <div style="font-size:.85rem;font-weight:800;color:#e2e8f0;">${st.yearsUsed}</div>
                                        </div>
                                        ${def.key === 'li' ? `
                                        <div>
                                          <div style="font-size:.68rem;color:#94a3b8;font-weight:600;">Gross DB</div>
                                          <div style="font-size:.85rem;font-weight:800;color:#d9b35a;">${fmtD(st.lastDeath)}</div>
                                        </div>
                                        <div>
                                          <div style="font-size:.68rem;color:#94a3b8;font-weight:600;">Loan Balance</div>
                                          <div style="font-size:.85rem;font-weight:800;color:#fbbf24;">${fmtD(st.lastLoan)}</div>
                                        </div>
                                        <div>
                                          <div style="font-size:.68rem;color:#94a3b8;font-weight:600;">Net DB</div>
                                          <div style="font-size:.85rem;font-weight:800;color:#4ade80;">${fmtD(st.lastNetDeath)}</div>
                                        </div>` : ''}
                                      </div>
                                      <div style="margin-top:7px;font-size:.7rem;font-weight:700;color:${longevityColor};">${longevity}</div>
                                      <div style="margin-top:5px;font-size:.68rem;color:${def.color};font-weight:600;">View Breakdown →</div>
                                    </button>`;
                                }).join('')}
                              </div>`;

                            // Bucket drill-down modal — built once, reused
                            const DRILL_ID = 'wfd_bktDrill';
                            if (!document.getElementById(DRILL_ID)) {
                                const drillEl = document.createElement('div');
                                drillEl.id = DRILL_ID;
                                drillEl.style.cssText = 'display:none;position:fixed;inset:0;z-index:999999;background:rgba(5,10,20,.88);align-items:flex-start;justify-content:center;padding:20px 16px 48px;overflow-y:auto;';
                                drillEl.innerHTML = `
                                  <div id="wfd_bktDrill_panel" style="background:linear-gradient(145deg,#0b1529,#0d1c36);color:#e2e8f0;border-radius:20px;box-shadow:0 28px 70px rgba(0,0,0,.6);border:1.5px solid rgba(166,128,35,.5);width:100%;max-width:900px;font-family:'Inter',sans-serif;position:relative;margin:auto;overflow:hidden;">
                                    <div id="wfd_bktDrill_hdr" style="padding:20px 22px 16px;border-bottom:1.5px solid rgba(166,128,35,.35);">
                                      <button id="wfd_bktDrill_close" style="position:absolute;top:14px;right:14px;background:transparent;border:1.5px solid rgba(166,128,35,.5);color:#d9b35a;font-size:1.2rem;font-weight:900;width:32px;height:32px;border-radius:50%;cursor:pointer;">×</button>
                                      <div id="wfd_bktDrill_title" style="font-size:1.4rem;font-weight:900;color:#d9b35a;"></div>
                                      <div id="wfd_bktDrill_sub"   style="font-size:.82rem;color:#64748b;margin-top:2px;"></div>
                                    </div>
                                    <div style="padding:18px 22px 22px;">
                                      <div id="wfd_bktDrill_stats" style="display:grid;grid-template-columns:repeat(auto-fit,minmax(130px,1fr));gap:10px;margin-bottom:18px;"></div>
                                      <div id="wfd_bktDrill_chartWrap" style="width:100%;height:200px;margin-bottom:18px;"></div>
                                      <div id="wfd_bktDrill_table"></div>
                                    </div>
                                  </div>`;
                                document.body.appendChild(drillEl);
                                document.getElementById('wfd_bktDrill_close').addEventListener('click', () => {
                                    drillEl.style.display = 'none';
                                    document.body.style.overflow = '';
                                });
                                drillEl.addEventListener('click', e => { if (e.target === drillEl) { drillEl.style.display = 'none'; document.body.style.overflow = ''; } });
                            }

                            let drillChart = null;

                            const openDrill = async (def) => {
                                const st   = bktStats[def.key];
                                const drillEl = document.getElementById(DRILL_ID);
                                if (!drillEl) return;

                                // Header
                                document.getElementById('wfd_bktDrill_title').textContent = def.label + ' — Bucket Breakdown';
                                document.getElementById('wfd_bktDrill_sub').textContent   = `Full retirement timeline · ${rows.length} year${rows.length !== 1 ? 's' : ''}`;

                                // Stat cards
                                const longevityTxt = st.depAge ? `Depletes Age ${st.depAge}` : `Lasts to Age ${summary.endAge}`;
                                const statCards = [
                                    { l: def.key === 'ann' ? 'Starting Annuity Value' : def.key === 'li' ? 'Starting Cash Value' : 'Starting Balance',  v: fmtD(st.firstStart) },
                                    { l: 'Total Withdrawn',   v: fmtD(st.totalW), cls: 'color:#f87171' },
                                    { l: def.key === 'ann' ? 'Remaining Annuity' : def.key === 'li' ? 'Remaining Cash Value' : 'Remaining Balance', v: fmtD(st.lastEnd), cls: 'color:#4ade80' }
                                ];
                                if ((def.key === 'li' || def.key === 'ann') && ((st.firstDeath ?? 0) > 0 || (st.lastDeath ?? 0) > 0)) {
                                    statCards.splice(1, 0,
                                        { l: 'Death Benefit Start', v: fmtD(st.firstDeath) },
                                        { l: 'Death Benefit End (Gross)',   v: fmtD(st.lastDeath), cls: 'color:#d9b35a' }
                                    );
                                }
                                if (def.key === 'li') {
                                    statCards.push({ l: 'Outstanding Loan', v: fmtD(st.lastLoan || 0), cls:'color:#fbbf24' });
                                    statCards.push({ l: 'Death Benefit Net', v: fmtD(st.lastNetDeath || st.lastDeath || 0), cls:'color:#4ade80' });
                                    statCards.push({ l: 'Loan Mechanics', v: 'Loans reduce net DB; cash value keeps growing.' });
                                    statCards.push({ l: 'Policy Status', v: st.lastStatus || 'Active', cls: st.lastStatus === 'Lapsed' ? 'color:#f87171' : st.lastStatus === 'At Risk' ? 'color:#fbbf24' : 'color:#4ade80' });
                                }
                                if (def.key === 'ann') {
                                    statCards.push({ l: 'Annuity Design', v: annDesignDisplay });
                                    if (hasIncRider && annRollupPct !== null) {
                                        statCards.push({ l: 'Income Rider Rollup', v: `${annRollupPct.toFixed(1)}%` });
                                    }
                                }
                                statCards.push(
                                    { l: 'Years Used',        v: `${st.yearsUsed} / ${rows.length}` },
                                    { l: 'Longevity',         v: longevityTxt, cls: st.depAge ? 'color:#f87171' : 'color:#4ade80' }
                                );
                                if (def.key === 'li') {
                                    statCards.push({ l: 'Policy Design', v: lifeDesignLabel });
                                }
                                document.getElementById('wfd_bktDrill_stats').innerHTML = statCards.map(c =>
                                    `<div style="background:rgba(255,255,255,.04);border:1px solid rgba(166,128,35,.35);border-radius:10px;padding:10px 12px;">
                                       <div style="font-size:.68rem;font-weight:700;color:#94a3b8;letter-spacing:.4px;text-transform:uppercase;">${c.l}</div>
                                       <div style="font-size:1rem;font-weight:900;margin-top:2px;${c.cls || 'color:#f8fafc'}">${c.v}</div>
                                     </div>`
                                ).join('');

                                // Mini chart
                                const chartWrap = document.getElementById('wfd_bktDrill_chartWrap');
                                chartWrap.innerHTML = '<canvas id="wfd_bktDrill_canvas" style="width:100%;height:200px;"></canvas>';
                                try { await ensureChartJs(); } catch(_) {}
                                if (typeof Chart !== 'undefined') {
                                    if (drillChart) { drillChart.destroy(); drillChart = null; }
                                const bktSeries = (chart.series[def.seriesKey] || []);
                                    const usedFlags = [false, ...rows.map(r => def.wOf(r) > 0)];
                                    const ptColor   = bktSeries.map((_, i) => usedFlags[i] ? def.color : 'rgba(148,163,184,.4)');
                                    const ptRadius  = bktSeries.map((_, i) => usedFlags[i] ? 3 : 1);
                                    drillChart = new Chart(document.getElementById('wfd_bktDrill_canvas'), {
                                        type: 'line',
                                        data: {
                                            labels: chart.labels,
                                            datasets: [{
                                                label: def.label + ' Balance',
                                                data: bktSeries,
                                                borderColor: def.color,
                                                borderWidth: 2.5,
                                                tension: 0.2,
                                                fill: false,
                                                pointRadius: ptRadius,
                                                pointBackgroundColor: ptColor,
                                                pointBorderColor: ptColor
                                            }]
                                        },
                                        options: {
                                            responsive: true, maintainAspectRatio: false,
                                            plugins: {
                                                legend: { display: false },
                                                tooltip: { callbacks: {
                                                    label: ctx => ` Balance: $${Math.round(Number(ctx.raw)).toLocaleString()}`,
                                                    afterLabel: ctx => {
                                                        const i = ctx.dataIndex;
                                                        if (i === 0) return '';
                                                        const r = rows[i - 1];
                                                        const w = def.wOf(r);
                                                        return w > 0 ? ` Withdrawal: $${Math.round(w).toLocaleString()}` : ' No withdrawal';
                                                    }
                                                }}
                                            },
                                            scales: {
                                                x: { ticks: { color: '#64748b', maxTicksLimit: 10 }, grid: { color: 'rgba(255,255,255,.04)' } },
                                                y: { ticks: { color: '#64748b', callback: v => '$' + Number(v).toLocaleString() }, grid: { color: 'rgba(255,255,255,.04)' } }
                                            }
                                        }
                                    });
                                }

                                // Per-year table
                                const isLife = def.key === 'li';
                                const isAnn = def.key === 'ann';
                                const hdrCells = [
                                    'Age',
                                    isAnn ? 'Start Annuity Value' : isLife ? 'Start Cash Value' : 'Start Balance'
                                ];
                                if (isLife || isAnn) hdrCells.push(isLife ? 'Start Death Benefit' : 'Start Death Value');
                                if (isLife) hdrCells.push('Loan Balance');
                                hdrCells.push(def.rateLabel);
                                hdrCells.push(isAnn ? 'Withdrawal from Account' : 'Withdrawal');
                                if (isLife || isAnn) hdrCells.push('Growth / Credited');
                                if (isLife) hdrCells.push('Charges / COI');
                                if (isAnn) hdrCells.push('Rider Income Paid');
                                if (isAnn) hdrCells.push('Rider Charges');
                                hdrCells.push(isAnn ? 'End Annuity Value' : isLife ? 'End Cash Value' : 'End Balance');
                                if (isLife || isAnn) hdrCells.push(isLife ? 'End Death Benefit (Gross)' : 'End Death Value');
                                if (isLife) hdrCells.push('Net Death Benefit');
                                if (isLife) hdrCells.push('Policy Status');
                                if (isAnn) hdrCells.push('Net to Plan');
                                hdrCells.push('Used');

                               const tableRows = rows.map(r => {
                                   const w   = def.wOf(r);
                                   const st0 = def.startOf(r);
                                   const end = def.endOf(r);
                                    const deathStart = def.deathStartOf ? def.deathStartOf(r) : null;
                                    const deathEnd   = def.deathEndOf ? def.deathEndOf(r) : null;
                                    const netDeath = def.netDeathOf ? def.netDeathOf(r) : deathEnd;
                                    const loanBal = def.loanOf ? def.loanOf(r) : null;
                                    const rate = def.rateOf(r);
                                    const growth = def.growthOf ? def.growthOf(r) : null;
                                    const used = def.usedOf ? def.usedOf(r) : (w > 0);
                                    const growthStyle = growth !== null ? (growth < -0.001 ? 'color:#f87171' : 'color:#4ade80') : 'color:#94a3b8';
                                    const riderIncome = r.ann?.riderIncome ?? null;
                                    const riderCharge = r.ann?.charges ?? null;
                                    const annNetToPlan = r.ann?.fundedNet ?? null;
                                    return `<tr style="opacity:${used ? '1' : '.55'};">
                                      <td style="padding:4px 7px;">${r.age}</td>
                                      <td style="padding:4px 7px;">${st0 !== null ? fmtD(st0) : '—'}</td>
                                      ${isLife || isAnn ? `<td style="padding:4px 7px;">${deathStart !== null ? fmtD(deathStart) : '—'}</td>` : ''}
                                      ${isLife ? `<td style="padding:4px 7px;">${loanBal !== null ? fmtD(loanBal) : '—'}</td>` : ''}
                                      <td style="padding:4px 7px;${rate !== null && rate < -0.001 ? 'color:#f87171' : rate !== null && rate > 0.001 ? 'color:#4ade80' : 'color:#94a3b8'}">${rate !== null ? rate.toFixed(1) + '%' : '—'}</td>
                                      <td style="padding:4px 7px;${used ? 'color:#f87171;font-weight:700;' : 'color:#475569;'}">${used ? fmtD(w) : '—'}</td>
                                      ${isLife || isAnn ? `<td style="padding:4px 7px;${growthStyle}">${growth !== null ? fmtD(growth) : '—'}</td>` : ''}
                                      ${isLife ? `<td style="padding:4px 7px;">${r.life?.charges ? fmtD(r.life.charges) : (used ? '$0' : '—')}</td>` : ''}
                                      ${isAnn ? `<td style="padding:4px 7px;">${riderIncome !== null && riderIncome !== 0 ? fmtD(riderIncome) : (used ? '$0' : '—')}</td>` : ''}
                                      ${isAnn ? `<td style="padding:4px 7px;">${riderCharge !== null && Math.abs(riderCharge) > 1e-6 ? fmtD(riderCharge) : (used ? '$0' : '—')}</td>` : ''}
                                      <td style="padding:4px 7px;">${end !== null ? fmtD(end) : '—'}</td>
                                      ${isLife || isAnn ? `<td style="padding:4px 7px;">${deathEnd !== null ? fmtD(deathEnd) : '—'}</td>` : ''}
                                      ${isLife ? `<td style="padding:4px 7px;">${netDeath !== null ? fmtD(netDeath) : '—'}</td>` : ''}
                                      ${isLife ? `<td style="padding:4px 7px;">${r.life?.status || '—'}</td>` : ''}
                                      ${isAnn ? `<td style="padding:4px 7px;">${annNetToPlan !== null ? fmtD(annNetToPlan) : '—'}</td>` : ''}
                                      <td style="padding:4px 7px;">${used ? '<span style="color:#4ade80;font-weight:700;">Yes</span>' : '<span style="color:#475569;">—</span>'}</td>
                                    </tr>`;
                                }).join('');
                                document.getElementById('wfd_bktDrill_table').innerHTML = `
                                  <div style="max-height:300px;overflow:auto;border:1px solid rgba(217,179,90,.3);border-radius:10px;background:#0f172a;">
                                    <table style="width:100%;font-size:.73rem;color:#e2e8f0;border-collapse:collapse;">
                                      <thead style="position:sticky;top:0;background:#0b1529;">
                                        <tr>
                                          ${hdrCells.map(h => `<th style="padding:5px 7px;">${h}</th>`).join('')}
                                        </tr>
                                      </thead>
                                      <tbody>${tableRows}</tbody>
                                    </table>
                                  </div>`;

                                drillEl.style.display = 'flex';
                                document.body.style.overflow = 'hidden';
                            };

                            // Wire tile clicks — re-wire each render so closures stay fresh
                            tilesEl.querySelectorAll('.wfd-bkt-tile').forEach(btn => {
                                btn.addEventListener('click', () => {
                                    const key = btn.dataset.bkt;
                                    const def = bktDefs.find(d => d.key === key);
                                    if (def) openDrill(def);
                                });
                            });
                        } else {
                            tilesEl.style.display = 'none';
                        }
                    }

                    // Chart
                    const chartCanvas = gid('wfd_chart');
                    const renderChart = async () => {
                        let ready = true;
                        try { await ensureChartJs(); } catch(_) { ready = false; }
                        if (!ready || !chartCanvas || typeof Chart === 'undefined') {
                            if (chartCanvas) chartCanvas.outerHTML = '<div style="padding:14px;border:1px solid #e2e8f0;border-radius:10px;color:#475569;font-weight:700;">Chart unavailable. Please retry or check your connection.</div>';
                            return;
                        }
                        if (distChart) { distChart.destroy(); distChart = null; }
                        const { labels, series, marketStates, fundingSources:fs } = chart;
                        const downRadius = labels.map((_, idx) => idx === 0 ? 0 : (marketStates[idx-1] === 'down' ? 4 : 0));
                        const downColor = labels.map((_, idx) => idx === 0 ? '#d9b35a' : (marketStates[idx-1] === 'down' ? '#dc2626' : '#d9b35a'));
                        const datasets = [
                            { label: 'Total Assets', data: series.total, borderColor: '#d9b35a', borderWidth: 3, tension: 0.2, fill: false, pointRadius: downRadius, pointHoverRadius: 5, pointBackgroundColor: downColor, pointBorderColor: downColor }
                        ];
                        if (active.em)  datasets.push({ label: 'Emergency', data: series.em, borderColor: '#dc2626', borderWidth: 2, borderDash: [4,4], tension: 0.2, fill: false, pointRadius: 0, pointHoverRadius: 3 });
                        if (active.inv) datasets.push({ label: 'Investments', data: series.inv, borderColor: '#3b82f6', borderWidth: 2, borderDash: [5,3], tension: 0.2, fill: false, pointRadius: 0, pointHoverRadius: 3 });
                        if (active.li)  datasets.push({ label: 'Life Ins', data: series.li, borderColor: '#a68023', borderWidth: 2, borderDash: [5,3], tension: 0.2, fill: false, pointRadius: 0, pointHoverRadius: 3 });
                        if (active.ann) datasets.push({ label: 'Annuities', data: series.ann, borderColor: '#16a34a', borderWidth: 2, borderDash: [5,3], tension: 0.2, fill: false, pointRadius: 0, pointHoverRadius: 3 });

                        distChart = new Chart(chartCanvas, {
                            type: 'line',
                            data: { labels, datasets },
                            options: {
                                responsive: true, maintainAspectRatio: false,
                                plugins: {
                                    legend: { labels: { color: '#334155', usePointStyle: true, padding: 14 } },
                                    tooltip: { callbacks: { label: ctx => ` ${ctx.dataset.label}: $${Math.round(Number(ctx.raw)).toLocaleString()}`, afterBody: items => {
                                        const idx = items?.[0]?.dataIndex || 0;
                                        if (idx === 0) return '';
                                        const m = marketStates[idx-1] === 'down' ? 'Down-Market (defense)' : 'Normal year';
                                        const f = fs[idx-1] || '—';
                                        return [`Market: ${m}`, `Funding: ${f}`];
                                    } } }
                                },
                                scales: {
                                    x: { ticks: { color: '#64748b', maxTicksLimit: 10 }, grid: { color: 'rgba(0,0,0,.05)' } },
                                    y: { ticks: { color: '#64748b', callback: v => '$' + Number(v).toLocaleString() }, grid: { color: 'rgba(0,0,0,.05)' } }
                                }
                            }
                        });
                    };
                    renderChart();

                    if (gid('wfd_results')) gid('wfd_results').style.display = 'block';
                }

                function hydrateResultsFromMeta(){
                    if (distMeta.hasValidResults && distMeta.result){
                        renderResults(distMeta.result, distMeta.stale);
                    } else {
                        renderEmptyResults();
                    }
                }

                gid('wfd_run').addEventListener('click', () => gid('wfd_calcBtn').click());
                gid('wfd_recalcBtn')?.addEventListener('click', () => gid('wfd_calcBtn').click());
                gid('wfd_prev').addEventListener('click', () => {
                    const idx = Math.max(0, steps.indexOf(activeStep)-1);
                    setStep(steps[idx]);
                });
                gid('wfd_next').addEventListener('click', () => {
                    if (activeStep === '3') {
                        // If we already have a valid run, jump straight to Results; otherwise trigger a run.
                        if (distMeta.hasValidResults && distMeta.result) {
                            setStep('4');
                            hydrateResultsFromMeta();
                            return;
                        }
                        gid('wfd_calcBtn').click();
                        return;
                    }
                    const idx = Math.min(steps.length-1, steps.indexOf(activeStep)+1);
                    setStep(steps[idx]);
                });
                gid('wfd_editFoundation')?.addEventListener('click', ()=>setStep('1'));
                gid('wfd_editBuckets')?.addEventListener('click', ()=>setStep('2'));
                gid('wfd_editStrategy')?.addEventListener('click', ()=>setStep('3'));
                gid('wfd_runBase')?.addEventListener('click', ()=>{
                    const scen = gid('wfd_scenarioMode'); if (scen) scen.value = 'fixed';
                    gid('wfd_manualReturns').value = '';
                    gid('wfd_calcBtn').click();
                });
                gid('wfd_runDown')?.addEventListener('click', ()=>{
                    const scen = gid('wfd_scenarioMode'); if (scen) scen.value = 'random';
                    const threshold = gid('wfd_downThreshold'); if (threshold) threshold.value = '0';
                    gid('wfd_protectInvest').checked = true;
                    gid('wfd_calcBtn').click();
                });
                gid('wfd_runScenario')?.addEventListener('click', ()=>{
                    if (typeof wfdScenarioCache === 'object') wfdScenarioCache = [];
                    const scen = gid('wfd_scenarioMode'); if (scen) scen.value = 'random';
                    gid('wfd_genScenario')?.click();
                });

                gid('wfd_calcBtn').addEventListener('click', async () => {
                    const preErrs = validateDist();
                    showBlock(preErrs);
                    if (preErrs.length) return;

                    try { await ensureChartJs(); } catch (_) { /* chart unavailable; renderResults handles gracefully */ }

                    const base          = pf(gid('wfd_base').value);
                    const retAge        = pf(gid('wfd_retAge').value);
                    const endAge        = pf(gid('wfd_endAge').value);
                    const years         = Math.floor(endAge - retAge);
                    const desiredInc    = pf(gid('wfd_desiredIncome').value);
                    const guarInc       = pf(gid('wfd_guaranteedIncome').value);
                    const incGap        = Math.max(desiredInc - guarInc, 0);
                    let emergencyBal    = Math.max(0, pf(gid('wfd_emergency').value));

                    const invAllocPct   = pf(gid('wfd_invAlloc').value);
                    const liAllocPct    = pf(gid('wfd_liAlloc').value);
                    const annAllocPct   = pf(gid('wfd_annAlloc').value);

                    const invReturn     = pf(gid('wfd_invReturn').value)   / 100;
                    const invTax        = pf(gid('wfd_invTax').value)      / 100;
                    const invDownMkt    = gid('wfd_invDownMkt').checked;

                    const liGrowth      = pf(gid('wfd_liGrowth').value)    / 100;
                    const liTax         = pf(gid('wfd_liTax').value)       / 100;
                    const liEff         = (pf(gid('wfd_liEfficiency').value) || 100) / 100;
                    const liDeathStart  = pf(gid('wfd_liDeath').value);
                    const liDownMkt     = gid('wfd_liDownMkt').checked;

                    const annReturn     = pf(gid('wfd_annReturn').value)   / 100;
                    const annTax        = pf(gid('wfd_annTax').value)      / 100;
                    const annDeathStart = pf(gid('wfd_annDeath').value);
                    const annDownMkt    = gid('wfd_annDownMkt').checked;
                    const annDbRider    = gid('wfd_annDbRider').checked;
                    const annIncomeRider= gid('wfd_annIncomeRider').checked;
                    const annRollupRate = (pf(gid('wfd_annRollup').value) || 5) / 100;
                    const annDesign     = gid('wfd_annDesign').value || 'fixed';
                    const liType        = gid('wfd_liType').value || 'whole';
                    const liAccess      = gid('wfd_liAccess').value || 'withdrawal';

                    let strategy        = gid('wfd_strategy').value;
                    if (strategy === 'downmarket') strategy = 'guardrail'; // legacy persisted value
                    const protectInvest = gid('wfd_protectInvest').checked;
                    const gapSource     = gid('wfd_gapSource').value || 'life';
                    const scenarioMode  = gid('wfd_scenarioMode').value || 'fixed';
                    const downThreshold = pf(gid('wfd_downThreshold').value) / 100;
                    const manualReturnTxt = gid('wfd_manualReturns').value || '';
                    const priOrder      = getPriorityOrder();
                    const scenarioReturns = buildScenarioReturns(years, scenarioMode, invReturn, manualReturnTxt);
                    const annVarReturns = generateRandomReturns(years, annReturn * 100).map(v => v / 100);

                    // --- Validation ---
                    const errs = validateDist();
                    gid('wfd_warnArea').innerHTML = '';
                    if (errs.length > 0) {
                        gid('wfd_warnArea').innerHTML = errs.map(e => `<div class="wfd-warn-box">⚠️ ${e}</div>`).join('');
                        if (distMeta.hasValidResults && distMeta.result) {
                            hydrateResultsFromMeta();
                        } else {
                            renderEmptyResults();
                        }
                        return;
                    }

                    // --- Starting balances ---
                    let invBal  = base * invAllocPct  / 100;
                    let liBal   = base * liAllocPct   / 100;
                    let annBal  = base * annAllocPct  / 100;
                    let emBal   = emergencyBal;
                    let liDeathBal  = Math.max(0, liDeathStart);
                    let annDeathBal = annDbRider ? Math.max(0, annDeathStart || annBal) : annBal;
                    let annRiderBase = annDbRider ? annDeathBal : 0;
                    // whole_loan: tracks outstanding loan balance (not subtracted from cash value)
                    const liLoanRate = 0.05; // 5% annual policy loan interest (fixed rate default)
                    let liLoanBal = 0;
                    // income rider (optional): dual-account — cash value + guaranteed income base
                    // income rider rollup rate (independent of market returns)
                    const annRollupRateDec = annIncomeRider ? annRollupRate : 0;
                    // age-banded payout rate: higher payout at older retirement ages (locked on first income draw)
                    const annPayoutRateForAge = (age) => age < 60 ? 0.040 : age < 65 ? 0.045 : age < 70 ? 0.050 : age < 75 ? 0.055 : 0.060;
                    let annLockedPayoutRate = annPayoutRateForAge(retAge); // provisional; re-locked at income start
                    let annIncomeBase = annIncomeRider ? annBal : 0;
                    let annIncomeBenefit = annIncomeRider ? annIncomeBase * annLockedPayoutRate : 0;
                    let annIncomeStarted = false; // income rider: tracks whether income draw has begun (locks rollup + payout rate)
                    const startInvBal = invBal, startLiBal = liBal, startAnnBal = annBal, startEmBal = emBal;
                    const startLiDeath = liDeathBal, startAnnDeath = annDeathBal;

                    const shortfallTol = Math.max(100, incGap * 0.02); // tolerance used for visuals only
                    const onlyInvestmentsFunded = (invAllocPct > 0) && (liAllocPct <= 0) && (annAllocPct <= 0);

                    // --- Year-by-year simulation ---
                    const totalPts = [invBal + liBal + annBal + emBal];
                    const invPts   = [invBal];
                    const liPts    = [liBal];
                    const annPts   = [annBal];
                    const annReturnSeries = [];
                    const liDeathPts  = [liDeathBal];
                    const annDeathPts = [annDeathBal];
                    const emPts    = [emBal];
                    const yLabels  = ['Age ' + retAge];
                    const auditRows = [];
                    const marketStates = [];
                    const fundingSources = [];
                    const invReturnSeries = [];

                    let depletionYr = null;
                    let depletionEmerg = null;
                    let liLapsed = false;
                    let fy_emW = 0, fy_invW = 0, fy_liW = 0, fy_annW = 0; // first-year withdrawals
                    let year1Shortfall = 0;
                    let anyYearShortfall = false;
                    let cumulativeShortfall = 0;
                    let firstFailureYear = null;
                    let lastFullyFundedAge = null;
                    let lastPositiveAge = retAge;
                    let totalEmUsed = 0;
                    let totalInvDraw = 0, totalLiDraw = 0, totalAnnDraw = 0;
                    let depletionEmergAge = null;
                    let downYearCount = 0;

                    const bucketLabels = { investments:'Investments', life:'Life Insurance', annuities:'Annuities', emergency:'Emergency' };
                    const uniqSeq = (arr) => arr.filter((v,i) => v && (i === 0 || arr[i-1] !== v));
                    const joinArrow = (arr) => arr.map(b => bucketLabels[b] || b).join(' → ');
                    const joinPlus  = (arr) => arr.map(b => bucketLabels[b] || b).join(' + ');
                    const buildFundingLabel = ({ path, investGuarded, marketState, invW, strategy }) => {
                        const clean = uniqSeq(path);
                        if (investGuarded && marketState === 'down') {
                            const nonInv = clean.filter(p => p !== 'investments');
                            if (invW <= 0) {
                                if (nonInv.length) return `Protected Investments; ${joinArrow(nonInv)}`;
                                return 'Protected Investments; no draw';
                            }
                            if (nonInv.length) return `Protected Investments; ${joinArrow(nonInv)} → Investments (fallback)`;
                            return 'Protected Investments; Investments (fallback)';
                        }
                        if (clean.length === 0) return 'None';
                        if (clean.length === 1) return bucketLabels[clean[0]] || clean[0];
                        return strategy === 'proportional' ? joinPlus(clean) : joinArrow(clean);
                    };

                    for (let y = 1; y <= years; y++) {
                        // Snapshot each bucket before any withdrawal or growth this iteration
                        const invStart0 = invBal;
                        const liStart0  = liBal;
                        const annStart0 = annBal;
                        const emStart0  = emBal;
                        const liDeathStart0  = liDeathBal;
                        const annDeathStart0 = annDeathBal;
                        const startBalTotal = invStart0 + liStart0 + annStart0 + emStart0;
                        const invYearR = (scenarioReturns[y-1] !== undefined ? scenarioReturns[y-1] : invReturn);
                        // Life design-driven growth
                        let liYearR = liGrowth;
                        if (liType === 'iul') liYearR = Math.max(0, Math.min(Math.max(invYearR, 0), 0.12) * 0.6); // capped + participation for conservatism
                        else if (liType === 'vul') liYearR = invYearR;
                        else if (liType === 'legacy_rpu') liYearR = Math.min(liGrowth, 0.03); // conservative credited

                        // Annuity design-driven growth
                        const annBaseVarR = (annVarReturns[y-1] !== undefined ? annVarReturns[y-1] : annReturn);
                        let annYearR = annReturn;
                        if (annDesign === 'variable') annYearR = annBaseVarR;
                        else if (annDesign === 'fixedIndexed') {
                            const capped = Math.min(Math.max(annBaseVarR, 0), 0.10);
                            annYearR = Math.max(0, (capped * 0.6) - 0.01); // 60% participation minus 1% spread
                        }
                        if (annIncomeRider) {
                            // Income base rolls up only during deferral; locks once income draw begins
                            if (!annIncomeStarted) {
                                annIncomeBase = annIncomeBase * (1 + annRollupRateDec);
                                annIncomeBenefit = annIncomeBase * annLockedPayoutRate;
                            }
                        }
                        // For fixed base, annReturn already set; for FIA we modified; for VAR we set above.
                        invReturnSeries.push(invYearR);
                        const effInvR = invYearR;
                        const effAnnR = annYearR;
                        const effLiR  = liYearR;
                        annReturnSeries.push(effAnnR);
                        const marketState = invYearR <= downThreshold ? 'down' : 'normal';
                        if (marketState === 'down') downYearCount += 1;
                        marketStates.push(marketState);

                        let invW = 0, liW = 0, annW = 0;
                        let liCharges = 0;
                        let needLeftNet = incGap; // net gap after guaranteed income

                        const allowLife   = (liAccess !== 'none') && (liType !== 'legacy_rpu') && !liLapsed && (marketState === 'down' ? liDownMkt : true);
                        const allowAnn    = (marketState === 'down' ? annDownMkt : true);
                        const investGuarded = protectInvest && marketState === 'down' && !onlyInvestmentsFunded;
                        const fundingPath = [];
                        const recordBucket = (bucket, amt) => { if (amt > 0 && fundingPath[fundingPath.length-1] !== bucket) fundingPath.push(bucket); };

                        // ── Strategy-driven cascade engine ──────────────────────────────
                        // Each year we build an ordered draw sequence and pull from each
                        // bucket only as much as needed — stopping as soon as the gap is met.
                        // No per-bucket withdrawal rate cap; buckets can provide up to their
                        // full available balance, limited only by the annual need.

                        const drawFromBucket = (bucket) => {
                            if (needLeftNet <= 1e-2) return; // tolerance: stop when gap is covered
                            const canUse = bucket === 'investments' ? (investGuarded ? false : (marketState === 'down' ? invDownMkt : true))
                                         // legacy_rpu or access none or lapsed: preservation/legacy bucket — never drawn as income source
                                         : bucket === 'life'        ? (allowLife)
                                         :                            (marketState === 'down' ? annDownMkt : true);
                            if (bucket === 'annuities' && annIncomeRider) return; // rider handles income separately
                            if (!canUse) return;
                            const avail   = bucket === 'investments' ? Math.max(0, invBal)
                                          : bucket === 'life'
                                            ? (liAccess === 'loan'
                                                ? Math.max(0, Math.min((liBal * 0.9 - liLoanBal), (liBal - liLoanBal) * liEff))
                                                : Math.max(0, liBal * liEff))
                                          : Math.max(0, annBal);
                            // policy loan proceeds are income-tax-free
                            const tax     = bucket === 'investments' ? invTax
                                          : bucket === 'life'        ? (liAccess === 'loan' ? 0 : liTax)
                                          :                            annTax;
                            const grossNeed = tax < 1 ? needLeftNet / (1 - tax) : needLeftNet;
                            const draw      = Math.min(avail, grossNeed);
                            if (draw <= 0) return;
                            if (bucket === 'investments') invW += draw;
                            else if (bucket === 'life')   liW  += draw;
                            else                          annW += draw;
                            needLeftNet -= netFromGross(draw, tax);
                            recordBucket(bucket, draw);
                        };

                        // Build draw order for this year
                        if (investGuarded && gapSource === 'split') {
                            // Proportional split between life and annuities, investments as last resort
                            // legacy_rpu blocked as income source; policy loan draws are tax-free
                            const liAvail  = allowLife ? Math.max(0, liBal * liEff) : 0;
                            const annAvail = allowAnn  ? Math.max(0, annBal)         : 0;
                            const total    = liAvail + annAvail;
                            if (total > 0 && needLeftNet > 1e-2) {
                                const liShare  = needLeftNet * (liAvail  / total);
                                const annShare = needLeftNet * (annAvail / total);
                                if (allowLife && liAvail > 0) {
                                    const liTaxSplit = liDesign === 'whole_loan' ? 0 : liTax;
                                    const draw = Math.min(liAvail, liTaxSplit < 1 ? liShare / (1 - liTaxSplit) : liShare);
                                    liW += draw; needLeftNet -= netFromGross(draw, liTaxSplit);
                                    recordBucket('life', draw);
                                }
                                if (allowAnn && annAvail > 0 && needLeftNet > 1e-2) {
                                    const draw = Math.min(annAvail, annTax < 1 ? annShare / (1 - annTax) : annShare);
                                    annW += draw; needLeftNet -= netFromGross(draw, annTax);
                                    recordBucket('annuities', draw);
                                }
                            }
                            // Investments as final fallback even when guarded
                            drawFromBucket('investments');
                        } else {
                            let drawOrder;
                            if (investGuarded) {
                                // Down-year with investment protection: backup order, investments as last resort
                                if (gapSource === 'life' || gapSource === 'lifeThenAnnuities')      drawOrder = ['life','annuities','investments'];
                                else if (gapSource === 'annuities' || gapSource === 'annThenLife')  drawOrder = ['annuities','life','investments'];
                                else if (gapSource === 'custom') {
                                    const custom = normalizePriority(priOrder).filter(x => x !== 'emergency' && x !== 'investments');
                                    drawOrder = [...custom, 'investments'];
                                } else drawOrder = ['life','annuities','investments'];
                            } else if (strategy === 'priority') {
                                // User-defined priority order every year
                                drawOrder = normalizePriority(priOrder).filter(x => x !== 'emergency');
                            } else {
                                // proportional + guardrail normal years: investments first, cascade only if insufficient
                                drawOrder = ['investments','life','annuities'];
                            }
                            drawOrder.forEach(drawFromBucket);
                        }

                        if (y === 1) { fy_invW = invW; fy_liW = liW; fy_annW = annW; }

                        // Emergency LAST, only for remaining gap
                        const grossNeededAfterBuckets = Math.max(0, needLeftNet);
                        const emUse = Math.min(grossNeededAfterBuckets, emBal);
                        emBal -= emUse;
                        totalEmUsed += emUse;
                        if (emBal <= 0 && depletionEmerg === null && emergencyBal > 0) depletionEmerg = y;
                        if (y === 1) fy_emW = emUse;
                        if (emUse > 0) recordBucket('emergency', emUse);

                        // Lock payout rate at first income year (retirement start in this model) and stop future rollup
                        if (annIncomeRider && !annIncomeStarted) {
                            annLockedPayoutRate = annPayoutRateForAge(retAge + y);
                            annIncomeBenefit = annIncomeBase * annLockedPayoutRate;
                            annIncomeStarted = true;
                        }

                        const liEffTax  = liAccess === 'loan' ? 0 : liTax;
                        const annNetContribution = annIncomeRider ? annIncomeBenefit : netFromGross(annW, annTax);
                        const annGrossContribution = annIncomeRider ? annIncomeBenefit : annW;
                        let fundedNet = netFromGross(invW, invTax) + netFromGross(liW, liEffTax) + annNetContribution + emUse;
                        const yearShort = Math.max(incGap - fundedNet, 0);
                        if (y === 1) year1Shortfall = yearShort;
                        cumulativeShortfall += yearShort;
                        if (yearShort > 0 && firstFailureYear === null) firstFailureYear = y;
                        if (yearShort > 0) anyYearShortfall = true;
                        if (yearShort === 0 && !anyYearShortfall) lastFullyFundedAge = retAge + y;

                        const fundingSource = buildFundingLabel({ path: fundingPath, investGuarded, marketState, invW, strategy });
                        fundingSources.push(fundingSource);

                        const riderIncomeGross = annIncomeRider ? annIncomeBenefit : 0;
                        const riderPaidFromAccount = annIncomeRider ? Math.min(annBal, riderIncomeGross) : 0;
                        if (annIncomeRider && riderIncomeGross > 0) recordBucket('annuities', riderIncomeGross);

                        // Withdrawal first, then growth
                        const invPre   = Math.max(0, invBal  - invW);
                        // Policy loan: accrue interest on prior loan balance first, then add this year's draw
                        let liLoanInterest = 0;
                        if (liAccess === 'loan') { liLoanInterest = liLoanBal * liLoanRate; liLoanBal = liLoanBal + liLoanInterest + liW; }
                        const liPre    = liAccess === 'loan' ? liBal : Math.max(0, liBal - liW);
                        const annPre   = Math.max(0, annBal  - annW - riderPaidFromAccount);
                        // Death benefit start-of-year snapshot (conservative level DB unless explicitly modeled otherwise)
                        const liDeathPre  = liDeathBal;
                        const annDeathPre = annDbRider ? annDeathBal : Math.max(0, annDeathBal - annW);

                        invBal  = invPre  * (1 + effInvR);
                        liBal   = liPre   * (1 + effLiR);
                        // vul: age-banded COI drag (worsens with age; approximates blended COI + sub-account expenses)
                        if (liType === 'vul') {
                            const vulAge = retAge + y;
                            const vulCOI = vulAge < 70 ? 0.010 : vulAge < 75 ? 0.015 : vulAge < 80 ? 0.022 : 0.032;
                            const vulCharge = liBal * vulCOI;
                            liCharges += vulCharge;
                            liBal = Math.max(0, liBal - vulCharge);
                        }
                        // iul admin/insurance drag (conservative)
                        if (liType === 'iul') {
                            const iulAdmin = liBal * 0.0075;
                            liCharges += iulAdmin;
                            liBal = Math.max(0, liBal - iulAdmin);
                        }
                        annBal  = annPre  * (1 + effAnnR);
                        // variable: annual M&E drag (~1.25% of account value)
                        if (annDesign === 'variable') annBal = Math.max(0, annBal * (1 - 0.0125));
                        // income rider: annual rider charge (~0.6% of income base, deducted from cash value)
                        let annCharges = 0;
                        if (annDesign === 'variable') { /* charge already applied in net effect above; track for audit */ annCharges += annBal * 0; }
                        if (annIncomeRider) { const riderCharge = annIncomeBase * 0.006; annCharges += riderCharge; annBal = Math.max(0, annBal - riderCharge); }
                        // Death benefit evolution by design (default: level, no automatic accrual)
                        let liDeathNext = liDeathPre;
                        // Future increasing DB options would adjust liDeathNext here; none are enabled by default.
                        liDeathBal = liDeathNext;
                        if (annDbRider) {
                            // true high-water-mark: ratchet steps up only when account value exceeds prior high
                            annRiderBase = Math.max(annRiderBase, annBal);
                            annDeathBal = Math.max(annBal, annRiderBase);
                        } else {
                            annDeathBal = annBal;
                        }
                        emBal   = Math.max(0, emBal); // cash reserve, no growth

                        const invGrowth = invBal - invPre;
                        const liGrowthAmt = liBal - liPre;
                        const annGrowthAmt = annBal - annPre;
                        const liDeathGrowth = liDeathBal - liDeathPre;
                        const annDeathGrowth = annDeathBal - annDeathPre;

                        totalInvDraw += invW; totalLiDraw += liW; totalAnnDraw += (annW + riderPaidFromAccount);

                        const totalNow = invBal + liBal + annBal + emBal;
                        invPts.push(invBal); liPts.push(liBal); annPts.push(annBal); emPts.push(emBal);
                        // whole_loan: chart shows net death benefit (gross minus outstanding loans)
                        const liNetDeath = liAccess === 'loan' ? Math.max(0, liDeathBal - liLoanBal) : liDeathBal;
                        if (liAccess === 'loan' && liNetDeath <= 0) {
                            liLapsed = true;
                            liBal = 0;
                        }
                        liDeathPts.push(liNetDeath);
                        annDeathPts.push(annDeathBal);
                        totalPts.push(totalNow);
                        if (totalNow > 0) lastPositiveAge = retAge + y;
                        yLabels.push('Age ' + (retAge + y));

                        if (invBal + liBal + annBal + emBal <= 0 && !depletionYr) depletionYr = y;
                        if (emBal <= 0 && depletionEmerg !== null && !depletionEmergAge) depletionEmergAge = retAge + depletionEmerg;

                        // Build source label strictly from nonzero withdrawals — no strategy language
                        const usedBuckets = [];
                        if (emUse > 0) usedBuckets.push('Emergency');
                        if (invW  > 0) usedBuckets.push('Investments');
                        if (liW   > 0) usedBuckets.push('Life Insurance');
                        if (annW  > 0 || riderIncomeGross > 0) usedBuckets.push('Annuities');
                        const sourceFundedLabel = usedBuckets.length ? usedBuckets.join(' + ') : (yearShort > 0 ? 'Unfunded' : 'None');

                        auditRows.push({
                            age: retAge + y,
                            invReturnPct: invYearR * 100,
                            liRatePct: liGrowth * 100,
                            annRatePct: effAnnR * 100,
                            marketState,
                            sourceFunded: sourceFundedLabel,
                            startTotal: startBalTotal,
                            withdrawTotal: invW + liW + annW + riderPaidFromAccount + emUse,
                            netIncome: fundedNet,
                            endTotal: invBal + liBal + annBal + emBal,
                            shortfall: yearShort,
                            // per-bucket detail — start is pre-withdrawal snapshot; end is post-growth balance
                            inv:  (invStart0 > 0 || invW > 0) ? { start: invStart0, w: invW, end: invBal, growth: invGrowth, used: invW > 0 } : null,
                            life: (liStart0 > 0 || liDeathStart0 > 0 || liW > 0 || (liAccess === 'loan' && liLoanBal > 0)) ? {
                                cashStart: liStart0,
                                deathStart: liDeathStart0,
                                w: liW,
                                cashEnd: liBal,
                                deathEndGross: liDeathBal,
                                deathEndNet: liNetDeath,
                                loanBal: liAccess === 'loan' ? liLoanBal : null,
                                growth: liGrowthAmt,
                                deathGrowth: liDeathGrowth,
                                loanInterest: liAccess === 'loan' ? liLoanInterest : null,
                                charges: liCharges,
                                status: liAccess === 'loan' ? (liLapsed ? 'Lapsed' : (liLoanBal >= liDeathBal * 0.9 ? 'At Risk' : 'Active')) : 'Active',
                                used: liW > 0 || (liAccess === 'loan' && liLoanBal > 0)
                            } : null,
                            ann:  (annStart0 > 0 || annDeathStart0 > 0 || annW > 0 || riderIncomeGross > 0 || riderPaidFromAccount > 0) ? {
                                start: annStart0,
                                deathStart: annDeathStart0,
                                w: annW,
                                riderIncome: riderIncomeGross,
                                riderPaidFromAccount,
                                charges: annCharges,
                                end: annBal,
                                deathEnd: annDeathBal,
                                incomeBase: annIncomeRider ? annIncomeBase : null,
                                incomeBenefit: annIncomeRider ? annIncomeBenefit : null,
                                fundedNet: annNetContribution,
                                growth: annGrowthAmt,
                                deathGrowth: annDeathGrowth,
                                used: (annW > 0) || (riderIncomeGross > 0) || (riderPaidFromAccount > 0)
                            } : null,
                            em:   emUse > 0 ? { start: emStart0, w: emUse, end: emBal, used: emUse > 0 } : null
                        });
                    }

                    // --- Tax-aware first-year outputs ---
                    const net_invW  = fy_invW  * (1 - invTax);
                    const net_liW   = fy_liW   * (1 - (liAccess === 'loan' ? 0 : liTax));
                    const annGrossContributionFY = annIncomeRider ? annIncomeBenefit : fy_annW;
                    const net_annW  = annIncomeRider ? annIncomeBenefit : (fy_annW * (1 - annTax));
                    const net_emW   = fy_emW;
                    const totalNetW = net_invW + net_liW + net_annW + net_emW;
                    const totalGrW  = fy_invW + fy_liW + annGrossContributionFY + fy_emW;
                    const firstYearShortfall = year1Shortfall;
                    // --- Horizon-wide tracking ---
                    const shortfall = firstYearShortfall; // single source of truth for Yr1 shortfall
                    const atSpend   = guarInc + totalNetW;
                    const finalTot  = totalPts[totalPts.length - 1];
                    const depAge    = depletionYr ? retAge + depletionYr : null;

                    // --- Additional horizon metrics ---
                    const incomeSufficient = !anyYearShortfall && cumulativeShortfall <= 0;
                    const assetsLast = !depAge;
                    const anyYearFailure = anyYearShortfall;
                    const lastFundedAge = lastFullyFundedAge || (anyYearFailure ? retAge + (firstFailureYear || 0) - 1 : endAge);
                    const depletionAge = depAge || null;
                    const cumulativeShort = cumulativeShortfall;

                    let health = 'Healthy', healthCls = 'wfd-hlthy';
                    if (assetsLast && incomeSufficient) {
                        health = 'Healthy'; healthCls = 'wfd-hlthy';
                    } else if (assetsLast && !incomeSufficient && cumulativeShort <= incGap * Math.max(0.15, years ? 0.05 * years : 0.15)) {
                        health = 'Tight'; healthCls = 'wfd-tight';
                    } else {
                        health = 'At Risk'; healthCls = 'wfd-risk';
                    }

                    const badge = gid('wfd_healthBadge');
                    badge.textContent = health;
                    badge.className = 'wfd-badge ' + healthCls;

                    // --- Active buckets: only show those used/allocated
                    const active = {
                        inv: (invAllocPct > 0) || (startInvBal > 0) || (totalInvDraw > 0),
                        li:  (liAllocPct  > 0) || (startLiBal  > 0) || (totalLiDraw  > 0),
                        ann: (annAllocPct > 0) || (startAnnBal > 0) || (totalAnnDraw > 0),
                        em:  (startEmBal   > 0) || (totalEmUsed > 0)
                    };

                    const failAge = firstFailureYear ? (retAge + firstFailureYear) : null;
                    const annuityType = annDesign === 'variable' ? 'Variable' : annDesign === 'fixedIndexed' ? 'Fixed Indexed' : 'Fixed';
                    const lifeDesignLabel = (() => {
                        const typeLabel = liType === 'iul' ? 'Indexed UL'
                                          : liType === 'vul' ? 'Variable UL'
                                          : liType === 'legacy_rpu' ? 'Legacy-Focused / RPU'
                                          : 'Whole Life';
                        const accessLabel = liAccess === 'loan' ? 'Policy Loans'
                                           : liAccess === 'withdrawal' ? 'Withdrawals'
                                           : 'No Distributions';
                        return `${typeLabel} — ${accessLabel}`;
                    })();

                    // --- Result cards ---
                    const cards = [
                        { l: 'Desired Annual Income',      v: fmtD(desiredInc),   c: '' },
                        { l: 'Guaranteed Income (after-tax)',          v: fmtD(guarInc),      c: 'green' },
                        { l: 'Income Gap (from Assets)',   v: fmtD(incGap),       c: incGap > desiredInc * 0.85 ? 'red' : '' },
                        active.em  ? { l: 'Yr 1 Emergency W/D',         v: fmtD(fy_emW),  c: '' } : null,
                        active.inv ? { l: 'Yr 1 Investments Gross W/D', v: fmtD(fy_invW), c: '' } : null,
                        active.li  ? { l: 'Yr 1 Life Ins Gross W/D',    v: fmtD(fy_liW),  c: '' } : null,
                        active.ann ? { l: 'Yr 1 Annuity Gross W/D',     v: fmtD(fy_annW), c: '' } : null,
                        { l: 'Total Yr 1 Gross Withdrawals',     v: fmtD(totalGrW),     c: '' },
                        { l: 'After-Tax Spendable (Yr1)',  v: fmtD(atSpend),      c: incomeSufficient ? 'green' : 'red' },
                        { l: 'First-Year Shortfall',       v: fmtD(firstYearShortfall), c: firstYearShortfall > shortfallTol ? 'red' : '' },
                        { l: 'Cumulative Shortfall',       v: fmtD(cumulativeShortfall), c: cumulativeShortfall > 0 ? 'red' : '' },
                        { l: 'Any-Year Funding Failure',   v: anyYearFailure ? 'Yes' : 'No', c: anyYearFailure ? 'red' : 'green' },
                        { l: 'Last Continuous Funded Year',      v: lastFundedAge ? `Age ${lastFundedAge}` : '—', c: anyYearFailure ? 'red' : 'green' },
                        { l: 'Asset Longevity',            v: assetsLast ? `Lasts to Age ${endAge}` : `Depletes @ Age ${depAge}`, c: assetsLast ? 'green' : 'red' },
                        { l: 'Income Sufficiency',         v: incomeSufficient ? `Fully funded to Age ${endAge}` : (failAge ? `Income fails @ Age ${failAge}` : `Income fails`), c: incomeSufficient ? 'green' : 'red' },
                    ].filter(Boolean);
                    // --- Source parts (used in canonical result) ---
                    const srcParts = [];
                    if (active.em)  srcParts.push(`From Emergency: ${fmtD(fy_emW)}`);
                    if (active.inv) srcParts.push(`From Investments (gross): ${fmtD(fy_invW)}`);
                    if (active.li)  srcParts.push(`From Life Insurance (gross): ${fmtD(fy_liW)}`);
                    if (active.ann) srcParts.push(`From Annuities (gross): ${fmtD(annGrossContributionFY)}`);
                    srcParts.push(`Total Gross Sourced: ${fmtD(totalGrW)}`);
                    srcParts.push(`After-Tax Spendable: ${fmtD(atSpend)}`);
                    if (shortfall>0) srcParts.push(`Unfunded Shortfall: ${fmtD(shortfall)}`);
                    if (downYearCount > 0 && protectInvest) srcParts.push(`Protection active in ${downYearCount} down-market year(s)`);

                    // --- Warnings (used in canonical result) ---
                    const warns = [];
                    if (!incomeSufficient)
                        warns.push({ type:'warn', msg:`Income target underfunded by ${fmtD(shortfall)} in year 1; plan longevity alone does not meet the desired cash flow.` });
                    if (atSpend < desiredInc * 0.9)
                        warns.push({ type:'warn', msg:`After-tax spendable (${fmtD(atSpend)}) is below the desired income target. Consider increasing allocations, improving protected/guaranteed income, or revisiting strategy/tax assumptions.` });
                    if (depAge && endAge - depAge > 5)
                        warns.push({ type:'warn', msg:`Assets deplete ${endAge - depAge} years before the plan horizon. Reduce withdrawals, extend guaranteed income, or increase the retirement base.` });
                    if (totalGrW < incGap * 0.8 && incGap > 0)
                        warns.push({ type:'warn', msg:`Total first-year withdrawals (${fmtD(totalGrW)}) are below the income gap (${fmtD(incGap)}). The selected strategy may not be drawing enough from the available buckets to meet the income target.` });
                    if (depletionEmerg && depletionEmerg < years)
                        warns.push({ type:'warn', msg:`Emergency reserve depletes by year ${depletionEmerg}. Remaining needs are covered by other buckets thereafter.` });
                    if (shortfall > 0)
                        warns.push({ type:'warn', msg:`Unfunded shortfall of ${fmtD(shortfall)} remains after withdrawals; reduce income target or increase protected sources.` });
                    if (downYearCount > 0 && protectInvest)
                        warns.push({ type:'info', msg:`Investment bucket was protected in ${downYearCount} down-market year(s); safer buckets filled the gap first.` });
                    if (scenarioMode === 'random')
                        warns.push({ type:'info', msg:`Randomized market path is an illustration for stress-testing only — not a prediction or guarantee.` });

                    // --- Persist + hydrate canonical result ---
                        const result = {
                            summary: {
                                atSpend,
                                incomeSufficient,
                                health,
                                healthCls,
                                depAge,
                                endAge,
                                failAge,
                                cumulativeShortfall
                            },
                            annuityType,
                            annDesign,
                            liType,
                            liAccess,
                            lifeDesignLabel,
                            annIncomeRider,
                            annDbRider,
                            annRollupRate: annIncomeRider ? annRollupRateDec * 100 : null,
                            startBalances: { inv: startInvBal, li: startLiBal, ann: startAnnBal, em: startEmBal, liDeath: startLiDeath, annDeath: startAnnDeath },
                        cards,
                        sourceParts: srcParts,
                        barValues: { em: fy_emW, inv: fy_invW, li: fy_liW, ann: annGrossContributionFY },
                        active,
                        emCard: { emergencyBal, fy_emW, totalEmUsed, emBal, depletionEmergAge },
                        warns,
                        audit: { rows: auditRows },
                            chart: {
                            labels: yLabels,
                            series: { total: totalPts, em: emPts, inv: invPts, li: liPts, ann: annPts, liDeath: liDeathPts, annDeath: annDeathPts, annReturn: annReturnSeries },
                            marketStates,
                            fundingSources
                        }
                    };
                    distMeta.hasValidResults = true;
                    distMeta.stale = false;
                    distMeta.lastStep = '4';
                    distMeta.result = result;
                    saveMeta();
                    renderResults(result, false);

                    // Save state without flagging stale
                    hydrating = true;
                    saveDistState();
                    hydrating = false;

                    goResults();
                });
            } // end: if (!document.getElementById(DIST_OVR_ID))

            // Distribution button opens modal and syncs base
                distBtn.addEventListener('click', () => {
                    const overlay = document.getElementById(DIST_OVR_ID);
                    if (!overlay) {
                        console.error('Distribution overlay not found.');
                        return;
                    }
                    lastActiveEl = document.activeElement;
                    overlay.classList.add('wfd-open');
                    document.body.style.overflow = 'hidden';
                    trapFocus(overlay);
                    const statusEl = document.getElementById('dpPlanStatus');
                    if (!dpActiveClientId){
                        if (statusEl) statusEl.textContent = "Select a client to load plan.";
                        const inp = document.getElementById('dpClientSearch');
                        inp?.focus();
                        return;
                    }
                    loadDpPlan(dpActiveClientId, true);
                });

            // Initial calculation
            // hydrate-first: run calc only after load if client selected
            if (wfActiveClientId){
                // load will call calc when finished
            } else {
                calcWealthForecast();
            }

            [startingBalEl, incomeEl, yearsEl, inflEl, retEl, taxEl, liabEl, lifeEl, disruptStartEl, disruptYearsEl, disruptMonthsEl, disabilityPctEl].forEach(el => {
                el.addEventListener("input", () => {
                    calcWealthForecast();
                    saveWfPlanDebounced();
                });
            });
        }

// ==========================================================
// 2️⃣ SAVINGS ACCELERATOR (ELEVATED) + Tooltips
// ==========================================================
if (t.id === "SavingsAccelerator") {
    embedContainer.innerHTML = `
<div class="networth-tool p-4" 
     style="background:#ffffff; 
            border-radius:20px; 
            box-shadow:0 12px 35px rgba(166,128,35,0.15); 
            border:1px solid rgba(166,128,35,0.35); 
            max-width:1200px; 
            margin:0 auto;
            font-family: 'Inter', sans-serif;">

    <!-- Tooltip styles (safe + isolated) -->
    <style>
        .sa-label{
            display:inline-flex;
            align-items:center;
            gap:8px;
            margin-bottom:6px;
            font-weight:700;
            color:#a68023;
        }
        .sa-i{
            display:inline-flex;
            align-items:center;
            justify-content:center;
            width:18px;
            height:18px;
            border-radius:999px;
            background:#fff;
            border:1px solid rgba(210,31,43,.9);
            color:#d21f2b;
            font-weight:900;
            font-size:12px;
            line-height:1;
            cursor:pointer;
            user-select:none;
            transform: translateY(-1px);
            box-shadow:0 6px 18px rgba(0,0,0,.08);
        }
        .sa-i:focus{
            outline:none;
            box-shadow:0 0 0 3px rgba(210,31,43,.18), 0 10px 25px rgba(0,0,0,.10);
        }
        #saTipLayer{
            position:fixed;
            inset:0;
            pointer-events:none;
            z-index:2147483647;
        }
        .sa-tipbox{
            position:absolute;
            max-width:min(360px, 86vw);
            background:#fff;
            color:#111;
            border:1px solid rgba(0,0,0,.12);
            border-left:4px solid #d21f2b;
            padding:12px 12px;
            border-radius:14px;
            font-size:12.8px;
            font-weight:650;
            line-height:1.35;
            box-shadow:0 18px 45px rgba(0,0,0,.18);
            opacity:0;
            transform:translateY(6px);
            transition:opacity .12s ease, transform .12s ease;
            pointer-events:none;
            white-space:normal;
        }
        .sa-tipbox b{ font-weight:900; }
        .sa-tipbox.show{ opacity:1; transform:translateY(0); }
    </style>

    <div id="saTipLayer"></div>

    <h3 style="color:#a68023; font-weight:900; letter-spacing:0.5px; font-size:2rem;">
        ${t.name}
    </h3>

    <p style="font-style:italic; color:#666; margin-bottom:20px;">
        Calculate your monthly surplus and optimize how you allocate it for maximum wealth building.
    </p>

    <div class="row mb-3" style="display:flex; gap:20px; flex-wrap:wrap;">
        <div style="flex:1; min-width:200px;">
            <div class="sa-label">
                Net Cash Flow
                <span class="sa-i" tabindex="0" data-tip="<b>Examples:</b> 3,800 • 5,200 (monthly take-home / net income)">i</span>
            </div>
            <div style="position:relative;">
                <input id="saNet" type="text" class="form-control" placeholder="e.g., 2,000"
                       style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; font-size:1.1rem; color:#a68023; padding-right:30px;" />
                <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">$</span>
            </div>
        </div>
        <div style="flex:1; min-width:200px;">
            <div class="sa-label">
                Essential Expenses
                <span class="sa-i" tabindex="0" data-tip="<b>Examples:</b> 2,100 • 3,000 (rent, utilities, food, transport, insurance)">i</span>
            </div>
            <div style="position:relative;">
                <input id="saEss" type="text" class="form-control" placeholder="e.g., 1,500"
                       style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; font-size:1.1rem; color:#a68023; padding-right:30px;" />
                <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">$</span>
            </div>
        </div>
    </div>

    <h5 style="margin-top:10px; font-weight:700;">
        Surplus:
        <span id="saOut" style="color:#222; font-weight:900;">$0</span>
    </h5>

    <div class="mt-4">
        <h5 style="color:#a68023; font-weight:700; border-bottom:1px solid rgba(166,128,35,0.35); padding-bottom:6px;">
            Cash Flow Allocation
        </h5>

        <!-- New totals row: Remaining Surplus left on the left, Total Allocated on the right -->
        <div class="d-flex align-items-center mb-3" style="gap:8px;">
            <div style="flex:2; font-weight:700; color:#222; text-align:left;">
                Remaining Surplus: <span id="saRemaining" style="color:#a68023; font-weight:900;">$0</span>
            </div>
            <div style="flex:1; text-align:right; font-weight:700; color:#222;">
                Total Allocated: <span id="saPctTotal" style="color:#a68023; font-weight:900;">0%</span>
            </div>
        </div>

        <div id="allocationContainer" class="mt-3"></div>

        <div class="d-flex gap-2 mt-3">
            <button id="saAddCat" class="btn btn-outline-gold"
                    style="border:1px solid #a68023; color:#a68023; font-weight:600;">+ Add Category</button>
            <button id="saDelCat" class="btn btn-outline-gold"
                    style="border:1px solid #a68023; color:#a68023; font-weight:600;">- Delete Last</button>
        </div>
    </div>

    <div id="saTips"
         style="padding:14px;
                background:linear-gradient(135deg, #f1ede3, #e1d6b8);
                border-left:5px solid #a68023;
                font-style:italic;
                color:#333;
                margin-top:20px;
                border-radius:10px;
                box-shadow:inset 0 0 12px rgba(166,128,35,0.25);">
        Direct extra cash strategically across savings, debt reduction, and key priorities.
    </div>
</div>`;

    const container = embedContainer.querySelector('.networth-tool');
    const saNetInput = document.getElementById('saNet');
    const saEssInput = document.getElementById('saEss');
    const saOut = document.getElementById('saOut');
    const saTips = document.getElementById('saTips');
    const allocationContainer = document.getElementById('allocationContainer');
    const addBtn = document.getElementById('saAddCat');
    const delBtn = document.getElementById('saDelCat');
    const saPctTotal = document.getElementById('saPctTotal');
    const saRemaining = document.getElementById('saRemaining');

    let categoryCount = 0;

    const formatNumber = (val) => {
        val = val.toString().replace(/,/g, '');
        return !isNaN(val) && val !== '' ? Number(val).toLocaleString() : '';
    };

    // ✅ Single paint helper so EVERYTHING stays consistent (income/expense/neutral)
    const paint = (el, mode) => {
        if (!el) return;
        if (mode === 'income') markIncome(el);
        else if (mode === 'expense') markExpense(el);
        else markNeutral(el); // neutral = gold
    };

    // ----- Tooltip engine (overlay) -----
    const tipLayer = document.getElementById('saTipLayer');
    const tipBox = document.createElement('div');
    tipBox.className = 'sa-tipbox';
    tipLayer.appendChild(tipBox);

    const showTip = (el) => {
        const html = el.getAttribute('data-tip') || '';
        if (!html) return;

        tipBox.innerHTML = html;

        const r = el.getBoundingClientRect();
        const pad = 10;
        const boxW = Math.min(360, Math.floor(window.innerWidth * 0.86));

        let left = Math.min(window.innerWidth - boxW - pad, Math.max(pad, r.left - 10));
        tipBox.style.maxWidth = boxW + 'px';
        tipBox.style.left = left + 'px';

        tipBox.classList.add('show');
        const h = tipBox.getBoundingClientRect().height;

        let desiredTop = (r.top - h - 12);
        if (desiredTop < pad) desiredTop = (r.bottom + 12);

        tipBox.style.top = desiredTop + 'px';
    };

    const hideTip = () => tipBox.classList.remove('show');

    // Register for global click binder
    window.__LegendHideActiveTip = hideTip;

    container.querySelectorAll('.sa-i').forEach(el => {
        el.addEventListener('mouseenter', () => showTip(el));
        el.addEventListener('mouseleave', hideTip);
        el.addEventListener('focus', () => showTip(el));
        el.addEventListener('blur', hideTip);
        el.addEventListener('click', (e) => {
            e.stopPropagation();
            if (tipBox.classList.contains('show')) hideTip();
            else showTip(el);
        });
    });

    const saveAllocationState = () => {
        const net = saNetInput.value || '';
        const ess = saEssInput.value || '';
        const allocations = [];
        document.querySelectorAll('.allocation-row').forEach(row => {
            allocations.push({
                name: row.querySelector('.allocation-name').value || '',
                percent: row.querySelector('.allocation-percent').value || ''
            });
        });
        savePersistedState('SavingsAccelerator', { net, ess, allocations });

        // Push to shared Finance Profile (only fields this tool owns)
        if (window.LegendFinanceProfile?.update) {
            const partial = {};
            const netNum = +net.replace(/,/g, '') || 0;
            const essNum = +ess.replace(/,/g, '') || 0;
            if (net) partial.monthlyNet = netNum;
            if (ess) partial.fixedExpenses = essNum;
            window.LegendFinanceProfile.update(partial);
        }
    };

    const loadAllocationState = async () => {
        allocationContainer.innerHTML = '';
        categoryCount = 0;
        let created = 0;

        const state = await loadPersistedState('SavingsAccelerator');
        saNetInput.value = state.net || '';
        saEssInput.value = state.ess || '';

        (state.allocations || []).forEach(a => {
            createAllocationRow(++categoryCount, a.name, a.percent);
            created++;
        });

        while (created < 3) {
            createAllocationRow(++categoryCount);
            created++;
        }

        refreshSurplus();
    };

    const applyProfileToSavingsAccelerator = () => {
        const prof = window.LegendFinanceProfile?.get?.();
        if (!prof) return;
        if (saNetInput && !saNetInput.value) {
            saNetInput.value = prof.monthlyNet || prof.monthlyGross || '';
        }
        if (saEssInput && !saEssInput.value) {
            saEssInput.value = prof.fixedExpenses || '';
        }
        refreshSurplus();
    };

    const createAllocationRow = (index, preName = '', prePercent = '') => {
        const row = document.createElement('div');
        row.className = 'allocation-row d-flex align-items-center mb-2 gap-2';
        row.style.cssText = 'background:#fafafa;padding:8px;border-radius:10px;border:1px solid #eee;';

        const name = document.createElement('input');
        name.className = 'form-control allocation-name';
        name.style.flex = '2';
        name.placeholder = `Category ${index}`;
        name.value = preName;
        name.addEventListener('input', saveAllocationState);

        const amtWrap = document.createElement('div');
        amtWrap.style.cssText = 'flex:1;position:relative;';

        const amt = document.createElement('input');
        amt.className = 'form-control allocation-amount';
        amt.readOnly = true;
        amt.style.cssText = 'border:1px solid #d6c48a;font-weight:700;color:#a68023;background:#f3f0e8;';
        amt.value = '';

        const dollar = document.createElement('span');
        dollar.textContent = '$';
        dollar.style.cssText = 'position:absolute;right:10px;top:50%;transform:translateY(-50%);font-weight:700;color:#a68023;';
        amtWrap.appendChild(amt);
        amtWrap.appendChild(dollar);

        const pctWrap = document.createElement('div');
        pctWrap.style.cssText = 'flex:1;position:relative;';

        const pct = document.createElement('input');
        pct.className = 'form-control allocation-percent';
        pct.value = prePercent || '';
        pct.style.cssText = 'font-weight:700;color:#a68023;padding-right:28px;';
        pct.oninput = refreshSurplus;

        const pctSign = document.createElement('span');
        pctSign.textContent = '%';
        pctSign.style.cssText = 'position:absolute;right:10px;top:50%;transform:translateY(-50%);font-weight:700;color:#a68023;';
        pctWrap.appendChild(pct);
        pctWrap.appendChild(pctSign);

        const del = document.createElement('button');
        del.textContent = '✕';
        del.style.cssText = 'border:none;background:transparent;color:#a68023;font-weight:900;cursor:pointer;';
        del.onclick = () => { allocationContainer.removeChild(row); refreshSurplus(); };

        row.append(name, amtWrap, pctWrap, del);
        allocationContainer.appendChild(row);

        // Initial paint for new rows
        paint(name, 'neutral');
        paint(pct, 'neutral');
        paint(amt, 'neutral');
    };

    const refreshSurplus = () => {
        const net = +saNetInput.value.replace(/,/g, '') || 0;
        const ess = +saEssInput.value.replace(/,/g, '') || 0;
        const surplus = net - ess;
        saOut.textContent = `$${surplus.toLocaleString()}`;

        let usedPct = 0;
        let totalAllocatedAmt = 0;

        document.querySelectorAll('.allocation-row').forEach(row => {
            const pctInput = row.querySelector('.allocation-percent');
            const amtInput = row.querySelector('.allocation-amount');

            let pct = +pctInput.value || 0;
            if (usedPct + pct > 100) pct = Math.max(0, 100 - usedPct);
            usedPct += pct;

            const amt = surplus > 0 ? (pct / 100) * surplus : 0;
            totalAllocatedAmt += amt;

            pctInput.value = pct;
            amtInput.value = amt.toLocaleString();
        });

        const remaining = surplus - totalAllocatedAmt;

        saPctTotal.textContent = usedPct.toFixed(1) + '%';
        saRemaining.textContent = `$${remaining.toLocaleString()}`;

        saTips.textContent = surplus <= 0
            ? '⚠️ Your expenses match or exceed your net cash flow. Adjust your budget or increase income.'
            : '✅ Good surplus! Use surplus funds strategically for savings and financial goals.';

        // ==========================================================
        // ✅ COLOR CODING — INPUTS + OUTPUTS + ROWS (FULL COVERAGE)
        // ==========================================================

        // Inputs
        paint(saNetInput, net > 0 ? 'income' : (net < 0 ? 'expense' : 'neutral'));
        paint(saEssInput, ess > 0 ? 'expense' : (ess < 0 ? 'income' : 'neutral'));

        // Outputs
        paint(saOut, surplus > 0 ? 'income' : (surplus < 0 ? 'expense' : 'neutral'));
        paint(saPctTotal, usedPct >= 100 ? 'expense' : 'neutral'); // gold until "maxed", then red as a warning
        paint(saRemaining, remaining > 0 ? 'income' : (remaining < 0 ? 'expense' : 'neutral'));
        paint(saTips, 'neutral');

        // Rows
        document.querySelectorAll('.allocation-percent').forEach(p => paint(p, 'neutral'));
        document.querySelectorAll('.allocation-name').forEach(n => paint(n, 'neutral'));

        // Allocation $ amounts follow surplus state (income=green, deficit=red, zero=gold)
        document.querySelectorAll('.allocation-amount').forEach(a => {
            paint(a, surplus > 0 ? 'income' : (surplus < 0 ? 'expense' : 'neutral'));
        });

        saveAllocationState();
    };

    saNetInput.oninput = saEssInput.oninput = refreshSurplus;
    saNetInput.onblur = () => saNetInput.value = formatNumber(saNetInput.value);
    saEssInput.onblur = () => saEssInput.value = formatNumber(saEssInput.value);

    addBtn.onclick = () => { createAllocationRow(++categoryCount); refreshSurplus(); };
    delBtn.onclick = () => {
        const last = allocationContainer.lastElementChild;
        if (last) { allocationContainer.removeChild(last); refreshSurplus(); }
    };

    addClearButton(container, () => {
        saNetInput.value = saEssInput.value = '';
        allocationContainer.innerHTML = '';
        categoryCount = 0;
        for (let i = 0; i < 3; i++) createAllocationRow(++categoryCount);
        saOut.textContent = '$0';
        saPctTotal.textContent = '0%';
        saRemaining.textContent = '$0';
        saTips.textContent = 'Direct extra cash strategically across savings, debt reduction, and key priorities.';
        clearPersistedState('SavingsAccelerator');
        hideTip();
        refreshSurplus();
    });

await loadAllocationState();
    applyProfileToSavingsAccelerator();
    window.addEventListener("FinanceProfile:updated", applyProfileToSavingsAccelerator);
    window.addEventListener("FinanceProfile:ready", applyProfileToSavingsAccelerator);

// ✅ Force correct colors AFTER state load (so it stays green/red)
refreshSurplus();

}


/* -------------------------------
    3️⃣ EXPENSE LENS (ELEVATED)
--------------------------------*/
if (t.id === "ExpenseLens") {
    try {
        embedContainer.innerHTML = `
        <div class="networth-tool p-4" 
             style="background:#ffffff; 
                    border-radius:20px; 
                    box-shadow:0 12px 35px rgba(166,128,35,0.15); 
                    border:1px solid rgba(166,128,35,0.35); 
                    max-width:1200px; margin:0 auto;
                    font-family: 'Inter', sans-serif;">

            <!-- Tooltip styles (safe + isolated) -->
            <style>
                .el-label{
                    display:inline-flex;
                    align-items:center;
                    gap:8px;
                    margin-bottom:6px;
                    font-weight:800;
                    color:#a68023;
                }
                .el-i{
                    display:inline-flex;
                    align-items:center;
                    justify-content:center;
                    width:18px;
                    height:18px;
                    border-radius:999px;
                    background:#fff;
                    border:1px solid rgba(210,31,43,.9);
                    color:#d21f2b;
                    font-weight:900;
                    font-size:12px;
                    line-height:1;
                    cursor:pointer;
                    user-select:none;
                    transform: translateY(-1px);
                    box-shadow:0 6px 18px rgba(0,0,0,.08);
                }
                .el-i:focus{
                    outline:none;
                    box-shadow:0 0 0 3px rgba(210,31,43,.18), 0 10px 25px rgba(0,0,0,.10);
                }
                #elTipLayer{
                    position:fixed;
                    inset:0;
                    pointer-events:none;
                    z-index:2147483647;
                }
                .el-tipbox{
                    position:absolute;
                    max-width:min(360px, 86vw);
                    background:#fff;
                    color:#111;
                    border:1px solid rgba(0,0,0,.12);
                    border-left:4px solid #d21f2b;
                    padding:12px 12px;
                    border-radius:14px;
                    font-size:12.8px;
                    font-weight:650;
                    line-height:1.35;
                    box-shadow:0 18px 45px rgba(0,0,0,.18);
                    opacity:0;
                    transform:translateY(6px);
                    transition:opacity .12s ease, transform .12s ease;
                    pointer-events:none;
                    white-space:normal;
                }
                .el-tipbox b{ font-weight:900; }
                .el-tipbox.show{ opacity:1; transform:translateY(0); }

               
            </style>

            <div id="elTipLayer"></div>

            <h3 style="color:#a68023; font-weight:900; letter-spacing:0.5px; font-size:2rem;">
                ${t.name}
            </h3>

            <p style="font-style:italic; color:#666; margin-bottom:20px;">
                Break down your income into categories and visualize spending percentages for better budgeting.
            </p>

            <div class="el-label">
                Total Income
                <span class="el-i" tabindex="0"
                      data-tip="<b>Examples:</b> 4,500 • 6,200 (total monthly income before allocating categories)">i</span>
            </div>
            <div style="position:relative; margin-bottom:15px;">
                <input id="elIncome" type="text" 
                       class="form-control mb-3"
                       placeholder="Enter total monthly income"
                       style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#a68023; padding-right:30px;" />
                <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">$</span>
            </div>

            <div id="elCategories" style="margin-top:10px; display:flex; flex-direction:column; gap:12px;"></div>

            <div class="d-flex gap-2 mt-3" style="gap:12px; flex-wrap:wrap;">
                <button id="elAddCat" 
                        class="btn btn-outline-gold"
                        style="border:1px solid #a68023; color:#a68023; font-weight:600;">
                    + Add Category
                </button>
                <button id="elDelCat" 
                        class="btn btn-outline-gold"
                        style="border:1px solid #a68023; color:#a68023; font-weight:600;">
                    - Delete Last
                </button>
            </div>

            <div id="elTips"
                 style="padding:14px; 
                        background:linear-gradient(135deg, #f1ede3, #e1d6b8); 
                        border-left:5px solid #a68023; 
                        font-style:italic; 
                        color:#333; 
                        margin-top:20px; 
                        border-radius:10px;
                        box-shadow:inset 0 0 12px rgba(166,128,35,0.25);">
                Monitor each category to identify areas to save or invest.
            </div>

            <div id="elMargin"
                 style="margin-top:18px;
                        padding:16px;
                        background:#f8f6f0;
                        border-radius:12px;
                        font-weight:800;
                        color:#222;
                        font-size:1.1rem;
                        text-align:center;
                        border:1px solid #dbd9d3;">
                Remaining Balance: $0
            </div>
        </div>`;

        const container = embedContainer.querySelector('.networth-tool');
        const categoriesContainer = document.getElementById("elCategories");
        const addBtn = document.getElementById("elAddCat");
        const delBtn = document.getElementById("elDelCat");
        const elTips = document.getElementById("elTips");
        const elMargin = document.getElementById("elMargin");
        const elIncome = document.getElementById("elIncome");
        

        

        // Apply visual styles (matches the rest)
        applyToolBoxStyles(container);

        // ✅ TOOLTIP ENGINE (overlay)
        const tipLayer = document.getElementById('elTipLayer');
        const tipBox = document.createElement('div');
        tipBox.className = 'el-tipbox';
        tipLayer.appendChild(tipBox);

        const showTip = (el) => {
            const html = el.getAttribute('data-tip') || '';
            if (!html) return;

            tipBox.innerHTML = html;

            const r = el.getBoundingClientRect();
            const pad = 10;
            const boxW = Math.min(360, Math.floor(window.innerWidth * 0.86));

            let left = Math.min(window.innerWidth - boxW - pad, Math.max(pad, r.left - 10));
            tipBox.style.maxWidth = boxW + 'px';
            tipBox.style.left = left + 'px';

            tipBox.classList.add('show');
            const h = tipBox.getBoundingClientRect().height;

            let desiredTop = (r.top - h - 12);
            if (desiredTop < pad) desiredTop = (r.bottom + 12);

            tipBox.style.top = desiredTop + 'px';
        };

        const hideTip = () => tipBox.classList.remove('show');

        // Register for global click binder
        window.__LegendHideActiveTip = hideTip;

        container.querySelectorAll('.el-i').forEach(el => {
            el.addEventListener('mouseenter', () => showTip(el));
            el.addEventListener('mouseleave', hideTip);
            el.addEventListener('focus', () => showTip(el));
            el.addEventListener('blur', hideTip);
            el.addEventListener('click', (e) => {
                e.stopPropagation();
                if (tipBox.classList.contains('show')) hideTip();
                else showTip(el);
            });
        });

        let categoryCount = 0;

        // -----------------------------
        // Format numbers with commas
        // -----------------------------
        const formatNumber = (val) => {
            val = val.toString().replace(/,/g,'');
            return !isNaN(val) && val !== '' ? Number(val).toLocaleString() : '';
        };

        // -----------------------------
        // State Handling
        // -----------------------------
        const saveExpenseLensState = () => {
            try {
                const income = elIncome.value || '';
                const categories = [];
                document.querySelectorAll('[id^="elCatRow"]').forEach(row => {
                    const index = row.id.replace('elCatRow', '');
                    const name = document.getElementById(`elCatName${index}`).value || '';
                    const amount = document.getElementById(`elCatAmount${index}`).value || '';
                    categories.push({ index, name, amount });
                });
                const state = { income, categories };
                savePersistedState('ExpenseLens', state);
            } catch (e) { console.error(e); }
        };

        const loadExpenseLensState = async () => {
            try {
                const state = await loadPersistedState('ExpenseLens');
                categoriesContainer.innerHTML = '';
                categoryCount = 0;
                let categoriesCreated = 0;

                if (state) {
                    elIncome.value = state.income || '';

                    if (state.categories && state.categories.length > 0) {
                        state.categories.forEach(cat => {
                            createCategoryRow(++categoryCount, cat.name, cat.amount);
                            categoriesCreated++;
                        });
                    }
                }

                // Fallback to shared Finance Profile if nothing saved
                if (categoriesCreated === 0) {
                    const prof = window.LegendFinanceProfile?.get?.();
                    if (prof) {
                        if (!elIncome.value) {
                            elIncome.value = prof.monthlyNet || prof.monthlyGross || '';
                        }
                        if (Array.isArray(prof.expenses) && prof.expenses.length > 0) {
                            prof.expenses.forEach(exp => {
                                const amt = exp?.amount ?? '';
                                createCategoryRow(++categoryCount, exp?.name || `Expense ${categoryCount}`, amt);
                                categoriesCreated++;
                            });
                        }
                    }
                }

                if (categoriesCreated === 0) createCategoryRow(++categoryCount);
                refreshExpenseLens();
            } catch (e) { console.error(e); }
        };

        const clearExpenseLensState = () => clearPersistedState('ExpenseLens');

        // -----------------------------
        // Create Category Row
        // -----------------------------
        const createCategoryRow = (index, preName = '', preAmount = '') => {
            const div = document.createElement("div");
            div.className = "d-flex align-items-center";
            div.id = `elCatRow${index}`;
            div.style.background = "#fafafa";
            div.style.padding = "10px";
            div.style.borderRadius = "10px";
            div.style.border = "1px solid #eee";
            div.style.columnGap = "12px";
            div.style.rowGap = "10px";
            div.style.flexWrap = "wrap";

            const nameInput = document.createElement("input");
            nameInput.type = "text";
            nameInput.id = `elCatName${index}`;
            nameInput.className = "form-control flex-grow-1";
            nameInput.placeholder = `Category ${index} Name`;
            nameInput.style.border = "1px solid #ddd";
            nameInput.style.color = "#a68023";
            nameInput.style.flex = "1 1 220px";
            nameInput.value = preName;
            nameInput.addEventListener("input", saveExpenseLensState);

            const amountWrapper = document.createElement("div");
            amountWrapper.style.position = "relative";
            amountWrapper.style.flex = "1 1 150px";
            amountWrapper.style.minWidth = "140px";

            const amountInput = document.createElement("input");
            amountInput.type = "text";
            amountInput.id = `elCatAmount${index}`;
            amountInput.className = "form-control";
            amountInput.placeholder = "Amount";
            amountInput.style.width = "100%";
            amountInput.style.border = "1px solid #d6c48a";
            amountInput.style.fontWeight = "700";
            amountInput.style.color = "#a68023";
            amountInput.value = preAmount;

            const dollarSpan = document.createElement("span");
            dollarSpan.textContent = "$";
            dollarSpan.style.position = "absolute";
            dollarSpan.style.right = "10px";
            dollarSpan.style.top = "50%";
            dollarSpan.style.transform = "translateY(-50%)";
            dollarSpan.style.fontWeight = "700";
            dollarSpan.style.color = "#a68023";

            amountWrapper.appendChild(amountInput);
            amountWrapper.appendChild(dollarSpan);

            const percentSpan = document.createElement("span");
            percentSpan.id = `elOut${index}`;
            percentSpan.style.minWidth = "80px";
            percentSpan.style.flex = "0 0 90px";
            percentSpan.style.textAlign = "right";
            percentSpan.style.fontWeight = "700";
            percentSpan.style.color = "#a68023";

            const deleteBtn = document.createElement("button");
            deleteBtn.textContent = "✕";
            deleteBtn.style.border = "none";
            deleteBtn.style.background = "transparent";
            deleteBtn.style.color = "#a68023";
            deleteBtn.style.fontWeight = "900";
            deleteBtn.style.cursor = "pointer";
            deleteBtn.addEventListener("click", () => {
                categoriesContainer.removeChild(div);
                refreshExpenseLens();
            });

            // Format numbers with commas on blur
            amountInput.addEventListener("blur", () => {
                amountInput.value = formatNumber(amountInput.value);
            });

            amountInput.addEventListener("input", refreshExpenseLens);

            div.appendChild(nameInput);
            div.appendChild(amountWrapper);
            div.appendChild(percentSpan);
            div.appendChild(deleteBtn);
            categoriesContainer.appendChild(div);

            if (preAmount) refreshExpenseLens();
        };

        // -----------------------------
        // Refresh Function
        // -----------------------------
        const refreshExpenseLens = () => {
            const income = +elIncome.value.replace(/,/g,'') || 0;
            let totalSpent = 0;
            const categoriesData = [];

            document.querySelectorAll('[id^="elCatAmount"]').forEach(input => {
                const val = +input.value.replace(/,/g,'') || 0;
                totalSpent += val;
                const index = input.id.replace('elCatAmount','');
                const pct = income > 0 ? ((val/income)*100).toFixed(1)+'%' : '0%';
                document.getElementById(`elOut${index}`).textContent = pct;

                const name = (document.getElementById(`elCatName${index}`).value || `Category ${index}`).trim();
                categoriesData.push({ name, amount: val });
            });

            const remaining = income - totalSpent;
            const pct = income > 0 ? (totalSpent / income * 100) : 0;

            elMargin.textContent = `Remaining Balance: $${remaining.toLocaleString()}`;
            if (remaining >= 0) markIncome(elMargin);
            else markExpense(elMargin);

            if(pct > 1) {
                if(pct > 1 && pct <= 80) elTips.textContent = `✅ You are spending ${pct.toFixed(1)}% of your income. Good balance!`;
                else if(pct <= 100) elTips.textContent = `You are spending ${pct.toFixed(1)}% of your income. Consider trimming non-essentials.`;
                else elTips.textContent = `⚠️ You are overspending by ${(pct - 100).toFixed(1)}% of your income!`;
            } else {
                elTips.textContent = 'Monitor each category to identify areas to save or invest.';
            }

            saveExpenseLensState();

            // Push expenses + income into shared Finance Profile
            if (window.LegendFinanceProfile?.update) {
                window.LegendFinanceProfile.update({
                    monthlyNet: income || undefined,
                    expenses: categoriesData
                });
            }
        };

        // -----------------------------
        // Event Listeners
        // -----------------------------
        elIncome.addEventListener("input", refreshExpenseLens);
        elIncome.addEventListener("blur", () => { elIncome.value = formatNumber(elIncome.value); });

        addBtn.addEventListener("click", () => createCategoryRow(++categoryCount));
        delBtn.addEventListener("click", () => {
            const lastRow = categoriesContainer.lastElementChild;
            if(lastRow){
                categoriesContainer.removeChild(lastRow);
                refreshExpenseLens();
            }
        });

        addClearButton(container, () => {
            elIncome.value = '';
            categoriesContainer.innerHTML = '';
            categoryCount = 0;
            createCategoryRow(++categoryCount);
            elTips.textContent = 'Monitor each category to identify areas to save or invest.';
            elMargin.textContent = 'Remaining Balance: $0';
            clearExpenseLensState();
            hideTip();
            refreshExpenseLens();
        });

        await loadExpenseLensState();

        // Apply shared profile updates when fields are empty
        const applyProfileToExpenseLens = () => {
            const prof = window.LegendFinanceProfile?.get?.();
            if (!prof) return;

            // Only fill income if empty
            if (elIncome && !elIncome.value) {
                elIncome.value = prof.monthlyNet || prof.monthlyGross || '';
            }

            // If categories are empty or all blank, fill from profile
            const rows = Array.from(categoriesContainer.querySelectorAll('[id^=\"elCatRow\"]'));
            const allBlank = rows.length === 0 || rows.every(r => {
                const n = r.querySelector('[id^=\"elCatName\"]')?.value?.trim() || '';
                const a = r.querySelector('[id^=\"elCatAmount\"]')?.value?.trim() || '';
                return !n && !a;
            });
            if (allBlank) {
                categoriesContainer.innerHTML = '';
                categoryCount = 0;
                if (Array.isArray(prof.expenses) && prof.expenses.length) {
                    prof.expenses.forEach(exp => {
                        const amt = exp?.amount ?? '';
                        createCategoryRow(++categoryCount, exp?.name || `Expense ${categoryCount}`, amt);
                    });
                } else {
                    createCategoryRow(++categoryCount);
                }
                refreshExpenseLens();
            }
        };

        window.addEventListener("FinanceProfile:updated", applyProfileToExpenseLens);
        window.addEventListener("FinanceProfile:ready", applyProfileToExpenseLens);
        applyProfileToExpenseLens();

        // ✅ Color engine (no refresh needed)
        const applyExpenseLensColors = () => {
            // Inputs
            markIncome(elIncome);

            // Rows (dynamic)
            document.querySelectorAll('[id^="elCatName"]').forEach(n => markNeutral(n));     // labels
            document.querySelectorAll('[id^="elCatAmount"]').forEach(a => markExpense(a));  // spending
            document.querySelectorAll('[id^="elOut"]').forEach(p => markExpense(p));        // % outputs

            // Tips
            markNeutral(elTips);

            // Remaining Balance (based on current computed values)
            const income = +elIncome.value.replace(/,/g, '') || 0;
            let totalSpent = 0;
            document.querySelectorAll('[id^="elCatAmount"]').forEach(input => {
                totalSpent += (+input.value.replace(/,/g, '') || 0);
            });
            const remaining = income - totalSpent;

            if (remaining >= 0) markIncome(elMargin);
            else markExpense(elMargin);
        };

        // ✅ Force style application after DOM paint (this is what kills the “refresh page” issue)
        requestAnimationFrame(() => {
            applyExpenseLensColors();
            refreshExpenseLens();            // ensures Remaining Balance + tip text is current
            applyExpenseLensColors();        // re-apply after refresh updates DOM text
        });

    } catch (e) {
        console.error('ExpenseLens initialization error:', e);
    }
}



/* -------------------------------
    4️⃣ NET WORTH (ELEVATED)
--------------------------------*/
if (t.id === "NetWorth") {
    embedContainer.innerHTML = `
  <div class="networth-tool p-4" 
       style="background:#ffffff;
              border-radius:20px;
              box-shadow:0 12px 35px rgba(166,128,35,0.15);
              max-width:1200px; 
              margin:0 auto;
              font-family: 'Inter', sans-serif;">

        <!-- Tooltip styles (safe + isolated) -->
        <style>
            .nw-label{
                display:inline-flex;
                align-items:center;
                gap:8px;
                margin-bottom:6px;
                font-weight:800;
                color:#a68023;
            }
            .nw-i{
                display:inline-flex;
                align-items:center;
                justify-content:center;
                width:18px;
                height:18px;
                border-radius:999px;
                background:#fff;
                border:1px solid rgba(210,31,43,.9);
                color:#d21f2b;
                font-weight:900;
                font-size:12px;
                line-height:1;
                cursor:pointer;
                user-select:none;
                transform: translateY(-1px);
                box-shadow:0 6px 18px rgba(0,0,0,.08);
            }
            .nw-i:focus{
                outline:none;
                box-shadow:0 0 0 3px rgba(210,31,43,.18), 0 10px 25px rgba(0,0,0,.10);
            }
            #nwTipLayer{
                position:fixed;
                inset:0;
                pointer-events:none;
                z-index:2147483647;
            }
            .nw-tipbox{
                position:absolute;
                max-width:min(360px, 86vw);
                background:#fff;
                color:#111;
                border:1px solid rgba(0,0,0,.12);
                border-left:4px solid #d21f2b;
                padding:12px 12px;
                border-radius:14px;
                font-size:12.8px;
                font-weight:650;
                line-height:1.35;
                box-shadow:0 18px 45px rgba(0,0,0,.18);
                opacity:0;
                transform:translateY(6px);
                transition:opacity .12s ease, transform .12s ease;
                pointer-events:none;
                white-space:normal;
            }
            .nw-tipbox b{ font-weight:900; }
            .nw-tipbox.show{ opacity:1; transform:translateY(0); }
        </style>

        <div id="nwTipLayer"></div>
      
        <h3 class="fw-bold mb-3" style="color:#a68023; font-weight:900; letter-spacing:0.5px; font-size:2rem;">
            ${t.name}
        </h3>

        <p style="font-style:italic; color:#666; margin-bottom:18px;">
            Track your total assets, liabilities, and net worth. See insights to grow your wealth.
        </p>

        <div class="row mb-3" style="display:flex; gap:20px; flex-wrap:wrap;">
            <div style="flex:1; min-width:200px;">
                <div class="nw-label">
                    Total Assets
                    <span class="nw-i" tabindex="0"
                          data-tip="<b>Examples:</b> cash, investments, retirement accounts, property value (total value)">i</span>
                </div>
                <div style="position:relative;">
                    <input id="assets" type="text" class="form-control" placeholder="e.g., 150,000"
                           style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; font-size:1.1rem; color:#a68023; padding-right:30px;" />
                    <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">$</span>
                </div>
            </div>
            <div style="flex:1; min-width:200px;">
                <div class="nw-label">
                    Total Liabilities
                    <span class="nw-i" tabindex="0"
                          data-tip="<b>Examples:</b> credit cards, loans, mortgage balance, any debts owed (total)">i</span>
                </div>
                <div style="position:relative;">
                    <input id="liabs" type="text" class="form-control" placeholder="e.g., 50,000"
                           style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; font-size:1.1rem; color:#a68023; padding-right:30px;" />
                    <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">$</span>
                </div>
            </div>
        </div>

        <table class="table mt-3"
               style="background:#fafafa;
                      border-radius:12px;
                      overflow:hidden;
                      border:1px solid #eee; font-weight:700; font-size:1.1rem; color:#222;">
            <tr>
                <th>Assets</th>
                <th>Liabilities</th>
                <th>Net Worth</th>
            </tr>
            <tr>
                <td id="aVal">$0</td>
                <td id="lVal">$0</td>
                <td id="nVal">$0</td>
            </tr>
        </table>

        <table class="table mt-3"
               style="background:#fafafa;
                      border-radius:12px;
                      overflow:hidden;
                      border:1px solid #eee; font-weight:700; font-size:1.1rem; color:#222;">
            <tr>
                <th>Net Worth to Assets Ratio</th>
                <td id="nwRatio">0%</td>
            </tr>
            <tr>
                <th>Liabilities to Assets Ratio</th>
                <td id="liabRatio">0%</td>
            </tr>
            <tr>
                <th>Wealth Status</th>
                <td id="wealthStatus">—</td>
            </tr>
        </table>

        <div id="nwTips"
             style="padding:12px;
                    background:linear-gradient(135deg,#f1f3f6,#e4e7ec);
                    border-left:4px solid #a68023;
                    border-radius:8px;
                    font-style:italic;
                    color:#333;
                    margin-top:14px;">
            Enter your assets and liabilities to get personalized insights.
        </div>

    </div>`;

    const container = embedContainer.querySelector('.networth-tool');
    applyToolBoxStyles(container);
    await loadToolState('NetWorth');

    // ✅ TOOLTIP ENGINE (overlay)
    const tipLayer = document.getElementById('nwTipLayer');
    const tipBox = document.createElement('div');
    tipBox.className = 'nw-tipbox';
    tipLayer.appendChild(tipBox);

    const showTip = (el) => {
        const html = el.getAttribute('data-tip') || '';
        if (!html) return;

        tipBox.innerHTML = html;

        const r = el.getBoundingClientRect();
        const pad = 10;
        const boxW = Math.min(360, Math.floor(window.innerWidth * 0.86));

        let left = Math.min(window.innerWidth - boxW - pad, Math.max(pad, r.left - 10));
        tipBox.style.maxWidth = boxW + 'px';
        tipBox.style.left = left + 'px';

        tipBox.classList.add('show');
        const h = tipBox.getBoundingClientRect().height;

        let desiredTop = (r.top - h - 12);
        if (desiredTop < pad) desiredTop = (r.bottom + 12);

        tipBox.style.top = desiredTop + 'px';
    };

    const hideTip = () => tipBox.classList.remove('show');

    // Register for global click binder
    window.__LegendHideActiveTip = hideTip;

    container.querySelectorAll('.nw-i').forEach(el => {
        el.addEventListener('mouseenter', () => showTip(el));
        el.addEventListener('mouseleave', hideTip);
        el.addEventListener('focus', () => showTip(el));
        el.addEventListener('blur', hideTip);
        el.addEventListener('click', (e) => {
            e.stopPropagation();
            if (tipBox.classList.contains('show')) hideTip();
            else showTip(el);
        });
    });

    const assets = document.getElementById('assets');
    const liabs = document.getElementById('liabs');
    const aVal = document.getElementById('aVal');
    const lVal = document.getElementById('lVal');
    const nVal = document.getElementById('nVal');

    const nwRatio = document.getElementById('nwRatio');
    const liabRatio = document.getElementById('liabRatio');
    const wealthStatus = document.getElementById('wealthStatus');
    const nwTips = document.getElementById('nwTips');

    // ==============================
    // Format inputs with commas on blur
    // ==============================
    [assets, liabs].forEach(el => {
        el.addEventListener("blur", () => {
            let val = el.value.replace(/,/g, '');
            if (!isNaN(val) && val !== '') {
                el.value = Number(val).toLocaleString();
            }
        });
    });

    addClearButton(container, () => {
        assets.value = liabs.value = '';
        aVal.textContent = lVal.textContent = nVal.textContent = '$0';
        nwRatio.textContent = liabRatio.textContent = '0%';
        wealthStatus.textContent = '—';
        nwTips.textContent = 'Enter your assets and liabilities to get personalized insights.';
        clearToolState('NetWorth');
        hideTip();

        // ✅ repaint-safe color reset
        requestAnimationFrame(() => {
            applyNetWorthColors(0, 0, 0, 0, 0);
        });
    });

    function formatDollar(val) {
        return `$${val.toLocaleString()}`;
    }

    // ✅ Color engine (paint-safe, no refresh required)
    const applyNetWorthColors = (a, l, net, ratio, liabR) => {
        // Inputs
        markIncome(assets);   // assets = positive input
        markExpense(liabs);   // liabilities = “debt” input

        // Outputs
        markIncome(aVal);
        markExpense(lVal);

        if (net > 0) markIncome(nVal);
        else if (net < 0) markExpense(nVal);
        else markNeutral(nVal);

        // Ratios
        // Net Worth / Assets ratio (good if >0)
        if (ratio > 0) markIncome(nwRatio);
        else if (ratio < 0) markExpense(nwRatio);
        else markNeutral(nwRatio);

        // Liabilities / Assets ratio: green <=30, red >=50, gold in between
        if (liabR <= 30) markIncome(liabRatio);
        else if (liabR >= 50) markExpense(liabRatio);
        else markNeutral(liabRatio);

        // Status + tips neutral
        markNeutral(wealthStatus);
        markNeutral(nwTips);
    };

    function calc() {
        const a = +assets.value.replace(/,/g,'') || 0;
        const l = +liabs.value.replace(/,/g,'') || 0;
        const net = a - l;

        aVal.textContent = formatDollar(a);
        lVal.textContent = formatDollar(l);
        nVal.textContent = formatDollar(net);

        const ratio = a > 0 ? (net / a) * 100 : 0;
        const liabR = a > 0 ? (l / a) * 100 : 0;
        nwRatio.textContent = `${ratio.toFixed(1)}%`;
        liabRatio.textContent = `${liabR.toFixed(1)}%`;

        let status = '';
        if (net <= 0) status = '⚠️Negative Net Worth';
        else if (ratio < 25) status = '🔹 Early Stage';
        else if (ratio < 50) status = '🔸 Growing';
        else if (ratio < 75) status = '⭐ Solid';
        else status = 'Wealthy';
        wealthStatus.textContent = status;

        let tips = '';
        if (ratio < 25) tips += '💡 Focus on reducing liabilities and increasing savings.\n';
        else if (ratio < 50) tips += 'Your net worth is growing steadily; Maintain consistent financial habits.\n';
        else tips += '✅ Strong net worth! Continue smart asset allocation to preserve and grow wealth.\n';

        if (liabR > 50) tips += '⚠️ High liabilities relative to assets; consider risk mitigation planning.\n';
        nwTips.textContent = tips.trim();

        saveToolState('NetWorth');

        // ✅ apply colors immediately after compute
        applyNetWorthColors(a, l, net, ratio, liabR);
    }

    assets.oninput = liabs.oninput = calc;

    // ✅ paint-safe initial color apply (fixes “all gold until refresh”)
    calc(); // computes + paints + colors based on saved state
}

/* -------------------------------
    5️⃣ CASH FLOW MAP (ELEVATED)
--------------------------------*/
if (t.id === "CashFlow") {
    embedContainer.innerHTML = `
   <div class="networth-tool p-4"
        style="background:#ffffff;
               border-radius:20px;
               box-shadow:0 12px 35px rgba(166,128,35,0.15);
               max-width:1200px; 
               margin:0 auto;
               font-family: 'Inter', sans-serif;">

        <!-- Tooltip styles (safe + isolated) -->
        <style>
            .cf-label{
                display:inline-flex;
                align-items:center;
                gap:8px;
                margin-bottom:6px;
                font-weight:800;
                color:#a68023;
            }
            .cf-i{
                display:inline-flex;
                align-items:center;
                justify-content:center;
                width:18px;
                height:18px;
                border-radius:999px;
                background:#fff;
                border:1px solid rgba(210,31,43,.9);
                color:#d21f2b;
                font-weight:900;
                font-size:12px;
                line-height:1;
                cursor:pointer;
                user-select:none;
                transform: translateY(-1px);
                box-shadow:0 6px 18px rgba(0,0,0,.08);
            }
            .cf-i:focus{
                outline:none;
                box-shadow:0 0 0 3px rgba(210,31,43,.18), 0 10px 25px rgba(0,0,0,.10);
            }
            #cfTipLayer{
                position:fixed;
                inset:0;
                pointer-events:none;
                z-index:2147483647;
            }
            .cf-tipbox{
                position:absolute;
                max-width:min(360px, 86vw);
                background:#fff;
                color:#111;
                border:1px solid rgba(0,0,0,.12);
                border-left:4px solid #d21f2b;
                padding:12px 12px;
                border-radius:14px;
                font-size:12.8px;
                font-weight:650;
                line-height:1.35;
                box-shadow:0 18px 45px rgba(0,0,0,.18);
                opacity:0;
                transform:translateY(6px);
                transition:opacity .12s ease, transform .12s ease;
                pointer-events:none;
                white-space:normal;
            }
            .cf-tipbox b{ font-weight:900; }
            .cf-tipbox.show{ opacity:1; transform:translateY(0); }
        </style>

        <div id="cfTipLayer"></div>
       
        <h3 style="color:#a68023; font-weight:900; letter-spacing:0.5px; font-size:2rem;">
            ${t.name}
        </h3>

        <p style="font-style:italic; color:#666; margin-bottom:18px;">
            Understand your monthly cash flow and uncover opportunities to save or invest.
        </p>

        <div class="row mb-3" style="display:flex; gap:20px; flex-wrap:wrap;">
            <div style="flex:1; min-width:200px;">
                <div class="cf-label">
                    Monthly Income
                    <span class="cf-i" tabindex="0"
                          data-tip="<b>Examples:</b> 5,000 • 7,200 (total monthly take-home or reliable monthly income)">i</span>
                </div>
                <div style="position:relative;">
                    <input id="cfIncome" type="text" class="form-control"
                           placeholder="e.g., 5,000"
                           style="border:1px solid #d6c48a;
                                  box-shadow:inset 0 0 6px rgba(166,128,35,0.15);
                                  font-weight:700; font-size:1.1rem; color:#a68023; padding-right:30px;" />
                    <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">$</span>
                </div>
            </div>
            <div style="flex:1; min-width:200px;">
                <div class="cf-label">
                    Monthly Bills
                    <span class="cf-i" tabindex="0"
                          data-tip="<b>Examples:</b> 2,500 • 3,900 (fixed bills + minimum payments + essentials)">i</span>
                </div>
                <div style="position:relative;">
                    <input id="cfBills" type="text" class="form-control"
                           placeholder="e.g., 2,500"
                           style="border:1px solid #d6c48a;
                                  box-shadow:inset 0 0 6px rgba(166,128,35,0.15);
                                  font-weight:700; font-size:1.1rem; color:#a68023; padding-right:30px;" />
                    <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">$</span>
                </div>
            </div>
        </div>

        <h5 style="font-weight:700; margin-top:6px;">
            Net Cash Flow:
            <span id="cfResult" style="color:#222; font-weight:900;">$0</span>
        </h5>

        <table class="table mt-3"
               style="background:#fafafa;
                      border-radius:12px;
                      overflow:hidden;
                      border:1px solid #eee; font-weight:700; font-size:1.1rem; color:#222;">
            <tr>
                <th style="width:50%; background:#f3f3f3;">Savings Potential</th>
                <td id="cfSavingsPotential">$0</td>
            </tr>
            <tr>
                <th style="background:#f3f3f3;">Suggested Allocation</th>
                <td id="cfInvestPct">0%</td>
            </tr>
        </table>

        <div id="cfTips"
             style="padding:12px;
                    background:linear-gradient(135deg,#f1f3f6,#e4e7ec);
                    border-left:4px solid #a68023;
                    border-radius:8px;
                    font-style:italic;
                    color:#333;
                    margin-top:14px;">
            Enter your monthly income and bills to get personalized tips.
        </div>
    </div>`;

    const container = embedContainer.querySelector('.networth-tool');
    applyToolBoxStyles(container);
    await loadToolState('CashFlow');

    // ✅ TOOLTIP ENGINE (overlay)
    const tipLayer = document.getElementById('cfTipLayer');
    const tipBox = document.createElement('div');
    tipBox.className = 'cf-tipbox';
    tipLayer.appendChild(tipBox);

    const showTip = (el) => {
        const html = el.getAttribute('data-tip') || '';
        if (!html) return;

        tipBox.innerHTML = html;

        const r = el.getBoundingClientRect();
        const pad = 10;
        const boxW = Math.min(360, Math.floor(window.innerWidth * 0.86));

        let left = Math.min(window.innerWidth - boxW - pad, Math.max(pad, r.left - 10));
        tipBox.style.maxWidth = boxW + 'px';
        tipBox.style.left = left + 'px';

        tipBox.classList.add('show');
        const h = tipBox.getBoundingClientRect().height;

        let desiredTop = (r.top - h - 12);
        if (desiredTop < pad) desiredTop = (r.bottom + 12);

        tipBox.style.top = desiredTop + 'px';
    };

    const hideTip = () => tipBox.classList.remove('show');

    // Register for global click binder (from your TOP section)
    window.__LegendHideActiveTip = hideTip;

    container.querySelectorAll('.cf-i').forEach(el => {
        el.addEventListener('mouseenter', () => showTip(el));
        el.addEventListener('mouseleave', hideTip);
        el.addEventListener('focus', () => showTip(el));
        el.addEventListener('blur', hideTip);
        el.addEventListener('click', (e) => {
            e.stopPropagation();
            if (tipBox.classList.contains('show')) hideTip();
            else showTip(el);
        });
    });

    const cfIncome = document.getElementById('cfIncome');
    const cfBills = document.getElementById('cfBills');
    const cfResult = document.getElementById('cfResult');

    const cfSavingsPotential = document.getElementById('cfSavingsPotential');
    const cfInvestPct = document.getElementById('cfInvestPct');
    const cfTips = document.getElementById('cfTips');

    // Format inputs with commas on blur
    [cfIncome, cfBills].forEach(el => {
        el.addEventListener("blur", () => {
            let val = el.value.replace(/,/g, '');
            if (!isNaN(val) && val !== '') {
                el.value = Number(val).toLocaleString();
            }
        });
    });

    addClearButton(container, () => {
        cfIncome.value = cfBills.value = '';
        cfResult.textContent = '$0';
        cfSavingsPotential.textContent = '$0';
        cfInvestPct.textContent = '0%';
        cfTips.textContent = 'Enter your monthly income and bills to get personalized tips.';
        clearToolState('CashFlow');
        hideTip();

        // ✅ repaint-safe colors
        requestAnimationFrame(() => applyCashFlowColors(0, 0, 0, 0, 0));
    });

    function formatDollar(val) {
        return `$${val.toLocaleString()}`;
    }

    // ✅ Color engine (paint-safe, no refresh required)
    const applyCashFlowColors = (income, bills, net, savingsPotential, investPct) => {
        // Inputs
        markIncome(cfIncome);
        markExpense(cfBills);

        // Net cash flow
        if (net > 0) markIncome(cfResult);
        else if (net < 0) markExpense(cfResult);
        else markNeutral(cfResult);

        // Savings potential (0 should be neutral, not red)
        if (savingsPotential > 0) markIncome(cfSavingsPotential);
        else if (savingsPotential < 0) markExpense(cfSavingsPotential);
        else markNeutral(cfSavingsPotential);

        // Suggested allocation %: green if net positive, red if net negative, gold if zero
        if (net > 0) markIncome(cfInvestPct);
        else if (net < 0) markExpense(cfInvestPct);
        else markNeutral(cfInvestPct);

        // Tips neutral
        markNeutral(cfTips);
    };

    function calcCashFlow() {
        const income = +cfIncome.value.replace(/,/g,'') || 0;
        const bills = +cfBills.value.replace(/,/g,'') || 0;
        const net = income - bills;

        cfResult.textContent = formatDollar(net);

        const savingsPotential = Math.max(net * 0.5, 0);
        const investPct = income > 0 ? Math.min((net / income) * 100, 100).toFixed(0) : 0;

        cfSavingsPotential.textContent = formatDollar(savingsPotential);
        cfInvestPct.textContent = `${investPct}%`;

        let tips = '';
        if (net <= 0)
            tips = '⚠️ Your expenses exceed or equal your income. Reduce bills or increase income.';
        else if (net < income * 0.2)
            tips = '💡 Your net cash flow is tight. Focus on budgeting and increasing savings.';
        else
            tips = '✅ Strong cash flow. Use surplus funds strategically for savings and financial goals.';

        cfTips.textContent = tips;

        saveToolState('CashFlow');

        // ✅ apply colors immediately after compute
        applyCashFlowColors(income, bills, net, savingsPotential, investPct);
    }

    cfIncome.oninput = cfBills.oninput = calcCashFlow;

    // ✅ paint-safe initial compute + color (fixes “need refresh”)
    calcCashFlow();
}

/* -------------------------------
    6️⃣ DEBT CLARITY (ELEVATED)
--------------------------------*/
if (t.id === "DebtClarity") {
    embedContainer.innerHTML = `
   <div class="networth-tool p-4"
        style="background:#ffffff;
               border-radius:20px;
               box-shadow:0 12px 35px rgba(166,128,35,0.15);
               max-width:1200px; 
               margin:0 auto;
               font-family: 'Inter', sans-serif;">

        <!-- Tooltip styles (safe + isolated) -->
        <style>
            .dc-label{
                display:inline-flex;
                align-items:center;
                gap:8px;
                margin-bottom:6px;
                font-weight:800;
                color:#a68023;
            }
            .dc-i{
                display:inline-flex;
                align-items:center;
                justify-content:center;
                width:18px;
                height:18px;
                border-radius:999px;
                background:#fff;
                border:1px solid rgba(210,31,43,.9);
                color:#d21f2b;
                font-weight:900;
                font-size:12px;
                line-height:1;
                cursor:pointer;
                user-select:none;
                transform: translateY(-1px);
                box-shadow:0 6px 18px rgba(0,0,0,.08);
            }
            .dc-i:focus{
                outline:none;
                box-shadow:0 0 0 3px rgba(210,31,43,.18), 0 10px 25px rgba(0,0,0,.10);
            }
            #dcTipLayer{
                position:fixed;
                inset:0;
                pointer-events:none;
                z-index:2147483647;
            }
            .dc-tipbox{
                position:absolute;
                max-width:min(360px, 86vw);
                background:#fff;
                color:#111;
                border:1px solid rgba(0,0,0,.12);
                border-left:4px solid #d21f2b;
                padding:12px 12px;
                border-radius:14px;
                font-size:12.8px;
                font-weight:650;
                line-height:1.35;
                box-shadow:0 18px 45px rgba(0,0,0,.18);
                opacity:0;
                transform:translateY(6px);
                transition:opacity .12s ease, transform .12s ease;
                pointer-events:none;
                white-space:normal;
            }
            .dc-tipbox b{ font-weight:900; }
            .dc-tipbox.show{ opacity:1; transform:translateY(0); }
        </style>

        <div id="dcTipLayer"></div>
       
        <h3 style="color:#a68023; font-weight:900; letter-spacing:0.5px; font-size:2rem;">
            ${t.name}
        </h3>

        <p style="font-style:italic; color:#666; margin-bottom:18px;">
            Quickly calculate your Debt-to-Income (DTI) ratio and get actionable guidance.
        </p>

        <div class="row mb-3" style="display:flex; gap:20px; flex-wrap:wrap;">
            <div style="flex:1; min-width:200px;">
                <div class="dc-label">
                    Total Liabilities
                    <span class="dc-i" tabindex="0"
                          data-tip="<b>Examples:</b> 40,000 • 75,000 (total debts owed: loans, cards, etc.)">i</span>
                </div>
                <div style="position:relative;">
                    <input id="dcDebt" type="text" class="form-control"
                           placeholder="e.g., 40,000"
                           style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; font-size:1.1rem; color:#a68023; padding-right:30px;" />
                    <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">$</span>
                </div>
            </div>
            <div style="flex:1; min-width:200px;">
                <div class="dc-label">
                    Annual Income
                    <span class="dc-i" tabindex="0"
                          data-tip="<b>Examples:</b> 60,000 • 80,000 (gross annual income)">i</span>
                </div>
                <div style="position:relative;">
                    <input id="dcIncome" type="text" class="form-control"
                           placeholder="e.g., 80,000"
                           style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; font-size:1.1rem; color:#a68023; padding-right:30px;" />
                    <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">$</span>
                </div>
            </div>
        </div>

        <h5 style="font-weight:700; margin-top:8px;">
            DTI Ratio:
            <span id="dcResult" style="color:#222; font-weight:900;">0%</span>
        </h5>

        <table class="table mt-3"
               style="background:#fafafa;
                      border-radius:12px;
                      overflow:hidden;
                      border:1px solid #eee; font-weight:700; font-size:1.1rem; color:#222;">
            <tr>
                <th style="width:40%; background:#f3f3f3;">DTI Status</th>
                <td id="dcStatus">—</td>
            </tr>
            <tr>
                <th style="background:#f3f3f3;">Recommendation</th>
                <td id="dcTips">Enter your liabilities and income to receive guidance.</td>
            </tr>
        </table>
    </div>`;

    const container = embedContainer.querySelector('.networth-tool');
    applyToolBoxStyles(container);
    await loadToolState('DebtClarity');

    // ✅ TOOLTIP ENGINE (overlay)
    const tipLayer = document.getElementById('dcTipLayer');
    const tipBox = document.createElement('div');
    tipBox.className = 'dc-tipbox';
    tipLayer.appendChild(tipBox);

    const showTip = (el) => {
        const html = el.getAttribute('data-tip') || '';
        if (!html) return;

        tipBox.innerHTML = html;

        const r = el.getBoundingClientRect();
        const pad = 10;
        const boxW = Math.min(360, Math.floor(window.innerWidth * 0.86));

        let left = Math.min(window.innerWidth - boxW - pad, Math.max(pad, r.left - 10));
        tipBox.style.maxWidth = boxW + 'px';
        tipBox.style.left = left + 'px';

        tipBox.classList.add('show');
        const h = tipBox.getBoundingClientRect().height;

        let desiredTop = (r.top - h - 12);
        if (desiredTop < pad) desiredTop = (r.bottom + 12);

        tipBox.style.top = desiredTop + 'px';
    };

    const hideTip = () => tipBox.classList.remove('show');

    // Register for global click binder (from your TOP section)
    window.__LegendHideActiveTip = hideTip;

    container.querySelectorAll('.dc-i').forEach(el => {
        el.addEventListener('mouseenter', () => showTip(el));
        el.addEventListener('mouseleave', hideTip);
        el.addEventListener('focus', () => showTip(el));
        el.addEventListener('blur', hideTip);
        el.addEventListener('click', (e) => {
            e.stopPropagation();
            if (tipBox.classList.contains('show')) hideTip();
            else showTip(el);
        });
    });

    const dcDebt = document.getElementById('dcDebt');
    const dcIncome = document.getElementById('dcIncome');
    const dcResult = document.getElementById('dcResult');
    const dcStatus = document.getElementById('dcStatus');
    const dcTips = document.getElementById('dcTips');

    const applyProfileToDebtClarity = () => {
        const prof = window.LegendFinanceProfile?.get?.();
        if (!prof) return;
        if (dcIncome && !dcIncome.value) {
            const monthly = prof.monthlyGross || prof.monthlyNet;
            dcIncome.value = monthly ? (monthly * 12).toLocaleString() : '';
        }
        if (dcDebt && !dcDebt.value && prof.debtMinimums) {
            dcDebt.value = Number(prof.debtMinimums * 12 || 0).toLocaleString();
        }
        calcDebtClarity();
    };

    // Format inputs with commas on blur
    [dcDebt, dcIncome].forEach(el => {
        el.addEventListener("blur", () => {
            let val = el.value.replace(/,/g, '');
            if (!isNaN(val) && val !== '') {
                el.value = Number(val).toLocaleString();
            }
        });
    });

    // ✅ Color engine (paint-safe, no refresh required)
    const applyDebtClarityColors = (dtiNum) => {
        // Inputs
        markExpense(dcDebt);
        markIncome(dcIncome);

        // DTI output coloring
        if (dtiNum <= 30) markIncome(dcResult);
        else if (dtiNum >= 50) markExpense(dcResult);
        else markNeutral(dcResult);

        // Status matches DTI severity
        if (dtiNum <= 30) markIncome(dcStatus);
        else if (dtiNum >= 50) markExpense(dcStatus);
        else markNeutral(dcStatus);

        // Tips neutral (guidance text)
        markNeutral(dcTips);
    };

    addClearButton(container, () => {
        dcDebt.value = dcIncome.value = '';
        dcResult.textContent = '0%';
        dcStatus.textContent = '—';
        dcTips.textContent = 'Enter your liabilities and income to receive guidance.';
        clearToolState('DebtClarity');
        hideTip();

        // ✅ repaint-safe colors
        requestAnimationFrame(() => applyDebtClarityColors(0));
    });

    function calcDebtClarity() {
        const debt = +dcDebt.value.replace(/,/g,'') || 0;
        const income = +dcIncome.value.replace(/,/g,'') || 1;
        const dtiNum = (debt / income) * 100;
        const dti = dtiNum.toFixed(1);

        dcResult.textContent = `${dti}%`;

        let status = '';
        let tips = '';

        if (dtiNum > 50) {
            status = '⚠️ High DTI';
            tips = 'Work toward increasing income and reducing debt over time to avoid taking on new liabilities.';
        } else if (dtiNum > 30) {
            status = '🔹 Moderate DTI';
            tips = 'Monitor spending and pay down debt strategically (highest interest first or snowball).';
        } else {
            status = '✅ Healthy DTI';
            tips = 'Good balance. Stay disciplined and keep liabilities controlled.';
        }

        dcStatus.textContent = status;
        dcTips.textContent = tips;

        saveToolState('DebtClarity');

        if (window.LegendFinanceProfile?.update) {
            const debtMonthly = debt ? debt / 12 : undefined;
            const incomeMonthly = income ? income / 12 : undefined;
            window.LegendFinanceProfile.update({
                debtMinimums: debtMonthly,
                monthlyGross: incomeMonthly
            });
        }

        // ✅ apply colors immediately after compute
        applyDebtClarityColors(dtiNum);
    }

    dcDebt.oninput = dcIncome.oninput = calcDebtClarity;

    // ✅ paint-safe initial compute + color (fixes “need refresh”)
    calcDebtClarity();
    applyProfileToDebtClarity();
    window.addEventListener("FinanceProfile:updated", applyProfileToDebtClarity);
    window.addEventListener("FinanceProfile:ready", applyProfileToDebtClarity);
}


/* -------------------------------
    7️⃣ FINANCIAL BUFFER (ENHANCED & ELEVATED)
--------------------------------*/
if (t.id === "FinancialBuffer") {
    embedContainer.innerHTML = `
    <div class="networth-tool p-4"
         style="background:#ffffff;
                border-radius:20px;
                box-shadow:0 12px 35px rgba(166,128,35,0.12);
                border:1px solid rgba(166,128,35,0.35);
                max-width:600px;
                margin:0 auto;
                font-family:'Inter',sans-serif;">

        <!-- Tooltip styles (safe + isolated) -->
        <style>
            .fb-label{
                display:inline-flex;
                align-items:center;
                gap:8px;
                margin-bottom:6px;
                font-weight:800;
                color:#444;
            }
            .fb-i{
                display:inline-flex;
                align-items:center;
                justify-content:center;
                width:18px;
                height:18px;
                border-radius:999px;
                background:#fff;
                border:1px solid rgba(210,31,43,.9);
                color:#d21f2b;
                font-weight:900;
                font-size:12px;
                line-height:1;
                cursor:pointer;
                user-select:none;
                transform: translateY(-1px);
                box-shadow:0 6px 18px rgba(0,0,0,.08);
            }
            .fb-i:focus{
                outline:none;
                box-shadow:0 0 0 3px rgba(210,31,43,.18), 0 10px 25px rgba(0,0,0,.10);
            }
            #fbTipLayer{
                position:fixed;
                inset:0;
                pointer-events:none;
                z-index:2147483647;
            }
            .fb-tipbox{
                position:absolute;
                max-width:min(360px, 86vw);
                background:#fff;
                color:#111;
                border:1px solid rgba(0,0,0,.12);
                border-left:4px solid #d21f2b;
                padding:12px 12px;
                border-radius:14px;
                font-size:12.8px;
                font-weight:650;
                line-height:1.35;
                box-shadow:0 18px 45px rgba(0,0,0,.18);
                opacity:0;
                transform:translateY(6px);
                transition:opacity .12s ease, transform .12s ease;
                pointer-events:none;
                white-space:normal;
            }
            .fb-tipbox b{ font-weight:900; }
            .fb-tipbox.show{ opacity:1; transform:translateY(0); }
        </style>

        <div id="fbTipLayer"></div>

        <h3 style="color:#a68023; font-weight:900; letter-spacing:0.5px; margin-bottom:12px; font-size:1.8rem;">
            ${t.name}
        </h3>

        <p style="font-style:italic; color:#555; margin-bottom:18px;">
            Build a financial safety net to protect yourself from unexpected expenses.
        </p>

        <div class="fb-label">
            Monthly Bills
            <span class="fb-i" tabindex="0"
                  data-tip="<b>Examples:</b> 2,500 • 3,800 (rent/mortgage, utilities, insurance, minimum debt payments, essentials)">i</span>
        </div>
        <div style="position:relative; margin-bottom:15px;">
            <input id="fbBills" type="text" class="form-control mb-3" placeholder="e.g., 2,500"
                   style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#a68023; padding-right:30px;" />
            <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">$</span>
        </div>

        <div class="mb-3">
            <h5 style="margin-bottom:6px;">1 Month Goal: <span id="fb1">$0</span></h5>
            <h5 style="margin-bottom:6px;">3–6 Month Goal: <span id="fb3">$0</span></h5>
            <h5 style="margin-bottom:6px;">12 Month Goal: <span id="fb12">$0</span></h5>
        </div>

        <div id="fbTips"
             style="padding:12px;
                    background:linear-gradient(135deg,#f1f3f6,#e4e7ec);
                    border-left:4px solid #a68023;
                    border-radius:8px;
                    font-style:italic;
                    color:#333;
                    margin-top:14px;">
            Tip: Save consistently each month to build your buffer. Consider automating transfers to a separate emergency account.
        </div>
    </div>`;

    const container = embedContainer.querySelector('.networth-tool');
    applyToolBoxStyles(container);
    await loadToolState('FinancialBuffer');

    // ✅ TOOLTIP ENGINE (overlay)
    const tipLayer = document.getElementById('fbTipLayer');
    const tipBox = document.createElement('div');
    tipBox.className = 'fb-tipbox';
    tipLayer.appendChild(tipBox);

    const showTip = (el) => {
        const html = el.getAttribute('data-tip') || '';
        if (!html) return;

        tipBox.innerHTML = html;

        const r = el.getBoundingClientRect();
        const pad = 10;
        const boxW = Math.min(360, Math.floor(window.innerWidth * 0.86));

        let left = Math.min(window.innerWidth - boxW - pad, Math.max(pad, r.left - 10));
        tipBox.style.maxWidth = boxW + 'px';
        tipBox.style.left = left + 'px';

        tipBox.classList.add('show');
        const h = tipBox.getBoundingClientRect().height;

        let desiredTop = (r.top - h - 12);
        if (desiredTop < pad) desiredTop = (r.bottom + 12);

        tipBox.style.top = desiredTop + 'px';
    };

    const hideTip = () => tipBox.classList.remove('show');

    // Register for global click binder (from your TOP section)
    window.__LegendHideActiveTip = hideTip;

    container.querySelectorAll('.fb-i').forEach(el => {
        el.addEventListener('mouseenter', () => showTip(el));
        el.addEventListener('mouseleave', hideTip);
        el.addEventListener('focus', () => showTip(el));
        el.addEventListener('blur', hideTip);
        el.addEventListener('click', (e) => {
            e.stopPropagation();
            if (tipBox.classList.contains('show')) hideTip();
            else showTip(el);
        });
    });

    const fbBillsInput = document.getElementById('fbBills');
    const fb1 = document.getElementById('fb1');
    const fb3 = document.getElementById('fb3');
    const fb12 = document.getElementById('fb12');
    const fbTips = document.getElementById('fbTips');

    const applyProfileToFinancialBuffer = () => {
        const prof = window.LegendFinanceProfile?.get?.();
        if (!prof) return;
        if (fbBillsInput && !fbBillsInput.value) {
            const base =
                (prof.fixedExpenses || 0) +
                (prof.variableBudget || 0) +
                (prof.debtMinimums || 0);
            if (base > 0) fbBillsInput.value = base.toLocaleString();
        }
        updateBuffer();
    };

    const formatWithCommas = (val) => val ? (+val).toLocaleString() : '0';

    // Format input with commas on blur (consistent with other sections)
    fbBillsInput.addEventListener("blur", () => {
        let val = fbBillsInput.value.toString().replace(/,/g,'');
        if (!isNaN(val) && val !== '') fbBillsInput.value = Number(val).toLocaleString();
    });

    // ✅ Color painter (no refresh needed)
    const applyFinancialBufferColors = (billsNum) => {
        // Input: bills are an expense
        markExpense(fbBillsInput);

        // Outputs: goals are targets (neutral by your spec)
        markNeutral(fb1);
        markNeutral(fb3);
        markNeutral(fb12);

        // Tips neutral
        markNeutral(fbTips);

        // If you ever want a subtle warning color when bills=0, keep text logic only (colors stay consistent)
    };

    addClearButton(container, () => {
        fbBillsInput.value = '';
        fb1.textContent = '$0';
        fb3.textContent = '$0';
        fb12.textContent = '$0';
        fbTips.textContent = 'Tip: Save consistently each month to build your buffer. Consider automating transfers to a separate emergency account.';
        clearToolState('FinancialBuffer');
        hideTip();

        requestAnimationFrame(() => applyFinancialBufferColors(0));
    });

    const updateBuffer = () => {
        let bills = +fbBillsInput.value.toString().replace(/,/g,'') || 0;

        fb1.textContent = `$${formatWithCommas(bills)}`;
        fb3.textContent = `$${formatWithCommas(bills * 6)}`;
        fb12.textContent = `$${formatWithCommas(bills * 12)}`;

        if(bills <= 0) fbTips.textContent = '⚠️ Enter your monthly bills to calculate your buffer goals.';
        else if(bills < 1000) fbTips.textContent = 'Your bills are low; consider using this buffer to accelerate growth.';
        else fbTips.textContent = '✅ Your buffer goals are ready. Automate savings to reach these targets efficiently.';

        saveToolState('FinancialBuffer');

        if (window.LegendFinanceProfile?.update) {
            window.LegendFinanceProfile.update({
                emergencyTarget: bills * 6 || undefined
            });
        }

        // ✅ apply colors immediately after compute
        applyFinancialBufferColors(bills);
    };

    fbBillsInput.addEventListener('input', updateBuffer);

    // ✅ initial compute + paint (for persisted state)
    updateBuffer();
    applyProfileToFinancialBuffer();
    window.addEventListener("FinanceProfile:updated", applyProfileToFinancialBuffer);
    window.addEventListener("FinanceProfile:ready", applyProfileToFinancialBuffer);
}


/* -------------------------------
    8️⃣ WEALTH PROJECTION (ENHANCED & ELEVATED)
--------------------------------*/
if (t.id === "WealthProjection") {
    embedContainer.innerHTML = `
    <div class="networth-tool p-4"
         style="background:#ffffff;
                border-radius:20px;
                box-shadow:0 12px 35px rgba(166,128,35,0.15);
                border:1px solid rgba(166,128,35,0.35);
                max-width:600px;
                margin:0 auto;
                font-family:'Inter',sans-serif;">

        <!-- Tooltip styles (safe + isolated) -->
        <style>
            .wp-label{
                display:inline-flex;
                align-items:center;
                gap:8px;
                margin-bottom:6px;
                font-weight:800;
                color:#444;
            }
            .wp-i{
                display:inline-flex;
                align-items:center;
                justify-content:center;
                width:18px;
                height:18px;
                border-radius:999px;
                background:#fff;
                border:1px solid rgba(210,31,43,.9);
                color:#d21f2b;
                font-weight:900;
                font-size:12px;
                line-height:1;
                cursor:pointer;
                user-select:none;
                transform: translateY(-1px);
                box-shadow:0 6px 18px rgba(0,0,0,.08);
            }
            .wp-i:focus{
                outline:none;
                box-shadow:0 0 0 3px rgba(210,31,43,.18), 0 10px 25px rgba(0,0,0,.10);
            }
            #wpTipLayer{
                position:fixed;
                inset:0;
                pointer-events:none;
                z-index:2147483647;
            }
            .wp-tipbox{
                position:absolute;
                max-width:min(360px, 86vw);
                background:#fff;
                color:#111;
                border:1px solid rgba(0,0,0,.12);
                border-left:4px solid #d21f2b;
                padding:12px 12px;
                border-radius:14px;
                font-size:12.8px;
                font-weight:650;
                line-height:1.35;
                box-shadow:0 18px 45px rgba(0,0,0,.18);
                opacity:0;
                transform:translateY(6px);
                transition:opacity .12s ease, transform .12s ease;
                pointer-events:none;
                white-space:normal;
            }
            .wp-tipbox b{ font-weight:900; }
            .wp-tipbox.show{ opacity:1; transform:translateY(0); }
        </style>

        <div id="wpTipLayer"></div>

        <h3 style="color:#a68023; font-weight:900; letter-spacing:0.5px; margin-bottom:12px; font-size:1.8rem;">
            ${t.name}
        </h3>

        <p style="font-style:italic; color:#555; margin-bottom:18px;">
            Project your net worth growth based on current savings and surplus. Visualize both short and long-term potential.
        </p>

        <div class="wp-label">
            Current Net Worth
            <span class="wp-i" tabindex="0"
                  data-tip="<b>Examples:</b> 50,000 • 120,000 (assets minus liabilities today)">i</span>
        </div>
        <input id="wpNet" type="text" class="form-control mb-2" placeholder="e.g., 50,000"
               style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#a68023;" />

        <div class="wp-label">
            Monthly Surplus
            <span class="wp-i" tabindex="0"
                  data-tip="<b>Examples:</b> 500 • 2,000 (income minus expenses each month)">i</span>
        </div>
        <input id="wpSurplus" type="text" class="form-control mb-2" placeholder="e.g., 2,000"
               style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#a68023;" />

        <div class="wp-label">
            Custom Months
            <span class="wp-i" tabindex="0"
                  data-tip="<b>Examples:</b> 18 • 24 • 60 (how far out you want to project)">i</span>
        </div>
        <input id="wpMonths" type="number" class="form-control mb-3" placeholder="e.g., 18"
               style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#a68023;" />

        <div style="background:#fafafa; border-radius:12px; padding:14px; border:1px solid #eee; margin-bottom:10px;">
            <h5 style="font-weight:700;">
                Projected Net Worth (Custom Months): 
                <span id="wpOut" style="color:#a68023; font-weight:800;">$0</span>
            </h5>
            <h6 style="margin-top:8px;">
                Projection in 6 Months: 
                <span id="wp6" style="color:#a68023; font-weight:700;">$0</span>
            </h6>
            <h6>
                Projection in 12 Months: 
                <span id="wp12" style="color:#a68023; font-weight:700;">$0</span>
            </h6>
        </div>

        <div id="wpTips"
             style="padding:14px; 
                    background:linear-gradient(135deg, #f1ede3, #e1d6b8); 
                    border-left:5px solid #a68023; 
                    font-style:italic; 
                    color:#333; 
                    margin-top:15px; 
                    border-radius:10px;
                    box-shadow:inset 0 0 12px rgba(166,128,35,0.25);">
            Tip: Regularly increase your monthly surplus to accelerate your wealth growth.
        </div>
    </div>`;

    const container = embedContainer.querySelector('.networth-tool');
    applyToolBoxStyles(container);

    const wpNet = document.getElementById('wpNet');
    const wpSurplus = document.getElementById('wpSurplus');
    const wpMonths = document.getElementById('wpMonths');
    const wpOut = document.getElementById('wpOut');
    const wp6 = document.getElementById('wp6');
    const wp12 = document.getElementById('wp12');
    const wpTips = document.getElementById('wpTips');

    // ✅ TOOLTIP ENGINE (overlay)
    const tipLayer = document.getElementById('wpTipLayer');
    const tipBox = document.createElement('div');
    tipBox.className = 'wp-tipbox';
    tipLayer.appendChild(tipBox);

    const showTip = (el) => {
        const html = el.getAttribute('data-tip') || '';
        if (!html) return;

        tipBox.innerHTML = html;

        const r = el.getBoundingClientRect();
        const pad = 10;
        const boxW = Math.min(360, Math.floor(window.innerWidth * 0.86));

        let left = Math.min(window.innerWidth - boxW - pad, Math.max(pad, r.left - 10));
        tipBox.style.maxWidth = boxW + 'px';
        tipBox.style.left = left + 'px';

        tipBox.classList.add('show');
        const h = tipBox.getBoundingClientRect().height;

        let desiredTop = (r.top - h - 12);
        if (desiredTop < pad) desiredTop = (r.bottom + 12);

        tipBox.style.top = desiredTop + 'px';
    };

    const hideTip = () => tipBox.classList.remove('show');

    // Register for global click binder (from your TOP section)
    window.__LegendHideActiveTip = hideTip;

    container.querySelectorAll('.wp-i').forEach(el => {
        el.addEventListener('mouseenter', () => showTip(el));
        el.addEventListener('mouseleave', hideTip);
        el.addEventListener('focus', () => showTip(el));
        el.addEventListener('blur', hideTip);
        el.addEventListener('click', (e) => {
            e.stopPropagation();
            if (tipBox.classList.contains('show')) hideTip();
            else showTip(el);
        });
    });

    const formatWithCommas = (val) => val ? (+val).toLocaleString() : '0';
    const parseNumber = (val) => +val.toString().replace(/,/g,'') || 0;

    // --- PERSISTENCE ---
    const loadWP = async () => {
        const state = await loadPersistedState('WealthProjection');
        if(state.wpNet) wpNet.value = state.wpNet;
        if(state.wpSurplus) wpSurplus.value = state.wpSurplus;
        if(state.wpMonths) wpMonths.value = state.wpMonths;
        if(state.wpOut) wpOut.textContent = state.wpOut;
        if(state.wp6) wp6.textContent = state.wp6;
        if(state.wp12) wp12.textContent = state.wp12;
        if(state.wpTips) wpTips.textContent = state.wpTips;
    };
    const saveWP = () => {
        savePersistedState('WealthProjection', {
            wpNet: wpNet.value,
            wpSurplus: wpSurplus.value,
            wpMonths: wpMonths.value,
            wpOut: wpOut.textContent,
            wp6: wp6.textContent,
            wp12: wp12.textContent,
            wpTips: wpTips.textContent
        });
    };
    await loadWP();

    addClearButton(container, () => {
        wpNet.value = wpSurplus.value = wpMonths.value = '';
        wpOut.textContent = wp6.textContent = wp12.textContent = '$0';
        wpTips.textContent = 'Tip: Regularly increase your monthly surplus to accelerate your wealth growth.';
        clearPersistedState('WealthProjection');
        hideTip();
    });

    // ✅ Color painter (no refresh needed)
    const applyWealthProjectionColors = (netNum, surplusNum) => {
        // Inputs
        if (netNum > 0) markIncome(wpNet);
        else if (netNum < 0) markExpense(wpNet);
        else markNeutral(wpNet);

        if (surplusNum > 0) markIncome(wpSurplus);
        else if (surplusNum < 0) markExpense(wpSurplus);
        else markNeutral(wpSurplus);

        markNeutral(wpMonths);

        // Outputs: projections are “wealth”
        if (surplusNum > 0 || netNum > 0) {
            markIncome(wpOut);
            markIncome(wp6);
            markIncome(wp12);
        } else if (surplusNum < 0 || netNum < 0) {
            markExpense(wpOut);
            markExpense(wp6);
            markExpense(wp12);
        } else {
            markNeutral(wpOut);
            markNeutral(wp6);
            markNeutral(wp12);
        }

        // Tips neutral
        markNeutral(wpTips);
    };

    const updateWealthProjection = () => {
        let net = parseNumber(wpNet.value);
        let surplus = parseNumber(wpSurplus.value);
        let months = +wpMonths.value || 0;

        wpOut.textContent = `$${formatWithCommas(net + surplus * months)}`;
        wp6.textContent = `$${formatWithCommas(net + surplus * 6)}`;
        wp12.textContent = `$${formatWithCommas(net + surplus * 12)}`;

        if(net <= 0 && surplus <= 0) wpTips.textContent = '⚠️ Enter your current net worth and surplus to see projections.';
        else if(surplus <= 0) wpTips.textContent = '⚠️ Your surplus is zero; focus on increasing your savings to grow wealth.';
        else wpTips.textContent = '✅ Good! Keep adding to your surplus consistently to maximize growth over time.';

        saveWP();

        // ✅ apply colors immediately after compute
        applyWealthProjectionColors(net, surplus);
    };

    [wpNet, wpSurplus, wpMonths].forEach(input => {
        input.addEventListener('input', updateWealthProjection);
        input.addEventListener('blur', () => {
            if(input.id !== 'wpMonths') input.value = parseNumber(input.value).toLocaleString();
            updateWealthProjection();
        });
    });

    // ✅ initial compute + paint (for persisted state)
    updateWealthProjection();

    addClearButton(container, () => {
        wpNet.value = wpSurplus.value = wpMonths.value = '';
        wpOut.textContent = wp6.textContent = wp12.textContent = '$0';
        wpTips.textContent = 'Tip: Regularly increase your monthly surplus to accelerate your wealth growth.';
        clearPersistedState('WealthProjection');
        hideTip();

        requestAnimationFrame(() => applyWealthProjectionColors(0, 0));
    });
}

/* -------------------------------
    9️⃣ FREEDOM INDEX (ENHANCED & ELEVATED)
--------------------------------*/
if (t.id === "FreedomIndex") {
    embedContainer.innerHTML = `
    <div class="networth-tool p-4"
         style="background:#ffffff;
                border-radius:20px;
                box-shadow:0 12px 35px rgba(166,128,35,0.15);
                border:1px solid rgba(166,128,35,0.35);
                max-width:600px;
                margin:0 auto;
                font-family:'Inter',sans-serif;">

        <!-- Tooltip styles (safe + isolated) -->
        <style>
            .fi-label{
                display:inline-flex;
                align-items:center;
                gap:8px;
                margin-bottom:6px;
                font-weight:800;
                color:#444;
            }
            .fi-i{
                display:inline-flex;
                align-items:center;
                justify-content:center;
                width:18px;
                height:18px;
                border-radius:999px;
                background:#fff;
                border:1px solid rgba(210,31,43,.9);
                color:#d21f2b;
                font-weight:900;
                font-size:12px;
                line-height:1;
                cursor:pointer;
                user-select:none;
                transform: translateY(-1px);
                box-shadow:0 6px 18px rgba(0,0,0,.08);
            }
            .fi-i:focus{
                outline:none;
                box-shadow:0 0 0 3px rgba(210,31,43,.18), 0 10px 25px rgba(0,0,0,.10);
            }
            #fiTipLayer{
                position:fixed;
                inset:0;
                pointer-events:none;
                z-index:2147483647;
            }
            .fi-tipbox{
                position:absolute;
                max-width:min(360px, 86vw);
                background:#fff;
                color:#111;
                border:1px solid rgba(0,0,0,.12);
                border-left:4px solid #d21f2b;
                padding:12px 12px;
                border-radius:14px;
                font-size:12.8px;
                font-weight:650;
                line-height:1.35;
                box-shadow:0 18px 45px rgba(0,0,0,.18);
                opacity:0;
                transform:translateY(6px);
                transition:opacity .12s ease, transform .12s ease;
                pointer-events:none;
                white-space:normal;
            }
            .fi-tipbox b{ font-weight:900; }
            .fi-tipbox.show{ opacity:1; transform:translateY(0); }
        </style>

        <div id="fiTipLayer"></div>

        <h3 style="color:#a68023; font-weight:900; letter-spacing:0.5px; margin-bottom:12px; font-size:1.8rem;">
            ${t.name}
        </h3>

        <p style="font-style:italic; color:#555; margin-bottom:18px;">
            Measure your financial freedom: how long you could live off your net worth and passive income.
        </p>

        <div class="fi-label">
            Net Worth
            <span class="fi-i" tabindex="0"
                  data-tip="<b>What to enter:</b> Assets minus liabilities today. <b>Example:</b> 150,000">i</span>
        </div>
        <input id="fiNet" type="text" class="form-control mb-2" placeholder="e.g., 150,000"
               style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#a68023;" />

        <div class="fi-label">
            Annual Expenses
            <span class="fi-i" tabindex="0"
                  data-tip="<b>What to enter:</b> Your yearly cost of living. <b>Example:</b> 50,000 (≈ 4,167/mo)">i</span>
        </div>
        <input id="fiExp" type="text" class="form-control mb-2" placeholder="e.g., 50,000"
               style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#a68023;" />

        <div class="fi-label">
            Passive Income
            <span class="fi-i" tabindex="0"
                  data-tip="<b>Optional:</b> Annual passive income (rent, dividends, etc.). <b>Example:</b> 10,000">i</span>
        </div>
        <input id="fiPassive" type="text" class="form-control mb-3" placeholder="e.g., 10,000"
               style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#a68023;" />

        <h5 style="font-weight:700; margin-top:10px;">
            Freedom Index: <span id="fiOut" style="color:#a68023; font-weight:800;">0</span>
        </h5>

        <table class="table mt-3" style="background:#fafafa; border-radius:12px; overflow:hidden; border:1px solid #eee;">
            <tr><th style="width:45%; background:#f3f3f3;">Net Worth</th><td id="fiNetOut">$0</td></tr>
            <tr><th style="background:#f3f3f3;">Annual Expenses</th><td id="fiExpOut">$0</td></tr>
            <tr><th style="background:#f3f3f3;">Passive Income</th><td id="fiPassiveOut">$0</td></tr>
            <tr><th style="background:#f3f3f3;">Months of Freedom</th><td id="fiMonths">0</td></tr>
        </table>

        <div id="fiAdvice"
             style="padding:14px; background:linear-gradient(135deg, #f1ede3, #e1d6b8);
                    border-left:5px solid #a68023; font-style:italic; color:#333; margin-top:15px;
                    border-radius:10px; box-shadow:inset 0 0 12px rgba(166,128,35,0.25);">
            Enter your values to see recommendations.
        </div>
    </div>`;

    const container = embedContainer.querySelector('.networth-tool');
    applyToolBoxStyles(container);

    // ✅ TOOLTIP ENGINE (overlay)
    const tipLayer = document.getElementById('fiTipLayer');
    const tipBox = document.createElement('div');
    tipBox.className = 'fi-tipbox';
    tipLayer.appendChild(tipBox);

    const showTip = (el) => {
        const html = el.getAttribute('data-tip') || '';
        if (!html) return;

        tipBox.innerHTML = html;

        const r = el.getBoundingClientRect();
        const pad = 10;
        const boxW = Math.min(360, Math.floor(window.innerWidth * 0.86));

        let left = Math.min(window.innerWidth - boxW - pad, Math.max(pad, r.left - 10));
        tipBox.style.maxWidth = boxW + 'px';
        tipBox.style.left = left + 'px';

        tipBox.classList.add('show');
        const h = tipBox.getBoundingClientRect().height;

        let desiredTop = (r.top - h - 12);
        if (desiredTop < pad) desiredTop = (r.bottom + 12);

        tipBox.style.top = desiredTop + 'px';
    };

    const hideTip = () => tipBox.classList.remove('show');

    // Register for global click binder (from your TOP section)
    window.__LegendHideActiveTip = hideTip;

    container.querySelectorAll('.fi-i').forEach(el => {
        el.addEventListener('mouseenter', () => showTip(el));
        el.addEventListener('mouseleave', hideTip);
        el.addEventListener('focus', () => showTip(el));
        el.addEventListener('blur', hideTip);
        el.addEventListener('click', (e) => {
            e.stopPropagation();
            if (tipBox.classList.contains('show')) hideTip();
            else showTip(el);
        });
    });

    const fiNet = document.getElementById('fiNet');
    const fiExp = document.getElementById('fiExp');
    const fiPassive = document.getElementById('fiPassive');
    const fiOut = document.getElementById('fiOut');
    const fiNetOut = document.getElementById('fiNetOut');
    const fiExpOut = document.getElementById('fiExpOut');
    const fiPassiveOut = document.getElementById('fiPassiveOut');
    const fiMonths = document.getElementById('fiMonths');
    const fiAdvice = document.getElementById('fiAdvice');

    const formatWithCommas = (val) => val ? (+val).toLocaleString() : '0';
    const parseNumber = (val) => +val.toString().replace(/,/g,'') || 0;

    // --- PERSISTENCE ---
    const loadFI = async () => {
        const state = await loadPersistedState('FreedomIndex');
        if(state.fiNet) fiNet.value = state.fiNet;
        if(state.fiExp) fiExp.value = state.fiExp;
        if(state.fiPassive) fiPassive.value = state.fiPassive;
        if(state.fiOut) fiOut.textContent = state.fiOut;
        if(state.fiNetOut) fiNetOut.textContent = state.fiNetOut;
        if(state.fiExpOut) fiExpOut.textContent = state.fiExpOut;
        if(state.fiPassiveOut) fiPassiveOut.textContent = state.fiPassiveOut;
        if(state.fiMonths) fiMonths.textContent = state.fiMonths;
        if(state.fiAdvice) fiAdvice.textContent = state.fiAdvice;
    };
    const saveFI = () => {
        savePersistedState('FreedomIndex', {
            fiNet: fiNet.value,
            fiExp: fiExp.value,
            fiPassive: fiPassive.value,
            fiOut: fiOut.textContent,
            fiNetOut: fiNetOut.textContent,
            fiExpOut: fiExpOut.textContent,
            fiPassiveOut: fiPassiveOut.textContent,
            fiMonths: fiMonths.textContent,
            fiAdvice: fiAdvice.textContent
        });
    };
    await loadFI();

    // ✅ Color painter (no refresh needed)
    const applyFreedomColors = (netNum, expNum, passiveNum, fiNum, monthsNum) => {
        if (netNum > 0) markIncome(fiNet); else if (netNum < 0) markExpense(fiNet); else markNeutral(fiNet);

        // Expenses are red (always)
        markExpense(fiExp);

        if (passiveNum > 0) markIncome(fiPassive);
        else if (passiveNum < 0) markExpense(fiPassive);
        else markNeutral(fiPassive);

        if (netNum > 0) markIncome(fiNetOut); else if (netNum < 0) markExpense(fiNetOut); else markNeutral(fiNetOut);
        markExpense(fiExpOut);

        if (passiveNum > 0) markIncome(fiPassiveOut);
        else if (passiveNum < 0) markExpense(fiPassiveOut);
        else markNeutral(fiPassiveOut);

        if (fiNum >= 7) markIncome(fiOut);
        else if (fiNum <= 3) markExpense(fiOut);
        else markNeutral(fiOut);

        if (monthsNum >= 60) markIncome(fiMonths);
        else if (monthsNum <= 12) markExpense(fiMonths);
        else markNeutral(fiMonths);

        markNeutral(fiAdvice);
    };

    addClearButton(container, () => {
        fiNet.value = fiExp.value = fiPassive.value = '';
        fiOut.textContent = '0';
        fiNetOut.textContent = fiExpOut.textContent = fiPassiveOut.textContent = '$0';
        fiMonths.textContent = '0';
        fiAdvice.textContent = 'Enter your values to see recommendations.';
        clearPersistedState('FreedomIndex');
        hideTip();

        requestAnimationFrame(() => applyFreedomColors(0, 0, 0, 0, 0));
    });

    const updateFreedom = () => {
        const net = parseNumber(fiNet.value);
        const expRaw = parseNumber(fiExp.value);
        const exp = expRaw || 0; // for display
        const expDiv = expRaw || 1; // for division safety (keeps your logic stable)
        const passive = parseNumber(fiPassive.value);

        fiNetOut.textContent = `$${formatWithCommas(net)}`;
        fiExpOut.textContent = `$${formatWithCommas(exp)}`;
        fiPassiveOut.textContent = `$${formatWithCommas(passive)}`;

        const fi = (net / expDiv);
        fiOut.textContent = fi.toFixed(1);

        const months = Math.floor(((net + passive * 12) / expDiv) * 12);
        fiMonths.textContent = months;

        let advice = '';
        if (fi < 3) advice = '⚠️ Urgent: Increase savings and reduce expenses immediately.';
        else if (fi < 5) advice = 'Moderate: Keep growing assets, manage expenses wisely.';
        else if (fi < 7) advice = '✅ Good: You have partial financial freedom; keep building passive income.';
        else advice = '🌟 Excellent: Approaching full financial independence! Consider early investment opportunities.';

        fiAdvice.textContent = advice;

        applyFreedomColors(net, expDiv, passive, fi, months);
        saveFI();
    };

    [fiNet, fiExp, fiPassive].forEach(input => {
        input.addEventListener('input', updateFreedom);
        input.addEventListener('blur', () => {
            input.value = parseNumber(input.value).toLocaleString();
            updateFreedom();
        });
    });

    // ✅ initial compute + paint (for persisted state)
    updateFreedom();
}


/* -------------------------------
    🔟 DEBT VS ASSET PULSE (ENHANCED & ELEVATED)
--------------------------------*/
if (t.id === "DebtAssetPulse") {
    embedContainer.innerHTML = `
    <div class="networth-tool p-4"
         style="background:#ffffff;
                border-radius:20px;
                box-shadow:0 12px 35px rgba(166,128,35,0.15);
                border:1px solid rgba(166,128,35,0.35);
                max-width:600px;
                margin:0 auto;
                font-family:'Inter',sans-serif;">

        <!-- Tooltip styles (safe + isolated) -->
        <style>
            .dap-label{
                display:inline-flex;
                align-items:center;
                gap:8px;
                margin-bottom:6px;
                font-weight:800;
                color:#444;
            }
            .dap-i{
                display:inline-flex;
                align-items:center;
                justify-content:center;
                width:18px;
                height:18px;
                border-radius:999px;
                background:#fff;
                border:1px solid rgba(210,31,43,.9);
                color:#d21f2b;
                font-weight:900;
                font-size:12px;
                line-height:1;
                cursor:pointer;
                user-select:none;
                transform: translateY(-1px);
                box-shadow:0 6px 18px rgba(0,0,0,.08);
            }
            .dap-i:focus{
                outline:none;
                box-shadow:0 0 0 3px rgba(210,31,43,.18), 0 10px 25px rgba(0,0,0,.10);
            }
            #dapTipLayer{
                position:fixed;
                inset:0;
                pointer-events:none;
                z-index:2147483647;
            }
            .dap-tipbox{
                position:absolute;
                max-width:min(360px, 86vw);
                background:#fff;
                color:#111;
                border:1px solid rgba(0,0,0,.12);
                border-left:4px solid #d21f2b;
                padding:12px 12px;
                border-radius:14px;
                font-size:12.8px;
                font-weight:650;
                line-height:1.35;
                box-shadow:0 18px 45px rgba(0,0,0,.18);
                opacity:0;
                transform:translateY(6px);
                transition:opacity .12s ease, transform .12s ease;
                pointer-events:none;
                white-space:normal;
            }
            .dap-tipbox b{ font-weight:900; }
            .dap-tipbox.show{ opacity:1; transform:translateY(0); }
        </style>

        <div id="dapTipLayer"></div>

        <h3 style="color:#a68023; font-weight:900; letter-spacing:0.5px; margin-bottom:12px; font-size:1.8rem;">
            ${t.name}
        </h3>

        <p style="font-style:italic; color:#555; margin-bottom:18px;">
            Evaluate your financial health by comparing assets to liabilities and assess your risk.
        </p>

        <div class="dap-label">
            Total Assets
            <span class="dap-i" tabindex="0"
                  data-tip="<b>Examples:</b> 100,000 • 250,000 (cash, investments, retirement, property, etc.)">i</span>
        </div>
        <input id="dapA" type="text" class="form-control mb-2" placeholder="e.g., 100,000"
               style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#a68023;" />

        <div class="dap-label">
            Total Liabilities
            <span class="dap-i" tabindex="0"
                  data-tip="<b>Examples:</b> 50,000 • 180,000 (credit cards, loans, mortgage balance, etc.)">i</span>
        </div>
        <input id="dapL" type="text" class="form-control mb-2" placeholder="e.g., 50,000"
               style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#a68023;" />

        <div class="dap-label">
            Monthly Income
            <span class="dap-i" tabindex="0"
                  data-tip="<b>Optional:</b> Monthly income helps estimate how fast you could crush liabilities. <b>Example:</b> 6,000">i</span>
        </div>
        <input id="dapIncome" type="text" class="form-control mb-3" placeholder="e.g., 6,000"
               style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#a68023;" />

        <h5 style="font-weight:700; margin-top:10px;">
            Debt-to-Asset Ratio:
            <span id="dapOut" style="color:#a68023; font-weight:800;">0</span>
        </h5>

        <table class="table mt-3" style="background:#fafafa; border-radius:12px; overflow:hidden; border:1px solid #eee;">
            <tr><th style="width:45%; background:#f3f3f3;">Assets</th><td id="dapAssets">$0</td></tr>
            <tr><th style="background:#f3f3f3;">Liabilities</th><td id="dapLiabilities">$0</td></tr>
            <tr><th style="background:#f3f3f3;">Net Worth</th><td id="dapNetWorth">$0</td></tr>
            <tr><th style="background:#f3f3f3;">Monthly Income</th><td id="dapMonthlyIncome">$0</td></tr>
        </table>

        <div id="dapAdvice"
             style="padding:14px; background:linear-gradient(135deg, #f1ede3, #e1d6b8);
                    border-left:5px solid #a68023; font-style:italic; color:#333; margin-top:15px;
                    border-radius:10px; box-shadow:inset 0 0 12px rgba(166,128,35,0.25);">
            Enter values to get guidance on your financial health.
        </div>
    </div>`;

    const container = embedContainer.querySelector('.networth-tool');
    applyToolBoxStyles(container);

    // ✅ TOOLTIP ENGINE (overlay)
    const tipLayer = document.getElementById('dapTipLayer');
    const tipBox = document.createElement('div');
    tipBox.className = 'dap-tipbox';
    tipLayer.appendChild(tipBox);

    const showTip = (el) => {
        const html = el.getAttribute('data-tip') || '';
        if (!html) return;

        tipBox.innerHTML = html;

        const r = el.getBoundingClientRect();
        const pad = 10;
        const boxW = Math.min(360, Math.floor(window.innerWidth * 0.86));

        let left = Math.min(window.innerWidth - boxW - pad, Math.max(pad, r.left - 10));
        tipBox.style.maxWidth = boxW + 'px';
        tipBox.style.left = left + 'px';

        tipBox.classList.add('show');
        const h = tipBox.getBoundingClientRect().height;

        let desiredTop = (r.top - h - 12);
        if (desiredTop < pad) desiredTop = (r.bottom + 12);

        tipBox.style.top = desiredTop + 'px';
    };

    const hideTip = () => tipBox.classList.remove('show');

    // Register for global click binder (from your TOP section)
    window.__LegendHideActiveTip = hideTip;

    container.querySelectorAll('.dap-i').forEach(el => {
        el.addEventListener('mouseenter', () => showTip(el));
        el.addEventListener('mouseleave', hideTip);
        el.addEventListener('focus', () => showTip(el));
        el.addEventListener('blur', hideTip);
        el.addEventListener('click', (e) => {
            e.stopPropagation();
            if (tipBox.classList.contains('show')) hideTip();
            else showTip(el);
        });
    });

    const dapA = document.getElementById('dapA');
    const dapL = document.getElementById('dapL');
    const dapIncome = document.getElementById('dapIncome');
    const dapOut = document.getElementById('dapOut');
    const dapAssets = document.getElementById('dapAssets');
    const dapLiabilities = document.getElementById('dapLiabilities');
    const dapNetWorth = document.getElementById('dapNetWorth');
    const dapMonthlyIncome = document.getElementById('dapMonthlyIncome');
    const dapAdvice = document.getElementById('dapAdvice');

    const parseNumber = (v) => +v.toString().replace(/,/g,'') || 0;
    const formatWithCommas = (v) => v ? (+v).toLocaleString() : '0';

    /* ---------- PERSISTENCE ---------- */
    const loadDAP = async () => {
        const s = await loadPersistedState('DebtAssetPulse');
        if(s.dapA) dapA.value = s.dapA;
        if(s.dapL) dapL.value = s.dapL;
        if(s.dapIncome) dapIncome.value = s.dapIncome;
        if(s.dapOut) dapOut.textContent = s.dapOut;
        if(s.dapAssets) dapAssets.textContent = s.dapAssets;
        if(s.dapLiabilities) dapLiabilities.textContent = s.dapLiabilities;
        if(s.dapNetWorth) dapNetWorth.textContent = s.dapNetWorth;
        if(s.dapMonthlyIncome) dapMonthlyIncome.textContent = s.dapMonthlyIncome;
        if(s.dapAdvice) dapAdvice.textContent = s.dapAdvice;
    };

    const saveDAP = () => {
        savePersistedState('DebtAssetPulse', {
            dapA: dapA.value,
            dapL: dapL.value,
            dapIncome: dapIncome.value,
            dapOut: dapOut.textContent,
            dapAssets: dapAssets.textContent,
            dapLiabilities: dapLiabilities.textContent,
            dapNetWorth: dapNetWorth.textContent,
            dapMonthlyIncome: dapMonthlyIncome.textContent,
            dapAdvice: dapAdvice.textContent
        });
    };

    await loadDAP();

    // ✅ Color painter (no refresh needed)
    const applyDAPColors = (assetsNum, liabilitiesNum, incomeNum, ratioNum) => {
        // Inputs
        markIncome(dapA);
        markExpense(dapL);

        if (incomeNum > 0) markIncome(dapIncome);
        else if (incomeNum < 0) markExpense(dapIncome);
        else markNeutral(dapIncome);

        // Outputs (money)
        markIncome(dapAssets);
        markExpense(dapLiabilities);

        const netWorth = assetsNum - liabilitiesNum;
        if (netWorth > 0) markIncome(dapNetWorth);
        else if (netWorth < 0) markExpense(dapNetWorth);
        else markNeutral(dapNetWorth);

        if (incomeNum > 0) markIncome(dapMonthlyIncome);
        else if (incomeNum < 0) markExpense(dapMonthlyIncome);
        else markNeutral(dapMonthlyIncome);

        // Ratio output (assets/liabilities)
        if (ratioNum >= 2) markIncome(dapOut);
        else if (ratioNum <= 1) markExpense(dapOut);
        else markNeutral(dapOut);

        // Advice neutral
        markNeutral(dapAdvice);
    };

    addClearButton(container, () => {
        dapA.value = dapL.value = dapIncome.value = '';
        dapOut.textContent = '0';
        dapAssets.textContent = dapLiabilities.textContent =
        dapNetWorth.textContent = dapMonthlyIncome.textContent = '$0';
        dapAdvice.textContent = 'Enter values to get guidance on your financial health.';
        clearPersistedState('DebtAssetPulse');
        hideTip();

        requestAnimationFrame(() => applyDAPColors(0, 0, 0, 0));
    });

    const updateDAP = () => {
        const assets = parseNumber(dapA.value);
        const liabilities = parseNumber(dapL.value);
        const income = parseNumber(dapIncome.value);

        dapAssets.textContent = `$${formatWithCommas(assets)}`;
        dapLiabilities.textContent = `$${formatWithCommas(liabilities)}`;
        dapNetWorth.textContent = `$${formatWithCommas(assets - liabilities)}`;
        dapMonthlyIncome.textContent = `$${formatWithCommas(income)}`;

        // Keep your existing "ratio" meaning (assets/liabilities)
        const ratioNum = (liabilities > 0) ? (assets / liabilities) : (assets > 0 ? Infinity : 0);
        const ratioTxt = liabilities > 0 ? ratioNum.toFixed(2) : (assets > 0 ? '∞' : '0');
        dapOut.textContent = ratioTxt;

        let advice = '';
        if(liabilities > assets) advice = '⚠️ High risk: Liabilities exceed assets. Reduce debt immediately.';
        else if(assets <= liabilities * 1.25) advice = '⚠️ Caution: Assets barely cover liabilities.';
        else if(assets <= liabilities * 2) advice = 'Moderate: Assets exceed liabilities — keep building.';
        else advice = '✅ Healthy: Strong asset base relative to debt.';

        if(income > 0 && liabilities > 0) {
            const months = Math.ceil(liabilities / income);
            advice += ` You could cover liabilities in ~${months} month${months !== 1 ? 's' : ''}.`;
        }

        dapAdvice.textContent = advice;

        applyDAPColors(assets, liabilities, income, ratioNum);
        saveDAP();
    };

    [dapA, dapL, dapIncome].forEach(input => {
        input.addEventListener('input', updateDAP);
        input.addEventListener('blur', () => {
            input.value = formatWithCommas(parseNumber(input.value));
            updateDAP();
        });
    });

    // ✅ initial compute + paint (for persisted state)
    updateDAP();
    } // ✅ closes if (t.id === "DebtAssetPulse")

}); // ✅ closes dropdown.addEventListener("change", ...)

    const savedToolId = await loadSelectedToolId();
    if (savedToolId && tools.some(tool => tool.id === savedToolId)) {
        dropdown.value = savedToolId;
        dropdown.dispatchEvent(new Event("change"));
    } else {
        dropdown.value = "WealthForecast";
        dropdown.dispatchEvent(new Event("change"));
    }

}); // ✅ closes document.addEventListener("DOMContentLoaded", ...)
