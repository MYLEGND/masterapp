(() => {
  const API_BASE = "/api/proposals";

  const openBtn = document.getElementById("btnProposal");
  const newBtn = document.getElementById("btnProposalNew");
  const menu = document.getElementById("proposalMenu");
  const menuList = document.getElementById("proposalMenuList");
  const overlay = document.getElementById("proposalOverlay");
  const dialog = document.getElementById("proposalDialog");
  const form = document.getElementById("proposalForm");
  const saveBtn = document.getElementById("btnSaveProposal");
  const nameInput = document.getElementById("propName");
  const closeEls = Array.from(document.querySelectorAll("[data-proposal-close]"));
  const currencyInputs = Array.from(document.querySelectorAll(".currency-input"));

  if (!openBtn || !menu || !overlay || !form) return;

  const bucketCount = 3;
  const rowCount = 3;

  let cache = [];
  let editingId = null;
  let currentRecord = null;
  let isSaving = false;

  const pick = (id) => document.getElementById(id);
  const pickInForm = (selector) => form?.querySelector(selector);

  function getBucketEl(bucketNumber) {
    return pickInForm(`.proposal-bucket[data-bucket="${bucketNumber}"]`);
  }

  function getBucketInput(bucketNumber, rowNumber, kind) {
    const bucketEl = getBucketEl(bucketNumber);
    if (!bucketEl) return null;
    if (!rowNumber) {
      if (kind === "carrier") return bucketEl.querySelector(".prop-carrier");
      if (kind === "type") return bucketEl.querySelector(".prop-type");
      return null;
    }
    const rowEl = bucketEl.querySelectorAll(".pb-row")?.[rowNumber - 1];
    if (!rowEl) return null;
    if (kind === "benefit") return rowEl.querySelector(".prop-benefit");
    if (kind === "premium") return rowEl.querySelector(".prop-premium");
    return null;
  }

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

  const normalize = (value) => (value || "").trim().toLowerCase();
  const normalizeKeyPart = (value) => (value || "")
    .toString()
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "");

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
    if (!meta) return "";
    const leadIdKey = normalizeKeyPart(meta.leadId);
    if (leadIdKey) return `lead:${leadIdKey}`;
    const scopeKey = normalizeKeyPart(meta.scopeKey || meta.pageTitle || meta.leadName);
    if (scopeKey) return `scope:${scopeKey}`;
    const queueKey = normalizeKeyPart(meta.queueKey);
    if (queueKey) return `queue:${queueKey}`;
    return "";
  }

  function mergeLeadMeta(existing = {}, fresh = getLeadMeta()) {
    const merged = {
      ...fresh,
      leadId: (existing.leadId || fresh.leadId || "").trim(),
      leadName: (existing.leadName || fresh.leadName || "").trim(),
      queueKey: (existing.queueKey || fresh.queueKey || "").trim(),
      pageTitle: (existing.pageTitle || fresh.pageTitle || "").trim(),
      scopeKey: (existing.scopeKey || fresh.scopeKey || "").trim()
    };
    merged.leadKey = buildLeadKey(merged);
    return merged;
  }

  function sameLead(record, meta = getLeadMeta()) {
    const recKey = normalizeKeyPart(record?.leadKey || buildLeadKey(record));
    const curKey = normalizeKeyPart(buildLeadKey(meta));
    if (recKey && curKey && recKey === curKey) return true;
    const recLeadId = normalizeKeyPart(record?.leadId);
    const curLeadId = normalizeKeyPart(meta?.leadId);
    if (recLeadId && curLeadId && recLeadId === curLeadId) return true;
    return false;
  }

  function matchesScope(record, meta = getLeadMeta()) {
    const recKey = normalizeKeyPart(record?.leadKey || buildLeadKey(record));
    const curKey = normalizeKeyPart(buildLeadKey(meta));
    if (recKey && curKey) return recKey === curKey;
    return false;
  }

  function deepCloneBuckets(sourceBuckets = []) {
    return (sourceBuckets || []).map(b => ({
      type: b?.type || "",
      carrier: b?.carrier || "",
      rows: (b?.rows || []).map(r => ({
        benefit: r?.benefit || "",
        premium: r?.premium || ""
      }))
    }));
  }

  function buildBlankProposalState(bCount = bucketCount, rCount = rowCount, defaultName = "") {
    return {
      name: defaultName || "",
      buckets: Array.from({ length: bCount }, () => ({
        type: "",
        carrier: "",
        rows: Array.from({ length: rCount }, () => ({ benefit: "", premium: "" }))
      }))
    };
  }

  function resetForm(state, opts = { clearAll: false }) {
    if (opts.clearAll && form) {
      Array.from(form.querySelectorAll("input, select, textarea")).forEach(el => { el.value = ""; });
    }
    const proposal = state || buildBlankProposalState(bucketCount, rowCount);
    nameInput.value = proposal.name || "";
    for (let b = 1; b <= bucketCount; b++) {
      const bucket = proposal.buckets?.[b - 1] || { type: "", carrier: "", rows: [] };
      const typeEl = getBucketInput(b, null, "type") || pick(`propType${b}`);
      if (typeEl) typeEl.value = bucket.type || "";
      const carrierEl = getBucketInput(b, null, "carrier") || pick(`propCarrier${b}`);
      if (carrierEl) carrierEl.value = bucket.carrier || "";
      for (let r = 1; r <= rowCount; r++) {
        const row = bucket.rows?.[r - 1] || { benefit: "", premium: "" };
        const benEl = getBucketInput(b, r, "benefit") || pick(`propBenefit${b}${r}`);
        if (benEl) benEl.value = row.benefit || "";
        const preEl = getBucketInput(b, r, "premium") || pick(`propPremium${b}${r}`);
        if (preEl) preEl.value = row.premium || "";
      }
    }
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
    const meta = mergeLeadMeta(
      {
        leadId: dto.leadId || dto.LeadId || "",
        leadName: dto.leadName || dto.LeadName || "",
        queueKey: dto.queueKey || dto.QueueKey || "",
        scopeKey: dto.scopeKey || dto.ScopeKey || "",
        pageTitle: dto.pageTitle || dto.PageTitle || "",
        leadKey: dto.leadKey || dto.LeadKey || ""
      },
      {}
    );
    return {
      id: dto.id || dto.Id || "",
      leadId: meta.leadId,
      leadName: meta.leadName,
      leadKey: meta.leadKey,
      queueKey: meta.queueKey,
      scopeKey: meta.scopeKey,
      pageTitle: meta.pageTitle,
      name: dto.name || dto.Name || "Proposal",
      buckets: deepCloneBuckets(parseBuckets(dto.bucketsJson || dto.BucketsJson)),
      createdUtc: dto.createdUtc || dto.CreatedUtc,
      updatedUtc: dto.updatedUtc || dto.UpdatedUtc
    };
  }

  function upsertCache(record) {
    if (!record || !record.id) return;
    record.leadKey = record.leadKey || buildLeadKey(record);
    const idx = cache.findIndex(x => x.id === record.id);
    if (idx >= 0) cache[idx] = record;
    else cache.push(record);
    cache.sort((a, b) => {
      const aTime = Date.parse(a.updatedUtc || a.createdUtc || 0) || 0;
      const bTime = Date.parse(b.updatedUtc || b.createdUtc || 0) || 0;
      return bTime - aTime;
    });
  }

  async function fetchJson(url, options = {}) {
    const res = await fetch(url, {
      credentials: "same-origin",
      headers: { "Content-Type": "application/json", "X-Requested-With": "XMLHttpRequest", ...(options.headers || {}) },
      ...options
    });
    if (!res.ok) {
      const text = await res.text();
      throw new Error(text || `Request failed (${res.status})`);
    }
    const ct = res.headers.get("content-type") || "";
    if (ct.includes("application/json")) return await res.json();
    return null;
  }

  async function apiList() {
    const data = await fetchJson(API_BASE);
    cache = Array.isArray(data) ? data.map(hydrate).filter(Boolean) : [];
    renderMenu();
  }

  async function apiGet(id) {
    if (!id) return null;
    const dto = await fetchJson(`${API_BASE}/${id}`);
    return hydrate(dto);
  }

  async function apiCreate(payload) {
    const body = {
      leadId: payload.leadId,
      leadName: payload.leadName || "",
      name: payload.name || "Proposal",
      bucketsJson: JSON.stringify(payload.buckets || [])
    };
    const dto = await fetchJson(API_BASE, { method: "POST", body: JSON.stringify(body) });
    const rec = hydrate(dto);
    if (rec) upsertCache(rec);
    return rec;
  }

  async function apiUpdate(id, payload) {
    const body = {
      id,
      leadId: payload.leadId,
      leadName: payload.leadName || "",
      name: payload.name || "Proposal",
      bucketsJson: JSON.stringify(payload.buckets || [])
    };
    const dto = await fetchJson(`${API_BASE}/${id}`, { method: "PUT", body: JSON.stringify(body) });
    const rec = hydrate(dto);
    if (rec) upsertCache(rec);
    return rec;
  }

  async function apiDelete(id) {
    await fetchJson(`${API_BASE}/${id}`, { method: "DELETE" });
    cache = cache.filter(p => p.id !== id);
    renderMenu();
  }

  function collectBuckets() {
    const buckets = [];
    for (let b = 1; b <= bucketCount; b++) {
      const type = (getBucketInput(b, null, "type") || pick(`propType${b}`))?.value?.trim() || "";
      const carrier = (getBucketInput(b, null, "carrier") || pick(`propCarrier${b}`))?.value?.trim() || "";
      const rows = [];
      for (let r = 1; r <= rowCount; r++) {
        rows.push({
          benefit: (getBucketInput(b, r, "benefit") || pick(`propBenefit${b}${r}`))?.value?.trim() || "",
          premium: (getBucketInput(b, r, "premium") || pick(`propPremium${b}${r}`))?.value?.trim() || ""
        });
      }
      buckets.push({ type, carrier, rows });
    }
    return buckets;
  }

  function formatCurrencyValue(raw) {
    if (raw == null) return "";
    const clean = raw.toString().replace(/[^0-9.]/g, "");
    if (!clean) return "";
    const num = Number(clean);
    if (!Number.isFinite(num)) return "";
    return num.toLocaleString("en-US", { minimumFractionDigits: 0, maximumFractionDigits: 2 });
  }

  function attachCurrencyMask() {
    currencyInputs.forEach(input => {
      input.addEventListener("input", () => {
        const caret = input.selectionStart;
        const formatted = formatCurrencyValue(input.value);
        if (formatted !== "") {
          input.value = formatted;
          input.setSelectionRange(formatted.length, formatted.length);
        }
      });
      input.addEventListener("blur", () => {
        input.value = formatCurrencyValue(input.value);
      });
    });
  }

  function renderMenu() {
    const currentLeadId = readLead("leadId");
    if (!menuList) return;
    if (!cache.length) {
      menuList.innerHTML = `<div class="proposal-empty">No proposals saved</div>`;
      return;
    }
    menuList.innerHTML = cache.map(p => `
      <div class="proposal-item${currentLeadId && normalize(p.leadId) === normalize(currentLeadId) ? " is-current" : ""}" data-id="${escapeHtml(p.id)}">
        <div class="proposal-name-wrap" data-prop-open>
          <div class="proposal-name">${escapeHtml(p.name || "Proposal")}</div>
          <div class="proposal-meta-line">${escapeHtml(p.leadName || p.leadId || "Unassigned lead")}</div>
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
    document.querySelectorAll(".ellipsis-menu").forEach(m => m.hidden = true);
  }

  function closeMenu() { menu.hidden = true; }
  function toggleMenu() { renderMenu(); menu.hidden = !menu.hidden; }

  function currentLeadMatches(leadId) {
    return !!leadId && normalize(leadId) === normalize(readLead("leadId"));
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
        if (normalize(event?.detail?.lead?.leadId) === normalize(leadId) || currentLeadMatches(leadId)) {
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
    let selected = false;
    try {
      if (window.LeadBridge?.selectLead) {
        selected = !!(await window.LeadBridge.selectLead({ leadId, queueKey: record.queueKey || getQueueKey() }));
      }
    } catch { }

    if (!selected) {
      try {
        if (window.LeadBridge?.selectLead) {
          selected = !!(await window.LeadBridge.selectLead({ leadId }));
        }
      } catch { }
      }

    return waitForLead(leadId);
  }

  async function openModal(id = null) {
    if (!id) {
      editingId = null;
      currentRecord = null;
      const meta = getLeadMeta();
      const defaultName = meta.leadName ? `${meta.leadName} Proposal` : "";
      resetForm(buildBlankProposalState(bucketCount, rowCount, defaultName), { clearAll: true });
      overlay.hidden = false;
      document.body.classList.add("uw-open");
      return;
    }

    let record = cache.find(p => p.id === id);
    if (!record) {
      try {
        record = await apiGet(id);
        if (record) upsertCache(record);
      } catch { record = null; }
    }

    if (!record) {
      editingId = null;
      currentRecord = null;
      const meta = getLeadMeta();
      const defaultName = meta.leadName ? `${meta.leadName} Proposal` : "";
      resetForm(buildBlankProposalState(bucketCount, rowCount, defaultName), { clearAll: true });
    } else {
      editingId = record.id;
      currentRecord = { ...record, buckets: deepCloneBuckets(record.buckets || []) };
      resetForm(currentRecord, { clearAll: false });
    }
    overlay.hidden = false;
    document.body.classList.add("uw-open");
  }

  function closeModal() {
    overlay.hidden = true;
    document.body.classList.remove("uw-open");
    editingId = null;
    currentRecord = null;
    const meta = getLeadMeta();
    const defaultName = meta.leadName ? `${meta.leadName} Proposal` : "";
    resetForm(buildBlankProposalState(bucketCount, rowCount, defaultName), { clearAll: true });
  }

  async function handleSave(e) {
    e.preventDefault();
    if (isSaving) return;

    const meta = mergeLeadMeta(currentRecord || {}, getLeadMeta());
    const effectiveLeadId = (meta.leadId || "").trim();
    const effectiveLeadName = (meta.leadName || "").trim();

    if (!effectiveLeadId) {
      alert("No lead is selected. Select a lead first.");
      return;
    }

    const payload = {
      leadId: effectiveLeadId,
      leadName: effectiveLeadName || "",
      name: (nameInput.value || "").trim() || "Proposal",
      buckets: deepCloneBuckets(collectBuckets())
    };

    isSaving = true;
    if (saveBtn) saveBtn.disabled = true;

    try {
      if (editingId) {
        await apiUpdate(editingId, payload);
      } else {
        await apiCreate(payload);
      }
      await apiList();
      closeModal();
    } catch (err) {
      console.error("Proposal save failed", err);
      alert("Unable to save proposal right now. Please try again.");
    } finally {
      isSaving = false;
      if (saveBtn) saveBtn.disabled = false;
    }
  }

  menuList?.addEventListener("click", async (e) => {
    const item = e.target.closest(".proposal-item");
    if (!item) return;
    const id = item.getAttribute("data-id");
    const record = cache.find(p => p.id === id);

    if (e.target.matches(".ellipsis-btn")) {
      e.stopPropagation();
      const wrap = e.target.closest(".ellipsis-wrap");
      const drop = wrap?.querySelector(".ellipsis-menu");
      if (drop) drop.hidden = !drop.hidden;
      document.querySelectorAll(".ellipsis-menu").forEach(m => { if (m !== drop) m.hidden = true; });
      return;
    }

    if (e.target.matches("[data-prop-delete]")) {
      if (id) await apiDelete(id);
      return;
    }

    if (e.target.matches("[data-prop-edit]") || e.target.closest("[data-prop-open]")) {
      if (!record) return;
      document.querySelectorAll(".ellipsis-menu").forEach(m => m.hidden = true);
      closeMenu();
      await focusRecordLead(record);
      await openModal(id);
    }
  });

  openBtn.addEventListener("click", async (e) => {
    e.stopPropagation();
    try {
      await apiList();
    } catch (err) {
      console.error("Proposal list refresh failed", err);
    }
    toggleMenu();
  });

  newBtn?.addEventListener("click", (e) => {
    e.stopPropagation();
    closeMenu();
    editingId = null;
    currentRecord = null;
    openModal(null);
  });

  document.addEventListener("click", (e) => {
    if (menu.contains(e.target) || e.target === openBtn) return;
    closeMenu();
  });

  // Only explicit exit buttons close the modal
  overlay.addEventListener("click", (e) => {
    // Intentionally ignore backdrop clicks to prevent accidental close
  });
  closeEls.forEach(el => el.addEventListener("click", closeModal));
  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape") {
      // Keep Escape for menus only; do not close the proposal modal implicitly
      closeMenu();
    }
  });

  ["leadId", "name"].forEach((key) => {
    const el = document.querySelector(`[data-lf-value="${key}"]`);
    if (!el || typeof MutationObserver === "undefined") return;
    const observer = new MutationObserver(() => renderMenu());
    observer.observe(el, { childList: true, characterData: true, subtree: true });
  });

  window.addEventListener("leadbridge:currentLead", () => renderMenu());

  form.addEventListener("submit", handleSave);

  attachCurrencyMask();
  apiList();
})();
