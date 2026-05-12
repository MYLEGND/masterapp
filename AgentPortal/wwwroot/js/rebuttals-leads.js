(function(){
  const bridges = Array.from(document.querySelectorAll('[data-lead-bridge]'));
  if (!bridges.length) return;

  const LS_PROFILE = "legend_agent_profile";
  const bucketLabels = {
    MortgageProtection: "Mortgage Protection",
    LifeInsurance: "Life Insurance",
    FinalExpense: "Final Expense",
    DisabilityInsurance: "Disability Insurance",
    CalledToday: "Called Today",
    CallBack: "Call Back",
    Contacted: "Contacted",
    Booked: "Booked",
    FollowUp: "Follow Up",
    NeedsDocs: "Needs Docs",
    PolicyPlaced: "Policy Placed",
    Voicemail: "Voicemail Left",
    NotInterested: "Not Interested",
    Nurture: "Nurture",
    NoAnswer: "No Answer",
    Lost: "Lost",
    AIReception: "AI Reception",
    DoNotCallList: "Do Not Call List"
  };
  const allStages = [
    "MortgageProtection",
    "LifeInsurance",
    "FinalExpense",
    "DisabilityInsurance",
    "CalledToday",
    "CallBack",
    "Contacted",
    "Booked",
    "FollowUp",
    "NeedsDocs",
    "PolicyPlaced",
    "Voicemail",
    "NotInterested",
    "Nurture",
    "NoAnswer",
    "Lost",
    "AIReception",
    "DoNotCallList"
  ];
  const noCallStages = new Set(["Booked", "FollowUp", "PolicyPlaced"]);
  const doNotCallStages = new Set(["DoNotCallList"]);
  const productBuckets = new Set([
    "MortgageProtection",
    "LifeInsurance",
    "FinalExpense",
    "DisabilityInsurance"
  ]);
  const agentTimeZone = Intl.DateTimeFormat().resolvedOptions().timeZone || "";
  const agentTzOffset = String(new Date().getTimezoneOffset());
  const signalRAvailable = typeof signalR !== 'undefined';
  const leadBridgeControllers = [];

  function pickLeadBridgeController(queueKey){
    const normalized = normalizeQueueKey(queueKey || '');
    return leadBridgeControllers.find(controller => controller.queueKey === normalized)
      || leadBridgeControllers.find(controller => controller.rawQueueKey === (queueKey || ''))
      || leadBridgeControllers[0]
      || null;
  }

  function syncLeadBridgeApi(){
    window.LeadBridge = window.LeadBridge || {};
    window.LeadBridge.selectLead = async ({ leadId, queueKey } = {}) => {
      const controller = pickLeadBridgeController(queueKey);
      if (!controller || !leadId) return false;
      return controller.selectLeadById(leadId);
    };
    window.LeadBridge.getCurrentLead = (queueKey) => {
      const controller = pickLeadBridgeController(queueKey);
      return controller ? controller.getCurrentLead() : null;
    };
    window.LeadBridge.sendTextMessage = async ({ message, queueKey } = {}) => {
      const controller = pickLeadBridgeController(queueKey);
      if (!controller || !message) return false;
      return controller.sendTextMessage(message);
    };
  }

  function registerLeadBridgeController(controller){
    if (!controller) return;
    const existingIndex = leadBridgeControllers.findIndex(item => item.bridge === controller.bridge);
    if (existingIndex >= 0) leadBridgeControllers.splice(existingIndex, 1, controller);
    else leadBridgeControllers.push(controller);
    syncLeadBridgeApi();
  }

  function withDialHeaders(init = {}) {
    const headers = new Headers(init.headers || {});
    if (agentTimeZone) headers.set("X-Agent-TimeZone", agentTimeZone);
    headers.set("X-Agent-TzOffset", agentTzOffset);
    return { ...init, headers };
  }

  function normalizedOriginalLeadType(value){
    const key = (value || '').trim();
    return productBuckets.has(key) ? key : '';
  }

  function parseLeadAge(value){
    if (value === null || value === undefined) return null;
    const match = String(value).match(/\d+/);
    if (!match) return null;
    const age = Number.parseInt(match[0], 10);
    return Number.isFinite(age) ? age : null;
  }

  function parseLeadDob(value){
    if (!value) return null;

    if (value instanceof Date && !Number.isNaN(value.getTime())){
      return {
        year: value.getFullYear(),
        month: value.getMonth() + 1,
        day: value.getDate()
      };
    }

    const raw = String(value).trim();
    if (!raw) return null;

    let match = raw.match(/^(\d{4})-(\d{2})-(\d{2})/);
    if (match){
      const year = Number.parseInt(match[1], 10);
      const month = Number.parseInt(match[2], 10);
      const day = Number.parseInt(match[3], 10);
      if (Number.isFinite(year) && Number.isFinite(month) && Number.isFinite(day)){
        return { year, month, day };
      }
    }

    match = raw.match(/^(\d{1,2})-(\d{1,2})-(\d{4})$/);
    if (match){
      const month = Number.parseInt(match[1], 10);
      const day = Number.parseInt(match[2], 10);
      const year = Number.parseInt(match[3], 10);
      if (Number.isFinite(year) && Number.isFinite(month) && Number.isFinite(day)){
        return { year, month, day };
      }
    }

    const parsed = new Date(raw);
    if (Number.isNaN(parsed.getTime())) return null;

    return {
      year: parsed.getFullYear(),
      month: parsed.getMonth() + 1,
      day: parsed.getDate()
    };
  }

  function deriveLeadAgeFromDob(value){
    const dob = parseLeadDob(value);
    if (!dob) return null;

    const today = new Date();
    let age = today.getFullYear() - dob.year;
    const monthDelta = (today.getMonth() + 1) - dob.month;
    const hasHadBirthday =
      monthDelta > 0 ||
      (monthDelta === 0 && today.getDate() >= dob.day);

    if (!hasHadBirthday) age -= 1;
    return age >= 0 ? age : null;
  }

  function resolveLeadAgeNumber(lead){
    const explicitAge = parseLeadAge(lead?.age);
    if (explicitAge !== null) return explicitAge;
    return deriveLeadAgeFromDob(lead?.dob);
  }

  function normalizeLeadAgeFromDob(lead){
    if (!lead) return lead;
    const derivedAge = deriveLeadAgeFromDob(lead.dob);
    if (derivedAge === null) return lead;
    lead.age = String(derivedAge);
    return lead;
  }

  function parseAgeRangeValue(value){
    const raw = (value || '').trim();
    if (!raw) return null;
    const m = raw.match(/^(\d+)-(\d+)$/);
    if (!m) return null;
    const min = Number.parseInt(m[1], 10);
    const max = Number.parseInt(m[2], 10);
    if (!Number.isFinite(min) || !Number.isFinite(max) || max <= min) return null;
    return { min, max };
  }

  function leadMatchesAgeRange(lead, rangeValue){
    if (!rangeValue) return true;
    const range = parseAgeRangeValue(rangeValue);
    if (!range) return true;
    const leadAge = resolveLeadAgeNumber(lead);
    if (leadAge === null) return false;
    return leadAge >= range.min && leadAge < range.max;
  }

  function leadOriginalLeadType(lead){
    return normalizedOriginalLeadType(lead?.originalLeadType) || normalizedOriginalLeadType(lead?.bucket);
  }

  function leadWasCalledToday(lead){
    return Number(lead?.attemptsToday ?? lead?.dialsToday ?? 0) > 0;
  }

  function matchesStageSelection(lead, stage){
    if (!stage) return true;
    if (productBuckets.has(stage)) return leadOriginalLeadType(lead) === stage;
    if (stage === "CalledToday") return leadWasCalledToday(lead);
    return ((lead?.bucket || lead?.crmStage || '').trim()) === stage;
  }

  function normalizedStageKey(value){
    return (value || '')
      .trim()
      .replace(/\s+/g, '')
      .replace(/[-_]/g, '');
  }

  function loadProfile(){
    let profile;
    try { profile = JSON.parse(localStorage.getItem(LS_PROFILE) || "{}") || {}; }
    catch { profile = {}; }

    const serverFirstName = normalizeProfileField(window.LegendAgentProfileApi?.getFirstName?.());
    const serverPhone = normalizeProfileField(window.LegendAgentProfileApi?.getPhone?.());
    let changed = false;

    if (serverFirstName && profile.firstName !== serverFirstName){
      profile.firstName = serverFirstName;
      changed = true;
    }

    if (serverPhone && !normalizeProfileField(profile.phone)){
      profile.phone = serverPhone;
      changed = true;
    }

    if (changed){
      try {
        localStorage.setItem(LS_PROFILE, JSON.stringify(profile));
      } catch {}
    }

    return profile;
  }

  function normalizeProfileField(value){
    return typeof value === "string" ? value.trim() : "";
  }

  function normalizeDialTotal(value){
    if (typeof value === 'number' && Number.isFinite(value)) return value;
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : null;
  }

  function readAgentWideDialTotals(payload){
    return {
      today: normalizeDialTotal(payload?.dialsTodayAgentWide),
      week: normalizeDialTotal(payload?.dialsWeekAgentWide)
    };
  }

  function escapeHtml(value){
    return (value ?? '').toString()
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  }

  function bucketLabel(value){
    return bucketLabels[value] || value || 'Lead';
  }

  function normalizeQueueKey(value){
    const raw = (value || '').trim();
    if (!raw) return '';
    const key = raw.replace(/[\s\-_]/g, '').toLowerCase();
    const map = {
      mortgageprotection: 'MortgageProtection',
        mortgageprotectionleads: 'MortgageProtection',
        mortgageprotectionrebuttals: 'MortgageProtection',
      finalexpense: 'FinalExpense',
        finalexpenseleads: 'FinalExpense',
        finalexpenserebuttals: 'FinalExpense',
      lifeinsurance: 'LifeInsurance',
        lifeinsuranceleads: 'LifeInsurance',
        lifeinsurancerebuttals: 'LifeInsurance',
        disabilityinsurance: 'DisabilityInsurance',
        disabilityinsuranceleads: 'DisabilityInsurance',
        disabilityinsurancerebuttals: 'DisabilityInsurance'
    };
    return map[key] || raw;
  }

  function ensureAgentPhone(){
    const profile = loadProfile();
    if (profile.phone) return profile.phone;
    const entered = window.prompt("Enter your call-back number to use in texts:", "");
    if (entered){
      profile.phone = entered.trim();
      localStorage.setItem(LS_PROFILE, JSON.stringify(profile));
      return profile.phone;
    }
    return "";
  }

  function ensureAgentName(){
    const profile = loadProfile();
    if (profile.firstName) return profile.firstName;
    const entered = window.prompt("Enter your first name for texting:", "");
    if (entered){
      profile.firstName = entered.trim();
      localStorage.setItem(LS_PROFILE, JSON.stringify(profile));
      return profile.firstName;
    }
    return "Agent";
  }

  function getToken(root){
    const local = root.querySelector('input[name="__RequestVerificationToken"]');
    if (local) return local.value;
    const global = document.querySelector('input[name="__RequestVerificationToken"]');
    return global ? global.value : '';
  }

  function fmtPhone(raw){
    const digits = (raw || '').replace(/\D/g, '');
    if (digits.length === 10) return `(${digits.slice(0,3)}) ${digits.slice(3,6)}-${digits.slice(6)}`;
    return raw || '';
  }

  function normalizeSearchDigits(value){
    return (value || '').replace(/\D/g, '');
  }

  function leadMatchesBridgeSearch(lead, query){
    const textQuery = (query || '').toLowerCase().trim();
    if (!textQuery) return true;

    const digitQuery = normalizeSearchDigits(query);
    const hay = [
      lead?.firstName || '',
      lead?.lastName || '',
      lead?.email || '',
      lead?.phone || '',
      lead?.phone2 || ''
    ].join(' ').toLowerCase();

    if (hay.includes(textQuery)) return true;
    if (!digitQuery) return false;

    const phoneDigits = [
      normalizeSearchDigits(lead?.phone || ''),
      normalizeSearchDigits(lead?.phone2 || '')
    ].filter(Boolean);

    return phoneDigits.some(value => value.includes(digitQuery));
  }


  function formatDob(value){
    const dob = parseLeadDob(value);
    if (!dob) return '—';

    const mm = String(dob.month).padStart(2, '0');
    const dd = String(dob.day).padStart(2, '0');
    const yyyy = dob.year;

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

  function buildTextMessage(lead, bucket){
    const leadFirst = (lead.firstName || "there").trim() || "there";
    const agentFirst = ensureAgentName();
    const agentPhone = ensureAgentPhone() || "623-223-7177";
    const addrLine = (lead.addressLine || "").trim();
    const city = (lead.city || "").trim();
    const state = (lead.state || "").trim();
    const zip = (lead.zipCode || lead.zip || "").trim();
    const lender = (lead.mortgageLender || lead.lender || "").trim() || "your lender";
    const addrFull = [addrLine, city, state, zip].filter(Boolean).join(" ").replace(/\s+/g," ").trim();

    if ((bucket || "").toLowerCase() === "mortgageprotection"){
      return `${leadFirst}, this is ${agentFirst} regarding your mortgage with ${lender}. Just left you a message. Give me a call back when you get this, we have some pending paperwork to get out to you regarding the mortgage for your property at ${addrFull || 'your property'}. The office number is ${agentPhone}, thanks`;
    }

    return `Hi ${leadFirst}, this is ${agentFirst}. Let's connect about your ${bucket || 'policy'} - call or text me at ${agentPhone}.`;
  }

  function parseTextScriptTemplates(node){
    const raw = String(node?.textContent || '').trim();
    if (!raw) return [];
    try {
      const parsed = JSON.parse(raw);
      return Array.isArray(parsed)
        ? parsed
            .map(item => {
              if (!item || typeof item !== 'object') return null;
              const key = String(item.key || item.Key || '').trim();
              const title = String(item.title || item.Title || '').trim();
              const template = String(item.template || item.Template || '').trim();
              if (!title || !template) return null;
              return { key, title, template };
            })
            .filter(Boolean)
        : [];
    } catch {
      return [];
    }
  }

  function buildSmsLaunchHref(rawDigits, message){
    const digits = String(rawDigits || '').replace(/\D/g, '');
    const recipient = digits.length === 10 ? `+1${digits}` : digits;
    if (!recipient) return '';
    return `sms:${recipient}&body=${encodeURIComponent(String(message || '').trim())}`;
  }

  // Only treat true phone-sized viewports as mobile (protect desktop/tablet).
  function isMobileScreen() {
    return Math.min(window.innerWidth || 0, window.innerHeight || 0) <= 600;
  }

  function syncLeadBridgeMobileFlag(){
    const mobile = isMobileScreen();
    document.documentElement.classList.toggle('lead-bridge-mobile', mobile);
  }

  syncLeadBridgeMobileFlag();
  window.addEventListener('resize', syncLeadBridgeMobileFlag);
  window.addEventListener('orientationchange', syncLeadBridgeMobileFlag);
  document.addEventListener('DOMContentLoaded', syncLeadBridgeMobileFlag);

  bridges.forEach(async (bridge) => {
    const bucket = bridge.getAttribute('data-bucket') || '';
    const shellRoot = bridge.closest('#rbShell') || document;
    const queueKey = bucket || '';
    const normalizedQueueKey = normalizeQueueKey(queueKey);
    const filterStoreKey = `leadbridge_filters_${normalizedQueueKey || 'all'}`;
    let activeStateVersion = null;
    let suppressPush = false;
    let hydrationDone = false;
    let nextInFlight = false;
    const token = getToken(bridge);
    const statusEl = bridge.querySelector('[data-lb-status]');
    const posEl = bridge.querySelector('[data-lb-pos]');
    const originEl = bridge.querySelector('[data-lb-origin]');
    const metaWrap = posEl?.closest('.lb-meta') || null;
    const nextBtn = bridge.querySelector('[data-lb-next]');
    const headerMetaHost = shellRoot.querySelector('[data-lb-header-meta]');
    const headerNextHost = shellRoot.querySelector('[data-lb-header-next]');
    const callBtn = bridge.querySelector('[data-lb-call]');
    const textBtn = bridge.querySelector('[data-lb-text]');
    const actionHost = callBtn?.closest('.lb-actions') || null;
    const dayDialsBtn = bridge.querySelector('[data-lb-dials-day]');
    const deleteBtn = bridge.querySelector('[data-lb-delete]');
    const outcomeButtons = Array.from(bridge.querySelectorAll('[data-outcome]'));
    const firstOutcomeBtn = outcomeButtons[0] || null;
    const originalMetaHost = metaWrap?.parentElement || null;
    const clearBtn = bridge.querySelector('[data-lb-clear]');
    const editClientBtn = bridge.querySelector('[data-lb-edit-client]');
    const openCrmLink = bridge.querySelector('[data-lb-open-crm]');
    const stateFilter = bridge.querySelector('[data-lb-state-filter]');
    const stageFilter = bridge.querySelector('[data-lb-stage-filter]');
    const calledFilter = bridge.querySelector('[data-lb-called-filter]');
    const ageFilter = bridge.querySelector('[data-lb-age-filter]');
    const searchInput = bridge.querySelector('[data-lb-search]');
    const noteOpenBtn = bridge.querySelector('[data-note-self-open]');
    const noteOverlay = bridge.querySelector('[data-note-self-overlay]');
    const noteCloseBtn = bridge.querySelector('[data-note-self-close]');
    const noteDateInput = bridge.querySelector('[data-note-self-date]');
    const noteLeadInput = bridge.querySelector('[data-note-self-lead]');
    const noteDatesSelect = bridge.querySelector('[data-note-self-dates]');
    const noteSaveBtn = bridge.querySelector('[data-note-self-save]');
    const noteWentWell = bridge.querySelector('[data-note-self-well]');
    const noteCouldBetter = bridge.querySelector('[data-note-self-better]');
    const noteStatusEl = bridge.querySelector('[data-note-self-status]');
    const textTemplatesNode = bridge.querySelector('[data-lb-text-templates]');
    const textScriptTemplates = parseTextScriptTemplates(textTemplatesNode);
    const baseLabels = {
      call: (callBtn?.textContent || 'Call').trim() || 'Call',
      text: (textBtn?.textContent || 'Text').trim() || 'Text'
    };
    const fields = {
      name: bridge.querySelector('[data-lf-value="name"]'),
      leadId: bridge.querySelector('[data-lf-value="leadId"]'),
      calls: bridge.querySelector('[data-lf-value="calls"]'),
      address: bridge.querySelector('[data-lf-value="address"]'),
      city: bridge.querySelector('[data-lf-value="city"]'),
      state: bridge.querySelector('[data-lf-value="state"]'),
      county: bridge.querySelector('[data-lf-value="county"]'),
      dob: bridge.querySelector('[data-lf-value="dob"]'),
      gender: bridge.querySelector('[data-lf-value="gender"]'),
      lender: bridge.querySelector('[data-lf-value="lender"]'),
      loan: bridge.querySelector('[data-lf-value="loan"]'),
      phone: bridge.querySelector('[data-lf-value="phone"]'),
      phone2: bridge.querySelector('[data-lf-value="phone2"]'),
      age: bridge.querySelector('[data-lf-value="age"]'),
      btc: bridge.querySelector('[data-lf-value="btc"]')
    };
    const lenderField = bridge.querySelector('[data-lf="lender"]');
    const lenderLabel = bridge.querySelector('[data-lf-label="lender"]');
    const loanLabel = bridge.querySelector('[data-lf-label="loan"]');
    let confirmTimer = 0;
    let pendingAction = null;
    let baseLeads = [];
    let leads = [];
    let idx = 0;
    let canonicalLeadId = '';
    let canonicalPosition = 0;
    let canonicalTotal = 0;
    let agentWideDials = { today: 0, week: 0 };
    let serverFilterState = null;   // authoritative filter state from server
    let filterSyncToken = 0;        // increments per pushFilters(); guards stale responses
    let suppressFilterPush = false; // prevents push-back loops when server applies filter state
    let activeStateRequestToken = 0;
    let searchPushTimer = 0;
    let serverStateOptions = [];
    let noteDatesLoaded = false;
    let dialTotalsRefreshInFlight = false;
    let textMenuEl = null;

    function restoreMobileLeadControls(){
      if (originalMetaHost && metaWrap && metaWrap.parentElement !== originalMetaHost){
        originalMetaHost.appendChild(metaWrap);
      }

      if (!actionHost) return;

      if (callBtn && callBtn.parentElement !== actionHost){
        actionHost.insertBefore(callBtn, actionHost.firstElementChild || null);
      }

      if (textBtn && textBtn.parentElement !== actionHost){
        actionHost.insertBefore(textBtn, dayDialsBtn || firstOutcomeBtn || null);
      }

      if (nextBtn && nextBtn.parentElement !== actionHost){
        actionHost.insertBefore(nextBtn, firstOutcomeBtn || null);
      }
    }

    function moveDesktopLeadControls(){
      if (headerMetaHost && metaWrap && metaWrap.parentElement !== headerMetaHost){
        headerMetaHost.appendChild(metaWrap);
      }

      if (!headerNextHost) return;

      if (callBtn && callBtn.parentElement !== headerNextHost){
        headerNextHost.appendChild(callBtn);
      }
      if (textBtn && textBtn.parentElement !== headerNextHost){
        headerNextHost.appendChild(textBtn);
      }
      if (nextBtn && nextBtn.parentElement !== headerNextHost){
        headerNextHost.appendChild(nextBtn);
      }
    }

    function syncLeadBridgeHeaderPlacement(){
      closeTextMenu();
      if (isMobileScreen()){
        restoreMobileLeadControls();
        return;
      }

      moveDesktopLeadControls();
    }

    syncLeadBridgeHeaderPlacement();
    window.addEventListener('resize', syncLeadBridgeHeaderPlacement);
    window.addEventListener('orientationchange', syncLeadBridgeHeaderPlacement);

    function todayIsoDate(){
      return new Date().toISOString().slice(0, 10);
    }

    function noteLineDate(isoDate){
      const m = String(isoDate || '').match(/^(\d{4})-(\d{2})-(\d{2})$/);
      if (!m) return '00/00/0000';
      return `${m[2]}/${m[3]}/${m[1]}`;
    }

    function notePrefix(isoDate){
      return `[${noteLineDate(isoDate)}]`;
    }

    function stripExistingPrefix(line){
      return String(line || '')
        .replace(/^\s*\[[^\]]*\]\s*/, '')
        .replace(/^\s*---\s*$/, '')
        .trim();
    }

    function extractNoteBodyText(rawText){
      const normalizedLines = String(rawText || '')
        .split(/\r?\n/)
        .map(stripExistingPrefix)
        .filter(x => !!x);
      return normalizedLines.join('\n').trim();
    }

    function normalizeNoteBodyForDate(rawText, isoDate){
      const prefix = notePrefix(isoDate);
      const body = extractNoteBodyText(rawText);
      if (!body) return `${prefix} `;

      const lines = body.split(/\r?\n/);
      const first = lines[0] || '';
      const rest = lines.slice(1).join('\n');
      return rest ? `${prefix} ${first}\n${rest}` : `${prefix} ${first}`;
    }

    function lineStartIndex(text, pos){
      const p = Math.max(0, pos || 0);
      return text.lastIndexOf('\n', Math.max(0, p - 1)) + 1;
    }

    function lineEndIndex(text, pos){
      const p = Math.max(0, pos || 0);
      const idx = text.indexOf('\n', p);
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
      const value = textarea.value || '';
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
      const value = textarea.value || '';
      const start = textarea.selectionStart ?? 0;
      const end = textarea.selectionEnd ?? start;
      const lineStart = lineStartIndex(value, start);
      const protectedPrefixLen = currentLinePrefixLength(value, start);
      if (!protectedPrefixLen) return false;
      const min = lineStart + protectedPrefixLen;

      if (start !== end){
        return start < min;
      }

      if (key === 'Backspace') return start <= min;
      if (key === 'Delete') return start < min;
      if (key === 'Home') return true;
      return false;
    }

    function displayDate(isoDate){
      if (!isoDate) return '00-00-0000';
      const m = String(isoDate).match(/^(\d{4})-(\d{2})-(\d{2})$/);
      if (!m) return '00-00-0000';
      return `${m[2]}-${m[3]}-${m[1]}`;
    }

    function currentLeadContext(){
      const lead = resolveCurrentLead();
      const leadId = (lead?.leadId || '').trim();
      const leadName = `${lead?.firstName || ''} ${lead?.lastName || ''}`.trim() || 'Lead';
      return { leadId, leadName };
    }

    function encodeNoteKey(leadId, noteDate){
      return `${encodeURIComponent(leadId || '')}|${noteDate || ''}`;
    }

    function decodeNoteKey(raw){
      const value = (raw || '').trim();
      if (!value) return { leadId: '', noteDate: '' };
      const sep = value.indexOf('|');
      if (sep < 0) return { leadId: '', noteDate: value };
      return {
        leadId: decodeURIComponent(value.slice(0, sep)),
        noteDate: value.slice(sep + 1)
      };
    }

    function syncLeadField(){
      if (!noteLeadInput) return;
      const ctx = currentLeadContext();
      noteLeadInput.value = ctx.leadId ? ctx.leadName : 'No lead selected';
    }

    function setNoteStatus(message, bad){
      if (!noteStatusEl) return;
      noteStatusEl.textContent = message || '';
      noteStatusEl.classList.toggle('is-bad', !!bad);
    }

    async function loadNoteDates(leadIdValue){
      if (!noteDatesSelect) return [];
      const leadId = (leadIdValue || currentLeadContext().leadId || '').trim();
      if (!leadId){
        noteDatesSelect.innerHTML = '<option value="">Select lead + date</option>';
        noteDatesLoaded = false;
        return [];
      }
      try {
        const res = await fetch(`/WorkstationNotes/Dates?leadId=${encodeURIComponent(leadId)}`, withDialHeaders({ credentials: 'include' }));
        if (!res.ok) throw new Error('failed');
        const dates = await res.json();
        const list = Array.isArray(dates) ? dates : [];
        const current = noteDatesSelect.value || '';
        noteDatesSelect.innerHTML = ['<option value="">Select lead + date</option>']
          .concat(list.map(d => {
            const leadId = (d?.leadId || '').toString();
            const leadName = (d?.leadName || 'Lead').toString();
            const noteDate = (d?.noteDate || '').toString();
            const label = `${leadName} — ${displayDate(noteDate)}`;
            return `<option value="${encodeNoteKey(leadId, noteDate)}">${escapeHtml(label)}</option>`;
          }))
          .join('');
        if (current && Array.from(noteDatesSelect.options).some(o => o.value === current)){
          noteDatesSelect.value = current;
        }
        noteDatesLoaded = true;
        return list;
      } catch {
        setNoteStatus('Could not load saved dates', true);
        return [];
      }
    }

    async function loadNoteForDate(dateValue, leadIdValue){
      if (!noteDateInput || !noteWentWell || !noteCouldBetter) return;
      const date = (dateValue || noteDateInput.value || todayIsoDate()).trim();
      const fallbackLeadId = currentLeadContext().leadId;
      const leadId = (leadIdValue || fallbackLeadId || '').trim();
      if (!leadId){
        noteWentWell.value = '';
        noteCouldBetter.value = '';
        setNoteStatus('Select a lead first', true);
        return;
      }
      if (!date) return;
      noteDateInput.value = date;
      setNoteStatus('Loading...');
      try {
        const res = await fetch(`/WorkstationNotes/Entry?leadId=${encodeURIComponent(leadId)}&date=${encodeURIComponent(date)}`, withDialHeaders({ credentials: 'include' }));
        if (!res.ok) throw new Error('failed');
        const payload = await res.json();
        noteWentWell.value = normalizeNoteBodyForDate(payload?.wentWell || '', date);
        noteCouldBetter.value = normalizeNoteBodyForDate(payload?.couldBetter || '', date);
        const loadedLeadName = (payload?.leadName || currentLeadContext().leadName || 'Lead').toString();
        if (noteDatesSelect) noteDatesSelect.value = encodeNoteKey(leadId, date);
        setNoteStatus(`Loaded ${loadedLeadName} — ${displayDate(date)}`);
      } catch {
        setNoteStatus('Failed to load note', true);
      }
    }

    async function saveNoteForDate(){
      if (!noteDateInput || !noteWentWell || !noteCouldBetter) return;
      const date = (noteDateInput.value || '').trim();
      const ctx = currentLeadContext();
      if (!ctx.leadId){
        setNoteStatus('Select a lead first', true);
        return;
      }
      if (!date){
        setNoteStatus('Pick a date first', true);
        return;
      }
      setNoteStatus('Saving...');
      try {
        const normalizedWentWell = normalizeNoteBodyForDate(noteWentWell.value || '', date);
        const normalizedCouldBetter = normalizeNoteBodyForDate(noteCouldBetter.value || '', date);
        const wentWellBody = extractNoteBodyText(normalizedWentWell);
        const couldBetterBody = extractNoteBodyText(normalizedCouldBetter);

        noteWentWell.value = normalizedWentWell;
        noteCouldBetter.value = normalizedCouldBetter;

        const res = await fetch('/WorkstationNotes/Entry', withDialHeaders({
          method: 'POST',
          credentials: 'include',
          headers: {
            'Content-Type': 'application/json',
            'RequestVerificationToken': token
          },
          body: JSON.stringify({
            leadId: ctx.leadId,
            leadName: ctx.leadName,
            date,
            wentWell: wentWellBody ? normalizedWentWell : '',
            couldBetter: couldBetterBody ? normalizedCouldBetter : ''
          })
        }));
        if (!res.ok) throw new Error('failed');
        const payload = await res.json().catch(() => null);
        if (payload?.deleted){
          setNoteStatus(`Cleared ${ctx.leadName} — ${displayDate(date)}`);
        } else {
          setNoteStatus(`Saved ${ctx.leadName} — ${displayDate(date)}`);
        }
        await loadNoteDates(ctx.leadId);
        if (noteDatesSelect) noteDatesSelect.value = encodeNoteKey(ctx.leadId, date);
        if (payload?.deleted && noteDatesSelect && !Array.from(noteDatesSelect.options).some(o => o.value === encodeNoteKey(ctx.leadId, date))){
          noteDatesSelect.value = '';
        }
      } catch {
        setNoteStatus('Failed to save note', true);
      }
    }

    async function openNoteModal(){
      if (!noteOverlay) return;
      noteOverlay.hidden = false;
      document.body.classList.add('note-self-open');
      syncLeadField();
      const ctx = currentLeadContext();
      if (!ctx.leadId){
        setNoteStatus('Select a lead first', true);
        if (noteWentWell) noteWentWell.value = '';
        if (noteCouldBetter) noteCouldBetter.value = '';
        if (noteDatesSelect) noteDatesSelect.innerHTML = '<option value="">Select lead + date</option>';
        if (noteDateInput && !noteDateInput.value) noteDateInput.value = todayIsoDate();
        return;
      }
      if (noteDateInput && !noteDateInput.value) noteDateInput.value = todayIsoDate();
      const list = await loadNoteDates(ctx.leadId);
      if (Array.isArray(list) && list.length){
        const newestDate = (list[0]?.noteDate || '').toString();
        if (newestDate){
          if (noteDateInput) noteDateInput.value = newestDate;
          if (noteDatesSelect) noteDatesSelect.value = encodeNoteKey(ctx.leadId, newestDate);
          await loadNoteForDate(newestDate, ctx.leadId);
        } else {
          await loadNoteForDate(noteDateInput?.value || todayIsoDate(), ctx.leadId);
        }
      } else {
        await loadNoteForDate(noteDateInput?.value || todayIsoDate(), ctx.leadId);
      }
      noteWentWell?.focus();
    }

    function closeNoteModal(){
      if (!noteOverlay) return;
      noteOverlay.hidden = true;
      document.body.classList.remove('note-self-open');
    }

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

      // Handle mixed formats like "AZ - Arizona" or "Arizona (AZ)".
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

    // Ensure Open CRM always goes somewhere useful even before leads load.
    if (openCrmLink){
      openCrmLink.href = "/Leads";
      openCrmLink.classList.remove("disabled");
    }
    if (editClientBtn){
      editClientBtn.disabled = true;
      editClientBtn.dataset.href = '';
      editClientBtn.title = 'No lead selected';
    }

    function updateAgentWideDials(payload){
      const next = readAgentWideDialTotals(payload);
      if (next.today !== null) agentWideDials.today = next.today;
      if (next.week !== null) agentWideDials.week = next.week;
    }

    function renderAgentWideDialCounters(){
      const dDay = bridge.querySelector('[data-lb-dials-day] .lb-dials-count');
      const dWeek = bridge.querySelector('[data-lb-dials-week] .lb-dials-count');
      if (dDay) dDay.textContent = String(agentWideDials.today ?? 0);
      if (dWeek) dWeek.textContent = String(agentWideDials.week ?? 0);
    }

    function renderLeadCallCount(lead){
      if (!lead || !fields.calls) return;
      const currentLead = resolveCurrentLead();
      if (!currentLead || currentLead.leadId !== lead.leadId) return;
      fields.calls.textContent = `Calls: ${lead.callCount ?? 0}`;
    }

    function applyOptimisticCallIncrement(lead){
      if (!lead) return;
      const priorCallCount = Number(lead.callCount ?? 0);
      const priorLifetime = Number(lead.attemptsLifetime ?? priorCallCount);
      lead.callCount = priorCallCount + 1;
      lead.attemptsToday = Number(lead.attemptsToday ?? lead.dialsToday ?? 0) + 1;
      lead.attemptsThisWeek = Number(lead.attemptsThisWeek ?? lead.dialsWeek ?? 0) + 1;
      lead.attemptsThisMonth = Number(lead.attemptsThisMonth ?? 0) + 1;
      lead.attemptsThisYear = Number(lead.attemptsThisYear ?? 0) + 1;
      lead.attemptsLifetime = (Number.isFinite(priorLifetime) ? priorLifetime : priorCallCount) + 1;
      lead.dialsToday = lead.attemptsToday;
      lead.dialsWeek = lead.attemptsThisWeek;

      agentWideDials.today = Number(agentWideDials.today ?? 0) + 1;
      agentWideDials.week = Number(agentWideDials.week ?? 0) + 1;

      renderLeadCallCount(lead);
      renderAgentWideDialCounters();
    }

    async function refreshAgentWideDialCounters(){
      if (dialTotalsRefreshInFlight) return;
      dialTotalsRefreshInFlight = true;
      try {
        const qs = normalizedQueueKey ? `?bucket=${encodeURIComponent(normalizedQueueKey)}` : '';
        const res = await fetch(`/Leads/Leads${qs}`, withDialHeaders({
          credentials: 'include',
          cache: 'no-store'
        }));
        if (!res.ok) return;
        const data = await res.json().catch(() => []);
        updateAgentWideDials(Array.isArray(data) && data.length ? data[0] : null);
      } catch {}
      finally {
        dialTotalsRefreshInFlight = false;
        renderAgentWideDialCounters();
      }
    }

    function setStatusHtml(html, tone){
      if (!statusEl) return;
      statusEl.innerHTML = html;
      statusEl.classList.toggle('is-auth', tone === 'auth');
      statusEl.classList.toggle('is-bad', tone === 'bad');
    }

    function setStatusMessage(message, tone){
      setStatusHtml(`<span class="lb-status-note">${escapeHtml(message)}</span>`, tone);
    }

    function setOrigin(lead){
      if (!originEl) return;
      if (!lead){
        originEl.textContent = 'Origin: —';
        return;
      }
      const origin = leadOriginalLeadType(lead) || bucket || '';
      originEl.textContent = `Origin: ${bucketLabel(origin || 'Lead')}`;
    }


    function isLifeOrFinal(leadType){
      return leadType === 'LifeInsurance' || leadType === 'FinalExpense';
    }

    function applyLeadTypeFieldDisplay(lead){
      const leadType = leadOriginalLeadType(lead)
        || normalizedQueueKey
        || normalizeQueueKey(lead?.bucket || lead?.crmStage || bucket || '');
      const hideLender = isLifeOrFinal(leadType);

      if (lenderField){
        lenderField.style.display = hideLender ? 'none' : '';
      }
      if (lenderLabel){
        lenderLabel.textContent = 'Lender';
      }
      if (loanLabel){
        loanLabel.textContent = hideLender ? 'Requested' : 'Loan Amount';
      }
    }

    function leadStageKey(lead){
      return normalizedStageKey(lead?.bucket || lead?.crmStage || '');
    }

    function isDoNotCallLead(lead){
      const stage = leadStageKey(lead);
      return doNotCallStages.has(stage) || stage.toLowerCase() === 'donotcalllist';
    }

    function isCallBlockedForLead(lead){
      if (!lead) return false;
      const stage = leadStageKey(lead);
      if (isDoNotCallLead(lead)) return true;
      if (stage.toLowerCase() === 'followup') return true;
      if (stage.toLowerCase() === 'booked') return true;
      if (stage.toLowerCase() === 'policyplaced') return true;
      return noCallStages.has(stage);
    }

    function isTextBlockedForLead(lead){
      return isDoNotCallLead(lead);
    }

    function updateCommunicationAvailability(lead){
      if (!lead){
        if (callBtn){
          callBtn.disabled = true;
          callBtn.title = 'No lead selected';
        }
        if (textBtn){
          textBtn.disabled = true;
          textBtn.title = 'No lead selected';
        }
        return;
      }

      if (isDoNotCallLead(lead)){
        if (callBtn){
          callBtn.disabled = true;
          callBtn.title = 'This lead is in Do Not Call List. Calling is disabled in Workstation.';
        }
        if (textBtn){
          textBtn.disabled = true;
          textBtn.title = 'This lead is in Do Not Call List. Texting is disabled in Workstation.';
        }
        return;
      }

      if (callBtn){
        if (isCallBlockedForLead(lead)){
          callBtn.disabled = true;
          callBtn.title = 'Calling from Workstation is disabled for Booked, Follow Up, and Policy Placed leads.';
        } else {
          callBtn.disabled = false;
          callBtn.title = '';
        }
      }

      if (textBtn){
        textBtn.disabled = false;
        textBtn.title = '';
      }
    }

  function getStatusHtml(lead){
      if (!lead) return '<span class="lb-status-note">No leads found</span>';
      return `
        <span class="lb-status-line">
          <span class="lb-status-bucket">${escapeHtml(bucketLabel(lead.bucket || bucket))}</span>
        </span>
      `;
    }

    function clearConfirmTimer(){
      if (!confirmTimer) return;
      window.clearTimeout(confirmTimer);
      confirmTimer = 0;
    }

    function restoreActionButtons(){
      if (callBtn){
        callBtn.textContent = baseLabels.call;
        callBtn.removeAttribute('data-confirming');
      }
      if (textBtn){
        textBtn.textContent = baseLabels.text;
        textBtn.removeAttribute('data-confirming');
      }
    }

    function resetPendingAction(options){
      const preserveStatus = !!(options && options.preserveStatus);
      pendingAction = null;
      clearConfirmTimer();
      restoreActionButtons();
      if (!preserveStatus){
        setStatusHtml(getStatusHtml(resolveCurrentLead()));
      }
    }

    function ensureTextMenu(){
      if (textMenuEl) return textMenuEl;

      textMenuEl = document.createElement('div');
      textMenuEl.className = 'lb-text-menu';
      textMenuEl.hidden = true;
      textMenuEl.style.display = 'none';
      textMenuEl.dataset.open = '0';
      document.body.appendChild(textMenuEl);

      textMenuEl.addEventListener('click', async (event) => {
        const item = event.target.closest('[data-lb-text-template-index]');
        if (!item) return;
        event.preventDefault();

        const templateIndex = Number.parseInt(item.getAttribute('data-lb-text-template-index') || '', 10);
        const template = Number.isFinite(templateIndex) ? textScriptTemplates[templateIndex] : null;
        closeTextMenu();
        if (!template?.template) return;

        if (typeof window.LegendAgentProfileApi?.sendTextMessage === 'function'){
          await window.LegendAgentProfileApi.sendTextMessage(template.template);
          return;
        }

        await sendCustomTextMessage(template.template);
      });

      return textMenuEl;
    }

    function closeTextMenu(){
      if (!textMenuEl) return;
      textMenuEl.hidden = true;
      textMenuEl.style.display = 'none';
      textMenuEl.dataset.open = '0';
      textMenuEl.dataset.leadId = '';
    }

    function isTextMenuOpenForLead(leadId){
      return !!textMenuEl && textMenuEl.dataset.open === '1' && textMenuEl.dataset.leadId === String(leadId || '');
    }

    function openTextMenu(anchorEl, lead){
      if (!anchorEl || !lead) return;
      if (!textScriptTemplates.length){
        setStatusMessage('No text scripts available on this workstation view.', 'bad');
        return;
      }

      const menu = ensureTextMenu();
      menu.innerHTML = '';

      textScriptTemplates.forEach((template, index) => {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'lb-text-menu-item';
        button.setAttribute('data-lb-text-template-index', String(index));
        button.textContent = template.title;
        menu.appendChild(button);
      });

      const rect = anchorEl.getBoundingClientRect();
      const preferredWidth = Math.max(rect.width, 220);
      menu.style.minWidth = `${preferredWidth}px`;
      menu.style.visibility = 'hidden';
      menu.hidden = false;
      menu.style.display = 'flex';

      const menuWidth = menu.offsetWidth || preferredWidth;
      const menuHeight = menu.offsetHeight || 0;
      const left = Math.max(12, Math.min(window.innerWidth - menuWidth - 12, rect.left));
      const preferredTop = rect.bottom + 8;
      const top = preferredTop + menuHeight > window.innerHeight - 12
        ? Math.max(12, rect.top - menuHeight - 8)
        : preferredTop;

      menu.style.left = `${Math.round(left)}px`;
      menu.style.top = `${Math.round(top)}px`;
      menu.style.visibility = 'visible';
      menu.dataset.open = '1';
      menu.dataset.leadId = String(lead.leadId || '');
    }

    function findLeadById(leadId){
      if (!leadId) return null;
      return leads.find(x => x.leadId === leadId)
        || baseLeads.find(x => x.leadId === leadId)
        || null;
    }

    function hasActiveFilters(){
      return !!(
        (stateFilter?.value || '').trim()
        || (stageFilter?.value || '').trim()
        || (calledFilter?.value || '').trim()
        || (ageFilter?.value || '').trim()
        || (searchInput?.value || '').trim()
      );
    }

    function resolveCurrentLead(){
      if (hasActiveFilters() && leads[idx]){
        return leads[idx];
      }
      if (canonicalLeadId){
        return findLeadById(canonicalLeadId) || leads[idx] || null;
      }
      return leads[idx] || null;
    }

    function resolveRenderState(lead){
      if (hasActiveFilters()){
        if (!lead){
          return { index: 0, total: leads.length || 0 };
        }
        const filteredIdx = leads.findIndex(x => x.leadId === lead.leadId);
        if (filteredIdx >= 0) idx = filteredIdx;
        return {
          index: Math.max(0, filteredIdx >= 0 ? filteredIdx : idx),
          total: leads.length || 0
        };
      }

      // Position/total must be canonical across devices when server state is known.
      if (canonicalTotal > 0 || canonicalPosition > 0){
        const filteredIdx = lead ? leads.findIndex(x => x.leadId === lead.leadId) : -1;
        if (filteredIdx >= 0) idx = filteredIdx;
        return {
          index: Math.max(0, (canonicalPosition || 1) - 1),
          total: canonicalTotal || leads.length || 0
        };
      }

      if (!lead){
        return { index: 0, total: leads.length || 0 };
      }

      const filteredIdx = leads.findIndex(x => x.leadId === lead.leadId);
      if (filteredIdx >= 0){
        idx = filteredIdx;
        return { index: filteredIdx, total: leads.length };
      }

      return { index: 0, total: leads.length || 0 };
    }

    // Returns true if `incoming` version string is older than `current` (ticks-as-string).
    function versionIsStale(incoming, current){
      if (!incoming || !current) return false;
      if (incoming.length !== current.length) return incoming.length < current.length;
      return incoming < current;
    }

    // Apply server-authoritative filter values to DOM without triggering a push.
    function applyFiltersFromState(filterPayload){
      if (!filterPayload) return;
      const restoreSelectValue = (selectEl, value) => {
        if (!selectEl) return;
        const next = typeof value === 'string' ? value : '';
        const options = Array.from(selectEl.options || []);
        const hasOption = options.some(opt => opt.value === next);
        if (hasOption){
          selectEl.value = next;
          return;
        }

        // Backward compatibility: previously persisted filter state may store
        // abbreviations (e.g. "AZ"), while options now use full names.
        if (selectEl === stateFilter){
          const normalizedState = normalizeStateOption(next);
          if (normalizedState){
            const mapped = options.find(opt => normalizeStateOption(opt.value || opt.textContent || '') === normalizedState);
            if (mapped){
              selectEl.value = mapped.value;
              return;
            }
          }
        }

        // Backward compatibility: if older cache/server state stored exact age (e.g. "37"),
        // map it into the corresponding range option (e.g. "35-40").
        if (selectEl === ageFilter){
          const exactAge = Number.parseInt(next, 10);
          if (Number.isFinite(exactAge)){
            const mapped = options.find(opt => {
              const r = parseAgeRangeValue(opt.value);
              return r && exactAge >= r.min && exactAge < r.max;
            });
            if (mapped){
              selectEl.value = mapped.value;
              return;
            }
          }
        }

        selectEl.value = '';
      };
      restoreSelectValue(stateFilter, filterPayload.state);
      restoreSelectValue(stageFilter, filterPayload.stage);
      restoreSelectValue(calledFilter, filterPayload.calls);
      restoreSelectValue(ageFilter, filterPayload.age);
      if (searchInput) searchInput.value = filterPayload.search || '';
    }

    // Persist current filter state to server; broadcasts to all devices for same agent+bucket.
    async function pushFilters(){
      if (suppressFilterPush) return;
      const myToken = ++filterSyncToken;
      const filterPayload = {
        state: stateFilter?.value || '',
        stage: stageFilter?.value || '',
        calls: calledFilter?.value || '',
        age: ageFilter?.value || '',
        search: searchInput?.value || ''
      };
      try {
        const res = await fetch('/LeadBridge/SetFilters', withDialHeaders({
          method: 'POST',
          headers: {
            'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8',
            'RequestVerificationToken': token
          },
          credentials: 'include',
          body: `QueueKey=${encodeURIComponent(queueKey)}&FilterState=${encodeURIComponent(JSON.stringify(filterPayload))}&Version=${encodeURIComponent(activeStateVersion||'')}` 
        }));
        if (!res.ok || myToken !== filterSyncToken) return; // stale or superseded
        const payload = await res.json().catch(() => null);
        if (payload && myToken === filterSyncToken){
          const v = payload.version || payload.Version;
          if (v && !versionIsStale(v, activeStateVersion)) activeStateVersion = v;
        }
      } catch {}
    }

  function armPendingAction(action, lead, digits){
      const targetBtn = action === 'call' ? callBtn : textBtn;
      pendingAction = {
        action,
        leadId: lead.leadId,
        digits
      };

      restoreActionButtons();
      if (targetBtn){
        const label = action === 'call' ? baseLabels.call : baseLabels.text;
        const short = fmtPhone(digits) || digits;
        targetBtn.textContent = `${label} ${short ? short : ''} — tap again`;
        targetBtn.setAttribute('data-confirming', 'true');
      }

      setStatusHtml(`
        <button type="button" class="lb-auth-link" data-lb-authorize="${action}">${escapeHtml(fmtPhone(digits))}</button>
        <span class="lb-auth-note">Tap the blue number or tap the ${action === 'call' ? 'Call' : 'Text'} button again to proceed. Press Escape to cancel.</span>
      `, 'auth');

      clearConfirmTimer();
      confirmTimer = window.setTimeout(() => {
        if (!pendingAction || pendingAction.action !== action) return;
        resetPendingAction();
      }, 8000);
    }

    async function incrementCallForLead(lead, options = {}){
      const skipLocalFallback = !!options.skipLocalFallback;
      let payload = null;
      try {
        const res = await fetch('/Leads/IncrementCall', withDialHeaders({
          method: 'POST',
          headers: {
            'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8',
            'RequestVerificationToken': token
          },
          credentials: 'include',
          keepalive: true,
          body: `id=${encodeURIComponent(lead.leadId || '')}`
        }));
        if (res.ok){
          payload = await res.json().catch(() => ({}));
        }
      } catch {}

      const currentCallCount = Number(lead.callCount ?? 0);
      const currentToday = Number(lead.attemptsToday ?? lead.dialsToday ?? 0);
      const currentWeek = Number(lead.attemptsThisWeek ?? lead.dialsWeek ?? 0);
      const currentMonth = Number(lead.attemptsThisMonth ?? 0);
      const currentYear = Number(lead.attemptsThisYear ?? 0);
      const currentLifetime = Number(lead.attemptsLifetime ?? currentCallCount);

      const payloadCallCount = normalizeDialTotal(payload?.callCount);
      const payloadToday = normalizeDialTotal(payload?.attemptsToday);
      const payloadWeek = normalizeDialTotal(payload?.attemptsThisWeek);
      const payloadMonth = normalizeDialTotal(payload?.attemptsThisMonth);
      const payloadYear = normalizeDialTotal(payload?.attemptsThisYear);
      const payloadLifetime = normalizeDialTotal(payload?.attemptsLifetime);
      const payloadDialsToday = normalizeDialTotal(payload?.dialsToday);
      const payloadDialsWeek = normalizeDialTotal(payload?.dialsWeek);

      lead.callCount = payloadCallCount ?? (skipLocalFallback ? currentCallCount : currentCallCount + 1);
      lead.attemptsToday = payloadToday ?? (skipLocalFallback ? currentToday : currentToday + 1);
      lead.attemptsThisWeek = payloadWeek ?? (skipLocalFallback ? currentWeek : currentWeek + 1);
      lead.attemptsThisMonth = payloadMonth ?? (skipLocalFallback ? currentMonth : currentMonth + 1);
      lead.attemptsThisYear = payloadYear ?? (skipLocalFallback ? currentYear : currentYear + 1);
      lead.attemptsLifetime = payloadLifetime ?? (skipLocalFallback ? currentLifetime : currentLifetime + 1);
      lead.dialsToday = payloadDialsToday ?? lead.attemptsToday;
      lead.dialsWeek = payloadDialsWeek ?? lead.attemptsThisWeek;
      updateAgentWideDials(payload);
      renderLeadCallCount(lead);
      renderAgentWideDialCounters();
      if (window.liveSync && lead?.leadId){
        window.liveSync.sendCall(lead.leadId, Number(lead.callCount ?? 0) || 0);
        window.liveSync.sendUpdate({
          leadId: lead.leadId,
          attemptsToday: lead.attemptsToday ?? 0,
          attemptsThisWeek: lead.attemptsThisWeek ?? 0,
          attemptsThisMonth: lead.attemptsThisMonth ?? 0,
          attemptsThisYear: lead.attemptsThisYear ?? 0,
          attemptsLifetime: lead.attemptsLifetime ?? lead.callCount ?? 0
        });
      }
      return lead.callCount;
    }

    function applyLeadPayload(target, payload){
      if (!target || !payload) return;
      target.leadId = payload.leadId || target.leadId;
      target.firstName = payload.firstName ?? target.firstName;
      target.lastName = payload.lastName ?? target.lastName;
      target.email = payload.email ?? target.email;
      target.phone = payload.phone ?? target.phone;
      target.phone2 = payload.phone2 ?? target.phone2;
      target.dob = payload.dob ?? target.dob;
      target.gender = payload.gender ?? target.gender;
      target.addressLine = payload.addressLine ?? target.addressLine;
      target.city = payload.city ?? target.city;
      target.state = payload.state ?? target.state;
      target.county = payload.county ?? target.county;
      target.zipCode = payload.zipCode ?? target.zipCode;
      target.mortgageLender = payload.mortgageLender ?? target.mortgageLender;
      target.loanAmount = payload.loanAmount ?? target.loanAmount;
      target.age = payload.age ?? target.age;
      target.btc = payload.btc ?? target.btc;
      target.crmStatus = payload.crmStatus ?? target.crmStatus;
      target.crmNotes = payload.crmNotes ?? payload.agentNotes ?? payload.crmNextText ?? target.crmNotes;
      target.bucket = payload.bucket ?? payload.pipelineStage ?? target.bucket;
      target.crmStage = payload.pipelineStage ?? target.crmStage;
      target.updatedUtc = payload.updatedUtc ?? payload.crmLastTouch ?? target.updatedUtc;
      target.createdUtc = payload.createdUtc ?? payload.stageEnteredUtc ?? target.createdUtc;
      target.originalLeadType = payload.originalLeadType ?? target.originalLeadType;
      target.callCount = payload.callCount ?? payload.attemptsLifetime ?? target.callCount;
      target.attemptsToday = payload.attemptsToday ?? payload.dialsToday ?? target.attemptsToday;
      target.attemptsThisWeek = payload.attemptsThisWeek ?? payload.dialsWeek ?? target.attemptsThisWeek;
      target.attemptsThisMonth = payload.attemptsThisMonth ?? target.attemptsThisMonth;
      target.attemptsThisYear = payload.attemptsThisYear ?? target.attemptsThisYear;
      target.attemptsLifetime = payload.attemptsLifetime ?? target.attemptsLifetime ?? target.callCount;
      target.dialsToday = payload.dialsToday ?? target.attemptsToday;
      target.dialsWeek = payload.dialsWeek ?? target.attemptsThisWeek;
      normalizeLeadAgeFromDob(target);
      updateAgentWideDials(payload);
    }

    function broadcastLeadUpdate(lead, payload){
      if (!window.liveSync || !lead?.leadId) return;
      window.liveSync.sendUpdate({
        leadId: lead.leadId,
        pipelineStage: payload?.pipelineStage || payload?.bucket || lead.bucket || lead.crmStage || "",
        crmStatus: payload?.crmStatus || lead.crmStatus || "",
        crmNextDate: payload?.crmNextDate || "",
        crmNextText: payload?.crmNextText || lead.crmNotes || "",
        attemptsToday: lead.attemptsToday ?? lead.dialsToday ?? 0,
        attemptsThisWeek: lead.attemptsThisWeek ?? lead.dialsWeek ?? 0,
        attemptsThisMonth: lead.attemptsThisMonth ?? 0,
        attemptsThisYear: lead.attemptsThisYear ?? 0,
        attemptsLifetime: lead.attemptsLifetime ?? lead.callCount ?? 0,
        firstName: lead.firstName || "",
        lastName: lead.lastName || "",
        phone: lead.phone || "",
        email: lead.email || "",
        originalLeadType: lead.originalLeadType || ""
      });
    }

    async function launchTextMessageForLead(lead, rawMessage){
      const digits = ((lead?.phone || lead?.phone2 || '')).replace(/\D/g,'');
      if (!digits){
        setStatusMessage('No phone on file', 'bad');
        return false;
      }

      const msg = String(rawMessage || '').trim();
      if (!msg){
        setStatusMessage('No message to send', 'bad');
        return false;
      }

      let copied = false;
      try {
        await navigator.clipboard.writeText(msg);
        copied = true;
      } catch {}

      setStatusMessage(copied ? 'Text copied. Opening messages...' : 'Opening messages...');
      const smsHref = buildSmsLaunchHref(digits, msg);
      if (!smsHref){
        setStatusMessage('No phone on file', 'bad');
        return false;
      }
      window.location.href = smsHref;
      return true;
    }

    async function sendCustomTextMessage(rawMessage){
      const lead = resolveCurrentLead();
      if (!lead){
        setStatusMessage('No active lead available', 'bad');
        return false;
      }

      if (isTextBlockedForLead(lead)){
        setStatusMessage('Calling and texting are disabled in Workstation for Do Not Call List leads.', 'bad');
        return false;
      }

      let resolvedMessage = String(rawMessage || '');
      if (typeof window.LegendAgentProfileApi?.fillTextMessagePlaceholders === 'function'){
        resolvedMessage = window.LegendAgentProfileApi.fillTextMessagePlaceholders(resolvedMessage);
      } else if (typeof window.LegendAgentProfileApi?.fillScriptPlaceholders === 'function'){
        resolvedMessage = window.LegendAgentProfileApi.fillScriptPlaceholders(resolvedMessage);
      }

      return launchTextMessageForLead(lead, resolvedMessage);
    }

    async function authorizePendingAction(){
      if (!pendingAction) return;
      const lead = findLeadById(pendingAction.leadId);
      if (!lead) return;
      const digits = pendingAction.digits;
      const action = pendingAction.action;

      if (action === 'call' && isDoNotCallLead(lead)){
        resetPendingAction();
        setStatusMessage('Calling and texting are disabled in Workstation for Do Not Call List leads.', 'bad');
        return;
      }

      resetPendingAction({ preserveStatus: true });

      if (action === 'call'){
        await incrementCallForLead(lead);
        showCurrent();
        window.location.href = `tel:${digits}`;
        return;
      }

      const msg = buildTextMessage(lead, leadOriginalLeadType(lead) || bucket || lead.bucket);
      await launchTextMessageForLead(lead, msg);
    }

    function renderLead(lead, index, total){
      resetPendingAction({ preserveStatus: true });
      if (!lead){
        applyLeadTypeFieldDisplay(null);
        Object.values(fields).forEach(f => f && (f.textContent = '—'));
        if (fields.leadId) fields.leadId.textContent = '';
        if (fields.calls) fields.calls.textContent = 'Calls: —';
        setStatusMessage('No leads found');
        posEl.textContent = 'Lead 0 of 0';
        setOrigin(null);
        renderAgentWideDialCounters();
        updateCommunicationAvailability(null);
        return;
      }
      fields.name.textContent = `${lead.firstName || ''} ${lead.lastName || ''}`.trim() || '—';
      if (fields.leadId) fields.leadId.textContent = lead.leadId || '';
      if (fields.calls) fields.calls.textContent = `Calls: ${lead.callCount ?? 0}`;
      fields.address.textContent = lead.addressLine || '—';
      fields.city.textContent = lead.city || '—';
      fields.state.textContent = lead.state || '—';
      fields.county.textContent = lead.county || '—';
      fields.dob.textContent = formatDob(lead.dob);
      fields.gender.textContent = lead.gender || '—';
      applyLeadTypeFieldDisplay(lead);
      fields.lender.textContent = lead.mortgageLender || '—';
      fields.loan.textContent = lead.loanAmount || '—';
      fields.phone.textContent = fmtPhone(lead.phone) || '—';
      fields.phone2.textContent = fmtPhone(lead.phone2) || '—';
      fields.age.textContent = normalizeLeadAgeFromDob(lead)?.age || '—';
      fields.btc.textContent = lead.btc || '—';
      posEl.textContent = `Lead ${Math.min(index+1, total)} of ${total}`;
      setOrigin(lead);
      setStatusHtml(getStatusHtml(lead));
      updateAgentWideDials(lead);
      renderAgentWideDialCounters();
      updateCommunicationAvailability(lead);
      if (noteOverlay && !noteOverlay.hidden) syncLeadField();
    }

    async function fetchLeads(){
      setStatusMessage('Loading...');
      // Pull all leads so state filter can work across buckets.
      const res = await fetch(`/Leads/Leads`, withDialHeaders());
      if (!res.ok) throw new Error('Unable to load leads.');
      const data = await res.json();
      if (Array.isArray(data)){
        data.forEach(normalizeLeadAgeFromDob);
      }

      updateAgentWideDials(Array.isArray(data) && data.length ? data[0] : null);
      data.sort((a,b)=>{
        const ac = a.callCount ?? 0;
        const bc = b.callCount ?? 0;
        if (ac !== bc) return ac - bc; // fewest calls first (matches LeadBridge server canonical order)
        const ao = a.crmOrder ?? 0;
        const bo = b.crmOrder ?? 0;
        return bo - ao; // then highest CrmOrder
      });
      return data;
    }

    async function fetchStateOptions(){
      try {
        const res = await fetch('/Leads/StateOptions', withDialHeaders());
        if (!res.ok) return [];
        const data = await res.json();
        if (!Array.isArray(data)) return [];
        return data.map(normalizeStateOption).filter(Boolean);
      } catch {
        return [];
      }
    }

    async function fetchStatesFromLeadsPage(){
      try {
        const res = await fetch('/Leads', withDialHeaders());
        if (!res.ok) return [];
        const html = await res.text();
        if (!html) return [];
        const doc = new DOMParser().parseFromString(html, 'text/html');
        const stateSet = new Set();

        doc.querySelectorAll('[data-state]').forEach(el => {
          const normalized = normalizeStateOption(el.getAttribute('data-state'));
          if (normalized) stateSet.add(normalized);
        });

        doc.querySelectorAll('#stateFilter option').forEach(opt => {
          const normalized = normalizeStateOption(opt.value || opt.textContent || '');
          if (normalized) stateSet.add(normalized);
        });

        return Array.from(stateSet).sort();
      } catch {
        return [];
      }
    }

    async function fetchActiveState(){
      const reqToken = ++activeStateRequestToken;
      try {
        const res = await fetch(`/LeadBridge/Active?queueKey=${encodeURIComponent(queueKey)}`, withDialHeaders());
        if (!res.ok || reqToken !== activeStateRequestToken) return false;
        const payload = await res.json();
        if (reqToken !== activeStateRequestToken) return false;
        await applyRemoteState(payload, true);
        return true;
      } catch {
        return false;
      }
    }

    async function applyRemoteState(payload, fromSignal){
      if (!payload) return;
      const remoteQueueKey = normalizeQueueKey(payload.queueKey || payload.QueueKey || '');
      if (remoteQueueKey && normalizedQueueKey && remoteQueueKey !== normalizedQueueKey) return;

      // Version guard: ignore payloads older than the state we already have.
      const incomingVersion = payload.version || payload.Version;
      if (versionIsStale(incomingVersion, activeStateVersion)) return;
      if (incomingVersion) activeStateVersion = incomingVersion;

      const activeLeadId = payload.activeLeadId || payload.ActiveLeadId;
      const total = Number(payload.total || payload.Total || leads.length || 0);
      const deletedLeadId = payload.deletedLeadId || payload.DeletedLeadId;
      const pos = Number(payload.position || payload.Position || 0);
      const rawFilterState = payload.filterState || payload.FilterState;

      canonicalLeadId = activeLeadId || '';
      canonicalPosition = pos;
      canonicalTotal = total;

      // Apply server-authoritative filter state; suppress push-back to avoid feedback loops.
      if (rawFilterState){
        let parsedFilters = null;
        try { parsedFilters = typeof rawFilterState === 'string' ? JSON.parse(rawFilterState) : rawFilterState; } catch {}
        if (parsedFilters){
          serverFilterState = parsedFilters;
          suppressFilterPush = true;
          applyFiltersFromState(parsedFilters);
          suppressFilterPush = false;
          saveFilters(); // keep localStorage cache in sync with server state
        }
      }

      if (deletedLeadId){
        baseLeads = baseLeads.filter(x => x.leadId !== deletedLeadId);
      }

      // If canonical lead is not in baseLeads at all, do a full refetch once.
      if (activeLeadId && !findLeadById(activeLeadId)){
        try {
          baseLeads = await fetchLeads();
          populateFilters(baseLeads);
        } catch {}
      }

      // Re-filter (server filter values already applied above); prefer canonical lead.
      suppressPush = true;
      suppressFilterPush = true;
      applyFilters(activeLeadId || canonicalLeadId);
      suppressFilterPush = false;
      suppressPush = false;
      nextInFlight = false;
    }

    async function pushSelect(selectedLead){
      if (suppressPush) return;
      const lead = selectedLead || resolveCurrentLead();
      if (!lead) return;
      try {
        const res = await fetch('/LeadBridge/Select', withDialHeaders({
          method: 'POST',
          headers: {
            'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8',
            'RequestVerificationToken': token
          },
          credentials: 'include',
          body: `LeadId=${encodeURIComponent(lead.leadId||'')}&QueueKey=${encodeURIComponent(queueKey)}&Version=${encodeURIComponent(activeStateVersion||'')}`
        }));
        if (res.ok){
          const payload = await res.json();
          applyRemoteState(payload, false);
        }
      } catch {}
    }

    function populateFilters(list){
      const allItems = Array.isArray(list) ? list : [];
      const source = allItems;

      if (stateFilter){
        const stateSet = new Set();
        source.forEach(l => {
          [l?.state, l?.State, l?.crmState, l?.sState].forEach(v => {
            const normalized = normalizeStateOption(v);
            if (normalized) stateSet.add(normalized);
          });
        });
        (serverStateOptions || []).forEach(v => {
          const normalized = normalizeStateOption(v);
          if (normalized) stateSet.add(normalized);
        });
        const states = Array.from(stateSet).sort();
        if (states.length){
          stateFilter.innerHTML = ['<option value=\"\" selected>State</option>']
            .concat(states.map(s => `<option value=\"${s}\">${s}</option>`))
            .join('');
        } else if (!stateFilter.querySelector('option[value=\"\"]')) {
          stateFilter.insertAdjacentHTML('afterbegin','<option value=\"\" selected>State</option>');
        }
      }
      if (stageFilter){
        // Always expose the full stage list so agents can jump across buckets.
        const stageOptions = allStages.slice();
        stageFilter.innerHTML = ['<option value="">Stage</option>']
          .concat(stageOptions.map(s => `<option value="${s}">${bucketLabel(s)}</option>`))
          .join('');
      }
      if (ageFilter){
        const ageSet = new Set();
        source.forEach(l => {
          const age = resolveLeadAgeNumber(l);
          if (age !== null) ageSet.add(age);
        });
        const ages = Array.from(ageSet).sort((a, b) => a - b);
        const ranges = [];
        if (ages.length){
          const minAge = ages[0];
          const maxAge = ages[ages.length - 1];
          const start = Math.floor(minAge / 5) * 5;
          const endExclusive = Math.ceil((maxAge + 1) / 5) * 5;
          for (let from = start; from < endExclusive; from += 5){
            const to = from + 5;
            ranges.push(`${from}-${to}`);
          }
        }
        ageFilter.innerHTML = ['<option value="">Age</option>']
          .concat(ranges.map(r => `<option value="${r}">${r}</option>`))
          .join('');
      }
    }

    function saveFilters(){
      try{
        const payload = {
          state: stateFilter?.value || '',
          stage: stageFilter?.value || '',
          calls: calledFilter?.value || '',
          age: ageFilter?.value || '',
          search: searchInput?.value || ''
        };
        localStorage.setItem(filterStoreKey, JSON.stringify(payload));
      }catch{}
    }

    function restoreFilters(){
      // localStorage is cache-only; authoritative filters come from server state.
      if (serverFilterState) return;
      let payload = null;
      try{
        payload = JSON.parse(localStorage.getItem(filterStoreKey) || 'null');
      }catch{}
      if (!payload) return;

      const restoreSelectValue = (selectEl, value) => {
        if (!selectEl) return;
        const next = typeof value === 'string' ? value : '';
        const options = Array.from(selectEl.options || []);
        const hasOption = options.some(opt => opt.value === next);
        if (hasOption){
          selectEl.value = next;
          return;
        }

        if (selectEl === ageFilter){
          const exactAge = Number.parseInt(next, 10);
          if (Number.isFinite(exactAge)){
            const mapped = options.find(opt => {
              const r = parseAgeRangeValue(opt.value);
              return r && exactAge >= r.min && exactAge < r.max;
            });
            if (mapped){
              selectEl.value = mapped.value;
              return;
            }
          }
        }

        selectEl.value = '';
      };

      restoreSelectValue(stateFilter, payload.state);
      restoreSelectValue(stageFilter, payload.stage);
      restoreSelectValue(calledFilter, payload.calls);
      restoreSelectValue(ageFilter, payload.age);
      if (searchInput) searchInput.value = payload.search || '';
    }

    function clearSavedFilters(){
      try{ localStorage.removeItem(filterStoreKey); }catch{}
    }

    function applyFilters(preferredLeadId){
      saveFilters();
      preferredLeadId = typeof preferredLeadId === 'string' ? preferredLeadId : '';
      // Start with all leads; bucket filtering is controlled by explicit dropdowns.
      const defaultBucket = normalizedQueueKey || bucket || "";
      let working = baseLeads.slice();

      const stateSel = normalizeStateOption(stateFilter?.value || '');
      const stageSel = stageFilter?.value || '';
      const callsRaw = (calledFilter?.value || '').trim();
      const callsSel = callsRaw === '' ? null : parseInt(callsRaw, 10);
      const ageRaw = (ageFilter?.value || '').trim();
      const q = (searchInput?.value || '').trim();
      const searchActive = !!q;
      const enforceDefaultBucket = false;
      const filtersActive = !!(stateSel || stageSel || callsRaw || ageRaw || q);

      const filtered = working.filter(l => {
        const leadTypeValue = leadOriginalLeadType(l);
        if (enforceDefaultBucket && defaultBucket && leadTypeValue !== defaultBucket) return false;

        // Search intentionally bypasses all other filter dropdowns so callbacks
        // can always be found by name, phone, or email regardless of UI state.
        if (searchActive) return leadMatchesBridgeSearch(l, q);

        const leadState = normalizeStateOption(l?.state ?? l?.State ?? l?.crmState ?? l?.sState ?? '');
        if (stateSel && leadState !== stateSel) return false;
        if (stageSel && !matchesStageSelection(l, stageSel)) return false;
        const calls = Number(l.callCount || 0);
        if (callsSel === null){
          // no filtering
        }else if (callsSel === 10){
          if (calls < 10) return false;
        }else{
          if (calls !== callsSel) return false;
        }
        if (ageRaw && !leadMatchesAgeRange(l, ageRaw)) return false;
        return true;
      });

      leads = filtered;
      const preferredIds = [preferredLeadId, canonicalLeadId, resolveCurrentLead()?.leadId].filter(Boolean);
      const preferredIndex = preferredIds.reduce((found, leadId) => {
        if (found >= 0) return found;
        return filtered.findIndex(l => l.leadId === leadId);
      }, -1);
      if (filtersActive){
        // When searching/filtering, always land on a visible matching card.
        if (preferredIndex >= 0){
          idx = preferredIndex;
        } else {
          idx = filtered.length ? 0 : 0;
        }
      } else if (preferredIndex >= 0){
        idx = preferredIndex;
      } else {
        idx = 0;
      }
      const canonicalLead = canonicalLeadId ? findLeadById(canonicalLeadId) : null;
      const filteredLead = leads[idx] || null;
      const leadToRender = filtersActive ? filteredLead : (canonicalLead || filteredLead);
      showCurrent({ lead: leadToRender || undefined });
      if (!filtered.length){
        setStatusMessage('No leads match the current filters', 'bad');
      }
    }

    function showCurrent(options = {}){
      const lead = options.lead || resolveCurrentLead();
      const renderState = resolveRenderState(lead);
      renderLead(lead, renderState.index, renderState.total);
      window.__currentLead = lead || null;
      try {
        window.dispatchEvent(new CustomEvent('leadbridge:currentLead', {
          detail: {
            queueKey,
            lead: lead || null,
            position: renderState.index + 1,
            total: renderState.total
          }
        }));
      } catch {}
      if (openCrmLink){
        if (lead){
          const searchKey = (lead.phone || lead.email || `${lead.firstName||''} ${lead.lastName||''}`).trim();
          openCrmLink.href = `/Leads${searchKey ? `?search=${encodeURIComponent(searchKey)}` : ""}`;
        }else{
          openCrmLink.href = "/Leads";
        }
        openCrmLink.classList.remove('disabled');
      }
      if (editClientBtn){
        const leadId = (lead?.leadId || '').trim();
        if (leadId){
          editClientBtn.disabled = false;
          editClientBtn.dataset.href = `/Leads?open=${encodeURIComponent(leadId)}`;
          editClientBtn.title = 'Open this lead in CRM Quick View';
        } else {
          editClientBtn.disabled = true;
          editClientBtn.dataset.href = '';
          editClientBtn.title = 'No lead selected';
        }
      }
      if (options.pushSelection){
        pushSelect(lead);
      }
    }

    async function selectLeadById(leadId){
      const requestedLeadId = (leadId || '').trim();
      if (!requestedLeadId) return false;

      if (!baseLeads.length){
        try {
          baseLeads = await fetchLeads();
          populateFilters(baseLeads);
        } catch {
          return false;
        }
      }

      const targetLead = findLeadById(requestedLeadId);
      if (!targetLead) return false;

      canonicalLeadId = targetLead.leadId || requestedLeadId;

      if (stateFilter) stateFilter.value = '';
      if (stageFilter) stageFilter.value = '';
      if (calledFilter) calledFilter.value = '';
      if (ageFilter) ageFilter.value = '';
      if (searchInput) searchInput.value = '';

      await pushFilters();
      applyFilters(canonicalLeadId);

      const nextLead = findLeadById(canonicalLeadId) || targetLead;
      const nextIndex = leads.findIndex(x => x.leadId === canonicalLeadId);
      if (nextIndex >= 0) idx = nextIndex;
      showCurrent({ lead: nextLead, pushSelection: true });
      return true;
    }

    registerLeadBridgeController({
      bridge,
      queueKey: normalizedQueueKey || '',
      rawQueueKey: queueKey || '',
      selectLeadById,
      getCurrentLead: () => resolveCurrentLead(),
      sendTextMessage: (message) => sendCustomTextMessage(message)
    });

    async function deleteCurrentLead(){
      const lead = resolveCurrentLead();
      if (!lead) return;
      const confirmed = window.confirm(`Delete lead ${lead.firstName || ''} ${lead.lastName || ''}? This cannot be undone.`);
      if (!confirmed) return;
      setStatusMessage('Deleting...');
      try {
        const res = await fetch('/LeadBridge/Delete', withDialHeaders({
          method: 'POST',
          headers: {
            'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8',
            'RequestVerificationToken': token
          },
          credentials: 'include',
          body: `LeadId=${encodeURIComponent(lead.leadId || '')}&QueueKey=${encodeURIComponent(queueKey)}&Version=${encodeURIComponent(activeStateVersion||'')}`
        }));
        if (!res.ok) throw new Error('Delete failed');
        const payload = await res.json();
        setStatusMessage('Lead deleted');
        applyRemoteState(payload, false);
      } catch {
        setStatusMessage('Failed to delete lead', 'bad');
      }
    }

    async function syncNext(options = {}){
      const retrying = !!options.retrying;
      if (nextInFlight) return;
      nextInFlight = true;
      try {
        if (hasActiveFilters()){
          if (!leads.length){
            setStatusMessage('No leads match the current filters', 'bad');
            return;
          }

          const currentLead = resolveCurrentLead() || leads[idx] || null;
          let currentIdx = currentLead ? leads.findIndex(x => x.leadId === currentLead.leadId) : -1;
          if (currentIdx < 0) currentIdx = Math.min(idx, Math.max(leads.length - 1, 0));
          const nextIdx = (currentIdx + 1) % leads.length;
          const nextLead = leads[nextIdx] || null;
          if (!nextLead){
            setStatusMessage('No active lead available', 'bad');
            return;
          }

          idx = nextIdx;
          canonicalLeadId = nextLead.leadId || canonicalLeadId;
          canonicalPosition = nextIdx + 1;
          canonicalTotal = leads.length;
          showCurrent({ lead: nextLead });
          await pushSelect(nextLead);
          return;
        }

        if (!resolveCurrentLead()){
          const hydrated = await fetchActiveState();
          if (!hydrated || !resolveCurrentLead()){
            setStatusMessage('No active lead available', 'bad');
            return;
          }
        }

        if (!activeStateVersion){
          const hydrated = await fetchActiveState();
          if (!hydrated || !activeStateVersion){
            setStatusMessage('Unable to sync active lead', 'bad');
            return;
          }

          nextInFlight = false;
          return syncNext({ retrying: true });
        }

        const currentLeadBeforeRequest = resolveCurrentLead();
        if (!currentLeadBeforeRequest){
          setStatusMessage('No active lead available', 'bad');
          return;
        }

        const beforeLeadId = currentLeadBeforeRequest.leadId || '';
        const beforeVersion = activeStateVersion;
        const res = await fetch('/LeadBridge/Next', withDialHeaders({
          method: 'POST',
          headers: {
            'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8',
            'RequestVerificationToken': token
          },
          credentials: 'include',
          body: `QueueKey=${encodeURIComponent(queueKey)}&Version=${encodeURIComponent(activeStateVersion||'')}`
        }));
        if (res.ok){
          const payload = await res.json();
          await applyRemoteState(payload, false);

          const afterLead = resolveCurrentLead();
          const afterLeadId = afterLead?.leadId || '';
          const afterVersion = payload?.version || payload?.Version || activeStateVersion;

          const stateReconciledOnly =
            !!beforeVersion &&
            !!afterVersion &&
            beforeVersion !== afterVersion &&
            beforeLeadId &&
            afterLeadId === beforeLeadId;

          const leadDidNotAdvance =
            beforeLeadId &&
            afterLeadId &&
            beforeLeadId === afterLeadId;

          if (!retrying && (stateReconciledOnly || leadDidNotAdvance)){
            nextInFlight = false;
            return syncNext({ retrying: true });
          }

          if (beforeLeadId && afterLeadId && beforeLeadId !== afterLeadId){
            setStatusMessage('Advanced to next lead');
          }
        }
        else {
          const text = await res.text();
          console.error('LeadBridge Next failed', res.status, text);
          setStatusMessage(`Next failed (${res.status})`, 'bad');
        }
      } catch (err) {
        console.error('LeadBridge Next exception', err);
        setStatusMessage('Next failed', 'bad');
      } finally {
        nextInFlight = false;
      }
    }

    nextBtn?.addEventListener('click', () => {
      resetPendingAction({ preserveStatus: true });
      syncNext();
    });

    callBtn?.addEventListener('click', () => {
      const lead = resolveCurrentLead();
      if (!lead) return;
      if (isDoNotCallLead(lead)){
        setStatusMessage('Calling and texting are disabled in Workstation for Do Not Call List leads.', 'bad');
        return;
      }
      if (isCallBlockedForLead(lead)){
        setStatusMessage('Calls are disabled in Workstation for Booked, Follow Up, and Policy Placed leads.', 'bad');
        return;
      }
      const digits = (lead?.phone || '').replace(/\D/g,'');
      if (!digits){
        setStatusMessage('No phone on file', 'bad');
        return;
      }
      // One-click call: launch dialer immediately; track dials without blocking.
      resetPendingAction({ preserveStatus: true });
      applyOptimisticCallIncrement(lead);
      setStatusMessage(`Dialing ${fmtPhone(digits) || digits}...`);
      // Keep UI instant via optimistic update, then reconcile with server totals.
      incrementCallForLead(lead, { skipLocalFallback: true }).catch(() => {});
      const launchDelayMs = isMobileScreen() ? 0 : 140;
      setTimeout(() => { window.location.href = `tel:${digits}`; }, launchDelayMs);
    });

    textBtn?.addEventListener('click', () => {
      const lead = resolveCurrentLead();
      if (!lead) return;
      if (isTextBlockedForLead(lead)){
        setStatusMessage('Calling and texting are disabled in Workstation for Do Not Call List leads.', 'bad');
        return;
      }
      const digits = String(lead?.phone || lead?.phone2 || '').replace(/\D/g,'');
      if (!digits){
        setStatusMessage('No phone on file', 'bad');
        return;
      }

      resetPendingAction({ preserveStatus: true });

      if (isTextMenuOpenForLead(lead.leadId)){
        closeTextMenu();
        return;
      }

      openTextMenu(textBtn, lead);
    });

    deleteBtn?.addEventListener('click', (event) => {
      event.preventDefault();
      deleteCurrentLead();
    });

    statusEl?.addEventListener('click', async (event) => {
      const authLink = event.target.closest('[data-lb-authorize]');
      if (!authLink) return;
      event.preventDefault();
      await authorizePendingAction();
    });

    outcomeButtons.forEach(btn => btn.addEventListener('click', async () => {
      resetPendingAction({ preserveStatus: true });
      const lead = resolveCurrentLead();
      if (!lead) return;
      const outcomeCode = (btn.dataset.outcome || '').trim();
      if (!outcomeCode) return;
      if (btn.dataset.saving === '1') return;
      btn.dataset.saving = '1';
      btn.disabled = true;
      const currentFilteredIdx = leads.findIndex(x => x.leadId === lead.leadId);
      const nextLeadId = currentFilteredIdx >= 0 && leads.length > 1
        ? leads[(currentFilteredIdx + 1) % leads.length]?.leadId
        : "";
      setStatusMessage('Saving...');
      try {
        const res = await fetch('/Leads/ApplyOutcome', withDialHeaders({
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            'RequestVerificationToken': token
          },
          credentials: 'include',
          body: JSON.stringify({ clientUserId: lead.leadId, outcomeCode: outcomeCode, customNote: null })
        }));

        const responseText = await res.text();
        let result = {};
        let parsedJson = false;
        if (responseText){
          try {
            result = JSON.parse(responseText);
            parsedJson = true;
          } catch {}
        }

        if (!res.ok){
          const detail = (result?.title || result?.message || result?.error || responseText || '').toString().trim();
          setStatusMessage(detail ? `Failed to save outcome (${res.status}): ${detail}` : `Failed to save outcome (${res.status})`, 'bad');
          return;
        }

        if (responseText && !parsedJson){
          setStatusMessage('Failed to save outcome: unexpected server response. Refresh and retry.', 'bad');
          return;
        }

        const payload = result?.payload || {};
        const safePayload = {
          pipelineStage: outcomeCode,
          bucket: outcomeCode,
          updatedUtc: new Date().toISOString(),
          ...payload
        };

        updateAgentWideDials(payload || result);
        applyLeadPayload(lead, safePayload);
        broadcastLeadUpdate(lead, safePayload);

        canonicalLeadId = nextLeadId || canonicalLeadId;
        applyFilters(nextLeadId);
        if (nextLeadId){
          const nextLead = findLeadById(nextLeadId);
          if (nextLead) pushSelect(nextLead);
        } else {
          setStatusMessage('Outcome saved');
        }
      } catch (err) {
        console.error('Lead outcome save failed', err);
        setStatusMessage('Failed to save outcome', 'bad');
      } finally {
        btn.disabled = false;
        delete btn.dataset.saving;
      }
    }));

    document.addEventListener('click', (event) => {
      const target = event.target;
      if (!(target instanceof Node)) return;
      if (textMenuEl && !textMenuEl.contains(target) && !textBtn?.contains(target)){
        closeTextMenu();
      }
      if (!pendingAction) return;
      if (callBtn?.contains(target) || textBtn?.contains(target) || statusEl?.contains(target) || textMenuEl?.contains(target)) return;
      resetPendingAction();
    }, true);

    document.addEventListener('keydown', (event) => {
      if (event.key !== 'Escape') return;
      if (noteOverlay && !noteOverlay.hidden) {
        closeNoteModal();
        return;
      }
      closeTextMenu();
      if (!pendingAction) return;
      resetPendingAction();
    });

    stateFilter?.addEventListener('change', async () => {
      applyFilters();
      await pushFilters();
    });
    stageFilter?.addEventListener('change', async () => {
      applyFilters();
      await pushFilters();
    });
    calledFilter?.addEventListener('change', async () => {
      applyFilters();
      await pushFilters();
    });
    ageFilter?.addEventListener('change', async () => {
      applyFilters();
      await pushFilters();
    });
    searchInput?.addEventListener('input', () => {
      applyFilters();
      if (searchPushTimer) window.clearTimeout(searchPushTimer);
      searchPushTimer = window.setTimeout(() => {
        pushFilters();
      }, 250);
    });
    clearBtn?.addEventListener('click', () => {
      if (stateFilter) stateFilter.value = '';
      if (stageFilter) stageFilter.value = '';
      if (calledFilter) calledFilter.value = '';
      if (ageFilter) ageFilter.value = '';
      if (searchInput) searchInput.value = '';
      clearSavedFilters();
      applyFilters();
      pushFilters();
      setStatusMessage('Filters cleared');
    });

    editClientBtn?.addEventListener('click', () => {
      if (editClientBtn.disabled) return;
      const href = (editClientBtn.dataset.href || '').trim();
      if (!href) return;
      window.open(href, '_blank', 'noopener');
    });

    noteOpenBtn?.addEventListener('click', async () => {
      await openNoteModal();
    });
    noteCloseBtn?.addEventListener('click', () => {
      closeNoteModal();
    });
    noteOverlay?.addEventListener('click', (event) => {
      if (event.target !== noteOverlay) return;
      closeNoteModal();
    });
    noteDateInput?.addEventListener('change', async () => {
      await loadNoteForDate(noteDateInput.value, currentLeadContext().leadId);
    });
    noteDatesSelect?.addEventListener('change', async () => {
      const selected = decodeNoteKey(noteDatesSelect.value || '');
      if (!selected.noteDate) return;
      if (noteDateInput) noteDateInput.value = selected.noteDate;
      await loadNoteForDate(selected.noteDate, selected.leadId || currentLeadContext().leadId);
    });
    noteSaveBtn?.addEventListener('click', async () => {
      await saveNoteForDate();
    });

    [noteWentWell, noteCouldBetter].forEach(textarea => {
      if (!textarea) return;

      textarea.addEventListener('focus', () => {
        const date = (noteDateInput?.value || todayIsoDate()).trim();
        if (!textarea.value.trim()) textarea.value = `${notePrefix(date)} `;
      });

      textarea.addEventListener('keydown', (event) => {
        if (isEditingInsidePrefix(textarea, event.key)){
          event.preventDefault();
          const value = textarea.value || '';
          const start = textarea.selectionStart ?? 0;
          const lineStart = lineStartIndex(value, start);
          const protectedPrefixLen = currentLinePrefixLength(value, start);
          const min = lineStart + protectedPrefixLen;
          textarea.setSelectionRange(min, min);
          return;
        }
      });

      textarea.addEventListener('click', () => {
        enforceCaretAfterPrefix(textarea);
      });

      textarea.addEventListener('keyup', () => {
        enforceCaretAfterPrefix(textarea);
      });

      textarea.addEventListener('input', () => {
        enforceCaretAfterPrefix(textarea);
      });

      textarea.addEventListener('blur', () => {
        const date = (noteDateInput?.value || todayIsoDate()).trim();
        textarea.value = normalizeNoteBodyForDate(textarea.value || '', date);
        if (!textarea.value.trim()) textarea.value = `${notePrefix(date)} `;
      });
    });
    // Initialize SignalR and active state sync
    if (signalRAvailable){
      try {
        const connection = new signalR.HubConnectionBuilder()
          .withUrl('/leadbridgehub')
          .withAutomaticReconnect()
          .build();

        connection.on('LeadChanged', payload => applyRemoteState(payload, true));
        connection.start().then(fetchActiveState).catch(()=>{});
      } catch {}
    }

    try {
      baseLeads = await fetchLeads();
      leads = baseLeads.slice();
      const [stateOptionsFromApi, stateOptionsFromPage] = await Promise.all([
        fetchStateOptions(),
        fetchStatesFromLeadsPage()
      ]);
      serverStateOptions = Array.from(new Set([
        ...(stateOptionsFromApi || []),
        ...(stateOptionsFromPage || [])
      ].map(normalizeStateOption).filter(Boolean))).sort();
      populateFilters(baseLeads);
      idx = 0;
      const hydrated = await fetchActiveState();
      if (!hydrated || !serverFilterState){
        // Fallback: server unreachable OR server had no saved filter state for this session.
        // restoreFilters() is a no-op when serverFilterState is already set (set inside applyRemoteState).
        restoreFilters();
        applyFilters();
      }

      window.setInterval(refreshAgentWideDialCounters, 5 * 60 * 1000);
      document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'visible'){
          refreshAgentWideDialCounters().catch(() => {});
        }
      });
    } catch {
      setStatusMessage('Failed to load leads', 'bad');
    }
  });
})();
