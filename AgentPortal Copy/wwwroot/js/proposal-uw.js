(() => {
  const API_BASE = "/api/proposals";
  const LS_KEY = "legend_proposals_v4";
  const MIGRATION_FLAG = "legend_proposals_v4_server_migrated";

  const openBtn = document.getElementById("btnProposal");
  const newBtn = document.getElementById("btnProposalNew");
  const menu = document.getElementById("proposalMenu");
  const menuList = document.getElementById("proposalMenuList");
  const overlay = document.getElementById("proposalOverlay");
  const dialog = document.getElementById("proposalDialog");
  const form = document.getElementById("proposalForm");
  const saveBtn = document.getElementById("btnSaveProposal");
  const closeEls = Array.from(document.querySelectorAll("[data-proposal-close]"));
  const nameInput = document.getElementById("propName");

  if (!openBtn || !overlay || !form) return;

  const bucketCount = 3;
  const rowCount = 3;
  let cache = [];
  let editingId = null;
  let draftId = null;
  let migrationPromise = null;
  let isSaving = false;

  const pick = (id) => document.getElementById(id);
  const pickInForm = (selector) => form?.querySelector(selector);

  const normalizeKeyPart = (value) => (value || "")
    .toString()
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "");

  const readLead = (key) => {
    const el = document.querySelector(`[data-lf-value="${key}"]`);
    if (!el) return "";
    return (el.textContent || "").replace(/\s+/g, " ").trim();
  };

  const escapeHtml = (value) => (value ?? "").toString()
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");

  function getQueueKey() {
    return (document.querySelector("[data-lead-bridge]")?.getAttribute("data-bucket") || "").trim();
  }

  function getLeadMeta() {
    const current = window.__currentLead || {};
    const leadId = readLead("leadId") || current.leadId || current.id || current.clientUserId || "";
    const leadName = readLead("name") || `${current.firstName || ""} ${current.lastName || ""}`.trim();
    const pageTitle = (document.querySelector("h1")?.textContent || "Proposal").trim();
    const queueKey = getQueueKey();
    const scopeKey = leadId ? `lead:${normalizeKeyPart(leadId)}` : `page:${normalizeKeyPart(leadName || pageTitle)}`;
    return {
      leadId: (leadId || "").trim(),
      leadName: (leadName || "").trim(),
      queueKey,
      pageTitle,
      scopeKey
    };
  }

  function buildLeadKey(meta = getLeadMeta()) {
    const leadIdKey = normalizeKeyPart(meta?.leadId);
    if (leadIdKey) return `lead:${leadIdKey}`;
    const scopeKey = normalizeKeyPart(meta?.scopeKey || meta?.pageTitle || meta?.leadName);
    if (scopeKey) return `scope:${scopeKey}`;
    const queueKey = normalizeKeyPart(meta?.queueKey);
    if (queueKey) return `queue:${queueKey}`;
    return "";
  }

  function sameScope(record, meta = getLeadMeta()) {
    const recKey = normalizeKeyPart(record?.leadKey || buildLeadKey(record));
    const curKey = normalizeKeyPart(buildLeadKey(meta));
    return !!recKey && !!curKey && recKey === curKey;
  }

  function getBucketEl(bucketNumber) {
    return pickInForm(`.proposal-bucket[data-bucket="${bucketNumber}"]`);
  }

  function getBucketInput(bucketNumber, rowNumber, kind) {
    const bucketEl = getBucketEl(bucketNumber);
    if (!bucketEl) return null;
    if (!rowNumber) {
      if (kind === "type") return bucketEl.querySelector(".prop-type");
      if (kind === "carrier") return bucketEl.querySelector(".prop-carrier");
      return null;
    }
    const rowEl = bucketEl.querySelectorAll(".pb-row")?.[rowNumber - 1];
    if (!rowEl) return null;
    if (kind === "benefit") return rowEl.querySelector(".prop-benefit");
    if (kind === "premium") return rowEl.querySelector(".prop-premium");
    return null;
  }

  function deepCloneBuckets(sourceBuckets = []) {
    return (sourceBuckets || []).map((bucket) => ({
      type: bucket?.type || "",
      carrier: bucket?.carrier || "",
      rows: (bucket?.rows || []).map((row) => ({
        benefit: row?.benefit || "",
        premium: row?.premium || ""
      }))
    }));
  }

  function buildBlankState(defaultName = "") {
    return {
      name: defaultName || "",
      buckets: Array.from({ length: bucketCount }, () => ({
        type: "",
        carrier: "",
        rows: Array.from({ length: rowCount }, () => ({ benefit: "", premium: "" }))
      }))
    };
  }

  function resetForm(state, clearAll = false) {
    if (clearAll) {
      Array.from(form.querySelectorAll("input, select, textarea")).forEach((el) => { el.value = ""; });
    }
    const proposal = state || buildBlankState();
    nameInput.value = proposal.name || "";
    for (let b = 1; b <= bucketCount; b++) {
      const bucket = proposal.buckets?.[b - 1] || { type: "", carrier: "", rows: [] };
      const typeEl = getBucketInput(b, null, "type");
      if (typeEl) typeEl.value = bucket.type || "";
      const carrierEl = getBucketInput(b, null, "carrier");
      if (carrierEl) carrierEl.value = bucket.carrier || "";
      for (let r = 1; r <= rowCount; r++) {
        const row = bucket.rows?.[r - 1] || { benefit: "", premium: "" };
        const benefitEl = getBucketInput(b, r, "benefit");
        if (benefitEl) benefitEl.value = row.benefit || "";
        const premiumEl = getBucketInput(b, r, "premium");
        if (premiumEl) premiumEl.value = row.premium || "";
      }
    }
  }

  function collectBuckets() {
    const buckets = [];
    for (let b = 1; b <= bucketCount; b++) {
      const type = getBucketInput(b, null, "type")?.value?.trim() || "";
      const carrier = getBucketInput(b, null, "carrier")?.value?.trim() || "";
      const rows = [];
      for (let r = 1; r <= rowCount; r++) {
        rows.push({
          benefit: getBucketInput(b, r, "benefit")?.value?.trim() || "",
          premium: getBucketInput(b, r, "premium")?.value?.trim() || ""
        });
      }
      buckets.push({ type, carrier, rows });
    }
    return buckets;
  }

  function sortStore(list) {
    const meta = getLeadMeta();
    const curKey = normalizeKeyPart(buildLeadKey(meta));
    return (Array.isArray(list) ? list.slice() : []).sort((a, b) => {
      const aKey = normalizeKeyPart(a?.leadKey || buildLeadKey(a));
      const bKey = normalizeKeyPart(b?.leadKey || buildLeadKey(b));
      const aCur = curKey && aKey === curKey ? 1 : 0;
      const bCur = curKey && bKey === curKey ? 1 : 0;
      if (aCur !== bCur) return bCur - aCur;
      const aTime = Date.parse(a.updatedUtc || a.createdUtc || 0) || 0;
      const bTime = Date.parse(b.updatedUtc || b.createdUtc || 0) || 0;
      if (aTime !== bTime) return bTime - aTime;
      return String(a.name || "").localeCompare(String(b.name || ""));
    });
  }

  function parseBuckets(jsonValue) {
    try {
      const parsed = JSON.parse(jsonValue || "[]");
      if (Array.isArray(parsed)) return parsed;
      if (Array.isArray(parsed.buckets)) return parsed.buckets;
    } catch { }
    return [];
  }

  function hydrate(dto) {
    if (!dto) return null;
    const meta = {
      leadId: (dto.leadId || dto.LeadId || "").trim(),
      leadName: (dto.leadName || dto.LeadName || "").trim(),
      queueKey: (dto.queueKey || dto.QueueKey || "").trim(),
      scopeKey: (dto.scopeKey || dto.ScopeKey || "").trim(),
      pageTitle: (dto.pageTitle || dto.PageTitle || "").trim(),
      leadKey: (dto.leadKey || dto.LeadKey || "").trim()
    };

    return {
      id: dto.id || dto.Id || "",
      ...meta,
      name: dto.name || dto.Name || "Proposal",
      buckets: deepCloneBuckets(parseBuckets(dto.bucketsJson || dto.BucketsJson)),
      isDraft: !!(dto.isDraft ?? dto.IsDraft),
      createdUtc: dto.createdUtc || dto.CreatedUtc,
      updatedUtc: dto.updatedUtc || dto.UpdatedUtc
    };
  }

  async function fetchJson(url, options = {}) {
    const response = await fetch(url, {
      credentials: "same-origin",
      headers: {
        "Content-Type": "application/json",
        "X-Requested-With": "XMLHttpRequest",
        ...(options.headers || {})
      },
      ...options
    });

    if (!response.ok) {
      const text = await response.text();
      throw new Error(text || `Request failed (${response.status})`);
    }

    const contentType = response.headers.get("content-type") || "";
    if (contentType.includes("application/json")) return await response.json();
    return null;
  }

  async function apiList(includeDrafts = true) {
    const query = includeDrafts ? "?includeDrafts=true" : "";
    const data = await fetchJson(`${API_BASE}${query}`);
    cache = sortStore(Array.isArray(data) ? data.map(hydrate).filter(Boolean) : []);
    renderMenu();
    return cache;
  }

  async function apiCreate(payload) {
    const dto = await fetchJson(API_BASE, {
      method: "POST",
      body: JSON.stringify(payload)
    });
    const record = hydrate(dto);
    if (record) upsertCache(record);
    return record;
  }

  async function apiUpdate(id, payload) {
    const dto = await fetchJson(`${API_BASE}/${id}`, {
      method: "PUT",
      body: JSON.stringify(payload)
    });
    const record = hydrate(dto);
    if (record) upsertCache(record);
    return record;
  }

  async function apiDelete(id) {
    await fetchJson(`${API_BASE}/${id}`, { method: "DELETE" });
    cache = cache.filter((record) => record.id !== id);
    renderMenu();
  }

  function upsertCache(record) {
    if (!record?.id) return;
    const index = cache.findIndex((item) => item.id === record.id);
    if (index >= 0) cache[index] = record;
    else cache.push(record);
    cache = sortStore(cache);
    renderMenu();
  }

  function isGuid(value) {
    return /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test((value || "").trim());
  }

  async function migrateLegacyStore() {
    if (localStorage.getItem(MIGRATION_FLAG)) return;

    let local = [];
    try {
      local = JSON.parse(localStorage.getItem(LS_KEY) || "[]") || [];
    } catch {
      local = [];
    }

    if (!Array.isArray(local) || !local.length) {
      localStorage.setItem(MIGRATION_FLAG, new Date().toISOString());
      return;
    }

    for (const record of local) {
      if (!record || !record.leadId) continue;
      const id = isGuid(record.id) ? record.id : crypto.randomUUID();
      const meta = {
        leadId: (record.leadId || "").trim(),
        leadName: (record.leadName || "").trim(),
        queueKey: (record.queueKey || "").trim(),
        scopeKey: (record.scopeKey || "").trim(),
        pageTitle: (record.pageTitle || document.querySelector("h1")?.textContent || "Proposal").trim(),
        leadKey: (record.leadKey || buildLeadKey(record)).trim()
      };

      await apiCreate({
        id,
        leadId: meta.leadId,
        leadName: meta.leadName,
        queueKey: meta.queueKey,
        scopeKey: meta.scopeKey,
        pageTitle: meta.pageTitle,
        leadKey: meta.leadKey,
        name: (record.name || "Proposal").trim() || "Proposal",
        bucketsJson: JSON.stringify(record.buckets || []),
        isDraft: !!record.isDraft
      });
    }

    localStorage.setItem(MIGRATION_FLAG, new Date().toISOString());
  }

  async function ensureServerData() {
    if (!migrationPromise) {
      migrationPromise = (async () => {
        await migrateLegacyStore();
        await apiList(true);
      })().catch((error) => {
        migrationPromise = null;
        throw error;
      });
    }
    return migrationPromise;
  }

  function currentLeadMatches(leadId) {
    return !!leadId && leadId === readLead("leadId");
  }

  function waitForLead(leadId, timeoutMs = 1800) {
    if (!leadId || currentLeadMatches(leadId)) return Promise.resolve(true);
    return new Promise((resolve) => {
      let done = false;
      const cleanup = () => {
        if (done) return;
        done = true;
        window.removeEventListener("leadbridge:currentLead", onLeadChanged);
        window.clearTimeout(timer);
      };
      const onLeadChanged = (event) => {
        if (event?.detail?.lead?.leadId === leadId || currentLeadMatches(leadId)) {
          cleanup();
          resolve(true);
        }
      };
      const timer = window.setTimeout(() => {
        cleanup();
        resolve(currentLeadMatches(leadId));
      }, timeoutMs);
      window.addEventListener("leadbridge:currentLead", onLeadChanged);
    });
  }

  async function focusRecordLead(record) {
    const leadId = (record?.leadId || "").trim();
    if (!leadId) return false;
    try {
      if (window.LeadBridge?.selectLead) {
        await window.LeadBridge.selectLead({ leadId, queueKey: record.queueKey || getQueueKey() });
      }
    } catch { }
    return waitForLead(leadId);
  }

  function renderMenu() {
    if (!menuList) return;
    const meta = getLeadMeta();
    const currentLeadId = meta.leadId;
    const store = sortStore(cache.filter((record) => !record.isDraft));

    if (!store.length) {
      menuList.innerHTML = `<div class="proposal-empty">No proposals saved</div>`;
      return;
    }

    menuList.innerHTML = store.map((record) => `
      <div class="proposal-item${currentLeadId && record.leadId === currentLeadId ? " is-current" : ""}" data-id="${escapeHtml(record.id)}">
        <div class="proposal-name-wrap" data-prop-open>
          <div class="proposal-name">${escapeHtml(record.name || "Proposal")}</div>
          <div class="proposal-meta-line">${escapeHtml(record.leadName || record.leadId || "Unassigned lead")}</div>
        </div>
        <div class="proposal-actions ellipsis-wrap">
          <button type="button" class="btn-mini ellipsis-btn" aria-label="Actions" data-ellipsis="prop">⋮</button>
          <div class="ellipsis-menu" hidden>
            <button type="button" class="btn-mini" data-prop-edit>Edit</button>
            <button type="button" class="btn-mini danger" data-prop-delete>Delete</button>
          </div>
        </div>
      </div>
    `).join("");

    document.querySelectorAll(".ellipsis-menu").forEach((dropdown) => { dropdown.hidden = true; });
  }

  function openModal(record = null) {
    const meta = getLeadMeta();
    const currentDraft = !record ? cache.find((item) => item.isDraft && sameScope(item, meta)) : null;
    const source = record || currentDraft || null;
    const defaultName = meta.leadName ? `${meta.leadName}` : "";

    draftId = source?.isDraft ? source.id : null;
    editingId = source?.id || null;

    if (!source) resetForm(buildBlankState(defaultName), true);
    else resetForm({ ...source, buckets: deepCloneBuckets(source.buckets || []) }, true);

    overlay.hidden = false;
    document.body.classList.add("uw-open");
    dialog?.focus();
  }

  function closeModal() {
    overlay.hidden = true;
    document.body.classList.remove("uw-open");
    editingId = null;
    draftId = null;
    resetForm(buildBlankState(""), true);
  }

  function collectFormPayload() {
    return {
      name: (nameInput?.value || "").trim() || "Proposal",
      buckets: deepCloneBuckets(collectBuckets())
    };
  }

  async function saveRecord(isDraft) {
    const meta = getLeadMeta();
    if (!meta.leadId) return null;

    const payload = collectFormPayload();
    const recordPayload = {
      id: editingId || draftId || crypto.randomUUID(),
      leadId: meta.leadId,
      leadName: meta.leadName,
      queueKey: meta.queueKey,
      scopeKey: meta.scopeKey,
      pageTitle: meta.pageTitle,
      leadKey: buildLeadKey(meta),
      name: payload.name,
      bucketsJson: JSON.stringify(payload.buckets || []),
      isDraft
    };

    const existingId = editingId || draftId;
    const record = existingId
      ? await apiUpdate(existingId, recordPayload)
      : await apiCreate(recordPayload);

    if (record?.isDraft) draftId = record.id;
    else draftId = null;
    editingId = record?.id || editingId;
    return record;
  }

  const debounce = (fn, wait = 350) => {
    let timer = null;
    return (...args) => {
      clearTimeout(timer);
      timer = setTimeout(() => fn(...args), wait);
    };
  };

  const autoSave = debounce(async () => {
    try {
      await ensureServerData();
      await saveRecord(true);
    } catch (error) {
      console.error("Proposal draft autosave failed", error);
    }
  }, 350);

  form.addEventListener("input", autoSave);
  form.addEventListener("change", autoSave);

  form.addEventListener("submit", async (event) => {
    event.preventDefault();
    const meta = getLeadMeta();
    if (!meta.leadId) {
      alert("No lead is selected. Select a lead first.");
      return;
    }
    if (isSaving) return;

    isSaving = true;
    if (saveBtn) saveBtn.disabled = true;

    try {
      await ensureServerData();
      const record = await saveRecord(false);
      editingId = record?.id || null;
      closeModal();
      await apiList(true);
    } catch (error) {
      console.error("Proposal save failed", error);
      alert("Unable to save proposal right now. Please try again.");
    } finally {
      isSaving = false;
      if (saveBtn) saveBtn.disabled = false;
    }
  });

  menuList?.addEventListener("click", async (event) => {
    const item = event.target.closest(".proposal-item");
    if (!item) return;
    const id = item.getAttribute("data-id");
    const record = cache.find((entry) => entry.id === id);

    if (event.target.matches(".ellipsis-btn")) {
      event.stopPropagation();
      const wrap = event.target.closest(".ellipsis-wrap");
      const dropdown = wrap?.querySelector(".ellipsis-menu");
      if (dropdown) dropdown.hidden = !dropdown.hidden;
      document.querySelectorAll(".ellipsis-menu").forEach((menuEl) => { if (menuEl !== dropdown) menuEl.hidden = true; });
      return;
    }

    if (event.target.matches("[data-prop-delete]")) {
      if (!id) return;
      await apiDelete(id);
      return;
    }

    if (event.target.matches("[data-prop-edit]") || event.target.closest("[data-prop-open]")) {
      if (!record) return;
      await focusRecordLead(record);
      document.querySelectorAll(".ellipsis-menu").forEach((menuEl) => { menuEl.hidden = true; });
      closeMenu();
      openModal(record);
    }
  });

  function toggleMenu() {
    renderMenu();
    if (menu) menu.hidden = !menu.hidden;
  }

  function closeMenu() {
    if (menu) menu.hidden = true;
  }

  openBtn.addEventListener("click", async (event) => {
    event.preventDefault();
    event.stopPropagation();
    try {
      await ensureServerData();
    } catch (error) {
      console.error("Proposal list refresh failed", error);
    }
    toggleMenu();
  });

  newBtn?.addEventListener("click", async (event) => {
    event.stopPropagation();
    closeMenu();
    try {
      await ensureServerData();
    } catch (error) {
      console.error("Proposal refresh failed", error);
    }
    editingId = null;
    draftId = null;
    openModal(null);
  });

  document.addEventListener("click", (event) => {
    if (menu && (menu.contains(event.target) || event.target === openBtn)) return;
    closeMenu();
    document.querySelectorAll(".ellipsis-menu").forEach((menuEl) => { menuEl.hidden = true; });
  });

  overlay.addEventListener("click", () => { /* no-op */ });
  closeEls.forEach((el) => el.addEventListener("click", () => closeModal()));
  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape") {
      closeMenu();
      document.querySelectorAll(".ellipsis-menu").forEach((menuEl) => { menuEl.hidden = true; });
    }
  });

  ["leadId", "name"].forEach((key) => {
    const el = document.querySelector(`[data-lf-value="${key}"]`);
    if (!el || typeof MutationObserver === "undefined") return;
    const observer = new MutationObserver(() => renderMenu());
    observer.observe(el, { childList: true, characterData: true, subtree: true });
  });

  window.addEventListener("leadbridge:currentLead", () => renderMenu());

  ensureServerData().catch((error) => {
    console.error("Proposal initialization failed", error);
  });
})();
