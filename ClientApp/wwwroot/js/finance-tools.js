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
        #budget-embed .legend-money-input,
        .networth-tool .legend-money-input {
            display: flex;
            align-items: center;
            width: 100%;
            min-width: 180px;
            min-height: 42px;
            background: #f4f4f2;
            border: 1px solid rgba(198, 151, 45, 0.75);
            border-radius: 10px;
            overflow: hidden;
            box-shadow: inset 0 1px 0 rgba(255,255,255,.4);
        }
        #budget-embed .legend-money-input[hidden],
        .networth-tool .legend-money-input[hidden] {
            display: none !important;
        }
        #budget-embed .legend-money-prefix,
        .networth-tool .legend-money-prefix {
            flex: 0 0 auto;
            padding-left: 12px;
            padding-right: 8px;
            color: #0b2a66;
            font-weight: 800;
            line-height: 1;
            pointer-events: none;
            user-select: none;
        }
        #budget-embed .legend-money-field,
        .networth-tool .legend-money-field {
            flex: 1 1 auto;
            min-width: 0;
            width: 100%;
            height: 100%;
            border: 0 !important;
            outline: 0 !important;
            background: transparent !important;
            box-shadow: none !important;
            border-radius: 0 !important;
            padding: 0 12px 0 0 !important;
            color: #0b2a66 !important;
            font-weight: 800 !important;
        }
        #budget-embed .legend-money-field:focus,
        .networth-tool .legend-money-field:focus {
            border: 0 !important;
            outline: 0 !important;
            box-shadow: none !important;
        }
        #budget-embed .legend-money-input:focus-within,
        .networth-tool .legend-money-input:focus-within {
            border-color: #d4af37;
            box-shadow: 0 0 0 3px rgba(212, 175, 55, 0.18);
        }
        #budget-embed .legend-percent-input,
        .networth-tool .legend-percent-input {
            display: flex;
            align-items: center;
            width: 100%;
            min-width: 90px;
            min-height: 42px;
            background: #f4f4f2;
            border: 1px solid rgba(198, 151, 45, 0.75);
            border-radius: 10px;
            overflow: hidden;
            box-shadow: inset 0 1px 0 rgba(255,255,255,.4);
        }
        #budget-embed .legend-percent-suffix,
        .networth-tool .legend-percent-suffix {
            flex: 0 0 auto;
            padding: 0 10px 0 4px;
            color: #0b2a66;
            font-weight: 800;
            line-height: 1;
            pointer-events: none;
            user-select: none;
        }
        #budget-embed .legend-percent-field,
        .networth-tool .legend-percent-field {
            flex: 1 1 auto;
            min-width: 0;
            width: 100%;
            height: 100%;
            border: 0 !important;
            outline: 0 !important;
            background: transparent !important;
            box-shadow: none !important;
            border-radius: 0 !important;
            padding: 0 4px 0 10px !important;
            margin: 0 !important;
            color: #0b2a66 !important;
            font-weight: 800 !important;
            appearance: none !important;
        }
        #budget-embed .legend-percent-field:focus,
        .networth-tool .legend-percent-field:focus {
            border: 0 !important;
            outline: 0 !important;
            box-shadow: none !important;
        }
        #budget-embed .legend-percent-input:focus-within,
        .networth-tool .legend-percent-input:focus-within {
            border-color: #ddb457;
            box-shadow: 0 0 0 3px rgba(221,180,87,.16);
        }
    `;
    document.head.appendChild(s);
})();

document.addEventListener("DOMContentLoaded", async function () {
    const dropdown = document.getElementById("budgetDropdown");
    const financialHealthButton = document.getElementById("btnFinancialHealthSnapshot");
    const embedContainer = document.getElementById("budget-embed");
    const financeShell = document.querySelector(".finance-shell");
    const financeToolsRow = document.querySelector(".finance-tools-row");
    const financeRoot = document.getElementById("financeRoot");
    const DEFAULT_TOOL_ID = "LegendLivingBalanceSheet";
    const clientProfileId = financeRoot?.dataset.clientProfileId?.trim() || "";
    const clientUserId = financeRoot?.dataset.clientUserId?.trim() || "";
    const isBusinessClient = (financeRoot?.dataset.isBusinessClient || "").toLowerCase() === "true";
    const clientFirstName = financeRoot?.dataset.clientFirstName?.trim() || "";
    const spouseFirstName = financeRoot?.dataset.spouseFirstName?.trim() || "";
    const hasSpouseAttr = financeRoot?.dataset.hasSpouse;
    const hasSpouse = hasSpouseAttr === "true" ? true : hasSpouseAttr === "false" ? false : undefined;
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
    const rawStateFirstToolIds = new Set([
        "SavingsAccelerator",
        "BusinessSavingsAccelerator",
        "ExpenseLens",
        "BusinessExpenseLens"
    ]);
    const removeDualToolPopout = () => {
        document.getElementById("financeDualToolPopout")?.remove();
    };
    const setDualToolMode = (enabled) => {
        if (!enabled) removeDualToolPopout();
        financeShell?.classList.toggle("finance-shell--dual-tools", !!enabled);
        financeToolsRow?.classList.toggle("finance-tools-row--dual-tools", !!enabled);
        document.body.classList.toggle("finance-dual-tools-open", !!enabled);
    };
    const closeDualToolPopout = () => {
        removeDualToolPopout();
        setDualToolMode(false);
        embedContainer.innerHTML = "";
        embedContainer.classList.remove("finance-main--dual");
        if (dropdown) {
            requestToolSelection(DEFAULT_TOOL_ID);
        }
    };
    const createDualToolPopout = (title, subtitle) => {
        removeDualToolPopout();
        setDualToolMode(true);
        embedContainer.innerHTML = "";
        embedContainer.classList.add("finance-main--dual");

        const popout = document.createElement("section");
        popout.id = "financeDualToolPopout";
        popout.className = "finance-dual-popout";
        popout.setAttribute("role", "dialog");
        popout.setAttribute("aria-modal", "true");
        popout.setAttribute("aria-label", title);
        popout.innerHTML = `
            <div class="finance-dual-popout__header">
                <div>
                    <div class="finance-dual-popout__eyebrow">Business client workspace</div>
                    <h2 class="finance-dual-popout__title">${title}</h2>
                    <p class="finance-dual-popout__sub">${subtitle}</p>
                </div>
                <button type="button" class="finance-dual-popout__close" data-dual-popout-close>Close</button>
            </div>
            <div class="finance-dual-popout__body"></div>
        `;
        popout.querySelector("[data-dual-popout-close]")?.addEventListener("click", closeDualToolPopout);
        document.body.appendChild(popout);
        return popout.querySelector(".finance-dual-popout__body");
    };

    const fitSingleLineControlText = (control, options = {}) => {
        if (!control) return;
        const minSize = options.minSize || 10;
        const maxSize = options.maxSize || 14;
        const reserve = options.reserve || 8;
        const update = () => {
            if (!control.isConnected) return;
            const styles = window.getComputedStyle(control);
            const baseSize = Number.parseFloat(control.dataset.fitBaseFontSize || styles.fontSize || `${maxSize}`) || maxSize;
            control.dataset.fitBaseFontSize = String(baseSize);
            const text = control.tagName === "SELECT"
                ? (control.options[control.selectedIndex]?.textContent || control.value || "")
                : (control.value || control.placeholder || "");
            const measurer = fitSingleLineControlText._measurer || (() => {
                const span = document.createElement("span");
                span.style.position = "fixed";
                span.style.left = "-9999px";
                span.style.top = "-9999px";
                span.style.whiteSpace = "pre";
                span.style.pointerEvents = "none";
                document.body.appendChild(span);
                fitSingleLineControlText._measurer = span;
                return span;
            })();
            measurer.style.fontFamily = styles.fontFamily;
            measurer.style.fontWeight = styles.fontWeight;
            measurer.style.fontStyle = styles.fontStyle;
            measurer.style.letterSpacing = styles.letterSpacing;
            measurer.style.fontSize = `${baseSize}px`;
            measurer.textContent = text || " ";
            const horizontalPadding =
                (Number.parseFloat(styles.paddingLeft) || 0) +
                (Number.parseFloat(styles.paddingRight) || 0) +
                reserve;
            const available = Math.max(24, control.clientWidth - horizontalPadding);
            const measured = Math.max(1, measurer.getBoundingClientRect().width);
            const nextSize = Math.max(minSize, Math.min(maxSize, baseSize * Math.min(1, available / measured)));
            control.style.fontSize = `${nextSize.toFixed(2)}px`;
        };

        control.addEventListener("input", update);
        control.addEventListener("change", update);
        if (window.ResizeObserver) {
            const observer = new ResizeObserver(update);
            observer.observe(control);
        }
        requestAnimationFrame(update);
    };

    function getStateKeys(key) {
        if (!key) return [];
        if (key === selectedToolStateId || key === "ActionTracker" || key.startsWith("toolState-")) {
            return [key];
        }

        if (rawStateFirstToolIds.has(key)) {
            return [key, `toolState-${key}`];
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

    const serverSaveQueue = new Map();
    const serverSaveTimers = new Map();
    const serverSaveInFlight = new Set();
    const localStateKey = (key) => scopeKey(key);

    function readLocalPersistedState(key) {
        const raw = localStorage.getItem(localStateKey(key));
        if (!raw) return null;

        try {
            return normalizePersistedState(key, JSON.parse(raw || "{}"));
        } catch (_) {
            return null;
        }
    }

    function hasPendingServerState(key) {
        const primaryKey = getPrimaryStateKey(key);
        return serverSaveQueue.has(primaryKey) || serverSaveInFlight.has(primaryKey);
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
        const preferLocalState = rawStateFirstToolIds.has(key);

        if (preferLocalState) {
            for (const candidateKey of keys) {
                const localState = readLocalPersistedState(candidateKey);
                if (localState !== null) {
                    return localState;
                }
            }
        }

        // If this browser has a newer unsynced edit queued, trust that immediately
        // so downstream tools stay in sync when switching tools quickly.
        for (const candidateKey of keys) {
            if (!hasPendingServerState(candidateKey)) continue;
            const localState = readLocalPersistedState(candidateKey);
            if (localState !== null) {
                return localState;
            }
        }

        if (canUseServerState) {
            for (const candidateKey of keys) {
                try {
                    const url = `/api/finance-state/load?${buildQuery(candidateKey)}`;
                    const res = await fetch(url, { credentials: "include" });
                    if (res.ok) {
                        const payload = await res.json();
                        if (payload?.found) {
                            const state = normalizePersistedState(candidateKey, JSON.parse(payload?.jsonState || "{}"));
                            localStorage.setItem(localStateKey(getPrimaryStateKey(candidateKey)), JSON.stringify(state ?? {}));
                            return state;
                        }
                    }
                } catch (_) { }
            }
        }

        for (const candidateKey of keys) {
            const state = readLocalPersistedState(candidateKey);
            if (state !== null) {
                if (canUseServerState) {
                    savePersistedState(candidateKey, state, { skipLocalCache: true, immediate: true });
                }
                return state;
            }
        }

        return normalizePersistedState(key, {});
    }

    const getAntiForgeryToken = () =>
        document.querySelector('#__af input[name="__RequestVerificationToken"]')?.value
        || document.querySelector('input[name="__RequestVerificationToken"]')?.value
        || "";

    function postServerState(primaryKey, jsonState, keepalive = false) {
        const token = getAntiForgeryToken();
        const headers = { "Content-Type": "application/json" };
        if (token) headers["RequestVerificationToken"] = token;

        return fetch("/api/finance-state/save", {
            method: "POST",
            credentials: "include",
            headers,
            body: JSON.stringify({ clientProfileId, clientUserId, toolId: primaryKey, jsonState }),
            keepalive
        });
    }

    function scheduleServerStateFlush(primaryKey, delayMs = 300) {
        if (serverSaveTimers.has(primaryKey)) {
            clearTimeout(serverSaveTimers.get(primaryKey));
        }

        serverSaveTimers.set(primaryKey, setTimeout(() => {
            serverSaveTimers.delete(primaryKey);
            flushServerState(primaryKey);
        }, delayMs));
    }

    async function flushServerState(primaryKey, keepalive = false) {
        if (!canUseServerState || !serverSaveQueue.has(primaryKey)) return;
        if (serverSaveInFlight.has(primaryKey)) return;

        const jsonState = serverSaveQueue.get(primaryKey);
        serverSaveQueue.delete(primaryKey);
        serverSaveInFlight.add(primaryKey);

        let shouldRetry = false;
        try {
            const res = await postServerState(primaryKey, jsonState, keepalive);
            if (!res.ok) throw new Error(`Save failed (${res.status})`);
        } catch (_) {
            if (!keepalive) {
                shouldRetry = true;
                serverSaveQueue.set(primaryKey, jsonState);
            }
        } finally {
            serverSaveInFlight.delete(primaryKey);
            if (serverSaveQueue.has(primaryKey)) {
                scheduleServerStateFlush(primaryKey, shouldRetry ? 2500 : 0);
            }
        }
    }

    function flushAllServerState(keepalive = false) {
        Array.from(serverSaveTimers.values()).forEach(timer => clearTimeout(timer));
        serverSaveTimers.clear();
        Array.from(serverSaveQueue.keys()).forEach(key => {
            flushServerState(key, keepalive);
        });
    }

    window.addEventListener("pagehide", () => flushAllServerState(true));
    document.addEventListener("visibilitychange", () => {
        if (document.visibilityState === "hidden") flushAllServerState(true);
    });

    function savePersistedState(key, state, options = {}) {
        const primaryKey = getPrimaryStateKey(key);
        const normalizedState = normalizePersistedState(primaryKey, state);
        const jsonState = JSON.stringify(normalizedState ?? {});
        if (!options.skipLocalCache) {
            localStorage.setItem(localStateKey(primaryKey), jsonState);
        }

        if (!canUseServerState) return;

        serverSaveQueue.set(primaryKey, jsonState);
        scheduleServerStateFlush(primaryKey, options.immediate ? 0 : 300);
    }

    function clearPersistedState(key) {
        const keys = getStateKeys(key);
        keys.forEach(k => {
            localStorage.removeItem(localStateKey(k));
            serverSaveQueue.delete(k);
            if (serverSaveTimers.has(k)) {
                clearTimeout(serverSaveTimers.get(k));
                serverSaveTimers.delete(k);
            }
        });

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
        if ((embedContainer?.dataset?.activeToolId || "") !== toolId) return;
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
        if ((embedContainer?.dataset?.activeToolId || "") !== toolId) return;
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
        upgradeMoneyInputs(container);
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
        { id: DEFAULT_TOOL_ID, name: "Financial Health Snapshot" },
        { id: "WealthForecast", name: "Wealth Forecast" },
        { id: "ExpenseLens", name: "Expense Lens" },
        { id: "SavingsAccelerator", name: "Savings Accelerator" },
        { id: "NetWorth", name: "Net Worth Tracker" },
        { id: "CashFlow", name: "Cash Flow Map" },
        { id: "DebtClarity", name: "Debt Clarity" },
        { id: "FinancialBuffer", name: "Financial Buffer" },
        { id: "WealthProjection", name: "Wealth Projection" },
        { id: "FreedomIndex", name: "Freedom Index" },
        { id: "DebtAssetPulse", name: "Debt vs Asset Pulse" }
    ];
    const dropdownTools = tools.filter(tool => tool.id !== DEFAULT_TOOL_ID);
    let requestedToolOverrideId = "";

    function syncToolSelectorState(toolId) {
        const isDefaultTool = toolId === DEFAULT_TOOL_ID;
        financialHealthButton?.setAttribute("aria-pressed", isDefaultTool ? "true" : "false");
        if (!dropdown) return;
        if (isDefaultTool) {
            dropdown.selectedIndex = 0;
        } else if (dropdown.value !== toolId) {
            dropdown.value = toolId || "";
        }
    }

    function requestToolSelection(toolId) {
        requestedToolOverrideId = toolId || "";
        dropdown?.dispatchEvent(new Event("change"));
    }

    // Populate dropdown
    dropdownTools.forEach(tool => {
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
  const affixGroups = [
    el.closest?.(".legend-money-input"),
    el.closest?.(".legend-percent-input")
  ].filter(Boolean);
  affixGroups.forEach((group) => {
    group.querySelectorAll(".legend-money-prefix, .legend-percent-suffix").forEach((node) => {
      node.style.setProperty("color", color, "important");
      node.style.setProperty("font-weight", weight, "important");
    });
  });
}

const COLOR_GOLD = "#a68023";
function markIncome(el)  { paint(el, COLOR_INCOME); }
function markExpense(el) { paint(el, COLOR_EXPENSE); }
function markNeutral(el) { paint(el, COLOR_NEUTRAL, "700"); }
function markGold(el)    { paint(el, COLOR_GOLD, "900"); }
function markWithSuffix(markFn, el) {
    if (!el) return;
    markFn(el);
    const group = el.closest('.legend-money-input');
    if (group) {
        group.querySelectorAll('.legend-money-prefix').forEach(markFn);
    }
    [el.previousElementSibling, el.nextElementSibling].forEach((sib) => {
        if (sib && sib.tagName === 'SPAN') markFn(sib);
    });
}

const MONEY_INPUT_EXPLICIT_IDS = new Set([
    "wbStartingBalance", "wbIncome", "saAllocation", "assets", "liabs", "cfIncome", "cfBills",
    "dcDebt", "dcIncome", "fbBills", "wpNet", "wpSurplus", "fiNet", "fiExp", "fiPassive",
    "dapA", "dapL", "dapIncome", "wfd_base", "wfd_emergency", "wfd_desiredIncome",
    "wfd_guaranteedIncome", "wfd_incomeGap", "wfd_invAmt", "wfd_liDeath", "wfd_liAmt",
    "wfd_annDeath", "wfd_annAmt"
]);

const MONEY_INPUT_CLASS_NAMES = [
    "sa-alloc-amount",
    "sa-alloc-starting-balance",
    "sa-alloc-projected"
];

function stripFormattedNumericValue(value) {
    const text = String(value ?? "").trim();
    if (!text) return "";
    const cleaned = text.replace(/[$,%\s]/g, "").replace(/,/g, "");
    if (cleaned === "" || cleaned === "-" || cleaned === "." || cleaned === "-.") return "";
    return cleaned;
}

function sanitizeEditableNumericValue(value) {
    const stripped = String(value ?? "").replace(/[$,\s]/g, "");
    const negative = stripped.startsWith("-") ? "-" : "";
    const unsigned = stripped.replace(/-/g, "");
    const parts = unsigned.split(".");
    const whole = parts.shift()?.replace(/[^\d]/g, "") || "";
    const decimal = parts.length > 0 ? `.${parts.join("").replace(/[^\d]/g, "")}` : "";
    return `${negative}${whole}${decimal}`;
}

function formatNumericDisplayValue(value, maxFractionDigits = 2) {
    const raw = stripFormattedNumericValue(value);
    if (!raw) return "";
    const numeric = Number(raw);
    if (!Number.isFinite(numeric)) return "";
    const decimalText = raw.includes(".") ? raw.split(".")[1] || "" : "";
    const fractionDigits = decimalText ? Math.min(maxFractionDigits, decimalText.length) : 0;
    return numeric.toLocaleString(undefined, {
        minimumFractionDigits: fractionDigits,
        maximumFractionDigits: fractionDigits
    });
}

function findNearestInputLabelText(input) {
    if (!input) return "";
    if (input.id) {
        const label = document.querySelector(`label[for="${CSS.escape(input.id)}"]`);
        if (label) return label.textContent || "";
    }
    const wrappingLabel = input.closest("label");
    if (wrappingLabel) return wrappingLabel.textContent || "";

    let cursor = input.parentElement;
    for (let depth = 0; cursor && depth < 3; depth += 1, cursor = cursor.parentElement) {
        const previous = cursor.previousElementSibling;
        if (previous && previous.tagName === "LABEL") return previous.textContent || "";
    }
    return "";
}

function hasDirectAffix(input, affixText) {
    return Array.from(input.parentElement?.children || []).some((child) =>
        child !== input &&
        child.tagName === "SPAN" &&
        child.textContent.trim() === affixText
    );
}

function isMoneyInputCandidate(input) {
    if (!input || input.tagName !== "INPUT") return false;
    if (input.dataset.moneyInput === "true") return true;
    if (input.type === "hidden" || input.type === "checkbox" || input.type === "radio" || input.type === "date") return false;
    if (MONEY_INPUT_EXPLICIT_IDS.has(input.id)) return true;
    if (MONEY_INPUT_CLASS_NAMES.some((className) => input.classList.contains(className))) return true;
    if (hasDirectAffix(input, "%")) return false;
    if (hasDirectAffix(input, "$")) return true;

    const labelText = findNearestInputLabelText(input);
    if (/%|percent|rate|years?|months?|inflation|tax bracket|efficiency|frequency|date|apr/i.test(labelText)) {
        return false;
    }

    const placeholder = input.getAttribute("placeholder") || "";
    if (/^\$/.test(placeholder)) return true;
    if (/(?:^|[^0-9])\d{1,3}(?:,\d{3})+(?:\.\d+)?(?:[^0-9]|$)/.test(placeholder)) return true;

    return /(balance|income|assets?|liab(?:ilities|s)?|net worth|monthly bills|expenses?|passive income|death benefit|cash value|starting dollar amount|income gap|surplus|allocation|value|amount|emergency)/i.test(labelText);
}

function formatMoneyInputs(root) {
    if (!root) return;
    root.querySelectorAll('input[data-money-input="true"]').forEach((input) => {
        if (document.activeElement === input) return;
        input.value = formatNumericDisplayValue(input.value);
    });
}

function upgradeMoneyInput(input) {
    if (!input || input.dataset.moneyInput === "true") return;
    const parent = input.parentElement;
    let wrapper = parent;
    const canReuseParent = !!parent &&
        parent.tagName === "DIV" &&
        Array.from(parent.children).every((child) =>
            child === input ||
            (child.tagName === "SPAN" && (child.textContent || "").trim() === "$")
        );

    if (!canReuseParent) {
        wrapper = document.createElement("div");
        input.parentNode?.insertBefore(wrapper, input);
        wrapper.appendChild(input);
    } else {
        Array.from(parent.children).forEach((child) => {
            if (child !== input && child.tagName === "SPAN" && (child.textContent || "").trim() === "$") {
                child.remove();
            }
        });
    }

    Array.from(input.classList).forEach((className) => {
        if (/^m[trblxyse]?-\d+$/.test(className) && !wrapper.classList.contains(className)) {
            wrapper.classList.add(className);
            input.classList.remove(className);
        }
    });
    wrapper.classList.add("legend-money-input", "finance-money-input-group");

    let prefix = wrapper.querySelector(".legend-money-prefix");
    if (!prefix) {
        prefix = document.createElement("span");
        prefix.className = "legend-money-prefix";
        prefix.textContent = "$";
        wrapper.insertBefore(prefix, input);
    }

    input.dataset.moneyInput = "true";
    input.classList.add("legend-money-field");
    input.classList.remove("form-control", "form-control-sm", "form-select");
    input.setAttribute("inputmode", "decimal");
    if (input.type !== "hidden") {
        input.type = "text";
    }
    if (/^\$/.test(input.placeholder || "")) {
        input.placeholder = (input.placeholder || "").replace(/^\$\s*/, "");
    }
    input.style.setProperty("border", "0", "important");
    input.style.setProperty("outline", "0", "important");
    input.style.setProperty("background", "transparent", "important");
    input.style.setProperty("box-shadow", "none", "important");
    input.style.setProperty("border-radius", "0", "important");
    input.style.setProperty("padding", "0 12px 0 0", "important");
    input.style.setProperty("margin", "0", "important");
    input.style.setProperty("height", "100%", "important");
    input.style.setProperty("width", "100%", "important");
    input.style.setProperty("min-width", "0", "important");
    input.style.setProperty("appearance", "none", "important");

    if (input.dataset.moneyInputBound !== "true") {
        input.addEventListener("focus", () => {
            input.value = stripFormattedNumericValue(input.value);
        });
        input.addEventListener("input", () => {
            input.value = sanitizeEditableNumericValue(input.value);
        });
        input.addEventListener("blur", () => {
            input.value = formatNumericDisplayValue(input.value);
        });
        input.dataset.moneyInputBound = "true";
    }

    input.value = formatNumericDisplayValue(input.value);
}

function upgradeMoneyInputs(root) {
    if (!root) return;
    root.querySelectorAll("input").forEach((input) => {
        if (isMoneyInputCandidate(input)) {
            upgradeMoneyInput(input);
        }
    });
    if (root.dataset.moneyInputObserverBound !== "true" && window.MutationObserver) {
        const observer = new MutationObserver((mutations) => {
            mutations.forEach((mutation) => {
                mutation.addedNodes.forEach((node) => {
                    if (!(node instanceof Element)) return;
                    if (node.matches?.("input") && isMoneyInputCandidate(node)) {
                        upgradeMoneyInput(node);
                    }
                    node.querySelectorAll?.("input").forEach((input) => {
                        if (isMoneyInputCandidate(input)) upgradeMoneyInput(input);
                    });
                });
            });
        });
        observer.observe(root, { childList: true, subtree: true });
        root.dataset.moneyInputObserverBound = "true";
    }
    formatMoneyInputs(root);
}

    const isDropdownTypeaheadKey = (event) =>
        /^[a-z]$/i.test(event.key) &&
        !event.ctrlKey &&
        !event.metaKey &&
        !event.altKey;

    dropdown?.addEventListener("keydown", function (event) {
        if (isDropdownTypeaheadKey(event)) {
            event.preventDefault();
        }
    });

    financialHealthButton?.addEventListener("click", function () {
        this.blur();
        requestToolSelection(DEFAULT_TOOL_ID);
    });

    let activeToolWindowCleanups = [];
    const clearActiveToolWindowBindings = () => {
        activeToolWindowCleanups.forEach(dispose => {
            try { dispose(); } catch (_) { }
        });
        activeToolWindowCleanups = [];
    };

    const createActiveToolContext = (toolId) => {
        clearActiveToolWindowBindings();
        if (embedContainer) {
            embedContainer.dataset.activeToolId = toolId || "";
        }

        const context = {
            toolId,
            isActive: () => (embedContainer?.dataset?.activeToolId || "") === (toolId || ""),
            onWindow(eventName, handler, options) {
                const wrapped = (event) => {
                    if (!context.isActive()) return;
                    return handler(event);
                };
                window.addEventListener(eventName, wrapped, options);
                activeToolWindowCleanups.push(() => window.removeEventListener(eventName, wrapped, options));
                return wrapped;
            }
        };

        return context;
    };


    // ------------------- Tool Renderer -------------------
    dropdown.addEventListener("change", async function () {
        const selectedToolId = requestedToolOverrideId || this.value;
        requestedToolOverrideId = "";
        this.blur();
        if (!selectedToolId) return;
        syncToolSelectorState(selectedToolId);
        const t = tools.find(x => x.id === selectedToolId);
        saveSelectedToolId(selectedToolId);
        const toolContext = createActiveToolContext(selectedToolId);

        // clear UI
        embedContainer.innerHTML = '';
        embedContainer.classList.remove('finance-main--dual');
        setDualToolMode(false);

        // close any active tooltip cleanly
        if (typeof window.__LegendHideActiveTip === "function") window.__LegendHideActiveTip();

        if (!t) return;

        if (t.id === DEFAULT_TOOL_ID) {
            const tool = window.LegendLivingBalanceSheetTool;
            if (!tool?.render) {
                embedContainer.innerHTML = `
<div class="networth-tool" style="max-width:1100px;margin:0 auto;padding:32px;border-radius:18px;background:#0d1b34;color:#f8fafc;border:1px solid rgba(166,128,35,.35);">
    <h3 style="margin:0 0 8px;">Financial Health Snapshot</h3>
    <p style="margin:0;color:rgba(248,250,252,.72);">This tool could not load. Please refresh and try again.</p>
</div>`;
                return;
            }

            await tool.render({
                host: embedContainer,
                persistence: window.LegendFinancePersistence,
                clientProfileId,
                clientUserId,
                isBusinessClient,
                protectionRoute: "/ProtectionSnapshot",
                clientFirstName,
                spouseFirstName,
                hasSpouse
            });
            return;
        }

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
            padding:14px 16px;
            display:flex;
            flex-direction:column;
            gap:8px;
        }
        .wf-stat-row{
            display:flex;
            justify-content:space-between;
            align-items:center;
            padding:9px 14px;
            border-radius:6px;
            background:rgba(255,255,255,0.04);
            border:1px solid rgba(148,163,184,0.12);
        }
        .wf-stat-label{
            color:#94A3B8;
            font-weight:700;
            font-size:0.8rem;
            letter-spacing:0.04em;
            text-transform:uppercase;
        }
        .wf-stat-value{
            color:#38BDF8;
            font-weight:900;
            font-size:0.92rem;
        }
        .wf-tip-text{
            color:#d4a820 !important;
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
                <div class="wf-stat-row">
                    <span class="wf-stat-label">Real Growth Rate</span>
                    <span id="wbRealGrowth" class="wf-stat-value">0%</span>
                </div>
                <div class="wf-stat-row">
                    <span class="wf-stat-label">Avg Savings Rate</span>
                    <span id="wbSavingsPercent" class="wf-stat-value">0%</span>
                </div>
                <div class="wf-stat-row">
                    <span class="wf-stat-label">Avg Annual Savings</span>
                    <span id="wbActualSavings" class="wf-stat-value">$0</span>
                </div>
                <div id="wbSavingsTips" class="wf-tip-text" style="padding:10px 14px;border-radius:6px;border:2px solid rgba(166,128,35,0.45);background:rgba(166,128,35,0.10);font-weight:700;font-size:0.875rem;color:#d4a820;letter-spacing:0.01em;font-style:italic;transition:background .2s,color .2s,border-color .2s;">
                    Enter your profile above to calculate savings.
                </div>
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
markGold(savingsTipsOut);

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
if (t.id === "SavingsAccelerator") {
    try {
    const renderSavingsAcceleratorInstance = async (renderToolId, hostElement) => {
    const isBusinessSA = renderToolId === "BusinessSavingsAccelerator";
    const isDualPanel = hostElement.classList.contains('expense-lens-dual-panel');
    const prefix = isBusinessSA ? 'bsa' : 'sa';
    const pid = (name) => `${prefix}${name}`;
    const saStateId = isBusinessSA ? "BusinessSavingsAccelerator" : "SavingsAccelerator";
    const linkedELStateId = isBusinessSA ? "BusinessExpenseLens" : "ExpenseLens";
    const linkedELEvent = `${linkedELStateId}:updated`;
    const saTitle = isBusinessSA
        ? "Business Savings Accelerator"
        : (isBusinessClient ? "Personal Savings Accelerator" : "Savings Accelerator");
    const savingsSubtitle = isBusinessSA
        ? "Pull the business remaining balance from Expense Lens and allocate operating surplus with clarity."
        : "Pull the remaining balance from Expense Lens and optimize how you allocate it for maximum wealth building.";
    const DEFAULT_SAVINGS_HELPER_TEXT = "Default buckets are built for growth-mode clients, but every percentage can be customized.";

    const getDefaultPersonalSavingsAllocationRows = () => ([
        {
            name: "Emergency Reserve / Cash Buffer",
            percent: 30,
            description: "Liquid savings for unexpected expenses, income gaps, deductibles, and short-term stability."
        },
        {
            name: "Short-Term Sinking Funds",
            percent: 15,
            description: "Planned near-term needs like car repairs, travel, gifts, moving costs, deductibles, and annual expenses."
        },
        {
            name: "Mid-Term Opportunity Fund",
            percent: 20,
            description: "Money set aside for goals within roughly 2–5 years such as a home fund, business launch, education, or major life moves."
        },
        {
            name: "Retirement / Roth IRA / Long-Term Investing",
            percent: 25,
            description: "Long-term wealth building for retirement accounts, Roth IRA contributions, brokerage investing, or diversified long-term growth."
        },
        {
            name: "Debt Paydown / Wealth Acceleration",
            percent: 10,
            description: "Extra dollars toward high-interest debt, principal reduction, or intentional wealth-building acceleration."
        }
    ]);

    const getDefaultBusinessSavingsAllocationRows = () => ([
        {
            name: "Tax Reserve",
            percent: 30,
            description: "Set aside money for estimated taxes, payroll taxes, sales tax, and year-end tax obligations."
        },
        {
            name: "Operating Reserve",
            percent: 25,
            description: "Business emergency fund for slow months, delayed receivables, repairs, chargebacks, or unexpected expenses."
        },
        {
            name: "Payroll / Owner Pay Stability",
            percent: 15,
            description: "Stabilizes owner draws, contractor payments, payroll obligations, and predictable compensation."
        },
        {
            name: "Growth / Marketing Reinvestment",
            percent: 20,
            description: "Capital for lead generation, marketing, sales tools, branding, technology, and client acquisition."
        },
        {
            name: "Equipment / Systems / Future Expansion",
            percent: 10,
            description: "Reserved for equipment, software, hiring support, systems, expansion costs, or future business upgrades."
        }
    ]);

    const getDefaultSavingsAllocationRows = () => (
        isBusinessSA ? getDefaultBusinessSavingsAllocationRows() : getDefaultPersonalSavingsAllocationRows()
    );

    const hasMeaningfulSavingsAllocationRows = (rows) => Array.isArray(rows) && rows.some((row) => {
        const name = String(row?.name || '').trim();
        const percent = parseSavingsMoney(row?.percent);
        return name.length > 0 || percent > 0;
    });

    hostElement.innerHTML = `
<div class="networth-tool p-4"
     style="background:radial-gradient(900px 320px at 0% 0%,rgba(166,128,35,.12),transparent 55%),linear-gradient(180deg,rgba(11,21,41,.99),rgba(15,29,56,.99));
            border-radius:20px;box-shadow:0 40px 100px rgba(0,0,0,.58);
            border:1.8px solid rgba(166,128,35,.52);width:min(96vw,1460px);max-width:1460px;margin:0 auto;
            color:#f8fafc;font-family:'Inter',sans-serif;">
    <style>
        .${prefix}-label{display:inline-flex;align-items:center;gap:8px;margin-bottom:6px;font-weight:800;color:#a68023;}
        .${prefix}-i{display:inline-flex;align-items:center;justify-content:center;width:18px;height:18px;border-radius:999px;background:#fff;border:1px solid rgba(210,31,43,.9);color:#d21f2b;font-weight:900;font-size:12px;line-height:1;cursor:pointer;user-select:none;transform:translateY(-1px);box-shadow:0 6px 18px rgba(0,0,0,.08);}
        .${prefix}-i:focus{outline:none;box-shadow:0 0 0 3px rgba(210,31,43,.18),0 10px 25px rgba(0,0,0,.10);}
        #${pid('TipLayer')}{position:fixed;inset:0;pointer-events:none;z-index:2147483647;}
        .${prefix}-tipbox{position:absolute;max-width:min(360px,86vw);background:#fff;color:#111;border:1px solid rgba(0,0,0,.12);border-left:4px solid #d21f2b;padding:12px;border-radius:14px;font-size:12.8px;font-weight:650;line-height:1.35;box-shadow:0 18px 45px rgba(0,0,0,.18);opacity:0;transform:translateY(6px);transition:opacity .12s ease,transform .12s ease;pointer-events:none;white-space:normal;}
        .${prefix}-tipbox b{font-weight:900;}
        .${prefix}-tipbox.show{opacity:1;transform:translateY(0);}
        .savings-accelerator-header{display:flex;align-items:flex-start;justify-content:space-between;gap:16px;flex-wrap:wrap;margin-bottom:18px;}
        .savings-accelerator-title{flex:1 1 320px;min-width:0;}
        .savings-accelerator-title h3{margin:0;color:#a68023;font-weight:900;letter-spacing:.5px;font-size:2rem;}
        .savings-accelerator-title p{margin:8px 0 0;color:#b9c5d8;font-style:italic;}
        .savings-accelerator-actions{display:flex;align-items:center;justify-content:flex-end;gap:10px;flex:0 0 auto;flex-wrap:wrap;}
        .savings-illustration-btn,
        .savings-accelerator-actions .clear-btn{
            min-height:40px!important;
            height:40px!important;
            padding:0 16px!important;
            border-radius:12px!important;
            border:1.5px solid rgba(214,176,90,.88)!important;
            background:linear-gradient(180deg,rgba(9,19,38,.98),rgba(16,31,58,.98))!important;
            color:#f7e7be!important;
            font-weight:800!important;
            font-size:.86rem!important;
            letter-spacing:.01em;
            line-height:1!important;
            display:inline-flex!important;
            align-items:center!important;
            justify-content:center!important;
            gap:8px;
            box-shadow:0 14px 34px rgba(3,8,20,.32),inset 0 1px 0 rgba(255,255,255,.06)!important;
            position:static!important;
            transform:none!important;
            top:auto!important;
            right:auto!important;
            width:auto!important;
            min-width:108px!important;
            margin:0!important;
            text-decoration:none!important;
        }
        .savings-illustration-btn:hover,
        .savings-accelerator-actions .clear-btn:hover{
            background:linear-gradient(180deg,rgba(17,35,66,.99),rgba(24,46,80,.99))!important;
            color:#fff4d4!important;
            border-color:#f1cf82!important;
            box-shadow:0 18px 40px rgba(3,8,20,.42),0 0 0 1px rgba(214,176,90,.14) inset!important;
        }
        .savings-illustration-btn:focus,
        .savings-accelerator-actions .clear-btn:focus{
            outline:none!important;
            box-shadow:0 0 0 3px rgba(214,176,90,.28),0 14px 34px rgba(3,8,20,.32)!important;
        }
        .savings-illustration-btn__icon{display:inline-flex;align-items:center;justify-content:center;flex:0 0 auto;}
        .savings-illustration-btn__icon svg{width:16px;height:16px;display:block;}
        .savings-illustration-backdrop{position:fixed;inset:0;display:none;align-items:center;justify-content:center;padding:16px;background:rgba(3,7,18,.8);backdrop-filter:blur(10px);z-index:2147483000;}
        .savings-illustration-backdrop.is-open{display:flex;}
        .savings-illustration-modal{width:min(1460px,96vw);max-height:min(92vh,940px);overflow:auto;border-radius:28px;border:1px solid rgba(214,176,90,.34);background:radial-gradient(1400px 520px at 0% 0%,rgba(166,128,35,.14),transparent 52%),linear-gradient(180deg,rgba(5,13,28,.995),rgba(10,22,44,.99));box-shadow:0 46px 120px rgba(0,0,0,.58);padding:16px 16px 14px;}
        .savings-illustration-modal-head{display:grid;grid-template-columns:minmax(300px,.92fr) minmax(420px,1.2fr) auto;align-items:start;gap:14px;padding-bottom:12px;border-bottom:1px solid rgba(214,176,90,.18);}
        .savings-illustration-modal-copy{min-width:0;}
        .savings-illustration-modal-copy h4{margin:0;color:#f8fafc;font-size:1.05rem;font-weight:900;letter-spacing:.08em;text-transform:uppercase;}
        .savings-illustration-modal-copy p{margin:8px 0 0;color:#d2d7e1;font-size:.82rem;line-height:1.32;max-width:460px;}
        .savings-illustration-step-counter{display:inline-flex;align-items:center;gap:8px;padding:6px 12px;border-radius:999px;background:rgba(166,128,35,.12);border:1px solid rgba(214,176,90,.34);color:#f4d06f;font-size:.54rem;font-weight:900;letter-spacing:.14em;text-transform:uppercase;margin-bottom:12px;box-shadow:inset 0 1px 0 rgba(255,255,255,.05);}
        .savings-illustration-summary-bar{display:grid;grid-template-columns:repeat(5,minmax(0,1fr));gap:0;min-height:92px;border-radius:20px;border:1px solid rgba(148,163,184,.16);background:linear-gradient(180deg,rgba(14,25,45,.92),rgba(9,18,36,.94));overflow:hidden;align-self:start;}
        .savings-illustration-summary-metric{display:flex;flex-direction:column;justify-content:center;gap:6px;padding:13px 14px;min-width:0;border-left:1px solid rgba(148,163,184,.14);}
        .savings-illustration-summary-metric:first-child{border-left:none;}
        .savings-illustration-summary-metric-label{display:block;color:#94a3b8;font-size:.64rem;font-weight:900;letter-spacing:.08em;text-transform:uppercase;line-height:1.15;}
        .savings-illustration-summary-metric-value{display:block;color:#f8fafc;font-size:.92rem;font-weight:900;line-height:1.1;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;}
        .savings-illustration-summary-metric--income .savings-illustration-summary-metric-value{color:#f8fafc;}
        .savings-illustration-summary-metric--expense .savings-illustration-summary-metric-value{color:#ff6f72;}
        .savings-illustration-summary-metric--available .savings-illustration-summary-metric-value,
        .savings-illustration-summary-metric--allocated .savings-illustration-summary-metric-value{color:#54df7c;}
        .savings-illustration-summary-metric--remaining .savings-illustration-summary-metric-value{color:#f8fafc;}
        .savings-illustration-summary-metric.is-active{background:linear-gradient(180deg,rgba(255,255,255,.04),rgba(255,255,255,.02));box-shadow:inset 0 0 0 1px rgba(255,255,255,.04);}
        .savings-illustration-close{width:42px;height:42px;border-radius:15px;border:1px solid rgba(214,176,90,.56);background:rgba(11,22,43,.94);color:#f8fafc;display:inline-flex;align-items:center;justify-content:center;font-size:1.2rem;font-weight:800;cursor:pointer;box-shadow:inset 0 1px 0 rgba(255,255,255,.05);}
        .savings-illustration-close:hover{background:rgba(18,34,62,.98);border-color:#f1cf82;}
        .savings-illustration-close:focus{outline:none;box-shadow:0 0 0 3px rgba(214,176,90,.22);}
        .savings-illustration-content{padding-top:12px;}
        .savings-illustration-board{position:relative;display:grid;grid-template-columns:minmax(320px,.9fr) minmax(520px,1.4fr);gap:32px;min-height:0;padding:18px 20px;border-radius:24px;border:1px solid rgba(214,176,90,.14);background:linear-gradient(180deg,rgba(6,14,30,.62),rgba(7,16,32,.38));}
        .savings-illustration-left{display:flex;flex-direction:column;justify-content:flex-start;}
        .savings-illustration-rail{display:flex;flex-direction:column;align-items:flex-start;gap:12px;min-height:100%;}
        .savings-illustration-account-flow{position:relative;width:min(100%,430px);}
        .savings-illustration-rail-link{display:flex;align-items:center;justify-content:center;width:min(100%,430px);height:54px;}
        .savings-illustration-rail-link--expense{height:62px;}
        .savings-illustration-rail-link-line{position:relative;width:4px;height:100%;border-radius:999px;background:linear-gradient(180deg,rgba(214,176,90,.95),rgba(214,176,90,.35));}
        .savings-illustration-rail-link--expense .savings-illustration-rail-link-line{background:linear-gradient(180deg,rgba(255,107,107,.98),rgba(255,107,107,.34));}
        .savings-illustration-rail-link-line::after{content:"";position:absolute;left:50%;bottom:-1px;transform:translateX(-50%);border-left:10px solid transparent;border-right:10px solid transparent;border-top:15px solid rgba(214,176,90,.98);}
        .savings-illustration-rail-link--expense .savings-illustration-rail-link-line::after{border-top-color:rgba(255,107,107,.98);}
        .savings-illustration-transfer-arrow{position:absolute;left:calc(100% + 18px);top:50%;width:88px;height:18px;transform:translateY(-50%);display:flex;align-items:center;pointer-events:none;}
        .savings-illustration-transfer-arrow-line{position:relative;width:100%;height:4px;border-radius:999px;background:linear-gradient(90deg,rgba(82,224,130,.98),rgba(82,224,130,.54));}
        .savings-illustration-transfer-arrow-line::after{content:"";position:absolute;right:-1px;top:50%;transform:translateY(-50%);border-top:10px solid transparent;border-bottom:10px solid transparent;border-left:16px solid rgba(82,224,130,.98);}
        .savings-illustration-transfer-arrow-mobile{display:none;}
        .savings-illustration-transfer-arrow-mobile-line{position:relative;width:4px;height:34px;border-radius:999px;background:linear-gradient(180deg,rgba(82,224,130,.98),rgba(82,224,130,.54));}
        .savings-illustration-transfer-arrow-mobile-line::after{content:"";position:absolute;left:50%;bottom:-1px;transform:translateX(-50%);border-left:9px solid transparent;border-right:9px solid transparent;border-top:13px solid rgba(82,224,130,.98);}
        .savings-illustration-node-stack{display:flex;flex-direction:column;gap:8px;width:100%;}
        .savings-illustration-kicker{display:block;color:#f4c95f;font-size:.62rem;font-weight:900;letter-spacing:.07em;text-transform:uppercase;}
        .savings-illustration-kicker--expense{color:#ff5f67;}
        .savings-illustration-card{width:min(100%,430px);padding:16px 18px;border-radius:20px;border:1px solid rgba(214,176,90,.88);background:linear-gradient(180deg,rgba(17,29,52,.96),rgba(12,22,40,.94));box-shadow:0 22px 46px rgba(0,0,0,.22);}
        .savings-illustration-card--account{margin-top:2px;}
        .savings-illustration-card--expense{border-color:rgba(255,95,103,.84);}
        .savings-illustration-card.is-active{box-shadow:0 24px 52px rgba(0,0,0,.24),0 0 0 1px rgba(255,255,255,.05) inset;}
        .savings-illustration-card__body{display:grid;grid-template-columns:70px minmax(0,1fr);gap:16px;align-items:center;}
        .savings-illustration-card__icon{width:70px;height:70px;border-radius:20px;display:inline-flex;align-items:center;justify-content:center;border:1px solid rgba(255,255,255,.06);background:radial-gradient(circle at 30% 30%,rgba(255,255,255,.08),rgba(255,255,255,.02));}
        .savings-illustration-card__icon svg{width:34px;height:34px;display:block;}
        .savings-illustration-card--source .savings-illustration-card__icon,
        .savings-illustration-card--account .savings-illustration-card__icon{color:#f4c95f;background:radial-gradient(circle at 30% 30%,rgba(244,201,95,.14),rgba(244,201,95,.04));}
        .savings-illustration-card--expense .savings-illustration-card__icon{color:#ff6f72;background:radial-gradient(circle at 30% 30%,rgba(255,111,114,.16),rgba(255,111,114,.05));}
        .savings-illustration-card__copy{display:flex;flex-direction:column;gap:4px;min-width:0;}
        .savings-illustration-card__title{display:block;color:#f8fafc;font-size:.96rem;font-weight:900;line-height:1.22;}
        .savings-illustration-card__sub{display:block;color:#d4d9e2;font-size:.75rem;font-weight:700;line-height:1.24;}
        .savings-illustration-card__value{display:block;color:#55dd7c;font-size:1rem;font-weight:900;line-height:1.12;}
        .savings-illustration-card--expense .savings-illustration-card__value{color:#ff6f72;}
        .savings-illustration-right{display:flex;flex-direction:column;gap:12px;min-width:0;padding-left:28px;}
        .savings-illustration-surplus-head{display:flex;flex-direction:column;gap:4px;padding-top:0;}
        .savings-illustration-surplus-label{display:block;color:#55dd7c;font-size:.78rem;font-weight:900;letter-spacing:.03em;text-transform:uppercase;}
        .savings-illustration-surplus-value{display:block;color:#f8fafc;font-size:.88rem;font-weight:900;}
        .savings-illustration-surplus-shell{min-height:100%;}
        .savings-illustration-bucket-list{display:flex;flex-direction:column;gap:12px;padding-left:0;}
        .savings-illustration-bucket-row{position:relative;min-width:0;}
        .savings-illustration-bucket-card{min-height:92px;padding:15px 18px 14px;border-radius:18px;border:1px solid rgba(82,224,130,.34);border-left:4px solid rgba(82,224,130,.94);background:linear-gradient(180deg,rgba(16,31,54,.96),rgba(11,22,41,.94));box-shadow:0 18px 36px rgba(0,0,0,.18);}
        .savings-illustration-bucket-row.is-active .savings-illustration-bucket-card{border-color:rgba(111,241,155,.42);box-shadow:0 22px 44px rgba(0,0,0,.22),0 0 0 1px rgba(255,255,255,.05) inset;}
        .savings-illustration-bucket-card__grid{display:grid;grid-template-columns:68px minmax(220px,1.55fr) minmax(96px,.56fr) minmax(74px,.36fr) minmax(136px,.74fr);gap:14px;align-items:center;min-width:0;}
        .savings-illustration-bucket-card__icon{width:64px;height:64px;border-radius:18px;display:inline-flex;align-items:center;justify-content:center;color:#55dd7c;background:radial-gradient(circle at 30% 30%,rgba(82,224,130,.18),rgba(82,224,130,.06));border:1px solid rgba(82,224,130,.18);}
        .savings-illustration-bucket-card__icon svg{width:31px;height:31px;display:block;}
        .savings-illustration-bucket-card__main{display:flex;flex-direction:column;gap:3px;min-width:0;}
        .savings-illustration-bucket-card__title{display:-webkit-box;color:#f8fafc;font-size:.94rem;font-weight:900;line-height:1.2;-webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden;}
        .savings-illustration-bucket-card__meta{display:block;color:#d4d9e2;font-size:.72rem;font-weight:700;line-height:1.18;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;}
        .savings-illustration-bucket-card__amount,
        .savings-illustration-bucket-card__stat{display:flex;flex-direction:column;gap:4px;min-width:0;padding-left:14px;border-left:1px solid rgba(148,163,184,.14);}
        .savings-illustration-bucket-card__amount-label,
        .savings-illustration-bucket-card__stat-label{display:block;color:#d4d9e2;font-size:.66rem;font-weight:900;line-height:1.08;}
        .savings-illustration-bucket-card__amount-value{display:block;color:#55dd7c;font-size:.94rem;font-weight:900;line-height:1.08;}
        .savings-illustration-bucket-card__amount-share{display:block;color:#f8fafc;font-size:.82rem;font-weight:900;line-height:1.08;}
        .savings-illustration-bucket-card__stat-value{display:block;color:#f8fafc;font-size:.9rem;font-weight:900;line-height:1.08;}
        .savings-illustration-bucket-card__stat--projection .savings-illustration-bucket-card__stat-value{color:#55dd7c;}
        .savings-illustration-footer{display:grid;grid-template-columns:auto 1fr auto;align-items:center;gap:16px;margin-top:14px;padding-top:14px;border-top:1px solid rgba(214,176,90,.18);}
        .savings-illustration-progress{display:flex;align-items:center;justify-content:center;gap:10px;min-width:0;flex-wrap:wrap;}
        .savings-illustration-progress-dot{width:12px;height:12px;border-radius:999px;background:rgba(148,163,184,.26);border:1px solid rgba(148,163,184,.08);box-shadow:inset 0 1px 0 rgba(255,255,255,.05);}
        .savings-illustration-progress-dot.is-active{background:#f4c95f;box-shadow:0 0 0 2px rgba(244,201,95,.12),0 0 14px rgba(244,201,95,.35);}
        .savings-illustration-nav-btn{min-width:112px;min-height:46px;width:auto;padding:0 22px;border-radius:16px;border:1.5px solid rgba(214,176,90,.58);background:linear-gradient(180deg,rgba(16,29,52,.98),rgba(11,22,40,.96));color:#f8fafc;font-size:.86rem;font-weight:900;display:inline-flex;align-items:center;justify-content:center;cursor:pointer;box-shadow:inset 0 1px 0 rgba(255,255,255,.05);}
        .savings-illustration-nav-btn:hover:not(:disabled){background:linear-gradient(180deg,rgba(23,41,70,.99),rgba(15,28,50,.98));border-color:#f1cf82;}
        .savings-illustration-nav-btn:focus{outline:none;box-shadow:0 0 0 3px rgba(214,176,90,.22);}
        .savings-illustration-nav-btn:disabled{opacity:.45;cursor:not-allowed;}
        .savings-illustration-footer .savings-illustration-nav-btn:first-child{justify-self:start;}
        .savings-illustration-footer .savings-illustration-nav-btn:last-child{justify-self:end;}
        .sa-alloc-row{display:grid;gap:8px;margin-bottom:10px;padding:12px 14px;border-radius:14px;border:1.5px solid rgba(166,128,35,.24);background:linear-gradient(180deg,rgba(255,255,255,.055),rgba(255,255,255,.02));box-sizing:border-box;overflow:hidden;}
        .savings-row-header{display:grid;grid-template-columns:minmax(248px,2.25fr) 136px 74px 64px 136px 136px 196px;gap:10px;align-items:end;width:100%;max-width:100%;margin:0 0 12px;padding:0 14px;box-sizing:border-box;color:#c9a448;font-size:.64rem;font-weight:900;letter-spacing:.08em;line-height:1.08;text-transform:uppercase;}
        .savings-row-header.compact{grid-template-columns:minmax(190px,1.8fr) 66px 108px 64px 148px 34px;}
        .savings-row-header span{display:flex;align-items:flex-end;min-width:0;min-height:2.1em;white-space:nowrap;overflow:visible;word-break:normal;}
        .savings-row-header .savings-row-header__multiline{display:block;white-space:normal;line-height:1.02;}
        .savings-row-header .savings-row-header__projection{letter-spacing:.05em;}
        .savings-row-header .savings-row-header__action{justify-self:center;white-space:nowrap;}
        .savings-row{display:grid;grid-template-columns:minmax(248px,2.25fr) 136px 74px 64px 136px 136px 196px;gap:10px;align-items:center;width:100%;max-width:100%;box-sizing:border-box;overflow:hidden;}
        .savings-row>*{min-width:0;}
        .savings-row.compact{grid-template-columns:minmax(190px,1.8fr) 66px 108px 64px 148px 34px;}
        .savings-row .legend-money-input,
        .savings-row .legend-percent-input,
        .savings-row .projected-year-end{
            min-width:0!important;
            width:100%;
            max-width:100%;
        }
        .savings-row .projected-year-end{justify-self:stretch;}
        .savings-row .legend-percent-input{justify-self:stretch;}
        .savings-row .legend-percent-field{
            padding:0 1px 0 6px!important;
            text-align:center;
        }
        .savings-row .legend-percent-suffix{
            padding:0 6px 0 1px;
        }
        .savings-row .legend-money-field{
            padding:0 10px 0 0!important;
        }
        .savings-name,.savings-start-date{width:100%;max-width:100%;box-sizing:border-box;background:rgba(255,255,255,.92)!important;color:#1a2540!important;border:1.2px solid rgba(166,128,35,.4)!important;border-radius:8px;font-weight:700;padding:8px 10px;outline:none;}
        .savings-name{text-overflow:ellipsis;}
        .savings-start-date{padding-right:12px;}
        .savings-name:focus,.savings-start-date:focus{border-color:#ddb457!important;box-shadow:0 0 0 2px rgba(166,128,35,.2)!important;}
        .legend-money-input{display:flex;align-items:center;width:100%;max-width:100%;min-height:42px;box-sizing:border-box;background:#f4f4f2;border:1px solid rgba(198,151,45,.75);border-radius:10px;overflow:hidden;box-shadow:inset 0 1px 0 rgba(255,255,255,.4);}
        .legend-money-prefix{flex:0 0 auto;padding:0 8px 0 12px;font-weight:800;color:#0b2a66;line-height:1;pointer-events:none;user-select:none;}
        .legend-money-field{flex:1 1 auto;min-width:0;height:100%;border:none!important;background:transparent!important;box-shadow:none!important;border-radius:0!important;padding:0 12px 0 0!important;color:#0b2a66!important;font-weight:800!important;outline:none!important;}
        .legend-money-input:focus-within{border-color:#d4af37;box-shadow:0 0 0 3px rgba(212,175,55,.18);}
        .legend-percent-input{display:flex;align-items:center;width:100%;max-width:100%;min-height:42px;box-sizing:border-box;background:#f4f4f2;border:1px solid rgba(198,151,45,.75);border-radius:10px;overflow:hidden;box-shadow:inset 0 1px 0 rgba(255,255,255,.4);}
        .legend-percent-field{flex:1 1 auto;min-width:0;width:100%;height:100%;margin:0!important;border:none!important;background:transparent!important;box-shadow:none!important;border-radius:0!important;padding:0 4px 0 10px!important;color:#0b2a66!important;font-weight:800!important;outline:none!important;appearance:none!important;}
        .legend-percent-suffix{flex:0 0 auto;padding:0 10px 0 4px;font-weight:800;color:#0b2a66;pointer-events:none;user-select:none;line-height:1;}
        .legend-percent-input:focus-within{border-color:#ddb457;box-shadow:0 0 0 2px rgba(166,128,35,.2);}
        .projected-year-end{justify-self:stretch;}
        .projected-year-end .projected-prefix{flex:0 0 auto;padding:0 8px 0 12px;font-weight:800;line-height:1;pointer-events:none;user-select:none;color:#2f8f55;}
        .projected-year-end .projected-value{flex:1 1 auto;min-width:0;padding:0 12px 0 0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:#2f8f55;font-weight:900;font-size:1rem;letter-spacing:.01em;}
        .projected-year-end.is-neutral .projected-prefix,
        .projected-year-end.is-neutral .projected-value{color:#0b2a66;}
        .remove-row{display:flex;align-items:center;justify-content:center;width:28px;height:28px;border:1px solid rgba(166,128,35,.42);border-radius:999px;background:rgba(166,128,35,.08);color:#a68023;font-weight:900;cursor:pointer;padding:0;font-size:1rem;line-height:1;justify-self:center;align-self:center;box-shadow:inset 0 1px 0 rgba(255,255,255,.06);}
        .remove-row:hover{color:#f2c867;border-color:rgba(199,153,49,.7);background:rgba(166,128,35,.16);}
        .sa-alloc-toggle{min-width:40px;border:1px solid rgba(166,128,35,.42);border-radius:8px;padding:5px 8px;background:rgba(166,128,35,.10);color:#f8fafc;font-weight:800;cursor:pointer;font-size:.78rem;white-space:nowrap;}
        .sa-alloc-drawer{display:none;grid-template-columns:repeat(2,minmax(0,1fr));gap:10px;padding-top:8px;margin-top:6px;border-top:1px solid rgba(166,128,35,.15);}
        .sa-alloc-drawer.is-open{display:grid;}
        .sa-alloc-note{grid-column:1 / -1;color:#b9c5d8;font-size:.74rem;font-style:italic;line-height:1.35;}
        @media (max-width: 1260px){
            .savings-illustration-modal-head{grid-template-columns:minmax(260px,.95fr) minmax(320px,1.2fr) auto;}
            .savings-illustration-summary-bar{grid-template-columns:repeat(3,minmax(0,1fr));}
            .savings-illustration-board{grid-template-columns:minmax(300px,.88fr) minmax(440px,1.18fr);gap:24px;}
            .savings-illustration-right{padding-left:18px;}
            .savings-illustration-transfer-arrow{left:calc(100% + 12px);width:64px;}
            .savings-illustration-bucket-card__grid{grid-template-columns:64px minmax(180px,1.42fr) minmax(86px,.5fr) minmax(68px,.34fr) minmax(120px,.64fr);}
        }
        @media (max-width: 980px){
            .savings-illustration-modal-head{grid-template-columns:1fr auto;}
            .savings-illustration-summary-bar{grid-column:1 / -1;grid-template-columns:repeat(5,minmax(0,1fr));min-height:auto;}
            .savings-illustration-board{grid-template-columns:1fr;gap:16px;min-height:0;}
            .savings-illustration-right{padding-left:0;}
            .savings-illustration-transfer-arrow{display:none;}
            .savings-illustration-transfer-arrow-mobile{display:flex;align-items:center;justify-content:center;height:42px;}
            .savings-illustration-bucket-card__grid{grid-template-columns:64px minmax(0,1.3fr) minmax(84px,.48fr) minmax(64px,.3fr) minmax(112px,.58fr);}
        }
        @media (max-width: 760px){
            .savings-accelerator-actions{width:100%;justify-content:flex-start;}
            .savings-illustration-btn,
            .savings-accelerator-actions .clear-btn{flex:1 1 calc(50% - 5px);}
            .savings-row-header,.savings-row-header.compact{display:none;}
            .savings-row,.savings-row.compact{grid-template-columns:1fr 1fr;}
            .savings-name{grid-column:1 / -1;}
            .savings-start-date{grid-column:span 1;}
            .projected-year-end{grid-column:span 1;}
            .remove-row{justify-self:end;}
            .sa-alloc-drawer{grid-template-columns:1fr;}
            .savings-illustration-backdrop{padding:10px;}
            .savings-illustration-modal{padding:14px 12px 12px;max-height:90vh;}
            .savings-illustration-modal-head{grid-template-columns:1fr auto;gap:12px;}
            .savings-illustration-modal-copy h4{font-size:1.3rem;}
            .savings-illustration-modal-copy p{display:block;font-size:.92rem;}
            .savings-illustration-step-counter{margin-bottom:12px;}
            .savings-illustration-summary-bar{grid-template-columns:repeat(2,minmax(0,1fr));}
            .savings-illustration-summary-metric{min-height:76px;padding:14px 12px;border-left:none;border-top:1px solid rgba(148,163,184,.14);}
            .savings-illustration-summary-metric:nth-child(-n+2){border-top:none;}
            .savings-illustration-board{padding:12px;}
            .savings-illustration-card{width:100%;padding:16px;}
            .savings-illustration-card__body{grid-template-columns:64px minmax(0,1fr);gap:14px;}
            .savings-illustration-card__icon{width:64px;height:64px;border-radius:18px;}
            .savings-illustration-card__icon svg{width:34px;height:34px;}
            .savings-illustration-account-flow{width:100%;}
            .savings-illustration-rail-link{width:100%;}
            .savings-illustration-transfer-arrow-mobile{height:38px;}
            .savings-illustration-bucket-card{padding:14px 14px 12px;}
            .savings-illustration-bucket-card__grid{grid-template-columns:1fr;gap:12px;}
            .savings-illustration-bucket-card__icon{width:64px;height:64px;border-radius:18px;}
            .savings-illustration-bucket-card__icon svg{width:32px;height:32px;}
            .savings-illustration-bucket-card__amount,
            .savings-illustration-bucket-card__stat{padding-left:0;border-left:none;border-top:1px solid rgba(148,163,184,.14);padding-top:10px;}
            .savings-illustration-bucket-list{gap:12px;}
            .savings-illustration-footer{grid-template-columns:1fr;gap:14px;}
            .savings-illustration-progress{order:3;}
            .savings-illustration-nav-btn{width:100%;}
        }
    </style>
    <div id="${pid('TipLayer')}"></div>
    <div class="savings-accelerator-header">
        <div class="savings-accelerator-title">
            <h3>${saTitle}</h3>
            <p>${savingsSubtitle}</p>
        </div>
        <div id="${pid('ActionRow')}" class="savings-accelerator-actions" aria-label="Savings Accelerator actions"></div>
    </div>
    <div class="row mb-3" style="display:flex;gap:20px;flex-wrap:wrap;">
        <div style="flex:1;min-width:200px;max-width:380px;">
            <div class="${prefix}-label">Savings Allocation</div>
            <div class="legend-money-input sa-source-money">
                <span class="legend-money-prefix">$</span>
                <input id="${pid('Allocation')}" type="text" class="legend-money-field" readonly data-money-input="true"
                       placeholder="Sync from Expense Lens…"
                       style="font-size:1.1rem;color:#d4a820;cursor:default;"/>
            </div>
            <div style="font-size:0.72rem;color:#94A3B8;margin-top:5px;font-style:italic;">
                Auto-synced · ${isBusinessSA ? "Business " : ""}Expense Lens remaining balance
            </div>
        </div>
    </div>
    <div class="mt-4">
        <h5 style="color:#a68023;font-weight:700;border-bottom:1px solid rgba(166,128,35,0.35);padding-bottom:6px;">Savings Allocation Plan</h5>
        <div style="margin-top:8px;color:#b9c5d8;font-size:.78rem;font-style:italic;">
            ${DEFAULT_SAVINGS_HELPER_TEXT}
        </div>
        <div class="d-flex align-items-center mb-3" style="gap:8px;">
            <div style="flex:2;font-weight:700;color:#fff;text-align:left;">
                Remaining Allocation: <span id="${pid('Remaining')}" style="color:#a68023;font-weight:900;">$0</span>
            </div>
            <div style="flex:1;text-align:right;font-weight:700;color:#fff;">
                Total Allocated: <span id="${pid('PctTotal')}" style="color:#a68023;font-weight:900;">0%</span>
            </div>
        </div>
        <div class="savings-row-header${isDualPanel ? ' compact' : ''}" aria-hidden="true">
            ${isDualPanel
                ? `
                    <span>Bucket Name</span>
                    <span>Alloc %</span>
                    <span>Alloc $</span>
                    <span>APR %</span>
                    <span class="savings-row-header__multiline savings-row-header__projection">Projected<br>Year-End</span>
                    <span class="savings-row-header__action">Edit</span>
                `
                : `
                    <span>Bucket Name</span>
                    <span>Allocation Amount</span>
                    <span class="savings-row-header__multiline">Allocation<br>%</span>
                    <span>APR %</span>
                    <span>Start Date</span>
                    <span>Starting Balance</span>
                    <span class="savings-row-header__multiline savings-row-header__projection">Projected<br>Year-End</span>
                `}
        </div>
        <div id="${pid('AllocContainer')}" class="mt-3"></div>
        <div class="d-flex gap-2 mt-3">
            <button id="${pid('AddCat')}" class="btn btn-outline-gold" style="font-weight:600;">+ Add Category</button>
            <button id="${pid('DelCat')}" class="btn btn-outline-gold" style="font-weight:600;">- Delete Last</button>
        </div>
    </div>
    <div id="${pid('Tips')}"
         style="margin-top:18px;padding:12px 16px;border-radius:6px;border:2px solid rgba(166,128,35,0.45);background:rgba(166,128,35,0.10);font-weight:700;font-size:0.875rem;color:#d4a820;letter-spacing:0.01em;font-style:italic;transition:background .2s,color .2s,border-color .2s;">
        Direct extra cash strategically across savings, debt reduction, and key priorities.
    </div>
    <div id="${pid('IllustrationBackdrop')}" class="savings-illustration-backdrop" hidden aria-hidden="true">
        <div id="${pid('IllustrationModal')}" class="savings-illustration-modal" role="dialog" aria-modal="true" aria-labelledby="${pid('IllustrationTitle')}" aria-describedby="${pid('IllustrationSubtitle')}">
            <div class="savings-illustration-modal-head">
                <div class="savings-illustration-modal-copy">
                    <div id="${pid('IllustrationCounter')}" class="savings-illustration-step-counter" aria-live="polite">Step 1 of 1</div>
                    <h4 id="${pid('IllustrationTitle')}">Cashflow Illustration</h4>
                    <p id="${pid('IllustrationSubtitle')}">See how income, expenses, and surplus allocation work together.</p>
                </div>
                <div id="${pid('IllustrationSummary')}" class="savings-illustration-summary-bar" aria-live="polite"></div>
                <button id="${pid('IllustrationClose')}" type="button" class="savings-illustration-close" aria-label="Close cashflow illustration">&times;</button>
            </div>
            <div id="${pid('IllustrationContent')}" class="savings-illustration-content"></div>
            <div class="savings-illustration-footer">
                <button id="${pid('IllustrationBack')}" type="button" class="savings-illustration-nav-btn" aria-label="Go to previous illustration step">Back</button>
                <div id="${pid('IllustrationProgress')}" class="savings-illustration-progress" aria-hidden="true"></div>
                <button id="${pid('IllustrationNext')}" type="button" class="savings-illustration-nav-btn" aria-label="Go to next illustration step">Next</button>
            </div>
        </div>
    </div>
</div>`;

    const container = hostElement.querySelector('.networth-tool');
    applyToolBoxStyles(container);
    const saAllocationInput = document.getElementById(pid('Allocation'));
    const saTips = document.getElementById(pid('Tips'));
    const allocationContainer = document.getElementById(pid('AllocContainer'));
    const addBtn = document.getElementById(pid('AddCat'));
    const delBtn = document.getElementById(pid('DelCat'));
    const saPctTotal = document.getElementById(pid('PctTotal'));
    const saRemaining = document.getElementById(pid('Remaining'));
    const actionRow = document.getElementById(pid('ActionRow'));
    const illustrationBackdrop = document.getElementById(pid('IllustrationBackdrop'));
    const illustrationContent = document.getElementById(pid('IllustrationContent'));
    const illustrationCounter = document.getElementById(pid('IllustrationCounter'));
    const illustrationBackBtn = document.getElementById(pid('IllustrationBack'));
    const illustrationNextBtn = document.getElementById(pid('IllustrationNext'));
    const illustrationCloseBtn = document.getElementById(pid('IllustrationClose'));
    const illustrationSummary = document.getElementById(pid('IllustrationSummary'));
    const illustrationProgress = document.getElementById(pid('IllustrationProgress'));

    let categoryCount = 0;
    let latestExpenseLensState = null;
    let savingsIllustrationData = { steps: [] };
    let savingsIllustrationStepIndex = 0;
    let savingsIllustrationOpen = false;
    let savingsIllustrationTrigger = null;

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
    const tipLayer = document.getElementById(pid('TipLayer'));
    const tipBox = document.createElement('div');
    tipBox.className = `${prefix}-tipbox`;
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

    container.querySelectorAll(`.${prefix}-i`).forEach(el => {
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

    const parseSavingsMoney = (value) => +(String(value || '').replace(/[,$\s]/g, '')) || 0;

    const formatSavingsMoneyText = (value) => {
        const rounded = Math.round(Number(value) || 0);
        const sign = rounded < 0 ? '-' : '';
        return `${sign}$${Math.abs(rounded).toLocaleString()}`;
    };

    const escapeSavingsIllustrationHtml = (value) => String(value ?? '').replace(/[&<>"']/g, (char) => ({
        '&': '&amp;',
        '<': '&lt;',
        '>': '&gt;',
        '"': '&quot;',
        "'": '&#39;'
    }[char] || char));

    const formatSavingsIllustrationPercent = (value) => {
        const numeric = parseFloat(String(value ?? '').replace(/[^0-9.-]/g, ''));
        if (!Number.isFinite(numeric)) return '0%';
        const digits = Math.abs(numeric % 1) > 0.001 ? 1 : 0;
        return `${numeric.toLocaleString(undefined, {
            minimumFractionDigits: digits,
            maximumFractionDigits: 1
        })}%`;
    };

    const formatSavingsIllustrationDate = (value) => {
        const normalized = normalizeSavingsDateInput(value);
        if (!normalized) return 'Not set';
        const parsed = new Date(`${normalized}T00:00:00`);
        if (Number.isNaN(parsed.getTime())) return 'Not set';
        return parsed.toLocaleDateString('en-US', {
            month: '2-digit',
            day: '2-digit',
            year: 'numeric'
        });
    };

    const getSavingsIllustrationIcon = (kind) => {
        if (kind === 'source') {
            return `
                <svg viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
                    <path d="M4 20h16M6 20V8l6-4 6 4v12M9 10h.01M15 10h.01M9 14h.01M15 14h.01" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/>
                </svg>`;
        }
        if (kind === 'account') {
            return `
                <svg viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
                    <path d="M3 9.5 12 5l9 4.5M5 11h14M6 11v7M10 11v7M14 11v7M18 11v7M4 20h16" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/>
                </svg>`;
        }
        if (kind === 'expense') {
            return `
                <svg viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
                    <path d="M7 4h10v16l-2-1.5L13 20l-2-1.5L9 20l-2-1.5L5 20V6a2 2 0 0 1 2-2Z" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/>
                    <path d="M9 9h6M9 13h4" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/>
                    <path d="M14 15.5c.8 0 1.5-.56 1.5-1.25S14.8 13 14 13s-1.5.56-1.5 1.25S13.2 15.5 14 15.5Zm0 0v1" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/>
                </svg>`;
        }
        if (kind === 'surplus' || kind === 'bucket-growth') {
            return `
                <svg viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
                    <path d="M5 17v-4M10 17V9M15 17v-6M19 7v10M6 9l4-3 4 2 5-4" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/>
                    <path d="M17 4h4v4" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/>
                </svg>`;
        }
        if (kind === 'bucket-protection') {
            return `
                <svg viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
                    <path d="M12 3 19 6v5c0 4.5-2.8 7.7-7 10-4.2-2.3-7-5.5-7-10V6l7-3Z" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/>
                    <path d="m9.5 12.5 1.8 1.8 3.2-4" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/>
                </svg>`;
        }
        if (kind === 'bucket-short') {
            return `
                <svg viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
                    <path d="M6.5 15h11l-1-3.8a2 2 0 0 0-1.94-1.5H9.44a2 2 0 0 0-1.94 1.5L6.5 15Zm0 0H5.4a1.4 1.4 0 0 0-1.4 1.4V18h1.5m12-3h1.1A1.4 1.4 0 0 1 20 16.4V18h-1.5M8 18a1.5 1.5 0 1 0 0 3 1.5 1.5 0 0 0 0-3Zm8 0a1.5 1.5 0 1 0 0 3 1.5 1.5 0 0 0 0-3ZM9 9 8 7m8 2 1-2" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/>
                </svg>`;
        }
        if (kind === 'bucket-retirement') {
            return `
                <svg viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
                    <path d="M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8Zm-7 8a7 7 0 0 1 14 0M4 20h16" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/>
                </svg>`;
        }
        if (kind === 'bucket-debt') {
            return `
                <svg viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
                    <path d="M5 16a7 7 0 1 1 14 0M5 16h14M12 16l4-4" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/>
                    <circle cx="12" cy="16" r="1.2" fill="currentColor"/>
                </svg>`;
        }
        return `
            <svg viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
                <rect x="5" y="5" width="14" height="14" rx="3" stroke="currentColor" stroke-width="1.8"/>
                <path d="M9 12h6M12 9v6" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/>
            </svg>`;
    };

    const getSavingsIllustrationBucketIconKey = (name) => {
        const normalized = String(name || '').toLowerCase();
        if (/(emergency|reserve|protect|protection|safety|buffer)/.test(normalized)) return 'bucket-protection';
        if (/(sinking|car|vehicle|travel|vacation|home|repair|medical|short)/.test(normalized)) return 'bucket-short';
        if (/(growth|opportun|wealth|mid|invest|brokerage|education)/.test(normalized)) return 'bucket-growth';
        if (/(retire|roth|ira|401|long.?term)/.test(normalized)) return 'bucket-retirement';
        if (/(debt|loan|paydown|credit|acceler)/.test(normalized)) return 'bucket-debt';
        return 'bucket-generic';
    };

    const buildSavingsIllustrationSummaryMetric = ({ label, value, tone = 'income', active = false }) => `
        <div class="savings-illustration-summary-metric savings-illustration-summary-metric--${tone}${active ? ' is-active' : ''}">
            <span class="savings-illustration-summary-metric-label">${escapeSavingsIllustrationHtml(label)}</span>
            <span class="savings-illustration-summary-metric-value">${escapeSavingsIllustrationHtml(value)}</span>
        </div>`;

    const buildSavingsIllustrationProgressDots = (total, activeIndex) => Array.from({ length: total }, (_, index) => `
        <span class="savings-illustration-progress-dot${index === activeIndex ? ' is-active' : ''}"></span>
    `).join('');

    const buildSavingsIllustrationCard = ({
        kicker,
        title,
        subtitle = '',
        value = '',
        tone = 'source',
        icon = tone,
        active = false
    }) => `
        <div class="savings-illustration-node-stack">
            <span class="savings-illustration-kicker${tone === 'expense' ? ' savings-illustration-kicker--expense' : ''}">${escapeSavingsIllustrationHtml(kicker)}</span>
            <div class="savings-illustration-card savings-illustration-card--${tone}${active ? ' is-active' : ''}">
                <div class="savings-illustration-card__body">
                    <span class="savings-illustration-card__icon">${getSavingsIllustrationIcon(icon)}</span>
                    <div class="savings-illustration-card__copy">
                        <span class="savings-illustration-card__title">${escapeSavingsIllustrationHtml(title)}</span>
                        ${subtitle ? `<span class="savings-illustration-card__sub">${escapeSavingsIllustrationHtml(subtitle)}</span>` : ''}
                        ${value ? `<span class="savings-illustration-card__value">${escapeSavingsIllustrationHtml(value)}</span>` : ''}
                    </div>
                </div>
            </div>
        </div>`;

    const buildSavingsIllustrationBucketCard = ({ bucket, active = false }) => `
        <div class="savings-illustration-bucket-row${active ? ' is-active' : ''}">
            <div class="savings-illustration-bucket-card">
                <div class="savings-illustration-bucket-card__grid">
                    <span class="savings-illustration-bucket-card__icon">${getSavingsIllustrationIcon(getSavingsIllustrationBucketIconKey(bucket.name))}</span>
                    <div class="savings-illustration-bucket-card__main">
                        <span class="savings-illustration-bucket-card__title">${escapeSavingsIllustrationHtml(bucket.name)}</span>
                        <span class="savings-illustration-bucket-card__meta">${escapeSavingsIllustrationHtml(`Start: ${bucket.startDateText}`)}</span>
                        <span class="savings-illustration-bucket-card__meta">${escapeSavingsIllustrationHtml(`Balance: ${bucket.startingBalanceText}`)}</span>
                    </div>
                    <div class="savings-illustration-bucket-card__amount">
                        <span class="savings-illustration-bucket-card__amount-label">Allocated</span>
                        <span class="savings-illustration-bucket-card__amount-value">${escapeSavingsIllustrationHtml(bucket.allocationAmountText)}</span>
                        <span class="savings-illustration-bucket-card__amount-share">${escapeSavingsIllustrationHtml(bucket.allocationPercentText)}</span>
                    </div>
                    <div class="savings-illustration-bucket-card__stat">
                        <span class="savings-illustration-bucket-card__stat-label">APR</span>
                        <span class="savings-illustration-bucket-card__stat-value">${escapeSavingsIllustrationHtml(bucket.aprPercentText)}</span>
                    </div>
                    <div class="savings-illustration-bucket-card__stat savings-illustration-bucket-card__stat--projection">
                        <span class="savings-illustration-bucket-card__stat-label">Projected Year-End</span>
                        <span class="savings-illustration-bucket-card__stat-value">${escapeSavingsIllustrationHtml(bucket.projectedYearEndText)}</span>
                    </div>
                </div>
            </div>
        </div>`;

    const todayIsoDate = () => {
        const now = new Date();
        const local = new Date(now.getTime() - now.getTimezoneOffset() * 60000);
        return local.toISOString().slice(0, 10);
    };

    const normalizeSavingsDateInput = (value) => {
        if (!value) return '';
        const parsed = new Date(`${String(value).slice(0, 10)}T00:00:00`);
        if (Number.isNaN(parsed.getTime())) return '';
        return `${parsed.getFullYear()}-${String(parsed.getMonth() + 1).padStart(2, '0')}-${String(parsed.getDate()).padStart(2, '0')}`;
    };

    const monthsToYearEnd = (value) => {
        const now = new Date();
        const currentYear = now.getFullYear();
        const fallback = new Date(`${todayIsoDate()}T00:00:00`);
        const rawDate = normalizeSavingsDateInput(value);
        const parsed = rawDate ? new Date(`${rawDate}T00:00:00`) : fallback;
        if (Number.isNaN(parsed.getTime())) return 0;
        const yearEnd = new Date(currentYear, 11, 31);
        if (parsed > yearEnd) return 0;
        const effective = parsed.getFullYear() < currentYear ? new Date(currentYear, 0, 1) : parsed;
        return Math.max(0, 12 - effective.getMonth());
    };

    const calculateProjectedYearEndValue = ({ allocationAmount, aprPercent, allocationStartDate, startingBalance }) => {
        const months = monthsToYearEnd(allocationStartDate);
        const monthlyContribution = Math.max(0, Number(allocationAmount) || 0);
        const openingBalance = Math.max(0, parseSavingsMoney(startingBalance));
        const aprRate = Math.max(0, parseSavingsMoney(aprPercent)) / 100;

        if (months <= 0) {
            return { months: 0, projectedValue: openingBalance };
        }

        if (aprRate > 0) {
            const monthlyRate = aprRate / 12;
            const growthFactor = Math.pow(1 + monthlyRate, months);
            const contributionGrowth = monthlyContribution * ((growthFactor - 1) / monthlyRate);
            return {
                months,
                projectedValue: (openingBalance * growthFactor) + contributionGrowth
            };
        }

        return {
            months,
            projectedValue: openingBalance + (monthlyContribution * months)
        };
    };

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

    const getExpenseLensIncomeTotal = (state) => {
        const hasSplitIncome =
            String(state?.primaryIncome ?? '').trim() !== ''
            || String(state?.spouseIncome ?? '').trim() !== '';
        if (hasSplitIncome) {
            return parseSavingsMoney(state?.primaryIncome) + parseSavingsMoney(state?.spouseIncome);
        }
        return parseSavingsMoney(state?.income);
    };

    const buildSavingsIllustrationData = () => {
        const sourceLabel = isBusinessSA ? 'Company / Revenue Source' : 'Company / Income Source';
        const accountLabel = isBusinessSA ? 'Business Operating Account' : 'Personal Checking / Savings';
        const expensesLabel = isBusinessSA ? 'Total Business Expenses' : 'Total Expenses';
        const surplusLabel = isBusinessSA ? 'Business Surplus / Remaining Allocation' : 'Surplus / Remaining Allocation';
        const expenseState = latestExpenseLensState || {};
        const monthlyIncome = getExpenseLensIncomeTotal(expenseState);
        const totalExpenses = calculateExpenseLensMonthlyTotal(expenseState);
        const savingsAllocation = parseSavingsMoney(saAllocationInput.value);
        const rows = Array.from(allocationContainer.querySelectorAll('.sa-alloc-row')).map((row, index) => {
            const projectedValue = parseSavingsMoney(row.querySelector('.projected-value')?.textContent || '');
            const allocationAmount = parseSavingsMoney(row.querySelector('.sa-alloc-amount')?.value || '');
            const allocationPercent = row.querySelector('.sa-alloc-percent')?.value || '';
            const aprPercent = row.querySelector('.sa-alloc-apr')?.value || '';
            const startDate = row.querySelector('.sa-alloc-start-date')?.value || '';
            const startingBalance = row.querySelector('.sa-alloc-starting-balance')?.value || '';
            const bucketName = String(row.querySelector('.sa-alloc-name')?.value || '').trim() || `Bucket ${index + 1}`;

            return {
                index,
                name: bucketName,
                allocationAmount,
                allocationAmountText: formatSavingsMoneyText(allocationAmount),
                allocationPercentText: formatSavingsIllustrationPercent(allocationPercent),
                aprPercentText: formatSavingsIllustrationPercent(aprPercent),
                startDateText: formatSavingsIllustrationDate(startDate),
                startingBalanceText: formatSavingsMoneyText(parseSavingsMoney(startingBalance)),
                projectedYearEndValue: projectedValue,
                projectedYearEndText: formatSavingsMoneyText(projectedValue)
            };
        });

        const totalAllocated = rows.reduce((sum, row) => sum + row.allocationAmount, 0);
        const remainingAllocation = parseSavingsMoney(saRemaining.textContent) || (savingsAllocation - totalAllocated);
        const projectedYearEndTotal = rows.reduce((sum, row) => sum + row.projectedYearEndValue, 0);

        const steps = [
            {
                kind: 'origin',
                header: 'Where your money starts',
                sourceLabel,
                monthlyIncome,
                visibleBucketCount: 0,
                activeBucketIndex: -1
            },
            {
                kind: 'account',
                header: 'Money enters your account',
                sourceLabel,
                accountLabel,
                monthlyIncome,
                visibleBucketCount: 0,
                activeBucketIndex: -1
            },
            {
                kind: 'expense',
                header: 'Your lifestyle costs come out first',
                accountLabel,
                expensesLabel,
                totalExpenses,
                visibleBucketCount: 0,
                activeBucketIndex: -1
            },
            {
                kind: 'surplus',
                header: 'Your remaining cashflow becomes the opportunity',
                accountLabel,
                surplusLabel,
                savingsAllocation,
                remainingAllocation,
                visibleBucketCount: 0,
                activeBucketIndex: -1
            },
            ...rows.map((bucket, index) => ({
                kind: 'bucket',
                header: `Allocating to ${bucket.name}`,
                accountLabel,
                bucket,
                visibleBucketCount: index + 1,
                activeBucketIndex: index
            })),
            {
                kind: 'summary',
                header: 'Your complete cashflow system',
                sourceLabel,
                accountLabel,
                expensesLabel,
                totalExpenses,
                totalAllocated,
                remainingAllocation,
                projectedYearEndTotal,
                rows,
                visibleBucketCount: rows.length,
                activeBucketIndex: -1
            }
        ];

        return {
            monthlyIncome,
            totalExpenses,
            savingsAllocation,
            totalAllocated,
            remainingAllocation,
            projectedYearEndTotal,
            rows,
            steps
        };
    };

    const renderSavingsIllustrationStep = () => {
        if (!illustrationContent || !illustrationCounter || !illustrationBackBtn || !illustrationNextBtn) return;
        if (!savingsIllustrationData.steps.length) {
            illustrationContent.innerHTML = '';
            illustrationCounter.textContent = 'STEP 0 OF 0';
            if (illustrationSummary) illustrationSummary.innerHTML = '';
            if (illustrationProgress) illustrationProgress.innerHTML = '';
            illustrationBackBtn.disabled = true;
            illustrationNextBtn.disabled = true;
            return;
        }

        savingsIllustrationStepIndex = Math.max(0, Math.min(savingsIllustrationStepIndex, savingsIllustrationData.steps.length - 1));
        const step = savingsIllustrationData.steps[savingsIllustrationStepIndex];
        const stepCountText = `STEP ${savingsIllustrationStepIndex + 1} OF ${savingsIllustrationData.steps.length}`;
        const money = formatSavingsMoneyText;
        const stepHasAccount = step.kind !== 'origin';
        const stepHasExpenses = ['expense', 'surplus', 'bucket', 'summary'].includes(step.kind);
        const stepHasSurplus = ['surplus', 'bucket', 'summary'].includes(step.kind);
        const visibleBuckets = savingsIllustrationData.rows.slice(0, step.visibleBucketCount || 0);
        const sourceParts = String(step.sourceLabel || savingsIllustrationData.steps[0]?.sourceLabel || '')
            .split('/')
            .map((part) => part.trim())
            .filter(Boolean);
        const sourceTitle = sourceParts[0] || 'Company';
        const sourceSubtitle = sourceParts.slice(1).join(' / ') || (isBusinessSA ? 'Revenue Source' : 'Income Source');
        const summaryMetrics = [
            buildSavingsIllustrationSummaryMetric({
                label: 'Income',
                value: money(savingsIllustrationData.monthlyIncome),
                tone: 'income',
                active: step.kind === 'origin' || step.kind === 'account'
            }),
            buildSavingsIllustrationSummaryMetric({
                label: 'Expenses',
                value: money(savingsIllustrationData.totalExpenses),
                tone: 'expense',
                active: step.kind === 'expense'
            }),
            buildSavingsIllustrationSummaryMetric({
                label: 'Available',
                value: money(savingsIllustrationData.savingsAllocation),
                tone: 'available',
                active: step.kind === 'surplus'
            }),
            buildSavingsIllustrationSummaryMetric({
                label: 'Allocated',
                value: money(savingsIllustrationData.totalAllocated),
                tone: 'allocated',
                active: step.kind === 'bucket'
            }),
            buildSavingsIllustrationSummaryMetric({
                label: 'Remaining',
                value: money(savingsIllustrationData.remainingAllocation),
                tone: 'remaining',
                active: step.kind === 'summary'
            })
        ].join('');

        const sourceCard = buildSavingsIllustrationCard({
            kicker: 'Income Source',
            title: sourceTitle,
            subtitle: sourceSubtitle,
            tone: 'source',
            icon: 'source',
            active: step.kind === 'origin'
        });
        const accountCard = stepHasAccount
            ? buildSavingsIllustrationCard({
                kicker: 'Cash Received',
                title: step.accountLabel,
                value: money(savingsIllustrationData.monthlyIncome),
                tone: 'account',
                icon: 'account',
                active: step.kind === 'account'
            })
            : '';
        const expenseCard = stepHasExpenses
            ? buildSavingsIllustrationCard({
                kicker: 'Expenses',
                title: step.expensesLabel,
                subtitle: 'From Expense Lens',
                value: money(savingsIllustrationData.totalExpenses),
                tone: 'expense',
                icon: 'expense',
                active: step.kind === 'expense'
            })
            : '';
        const bucketRows = visibleBuckets.map((bucket) => buildSavingsIllustrationBucketCard({
            bucket,
            active: step.kind === 'bucket' && bucket.index === step.activeBucketIndex
        })).join('');

        illustrationCounter.textContent = stepCountText;
        if (illustrationSummary) illustrationSummary.innerHTML = summaryMetrics;
        if (illustrationProgress) {
            illustrationProgress.innerHTML = buildSavingsIllustrationProgressDots(
                savingsIllustrationData.steps.length,
                savingsIllustrationStepIndex
            );
        }
        illustrationContent.innerHTML = `
            <div class="savings-illustration-board" aria-label="${escapeSavingsIllustrationHtml(step.header)}">
                <div class="savings-illustration-left">
                    <div class="savings-illustration-rail">
                        ${sourceCard}
                        ${stepHasAccount ? `
                            <div class="savings-illustration-rail-link" aria-hidden="true">
                                <div class="savings-illustration-rail-link-line"></div>
                            </div>
                            <div class="savings-illustration-account-flow">
                                ${accountCard}
                                ${stepHasSurplus ? `
                                    <div class="savings-illustration-transfer-arrow" aria-hidden="true">
                                        <div class="savings-illustration-transfer-arrow-line"></div>
                                    </div>
                                ` : ''}
                            </div>
                        ` : ''}
                        ${stepHasExpenses ? `
                            <div class="savings-illustration-rail-link savings-illustration-rail-link--expense" aria-hidden="true">
                                <div class="savings-illustration-rail-link-line"></div>
                            </div>
                            ${expenseCard}
                        ` : ''}
                    </div>
                </div>
                ${stepHasSurplus ? `
                    <div class="savings-illustration-transfer-arrow-mobile" aria-hidden="true">
                        <div class="savings-illustration-transfer-arrow-mobile-line"></div>
                    </div>
                ` : ''}
                <div class="savings-illustration-right">
                    ${stepHasSurplus ? `
                        <div class="savings-illustration-surplus-head">
                            <span class="savings-illustration-surplus-label">Surplus Allocation</span>
                            <span class="savings-illustration-surplus-value">${escapeSavingsIllustrationHtml(`${money(savingsIllustrationData.savingsAllocation)} Available to Allocate`)}</span>
                        </div>
                        <div class="savings-illustration-surplus-shell">
                            <div class="savings-illustration-bucket-list">${bucketRows}</div>
                        </div>
                    ` : ''}
                </div>
            </div>`;
        illustrationBackBtn.disabled = savingsIllustrationStepIndex === 0;
        illustrationNextBtn.disabled = false;
        illustrationNextBtn.textContent = savingsIllustrationStepIndex === savingsIllustrationData.steps.length - 1
            ? 'Restart'
            : 'Next';
        illustrationNextBtn.setAttribute('aria-label', illustrationNextBtn.textContent);
    };

    const refreshSavingsIllustrationData = () => {
        savingsIllustrationData = buildSavingsIllustrationData();
        if (savingsIllustrationStepIndex > savingsIllustrationData.steps.length - 1) {
            savingsIllustrationStepIndex = Math.max(0, savingsIllustrationData.steps.length - 1);
        }
        if (savingsIllustrationOpen) {
            renderSavingsIllustrationStep();
        }
    };

    const closeSavingsIllustration = () => {
        if (!illustrationBackdrop) return;
        savingsIllustrationOpen = false;
        illustrationBackdrop.hidden = true;
        illustrationBackdrop.classList.remove('is-open');
        illustrationBackdrop.setAttribute('aria-hidden', 'true');
        if (savingsIllustrationTrigger && typeof savingsIllustrationTrigger.focus === 'function') {
            requestAnimationFrame(() => {
                try { savingsIllustrationTrigger.focus({ preventScroll: true }); } catch (_) { }
            });
        }
    };

    const openSavingsIllustration = (trigger) => {
        if (!illustrationBackdrop) return;
        savingsIllustrationTrigger = trigger || savingsIllustrationTrigger || null;
        savingsIllustrationStepIndex = 0;
        refreshSavingsIllustrationData();
        savingsIllustrationOpen = true;
        illustrationBackdrop.hidden = false;
        illustrationBackdrop.classList.add('is-open');
        illustrationBackdrop.setAttribute('aria-hidden', 'false');
        renderSavingsIllustrationStep();
        requestAnimationFrame(() => {
            try { illustrationCloseBtn?.focus({ preventScroll: true }); } catch (_) { }
        });
    };

    const applyExpenseLensToSavingsAccelerator = async (event) => {
        const state = event?.detail || await loadPersistedState(linkedELStateId);
        latestExpenseLensState = state || {};
        const income = getExpenseLensIncomeTotal(state);
        const monthlyExpenses = calculateExpenseLensMonthlyTotal(state);
        const hasSavedRemaining = state && Object.prototype.hasOwnProperty.call(state, 'monthlyRemaining');
        const savingsAllocation = hasSavedRemaining ? parseSavingsMoney(state.monthlyRemaining) : income - monthlyExpenses;
        const hasCategoryData = Array.isArray(state?.categories)
            && state.categories.some(category => parseSavingsMoney(category?.amount || category?.occurrenceAmount));
        const hasSourceData = !!state
            && (income !== 0 || monthlyExpenses !== 0 || hasCategoryData);

        saAllocationInput.value = hasSourceData ? formatNumber(savingsAllocation) : '';
        refreshSurplus();
    };

    const saveAllocationState = () => {
        const allocations = [];
        allocationContainer.querySelectorAll('.sa-alloc-row').forEach(row => {
            allocations.push({
                name: row.querySelector('.sa-alloc-name').value || '',
                percent: row.querySelector('.sa-alloc-percent').value || '',
                description: row.dataset.description || '',
                aprPercent: row.querySelector('.sa-alloc-apr')?.value || '',
                allocationStartDate: row.querySelector('.sa-alloc-start-date')?.value || '',
                startingBalance: row.querySelector('.sa-alloc-starting-balance')?.value || ''
            });
        });
        savePersistedState(saStateId, { allocations });
    };

    const injectDefaultSavingsAllocationRows = () => {
        getDefaultSavingsAllocationRows().forEach((allocation) => {
            createAllocationRow(++categoryCount, {
                name: allocation.name,
                percent: allocation.percent,
                description: allocation.description,
                isTemplate: true,
                allocationStartDate: todayIsoDate()
            });
        });
    };

    const loadAllocationState = async () => {
        allocationContainer.innerHTML = '';
        categoryCount = 0;

        const state = await loadPersistedState(saStateId);

        if (hasMeaningfulSavingsAllocationRows(state?.allocations)) {
            (state.allocations || []).forEach(a => {
                createAllocationRow(++categoryCount, {
                    name: a.name,
                    percent: a.percent,
                    description: a.description || '',
                    isTemplate: false,
                    aprPercent: a.aprPercent || '',
                    allocationStartDate: a.allocationStartDate || '',
                    startingBalance: a.startingBalance || ''
                });
            });
        } else {
            injectDefaultSavingsAllocationRows();
        }

        refreshSurplus();
    };

    const makeSaMoney = (input) => {
        const wrap = document.createElement('div');
        wrap.className = 'legend-money-input';
        const pre = document.createElement('span');
        pre.className = 'legend-money-prefix';
        pre.textContent = '$';
        wrap.append(pre, input);
        return wrap;
    };

    const makeSaPct = (input) => {
        const wrap = document.createElement('div');
        wrap.className = 'legend-percent-input';
        const suf = document.createElement('span');
        suf.className = 'legend-percent-suffix';
        suf.textContent = '%';
        wrap.append(input, suf);
        return wrap;
    };

    const createAllocationRow = (index, options = {}) => {
        const {
            name: preName = '',
            percent: prePercent = '',
            description = '',
            isTemplate = false,
            aprPercent = '',
            allocationStartDate = '',
            startingBalance = ''
        } = options;

        const row = document.createElement('div');
        row.className = 'sa-alloc-row';
        row.dataset.description = description || '';
        row.dataset.isTemplate = isTemplate ? 'true' : 'false';

        const grid = document.createElement('div');
        grid.className = isDualPanel ? 'savings-row compact' : 'savings-row';

        const name = document.createElement('input');
        name.className = 'sa-alloc-name savings-name';
        name.placeholder = 'Bucket Name';
        name.value = preName;
        name.title = description || '';
        name.addEventListener('input', saveAllocationState);

        const amt = document.createElement('input');
        amt.className = 'sa-alloc-amount legend-money-field allocation-amount';
        amt.readOnly = true;
        amt.placeholder = '0';
        const amtWrap = makeSaMoney(amt);

        const pct = document.createElement('input');
        pct.className = 'sa-alloc-percent legend-percent-field allocation-percent';
        pct.value = prePercent || '';
        pct.placeholder = '0';
        pct.oninput = refreshSurplus;
        const pctWrap = makeSaPct(pct);

        const apr = document.createElement('input');
        apr.className = 'sa-alloc-apr legend-percent-field apr-percent';
        apr.placeholder = '0';
        apr.value = aprPercent || '';
        apr.addEventListener('input', refreshSurplus);
        const aprWrap = makeSaPct(apr);

        const startDate = document.createElement('input');
        startDate.type = 'date';
        startDate.className = 'sa-alloc-start-date savings-start-date';
        startDate.value = normalizeSavingsDateInput(allocationStartDate) || todayIsoDate();
        startDate.addEventListener('input', refreshSurplus);
        startDate.addEventListener('change', refreshSurplus);

        const startingBalanceInput = document.createElement('input');
        startingBalanceInput.className = 'sa-alloc-starting-balance legend-money-field starting-balance';
        startingBalanceInput.placeholder = '0';
        startingBalanceInput.value = startingBalance || '';
        startingBalanceInput.addEventListener('input', refreshSurplus);
        const startingWrap = makeSaMoney(startingBalanceInput);

        const projectedDiv = document.createElement('div');
        projectedDiv.className = 'legend-money-input projected-year-end sa-alloc-projected';
        const projectedPrefix = document.createElement('span');
        projectedPrefix.className = 'projected-prefix';
        projectedPrefix.textContent = '$';
        const projectedValue = document.createElement('strong');
        projectedValue.className = 'projected-value';
        projectedValue.textContent = '0';
        projectedDiv.append(projectedPrefix, projectedValue);

        const del = document.createElement('button');
        del.type = 'button';
        del.className = 'remove-row';
        del.textContent = '×';
        del.title = 'Remove bucket';
        del.onclick = () => { allocationContainer.removeChild(row); refreshSurplus(); };

        const drawer = document.createElement('div');
        drawer.className = 'sa-alloc-drawer';
        const note = document.createElement('div');
        note.className = 'sa-alloc-note';
        note.textContent = description || 'Adjust APR, start date, and opening balance to refine the year-end projection.';

        if (isDualPanel) {
            const editBtn = document.createElement('button');
            editBtn.type = 'button';
            editBtn.className = 'sa-alloc-toggle';
            const syncLabel = () => { editBtn.textContent = drawer.classList.contains('is-open') ? 'Hide' : 'Edit'; };
            editBtn.addEventListener('click', () => { drawer.classList.toggle('is-open'); syncLabel(); });
            syncLabel();
            grid.append(name, pctWrap, amtWrap, aprWrap, projectedDiv, editBtn);
            const delRow = document.createElement('div');
            delRow.style.cssText = 'display:flex;justify-content:flex-end;align-items:center;grid-column:1/-1;';
            delRow.appendChild(del);
            drawer.append(startDate, startingWrap, note, delRow);
            fitSingleLineControlText(name, { minSize: 10, maxSize: 14 });
            fitSingleLineControlText(amt, { minSize: 10, maxSize: 14, reserve: 18 });
            fitSingleLineControlText(pct, { minSize: 10, maxSize: 14, reserve: 18 });
            fitSingleLineControlText(apr, { minSize: 10, maxSize: 14, reserve: 18 });
        } else {
            grid.append(name, amtWrap, pctWrap, aprWrap, startDate, startingWrap, projectedDiv);
            drawer.append(note);
        }

        row.append(grid, drawer);
        allocationContainer.appendChild(row);
        fitSingleLineControlText(name, { minSize: 10, maxSize: 14, reserve: 12 });
        markNeutral(name);
        markWithSuffix(markNeutral, pct);
        markWithSuffix(markNeutral, amt);
        markWithSuffix(markNeutral, apr);
        markWithSuffix(markNeutral, startingBalanceInput);
    };

    const refreshSurplus = () => {
        const hasAllocationValue = String(saAllocationInput.value || '').trim() !== '';
        const surplus = parseSavingsMoney(saAllocationInput.value);

        let usedPct = 0;
        let totalAllocatedAmt = 0;

        allocationContainer.querySelectorAll('.sa-alloc-row').forEach(row => {
            const pctInput = row.querySelector('.sa-alloc-percent');
            const amtInput = row.querySelector('.sa-alloc-amount');
            const aprInput = row.querySelector('.sa-alloc-apr');
            const startDateInput = row.querySelector('.sa-alloc-start-date');
            const startingBalanceInput = row.querySelector('.sa-alloc-starting-balance');
            let pct = +pctInput.value || 0;
            if (usedPct + pct > 100) pct = Math.max(0, 100 - usedPct);
            usedPct += pct;

            const amt = surplus > 0 ? (pct / 100) * surplus : 0;
            totalAllocatedAmt += amt;
            const projection = calculateProjectedYearEndValue({
                allocationAmount: amt,
                aprPercent: aprInput?.value || '',
                allocationStartDate: startDateInput?.value || '',
                startingBalance: startingBalanceInput?.value || ''
            });

            pctInput.value = pct;
            amtInput.value = Math.round(amt).toLocaleString();
            const projectedEl = row.querySelector('.sa-alloc-projected');
            if (projectedEl) {
                const roundedProjection = Math.round(projection.projectedValue);
                const strong = projectedEl.querySelector('.projected-value');
                if (strong) strong.textContent = Math.abs(roundedProjection).toLocaleString();
                const projectionSummary = projection.months > 0
                    ? `${projection.months} monthly period${projection.months === 1 ? '' : 's'} through year end`
                    : 'No remaining monthly periods in the current year';
                projectedEl.removeAttribute('title');
                projectedEl.classList.toggle('is-neutral', roundedProjection <= 0);
                projectedEl.setAttribute('aria-label', `$${Math.abs(roundedProjection).toLocaleString()} projected year-end value. ${projectionSummary}.`);
            }
        });

        const remaining = surplus - totalAllocatedAmt;

        saPctTotal.textContent = usedPct.toFixed(1) + '%';
        saRemaining.textContent = formatSavingsMoneyText(remaining);

        saTips.textContent = !hasAllocationValue
            ? 'Complete Expense Lens first so Savings Accelerator can pull the remaining balance automatically.'
            : surplus <= 0
            ? '⚠️ Expense Lens shows no remaining balance to allocate. Adjust income or bills there first.'
            : '✅ Good remaining balance! Allocate it strategically across savings and financial goals.';

        if (surplus > 0) markWithSuffix(markIncome, saAllocationInput);
        else if (surplus < 0) markWithSuffix(markExpense, saAllocationInput);
        else markWithSuffix(markNeutral, saAllocationInput);

        if (usedPct >= 100) markExpense(saPctTotal); else markGold(saPctTotal);
        markGold(saRemaining);

        // Rows — percent input + % suffix, name, amount + $ suffix
        allocationContainer.querySelectorAll('.sa-alloc-percent').forEach(p => markWithSuffix(markNeutral, p));
        allocationContainer.querySelectorAll('.sa-alloc-name').forEach(n => markNeutral(n));
        allocationContainer.querySelectorAll('.sa-alloc-apr').forEach(p => markWithSuffix(markNeutral, p));
        allocationContainer.querySelectorAll('.sa-alloc-starting-balance').forEach(a => markWithSuffix(markNeutral, a));
        allocationContainer.querySelectorAll('.sa-alloc-amount').forEach(a => {
            if (surplus > 0) markWithSuffix(markIncome, a);
            else if (surplus < 0) markWithSuffix(markExpense, a);
            else markWithSuffix(markNeutral, a);
        });
        allocationContainer.querySelectorAll('.sa-alloc-projected').forEach(el => {
            const strong = el.querySelector('.projected-value');
            const val = strong ? parseSavingsMoney(strong.textContent) : 0;
            el.classList.toggle('is-neutral', val <= 0);
        });

        saveAllocationState();
        refreshSavingsIllustrationData();
    };

    const illustrationBtn = document.createElement('button');
    illustrationBtn.type = 'button';
    illustrationBtn.className = 'savings-illustration-btn';
    illustrationBtn.setAttribute('aria-haspopup', 'dialog');
    illustrationBtn.setAttribute('aria-controls', pid('IllustrationModal'));
    illustrationBtn.setAttribute('aria-label', 'Open cashflow illustration');
    illustrationBtn.innerHTML = `
        <span class="savings-illustration-btn__icon" aria-hidden="true">
            <svg viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg">
                <path d="M3 7h6v4H3V7Zm12 0h6v4h-6V7ZM9 15h6v4H9v-4ZM9 9h6m-3 0v6" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/>
            </svg>
        </span>
        <span>Illustration</span>`;
    illustrationBtn.addEventListener('click', () => openSavingsIllustration(illustrationBtn));
    actionRow?.appendChild(illustrationBtn);

    allocationContainer.addEventListener('input', () => refreshSavingsIllustrationData());
    allocationContainer.addEventListener('change', () => refreshSavingsIllustrationData());

    illustrationBackBtn?.addEventListener('click', () => {
        if (savingsIllustrationStepIndex > 0) {
            savingsIllustrationStepIndex -= 1;
            renderSavingsIllustrationStep();
        }
    });

    illustrationNextBtn?.addEventListener('click', () => {
        if (!savingsIllustrationData.steps.length) return;
        if (savingsIllustrationStepIndex >= savingsIllustrationData.steps.length - 1) {
            savingsIllustrationStepIndex = 0;
        } else {
            savingsIllustrationStepIndex += 1;
        }
        renderSavingsIllustrationStep();
    });

    illustrationCloseBtn?.addEventListener('click', closeSavingsIllustration);
    illustrationBackdrop?.addEventListener('click', (event) => {
        if (event.target === illustrationBackdrop) {
            closeSavingsIllustration();
        }
    });

    addBtn.onclick = () => { createAllocationRow(++categoryCount); refreshSurplus(); };
    delBtn.onclick = () => {
        const last = allocationContainer.lastElementChild;
        if (last) { allocationContainer.removeChild(last); refreshSurplus(); }
    };

    addClearButton(container, () => {
        allocationContainer.innerHTML = '';
        categoryCount = 0;
        injectDefaultSavingsAllocationRows();
        saPctTotal.textContent = '0%';
        saRemaining.textContent = '$0';
        saTips.textContent = 'Direct extra cash strategically across savings, debt reduction, and key priorities.';
        clearPersistedState(saStateId);
        hideTip();
        closeSavingsIllustration();
        refreshSurplus();
    }, actionRow);

    toolContext.onWindow('keydown', (event) => {
        if (event.key === 'Escape' && savingsIllustrationOpen) {
            event.preventDefault();
            closeSavingsIllustration();
        }
    });

    await loadAllocationState();
    await applyExpenseLensToSavingsAccelerator();
    toolContext.onWindow(linkedELEvent, applyExpenseLensToSavingsAccelerator);
    refreshSurplus();

    }; // end renderSavingsAcceleratorInstance

    if (isBusinessClient) {
        const popoutBody = createDualToolPopout(
            "Savings Accelerator",
            "Personal and business savings allocation side by side, outside the normal tool container."
        );
        popoutBody.innerHTML = `
            <div class="expense-lens-dual-shell">
                <div class="expense-lens-dual-panel" id="savingsPersonalHost"></div>
                <div class="expense-lens-dual-panel" id="savingsBusinessHost"></div>
            </div>
        `;
        await renderSavingsAcceleratorInstance("SavingsAccelerator", document.getElementById('savingsPersonalHost'));
        await renderSavingsAcceleratorInstance("BusinessSavingsAccelerator", document.getElementById('savingsBusinessHost'));
    } else {
        await renderSavingsAcceleratorInstance("SavingsAccelerator", embedContainer);
    }
    } catch(e) { console.error('SavingsAccelerator error:', e); }
}


/* -------------------------------
    3️⃣ EXPENSE LENS (ELEVATED)
--------------------------------*/
if (t.id === "ExpenseLens" || t.id === "BusinessExpenseLens") {
    try {
        const renderExpenseLensInstance = async (renderToolId, hostElement) => {
        const isBusinessExpenseLens = renderToolId === "BusinessExpenseLens";
        const isDualPanel = hostElement.classList.contains('expense-lens-dual-panel');
        const expenseLensToolStateId = isBusinessExpenseLens ? "BusinessExpenseLens" : "ExpenseLens";
        const expenseLensUpdatedEvent = `${expenseLensToolStateId}:updated`;
        const expenseLensIdPrefix = isBusinessExpenseLens ? "elBusiness" : "elPersonal";
        const expenseLensTitle = isBusinessExpenseLens
            ? "Business Expenses"
            : "Personal Expenses";
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

            <div id="${elId('MarginWrap')}" class="d-flex gap-2 mt-3" style="margin-top:18px; gap:12px; align-items:center; flex-wrap:wrap;">
                <button id="${elId('AddCat')}"
                        class="btn btn-outline-gold"
                        style="font-weight:600;">
                    + Add Category
                </button>
                <div id="${elId('ActionMeta')}" style="display:flex; align-items:center; gap:12px; flex-wrap:wrap;">
                    <div id="${elId('Margin')}"
                         style="display:inline-flex;align-items:center;height:38px;padding:0 16px;
                                border-radius:6px;border:2px solid rgba(100,116,139,0.35);background:rgba(255,255,255,0.04);
                                font-weight:800;font-size:0.875rem;white-space:nowrap;color:#64748B;letter-spacing:0.01em;
                                transition:background .2s,color .2s,border-color .2s;">
                        Remaining Balance: $0
                    </div>
                </div>
            </div>

            <div id="${elId('Tips')}"
                 style="margin-top:18px;padding:12px 16px;border-radius:6px;border:2px solid rgba(166,128,35,0.45);background:rgba(166,128,35,0.10);font-weight:700;font-size:0.875rem;color:#d4a820;letter-spacing:0.01em;font-style:italic;transition:background .2s,color .2s,border-color .2s;">
                ${expenseLensDefaultTip}
            </div>
        </div>`;

        const container = hostElement.querySelector('.networth-tool');
        const categoriesContainer = elById("Categories");
        const addBtn = elById("AddCat");
        const elTips = elById("Tips");
        const elMargin = elById("Margin");
        const elMarginWrap = elById("MarginWrap");
        const elActionMeta = elById("ActionMeta");
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
                const primaryIncome = elPrimaryIncome?.value || '';
                const spouseIncome = elSpouseIncome?.value || '';
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
                    const isPinned = row.dataset.isPinned === 'true';
                    categories.push({ index, name, amount, due, frequency, isTemplate, isPinned });
                });
                const state = { income, primaryIncome, spouseIncome, categories, ...extraState };
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
                    if (elPrimaryIncome && state.primaryIncome) {
                        elPrimaryIncome.value = state.primaryIncome;
                        if (elSpouseIncome && state.spouseIncome) elSpouseIncome.value = state.spouseIncome;
                        // Recompute total from split values; ignore stored income to avoid drift
                        const pri = parseFloat((state.primaryIncome || '').replace(/,/g, '')) || 0;
                        const spo = parseFloat((state.spouseIncome || '').replace(/,/g, '')) || 0;
                        const total = pri + spo;
                        elIncome.value = total > 0 ? total.toLocaleString() : '';
                    } else {
                        elIncome.value = state.income || '';
                    }

                    if (state.categories && state.categories.length > 0) {
                        state.categories.forEach(cat => {
                            createCategoryRow(++categoryCount, cat.name, cat.amount, cat.due || '', cat.frequency || cat.recurrence, cat.isTemplate === true, cat.isPinned === true);
                            categoriesCreated++;
                        });
                    }
                }
                if (categoriesCreated === 0) injectDefaultExpenseRows();
                refreshExpenseLens({ sortRows: true });
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

        const refreshExpenseLensViews = (options = {}) => {
            const shouldSortRows = !!options.sortRows;
            if (elActiveWeek) {
                elApplyWeekFilter(elActiveWeek, { sortRows: shouldSortRows });
                return;
            }
            refreshExpenseLens({ sortRows: shouldSortRows });
            if (weekPanel?.style.display !== 'none') renderWeekPanel();
        };

        const isExpenseRowPinned = (row) => row?.dataset?.isPinned === 'true';

        const keepPinnedExpenseRowsAtTop = () => {
            const rows = Array.from(categoriesContainer.querySelectorAll(`[id^="${elId('CatRow')}"]`));
            if (rows.length < 2) return;

            const ordered = [
                ...rows.filter(isExpenseRowPinned),
                ...rows.filter(row => !isExpenseRowPinned(row))
            ];
            const changed = ordered.some((row, index) => row !== rows[index]);
            if (!changed) return;
            ordered.forEach(row => categoriesContainer.appendChild(row));
        };

        const sortExpenseRowsByAllocatedPercent = () => {
            const rows = Array.from(categoriesContainer.querySelectorAll(`[id^="${elId('CatRow')}"]`));
            if (rows.length < 2) return;

            const mapped = rows
                .map((row, order) => {
                    const sortValue = Number.parseFloat(row.dataset.expenseSortValue || '0');
                    const amount = Number.parseFloat(row.dataset.expenseSortAmount || '0');
                    return {
                        row,
                        order,
                        isPinned: isExpenseRowPinned(row),
                        sortValue: Number.isFinite(sortValue) ? sortValue : 0,
                        hasAmount: Number.isFinite(amount) && amount > 0
                    };
                });
            const sorted = [
                ...mapped.filter(item => item.isPinned),
                ...mapped.filter(item => !item.isPinned)
                .sort((a, b) => {
                    if (b.sortValue !== a.sortValue) return b.sortValue - a.sortValue;
                    if (a.hasAmount !== b.hasAmount) return a.hasAmount ? -1 : 1;
                    return a.order - b.order;
                })
            ];

            const changed = sorted.some((item, index) => item.row !== rows[index]);
            if (!changed) return;
            sorted.forEach(item => categoriesContainer.appendChild(item.row));
        };

        // -----------------------------
        // Create Category Row
        // -----------------------------
        const createCategoryRow = (index, preName = '', preAmount = '', preDue = '', preFrequency = 'monthly', isTemplate = false, isPinned = false) => {
            const div = document.createElement("div");
            div.className = "d-flex align-items-center";
            div.id = `${elId('CatRow')}${index}`;
            div.dataset.isTemplate = isTemplate ? 'true' : 'false';
            div.dataset.isPinned = isPinned ? 'true' : 'false';
            div.style.background = "linear-gradient(180deg, rgba(255,255,255,.055), rgba(255,255,255,.02))";
            div.style.padding = isDualPanel ? "7px" : "10px";
            div.style.borderRadius = "10px";
            div.style.border = "1.5px solid rgba(166,128,35,.24)";
            div.style.columnGap = isDualPanel ? "7px" : "12px";
            div.style.rowGap = "8px";
            div.style.flexWrap = "nowrap";
            div.style.minWidth = "0";
            div.style.overflow = isDualPanel ? "hidden" : "";

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
            nameInput.style.flex = isDualPanel ? "1 1 240px" : "1 1 220px";
            nameInput.style.minWidth = isDualPanel ? "112px" : "";
            nameInput.style.boxSizing = "border-box";
            nameInput.value = preName;
            nameInput.addEventListener("input", refreshExpenseLensViews);

            // Due date field
            const dueWrapper = document.createElement("div");
            dueWrapper.style.position = "relative";
            dueWrapper.style.flex = isDualPanel ? "0 1 118px" : "1 1 140px";
            dueWrapper.style.minWidth = isDualPanel ? "104px" : "130px";
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
            dueInput.addEventListener("blur", () => refreshExpenseLensViews({ sortRows: true }));
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
            frequencySelect.style.flex = isDualPanel ? "0 1 104px" : "0 1 132px";
            frequencySelect.style.minWidth = isDualPanel ? "92px" : "124px";
            frequencySelect.style.boxSizing = "border-box";
            EL_BILL_FREQUENCIES.forEach(option => {
                const opt = document.createElement("option");
                opt.value = option.value;
                opt.textContent = option.label;
                frequencySelect.appendChild(opt);
            });
            frequencySelect.value = resolvedPreFrequency;
            frequencySelect.addEventListener("change", () => refreshExpenseLensViews({ sortRows: true }));

            const amountWrapper = document.createElement("div");
            amountWrapper.style.position = "relative";
            amountWrapper.style.flex = isDualPanel ? "0 1 110px" : "1 1 150px";
            amountWrapper.style.minWidth = isDualPanel ? "88px" : "140px";

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
            percentSpan.style.minWidth = isDualPanel ? "38px" : "80px";
            percentSpan.style.flex = isDualPanel ? "0 0 42px" : "0 0 90px";
            percentSpan.style.textAlign = "right";
            percentSpan.style.fontWeight = "700";
            percentSpan.style.color = "#1E3A8A";

            const deleteBtn = document.createElement("button");
            deleteBtn.textContent = "✕";
            deleteBtn.style.border = "none";
            deleteBtn.style.background = "transparent";
            deleteBtn.style.fontWeight = "900";
            deleteBtn.style.flex = isDualPanel ? "0 0 16px" : "";
            deleteBtn.style.padding = isDualPanel ? "0" : "";
            const isInsuranceRow = isTemplate && preName.toLowerCase().includes("insurance");
            if (isInsuranceRow) {
                deleteBtn.style.opacity = "0.2";
                deleteBtn.style.cursor = "not-allowed";
                deleteBtn.style.color = "#94a3b8";
                deleteBtn.setAttribute("disabled", "true");
                deleteBtn.setAttribute("aria-disabled", "true");
                deleteBtn.title = "Insurance rows cannot be removed";
            } else {
                deleteBtn.style.color = "#1E3A8A";
                deleteBtn.style.cursor = "pointer";
                deleteBtn.addEventListener("click", () => {
                    categoriesContainer.removeChild(div);
                    refreshExpenseLensViews();
                });
            }

            // Format numbers with commas on blur
            amountInput.addEventListener("blur", () => {
                amountInput.value = formatNumber(amountInput.value);
                refreshExpenseLensViews({ sortRows: true });
            });

            amountInput.addEventListener("input", refreshExpenseLensViews);

            const leftControls = document.createElement("div");
            leftControls.style.cssText = `display:flex;align-items:center;justify-content:flex-start;gap:${isDualPanel ? "3px" : "5px"};flex:0 0 ${isDualPanel ? "38px" : "54px"};min-width:${isDualPanel ? "38px" : "54px"};`;

            // Drag handle — drag only activates from this grip, never from inputs
            const dragHandle = document.createElement("span");
            dragHandle.textContent = "⠿";
            dragHandle.title = "Drag to reorder";
            dragHandle.style.cssText = `cursor:grab;color:#1E3A8A;font-size:${isDualPanel ? "1rem" : "1.2rem"};display:inline-flex;align-items:center;justify-content:center;width:${isDualPanel ? "14px" : "18px"};height:${isDualPanel ? "26px" : "30px"};user-select:none;opacity:0.55;touch-action:none;`;
            dragHandle.addEventListener("pointerdown", () => { div.draggable = true; });
            dragHandle.addEventListener("pointerup",   () => { div.draggable = false; });
            dragHandle.addEventListener("pointercancel", () => { div.draggable = false; });

            const pinBtn = document.createElement("button");
            pinBtn.type = "button";
            pinBtn.style.cssText = `display:inline-flex;align-items:center;justify-content:center;width:${isDualPanel ? "20px" : "28px"};height:${isDualPanel ? "24px" : "30px"};border-radius:7px;border:1px solid rgba(30,58,138,.18);background:rgba(255,255,255,.04);color:#1E3A8A;font-size:${isDualPanel ? ".78rem" : ".9rem"};font-weight:900;line-height:1;cursor:pointer;padding:0;flex:0 0 ${isDualPanel ? "20px" : "28px"};`;

            const syncPinButton = () => {
                const pinned = isExpenseRowPinned(div);
                pinBtn.textContent = pinned ? "★" : "☆";
                pinBtn.title = pinned ? "Pinned to top" : "Pin to top";
                pinBtn.setAttribute("aria-label", pinned ? "Unpin category from top" : "Pin category to top");
                pinBtn.setAttribute("aria-pressed", pinned ? "true" : "false");
                pinBtn.style.background = pinned ? "rgba(166,128,35,.22)" : "rgba(255,255,255,.04)";
                pinBtn.style.borderColor = pinned ? "rgba(166,128,35,.65)" : "rgba(30,58,138,.18)";
                pinBtn.style.color = pinned ? "#A68023" : "#1E3A8A";
                pinBtn.style.opacity = pinned ? "1" : ".62";
                div.style.boxShadow = pinned ? "inset 4px 0 0 rgba(166,128,35,.86)" : "";
            };

            pinBtn.addEventListener("click", (e) => {
                e.preventDefault();
                e.stopPropagation();
                div.dataset.isPinned = isExpenseRowPinned(div) ? 'false' : 'true';
                syncPinButton();
                keepPinnedExpenseRowsAtTop();
                refreshExpenseLensViews();
            });
            syncPinButton();
            leftControls.appendChild(dragHandle);
            leftControls.appendChild(pinBtn);

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
                elDragSrc = null;
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
                    keepPinnedExpenseRowsAtTop();
                    div.style.border = "1.5px solid rgba(166,128,35,.24)";
                    refreshExpenseLensViews();
                }
            });

            div.appendChild(leftControls);
            div.appendChild(nameInput);
            div.appendChild(dueWrapper);
            div.appendChild(frequencySelect);
            div.appendChild(amountWrapper);
            div.appendChild(percentSpan);
            div.appendChild(deleteBtn);
            categoriesContainer.appendChild(div);

            if (isDualPanel) {
                fitSingleLineControlText(nameInput, { minSize: 10, maxSize: 14 });
                fitSingleLineControlText(dueInput, { minSize: 10, maxSize: 13 });
                fitSingleLineControlText(frequencySelect, { minSize: 10, maxSize: 13, reserve: 18 });
                fitSingleLineControlText(amountInput, { minSize: 10, maxSize: 14, reserve: 24 });
            }

            if (preAmount) refreshExpenseLens();
        };

        // -----------------------------
        // Refresh Function
        // -----------------------------
        const refreshExpenseLens = (options = {}) => {
            const shouldSortRows = !!options.sortRows;
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
                const rowEl = input.closest(`[id^="${elId('CatRow')}"]`);
                if (rowEl) {
                    rowEl.dataset.expenseSortValue = String(income > 0 ? (rowTotal / income) * 100 : rowTotal);
                    rowEl.dataset.expenseSortAmount = String(rowTotal);
                }
                const isPinned = isExpenseRowPinned(rowEl);
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
                    isPinned,
                    occurrenceAmount: val,
                    _isPinned: isPinned,
                    _sortValue: income > 0 ? (rowTotal / income) * 100 : rowTotal,
                    _sortOrder: categoriesData.length
                });

                if (elActiveWeek && occurrenceCount === 0) return;
                totalSpent += rowTotal;
            });

            const remaining = income - totalSpent;
            const pct = income > 0 ? (totalSpent / income * 100) : 0;

            if (elActiveWeek) {
                elMargin.textContent = `${elActiveWeek.label} Due: $${totalSpent.toLocaleString()}`;
                elMargin.style.background = 'rgba(239,68,68,0.12)';
                elMargin.style.color = '#ef4444';
                elMargin.style.borderColor = 'rgba(239,68,68,0.45)';
            } else {
                elMargin.textContent = `Remaining Balance: $${remaining.toLocaleString()}`;
                if (remaining >= 0) {
                    elMargin.style.background = 'rgba(34,197,94,0.12)';
                    elMargin.style.color = '#22c55e';
                    elMargin.style.borderColor = 'rgba(34,197,94,0.45)';
                } else {
                    elMargin.style.background = 'rgba(239,68,68,0.12)';
                    elMargin.style.color = '#ef4444';
                    elMargin.style.borderColor = 'rgba(239,68,68,0.45)';
                }
            }

            // Top remaining balance badge — always reflects full-month income vs all monthly bills
            const monthlyRemaining = income - monthlyTotalSpent;
            const badge = elById('RemainingBadge');
            if (badge) {
                if (monthlyRemaining >= 0) {
                    badge.textContent = `Remaining: $${monthlyRemaining.toLocaleString()}`;
                    badge.style.background = 'rgba(34,197,94,0.12)';
                    badge.style.color = '#22c55e';
                    badge.style.borderColor = 'rgba(34,197,94,0.45)';
                } else {
                    badge.textContent = `Remaining: -$${Math.abs(monthlyRemaining).toLocaleString()}`;
                    badge.style.background = 'rgba(239,68,68,0.12)';
                    badge.style.color = '#ef4444';
                    badge.style.borderColor = 'rgba(239,68,68,0.45)';
                }
            }

            if(pct > 1) {
                if(pct > 1 && pct <= 80) elTips.textContent = `✅ You are spending ${pct.toFixed(1)}% of your income. Good balance!`;
                else if(pct <= 100) elTips.textContent = `You are spending ${pct.toFixed(1)}% of your income. Consider trimming non-essentials.`;
                else elTips.textContent = `⚠️ You are overspending by ${(pct - 100).toFixed(1)}% of your income!`;
            } else {
                elTips.textContent = expenseLensDefaultTip;
            }

            if (shouldSortRows) {
                sortExpenseRowsByAllocatedPercent();
                categoriesData.sort((a, b) => {
                    if (a._isPinned !== b._isPinned) return a._isPinned ? -1 : 1;
                    if (a._isPinned && b._isPinned) return a._sortOrder - b._sortOrder;
                    if (b._sortValue !== a._sortValue) return b._sortValue - a._sortValue;
                    return a._sortOrder - b._sortOrder;
                });
            }
            categoriesData.forEach(category => {
                delete category._isPinned;
                delete category._sortValue;
                delete category._sortOrder;
            });

            saveExpenseLensState({ monthlyExpenseTotal: monthlyTotalSpent, monthlyRemaining });
            window.dispatchEvent(new CustomEvent(expenseLensUpdatedEvent, {
                detail: {
                    income,
                    monthlyExpenseTotal: monthlyTotalSpent,
                    monthlyRemaining,
                    expenses: categoriesData
                }
            }));
        };

        // -----------------------------
        // Event Listeners
        // -----------------------------
        elIncome.addEventListener("input", refreshExpenseLens);
        elIncome.addEventListener("blur", () => {
            elIncome.value = formatNumber(elIncome.value);
            refreshExpenseLens({ sortRows: true });
        });

        addBtn.addEventListener("click", () => {
            createCategoryRow(++categoryCount);
            refreshExpenseLensViews();
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

            // Anchor to the Sunday on or before the 1st — every week is exactly 7 days
            const firstOfMonth = new Date(ctx.year, ctx.month, 1);
            const startOffset = (firstOfMonth.getDay() - EL_WEEK_START_DAY + 7) % 7;
            const cursor = new Date(ctx.year, ctx.month, 1 - startOffset);
            const lastOfMonth = new Date(ctx.year, ctx.month, ctx.days);

            let weekNumber = 1;
            while (cursor <= lastOfMonth) {
                const weekStart = new Date(cursor);
                const weekEnd = new Date(cursor);
                weekEnd.setDate(weekEnd.getDate() + 6);
                weekEnd.setHours(23, 59, 59, 999);

                const isCurrent = ctx.now >= weekStart && ctx.now <= weekEnd;

                const fmt = (d) => d.toLocaleString('default', { month: 'short', day: 'numeric' });
                const rangeLabel = weekStart.getMonth() === weekEnd.getMonth()
                    ? `${weekStart.toLocaleString('default', { month: 'short' })} ${weekStart.getDate()}–${weekEnd.getDate()}`
                    : `${fmt(weekStart)} – ${fmt(weekEnd)}`;

                weeks.push({
                    id: `${weekStart.getFullYear()}-${String(weekStart.getMonth() + 1).padStart(2, '0')}-${String(weekStart.getDate()).padStart(2, '0')}`,
                    label: `Week ${weekNumber}`,
                    startDate: new Date(weekStart),
                    endDate: new Date(weekEnd),
                    year: ctx.year,
                    month: ctx.month,
                    rangeLabel,
                    isCurrent
                });

                cursor.setDate(cursor.getDate() + 7);
                weekNumber++;
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
            const occurrences = []; // Array of Date objects

            const rangeStart = week ? week.startDate : new Date(y, m, 1);
            const rangeEnd   = week ? week.endDate   : new Date(y, m, days);

            if (frequency === 'monthly') {
                const dayNum = dueDate.getDate();

                // Current month occurrence
                const d = new Date(y, m, Math.min(dayNum, days));
                if (d >= rangeStart && d <= rangeEnd) occurrences.push(d);

                // When a week filter crosses a month boundary, also check adjacent months
                // so bills due on e.g. May 1 appear in the Apr 26–May 2 week
                if (week) {
                    const wStartMonth = rangeStart.getFullYear() * 12 + rangeStart.getMonth();
                    const wEndMonth   = rangeEnd.getFullYear()   * 12 + rangeEnd.getMonth();
                    const curMonth    = y * 12 + m;

                    if (wStartMonth < curMonth) {
                        // Week starts in previous month
                        const prevDays = new Date(y, m, 0).getDate();
                        const dp = new Date(y, m - 1, Math.min(dayNum, prevDays));
                        if (dp >= rangeStart && dp <= rangeEnd) occurrences.push(dp);
                    }
                    if (wEndMonth > curMonth) {
                        // Week ends in next month
                        const nextDays = new Date(y, m + 2, 0).getDate();
                        const dn = new Date(y, m + 1, Math.min(dayNum, nextDays));
                        if (dn >= rangeStart && dn <= rangeEnd) occurrences.push(dn);
                    }
                }

                return occurrences;
            }

            if (frequency === 'weekly') {
                const targetWeekday = dueDate.getDay();
                const cursor = new Date(rangeStart);
                const daysUntil = (targetWeekday - cursor.getDay() + 7) % 7;
                cursor.setDate(cursor.getDate() + daysUntil);
                while (cursor <= rangeEnd) {
                    occurrences.push(new Date(cursor));
                    cursor.setDate(cursor.getDate() + 7);
                }
                return occurrences;
            }

            // Bi-weekly: jump directly to the first occurrence >= rangeStart
            const msPerDay = 86400000;
            const diffToStart = Math.round((rangeStart - dueDate) / msPerDay);
            const mod = ((diffToStart % 14) + 14) % 14;
            const cursor = new Date(rangeStart);
            cursor.setDate(cursor.getDate() + (mod === 0 ? 0 : 14 - mod));
            while (cursor <= rangeEnd) {
                occurrences.push(new Date(cursor));
                cursor.setDate(cursor.getDate() + 14);
            }
            return occurrences;
        };

        const elApplyWeekFilter = (week, options = {}) => {
            const shouldSortRows = options.sortRows !== false;
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
            refreshExpenseLens({ sortRows: shouldSortRows });
            renderWeekPanel();
        };

        const weekPanel = document.createElement('div');
        weekPanel.className = 'expense-lens-week-panel';
        weekPanel.style.cssText = 'display:none;position:fixed;top:50%;left:50%;transform:translate(-50%,-50%);z-index:9999;background:#0b1529;border:1.5px solid #38BDF8;border-radius:14px;padding:16px 18px;width:520px;max-width:calc(100vw - 48px);max-height:min(560px, calc(100vh - 40px));overflow-y:auto;overflow-x:hidden;box-shadow:0 24px 64px rgba(30,58,138,0.48);box-sizing:border-box;';
        document.body.appendChild(weekPanel);

        const positionWeekPanel = () => {
            const horizontalPad = window.innerWidth < 560 ? 24 : 48;
            const panelWidth = Math.max(300, Math.min(520, window.innerWidth - horizontalPad));
            weekPanel.style.setProperty('position', 'fixed', 'important');
            weekPanel.style.setProperty('top', '50%', 'important');
            weekPanel.style.setProperty('left', '50%', 'important');
            weekPanel.style.setProperty('right', 'auto', 'important');
            weekPanel.style.setProperty('bottom', 'auto', 'important');
            weekPanel.style.setProperty('transform', 'translate(-50%,-50%)', 'important');
            weekPanel.style.setProperty('width', `${panelWidth}px`, 'important');
            weekPanel.style.setProperty('min-width', '0', 'important');
            weekPanel.style.setProperty('max-width', `${panelWidth}px`, 'important');
            weekPanel.style.setProperty('max-height', 'min(560px, calc(100vh - 40px))', 'important');
            weekPanel.style.setProperty('box-sizing', 'border-box', 'important');
        };

        const hideOtherWeekPanels = () => {
            document.querySelectorAll('.expense-lens-week-panel').forEach(panel => {
                if (panel !== weekPanel) panel.style.display = 'none';
            });
        };

        const renderWeekPanel = () => {
            const { monthYearLabel } = elMonthContext();
            const weeks = elBuildCalendarWeeks();
            weekPanel.innerHTML = '';

            // Header with close button
            const header = document.createElement('div');
            header.style.cssText = 'display:flex;justify-content:space-between;align-items:center;margin-bottom:10px;padding-bottom:8px;border-bottom:1px solid rgba(56,189,248,0.25);';
            const titleWrap = document.createElement('div');
            titleWrap.style.cssText = 'display:flex;flex-direction:column;gap:2px;';
            const title = document.createElement('span');
            title.style.cssText = 'color:#38BDF8;font-weight:800;font-size:0.86rem;letter-spacing:0.05em;';
            title.textContent = 'WEEKLY BILL TRACKER';
            const subtitle = document.createElement('span');
            subtitle.style.cssText = 'color:#94A3B8;font-size:0.68rem;font-weight:700;';
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
            allRow.style.cssText = `cursor:pointer;padding:8px 10px;border-radius:8px;font-weight:700;font-size:0.79rem;margin-bottom:8px;display:flex;justify-content:space-between;align-items:center;gap:10px;${!elActiveWeek ? 'background:#38BDF8;color:#0b1529;' : 'color:#38BDF8;'}`;

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
                    occurrences.forEach(date => {
                        const amt = +(amtEl?.value || '').replace(/,/g, '') || 0;
                        if (amt <= 0) return;
                        weekTotal += amt;
                        bills.push({
                            name: nameEl?.value?.trim() || '(Unnamed)',
                            amount: amt,
                            date,
                            day: date.getDate(),
                            frequency
                        });
                    });
                });
                bills.sort((a, b) => a.date - b.date);
                const billCount = bills.length;
                const isActive   = elSameCalendarWeek(elActiveWeek, week);
                const isExpanded = elSameCalendarWeek(elExpandedWeek, week);

                const weekBlock = document.createElement('div');
                weekBlock.style.cssText = 'border-radius:10px;margin-bottom:6px;overflow:hidden;border:1px solid rgba(56,189,248,0.1);';

                // Summary row
                const summaryRow = document.createElement('div');
                summaryRow.style.cssText = `display:flex;justify-content:space-between;align-items:center;padding:8px 10px;cursor:pointer;gap:10px;${isActive ? 'background:#1E3A8A;' : 'background:rgba(255,255,255,0.04);'}`;

                const wLabel = document.createElement('span');
                wLabel.style.cssText = `font-weight:700;font-size:0.78rem;color:${isActive ? '#fff' : '#E0F2FE'};flex:1;min-width:0;`;
                wLabel.textContent = `${week.label}  (${week.rangeLabel})`;

                const rightGroup = document.createElement('div');
                rightGroup.style.cssText = 'display:flex;align-items:center;gap:8px;flex-shrink:0;';

                const amtSpan = document.createElement('span');
                amtSpan.style.cssText = `font-weight:800;font-size:0.78rem;color:${billCount > 0 ? '#38BDF8' : '#64748B'};white-space:nowrap;`;
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
                    colHeader.style.cssText = 'display:flex;padding:5px 10px 4px 12px;border-bottom:1px solid rgba(56,189,248,0.12);';
                    colHeader.innerHTML = '<span style="flex:1;font-size:0.7rem;color:#475569;font-weight:700;text-transform:uppercase;letter-spacing:0.06em;">Bill</span><span style="min-width:60px;text-align:center;font-size:0.7rem;color:#475569;font-weight:700;text-transform:uppercase;letter-spacing:0.06em;">Due</span><span style="min-width:80px;text-align:right;font-size:0.7rem;color:#475569;font-weight:700;text-transform:uppercase;letter-spacing:0.06em;">Amount</span>';
                    detailWrap.appendChild(colHeader);

                    bills.forEach((bill, i) => {
                        const billRow = document.createElement('div');
                        billRow.style.cssText = `display:flex;align-items:center;padding:7px 10px 7px 12px;${i < bills.length - 1 ? 'border-bottom:1px solid rgba(56,189,248,0.07);' : ''}`;

                        const bName = document.createElement('span');
                        bName.style.cssText = 'flex:1;font-size:0.76rem;color:#CBD5E1;font-weight:600;min-width:0;';
                        bName.textContent = bill.frequency === 'monthly' ? bill.name : `${bill.name} (${elFrequencyLabel(bill.frequency)})`;

                        const bDue = document.createElement('span');
                        bDue.style.cssText = 'min-width:52px;text-align:center;font-size:0.74rem;color:#94A3B8;font-weight:500;';
                        bDue.textContent = bill.date.toLocaleString('default', { month: 'short', day: 'numeric' });

                        const bAmt = document.createElement('span');
                        bAmt.style.cssText = 'min-width:72px;text-align:right;font-size:0.76rem;color:#38BDF8;font-weight:700;';
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

        // Weekly button — sits in the category action row
        const weeklyBtn = document.createElement('button');
        weeklyBtn.type = 'button';
        weeklyBtn.textContent = 'Weekly ▾';
        weeklyBtn.className = 'btn';
        weeklyBtn.style.cssText = 'background:#1E3A8A;color:#fff;font-weight:700;border:none;white-space:nowrap;flex-shrink:0;padding:0 16px;height:38px;line-height:1;border-radius:6px;font-size:0.875rem;';
        weeklyBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            const isOpen = weekPanel.style.display !== 'none';
            if (isOpen) { weekPanel.style.display = 'none'; return; }
            renderWeekPanel();
            positionWeekPanel(weeklyBtn);
            hideOtherWeekPanels();
            weekPanel.style.display = 'block';
        });
        document.addEventListener('click', () => { weekPanel.style.display = 'none'; });
        weekPanel.addEventListener('click', e => e.stopPropagation());
        (elActionMeta || addBtn.parentElement).appendChild(weeklyBtn);

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
            positionWeekPanel(weeklyBtnTop);
            hideOtherWeekPanels();
            weekPanel.style.display = 'block';
        });
        // Wrap the income input row in a flex container so the button sits cleanly to the right.
        // Remove mb-3 from the input (it adds margin-bottom inside the wrapper causing height mismatch).
        elIncome.classList.remove('mb-3');
        const incomeInputRow = elIncome.parentElement;
        const incomeFlexWrap = document.createElement('div');
        incomeFlexWrap.style.cssText = 'display:flex;align-items:center;gap:10px;margin-bottom:15px;';
        incomeInputRow.style.cssText = 'position:relative;margin-bottom:0;width:240px;max-width:240px;flex:0 0 240px;';
        incomeInputRow.parentElement.insertBefore(incomeFlexWrap, incomeInputRow);
        incomeFlexWrap.appendChild(incomeInputRow);

        // Remaining balance badge — live read of monthly income minus all monthly bills
        const elRemainingBadge = document.createElement('div');
        elRemainingBadge.id = elId('RemainingBadge');
        elRemainingBadge.style.cssText = [
            'display:flex;align-items:center;height:38px;padding:0 16px;',
            'border-radius:6px;border:2px solid rgba(100,116,139,0.35);',
            'background:rgba(255,255,255,0.04);',
            'font-weight:800;font-size:0.875rem;white-space:nowrap;',
            'color:#64748B;letter-spacing:0.01em;',
            'transition:background .2s,color .2s,border-color .2s;'
        ].join('');
        elRemainingBadge.textContent = 'Remaining: $0';
        incomeFlexWrap.appendChild(elRemainingBadge);
        incomeFlexWrap.appendChild(weeklyBtnTop);

        // ── Split income row (personal lens only) ────────────────────────────
        let elPrimaryIncome = null;
        let elSpouseIncome = null;

        if (!isBusinessExpenseLens) {
            const splitRow = document.createElement('div');
            splitRow.id = elId('SplitIncomeRow');
            splitRow.style.cssText = 'display:flex;align-items:flex-end;gap:12px;margin-bottom:10px;flex-wrap:wrap;';

            const makeSplitField = (inputId, labelText) => {
                const wrap = document.createElement('div');
                wrap.style.cssText = 'display:flex;flex-direction:column;gap:3px;min-width:160px;';
                const lbl = document.createElement('label');
                lbl.htmlFor = inputId;
                lbl.style.cssText = 'font-size:0.72rem;font-weight:800;color:#c79931;letter-spacing:0.04em;text-transform:uppercase;';
                lbl.textContent = labelText;
                const inputWrap = document.createElement('div');
                inputWrap.style.cssText = 'position:relative;';
                const inp = document.createElement('input');
                inp.type = 'text';
                inp.id = inputId;
                inp.placeholder = '0';
                inp.style.cssText = 'border:1px solid #d6c48a;border-radius:6px;padding:5px 28px 5px 8px;font-weight:700;font-size:0.875rem;color:#1E3A8A;width:100%;height:36px;box-sizing:border-box;';
                const dollar = document.createElement('span');
                dollar.textContent = '$';
                dollar.style.cssText = 'position:absolute;right:8px;top:50%;transform:translateY(-50%);font-weight:700;color:#1E3A8A;pointer-events:none;';
                const pct = document.createElement('span');
                pct.id = inputId + 'Pct';
                pct.style.cssText = 'font-size:0.72rem;font-weight:800;color:#64748B;margin-top:2px;display:block;';
                pct.textContent = '0%';
                inputWrap.appendChild(inp);
                inputWrap.appendChild(dollar);
                wrap.appendChild(lbl);
                wrap.appendChild(inputWrap);
                wrap.appendChild(pct);
                return { wrap, inp, pct };
            };

            const makePossessive = (name) => name ? name + (name.endsWith('s') ? "' Income" : "'s Income") : 'Client Income';
            const primaryLabel = makePossessive(clientFirstName);
            const { wrap: primaryWrap, inp: priInp, pct: priPct } = makeSplitField(elId('PrimaryIncome'), primaryLabel);
            elPrimaryIncome = priInp;
            splitRow.appendChild(primaryWrap);

            let spoInp = null;
            let spoPct = null;
            if (hasSpouse) {
                const spouseLabel = makePossessive(spouseFirstName || 'Spouse');
                const { wrap: spouseWrap, inp, pct } = makeSplitField(elId('SpouseIncome'), spouseLabel);
                elSpouseIncome = inp;
                spoInp = inp;
                spoPct = pct;
                splitRow.appendChild(spouseWrap);
            }

            const updateSplitIncome = () => {
                const pri = parseFloat((priInp.value || '').replace(/,/g, '')) || 0;
                const spo = spoInp ? (parseFloat((spoInp.value || '').replace(/,/g, '')) || 0) : 0;
                const total = pri + spo;
                elIncome.value = total > 0 ? total.toLocaleString() : '';
                if (total > 0) {
                    priPct.textContent = ((pri / total) * 100).toFixed(1) + '%';
                    if (spoPct) spoPct.textContent = ((spo / total) * 100).toFixed(1) + '%';
                } else {
                    priPct.textContent = '0%';
                    if (spoPct) spoPct.textContent = '0%';
                }
                refreshExpenseLens();
            };

            priInp.addEventListener('input', updateSplitIncome);
            priInp.addEventListener('blur', () => {
                const v = parseFloat((priInp.value || '').replace(/,/g, '')) || 0;
                priInp.value = v > 0 ? v.toLocaleString() : '';
                updateSplitIncome();
            });
            if (spoInp) {
                spoInp.addEventListener('input', updateSplitIncome);
                spoInp.addEventListener('blur', () => {
                    const v = parseFloat((spoInp.value || '').replace(/,/g, '')) || 0;
                    spoInp.value = v > 0 ? v.toLocaleString() : '';
                    updateSplitIncome();
                });
            }

            incomeFlexWrap.parentElement.insertBefore(splitRow, incomeFlexWrap.nextSibling);

            // Total Income is now computed from split fields — lock it
            elIncome.readOnly = true;
            const incomeMoneyWrap = elIncome.closest('.legend-money-input');
            if (incomeMoneyWrap) {
                incomeMoneyWrap.style.background = 'rgba(255,255,255,.82)';
                incomeMoneyWrap.style.borderColor = 'rgba(166,128,35,.45)';
            }
            elIncome.style.background = 'transparent';
            elIncome.style.cursor = 'default';
            elIncome.style.color = '#64748B';
        }

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

            // Remaining Balance (based on current computed values)
            if (elActiveWeek) {
                elMargin.style.background = 'rgba(239,68,68,0.12)';
                elMargin.style.color = '#ef4444';
                elMargin.style.borderColor = 'rgba(239,68,68,0.45)';
            } else {
                const income = +elIncome.value.replace(/,/g, '') || 0;
                let totalSpent = 0;
                categoriesContainer.querySelectorAll(`[id^="${elId('CatAmount')}"]`).forEach(input => {
                    const idx = input.id.replace(elId('CatAmount'), '');
                    const occurrenceCount = elGetBillOccurrenceDays(idx).length;
                    totalSpent += (+input.value.replace(/,/g, '') || 0) * occurrenceCount;
                });
                const remaining = income - totalSpent;
                if (remaining >= 0) {
                    elMargin.style.background = 'rgba(34,197,94,0.12)';
                    elMargin.style.color = '#22c55e';
                    elMargin.style.borderColor = 'rgba(34,197,94,0.45)';
                } else {
                    elMargin.style.background = 'rgba(239,68,68,0.12)';
                    elMargin.style.color = '#ef4444';
                    elMargin.style.borderColor = 'rgba(239,68,68,0.45)';
                }
            }
        };

        // ✅ Force style application after DOM paint (this is what kills the “refresh page” issue)
        requestAnimationFrame(() => {
            applyExpenseLensColors();
            refreshExpenseLens({ sortRows: true });            // ensures Remaining Balance + tip text is current
            applyExpenseLensColors();        // re-apply after refresh updates DOM text
        });
        };

        if (isBusinessClient && t.id === "ExpenseLens") {
            const popoutBody = createDualToolPopout(
                "Expenses",
                "Personal and business expense forms side by side, outside the normal tool container."
            );
            popoutBody.innerHTML = `
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
                    <input id="assets" type="text" class="form-control" readonly placeholder="Sync from Financial Health Snapshot…"
                           style="border:2px solid rgba(166,128,35,0.45); background:rgba(166,128,35,0.06); font-weight:800; font-size:1.1rem; color:#d4a820; padding-right:30px; cursor:default;" />
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
                    <input id="liabs" type="text" class="form-control" readonly placeholder="Sync from Financial Health Snapshot…"
                           style="border:2px solid rgba(166,128,35,0.45); background:rgba(166,128,35,0.06); font-weight:800; font-size:1.1rem; color:#d4a820; padding-right:30px; cursor:default;" />
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
             style="margin-top:18px;padding:12px 16px;border-radius:6px;border:2px solid rgba(166,128,35,0.45);background:rgba(166,128,35,0.10);font-weight:700;font-size:0.875rem;color:#d4a820;letter-spacing:0.01em;font-style:italic;transition:background .2s,color .2s,border-color .2s;">
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
        aVal.textContent = lVal.textContent = nVal.textContent = '$0';
        nwRatio.textContent = liabRatio.textContent = '0%';
        wealthStatus.textContent = '—';
        nwTips.textContent = 'Enter your assets and liabilities to get personalized insights.';
        clearToolState('NetWorth');
        hideTip();
        applyLLBSToNetWorth();
    });

    function formatDollar(val) {
        return `$${val.toLocaleString()}`;
    }

    // ✅ Color engine (paint-safe, no refresh required)
    const applyNetWorthColors = (a, l, net, ratio, liabR) => {
        // Outputs
        markIncome(aVal);
        markExpense(lVal);

        if (net > 0) markIncome(nVal);
        else if (net < 0) markExpense(nVal);
        else markGold(nVal);

        // Ratios
        if (ratio > 0) markIncome(nwRatio);
        else if (ratio < 0) markExpense(nwRatio);
        else markGold(nwRatio);

        if (liabR <= 30) markIncome(liabRatio);
        else if (liabR >= 50) markExpense(liabRatio);
        else markGold(liabRatio);

        markGold(wealthStatus);
        markGold(nwTips);
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

    const applyLLBSToNetWorth = async (event) => {
        const src = event?.detail || (await loadPersistedState('LegendLivingBalanceSheet'))?.summary || {};
        const llbsAssets = +(String(src.assetsTotal ?? 0).replace(/[,$\s]/g, '')) || 0;
        const llbsLiabs = +(String(src.liabilitiesTotal ?? 0).replace(/[,$\s]/g, '')) || 0;
        assets.value = llbsAssets > 0 ? llbsAssets.toLocaleString() : '';
        liabs.value = llbsLiabs > 0 ? llbsLiabs.toLocaleString() : '';
        calc();
    };

    await applyLLBSToNetWorth();
    toolContext.onWindow('LegendLivingBalanceSheet:updated', applyLLBSToNetWorth);
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
                    <input id="cfIncome" type="text" class="form-control" readonly
                           placeholder="Sync from Expense Lens…"
                           style="border:2px solid rgba(166,128,35,0.45); background:rgba(166,128,35,0.06);
                                  font-weight:800; font-size:1.1rem; color:#d4a820; padding-right:30px; cursor:default;" />
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
                    <input id="cfBills" type="text" class="form-control" readonly
                           placeholder="Sync from Expense Lens…"
                           style="border:2px solid rgba(166,128,35,0.45); background:rgba(166,128,35,0.06);
                                  font-weight:800; font-size:1.1rem; color:#d4a820; padding-right:30px; cursor:default;" />
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
             style="margin-top:18px;padding:12px 16px;border-radius:6px;border:2px solid rgba(166,128,35,0.45);background:rgba(166,128,35,0.10);font-weight:700;font-size:0.875rem;color:#d4a820;letter-spacing:0.01em;font-style:italic;transition:background .2s,color .2s,border-color .2s;">
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
        cfResult.textContent = '$0';
        cfSavingsPotential.textContent = '$0';
        cfInvestPct.textContent = '0%';
        cfTips.textContent = 'Enter your monthly income and bills to get personalized tips.';
        clearToolState('CashFlow');
        hideTip();
        applyExpenseLensToCashFlow();
    });

    function formatDollar(val) {
        return `$${val.toLocaleString()}`;
    }

    // ✅ Color engine (paint-safe, no refresh required)
    const applyCashFlowColors = (income, bills, net, savingsPotential, investPct) => {
        // Net cash flow
        if (net > 0) markIncome(cfResult);
        else if (net < 0) markExpense(cfResult);
        else markGold(cfResult);

        // Savings potential
        if (savingsPotential > 0) markIncome(cfSavingsPotential);
        else if (savingsPotential < 0) markExpense(cfSavingsPotential);
        else markGold(cfSavingsPotential);

        // Suggested allocation %
        if (net > 0) markIncome(cfInvestPct);
        else if (net < 0) markExpense(cfInvestPct);
        else markGold(cfInvestPct);

        markGold(cfTips);
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

    const applyExpenseLensToCashFlow = async (event) => {
        const state = event?.detail || await loadPersistedState('ExpenseLens');
        const elIncome = getExpenseLensIncomeTotal(state);
        const elExpenses = calculateExpenseLensMonthlyTotal(state);
        cfIncome.value = elIncome > 0 ? elIncome.toLocaleString() : '';
        cfBills.value = elExpenses > 0 ? elExpenses.toLocaleString() : '';
        calcCashFlow();
    };

    await applyExpenseLensToCashFlow();
    toolContext.onWindow('ExpenseLens:updated', applyExpenseLensToCashFlow);
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
                    <input id="dcDebt" type="text" class="form-control" readonly
                           placeholder="Sync from Financial Health Snapshot…"
                           style="border:2px solid rgba(166,128,35,0.45); background:rgba(166,128,35,0.06); font-weight:800; font-size:1.1rem; color:#d4a820; padding-right:30px; cursor:default;" />
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
                    <input id="dcIncome" type="text" class="form-control" readonly
                           placeholder="Sync from Expense Lens…"
                           style="border:2px solid rgba(166,128,35,0.45); background:rgba(166,128,35,0.06); font-weight:800; font-size:1.1rem; color:#d4a820; padding-right:30px; cursor:default;" />
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
                <td id="dcTips" style="margin-top:18px;padding:12px 16px;border-radius:6px;border:2px solid rgba(166,128,35,0.45);background:rgba(166,128,35,0.10);font-weight:700;font-size:0.875rem;color:#d4a820;letter-spacing:0.01em;font-style:italic;transition:background .2s,color .2s,border-color .2s;">Enter your liabilities and income to receive guidance.</td>
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
        // DTI output coloring
        if (dtiNum <= 30) markIncome(dcResult);
        else if (dtiNum >= 50) markExpense(dcResult);
        else markGold(dcResult);

        if (dtiNum <= 30) markIncome(dcStatus);
        else if (dtiNum >= 50) markExpense(dcStatus);
        else markGold(dcStatus);

        markGold(dcTips);
    };

    addClearButton(container, () => {
        dcResult.textContent = '0%';
        dcStatus.textContent = '—';
        dcTips.textContent = 'Enter your liabilities and income to receive guidance.';
        clearToolState('DebtClarity');
        hideTip();
        applyLLBSToDebtClarity();
        applyExpenseLensToDebtClarity();
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

    dcDebt.oninput = calcDebtClarity;

    const applyLLBSToDebtClarity = async (event) => {
        const src = event?.detail || (await loadPersistedState('LegendLivingBalanceSheet'))?.summary || {};
        const llbsLiabs = +(String(src.liabilitiesTotal ?? 0).replace(/[,$\s]/g, '')) || 0;
        dcDebt.value = llbsLiabs > 0 ? llbsLiabs.toLocaleString() : '';
        calcDebtClarity();
    };

    const applyExpenseLensToDebtClarity = async (event) => {
        const state = event?.detail || await loadPersistedState('ExpenseLens');
        const elIncome = getExpenseLensIncomeTotal(state);
        dcIncome.value = elIncome > 0 ? (elIncome * 12).toLocaleString() : '';
        calcDebtClarity();
    };

    await applyLLBSToDebtClarity();
    await applyExpenseLensToDebtClarity();
    toolContext.onWindow('LegendLivingBalanceSheet:updated', applyLLBSToDebtClarity);
    toolContext.onWindow('ExpenseLens:updated', applyExpenseLensToDebtClarity);
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
            <input id="fbBills" type="text" class="form-control mb-3" readonly placeholder="Sync from Expense Lens…"
                   style="border:2px solid rgba(166,128,35,0.45); background:rgba(166,128,35,0.06); font-weight:800; color:#d4a820; padding-right:30px; cursor:default;" />
            <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#1E3A8A;">$</span>
        </div>

        <div class="mb-3">
            <h5 style="margin-bottom:6px;">1 Month Goal: <span id="fb1">$0</span></h5>
            <h5 style="margin-bottom:6px;">3–6 Month Goal: <span id="fb3">$0</span></h5>
            <h5 style="margin-bottom:6px;">12 Month Goal: <span id="fb12">$0</span></h5>
        </div>

        <div id="fbTips"
             style="margin-top:18px;padding:12px 16px;border-radius:6px;border:2px solid rgba(166,128,35,0.45);background:rgba(166,128,35,0.10);font-weight:700;font-size:0.875rem;color:#d4a820;letter-spacing:0.01em;font-style:italic;transition:background .2s,color .2s,border-color .2s;">
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
        // Outputs: goals are targets
        markGold(fb1);
        markGold(fb3);
        markGold(fb12);

        markGold(fbTips);
    };

    addClearButton(container, () => {
        fb1.textContent = '$0';
        fb3.textContent = '$0';
        fb12.textContent = '$0';
        fbTips.textContent = 'Tip: Save consistently each month to build your buffer. Consider automating transfers to a separate emergency account.';
        clearToolState('FinancialBuffer');
        hideTip();
        applyExpenseLensToFinancialBuffer();
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

    const applyExpenseLensToFinancialBuffer = async (event) => {
        const state = event?.detail || await loadPersistedState('ExpenseLens');
        const elExpenses = calculateExpenseLensMonthlyTotal(state);
        fbBillsInput.value = elExpenses > 0 ? elExpenses.toLocaleString() : '';
        updateBuffer();
    };

    await applyExpenseLensToFinancialBuffer();
    toolContext.onWindow('ExpenseLens:updated', applyExpenseLensToFinancialBuffer);
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
                  data-tip="<b>Auto-synced:</b> Pulls your live net worth from Financial Health Snapshot.">i</span>
        </div>
        <input id="wpNet" type="text" class="form-control mb-2" placeholder="Syncs from Financial Health Snapshot..."
               readonly aria-readonly="true"
               style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#1E3A8A; background:rgba(255,255,255,.94); cursor:default;" />

        <div class="wp-label">
            Monthly Surplus
            <span class="wp-i" tabindex="0"
                  data-tip="<b>Auto-synced:</b> Pulls the Remaining Balance from the top of Expense Lens.">i</span>
        </div>
        <input id="wpSurplus" type="text" class="form-control mb-2" placeholder="Syncs from Expense Lens Remaining Balance..."
               readonly aria-readonly="true"
               style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#1E3A8A; background:rgba(255,255,255,.94); cursor:default;" />

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
             style="margin-top:18px;padding:12px 16px;border-radius:6px;border:2px solid rgba(166,128,35,0.45);background:rgba(166,128,35,0.10);font-weight:700;font-size:0.875rem;color:#d4a820;letter-spacing:0.01em;font-style:italic;transition:background .2s,color .2s,border-color .2s;">
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
    const hasLinkedMoneyValue = (value) => value !== undefined && value !== null && String(value).trim() !== '';
    let hasSyncedNetWorth = false;
    let hasSyncedSurplus = false;

    // --- PERSISTENCE ---
    const loadWP = async () => {
        const state = await loadPersistedState('WealthProjection');
        if(state.wpMonths) wpMonths.value = state.wpMonths;
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

        markGold(wpTips);
    };

    const updateWealthProjection = ({ skipSave = false } = {}) => {
        let net = parseNumber(wpNet.value);
        let surplus = parseNumber(wpSurplus.value);
        let months = +wpMonths.value || 0;

        wpOut.textContent = `$${formatWithCommas(net + surplus * months)}`;
        wp6.textContent = `$${formatWithCommas(net + surplus * 6)}`;
        wp12.textContent = `$${formatWithCommas(net + surplus * 12)}`;

        if (!hasSyncedNetWorth && !hasSyncedSurplus) wpTips.textContent = '⚠️ Complete Financial Health Snapshot and Expense Lens to sync your projection inputs.';
        else if (!hasSyncedNetWorth) wpTips.textContent = '⚠️ Complete Financial Health Snapshot to sync your current net worth here.';
        else if (!hasSyncedSurplus) wpTips.textContent = '⚠️ Complete Expense Lens to sync your monthly surplus here.';
        else if (net <= 0 && surplus <= 0) wpTips.textContent = '⚠️ Your synced net worth and remaining balance are not positive yet; improve the source numbers to grow your projection.';
        else if (surplus <= 0) wpTips.textContent = '⚠️ Expense Lens shows no positive remaining balance; improve income or reduce bills there first.';
        else wpTips.textContent = '✅ Good! Keep building your remaining balance in Expense Lens to maximize long-term wealth growth.';

        if (!skipSave) {
            saveWP();
        }

        // ✅ apply colors immediately after compute
        applyWealthProjectionColors(net, surplus);
    };

    const applyLLBSToWealthProjection = async (event) => {
        const src = event?.detail || (await loadPersistedState('LegendLivingBalanceSheet'))?.summary || {};
        const rawNetWorth = src?.netWorth;
        const netWorth = +(String(rawNetWorth ?? 0).replace(/[,$\s]/g, '')) || 0;
        hasSyncedNetWorth = hasLinkedMoneyValue(rawNetWorth);
        wpNet.value = hasSyncedNetWorth ? netWorth.toLocaleString() : '';
        updateWealthProjection();
    };

    const applyExpenseLensToWealthProjection = async (event) => {
        const state = event?.detail || await loadPersistedState('ExpenseLens');
        const rawRemaining = state?.monthlyRemaining;
        const remaining = +(String(rawRemaining ?? 0).replace(/[,$\s]/g, '')) || 0;
        hasSyncedSurplus = hasLinkedMoneyValue(rawRemaining);
        wpSurplus.value = hasSyncedSurplus ? remaining.toLocaleString() : '';
        updateWealthProjection();
    };

    wpMonths.addEventListener('input', () => updateWealthProjection());
    wpMonths.addEventListener('blur', () => updateWealthProjection());

    await applyLLBSToWealthProjection();
    await applyExpenseLensToWealthProjection();
    toolContext.onWindow('LegendLivingBalanceSheet:updated', applyLLBSToWealthProjection);
    toolContext.onWindow('ExpenseLens:updated', applyExpenseLensToWealthProjection);

    // ✅ initial compute + paint (for persisted state)
    updateWealthProjection();

    addClearButton(container, () => {
        wpMonths.value = '';
        clearPersistedState('WealthProjection');
        hideTip();
        updateWealthProjection({ skipSave: true });
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
        <input id="fiNet" type="text" class="form-control mb-2" readonly placeholder="Sync from Financial Health Snapshot…"
               style="border:2px solid rgba(166,128,35,0.45); background:rgba(166,128,35,0.06); font-weight:800; color:#d4a820; cursor:default;" />

        <div class="fi-label">
            Annual Expenses
            <span class="fi-i" tabindex="0"
                  data-tip="<b>What to enter:</b> Your yearly cost of living. <b>Example:</b> 50,000 (≈ 4,167/mo)">i</span>
        </div>
        <input id="fiExp" type="text" class="form-control mb-2" readonly placeholder="Sync from Expense Lens…"
               style="border:2px solid rgba(166,128,35,0.45); background:rgba(166,128,35,0.06); font-weight:800; color:#d4a820; cursor:default;" />

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
             style="margin-top:18px;padding:12px 16px;border-radius:6px;border:2px solid rgba(166,128,35,0.45);background:rgba(166,128,35,0.10);font-weight:700;font-size:0.875rem;color:#d4a820;letter-spacing:0.01em;font-style:italic;transition:background .2s,color .2s,border-color .2s;">
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
        if (passiveNum > 0) markIncome(fiPassive);
        else if (passiveNum < 0) markExpense(fiPassive);
        else markNeutral(fiPassive);

        if (netNum > 0) markIncome(fiNetOut); else if (netNum < 0) markExpense(fiNetOut); else markGold(fiNetOut);
        markExpense(fiExpOut);

        if (passiveNum > 0) markIncome(fiPassiveOut);
        else if (passiveNum < 0) markExpense(fiPassiveOut);
        else markGold(fiPassiveOut);

        if (fiNum >= 7) markIncome(fiOut);
        else if (fiNum <= 3) markExpense(fiOut);
        else markGold(fiOut);

        if (monthsNum >= 60) markIncome(fiMonths);
        else if (monthsNum <= 12) markExpense(fiMonths);
        else markGold(fiMonths);

        markGold(fiAdvice);
    };

    addClearButton(container, () => {
        fiPassive.value = '';
        fiOut.textContent = '0';
        fiNetOut.textContent = fiExpOut.textContent = fiPassiveOut.textContent = '$0';
        fiMonths.textContent = '0';
        fiAdvice.textContent = 'Enter your values to see recommendations.';
        clearPersistedState('FreedomIndex');
        hideTip();
        applyLLBSToFreedomIndex();
        applyExpenseLensToFreedomIndex();
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

    [fiPassive].forEach(input => {
        input.addEventListener('input', updateFreedom);
        input.addEventListener('blur', () => {
            input.value = parseNumber(input.value).toLocaleString();
            updateFreedom();
        });
    });

    const applyLLBSToFreedomIndex = async (event) => {
        const src = event?.detail || (await loadPersistedState('LegendLivingBalanceSheet'))?.summary || {};
        const llbsNet = +(String(src.netWorth ?? 0).replace(/[,$\s]/g, '')) || 0;
        fiNet.value = llbsNet !== 0 ? llbsNet.toLocaleString() : '';
        updateFreedom();
    };

    const applyExpenseLensToFreedomIndex = async (event) => {
        const state = event?.detail || await loadPersistedState('ExpenseLens');
        const elExpenses = calculateExpenseLensMonthlyTotal(state);
        fiExp.value = elExpenses > 0 ? (elExpenses * 12).toLocaleString() : '';
        updateFreedom();
    };

    await applyLLBSToFreedomIndex();
    await applyExpenseLensToFreedomIndex();
    toolContext.onWindow('LegendLivingBalanceSheet:updated', applyLLBSToFreedomIndex);
    toolContext.onWindow('ExpenseLens:updated', applyExpenseLensToFreedomIndex);
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
        <input id="dapA" type="text" class="form-control mb-2" readonly placeholder="Sync from Financial Health Snapshot…"
               style="border:2px solid rgba(166,128,35,0.45); background:rgba(166,128,35,0.06); font-weight:800; color:#d4a820; cursor:default;" />

        <div class="dap-label">
            Total Liabilities
            <span class="dap-i" tabindex="0"
                  data-tip="<b>Examples:</b> 50,000 • 180,000 (credit cards, loans, mortgage balance, etc.)">i</span>
        </div>
        <input id="dapL" type="text" class="form-control mb-2" readonly placeholder="Sync from Financial Health Snapshot…"
               style="border:2px solid rgba(166,128,35,0.45); background:rgba(166,128,35,0.06); font-weight:800; color:#d4a820; cursor:default;" />

        <div class="dap-label">
            Monthly Income
            <span class="dap-i" tabindex="0"
                  data-tip="<b>Optional:</b> Monthly income helps estimate how fast you could crush liabilities. <b>Example:</b> 6,000">i</span>
        </div>
        <input id="dapIncome" type="text" class="form-control mb-3" readonly placeholder="Sync from Expense Lens…"
               style="border:2px solid rgba(166,128,35,0.45); background:rgba(166,128,35,0.06); font-weight:800; color:#d4a820; cursor:default;" />

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
             style="margin-top:18px;padding:12px 16px;border-radius:6px;border:2px solid rgba(166,128,35,0.45);background:rgba(166,128,35,0.10);font-weight:700;font-size:0.875rem;color:#d4a820;letter-spacing:0.01em;font-style:italic;transition:background .2s,color .2s,border-color .2s;">
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
        // Outputs (money)
        markIncome(dapAssets);
        markExpense(dapLiabilities);

        const netWorth = assetsNum - liabilitiesNum;
        if (netWorth > 0) markIncome(dapNetWorth);
        else if (netWorth < 0) markExpense(dapNetWorth);
        else markGold(dapNetWorth);

        if (incomeNum > 0) markIncome(dapMonthlyIncome);
        else if (incomeNum < 0) markExpense(dapMonthlyIncome);
        else markGold(dapMonthlyIncome);

        // Ratio output (assets/liabilities)
        if (ratioNum >= 2) markIncome(dapOut);
        else if (ratioNum <= 1) markExpense(dapOut);
        else markGold(dapOut);

        markGold(dapAdvice);
    };

    addClearButton(container, () => {
        dapOut.textContent = '0';
        dapAssets.textContent = dapLiabilities.textContent =
        dapNetWorth.textContent = dapMonthlyIncome.textContent = '$0';
        dapAdvice.textContent = 'Enter values to get guidance on your financial health.';
        clearPersistedState('DebtAssetPulse');
        hideTip();
        applyLLBSToDebtAssetPulse();
        applyExpenseLensToDebtAssetPulse();
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

    const applyLLBSToDebtAssetPulse = async (event) => {
        const src = event?.detail || (await loadPersistedState('LegendLivingBalanceSheet'))?.summary || {};
        const llbsAssets = +(String(src.assetsTotal ?? 0).replace(/[,$\s]/g, '')) || 0;
        const llbsLiabs = +(String(src.liabilitiesTotal ?? 0).replace(/[,$\s]/g, '')) || 0;
        dapA.value = llbsAssets > 0 ? llbsAssets.toLocaleString() : '';
        dapL.value = llbsLiabs > 0 ? llbsLiabs.toLocaleString() : '';
        updateDAP();
    };

    const applyExpenseLensToDebtAssetPulse = async (event) => {
        const state = event?.detail || await loadPersistedState('ExpenseLens');
        const elIncome = getExpenseLensIncomeTotal(state);
        dapIncome.value = elIncome > 0 ? elIncome.toLocaleString() : '';
        updateDAP();
    };

    await applyLLBSToDebtAssetPulse();
    await applyExpenseLensToDebtAssetPulse();
    toolContext.onWindow('LegendLivingBalanceSheet:updated', applyLLBSToDebtAssetPulse);
    toolContext.onWindow('ExpenseLens:updated', applyExpenseLensToDebtAssetPulse);
    } // ✅ closes if (t.id === "DebtAssetPulse")

}); // ✅ closes dropdown.addEventListener("change", ...)

    // Financial Health Snapshot is always the entry point — every load, refresh, and login.
    requestToolSelection(DEFAULT_TOOL_ID);

}); // ✅ closes document.addEventListener("DOMContentLoaded", ...)
