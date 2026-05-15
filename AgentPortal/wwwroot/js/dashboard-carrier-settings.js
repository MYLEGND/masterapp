document.addEventListener("DOMContentLoaded", () => {
    const modalEl = document.getElementById("carrierSettingsModal");
    const bodyEl = document.getElementById("carrierSettingsBody");
    const statusEl = document.getElementById("carrierSettingsStatus");
    const countEl = document.getElementById("carrierSettingsCount");
    const antiForgeryToken = document.querySelector('#dashboardAf input[name="__RequestVerificationToken"]')?.value || "";

    if (!modalEl || !bodyEl || !statusEl || !countEl) return;

    window.LegendModal?.bind?.("carrierSettingsModal", { modalZ: 1050, backdropZ: 1045 });

    const asTrimmed = (value) => (typeof value === "string" ? value.trim() : "");
    const loadUrl = modalEl.dataset.loadUrl || "/Dashboard/CarrierSettings";
    const saveUrl = modalEl.dataset.saveUrl || "/Dashboard/SaveCarrierSettings";
    const storageScope = asTrimmed(modalEl.dataset.storageScope) || "dashboard";
    const draftStorageKey = `legend.dashboardCarrierSettingsDraft.${storageScope}`;

    const store = {
        loaded: false,
        loadingPromise: null,
        saveTimer: null,
        saveInFlight: false,
        saveQueued: false,
        dirty: false,
        savedUtc: null,
        lastSavedFingerprint: "",
        retryTimer: null,
        inventory: [],
        inventoryByKey: new Map(),
        itemsByKey: new Map(),
        expandedKeys: new Set(),
    };

    function slugify(value) {
        return asTrimmed(value)
            .toLowerCase()
            .replace(/[^a-z0-9]+/g, "-")
            .replace(/^-+|-+$/g, "");
    }

    function escapeHtml(value) {
        return String(value ?? "")
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;")
            .replace(/'/g, "&#39;");
    }

    function escapeAttr(value) {
        return escapeHtml(value);
    }

    function createDefaultItem(meta) {
        return {
            entryKey: meta.entryKey,
            categoryKey: meta.categoryKey,
            categoryName: meta.categoryName,
            carrierKey: meta.carrierKey,
            carrierName: meta.carrierName,
            agentNumber: "",
            entityNumber: "",
            notes: "",
            compensationLines: [],
        };
    }

    function normalizeLine(line) {
        return {
            productLine: asTrimmed(line?.productLine),
            commissionPercent: asTrimmed(line?.commissionPercent),
            eligibilityNotes: asTrimmed(line?.eligibilityNotes),
        };
    }

    function normalizeItem(item, meta = null) {
        const fallback = meta || item || {};
        return {
            entryKey: asTrimmed(item?.entryKey) || fallback.entryKey || "",
            categoryKey: asTrimmed(item?.categoryKey) || fallback.categoryKey || "",
            categoryName: asTrimmed(item?.categoryName) || fallback.categoryName || "",
            carrierKey: asTrimmed(item?.carrierKey) || fallback.carrierKey || "",
            carrierName: asTrimmed(item?.carrierName) || fallback.carrierName || "",
            agentNumber: asTrimmed(item?.agentNumber),
            entityNumber: asTrimmed(item?.entityNumber || item?.producerNumber),
            notes: asTrimmed(item?.notes),
            compensationLines: Array.isArray(item?.compensationLines)
                ? item.compensationLines.map(normalizeLine)
                : [],
        };
    }

    function lineHasData(line) {
        return !!(asTrimmed(line?.productLine) || asTrimmed(line?.commissionPercent) || asTrimmed(line?.eligibilityNotes));
    }

    function itemHasData(item) {
        return !!(
            asTrimmed(item?.agentNumber) ||
            asTrimmed(item?.entityNumber) ||
            asTrimmed(item?.notes) ||
            (Array.isArray(item?.compensationLines) && item.compensationLines.some(lineHasData))
        );
    }

    function serializeItems() {
        return Array.from(store.itemsByKey.values())
            .map((item) => ({
                entryKey: item.entryKey,
                categoryKey: item.categoryKey,
                categoryName: item.categoryName,
                carrierKey: item.carrierKey,
                carrierName: item.carrierName,
                agentNumber: item.agentNumber,
                entityNumber: item.entityNumber,
                notes: item.notes,
                compensationLines: (item.compensationLines || []).filter(lineHasData).map(normalizeLine),
            }))
            .filter(itemHasData)
            .sort((left, right) => left.entryKey.localeCompare(right.entryKey));
    }

    function serializeDraftItems() {
        return Array.from(store.itemsByKey.values())
            .map((item) => ({
                entryKey: item.entryKey,
                categoryKey: item.categoryKey,
                categoryName: item.categoryName,
                carrierKey: item.carrierKey,
                carrierName: item.carrierName,
                agentNumber: item.agentNumber,
                entityNumber: item.entityNumber,
                notes: item.notes,
                compensationLines: (item.compensationLines || []).map(normalizeLine),
            }))
            .sort((left, right) => left.entryKey.localeCompare(right.entryKey));
    }

    function buildPayload() {
        return {
            items: serializeItems(),
        };
    }

    function buildDraftSnapshot() {
        return {
            items: serializeDraftItems(),
        };
    }

    function fingerprintPayload(payload) {
        try {
            return JSON.stringify(payload ?? { items: [] });
        } catch {
            return "";
        }
    }

    function readDraft() {
        try {
            const raw = window.localStorage.getItem(draftStorageKey);
            if (!raw) return null;

            const parsed = JSON.parse(raw);
            const items = Array.isArray(parsed?.items) ? parsed.items : [];
            return {
                updatedAt: asTrimmed(parsed?.updatedAt),
                items,
            };
        } catch {
            return null;
        }
    }

    function writeDraft(snapshot = buildDraftSnapshot()) {
        try {
            window.localStorage.setItem(draftStorageKey, JSON.stringify({
                updatedAt: new Date().toISOString(),
                items: snapshot.items,
            }));
        } catch {
            // Ignore storage limits and private browsing failures.
        }
    }

    function clearDraft() {
        try {
            window.localStorage.removeItem(draftStorageKey);
        } catch {
            // Ignore storage cleanup failures.
        }
    }

    function clearRetryTimer() {
        if (store.retryTimer) {
            window.clearTimeout(store.retryTimer);
            store.retryTimer = null;
        }
    }

    function scheduleRetry(delayMs = 4000) {
        clearRetryTimer();
        store.retryTimer = window.setTimeout(() => {
            store.retryTimer = null;
            if (!store.loaded || !store.dirty) return;
            void saveState();
        }, delayMs);
    }

    function collectInventory() {
        const inventory = [];
        const inventoryByKey = new Map();

        document.querySelectorAll(".category-container").forEach((categoryEl) => {
            const heading = categoryEl.querySelector("h3");
            const categoryName = asTrimmed(heading?.textContent);
            if (!categoryName) return;

            const categoryKey = slugify(categoryName);
            const carriers = [];
            const seen = new Set();

            categoryEl.querySelectorAll(".dashboard-tool").forEach((toolEl) => {
                const carrierName = asTrimmed(toolEl.dataset.name || toolEl.querySelector(".tool-name")?.textContent);
                if (!carrierName) return;

                const carrierKey = slugify(carrierName);
                const entryKey = `${categoryKey}::${carrierKey}`;
                if (seen.has(entryKey)) return;
                seen.add(entryKey);

                const meta = {
                    entryKey,
                    categoryKey,
                    categoryName,
                    carrierKey,
                    carrierName,
                    href: toolEl.getAttribute("href") || "",
                    description: asTrimmed(toolEl.querySelector(".tool-desc")?.textContent),
                };

                carriers.push(meta);
                inventoryByKey.set(entryKey, meta);
            });

            if (carriers.length) {
                inventory.push({
                    categoryKey,
                    categoryName,
                    carriers,
                });
            }
        });

        store.inventory = inventory;
        store.inventoryByKey = inventoryByKey;
        return inventory;
    }

    function ensureInventoryItems() {
        store.inventoryByKey.forEach((meta, entryKey) => {
            const existing = store.itemsByKey.get(entryKey);
            store.itemsByKey.set(entryKey, normalizeItem(existing || {}, meta));
        });
    }

    function getItem(entryKey) {
        const existing = store.itemsByKey.get(entryKey);
        if (existing) return existing;

        const meta = store.inventoryByKey.get(entryKey);
        const created = createDefaultItem(meta || {
            entryKey,
            categoryKey: "",
            categoryName: "",
            carrierKey: "",
            carrierName: "",
        });
        store.itemsByKey.set(entryKey, created);
        return created;
    }

    function setStatus(message, stateClass = "") {
        statusEl.textContent = message;
        statusEl.className = "carrier-settings-status";
        if (stateClass) {
            statusEl.classList.add(stateClass);
        }
    }

    function formatSavedMessage(savedUtc) {
        if (!savedUtc) return "Ready";

        try {
            const dt = new Date(savedUtc);
            return `Saved ${dt.toLocaleString([], {
                month: "short",
                day: "numeric",
                hour: "numeric",
                minute: "2-digit",
            })}`;
        } catch {
            return "Saved";
        }
    }

    function recoverDraftIfNeeded() {
        const draft = readDraft();
        if (!draft) return false;

        const currentSnapshotFingerprint = fingerprintPayload(buildDraftSnapshot());
        const draftFingerprint = fingerprintPayload({ items: draft.items });

        if (draftFingerprint === currentSnapshotFingerprint) {
            clearDraft();
            return false;
        }

        const draftTime = Date.parse(draft.updatedAt || "");
        const serverTime = Date.parse(store.savedUtc || "");
        if (Number.isFinite(draftTime) && Number.isFinite(serverTime) && draftTime <= serverTime) {
            clearDraft();
            return false;
        }

        store.itemsByKey.clear();
        for (const rawItem of draft.items || []) {
            const normalized = normalizeItem(rawItem);
            if (!normalized.entryKey) continue;
            const meta = store.inventoryByKey.get(normalized.entryKey);
            store.itemsByKey.set(normalized.entryKey, normalizeItem(normalized, meta || normalized));
        }

        store.dirty = true;
        return true;
    }

    function renderLoading() {
        bodyEl.innerHTML = `
            <div class="carrier-settings-loading">
                Pulling your current dashboard sections and saved carrier data...
            </div>
        `;
    }

    function renderLoadError() {
        bodyEl.innerHTML = `
            <div class="carrier-settings-error">
                We couldn't load your carrier settings right now.
                <div>
                    <button type="button" class="carrier-settings-retry" data-action="retry-load">Try Again</button>
                </div>
            </div>
        `;
    }

    function buildSummaryPills(item) {
        const pills = [];
        const lineCount = (item.compensationLines || []).filter(lineHasData).length;

        if (lineCount > 0) {
            pills.push(`<span class="carrier-editor-pill">${lineCount} commission ${lineCount === 1 ? "line" : "lines"}</span>`);
        }
        if (asTrimmed(item.notes)) {
            pills.push('<span class="carrier-editor-pill">Notes added</span>');
        }
        if (!pills.length) {
            pills.push('<span class="carrier-editor-pill">No commission details yet</span>');
        }

        return pills.join("");
    }

    function renderCompRows(item) {
        const lines = item.compensationLines || [];
        if (!lines.length) {
            return `
                <div class="carrier-panel-help">
                    No commission lines yet. Add one for each product, age band, or payout tier you want to remember.
                </div>
            `;
        }

        return lines.map((line, index) => `
            <div class="carrier-comp-row" data-line-index="${index}">
                <label class="carrier-inline-field">
                    <span>Product / Plan</span>
                    <input
                        type="text"
                        class="carrier-panel-input"
                        data-line-field="productLine"
                        data-line-index="${index}"
                        value="${escapeAttr(line.productLine)}"
                        placeholder="Level Final Expense"
                    />
                </label>
                <label class="carrier-inline-field">
                    <span>Commission %</span>
                    <input
                        type="text"
                        class="carrier-panel-input"
                        data-line-field="commissionPercent"
                        data-line-index="${index}"
                        value="${escapeAttr(line.commissionPercent)}"
                        placeholder="115%"
                    />
                </label>
                <label class="carrier-inline-field carrier-comp-notes">
                    <span>Eligibility / Notes</span>
                    <input
                        type="text"
                        class="carrier-panel-input"
                        data-line-field="eligibilityNotes"
                        data-line-index="${index}"
                        value="${escapeAttr(line.eligibilityNotes)}"
                        placeholder="Level products, ages 60-80, Eagle Select 1 & 2"
                    />
                </label>
                <button type="button" class="carrier-comp-remove" data-action="remove-comp-line" data-line-index="${index}">
                    Remove
                </button>
            </div>
        `).join("");
    }

    function renderCarrierItem(item) {
        const meta = store.inventoryByKey.get(item.entryKey) || {};
        const isOpen = store.expandedKeys.has(item.entryKey);
        const description = meta.description
            ? escapeHtml(meta.description)
            : "Pulled automatically from the current dashboard list.";
        const openLabel = isOpen ? "Hide Details" : "Commission Details";
        const chevron = isOpen ? "-" : "+";
        const href = meta.href ? `<a class="carrier-editor-link" href="${escapeAttr(meta.href)}" target="_blank" rel="noopener">Open</a>` : "";

        return `
            <div class="carrier-editor-item ${isOpen ? "is-open" : ""}" data-entry-key="${escapeAttr(item.entryKey)}">
                <div class="carrier-editor-row">
                    <div class="carrier-editor-identity">
                        <div class="carrier-editor-titleline">
                            <button type="button" class="carrier-editor-toggle" data-action="toggle-details">
                                <span class="carrier-editor-chevron">${chevron}</span>
                                <span>${escapeHtml(item.carrierName)}</span>
                            </button>
                            ${href}
                        </div>
                        <div class="carrier-editor-meta">${description}</div>
                    </div>
                    <label class="carrier-inline-field">
                        <span>Agent #</span>
                        <input
                            type="text"
                            data-field="agentNumber"
                            value="${escapeAttr(item.agentNumber)}"
                            placeholder="Agent number"
                        />
                    </label>
                    <label class="carrier-inline-field">
                        <span>Entity #</span>
                        <input
                            type="text"
                            data-field="entityNumber"
                            value="${escapeAttr(item.entityNumber)}"
                            placeholder="Entity number"
                        />
                    </label>
                    <div class="carrier-editor-summary">
                        <div class="carrier-editor-pills">${buildSummaryPills(item)}</div>
                        <button type="button" class="carrier-editor-expand" data-action="toggle-details">${openLabel}</button>
                    </div>
                </div>
                <div class="carrier-editor-panel" ${isOpen ? "" : "hidden"}>
                    <div class="carrier-panel-grid">
                        <div class="carrier-panel-stack">
                            <div class="carrier-panel-label">Commission Lines</div>
                            <div class="carrier-comp-list">
                                ${renderCompRows(item)}
                            </div>
                            <button type="button" class="carrier-comp-add" data-action="add-comp-line">Add Commission Line</button>
                        </div>
                        <div class="carrier-panel-stack">
                            <div class="carrier-panel-label">General Notes</div>
                            <textarea
                                class="carrier-panel-textarea"
                                data-field="notes"
                                placeholder="Store quick reminders, carrier quirks, or how this carrier structures comp for your book of business."
                            >${escapeHtml(item.notes)}</textarea>
                            <div class="carrier-panel-help">
                                Clean format examples: "United of Omaha: 115% level FE, 80% graded." "Americo: 120% ages 60-80 on Eagle Select 1 & 2; 110% ages 40-59 and 81-85."
                            </div>
                        </div>
                    </div>
                </div>
            </div>
        `;
    }

    function renderBody() {
        collectInventory();
        ensureInventoryItems();

        const carrierCount = store.inventory.reduce((sum, category) => sum + category.carriers.length, 0);
        countEl.textContent = `${carrierCount} rows live`;
        if (!store.loaded) {
            renderLoading();
            return;
        }

        if (!store.inventory.length) {
            bodyEl.innerHTML = `
                <div class="carrier-settings-intro">
                    <h6>How this works</h6>
                    <p>This editor mirrors the carrier and partner tools currently shown on your dashboard, then saves your agent numbers and compensation notes automatically.</p>
                </div>
                <div class="carrier-settings-empty">
                    No dashboard categories were found to pull from right now.
                </div>
            `;
            return;
        }

        const categoriesHtml = store.inventory.map((category) => `
            <section class="carrier-category-block" data-category-key="${escapeAttr(category.categoryKey)}">
                <div class="carrier-category-header">
                    <div>
                        <h3 class="carrier-category-title">${escapeHtml(category.categoryName)}</h3>
                        <div class="carrier-category-sub">${category.carriers.length} ${category.carriers.length === 1 ? "entry" : "entries"} pulled from this dashboard section</div>
                    </div>
                </div>
                <div class="carrier-category-list">
                    ${category.carriers.map((meta) => renderCarrierItem(getItem(meta.entryKey))).join("")}
                </div>
            </section>
        `).join("");

        bodyEl.innerHTML = `
            <div class="carrier-settings-intro">
                <h6>Auto-pulled from your live dashboard</h6>
                <p>Every category here is pulled from the current dashboard sections, so when you add a new carrier or partner tile later, it shows up here automatically without a second hardcoded list.</p>
                <p>Use the top row for quick agent and entity numbers, then open commission details only when you need more depth. Changes save automatically to the server for the current agent profile.</p>
            </div>
            <div class="carrier-settings-categories">
                ${categoriesHtml}
            </div>
        `;
    }

    async function ensureLoaded(force = false) {
        if (store.loaded && !force) {
            renderBody();
            return;
        }
        if (store.loadingPromise && !force) {
            return store.loadingPromise;
        }

        renderLoading();

        store.loadingPromise = fetch(loadUrl, {
            credentials: "include",
        })
            .then(async (response) => {
                if (!response.ok) {
                    throw new Error("load_failed");
                }
                const payload = await response.json();
                store.itemsByKey.clear();
                for (const item of Array.isArray(payload?.items) ? payload.items : []) {
                    const normalized = normalizeItem(item);
                    if (!normalized.entryKey) continue;
                    store.itemsByKey.set(normalized.entryKey, normalized);
                }
                store.savedUtc = payload?.savedUtc || null;
                ensureInventoryItems();
                store.lastSavedFingerprint = fingerprintPayload(buildPayload());
                store.loaded = true;

                if (recoverDraftIfNeeded()) {
                    setStatus("Recovered unsaved changes. Syncing to server...", "is-saving");
                } else {
                    setStatus(formatSavedMessage(store.savedUtc), store.savedUtc ? "is-saved" : "");
                }

                renderBody();
                if (store.dirty) {
                    void saveState();
                }
            })
            .catch(() => {
                store.loaded = false;
                setStatus("Could not load carrier settings", "is-error");
                renderLoadError();
            })
            .finally(() => {
                store.loadingPromise = null;
            });

        return store.loadingPromise;
    }

    function markDirty() {
        store.dirty = true;
        clearRetryTimer();
        writeDraft();
        setStatus("Saving changes...", "is-saving");
        if (store.saveTimer) {
            window.clearTimeout(store.saveTimer);
        }
        store.saveTimer = window.setTimeout(() => {
            store.saveTimer = null;
            void saveState();
        }, 700);
    }

    async function saveState(options = {}) {
        const keepalive = !!options.keepalive;
        const suppressStatus = !!options.suppressStatus;
        if (!store.loaded) return;

        const payload = buildPayload();
        const fingerprint = fingerprintPayload(payload);
        const needsSave = store.dirty || fingerprint !== store.lastSavedFingerprint;

        if (!needsSave) {
            clearDraft();
            if (!suppressStatus) {
                setStatus(formatSavedMessage(store.savedUtc), store.savedUtc ? "is-saved" : "");
            }
            return;
        }

        writeDraft();

        if (store.saveInFlight && !keepalive) {
            store.saveQueued = true;
            return;
        }

        if (!keepalive) {
            store.saveInFlight = true;
            clearRetryTimer();
            if (!suppressStatus) {
                setStatus("Saving changes...", "is-saving");
            }
        }

        try {
            const response = await fetch(saveUrl, {
                method: "POST",
                credentials: "include",
                keepalive,
                headers: {
                    "Content-Type": "application/json",
                    "RequestVerificationToken": antiForgeryToken,
                },
                body: JSON.stringify(payload),
            });

            if (!response.ok) {
                throw new Error("save_failed");
            }

            const result = await response.json();
            store.savedUtc = result?.savedUtc || null;
            store.lastSavedFingerprint = fingerprint;
            store.dirty = false;
            clearDraft();
            if (!suppressStatus && !keepalive) {
                setStatus(formatSavedMessage(store.savedUtc), "is-saved");
            }
        } catch {
            store.dirty = true;
            if (!suppressStatus && !keepalive) {
                setStatus("Could not reach the server. Your draft is cached and will retry automatically.", "is-error");
            }
            if (!keepalive) {
                scheduleRetry();
            }
        } finally {
            if (!keepalive) {
                store.saveInFlight = false;
                if (store.saveQueued) {
                    store.saveQueued = false;
                    void saveState();
                }
            }
        }
    }

    function flushPendingSave(options = {}) {
        if (store.saveTimer) {
            window.clearTimeout(store.saveTimer);
            store.saveTimer = null;
        }

        if (!store.loaded) return;
        if (!store.dirty && fingerprintPayload(buildPayload()) === store.lastSavedFingerprint) {
            clearDraft();
            return;
        }

        void saveState(options);
    }

    function replaceCarrierItem(entryKey) {
        const itemEl = bodyEl.querySelector(`.carrier-editor-item[data-entry-key="${CSS.escape(entryKey)}"]`);
        const item = getItem(entryKey);
        if (!itemEl) return;

        const wrapper = document.createElement("div");
        wrapper.innerHTML = renderCarrierItem(item);
        const next = wrapper.firstElementChild;
        if (next) {
            itemEl.replaceWith(next);
        }
    }

    function refreshSummary(entryKey) {
        const itemEl = bodyEl.querySelector(`.carrier-editor-item[data-entry-key="${CSS.escape(entryKey)}"]`);
        if (!itemEl) return;
        const pillsEl = itemEl.querySelector(".carrier-editor-pills");
        if (!pillsEl) return;
        pillsEl.innerHTML = buildSummaryPills(getItem(entryKey));
    }

    function updateItemField(entryKey, field, value) {
        const item = getItem(entryKey);
        item[field] = value;
        store.itemsByKey.set(entryKey, item);
    }

    function updateLineField(entryKey, lineIndex, field, value) {
        const item = getItem(entryKey);
        while (item.compensationLines.length <= lineIndex) {
            item.compensationLines.push(normalizeLine({}));
        }
        item.compensationLines[lineIndex][field] = value;
        store.itemsByKey.set(entryKey, item);
    }

    function toggleEntry(entryKey) {
        if (store.expandedKeys.has(entryKey)) {
            store.expandedKeys.delete(entryKey);
        } else {
            store.expandedKeys.add(entryKey);
        }
        replaceCarrierItem(entryKey);
    }

    modalEl.addEventListener("show.bs.modal", () => {
        collectInventory();
        if (store.loaded) {
            renderBody();
        } else {
            void ensureLoaded();
        }
    });

    modalEl.addEventListener("hide.bs.modal", () => {
        flushPendingSave();
    });

    bodyEl.addEventListener("click", (event) => {
        const target = event.target.closest("[data-action]");
        if (!target) return;

        const action = target.getAttribute("data-action");
        if (action === "retry-load") {
            void ensureLoaded(true);
            return;
        }

        const itemEl = target.closest(".carrier-editor-item");
        const entryKey = itemEl?.dataset.entryKey;
        if (!entryKey) return;

        if (action === "toggle-details") {
            toggleEntry(entryKey);
            return;
        }

        if (action === "add-comp-line") {
            const item = getItem(entryKey);
            item.compensationLines.push(normalizeLine({}));
            store.itemsByKey.set(entryKey, item);
            store.expandedKeys.add(entryKey);
            replaceCarrierItem(entryKey);
            markDirty();
            return;
        }

        if (action === "remove-comp-line") {
            const lineIndex = Number.parseInt(target.getAttribute("data-line-index") || "-1", 10);
            if (!Number.isFinite(lineIndex) || lineIndex < 0) return;
            const item = getItem(entryKey);
            item.compensationLines.splice(lineIndex, 1);
            store.itemsByKey.set(entryKey, item);
            store.expandedKeys.add(entryKey);
            replaceCarrierItem(entryKey);
            markDirty();
        }
    });

    bodyEl.addEventListener("input", (event) => {
        const target = event.target;
        if (!(target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement)) return;

        const itemEl = target.closest(".carrier-editor-item");
        const entryKey = itemEl?.dataset.entryKey;
        if (!entryKey) return;

        const lineField = target.getAttribute("data-line-field");
        if (lineField) {
            const lineIndex = Number.parseInt(target.getAttribute("data-line-index") || "-1", 10);
            if (!Number.isFinite(lineIndex) || lineIndex < 0) return;
            updateLineField(entryKey, lineIndex, lineField, target.value);
            refreshSummary(entryKey);
            markDirty();
            return;
        }

        const field = target.getAttribute("data-field");
        if (!field) return;

        updateItemField(entryKey, field, target.value);
        refreshSummary(entryKey);
        markDirty();
    });

    document.addEventListener("visibilitychange", () => {
        if (document.visibilityState === "hidden") {
            flushPendingSave({ keepalive: true, suppressStatus: true });
        }
    });

    window.addEventListener("pagehide", () => {
        flushPendingSave({ keepalive: true, suppressStatus: true });
    });

    window.addEventListener("online", () => {
        if (!store.loaded) return;
        if (!store.dirty && !readDraft()) return;
        void saveState();
    });
});
