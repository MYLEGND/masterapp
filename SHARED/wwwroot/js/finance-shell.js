document.addEventListener("DOMContentLoaded", () => {
    const supportPanels = document.getElementById("financeSupportPanels");
    const toggleButton = document.getElementById("btnToggleSupportPanels");
    const closeSupport = document.getElementById("btnCloseSupportPanels");
    if (!supportPanels) return;

    const setButtonState = (isOpen) => {
        if (!toggleButton) return;
        toggleButton.textContent = isOpen ? "Hide Goals + Alerts" : "Goals + Alerts";
        toggleButton.setAttribute("aria-pressed", isOpen ? "true" : "false");
    };

    const setOpenState = (isOpen) => {
        supportPanels.classList.toggle("d-none", !isOpen);
        supportPanels.setAttribute("aria-hidden", isOpen ? "false" : "true");
        document.body.classList.toggle("finance-support-open", isOpen);
        setButtonState(isOpen);
    };

    toggleButton?.addEventListener("click", () => setOpenState(supportPanels.classList.contains("d-none")));
    closeSupport?.addEventListener("click", () => setOpenState(false));
    supportPanels.querySelector("[data-support-close='true']")?.addEventListener("click", () => setOpenState(false));
    document.addEventListener("keydown", (event) => {
        if (event.key === "Escape") setOpenState(false);
    });

    setButtonState(false);
});
