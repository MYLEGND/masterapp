(() => {
    const copyButtons = Array.from(document.querySelectorAll("[data-legend-copy-button]"));
    if (copyButtons.length === 0) return;

    const sourceFor = target => Array.from(document.querySelectorAll("[data-legend-copy-source]"))
        .find(source => source.dataset.legendCopySource === target);

    function copyTextFor(source) {
        const copy = source.cloneNode(true);
        copy.querySelectorAll("script, style, form, input, select, textarea, button, [data-legend-copy-button], .legend-connect-summary-pill-action, [aria-hidden='true'], [hidden]")
            .forEach(node => node.remove());
        copy.querySelectorAll("br").forEach(node => node.replaceWith("\n"));
        copy.querySelectorAll("tr").forEach(row => {
            const cells = Array.from(row.querySelectorAll("th, td"))
                .map(cell => cell.textContent.replace(/\s+/g, " ").trim())
                .filter(Boolean);
            row.replaceChildren(document.createTextNode(`\n${cells.join(" | ")}\n`));
        });
        copy.querySelectorAll("h1, h2, h3, h4, p, li, .dashboard-stat-card, .legend-connect-disclosure-status")
            .forEach(node => node.after(document.createTextNode("\n")));

        return copy.textContent
            .split("\n")
            .map(line => line.replace(/[ \t]+/g, " ").trim())
            .filter(Boolean)
            .join("\n");
    }

    async function writeToClipboard(text) {
        if (navigator.clipboard?.writeText) {
            await navigator.clipboard.writeText(text);
            return;
        }

        const fallback = document.createElement("textarea");
        fallback.value = text;
        fallback.setAttribute("readonly", "");
        fallback.style.position = "fixed";
        fallback.style.opacity = "0";
        document.body.append(fallback);
        fallback.select();
        const copied = document.execCommand("copy");
        fallback.remove();
        if (!copied) throw new Error("Clipboard copy was unavailable.");
    }

    function announce(button, copied) {
        const label = button.querySelector("[data-legend-copy-label]");
        if (!label) return;

        label.textContent = copied ? "Copied" : "Copy failed";
        button.classList.toggle("is-copied", copied);
        window.setTimeout(() => {
            label.textContent = "Copy";
            button.classList.remove("is-copied");
        }, 2_000);
    }

    copyButtons.forEach(button => button.addEventListener("click", async () => {
        const source = sourceFor(button.dataset.legendCopyTarget);
        if (!source) return announce(button, false);

        const text = copyTextFor(source);
        if (!text) return announce(button, false);

        try {
            await writeToClipboard(text);
            announce(button, true);
        } catch {
            announce(button, false);
        }
    }));
})();

(() => {
    const form = document.querySelector("[data-verified-target-form]");
    if (!form) return;

    const toggle = form.querySelector("[data-verified-target-toggle]");
    const normalFields = Array.from(form.querySelectorAll("[data-verified-target-normal-fields]"));
    const normalInputs = Array.from(form.querySelectorAll("[data-verified-target-normal-input]"));
    const rowsField = form.querySelector("[data-verified-target-rows]");
    const rowsInput = form.querySelector("[data-verified-target-rows-input]");
    const targetLanguage = form.querySelector("[data-verified-target-language]");
    const targetLanguageLabel = form.querySelector("[data-verified-target-language-label]");
    const modeHelp = form.querySelector("#legendConnectVerifiedTargetModeHelp");
    const submit = form.querySelector("[data-verified-target-submit]");
    if (!toggle || !rowsField || !rowsInput || !targetLanguage || !targetLanguageLabel || !modeHelp || !submit) return;

    function updateMode() {
        const enabled = toggle.checked;
        normalFields.forEach(field => field.hidden = enabled);
        normalInputs.forEach(input => input.disabled = enabled);
        rowsField.hidden = !enabled;
        rowsInput.disabled = !enabled;
        targetLanguage.required = enabled;
        targetLanguageLabel.textContent = enabled
            ? "Target language (required for verified targets)"
            : "Target language (optional — leave empty to queue autonomous expansion)";
        modeHelp.textContent = enabled
            ? "Resolve exact existing sources and attach Founder-approved targets. No source curriculum will be created."
            : "Off: preserve normal Founder curriculum submission. On: resolve existing canonical sources and attach verified targets only.";
        submit.textContent = enabled ? "Apply verified target translations" : "Save approved knowledge";
    }

    toggle.addEventListener("change", updateMode);
    updateMode();
})();

(() => {
    const form = document.querySelector("[data-language-focus-form]");
    if (!form) return;

    const focusToggle = form.querySelector("#legendConnectFocusEnabled");
    const picker = form.querySelector("[data-language-picker]");
    const summary = form.querySelector("[data-language-picker-summary]");
    const status = form.querySelector("[data-language-focus-status]");
    const statusText = form.querySelector("[data-language-focus-status-text]");
    const help = form.querySelector("[data-language-focus-help]");
    const search = form.querySelector("[data-language-picker-search]");
    const empty = form.querySelector("[data-language-picker-empty]");
    const choices = Array.from(form.querySelectorAll("[data-language-choice]"));
    const options = Array.from(form.querySelectorAll("[data-language-picker-option]"));

    if (!focusToggle || !picker || !summary || !status || !statusText || !help) return;

    const selectedChoices = () => choices.filter(choice => choice.checked);
    const selectedNames = () => selectedChoices()
        .map(choice => choice.closest("label")?.innerText?.trim())
        .filter(Boolean);

    function updatePickerSummary() {
        const names = selectedNames();
        if (names.length === 0) {
            summary.textContent = "Select one or more languages";
            return;
        }

        summary.textContent = names.length === 1
            ? names[0]
            : `${names.length} languages selected`;
    }

    function updateFocusState() {
        const enabled = focusToggle.checked;
        status.classList.toggle("legend-connect-focus-status--on", enabled);
        status.classList.toggle("legend-connect-focus-status--off", !enabled);
        statusText.textContent = enabled ? "Focus on" : "Focus off";

        const names = selectedNames();
        help.textContent = enabled
            ? names.length > 0
                ? `This run will use approved English learning sets for: ${names.join(", ")}.`
                : "Select at least one target language before activating this focused run."
            : "Focus is off. All enabled language pairs are prioritized by demand, coverage, and quality.";
    }

    function filterOptions() {
        const query = (search?.value || "").trim().toLocaleLowerCase();
        let visibleCount = 0;

        options.forEach(option => {
            const matches = option.innerText.toLocaleLowerCase().includes(query);
            option.hidden = !matches;
            if (matches) visibleCount++;
        });

        if (empty) empty.hidden = visibleCount !== 0;
    }

    focusToggle.addEventListener("change", updateFocusState);
    choices.forEach(choice => choice.addEventListener("change", () => {
        picker.classList.remove("is-invalid");
        updatePickerSummary();
        updateFocusState();
    }));

    search?.addEventListener("input", filterOptions);

    picker.addEventListener("toggle", () => {
        if (picker.open) {
            filterOptions();
            if (window.matchMedia("(min-width: 721px)").matches) {
                window.setTimeout(() => search?.focus(), 0);
            }
        }
    });

    document.addEventListener("click", event => {
        if (picker.open && !picker.contains(event.target)) picker.open = false;
    });

    document.addEventListener("keydown", event => {
        if (event.key !== "Escape" || !picker.open) return;
        picker.open = false;
        picker.querySelector("summary")?.focus();
    });

    form.addEventListener("submit", event => {
        if (!focusToggle.checked || selectedChoices().length > 0) return;

        event.preventDefault();
        picker.open = true;
        picker.classList.add("is-invalid");
        updateFocusState();
        window.setTimeout(() => search?.focus(), 0);
    });

    updatePickerSummary();
    updateFocusState();
})();

(() => {
    const valueNodes = Array.from(document.querySelectorAll("[data-legend-live-value]"));
    if (valueNodes.length === 0) return;

    const cardNodes = Array.from(document.querySelectorAll("[data-legend-live-card]"));

    function applyTone(card, tone) {
        Array.from(card.classList)
            .filter(className => className.startsWith("legend-connect-metric--"))
            .forEach(className => card.classList.remove(className));

        if (tone) card.classList.add(tone);
    }

    function update(snapshot) {
        const metrics = snapshot?.metrics;
        if (!metrics) return;

        valueNodes.forEach(node => {
            const metric = metrics[node.dataset.legendLiveValue];
            if (metric?.displayValue !== undefined) node.textContent = metric.displayValue;
        });

        cardNodes.forEach(card => {
            const metric = metrics[card.dataset.legendLiveCard];
            if (metric?.tone) applyTone(card, metric.tone);
        });

        window.dispatchEvent(new CustomEvent("legend-connect-live-metrics", {
            detail: snapshot.providerCapacity
        }));
    }

    let isRefreshing = false;

    async function refresh() {
        if (isRefreshing) return;
        isRefreshing = true;
        try {
            const response = await fetch("/founder/legend-connect/metrics", {
                cache: "no-store",
                credentials: "same-origin",
                headers: { Accept: "application/json" }
            });
            if (!response.ok) return;
            update(await response.json());
        } catch {
            // Preserve the last server-rendered projection when a read fails.
        } finally {
            isRefreshing = false;
        }
    }

    document.addEventListener("visibilitychange", () => {
        if (!document.hidden) void refresh();
    });

    window.setInterval(() => {
        if (!document.hidden) void refresh();
    }, 30_000);
})();

(() => {
    const statusNodes = Array.from(document.querySelectorAll("[data-azure-capacity-status]"));
    if (statusNodes.length === 0) return;

    const valueNodes = Array.from(document.querySelectorAll("[data-azure-capacity-value]"));
    const detailNodes = Array.from(document.querySelectorAll("[data-azure-capacity-detail]"));
    const resourceNodes = Array.from(document.querySelectorAll("[data-azure-capacity-resource]"));
    const formatter = new Intl.NumberFormat();
    const valueFor = {
        "monthly-limit": snapshot => snapshot.monthlyIncludedCharacterAllowance,
        "monthly-consumed": snapshot => snapshot.monthlyCharactersConsumed,
        "monthly-reserved": snapshot => snapshot.monthlyReservedCharacters,
        "monthly-remaining": snapshot => snapshot.monthlyRemainingCharacters,
        "monthly-reserve": snapshot => snapshot.monthlyLiveReserveCharacters,
        "monthly-corpus": snapshot => snapshot.maximumSafeCorpusConsumptionCharacters,
        "hourly-limit": snapshot => snapshot.hourlyCharacterLimit,
        "hourly-consumed": snapshot => snapshot.hourlyCharactersConsumed,
        "hourly-reserved": snapshot => snapshot.hourlyReservedCharacters,
        "hourly-remaining": snapshot => snapshot.hourlyRemainingCharacters,
        "hourly-reserve": snapshot => snapshot.hourlyLiveReserveCharacters,
        safe: snapshot => snapshot.safeAcquisitionCharacters
    };
    const monthlyAllowanceFields = new Set([
        "monthly-limit", "monthly-consumed", "monthly-reserved",
        "monthly-remaining", "monthly-reserve", "monthly-corpus"
    ]);

    function update(snapshot) {
        if (!snapshot) return;
        statusNodes.forEach(node => node.textContent = snapshot.status || "Unavailable");
        valueNodes.forEach(node => {
            const value = valueFor[node.dataset.azureCapacityValue]?.(snapshot);
            node.textContent = Number.isFinite(value)
                ? formatter.format(value)
                : snapshot.isSynchronized && monthlyAllowanceFields.has(node.dataset.azureCapacityValue)
                    ? "Metered"
                    : "Unavailable";
        });
        detailNodes.forEach(node => node.textContent = snapshot.detail || "Azure Translator capacity has not synchronized.");
        resourceNodes.forEach(node => {
            const resource = snapshot.resourceName || "Azure Translator";
            const tier = snapshot.tier || "unavailable";
            const window = Number.isFinite(snapshot.hourlyCapacityWindowMinutes)
                ? snapshot.hourlyCapacityWindowMinutes
                : 60;
            node.textContent = `${resource} · tier ${tier}. Monthly billing allowance and rolling ${window}-minute velocity are enforced independently.`;
        });
    }

    window.addEventListener("legend-connect-live-metrics", event => update(event.detail));
})();

(() => {
    const modalElement = document.getElementById("legendConnectMetricSummaryModal");
    const page = document.querySelector(".legend-connect-page");
    if (!modalElement || !page || !window.bootstrap?.Modal) return;

    const modal = bootstrap.Modal.getOrCreateInstance(modalElement);
    const title = modalElement.querySelector("[data-legend-summary-title]");
    const context = modalElement.querySelector("[data-legend-summary-context]");
    const description = modalElement.querySelector("[data-legend-summary-description]");
    const body = modalElement.querySelector("[data-legend-summary-body]");
    const tiles = Array.from(page.querySelectorAll(".dashboard-stat-card"))
        .filter(tile => !tile.closest(".modal") && metricKeyFor(tile));
    const detailTones = ["neutral", "info", "authority", "success", "warning", "danger"];
    let activeTile = null;
    let requestVersion = 0;

    if (!title || !context || !description || !body || tiles.length === 0) return;

    const normalizedText = element => (element?.textContent || "").replace(/\s+/g, " ").trim();

    function toneClass(tone) {
        return detailTones.includes(tone) ? `legend-connect-metric--${tone}` : "legend-connect-metric--neutral";
    }

    function applyModalTone(tone) {
        modalElement.classList.remove(...detailTones.map(toneName => toneClass(toneName)));
        modalElement.classList.add(toneClass(tone));
    }

    function toneForTile(tile) {
        return detailTones.find(tone => tile.classList.contains(toneClass(tone))) || "neutral";
    }

    function metricKeyFor(tile) {
        if (tile.dataset.legendLiveCard) return tile.dataset.legendLiveCard;
        const capacityValue = tile.querySelector("[data-azure-capacity-value]")?.dataset.azureCapacityValue;
        return capacityValue ? `capacity-${capacityValue}` : null;
    }

    function appendText(parent, tagName, className, text) {
        const element = document.createElement(tagName);
        if (className) element.className = className;
        element.textContent = text || "—";
        parent.append(element);
        return element;
    }

    function renderLoading(metricLabel, tone) {
        applyModalTone(tone);
        title.textContent = metricLabel;
        context.textContent = "Loading record-level detail";
        description.textContent = "Loading the current server-backed records behind this metric. No operational data is changed.";
        body.replaceChildren();
        appendText(body, "p", "dashboard-section-copy", "Loading the current record-level detail…");
    }

    function render(snapshot) {
        title.textContent = snapshot.title || "Metric details";
        context.textContent = snapshot.context || "Legend Connect";
        description.textContent = snapshot.description || "Current record-level Legend Connect detail.";
        body.replaceChildren();
        const sections = Array.isArray(snapshot.sections) ? snapshot.sections : [];
        if (sections.length === 0) {
            appendText(body, "p", "dashboard-section-copy", "No current record-level data is available for this metric.");
            return;
        }
        sections.forEach(section => {
            const sectionElement = document.createElement("section");
            sectionElement.className = "dashboard-detail-modal-section";
            const heading = document.createElement("div");
            heading.className = "dashboard-section-head dashboard-section-head-tight";
            const headingContent = document.createElement("div");
            appendText(headingContent, "span", "dashboard-section-kicker", "Record-level detail");
            appendText(headingContent, "h3", "dashboard-section-title", section.title);
            heading.append(headingContent);
            sectionElement.append(heading);
            appendText(sectionElement, "p", "dashboard-section-copy", section.description);
            const rows = Array.isArray(section.rows) ? section.rows : [];
            const rowTones = Array.isArray(section.rowTones) ? section.rowTones : [];
            const columns = Array.isArray(section.columns) ? section.columns : [];
            if (columns.length === 0 || rows.length === 0) {
                appendText(sectionElement, "p", "dashboard-section-copy", "No matching current records.");
            } else {
                const surface = document.createElement("div");
                surface.className = "dashboard-data-surface";
                const table = document.createElement("table");
                table.className = "table dashboard-data-table";
                const thead = document.createElement("thead");
                const headerRow = document.createElement("tr");
                columns.forEach(column => appendText(headerRow, "th", "", column));
                thead.append(headerRow);
                const tbody = document.createElement("tbody");
                rows.forEach((row, rowIndex) => {
                    const dataRow = document.createElement("tr");
                    dataRow.className = toneClass(rowTones[rowIndex]);
                    columns.forEach((_, index) => appendText(dataRow, "td", "", Array.isArray(row) ? row[index] : ""));
                    tbody.append(dataRow);
                });
                table.append(thead, tbody);
                surface.append(table);
                sectionElement.append(surface);
            }
            body.append(sectionElement);
        });
    }

    async function open(tile) {
        activeTile = tile;
        const metricKey = metricKeyFor(tile);
        const request = ++requestVersion;
        renderLoading(normalizedText(tile.querySelector(".dashboard-stat-label")) || "Legend Connect metric", toneForTile(tile));
        modal.show();
        try {
            const response = await fetch(`/founder/legend-connect/metric-details?metric=${encodeURIComponent(metricKey)}`, {
                cache: "no-store",
                credentials: "same-origin",
                headers: { Accept: "application/json" }
            });
            if (!response.ok) throw new Error("Metric detail request failed.");
            const snapshot = await response.json();
            if (request === requestVersion && activeTile === tile) render(snapshot);
        } catch {
            if (request !== requestVersion || activeTile !== tile) return;
            context.textContent = "Record-level detail unavailable";
            description.textContent = "The current dashboard value remains unchanged, but its detailed records could not be loaded. Try again shortly.";
            body.replaceChildren();
            appendText(body, "p", "dashboard-section-copy", "No records were changed or recalculated.");
        }
    }

    tiles.forEach(tile => {
        tile.classList.add("legend-connect-summary-pill");
        tile.dataset.legendSummaryTile = "true";
        tile.setAttribute("role", "button");
        tile.setAttribute("tabindex", "0");
        tile.setAttribute("aria-haspopup", "dialog");
        tile.setAttribute("aria-controls", "legendConnectMetricSummaryModal");
        const action = document.createElement("span");
        action.className = "legend-connect-summary-pill-action";
        action.setAttribute("aria-hidden", "true");
        action.textContent = "View details";
        tile.append(action);
        tile.addEventListener("click", event => {
            event.preventDefault();
            event.stopPropagation();
            open(tile);
        });
        tile.addEventListener("keydown", event => {
            if (event.key !== "Enter" && event.key !== " ") return;
            event.preventDefault();
            event.stopPropagation();
            open(tile);
        });
    });

    modalElement.addEventListener("hidden.bs.modal", () => {
        const returnFocus = activeTile;
        activeTile = null;
        requestVersion++;
        returnFocus?.focus();
    });
})();
