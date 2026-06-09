(() => {
  const overlay = window.LegendModal?.ensureInBody("workstationClientPicker") || document.getElementById("workstationClientPicker");
  const dialog = overlay?.querySelector(".ws-client-picker-dialog");
  const searchInput = document.getElementById("workstationClientPickerSearch");
  const resultsEl = document.getElementById("workstationClientPickerResults");
  const statusEl = document.getElementById("workstationClientPickerStatus");
  const titleEl = document.getElementById("workstationClientPickerTitle");
  const subEl = document.getElementById("workstationClientPickerSub");
  const closeButtons = Array.from(document.querySelectorAll("[data-wcp-close]"));
  const lookupUrl = (overlay?.getAttribute("data-lookup-url") || "/Clients/WorkstationLookupClients").trim();
  const refreshViewportOffsets = () => window.LegendModal?.refreshViewportOffsets?.();

  if (!overlay || !dialog || !searchInput || !resultsEl || !statusEl) return;

  let resolver = null;
  let activeFetch = 0;
  let lastResults = [];

  const escapeHtml = (value) => (value ?? "").toString()
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");

  function normalize(item = {}) {
    return {
      clientUserId: (item.clientUserId || "").trim(),
      displayName: (item.displayName || "").trim(),
      email: (item.email || "").trim(),
      phone: (item.phone || "").trim(),
      recordType: (item.recordType || "").trim()
    };
  }

  function renderResults(items = []) {
    lastResults = items.map(normalize).filter((item) => item.clientUserId);
    if (!lastResults.length) {
      resultsEl.innerHTML = `
        <div class="ws-client-picker-empty">
          <div class="ws-client-picker-empty-title">No clients found</div>
          <div class="ws-client-picker-empty-sub">Try a different name, phone, or email.</div>
        </div>`;
      return;
    }

    resultsEl.innerHTML = lastResults.map((item) => `
      <button type="button" class="ws-client-picker-card" data-client-id="${escapeHtml(item.clientUserId)}">
        <span class="ws-client-picker-card-top">
          <span class="ws-client-picker-name">${escapeHtml(item.displayName || "Client")}</span>
          <span class="ws-client-picker-type">${escapeHtml(item.recordType || "Client")}</span>
        </span>
        <span class="ws-client-picker-meta">
          <span>${escapeHtml(item.email || "No email")}</span>
          <span>${escapeHtml(item.phone || "No phone")}</span>
        </span>
      </button>
    `).join("");
  }

  async function searchClients(query = "") {
    const requestId = ++activeFetch;
    statusEl.textContent = query.trim()
      ? "Searching client records..."
      : "Loading recent client records...";

    try {
      const response = await fetch(`${lookupUrl}?q=${encodeURIComponent(query.trim())}`, {
        credentials: "same-origin",
        headers: { "X-Requested-With": "XMLHttpRequest" }
      });

      if (!response.ok) {
        throw new Error(`Lookup failed (${response.status})`);
      }

      const data = await response.json();
      if (requestId !== activeFetch) return;

      renderResults(Array.isArray(data) ? data : []);
      statusEl.textContent = lastResults.length
        ? `${lastResults.length} client${lastResults.length === 1 ? "" : "s"} ready to load.`
        : "No matching clients found.";
    } catch (error) {
      if (requestId !== activeFetch) return;
      console.error("Workstation client lookup failed", error);
      resultsEl.innerHTML = `
        <div class="ws-client-picker-empty">
          <div class="ws-client-picker-empty-title">Client lookup unavailable</div>
          <div class="ws-client-picker-empty-sub">Try again in a moment.</div>
        </div>`;
      statusEl.textContent = "Client lookup unavailable right now.";
    }
  }

  function close(value = null) {
    overlay.hidden = true;
    document.body.classList.remove("ws-client-picker-open");
    activeFetch += 1;
    if (resolver) {
      resolver(value);
      resolver = null;
    }
  }

  function open(options = {}) {
    refreshViewportOffsets();
    if (titleEl) titleEl.textContent = options.title || "Load a client manually";
    if (subEl) subEl.textContent = options.subtitle || "Search your Clients CRM records and launch a proposal or underwriting form without touching lead dialing.";
    overlay.hidden = false;
    document.body.classList.add("ws-client-picker-open");
    searchInput.value = (options.query || "").trim();
    dialog.scrollTop = 0;
    window.setTimeout(() => searchInput.focus(), 30);
    searchClients(searchInput.value);
    return new Promise((resolve) => {
      resolver = resolve;
    });
  }

  const debouncedSearch = (() => {
    let timer = null;
    return () => {
      clearTimeout(timer);
      timer = window.setTimeout(() => searchClients(searchInput.value || ""), 180);
    };
  })();

  searchInput.addEventListener("input", debouncedSearch);
  overlay.addEventListener("click", (event) => {
    if (event.target === overlay) close(null);
  });
  closeButtons.forEach((button) => button.addEventListener("click", () => close(null)));
  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && !overlay.hidden) {
      event.preventDefault();
      close(null);
    }
  });

  resultsEl.addEventListener("click", (event) => {
    const card = event.target.closest(".ws-client-picker-card");
    if (!card) return;
    const clientId = (card.getAttribute("data-client-id") || "").trim();
    const record = lastResults.find((item) => item.clientUserId === clientId);
    if (record) close(record);
  });

  window.WorkstationClientPicker = {
    open
  };
})();
