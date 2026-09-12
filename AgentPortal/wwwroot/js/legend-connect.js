(() => {
    "use strict";

    const read = (source, ...keys) => {
        if (!source) return undefined;

        for (const key of keys) {
            if (Object.prototype.hasOwnProperty.call(source, key)) {
                return source[key];
            }
        }

        return undefined;
    };

    const formatNumber = (input, fallback = "—") => {
        if (input === null || input === undefined || input === "") {
            return fallback;
        }

        const number = Number(input);

        return Number.isFinite(number)
            ? new Intl.NumberFormat().format(number)
            : String(input);
    };

    const verifiedForm =
        document.querySelector("[data-verified-target-form]");

    if (verifiedForm) {
        const toggle =
            verifiedForm.querySelector("[data-verified-target-toggle]");

        const normalFields =
            Array.from(
                verifiedForm.querySelectorAll(
                    "[data-verified-target-normal-fields]"));

        const normalInputs =
            Array.from(
                verifiedForm.querySelectorAll(
                    "[data-verified-target-normal-input]"));

        const rowsField =
            verifiedForm.querySelector("[data-verified-target-rows]");

        const rowsInput =
            verifiedForm.querySelector("[data-verified-target-rows-input]");

        const targetLanguage =
            verifiedForm.querySelector("[data-verified-target-language]");

        const help =
            verifiedForm.querySelector("[data-verified-target-help]");

        const submit =
            verifiedForm.querySelector("[data-verified-target-submit]");

        if (
            toggle &&
            rowsField &&
            rowsInput &&
            targetLanguage &&
            submit
        ) {
            const update = () => {
                const enabled = toggle.checked;

                normalFields.forEach(
                    field => field.hidden = enabled);

                normalInputs.forEach(
                    input => input.disabled = enabled);

                rowsField.hidden = !enabled;
                rowsInput.disabled = !enabled;
                targetLanguage.required = enabled;

                if (help) {
                    help.textContent = enabled
                        ? "Resolve exact existing canonical sources and attach verified targets only."
                        : "Add Founder-approved source material through the canonical ingestion authority.";
                }

                submit.textContent = enabled
                    ? "Apply verified targets"
                    : "Save approved knowledge";
            };

            toggle.addEventListener("change", update);
            update();
        }
    }

    async function loadLiveMetrics() {
        const status =
            document.querySelector("[data-live-metrics-status]");

        if (status) status.textContent = "Refreshing…";
        document.querySelectorAll("[data-live-metric] strong, [data-capacity]").forEach(value => value.textContent = "Refreshing…");
        try {
            const response = await fetch(
                "/founder/legend-connect/live-metrics",
                {
                    cache: "no-store", signal: AbortSignal.timeout(20000),
                    credentials: "same-origin",
                    headers: {
                        Accept: "application/json"
                    }
                });

            if (!response.ok) {
                throw new Error("Live metrics unavailable.");
            }

            const snapshot = await response.json();

            const metrics =
                read(snapshot, "metrics", "Metrics") || {};

            document
                .querySelectorAll("[data-live-metric]")
                .forEach(card => {
                    const metric =
                        metrics[card.dataset.liveMetric];

                    const output =
                        card.querySelector("strong");

                    if (!output) return;
                    if (!metric) { output.textContent = "Unavailable"; return; }

                    output.textContent =
                        read(
                            metric,
                            "displayValue",
                            "DisplayValue") || "—";
                });

            renderAzureCapacity(read(snapshot, "providerCapacity", "ProviderCapacity"));
            if (status) status.textContent = "Updated " + new Date().toLocaleTimeString();
        } catch {
            if (status) status.textContent = "Unavailable · retrying";
            document.querySelectorAll("[data-live-metric] strong").forEach(value => value.textContent = "Unavailable");
            renderAzureCapacity(null);
        }
    }

    function renderAzureCapacity(capacity) {
        const status =
            document.querySelector("[data-azure-status]");

        try {
            if (!capacity) throw new Error("Capacity unavailable");
            const synchronized =
                read(
                    capacity,
                    "isSynchronized",
                    "IsSynchronized");

            const setText = (selector, text) => {
                const element =
                    document.querySelector(selector);

                if (element) {
                    element.textContent = text;
                }
            };

            setText(
                "[data-azure-resource]",
                read(
                    capacity,
                    "resourceName",
                    "ResourceName") ||
                    "Azure Translator");

            setText(
                "[data-azure-tier]",
                "Tier " +
                (
                    read(
                        capacity,
                        "tier",
                        "Tier") || "—"
                ));

            setText(
                "[data-azure-detail]",
                read(
                    capacity,
                    "detail",
                    "Detail") ||
                    "Azure Translator capacity status unavailable.");
            const usageDetail = read(capacity, "usageDetail", "UsageDetail");
            const usageTime = read(capacity, "usageRefreshedUtc", "UsageRefreshedUtc");
            setText("[data-azure-usage-detail]", (usageDetail || "Usage is estimated from recorded LEGEND activity.") +
                (usageTime ? " Updated " + new Date(usageTime).toLocaleString() : ""));

            setText('[data-capacity="azure-observed"]', formatNumber(read(capacity, "monthlyAzureReportedCharacters", "MonthlyAzureReportedCharacters"), "Not available"));
            setText('[data-capacity="azure-observed-remaining"]', formatNumber(read(capacity, "monthlyAzureReportedRemainingCharacters", "MonthlyAzureReportedRemainingCharacters"), "Not available"));
            setText('[data-capacity="monthly-accounted"]', formatNumber(read(capacity, "monthlyCapacityAccountedCharacters", "MonthlyCapacityAccountedCharacters"), "Not available"));
            const observedThrough = read(capacity, "azureUsageRetrievedUtc", "AzureUsageRetrievedUtc");
            setText("[data-azure-observed-through]", observedThrough ? "Azure usage retrieved " + new Date(observedThrough).toLocaleString() : "Azure-reported usage is not available for this resource.");
            const unavailable = synchronized !== true;

            setText(
                '[data-capacity="monthly-limit"]',
                unavailable
                    ? "Unavailable"
                    : formatNumber(
                        read(
                            capacity,
                            "monthlyIncludedCharacterAllowance",
                            "MonthlyIncludedCharacterAllowance"),
                        "Metered"));

            setText(
                '[data-capacity="monthly-consumed"]',
                unavailable
                    ? "Unavailable"
                    : formatNumber(
                        read(
                            capacity,
                            "monthlyCharactersConsumed",
                            "MonthlyCharactersConsumed")));

            setText(
                '[data-capacity="monthly-remaining"]',
                unavailable
                    ? "Unavailable"
                    : formatNumber(
                        read(
                            capacity,
                            "monthlyRemainingCharacters",
                            "MonthlyRemainingCharacters"),
                        "Metered"));

            setText(
                '[data-capacity="safe"]',
                unavailable
                    ? "Unavailable"
                    : formatNumber(
                        read(
                            capacity,
                            "safeAcquisitionCharacters",
                            "SafeAcquisitionCharacters")));

            if (status) {
                status.textContent =
                    synchronized !== true ? "Unavailable" :
                    read(capacity, "isAzureUsageVerified", "IsAzureUsageVerified") === true
                        ? "Azure observed · estimates protected" : "Tier connected · estimated usage";
            }
        } catch {
            if (status) {
                status.textContent = "Unavailable";
            }

            document.querySelectorAll("[data-capacity]").forEach(value => value.textContent = "Unavailable");
            const detail =
                document.querySelector("[data-azure-detail]");

            if (detail) {
                detail.textContent =
                    "Azure Translator capacity could not be synchronized.";
            }
        }
    }

    let refreshing = false;
    async function refreshMetrics() {
        if (refreshing || document.hidden) return;
        refreshing = true;
        try { await loadLiveMetrics(); } finally { refreshing = false; }
    }
    void refreshMetrics();
    window.setInterval(refreshMetrics, 30000);
    document.addEventListener("visibilitychange", () => { if (!document.hidden) void refreshMetrics(); });

    const root =
        document.querySelector("[data-legend-connect-shell]");

    if (!root) return;

    const limitsModal = document.getElementById("translationLimitsModal");
    const limitsBody = document.querySelector("[data-translation-limits-body]");
    let accountSearch = new URLSearchParams(location.search).get("account") || "";
    let loadingLimits = false;
    let limitsRequest = null;
    let limitsGeneration = 0;
    const editingLimits = () => limitsModal?.contains(document.activeElement) &&
        document.activeElement.matches("input, select, textarea, [contenteditable=true]");
    function setLimitsBusy(busy) {
        limitsBody.setAttribute("aria-busy", String(busy));
        limitsBody.querySelectorAll(".translation-limits-summary, .translation-account-list").forEach(section => section.hidden = busy);
        let status = limitsBody.querySelector("[data-limits-loading]");
        if (busy && !status) {
            status = document.createElement("p");
            status.dataset.limitsLoading = "true";
            status.className = "lc-copy";
            status.setAttribute("role", "status");
            status.textContent = "Refreshing current allowances…";
            limitsBody.prepend(status);
        }
        if (!busy) status?.remove();
    }
    async function loadLimits(search = accountSearch, automatic = false) {
        if (automatic && (loadingLimits || editingLimits() || relayBusy)) return;
        limitsRequest?.abort();
        const generation = ++limitsGeneration;
        const request = new AbortController();
        limitsRequest = request;
        const timeout = window.setTimeout(() => request.abort(), 20000);
        loadingLimits = true;
        accountSearch = search;
        setLimitsBusy(true);
        try {
            const response = await fetch("/founder/translation-limits?search=" + encodeURIComponent(search), {
                cache: "no-store", signal: request.signal, credentials: "same-origin",
                headers: { "X-Requested-With": "XMLHttpRequest" }
            });
            if (!response.ok || response.redirected) throw new Error("Allowances unavailable");
            const html = await response.text();
            if (generation !== limitsGeneration || request.signal.aborted || (automatic && editingLimits())) return;
            root.querySelectorAll("[data-limit-editor]").forEach(modal => { bootstrap.Modal.getInstance(modal)?.dispose(); modal.remove(); });
            limitsBody.innerHTML = html;
            limitsBody.querySelectorAll(".modal").forEach(modal => {
                modal.dataset.limitEditor = "true";
                root.append(modal);
            });
            root.querySelectorAll("[data-limit-mode]").forEach(select => {
                const update = () => {
                    const custom = select.closest("form").querySelector("[data-custom-limit]");
                    custom.hidden = select.value !== "Custom";
                    custom.querySelector("input").disabled = select.value !== "Custom";
                    custom.querySelector("input").required = select.value === "Custom";
                };
                select.addEventListener("change", update);
                update();
            });
            document.dispatchEvent(new CustomEvent("legend:limits-loaded"));
        } catch {
            if (generation !== limitsGeneration) return;
            limitsBody.innerHTML = '<p class="lc-notice lc-notice-error" role="alert">Current allowances could not be loaded.</p><button type="button" class="lc-button" data-limits-retry>Retry</button>';
        } finally {
            window.clearTimeout(timeout);
            if (generation === limitsGeneration) {
                loadingLimits = false;
                limitsRequest = null;
                setLimitsBusy(false);
            }
        }
    }
    limitsModal?.addEventListener("hide.bs.modal", () => {
        ++limitsGeneration;
        limitsRequest?.abort();
        limitsRequest = null;
        loadingLimits = false;
        setLimitsBusy(false);
    });
    let relayBusy = false;
    async function refreshRelay() {
        const section = document.querySelector("[data-relay-management]");
        if (!section || relayBusy) return;
        relayBusy = true;
        section.querySelector("[data-relay-status]").textContent = "Refreshing Azure status…";
        section.querySelector("[data-relay-observed]").textContent = "";
        section.querySelectorAll("[data-relay-start], [data-relay-stop]").forEach(button => button.disabled = true);
        try {
            const response = await fetch("/founder/legend-connect/relay", { cache: "no-store", signal: AbortSignal.timeout(20000), credentials: "same-origin", headers: { Accept: "application/json" } });
            if (!response.ok) throw new Error("Relay status unavailable");
            const result = await response.json();
            section.querySelector("[data-relay-status]").textContent = read(result, "status", "Status") || "Unavailable";
            section.querySelector("[data-relay-detail]").textContent = read(result, "detail", "Detail") || "";
            const observed = read(result, "observedUtc", "ObservedUtc");
            section.querySelector("[data-relay-observed]").textContent = observed ? "Observed " + new Date(observed).toLocaleString() : "Observation time unavailable";
            section.querySelector("[data-relay-start]").disabled = read(result, "canStart", "CanStart") !== true;
            section.querySelector("[data-relay-stop]").disabled = read(result, "canStop", "CanStop") !== true;
        } catch {
            section.querySelector("[data-relay-status]").textContent = "Unavailable · refresh to retry";
            section.querySelector("[data-relay-detail]").textContent = "Azure resource status could not be verified.";
        } finally { relayBusy = false; }
    }
    document.addEventListener("legend:limits-loaded", refreshRelay);
    window.setInterval(() => {
        if (!document.hidden && limitsModal?.classList.contains("show")) {
            if (!editingLimits() && !relayBusy) void loadLimits(accountSearch, true);
            else void refreshRelay();
        }
    }, 30000);
    document.addEventListener("click", event => { if (event.target.closest("[data-relay-refresh]")) void refreshRelay(); });
    document.addEventListener("submit", async event => {
        const form = event.target.closest("[data-relay-form]");
        if (!form) return;
        event.preventDefault();
        if (relayBusy) return;
        const action = event.submitter?.value;
        if (!["start", "stop"].includes(action)) return;
        if (!window.confirm(action === "start" ? "Start the configured Azure relay? Compute and bandwidth charges will apply." : "Stop the configured Azure relay? Relayed calls will be interrupted; disk and IP charges may continue.")) return;
        relayBusy = true;
        form.querySelectorAll("button").forEach(button => button.disabled = true);
        const data = new FormData(form);
        data.set("action", action);
        data.set("confirmed", "true");
        try {
            const response = await fetch("/founder/legend-connect/relay", { method: "POST", signal: AbortSignal.timeout(20000), credentials: "same-origin", body: data, headers: { Accept: "application/json" } });
            if (!response.ok) throw new Error("Relay action failed");
            const result = await response.json();
            form.closest("[data-relay-management]").querySelector("[data-relay-result]").textContent = read(result, "detail", "Detail") || "Request completed. Refresh status to verify.";
        } catch {
            form.closest("[data-relay-management]").querySelector("[data-relay-result]").textContent = "The action could not be verified. Refresh Azure status before retrying.";
        } finally {
            relayBusy = false;
            form.querySelector("[data-relay-refresh]").disabled = false;
            void refreshRelay();
        }
    });
    limitsModal?.addEventListener("show.bs.modal", () => { if (!limitsModal.dataset.returning) void loadLimits(); });
    document.addEventListener("submit", event => {
        const form = event.target.closest("[data-translation-limit-search]");
        if (!form) return;
        event.preventDefault();
        void loadLimits(new FormData(form).get("search") || "");
    });
    document.addEventListener("click", event => {
        if (event.target.closest("[data-limits-retry]")) void loadLimits();
    });
    // Bootstrap supports one visible dialog. Return child editors to their parent,
    // preserving the search and focus without creating overlapping backdrops.
    const parents = new WeakMap();
    document.addEventListener("click", event => {
        const trigger = event.target.closest('[data-bs-toggle="modal"]');
        if (!trigger?.closest(".legend-connect-page") || !window.bootstrap) return;
        const selector = trigger.getAttribute("data-bs-target");
        if (!selector?.startsWith("#")) return;
        const modal = document.getElementById(selector.slice(1));
        if (!modal) return;
        event.preventDefault();
        event.stopImmediatePropagation();
        const parent = trigger.closest(".modal.show");
        if (parent && parent !== modal) {
            parents.set(modal, { parent, trigger });
            parent.addEventListener("hidden.bs.modal", () => bootstrap.Modal.getOrCreateInstance(modal).show(trigger), { once: true });
            bootstrap.Modal.getOrCreateInstance(parent).hide();
        } else bootstrap.Modal.getOrCreateInstance(modal).show(trigger);
    }, true);
    document.addEventListener("show.bs.modal", event => {
        if (event.target.closest(".legend-connect-page")) void refreshMetrics();
    });
    document.addEventListener("hidden.bs.modal", event => {
        const previous = parents.get(event.target);
        if (!previous) return;
        parents.delete(event.target);
        previous.parent.dataset.returning = "true";
        previous.parent.addEventListener("shown.bs.modal", () => {
            delete previous.parent.dataset.returning;
            previous.trigger?.focus();
        }, { once: true });
        bootstrap.Modal.getOrCreateInstance(previous.parent).show();
    });
    if (new URLSearchParams(location.search).get("panel") === "translation-limits" && limitsModal && window.bootstrap)
        bootstrap.Modal.getOrCreateInstance(limitsModal).show();

    const language = root.dataset.language;

    if (!language) return;

    const activeRequests = new WeakMap();
    const panelState = new WeakMap();

    const appendText =
        (parent, tag, className, text) => {
            const element =
                document.createElement(tag);

            if (className) {
                element.className = className;
            }

            element.textContent = text || "";
            parent.append(element);

            return element;
        };

    const clearPanel = panel => {
        const body =
            panel.querySelector("[data-legend-section-body]");

        if (body) {
            body.replaceChildren();
        }

        return body;
    };

    const loading = panel => {
        const body = clearPanel(panel);

        if (body) {
            appendText(
                body,
                "p",
                "lc-copy",
                "Loading governed LEGEND® intelligence.");
        }
    };

    const failed = (panel, retry) => {
        const body = clearPanel(panel);

        if (!body) return;

        appendText(
            body,
            "p",
            "lc-copy",
            "This intelligence section could not be loaded. The rest of Legend® Connect remains available.");

        const button =
            appendText(
                body,
                "button",
                "lc-button lc-button-mode",
                "Retry");

        button.type = "button";

        button.addEventListener(
            "click",
            retry,
            { once: true });
    };

    function createFamilyExamples(
        panel,
        familyId,
        familyName) {

        const detail =
            document.createElement("details");

        detail.className = "lc-disclosure";
        detail.dataset.legendSection = "curriculum-examples";
        detail.dataset.legendFamilyId = familyId;

        const summary =
            document.createElement("summary");

        const label =
            document.createElement("span");

        appendText(
            label,
            "small",
            "",
            "CURRICULUM EXAMPLES");

        appendText(
            label,
            "strong",
            "",
            "Examples · " + familyName);

        summary.append(label);

        appendText(
            summary,
            "span",
            "lc-tag",
            "Load on demand");

        detail.append(summary);

        const body =
            document.createElement("div");

        body.className = "lc-disclosure-body";
        body.dataset.legendSectionBody = "";

        detail.append(body);

        panel
            .querySelector("[data-legend-section-body]")
            ?.append(detail);

        detail.addEventListener(
            "toggle",
            () => {
                if (
                    detail.open &&
                    !panelState.has(detail)
                ) {
                    void requestPage(detail);
                }
            });
    }

    function render(
        panel,
        snapshot,
        append = false) {

        const body =
            panel.querySelector("[data-legend-section-body]");

        if (!body) return;

        const prior =
            append
                ? panelState.get(panel)
                : null;

        const rows =
            append
                ? [
                    ...(prior?.rows || []),
                    ...(snapshot.rows || [])
                ]
                : (snapshot.rows || []);

        panelState.set(
            panel,
            {
                ...snapshot,
                rows
            });

        body.replaceChildren();

        const searchForm =
            document.createElement("form");

        searchForm.className =
            "lc-section-search-wrap";

        const search =
            document.createElement("input");

        search.className = "lc-section-search";
        search.type = "search";
        search.maxLength = 160;
        search.placeholder = "Search this section";
        search.value =
            snapshot.search ||
            prior?.search ||
            "";

        const submit =
            appendText(
                searchForm,
                "button",
                "lc-button lc-button-mode",
                "Search");

        submit.type = "submit";

        searchForm.prepend(search);

        searchForm.addEventListener(
            "submit",
            event => {
                event.preventDefault();

                void requestPage(
                    panel,
                    null,
                    search.value,
                    false);
            });

        body.append(searchForm);

        if (rows.length === 0) {
            appendText(
                body,
                "p",
                "lc-copy",
                snapshot.emptyMessage ||
                "No matching current records.");
        } else {
            const surface =
                document.createElement("div");

            surface.className = "lc-table-wrap";

            const table =
                document.createElement("table");

            table.className = "lc-table";

            const head =
                document.createElement("thead");

            const header =
                document.createElement("tr");

            (snapshot.columns || [])
                .forEach(column => {
                    appendText(
                        header,
                        "th",
                        "",
                        column);
                });

            if (snapshot.section === "curriculum") {
                appendText(
                    header,
                    "th",
                    "",
                    "Explore");
            }

            head.append(header);
            table.append(head);

            const tbody =
                document.createElement("tbody");

            rows.forEach(row => {
                const tr =
                    document.createElement("tr");

                (row || [])
                    .forEach(cell => {
                        appendText(
                            tr,
                            "td",
                            "",
                            cell);
                    });

                if (snapshot.section === "curriculum") {
                    const td =
                        document.createElement("td");

                    const button =
                        appendText(
                            td,
                            "button",
                            "lc-button lc-button-mode",
                            "Examples");

                    button.type = "button";

                    button.addEventListener(
                        "click",
                        () => {
                            button.disabled = true;

                            createFamilyExamples(
                                panel,
                                row[0],
                                row[1]);
                        },
                        { once: true });

                    tr.append(td);
                }

                tbody.append(tr);
            });

            table.append(tbody);
            surface.append(table);
            body.append(surface);
        }

        if (snapshot.nextCursor) {
            const next =
                appendText(
                    body,
                    "button",
                    "lc-button lc-button-mode",
                    "Load next page");

            next.type = "button";

            next.addEventListener(
                "click",
                () => void requestPage(
                    panel,
                    snapshot.nextCursor,
                    snapshot.search || null,
                    true),
                { once: true });
        }
    }

    async function requestPage(
        panel,
        cursor = null,
        search = null,
        append = false) {

        activeRequests.get(panel)?.abort();

        const controller =
            new AbortController();

        activeRequests.set(panel, controller);

        if (!append) {
            loading(panel);
        }

        const parameters =
            new URLSearchParams({
                section: panel.dataset.legendSection,
                language
            });

        if (cursor) {
            parameters.set("cursor", cursor);
        }

        if (search) {
            parameters.set("search", search);
        }

        if (panel.dataset.legendFamilyId) {
            parameters.set(
                "familyId",
                panel.dataset.legendFamilyId);
        }

        try {
            const response =
                await fetch(
                    "/founder/legend-connect/sections?" +
                    parameters.toString(),
                    {
                        cache: "no-store", signal: AbortSignal.timeout(20000),
                        credentials: "same-origin",
                        headers: {
                            Accept: "application/json"
                        },
                        signal: controller.signal
                    });

            if (!response.ok) {
                throw new Error("Section request failed.");
            }

            if (
                activeRequests.get(panel) !==
                controller
            ) {
                return;
            }

            render(
                panel,
                await response.json(),
                append);
        } catch (error) {
            if (
                error?.name === "AbortError" ||
                activeRequests.get(panel) !==
                controller
            ) {
                return;
            }

            failed(
                panel,
                () => void requestPage(
                    panel,
                    cursor,
                    search,
                    append));
        } finally {
            if (
                activeRequests.get(panel) ===
                controller
            ) {
                activeRequests.delete(panel);
            }
        }
    }

    document
        .querySelectorAll("[data-legend-section]")
        .forEach(panel => {
            panel.addEventListener(
                "toggle",
                () => {
                    if (
                        panel.open &&
                        !panelState.has(panel)
                    ) {
                        void requestPage(panel);
                    }
                });
        });
})();
