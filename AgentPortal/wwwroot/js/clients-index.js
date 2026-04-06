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
const LS_PROD_DRAFT_CLIENT = "legend_prod_draft_client_v1";
const LS_ADVANCED_MARKETS_DRAFTS = "legend_adv_markets_drafts_v1";
const liveSync = window.liveSync;

const $  = (sel, root=document) => root.querySelector(sel);
const $$ = (sel, root=document) => Array.from(root.querySelectorAll(sel));
const advancedMarketsStrategyOrder = ["DefinedBenefit", "CashBalance", "ComboDb401k", "ExecutiveBonus162", "DeferredComp", "SplitDollar", "TaxDiversification"];
const PROTECTION_SNAPSHOT_TOOL_ID = "ProtectionSnapshot";
const advancedMarketsIntegerFieldNames = new Set([
  "Client.OwnerAge",
  "Client.SpouseAge",
  "Client.RetirementAge",
  "Business.EmployeeCount",
  "Business.EligibleEmployeeCount",
  "Business.AverageEmployeeAge",
  "DefinedBenefit.SpouseAge",
  "ExecutiveBonus.YearsFunded",
  "DeferredComp.DeferralYears",
  "DeferredComp.DistributionStartAge",
  "DeferredComp.DistributionYears",
  "SplitDollar.FundingYears",
  "SplitDollar.ExitYear",
  "Projection.RetirementDurationYears"
]);
const advancedMarketsBooleanFieldNames = new Set([
  "DefinedBenefit.IncludeSpouse",
  "Combo.CatchUp"
]);

// Agent-local timezone header for every request (single source of truth).
const agentTimeZone = Intl.DateTimeFormat().resolvedOptions().timeZone || "";
function withAgentTimeZone(init = {}){
  const headers = new Headers(init?.headers || {});
  if (agentTimeZone) headers.set("X-Agent-TimeZone", agentTimeZone);
  return { ...init, headers };
}
if (window.fetch){
  const __origFetch = window.fetch.bind(window);
  window.fetch = (input, init = {}) => __origFetch(input, withAgentTimeZone(init));
}

function loadJSON(key, fallback){
  try { return JSON.parse(localStorage.getItem(key) || "") ?? fallback; }
  catch { return fallback; }
}
function saveJSON(key, obj){ localStorage.setItem(key, JSON.stringify(obj ?? {})); }

function todayISO(){
  const d = new Date();
  const tz = new Date(d.getTime() - d.getTimezoneOffset() * 60000);
  return tz.toISOString().slice(0,10);
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

const LegendModalApi = window.LegendModal || {};
const ensureModalInBody = LegendModalApi.ensureInBody?.bind(LegendModalApi) || (() => null);
const bindBootstrapModalStability = LegendModalApi.bind?.bind(LegendModalApi) || (() => null);
const hideBootstrapModalById = LegendModalApi.hide?.bind(LegendModalApi) || (() => null);
const closeLegacyOverlayModals = LegendModalApi.closeLegacyExecutionOverlays?.bind(LegendModalApi) || (() => {});
const reconcileBootstrapModalState = LegendModalApi.reconcile?.bind(LegendModalApi) || (() => {});

function bindQuickViewBootstrapModals(){
  bindBootstrapModalStability("clientActionsHubModal", {
    modalZ: 1060,
    backdropZ: 1055,
    onHidden: () => {
      hideBootstrapModalById("clientQuickCreateActionModal");
      hideBootstrapModalById("addClientCommitmentModal");
      hideBootstrapModalById(finPlanModalId);
    }
  });

  bindBootstrapModalStability("clientQuickCreateActionModal", { modalZ: 1085, backdropZ: 1080 });
  bindBootstrapModalStability("addClientCommitmentModal", { modalZ: 1085, backdropZ: 1080 });
  bindBootstrapModalStability(finPlanModalId, { modalZ: 1095, backdropZ: 1090 });
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

/* ========= Financial Plan (Accumulation + Distribution) ========= */
async function openFinPlanModal(clientUserId){
  bindQuickViewBootstrapModals();
  closeLegacyOverlayModals();
  reconcileBootstrapModalState();
  ensureModalInBody(finPlanModalId);
  const modalEl = document.getElementById(finPlanModalId);
  if (!modalEl) { toast("Modal not found."); return; }
  if (!window.bootstrap){
    toast("UI library missing; cannot open modal.");
    return;
  }
  ensureFinPlanSelectOptions();
  finPlanModal = window.bootstrap.Modal.getOrCreateInstance(modalEl);
  resetFinPlanForm();
  await loadFinPlan(clientUserId);
  finPlanModal.show();
}

function resetFinPlanForm(){
  finPlanVersion = 0;
  window.__wfFinalBalance = null;
  finPlanAllocManual = false;
  const form = document.getElementById("finPlanForm");
  if (!form) return;
  form.reset();
  $("#finPlanError").style.display = "none";
  $("#finPlanStatusLabel").textContent = "Loading…";
  $("#finPlanClientLabel").textContent = "";
  const profileIdEl = document.getElementById("finPlanClientProfileId");
  if (profileIdEl) profileIdEl.value = "";
  const userIdEl = document.getElementById("finPlanClientUserId");
  if (userIdEl) userIdEl.value = "";
  updateFinPlanAllocTotal();
}

function recalcFinPlanWealthForecastBalance(){
  const toNumber = (id, def = 0) => {
    const raw = ((document.getElementById(id)?.value) || "").toString().replace(/,/g, '').replace('%', '');
    const num = parseFloat(raw);
    return Number.isFinite(num) ? num : def;
  };
  const clamp = (val, min, max) => Math.min(Math.max(val, min), max);

  const income = Math.max(0, toNumber("wbIncome", 0));
  const startingBalance = Math.max(0, toNumber("wbStartingBalance", 0));
  const years = Math.max(0, Math.floor(toNumber("wbYears", 0)));
  const inflation = Math.max(-0.95, toNumber("wbInflation", 0) / 100);
  const nominalReturn = Math.max(-0.95, toNumber("wbReturn", 0) / 100);
  const tax = clamp(toNumber("wbTax", 0) / 100, 0, 1);
  const liabilities = clamp(toNumber("wbLiabilities", 0) / 100, 0, 1);
  const lifestyle = clamp(toNumber("wbLifestyle", 0) / 100, 0, 1);
  const realGrowthRate = (1 + nominalReturn) / (1 + inflation) - 1;

  const baselineLiabAmt = income * liabilities;
  const baselineLifeAmt = income * lifestyle;

  let investedBalance = startingBalance;
  for (let y = 1; y <= years; y++) {
    const annualExpenses = (income * tax) + baselineLiabAmt + baselineLifeAmt;
    const annualSavings = income - annualExpenses;
    investedBalance = investedBalance * (1 + realGrowthRate) + annualSavings;
  }

  window.__wfFinalBalance = investedBalance > 0 ? investedBalance : null;
  const baseEl = document.getElementById("wfd_base");
  if (baseEl && !document.getElementById("wfd_manualOverride")?.checked) {
    baseEl.value = window.__wfFinalBalance ? Math.round(window.__wfFinalBalance).toLocaleString() : "";
  }
  return window.__wfFinalBalance || 0;
}

function finPlanPayload(){
  const pf = (v)=>{ const n = Number((v||"").toString().replace(/,/g,'')); return isNaN(n)?0:n; };
  const parseManualReturns = (txt) => String(txt || "")
    .split(/[\n,]+/)
    .map(s => Number(String(s).trim()))
    .filter(n => Number.isFinite(n));
  const manualOverride = !!document.getElementById("wfd_manualOverride")?.checked;
  const wfFinalBalance = recalcFinPlanWealthForecastBalance();
  const base = manualOverride ? pf($("#wfd_base")?.value) : wfFinalBalance;
  if (!manualOverride && $("#wfd_base")) $("#wfd_base").value = (base||0).toLocaleString();

  const canonical = {
    schemaVersion: (window.DP_CONSTANTS?.DP_SCHEMA_VERSION) || "1.0",
    planVersion: finPlanVersion || 1,
    retireAge: pf($("#wfd_retAge")?.value),
    endAge: pf($("#wfd_endAge")?.value),
    inflationPct: (window.DP_CONSTANTS?.DP_DEFAULTS?.inflationPct) ?? 3,
    retirementBase: base,
    desiredIncome: pf($("#wfd_desiredIncome")?.value),
    guaranteedIncome: pf($("#wfd_guaranteedIncome")?.value),
    emergencyReserve: pf($("#wfd_emergency")?.value),
    manualBaseOverride: manualOverride,
    invAllocPct: pf($("#wfd_invAlloc")?.value),
    invReturnPct: pf($("#wfd_invReturn")?.value),
    invTaxPct: pf($("#wfd_invTax")?.value),
    liAllocPct: pf($("#wfd_liAlloc")?.value),
    liReturnPct: pf($("#wfd_liGrowth")?.value),
    liTaxPct: pf($("#wfd_liTax")?.value),
    liAccessMode: ($("#wfd_liAccess")?.value || "withdrawal"),
    liPolicyType: ($("#wfd_liType")?.value || "whole"),
    annAllocPct: pf($("#wfd_annAlloc")?.value),
    annReturnPct: pf($("#wfd_annReturn")?.value),
    annTaxPct: pf($("#wfd_annTax")?.value),
    annDesign: ($("#wfd_annDesign")?.value || "fixed"),
    invDownMarket: !!document.getElementById("wfd_invDownMkt")?.checked,
    liDownMarket: !!document.getElementById("wfd_liDownMkt")?.checked,
    annDownMarket: !!document.getElementById("wfd_annDownMkt")?.checked,
    protectInvest: !!document.getElementById("wfd_protectInvest")?.checked,
    annIncomeRider: !!document.getElementById("wfd_annIncomeRider")?.checked,
    annDbRider: !!document.getElementById("wfd_annDbRider")?.checked,
    annRollupPct: pf($("#wfd_annRollup")?.value),
    liEfficiencyPct: pf($("#wfd_liEfficiency")?.value),
    annDeathBenefit: pf($("#wfd_annDeath")?.value),
    liDeathBenefit: pf($("#wfd_liDeath")?.value),
    strategy: ($("#wfd_strategy")?.value || "proportional"),
    gapSource: ($("#wfd_gapSource")?.value || "life"),
    downThreshold: pf($("#wfd_downThreshold")?.value),
    scenarioMode: ($("#wfd_scenarioMode")?.value || "fixed"),
    manualReturns: parseManualReturns($("#wfd_manualReturns")?.value),
    withdrawalOrder: (window.DP_CONSTANTS?.DP_WITHDRAWAL_ORDER_DEFAULT) || ["inv","li","ann","reserve"]
  };

  return {
    version: finPlanVersion,
    wealthForecast: { inputs: { wbStartingBalance: $("#wbStartingBalance")?.value || "", wbIncome: $("#wbIncome")?.value || "", wbYears: $("#wbYears")?.value || "", wbInflation: $("#wbInflation")?.value || "", wbReturn: $("#wbReturn")?.value || "", wbTax: $("#wbTax")?.value || "", wbLiabilities: $("#wbLiabilities")?.value || "", wbLifestyle: $("#wbLifestyle")?.value || "" } },
    distribution: { canonicalInput: canonical, meta:{ source:'crm' } }
  };
}

let finPlanPreviewTimer = null;
function scheduleDpPreview(){
  clearTimeout(finPlanPreviewTimer);
  finPlanPreviewTimer = setTimeout(runDpPreview, 280);
}

function runDpPreview(){
  const status = $("#finPlanStatusLabel");
  if (!window.DP_VALIDATORS?.validatePlanInput || !window.runDistributionPlan){
    if (status) status.textContent = "Ready";
    return;
  }
  const lockedInputs = captureFinPlanEditableState();
  try {
    const canonical = finPlanPayload().distribution?.canonicalInput || {};
    const errs = window.DP_VALIDATORS.validatePlanInput(canonical);
    if (errs.length){
      if (status) status.textContent = `Needs fix: ${errs[0].message}`;
      return;
    }
    const res = window.runDistributionPlan(canonical);
    if (res.errors?.length){
      if (status) status.textContent = `Error: ${res.errors[0].message}`;
      return;
    }
    const sum = res.summary || {};
    const fmt = (v)=> (Number(v)||0).toLocaleString("en-US",{style:"currency",currency:"USD",maximumFractionDigits:0});
    const shortTxt = sum.totalShortfall > 0 ? ` | Shortfall ${fmt(sum.totalShortfall)}` : "";
    if (status) status.textContent = `Preview: Avg Net ${fmt(sum.avgIncomeDeliveredNet||0)} | End ${fmt(sum.totalEndBalance||0)}${shortTxt}`;
  } finally {
    restoreFinPlanEditableState(lockedInputs);
    recalcFinPlanWealthForecastBalance();
    updateFinPlanAllocTotal();
    updateFinPlanDownMarketState();
  }
}

async function loadFinPlan(clientUserId){
  const status = $("#finPlanStatusLabel");
  const label = $("#finPlanClientLabel");
  if (status) status.textContent = "Loading…";
  if (label) label.textContent = clientUserId || "";
  try{
    const planUrl = `/clients/${encodeURIComponent(clientUserId)}/financial-plan?clientUserId=${encodeURIComponent(clientUserId)}`;
    const res = await fetch(planUrl, { credentials:"include" });
    if (!res.ok){
      throw new Error(`Load failed (${res.status})`);
    }
    const text = await res.text();
    let data;
    try{
      data = JSON.parse(text);
    }catch(parseErr){
      throw new Error("Unexpected response while loading plan.");
    }
    finPlanVersion = data.version || 0;
    $("#finPlanVersion").value = finPlanVersion;
    if (data.clientProfileId) $("#finPlanClientProfileId").value = data.clientProfileId;
    if (data.clientUserId) $("#finPlanClientUserId").value = data.clientUserId;
    if (data.clientName) $("#finPlanClientLabel").textContent = data.clientName;
    else $("#finPlanClientLabel").textContent = data.clientUserId || "";
    hydrateFinPlan(data.jsonData);
    if (status) status.textContent = data.updatedUtc ? `Last updated ${new Date(data.updatedUtc).toLocaleString()}` : "Loaded";
  }catch(err){
    if (status) status.textContent = "Failed to load plan.";
    showFinPlanError(err?.message || "Failed to load plan.");
  }
}

function hydrateFinPlan(jsonData){
  let payload = {};
  try { payload = JSON.parse(jsonData || "{}"); } catch { payload = {}; }
  const wf = payload.wealthForecast?.inputs || {};
  Object.keys(wf).forEach(id => { const el = document.getElementById(id); if (el) el.value = wf[id]; });

  const canonical = payload.distribution?.canonicalInput || {};
  const setVal = (id, v) => { const el = document.getElementById(id); if (el) el.value = v ?? ""; };
  const setChecked = (id, v) => { const el = document.getElementById(id); if (el) el.checked = !!v; };
  setVal("wfd_base", canonical.retirementBase ?? "");
  setChecked("wfd_manualOverride", !!canonical.manualBaseOverride);
  setVal("wfd_retAge", canonical.retireAge ?? "");
  setVal("wfd_endAge", canonical.endAge ?? "");
  setVal("wfd_emergency", canonical.emergencyReserve ?? "");
  setVal("wfd_desiredIncome", canonical.desiredIncome ?? "");
  setVal("wfd_guaranteedIncome", canonical.guaranteedIncome ?? "");
  setVal("wfd_invAlloc", canonical.invAllocPct ?? "");
  setVal("wfd_invReturn", canonical.invReturnPct ?? "");
  setVal("wfd_invTax", canonical.invTaxPct ?? "");
  setVal("wfd_liAlloc", canonical.liAllocPct ?? "");
  setVal("wfd_liGrowth", canonical.liReturnPct ?? "");
  setVal("wfd_liTax", canonical.liTaxPct ?? "");
  setVal("wfd_annAlloc", canonical.annAllocPct ?? "");
  setVal("wfd_annReturn", canonical.annReturnPct ?? "");
  setVal("wfd_annTax", canonical.annTaxPct ?? "");
  const liAccess = canonical.liAccessMode || "withdrawal";
  const liType = canonical.liPolicyType || "whole";
  const annDesign = canonical.annDesign || "fixed";
  const liAccessEl = document.getElementById("wfd_liAccess"); if (liAccessEl) liAccessEl.value = liAccess;
  const liTypeEl = document.getElementById("wfd_liType"); if (liTypeEl) liTypeEl.value = liType;
  const annDesignEl = document.getElementById("wfd_annDesign"); if (annDesignEl) annDesignEl.value = annDesign;
  const setCheck = (id, val, fallback = false) => {
    const el = document.getElementById(id);
    if (el) el.checked = (typeof val === "boolean") ? val : fallback;
  };
  setCheck("wfd_invDownMkt", canonical.invDownMarket, false);
  setCheck("wfd_liDownMkt", canonical.liDownMarket, true);
  setCheck("wfd_annDownMkt", canonical.annDownMarket, true);
  setCheck("wfd_protectInvest", canonical.protectInvest, true);
  setCheck("wfd_annIncomeRider", canonical.annIncomeRider, false);
  setCheck("wfd_annDbRider", canonical.annDbRider, false);
  setVal("wfd_annRollup", canonical.annRollupPct ?? "");
  setVal("wfd_liEfficiency", canonical.liEfficiencyPct ?? "");
  setVal("wfd_annDeath", canonical.annDeathBenefit ?? "");
  setVal("wfd_liDeath", canonical.liDeathBenefit ?? "");
  setVal("wfd_strategy", canonical.strategy ?? "proportional");
  setVal("wfd_gapSource", canonical.gapSource ?? "life");
  setVal("wfd_downThreshold", canonical.downThreshold ?? "0");
  setVal("wfd_scenarioMode", canonical.scenarioMode ?? "fixed");
  const manualReturnsEl = document.getElementById("wfd_manualReturns");
  if (manualReturnsEl && Array.isArray(canonical.manualReturns)) {
    manualReturnsEl.value = canonical.manualReturns.join(", ");
  }
  finPlanAllocManual = true;
  recalcFinPlanWealthForecastBalance();
  updateFinPlanAllocTotal();
  updateFinPlanDownMarketState();
}

function updateFinPlanDownMarketState(){
  const gid = (id) => document.getElementById(id);
  const rows = [
    { chk:'wfd_invDownMkt', badge:'wfd_invDmBadge', card:'wfd_invCard' },
    { chk:'wfd_liDownMkt',  badge:'wfd_liDmBadge',  card:'wfd_liCard' },
    { chk:'wfd_annDownMkt', badge:'wfd_annDmBadge', card:'wfd_annCard' }
  ];
  rows.forEach(r => {
    const on = !!gid(r.chk)?.checked;
    const badge = gid(r.badge);
    const card = gid(r.card);
    if (badge){
      badge.textContent = on ? 'Down-Market: On' : 'Down-Market: Off';
      badge.classList.toggle('off', !on);
    }
    if (card) card.classList.toggle('wfd-dm-off', !on);
  });
}

function captureFinPlanEditableState(){
  const state = { inputs:{}, checks:{} };
  [
    'wbStartingBalance','wbIncome','wbYears','wbInflation','wbReturn','wbTax','wbLiabilities','wbLifestyle',
    'wfd_retAge','wfd_endAge','wfd_emergency','wfd_desiredIncome','wfd_guaranteedIncome',
    'wfd_invAlloc','wfd_invReturn','wfd_invTax',
    'wfd_liAlloc','wfd_liGrowth','wfd_liTax','wfd_liEfficiency','wfd_liDeath','wfd_liType','wfd_liAccess',
    'wfd_annAlloc','wfd_annReturn','wfd_annTax','wfd_annDeath','wfd_annRollup','wfd_annDesign',
    'wfd_strategy','wfd_gapSource','wfd_downThreshold','wfd_scenarioMode','wfd_manualReturns'
  ].forEach(id => {
    const el = document.getElementById(id);
    if (el) state.inputs[id] = el.value;
  });
  if (document.getElementById('wfd_manualOverride')?.checked) {
    const baseEl = document.getElementById('wfd_base');
    if (baseEl) state.inputs.wfd_base = baseEl.value;
  }
  ['wfd_manualOverride','wfd_invDownMkt','wfd_liDownMkt','wfd_annDownMkt','wfd_protectInvest','wfd_annIncomeRider','wfd_annDbRider'].forEach(id => {
    const el = document.getElementById(id);
    if (el) state.checks[id] = !!el.checked;
  });
  return state;
}

function restoreFinPlanEditableState(state){
  if (!state) return;
  Object.entries(state.inputs || {}).forEach(([id, value]) => {
    const el = document.getElementById(id);
    if (el) el.value = value ?? '';
  });
  Object.entries(state.checks || {}).forEach(([id, value]) => {
    const el = document.getElementById(id);
    if (el) el.checked = !!value;
  });
}

function showFinPlanError(msg){
  const errEl = $("#finPlanError");
  if (!errEl) return;
  errEl.textContent = msg || "";
  errEl.style.display = msg ? "block" : "none";
}

function ensureFinPlanSelectOptions(){
  const setOptions = (id, opts) => {
    const sel = document.getElementById(id);
    if (!sel) return;
    if (sel.children.length === 0){
      opts.forEach(o => {
        const opt = document.createElement("option");
        opt.value = o.value; opt.textContent = o.label;
        sel.appendChild(opt);
      });
    }
  };
  setOptions("wfd_liType", [
    { value:"whole",      label:"Whole Life" },
    { value:"iul",        label:"Indexed UL" },
    { value:"vul",        label:"Variable UL" },
    { value:"legacy_rpu", label:"Legacy / Reduced Paid-Up" }
  ]);

  setOptions("wfd_liAccess", [
    { value:"withdrawal", label:"Withdrawals" },
    { value:"loan",       label:"Policy Loans" },
    { value:"none",       label:"No Distributions" }
  ]);

  setOptions("wfd_annDesign", [
    { value:"fixed",        label:"Fixed Annuity" },
    { value:"fixedIndexed", label:"Fixed Indexed Annuity" },
    { value:"variable",     label:"Variable Annuity" }
  ]);

  // Keep all DP controls visible in CRM quick view for exact parity with Finance DP.
}

function updateFinPlanAllocTotal(trigger = "generic"){
  const inv = parseFloat((($("#wfd_invAlloc")?.value || "").replace(/[^0-9.\-]/g,""))) || 0;
  const li  = parseFloat((($("#wfd_liAlloc")?.value || "").replace(/[^0-9.\-]/g,""))) || 0;
  const ann = parseFloat((($("#wfd_annAlloc")?.value || "").replace(/[^0-9.\-]/g,""))) || 0;
  let invPct = inv;
  let liPct  = li;
  let annPct = ann;

  if (invPct >= 100){
    invPct = 100;
    liPct = 0;
    annPct = 0;
    finPlanAllocManual = false;
    $("#wfd_invAlloc").value = "100";
    $("#wfd_liAlloc").value = "0";
    $("#wfd_annAlloc").value = "0";
  } else if (trigger === "inv" && !finPlanAllocManual){
    const remaining = Math.max(0, 100 - invPct);
  const half = +(remaining / 2).toFixed(1);
    liPct = half;
    annPct = remaining - half;
    $("#wfd_liAlloc").value = liPct.toString();
    $("#wfd_annAlloc").value = annPct.toString();
  }

  const total = invPct + liPct + annPct;
  const manualOverride = !!document.getElementById("wfd_manualOverride")?.checked;
  const base = manualOverride
    ? (parseFloat(((document.getElementById("wfd_base")?.value || "").replace(/[^0-9.\-]/g, ""))) || 0)
    : (window.__wfFinalBalance || 0);
  const el = document.getElementById("finPlanAllocTotal");
  if (el){
    el.textContent = `${total.toFixed(1)}%`;
    el.classList.toggle("text-success", Math.abs(total-100) < 0.1);
    el.classList.toggle("text-warning", Math.abs(total-100) >= 0.1);
  }

  const setMoney = (id, val) => {
    const target = document.getElementById(id);
    if (target) target.value = Math.round(val || 0).toLocaleString();
  };
  setMoney('wfd_invAmt', base * (invPct / 100));
  setMoney('wfd_liAmt', base * (liPct / 100));
  setMoney('wfd_annAmt', base * (annPct / 100));

  // DP visual parity: update badges and bars
  const totEl = document.getElementById('wfd_allocTotal');
  const stEl  = document.getElementById('wfd_allocStatus');
  if (totEl){
    const ready = Math.abs(total - 100) < 0.11;
    totEl.textContent = `${total.toFixed(1)}%`;
    totEl.className = ready ? 'wfd-alloc-good' : 'wfd-alloc-bad';
    if (stEl){
      stEl.textContent = ready ? '✓ Ready' : '— must equal 100%';
      stEl.style.color = ready ? '#16a34a' : '#dc2626';
    }
  }

  const mx = Math.max(invPct, liPct, annPct, 1);
  const invBar = document.getElementById('wfd_invBar');
  const liBar  = document.getElementById('wfd_liBar');
  const annBar = document.getElementById('wfd_annBar');
  if (invBar) invBar.style.height = Math.max(invPct / mx * 100, 3) + '%';
  if (liBar)  liBar.style.height  = Math.max(liPct  / mx * 100, 3) + '%';
  if (annBar) annBar.style.height = Math.max(annPct / mx * 100, 3) + '%';
}

async function saveFinPlan(){
  const lockedInputs = captureFinPlanEditableState();
  const payload = finPlanPayload();
  const canonical = payload.distribution?.canonicalInput;
  const errs = window.DP_VALIDATORS?.validatePlanInput ? window.DP_VALIDATORS.validatePlanInput(canonical) : [{message:"Validator unavailable"}];
  if (errs.length){
    showFinPlanError(errs.map(e=>e.message).join("; "));
    return;
  }
  const clientProfileId = $("#finPlanClientProfileId")?.value?.trim() || "";
  const clientUserId = finPlanActiveClientId || $("#finPlanClientUserId")?.value || "";
  const routeId = clientProfileId || clientUserId;
  if (!routeId){
    showFinPlanError("Missing client id.");
    return;
  }
  if (!finPlanVersion){
    showFinPlanError("Plan not loaded — load the client before saving.");
    return;
  }
  showFinPlanError("");
  $("#finPlanStatusLabel").textContent = "Saving…";
  const planUrl = `/clients/${encodeURIComponent(routeId)}/financial-plan?clientUserId=${encodeURIComponent(clientUserId)}`;
  try {
    const res = await fetch(planUrl, {
      method:"POST",
      credentials:"include",
      headers:{
        "Content-Type":"application/json",
        "RequestVerificationToken": getAntiForgeryToken()
      },
      body: JSON.stringify({ clientProfileId, clientUserId, jsonData: JSON.stringify(payload), version: payload.version })
    });
    if (!res.ok){
      const errorText = (await res.text().catch(() => "")).trim();
      if (res.status === 409){
        showFinPlanError("Version conflict — reload the latest plan before saving.");
      } else {
        showFinPlanError(errorText || `Save failed (${res.status}).`);
      }
      $("#finPlanStatusLabel").textContent = "Save failed";
      return;
    }
    const data = await res.json();
    finPlanVersion = data.version || payload.version;
    $("#finPlanVersion").value = finPlanVersion;
    if (data.clientProfileId) $("#finPlanClientProfileId").value = data.clientProfileId;
    if (data.clientUserId) $("#finPlanClientUserId").value = data.clientUserId;
    $("#finPlanStatusLabel").textContent = data.updatedUtc ? `Saved ${new Date(data.updatedUtc).toLocaleString()}` : "Saved";
    toast("Plan saved.", { autoClose: 1800 });
  } finally {
    restoreFinPlanEditableState(lockedInputs);
    recalcFinPlanWealthForecastBalance();
    updateFinPlanAllocTotal();
    updateFinPlanDownMarketState();
  }
}

document.getElementById("finPlanSaveBtn")?.addEventListener("click", () => { void saveFinPlan(); });
document.getElementById("wfd_invAlloc")?.addEventListener("input", ()=>{ updateFinPlanAllocTotal("inv"); });
document.getElementById("wfd_liAlloc")?.addEventListener("input", ()=>{ finPlanAllocManual = true; updateFinPlanAllocTotal("li"); });
document.getElementById("wfd_annAlloc")?.addEventListener("input", ()=>{ finPlanAllocManual = true; updateFinPlanAllocTotal("ann"); });
['wfd_invDownMkt','wfd_liDownMkt','wfd_annDownMkt'].forEach(id=>{
  const el = document.getElementById(id);
  if (!el) return;
  el.addEventListener('change', ()=>{ updateFinPlanDownMarketState(); scheduleDpPreview(); });
});
['wbStartingBalance','wbIncome','wbYears','wbInflation','wbReturn','wbTax','wbLiabilities','wbLifestyle'].forEach(id=>{
  const el = document.getElementById(id);
  if (!el) return;
  ['input','change','blur'].forEach(evt => el.addEventListener(evt, ()=>{ recalcFinPlanWealthForecastBalance(); scheduleDpPreview(); }));
});
['wfd_retAge','wfd_endAge','wfd_emergency','wfd_desiredIncome','wfd_guaranteedIncome','wfd_invAlloc','wfd_invReturn','wfd_invTax','wfd_liAlloc','wfd_liGrowth','wfd_liTax','wfd_liAccess','wfd_liType','wfd_annAlloc','wfd_annReturn','wfd_annTax','wfd_annDesign','wfd_manualOverride','wfd_base','wfd_protectInvest','wfd_annIncomeRider','wfd_annDbRider','wfd_annRollup','wfd_liEfficiency','wfd_annDeath','wfd_liDeath','wfd_strategy','wfd_gapSource','wfd_downThreshold','wfd_scenarioMode','wfd_manualReturns'].forEach(id=>{
  const el = document.getElementById(id);
  if (!el) return;
  ['input','change','blur'].forEach(evt=> el.addEventListener(evt, scheduleDpPreview));
});
updateFinPlanDownMarketState();
scheduleDpPreview();

function norm(v){ return (v || "").toString().trim(); }
function fullName(row){ return (norm(row.dataset.first) + " " + norm(row.dataset.last)).trim(); }

function escapeHtml(value){
  return (value ?? "").toString()
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

function formatCurrency(value){
  const num = Number(value) || 0;
  return num.toLocaleString("en-US", { style: "currency", currency: "USD", maximumFractionDigits: 0 });
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

function renderEmailLinkHtml(email){
  const value = norm(email);
  if (!value) return "No email";

  const fakeClass = isPlaceholderEmail(value) ? " crm-email-fake" : "";
  return `<a class=\"link link-email${fakeClass}\" href=\"mailto:${escapeHtml(value)}\">${escapeHtml(value)}</a>`;
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
  const persistent = !!opts.persistent;
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

  // Standalone partial pages (e.g. /Clients/Actions/{id}) do not render #__af.
  const any = document.querySelector('input[name="__RequestVerificationToken"]');
  return any?.value || "";
}

async function postJson(url, payload){
  const token = getAntiForgeryToken();
  const res = await fetch(url, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "RequestVerificationToken": token
    },
    credentials: "include",
    body: JSON.stringify(payload)
  });

  if (!res.ok){
    const text = await res.text().catch(() => "");
    throw new Error(text || `Request failed: ${res.status}`);
  }

  return await res.json();
}

async function loadQuickView(clientId){
  const url = `/Clients/QuickView?clientUserId=${encodeURIComponent(clientId)}`;
  console.info("Quick View request", { clientUserId: clientId, url });
  const res = await fetch(url, {
    credentials: "include"
  });

  const text = await res.text().catch(() => "");

  if (!res.ok){
    const err = new Error(text || `Quick View failed: ${res.status} ${res.statusText}`);
    err.status = res.status;
    err.statusText = res.statusText;
    err.body = text;
    err.url = url;
    console.error("Quick View failed", { clientUserId: clientId, status: res.status, statusText: res.statusText, body: text });
    throw err;
  }

  try{
    return JSON.parse(text);
  }catch(parseErr){
    const err = new Error("Quick View JSON parse failed");
    err.body = text;
    throw err;
  }
}

async function loadAdvancedMarketsInputs(clientUserId, clientProfileId){
  const query = new URLSearchParams();
  if (clientUserId) query.set("clientUserId", clientUserId);
  if (clientProfileId) query.set("clientProfileId", clientProfileId);

  let data = null;

  // Primary fetch
  try{
    const res = await fetch(`/Clients/AdvancedMarketsInputs?${query.toString()}`, {
      credentials: "include"
    });

    if (res.ok){
      data = await res.json();
    }else{
      const text = await res.text().catch(() => "");
      console.warn("Advanced Markets inputs primary load failed", res.status, text);
    }
  }catch(err){
    console.warn("Advanced Markets inputs primary load error", err);
  }

  // Fallback to finance-state if primary missing or empty
  const missing = !data?.hasSavedInputs || !data?.inputs || (typeof data?.fingerprint === "string" && data?.fingerprint === "(none)");
  if (!data || missing) {
    try{
      const fs = new URLSearchParams();
      fs.set("toolId", "AdvancedMarketsInputs");
      if (clientProfileId) fs.set("clientProfileId", clientProfileId);
      if (clientUserId) fs.set("clientUserId", clientUserId);
      const fsRes = await fetch(`/api/finance-state/load?${fs.toString()}`, { credentials: "include" });
      if (fsRes.ok){
        const fsJson = await fsRes.json();
        if (fsJson?.found && fsJson.jsonState){
          const parsed = JSON.parse(fsJson.jsonState || "{}");
          data = {
            ...(data || {}),
            hasSavedInputs: true,
            inputs: parsed,
            clientProfileId: fsJson.clientProfileId || clientProfileId,
            clientUserId: clientUserId
          };
        }
      }
    }catch(err){
      console.warn("Advanced Markets finance-state fallback load failed", err);
    }
  }

  // If still nothing, return an empty structure rather than null to avoid wiping fields that may have drafts.
  return data || { hasSavedInputs: false, inputs: {}, clientProfileId, clientUserId };
}

async function loadProtectionSnapshot(clientUserId, clientProfileId){
  const query = new URLSearchParams({ toolId: PROTECTION_SNAPSHOT_TOOL_ID });
  if (clientUserId) query.set("clientUserId", clientUserId);
  if (clientProfileId) query.set("clientProfileId", clientProfileId);

  const res = await fetch(`/api/finance-state/load?${query.toString()}`, {
    credentials: "include"
  });

  if (!res.ok){
    const text = await res.text().catch(() => "");
    throw new Error(text || `Protection Snapshot failed: ${res.status}`);
  }

  return await res.json();
}

function normalizeProtectionSnapshotState(raw){
  const source = raw && typeof raw === "object" ? raw : {};
  const normalizeList = (value) => Array.isArray(value)
    ? value.filter(Boolean).map(item => String(item).trim()).filter(Boolean)
    : [];

  return {
    householdStage: String(source.householdStage || "Foundation"),
    primaryGoal: String(source.primaryGoal || "Protect income"),
    housingStatus: String(source.housingStatus || "Own"),
    dependentsCount: Number.isFinite(Number(source.dependentsCount)) ? Math.max(0, Number(source.dependentsCount)) : 0,
    emergencyFundMonths: Number.isFinite(Number(source.emergencyFundMonths)) ? Math.max(0, Number(source.emergencyFundMonths)) : 0,
    incomeProtectionYears: Number.isFinite(Number(source.incomeProtectionYears)) ? Math.max(0, Number(source.incomeProtectionYears)) : 0,
    legalDocsStatus: String(source.legalDocsStatus || "Not started"),
    beneficiariesStatus: String(source.beneficiariesStatus || "Needs review"),
    legacyPlanStatus: String(source.legacyPlanStatus || "Needs review"),
    reviewCadence: String(source.reviewCadence || "Semiannual"),
    hasLifeInsurance: !!source.hasLifeInsurance,
    hasDisabilityCoverage: !!source.hasDisabilityCoverage,
    hasMortgageProtection: !!source.hasMortgageProtection,
    hasEquityProtection: !!source.hasEquityProtection,
    hasLongTermCarePlan: !!source.hasLongTermCarePlan,
    hasBusinessDisabilityCoverage: !!source.hasBusinessDisabilityCoverage,
    hasEstateDocuments: !!source.hasEstateDocuments,
    hasEmergencyContacts: !!source.hasEmergencyContacts,
    hasSharedDocumentVault: !!source.hasSharedDocumentVault,
    ownsBusiness: !!source.ownsBusiness,
    hasEmployees: !!source.hasEmployees,
    drivesForWork: !!source.drivesForWork,
    givesProfessionalAdvice: !!source.givesProfessionalAdvice,
    handlesCustomerData: !!source.handlesCustomerData,
    hasHomeInsurance: !!source.hasHomeInsurance,
    hasRentersInsurance: !!source.hasRentersInsurance,
    hasAutoInsurance: !!source.hasAutoInsurance,
    hasUmbrellaCoverage: !!source.hasUmbrellaCoverage,
    hasGeneralLiability: !!source.hasGeneralLiability,
    hasProfessionalLiability: !!source.hasProfessionalLiability,
    hasCyberCoverage: !!source.hasCyberCoverage,
    hasWorkersComp: !!source.hasWorkersComp,
    hasCommercialAuto: !!source.hasCommercialAuto,
    hasWillInPlace: !!source.hasWillInPlace,
    hasTrustInPlace: !!source.hasTrustInPlace,
    priorityFocusAreas: normalizeList(source.priorityFocusAreas),
    protectionNeeds: normalizeList(source.protectionNeeds),
    recentLifeEvents: normalizeList(source.recentLifeEvents),
    agentFollowUpFocus: String(source.agentFollowUpFocus || ""),
    clientNotes: String(source.clientNotes || ""),
    nextReviewUtc: source.nextReviewUtc ? String(source.nextReviewUtc).slice(0, 10) : ""
  };
}

function computeProtectionSnapshotScore(snapshot){
  let total = 0;
  total += snapshot.hasLifeInsurance ? 16 : 0;
  total += snapshot.hasDisabilityCoverage ? 14 : 0;
  total += snapshot.hasMortgageProtection ? 9 : 0;
  total += snapshot.hasEquityProtection ? 8 : 0;
  total += snapshot.hasLongTermCarePlan ? 10 : 0;
  total += snapshot.hasBusinessDisabilityCoverage ? 8 : 0;
  total += snapshot.hasEstateDocuments ? 12 : 0;
  total += snapshot.hasEmergencyContacts ? 8 : 0;
  total += snapshot.hasSharedDocumentVault ? 8 : 0;
  total += snapshot.hasHomeInsurance ? 6 : 0;
  total += snapshot.hasRentersInsurance ? 4 : 0;
  total += snapshot.hasAutoInsurance ? 6 : 0;
  total += snapshot.hasUmbrellaCoverage ? 6 : 0;
  total += snapshot.hasGeneralLiability ? 6 : 0;
  total += snapshot.hasProfessionalLiability ? 6 : 0;
  total += snapshot.hasCyberCoverage ? 5 : 0;
  total += snapshot.hasWorkersComp ? 4 : 0;
  total += snapshot.hasCommercialAuto ? 4 : 0;
  total += Math.min(snapshot.emergencyFundMonths, 6) * 3;
  total += Math.min(snapshot.incomeProtectionYears, 12) * 2;
  total += snapshot.hasWillInPlace ? 5 : 0;
  total += snapshot.hasTrustInPlace ? 5 : 0;
  total += snapshot.legalDocsStatus === "Complete" ? 8 : snapshot.legalDocsStatus === "In progress" ? 4 : 0;
  total += snapshot.beneficiariesStatus === "Current" ? 7 : snapshot.beneficiariesStatus === "Partially updated" ? 3 : 0;
  total += snapshot.legacyPlanStatus === "Clear and documented" ? 7 : snapshot.legacyPlanStatus === "In progress" ? 3 : 0;
  return Math.max(0, Math.min(100, total));
}

function protectionSnapshotTone(score){
  if (score >= 75) return "Strong";
  if (score >= 50) return "In Progress";
  return "Needs Attention";
}

function buildProtectionSnapshotNextSteps(snapshot){
  const steps = [];
  if (!snapshot.hasLifeInsurance) steps.push("Review life insurance alignment.");
  if (!snapshot.hasDisabilityCoverage) steps.push("Evaluate disability income protection.");
  if (snapshot.housingStatus === "Own" && !snapshot.hasMortgageProtection) steps.push("Review mortgage protection for payment continuity.");
  if (snapshot.housingStatus === "Own" && !snapshot.hasEquityProtection) steps.push("Review equity protection strategy for major claims.");
  if (snapshot.housingStatus === "Own" && !snapshot.hasHomeInsurance) steps.push("Confirm homeowners coverage and liability limits.");
  if (snapshot.housingStatus === "Rent" && !snapshot.hasRentersInsurance) steps.push("Add renters coverage for belongings and liability.");
  if (!snapshot.hasAutoInsurance) steps.push("Review auto policy liability limits.");
  if (snapshot.ownsBusiness && !snapshot.hasBusinessDisabilityCoverage) steps.push("Add business disability/overhead protection for continuity.");
  if (snapshot.ownsBusiness && !snapshot.hasGeneralLiability) steps.push("Add general liability for business claim protection.");
  if (snapshot.givesProfessionalAdvice && !snapshot.hasProfessionalLiability) steps.push("Evaluate professional liability for advice/services risk.");
  if (snapshot.handlesCustomerData && !snapshot.hasCyberCoverage) steps.push("Review cyber liability coverage for data incidents.");
  if (snapshot.hasEmployees && !snapshot.hasWorkersComp) steps.push("Review workers' comp obligations and protection.");
  if (snapshot.drivesForWork && !snapshot.hasCommercialAuto) steps.push("Review commercial auto exposure for work driving.");
  if (snapshot.emergencyFundMonths < 3) steps.push("Increase liquid emergency reserves.");
  if (snapshot.legalDocsStatus === "Not started") steps.push("Start legal document planning.");
  if (!snapshot.hasWillInPlace) steps.push("Create or update a will.");
  if (!snapshot.hasTrustInPlace) steps.push("Evaluate trust setup.");
  if (snapshot.beneficiariesStatus !== "Current") steps.push("Update beneficiaries.");
  if (!steps.length) steps.push("Maintain cadence and trigger updates from life events.");
  return steps.slice(0, 4);
}

function buildProtectionSnapshotPortalUrl(baseUrl){
  if (!baseUrl) return "";
  const separator = baseUrl.includes("?") ? "&" : "?";
  return `${baseUrl}${separator}returnUrl=${encodeURIComponent("/ProtectionSnapshot")}`;
}

function renderProtectionSnapshotSummary(snapshot, options = {}){
  const {
    loading = false,
    error = false,
    clientPortalUrl = "",
    hasSavedSnapshot = false
  } = options;

  const scorePill = $("#qvProtectionScorePill");
  const stagePill = $("#qvProtectionStagePill");
  const reviewPill = $("#qvProtectionReviewPill");
  const priorities = $("#qvProtectionPriorities");
  const lifeEvents = $("#qvProtectionLifeEvents");
  const coverageGrid = $("#qvProtectionCoverageGrid");
  const nextSteps = $("#qvProtectionNextSteps");
  const agentFocus = $("#qvProtectionAgentFocus");
  const clientNotes = $("#qvProtectionClientNotes");
  const btn = $("#btnOpenProtectionSnapshot");

  if (btn){
    const href = buildProtectionSnapshotPortalUrl(clientPortalUrl);
    btn.href = href || "#";
    btn.style.display = href ? "inline-flex" : "none";
  }

  if (loading){
    if (scorePill) scorePill.textContent = "Readiness Loading…";
    if (stagePill) stagePill.textContent = "Stage Loading…";
    if (reviewPill) reviewPill.textContent = "Review Loading…";
    if (priorities) priorities.textContent = "Loading shared snapshot…";
    if (lifeEvents) lifeEvents.textContent = "Loading shared snapshot…";
    if (nextSteps) nextSteps.innerHTML = `<div class="tiny">Loading shared snapshot…</div>`;
    if (agentFocus) agentFocus.textContent = "—";
    if (clientNotes) clientNotes.textContent = "—";
    return;
  }

  if (error || !snapshot){
    if (scorePill) scorePill.textContent = "Readiness Unavailable";
    if (stagePill) stagePill.textContent = "Stage —";
    if (reviewPill) reviewPill.textContent = "Review —";
    if (priorities) priorities.textContent = "Could not load the shared snapshot.";
    if (lifeEvents) lifeEvents.textContent = "—";
    if (nextSteps) nextSteps.innerHTML = `<div class="tiny">No shared snapshot available right now.</div>`;
    if (agentFocus) agentFocus.textContent = "—";
    if (clientNotes) clientNotes.textContent = "—";
    return;
  }

  const score = computeProtectionSnapshotScore(snapshot);
  if (scorePill) scorePill.textContent = `Readiness ${score} • ${protectionSnapshotTone(score)}`;
  if (stagePill) stagePill.textContent = `Stage ${snapshot.householdStage || "Foundation"}`;
  if (reviewPill) reviewPill.textContent = hasSavedSnapshot
    ? `Review ${snapshot.reviewCadence || "Semiannual"}${snapshot.nextReviewUtc ? ` • ${snapshot.nextReviewUtc}` : ""}`
    : "Review Not saved yet";
  const combinedPriorities = [
    ...(snapshot.priorityFocusAreas || []),
    ...(snapshot.protectionNeeds || [])
  ];
  if (priorities) priorities.textContent = combinedPriorities.length
    ? Array.from(new Set(combinedPriorities)).join(", ")
    : "No priority focus selected yet.";
  if (lifeEvents) lifeEvents.textContent = snapshot.recentLifeEvents.length
    ? snapshot.recentLifeEvents.join(", ")
    : "No life events selected.";
  if (agentFocus) agentFocus.textContent = snapshot.agentFollowUpFocus || "No agent follow-up focus saved yet.";
  if (clientNotes) clientNotes.textContent = snapshot.clientNotes || "No client notes saved yet.";

  if (coverageGrid){
    const items = [
      ["Life Insurance", snapshot.hasLifeInsurance],
      ["Disability", snapshot.hasDisabilityCoverage],
      ["Mortgage Protection", snapshot.hasMortgageProtection],
      ["Equity Protection", snapshot.hasEquityProtection],
      ["Long-Term Care", snapshot.hasLongTermCarePlan],
      ["Business Disability", snapshot.hasBusinessDisabilityCoverage],
      ["Home", snapshot.hasHomeInsurance],
      ["Renters", snapshot.hasRentersInsurance],
      ["Auto", snapshot.hasAutoInsurance],
      ["Umbrella", snapshot.hasUmbrellaCoverage],
      ["General Liability", snapshot.hasGeneralLiability],
      ["Professional Liability", snapshot.hasProfessionalLiability],
      ["Cyber", snapshot.hasCyberCoverage],
      ["Workers' Comp", snapshot.hasWorkersComp],
      ["Commercial Auto", snapshot.hasCommercialAuto],
      ["Estate Docs", snapshot.hasEstateDocuments],
      ["Will", snapshot.hasWillInPlace],
      ["Trust", snapshot.hasTrustInPlace],
      ["Emergency Contacts", snapshot.hasEmergencyContacts],
      ["Document Vault", snapshot.hasSharedDocumentVault]
    ];
    coverageGrid.innerHTML = items.map(([label, on]) => `<span class="pill">${label} ${on ? "✓" : "—"}</span>`).join("");
  }

  if (nextSteps){
    nextSteps.innerHTML = buildProtectionSnapshotNextSteps(snapshot)
      .map(step => `<div class="event"><div class="top"><div class="type">Next Step</div></div><div class="body">${safeHtml(step)}</div></div>`)
      .join("");
  }
}

async function hydrateProtectionSnapshot(row, detail){
  const requestClientId = row?.dataset?.clientId || "";
  renderProtectionSnapshotSummary(null, {
    loading: true,
    clientPortalUrl: detail?.clientPortalUrl || ""
  });

  try {
    const payload = await loadProtectionSnapshot(requestClientId, detail?.clientProfileId || row?.dataset?.clientProfileId || "");
    if (activeClientId !== requestClientId) return;
    const snapshot = normalizeProtectionSnapshotState(payload?.jsonState ? JSON.parse(payload.jsonState) : {});
    renderProtectionSnapshotSummary(snapshot, {
      clientPortalUrl: detail?.clientPortalUrl || "",
      hasSavedSnapshot: !!payload?.found
    });
  } catch (err) {
    console.error("Protection Snapshot load failed", err);
    if (activeClientId !== requestClientId) return;
    renderProtectionSnapshotSummary(null, {
      error: true,
      clientPortalUrl: detail?.clientPortalUrl || ""
    });
  }
}

function getNestedValue(source, path){
  return path.split(".").reduce((acc, key) => {
    if (acc == null || typeof acc !== "object") return undefined;
    return acc[key];
  }, source);
}

function setNestedValue(target, path, value){
  const keys = path.split(".");
  let cursor = target;
  keys.forEach((key, index) => {
    if (index === keys.length - 1){
      cursor[key] = value;
      return;
    }
    if (!cursor[key] || typeof cursor[key] !== "object") cursor[key] = {};
    cursor = cursor[key];
  });
}

function normalizeAdvancedMarketsFormValue(input){
  if (!input) return null;
  if (input.type === "checkbox") return !!input.checked;
  const name = input.getAttribute("name") || "";
  const raw = (input.value ?? "").toString().trim();
  if (!raw && advancedMarketsBooleanFieldNames.has(name)) return false;
  if (!raw) return null;
  if (name === "Strategy.Selected"){
    const strategyIndex = advancedMarketsStrategyOrder.indexOf(raw);
    if (strategyIndex >= 0) return strategyIndex;
    if (/^\d+$/.test(raw)) return Number(raw);
    return 0;
  }
  if (input.tagName === "SELECT" && (raw === "true" || raw === "false")) return raw === "true";
  if (input.type === "number"){
    const numeric = Number(raw);
    if (!Number.isFinite(numeric)) return null;
    if (advancedMarketsIntegerFieldNames.has(name)) return Number.isInteger(numeric) ? numeric : null;
    return numeric;
  }
  if (input.classList.contains("money-input") || input.classList.contains("pct-input")){
    const numeric = Number(raw.replace(/,/g, ""));
    return Number.isFinite(numeric) ? numeric : null;
  }
  return raw;
}

function serializeAdvancedMarketsForm(form){
  const payload = {};
  $$("[name]", form).forEach((input) => {
    const name = input.getAttribute("name");
    if (!name || name.startsWith("__")) return;
    setNestedValue(payload, name, normalizeAdvancedMarketsFormValue(input));
  });
  return payload;
}

function applyAdvancedMarketsInputsToForm(form, payload){
  $$("[name]", form).forEach((input) => {
    const name = input.getAttribute("name");
    if (!name) return;
    const value = getNestedValue(payload, name);
    if (value === undefined || value === null) {
      if (input.type === "checkbox") input.checked = false;
      else input.value = "";
      return;
    }
    if (input.type === "checkbox"){
      input.checked = !!value;
      return;
    }
    input.value = `${value}`;
    if (input.tagName === "SELECT") input.dataset.current = `${value}`;
  });
}

function isAdvancedMarketsEligible(recordType, explicitEligible){
  if (typeof explicitEligible === "boolean") return explicitEligible;
  const flag = norm(explicitEligible).toLowerCase();
  if (flag === "true") return true;
  if (flag === "false") return false;
  return norm(recordType).toLowerCase() === "businessclient";
}

function setAdvancedMarketsActionState(recordType, explicitEligible){
  if (!advancedMarketsActionRow) return;
  const show = isAdvancedMarketsEligible(recordType, explicitEligible);
  advancedMarketsActionRow.style.display = show ? "" : "none";
  if (!show && advancedMarketsSection){
    advancedMarketsSection.open = false;
  }
}

function wireClientActionForm(){
  const form = document.getElementById('clientQuickCreateActionForm');
  if (!form || form.dataset.bound === "1") return;
  form.dataset.bound = "1";
  const modalEl = ensureModalInBody('clientQuickCreateActionModal') || document.getElementById('clientQuickCreateActionModal');
  const actionsContainer = document.querySelector('#clientActionsContainer');
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
      const res = await fetch("/Clients/CreateAction", {
        method: "POST",
        headers: { "RequestVerificationToken": getAntiForgeryToken() },
        body: data,
        credentials: "include"
      });
      const html = await res.text();
      if (!res.ok) throw new Error(html || "Save failed");
      if (window.bootstrap && modalEl){
        const inst = bootstrap.Modal.getInstance(modalEl) || new bootstrap.Modal(modalEl);
        inst.hide();
        inst.dispose();
      }
      disposeModalById('clientQuickCreateActionModal');
      actionsContainer.innerHTML = html;
      wireClientActionForm(); // rebind events to new DOM
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

function wireClientActionListControls(){
  const actionsContainer = document.querySelector('#clientActionsContainer');
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
      const res = await fetch("/Dashboard/CompleteAction", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "RequestVerificationToken": getAntiForgeryToken(actionsContainer)
        },
        credentials: "include",
        body: JSON.stringify({ id: actionId })
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      await loadClientActionsPanel();
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
      const res = await fetch(form.getAttribute('action') || "", {
        method: "POST",
        headers: { "RequestVerificationToken": getAntiForgeryToken() },
        body: data,
        credentials: "include"
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      await loadClientActionsPanel();
      toast("Action deleted.");
    }catch(err){
      console.error(err);
      toast("Failed to delete action.", { error: true, persistent: true });
    }
  });
}

function loadClientActionsPanel(){
  const actionsContainer = document.querySelector('#clientActionsContainer');
  const requestedClientId = (activeClientId || drawer?.dataset?.clientId || "").toString().trim();
  if (!actionsContainer || !requestedClientId) return Promise.resolve(false);
  if (!activeClientId) activeClientId = requestedClientId;
  wireClientActionListControls();
  if (clientActionsLoadPromise) return clientActionsLoadPromise;

  disposeModalById('clientQuickCreateActionModal');
  actionsContainer.innerHTML = '<div class="text-muted">Loading actions...</div>';
  clientActionsLoadPromise = fetch(`/Clients/Actions?id=${encodeURIComponent(requestedClientId)}`)
    .then(async (r) => {
      const text = await r.text();
      if (!r.ok) throw new Error(text || `Failed to load actions (HTTP ${r.status})`);
      return text;
    })
    .then(html => {
      if (activeClientId !== requestedClientId) return false;
      actionsContainer.innerHTML = html;
      wireClientActionForm();
      return true;
    })
    .catch((err) => {
      if (activeClientId === requestedClientId){
        actionsContainer.innerHTML = `<div class="text-danger">${escapeHtml(err?.message || "Failed to load actions")}</div>`;
      }
      return false;
    })
    .finally(() => {
      clientActionsLoadPromise = null;
    });

  return clientActionsLoadPromise;
}

function loadClientCommitmentsPanel(){
  const commitmentsContainer = document.querySelector('#clientCommitmentsContainer');
  const requestedClientId = (activeClientId || drawer?.dataset?.clientId || "").toString().trim();
  if (!commitmentsContainer || !requestedClientId) return Promise.resolve(false);
  if (!activeClientId) activeClientId = requestedClientId;

  commitmentsContainer.innerHTML = '<div class="text-muted">Loading commitments...</div>';
  return fetch(`/Clients/Commitments?id=${encodeURIComponent(requestedClientId)}`)
    .then(async (r) => {
      const text = await r.text();
      if (!r.ok) throw new Error(text || `Failed to load commitments (HTTP ${r.status})`);
      return text;
    })
    .then(html => {
      if (activeClientId !== requestedClientId) return false;
      commitmentsContainer.innerHTML = html;
      wireClientCommitmentForm();
      wireClientCommitmentActions();
      return true;
    })
    .catch((err) => {
      if (activeClientId === requestedClientId){
        commitmentsContainer.innerHTML = `<div class="text-danger">${escapeHtml(err?.message || "Failed to load commitments")}</div>`;
      }
      return false;
    });
}

function wireClientCommitmentForm(){
  const form = document.querySelector('#clientCommitmentsContainer #clientCreateCommitmentForm');
  if (!form || form.dataset.bound === "1") return;
  form.dataset.bound = "1";
  const modalEl = ensureModalInBody('addClientCommitmentModal') || document.getElementById('addClientCommitmentModal');
  const errorBoxId = 'clientCommitmentFormError';

  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    const container = document.querySelector('#clientCommitmentsContainer');
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
    if (dueInput && dueInput.value){
      const local = new Date(dueInput.value);
      if (!isNaN(local.getTime())){
        data.set("DueDateUtc", local.toISOString());
      }
    }

    container.innerHTML = '<div class="text-muted">Saving...</div>';
    try{
      const res = await fetch(form.getAttribute('action') || "/Clients/CreateCommitment", {
        method: "POST",
        headers: { "RequestVerificationToken": getAntiForgeryToken() },
        body: data,
        credentials: "include"
      });
      const text = await res.text();
      if (!res.ok) throw new Error(text || "Save failed");
      if (window.bootstrap && modalEl){
        const inst = bootstrap.Modal.getInstance(modalEl) || new bootstrap.Modal(modalEl);
        inst.hide();
      }
      await loadClientCommitmentsPanel();
      await loadClientActionsPanel();
      toast("Commitment added.");
    }catch(err){
      console.error(err);
      container.innerHTML = '<div class="text-danger">Failed to save commitment.</div>';
      toast(err?.message || "Failed to save commitment", { error: true, persistent: true });
    }
  });
}

function wireClientCommitmentActions(){
  const buttons = document.querySelectorAll('#clientCommitmentsContainer [data-client-commitment-action]');
  buttons.forEach(btn => {
    if (btn.dataset.bound === "1") return;
    btn.dataset.bound = "1";
    btn.addEventListener('click', async (event) => {
      event.preventDefault();
      const action = btn.dataset.clientCommitmentAction;
      const id = btn.dataset.commitmentId;
      if (!action || !id) return;
      const container = document.querySelector('#clientCommitmentsContainer');
      if (container) container.innerHTML = '<div class="text-muted">Updating...</div>';

      const url = action === "fulfill"
        ? `/Clients/FulfillCommitment?id=${encodeURIComponent(id)}`
        : `/Clients/BreakCommitment?id=${encodeURIComponent(id)}`;
      try{
        const res = await fetch(url, {
          method: "POST",
          headers: { "RequestVerificationToken": getAntiForgeryToken() },
          credentials: "include"
        });
        const html = await res.text();
        if (!res.ok) throw new Error(html || "Update failed");
        if (container) container.innerHTML = html;
        wireClientCommitmentForm();
        wireClientCommitmentActions();
        toast(action === "fulfill" ? "Commitment fulfilled." : "Commitment broken.");
      }catch(err){
        console.error(err);
        if (container) container.innerHTML = '<div class="text-danger">Failed to update commitment.</div>';
        toast("Failed to update commitment", { error: true, persistent: true });
      }
    }, { once: true });
  });
}

function syncAdvancedMarketsMirrors(source){
  if (!advancedMarketsInputsForm || !source?.name) return;
  const selector = `[name="${source.name}"]`;
  $$(selector, advancedMarketsInputsForm).forEach((input) => {
    if (input === source) return;
    if (input.type === "checkbox"){
      input.checked = !!source.checked;
      return;
    }
    input.value = source.value;
  });
}

function clearAdvancedMarketsAutosaveTimer(){
  if (!advancedMarketsAutosaveTimer) return;
  clearTimeout(advancedMarketsAutosaveTimer);
  advancedMarketsAutosaveTimer = 0;
}

function buildAdvancedMarketsSavePayload(){
  if (!activeAdvancedMarketsClient?.clientProfileId || !advancedMarketsInputsForm) return null;
  return {
    clientProfileId: activeAdvancedMarketsClient.clientProfileId,
    clientUserId: activeAdvancedMarketsClient.clientUserId,
    inputs: serializeAdvancedMarketsForm(advancedMarketsInputsForm)
  };
}

function captureAdvancedMarketsFormState(form){
  const snapshot = {};
  if (!form) return snapshot;
  $$("[name]", form).forEach((input) => {
    const name = input.getAttribute("name");
    if (!name || name.startsWith("__")) return;
    if (input.type === "checkbox"){
      setNestedValue(snapshot, name, !!input.checked);
      return;
    }
    setNestedValue(snapshot, name, input.value ?? "");
  });
  return snapshot;
}

function hasAdvancedMarketsPendingPartialNumbers(form){
  if (!form) return false;
  return $$("[name]", form).some((input) => {
    const name = input.getAttribute("name");
    if (!name || name.startsWith("__")) return false;
    const raw = (input.value ?? "").toString().trim();
    if (!raw) return false;
    const isNumericLike = input.type === "number" || input.classList.contains("money-input") || input.classList.contains("pct-input");
    if (!isNumericLike) return false;
    return normalizeAdvancedMarketsFormValue(input) === null;
  });
}

function fingerprintAdvancedMarketsInputs(payload){
  try{
    return JSON.stringify(payload?.inputs || {});
  }catch{
    return "";
  }
}

const advancedMarketsCache = Object.create(null);

function cacheAdvancedMarketsInputs(clientProfileId, inputs, updatedUtc){
  if (!clientProfileId) return;
  const safeInputs = inputs || {};
  advancedMarketsCache[clientProfileId] = {
    inputs: safeInputs,
    updatedUtc: updatedUtc || null,
    fingerprint: fingerprintAdvancedMarketsInputs({ inputs: safeInputs })
  };
}

function getAdvancedMarketsCachedPayload(clientProfileId){
  if (!clientProfileId) return null;
  return advancedMarketsCache[clientProfileId] || null;
}

function syncAdvancedMarketsFingerprintFromForm(){
  advancedMarketsLastSavedFingerprint = fingerprintAdvancedMarketsInputs(buildAdvancedMarketsSavePayload());
  advancedMarketsInputsDirty = false;
}

function readAdvancedMarketsDraft(clientProfileId, clientUserId){
  if (!clientProfileId && !clientUserId) return null;
  const drafts = loadJSON(LS_ADVANCED_MARKETS_DRAFTS, {});
  return drafts?.[clientProfileId] || drafts?.[clientUserId] || null;
}

function writeAdvancedMarketsDraft(payload){
  if (!payload?.clientProfileId) return;
  const drafts = loadJSON(LS_ADVANCED_MARKETS_DRAFTS, {});
  drafts[payload.clientProfileId] = {
    clientProfileId: payload.clientProfileId,
    clientUserId: payload.clientUserId,
    clientName: activeAdvancedMarketsClient?.clientName || "",
    updatedUtc: new Date().toISOString(),
    inputs: advancedMarketsInputsForm ? captureAdvancedMarketsFormState(advancedMarketsInputsForm) : (payload.inputs || {})
  };
  if (payload.clientUserId && Object.prototype.hasOwnProperty.call(drafts, payload.clientUserId)){
    delete drafts[payload.clientUserId];
  }
  saveJSON(LS_ADVANCED_MARKETS_DRAFTS, drafts);
}

function clearAdvancedMarketsDraft(clientProfileId, clientUserId){
  if (!clientProfileId && !clientUserId) return;
  const drafts = loadJSON(LS_ADVANCED_MARKETS_DRAFTS, {});
  let changed = false;
  if (clientProfileId && Object.prototype.hasOwnProperty.call(drafts, clientProfileId)){
    delete drafts[clientProfileId];
    changed = true;
  }
  if (clientUserId && Object.prototype.hasOwnProperty.call(drafts, clientUserId)){
    delete drafts[clientUserId];
    changed = true;
  }
  if (!changed) return;
  saveJSON(LS_ADVANCED_MARKETS_DRAFTS, drafts);
}

async function persistAdvancedMarketsInputs(options = {}){
  const { auto = false, force = false, allowClosed = false, sessionId = advancedMarketsCurrentSession } = options;
  if (!activeAdvancedMarketsClient?.clientProfileId || !advancedMarketsInputsForm) return false;
  if (!allowClosed && !advancedMarketsSection?.open) return false;
  if (sessionId !== advancedMarketsCurrentSession) return false;
  if (auto && hasAdvancedMarketsPendingPartialNumbers(advancedMarketsInputsForm)){
    if (advancedMarketsSection?.open && advancedMarketsInputsStatus){
      advancedMarketsInputsStatus.textContent = "Finish the current number to auto-save";
    }
    return false;
  }

  const payload = buildAdvancedMarketsSavePayload();
  if (!payload) return false;
  writeAdvancedMarketsDraft(payload);

  const requestedClientProfileId = payload.clientProfileId;
  const payloadFingerprint = fingerprintAdvancedMarketsInputs(payload);
  if (auto && payloadFingerprint === "{}" && advancedMarketsLastSavedFingerprint && advancedMarketsLastSavedFingerprint !== "{}"){
    if (advancedMarketsInputsStatus){
      advancedMarketsInputsStatus.textContent = "Not auto-saving empty payload — keeping last saved inputs.";
    }
    return false;
  }
  // Never overwrite server state with an empty payload.
  if (payloadFingerprint === "{}") {
    return false;
  }

  console.info("Advanced Markets save payload", { clientProfileId: requestedClientProfileId, fingerprint: payloadFingerprint, payload });

  const requestEditVersion = advancedMarketsEditVersion;
  if (!force && !advancedMarketsInputsDirty && payloadFingerprint === advancedMarketsLastSavedFingerprint){
    return true;
  }

  if (advancedMarketsAutosaveInFlight){
    advancedMarketsAutosaveQueued = true;
    return false;
  }

  clearAdvancedMarketsAutosaveTimer();
  advancedMarketsAutosaveInFlight = true;

  if (btnSaveAdvancedMarketsInputs) btnSaveAdvancedMarketsInputs.disabled = true;
  if (advancedMarketsSection?.open && advancedMarketsInputsStatus){
    advancedMarketsInputsStatus.textContent = auto ? "Saving changes automatically…" : "Saving…";
  }

  try{
    const response = await postJson("/Clients/SaveAdvancedMarketsInputs", payload);
    const isSameClient = activeAdvancedMarketsClient?.clientProfileId === requestedClientProfileId;
    const isStaleResponse = advancedMarketsEditVersion !== requestEditVersion;
    const isStaleSession = sessionId !== advancedMarketsCurrentSession;

    if (isSameClient && !isStaleResponse && !isStaleSession){
      const savedPayload = { inputs: response.inputs || payload.inputs };
      advancedMarketsLastSavedFingerprint = fingerprintAdvancedMarketsInputs(savedPayload);
      advancedMarketsInputsDirty = false;
      // keep the local draft so Quick View fields stay hydrated after external fetches
      lastAdvancedMarketsClientId = requestedClientProfileId;
      cacheAdvancedMarketsInputs(requestedClientProfileId, savedPayload.inputs, response.updatedUtc);
      const row = rows.find(r => r.dataset.clientProfileId === requestedClientProfileId);
      if (row){
        row.dataset.amInputs = JSON.stringify(savedPayload.inputs || {});
        row.dataset.amFingerprint = advancedMarketsLastSavedFingerprint;
        row.dataset.amUpdatedUtc = response.updatedUtc || "";
      }
      console.info("Advanced Markets save confirmed", {
        clientProfileId: requestedClientProfileId,
        responseFingerprint: response.fingerprint || advancedMarketsLastSavedFingerprint,
        savedFingerprint: advancedMarketsLastSavedFingerprint,
        updatedUtc: response.updatedUtc
      });

      if (advancedMarketsSection?.open && advancedMarketsInputsStatus){
        const savedAt = formatAdvancedMarketsSavedAt(response.updatedUtc);
        const label = auto ? "Changes auto-saved" : "Saved successfully";
        advancedMarketsInputsStatus.textContent = savedAt
          ? `${label} • Updated ${savedAt}`
          : label;
      }
    }else if (isSameClient && (isStaleResponse || isStaleSession) && advancedMarketsSection?.open && advancedMarketsInputsStatus){
      advancedMarketsInputsStatus.textContent = "Changes pending…";
    }

    if (!auto) toast("Advanced Markets inputs saved.");
    return true;
  }catch(err){
    console.error("Advanced Markets save failed", err);
    if (activeAdvancedMarketsClient?.clientProfileId === requestedClientProfileId){
      advancedMarketsInputsDirty = true;
      if (advancedMarketsSection?.open && advancedMarketsInputsStatus){
        advancedMarketsInputsStatus.textContent = /Failed to fetch/i.test(err?.message || "")
          ? "Saved locally — server unreachable"
          : (auto ? "Autosave failed" : "Save failed");
      }
    }
    console.error(err);
    toast(
      /Failed to fetch/i.test(err?.message || "")
        ? "Could not reach the server right now. Your Advanced Markets entries are still saved locally in this browser."
        : (err?.message || (auto ? "Failed to auto-save Advanced Markets inputs." : "Failed to save Advanced Markets inputs.")),
      { persistent: true, error: true }
    );
    return false;
  }finally{
    advancedMarketsAutosaveInFlight = false;
    if (advancedMarketsSection?.open && btnSaveAdvancedMarketsInputs){
      btnSaveAdvancedMarketsInputs.disabled = false;
    }
    if (advancedMarketsAutosaveQueued){
      advancedMarketsAutosaveQueued = false;
      queueAdvancedMarketsAutosave({ immediate: true });
    }
  }
}

function queueAdvancedMarketsAutosave(options = {}){
  const { immediate = false, sessionId = advancedMarketsCurrentSession } = options;
  if (!advancedMarketsInputsDirty || !activeAdvancedMarketsClient?.clientProfileId) return;
  if (sessionId !== advancedMarketsCurrentSession) return;

  clearAdvancedMarketsAutosaveTimer();
  if (immediate){
    void persistAdvancedMarketsInputs({ auto: true, force: true, allowClosed: true, sessionId });
    return;
  }

  if (advancedMarketsSection?.open && advancedMarketsInputsStatus){
    advancedMarketsInputsStatus.textContent = "Changes pending…";
  }

  advancedMarketsAutosaveTimer = window.setTimeout(() => {
    advancedMarketsAutosaveTimer = 0;
    void persistAdvancedMarketsInputs({ auto: true, sessionId });
  }, ADVANCED_MARKETS_AUTOSAVE_DELAY_MS);
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
  NewLead: "Lead",
  Opportunities: "Opportunities",
  Contacted: "Contacted",
  Qualified: "Qualified",
  Client: "Clients",
  BusinessClient: "Business Clients",
  MeetingScheduled: "Meeting Scheduled",
  ProposalSent: "Proposal Sent",
  ApplicationStarted: "Application Started",
  Submitted: "Submitted",
  ClosedLost: "Not Moving Forward",
  Nurture: "Nurture"
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
  { key: "Client", label: "Clients", tone: "good", className: "stage-client", note: "Portal-enabled clients with active access to the shared client workspace." },
  { key: "BusinessClient", label: "Business Clients", tone: "good", className: "stage-businessclient", note: "Business clients with expanded finance workspace access." },
  { key: "Opportunities", label: "Opportunities", tone: "warn", className: "stage-opportunities", note: "Qualified opportunities that need pressure and movement before becoming full clients." },
  { key: "NewLead", label: "Lead", tone: "warn", className: "stage-newlead", note: "Fresh incoming records that need first contact and qualification." },
  { key: "Contacted", label: "Contacted", tone: "info", className: "stage-contacted", note: "The first touch happened. Keep momentum alive." },
  { key: "Qualified", label: "Qualified", tone: "good", className: "stage-qualified", note: "The fit is real and deeper planning should happen now." },
  { key: "MeetingScheduled", label: "Meeting Scheduled", tone: "info", className: "stage-meetingscheduled", note: "Appointments are queued and should sync to Outlook fast." },
  { key: "ProposalSent", label: "Proposal Sent", tone: "warn", className: "stage-proposalsent", note: "Recommendations are out and follow-up pressure is on." },
  { key: "ApplicationStarted", label: "Application Started", tone: "info", className: "stage-applicationstarted", note: "The application has started and needs tight execution." },
  { key: "Submitted", label: "Submitted", tone: "good", className: "stage-submitted", note: "Business is in motion and should not stall." },
  { key: "ClosedLost", label: "Not Moving Forward", tone: "bad", className: "stage-closedlost", note: "This opportunity is not advancing and needs cleanup or archive." },
  { key: "Nurture", label: "Nurture", tone: "warn", className: "stage-nurture", note: "Long-cycle relationships that still deserve disciplined follow-up." }
];

let pipelineOrder = loadJSON(LS_PIPELINE_ORDER, {});

function savePipelineOrder(db){
  pipelineOrder = db || {};
  saveJSON(LS_PIPELINE_ORDER, pipelineOrder);
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
const btnCallReview = $("#btnCallReview");

const statusFilter = $("#statusFilter");
const priorityFilter = $("#priorityFilter");
const stageFilter = $("#stageFilter");
const attentionFilter = $("#attentionFilter");
const sortBy = $("#sortBy");
const pageSize = $("#pageSize");
const viewMode = $("#viewMode");
const density = $("#density");

const btnCopyEmails = $("#btnCopyEmails");
const btnExportCsv = $("#btnExportCsv");
const btnClearSel = $("#btnClearSel");
const btnOpenFirst = $("#btnOpenFirst");
const btnBulkEdit = $("#btnBulkEdit");
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
const btnImportSubmit = $("#btnImportSubmit");
const btnImportLeads = $("#btnImportLeads");
const colsBody = $("#colsBody");

const btnCalendarAuth = $("#btnCalendarAuth");
const btnResourceCalendar = $("#btnResourceCalendar");
const btnFilterMeetings = $("#btnFilterMeetings");
const btnFilterOverdue = $("#btnFilterOverdue");
const bulkModal = $("#bulkModal");
const callTaskModal = $("#callTaskModal");
const callTaskBody = $("#callTaskBody");
const advancedMarketsSection = $("#advancedMarketsActionRow");
const advancedMarketsPanel = $("#advancedMarketsPanel");
const advancedMarketsInputsForm = $("#advancedMarketsInputsForm");
const advancedMarketsClientUserId = $("#advancedMarketsClientUserId");
const advancedMarketsActionRow = $("#advancedMarketsActionRow");
const btnAdvancedMarketsInputs = $("#btnAdvancedMarketsInputs");
const btnSaveAdvancedMarketsInputs = $("#btnSaveAdvancedMarketsInputs");
const advancedMarketsClientLabel = $("#advancedMarketsClientLabel");
const advancedMarketsInputsStatus = $("#advancedMarketsInputsStatus");
const advancedMarketsInputsSummary = $("#advancedMarketsInputsSummary");
const ADVANCED_MARKETS_AUTOSAVE_DELAY_MS = 1200;
const btnPipelineRefresh = $("#btnPipelineRefresh");
const btnPipelineAll = $("#btnPipelineAll");
const btnPipelineCallTask = $("#btnPipelineCallTask");
const btnPipelineNew = $("#btnPipelineNew");
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
const clientQuickActionsShortcut = $("#clientQuickActionsShortcut");
const clientFinPlanBtn = $("#btnClientFinPlan");
const finPlanModalId = "clientFinPlanModal";
let finPlanModal = null;
let finPlanActiveClientId = null;
let finPlanVersion = 0;
let finPlanAllocManual = false; // align with workstation auto-split behavior
const clientActionsHubModal = $("#clientActionsHubModal");
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
const MYDAY_SNAPSHOT_URL = "/Clients/MyDaySnapshot";
const MYDAY_SNAPSHOT_TTL_MS = 15 * 1000;
let myDaySnapshot = { counts: {}, idsByQueue: {}, loadedAt: 0, isLoading: false };
let pipelineNavSelectedStage = "";
let pipelineNavSearchTerm = "";
let draggingClientId = null;
let meetingSuggestAbort = null;
let calendarBusyAbort = null;
let meetingSuggestTimer = null;
let quickViewScrollY = 0;
let activeAdvancedMarketsClient = null;
let activeAdvancedMarketsLoadSeq = 0;
let advancedMarketsAutosaveTimer = 0;
let advancedMarketsAutosaveInFlight = false;
let advancedMarketsAutosaveQueued = false;
let advancedMarketsInputsDirty = false;
let advancedMarketsInputsHydrating = false;
let advancedMarketsLastSavedFingerprint = "";
let advancedMarketsEditVersion = 0;
let lastAdvancedMarketsClientId = null;
let advancedMarketsModalSessionCounter = 0;
let advancedMarketsCurrentSession = 0;
let quickViewOpenedFromUrl = false;
let clientActionsLoadPromise = null;

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
  return rows.filter(r => norm(r.dataset.crmPipeline) === stageKey).length;
}

function applyStagePickerTone(el, className){
  if (!el) return;
  el.classList.remove(...STAGE_PICKER_TONES);
  if (className) el.classList.add(className);
}

function syncStagePickerUi(stageOverride = ""){
  if (!stagePickerSelect) return;

  const fallback = pipelineStages[0]?.key || "Client";
  const selectedRaw = stageOverride || stagePickerSelect.value || fallback;
  const selected = pipelineMeta(selectedRaw).key;
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
const dEmailInput = $("#dEmailInput");
const dPhoneInput = $("#dPhoneInput");
const dPhone2Input = $("#dPhone2Input");
const dAge = $("#dAge");
const dDob = $("#dDob");
const dGender = $("#dGender");
const dAddress = $("#dAddress");
const dCity = $("#dCity");
const dState = $("#dState");
const dCounty = $("#dCounty");
const dZip = $("#dZip");
const dBtc = $("#dBtc");
const dLender = $("#dLender");
const dLoanAmount = $("#dLoanAmount");

const dStatus = $("#dStatus");
const dPipelineStage = $("#dPipelineStage");
const dLastTouch = $("#dLastTouch");
const dTags = $("#dTags");
const dNotes = $("#dNotes");

const dNextDate = $("#dNextDate");
const dNextText = $("#dNextText");
const dPriority = $("#dPriority");
const dMeetingNextDate = $("#dMeetingNextDate") || { value: "", addEventListener(){} };
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
const btnResendClientInvite = $("#btnResendClientInvite");
const dResendInviteStatus = $("#dResendInviteStatus");
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
const dPlanLifeInsurance = $("#dPlanLifeInsurance");
const dPlanDisabilityIncome = $("#dPlanDisabilityIncome");
const dPlanLongTermCare = $("#dPlanLongTermCare");
const dPlanCriticalIllness = $("#dPlanCriticalIllness");
const dPlanTerminalIllness = $("#dPlanTerminalIllness");
const dPlanAnnuityRetirement = $("#dPlanAnnuityRetirement");
const dPlanMortgageProtection = $("#dPlanMortgageProtection");
const dPlanFinalExpense = $("#dPlanFinalExpense");
const dPlanMedicare = $("#dPlanMedicare");
const dPlanHealth = $("#dPlanHealth");
const dPlanDentalVision = $("#dPlanDentalVision");
const dPlanHospitalIndemnity = $("#dPlanHospitalIndemnity");
const dPlanPersonalAuto = $("#dPlanPersonalAuto");
const dPlanHomeRenters = $("#dPlanHomeRenters");
const dPlanUmbrella = $("#dPlanUmbrella");
const dPlanFloodEarthquake = $("#dPlanFloodEarthquake");
const dPlanCommercialAuto = $("#dPlanCommercialAuto");
const dPlanGeneralLiability = $("#dPlanGeneralLiability");
const dPlanBusinessOwnersPolicy = $("#dPlanBusinessOwnersPolicy");
const dPlanWorkersComp = $("#dPlanWorkersComp");
const dPlanKeyPersonBuySell = $("#dPlanKeyPersonBuySell");
const dPlanGroupBenefits = $("#dPlanGroupBenefits");
const dAssignedOwner = $("#dAssignedOwner");
const dWatchers = $("#dWatchers");
const dMentionNote = $("#dMentionNote");
const mentionList = $("#mentionList");
const dShareAgentSearch = $("#dShareAgentSearch");
const dShareSelectedAgent = $("#dShareSelectedAgent");
const dShareAgentResults = $("#dShareAgentResults");
const btnShareAgentAccess = $("#btnShareAgentAccess");
const dShareAgentStatus = $("#dShareAgentStatus");
const dSharedAgentList = $("#dSharedAgentList");

let shareLookupTimer = null;
let selectedShareAgent = null;

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

function buildClientQuickViewOverrides(){
  return {
    crmStatus: norm(dStatus.value) || "Active",
    crmPriority: norm(dPriority.value) || "Normal",
    crmLastTouch: norm(dLastTouch.value) || null,
    crmNextDate: norm(dNextDate.value) || null,
    crmNextText: norm(dNextText.value),
    crmTags: norm(dTags.value),
    agentNotes: norm(dNotes.value),
    pipelineStage: norm(dPipelineStage.value) || "NewLead",
    waitingOn: norm(dWaitingOn.value) || "WaitingOnAgent",
    pinnedBrief: norm(dPinnedBrief.value),
    docIdReceived: !!dDocIdReceived.checked,
    docAppSent: !!dDocAppSent.checked,
    docAppSigned: !!dDocAppSigned.checked,
    docPolicyDelivered: !!dDocPolicyDelivered.checked,
    docReviewBooked: !!dDocReviewBooked.checked,
    watchers: norm(dWatchers.value),
    mentionNote: norm(dMentionNote.value),
    opportunityPlanning: buildOpportunityPlanningPayload()
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
    await saveQuickViewForRow(row, buildClientQuickViewOverrides(), "Saved ✔");
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
    dDob,dAge,dGender,dBtc,dLender,dLoanAmount,dStatus,dPriority,dLastTouch,dNextDate,dNextText,dTags,dNotes,
    dPipelineStage,
    dWaitingOn,dPinnedBrief,dDocIdReceived,dDocAppSent,dDocAppSigned,dDocPolicyDelivered,dDocReviewBooked,
    dAssignedOwner,dWatchers,dMentionNote
  ];
  autosaveFields.forEach(el => {
    if (!el) return;
    if (el.type === "checkbox"){
      el.addEventListener("change", () => queueQuickViewAutosave());
    }else{
      el.addEventListener("input", () => queueQuickViewAutosave());
      el.addEventListener("change", () => queueQuickViewAutosave());
    }
  });
  opportunityPlanningInputs.forEach(([, input]) => {
    if (!input) return;
    input.addEventListener("change", () => queueQuickViewAutosave());
  });

  // Contact fields (email, phone, address) save on blur for clean editing without lag
  const contactFields = [dEmailInput, dPhoneInput, dPhone2Input, dAddress, dCity, dState, dCounty, dZip];
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

const opportunityPlanningInputs = [
  ["lifeInsurance", dPlanLifeInsurance],
  ["disabilityIncome", dPlanDisabilityIncome],
  ["longTermCare", dPlanLongTermCare],
  ["criticalIllness", dPlanCriticalIllness],
  ["terminalIllness", dPlanTerminalIllness],
  ["annuityRetirement", dPlanAnnuityRetirement],
  ["mortgageProtection", dPlanMortgageProtection],
  ["finalExpense", dPlanFinalExpense],
  ["medicare", dPlanMedicare],
  ["health", dPlanHealth],
  ["dentalVision", dPlanDentalVision],
  ["hospitalIndemnity", dPlanHospitalIndemnity],
  ["personalAuto", dPlanPersonalAuto],
  ["homeRenters", dPlanHomeRenters],
  ["umbrellaLiability", dPlanUmbrella],
  ["floodEarthquake", dPlanFloodEarthquake],
  ["commercialAuto", dPlanCommercialAuto],
  ["generalLiability", dPlanGeneralLiability],
  ["businessOwnersPolicy", dPlanBusinessOwnersPolicy],
  ["workersComp", dPlanWorkersComp],
  ["keyPersonBuySell", dPlanKeyPersonBuySell],
  ["groupBenefits", dPlanGroupBenefits]
];

function applyOpportunityPlanningValues(values){
  opportunityPlanningInputs.forEach(([key, input]) => {
    if (!input) return;
    input.checked = !!(values && values[key]);
  });
}

function buildOpportunityPlanningPayload(values){
  const payload = {};
  opportunityPlanningInputs.forEach(([key, input]) => {
    if (values && Object.prototype.hasOwnProperty.call(values, key)){
      payload[key] = !!values[key];
      return;
    }
    payload[key] = !!(input && input.checked);
  });
  return payload;
}

const btnSaveLocal = $("#btnSaveLocal");
const btnResetLocal = $("#btnResetLocal");
const btnMarkToday = $("#btnMarkToday");
const btnSetNextToday = $("#btnSetNextToday");
const btnCopyContact = $("#btnCopyContact");
const btnMail = $("#btnMail");
const btnCall = $("#btnCall");
const btnOpenEdit = $("#btnOpenEdit");
const btnOpenProfile = $("#btnOpenProfile");
const btnEditProfile = $("#btnEditProfile");
const btnDeleteClient = $("#btnDeleteClient");

const dActType = $("#dActType");
const dActDate = $("#dActDate");
const dActNote = $("#dActNote");
const btnAddActivity = $("#btnAddActivity");
const btnClearTimeline = $("#btnClearTimeline");
const timeline = $("#timeline");
const timelineFilters = $("#timelineFilters");

const cmdInput = $("#cmdInput");
let activeTimelineFilter = "all";

function setDrawerNextActionDate(value){
  const safeValue = value || "";
  if (dNextDate) dNextDate.value = safeValue;
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
  const pipeline  = row.dataset.sPipeline  || "NewLead";
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
  const prodStatus = row.dataset.prodStatus || row.dataset.sProdstatus || "";
  const prodAmount = row.dataset.prodAmount || row.dataset.sProdamount || 0;

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

  setClientProduction(row, prodStatus, prodAmount);
}

function setClientProduction(row, status, amount, totals){
  const badge = $("[data-prod-card]", row);
  const cleanStatus = (status || "").trim();
  const amt = Number(amount || 0);
  const paid = Number(totals?.paid ?? row.dataset.prodPaid ?? row.dataset.paid ?? 0);
  const issued = Number(totals?.issued ?? row.dataset.prodIssued ?? 0);
  const submitted = Number(totals?.submitted ?? row.dataset.prodSubmitted ?? 0);

  row.dataset.prodPaid = Number.isFinite(paid) ? `${paid}` : "0";
  row.dataset.prodIssued = Number.isFinite(issued) ? `${issued}` : "0";
  row.dataset.prodSubmitted = Number.isFinite(submitted) ? `${submitted}` : "0";
  row.dataset.paid = row.dataset.prodPaid;
  row.dataset.prodStatus = (paid > 0 ? "Paid" : cleanStatus);
  row.dataset.prodAmount = paid > 0 ? paid : amt;

  if (!badge) return;
  if (paid > 0){
    badge.innerHTML = `<span class="prod-status">Paid</span><span class="prod-amt"> ${formatCurrency(paid)}</span>`;
    badge.classList.remove("hidden");
  } else {
    badge.textContent = "";
    badge.classList.add("hidden");
  }
}

function setClientProductionById(clientId, status, amount, totals){
  const row = rows.find(r => r.dataset.clientId === clientId);
  if (row) setClientProduction(row, status, amount, totals);
  updatePipelineCardProduction(clientId);
}

function renderPipelineProdBadge({ paid = 0, issued = 0, submitted = 0 } = {}){
  const paidAmt = Number(paid || 0);
  const issuedAmt = Number(issued || 0);
  const submittedAmt = Number(submitted || 0);
  if (paidAmt <= 0 && issuedAmt <= 0 && submittedAmt <= 0) return "";

  return `
    <div class="prod-line"><span class="prod-lbl">Paid</span><span class="prod-val">${formatCurrency(paidAmt)}</span></div>
    <div class="prod-line"><span class="prod-lbl">Issued</span><span class="prod-val">${formatCurrency(issuedAmt)}</span></div>
    <div class="prod-line"><span class="prod-lbl">Submitted</span><span class="prod-val">${formatCurrency(submittedAmt)}</span></div>
  `;
}

function updatePipelineCardProduction(clientId){
  if (!clientId || !pipelineBoard) return;
  const card = pipelineBoard.querySelector(`[data-cardid="${CSS.escape(clientId)}"]`);
  const row = rows.find(r => r.dataset.clientId === clientId);
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

// ===== Production tiles refresh (Clients) =====
const tileClientSubmittedTotal = $("#tileClientSubmittedTotal");
const tileClientIssuedTotal = $("#tileClientIssuedTotal");
const tileClientPaidTotal = $("#tileClientPaidTotal");
const tileClientPersonalTotal = $("#tileClientPersonalTotal");
const tileClientSubmittedCount = $("#tileClientSubmittedCount");
const tileClientIssuedCount = $("#tileClientIssuedCount");
const tileClientPaidCount = $("#tileClientPaidCount");
const tileClientPersonalCount = $("#tileClientPersonalCount");

async function refreshClientProductionTiles(){
  try{
    const res = await fetch("/production/summary/clients", { credentials: "include" });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const data = await res.json();
    const fmt = (v) => Number(v || 0).toLocaleString("en-US", { style:"currency", currency:"USD", maximumFractionDigits:0 });
    if (tileClientSubmittedTotal) tileClientSubmittedTotal.textContent = fmt(data.submitted);
    if (tileClientIssuedTotal) tileClientIssuedTotal.textContent = fmt(data.issued);
    if (tileClientPaidTotal) tileClientPaidTotal.textContent = fmt(data.paid);
    if (tileClientPersonalTotal) tileClientPersonalTotal.textContent = fmt(data.personal);
    if (tileClientSubmittedCount) tileClientSubmittedCount.textContent = data.countSubmitted ?? 0;
    if (tileClientIssuedCount) tileClientIssuedCount.textContent = data.countIssued ?? 0;
    if (tileClientPaidCount) tileClientPaidCount.textContent = data.countPaid ?? 0;
    if (tileClientPersonalCount) tileClientPersonalCount.textContent = data.countPersonal ?? 0;
  }catch(err){
    console.warn("Client production tile refresh failed", err);
  }
}

function updateCallMetrics(){
  if (!cmToday || !cmWeek || !cmMonth) return;
  let day = 0, week = 0, month = 0;
  rows.forEach(r => {
    day   += parseInt(r.dataset.crmAttemptsToday || r.dataset.sAttemptstoday || "0", 10) || 0;
    week  += parseInt(r.dataset.crmAttemptsWeek  || r.dataset.sAttemptsweek  || "0", 10) || 0;
    month += parseInt(r.dataset.crmAttemptsMonth || r.dataset.sAttemptsmonth || "0", 10) || 0;
  });
  cmToday.textContent = day.toLocaleString();
  cmWeek.textContent = week.toLocaleString();
  cmMonth.textContent = month.toLocaleString();
}
rows.forEach(hydrateRow);
updateCallMetrics();

btnCallReview?.addEventListener("click", () => {
  toast(`Calls — Today: ${cmToday?.textContent || 0} • Week: ${cmWeek?.textContent || 0} • Month: ${cmMonth?.textContent || 0}`, { persistent: true });
});

/* ========= Selection ========= */
function getVisibleRows(){ return rows.filter(r => r.style.display !== "none"); }
function getCheckedRows(){ return rows.filter(r => $(".row-chk", r)?.checked); }

function updateSelectionUI(){
  const checked = getCheckedRows();
  const count = checked.length;

  selCount.textContent = String(count);
  btnCopyEmails.disabled = count === 0;
  btnClearSel.disabled = count === 0;
  btnOpenFirst.disabled = count === 0;
  if (btnBulkEdit) btnBulkEdit.disabled = count === 0;

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

/* ========= Delegated Clicks (fast, minimal listeners) ========= */
document.addEventListener("click", (e) => {
  if (!e.target.closest(".actions")) closeAllMenus(null);

  const kebab = e.target.closest("[data-kebab]");
  if (kebab){
    e.preventDefault();
    return toggleMenu(kebab);
  }

  if (e.target.matches("[data-closemodal]")){
    e.preventDefault();
    return closeModal();
  }

  if (e.target === drawerBackdrop) closeDrawer();
  if (e.target === modalBackdrop) closeModal();

  const openDrawerEl = e.target.closest(".open-drawer");
  if (openDrawerEl){
    const row = openDrawerEl.closest(".client-row");
    if (row) openDrawerForRow(row);
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
      window.location.href = `/Clients/Queue?queue=${encodeURIComponent(queue)}`;
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
const CLIENTS_MEETING_BUCKET_KEYS = new Set(["meetingscheduled"]);

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
  const attn = norm(attentionFilter.value);

  let filtered = rows.slice();

  if (s) filtered = filtered.filter(r => norm(r.dataset.crmStatus) === s);
  if (priority) filtered = filtered.filter(r => norm(r.dataset.crmPriority) === priority);
  if (stage) filtered = filtered.filter(r => norm(r.dataset.crmPipeline) === stage);

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
  if (attn === "meeting") filtered = filtered.filter(r => CLIENTS_MEETING_BUCKET_KEYS.has(pipelineKey(r)));
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
  if (attn === "appsinflight") filtered = filtered.filter(r => ["ApplicationStarted", "Submitted"].includes(norm(r.dataset.crmPipeline)));

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
  const filtered = computeFiltered();
  renderList(filtered);
  renderCards(filtered);
  syncStagePickerUi();
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
  if (liveSync) liveSync.sendPage("clients", currentPage);
  renderAll();
});
btnNext?.addEventListener("click", () => {
  currentPage = currentPage + 1;
  if (liveSync) liveSync.sendPage("clients", currentPage);
  renderAll();
});

[statusFilter, priorityFilter, stageFilter, attentionFilter, sortBy, pageSize].forEach(el => {
  el?.addEventListener("change", () => { currentPage = 1; renderAll(); });
});

stagePickerSelect?.addEventListener("change", () => {
  syncStagePickerUi(stagePickerSelect.value || "Client");
});

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
    stageFilter.value = "NewLead";
    attentionFilter.value = "needs";
    sortBy.value = "nextaction_asc";
  } else if (name === "followup"){
    attentionFilter.value = "overdue";
    sortBy.value = "nextaction_asc";
  } else if (name === "meetingstoday"){
    stageFilter.value = "MeetingScheduled";
    attentionFilter.value = "today";
    sortBy.value = "nextaction_asc";
    pipelineFocusStage = "MeetingScheduled";
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

function queueRows(type){
  const sourceRows = uniqueRows(rows);
  const ids = myDaySnapshot.idsByQueue?.[type];
  if (ids instanceof Set){
    return sourceRows.filter(r => ids.has(norm(r.dataset.clientId)));
  }

  return queueRowsLocal(type);
}

function queueRowsLocal(type){
  const sourceRows = uniqueRows(rows);

  const hasScheduledNextDate = (row) => !!norm(row.dataset.crmNextDate);
  const isCallsNowRow = (row) => HIGH_PRIORITY_KEYS.has(priorityKey(row))
    && hasScheduledNextDate(row)
    && (isToday(norm(row.dataset.crmNextDate)) || isOverdue(norm(row.dataset.crmNextDate)));

  if (type === "callsnow") return sourceRows.filter(isCallsNowRow);
  if (type === "today") return sourceRows.filter(r => hasScheduledNextDate(r) && isToday(norm(r.dataset.crmNextDate)) && !isCallsNowRow(r));
  if (type === "overdue") return sourceRows.filter(r => hasScheduledNextDate(r) && isOverdue(norm(r.dataset.crmNextDate)) && !isCallsNowRow(r));
  if (type === "meetings") return sourceRows.filter(r => CLIENTS_MEETING_BUCKET_KEYS.has(pipelineKey(r)));
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
    const res = await fetch(MYDAY_SNAPSHOT_URL, { credentials: "include" });
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

function openQuickViewFromUrl(){
  if (quickViewOpenedFromUrl) return;
  try{
    const params = new URLSearchParams(window.location.search || "");
    const targetId = norm(params.get("clientUserId") || params.get("id") || params.get("open"));
    if (!targetId) return;

    const row = rows.find(r => norm(r.dataset.clientId) === targetId);
    if (!row) return;

    quickViewOpenedFromUrl = true;
    openDrawerForRow(row);
  }catch(err){
    console.error("Auto-open Quick View failed", err);
  }
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
async function openDrawerForRow(row){
  activeClientId = row.dataset.clientId;
  if (drawer) drawer.dataset.clientId = activeClientId || "";
  activeClientDetail = null;
  activeAdvancedMarketsClient = null;
  if (advancedMarketsSection){
    advancedMarketsSection.open = false;
  }
  advancedMarketsCurrentSession = ++advancedMarketsModalSessionCounter;

  const name = fullName(row);
  const email = norm(row.dataset.email);
  const phone = norm(row.dataset.phone);
  const phone2 = norm(row.dataset.phone2);
  dName.textContent = name || "Client";
  syncDrawerEmailDisplay(email);
  dPhone.textContent = phone || "No phone";
  if (dEmailInput) dEmailInput.value = email;
  if (dPhoneInput) dPhoneInput.value = phone;
  if (dPhone2Input) dPhone2Input.value = phone2 || "";
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
  const ts = Date.now();
  if (btnOpenEdit){
    btnOpenEdit.href = row.dataset.clientId ? `/Clients/Edit?clientUserId=${encodeURIComponent(row.dataset.clientId)}&_=${ts}` : "#";
    btnOpenEdit.textContent = "Edit Client";
  }
  if (btnOpenProfile){
    if (row.dataset.isguid === "true" && row.dataset.clientId){
      btnOpenProfile.href = `/ClientWorkspace/Profile?clientUserId=${encodeURIComponent(row.dataset.clientId)}&_=${ts}`;
    }else{
      btnOpenProfile.href = row.dataset.clientId ? `/Clients/Edit?clientUserId=${encodeURIComponent(row.dataset.clientId)}&_=${ts}` : "#";
    }
    btnOpenProfile.textContent = "Open Client Profile";
  }
  dStatus.value = row.dataset.crmStatus || "Active";
  dPipelineStage.value = row.dataset.crmPipeline || "NewLead";
  dLastTouch.value = row.dataset.crmLastTouch || "";
  dTags.value = row.dataset.crmTags || "";
  dNotes.value = row.dataset.crmNotes || "";
  setDrawerNextActionDate(row.dataset.crmNextDate || "");
  dNextText.value = row.dataset.crmNextText || "";
  dPriority.value = row.dataset.crmPriority || "Normal";
  dWaitingOn.value = row.dataset.crmWaitingOn || "WaitingOnAgent";
  dPinnedBrief.value = row.dataset.crmPinnedBrief || "";
  dDocIdReceived.checked = false;
  dDocAppSent.checked = false;
  dDocAppSigned.checked = false;
  dDocPolicyDelivered.checked = false;
  dDocReviewBooked.checked = false;
  applyOpportunityPlanningValues(null);
  dAssignedOwner.value = row.dataset.crmOwner || "";
  dWatchers.value = row.dataset.crmWatchers || "";
  dMentionNote.value = "";

  // Load Actions tab (Execution MVP - Clients)
  void loadClientActionsPanel();

  void loadClientCommitmentsPanel();
  dStageAge.textContent = `Stage Age: ${stageAgeDays(row)}d`;

  loadClientProductionHistory(row.dataset.clientId, name || "Client");
  dAttempts.textContent = `Attempts: ${row.dataset.crmAttemptsToday || 0} today • ${row.dataset.crmAttemptsWeek || 0} week • ${row.dataset.crmAttemptsLife || 0} total`;
  dWaitingOnPill.textContent = waitingLabel(row.dataset.crmWaitingOn || "WaitingOnAgent");
  dOutcomeSuggestion.textContent = "Use one-click outcomes to log activity, move the record forward, and queue the next move.";
  setAdvancedMarketsActionState(row.dataset.sRecordtype || "", row.dataset.advancedMarketsEligible);

  renderPortalActions(row, null);
  renderProtectionSnapshotSummary(null, { loading: true });

  dActDate.value = todayISO();
  dActNote.value = "";
  renderTimeline([]);
  renderMentionNotes([]);
  dSaved.textContent = "Loading…";

  drawer.classList.add("open");
  drawerBackdrop.classList.add("open");
  drawer.setAttribute("aria-hidden", "false");
  lockPageScrollForQuickView();
  closeAllMenus(null);

  try{
    const detail = await loadQuickView(activeClientId);
    if (activeClientId !== row.dataset.clientId) return;

    activeClientDetail = detail;
    dStatus.value = detail.crmStatus || row.dataset.crmStatus || "Active";
    dPipelineStage.value = detail.pipelineStage || row.dataset.crmPipeline || "NewLead";
    dLastTouch.value = detail.crmLastTouch || row.dataset.crmLastTouch || "";
    dTags.value = detail.crmTags || row.dataset.crmTags || "";
    dNotes.value = detail.agentNotes || row.dataset.crmNotes || "";
    setDrawerNextActionDate(detail.crmNextDate || row.dataset.crmNextDate || "");
    dNextText.value = detail.crmNextText || row.dataset.crmNextText || "";
    dPriority.value = detail.crmPriority || row.dataset.crmPriority || "Normal";
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
    dWaitingOn.value = detail.waitingOn || row.dataset.crmWaitingOn || "WaitingOnAgent";
    dPinnedBrief.value = detail.pinnedBrief || row.dataset.crmPinnedBrief || "";
    dAssignedOwner.value = detail.collaboration?.owner || row.dataset.crmOwner || "";
    dWatchers.value = (detail.collaboration?.watchers || []).join(", ");
    dDocIdReceived.checked = !!detail.docChecklist?.idReceived;
    dDocAppSent.checked = !!detail.docChecklist?.appSent;
    dDocAppSigned.checked = !!detail.docChecklist?.appSigned;
    dDocPolicyDelivered.checked = !!detail.docChecklist?.policyDelivered;
    dDocReviewBooked.checked = !!detail.docChecklist?.reviewBooked;
    applyOpportunityPlanningValues(detail.opportunityPlanning);
    dStageAge.textContent = `Stage Age: ${detail.stageAgeDays || stageAgeDays(row)}d`;
    dAttempts.textContent = `Attempts: ${detail.attemptsToday || 0} today • ${detail.attemptsThisWeek || 0} week • ${detail.attemptsLifetime || 0} total`;
    dWaitingOnPill.textContent = detail.waitingOnLabel || waitingLabel(detail.waitingOn || row.dataset.crmWaitingOn || "WaitingOnAgent");
    renderTimeline(detail.activities || []);
    renderMentionNotes(detail.collaboration?.mentionNotes || []);
    await loadSharedAgentAccess(activeClientId);
    setAdvancedMarketsActionState(
      detail.recordType || row.dataset.sRecordtype || "",
      detail.advancedMarketsEligible ?? row.dataset.advancedMarketsEligible
    );

    renderPortalActions(row, detail);
    void hydrateProtectionSnapshot(row, detail);

    dSaved.textContent = "Loaded";
  }catch(err){
    console.error("Quick View load failed", err);
    const status = err?.status || "";
    const statusText = err?.statusText || "";
    const msg = status
      ? `Failed to load client details (${status} ${statusText || ""}).`
      : "Failed to load client details.";
    dSaved.textContent = msg;
    toast(msg, { persistent: true, error: true });
    void hydrateProtectionSnapshot(row, null);
  }
}

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

function clearShareSelection(){
  selectedShareAgent = null;
  if (dShareSelectedAgent){
    dShareSelectedAgent.textContent = "No agent selected.";
  }
  if (btnShareAgentAccess){
    btnShareAgentAccess.disabled = true;
  }
}

function renderShareLookupResults(items){
  if (!dShareAgentResults) return;
  dShareAgentResults.innerHTML = "";

  const rows = Array.isArray(items) ? items : [];
  if (!rows.length){
    dShareAgentResults.innerHTML = `<div class="tiny">No tenant agents matched that search.</div>`;
    return;
  }

  const frag = document.createDocumentFragment();
  rows.forEach(item => {
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "btn btn-ghost";
    btn.style.width = "100%";
    btn.style.textAlign = "left";
    btn.style.marginTop = "6px";

    const name = norm(item.fullName) || norm(item.agentUpn) || "Agent";
    const email = norm(item.agentUpn) || "No email";
    const phone = norm(item.phone) || "No phone";
    const already = item.isShared ? " • Shared" : "";
    btn.textContent = `${name} — ${email}${phone ? ` — ${phone}` : ""}${already}`;

    btn.addEventListener("click", () => {
      selectedShareAgent = {
        agentUserId: norm(item.agentUserId),
        agentUpn: norm(item.agentUpn),
        fullName: norm(item.fullName),
        phone: norm(item.phone)
      };

      if (dShareSelectedAgent){
        dShareSelectedAgent.textContent = `${name} (${email})${phone ? ` • ${phone}` : ""}`;
      }
      if (btnShareAgentAccess){
        btnShareAgentAccess.disabled = !selectedShareAgent.agentUserId;
      }
      if (dShareAgentStatus){
        dShareAgentStatus.textContent = item.isShared
          ? "This agent already has access."
          : "Ready to grant this agent access to the current client.";
      }
    });

    frag.appendChild(btn);
  });

  dShareAgentResults.appendChild(frag);
}

function renderSharedAgentList(items){
  if (!dSharedAgentList) return;
  dSharedAgentList.innerHTML = "";

  const rows = Array.isArray(items) ? items : [];
  if (!rows.length){
    dSharedAgentList.innerHTML = `<div class="tiny">No shared agents yet. Access is currently restricted to the original owner only.</div>`;
    return;
  }

  const frag = document.createDocumentFragment();
  rows.forEach(item => {
    const card = document.createElement("div");
    card.className = "event";

    const top = document.createElement("div");
    top.className = "top";

    const type = document.createElement("div");
    type.className = "type";
    const name = norm(item.fullName) || norm(item.agentUpn) || "Agent";
    type.textContent = `${name}${item.isOwner ? " (Owner)" : ""}`;

    const meta = document.createElement("div");
    meta.className = "meta";
    meta.textContent = norm(item.agentUpn) || "";

    top.appendChild(type);
    top.appendChild(meta);

    const note = document.createElement("div");
    note.className = "note";
    note.textContent = norm(item.phone) || "No phone on file";

    card.appendChild(top);
    card.appendChild(note);

    if (!item.isOwner && norm(item.agentUserId)){
      const actions = document.createElement("div");
      actions.style.marginTop = "8px";

      const revokeBtn = document.createElement("button");
      revokeBtn.type = "button";
      revokeBtn.className = "btn btn-ghost";
      revokeBtn.textContent = "Revoke Access";
      revokeBtn.addEventListener("click", () => {
        void revokeSharedAgentAccess(item.agentUserId);
      });

      actions.appendChild(revokeBtn);
      card.appendChild(actions);
    }

    frag.appendChild(card);
  });

  dSharedAgentList.appendChild(frag);
}

async function loadSharedAgentAccess(clientId){
  if (!clientId || !dSharedAgentList) return;
  try{
    const res = await fetch(`/Clients/ClientAccessCollaborators?clientUserId=${encodeURIComponent(clientId)}`, {
      credentials: "include"
    });
    if (!res.ok){
      throw new Error(`Shared access load failed (${res.status})`);
    }
    const data = await res.json();
    renderSharedAgentList(data);
  }catch(err){
    console.error("Shared access load failed", err);
    dSharedAgentList.innerHTML = `<div class="tiny">Unable to load shared access right now.</div>`;
  }
}

async function searchShareAgents(query){
  const q = norm(query);
  if (!activeClientId || !dShareAgentResults) return;

  if (!q || q.length < 2){
    dShareAgentResults.innerHTML = `<div class="tiny">Type at least 2 characters to search tenant agents.</div>`;
    return;
  }

  try{
    const res = await fetch(`/Clients/CollaboratorLookup?clientUserId=${encodeURIComponent(activeClientId)}&q=${encodeURIComponent(q)}`, {
      credentials: "include"
    });
    if (!res.ok){
      throw new Error(`Lookup failed (${res.status})`);
    }
    const data = await res.json();
    renderShareLookupResults(data);
  }catch(err){
    console.error("Collaborator lookup failed", err);
    dShareAgentResults.innerHTML = `<div class="tiny">Unable to search agents right now.</div>`;
  }
}

async function grantSelectedAgentAccess(){
  if (!activeClientId || !selectedShareAgent?.agentUserId) return;

  try{
    const response = await postJson("/Clients/GrantClientAccess", {
      clientUserId: activeClientId,
      agentUserId: selectedShareAgent.agentUserId,
      agentUpn: selectedShareAgent.agentUpn,
      agentName: selectedShareAgent.fullName,
      agentPhone: selectedShareAgent.phone
    });

    renderSharedAgentList(response.sharedAgents || []);
    clearShareSelection();
    if (dShareAgentSearch) dShareAgentSearch.value = "";
    if (dShareAgentResults) dShareAgentResults.innerHTML = "";
    if (dShareAgentStatus) dShareAgentStatus.textContent = "Access granted.";
    toast("Client access granted.");
  }catch(err){
    console.error("GrantClientAccess failed", err);
    toast(err?.message || "Unable to grant access.", { error: true, persistent: true });
  }
}

async function resendClientInvite(){
  if (!activeClientId) return;
  const btn = btnResendClientInvite;
  const emailVal = (dEmailInput?.value || "").trim();
  if (!emailVal) {
    if (dResendInviteStatus) dResendInviteStatus.textContent = "Enter an email address first.";
    return;
  }

  // Determine if email changed from what's on record
  const currentEmail = (activeClientDetail?.email || "").trim().toLowerCase();
  const newEmail = emailVal.toLowerCase() === currentEmail ? null : emailVal;

  if (btn) btn.disabled = true;
  if (dResendInviteStatus) dResendInviteStatus.textContent = "Sending…";

  try {
    const response = await postJson("/Clients/ResendClientInvite", {
      clientUserId: activeClientId,
      newEmail: newEmail
    });

    if (dResendInviteStatus) dResendInviteStatus.textContent = `✔ Sent to ${response.sentTo}`;
    if (newEmail && activeClientDetail) activeClientDetail.email = emailVal;
    toast(`Access link resent to ${response.sentTo}`);
  } catch(err) {
    console.error("ResendClientInvite failed", err);
    if (dResendInviteStatus) dResendInviteStatus.textContent = `⚠ ${err?.message || "Send failed"}`;
    toast(err?.message || "Failed to resend invite.", { error: true, persistent: true });
  } finally {
    if (btn) btn.disabled = false;
  }
}

async function revokeSharedAgentAccess(agentUserId){
  if (!activeClientId || !agentUserId) return;
  if (!confirm("Revoke this agent's access to the current client?")) return;

  try{
    const response = await postJson("/Clients/RevokeClientAccess", {
      clientUserId: activeClientId,
      agentUserId
    });
    renderSharedAgentList(response.sharedAgents || []);
    if (dShareAgentStatus) dShareAgentStatus.textContent = "Access revoked.";
    toast("Client access revoked.");
  }catch(err){
    console.error("RevokeClientAccess failed", err);
    toast(err?.message || "Unable to revoke access.", { error: true, persistent: true });
  }
}

function closeDrawer(){
  // Always capture Advanced Markets before leaving the drawer to avoid clearing on reopen.
  const draftPayload = buildAdvancedMarketsSavePayload();
  if (draftPayload) {
    writeAdvancedMarketsDraft(draftPayload);
    void persistAdvancedMarketsInputs({ auto: true, force: true, allowClosed: true, sessionId: advancedMarketsCurrentSession });
  }
  if (advancedMarketsSection){
    advancedMarketsSection.open = false;
  }
  clearAdvancedMarketsAutosaveTimer();
  activeAdvancedMarketsLoadSeq += 1;
  advancedMarketsCurrentSession = ++advancedMarketsModalSessionCounter;
  activeClientId = null;
  if (drawer) drawer.dataset.clientId = "";
  clearShareSelection();
  if (dShareAgentSearch) dShareAgentSearch.value = "";
  if (dShareAgentResults) dShareAgentResults.innerHTML = "";
  if (dSharedAgentList) dSharedAgentList.innerHTML = "";
  if (dShareAgentStatus) dShareAgentStatus.textContent = "Client access remains blocked for non-permitted agents.";
  if (btnResendClientInvite) btnResendClientInvite.style.display = "none";
  if (dResendInviteStatus) dResendInviteStatus.textContent = "";
  clientActionsLoadPromise = null;
  if (clientActionsHubModal && window.bootstrap){
    const inst = bootstrap.Modal.getInstance(clientActionsHubModal);
    inst?.hide();
  }
  drawer.classList.remove("open");
  drawerBackdrop.classList.remove("open");
  drawer.setAttribute("aria-hidden", "true");
  closeNoteModal();
  unlockPageScrollForQuickView();
}
function openClientActionsHub(){
  const requestedClientId = (activeClientId || drawer?.dataset?.clientId || "").toString().trim();
  if (!requestedClientId){
    toast("Open a client first.");
    return;
  }
  bindQuickViewBootstrapModals();
  closeLegacyOverlayModals();
  reconcileBootstrapModalState();
  activeClientId = requestedClientId;
  if (drawer) drawer.dataset.clientId = requestedClientId;
  ensureModalInBody('clientActionsHubModal');
  const modalEl = document.getElementById('clientActionsHubModal');
  if (modalEl && window.bootstrap){
    bootstrap.Modal.getOrCreateInstance(modalEl).show();
  }
  void loadClientActionsPanel();
  void loadClientCommitmentsPanel();
}

btnCloseDrawer?.addEventListener("click", closeDrawer);
clientQuickActionsShortcut?.addEventListener("click", (event) => {
  event.preventDefault();
  openClientActionsHub();
});
clientFinPlanBtn?.addEventListener("click", async (e) => {
  e.preventDefault();
  const requestedClientId = (activeClientId || drawer?.dataset?.clientId || "").toString().trim();
  if (!requestedClientId){
    toast("Open a client first.");
    return;
  }
  finPlanActiveClientId = requestedClientId;
  await openFinPlanModal(requestedClientId);
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
  const leadId = (activeClientId || "").trim();
  const leadName = (dName?.textContent || "").trim() || "Lead";
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
    const res = await fetch(`/WorkstationNotes/Dates?leadId=${encodeURIComponent(leadId)}`, {
      credentials: "include"
    });
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
    const res = await fetch(`/WorkstationNotes/Entry?leadId=${encodeURIComponent(leadId)}&date=${encodeURIComponent(date)}`, {
      credentials: "include"
    });
    if (!res.ok) throw new Error("fail");
    const payload = await res.json();
    noteWentWell.value = normalizeNoteBodyForDate(payload?.wentWell || "", date);
    noteCouldBetter.value = normalizeNoteBodyForDate(payload?.couldBetter || "", date);
    if (noteDatesSelect) noteDatesSelect.value = noteEncodeKey(leadId, date);
    noteSetStatus(`Loaded ${(payload?.leadName || noteCurrentLeadContext().leadName)} — ${noteDisplayDate(date)}`);
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
    const res = await fetch("/WorkstationNotes/Entry", {
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
    });
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
    .replaceAll('"',"&quot;");
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
        row.dataset.sRecordtype = response.recordType || recordType;
        row.dataset.advancedMarketsEligible = ((response.advancedMarketsEligible ?? (recordType === "BusinessClient")) ? "true" : "false");
        row.dataset.sPipeline = response.pipelineStage || (recordType === "BusinessClient" ? "BusinessClient" : "Client");
        activeClientId = response.newClientUserId;
        activeClientDetail = {
          ...(activeClientDetail || {}),
          clientUserId: response.newClientUserId,
          recordType: response.recordType || recordType,
          advancedMarketsEligible: (response.advancedMarketsEligible ?? (recordType === "BusinessClient")),
          portalAccessEnabled: true,
          pipelineStage: response.pipelineStage || (recordType === "BusinessClient" ? "BusinessClient" : "Client")
        };
        hydrateRow(row);
        if (dPipelineStage) dPipelineStage.value = recordType === "BusinessClient" ? "BusinessClient" : "Client";
        if (dStatus) dStatus.value = "Active";
        setAdvancedMarketsActionState(row.dataset.sRecordtype || recordType, row.dataset.advancedMarketsEligible);
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
    if (btnResendClientInvite) btnResendClientInvite.style.display = "none";
    if (dResendInviteStatus) dResendInviteStatus.textContent = "";
    return;
  }

  if (!portal) {
    dPortalWrap.innerHTML = `<span class="btn btn-ghost" aria-disabled="true">Portal Not Available</span>`;
  } else {
    dPortalWrap.innerHTML = "";
  }

  if (detail?.lastCalendarEventWebLink){
    dPortalWrap.innerHTML += `${dPortalWrap.innerHTML ? " " : ""}<a class="btn btn-ghost" href="${detail.lastCalendarEventWebLink}" target="_blank" rel="noopener">Last Calendar Event</a>`;
  }

  // Show resend button beside primary email field
  if (btnResendClientInvite) btnResendClientInvite.style.display = "";
  if (dResendInviteStatus) dResendInviteStatus.textContent = "";
}

function formatAdvancedMarketsSavedAt(value){
  if (!value) return "";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "" : date.toLocaleString();
}

async function loadAdvancedMarketsSection(){
  if (!activeClientId || !advancedMarketsSection || !advancedMarketsInputsForm) return;

  advancedMarketsCurrentSession = ++advancedMarketsModalSessionCounter;
  const row = rows.find(r => r.dataset.clientId === activeClientId);
  const clientProfileId = row?.dataset.clientProfileId || "";
  const sameClient = lastAdvancedMarketsClientId === clientProfileId;
  const cached = getAdvancedMarketsCachedPayload(clientProfileId);
  const recordType = activeClientDetail?.recordType || row?.dataset.sRecordtype || "";
  const eligible = activeClientDetail?.advancedMarketsEligible ?? row?.dataset.advancedMarketsEligible;
  if (!isAdvancedMarketsEligible(recordType, eligible)){
    toast("Advanced Markets inputs are only available for business clients.");
    if (advancedMarketsSection.open) advancedMarketsSection.open = false;
    return;
  }

  const clientName = row ? (fullName(row) || "Business Client") : "Business Client";
  if (!clientProfileId){
    toast("Missing managed client profile id for Advanced Markets inputs.", { persistent: true, error: true });
    if (advancedMarketsSection.open) advancedMarketsSection.open = false;
    return;
  }
  activeAdvancedMarketsClient = {
    clientProfileId,
    clientUserId: activeClientId,
    clientName
  };

  if (advancedMarketsClientUserId) advancedMarketsClientUserId.value = activeClientId;
  if (advancedMarketsClientLabel) advancedMarketsClientLabel.textContent = clientName;
  if (advancedMarketsInputsSummary) {
    advancedMarketsInputsSummary.textContent = `${clientName} — save reusable business-planning inputs for the shared Advanced Markets illustration tool.`;
  }
  if (advancedMarketsInputsStatus) advancedMarketsInputsStatus.textContent = "Loading saved inputs…";
  if (btnSaveAdvancedMarketsInputs) btnSaveAdvancedMarketsInputs.disabled = true;
  clearAdvancedMarketsAutosaveTimer();
  advancedMarketsAutosaveQueued = false;
  if (!sameClient){
    advancedMarketsEditVersion = 0;
  }

  const draftBeforeLoad = readAdvancedMarketsDraft(clientProfileId, activeClientId);
  let prefillPayload = draftBeforeLoad?.inputs || cached?.inputs || null;
  if (!prefillPayload && row?.dataset.amInputs){
    try{ prefillPayload = JSON.parse(row.dataset.amInputs); }catch{ /* ignore */ }
    if (prefillPayload){
      cached && (cached.fingerprint = row.dataset.amFingerprint || cached.fingerprint);
    }
  }

  if (prefillPayload){
    const fp = fingerprintAdvancedMarketsInputs({ inputs: prefillPayload });
    advancedMarketsInputsHydrating = true;
    try{
      applyAdvancedMarketsInputsToForm(advancedMarketsInputsForm, prefillPayload);
    }finally{
      advancedMarketsInputsHydrating = false;
    }
    advancedMarketsLastSavedFingerprint = fp;
    advancedMarketsInputsDirty = !!draftBeforeLoad?.inputs && (!cached || fp !== cached.fingerprint);
    if (!draftBeforeLoad?.inputs) advancedMarketsInputsDirty = false;
    if (advancedMarketsInputsStatus && draftBeforeLoad?.updatedUtc){
      const draftAt = formatAdvancedMarketsSavedAt(draftBeforeLoad.updatedUtc);
      advancedMarketsInputsStatus.textContent = draftAt
        ? `Recovered unsaved local changes • ${draftAt}`
        : "Recovered unsaved local changes";
    }
  }else if (!sameClient){
    advancedMarketsInputsDirty = false;
    advancedMarketsLastSavedFingerprint = "";
    advancedMarketsInputsHydrating = true;
    try{
      applyAdvancedMarketsInputsToForm(advancedMarketsInputsForm, {});
    }finally{
      advancedMarketsInputsHydrating = false;
    }
  }

  if (prefillPayload && !draftBeforeLoad?.inputs && cached?.updatedUtc && advancedMarketsInputsStatus){
    const savedAt = formatAdvancedMarketsSavedAt(cached.updatedUtc);
    advancedMarketsInputsStatus.textContent = savedAt
      ? `Saved inputs loaded • Updated ${savedAt}`
      : "Saved inputs loaded";
  }
  if (prefillPayload && !draftBeforeLoad?.inputs && row?.dataset.amUpdatedUtc && advancedMarketsInputsStatus && !cached?.updatedUtc){
    const savedAt = formatAdvancedMarketsSavedAt(row.dataset.amUpdatedUtc);
    advancedMarketsInputsStatus.textContent = savedAt
      ? `Saved inputs loaded • Updated ${savedAt}`
      : "Saved inputs loaded";
  }

  const requestedClientId = activeClientId;
  const requestedClientProfileId = clientProfileId;
  const loadSeq = ++activeAdvancedMarketsLoadSeq;
  const loadEditVersion = advancedMarketsEditVersion;
  const loadSession = advancedMarketsCurrentSession;
  if (!advancedMarketsSection.open) advancedMarketsSection.open = true;

  try{
    const data = await loadAdvancedMarketsInputs(requestedClientId, requestedClientProfileId);
    if (loadSeq !== activeAdvancedMarketsLoadSeq) return;
    if (activeAdvancedMarketsClient?.clientProfileId !== requestedClientProfileId) return;
    if (!advancedMarketsSection.open) return;
    if (advancedMarketsEditVersion !== loadEditVersion) return;
    if (advancedMarketsCurrentSession !== loadSession) return;

    const incomingClientUserId = data.clientUserId || requestedClientId;
    const incomingClientProfileId = data.clientProfileId || requestedClientProfileId;
    activeAdvancedMarketsClient = {
      clientProfileId: incomingClientProfileId,
      clientUserId: incomingClientUserId,
      clientName: data.clientName || clientName
    };
    if (advancedMarketsClientUserId) advancedMarketsClientUserId.value = data.clientUserId || requestedClientId;
    if (advancedMarketsClientLabel) advancedMarketsClientLabel.textContent = data.clientName || clientName;
    if (advancedMarketsInputsSummary) {
      advancedMarketsInputsSummary.textContent = `${data.clientName || clientName} — save reusable business-planning inputs for the shared Advanced Markets illustration tool.`;
    }
    const incomingInputs = data.inputs || {};
    const incomingFingerprint = fingerprintAdvancedMarketsInputs({ inputs: incomingInputs });
    const hasSavedResponse = !!data.hasSavedInputs && !!incomingFingerprint && incomingFingerprint !== "{}";
    const incomingHasContent = incomingFingerprint && incomingFingerprint !== "{}";
    const cachedForClient = getAdvancedMarketsCachedPayload(incomingClientProfileId);
    const hasDraft = !!draftBeforeLoad?.inputs;
    // Only apply server payload if it has content AND we don't already have a draft for this client.
    const shouldApplyServer = (hasSavedResponse || incomingHasContent) && !hasDraft;
    console.info("Advanced Markets load", {
      clientUserId: incomingClientUserId,
      hasSavedResponse,
      incomingFingerprint,
      cachedFingerprint: cachedForClient?.fingerprint,
      hasDraft,
      shouldApplyServer
    });

    if (shouldApplyServer){
      advancedMarketsInputsHydrating = true;
      try{
        applyAdvancedMarketsInputsToForm(advancedMarketsInputsForm, incomingInputs);
      }finally{
        advancedMarketsInputsHydrating = false;
      }
      advancedMarketsLastSavedFingerprint = incomingFingerprint;
      advancedMarketsInputsDirty = false;
      cacheAdvancedMarketsInputs(incomingClientProfileId, incomingInputs, data.updatedUtc);
      if (row){
        row.dataset.amInputs = JSON.stringify(incomingInputs || {});
        row.dataset.amFingerprint = incomingFingerprint;
        row.dataset.amUpdatedUtc = data.updatedUtc || "";
      }
      if (advancedMarketsInputsStatus){
        const savedAt = formatAdvancedMarketsSavedAt(data.updatedUtc);
        advancedMarketsInputsStatus.textContent = data.hasSavedInputs
          ? (savedAt ? `Saved inputs loaded • Updated ${savedAt}` : "Saved inputs loaded")
          : "No saved inputs yet — using the client snapshot defaults where available.";
      }
    }else if (advancedMarketsInputsStatus && hasDraft){
      const draftAt = formatAdvancedMarketsSavedAt(draftBeforeLoad?.updatedUtc);
      advancedMarketsInputsStatus.textContent = draftAt
        ? `Recovered unsaved local changes • ${draftAt}`
        : "Recovered unsaved local changes";
    }
  }catch(err){
    if (loadSeq !== activeAdvancedMarketsLoadSeq) return;
    if (activeAdvancedMarketsClient?.clientProfileId !== requestedClientProfileId) return;
    if (!advancedMarketsSection.open) return;
    console.error(err);
    if (advancedMarketsInputsStatus) advancedMarketsInputsStatus.textContent = "Load failed";
    toast(err?.message || "Failed to load Advanced Markets inputs.", { persistent: true, error: true });
  }finally{
    if (loadSeq === activeAdvancedMarketsLoadSeq && activeAdvancedMarketsClient?.clientProfileId === requestedClientProfileId && advancedMarketsSection.open){
      if (btnSaveAdvancedMarketsInputs) btnSaveAdvancedMarketsInputs.disabled = false;
    }
  }
  lastAdvancedMarketsClientId = clientProfileId;
}

btnAdvancedMarketsInputs?.addEventListener("click", () => {
  if (!advancedMarketsSection) return;
  const willOpen = !advancedMarketsSection.open;
  advancedMarketsSection.open = willOpen;
  if (willOpen){
    advancedMarketsSection.scrollIntoView({ behavior: "smooth", block: "start" });
  }
});

advancedMarketsSection?.addEventListener("toggle", () => {
  if (advancedMarketsSection.open){
    void loadAdvancedMarketsSection();
  }else{
    if (advancedMarketsInputsDirty){
      const draftPayload = buildAdvancedMarketsSavePayload();
      if (draftPayload) writeAdvancedMarketsDraft(draftPayload);
      void persistAdvancedMarketsInputs({ auto: true, force: true, allowClosed: true, sessionId: advancedMarketsCurrentSession });
    }
    advancedMarketsCurrentSession = ++advancedMarketsModalSessionCounter;
    clearAdvancedMarketsAutosaveTimer();
    activeAdvancedMarketsLoadSeq += 1;
  }
});

advancedMarketsInputsForm?.addEventListener("input", (event) => {
  const source = event.target;
  if (!(source instanceof HTMLInputElement || source instanceof HTMLSelectElement || source instanceof HTMLTextAreaElement)) return;
  syncAdvancedMarketsMirrors(source);
  if (advancedMarketsInputsHydrating) return;
  advancedMarketsEditVersion += 1;
  advancedMarketsInputsDirty = true;
  const draftPayload = buildAdvancedMarketsSavePayload();
  if (draftPayload) writeAdvancedMarketsDraft(draftPayload);
  queueAdvancedMarketsAutosave();
});

advancedMarketsInputsForm?.addEventListener("change", (event) => {
  const source = event.target;
  if (!(source instanceof HTMLInputElement || source instanceof HTMLSelectElement || source instanceof HTMLTextAreaElement)) return;
  syncAdvancedMarketsMirrors(source);
  if (advancedMarketsInputsHydrating) return;
  advancedMarketsEditVersion += 1;
  advancedMarketsInputsDirty = true;
  const draftPayload = buildAdvancedMarketsSavePayload();
  if (draftPayload) writeAdvancedMarketsDraft(draftPayload);
  queueAdvancedMarketsAutosave();
});

btnSaveAdvancedMarketsInputs?.addEventListener("click", async () => {
  await persistAdvancedMarketsInputs({ force: true });
});

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
});


$$("[data-schedulepreset]").forEach(btn => {
  btn.addEventListener("click", () => {
    const preset = btn.getAttribute("data-schedulepreset");
    const now = new Date();
    if (preset === "today3"){
      setDrawerNextActionDate(todayISO());
      dNextText.value = dNextText.value || "Same-day follow-up";
    } else if (preset === "tomorrow10"){
      const d = new Date();
      d.setDate(d.getDate() + 1);
      setDrawerNextActionDate(d.toISOString().slice(0, 10));
      dNextText.value = dNextText.value || "Tomorrow morning follow-up";
    } else if (preset === "nextbiz"){
      let d = new Date();
      d.setDate(d.getDate() + 1);
      while ([0, 6].includes(d.getDay())) d.setDate(d.getDate() + 1);
      setDrawerNextActionDate(d.toISOString().slice(0, 10));
      dNextText.value = dNextText.value || "Next business day touch";
    } else if (preset === "week"){
      const d = new Date(now);
      d.setDate(d.getDate() + 7);
      setDrawerNextActionDate(d.toISOString().slice(0, 10));
      dNextText.value = dNextText.value || "1 week follow-up";
    }
    dSaved.textContent = "Next-step preset applied — saving…";
    queueQuickViewAutosave();
  });
});

btnCopyContact?.addEventListener("click", () => {
  if (!activeClientId) return;
  const row = rows.find(r => r.dataset.clientId === activeClientId);
  if (!row) return;
  copyText(`${fullName(row)}\n${norm(row.dataset.email)}\n${norm(row.dataset.phone)}`.trim());
});

btnEditProfile?.addEventListener("click", () => {
  // noop; link handles navigation
});

dShareAgentSearch?.addEventListener("input", () => {
  clearTimeout(shareLookupTimer);
  shareLookupTimer = setTimeout(() => {
    void searchShareAgents(dShareAgentSearch.value || "");
  }, 220);
});

btnShareAgentAccess?.addEventListener("click", () => {
  void grantSelectedAgentAccess();
});

btnResendClientInvite?.addEventListener("click", () => {
  void resendClientInvite();
});

btnDeleteClient?.addEventListener("click", () => {
  if (!activeClientId) return;
  if (!confirm("ARE YOU SURE YOU WANT TO DELETE THIS CLIENT? This removes the profile, household, and portal access.")) return;

  const f = document.getElementById("__af");
  if (!f) {
    // Fallback path when the hidden antiforgery form isn't on the page
    const token = getAntiForgeryToken();
    if (!token){
      toast("Missing antiforgery token.");
      return;
    }
    const formData = new FormData();
    formData.append("__RequestVerificationToken", token);
    formData.append("clientUserId", activeClientId);
    fetch("/Clients/Delete", {
      method: "POST",
      credentials: "include",
      body: formData
    })
    .then(res => {
      if (!res.ok) throw new Error(`Delete failed (${res.status})`);
      toast("Client deleted. Reloading…");
      window.location.href = "/Clients";
    })
    .catch(err => toast(err.message || "Delete failed.", { error:true, persistent:true }));
    return;
  }

  f.setAttribute("action", "/Clients/Delete");
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
      row.dataset.sAttemptstoday = String(data.attemptsToday ?? row.dataset.sAttemptstoday ?? 0);
      row.dataset.sAttemptsweek = String(data.attemptsThisWeek ?? row.dataset.sAttemptsweek ?? 0);
      row.dataset.sAttemptsmonth = String(data.attemptsThisMonth ?? row.dataset.sAttemptsmonth ?? 0);
      row.dataset.sAttemptsyear = String(data.attemptsThisYear ?? row.dataset.sAttemptsyear ?? 0);
      row.dataset.sAttemptslife = String(data.attemptsLifetime ?? row.dataset.sAttemptslife ?? 0);
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
  if (!confirm("Clear this client activity timeline?")) return;

  postJson("/Clients/ClearActivities", { clientUserId: activeClientId })
    .then((data) => {
      const row = rows.find(r => r.dataset.clientId === activeClientId);
      if (row){
        row.dataset.sAttemptstoday = String(data?.attemptsToday ?? 0);
        row.dataset.sAttemptsweek = String(data?.attemptsThisWeek ?? 0);
        row.dataset.sAttemptsmonth = String(data?.attemptsThisMonth ?? 0);
        row.dataset.sAttemptsyear = String(data?.attemptsThisYear ?? 0);
        row.dataset.sAttemptslife = String(data?.attemptsLifetime ?? 0);
        hydrateRow(row);
      }
      activeClientDetail = { ...(activeClientDetail || {}), activities: [] };
      renderTimeline([]);
      dAttempts.textContent = `Attempts: ${row?.dataset.crmAttemptsToday || 0} today • ${row?.dataset.crmAttemptsWeek || 0} week • ${row?.dataset.crmAttemptsLife || 0} total`;
      dSaved.textContent = "Timeline cleared ✔";
      toast("Timeline cleared");
      renderAll();
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

  saveQuickViewForRow(row, buildClientQuickViewOverrides(), "Saved ✔").then(() => {
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

btnImportLeads?.addEventListener("click", () => {
  if (importResult) importResult.textContent = "";
  if (importFile) importFile.value = "";
  openModal(importModal);
});

btnImportSubmit?.addEventListener("click", async () => {
  if (!importFile?.files?.length){
    toast("Choose a CSV file");
    return;
  }

  const form = new FormData();
  form.append("__RequestVerificationToken", getAntiForgeryToken());
  form.append("file", importFile.files[0]);

  const originalText = btnImportSubmit.textContent;
  btnImportSubmit.disabled = true;
  btnImportSubmit.textContent = "Importing...";

  try{
    const res = await fetch("/Clients/ImportLeadsCsv", {
      method: "POST",
      body: form,
      credentials: "include"
    });

    const raw = await res.text();
    let data;
    try { data = JSON.parse(raw); }
    catch { data = { error: raw || "Import failed." }; }

    if (!res.ok || data.error){
      throw new Error(data.error || "Import failed.");
    }

    const imported = data.imported || 0;
    const skipped = data.skipped || 0;
    const firstError = Array.isArray(data.errors) && data.errors.length ? data.errors[0] : "";
    const message = `Added ${imported} lead${imported === 1 ? "" : "s"}. Skipped ${skipped}.`;

    if (importResult) importResult.textContent = firstError ? `${message} ${firstError}` : message;
    toast(message);

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
        <button type="button" class="btn btn-ghost" data-taskoutcome="${safeHtml(row.dataset.clientId)}:NoAnswer">No Answer</button>
        <button type="button" class="btn btn-ghost" data-taskoutcome="${safeHtml(row.dataset.clientId)}:LeftVM">Left VM</button>
        <button type="button" class="btn btn-ghost" data-taskoutcome="${safeHtml(row.dataset.clientId)}:Spoke">Spoke</button>
        <button type="button" class="btn btn-ghost" data-taskoutcome="${safeHtml(row.dataset.clientId)}:Booked">Booked</button>
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
    try{
      const response = await postJson("/Clients/ApplyOutcome", {
        clientUserId: activeClientId,
        outcomeCode: btn.getAttribute("data-outcome"),
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
               draggable="true"
               data-cardid="${safeHtml(r.dataset.clientId)}">
        <div class="client-card-head" style="position:relative;">
          <div class="client-card-main">
            <h3 class="cc-name" data-open-card="${safeHtml(r.dataset.clientId)}">${safeHtml(displayName)}</h3>
            <div class="cc-sub cc-sub-primary">${phone ? `<a class=\"link link-phone\" href=\"tel:${safeHtml(phone)}\">${safeHtml(phoneDisplay)}</a>` : "No phone"}</div>
            <div class="cc-sub">${renderEmailLinkHtml(email)}</div>
          </div>
          ${prodBadge}
        </div>
        <div class="client-card-actions actions" style="margin-top:10px; padding-top:8px; border-top:1px solid rgba(0,0,0,.10); justify-content:flex-end; gap:8px;">
          ${phone ? `<a class="btn btn-ghost" href="tel:${safeHtml(phone)}">Call</a>` : ""}
          <button type="button"
                  class="btn btn-gold openCard"
                  data-open-card="${safeHtml(r.dataset.clientId)}"
                  title="Open Quick View"
                  style="min-width:110px;">
            Quick View
          </button>
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
          <div class="pipeline-lane-meta">
            <span class="pipeline-lane-count">${stageRows.length} contact${stageRows.length === 1 ? "" : "s"}</span>
            <button type="button" class="btn btn-ghost" data-pipeline-nav="${stage.key}">${focusMeta ? "Refresh" : "Review"}</button>
          </div>
        </div>
        <div class="pipeline-lane-body" data-dropzone="${stage.key}">
          ${renderLaneCards(stageRows)}
        </div>
      </section>
    `;
  }).join("");

  filteredRows.forEach(r => updatePipelineCardProduction(r.dataset.clientId));
}

async function saveQuickViewForRow(row, overrides, successMessage){
  const clientId = row?.dataset.clientId;
  // Keep a local alias for any legacy references that still expect `id`.
  const id = clientId;
  const payload = {
    clientUserId: row.dataset.clientId,
    email: dEmailInput?.value || "",
    phone: dPhoneInput?.value || "",
    dob: dDob?.value || null,
    gender: dGender?.value || "",
    addressLine: dAddress?.value || "",
    city: dCity?.value || "",
    state: dState?.value || "",
    county: dCounty?.value || "",
    zipCode: dZip?.value || "",
    phone2: dPhone2Input?.value || "",
    age: dAge?.value || "",
    btc: dBtc?.value || "",
    mortgageLender: dLender?.value || "",
    loanAmount: dLoanAmount?.value || "",
    crmStatus: (overrides?.crmStatus ?? norm(row.dataset.crmStatus)) || "Lead",
    crmPriority: (overrides?.crmPriority ?? norm(row.dataset.crmPriority)) || "Normal",
    crmLastTouch: (overrides?.crmLastTouch ?? norm(row.dataset.crmLastTouch)) || null,
    crmNextDate: (overrides?.crmNextDate ?? norm(row.dataset.crmNextDate)) || null,
    crmNextText: overrides?.crmNextText ?? norm(row.dataset.crmNextText),
    crmTags: overrides?.crmTags ?? norm(row.dataset.crmTags),
    agentNotes: overrides?.agentNotes ?? norm(row.dataset.crmNotes),
    pipelineStage: (overrides?.pipelineStage ?? norm(row.dataset.crmPipeline)) || "NewLead",
    waitingOn: overrides?.waitingOn ?? norm(row.dataset.crmWaitingOn),
    pinnedBrief: overrides?.pinnedBrief ?? norm(row.dataset.crmPinnedBrief),
    docIdReceived: overrides?.docIdReceived ?? !!dDocIdReceived?.checked,
    docAppSent: overrides?.docAppSent ?? !!dDocAppSent?.checked,
    docAppSigned: overrides?.docAppSigned ?? !!dDocAppSigned?.checked,
    docPolicyDelivered: overrides?.docPolicyDelivered ?? !!dDocPolicyDelivered?.checked,
    docReviewBooked: overrides?.docReviewBooked ?? !!dDocReviewBooked?.checked,
    opportunityPlanning: buildOpportunityPlanningPayload(overrides?.opportunityPlanning),
    watchers: overrides?.watchers ?? norm(row.dataset.crmWatchers),
    mentionNote: overrides?.mentionNote ?? ""
  };

  const response = await postJson("/Clients/SaveQuickView", payload);
  const data = response.payload || response;
  row.dataset.sStatus = data.crmStatus || "Lead";
  row.dataset.sPriority = data.crmPriority || "Normal";
  row.dataset.sLasttouch = data.crmLastTouch || "";
  row.dataset.sNextdate = data.crmNextDate || "";
  row.dataset.sNexttext = data.crmNextText || "";
  row.dataset.sTags = data.crmTags || "";
  row.dataset.sNotes = data.agentNotes || "";
  row.dataset.sRecordtype = data.recordType || row.dataset.sRecordtype || "";
  row.dataset.advancedMarketsEligible = ((data.advancedMarketsEligible ?? isAdvancedMarketsEligible(data.recordType || row.dataset.sRecordtype || "", row.dataset.advancedMarketsEligible)) ? "true" : "false");
  row.dataset.sPipeline = data.pipelineStage || "NewLead";
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
  row.dataset.sPlanningcount = String(data.opportunityPlanning?.completedCount || row.dataset.sPlanningcount || 0);
  row.dataset.sOwner = data.collaboration?.owner || "";
  row.dataset.sWatchers = (data.collaboration?.watchers || []).join(", ");
  row.dataset.email = data.email || row.dataset.email || "";
  row.dataset.phone = data.phone || row.dataset.phone || "";
  row.dataset.dob = data.dob || "";
  row.dataset.gender = data.gender || "";
  row.dataset.addressLine = data.addressLine || "";
  row.dataset.city = data.city || "";
  row.dataset.state = data.state || "";
  row.dataset.county = data.county || "";
  row.dataset.zipCode = data.zipCode || "";
  row.dataset.phone2 = data.phone2 || "";
  row.dataset.age = data.age || "";
  row.dataset.btc = data.btc || "";
  row.dataset.mortgageLender = data.mortgageLender || "";
  row.dataset.loanAmount = data.loanAmount || "";
  hydrateRow(row);

  syncDrawerEmailDisplay(row.dataset.email);
  if (dPhone) dPhone.textContent = row.dataset.phone || "No phone";

  if (activeClientId === row.dataset.clientId){
    activeClientDetail = { ...(activeClientDetail || {}), ...data };
    dStatus.value = data.crmStatus || dStatus.value;
    dPriority.value = data.crmPriority || dPriority.value;
    dLastTouch.value = data.crmLastTouch || dLastTouch.value;
    setDrawerNextActionDate(data.crmNextDate || dNextDate.value);
    dNextText.value = data.crmNextText || dNextText.value;
    dTags.value = data.crmTags || dTags.value;
    dNotes.value = data.agentNotes || dNotes.value;
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
    dPipelineStage.value = data.pipelineStage || dPipelineStage.value;
    dMeetingLocation.value = data.meetingLocation || dMeetingLocation.value;
    dZoomJoinUrl.value = data.zoomJoinUrl || dZoomJoinUrl.value;
    dUsePersonalZoomLink.checked = !!data.usePersonalZoomLink;
    dMeetingTime.value = data.meetingTime || dMeetingTime.value;
    dMeetingDuration.value = String(data.meetingDurationMinutes || dMeetingDuration.value || 30);
    dWaitingOn.value = data.waitingOn || dWaitingOn.value;
    dPinnedBrief.value = data.pinnedBrief || dPinnedBrief.value;
    setAdvancedMarketsActionState(
      data.recordType || row.dataset.sRecordtype || "",
      data.advancedMarketsEligible ?? row.dataset.advancedMarketsEligible
    );
    dAssignedOwner.value = data.collaboration?.owner || dAssignedOwner.value;
    dWatchers.value = (data.collaboration?.watchers || []).join(", ");
    if (data.docChecklist){
      dDocIdReceived.checked = !!data.docChecklist.idReceived;
      dDocAppSent.checked = !!data.docChecklist.appSent;
      dDocAppSigned.checked = !!data.docChecklist.appSigned;
      dDocPolicyDelivered.checked = !!data.docChecklist.policyDelivered;
      dDocReviewBooked.checked = !!data.docChecklist.reviewBooked;
    }
    applyOpportunityPlanningValues(data.opportunityPlanning);
  dStageAge.textContent = `Stage Age: ${data.stageAgeDays || stageAgeDays(row)}d`;

  loadClientProductionHistory(clientId, data.name || "Client");
    dAttempts.textContent = `Attempts: ${data.attemptsToday || 0} today • ${data.attemptsThisWeek || 0} week • ${data.attemptsLifetime || 0} total`;
    dWaitingOnPill.textContent = data.waitingOnLabel || waitingLabel(data.waitingOn || "WaitingOnAgent");
    renderMentionNotes(data.collaboration?.mentionNotes || []);
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
    toast("Priority reordered");
    renderAll();
    return;
  }

  try{
    await saveQuickViewForRow(row, { pipelineStage: targetStage }, `Moved to ${pipelineLabel(targetStage)} ✔`);
    toast(`Moved to ${pipelineLabel(targetStage)}`);
  }catch(err){
    console.error(err);
    toast(err?.message || "Stage move failed.");
  }
});

/* ========= Columns Modal ========= */
function openModal(el){
  modalBackdrop.classList.add("open");
  el.classList.add("open");
}
function closeModal(){
  clearAdvancedMarketsAutosaveTimer();
  activeAdvancedMarketsLoadSeq += 1;
  modalBackdrop.classList.remove("open");
  [colsModal, shortcutsModal, remindersModal, cmdModal, bulkModal, callTaskModal, importModal].forEach(m => m?.classList.remove("open"));
}

$("#btnCols")?.addEventListener("click", () => {
  const cols = ["select","name","email","phone","portal","status","stage","next","touch","actions"];
  const saved = loadJSON(LS_COLS, {});
  colsBody.innerHTML = cols.map(c => {
    const on = saved[c] !== false;
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
    closeDrawer();
    closeModal();
  }

  if (e.key === "/" && !["INPUT","TEXTAREA"].includes(document.activeElement?.tagName)){
    e.preventDefault();
    const input = $(".field input[name='search']");
    input?.focus();
  }

  const isMac = navigator.platform.toLowerCase().includes("mac");
  if ((isMac ? e.metaKey : e.ctrlKey) && e.key.toLowerCase() === "k"){
    e.preventDefault();
    openCmd();
  }

  if (!drawer.classList.contains("open")) return;

  if (e.altKey && e.key.toLowerCase() === "n"){
    e.preventDefault();
    const btn = $$(".outcome-btn").find(x => x.getAttribute("data-outcome") === "NoAnswer");
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
    dPipelineStage.value = "MeetingScheduled";
    dSaved.textContent = "Quick key: moved stage to Meeting Scheduled — saving…";
    queueQuickViewAutosave();
  }
});

/* ========= Calendar Sync (Microsoft 365 / Outlook) ========= */
async function calendarStatus(){
  try{
    const res = await fetch("/calendar/status", { credentials:"include" });
    if (!res.ok) return { connected:false };
    return await res.json();
  }catch{ return { connected:false }; }
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
  pipelineFocusStage = "MeetingScheduled";
  pipelineNavSelectedStage = "MeetingScheduled";
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

btnPipelineNew?.addEventListener("click", (e) => {
  e.preventDefault();
  window.location.href = "/Clients/Create";
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
    const res = await fetch(`/calendar/day-availability?date=${encodeURIComponent(nextDate)}`, {
      credentials: "include",
      signal: calendarBusyAbort.signal
    });

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

/* ========= Prefs Restore ========= */
(function restorePrefs(){
  const prefs = loadJSON(LS_PREFS, {});
  viewMode.value = "pipeline";
  if (prefs.density) density.value = prefs.density;
  applyViewMode();
  applyDensityClass();
})();

wireQuickViewAutosave();

// ===== Production (Client) - inline, autosave =====
const clientProdAmount = document.getElementById("clientProdAmount");
const clientProdPersonal = document.getElementById("clientProdPersonal");
const clientProdStatus = document.getElementById("clientProdStatus");
const clientProdNotes = document.getElementById("clientProdNotes");
const clientProdSaveBtn = document.getElementById("clientProdSaveBtn");
const clientProdResetBtn = document.getElementById("clientProdResetBtn");
const clientProdSaveStatus = document.getElementById("clientProdSaveStatus");
let clientProdActiveId = null;
let clientProdTimer = null;

function setClientProdStatus(msg, css){
  if (!clientProdSaveStatus) return;
  clientProdSaveStatus.textContent = msg;
  clientProdSaveStatus.className = css ? `tiny ${css}` : "tiny";
}

function hydrateClientProdForm(item){
  if (clientProdAmount) clientProdAmount.value = item ? item.amount : "";
  if (clientProdPersonal) clientProdPersonal.value = item ? item.personalAmount ?? "" : "";
  if (clientProdStatus) clientProdStatus.value = item ? (item.status === "Issued" ? "1" : item.status === "Paid" ? "2" : "0") : "0";
  if (clientProdNotes) clientProdNotes.value = item ? (item.notes ?? "") : "";
}

async function saveClientProductionInline(){
  if (!clientProdActiveId) return;
  const token = getAntiForgeryToken();
  const body = new URLSearchParams();
  body.set("clientUserId", clientProdActiveId);
  body.set("amount", clientProdAmount?.value || "0");
  body.set("personalAmount", clientProdPersonal?.value || "0");
  body.set("status", clientProdStatus?.value || "0");
  body.set("notes", clientProdNotes?.value || "");
  setClientProdStatus("Saving…");
  await fetch("/production/add/client", {
    method:"POST",
    headers:{
      "Content-Type":"application/x-www-form-urlencoded",
      "RequestVerificationToken": token,
      "Accept":"application/json"
    },
    body: body.toString()
  }).then(()=>setClientProdStatus("Saved","text-success"))
    .catch(()=>setClientProdStatus("Save failed","text-danger"));
  await loadClientProductionHistory(clientProdActiveId, null, false);
}

function queueClientProdSave(){
  if (!clientProdActiveId) return;
  setClientProdStatus("Saving…");
  clearTimeout(clientProdTimer);
  clientProdTimer = setTimeout(saveClientProductionInline, 500);
}

async function resetClientProductionInline(){
  if (!clientProdActiveId) return;
  if (!confirm("Reset production for this client?")) return;
  const token = getAntiForgeryToken();
  await fetch("/production/reset/client", {
    method:"POST",
    headers:{
      "Content-Type":"application/x-www-form-urlencoded",
      "RequestVerificationToken": token,
      "Accept":"application/json"
    },
    body:`clientUserId=${encodeURIComponent(clientProdActiveId)}`
  }).catch(()=>{});
  hydrateClientProdForm(null);
  setClientProdStatus("Reset","text-warning");
  await loadClientProductionHistory(clientProdActiveId, null, false);
}

async function loadClientProductionHistory(clientUserId, displayName, hydrate=true){
  const list = document.getElementById("clientPhList");
  const addBtn = document.getElementById("clientPhAdd");
  if (!list) return;
  clientProdActiveId = clientUserId;
  list.innerHTML = '<div class=\"ph-empty muted\">Loading…</div>';
  try{
    const res = await fetch(`/production/history/client?clientUserId=${encodeURIComponent(clientUserId)}`, { headers: { 'Accept':'application/json' }});
    if (!res.ok) throw new Error("load fail");
    const data = await res.json();
    const item = (data && data.length) ? data[0] : null;
    const totals = (data || []).reduce((acc, p) => {
      const amt = Number(p?.amount || 0);
      const st = norm(p?.status);
      if (st === "paid") acc.paid += amt;
      else if (st === "issued") acc.issued += amt;
      else acc.submitted += amt;
      return acc;
    }, { paid: 0, issued: 0, submitted: 0 });

    if (item){
      setClientProductionById(clientUserId, item.status, item.amount, totals);
    } else {
      setClientProductionById(clientUserId, "", 0, totals);
    }
    if (hydrate) hydrateClientProdForm(item);
    if (!data || !data.length){
      list.innerHTML = '<div class="ph-empty muted">No production yet.</div>';
    } else {
      list.innerHTML = "";
      data.forEach(p=>{
        const div = document.createElement("div");
        div.className = "ph-item";
        div.innerHTML = `<div class="ph-left">
            <div class="ph-amt">$${Number(p.amount).toLocaleString(undefined,{maximumFractionDigits:2})}</div>
            <div class="ph-amt personal">Personal: $${Number(p.personalAmount || 0).toLocaleString(undefined,{maximumFractionDigits:2})}</div>
            <div class="ph-status ${p.status.toLowerCase()}">${p.status}</div>
            <div class="ph-note">${p.notes ?? ""}</div>
          </div>
          <div class="ph-actions">
            <button class="btn btn-ghost ph-edit" data-id="${p.id}" data-amount="${p.amount}" data-personal="${p.personalAmount ?? ""}" data-status="${p.status}" data-notes="${p.notes ?? ""}">Edit</button>
            <button class="btn btn-red ph-delete" data-id="${p.id}">Delete</button>
          </div>`;
        list.appendChild(div);
      });
      list.querySelectorAll(".ph-edit").forEach(btn=>{
        btn.addEventListener("click", ()=>{
          openClientProductionModalEdit(
            btn.getAttribute("data-id"),
            clientUserId,
            btn.getAttribute("data-amount"),
            btn.getAttribute("data-status"),
            btn.getAttribute("data-notes"),
            displayName,
            btn.getAttribute("data-personal")
          );
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
          await loadClientProductionHistory(clientUserId, displayName);
        });
      });
    }
  }catch{
    list.innerHTML = '<div class=\"ph-empty muted\">Unable to load production.</div>';
  }

  if (addBtn){
    addBtn.onclick = () => openClientProductionModalAdd(clientUserId, displayName || "Client");
  }

  refreshClientProductionTiles();
}

if (clientProdAmount){
  [clientProdAmount, clientProdPersonal, clientProdStatus, clientProdNotes].forEach(el=>{
    el?.addEventListener("input", queueClientProdSave);
    el?.addEventListener("change", queueClientProdSave);
  });
}
clientProdSaveBtn?.addEventListener("click", saveClientProductionInline);
clientProdResetBtn?.addEventListener("click", resetClientProductionInline);

function openClientProductionModalAdd(clientId, name){
  const modalEl = document.getElementById('clientProductionModal');
  if (!modalEl) return;
  const form = modalEl.querySelector("form");
  form.action = "/production/add/client";
  form.querySelector("input[name='id']")?.remove();
  document.getElementById('clientProdId').value = clientId;
  document.getElementById('clientProdName').textContent = name;

  const draftAll = loadJSON(LS_PROD_DRAFT_CLIENT, {});
  const draft = draftAll[clientId] || {};
  const amtEl = form.querySelector("input[name='amount']");
  const personalEl = form.querySelector("input[name='personalAmount']");
  const statusEl = form.querySelector("select[name='status']");
  const notesEl = form.querySelector("textarea[name='notes']");
  if (amtEl && draft.amount != null) amtEl.value = draft.amount;
  if (personalEl && draft.personalAmount != null) personalEl.value = draft.personalAmount;
  if (statusEl && draft.status != null) statusEl.value = draft.status;
  if (notesEl && draft.notes != null) notesEl.value = draft.notes;

  const persistDraft = () => {
    draftAll[clientId] = {
      amount: amtEl?.value || "",
      personalAmount: personalEl?.value || "",
      status: statusEl?.value || "0",
      notes: notesEl?.value || ""
    };
    saveJSON(LS_PROD_DRAFT_CLIENT, draftAll);
  };
  [amtEl, personalEl, statusEl, notesEl].forEach(el => el?.addEventListener("input", persistDraft, { once: false }));
  form.addEventListener("submit", () => {
    delete draftAll[clientId];
    saveJSON(LS_PROD_DRAFT_CLIENT, draftAll);
  }, { once: true });

  bootstrap.Modal.getOrCreateInstance(modalEl).show();
}

function openClientProductionModalEdit(id, clientId, amount, status, notes, name, personal){
  const modalEl = document.getElementById('clientProductionModal');
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
  document.getElementById('clientProdId').value = clientId;
  document.getElementById('clientProdName').textContent = name || clientId;
  const amtEl = form.querySelector("input[name='amount']");
  if (amtEl) amtEl.value = amount;
  const personalEl = form.querySelector("input[name='personalAmount']");
  if (personalEl) personalEl.value = personal || "";
  const statusEl = form.querySelector("select[name='status']");
  if (statusEl) statusEl.value = status === "Issued" ? "1" : status === "Paid" ? "2" : "0";
  const notesEl = form.querySelector("textarea[name='notes']");
  if (notesEl) notesEl.value = notes || "";

  const draftAll = loadJSON(LS_PROD_DRAFT_CLIENT, {});
  const persistDraft = () => {
    draftAll[clientId] = {
      amount: amtEl?.value || "",
      personalAmount: personalEl?.value || "",
      status: statusEl?.value || "0",
      notes: notesEl?.value || ""
    };
    saveJSON(LS_PROD_DRAFT_CLIENT, draftAll);
  };
  [amtEl, personalEl, statusEl, notesEl].forEach(el => el?.addEventListener("input", persistDraft, { once: false }));
  form.addEventListener("submit", () => {
    delete draftAll[clientId];
    saveJSON(LS_PROD_DRAFT_CLIENT, draftAll);
  }, { once: true });

  bootstrap.Modal.getOrCreateInstance(modalEl).show();
}

function addResetActionClient(list, clientUserId, displayName){
  if (!list) return;
  const reset = document.createElement("button");
  reset.type = "button";
  reset.className = "btn btn-red";
  reset.textContent = "Reset Production";
  reset.style.marginTop = "10px";
  reset.addEventListener("click", async ()=>{
    if (!confirm("Reset production for this client?")) return;
    const token = getAntiForgeryToken();
    await fetch("/production/reset/client", {
      method:"POST",
      headers:{
        "Content-Type":"application/x-www-form-urlencoded",
        "RequestVerificationToken": token,
        "Accept":"application/json"
      },
      body:`clientUserId=${encodeURIComponent(clientUserId)}`
    }).catch(()=>{});
    loadClientProductionHistory(clientUserId, displayName);
  });
  list.appendChild(reset);
}

/* ========= Boot ========= */
async function boot(){
  syncBarHeight();
  applyColumnPrefs();
  applyDensityClass();
  renderSavedViews();
  ensureModalInBody('clientQuickCreateActionModal');
  bindQuickViewTabs();

  await loadMyDaySnapshot(true);

  renderAll();
  refreshClientProductionTiles();
  openQuickViewFromUrl();
  updateSelectionUI();
  refreshRemindersUI();

  checkReminders();
  setInterval(checkReminders, 60 * 1000);

  updateCalendarButton();
  updateZoomControls();
}
boot();

/* ========= Copy Emails guard ========= */
btnCopyEmails?.addEventListener("click", () => {
  const emails = getCheckedRows().map(r => norm(r.dataset.email)).filter(Boolean);
  if (!emails.length) return toast("No emails selected");
  copyText(emails.join(", "));
});

document.addEventListener("change", (e) => {
  if (e.target && e.target.classList?.contains("row-chk")) updateSelectionUI();
});

// Persist Advanced Markets inputs when navigating away, to avoid accidental clears.
window.addEventListener("pagehide", () => {
  try {
    if (!activeAdvancedMarketsClient?.clientProfileId) return;
    const payload = buildAdvancedMarketsSavePayload();
    if (!payload) return;
    const fp = fingerprintAdvancedMarketsInputs(payload);
    if (fp === "{}") return; // guard: don't overwrite with empty
    writeAdvancedMarketsDraft(payload);
    void persistAdvancedMarketsInputs({ auto: true, force: true, allowClosed: true, sessionId: advancedMarketsCurrentSession });
  } catch (_) { /* ignore */ }
});

// Live sync listeners (optional)
if (liveSync){
  liveSync.onPage((pageKey, pageNumber) => {
    if (pageKey !== "clients") return;
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
}
const hybridBar = null;
const hbJumpBoard = null;
const hbJumpTable = null;
const hbClearFilters = null;
const hbRefresh = null;
const hbCallTasks = null;
const hbTop = null;
