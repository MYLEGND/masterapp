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

    const parseSavingsMoney = (value) => +(String(value || '').replace(/[,$\s]/g, '')) || 0;
    const hasNonBlankValue = (value) => value !== undefined && value !== null && String(value).trim() !== '';

    const normalizeScheduledFrequency = (value) => {
        const normalized = (value || '').toString().toLowerCase().replace(/[^a-z]/g, '');
        if (normalized === 'weekly') return 'weekly';
        if (normalized === 'biweekly') return 'biweekly';
        return 'monthly';
    };

    const parseScheduledAnchorDate = (value) => {
        const parts = (value || '').split('-').map(part => parseInt(part, 10));
        if (parts.length < 3 || parts.some(part => !Number.isFinite(part))) return null;
        return new Date(parts[0], parts[1] - 1, parts[2]);
    };

    const getScheduledMonthContext = (options = {}) => {
        const now = options.now instanceof Date ? new Date(options.now) : new Date();
        const year = Number.isInteger(options.year) ? options.year : now.getFullYear();
        const month = Number.isInteger(options.month) ? options.month : now.getMonth();
        const days = Number.isInteger(options.days) ? options.days : new Date(year, month + 1, 0).getDate();
        return { now, year, month, days };
    };

    const getDefaultScheduledAnchorDate = (options = {}) => {
        const { year, month } = getScheduledMonthContext(options);
        return `${year}-${String(month + 1).padStart(2, '0')}-01`;
    };

    const getScheduledOccurrenceDays = (anchorValue, frequencyValue, options = {}) => {
        const anchorDate = parseScheduledAnchorDate(anchorValue);
        if (!anchorDate) return [];

        anchorDate.setHours(0, 0, 0, 0);

        const { year, month, days } = getScheduledMonthContext(options);
        const frequency = normalizeScheduledFrequency(frequencyValue);
        const week = options.week || null;
        const rangeStart = week ? new Date(week.startDate) : new Date(year, month, 1);
        const rangeEnd = week ? new Date(week.endDate) : new Date(year, month, days, 23, 59, 59, 999);

        rangeStart.setHours(0, 0, 0, 0);
        rangeEnd.setHours(23, 59, 59, 999);

        const occurrences = [];
        const cursor = new Date(anchorDate);

        if (frequency === 'monthly') {
            const anchorDay = anchorDate.getDate();
            const monthDays = new Date(year, month + 1, 0).getDate();
            cursor.setFullYear(year, month, Math.min(anchorDay, monthDays));

            if (cursor >= anchorDate && cursor >= rangeStart && cursor <= rangeEnd) {
                occurrences.push(new Date(cursor));
            }

            return occurrences;
        }

        const intervalDays = frequency === 'biweekly' ? 14 : 7;
        const msPerDay = 86400000;
        const daysFromAnchorToRangeStart = Math.floor(
            (Date.UTC(rangeStart.getFullYear(), rangeStart.getMonth(), rangeStart.getDate()) -
             Date.UTC(anchorDate.getFullYear(), anchorDate.getMonth(), anchorDate.getDate())) / msPerDay
        );

        if (daysFromAnchorToRangeStart > 0) {
            const intervalsToAdvance = Math.ceil(daysFromAnchorToRangeStart / intervalDays);
            cursor.setDate(cursor.getDate() + intervalsToAdvance * intervalDays);
        }

        while (cursor <= rangeEnd) {
            if (cursor >= rangeStart && cursor >= anchorDate) {
                occurrences.push(new Date(cursor));
            }
            cursor.setDate(cursor.getDate() + intervalDays);
        }

        return occurrences;
    };

    const getSavingsExpenseOccurrences = (category) => getScheduledOccurrenceDays(
        category?.due || '',
        category?.frequency || category?.recurrence,
    ).length;

    const sanitizeExpenseLensIncomeStream = (stream) => ({
        label: String(stream?.label || '').trim(),
        amount: String(stream?.amount || '').trim(),
        frequency: normalizeScheduledFrequency(stream?.frequency || stream?.recurrence),
        anchorDate: String(stream?.anchorDate || stream?.date || '').trim()
    });

    const getExpenseLensIncomeStreamGroupsFromState = (state) => {
        const rawGroups = state?.incomeStreams;
        const normalizeGroup = (value) => Array.isArray(value)
            ? value.map(sanitizeExpenseLensIncomeStream)
            : [];

        if (rawGroups && typeof rawGroups === 'object') {
            return {
                primary: normalizeGroup(rawGroups.primary),
                secondary: normalizeGroup(rawGroups.secondary)
            };
        }

        const primary = [];
        const secondary = [];
        const primaryIncome = String(state?.primaryIncome ?? '').trim();
        const spouseIncome = String(state?.spouseIncome ?? '').trim();
        const legacyIncome = String(state?.income ?? '').trim();

        if (primaryIncome) {
            primary.push({
                label: '',
                amount: primaryIncome,
                frequency: 'monthly',
                anchorDate: getDefaultScheduledAnchorDate()
            });
        } else if (legacyIncome) {
            primary.push({
                label: '',
                amount: legacyIncome,
                frequency: 'monthly',
                anchorDate: getDefaultScheduledAnchorDate()
            });
        }

        if (spouseIncome) {
            secondary.push({
                label: '',
                amount: spouseIncome,
                frequency: 'monthly',
                anchorDate: getDefaultScheduledAnchorDate()
            });
        }

        return { primary, secondary };
    };

    const summarizeExpenseLensIncomeGroups = (groups, options = {}) => {
        const groupLabelMap = options.groupLabelMap || {};
        const groupTotals = { primary: 0, secondary: 0 };
        const hits = [];

        ['primary', 'secondary'].forEach((groupKey) => {
            const streams = Array.isArray(groups?.[groupKey]) ? groups[groupKey] : [];
            const baseLabel = groupLabelMap[groupKey]
                || (groupKey === 'secondary' ? 'Partner Income' : 'Income');

            streams.forEach((stream, index) => {
                const amount = parseSavingsMoney(stream?.amount);
                if (amount <= 0) return;

                const frequency = normalizeScheduledFrequency(stream?.frequency);
                const anchorDate = String(stream?.anchorDate || '').trim() || getDefaultScheduledAnchorDate(options);
                const label = String(stream?.label || '').trim()
                    || (streams.length > 1 ? `${baseLabel} Stream ${index + 1}` : baseLabel);

                const normalizedMonthlyAmount = frequency === 'weekly'
                    ? amount * 52 / 12
                    : frequency === 'biweekly'
                        ? amount * 26 / 12
                        : amount;

                groupTotals[groupKey] += normalizedMonthlyAmount;

                getScheduledOccurrenceDays(anchorDate, frequency, options).forEach((date) => {
                    hits.push({
                        groupKey,
                        label,
                        amount,
                        date,
                        frequency,
                        anchorDate
                    });
                });
            });
        });

        hits.sort((a, b) => (a.date - b.date) || a.label.localeCompare(b.label));

        return {
            monthlyTotal: groupTotals.primary + groupTotals.secondary,
            groupTotals,
            hits,
            count: hits.length
        };
    };

    const hasExpenseLensExpenseRows = (state) => {
        const categories = Array.isArray(state?.categories) ? state.categories : [];
        if (categories.some(category => parseSavingsMoney(category?.amount || category?.occurrenceAmount) > 0)) {
            return true;
        }

        const expenses = Array.isArray(state?.expenses) ? state.expenses : [];
        return expenses.some(expense => parseSavingsMoney(expense?.occurrenceAmount || expense?.amount) > 0);
    };

    const calculateExpenseLensMonthlyTotal = (state) => {
        const categories = Array.isArray(state?.categories) ? state.categories : [];
        if (categories.length > 0) {
            return categories.reduce((sum, category) => {
                const amount = parseSavingsMoney(category?.amount || category?.occurrenceAmount);
                const occurrences = getSavingsExpenseOccurrences(category);
                return sum + (amount * occurrences);
            }, 0);
        }

        const expenses = Array.isArray(state?.expenses) ? state.expenses : [];
        if (expenses.length > 0) {
            return expenses.reduce((sum, expense) => {
                const occurrenceAmount = parseSavingsMoney(expense?.occurrenceAmount);
                const monthlyAmount = parseSavingsMoney(expense?.amount);
                const hasRecurringShape = occurrenceAmount > 0 && String(expense?.due || '').trim().length > 0;
                if (hasRecurringShape) {
                    return sum + (occurrenceAmount * getSavingsExpenseOccurrences(expense));
                }
                return sum + monthlyAmount;
            }, 0);
        }

        return parseSavingsMoney(state?.monthlyExpenseTotal);
    };

    const calculateExpenseLensMonthlyRemaining = (state) => {
        const income = getExpenseLensIncomeTotal(state);
        const monthlyExpenses = calculateExpenseLensMonthlyTotal(state);
        const hasRecomputableSources = income !== 0 || monthlyExpenses !== 0 || hasExpenseLensExpenseRows(state);
        if (hasRecomputableSources) {
            return income - monthlyExpenses;
        }

        return parseSavingsMoney(state?.monthlyRemaining);
    };

    const getExpenseLensIncomeTotal = (state) => {
        const incomeGroups = getExpenseLensIncomeStreamGroupsFromState(state);
        return summarizeExpenseLensIncomeGroups(incomeGroups).monthlyTotal;
    };

    const hasExpenseLensFinancialData = (state) =>
        getExpenseLensIncomeTotal(state) !== 0
        || calculateExpenseLensMonthlyTotal(state) !== 0
        || hasExpenseLensExpenseRows(state)
        || parseSavingsMoney(state?.monthlyRemaining) !== 0;

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
        const allowLegacyLocalFirst = !canUseServerState && rawStateFirstToolIds.has(key);

        if (allowLegacyLocalFirst) {
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

        // Server-backed finance pages use the database as the source of truth.
        // Local storage is only a cache/fallback so old blank browser state cannot
        // overwrite valid saved rows after reloads, deploys, or app restarts.
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
        btn.className = 'btn btn-outline-secondary btn-sm finance-clear-btn';
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

    function applyToolBoxStyles(container) {
        if (!container) return;
        container.classList.add('legend-finance-tool-shell');
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
    function formatDollar(value) {
        return `$${(+value || 0).toLocaleString()}`;
    }

// ------------------- Financial Meaning Colors -------------------
const FINANCE_TONE_CLASSES = ["finance-tone-income", "finance-tone-expense", "finance-tone-neutral", "finance-tone-gold"];

function paint(el, toneClass) {
  if (!el) return;
  el.classList.remove(...FINANCE_TONE_CLASSES);
  el.classList.add(toneClass);
  const affixGroups = [
    el.closest?.(".legend-money-input"),
    el.closest?.(".legend-percent-input")
  ].filter(Boolean);
  affixGroups.forEach((group) => {
    group.querySelectorAll(".legend-money-prefix, .legend-percent-suffix").forEach((node) => {
      node.classList.remove(...FINANCE_TONE_CLASSES);
      node.classList.add(toneClass);
    });
  });
}

function markIncome(el)  { paint(el, "finance-tone-income"); }
function markExpense(el) { paint(el, "finance-tone-expense"); }
function markNeutral(el) { paint(el, "finance-tone-neutral"); }
function markGold(el)    { paint(el, "finance-tone-gold"); }
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
<div class="networth-tool legend-finance-tool-card legend-finance-tool-card--fallback el-shell">
    <h3 class="lf-ui-001">Financial Health Snapshot</h3>
    <p class="lf-ui-002">This tool could not load. Please refresh and try again.</p>
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
<div class="networth-tool legend-finance-tool-card legend-finance-tool-card--wide legend-finance-tool-card--spacious el-shell">
    <div id="wbTipLayer"></div>

    <h3 class="lf-ui-003">
        ${t.name}
    </h3>
    <div class="lf-ui-004">
        <!-- Inputs Column -->
        <div class="lf-ui-005">

            <label class="wb-label">
                Annual Income
                <span class="wb-i" tabindex="0" data-tip="<b>Examples:</b> 60,000 • 85,500 • 120,000 (gross annual pay)">i</span>
            </label>
            <div class="lf-ui-006">
                <input id="wbIncome" type="text" class="form-control lf-ui-007" />
                <span class="lf-ui-008">$</span>
            </div>

            <label class="wb-label">
                Working Period (Years)
                <span class="wb-i" tabindex="0" data-tip="<b>Examples:</b> 10 • 20 • 30 (years you plan to keep earning/saving)">i</span>
            </label>
            <input id="wbYears" type="text" class="form-control lf-ui-009" />

            <label class="wb-label">
                Inflation
                <span class="wb-i" tabindex="0" data-tip="<b>Examples:</b> 2.5 • 3 • 4 (average annual inflation %)">i</span>
            </label>
            <div class="lf-ui-006">
                <input id="wbInflation" type="text" class="form-control lf-ui-007" />
                <span class="lf-ui-008">%</span>
            </div>

            <label class="wb-label">
                After-Tax Rate of Return
                <span class="wb-i" tabindex="0" data-tip="<b>Examples:</b> 5 • 7 • 9 (after-tax investment return %)">i</span>
            </label>
            <div class="lf-ui-006">
                <input id="wbReturn" type="text" class="form-control lf-ui-007" />
                <span class="lf-ui-008">%</span>
            </div>

            <label class="wb-label">
                Tax Bracket
                <span class="wb-i" tabindex="0" data-tip="<b>Examples:</b> 12 • 22 • 24 (effective/estimated rate %)">i</span>
            </label>
            <div class="lf-ui-006">
                <input id="wbTax" type="text" class="form-control lf-ui-007" />
                <span class="lf-ui-008">%</span>
            </div>

            <label class="wb-label">
                Fixed Liabilities
                <span class="wb-i" tabindex="0" data-tip="<b>Examples:</b> 10 • 18 • 25 (debt payments as % of income)">i</span>
            </label>
            <div class="lf-ui-006">
                <input id="wbLiabilities" type="text" class="form-control lf-ui-007" />
                <span class="lf-ui-008">%</span>
            </div>

            <label class="wb-label">
                Lifestyle Spending
                <span class="wb-i" tabindex="0" data-tip="<b>Examples:</b> 35 • 45 • 55 (living costs + wants as % of income)">i</span>
            </label>
            <div class="lf-ui-006">
                <input id="wbLifestyle" type="text" class="form-control lf-ui-007" />
                <span class="lf-ui-008">%</span>
            </div>

        </div>

        <!-- Outputs + Chart -->
        <div class="wf-output-col lf-ui-010">
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
                <div id="wbSavingsTips" class="wf-tip-text lf-ui-011">
                    Enter your profile above to calculate savings.
                </div>
                <!-- hidden holders to keep IDs for logic -->
                <span class="lf-ui-012" id="wbEarnings">$0</span>
                <span class="lf-ui-012" id="wbWealth">$0</span>
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
    const DEFAULT_SAVINGS_HELPER_TEXT = "Default buckets intentionally start at 60% of available savings allocation so 40% remains open for lifestyle flexibility, and every percentage can be customized.";
    const DEFAULT_SAVINGS_TEMPLATE_TOTAL_PERCENT = 60;

    const getDefaultPersonalSavingsAllocationRows = () => ([
        {
            name: "Emergency Reserve / Cash Buffer",
            percent: 18,
            aprPercent: 3,
            description: "Liquid savings for unexpected expenses, income gaps, deductibles, and short-term stability."
        },
        {
            name: "Short-Term Sinking Funds",
            percent: 9,
            aprPercent: 4,
            description: "Planned near-term needs like car repairs, travel, gifts, moving costs, deductibles, and annual expenses."
        },
        {
            name: "Mid-Term Opportunity Fund",
            percent: 12,
            aprPercent: 7,
            description: "Money set aside for goals within roughly 2–5 years such as a home fund, business launch, education, or major life moves."
        },
        {
            name: "Retirement / Long-Term Investments",
            percent: 15,
            aprPercent: 10,
            description: "Long-term wealth building for retirement accounts, brokerage investing, and diversified long-term growth."
        },
        {
            name: "Debt Paydown / Wealth Acceleration",
            percent: 6,
            aprPercent: 3,
            description: "Extra dollars toward high-interest debt, principal reduction, or intentional wealth-building acceleration."
        }
    ]);

    const getDefaultBusinessSavingsAllocationRows = () => ([
        {
            name: "Tax Reserve",
            percent: 18,
            description: "Set aside money for estimated taxes, payroll taxes, sales tax, and year-end tax obligations."
        },
        {
            name: "Operating Reserve",
            percent: 15,
            description: "Business emergency fund for slow months, delayed receivables, repairs, chargebacks, or unexpected expenses."
        },
        {
            name: "Payroll / Owner Pay Stability",
            percent: 9,
            description: "Stabilizes owner draws, contractor payments, payroll obligations, and predictable compensation."
        },
        {
            name: "Growth / Marketing Reinvestment",
            percent: 12,
            description: "Capital for lead generation, marketing, sales tools, branding, technology, and client acquisition."
        },
        {
            name: "Equipment / Systems / Future Expansion",
            percent: 6,
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
<div class="networth-tool p-4 legend-finance-tool-card legend-finance-tool-card--xl el-shell">
    <div id="${pid('TipLayer')}"></div>
    <div class="savings-accelerator-header">
        <div class="savings-accelerator-title">
            <h3>${saTitle}</h3>
            <p>${savingsSubtitle}</p>
        </div>
        <div id="${pid('ActionRow')}" class="savings-accelerator-actions" aria-label="Savings Accelerator actions"></div>
    </div>
    <div class="ft-sync-grid ft-sync-grid--single">
        <div class="ft-sync-card">
            <div class="el-label">${isBusinessSA ? "Business " : ""}Savings Allocation</div>
            <div class="legend-money-input sa-source-money">
                <span class="legend-money-prefix">$</span>
                <input id="${pid('Allocation')}" type="text" class="legend-money-field" readonly data-money-input="true"
                       placeholder="Sync from Expense Lens…"
                      />
            </div>
            <div class="ft-field-note">
                Auto-synced · ${isBusinessSA ? "Business " : ""}Expense Lens remaining balance
            </div>
        </div>
    </div>
    <div class="mt-4">
        <h5 class="mb-0">Savings Allocation Plan</h5>
        <div class="ft-field-note">
            ${DEFAULT_SAVINGS_HELPER_TEXT}
        </div>
        <div class="ft-kpi-grid ft-kpi-grid--two">
            <dl class="ft-kpi-card">
                <dt>Remaining Allocation</dt>
                <dd id="${pid('Remaining')}">$0</dd>
            </dl>
            <dl class="ft-kpi-card">
                <dt>Total Allocated</dt>
                <dd id="${pid('PctTotal')}">0%</dd>
            </dl>
        </div>
        <div class="savings-row-header${isDualPanel ? ' compact' : ''}" aria-hidden="true">
            ${isDualPanel
                ? `
                    <span>Bucket Name</span>
                    <span>Alloc %</span>
                    <span>Alloc $</span>
                    <span>APR %</span>
                    <span class="savings-row-header__multiline savings-row-header__projection">Projected<br>Year-End</span>
                    <span class="savings-row-header__action">Details</span>
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
    </div>
    <div class="el-tip-strip" id="${pid('Tips')}">
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
                    <path d="M6 20h12M8 20V8.4L12 5l4 3.4V20M8 8.9h8M10 11.2h.01M14 11.2h.01M10 14.7h.01M14 14.7h.01M11 20v-3.3h2V20" stroke="currentColor" stroke-width="1.95" stroke-linecap="round" stroke-linejoin="round"/>
                </svg>`;
        }
        if (kind === 'account') {
            return `
                <svg viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
                    <path d="M3.5 9 12 5l8.5 4M5 10.6h14M6.6 18.2v-5.8M10.2 18.2v-5.8M13.8 18.2v-5.8M17.4 18.2v-5.8M4.4 20h15.2" stroke="currentColor" stroke-width="1.95" stroke-linecap="round" stroke-linejoin="round"/>
                </svg>`;
        }
        if (kind === 'expense') {
            return `
                <svg viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
                    <path d="M8 4.5h8v14.3l-1.9-1.3-2.1 1.4-2.1-1.4-2 1.4-1.4-1V6.2A1.7 1.7 0 0 1 8 4.5Z" stroke="currentColor" stroke-width="1.95" stroke-linecap="round" stroke-linejoin="round"/>
                    <path d="M12 7.1v8.2M14.2 9.1c0-.9-.9-1.6-2-1.6s-2 .7-2 1.6.9 1.6 2 1.6 2 .8 2 1.8-.9 1.6-2 1.6-2-.7-2-1.6" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/>
                </svg>`;
        }
        if (kind === 'surplus' || kind === 'bucket-growth') {
            return `
                <svg viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
                    <path d="M5.2 18v-4.9M10 18v-7.5M14.8 18v-5.5M19 18V8M5.8 10.7 10 7.4l3.8 2.5L18.9 5" stroke="currentColor" stroke-width="1.95" stroke-linecap="round" stroke-linejoin="round"/>
                    <path d="M16.2 5H19v2.8" stroke="currentColor" stroke-width="1.95" stroke-linecap="round" stroke-linejoin="round"/>
                </svg>`;
        }
        if (kind === 'bucket-protection') {
            return `
                <svg viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
                    <path d="M12 3.7 18.7 6.6v4.8c0 4.1-2.5 7.1-6.7 9.4-4.2-2.3-6.7-5.3-6.7-9.4V6.6L12 3.7Z" stroke="currentColor" stroke-width="1.95" stroke-linecap="round" stroke-linejoin="round"/>
                    <path d="M12 8.6v5.8M9.1 11.5h5.8" stroke="currentColor" stroke-width="1.95" stroke-linecap="round" stroke-linejoin="round"/>
                </svg>`;
        }
        if (kind === 'bucket-short') {
            return `
                <svg viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
                    <path d="M8.2 10.3c1-1.6 2.9-2.6 5.3-2.6 3.4 0 6.2 2 6.2 4.8 0 1-.4 2-1.1 2.7l.9 1.4h-2.2l-.5 1.4h-1.6l-.4-1.1h-3.8c-3.4 0-6.2-1.9-6.2-4.5 0-1.7 1.1-3.2 2.8-4.1Z" stroke="currentColor" stroke-width="1.95" stroke-linecap="round" stroke-linejoin="round"/>
                    <path d="M9.2 18v1.3M16 18v1.3M10.1 9.2h2.5M16.5 11.3h.01" stroke="currentColor" stroke-width="1.95" stroke-linecap="round" stroke-linejoin="round"/>
                </svg>`;
        }
        if (kind === 'bucket-retirement') {
            return `
                <svg viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
                    <path d="M12 11a3.8 3.8 0 1 0 0-7.6 3.8 3.8 0 0 0 0 7.6Z" stroke="currentColor" stroke-width="1.95" stroke-linecap="round" stroke-linejoin="round"/>
                    <path d="M6.2 20v-1.1c0-2.8 2.6-5 5.8-5s5.8 2.2 5.8 5V20M8.7 20c.1-1.5 1.5-2.7 3.3-2.7 1.8 0 3.2 1.2 3.3 2.7" stroke="currentColor" stroke-width="1.95" stroke-linecap="round" stroke-linejoin="round"/>
                </svg>`;
        }
        if (kind === 'bucket-debt') {
            return `
                <svg viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
                    <path d="M5 16.3a7 7 0 1 1 14 0M6.1 16.3h11.8M12 16.3l3.7-3.8" stroke="currentColor" stroke-width="1.95" stroke-linecap="round" stroke-linejoin="round"/>
                    <path d="M9 10.1h.01M15 10.1h.01" stroke="currentColor" stroke-width="1.95" stroke-linecap="round"/>
                    <circle cx="12" cy="16.3" r="1.15" fill="currentColor"/>
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
        if (/(retire|roth|ira|401|long.?term)/.test(normalized)) return 'bucket-retirement';
        if (/(debt|loan|paydown|credit|acceler)/.test(normalized)) return 'bucket-debt';
        if (/(growth|opportun|wealth|mid|invest|brokerage|education)/.test(normalized)) return 'bucket-growth';
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
            const projectedValue = parseSavingsMoney(row.querySelector('.sa-alloc-projected-field')?.value || row.querySelector('.sa-alloc-projected-field')?.textContent || '');
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
                expensesLabel,
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
                expensesLabel,
                bucket,
                visibleBucketCount: index + 1,
                activeBucketIndex: index
            }))
        ];

        return {
            sourceLabel,
            accountLabel,
            expensesLabel,
            surplusLabel,
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
        const stepHasExpenses = ['expense', 'surplus', 'bucket'].includes(step.kind);
        const stepHasSurplus = ['surplus', 'bucket'].includes(step.kind);
        const visibleBuckets = savingsIllustrationData.rows.slice(0, step.visibleBucketCount || 0);
        const sourceParts = String(step.sourceLabel || savingsIllustrationData.sourceLabel || savingsIllustrationData.steps[0]?.sourceLabel || '')
            .split('/')
            .map((part) => part.trim())
            .filter(Boolean);
        const sourceTitle = sourceParts[0] || 'Company';
        const sourceSubtitle = sourceParts.slice(1).join(' / ') || (isBusinessSA ? 'Revenue Source' : 'Income Source');
        const accountTitle = step.accountLabel || savingsIllustrationData.accountLabel || (isBusinessSA ? 'Business Operating Account' : 'Personal Checking / Savings');
        const expenseTitle = step.expensesLabel || savingsIllustrationData.expensesLabel || (isBusinessSA ? 'Total Business Expenses' : 'Total Expenses');
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
                active: false
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
                title: accountTitle,
                value: money(savingsIllustrationData.monthlyIncome),
                tone: 'account',
                icon: 'account',
                active: step.kind === 'account'
            })
            : '';
        const expenseCard = stepHasExpenses
            ? buildSavingsIllustrationCard({
                kicker: 'Expenses',
                title: expenseTitle,
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
        const projectedBucketCount = savingsIllustrationData.rows.length;
        const projectedBucketMeta = projectedBucketCount === 1
            ? 'Across 1 allocation bucket'
            : `Across ${projectedBucketCount} allocation buckets`;

        illustrationCounter.textContent = stepCountText;
        if (illustrationSummary) illustrationSummary.innerHTML = summaryMetrics;
        if (illustrationProgress) {
            illustrationProgress.innerHTML = buildSavingsIllustrationProgressDots(
                savingsIllustrationData.steps.length,
                savingsIllustrationStepIndex
            );
        }
        const boardDensityClass = visibleBuckets.length >= 5
            ? ' is-dense'
            : visibleBuckets.length >= 4
                ? ' is-compact'
                : '';
        const hasAllVisibleBuckets = visibleBuckets.length > 0 && visibleBuckets.length === savingsIllustrationData.rows.length;
        const isFinalIllustrationStep = savingsIllustrationStepIndex === savingsIllustrationData.steps.length - 1;
        const showProjectedYearEndTotal = isFinalIllustrationStep && (projectedBucketCount > 0 || step.kind === 'surplus');

        illustrationContent.innerHTML = `
            <div class="savings-illustration-board${boardDensityClass}" aria-label="${escapeSavingsIllustrationHtml(step.header)}">
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
                            <div class="savings-illustration-surplus-copy">
                                <span class="savings-illustration-surplus-label">Surplus Allocation</span>
                                <span class="savings-illustration-surplus-value">${escapeSavingsIllustrationHtml(`${money(savingsIllustrationData.savingsAllocation)} Available to Allocate`)}</span>
                            </div>
                        </div>
                        <div class="savings-illustration-surplus-shell${hasAllVisibleBuckets ? ' is-complete-flow' : ''}">
                            <div class="savings-illustration-bucket-list${visibleBuckets.length ? ' has-buckets' : ' is-empty'}">${bucketRows}</div>
                        </div>
                        ${showProjectedYearEndTotal ? `
                            <div class="savings-illustration-projection-box" aria-live="polite">
                                <span class="savings-illustration-projection-box__label">Projected Year-End Total</span>
                                <span class="savings-illustration-projection-box__value">${escapeSavingsIllustrationHtml(money(savingsIllustrationData.projectedYearEndTotal))}</span>
                                <span class="savings-illustration-projection-box__meta">${escapeSavingsIllustrationHtml(projectedBucketMeta)}</span>
                            </div>
                        ` : ''}
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
        const savingsAllocation = calculateExpenseLensMonthlyRemaining(state);
        const hasSourceData = !!state && hasExpenseLensFinancialData(state);

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

    const normalizeSavingsAllocationTemplateKey = (value) => {
        const normalized = String(value || '')
            .toLowerCase()
            .replace(/[^a-z0-9]+/g, ' ')
            .trim();

        if (/^retirement\b.*long term/.test(normalized)) {
            return 'retirement long term investments';
        }

        return normalized;
    };

    const parseSavingsAllocationPercentValue = (value) => {
        const numeric = parseFloat(String(value ?? '').replace(/[^0-9.-]/g, ''));
        return Number.isFinite(numeric) ? numeric : 0;
    };

    const formatSavingsAllocationPercentValue = (value) => {
        const numeric = Math.max(0, Number(value) || 0);
        const rounded = Math.round(numeric * 10) / 10;
        return Math.abs(rounded % 1) < 0.001 ? String(Math.round(rounded)) : rounded.toFixed(1);
    };

    const buildSavingsAllocationTemplateRows = (savedRows) => {
        const defaults = getDefaultSavingsAllocationRows();
        const normalizedSavedRows = Array.isArray(savedRows) ? savedRows : [];
        const matchedSavedRows = defaults.map((allocation, index) => (
            normalizedSavedRows[index]
            || normalizedSavedRows.find((row) => (
                normalizeSavingsAllocationTemplateKey(row?.name)
                === normalizeSavingsAllocationTemplateKey(allocation.name)
            ))
            || null
        ));
        const hasFullTemplateMatch = matchedSavedRows.every((row) => row && normalizeSavingsAllocationTemplateKey(row.name));
        const savedTemplatePercentTotal = matchedSavedRows.reduce((sum, row) => (
            sum + Math.max(0, parseSavingsAllocationPercentValue(row?.percent))
        ), 0);
        const shouldProrateTemplatePercents =
            hasFullTemplateMatch
            && savedTemplatePercentTotal > DEFAULT_SAVINGS_TEMPLATE_TOTAL_PERCENT + 0.5;
        const percentScale = shouldProrateTemplatePercents && savedTemplatePercentTotal > 0
            ? DEFAULT_SAVINGS_TEMPLATE_TOTAL_PERCENT / savedTemplatePercentTotal
            : 1;

        return defaults.map((allocation, index) => {
            const matchedSavedRow = matchedSavedRows[index] || {};
            const savedPercent = parseSavingsAllocationPercentValue(matchedSavedRow.percent);
            const resolvedPercent = matchedSavedRow.percent == null
                ? allocation.percent
                : formatSavingsAllocationPercentValue(savedPercent * percentScale);

            return {
                name: allocation.name,
                percent: resolvedPercent,
                description: allocation.description,
                isTemplate: true,
                aprPercent: matchedSavedRow.aprPercent || allocation.aprPercent || '',
                allocationStartDate: matchedSavedRow.allocationStartDate || todayIsoDate(),
                startingBalance: matchedSavedRow.startingBalance || ''
            };
        });
    };

    const loadAllocationState = async () => {
        allocationContainer.innerHTML = '';
        categoryCount = 0;

        const state = await loadPersistedState(saStateId);

        const rowsToRender = hasMeaningfulSavingsAllocationRows(state?.allocations)
            ? buildSavingsAllocationTemplateRows(state.allocations)
            : buildSavingsAllocationTemplateRows();

        rowsToRender.forEach((allocation) => {
            createAllocationRow(++categoryCount, allocation);
        });

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
        name.value = preName;
        name.readOnly = true;
        name.tabIndex = -1;
        name.setAttribute('aria-readonly', 'true');
        name.title = description || '';

        const amt = document.createElement('input');
        amt.className = 'sa-alloc-amount legend-money-field allocation-amount';
        amt.readOnly = true;
        amt.tabIndex = -1;
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
        const projectedValue = document.createElement('input');
        projectedValue.type = 'text';
        projectedValue.className = 'sa-alloc-projected-field legend-money-field';
        projectedValue.readOnly = true;
        projectedValue.tabIndex = -1;
        projectedValue.value = '0';
        const projectedWrap = makeSaMoney(projectedValue);
        projectedWrap.classList.add('sa-alloc-projected');

        const drawer = document.createElement('div');
        drawer.className = 'sa-alloc-drawer';
        const note = document.createElement('div');
        note.className = 'sa-alloc-note';
        note.textContent = description || 'Adjust APR, start date, and opening balance to refine the year-end projection.';

        if (isDualPanel) {
            const editBtn = document.createElement('button');
            editBtn.type = 'button';
            editBtn.className = 'sa-alloc-toggle';
            const syncLabel = () => { editBtn.textContent = drawer.classList.contains('is-open') ? 'Hide' : 'Details'; };
            editBtn.addEventListener('click', () => { drawer.classList.toggle('is-open'); syncLabel(); });
            syncLabel();
            grid.append(name, pctWrap, amtWrap, aprWrap, projectedWrap, editBtn);
            drawer.append(startDate, startingWrap, note);
            fitSingleLineControlText(name, { minSize: 10, maxSize: 14 });
            fitSingleLineControlText(amt, { minSize: 10, maxSize: 14, reserve: 18 });
            fitSingleLineControlText(pct, { minSize: 10, maxSize: 14, reserve: 18 });
            fitSingleLineControlText(apr, { minSize: 10, maxSize: 14, reserve: 18 });
        } else {
            grid.append(name, amtWrap, pctWrap, aprWrap, startDate, startingWrap, projectedWrap);
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
                const projectedField = projectedEl.querySelector('.sa-alloc-projected-field');
                if (projectedField) projectedField.value = Math.abs(roundedProjection).toLocaleString();
                const projectionSummary = projection.months > 0
                    ? `${projection.months} monthly period${projection.months === 1 ? '' : 's'} through year end`
                    : 'No remaining monthly periods in the current year';
                projectedEl.removeAttribute('title');
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

        markGold(saPctTotal);
        if (remaining > 0) markIncome(saRemaining);
        else if (remaining < 0) markExpense(saRemaining);
        else markNeutral(saRemaining);

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
            const field = el.querySelector('.sa-alloc-projected-field');
            const val = field ? parseSavingsMoney(field.value || field.textContent || '') : 0;
            if (val > 0) {
                field && markWithSuffix(markIncome, field);
            } else if (val < 0) {
                field && markWithSuffix(markExpense, field);
            } else {
                field && markWithSuffix(markNeutral, field);
            }
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
        const expenseLensHasPartner = !isBusinessExpenseLens && (hasSpouse === true || (hasSpouse !== false && spouseFirstName.length > 0));

        hostElement.innerHTML = `
        <div class="networth-tool p-4 el-shell">

            <div id="${elId('TipLayer')}" class="el-tip-layer"></div>

            <div class="el-header">
                <h3 class="el-title">${expenseLensTitle}</h3>
                <p class="el-subtitle">${expenseLensSubtitle}</p>
            </div>

            <div class="el-label">
                ${isBusinessExpenseLens ? "Business Total Income" : "Total Income"}
                <span class="el-i" tabindex="0"
                      data-tip="<b>Examples:</b> 4,500 • 6,200 (total monthly income before allocating categories)">i</span>
            </div>
            <div class="el-income-input-wrap">
                <span class="el-currency-prefix">$</span>
                <input id="${elId('Income')}" type="text" 
                       class="form-control mb-3"
                       placeholder="Enter total monthly income" />
            </div>

            <div id="${elId('Categories')}" class="el-category-stack"></div>

            <div id="${elId('MarginWrap')}" class="el-toolbar">
                <button id="${elId('AddCat')}"
                        class="btn el-toolbar-btn">
                    + Add Category
                </button>
                <div id="${elId('ActionMeta')}" class="el-toolbar-actions">
                    <div id="${elId('Margin')}" class="el-balance-chip el-balance-chip-muted">
                        Remaining Balance: $0
                    </div>
                </div>
            </div>

            <div id="${elId('Tips')}" class="el-tip-strip">
                ${expenseLensDefaultTip}
            </div>
        </div>`;

        const container = hostElement.querySelector('.networth-tool');
        applyToolBoxStyles(container);

        const categoriesContainer = elById("Categories");
        const addBtn = elById("AddCat");
        const elTips = elById("Tips");
        const elMargin = elById("Margin");
        const elActionMeta = elById("ActionMeta");
        const elIncome = elById("Income");
       

       

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

        const EL_MAX_INCOME_STREAMS_PER_GROUP = 4;
        const EL_INCOME_METRIC_META = {
            label: 'Income',
            text: '#FFF7ED',
            muted: '#FDE68A',
            bg: 'linear-gradient(145deg, rgba(180,83,9,0.94) 0%, rgba(120,53,15,0.98) 100%)',
            border: 'rgba(245,158,11,0.56)',
            shadow: '0 14px 30px rgba(245,158,11,0.22), inset 0 1px 0 rgba(255,255,255,0.08)'
        };
        const EL_ENDING_BALANCE_META = {
            label: 'Week End',
            text: '#ECFDF5',
            muted: '#A7F3D0',
            bg: 'linear-gradient(145deg, rgba(6,95,70,0.96) 0%, rgba(5,150,105,0.99) 100%)',
            border: 'rgba(110,231,183,0.58)',
            shadow: '0 18px 36px rgba(16,185,129,0.24), inset 0 1px 0 rgba(255,255,255,0.10)'
        };
        const EL_MONTH_END_BALANCE_META = {
            ...EL_ENDING_BALANCE_META,
            label: 'Month End'
        };
        const EL_NEGATIVE_METRIC_META = {
            label: 'Negative',
            text: '#FFF1F2',
            muted: '#FCA5A5',
            bg: 'linear-gradient(145deg, rgba(220,38,38,0.92) 0%, rgba(127,29,29,0.98) 100%)',
            border: 'rgba(248,113,113,0.56)',
            shadow: '0 14px 30px rgba(239,68,68,0.22), inset 0 1px 0 rgba(255,255,255,0.08)'
        };

        const makePossessiveIncomeLabel = (name) => name
            ? `${name}${name.endsWith('s') ? "'" : "'s"} Income`
            : 'Client Income';

        const buildIncomeGroupDefinitions = () => {
            if (isBusinessExpenseLens) return [];

            const definitions = [
                {
                    key: 'primary',
                    label: makePossessiveIncomeLabel(clientFirstName)
                }
            ];

            if (expenseLensHasPartner) {
                const partnerName = spouseFirstName || 'Partner';
                definitions.push({
                    key: 'secondary',
                    label: makePossessiveIncomeLabel(partnerName)
                });
            }

            return definitions;
        };

        const incomeGroupDefinitions = buildIncomeGroupDefinitions();
        const incomeGroupLabelMap = incomeGroupDefinitions.reduce((acc, definition) => {
            acc[definition.key] = definition.label;
            return acc;
        }, {});

        let incomeStreamSeed = 0;
        const createIncomeStreamId = (groupKey) => {
            incomeStreamSeed += 1;
            return `${groupKey}-${Date.now().toString(36)}-${incomeStreamSeed.toString(36)}`;
        };

        const getLegacyIncomeStreamAutoLabel = (groupKey) => {
            if (groupKey === 'secondary') {
                const partnerName = spouseFirstName || 'Partner';
                return `${partnerName}${partnerName.endsWith('s') ? "'" : "'s"} Pay`;
            }

            return clientFirstName
                ? `${clientFirstName}${clientFirstName.endsWith('s') ? "'" : "'s"} Pay`
                : 'Primary Pay';
        };

        const normalizeIncomeStreamLabel = (groupKey, value) => {
            const label = String(value || '').trim();
            return label === getLegacyIncomeStreamAutoLabel(groupKey) ? '' : label;
        };

        const normalizeIncomeAnchorDate = (value) => {
            const parsed = parseScheduledAnchorDate(value);
            if (!parsed) return getDefaultScheduledAnchorDate();
            return `${parsed.getFullYear()}-${String(parsed.getMonth() + 1).padStart(2, '0')}-${String(parsed.getDate()).padStart(2, '0')}`;
        };

        const createIncomeStream = (groupKey, overrides = {}) => ({
            id: String(overrides.id || '').trim() || createIncomeStreamId(groupKey),
            label: normalizeIncomeStreamLabel(groupKey, overrides.label),
            amount: String(overrides.amount || '').trim(),
            frequency: normalizeScheduledFrequency(overrides.frequency || 'monthly'),
            anchorDate: normalizeIncomeAnchorDate(overrides.anchorDate || getDefaultScheduledAnchorDate())
        });

        const sanitizeIncomeStreamList = (groupKey, streams = []) => {
            const normalized = Array.isArray(streams)
                ? streams.slice(0, EL_MAX_INCOME_STREAMS_PER_GROUP).map(stream => createIncomeStream(groupKey, stream))
                : [];
            return normalized.length > 0 ? normalized : [createIncomeStream(groupKey)];
        };

        const summarizePersonalIncomeGroups = (groups, options = {}) => summarizeExpenseLensIncomeGroups(groups, {
            ...options,
            groupLabelMap: incomeGroupLabelMap
        });

        const serializeIncomeStreamsForSave = (groups) => {
            const serialized = {};
            incomeGroupDefinitions.forEach((definition) => {
                serialized[definition.key] = sanitizeIncomeStreamList(definition.key, groups?.[definition.key]).map(stream => ({
                    id: stream.id,
                    label: stream.label,
                    amount: stream.amount,
                    frequency: stream.frequency,
                    anchorDate: stream.anchorDate
                }));
            });
            return serialized;
        };

        const EL_BILL_FREQUENCIES = [
            { value: 'monthly', label: 'Monthly' },
            { value: 'weekly', label: 'Weekly' },
            { value: 'biweekly', label: 'Bi-weekly' },
        ];

        const normalizeBillFrequency = (value) => normalizeScheduledFrequency(value);

        const elFrequencyLabel = (value) => {
            const normalized = normalizeBillFrequency(value);
            return EL_BILL_FREQUENCIES.find(f => f.value === normalized)?.label || 'Monthly';
        };

        const EL_PAYMENT_METHODS = [
            { value: '', label: '--Select--' },
            { value: 'debit', label: 'Debit' },
            { value: 'credit', label: 'Credit' },
        ];

        const EL_PAYMENT_FILTERS = [
            { value: 'all', label: 'All Bills' },
            { value: 'debit', label: 'Debit / Cash' },
            { value: 'credit', label: 'Credit' },
        ];

        const normalizeBillPaymentMethod = (value) => {
            const normalized = (value || '').toString().trim().toLowerCase().replace(/[^a-z]/g, '');
            if (normalized === 'credit') return 'credit';
            if (normalized === 'debit' || normalized === 'cash' || normalized === 'cashdebit' || normalized === 'debitcash')
                return 'debit';
            return '';
        };

        const EL_PAYMENT_METHOD_META = {
            debit: {
                label: 'Debit',
                text: '#F0FDF4',
                muted: '#A7F3D0',
                bg: 'linear-gradient(145deg, rgba(8,145,112,0.90) 0%, rgba(6,78,59,0.98) 100%)',
                border: 'rgba(110,231,183,0.56)',
                shadow: '0 14px 30px rgba(16,185,129,0.26), inset 0 1px 0 rgba(255,255,255,0.08)'
            },
            credit: {
                label: 'Credit',
                text: '#FFF1F2',
                muted: '#FBCFE8',
                bg: 'linear-gradient(145deg, rgba(190,24,93,0.90) 0%, rgba(131,24,67,0.98) 100%)',
                border: 'rgba(251,113,133,0.56)',
                shadow: '0 14px 30px rgba(244,63,94,0.24), inset 0 1px 0 rgba(255,255,255,0.08)'
            },
            unassigned: {
                label: 'Open',
                text: '#F8FAFC',
                muted: '#CBD5E1',
                bg: 'linear-gradient(145deg, rgba(71,85,105,0.78) 0%, rgba(30,41,59,0.92) 100%)',
                border: 'rgba(148,163,184,0.42)',
                shadow: '0 10px 24px rgba(15,23,42,0.22), inset 0 1px 0 rgba(255,255,255,0.06)'
            },
            total: {
                label: 'Total',
                text: '#F0F9FF',
                muted: '#7DD3FC',
                bg: 'linear-gradient(145deg, rgba(14,116,144,0.92) 0%, rgba(12,74,110,0.98) 100%)',
                border: 'rgba(56,189,248,0.58)',
                shadow: '0 14px 30px rgba(14,165,233,0.24), inset 0 1px 0 rgba(255,255,255,0.08)'
            }
        };

        const elGetPaymentMethodMeta = (value) => {
            const normalized = normalizeBillPaymentMethod(value);
            return EL_PAYMENT_METHOD_META[normalized || 'unassigned'];
        };

        const EL_METRIC_TONE_CLASS_NAMES = ['el-tone-income', 'el-tone-debit', 'el-tone-credit', 'el-tone-open', 'el-tone-total', 'el-tone-ending', 'el-tone-negative', 'el-tone-empty'];
        const elResolveMetricToneClass = (meta) => {
            if (meta === EL_INCOME_METRIC_META) return 'el-tone-income';
            if (meta === EL_PAYMENT_METHOD_META.debit) return 'el-tone-debit';
            if (meta === EL_PAYMENT_METHOD_META.credit) return 'el-tone-credit';
            if (meta === EL_PAYMENT_METHOD_META.total) return 'el-tone-total';
            if (meta === EL_ENDING_BALANCE_META || meta === EL_MONTH_END_BALANCE_META) return 'el-tone-ending';
            if (meta === EL_NEGATIVE_METRIC_META) return 'el-tone-negative';
            return 'el-tone-open';
        };
        const elApplyMetricToneClass = (element, toneClass) => {
            if (!element) return;
            element.classList.remove(...EL_METRIC_TONE_CLASS_NAMES);
            if (toneClass) element.classList.add(toneClass);
        };

        const elCreateWeekMetricChip = (label, amount, meta, note = '', options = {}) => {
            const hasValue = amount !== 0;
            const toneMeta = amount < 0 && options.negativeMeta ? options.negativeMeta : meta;
            const chip = document.createElement('div');
            chip.className = 'el-week-metric-chip';
            elApplyMetricToneClass(chip, hasValue ? elResolveMetricToneClass(toneMeta) : 'el-tone-empty');

            const labelEl = document.createElement('span');
            labelEl.textContent = label.toUpperCase();
            labelEl.className = 'el-week-metric-chip__label';

            const valueEl = document.createElement('span');
            valueEl.textContent = hasValue ? `${amount < 0 ? '-$' : '$'}${Math.abs(amount).toLocaleString()}` : '—';
            valueEl.className = 'el-week-metric-chip__value';

            chip.appendChild(labelEl);
            chip.appendChild(valueEl);

            if (note) {
                const noteEl = document.createElement('span');
                noteEl.textContent = note;
                noteEl.className = 'el-week-metric-chip__note';
                chip.appendChild(noteEl);
            }

            return chip;
        };

        const elFormatCashflowCurrency = (amount) => `${amount < 0 ? '-$' : '$'}${Math.abs(amount).toLocaleString()}`;
        const elApplyBalanceTone = (element, amount) => {
            if (!element) return;
            element.classList.add('el-balance-surface');
            element.classList.remove('el-balance-chip-info', 'el-balance-chip-alert');
            element.classList.toggle('is-positive', amount >= 0);
            element.classList.toggle('is-negative', amount < 0);
        };
        const elCreateBalanceValue = (amount, fontSize = '0.78rem') => {
            const value = document.createElement('span');
            value.textContent = elFormatCashflowCurrency(amount);
            value.className = `el-balance-value${fontSize === '0.74rem' ? ' el-balance-value--compact' : ''}`;
            value.classList.add(amount >= 0 ? 'is-positive' : 'is-negative');
            return value;
        };

        const elCreateToneBadge = (label, meta) => {
            const badge = document.createElement('span');
            badge.textContent = label;
            badge.className = 'el-tone-badge';
            elApplyMetricToneClass(badge, elResolveMetricToneClass(meta));
            return badge;
        };

        const elCreatePaymentBadge = (value) => {
            const meta = elGetPaymentMethodMeta(value);
            return elCreateToneBadge(meta.label, meta);
        };

        const elPaymentFilterLabel = (value) => {
            const normalized = EL_PAYMENT_FILTERS.some(option => option.value === value) ? value : 'all';
            return EL_PAYMENT_FILTERS.find(option => option.value === normalized)?.label || 'All Bills';
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
                createCategoryRow(++categoryCount, row.name, row.due, row.amount, row.frequency, '', row.isTemplate);
            });
        };

        // -----------------------------
        // State Handling
        // -----------------------------
        let incomeGroupsState = {};
        let incomeGroupsHost = null;
        let incomeGroupRefs = new Map();

        const buildPersonalIncomeGroupsState = (state = null) => {
            if (isBusinessExpenseLens) return {};

            const persistedGroups = getExpenseLensIncomeStreamGroupsFromState(state);
            const nextState = {};

            incomeGroupDefinitions.forEach((definition) => {
                nextState[definition.key] = sanitizeIncomeStreamList(definition.key, persistedGroups[definition.key]);
            });

            return nextState;
        };

        incomeGroupsState = buildPersonalIncomeGroupsState();

        const buildIncomeCompatibilityState = () => {
            if (isBusinessExpenseLens) {
                return { income: elIncome.value || '' };
            }

            const summary = summarizePersonalIncomeGroups(incomeGroupsState);
            const primaryTotal = summary.groupTotals.primary || 0;
            const secondaryTotal = summary.groupTotals.secondary || 0;

            return {
                income: summary.monthlyTotal > 0 ? formatNumber(summary.monthlyTotal) : '',
                primaryIncome: primaryTotal > 0 ? formatNumber(primaryTotal) : '',
                spouseIncome: secondaryTotal > 0 ? formatNumber(secondaryTotal) : '',
                incomeStreams: serializeIncomeStreamsForSave(incomeGroupsState)
            };
        };

        const syncPersonalIncomeDisplay = () => {
            if (isBusinessExpenseLens) {
                return {
                    monthlyTotal: parseSavingsMoney(elIncome.value),
                    groupTotals: { primary: 0, secondary: 0 },
                    hits: [],
                    count: 0
                };
            }

            const summary = summarizePersonalIncomeGroups(incomeGroupsState);
            elIncome.value = summary.monthlyTotal > 0 ? formatNumber(summary.monthlyTotal) : '';

            incomeGroupDefinitions.forEach((definition) => {
                const refs = incomeGroupRefs.get(definition.key);
                if (!refs) return;

                const groupTotal = summary.groupTotals[definition.key] || 0;
                refs.total.textContent = `$ ${groupTotal.toLocaleString()}`;
                refs.share.textContent = summary.monthlyTotal > 0
                    ? `${((groupTotal / summary.monthlyTotal) * 100).toFixed(1)}%`
                    : '0%';
                if (groupTotal > 0) markIncome(refs.total);
                else markNeutral(refs.total);
                markNeutral(refs.share);
                refs.addBtn.disabled = incomeGroupsState[definition.key].length >= EL_MAX_INCOME_STREAMS_PER_GROUP;
                refs.addBtn.classList.toggle('is-disabled', refs.addBtn.disabled);
            });

            return summary;
        };

        const renderPersonalIncomeGroups = () => {
            if (isBusinessExpenseLens || !incomeGroupsHost) return;

            incomeGroupsHost.innerHTML = '';
            incomeGroupRefs = new Map();
            const useSplitPersonLayout = incomeGroupDefinitions.length > 1;
            incomeGroupsHost.classList.toggle('el-income-groups--paired', useSplitPersonLayout);
            incomeGroupsHost.classList.toggle('el-income-groups--solo', !useSplitPersonLayout);

            incomeGroupDefinitions.forEach((definition) => {
                const groupStreams = sanitizeIncomeStreamList(definition.key, incomeGroupsState[definition.key]);
                incomeGroupsState[definition.key] = groupStreams;

                const groupRow = document.createElement('div');
                groupRow.className = 'el-income-group';
                groupRow.classList.add(useSplitPersonLayout ? 'el-income-group--paired' : 'el-income-group--solo');

                const summaryCard = document.createElement('div');
                summaryCard.className = 'el-income-summary';

                const summaryTop = document.createElement('div');
                summaryTop.className = 'el-income-summary-main';

                const labelEl = document.createElement('div');
                labelEl.className = 'el-income-group-label';
                labelEl.textContent = definition.label;

                const totalEl = document.createElement('div');
                totalEl.className = 'el-income-total';
                totalEl.textContent = '$ 0';

                const shareEl = document.createElement('span');
                shareEl.className = 'el-income-share';
                shareEl.textContent = '0%';

                const addBtn = document.createElement('button');
                addBtn.type = 'button';
                addBtn.className = 'btn el-toolbar-btn';
                addBtn.textContent = '+ Add Stream';
                addBtn.classList.add('lf-js-001');
                addBtn.addEventListener('click', () => {
                    if (incomeGroupsState[definition.key].length >= EL_MAX_INCOME_STREAMS_PER_GROUP) return;
                    incomeGroupsState[definition.key].push(createIncomeStream(definition.key));
                    renderPersonalIncomeGroups();
                    refreshExpenseLensViews({ sortRows: false });
                });

                summaryTop.appendChild(labelEl);
                summaryTop.appendChild(shareEl);
                summaryCard.appendChild(summaryTop);
                summaryCard.appendChild(totalEl);
                summaryCard.appendChild(addBtn);

                const streamsWrap = document.createElement('div');
                streamsWrap.className = 'el-stream-grid';
                streamsWrap.classList.add('el-stream-grid--single');

                groupStreams.forEach((stream, streamIndex) => {
                    const streamCard = document.createElement('div');
                    streamCard.className = 'el-stream-card el-stream-card--income';

                    const streamTitle = document.createElement('span');
                    streamTitle.className = 'el-stream-tag';
                    streamTitle.textContent = `Stream ${streamIndex + 1}`;

                    let removeBtn = null;
                    if (groupStreams.length > 1) {
                        removeBtn = document.createElement('button');
                        removeBtn.type = 'button';
                        removeBtn.textContent = '×';
                        removeBtn.className = 'el-stream-remove';
                        removeBtn.addEventListener('click', () => {
                            incomeGroupsState[definition.key].splice(streamIndex, 1);
                            renderPersonalIncomeGroups();
                            refreshExpenseLensViews({ sortRows: false });
                        });
                    }

                    const amountWrap = document.createElement('div');
                    amountWrap.className = 'el-currency-field';

                    const amountInput = document.createElement('input');
                    amountInput.type = 'text';
                    amountInput.className = 'form-control';
                    amountInput.placeholder = '0';
                    amountInput.value = stream.amount;
                    amountInput.classList.add('lf-js-002');
                    amountInput.addEventListener('input', () => {
                        incomeGroupsState[definition.key][streamIndex].amount = amountInput.value;
                        refreshExpenseLensViews({ sortRows: false });
                    });
                    amountInput.addEventListener('blur', () => {
                        amountInput.value = formatNumber(amountInput.value);
                        incomeGroupsState[definition.key][streamIndex].amount = amountInput.value;
                        refreshExpenseLensViews({ sortRows: false });
                    });

                    const amountSuffix = document.createElement('span');
                    amountSuffix.textContent = '$';
                    amountSuffix.className = 'el-currency-prefix';
                    amountWrap.appendChild(amountInput);
                    amountWrap.appendChild(amountSuffix);
                    upgradeMoneyInput(amountInput);

                    const frequencySelect = document.createElement('select');
                    frequencySelect.className = 'form-select';
                    frequencySelect.classList.add('lf-js-003');
                    EL_BILL_FREQUENCIES.forEach((option) => {
                        const opt = document.createElement('option');
                        opt.value = option.value;
                        opt.textContent = option.label;
                        frequencySelect.appendChild(opt);
                    });
                    frequencySelect.value = normalizeBillFrequency(stream.frequency);
                    frequencySelect.addEventListener('change', () => {
                        incomeGroupsState[definition.key][streamIndex].frequency = normalizeBillFrequency(frequencySelect.value);
                        refreshExpenseLensViews({ sortRows: false });
                    });
                    fitSingleLineControlText(frequencySelect, { minSize: 9.5, maxSize: 11, reserve: 22 });

                    const dateInput = document.createElement('input');
                    dateInput.type = 'date';
                    dateInput.className = 'form-control';
                    dateInput.value = normalizeIncomeAnchorDate(stream.anchorDate);
                    dateInput.classList.add('lf-js-004');
                    dateInput.addEventListener('change', () => {
                        incomeGroupsState[definition.key][streamIndex].anchorDate = normalizeIncomeAnchorDate(dateInput.value);
                        dateInput.value = incomeGroupsState[definition.key][streamIndex].anchorDate;
                        refreshExpenseLensViews({ sortRows: false });
                    });

                    streamCard.appendChild(streamTitle);
                    streamCard.appendChild(amountWrap);
                    streamCard.appendChild(frequencySelect);
                    streamCard.appendChild(dateInput);
                    if (removeBtn) {
                        streamCard.appendChild(removeBtn);
                    } else {
                        const streamSpacer = document.createElement('span');
                        streamSpacer.className = 'el-stream-spacer';
                        streamCard.appendChild(streamSpacer);
                    }
                    streamsWrap.appendChild(streamCard);
                });

                groupRow.appendChild(summaryCard);
                groupRow.appendChild(streamsWrap);
                incomeGroupsHost.appendChild(groupRow);
                incomeGroupRefs.set(definition.key, { total: totalEl, share: shareEl, addBtn });
            });

            syncPersonalIncomeDisplay();
        };

        const saveExpenseLensState = (extraState = {}) => {
            try {
                const compatibilityState = buildIncomeCompatibilityState();
                const categories = [];
                categoriesContainer.querySelectorAll(`[id^="${elId('CatRow')}"]`).forEach(row => {
                    const index = row.id.replace(elId('CatRow'), '');
                    const nameEl = elById(`CatName${index}`);
                    const dueEl = elById(`CatDue${index}`);
                    const frequencyEl = elById(`CatFrequency${index}`);
                    const paymentMethodEl = elById(`CatPaymentMethod${index}`);
                    const amountEl = elById(`CatAmount${index}`);
                    const name = nameEl ? nameEl.value || '' : '';
                    const due = dueEl ? dueEl.value || '' : '';
                    const frequency = normalizeBillFrequency(frequencyEl ? frequencyEl.value : 'monthly');
                    const paymentMethod = normalizeBillPaymentMethod(paymentMethodEl ? paymentMethodEl.value : '');
                    const amount = amountEl ? amountEl.value || '' : '';
                    const isTemplate = row.dataset.isTemplate === 'true';
                    const isPinned = row.dataset.isPinned === 'true';
                    categories.push({ index, name, due, frequency, paymentMethod, amount, isTemplate, isPinned });
                });
                const state = { ...compatibilityState, categories, ...extraState };
                savePersistedState(expenseLensToolStateId, state);
            } catch (e) { console.error(e); }
        };

        const loadExpenseLensState = async () => {
            try {
                const state = await loadPersistedState(expenseLensToolStateId);
                categoriesContainer.innerHTML = '';
                categoryCount = 0;
                let categoriesCreated = 0;

                if (!isBusinessExpenseLens) {
                    incomeGroupsState = buildPersonalIncomeGroupsState(state);
                    renderPersonalIncomeGroups();
                } else {
                    elIncome.value = state?.income || '';
                }

                if (state?.categories && state.categories.length > 0) {
                    state.categories.forEach(cat => {
                        createCategoryRow(++categoryCount, cat.name, cat.due || '', cat.amount, cat.frequency || cat.recurrence, cat.paymentMethod || '', cat.isTemplate === true, cat.isPinned === true);
                        categoriesCreated++;
                    });
                }

                if (categoriesCreated === 0) {
                    const prof = window.LegendFinanceProfile?.get?.();
                    if (prof) {
                        if (!isBusinessExpenseLens) {
                            const summary = syncPersonalIncomeDisplay();
                            const profileIncome = prof.monthlyNet || prof.monthlyGross || '';
                            if (summary.monthlyTotal <= 0 && parseSavingsMoney(profileIncome) > 0 && incomeGroupDefinitions[0]) {
                                incomeGroupsState[incomeGroupDefinitions[0].key][0].amount = profileIncome;
                                renderPersonalIncomeGroups();
                            }
                        } else if (!elIncome.value) {
                            elIncome.value = prof.monthlyNet || prof.monthlyGross || '';
                        }

                        if (Array.isArray(prof.expenses) && prof.expenses.length > 0) {
                            prof.expenses.forEach(exp => {
                                const amt = exp?.occurrenceAmount ?? exp?.amount ?? '';
                                createCategoryRow(++categoryCount, exp?.name || `Expense ${categoryCount}`, exp?.due || '', amt, exp?.frequency || exp?.recurrence, exp?.paymentMethod || '', false, exp?.isPinned === true);
                                categoriesCreated++;
                            });
                        }
                    }
                }

                if (categoriesCreated === 0) injectDefaultExpenseRows();
                if (!isBusinessExpenseLens) syncPersonalIncomeDisplay();
                refreshExpenseLens({ sortRows: true });
            } catch (e) { console.error(e); }
        };

        // Active week filter (null = show all)
        let elActiveWeek = null;
        // Which week's detail is expanded in the panel (independent of filter)
        let elExpandedWeek = null;
        // Drag-and-drop state
        let elDragSrc = null;
        let elActivePaymentFilter = 'all';
        let weeklyBtn = null;
        let paymentFilterSelect = null;
        let weekPanelBackdrop = null;
        let weekPanel = null;
        let elWeekPanelBodyOverflow = '';

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

        const elGetBillPaymentMethod = (index) => {
            const paymentMethodEl = elById(`CatPaymentMethod${index}`);
            return normalizeBillPaymentMethod(paymentMethodEl?.value || '');
        };

        const elMatchesPaymentFilter = (paymentMethod) => {
            return elActivePaymentFilter === 'all' || paymentMethod === elActivePaymentFilter;
        };

        const syncExpenseLensViewControls = () => {
            if (weeklyBtn) weeklyBtn.textContent = elActiveWeek ? `${elActiveWeek.label} ▾` : 'Weekly ▾';
            const topBtn = elById('WeeklyBtnTop');
            if (topBtn) topBtn.textContent = elActiveWeek ? `${elActiveWeek.label} ▾` : 'Weekly ▾';
            if (paymentFilterSelect) paymentFilterSelect.value = elActivePaymentFilter;
        };

        const applyExpenseLensRowVisibility = () => {
            categoriesContainer.querySelectorAll(`[id^="${elId('CatRow')}"]`).forEach(row => {
                const idx = row.id.replace(elId('CatRow'), '');
                const matchesWeek = !elActiveWeek || elGetBillOccurrenceDays(idx, elActiveWeek).length > 0;
                const matchesPayment = elMatchesPaymentFilter(elGetBillPaymentMethod(idx));
                const show = matchesWeek && matchesPayment;
                row.classList.toggle('is-filter-hidden', !show);
            });
        };

        const refreshExpenseLensViews = (options = {}) => {
            const shouldSortRows = !!options.sortRows;
            applyExpenseLensRowVisibility();
            syncExpenseLensViewControls();
            refreshExpenseLens({ sortRows: shouldSortRows });
            if (weekPanelBackdrop && weekPanelBackdrop.style.display !== 'none') renderWeekPanel();
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
        const createCategoryRow = (index, preName = '', preDue = '', preAmount = '', preFrequency = 'monthly', prePaymentMethod = '', isTemplate = false, isPinned = false) => {
            const div = document.createElement("div");
            div.className = `d-flex align-items-center el-category-row ${isDualPanel ? 'el-category-row--compact' : 'el-category-row--standard'}`;
            div.id = `${elId('CatRow')}${index}`;
            div.dataset.isTemplate = isTemplate ? 'true' : 'false';
            div.dataset.isPinned = isPinned ? 'true' : 'false';

            const nameInput = document.createElement("input");
            nameInput.type = "text";
            nameInput.id = `${elId('CatName')}${index}`;
            nameInput.className = `form-control flex-grow-1 el-row-name ${isDualPanel ? 'el-row-name--compact' : 'el-row-name--standard'}`;
            nameInput.placeholder = `Category ${index} Name`;
            nameInput.value = preName;
            nameInput.addEventListener("input", refreshExpenseLensViews);

            // Due date field
            const dueWrapper = document.createElement("div");
            dueWrapper.className = `el-row-date-wrap ${isDualPanel ? 'el-row-date-wrap--compact' : 'el-row-date-wrap--standard'}`;
            const dueInput = document.createElement("input");
            dueInput.type = "date";
            dueInput.id = `${elId('CatDue')}${index}`;
            dueInput.className = "form-control el-row-date-input";
            dueInput.placeholder = "Due";
            const resolvedPreFrequency = normalizeBillFrequency(preFrequency);
            const shouldPreserveDueDate = resolvedPreFrequency === 'weekly' || resolvedPreFrequency === 'biweekly';
            dueInput.value = shouldPreserveDueDate && preDue ? preDue : toCurrentMonthDue(preDue);
            dueInput.addEventListener("input", refreshExpenseLensViews);
            dueInput.addEventListener("blur", () => refreshExpenseLensViews({ sortRows: true }));
            dueWrapper.appendChild(dueInput);

            const frequencySelect = document.createElement("select");
            frequencySelect.id = `${elId('CatFrequency')}${index}`;
            frequencySelect.className = `form-select el-row-select ${isDualPanel ? 'el-row-select--compact' : 'el-row-select--standard'}`;
            EL_BILL_FREQUENCIES.forEach(option => {
                const opt = document.createElement("option");
                opt.value = option.value;
                opt.textContent = option.label;
                frequencySelect.appendChild(opt);
            });
            frequencySelect.value = resolvedPreFrequency;
            frequencySelect.addEventListener("change", () => refreshExpenseLensViews({ sortRows: true }));

            const paymentMethodSelect = document.createElement("select");
            paymentMethodSelect.id = `${elId('CatPaymentMethod')}${index}`;
            paymentMethodSelect.className = `form-select el-row-select ${isDualPanel ? 'el-row-select--compact' : 'el-row-select--standard'}`;
            paymentMethodSelect.setAttribute("aria-label", "Payment method");
            EL_PAYMENT_METHODS.forEach(option => {
                const opt = document.createElement("option");
                opt.value = option.value;
                opt.textContent = option.label;
                paymentMethodSelect.appendChild(opt);
            });
            paymentMethodSelect.value = normalizeBillPaymentMethod(prePaymentMethod);
            paymentMethodSelect.addEventListener("change", () => refreshExpenseLensViews({ sortRows: true }));

            const amountWrapper = document.createElement("div");
            amountWrapper.className = `el-currency-field ${isDualPanel ? 'el-row-amount-wrap--compact' : 'el-row-amount-wrap--standard'}`;

            const amountInput = document.createElement("input");
            amountInput.type = "text";
            amountInput.id = `${elId('CatAmount')}${index}`;
            amountInput.className = "form-control el-row-amount-input";
            amountInput.placeholder = "Amount";
            amountInput.value = preAmount;

            const dollarSpan = document.createElement("span");
            dollarSpan.textContent = "$";
            dollarSpan.className = "el-currency-prefix";

            amountWrapper.appendChild(amountInput);
            amountWrapper.appendChild(dollarSpan);
            upgradeMoneyInput(amountInput);

            const percentSpan = document.createElement("span");
            percentSpan.id = `${elId('Out')}${index}`;
            percentSpan.className = `el-percentage ${isDualPanel ? 'el-percentage--compact' : 'el-percentage--standard'}`;

            const deleteBtn = document.createElement("button");
            deleteBtn.textContent = "✕";
            deleteBtn.className = `el-delete-btn ${isDualPanel ? 'el-delete-btn--compact' : 'el-delete-btn--standard'}`;
            const isInsuranceRow = isTemplate && preName.toLowerCase().includes("insurance");
            if (isInsuranceRow) {
                deleteBtn.classList.add('is-locked');
                deleteBtn.setAttribute("disabled", "true");
                deleteBtn.setAttribute("aria-disabled", "true");
                deleteBtn.title = "Insurance rows cannot be removed";
            } else {
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
            leftControls.className = `el-left-controls ${isDualPanel ? 'el-left-controls--compact' : 'el-left-controls--standard'}`;

            // Drag handle — drag only activates from this grip, never from inputs
            const dragHandle = document.createElement("span");
            dragHandle.textContent = "⠿";
            dragHandle.title = "Drag to reorder";
            dragHandle.className = `el-drag-handle ${isDualPanel ? 'el-drag-handle--compact' : 'el-drag-handle--standard'}`;
            dragHandle.addEventListener("pointerdown", () => { div.draggable = true; });
            dragHandle.addEventListener("pointerup",   () => { div.draggable = false; });
            dragHandle.addEventListener("pointercancel", () => { div.draggable = false; });

            const pinBtn = document.createElement("button");
            pinBtn.type = "button";
            pinBtn.className = `el-pin-btn ${isDualPanel ? 'el-pin-btn--compact' : 'el-pin-btn--standard'}`;

            const syncPinButton = () => {
                const pinned = isExpenseRowPinned(div);
                pinBtn.textContent = pinned ? "★" : "☆";
                pinBtn.title = pinned ? "Pinned to top" : "Pin to top";
                pinBtn.setAttribute("aria-label", pinned ? "Unpin category from top" : "Pin category to top");
                pinBtn.setAttribute("aria-pressed", pinned ? "true" : "false");
                pinBtn.classList.toggle('is-pinned', pinned);
                div.classList.toggle('is-pinned', pinned);
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
                setTimeout(() => { div.classList.add('is-drag-source'); }, 0);
            });
            div.addEventListener("dragend", () => {
                div.classList.remove('is-drag-source');
                div.draggable = false;
                elDragSrc = null;
                categoriesContainer.querySelectorAll(`[id^="${elId('CatRow')}"]`).forEach(r => {
                    r.classList.remove('is-drag-over');
                });
            });
            div.addEventListener("dragover", (e) => {
                e.preventDefault();
                if (div !== elDragSrc) div.classList.add('is-drag-over');
            });
            div.addEventListener("dragleave", () => {
                div.classList.remove('is-drag-over');
            });
            div.addEventListener("drop", (e) => {
                e.preventDefault();
                if (elDragSrc && elDragSrc !== div) {
                    const rect = div.getBoundingClientRect();
                    const after = e.clientY > rect.top + rect.height / 2;
                    categoriesContainer.insertBefore(elDragSrc, after ? div.nextSibling : div);
                    keepPinnedExpenseRowsAtTop();
                    div.classList.remove('is-drag-over');
                    refreshExpenseLensViews();
                }
            });

            div.appendChild(leftControls);
            div.appendChild(nameInput);
            div.appendChild(dueWrapper);
            div.appendChild(frequencySelect);
            div.appendChild(paymentMethodSelect);
            div.appendChild(amountWrapper);
            div.appendChild(percentSpan);
            div.appendChild(deleteBtn);
            categoriesContainer.appendChild(div);

            if (isDualPanel) {
                fitSingleLineControlText(nameInput, { minSize: 10, maxSize: 14 });
                fitSingleLineControlText(dueInput, { minSize: 10, maxSize: 13 });
                fitSingleLineControlText(frequencySelect, { minSize: 10, maxSize: 13, reserve: 18 });
                fitSingleLineControlText(paymentMethodSelect, { minSize: 10, maxSize: 13, reserve: 18 });
                fitSingleLineControlText(amountInput, { minSize: 10, maxSize: 14, reserve: 24 });
            }

            if (preAmount) refreshExpenseLens();
        };

        // -----------------------------
        // Refresh Function
        // -----------------------------
        const refreshExpenseLens = (options = {}) => {
            const shouldSortRows = !!options.sortRows;
            const incomeSummary = !isBusinessExpenseLens ? syncPersonalIncomeDisplay() : null;
            const income = incomeSummary?.monthlyTotal ?? (+elIncome.value.replace(/,/g,'') || 0);
            let totalSpent = 0;
            let monthlyTotalSpent = 0;
            const categoriesData = [];
            const hasPaymentFilter = elActivePaymentFilter !== 'all';

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
                if (val > 0) { markWithSuffix(markExpense, input); markExpense(pctEl); }
                else { markWithSuffix(markNeutral, input); markNeutral(pctEl); }

                const name = (elById(`CatName${index}`).value || `Category ${index}`).trim();
                const due = elById(`CatDue${index}`)?.value || '';
                const frequency = elGetBillFrequency(index);
                const paymentMethod = elGetBillPaymentMethod(index);
                categoriesData.push({
                    name,
                    amount: monthlyTotal,
                    due,
                    frequency,
                    paymentMethod,
                    isPinned,
                    occurrenceAmount: val,
                    _isPinned: isPinned,
                    _sortValue: income > 0 ? (rowTotal / income) * 100 : rowTotal,
                    _sortOrder: categoriesData.length
                });

                if (!elMatchesPaymentFilter(paymentMethod)) return;
                if (elActiveWeek && occurrenceCount === 0) return;
                totalSpent += rowTotal;
            });

            const pct = income > 0 ? (totalSpent / income * 100) : 0;
            const monthlyRemaining = income - monthlyTotalSpent;

            if (elActiveWeek) {
                elMargin.textContent = hasPaymentFilter
                    ? `${elActiveWeek.label} ${elPaymentFilterLabel(elActivePaymentFilter)}: $${totalSpent.toLocaleString()}`
                    : `${elActiveWeek.label} Due: $${totalSpent.toLocaleString()}`;
                elMargin.classList.remove('is-positive', 'is-negative');
                elMargin.classList.toggle('el-balance-chip-info', hasPaymentFilter);
                elMargin.classList.toggle('el-balance-chip-alert', !hasPaymentFilter);
            } else if (hasPaymentFilter) {
                elMargin.textContent = `${elPaymentFilterLabel(elActivePaymentFilter)} Bills: $${totalSpent.toLocaleString()}`;
                elMargin.classList.remove('is-positive', 'is-negative', 'el-balance-chip-alert');
                elMargin.classList.add('el-balance-chip-info');
            } else {
                elMargin.textContent = `Remaining Balance: $${monthlyRemaining.toLocaleString()}`;
                elApplyBalanceTone(elMargin, monthlyRemaining);
            }

            // Top remaining balance badge — always reflects full-month income vs all monthly bills
            const badge = elById('RemainingBadge');
            if (badge) {
                if (monthlyRemaining >= 0) {
                    badge.textContent = `Remaining: $${monthlyRemaining.toLocaleString()}`;
                } else {
                    badge.textContent = `Remaining: -$${Math.abs(monthlyRemaining).toLocaleString()}`;
                }
                elApplyBalanceTone(badge, monthlyRemaining);
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
            if (!isBusinessExpenseLens && window.LegendFinanceProfile?.update) {
                window.LegendFinanceProfile.update({
                    monthlyNet: income || undefined,
                    fixedExpenses: monthlyTotalSpent || undefined,
                    expenses: categoriesData
                });
            }
            window.dispatchEvent(new CustomEvent(expenseLensUpdatedEvent, {
                detail: {
                    ...(isBusinessExpenseLens ? {} : buildIncomeCompatibilityState()),
                    income,
                    incomeStreams: isBusinessExpenseLens ? undefined : serializeIncomeStreamsForSave(incomeGroupsState),
                    monthlyExpenseTotal: monthlyTotalSpent,
                    monthlyRemaining,
                    expenses: categoriesData
                }
            }));
        };

        // -----------------------------
        // Event Listeners
        // -----------------------------
        if (isBusinessExpenseLens) {
            elIncome.addEventListener("input", refreshExpenseLens);
            elIncome.addEventListener("blur", () => {
                elIncome.value = formatNumber(elIncome.value);
                refreshExpenseLens({ sortRows: true });
            });
        }

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

        const elGetBillFrequency = (index) => {
            const frequencyEl = elById(`CatFrequency${index}`);
            return normalizeBillFrequency(frequencyEl?.value || 'monthly');
        };

        const elGetBillOccurrenceDays = (index, week = null) => {
            const dueEl = elById(`CatDue${index}`);
            const frequency = elGetBillFrequency(index);
            return getScheduledOccurrenceDays(dueEl?.value || '', frequency, {
                ...elMonthContext(),
                week
            });
        };

        const elApplyWeekFilter = (week, options = {}) => {
            elActiveWeek = week ? (elBuildCalendarWeeks().find(candidate => candidate.id === week.id) || week) : null;
            refreshExpenseLensViews({ sortRows: options.sortRows !== false });
        };

        weekPanelBackdrop = document.createElement('div');
        weekPanelBackdrop.className = 'expense-lens-week-panel-backdrop';
        weekPanelBackdrop.classList.add('lf-js-005');

        weekPanel = document.createElement('div');
        weekPanel.className = 'expense-lens-week-panel';
        weekPanel.classList.add('lf-js-006');
        weekPanelBackdrop.appendChild(weekPanel);
        document.body.appendChild(weekPanelBackdrop);

        const hideOtherWeekPanels = () => {
            document.querySelectorAll('.expense-lens-week-panel-backdrop').forEach(panel => {
                if (panel !== weekPanelBackdrop) panel.style.display = 'none';
            });
        };

        const openWeekPanel = () => {
            renderWeekPanel();
            hideOtherWeekPanels();
            elWeekPanelBodyOverflow = document.body.style.overflow || '';
            weekPanelBackdrop.style.display = 'flex';
            document.body.style.overflow = 'hidden';
        };

        const closeWeekPanel = () => {
            weekPanelBackdrop.style.display = 'none';
            document.body.style.overflow = elWeekPanelBodyOverflow;
        };

        const renderWeekPanel = () => {
            const { monthYearLabel } = elMonthContext();
            const weeks = elBuildCalendarWeeks();
            weekPanel.innerHTML = '';
            const personalCashflowMode = !isBusinessExpenseLens;

            const formatBillCount = (count) => `${count} bill${count !== 1 ? 's' : ''}`;
            const formatIncomeHitCount = (count) => `${count} pay hit${count !== 1 ? 's' : ''}`;
            const debitCashLabel = EL_PAYMENT_FILTERS.find(option => option.value === 'debit')?.label || 'Debit / Cash';

            const collectWeekBills = (week = null) => {
                const bills = [];
                categoriesContainer.querySelectorAll(`[id^="${elId('CatRow')}"]`).forEach(row => {
                    const idx = row.id.replace(elId('CatRow'), '');
                    const paymentMethod = elGetBillPaymentMethod(idx);
                    if (!elMatchesPaymentFilter(paymentMethod)) return;

                    const amtEl = elById(`CatAmount${idx}`);
                    const nameEl = elById(`CatName${idx}`);
                    const frequency = elGetBillFrequency(idx);
                    const amt = +(amtEl?.value || '').replace(/,/g, '') || 0;
                    if (amt <= 0) return;

                    const occurrences = elGetBillOccurrenceDays(idx, week);
                    occurrences.forEach(date => {
                        bills.push({
                            name: nameEl?.value?.trim() || '(Unnamed)',
                            amount: amt,
                            date,
                            frequency,
                            paymentMethod
                        });
                    });
                });

                bills.sort((a, b) => (a.date - b.date) || a.name.localeCompare(b.name));
                return bills;
            };

            const collectWeekIncomeHits = (week = null) => personalCashflowMode
                ? summarizePersonalIncomeGroups(incomeGroupsState, { week }).hits
                : [];

            const summarizeBills = (bills) => {
                let total = 0;
                let debitTotal = 0;
                let creditTotal = 0;

                bills.forEach(bill => {
                    total += bill.amount;
                    if (bill.paymentMethod === 'debit') debitTotal += bill.amount;
                    if (bill.paymentMethod === 'credit') creditTotal += bill.amount;
                });

                return {
                    total,
                    debitTotal,
                    creditTotal,
                    count: bills.length
                };
            };

            const buildCashflowSummary = (incomeHits, bills, startingBalance = 0) => {
                const incomeTotal = incomeHits.reduce((sum, hit) => sum + hit.amount, 0);
                const debitCashBills = bills.filter(bill => bill.paymentMethod !== 'credit');
                const creditBills = bills.filter(bill => bill.paymentMethod === 'credit');
                const debitCashTotal = debitCashBills.reduce((sum, bill) => sum + bill.amount, 0);
                const creditTotal = creditBills.reduce((sum, bill) => sum + bill.amount, 0);
                const rawEvents = [
                    ...incomeHits.map((hit) => ({
                        kind: 'income',
                        label: hit.label,
                        date: hit.date,
                        frequency: hit.frequency,
                        amount: hit.amount
                    })),
                    ...bills.map((bill) => ({
                        kind: bill.paymentMethod === 'credit' ? 'credit' : bill.paymentMethod === 'debit' ? 'debit' : 'open',
                        label: bill.frequency === 'monthly' ? bill.name : `${bill.name} (${elFrequencyLabel(bill.frequency)})`,
                        date: bill.date,
                        amount: bill.amount,
                        paymentMethod: bill.paymentMethod
                    }))
                ].sort((a, b) => {
                    const aDay = a.date.getTime();
                    const bDay = b.date.getTime();
                    if (aDay !== bDay) return aDay - bDay;
                    const order = { income: 0, debit: 1, open: 2, credit: 3 };
                    if ((order[a.kind] ?? 9) !== (order[b.kind] ?? 9)) {
                        return (order[a.kind] ?? 9) - (order[b.kind] ?? 9);
                    }
                    return a.label.localeCompare(b.label);
                });

                let runningBalance = startingBalance;
                const events = rawEvents.map((eventItem) => {
                    const impact = eventItem.kind === 'income' ? eventItem.amount : -eventItem.amount;
                    const balanceBefore = runningBalance;
                    runningBalance += impact;
                    return {
                        ...eventItem,
                        impact,
                        balanceBefore,
                        balanceAfter: runningBalance
                    };
                });

                return {
                    incomeTotal,
                    incomeCount: incomeHits.length,
                    debitCashTotal,
                    debitCashCount: debitCashBills.length,
                    creditTotal,
                    creditCount: creditBills.length,
                    totalBills: bills.reduce((sum, bill) => sum + bill.amount, 0),
                    billCount: bills.length,
                    startingBalance,
                    endingBalance: runningBalance,
                    netChange: incomeTotal - bills.reduce((sum, bill) => sum + bill.amount, 0),
                    events
                };
            };

            const createBillSummaryMetrics = (totals) => {
                const metricsWrap = document.createElement('div');
                metricsWrap.classList.add('lf-js-007');
                metricsWrap.appendChild(elCreateWeekMetricChip(EL_PAYMENT_METHOD_META.debit.label, totals.debitTotal, EL_PAYMENT_METHOD_META.debit));
                metricsWrap.appendChild(elCreateWeekMetricChip(EL_PAYMENT_METHOD_META.credit.label, totals.creditTotal, EL_PAYMENT_METHOD_META.credit));
                metricsWrap.appendChild(elCreateWeekMetricChip(EL_PAYMENT_METHOD_META.total.label, totals.total, EL_PAYMENT_METHOD_META.total, totals.count > 0 ? formatBillCount(totals.count) : ''));
                return metricsWrap;
            };

            const createCashflowSummaryMetrics = (summary, options = {}) => {
                const endingMeta = options.endingMeta || EL_ENDING_BALANCE_META;
                const metricsWrap = document.createElement('div');
                metricsWrap.classList.add('lf-js-008');
                metricsWrap.appendChild(elCreateWeekMetricChip(EL_INCOME_METRIC_META.label, summary.incomeTotal, EL_INCOME_METRIC_META, summary.incomeCount > 0 ? formatIncomeHitCount(summary.incomeCount) : ''));
                metricsWrap.appendChild(elCreateWeekMetricChip(debitCashLabel, summary.debitCashTotal, EL_PAYMENT_METHOD_META.debit, summary.debitCashCount > 0 ? formatBillCount(summary.debitCashCount) : ''));
                metricsWrap.appendChild(elCreateWeekMetricChip(EL_PAYMENT_METHOD_META.credit.label, summary.creditTotal, EL_PAYMENT_METHOD_META.credit, summary.creditCount > 0 ? formatBillCount(summary.creditCount) : ''));
                metricsWrap.appendChild(elCreateWeekMetricChip(endingMeta.label, summary.endingBalance, endingMeta, '', { negativeMeta: EL_NEGATIVE_METRIC_META }));
                return metricsWrap;
            };

            const header = document.createElement('div');
            header.classList.add('lf-js-009');
            const titleWrap = document.createElement('div');
            titleWrap.classList.add('lf-js-010');
            const title = document.createElement('span');
            title.classList.add('lf-js-011');
            title.textContent = personalCashflowMode ? 'WEEKLY CASHFLOW TRACKER' : 'WEEKLY BILL TRACKER';
            const subtitle = document.createElement('span');
            subtitle.classList.add('lf-js-012');
            subtitle.textContent = elActivePaymentFilter === 'all'
                ? `Calendar weeks for ${monthYearLabel}`
                : `Calendar weeks for ${monthYearLabel} · ${elPaymentFilterLabel(elActivePaymentFilter)} only`;
            const closeX = document.createElement('span');
            closeX.textContent = '✕';
            closeX.classList.add('lf-js-013');
            closeX.addEventListener('click', (e) => { e.stopPropagation(); closeWeekPanel(); });
            titleWrap.appendChild(title);
            titleWrap.appendChild(subtitle);
            header.appendChild(titleWrap);
            header.appendChild(closeX);
            weekPanel.appendChild(header);

            const allBills = collectWeekBills();
            const allIncomeHits = collectWeekIncomeHits();
            const allTotals = summarizeBills(allBills);
            const allCashflow = buildCashflowSummary(allIncomeHits, allBills);

            const allRow = document.createElement('div');
            allRow.className = 'el-week-summary-row el-week-summary-row--month';
            allRow.classList.toggle('is-selected', !elActiveWeek);

            const allRowLeft = document.createElement('div');
            allRowLeft.classList.add('lf-js-014');
            const allRowLabel = document.createElement('span');
            allRowLabel.className = 'el-week-summary-title';
            allRowLabel.classList.toggle('is-selected', !elActiveWeek);
            allRowLabel.textContent = personalCashflowMode
                ? (elActivePaymentFilter === 'all' ? 'Show Full Cashflow' : `${elPaymentFilterLabel(elActivePaymentFilter)} Cashflow`)
                : (elActivePaymentFilter === 'all' ? 'Show All Bills' : `${elPaymentFilterLabel(elActivePaymentFilter)} Bills`);
            const allRowSub = document.createElement('span');
            allRowSub.className = 'el-week-summary-subtitle el-week-summary-subtitle--month';
            allRowSub.classList.toggle('is-selected', !elActiveWeek);
            allRowSub.textContent = personalCashflowMode
                ? ((allCashflow.incomeCount > 0 || allCashflow.billCount > 0)
                    ? `Entire ${monthYearLabel} income + bill map`
                    : 'No income or bill events scheduled in the current month')
                : (allTotals.count > 0
                    ? `Entire ${monthYearLabel} payment map`
                    : 'No payments scheduled in the current month');
            allRowLeft.appendChild(allRowLabel);
            allRowLeft.appendChild(allRowSub);
            allRow.appendChild(allRowLeft);
            allRow.appendChild(personalCashflowMode ? createCashflowSummaryMetrics(allCashflow, { endingMeta: EL_MONTH_END_BALANCE_META }) : createBillSummaryMetrics(allTotals));
            allRow.addEventListener('click', (e) => { e.stopPropagation(); elExpandedWeek = null; elApplyWeekFilter(null); });
            weekPanel.appendChild(allRow);

            let runningBalance = 0;
            weeks.forEach(week => {
                const bills = collectWeekBills(week);
                const totals = summarizeBills(bills);
                const incomeHits = collectWeekIncomeHits(week);
                const cashflowSummary = buildCashflowSummary(incomeHits, bills, runningBalance);
                runningBalance = cashflowSummary.endingBalance;
                const isActive = elSameCalendarWeek(elActiveWeek, week);
                const isExpanded = elSameCalendarWeek(elExpandedWeek, week);

                const weekBlock = document.createElement('div');
                weekBlock.classList.add('lf-js-015');

                const summaryRow = document.createElement('div');
                summaryRow.className = 'el-week-summary-row el-week-summary-row--week';
                summaryRow.classList.toggle('is-selected', isActive);

                const wLabelWrap = document.createElement('div');
                wLabelWrap.classList.add('lf-js-014');
                const wLabel = document.createElement('span');
                wLabel.className = 'el-week-summary-title el-week-summary-title--week';
                wLabel.classList.toggle('is-selected', isActive);
                wLabel.textContent = week.label;
                const wRange = document.createElement('span');
                wRange.className = 'el-week-summary-subtitle el-week-summary-subtitle--week';
                wRange.classList.toggle('is-selected', isActive);
                wRange.textContent = week.rangeLabel;
                wLabelWrap.appendChild(wLabel);
                wLabelWrap.appendChild(wRange);

                const rightGroup = document.createElement('div');
                rightGroup.classList.add('lf-js-016');
                rightGroup.appendChild(personalCashflowMode ? createCashflowSummaryMetrics(cashflowSummary) : createBillSummaryMetrics(totals));

                const chevron = document.createElement('span');
                chevron.textContent = isExpanded ? '▴' : '▾';
                chevron.classList.add('lf-js-017');
                rightGroup.appendChild(chevron);

                summaryRow.appendChild(wLabelWrap);
                summaryRow.appendChild(rightGroup);

                const detailWrap = document.createElement('div');
                detailWrap.className = 'el-week-detail';
                detailWrap.classList.toggle('is-open', isExpanded);

                if (personalCashflowMode && cashflowSummary.events.length > 0) {
                    const detailBanner = document.createElement('div');
                    detailBanner.classList.add('lf-js-018');

                    const detailHint = document.createElement('span');
                    detailHint.classList.add('lf-js-019');
                    detailHint.textContent = 'Running order: pay hits post first, then debit / cash obligations, then credit due in that week.';

                    const detailCarry = document.createElement('div');
                    detailCarry.classList.add('lf-js-020');

                    const startGroup = document.createElement('span');
                    startGroup.classList.add('lf-js-021');
                    startGroup.appendChild(document.createTextNode('Start'));
                    startGroup.appendChild(elCreateBalanceValue(cashflowSummary.startingBalance, '0.74rem'));

                    const divider = document.createElement('span');
                    divider.classList.add('lf-js-022');
                    divider.textContent = '•';

                    const endGroup = document.createElement('span');
                    endGroup.classList.add('lf-js-023');
                    endGroup.appendChild(document.createTextNode('Week End'));
                    endGroup.appendChild(elCreateBalanceValue(cashflowSummary.endingBalance, '0.74rem'));

                    detailCarry.appendChild(startGroup);
                    detailCarry.appendChild(divider);
                    detailCarry.appendChild(endGroup);

                    detailBanner.appendChild(detailHint);
                    detailBanner.appendChild(detailCarry);
                    detailWrap.appendChild(detailBanner);

                    const colHeader = document.createElement('div');
                    colHeader.classList.add('lf-js-024');
                    colHeader.innerHTML = '<span class="lf-ui-024">Event</span><span class="lf-ui-025">Date</span><span class="lf-ui-025">Type</span><span class="lf-ui-026">Impact</span><span class="lf-ui-026">Running Bal</span>';
                    detailWrap.appendChild(colHeader);

                    cashflowSummary.events.forEach((eventItem, i) => {
                        const eventRow = document.createElement('div');
                        eventRow.className = 'el-week-event-row';
                        eventRow.classList.toggle('is-last', i === cashflowSummary.events.length - 1);

                        const eventName = document.createElement('span');
                        eventName.classList.add('lf-js-025');
                        eventName.textContent = eventItem.kind === 'income' && eventItem.frequency
                            ? `${eventItem.label} (${elFrequencyLabel(eventItem.frequency)})`
                            : eventItem.label;

                        const eventDate = document.createElement('span');
                        eventDate.classList.add('lf-js-026');
                        eventDate.textContent = eventItem.date.toLocaleString('default', { month: 'short', day: 'numeric' });

                        const eventType = document.createElement('span');
                        eventType.classList.add('lf-js-027');
                        if (eventItem.kind === 'income') {
                            eventType.appendChild(elCreateToneBadge(EL_INCOME_METRIC_META.label, EL_INCOME_METRIC_META));
                        } else if (eventItem.kind === 'credit') {
                            eventType.appendChild(elCreatePaymentBadge('credit'));
                        } else if (eventItem.kind === 'debit') {
                            eventType.appendChild(elCreatePaymentBadge('debit'));
                        } else {
                            eventType.appendChild(elCreatePaymentBadge(''));
                        }

                        const amountMeta = eventItem.kind === 'income'
                            ? EL_INCOME_METRIC_META
                            : eventItem.kind === 'credit'
                                ? EL_PAYMENT_METHOD_META.credit
                                : eventItem.kind === 'debit'
                                    ? EL_PAYMENT_METHOD_META.debit
                                    : EL_PAYMENT_METHOD_META.unassigned;
                        const eventAmount = document.createElement('span');
                        eventAmount.className = `el-week-amount ${elResolveMetricToneClass(amountMeta)}`;
                        eventAmount.textContent = `${eventItem.impact < 0 ? '-$' : '+$'}${Math.abs(eventItem.impact).toLocaleString()}`;

                        const eventBalance = document.createElement('span');
                        eventBalance.className = 'el-week-balance';
                        eventBalance.classList.add(eventItem.balanceAfter < 0 ? 'is-negative' : 'is-positive');
                        eventBalance.textContent = elFormatCashflowCurrency(eventItem.balanceAfter);

                        eventRow.appendChild(eventName);
                        eventRow.appendChild(eventDate);
                        eventRow.appendChild(eventType);
                        eventRow.appendChild(eventAmount);
                        eventRow.appendChild(eventBalance);
                        detailWrap.appendChild(eventRow);
                    });
                } else if (totals.count > 0) {
                    const colHeader = document.createElement('div');
                    colHeader.classList.add('lf-js-028');
                    colHeader.innerHTML = '<span class="lf-ui-027">Bill</span><span class="lf-ui-028">Due</span><span class="lf-ui-029">Pay Type</span><span class="lf-ui-030">Amount</span>';
                    detailWrap.appendChild(colHeader);

                    bills.forEach((bill, i) => {
                        const paymentMeta = elGetPaymentMethodMeta(bill.paymentMethod);
                        const billRow = document.createElement('div');
                        billRow.className = 'el-week-bill-row';
                        billRow.classList.toggle('is-last', i === bills.length - 1);

                        const bName = document.createElement('span');
                        bName.classList.add('lf-js-029');
                        bName.textContent = bill.frequency === 'monthly'
                            ? bill.name
                            : `${bill.name} (${elFrequencyLabel(bill.frequency)})`;

                        const bDue = document.createElement('span');
                        bDue.classList.add('lf-js-030');
                        bDue.textContent = bill.date.toLocaleString('default', { month: 'short', day: 'numeric' });

                        const paymentCell = document.createElement('span');
                        paymentCell.classList.add('lf-js-031');
                        paymentCell.appendChild(elCreatePaymentBadge(bill.paymentMethod));

                        const bAmt = document.createElement('span');
                        bAmt.className = `el-week-amount ${elResolveMetricToneClass(paymentMeta)}`;
                        bAmt.textContent = `$${bill.amount.toLocaleString()}`;

                        billRow.appendChild(bName);
                        billRow.appendChild(bDue);
                        billRow.appendChild(paymentCell);
                        billRow.appendChild(bAmt);
                        detailWrap.appendChild(billRow);
                    });
                } else {
                    const empty = document.createElement('div');
                    if (personalCashflowMode && elActivePaymentFilter === 'all') {
                        empty.classList.add('lf-js-032');

                        const emptyCopy = document.createElement('span');
                        emptyCopy.classList.add('lf-js-033');
                        emptyCopy.textContent = 'No income or bill events scheduled in this week. Ending balance carries at';

                        const emptyBalance = document.createElement('span');
                        emptyBalance.classList.add('lf-js-034');
                        emptyBalance.textContent = elFormatCashflowCurrency(cashflowSummary.endingBalance);
                        elApplyBalanceTone(emptyBalance, cashflowSummary.endingBalance);

                        empty.appendChild(emptyCopy);
                        empty.appendChild(emptyBalance);
                    } else {
                        empty.classList.add('lf-js-035');
                        empty.textContent = personalCashflowMode
                            ? `No ${elPaymentFilterLabel(elActivePaymentFilter).toLowerCase()} bill events scheduled in this week.`
                            : (elActivePaymentFilter === 'all'
                                ? 'No bills with due dates set for this week.'
                                : `No ${elPaymentFilterLabel(elActivePaymentFilter).toLowerCase()} bills with due dates set for this week.`);
                    }
                    detailWrap.appendChild(empty);
                }

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
        weeklyBtn = document.createElement('button');
        weeklyBtn.type = 'button';
        weeklyBtn.textContent = 'Weekly ▾';
        weeklyBtn.className = 'btn el-week-btn';
        weeklyBtn.classList.add('lf-js-036');
        weeklyBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            const isOpen = weekPanelBackdrop.style.display !== 'none';
            if (isOpen) { closeWeekPanel(); return; }
            openWeekPanel();
        });
        weekPanel.addEventListener('click', e => e.stopPropagation());
        weekPanelBackdrop.addEventListener('click', e => e.stopPropagation());

        paymentFilterSelect = document.createElement('select');
        paymentFilterSelect.id = elId('PaymentFilter');
        paymentFilterSelect.className = 'form-select el-filter-select';
        paymentFilterSelect.title = 'Filter bills by payment method';
        paymentFilterSelect.classList.add('lf-js-037');
        EL_PAYMENT_FILTERS.forEach(option => {
            const opt = document.createElement('option');
            opt.value = option.value;
            opt.textContent = option.label;
            paymentFilterSelect.appendChild(opt);
        });
        paymentFilterSelect.value = elActivePaymentFilter;
        paymentFilterSelect.addEventListener('change', () => {
            elActivePaymentFilter = EL_PAYMENT_FILTERS.some(option => option.value === paymentFilterSelect.value)
                ? paymentFilterSelect.value
                : 'all';
            elExpandedWeek = null;
            refreshExpenseLensViews({ sortRows: true });
        });

        (elActionMeta || addBtn.parentElement).appendChild(paymentFilterSelect);
        (elActionMeta || addBtn.parentElement).appendChild(weeklyBtn);

        // Second Weekly button — placed to the right of the Total Monthly Income input for quick top-of-page access
        const weeklyBtnTop = document.createElement('button');
        weeklyBtnTop.id = elId('WeeklyBtnTop');
        weeklyBtnTop.type = 'button';
        weeklyBtnTop.textContent = 'Weekly ▾';
        weeklyBtnTop.className = 'btn el-week-btn';
        weeklyBtnTop.classList.add('lf-js-036');
        weeklyBtnTop.addEventListener('click', (e) => {
            e.stopPropagation();
            const isOpen = weekPanelBackdrop.style.display !== 'none';
            if (isOpen) { closeWeekPanel(); return; }
            openWeekPanel();
        });
        // Wrap the income input row in a flex container so the button sits cleanly to the right.
        // Remove mb-3 from the input (it adds margin-bottom inside the wrapper causing height mismatch).
        elIncome.classList.remove('mb-3');
        const incomeInputRow = elIncome.parentElement;
        const incomeFlexWrap = document.createElement('div');
        incomeFlexWrap.className = 'el-income-flex';
        incomeInputRow.classList.add('el-income-input-inline');
        incomeInputRow.parentElement.insertBefore(incomeFlexWrap, incomeInputRow);
        incomeFlexWrap.appendChild(incomeInputRow);

        // Remaining balance badge — live read of monthly income minus all monthly bills
        const elRemainingBadge = document.createElement('div');
        elRemainingBadge.id = elId('RemainingBadge');
        elRemainingBadge.className = 'el-balance-chip el-balance-chip-muted';
        elRemainingBadge.textContent = 'Remaining: $0';
        incomeFlexWrap.appendChild(elRemainingBadge);
        incomeFlexWrap.appendChild(weeklyBtnTop);

        if (!isBusinessExpenseLens) {
            incomeGroupsHost = document.createElement('div');
            incomeGroupsHost.id = elId('IncomeGroups');
            incomeGroupsHost.className = 'el-income-groups';
            incomeFlexWrap.parentElement.insertBefore(incomeGroupsHost, incomeFlexWrap.nextSibling);

            elIncome.readOnly = true;
            elIncome.classList.add('el-income-total-input--locked');
            renderPersonalIncomeGroups();
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

        // Apply shared profile updates when fields are empty
        const applyProfileToExpenseLens = () => {
            if (isBusinessExpenseLens) return;
            const prof = window.LegendFinanceProfile?.get?.();
            if (!prof) return;

            let didMutate = false;
            const profileIncome = prof.monthlyNet || prof.monthlyGross || '';
            const summary = syncPersonalIncomeDisplay();
            if (summary.monthlyTotal <= 0 && parseSavingsMoney(profileIncome) > 0 && incomeGroupDefinitions[0]) {
                const primaryGroupKey = incomeGroupDefinitions[0].key;
                const primaryStream = incomeGroupsState[primaryGroupKey]?.[0];
                if (primaryStream) {
                    primaryStream.amount = profileIncome;
                    renderPersonalIncomeGroups();
                    didMutate = true;
                }
            }

            const rows = Array.from(categoriesContainer.querySelectorAll(`[id^="${elId('CatRow')}"]`));
            const allBlank = rows.length === 0 || rows.every(r => {
                const n = r.querySelector(`[id^="${elId('CatName')}"]`)?.value?.trim() || '';
                const a = r.querySelector(`[id^="${elId('CatAmount')}"]`)?.value?.trim() || '';
                return !n && !a;
            });
            if (allBlank) {
                categoriesContainer.innerHTML = '';
                categoryCount = 0;
                if (Array.isArray(prof.expenses) && prof.expenses.length) {
                    prof.expenses.forEach(exp => {
                        const amt = exp?.occurrenceAmount ?? exp?.amount ?? '';
                        createCategoryRow(++categoryCount, exp?.name || `Expense ${categoryCount}`, exp?.due || '', amt, exp?.frequency || exp?.recurrence, exp?.paymentMethod || '', false, exp?.isPinned === true);
                    });
                } else {
                    createCategoryRow(++categoryCount);
                }
                didMutate = true;
            }

            if (didMutate) {
                refreshExpenseLens({ sortRows: true });
            }
        };

        toolContext.onWindow("FinanceProfile:updated", applyProfileToExpenseLens);
        toolContext.onWindow("FinanceProfile:ready", applyProfileToExpenseLens);
        applyProfileToExpenseLens();

        // ✅ Color engine (no refresh needed)
        const applyExpenseLensColors = () => {
            // Inputs
            markWithSuffix(markIncome, elIncome);

            container.querySelectorAll('.el-income-group-label').forEach(markGold);
            container.querySelectorAll('.el-income-total').forEach((node) => {
                const value = parseSavingsMoney(node.textContent || '');
                if (value > 0) markIncome(node);
                else markNeutral(node);
            });
            container.querySelectorAll('.el-income-share').forEach(markNeutral);
            container.querySelectorAll('.el-stream-tag').forEach(markNeutral);
            container.querySelectorAll('.el-stream-card .legend-money-field').forEach((field) => {
                const value = parseSavingsMoney(field.value || field.textContent || '');
                if (value > 0) markWithSuffix(markIncome, field);
                else markWithSuffix(markNeutral, field);
            });
            container.querySelectorAll('.el-stream-card select').forEach(markNeutral);
            container.querySelectorAll('.el-stream-card input[type="date"]').forEach(markNeutral);

            // Rows (dynamic)
            categoriesContainer.querySelectorAll(`[id^="${elId('CatName')}"]`).forEach(n => markNeutral(n));     // labels
            categoriesContainer.querySelectorAll(`[id^="${elId('CatFrequency')}"]`).forEach(f => markNeutral(f)); // frequency
            categoriesContainer.querySelectorAll(`[id^="${elId('CatPaymentMethod')}"]`).forEach(p => markNeutral(p)); // payment method
            categoriesContainer.querySelectorAll(`[id^="${elId('CatAmount')}"]`).forEach(a => {
                const value = parseSavingsMoney(a.value || '');
                if (value > 0) markWithSuffix(markExpense, a);
                else markWithSuffix(markNeutral, a);
            });
            categoriesContainer.querySelectorAll(`[id^="${elId('Out')}"]`).forEach(p => markExpense(p));        // % outputs
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
  <div class="networth-tool p-4 legend-finance-tool-card legend-finance-tool-card--wide el-shell">

        <div id="nwTipLayer"></div>
      
        <h3>
            ${t.name}
        </h3>

        <p>
            Track your total assets, liabilities, and net worth. See insights to grow your wealth.
        </p>

        <div class="ft-sync-grid">
            <div class="ft-sync-card">
                <div class="el-label">
                    Total Assets
                    <span class="el-i nw-i" tabindex="0"
                          data-tip="<b>Examples:</b> cash, investments, retirement accounts, property value (total value)">i</span>
                </div>
                <div class="legend-money-input">
                    <span class="legend-money-prefix">$</span>
                    <input id="assets" type="text" class="legend-money-field" readonly placeholder="Sync from Financial Health Snapshot…" />
                </div>
            </div>
            <div class="ft-sync-card">
                <div class="el-label">
                    Total Liabilities
                    <span class="el-i nw-i" tabindex="0"
                          data-tip="<b>Examples:</b> credit cards, loans, mortgage balance, any debts owed (total)">i</span>
                </div>
                <div class="legend-money-input">
                    <span class="legend-money-prefix">$</span>
                    <input id="liabs" type="text" class="legend-money-field" readonly placeholder="Sync from Financial Health Snapshot…" />
                </div>
            </div>
        </div>

        <div class="ft-kpi-grid">
            <dl class="ft-kpi-card">
                <dt>Assets</dt>
                <dd id="aVal">$0</dd>
            </dl>
            <dl class="ft-kpi-card">
                <dt>Liabilities</dt>
                <dd id="lVal">$0</dd>
            </dl>
            <dl class="ft-kpi-card">
                <dt>Net Worth</dt>
                <dd id="nVal">$0</dd>
            </dl>
        </div>

        <div class="ft-panel-stack">
            <div class="ft-panel-row">
                <div class="ft-panel-label">Net Worth to Assets Ratio</div>
                <div class="ft-panel-value" id="nwRatio">0%</div>
            </div>
            <div class="ft-panel-row">
                <div class="ft-panel-label">Liabilities to Assets Ratio</div>
                <div class="ft-panel-value" id="liabRatio">0%</div>
            </div>
            <div class="ft-panel-row">
                <div class="ft-panel-label">Wealth Status</div>
                <div class="ft-panel-value" id="wealthStatus">—</div>
            </div>
        </div>

        <div class="el-tip-strip" id="nwTips">
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
        const hasSourceData = hasNonBlankValue(assets.value) || hasNonBlankValue(liabs.value);
        if (!hasSourceData) {
            aVal.textContent = lVal.textContent = nVal.textContent = '$0';
            nwRatio.textContent = liabRatio.textContent = '0%';
            wealthStatus.textContent = '—';
            nwTips.textContent = 'Enter your assets and liabilities to get personalized insights.';
            markGold(aVal);
            markGold(lVal);
            markGold(nVal);
            markGold(nwRatio);
            markGold(liabRatio);
            markGold(wealthStatus);
            markGold(nwTips);
            saveToolState('NetWorth');
            return;
        }

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
   <div class="networth-tool p-4 legend-finance-tool-card legend-finance-tool-card--wide el-shell">

        <div id="cfTipLayer"></div>
       
        <h3>
            ${t.name}
        </h3>

        <p>
            Understand your monthly cash flow and uncover opportunities to save or invest.
        </p>

        <div class="ft-sync-grid">
            <div class="ft-sync-card">
                <div class="el-label">
                    Monthly Income
                    <span class="el-i cf-i" tabindex="0"
                          data-tip="<b>Examples:</b> 5,000 • 7,200 (total monthly take-home or reliable monthly income)">i</span>
                </div>
                <div class="legend-money-input">
                    <span class="legend-money-prefix">$</span>
                    <input id="cfIncome" type="text" class="legend-money-field" readonly placeholder="Sync from Expense Lens…" />
                </div>
            </div>
            <div class="ft-sync-card">
                <div class="el-label">
                    Monthly Bills
                    <span class="el-i cf-i" tabindex="0"
                          data-tip="<b>Examples:</b> 2,500 • 3,900 (fixed bills + minimum payments + essentials)">i</span>
                </div>
                <div class="legend-money-input">
                    <span class="legend-money-prefix">$</span>
                    <input id="cfBills" type="text" class="legend-money-field" readonly placeholder="Sync from Expense Lens…" />
                </div>
            </div>
        </div>

        <div class="ft-kpi-grid">
            <dl class="ft-kpi-card">
                <dt>Net Cash Flow</dt>
                <dd id="cfResult">$0</dd>
            </dl>
            <dl class="ft-kpi-card">
                <dt>Savings Potential</dt>
                <dd id="cfSavingsPotential">$0</dd>
            </dl>
            <dl class="ft-kpi-card">
                <dt>Suggested Allocation</dt>
                <dd id="cfInvestPct">0%</dd>
            </dl>
        </div>

        <div class="el-tip-strip" id="cfTips">
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
        const hasSourceData = hasNonBlankValue(cfIncome.value) || hasNonBlankValue(cfBills.value);
        if (!hasSourceData) {
            cfResult.textContent = '$0';
            cfSavingsPotential.textContent = '$0';
            cfInvestPct.textContent = '0%';
            cfTips.textContent = 'Enter your monthly income and bills to get personalized tips.';
            markGold(cfResult);
            markGold(cfSavingsPotential);
            markGold(cfInvestPct);
            markGold(cfTips);
            saveToolState('CashFlow');
            return;
        }

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
   <div class="networth-tool p-4 legend-finance-tool-card legend-finance-tool-card--wide el-shell">

        <div id="dcTipLayer"></div>
       
        <h3>
            ${t.name}
        </h3>

        <p>
            Quickly calculate your Debt-to-Income (DTI) ratio and get actionable guidance.
        </p>

        <div class="ft-sync-grid">
            <div class="ft-sync-card">
                <div class="el-label">
                    Total Liabilities
                    <span class="el-i dc-i" tabindex="0"
                          data-tip="<b>Examples:</b> 40,000 • 75,000 (total debts owed: loans, cards, etc.)">i</span>
                </div>
                <div class="legend-money-input">
                    <span class="legend-money-prefix">$</span>
                    <input id="dcDebt" type="text" class="legend-money-field" readonly placeholder="Sync from Financial Health Snapshot…" />
                </div>
            </div>
            <div class="ft-sync-card">
                <div class="el-label">
                    Annual Income
                    <span class="el-i dc-i" tabindex="0"
                          data-tip="<b>Examples:</b> 60,000 • 80,000 (gross annual income)">i</span>
                </div>
                <div class="legend-money-input">
                    <span class="legend-money-prefix">$</span>
                    <input id="dcIncome" type="text" class="legend-money-field" readonly placeholder="Sync from Expense Lens…" />
                </div>
            </div>
        </div>

        <div class="ft-kpi-grid ft-kpi-grid--two">
            <dl class="ft-kpi-card">
                <dt>DTI Ratio</dt>
                <dd id="dcResult">0%</dd>
            </dl>
            <dl class="ft-kpi-card">
                <dt>Status</dt>
                <dd id="dcStatus">—</dd>
            </dl>
        </div>

        <div class="ft-panel-stack">
            <div class="ft-panel-row">
                <div class="ft-panel-label">Recommendation</div>
                <div class="ft-panel-value ft-panel-value--tip" id="dcTips">Enter your liabilities and income to receive guidance.</div>
            </div>
        </div>
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
        dcResult.textContent = '—';
        dcStatus.textContent = '—';
        dcTips.textContent = 'Enter your liabilities and income to receive guidance.';
        clearToolState('DebtClarity');
        hideTip();
        applyLLBSToDebtClarity();
        applyExpenseLensToDebtClarity();
    });

    function calcDebtClarity() {
        const hasDebt = hasNonBlankValue(dcDebt.value);
        const hasIncome = hasNonBlankValue(dcIncome.value);
        if (!hasDebt && !hasIncome) {
            dcResult.textContent = '—';
            dcStatus.textContent = '—';
            dcTips.textContent = 'Enter your liabilities and income to receive guidance.';
            markGold(dcResult);
            markGold(dcStatus);
            markGold(dcTips);
            saveToolState('DebtClarity');
            return;
        }

        if (!hasIncome) {
            dcResult.textContent = '—';
            dcStatus.textContent = '⚠️ Missing Income';
            dcTips.textContent = 'Complete Expense Lens so Debt Clarity can compare liabilities against annual income accurately.';
            markGold(dcResult);
            markGold(dcStatus);
            markGold(dcTips);
            saveToolState('DebtClarity');
            return;
        }

        if (!hasDebt) {
            dcResult.textContent = '—';
            dcStatus.textContent = '⚠️ Missing Liabilities';
            dcTips.textContent = 'Complete Financial Health Snapshot so Debt Clarity can compare your liabilities against income.';
            markGold(dcResult);
            markGold(dcStatus);
            markGold(dcTips);
            saveToolState('DebtClarity');
            return;
        }

        const debt = +dcDebt.value.replace(/,/g,'') || 0;
        const income = +dcIncome.value.replace(/,/g,'') || 0;
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
    <div class="networth-tool p-4 legend-finance-tool-card legend-finance-tool-card--narrow el-shell">

        <div id="fbTipLayer"></div>

        <h3>
            ${t.name}
        </h3>

        <p>
            Build a financial safety net to protect yourself from unexpected expenses.
        </p>

        <div class="ft-sync-grid ft-sync-grid--single">
            <div class="ft-sync-card">
                <div class="el-label">
                    Monthly Bills
                    <span class="el-i fb-i" tabindex="0"
                          data-tip="<b>Examples:</b> 2,500 • 3,800 (rent/mortgage, utilities, insurance, minimum debt payments, essentials)">i</span>
                </div>
                <div class="legend-money-input">
                    <span class="legend-money-prefix">$</span>
                    <input id="fbBills" type="text" class="legend-money-field" readonly placeholder="Sync from Expense Lens…" />
                </div>
            </div>
        </div>

        <div class="ft-goal-stack">
            <div class="ft-goal-row">
                <div class="ft-goal-label">1 Month Goal</div>
                <div class="ft-goal-value" id="fb1">$0</div>
            </div>
            <div class="ft-goal-row">
                <div class="ft-goal-label">3–6 Month Goal</div>
                <div class="ft-goal-value" id="fb3">$0</div>
            </div>
            <div class="ft-goal-row">
                <div class="ft-goal-label">12 Month Goal</div>
                <div class="ft-goal-value" id="fb12">$0</div>
            </div>
        </div>

        <div class="el-tip-strip" id="fbTips">
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
    <div class="networth-tool p-4 legend-finance-tool-card legend-finance-tool-card--narrow el-shell">

        <div id="wpTipLayer"></div>

        <h3 class="lf-ui-042">
            ${t.name}
        </h3>

        <p class="lf-ui-043">
            Project your net worth growth based on current savings and surplus. Visualize both short and long-term potential.
        </p>

        <div class="wp-label">
            Current Net Worth
            <span class="wp-i" tabindex="0"
                  data-tip="<b>Auto-synced:</b> Pulls your live net worth from Financial Health Snapshot.">i</span>
        </div>
        <input id="wpNet" type="text" class="form-control mb-2 lf-ui-047" placeholder="Syncs from Financial Health Snapshot..."
               readonly aria-readonly="true"
               />

        <div class="wp-label">
            Monthly Surplus
            <span class="wp-i" tabindex="0"
                  data-tip="<b>Auto-synced:</b> Pulls the Remaining Balance from the top of Expense Lens.">i</span>
        </div>
        <input id="wpSurplus" type="text" class="form-control mb-2 lf-ui-047" placeholder="Syncs from Expense Lens Remaining Balance..."
               readonly aria-readonly="true"
               />

        <div class="wp-label">
            Custom Months
            <span class="wp-i" tabindex="0"
                  data-tip="<b>Examples:</b> 18 • 24 • 60 (how far out you want to project)">i</span>
        </div>
        <input id="wpMonths" type="number" class="form-control mb-3 lf-ui-048" placeholder="e.g., 18"
               />

        <div class="lf-ui-049">
            <h5 class="lf-ui-050">
                Projected Net Worth (Custom Months):
                <span class="lf-ui-051" id="wpOut">$0</span>
            </h5>
            <h6 class="lf-ui-052">
                Projection in 6 Months:
                <span class="lf-ui-053" id="wp6">$0</span>
            </h6>
            <h6>
                Projection in 12 Months:
                <span class="lf-ui-053" id="wp12">$0</span>
            </h6>
        </div>

        <div class="lf-ui-023" id="wpTips"
            >
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
        hasSyncedNetWorth = hasNonBlankValue(rawNetWorth);
        wpNet.value = hasSyncedNetWorth ? netWorth.toLocaleString() : '';
        updateWealthProjection();
    };

    const applyExpenseLensToWealthProjection = async (event) => {
        const state = event?.detail || await loadPersistedState('ExpenseLens');
        const remaining = calculateExpenseLensMonthlyRemaining(state);
        hasSyncedSurplus = !!state && hasExpenseLensFinancialData(state);
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
    <div class="networth-tool p-4 legend-finance-tool-card legend-finance-tool-card--narrow el-shell">

        <div id="fiTipLayer"></div>

        <h3 class="lf-ui-042">
            ${t.name}
        </h3>

        <p class="lf-ui-043">
            Measure your financial freedom: how long you could live off your net worth and passive income.
        </p>

        <div class="fi-label">
            Net Worth
            <span class="fi-i" tabindex="0"
                  data-tip="<b>What to enter:</b> Assets minus liabilities today. <b>Example:</b> 150,000">i</span>
        </div>
        <input id="fiNet" type="text" class="form-control mb-2 lf-ui-054" readonly placeholder="Sync from Financial Health Snapshot…"
               />

        <div class="fi-label">
            Annual Expenses
            <span class="fi-i" tabindex="0"
                  data-tip="<b>What to enter:</b> Your yearly cost of living. <b>Example:</b> 50,000 (≈ 4,167/mo)">i</span>
        </div>
        <input id="fiExp" type="text" class="form-control mb-2 lf-ui-054" readonly placeholder="Sync from Expense Lens…"
               />

        <div class="fi-label">
            Passive Income
            <span class="fi-i" tabindex="0"
                  data-tip="<b>Optional:</b> Annual passive income (rent, dividends, etc.). <b>Example:</b> 10,000">i</span>
        </div>
        <input id="fiPassive" type="text" class="form-control mb-3 lf-ui-048" placeholder="e.g., 10,000"
               />

        <h5 class="lf-ui-055">
            Freedom Index: <span class="lf-ui-051" id="fiOut">0</span>
        </h5>

        <table class="table mt-3 lf-ui-056">
            <tr><th class="lf-ui-057">Net Worth</th><td id="fiNetOut">$0</td></tr>
            <tr><th class="lf-ui-058">Annual Expenses</th><td id="fiExpOut">$0</td></tr>
            <tr><th class="lf-ui-058">Passive Income</th><td id="fiPassiveOut">$0</td></tr>
            <tr><th class="lf-ui-058">Months of Freedom</th><td id="fiMonths">0</td></tr>
        </table>

        <div class="lf-ui-023" id="fiAdvice"
            >
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
        fiOut.textContent = '—';
        fiNetOut.textContent = fiExpOut.textContent = fiPassiveOut.textContent = '$0';
        fiMonths.textContent = '—';
        fiAdvice.textContent = 'Enter your values to see recommendations.';
        clearPersistedState('FreedomIndex');
        hideTip();
        applyLLBSToFreedomIndex();
        applyExpenseLensToFreedomIndex();
    });

    const updateFreedom = () => {
        const net = parseNumber(fiNet.value);
        const expRaw = parseNumber(fiExp.value);
        const passive = parseNumber(fiPassive.value);
        const hasNetValue = hasNonBlankValue(fiNet.value);
        const hasExpenseValue = hasNonBlankValue(fiExp.value);

        fiNetOut.textContent = `$${formatWithCommas(net)}`;
        fiExpOut.textContent = `$${formatWithCommas(expRaw)}`;
        fiPassiveOut.textContent = `$${formatWithCommas(passive)}`;

        if (!hasNetValue || !hasExpenseValue) {
            fiOut.textContent = '—';
            fiMonths.textContent = '—';

            if (!hasNetValue && !hasExpenseValue) {
                fiAdvice.textContent = '⚠️ Complete Financial Health Snapshot and Expense Lens to calculate your Freedom Index.';
            } else if (!hasNetValue) {
                fiAdvice.textContent = '⚠️ Complete Financial Health Snapshot to sync your net worth here.';
            } else {
                fiAdvice.textContent = '⚠️ Complete Expense Lens to sync your annual expenses here.';
            }

            if (hasNetValue) markIncome(fiNetOut);
            else markGold(fiNetOut);
            if (hasExpenseValue) markExpense(fiExpOut);
            else markGold(fiExpOut);
            if (passive > 0) markIncome(fiPassiveOut);
            else if (passive < 0) markExpense(fiPassiveOut);
            else markGold(fiPassiveOut);
            if (passive > 0) markIncome(fiPassive);
            else if (passive < 0) markExpense(fiPassive);
            else markNeutral(fiPassive);
            markGold(fiOut);
            markGold(fiMonths);
            markGold(fiAdvice);
            saveFI();
            return;
        }

        const fi = (net / expRaw);
        fiOut.textContent = fi.toFixed(1);

        const months = Math.floor(((net + passive * 12) / expRaw) * 12);
        fiMonths.textContent = months;

        let advice = '';
        if (fi < 3) advice = '⚠️ Urgent: Increase savings and reduce expenses immediately.';
        else if (fi < 5) advice = 'Moderate: Keep growing assets, manage expenses wisely.';
        else if (fi < 7) advice = '✅ Good: You have partial financial freedom; keep building passive income.';
        else advice = '🌟 Excellent: Approaching full financial independence! Consider early investment opportunities.';

        fiAdvice.textContent = advice;

        applyFreedomColors(net, expRaw, passive, fi, months);
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
    <div class="networth-tool p-4 legend-finance-tool-card legend-finance-tool-card--narrow el-shell">

        <div id="dapTipLayer"></div>

        <h3 class="lf-ui-042">
            ${t.name}
        </h3>

        <p class="lf-ui-043">
            Evaluate your financial health by comparing assets to liabilities and assess your risk.
        </p>

        <div class="dap-label">
            Total Assets
            <span class="dap-i" tabindex="0"
                  data-tip="<b>Examples:</b> 100,000 • 250,000 (cash, investments, retirement, property, etc.)">i</span>
        </div>
        <input id="dapA" type="text" class="form-control mb-2 lf-ui-054" readonly placeholder="Sync from Financial Health Snapshot…"
               />

        <div class="dap-label">
            Total Liabilities
            <span class="dap-i" tabindex="0"
                  data-tip="<b>Examples:</b> 50,000 • 180,000 (credit cards, loans, mortgage balance, etc.)">i</span>
        </div>
        <input id="dapL" type="text" class="form-control mb-2 lf-ui-054" readonly placeholder="Sync from Financial Health Snapshot…"
               />

        <div class="dap-label">
            Monthly Income
            <span class="dap-i" tabindex="0"
                  data-tip="<b>Optional:</b> Monthly income helps estimate how fast you could crush liabilities. <b>Example:</b> 6,000">i</span>
        </div>
        <input id="dapIncome" type="text" class="form-control mb-3 lf-ui-054" readonly placeholder="Sync from Expense Lens…"
               />

        <h5 class="lf-ui-055">
            Debt-to-Asset Ratio:
            <span class="lf-ui-059" id="dapOut">0</span>
        </h5>

        <table class="table mt-3 lf-ui-056">
            <tr><th class="lf-ui-057">Assets</th><td id="dapAssets">$0</td></tr>
            <tr><th class="lf-ui-058">Liabilities</th><td id="dapLiabilities">$0</td></tr>
            <tr><th class="lf-ui-058">Net Worth</th><td id="dapNetWorth">$0</td></tr>
            <tr><th class="lf-ui-058">Monthly Income</th><td id="dapMonthlyIncome">$0</td></tr>
        </table>

        <div class="lf-ui-023" id="dapAdvice"
            >
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
        dapOut.textContent = '—';
        dapAssets.textContent = dapLiabilities.textContent =
        dapNetWorth.textContent = dapMonthlyIncome.textContent = '$0';
        dapAdvice.textContent = 'Enter values to get guidance on your financial health.';
        clearPersistedState('DebtAssetPulse');
        hideTip();
        applyLLBSToDebtAssetPulse();
        applyExpenseLensToDebtAssetPulse();
    });

    const updateDAP = () => {
        const hasBalanceSheetSource = hasNonBlankValue(dapA.value) || hasNonBlankValue(dapL.value);
        const hasIncomeSource = hasNonBlankValue(dapIncome.value);
        const assets = parseNumber(dapA.value);
        const liabilities = parseNumber(dapL.value);
        const income = parseNumber(dapIncome.value);

        dapAssets.textContent = `$${formatWithCommas(assets)}`;
        dapLiabilities.textContent = `$${formatWithCommas(liabilities)}`;
        dapNetWorth.textContent = `$${formatWithCommas(assets - liabilities)}`;
        dapMonthlyIncome.textContent = `$${formatWithCommas(income)}`;

        if (!hasBalanceSheetSource) {
            dapOut.textContent = '—';
            dapAdvice.textContent = hasIncomeSource
                ? '⚠️ Complete Financial Health Snapshot to compare your assets and liabilities here.'
                : 'Enter values to get guidance on your financial health.';
            markGold(dapAssets);
            markGold(dapLiabilities);
            markGold(dapNetWorth);
            if (hasIncomeSource && income > 0) markIncome(dapMonthlyIncome);
            else markGold(dapMonthlyIncome);
            markGold(dapOut);
            markGold(dapAdvice);
            saveDAP();
            return;
        }

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
