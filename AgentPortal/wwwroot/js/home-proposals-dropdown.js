(() => {
  "use strict";

  const API_BASE = "/api/proposals";

  const openBtn    = document.getElementById("hpBtnProposal");
  const newBtn     = document.getElementById("hpBtnNew");
  const menu       = document.getElementById("hpProposalMenu");
  const menuList   = document.getElementById("hpProposalList");
  const overlay    = document.getElementById("hpProposalOverlay");
  const form       = document.getElementById("hpProposalForm");
  const saveBtn    = document.getElementById("hpBtnSave");
  const nameInput  = document.getElementById("hpPropName");
  const clientInput = document.getElementById("hpLeadName");
  const closeEls   = Array.from(document.querySelectorAll("[data-hp-close]"));

  if (!openBtn || !menu || !menuList || !overlay || !form) return;

  const bucketCount = 3;
  const rowCount    = 3;

  let cache     = [];
  let editingId = null;
  let isSaving  = false;

  const esc = (v) => (v ?? "").toString()
    .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
    .replace(/\"/g, "&quot;").replace(/'/g, "&#39;");

  function getBucketInput(b, r, kind) {
    const bucket = form.querySelector(`.hp-bucket[data-bucket="${b}"]`);
    if (!bucket) return null;
    if (!r) {
      if (kind === "carrier") return bucket.querySelector(".prop-carrier");
      if (kind === "term")    return bucket.querySelector(".prop-term");
      if (kind === "type")    return bucket.querySelector(".prop-type");
      return null;
    }
    const row = bucket.querySelectorAll(".hp-row, .pb-row")?.[r - 1];
    if (!row) return null;
    if (kind === "benefit") return row.querySelector(".prop-benefit");
    if (kind === "premium") return row.querySelector(".prop-premium");
    return null;
  }

  async function fetchJson(url, opts = {}) {
    const method = (opts.method || "GET").toUpperCase();
    const headers = {
      "Content-Type": "application/json",
      "X-Requested-With": "XMLHttpRequest",
      ...(opts.headers || {})
    };
    if (!["GET", "HEAD", "OPTIONS", "TRACE"].includes(method)) {
      const tok = document.querySelector('input[name="__RequestVerificationToken"]')?.value || "";
      if (tok) headers.RequestVerificationToken = tok;
    }
    const res = await fetch(url, { credentials: "same-origin", headers, ...opts });
    if (!res.ok) {
      const text = await res.text();
      throw new Error(text || `Request failed (${res.status})`);
    }
    const ct = res.headers.get("content-type") || "";
    if (ct.includes("application/json")) return await res.json();
    return null;
  }

  function parseBuckets(json) {
    try {
      const v = JSON.parse(json || "[]");
      return Array.isArray(v) ? v : (Array.isArray(v?.buckets) ? v.buckets : []);
    } catch { return []; }
  }

  function hydrate(dto) {
    if (!dto) return null;
    return {
      id:         (dto.id         || dto.Id         || "").toString(),
      leadId:     (dto.leadId     || dto.LeadId     || "").trim(),
      leadName:   (dto.leadName   || dto.LeadName   || "").trim(),
      name:       (dto.name       || dto.Name       || "Proposal").trim(),
      buckets:    parseBuckets(dto.bucketsJson || dto.BucketsJson),
      updatedUtc: dto.updatedUtc || dto.UpdatedUtc || null,
      createdUtc: dto.createdUtc || dto.CreatedUtc || null,
    };
  }

  function sortCache() {
    cache.sort((a, b) => {
      const at = Date.parse(a.updatedUtc || a.createdUtc || 0) || 0;
      const bt = Date.parse(b.updatedUtc || b.createdUtc || 0) || 0;
      return bt - at;
    });
  }

  function upsertCache(rec) {
    if (!rec?.id) return;
    const idx = cache.findIndex(p => p.id === rec.id);
    if (idx >= 0) cache[idx] = rec;
    else cache.unshift(rec);
    sortCache();
  }

  async function apiList() {
    const data = await fetchJson(`${API_BASE}?includeDrafts=true`);
    cache = Array.isArray(data) ? data.map(hydrate).filter(Boolean) : [];
    sortCache();
    renderMenu();
  }

  async function apiCreate(payload) {
    const body = JSON.stringify({
      leadId:      (payload.clientName || "GLOBAL").trim() || "GLOBAL",
      leadName:    (payload.clientName || "").trim(),
      name:        (payload.name || "Proposal").trim(),
      bucketsJson: JSON.stringify(payload.buckets || []),
    });
    const dto = await fetchJson(API_BASE, { method: "POST", body });
    const rec = hydrate(dto);
    if (rec) upsertCache(rec);
    return rec;
  }

  async function apiUpdate(id, payload) {
    const existing = cache.find(p => p.id === id);
    const body = JSON.stringify({
      id,
      leadId:      existing?.leadId || (payload.clientName || "GLOBAL").trim() || "GLOBAL",
      leadName:    (payload.clientName || existing?.leadName || "").trim(),
      name:        (payload.name || "Proposal").trim(),
      bucketsJson: JSON.stringify(payload.buckets || []),
    });
    const dto = await fetchJson(`${API_BASE}/${id}`, { method: "PUT", body });
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
      const type    = getBucketInput(b, null, "type")?.value?.trim()    || "";
      const carrier = getBucketInput(b, null, "carrier")?.value?.trim() || "";
      const term    = getBucketInput(b, null, "term")?.value?.trim()    || "";
      const rows    = [];
      for (let r = 1; r <= rowCount; r++) {
        rows.push({
          benefit: getBucketInput(b, r, "benefit")?.value?.trim() || "",
          premium: getBucketInput(b, r, "premium")?.value?.trim() || "",
        });
      }
      buckets.push({ type, carrier, term, rows });
    }
    return buckets;
  }

  function resetForm(record = null) {
    if (nameInput) nameInput.value = record?.name || "";
    if (clientInput) clientInput.value = record?.leadName || "";
    const buckets = record?.buckets || [];
    for (let b = 1; b <= bucketCount; b++) {
      const bucket = buckets[b - 1] || { type: "", carrier: "", term: "", rows: [] };
      const typeEl    = getBucketInput(b, null, "type");
      const carrierEl = getBucketInput(b, null, "carrier");
      const termEl    = getBucketInput(b, null, "term");
      if (typeEl) typeEl.value = bucket.type || "";
      if (carrierEl) carrierEl.value = bucket.carrier || "";
      if (termEl) termEl.value = bucket.term || "";
      for (let r = 1; r <= rowCount; r++) {
        const row = bucket.rows?.[r - 1] || {};
        const benEl = getBucketInput(b, r, "benefit");
        const preEl = getBucketInput(b, r, "premium");
        if (benEl) benEl.value = row.benefit || "";
        if (preEl) preEl.value = row.premium || "";
      }
    }
  }

  function formatCurrency(raw) {
    const clean = (raw || "").toString().replace(/[^0-9.]/g, "");
    if (!clean) return "";
    const num = Number(clean);
    return Number.isFinite(num)
      ? num.toLocaleString("en-US", { minimumFractionDigits: 0, maximumFractionDigits: 2 })
      : "";
  }

  function attachCurrencyMask() {
    form.querySelectorAll(".currency-input").forEach(input => {
      input.addEventListener("input", () => {
        const f = formatCurrency(input.value);
        if (f !== "") input.value = f;
      });
      input.addEventListener("blur", () => {
        input.value = formatCurrency(input.value);
      });
    });
  }

  function fmtDate(iso) {
    if (!iso) return "";
    const d = new Date(iso);
    if (isNaN(d.getTime())) return "";
    return d.toLocaleDateString("en-US", { month: "short", day: "numeric", year: "numeric" });
  }

  function renderMenu() {
    if (!cache.length) {
      menuList.innerHTML = `<div class="hp-proposal-empty">No proposals saved</div>`;
      return;
    }

    menuList.innerHTML = cache.map((p) => `
      <div class="hp-proposal-item" data-id="${esc(p.id)}">
        <div class="hp-proposal-name-wrap" data-hp-open>
          <div class="hp-proposal-name">${esc(p.name || "Proposal")}</div>
          <div class="hp-proposal-meta-line">${esc(p.leadName || p.leadId || "Unassigned")} · ${esc(fmtDate(p.updatedUtc || p.createdUtc))}</div>
        </div>
        <div class="hp-proposal-actions hp-ellipsis-wrap">
          <button type="button" class="btn-mini hp-ellipsis-btn" aria-label="Actions" data-hp-ellipsis>⋮</button>
          <div class="hp-ellipsis-menu" hidden>
            <button type="button" class="btn-mini" data-hp-edit>Edit</button>
            <button type="button" class="btn-mini danger" data-hp-delete>Delete</button>
          </div>
        </div>
      </div>
    `).join("");

    document.querySelectorAll(".hp-ellipsis-menu").forEach((el) => { el.hidden = true; });
  }

  function openModal(record = null) {
    editingId = record?.id || null;
    resetForm(record);
    overlay.hidden = false;
    document.body.classList.add("hp-modal-open");
    setTimeout(() => nameInput?.focus(), 40);
  }

  function closeModal() {
    overlay.hidden = true;
    document.body.classList.remove("hp-modal-open");
    editingId = null;
    resetForm(null);
  }

  async function handleSave(e) {
    e.preventDefault();
    if (isSaving) return;

    const payload = {
      clientName: (clientInput?.value || "").trim(),
      name: (nameInput?.value || "").trim() || "Proposal",
      buckets: collectBuckets(),
    };

    isSaving = true;
    if (saveBtn) saveBtn.disabled = true;
    try {
      if (editingId) await apiUpdate(editingId, payload);
      else await apiCreate(payload);
      await apiList();
      closeModal();
    } catch (err) {
      const msg = (err?.message || "").trim();
      alert(msg ? `Unable to save proposal: ${msg}` : "Unable to save proposal. Please try again.");
    } finally {
      isSaving = false;
      if (saveBtn) saveBtn.disabled = false;
    }
  }

  function closeMenu() { menu.hidden = true; }

  openBtn.addEventListener("click", async (e) => {
    e.preventDefault();
    e.stopPropagation();
    try { await apiList(); } catch (err) { console.error(err); }
    menu.hidden = !menu.hidden;
  });

  newBtn?.addEventListener("click", (e) => {
    e.stopPropagation();
    closeMenu();
    openModal(null);
  });

  menuList.addEventListener("click", async (e) => {
    const item = e.target.closest(".hp-proposal-item");
    if (!item) return;

    const id = item.getAttribute("data-id");
    const rec = cache.find((x) => x.id === id);

    if (e.target.matches("[data-hp-ellipsis]")) {
      e.stopPropagation();
      const wrap = e.target.closest(".hp-ellipsis-wrap");
      const menuEl = wrap?.querySelector(".hp-ellipsis-menu");
      if (menuEl) menuEl.hidden = !menuEl.hidden;
      document.querySelectorAll(".hp-ellipsis-menu").forEach((m) => { if (m !== menuEl) m.hidden = true; });
      return;
    }

    if (e.target.matches("[data-hp-delete]")) {
      if (!id) return;
      if (confirm(`Delete "${rec?.name || "this proposal"}"?`)) {
        try { await apiDelete(id); } catch { alert("Could not delete. Please try again."); }
      }
      return;
    }

    if (!rec) return;
    document.querySelectorAll(".hp-ellipsis-menu").forEach((m) => { m.hidden = true; });
    closeMenu();
    openModal(rec);
  });

  closeEls.forEach(el => el.addEventListener("click", closeModal));

  document.addEventListener("click", (e) => {
    if (menu.contains(e.target) || e.target === openBtn) return;
    closeMenu();
    document.querySelectorAll(".hp-ellipsis-menu").forEach((m) => { m.hidden = true; });
  });

  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape") {
      if (!overlay.hidden) closeModal();
      closeMenu();
    }
  });

  form.addEventListener("submit", handleSave);
  attachCurrencyMask();

  const hub = document.getElementById("homeClientsHub");
  if (hub) {
    new MutationObserver(() => {
      if (hub.classList.contains("open")) apiList().catch(console.error);
    }).observe(hub, { attributes: true, attributeFilter: ["class"] });
  }
})();
