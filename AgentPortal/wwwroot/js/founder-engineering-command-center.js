(() => {
    const root = document.querySelector("[data-engineering-command-center]");
    if (!root) return;

    const form = root.querySelector("[data-contract-form]");
    const dirtyState = root.querySelector("[data-dirty-state]");
    const quickInput = root.querySelector("#engQuickCommand");
    const shared = root.querySelector("#SharedDirective");
    let dirty = false;

    const setDirty = () => {
        if (dirty) return;
        dirty = true;
        if (dirtyState) dirtyState.textContent = "Unsaved contract changes";
    };

    const updateCount = (textarea) => {
        const counter = root.querySelector(`[data-count-for="${textarea.id}"]`);
        if (!counter) return;
        const max = Number(textarea.getAttribute("maxlength") || "0");
        counter.textContent = max > 0
            ? `${textarea.value.length.toLocaleString()} / ${max.toLocaleString()}`
            : textarea.value.length.toLocaleString();
    };

    root.querySelectorAll("textarea[maxlength]").forEach((textarea) => {
        updateCount(textarea);
        textarea.addEventListener("input", () => {
            updateCount(textarea);
            setDirty();
        });
    });

    root.querySelectorAll('input[type="checkbox"]').forEach((input) => {
        input.addEventListener("change", setDirty);
    });

    const addButton = root.querySelector("[data-add-command]");
    if (addButton && quickInput && shared) {
        const appendCommand = () => {
            const value = quickInput.value.trim();
            if (!value) return;
            const prefix = shared.value.trim().length ? "\n\n" : "";
            const next = `${shared.value.trimEnd()}${prefix}- ${value}`;
            if (next.length > Number(shared.maxLength || 12000)) return;
            shared.value = next;
            quickInput.value = "";
            updateCount(shared);
            setDirty();
            shared.focus();
            shared.setSelectionRange(shared.value.length, shared.value.length);
        };

        addButton.addEventListener("click", appendCommand);
        quickInput.addEventListener("keydown", (event) => {
            if (event.key !== "Enter") return;
            event.preventDefault();
            appendCommand();
        });
    }

    if (form) {
        form.addEventListener("submit", () => {
            dirty = false;
            if (dirtyState) dirtyState.textContent = "Publishing new contract revision…";
        });
    }

    root.querySelectorAll("[data-restore-form]").forEach((restoreForm) => {
        restoreForm.addEventListener("submit", (event) => {
            if (!window.confirm("Restore this revision as a new live contract revision?")) {
                event.preventDefault();
            }
        });
    });

    const disconnectForm = root.querySelector("[data-disconnect-form]");
    if (disconnectForm) {
        disconnectForm.addEventListener("submit", (event) => {
            if (!window.confirm("Disconnect the ChatGPT plan from LEGEND Engineering? New model execution will stop until the plan is reconnected.")) {
                event.preventDefault();
            }
        });
    }

    window.addEventListener("beforeunload", (event) => {
        if (!dirty) return;
        event.preventDefault();
        event.returnValue = "";
    });
})();
