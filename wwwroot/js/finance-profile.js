(() => {
    const toolId = "FinanceProfile";

    const byId = (id) => document.getElementById(id);
    const drawer = () => document.getElementById("financeDataDrawer");
    const triggerBtn = () => document.getElementById("btnFinanceDataDrawer");
    const statusEl = () => document.getElementById("financeDataStatus");

    const financeRoot = document.getElementById("financeRoot");
    const clientProfileId = financeRoot?.dataset.clientProfileId?.trim() || "";
    const clientUserId = financeRoot?.dataset.clientUserId?.trim() || "";
    const canUseServerState = !!(clientProfileId || clientUserId);
    const scopeKey = (key) => `finance-profile:${clientUserId || clientProfileId || "agent"}:${key}`;
    const getPersistence = () => window.LegendFinancePersistence;

    let currentState = {};
    const debounce = (fn, wait = 350) => {
        let t;
        return (...args) => {
            clearTimeout(t);
            t = setTimeout(() => fn(...args), wait);
        };
    };

    const fields = [
        { id: "fdMonthlyNet", key: "monthlyNet" },
        { id: "fdMonthlyGross", key: "monthlyGross" },
        { id: "fdFixedExpenses", key: "fixedExpenses" },
        { id: "fdVariableBudget", key: "variableBudget" },
        { id: "fdDebtMins", key: "debtMinimums" },
        { id: "fdSavingsGoal", key: "savingsGoal" },
        { id: "fdEmergencyTarget", key: "emergencyTarget" },
        { id: "fdEmergencyBalance", key: "emergencyBalance" }
    ];

    const setStatus = (msg, tone = "muted") => {
        const el = statusEl();
        if (!el) return;
        el.textContent = msg || "";
        el.className = `finance-data-status tone-${tone}`;
    };

    const fillForm = (state) => {
        fields.forEach(({ id, key }) => {
            const input = byId(id);
            if (input) input.value = state?.[key] ?? "";
        });
        renderExpenses(state?.expenses);
        currentState = { ...(state || {}) };
    };

    const readForm = () => {
        const obj = {};
        fields.forEach(({ id, key }) => {
            const input = byId(id);
            if (input) obj[key] = input.value || "";
        });
        obj.expenses = readExpenses();
        return obj;
    };

    const openDrawer = () => {
        const d = drawer();
        if (!d) return;
        d.classList.remove("d-none");
        d.setAttribute("aria-hidden", "false");
        document.body.classList.add("finance-data-open");
    };
    const closeDrawer = () => {
        const d = drawer();
        if (!d) return;
        d.classList.add("d-none");
        d.setAttribute("aria-hidden", "true");
        document.body.classList.remove("finance-data-open");
    };

    const buildQuery = () => {
        const params = new URLSearchParams({ toolId });
        if (clientUserId) params.set("clientUserId", clientUserId);
        if (clientProfileId) params.set("clientProfileId", clientProfileId);
        return params.toString();
    };

    const getAntiForgeryToken = () =>
        document.querySelector('#__af input[name="__RequestVerificationToken"]')?.value
        || document.querySelector('input[name="__RequestVerificationToken"]')?.value
        || "";

    // --------------- Expense rows -----------------
    const expensesList = () => document.getElementById("fdExpensesList");
    const addExpenseButton = () => document.getElementById("btnAddExpense");

    function createExpenseRow(name = "", amount = "") {
        const row = document.createElement("div");
        row.className = "fd-expense-row";
        row.innerHTML = `
            <input type="text" placeholder="Expense name" value="${name || ""}" aria-label="Expense name" />
            <input type="number" step="0.01" inputmode="decimal" placeholder="Amount" value="${amount || ""}" aria-label="Expense amount" />
            <button type="button" class="fd-expense-remove" aria-label="Remove expense">Remove</button>
        `;
        row.querySelector(".fd-expense-remove")?.addEventListener("click", () => {
            row.remove();
        });
        return row;
    }

    function renderExpenses(expenses) {
        const list = expensesList();
        if (!list) return;
        list.innerHTML = "";
        const items = Array.isArray(expenses) && expenses.length ? expenses : [{}, {}, {}];
        items.forEach(item => {
            const { name = "", amount = "" } = item || {};
            list.appendChild(createExpenseRow(name, amount));
        });
    }

    function readExpenses() {
        const list = expensesList();
        if (!list) return [];
        const rows = Array.from(list.querySelectorAll(".fd-expense-row"));
        return rows.map(row => {
            const [nameInput, amountInput] = row.querySelectorAll("input");
            return {
                name: nameInput?.value?.trim() || "",
                amount: amountInput?.value || ""
            };
        }).filter(x => x.name || x.amount);
    }

    async function loadProfile() {
        try {
            const persistence = getPersistence();
            if (persistence) {
                const state = await persistence.loadState("FinanceProfile");
                return state && typeof state === "object" ? state : {};
            }
            if (!canUseServerState) {
                const raw = localStorage.getItem(scopeKey("profile"));
                return raw ? JSON.parse(raw) : {};
            }
            const res = await fetch(`/api/finance-state/load?${buildQuery()}`, { credentials: "include" });
            if (!res.ok) return {};
            const payload = await res.json();
            return payload?.found ? JSON.parse(payload?.jsonState || "{}") : {};
        } catch {
            return {};
        }
    }

    async function saveProfile(state) {
        const persistence = getPersistence();
        if (persistence) {
            await persistence.saveState("FinanceProfile", state || {});
            return { ok: true };
        }
        if (!canUseServerState) {
            localStorage.setItem(scopeKey("profile"), JSON.stringify(state || {}));
            return { ok: true };
        }

        const token = getAntiForgeryToken();
        const headers = { "Content-Type": "application/json" };
        if (token) headers["RequestVerificationToken"] = token;

        const body = JSON.stringify({
            clientProfileId,
            clientUserId,
            toolId,
            jsonState: JSON.stringify(state || {})
        });

        return fetch("/api/finance-state/save", {
            method: "POST",
            credentials: "include",
            headers,
            body
        });
    }

    async function updateProfile(partial) {
        if (!partial) return;
        currentState = { ...(currentState || {}), ...partial };
        fillForm(currentState); // reflect immediately
        setStatus("Saving…", "muted");
        try {
            const res = await saveProfile(currentState);
            if (res.ok) {
                setStatus("Saved & synced.", "ok");
                window.dispatchEvent(new CustomEvent("FinanceProfile:updated", { detail: currentState }));
            } else {
                setStatus("Save failed.", "error");
            }
        } catch {
            setStatus("Save failed.", "error");
        }
    }

    async function init() {
        if (!triggerBtn()) return;

        // preload
        setStatus("Loading…", "muted");
        currentState = await loadProfile();
        fillForm(currentState);
        setStatus(currentState && Object.keys(currentState).length ? "Loaded shared data." : "Ready to sync your data.", "muted");
        // announce readiness so tools can hydrate immediately
        window.dispatchEvent(new CustomEvent("FinanceProfile:ready", { detail: currentState }));

        triggerBtn().addEventListener("click", openDrawer);
        addExpenseButton()?.addEventListener("click", () => {
            expensesList()?.appendChild(createExpenseRow());
        });
        drawer()?.querySelectorAll("[data-finance-data-close='true']").forEach(btn => {
            btn.addEventListener("click", closeDrawer);
        });
        document.addEventListener("keydown", e => {
            if (e.key === "Escape") closeDrawer();
        });

        // Auto-save on change (debounced)
        const autoSave = debounce(async () => {
            currentState = readForm();
            setStatus("Saving…", "muted");
            try {
                const res = await saveProfile(currentState);
                if (res.ok) {
                    setStatus("Saved & synced.", "ok");
                    window.dispatchEvent(new CustomEvent("FinanceProfile:updated", { detail: currentState }));
                } else {
                    setStatus("Save failed.", "error");
                }
            } catch {
                setStatus("Save failed.", "error");
            }
        }, 400);

        const form = document.getElementById("financeDataForm");
        form?.addEventListener("input", autoSave);
        form?.addEventListener("change", autoSave);

        // expose for other tools
        window.LegendFinanceProfile = {
            get: () => ({ ...(currentState || {}) }),
            onUpdated: (fn) => window.addEventListener("FinanceProfile:updated", fn),
            update: (partial) => updateProfile(partial)
        };
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
})();
