(function () {
    function formatDate(value) {
        if (!value) return "";

        const raw = String(value).trim();

        let m = raw.match(/^(\d{4})-(\d{2})-(\d{2})/);
        if (m) return `${m[2]}/${m[3]}/${m[1]}`;

        m = raw.match(/^(\d{4})\/(\d{2})\/(\d{2})/);
        if (m) return `${m[2]}/${m[3]}/${m[1]}`;

        return raw;
    }

    function applyDateDisplays(root) {
        (root || document).querySelectorAll("[data-display-date]").forEach(el => {
            el.textContent = formatDate(el.textContent || el.value || el.dataset.date);
        });
    }

    window.LegendDates = { formatDate, applyDateDisplays };

    document.addEventListener("DOMContentLoaded", () => applyDateDisplays(document));
})();
