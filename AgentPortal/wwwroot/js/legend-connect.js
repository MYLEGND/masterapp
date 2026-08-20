(() => {
    const form = document.querySelector("[data-verified-target-form]");
    if (!form) return;

    const toggle = form.querySelector("[data-verified-target-toggle]");
    const normalFields = Array.from(form.querySelectorAll("[data-verified-target-normal-fields]"));
    const normalInputs = Array.from(form.querySelectorAll("[data-verified-target-normal-input]"));
    const rowsField = form.querySelector("[data-verified-target-rows]");
    const rowsInput = form.querySelector("[data-verified-target-rows-input]");
    const targetLanguage = form.querySelector("[data-verified-target-language]");
    const help = form.querySelector("[data-verified-target-help]");
    const submit = form.querySelector("[data-verified-target-submit]");
    if (!toggle || !rowsField || !rowsInput || !targetLanguage || !submit) return;

    function updateMode() {
        const enabled = toggle.checked;
        normalFields.forEach(field => field.hidden = enabled);
        normalInputs.forEach(input => input.disabled = enabled);
        rowsField.hidden = !enabled;
        rowsInput.disabled = !enabled;
        targetLanguage.required = enabled;
        if (help) help.textContent = enabled
            ? "Resolve exact existing canonical sources and attach verified targets only."
            : "Add controlled Founder-approved source material through the existing ingestion authority.";
        submit.textContent = enabled ? "Apply verified targets" : "Save approved knowledge";
    }

    toggle.addEventListener("change", updateMode);
    updateMode();
})();

(() => {
    const root = document.querySelector("[data-legend-connect-shell]");
    if (!root) return;
    const language = root.dataset.language;
    if (!language) return;

    const activeRequests = new WeakMap();
    const panelState = new WeakMap();

    function appendText(parent, tag, className, value) {
        const element = document.createElement(tag);
        if (className) element.className = className;
        element.textContent = value || "";
        parent.append(element);
        return element;
    }

    function clearPanel(panel) {
        const body = panel.querySelector("[data-legend-section-body]");
        if (body) body.replaceChildren();
        return body;
    }

    function loading(panel, message = "Loading this governed section…") {
        const body = clearPanel(panel);
        if (body) appendText(body, "p", "dashboard-section-copy", message);
    }

    function failed(panel, retry) {
        const body = clearPanel(panel);
        if (!body) return;
        appendText(body, "p", "dashboard-section-copy", "This section could not be loaded. The rest of the Founder page remains available.");
        const button = appendText(body, "button", "dashboard-command-action btn-carrier-settings", "Retry");
        button.type = "button";
        button.addEventListener("click", retry, { once: true });
    }

    function createFamilyExamples(panel, familyId, familyName) {
        const detail = document.createElement("details");
        detail.className = "legend-connect-disclosure";
        detail.dataset.legendSection = "curriculum-examples";
        detail.dataset.legendFamilyId = familyId;
        const summary = document.createElement("summary");
        summary.className = "legend-connect-disclosure-summary";
        appendText(summary, "span", "dashboard-section-title", `Examples · ${familyName}`);
        appendText(summary, "span", "legend-connect-disclosure-status", "Load on demand");
        detail.append(summary);
        const body = document.createElement("div");
        body.className = "legend-connect-disclosure-body";
        body.dataset.legendSectionBody = "";
        detail.append(body);
        panel.querySelector("[data-legend-section-body]")?.append(detail);
        detail.addEventListener("toggle", () => {
            if (detail.open && !panelState.has(detail)) void requestPage(detail);
        });
    }

    function render(panel, snapshot, append = false) {
        const body = panel.querySelector("[data-legend-section-body]");
        if (!body) return;
        const prior = append ? panelState.get(panel) : null;
        const rows = append ? [...(prior?.rows || []), ...(snapshot.rows || [])] : (snapshot.rows || []);
        const state = { ...snapshot, rows };
        panelState.set(panel, state);
        body.replaceChildren();

        const searchForm = document.createElement("form");
        searchForm.className = "dashboard-search-panel";
        const search = document.createElement("input");
        search.type = "search";
        search.maxLength = 160;
        search.placeholder = "Filter this section";
        search.value = snapshot.search || prior?.search || "";
        const submit = appendText(searchForm, "button", "dashboard-command-action btn-carrier-settings", "Search");
        submit.type = "submit";
        searchForm.prepend(search);
        searchForm.addEventListener("submit", event => {
            event.preventDefault();
            void requestPage(panel, null, search.value, false);
        });
        body.append(searchForm);

        if (rows.length === 0) {
            appendText(body, "p", "dashboard-section-copy", snapshot.emptyMessage || "No matching current records.");
        } else {
            const surface = document.createElement("div");
            surface.className = "dashboard-data-surface";
            const table = document.createElement("table");
            table.className = "table dashboard-data-table";
            const head = document.createElement("thead");
            const header = document.createElement("tr");
            (snapshot.columns || []).forEach(column => appendText(header, "th", "", column));
            if (snapshot.section === "curriculum") appendText(header, "th", "", "Explore");
            head.append(header);
            table.append(head);
            const tableBody = document.createElement("tbody");
            rows.forEach(row => {
                const rowElement = document.createElement("tr");
                (row || []).forEach(value => appendText(rowElement, "td", "", value));
                if (snapshot.section === "curriculum") {
                    const actionCell = document.createElement("td");
                    const button = appendText(actionCell, "button", "dashboard-command-action btn-carrier-settings", "Examples");
                    button.type = "button";
                    button.addEventListener("click", () => {
                        button.disabled = true;
                        createFamilyExamples(panel, row[0], row[1]);
                    }, { once: true });
                    rowElement.append(actionCell);
                }
                tableBody.append(rowElement);
            });
            table.append(tableBody);
            surface.append(table);
            body.append(surface);
        }

        if (snapshot.nextCursor) {
            const next = appendText(body, "button", "dashboard-command-action btn-carrier-settings", "Load next page");
            next.type = "button";
            next.addEventListener("click", () => void requestPage(panel, snapshot.nextCursor, snapshot.search || null, true), { once: true });
        }
    }

    async function requestPage(panel, cursor = null, search = null, append = false) {
        const previous = activeRequests.get(panel);
        previous?.abort();
        const controller = new AbortController();
        activeRequests.set(panel, controller);
        if (!append) loading(panel);

        const parameters = new URLSearchParams({
            section: panel.dataset.legendSection,
            language
        });
        if (cursor) parameters.set("cursor", cursor);
        if (search) parameters.set("search", search);
        if (panel.dataset.legendFamilyId) parameters.set("familyId", panel.dataset.legendFamilyId);

        try {
            const response = await fetch(`/founder/legend-connect/sections?${parameters.toString()}`, {
                cache: "no-store",
                credentials: "same-origin",
                headers: { Accept: "application/json" },
                signal: controller.signal
            });
            if (!response.ok) throw new Error("Founder section request failed.");
            if (activeRequests.get(panel) !== controller) return;
            render(panel, await response.json(), append);
        } catch (error) {
            if (error?.name === "AbortError" || activeRequests.get(panel) !== controller) return;
            failed(panel, () => void requestPage(panel, cursor, search, append));
        } finally {
            if (activeRequests.get(panel) === controller) activeRequests.delete(panel);
        }
    }

    document.querySelectorAll("[data-legend-section]").forEach(panel => {
        panel.addEventListener("toggle", () => {
            if (panel.open && !panelState.has(panel)) void requestPage(panel);
        });
    });
})();
