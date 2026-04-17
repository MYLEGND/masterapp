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
  const captureDecisionBtn = document.getElementById("btnCaptureDecision");
  const decisionModalEl = document.getElementById("captureDecisionModal");
  const decisionForm = document.getElementById("decisionForm");
  const decisionProposalId = document.getElementById("decisionProposalId");
  let decisionModalInstance = null;
  let activeDecisionModalEl = decisionModalEl;
  const decisionSummary = document.getElementById("latestDecisionSummary");
  const decisionSummaryTitle = document.querySelector("[data-decision-title]");
  const decisionSummaryType = document.querySelector("[data-decision-type]");
  const decisionSummaryRationale = document.querySelector("[data-decision-rationale]");
  const decisionSummaryTime = document.querySelector("[data-decision-time]");

  if (!openBtn || !overlay || !form) return;

  const bucketCount = 3;
  const rowCount = 3;
  let cache = [];
  let editingId = null;
  let draftId = null;
  let editingLeadId = "";
  let editingLeadName = "";
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

  function notify(message, opts = {}) {
    const toastEl = document.getElementById("toast");
    if (toastEl) {
      toastEl.textContent = message || "Done";
      toastEl.classList.toggle("error", !!opts.error);
      toastEl.classList.add("show");
      clearTimeout(window.__toastTimerProposal);
      window.__toastTimerProposal = setTimeout(() => toastEl.classList.remove("show"), opts.persistent ? 4000 : 1800);
    } else {
      if (opts?.error) console.error(message);
      else console.log(message);
      if (!opts?.silent) alert(message);
    }
  }

  const readLead = (key) => {
    const el = document.querySelector(`[data-lf-value="${key}"]`);
    if (!el) return "";
    return (el.textContent || "").replace(/\s+/g, " ").trim();
  };

  const LegendModalApi = window.LegendModal || {};
  const ensureModalInBody = LegendModalApi.ensureInBody?.bind(LegendModalApi) || (() => null);

  function getDecisionModalEl() {
    const moved = ensureModalInBody("captureDecisionModal");
    if (moved) activeDecisionModalEl = moved;
    return activeDecisionModalEl;
  }

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
    return pickInForm(`.hp-bucket[data-bucket="${bucketNumber}"]`) || pickInForm(`.proposal-bucket[data-bucket="${bucketNumber}"]`);
  }

  function getBucketInput(bucketNumber, rowNumber, kind) {
    const bucketEl = getBucketEl(bucketNumber);
    if (!bucketEl) return null;
    if (!rowNumber) {
      if (kind === "term") return bucketEl.querySelector(".prop-term");
      if (kind === "type") return bucketEl.querySelector(".prop-type");
      if (kind === "carrier") return bucketEl.querySelector(".prop-carrier");
      return null;
    }
    const rowEl = bucketEl.querySelectorAll(".hp-row, .pb-row")?.[rowNumber - 1];
    if (!rowEl) return null;
    if (kind === "benefit") return rowEl.querySelector(".prop-benefit");
    if (kind === "premium") return rowEl.querySelector(".prop-premium");
    return null;
  }

  function deepCloneBuckets(sourceBuckets = []) {
    return (sourceBuckets || []).map((bucket) => ({
      type: bucket?.type || "",
      carrier: bucket?.carrier || "",
      term: bucket?.term || "",
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
        term: "",
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
      const bucket = proposal.buckets?.[b - 1] || { type: "", carrier: "", term: "", rows: [] };
      const typeEl = getBucketInput(b, null, "type");
      if (typeEl) typeEl.value = bucket.type || "";
      const carrierEl = getBucketInput(b, null, "carrier");
      if (carrierEl) carrierEl.value = bucket.carrier || "";
      const termEl = getBucketInput(b, null, "term");
      if (termEl) termEl.value = bucket.term || "";
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
      const term = getBucketInput(b, null, "term")?.value?.trim() || "";
      const rows = [];
      for (let r = 1; r <= rowCount; r++) {
        rows.push({
          benefit: getBucketInput(b, r, "benefit")?.value?.trim() || "",
          premium: getBucketInput(b, r, "premium")?.value?.trim() || ""
        });
      }
      buckets.push({ type, carrier, term, rows });
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

  async function apiDecision(id, payload) {
    if (!id) throw new Error("Proposal id required");
    return await fetchJson(`${API_BASE}/${id}/decision`, {
      method: "POST",
      body: JSON.stringify(payload || {})
    });
  }

  function upsertCache(record) {
    if (!record?.id) return;
    const index = cache.findIndex((item) => item.id === record.id);
    if (index >= 0) cache[index] = record;
    else cache.push(record);
    cache = sortStore(cache);
    renderMenu();
  }

  async function loadLatestDecision(proposalId) {
    if (!proposalId || !decisionSummary) return;
    try {
      const data = await fetchJson(`${API_BASE}/${proposalId}/decision/latest`);
      renderDecisionSummary(data);
    } catch (err) {
      // Swallow 404 (no decision yet)
      renderDecisionSummary(null);
    }
  }

  function renderDecisionSummary(decision) {
    if (!decisionSummary) return;
    if (!decision) {
      if (decisionSummaryTitle) decisionSummaryTitle.textContent = "No decision captured yet.";
      if (decisionSummaryType) decisionSummaryType.textContent = "";
      if (decisionSummaryRationale) decisionSummaryRationale.textContent = "Use Capture Decision to log the rationale for this recommendation.";
      if (decisionSummaryTime) decisionSummaryTime.textContent = "";
      decisionSummary.hidden = false;
      return;
    }
    if (decisionSummaryTitle) decisionSummaryTitle.textContent = decision.title || "Decision";
    if (decisionSummaryType) decisionSummaryType.textContent = decision.recommendationType || "";
    if (decisionSummaryRationale) decisionSummaryRationale.textContent = decision.rationale || "";
    if (decisionSummaryTime) {
      const dt = decision.createdUtc ? new Date(decision.createdUtc) : null;
      decisionSummaryTime.textContent = dt ? dt.toLocaleString() : "";
    }
    decisionSummary.hidden = false;
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
      const buildPayload = (id) => {
        const meta = {
          leadId: (record.leadId || "").trim(),
          leadName: (record.leadName || "").trim(),
          queueKey: (record.queueKey || "").trim(),
          scopeKey: (record.scopeKey || "").trim(),
          pageTitle: (record.pageTitle || document.querySelector("h1")?.textContent || "Proposal").trim(),
          leadKey: (record.leadKey || buildLeadKey(record)).trim()
        };
        return {
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
        };
      };

      const preferredId = isGuid(record.id) ? record.id : crypto.randomUUID();
      try {
        await apiCreate(buildPayload(preferredId));
      } catch (error) {
        const message = (error?.message || "").toLowerCase();
        // Retry with a fresh id when a legacy id collides with a different lead.
        if (message.includes("leadid cannot be changed")) {
          try {
            await apiCreate(buildPayload(crypto.randomUUID()));
          } catch (retryError) {
            console.warn("Legacy proposal migration retry failed", retryError);
          }
        } else {
          console.warn("Legacy proposal migration failed for one record", error);
        }
      }
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
      menuList.innerHTML = `<div class="hp-proposal-empty">No proposals saved</div>`;
      return;
    }

    menuList.innerHTML = store.map((record) => `
      <div class="hp-proposal-item${currentLeadId && record.leadId === currentLeadId ? " is-current" : ""}" data-id="${escapeHtml(record.id)}">
        <div class="hp-proposal-name-wrap" data-prop-open>
          <div class="hp-proposal-name">${escapeHtml(record.name || "Proposal")}</div>
          ${(record.leadName || record.leadId) && (record.leadName || "").trim().toLowerCase() !== (record.name || "").trim().toLowerCase()
            ? `<div class="hp-proposal-meta-line">${escapeHtml(record.leadName || record.leadId || "Unassigned lead")}</div>`
            : ""}
        </div>
        <div class="hp-proposal-actions hp-ellipsis-wrap">
          <button type="button" class="btn-mini hp-ellipsis-btn" aria-label="Actions" data-ellipsis="prop">⋮</button>
          <div class="hp-ellipsis-menu" hidden>
            <button type="button" class="btn-mini" data-prop-edit>Edit</button>
            <button type="button" class="btn-mini danger" data-prop-delete>Delete</button>
          </div>
        </div>
      </div>
    `).join("");

    document.querySelectorAll(".hp-ellipsis-menu, .ellipsis-menu").forEach((dropdown) => { dropdown.hidden = true; });
  }

  function openModal(record = null) {
    const meta = getLeadMeta();
    const currentDraft = !record ? cache.find((item) => item.isDraft && sameScope(item, meta)) : null;
    const source = record || currentDraft || null;
    const defaultName = meta.leadName ? `${meta.leadName}` : "";

    draftId = source?.isDraft ? source.id : null;
    editingId = source?.id || null;
    editingLeadId = (source?.leadId || "").trim();
    editingLeadName = (source?.leadName || "").trim();

    if (!source) resetForm(buildBlankState(defaultName), true);
    else resetForm({ ...source, buckets: deepCloneBuckets(source.buckets || []) }, true);

    overlay.hidden = false;
    document.body.classList.add("uw-open");
    dialog?.focus();
    if (editingId || source?.id) {
      loadLatestDecision(editingId || source?.id);
    } else {
      renderDecisionSummary(null);
    }
  }

  function closeModal() {
    overlay.hidden = true;
    document.body.classList.remove("uw-open");
    editingId = null;
    draftId = null;
    editingLeadId = "";
    editingLeadName = "";
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
    const effectiveLeadId = (editingLeadId || meta.leadId || "").trim();
    if (!effectiveLeadId) return null;
    const effectiveLeadName = (editingLeadName || meta.leadName || "").trim();

    const payload = collectFormPayload();
    const recordPayload = {
      id: editingId || draftId || crypto.randomUUID(),
      leadId: effectiveLeadId,
      leadName: effectiveLeadName,
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
    editingLeadId = (record?.leadId || editingLeadId || "").trim();
    editingLeadName = (record?.leadName || editingLeadName || "").trim();
    if (editingId) loadLatestDecision(editingId);
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
    if (!(editingLeadId || meta.leadId || "").trim()) {
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
      const detail = (error?.message || "").trim();
      notify(detail ? `Unable to save proposal right now: ${detail}` : "Unable to save proposal right now. Please try again.", { error: true, persistent: true });
    } finally {
      isSaving = false;
      if (saveBtn) saveBtn.disabled = false;
    }
  });

  async function openDecisionModal() {
    try {
      await ensureServerData();
      if (!editingId) {
        const saved = await saveRecord(false);
        editingId = saved?.id || editingId;
      }
      if (!editingId) {
        alert("Save the proposal first, then capture the decision.");
        return;
      }
      if (decisionProposalId) decisionProposalId.value = editingId;
      if (decisionForm) {
        decisionForm.reset();
      }
      if (window.LegendModal?.closeLegacyExecutionOverlays) {
        window.LegendModal.closeLegacyExecutionOverlays();
      }
      const modalEl = getDecisionModalEl();
      if (modalEl && window.bootstrap) {
        decisionModalInstance = bootstrap.Modal.getOrCreateInstance(modalEl);
        decisionModalInstance.show();
      } else if (modalEl) {
        modalEl.hidden = false;
      }
    } catch (error) {
      console.error("Unable to open decision modal", error);
      alert("Unable to open decision capture right now. Try saving the proposal first.");
    }
  }

  captureDecisionBtn?.addEventListener("click", (event) => {
    event.preventDefault();
    openDecisionModal();
  });

  decisionForm?.addEventListener("submit", async (event) => {
    event.preventDefault();
    const proposalId = (decisionProposalId?.value || editingId || "").trim();
    if (!proposalId) {
      alert("Save the proposal first, then capture the decision.");
      return;
    }
    const formData = new FormData(decisionForm);
    const payload = {
      title: (formData.get("title") || "").toString().trim() || "Proposal Decision",
      rationale: (formData.get("rationale") || "").toString().trim(),
      recommendationType: (formData.get("recommendationType") || "ProposalRecommendation").toString()
    };
    try {
      await apiDecision(proposalId, payload);
      if (decisionModalInstance) decisionModalInstance.hide();
      else if (activeDecisionModalEl) activeDecisionModalEl.hidden = true;
      loadLatestDecision(proposalId);
      notify("Decision saved.");
    } catch (error) {
      console.error("Decision save failed", error);
      notify("Unable to save decision right now.", { error: true, persistent: true });
    }
  });

  menuList?.addEventListener("click", async (event) => {
    const item = event.target.closest(".hp-proposal-item, .proposal-item");
    if (!item) return;
    const id = item.getAttribute("data-id");
    const record = cache.find((entry) => entry.id === id);

    if (event.target.matches(".hp-ellipsis-btn, .ellipsis-btn")) {
      event.stopPropagation();
      const wrap = event.target.closest(".hp-ellipsis-wrap, .ellipsis-wrap");
      const dropdown = wrap?.querySelector(".hp-ellipsis-menu, .ellipsis-menu");
      if (dropdown) dropdown.hidden = !dropdown.hidden;
      document.querySelectorAll(".hp-ellipsis-menu, .ellipsis-menu").forEach((menuEl) => { if (menuEl !== dropdown) menuEl.hidden = true; });
      return;
    }

    if (event.target.matches("[data-prop-delete]")) {
      if (!id) return;
      await apiDelete(id);
      return;
    }

    if (!record) return;
    await focusRecordLead(record);
    document.querySelectorAll(".hp-ellipsis-menu, .ellipsis-menu").forEach((menuEl) => { menuEl.hidden = true; });
    closeMenu();
    openModal(record);
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
    const isMobile = !!window.matchMedia?.("(max-width: 900px)")?.matches;
    if (isMobile) {
      closeMenu();
      openModal(null);
      return;
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
    document.querySelectorAll(".hp-ellipsis-menu, .ellipsis-menu").forEach((menuEl) => { menuEl.hidden = true; });
  });

  overlay.addEventListener("click", (event) => {
    if (event.target !== overlay) return;
    closeModal();
  });
  closeEls.forEach((el) => el.addEventListener("click", () => closeModal()));
  document.addEventListener("keydown", (event) => {
    if (event.key !== "Escape") return;
    const decisionModalOpen = !!document.querySelector("#captureDecisionModal.show");
    if (!decisionModalOpen && !overlay.hidden) {
      closeModal();
      return;
    }
    closeMenu();
    document.querySelectorAll(".hp-ellipsis-menu, .ellipsis-menu").forEach((menuEl) => { menuEl.hidden = true; });
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
