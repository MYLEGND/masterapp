/* ==========================================================
   LEGEND CLIENTS — OPTIMIZED + COLOR-CODED + NO WASTED WORK
   ==========================================================
   (JS LEFT AS-IS; layout restructured via wrappers only.)
*/

/* ========= UTIL ========= */
const LS_COLS  = "legend_crm_cols_v1";
const LS_PREFS = "legend_crm_prefs_v2";
const LS_NOTIF = "legend_crm_notif_v1";
const LS_ZOOM  = "legend_agent_zoom_v1";
const LS_VIEWS = "legend_saved_views_v1";
const LS_PIPELINE_ORDER = "legend_pipeline_order_v1";
const LS_PROD_DRAFT_LEAD = "legend_prod_draft_lead_v1";
const LS_DIAL_BASE = "legend_dial_baseline_v1";
const liveSync = window.liveSync;
const REORDER_URL = "/Leads/Reorder";
const LEADS_ONLY = true; // guard against creating client/portal records from the Leads CRM
const __timers = { dialFresh: null, reminders: null };
const CAL_STATUS_TTL_MS = 30 * 1000;
let _calendarStatusCache = null;
let _calendarStatusCacheAt = 0;

const $  = (sel, root=document) => root.querySelector(sel);
const $$ = (sel, root=document) => Array.from(root.querySelectorAll(sel));

// Dial tracking timezone headers (keep server windows aligned to the agent's local clock)
const agentTimeZone = Intl.DateTimeFormat().resolvedOptions().timeZone || "";
const agentTzOffset = String(new Date().getTimezoneOffset());
function withDialHeaders(init = {}){
  const headers = new Headers(init.headers || {});
  if (agentTimeZone) headers.set("X-Agent-TimeZone", agentTimeZone);
  headers.set("X-Agent-TzOffset", agentTzOffset);
  return { ...init, headers };
}

function loadJSON(key, fallback){
  try { return JSON.parse(localStorage.getItem(key) || "") ?? fallback; }
  catch { return fallback; }
}
function saveJSON(key, obj){ localStorage.setItem(key, JSON.stringify(obj ?? {})); }
function deleteKey(key){ try { localStorage.removeItem(key); } catch{} }

function todayISO(){
  const d = new Date();
  const tz = new Date(d.getTime() - d.getTimezoneOffset() * 60000);
  return tz.toISOString().slice(0,10);
}
function formatDob(value){
  if (!value) return "—";

  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return "—";

  const mm = String(d.getMonth() + 1).padStart(2, '0');
  const dd = String(d.getDate()).padStart(2, '0');
  const yyyy = d.getFullYear();

  return `${mm}-${dd}-${yyyy}`;
}

const US_STATE_NAME_BY_CODE = Object.freeze({
  AL: 'ALABAMA',
  AK: 'ALASKA',
  AZ: 'ARIZONA',
  AR: 'ARKANSAS',
  CA: 'CALIFORNIA',
  CO: 'COLORADO',
  CT: 'CONNECTICUT',
  DE: 'DELAWARE',
  DC: 'DISTRICT OF COLUMBIA',
  FL: 'FLORIDA',
  GA: 'GEORGIA',
  HI: 'HAWAII',
  ID: 'IDAHO',
  IL: 'ILLINOIS',
  IN: 'INDIANA',
  IA: 'IOWA',
  KS: 'KANSAS',
  KY: 'KENTUCKY',
  LA: 'LOUISIANA',
  ME: 'MAINE',
  MD: 'MARYLAND',
  MA: 'MASSACHUSETTS',
  MI: 'MICHIGAN',
  MN: 'MINNESOTA',
  MS: 'MISSISSIPPI',
  MO: 'MISSOURI',
  MT: 'MONTANA',
  NE: 'NEBRASKA',
  NV: 'NEVADA',
  NH: 'NEW HAMPSHIRE',
  NJ: 'NEW JERSEY',
  NM: 'NEW MEXICO',
  NY: 'NEW YORK',
  NC: 'NORTH CAROLINA',
  ND: 'NORTH DAKOTA',
  OH: 'OHIO',
  OK: 'OKLAHOMA',
  OR: 'OREGON',
  PA: 'PENNSYLVANIA',
  RI: 'RHODE ISLAND',
  SC: 'SOUTH CAROLINA',
  SD: 'SOUTH DAKOTA',
  TN: 'TENNESSEE',
  TX: 'TEXAS',
  UT: 'UTAH',
  VT: 'VERMONT',
  VA: 'VIRGINIA',
  WA: 'WASHINGTON',
  WV: 'WEST VIRGINIA',
  WI: 'WISCONSIN',
  WY: 'WYOMING',
  PR: 'PUERTO RICO',
  GU: 'GUAM',
  VI: 'U.S. VIRGIN ISLANDS',
  AS: 'AMERICAN SAMOA',
  MP: 'NORTHERN MARIANA ISLANDS'
});
const US_STATE_CANONICAL_BY_KEY = Object.freeze(
  Object.values(US_STATE_NAME_BY_CODE).reduce((acc, name) => {
    acc[name.replace(/[^A-Z]/g, '')] = name;
    return acc;
  }, {})
);
function normalizeStateOption(value){
  const raw = (value ?? '').toString().trim();
  if (!raw) return '';

  const upper = raw
    .toUpperCase()
    .replace(/\./g, '')
    .replace(/\s+/g, ' ')
    .trim();

  if (US_STATE_NAME_BY_CODE[upper]) {
    return US_STATE_NAME_BY_CODE[upper];
  }

  const tokens = upper.split(/[^A-Z]/).filter(Boolean);
  for (const token of tokens){
    if (token.length === 2 && US_STATE_NAME_BY_CODE[token]) {
      return US_STATE_NAME_BY_CODE[token];
    }
  }

  const key = upper.replace(/[^A-Z]/g, '');
  if (US_STATE_CANONICAL_BY_KEY[key]) {
    return US_STATE_CANONICAL_BY_KEY[key];
  }

  return upper;
}

/* ===== Note to Self helpers ===== */
function noteTodayISO(){
  const d = new Date();
  const tz = new Date(d.getTime() - d.getTimezoneOffset() * 60000);
  return tz.toISOString().slice(0,10);
}

function noteDisplayDate(iso){
  if (!iso) return "00-00-0000";
  const m = String(iso).match(/^(\d{4})-(\d{2})-(\d{2})$/);
  return m ? `${m[2]}-${m[3]}-${m[1]}` : "00-00-0000";
}

function noteEncodeKey(leadId, noteDate){
  return `${encodeURIComponent(leadId || "")}|${noteDate || ""}`;
}

function noteDecodeKey(raw){
  const value = (raw || "").trim();
  if (!value) return { leadId: "", noteDate: "" };
  const sep = value.indexOf("|");
  if (sep < 0) return { leadId: "", noteDate: value };
  return { leadId: decodeURIComponent(value.slice(0, sep)), noteDate: value.slice(sep + 1) };
}

function showQuickViewTab(target, opts = {}){
  if (!target) return;
  const drawerRoot = $("#drawer") || document;
  const panels = $$('.qv-tabpanel', drawerRoot);
  if (!panels.length) return;

  const targetPanel = panels.find(p => p.id === target) || document.getElementById(target);
  if (!targetPanel) return;

  const disclosure = targetPanel.closest('details.qv-disclosure');
  if (disclosure) disclosure.open = true;

  panels.forEach(panel => {
    panel.style.display = panel.id === target ? '' : 'none';
  });

  const tabButtons = $$('.qv-tabs button[data-tab-target]', drawerRoot);
  tabButtons.forEach(tab => {
    const isActive = tab.dataset.tabTarget === target;
    tab.classList.toggle('btn-gold', isActive);
    tab.classList.toggle('btn-ghost', !isActive);
  });

  if (opts.scroll){
    targetPanel.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }
}

// quick view tab switcher for notes/actions, including top-level shortcuts
function bindQuickViewTabs(){
  const drawerRoot = $("#drawer") || document;
  const launchers = $$('[data-tab-target]', drawerRoot);
  if (!launchers.length) return;

  launchers.forEach(btn => {
    if (btn.dataset.tabBound === "1") return;
    btn.dataset.tabBound = "1";
    btn.addEventListener('click', () => {
      showQuickViewTab(btn.dataset.tabTarget, { scroll: btn.dataset.tabScroll === "1" });
    });
  });
}

const LegendModalApi = window.LegendModal || {};
const ensureModalInBody = LegendModalApi.ensureInBody?.bind(LegendModalApi) || (() => null);
const bindBootstrapModalStability = LegendModalApi.bind?.bind(LegendModalApi) || (() => null);
const hideBootstrapModalById = LegendModalApi.hide?.bind(LegendModalApi) || (() => null);
const closeLegacyOverlayModals = LegendModalApi.closeLegacyExecutionOverlays?.bind(LegendModalApi) || (() => {});
const reconcileBootstrapModalState = LegendModalApi.reconcile?.bind(LegendModalApi) || (() => {});

function bindQuickViewBootstrapModals(){
  bindBootstrapModalStability("leadActionsHubModal", {
    modalZ: 1060,
    backdropZ: 1055,
    onHidden: () => {
      hideBootstrapModalById("quickCreateActionModal");
      hideBootstrapModalById("addCommitmentModal");
    }
  });

  bindBootstrapModalStability("quickCreateActionModal", { modalZ: 1085, backdropZ: 1080 });
  bindBootstrapModalStability("addCommitmentModal", { modalZ: 1085, backdropZ: 1080 });
}

function norm(v){ return (v || "").toString().trim(); }
function fullName(row){ return (norm(row.dataset.first) + " " + norm(row.dataset.last)).trim(); }

function wireActionForm(){
  const form = document.getElementById('quickCreateActionForm');
  if (!form || form.dataset.bound === "1") return;
  form.dataset.bound = "1";
  const modalEl = ensureModalInBody('quickCreateActionModal') || document.getElementById('quickCreateActionModal');
  const actionsContainer = document.querySelector('#leadActionsContainer');
  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    if (!actionsContainer) return;
    const data = new FormData(form);
    const showInDashboardInput = form.querySelector('input[name="ShowInCommandCenter"]');
    data.set("ShowInCommandCenter", showInDashboardInput?.checked ? "true" : "false");
    const dueInput = form.querySelector('input[name="DueDateUtc"]');
    if (dueInput && dueInput.value){
      const local = new Date(dueInput.value);
      if (!isNaN(local.getTime())){
        data.set("DueDateUtc", local.toISOString());
      }
    }
    actionsContainer.innerHTML = '<div class="text-muted">Saving...</div>';
    try{
      const res = await fetch("/Leads/CreateAction", withDialHeaders({
        method: "POST",
        headers: { "RequestVerificationToken": getAntiForgeryToken() },
        body: data,
        credentials: "include"
      }));
      const html = await res.text();
      if (!res.ok) throw new Error(html || "Save failed");
      if (window.bootstrap && modalEl){
        const inst = bootstrap.Modal.getInstance(modalEl) || new bootstrap.Modal(modalEl);
        inst.hide();
        inst.dispose();
      }
      disposeModalById('quickCreateActionModal');
      actionsContainer.innerHTML = html;
      wireActionForm(); // rebind to new DOM
      toast("Action added.");
    }catch(err){
      console.error(err);
      const detail = (err?.message || "").toString().trim();
      actionsContainer.innerHTML = `<div class="text-danger">${escapeHtml(detail || "Failed to save action.")}</div>`;
      toast(detail || "Failed to save action", { error: true, persistent: true });
    }
  });
}

function disposeModalById(modalId){
  const existing = document.getElementById(modalId);
  if (!existing) return;
  try{
    if (window.bootstrap){
      const inst = bootstrap.Modal.getInstance(existing);
      inst?.dispose();
    }
  }catch{}
  existing.remove();
}

function wireLeadActionListControls(){
  const actionsContainer = document.querySelector('#leadActionsContainer');
  if (!actionsContainer || actionsContainer.dataset.controlsBound === "1") return;
  actionsContainer.dataset.controlsBound = "1";

  actionsContainer.addEventListener('click', async (event) => {
    const btn = event.target.closest('.action-complete-btn');
    if (!btn) return;
    event.preventDefault();

    const actionId = btn.getAttribute('data-action-id');
    if (!actionId) return;
    btn.closest('.action-kebab')?.removeAttribute('open');

    btn.disabled = true;
    try{
      const res = await fetch("/Dashboard/CompleteAction", withDialHeaders({
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "RequestVerificationToken": getAntiForgeryToken(actionsContainer)
        },
        credentials: "include",
        body: JSON.stringify({ id: actionId })
      }));
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      await loadLeadActionsPanel();
      toast("Action completed.");
    }catch(err){
      console.error(err);
      btn.disabled = false;
      toast("Failed to complete action.", { error: true, persistent: true });
    }
  });

  actionsContainer.addEventListener('submit', async (event) => {
    const form = event.target.closest('.action-delete-form');
    if (!form) return;
    event.preventDefault();
    form.closest('.action-kebab')?.removeAttribute('open');

    try{
      const data = new FormData(form);
      const res = await fetch(form.getAttribute('action') || "", withDialHeaders({
        method: "POST",
        headers: { "RequestVerificationToken": getAntiForgeryToken() },
        body: data,
        credentials: "include"
      }));
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      await loadLeadActionsPanel();
      toast("Action deleted.");
    }catch(err){
      console.error(err);
      toast("Failed to delete action.", { error: true, persistent: true });
    }
  });
}

function loadLeadActionsPanel(){
  const actionsContainer = document.querySelector('#leadActionsContainer');
  const requestedClientId = (activeClientId || drawer?.dataset?.clientId || "").toString().trim();
  if (!actionsContainer || !requestedClientId) return Promise.resolve(false);
  if (!activeClientId) activeClientId = requestedClientId;
  wireLeadActionListControls();
  if (leadActionsLoadPromise) return leadActionsLoadPromise;

  disposeModalById('quickCreateActionModal');
  actionsContainer.innerHTML = '<div class="text-muted">Loading actions...</div>';
  leadActionsLoadPromise = fetch(`/Leads/Actions?id=${encodeURIComponent(requestedClientId)}`, withDialHeaders())
    .then(async (r) => {
      const text = await r.text();
      if (!r.ok) throw new Error(text || `Failed to load actions (HTTP ${r.status})`);
      return text;
    })
    .then(html => {
      if (activeClientId !== requestedClientId) return false;
      actionsContainer.innerHTML = html;
      wireActionForm();
      return true;
    })
    .catch((err) => {
      if (activeClientId === requestedClientId){
        actionsContainer.innerHTML = `<div class="text-danger">${escapeHtml(err?.message || "Failed to load actions")}</div>`;
      }
      return false;
    })
    .finally(() => {
      leadActionsLoadPromise = null;
    });

  return leadActionsLoadPromise;
}

function loadLeadCommitmentsPanel(){
  const commitmentsContainer = document.querySelector('#leadCommitmentsContainer');
  const requestedClientId = (activeClientId || drawer?.dataset?.clientId || "").toString().trim();
  if (!commitmentsContainer || !requestedClientId) return Promise.resolve(false);
  if (!activeClientId) activeClientId = requestedClientId;

  commitmentsContainer.innerHTML = '<div class="text-muted">Loading commitments...</div>';
  return fetch(`/Leads/Commitments?id=${encodeURIComponent(requestedClientId)}`, withDialHeaders())
    .then(async (r) => {
      const text = await r.text();
      if (!r.ok) throw new Error(text || `Failed to load commitments (HTTP ${r.status})`);
      return text;
    })
    .then(html => {
      if (activeClientId !== requestedClientId) return false;
      commitmentsContainer.innerHTML = html;
      wireCommitmentForm();
      wireCommitmentActions();
      return true;
    })
    .catch((err) => {
      if (activeClientId === requestedClientId){
        commitmentsContainer.innerHTML = `<div class="text-danger">${escapeHtml(err?.message || "Failed to load commitments")}</div>`;
      }
      return false;
    });
}

function wireCommitmentForm(){
  const form = document.querySelector('#leadCommitmentsContainer #createCommitmentForm');
  if (!form || form.dataset.bound === "1") return;
  form.dataset.bound = "1";
  const modalEl = ensureModalInBody('addCommitmentModal') || document.getElementById('addCommitmentModal');
  const errorBoxId = 'commitmentFormError';

  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    const container = document.querySelector('#leadCommitmentsContainer');
    if (!container) return;

    const data = new FormData(form);
    const promise = (data.get("PromiseText") || "").toString().trim();
    const dueRaw = (data.get("DueDateUtc") || "").toString().trim();
    const errEl = document.getElementById(errorBoxId);
    if (errEl) errEl.remove();
    if (!promise){
      form.insertAdjacentHTML("afterbegin", `<div id="${errorBoxId}" class="text-danger small" style="margin-bottom:6px;">Promise is required.</div>`);
      return;
    }
    if (!dueRaw){
      form.insertAdjacentHTML("afterbegin", `<div id="${errorBoxId}" class="text-danger small" style="margin-bottom:6px;">Due date is required.</div>`);
      return;
    }
    const dueInput = form.querySelector('input[name="DueDateUtc"]');
    if (dueInput && dueInput.value) {
      const local = new Date(dueInput.value);
      if (!isNaN(local.getTime())) {
        data.set("DueDateUtc", local.toISOString());
      }
    }

    container.innerHTML = '<div class="text-muted">Saving...</div>';
    try{
      const res = await fetch(form.getAttribute('action') || "/Leads/CreateCommitment", withDialHeaders({
        method: "POST",
        headers: { "RequestVerificationToken": getAntiForgeryToken() },
        body: data,
        credentials: "include"
      }));
      const text = await res.text();
      if (!res.ok) throw new Error(text || "Save failed");
      if (window.bootstrap && modalEl){
        const inst = bootstrap.Modal.getInstance(modalEl) || new bootstrap.Modal(modalEl);
        inst.hide();
      }
      await loadLeadCommitmentsPanel();
      await loadLeadActionsPanel();
      toast("Commitment added.");
    }catch(err){
      console.error(err);
      container.innerHTML = '<div class="text-danger">Failed to save commitment.</div>';
      toast(err?.message || "Failed to save commitment", { error: true, persistent: true });
    }
  });
}

function wireCommitmentActions(){
  const buttons = document.querySelectorAll('#leadCommitmentsContainer [data-commitment-action]');
  buttons.forEach(btn => {
    if (btn.dataset.bound === "1") return;
    btn.dataset.bound = "1";
    btn.addEventListener('click', async (event) => {
      event.preventDefault();
      const action = btn.dataset.commitmentAction;
      const id = btn.dataset.commitmentId;
      if (!action || !id) return;
      const container = document.querySelector('#leadCommitmentsContainer');
      if (container) container.innerHTML = '<div class="text-muted">Updating...</div>';

      const url = action === "fulfill"
        ? `/Leads/FulfillCommitment?id=${encodeURIComponent(id)}`
        : `/Leads/BreakCommitment?id=${encodeURIComponent(id)}`;
      try{
        const res = await fetch(url, withDialHeaders({
          method: "POST",
          headers: { "RequestVerificationToken": getAntiForgeryToken() },
          credentials: "include"
        }));
        const html = await res.text();
        if (!res.ok) throw new Error(html || "Update failed");
        if (container) container.innerHTML = html;
        wireCommitmentForm();
        wireCommitmentActions();
        toast(action === "fulfill" ? "Commitment fulfilled." : "Commitment broken.");
      }catch(err){
        console.error(err);
        if (container) container.innerHTML = '<div class="text-danger">Failed to update commitment.</div>';
        toast("Failed to update commitment", { error: true, persistent: true });
      }
    }, { once: true });
  });
}

function escapeHtml(value){
  return (value ?? "").toString()
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

function isPlaceholderEmail(value){
  const email = norm(value).toLowerCase();
  if (!email) return false;

  return (/^no-email@.+\.com$/i).test(email)
    || (/^lead-.+@scripts\.local$/i).test(email)
    || email.endsWith("@leads.local");
}

function applyFakeEmailState(el, email){
  if (!el) return;
  el.classList.toggle("crm-email-fake", isPlaceholderEmail(email));
}

function syncRowEmailDisplays(row){
  if (!row) return;

  const email = norm(row.dataset.email);
  const summaryEl = $("[data-email-summary]", row);
  const cellEl = $("[data-email-cell]", row);

  if (summaryEl){
    summaryEl.textContent = email;
    applyFakeEmailState(summaryEl, email);
  }

  if (cellEl){
    cellEl.textContent = email;
    applyFakeEmailState(cellEl, email);
  }
}

function syncDrawerEmailDisplay(email){
  if (!dEmail) return;
  dEmail.textContent = norm(email) || "No email";
  applyFakeEmailState(dEmail, email);
}

function renderEmailLinkHtml(email, styleText = ""){
  const value = norm(email);
  if (!value) return "No email";

  const fakeClass = isPlaceholderEmail(value) ? " crm-email-fake" : "";
  const styleAttr = styleText ? ` style=\"${escapeHtml(styleText)}\"` : "";
  return `<a class=\"link link-email${fakeClass}\"${styleAttr} href=\"mailto:${escapeHtml(value)}\">${escapeHtml(value)}</a>`;
}

function hideToast(){
  const t = $("#toast");
  if (!t) return;
  clearTimeout(window.__toastTimer);
  t.classList.remove("show", "persistent", "error");
  t.innerHTML = "";
}

function toast(msg, opts = {}){
  const t = $("#toast");
  if (!t) return;
  clearTimeout(window.__toastTimer);
  const text = msg || "Done";
  const persistent = !!opts.persistent || !!opts.error; // errors stay until dismissed
  const isError = !!opts.error;

  t.classList.toggle("persistent", persistent);
  t.classList.toggle("error", isError);
  t.innerHTML = `
    <div class="toast-body">${escapeHtml(text)}</div>
    ${persistent ? '<button type="button" class="toast-close" aria-label="Close">×</button>' : ""}
  `;
  t.classList.add("show");

  const closeBtn = $(".toast-close", t);
  closeBtn?.addEventListener("click", hideToast, { once: true });

  if (!persistent){
    window.__toastTimer = setTimeout(hideToast, 1400);
  }
}

function getAntiForgeryToken(scope){
  const scoped = scope?.querySelector?.('input[name="__RequestVerificationToken"]');
  if (scoped?.value) return scoped.value;

  const primary = document.querySelector('#__af input[name="__RequestVerificationToken"]');
  if (primary?.value) return primary.value;

  // Standalone partial pages (e.g. /Leads/Actions/{id}) do not render #__af.
  const any = document.querySelector('input[name="__RequestVerificationToken"]');
  return any?.value || "";
}

async function postJson(url, payload){
  const token = getAntiForgeryToken();
  const res = await fetch(url, withDialHeaders({
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "RequestVerificationToken": token
    },
    credentials: "include",
    body: JSON.stringify(payload)
  }));

  const raw = await res.text().catch(() => "");

  if (!res.ok){
    // Surface server message (even if HTML) to avoid "Unexpected token" noise.
    throw new Error(raw || `Request failed: ${res.status}`);
  }

  try {
    return raw ? JSON.parse(raw) : {};
  } catch (err){
    throw new Error(raw || err.message || "Invalid JSON response");
  }
}

async function loadQuickView(clientId){
  // Prefer DOM row; fall back to server fetch so Quick View always works
  const row = rows.find(r => r.dataset.clientId === clientId);
  if (row){
    let lead = null;
    try{
      const leadRes = await fetch(`/Leads/Lead?id=${encodeURIComponent(clientId)}`, withDialHeaders());
      if (leadRes.ok) lead = await leadRes.json();
    }catch{}

    const preferNumber = (...vals) => {
      for (const v of vals){
        const num = Number(v);
        if (Number.isFinite(num)) return num;
      }
      return 0;
    };

    // Canonical tracker fields: prefer freshly rolled API values; fall back to row dataset.
    const attemptsToday = preferNumber(lead?.attemptsToday, lead?.dialsToday, row.dataset.crmAttemptsToday, row.dataset.sAttemptstoday);
    const attemptsWeek = preferNumber(lead?.attemptsThisWeek, lead?.dialsWeek, row.dataset.crmAttemptsWeek, row.dataset.sAttemptsweek);
    const attemptsMonth = preferNumber(lead?.attemptsThisMonth, row.dataset.crmAttemptsMonth, row.dataset.sAttemptsmonth);
    const attemptsYear = preferNumber(lead?.attemptsThisYear, row.dataset.crmAttemptsYear, row.dataset.sAttemptsyear);
    const attemptsLife = preferNumber(lead?.attemptsLifetime, lead?.callCount, row.dataset.crmAttemptsLife, row.dataset.sAttemptslife);
    const stageEntered = lead?.stageEnteredUtc || row.dataset.crmStageEntered || row.dataset.sStageentered || todayISO();
    const createdUtc = lead?.createdUtc || row.dataset.sCreated || row.dataset.crmCreated || stageEntered;
    const stageAgeDaysFresh = lead?.stageAgeDays ?? stageAgeDays(row);

    // Keep list row in sync to avoid stale numbers and double counting.
    row.dataset.crmAttemptsToday = String(attemptsToday);
    row.dataset.sAttemptstoday = String(attemptsToday);
    row.dataset.crmAttemptsWeek = String(attemptsWeek);
    row.dataset.sAttemptsweek = String(attemptsWeek);
    row.dataset.crmAttemptsMonth = String(attemptsMonth);
    row.dataset.sAttemptsmonth = String(attemptsMonth);
    row.dataset.crmAttemptsYear = String(attemptsYear);
    row.dataset.sAttemptsyear = String(attemptsYear);
    row.dataset.crmAttemptsLife = String(attemptsLife);
    row.dataset.sAttemptslife = String(attemptsLife);
    row.dataset.crmStageEntered = stageEntered;
    row.dataset.sStageentered = stageEntered;

    const dobIso = lead?.dob ? lead.dob.slice(0,10) : (row.dataset.dob || "");
    return {
      clientUserId: clientId,
      firstName: lead?.firstName || row.dataset.first || "",
      lastName: lead?.lastName || row.dataset.last || "",
      email: lead?.email || norm(row.dataset.email),
      phone: lead?.phone || norm(row.dataset.phone),
      phone2: lead?.phone2 || row.dataset.phone2 || "",
      dob: dobIso,
      dobFormatted: lead?.dobFormatted || formatDob(dobIso),
      age: lead?.age || row.dataset.age || "",
      gender: lead?.gender || row.dataset.gender || "",
      addressLine: lead?.addressLine || row.dataset.addressLine || "",
      city: lead?.city || row.dataset.city || "",
      state: lead?.state || row.dataset.state || "",
      county: lead?.county || row.dataset.county || "",
      zipCode: lead?.zipCode || lead?.zip || row.dataset.zipCode || "",
      mortgageLender: lead?.mortgageLender || row.dataset.mortgageLender || "",
      loanAmount: lead?.loanAmount || row.dataset.loanAmount || "",
      btc: lead?.btc || row.dataset.btc || "",
      crmStatus: row.dataset.crmStatus || row.dataset.sStatus || "Lead",
      crmPriority: row.dataset.crmPriority || row.dataset.sPriority || "Normal",
      crmLastTouch: row.dataset.crmLastTouch || row.dataset.sLasttouch || "",
      crmNextDate: row.dataset.crmNextDate || row.dataset.sNextdate || "",
      crmNextText: row.dataset.crmNextText || row.dataset.sNexttext || "",
      crmTags: row.dataset.crmTags || row.dataset.sTags || "",
      agentNotes: row.dataset.crmNotes || row.dataset.sNotes || "",
      pipelineStage: row.dataset.crmPipeline || row.dataset.sPipeline || "MortgageProtection",
      originalLeadType: rowOriginalLeadType(row),
      meetingLocation: row.dataset.sMeetingLocation || "",
      zoomJoinUrl: row.dataset.sZoom || "",
      usePersonalZoomLink: (row.dataset.sUsezoom || "false") === "true",
      meetingTime: row.dataset.sMeetingTime || "09:00",
      meetingDurationMinutes: parseInt(row.dataset.sMeetingDuration || "30", 10) || 30,
      waitingOn: row.dataset.crmWaitingOn || row.dataset.sWaiting || "WaitingOnAgent",
      pinnedBrief: row.dataset.crmPinnedBrief || row.dataset.sPinnedbrief || "",
      stageEnteredUtc: stageEntered,
      createdUtc,
      stageAgeDays: stageAgeDaysFresh,
      attemptsToday,
      attemptsThisWeek: attemptsWeek,
      attemptsThisMonth: attemptsMonth,
      attemptsThisYear: attemptsYear,
      attemptsLifetime: attemptsLife,
      lastContactChannel: row.dataset.crmLastChannel || row.dataset.sChannel || "",
      docChecklist: { completedCount: row.dataset.sDoccount || 0 },
      collaboration: { watchers: (row.dataset.crmWatchers || row.dataset.sWatchers || "").split(/,\s*/).filter(Boolean) }
    };
  }

  const res = await fetch(`/Leads/Lead?id=${encodeURIComponent(clientId)}`, withDialHeaders());
  if (!res.ok) throw new Error("Lead not found");
  const lead = await res.json();
  const dobIso = lead.dob ? lead.dob.slice(0,10) : "";
    return {
      clientUserId: lead.leadId || clientId,
      firstName: lead.firstName || "",
      lastName: lead.lastName || "",
      email: norm(lead.email),
      phone: norm(lead.phone),
      phone2: norm(lead.phone2),
      dob: dobIso,
      dobFormatted: lead.dobFormatted || formatDob(dobIso),
      age: lead.age || "",
      gender: lead.gender || "",
      addressLine: lead.addressLine || "",
      city: lead.city || "",
      state: lead.state || "",
      county: lead.county || "",
      zipCode: lead.zipCode || lead.zip || "",
      mortgageLender: lead.mortgageLender || "",
      loanAmount: lead.loanAmount || "",
      btc: lead.btc || "",
      crmStatus: lead.crmStatus || "Lead",
      crmPriority: lead.crmPriority || "Normal",
      crmLastTouch: lead.crmLastTouch || lead.updatedUtc || "",
      crmNextDate: lead.crmNextDate || "",
      crmNextText: lead.crmNextText || "",
      crmTags: lead.crmTags || "",
      agentNotes: lead.agentNotes || lead.crmNotes || "",
      pipelineStage: lead.bucket || "MortgageProtection",
      originalLeadType: normalizeOriginalLeadTypeValue(lead.originalLeadType) || normalizeOriginalLeadTypeValue(lead.bucket),
      meetingLocation: lead.meetingLocation || lead.addressLine || "",
      zoomJoinUrl: lead.zoomJoinUrl || "",
      usePersonalZoomLink: !!lead.usePersonalZoomLink,
      meetingTime: lead.meetingTime || "09:00",
      meetingDurationMinutes: lead.meetingDurationMinutes || 30,
      waitingOn: lead.waitingOn || "WaitingOnAgent",
      pinnedBrief: lead.pinnedBrief || "",
      stageEnteredUtc: lead.createdUtc || todayISO(),
      attemptsToday: lead.attemptsToday ?? lead.dialsToday ?? 0,
      attemptsThisWeek: lead.attemptsThisWeek ?? lead.dialsWeek ?? 0,
      attemptsThisMonth: lead.attemptsThisMonth ?? 0,
      attemptsThisYear: lead.attemptsThisYear ?? 0,
      attemptsLifetime: lead.attemptsLifetime ?? lead.callCount ?? 0,
      lastContactChannel: lead.lastContactChannel || "Call",
      docChecklist: lead.docChecklist || { completedCount: 0 },
      collaboration: lead.collaboration || { watchers: [] }
    };
  }

async function refreshLeadCountsFromServer(row){
  const clientId = row?.dataset.clientId;
  if (!clientId) return;
  try{
    const res = await fetch(`/Leads/Lead?id=${encodeURIComponent(clientId)}`, withDialHeaders());
    if (!res.ok) return;
    const lead = await res.json();
    const preferNumber = (...vals) => {
      for (const v of vals){
        const n = Number(v);
        if (Number.isFinite(n)) return n;
      }
      return 0;
    };
    const today = preferNumber(lead.attemptsToday, lead.dialsToday);
    const week = preferNumber(lead.attemptsThisWeek, lead.dialsWeek);
    const month = preferNumber(lead.attemptsThisMonth);
    const year = preferNumber(lead.attemptsThisYear);
    const life = preferNumber(lead.attemptsLifetime, lead.callCount);
    const stageEntered = lead.stageEnteredUtc || row.dataset.crmStageEntered || todayISO();
    row.dataset.sAttemptstoday = String(today);
    row.dataset.sAttemptsweek = String(week);
    row.dataset.sAttemptsmonth = String(month);
    row.dataset.sAttemptsyear = String(year);
    row.dataset.sAttemptslife = String(life);
    row.dataset.crmStageEntered = stageEntered;
    row.dataset.sStageentered = stageEntered;
    hydrateRow(row);
    syncAttemptSummary(row);
    renderAll();
  }catch{
    // swallow to keep UI stable; no optimistic increment
  }
}

async function copyText(text){
  const val = (text ?? "").toString();
  if (!val) return toast("Nothing to copy");
  try{
    await navigator.clipboard.writeText(val);
    toast("Copied");
  }catch{
    const ta = document.createElement("textarea");
    ta.value = val;
    document.body.appendChild(ta);
    ta.select();
    document.execCommand("copy");
    ta.remove();
    toast("Copied");
  }
}

function getAgentProfile(){
  return loadJSON("legend_agent_profile", {});
}

function saveAgentProfile(db){
  saveJSON("legend_agent_profile", db || {});
}

function ensureAgentPhone(){
  const profile = getAgentProfile();
  if (profile.phone) return profile.phone;
  const entered = window.prompt("Enter your call-back number to use in texts:", "");
  if (entered){
    profile.phone = entered.trim();
    saveAgentProfile(profile);
    return profile.phone;
  }
  return "";
}

function ensureAgentName(){
  const profile = getAgentProfile();
  if (profile.firstName) return profile.firstName;
  const entered = window.prompt("Enter your first name for texting:", "");
  if (entered){
    profile.firstName = entered.trim();
    saveAgentProfile(profile);
    return profile.firstName;
  }
  return "Agent";
}

function buildTextMessage(templateKey, row){
  const leadFirst = norm(row.dataset.first) || "there";
  const agentFirst = ensureAgentName();
  const agentPhone = ensureAgentPhone() || "623-223-7177";
  const addrLine = norm(row.dataset.addressLine) || norm(row.dataset.address) || "";
  const city = norm(row.dataset.city) || "";
  const state = norm(row.dataset.state) || "";
  const zip = norm(row.dataset.zipCode) || norm(row.dataset.zip) || "";
  const lender = norm(row.dataset.mortgageLender) || norm(row.dataset.lender) || "";

  if (templateKey === "MortgageProtection"){
    const addrFull = [addrLine, city, state, zip].filter(Boolean).join(" ").replace(/\s+/g, " ").trim();
    return `${leadFirst}, this is ${agentFirst} regarding your mortgage with ${lender}. Just left you a message. Give me a call back when you get this, we have some pending paperwork to get out to you regarding the mortgage for your property at ${addrFull || 'your property'}. The office number is ${agentPhone}, thanks`;
  }

  return `Hi ${leadFirst}, this is ${agentFirst}. Let's connect.`;
}

function formatPhoneDisplay(raw){
  const digits = (raw || "").replace(/\D/g, "");
  if (digits.length === 10){
    return `(${digits.slice(0,3)}) ${digits.slice(3,6)}-${digits.slice(6)}`;
  }
  return raw || "";
}

function formatCurrency(value){
  const num = Number(value) || 0;
  return num.toLocaleString("en-US", { style: "currency", currency: "USD", maximumFractionDigits: 0 });
}

async function sendTextForRow(row, templateKey){
  const phone = norm(row.dataset.phone);
  if (!phone) return toast("No phone for this lead");
  const message = buildTextMessage(templateKey, row);

  try {
    await navigator.clipboard.writeText(message);
    toast("Text template copied. Paste into your SMS app.");
  } catch {
    toast("Copied text to clipboard failed. You can press Send SMS to open your app.");
  }

  window.location.href = `sms:${encodeURIComponent(phone)}?&body=${encodeURIComponent(message)}`;
}

function closeLeadCommAuth(){
  const popup = document.getElementById("leadCommAuthGlobal");
  if (!popup) return;
  popup.style.display = "none";
  popup.dataset.clientId = "";
  popup.dataset.phone = "";
  popup.dataset.action = "";
  popup.dataset.template = "";
}

function syncAttemptSummary(row){
  if (!row) return;
  hydrateRow(row);

  if (activeClientId === row.dataset.clientId && dAttempts){
    dAttempts.textContent = `Attempts: ${row.dataset.crmAttemptsToday || 0} today • ${row.dataset.crmAttemptsWeek || 0} week • ${row.dataset.crmAttemptsLife || 0} total`;
  }

  if (activeClientDetail && activeClientId === row.dataset.clientId){
    activeClientDetail = {
      ...activeClientDetail,
      attemptsToday: parseInt(row.dataset.crmAttemptsToday || "0", 10) || 0,
      attemptsThisWeek: parseInt(row.dataset.crmAttemptsWeek || "0", 10) || 0,
      attemptsThisMonth: parseInt(row.dataset.crmAttemptsMonth || "0", 10) || 0,
      attemptsThisYear: parseInt(row.dataset.crmAttemptsYear || "0", 10) || 0,
      attemptsLifetime: parseInt(row.dataset.crmAttemptsLife || "0", 10) || 0
    };
  }
}

function ensureLeadCommAuthPopup(){
  let popup = document.getElementById("leadCommAuthGlobal");
  if (popup) return popup;

  popup = document.createElement("div");
  popup.id = "leadCommAuthGlobal";
  popup.className = "lead-comm-auth";
  popup.style.display = "none";
  popup.innerHTML = `
    <div class="lead-comm-auth-card">
      <div class="lead-comm-auth-label" data-auth-label></div>
      <div class="lead-comm-auth-copy" data-auth-copy></div>
      <button type="button" class="lead-comm-auth-number" data-auth-confirm></button>
      <div class="lead-comm-auth-note" data-auth-note></div>
      <div class="lead-comm-auth-actions">
        <button type="button" class="lead-comm-auth-cancel" data-auth-cancel>Cancel</button>
      </div>
    </div>
  `;
  document.body.appendChild(popup);

  popup.addEventListener("click", async (e) => {
    const cancelBtn = e.target.closest("[data-auth-cancel]");
    if (cancelBtn){
      closeLeadCommAuth();
      return;
    }

    const confirmBtn = e.target.closest("[data-auth-confirm]");
    if (!confirmBtn) return;

    const action = popup.dataset.action || "";
    const clientId = popup.dataset.clientId || "";
    const phone = popup.dataset.phone || "";
    const templateKey = popup.dataset.template || "MortgageProtection";
    const row = rows.find(r => r.dataset.clientId === clientId);

    closeLeadCommAuth();

    if (!phone) return toast("No phone for this lead");

    if (action === "call"){
      if (row) await incrementCallLead(row);
      window.location.href = `tel:${encodeURIComponent(phone)}`;
      return;
    }

    if (action === "text" && row){
      await sendTextForRow(row, templateKey);
    }
  });

  return popup;
}

function showLeadCommAuth(anchorEl, options = {}){
  const row = options.row || null;
  const phone = norm(options.phone || row?.dataset.phone);
  if (!phone) return toast("No phone for this lead");

  const rect = anchorEl.getBoundingClientRect();
  closeAllTextMenus(null);
  const popup = ensureLeadCommAuthPopup();
  const labelEl = $("[data-auth-label]", popup);
  const copyEl = $("[data-auth-copy]", popup);
  const numberEl = $("[data-auth-confirm]", popup);
  const noteEl = $("[data-auth-note]", popup);
  const action = options.action === "text" ? "text" : "call";

  popup.dataset.action = action;
  popup.dataset.clientId = row?.dataset.clientId || activeClientId || "";
  popup.dataset.phone = phone;
  popup.dataset.template = options.templateKey || "MortgageProtection";

  if (labelEl){
    labelEl.textContent = action === "call" ? "CALL AUTHORIZATION" : "TEXT AUTHORIZATION";
  }
  if (copyEl){
    copyEl.textContent = action === "call"
      ? "Click the number below to open the dialer. If you cancel before that click, it does not count as a dial."
      : "Click the number below to open your text app. Nothing opens until you click the number.";
  }
  if (numberEl){
    numberEl.textContent = formatPhoneDisplay(phone);
  }
  if (noteEl){
    noteEl.textContent = action === "call"
      ? "Dial count updates only after the number click."
      : "Text authorization stays red until you click the number or cancel.";
  }

  popup.style.visibility = "hidden";
  popup.style.display = "block";

  const width = popup.offsetWidth || 320;
  const height = popup.offsetHeight || 180;
  const left = Math.max(12, Math.min(window.innerWidth - width - 12, rect.left + (rect.width / 2) - (width / 2)));
  const preferredTop = rect.bottom + 10;
  const top = preferredTop + height > window.innerHeight - 12
    ? Math.max(12, rect.top - height - 10)
    : preferredTop;

  popup.style.left = `${Math.round(left)}px`;
  popup.style.top = `${Math.round(top)}px`;
  popup.style.visibility = "visible";
}

function closeAllTextMenus(exceptId){
  const menu = document.getElementById('textMenuGlobal');
  if (menu && menu.dataset.clientId !== exceptId){
    menu.style.display = 'none';
    menu.dataset.clientId = '';
  }
}

async function incrementCallLead(row){
  const clientId = row?.dataset.clientId;
  if (!clientId) return;
  if (row.dataset.callInFlight === "1") return parseInt(row.dataset.sAttemptslife || row.dataset.crmAttemptsLife || '0', 10) || 0;
  row.dataset.callInFlight = "1";
  let payload = null;
  try{
    const res = await fetch('/Leads/IncrementCall', withDialHeaders({
      method: 'POST',
      headers: {
        'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8',
        'RequestVerificationToken': getAntiForgeryToken()
      },
      credentials: 'include',
      body: `id=${encodeURIComponent(clientId)}`
    }));
    if (res.ok){
      payload = await res.json().catch(() => null);
    }
  }catch{}

  if (!payload || typeof payload.attemptsToday !== "number") {
    toast("Call not recorded — please retry. (No change applied)");
    await refreshLeadCountsFromServer(row);
    delete row.dataset.callInFlight;
    return parseInt(row.dataset.sAttemptslife || row.dataset.crmAttemptsLife || '0', 10) || 0;
  }

  const today = Number(payload.attemptsToday) || 0;
  const week = Number(payload.attemptsThisWeek) || 0;
  const month = Number(payload.attemptsThisMonth) || 0;
  const year = Number(payload.attemptsThisYear) || 0;
  const life = Number(payload.attemptsLifetime ?? payload.callCount) || 0;

  row.dataset.sAttemptstoday = String(today);
  row.dataset.sAttemptsweek = String(week);
  row.dataset.sAttemptsmonth = String(month);
  row.dataset.sAttemptsyear = String(year);
  row.dataset.sAttemptslife = String(life);
  hydrateRow(row);
  updateCallMetrics();
  syncAttemptSummary(row);
  if (liveSync) liveSync.sendCall(clientId, life);
  renderAll();
  delete row.dataset.callInFlight;
  return life;
}

function daysDiff(fromISO){
  if (!fromISO) return null;
  const d = new Date(fromISO + "T00:00:00");
  const now = new Date();
  return Math.floor((now - d) / (1000*60*60*24));
}
function isOverdue(nextISO){
  const diff = daysDiff(nextISO);
  return diff !== null && diff > 0;
}
function isToday(nextISO){ return nextISO && nextISO === todayISO(); }
function isSoon(nextISO){
  const diff = daysDiff(nextISO);
  return diff !== null && diff <= 3 && diff >= -3;
}

/* ========= CRM STORE ========= */
const pipelineLabels = {
  MortgageProtection: "MORTGAGE PROTECTION LEADS",
  LifeInsurance: "LIFE INSURANCE LEADS",
  FinalExpense: "FINAL EXPENSE LEADS",
  DisabilityInsurance: "DISABILITY INSURANCE LEADS",
  Contacted: "Contacted",
  Booked: "Booked",
  FollowUp: "Follow Up",
  NeedsDocs: "Needs Docs",
  PolicyPlaced: "Policy Placed",
  NotInterested: "Not Interested",
  Nurture: "Nurture",
  NoAnswer: "No Answer",
  Lost: "Lost",
  AIReception: "AI Reception"
};

const statusLabels = {
  Lead: "Lead",
  Prospect: "Prospect",
  Active: "Portal Client",
  Dormant: "Inactive"
};

function crmStatusLabel(status){
  return statusLabels[status] || "Lead";
}

const pipelineStages = [
  // Priority buckets up top
  { key: "PolicyPlaced", label: "Policy Placed", tone: "good", className: "stage-submitted", note: "Placed business; service and retain." },
  { key: "Booked", label: "Booked", tone: "good", className: "stage-meetingscheduled", note: "Appointment set; prepare and confirm." },
  { key: "FollowUp", label: "Follow Up", tone: "warn", className: "stage-proposalsent", note: "Needs follow-up after conversation." },
  // Core lead buckets (keep existing order)
  { key: "MortgageProtection", label: "MORTGAGE PROTECTION LEADS", tone: "warn", className: "stage-newlead", note: "Mortgage Protection leads direct from scripts." },
  { key: "LifeInsurance", label: "LIFE INSURANCE LEADS", tone: "good", className: "stage-qualified", note: "Life Insurance leads ready for first touch." },
  { key: "FinalExpense", label: "FINAL EXPENSE LEADS", tone: "warn", className: "stage-contacted", note: "Final Expense leads queued for contact." },
  { key: "DisabilityInsurance", label: "DISABILITY INSURANCE LEADS", tone: "warn", className: "stage-opportunities", note: "Disability Insurance leads to qualify fast." },
  { key: "Contacted", label: "Contacted", tone: "info", className: "stage-contacted", note: "The first touch happened. Keep momentum alive." },
  { key: "NeedsDocs", label: "Needs Docs", tone: "info", className: "stage-applicationstarted", note: "Waiting on documents to proceed." },
  { key: "NotInterested", label: "Not Interested", tone: "bad", className: "stage-closedlost", note: "Lead not moving forward right now." },
  { key: "Nurture", label: "Nurture", tone: "warn", className: "stage-nurture", note: "Stay in touch over time." },
  { key: "NoAnswer", label: "No Answer", tone: "warn", className: "stage-nurture", note: "Could not reach lead yet." },
  { key: "Lost", label: "Lost", tone: "bad", className: "stage-closedlost", note: "Opportunity was lost." },
  { key: "AIReception", label: "AI Reception", tone: "info", className: "stage-contacted", note: "Handled by AI receptionist flow." }
];

const pipelineAliases = {
  newlead: "MortgageProtection",
  opportunities: "Contacted",
  qualified: "Contacted",
  client: "PolicyPlaced",
  businessclient: "PolicyPlaced",
  meetingscheduled: "Booked",
  proposalsent: "FollowUp",
  applicationstarted: "NeedsDocs",
  medicare: "MortgageProtection",
  medicareleads: "MortgageProtection",
  submitted: "PolicyPlaced",
  closedlost: "NotInterested",
  closedwon: "PolicyPlaced",
  noanswer: "NoAnswer",
  lost: "Lost",
  aireception: "AIReception",
  aireceptionist: "AIReception",
  "ai-reception": "AIReception",
  "ai_reception": "AIReception",
  leftvm: "FollowUp",
  spoke: "Contacted"
};

function normalizePipelineStageValue(stage, fallback = "MortgageProtection"){
  const value = norm(stage);
  if (!value) return fallback;
  const exact = pipelineStages.find(x => x.key.toLowerCase() === value.toLowerCase());
  if (exact) return exact.key;
  return pipelineAliases[value.toLowerCase()] || fallback;
}

const productBuckets = new Set(["MortgageProtection","LifeInsurance","FinalExpense","DisabilityInsurance"]);

function normalizeOriginalLeadTypeValue(value){
  const normalized = normalizePipelineStageValue(value, "");
  return productBuckets.has(normalized) ? normalized : "";
}

function rowOriginalLeadType(row){
  return normalizeOriginalLeadTypeValue(row?.dataset?.originalLeadType)
    || normalizeOriginalLeadTypeValue(row?.dataset?.crmPipeline)
    || normalizeOriginalLeadTypeValue(row?.dataset?.sPipeline);
}

function isLifeOrFinalExpenseLeadType(leadType){
  return leadType === "LifeInsurance" || leadType === "FinalExpense";
}

function resolveQuickViewLeadType(row, detail, stageOverride){
  return normalizeOriginalLeadTypeValue(detail?.originalLeadType)
    || normalizeOriginalLeadTypeValue(row?.dataset?.originalLeadType)
    || normalizeOriginalLeadTypeValue(stageOverride)
    || normalizeOriginalLeadTypeValue(detail?.pipelineStage)
    || normalizeOriginalLeadTypeValue(row?.dataset?.crmPipeline)
    || normalizeOriginalLeadTypeValue(row?.dataset?.sPipeline);
}

function updateImportCsvHelp(bucket){
  const help = document.getElementById("importCsvHelp");
  if (!help) return;

  const key = normalizePipelineStageValue(bucket || "MortgageProtection", "MortgageProtection");
  if (key === "LifeInsurance" || key === "FinalExpense"){
    help.textContent = "Save your Excel sheet as CSV (UTF-8). Required column order (no extra columns): First Name, Last Name, Address, City, State, County, Zip Code, Age, DOB, M/F, Requested, Phone #, Phone # 2. Everything is created as a Lead in the Lead bucket. Max 500 rows per upload.";
    return;
  }

  help.textContent = "Save your Excel sheet as CSV (UTF-8). Required column order (no extra columns): First Name, Last Name, Address, City, State, County, Zip Code, Age, DOB, M/F, Lender, Loan, Phone #, Phone # 2. Everything is created as a Lead in the Lead bucket. Max 500 rows per upload.";
}

function applyQuickViewContactProfileLabels(row, detail, stageOverride){
  const leadType = resolveQuickViewLeadType(row, detail, stageOverride);
  const hideLender = isLifeOrFinalExpenseLeadType(leadType);

  const lenderField = document.getElementById("dLenderField");
  const lenderLabel = document.getElementById("dLenderLabel");
  const loanLabel = document.getElementById("dLoanLabel");

  if (lenderField){
    lenderField.style.display = hideLender ? "none" : "";
  }
  if (lenderLabel){
    lenderLabel.textContent = "Mortgage Lender";
  }
  if (loanLabel){
    loanLabel.textContent = hideLender ? "Requested" : "Loan Amount";
  }
  if (dLoanAmount){
    dLoanAmount.placeholder = hideLender ? "e.g. 250" : "e.g. 250000";
  }
  if (hideLender && dLender){
    dLender.value = "";
  }
}

function matchesStageSelection(row, stage){
  if (!stage) return true;
  if (productBuckets.has(stage)) return rowOriginalLeadType(row) === stage;
  return norm(row.dataset.crmPipeline) === stage;
}

let pipelineOrder = loadJSON(LS_PIPELINE_ORDER, {});

function savePipelineOrder(db){
  pipelineOrder = db || {};
  saveJSON(LS_PIPELINE_ORDER, pipelineOrder);
}

async function persistOrder(stageKey, orderedIds){
  if (!orderedIds || !orderedIds.length) return;
  try{
    await postJson(REORDER_URL, { bucket: stageKey, ids: orderedIds });
    if (liveSync) liveSync.sendReorder(stageKey, orderedIds);
  }catch(err){
    console.error("Reorder persist failed", err);
  }
}

function ensureStageOrder(stageKey, ids){
  const current = pipelineOrder[stageKey] || [];
  const merged = current.filter(id => ids.includes(id)).concat(ids.filter(id => !current.includes(id)));
  if (JSON.stringify(current) !== JSON.stringify(merged)){
    savePipelineOrder({ ...pipelineOrder, [stageKey]: merged });
  }else{
    pipelineOrder[stageKey] = merged;
  }
  return pipelineOrder[stageKey];
}

function orderedStageRows(stageKey, rows){
  const ids = rows.map(r => r.dataset.clientId).filter(Boolean);
  const orderIds = ensureStageOrder(stageKey, ids);
  const map = new Map(rows.map(r => [r.dataset.clientId, r]));
  const ordered = [];
  orderIds.forEach(id => {
    const row = map.get(id);
    if (row){
      ordered.push(row);
      map.delete(id);
    }
  });
  map.forEach(row => ordered.push(row));
  return ordered;
}

function laneOrderFromDom(stageKey){
  const zone = pipelineBoard?.querySelector(`[data-dropzone="${stageKey}"]`);
  if (!zone) return ensureStageOrder(stageKey, []);
  const ids = Array.from(zone.querySelectorAll(".client-card")).map(c => c.dataset.cardid).filter(Boolean);
  return ensureStageOrder(stageKey, ids);
}

/* ========= Open handlers ========= */
function wireOpenHandlers(){
  // Table + list

  $$(".open-drawer").forEach(el => {
    if (el.dataset.boundOpen) return;
    el.dataset.boundOpen = "1";
    el.addEventListener("click", (e) => {
      e.preventDefault();
      const row = el.closest(".client-row");
      if (row) openDrawerForRow(row);
    });
  });

  // View Production button handler
  $$(".view-production-btn").forEach(el => {
    if (el.dataset.boundOpen) return;
    el.dataset.boundOpen = "1";
    el.addEventListener("click", (e) => {
      e.preventDefault();
      const leadId = el.getAttribute("data-lead-id");
      const leadName = el.getAttribute("data-lead-name");
      const modalEl = document.getElementById('productionModal');
      const idEl = document.getElementById('prodLeadId');
      const nameEl = document.getElementById('prodLeadName');
      if (idEl) idEl.value = leadId ?? '';
      if (nameEl) nameEl.textContent = leadName ?? leadId ?? '';
      if (typeof loadProductionHistory === 'function') {
        loadProductionHistory(leadId);
      }
      if (modalEl && window.bootstrap?.Modal) {
        window.bootstrap.Modal.getOrCreateInstance(modalEl).show();
      }
    });
  });

  $$("[data-open-row]").forEach(el => {
    if (el.dataset.boundOpen) return;
    el.dataset.boundOpen = "1";
    el.addEventListener("click", (e) => {
      if (e.target.closest("a, button, input, select, textarea, label")) return;
      const row = el.closest(".client-row");
      if (row) openDrawerForRow(row);
    });
  });

  // Pipeline cards
  $$("[data-open-card]").forEach(el => {
    if (el.dataset.boundOpen) return;
    el.dataset.boundOpen = "1";
    el.addEventListener("click", (e) => {
      e.preventDefault();
      const id = el.getAttribute("data-open-card");
      if (!id) return;
      const row = rows.find(r => r.dataset.clientId === id);
      if (row) openDrawerForRow(row);
    });
  });
}

function pipelineLabel(stage){
  return pipelineLabels[stage] || "Lead";
}

function pipelineMeta(stage){
  return pipelineStages.find(x => x.key === stage) || pipelineStages[0];
}

function pipelineClass(stage){
  return pipelineMeta(stage).className;
}

const waitingLabels = {
  WaitingOnAgent: "Waiting On Agent",
  WaitingOnClient: "Waiting On Client",
  WaitingOnCarrier: "Waiting On Carrier",
  WaitingOnUnderwriting: "Waiting On Underwriting",
  WaitingOnDocs: "Waiting On Docs"
};

function waitingLabel(value){
  return waitingLabels[value] || "Waiting On Agent";
}

function stageAgeDays(row){
  const stageEntered = norm(row.dataset.crmStageEntered);
  if (!stageEntered) return 0;
  return Math.max(0, daysDiff(stageEntered) || 0);
}

function hasDuplicateWarning(row){
  return row.dataset.crmDupeEmail === "true"
    || row.dataset.crmDupePhone === "true"
    || row.dataset.crmDupeHousehold === "true";
}

/* ========= DOM CACHED ========= */
const legendWrap = $("#legendWrap");
const bar = $("#legendBar");

const rows = $$(".client-row");
const kpiGrid = $("#kpiGrid");
const cmToday = $("#cmToday");
const cmWeek = $("#cmWeek");
const cmMonth = $("#cmMonth");
const cmYearBtn = $("#cmYearBtn");
const btnDialYear = $("#btnDialYear");

const statusFilter = $("#statusFilter");
const priorityFilter = $("#priorityFilter");
const stageFilter = $("#stageFilter");
const attentionFilter = $("#attentionFilter");
const stateFilter = $("#stateFilter");
const sortBy = $("#sortBy");
const pageSize = $("#pageSize");
const viewMode = $("#viewMode");
const density = $("#density");

const btnCopyEmails = $("#btnCopyEmails");
const btnExportCsv = $("#btnExportCsv");
const btnClearSel = $("#btnClearSel");
const btnOpenFirst = $("#btnOpenFirst");
const btnBulkEdit = $("#btnBulkEdit");
const btnBulkDelete = $("#btnBulkDelete");
const btnDeleteBucket = $("#btnDeleteBucket");
const savedViewsBar = $("#savedViewsBar");
const btnCallTaskMode = $("#btnCallTaskMode");
const myDayQueue = $("#myDayQueue");
const mydayFocus = $("#mydayFocus");
const btnMyDayBack = $("#btnMyDayBack");
const btnMyDayCallTask = $("#btnMyDayCallTask");
const mydayFocusTitle = $("#mydayFocusTitle");
const mydayFocusSub = $("#mydayFocusSub");
const mydayFocusCount = $("#mydayFocusCount");

const chkAll = $("#chkAll");

const btnPrev = $("#btnPrev");
const btnNext = $("#btnNext");
const pageNow = $("#pageNow");
const pageMax = $("#pageMax");
const pagerInfo = $("#pagerInfo");

const selCount = $("#selCount");

const tableView = $("#tableView");
const cardsView = $("#cardsView");
const pipelineBoard = $("#pipelineBoard");
const pipelineStageNav = $("#pipelineStageNav");
const pipelineBoardCount = $("#pipelineBoardCount");
const pipelineTotalCards = $("#pipelineTotalCards");
const pipelineFocusPill = $("#pipelineFocusPill");
const btnPipeOverdue = $("#btnPipeOverdue");
const btnPipeNeeds = $("#btnPipeNeeds");
const btnPipeMeetings = $("#btnPipeMeetings");
const btnPipeTable = $("#btnPipeTable");
const btnPipeReset = $("#btnPipeReset");
const pipelineFocusBar = $("#pipelineFocusBar");
const pipelineFocusTitle = $("#pipelineFocusTitle");
const pipelineFocusSub = $("#pipelineFocusSub");
const btnBoardBack = $("#btnBoardBack");

// Track whether Quick View is currently editing to avoid renderAll race conditions
let drawerEditing = false;

const btnEnableReminders = $("#btnEnableReminders");
const btnReminders = $("#btnReminders");
const remCount = $("#remCount");

const modalBackdrop = $("#modalBackdrop");
const colsModal = $("#colsModal");
const shortcutsModal = $("#shortcutsModal");
const remindersModal = $("#remindersModal");
const cmdModal = $("#cmdModal");
const importModal = $("#importModal");
const importFile = $("#importFile");
const importResult = $("#importResult");
const importBucketSelect = $("#importBucket");
const btnImportSubmit = $("#btnImportSubmit");
const btnImportLeads = $("#btnImportLeads");
let pendingImportBucket = null;
const colsBody = $("#colsBody");

const btnCalendarAuth = $("#btnCalendarAuth");
const btnResourceCalendar = $("#btnResourceCalendar");
const btnFilterMeetings = $("#btnFilterMeetings");
const btnFilterOverdue = $("#btnFilterOverdue");
const bulkModal = $("#bulkModal");
const callTaskModal = $("#callTaskModal");
const callTaskBody = $("#callTaskBody");
const btnPipelineRefresh = $("#btnPipelineRefresh");
const btnPipelineAll = $("#btnPipelineAll");
const btnPipelineCallTask = $("#btnPipelineCallTask");
const bStage = $("#bStage");
const bWaitingOn = $("#bWaitingOn");
const bPriority = $("#bPriority");
const bNextDate = $("#bNextDate");
const bNextText = $("#bNextText");
const bTags = $("#bTags");
const bSharedNote = $("#bSharedNote");
const btnRunBulk = $("#btnRunBulk");
const stagePickerSelect = $("#stagePickerSelect");
const stagePickerDetail = $("#stagePickerDetail");
const stagePickerOpen = $("#btnStagePickerOpen");
const stagePickerName = $("#stagePickerName");
const stagePickerCount = $("#stagePickerCount");
const stagePickerNote = $("#stagePickerNote");

/* Drawer elements */
const drawer = $("#drawer");
const drawerBackdrop = $("#drawerBackdrop");
const btnCloseDrawer = $("#btnCloseDrawer");
const leadQuickActionsShortcut = $("#leadQuickActionsShortcut");
const leadActionsHubModal = $("#leadActionsHubModal");
const noteOpenBtn = document.querySelector("[data-note-self-open]");
const noteOverlay = document.querySelector("[data-note-self-overlay]");
const noteCloseBtn = document.querySelector("[data-note-self-close]");
const noteDateInput = document.querySelector("[data-note-self-date]");
const noteLeadInput = document.querySelector("[data-note-self-lead]");
const noteDatesSelect = document.querySelector("[data-note-self-dates]");
const noteSaveBtn = document.querySelector("[data-note-self-save]");
const noteWentWell = document.querySelector("[data-note-self-well]");
const noteCouldBetter = document.querySelector("[data-note-self-better]");
const noteStatusEl = document.querySelector("[data-note-self-status]");

let activeClientId = null;
let activeClientDetail = null;
let pipelineFocusStage = "";
let activeMyDayQueue = "";
const MYDAY_QUEUE_KEYS = ["callsnow", "today", "overdue", "meetings", "waitingclient", "waitingcarrier"];
const MYDAY_SNAPSHOT_URL = "/Leads/MyDaySnapshot";
const MYDAY_SNAPSHOT_TTL_MS = 15 * 1000;
let myDaySnapshot = { counts: {}, idsByQueue: {}, loadedAt: 0, isLoading: false };
let pipelineNavSelectedStage = "";
let pipelineNavSearchTerm = "";
let draggingClientId = null;
let meetingSuggestAbort = null;
let quickViewOpenedFromUrl = false;
let leadActionsLoadPromise = null;

const STAGE_PICKER_TONES = [
  "stage-newlead",
  "stage-opportunities",
  "stage-contacted",
  "stage-qualified",
  "stage-client",
  "stage-businessclient",
  "stage-meetingscheduled",
  "stage-proposalsent",
  "stage-applicationstarted",
  "stage-submitted",
  "stage-closedlost",
  "stage-nurture"
];

function countRowsForStage(stageKey){
  if (!Array.isArray(rows) || !rows.length) return 0;
  if (productBuckets.has(stageKey)){
    return rows.filter(r => matchesStageSelection(r, stageKey)).length;
  }
  return rows.filter(r => norm(r.dataset.crmPipeline) === stageKey).length;
}

function applyStagePickerTone(el, className){
  if (!el) return;
  el.classList.remove(...STAGE_PICKER_TONES);
  if (className) el.classList.add(className);
}

function syncStagePickerUi(stageOverride = ""){
  if (!stagePickerSelect) return;

  const selected = normalizePipelineStageValue(stageOverride || stagePickerSelect.value || "MortgageProtection", "MortgageProtection");
  const option = stagePickerSelect.querySelector(`option[value="${selected}"]`);
  if (option) stagePickerSelect.value = selected;

  const meta = pipelineMeta(selected);
  const stageCount = countRowsForStage(selected);

  if (stagePickerName) stagePickerName.textContent = option?.textContent?.trim() || meta.label || selected;
  if (stagePickerNote) stagePickerNote.textContent = option?.dataset.note || meta.note || "";
  if (stagePickerCount) stagePickerCount.textContent = String(stageCount);

  [stagePickerOpen, stagePickerDetail].forEach(el => {
    if (!el) return;
    el.setAttribute("data-stagejump", selected);
    applyStagePickerTone(el, meta.className);
  });

  if (stagePickerOpen) stagePickerOpen.classList.add("btn-stage-tone");
}

/* ========= DIAL PERIOD ROLLOVER ========= */
let dialRefreshInFlight = false;

function metricRows(){
  return $$(".client-row");
}

async function ensureDialPeriodsFresh(){
  if (dialRefreshInFlight) return;
  dialRefreshInFlight = true;
  try {
    try { localStorage.removeItem("legendDialPeriods"); } catch {}

    const res = await fetch("/Leads/Leads", withDialHeaders({
      credentials: "include",
      cache: "no-store"
    }));

    if (res.ok){
      const leads = await res.json().catch(() => []);
      const byId = new Map((Array.isArray(leads) ? leads : []).map(x => [x?.leadId, x]));

      metricRows().forEach(row => {
        const payload = byId.get(row.dataset.clientId);
        if (!payload) return;

        row.dataset.sAttemptstoday = String(payload.attemptsToday ?? row.dataset.sAttemptstoday ?? 0);
        row.dataset.sAttemptsweek = String(payload.attemptsThisWeek ?? row.dataset.sAttemptsweek ?? 0);
        row.dataset.sAttemptsmonth = String(payload.attemptsThisMonth ?? row.dataset.sAttemptsmonth ?? 0);
        row.dataset.sAttemptsyear = String(payload.attemptsThisYear ?? row.dataset.sAttemptsyear ?? 0);
        row.dataset.sAttemptslife = String(payload.attemptsLifetime ?? payload.callCount ?? row.dataset.sAttemptslife ?? 0);
        hydrateRow(row);
        if (activeClientId === row.dataset.clientId) syncAttemptSummary(row);
      });
    }
  }catch(err){
    console.warn("Dial period refresh failed", err);
  }finally{
    dialRefreshInFlight = false;
    updateCallMetrics();
  }
}
let calendarBusyAbort = null;
let meetingSuggestTimer = null;
let quickViewScrollY = 0;

function lockPageScrollForQuickView(){
  if (document.body.dataset.quickViewLocked === "true") return;
  quickViewScrollY = window.scrollY || window.pageYOffset || 0;
  document.body.dataset.quickViewLocked = "true";
  document.body.style.position = "fixed";
  document.body.style.top = `-${quickViewScrollY}px`;
  document.body.style.left = "0";
  document.body.style.right = "0";
  document.body.style.width = "100%";
  document.body.style.overflow = "hidden";
}

function unlockPageScrollForQuickView(){
  if (document.body.dataset.quickViewLocked !== "true") return;
  const restoreY = quickViewScrollY;
  delete document.body.dataset.quickViewLocked;
  document.body.style.position = "";
  document.body.style.top = "";
  document.body.style.left = "";
  document.body.style.right = "";
  document.body.style.width = "";
  document.body.style.overflow = "";
  window.scrollTo(0, restoreY);
}

const dName = $("#dName");
const dEmail = $("#dEmail");
const dPhone = $("#dPhone");
const dFirst = $("#dFirst");
const dLast = $("#dLast");
const dEmailInput = $("#dEmailInput");
const dPhoneInput = $("#dPhoneInput");
const dPhone2Input = $("#dPhone2Input");
const dDob = $("#dDob");
const dGender = $("#dGender");
const dAddress = $("#dAddress");
const dCity = $("#dCity");
const dState = $("#dState");
const dCounty = $("#dCounty");
const dZip = $("#dZip");
const dLender = $("#dLender");
const dLoanAmount = $("#dLoanAmount");
const dAge = $("#dAge");
const dBtc = $("#dBtc");

const dStatus = $("#dStatus");
const dPipelineStage = $("#dPipelineStage");
const dLastTouch = $("#dLastTouch");
const dTags = $("#dTags");
const dNotes = $("#dNotes");

const dNextDate = $("#dNextDate");
const dMeetingNextDate = $("#dMeetingNextDate") || { value: "", addEventListener(){} };
const dNextText = $("#dNextText");
const dPriority = $("#dPriority");
const dMeetingType = $("#dMeetingType") || { value: "Phone", addEventListener(){} };
const dMeetingTime = $("#dMeetingTime") || { value: "09:00", addEventListener(){} };
const dMeetingDuration = $("#dMeetingDuration") || { value: "30", addEventListener(){} };
const dMeetingLocation = $("#dMeetingLocation") || { value: "", dataset: {}, classList: { add(){}, remove(){} }, addEventListener(){}, readOnly: false, placeholder: "" };
const dMeetingLocationSuggest = $("#dMeetingLocationSuggest") || { classList: { add(){}, remove(){} }, innerHTML: "" };
const dZoomWrap = $("#dZoomWrap") || { style: {}, classList: { add(){}, remove(){} } };
const dUsePersonalZoomLink = $("#dUsePersonalZoomLink") || { checked: false, addEventListener(){} };
const dZoomJoinUrl = $("#dZoomJoinUrl") || { value: "", addEventListener(){} };
const dZoomStatus = $("#dZoomStatus") || { textContent: "" };
const btnZoomSavePersonal = $("#btnZoomSavePersonal");
const btnZoomClearPersonal = $("#btnZoomClearPersonal");
const dCalendarBusyDate = $("#dCalendarBusyDate");
const dCalendarBusyNote = $("#dCalendarBusyNote");
const dCalendarBusyList = $("#dCalendarBusyList");
const dCalendarWorkHours = $("#dCalendarWorkHours");
const dCalendarFreeList = $("#dCalendarFreeList");

const dPortalWrap = $("#dPortalWrap");
const dSaved = $("#dSaved");
const dWaitingOn = $("#dWaitingOn");
const dPinnedBrief = $("#dPinnedBrief");
const dStageAge = $("#dStageAge");
const dAttempts = $("#dAttempts");
const dWaitingOnPill = $("#dWaitingOnPill");
const dOutcomeSuggestion = $("#dOutcomeSuggestion");
const dDocIdReceived = $("#dDocIdReceived");
const dDocAppSent = $("#dDocAppSent");
const dDocAppSigned = $("#dDocAppSigned");
const dDocPolicyDelivered = $("#dDocPolicyDelivered");
const dDocReviewBooked = $("#dDocReviewBooked");
const dAssignedOwner = $("#dAssignedOwner");
const dWatchers = $("#dWatchers");
const dMentionNote = $("#dMentionNote");
const mentionList = $("#mentionList");

const btnSaveLocal = $("#btnSaveLocal");
const btnResetLocal = $("#btnResetLocal");
const btnMarkToday = $("#btnMarkToday");
const btnSetNextToday = $("#btnSetNextToday");
const btnMeetingNextToday = $("#btnMeetingNextToday");
const btnCopyContact = $("#btnCopyContact");
const btnMail = $("#btnMail");
const btnCall = $("#btnCall");
const btnOpenQueue = $("#btnOpenQueue");
const btnDeleteClient = $("#btnDeleteClient");

const dActType = $("#dActType");
const dActDate = $("#dActDate");
const dActNote = $("#dActNote");
const btnAddActivity = $("#btnAddActivity");
const btnClearTimeline = $("#btnClearTimeline");
const btnCreateCalendarEvent = $("#btnCreateCalendarEvent");
const timeline = $("#timeline");
const timelineFilters = $("#timelineFilters");
const leadNextActionDatePreview = $("#leadNextActionDatePreview");
const leadNextActionPreview = $("#leadNextActionPreview");
const leadNotePreview = $("#leadNotePreview");

const cmdInput = $("#cmdInput");
let activeTimelineFilter = "all";

function refreshLeadOverviewSummary(){
  if (leadNextActionDatePreview){
    const nextDate = norm(dNextDate?.value);
    leadNextActionDatePreview.textContent = nextDate || "Not tracked";
  }
  if (leadNextActionPreview){
    const nextText = norm(dNextText?.value);
    leadNextActionPreview.textContent = nextText || "Not tracked for this lead yet.";
  }
  if (leadNotePreview){
    const note = norm(dNotes?.value);
    leadNotePreview.textContent = note || "No CRM note yet.";
  }
}

// Quick View autosave (debounced)
const AUTOSAVE_DELAY_MS = 2000;  // 2 seconds: reduces UI lag from rapid keystroke autosaves
let quickViewAutosaveTimer = null;
let quickViewAutosaveInFlight = false;

function calculateAgeFromDOB(dobString) {
  if (!dobString) return "";
  const dob = new Date(dobString);
  if (isNaN(dob.getTime())) return "";
  const today = new Date();
  let age = today.getFullYear() - dob.getFullYear();
  const hasHadBirthdayThisYear = (today.getMonth() > dob.getMonth()) || 
    (today.getMonth() === dob.getMonth() && today.getDate() >= dob.getDate());
  if (!hasHadBirthdayThisYear) age--;
  return age.toString();
}

function buildLeadQuickViewOverrides(){
  return {
    crmStatus: norm(dStatus.value) || "Lead",
    crmPriority: norm(dPriority.value) || "Normal",
    crmLastTouch: norm(dLastTouch.value) || null,
    crmNextDate: norm(dNextDate.value) || null,
    crmNextText: norm(dNextText.value),
    crmTags: norm(dTags.value),
    agentNotes: norm(dNotes.value),
    pipelineStage: normalizePipelineStageValue(norm(dPipelineStage.value), "MortgageProtection"),
    meetingLocation: norm(dMeetingLocation.value),
    zoomJoinUrl: norm(dZoomJoinUrl.value),
    usePersonalZoomLink: !!dUsePersonalZoomLink.checked,
    meetingTime: norm(dMeetingTime.value) || "09:00",
    meetingDurationMinutes: parseInt(dMeetingDuration.value || "30", 10) || 30,
    waitingOn: norm(dWaitingOn.value) || "WaitingOnAgent",
    pinnedBrief: norm(dPinnedBrief.value),
    docIdReceived: !!dDocIdReceived.checked,
    docAppSent: !!dDocAppSent.checked,
    docAppSigned: !!dDocAppSigned.checked,
    docPolicyDelivered: !!dDocPolicyDelivered.checked,
    docReviewBooked: !!dDocReviewBooked.checked,
    watchers: norm(dWatchers.value),
    mentionNote: norm(dMentionNote.value)
  };
}

async function performQuickViewAutosave(){
  if (!activeClientId) return;
  if (quickViewAutosaveInFlight){
    quickViewAutosaveTimer = setTimeout(performQuickViewAutosave, AUTOSAVE_DELAY_MS);
    return;
  }
  const row = rows.find(r => r.dataset.clientId === activeClientId);
  if (!row) return;
  quickViewAutosaveInFlight = true;
  try{
    await saveQuickViewForRow(row, buildLeadQuickViewOverrides(), "Saved ✔");
    if (dSaved) dSaved.textContent = "Autosaved ✔";
    if (dMentionNote) dMentionNote.value = "";
    maybeNotifyImmediateForDue(activeClientId);
  }catch(err){
    console.error("Quick View autosave failed", err);
    if (dSaved) dSaved.textContent = "Autosave failed";
  }finally{
    quickViewAutosaveInFlight = false;
  }
}

function queueQuickViewAutosave(reason){
  if (!activeClientId) return;
  if (dSaved) dSaved.textContent = reason || "Saving…";
  clearTimeout(quickViewAutosaveTimer);
  quickViewAutosaveTimer = setTimeout(performQuickViewAutosave, AUTOSAVE_DELAY_MS);
}

function wireQuickViewAutosave(){
  const autosaveFields = [
    // Contact fields (email, phone, address) are excluded here to allow clean editing without lag.
    // They will autosave on blur instead (see wireContactFieldBlur below).
    dFirst,dLast,dDob,dAge,dGender,dBtc,dLender,dLoanAmount,
    dStatus,dPriority,dLastTouch,dNextDate,dNextText,dTags,dNotes,
    dPipelineStage,dMeetingTime,dMeetingDuration,dMeetingLocation,dZoomJoinUrl,
    dUsePersonalZoomLink,dWaitingOn,dPinnedBrief,
    dDocIdReceived,dDocAppSent,dDocAppSigned,dDocPolicyDelivered,dDocReviewBooked,
    dWatchers,dMentionNote
  ];
  autosaveFields.forEach(el => {
    if (!el) return;
    if (el.type === "checkbox"){
      el.addEventListener("change", () => queueQuickViewAutosave());
    }else{
      el.addEventListener("input", () => {
        if (el === dNotes) refreshLeadOverviewSummary();
        queueQuickViewAutosave();
      });
      el.addEventListener("change", () => {
        if (el === dNotes) refreshLeadOverviewSummary();
        queueQuickViewAutosave();
      });
    }
  });

  // Contact fields (email, phone, address) save on blur for clean editing without lag
  const contactFields = [dFirst, dLast, dEmailInput, dPhoneInput, dPhone2Input, dAddress, dCity, dState, dCounty, dZip];
  contactFields.forEach(el => {
    if (!el) return;
    el.addEventListener("blur", () => {
      if (activeClientId) queueQuickViewAutosave("Saving…");
    });
  });

  // Auto-calculate age when DOB changes
  if (dDob) {
    dDob.addEventListener("change", () => {
      const calculatedAge = calculateAgeFromDOB(dDob.value);
      if (calculatedAge && dAge) {
        dAge.value = calculatedAge;
        queueQuickViewAutosave("Age updated…");
      }
    });
  }
}

const clientSearchForm = $("#clientSearchForm");
const clientSearchInput = $("#clientSearchInput");
const clientSearchWarning = $("#clientSearchWarning");

function refreshStateFilterOptions(){
  if (!stateFilter) return;
  const current = normalizeStateOption(stateFilter.value || "");
  const states = Array.from(new Set(
    rows
      .map(r => normalizeStateOption(r.dataset.state || ""))
      .filter(Boolean)
  )).sort();
  const options = ['<option value=\"\">State</option>']
    .concat(states.map(s => `<option value=\"${s}\">${s === "STATE" ? "No State" : s}</option>`));
  stateFilter.innerHTML = options.join("");
  if (states.includes(current)) stateFilter.value = current;
  else stateFilter.value = "";
}

function focusClientByQuery(query){
  const term = norm(query).toLowerCase();
  if (!term) return false;
  const phoneTerm = term.replace(/\D/g, "");

  const match = rows.find(r => {
    const name = fullName(r).toLowerCase();
    const email = norm(r.dataset.email).toLowerCase();
    const phone = norm(r.dataset.phone).replace(/\D/g, "");
    return name.includes(term) || email.includes(term) || (phoneTerm && phone.includes(phoneTerm));
  });

  if (!match) return false;

  const stage = normalizePipelineStageValue(match.dataset.sPipeline || match.dataset.crmPipeline, "");
  pipelineFocusStage = stage;
  pipelineNavSelectedStage = stage;
  if (stageFilter) stageFilter.value = stage || "";
  if (viewMode) viewMode.value = "pipeline";
  applyViewMode();
  renderAll();

  setTimeout(() => {
    const live = document.querySelector(`[data-client-id="${match.dataset.clientId}"]`);
    if (live){
      live.scrollIntoView({ behavior: "smooth", block: "center" });
      live.classList.add("row-flash");
      setTimeout(() => live.classList.remove("row-flash"), 1600);
      openDrawerForRow(live);
    }
  }, 40);

  return true;
}

function setDrawerNextActionDate(value){
  const safeValue = value || "";
  if (dNextDate) dNextDate.value = safeValue;
  if (dMeetingNextDate) dMeetingNextDate.value = safeValue;
}

/* ========= Sticky header height (real, no guesswork) ========= */
function syncBarHeight(){
  if (!bar) return;
  const h = Math.ceil(bar.getBoundingClientRect().height) + 2;
  document.documentElement.style.setProperty("--barH", h + "px");
}
window.addEventListener("resize", () => {
  clearTimeout(window.__barT);
  window.__barT = setTimeout(syncBarHeight, 80);
});

/* ========= KEBAB MENUS (delegated) ========= */
function closeAllMenus(exceptMenu){
  $$(".menu.open").forEach(m => { if (m !== exceptMenu) m.classList.remove("open"); });
  $$("[data-kebab][aria-expanded='true']").forEach(b => b.setAttribute("aria-expanded","false"));
}
function toggleMenu(btn){
  const cell = btn.closest(".actions");
  if (!cell) return;
  const menu = $(".menu", cell);
  if (!menu) return;

  const isOpen = menu.classList.contains("open");
  closeAllMenus(menu);

  if (!isOpen){
    menu.classList.add("open");
    btn.setAttribute("aria-expanded","true");
  }else{
    menu.classList.remove("open");
    btn.setAttribute("aria-expanded","false");
  }
}

/* ========= Classifiers ========= */
function classifyBadge(status, row){
  const isGuid = (row.dataset.isguid === "true");
  const email = norm(row.dataset.email);
  const phone = norm(row.dataset.phone);
  const missing = (!email && !phone);

  if (!isGuid) return "bad";
  if (missing) return "warn";
  if (status === "Dormant" || status === "Lead" || status === "Prospect") return "warn";
  return "good";
}

function pipelineBadgeClass(stage){
  return pipelineClass(stage);
}

function needsAttention(row){
  const isGuid = (row.dataset.isguid === "true");
  const email = norm(row.dataset.email);
  const phone = norm(row.dataset.phone);
  const status = norm(row.dataset.crmStatus);
  const nextDate = norm(row.dataset.crmNextDate);

  if (!isGuid) return true;
  if (!email && !phone) return true;
  if (status === "Dormant" || status === "Lead" || status === "Prospect") return true;
  if (isOverdue(nextDate)) return true;
  return false;
}

/* ========= Hydrate ========= */
function hydrateRow(row){
  const status    = row.dataset.sStatus    || "Lead";
  const lastTouch = row.dataset.sLasttouch || "";
  const tags      = row.dataset.sTags      || "";
  const notes     = row.dataset.sNotes     || "";
  const nextDate  = row.dataset.sNextdate  || "";
  const nextText  = row.dataset.sNexttext  || "";
  const priority  = row.dataset.sPriority  || "Normal";
  const pipeline  = normalizePipelineStageValue(row.dataset.sPipeline, "MortgageProtection");
  const waitingOn = row.dataset.sWaiting   || "WaitingOnAgent";
  const pinnedBrief = row.dataset.sPinnedbrief || "";
  const stageEntered = row.dataset.sStageentered || todayISO();
  const attemptsToday = row.dataset.sAttemptstoday || "0";
  const attemptsWeek = row.dataset.sAttemptsweek || "0";
  const attemptsMonth = row.dataset.sAttemptsmonth || "0";
  const attemptsYear = row.dataset.sAttemptsyear || "0";
  const attemptsLife = row.dataset.sAttemptslife || "0";
  const lastChannel = row.dataset.sChannel || "";
  const docCount = row.dataset.sDoccount || "0";
  const owner = row.dataset.sOwner || "";
  const watchers = row.dataset.sWatchers || "";
  const dupeEmail = row.dataset.sDupeEmail || "false";
  const dupePhone = row.dataset.sDupePhone || "false";
  const dupeHousehold = row.dataset.sDupeHousehold || "false";
  const prodStatus = row.dataset.prodStatus || "";
  const prodAmount = Number(row.dataset.prodAmount || 0);

  row.dataset.crmStatus    = status;
  row.dataset.crmLastTouch = lastTouch;
  row.dataset.crmTags      = tags;
  row.dataset.crmNotes     = notes;
  row.dataset.crmNextDate  = nextDate;
  row.dataset.crmNextText  = nextText;
  row.dataset.crmPriority  = priority;
  row.dataset.crmPipeline  = pipeline;
  row.dataset.crmWaitingOn = waitingOn;
  row.dataset.crmPinnedBrief = pinnedBrief;
  row.dataset.crmStageEntered = stageEntered;
  row.dataset.crmAttemptsToday = attemptsToday;
  row.dataset.crmAttemptsWeek = attemptsWeek;
  row.dataset.crmAttemptsMonth = attemptsMonth;
  row.dataset.crmAttemptsYear = attemptsYear;
  row.dataset.crmAttemptsLife = attemptsLife;
  row.dataset.crmLastChannel = lastChannel;
  row.dataset.crmDocCount = docCount;
  row.dataset.crmOwner = owner;
  row.dataset.crmWatchers = watchers;
  row.dataset.crmDupeEmail = dupeEmail;
  row.dataset.crmDupePhone = dupePhone;
  row.dataset.crmDupeHousehold = dupeHousehold;
  row.dataset.prodStatus = prodStatus;
  row.dataset.prodAmount = prodAmount;

  const badge = $("[data-badge]", row);
  const statusText = $("[data-status-text]", row);
  const pipelineBadge = $("[data-pipeline-badge]", row);
  const pipelineText = $("[data-pipeline-text]", row);
  const lastTouchEl = $("[data-lasttouch]", row);
  const nextTextEl = $("[data-nextaction]", row);
  const nextPill = $("[data-nextpill]", row);
  const waitingTextEl = $("[data-waiting-text]", row);
  const stageAgeEl = $("[data-stageage]", row);

  syncRowEmailDisplays(row);

  if (statusText) statusText.textContent = crmStatusLabel(status);
  if (waitingTextEl) waitingTextEl.textContent = waitingLabel(waitingOn);
  if (stageAgeEl) stageAgeEl.textContent = `Stage Age: ${stageAgeDays(row)}d`;

  const cls = classifyBadge(status, row);
  if (badge){
    badge.classList.remove("good","warn","bad");
    badge.classList.add(cls);
  }

  if (pipelineText) pipelineText.textContent = pipelineLabel(pipeline);
  row.classList.remove(
    "stage-newlead","stage-contacted","stage-qualified","stage-client","stage-meetingscheduled",
    "stage-proposalsent","stage-applicationstarted","stage-submitted",
    "stage-closedwon","stage-closedlost","stage-nurture"
  );
  row.classList.add(pipelineBadgeClass(pipeline));
  if (pipelineBadge){
    pipelineBadge.classList.remove(
      "stage-newlead","stage-contacted","stage-qualified","stage-client","stage-meetingscheduled",
      "stage-proposalsent","stage-applicationstarted","stage-submitted",
      "stage-closedwon","stage-closedlost","stage-nurture"
    );
    pipelineBadge.classList.add(pipelineBadgeClass(pipeline));
  }

  if (lastTouchEl) lastTouchEl.textContent = lastTouch ? lastTouch : "—";

  if (nextPill && nextTextEl){
    nextPill.classList.remove("overdue","today","soon","none");
    let label = "—";
    let pillCls = "none";

    if (!nextDate && !nextText){
      label = "No next action";
      pillCls = "none";
    } else {
      const datePart = nextDate || "No date";
      const actPart = nextText || "Next action";
      label = `${datePart} • ${actPart}`;

      if (isOverdue(nextDate)) pillCls = "overdue";
      else if (isToday(nextDate)) pillCls = "today";
      else if (isSoon(nextDate)) pillCls = "soon";
      else pillCls = "";
    }

    if (pillCls) nextPill.classList.add(pillCls);
    nextTextEl.textContent = label;

    const flag = $(".flag", nextPill);
    if (flag){
      flag.textContent =
        pillCls === "overdue" ? "⛔" :
        pillCls === "today"   ? "⚠️" :
        pillCls === "soon"    ? "⏳" : "—";
    }
    nextPill.title =
      pillCls === "overdue" ? "Overdue" :
      pillCls === "today" ? "Due Today" :
      pillCls === "soon" ? "Due Soon" :
      (!nextDate && !nextText) ? "Set Next Action in Quick View" : "Next Action";
  }

  setLeadProduction(row, prodStatus, prodAmount);
}

function setLeadProduction(row, status, amount, totals){
  const badge = $("[data-prod-card]", row);
  const cleanStatus = (status || "").trim();
  const amt = Number(amount || 0);
  const resolved = resolveProductionTotals(cleanStatus, amt, {
    paid: totals?.paid ?? row.dataset.prodPaid ?? row.dataset.paid,
    issued: totals?.issued ?? row.dataset.prodIssued,
    submitted: totals?.submitted ?? row.dataset.prodSubmitted
  });
  const paid = resolved.paid;
  const issued = resolved.issued;
  const submitted = resolved.submitted;
  const hasAny = paid > 0 || issued > 0 || submitted > 0;

  row.dataset.prodPaid = Number.isFinite(paid) ? `${paid}` : "0";
  row.dataset.prodIssued = Number.isFinite(issued) ? `${issued}` : "0";
  row.dataset.prodSubmitted = Number.isFinite(submitted) ? `${submitted}` : "0";
  row.dataset.paid = row.dataset.prodPaid;
  row.dataset.prodStatus = (paid > 0 ? "Paid" : cleanStatus);
  row.dataset.prodAmount = paid > 0 ? paid : amt;

  if (!badge) return;
  if (hasAny){
    badge.innerHTML = renderPipelineProdBadge({ paid, issued, submitted });
    badge.classList.remove("hidden");
  } else {
    badge.textContent = "";
    badge.classList.add("hidden");
  }
}

function setLeadProductionById(leadId, status, amount, totals){
  const row = rows.find(r => r.dataset.clientId === leadId);
  if (row) setLeadProduction(row, status, amount, totals);
  updatePipelineCardProduction(leadId);
}

function productionBucket(rawStatus){
  const s = norm(rawStatus).toLowerCase();
  if (!s) return "";
  if (s === "2" || s.includes("paid")) return "paid";
  if (s === "1" || s.includes("issued")) return "issued";
  if (s === "0" || s.includes("submitted")) return "submitted";
  return "";
}

function resolveProductionTotals(status, amount, seed = {}){
  let paid = Number(seed.paid ?? 0);
  let issued = Number(seed.issued ?? 0);
  let submitted = Number(seed.submitted ?? 0);
  if (!Number.isFinite(paid)) paid = 0;
  if (!Number.isFinite(issued)) issued = 0;
  if (!Number.isFinite(submitted)) submitted = 0;

  if (paid <= 0 && issued <= 0 && submitted <= 0){
    const amt = Number(amount || 0);
    if (amt > 0){
      const bucket = productionBucket(status);
      if (bucket === "paid") paid = amt;
      else if (bucket === "issued") issued = amt;
      else if (bucket === "submitted") submitted = amt;
    }
  }

  return { paid, issued, submitted };
}

function renderPipelineProdBadge({ paid = 0, issued = 0, submitted = 0 } = {}){
  const paidAmt = Number(paid || 0);
  const issuedAmt = Number(issued || 0);
  const submittedAmt = Number(submitted || 0);
  if (paidAmt <= 0 && issuedAmt <= 0 && submittedAmt <= 0) return "";

  return `
    <div class="prod-line prod-line-paid"><span class="prod-lbl">Paid:</span><span class="prod-val">${formatCurrency(paidAmt)}</span></div>
    <div class="prod-line prod-line-issued"><span class="prod-lbl">Issued:</span><span class="prod-val">${formatCurrency(issuedAmt)}</span></div>
    <div class="prod-line prod-line-submitted"><span class="prod-lbl">Submitted:</span><span class="prod-val">${formatCurrency(submittedAmt)}</span></div>
  `;
}

function updatePipelineCardProduction(leadId){
  if (!leadId || !pipelineBoard) return;
  const card = pipelineBoard.querySelector(`[data-cardid="${CSS.escape(leadId)}"]`);
  const row = rows.find(r => r.dataset.clientId === leadId);
  if (!card || !row) return;
  const badge = card.querySelector("[data-prod-card]");
  if (!badge) return;

  const paid = Number(row.dataset.prodPaid || row.dataset.paid || 0);
  const issued = Number(row.dataset.prodIssued || 0);
  const submitted = Number(row.dataset.prodSubmitted || 0);
  const html = renderPipelineProdBadge({ paid, issued, submitted });
  if (html){
    badge.innerHTML = html;
    badge.classList.remove("hidden");
  } else {
    badge.textContent = "";
    badge.classList.add("hidden");
  }
}

// ===== Production tiles refresh =====
const tileSubmittedTotal = $("#tileSubmittedTotal");
const tileIssuedTotal = $("#tileIssuedTotal");
const tilePaidTotal = $("#tilePaidTotal");
const tilePersonalTotal = $("#tilePersonalTotal");
const tileSubmittedCount = $("#tileSubmittedCount");
const tileIssuedCount = $("#tileIssuedCount");
const tilePaidCount = $("#tilePaidCount");
const tilePersonalCount = $("#tilePersonalCount");

async function refreshLeadProductionTiles(){
  try{
    const res = await fetch("/production/summary/leads", { credentials: "include" });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const data = await res.json();
    const fmt = (v) => Number(v || 0).toLocaleString("en-US", { style:"currency", currency:"USD", maximumFractionDigits:0 });
    if (tileSubmittedTotal) tileSubmittedTotal.textContent = fmt(data.submitted);
    if (tileIssuedTotal) tileIssuedTotal.textContent = fmt(data.issued);
    if (tilePaidTotal) tilePaidTotal.textContent = fmt(data.paid);
    if (tilePersonalTotal) tilePersonalTotal.textContent = fmt(data.personal);
    if (tileSubmittedCount) tileSubmittedCount.textContent = data.countSubmitted ?? 0;
    if (tileIssuedCount) tileIssuedCount.textContent = data.countIssued ?? 0;
    if (tilePaidCount) tilePaidCount.textContent = data.countPaid ?? 0;
    if (tilePersonalCount) tilePersonalCount.textContent = data.countPersonal ?? 0;
  }catch(err){
    console.warn("Production tile refresh failed", err);
  }
}

function updateCallMetrics(){
  if (!cmToday || !cmWeek || !cmMonth) return;

  let day = 0, week = 0, month = 0, year = 0;
  metricRows().forEach(r => {
    day   += parseInt(r.dataset.crmAttemptsToday  || r.dataset.sAttemptstoday  || "0", 10) || 0;
    week  += parseInt(r.dataset.crmAttemptsWeek   || r.dataset.sAttemptsweek   || "0", 10) || 0;
    month += parseInt(r.dataset.crmAttemptsMonth  || r.dataset.sAttemptsmonth  || "0", 10) || 0;
    year  += parseInt(r.dataset.crmAttemptsYear   || r.dataset.sAttemptsyear   || "0", 10) || 0;
  });

  cmToday.textContent = day.toLocaleString();
  cmWeek.textContent = week.toLocaleString();
  cmMonth.textContent = month.toLocaleString();
  if (cmYearBtn) cmYearBtn.textContent = year.toLocaleString();
}
rows.forEach(hydrateRow);
refreshStateFilterOptions();
updateCallMetrics();
ensureDialPeriodsFresh();
__timers.dialFresh = setInterval(ensureDialPeriodsFresh, 5 * 60 * 1000);

btnDialYear?.addEventListener("click", () => {
  const today = cmToday?.textContent || "0";
  const week = cmWeek?.textContent || "0";
  const month = cmMonth?.textContent || "0";
  const year = cmYearBtn?.textContent || "0";
  toast(`Dial Tracker — Today: ${today} • Week: ${week} • Month: ${month} • YTD: ${year}`, { persistent: true });
});

/* ========= Selection ========= */
function getVisibleRows(){ return rows.filter(r => r.style.display !== "none"); }
function getCheckedRows(){ return rows.filter(r => $(".row-chk", r)?.checked); }

function selectFirstNInBucket(bucket, n){
  let picked = 0;
  rows.forEach(r => {
    if (picked >= n) return;
    if (!matchesStageSelection(r, bucket)) return;
    const c = $(".row-chk", r);
    if (c && !c.checked){
      c.checked = true;
      picked++;
    }
  });
  updateSelectionUI();
  return picked;
}

function updateSelectionUI(){
  const checked = getCheckedRows();
  const count = checked.length;

  selCount.textContent = String(count);
  btnCopyEmails.disabled = count === 0;
  btnClearSel.disabled = count === 0;
  btnOpenFirst.disabled = count === 0;
  if (btnBulkEdit) btnBulkEdit.disabled = count === 0;
  // keep bulk delete clickable; handler will guard zero-selection
  if (btnBulkDelete) btnBulkDelete.disabled = false;
  if (btnDeleteBucket) btnDeleteBucket.disabled = false;

  rows.forEach(r => r.classList.toggle("row-selected", !!$(".row-chk", r)?.checked));
}

chkAll?.addEventListener("change", () => {
  getVisibleRows().forEach(r => {
    const c = $(".row-chk", r);
    if (c) c.checked = chkAll.checked;
  });
  updateSelectionUI();
});

btnClearSel?.addEventListener("click", () => {
  if (chkAll) chkAll.checked = false;
  rows.forEach(r => { const c = $(".row-chk", r); if (c) c.checked = false; });
  updateSelectionUI();
});

btnCopyEmails?.addEventListener("click", () => {
  const emails = getCheckedRows().map(r => norm(r.dataset.email)).filter(Boolean);
  if (emails.length === 0) return toast("No emails selected");
  copyText(emails.join(", "));
});

btnImportLeads?.addEventListener("click", () => {
  pendingImportBucket = importBucketSelect?.value || "MortgageProtection";
  if (importBucketSelect) importBucketSelect.value = pendingImportBucket;
  if (importResult) importResult.textContent = `Target bucket: ${pipelineLabel(pendingImportBucket)}`;
  updateImportCsvHelp(pendingImportBucket);
  if (importFile) importFile.value = "";
  openModal(importModal);
});

importBucketSelect?.addEventListener("change", () => {
  const selected = importBucketSelect.value || "MortgageProtection";
  pendingImportBucket = selected;
  if (importResult) importResult.textContent = `Target bucket: ${pipelineLabel(selected)}`;
  updateImportCsvHelp(selected);
});

async function bulkDeleteSelected(){
  let selectedRows = getCheckedRows();
  if (!selectedRows.length){
    const bucket = norm(stageFilter?.value);
    if (bucket){
      selectFirstNInBucket(bucket, 100);
    }else{
      // no bucket filter—select first 100 visible rows
      getVisibleRows().slice(0, 100).forEach(r => {
        const c = $(".row-chk", r);
        if (c) c.checked = true;
      });
      updateSelectionUI();
    }
    selectedRows = getCheckedRows();
  }
  const ids = selectedRows.map(r => r.dataset.clientId).filter(Boolean);
  if (!ids.length){
    toast("No leads available to delete.");
    return;
  }
  const confirmMsg = ids.length === 1
    ? "Delete this lead? This cannot be undone."
    : `Delete ${ids.length} leads? This cannot be undone.`;
  if (!window.confirm(confirmMsg)) return;
  try{
    await postJson("/Leads/DeleteBulk", ids);
    ids.forEach(id => {
      const row = rows.find(r => r.dataset.clientId === id);
      if (row) row.remove();
      if (liveSync) liveSync.sendDelete(id);
    });
    toast(`Deleted ${ids.length} lead${ids.length === 1 ? "" : "s"}.`);
    renderAll();
  }catch(err){
    console.error(err);
    toast(err?.message || "Bulk delete failed.", { error: true, persistent: true });
  }
}

btnBulkDelete?.addEventListener("click", bulkDeleteSelected);

// Live sync listeners (no-op if SignalR not connected)
if (liveSync){
  liveSync.onDelete((leadId) => {
    const row = rows.find(r => r.dataset.clientId === leadId);
    if (row){
      row.remove();
      renderAllDebounced();
    }
  });

  liveSync.onCall(async (leadId, callCount) => {
    const row = rows.find(r => r.dataset.clientId === leadId);
    if (!row) return;
    try{
      const res = await fetch(`/Leads/Lead?id=${encodeURIComponent(leadId)}`, withDialHeaders({ credentials: "include" }));
      if (res.ok){
        const payload = await res.json().catch(() => ({}));
        row.dataset.sAttemptstoday = String(payload.attemptsToday ?? row.dataset.sAttemptstoday ?? 0);
        row.dataset.sAttemptsweek = String(payload.attemptsThisWeek ?? row.dataset.sAttemptsweek ?? 0);
        row.dataset.sAttemptsmonth = String(payload.attemptsThisMonth ?? row.dataset.sAttemptsmonth ?? 0);
        row.dataset.sAttemptsyear = String(payload.attemptsThisYear ?? row.dataset.sAttemptsyear ?? 0);
        row.dataset.sAttemptslife = String(payload.attemptsLifetime ?? payload.callCount ?? row.dataset.sAttemptslife ?? 0);
        hydrateRow(row);
        syncAttemptSummary(row);
        renderAllDebounced();
        return;
      }
    }catch{}

    const val = Number(callCount);
    if (Number.isFinite(val)){
      row.dataset.sAttemptslife = String(val);
      hydrateRow(row);
      syncAttemptSummary(row);
      renderAllDebounced();
    }
  });

  liveSync.onPage((pageKey, pageNumber) => {
    if (pageKey !== "leads") return;
    const num = parseInt(pageNumber, 10);
    if (!Number.isFinite(num) || num < 1) return;
    currentPage = num;
    renderAllDebounced();
  });

  liveSync.onReorder((stageKey, orderedIds = []) => {
    if (!stageKey || !Array.isArray(orderedIds)) return;
    pipelineOrder[stageKey] = orderedIds.slice();
    renderAllDebounced();
  });

  liveSync.onUpdate((payload = {}) => {
    const leadId = payload.leadId;
    if (!leadId) return;
    const row = rows.find(r => r.dataset.clientId === leadId);
    if (!row) return;
    if (payload.pipelineStage) row.dataset.sPipeline = normalizePipelineStageValue(payload.pipelineStage, row.dataset.sPipeline || "MortgageProtection");
    if (payload.crmNextDate !== undefined) row.dataset.sNextdate = payload.crmNextDate || "";
    if (payload.crmNextText !== undefined) row.dataset.sNexttext = payload.crmNextText || "";
    if (payload.crmStatus) row.dataset.sStatus = payload.crmStatus;
    if (payload.crmPriority) row.dataset.sPriority = payload.crmPriority;
    if (payload.attemptsToday !== undefined) row.dataset.sAttemptstoday = String(payload.attemptsToday ?? 0);
    if (payload.attemptsThisWeek !== undefined) row.dataset.sAttemptsweek = String(payload.attemptsThisWeek ?? 0);
    if (payload.attemptsThisMonth !== undefined) row.dataset.sAttemptsmonth = String(payload.attemptsThisMonth ?? 0);
    if (payload.attemptsThisYear !== undefined) row.dataset.sAttemptsyear = String(payload.attemptsThisYear ?? 0);
    if (payload.attemptsLifetime !== undefined) row.dataset.sAttemptslife = String(payload.attemptsLifetime ?? 0);
    if (payload.firstName !== undefined) row.dataset.first = payload.firstName || row.dataset.first;
    if (payload.lastName !== undefined) row.dataset.last = payload.lastName || row.dataset.last;
    if (payload.phone !== undefined) row.dataset.phone = payload.phone || row.dataset.phone;
    if (payload.email !== undefined) row.dataset.email = payload.email || row.dataset.email;
    if (payload.originalLeadType !== undefined) row.dataset.originalLeadType = normalizeOriginalLeadTypeValue(payload.originalLeadType) || row.dataset.originalLeadType || "";
    hydrateRow(row);
    renderAllDebounced();
  });
}

function inferCurrentBucket(){
  const fromFilter = norm(stageFilter?.value);
  if (fromFilter) return fromFilter;
  const fromFocus = norm(pipelineFocusStage || pipelineNavSelectedStage);
  if (fromFocus) return fromFocus;
  const firstVisible = getVisibleRows().find(r => norm(r.dataset.crmPipeline));
  return norm(firstVisible?.dataset.crmPipeline);
}

async function deleteCurrentBucket(){
  const bucket = inferCurrentBucket();
  if (!bucket){
    toast("No bucket detected. Pick a bucket or filter first.");
    return;
  }
  const bucketLabel = pipelineLabel(bucket);
  const message = `Delete the most recent 100 lead(s) in “${bucketLabel}”? This cannot be undone.`;
  if (!window.confirm(message)) return;
  try{
    const res = await postJson("/Leads/DeleteBucket", { bucket, batchSize: 100 });
    const remaining = res.remaining ?? 0;
    toast(`Deleted ${res.deleted || 0} (latest) from “${bucketLabel}”. ${remaining} remaining. Reloading...`, { persistent: true });
    setTimeout(() => window.location.reload(), 1200);
  }catch(err){
    console.error(err);
    toast(err?.message || "Bucket delete failed.", { error: true, persistent: true });
  }
}

btnDeleteBucket?.addEventListener("click", deleteCurrentBucket);

/* ========= Delegated Clicks (fast, minimal listeners) ========= */
document.addEventListener("click", (e) => {
  if (!e.target.closest(".actions")) closeAllMenus(null);
  if (!e.target.closest("#textMenuGlobal")) closeAllTextMenus(null);
  if (!e.target.closest("#leadCommAuthGlobal")) closeLeadCommAuth();

  const kebab = e.target.closest("[data-kebab]");
  if (kebab){
    e.preventDefault();
    return toggleMenu(kebab);
  }

  if (e.target.matches("[data-closemodal]")){
    e.preventDefault();
    return closeModal();
  }

  const directCallLink = e.target.closest("#tableView a.phone-link[href^='tel:'], #pipelineBoard a[data-call-link][href^='tel:']");
  if (directCallLink){
    const row = directCallLink.closest(".client-row")
      || rows.find(r => r.dataset.clientId === directCallLink.getAttribute("data-call-link"));
    const phone = directCallLink.getAttribute("data-call-phone")
      || (directCallLink.getAttribute("href") || "").replace(/^tel:/i, "");
    if (row && phone){
      e.preventDefault();
      incrementCallLead(row).then(() => {
        window.location.href = `tel:${encodeURIComponent(phone)}`;
      });
      return;
    }
  }

  if (e.target === drawerBackdrop) closeDrawer();
  if (e.target === modalBackdrop) closeModal();

  const openDrawerEl = e.target.closest(".open-drawer");
  if (openDrawerEl){
    const row = openDrawerEl.closest(".client-row");
    if (row) openDrawerForRow(row);
    else {
      const id = openDrawerEl.closest("[data-cardid]")?.getAttribute("data-cardid");
      if (id) openDrawerById(id);
    }
    return;
  }

  const openRowEl = e.target.closest("[data-open-row]");
  if (openRowEl && !e.target.closest("a, button, input, select, textarea, label")){
    const row = openRowEl.closest(".client-row");
    if (row) openDrawerForRow(row);
    return;
  }

  const openCardId = e.target.closest("[data-open-card]")?.getAttribute("data-open-card");
  if (openCardId){
    const row = rows.find(r => r.dataset.clientId === openCardId);
    if (row) openDrawerForRow(row);
    else openDrawerById(openCardId);
    return;
  }

  if (e.target.classList.contains("row-chk")){
    updateSelectionUI();
    return;
  }

  const queueBtn = e.target.closest("[data-queue]");
  if (queueBtn){
    const queue = queueBtn.getAttribute("data-queue");
    if (queue){
      window.location.href = `/Leads/Queue?queue=${encodeURIComponent(queue)}`;
    }
    return;
  }

  const savedViewBtn = e.target.closest("[data-savedview]");
  if (savedViewBtn){
    applySavedView(parseInt(savedViewBtn.getAttribute("data-savedview") || "-1", 10));
    return;
  }

  const presetBtn = e.target.closest("[data-preset]");
  if (presetBtn){
    applyPreset(presetBtn.getAttribute("data-preset") || "");
    return;
  }

  const actionBtn = e.target.closest("[data-action]");
  if (actionBtn){
    const row = actionBtn.closest(".client-row");
    if (!row) return;

    const action = actionBtn.getAttribute("data-action");

    if (action === "copy-email"){
      const email = norm(row.dataset.email);
      return email ? copyText(email) : toast("No email");
    }
    if (action === "copy-phone"){
      const phone = norm(row.dataset.phone);
      return phone ? copyText(phone) : toast("No phone");
    }
    if (action === "copy-contact"){
      const name = fullName(row);
      const email = norm(row.dataset.email);
      const phone = norm(row.dataset.phone);
      return copyText(`${name}\n${email}\n${phone}`.trim());
    }
    if (action === "log-call"){
      openDrawerForRow(row);
      dActType.value = "Call";
      dActDate.value = todayISO();
      dActNote.focus();
      return;
    }
    if (action === "set-next"){
      openDrawerForRow(row);
      setDrawerNextActionDate(todayISO());
      dNextText.focus();
      return;
    }
  }

  const deleteClientId = e.target.closest("[data-delete-client]")?.getAttribute("data-delete-client");
  if (deleteClientId){
    if (LEADS_ONLY){
      toast("Client delete is disabled in Leads CRM.");
      return;
    }
    if (!confirm("Delete this client? This will remove the profile + household + Entra login for this client.")) return;

    const f = document.getElementById("__af");
    if (!f) return toast("Missing antiforgery form.");

    f.setAttribute("action", "/Clients/Delete");
    f.querySelectorAll("input[name='clientUserId']").forEach(x => x.remove());

    const inp = document.createElement("input");
    inp.type = "hidden";
    inp.name = "clientUserId";
    inp.value = deleteClientId;
    f.appendChild(inp);

    f.submit();
    return;
  }
});

// Hard-bind basic open-drawer clicks so delegation issues can't block Quick View
$$(".open-drawer").forEach(btn => {
  if (btn.dataset.boundDirect) return;
  btn.dataset.boundDirect = "1";
  btn.addEventListener("click", (e) => {
    e.preventDefault();
    const row = btn.closest(".client-row");
    if (row) openDrawerForRow(row);
  });
});

$$("[data-open-row]").forEach(el => {
  if (el.dataset.boundDirect) return;
  el.dataset.boundDirect = "1";
  el.addEventListener("click", (e) => {
    if (e.target.closest("a, button, input, select, textarea, label")) return;
    const row = el.closest(".client-row");
    if (row) openDrawerForRow(row);
  });
});

/* ========= Export CSV ========= */
function csvEscape(v){
  const s = (v ?? "").toString();
  if (s.includes('"') || s.includes(",") || s.includes("\n")) return `"${s.replaceAll('"','""')}"`;
  return s;
}
btnExportCsv?.addEventListener("click", () => {
  const visible = getVisibleRows();
  const header = ["FirstName","LastName","Email","Phone","ClientUserId","PortalUrl","CRMStatus","PipelineStage","LastTouch","NextDate","NextAction","Priority","Tags","Notes"];
  const lines = [header.join(",")];

  visible.forEach(r => {
    const row = [
      norm(r.dataset.first),
      norm(r.dataset.last),
      norm(r.dataset.email),
      norm(r.dataset.phone),
      norm(r.dataset.clientId),
      norm(r.dataset.clienturl),
      norm(r.dataset.crmStatus),
      norm(r.dataset.crmLastTouch),
      norm(r.dataset.crmNextDate),
      norm(r.dataset.crmNextText),
      norm(r.dataset.crmPriority),
      pipelineLabel(norm(r.dataset.crmPipeline)),
      norm(r.dataset.crmTags),
      norm(r.dataset.crmNotes)
    ].map(csvEscape);
    lines.push(row.join(","));
  });

  const blob = new Blob([lines.join("\n")], { type: "text/csv;charset=utf-8;" });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = `legend_clients_${todayISO()}.csv`;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
  toast("Exported CSV");
});

/* ========= Filters / Sort / Pagination ========= */
let currentPage = 1;

function touchedWithinDays(row, days){
  const lt = norm(row.dataset.crmLastTouch);
  if (!lt) return false;
  const diff = daysDiff(lt);
  return diff !== null && diff >= 0 && diff <= days;
}
function missingContact(row){
  const email = norm(row.dataset.email);
  const phone = norm(row.dataset.phone);
  return (!email && !phone);
}

function hasNextAction(row){
  return !!(norm(row.dataset.crmNextDate) || norm(row.dataset.crmNextText));
}

function noLastTouch(row){
  return !norm(row.dataset.crmLastTouch);
}

function waitingOn(row, key){
  return norm(row.dataset.crmWaitingOn) === key;
}

const HIGH_PRIORITY_KEYS = new Set(["high", "urgent"]);
const LEADS_MEETING_BUCKET_KEYS = new Set(["booked"]);

function rowIdentity(row){
  return norm(row.dataset.clientId)
    || norm(row.dataset.leadId)
    || norm(row.dataset.clientProfileId)
    || [norm(row.dataset.first), norm(row.dataset.last), norm(row.dataset.email), norm(row.dataset.phone)].join("|");
}

function uniqueRows(sourceRows){
  const seen = new Set();
  return sourceRows.filter(row => {
    const key = rowIdentity(row);
    if (!key) return true;
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

function priorityKey(row){
  return norm(row.dataset.crmPriority).toLowerCase();
}

function pipelineKey(row){
  return norm(row.dataset.crmPipeline).toLowerCase();
}

function attemptsThisWeek(row){
  return parseInt(row.dataset.crmAttemptsWeek || "0", 10) || 0;
}

function docChecklistOpen(row){
  return (parseInt(row.dataset.crmDocCount || "0", 10) || 0) < 5;
}

function applySort(filtered){
  const sort = norm(sortBy.value) || "name_asc";
  const cmp = (x,y) => x < y ? -1 : x > y ? 1 : 0;

  filtered.sort((a,b) => {
    const an = fullName(a).toLowerCase();
    const bn = fullName(b).toLowerCase();
    const as = norm(a.dataset.crmStatus).toLowerCase();
    const bs = norm(b.dataset.crmStatus).toLowerCase();

    const na = norm(a.dataset.crmNextDate);
    const nb = norm(b.dataset.crmNextDate);

    const la = norm(a.dataset.crmLastTouch);
    const lb = norm(b.dataset.crmLastTouch);

    if (sort === "name_asc") return cmp(an,bn);
    if (sort === "name_desc") return -cmp(an,bn);
    if (sort === "status_asc") return cmp(as,bs);

    if (sort === "nextaction_asc"){
      const ax = na ? new Date(na).getTime() : Number.MAX_SAFE_INTEGER;
      const bx = nb ? new Date(nb).getTime() : Number.MAX_SAFE_INTEGER;
      return ax - bx;
    }

    if (sort === "lasttouch_desc"){
      const ax = la ? new Date(la).getTime() : 0;
      const bx = lb ? new Date(lb).getTime() : 0;
      return bx - ax;
    }

    return cmp(an,bn);
  });

  return filtered;
}

function computeFiltered(){
  const s = norm(statusFilter.value);
  const priority = norm(priorityFilter.value);
  const stage = norm(stageFilter.value);
  const state = normalizeStateOption(stateFilter?.value || "");
  const attn = norm(attentionFilter.value);

  let filtered = rows.slice();

  // My Day focus overrides other filters to show the active queue
  if (activeMyDayQueue){
    filtered = queueRows(activeMyDayQueue);
  }

  if (s) filtered = filtered.filter(r => norm(r.dataset.crmStatus) === s);
  if (priority) filtered = filtered.filter(r => norm(r.dataset.crmPriority) === priority);
  if (stage) filtered = filtered.filter(r => matchesStageSelection(r, stage));
  if (state) filtered = filtered.filter(r => normalizeStateOption(r.dataset.state || "") === state);

  if (attn === "needs") filtered = filtered.filter(needsAttention);
  if (attn === "callsnow") filtered = filtered.filter(r => HIGH_PRIORITY_KEYS.has(priorityKey(r)) && (isToday(norm(r.dataset.crmNextDate)) || isOverdue(norm(r.dataset.crmNextDate))));
  if (attn === "overdue") filtered = filtered.filter(r => isOverdue(norm(r.dataset.crmNextDate)));
  if (attn === "today") filtered = filtered.filter(r => isToday(norm(r.dataset.crmNextDate)));
  if (attn === "soon") filtered = filtered.filter(r => isSoon(norm(r.dataset.crmNextDate)));
  if (attn === "hasnext") filtered = filtered.filter(hasNextAction);
  if (attn === "nonext") filtered = filtered.filter(r => !hasNextAction(r));
  if (attn === "notouch") filtered = filtered.filter(noLastTouch);
  if (attn === "touched14") filtered = filtered.filter(r => touchedWithinDays(r, 14));
  if (attn === "missing") filtered = filtered.filter(missingContact);
  if (attn === "missingemail") filtered = filtered.filter(r => !norm(r.dataset.email));
  if (attn === "missingphone") filtered = filtered.filter(r => !norm(r.dataset.phone));
  if (attn === "broken") filtered = filtered.filter(r => r.dataset.isguid !== "true");
  if (attn === "meeting") filtered = filtered.filter(r => LEADS_MEETING_BUCKET_KEYS.has(pipelineKey(r)));
  if (attn === "zoom") filtered = filtered.filter(r => !!norm(r.dataset.sZoom));
  if (attn === "location") filtered = filtered.filter(r => !!norm(r.dataset.sMeetingLocation));
  if (attn === "waitingclient") filtered = filtered.filter(r => waitingOn(r, "WaitingOnClient"));
  if (attn === "waitingagent") filtered = filtered.filter(r => waitingOn(r, "WaitingOnAgent"));
  if (attn === "waitingcarrier") filtered = filtered.filter(r => waitingOn(r, "WaitingOnCarrier"));
  if (attn === "waitinguw") filtered = filtered.filter(r => waitingOn(r, "WaitingOnUnderwriting"));
  if (attn === "waitingdocs") filtered = filtered.filter(r => waitingOn(r, "WaitingOnDocs"));
  if (attn === "stalled") filtered = filtered.filter(r => stageAgeDays(r) >= 7);
  if (attn === "attempts3") filtered = filtered.filter(r => attemptsThisWeek(r) >= 3);
  if (attn === "duplicates") filtered = filtered.filter(hasDuplicateWarning);
  if (attn === "docsopen") filtered = filtered.filter(docChecklistOpen);
  if (attn === "rescue") filtered = filtered.filter(r => isOverdue(norm(r.dataset.crmNextDate)) || stageAgeDays(r) >= 10 || hasDuplicateWarning(r));
  if (attn === "appsinflight") filtered = filtered.filter(r => ["NeedsDocs", "PolicyPlaced"].includes(norm(r.dataset.crmPipeline)));

  return applySort(filtered);
}

function renderList(filtered){
  if (chkAll) chkAll.checked = false;
  rows.forEach(r => { const c = $(".row-chk", r); if (c) c.checked = false; });
  updateSelectionUI();

  const size = parseInt(pageSize.value || "20", 10);
  const max = Math.max(1, Math.ceil(filtered.length / size));
  currentPage = Math.min(currentPage, max);

  const start = (currentPage - 1) * size;
  const end = start + size;
  const pageRows = filtered.slice(start, end);

  rows.forEach(r => r.style.display = "none");
  pageRows.forEach(r => r.style.display = "");

  pageNow.textContent = String(currentPage);
  pageMax.textContent = String(max);

  const showingA = filtered.length === 0 ? 0 : (start + 1);
  const showingB = Math.min(end, filtered.length);
  pagerInfo.textContent = `${filtered.length} result(s) • Showing ${showingA}–${showingB}`;

  btnPrev.disabled = currentPage <= 1;
  btnNext.disabled = currentPage >= max;
}

function renderAll(){
  if (drawerEditing) return;
  refreshStateFilterOptions();
  const filtered = computeFiltered();
  renderList(filtered);
  renderCards(filtered);
  syncStagePickerUi();
  wireOpenHandlers();
  refreshKPIs();
  refreshMyDay();
  refreshRemindersUI();
  syncBarHeight();
}

function debounce(fn, ms){
  let t = null;
  return (...args) => {
    clearTimeout(t);
    t = setTimeout(() => fn(...args), ms);
  };
}
const renderAllDebounced = debounce(renderAll, 40);

btnPrev?.addEventListener("click", () => {
  currentPage = Math.max(1, currentPage - 1);
  if (liveSync) liveSync.sendPage("leads", currentPage);
  renderAll();
});
btnNext?.addEventListener("click", () => {
  currentPage = currentPage + 1;
  if (liveSync) liveSync.sendPage("leads", currentPage);
  renderAll();
});

[statusFilter, priorityFilter, stateFilter, stageFilter, attentionFilter, sortBy, pageSize].forEach(el => {
  el?.addEventListener("change", () => { currentPage = 1; renderAll(); });
});

stagePickerSelect?.addEventListener("change", () => {
  syncStagePickerUi(stagePickerSelect.value || "MortgageProtection");
});

if (clientSearchForm && clientSearchInput){
  clientSearchForm.addEventListener("submit", (e) => {
    e.preventDefault();
    const q = clientSearchInput.value || "";
    const ok = focusClientByQuery(q);
    if (clientSearchWarning){
      clientSearchWarning.textContent = ok ? "" : "No matching client found.";
    }
  });
  clientSearchInput.addEventListener("keydown", (e) => {
    if (e.key === "Enter"){
      e.preventDefault();
      const ok = focusClientByQuery(clientSearchInput.value || "");
      if (clientSearchWarning){
        clientSearchWarning.textContent = ok ? "" : "No matching client found.";
      }
    }
  });
}

/* ========= Density + View ========= */
function applyDensityClass(){
  if (!legendWrap) return;
  legendWrap.classList.remove("density-compact","density-comfort");
  legendWrap.classList.add(density.value === "compact" ? "density-compact" : "density-comfort");
}

function resetFilters(){
  if (statusFilter) statusFilter.value = "";
  if (priorityFilter) priorityFilter.value = "";
  if (stageFilter) stageFilter.value = "";
  if (attentionFilter) attentionFilter.value = "";
  if (sortBy) sortBy.value = "name_asc";
  pipelineFocusStage = "";
  pipelineNavSelectedStage = "";
  pipelineNavSearchTerm = "";
}

function applyPreset(name){
  resetFilters();

  if (name === "hotleads"){
    priorityFilter.value = "High";
    stageFilter.value = "MortgageProtection";
    attentionFilter.value = "needs";
    sortBy.value = "nextaction_asc";
  } else if (name === "followup"){
    attentionFilter.value = "overdue";
    sortBy.value = "nextaction_asc";
  } else if (name === "meetingstoday"){
    stageFilter.value = "Booked";
    attentionFilter.value = "today";
    sortBy.value = "nextaction_asc";
    pipelineFocusStage = "Booked";
    if (viewMode) viewMode.value = "pipeline";
    applyViewMode();
  } else if (name === "rescue"){
    attentionFilter.value = "rescue";
    sortBy.value = "nextaction_asc";
  } else if (name === "appsinflight"){
    attentionFilter.value = "appsinflight";
    sortBy.value = "lasttouch_desc";
  }

  currentPage = 1;
  renderAll();
}

function applyViewMode(){
  const mode = viewMode.value || "pipeline";
  const isPipeline = mode === "pipeline";
  const isTable = mode === "table";
  const isHybrid = mode === "hybrid";

  legendWrap?.classList.toggle("pipeline-mode", isPipeline);
  legendWrap?.classList.toggle("table-mode", isTable);
  legendWrap?.classList.toggle("hybrid-mode", isHybrid);

  if (tableView) tableView.style.display = isPipeline ? "none" : "block";
  if (cardsView) cardsView.style.display = isTable ? "none" : "block";
}

density?.addEventListener("change", () => {
  saveJSON(LS_PREFS, { ...loadJSON(LS_PREFS, {}), density: density.value });
  applyDensityClass();
});

viewMode?.addEventListener("change", () => {
  saveJSON(LS_PREFS, { ...loadJSON(LS_PREFS, {}), view: viewMode.value });
  applyViewMode();
  renderAll();
});

/* ========= KPIs ========= */
function refreshKPIs(){
  if (!kpiGrid) return;

  const sourceRows = uniqueRows(rows);

  const total = sourceRows.length;
  const broken = sourceRows.filter(r => r.dataset.isguid !== "true").length;
  const needs = sourceRows.filter(needsAttention).length;
  const touched = sourceRows.filter(r => touchedWithinDays(r, 14)).length;
  const overdue = sourceRows.filter(r => isOverdue(norm(r.dataset.crmNextDate))).length;
  const today = sourceRows.filter(r => isToday(norm(r.dataset.crmNextDate))).length;

  $("#kTotal").textContent = total;
  $("#kBroken").textContent = broken;
  $("#kNeeds").textContent = needs;
  $("#kTouched").textContent = touched;
  $("#kOverdue").textContent = overdue;
  $("#kToday").textContent = today;

  kpiGrid.style.display = "";
}

function queueDate(row){
  return norm(row.dataset.crmNextDate);
}

function hasScheduledNextDate(row){
  return !!queueDate(row);
}

function isCallsNowRow(row){
  const qd = queueDate(row);
  if (!qd) return false;
  return HIGH_PRIORITY_KEYS.has(priorityKey(row)) && (isToday(qd) || isOverdue(qd));
}

function queueRowsLocal(type){
  const sourceRows = uniqueRows(rows);

  if (type === "callsnow") return sourceRows.filter(isCallsNowRow);
  if (type === "today") return sourceRows.filter(r => {
    const qd = queueDate(r);
    return hasScheduledNextDate(r) && isToday(qd) && !isCallsNowRow(r);
  });
  if (type === "overdue") return sourceRows.filter(r => {
    const qd = queueDate(r);
    return hasScheduledNextDate(r) && isOverdue(qd) && !isCallsNowRow(r);
  });
  if (type === "meetings") return sourceRows.filter(r => LEADS_MEETING_BUCKET_KEYS.has(pipelineKey(r)));
  if (type === "waitingclient") return sourceRows.filter(r => waitingOn(r, "WaitingOnClient"));
  if (type === "waitingcarrier") return sourceRows.filter(r => waitingOn(r, "WaitingOnCarrier"));
  return [];
}

async function loadMyDaySnapshot(force = false){
  if (myDaySnapshot.isLoading) return;

  const fresh = (Date.now() - (myDaySnapshot.loadedAt || 0)) < MYDAY_SNAPSHOT_TTL_MS;
  if (!force && fresh) return;

  myDaySnapshot.isLoading = true;
  try{
    const res = await fetch(MYDAY_SNAPSHOT_URL, withDialHeaders({ credentials: "include" }));
    if (!res.ok) return;
    const data = await res.json();
    const queues = data?.queues || {};
    const idsByQueue = {};
    const counts = {};

    for (const key of MYDAY_QUEUE_KEYS){
      const ids = Array.isArray(queues?.[key]?.ids) ? queues[key].ids.map(x => norm(x)).filter(Boolean) : [];
      idsByQueue[key] = new Set(ids);
      counts[key] = Number.isFinite(queues?.[key]?.count) ? Number(queues[key].count) : ids.length;
    }

    myDaySnapshot = {
      counts,
      idsByQueue,
      loadedAt: Date.now(),
      isLoading: false
    };
  }catch{
    myDaySnapshot.isLoading = false;
  }
}

function queueRows(type){
  const sourceRows = uniqueRows(rows);
  const ids = myDaySnapshot.idsByQueue?.[type];
  if (ids instanceof Set){
    return sourceRows.filter(r => ids.has(norm(r.dataset.clientId)));
  }
  return queueRowsLocal(type);
}

function queueMeta(type){
  if (type === "callsnow") return { title: "Calls Now", sub: "Priority follow-up calls that should happen immediately.", count: queueRows(type).length, callTask: true };
  if (type === "today") return { title: "Due Today", sub: "Touches due today and ready for execution.", count: queueRows(type).length, callTask: false };
  if (type === "overdue") return { title: "Overdue", sub: "Rescue this list before it gets stale.", count: queueRows(type).length, callTask: true };
  if (type === "meetings") return { title: "Meetings", sub: "Meeting-stage clients with event execution pressure.", count: queueRows(type).length, callTask: false };
  if (type === "waitingclient") return { title: "Waiting On Client", sub: "Clients who owe the next move back to you.", count: queueRows(type).length, callTask: false };
  if (type === "waitingcarrier") return { title: "Waiting On Carrier", sub: "Cases blocked externally and needing visibility.", count: queueRows(type).length, callTask: false };
  return null;
}

function renderMyDayFocus(){
  if (!mydayFocus) return;
  const meta = queueMeta(activeMyDayQueue);
  mydayFocus.classList.toggle("active", !!meta);
  $$(".myday-tile").forEach(tile => tile.classList.toggle("active", tile.getAttribute("data-queue") === activeMyDayQueue));
  if (!meta) return;
  if (mydayFocusTitle) mydayFocusTitle.textContent = meta.title;
  if (mydayFocusSub) mydayFocusSub.textContent = `${meta.sub} Open a record to edit it in Quick View.`;
  if (mydayFocusCount) mydayFocusCount.textContent = `${meta.count} record${meta.count === 1 ? "" : "s"}`;
  if (btnMyDayCallTask) btnMyDayCallTask.style.display = meta.callTask ? "" : "none";
}

function refreshMyDay(){
  $("#qCallsNow") && ($("#qCallsNow").textContent = String(myDaySnapshot.counts?.callsnow ?? queueRows("callsnow").length));
  $("#qDueToday") && ($("#qDueToday").textContent = String(myDaySnapshot.counts?.today ?? queueRows("today").length));
  $("#qOverdue") && ($("#qOverdue").textContent = String(myDaySnapshot.counts?.overdue ?? queueRows("overdue").length));
  $("#qMeetings") && ($("#qMeetings").textContent = String(myDaySnapshot.counts?.meetings ?? queueRows("meetings").length));
  $("#qWaitingClient") && ($("#qWaitingClient").textContent = String(myDaySnapshot.counts?.waitingclient ?? queueRows("waitingclient").length));
  $("#qWaitingCarrier") && ($("#qWaitingCarrier").textContent = String(myDaySnapshot.counts?.waitingcarrier ?? queueRows("waitingcarrier").length));
  renderMyDayFocus();

  loadMyDaySnapshot().then(() => {
    $("#qCallsNow") && ($("#qCallsNow").textContent = String(myDaySnapshot.counts?.callsnow ?? 0));
    $("#qDueToday") && ($("#qDueToday").textContent = String(myDaySnapshot.counts?.today ?? 0));
    $("#qOverdue") && ($("#qOverdue").textContent = String(myDaySnapshot.counts?.overdue ?? 0));
    $("#qMeetings") && ($("#qMeetings").textContent = String(myDaySnapshot.counts?.meetings ?? 0));
    $("#qWaitingClient") && ($("#qWaitingClient").textContent = String(myDaySnapshot.counts?.waitingclient ?? 0));
    $("#qWaitingCarrier") && ($("#qWaitingCarrier").textContent = String(myDaySnapshot.counts?.waitingcarrier ?? 0));
    renderMyDayFocus();
  }).catch(() => {});
}

function savedViews(){
  return loadJSON(LS_VIEWS, []);
}

function renderSavedViews(){
  if (!savedViewsBar) return;
  const views = savedViews();
  const base = [];

  const custom = views.map((view, idx) =>
    `<button type="button" class="preset-chip" data-savedview="${idx}">${safeHtml(view.name || `View ${idx + 1}`)}</button>`
  );

  savedViewsBar.innerHTML = base.concat(custom).join("");
}

function saveCurrentView(){
  const name = window.prompt("Name this saved view:");
  if (!name) return;

  const views = savedViews();
  views.push({
    name: name.trim(),
    status: norm(statusFilter.value),
    priority: norm(priorityFilter.value),
    stage: norm(stageFilter.value),
    attention: norm(attentionFilter.value),
    sort: norm(sortBy.value),
    view: norm(viewMode.value)
  });
  saveJSON(LS_VIEWS, views.slice(-8));
  renderSavedViews();
  toast("Saved view");
}

function applySavedView(index){
  const view = savedViews()[index];
  if (!view) return;
  statusFilter.value = view.status || "";
  priorityFilter.value = view.priority || "";
  stageFilter.value = view.stage || "";
  attentionFilter.value = view.attention || "";
  sortBy.value = view.sort || "name_asc";
  viewMode.value = view.view || "pipeline";
  applyViewMode();
  currentPage = 1;
  renderAll();
}

/* ========= Drawer ========= */
async function openDrawerById(clientId){
  const row = rows.find(r => r.dataset.clientId === clientId);
  if (row) return openDrawerForRow(row);

  try{
    const res = await fetch(`/Leads/Lead?id=${encodeURIComponent(clientId)}`, withDialHeaders());
    if (!res.ok) throw new Error("Lead not found");
    const lead = await res.json();
    const stub = {
      dataset: {
        clientId: lead.leadId || clientId,
        first: lead.firstName || "",
        last: lead.lastName || "",
        email: lead.email || "",
        phone: lead.phone || "",
        originalLeadType: normalizeOriginalLeadTypeValue(lead.originalLeadType) || normalizeOriginalLeadTypeValue(lead.bucket),
        dob: lead.dob || "",
        gender: lead.gender || "",
        addressLine: lead.addressLine || "",
        city: lead.city || "",
        state: lead.state || "",
        county: lead.county || "",
        zipCode: lead.zipCode || lead.zip || "",
        mortgageLender: lead.mortgageLender || "",
        loanAmount: lead.loanAmount || "",
        crmStatus: lead.crmStatus || "Lead",
        crmPriority: "Normal",
        crmLastTouch: lead.crmLastTouch || lead.updatedUtc || "",
        crmNextDate: "",
        crmNextText: lead.crmNotes || "",
        crmTags: "",
        crmPipeline: lead.bucket || "MortgageProtection",
        crmWaitingOn: "WaitingOnAgent",
        crmPinnedBrief: "",
        crmAttemptsToday: lead.attemptsToday ?? lead.dialsToday ?? 0,
        crmAttemptsWeek: lead.attemptsThisWeek ?? lead.dialsWeek ?? 0,
        crmAttemptsMonth: lead.attemptsThisMonth ?? 0,
        crmAttemptsYear: lead.attemptsThisYear ?? 0,
        crmAttemptsLife: lead.attemptsLifetime ?? lead.callCount ?? 0,
        crmStageEntered: lead.createdUtc || todayISO(),
        sMeetingLocation: lead.addressLine || "",
        sZoom: "",
        sUsezoom: "false",
        sMeetingTime: "09:00",
        sMeetingDuration: "30",
        crmOwner: "",
        crmWatchers: "",
      }
    };
    return openDrawerForRow(stub);
  }catch(err){
    console.error(err);
    toast("Quick View failed to load lead.");
  }
}

async function openDrawerForRow(row){
  drawerEditing = true;
  activeClientId = row.dataset.clientId;
  if (drawer) drawer.dataset.clientId = activeClientId || "";
  activeClientDetail = null;
  noteSyncLeadField();
  // Load Actions tab (Execution MVP)
  void loadLeadActionsPanel();

  void loadLeadCommitmentsPanel();

  const name = fullName(row);
  const email = norm(row.dataset.email);
  const phone = norm(row.dataset.phone);
  dName.textContent = name || "Lead";
  if (dFirst) dFirst.value = row.dataset.first || "";
  if (dLast) dLast.value = row.dataset.last || "";
  syncDrawerEmailDisplay(email);
  dPhone.textContent = phone || "No phone";
  if (dEmailInput) dEmailInput.value = email;
  if (dPhoneInput) dPhoneInput.value = phone;
  if (dPhone2Input) dPhone2Input.value = row.dataset.phone2 || "";
  if (dDob) dDob.value = row.dataset.dob || "";
  if (dAge) dAge.value = row.dataset.age || "";
  if (dGender) dGender.value = row.dataset.gender || "";
  if (dAddress) dAddress.value = row.dataset.addressLine || "";
  if (dCity) dCity.value = row.dataset.city || "";
  if (dState) dState.value = row.dataset.state || "";
  if (dCounty) dCounty.value = row.dataset.county || "";
  if (dZip) dZip.value = row.dataset.zipCode || "";
  if (dBtc) dBtc.value = row.dataset.btc || "";
  if (dLender) dLender.value = row.dataset.mortgageLender || "";
  if (dLoanAmount) dLoanAmount.value = row.dataset.loanAmount || "";

  btnMail.href = email ? ("mailto:" + email) : "#";
  btnCall.href = phone ? ("tel:" + phone) : "#";
  dStatus.value = row.dataset.crmStatus || "Active";
  dPipelineStage.value = normalizePipelineStageValue(row.dataset.crmPipeline, "MortgageProtection");
  applyQuickViewContactProfileLabels(row, null);
  dLastTouch.value = row.dataset.crmLastTouch || "";
  dTags.value = row.dataset.crmTags || "";
  dNotes.value = row.dataset.crmNotes || "";
  setDrawerNextActionDate(row.dataset.crmNextDate || "");
  dNextText.value = row.dataset.crmNextText || "";
  dPriority.value = row.dataset.crmPriority || "Normal";
  dMeetingLocation.value = row.dataset.sMeetingLocation || "";
  dZoomJoinUrl.value = row.dataset.sZoom || "";
  dUsePersonalZoomLink.checked = (row.dataset.sUsezoom || "false") === "true";
  if (!dZoomJoinUrl.value && dUsePersonalZoomLink.checked) dZoomJoinUrl.value = loadSavedZoomLink();
  applyMeetingType(inferMeetingType(null, row), row);
  dMeetingTime.value = row.dataset.sMeetingTime || "09:00";
  dMeetingDuration.value = row.dataset.sMeetingDuration || "30";
  dWaitingOn.value = row.dataset.crmWaitingOn || "WaitingOnAgent";
  dPinnedBrief.value = row.dataset.crmPinnedBrief || "";
  dDocIdReceived.checked = false;
  dDocAppSent.checked = false;
  dDocAppSigned.checked = false;

  loadProductionHistory(row.dataset.clientId);
  dDocPolicyDelivered.checked = false;
  dDocReviewBooked.checked = false;
  dAssignedOwner.value = row.dataset.crmOwner || "";
  dWatchers.value = row.dataset.crmWatchers || "";
  dMentionNote.value = "";
  dStageAge.textContent = `Stage Age: ${stageAgeDays(row)}d`;
  dAttempts.textContent = `Attempts: ${row.dataset.crmAttemptsToday || 0} today • ${row.dataset.crmAttemptsWeek || 0} week • ${row.dataset.crmAttemptsLife || 0} total`;
  dWaitingOnPill.textContent = waitingLabel(row.dataset.crmWaitingOn || "WaitingOnAgent");
  dOutcomeSuggestion.textContent = "Use one-click outcomes to move leads into the last 7 non-lead buckets.";
  refreshCalendarBusyPanel();

  renderPortalActions(row, null);

  dActDate.value = todayISO();
  dActNote.value = "";
  renderTimeline([]);
  renderMentionNotes([]);
  refreshLeadOverviewSummary();
  dSaved.textContent = "Loading…";

  drawer.classList.add("open");
  drawerBackdrop.classList.add("open");
  drawer.setAttribute("aria-hidden", "false");
  lockPageScrollForQuickView();
  updateZoomControls();

  closeAllMenus(null);

  try{
    const detail = await loadQuickView(activeClientId);
    if (activeClientId !== row.dataset.clientId) return;

    activeClientDetail = detail;
    noteSyncLeadField();
    dStatus.value = detail.crmStatus || row.dataset.crmStatus || "Active";
    dPipelineStage.value = normalizePipelineStageValue(detail.pipelineStage || row.dataset.crmPipeline, "MortgageProtection");
    applyQuickViewContactProfileLabels(row, detail, dPipelineStage.value);
    dLastTouch.value = detail.crmLastTouch || row.dataset.crmLastTouch || "";
    dTags.value = detail.crmTags || row.dataset.crmTags || "";
    dNotes.value = detail.agentNotes || row.dataset.crmNotes || "";
    setDrawerNextActionDate(detail.crmNextDate || row.dataset.crmNextDate || "");
    dNextText.value = detail.crmNextText || row.dataset.crmNextText || "";
    dPriority.value = detail.crmPriority || row.dataset.crmPriority || "Normal";
    if (dFirst) dFirst.value = detail.firstName || row.dataset.first || "";
    if (dLast) dLast.value = detail.lastName || row.dataset.last || "";
    if (dEmailInput) dEmailInput.value = detail.email || email || "";
    if (dPhoneInput) dPhoneInput.value = detail.phone || phone || "";
    if (dPhone2Input) dPhone2Input.value = detail.phone2 || row.dataset.phone2 || "";
    if (dDob) dDob.value = detail.dob || "";
    if (dAge) dAge.value = detail.age || row.dataset.age || "";
    if (dGender) dGender.value = detail.gender || "";
    if (dAddress) dAddress.value = detail.addressLine || "";
    if (dCity) dCity.value = detail.city || "";
    if (dState) dState.value = detail.state || "";
    if (dCounty) dCounty.value = detail.county || "";
    if (dZip) dZip.value = detail.zipCode || "";
    if (dBtc) dBtc.value = detail.btc || row.dataset.btc || "";
    if (dLender) dLender.value = detail.mortgageLender || "";
    if (dLoanAmount) dLoanAmount.value = detail.loanAmount || "";
    syncDrawerEmailDisplay(detail.email || email);
    dPhone.textContent = detail.phone || phone || "No phone";
    dName.textContent = `${dFirst?.value || ""} ${dLast?.value || ""}`.trim() || "Lead";
    dMeetingLocation.value = detail.meetingLocation || row.dataset.sMeetingLocation || "";
    dZoomJoinUrl.value = detail.zoomJoinUrl || row.dataset.sZoom || "";
    dUsePersonalZoomLink.checked = !!detail.usePersonalZoomLink;
    if (!dZoomJoinUrl.value && dUsePersonalZoomLink.checked) dZoomJoinUrl.value = loadSavedZoomLink();
    applyMeetingType(inferMeetingType(detail, row), row);
    dMeetingTime.value = detail.meetingTime || row.dataset.sMeetingTime || "09:00";
    dMeetingDuration.value = String(detail.meetingDurationMinutes || row.dataset.sMeetingDuration || 30);
    dWaitingOn.value = detail.waitingOn || row.dataset.crmWaitingOn || "WaitingOnAgent";
    dPinnedBrief.value = detail.pinnedBrief || row.dataset.crmPinnedBrief || "";
    dAssignedOwner.value = detail.collaboration?.owner || row.dataset.crmOwner || "";
    dWatchers.value = (detail.collaboration?.watchers || []).join(", ");
    dDocIdReceived.checked = !!detail.docChecklist?.idReceived;
    dDocAppSent.checked = !!detail.docChecklist?.appSent;
    dDocAppSigned.checked = !!detail.docChecklist?.appSigned;
    dDocPolicyDelivered.checked = !!detail.docChecklist?.policyDelivered;
    dDocReviewBooked.checked = !!detail.docChecklist?.reviewBooked;
    dStageAge.textContent = `Stage Age: ${detail.stageAgeDays || stageAgeDays(row)}d`;
    dAttempts.textContent = `Attempts: ${detail.attemptsToday || 0} today • ${detail.attemptsThisWeek || 0} week • ${detail.attemptsLifetime || 0} total`;
    dWaitingOnPill.textContent = detail.waitingOnLabel || waitingLabel(detail.waitingOn || row.dataset.crmWaitingOn || "WaitingOnAgent");
    refreshCalendarBusyPanel();
    renderTimeline(detail.activities || []);
    renderMentionNotes(detail.collaboration?.mentionNotes || []);
    refreshLeadOverviewSummary();

    renderPortalActions(row, detail);

    dSaved.textContent = "Loaded";
  }catch(err){
    console.error(err);
    dSaved.textContent = "Load failed";
    toast("Failed to load lead details.");
  }
}

// ===== Production (Lead) - inline, autosave =====
const prodAmountInput = document.getElementById("prodAmount");
const prodPersonalInput = document.getElementById("prodPersonal");
async function loadProductionHistory(leadId){
  const list = document.getElementById("phList");
  const addBtn = document.getElementById("phAddFromDrawer");
  const summary = document.getElementById("phProductionSummary");
  if (!list) return;
    list.innerHTML = '<div class="ph-empty muted">Loading…</div>';
    try{
      const res = await fetch(`/production/history/lead?leadId=${encodeURIComponent(leadId)}`, { headers: { 'Accept':'application/json' }});
      if (!res.ok) throw new Error("load fail");
      const data = await res.json();
    const latest = (data && data.length) ? data[0] : null;
    const totals = (data || []).reduce((acc, p) => {
      const amt = Number(p?.amount || 0);
      const raw = norm(p?.status);
      const st = productionBucket(raw);
      if (st === "paid") acc.paid += amt;
      else if (st === "issued") acc.issued += amt;
      else if (st === "submitted") acc.submitted += amt;
      return acc;
    }, { paid: 0, issued: 0, submitted: 0 });
    setLeadProductionById(leadId, latest?.status || "", latest?.amount || 0, totals);
    // --- Production summary logic ---
    if (summary) {
      if (!data || !data.length) {
        summary.textContent = "No production";
      } else {
        // Count by status
        const counts = { Submitted: 0, Issued: 0, Paid: 0 };
        data.forEach(item => {
          if (item.status === "Submitted") counts.Submitted++;
          if (item.status === "Issued") counts.Issued++;
          if (item.status === "Paid") counts.Paid++;
        });
        const parts = [];
        if (counts.Submitted) parts.push(`${counts.Submitted} Submitted`);
        if (counts.Issued) parts.push(`${counts.Issued} Issued`);
        if (counts.Paid) parts.push(`${counts.Paid} Paid`);
        summary.textContent = parts.length ? parts.join(" • ") : "No production";
      }
    }
    if (!data || !data.length){
      list.innerHTML = '<div class="ph-empty muted">No production yet.</div>';
      return;
    }
    list.innerHTML = "";
    data.forEach(item => {
      const div = document.createElement("div");
      div.className = "ph-item";
      const safeStatus = norm(item.status) || "Submitted";
      const toneClass = safeStatus.toLowerCase();
      const updatedLabel = item.updated ? new Date(item.updated).toLocaleString() : "";
      div.innerHTML = `
        <div class="ph-left">
        <div class="ph-top">
          <div class="ph-status ${toneClass}">${safeStatus}</div>
          ${updatedLabel ? `<div class="ph-updated">${safeHtml(updatedLabel)}</div>` : ""}
        </div>
        <div class="ph-metrics">
          <div class="ph-metric ${toneClass}">
            <span class="ph-metric-label">${safeHtml(safeStatus)} Amount</span>
            <div class="ph-amt">$${Number(item.amount).toLocaleString(undefined,{maximumFractionDigits:2})}</div>
          </div>
          ${Number(item.personalAmount || 0) > 0 ? `<div class="ph-metric">
            <span class="ph-metric-label">Personal Revenue</span>
            <div class="ph-amt personal">$${Number(item.personalAmount || 0).toLocaleString(undefined,{maximumFractionDigits:2})}</div>
          </div>` : ""}
          ${norm(item.notes) ? `<div class="ph-note"><span class="ph-note-label">Notes</span><span class="ph-note-text">${safeHtml(item.notes)}</span></div>` : ""}
        </div>
        </div>
        <div class="ph-actions">
            <button class="btn btn-ghost ph-edit" data-id="${item.id}" data-amount="${item.amount}" data-personal="${item.personalAmount ?? ""}" data-status="${item.status}" data-notes="${item.notes ?? ""}">Edit</button>
            <button class="btn btn-red ph-delete" data-id="${item.id}">Delete</button>
        </div>`;
      list.appendChild(div);
    });
    list.querySelectorAll(".ph-edit").forEach(btn=>{
      btn.addEventListener("click", ()=>{
        const id = btn.getAttribute("data-id");
        const amt = btn.getAttribute("data-amount");
        const status = btn.getAttribute("data-status");
        const notes = btn.getAttribute("data-notes");
        const personal = btn.getAttribute("data-personal") || "";
        openProductionModalEdit(id, leadId, amt, status, notes, personal);
      });
    });
    list.querySelectorAll(".ph-delete").forEach(btn=>{
      btn.addEventListener("click", async ()=>{
        if (!confirm("Delete this production entry?")) return;
        const token = getAntiForgeryToken();
        const id = btn.getAttribute("data-id");
        await fetch("/production/delete", {
          method:"POST",
          headers:{
            "Content-Type":"application/x-www-form-urlencoded",
            "RequestVerificationToken": token,
            "Accept":"application/json"
          },
          body:`id=${encodeURIComponent(id)}`
        }).catch(()=>{});
        await loadProductionHistory(leadId);
        refreshLeadProductionTiles();
      });
    });
      // Removed addResetAction to prevent duplicate Reset Production button
    }catch{
      list.innerHTML = '<div class="ph-empty muted">Unable to load production.</div>';
      // Removed addResetAction to prevent duplicate Reset Production button
    }
    await refreshLeadProductionTiles();
    if (addBtn){
      addBtn.onclick = () => openProductionModalAdd(leadId, dName.textContent || "Lead");
    }
  }

// Fallback binding in case loadProductionHistory hasn’t run yet
const qvAddProductionBtn = document.getElementById("phAddFromDrawer");
if (qvAddProductionBtn){
  qvAddProductionBtn.addEventListener("click", () => {
    const leadId = document.getElementById("prodLeadId")?.value || activeClientId;
    if (!leadId){
      toast("Open a lead before adding production.");
      return;
    }
    openProductionModalAdd(leadId, dName?.textContent || "Lead");
  });
}

function addResetAction(list, leadId){
  if (!list) return;
  const reset = document.createElement("button");
  reset.type = "button";
  reset.className = "btn btn-red";
  reset.textContent = "Reset Production";
  reset.style.marginTop = "10px";
  reset.addEventListener("click", async ()=>{
    if (!confirm("Reset production for this lead?")) return;
    const token = getAntiForgeryToken();
    await fetch("/production/reset/lead", {
      method:"POST",
      headers:{
        "Content-Type":"application/x-www-form-urlencoded",
        "RequestVerificationToken": token,
        "Accept":"application/json"
      },
      body:`leadId=${encodeURIComponent(leadId)}`
    }).catch(()=>{});
    loadProductionHistory(leadId);
  });
  list.appendChild(reset);
}

function openProductionModalAdd(leadId, name){
  const modalEl = document.getElementById('productionModal');
  if (!modalEl) return;
  const form = modalEl.querySelector("form");
  form.action = "/production/add/lead";
  form.querySelector("input[name='id']")?.remove();
  document.getElementById('prodLeadId').value = leadId;
  document.getElementById('prodLeadName').textContent = name;
  const amtEl = form.querySelector("input[name='amount']");
  const personalEl = form.querySelector("input[name='personalAmount']");
  const statusEl = form.querySelector("select[name='status']");
  const notesEl = form.querySelector("textarea[name='notes']");
  if (amtEl) amtEl.value = "";
  if (personalEl) personalEl.value = "";
  if (statusEl) statusEl.value = "0";
  if (notesEl) notesEl.value = "";

  bootstrap.Modal.getOrCreateInstance(modalEl).show();
}

function openProductionModalEdit(id, leadId, amount, status, notes, personal){
  const modalEl = document.getElementById('productionModal');
  if (!modalEl) return;
  const form = modalEl.querySelector("form");
  form.action = "/production/update";
  let idField = form.querySelector("input[name='id']");
  if (!idField){
    idField = document.createElement("input");
    idField.type = "hidden";
    idField.name = "id";
    form.appendChild(idField);
  }
  idField.value = id;
  document.querySelector("input[name='leadId']").value = leadId;
  const amtEl = form.querySelector("input[name='amount']");
  if (amtEl) amtEl.value = amount;
  const personalEl = form.querySelector("input[name='personalAmount']");
  if (personalEl) personalEl.value = personal || "";
  const statusEl = form.querySelector("select[name='status']");
  if (statusEl) statusEl.value = status === "Issued" ? "1" : status === "Paid" ? "2" : "0";
  const notesEl = form.querySelector("textarea[name='notes']");
  if (notesEl) notesEl.value = notes || "";

  const draftAll = loadJSON(LS_PROD_DRAFT_LEAD, {});
  const persistDraft = () => {
    draftAll[leadId] = {
      amount: amtEl?.value || "",
      personalAmount: personalEl?.value || "",
      status: statusEl?.value || "0",
      notes: notesEl?.value || ""
    };
    saveJSON(LS_PROD_DRAFT_LEAD, draftAll);
  };
  [amtEl, personalEl, statusEl, notesEl].forEach(el => el?.addEventListener("input", persistDraft, { once: false }));
  form.addEventListener("submit", () => {
    delete draftAll[leadId];
    saveJSON(LS_PROD_DRAFT_LEAD, draftAll);
  }, { once: true });

  bootstrap.Modal.getOrCreateInstance(modalEl).show();
}

// Intercept production modal submit to keep UI in sync without full reload
(function wireProductionModalSubmit(){
  const modalEl = document.getElementById('productionModal');
  if (!modalEl) return;
  const form = modalEl.querySelector("form");
  if (!form) return;

  form.addEventListener("submit", async (e) => {
    e.preventDefault();
    const leadId = document.getElementById("prodLeadId")?.value || activeClientId;
    if (!leadId) return toast("Lead not found for production save.");
    const fd = new FormData(form);
    const body = new URLSearchParams();
    fd.forEach((v, k) => body.append(k, v.toString()));

    try{
      const res = await fetch(form.action, {
        method: "POST",
        headers: {
          "Content-Type": "application/x-www-form-urlencoded; charset=UTF-8",
          "RequestVerificationToken": getAntiForgeryToken(),
          "Accept": "application/json"
        },
        credentials: "include",
        body: body.toString()
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      bootstrap.Modal.getOrCreateInstance(modalEl).hide();
      await loadProductionHistory(leadId);
      toast("Production saved");
      refreshLeadProductionTiles();
    }catch(err){
      console.error("Production save failed", err);
      toast("Production save failed. Please retry.");
    }
  });
})();

function renderMentionNotes(items){
  if (!mentionList) return;
  const arr = Array.isArray(items) ? items : [];
  if (!arr.length){
    mentionList.innerHTML = `<div class="tiny">No team mention notes yet.</div>`;
    return;
  }

  mentionList.innerHTML = arr.map(item => `
    <div class="event">
      <div class="top">
        <div class="type">${safeHtml(item.mentionedUser || item.createdBy || "Team Note")}</div>
        <div class="meta">${safeHtml((item.createdUtc || "").toString().replace("T", " ").slice(0, 16))}</div>
      </div>
      <div class="note">${safeHtml(item.note || "")}</div>
    </div>
  `).join("");
}

function closeDrawer(){
  drawerEditing = false;
  activeClientId = null;
  if (drawer) drawer.dataset.clientId = "";
  leadActionsLoadPromise = null;
  if (leadActionsHubModal && window.bootstrap){
    const inst = bootstrap.Modal.getInstance(leadActionsHubModal);
    inst?.hide();
  }
  drawer.classList.remove("open");
  drawerBackdrop.classList.remove("open");
  drawer.setAttribute("aria-hidden", "true");
  unlockPageScrollForQuickView();
}
function openLeadActionsHub(){
  const requestedClientId = (activeClientId || drawer?.dataset?.clientId || "").toString().trim();
  if (!requestedClientId){
    toast("Open a lead first.");
    return;
  }
  bindQuickViewBootstrapModals();
  closeLegacyOverlayModals();
  reconcileBootstrapModalState();
  activeClientId = requestedClientId;
  if (drawer) drawer.dataset.clientId = requestedClientId;
  ensureModalInBody('leadActionsHubModal');
  const modalEl = document.getElementById('leadActionsHubModal');
  if (modalEl && window.bootstrap){
    bootstrap.Modal.getOrCreateInstance(modalEl).show();
  }
  if (dActDate && !norm(dActDate.value)) dActDate.value = todayISO();
  renderTimeline(activeClientDetail?.activities || []);
  void loadLeadActionsPanel();
  void loadLeadCommitmentsPanel();
}

btnCloseDrawer?.addEventListener("click", closeDrawer);
leadQuickActionsShortcut?.addEventListener("click", (event) => {
  event.preventDefault();
  openLeadActionsHub();
});

/* ========= Note to Self (Quick View) ========= */
function noteLineDate(isoDate){
  const m = String(isoDate || "").match(/^(\d{4})-(\d{2})-(\d{2})$/);
  if (!m) return "00/00/0000";
  return `${m[2]}/${m[3]}/${m[1]}`;
}

function notePrefix(isoDate){
  return `[${noteLineDate(isoDate)}]`;
}

function stripExistingPrefix(line){
  return String(line || "")
    .replace(/^\s*\[[^\]]*\]\s*/, "")
    .replace(/^\s*---\s*$/, "")
    .trim();
}

function extractNoteBodyText(rawText){
  const normalizedLines = String(rawText || "")
    .split(/\r?\n/)
    .map(stripExistingPrefix)
    .filter(x => !!x);
  return normalizedLines.join("\n").trim();
}

function normalizeNoteBodyForDate(rawText, isoDate){
  const prefix = notePrefix(isoDate);
  const body = extractNoteBodyText(rawText);
  if (!body) return `${prefix} `;

  const lines = body.split(/\r?\n/);
  const first = lines[0] || "";
  const rest = lines.slice(1).join("\n");
  return rest ? `${prefix} ${first}\n${rest}` : `${prefix} ${first}`;
}

function lineStartIndex(text, pos){
  const p = Math.max(0, pos || 0);
  return text.lastIndexOf("\n", Math.max(0, p - 1)) + 1;
}

function lineEndIndex(text, pos){
  const p = Math.max(0, pos || 0);
  const idx = text.indexOf("\n", p);
  return idx < 0 ? text.length : idx;
}

function currentLinePrefixLength(text, caretPos){
  const start = lineStartIndex(text, caretPos);
  const end = lineEndIndex(text, caretPos);
  const line = text.slice(start, end);
  const m = line.match(/^\[(\d{2})\/(\d{2})\/(\d{4})\]\s?/);
  return m ? m[0].length : 0;
}

function enforceCaretAfterPrefix(textarea){
  if (!textarea) return;
  const value = textarea.value || "";
  const start = textarea.selectionStart ?? 0;
  const end = textarea.selectionEnd ?? start;
  const lineStart = lineStartIndex(value, start);
  const protectedPrefixLen = currentLinePrefixLength(value, start);
  if (!protectedPrefixLen) return;
  const min = lineStart + protectedPrefixLen;
  if (start < min || end < min){
    textarea.setSelectionRange(min, min);
  }
}

function isEditingInsidePrefix(textarea, key){
  if (!textarea) return false;
  const value = textarea.value || "";
  const start = textarea.selectionStart ?? 0;
  const end = textarea.selectionEnd ?? start;
  const lineStart = lineStartIndex(value, start);
  const protectedPrefixLen = currentLinePrefixLength(value, start);
  if (!protectedPrefixLen) return false;
  const min = lineStart + protectedPrefixLen;

  if (start !== end){
    return start < min;
  }

  if (key === "Backspace") return start <= min;
  if (key === "Delete") return start < min;
  if (key === "Home") return true;
  return false;
}

function noteCurrentLeadContext(){
  const leadId = activeClientId || "";
  const first = dFirst?.value || activeClientDetail?.firstName || "";
  const last = dLast?.value || activeClientDetail?.lastName || "";
  const leadName = `${first} ${last}`.trim() || "Lead";
  return { leadId, leadName };
}

function noteSetStatus(msg, bad = false){
  if (!noteStatusEl) return;
  noteStatusEl.textContent = msg || "";
  noteStatusEl.classList.toggle("is-bad", !!bad);
}

function noteSyncLeadField(){
  if (!noteLeadInput) return;
  const ctx = noteCurrentLeadContext();
  noteLeadInput.value = ctx.leadId ? ctx.leadName : "No lead selected";
}

async function noteLoadDates(leadIdValue){
  if (!noteDatesSelect) return [];
  const leadId = (leadIdValue || noteCurrentLeadContext().leadId || "").trim();
  if (!leadId){
    noteDatesSelect.innerHTML = '<option value="">Select lead + date</option>';
    return [];
  }
  try{
    const res = await fetch(`/WorkstationNotes/Dates?leadId=${encodeURIComponent(leadId)}`, withDialHeaders({ credentials: "include" }));
    if (!res.ok) throw new Error("fail");
    const dates = await res.json();
    const list = Array.isArray(dates) ? dates : [];
    const current = noteDatesSelect.value || "";
    noteDatesSelect.innerHTML = ['<option value="">Select lead + date</option>']
      .concat(list.map(d => {
        const lid = (d?.leadId || "").toString();
        const nm = (d?.leadName || "Lead").toString();
        const nd = (d?.noteDate || "").toString();
        return `<option value="${noteEncodeKey(lid, nd)}">${escapeHtml(`${nm} — ${noteDisplayDate(nd)}`)}</option>`;
      })).join("");
    if (current && Array.from(noteDatesSelect.options).some(o => o.value === current)){
      noteDatesSelect.value = current;
    }
    return list;
  }catch{
    noteSetStatus("Could not load saved dates", true);
    return [];
  }
}

async function noteLoadForDate(dateValue, leadIdValue){
  if (!noteDateInput || !noteWentWell || !noteCouldBetter) return;
  const date = (dateValue || noteDateInput.value || noteTodayISO()).trim();
  const fallbackLead = noteCurrentLeadContext().leadId;
  const leadId = (leadIdValue || fallbackLead || "").trim();
  if (!leadId){
    noteWentWell.value = "";
    noteCouldBetter.value = "";
    noteSetStatus("Select a lead first", true);
    return;
  }
  if (!date) return;
  noteDateInput.value = date;
  noteSetStatus("Loading...");
  try{
    const res = await fetch(`/WorkstationNotes/Entry?leadId=${encodeURIComponent(leadId)}&date=${encodeURIComponent(date)}`, withDialHeaders({ credentials: "include" }));
    if (!res.ok) throw new Error("fail");
    const payload = await res.json();
    noteWentWell.value = normalizeNoteBodyForDate(payload?.wentWell || "", date);
    noteCouldBetter.value = normalizeNoteBodyForDate(payload?.couldBetter || "", date);
    if (noteDatesSelect) noteDatesSelect.value = noteEncodeKey(leadId, date);
    noteSetStatus(`Loaded ${payload?.leadName || noteCurrentLeadContext().leadName} — ${noteDisplayDate(date)}`);
  }catch{
    noteSetStatus("Failed to load note", true);
  }
}

async function noteSave(){
  if (!noteDateInput || !noteWentWell || !noteCouldBetter) return;
  const date = (noteDateInput.value || "").trim();
  const ctx = noteCurrentLeadContext();
  if (!ctx.leadId){
    noteSetStatus("Select a lead first", true);
    return;
  }
  if (!date){
    noteSetStatus("Pick a date first", true);
    return;
  }
  noteSetStatus("Saving...");
  try{
    const normalizedWentWell = normalizeNoteBodyForDate(noteWentWell.value || "", date);
    const normalizedCouldBetter = normalizeNoteBodyForDate(noteCouldBetter.value || "", date);
    const wentWellBody = extractNoteBodyText(normalizedWentWell);
    const couldBetterBody = extractNoteBodyText(normalizedCouldBetter);

    noteWentWell.value = normalizedWentWell;
    noteCouldBetter.value = normalizedCouldBetter;

    const token = getAntiForgeryToken();
    const res = await fetch("/WorkstationNotes/Entry", withDialHeaders({
      method: "POST",
      credentials: "include",
      headers: {
        "Content-Type": "application/json",
        "RequestVerificationToken": token
      },
      body: JSON.stringify({
        leadId: ctx.leadId,
        leadName: ctx.leadName,
        date,
        wentWell: wentWellBody ? normalizedWentWell : "",
        couldBetter: couldBetterBody ? normalizedCouldBetter : ""
      })
    }));
    if (!res.ok) throw new Error("fail");
    const payload = await res.json().catch(() => null);
    noteSetStatus(payload?.deleted ? `Cleared ${ctx.leadName} — ${noteDisplayDate(date)}` : `Saved ${ctx.leadName} — ${noteDisplayDate(date)}`);
    await noteLoadDates(ctx.leadId);
    if (noteDatesSelect) noteDatesSelect.value = noteEncodeKey(ctx.leadId, date);
  }catch{
    noteSetStatus("Failed to save note", true);
  }
}

async function openNoteModal(){
  if (!noteOverlay) return;
  noteOverlay.hidden = false;
  document.body.classList.add("note-self-open");
  noteSyncLeadField();
  const ctx = noteCurrentLeadContext();
  if (!ctx.leadId){
    noteSetStatus("Select a lead first", true);
    if (noteWentWell) noteWentWell.value = "";
    if (noteCouldBetter) noteCouldBetter.value = "";
    if (noteDatesSelect) noteDatesSelect.innerHTML = '<option value="">Select lead + date</option>';
    if (noteDateInput && !noteDateInput.value) noteDateInput.value = noteTodayISO();
    return;
  }

  if (noteDateInput && !noteDateInput.value) noteDateInput.value = noteTodayISO();

  const list = await noteLoadDates(ctx.leadId);
  if (Array.isArray(list) && list.length){
    const newestDate = (list[0]?.noteDate || "").toString();
    if (newestDate){
      if (noteDateInput) noteDateInput.value = newestDate;
      if (noteDatesSelect) noteDatesSelect.value = noteEncodeKey(ctx.leadId, newestDate);
      await noteLoadForDate(newestDate, ctx.leadId);
    }else{
      await noteLoadForDate(noteDateInput?.value || noteTodayISO(), ctx.leadId);
    }
  }else{
    await noteLoadForDate(noteDateInput?.value || noteTodayISO(), ctx.leadId);
  }

  noteWentWell?.focus();
}

function closeNoteModal(){
  if (!noteOverlay) return;
  noteOverlay.hidden = true;
  document.body.classList.remove("note-self-open");
}

noteOpenBtn?.addEventListener("click", openNoteModal);
noteCloseBtn?.addEventListener("click", closeNoteModal);
noteDatesSelect?.addEventListener("change", (e) => {
  const decoded = noteDecodeKey(e.target.value);
  if (!decoded.leadId || !decoded.noteDate) return;
  noteLoadForDate(decoded.noteDate, decoded.leadId);
});
noteDateInput?.addEventListener("change", () => {
  noteLoadForDate(noteDateInput.value, noteCurrentLeadContext().leadId);
});
noteSaveBtn?.addEventListener("click", noteSave);

[noteWentWell, noteCouldBetter].forEach((ta) => {
  if (!ta) return;
  ta.addEventListener("keydown", (e) => {
    if (isEditingInsidePrefix(ta, e.key)) e.preventDefault();
  });
  ta.addEventListener("click", () => enforceCaretAfterPrefix(ta));
  ta.addEventListener("keyup", () => enforceCaretAfterPrefix(ta));
  ta.addEventListener("mouseup", () => enforceCaretAfterPrefix(ta));
  ta.addEventListener("paste", () => setTimeout(() => enforceCaretAfterPrefix(ta), 0));
});

function safeHtml(s){
  return (s || "").toString()
    .replaceAll("&","&amp;")
    .replaceAll("<","&lt;")
    .replaceAll(">","&gt;")
    .replaceAll('"',"&quot;")
    .replaceAll("'","&#39;");
}

function renderTimeline(items){
  let arr = Array.isArray(items) ? items.slice() : [];
  if (activeTimelineFilter === "calls") arr = arr.filter(ev => (ev.type || "").toLowerCase() === "call");
  if (activeTimelineFilter === "meetings") arr = arr.filter(ev => (ev.type || "").toLowerCase() === "meeting");
  if (activeTimelineFilter === "notes") arr = arr.filter(ev => (ev.type || "").toLowerCase() === "note");
  if (activeTimelineFilter === "system") arr = arr.filter(ev => !!ev.isSystem);
  timeline.innerHTML = "";
  if (arr.length === 0){
    timeline.innerHTML = `<div class="tiny">No activity yet. Log the first touchpoint.</div>`;
    return;
  }
  arr.slice().reverse().forEach(ev => {
    const div = document.createElement("div");
    div.className = "event";
    const extras = [
      ev.location ? `<div class="meta">Location: ${safeHtml(ev.location)}</div>` : "",
      ev.meetingLink ? `<div class="meta"><a class="link" href="${safeHtml(ev.meetingLink)}" target="_blank" rel="noopener">Open Meeting Link</a></div>` : "",
      ev.calendarWebLink ? `<div class="meta"><a class="link" href="${safeHtml(ev.calendarWebLink)}" target="_blank" rel="noopener">Open Outlook Event</a></div>` : ""
    ].filter(Boolean).join("");
    div.innerHTML = `
      <div class="top">
        <div class="type">${safeHtml(ev.type || "Note")}</div>
        <div class="meta">${safeHtml(ev.date || "")}</div>
      </div>
      <div class="note">${safeHtml(ev.note || "")}</div>
      ${extras}
    `;
    timeline.appendChild(div);
  });
}

function renderPortalActions(row, detail){
  if (!dPortalWrap || !row) return;

  // Leads CRM should not create client portal users
  if (LEADS_ONLY){
    dPortalWrap.innerHTML = `<span class="pill">Portal actions disabled for leads</span>`;
    return;
  }
  const isGuid = (row.dataset.isguid === "true");
  const portal = norm(row.dataset.clienturl);

  if (!isGuid) {
    dPortalWrap.innerHTML = `
      <div style="display:flex; gap:10px; flex-wrap:wrap;">
        <button type="button" class="btn btn-gold" id="btnEnablePortalAccess" title="Convert to Client">Convert To Client</button>
        <button type="button" class="btn btn-gold" id="btnEnableBizPortal" title="Convert to Business Client">Convert To Business Client</button>
      </div>
    `;
    const btn = $("#btnEnablePortalAccess", dPortalWrap);
    const btnBiz = $("#btnEnableBizPortal", dPortalWrap);

    const runConvert = async (recordType, button) => {
      if (!window.confirm(`ARE YOU SURE YOU WANT TO CONVERT TO ${recordType === "BusinessClient" ? "BUSINESS CLIENT" : "CLIENT"}? This updates access immediately.`)) return;
      try{
        button.disabled = true;
        const response = await postJson("/Clients/EnablePortalAccess", { clientUserId: row.dataset.clientId, recordType });
        row.dataset.clientId = response.newClientUserId;
        row.dataset.isguid = "true";
        row.dataset.clienturl = response.clientPortalUrl || "";
        row.dataset.sStatus = "Active";
        const convertedStage = normalizePipelineStageValue(response.pipelineStage, "PolicyPlaced");
        row.dataset.sPipeline = convertedStage;
        activeClientId = response.newClientUserId;
        hydrateRow(row);
        if (dPipelineStage) dPipelineStage.value = convertedStage;
        if (dStatus) dStatus.value = "Active";
        renderPortalActions(row, detail);
        dSaved.textContent = response.emailSent === false ? "Portal access enabled ⚠" : "Portal access sent ✔";
        toast(
          response.emailSent === false
            ? `Portal enabled. Login: ${response.loginUpn}. Email failed.`
            : `Portal access sent. Login: ${response.loginUpn}`
        );
        if (response.warning){
          console.warn(response.warning);
        }
        renderAll();
      }catch(err){
        console.error(err);
        toast(err?.message || "Portal access failed.", { persistent: true, error: true });
      }finally{
        button.disabled = false;
      }
    };

    btn?.addEventListener("click", () => runConvert("Client", btn));
    btnBiz?.addEventListener("click", () => runConvert("BusinessClient", btnBiz));
    return;
  }

  if (portal) {
    dPortalWrap.innerHTML = `<span class="pill">Portal Enabled</span>`;
  } else {
    dPortalWrap.innerHTML = `<span class="btn btn-ghost" aria-disabled="true">Portal Not Available</span>`;
  }

  if (detail?.lastCalendarEventWebLink){
    dPortalWrap.innerHTML += ` <a class="btn btn-ghost" href="${safeHtml(detail.lastCalendarEventWebLink)}" target="_blank" rel="noopener">Last Calendar Event</a>`;
  }
}

btnOpenFirst?.addEventListener("click", () => {
  const first = getCheckedRows()[0];
  if (!first) return;
  openDrawerForRow(first);
});

btnMarkToday?.addEventListener("click", () => {
  dLastTouch.value = todayISO();
  dSaved.textContent = "Touched today — saving…";
  queueQuickViewAutosave();
});

btnSetNextToday?.addEventListener("click", () => {
  setDrawerNextActionDate(todayISO());
  dSaved.textContent = "Next action set — saving…";
  queueQuickViewAutosave();
  refreshCalendarBusyPanel();
});

btnMeetingNextToday?.addEventListener("click", () => {
  setDrawerNextActionDate(todayISO());
  dSaved.textContent = "Meeting date set — saving…";
  queueQuickViewAutosave();
  refreshCalendarBusyPanel();
});

dMeetingType?.addEventListener("change", () => {
  const row = rows.find(r => r.dataset.clientId === activeClientId);
  applyMeetingType(dMeetingType.value, row);
  refreshCalendarBusyPanel();
  queueQuickViewAutosave();
});

dPipelineStage?.addEventListener("change", () => {
  const row = rows.find(r => r.dataset.clientId === activeClientId);
  applyQuickViewContactProfileLabels(row, activeClientDetail, dPipelineStage.value);
});

dNextDate?.addEventListener("change", refreshCalendarBusyPanel);
dNextDate?.addEventListener("change", () => {
  if (dMeetingNextDate && dMeetingNextDate.value !== dNextDate.value) {
    dMeetingNextDate.value = dNextDate.value;
  }
});
dMeetingNextDate?.addEventListener("change", () => {
  if (dNextDate && dNextDate.value !== dMeetingNextDate.value) {
    dNextDate.value = dMeetingNextDate.value;
  }
  refreshCalendarBusyPanel();
});
dMeetingTime?.addEventListener("change", refreshCalendarBusyPanel);
dMeetingDuration?.addEventListener("change", refreshCalendarBusyPanel);

$$("[data-schedulepreset]").forEach(btn => {
  btn.addEventListener("click", () => {
    const preset = btn.getAttribute("data-schedulepreset");
    const now = new Date();
    if (preset === "today3"){
      setDrawerNextActionDate(todayISO());
      dMeetingTime.value = "15:00";
      dNextText.value = dNextText.value || "Same-day follow-up";
    } else if (preset === "tomorrow10"){
      const d = new Date();
      d.setDate(d.getDate() + 1);
      setDrawerNextActionDate(d.toISOString().slice(0, 10));
      dMeetingTime.value = "10:00";
      dNextText.value = dNextText.value || "Tomorrow morning follow-up";
    } else if (preset === "nextbiz"){
      let d = new Date();
      d.setDate(d.getDate() + 1);
      while ([0, 6].includes(d.getDay())) d.setDate(d.getDate() + 1);
      setDrawerNextActionDate(d.toISOString().slice(0, 10));
      dMeetingTime.value = "09:00";
      dNextText.value = dNextText.value || "Next business day touch";
    } else if (preset === "week"){
      const d = new Date(now);
      d.setDate(d.getDate() + 7);
      setDrawerNextActionDate(d.toISOString().slice(0, 10));
      dMeetingTime.value = "09:00";
      dNextText.value = dNextText.value || "1 week follow-up";
    }
    dSaved.textContent = "Next-step preset applied — saving…";
    queueQuickViewAutosave();
    refreshCalendarBusyPanel();
  });
});

btnCopyContact?.addEventListener("click", () => {
  if (!activeClientId) return;
  const row = rows.find(r => r.dataset.clientId === activeClientId);
  if (!row) return;
  copyText(`${fullName(row)}\n${norm(row.dataset.email)}\n${norm(row.dataset.phone)}`.trim());
});

btnCall?.addEventListener("click", (e) => {
  if (!activeClientId) return;
  const row = rows.find(r => r.dataset.clientId === activeClientId);
  const phone = norm(row?.dataset.phone || dPhoneInput?.value || dPhone?.textContent);
  if (!phone){
    e.preventDefault();
    toast("No phone for this lead");
    return;
  }
  e.preventDefault();
  showLeadCommAuth(btnCall, { action: "call", row, phone });
});

// Disable client queues jump in leads-only mode
if (btnOpenQueue && LEADS_ONLY){
  btnOpenQueue.href = "#";
  btnOpenQueue.addEventListener("click", (e) => {
    e.preventDefault();
    toast("Client queues are disabled in Leads CRM.");
  });
}

btnDeleteClient?.addEventListener("click", (e) => {
  e.preventDefault();
  if (!activeClientId) return;
  if (!confirm("Delete this lead? This removes the lead record.")) return;

  const f = document.getElementById("__af");
  if (!f) return toast("Missing antiforgery form.");

  f.setAttribute("action", "/Leads/Delete");
  f.querySelectorAll("input[name='clientUserId']").forEach(x => x.remove());

  const inp = document.createElement("input");
  inp.type = "hidden";
  inp.name = "clientUserId";
  inp.value = activeClientId;
  f.appendChild(inp);

  f.submit();
});

btnAddActivity?.addEventListener("click", () => {
  if (!activeClientId) return;
  if (LEADS_ONLY){
    toast("Timeline activity is disabled in Leads-only mode.");
    return;
  }
  const ev = {
    type: norm(dActType.value) || "Note",
    date: norm(dActDate.value) || todayISO(),
    note: norm(dActNote.value) || ""
  };
  if (!ev.note) return toast("Add an outcome note.");
  postJson("/Clients/AddActivity", {
    clientUserId: activeClientId,
    type: ev.type,
    date: ev.date,
    note: ev.note,
    location: norm(dMeetingLocation.value),
    meetingLink: norm(dZoomJoinUrl.value) || (dUsePersonalZoomLink.checked ? loadSavedZoomLink() : "")
  }).then(data => {
    const row = rows.find(r => r.dataset.clientId === activeClientId);
    if (row){
      row.dataset.sLasttouch = data.crmLastTouch || ev.date;
      row.dataset.sChannel = ev.type;
      row.dataset.sAttemptstoday = String((parseInt(row.dataset.sAttemptstoday || "0", 10) || 0) + (["Call","Text","Email"].includes(ev.type) ? 1 : 0));
      row.dataset.sAttemptsweek = String((parseInt(row.dataset.sAttemptsweek || "0", 10) || 0) + (["Call","Text","Email"].includes(ev.type) ? 1 : 0));
      row.dataset.sAttemptslife = String((parseInt(row.dataset.sAttemptslife || "0", 10) || 0) + (["Call","Text","Email"].includes(ev.type) ? 1 : 0));
      hydrateRow(row);
      dAttempts.textContent = `Attempts: ${row.dataset.crmAttemptsToday || 0} today • ${row.dataset.crmAttemptsWeek || 0} week • ${row.dataset.crmAttemptsLife || 0} total`;
    }
    dLastTouch.value = data.crmLastTouch || ev.date;
    activeClientDetail = { ...(activeClientDetail || {}), activities: data.activities || [] };
    dActNote.value = "";
    renderTimeline(data.activities || []);
    dSaved.textContent = "Activity saved ✔";
    toast("Activity logged");
    renderAll();
  }).catch(err => {
    console.error(err);
    toast("Activity save failed.");
  });
});

timelineFilters?.addEventListener("click", (e) => {
  const btn = e.target.closest("[data-timelinefilter]");
  if (!btn) return;
  activeTimelineFilter = btn.getAttribute("data-timelinefilter") || "all";
  $$("[data-timelinefilter]", timelineFilters).forEach(x => x.classList.toggle("active", x === btn));
  renderTimeline(activeClientDetail?.activities || []);
});

btnClearTimeline?.addEventListener("click", () => {
  if (!activeClientId) return;
  if (LEADS_ONLY){
    toast("Timeline clear is disabled in Leads-only mode.");
    return;
  }
  if (!confirm("Clear this client activity timeline?")) return;

  postJson("/Clients/ClearActivities", { clientUserId: activeClientId })
    .then(() => {
      activeClientDetail = { ...(activeClientDetail || {}), activities: [] };
      renderTimeline([]);
      dSaved.textContent = "Timeline cleared ✔";
      toast("Timeline cleared");
    })
    .catch(err => {
      console.error(err);
      toast("Timeline clear failed.");
    });
});

btnSaveLocal?.addEventListener("click", () => {
  if (!activeClientId) return;
  const row = rows.find(r => r.dataset.clientId === activeClientId);
  if (!row) return;

  saveQuickViewForRow(row, buildLeadQuickViewOverrides(), "Saved ✔").then(() => {
    toast("Saved");
    dMentionNote.value = "";
    maybeNotifyImmediateForDue(activeClientId);
  }).catch(err => {
    console.error(err);
    toast(err?.message || "Save failed.");
  });
});

btnResetLocal?.addEventListener("click", () => {
  if (!activeClientId) return;
  if (!confirm("Reload the saved client CRM data from the server?")) return;
  const row = rows.find(r => r.dataset.clientId === activeClientId);
  if (row) openDrawerForRow(row);
});

/* ========= Row Hotkeys ========= */
rows.forEach(r => {
  r.tabIndex = 0;
  r.addEventListener("keydown", (e) => {
    if (e.key === "Enter") openDrawerForRow(r);
  });
});

/* ========= Table View Row Open (full-row hit area) ========= */
if (tableView){
  tableView.addEventListener("click", (e) => {
    if (e.target.closest("[data-open-row]")) return; // keep existing delegated handler, avoid double-fetch
    if (e.target.closest("a, button, input, select, textarea, label, .menu, .kebab")) return;
    const row = e.target.closest(".client-row");
    if (row) openDrawerForRow(row);
  });

  tableView.addEventListener("keydown", (e) => {
    if (e.key !== "Enter") return;
    if (e.target.classList?.contains("client-row")) return; // row has its own key handler
    if (e.target.closest("input, select, textarea, button, a, label")) return;
    const row = e.target.closest(".client-row");
    if (row) openDrawerForRow(row);
  });
}

btnBulkEdit?.addEventListener("click", () => {
  if (!getCheckedRows().length) return toast("Select at least one contact.");
  openModal(bulkModal);
});

btnRunBulk?.addEventListener("click", async () => {
  const selected = getCheckedRows();
  if (!selected.length) return toast("Select at least one contact.");
  if (LEADS_ONLY){
    toast("Bulk update is disabled in Leads-only mode.");
    return;
  }
  try{
    const result = await postJson("/Clients/BulkUpdate", {
      clientUserIds: selected.map(r => r.dataset.clientId),
      pipelineStage: norm(bStage.value) || null,
      crmNextDate: norm(bNextDate.value) || null,
      crmNextText: norm(bNextText.value) || null,
      crmPriority: norm(bPriority.value) || null,
      crmTags: norm(bTags.value) || null,
      sharedNote: norm(bSharedNote.value) || null,
      waitingOn: norm(bWaitingOn.value) || null
    });
    selected.forEach(r => {
      if (norm(bStage.value)) r.dataset.sPipeline = norm(bStage.value);
      if (norm(bNextDate.value)) r.dataset.sNextdate = norm(bNextDate.value);
      if (norm(bNextText.value)) r.dataset.sNexttext = norm(bNextText.value);
      if (norm(bPriority.value)) r.dataset.sPriority = norm(bPriority.value);
      if (norm(bTags.value)) r.dataset.sTags = norm(bTags.value);
      if (norm(bWaitingOn.value)) r.dataset.sWaiting = norm(bWaitingOn.value);
      hydrateRow(r);
    });
    closeModal();
    renderAll();
    toast(`Updated ${result.updatedCount || selected.length} contacts`);
  }catch(err){
    console.error(err);
    toast(err?.message || "Bulk update failed.");
  }
});

btnImportSubmit?.addEventListener("click", async () => {
  if (!importFile?.files?.length){
    toast("Choose a CSV file");
    return;
  }

  const form = new FormData();
  form.append("__RequestVerificationToken", getAntiForgeryToken());
  form.append("file", importFile.files[0]);
  const chosenBucket = importBucketSelect?.value || pendingImportBucket || "MortgageProtection";
  form.append("bucket", chosenBucket);

  const originalText = btnImportSubmit.textContent;
  btnImportSubmit.disabled = true;
  btnImportSubmit.textContent = "Importing...";

  try{
    const res = await fetch("/Leads/Import", withDialHeaders({
      method: "POST",
      body: form,
      credentials: "include"
    }));

    const raw = await res.text();
    let data;
    try { data = JSON.parse(raw); }
    catch { data = { error: raw || "Import failed." }; }

    if (!res.ok || data.error){
      throw new Error(data.error || "Import failed.");
    }

    const imported = data.imported || 0;
    const updated = data.updated || 0;
    const skipped = data.skipped || 0;
    const firstError = Array.isArray(data.errors) && data.errors.length ? data.errors[0] : "";
    const message = `Added ${imported} • Updated ${updated} • Skipped ${skipped}`;

    const hasIssues = (!!firstError) || skipped > 0;
    const toastMsg = firstError ? `${message}. ${firstError}` : `${message}.`;
    if (importResult) importResult.textContent = toastMsg;
    toast(toastMsg, { error: hasIssues, persistent: hasIssues });

    if (imported > 0){
      setTimeout(() => window.location.reload(), 800);
    }
  }catch(err){
    if (importResult) importResult.textContent = err?.message || "Import failed.";
    toast(err?.message || "Import failed.", { error: true, persistent: true });
  }finally{
    btnImportSubmit.disabled = false;
    btnImportSubmit.textContent = originalText || "Import Leads";
  }
});

function renderCallTaskMode(){
  if (!callTaskBody) return;
  const queue = queueRows("callsnow").concat(queueRows("overdue").filter(r => !queueRows("callsnow").includes(r)));
  if (!queue.length){
    callTaskBody.innerHTML = `<div class="tiny">No urgent call tasks right now.</div>`;
    return;
  }

  callTaskBody.innerHTML = queue.slice(0, 20).map(row => `
    <div class="call-task">
      <div class="call-task-head">
        <div>
          <div class="call-task-name">${safeHtml(fullName(row))}</div>
          <div class="call-task-sub">${safeHtml(norm(row.dataset.phone) || "No phone")} • Last touch: ${safeHtml(norm(row.dataset.crmLastTouch) || "—")}</div>
          <div class="tiny" style="margin-top:6px;">${safeHtml(norm(row.dataset.crmPinnedBrief) || norm(row.dataset.crmNextText) || "No pinned brief yet.")}</div>
        </div>
        <span class="meta-chip">${safeHtml(pipelineLabel(norm(row.dataset.crmPipeline)))}</span>
      </div>
      <div class="call-task-actions">
        <button type="button" class="btn btn-gold" data-open-card="${safeHtml(row.dataset.clientId)}">Open</button>
        <a class="btn btn-ghost" href="tel:${safeHtml(norm(row.dataset.phone))}">Call</a>
        <button type="button" class="btn btn-ghost" data-taskoutcome="${safeHtml(row.dataset.clientId)}:Contacted">Mark Contacted</button>
        <button type="button" class="btn btn-ghost" data-taskoutcome="${safeHtml(row.dataset.clientId)}:Booked">Booked Meeting</button>
        <button type="button" class="btn btn-ghost" data-taskoutcome="${safeHtml(row.dataset.clientId)}:FollowUp">Follow Up</button>
        <button type="button" class="btn btn-ghost" data-taskoutcome="${safeHtml(row.dataset.clientId)}:NotInterested">Not Interested</button>
      </div>
    </div>
  `).join("");
}

btnCallTaskMode?.addEventListener("click", () => {
  renderCallTaskMode();
  openModal(callTaskModal);
});

btnMyDayCallTask?.addEventListener("click", () => {
  renderCallTaskMode();
  openModal(callTaskModal);
});

$$(".myday-tile").forEach(tile => {
  tile.addEventListener("click", () => {
    const q = tile.getAttribute("data-queue") || "";
    if (!q) return;
    window.location.href = `/Leads/Queue?queue=${encodeURIComponent(q)}`;
  });
});

btnMyDayBack?.addEventListener("click", () => {
  activeMyDayQueue = "";
  resetFilters();
  if (viewMode) viewMode.value = "pipeline";
  applyViewMode();
  currentPage = 1;
  renderAll();
  myDayQueue?.scrollIntoView({ behavior: "smooth", block: "start" });
});

$$(".outcome-btn").forEach(btn => {
  btn.addEventListener("click", async () => {
    if (!activeClientId) return;
    const outcome = btn.getAttribute("data-outcome");
    const row = rows.find(r => r.dataset.clientId === activeClientId);
    if (!row) return;

    const outcomeStageMap = {
      Contacted: "Contacted",
      Booked: "Booked",
      FollowUp: "FollowUp",
      NeedsDocs: "NeedsDocs",
      PolicyPlaced: "PolicyPlaced",
      NotInterested: "NotInterested",
      Nurture: "Nurture"
    };

    // If outcome targets a non-lead bucket, move it client-side without server ApplyOutcome.
    if (outcomeStageMap[outcome]){
      try {
        await saveQuickViewForRow(row, {
          pipelineStage: outcomeStageMap[outcome],
          crmLastTouch: todayISO(),
          crmNextText: norm(dActNote.value)
        }, `${btn.textContent} ✔`);
        dActNote.value = "";
        renderAll();
        toast(`${btn.textContent} applied`);
      } catch (err){
        console.error(err);
        toast(err?.message || "Outcome failed.");
      }
      return;
    }

    try{
      const response = await postJson("/Leads/ApplyOutcome", {
        clientUserId: activeClientId,
        outcomeCode: outcome,
        customNote: norm(dActNote.value),
        meetingLocation: norm(dMeetingLocation.value),
        zoomJoinUrl: norm(dZoomJoinUrl.value),
        usePersonalZoomLink: !!dUsePersonalZoomLink.checked,
        meetingTime: norm(dMeetingTime.value),
        meetingDurationMinutes: parseInt(dMeetingDuration.value || "30", 10) || 30
      });
      const data = response.payload || response;
      const row = rows.find(r => r.dataset.clientId === activeClientId);
      if (row){
        await saveQuickViewForRow(row, {
          crmStatus: data.crmStatus,
          crmPriority: data.crmPriority,
          crmLastTouch: data.crmLastTouch,
          crmNextDate: data.crmNextDate,
          crmNextText: data.crmNextText,
          pipelineStage: data.pipelineStage,
          waitingOn: data.waitingOn,
          meetingLocation: data.meetingLocation,
          zoomJoinUrl: data.zoomJoinUrl,
          usePersonalZoomLink: data.usePersonalZoomLink,
          meetingTime: data.meetingTime,
          meetingDurationMinutes: data.meetingDurationMinutes,
          pinnedBrief: data.pinnedBrief,
          watchers: (data.collaboration?.watchers || []).join(", "),
          mentionNote: ""
        }, `${btn.textContent} applied ✔`);
      }
      activeClientDetail = data;
      renderTimeline(data.activities || []);
      renderMentionNotes(data.collaboration?.mentionNotes || []);
      dOutcomeSuggestion.textContent = `Suggested next step: ${response.suggestion?.nextDate || data.crmNextDate || ""} • ${response.suggestion?.nextText || data.crmNextText || ""}`;
      dActNote.value = "";
      toast(`${btn.textContent} applied`);
    }catch(err){
      console.error(err);
      toast(err?.message || "Outcome failed.");
    }
  });
});

/* ========= Cards View ========= */
function nextPillState(nextDate, nextText){
  const cls =
    isOverdue(nextDate) ? "overdue" :
    isToday(nextDate) ? "today" :
    isSoon(nextDate) ? "soon" :
    (!nextDate && !nextText) ? "none" : "";

  const label = (!nextDate && !nextText) ? "No next action" : `${nextDate || "No date"} • ${nextText || "Next action"}`;
  return { cls, label };
}

function cardTags(raw){
  return (raw || "")
    .split(",")
    .map(x => norm(x))
    .filter(Boolean)
    .slice(0, 3);
}

function renderPipelineNav(filteredRows){
  if (!pipelineStageNav) return;

  const optionHtml = pipelineStages.map(stage => {
    const count = filteredRows.filter(r => norm(r.dataset.crmPipeline) === stage.key).length;
    const selected = pipelineNavSelectedStage === stage.key ? "selected" : "";
    return `<option value="${safeHtml(stage.key)}" ${selected}>${safeHtml(stage.label)} (${count})</option>`;
  }).join("");

  pipelineStageNav.innerHTML = `
    <div class="pipeline-nav-shell">
      <div class="pipeline-nav-toolbar">
        <div class="pipeline-nav-copy">
          <div class="pipeline-nav-label">Bucket Selector</div>
          <div class="pipeline-nav-title-row">
          <div class="pipeline-nav-name">${safeHtml(pipelineFocusStage || pipelineNavSelectedStage || "Select A Bucket")}</div>
          <span class="pipeline-nav-count">${filteredRows.length}</span>
        </div>
      </div>
      <div class="pipeline-nav-actions">
          <select class="select pipeline-nav-select" id="pipelineNavSelect" aria-label="Select pipeline bucket">
            <option value="" ${pipelineNavSelectedStage ? "" : "selected"} disabled>--SELECT--</option>
            ${optionHtml}
          </select>
          <button type="button" class="btn btn-ghost pipeline-nav-reset" id="pipelineNavReset" ${pipelineNavSelectedStage || pipelineFocusStage ? "" : "disabled"}>All Buckets</button>
        </div>
      </div>
      <div class="pipeline-nav-note">Use the dropdown to jump into a bucket. Click the back button in the board to return.</div>
    </div>
  `;

  const navSelect = pipelineStageNav.querySelector("#pipelineNavSelect");
  navSelect?.addEventListener("change", (e) => {
    pipelineNavSelectedStage = e.target.value || "";
    pipelineFocusStage = pipelineNavSelectedStage;
    if (viewMode) viewMode.value = "pipeline";
    applyViewMode();
    renderAll();
  });

  const navReset = pipelineStageNav.querySelector("#pipelineNavReset");
  navReset?.addEventListener("click", () => {
    pipelineNavSelectedStage = "";
    pipelineNavSearchTerm = "";
    pipelineFocusStage = "";
    renderAll();
  });
}

function renderLaneCards(rowsForStage){
  if (!rowsForStage.length){
    return `<div class="pipeline-empty">No contacts in this stage right now.</div>`;
  }

  const formatPhone = (raw) => {
    const digits = (raw || "").replace(/\D/g, "");
    if (digits.length === 10){
      return `(${digits.slice(0,3)}) ${digits.slice(3,6)}-${digits.slice(6)}`;
    }
    return raw || "";
  };

  return rowsForStage.map(r => {
    const name = fullName(r);
    const email = norm(r.dataset.email);
    const phone = norm(r.dataset.phone);
    const stage = norm(r.dataset.crmPipeline);
    const phoneDisplay = formatPhone(phone);
    const phoneDigits = phone.replace(/\D/g, "");
    const shortPhone = phoneDigits ? `···${phoneDigits.slice(-4)}` : "";
    const displayName = name || (phone ? `Lead • ${shortPhone}` : `Lead • ${r.dataset.clientId.slice(0, 6)}`);
    const callCount = norm(r.dataset.sAttemptslife || r.dataset.crmAttemptsLife || "0");
    const paidAmount = Number(r.dataset.prodPaid || r.dataset.paid || 0);
    const issuedAmount = Number(r.dataset.prodIssued || 0);
    const submittedAmount = Number(r.dataset.prodSubmitted || 0);
    const prodBadgeHtml = renderPipelineProdBadge({
      paid: paidAmount,
      issued: issuedAmount,
      submitted: submittedAmount
    });
    const prodBadge = `<div class="lead-prod-badge ${prodBadgeHtml ? "" : "hidden"}" data-prod-card data-card-prod="${safeHtml(r.dataset.clientId)}">${prodBadgeHtml}</div>`;

    return `
      <article class="client-card ${pipelineBadgeClass(stage)}"
               style="overflow:visible;"
               draggable="true"
               data-cardid="${safeHtml(r.dataset.clientId)}">
        <div class="client-card-head" style="position:relative;">
          <div class="client-card-main">
            <h3 class="cc-name" data-open-card="${safeHtml(r.dataset.clientId)}">${safeHtml(displayName)}</h3>
              <div class="cc-sub cc-sub-primary">${phone ? `<a class=\"link link-phone\" style=\"color:#c48d02;font-weight:700;\" href=\"tel:${safeHtml(phone)}\" data-call-link=\"${safeHtml(r.dataset.clientId)}\" data-call-phone=\"${safeHtml(phone)}\">${safeHtml(phoneDisplay)}</a>` : "No phone"}</div>
              <div class="cc-sub">${renderEmailLinkHtml(email, "color:#7a7a7a;opacity:0.78;")}</div>
          </div>
          <div class="client-card-actions actions" style="gap:6px; flex-wrap:wrap; align-items:center;">
            <span class="pill" style="border:1px solid #dc2626;color:#b91c1c;font-weight:700;padding:3px 7px;font-size:12px;">Called: ${safeHtml(callCount)}</span>
            ${phone ? `<button type="button" class="btn btn-ghost" style="padding:5px 8px;font-size:12px;" data-call-lead="${safeHtml(r.dataset.clientId)}" data-call-phone="${safeHtml(phone)}">Call</button>` : ""}
            ${phone ? `<button type="button" class="btn btn-ghost" style="padding:5px 8px;font-size:12px;" data-text-menu="${safeHtml(r.dataset.clientId)}">Text ▾</button>` : ""}
            <button type="button"
                    class="btn btn-gold openCard"
                    data-open-card="${safeHtml(r.dataset.clientId)}"
                    title="Open Quick View"
                    style="min-width:90px; padding:5px 8px;font-size:12px;">
              Quick View
            </button>
          </div>
          ${prodBadge}
        </div>
      </article>
    `;
  }).join("");
}

function renderCards(filteredRows){
  if (!pipelineBoard || !cardsView) return;

  const focusMeta = pipelineFocusStage ? pipelineMeta(pipelineFocusStage) : (pipelineNavSelectedStage ? pipelineMeta(pipelineNavSelectedStage) : null);
  const lanes = focusMeta ? [focusMeta] : pipelineStages;

  renderPipelineNav(filteredRows);

  pipelineBoard.classList.toggle("focused", !!focusMeta);
  pipelineFocusBar?.classList.toggle("active", !!focusMeta);

  if (pipelineBoardCount){
    pipelineBoardCount.textContent = `${filteredRows.length} active card${filteredRows.length === 1 ? "" : "s"}`;
  }
  if (pipelineTotalCards){
    pipelineTotalCards.textContent = `${filteredRows.length} cards`;
  }
  if (pipelineFocusPill){
    pipelineFocusPill.textContent = focusMeta ? `${focusMeta.label} bucket` : "All buckets";
  }

  if (focusMeta){
    pipelineFocusTitle.textContent = `${focusMeta.label} Review`;
    pipelineFocusSub.textContent = focusMeta.note;
  }

  pipelineBoard.innerHTML = lanes.map(stage => {
    const stageRows = orderedStageRows(stage.key, filteredRows.filter(r => norm(r.dataset.crmPipeline) === stage.key));
    return `
      <section class="pipeline-lane ${stage.className}" data-dropstage="${stage.key}">
        <div class="pipeline-lane-head">
          <div>
            <h3 class="pipeline-lane-title">${safeHtml(stage.label)}</h3>
            <div class="pipeline-lane-note">${safeHtml(stage.note)}</div>
          </div>
          <div class="pipeline-lane-meta" style="display:flex;gap:8px;align-items:center;flex-wrap:wrap;">
            <span class="pipeline-lane-count">${stageRows.length} contact${stageRows.length === 1 ? "" : "s"}</span>
            ${productBuckets.has(stage.key) ? `<button type="button" class="btn btn-gold" data-import-bucket="${stage.key}">Import CSV</button>` : ""}
          </div>
        </div>
        <div class="pipeline-lane-body" data-dropzone="${stage.key}">
          ${renderLaneCards(stageRows)}
        </div>
      </section>
    `;
  }).join("");

  // ensure production badges in cards reflect latest row data
  filteredRows.forEach(r => updatePipelineCardProduction(r.dataset.clientId));

  wireOpenHandlers();
}

async function saveQuickViewForRow(row, overrides, successMessage){
  const currentLeadType = resolveQuickViewLeadType(row, activeClientDetail, overrides?.pipelineStage || dPipelineStage?.value);
  const hideMortgageLender = isLifeOrFinalExpenseLeadType(currentLeadType);

  const payload = {
    clientUserId: row.dataset.clientId,
    firstName: dFirst?.value || row.dataset.first || "",
    lastName: dLast?.value || row.dataset.last || "",
    email: dEmailInput?.value || "",
    phone: dPhoneInput?.value || "",
    phone2: dPhone2Input?.value || "",
    dob: dDob?.value || null,
    age: dAge?.value || "",
    gender: dGender?.value || "",
    addressLine: dAddress?.value || "",
    city: dCity?.value || "",
    state: dState?.value || "",
    county: dCounty?.value || "",
    zipCode: dZip?.value || "",
    btc: dBtc?.value || "",
    mortgageLender: hideMortgageLender ? "" : (dLender?.value || ""),
    loanAmount: dLoanAmount?.value || "",
    crmStatus: (overrides?.crmStatus ?? norm(row.dataset.crmStatus)) || "Lead",
    crmPriority: (overrides?.crmPriority ?? norm(row.dataset.crmPriority)) || "Normal",
    crmLastTouch: (overrides?.crmLastTouch ?? norm(row.dataset.crmLastTouch)) || null,
    crmNextDate: (overrides?.crmNextDate ?? norm(row.dataset.crmNextDate)) || null,
    crmNextText: overrides?.crmNextText ?? norm(row.dataset.crmNextText),
    crmTags: overrides?.crmTags ?? norm(row.dataset.crmTags),
    agentNotes: overrides?.agentNotes ?? norm(row.dataset.crmNotes),
    pipelineStage: normalizePipelineStageValue(overrides?.pipelineStage ?? norm(row.dataset.crmPipeline), "MortgageProtection"),
    meetingLocation: overrides?.meetingLocation ?? norm(row.dataset.sMeetingLocation),
    zoomJoinUrl: overrides?.zoomJoinUrl ?? norm(row.dataset.sZoom),
    usePersonalZoomLink: overrides?.usePersonalZoomLink ?? ((row.dataset.sUsezoom || "false") === "true"),
    meetingTime: (overrides?.meetingTime ?? norm(row.dataset.sMeetingTime)) || "09:00",
    meetingDurationMinutes: (overrides?.meetingDurationMinutes ?? parseInt(row.dataset.sMeetingDuration || "30", 10)) || 30,
    waitingOn: overrides?.waitingOn ?? norm(row.dataset.crmWaitingOn),
    pinnedBrief: overrides?.pinnedBrief ?? norm(row.dataset.crmPinnedBrief),
    docIdReceived: overrides?.docIdReceived ?? !!dDocIdReceived?.checked,
    docAppSent: overrides?.docAppSent ?? !!dDocAppSent?.checked,
    docAppSigned: overrides?.docAppSigned ?? !!dDocAppSigned?.checked,
    docPolicyDelivered: overrides?.docPolicyDelivered ?? !!dDocPolicyDelivered?.checked,
    docReviewBooked: overrides?.docReviewBooked ?? !!dDocReviewBooked?.checked,
    watchers: overrides?.watchers ?? norm(row.dataset.crmWatchers),
    mentionNote: overrides?.mentionNote ?? ""
  };

  const response = await postJson("/Leads/SaveQuickView", payload);
  const data = response.payload || response;
  const payloadNextText = payload.crmNextText ?? "";
  const resolvedAgentNotes = data.agentNotes ?? data.crmNotes ?? payload.agentNotes ?? row.dataset.sNotes ?? "";
  const resolvedNextDate = data.crmNextDate ?? payload.crmNextDate ?? row.dataset.sNextdate ?? "";
  const resolvedTags = data.crmTags ?? payload.crmTags ?? row.dataset.sTags ?? "";
  let resolvedNextText = data.crmNextText ?? payloadNextText ?? row.dataset.sNexttext ?? "";
  if ((data.crmNotes ?? null) !== null && data.crmNextText === data.crmNotes && payloadNextText && payloadNextText !== data.crmNotes){
    resolvedNextText = payloadNextText;
  }
  row.dataset.sStatus = data.crmStatus || "Lead";
  row.dataset.sPriority = data.crmPriority || "Normal";
  row.dataset.sLasttouch = data.crmLastTouch || "";
  row.dataset.sNextdate = resolvedNextDate || "";
  row.dataset.sNexttext = resolvedNextText || "";
  row.dataset.sTags = resolvedTags || "";
  row.dataset.sNotes = resolvedAgentNotes || "";
  row.dataset.sPipeline = normalizePipelineStageValue(data.pipelineStage, "MortgageProtection");
  row.dataset.sMeetingLocation = data.meetingLocation || "";
  row.dataset.sZoom = data.zoomJoinUrl || "";
  row.dataset.sUsezoom = data.usePersonalZoomLink ? "true" : "false";
  row.dataset.sMeetingTime = data.meetingTime || "09:00";
  row.dataset.sMeetingDuration = String(data.meetingDurationMinutes || 30);
  row.dataset.sWaiting = data.waitingOn || "WaitingOnAgent";
  row.dataset.sPinnedbrief = data.pinnedBrief || "";
  row.dataset.sStageentered = (data.stageEnteredUtc || "").toString().slice(0, 10) || todayISO();
  row.dataset.sAttemptstoday = String(data.attemptsToday ?? row.dataset.sAttemptstoday ?? 0);
  row.dataset.sAttemptsweek = String(data.attemptsThisWeek ?? row.dataset.sAttemptsweek ?? 0);
  row.dataset.sAttemptsmonth = String(data.attemptsThisMonth ?? row.dataset.sAttemptsmonth ?? 0);
  row.dataset.sAttemptsyear = String(data.attemptsThisYear ?? row.dataset.sAttemptsyear ?? 0);
  row.dataset.sAttemptslife = String(data.attemptsLifetime ?? row.dataset.sAttemptslife ?? 0);
  row.dataset.sChannel = data.lastContactChannel || row.dataset.sChannel || "";
  row.dataset.sDoccount = String(data.docChecklist?.completedCount || 0);
  row.dataset.sOwner = data.collaboration?.owner || "";
  row.dataset.sWatchers = (data.collaboration?.watchers || []).join(", ");
  row.dataset.first = data.firstName || row.dataset.first || "";
  row.dataset.last = data.lastName || row.dataset.last || "";
  row.dataset.email = data.email || row.dataset.email || "";
  row.dataset.phone = data.phone || row.dataset.phone || "";
  row.dataset.phone2 = data.phone2 || row.dataset.phone2 || "";
  row.dataset.originalLeadType = normalizeOriginalLeadTypeValue(data.originalLeadType) || row.dataset.originalLeadType || "";
  row.dataset.dob = data.dob || "";
  row.dataset.age = data.age || row.dataset.age || "";
  row.dataset.gender = data.gender || "";
  row.dataset.addressLine = data.addressLine || "";
  row.dataset.city = data.city || "";
  row.dataset.state = data.state || "";
  row.dataset.county = data.county || "";
  row.dataset.zipCode = data.zipCode || "";
  row.dataset.btc = data.btc || row.dataset.btc || "";
  row.dataset.mortgageLender = data.mortgageLender || "";
  row.dataset.loanAmount = data.loanAmount || "";
  hydrateRow(row);

  if (liveSync){
    liveSync.sendUpdate({
      leadId: row.dataset.clientId,
      pipelineStage: row.dataset.sPipeline,
      crmNextDate: row.dataset.sNextdate,
      crmNextText: row.dataset.sNexttext,
      crmStatus: row.dataset.sStatus,
      crmPriority: row.dataset.sPriority,
      attemptsLifetime: row.dataset.sAttemptslife,
      firstName: row.dataset.first,
      lastName: row.dataset.last,
      phone: row.dataset.phone,
      email: row.dataset.email
    });
  }

  const nameCell = $(".name.open-drawer", row);
  if (nameCell) nameCell.textContent = `${row.dataset.first || ""} ${row.dataset.last || ""}`.trim();
  if (dName) dName.textContent = `${row.dataset.first || ""} ${row.dataset.last || ""}`.trim() || "Lead";
  syncDrawerEmailDisplay(row.dataset.email);
  if (dPhone) dPhone.textContent = row.dataset.phone || "No phone";
  if (dPhone2Input) dPhone2Input.value = row.dataset.phone2 || dPhone2Input.value;
  if (dAge) dAge.value = row.dataset.age || dAge.value;
  if (dBtc) dBtc.value = row.dataset.btc || dBtc.value;

  if (activeClientId === row.dataset.clientId){
    activeClientDetail = {
      ...(activeClientDetail || {}),
      ...data,
      crmNextDate: resolvedNextDate || null,
      crmNextText: resolvedNextText,
      crmTags: resolvedTags,
      agentNotes: resolvedAgentNotes
    };
    dStatus.value = data.crmStatus || dStatus.value;
    dPriority.value = data.crmPriority || dPriority.value;
    dLastTouch.value = data.crmLastTouch || dLastTouch.value;
    setDrawerNextActionDate(resolvedNextDate || dNextDate.value);
    dNextText.value = resolvedNextText || dNextText.value;
    dTags.value = resolvedTags || dTags.value;
    dNotes.value = resolvedAgentNotes || dNotes.value;
    if (dFirst) dFirst.value = data.firstName || dFirst.value || "";
    if (dLast) dLast.value = data.lastName || dLast.value || "";
    if (dEmailInput) dEmailInput.value = data.email || dEmailInput.value;
    if (dPhoneInput) dPhoneInput.value = data.phone || dPhoneInput.value;
    if (dPhone2Input) dPhone2Input.value = data.phone2 || dPhone2Input.value;
    if (dDob) dDob.value = data.dob || dDob.value;
    if (dAge) dAge.value = data.age || dAge.value;
    if (dGender) dGender.value = data.gender || dGender.value;
    if (dAddress) dAddress.value = data.addressLine || dAddress.value;
    if (dCity) dCity.value = data.city || dCity.value;
    if (dState) dState.value = data.state || dState.value;
    if (dCounty) dCounty.value = data.county || dCounty.value;
    if (dZip) dZip.value = data.zipCode || dZip.value;
    if (dBtc) dBtc.value = data.btc || dBtc.value;
    if (dLender) dLender.value = data.mortgageLender || dLender.value;
    if (dLoanAmount) dLoanAmount.value = data.loanAmount || dLoanAmount.value;
    dPipelineStage.value = normalizePipelineStageValue(data.pipelineStage, dPipelineStage.value || "MortgageProtection");
    applyQuickViewContactProfileLabels(row, data, dPipelineStage.value);
    dMeetingLocation.value = data.meetingLocation || dMeetingLocation.value;
    dZoomJoinUrl.value = data.zoomJoinUrl || dZoomJoinUrl.value;
    dUsePersonalZoomLink.checked = !!data.usePersonalZoomLink;
    dMeetingTime.value = data.meetingTime || dMeetingTime.value;
    dMeetingDuration.value = String(data.meetingDurationMinutes || dMeetingDuration.value || 30);
    dWaitingOn.value = data.waitingOn || dWaitingOn.value;
    dPinnedBrief.value = data.pinnedBrief || dPinnedBrief.value;
    if (dName) dName.textContent = `${dFirst?.value || ""} ${dLast?.value || ""}`.trim() || "Lead";
    dAssignedOwner.value = data.collaboration?.owner || dAssignedOwner.value;
    dWatchers.value = (data.collaboration?.watchers || []).join(", ");
    if (data.docChecklist){
      dDocIdReceived.checked = !!data.docChecklist.idReceived;
      dDocAppSent.checked = !!data.docChecklist.appSent;
      dDocAppSigned.checked = !!data.docChecklist.appSigned;
      dDocPolicyDelivered.checked = !!data.docChecklist.policyDelivered;
      dDocReviewBooked.checked = !!data.docChecklist.reviewBooked;
    }
    dStageAge.textContent = `Stage Age: ${data.stageAgeDays || stageAgeDays(row)}d`;
    dAttempts.textContent = `Attempts: ${data.attemptsToday || 0} today • ${data.attemptsThisWeek || 0} week • ${data.attemptsLifetime || 0} total`;
    dWaitingOnPill.textContent = data.waitingOnLabel || waitingLabel(data.waitingOn || "WaitingOnAgent");
    renderMentionNotes(data.collaboration?.mentionNotes || []);
    refreshLeadOverviewSummary();
    dSaved.textContent = successMessage || "Saved ✔";
  }

  updateCallMetrics();
  renderAll();
  return data;
}

btnBoardBack?.addEventListener("click", () => {
  pipelineFocusStage = "";
  pipelineNavSelectedStage = "";
  pipelineNavSearchTerm = "";
  renderAll();
});

btnPipeOverdue?.addEventListener("click", () => {
  if (attentionFilter) attentionFilter.value = "overdue";
  if (viewMode) viewMode.value = "pipeline";
  applyViewMode();
  renderAll();
});

btnPipeNeeds?.addEventListener("click", () => {
  if (attentionFilter) attentionFilter.value = "needs";
  if (viewMode) viewMode.value = "pipeline";
  applyViewMode();
  renderAll();
});

btnPipeMeetings?.addEventListener("click", () => {
  applyPreset("meetingstoday");
});

btnPipeTable?.addEventListener("click", () => {
  if (viewMode) viewMode.value = "table";
  applyViewMode();
  renderAll();
});

btnPipeReset?.addEventListener("click", () => {
  resetFilters();
  if (viewMode) viewMode.value = "pipeline";
  applyViewMode();
  renderAll();
});

pipelineStageNav?.addEventListener("click", (e) => {
  const stage = e.target.closest("[data-pipeline-nav]")?.getAttribute("data-pipeline-nav");
  if (!stage) return;
  pipelineFocusStage = stage;
  pipelineNavSelectedStage = stage;
  if (viewMode) viewMode.value = "pipeline";
  applyViewMode();
  renderAll();
});

pipelineBoard?.addEventListener("click", (e) => {
  const stageBtn = e.target.closest("[data-pipeline-nav]")?.getAttribute("data-pipeline-nav");
  if (stageBtn){
    pipelineFocusStage = stageBtn;
    pipelineNavSelectedStage = stageBtn;
    if (viewMode) viewMode.value = "pipeline";
    applyViewMode();
    renderAll();
    return;
  }

  const openId = e.target.closest("[data-open-card]")?.getAttribute("data-open-card");
  if (openId){
    const row = rows.find(r => r.dataset.clientId === openId);
    if (row) openDrawerForRow(row);
    return;
  }

  const pipelineCard = e.target.closest(".client-card");
  if (pipelineCard && !e.target.closest("[data-kebab], .menu, a, button, input, select, textarea, label")){
    const cardId = pipelineCard.getAttribute("data-cardid");
    const row = rows.find(r => r.dataset.clientId === cardId);
    if (row) openDrawerForRow(row);
    return;
  }

  const copyId = e.target.closest("[data-copy-card]")?.getAttribute("data-copy-card");
  if (copyId){
    const row = rows.find(r => r.dataset.clientId === copyId);
    if (row) copyText(`${fullName(row)}\n${norm(row.dataset.email)}\n${norm(row.dataset.phone)}`.trim());
    return;
  }

  const importBucket = e.target.closest("[data-import-bucket]")?.getAttribute("data-import-bucket");
  if (importBucket){
    pendingImportBucket = importBucket;
    if (importBucketSelect) importBucketSelect.value = importBucket;
    if (importResult) importResult.textContent = `Target bucket: ${pipelineLabel(importBucket)}`;
    updateImportCsvHelp(importBucket);
    if (importFile) importFile.value = "";
    openModal(importModal);
    return;
  }

  const taskOutcome = e.target.closest("[data-taskoutcome]")?.getAttribute("data-taskoutcome");
  if (taskOutcome){
    const [clientId, outcome] = taskOutcome.split(":");
    const row = rows.find(r => r.dataset.clientId === clientId);
    if (row){
      openDrawerForRow(row).then(() => {
        const btn = $$(`.outcome-btn`).find(x => x.getAttribute("data-outcome") === outcome);
        btn?.click();
      });
    }
  }
});

pipelineBoard?.addEventListener("dragstart", (e) => {
  const card = e.target.closest(".client-card");
  if (!card) return;
  draggingClientId = card.getAttribute("data-cardid");
  card.classList.add("dragging");
  e.dataTransfer.effectAllowed = "move";
  e.dataTransfer.setData("text/plain", draggingClientId || "");
});

pipelineBoard?.addEventListener("dragend", (e) => {
  draggingClientId = null;
  e.target.closest(".client-card")?.classList.remove("dragging");
  $$(".pipeline-lane.drag-over", pipelineBoard).forEach(el => el.classList.remove("drag-over"));
});

pipelineBoard?.addEventListener("dragover", (e) => {
  const lane = e.target.closest(".pipeline-lane");
  if (!lane) return;
  e.preventDefault();
  e.dataTransfer.dropEffect = "move";
  $$(".pipeline-lane.drag-over", pipelineBoard).forEach(el => { if (el !== lane) el.classList.remove("drag-over"); });
  lane.classList.add("drag-over");
});

pipelineBoard?.addEventListener("dragleave", (e) => {
  const lane = e.target.closest(".pipeline-lane");
  if (!lane) return;
  if (!lane.contains(e.relatedTarget)) lane.classList.remove("drag-over");
});

pipelineBoard?.addEventListener("drop", async (e) => {
  const lane = e.target.closest(".pipeline-lane");
  if (!lane) return;
  e.preventDefault();
  lane.classList.remove("drag-over");

  const targetStage = lane.getAttribute("data-dropstage");
  const clientId = draggingClientId || e.dataTransfer.getData("text/plain");
  if (!targetStage || !clientId) return;

  const row = rows.find(r => r.dataset.clientId === clientId);
  if (!row) return;

  const sourceStage = norm(row.dataset.crmPipeline);
  // Prevent moving between lead buckets (product buckets cannot be moved into other lead buckets)
  if (productBuckets.has(sourceStage) && productBuckets.has(targetStage) && sourceStage !== targetStage){
    toast("Lead buckets cannot move into other lead buckets.");
    return;
  }
  const zone = lane.querySelector("[data-dropzone]");
  const cards = zone ? Array.from(zone.querySelectorAll(".client-card")) : [];
  const beforeCard = cards.find(c => {
    const rect = c.getBoundingClientRect();
    return e.clientY < rect.top + rect.height / 2;
  });
  const beforeId = (beforeCard && beforeCard.dataset.cardid !== clientId) ? beforeCard.dataset.cardid : "";

  const db = { ...pipelineOrder };
  const sourceOrder = laneOrderFromDom(sourceStage).filter(id => id !== clientId);
  const targetOrder = (sourceStage === targetStage ? sourceOrder.slice() : laneOrderFromDom(targetStage)).filter(id => id !== clientId);

  const insertIdx = beforeId ? targetOrder.indexOf(beforeId) : -1;
  if (insertIdx >= 0) targetOrder.splice(insertIdx, 0, clientId);
  else targetOrder.push(clientId);

  db[sourceStage] = sourceOrder;
  db[targetStage] = targetOrder;
  savePipelineOrder(db);

  if (sourceStage === targetStage){
    await persistOrder(targetStage, targetOrder);
    toast("Priority reordered");
    renderAll();
    return;
  }

  try{
    await saveQuickViewForRow(row, { pipelineStage: targetStage }, `Moved to ${pipelineLabel(targetStage)} ✔`);
    // Persist both source and target ordering after move
    await persistOrder(targetStage, targetOrder);
    await persistOrder(sourceStage, sourceOrder);
    toast(`Moved to ${pipelineLabel(targetStage)}`);
  }catch(err){
    console.error(err);
    toast(err?.message || "Stage move failed.");
  }
});

pipelineBoard?.addEventListener("click", (e) => {
  const textBtn = e.target.closest("[data-text-menu]");
  if (textBtn){
    const id = textBtn.getAttribute("data-text-menu");
    const row = rows.find(r => r.dataset.clientId === id);
    if (!row) return;
    closeLeadCommAuth();

    let menu = document.getElementById("textMenuGlobal");
    if (!menu){
      menu = document.createElement("div");
      menu.id = "textMenuGlobal";
      menu.style.position = "fixed";
      menu.style.zIndex = "99999";
      menu.style.background = "#fff";
      menu.style.border = "1px solid rgba(0,0,0,.14)";
      menu.style.boxShadow = "0 16px 32px rgba(0,0,0,.22)";
      menu.style.borderRadius = "10px";
      menu.style.minWidth = "180px";
      menu.style.display = "none";
      menu.style.padding = "4px 0";
      menu.innerHTML = `
        <button type="button" class="btn btn-ghost" style="width:100%; text-align:left; padding:10px 12px; font-size:12px;" data-template="MortgageProtection">Mortgage Protection</button>
      `;
      document.body.appendChild(menu);

      menu.addEventListener("click", (ev) => {
        const btn = ev.target.closest("[data-template]");
        if (!btn) return;
        const clientId = menu.dataset.clientId;
        const tmpl = btn.getAttribute("data-template") || "MortgageProtection";
        const r = rows.find(x => x.dataset.clientId === clientId);
        if (r){
          showLeadCommAuth(btn, {
            action: "text",
            row: r,
            phone: norm(r.dataset.phone),
            templateKey: tmpl
          });
        } else {
          closeAllTextMenus(null);
        }
        menu.style.display = "none";
        menu.dataset.clientId = "";
        ev.stopPropagation();
      });
    }

    const rect = textBtn.getBoundingClientRect();
    menu.style.left = `${rect.left}px`;
    menu.style.top = `${rect.bottom + 6}px`;
    menu.dataset.clientId = id;
    closeAllTextMenus(id);
    menu.style.display = "block";
    e.stopPropagation();
    return;
  }

  // clicking elsewhere closes menus
  if (!e.target.closest('#textMenuGlobal')) closeAllTextMenus(null);

  const callBtn = e.target.closest('[data-call-lead]');
  if (callBtn){
    const clientId = callBtn.getAttribute('data-call-lead');
    const phone = callBtn.getAttribute('data-call-phone');
    const row = rows.find(r => r.dataset.clientId === clientId);
    if (row && phone){
      showLeadCommAuth(callBtn, { action: "call", row, phone });
    }
    e.stopPropagation();
    return;
  }
});

/* ========= Columns Modal ========= */
function openModal(el){
  modalBackdrop.classList.add("open");
  el.classList.add("open");
}
function closeModal(){
  modalBackdrop.classList.remove("open");
  [colsModal, shortcutsModal, remindersModal, cmdModal, bulkModal, callTaskModal, importModal].forEach(m => m.classList.remove("open"));
}

$("#btnCols")?.addEventListener("click", () => {
  const cols = ["select","name","email","phone","portal","status","stage","next","touch","actions"];
  const saved = loadJSON(LS_COLS, {});
  colsBody.innerHTML = cols.map(c => {
    const on = c === "select" ? true : saved[c] !== false;
    return `
      <div style="display:flex; align-items:center; justify-content:space-between; gap:12px; padding:10px 0; border-bottom:1px solid rgba(18,18,18,.08);">
        <div><span class="kbd">${c}</span></div>
        <div>
          <label style="display:flex; align-items:center; gap:10px; cursor:pointer;">
            <input type="checkbox" ${on ? "checked" : ""} data-coltoggle="${c}" />
            <span>${on ? "Shown" : "Hidden"}</span>
          </label>
        </div>
      </div>
    `;
  }).join("");
  openModal(colsModal);

  $$("[data-coltoggle]", colsBody).forEach(chk => {
    chk.addEventListener("change", (e) => {
      const col = e.target.getAttribute("data-coltoggle");
      const db = loadJSON(LS_COLS, {});
      db[col] = e.target.checked;
      saveJSON(LS_COLS, db);
      applyColumnPrefs();
    });
  });
});

$("#btnShortcuts")?.addEventListener("click", () => openModal(shortcutsModal));

function applyColumnPrefs(){
  const db = loadJSON(LS_COLS, {});
  $$("[data-col]").forEach(el => {
    const key = el.getAttribute("data-col");
    // Force selection column always visible
    if (key === "select"){
      if (db.select === false){
        db.select = true;
        saveJSON(LS_COLS, db);
      }
      el.style.display = "";
      return;
    }
    const show = db[key] !== false;
    el.style.display = show ? "" : "none";
  });
}

/* ========= Reminders ========= */
function remindersEnabled(){
  const prefs = loadJSON(LS_PREFS, {});
  return !!prefs.remindersEnabled;
}
function notifDB(){ return loadJSON(LS_NOTIF, {}); }
function markNotified(id){
  const db = notifDB();
  db[id] = todayISO();
  saveJSON(LS_NOTIF, db);
}
function alreadyNotifiedToday(id){
  const db = notifDB();
  return db[id] === todayISO();
}

async function enableReminders(){
  if (!("Notification" in window)) return toast("This browser doesn't support notifications.");
  if (Notification.permission === "granted"){
    saveJSON(LS_PREFS, { ...loadJSON(LS_PREFS, {}), remindersEnabled: true });
    toast("Reminders enabled");
    return refreshRemindersUI();
  }
  if (Notification.permission === "denied") return toast("Notifications blocked in browser settings.");

  const perm = await Notification.requestPermission();
  if (perm === "granted"){
    saveJSON(LS_PREFS, { ...loadJSON(LS_PREFS, {}), remindersEnabled: true });
    toast("Reminders enabled");
    refreshRemindersUI();
  } else {
    toast("Reminders not enabled");
  }
}

function listDueItems(){
  const items = rows.map(r => {
    const id = r.dataset.clientId;
    const name = fullName(r) || "Client";
    const email = norm(r.dataset.email);
    const phone = norm(r.dataset.phone);
    const nextDate = norm(r.dataset.crmNextDate);
    const nextText = norm(r.dataset.crmNextText);
    const pri = norm(r.dataset.crmPriority) || "Normal";

    if (!nextDate && !nextText) return null;

    const overdue = isOverdue(nextDate);
    const today = isToday(nextDate);
    if (!overdue && !today) return null;

    return { id, name, email, phone, nextDate, nextText, pri, overdue, today };
  }).filter(Boolean);

  const priScore = (p) => {
    const x = (p || "").toLowerCase();
    if (x === "urgent") return 0;
    if (x === "high") return 1;
    if (x === "normal") return 2;
    return 3;
  };

  items.sort((a,b) => {
    if (a.overdue !== b.overdue) return a.overdue ? -1 : 1;
    const ps = priScore(a.pri) - priScore(b.pri);
    if (ps !== 0) return ps;
    return (a.name || "").localeCompare(b.name || "");
  });

  return items;
}

function refreshRemindersUI(){
  const due = listDueItems();
  remCount.textContent = String(due.length);

  if (remindersEnabled() || Notification.permission === "granted") {
    btnEnableReminders.textContent = "Reminders: On";
    btnEnableReminders.classList.add("btn-gold");
  } else {
    btnEnableReminders.textContent = "Enable Reminders";
    btnEnableReminders.classList.remove("btn-gold");
  }
}

function openRemindersModal(){
  const body = $("#remindersBody");
  const due = listDueItems();

  if (due.length === 0){
    body.innerHTML = `<div class="tiny">Nothing due today. Keep executing.</div>`;
    openModal(remindersModal);
    return;
  }

  body.innerHTML = due.map(x => {
    const flag = x.overdue
      ? `<span class="badge bad"><span class="dot"></span>Overdue</span>`
      : `<span class="badge warn"><span class="dot"></span>Due Today</span>`;

    const actionText = safeHtml(x.nextText || "Next action");
    const dateText = safeHtml(x.nextDate || "No date");

    const mail = x.email
      ? `mailto:${encodeURIComponent(x.email)}?subject=${encodeURIComponent("Quick follow-up")}&body=${encodeURIComponent(`Hey ${x.name},\n\nQuick follow-up on: ${x.nextText || "Next action"}.\n\n— Legend`)}`
      : "#";

    return `
      <div class="rem-row">
        <div class="rem-left">
          <div style="display:flex; gap:10px; flex-wrap:wrap; align-items:center;">
            <div class="rem-title">${safeHtml(x.name)}</div>
            ${flag}
            <span class="badge"><span class="dot"></span>${safeHtml(x.pri)}</span>
          </div>
          <div class="rem-meta">${dateText} • ${actionText}</div>
        </div>
        <div class="rem-actions">
          <button class="btn btn-gold" type="button" data-remopen="${x.id}">Open</button>
          <button class="btn btn-ghost" type="button" data-remcopy="${x.id}">Copy</button>
          <a class="btn btn-ghost" href="${mail}" ${x.email ? "" : "onclick=\"return false;\""}>Email</a>
        </div>
      </div>
    `;
  }).join("");

  openModal(remindersModal);
}

btnEnableReminders?.addEventListener("click", enableReminders);
btnReminders?.addEventListener("click", openRemindersModal);

document.addEventListener("click", (e) => {
  const openId = e.target.closest("[data-remopen]")?.getAttribute("data-remopen");
  if (openId){
    const row = rows.find(r => r.dataset.clientId === openId);
    if (row){
      closeModal();
      openDrawerForRow(row);
    }
  }

  const delBtn = e.target.closest(".delCard");
  if (delBtn){
    const card = delBtn.closest(".client-card");
    if (!card) return;

    const clientId = card.getAttribute("data-cardid");
    if (!clientId) return;

    if (!confirm("Delete this client? This will remove the profile + household + Entra login for this client.")) return;

    const f = document.getElementById("__af");
    if (!f) return toast("Missing antiforgery form.");

    f.setAttribute("action", "/Clients/Delete");
    f.querySelectorAll("input[name='clientUserId']").forEach(x => x.remove());

    const inp = document.createElement("input");
    inp.type = "hidden";
    inp.name = "clientUserId";
    inp.value = clientId;
    f.appendChild(inp);

    f.submit();
    return;
  }

  const copyId = e.target.closest("[data-remcopy]")?.getAttribute("data-remcopy");
  if (copyId){
    const row = rows.find(r => r.dataset.clientId === copyId);
    if (!row) return;
    const line = `${fullName(row)}\nNext: ${norm(row.dataset.crmNextDate)} • ${norm(row.dataset.crmNextText)}\nEmail: ${norm(row.dataset.email)}\nPhone: ${norm(row.dataset.phone)}`.trim();
    copyText(line);
  }
});

function notify(title, body){
  if (!remindersEnabled()) return;
  if (!("Notification" in window)) return;
  if (Notification.permission !== "granted") return;
  try { new Notification(title, { body }); } catch {}
}

function checkReminders(){
  const due = listDueItems();
  refreshRemindersUI();

  due.filter(x => x.today).forEach(x => {
    if (alreadyNotifiedToday(x.id)) return;
    notify("Legend Reminder", `${x.name}: ${x.nextText || "Next action"} (due today)`);
    markNotified(x.id);
  });
}

function maybeNotifyImmediateForDue(clientId){
  const row = rows.find(r => r.dataset.clientId === clientId);
  if (!row) return;
  const nextDate = norm(row.dataset.crmNextDate);
  const nextText = norm(row.dataset.crmNextText);
  if (!isToday(nextDate)) return;
  if (alreadyNotifiedToday(clientId)) return;

  notify("Legend Reminder", `${fullName(row)}: ${nextText || "Next action"} (due today)`);
  markNotified(clientId);
  refreshRemindersUI();
}

/* ========= Command Palette ========= */
function openCmd(){
  openModal(cmdModal);
  setTimeout(() => cmdInput?.focus(), 60);
  if (cmdInput) cmdInput.value = "";
}

function runCommand(text){
  const t = (text || "").toLowerCase().trim();
  if (!t) return;

  if (t.includes("export")) btnExportCsv?.click();
  else if (t.includes("copy")) btnCopyEmails?.click();
  else if (t.includes("reminders")) openRemindersModal();
  else if (t.includes("enable reminders")) enableReminders();
  else if (t.includes("view pipeline") || t.includes("view cards")) { viewMode.value = "pipeline"; viewMode.dispatchEvent(new Event("change")); }
  else if (t.includes("view table")) { viewMode.value = "table"; viewMode.dispatchEvent(new Event("change")); }
  else if (t.includes("filter overdue")) { attentionFilter.value = "overdue"; attentionFilter.dispatchEvent(new Event("change")); }
  else if (t.includes("filter needs")) { attentionFilter.value = "needs"; attentionFilter.dispatchEvent(new Event("change")); }
  else if (t.includes("density compact")) { density.value = "compact"; density.dispatchEvent(new Event("change")); }
  else if (t.includes("density comfort")) { density.value = "comfort"; density.dispatchEvent(new Event("change")); }
  else if (t.includes("connect calendar")) { startCalendarConnect(); }
  else if (t.includes("save zoom")) { savePersonalZoomLink(); }
  else if (t.includes("clear zoom")) { clearPersonalZoomLink(); }
  else if (t.includes("create event")) { createCalendarEventFromDrawer(); }
  else toast("Unknown command");

  closeModal();
}

cmdInput?.addEventListener("keydown", (e) => {
  if (e.key === "Enter") runCommand(cmdInput.value);
});

/* ========= Global Keys ========= */
document.addEventListener("keydown", (e) => {
  if (e.key === "Escape"){
    closeAllMenus(null);
    closeAllTextMenus(null);
    closeLeadCommAuth();
    closeDrawer();
    closeModal();
  }

  if (e.key === "/" && !["INPUT","TEXTAREA"].includes(document.activeElement?.tagName)){
    e.preventDefault();
    const input = $(".field input[name='search']");
    input?.focus();
  }

  // Cmd/Ctrl + Shift + D -> bulk delete selected leads
  const isMac = navigator.platform.toLowerCase().includes("mac");
  if ((isMac ? e.metaKey : e.ctrlKey) && e.shiftKey && e.key.toLowerCase() === "d"){
    e.preventDefault();
    bulkDeleteSelected();
    return;
  }

  if ((isMac ? e.metaKey : e.ctrlKey) && e.key.toLowerCase() === "k"){
    e.preventDefault();
    openCmd();
  }

  if (!drawer.classList.contains("open")) return;

  if (e.altKey && e.key.toLowerCase() === "n"){
    e.preventDefault();
    const btn = $$(".outcome-btn").find(x => x.getAttribute("data-outcome") === "Nurture");
    btn?.click();
  }

  if (e.altKey && e.key.toLowerCase() === "b"){
    e.preventDefault();
    const btn = $$(".outcome-btn").find(x => x.getAttribute("data-outcome") === "Booked");
    btn?.click();
  }

  if (e.altKey && e.key.toLowerCase() === "d"){
    e.preventDefault();
    const tomorrow = new Date();
    tomorrow.setDate(tomorrow.getDate() + 1);
    setDrawerNextActionDate(tomorrow.toISOString().slice(0, 10));
    dSaved.textContent = "Quick key: next day set — saving…";
    queueQuickViewAutosave();
  }

  if (e.altKey && e.key.toLowerCase() === "e"){
    e.preventDefault();
    createCalendarEventFromDrawer();
  }

  if (e.altKey && e.key.toLowerCase() === "m"){
    e.preventDefault();
    dPipelineStage.value = "Booked";
    dSaved.textContent = "Quick key: moved stage to Booked Meeting — saving…";
    queueQuickViewAutosave();
  }
});

/* ========= Calendar Sync (Microsoft 365 / Outlook) ========= */
async function calendarStatus(){
  const now = Date.now();
  if (_calendarStatusCache && (now - _calendarStatusCacheAt) < CAL_STATUS_TTL_MS){
    return _calendarStatusCache;
  }
  try{
    const res = await fetch("/calendar/status", withDialHeaders({ credentials:"include" }));
    if (!res.ok){
      _calendarStatusCache = { connected:false };
      _calendarStatusCacheAt = now;
      return _calendarStatusCache;
    }
    _calendarStatusCache = await res.json();
    _calendarStatusCacheAt = now;
    return _calendarStatusCache;
  }catch{
    _calendarStatusCache = { connected:false };
    _calendarStatusCacheAt = now;
    return _calendarStatusCache;
  }
}

function loadSavedZoomLink(){
  const db = loadJSON(LS_ZOOM, {});
  return norm(db.personalRoomUrl);
}

function savePersonalZoomLink(){
  const link = norm(dZoomJoinUrl.value);
  if (!link){
    toast("Add your Zoom room link first.");
    return;
  }

  saveJSON(LS_ZOOM, { personalRoomUrl: link });
  dUsePersonalZoomLink.checked = true;
  updateZoomControls();
  dSaved.textContent = "Saved personal Zoom link ✔";
  toast("Saved personal Zoom link");
}

function clearPersonalZoomLink(){
  saveJSON(LS_ZOOM, {});
  if (dUsePersonalZoomLink.checked && !norm(dZoomJoinUrl.value)) dUsePersonalZoomLink.checked = false;
  updateZoomControls();
  toast("Cleared saved Zoom link");
}

function startCalendarConnect(){
  window.location.href = "/calendar/connect";
}

btnCalendarAuth?.addEventListener("click", startCalendarConnect);
btnResourceCalendar?.addEventListener("click", startCalendarConnect);
btnZoomSavePersonal?.addEventListener("click", savePersonalZoomLink);
btnZoomClearPersonal?.addEventListener("click", clearPersonalZoomLink);

btnFilterMeetings?.addEventListener("click", () => {
  pipelineFocusStage = "Booked";
  pipelineNavSelectedStage = "Booked";
  if (viewMode) viewMode.value = "pipeline";
  applyViewMode();
  renderAll();
});

btnFilterOverdue?.addEventListener("click", () => {
  attentionFilter.value = "overdue";
  currentPage = 1;
  renderAll();
});

btnPipelineRefresh?.addEventListener("click", () => {
  renderAll();
  toast("Pipeline refreshed");
});

btnPipelineAll?.addEventListener("click", () => {
  pipelineFocusStage = "";
  pipelineNavSelectedStage = "";
  pipelineNavSearchTerm = "";
  renderAll();
});

btnPipelineCallTask?.addEventListener("click", () => {
  renderCallTaskMode();
  openModal(callTaskModal);
});

$$("[data-stagejump]").forEach(btn => {
  btn.addEventListener("click", () => {
    const stage = btn.getAttribute("data-stagejump") || "";
    pipelineFocusStage = stage;
    pipelineNavSelectedStage = stage;
    syncStagePickerUi(stage);
    if (viewMode) viewMode.value = "pipeline";
    applyViewMode();
    renderAll();
  });
});

async function updateCalendarButton(){
  const st = await calendarStatus();
  if (st.connected){
    btnCalendarAuth.textContent = `Calendar: Connected`;
    btnCalendarAuth.classList.add("btn-gold");
    btnCalendarAuth.classList.remove("btn-ghost");
    btnCalendarAuth.title = st.email ? `Connected: ${st.email}` : "Connected";
  } else {
    btnCalendarAuth.textContent = "Connect Calendar";
    btnCalendarAuth.classList.remove("btn-gold");
    btnCalendarAuth.classList.add("btn-ghost");
    btnCalendarAuth.title = "Connect your Microsoft calendar (per agent)";
  }
}

async function updateZoomControls(){
  if (!dZoomStatus) return;
  const saved = loadSavedZoomLink();
  dZoomStatus.textContent = saved
    ? "Saved personal Zoom room link ready for fast event creation."
    : "No saved personal Zoom room link yet.";
}

function clearMeetingSuggestions(){
  if (!dMeetingLocationSuggest) return;
  dMeetingLocationSuggest.classList.remove("active");
  dMeetingLocationSuggest.innerHTML = "";
}

let zoomWrapHideTimer = null;

function toggleZoomPanel(show){
  if (!dZoomWrap) return;

  clearTimeout(zoomWrapHideTimer);

  if (show){
    dZoomWrap.style.display = "";
    requestAnimationFrame(() => {
      dZoomWrap.classList.add("is-visible");
      dZoomWrap.classList.remove("is-hidden");
    });
    return;
  }

  dZoomWrap.classList.remove("is-visible");
  dZoomWrap.classList.add("is-hidden");
  zoomWrapHideTimer = setTimeout(() => {
    dZoomWrap.style.display = "none";
  }, 180);
}

function renderMeetingSuggestionStatus(message, tone = "info"){
  if (!dMeetingLocationSuggest) return;
  const safeTone = ["info", "success", "warn", "error"].includes(tone) ? tone : "info";
  dMeetingLocationSuggest.innerHTML = `<div class="meeting-address-status ${safeTone}">${safeHtml(message)}</div>`;
  dMeetingLocationSuggest.classList.add("active");
}

function renderMeetingSuggestions(items){
  if (!dMeetingLocationSuggest) return;
  if (!items.length){
    clearMeetingSuggestions();
    return;
  }

  dMeetingLocationSuggest.innerHTML = items.map(item => `
    <button type="button" class="meeting-address-item" data-address-pick="${safeHtml(item)}">${safeHtml(item)}</button>
  `).join("");
  dMeetingLocationSuggest.classList.add("active");
}

async function fetchMeetingAddressSuggestions(query){
  const q = norm(query);
  if (q.length < 4 || !dMeetingType || dMeetingType.value !== "InPerson"){
    clearMeetingSuggestions();
    return;
  }

  if (meetingSuggestAbort){
    meetingSuggestAbort.abort();
  }

  meetingSuggestAbort = new AbortController();

  try{
    renderMeetingSuggestionStatus("Searching addresses...", "info");
    const url = `https://nominatim.openstreetmap.org/search?format=jsonv2&addressdetails=1&limit=5&countrycodes=us&q=${encodeURIComponent(q)}`;
    const res = await fetch(url, {
      signal: meetingSuggestAbort.signal,
      headers: {
        "Accept": "application/json"
      }
    });

    if (!res.ok){
      throw new Error("Address lookup failed.");
    }

    const data = await res.json();
    const suggestions = Array.isArray(data)
      ? data.map(x => norm(x.display_name)).filter(Boolean).slice(0, 5)
      : [];

    if (!suggestions.length){
      renderMeetingSuggestionStatus("No address suggestions found yet. Keep typing.", "warn");
      return;
    }

    renderMeetingSuggestions(suggestions);
  }catch(err){
    if (err?.name === "AbortError") return;
    console.error(err);
    renderMeetingSuggestionStatus("Address suggestions are unavailable right now. You can still type the address manually.", "error");
  }
}

function applyMeetingType(type, row){
  const normalized = type === "Zoom" ? "Zoom" : (type === "InPerson" ? "InPerson" : "Phone");
  const currentLocation = norm(dMeetingLocation?.value);

  if (dMeetingLocation && currentLocation && currentLocation !== "Zoom" && currentLocation !== "Zoom Call" && !currentLocation.startsWith("Phone Call")){
    dMeetingLocation.dataset.inPersonValue = currentLocation;
  }

  if (dMeetingType) dMeetingType.value = normalized;
  toggleZoomPanel(normalized === "Zoom");

  if (!dMeetingLocation) return;

  dMeetingLocation.classList.remove("is-phone", "is-zoom", "is-inperson");

  if (normalized === "Phone"){
    dMeetingLocation.classList.add("is-phone");
    dMeetingLocation.readOnly = true;
    dMeetingLocation.placeholder = "Phone Call";
    dMeetingLocation.value = `Phone Call • ${norm(row?.dataset.phone) || "No phone on file"}`;
    dZoomJoinUrl.value = "";
    dUsePersonalZoomLink.checked = false;
    clearMeetingSuggestions();
    return;
  }

  if (normalized === "Zoom"){
    dMeetingLocation.classList.add("is-zoom");
    dMeetingLocation.readOnly = true;
    dMeetingLocation.placeholder = "Zoom Call";
    dMeetingLocation.value = "Zoom Call";
    if (!norm(dZoomJoinUrl.value) && dUsePersonalZoomLink.checked){
      dZoomJoinUrl.value = loadSavedZoomLink();
    }
    clearMeetingSuggestions();
    return;
  }

  dMeetingLocation.classList.add("is-inperson");
  dMeetingLocation.readOnly = false;
  dMeetingLocation.placeholder = "Office, client home, coffee shop...";

  if (!norm(dMeetingLocation.value) || norm(dMeetingLocation.value) === "Zoom" || norm(dMeetingLocation.value) === "Zoom Call" || norm(dMeetingLocation.value).startsWith("Phone Call")){
    dMeetingLocation.value = dMeetingLocation.dataset.inPersonValue || "";
  }
}

function inferMeetingType(detail, row){
  if (detail?.zoomJoinUrl || detail?.usePersonalZoomLink || norm(row?.dataset.sZoom)){
    return "Zoom";
  }

  const location = norm(detail?.meetingLocation || row?.dataset.sMeetingLocation);
  if (location.startsWith("Phone Call")){
    return "Phone";
  }

  if (location === "Zoom" || location === "Zoom Call"){
    return "Zoom";
  }

  if (location){
    return "InPerson";
  }

  return "Phone";
}

dMeetingLocation?.addEventListener("input", () => {
  if (dMeetingType?.value !== "InPerson") return;
  dMeetingLocation.dataset.inPersonValue = norm(dMeetingLocation.value);
  clearTimeout(meetingSuggestTimer);
  meetingSuggestTimer = setTimeout(() => {
    fetchMeetingAddressSuggestions(dMeetingLocation.value);
  }, 260);
});

dMeetingLocation?.addEventListener("blur", () => {
  setTimeout(() => clearMeetingSuggestions(), 180);
});

document.addEventListener("click", (e) => {
  const pick = e.target.closest("[data-address-pick]");
  if (pick && dMeetingLocation){
    dMeetingLocation.value = pick.getAttribute("data-address-pick") || "";
    clearMeetingSuggestions();
    return;
  }

  if (!e.target.closest("#dMeetingLocation") && !e.target.closest("#dMeetingLocationSuggest")){
    clearMeetingSuggestions();
  }

  const freeSlot = e.target.closest("[data-free-slot-time]");
  if (freeSlot && dMeetingTime){
    const timeValue = freeSlot.getAttribute("data-free-slot-time") || "";
    const startLabel = freeSlot.getAttribute("data-free-slot-start") || "";
    if (timeValue){
      dMeetingTime.value = timeValue;
      refreshCalendarBusyPanel();
      toast(`Meeting time set to ${startLabel}`);
    }
  }
});

function defaultEventTimes(dateISO){
  const meetingTime = norm(dMeetingTime?.value) || "09:00";
  const duration = parseInt(dMeetingDuration?.value || "30", 10) || 30;
  const base = new Date(`${dateISO}T${meetingTime}:00`);
  const end = new Date(base.getTime() + duration*60*1000);

  const toLocalIsoNoZ = (d) => {
    const tz = new Date(d.getTime() - d.getTimezoneOffset()*60000);
    return tz.toISOString().slice(0,19);
  };

  return { startISO: toLocalIsoNoZ(base), endISO: toLocalIsoNoZ(end) };
}

function formatBusyRange(item){
  if (item.isAllDay) return "All day";
  return `${item.startLabel || ""} - ${item.endLabel || ""}`.trim();
}

function isBusyConflict(item, selectedStart, selectedEnd){
  if (!selectedStart || !selectedEnd) return false;
  if (item.isAllDay) return true;
  const start = item.startIso ? new Date(item.startIso) : null;
  const end = item.endIso ? new Date(item.endIso) : null;
  if (!start || !end || Number.isNaN(start.getTime()) || Number.isNaN(end.getTime())) return false;
  return start < selectedEnd && end > selectedStart;
}

function renderCalendarBusyState(message, tone = "neutral"){
  if (!dCalendarBusyList || !dCalendarBusyNote || !dCalendarFreeList || !dCalendarWorkHours) return;
  dCalendarBusyList.innerHTML = "";
  dCalendarFreeList.innerHTML = "";
  dCalendarWorkHours.textContent = "Uses Outlook work hours or your default 7:00 AM - 7:00 PM";
  dCalendarBusyNote.textContent = message;
  dCalendarBusyNote.style.color = tone === "error"
    ? "#b91c1c"
    : tone === "good"
      ? "#166534"
      : "#7f1d1d";
}

function renderCalendarBusy(items, freeSlots = [], workHours = null){
  if (!dCalendarBusyList || !dCalendarBusyNote || !dCalendarFreeList || !dCalendarWorkHours) return;

  const nextDate = norm(dNextDate?.value);
  const selected = nextDate ? defaultEventTimes(nextDate) : null;
  const selectedStart = selected?.startISO ? new Date(selected.startISO) : null;
  const selectedEnd = selected?.endISO ? new Date(selected.endISO) : null;
  const conflicts = items.filter(item => isBusyConflict(item, selectedStart, selectedEnd));

  if (workHours?.enabled){
    dCalendarWorkHours.textContent = `Work hours: ${workHours.startLabel} - ${workHours.endLabel}${workHours.source === "outlook" ? "" : " (default)"}`;
  } else {
    dCalendarWorkHours.textContent = "This day is outside your Outlook work week";
  }

  if (freeSlots.length){
    dCalendarFreeList.innerHTML = freeSlots.map(slot => `
      <button type="button" class="calendar-free-slot" data-free-slot-time="${safeHtml(slot.startTimeValue || "")}" data-free-slot-start="${safeHtml(slot.startLabel || "")}">
        ${safeHtml(`${slot.startLabel || ""} - ${slot.endLabel || ""}`.trim())}
      </button>
    `).join("");
  } else {
    dCalendarFreeList.innerHTML = `<div class="calendar-free-empty">${workHours?.enabled ? "No open booking windows inside your work hours for this day." : "No booking windows because this day is outside your work hours."}</div>`;
  }

  if (!items.length){
    dCalendarBusyNote.textContent = "No busy blocks found on your Outlook calendar for this day.";
    dCalendarBusyNote.style.color = "#166534";
    dCalendarBusyList.innerHTML = `<div class="calendar-busy-empty">Open day. No busy blocks found.</div>`;
    return;
  }

  dCalendarBusyNote.textContent = conflicts.length
    ? `${conflicts.length} conflict${conflicts.length === 1 ? "" : "s"} with the currently selected meeting time.`
    : "Busy blocks for the selected day. Your current time does not overlap any of them.";
  dCalendarBusyNote.style.color = conflicts.length ? "#b91c1c" : "#166534";

  dCalendarBusyList.innerHTML = items.map(item => {
    const conflict = isBusyConflict(item, selectedStart, selectedEnd);
    const showAs = norm(item.showAs) || "busy";
    const label = showAs.charAt(0).toUpperCase() + showAs.slice(1);

    return `
      <div class="calendar-busy-item ${conflict ? "conflict" : ""}">
        <div class="calendar-busy-main">
          <div class="calendar-busy-subject">${safeHtml(item.subject || "Busy")}</div>
          <div class="calendar-busy-meta">${safeHtml(label)}</div>
          ${conflict ? '<div class="calendar-busy-flag">Conflicts with selected time</div>' : ""}
        </div>
        <div class="calendar-busy-time">${safeHtml(formatBusyRange(item))}</div>
      </div>
    `;
  }).join("");
}

async function refreshCalendarBusyPanel(){
  if (!dCalendarBusyDate) return;

  const nextDate = norm(dNextDate?.value);
  dCalendarBusyDate.textContent = nextDate || "Select a date";

  if (!nextDate){
    renderCalendarBusyState("Choose a Next Action Date to see your Outlook busy blocks for that day.");
    return;
  }

  const st = await calendarStatus();
  if (!st.connected){
    renderCalendarBusyState("Connect your Outlook calendar to load busy times for this day.", "error");
    return;
  }

  if (calendarBusyAbort){
    calendarBusyAbort.abort();
  }

  calendarBusyAbort = new AbortController();
  renderCalendarBusyState("Loading busy times from Outlook...");

  try{
    const res = await fetch(`/calendar/day-availability?date=${encodeURIComponent(nextDate)}`, withDialHeaders({
      credentials: "include",
      signal: calendarBusyAbort.signal
    }));

    if (!res.ok){
      const text = await res.text().catch(() => "");
      throw new Error(text || "Calendar availability failed.");
    }

    const data = await res.json();
    if (!data.connected){
      renderCalendarBusyState("Calendar availability is unavailable right now. You can still create the meeting manually.", "error");
      return;
    }

    renderCalendarBusy(
      Array.isArray(data.items) ? data.items : [],
      Array.isArray(data.freeSlots) ? data.freeSlots : [],
      data.workHours || null
    );
  }catch(err){
    if (err?.name === "AbortError") return;
    console.error(err);
    renderCalendarBusyState("Could not load Outlook busy times for this day.", "error");
  }
}

async function createCalendarEventFromDrawer(){
  if (!activeClientId) return toast("Open a client first.");

  const st = await calendarStatus();
  if (!st.connected){
    toast("Connect calendar first.");
    return;
  }

  const row = rows.find(r => r.dataset.clientId === activeClientId);
  if (!row) return toast("Client not found.");

  const nextDate = norm(dNextDate.value);
  const nextText = norm(dNextText.value);

  if (!nextDate) return toast("Set a Next Action Date first.");
  if (!nextText) return toast("Add Next Action text first.");

  const { startISO, endISO } = defaultEventTimes(nextDate);

  const payload = {
    clientUserId: activeClientId,
    subject: `Client Follow-up: ${fullName(row)}`,
    startISO,
    endISO,
    body: `Next Action: ${nextText}\n\nPipeline Stage: ${pipelineLabel(norm(dPipelineStage.value))}\nClient: ${fullName(row)}\nEmail: ${norm(row.dataset.email)}\nPhone: ${norm(row.dataset.phone)}`,
    location: dMeetingType?.value === "Phone"
      ? `Phone Call • ${norm(row.dataset.phone) || "No phone on file"}`
      : norm(dMeetingLocation.value),
    zoomJoinUrl: dMeetingType?.value === "Zoom"
      ? (norm(dZoomJoinUrl.value) || (dUsePersonalZoomLink.checked ? loadSavedZoomLink() : ""))
      : "",
    activityNote: `Calendar event created: ${nextText}`
  };

  try{
    const data = await postJson("/calendar/create-event", payload);
    row.dataset.sLasttouch = data.crmLastTouch || todayISO();
    hydrateRow(row);
    activeClientDetail = { ...(activeClientDetail || {}), activities: data.activities || [], lastCalendarEventWebLink: data.webLink || "" };
    renderTimeline(data.activities || []);
    dLastTouch.value = data.crmLastTouch || todayISO();
    dSaved.textContent = "Calendar event synced ✔";
    refreshCalendarBusyPanel();
    toast("Calendar event created");
  }catch(err){
    console.error(err);
    toast(err?.message || "Calendar create failed.");
  }
}

btnCreateCalendarEvent?.addEventListener("click", createCalendarEventFromDrawer);

/* ========= Prefs Restore ========= */
(function restorePrefs(){
  const prefs = loadJSON(LS_PREFS, {});
  viewMode.value = "pipeline";
  if (prefs.density) density.value = prefs.density;
  applyViewMode();
  applyDensityClass();
})();

wireQuickViewAutosave();

function focusLeadFromUrl(){
  try{
    const params = new URLSearchParams(window.location.search || "");
    const q = norm(params.get("search"));
    if (!q) return;
    if (clientSearchInput) clientSearchInput.value = q;
    const ok = focusClientByQuery(q);
    if (clientSearchWarning){
      clientSearchWarning.textContent = ok ? "" : "No matching client found.";
    }
  }catch{}
}

function openQuickViewFromUrl(){
  if (quickViewOpenedFromUrl) return;
  try{
    const params = new URLSearchParams(window.location.search || "");
    const targetId = norm(params.get("clientUserId") || params.get("leadId") || params.get("id") || params.get("open"));
    if (!targetId) return;

    quickViewOpenedFromUrl = true;
    const row = rows.find(r => norm(r.dataset.clientId) === targetId);
    if (row) return openDrawerForRow(row);
    return openDrawerById(targetId);
  }catch(err){
    console.error("Auto-open Quick View failed", err);
  }
}

/* ========= Boot ========= */
async function boot(){
  syncBarHeight();
  applyColumnPrefs();
  applyDensityClass();
  renderSavedViews();
  ensureModalInBody('quickCreateActionModal');

  await loadMyDaySnapshot(true);

  renderAll();
  bindQuickViewTabs();
  focusLeadFromUrl();
  openQuickViewFromUrl();
  updateSelectionUI();
  refreshRemindersUI();

  checkReminders();
  __timers.reminders = setInterval(checkReminders, 60 * 1000);

  updateCalendarButton();
  updateZoomControls();
}
boot();

window.addEventListener("beforeunload", () => {
  if (__timers.dialFresh) clearInterval(__timers.dialFresh);
  if (__timers.reminders) clearInterval(__timers.reminders);
});

/* ========= Copy Emails guard ========= */
btnCopyEmails?.addEventListener("click", () => {
  const emails = getCheckedRows().map(r => norm(r.dataset.email)).filter(Boolean);
  if (!emails.length) return toast("No emails selected");
  copyText(emails.join(", "));
});

document.addEventListener("change", (e) => {
  if (e.target && e.target.classList?.contains("row-chk")) updateSelectionUI();
});
const hybridBar = null;
const hbJumpBoard = null;
const hbJumpTable = null;
const hbClearFilters = null;
const hbRefresh = null;
const hbCallTasks = null;
const hbTop = null;
