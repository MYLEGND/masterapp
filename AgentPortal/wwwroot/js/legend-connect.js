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
