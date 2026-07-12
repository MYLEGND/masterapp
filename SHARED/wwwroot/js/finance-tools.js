document.addEventListener("DOMContentLoaded", async function () {
    const dropdown = document.getElementById("budgetDropdown");
    const financialHealthButton = document.getElementById("btnFinancialHealthSnapshot");
    const embedContainer = document.getElementById("budget-embed");
    const financeShell = document.querySelector(".finance-shell");
    const financeToolsRow = document.querySelector(".finance-tools-row");
    const financeRoot = document.getElementById("financeRoot");
    const DEFAULT_TOOL_ID = "LegendLivingBalanceSheet";
    const financeApp = (financeRoot?.dataset.financeApp || "").trim().toLowerCase();
    const financeScopeFallback =
        financeRoot?.dataset.financeScopeFallback?.trim() ||
        (financeApp === "agent" ? "agent" : "client");
    const enableGrowthCalculator = (financeRoot?.dataset.enableGrowthCalculator || "").toLowerCase() === "true";
    const enableWealthForecast = financeApp === "agent";
    const wantsAdvancedWealthForecast = (financeRoot?.dataset.enableAdvancedWealthForecast || "").toLowerCase() === "true";
    const enableClientPlanSearch = (financeRoot?.dataset.enableClientPlanSearch || "").toLowerCase() === "true";
    const wantsDistributionPlanner = (financeRoot?.dataset.enableDistributionPlanner || "").toLowerCase() === "true";
    const hasAdvancedWealthForecastDeps =
        !!window.DP_CONSTANTS &&
        !!window.DP_VALIDATORS &&
        typeof window.runDistributionPlan === "function";
    const enableAdvancedWealthForecast = wantsAdvancedWealthForecast && hasAdvancedWealthForecastDeps;
    const enableDistributionPlanner = enableAdvancedWealthForecast && wantsDistributionPlanner;
    if (!dropdown || !embedContainer) return;
    const clientProfileId = financeRoot?.dataset.clientProfileId?.trim() || "";
    const clientUserId = financeRoot?.dataset.clientUserId?.trim() || "";
    const isBusinessClient = (financeRoot?.dataset.isBusinessClient || "").toLowerCase() === "true";
    const clientFirstName = financeRoot?.dataset.clientFirstName?.trim() || "";
    const spouseFirstName = financeRoot?.dataset.spouseFirstName?.trim() || "";
    const hasSpouseAttr = financeRoot?.dataset.hasSpouse;
    const hasSpouse = hasSpouseAttr === "true" ? true : hasSpouseAttr === "false" ? false : undefined;
    const hasClientFinanceContext = clientUserId.length > 0 || clientProfileId.length > 0;
    const workspaceScope =
        clientUserId ||
        clientProfileId ||
        financeScopeFallback;
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
    const effectiveUserScope = enableDistributionPlanner
        ? (plannerUserScope || getLocalFallbackUser())
        : plannerUserScope;
    const scopeKey = (key) => `legend-finance:${workspaceScope}:${key}`;
    const plannerScopeKey = (key) => {
        const plannerScope = effectiveUserScope || financeScopeFallback;
        return `legend-finance:user:${plannerScope}:${key}`;
    };
    const selectedToolStateId = "__workspace__";
    const disableLocalForWF = false; // Wealth Forecast also saves through FinanceToolStates when a client context exists.
    const disableLocalForDP = enableDistributionPlanner; // Distribution Planner stays server-backed only when enabled for this app.
    const storageSet = (key, value) => localStorage.setItem(scopeKey(key), value);
    const storageRemove = (key) => localStorage.removeItem(scopeKey(key));
    // Standalone AgentPortal /Finance should not revive agent-scoped finance tool rows.
    // Those snapshots drift from the shared client finance state and can surface stale
    // Expense Lens bills, income hits, and percentages after the calculation fixes.
    const canUseServerState = hasClientFinanceContext;
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

    const getAntiForgeryToken = () =>
        document.querySelector('#__af input[name="__RequestVerificationToken"]')?.value
        || document.querySelector('input[name="__RequestVerificationToken"]')?.value
        || "";

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

    function normalizePersistedState(key, value) {
        if (key === DEFAULT_TOOL_ID) {
            try {
                return window.LegendLivingBalanceSheetTool?.calculate?.(value ?? {}) ?? (value ?? {});
            } catch (_) {
                return value ?? {};
            }
        }

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

    const localStateKey = (key) =>
        (key && key.startsWith('DistributionPlanner')) ? plannerScopeKey(key) : scopeKey(key);

    const serverSaveQueue = new Map();
    const serverSaveTimers = new Map();
    const serverSaveInFlight = new Set();

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
        const allowLegacyLocalFirst = !canUseServerState && rawStateFirstToolIds.has(key);

        if ((disableLocalForWF && keys.some(k => (k || "").includes("WealthForecast"))) ||
            (disableLocalForDP && keys.some(k => (k || "").includes("DistributionPlanner")))) {
            return {};
        }

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

        // Recovery path for old browser-only state: load it once, then push it to the server.
        // When server-backed state exists, the database stays authoritative so stale
        // browser cache cannot erase valid finance data on reload or deploy.
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
        if ((disableLocalForWF && (key || "").includes("WealthForecast")) ||
            (disableLocalForDP && (key || "").includes("DistributionPlanner"))) return;

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
        if ((disableLocalForWF && toolId === 'WealthForecast') || (disableLocalForDP && toolId === 'DistributionPlanner')) return; // server-backed only
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
        if ((disableLocalForWF && toolId === 'WealthForecast') || (disableLocalForDP && toolId === 'DistributionPlanner')) return; // server-backed only
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
    ].filter(tool => enableWealthForecast || tool.id !== "WealthForecast");
    const dropdownTools = tools.filter(tool => tool.id !== DEFAULT_TOOL_ID);
    let requestedToolOverrideId = "";
    const resolveToolSelection = (toolId) => {
        const resolvedToolId = (toolId || "").trim();
        return tools.some(tool => tool.id === resolvedToolId)
            ? resolvedToolId
            : DEFAULT_TOOL_ID;
    };

    function syncToolSelectorState(toolId) {
        const resolvedToolId = resolveToolSelection(toolId);
        const isDefaultTool = resolvedToolId === DEFAULT_TOOL_ID;
        financialHealthButton?.setAttribute("aria-pressed", isDefaultTool ? "true" : "false");
        if (!dropdown) return;
        if (isDefaultTool) {
            dropdown.selectedIndex = 0;
        } else if (dropdown.value !== resolvedToolId) {
            dropdown.value = resolvedToolId;
        }
    }

    function requestToolSelection(toolId) {
        requestedToolOverrideId = resolveToolSelection(toolId);
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
// Paint an element AND its adjacent suffix span ($ / %) the same color
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

// Safe toast helper for contexts where global toast may not be present
const toast = typeof window.toast === "function" ? window.toast : (msg => console.log(msg || ""));

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
    const wfSearchHost = enableClientPlanSearch ? document.getElementById("wfClientSearchHost") : null;

    dropdown.addEventListener("change", async function () {
        const selectedToolId = resolveToolSelection(requestedToolOverrideId || this.value);
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

        // Toggle WF search host visibility
        if (wfSearchHost) {
            const show = !!t && t.id === "WealthForecast" && enableAdvancedWealthForecast && enableClientPlanSearch;
            wfSearchHost.classList.toggle("d-none", !show);
            if (!show) {
                const statusEl = document.getElementById("wfPlanStatus");
                if (statusEl) statusEl.textContent = "Type to search.";
                const resultsEl = document.getElementById("wfClientResults");
                if (resultsEl) {
                    resultsEl.classList.add("d-none");
                    resultsEl.innerHTML = "";
                }
                const inputEl = document.getElementById("wfClientSearch");
                if (inputEl) inputEl.value = "";
            }
        }

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
                compoundLabEnabled: enableGrowthCalculator,
                clientFirstName,
                spouseFirstName,
                hasSpouse
            });
            return;
        }

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
        const dpUiSessionKey = plannerScopeKey('DistributionPlannerUiSession');
        const loadDpUiSession = () => {
            try { return JSON.parse(localStorage.getItem(dpUiSessionKey) || '{}') || {}; }
            catch { return {}; }
        };
        const saveDpUiSession = (patch = {}) => {
            const merged = { ...loadDpUiSession(), ...(patch || {}) };
            try { localStorage.setItem(dpUiSessionKey, JSON.stringify(merged)); } catch (_) { }
            return merged;
        };

        // ==========================================================
        // 1️⃣ WEALTH FORECAST (ELEVATED) + Tooltips
        // ==========================================================
        if (t.id === "WealthForecast") {
            if (!enableAdvancedWealthForecast) {
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
                <span class="lf-ui-012" id="wbEarnings">$0</span>
                <span class="lf-ui-012" id="wbWealth">$0</span>
            </div>
        </div>
    </div>
</div>`;

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
                            { x: area.right - 8, y: area.top + 14 },
                            { x: area.right - 8, y: area.bottom - 14 }
                        ];
                        ctx.save();
                        data.datasets.forEach((ds, i) => {
                            const val = ds.data?.[ds.data.length - 1];
                            if (val == null) return;
                            const label = `$${Number(val).toLocaleString()}`;
                            const slot = slots[i % slots.length];

                            const padX = 6;
                            ctx.font = "bold 13px 'Inter', sans-serif";
                            const textW = ctx.measureText(label).width;
                            const boxW = textW + padX * 2;
                            const boxH = 20;
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

                            ctx.fillStyle = "#eaf2ff";
                            ctx.textAlign = "center";
                            ctx.textBaseline = "middle";
                            ctx.fillText(label, boxX + boxW / 2, boxY + boxH / 2);
                        });
                        ctx.restore();
                    }
                };

                applyToolBoxStyles(container);

                const TOOL_KEY = "WealthForecast";
                await loadToolState(TOOL_KEY);

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

                [incomeEl, yearsEl, inflEl, retEl, taxEl, liabEl, lifeEl].forEach(el => {
                    el.addEventListener("blur", () => {
                        let val = el.value.replace(/,/g, '').replace('%', '');
                        if (!isNaN(val) && val !== '') {
                            el.value = Number(val).toLocaleString();
                        }
                    });
                });

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
                        spendPoints.push(-cumulativeSpend);
                    }

                    earningsOut.textContent = `$${(income * years).toLocaleString()}`;
                    wealthOut.textContent = `$${investedBalance.toLocaleString()}`;
                    realGrowthOut.textContent = `${(realGrowthRate * 100).toFixed(2)}%`;
                    savingsPercentOut.textContent = `${(savingsRate * 100).toFixed(2)}%`;
                    actualSavingsOut.textContent = `$${annualSavings.toLocaleString()}`;

                    markWithSuffix(markIncome,  incomeEl);
                    markWithSuffix(markExpense, taxEl);
                    markWithSuffix(markExpense, liabEl);
                    markWithSuffix(markExpense, lifeEl);

                    markNeutral(yearsEl);
                    markWithSuffix(markNeutral, inflEl);
                    markWithSuffix(markNeutral, retEl);

                    markIncome(earningsOut);
                    markIncome(wealthOut);
                    markIncome(actualSavingsOut);

                    if (savingsRate > 0) markIncome(savingsPercentOut);
                    else markExpense(savingsPercentOut);

                    if (realGrowthRate >= 0) markIncome(realGrowthOut);
                    else markExpense(realGrowthOut);

                    markGold(savingsTipsOut);

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

                [incomeEl, yearsEl, inflEl, retEl, taxEl, liabEl, lifeEl].forEach(el => {
                    el.addEventListener("input", calcWealthForecast);
                });

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

                calcWealthForecast();
                return;
            }

            await ensureChartJs();
            embedContainer.innerHTML = `
<div class="networth-tool legend-finance-tool-card legend-finance-tool-card--wide legend-finance-tool-card--spacious el-shell">

    <div id="wbTipLayer"></div>
    <div class="wf-header-row">
      <div class="wf-title-stack">
        <h3 class="lf-ui-060">
            ${t.name}
        </h3>
      </div>
      <div id="wfActions" class="wf-actions"></div>
    </div>
    <div class="lf-ui-061">
        <!-- Inputs Column -->
        <div class="lf-ui-062">
            <div class="wf-input-grid">
                <div class="wf-row row-primary">
                    <div>
                        <label class="wb-label">
                            Starting Balance
                            <span class="wb-i" tabindex="0" data-tip="<b>Examples:</b> 25,000 • 100,000 • 250,000 (existing investable assets at start)">i</span>
                        </label>
                        <div class="lf-ui-006">
                            <input id="wbStartingBalance" type="text" class="form-control lf-ui-007" />
                            <span class="lf-ui-008">$</span>
                        </div>
                    </div>
                    <div>
                        <label class="wb-label">
                            Annual Income
                            <span class="wb-i" tabindex="0" data-tip="<b>Examples:</b> 60,000 • 85,500 • 120,000 (gross annual pay)">i</span>
                        </label>
                        <div class="lf-ui-006">
                            <input id="wbIncome" type="text" class="form-control lf-ui-007" />
                            <span class="lf-ui-008">$</span>
                        </div>
                    </div>
                    <div>
                        <label class="wb-label">
                            Work Period
                            <span class="wb-i" tabindex="0" data-tip="<b>Examples:</b> 10 • 20 • 30 (years you plan to keep earning/saving)">i</span>
                        </label>
                        <input id="wbYears" type="text" class="form-control lf-ui-009" />
                    </div>
                </div>

                <div class="wf-row row-duo">
                    <div>
                        <label class="wb-label">
                            Inflation
                            <span class="wb-i" tabindex="0" data-tip="<b>Examples:</b> 2.5 • 3 • 4 (average annual inflation %)">i</span>
                        </label>
                        <div class="lf-ui-006">
                            <input id="wbInflation" type="text" class="form-control lf-ui-007" />
                            <span class="lf-ui-008">%</span>
                        </div>
                    </div>
                    <div>
                        <label class="wb-label">
                            After-Tax Rate of Return
                            <span class="wb-i" tabindex="0" data-tip="<b>Examples:</b> 5 • 7 • 9 (after-tax investment return %)">i</span>
                        </label>
                        <div class="lf-ui-006">
                            <input id="wbReturn" type="text" class="form-control lf-ui-007" />
                            <span class="lf-ui-008">%</span>
                        </div>
                    </div>
                </div>

                <div class="wf-row row-trio">
                    <div>
                        <label class="wb-label">
                            Tax Bracket
                            <span class="wb-i" tabindex="0" data-tip="<b>Examples:</b> 12 • 22 • 24 (effective/estimated rate %)">i</span>
                        </label>
                        <div class="lf-ui-006">
                            <input id="wbTax" type="text" class="form-control lf-ui-007" />
                            <span class="lf-ui-008">%</span>
                        </div>
                    </div>
                    <div>
                        <label class="wb-label">
                            Fixed Liabilities
                            <span class="wb-i" tabindex="0" data-tip="<b>Examples:</b> 10 • 18 • 25 (debt payments as % of income)">i</span>
                        </label>
                        <div class="lf-ui-006">
                            <input id="wbLiabilities" type="text" class="form-control lf-ui-007" />
                            <span class="lf-ui-008">%</span>
                        </div>
                    </div>
                    <div>
                        <label class="wb-label">
                            Lifestyle Spending
                            <span class="wb-i" tabindex="0" data-tip="<b>Examples:</b> 35 • 45 • 55 (living costs + wants as % of income)">i</span>
                        </label>
                        <div class="lf-ui-006">
                            <input id="wbLifestyle" type="text" class="form-control lf-ui-007" />
                            <span class="lf-ui-008">%</span>
                        </div>
                    </div>
                </div>

                <div class="wf-disrupt-card">
                    <div class="wf-disrupt-head">
                        <div class="wf-disrupt-title">Income Disruption / Disability Income</div>
                        <div class="wf-disrupt-sub">Model a temporary income loss and disability income replacement during accumulation.</div>
                    </div>
                    <div class="wf-disrupt-row lf-ui-063">
                        <div>
                            <label class="wb-label">Disruption Start Year</label>
                            <input id="wbDisruptStartYear" type="text" class="form-control lf-ui-064" placeholder="1" />
                        </div>
                        <div>
                            <label class="wb-label">Years of Income Disruption</label>
                            <input id="wbDisruptYears" type="text" class="form-control lf-ui-064" placeholder="0" />
                        </div>
                    </div>
                    <div class="wf-disrupt-row">
                        <div>
                            <label class="wb-label">Months of Income Disruption</label>
                            <input id="wbDisruptMonths" type="text" class="form-control lf-ui-064" placeholder="0" />
                        </div>
                        <div>
                            <label class="wb-label">Income Replacement %</label>
                            <div class="lf-ui-006">
                                <input id="wbDisabilityPct" type="text" class="form-control lf-ui-065" placeholder="0" />
                                <span class="lf-ui-008">%</span>
                            </div>
                        </div>
                    </div>
                </div>
            </div>

        </div>

        <!-- Outputs + Chart -->
        <div class="wf-output-col lf-ui-066">
            <div class="wf-toggle-row">
                <label class="wf-toggle-label lf-ui-067">
                    <input id="wf_toggleWealth" type="checkbox" checked> Projected Wealth
                </label>
                <label class="wf-toggle-label lf-ui-068">
                    <input id="wf_toggleSpend" type="checkbox"> Cumulative Spending
                </label>
            </div>
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
            <div id="wfOutputActions" class="wf-output-actions"></div>
        </div>
    </div>

    <!-- Full-screen chart modal -->
    <div id="wfChartModalBackdrop" class="wf-chart-modal-backdrop" aria-hidden="true">
        <div class="wf-chart-modal" role="dialog" aria-modal="true" aria-label="Wealth Forecast full chart">
            <div class="wf-chart-modal-head">
                <h4>Wealth Forecast — Full View</h4>
                <button type="button" class="wf-chart-close" id="wfChartCloseBtn">Close</button>
            </div>
            <div class="wf-chart-modal-body">
                <canvas id="wfChartModalCanvas"></canvas>
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
            const chartModalBackdrop = document.getElementById("wfChartModalBackdrop");
            const chartModalCanvas = document.getElementById("wfChartModalCanvas");
            const chartModalClose = document.getElementById("wfChartCloseBtn");
            let wfChart = null;
            let wfModalChart = null;
            let wfChartData = null;
            let wfChartClickBound = false;
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

            // Modal helpers
            async function renderWfModalChart(){
                if (!chartModalCanvas || !wfChartData) return;
                await ensureChartJs();
                if (wfModalChart){
                    wfModalChart.destroy();
                    wfModalChart = null;
                }
                const ctx = chartModalCanvas.getContext("2d");
                wfModalChart = new Chart(ctx, {
                    type:"line",
                    data:{
                        labels: wfChartData.labels,
                        datasets:[
                            {
                                label:"Projected Wealth",
                                data: wfChartData.wealthPoints,
                                borderColor:"#16a34a",
                                borderWidth:3,
                                tension:0.25,
                                fill:false,
                                pointRadius:3,
                                pointHoverRadius:6
                            },
                            {
                                label:"Cumulative Spending",
                                data: wfChartData.spendPoints,
                                borderColor:"#dc2626",
                                borderWidth:3,
                                tension:0.25,
                                fill:false,
                                pointRadius:3,
                                pointHoverRadius:6
                            }
                        ]
                    },
                    options:{
                        responsive:true,
                        maintainAspectRatio:false,
                        interaction:{ mode:'nearest', intersect:true },
                        events:['mousemove','mouseout','click','touchstart','touchmove'],
                        plugins:{
                            legend:{ labels:{ color:"#eaf2ff", usePointStyle:true } },
                            tooltip:{
                                enabled:true,
                                callbacks:{
                                    label: ctx => ` ${ctx.dataset.label}: ${ctx.formattedValue}`
                                }
                            }
                        },
                        scales:{
                            x:{ ticks:{ color:"#eaf2ff" }, grid:{ color:"rgba(255,255,255,.08)" }, title:{ display:true, text:"Year", color:"#eaf2ff" } },
                            y:{ ticks:{ color:"#eaf2ff", callback:v=>`$${Number(v).toLocaleString()}` }, grid:{ color:"rgba(255,255,255,.08)" }, title:{ display:true, text:"Balance / Spend ($)", color:"#eaf2ff" } }
                        }
                    }
                });
            }

            function showWfModal(){
                if (!chartModalBackdrop || !wfChartData) return;
                chartModalBackdrop.style.display = "flex";
                chartModalBackdrop.setAttribute("aria-hidden","false");
                document.body.style.overflow = "hidden";
                void renderWfModalChart();
            }

            function hideWfModal(){
                if (!chartModalBackdrop) return;
                chartModalBackdrop.style.display = "none";
                chartModalBackdrop.setAttribute("aria-hidden","true");
                document.body.style.overflow = "";
                if (wfModalChart){
                    wfModalChart.destroy();
                    wfModalChart = null;
                }
            }

            chartModalBackdrop?.addEventListener("click", (e) => { if (e.target === chartModalBackdrop) hideWfModal(); });
            chartModalClose?.addEventListener("click", hideWfModal);
            document.addEventListener("keydown", (e) => { if (e.key === "Escape") hideWfModal(); });

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
            const setSearchResultsVisible = (element, isVisible) => {
                if (!element) return;
                element.classList.toggle("d-none", !isVisible);
            };

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
                if (wfResultsEl){
                    setSearchResultsVisible(wfResultsEl, false);
                    wfResultsEl.innerHTML = "";
                }
                if (dpResultsRef){
                    setSearchResultsVisible(dpResultsRef, false);
                    dpResultsRef.innerHTML = "";
                }
                const statusEl = document.getElementById("wfPlanStatus");
                if (statusEl){ statusEl.textContent = "Loading plan…"; statusEl.classList.remove("text-danger"); }
                wfActiveClientId = item.clientUserId;
                dpActiveClientId = item.clientUserId;
                saveDpUiSession({ activeClientId: item.clientUserId, activeClientName: name });
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
                    if (wfResultsEl){
                        setSearchResultsVisible(wfResultsEl, false);
                        wfResultsEl.innerHTML = "";
                    }
                    wfActiveClientId = null;
                    dpActiveClientId = null;
                    saveDpUiSession({ activeClientId: null, activeClientName: null });
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
                            div.className = "list-group-item list-group-item-action finance-search-result";
                            div.innerHTML = `
                                <span class="lf-ui-069">${item.displayName || "Client"}</span>
                                <span class="lf-ui-070">${item.email || "—"}${item.phone ? " · " + item.phone : ""}</span>
                                <span class="finance-search-result__note ${item.hasSavedPlan ? 'finance-search-result__note--saved' : 'finance-search-result__note--empty'}">${item.hasSavedPlan ? 'Plan saved' : 'No plan yet'}</span>
                            `;
                            div.addEventListener("click", async () => { await selectActiveClient(item); });
                            frag.appendChild(div);
                        });
                        wfResultsEl.replaceChildren(frag);
                        setSearchResultsVisible(wfResultsEl, true);
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
                    const annualExpenses = taxAmt + baselineLiabAmt + baselineLifeAmt; // single source of truth
                    const annualSavings = effectiveIncome - annualExpenses; // allow negative to reflect shortfall
                    const annualSpend = annualExpenses; // track true expense outflow (no scaling)

                    investedBalance = investedBalance * (1 + realGrowthRate) + annualSavings;
                    cumulativeSpend += annualExpenses;
                    totalSavings += annualSavings;
                    totalIncome += effectiveIncome;

                    labels.push(`Year ${y}`);
                    wealthPoints.push(investedBalance);
                    spendPoints.push(-cumulativeSpend);
                }

                wfChartData = { labels, wealthPoints, spendPoints };

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
markWithSuffix(markIncome,  incomeEl);
markWithSuffix(markNeutral, startingBalEl);
markWithSuffix(markExpense, taxEl);
markWithSuffix(markExpense, liabEl);
markWithSuffix(markExpense, lifeEl);

markNeutral(yearsEl);
markWithSuffix(markNeutral, inflEl);
markWithSuffix(markNeutral, retEl);
markNeutral(disruptStartEl);
markNeutral(disruptYearsEl);
markNeutral(disruptMonthsEl);
markWithSuffix(markNeutral, disabilityPctEl);

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
markGold(savingsTipsOut);

            // Chart update
            if (chartEl && typeof Chart !== "undefined"){
                    if (!wfChart){
                        wfChart = new Chart(chartEl, {
                            type: "line",
                            data: {
                                labels,
                                datasets: [{
                                    label: "Projected Wealth",
                                    data: wealthPoints,
                                    borderWidth: 3,
                                    tension: 0.25,
                                    fill: false,
                                    borderColor: "#16a34a",
                                    pointRadius: ctx => ctx.dataIndex === ctx.dataset.data.length - 1 ? 5 : 0,
                                    pointHoverRadius: ctx => ctx.dataIndex === ctx.dataset.data.length - 1 ? 8 : 0,
                                    pointHitRadius: ctx => ctx.dataIndex === ctx.dataset.data.length - 1 ? 12 : 0
                                },{
                                    label: "Cumulative Spending",
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
                                    legend: { display: false },
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

                // Click to open modal
                if (chartEl && !wfChartClickBound){
                    chartEl.addEventListener("click", () => showWfModal());
                    wfChartClickBound = true;
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
            // DISTRIBUTION BUTTON
            // ========================
            const distBtn = document.createElement('button');
            distBtn.type = 'button';
            distBtn.innerHTML = '<span class="wfd-btn-icon">&#9654;</span> Distribution Planner';
            distBtn.className = 'wf-dist-launch-btn';
            const wfOutputActionsHost = document.getElementById('wfOutputActions');
            if (wfOutputActionsHost) {
                wfOutputActionsHost.appendChild(distBtn);
            } else if (wfActionsHost) {
                wfActionsHost.appendChild(distBtn);
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

<div id="wfDist_panel">
  <!-- HEADER -->
    <div class="wfd-hdr">
      <button class="lf-ui-071" id="wfd_close" type="button" aria-label="Close"
       >×</button>
      <h2 class="lf-ui-072">Distribution Planner</h2>
      <p class="lf-ui-073">Retirement income strategy — coming down the mountain</p>
      <p class="lf-ui-074">Auto-populated from your Wealth Forecast final projected balance.</p>
      <div class="lf-ui-075" id="dpClientSearchRow">
        <input id="dpClientSearch" class="form-control form-control-sm lf-ui-076" placeholder="Search client" />
        <button id="dpClientSearchBtn" class="btn btn-ghost btn-sm" type="button">Search</button>
        <span id="dpPlanStatus" class="text-muted small">No client selected.</span>
      </div>
      <div id="dpClientResults" class="list-group lf-ui-077"></div>
      <div class="wfd-steps" id="wfd_stepsNav">
        <div class="wfd-step-chip active" data-step="1"><span class="step-num">1</span> Foundation</div>
                <div class="wfd-step-chip" data-step="2"><span class="step-num">2</span> Strategy</div>
            <div class="wfd-step-chip" data-step="3"><span class="step-num">3</span> Results</div>
    </div>
  </div>

  <!-- BODY -->
  <div class="wfd-body">
    <div id="wfd_block" class="wfd-warn-box lf-ui-078"></div>

    <!-- STEP 1: Foundation -->
    <div class="wfd-step-wrap active" data-step="1">
      <div id="wfd_block_top" class="wfd-warn-box lf-ui-078"></div>
    <div id="wfd_noBaseWarn" class="wfd-warn-box lf-ui-079">
      ⚠️ Wealth Forecast has no valid result yet. Complete the Wealth Forecast inputs above first, or enable <strong>Manual Override</strong> below to enter a base manually.
    </div>

    <!-- No-base warning -->
    <!-- SECTION 1: Retirement Foundation -->
    <div class="wfd-sec">
      <div class="lf-ui-080">
        <p class="wfd-sec-title lf-ui-081">1 — Retirement Foundation</p>
        <button type="button" class="wfd-step-clear" id="wfd_clearStep1">Clear Step</button>
      </div>
      <div class="wfd-row">
        <div class="wfd-col">
          <label class="wfd-lbl" for="wfd_base">Retirement Base (from Wealth Forecast) <span class="lf-ui-082">read-only</span></label>
          <input id="wfd_base" class="wfd-inp" type="text" readonly placeholder="Run Wealth Forecast above" />
        </div>
        <div class="wfd-col lf-ui-083">
          <div class="wfd-tog-wrap lf-ui-084">
            <label class="wfd-tog"><input type="checkbox" id="wfd_manualOverride" /><span class="wfd-tog-sl"></span></label>
            <span class="wfd-tog-lbl lf-ui-085">Manual Override (what-if)</span>
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
          <label class="wfd-lbl" for="wfd_yrsInDist">Years in Distribution <span class="lf-ui-082">auto-calc</span></label>
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
          <label class="wfd-lbl" for="wfd_guaranteedIncome">Other Guaranteed Income ($, after-tax) <span class="lf-ui-086">Social Security, pension, rental</span></label>
          <input id="wfd_guaranteedIncome" class="wfd-inp" type="text" placeholder="20,000" />
        </div>
        <div class="wfd-col">
          <label class="wfd-lbl" for="wfd_incomeGap">Net Income Gap to Fund From Assets <span class="lf-ui-086">auto-calc</span></label>
          <input id="wfd_incomeGap" class="wfd-inp" type="text" readonly placeholder="$0" />
        </div>
      </div>
    </div><!-- end foundation -->

    </div><!-- end step 1 -->

    <!-- STEP 2: Three Bucket Allocation -->
    <div class="wfd-step-wrap" data-step="2">
      <div class="wfd-sec">
      <div class="lf-ui-080">
        <p class="wfd-sec-title lf-ui-081">2 — Three Bucket Allocation</p>
        <button type="button" class="wfd-step-clear" id="wfd_clearStep2">Clear Step</button>
      </div>
      <p class="lf-ui-087">Allocations must total exactly 100%. Dollar amounts are auto-calculated from the Retirement Base.</p>

      <div class="wfd-alloc-row">
        <span class="lf-ui-088">Total Allocated:</span>
        <span id="wfd_allocTotal" class="wfd-alloc-bad">0%</span>
        <span class="lf-ui-089" id="wfd_allocStatus">— must equal 100%</span>
      </div>

      <!-- Allocation bar visual -->
      <div class="wfd-bkt-vis" id="wfd_allocVis">
        <div class="wfd-bkt-bar-wrap">
          <div id="wfd_invBar" class="wfd-bkt-bar lf-ui-090"></div>
          <div class="wfd-bkt-bar-lbl">Investments</div>
        </div>
        <div class="wfd-bkt-bar-wrap">
          <div id="wfd_liBar" class="wfd-bkt-bar lf-ui-091"></div>
          <div class="wfd-bkt-bar-lbl">Life Ins</div>
        </div>
        <div class="wfd-bkt-bar-wrap">
          <div id="wfd_annBar" class="wfd-bkt-bar lf-ui-092"></div>
          <div class="wfd-bkt-bar-lbl">Annuities</div>
        </div>
      </div>

      <div class="wfd-bkt-grid">

        <!-- A: Investments -->
        <div id="wfd_invCard" class="wfd-bkt lf-ui-093">
          <p class="wfd-bkt-title lf-ui-094">A — Investments</p>
          <p class="wfd-bkt-sub">Growth Engine — Stocks, bonds, ETFs, mutual funds, brokerage, retirement accounts</p>
          <div class="wfd-tog-wrap lf-ui-095">
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
        <div id="wfd_liCard" class="wfd-bkt lf-ui-096">
          <p class="wfd-bkt-title lf-ui-097">B — Life Insurance / Equivalent</p>
          <p class="wfd-bkt-sub">Stability Buffer — Cash value life insurance, overfunded permanent insurance, protected strategies</p>
          <div class="wfd-tog-wrap lf-ui-095">
            <span id="wfd_liDmBadge" class="wfd-dm-badge">Down-Market: On</span>
          </div>
          <label class="wfd-lbl" for="wfd_liType">Policy Type</label>
          <select id="wfd_liType" class="wfd-inp lf-ui-098">
            <option value="whole">Whole Life</option>
            <option value="iul">Indexed UL</option>
            <option value="vul">Variable UL</option>
            <option value="legacy_rpu">Legacy / Reduced Paid-Up</option>
          </select>
          <label class="wfd-lbl" for="wfd_liAccess">Access Method</label>
          <select id="wfd_liAccess" class="wfd-inp lf-ui-098">
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
          <label class="wfd-lbl" for="wfd_liEfficiency">Access / Efficiency Factor % <span class="lf-ui-086">optional, default 100</span></label>
          <input id="wfd_liEfficiency" class="wfd-inp" type="number" step="0.1" placeholder="100" />
          <div class="wfd-tog-wrap">
            <label class="wfd-tog"><input type="checkbox" id="wfd_liDownMkt" checked /><span class="wfd-tog-sl"></span></label>
            <span class="wfd-tog-lbl">Use in Down Market?</span>
          </div>
        </div>

        <!-- C: Annuities -->
        <div id="wfd_annCard" class="wfd-bkt lf-ui-099">
          <p class="wfd-bkt-title lf-ui-100">C — Annuities</p>
          <p class="wfd-bkt-sub">Income Floor — Protected income / accumulation hybrid</p>
          <div class="wfd-tog-wrap lf-ui-095">
            <span id="wfd_annDmBadge" class="wfd-dm-badge">Down-Market: On</span>
          </div>
          <label class="wfd-lbl" for="wfd_annDesign">Annuity Design</label>
          <select id="wfd_annDesign" class="wfd-inp lf-ui-098">
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
          <div class="wfd-tog-wrap lf-ui-101">
            <label class="wfd-tog"><input type="checkbox" id="wfd_annIncomeRider" /><span class="wfd-tog-sl"></span></label>
            <span class="wfd-tog-lbl">Income Rider</span>
          </div>
          <div class="lf-ui-012" id="wfd_annRollupWrap">
            <label class="wfd-lbl" for="wfd_annRollup">Income Rider Rollup Rate (%)</label>
            <input id="wfd_annRollup" class="wfd-inp" type="number" step="0.1" placeholder="5.0" value="5.0" />
          </div>
          <div class="wfd-tog-wrap lf-ui-101">
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
            <div class="lf-ui-102"></div>
            <div class="lf-ui-080">
                <p class="wfd-sec-title lf-ui-081">3 — Strategy Controls</p>
            </div>
      <div class="wfd-row lf-ui-103">
        <button type="button" class="wfd-calc-btn lf-ui-104" id="wfd_strat_prop">Proportional</button>
        <button type="button" class="wfd-calc-btn lf-ui-104" id="wfd_strat_pri">Priority Order</button>
        <button type="button" class="wfd-calc-btn lf-ui-104" id="wfd_strat_guard">Protect Investments</button>
      </div>
      <input type="hidden" id="wfd_strategy" value="proportional" />
      <div id="wfd_priorityRow" class="wfd-row lf-ui-105">
        <div class="wfd-col lf-ui-106">
          <label class="wfd-lbl lf-ui-046" for="wfd_pri1">Withdrawal Priority (1 = first)</label>
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
      <div class="wfd-row lf-ui-107">
        <div class="wfd-col">
          <div class="wfd-tog-wrap lf-ui-108">
            <label class="wfd-tog"><input type="checkbox" id="wfd_protectInvest" checked /><span class="wfd-tog-sl"></span></label>
            <span class="wfd-tog-lbl lf-ui-109">Protect Investments During Down Markets</span>
          </div>
          <p class="wfd-mini-note lf-ui-110">When on, investments pause in down years unless fallback is required.</p>
        </div>
        <div class="wfd-col">
          <label class="wfd-lbl lf-ui-108" for="wfd_downThreshold">Down-Market Threshold % <span class="lf-ui-086">e.g. 0 = negative years only</span></label>
          <input id="wfd_downThreshold" class="wfd-inp" type="number" step="0.1" placeholder="0" value="0" />
        </div>
      </div>

      <div class="wfd-row lf-ui-111">
        <div class="wfd-col">
          <label class="wfd-lbl" for="wfd_gapSource">Gap Funding Source (Down Years)</label>
          <select id="wfd_gapSource" class="wfd-inp lf-ui-098">
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
          <select id="wfd_scenarioMode" class="wfd-inp lf-ui-098">
            <option value="fixed">Fixed return each year</option>
            <option value="random">Randomized yearly path</option>
            <option value="manual">Manual yearly returns</option>
          </select>
        </div>
                <div class="wfd-col">
                    <label class="wfd-lbl" for="wfd_stressProfile">Historical Stress Profile</label>
                    <select id="wfd_stressProfile" class="wfd-inp lf-ui-098">
                        <option value="conservative">Conservative</option>
                        <option value="balanced" selected>Balanced</option>
                        <option value="aggressive">Aggressive</option>
                    </select>
                </div>
      </div>

      <div class="wfd-row lf-ui-112">
        <div class="wfd-col lf-ui-113">
          <label class="wfd-lbl lf-ui-108" for="wfd_manualReturns">Manual / Scenario Returns (% per year, comma or line separated)</label>
          <textarea id="wfd_manualReturns" class="wfd-inp lf-ui-114" placeholder="7, 6.5, -12, 8, 5, ..."></textarea>
          <p class="wfd-mini-note lf-ui-101">Illustration only — randomized paths are not predictions or guarantees.</p>
        </div>
        <div class="wfd-col lf-ui-115">
          <button id="wfd_genScenario" class="wfd-calc-btn lf-ui-108" type="button">Generate Market Scenario</button>
        </div>
      </div>
            </div><!-- end sec -->
        </div><!-- end buckets + strategy -->

        <!-- STEP 3: RESULTS -->
        <div class="wfd-step-wrap" data-step="3" id="wfd_results">
      <div class="wfd-sec lf-ui-116">
        <div class="lf-ui-117">
          <button class="wfd-calc-btn lf-ui-118" id="wfd_editFoundation" type="button">Edit Foundation</button>
                    <button class="wfd-calc-btn lf-ui-119" id="wfd_editBuckets" type="button">Edit Strategy</button>
          <button class="wfd-calc-btn lf-ui-120" id="wfd_recalcBtn" type="button">Recalculate</button>
        </div>
        <div class="lf-ui-121">
          <button class="wfd-calc-btn lf-ui-122" type="button" id="wfd_runBase">Run Base Case</button>
          <button class="wfd-calc-btn lf-ui-122" type="button" id="wfd_runDown">Simulate Down Market</button>
          <button class="wfd-calc-btn lf-ui-122" type="button" id="wfd_runScenario">Generate Market Scenario</button>
        </div>
        <div class="accordion lf-ui-123">
          <div class="wfd-acc">
            <button class="wfd-acc-btn" data-target="wfd_summaryWrap">Summary</button>
            <div id="wfd_summaryWrap" class="wfd-acc-body">
              <div id="wfd_summary" class="wfd-summary lf-ui-063">
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
              <div class="lf-ui-124">
                <span class="lf-ui-125">Plan Health:</span>
                <span id="wfd_healthBadge" class="wfd-badge">—</span>
              </div>
            </div>
          </div>
                    <div class="wfd-acc collapsed">
            <button class="wfd-acc-btn" data-target="wfd_fundingWrap">Funding Breakdown</button>
            <div id="wfd_fundingWrap" class="wfd-acc-body">
              <div class="wfd-res-grid" id="wfd_resGrid"></div>
              <div id="wfd_sourceBreak" class="wfd-mini-note lf-ui-110"></div>
              <div class="wfd-bkt-vis lf-ui-126" id="wfd_wdrlVis">
                <div class="wfd-bkt-bar-wrap">
                  <div id="wfd_emWBar" class="wfd-bkt-bar lf-ui-127"></div>
                  <div id="wfd_emWLbl" class="wfd-bkt-bar-lbl">Emergency<br>$0</div>
                </div>
                <div class="wfd-bkt-bar-wrap">
                  <div id="wfd_invWBar" class="wfd-bkt-bar lf-ui-128"></div>
                  <div id="wfd_invWLbl" class="wfd-bkt-bar-lbl">Investments<br>$0</div>
                </div>
                <div class="wfd-bkt-bar-wrap">
                  <div id="wfd_liWBar" class="wfd-bkt-bar lf-ui-129"></div>
                  <div id="wfd_liWLbl" class="wfd-bkt-bar-lbl">Life Ins<br>$0</div>
                </div>
                <div class="wfd-bkt-bar-wrap">
                  <div id="wfd_annWBar" class="wfd-bkt-bar lf-ui-130"></div>
                  <div id="wfd_annWLbl" class="wfd-bkt-bar-lbl">Annuities<br>$0</div>
                </div>
              </div>
              <div id="wfd_emCard" class="wfd-em-card lf-ui-131">
                <div>
                  <p class="wfd-res-lbl lf-ui-081">Emergency Reserve</p>
                  <p id="wfd_emNow" class="wfd-sum-value lf-ui-132">—</p>
                </div>
                <div>
                  <p class="wfd-mini-note lf-ui-081">Year 1 Used</p>
                  <p id="wfd_emUsed" class="wfd-res-val lf-ui-081">—</p>
                </div>
                <div>
                  <p class="wfd-mini-note lf-ui-081">Total Used (Plan)</p>
                  <p id="wfd_emTotal" class="wfd-res-val lf-ui-081">—</p>
                </div>
                <div>
                  <p class="wfd-mini-note lf-ui-081">Remaining</p>
                  <p id="wfd_emRemain" class="wfd-res-val lf-ui-081">—</p>
                </div>
                <div>
                  <p class="wfd-mini-note lf-ui-081">Depletion</p>
                  <p id="wfd_emDeplete" class="wfd-res-val lf-ui-081">—</p>
                </div>
                <div id="wfd_emStatus" class="wfd-badge lf-ui-133">—</div>
              </div>
            </div>
          </div>
          <div class="wfd-acc">
            <button class="wfd-acc-btn" data-target="wfd_chartWrapAcc">Longevity Chart</button>
            <div id="wfd_chartWrapAcc" class="wfd-acc-body">
              <p class="lf-ui-134">Asset Longevity Over Distribution Period</p>
              <div class="wfd-chart-wrap"><canvas id="wfd_chart"></canvas></div>
            </div>
          </div>
          <div class="wfd-acc collapsed">
            <button class="wfd-acc-btn" data-target="wfd_tipsWrap">Year-by-Year Audit</button>
            <div id="wfd_tipsWrap" class="wfd-acc-body">
                              <div class="lf-ui-135" id="wfd_legacyTiles"></div>
              <div class="lf-ui-136" id="wfd_bktTiles"></div>
              <div class="lf-ui-108" id="wfd_tips"></div>
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
    <button class="lf-ui-012" id="wfd_calcBtn" type="button">Calculate</button>

  </div><!-- end body -->

  <!-- STICKY FOOTER NAV -->
  <div class="wfd-footer">
    <button id="wfd_clearBtn" class="wfd-calc-btn wfd-secondary lf-ui-137" type="button">Clear</button>
    <button id="wfd_prev" class="wfd-calc-btn wfd-secondary lf-ui-138" type="button">Back</button>
    <button id="wfd_next" class="wfd-calc-btn lf-ui-139" type="button">Continue</button>
    <button id="wfd_run" class="wfd-calc-btn lf-ui-140" type="button">Run Plan</button>
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
                    saveDpUiSession({ modalOpen: false, lastStep: activeStep });
                };
                gid('wfd_close').addEventListener('click', closeDistModal);
                const showDistModal = (stepToOpen='1') => {
                    const modal = gid(DIST_OVR_ID);
                    modal.classList.add('wfd-open');
                    document.body.style.overflow = 'hidden';
                    trapFocus(modal);
                    updateDMState();
                    document.getElementById('wfd_retAge').dispatchEvent(new Event('input'));
                    document.getElementById('wfd_desiredIncome').dispatchEvent(new Event('input'));
                    const reopenStep = stepToOpen || '1';
                    setStep(reopenStep);
                    if (reopenStep === '3') hydrateResultsFromMeta();
                    distMeta.open = true; saveMeta();
                    saveDpUiSession({ modalOpen: true, lastStep: reopenStep });
                };
                // Step navigation + meta
                const steps = ['1','2','3'];
                let activeStep = '1';
                let distAllocManual = false;
                var distMeta = { hasValidResults:false, lastStep:'1', stale:false, result:null, open:false };
                function syncStepVisibility() {
                    document.querySelectorAll('.wfd-step-wrap').forEach(w=>{
                        const isActive = w.dataset.step === activeStep;
                        w.classList.toggle('active', isActive);
                        w.style.display = isActive ? 'block' : 'none';
                    });
                }
                function applyResultsAccordionDefaults(){
                    const openTargets = new Set(['wfd_summaryWrap','wfd_chartWrapAcc']);
                    document.querySelectorAll('.wfd-acc-btn').forEach(btn => {
                        const parent = btn.closest('.wfd-acc');
                        const target = btn.dataset.target;
                        if (!parent || !target) return;
                        if (openTargets.has(target)) parent.classList.remove('collapsed');
                        else parent.classList.add('collapsed');
                    });
                }
                function setStep(step, { skipHydrate = false } = {}){
                    if (step === '4') step = '3';
                    activeStep = step;
                    document.querySelectorAll('.wfd-step-chip').forEach(chip=>{
                        chip.classList.toggle('active', chip.dataset.step === step);
                    });
                    syncStepVisibility();
                    distMeta.lastStep = step; saveMeta(); saveDistState();
                    saveDpUiSession({ lastStep: step });
                    gid('wfd_prev').style.visibility = step === '1' ? 'hidden' : 'visible';
                    const next = gid('wfd_next');
                    const run  = gid('wfd_run');
                    const nextLabels = { '1':'Next: Strategy', '2':'View Results', '3':'View Results' };
                    if (next) {
                        next.textContent = nextLabels[step] || 'Continue';
                        next.style.display = step === '3' ? 'none' : 'inline-flex';
                    }
                    if (run) {
                        if (step === '2' || step === '3') {
                            run.style.display = 'inline-flex';
                            run.textContent = step === '3' ? 'Run Again' : 'Run Plan';
                        } else {
                            run.style.display = 'none';
                        }
                    }
                    if (step === '3') {
                        applyResultsAccordionDefaults();
                    }
                    if (step === '3' && !skipHydrate) {
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
                const distSelectIds = ['wfd_strategy','wfd_pri1','wfd_pri2','wfd_pri3','wfd_pri4','wfd_gapSource','wfd_scenarioMode','wfd_stressProfile','wfd_liType','wfd_liAccess','wfd_annDesign'];
                const DIST_META_KEY = plannerScoped ? `DistributionPlannerMeta:user:${effectiveUserScope}` : null;
                const DIST_META_LOCAL_KEY = plannerScopeKey('DistributionPlannerMetaLocal');
                let dpPlanLoaded = false;

                function captureDpEditableState(){
                    const state = { inputs:{}, checks:{}, selects:{} };
                    distPersistInputs.forEach(id => {
                        const el = gid(id);
                        if (el) state.inputs[id] = el.value;
                    });
                    if (gid('wfd_manualOverride')?.checked) {
                        const baseEl = gid('wfd_base');
                        if (baseEl) state.inputs.wfd_base = baseEl.value;
                    }
                    distCheckIds.forEach(id => {
                        const el = gid(id);
                        if (el) state.checks[id] = !!el.checked;
                    });
                    distSelectIds.forEach(id => {
                        const el = gid(id);
                        if (el) state.selects[id] = el.value;
                    });
                    return state;
                }

                function restoreDpEditableState(state){
                    if (!state) return;
                    Object.entries(state.inputs || {}).forEach(([id, value]) => {
                        const el = gid(id);
                        if (el) el.value = value ?? '';
                    });
                    Object.entries(state.checks || {}).forEach(([id, value]) => {
                        const el = gid(id);
                        if (el) el.checked = !!value;
                    });
                    Object.entries(state.selects || {}).forEach(([id, value]) => {
                        const el = gid(id);
                        if (el) el.value = value ?? '';
                    });
                }

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
                        selects: ['wfd_strategy','wfd_pri1','wfd_pri2','wfd_pri3','wfd_pri4','wfd_gapSource','wfd_scenarioMode','wfd_stressProfile']
                    }
                };
                let hydrating = false;
                function saveMeta(){
                    try { localStorage.setItem(DIST_META_LOCAL_KEY, JSON.stringify(distMeta || {})); } catch (_) { }
                    if (DIST_META_KEY && !disableLocalForDP) savePersistedState(DIST_META_KEY, distMeta);
                }
                async function loadMeta(){
                    let m = null;
                    try { m = JSON.parse(localStorage.getItem(DIST_META_LOCAL_KEY) || 'null'); } catch { m = null; }
                    if ((!m || typeof m !== 'object') && DIST_META_KEY && !disableLocalForDP) {
                        m = await loadPersistedState(DIST_META_KEY);
                    }
                    if (m && typeof m === 'object') {
                        distMeta = {
                            hasValidResults: !!m.hasValidResults,
                            lastStep: m.lastStep || '1',
                            stale: !!m.stale,
                            result: m.result || null,
                            open: !!m.open
                        };
                    }
                }

                // Market scenario helpers
                let wfdScenarioCache = [];
                let wfdScenarioMeta = { mode:'fixed', years:0 };
                // Historical annual market return events (%), 1930-2026 (S&P yearly performance snapshots)
                const HIST_SP500_RETURNS_PCT_1930_2026 = [
                    -28.48,-47.07,-15.15,46.59,-5.94,41.37,27.92,-38.59,25.21,-5.45,
                    -15.29,-17.86,12.43,19.45,13.8,30.72,-11.87,0,-0.65,10.26,
                    21.78,16.46,11.78,-6.62,45.02,26.4,2.62,-14.31,38.06,8.48,
                    -2.97,23.13,-11.81,18.89,12.97,9.06,-13.09,20.09,7.66,-11.36,
                    0.1,10.79,15.63,-17.37,-29.72,31.55,19.15,-11.5,1.06,12.31,
                    25.77,-9.73,14.76,17.27,1.4,26.33,14.62,2.03,12.4,27.25,
                    -6.56,26.31,4.46,7.06,-1.54,34.11,20.26,31.01,26.67,19.53,
                    -10.14,-13.04,-23.37,26.38,8.99,3,13.62,3.53,-38.49,23.45,
                    12.78,0,13.41,29.6,11.39,-0.73,9.54,19.42,-6.24,28.88,
                    16.26,26.89,-19.44,24.23,23.31,16.39,-3.84
                ];
                const HIST_STRESS_BLOCKS = [
                    [-28.48,-47.07,-15.15,46.59],
                    [-38.59,25.21],
                    [-29.72,31.55],
                    [-23.37,26.38],
                    [-38.49,23.45],
                    [-19.44,24.23]
                ];
                function parseManualReturns(txt){
                    return (txt || '').split(/[\n,]+/).map(pf).filter(v => !isNaN(v));
                }
                function generateRandomReturns(years, meanPct, stressProfile){
                    const n = Math.max(years, 1);
                    const hist = HIST_SP500_RETURNS_PCT_1930_2026;
                    const histMean = hist.reduce((s, v) => s + v, 0) / Math.max(hist.length, 1);
                    const targetMean = isFinite(meanPct) ? meanPct : histMean;
                    const profile = String(stressProfile || 'balanced').toLowerCase();
                    const cfg = profile === 'conservative'
                        ? { stressProb: 0.16, negTail: 1.03, rebound: 1.04, meanTilt: 0.48, minLen: 3, lenSpan: 4 }
                        : profile === 'aggressive'
                            ? { stressProb: 0.42, negTail: 1.24, rebound: 1.10, meanTilt: 0.18, minLen: 2, lenSpan: 4 }
                            : { stressProb: 0.28, negTail: 1.12, rebound: 1.08, meanTilt: 0.35, minLen: 2, lenSpan: 4 };
                    // Keep historical shape; only partially tilt toward user-selected expected return.
                    const meanShift = (targetMean - histMean) * cfg.meanTilt;

                    const out = [];
                    while (out.length < n) {
                        // Periodically inject real historical stress/rebound blocks to preserve tail behavior.
                        if (Math.random() < cfg.stressProb) {
                            const block = HIST_STRESS_BLOCKS[Math.floor(Math.random() * HIST_STRESS_BLOCKS.length)];
                            for (let i = 0; i < block.length && out.length < n; i++) {
                                let v = block[i] + meanShift * (block[i] >= 0 ? 0.30 : 0.10);
                                if (v < 0) v *= cfg.negTail;
                                v = Math.max(-55, Math.min(55, v));
                                out.push(v);
                            }
                            continue;
                        }

                        // Block bootstrap from real yearly sequence to keep realistic up/down clustering.
                        const start = Math.floor(Math.random() * hist.length);
                        const len = cfg.minLen + Math.floor(Math.random() * cfg.lenSpan);
                        for (let j = 0; j < len && out.length < n; j++) {
                            const raw = hist[(start + j) % hist.length];
                            let v = raw + meanShift;
                            if (v < 0) v *= Math.max(1, cfg.negTail - 0.02);
                            if (out.length > 0 && out[out.length - 1] <= -20 && v > 0) v *= cfg.rebound;
                            v = Math.max(-55, Math.min(55, v));
                            out.push(v);
                        }
                    }

                    return out.slice(0, n).map(v => Math.round(v * 10) / 10);
                }
                function buildScenarioReturns(years, mode, baseReturnDec, manualTxt, stressProfile){
                    if (years <= 0) return [];
                    const basePct = (baseReturnDec || 0) * 100;
                    if (mode === 'manual'){
                        const vals = parseManualReturns(manualTxt);
                        if (vals.length === 0) return Array(years).fill(baseReturnDec);
                        while (vals.length < years) vals.push(vals[vals.length-1]);
                        return vals.slice(0, years).map(v => v / 100);
                    }
                    if (mode === 'random'){
                        if (wfdScenarioCache.length === years && wfdScenarioMeta.mode === 'random' && wfdScenarioMeta.profile === (stressProfile || 'balanced') && Math.abs((wfdScenarioMeta.basePct ?? basePct) - basePct) < 0.01) {
                            return wfdScenarioCache.map(v => v / 100);
                        }
                        const gen = generateRandomReturns(years, basePct, stressProfile);
                        wfdScenarioCache = gen;
                        wfdScenarioMeta = { mode:'random', years, profile: (stressProfile || 'balanced'), basePct };
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
                    if (gid('wfd_stressProfile') && !gid('wfd_stressProfile').value) gid('wfd_stressProfile').value = 'balanced';
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
                function updateBktAmounts(trigger = '') {
                    const base = pf(gid('wfd_base').value);
                    let inv = pf(gid('wfd_invAlloc').value);
                    let li  = pf(gid('wfd_liAlloc').value);
                    let ann = pf(gid('wfd_annAlloc').value);

                    inv = Math.max(0, Math.min(100, inv));
                    if (String(gid('wfd_invAlloc').value) !== String(inv)) {
                        gid('wfd_invAlloc').value = String(inv);
                    }

                    // Auto-allocation rule (only when Investments changes):
                    // - 100% Investments => Life/Annuity = 0/0
                    // - <100% Investments => split remaining amount 50/50 between Life and Annuity
                    // Users can then manually override Life/Annuity without being forced back,
                    // until Investments is changed again.
                    if (trigger === 'inv' && !distAllocManual) {
                        if (inv >= 100) {
                            li = 0;
                            ann = 0;
                        } else {
                            const remaining = Math.max(0, 100 - inv);
                            li = remaining / 2;
                            ann = remaining - li;
                        }
                        gid('wfd_liAlloc').value = String(li);
                        gid('wfd_annAlloc').value = String(ann);
                    }

                    // Convenience: if Investments set to 100%, zero other buckets automatically
                    if (inv >= 100) {
                        inv = 100;
                        if (li !== 0 || ann !== 0) {
                            li = 0; ann = 0;
                            gid('wfd_liAlloc').value = '0';
                            gid('wfd_annAlloc').value = '0';
                        }
                        distAllocManual = false;
                    }
                    const total = inv + li + ann;

                    const totEl = gid('wfd_allocTotal');
                    const stEl  = gid('wfd_allocStatus');
                    totEl.textContent = total.toFixed(1) + '%';
                    if (Math.abs(total - 100) < 0.11) {
                        totEl.className = 'wfd-alloc-good';
                        stEl.textContent = '✓ Ready';
                        stEl.className = 'wfd-alloc-status wfd-alloc-status--ready';
                    } else {
                        totEl.className = 'wfd-alloc-bad';
                        stEl.textContent = '— must equal 100%';
                        stEl.className = 'wfd-alloc-status wfd-alloc-status--bad';
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
                gid('wfd_invAlloc').addEventListener('input', () => { updateBktAmounts('inv'); dpSaveDebounced(); });
                gid('wfd_liAlloc').addEventListener('input', () => { distAllocManual = true; updateBktAmounts('li'); dpSaveDebounced(); });
                gid('wfd_annAlloc').addEventListener('input', () => { distAllocManual = true; updateBktAmounts('ann'); dpSaveDebounced(); });
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
                        if (dpResultsRef){
                            setSearchResultsVisible(dpResultsRef, false);
                            dpResultsRef.innerHTML = "";
                        }
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
                            if (dpResultsRef){
                                setSearchResultsVisible(dpResultsRef, false);
                                dpResultsRef.innerHTML = "";
                            }
                            return;
                        }
                        if (dpResultsRef){
                            const frag = document.createDocumentFragment();
                            list.forEach(item => {
                                const btn = document.createElement('button');
                                btn.type = "button";
                                btn.className = "list-group-item list-group-item-action finance-search-result";
                                btn.innerHTML = `
                                    <span class="lf-ui-069">${item.displayName || "Client"}</span>
                                    <span class="lf-ui-070">${item.email || "—"}${item.phone ? " · " + item.phone : ""}</span>
                                    <span class="finance-search-result__note ${item.hasSavedPlan ? 'finance-search-result__note--saved' : 'finance-search-result__note--empty'}">${item.hasSavedPlan ? 'Plan saved' : 'No plan yet'}</span>
                                `;
                                btn.addEventListener('click', async ()=>{ await selectActiveClient(item); });
                                frag.appendChild(btn);
                            });
                            dpResultsRef.replaceChildren(frag);
                            setSearchResultsVisible(dpResultsRef, true);
                        }
                        if (statusEl){ statusEl.textContent = `Found ${list.length}. Select to load.`; statusEl.classList.remove('text-danger'); }
                    }catch(err){
                        // AbortError is expected when the user keeps typing; suppress noise.
                        if (err?.name === 'AbortError') return;
                        if (statusEl){ statusEl.textContent = err?.message || "Search failed."; statusEl.classList.add('text-danger'); }
                        if (dpResultsRef){
                            setSearchResultsVisible(dpResultsRef, false);
                        }
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
                        const legacyBlock = ['wfd_strategy','wfd_pri1','wfd_pri2','wfd_pri3','wfd_pri4','wfd_gapSource','wfd_scenarioMode','wfd_stressProfile'];
                        if (fromCrm && legacyBlock.includes(id)) return; // CRM cannot override strategy/scenario
                        el.value = selects[id];
                    });
                    distAllocManual = true;
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
                const dpCrmReadEnabled = true; // DP auto-loads selected client's saved distribution data.
                const dpCrmWriteEnabled = false; // DP edits stay local on Finance page.

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
                    const built = { inputs:{}, checks:{}, selects:{}, meta: dist.meta || {} };
                    const checkSet = new Set(distCheckIds);
                    const selectSet = new Set(distSelectIds);

                    if (dist.inputs && typeof dist.inputs === 'object') Object.assign(built.inputs, dist.inputs);
                    if (dist.checks && typeof dist.checks === 'object') Object.assign(built.checks, dist.checks);
                    if (dist.selects && typeof dist.selects === 'object') Object.assign(built.selects, dist.selects);

                    const canonical = dist.canonicalInput && typeof dist.canonicalInput === 'object'
                        ? dist.canonicalInput
                        : null;

                    if (canonical) {
                        const mapInput = (field, id, transform = (v) => v) => {
                            if (canonical[field] === undefined || canonical[field] === null) return;
                            built.inputs[id] = transform(canonical[field]);
                        };
                        const mapCheck = (field, id) => {
                            if (canonical[field] === undefined || canonical[field] === null) return;
                            built.checks[id] = !!canonical[field];
                        };
                        const mapSelect = (field, id) => {
                            if (canonical[field] === undefined || canonical[field] === null) return;
                            built.selects[id] = canonical[field];
                        };

                        mapCheck('manualBaseOverride', 'wfd_manualOverride');
                        if (canonical.manualBaseOverride) mapInput('retirementBase', 'wfd_base');
                        mapInput('retireAge', 'wfd_retAge');
                        mapInput('endAge', 'wfd_endAge');
                        mapInput('emergencyReserve', 'wfd_emergency');
                        mapInput('desiredIncome', 'wfd_desiredIncome');
                        mapInput('guaranteedIncome', 'wfd_guaranteedIncome');

                        mapInput('invAllocPct', 'wfd_invAlloc');
                        mapInput('invReturnPct', 'wfd_invReturn');
                        mapInput('invTaxPct', 'wfd_invTax');
                        mapCheck('invDownMarket', 'wfd_invDownMkt');

                        mapInput('liAllocPct', 'wfd_liAlloc');
                        mapInput('liReturnPct', 'wfd_liGrowth');
                        mapInput('liTaxPct', 'wfd_liTax');
                        mapInput('liEfficiencyPct', 'wfd_liEfficiency');
                        mapInput('liDeathBenefit', 'wfd_liDeath');
                        mapCheck('liDownMarket', 'wfd_liDownMkt');
                        mapSelect('liPolicyType', 'wfd_liType');
                        mapSelect('liAccessMode', 'wfd_liAccess');

                        mapInput('annAllocPct', 'wfd_annAlloc');
                        mapInput('annReturnPct', 'wfd_annReturn');
                        mapInput('annTaxPct', 'wfd_annTax');
                        mapInput('annDeathBenefit', 'wfd_annDeath');
                        mapInput('annRollupPct', 'wfd_annRollup');
                        mapCheck('annDownMarket', 'wfd_annDownMkt');
                        mapCheck('annIncomeRider', 'wfd_annIncomeRider');
                        mapCheck('annDbRider', 'wfd_annDbRider');
                        mapSelect('annDesign', 'wfd_annDesign');

                        mapCheck('protectInvest', 'wfd_protectInvest');
                        mapSelect('strategy', 'wfd_strategy');
                        mapSelect('gapSource', 'wfd_gapSource');
                        mapSelect('scenarioMode', 'wfd_scenarioMode');
                        mapInput('downThreshold', 'wfd_downThreshold');
                        if (Array.isArray(canonical.manualReturns)) {
                            built.inputs.wfd_manualReturns = canonical.manualReturns.join(', ');
                        }

                    }

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
                    if (!dpCrmReadEnabled) {
                        if (statusEl) statusEl.textContent = "DP uses local/session state only (CRM load disabled).";
                        dpPlanLoaded = true;
                        syncBase();
                        updateBktAmounts();
                        if (initAfter) distInitAfterHydrate();
                        return;
                    }
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
                        if (statusEl) {
                            const loadedTxt = data.updatedUtc ? `Loaded (updated ${new Date(data.updatedUtc).toLocaleString()})` : "Loaded";
                            statusEl.textContent = dpCrmWriteEnabled ? loadedTxt : `${loadedTxt} • DP edits are local only`;
                        }
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
                    if (!dpCrmWriteEnabled) {
                        const statusEl = document.getElementById('dpPlanStatus');
                        if (statusEl) statusEl.textContent = "DP edits are local only (CRM write-back disabled).";
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
                    if (!dpCrmWriteEnabled) return;
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
                        btn.classList.toggle('is-selected', strat === val);
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
                    distAllocManual = false;
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
                gid('wfd_clearStep2')?.addEventListener('click', () => { clearStep('step2'); clearStep('step3'); });

                function clearStep(stepKey){
                    const sets = stepFieldSets[stepKey];
                    if (!sets) return;
                    sets.inputs.forEach(id => { const el = gid(id); if (el) el.value = ''; });
                    sets.checks.forEach(id => { const el = gid(id); if (el) el.checked = false; });
                    sets.selects.forEach(id => { const el = gid(id); if (el) el.value = ''; });
                    // Restore defaults for specific toggles when clearing step context
                    if (stepKey === 'step2') {
                        distAllocManual = false;
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
                        const profile = gid('wfd_stressProfile'); if (profile && !profile.value) profile.value = 'balanced';
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
                    const dpSession = loadDpUiSession();
                    const restoreClientId = (dpSession.activeClientId || '').trim();
                    const restoreClientName = (dpSession.activeClientName || '').trim();
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

                    if (restoreClientId) {
                        wfActiveClientId = restoreClientId;
                        dpActiveClientId = restoreClientId;
                        if (wfSearchInput) wfSearchInput.value = restoreClientName || restoreClientId;
                        if (dpSearchInputRef) dpSearchInputRef.value = restoreClientName || restoreClientId;
                        await loadWfPlan(restoreClientId);
                        if (dpCrmReadEnabled) await loadDpPlan(restoreClientId);
                    }

                    let startStep = dpSession.lastStep || distMeta.lastStep || '1';
                    if (startStep === '4') startStep = '3';
                    else if (startStep === '3' && !distMeta.hasValidResults) startStep = '2';
                    setStep(startStep); // internally calls hydrateResultsFromMeta if step === '3'
                    const shouldOpenDp = !!(dpSession.modalOpen || distMeta.open);
                    if (shouldOpenDp) {
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
                    const stressProfile = gid('wfd_stressProfile')?.value || 'balanced';
                    const list = generateRandomReturns(yrs, basePct, stressProfile);
                    wfdScenarioCache = list;
                    wfdScenarioMeta = { mode:'random', years: yrs, profile: stressProfile, basePct };
                    const area = gid('wfd_manualReturns');
                    if (area) area.value = list.map(v=>v.toFixed(1)).join(', ');
                    gid('wfd_scenarioMode').value = 'random';
                    saveDistState();
                });
                const manualArea = gid('wfd_manualReturns');
                if (manualArea) manualArea.addEventListener('input', saveDistStateDebounced);
                ['wfd_gapSource','wfd_scenarioMode','wfd_stressProfile'].forEach(id=>{
                    const el = gid(id); if (el) el.addEventListener('change', saveDistStateDebounced);
                });

                const goResults = () => setStep('3', { skipHydrate: true });

                function renderEmptyResults(){
                    const ctaHtml = `
                        <div class="lf-ui-141">
                          <button id="wfd_emptyRun" class="wfd-calc-btn lf-ui-120" type="button">Run Plan</button>
                          <button id="wfd_emptyStrategy" class="wfd-calc-btn wfd-secondary lf-ui-120" type="button">Go to Strategy</button>
                        </div>`;
                    const msg = `<div class="lf-ui-142">Run the plan to view results, funding analysis, and stress-test outputs.${ctaHtml}</div>`;
                    const resGrid = gid('wfd_resGrid'); if (resGrid) resGrid.innerHTML = msg;
                    const src = gid('wfd_sourceBreak'); if (src) src.innerHTML = '';
                    const legacyTiles = gid('wfd_legacyTiles');
                    if (legacyTiles) legacyTiles.innerHTML = '';
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
                    if (stratBtn) stratBtn.onclick = () => setStep('2');
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

                    // End-of-plan legacy tiles (displayed in Year-by-Year Audit above bucket tiles)
                    const series = result.chart?.series || {};
                    const lastOf = (arr) => Array.isArray(arr) && arr.length ? Number(arr[arr.length - 1]) || 0 : 0;
                    const invLeft = Math.max(0, lastOf(series.inv));
                    const lifeDeathBenefitLeft = Math.max(0, lastOf(series.liDeath));
                    const annuityDeathBenefitLeft = Math.max(0, lastOf(series.annDeath));
                    const totalLegacyLeft = invLeft + lifeDeathBenefitLeft + annuityDeathBenefitLeft;
                    const legacyTiles = gid('wfd_legacyTiles');
                    if (legacyTiles) {
                        const tile = (label, value, toneClass = 'wfd-tone-white') => `
                            <div class="lf-ui-143">
                                <div class="lf-ui-144">${label}</div>
                                <div class="wfd-legacy-value ${toneClass}">${fmtD(value)}</div>
                            </div>`;
                        const op = (symbol) => `<div class="lf-ui-145">${symbol}</div>`;
                        legacyTiles.innerHTML = `
                            <div class="lf-ui-146">
                                ${tile('Investments Left (End of Plan)', invLeft, 'wfd-tone-blue')}
                                ${op('+')}
                                ${tile('Life Insurance Death Benefit Left', lifeDeathBenefitLeft, 'wfd-tone-gold')}
                                ${op('+')}
                                ${tile('Annuities Death Benefit Left', annuityDeathBenefitLeft, 'wfd-tone-green')}
                                ${op('=')}
                                ${tile('Total Legacy Left (Combined)', totalLegacyLeft, 'wfd-tone-green')}
                            </div>`;
                    }

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
                          <div class="lf-ui-147">
                            <table class="lf-ui-148">
                              <thead class="lf-ui-149">
                                <tr>
                                  <th class="lf-ui-150">Age</th>
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
                              <tbody>${rows || `<tr><td class="lf-ui-151" colspan="9">No data</td></tr>`}</tbody>
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
                              <div class="lf-ui-152">
                                ${activeDefs.map(def => {
                                    const st = bktStats[def.key];
                                      const longevity = st.depAge ? `Depletes Age ${st.depAge}` : `Lasts to Age ${summary.endAge}`;
                                      const toneClass = def.key === 'inv' ? 'wfd-tone-blue' : def.key === 'li' ? 'wfd-tone-gold' : 'wfd-tone-green';
                                      const tileClass = def.key === 'inv' ? 'wfd-bkt-tile--inv' : def.key === 'li' ? 'wfd-bkt-tile--li' : 'wfd-bkt-tile--ann';
                                      const statusToneClass = st.lastStatus === 'Lapsed' ? 'wfd-tone-red' : st.lastStatus === 'At Risk' ? 'wfd-tone-amber' : 'wfd-tone-green';
                                      const longevityToneClass = st.depAge ? 'wfd-tone-red' : 'wfd-tone-green';
                                      return `<button
                                    class="wfd-bkt-tile ${tileClass}"
                                    data-bkt="${def.key}">
                                    <div class="wfd-bkt-tile__heading ${toneClass}">${def.label}</div>
                                    ${def.key === 'li' && result.liType === 'legacy_rpu' ? `<div class="lf-ui-153">Legacy only — not used for income</div>` : ''}
                                    ${def.key === 'li' ? `<div class="wfd-bkt-tile__status ${statusToneClass}">Status: ${st.lastStatus || 'Active'}</div>` : ''}
                                    ${def.key === 'ann' ? `<div class="lf-ui-154">Design: ${annDesignDisplay}${hasIncRider && annRollupPct !== null ? ` · Rollup ${annRollupPct.toFixed(1)}%` : ''}</div>` : ''}
                                      <div class="lf-ui-155">Start</div>
                                      <div class="lf-ui-156">${fmtD(st.firstStart)}</div>
                                      ${(def.key === 'li' || def.key === 'ann') && ((st.firstDeath ?? 0) > 0 || (st.lastDeath ?? 0) > 0) ? `
                                      <div class="lf-ui-157">
                                        <div>
                                          <div class="lf-ui-158">Death Benefit Start</div>
                                          <div class="wfd-bkt-tile__value ${toneClass}">${fmtD(st.firstDeath)}</div>
                                        </div>
                                        <div>
                                          <div class="lf-ui-158">Death Benefit End</div>
                                          <div class="wfd-bkt-tile__value ${toneClass}">${fmtD(st.lastDeath)}</div>
                                        </div>
                                      </div>` : ''}
                                      <div class="lf-ui-159">
                                        <div>
                                          <div class="lf-ui-158">Total Gross W/D</div>
                                          <div class="lf-ui-160">${fmtD(st.totalW)}</div>
                                        </div>
                                        <div>
                                          <div class="lf-ui-158">Remaining</div>
                                          <div class="lf-ui-161">${fmtD(st.lastEnd)}</div>
                                        </div>
                                        <div>
                                          <div class="lf-ui-158">Yrs Used</div>
                                          <div class="lf-ui-162">${st.yearsUsed}</div>
                                        </div>
                                        ${def.key === 'li' ? `
                                        <div>
                                          <div class="lf-ui-158">Gross DB</div>
                                          <div class="lf-ui-163">${fmtD(st.lastDeath)}</div>
                                        </div>
                                        <div>
                                          <div class="lf-ui-158">Loan Balance</div>
                                          <div class="lf-ui-164">${fmtD(st.lastLoan)}</div>
                                        </div>
                                        <div>
                                          <div class="lf-ui-158">Net DB</div>
                                          <div class="lf-ui-161">${fmtD(st.lastNetDeath)}</div>
                                        </div>` : ''}
                                      </div>
                                      <div class="wfd-bkt-tile__longevity ${longevityToneClass}">${longevity}</div>
                                      <div class="wfd-bkt-tile__subnote ${toneClass}">View Breakdown →</div>
                                    </button>`;
                                }).join('')}
                              </div>`;

                            // Bucket drill-down modal — built once, reused
                            const DRILL_ID = 'wfd_bktDrill';
                            if (!document.getElementById(DRILL_ID)) {
                                const drillEl = document.createElement('div');
                                drillEl.id = DRILL_ID;
                                drillEl.classList.add('lf-js-038');
                                drillEl.innerHTML = `
                                  <div class="lf-ui-165" id="wfd_bktDrill_panel">
                                    <div class="lf-ui-166" id="wfd_bktDrill_hdr">
                                      <button class="lf-ui-167" id="wfd_bktDrill_close">×</button>
                                      <div class="lf-ui-168" id="wfd_bktDrill_title"></div>
                                      <div class="lf-ui-169" id="wfd_bktDrill_sub"  ></div>
                                    </div>
                                    <div class="lf-ui-170">
                                      <div class="lf-ui-171" id="wfd_bktDrill_stats"></div>
                                      <div class="lf-ui-172" id="wfd_bktDrill_chartWrap"></div>
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
                                    { l: 'Total Withdrawn',   v: fmtD(st.totalW), cls: 'wfd-tone-red' },
                                    { l: def.key === 'ann' ? 'Remaining Annuity' : def.key === 'li' ? 'Remaining Cash Value' : 'Remaining Balance', v: fmtD(st.lastEnd), cls: 'wfd-tone-green' }
                                ];
                                if ((def.key === 'li' || def.key === 'ann') && ((st.firstDeath ?? 0) > 0 || (st.lastDeath ?? 0) > 0)) {
                                    statCards.splice(1, 0,
                                        { l: 'Death Benefit Start', v: fmtD(st.firstDeath) },
                                        { l: 'Death Benefit End (Gross)',   v: fmtD(st.lastDeath), cls: 'wfd-tone-gold' }
                                    );
                                }
                                if (def.key === 'li') {
                                    statCards.push({ l: 'Outstanding Loan', v: fmtD(st.lastLoan || 0), cls:'wfd-tone-amber' });
                                    statCards.push({ l: 'Death Benefit Net', v: fmtD(st.lastNetDeath || st.lastDeath || 0), cls:'wfd-tone-green' });
                                    statCards.push({ l: 'Loan Mechanics', v: 'Loans reduce net DB; cash value keeps growing.' });
                                    statCards.push({ l: 'Policy Status', v: st.lastStatus || 'Active', cls: st.lastStatus === 'Lapsed' ? 'wfd-tone-red' : st.lastStatus === 'At Risk' ? 'wfd-tone-amber' : 'wfd-tone-green' });
                                }
                                if (def.key === 'ann') {
                                    statCards.push({ l: 'Annuity Design', v: annDesignDisplay });
                                    if (hasIncRider && annRollupPct !== null) {
                                        statCards.push({ l: 'Income Rider Rollup', v: `${annRollupPct.toFixed(1)}%` });
                                    }
                                }
                                statCards.push(
                                    { l: 'Years Used',        v: `${st.yearsUsed} / ${rows.length}` },
                                    { l: 'Longevity',         v: longevityTxt, cls: st.depAge ? 'wfd-tone-red' : 'wfd-tone-green' }
                                );
                                if (def.key === 'li') {
                                    statCards.push({ l: 'Policy Design', v: lifeDesignLabel });
                                }
                                document.getElementById('wfd_bktDrill_stats').innerHTML = statCards.map(c =>
                                    `<div class="lf-ui-173">
                                       <div class="lf-ui-174">${c.l}</div>
                                       <div class="wfd-kpi-value ${c.cls || 'wfd-tone-white'}">${c.v}</div>
                                     </div>`
                                ).join('');

                                // Mini chart
                                const chartWrap = document.getElementById('wfd_bktDrill_chartWrap');
                                chartWrap.innerHTML = '<canvas class="lf-ui-175" id="wfd_bktDrill_canvas"></canvas>';
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
                                    const rateToneClass = rate !== null && rate < -0.001 ? 'wfd-audit-cell--negative' : rate !== null && rate > 0.001 ? 'wfd-audit-cell--positive' : 'wfd-audit-cell--neutral';
                                    const withdrawalToneClass = used ? 'wfd-audit-cell--used' : 'wfd-audit-cell--inactive';
                                    const growthToneClass = growth !== null ? (growth < -0.001 ? 'wfd-audit-cell--negative' : 'wfd-audit-cell--positive') : 'wfd-audit-cell--neutral';
                                    const riderIncome = r.ann?.riderIncome ?? null;
                                    const riderCharge = r.ann?.charges ?? null;
                                    const annNetToPlan = r.ann?.fundedNet ?? null;
                                    return `<tr class="wfd-audit-row${used ? '' : ' is-dim'}">
                                      <td class="lf-ui-176">${r.age}</td>
                                      <td class="lf-ui-176">${st0 !== null ? fmtD(st0) : '—'}</td>
                                      ${isLife || isAnn ? `<td class="lf-ui-176">${deathStart !== null ? fmtD(deathStart) : '—'}</td>` : ''}
                                      ${isLife ? `<td class="lf-ui-176">${loanBal !== null ? fmtD(loanBal) : '—'}</td>` : ''}
                                      <td class="wfd-audit-cell ${rateToneClass}">${rate !== null ? rate.toFixed(1) + '%' : '—'}</td>
                                      <td class="wfd-audit-cell ${withdrawalToneClass}">${used ? fmtD(w) : '—'}</td>
                                      ${isLife || isAnn ? `<td class="wfd-audit-cell ${growthToneClass}">${growth !== null ? fmtD(growth) : '—'}</td>` : ''}
                                      ${isLife ? `<td class="lf-ui-176">${r.life?.charges ? fmtD(r.life.charges) : (used ? '$0' : '—')}</td>` : ''}
                                      ${isAnn ? `<td class="lf-ui-176">${riderIncome !== null && riderIncome !== 0 ? fmtD(riderIncome) : (used ? '$0' : '—')}</td>` : ''}
                                      ${isAnn ? `<td class="lf-ui-176">${riderCharge !== null && Math.abs(riderCharge) > 1e-6 ? fmtD(riderCharge) : (used ? '$0' : '—')}</td>` : ''}
                                      <td class="lf-ui-176">${end !== null ? fmtD(end) : '—'}</td>
                                      ${isLife || isAnn ? `<td class="lf-ui-176">${deathEnd !== null ? fmtD(deathEnd) : '—'}</td>` : ''}
                                      ${isLife ? `<td class="lf-ui-176">${netDeath !== null ? fmtD(netDeath) : '—'}</td>` : ''}
                                      ${isLife ? `<td class="lf-ui-176">${r.life?.status || '—'}</td>` : ''}
                                      ${isAnn ? `<td class="lf-ui-176">${annNetToPlan !== null ? fmtD(annNetToPlan) : '—'}</td>` : ''}
                                      <td class="lf-ui-176">${used ? '<span class="lf-ui-177">Yes</span>' : '<span class="lf-ui-178">—</span>'}</td>
                                    </tr>`;
                                }).join('');
                                document.getElementById('wfd_bktDrill_table').innerHTML = `
                                  <div class="lf-ui-179">
                                    <table class="lf-ui-180">
                                      <thead class="lf-ui-181">
                                        <tr>
                                          ${hdrCells.map(h => `<th class="lf-ui-182">${h}</th>`).join('')}
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
                            if (chartCanvas) chartCanvas.outerHTML = '<div class="lf-ui-183">Chart unavailable. Please retry or check your connection.</div>';
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
                    if (activeStep === '2') {
                        // If we already have a valid run, jump straight to Results; otherwise trigger a run.
                        if (distMeta.hasValidResults && distMeta.result) {
                            setStep('3');
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
                    const lockedInputs = captureDpEditableState();
                    const preErrs = validateDist();
                    showBlock(preErrs);
                    if (preErrs.length) return;

                    try {
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
                    const stressProfile = gid('wfd_stressProfile')?.value || 'balanced';
                    const downThreshold = pf(gid('wfd_downThreshold').value) / 100;
                    const manualReturnTxt = gid('wfd_manualReturns').value || '';
                    const priOrder      = getPriorityOrder();
                    const scenarioReturns = buildScenarioReturns(years, scenarioMode, invReturn, manualReturnTxt, stressProfile);
                    const annVarReturns = generateRandomReturns(years, annReturn * 100, 'balanced').map(v => v / 100);

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
                    let totalAnnGrossFunded = 0;
                    let totalBucketNetFunded = 0;
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

                        // Annuity design-driven growth (product-specific rules)
                        const annBaseVarR = (annVarReturns[y-1] !== undefined ? annVarReturns[y-1] : annReturn);
                        let annYearR = annReturn;
                        if (annDesign === 'fixed') {
                            // Fixed annuity: declared/guaranteed credited rate
                            annYearR = Math.max(annReturn, -0.99);
                        } else if (annDesign === 'fixedIndexed') {
                            // FIA: market-linked with floor/cap/participation
                            const floor = 0.00;
                            const cap = Math.max(0, annReturn);   // uses configured APR as cap proxy
                            const participation = 0.85;            // standard planning assumption
                            const indexedCredit = annBaseVarR * participation;
                            annYearR = Math.min(cap, Math.max(floor, indexedCredit));
                        } else if (annDesign === 'variable') {
                            // Variable annuity: market-linked subaccount return
                            annYearR = annBaseVarR;
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
                                    const liTaxSplit = liAccess === 'loan' ? 0 : liTax;
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
                        totalAnnGrossFunded += annGrossContribution;
                        totalBucketNetFunded += fundedNet;
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
                        const annDeathPre = annDeathBal;

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
                        // Life death benefit behavior (model assumption):
                        // - APR/crediting compounds CASH VALUE (liBal), not death benefit by default.
                        // - Gross death benefit is level unless directly reduced by distributions.
                        // - Policy loans reduce NET death benefit separately via outstanding loan balance.
                        // This is applied consistently across Whole, IUL, VUL, and Legacy RPU in this planner.
                        if (liAccess === 'withdrawal') {
                            liDeathBal = Math.max(0, liDeathPre - liW);
                        } else {
                            liDeathBal = Math.max(0, liDeathPre);
                        }
                        if (annDbRider) {
                            // Death-benefit rider model:
                            // distributions reduce rider base, then high-water-mark ratchet can step it up.
                            const annDistForDb = annW + riderPaidFromAccount;
                            annRiderBase = Math.max(0, annRiderBase - annDistForDb);
                            annRiderBase = Math.max(annRiderBase, annBal);
                            annDeathBal = Math.max(annBal, annRiderBase);
                        } else {
                            // No rider: annuity death value follows account value.
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
                            liRatePct: effLiR * 100,
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
                    const totalNetW = net_invW + net_liW + net_annW + net_emW; // year-1 after-tax from asset buckets only
                    const totalGrW  = fy_invW + fy_liW + annGrossContributionFY + fy_emW; // year-1 gross from asset buckets only
                    const firstYearShortfall = year1Shortfall;
                    // --- Horizon-wide tracking ---
                    const shortfall = firstYearShortfall; // single source of truth for Yr1 shortfall
                    const sourcedAfterTax = totalNetW;
                    const atSpend   = guarInc + sourcedAfterTax; // total after-tax spendable including guaranteed income
                    const totalGuarIncome = guarInc * years;
                    const totalGrossSourced = totalInvDraw + totalLiDraw + totalAnnGrossFunded + totalEmUsed;
                    const totalSpendableAllYears = totalBucketNetFunded + totalGuarIncome;
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
                        { l: 'Desired Annual Income',      v: fmtD(desiredInc),   c: 'gold' },
                        { l: 'Guaranteed Income (after-tax)',          v: fmtD(guarInc),      c: 'green' },
                        { l: 'Income Gap (from Assets)',   v: fmtD(incGap),       c: 'orange' },
                        active.em  ? { l: 'Plan Emergency W/D (Gross)',         v: fmtD(totalEmUsed),  c: 'red' } : null,
                        active.inv ? { l: 'Plan Investments W/D (Gross)', v: fmtD(totalInvDraw), c: 'red' } : null,
                        active.li  ? { l: 'Plan Life Ins W/D (Gross)',    v: fmtD(totalLiDraw),  c: 'red' } : null,
                        active.ann ? { l: 'Plan Annuity Funding (Gross)',     v: fmtD(totalAnnGrossFunded), c: 'red' } : null,
                        { l: 'Plan Gross Sourced',     v: fmtD(totalGrossSourced),     c: 'red' },
                        { l: 'Plan Spendable (After-Tax)',  v: fmtD(totalSpendableAllYears),      c: incomeSufficient ? 'green' : 'red' },
                        { l: 'First-Year Shortfall',       v: fmtD(firstYearShortfall), c: firstYearShortfall > shortfallTol ? 'red' : 'green' },
                        { l: 'Cumulative Shortfall',       v: fmtD(cumulativeShortfall), c: cumulativeShortfall > 0 ? 'red' : 'green' },
                        { l: 'Any-Year Funding Failure',   v: anyYearFailure ? 'Yes' : 'No', c: anyYearFailure ? 'red' : 'green' },
                        { l: 'Last Continuous Funded Year',      v: lastFundedAge ? `Age ${lastFundedAge}` : '—', c: anyYearFailure ? 'orange' : 'green' },
                        { l: 'Asset Longevity',            v: assetsLast ? `Lasts to Age ${endAge}` : `Depletes @ Age ${depAge}`, c: assetsLast ? 'green' : 'red' },
                        { l: 'Income Sufficiency',         v: incomeSufficient ? `Fully funded to Age ${endAge}` : (failAge ? `Income fails @ Age ${failAge}` : `Income fails`), c: incomeSufficient ? 'green' : 'red' },
                    ].filter(Boolean);
                    // --- Source parts (used in canonical result) ---
                    const srcParts = [];
                    if (active.em)  srcParts.push(`From Emergency (plan gross): ${fmtD(totalEmUsed)}`);
                    if (active.inv) srcParts.push(`From Investments (plan gross): ${fmtD(totalInvDraw)}`);
                    if (active.li)  srcParts.push(`From Life Insurance (plan gross): ${fmtD(totalLiDraw)}`);
                    if (active.ann) srcParts.push(`From Annuities (plan gross): ${fmtD(totalAnnGrossFunded)}`);
                    srcParts.push(`Total Gross Sourced (plan): ${fmtD(totalGrossSourced)}`);
                    srcParts.push(`After-Tax from Buckets (plan): ${fmtD(totalBucketNetFunded)}`);
                    srcParts.push(`Guaranteed Income (plan after-tax): ${fmtD(totalGuarIncome)}`);
                    srcParts.push(`Total Spendable (plan after-tax): ${fmtD(totalSpendableAllYears)}`);
                    if (cumulativeShortfall>0) srcParts.push(`Unfunded Shortfall (plan): ${fmtD(cumulativeShortfall)}`);
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
                        warns.push({ type:'info', msg:`Historical ${stressProfile} stress profile path is an illustration for stress-testing only — not a prediction or guarantee.` });

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
                        barValues: { em: totalEmUsed, inv: totalInvDraw, li: totalLiDraw, ann: totalAnnGrossFunded },
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
                    distMeta.lastStep = '3';
                    distMeta.result = result;
                    saveMeta();
                    renderResults(result, false);

                    // Save state without flagging stale
                    hydrating = true;
                    saveDistState();
                    hydrating = false;

                        goResults();
                    } finally {
                        restoreDpEditableState(lockedInputs);
                        syncBase();
                        updateYrs();
                        updateGap();
                        updateBktAmounts();
                        updateDMState();
                        togglePriorityRow();
                        toggleAnnRollup();
                    }
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
    try {
    const renderSavingsAcceleratorInstance = async (renderToolId, hostElement) => {
    const isBusinessSA = renderToolId === "BusinessSavingsAccelerator";
    const isDualPanel = hostElement.classList.contains('expense-lens-dual-panel');
    const prefix = isBusinessSA ? 'bsa' : 'sa';
    const pid = (name) => `${prefix}${name}`;
    const saStateId = isBusinessSA ? "BusinessSavingsAccelerator" : "SavingsAccelerator";
    const savingsToolStateId = saStateId; // alias
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

        const state = await loadPersistedState(savingsToolStateId);

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

        // ==========================================================
        // COLOR CODING — INPUTS + OUTPUTS + ROWS
        // ==========================================================

        // Source field + outputs
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
        clearPersistedState(savingsToolStateId);
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

            // Premium blue due date field
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

        // ✅ Force style application after DOM paint (this is what kills the "refresh page" issue)
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

    const applyProfileToDebtClarity = () => {
        const prof = window.LegendFinanceProfile?.get?.();
        if (!prof) return;
        if (dcIncome && !dcIncome.value) {
            const monthly = prof.monthlyGross || prof.monthlyNet;
            dcIncome.value = monthly ? (monthly * 12).toLocaleString() : '';
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

    calcDebtClarity();
    applyProfileToDebtClarity();
    toolContext.onWindow("FinanceProfile:updated", applyProfileToDebtClarity);
    toolContext.onWindow("FinanceProfile:ready", applyProfileToDebtClarity);
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

        if (window.LegendFinanceProfile?.update) {
            window.LegendFinanceProfile.update({
                emergencyTarget: bills * 6 || undefined
            });
        }

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

    updateBuffer();
    applyProfileToFinancialBuffer();
    toolContext.onWindow("FinanceProfile:updated", applyProfileToFinancialBuffer);
    toolContext.onWindow("FinanceProfile:ready", applyProfileToFinancialBuffer);
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

        // Outputs: projections are "wealth"
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

    [dapA, dapL].forEach(input => {
        input.addEventListener('input', updateDAP);
        input.addEventListener('blur', () => {
            input.value = formatWithCommas(parseNumber(input.value));
            updateDAP();
        });
    });

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
