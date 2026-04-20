// ── Global Finance Tools Theme ── injected once on load, covers every tool ──
(function injectFinanceToolsTheme() {
    if (document.getElementById('ft-dark-theme')) return;
    const s = document.createElement('style');
    s.id = 'ft-dark-theme';
    s.textContent = `
        #budget-embed input,
        #budget-embed select,
        #budget-embed textarea,
        #budget-embed input.form-control,
        #budget-embed select.form-control,
        #budget-embed .form-control,
        #budget-embed .form-select,
        .networth-tool input,
        .networth-tool select,
        .networth-tool textarea,
        .networth-tool input.form-control,
        .networth-tool select.form-control,
        .networth-tool .form-control,
        .networth-tool .form-select {
            background-color: rgba(255,255,255,.92) !important;
            border: 1.5px solid rgba(166,128,35,.38) !important;
            border-radius: 10px !important;
            box-shadow: inset 0 1px 0 rgba(255,255,255,.05) !important;
            transition: border-color .15s ease, box-shadow .15s ease !important;
        }
        #budget-embed input:focus,
        #budget-embed select:focus,
        #budget-embed textarea:focus,
        #budget-embed .form-control:focus,
        #budget-embed .form-select:focus,
        .networth-tool input:focus,
        .networth-tool select:focus,
        .networth-tool textarea:focus,
        .networth-tool .form-control:focus,
        .networth-tool .form-select:focus {
            border-color: #ddb457 !important;
            box-shadow: 0 0 0 3px rgba(221,180,87,.16) !important;
            outline: none !important;
        }
        #budget-embed input[type="date"],
        .networth-tool input[type="date"] { color-scheme: light; }
        #budget-embed .btn-outline-gold,
        .networth-tool .btn-outline-gold {
            background: linear-gradient(155deg, #0d1f42 0%, #0a1630 100%) !important;
            border: 1.5px solid rgba(199,153,49,.55) !important;
            border-radius: 10px !important;
            box-shadow: 0 4px 12px rgba(0,0,0,.22) !important;
        }
    `;
    document.head.appendChild(s);
})();

document.addEventListener("DOMContentLoaded", async function () {
    const dropdown = document.getElementById("budgetDropdown");
    const embedContainer = document.getElementById("budget-embed");
    const financeRoot = document.getElementById("financeRoot");
    const clientProfileId = financeRoot?.dataset.clientProfileId?.trim() || "";
    const clientUserId = financeRoot?.dataset.clientUserId?.trim() || "";
    const isBusinessClient = (financeRoot?.dataset.isBusinessClient || "").toLowerCase() === "true";
    const workspaceScope =
        clientUserId ||
        clientProfileId ||
        "client";
    const scopeKey = (key) => `legend-finance:${workspaceScope}:${key}`;
    const selectedToolStateId = "__workspace__";
    const storageGet = (key) => localStorage.getItem(scopeKey(key));
    const storageSet = (key, value) => localStorage.setItem(scopeKey(key), value);
    const storageRemove = (key) => localStorage.removeItem(scopeKey(key));
    const canUseServerState = clientUserId.length > 0 || clientProfileId.length > 0;
    const toolStateIds = new Set([
        "WealthForecast",
        "SavingsAccelerator",
        "BusinessSavingsAccelerator",
        "ExpenseLens",
        "BusinessExpenseLens",
        "NetWorth",
        "CashFlow",
        "DebtClarity",
        "FinancialBuffer",
        "WealthProjection",
        "FreedomIndex",
        "DebtAssetPulse"
    ]);

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

    function normalizePersistedState(key, value) {
        if (key !== "ActionTracker") return value ?? {};

        if (Array.isArray(value)) {
            return value.map(item => ({
                name: typeof item?.name === "string" ? item.name : "",
                done: Boolean(item?.done)
            }));
        }

        if (Array.isArray(value?.goals)) {
            return value.goals.map(item => ({
                name: typeof item?.name === "string" ? item.name : "",
                done: Boolean(item?.done)
            }));
        }

        if (Array.isArray(value?.items)) {
            return value.items.map(item => ({
                name: typeof item?.name === "string" ? item.name : "",
                done: Boolean(item?.done)
            }));
        }

        return [];
    }

    const buildQuery = (key) => {
        const params = new URLSearchParams({ toolId: key });
        if (clientUserId) params.set("clientUserId", clientUserId);
        if (clientProfileId) params.set("clientProfileId", clientProfileId);
        return params.toString();
    };

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

    async function loadPersistedState(key) {
        const keys = getStateKeys(key);

        if (canUseServerState) {
            for (const candidateKey of keys) {
                try {
                    const url = `/api/finance-state/load?${buildQuery(candidateKey)}`;
                    const res = await fetch(url, { credentials: "include" });
                    if (res.ok) {
                        const payload = await res.json();
                        if (payload?.found) {
                            return normalizePersistedState(candidateKey, JSON.parse(payload?.jsonState || "{}"));
                        }
                    }
                } catch (_) { }
            }
        }

        if (canUseServerState) return normalizePersistedState(key, {});

        for (const candidateKey of keys) {
            const raw = storageGet(candidateKey);
            if (raw) {
                return normalizePersistedState(candidateKey, JSON.parse(raw || "{}"));
            }
        }

        return normalizePersistedState(key, {});
    }

    const getAntiForgeryToken = () =>
        document.querySelector('#__af input[name="__RequestVerificationToken"]')?.value
        || document.querySelector('input[name="__RequestVerificationToken"]')?.value
        || "";

    function savePersistedState(key, state) {
        const normalizedState = normalizePersistedState(key, state);
        const jsonState = JSON.stringify(normalizedState ?? {});
        const primaryKey = getPrimaryStateKey(key);
        if (!canUseServerState) {
            storageSet(primaryKey, jsonState);
        }

        if (!canUseServerState) return;

        const token = getAntiForgeryToken();
        const buildHeaders = (contentType) => {
            const headers = contentType ? { "Content-Type": contentType } : {};
            if (token) headers["RequestVerificationToken"] = token;
            return headers;
        };

        const payload = { clientProfileId, clientUserId, toolId: primaryKey, jsonState };

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
            keys.forEach(storageRemove);
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

        container.style.boxSizing = 'border-box';
        container.style.overflow = 'visible';
        container.style.border = '1.8px solid rgba(166,128,35,.52)';
        container.style.borderRadius = '16px';
        container.style.background = 'radial-gradient(900px 320px at 0% 0%, rgba(166,128,35,.12), transparent 55%), linear-gradient(180deg, rgba(11,21,41,.99), rgba(15,29,56,.99))';
        container.style.boxShadow = '0 40px 100px rgba(0,0,0,.58)';
        container.style.margin = '0 auto 50px auto';
        container.style.color = '#f8fafc';
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
        ...(isBusinessClient ? [{ id: "BusinessSavingsAccelerator", name: "Business Savings Accelerator" }] : []),
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
const COLOR_NEUTRAL = "#1E3A8A";     // gold (for neutral inputs and tips)

function paint(el, color, weight = "800") {
  if (!el) return;
  el.style.setProperty("color", color, "important");
  el.style.setProperty("font-weight", weight, "important");
}

const COLOR_GOLD = "#a68023";
function markIncome(el)  { paint(el, COLOR_INCOME); }
function markExpense(el) { paint(el, COLOR_EXPENSE); }
function markNeutral(el) { paint(el, COLOR_NEUTRAL, "700"); }
function markGold(el)    { paint(el, COLOR_GOLD, "900"); }
function markWithSuffix(markFn, el) {
    if (!el) return;
    markFn(el);
    const sib = el.nextElementSibling;
    if (sib && sib.tagName === 'SPAN') markFn(sib);
}


    // ------------------- Tool Renderer -------------------
    dropdown.addEventListener("change", async function () {
        const t = tools.find(x => x.id === this.value);
        saveSelectedToolId(this.value || "");

        // clear UI
        embedContainer.innerHTML = '';

        // close any active tooltip cleanly
        if (typeof window.__LegendHideActiveTip === "function") window.__LegendHideActiveTip();

        if (!t) return;

        // ==========================================================
        // 1️⃣ WEALTH FORECAST (ELEVATED) + Tooltips
        // ==========================================================
        if (t.id === "WealthForecast") {
            await ensureChartJs();
            embedContainer.innerHTML = `
<div class="networth-tool" style="
    background: radial-gradient(900px 320px at 0% 0%, rgba(166,128,35,.12), transparent 55%), linear-gradient(180deg, rgba(11,21,41,.99), rgba(15,29,56,.99));
    padding:40px;
    border-radius:20px;
    box-shadow:0 40px 100px rgba(0,0,0,.58);
    border:1.8px solid rgba(166,128,35,.52);
    max-width:1200px;
    margin:0 auto;
    color:#f8fafc;
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
    </style>

    <div id="wbTipLayer"></div>

    <h3 style="color:#a68023; font-weight:900; font-size:2.2rem; margin-bottom:30px; letter-spacing:0.5px;">
        ${t.name}
    </h3>
    <div style="display:flex; flex-wrap:wrap; gap:50px;">
        <!-- Inputs Column -->
        <div style="flex:1; min-width:400px;">

            <label class="wb-label">
                Annual Income
                <span class="wb-i" tabindex="0" data-tip="<b>Examples:</b> 60,000 • 85,500 • 120,000 (gross annual pay)">i</span>
            </label>
            <div style="position:relative;">
                <input id="wbIncome" type="text" class="form-control" style="font-weight:700; font-size:1.1rem; color:#1E3A8A; padding-right:30px;" />
                <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#1E3A8A;">$</span>
            </div>

            <label class="wb-label">
                Working Period (Years)
                <span class="wb-i" tabindex="0" data-tip="<b>Examples:</b> 10 • 20 • 30 (years you plan to keep earning/saving)">i</span>
            </label>
            <input id="wbYears" type="text" class="form-control" style="font-weight:700; font-size:1.1rem; color:#1E3A8A;" />

            <label class="wb-label">
                Inflation
                <span class="wb-i" tabindex="0" data-tip="<b>Examples:</b> 2.5 • 3 • 4 (average annual inflation %)">i</span>
            </label>
            <div style="position:relative;">
                <input id="wbInflation" type="text" class="form-control" style="font-weight:700; font-size:1.1rem; color:#1E3A8A; padding-right:30px;" />
                <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#1E3A8A;">%</span>
            </div>

            <label class="wb-label">
                After-Tax Rate of Return
                <span class="wb-i" tabindex="0" data-tip="<b>Examples:</b> 5 • 7 • 9 (after-tax investment return %)">i</span>
            </label>
            <div style="position:relative;">
                <input id="wbReturn" type="text" class="form-control" style="font-weight:700; font-size:1.1rem; color:#1E3A8A; padding-right:30px;" />
                <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#1E3A8A;">%</span>
            </div>

            <label class="wb-label">
                Tax Bracket
                <span class="wb-i" tabindex="0" data-tip="<b>Examples:</b> 12 • 22 • 24 (effective/estimated rate %)">i</span>
            </label>
            <div style="position:relative;">
                <input id="wbTax" type="text" class="form-control" style="font-weight:700; font-size:1.1rem; color:#1E3A8A; padding-right:30px;" />
                <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#1E3A8A;">%</span>
            </div>

            <label class="wb-label">
                Fixed Liabilities
                <span class="wb-i" tabindex="0" data-tip="<b>Examples:</b> 10 • 18 • 25 (debt payments as % of income)">i</span>
            </label>
            <div style="position:relative;">
                <input id="wbLiabilities" type="text" class="form-control" style="font-weight:700; font-size:1.1rem; color:#1E3A8A; padding-right:30px;" />
                <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#1E3A8A;">%</span>
            </div>

            <label class="wb-label">
                Lifestyle Spending
                <span class="wb-i" tabindex="0" data-tip="<b>Examples:</b> 35 • 45 • 55 (living costs + wants as % of income)">i</span>
            </label>
            <div style="position:relative;">
                <input id="wbLifestyle" type="text" class="form-control" style="font-weight:700; font-size:1.1rem; color:#1E3A8A; padding-right:30px;" />
                <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#1E3A8A;">%</span>
            </div>

        </div>

        <!-- Outputs + Chart -->
        <div style="flex:1; min-width:420px;" class="wf-output-col">
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

            const earningsOut = document.getElementById("wbEarnings");
            const wealthOut = document.getElementById("wbWealth");
            const realGrowthOut = document.getElementById("wbRealGrowth");
            const savingsPercentOut = document.getElementById("wbSavingsPercent");

            const actualSavingsOut = document.getElementById("wbActualSavings");
            const savingsTipsOut = document.getElementById("wbSavingsTips");
            const chartEl = document.getElementById("wfChart");
            let wfChart = null;
            const wfLabelPlugin = {
                id: "wfLabelPlugin",
                afterDatasetsDraw(chart){
                    const {ctx, data} = chart;
                    const area = chart.chartArea;
                    const slots = [
                        { x: area.right - 8, y: area.top + 14 },           // wealth (green) near top
                        { x: area.right - 8, y: area.bottom - 14 }         // spending (red) near bottom
                    ];
                    ctx.save();
                    data.datasets.forEach((ds, i) => {
                        const val = ds.data?.[ds.data.length - 1];
                        if (val == null) return;
                        const label = `$${Number(val).toLocaleString()}`;
                        const slot = slots[i % slots.length];

                        // background pill
                        const padX = 6, padY = 4;
                        ctx.font = "bold 13px 'Inter', sans-serif";
                        const textW = ctx.measureText(label).width;
                        const boxW = textW + padX * 2;
                        const boxH = 20 + padY * 0;
                        const boxX = slot.x - boxW;
                        const boxY = slot.y - boxH / 2;
                        ctx.fillStyle = "rgba(15,23,42,0.85)";
                        ctx.strokeStyle = ds.borderColor || "#d1a034";
                        ctx.lineWidth = 1.2;
                        ctx.beginPath();
                        const r = 6;
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

                        // text
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
            [incomeEl, yearsEl, inflEl, retEl, taxEl, liabEl, lifeEl].forEach(el => {
                el.addEventListener("blur", () => {
                    let val = el.value.replace(/,/g, '').replace('%', '');
                    if (!isNaN(val) && val !== '') {
                        el.value = Number(val).toLocaleString();
                    }
                });
            });

            // Main calculation function
            function calcWealthForecast() {
                const income = +incomeEl.value.replace(/,/g, '').replace('%', '') || 0;
                const years = +yearsEl.value.replace(/,/g, '').replace('%', '') || 0;
                const inflation = (+inflEl.value.replace(/,/g, '').replace('%', '') || 0) / 100;
                const nominalReturn = (+retEl.value.replace(/,/g, '').replace('%', '') || 0) / 100;
                const tax = (+taxEl.value.replace(/,/g, '').replace('%', '') || 0) / 100;
                const liabilities = (+liabEl.value.replace(/,/g, '').replace('%', '') || 0) / 100;
                const lifestyle = (+lifeEl.value.replace(/,/g, '').replace('%', '') || 0) / 100;

                let savingsRate = 1 - tax - liabilities - lifestyle;
                if (savingsRate < 0) savingsRate = 0;

                const annualSavings = income * savingsRate;
                const annualSpend = income - annualSavings;
                const realGrowthRate = (1 + nominalReturn) / (1 + inflation) - 1;

                let investedBalance = 0;
                let cumulativeSpend = 0;
                const wealthPoints = [0];
                const spendPoints = [0];
                const labels = ["Year 0"];
                for (let y = 1; y <= years; y++) {
                    investedBalance = investedBalance * (1 + realGrowthRate) + annualSavings;
                    cumulativeSpend += annualSpend;
                    labels.push(`Year ${y}`);
                    wealthPoints.push(investedBalance);
                    spendPoints.push(-cumulativeSpend); // show spend as downward line
                }

                // Update outputs
                earningsOut.textContent = `$${(income * years).toLocaleString()}`;
                wealthOut.textContent = `$${investedBalance.toLocaleString()}`;
                realGrowthOut.textContent = `${(realGrowthRate * 100).toFixed(2)}%`;
                savingsPercentOut.textContent = `${(savingsRate * 100).toFixed(2)}%`;
                actualSavingsOut.textContent = `$${annualSavings.toLocaleString()}`;

// Inputs: income = green, % drains = red, years/return/inflation neutral
markWithSuffix(markIncome,  incomeEl);
markWithSuffix(markExpense, taxEl);
markWithSuffix(markExpense, liabEl);
markWithSuffix(markExpense, lifeEl);

markNeutral(yearsEl);
markWithSuffix(markNeutral, inflEl);
markWithSuffix(markNeutral, retEl);

// Outputs
markIncome(earningsOut);
markIncome(wealthOut);
markIncome(actualSavingsOut);

// Savings percent is good if > 0, otherwise red
if (savingsRate > 0) markIncome(savingsPercentOut);
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
                    } else {
                        wfChart.data.labels = labels;
                        wfChart.data.datasets[0].data = wealthPoints;
                        wfChart.data.datasets[1].data = spendPoints;
                        wfChart.update("none");
                    }
                }

                const sTips = savingsRate < 0.2
                    ? 'Savings potential is low; reduce lifestyle/fixed liabilities.'
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
            addClearButton(container, () => {
                [incomeEl, yearsEl, inflEl, retEl, taxEl, liabEl, lifeEl].forEach(el => el.value = '');
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
                    wfChart.update();
                }
                clearToolState(TOOL_KEY);
                hideTip();
            });

            // Initial calculation
            calcWealthForecast();
        }

// ==========================================================
// 2️⃣ SAVINGS ACCELERATOR (ELEVATED) + Tooltips
// ==========================================================
if (t.id === "SavingsAccelerator" || t.id === "BusinessSavingsAccelerator") {
    const isBusinessSavingsAccelerator = t.id === "BusinessSavingsAccelerator";
    const savingsToolStateId = isBusinessSavingsAccelerator ? "BusinessSavingsAccelerator" : "SavingsAccelerator";
    const linkedExpenseLensToolStateId = isBusinessSavingsAccelerator ? "BusinessExpenseLens" : "ExpenseLens";
    const linkedExpenseLensUpdatedEvent = `${linkedExpenseLensToolStateId}:updated`;
    const savingsSubtitle = isBusinessSavingsAccelerator
        ? "Track business cash flow separately from personal money and allocate operating surplus with clarity."
        : "Calculate your monthly surplus and optimize how you allocate it for maximum wealth building.";

    embedContainer.innerHTML = `
<div class="networth-tool p-4"
     style="background: radial-gradient(900px 320px at 0% 0%, rgba(166,128,35,.12), transparent 55%), linear-gradient(180deg, rgba(11,21,41,.99), rgba(15,29,56,.99));
            border-radius:20px;
            box-shadow:0 40px 100px rgba(0,0,0,.58);
            border:1.8px solid rgba(166,128,35,.52);
            max-width:1200px;
            margin:0 auto;
            color:#f8fafc;
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

    <p style="font-style:italic; color:#b9c5d8; margin-bottom:20px;">
        ${savingsSubtitle}
    </p>

    <div class="row mb-3" style="display:flex; gap:20px; flex-wrap:wrap;">
        <div style="flex:1; min-width:200px;">
            <div class="sa-label">
                ${isBusinessSavingsAccelerator ? "Business Net Cash Flow" : "Net Cash Flow"}
                <span class="sa-i" tabindex="0" data-tip="<b>Examples:</b> 3,800 • 5,200 (monthly take-home / net income)">i</span>
            </div>
            <div style="position:relative;">
                <input id="saNet" type="text" class="form-control" placeholder="e.g., 2,000"
                       style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; font-size:1.1rem; color:#1E3A8A; padding-right:30px;" />
                <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#1E3A8A;">$</span>
            </div>
        </div>
        <div style="flex:1; min-width:200px;">
            <div class="sa-label">
                ${isBusinessSavingsAccelerator ? "Business Essential Expenses" : "Essential Expenses"}
                <span class="sa-i" tabindex="0" data-tip="<b>Examples:</b> 2,100 • 3,000 (rent, utilities, food, transport, insurance)">i</span>
            </div>
            <div style="position:relative;">
                <input id="saEss" type="text" class="form-control" placeholder="e.g., 1,500"
                       style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; font-size:1.1rem; color:#1E3A8A; padding-right:30px;" />
                <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#1E3A8A;">$</span>
            </div>
        </div>
    </div>

    <h5 style="margin-top:10px; font-weight:700; color:#fff;">
        Surplus:
        <span id="saOut" style="color:#a68023; font-weight:900;">$0</span>
    </h5>

    <div class="mt-4">
        <h5 style="color:#a68023; font-weight:700; border-bottom:1px solid rgba(166,128,35,0.35); padding-bottom:6px;">
            Cash Flow Allocation
        </h5>

        <!-- New totals row: Remaining Surplus left on the left, Total Allocated on the right -->
        <div class="d-flex align-items-center mb-3" style="gap:8px;">
            <div style="flex:2; font-weight:700; color:#fff; text-align:left;">
                Remaining Surplus: <span id="saRemaining" style="color:#a68023; font-weight:900;">$0</span>
            </div>
            <div style="flex:1; text-align:right; font-weight:700; color:#fff;">
                Total Allocated: <span id="saPctTotal" style="color:#a68023; font-weight:900;">0%</span>
            </div>
        </div>

        <div id="allocationContainer" class="mt-3"></div>

        <div class="d-flex gap-2 mt-3">
            <button id="saAddCat" class="btn btn-outline-gold"
                    style="font-weight:600;">+ Add Category</button>
            <button id="saDelCat" class="btn btn-outline-gold"
                    style="font-weight:600;">- Delete Last</button>
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

    const parseSavingsMoney = (value) => +(String(value || '').replace(/,/g, '')) || 0;

    const normalizeSavingsBillFrequency = (value) => {
        const normalized = (value || '').toString().toLowerCase().replace(/[^a-z]/g, '');
        if (normalized === 'weekly') return 'weekly';
        if (normalized === 'biweekly') return 'biweekly';
        return 'monthly';
    };

    const getSavingsExpenseOccurrences = (category) => {
        const frequency = normalizeSavingsBillFrequency(category?.frequency || category?.recurrence);
        if (frequency === 'monthly') return 1;

        const due = category?.due || '';
        const parts = due.split('-').map(part => parseInt(part, 10));
        if (parts.length < 3 || parts.some(part => !Number.isFinite(part))) return 0;

        const now = new Date();
        const year = now.getFullYear();
        const month = now.getMonth();
        const days = new Date(year, month + 1, 0).getDate();
        const dueDate = new Date(parts[0], parts[1] - 1, parts[2]);
        let occurrences = 0;

        if (frequency === 'weekly') {
            const targetWeekday = dueDate.getDay();
            for (let day = 1; day <= days; day++) {
                if (new Date(year, month, day).getDay() === targetWeekday) occurrences++;
            }
            return occurrences;
        }

        for (let day = 1; day <= days; day++) {
            const diffDays = Math.round((new Date(year, month, day) - dueDate) / 86400000);
            if (diffDays % 14 === 0) occurrences++;
        }
        return occurrences;
    };

    const calculateExpenseLensMonthlyTotal = (state) => {
        const savedTotal = parseSavingsMoney(state?.monthlyExpenseTotal);
        if (savedTotal > 0) return savedTotal;
        return (state?.categories || []).reduce((sum, category) => {
            const amount = parseSavingsMoney(category?.amount);
            const occurrences = getSavingsExpenseOccurrences(category);
            return sum + (amount * occurrences);
        }, 0);
    };

    const applyExpenseLensToSavingsAccelerator = async () => {
        const state = await loadPersistedState(linkedExpenseLensToolStateId);
        const income = parseSavingsMoney(state?.income);
        const monthlyExpenses = calculateExpenseLensMonthlyTotal(state);

        if (income > 0) saNetInput.value = formatNumber(income);
        if (monthlyExpenses > 0) saEssInput.value = formatNumber(monthlyExpenses);
        refreshSurplus();
    };

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
        savePersistedState(savingsToolStateId, { net, ess, allocations });
    };

    const loadAllocationState = async () => {
        allocationContainer.innerHTML = '';
        categoryCount = 0;
        let created = 0;

        const state = await loadPersistedState(savingsToolStateId);
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

    const createAllocationRow = (index, preName = '', prePercent = '') => {
        const row = document.createElement('div');
        row.className = 'allocation-row d-flex align-items-center mb-2 gap-2';
        row.style.cssText = 'background:linear-gradient(180deg,rgba(255,255,255,.055),rgba(255,255,255,.02));padding:8px;border-radius:10px;border:1.5px solid rgba(166,128,35,.24);';

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
        amt.style.cssText = 'font-weight:700;';
        amt.style.setProperty('background', 'rgba(255,255,255,.92)', 'important');
        amt.style.setProperty('border', '1.5px solid rgba(166,128,35,.38)', 'important');
        amt.style.setProperty('color', '#1a2540', 'important');
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
        pct.style.cssText = 'font-weight:700;padding-right:28px;';
        pct.style.setProperty('background', 'rgba(255,255,255,.92)', 'important');
        pct.style.setProperty('border', '1.5px solid rgba(166,128,35,.38)', 'important');
        pct.style.setProperty('color', '#1a2540', 'important');
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
        markNeutral(name);
        markWithSuffix(markNeutral, pct);
        markWithSuffix(markNeutral, amt);
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

        // Inputs + suffix spans
        if (net > 0) markWithSuffix(markIncome, saNetInput);
        else if (net < 0) markWithSuffix(markExpense, saNetInput);
        else markWithSuffix(markNeutral, saNetInput);

        if (ess > 0) markWithSuffix(markExpense, saEssInput);
        else if (ess < 0) markWithSuffix(markIncome, saEssInput);
        else markWithSuffix(markNeutral, saEssInput);

        // Outputs
        if (surplus > 0) markIncome(saOut);
        else if (surplus < 0) markExpense(saOut);
        else markNeutral(saOut);

        if (usedPct >= 100) markExpense(saPctTotal); else markGold(saPctTotal);
        markGold(saRemaining);
        markNeutral(saTips);

        // Rows — percent input + % suffix, name, amount + $ suffix
        document.querySelectorAll('.allocation-percent').forEach(p => markWithSuffix(markNeutral, p));
        document.querySelectorAll('.allocation-name').forEach(n => markNeutral(n));

        document.querySelectorAll('.allocation-amount').forEach(a => {
            if (surplus > 0) markWithSuffix(markIncome, a);
            else if (surplus < 0) markWithSuffix(markExpense, a);
            else markWithSuffix(markNeutral, a);
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
        clearPersistedState(savingsToolStateId);
        hideTip();
        refreshSurplus();
    });

 await loadAllocationState();
 await applyExpenseLensToSavingsAccelerator();
 window.addEventListener(linkedExpenseLensUpdatedEvent, () => { applyExpenseLensToSavingsAccelerator(); });

// ✅ Force correct colors AFTER state load (so it stays green/red)
refreshSurplus();

}


/* -------------------------------
    3️⃣ EXPENSE LENS (ELEVATED)
--------------------------------*/
if (t.id === "ExpenseLens" || t.id === "BusinessExpenseLens") {
    try {
        const renderExpenseLensInstance = async (renderToolId, hostElement) => {
        const isBusinessExpenseLens = renderToolId === "BusinessExpenseLens";
        const expenseLensToolStateId = isBusinessExpenseLens ? "BusinessExpenseLens" : "ExpenseLens";
        const expenseLensUpdatedEvent = `${expenseLensToolStateId}:updated`;
        const expenseLensIdPrefix = isBusinessExpenseLens ? "elBusiness" : "elPersonal";
        const expenseLensTitle = isBusinessExpenseLens
            ? "Business Expense Lens"
            : (isBusinessClient ? "Personal Expense Lens" : "Expense Lens");
        const elId = (name) => `${expenseLensIdPrefix}${name}`;
        const elById = (name) => document.getElementById(elId(name));
        const expenseLensSubtitle = isBusinessExpenseLens
            ? "Separate business operating income and recurring business bills from personal expenses."
            : "Break down your income into categories and visualize spending percentages for better budgeting.";
        const expenseLensDefaultTip = isBusinessExpenseLens
            ? "Monitor business categories to identify operating costs, savings opportunities, and reinvestment capacity."
            : "Monitor each category to identify areas to save or invest.";

        hostElement.innerHTML = `
        <div class="networth-tool p-4"
             style="background: radial-gradient(900px 320px at 0% 0%, rgba(166,128,35,.12), transparent 55%), linear-gradient(180deg, rgba(11,21,41,.99), rgba(15,29,56,.99));
                    border-radius:20px;
                    box-shadow:0 40px 100px rgba(0,0,0,.58);
                    border:1.8px solid rgba(166,128,35,.52);
                    max-width:1200px; margin:0 auto;
                    color:#f8fafc;
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
                #${elId('TipLayer')}{
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

            <div id="${elId('TipLayer')}"></div>

            <h3 style="color:#a68023; font-weight:900; letter-spacing:0.5px; font-size:2rem;">
                ${expenseLensTitle}
            </h3>

            <p style="font-style:italic; color:#b9c5d8; margin-bottom:20px;">
                ${expenseLensSubtitle}
            </p>

            <div class="el-label">
                ${isBusinessExpenseLens ? "Business Total Income" : "Total Income"}
                <span class="el-i" tabindex="0"
                      data-tip="<b>Examples:</b> 4,500 • 6,200 (total monthly income before allocating categories)">i</span>
            </div>
            <div style="position:relative; margin-bottom:15px;">
                <input id="${elId('Income')}" type="text"
                       class="form-control mb-3"
                       placeholder="Enter total monthly income"
                       style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#1E3A8A; padding-right:30px;" />
                <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#1E3A8A;">$</span>
            </div>

            <div id="${elId('Categories')}" style="margin-top:10px; display:flex; flex-direction:column; gap:12px;"></div>

            <div class="d-flex gap-2 mt-3" style="gap:12px; flex-wrap:wrap;">
                <button id="${elId('AddCat')}"
                        class="btn btn-outline-gold"
                        style="font-weight:600;">
                    + Add Category
                </button>
                <button id="${elId('DelCat')}"
                        class="btn btn-outline-gold"
                        style="font-weight:600;">
                    - Delete Last
                </button>
            </div>

            <div id="${elId('Tips')}"
                 style="padding:14px; 
                        background:linear-gradient(135deg, #f1ede3, #e1d6b8); 
                        border-left:5px solid #a68023; 
                        font-style:italic; 
                        color:#333; 
                        margin-top:20px; 
                        border-radius:10px;
                        box-shadow:inset 0 0 12px rgba(166,128,35,0.25);">
                ${expenseLensDefaultTip}
            </div>

            <div id="${elId('Margin')}"
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

        const container = hostElement.querySelector('.networth-tool');
        const categoriesContainer = elById("Categories");
        const addBtn = elById("AddCat");
        const delBtn = elById("DelCat");
        const elTips = elById("Tips");
        const elMargin = elById("Margin");
        const elIncome = elById("Income");
       

       

        // Apply visual styles (matches the rest)
        applyToolBoxStyles(container);

        // ✅ TOOLTIP ENGINE (overlay)
        const tipLayer = elById('TipLayer');
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

        const EL_BILL_FREQUENCIES = [
            { value: 'monthly', label: 'Monthly' },
            { value: 'weekly', label: 'Weekly' },
            { value: 'biweekly', label: 'Bi-weekly' },
        ];

        const normalizeBillFrequency = (value) => {
            const normalized = (value || '').toString().toLowerCase().replace(/[^a-z]/g, '');
            if (normalized === 'weekly') return 'weekly';
            if (normalized === 'biweekly') return 'biweekly';
            return 'monthly';
        };

        const elFrequencyLabel = (value) => {
            const normalized = normalizeBillFrequency(value);
            return EL_BILL_FREQUENCIES.find(f => f.value === normalized)?.label || 'Monthly';
        };

        // -----------------------------
        // Default Expense Templates
        // -----------------------------
        const getDefaultPersonalExpenseRows = () => [
            'Rent / Mortgage','Property Taxes','Home Insurance','HOA',
            'Electricity','Water','Gas','Internet','Mobile Phone',
            'Groceries','Dining / Eating Out',
            'Auto Payment','Auto Insurance','Fuel','Auto Maintenance / Repairs',
            'Health Insurance','Medical / Prescriptions','Life Insurance','Disability Insurance',
            'Childcare','Tuition / School','Child Support / Alimony',
            'Personal / Household Items','Subscriptions','Entertainment / Recreation',
            'Gym / Fitness','Pet Expenses','Savings Contribution',
            'Debt Payment - Credit Cards','Debt Payment - Student Loans',
            'Debt Payment - Personal Loans','Miscellaneous'
        ].map(name => ({ name, amount: '', due: null, frequency: 'monthly', isTemplate: true }));

        const getDefaultBusinessExpenseRows = () => [
            'Rent / Lease','CAM / Property Costs','Utilities','Internet','Phone / Communications',
            'Payroll','Payroll Taxes','Contractors / 1099 Labor','Owner Draw / Owner Pay',
            'Insurance - General Liability','Insurance - Workers Comp','Insurance - Commercial Auto',
            'Professional Services - CPA / Bookkeeping','Professional Services - Legal',
            'Software / SaaS Subscriptions','Merchant Processing Fees','Advertising / Marketing',
            'Office Supplies','Equipment / Maintenance','Vehicle Expense / Fuel',
            'Travel','Meals / Entertainment','Inventory / Cost of Goods',
            'Shipping / Postage','Licenses / Permits','Taxes Set Aside',
            'Debt Payment - Business Loans','Bank Charges / Fees',
            'Training / Education','Miscellaneous'
        ].map(name => ({ name, amount: '', due: null, frequency: 'monthly', isTemplate: true }));

        const injectDefaultExpenseRows = () => {
            const defaults = isBusinessExpenseLens ? getDefaultBusinessExpenseRows() : getDefaultPersonalExpenseRows();
            defaults.forEach(row => {
                createCategoryRow(++categoryCount, row.name, row.amount, row.due, row.frequency, row.isTemplate);
            });
        };

        // -----------------------------
        // State Handling
        // -----------------------------
        const saveExpenseLensState = (extraState = {}) => {
            try {
                const income = elIncome.value || '';
                const categories = [];
                categoriesContainer.querySelectorAll(`[id^="${elId('CatRow')}"]`).forEach(row => {
                    const index = row.id.replace(elId('CatRow'), '');
                    const nameEl = elById(`CatName${index}`);
                    const amountEl = elById(`CatAmount${index}`);
                    const dueEl = elById(`CatDue${index}`);
                    const frequencyEl = elById(`CatFrequency${index}`);
                    const name = nameEl ? nameEl.value || '' : '';
                    const amount = amountEl ? amountEl.value || '' : '';
                    const due = dueEl ? dueEl.value || '' : '';
                    const frequency = normalizeBillFrequency(frequencyEl ? frequencyEl.value : 'monthly');
                    const isTemplate = row.dataset.isTemplate === 'true';
                    categories.push({ index, name, amount, due, frequency, isTemplate });
                });
                const state = { income, categories, ...extraState };
                savePersistedState(expenseLensToolStateId, state);
            } catch (e) { console.error(e); }
        };

        const loadExpenseLensState = async () => {
            try {
                const state = await loadPersistedState(expenseLensToolStateId);
                categoriesContainer.innerHTML = '';
                categoryCount = 0;
                let categoriesCreated = 0;

                if (state) {
                    elIncome.value = state.income || '';

                    if (state.categories && state.categories.length > 0) {
                        state.categories.forEach(cat => {
                            createCategoryRow(++categoryCount, cat.name, cat.amount, cat.due || '', cat.frequency || cat.recurrence, cat.isTemplate === true);
                            categoriesCreated++;
                        });
                    }
                }
                if (categoriesCreated === 0) injectDefaultExpenseRows();
                refreshExpenseLens();
            } catch (e) { console.error(e); }
        };

        const clearExpenseLensState = () => clearPersistedState(expenseLensToolStateId);

        // Active week filter (null = show all)
        let elActiveWeek = null;
        // Which week's detail is expanded in the panel (independent of filter)
        let elExpandedWeek = null;
        // Drag-and-drop state
        let elDragSrc = null;

        // -----------------------------
        // Due Date Helper — always current month, user picks the day
        // -----------------------------
        const toCurrentMonthDue = (savedDate) => {
            const now = new Date();
            const y = now.getFullYear();
            const m = String(now.getMonth() + 1).padStart(2, '0');
            const days = new Date(y, now.getMonth() + 1, 0).getDate();
            if (!savedDate) return `${y}-${m}-01`;
            const parsedDay = parseInt(savedDate.split('-')[2] || '1', 10);
            const clampedDay = Math.min(Math.max(Number.isFinite(parsedDay) ? parsedDay : 1, 1), days);
            const day = String(clampedDay).padStart(2, '0');
            return `${y}-${m}-${day}`;
        };

        const refreshExpenseLensViews = () => {
            if (elActiveWeek) {
                elApplyWeekFilter(elActiveWeek);
                return;
            }
            refreshExpenseLens();
            if (weekPanel?.style.display !== 'none') renderWeekPanel();
        };

        // -----------------------------
        // Create Category Row
        // -----------------------------
        const createCategoryRow = (index, preName = '', preAmount = '', preDue = '', preFrequency = 'monthly', isTemplate = false) => {
            const div = document.createElement("div");
            div.className = "d-flex align-items-center";
            div.id = `${elId('CatRow')}${index}`;
            div.dataset.isTemplate = isTemplate ? 'true' : 'false';
            div.style.background = "linear-gradient(180deg, rgba(255,255,255,.055), rgba(255,255,255,.02))";
            div.style.padding = "10px";
            div.style.borderRadius = "10px";
            div.style.border = "1.5px solid rgba(166,128,35,.24)";
            div.style.columnGap = "12px";
            div.style.rowGap = "10px";
            div.style.flexWrap = "wrap";

            const nameInput = document.createElement("input");
            nameInput.type = "text";
            nameInput.id = `${elId('CatName')}${index}`;
            nameInput.className = "form-control flex-grow-1";
            nameInput.placeholder = `Category ${index} Name`;
            nameInput.style.setProperty("background-color", "rgba(255,255,255,.92)", "important");
            nameInput.style.setProperty("border", "1.5px solid rgba(166,128,35,.38)", "important");
            nameInput.style.setProperty("border-radius", "10px", "important");
            nameInput.style.setProperty("box-shadow", "inset 0 1px 0 rgba(255,255,255,.05)", "important");
            nameInput.style.color = "#1E3A8A";
            nameInput.style.flex = "1 1 220px";
            nameInput.value = preName;
            nameInput.addEventListener("input", refreshExpenseLensViews);

            // Due date field
            const dueWrapper = document.createElement("div");
            dueWrapper.style.position = "relative";
            dueWrapper.style.flex = "1 1 140px";
            dueWrapper.style.minWidth = "130px";
            const dueInput = document.createElement("input");
            dueInput.type = "date";
            dueInput.id = `${elId('CatDue')}${index}`;
            dueInput.className = "form-control";
            dueInput.placeholder = "Due";
            dueInput.style.setProperty("background-color", "rgba(255,255,255,.92)", "important");
            dueInput.style.setProperty("border", "1.5px solid rgba(166,128,35,.38)", "important");
            dueInput.style.setProperty("border-radius", "10px", "important");
            dueInput.style.setProperty("box-shadow", "inset 0 1px 0 rgba(255,255,255,.05)", "important");
            dueInput.style.setProperty("color", "#0284C7", "important");
            dueInput.style.setProperty("font-weight", "700", "important");
            const resolvedPreFrequency = normalizeBillFrequency(preFrequency);
            const shouldPreserveDueDate = resolvedPreFrequency === 'weekly' || resolvedPreFrequency === 'biweekly';
            dueInput.value = shouldPreserveDueDate && preDue ? preDue : toCurrentMonthDue(preDue);
            dueInput.addEventListener("input", refreshExpenseLensViews);
            dueWrapper.appendChild(dueInput);

            const frequencySelect = document.createElement("select");
            frequencySelect.id = `${elId('CatFrequency')}${index}`;
            frequencySelect.className = "form-select";
            frequencySelect.style.setProperty("background-color", "rgba(255,255,255,.92)", "important");
            frequencySelect.style.setProperty("border", "1.5px solid rgba(166,128,35,.38)", "important");
            frequencySelect.style.setProperty("border-radius", "10px", "important");
            frequencySelect.style.setProperty("box-shadow", "inset 0 1px 0 rgba(255,255,255,.05)", "important");
            frequencySelect.style.setProperty("color", "#1E3A8A", "important");
            frequencySelect.style.setProperty("font-weight", "700", "important");
            frequencySelect.style.flex = "0 1 132px";
            frequencySelect.style.minWidth = "124px";
            EL_BILL_FREQUENCIES.forEach(option => {
                const opt = document.createElement("option");
                opt.value = option.value;
                opt.textContent = option.label;
                frequencySelect.appendChild(opt);
            });
            frequencySelect.value = resolvedPreFrequency;
            frequencySelect.addEventListener("change", refreshExpenseLensViews);

            const amountWrapper = document.createElement("div");
            amountWrapper.style.position = "relative";
            amountWrapper.style.flex = "1 1 150px";
            amountWrapper.style.minWidth = "140px";

            const amountInput = document.createElement("input");
            amountInput.type = "text";
            amountInput.id = `${elId('CatAmount')}${index}`;
            amountInput.className = "form-control";
            amountInput.placeholder = "Amount";
            amountInput.style.width = "100%";
            amountInput.style.setProperty("background-color", "rgba(255,255,255,.92)", "important");
            amountInput.style.setProperty("border", "1.5px solid rgba(166,128,35,.38)", "important");
            amountInput.style.setProperty("border-radius", "10px", "important");
            amountInput.style.setProperty("box-shadow", "inset 0 1px 0 rgba(255,255,255,.05)", "important");
            amountInput.style.fontWeight = "700";
            amountInput.style.color = "#1E3A8A";
            amountInput.value = preAmount;

            const dollarSpan = document.createElement("span");
            dollarSpan.textContent = "$";
            dollarSpan.style.position = "absolute";
            dollarSpan.style.right = "10px";
            dollarSpan.style.top = "50%";
            dollarSpan.style.transform = "translateY(-50%)";
            dollarSpan.style.fontWeight = "700";
            dollarSpan.style.color = "#1E3A8A";

            amountWrapper.appendChild(amountInput);
            amountWrapper.appendChild(dollarSpan);

            const percentSpan = document.createElement("span");
            percentSpan.id = `${elId('Out')}${index}`;
            percentSpan.style.minWidth = "80px";
            percentSpan.style.flex = "0 0 90px";
            percentSpan.style.textAlign = "right";
            percentSpan.style.fontWeight = "700";
            percentSpan.style.color = "#1E3A8A";

            const deleteBtn = document.createElement("button");
            deleteBtn.textContent = "✕";
            deleteBtn.style.border = "none";
            deleteBtn.style.background = "transparent";
            deleteBtn.style.color = "#1E3A8A";
            deleteBtn.style.fontWeight = "900";
            deleteBtn.style.cursor = "pointer";
            deleteBtn.addEventListener("click", () => {
                categoriesContainer.removeChild(div);
                refreshExpenseLensViews();
            });

            // Format numbers with commas on blur
            amountInput.addEventListener("blur", () => {
                amountInput.value = formatNumber(amountInput.value);
            });

            amountInput.addEventListener("input", refreshExpenseLensViews);

            // Drag handle — drag only activates from this grip, never from inputs
            const dragHandle = document.createElement("span");
            dragHandle.textContent = "⠿";
            dragHandle.title = "Drag to reorder";
            dragHandle.style.cssText = "cursor:grab;color:#1E3A8A;font-size:1.2rem;padding:0 4px 0 0;user-select:none;flex-shrink:0;opacity:0.5;";
            dragHandle.addEventListener("mousedown", () => { div.draggable = true; });
            dragHandle.addEventListener("mouseup",   () => { div.draggable = false; });

            // Drag events on the row
            div.draggable = false;
            div.addEventListener("dragstart", (e) => {
                elDragSrc = div;
                e.dataTransfer.effectAllowed = "move";
                setTimeout(() => { div.style.opacity = "0.4"; }, 0);
            });
            div.addEventListener("dragend", () => {
                div.style.opacity = "1";
                div.draggable = false;
                categoriesContainer.querySelectorAll(`[id^="${elId('CatRow')}"]`).forEach(r => {
                    r.style.border = "1.5px solid rgba(166,128,35,.24)";
                });
            });
            div.addEventListener("dragover", (e) => {
                e.preventDefault();
                if (div !== elDragSrc) div.style.border = "2px solid #ddb457";
            });
            div.addEventListener("dragleave", () => {
                div.style.border = "1.5px solid rgba(166,128,35,.24)";
            });
            div.addEventListener("drop", (e) => {
                e.preventDefault();
                if (elDragSrc && elDragSrc !== div) {
                    const rect = div.getBoundingClientRect();
                    const after = e.clientY > rect.top + rect.height / 2;
                    categoriesContainer.insertBefore(elDragSrc, after ? div.nextSibling : div);
                    div.style.border = "1.5px solid rgba(166,128,35,.24)";
                    refreshExpenseLensViews();
                }
            });

            div.appendChild(dragHandle);
            div.appendChild(nameInput);
            div.appendChild(dueWrapper);
            div.appendChild(frequencySelect);
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
            let monthlyTotalSpent = 0;
            const categoriesData = [];

            categoriesContainer.querySelectorAll(`[id^="${elId('CatAmount')}"]`).forEach(input => {
                const val = +input.value.replace(/,/g,'') || 0;
                const index = input.id.replace(elId('CatAmount'),'');
                const monthOccurrences = elGetBillOccurrenceDays(index);
                const activeOccurrences = elActiveWeek ? elGetBillOccurrenceDays(index, elActiveWeek) : monthOccurrences;
                const occurrenceCount = elActiveWeek ? activeOccurrences.length : monthOccurrences.length;
                const rowTotal = val * occurrenceCount;
                const monthlyTotal = val * monthOccurrences.length;
                monthlyTotalSpent += monthlyTotal;
                const pct = income > 0 ? ((rowTotal/income)*100).toFixed(1)+'%' : '0%';
                const pctEl = elById(`Out${index}`);
                pctEl.textContent = pct;
                const dollarSign = input.nextElementSibling;
                if (val > 0) { markExpense(input); markExpense(pctEl); if (dollarSign) markExpense(dollarSign); }
                else { markNeutral(input); markNeutral(pctEl); if (dollarSign) markNeutral(dollarSign); }

                const name = (elById(`CatName${index}`).value || `Category ${index}`).trim();
                const due = elById(`CatDue${index}`)?.value || '';
                const frequency = elGetBillFrequency(index);
                categoriesData.push({
                    name,
                    amount: monthlyTotal,
                    due,
                    frequency,
                    occurrenceAmount: val
                });

                if (elActiveWeek && occurrenceCount === 0) return;
                totalSpent += rowTotal;
            });

            const remaining = income - totalSpent;
            const pct = income > 0 ? (totalSpent / income * 100) : 0;

            if (elActiveWeek) {
                elMargin.textContent = `${elActiveWeek.label} Due: $${totalSpent.toLocaleString()}`;
                markExpense(elMargin);
            } else {
                elMargin.textContent = `Remaining Balance: $${remaining.toLocaleString()}`;
                if (remaining >= 0) markIncome(elMargin);
                else markExpense(elMargin);
            }

            if(pct > 1) {
                if(pct > 1 && pct <= 80) elTips.textContent = `✅ You are spending ${pct.toFixed(1)}% of your income. Good balance!`;
                else if(pct <= 100) elTips.textContent = `You are spending ${pct.toFixed(1)}% of your income. Consider trimming non-essentials.`;
                else elTips.textContent = `⚠️ You are overspending by ${(pct - 100).toFixed(1)}% of your income!`;
            } else {
                elTips.textContent = expenseLensDefaultTip;
            }

            saveExpenseLensState({ monthlyExpenseTotal: monthlyTotalSpent });
            window.dispatchEvent(new CustomEvent(expenseLensUpdatedEvent, {
                detail: {
                    income,
                    monthlyExpenseTotal: monthlyTotalSpent,
                    expenses: categoriesData
                }
            }));
        };

        // -----------------------------
        // Event Listeners
        // -----------------------------
        elIncome.addEventListener("input", refreshExpenseLens);
        elIncome.addEventListener("blur", () => { elIncome.value = formatNumber(elIncome.value); });

        addBtn.addEventListener("click", () => {
            createCategoryRow(++categoryCount);
            refreshExpenseLensViews();
        });
        delBtn.addEventListener("click", () => {
            const lastRow = categoriesContainer.lastElementChild;
            if(lastRow){
                categoriesContainer.removeChild(lastRow);
                refreshExpenseLensViews();
            }
        });

        // -----------------------------------------
        // Weekly Bill Tracker
        // -----------------------------------------
        const EL_WEEK_START_DAY = 0; // Sunday, matching the standard US calendar grid.

        const elMonthContext = () => {
            const now = new Date();
            const year = now.getFullYear();
            const month = now.getMonth();
            return {
                now,
                year,
                month,
                days: new Date(year, month + 1, 0).getDate(),
                monthLabel: now.toLocaleString('default', { month: 'short' }),
                monthYearLabel: now.toLocaleString('default', { month: 'long', year: 'numeric' })
            };
        };

        const elDaysInMonth = () => elMonthContext().days;

        const elBuildCalendarWeeks = () => {
            const ctx = elMonthContext();
            const weeks = [];
            let start = 1;

            while (start <= ctx.days) {
                const startDate = new Date(ctx.year, ctx.month, start);
                const calendarOffset = (startDate.getDay() - EL_WEEK_START_DAY + 7) % 7;
                const end = Math.min(ctx.days, start + (6 - calendarOffset));
                const weekNumber = weeks.length + 1;

                weeks.push({
                    id: `${ctx.year}-${String(ctx.month + 1).padStart(2, '0')}-${String(start).padStart(2, '0')}`,
                    label: `Week ${weekNumber}`,
                    start,
                    end,
                    year: ctx.year,
                    month: ctx.month,
                    rangeLabel: `${ctx.monthLabel} ${start}${end === start ? '' : `-${end}`}`,
                    isCurrent: ctx.now.getFullYear() === ctx.year
                        && ctx.now.getMonth() === ctx.month
                        && ctx.now.getDate() >= start
                        && ctx.now.getDate() <= end
                });

                start = end + 1;
            }

            return weeks;
        };

        const elGetCurrentCalendarWeek = () => elBuildCalendarWeeks().find(week => week.isCurrent) || null;
        const elSameCalendarWeek = (a, b) => Boolean(a && b && a.id === b.id);

        const elParseDueDate = (val) => {
            if (!val) return null;
            const parts = val.split('-').map(part => parseInt(part, 10));
            if (parts.length < 3 || parts.some(part => !Number.isFinite(part))) return null;
            return new Date(parts[0], parts[1] - 1, parts[2]);
        };

        const elGetBillFrequency = (index) => {
            const frequencyEl = elById(`CatFrequency${index}`);
            return normalizeBillFrequency(frequencyEl?.value || 'monthly');
        };

        const elGetBillOccurrenceDays = (index, week = null) => {
            const dueEl = elById(`CatDue${index}`);
            const dueDate = elParseDueDate(dueEl?.value);
            if (!dueDate) return [];

            const { year: y, month: m, days } = elMonthContext();
            const frequency = elGetBillFrequency(index);
            const inRange = (day) => !week || (day >= week.start && day <= week.end);
            const occurrences = [];

            if (frequency === 'monthly') {
                const day = Math.min(dueDate.getDate(), days);
                return inRange(day) ? [day] : [];
            }

            if (frequency === 'weekly') {
                const targetWeekday = dueDate.getDay();
                for (let day = 1; day <= days; day++) {
                    if (new Date(y, m, day).getDay() === targetWeekday && inRange(day)) {
                        occurrences.push(day);
                    }
                }
                return occurrences;
            }

            for (let day = 1; day <= days; day++) {
                const currentDate = new Date(y, m, day);
                const diffDays = Math.round((currentDate - dueDate) / 86400000);
                if (diffDays % 14 === 0 && inRange(day)) {
                    occurrences.push(day);
                }
            }
            return occurrences;
        };

        const elApplyWeekFilter = (week) => {
            elActiveWeek = week ? (elBuildCalendarWeeks().find(candidate => candidate.id === week.id) || week) : null;
            categoriesContainer.querySelectorAll(`[id^="${elId('CatRow')}"]`).forEach(row => {
                const idx = row.id.replace(elId('CatRow'), '');
                const show = !elActiveWeek || elGetBillOccurrenceDays(idx, elActiveWeek).length > 0;
                // Use setProperty with 'important' so the rule beats Bootstrap's d-flex !important
                if (show) {
                    row.style.removeProperty('display');
                } else {
                    row.style.setProperty('display', 'none', 'important');
                }
            });
            weeklyBtn.textContent = elActiveWeek ? `${elActiveWeek.label} ▾` : 'Weekly ▾';
            const _topBtn = elById('WeeklyBtnTop');
            if (_topBtn) _topBtn.textContent = elActiveWeek ? `${elActiveWeek.label} ▾` : 'Weekly ▾';
            refreshExpenseLens();
            renderWeekPanel();
        };

        const weekPanel = document.createElement('div');
        weekPanel.style.cssText = 'display:none;position:fixed;top:50%;left:50%;transform:translate(-50%,-50%);z-index:9999;background:#0b1529;border:1.5px solid #38BDF8;border-radius:14px;padding:22px 28px;width:600px;max-width:95vw;max-height:82vh;overflow-y:auto;box-shadow:0 16px 60px rgba(30,58,138,0.55);';
        document.body.appendChild(weekPanel);

        const renderWeekPanel = () => {
            const { monthLabel, monthYearLabel } = elMonthContext();
            const weeks = elBuildCalendarWeeks();
            weekPanel.innerHTML = '';

            // Header with close button
            const header = document.createElement('div');
            header.style.cssText = 'display:flex;justify-content:space-between;align-items:center;margin-bottom:12px;padding-bottom:10px;border-bottom:1px solid rgba(56,189,248,0.25);';
            const titleWrap = document.createElement('div');
            titleWrap.style.cssText = 'display:flex;flex-direction:column;gap:2px;';
            const title = document.createElement('span');
            title.style.cssText = 'color:#38BDF8;font-weight:800;font-size:0.95rem;letter-spacing:0.05em;';
            title.textContent = 'WEEKLY BILL TRACKER';
            const subtitle = document.createElement('span');
            subtitle.style.cssText = 'color:#94A3B8;font-size:0.72rem;font-weight:700;';
            subtitle.textContent = `Calendar weeks for ${monthYearLabel}`;
            const closeX = document.createElement('span');
            closeX.textContent = '✕';
            closeX.style.cssText = 'cursor:pointer;color:#64748B;font-size:1rem;font-weight:700;line-height:1;padding:2px 4px;';
            closeX.addEventListener('click', (e) => { e.stopPropagation(); weekPanel.style.display = 'none'; });
            titleWrap.appendChild(title);
            titleWrap.appendChild(subtitle);
            header.appendChild(titleWrap);
            header.appendChild(closeX);
            weekPanel.appendChild(header);

            // Pre-compute grand total for "Show All" row — reads live DOM so it always reflects current bills
            let grandTotal = 0;
            let grandCount = 0;
            categoriesContainer.querySelectorAll(`[id^="${elId('CatRow')}"]`).forEach(row => {
                const idx = row.id.replace(elId('CatRow'), '');
                const amtEl = elById(`CatAmount${idx}`);
                const amt = +(amtEl?.value || '').replace(/,/g, '') || 0;
                const occurrences = elGetBillOccurrenceDays(idx);
                if (amt > 0 && occurrences.length > 0) {
                    grandTotal += amt * occurrences.length;
                    grandCount += occurrences.length;
                }
            });

            // Show All row
            const allRow = document.createElement('div');
            allRow.style.cssText = `cursor:pointer;padding:9px 12px;border-radius:8px;font-weight:700;font-size:0.83rem;margin-bottom:8px;display:flex;justify-content:space-between;align-items:center;${!elActiveWeek ? 'background:#38BDF8;color:#0b1529;' : 'color:#38BDF8;'}`;

            const allRowLeft = document.createElement('span');
            allRowLeft.style.cssText = 'font-weight:700;font-size:0.82rem;';
            allRowLeft.textContent = 'Show All Bills';

            const allRowRight = document.createElement('span');
            allRowRight.style.cssText = `font-weight:800;font-size:0.85rem;color:${!elActiveWeek ? '#0b1529' : (grandCount > 0 ? '#38BDF8' : '#64748B')};`;
            allRowRight.textContent = grandCount > 0 ? `$${grandTotal.toLocaleString()}  (${grandCount} bill${grandCount !== 1 ? 's' : ''})` : '—';

            allRow.appendChild(allRowLeft);
            allRow.appendChild(allRowRight);
            allRow.addEventListener('click', (e) => { e.stopPropagation(); elExpandedWeek = null; elApplyWeekFilter(null); });
            weekPanel.appendChild(allRow);

            weeks.forEach(week => {
                let weekTotal = 0;
                const bills = [];
                categoriesContainer.querySelectorAll(`[id^="${elId('CatRow')}"]`).forEach(row => {
                    const idx = row.id.replace(elId('CatRow'), '');
                    const amtEl  = elById(`CatAmount${idx}`);
                    const nameEl = elById(`CatName${idx}`);
                    const frequency = elGetBillFrequency(idx);
                    const occurrences = elGetBillOccurrenceDays(idx, week);
                    occurrences.forEach(day => {
                        const amt = +(amtEl?.value || '').replace(/,/g, '') || 0;
                        if (amt <= 0) return;
                        weekTotal += amt;
                        bills.push({
                            name: nameEl?.value?.trim() || '(Unnamed)',
                            amount: amt,
                            day,
                            frequency
                        });
                    });
                });
                bills.sort((a, b) => a.day - b.day);
                const billCount = bills.length;
                const isActive   = elSameCalendarWeek(elActiveWeek, week);
                const isExpanded = elSameCalendarWeek(elExpandedWeek, week);

                const weekBlock = document.createElement('div');
                weekBlock.style.cssText = 'border-radius:10px;margin-bottom:6px;overflow:hidden;border:1px solid rgba(56,189,248,0.1);';

                // Summary row
                const summaryRow = document.createElement('div');
                summaryRow.style.cssText = `display:flex;justify-content:space-between;align-items:center;padding:9px 12px;cursor:pointer;${isActive ? 'background:#1E3A8A;' : 'background:rgba(255,255,255,0.04);'}`;

                const wLabel = document.createElement('span');
                wLabel.style.cssText = `font-weight:700;font-size:0.82rem;color:${isActive ? '#fff' : '#E0F2FE'};`;
                wLabel.textContent = `${week.label}  (${week.rangeLabel})`;

                const rightGroup = document.createElement('div');
                rightGroup.style.cssText = 'display:flex;align-items:center;gap:10px;';

                const amtSpan = document.createElement('span');
                amtSpan.style.cssText = `font-weight:800;font-size:0.85rem;color:${billCount > 0 ? '#38BDF8' : '#64748B'};`;
                amtSpan.textContent = billCount > 0 ? `$${weekTotal.toLocaleString()}  (${billCount} bill${billCount !== 1 ? 's' : ''})` : '—';
                rightGroup.appendChild(amtSpan);

                // Chevron always shown so every row is clearly clickable
                const chevron = document.createElement('span');
                chevron.textContent = isExpanded ? '▴' : '▾';
                chevron.style.cssText = 'color:#38BDF8;font-size:0.75rem;user-select:none;';
                rightGroup.appendChild(chevron);

                summaryRow.appendChild(wLabel);
                summaryRow.appendChild(rightGroup);

                // Detail container — always built and appended
                const detailWrap = document.createElement('div');
                detailWrap.style.cssText = `display:${isExpanded ? 'block' : 'none'};`;

                if (billCount > 0) {
                    // Column header
                    const colHeader = document.createElement('div');
                    colHeader.style.cssText = 'display:flex;padding:5px 12px 4px 20px;border-bottom:1px solid rgba(56,189,248,0.12);';
                    colHeader.innerHTML = '<span style="flex:1;font-size:0.7rem;color:#475569;font-weight:700;text-transform:uppercase;letter-spacing:0.06em;">Bill</span><span style="min-width:60px;text-align:center;font-size:0.7rem;color:#475569;font-weight:700;text-transform:uppercase;letter-spacing:0.06em;">Due</span><span style="min-width:80px;text-align:right;font-size:0.7rem;color:#475569;font-weight:700;text-transform:uppercase;letter-spacing:0.06em;">Amount</span>';
                    detailWrap.appendChild(colHeader);

                    bills.forEach((bill, i) => {
                        const billRow = document.createElement('div');
                        billRow.style.cssText = `display:flex;align-items:center;padding:7px 12px 7px 20px;${i < bills.length - 1 ? 'border-bottom:1px solid rgba(56,189,248,0.07);' : ''}`;

                        const bName = document.createElement('span');
                        bName.style.cssText = 'flex:1;font-size:0.8rem;color:#CBD5E1;font-weight:600;';
                        bName.textContent = bill.frequency === 'monthly' ? bill.name : `${bill.name} (${elFrequencyLabel(bill.frequency)})`;

                        const bDue = document.createElement('span');
                        bDue.style.cssText = 'min-width:60px;text-align:center;font-size:0.8rem;color:#94A3B8;font-weight:500;';
                        bDue.textContent = `${monthLabel} ${bill.day}`;

                        const bAmt = document.createElement('span');
                        bAmt.style.cssText = 'min-width:80px;text-align:right;font-size:0.8rem;color:#38BDF8;font-weight:700;';
                        bAmt.textContent = `$${bill.amount.toLocaleString()}`;

                        billRow.appendChild(bName);
                        billRow.appendChild(bDue);
                        billRow.appendChild(bAmt);
                        detailWrap.appendChild(billRow);
                    });
                } else {
                    // Empty state — shown when no bills have a due date in this range
                    const empty = document.createElement('div');
                    empty.style.cssText = 'padding:10px 20px;color:#64748B;font-size:0.78rem;font-style:italic;';
                    empty.textContent = 'No bills with due dates set for this week.';
                    detailWrap.appendChild(empty);
                }

                // Click: toggle this week's detail + apply as the active week filter.
                summaryRow.addEventListener('click', (e) => {
                    e.stopPropagation();
                    elExpandedWeek = elSameCalendarWeek(elExpandedWeek, week) ? null : week;
                    elApplyWeekFilter(week);
                });

                weekBlock.appendChild(summaryRow);
                weekBlock.appendChild(detailWrap);
                weekPanel.appendChild(weekBlock);
            });
        };

        // Weekly button — sits directly right of Delete Last in the category action row
        const weeklyBtn = document.createElement('button');
        weeklyBtn.type = 'button';
        weeklyBtn.textContent = 'Weekly ▾';
        weeklyBtn.className = 'btn btn-sm';
        weeklyBtn.style.cssText = 'background:#1E3A8A;color:#fff;font-weight:700;border:none;white-space:nowrap;';
        weeklyBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            const isOpen = weekPanel.style.display !== 'none';
            if (isOpen) { weekPanel.style.display = 'none'; return; }
            renderWeekPanel();
            weekPanel.style.display = 'block';
        });
        document.addEventListener('click', () => { weekPanel.style.display = 'none'; });
        weekPanel.addEventListener('click', e => e.stopPropagation());
        addBtn.parentElement.appendChild(weeklyBtn);

        // Second Weekly button — placed to the right of the Total Monthly Income input for quick top-of-page access
        const weeklyBtnTop = document.createElement('button');
        weeklyBtnTop.id = elId('WeeklyBtnTop');
        weeklyBtnTop.type = 'button';
        weeklyBtnTop.textContent = 'Weekly ▾';
        weeklyBtnTop.className = 'btn';
        weeklyBtnTop.style.cssText = 'background:#1E3A8A;color:#fff;font-weight:700;border:none;white-space:nowrap;flex-shrink:0;padding:0 16px;height:38px;line-height:1;border-radius:6px;font-size:0.875rem;';
        weeklyBtnTop.addEventListener('click', (e) => {
            e.stopPropagation();
            const isOpen = weekPanel.style.display !== 'none';
            if (isOpen) { weekPanel.style.display = 'none'; return; }
            renderWeekPanel();
            weekPanel.style.display = 'block';
        });
        // Wrap the income input row in a flex container so the button sits cleanly to the right.
        // Remove mb-3 from the input (it adds margin-bottom inside the wrapper causing height mismatch).
        elIncome.classList.remove('mb-3');
        elIncome.style.maxWidth = '240px';
        elIncome.style.width = '240px';
        const incomeInputRow = elIncome.parentElement;
        const incomeFlexWrap = document.createElement('div');
        incomeFlexWrap.style.cssText = 'display:flex;align-items:center;gap:10px;margin-bottom:15px;';
        incomeInputRow.style.cssText = 'position:relative;margin-bottom:0;';
        incomeInputRow.parentElement.insertBefore(incomeFlexWrap, incomeInputRow);
        incomeFlexWrap.appendChild(incomeInputRow);
        incomeFlexWrap.appendChild(weeklyBtnTop);

        addClearButton(container, () => {
            elIncome.value = '';
            // Reset template rows in-place; remove custom rows entirely
            Array.from(categoriesContainer.querySelectorAll(`[id^="${elId('CatRow')}"]`)).forEach(row => {
                if (row.dataset.isTemplate === 'true') {
                    const idx = row.id.replace(elId('CatRow'), '');
                    const amtEl = elById(`CatAmount${idx}`);
                    const dueEl = elById(`CatDue${idx}`);
                    const freqEl = elById(`CatFrequency${idx}`);
                    if (amtEl) amtEl.value = '';
                    if (dueEl) dueEl.value = toCurrentMonthDue(null);
                    if (freqEl) freqEl.value = 'monthly';
                } else {
                    categoriesContainer.removeChild(row);
                }
            });
            // Set categoryCount to highest remaining index so new rows get unique IDs
            categoryCount = Array.from(categoriesContainer.querySelectorAll(`[id^="${elId('CatRow')}"]`))
                .reduce((max, r) => Math.max(max, parseInt(r.id.replace(elId('CatRow'), '')) || 0), 0);
            elTips.textContent = expenseLensDefaultTip;
            elMargin.textContent = 'Remaining Balance: $0';
            clearExpenseLensState();
            hideTip();
            elApplyWeekFilter(null);
            refreshExpenseLens();
        });

        await loadExpenseLensState();

        // Auto-apply current week filter on load if any bills are due this week.
        // This makes the tool time-aware: the user sees only today's relevant bills
        // by default rather than every bill. "Show All Bills" in the weekly panel resets it.
        (() => {
            const currentWeek = elGetCurrentCalendarWeek();
            if (!currentWeek) return;
            const hasThisWeek = [...categoriesContainer.querySelectorAll(`[id^="${elId('CatRow')}"]`)].some(row => {
                const idx = row.id.replace(elId('CatRow'), '');
                return elGetBillOccurrenceDays(idx, currentWeek).length > 0;
            });
            if (hasThisWeek) elApplyWeekFilter(currentWeek);
        })();

        // ✅ Color engine (no refresh needed)
        const applyExpenseLensColors = () => {
            // Inputs
            markIncome(elIncome);

            // Rows (dynamic)
            categoriesContainer.querySelectorAll(`[id^="${elId('CatName')}"]`).forEach(n => markNeutral(n));     // labels
            categoriesContainer.querySelectorAll(`[id^="${elId('CatFrequency')}"]`).forEach(f => markNeutral(f)); // frequency
            categoriesContainer.querySelectorAll(`[id^="${elId('CatAmount')}"]`).forEach(a => markExpense(a));  // spending
            categoriesContainer.querySelectorAll(`[id^="${elId('Out')}"]`).forEach(p => markExpense(p));        // % outputs

            // Tips
            markNeutral(elTips);

            // Remaining Balance (based on current computed values)
            // When a week filter is active, the label shows "Week X Due: $N" — always red (it's a bill amount).
            if (elActiveWeek) {
                markExpense(elMargin);
            } else {
                const income = +elIncome.value.replace(/,/g, '') || 0;
                let totalSpent = 0;
                categoriesContainer.querySelectorAll(`[id^="${elId('CatAmount')}"]`).forEach(input => {
                    const idx = input.id.replace(elId('CatAmount'), '');
                    const occurrenceCount = elGetBillOccurrenceDays(idx).length;
                    totalSpent += (+input.value.replace(/,/g, '') || 0) * occurrenceCount;
                });
                const remaining = income - totalSpent;
                if (remaining >= 0) markIncome(elMargin);
                else markExpense(elMargin);
            }
        };

        // ✅ Force style application after DOM paint (this is what kills the “refresh page” issue)
        requestAnimationFrame(() => {
            applyExpenseLensColors();
            refreshExpenseLens();            // ensures Remaining Balance + tip text is current
            applyExpenseLensColors();        // re-apply after refresh updates DOM text
        });
        };

        if (isBusinessClient && t.id === "ExpenseLens") {
            embedContainer.innerHTML = `
                <style>
                    .expense-lens-dual-shell {
                        width: min(1840px, calc(100vw - 32px));
                        margin: 0 auto 50px;
                        display: grid;
                        grid-template-columns: repeat(2, minmax(0, 1fr));
                        gap: 18px;
                        align-items: stretch;
                    }
                    .expense-lens-dual-panel {
                        min-width: 0;
                        display: flex;
                    }
                    .expense-lens-dual-panel > .networth-tool {
                        width: 100% !important;
                        max-width: none !important;
                        margin: 0 !important;
                        min-height: 100%;
                    }
                    @media (max-width: 1100px) {
                        .expense-lens-dual-shell {
                            width: min(100%, calc(100vw - 20px));
                            grid-template-columns: 1fr;
                        }
                    }
                </style>
                <div class="expense-lens-dual-shell">
                    <div class="expense-lens-dual-panel" id="expenseLensPersonalHost"></div>
                    <div class="expense-lens-dual-panel" id="expenseLensBusinessHost"></div>
                </div>
            `;
            const personalHost = document.getElementById("expenseLensPersonalHost");
            const businessHost = document.getElementById("expenseLensBusinessHost");
            await renderExpenseLensInstance("ExpenseLens", personalHost);
            await renderExpenseLensInstance("BusinessExpenseLens", businessHost);
        } else {
            await renderExpenseLensInstance(t.id, embedContainer);
        }

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
       style="background: radial-gradient(900px 320px at 0% 0%, rgba(166,128,35,.12), transparent 55%), linear-gradient(180deg, rgba(11,21,41,.99), rgba(15,29,56,.99));
              border-radius:20px;
              box-shadow:0 40px 100px rgba(0,0,0,.58);
              border:1.8px solid rgba(166,128,35,.52);
              max-width:1200px;
              margin:0 auto;
              color:#f8fafc;
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

        <p style="font-style:italic; color:#b9c5d8; margin-bottom:18px;">
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
                           style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; font-size:1.1rem; color:#1E3A8A; padding-right:30px;" />
                    <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#1E3A8A;">$</span>
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
                           style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; font-size:1.1rem; color:#1E3A8A; padding-right:30px;" />
                    <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#1E3A8A;">$</span>
                </div>
            </div>
        </div>

        <table class="table mt-3"
               style="background:linear-gradient(180deg,rgba(255,255,255,.055),rgba(255,255,255,.02));
                      border-radius:12px;
                      overflow:hidden;
                      border:1px solid rgba(166,128,35,.22); font-weight:700; font-size:1.1rem; color:#f8fafc;">
            <tr style="background:rgba(166,128,35,.15);">
                <th style="color:#f4d890;">Assets</th>
                <th style="color:#f4d890;">Liabilities</th>
                <th style="color:#f4d890;">Net Worth</th>
            </tr>
            <tr>
                <td id="aVal">$0</td>
                <td id="lVal">$0</td>
                <td id="nVal">$0</td>
            </tr>
        </table>

        <table class="table mt-3"
               style="background:linear-gradient(180deg,rgba(255,255,255,.055),rgba(255,255,255,.02));
                      border-radius:12px;
                      overflow:hidden;
                      border:1px solid rgba(166,128,35,.22); font-weight:700; font-size:1.1rem; color:#f8fafc;">
            <tr>
                <th style="color:#f4d890;">Net Worth to Assets Ratio</th>
                <td id="nwRatio">0%</td>
            </tr>
            <tr>
                <th style="color:#f4d890;">Liabilities to Assets Ratio</th>
                <td id="liabRatio">0%</td>
            </tr>
            <tr>
                <th style="color:#f4d890;">Wealth Status</th>
                <td id="wealthStatus">—</td>
            </tr>
        </table>

        <div id="nwTips"
             style="padding:12px;
                    background:rgba(255,255,255,.05);
                    border:1px solid rgba(166,128,35,.18);
                    border-left:4px solid #a68023;
                    border-radius:8px;
                    font-style:italic;
                    color:#f8fafc;
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
        style="background: radial-gradient(900px 320px at 0% 0%, rgba(166,128,35,.12), transparent 55%), linear-gradient(180deg, rgba(11,21,41,.99), rgba(15,29,56,.99));
               border-radius:20px;
               box-shadow:0 40px 100px rgba(0,0,0,.58);
               border:1.8px solid rgba(166,128,35,.52);
               max-width:1200px;
               margin:0 auto;
               color:#f8fafc;
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

        <p style="font-style:italic; color:#b9c5d8; margin-bottom:18px;">
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
                                  font-weight:700; font-size:1.1rem; color:#1E3A8A; padding-right:30px;" />
                    <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#1E3A8A;">$</span>
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
                                  font-weight:700; font-size:1.1rem; color:#1E3A8A; padding-right:30px;" />
                    <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#1E3A8A;">$</span>
                </div>
            </div>
        </div>

        <h5 style="font-weight:700; margin-top:6px; color:#fff;">
            Net Cash Flow:
            <span id="cfResult" style="color:#a68023; font-weight:900;">$0</span>
        </h5>

        <table class="table mt-3"
               style="background:linear-gradient(180deg,rgba(255,255,255,.055),rgba(255,255,255,.02));
                      border-radius:12px;
                      overflow:hidden;
                      border:1px solid rgba(166,128,35,.22); font-weight:700; font-size:1.1rem; color:#f8fafc;">
            <tr style="background:rgba(166,128,35,.15);">
                <th style="width:50%; color:#f4d890;">Savings Potential</th>
                <td id="cfSavingsPotential">$0</td>
            </tr>
            <tr>
                <th style="color:#f4d890;">Suggested Allocation</th>
                <td id="cfInvestPct">0%</td>
            </tr>
        </table>

        <div id="cfTips"
             style="padding:12px;
                    background:rgba(255,255,255,.05);
                    border:1px solid rgba(166,128,35,.18);
                    border-left:4px solid #a68023;
                    border-radius:8px;
                    font-style:italic;
                    color:#f8fafc;
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
        markWithSuffix(markIncome,  cfIncome);
        markWithSuffix(markExpense, cfBills);

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
        style="background: radial-gradient(900px 320px at 0% 0%, rgba(166,128,35,.12), transparent 55%), linear-gradient(180deg, rgba(11,21,41,.99), rgba(15,29,56,.99));
               border-radius:20px;
               box-shadow:0 40px 100px rgba(0,0,0,.58);
               border:1.8px solid rgba(166,128,35,.52);
               max-width:1200px;
               margin:0 auto;
               color:#f8fafc;
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

        <p style="font-style:italic; color:#b9c5d8; margin-bottom:18px;">
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
                           style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; font-size:1.1rem; color:#1E3A8A; padding-right:30px;" />
                    <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#1E3A8A;">$</span>
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
                           style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; font-size:1.1rem; color:#1E3A8A; padding-right:30px;" />
                    <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#1E3A8A;">$</span>
                </div>
            </div>
        </div>

        <h5 style="font-weight:700; margin-top:8px;">
            DTI Ratio:
            <span id="dcResult" style="color:#a68023; font-weight:900;">0%</span>
        </h5>

        <table class="table mt-3"
               style="background:linear-gradient(180deg,rgba(255,255,255,.055),rgba(255,255,255,.02));
                      border-radius:12px;
                      overflow:hidden;
                      border:1px solid rgba(166,128,35,.22); font-weight:700; font-size:1.1rem; color:#f8fafc;">
            <tr style="background:rgba(166,128,35,.15);">
                <th style="width:40%; color:#f4d890;">DTI Status</th>
                <td id="dcStatus">—</td>
            </tr>
            <tr>
                <th style="color:#f4d890;">Recommendation</th>
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

        // ✅ apply colors immediately after compute
        applyDebtClarityColors(dtiNum);
    }

    dcDebt.oninput = dcIncome.oninput = calcDebtClarity;

    // ✅ paint-safe initial compute + color (fixes “need refresh”)
    calcDebtClarity();
}


/* -------------------------------
    7️⃣ FINANCIAL BUFFER (ENHANCED & ELEVATED)
--------------------------------*/
if (t.id === "FinancialBuffer") {
    embedContainer.innerHTML = `
    <div class="networth-tool p-4"
         style="background: radial-gradient(900px 320px at 0% 0%, rgba(166,128,35,.12), transparent 55%), linear-gradient(180deg, rgba(11,21,41,.99), rgba(15,29,56,.99));
                border-radius:20px;
                box-shadow:0 40px 100px rgba(0,0,0,.58);
                border:1.8px solid rgba(166,128,35,.52);
                max-width:600px;
                margin:0 auto;
                color:#f8fafc;
                font-family:'Inter',sans-serif;">

        <!-- Tooltip styles (safe + isolated) -->
        <style>
            .fb-label{
                display:inline-flex;
                align-items:center;
                gap:8px;
                margin-bottom:6px;
                font-weight:800;
                color:#a68023;
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
                   style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#1E3A8A; padding-right:30px;" />
            <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#1E3A8A;">$</span>
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
        markGold(fb1);
        markGold(fb3);
        markGold(fb12);

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

        // ✅ apply colors immediately after compute
        applyFinancialBufferColors(bills);
    };

    fbBillsInput.addEventListener('input', updateBuffer);

    // ✅ initial compute + paint (for persisted state)
    updateBuffer();
}


/* -------------------------------
    8️⃣ WEALTH PROJECTION (ENHANCED & ELEVATED)
--------------------------------*/
if (t.id === "WealthProjection") {
    embedContainer.innerHTML = `
    <div class="networth-tool p-4"
         style="background: radial-gradient(900px 320px at 0% 0%, rgba(166,128,35,.12), transparent 55%), linear-gradient(180deg, rgba(11,21,41,.99), rgba(15,29,56,.99));
                border-radius:20px;
                box-shadow:0 40px 100px rgba(0,0,0,.58);
                border:1.8px solid rgba(166,128,35,.52);
                max-width:600px;
                margin:0 auto;
                color:#f8fafc;
                font-family:'Inter',sans-serif;">

        <!-- Tooltip styles (safe + isolated) -->
        <style>
            .wp-label{
                display:inline-flex;
                align-items:center;
                gap:8px;
                margin-bottom:6px;
                font-weight:800;
                color:#a68023;
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
               style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#1E3A8A;" />

        <div class="wp-label">
            Monthly Surplus
            <span class="wp-i" tabindex="0"
                  data-tip="<b>Examples:</b> 500 • 2,000 (income minus expenses each month)">i</span>
        </div>
        <input id="wpSurplus" type="text" class="form-control mb-2" placeholder="e.g., 2,000"
               style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#1E3A8A;" />

        <div class="wp-label">
            Custom Months
            <span class="wp-i" tabindex="0"
                  data-tip="<b>Examples:</b> 18 • 24 • 60 (how far out you want to project)">i</span>
        </div>
        <input id="wpMonths" type="number" class="form-control mb-3" placeholder="e.g., 18"
               style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#1E3A8A;" />

        <div style="background:linear-gradient(180deg,rgba(255,255,255,.055),rgba(255,255,255,.02)); border-radius:12px; padding:14px; border:1px solid rgba(166,128,35,.22); margin-bottom:10px; color:#f8fafc;">
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
                    background:rgba(255,255,255,.05);
                    border:1px solid rgba(166,128,35,.18);
                    border-left:5px solid #a68023;
                    font-style:italic;
                    color:#f8fafc;
                    margin-top:15px;
                    border-radius:10px;
                    box-shadow:inset 0 0 12px rgba(166,128,35,0.15);">
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
            markGold(wpOut);
            markGold(wp6);
            markGold(wp12);
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
         style="background: radial-gradient(900px 320px at 0% 0%, rgba(166,128,35,.12), transparent 55%), linear-gradient(180deg, rgba(11,21,41,.99), rgba(15,29,56,.99));
                border-radius:20px;
                box-shadow:0 40px 100px rgba(0,0,0,.58);
                border:1.8px solid rgba(166,128,35,.52);
                max-width:600px;
                margin:0 auto;
                color:#f8fafc;
                font-family:'Inter',sans-serif;">

        <!-- Tooltip styles (safe + isolated) -->
        <style>
            .fi-label{
                display:inline-flex;
                align-items:center;
                gap:8px;
                margin-bottom:6px;
                font-weight:800;
                color:#a68023;
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
               style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#1E3A8A;" />

        <div class="fi-label">
            Annual Expenses
            <span class="fi-i" tabindex="0"
                  data-tip="<b>What to enter:</b> Your yearly cost of living. <b>Example:</b> 50,000 (≈ 4,167/mo)">i</span>
        </div>
        <input id="fiExp" type="text" class="form-control mb-2" placeholder="e.g., 50,000"
               style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#1E3A8A;" />

        <div class="fi-label">
            Passive Income
            <span class="fi-i" tabindex="0"
                  data-tip="<b>Optional:</b> Annual passive income (rent, dividends, etc.). <b>Example:</b> 10,000">i</span>
        </div>
        <input id="fiPassive" type="text" class="form-control mb-3" placeholder="e.g., 10,000"
               style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#1E3A8A;" />

        <h5 style="font-weight:700; margin-top:10px;">
            Freedom Index: <span id="fiOut" style="color:#a68023; font-weight:800;">0</span>
        </h5>

        <table class="table mt-3" style="background:linear-gradient(180deg,rgba(255,255,255,.055),rgba(255,255,255,.02)); border-radius:12px; overflow:hidden; border:1px solid rgba(166,128,35,.22); color:#f8fafc;">
            <tr><th style="width:45%; background:rgba(0,0,0,.2); color:#a68023;">Net Worth</th><td id="fiNetOut">$0</td></tr>
            <tr><th style="background:rgba(0,0,0,.2); color:#a68023;">Annual Expenses</th><td id="fiExpOut">$0</td></tr>
            <tr><th style="background:rgba(0,0,0,.2); color:#a68023;">Passive Income</th><td id="fiPassiveOut">$0</td></tr>
            <tr><th style="background:rgba(0,0,0,.2); color:#a68023;">Months of Freedom</th><td id="fiMonths">0</td></tr>
        </table>

        <div id="fiAdvice"
             style="padding:14px; background:rgba(255,255,255,.05);
                    border:1px solid rgba(166,128,35,.18);
                    border-left:5px solid #a68023; font-style:italic; color:#f8fafc; margin-top:15px;
                    border-radius:10px; box-shadow:inset 0 0 12px rgba(166,128,35,0.15);">
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
         style="background: radial-gradient(900px 320px at 0% 0%, rgba(166,128,35,.12), transparent 55%), linear-gradient(180deg, rgba(11,21,41,.99), rgba(15,29,56,.99));
                border-radius:20px;
                box-shadow:0 40px 100px rgba(0,0,0,.58);
                border:1.8px solid rgba(166,128,35,.52);
                max-width:600px;
                margin:0 auto;
                color:#f8fafc;
                font-family:'Inter',sans-serif;">

        <!-- Tooltip styles (safe + isolated) -->
        <style>
            .dap-label{
                display:inline-flex;
                align-items:center;
                gap:8px;
                margin-bottom:6px;
                font-weight:800;
                color:#a68023;
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
               style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#1E3A8A;" />

        <div class="dap-label">
            Total Liabilities
            <span class="dap-i" tabindex="0"
                  data-tip="<b>Examples:</b> 50,000 • 180,000 (credit cards, loans, mortgage balance, etc.)">i</span>
        </div>
        <input id="dapL" type="text" class="form-control mb-2" placeholder="e.g., 50,000"
               style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#1E3A8A;" />

        <div class="dap-label">
            Monthly Income
            <span class="dap-i" tabindex="0"
                  data-tip="<b>Optional:</b> Monthly income helps estimate how fast you could crush liabilities. <b>Example:</b> 6,000">i</span>
        </div>
        <input id="dapIncome" type="text" class="form-control mb-3" placeholder="e.g., 6,000"
               style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#1E3A8A;" />

        <h5 style="font-weight:700; margin-top:10px;">
            Debt-to-Asset Ratio:
            <span id="dapOut" style="color:#1E3A8A; font-weight:800;">0</span>
        </h5>

        <table class="table mt-3" style="background:linear-gradient(180deg,rgba(255,255,255,.055),rgba(255,255,255,.02)); border-radius:12px; overflow:hidden; border:1px solid rgba(166,128,35,.22); color:#f8fafc;">
            <tr><th style="width:45%; background:rgba(0,0,0,.2); color:#a68023;">Assets</th><td id="dapAssets">$0</td></tr>
            <tr><th style="background:rgba(0,0,0,.2); color:#a68023;">Liabilities</th><td id="dapLiabilities">$0</td></tr>
            <tr><th style="background:rgba(0,0,0,.2); color:#a68023;">Net Worth</th><td id="dapNetWorth">$0</td></tr>
            <tr><th style="background:rgba(0,0,0,.2); color:#a68023;">Monthly Income</th><td id="dapMonthlyIncome">$0</td></tr>
        </table>

        <div id="dapAdvice"
             style="padding:14px; background:rgba(255,255,255,.05);
                    border:1px solid rgba(166,128,35,.18);
                    border-left:5px solid #a68023; font-style:italic; color:#f8fafc; margin-top:15px;
                    border-radius:10px; box-shadow:inset 0 0 12px rgba(166,128,35,0.15);">
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
    const normalizedSavedToolId = isBusinessClient && savedToolId === "BusinessExpenseLens"
        ? "ExpenseLens"
        : savedToolId;
    if (normalizedSavedToolId && tools.some(tool => tool.id === normalizedSavedToolId)) {
        dropdown.value = normalizedSavedToolId;
        dropdown.dispatchEvent(new Event("change"));
    } else {
        // Default to Wealth Forecast on first load
        dropdown.value = "WealthForecast";
        dropdown.dispatchEvent(new Event("change"));
    }

}); // ✅ closes document.addEventListener("DOMContentLoaded", ...)
