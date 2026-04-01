(() => {
  const API_BASE = "/api/underwriting";
  const LS_KEY = "legend_underwriting_v3";
  const LEGACY_PREFIX = "legend_underwriting_v2:";
  const MIGRATION_FLAG = "legend_underwriting_v3_server_migrated";

  document.addEventListener("DOMContentLoaded", init);

  function init() {
    const openBtn = document.getElementById("btnUnderwriting");
    const newBtn = document.getElementById("btnUwNew");
    const menu = document.getElementById("uwMenu");
    const menuList = document.getElementById("uwMenuList");
    const overlay = document.getElementById("uwOverlay");
    const dialog = document.getElementById("uwDialog");
    const form = document.getElementById("uwForm");
    const saveBtn = document.getElementById("uwSave");
    const downloadBtn = document.getElementById("btnUwDownload");
    const closeEls = Array.from(document.querySelectorAll("[data-uw-close]"));

    if (!openBtn || !overlay || !form) return;

    let cache = [];
    let editingId = null;
    let isSaving = false;
    let migrationPromise = null;

    const pick = (id) => document.getElementById(id);
    const fields = {
      date: pick("uwDate"),
      clientName: pick("uwClientName"),
      occupation: pick("uwOccupation"),
      phone: pick("uwPhone"),
      phone2: pick("uwPhone2"),
      email: pick("uwEmail"),
      address: pick("uwAddress"),
      city: pick("uwCity"),
      state: pick("uwState"),
      zip: pick("uwZip"),
      county: pick("uwCounty"),
      dob: pick("uwDob"),
      age: pick("uwAge"),
      gender: pick("uwGender"),
      height: pick("uwHeight"),
      weight: pick("uwWeight"),
      lender: pick("uwLender"),
      loan: pick("uwLoan"),
      homeValue: pick("uwHomeValue"),
      equity: pick("uwEquity"),
      payment: pick("uwPayment"),
      income: pick("uwIncome"),
      addlIncome: pick("uwAddlIncome"),
      coverage: pick("uwCoverage"),
      beneficiary: pick("uwBeneficiary"),
      beneficiary2: pick("uwBeneficiary2"),
      beneficiary3: pick("uwBeneficiary3"),
      beneficiary4: pick("uwBeneficiary4"),
      bank: pick("uwBank"),
      routing: pick("uwRouting"),
      account: pick("uwAccount"),
      dln: pick("uwDln"),
      ssn: pick("uwSsn")
    };

    const selectFields = {
      nicotine: pick("uwNicotine"),
      housingStatus: pick("uwHousingStatus"),
      term: pick("uwTerm"),
      savings: pick("uwSavings"),
      healthConditions: pick("uwHealthConditions"),
      emergencyAssets: pick("uwEmergencyAssets")
    };

    const textAreas = {
      health: pick("uwHealth"),
      meds: pick("uwMeds"),
      surgeries: pick("uwSurgeries"),
      notes: pick("uwNotes"),
      familyPlan: pick("uwFamilyPlan"),
      afford: pick("uwAfford")
    };

    const otherFields = {
      bankruptcies: pick("uwBankruptcies"),
      dui: pick("uwDui"),
      lifeCoverage: pick("uwLifeCoverage")
    };

    const currencyIds = ["uwLoan", "uwHomeValue", "uwEquity", "uwPayment", "uwIncome", "uwAddlIncome", "uwCoverageAmount", "uwPremium"];

    function toNumber(raw) {
      if (raw == null) return null;
      const num = Number((raw || "").toString().replace(/[^0-9.]/g, ""));
      return Number.isFinite(num) ? num : null;
    }

    function updateEquityFromInputs() {
      const loanVal = toNumber(fields.loan?.value);
      const homeVal = toNumber(fields.homeValue?.value);
      if (loanVal == null && homeVal == null) return;
      if (homeVal == null) return;
      const eq = loanVal == null ? homeVal : Math.max(0, homeVal - loanVal);
      if (Number.isFinite(eq) && fields.equity) {
        fields.equity.value = eq.toLocaleString("en-US", { minimumFractionDigits: 0, maximumFractionDigits: 0 });
      }
    }

    function formatCurrencyValue(raw) {
      if (raw == null) return "";
      const clean = raw.toString().replace(/[^0-9.]/g, "");
      if (!clean) return "";
      const num = Number(clean);
      if (!Number.isFinite(num)) return "";
      return num.toLocaleString("en-US", { minimumFractionDigits: 0, maximumFractionDigits: 2 });
    }

    function attachCurrencyMasks() {
      currencyIds.forEach((id) => {
        const el = document.getElementById(id);
        if (!el) return;
        el.addEventListener("input", () => {
          const formatted = formatCurrencyValue(el.value);
          if (formatted !== "") el.value = formatted;
          if (id === "uwLoan" || id === "uwHomeValue") updateEquityFromInputs();
        });
        el.addEventListener("blur", () => {
          el.value = formatCurrencyValue(el.value);
          if (id === "uwLoan" || id === "uwHomeValue") updateEquityFromInputs();
        });
      });
    }

    function clearSelect(el) {
      if (!el) return;
      if (el.multiple) {
        Array.from(el.options).forEach((option) => { option.selected = false; });
        if (el._multiButtons) el._multiButtons.forEach((btn) => btn.classList.remove("is-selected"));
      } else {
        el.value = "";
      }
    }

    function readSelect(el) {
      if (!el) return "";
      if (el.multiple) return Array.from(el.selectedOptions || []).map((option) => option.value).filter(Boolean);
      return (el.value || "").trim();
    }

    function setSelect(el, value) {
      if (!el) return;
      if (!el.multiple) {
        el.value = value ?? "";
        return;
      }
      const values = Array.isArray(value)
        ? value
        : typeof value === "string"
          ? value.split(",").map((part) => part.trim()).filter(Boolean)
          : [];
      const selected = new Set(values);
      Array.from(el.options).forEach((option) => { option.selected = selected.has(option.value); });
      if (el._multiButtons) el._multiButtons.forEach((btn) => btn.classList.toggle("is-selected", selected.has(btn.dataset.value)));
    }

    const readLead = (key) => {
      const el = document.querySelector(`[data-lf-value="${key}"]`);
      if (!el) return "";
      return (el.textContent || "").replace(/\s+/g, " ").trim();
    };

    const normalizeKeyPart = (value) => (value || "")
      .toString()
      .trim()
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, "-")
      .replace(/^-+|-+$/g, "") || "unknown";

    function escapeHtml(value) {
      return (value ?? "").toString()
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;")
        .replace(/'/g, "&#39;");
    }

    function getQueueKey() {
      return (document.querySelector("[data-lead-bridge]")?.getAttribute("data-bucket") || "").trim();
    }

    function getLeadMeta() {
      const leadId = readLead("leadId");
      const leadName = readLead("name");
      return {
        leadId,
        leadName,
        queueKey: getQueueKey(),
        pageTitle: (document.querySelector("h1")?.textContent || "Underwriting").trim(),
        scopeKey: leadId ? `lead:${normalizeKeyPart(leadId)}` : `page:${normalizeKeyPart(leadName)}`
      };
    }

    function mergeLeadMeta(existing = {}, fresh = getLeadMeta()) {
      return {
        ...fresh,
        leadId: (existing.leadId || fresh.leadId || "").trim(),
        leadName: (existing.leadName || fresh.leadName || "").trim(),
        queueKey: (existing.queueKey || fresh.queueKey || "").trim(),
        pageTitle: (existing.pageTitle || fresh.pageTitle || "").trim(),
        scopeKey: (existing.scopeKey || fresh.scopeKey || "").trim()
      };
    }

    function currentScopeMatches(record, meta = getLeadMeta()) {
      if (!record) return false;
      const recLead = (record.leadId || "").trim();
      const curLead = (meta.leadId || "").trim();
      if (recLead && curLead) return recLead === curLead;
      const recScope = (record.scopeKey || "").trim();
      const curScope = (meta.scopeKey || "").trim();
      if (recScope && curScope) return recScope === curScope;
      const recPage = (record.pageTitle || "").trim();
      const curPage = (meta.pageTitle || "").trim();
      return !!recPage && !!curPage && recPage === curPage;
    }

    function sortStore(list) {
      const currentLeadId = getLeadMeta().leadId;
      return (Array.isArray(list) ? list.slice() : []).sort((a, b) => {
        const aCurrent = currentLeadId && a.leadId === currentLeadId ? 1 : 0;
        const bCurrent = currentLeadId && b.leadId === currentLeadId ? 1 : 0;
        if (aCurrent !== bCurrent) return bCurrent - aCurrent;
        if (!!a.isDraft !== !!b.isDraft) return a.isDraft ? -1 : 1;
        const aTime = Date.parse(a.updatedUtc || a.createdUtc || 0) || 0;
        const bTime = Date.parse(b.updatedUtc || b.createdUtc || 0) || 0;
        if (aTime !== bTime) return bTime - aTime;
        return String(a.name || "").localeCompare(String(b.name || ""));
      });
    }

    function productCode() {
      return (overlay.getAttribute("data-uw-product") || "MP").trim().toUpperCase();
    }

    function overlaySubtitle() {
      return (overlay.getAttribute("data-uw-subtitle") || document.querySelector("#uwDialog .uw-sub")?.textContent || "Underwriting").trim();
    }

    function isGuid(value) {
      return /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test((value || "").trim());
    }

    function parseLegacyMetaFromKey(key) {
      const meta = {
        leadId: "",
        leadName: "",
        queueKey: getQueueKey(),
        pageTitle: (document.querySelector("h1")?.textContent || "Underwriting").trim(),
        scopeKey: ""
      };
      if (!key.startsWith(LEGACY_PREFIX)) return meta;
      const scope = key.slice(LEGACY_PREFIX.length);
      meta.scopeKey = scope;
      if (scope.startsWith("lead:")) {
        meta.leadId = scope.slice("lead:".length).trim();
        return meta;
      }
      const pageMatch = scope.match(/page:([^:]+)/i);
      const nameMatch = scope.match(/name:([^:]+)/i);
      if (pageMatch) meta.pageTitle = pageMatch[1].replace(/-/g, " ");
      if (nameMatch) meta.leadName = nameMatch[1].replace(/-/g, " ");
      return meta;
    }

    function mergeRecords(...lists) {
      const merged = new Map();
      lists.flat().forEach((record) => {
        if (!record) return;
        const key = record.id || `${record.leadId || ""}|${record.scopeKey || ""}|${record.updatedUtc || ""}`;
        if (!key) return;
        const current = merged.get(key) || {};
        merged.set(key, { ...current, ...record });
      });
      return Array.from(merged.values());
    }

    function getLegacyLocalRecords() {
      let current = [];
      try {
        current = JSON.parse(localStorage.getItem(LS_KEY) || "[]") || [];
      } catch {
        current = [];
      }

      const legacyRecords = [];
      for (let i = 0; i < localStorage.length; i++) {
        const key = localStorage.key(i);
        if (!key || !key.startsWith(LEGACY_PREFIX)) continue;
        let parsed = [];
        try {
          parsed = JSON.parse(localStorage.getItem(key) || "[]") || [];
        } catch {
          parsed = [];
        }
        const meta = parseLegacyMetaFromKey(key);
        parsed.forEach((record) => {
          if (!record) return;
          legacyRecords.push({
            ...record,
            leadId: record.leadId || meta.leadId || "",
            leadName: record.leadName || meta.leadName || "",
            queueKey: record.queueKey || meta.queueKey || "",
            pageTitle: record.pageTitle || meta.pageTitle || "",
            scopeKey: record.scopeKey || meta.scopeKey || ""
          });
        });
      }

      return mergeRecords(current, legacyRecords);
    }

    function collectForm() {
      const data = {};
      Object.entries(fields).forEach(([key, el]) => { if (el) data[key] = (el.value || "").trim(); });
      Object.entries(selectFields).forEach(([key, el]) => { if (el) data[key] = readSelect(el); });
      Object.entries(textAreas).forEach(([key, el]) => { if (el) data[key] = (el.value || "").trim(); });
      Object.entries(otherFields).forEach(([key, el]) => { if (el) data[key] = (el.value || "").trim(); });
      return data;
    }

    function clearForm() {
      Object.values(fields).forEach((el) => { if (el) el.value = ""; });
      Object.values(selectFields).forEach((el) => clearSelect(el));
      Object.values(textAreas).forEach((el) => { if (el) el.value = ""; });
      Object.values(otherFields).forEach((el) => { if (el) el.value = ""; });
    }

    function populateForm(payload = {}) {
      Object.entries(fields).forEach(([key, el]) => { if (el) el.value = payload?.[key] || ""; });
      Object.entries(selectFields).forEach(([key, el]) => setSelect(el, payload?.[key]));
      Object.entries(textAreas).forEach(([key, el]) => { if (el) el.value = payload?.[key] || ""; });
      Object.entries(otherFields).forEach(([key, el]) => { if (el) el.value = payload?.[key] || ""; });
    }

    function prefillFromLeadBridge() {
      if (fields.date && !fields.date.value) fields.date.value = new Date().toLocaleDateString();
      if (fields.clientName && !fields.clientName.value) fields.clientName.value = readLead("name");
      if (fields.phone && !fields.phone.value) fields.phone.value = readLead("phone");
      if (fields.phone2 && !fields.phone2.value) fields.phone2.value = readLead("phone2");
      if (fields.address && !fields.address.value) fields.address.value = readLead("address");
      if (fields.city && !fields.city.value) fields.city.value = readLead("city");
      if (fields.state && !fields.state.value) fields.state.value = readLead("state");
      if (fields.zip && !fields.zip.value) fields.zip.value = readLead("zip");
      if (fields.county && !fields.county.value) fields.county.value = readLead("county");
      if (fields.dob && !fields.dob.value) fields.dob.value = readLead("dob");
      if (fields.age && !fields.age.value) fields.age.value = readLead("age");
      if (fields.gender && !fields.gender.value) fields.gender.value = readLead("gender");
      if (fields.lender && !fields.lender.value) fields.lender.value = readLead("lender");
      if (fields.loan && !fields.loan.value) fields.loan.value = readLead("loan");
      if (fields.payment && !fields.payment.value) fields.payment.value = readLead("payment");
      if (fields.coverage && !fields.coverage.value) fields.coverage.value = "Mortgage balance protection";
      updateEquityFromInputs();
    }

    function toggleModal(show, opts = {}) {
      const shouldPrefill = opts.prefill !== false;
      if (show) {
        if (shouldPrefill) prefillFromLeadBridge();
        overlay.hidden = false;
        document.body.classList.add("uw-open");
        dialog?.focus();
        return;
      }
      overlay.hidden = true;
      document.body.classList.remove("uw-open");
      editingId = null;
    }

    async function fetchJson(url, options = {}) {
      const getAntiForgeryToken = () =>
        document.querySelector('input[name="__RequestVerificationToken"]')?.value
        || document.querySelector('#__af input[name="__RequestVerificationToken"]')?.value
        || "";

      const method = (options.method || "GET").toUpperCase();
      const headers = {
        "Content-Type": "application/json",
        "X-Requested-With": "XMLHttpRequest",
        ...(options.headers || {})
      };
      if (!["GET", "HEAD", "OPTIONS", "TRACE"].includes(method)) {
        const token = getAntiForgeryToken();
        if (token && !headers.RequestVerificationToken) {
          headers.RequestVerificationToken = token;
        }
      }

      const response = await fetch(url, {
        credentials: "same-origin",
        headers,
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

    function hydrate(dto) {
      if (!dto) return null;
      let payload = {};
      try {
        payload = JSON.parse(dto.payloadJson || dto.PayloadJson || "{}") || {};
      } catch {
        payload = {};
      }

      return {
        id: dto.id || dto.Id || "",
        leadId: (dto.leadId || dto.LeadId || "").trim(),
        leadName: (dto.leadName || dto.LeadName || "").trim(),
        queueKey: (dto.queueKey || dto.QueueKey || "").trim(),
        scopeKey: (dto.scopeKey || dto.ScopeKey || "").trim(),
        pageTitle: (dto.pageTitle || dto.PageTitle || "").trim(),
        productCode: (dto.productCode || dto.ProductCode || "").trim(),
        name: dto.name || dto.Name || "Underwriting",
        payload,
        isDraft: !!(dto.isDraft ?? dto.IsDraft),
        createdUtc: dto.createdUtc || dto.CreatedUtc,
        updatedUtc: dto.updatedUtc || dto.UpdatedUtc
      };
    }

    async function apiList() {
      const data = await fetchJson(`${API_BASE}?includeDrafts=true&productCode=${encodeURIComponent(productCode())}`);
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

    async function migrateLegacyStore() {
      if (localStorage.getItem(MIGRATION_FLAG)) return;
      const records = getLegacyLocalRecords();
      if (!records.length) {
        localStorage.setItem(MIGRATION_FLAG, new Date().toISOString());
        return;
      }

      for (const record of records) {
        const meta = mergeLeadMeta(record, getLeadMeta());
        await apiCreate({
          id: isGuid(record.id) ? record.id : crypto.randomUUID(),
          leadId: meta.leadId,
          leadName: meta.leadName,
          queueKey: meta.queueKey,
          scopeKey: meta.scopeKey,
          pageTitle: meta.pageTitle,
          productCode: productCode(),
          name: (record.name || record.payload?.clientName || "Underwriting").trim() || "Underwriting",
          payloadJson: JSON.stringify(record.payload || {}),
          isDraft: !!record.isDraft
        });
      }

      localStorage.setItem(MIGRATION_FLAG, new Date().toISOString());
    }

    async function ensureServerData() {
      if (!migrationPromise) {
        migrationPromise = (async () => {
          await migrateLegacyStore();
          await apiList();
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
      const currentLeadId = readLead("leadId");
      const store = sortStore(cache);
      if (!store.length) {
        menuList.innerHTML = `<div class="proposal-empty">No underwriting saved</div>`;
        return;
      }
      menuList.innerHTML = store.map((record) => `
        <div class="proposal-item${currentLeadId && record.leadId === currentLeadId ? " is-current" : ""}" data-id="${escapeHtml(record.id)}">
          <div class="proposal-name-wrap" data-uw-open>
            <div class="proposal-name">${escapeHtml(record.name || "Underwriting")}</div>
            <div class="proposal-meta-line">${escapeHtml(`${record.isDraft ? "Draft • " : ""}${record.leadName || record.leadId || record.pageTitle || "Unassigned lead"}`)}</div>
          </div>
          <div class="proposal-actions ellipsis-wrap">
            <button type="button" class="btn-mini ellipsis-btn" aria-label="Actions" data-ellipsis="uw">⋮</button>
            <div class="ellipsis-menu" hidden>
              <button type="button" class="btn-mini" data-uw-edit>Edit</button>
              <button type="button" class="btn-mini danger" data-uw-delete>Delete</button>
            </div>
          </div>
        </div>
      `).join("");
      document.querySelectorAll(".ellipsis-menu").forEach((menuEl) => { menuEl.hidden = true; });
    }

    function openRecord(record = null) {
      const currentDraft = !record ? cache.find((item) => item.isDraft && currentScopeMatches(item)) : null;
      const source = record || currentDraft || null;
      editingId = source?.id || null;
      clearForm();
      if (source?.payload) populateForm(source.payload);
      toggleModal(true, { prefill: !source });
    }

    function buildRequestPayload(data, isDraft) {
      const meta = mergeLeadMeta(cache.find((item) => item.id === editingId) || {}, getLeadMeta());
      return {
        id: editingId || crypto.randomUUID(),
        leadId: meta.leadId,
        leadName: meta.leadName,
        queueKey: meta.queueKey,
        scopeKey: meta.scopeKey,
        pageTitle: meta.pageTitle,
        productCode: productCode(),
        name: (data.clientName || "Underwriting").trim() || "Underwriting",
        payloadJson: JSON.stringify(data || {}),
        isDraft
      };
    }

    async function saveRecord(isDraft) {
      const data = collectForm();
      const payload = buildRequestPayload(data, isDraft);
      const record = editingId
        ? await apiUpdate(editingId, payload)
        : await apiCreate(payload);
      editingId = record?.id || editingId;
      return record;
    }

    function debounce(fn, wait = 400) {
      let timer = null;
      return (...args) => {
        clearTimeout(timer);
        timer = setTimeout(() => fn(...args), wait);
      };
    }

    const autoSave = debounce(async () => {
      try {
        await ensureServerData();
        await saveRecord(true);
      } catch (error) {
        console.error("Underwriting draft autosave failed", error);
      }
    }, 350);
    window.__uwAutoSave = autoSave;

    form.addEventListener("input", autoSave);
    form.addEventListener("change", autoSave);

    form.addEventListener("submit", async (event) => {
      event.preventDefault();
      if (!saveBtn || isSaving) return;
      const original = saveBtn.textContent || "Save";
      saveBtn.disabled = true;
      saveBtn.textContent = "Saving…";
      isSaving = true;

      try {
        await ensureServerData();
        await saveRecord(false);
        await apiList();
        toggleModal(false);
      } catch (error) {
        console.error("Underwriting save failed", error);
        alert("Unable to save underwriting right now. Please try again.");
      } finally {
        isSaving = false;
        saveBtn.disabled = false;
        saveBtn.textContent = original;
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

      if (event.target.matches("[data-uw-delete]")) {
        if (!id) return;
        await apiDelete(id);
        return;
      }

      if (event.target.matches("[data-uw-edit]") || event.target.closest("[data-uw-open]")) {
        if (!record) return;
        await focusRecordLead(record);
        document.querySelectorAll(".ellipsis-menu").forEach((menuEl) => { menuEl.hidden = true; });
        closeMenu();
        openRecord(record);
      }
    });

    function closeMenu() {
      if (menu) menu.hidden = true;
    }

    function toggleMenu() {
      if (menu) {
        renderMenu();
        menu.hidden = !menu.hidden;
      }
    }

    openBtn.addEventListener("click", async (event) => {
      event.preventDefault();
      event.stopPropagation();
      try {
        await ensureServerData();
      } catch (error) {
        console.error("Underwriting list refresh failed", error);
      }
      toggleMenu();
    });

    newBtn?.addEventListener("click", async (event) => {
      event.stopPropagation();
      closeMenu();
      try {
        await ensureServerData();
      } catch (error) {
        console.error("Underwriting refresh failed", error);
      }
      editingId = null;
      clearForm();
      openRecord(null);
    });

    document.addEventListener("click", (event) => {
      if (menu && (menu.contains(event.target) || event.target === openBtn)) return;
      closeMenu();
      document.querySelectorAll(".ellipsis-menu").forEach((menuEl) => { menuEl.hidden = true; });
    });

    overlay.addEventListener("click", () => { /* no-op on backdrop click */ });
    closeEls.forEach((el) => el.addEventListener("click", () => toggleModal(false)));
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

    attachCurrencyMasks();
    enhanceAllMultiSelects();
    renderMenu();

    window.addEventListener("leadbridge:currentLead", () => renderMenu());

    downloadBtn?.addEventListener("click", () => {
      const data = collectForm();
      const pageTitle = (document.querySelector("h1")?.textContent || "Underwriting").trim();
      const fileName = `${(data.clientName || "Client").replace(/\s+/g, " ").trim() || "Client"} - ${pageTitle}`.trim() + ".pdf";
      const html = buildPdfHtml(data, pageTitle, fileName);

      const win = window.open("", "uwpdf", "width=900,height=700");
      if (!win) { alert("Popup blocked. Allow popups to download PDF."); return; }
      win.document.write(html);
      win.document.title = fileName;
      win.document.close();
      win.focus();
      win.print();
      setTimeout(() => { try { win.close(); } catch {} }, 1500);
    });

    ensureServerData().catch((error) => {
      console.error("Underwriting initialization failed", error);
    });
  }
})();

function enhanceAllMultiSelects(){
  const selects = Array.from(document.querySelectorAll("#uwForm select[multiple]"));
  selects.forEach(enhanceMultiSelect);
}

function enhanceMultiSelect(sel){
  if (!sel || sel._enhanced) return;
  sel._enhanced = true;

  const wrap = document.createElement("div");
  wrap.className = "uw-multiselect";
  const btns = [];

  Array.from(sel.options).forEach((opt) => {
    const val = opt.value || "";
    if (!val) return; // skip empty placeholder
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "uw-multi-btn";
    btn.textContent = opt.textContent;
    btn.dataset.value = val;
    if (opt.selected) btn.classList.add("is-selected");
    btn.addEventListener("click", () => {
      const nowSelected = !opt.selected;
      opt.selected = nowSelected;
      btn.classList.toggle("is-selected", nowSelected);
      window.__uwAutoSave && window.__uwAutoSave();
    });
    btns.push(btn);
    wrap.appendChild(btn);
  });

  sel._multiButtons = btns;
  sel.style.display = "none";
  sel.insertAdjacentElement("afterend", wrap);
}

function buildPdfHtml(data, pageTitle, fileName){
  const esc = (v) => (v || "").toString().replace(/&/g,"&amp;").replace(/</g,"&lt;").replace(/>/g,"&gt;");
  const join = (v) => Array.isArray(v) ? v.filter(Boolean).join(", ") : (v || "");
  const logo = `${window.location.origin}/images/company-icons/protect.png`;
  const currentLead = window.__currentLead || {};
  const leadId = esc(currentLead.leadId || currentLead.clientUserId || currentLead.id || "");
  const leadStage = esc(currentLead.bucket || currentLead.Bucket || "");
  const leadOrigin = esc((currentLead.originalLeadType || currentLead.OriginalLeadType || leadStage || "").toString());

  const renderRows = (rows) => rows.map(([k,v]) => `
      <div class="kv">
        <div class="k">${esc(k)}</div>
        <div class="v">${esc(join(v) || "—")}</div>
      </div>
  `).join("");

  const sections = [
    {
      title: "Client + Contact",
      rows: [
        ["Date", data.date],
        ["Client Name", data.clientName],
        ["Phone(s)", [data.phone, data.phone2].filter(Boolean).join(" / ")],
        ["Email", data.email],
        ["Address", [data.address, data.city, data.state, data.zip].filter(Boolean).join(", ")],
        ["County", data.county],
        ["Occupation", data.occupation],
        ["DOB / Age / Gender", [data.dob, data.age, data.gender].filter(Boolean).join(" / ")],
        ["Smoker", join(data.nicotine)],
      ]
    },
    {
      title: "Vitals",
      rows: [
        ["Height / Weight", [data.height, data.weight].filter(Boolean).join(" / ")],
        ["Housing Status", join(data.housingStatus)],
        ["Lender / Loan / Value / Equity", [data.lender, data.loan, data.homeValue, data.equity].filter(Boolean).join(" / ")],
        ["Term", join(data.term)],
        ["Payment", data.payment],
        ["Income / Addl Income", [data.income, data.addlIncome].filter(Boolean).join(" / ")],
        ["Emergency Savings", join(data.savings)],
        ["Emergency Assets", join(data.emergencyAssets)],
        ["Coverage Goal", data.coverage],
        ["Beneficiaries", [data.beneficiary, data.beneficiary2, data.beneficiary3, data.beneficiary4].filter(Boolean).join(" • ")],
      ]
    },
    {
      title: "Health",
      rows: [
        ["Major Surgeries", data.surgeries],
        ["Medications", data.meds],
        ["Medical Conditions", join(data.healthConditions)],
        ["Medical Notes", data.health],
        ["Bankruptcies", data.bankruptcies],
        ["DUIs / Felonies", data.dui],
      ]
    },
    {
      title: "Plan & Affordability",
      rows: [
        ["Existing Coverage", data.lifeCoverage],
        ["Family Plan", data.familyPlan],
        ["Affordability / Budget", data.afford],
        ["Plan Term / Type", join(data.planTerm)],
        ["Notes / Next Steps", data.notes],
      ]
    },
    {
      title: "Payment & IDs",
      rows: [
        ["Bank", data.bank],
        ["Routing #", data.routing],
        ["Account #", data.account],
        ["Driver’s License #", data.dln],
        ["SSN", data.ssn],
      ]
    }
  ];

  const rowsHtml = sections.map(section => `
    <section class="section">
      <header class="section-head">
        <div class="section-title">${esc(section.title)}</div>
      </header>
      <div class="kv-grid">
        ${renderRows(section.rows)}
      </div>
    </section>
  `).join("");

  return `<!doctype html>
  <html>
  <head>
    <meta charset="utf-8" />
    <title>${esc(fileName)}</title>
    <style>
      *{box-sizing:border-box;}
      body{font-family:'Segoe UI', system-ui, -apple-system, sans-serif; margin:0; padding:30px; color:#0f172a; background:#f2f4f7;}
      .sheet{max-width:960px; margin:0 auto; background:#fff; border:1px solid #e2e8f0; border-radius:18px; padding:24px 26px 22px; box-shadow:0 18px 40px rgba(15,23,42,.08);}
      .head{display:flex; align-items:center; gap:14px; margin-bottom:12px;}
      .head img{width:58px; height:58px; object-fit:contain;}
      .title{font-size:22px; font-weight:900; margin:0;}
      .subtitle{color:#475569; font-weight:750; margin:2px 0 0 0;}
      .meta{display:flex; gap:12px; flex-wrap:wrap; margin-bottom:14px;}
      .pill{background:#0f172a; color:#fff; border-radius:10px; padding:6px 10px; font-weight:800; font-size:12px; letter-spacing:.25px;}
      .section{margin-bottom:12px; page-break-inside: avoid; border:1px solid #d9dce3; border-radius:12px; background:#fff; padding:12px 14px;}
      .section-title{font-size:12px; font-weight:900; letter-spacing:.45px; color:#0f172a; text-transform:uppercase; margin:0 0 6px 0;}
      .kv-grid{display:grid; grid-template-columns:repeat(auto-fit,minmax(320px,1fr)); gap:4px 14px;}
      .kv{display:grid; grid-template-columns:32% 68%; border-bottom:1px solid #eceff4; padding:4px 0 5px 0;}
      .kv:last-child{border-bottom:none;}
      .k{font-size:11px; letter-spacing:.3px; font-weight:800; color:#6b7280; text-transform:uppercase;}
      .v{font-size:14px; font-weight:760; color:#0f172a; line-height:1.35; white-space:pre-wrap;}
      @media print{
        body{background:#fff; padding:0;}
        .sheet{box-shadow:none; border:0; margin:0; max-width:100%; padding:14px;}
        .section{border-color:#e5e7eb;}
      }
    </style>
  </head>
  <body>
    <div class="sheet">
      <div class="head">
        <img src="${esc(logo)}" alt="Legend" />
        <div>
          <div class="company">LEGEND LEGACY PROTECTION</div>
          <div class="title">Underwriting Summary</div>
          <div class="subtitle">${esc(pageTitle)} • ${esc(data.clientName || "Client")}</div>
        </div>
      </div>
      <div class="meta">
        <span class="pill">Origin: ${esc(pageTitle)}</span>
        <span class="pill">Prepared: ${new Date().toLocaleDateString()}</span>
        ${leadId ? `<span class="pill">Lead ID: ${leadId}</span>` : ''}
        ${leadStage ? `<span class="pill">Stage: ${leadStage}</span>` : ''}
        ${leadOrigin ? `<span class="pill">Origin Type: ${leadOrigin}</span>` : ''}
      </div>
      ${rowsHtml}
    </div>
  </body>
  </html>`;
}
