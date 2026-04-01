// Shared numeric formatting helper
// - Formats numbers with thousands separators on blur
// - Strips separators before form submit to preserve model binding
// - Apply to inputs that include the attribute: data-number-format

(function () {
  function parseNumberSafe(raw) {
    if (raw === null || raw === undefined) return "";
    const cleaned = raw.toString().replace(/,/g, "").trim();
    if (cleaned === "") return "";
    const num = Number(cleaned);
    return Number.isFinite(num) ? cleaned : raw;
  }

  function formatWithCommas(raw) {
    if (raw === null || raw === undefined) return "";
    const cleaned = raw.toString().replace(/,/g, "").trim();
    if (cleaned === "") return "";

    const num = Number(cleaned);
    if (!Number.isFinite(num)) return raw;

    const decimals = cleaned.includes(".") ? cleaned.split(".")[1].length : 0;
    return num.toLocaleString("en-US", {
      minimumFractionDigits: decimals,
      maximumFractionDigits: decimals,
    });
  }

  function wireNumberInputs(scope) {
    const inputs = (scope || document).querySelectorAll("input[data-number-format]");
    inputs.forEach((input) => {
      // Allow natural typing: remove commas on focus
      input.addEventListener("focus", () => {
        input.value = parseNumberSafe(input.value);
      });

      // Format on blur
      input.addEventListener("blur", () => {
        input.value = formatWithCommas(input.value);
      });

      // On init, format existing values
      if (input.value) {
        input.value = formatWithCommas(input.value);
      }
    });

    // Strip commas before submit so MVC binds cleanly
    const forms = new Set();
    inputs.forEach((input) => {
      const form = input.form;
      if (form && !forms.has(form)) {
        forms.add(form);
        form.addEventListener("submit", () => {
          const scopedInputs = form.querySelectorAll("input[data-number-format]");
          scopedInputs.forEach((i) => {
            i.value = parseNumberSafe(i.value);
          });
        });
      }
    });
  }

  window.parseNumberSafe = parseNumberSafe;
  window.formatWithCommas = formatWithCommas;
  window.wireNumberInputs = wireNumberInputs;

  document.addEventListener("DOMContentLoaded", () => wireNumberInputs());
})();

