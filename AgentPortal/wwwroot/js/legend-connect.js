(() => {
    const copyButtons = Array.from(document.querySelectorAll("[data-legend-copy-button]"));
    if (copyButtons.length === 0) return;

    const sourceFor = target => Array.from(document.querySelectorAll("[data-legend-copy-source]"))
        .find(source => source.dataset.legendCopySource === target);

    function copyTextFor(source) {
        const copy = source.cloneNode(true);
        copy.querySelectorAll("script, style, form, input, select, textarea, button, [data-legend-copy-button], [aria-hidden='true'], [hidden]")
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
    }

    async function refresh() {
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
        }
    }

    document.addEventListener("visibilitychange", () => {
        if (!document.hidden) void refresh();
    });

    void refresh();
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

    async function refresh() {
        try {
            const response = await fetch("/founder/legend-connect/capacity", {
                cache: "no-store",
                credentials: "same-origin",
                headers: { Accept: "application/json" }
            });
            if (!response.ok) return;
            update(await response.json());
        } catch {
            // The server keeps the last verified projection visible. A failed
            // refresh never invents capacity or changes translation behavior.
        }
    }

    document.addEventListener("visibilitychange", () => {
        if (!document.hidden) void refresh();
    });

    void refresh();
    window.setInterval(() => {
        if (!document.hidden) void refresh();
    }, 30_000);
})();
