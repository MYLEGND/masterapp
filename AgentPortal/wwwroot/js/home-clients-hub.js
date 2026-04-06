(() => {
  const hub = document.getElementById("homeClientsHub");
  if (!hub) return;

  const trigger = document.getElementById("homeClientsTrigger");
  const closeBtn = document.getElementById("homeClientsClose");
  const backdrop = document.getElementById("homeClientsBackdrop");
  const searchInput = document.getElementById("homeClientsSearch");
  const results = document.getElementById("homeClientsResults");
  const recentHeading = document.getElementById("homeClientsRecentHeading");
  const recentSub = document.getElementById("homeClientsRecentSub");
  const recentGrid = document.getElementById("homeClientsRecentGrid");
  const addBtn = document.getElementById("homeClientsAdd");
  const crmBtn = document.getElementById("homeClientsOpenCrm");
  const resultStatus = document.getElementById("homeClientsResultStatus");

  const lookupUrl = hub.dataset.lookupUrl || "/Clients/PortalQuickAccessClients";
  const createUrl = hub.dataset.createUrl || "/Clients/Create";
  const editUrl = hub.dataset.editUrl || "/Clients/Edit";
  const crmUrl = hub.dataset.crmUrl || "/Clients";
  const homeReturnUrl = "/Home?clientHub=1";
  const recentKey = "legend.homeClientsHub.recent";

  let isOpen = false;
  let defaultItems = [];
  let activeFetchToken = 0;
  let debounceTimer = null;

  function norm(value) {
    return (value || "").toString().trim();
  }

  function phoneDisplay(value) {
    return norm(value) || "No phone on file";
  }

  function loadRecent() {
    try {
      const raw = window.localStorage.getItem(recentKey);
      const parsed = raw ? JSON.parse(raw) : [];
      return Array.isArray(parsed) ? parsed : [];
    } catch {
      return [];
    }
  }

  function saveRecent(items) {
    try {
      window.localStorage.setItem(recentKey, JSON.stringify(items.slice(0, 6)));
    } catch {
      // ignore storage failures
    }
  }

  function rememberClient(item) {
    if (!item || !item.clientUserId) return;
    const entry = {
      clientUserId: item.clientUserId,
      displayName: item.displayName,
      email: item.email,
      phone: item.phone,
      recordType: item.recordType,
      profileUrl: item.profileUrl,
      openedAt: new Date().toISOString()
    };

    const next = [entry].concat(loadRecent().filter(x => x.clientUserId !== entry.clientUserId));
    saveRecent(next);
    renderRecent(defaultItems);
  }

  function buildCreateHref() {
    const url = new URL(createUrl, window.location.origin);
    url.searchParams.set("returnUrl", homeReturnUrl);
    return `${url.pathname}${url.search}`;
  }

  function buildEditHref(clientUserId) {
    if (!norm(clientUserId)) return editUrl;
    const url = new URL(editUrl, window.location.origin);
    url.searchParams.set("clientUserId", clientUserId);
    url.searchParams.set("returnUrl", homeReturnUrl);
    return `${url.pathname}${url.search}`;
  }

  function openProfile(item) {
    if (!item || !item.clientUserId) return;
    rememberClient(item);
    const href = norm(item.profileUrl) || `/ClientWorkspace/Profile?clientUserId=${encodeURIComponent(item.clientUserId)}`;
    window.open(href, "_blank", "noopener,noreferrer");
  }

  function openEdit(item) {
    if (!item || !item.clientUserId) return;
    rememberClient(item);
    window.location.href = buildEditHref(item.clientUserId);
  }

  function setResultsStatus(message, isError = false) {
    if (!resultStatus) return;
    resultStatus.textContent = message || "";
    resultStatus.dataset.state = isError ? "error" : "info";
  }

  function clearResults() {
    if (!results) return;
    results.innerHTML = "";
    results.hidden = true;
  }

  function createClientCard(item, context = "recent") {
    const card = document.createElement("div");
    card.className = `home-clients-card${context === "result" ? " is-result" : ""}`;

    const titleRow = document.createElement("div");
    titleRow.className = "home-clients-card-top";

    const title = document.createElement("div");
    title.className = "home-clients-card-title";
    title.textContent = norm(item.displayName) || "Client";
    titleRow.appendChild(title);

    const badge = document.createElement("span");
    badge.className = "home-clients-card-badge";
    badge.textContent = norm(item.recordType) || "Client";
    titleRow.appendChild(badge);

    const email = document.createElement("div");
    email.className = "home-clients-card-meta";
    email.textContent = norm(item.email) || "No email on file";

    const phone = document.createElement("div");
    phone.className = "home-clients-card-meta";
    phone.textContent = phoneDisplay(item.phone);

    const cta = document.createElement("div");
    cta.className = "home-clients-card-cta";
    cta.textContent = context === "result" ? "Open or edit from home" : "Client actions";

    const actions = document.createElement("div");
    actions.className = "home-clients-card-actions";

    const openButton = document.createElement("button");
    openButton.type = "button";
    openButton.className = "home-clients-card-action is-primary";
    openButton.textContent = "Open Client Profile";
    openButton.addEventListener("click", () => openProfile(item));

    const editButton = document.createElement("button");
    editButton.type = "button";
    editButton.className = "home-clients-card-action";
    editButton.textContent = "Edit Record";
    editButton.addEventListener("click", () => openEdit(item));

    actions.appendChild(openButton);
    actions.appendChild(editButton);

    card.appendChild(titleRow);
    card.appendChild(email);
    card.appendChild(phone);
    card.appendChild(cta);
    card.appendChild(actions);
    return card;
  }

  function createResultItem(item) {
    return createClientCard(item, "result");
  }

  function renderResults(items) {
    if (!results) return;
    results.innerHTML = "";

    if (!items.length) {
      results.hidden = false;
      const empty = document.createElement("div");
      empty.className = "home-clients-empty";
      empty.textContent = "No portal-enabled clients matched that search.";
      results.appendChild(empty);
      return;
    }

    items.forEach(item => {
      results.appendChild(createResultItem(item));
    });

    results.hidden = false;
  }

  function renderRecent(fallbackItems = []) {
    if (!recentGrid) return;
    recentGrid.innerHTML = "";

    const recentItems = loadRecent();
    const items = recentItems.length ? recentItems : fallbackItems.slice(0, 6);

    if (recentHeading) {
      recentHeading.textContent = recentItems.length ? "Recently Viewed Clients" : "Quick Access Clients";
    }

    if (recentSub) {
      recentSub.textContent = recentItems.length
        ? "The last client profiles you opened are pinned here for fast repeat access."
        : "";
    }

    if (!items.length) {
      const empty = document.createElement("div");
      empty.className = "home-clients-empty home-clients-empty-block";
      empty.textContent = "No client profiles are ready yet.";
      recentGrid.appendChild(empty);
      return;
    }

    items.forEach(item => {
      recentGrid.appendChild(createClientCard(item, "recent"));
    });
  }

  async function fetchClients(query = "") {
    const token = ++activeFetchToken;
    const hasQuery = !!norm(query);
    setResultsStatus(hasQuery ? "Searching clients…" : "");

    try {
      const url = new URL(lookupUrl, window.location.origin);
      if (hasQuery) url.searchParams.set("q", query);
      const response = await fetch(`${url.pathname}${url.search}`, {
        headers: { "X-Requested-With": "XMLHttpRequest" },
        credentials: "same-origin"
      });

      if (!response.ok) {
        throw new Error(`Lookup failed: ${response.status}`);
      }

      const data = await response.json();
      if (token !== activeFetchToken) return;

      const items = Array.isArray(data) ? data : [];
      if (!hasQuery) {
        defaultItems = items.slice();
        clearResults();
      } else {
        renderResults(items);
      }
      renderRecent(defaultItems);
      if (hasQuery) {
        setResultsStatus(items.length ? "Select a client to open the live client profile." : "No portal-enabled clients found.", !items.length);
      } else {
        setResultsStatus("");
      }
    } catch (error) {
      if (token !== activeFetchToken) return;
      clearResults();
      renderRecent(defaultItems);
      setResultsStatus(error?.message || "Unable to load clients right now.", true);
    }
  }

  function debouncedFetch() {
    window.clearTimeout(debounceTimer);
    debounceTimer = window.setTimeout(() => {
      fetchClients(searchInput ? searchInput.value : "");
    }, 180);
  }

  function lockBody() {
    document.body.dataset.homeClientsHubOpen = "true";
    document.body.style.overflow = "hidden";
  }

  function unlockBody() {
    delete document.body.dataset.homeClientsHubOpen;
    document.body.style.overflow = "";
  }

  function openHub() {
    if (isOpen) return;
    isOpen = true;
    hub.hidden = false;
    window.requestAnimationFrame(() => hub.classList.add("open"));
    lockBody();
    fetchClients("");
    window.setTimeout(() => searchInput?.focus(), 80);
  }

  function closeHub() {
    if (!isOpen) return;
    isOpen = false;
    hub.classList.remove("open");
    window.setTimeout(() => {
      if (!isOpen) hub.hidden = true;
    }, 180);
    clearResults();
    if (searchInput) searchInput.value = "";
    unlockBody();
  }

  trigger?.addEventListener("click", openHub);
  closeBtn?.addEventListener("click", closeHub);
  backdrop?.addEventListener("click", closeHub);
  addBtn?.addEventListener("click", () => {
    window.location.href = buildCreateHref();
  });
  crmBtn?.addEventListener("click", () => {
    window.location.href = crmUrl;
  });

  searchInput?.addEventListener("input", debouncedFetch);
  searchInput?.addEventListener("focus", () => {
    if (norm(searchInput.value)) fetchClients(searchInput.value || "");
  });

  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && isOpen) {
      closeHub();
      return;
    }

    if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === "k") {
      return;
    }

    if (!isOpen && event.key.toLowerCase() === "c" && (event.metaKey || event.ctrlKey)) {
      event.preventDefault();
      openHub();
    }
  });

  const params = new URLSearchParams(window.location.search || "");
  if (params.get("clientHub") === "1") {
    openHub();
    params.delete("clientHub");
    const next = `${window.location.pathname}${params.toString() ? `?${params.toString()}` : ""}${window.location.hash || ""}`;
    window.history.replaceState({}, document.title, next);
  } else {
    renderRecent([]);
  }
})();
