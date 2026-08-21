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

        try {
            const response = await fetch(
                "/founder/legend-connect/live-metrics",
                {
                    cache: "no-store",
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

                    if (!metric || !output) return;

                    output.textContent =
                        read(
                            metric,
                            "displayValue",
                            "DisplayValue") || "—";
                });

            if (status) {
                status.textContent = "Live";
            }
        } catch {
            if (status) {
                status.textContent = "Unavailable";
            }
        }
    }

    async function loadAzureCapacity() {
        const status =
            document.querySelector("[data-azure-status]");

        try {
            const response = await fetch(
                "/founder/legend-connect/capacity",
                {
                    cache: "no-store",
                    credentials: "same-origin",
                    headers: {
                        Accept: "application/json"
                    }
                });

            if (!response.ok) {
                throw new Error("Azure capacity unavailable.");
            }

            const capacity = await response.json();

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
                    "Azure Translator capacity synchronized.");

            const unavailable =
                synchronized === false;

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
                    read(
                        capacity,
                        "status",
                        "Status") ||
                    "Available";
            }
        } catch {
            if (status) {
                status.textContent = "Unavailable";
            }

            const detail =
                document.querySelector("[data-azure-detail]");

            if (detail) {
                detail.textContent =
                    "Azure Translator capacity could not be synchronized.";
            }
        }
    }

    void loadLiveMetrics();
    void loadAzureCapacity();

    const root =
        document.querySelector("[data-legend-connect-shell]");

    if (!root) return;

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
                        cache: "no-store",
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
