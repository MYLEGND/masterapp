(() => {
  'use strict';

  const publishedData = document.getElementById('legend-cms-published-document');
  const renderInput = window.LEGEND_PUBLIC_CMS_RENDER_INPUT || (publishedData ? JSON.parse(publishedData.textContent) : null);
  const context = window.LEGEND_PUBLIC_CMS_CONTEXT || (renderInput?.business ? {
    siteKey: 'business',
    apiBase: renderInput.runtime?.apiBase || '',
    businessId: renderInput.business.id,
    trackingAsset: renderInput.runtime?.trackingAsset || '/legend-public-tracking.js',
    metaSignalAsset: renderInput.runtime?.metaSignalAsset || '/legend-public-meta-signal-intelligence.js'
  } : null);
  if (!context || !context.siteKey || typeof context.apiBase !== 'string') return;

  // Protect deliberately uses an empty base for its same-origin CMS authority.
  const API_BASE = (context.apiBase.trim() || location.origin).replace(/\/$/, '');
  let SITE_KEY = String(context.siteKey).toLowerCase();
  const AGENT_SLUG = context.agentSlug || '';
  let BUSINESS_ID = context.businessId || '';
  const params = new URLSearchParams(location.search);
  const requestedPage = SITE_KEY === 'business' && params.has('legendEdit') ? params.get('cmsPage') : null;
  const customPage = requestedPage && /^\/(?:[a-z0-9_-]+\/?)*$/.test(requestedPage) ? requestedPage.replace(/\/$/, '') || '/' : null;
  const pageKey = (customPage ? customPage.slice(1).replace(/\//g, '-') || 'home' : null) || renderInput?.pageKey || document.body?.dataset?.pageKey
    || location.pathname.replace(/^\/+|\/+$/g, '').replace(/[^a-z0-9]+/gi, '-')?.toLowerCase()
    || 'home';

  const editorTicket = params.get('legendEdit') || '';
  const editorMode = !!editorTicket;
  const originalTitle = document.title || '';
  const originalDescription = document.querySelector('meta[name="description"]')?.content || '';
  const initialFaviconLink = document.querySelector('link[rel~="icon"]');
  const originalFaviconHref = initialFaviconLink?.getAttribute('href') || (SITE_KEY === 'protect' ? '/images/favicon/legend-favicon.svg' : '/favicon.svg');
  const originalFaviconType = initialFaviconLink?.getAttribute('type') || '';
  const defaultBreakpoints = () => [
    { key: 'mobile', label: 'Mobile', minWidth: 0, maxWidth: 767, isSystem: true },
    { key: 'tablet', label: 'Tablet', minWidth: 768, maxWidth: 1199, isSystem: true },
    { key: 'desktop', label: 'Desktop', minWidth: 1200, maxWidth: null, isSystem: true }
  ];
  let documentState = { version: 2, faviconImageDataUrl: null, breakpoints: defaultBreakpoints(), elements: {}, sectionOrder: {}, extras: [], reusableComponents: {}, collections: {}, theme: {}, pages: {} };
  let signalCatalog = null;
  let ctaCatalog = [];
  let managementPayload = null;
  let pendingAiProposal = null;
  let collaborationReplyTo = null;
  let collectionData = new Map();
  let dynamicCollectionItem = renderInput?.dynamicItem || null;
  let selected = null;
  let selectedSection = null;
  let editorPreview = null;
  let editorBreakpointKey = 'base';
  let selectionFrame = null;
  let gridOverlay = null;
  let directGesture = null;
  let inlineEditNode = null;
  let inlineEditCheckpointed = false;
  let dirty = false;
  let autoSaveTimer = null;
  const pendingDeletedKeys = new Set();

  function pageElementDeletionKey(id) { return `page:${currentPageRoute()}|element:${id}`; }
  function pageExtraDeletionKey(id) { return `page:${currentPageRoute()}|extra:${id}`; }
  function markDeleted(key) { if (key) pendingDeletedKeys.add(key); }
  const originals = new WeakMap();
  const scaledElements = new Map();
  const animationRuntime = new WeakMap();
  const styleProperties = ['textAlign', 'fontSize', 'width', 'maxWidth', 'height', 'position', 'left', 'top', 'overflow', 'paddingTop', 'paddingBottom', 'objectPosition', 'color', 'backgroundColor', 'backgroundImage', 'fontFamily', 'fontWeight', 'lineHeight', 'letterSpacing', 'paddingLeft', 'paddingRight', 'borderRadius', 'objectFit', 'gridColumn', 'minWidth', 'overflowWrap', 'display', 'flexDirection', 'gap', 'gridTemplateColumns', 'alignItems', 'justifyContent', 'flexWrap'];

  function rememberOriginal(el) {
    if (!originals.has(el)) originals.set(el, {
      text: el.textContent, markup: el.innerHTML, src: el.getAttribute('src'), href: el.getAttribute('href'), alt: el.getAttribute('alt'), hidden: el.hidden,
      style: Object.fromEntries(styleProperties.map(key => [key, el.style[key] || '']))
    });
    return originals.get(el);
  }

  function positiveNumber(value) { return typeof value === 'number' && Number.isFinite(value) && value > 0; }
  function spacingNumber(value) { return typeof value === 'number' && Number.isFinite(value) && value >= 0; }

  function refreshScale(el, scale) {
    if (renderInput?.server) return;
    el.style.fontSize = rememberOriginal(el).style.fontSize;
    const base = parseFloat(getComputedStyle(el).fontSize);
    if (Number.isFinite(base)) el.style.fontSize = `${base * scale}px`;
  }

  function refreshScaledElements() {
    scaledElements.forEach((scale, el) => refreshScale(el, scale));
    if (selected) syncEditorControls();
  }

  const lockedSelector = [
    '[data-cms-locked="true"]',
    'script',
    'style',
    'noscript',
    'input',
    'select',
    'textarea',
    '.legend-cms-editor'
  ].join(',');

  const editableTextTags = new Set(['H1','H2','H3','H4','H5','P','LI','BUTTON','LABEL','SMALL','STRONG','SPAN']);
  const editableInteractiveTags = new Set(['A']);
  const sectionCandidates = 'main > section, main > .section, main > .page-hero, main > .cta, main > .legal-page-wrap, main > .quote-page, main > .container-narrow, main > .training-page';

  function normalizeBreakpoints(input) {
    const values = defaultBreakpoints();
    const seen = new Set(values.map(value => value.key));
    for (const item of Array.isArray(input) ? input : []) {
      if (!item || item.isSystem || typeof item.key !== 'string' || seen.has(item.key) || values.length >= 8) continue;
      const minWidth = Number(item.minWidth);
      const maxWidth = item.maxWidth == null ? null : Number(item.maxWidth);
      if (!Number.isFinite(minWidth) || minWidth < 0 || (maxWidth != null && (!Number.isFinite(maxWidth) || maxWidth < minWidth))) continue;
      seen.add(item.key);
      values.push({ key:item.key, label:typeof item.label === 'string' && item.label.trim() ? item.label.trim() : item.key, minWidth, maxWidth, isSystem:false });
    }
    return values;
  }

  function normalizeDocument(input) {
    const pages = {};
    const sourcePages = input?.pages && typeof input.pages === 'object' ? input.pages : {};
    // Read legacy template keys, but only write canonical route keys. Canonical
    // content wins a collision; unrelated legacy content is retained.
    for (const [key, value] of Object.entries(sourcePages).sort(([a], [b]) => Number(a.startsWith('/')) - Number(b.startsWith('/')))) {
      if (!value || typeof value !== 'object') continue;
      const route = key.startsWith('/') ? key : key === 'home' ? '/' : `/${key}`;
      const previous = pages[route];
      pages[route] = previous ? {
        ...previous, ...value,
        elements: { ...previous.elements, ...value.elements },
        sectionOrder: { ...previous.sectionOrder, ...value.sectionOrder },
        extras: [...new Map([...(previous.extras || []), ...(value.extras || [])].map(extra => [extra.id, extra])).values()]
      } : value;
    }
    return {
      version: 2,
      faviconImageDataUrl: typeof input?.faviconImageDataUrl === 'string' ? input.faviconImageDataUrl : null,
      breakpoints: normalizeBreakpoints(input?.breakpoints),
      elements: input?.elements && typeof input.elements === 'object' ? input.elements : {},
      sectionOrder: input?.sectionOrder && typeof input.sectionOrder === 'object' ? input.sectionOrder : {},
      extras: Array.isArray(input?.extras) ? input.extras : [],
      reusableComponents: input?.reusableComponents && typeof input.reusableComponents === 'object' ? input.reusableComponents : {},
      collections: input?.collections && typeof input.collections === 'object' ? input.collections : {},
      theme: input?.theme && typeof input.theme === 'object' ? input.theme : {},
      pages
    };
  }

  function normalizePageRoute(value) {
    if (typeof value !== 'string') return null;
    let route=value.trim().toLowerCase();
    if (!route.startsWith('/')) route='/'+route;
    route=route.replace(/\/+$/,'') || '/';
    if (!/^\/(?:[a-z0-9_-]+\/?)*$/.test(route) || route.length>160 || route.includes('..')) return null;
    return route;
  }

  function currentPageRoute() {
    const pathname = customPage || location.pathname.replace(/^\/business-preview/, '').replace(/\/$/, '') || '/';
    return normalizePageRoute(pathname) || '/';
  }

  function pageState() {
    documentState.pages ||= {};
    const routeKey = currentPageRoute();
    if (!documentState.pages[routeKey]) {
      const belongs = id => id.startsWith(`${pageKey}.`) || id.startsWith(`section:${pageKey}.`);
      const elements = Object.fromEntries(Object.entries(documentState.elements).filter(([id]) => belongs(id)));
      const sectionOrder = Object.fromEntries(Object.entries(documentState.sectionOrder).filter(([id]) => belongs(id)));
      const extras = documentState.extras.filter(extra => belongs(extra.sectionId || ''));
      documentState.pages[routeKey] = { elements, sectionOrder, extras, navigation: { showInNavigation: true, order: 0, isDeleted: false } };
      Object.keys(elements).forEach(id => delete documentState.elements[id]);
      Object.keys(sectionOrder).forEach(id => delete documentState.sectionOrder[id]);
      documentState.extras = documentState.extras.filter(extra => !extras.includes(extra));
    }
    const page = documentState.pages[routeKey]; page.elements ||= {}; page.sectionOrder ||= {}; page.extras ||= []; page.navigation ||= { showInNavigation: true, order: 0, isDeleted: false };
    return page;
  }

  function safeId(value) {
    return String(value || '')
      .toLowerCase()
      .replace(/[^a-z0-9_.:-]+/g, '-')
      .replace(/^-+|-+$/g, '')
      .slice(0, 160);
  }

  function canEditElement(el) {
    if (!(el instanceof HTMLElement)) return false;
    if (el.closest('.legend-cms-editor')) return false;
    if (el.matches(lockedSelector) || el.closest('[data-cms-locked="true"]')) return false;
    if (['IMG','VIDEO','DIV','ARTICLE','HEADER','FOOTER'].includes(el.tagName)) return true;
    if (editableTextTags.has(el.tagName)) return true;
    if (editableInteractiveTags.has(el.tagName)) return true;
    return false;
  }

  function isDirectCanvasSelectable(el) {
    if (!(el instanceof HTMLElement)) return false;
    if (el.dataset.cmsSection || el.dataset.cmsExtraId) return true;
    return !['DIV','ARTICLE','HEADER','FOOTER'].includes(el.tagName);
  }

  function editorSelectionTarget(node) {
    if (!(node instanceof HTMLElement)) return null;
    if (node.tagName === 'IMG') return node.closest('[data-cms-editable="true"]');
    return node.closest('a[data-cms-editable="true"]') || node.closest('[data-cms-editable="true"]');
  }

  function prepareDom() {
    const roots = [
      document.querySelector('main'),
      document.querySelector('.nav'),
      document.querySelector('.site-footer')
    ].filter(Boolean);

    const sections = [...document.querySelectorAll(sectionCandidates), ...document.querySelectorAll('.site-header,.site-footer')];
    sections.forEach((section, index) => {
      if (!section.dataset.cmsSection) {
        section.dataset.cmsSection = section.matches('.site-header') ? `${pageKey}.header` : section.matches('.site-footer') ? `${pageKey}.footer` : `${pageKey}.section.${index + 1}`;
      }
      section.dataset.cmsId = `section:${section.dataset.cmsSection}`;
      section.dataset.cmsEditable = 'true';
      rememberOriginal(section);
    });

    let counter = 0, addedCounter = 0;
    roots.forEach(root => {
      root.querySelectorAll('h1,h2,h3,h4,h5,p,li,a,button,label,small,strong,span,img,video,div,article,header,footer').forEach(el => {
        if (!canEditElement(el)) return;
        if (!['IMG','VIDEO','A','DIV','ARTICLE','HEADER','FOOTER'].includes(el.tagName) && el.children.length > 0) return;
        if (!el.dataset.cmsId) {
          const legacy = !el.closest('.brand,.brand-wordmark') && !['VIDEO','DIV','ARTICLE','HEADER','FOOTER'].includes(el.tagName) && (el.tagName === 'IMG' || el.children.length === 0);
          const index = legacy ? ++counter : `node${++addedCounter}`;
          const href = el.getAttribute('href');
          const route = href ? new URL(href, location.origin).pathname : '';
          const semantic = SITE_KEY !== 'business' && legacy ? el.dataset.cta || href || el.textContent || el.tagName : el.dataset.businessField || (el.hasAttribute?.('data-business-name') ? 'business-name' : '') || el.dataset.businessRoute || el.dataset.cta || route || (['DIV','ARTICLE','HEADER','FOOTER'].includes(el.tagName) ? el.className || el.tagName : '') || el.textContent || el.tagName;
          el.dataset.cmsId = `${pageKey}.${safeId(el.tagName)}.${safeId(semantic).slice(0,50) || index}.${index}`;
        }
        rememberOriginal(el);
        if (isDirectCanvasSelectable(el)) el.dataset.cmsEditable = 'true';
        else delete el.dataset.cmsEditable;
        if ((el.tagName === 'A' || el.tagName === 'BUTTON') && !el.dataset.cmsAction) {
          el.dataset.cmsAction = el.dataset.cta || el.getAttribute('href') || 'action';
        }
      });
    });
    document.querySelectorAll('main form, main input:not([type="hidden"]):not([type="password"]), main select, main textarea').forEach((node, index) => {
      if (node.closest('[data-cms-locked="true"]')) return;
      node.dataset.cmsId ||= `signal:${pageKey}.${safeId(node.tagName)}.${safeId(node.id || node.name || 'field')}.${index}`;
      node.dataset.cmsSignalOnly = 'true'; node.dataset.cmsEditable = 'true';
      rememberOriginal(node);
    });
  }

  function selectedSignalElementId() {
    if (!selected) return null;
    return selected.dataset.cmsExtraId ? `extra:${selected.dataset.cmsExtraId}` : selected.dataset.cmsId || null;
  }

  function signalDiagnosticHost(bindingId) {
    return document.querySelector(`[data-signal-diagnostics="${CSS.escape(bindingId)}"]`);
  }

  function renderSignalDiagnosticMessage(bindingId, text, tone = 'info') {
    const host = signalDiagnosticHost(bindingId);
    if (!host) return;
    host.replaceChildren();
    const message = document.createElement('p');
    message.className = `legend-cms-signal-diagnostic legend-cms-signal-${tone}`;
    message.textContent = text;
    host.appendChild(message);
  }

  async function ensureSavedForSignalInspection(bindingId) {
    if (!dirty) return true;
    renderSignalDiagnosticMessage(bindingId, 'Saving the current draft before inspecting this mapping…');
    const saved = await save(false);
    if (!saved || dirty) {
      renderSignalDiagnosticMessage(bindingId, 'Save the current draft before testing this mapping.', 'error');
      return false;
    }
    return true;
  }

  async function runSignalDryRun(binding) {
    const elementId = selectedSignalElementId();
    if (!binding?.id || !elementId) return;
    if (!await ensureSavedForSignalInspection(binding.id)) return;
    renderSignalDiagnosticMessage(binding.id, 'Running private dry-run validation…');
    try {
      const response = await fetch(`${API_BASE}/api/website-content/manage/signals/test`, {
        method: 'POST',
        headers: { 'Content-Type':'application/json' },
        body: JSON.stringify({
          ticket: editorTicket,
          expectedRevision: revision,
          pagePath: currentPageRoute(),
          elementId,
          bindingId: binding.id
        })
      });
      const payload = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(payload.message || payload.error || `Signal dry-run failed (${response.status})`);
      if (payload.source !== 'website_signal_private_dry_run' || payload.dryRun !== true ||
          payload.persisted !== false || payload.metaDispatched !== false)
        throw new Error('Signal dry-run response was invalid.');
      const stages = payload.stages || {};
      const destination = payload.destination || {};
      const details = [
        'PRIVATE TEST · no analytics or Meta event sent',
        `Mapping: ${stages.mappingValidated ? 'valid' : 'invalid'}`,
        `Browser trigger: ${stages.browserTriggerSupported ? 'supported' : 'server-only'}`,
        `Analytics ingest: ${stages.browserAnalyticsWouldBeAccepted ? 'would accept' : 'not browser-eligible'}`,
        `Browser Pixel: ${stages.browserPixelWouldInvoke ? 'would invoke' : 'would not invoke'}`,
        `Server outcome required: ${stages.serverOutcomeRequired ? 'yes' : 'no'}`,
        `Destination: Pixel ${destination.browserPixelConfigured ? 'ready' : 'not configured'}, CAPI ${destination.serverCapiConfigured ? 'ready' : 'not configured'}`
      ].join(' · ');
      renderSignalDiagnosticMessage(binding.id, details, 'ok');
    } catch (error) {
      renderSignalDiagnosticMessage(binding.id, error?.message || 'Unable to run private signal test.', 'error');
    }
  }

  async function loadSignalHealth(binding) {
    const elementId = selectedSignalElementId();
    if (!binding?.id || !elementId) return;
    if (!await ensureSavedForSignalInspection(binding.id)) return;
    renderSignalDiagnosticMessage(binding.id, 'Loading destination health and published delivery evidence…');
    try {
      const url = new URL(`${API_BASE}/api/website-content/manage/signals/health`);
      url.searchParams.set('ticket', editorTicket);
      url.searchParams.set('pagePath', currentPageRoute());
      url.searchParams.set('elementId', elementId);
      url.searchParams.set('bindingId', binding.id);
      const response = await fetch(url, { cache:'no-store' });
      const payload = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(payload.message || payload.error || `Signal health failed (${response.status})`);
      if (payload.source !== 'website_signal_existing_authorities')
        throw new Error('Signal health response was invalid.');

      const host = signalDiagnosticHost(binding.id);
      if (!host) return;
      host.replaceChildren();
      const destination = payload.destination || {};
      const mapping = payload.binding || {};
      const summary = document.createElement('p');
      summary.className = 'legend-cms-signal-diagnostic legend-cms-signal-ok';
      summary.textContent = [
        `Destination owner: ${destination.ownerType || 'none'}`,
        `Pixel: ${destination.browserPixelConfigured ? 'configured' : 'not configured'}`,
        `CAPI: ${destination.serverCapiConfigured ? 'configured' : 'not configured'}`,
        `Consent/matching: ${mapping.matchingConsent || 'not requested'}`,
        `Published version: ${payload.publishedVersionId || 'not published'}`
      ].join(' · ');
      host.appendChild(summary);

      const evidence = [
        ...(payload.analytics || []).map(row => `Analytics accepted · ${row.eventType} · ${row.receivedUtc || ''}`),
        ...(payload.meta || []).map(row => {
          const dispatch = row.dispatch || {};
          const server = dispatch.sent ? 'server sent' : dispatch.status ? `server ${dispatch.status}` : 'no server dispatch';
          return `Meta signal · ${row.eventName} · browser ${row.metaBrowserSent ? 'invoked' : 'not invoked'} · ${server}`;
        })
      ];
      if (!evidence.length) {
        const empty = document.createElement('p');
        empty.textContent = 'No published delivery evidence exists yet for this exact binding/version.';
        host.appendChild(empty);
      } else {
        for (const value of evidence.slice(0, 12)) {
          const row = document.createElement('div');
          row.className = 'legend-cms-signal-history';
          row.textContent = value;
          host.appendChild(row);
        }
      }
    } catch (error) {
      renderSignalDiagnosticMessage(binding.id, error?.message || 'Unable to load signal health.', 'error');
    }
  }

  function renderSignalControls() {
    const host = document.getElementById('legend-cms-signal-controls');
    if (!host) return;
    host.replaceChildren();
    const paragraph = text => { const node = document.createElement('p'); node.textContent = text; host.appendChild(node); };
    if (!selected) { paragraph('Select a button, form, field, or section on the page.'); return; }
    if (!signalCatalog) { paragraph('The event catalog could not be loaded. Reopen the editor to try again.'); return; }
    const type = selected.tagName;
    const triggers = ['viewed'];
    if (['A','BUTTON'].includes(type)) triggers.push('click');
    if (type === 'FORM') triggers.push('form_started','submit_attempt','submission_saved');
    if (['INPUT','SELECT','TEXTAREA'].includes(type)) triggers.push('field_started','validation_failed');
    if (type === 'INPUT' && selected.type === 'tel') triggers.push('field_completed');
    if (selected.dataset.cmsSection) triggers.push('scroll_threshold');
    const candidates = signalCatalog.events.filter(option => option.triggers.some(trigger => triggers.includes(trigger)));
    const overrides = selectedOverride();
    const bindings = overrides?.signals || [];
    paragraph(bindings.length ? `${bindings.length} interaction mapping${bindings.length === 1 ? '' : 's'}` : 'No signal. This element has no configured marketing event.');
    if (!signalCatalog.runtimeEnabled) paragraph('Delivery is not activated for this release. You can prepare and save mappings.');
    const managedActionKey = selected.dataset.websiteActionKey;
    const selectedHref = selected.getAttribute?.('href');
    const managedAction = (managedActionKey && availableCtaOptions().find(option => option.key === managedActionKey))
      || (selectedHref && availableCtaOptions().find(option => option.href === selectedHref))
      || null;
    if (type === 'FORM' && selected.matches?.('[data-website-inquiry]')) {
      const automaticTitle = document.createElement('strong'); automaticTitle.textContent = 'Automatic form analytics + Meta';
      host.appendChild(automaticTitle);
      const automaticHelp = document.createElement('p');
      automaticHelp.textContent = 'No mapping is required. The shared Protect Website runtime automatically tracks the canonical inquiry lifecycle, and the backend owns the confirmed Lead outcome.';
      host.appendChild(automaticHelp);
      const automatic = document.createElement('div'); automatic.className = 'legend-cms-signal-presets';
      const names = ['LeadFormStart','ContactInputStarted','PhoneFieldCompleted','RequiredContactFieldsCompleted','SubmitAttempt','Lead'];
      for (const name of names) {
        const option = signalCatalog.events.find(value => value.name === name);
        if (!option) continue;
        const row = document.createElement('div');
        row.textContent = `${name} · automatic · ${option.requiresServerOutcome ? 'verified server outcome' : option.metaEligible ? 'Meta + analytics when configured' : 'analytics'}`;
        automatic.appendChild(row);
      }
      host.appendChild(automatic);
    } else if (managedAction) {
      const automaticTitle = document.createElement('strong'); automaticTitle.textContent = 'Automatic button analytics + Meta';
      host.appendChild(automaticTitle);
      const automaticHelp = document.createElement('p');
      automaticHelp.textContent = `${managedAction.label || managedAction.key} is already wired by the shared action contract: ${managedAction.analyticsEventName || 'cta_click'}${managedAction.metaIntentEventName ? ' + ' + managedAction.metaIntentEventName : ''}. No manual mapping is required.`;
      host.appendChild(automaticHelp);
    }
    const addSelect = (labelText, values, value, action) => {
      const label = document.createElement('label'); label.className = 'legend-cms-group'; label.textContent = labelText;
      const select = document.createElement('select');
      for (const [key, text] of values) { const option = document.createElement('option'); option.value = key; option.textContent = text; select.appendChild(option); }
      select.value = value; select.addEventListener('change', () => { checkpoint(); action(select.value); markDirty(); renderSignalControls(); });
      label.appendChild(select); host.appendChild(label); return select;
    };
    for (const binding of bindings) {
      const definition = signalCatalog.events.find(x => x.name === binding.eventName);
      addSelect('Send', [['off','Do not send'],['analytics','Analytics only'], ...(definition?.metaEligible ? [['meta','Meta + analytics']] : [])], binding.deliveryMode, value => binding.deliveryMode = value);
      const available = candidates.flatMap(option => option.triggers.filter(trigger => triggers.includes(trigger)).map(trigger => [option.name + ':' + trigger, `${option.name} · ${trigger.replaceAll('_',' ')}`]));
      addSelect('Event and trigger', available, binding.eventName + ':' + binding.trigger, value => {
        const [name, trigger] = value.split(':'); binding.eventName = name; binding.trigger = trigger; binding.matchingFields = [];
        if (!signalCatalog.events.find(x => x.name === name)?.metaEligible && binding.deliveryMode === 'meta') binding.deliveryMode = 'analytics';
      });
      const label = document.createElement('label'), once = document.createElement('input'); once.type = 'checkbox'; once.checked = binding.oncePerSession;
      once.addEventListener('change', () => { checkpoint(); binding.oncePerSession = once.checked; markDirty(); }); label.append(once, document.createTextNode(' Once per session')); host.appendChild(label);
      if (definition?.requiresServerOutcome) {
        paragraph('Sent only after the backend confirms this outcome. Customer matching requires advertising consent.');
        for (const field of signalCatalog.matchingFields) {
          const label = document.createElement('label'), input = document.createElement('input'); input.type = 'checkbox'; input.checked = binding.matchingFields?.includes(field);
          input.addEventListener('change', () => { checkpoint(); const fields = new Set(binding.matchingFields || []); input.checked ? fields.add(field) : fields.delete(field); binding.matchingFields = [...fields]; markDirty(); });
          label.append(input, document.createTextNode(' Match approved ' + field)); host.appendChild(label);
        }
      }
      const diagnostics = document.createElement('div');
      diagnostics.className = 'legend-cms-signal-diagnostics';
      diagnostics.dataset.signalDiagnostics = binding.id;
      const diagnosticIntro = document.createElement('p');
      diagnosticIntro.textContent = 'Destination health and delivery evidence have not been checked for this mapping.';
      diagnostics.appendChild(diagnosticIntro);
      const diagnosticActions = document.createElement('div'); diagnosticActions.className = 'legend-cms-row';
      const testButton = document.createElement('button'); testButton.type = 'button'; testButton.textContent = 'Run private test';
      testButton.dataset.signalTest = binding.id;
      testButton.addEventListener('click', () => void runSignalDryRun(binding));
      const healthButton = document.createElement('button'); healthButton.type = 'button'; healthButton.textContent = 'Refresh delivery history';
      healthButton.dataset.signalHealth = binding.id;
      healthButton.addEventListener('click', () => void loadSignalHealth(binding));
      diagnosticActions.append(testButton, healthButton);
      diagnostics.appendChild(diagnosticActions);
      host.appendChild(diagnostics);
      const remove = document.createElement('button'); remove.type = 'button'; remove.textContent = 'Remove mapping';
      remove.addEventListener('click', () => { checkpoint(); overrides.signals = bindings.filter(x => x.id !== binding.id); markDirty(); renderSignalControls(); }); host.appendChild(remove);
    }
    const add = document.createElement('button'); add.type = 'button'; add.textContent = 'Add advanced custom mapping';
    const automaticContract = (type === 'FORM' && selected.matches?.('[data-website-inquiry]')) || !!managedAction;
    add.hidden = automaticContract;
    add.disabled = automaticContract || bindings.length >= 8 || !candidates.length;
    add.addEventListener('click', () => {
      const option = candidates.flatMap(x => x.triggers.filter(t => triggers.includes(t) && !bindings.some(b => b.trigger === t)).map(t => ({ event: x, trigger: t })))[0];
      if (!option) { paragraph('All supported triggers for this element are already mapped.'); return; }
      checkpoint(); overrides.signals ||= []; overrides.signals.push({ id: crypto.randomUUID().replaceAll('-',''), eventName: option.event.name, trigger: option.trigger, deliveryMode: 'off', oncePerSession: true, matchingFields: [] });
      markDirty(); renderSignalControls();
    }); host.appendChild(add);
  }

  function applyTheme(theme) {
    const root = document.documentElement;
    const map = {
      navy: '--web-navy',
      navyDeep: '--web-navy-deep',
      gold: '--web-gold',
      goldStrong: '--web-gold-strong',
      surface: '--web-surface', text: '--web-ink', muted: '--web-muted', fontFamily: '--web-font'
    };
    // The template's gradient end follows the selected primary color; it must
    // not retain an uneditable royal-blue stop when the palette changes.
    if (theme?.navy) root.style.setProperty('--web-navy-royal', theme.navy);
    else root.style.removeProperty('--web-navy-royal');
    Object.entries(map).forEach(([key, cssVar]) => {
      if (theme?.[key]) root.style.setProperty(cssVar, theme[key]);
      else root.style.removeProperty(cssVar);
    });
  }

  function responsiveViewportWidth() {
    const previewWidth = editorMode && editorPreview?.clientWidth;
    if (Number.isFinite(previewWidth) && previewWidth > 0) return previewWidth;
    const windowWidth = Number(window.innerWidth);
    if (Number.isFinite(windowWidth) && windowWidth > 0) return windowWidth;
    return Number(document.documentElement?.clientWidth) || 1200;
  }

  function activeBreakpoint(width = responsiveViewportWidth()) {
    if (editorMode) {
      if (editorBreakpointKey === 'base') return null;
      if ((documentState.breakpoints || []).some(value => value.key === editorBreakpointKey)) return editorBreakpointKey;
    }
    const candidates = (documentState.breakpoints || []).filter(value => {
      const min = Number(value.minWidth) || 0;
      const max = value.maxWidth == null ? Infinity : Number(value.maxWidth);
      return width >= min && width <= max;
    });
    candidates.sort((a,b) => {
      const system = Number(!!a.isSystem) - Number(!!b.isSystem);
      if (system !== 0) return system;
      const aSpan = (a.maxWidth == null ? 100000 : Number(a.maxWidth)) - Number(a.minWidth || 0);
      const bSpan = (b.maxWidth == null ? 100000 : Number(b.maxWidth)) - Number(b.minWidth || 0);
      return aSpan - bSpan;
    });
    return candidates[0]?.key || null;
  }

  function effectiveStyle(override) {
    const base = override?.style && typeof override.style === 'object' ? override.style : {};
    const key = activeBreakpoint();
    const responsive = key && override?.breakpointStyles && typeof override.breakpointStyles[key] === 'object' ? override.breakpointStyles[key] : null;
    return responsive ? { ...base, ...responsive } : base;
  }

  function effectiveLayout(override) {
    const base = override?.layout && typeof override.layout === 'object' ? override.layout : { mode:'free' };
    const key = activeBreakpoint();
    const responsive = key && override?.breakpointLayouts && typeof override.breakpointLayouts[key] === 'object' ? override.breakpointLayouts[key] : null;
    return responsive ? { ...base, ...responsive } : base;
  }

  function applyLayout(el, layout) {
    if (!el) return;
    const mode = layout?.mode || 'free';
    if (mode === 'stack' || mode === 'flex') {
      el.style.display = 'flex';
      el.style.flexDirection = mode === 'stack' ? 'column' : (layout.direction === 'row' ? 'row' : 'column');
      if (layout.gapPx != null) el.style.gap = `${Number(layout.gapPx)}px`;
      if (layout.alignItems) el.style.alignItems = layout.alignItems;
      if (layout.justifyContent) el.style.justifyContent = layout.justifyContent;
      if (mode === 'flex' && layout.wrap) el.style.flexWrap = layout.wrap;
    } else if (mode === 'grid') {
      el.style.display = 'grid';
      el.style.gridTemplateColumns = `repeat(${Math.max(1, Math.min(12, Number(layout.columns) || 12))},minmax(0,1fr))`;
      if (layout.gapPx != null) el.style.gap = `${Number(layout.gapPx)}px`;
      if (layout.alignItems) el.style.alignItems = layout.alignItems;
      if (layout.justifyContent) el.style.justifyContent = layout.justifyContent;
    }
  }
  function editingStyle(override, create = false) {
    if (!override) return null;
    if (editorBreakpointKey === 'base') {
      if (create) override.style ||= {};
      return override.style || {};
    }
    if (create) { override.breakpointStyles ||= {}; override.breakpointStyles[editorBreakpointKey] ||= {}; }
    return override.breakpointStyles?.[editorBreakpointKey] || {};
  }

  function editingLayout(override, create = false) {
    if (!override) return null;
    if (editorBreakpointKey === 'base') {
      if (create) override.layout ||= { mode:'free', direction:'column' };
      return override.layout || { mode:'free', direction:'column' };
    }
    if (create) { override.breakpointLayouts ||= {}; override.breakpointLayouts[editorBreakpointKey] ||= {}; }
    return override.breakpointLayouts?.[editorBreakpointKey] || {};
  }

  function forEachDocumentOverride(action) {
    Object.values(documentState.elements || {}).forEach(action);
    (documentState.extras || []).forEach(action);
    Object.values(documentState.pages || {}).forEach(page => {
      Object.values(page?.elements || {}).forEach(action);
      (page?.extras || []).forEach(action);
    });
    Object.values(documentState.reusableComponents || {}).forEach(component => {
      Object.values(component?.elements || {}).forEach(action);
      (component?.extras || []).forEach(action);
    });
  }

  function syncBreakpointControls() {
    const select = document.getElementById('legend-cms-breakpoint');
    const remove = document.getElementById('legend-cms-breakpoint-remove');
    if (!select) return;
    select.replaceChildren();
    const base = document.createElement('option'); base.value='base'; base.textContent='Base · all sizes'; select.appendChild(base);
    for (const breakpoint of documentState.breakpoints || []) {
      const option = document.createElement('option'); option.value=breakpoint.key; option.textContent=`${breakpoint.label || breakpoint.key} · ${breakpoint.minWidth}px${breakpoint.maxWidth == null ? '+' : '–'+breakpoint.maxWidth+'px'}`; select.appendChild(option);
    }
    if (![...select.options].some(option => option.value === editorBreakpointKey)) editorBreakpointKey='base';
    select.value=editorBreakpointKey;
    const current=(documentState.breakpoints || []).find(value=>value.key===editorBreakpointKey);
    if (remove) remove.disabled=!current || !!current.isSystem;
  }

  function applyBreakpointPreview() {
    if (!editorPreview) return;
    const breakpoint=(documentState.breakpoints || []).find(value=>value.key===editorBreakpointKey);
    if (!breakpoint) { editorPreview.style.width=''; editorPreview.style.maxWidth=''; editorPreview.style.justifySelf=''; }
    else {
      const representative = breakpoint.maxWidth == null ? Math.max(Number(breakpoint.minWidth)||1200,1440) : Math.max(320,Math.round(((Number(breakpoint.minWidth)||0)+Number(breakpoint.maxWidth))/2));
      editorPreview.style.width='100%'; editorPreview.style.maxWidth=`${representative}px`; editorPreview.style.justifySelf='center';
    }
    refreshResponsiveOverrides();
    syncEditorControls();
  }
  function applyStyle(el, style) {
    if (!el) return;
    const original = rememberOriginal(el);
    styleProperties.forEach(key => { el.style[key] = original.style[key]; });
    scaledElements.delete(el);
    if (!style) return;
    if (['left', 'center', 'right', 'start', 'end', 'justify'].includes(style.textAlign)) el.style.textAlign = style.textAlign;
    if (positiveNumber(style.fontScale) && !(el instanceof HTMLImageElement) && !spacingNumber(style.fontSize)) {
      scaledElements.set(el, style.fontScale);
      refreshScale(el, style.fontScale);
    }
    if (positiveNumber(style.widthPercent)) {
      el.style.width = `${style.widthPercent}%`;
      el.style.maxWidth = '100%';
    }
    if (positiveNumber(style.heightPx)) {
      el.style.height = `${style.heightPx}px`;
      el.style.overflow = el.classList.contains('cms-extra-code') ? 'hidden' : 'auto';
    }
    const hasOffsetX = style.offsetXPercent != null && Number.isFinite(Number(style.offsetXPercent));
    const hasOffsetY = style.offsetYPx != null && Number.isFinite(Number(style.offsetYPx));
    if (hasOffsetX || hasOffsetY) {
      el.style.position = 'relative';
      if (hasOffsetX) el.style.left = `${Number(style.offsetXPercent)}%`;
      if (hasOffsetY) el.style.top = `${Number(style.offsetYPx)}px`;
    }
    if (spacingNumber(style.paddingTop)) el.style.paddingTop = `${style.paddingTop}px`;
    if (spacingNumber(style.paddingBottom)) el.style.paddingBottom = `${style.paddingBottom}px`;
    ['color','backgroundColor','fontFamily','fontWeight','objectFit'].forEach(key => { if (style[key]) el.style[key] = style[key]; });
    // A chosen solid color replaces template gradients in editor and published rendering.
    if (style.backgroundColor) el.style.backgroundImage = 'none';
    ['fontSize','letterSpacing','paddingLeft','paddingRight','borderRadius'].forEach(key => { if (spacingNumber(style[key])) el.style[key] = `${style[key]}px`; });
    if (positiveNumber(style.lineHeight)) el.style.lineHeight = String(style.lineHeight);
    if (style.objectPosition && el instanceof HTMLImageElement) el.style.objectPosition = style.objectPosition;
  }

  function setContentText(el, text, preserveWhitespace = false) {
    if (preserveWhitespace) el.dataset.cmsPreserveWhitespace = 'true';
    else delete el.dataset.cmsPreserveWhitespace;
    if (el.tagName !== 'A' || !el.children.length || !document.createTreeWalker) { el.textContent = text; return; }
    const walker = document.createTreeWalker(el, 4); const nodes = []; let node;
    while ((node = walker.nextNode())) if (node.textContent.trim() && !node.parentElement.closest('svg,i,[aria-hidden="true"]')) nodes.push(node);
    if (nodes.length) { nodes[0].textContent = text; nodes.slice(1).forEach(node => { node.textContent = ''; }); }
    else el.appendChild(document.createTextNode(text));
  }


  function overrideForElement(el, create = true) {
    if (!el?.dataset?.cmsId) return null;
    // An added composite block (for example a service card) owns its content,
    // but each selectable child owns its own geometry/style. This keeps one
    // canonical document store while allowing title/copy/etc. to move independently.
    if (el.dataset.cmsExtraId && !el.dataset.cmsExtraField)
      return pageState().extras.find(x => x.id === el.dataset.cmsExtraId) || null;
    return create ? ensureOverride(el.dataset.cmsId) : pageState().elements[el.dataset.cmsId] || null;
  }

  function contentOverrideForElement(el, create = true) {
    if (el?.dataset?.cmsExtraId) {
      const extra = pageState().extras.find(x => x.id === el.dataset.cmsExtraId) || null;
      if (extra) return extra;
    }
    return overrideForElement(el, create);
  }

  function isInlineEditable(el) {
    if (!el || el.dataset.cmsSignalOnly || el.dataset.cmsSection) return false;
    if (['IMG','VIDEO','DIV','ARTICLE','HEADER','FOOTER','FORM','INPUT','SELECT','TEXTAREA'].includes(el.tagName)) return false;
    return editableTextTags.has(el.tagName) || editableInteractiveTags.has(el.tagName);
  }

  function inlineTextValue(el) {
    const value = typeof el.innerText === 'string' ? el.innerText : el.textContent || '';
    return value.replace(/\r/g, '');
  }

  function deactivateInlineEditing(el = inlineEditNode) {
    if (!el || !isInlineEditable(el)) {
      if (inlineEditNode === el) inlineEditNode = null;
      inlineEditCheckpointed = false;
      return;
    }
    const override = contentOverrideForElement(el, false);
    if (override) {
      const value = inlineTextValue(el);
      if (el.dataset.cmsExtraField === 'title') override.title = value;
      else override.text = value;
      setContentText(el, value, true);
    }
    el.removeAttribute?.('contenteditable');
    el.classList.remove('legend-cms-inline-editing');
    if (inlineEditNode === el) inlineEditNode = null;
    inlineEditCheckpointed = false;
  }

  function activateInlineEditing(el) {
    if (!isInlineEditable(el)) return;
    if (inlineEditNode && inlineEditNode !== el) deactivateInlineEditing(inlineEditNode);
    inlineEditNode = el;
    el.setAttribute('contenteditable', 'plaintext-only');
    el.setAttribute('spellcheck', 'true');
    el.classList.add('legend-cms-inline-editing');
    if (el.dataset.cmsInlineBound === 'true') return;
    el.dataset.cmsInlineBound = 'true';
    el.addEventListener('beforeinput', () => {
      checkpoint();
      inlineEditCheckpointed = true;
    });
    el.addEventListener('input', () => {
      if (!inlineEditCheckpointed) checkpoint();
      inlineEditCheckpointed = false;
      const override = contentOverrideForElement(el);
      if (!override) return;
      const value = inlineTextValue(el);
      if (el.dataset.cmsExtraField === 'title') override.title = value;
      else override.text = value;
      el.dataset.cmsPreserveWhitespace = 'true';
      markDirty();
      updateDirectCanvasUi();
    });
    el.addEventListener('blur', () => {
      if (inlineEditNode !== el) return;
      const override = contentOverrideForElement(el, false);
      if (override) {
        const value = inlineTextValue(el);
        if (el.dataset.cmsExtraField === 'title') override.title = value;
        else override.text = value;
        setContentText(el, value, true);
      }
      inlineEditCheckpointed = false;
      updateDirectCanvasUi();
    });
  }

  const defaultCodeBlock = '<div style="font:600 18px/1.5 system-ui;padding:24px">Edit this code block to build custom content.</div>';

  function renderCodePreview(el, extra) {
    const frame = el?.querySelector?.('iframe[data-cms-code-frame]');
    if (!frame) return;
    const source = extra?.text?.trim() ? extra.text : defaultCodeBlock;
    // A data document has an opaque origin under this sandbox and does not inherit
    // the parent page's script policy. This keeps owner code isolated without
    // weakening the main site's CSP with unsafe-inline/unsafe-eval.
    frame.src = 'data:text/html;charset=utf-8,' + encodeURIComponent(source);
  }

  function mediaUrl(value) {
    if (!editorMode || !value) return value;
    try { const url = new URL(value, API_BASE); const authority = new URL(API_BASE); if (url.origin === authority.origin && /^\/api\/website-content\/media\/[a-f0-9-]+$/i.test(url.pathname)) { url.searchParams.set('ticket', editorTicket); return url.toString(); } } catch {}
    return value;
  }

  function applyFavicon(value) {
    let link = document.querySelector('link[rel~="icon"]');
    if (!link) {
      link = document.createElement('link');
      link.setAttribute('rel', 'icon');
      document.head.appendChild(link);
    }
    const href = value ? mediaUrl(value) : originalFaviconHref;
    if (href) link.setAttribute('href', href);
    const clearType = () => {
      if (typeof link.removeAttribute === 'function') link.removeAttribute('type');
      else link.setAttribute('type', '');
    };
    if (value) clearType();
    else if (originalFaviconType) link.setAttribute('type', originalFaviconType);
    else clearType();
  }

  function syncFaviconControls() {
    const preview = document.getElementById('legend-cms-favicon-preview');
    const remove = document.getElementById('legend-cms-favicon-remove');
    const current = documentState.faviconImageDataUrl;
    if (preview) {
      preview.src = current ? mediaUrl(current) : originalFaviconHref;
      preview.alt = current ? 'Current website favicon' : 'LEGEND fallback favicon';
    }
    if (remove) remove.disabled = !current;
  }

  function updateCollectionData(payload) {
    collectionData = new Map();
    for (const projection of Array.isArray(payload?.collections) ? payload.collections : []) {
      if (projection?.id) collectionData.set(projection.id, projection);
    }
    dynamicCollectionItem = payload?.dynamicItem || dynamicCollectionItem;
  }

  function resolveDataBinding(binding) {
    if (!binding?.collectionId || !binding?.field) return undefined;
    const projection = collectionData.get(binding.collectionId);
    if (!projection) return undefined;
    let item = null;
    if (dynamicCollectionItem?.collectionId === binding.collectionId && dynamicCollectionItem?.fields) item = dynamicCollectionItem;
    else if (projection.isList !== true) item = Array.isArray(projection.items) ? projection.items[0] : null;
    if (!item?.fields || !Object.prototype.hasOwnProperty.call(item.fields, binding.field)) return undefined;
    return item.fields[binding.field];
  }

  function applyDataBinding(el, binding) {
    const value = resolveDataBinding(binding);
    if (value === undefined || value === null || !el) return;
    const target = binding?.target || 'text';
    if (target === 'image') {
      if (el instanceof HTMLImageElement && typeof value === 'string' && safeUrl(value, true)) el.src = mediaUrl(value);
      return;
    }
    if (target === 'href') {
      if (el.tagName === 'A' && typeof value === 'string' && safeUrl(value)) el.href = value;
      return;
    }
    if (!el.dataset.cmsSection && !['DIV','ARTICLE','HEADER','FOOTER','FORM'].includes(el.tagName)) setContentText(el, String(value), true);
  }
  function prefersReducedMotion() {
    try { return window.matchMedia?.('(prefers-reduced-motion: reduce)')?.matches === true; } catch { return false; }
  }

  function animationKeyframes(binding) {
    const distance = Number.isFinite(Number(binding?.distancePx)) ? Number(binding.distancePx) : 24;
    const effect = binding?.effect || 'fade';
    if (effect === 'slide-up') return [{ opacity:0, transform:`translateY(${distance}px)` }, { opacity:1, transform:'translateY(0)' }];
    if (effect === 'slide-down') return [{ opacity:0, transform:`translateY(${-distance}px)` }, { opacity:1, transform:'translateY(0)' }];
    if (effect === 'slide-left') return [{ opacity:0, transform:`translateX(${distance}px)` }, { opacity:1, transform:'translateX(0)' }];
    if (effect === 'slide-right') return [{ opacity:0, transform:`translateX(${-distance}px)` }, { opacity:1, transform:'translateX(0)' }];
    if (effect === 'scale') return [{ opacity:0, transform:'scale(.94)' }, { opacity:1, transform:'scale(1)' }];
    if (effect === 'rotate') return [{ opacity:0, transform:'rotate(-6deg)' }, { opacity:1, transform:'rotate(0deg)' }];
    return [{ opacity:0 }, { opacity:1 }];
  }

  function playAnimation(el, binding) {
    if (!el || typeof el.animate !== 'function' || prefersReducedMotion()) return null;
    const duration = Math.max(50, Math.min(5000, Number(binding?.durationMs) || 400));
    const delay = Math.max(0, Math.min(5000, Number(binding?.delayMs) || 0));
    const easing = ['linear','ease','ease-in','ease-out','ease-in-out'].includes(binding?.easing) ? binding.easing : 'ease';
    return el.animate(animationKeyframes(binding), { duration, delay, easing, fill:'none' });
  }

  function applyAnimations(el, bindings) {
    if (!el) return;
    const normalized = Array.isArray(bindings) ? bindings : [];
    const signature = JSON.stringify(normalized);
    const current = animationRuntime.get(el);
    if (current?.signature === signature) return;
    current?.cleanup?.();
    animationRuntime.delete(el);
    if (editorMode || renderInput?.server || normalized.length === 0) return;
    const cleanups = [];
    const played = new Set();
    const invoke = binding => {
      if (binding?.once && played.has(binding.id)) return;
      if (binding?.once) played.add(binding.id);
      playAnimation(el, binding);
    };
    for (const binding of normalized) {
      if (!binding?.id) continue;
      if (binding.trigger === 'load') {
        const timer = setTimeout(() => invoke(binding), 0);
        cleanups.push(() => clearTimeout(timer));
      } else if (binding.trigger === 'view') {
        if (typeof IntersectionObserver === 'function') {
          const observer = new IntersectionObserver(entries => {
            if (!entries.some(entry => entry.isIntersecting)) return;
            invoke(binding);
            if (binding.once) observer.disconnect();
          }, { threshold:0.15 });
          observer.observe(el);
          cleanups.push(() => observer.disconnect());
        } else {
          const timer = setTimeout(() => invoke(binding), 0);
          cleanups.push(() => clearTimeout(timer));
        }
      } else if (binding.trigger === 'hover') {
        const handler = () => invoke(binding);
        el.addEventListener('mouseenter', handler);
        cleanups.push(() => el.removeEventListener('mouseenter', handler));
      } else if (binding.trigger === 'click') {
        const handler = () => invoke(binding);
        el.addEventListener('click', handler);
        cleanups.push(() => el.removeEventListener('click', handler));
      }
    }
    animationRuntime.set(el, { signature, cleanup:() => cleanups.forEach(cleanup => cleanup()) });
  }

  function aiOperationLabel(operation) {
    const kind=String(operation?.kind || '').replaceAll('_',' ');
    if (operation?.kind==='suggest_image') return `${kind}: ${operation.imagePrompt || 'image direction'}`;
    if (operation?.kind==='set_text') return `${kind}: ${(operation.text || '').slice(0,120)}`;
    if (operation?.breakpointKey) return `${kind} · ${operation.breakpointKey}`;
    return kind || 'website change';
  }

  function renderAiProposal() {
    const host=document.getElementById('legend-cms-ai-proposal');
    const status=document.getElementById('legend-cms-ai-status');
    const apply=document.getElementById('legend-cms-ai-apply');
    const discard=document.getElementById('legend-cms-ai-discard');
    if(!host?.replaceChildren) return;
    host.replaceChildren();
    if(!pendingAiProposal){
      if(status) status.textContent='No proposal generated.';
      if(apply) apply.disabled=true;
      if(discard) discard.disabled=true;
      return;
    }
    if(status) status.textContent=`Proposal ready · base revision ${pendingAiProposal.baseRevision}. Review before applying.`;
    const summary=document.createElement('p'); summary.className='legend-cms-ai-summary'; summary.textContent=pendingAiProposal.summary || 'Website Studio AI proposal'; host.appendChild(summary);
    for(const operation of pendingAiProposal.operations || []){
      const row=document.createElement('div'); row.className='legend-cms-ai-operation'; row.textContent=aiOperationLabel(operation); host.appendChild(row);
    }
    if(!(pendingAiProposal.operations || []).length){
      const empty=document.createElement('p'); empty.textContent='The model proposed no document changes.'; host.appendChild(empty);
    }
    if(apply) apply.disabled=pendingAiProposal.baseRevision!==revision || !pendingAiProposal.proposedDocument;
    if(discard) discard.disabled=false;
  }

  async function requestAiProposal() {
    const status=document.getElementById('legend-cms-ai-status');
    const prompt=document.getElementById('legend-cms-ai-prompt')?.value?.trim() || '';
    const mode=document.getElementById('legend-cms-ai-mode')?.value || 'responsive';
    if(!prompt){ if(status) status.textContent='Enter what you want the assistant to improve or create.'; return; }
    if(mode==='responsive' && (!selected || selected.dataset.cmsSignalOnly)){
      if(status) status.textContent='Select the element or section you want AI to make responsive.';
      return;
    }
    if(dirty){
      const saved=await save(false);
      if(!saved || dirty){ if(status) status.textContent='Save the current draft before generating an AI proposal.'; return; }
    }
    if(status) status.textContent='Generating a structured proposal…';
    pendingAiProposal=null;
    try{
      const selectedText=selected && !selected.dataset.cmsSection ? inlineTextValue(selected).slice(0,4000) : null;
      const response=await fetch(`${API_BASE}/api/website-content/manage/ai/propose`,{
        method:'POST',
        headers:{'Content-Type':'application/json'},
        body:JSON.stringify({
          ticket:editorTicket,
          expectedRevision:revision,
          mode,
          instruction:prompt,
          pagePath:currentPageRoute(),
          selectedElementId:selected?.dataset?.cmsId || null,
          selectedSectionId:selectedSection?.dataset?.cmsSection || null,
          selectedText
        })
      });
      const payload=await response.json().catch(()=>({}));
      if(!response.ok) throw new Error(payload.message || payload.error || `AI proposal failed (${response.status})`);
      if(payload.source!=='ai_proposal_preview' || payload.persisted!==false || payload.published!==false || !payload.proposedDocument)
        throw new Error('AI proposal response was invalid.');
      pendingAiProposal=payload;
      renderAiProposal();
    }catch(error){
      pendingAiProposal=null;
      if(status) status.textContent=error?.message || 'Unable to generate an AI proposal.';
      renderAiProposal();
    }
  }

  function applyAiProposal() {
    const status=document.getElementById('legend-cms-ai-status');
    if(!pendingAiProposal?.proposedDocument) return;
    if(pendingAiProposal.baseRevision!==revision){
      if(status) status.textContent='This proposal is stale because the saved draft revision changed. Generate it again.';
      return;
    }
    checkpoint();
    setSelected(null);
    applyDocument(pendingAiProposal.proposedDocument);
    pendingAiProposal=null;
    markDirty();
    renderAiProposal();
    showPanel('ai');
    if(status) status.textContent='Proposal applied to the local draft. Review the canvas, then save or publish normally.';
  }

  function renderMotionControls() {
    const host = document.getElementById('legend-cms-motion-controls');
    if (!host?.replaceChildren) return;
    host.replaceChildren();
    const status = document.getElementById('legend-cms-motion-status');
    if (!selected || selected.dataset.cmsSignalOnly) { if (status) status.textContent='Select a page element or added block.'; return; }
    const existing = overrideForElement(selected, false);
    const bindings = Array.isArray(existing?.animations) ? existing.animations : [];
    if (status) status.textContent = bindings.length ? `${bindings.length} motion interaction${bindings.length===1?'':'s'} on this element.` : 'No motion interactions on this element.';
    const selectControl = (labelText, values, value, onChange) => {
      const label=document.createElement('label'); label.className='legend-cms-group'; label.textContent=labelText;
      const select=document.createElement('select');
      for(const [key,text] of values){ const option=document.createElement('option'); option.value=key; option.textContent=text; select.appendChild(option); }
      select.value=value; select.addEventListener('change',()=>onChange(select.value)); label.appendChild(select); return label;
    };
    for (const binding of bindings) {
      const row=document.createElement('div'); row.className='legend-cms-motion-row';
      const mutate=action=>{ checkpoint(); action(); markDirty(); renderMotionControls(); };
      row.appendChild(selectControl('Trigger', [['load','Page load'],['view','Enter viewport'],['hover','Hover'],['click','Click']], binding.trigger, value=>mutate(()=>binding.trigger=value)));
      row.appendChild(selectControl('Effect', [['fade','Fade'],['slide-up','Slide up'],['slide-down','Slide down'],['slide-left','Slide left'],['slide-right','Slide right'],['scale','Scale'],['rotate','Rotate']], binding.effect, value=>mutate(()=>binding.effect=value)));
      const timing=document.createElement('div'); timing.className='legend-cms-row';
      for(const [labelText,key,min,max] of [['Duration ms','durationMs',50,5000],['Delay ms','delayMs',0,5000],['Distance px','distancePx',-2000,2000]]) {
        const label=document.createElement('label'); label.className='legend-cms-group'; label.textContent=labelText;
        const input=document.createElement('input'); input.type='number'; input.min=String(min); input.max=String(max); input.step='1'; input.value=binding[key] ?? (key==='distancePx'?24:'');
        input.addEventListener('change',()=>mutate(()=>binding[key]=input.value===''?null:Number(input.value))); label.appendChild(input); timing.appendChild(label);
      }
      row.appendChild(timing);
      row.appendChild(selectControl('Easing', [['ease','Ease'],['linear','Linear'],['ease-in','Ease in'],['ease-out','Ease out'],['ease-in-out','Ease in/out']], binding.easing || 'ease', value=>mutate(()=>binding.easing=value)));
      const onceLabel=document.createElement('label'); onceLabel.className='legend-cms-group';
      const once=document.createElement('input'); once.type='checkbox'; once.checked=binding.once!==false; once.addEventListener('change',()=>mutate(()=>binding.once=once.checked));
      onceLabel.append(once,document.createTextNode(' Play once per page session')); row.appendChild(onceLabel);
      const actions=document.createElement('div'); actions.className='legend-cms-row';
      const preview=document.createElement('button'); preview.type='button'; preview.textContent='Preview effect'; preview.addEventListener('click',()=>playAnimation(selected,binding));
      const remove=document.createElement('button'); remove.type='button'; remove.textContent='Remove'; remove.addEventListener('click',()=>{ const ov=selectedOverride(); mutate(()=>ov.animations=(ov.animations||[]).filter(item=>item.id!==binding.id)); });
      actions.append(preview,remove); row.appendChild(actions); host.appendChild(row);
    }
    const add=document.createElement('button'); add.type='button'; add.textContent='Add motion interaction'; add.disabled=bindings.length>=8;
    add.addEventListener('click',()=>{ const ov=selectedOverride(); checkpoint(); ov.animations ||= []; ov.animations.push({id:crypto.randomUUID().replaceAll('-',''),trigger:'view',effect:'fade',durationMs:400,delayMs:0,distancePx:24,easing:'ease',once:true}); markDirty(); renderMotionControls(); });
    host.appendChild(add);
  }
  function applyElementOverride(el, override) {
    if (el?.dataset.cmsSignalOnly) return;
    if (!el || !override) return;
    if (override.actionKey) el.dataset.websiteActionKey = override.actionKey;
    else delete el.dataset.websiteActionKey;
    if (override.hidden === true) el.hidden = true;
    else if (override.hidden === false) el.hidden = false;

    const original = rememberOriginal(el);
    if (el instanceof HTMLImageElement) {
      el.src = override.imageDataUrl ? mediaUrl(override.imageDataUrl) : (original.src || '');
    } else if (!el.dataset.cmsSection && !['DIV','ARTICLE','HEADER','FOOTER','FORM'].includes(el.tagName)) {
      setContentText(el, override.text != null ? override.text : (original.text || ''), override.text != null);
    }

    if (el.tagName === 'A') {
      const href = override.href != null && safeUrl(override.href) ? override.href : original.href;
      if (href) el.setAttribute('href', href); else el.removeAttribute('href');
      el.target = override.target === '_blank' ? '_blank' : '_self'; el.rel = 'noopener noreferrer';
    }
    if (override.alt != null && el.tagName === 'IMG') el.alt = override.alt;
    if (override.videoUrl && el.tagName === 'VIDEO' && safeUrl(override.videoUrl, true)) el.src = mediaUrl(override.videoUrl);
    applyDataBinding(el, override.dataBinding);
    applyStyle(el, effectiveStyle(override));
    applyLayout(el, effectiveLayout(override));
    applyAnimations(el, override.animations);
  }

  function buildExtraNode(extra, editable = true, idPrefix = '') {
    let el;
    if (extra.type === 'image') {
      el = document.createElement('img');
      el.src = mediaUrl(extra.imageDataUrl || ''); el.alt = ''; el.className = 'cms-extra cms-extra-image';
    } else if (extra.type === 'section') {
      el = document.createElement('section');
      if (editable) el.dataset.cmsSection = `extra:${extra.id}`;
      el.className = 'cms-extra cms-extra-section';
    } else if (extra.type === 'video') {
      el = document.createElement('video'); el.controls = true; el.preload = 'metadata';
      if (safeUrl(extra.videoUrl, true)) el.src = mediaUrl(extra.videoUrl); el.className = 'cms-extra';
    } else if (extra.type === 'card') {
      el = document.createElement('article'); el.className = 'cms-extra card cms-extra-card';
      const heading = document.createElement('h3'); setContentText(heading, extra.title || 'New service', true);
      const copy = document.createElement('p'); setContentText(copy, extra.text || '', true);
      if (editable) for (const [node, field] of [[heading, 'title'], [copy, 'text']]) {
        node.dataset.cmsExtraId = extra.id; node.dataset.cmsExtraField = field;
        node.dataset.cmsId = `extra:${extra.id}:${field}`; node.dataset.cmsEditable = 'true';
      }
      el.append(heading, copy);
    } else if (extra.type === 'button') {
      el = document.createElement('a'); el.textContent = extra.text || 'New button';
      if (safeUrl(extra.href)) el.href = extra.href; el.className = 'cms-extra btn primary';
    } else if (extra.type === 'form') {
      el = document.createElement('form');
      el.id = `website_inquiry_${extra.id}`;
      el.className = 'cms-extra public-form cms-extra-form';
      el.dataset.websiteInquiry = '';
      el.dataset.formKey = 'website_inquiry';
      el.setAttribute('action', '/api/website-inquiries/public');
      el.setAttribute('method', 'post');
      const fieldset = document.createElement('fieldset');
      if (editorMode) { el.dataset.preview = ''; fieldset.disabled = true; }
      const legend = document.createElement('legend'); legend.textContent = extra.title || 'Send an inquiry';
      const grid = document.createElement('div'); grid.className = 'public-form-grid';
      const field = (labelText, name, type = 'text', attrs = {}) => {
        const label = document.createElement('label'); label.textContent = labelText;
        const input = name === 'Message' ? document.createElement('textarea') : document.createElement('input');
        if (name !== 'Message') input.type = type;
        input.name = name; input.required = true;
        if (name === 'Message') input.rows = 5;
        Object.entries(attrs).forEach(([key,value]) => input.setAttribute(key, value));
        label.appendChild(input); return label;
      };
      grid.append(
        field('First Name','FirstName','text',{autocomplete:'given-name',maxlength:'120'}),
        field('Last Name','LastName','text',{autocomplete:'family-name',maxlength:'120'}),
        field('Phone Number','Phone','tel',{inputmode:'tel',autocomplete:'tel',maxlength:'64'}),
        field('Email','Email','email',{autocomplete:'email',maxlength:'254'})
      );
      const message = field('Message','Message','text',{maxlength:'12000'}); message.className='public-form-full'; grid.appendChild(message);
      const consentLabel = document.createElement('label'); consentLabel.className='public-form-consent public-form-full';
      const consent = document.createElement('input'); consent.type='checkbox'; consent.name='consent'; consent.required=true;
      const consentText = document.createElement('span'); consentText.textContent='I agree to share this inquiry with this website.';
      consentLabel.append(consent,consentText); grid.appendChild(consentLabel);
      const submit = document.createElement('button'); submit.type='submit'; submit.className='btn primary'; submit.textContent=extra.text || 'Send inquiry';
      const status = document.createElement('p'); status.setAttribute('role','status'); status.setAttribute('aria-live','polite');
      fieldset.append(legend,grid,submit); el.append(fieldset,status);
    } else if (extra.type === 'code') {
      el = document.createElement('div'); el.className = 'cms-extra cms-extra-code';
      const frame = document.createElement('iframe'); frame.dataset.cmsCodeFrame = 'true'; frame.title = 'Custom code block';
      frame.setAttribute('sandbox', 'allow-scripts allow-forms allow-modals allow-popups');
      frame.setAttribute('referrerpolicy', 'no-referrer'); frame.setAttribute('loading', 'lazy');
      el.appendChild(frame); renderCodePreview(el, extra);
    } else {
      el = document.createElement('p'); el.textContent = extra.text || ''; el.className = 'cms-extra cms-extra-text';
    }
    if (editable) {
      el.dataset.cmsExtraId = extra.id; el.dataset.cmsId = `extra:${extra.id}`; el.dataset.cmsEditable = 'true';
    } else if (idPrefix) el.dataset.cmsReusableChild = `${idPrefix}:${extra.id}`;
    return el;
  }

  function reusableDefinition(instance) {
    return instance?.type === 'reusable' && instance.syncSourceId ? documentState.reusableComponents?.[instance.syncSourceId] || null : null;
  }

  function renderReusableInstance(wrapper, instance) {
    if (!wrapper || !instance) return;
    wrapper.replaceChildren();
    const definition = reusableDefinition(instance);
    wrapper.dataset.cmsReusableId = instance.syncSourceId || '';
    if (!definition) {
      if (editorMode) {
        const missing = document.createElement('p'); missing.className = 'legend-cms-reusable-missing';
        missing.textContent = 'Reusable component is unavailable. Restore its definition or remove this instance.';
        wrapper.appendChild(missing);
      }
      applyStyle(wrapper, effectiveStyle(instance)); applyLayout(wrapper, effectiveLayout(instance)); applyAnimations(wrapper, instance.animations);
      return;
    }
    const values = Array.isArray(definition.extras) ? definition.extras : [];
    const root = values.find(value => value.sectionId === 'component.root') || values[0] || null;
    const sectionMap = new Map([['component.root', wrapper]]);
    if (definition.kind === 'section' && root?.type === 'section') {
      sectionMap.set(`extra:${root.id}`, wrapper);
      applyStyle(wrapper, { ...effectiveStyle(root), ...effectiveStyle(instance) });
      applyLayout(wrapper, { ...effectiveLayout(root), ...effectiveLayout(instance) });
      applyAnimations(wrapper, instance.animations);
    } else {
      applyStyle(wrapper, effectiveStyle(instance)); applyLayout(wrapper, effectiveLayout(instance)); applyAnimations(wrapper, instance.animations);
    }
    for (const item of values.filter(value => value.type === 'section' && value !== root)) {
      const parent = sectionMap.get(item.sectionId) || wrapper;
      const node = buildExtraNode(item, false, instance.id); node.classList.add('legend-cms-reusable-child');
      parent.appendChild(node); sectionMap.set(`extra:${item.id}`, node);
      applyStyle(node, effectiveStyle(item)); applyLayout(node, effectiveLayout(item));
    }
    const leaves = values.filter(value => value.type !== 'section');
    for (const item of leaves) {
      if (definition.kind === 'block' && root && item !== root) continue;
      const parent = sectionMap.get(item.sectionId) || wrapper;
      const node = buildExtraNode(item, false, instance.id); node.classList.add('legend-cms-reusable-child');
      parent.appendChild(node); applyElementOverride(node, item);
    }
  }

  function createExtra(extra) {
    const section = extra.type === 'section' ? document.querySelector('main') : document.querySelector(`[data-cms-section="${CSS.escape(extra.sectionId)}"]`);
    if (!section) return null;
    if (extra.type === 'reusable') {
      const definition = reusableDefinition(extra);
      const el = document.createElement(definition?.kind === 'section' ? 'section' : 'div');
      el.className = 'cms-extra cms-reusable-instance';
      el.dataset.cmsExtraId = extra.id; el.dataset.cmsId = `extra:${extra.id}`; el.dataset.cmsEditable = 'true';
      if (extra.hidden === true) el.hidden = true;
      section.appendChild(el); renderReusableInstance(el, extra); return el;
    }
    const el = buildExtraNode(extra, true);
    section.appendChild(el); applyElementOverride(el, extra); return el;
  }

  function selectedAddedExtra() {
    const id = selected?.dataset?.cmsExtraId;
    return id ? pageState().extras.find(extra => extra.id === id) || null : null;
  }

  function captureReusableDefinition(name, existingId = null) {
    const source = selectedAddedExtra();
    if (!source || source.type === 'reusable' || source.type === 'form') return null;
    const componentId = existingId || crypto.randomUUID().replaceAll('-', '');
    const sourceIds = new Set([source.id]);
    if (source.type === 'section') {
      let changed = true;
      while (changed) {
        changed = false;
        for (const extra of pageState().extras) {
          const sectionId = String(extra.sectionId || '');
          const parent = sectionId.startsWith('extra:') ? sectionId.slice(6) : null;
          if (parent && sourceIds.has(parent) && !sourceIds.has(extra.id)) { sourceIds.add(extra.id); changed = true; }
        }
      }
    }
    const sourceExtras = [source, ...pageState().extras.filter(extra => extra.id !== source.id && sourceIds.has(extra.id))];
    const idMap = new Map([[source.id, 'root']]);
    sourceExtras.slice(1).forEach((extra, index) => idMap.set(extra.id, `item-${index + 1}`));
    const extras = sourceExtras.map(extra => {
      const copy = JSON.parse(JSON.stringify(extra));
      const oldId = extra.id;
      const sectionId = String(extra.sectionId || '');
      const parent = sectionId.startsWith('extra:') ? sectionId.slice(6) : null;
      copy.id = idMap.get(oldId);
      copy.sectionId = oldId === source.id ? 'component.root' : parent && idMap.has(parent) ? `extra:${idMap.get(parent)}` : 'component.root';
      copy.placement = null; copy.signals = []; copy.syncSourceId = null; delete copy.hidden;
      return copy;
    });
    return { id: componentId, name: (name || 'Reusable component').trim().slice(0, 120), kind: source.type === 'section' ? 'section' : 'block', elements: {}, sectionOrder: {}, extras };
  }

  function componentInUse(componentId) {
    let used = false;
    const inspect = extras => { if ((extras || []).some(extra => extra.type === 'reusable' && extra.syncSourceId === componentId)) used = true; };
    inspect(documentState.extras);
    Object.values(documentState.pages || {}).forEach(page => inspect(page?.extras));
    return used;
  }

  function refreshReusableInstances(componentId = null) {
    document.querySelectorAll('.cms-reusable-instance[data-cms-extra-id]').forEach(wrapper => {
      const instance = pageState().extras.find(extra => extra.id === wrapper.dataset.cmsExtraId);
      if (!instance || (componentId && instance.syncSourceId !== componentId)) return;
      renderReusableInstance(wrapper, instance);
    });
    updateDirectCanvasUi();
  }

  function renderReusableComponents() {
    const host = document.getElementById('legend-cms-component-list');
    const status = document.getElementById('legend-cms-component-status');
    if (!host?.replaceChildren) return;
    host.replaceChildren();
    const definitions = Object.values(documentState.reusableComponents || {}).sort((a,b) => (a.name || '').localeCompare(b.name || ''));
    for (const definition of definitions) {
      const row = document.createElement('div'); row.className = 'legend-cms-component-row';
      const info = document.createElement('div');
      const title = document.createElement('strong'); title.textContent = definition.name || definition.id;
      const meta = document.createElement('small'); meta.textContent = `${definition.kind || 'block'} · ${definition.id}`;
      info.append(title, meta);
      const insert = document.createElement('button'); insert.type = 'button'; insert.textContent = 'Insert';
      insert.addEventListener('click', () => {
        const section = selectedSection || document.querySelector('[data-cms-section]');
        if (!section) { if (status) status.textContent = 'Select a section before inserting a reusable component.'; return; }
        checkpoint();
        const instance = { id: crypto.randomUUID(), type: 'reusable', sectionId: section.dataset.cmsSection, syncSourceId: definition.id, style: { widthPercent: 100 }, signals: [] };
        pageState().extras.push(instance);
        const created = createExtra(instance); setSelected(created); markDirty(); renderReusableComponents();
      });
      const update = document.createElement('button'); update.type = 'button'; update.textContent = 'Update from selected';
      const selectedSource = selectedAddedExtra(); update.disabled = !selectedSource || selectedSource.type === 'reusable';
      update.addEventListener('click', () => {
        const next = captureReusableDefinition(definition.name, definition.id);
        if (!next) { if (status) status.textContent = 'Select an added block or added section to update this component.'; return; }
        checkpoint(); documentState.reusableComponents[definition.id] = next;
        refreshReusableInstances(definition.id); markDirty(); renderReusableComponents();
      });
      const remove = document.createElement('button'); remove.type = 'button'; remove.textContent = 'Delete';
      remove.disabled = componentInUse(definition.id);
      remove.title = remove.disabled ? 'Remove every instance before deleting this reusable definition.' : '';
      remove.addEventListener('click', () => {
        if (componentInUse(definition.id)) return;
        checkpoint(); markDeleted('component:' + definition.id); delete documentState.reusableComponents[definition.id]; markDirty(); renderReusableComponents();
      });
      row.append(info, insert, update, remove); host.appendChild(row);
    }
    if (!definitions.length) { const empty = document.createElement('p'); empty.textContent = 'No reusable components yet.'; host.appendChild(empty); }
  }
  function pageLayerSections() {
    return Array.from(document.querySelectorAll('[data-cms-section]')).filter(section => {
      if (section.matches('.site-header,.site-footer')) return false;
      const parentSection = section.parentElement?.closest?.('[data-cms-section]');
      return !parentSection;
    });
  }

  function syncSectionOrderFromDom() {
    const page = pageState();
    page.sectionOrder ||= {};
    const active = new Set();
    pageLayerSections().forEach((section,index)=>{
      const id=section.dataset.cmsSection;
      if(!id) return;
      active.add(id);
      page.sectionOrder[id]=index;
    });
    for(const key of Object.keys(page.sectionOrder)) if(!active.has(key)) delete page.sectionOrder[key];
  }

  function applySectionOrder() {
    const sections=pageLayerSections();
    const position=new Map(sections.map((section,index)=>[section,index]));
    sections.sort((a,b)=>{
      const ai=pageState().sectionOrder[a.dataset.cmsSection];
      const bi=pageState().sectionOrder[b.dataset.cmsSection];
      const av=Number.isFinite(Number(ai))?Number(ai):position.get(a);
      const bv=Number.isFinite(Number(bi))?Number(bi):position.get(b);
      return av-bv;
    });
    const groups=new Map();
    for(const section of sections){
      const parent=section.parentElement;
      if(!parent) continue;
      if(!groups.has(parent)) groups.set(parent,[]);
      groups.get(parent).push(section);
    }
    groups.forEach(group=>group.forEach(section=>section.parentElement.appendChild(section)));
  }

  function reorderSectionByLayer(sourceId,targetId) {
    if(!sourceId || !targetId || sourceId===targetId) return;
    const source=pageLayerSections().find(section=>section.dataset.cmsSection===sourceId);
    const target=pageLayerSections().find(section=>section.dataset.cmsSection===targetId);
    if(!source || !target || source.parentElement!==target.parentElement) return;
    checkpoint();
    target.parentElement.insertBefore(source,target);
    syncSectionOrderFromDom();
    setSelected(source);
    markDirty();
    refreshLayers();
  }

  function applyDocument(doc) {
    documentState = normalizeDocument(doc);
    applyTheme(documentState.theme);
    applyFavicon(documentState.faviconImageDataUrl);
    const metadata = pageState();
    document.title = metadata.title ?? originalTitle;
    const description = document.querySelector('meta[name="description"]');
    if (description) description.setAttribute('content', metadata.description ?? originalDescription);

    document.querySelectorAll('.cms-extra').forEach(x => { scaledElements.delete(x); x.remove(); });
    pageState().extras.filter(x => x.type === 'section').forEach(createExtra);
    pageState().extras.filter(x => x.type !== 'section').forEach(createExtra);
    // Composite children do not exist until their parent extra is rendered.
    // Re-apply the one canonical page element map after extras exist.
    Object.entries(pageState().elements).forEach(([id, override]) => {
      const el = document.querySelector(`[data-cms-id="${CSS.escape(id)}"]`);
      applyElementOverride(el, override);
    });
    applySectionOrder();
    Object.entries(pageState().elements).forEach(([id, ov]) => applyPlacement(document.querySelector(`[data-cms-id="${CSS.escape(id)}"]`), ov.placement));
    pageState().extras.forEach(extra => applyPlacement(document.querySelector(`[data-cms-id="extra:${CSS.escape(extra.id)}"]`), extra.placement));
  }

  function refreshResponsiveOverrides() {
    Object.entries(pageState().elements).forEach(([id, override]) => applyElementOverride(document.querySelector(`[data-cms-id="${CSS.escape(id)}"]`), override));
    pageState().extras.forEach(extra => {
      const node = document.querySelector(`[data-cms-id="extra:${CSS.escape(extra.id)}"]`);
      if (extra.type === 'reusable') renderReusableInstance(node, extra);
      else applyElementOverride(node, extra);
    });
    refreshScaledElements();
    updateDirectCanvasUi();
  }

  function bindBusiness(payload) {
    updateCollectionData(payload);
    const business = payload.business;
    if (SITE_KEY !== 'business') return;
    if (!business?.id || !business.displayName) throw new Error('This business website is unavailable.');
    BUSINESS_ID = business.id;
    document.querySelectorAll('[data-business-name]').forEach(el => { el.textContent = business.displayName; });
    document.querySelectorAll('[data-business-field]').forEach(el => { const value = business[el.dataset.businessField]; el.textContent = value || ''; el.hidden = !value; });

  }
  function templatePageEntries() {
    const entries=new Map();
    for (const page of context.pages || []) {
      const route=normalizePageRoute(page?.path);
      if (!route) continue;
      entries.set(route,{route,label:page.label || route,template:true});
    }
    return entries;
  }

  function websitePageEntries(includeDeleted = true) {
    const entries=templatePageEntries();
    for (const [rawPath,page] of Object.entries(documentState.pages || {})) {
      const route=normalizePageRoute(rawPath);
      if (!route || !page || typeof page!=='object') continue;
      const previous=entries.get(route);
      const navigation=page.navigation || {};
      entries.set(route,{
        route,
        label:navigation.label || page.title || previous?.label || route,
        template:previous?.template === true || !!page.templatePath,
        templatePath:normalizePageRoute(page.templatePath) || (previous?.template ? route : null),
        deleted:navigation.isDeleted === true,
        showInNavigation:navigation.showInNavigation !== false,
        parentPath:normalizePageRoute(navigation.parentPath),
        order:Number.isFinite(Number(navigation.order)) ? Number(navigation.order) : 0
      });
    }
    return [...entries.values()]
      .filter(entry=>includeDeleted || !entry.deleted)
      .sort((a,b)=>a.order-b.order || a.route.localeCompare(b.route));
  }

  function ensurePageRecord(route) {
    documentState.pages ||= {};
    if (!documentState.pages[route]) documentState.pages[route]={elements:{},sectionOrder:{},extras:[],navigation:{showInNavigation:true,order:0,isDeleted:false}};
    const page=documentState.pages[route];
    page.elements ||= {}; page.sectionOrder ||= {}; page.extras ||= []; page.navigation ||= {showInNavigation:true,order:0,isDeleted:false};
    return page;
  }

  async function navigateToEditorPage(route) {
    route=normalizePageRoute(route); if(!route) return;
    if (saving) { const status=document.getElementById('legend-cms-status'); if(status) status.textContent='Wait for the current save to finish, then choose a page.'; return; }
    if (dirty) { const saved=await save(false); if(!saved || dirty) return; }
    const templates=templatePageEntries();
    const entry=websitePageEntries(true).find(value=>value.route===route);
    const url=new URL(location.origin);
    if (SITE_KEY==='business') {
      const nativeTemplate=templates.has(route) && (!entry?.templatePath || entry.templatePath===route);
      url.pathname='/business-preview/' + (nativeTemplate ? route.replace(/^\//,'') : '');
      url.searchParams.set('businessId',BUSINESS_ID);
      if (!nativeTemplate) url.searchParams.set('cmsPage',route);
    } else {
      const prefix = SITE_KEY === 'protect' ? (managementPayload?.agentSlug ? `/a/${encodeURIComponent(managementPayload.agentSlug)}` : context.pagePrefix || '') : '';
      url.pathname=prefix + (route==='/'?'/':route);
    }
    url.searchParams.set('legendEdit',editorTicket);
    location.assign(url.toString());
  }

  function renderPageManager() {
    const host=document.getElementById('legend-cms-page-list');
    if (!host?.replaceChildren) return;
    host.replaceChildren();
    const current=currentPageRoute();
    const entries=websitePageEntries(true);
    for (const entry of entries) {
      const row=document.createElement('div'); row.className='legend-cms-page-row';
      const open=document.createElement('button'); open.type='button'; open.textContent=`${entry.label} · ${entry.route}${entry.deleted?' · Deleted':''}`;
      open.disabled=entry.deleted; open.setAttribute('aria-current',String(entry.route===current));
      open.addEventListener('click',()=>void navigateToEditorPage(entry.route)); row.appendChild(open);
      if (SITE_KEY==='business' && entry.deleted) {
        const restore=document.createElement('button'); restore.type='button'; restore.textContent='Restore';
        restore.addEventListener('click',()=>{ checkpoint(); const page=ensurePageRecord(entry.route); page.navigation.isDeleted=false; markDirty(); syncPageControls(); });
        row.appendChild(restore);
      }
      host.appendChild(row);
    }
  }

  function syncBusinessPageFields() {
    const page=pageState(); const navigation=page.navigation ||= {showInNavigation:true,order:0,isDeleted:false};
    const route=currentPageRoute();
    const values={
      'legend-cms-page-nav-label':navigation.label || page.title || route,
      'legend-cms-page-slug':route,
      'legend-cms-page-order':String(Number.isFinite(Number(navigation.order))?Number(navigation.order):0)
    };
    for (const [id,value] of Object.entries(values)) { const input=document.getElementById(id); if(input) input.value=value; }
    const visible=document.getElementById('legend-cms-page-nav-visible'); if(visible) visible.checked=navigation.showInNavigation!==false;
    const parent=document.getElementById('legend-cms-page-parent');
    if(parent){ parent.replaceChildren(); const none=document.createElement('option'); none.value=''; none.textContent='Top level'; parent.appendChild(none);
      for(const entry of websitePageEntries(false)){ if(entry.route===route) continue; const option=document.createElement('option'); option.value=entry.route; option.textContent=`${entry.label} · ${entry.route}`; parent.appendChild(option); }
      parent.value=normalizePageRoute(navigation.parentPath)||'';
    }
    const remove=document.getElementById('legend-cms-page-delete'); if(remove){ remove.disabled=route==='/'; remove.textContent=navigation.isDeleted?'Restore page':'Delete page'; }
    const businessOnly=document.getElementById('legend-cms-page-business-tools'); if(businessOnly) businessOnly.hidden=SITE_KEY!=='business';
    const fixedNotice=document.getElementById('legend-cms-page-fixed-notice'); if(fixedNotice) fixedNotice.hidden=SITE_KEY==='business';
    renderPageManager();
  }

  function refreshPageSelector() {
    const select=document.getElementById('legend-cms-page-select'); if(!select) return;
    const current=currentPageRoute();
    select.replaceChildren();
    for(const entry of websitePageEntries(false)){
      const option=document.createElement('option');
      option.value=entry.route;
      option.textContent=entry.label;
      select.appendChild(option);
    }
    if ([...select.options].some(option=>option.value===current)) select.value=current;
  }

  function installPageSelector() {
    const panel=document.querySelector('.legend-cms-panel'); if(!panel) return;
    const label=document.createElement('label'); label.className='legend-cms-group'; label.textContent='Website page';
    const select=document.createElement('select'); select.id='legend-cms-page-select'; select.setAttribute('aria-label','Website page'); label.appendChild(select);
    select.addEventListener('change',()=>void navigateToEditorPage(select.value));
    panel.insertBefore(label,panel.querySelector('.legend-cms-navigation'));
    refreshPageSelector();
    syncPageControls();
  }
  function preservePreviewNavigation() {
    if (renderInput || SITE_KEY !== 'business') return;
    document.querySelectorAll('a[href]').forEach(el => { const url = new URL(el.getAttribute('href'), location.origin); if (url.origin !== location.origin || !url.pathname.startsWith('/business-preview/')) return; url.searchParams.set('businessId', BUSINESS_ID); if (editorMode) url.searchParams.set('legendEdit', editorTicket); el.href = url.toString(); });
  }
  function unavailable(error) {
    if (SITE_KEY === 'business') { document.body.replaceChildren(); const main = document.createElement('main'); main.textContent = error.message || 'This website is unavailable.'; document.body.appendChild(main); document.documentElement.hidden = false; }
  }

  let publicRuntimeStarted = false;
  function loadRuntimeScript(src) {
    return new Promise((resolve, reject) => {
      if (!src) { resolve(); return; }
      const existing = [...document.scripts].find(script => script.src === new URL(src, location.origin).href);
      if (existing) { resolve(); return; }
      const script = document.createElement('script');
      script.src = src; script.async = true;
      script.addEventListener('load', resolve, { once: true });
      script.addEventListener('error', reject, { once: true });
      document.head.appendChild(script);
    });
  }

  function initializeMetaPixel(pixelId) {
    if (!pixelId || typeof window === 'undefined') return;
    if (typeof window.fbq !== 'function') {
      const fbq = function() { fbq.callMethod ? fbq.callMethod.apply(fbq, arguments) : fbq.queue.push(arguments); };
      if (!window._fbq) window._fbq = fbq;
      fbq.push = fbq; fbq.loaded = true; fbq.version = '2.0'; fbq.queue = [];
      window.fbq = fbq;
      const script = document.createElement('script');
      script.async = true; script.src = 'https://connect.facebook.net/en_US/fbevents.js';
      document.head.appendChild(script);
    }
    const initialized = window.__legendWebsitePixelIds ||= new Set();
    if (!initialized.has(pixelId)) {
      window.fbq('init', pixelId);
      initialized.add(pixelId);
    }
  }

  function applyRuntimeActionContracts() {
    const options = Array.isArray(ctaCatalog) ? ctaCatalog : [];
    if (!options.length) return;
    document.querySelectorAll('a[href],button[data-website-action-key]').forEach(element => {
      const key = element.dataset.websiteActionKey;
      const href = element.getAttribute('href');
      const option = (key && options.find(candidate => candidate.key === key))
        || (href && options.find(candidate => candidate.href === href));
      if (!option) return;
      element.dataset.websiteActionKey = option.key;
      element.dataset.websiteBindingId = element.dataset.cmsId || option.key;
      element.dataset.websiteAnalyticsEvent = option.analyticsEventName || 'cta_click';
      if (option.metaIntentEventName) element.dataset.websiteMetaIntent = option.metaIntentEventName;
      else delete element.dataset.websiteMetaIntent;
    });
  }

  function installPublishedSignalBindings() {
    if (editorMode || renderInput?.server) return;
    const session = window.LEGEND_PUBLIC_META_SESSION;
    if (!session || typeof session.trackConfiguredEvent !== 'function') return;

    const allowedTriggers = new Set([
      'viewed', 'click', 'form_started', 'submit_attempt',
      'field_started', 'validation_failed', 'field_completed', 'scroll_threshold'
    ]);
    const page = pageState();
    const candidates = [
      ...Object.entries(page.elements || {}).map(([id, override]) => ({ id, override, node: document.querySelector(`[data-cms-id="${CSS.escape(id)}"]`) })),
      ...(page.extras || []).map(extra => ({ id: `extra:${extra.id}`, override: extra, node: document.querySelector(`[data-cms-id="extra:${CSS.escape(extra.id)}"]`) }))
    ];
    const cleanups = [];

    const emit = (binding, elementId) => {
      if (!binding || binding.deliveryMode === 'off' || !allowedTriggers.has(binding.trigger)) return;
      const onceKey = binding.oncePerSession ? `website-binding:${binding.id}` : null;
      session.trackConfiguredEvent(binding.eventName, {
        deliveryMode: binding.deliveryMode,
        onceKey,
        metadata: {
          websiteBindingId: binding.id,
          elementId,
          trigger: binding.trigger,
          deliveryMode: binding.deliveryMode,
          pagePath: currentPageRoute(),
          source: 'website_signal_binding'
        }
      });
    };

    const observe = (node, binding, elementId, threshold) => {
      if (typeof IntersectionObserver !== 'function') {
        emit(binding, elementId);
        return;
      }
      const observer = new IntersectionObserver(entries => {
        if (!entries.some(entry => entry.isIntersecting && entry.intersectionRatio >= threshold)) return;
        emit(binding, elementId);
        if (binding.oncePerSession) observer.disconnect();
      }, { threshold });
      observer.observe(node);
      cleanups.push(() => observer.disconnect());
    };

    for (const candidate of candidates) {
      if (!candidate.node || !Array.isArray(candidate.override?.signals)) continue;
      for (const binding of candidate.override.signals) {
        if (!binding?.id || !binding.eventName || binding.deliveryMode === 'off' || !allowedTriggers.has(binding.trigger)) continue;
        const fire = () => emit(binding, candidate.id);
        switch (binding.trigger) {
          case 'viewed':
            observe(candidate.node, binding, candidate.id, 0.25);
            break;
          case 'scroll_threshold':
            observe(candidate.node, binding, candidate.id, 0.5);
            break;
          case 'click':
            candidate.node.addEventListener('click', fire);
            cleanups.push(() => candidate.node.removeEventListener('click', fire));
            break;
          case 'form_started':
            candidate.node.addEventListener('focusin', fire);
            cleanups.push(() => candidate.node.removeEventListener('focusin', fire));
            break;
          case 'submit_attempt':
            candidate.node.addEventListener('submit', fire, true);
            cleanups.push(() => candidate.node.removeEventListener('submit', fire, true));
            break;
          case 'field_started':
            candidate.node.addEventListener('focus', fire);
            cleanups.push(() => candidate.node.removeEventListener('focus', fire));
            break;
          case 'validation_failed':
            candidate.node.addEventListener('invalid', fire, true);
            cleanups.push(() => candidate.node.removeEventListener('invalid', fire, true));
            break;
          case 'field_completed':
            candidate.node.addEventListener('change', fire);
            cleanups.push(() => candidate.node.removeEventListener('change', fire));
            break;
        }
      }
    }

    window.__legendWebsiteSignalBindingsCleanup?.();
    window.__legendWebsiteSignalBindingsCleanup = () => cleanups.forEach(cleanup => cleanup());
  }

  async function startPublicRuntime() {
    if (publicRuntimeStarted || editorMode || renderInput?.server) return;
    if (!['legend','business'].includes(SITE_KEY)) return;
    if (!context.trackingAsset || !context.metaSignalAsset) return;
    if (SITE_KEY === 'business' && location.pathname.startsWith('/business-preview')) return;
    publicRuntimeStarted = true;
    try {
      const runtimeUrl = new URL(`${API_BASE}/api/website-content/public/runtime`);
      runtimeUrl.searchParams.set('siteKey', SITE_KEY);
      const response = await fetch(runtimeUrl, { cache: 'no-store' });
      if (!response.ok) throw new Error('Public website runtime is unavailable.');
      const payload = await response.json();
      ctaCatalog = Array.isArray(payload.ctaCatalog?.options) ? payload.ctaCatalog.options : [];
      applyRuntimeActionContracts();

      const analytics = payload.analytics || {};
      window.LEGEND_ANALYTICS_CONFIG = {
        ...analytics,
        siteKey: SITE_KEY,
        publishedVersionId: payload.publishedVersionId || null
      };
      document.body.dataset.pageKey ||= pageKey;

      const meta = payload.meta || {};
      initializeMetaPixel(meta.pixelId);
      await loadRuntimeScript(context.trackingAsset || '/legend-public-tracking.js');
      await loadRuntimeScript(context.metaSignalAsset || '/legend-public-meta-signal-intelligence.js');
      if (meta.enabled && window.metaSignalIntelligence?.createLandingSession) {
        const inquiryForm = document.querySelector('form[data-website-inquiry][data-form-key]');
        window.LEGEND_PUBLIC_META_SESSION = window.metaSignalIntelligence.createLandingSession({
          ...meta,
          siteKey: SITE_KEY,
          quoteType: SITE_KEY === 'business' ? 'business' : 'legend',
          pageKey,
          effectivePageKey: pageKey,
          pageVariant: SITE_KEY + '_website',
          pageMode: 'site_mode',
          formId: inquiryForm?.id || inquiryForm?.dataset.formKey || '',
          requiredContactFields: inquiryForm ? ['FirstName','LastName','Phone','Email'] : []
        });
        installPublishedSignalBindings();
      }
    } catch (error) {
      console.error('[legend-public-runtime]', error);
    }
  }
  async function loadPublic() {
    if (SITE_KEY === 'business' && !BUSINESS_ID) {
      const resolved = await fetch(`${API_BASE}/api/website-content/public/resolve?host=${encodeURIComponent(location.hostname)}`, { cache: 'no-store' });
      if (!resolved.ok) throw new Error('This domain is not connected to a published business website.');
      const resolvedPayload = await resolved.json(); BUSINESS_ID = resolvedPayload.businessId || resolvedPayload.business?.id;
      if (!BUSINESS_ID) throw new Error('This domain is not connected to a business.');
    }
    const url = new URL(`${API_BASE}/api/website-content/public/${encodeURIComponent(SITE_KEY)}`);
    if (AGENT_SLUG) url.searchParams.set('agentSlug', AGENT_SLUG);
    if (BUSINESS_ID) url.searchParams.set('businessId', BUSINESS_ID);
    try {
      const response = await fetch(url, { cache: 'no-store' });
      if (!response.ok) { if (SITE_KEY === 'business') throw new Error('This business website is unavailable.'); return; }
      const payload = await response.json();
      if (payload.businessName) {
        document.querySelectorAll('[data-business-name]').forEach(element => {
          element.textContent = payload.businessName;
        });
      }
      bindBusiness(payload);
      prepareDom();
      applyDocument(payload.document || {});
      preservePreviewNavigation();
      document.documentElement.hidden = false;
    } catch (error) {
      unavailable(error);
      // Public content remains fully usable from canonical defaults.
    }
  }

  function currentSectionFor(el) {
    return el?.closest?.('[data-cms-section]') || null;
  }

  function businessServiceCardFor(el) {
    if (SITE_KEY !== 'business') return null;
    return el?.closest?.('.card-grid > article.card') || null;
  }

  function ensureOverride(id) {
    if (!pageState().elements[id]) {
      pageState().elements[id] = { style: {} };
    }
    if (!pageState().elements[id].style) pageState().elements[id].style = {};
    return pageState().elements[id];
  }

  function markDirty() {
    dirty = true;
    refreshHistoryControls();
    const status = document.getElementById('legend-cms-status');
    if (status) status.textContent = 'Saving changes…';
    if (editorMode) {
      clearTimeout(autoSaveTimer);
      autoSaveTimer = setTimeout(() => { if (dirty && !saving) void save(false); }, 900);
    }
  }

  function setSelected(el) {
    const previous = selected;
    if (previous && previous !== el) deactivateInlineEditing(previous);
    document.querySelectorAll('.legend-cms-selected').forEach(x => x.classList.remove('legend-cms-selected'));
    selected = el;
    selectedSection = currentSectionFor(el);
    if (selected) {
      selected.classList.add('legend-cms-selected');
      selected.draggable = false;
    }
    syncEditorControls();
    renderSignalControls();
    showPanel('content');
    refreshLayers();
    updateDirectCanvasUi();
  }

  function selectedOverride(create = true) {
    return overrideForElement(selected, create);
  }

  function previewRelativeRect(el) {
    if (!editorPreview || !el?.getBoundingClientRect) return null;
    const rect = el.getBoundingClientRect();
    const previewRect = editorPreview.getBoundingClientRect();
    return {
      left: rect.left - previewRect.left + editorPreview.scrollLeft,
      top: rect.top - previewRect.top + editorPreview.scrollTop,
      width: rect.width,
      height: rect.height
    };
  }

  function positionGridOverlay(section) {
    if (!gridOverlay || !section) return;
    const rect = previewRelativeRect(section);
    if (!rect) return;
    gridOverlay.style.left = `${rect.left}px`;
    gridOverlay.style.top = `${rect.top}px`;
    gridOverlay.style.width = `${rect.width}px`;
    gridOverlay.style.height = `${Math.max(rect.height, 120)}px`;
  }

  function updateDirectCanvasUi() {
    if (!selectionFrame || !editorPreview || !selected || selected.dataset.cmsSignalOnly || selected.closest('.legend-cms-editor')) {
      if (selectionFrame) selectionFrame.hidden = true;
      if (gridOverlay && !directGesture) gridOverlay.hidden = true;
      return;
    }
    const rect = previewRelativeRect(selected);
    if (!rect) { selectionFrame.hidden = true; return; }
    selectionFrame.hidden = false;
    selectionFrame.style.left = `${rect.left}px`;
    selectionFrame.style.top = `${rect.top}px`;
    selectionFrame.style.width = `${Math.max(rect.width, 1)}px`;
    selectionFrame.style.height = `${Math.max(rect.height, 1)}px`;
    selectionFrame.dataset.sectionSelected = selected.dataset.cmsSection ? 'true' : 'false';
    if (directGesture) positionGridOverlay(directGesture.section);
  }

  function lockMobilePreviewHorizontalScroll() {
    if (!editorPreview || innerWidth > 800 || editorPreview.scrollLeft === 0) return;
    editorPreview.scrollLeft = 0;
  }

  function installDirectCanvasControls(preview) {
    editorPreview = preview;
    gridOverlay = document.createElement('div');
    gridOverlay.className = 'legend-cms-grid-overlay';
    gridOverlay.hidden = true;
    gridOverlay.setAttribute('aria-hidden', 'true');
    selectionFrame = document.createElement('div');
    selectionFrame.className = 'legend-cms-selection-frame';
    selectionFrame.hidden = true;
    selectionFrame.innerHTML = `
      <button type="button" class="legend-cms-move-handle" data-cms-gesture="move" aria-label="Move selected content">Move</button>
      <button type="button" class="legend-cms-edge-handle legend-cms-edge-top" data-cms-gesture="resize-y" data-cms-edge="top" aria-label="Resize selected content upward or downward"></button>
      <button type="button" class="legend-cms-edge-handle legend-cms-edge-right" data-cms-gesture="resize-x" data-cms-edge="right" aria-label="Resize selected content left or right"></button>
      <button type="button" class="legend-cms-edge-handle legend-cms-edge-bottom" data-cms-gesture="resize-y" data-cms-edge="bottom" aria-label="Resize selected content upward or downward"></button>
      <button type="button" class="legend-cms-edge-handle legend-cms-edge-left" data-cms-gesture="resize-x" data-cms-edge="left" aria-label="Resize selected content left or right"></button>
      <button type="button" class="legend-cms-edge-handle legend-cms-corner-nw" data-cms-gesture="resize-xy" data-cms-edge="top-left" aria-label="Resize selected content diagonally"></button>
      <button type="button" class="legend-cms-edge-handle legend-cms-corner-ne" data-cms-gesture="resize-xy" data-cms-edge="top-right" aria-label="Resize selected content diagonally"></button>
      <button type="button" class="legend-cms-edge-handle legend-cms-corner-se" data-cms-gesture="resize-xy" data-cms-edge="bottom-right" aria-label="Resize selected content diagonally"></button>
      <button type="button" class="legend-cms-edge-handle legend-cms-corner-sw" data-cms-gesture="resize-xy" data-cms-edge="bottom-left" aria-label="Resize selected content diagonally"></button>`;
    preview.appendChild(gridOverlay);
    preview.appendChild(selectionFrame);

    const beginGesture = (event, mode, edge = '', captureTarget = null) => {
      if (!selected || selected.dataset.cmsSignalOnly) return false;
      const section = selectedSection || currentSectionFor(selected);
      const parent = selected.parentElement;
      if (!section || !parent) return false;
      if (mode === 'move' && selected.dataset.cmsSection) return false;
      const selectedRect = selected.getBoundingClientRect();
      const sectionRect = section.getBoundingClientRect();
      const parentRect = parent.getBoundingClientRect();
      const override = selectedOverride();
      if (!override) return false;
      const gestureStyle = editingStyle(override, true);
      checkpoint();
      directGesture = {
        mode, edge, target: selected, section, parent,
        startX: event.clientX, startY: event.clientY,
        selectedRect, sectionRect, parentRect,
        startWidthPercent: positiveNumber(gestureStyle.widthPercent) ? Number(gestureStyle.widthPercent) : (parentRect.width > 0 ? selectedRect.width / parentRect.width * 100 : 100),
        startHeightPx: positiveNumber(gestureStyle.heightPx) ? Number(gestureStyle.heightPx) : Math.max(selectedRect.height, 24),
        startOffsetXPercent: Number.isFinite(Number(gestureStyle.offsetXPercent)) ? Number(gestureStyle.offsetXPercent) : 0,
        startOffsetYPx: Number.isFinite(Number(gestureStyle.offsetYPx)) ? Number(gestureStyle.offsetYPx) : 0,
        style: gestureStyle,
        changed: false
      };
      deactivateInlineEditing(selected);
      captureTarget?.setPointerCapture?.(event.pointerId);
      gridOverlay.hidden = false;
      gridOverlay.classList.remove('legend-cms-snap-x','legend-cms-snap-y');
      positionGridOverlay(section);
      event.preventDefault();
      event.stopPropagation();
      return true;
    };
    const startGesture = event => {
      if (event.button !== undefined && event.button !== 0) return;
      const handle = event.target.closest?.('[data-cms-gesture]');
      if (!handle) return;
      beginGesture(event, handle.dataset.cmsGesture, handle.dataset.cmsEdge || '', handle);
    };

    selectionFrame.addEventListener('pointerdown', startGesture);
    window.addEventListener('pointermove', event => {
      const gesture = directGesture;
      if (!gesture || selected !== gesture.target) return;
      const dx = event.clientX - gesture.startX;
      const dy = event.clientY - gesture.startY;
      const override = selectedOverride();
      if (!override) return;
      const style = gesture.style;
      const sectionWidth = gesture.sectionRect.width || gesture.parentRect.width || 1;
      const parentWidth = gesture.parentRect.width || sectionWidth || 1;
      const cell = sectionWidth / 12;
      const verticalStep = 24;
      let snappedDx = dx;
      let snappedDy = dy;
      gridOverlay.classList.remove('legend-cms-snap-x','legend-cms-snap-y');

      if (gesture.mode === 'move') {
        // Movement is free-form inside the selected section. The grid is visual
        // guidance only; only near-center alignment gets a soft snap.
        snappedDx = dx;
        snappedDy = dy;
        const desiredCenterX = gesture.selectedRect.left + snappedDx + gesture.selectedRect.width / 2;
        const sectionCenterX = gesture.sectionRect.left + gesture.sectionRect.width / 2;
        if (Math.abs(desiredCenterX - sectionCenterX) <= Math.max(8, cell * .18)) {
          snappedDx += sectionCenterX - desiredCenterX;
          gridOverlay.classList.add('legend-cms-snap-x');
        }
        const desiredCenterY = gesture.selectedRect.top + snappedDy + gesture.selectedRect.height / 2;
        const sectionCenterY = gesture.sectionRect.top + gesture.sectionRect.height / 2;
        if (gesture.sectionRect.height > 0 && Math.abs(desiredCenterY - sectionCenterY) <= 12) {
          snappedDy += sectionCenterY - desiredCenterY;
          gridOverlay.classList.add('legend-cms-snap-y');
        }
        const minDx = gesture.sectionRect.left - gesture.selectedRect.left;
        const maxDx = gesture.sectionRect.right - gesture.selectedRect.right;
        const minDy = gesture.sectionRect.top - gesture.selectedRect.top;
        const maxDy = gesture.sectionRect.bottom - gesture.selectedRect.bottom;
        snappedDx = Math.max(minDx, Math.min(maxDx, snappedDx));
        snappedDy = Math.max(minDy, Math.min(maxDy, snappedDy));
        style.offsetXPercent = Math.round((gesture.startOffsetXPercent + snappedDx / parentWidth * 100) * 1000) / 1000;
        style.offsetYPx = Math.round((gesture.startOffsetYPx + snappedDy) * 1000) / 1000;
      } else {
        if (gesture.mode === 'resize-x' || gesture.mode === 'resize-xy') {
          const fromLeft = gesture.edge.includes('left');
          const widthDelta = (fromLeft ? -dx : dx) / parentWidth * 100;
          const rawWidth = gesture.startWidthPercent + widthDelta;
          const snappedWidth = cell > 0 ? Math.round((rawWidth / 100 * parentWidth) / cell) * cell / parentWidth * 100 : rawWidth;
          const nextWidth = Math.max(5, Math.min(100, Math.round(snappedWidth * 1000) / 1000));
          style.widthPercent = nextWidth;
          if (fromLeft) {
            style.offsetXPercent = Math.round((gesture.startOffsetXPercent + dx / parentWidth * 100) * 1000) / 1000;
          }
        }
        if (gesture.mode === 'resize-y' || gesture.mode === 'resize-xy') {
          const fromTop = gesture.edge.includes('top');
          const heightDelta = fromTop ? -dy : dy;
          style.heightPx = Math.max(24, Math.round((gesture.startHeightPx + heightDelta) / verticalStep) * verticalStep);
          if (fromTop) {
            style.offsetYPx = Math.round((gesture.startOffsetYPx + dy) * 1000) / 1000;
          }
        }
      }
      gesture.changed = true;
      applyElementOverride(selected, override);
      updateDirectCanvasUi();
      event.preventDefault();
    }, { passive: false });

    const finishGesture = () => {
      if (!directGesture) return;
      const changed = directGesture.changed;
      directGesture = null;
      gridOverlay.hidden = true;
      gridOverlay.classList.remove('legend-cms-snap-x','legend-cms-snap-y');
      if (changed) {
        syncEditorControls();
        markDirty();
      }
      updateDirectCanvasUi();
    };
    window.addEventListener('pointerup', finishGesture);
    window.addEventListener('pointercancel', finishGesture);
    preview.addEventListener('scroll', () => {
      lockMobilePreviewHorizontalScroll();
      updateDirectCanvasUi();
    }, { passive: true });
    window.addEventListener('resize', () => {
      lockMobilePreviewHorizontalScroll();
      refreshResponsiveOverrides();
    });
    lockMobilePreviewHorizontalScroll();
    updateDirectCanvasUi();
  }

  function syncEditorControls() {
    const title = document.getElementById('legend-cms-selected-label');
    const inlineHelp = document.getElementById('legend-cms-inline-help');
    const imageGroup = document.getElementById('legend-cms-image-group');
    const codeGroup = document.getElementById('legend-cms-code-group');
    const scale = document.getElementById('legend-cms-scale');
    const width = document.getElementById('legend-cms-width');
    const height = document.getElementById('legend-cms-height');
    const top = document.getElementById('legend-cms-padding-top');
    const bottom = document.getElementById('legend-cms-padding-bottom');
    const offsetX = document.getElementById('legend-cms-offset-x');
    const offsetY = document.getElementById('legend-cms-offset-y');
    const align = document.getElementById('legend-cms-align');
    const hidden = document.getElementById('legend-cms-hidden');

    document.querySelectorAll('[data-cms-view="content"] input,[data-cms-view="content"] textarea,[data-cms-view="content"] select,[data-cms-view="appearance"] input,[data-cms-view="appearance"] select,[data-cms-view="layout"] input,[data-cms-view="layout"] select').forEach(control => { control.disabled = !selected || !!selected.dataset.cmsSignalOnly; });
    if (!selected) {
      if (title) title.textContent = 'Select content on the page';
      if (inlineHelp) inlineHelp.hidden = true;
      if (imageGroup) imageGroup.hidden = true;
      if (codeGroup) codeGroup.hidden = true;
      const duplicateButton = document.getElementById('legend-cms-duplicate'); if (duplicateButton) duplicateButton.disabled = true;
      return;
    }

    if (title) title.textContent = elementLabel(selected);
    const isImage = selected instanceof HTMLImageElement;
    const selectedExtra = selected.dataset.cmsExtraId ? pageState().extras.find(x => x.id === selected.dataset.cmsExtraId) : null;
    const extra = selected.dataset.cmsExtraField ? null : selectedExtra;
    const isCode = selectedExtra?.type === 'code' && !selected.dataset.cmsExtraField;
    if (inlineHelp) inlineHelp.hidden = !isInlineEditable(selected);
    if (imageGroup) imageGroup.hidden = !isImage;
    if (codeGroup) codeGroup.hidden = !isCode;

    const ov = selectedOverride(false) || {};
    const editStyle = editingStyle(ov, false);
    const editLayout = editingLayout(ov, false);
    const computed = getComputedStyle(selected);
    const parentStyle = selected.parentElement ? getComputedStyle(selected.parentElement) : null;
    const parentWidth = selected.parentElement
      ? selected.parentElement.clientWidth - (parseFloat(parentStyle.paddingLeft) || 0) - (parseFloat(parentStyle.paddingRight) || 0) : 0;
    const actualWidth = parentWidth > 0 ? parseFloat(computed.width) / parentWidth * 100 : 100;
    const displayNumber = value => String(Math.round(value * 1000) / 1000);
    const targetInput = document.getElementById('legend-cms-target'); if (targetInput) targetInput.checked = (ov.target ?? selected.getAttribute('target')) === '_blank';
    const serviceCard = businessServiceCardFor(selected);
    const duplicateButton = document.getElementById('legend-cms-duplicate');
    if (duplicateButton) {
      duplicateButton.textContent = serviceCard ? 'Duplicate service' : 'Duplicate selected';
      duplicateButton.disabled = !!selected.dataset.cmsSignalOnly || selected.tagName === 'FORM';
      duplicateButton.title = selected.tagName === 'FORM' ? 'Each page uses one canonical inquiry form.' : '';
    }
    const removeButton = document.getElementById('legend-cms-remove');
    if (removeButton) {
      const kind = serviceCard ? 'service'
        : selected.dataset.cmsSection ? 'section'
        : isCode ? 'code block'
        : selected.tagName === 'IMG' ? 'image'
        : selected.tagName === 'VIDEO' ? 'video'
        : selected.tagName === 'FORM' ? 'form'
        : ['INPUT','SELECT','TEXTAREA'].includes(selected.tagName) ? 'field'
        : ['A','BUTTON'].includes(selected.tagName) ? 'button'
        : ['DIV','ARTICLE','HEADER','FOOTER'].includes(selected.tagName) ? 'block'
        : 'element';
      removeButton.textContent = `Delete ${kind}`;
      removeButton.disabled = false;
    }
    if (scale) scale.value = String(editStyle?.fontScale ?? 1);
    if (width) width.value = displayNumber(editStyle?.widthPercent ?? (Number.isFinite(actualWidth) ? actualWidth : 100));
    if (height) height.value = editStyle?.heightPx != null ? displayNumber(editStyle.heightPx) : '';
    if (top) top.value = displayNumber(editStyle?.paddingTop ?? (parseFloat(computed.paddingTop) || 0));
    if (bottom) bottom.value = displayNumber(editStyle?.paddingBottom ?? (parseFloat(computed.paddingBottom) || 0));
    if (offsetX) offsetX.value = displayNumber(editStyle?.offsetXPercent ?? 0);
    if (offsetY) offsetY.value = displayNumber(editStyle?.offsetYPx ?? 0);
    if (align) align.value = editStyle?.textAlign ?? computed.textAlign ?? '';
    if (scale) scale.disabled = isImage || isCode;
    const values = { href: ov.href ?? rememberOriginal(selected).href ?? '', alt: ov.alt ?? selected.getAttribute('alt') ?? '', videoUrl: ov.videoUrl ?? selected.getAttribute('src') ?? '' };
    Object.entries(values).forEach(([key,value]) => { const input = document.getElementById(`legend-cms-${key}`); if(input) input.value = value; });
    document.querySelectorAll('[data-style-key]').forEach(input => { const key = input.dataset.styleKey; input.value = ['color','backgroundColor'].includes(key) ? colorHex(editStyle?.[key] || computed[key]) : editStyle?.[key] ?? (input.type === 'number' ? parseFloat(computed[key]) || '' : computed[key] || ''); });
    document.querySelectorAll('[data-color-hex]').forEach(input => { input.value = colorHex(editStyle?.[input.dataset.colorHex] || computed[input.dataset.colorHex]); });
    const linkGroup = document.getElementById('legend-cms-link-group'); if (linkGroup) linkGroup.hidden = selected.tagName !== 'A';
    if (selected.tagName === 'A') syncCtaControls(ov, values.href);
    const videoGroup = document.getElementById('legend-cms-video-group'); if (videoGroup) videoGroup.hidden = selected.tagName !== 'VIDEO';
    const layoutMode = document.getElementById('legend-cms-layout-mode'); if (layoutMode) layoutMode.value = editLayout?.mode || 'free';
    const layoutDirection = document.getElementById('legend-cms-layout-direction'); if (layoutDirection) layoutDirection.value = editLayout?.direction || 'column';
    const layoutGap = document.getElementById('legend-cms-layout-gap'); if (layoutGap) layoutGap.value = editLayout?.gapPx ?? '';
    const layoutColumns = document.getElementById('legend-cms-layout-columns'); if (layoutColumns) layoutColumns.value = editLayout?.columns ?? '';
    const layoutMin = document.getElementById('legend-cms-layout-min'); if (layoutMin) layoutMin.value = editLayout?.minItemWidthPx ?? '';
    const layoutAlign = document.getElementById('legend-cms-layout-align'); if (layoutAlign) layoutAlign.value = editLayout?.alignItems || '';
    const layoutJustify = document.getElementById('legend-cms-layout-justify'); if (layoutJustify) layoutJustify.value = editLayout?.justifyContent || '';
    const layoutWrap = document.getElementById('legend-cms-layout-wrap'); if (layoutWrap) layoutWrap.value = editLayout?.wrap || '';
    if (hidden) hidden.checked = ov.hidden === true || selected.hidden;
  }

  function updateSelectedFromControls(event) {
    if (!selected) return;
    const control = event.target;
    if (control.validity?.badInput) return;
    const fields = {
      'legend-cms-scale': 'fontScale',
      'legend-cms-width': 'widthPercent',
      'legend-cms-height': 'heightPx',
      'legend-cms-padding-top': 'paddingTop',
      'legend-cms-padding-bottom': 'paddingBottom',
      'legend-cms-offset-x': 'offsetXPercent',
      'legend-cms-offset-y': 'offsetYPx'
    };
    const field = fields[control.id];
    if (field && control.value !== '') {
      const value = Number(control.value);
      const signed = field === 'offsetXPercent' || field === 'offsetYPx';
      const valid = signed ? Number.isFinite(value) : field.startsWith('padding') ? spacingNumber(value) : positiveNumber(value);
      if (!valid) {
        control.setCustomValidity(signed ? 'Enter a finite position value.' : 'Enter a finite ' + (field.startsWith('padding') ? 'nonnegative' : 'positive') + ' number.');
        return;
      }
    }
    control.setCustomValidity('');
    checkpoint();
    const ov = selectedOverride();
    if (!ov) return;
    if (control.id === 'legend-cms-hidden') {
      ov.hidden = control.checked;
      selected.hidden = control.checked;
    } else {
      const style = editingStyle(ov, true);
      if (field) {
        if (control.value === '') delete style[field];
        else style[field] = Number(control.value);
      } else if (control.id === 'legend-cms-align') {
        if (control.value) style.textAlign = control.value;
        else delete style.textAlign;
      } else return;
      applyElementOverride(selected, ov);
    }
    updateDirectCanvasUi();
    markDirty();
  }

  async function uploadMedia(file) {
    if (!file) return null;
    const status = document.getElementById('legend-cms-status');
    if (status) status.textContent = 'Uploading media…';
    try {
      const body = new FormData(); body.append('ticket', editorTicket); body.append('file', file);
      const response = await fetch(`${API_BASE}/api/website-content/manage/media`, { method: 'POST', body });
      const result = await response.json();
      if (!response.ok || !result.url) throw new Error(result.message || result.error || 'Upload failed.');
      if (status) status.textContent = 'Media uploaded; save your draft to retain placement';
      return result.url;
    } catch (error) { if (status) status.textContent = error.message; return null; }
  }
  async function readImage(file, callback) {
    if (!file) return;
    if (!/^image\/(jpeg|png|webp)$/i.test(file.type)) { alert('Use a JPEG, PNG, or WebP image.'); return; }
    const url = await uploadMedia(file); if (url) callback(url);
  }

  function moveSelectedSection(delta) {
    if (!selectedSection || selectedSection.matches('.site-header,.site-footer')) return;
    const sections=pageLayerSections();
    const index=sections.indexOf(selectedSection);
    const target=index+delta;
    if(index<0 || target<0 || target>=sections.length) return;
    const targetSection=sections[target];
    if(targetSection.parentElement!==selectedSection.parentElement) return;
    checkpoint();
    if(delta<0) selectedSection.parentElement.insertBefore(selectedSection,targetSection);
    else selectedSection.parentElement.insertBefore(targetSection,selectedSection);
    syncSectionOrderFromDom();
    markDirty();
    refreshLayers();
  }

  function addImage(file) {
    if (!selectedSection) {
      alert('Select content inside the section where you want the new image.');
      return;
    }
    readImage(file, dataUrl => {
      checkpoint();
      const extra = {
        id: crypto.randomUUID ? crypto.randomUUID() : String(Date.now()),
        sectionId: selectedSection.dataset.cmsSection,
        type: 'image',
        imageDataUrl: dataUrl,
        style: { widthPercent: 70, paddingTop: 16, paddingBottom: 16 }
      };
      pageState().extras.push(extra);
      const el = createExtra(extra);
      setSelected(el);
      markDirty();
    });
  }

  function removeSelected() {
    if (!selected) return;
    checkpoint();
    if (selected.dataset.cmsSignalOnly) { markDeleted(pageElementDeletionKey(selected.dataset.cmsId)); delete pageState().elements[selected.dataset.cmsId]; markDirty(); renderSignalControls(); return; }
    if (selected.dataset.cmsExtraId) {
      const removedId = selected.dataset.cmsExtraId;
      const removedSection = selected.dataset.cmsSection;
      const removedExtras = pageState().extras.filter(x => x.id === removedId || (removedSection && (x.sectionId === removedSection || x.placement?.sectionId === removedSection)));
      removedExtras.forEach(extra => markDeleted(pageExtraDeletionKey(extra.id)));
      pageState().extras = pageState().extras.filter(x => !removedExtras.includes(x));
      const removedNode = document.querySelector(`[data-cms-id="extra:${CSS.escape(removedId)}"]`) || selected;
      scaledElements.delete(removedNode);
      removedNode.remove();
      setSelected(null);
      markDirty();
      return;
    }
    markDeleted(pageElementDeletionKey(selected.dataset.cmsId));
    delete pageState().elements[selected.dataset.cmsId];
    const original = rememberOriginal(selected);
    selected.hidden = original.hidden;
    if (selected instanceof HTMLImageElement) selected.src = original.src || '';
    else if (!selected.dataset.cmsSection && !['DIV','ARTICLE','HEADER','FOOTER'].includes(selected.tagName)) { setContentText(selected, original.text); }
    applyStyle(selected, null);
    syncEditorControls();
    markDirty();
  }

  let saving = false;
  async function save(publish = false, namedDraft = null) {
    if (saving) return;
    clearTimeout(autoSaveTimer);
    const status = document.getElementById('legend-cms-status');
    if (publish && dirty) { await save(false); if (dirty) return; }
    if (status) status.textContent = publish ? 'Publishing…' : 'Saving draft…';
    saving = true;
    const submitted = JSON.stringify(documentState);
    const submittedDeletedKeys = [...pendingDeletedKeys];
    let saved = false;
    try {
      const response = await fetch(`${API_BASE}/api/website-content/${publish ? 'manage/publish' : 'manage'}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ticket: editorTicket, document: JSON.parse(submitted), expectedRevision: revision, deletedKeys: submittedDeletedKeys, ...(namedDraft || {}) })
      });
      if (!response.ok) { const error = await response.json().catch(() => ({})); throw new Error(error.message || error.error || `Save failed (${response.status})`); }
      const payload = await response.json();
      const changedDuringSave = JSON.stringify(documentState) !== submitted;
      if (!changedDuringSave) documentState = normalizeDocument(payload.document || documentState);
      revision = payload.revision ?? revision;
      namedDrafts = payload.drafts || namedDrafts;
      submittedDeletedKeys.forEach(key => pendingDeletedKeys.delete(key));
      dirty = changedDuringSave;
      saved = true;
      if (status) status.textContent = changedDuringSave ? 'Draft saved; newer edits remain unsaved' : publish ? 'Published' : 'Draft saved';
    } catch (error) {
      if (status) status.textContent = error?.message || 'Save failed';
    } finally {
      saving = false;
      if (saved && dirty) {
        clearTimeout(autoSaveTimer);
        autoSaveTimer = setTimeout(() => { if (dirty && !saving) void save(false); }, 900);
      }
    }
    return saved;
  }

  let revision = null;
  let namedDrafts = [];
  function chooseDraft() {
    if (saving || document.getElementById('legend-cms-draft-dialog')) return;
    clearTimeout(autoSaveTimer);
    const dialog = document.createElement('dialog'); dialog.id = 'legend-cms-draft-dialog'; dialog.className = 'legend-cms-panel legend-cms-editor legend-cms-draft-dialog';
    const title = document.createElement('h2'); title.textContent = 'Save website draft';
    const label = document.createElement('label'); label.textContent = 'Save to';
    const select = document.createElement('select');
    const fresh = document.createElement('option'); fresh.value = ''; fresh.textContent = 'Create a new draft'; select.appendChild(fresh);
    namedDrafts.forEach(draft => { const option = document.createElement('option'); option.value = draft.id; option.textContent = draft.name; select.appendChild(option); });
    label.appendChild(select);
    const nameLabel = document.createElement('label'); nameLabel.textContent = 'Draft name';
    const name = document.createElement('input'); name.id = 'legend-cms-draft-name'; name.type = 'text'; name.maxLength = 100; name.required = true; nameLabel.appendChild(name);
    select.addEventListener('change', () => { name.value = namedDrafts.find(d => d.id === select.value)?.name || ''; });
    const feedback = document.createElement('p'); feedback.setAttribute('role','status');
    const cancel = document.createElement('button'); cancel.type = 'button'; cancel.textContent = 'Cancel'; cancel.addEventListener('click', () => dialog.close());
    const submit = document.createElement('button'); submit.id = 'legend-cms-draft-submit'; submit.type = 'button'; submit.textContent = 'Save draft';
    submit.addEventListener('click', async () => {
      if (!name.value.trim()) { name.reportValidity(); return; }
      submit.disabled = true;
      const saved = await save(false, { draftId: select.value || null, draftName: name.value.trim() });
      submit.disabled = false;
      if (saved) dialog.close(); else feedback.textContent = document.getElementById('legend-cms-status')?.textContent || 'Draft could not be saved.';
    });
    dialog.appendChild(title); dialog.appendChild(label); dialog.appendChild(nameLabel); dialog.appendChild(feedback); dialog.appendChild(submit); dialog.appendChild(cancel);
    dialog.addEventListener('close', () => { dialog.remove(); if (dirty) autoSaveTimer = setTimeout(() => save(false), 900); });
    document.body.appendChild(dialog); dialog.showModal(); name.focus();
  }
  const undoStack = [], redoStack = [];
  const baselineNodes = new Map();
  function historySnapshot() {
    return JSON.stringify({ document: documentState, deletedKeys: [...pendingDeletedKeys] });
  }
  function checkpoint() {
    undoStack.push(historySnapshot());
    if (undoStack.length > 80) undoStack.shift();
    redoStack.length = 0;
  }
  function restoreHistory(from, to) {
    if (!from.length) return;
    to.push(historySnapshot());
    baselineNodes.forEach(({ el, parent, next }) => { if (el.dataset.cmsSignalOnly) return; if (parent) parent.insertBefore(el, next?.parentElement === parent ? next : null); const original = rememberOriginal(el); el.hidden = original.hidden; if (!el.dataset.cmsSection && !['DIV','ARTICLE','HEADER','FOOTER'].includes(el.tagName)) { setContentText(el, original.text); } if (original.href != null) el.setAttribute('href',original.href); if (original.src != null) el.setAttribute('src',original.src); applyStyle(el, null); });
    const snapshot = JSON.parse(from.pop());
    pendingDeletedKeys.clear();
    for (const key of snapshot.deletedKeys || []) pendingDeletedKeys.add(key);
    applyDocument(snapshot.document || snapshot);
    setSelected(null);
    markDirty();
  }
  function safeUrl(value, media = false) {
    if (typeof value !== 'string' || !value.trim() || /[\u0000-\u0020\\]/.test(value) || /(?:legendEdit|ticket)=/i.test(value)) return false;
    if (!media && (value.startsWith('#') || (value.startsWith('/') && !value.startsWith('//')))) return true;
    try { const url = new URL(value); return media ? url.protocol === 'https:' : ['https:','mailto:','tel:'].includes(url.protocol); } catch { return false; }
  }
  function applyPlacement(el, placement) {
    if (!el || !placement || el.dataset.cmsSection) return;
    const section = document.querySelector(`[data-cms-section="${CSS.escape(placement.sectionId || '')}"]`);
    if (!section || el.contains?.(section)) return;
    const anchor = placement.beforeId ? document.querySelector(`[data-cms-id="${CSS.escape(placement.beforeId)}"]`) : null;
    const container = placement.containerId ? document.querySelector(`[data-cms-id="${CSS.escape(placement.containerId)}"]`) : null;
    // Preserve the actual destination's flow instead of extracting a heading or button
    // into an unrelated grid at the end of its section.
    if (placement.flow === true && container && section.contains(container) && !el.contains(container) && !container.closest(lockedSelector)) {
      container.insertBefore(el, anchor?.parentElement === container && anchor !== el ? anchor : null);
      el.style.removeProperty('--cms-column'); el.style.removeProperty('--cms-span');
      return;
    }
    let frame = Array.from(section.children).find(x => x.classList.contains('cms-layout-frame'));
    if (!frame) { frame = document.createElement('div'); frame.className = 'cms-layout-frame'; section.appendChild(frame); }
    const before = placement.beforeId ? document.querySelector(`[data-cms-id="${CSS.escape(placement.beforeId)}"]`) : null;
    frame.insertBefore(el, before?.parentElement === frame && before !== el ? before : null);
    const column = Math.min(12, Math.max(1, Number(placement.column) || 1));
    const span = Math.min(13 - column, Math.max(1, Number(placement.span) || 12));
    el.style.setProperty('--cms-column', String(column)); el.style.setProperty('--cms-span', String(span)); el.style.minWidth = '0'; el.style.maxWidth = '100%'; el.style.overflowWrap = 'anywhere';
  }
  function approvedDataSources() {
    return SITE_KEY === 'business' && Array.isArray(managementPayload?.dataCatalog) ? managementPayload.dataCatalog : [];
  }

  function sourceForCollection(collectionId) {
    const collection=documentState.collections?.[collectionId];
    return collection ? approvedDataSources().find(source=>source.key===collection.source) || null : null;
  }

  function ensureCollectionForSource(sourceKey, fields = []) {
    const source=approvedDataSources().find(value=>value.key===sourceKey);
    if(!source) return null;
    documentState.collections ||= {};
    let collection=Object.values(documentState.collections).find(value=>value?.source===sourceKey);
    if(!collection){ collection={id:sourceKey,name:source.label,source:sourceKey,fields:[]}; documentState.collections[sourceKey]=collection; }
    collection.fields ||= [];
    for(const field of fields) if(source.fields?.includes(field) && !collection.fields.includes(field)) collection.fields.push(field);
    return collection;
  }

  function setSelectOptions(select, values, current, emptyLabel = null) {
    if(!select) return;
    select.replaceChildren();
    if(emptyLabel!=null){ const empty=document.createElement('option'); empty.value=''; empty.textContent=emptyLabel; select.appendChild(empty); }
    for(const [value,label] of values){ const option=document.createElement('option'); option.value=value; option.textContent=label; select.appendChild(option); }
    if(current!=null) select.value=current;
  }

  function renderDataControls() {
    const business=document.getElementById('legend-cms-data-business');
    const notice=document.getElementById('legend-cms-data-unavailable');
    if(business) business.hidden=SITE_KEY!=='business';
    if(notice) notice.hidden=SITE_KEY==='business';
    if(SITE_KEY!=='business') return;
    const sources=approvedDataSources();
    const override=selectedOverride?.() || null;
    const binding=override?.dataBinding || null;
    const boundSource=sourceForCollection(binding?.collectionId);
    const sourceSelect=document.getElementById('legend-cms-data-source');
    const sourceKey=boundSource?.key || sourceSelect?.value || sources[0]?.key || '';
    setSelectOptions(sourceSelect,sources.map(source=>[source.key,source.label]),sourceKey,'Choose source');
    const source=sources.find(value=>value.key===sourceSelect?.value) || sources.find(value=>value.key===sourceKey);
    const fieldSelect=document.getElementById('legend-cms-data-field');
    setSelectOptions(fieldSelect,(source?.fields||[]).map(field=>[field,field.replaceAll(/([A-Z])/g,' $1').replace(/^./,m=>m.toUpperCase())]),binding?.field || fieldSelect?.value || source?.fields?.[0] || '');
    const target=document.getElementById('legend-cms-data-target'); if(target) target.value=binding?.target || target.value || 'text';
    const bindButton=document.getElementById('legend-cms-data-bind'); if(bindButton) bindButton.disabled=!selected || !!selected.dataset.cmsSignalOnly;
    const clearButton=document.getElementById('legend-cms-data-clear'); if(clearButton) clearButton.disabled=!binding;
    const status=document.getElementById('legend-cms-data-status');
    if(status) status.textContent=binding ? `Bound to ${boundSource?.label || binding.collectionId} · ${binding.field}.` : selected?'Selected content is not data-bound.':'Select content on the page to bind it.';

    const page=pageState();
    const dynamic=page.dynamicBinding || null;
    const listSources=sources.filter(value=>value.isList===true);
    const dynamicSource=sourceForCollection(dynamic?.collectionId);
    const dynamicSelect=document.getElementById('legend-cms-dynamic-source');
    setSelectOptions(dynamicSelect,listSources.map(value=>[value.key,value.label]),dynamicSource?.key || '','Static page');
    const chosenDynamic=listSources.find(value=>value.key===dynamicSelect?.value) || dynamicSource;
    const keySelect=document.getElementById('legend-cms-dynamic-key');
    setSelectOptions(keySelect,(chosenDynamic?.fields||[]).map(field=>[field,field]),dynamic?.itemKeyField || (chosenDynamic?.fields?.includes('slug')?'slug':chosenDynamic?.fields?.includes('id')?'id':chosenDynamic?.fields?.[0]||''));
    const pattern=document.getElementById('legend-cms-dynamic-pattern');
    if(pattern) pattern.value=dynamic?.routePattern || `${currentPageRoute()==='/'?'/items':currentPageRoute()}/{item}`;
    const preview=document.getElementById('legend-cms-dynamic-preview');
    const projection=dynamic?.collectionId ? collectionData.get(dynamic.collectionId) : chosenDynamic ? collectionData.get(chosenDynamic.key) : null;
    const items=Array.isArray(projection?.items)?projection.items:[];
    setSelectOptions(preview,items.map(item=>[item.key,String(item.fields?.name||item.key)]),dynamicCollectionItem?.collectionId===dynamic?.collectionId?dynamicCollectionItem.key:'','Preview item');
    const dynamicStatus=document.getElementById('legend-cms-dynamic-status');
    if(dynamicStatus) dynamicStatus.textContent=dynamic ? `Dynamic route ${dynamic.routePattern || ''} from ${dynamicSource?.label || dynamic.collectionId}.` : 'This page is static.';
  }
  function showPanel(name) {
    document.querySelectorAll('[data-cms-view]').forEach(view => { view.hidden = view.dataset.cmsView !== name; });
    document.querySelectorAll('[data-open]').forEach(button => button.setAttribute('aria-pressed', String(button.dataset.open === name)));
    if (name === 'layers') refreshLayers();
    if (name === 'media') void refreshMediaLibrary();
    if (name === 'components') renderReusableComponents();
    if (name === 'data') renderDataControls();
    if (name === 'ai') renderAiProposal();
    if (name === 'motion') renderMotionControls();
    if (name === 'page') syncPageControls();
    if (name === 'signals') renderSignalControls();
    if (name === 'quality') void refreshQualityInspector();
    if (name === 'collaboration') void refreshCollaboration();
  }


  function collaborationSelectedElementId() {
    if (!selected) return null;
    return selected.dataset.cmsExtraId ? `extra:${selected.dataset.cmsExtraId}` : selected.dataset.cmsId || null;
  }

  function collaborationAuthorLabel(comment) {
    const role = comment?.authorRole ? String(comment.authorRole).replaceAll('_',' ') : 'editor';
    return comment?.authorEmail ? `${comment.authorEmail} · ${role}` : role;
  }

  async function ensureSavedForCollaboration() {
    if (!dirty) return true;
    const role = document.getElementById('legend-cms-collaboration-role');
    if (role) role.textContent='Saving the current draft before anchoring this comment…';
    const saved = await save(false);
    return Boolean(saved && !dirty);
  }

  async function refreshCollaboration() {
    const commentsHost=document.getElementById('legend-cms-collaboration-comments');
    const rosterHost=document.getElementById('legend-cms-collaboration-roster');
    const roleHost=document.getElementById('legend-cms-collaboration-role');
    if(!commentsHost || !rosterHost || !editorTicket) return;
    commentsHost.replaceChildren();
    rosterHost.replaceChildren();
    if(roleHost) roleHost.textContent='Loading collaboration…';
    try{
      const url=new URL(`${API_BASE}/api/website-content/manage/collaboration`);
      url.searchParams.set('ticket',editorTicket);
      url.searchParams.set('pagePath',currentPageRoute());
      const response=await fetch(url,{cache:'no-store'});
      const payload=await response.json().catch(()=>({}));
      if(!response.ok) throw new Error(payload.message || payload.error || `Collaboration failed (${response.status})`);
      if(payload.source!=='website_studio_collaboration') throw new Error('Collaboration response was invalid.');
      const role=payload.role || {};
      if(roleHost) roleHost.textContent=`${role.label || role.roleKey || 'Editor'} · ${role.canPublish?'can publish':'review/edit only'} · revision ${payload.revision}`;
      for(const person of payload.collaborators || []){
        const row=document.createElement('div'); row.className='legend-cms-collaborator';
        const name=document.createElement('strong'); name.textContent=person.displayName || 'Website collaborator';
        const meta=document.createElement('small'); meta.textContent=`${person.roleKey || 'member'} · ${person.canPublish?'publisher':'editor'}`;
        row.append(name,meta); rosterHost.appendChild(row);
      }
      if(!(payload.collaborators || []).length){
        const empty=document.createElement('p'); empty.textContent='No other website collaborators are currently assigned.'; rosterHost.appendChild(empty);
      }

      const comments=payload.comments || [];
      const children=new Map();
      for(const comment of comments){
        const key=comment.parentCommentId || 'root';
        if(!children.has(key)) children.set(key,[]);
        children.get(key).push(comment);
      }
      const renderComment=(comment,depth=0)=>{
        const row=document.createElement('article'); row.className='legend-cms-comment'; row.dataset.depth=String(depth);
        const header=document.createElement('div'); header.className='legend-cms-comment-head';
        const who=document.createElement('strong'); who.textContent=collaborationAuthorLabel(comment);
        const status=document.createElement('span'); status.textContent=comment.status || 'open';
        header.append(who,status);
        const body=document.createElement('p'); body.textContent=comment.body || '';
        const meta=document.createElement('small');
        meta.textContent=`Revision ${comment.anchorRevision} · ${comment.elementId || 'page'} · ${comment.createdUtc || ''}`;
        const actions=document.createElement('div'); actions.className='legend-cms-row';
        if(comment.elementId){
          const locate=document.createElement('button'); locate.type='button'; locate.textContent='Select on page';
          locate.addEventListener('click',()=>{
            const node=document.querySelector(`[data-cms-id="${CSS.escape(comment.elementId)}"]`);
            if(node){ setSelected(node); node.scrollIntoView?.({block:'center',behavior:'smooth'}); }
          });
          actions.appendChild(locate);
        }
        if(!comment.parentCommentId){
          const reply=document.createElement('button'); reply.type='button'; reply.textContent='Reply';
          reply.addEventListener('click',()=>{
            collaborationReplyTo=comment.id;
            const replyStatus=document.getElementById('legend-cms-collaboration-reply');
            if(replyStatus) replyStatus.textContent=`Replying to ${collaborationAuthorLabel(comment)}`;
            document.getElementById('legend-cms-collaboration-body')?.focus();
          });
          actions.appendChild(reply);
        }
        if(comment.canResolve){
          const toggle=document.createElement('button'); toggle.type='button'; toggle.textContent=comment.status==='resolved'?'Reopen':'Resolve';
          toggle.addEventListener('click',()=>void setCollaborationCommentStatus(comment.id,comment.status==='resolved'?'open':'resolved'));
          actions.appendChild(toggle);
        }
        row.append(header,body,meta,actions); commentsHost.appendChild(row);
        for(const child of children.get(comment.id) || []) renderComment(child,depth+1);
      };
      for(const comment of children.get('root') || []) renderComment(comment,0);
      if(!comments.length){
        const empty=document.createElement('p'); empty.textContent='No review comments on this page yet.'; commentsHost.appendChild(empty);
      }
    }catch(error){
      if(roleHost) roleHost.textContent=error?.message || 'Unable to load collaboration.';
      commentsHost.replaceChildren();
    }
  }

  async function createCollaborationComment(pageOnly=false) {
    const input=document.getElementById('legend-cms-collaboration-body');
    const body=input?.value?.trim() || '';
    if(!body) return;
    if(!await ensureSavedForCollaboration()) return;
    const response=await fetch(`${API_BASE}/api/website-content/manage/collaboration/comments`,{
      method:'POST',
      headers:{'Content-Type':'application/json'},
      body:JSON.stringify({
        ticket:editorTicket,
        expectedRevision:revision,
        pagePath:currentPageRoute(),
        elementId:pageOnly?null:collaborationSelectedElementId(),
        body,
        parentCommentId:collaborationReplyTo
      })
    });
    const payload=await response.json().catch(()=>({}));
    if(!response.ok){
      const role=document.getElementById('legend-cms-collaboration-role');
      if(role) role.textContent=payload.message || payload.error || `Comment failed (${response.status})`;
      return;
    }
    if(input) input.value='';
    collaborationReplyTo=null;
    const replyStatus=document.getElementById('legend-cms-collaboration-reply'); if(replyStatus) replyStatus.textContent='';
    await refreshCollaboration();
  }

  async function setCollaborationCommentStatus(commentId,status) {
    const response=await fetch(`${API_BASE}/api/website-content/manage/collaboration/comments/status`,{
      method:'POST',
      headers:{'Content-Type':'application/json'},
      body:JSON.stringify({ticket:editorTicket,commentId,status})
    });
    if(response.ok) await refreshCollaboration();
  }

  async function refreshMediaLibrary() {
    const grid=document.getElementById('legend-cms-media-grid');
    const status=document.getElementById('legend-cms-media-status');
    if(!grid || !editorTicket) return;
    if(status) status.textContent='Loading website media…';
    try {
      const url=new URL(`${API_BASE}/api/website-content/manage/media`);
      url.searchParams.set('ticket',editorTicket);
      const search=document.getElementById('legend-cms-media-search')?.value?.trim();
      const kind=document.getElementById('legend-cms-media-kind')?.value || 'all';
      if(search) url.searchParams.set('q',search);
      url.searchParams.set('kind',kind);
      const response=await fetch(url,{cache:'no-store'});
      if(!response.ok) throw new Error(`Media library failed (${response.status})`);
      const payload=await response.json();
      if(!Array.isArray(payload.assets)) throw new Error('Media library response was invalid.');
      grid.replaceChildren();
      for(const asset of payload.assets){
        const card=document.createElement('article'); card.className='legend-cms-media-card';
        const preview=asset.contentType?.startsWith('image/')?document.createElement('img'):document.createElement('video');
        preview.src=mediaUrl(asset.url);
        if(preview.tagName==='IMG') preview.alt=asset.name || 'Website media';
        else { preview.muted=true; preview.preload='metadata'; }
        const name=document.createElement('strong'); name.textContent=asset.name || asset.contentType || 'Website media';
        const meta=document.createElement('small'); meta.textContent=`${asset.contentType || 'file'} · ${Math.max(1,Math.round((Number(asset.sizeBytes)||0)/1024))} KB`;
        const use=document.createElement('button'); use.type='button'; use.textContent='Use this asset';
        use.addEventListener('click',()=>useMediaAsset(asset));
        card.append(preview,name,meta,use); grid.appendChild(card);
      }
      if(!payload.assets.length){ const empty=document.createElement('p'); empty.textContent='No media matches this filter.'; grid.appendChild(empty); }
      if(status) status.textContent=`${payload.assets.length} asset${payload.assets.length===1?'':'s'} in this website scope.`;
    } catch(error) {
      grid.replaceChildren();
      if(status) status.textContent=error?.message || 'Unable to load website media.';
    }
  }

  function useMediaAsset(asset) {
    if(!asset?.url || !asset?.contentType) return;
    const isImage=asset.contentType.startsWith('image/');
    const isVideo=asset.contentType.startsWith('video/');
    if(!isImage && !isVideo) return;
    if(selected && ((isImage && selected instanceof HTMLImageElement) || (isVideo && selected.tagName==='VIDEO'))){
      checkpoint(); const override=selectedOverride(); if(!override) return;
      if(isImage) override.imageDataUrl=asset.url; else override.videoUrl=asset.url;
      applyElementOverride(selected,override); syncEditorControls(); markDirty(); return;
    }
    const section=selectedSection || document.querySelector('[data-cms-section]');
    if(!section){ alert('Select a section before inserting media.'); return; }
    checkpoint();
    const extra={id:crypto.randomUUID(),type:isImage?'image':'video',sectionId:section.dataset.cmsSection,style:{widthPercent:isImage?70:100}};
    if(isImage){ extra.imageDataUrl=asset.url; extra.alt=asset.name || ''; } else extra.videoUrl=asset.url;
    pageState().extras.push(extra); const created=createExtra(extra); setSelected(created); markDirty();
  }
  function liveQualityChecks() {
    const checks = [];
    const main = document.querySelector('main');
    const headings = main ? [...main.querySelectorAll('h1:not([hidden])')] : [];
    if (headings.length === 0) checks.push({ code:'live_h1_missing', severity:'warning', message:'The rendered page has no visible H1 heading.' });
    if (headings.length > 1) checks.push({ code:'live_h1_multiple', severity:'warning', message:`The rendered page has ${headings.length} visible H1 headings.` });

    const ids = new Map();
    document.querySelectorAll('[id]').forEach(node => {
      const id = node.id?.trim();
      if (!id || node.closest('.legend-cms-editor')) return;
      ids.set(id, (ids.get(id) || 0) + 1);
    });
    for (const [id, count] of ids) if (count > 1)
      checks.push({ code:'live_duplicate_id', severity:'error', message:`Duplicate rendered id "${id}" appears ${count} times.` });

    document.querySelectorAll('main img:not([hidden])').forEach(image => {
      if (!image.getAttribute('alt')?.trim())
        checks.push({ code:'live_image_alt_missing', severity:'warning', message:'A rendered image is missing alternative text.', elementId:image.dataset.cmsId || image.id || null });
    });

    document.querySelectorAll('main a:not([hidden])').forEach(link => {
      const href = link.getAttribute('href')?.trim();
      if (!href || href === '#')
        checks.push({ code:'live_link_destination_missing', severity:'warning', message:'A rendered link has no working destination.', elementId:link.dataset.cmsId || link.id || null });
    });

    document.querySelectorAll('main input:not([type="hidden"]),main select,main textarea').forEach(control => {
      const labelled = !!control.getAttribute('aria-label')?.trim()
        || !!control.getAttribute('aria-labelledby')?.trim()
        || !!control.closest('label')
        || (!!control.id && !!document.querySelector(`label[for="${CSS.escape(control.id)}"]`));
      if (!labelled)
        checks.push({ code:'live_control_label_missing', severity:'warning', message:'A rendered form control has no accessible label.', elementId:control.dataset.cmsId || control.id || null });
    });

    document.querySelectorAll('main [data-cms-editable="true"]').forEach(node => {
      if (node.hidden) return;
      if (Number(node.scrollWidth) > Number(node.clientWidth) + 1)
        checks.push({ code:'live_horizontal_overflow', severity:'warning', message:'Rendered content overflows its visible width.', elementId:node.dataset.cmsId || null });
    });
    return checks;
  }

  function renderQualityChecks(host, checks, emptyMessage) {
    if (!host?.replaceChildren) return;
    host.replaceChildren();
    if (!checks?.length) {
      const empty=document.createElement('p'); empty.className='legend-cms-quality-ok'; empty.textContent=emptyMessage; host.appendChild(empty); return;
    }
    for (const check of checks) {
      const row=document.createElement('div'); row.className=`legend-cms-quality-item legend-cms-quality-${check.severity || 'info'}`;
      const badge=document.createElement('strong'); badge.textContent=(check.severity || 'info').toUpperCase();
      const message=document.createElement('span'); message.textContent=check.message || check.code || 'Quality observation';
      row.append(badge,message); host.appendChild(row);
    }
  }

  async function refreshQualityInspector() {
    const savedHost=document.getElementById('legend-cms-quality-saved');
    const liveHost=document.getElementById('legend-cms-quality-live');
    const savedMeta=document.getElementById('legend-cms-quality-saved-meta');
    const liveMeta=document.getElementById('legend-cms-quality-live-meta');
    const liveChecks=liveQualityChecks();
    renderQualityChecks(liveHost,liveChecks,'No rendered-canvas issues detected by the current checks.');
    if (liveMeta) liveMeta.textContent=`Live page checks (rendered canvas) · ${liveChecks.length} observation${liveChecks.length===1?'':'s'} · not a publish authorization`;
    if (!editorTicket || !savedHost) return;
    savedHost.textContent='Checking the saved server draft…';
    if (savedMeta) savedMeta.textContent='Saved draft checks (server) · loading';
    try {
      const url=new URL(`${API_BASE}/api/website-content/manage/quality`);
      url.searchParams.set('ticket',editorTicket);
      const response=await fetch(url,{cache:'no-store'});
      if(!response.ok) throw new Error(`Quality check failed (${response.status})`);
      const payload=await response.json();
      if(payload.source!=='saved_draft_server' || !Array.isArray(payload.checks)) throw new Error('Saved-draft quality response was invalid.');
      renderQualityChecks(savedHost,payload.checks,'No saved-draft issues detected by the server checks.');
      if(savedMeta) savedMeta.textContent=`Saved draft checks (server) · revision ${payload.revision} · ${payload.errorCount||0} errors · ${payload.warningCount||0} warnings`;
    } catch(error) {
      renderQualityChecks(savedHost,[{severity:'error',message:error?.message || 'Unable to inspect the saved draft.'}],'');
      if(savedMeta) savedMeta.textContent='Saved draft checks (server) · unavailable';
    }
  }

  function elementLabel(el) {
    const kind = el.dataset.cmsSection ? 'Section' : ({ A: 'Link', IMG: 'Image', VIDEO: 'Video', H1: 'Heading', H2: 'Heading', H3: 'Heading', P: 'Text' }[el.tagName] || 'Block');
    const label = (el.getAttribute('alt') || el.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 64);
    return label ? `${kind} · ${label}` : kind;
  }

  function refreshLayers() {
    const list = document.getElementById('legend-cms-layers');
    if (!list?.replaceChildren) return;
    list.replaceChildren();
    const filter = (document.getElementById('legend-cms-layer-search')?.value || '').trim().toLowerCase();
    const sections=pageLayerSections();
    sections.forEach((section,index) => {
      const heading=section.querySelector('h1,h2,h3');
      const title=(heading?.textContent || '').trim().replace(/\s+/g,' ').slice(0,64);
      const label=title ? `Section ${index+1} · ${title}` : `Section ${index+1}`;
      if(filter && !label.toLowerCase().includes(filter)) return;
      const row=document.createElement('div');
      row.className='legend-cms-layer legend-cms-section-layer';
      row.draggable=true;
      row.dataset.sectionId=section.dataset.cmsSection || '';
      const drag=document.createElement('span');
      drag.className='legend-cms-layer-drag';
      drag.textContent='⋮⋮';
      drag.setAttribute('aria-hidden','true');
      const select=document.createElement('button'); select.type='button';
      select.textContent=label + (section.hidden ? ' · Hidden' : '');
      select.setAttribute('aria-pressed',String(section===selected || section===selectedSection));
      select.addEventListener('click',()=>{setSelected(section);section.scrollIntoView?.({block:'nearest',behavior:'smooth'});});
      row.append(drag,select);
      if(section.hidden){
        const restore=document.createElement('button');restore.type='button';restore.textContent='Show'; restore.setAttribute('aria-label',`Show ${label}`);
        restore.addEventListener('click',()=>{setSelected(section);checkpoint();const value=selectedOverride();if(!value)return;value.hidden=false;section.hidden=false;markDirty();syncEditorControls();showPanel('layers');});
        row.appendChild(restore);
      }
      row.addEventListener('dragstart',event=>{
        event.dataTransfer?.setData('text/plain',row.dataset.sectionId);
        row.classList.add('legend-cms-layer-dragging');
      });
      row.addEventListener('dragend',()=>row.classList.remove('legend-cms-layer-dragging'));
      row.addEventListener('dragover',event=>{event.preventDefault();row.classList.add('legend-cms-layer-drop');});
      row.addEventListener('dragleave',()=>row.classList.remove('legend-cms-layer-drop'));
      row.addEventListener('drop',event=>{
        event.preventDefault();row.classList.remove('legend-cms-layer-drop');
        const sourceId=event.dataTransfer?.getData('text/plain');
        reorderSectionByLayer(sourceId,row.dataset.sectionId);
      });
      list.appendChild(row);
    });
    if (!list.children.length) { const empty = document.createElement('p'); empty.textContent = 'No matching sections on this page.'; list.appendChild(empty); }
  }

  function refreshHistoryControls() {
    const undo = document.getElementById('legend-cms-undo'), redo = document.getElementById('legend-cms-redo');
    if (undo) undo.disabled = undoStack.length === 0;
    if (redo) redo.disabled = redoStack.length === 0;
  }

  function syncPageControls() {
    const page = pageState();
    const title = document.getElementById('legend-cms-page-title'), description = document.getElementById('legend-cms-page-description');
    if (title) title.value = page.title ?? document.title ?? '';
    if (description) description.value = page.description ?? document.querySelector('meta[name="description"]')?.content ?? '';
    syncBusinessPageFields();
    refreshPageSelector();
    updateSearchPreview();
  }

  function updateSearchPreview() {
    const title = document.getElementById('legend-cms-search-title'), description = document.getElementById('legend-cms-search-description');
    if (title) title.textContent = document.getElementById('legend-cms-page-title')?.value || 'Page title';
    if (description) description.textContent = document.getElementById('legend-cms-page-description')?.value || 'Add a helpful description of this page.';
  }

  function colorHex(value) {
    if (/^#[a-f0-9]{6}$/i.test(value || '')) return value;
    const channels = String(value || '').match(/^rgba?\(\s*(\d+)[, ]+\s*(\d+)[, ]+\s*(\d+)/i);
    return channels ? '#' + channels.slice(1,4).map(v => Math.min(255,Number(v)).toString(16).padStart(2,'0')).join('') : '#000000';
  }

  function appearanceFields() {
    const choices = { fontFamily: ['inherit','system-ui','serif','sans-serif','monospace','Georgia','Arial'], fontWeight: ['100','200','300','400','500','600','700','800','900'], objectFit: ['cover','contain','fill','none','scale-down'] };
    return ['color','backgroundColor','fontFamily','fontWeight','fontSize','lineHeight','letterSpacing','paddingLeft','paddingRight','borderRadius','objectFit'].map(key => {
      const label = key.replace(/([A-Z])/g, ' $1');
      const control = choices[key]
        ? `<select data-style-key="${key}"><option value="">Default</option>${choices[key].map(value => `<option value="${value}">${value}</option>`).join('')}</select>`
        : `<input data-style-key="${key}" type="${['color','backgroundColor'].includes(key) ? 'color' : 'number'}" step="any">`;
      return `<label class="legend-cms-group">${label}${control}${['color','backgroundColor'].includes(key) ? `<input data-color-hex="${key}" type="text" maxlength="7" pattern="#[a-fA-F0-9]{6}" aria-label="${label} hex code" placeholder="#000000"><button type="button" data-color-reset="${key}">Use template color</button>` : ''}</label>`;
    }).join('');
  }

  function installStudioControls(panel) {
    panel.querySelectorAll('button').forEach(button => button.type = 'button');
    document.getElementById('legend-cms-layer-search').addEventListener('input', refreshLayers);
    for (const [id, key] of [['legend-cms-page-title','title'], ['legend-cms-page-description','description']]) {
      document.getElementById(id).addEventListener('input', event => {
        checkpoint(); pageState()[key] = event.target.value;
        if (key === 'title') { refreshPageSelector(); renderPageManager(); }
        updateSearchPreview(); markDirty();
      });
    }
    const pageNavInput=(id,apply,eventName='input')=>document.getElementById(id)?.addEventListener(eventName,event=>{ if(SITE_KEY!=='business') return; checkpoint(); const page=pageState(); page.navigation ||= {showInNavigation:true,order:0,isDeleted:false}; apply(page.navigation,event.target); refreshPageSelector(); markDirty(); renderPageManager(); });
    pageNavInput('legend-cms-page-nav-label',(navigation,input)=>navigation.label=input.value);
    pageNavInput('legend-cms-page-order',(navigation,input)=>navigation.order=Number(input.value)||0);
    pageNavInput('legend-cms-page-nav-visible',(navigation,input)=>navigation.showInNavigation=input.checked);
    pageNavInput('legend-cms-page-parent',(navigation,input)=>navigation.parentPath=normalizePageRoute(input.value),'change');
    document.getElementById('legend-cms-page-create')?.addEventListener('click',async()=>{
      if(SITE_KEY!=='business') return; const route=normalizePageRoute(document.getElementById('legend-cms-page-slug')?.value);
      if(!route || route==='/' || websitePageEntries(true).some(entry=>entry.route===route)){ alert('Enter a unique website route such as /team or /services/commercial.'); return; }
      checkpoint(); const label=(document.getElementById('legend-cms-page-nav-label')?.value || route.split('/').filter(Boolean).at(-1) || 'Page').trim();
      const sectionId=crypto.randomUUID(); const textId=crypto.randomUUID();
      documentState.pages[route]={title:label,description:'',templatePath:null,navigation:{label,showInNavigation:true,parentPath:null,order:websitePageEntries(true).length*10,isDeleted:false},elements:{},sectionOrder:{},extras:[{id:sectionId,type:'section',sectionId:'custom.root',style:{}},{id:textId,type:'text',sectionId:'extra:'+sectionId,text:label,style:{}}]};
      markDirty(); await navigateToEditorPage(route);
    });
    document.getElementById('legend-cms-page-duplicate')?.addEventListener('click',async()=>{
      if(SITE_KEY!=='business') return; const sourceRoute=currentPageRoute(); const target=normalizePageRoute(document.getElementById('legend-cms-page-slug')?.value);
      if(!target || target===sourceRoute || websitePageEntries(true).some(entry=>entry.route===target)){ alert('Enter a unique route for the duplicate.'); return; }
      checkpoint(); const source=JSON.parse(JSON.stringify(pageState())); const template=templatePageEntries().has(sourceRoute)?sourceRoute:(normalizePageRoute(source.templatePath)||null);
      source.templatePath=template; source.navigation={...(source.navigation||{}),label:(source.navigation?.label||source.title||'Copy')+' copy',isDeleted:false,order:websitePageEntries(true).length*10};
      documentState.pages[target]=source; markDirty(); await navigateToEditorPage(target);
    });
    document.getElementById('legend-cms-page-rename')?.addEventListener('click',async()=>{
      if(SITE_KEY!=='business') return; const sourceRoute=currentPageRoute(); const target=normalizePageRoute(document.getElementById('legend-cms-page-slug')?.value);
      if(sourceRoute==='/' || !target || target==='/' || target===sourceRoute || websitePageEntries(true).some(entry=>entry.route===target)){ alert('Enter a unique route. The home page route cannot be renamed.'); return; }
      checkpoint(); const source=JSON.parse(JSON.stringify(pageState())); const template=templatePageEntries().has(sourceRoute)?sourceRoute:(normalizePageRoute(source.templatePath)||null); source.templatePath=template;
      documentState.pages[target]=source; const tombstone=ensurePageRecord(sourceRoute); tombstone.navigation={...(tombstone.navigation||{}),showInNavigation:false,isDeleted:true}; tombstone.elements={}; tombstone.sectionOrder={}; tombstone.extras=[];
      markDirty(); await navigateToEditorPage(target);
    });
    document.getElementById('legend-cms-page-delete')?.addEventListener('click',async()=>{
      if(SITE_KEY!=='business') return; const route=currentPageRoute(); if(route==='/') return; checkpoint(); const page=ensurePageRecord(route);
      page.navigation ||= {showInNavigation:true,order:0,isDeleted:false}; page.navigation.isDeleted=!page.navigation.isDeleted; if(page.navigation.isDeleted) page.navigation.showInNavigation=false; markDirty();
      if(page.navigation.isDeleted) await navigateToEditorPage('/'); else syncPageControls();
    });
    document.getElementById('legend-cms-duplicate').addEventListener('click', () => {
      if (!selected || !selectedSection || selected.dataset.cmsSignalOnly) return;
      const sourceExtra = selected.dataset.cmsExtraId ? pageState().extras.find(x => x.id === selected.dataset.cmsExtraId) : null;
      if (sourceExtra?.type === 'form' || selected.tagName === 'FORM') {
        alert('Each page uses one canonical inquiry form. Duplicate the surrounding content instead.');
        return;
      }

      const shiftCopy = copy => {
        copy.style ||= {};
        const currentY = Number(copy.style.offsetYPx);
        copy.style.offsetYPx = (Number.isFinite(currentY) ? currentY : 0) + 16;
        delete copy.hidden;
        copy.signals = [];
        return copy;
      };
      const rewriteExtraRef = (value, idMap) => {
        if (typeof value !== 'string' || !value.startsWith('extra:')) return value;
        const oldId = value.slice(6);
        return idMap.has(oldId) ? 'extra:' + idMap.get(oldId) : value;
      };

      if (sourceExtra) {
        checkpoint();
        if (sourceExtra.type === 'section') {
          const sourceIds = new Set([sourceExtra.id]);
          let changed = true;
          while (changed) {
            changed = false;
            for (const item of pageState().extras) {
              const parent = String(item.sectionId || '').startsWith('extra:') ? String(item.sectionId).slice(6) : null;
              if (parent && sourceIds.has(parent) && !sourceIds.has(item.id)) { sourceIds.add(item.id); changed = true; }
            }
          }
          const originals = pageState().extras.filter(item => sourceIds.has(item.id));
          const idMap = new Map(originals.map(item => [item.id, crypto.randomUUID()]));
          const copies = originals.map(item => {
            const copy = JSON.parse(JSON.stringify(item));
            copy.id = idMap.get(item.id);
            copy.sectionId = item.id === sourceExtra.id ? sourceExtra.sectionId : rewriteExtraRef(item.sectionId, idMap);
            copy.signals = [];
            if (copy.placement) {
              copy.placement.sectionId = rewriteExtraRef(copy.placement.sectionId, idMap);
              copy.placement.containerId = rewriteExtraRef(copy.placement.containerId, idMap);
              copy.placement.beforeId = rewriteExtraRef(copy.placement.beforeId, idMap);
            }
            if (item.id === sourceExtra.id) shiftCopy(copy);
            return copy;
          });
          pageState().extras.push(...copies);
          const rootCopy = copies.find(item => item.id === idMap.get(sourceExtra.id));
          const createdSections = new Map();
          copies.filter(item => item.type === 'section').forEach(item => createdSections.set(item.id, createExtra(item)));
          copies.filter(item => item.type !== 'section').forEach(item => {
            const created = createExtra(item);
            if (item.placement) applyPlacement(created, item.placement);
          });
          const sourceNode = document.querySelector(`[data-cms-id="extra:${CSS.escape(sourceExtra.id)}"]`);
          const createdRoot = rootCopy ? createdSections.get(rootCopy.id) : null;
          if (sourceNode && createdRoot && sourceNode.parentElement === createdRoot.parentElement)
            sourceNode.parentElement.insertBefore(createdRoot, sourceNode.nextSibling);
          syncSectionOrderFromDom();
          setSelected(createdRoot || null);
          markDirty();
          return;
        }

        const copy = shiftCopy(JSON.parse(JSON.stringify(sourceExtra)));
        copy.id = crypto.randomUUID();
        copy.sectionId = sourceExtra.sectionId;
        if (copy.placement) { copy.placement.beforeId = null; }
        pageState().extras.push(copy);
        const created = createExtra(copy);
        if (copy.placement) applyPlacement(created, copy.placement);
        const field = selected.dataset.cmsExtraField;
        setSelected(field ? created?.querySelector?.(`[data-cms-extra-field="${CSS.escape(field)}"]`) || created : created);
        markDirty();
        return;
      }

      const serviceCard = businessServiceCardFor(selected);
      if (serviceCard) {
        const grid = serviceCard.parentElement;
        if (!grid?.dataset.cmsId) return;
        checkpoint();
        const heading = serviceCard.querySelector('h1,h2,h3,h4,h5');
        const body = [...serviceCard.querySelectorAll('p')].find(node => !node.classList.contains('eyebrow')) || serviceCard.querySelector('p');
        const copy = {
          id: crypto.randomUUID(), type: 'card', sectionId: selectedSection.dataset.cmsSection,
          title: heading?.textContent || 'New service', text: body?.textContent || '', style: {},
          signals: [],
          placement: { sectionId: selectedSection.dataset.cmsSection, containerId: grid.dataset.cmsId, beforeId: null, flow: true, column: 1, span: 12 }
        };
        pageState().extras.push(copy);
        const created = createExtra(copy); applyPlacement(created, copy.placement); setSelected(created.querySelector('h3') || created); markDirty();
        return;
      }

      if (selected.dataset.cmsSection) {
        alert('Select an added section to duplicate the whole section, or select an individual item inside this template section.');
        return;
      }
      const supported = ['P','H1','H2','H3','H4','H5','H6','A','BUTTON','IMG','VIDEO','LI','SMALL','STRONG','SPAN'];
      if (!supported.includes(selected.tagName)) return;

      checkpoint();
      const original = pageState().elements[selected.dataset.cmsId] || {};
      const copy = shiftCopy({
        id: crypto.randomUUID(),
        sectionId: selectedSection.dataset.cmsSection,
        type: ({ A: 'button', BUTTON: 'button', IMG: 'image', VIDEO: 'video' }[selected.tagName] || 'text'),
        text: selected.textContent || '',
        style: JSON.parse(JSON.stringify(original.style || {})),
        breakpointStyles: JSON.parse(JSON.stringify(original.breakpointStyles || {})),
        layout: JSON.parse(JSON.stringify(original.layout || {})),
        breakpointLayouts: JSON.parse(JSON.stringify(original.breakpointLayouts || {})),
        signals: []
      });
      if (copy.type === 'button') {
        copy.actionKey = original.actionKey || null;
        copy.href = original.href ?? rememberOriginal(selected).href;
        copy.target = original.target ?? selected.getAttribute('target');
      }
      if (copy.type === 'image') {
        copy.imageDataUrl = original.imageDataUrl ?? rememberOriginal(selected).src;
        copy.alt = original.alt ?? selected.getAttribute('alt');
      }
      if (copy.type === 'video') copy.videoUrl = original.videoUrl ?? rememberOriginal(selected).src;
      const parent = selected.parentElement;
      if (parent && parent !== selectedSection && parent.dataset?.cmsId && !parent.closest(lockedSelector))
        copy.placement = { sectionId: selectedSection.dataset.cmsSection, containerId: parent.dataset.cmsId, beforeId: null, flow: true, column: 1, span: 12 };
      pageState().extras.push(copy);
      const created = createExtra(copy);
      if (copy.placement) applyPlacement(created, copy.placement);
      setSelected(created);
      markDirty();
    });
    const fontInput = panel.querySelector('[data-theme-key="fontFamily"]');
    if (fontInput?.replaceWith) {
      const select = document.createElement('select'); select.dataset.themeKey = 'fontFamily';
      for (const value of ['inherit','system-ui','serif','sans-serif','monospace','Georgia','Arial']) {
        const option = document.createElement('option'); option.value = value; option.textContent = value; select.appendChild(option);
      }
      fontInput.replaceWith(select);
    }
    document.addEventListener('keydown', event => {
      if (!(event.ctrlKey || event.metaKey) || event.altKey) return;
      const key = event.key.toLowerCase();
      if (key === 's') { event.preventDefault(); chooseDraft(); return; }
      if (event.target.closest?.('input,textarea,select,[contenteditable="true"]')) return;
      if (key === 'z' || key === 'y') {
        event.preventDefault();
        if (key === 'y' || event.shiftKey) restoreHistory(redoStack, undoStack);
        else restoreHistory(undoStack, redoStack);
      }
    });
    refreshHistoryControls();
  }
  function availableCtaOptions() {
    const items = [];
    const seen = new Set();
    const add = (option, managed = false) => {
      if (!option?.key || !option?.href || option.href === '#' || !safeUrl(option.href) || seen.has(option.key)) return;
      seen.add(option.key);
      items.push({ ...option, managed });
    };
    (ctaCatalog || []).forEach(option => add(option, true));
    const pageEntries = new Map();
    for (const page of context.pages || []) if (typeof page.path === 'string') pageEntries.set(page.path.replace(/\/$/, '') || '/', page.label || page.path);
    for (const [path, page] of Object.entries(documentState.pages || {})) pageEntries.set(path, page.title || pageEntries.get(path) || path);
    for (const [path, label] of pageEntries) add({ key: 'page:' + path, group: 'This website', label: 'Page · ' + label, defaultText: label, href: path });
    document.querySelectorAll('[data-cms-section][id]').forEach(section => {
      const id = section.id?.trim(); if (!id) return;
      const label = section.querySelector('h1,h2,h3')?.textContent?.trim() || id;
      add({ key: 'section:' + id, group: 'This page', label: 'Section · ' + label, defaultText: label, href: '#' + encodeURIComponent(id) });
    });
    return items;
  }

  function preferredCtaOption() {
    const preferred = SITE_KEY === 'business' ? 'business_contact' : SITE_KEY === 'protect' ? 'protect_quote' : 'legend_contact';
    const options = availableCtaOptions();
    return options.find(option => option.key === preferred) || options.find(option => option.managed) || options[0] || null;
  }

  function syncCtaControls(override, currentHref) {
    const select = document.getElementById('legend-cms-action');
    const custom = document.getElementById('legend-cms-custom-link');
    const wiring = document.getElementById('legend-cms-action-wiring');
    if (!select) return;
    const options = availableCtaOptions();
    select.replaceChildren();

    const presets = options.filter(option => option.managed);
    const navigation = options.filter(option => !option.managed);
    const appendGroup = (label, values, wired) => {
      if (!values.length) return;
      const group = document.createElement('optgroup'); group.label = label;
      values.forEach(option => {
        const node = document.createElement('option');
        node.value = option.key;
        node.textContent = (option.group ? option.group + ' · ' : '') + option.label +
          (wired ? (option.metaIntentEventName ? ' · AUTO Analytics + Meta' : ' · AUTO Analytics') : '');
        group.appendChild(node);
      });
      select.appendChild(group);
    };
    appendGroup('Preset actions · backend wired', presets, true);
    appendGroup('Navigate within this website', navigation, false);
    const customOption = document.createElement('option'); customOption.value = 'custom'; customOption.textContent = 'Custom destination…'; select.appendChild(customOption);

    const byKey = override?.actionKey ? options.find(option => option.key === override.actionKey) : null;
    const byHref = !byKey ? options.find(option => option.href === currentHref && !option.managed) || options.find(option => option.href === currentHref) : null;
    const selectedOption = byKey || byHref || null;
    select.value = selectedOption?.key || 'custom';
    if (custom) custom.hidden = select.value !== 'custom';
    if (wiring) {
      wiring.textContent = selectedOption?.managed
        ? `Automatic wiring: ${selectedOption.analyticsEventName || 'cta_click'}${selectedOption.metaIntentEventName ? ' + Meta ' + selectedOption.metaIntentEventName : ''}. No manual event mapping required.`
        : selectedOption ? 'Navigation link. Choose a preset action above when you want the existing automatic analytics/Meta contract.'
          : 'Custom link. Preset actions above are the backend-wired choices.';
    }
  }

  function selectedFlowContainer(section) {
    let node = selected;
    while (node && node !== section) {
      if (['DIV','ARTICLE'].includes(node.tagName) && node.dataset?.cmsId && !node.closest(lockedSelector)) return node;
      node = node.parentElement;
    }
    return null;
  }

  function openCodeEditor() {
    const block = selected;
    const extra = block?.dataset?.cmsExtraId ? pageState().extras.find(x => x.id === block.dataset.cmsExtraId) : null;
    if (!block || extra?.type !== 'code') return;
    clearTimeout(autoSaveTimer);
    const dialog = document.createElement('dialog');
    dialog.className = 'legend-cms-editor legend-cms-code-dialog';
    const title = document.createElement('h2'); title.textContent = 'Edit code block';
    const help = document.createElement('p'); help.textContent = 'HTML, CSS, and browser JavaScript run only inside this sandboxed block. Save to preview it on the page before publishing.';
    const textarea = document.createElement('textarea');
    textarea.className = 'legend-cms-code-source';
    textarea.setAttribute('aria-label', 'Code block source');
    textarea.spellcheck = false;
    textarea.value = extra.text || defaultCodeBlock;
    const status = document.createElement('p'); status.setAttribute('role','status');
    const actions = document.createElement('div'); actions.className = 'legend-cms-code-actions';
    const saveButton = document.createElement('button'); saveButton.type = 'button'; saveButton.textContent = 'Save & preview';
    const cancelButton = document.createElement('button'); cancelButton.type = 'button'; cancelButton.textContent = 'Cancel';
    saveButton.addEventListener('click', () => {
      if (textarea.value.length > 100000) { status.textContent = 'Code blocks can contain up to 100,000 characters.'; return; }
      checkpoint();
      extra.text = textarea.value;
      renderCodePreview(block, extra);
      markDirty();
      dialog.close();
      updateDirectCanvasUi();
    });
    cancelButton.addEventListener('click', () => dialog.close());
    actions.append(saveButton, cancelButton);
    dialog.append(title, help, textarea, status, actions);
    dialog.addEventListener('close', () => { dialog.remove(); if (dirty) autoSaveTimer = setTimeout(() => save(false), 900); });
    document.body.appendChild(dialog);
    dialog.showModal();
    textarea.focus();
  }

  function addBlock(type) {
    const section = selectedSection || document.querySelector('[data-cms-section]');
    if (!section && type !== 'section') return;
    if (type === 'form' && document.querySelector('form[data-website-inquiry]')) {
      alert('This page already has its canonical inquiry form. Select that form to move, resize, or review its automatic analytics and Meta wiring.');
      return;
    }
    const sectionAnchor = type === 'section'
      ? (selectedSection && !selectedSection.matches('.site-header,.site-footer') ? selectedSection : pageLayerSections()[0] || null)
      : null;
    const action = type === 'button' ? preferredCtaOption() : null;
    if (type === 'button' && !action) { alert('Configure a working website action before adding this button.'); return; }
    checkpoint();
    const extra = {
      id: crypto.randomUUID(),
      type,
      sectionId: section?.dataset.cmsSection || `${pageKey}.root`,
      text: type === 'button' ? action.defaultText || action.label : type === 'text' ? 'Your text' : type === 'form' ? 'Send inquiry' : type === 'code' ? defaultCodeBlock : '',
      title: type === 'form' ? 'Send an inquiry' : null,
      signals: [],
      style: type === 'code' ? { widthPercent: 100, heightPx: 320 } : type === 'form' ? { widthPercent: 100 } : {}
    };
    if (type === 'button') {
      extra.href = action.href;
      extra.target = action.openInNewTab ? '_blank' : '_self';
      if (action.managed) extra.actionKey = action.key;
      const container = selectedFlowContainer(section);
      if (container) extra.placement = { sectionId: section.dataset.cmsSection, containerId: container.dataset.cmsId, beforeId: null, flow: true, column: 1, span: 12 };
    }
    pageState().extras.push(extra);
    const el = createExtra(extra);
    if (type === 'section' && el && sectionAnchor?.parentElement === el.parentElement) {
      el.parentElement.insertBefore(el, sectionAnchor.nextSibling);
      syncSectionOrderFromDom();
    }
    if (extra.placement) applyPlacement(el, extra.placement);
    setSelected(el); markDirty();
    if (type === 'code') openCodeEditor();
  }
  function enhanceEditor(panel, preview) {
    document.querySelectorAll('[data-cms-id]').forEach(el => baselineNodes.set(el.dataset.cmsId, { el, parent: el.parentElement, next: el.nextSibling }));
    const content = document.createElement('div'); content.dataset.cmsView = 'content';
    Array.from(panel.children).filter(el => !el.classList.contains('legend-cms-bar')).forEach(el => content.appendChild(el));
    panel.appendChild(content);
    const navigation = document.createElement('nav');
    navigation.className = 'legend-cms-navigation'; navigation.setAttribute('aria-label', 'Website editing tools');
    navigation.innerHTML = `<div class="legend-cms-tabs"><button type="button" data-open="content">Content</button><button type="button" data-open="add">Add blocks</button><button type="button" data-open="appearance">Design</button><button type="button" data-open="layout">Responsive</button><button type="button" data-open="layers">Layers</button><button type="button" data-open="media">Media</button><button type="button" data-open="components">Components</button><button type="button" data-open="data">Data</button><button type="button" data-open="ai">AI Assist</button><button type="button" data-open="motion">Motion</button><button type="button" data-open="signals">Analytics & Meta</button><button type="button" data-open="quality">Quality</button><button type="button" data-open="collaboration">Collaborate</button><button type="button" data-open="theme">Site theme</button><button type="button" data-open="page">Pages & SEO</button></div>`;
    panel.insertBefore(navigation, content);
    const tools = document.createElement('div'); tools.innerHTML = `
      <section data-cms-view="add" hidden><h2>Add a block</h2><p>Add to the selected section, then position and resize it directly on the page.</p><div class="legend-cms-menu"><button data-add="text">Text</button><button data-add="button">Button / link</button><button id="legend-cms-new-image">Image</button><button data-add="video">Video</button><button data-add="form">Inquiry form</button><button data-add="code">Code / embed</button><button data-add="section">Section</button></div></section>
      <section data-cms-view="appearance" hidden><h2>Appearance</h2>${appearanceFields()}<button id="legend-cms-container">Select section container</button></section>
      <section data-cms-view="layout" hidden><h2>Responsive layout</h2><p>Edit the base design or explicitly target one breakpoint. Breakpoint overrides inherit every unset value from the base design.</p><label class="legend-cms-group">Editing breakpoint<select id="legend-cms-breakpoint"></select></label><div class="legend-cms-row"><label class="legend-cms-group">Custom name<input id="legend-cms-breakpoint-label" type="text" maxlength="80" placeholder="Large tablet"></label><label class="legend-cms-group">Key<input id="legend-cms-breakpoint-key" type="text" maxlength="40" placeholder="large-tablet"></label></div><div class="legend-cms-row"><label class="legend-cms-group">Min px<input id="legend-cms-breakpoint-min" type="number" min="0" max="10000" value="900"></label><label class="legend-cms-group">Max px<input id="legend-cms-breakpoint-max" type="number" min="0" max="10000" placeholder="No maximum"></label></div><div class="legend-cms-row"><button id="legend-cms-breakpoint-add" type="button">Add breakpoint</button><button id="legend-cms-breakpoint-remove" type="button">Remove custom breakpoint</button></div><hr><label class="legend-cms-group">Container behavior<select id="legend-cms-layout-mode"><option value="free">Free Canvas</option><option value="stack">Stack</option><option value="grid">Grid</option><option value="flex">Flex / Auto Layout</option></select></label><div class="legend-cms-row"><label class="legend-cms-group">Direction<select id="legend-cms-layout-direction"><option value="column">Column</option><option value="row">Row</option></select></label><label class="legend-cms-group">Gap px<input id="legend-cms-layout-gap" type="number" min="0" max="240" step="any"></label></div><div class="legend-cms-row"><label class="legend-cms-group">Grid columns<input id="legend-cms-layout-columns" type="number" min="1" max="12"></label><label class="legend-cms-group">Min item width px<input id="legend-cms-layout-min" type="number" min="1" max="4000"></label></div><div class="legend-cms-row"><label class="legend-cms-group">Align items<select id="legend-cms-layout-align"><option value="">Default</option><option value="start">Start</option><option value="center">Center</option><option value="end">End</option><option value="stretch">Stretch</option></select></label><label class="legend-cms-group">Justify<select id="legend-cms-layout-justify"><option value="">Default</option><option value="start">Start</option><option value="center">Center</option><option value="end">End</option><option value="space-between">Space between</option><option value="space-around">Space around</option><option value="space-evenly">Space evenly</option></select></label></div><label class="legend-cms-group">Wrap<select id="legend-cms-layout-wrap"><option value="">Default</option><option value="nowrap">No wrap</option><option value="wrap">Wrap</option></select></label><p>Selection, movement, and resizing are separate actions: click content to select it, drag the gold Move control to position it, and drag only the border edges or corners to resize. Use X/Y offsets for precise positioning.</p><div class="legend-cms-row"><label class="legend-cms-group">X offset %<input id="legend-cms-offset-x" type="number" step="any" value="0"></label><label class="legend-cms-group">Y offset px<input id="legend-cms-offset-y" type="number" step="any" value="0"></label></div><button id="legend-cms-undo">Undo</button><button id="legend-cms-redo">Redo</button></section>
      <section data-cms-view="layers" hidden><h2>Sections</h2><p>Drag only whole page sections to reorder them. Edit headings, buttons, fields, and other content directly on the page so this list stays clean and short.</p><label class="legend-cms-group">Find section<input id="legend-cms-layer-search" type="search" placeholder="Search sections"></label><div id="legend-cms-layers" class="legend-cms-layer-list"></div></section>
      <section data-cms-view="media" hidden><h2>Media library</h2><p>Browse media already owned by this website scope. Reusing an asset does not copy the file or create another storage record.</p><div class="legend-cms-row"><label class="legend-cms-group">Search<input id="legend-cms-media-search" type="search" placeholder="Name or file type"></label><label class="legend-cms-group">Type<select id="legend-cms-media-kind"><option value="all">All media</option><option value="image">Images</option><option value="video">Videos</option></select></label></div><input id="legend-cms-media-upload" type="file" accept="image/jpeg,image/png,image/webp,video/mp4,video/webm"><button id="legend-cms-media-refresh" type="button">Refresh library</button><small id="legend-cms-media-status" role="status"></small><div id="legend-cms-media-grid" class="legend-cms-media-grid"></div></section>\n      <section data-cms-view="components" hidden><h2>Reusable components</h2><p>Save an added block or added section once, then insert synchronized references. Template sections remain owned by the template system and are not copied into component storage.</p><label class="legend-cms-group">Component name<input id="legend-cms-component-name" type="text" maxlength="120" placeholder="Hero, testimonial, contact band"></label><button id="legend-cms-component-save" type="button">Save selected as component</button><small id="legend-cms-component-status" role="status"></small><div id="legend-cms-component-list" class="legend-cms-component-list"></div></section>\n      <section data-cms-view="data" hidden><h2>Dynamic CMS</h2><p id="legend-cms-data-unavailable" hidden>Scoped business data is available only on Business websites.</p><div id="legend-cms-data-business"><h3>Selected content binding</h3><label class="legend-cms-group">Source<select id="legend-cms-data-source"></select></label><div class="legend-cms-row"><label class="legend-cms-group">Field<select id="legend-cms-data-field"></select></label><label class="legend-cms-group">Apply as<select id="legend-cms-data-target"><option value="text">Text</option><option value="image">Image URL</option><option value="href">Link destination</option></select></label></div><div class="legend-cms-row"><button id="legend-cms-data-bind" type="button">Bind selected</button><button id="legend-cms-data-clear" type="button">Clear binding</button></div><small id="legend-cms-data-status" role="status"></small><hr><h3>Dynamic page</h3><p>Use an existing list source to generate one published route per item. The preview choice below is local editor state only.</p><label class="legend-cms-group">List source<select id="legend-cms-dynamic-source"></select></label><div class="legend-cms-row"><label class="legend-cms-group">Route key field<select id="legend-cms-dynamic-key"></select></label><label class="legend-cms-group">Route pattern<input id="legend-cms-dynamic-pattern" type="text" placeholder="/products/{item}"></label></div><label class="legend-cms-group">Preview item<select id="legend-cms-dynamic-preview"></select></label><div class="legend-cms-row"><button id="legend-cms-dynamic-apply" type="button">Apply dynamic page</button><button id="legend-cms-dynamic-clear" type="button">Make page static</button></div><small id="legend-cms-dynamic-status" role="status"></small></div></section>\n      <section data-cms-view="page" hidden><h2>Pages & search appearance</h2><p>Page structure and SEO stay in the same versioned website document.</p><div id="legend-cms-page-list" class="legend-cms-page-list"></div><p id="legend-cms-page-fixed-notice" hidden>LEGEND and Protect currently expose only their real published route catalog. Arbitrary route creation stays disabled until their shared route-manifest publication layer is connected.</p><div id="legend-cms-page-business-tools"><div class="legend-cms-row"><label class="legend-cms-group">Navigation label<input id="legend-cms-page-nav-label" type="text" maxlength="120"></label><label class="legend-cms-group">Route / slug<input id="legend-cms-page-slug" type="text" maxlength="160"></label></div><div class="legend-cms-row"><label class="legend-cms-group">Parent page<select id="legend-cms-page-parent"></select></label><label class="legend-cms-group">Navigation order<input id="legend-cms-page-order" type="number" step="1"></label></div><label class="legend-cms-group"><input id="legend-cms-page-nav-visible" type="checkbox"> Show in public navigation</label><div class="legend-cms-menu"><button id="legend-cms-page-create" type="button">Create page</button><button id="legend-cms-page-duplicate" type="button">Duplicate page</button><button id="legend-cms-page-rename" type="button">Rename / move route</button><button id="legend-cms-page-delete" type="button">Delete page</button></div></div><hr><label class="legend-cms-group">Page title<input id="legend-cms-page-title" type="text" maxlength="200"></label><label class="legend-cms-group">Search description<textarea id="legend-cms-page-description" rows="4" maxlength="500"></textarea></label><div class="legend-cms-search-preview"><strong id="legend-cms-search-title"></strong><p id="legend-cms-search-description"></p></div></section>
      <section data-cms-view="theme" id="legend-cms-theme-view" hidden><h2>Site theme</h2><p>One palette, typography system, and browser icon for every page of this website.</p><div class="legend-cms-group legend-cms-favicon"><label for="legend-cms-favicon">Browser favicon</label><img id="legend-cms-favicon-preview" class="legend-cms-favicon-preview" alt=""><input id="legend-cms-favicon" type="file" accept="image/jpeg,image/png,image/webp"><small>PNG, JPEG, or WebP. This is scoped to this website and becomes public only when the website is published.</small><button id="legend-cms-favicon-remove" type="button">Use LEGEND fallback favicon</button></div></section>`;
    panel.appendChild(tools);
    const signals = document.createElement('section'); signals.dataset.cmsView = 'signals'; signals.hidden = true;
    signals.innerHTML = '<h2>Analytics & Meta</h2><p>Standard page engagement, managed buttons, and the canonical inquiry form are wired automatically from the shared Protect Website analytics and Meta authorities. Select content to review that wiring. Advanced custom mappings are only for non-standard interactions.</p><div id="legend-cms-signal-controls"></div>';
    tools.appendChild(signals);
    const motion = document.createElement('section'); motion.dataset.cmsView='motion'; motion.hidden=true;
    motion.innerHTML='<h2>Motion & interactions</h2><p>Declarative visual motion only. These effects never create analytics, leads, bookings, purchases, or other business outcomes.</p><small id="legend-cms-motion-status">Select an element to configure motion.</small><div id="legend-cms-motion-controls"></div>';
    tools.appendChild(motion);

    const ai = document.createElement('section'); ai.dataset.cmsView='ai'; ai.hidden=true;
    ai.innerHTML='<h2>AI creation assistant</h2><p>AI proposes typed changes only. Nothing is saved or published until you apply the proposal and use the normal draft/publish controls.</p><label class="legend-cms-group">Mode<select id="legend-cms-ai-mode"><option value="responsive">Responsive improvement</option><option value="create">Content / section creation</option></select></label><label class="legend-cms-group">Instruction<textarea id="legend-cms-ai-prompt" rows="5" maxlength="2000" placeholder="Example: make this section cleaner on mobile without changing the wording"></textarea></label><button id="legend-cms-ai-generate" type="button">Generate proposal</button><small id="legend-cms-ai-status" role="status">No proposal generated.</small><div id="legend-cms-ai-proposal"></div><div class="legend-cms-row"><button id="legend-cms-ai-apply" type="button" disabled>Apply proposal to draft</button><button id="legend-cms-ai-discard" type="button" disabled>Discard</button></div>';
    tools.appendChild(ai);

    const quality = document.createElement('section'); quality.dataset.cmsView = 'quality'; quality.hidden = true;
    quality.innerHTML = '<h2>Quality inspector</h2><p>Saved draft checks and live canvas checks are different evidence sources. The server remains authoritative for saved state and publication.</p><h3>Saved draft checks (server)</h3><small id="legend-cms-quality-saved-meta">Open Quality to inspect the persisted draft.</small><div id="legend-cms-quality-saved" class="legend-cms-quality-list"></div><h3>Live page checks (rendered canvas)</h3><small id="legend-cms-quality-live-meta">Open Quality to inspect the rendered canvas.</small><div id="legend-cms-quality-live" class="legend-cms-quality-list"></div><button id="legend-cms-quality-refresh" type="button">Run checks again</button>';
    tools.appendChild(quality);

    const collaboration = document.createElement('section'); collaboration.dataset.cmsView='collaboration'; collaboration.hidden=true;
    collaboration.innerHTML='<h2>Collaboration</h2><p>Private review comments stay in Website Studio and never publish into the website document.</p><small id="legend-cms-collaboration-role">Loading role…</small><div id="legend-cms-collaboration-roster" class="legend-cms-collaboration-roster"></div><label class="legend-cms-group">Comment<textarea id="legend-cms-collaboration-body" rows="4" maxlength="4000" placeholder="Leave a review note for this page or selected element"></textarea></label><small id="legend-cms-collaboration-reply"></small><div class="legend-cms-row"><button id="legend-cms-collaboration-add" type="button">Comment on selection</button><button id="legend-cms-collaboration-page" type="button">Comment on page</button></div><div id="legend-cms-collaboration-comments" class="legend-cms-collaboration-comments"></div>';
    tools.appendChild(collaboration);


    const layoutView = tools.querySelector('[data-cms-view="layout"]');
    ['legend-cms-up','legend-cms-down','legend-cms-reset'].forEach(id => { const button = document.getElementById(id); if (button && layoutView) layoutView.appendChild(button); });
    const selectedDelete = document.getElementById('legend-cms-remove');
    const selectedActions = document.createElement('div'); selectedActions.className = 'legend-cms-row legend-cms-selected-actions';
    const duplicate = document.createElement('button'); duplicate.id = 'legend-cms-duplicate'; duplicate.type = 'button'; duplicate.textContent = 'Duplicate selected'; duplicate.disabled = true;
    selectedActions.appendChild(duplicate);
    if (selectedDelete) {
      selectedDelete.disabled = true;
      selectedDelete.setAttribute('aria-label', 'Delete selected website element');
      selectedActions.appendChild(selectedDelete);
    }
    if (content) content.appendChild(selectedActions);
    ['legend-cms-undo', 'legend-cms-redo'].forEach(id => panel.querySelector('.legend-cms-bar').appendChild(document.getElementById(id)));
    const theme = content.querySelector('.legend-cms-theme'); if (theme) document.getElementById('legend-cms-theme-view').appendChild(theme.parentElement);
    const links = document.createElement('div'); links.innerHTML = `<div id="legend-cms-link-group" class="legend-cms-group" hidden><label for="legend-cms-action">Button action</label><select id="legend-cms-action"></select><small>Preset actions are the existing backend-wired choices. Page and section links are navigation only.</small><small id="legend-cms-action-wiring"></small><div id="legend-cms-custom-link"><label for="legend-cms-href">Custom destination</label><input id="legend-cms-href" type="url" placeholder="https://…"></div><label><input id="legend-cms-target" type="checkbox"> Open in a new tab</label></div><div id="legend-cms-video-group" class="legend-cms-group" hidden><label for="legend-cms-videoUrl">HTTPS video URL</label><input id="legend-cms-videoUrl" type="url"><label for="legend-cms-video-file">Upload video</label><input id="legend-cms-video-file" type="file" accept="video/mp4,video/webm"></div><label class="legend-cms-group">Image description<input id="legend-cms-alt" type="text"></label>`;
    content.appendChild(links);
    panel.querySelectorAll('[data-open]').forEach(button => button.addEventListener('click', () => showPanel(button.dataset.open)));
    syncBreakpointControls();
    document.getElementById('legend-cms-data-source')?.addEventListener('change',renderDataControls);
    document.getElementById('legend-cms-dynamic-source')?.addEventListener('change',renderDataControls);
    document.getElementById('legend-cms-data-bind')?.addEventListener('click',()=>{
      if(SITE_KEY!=='business' || !selected) return;
      const sourceKey=document.getElementById('legend-cms-data-source')?.value;
      const field=document.getElementById('legend-cms-data-field')?.value;
      const target=document.getElementById('legend-cms-data-target')?.value || 'text';
      const source=approvedDataSources().find(value=>value.key===sourceKey);
      if(!source || !source.fields?.includes(field)) return;
      if(target==='image' && !(selected instanceof HTMLImageElement)){ alert('Image data can only bind to an image.'); return; }
      if(target==='href' && selected.tagName!=='A'){ alert('Link destinations can only bind to a link.'); return; }
      checkpoint(); const collection=ensureCollectionForSource(sourceKey,[field]); const override=selectedOverride(); if(!collection || !override) return;
      override.dataBinding={collectionId:collection.id,field,target}; applyElementOverride(selected,override); markDirty(); renderDataControls();
    });
    document.getElementById('legend-cms-data-clear')?.addEventListener('click',()=>{
      const override=selectedOverride(); if(!override?.dataBinding) return; checkpoint(); delete override.dataBinding; applyElementOverride(selected,override); markDirty(); renderDataControls();
    });
    document.getElementById('legend-cms-dynamic-apply')?.addEventListener('click',()=>{
      if(SITE_KEY!=='business') return;
      const sourceKey=document.getElementById('legend-cms-dynamic-source')?.value;
      const itemKeyField=document.getElementById('legend-cms-dynamic-key')?.value;
      const routePattern=document.getElementById('legend-cms-dynamic-pattern')?.value?.trim();
      const source=approvedDataSources().find(value=>value.key===sourceKey && value.isList===true);
      if(!source || !source.fields?.includes(itemKeyField) || !/^\/(?:[a-z0-9_-]+\/)*\{item\}\/?$/i.test(routePattern||'')){ alert('Choose a list source, a valid key field, and a route pattern such as /products/{item}.'); return; }
      checkpoint(); const collection=ensureCollectionForSource(sourceKey,[itemKeyField]); if(!collection) return;
      pageState().dynamicBinding={collectionId:collection.id,itemKeyField,routePattern:routePattern.toLowerCase()}; dynamicCollectionItem=null; markDirty(); renderDataControls();
    });
    document.getElementById('legend-cms-dynamic-clear')?.addEventListener('click',()=>{
      if(!pageState().dynamicBinding) return; checkpoint(); pageState().dynamicBinding=null; dynamicCollectionItem=null; refreshResponsiveOverrides(); markDirty(); renderDataControls();
    });
    document.getElementById('legend-cms-dynamic-preview')?.addEventListener('change',event=>{
      const binding=pageState().dynamicBinding; const projection=binding?collectionData.get(binding.collectionId):null;
      const item=(projection?.items||[]).find(value=>value.key===event.target.value);
      dynamicCollectionItem=item?{collectionId:binding.collectionId,key:item.key,fields:item.fields}:null; refreshResponsiveOverrides(); renderDataControls();
    });
    document.getElementById('legend-cms-collaboration-add')?.addEventListener('click',()=>void createCollaborationComment(false));
    document.getElementById('legend-cms-collaboration-page')?.addEventListener('click',()=>void createCollaborationComment(true));
    document.getElementById('legend-cms-ai-generate')?.addEventListener('click',()=>void requestAiProposal());
    document.getElementById('legend-cms-ai-apply')?.addEventListener('click',applyAiProposal);
    document.getElementById('legend-cms-ai-discard')?.addEventListener('click',()=>{ pendingAiProposal=null; renderAiProposal(); });
    document.getElementById('legend-cms-component-save')?.addEventListener('click',()=>{
      const status=document.getElementById('legend-cms-component-status');
      const name=document.getElementById('legend-cms-component-name')?.value?.trim();
      const definition=captureReusableDefinition(name);
      if(!definition){ if(status) status.textContent='Select an added block or added section. Template content stays linked to its template authority.'; return; }
      checkpoint(); documentState.reusableComponents ||= {}; documentState.reusableComponents[definition.id]=definition;
      if(status) status.textContent=`Saved ${definition.name}.`; markDirty(); renderReusableComponents();
    });
    document.getElementById('legend-cms-media-refresh')?.addEventListener('click',()=>void refreshMediaLibrary());
    document.getElementById('legend-cms-media-search')?.addEventListener('input',()=>void refreshMediaLibrary());
    document.getElementById('legend-cms-media-kind')?.addEventListener('change',()=>void refreshMediaLibrary());
    document.getElementById('legend-cms-media-upload')?.addEventListener('change',async event=>{ const file=event.target.files?.[0]; if(!file) return; const url=await uploadMedia(file); event.target.value=''; if(url) await refreshMediaLibrary(); });
    document.getElementById('legend-cms-quality-refresh')?.addEventListener('click', () => void refreshQualityInspector());
    document.getElementById('legend-cms-breakpoint')?.addEventListener('change', event => { editorBreakpointKey=event.target.value; applyBreakpointPreview(); syncBreakpointControls(); });
    document.getElementById('legend-cms-breakpoint-add')?.addEventListener('click', () => {
      const key=safeId(document.getElementById('legend-cms-breakpoint-key')?.value);
      const label=(document.getElementById('legend-cms-breakpoint-label')?.value || key).trim();
      const min=Number(document.getElementById('legend-cms-breakpoint-min')?.value);
      const maxRaw=document.getElementById('legend-cms-breakpoint-max')?.value;
      const max=maxRaw === '' ? null : Number(maxRaw);
      if (!key || (documentState.breakpoints||[]).some(value=>value.key===key) || !Number.isFinite(min) || min<0 || (max!=null && (!Number.isFinite(max) || max<min))) { alert('Enter a unique breakpoint key and a valid minimum/maximum width.'); return; }
      checkpoint(); documentState.breakpoints ||= defaultBreakpoints(); documentState.breakpoints.push({key,label:label||key,minWidth:min,maxWidth:max,isSystem:false}); editorBreakpointKey=key; syncBreakpointControls(); applyBreakpointPreview(); markDirty();
    });
    document.getElementById('legend-cms-breakpoint-remove')?.addEventListener('click', () => {
      const current=(documentState.breakpoints||[]).find(value=>value.key===editorBreakpointKey); if (!current || current.isSystem) return;
      checkpoint(); const key=current.key; markDeleted('breakpoint:' + key); documentState.breakpoints=documentState.breakpoints.filter(value=>value.key!==key);
      forEachDocumentOverride(override=>{ if(override?.breakpointStyles) delete override.breakpointStyles[key]; if(override?.breakpointLayouts) delete override.breakpointLayouts[key]; });
      editorBreakpointKey='base'; syncBreakpointControls(); applyBreakpointPreview(); markDirty();
    });
    const layoutHandlers={
      'legend-cms-layout-mode':['mode',value=>value],
      'legend-cms-layout-direction':['direction',value=>value],
      'legend-cms-layout-gap':['gapPx',value=>value===''?null:Number(value)],
      'legend-cms-layout-columns':['columns',value=>value===''?null:Number(value)],
      'legend-cms-layout-min':['minItemWidthPx',value=>value===''?null:Number(value)],
      'legend-cms-layout-align':['alignItems',value=>value||null],
      'legend-cms-layout-justify':['justifyContent',value=>value||null],
      'legend-cms-layout-wrap':['wrap',value=>value||null]
    };
    Object.entries(layoutHandlers).forEach(([id,[field,convert]])=>document.getElementById(id)?.addEventListener('input',event=>{
      if(!selected) return; checkpoint(); const override=selectedOverride(); if(!override) return; const layout=editingLayout(override,true); const value=convert(event.target.value); if(value==null) delete layout[field]; else layout[field]=value; applyElementOverride(selected,override); updateDirectCanvasUi(); markDirty();
    }));
    installStudioControls(panel);
    document.getElementById('legend-cms-action').addEventListener('change', event => {
      if (!selected || selected.tagName !== 'A') return;
      const ov = selectedOverride(); if (!ov) return;
      checkpoint();
      const option = availableCtaOptions().find(candidate => candidate.key === event.target.value);
      if (!option) {
        delete ov.actionKey;
        document.getElementById('legend-cms-custom-link').hidden = false;
        markDirty();
        return;
      }
      if (option.managed) ov.actionKey = option.key; else delete ov.actionKey;
      ov.href = option.href; ov.target = option.openInNewTab ? '_blank' : '_self';
      if (selected.dataset.cmsExtraId) { ov.text = option.defaultText || option.label; setContentText(selected, ov.text, true); }
      applyElementOverride(selected, ov); syncEditorControls(); markDirty();
    });
    panel.querySelectorAll('[data-add]').forEach(button => button.addEventListener('click', () => addBlock(button.dataset.add)));
    document.getElementById('legend-cms-new-image').addEventListener('click', () => document.getElementById('legend-cms-extra-image').click());
    document.getElementById('legend-cms-edit-code')?.addEventListener('click', openCodeEditor);
    document.getElementById('legend-cms-container').addEventListener('click', () => { if (selectedSection) setSelected(selectedSection); });
    panel.querySelectorAll('[data-style-key]').forEach(input => input.addEventListener('input', () => { if (!selected) return; const value = input.type === 'number' || input.dataset.styleKey === 'fontWeight' ? Number(input.value) : input.value; if (input.type === 'number' && input.value !== '' && (!Number.isFinite(value) || (input.dataset.styleKey !== 'letterSpacing' && value < 0) || (['fontSize','lineHeight'].includes(input.dataset.styleKey) && value === 0))) return; checkpoint(); const ov = selectedOverride(); ov.style ||= {}; if (input.value === '') delete ov.style[input.dataset.styleKey]; else ov.style[input.dataset.styleKey] = value; applyStyle(selected, ov.style); markDirty(); }));
    panel.querySelectorAll('[data-color-hex]').forEach(input => input.addEventListener('change', () => {
      if (!selected) return;
      if (!/^#[a-f0-9]{6}$/i.test(input.value)) { input.setCustomValidity('Enter a six-digit hex color, such as #000000.'); input.reportValidity(); return; }
      input.setCustomValidity(''); checkpoint(); const ov = selectedOverride(); ov.style ||= {}; ov.style[input.dataset.colorHex] = input.value.toLowerCase();
      applyStyle(selected, ov.style); syncEditorControls(); markDirty();
    }));
    panel.querySelectorAll('[data-color-reset]').forEach(button => button.addEventListener('click', () => {
      if (!selected) return; checkpoint(); const ov = selectedOverride(); if (ov.style) delete ov.style[button.dataset.colorReset];
      applyStyle(selected, ov.style); syncEditorControls(); markDirty();
    }));
    ['href','videoUrl','alt'].forEach(key => document.getElementById(`legend-cms-${key}`).addEventListener('input', event => { if (!selected) return; const value = event.target.value; if (key !== 'alt' && !safeUrl(value, key === 'videoUrl')) { event.target.setCustomValidity('Enter a supported URL.'); return; } event.target.setCustomValidity(''); checkpoint(); const ov = selectedOverride(); ov[key] = value; if (key === 'href') { delete ov.actionKey; const action = document.getElementById('legend-cms-action'); if (action) action.value = 'custom'; const custom = document.getElementById('legend-cms-custom-link'); if (custom) custom.hidden = false; const wiring = document.getElementById('legend-cms-action-wiring'); if (wiring) wiring.textContent = 'Custom link. Preset actions above are the backend-wired choices.'; } applyElementOverride(selected, ov); markDirty(); }));
    document.getElementById('legend-cms-video-file').addEventListener('change', async event => { const video = selected; if (video?.tagName !== 'VIDEO') return; const url = await uploadMedia(event.target.files?.[0]); if (!url || selected !== video) return; checkpoint(); const ov = selectedOverride(); ov.videoUrl = url; applyElementOverride(video, ov); syncEditorControls(); markDirty(); });
    document.getElementById('legend-cms-target').addEventListener('input', event => { if (!selected) return; checkpoint(); const ov = selectedOverride(); ov.target = event.target.checked ? '_blank' : '_self'; ov.href ||= rememberOriginal(selected).href; applyElementOverride(selected, ov); markDirty(); });
    document.getElementById('legend-cms-undo').addEventListener('click', () => restoreHistory(undoStack, redoStack));
    document.getElementById('legend-cms-redo').addEventListener('click', () => restoreHistory(redoStack, undoStack));
    installDirectCanvasControls(preview);
    showPanel('content');
  }

  function injectContentStyles() { const style = document.createElement('style'); style.textContent = `      [data-cms-id][hidden]{display:none}.cms-layout-frame{display:grid;grid-template-columns:repeat(12,minmax(0,1fr));gap:clamp(8px,2vw,24px);width:100%;min-width:0}.cms-layout-frame>*{grid-column:var(--cms-column,1) / span var(--cms-span,12);max-width:100%;min-width:0;overflow-wrap:anywhere}.cms-extra-section{padding:clamp(24px,5vw,64px);min-height:120px}.cms-extra video,video.cms-extra{max-width:100%;height:auto}.cms-extra-code{display:block;width:100%;height:320px;min-height:72px;overflow:hidden;background:#fff}.cms-extra-code iframe{display:block;width:100%;height:100%;border:0;background:#fff}@media(max-width:600px){.cms-layout-frame>*{grid-column:1 / -1}}
`; document.head.appendChild(style); }

  function injectEditorStyles() {
    const style = document.createElement('style');
    style.textContent = `
      .legend-cms-selected{outline:none}
      [data-cms-editable="true"]{cursor:pointer}
      .legend-cms-inline-editing{cursor:text;user-select:text;caret-color:currentColor}
      .legend-cms-preview .cms-extra-code iframe{pointer-events:none}
      .legend-cms-grid-overlay{position:absolute;z-index:2147482000;pointer-events:none;border:1px solid #d4ad454d;background-color:#081a3a08;background-image:linear-gradient(to right,#d4ad4526 1px,transparent 1px),linear-gradient(to bottom,#d4ad4517 1px,transparent 1px);background-size:calc(100% / 12) 100%,100% 24px}
      .legend-cms-grid-overlay::before,.legend-cms-grid-overlay::after{content:"";position:absolute;pointer-events:none;opacity:0;background:#f0cf78;box-shadow:0 0 0 1px #081a3a66}
      .legend-cms-grid-overlay::before{left:50%;top:0;bottom:0;width:1px;transform:translateX(-.5px)}
      .legend-cms-grid-overlay::after{top:50%;left:0;right:0;height:1px;transform:translateY(-.5px)}
      .legend-cms-grid-overlay.legend-cms-snap-x::before,.legend-cms-grid-overlay.legend-cms-snap-y::after{opacity:1}
      .legend-cms-selection-frame{position:absolute;z-index:2147482500;pointer-events:none;border:1px solid #d4ad45;box-shadow:0 0 0 1px #081a3a26}
      .legend-cms-move-handle{position:absolute;left:8px;top:8px;z-index:2;pointer-events:auto;touch-action:none;min-width:48px!important;min-height:30px!important;padding:5px 10px!important;border:1px solid #081a3a!important;border-radius:999px!important;background:#d4ad45!important;color:#081a3a!important;font:800 11px/1 Inter,system-ui,sans-serif!important;letter-spacing:.02em;cursor:grab!important;box-shadow:0 4px 12px #0004!important}
      .legend-cms-move-handle:active{cursor:grabbing!important}
      .legend-cms-selection-frame[data-section-selected="true"] .legend-cms-move-handle{display:none}
      .legend-cms-edge-handle{position:absolute;pointer-events:auto;touch-action:none;margin:0;padding:0;border:0!important;border-radius:0!important;background:transparent!important;box-shadow:none!important;min-width:0!important;min-height:0!important}
      .legend-cms-edge-top,.legend-cms-edge-bottom{left:10px;right:10px;height:12px;cursor:ns-resize}
      .legend-cms-edge-top{top:-6px}.legend-cms-edge-bottom{bottom:-6px}
      .legend-cms-edge-left,.legend-cms-edge-right{top:10px;bottom:10px;width:12px;cursor:ew-resize}
      .legend-cms-edge-left{left:-6px}.legend-cms-edge-right{right:-6px}
      .legend-cms-corner-nw,.legend-cms-corner-ne,.legend-cms-corner-se,.legend-cms-corner-sw{width:14px;height:14px}
      .legend-cms-corner-nw{left:-7px;top:-7px;cursor:nwse-resize}.legend-cms-corner-ne{right:-7px;top:-7px;cursor:nesw-resize}.legend-cms-corner-se{right:-7px;bottom:-7px;cursor:nwse-resize}.legend-cms-corner-sw{left:-7px;bottom:-7px;cursor:nesw-resize}
      .legend-cms-edge-handle:hover{background:#d4ad451f!important}
      body.legend-cms-editing{display:grid;grid-template-columns:minmax(0,1fr) minmax(20rem,24rem);height:100dvh;min-height:0;margin:0;overflow:hidden}
      body.legend-cms-editing.legend-cms-panel-hidden{grid-template-columns:minmax(0,1fr)}
      .legend-cms-preview{width:100%;max-width:100%;min-width:0;min-height:0;height:100%;overflow-y:auto;overflow-x:hidden;overscroll-behavior-x:none;position:relative;transform:translateZ(0)}
      .legend-cms-editor{font-family:Inter,system-ui,sans-serif;box-sizing:border-box}
      .legend-cms-editor *{box-sizing:border-box}
      .legend-cms-editor [hidden]{display:none}
      .legend-cms-preview,.legend-cms-panel{scrollbar-width:none}
      .legend-cms-preview::-webkit-scrollbar,.legend-cms-panel::-webkit-scrollbar{display:none}
      .legend-cms-bar{display:flex;flex-wrap:wrap;align-items:center;gap:8px;padding:14px 0;background:#081a3a;color:#fff;border-bottom:1px solid #344766;margin:0 0 12px;position:sticky;top:-20px;z-index:2}
      .legend-cms-bar button{min-height:38px;border-radius:8px;padding:8px 12px;border:1px solid #50617e;background:#142c50;color:#fff;font-weight:650}
      .legend-cms-bar .primary{background:#d4ad45;color:#081a3a}
      .legend-cms-panel{min-width:0;min-height:0;height:100%;overflow:auto;background:#081a3a;color:#f7f6f2;border:1px solid #d4ad45;border-radius:0;padding:20px;padding-bottom:max(20px,env(safe-area-inset-bottom))}
      body.legend-cms-panel-hidden .legend-cms-panel{display:none}
      .legend-cms-draft-dialog{width:min(500px,calc(100vw - 32px));height:auto;max-height:calc(100dvh - 32px);border-radius:16px}.legend-cms-draft-dialog::backdrop{background:#0009}.legend-cms-draft-dialog label{display:grid;gap:8px;margin:16px 0}.legend-cms-draft-dialog button{padding:10px 16px;margin-right:8px}
      .legend-cms-code-dialog{width:min(980px,calc(100vw - 32px));height:min(78dvh,760px);max-height:calc(100dvh - 32px);display:grid;grid-template-rows:auto auto minmax(220px,1fr) auto auto;gap:12px;padding:20px;border:1px solid #d4ad45;border-radius:16px;background:#081a3a;color:#f7f6f2}.legend-cms-code-dialog::backdrop{background:#000a}.legend-cms-code-dialog h2,.legend-cms-code-dialog p{margin:0}.legend-cms-code-source{width:100%;min-width:0;min-height:220px;resize:none;padding:14px;border:1px solid #50617e;border-radius:10px;background:#07152d;color:#f7f6f2;font:13px/1.5 ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;tab-size:2}.legend-cms-code-actions{display:flex;gap:10px;justify-content:flex-end}.legend-cms-code-actions button,#legend-cms-code-group button{padding:10px 14px;border:1px solid #50617e;border-radius:10px;background:#142c50;color:#fff;font-weight:700}
      .legend-cms-panel-toggle{position:fixed;z-index:2147483000;top:max(10px,env(safe-area-inset-top));right:10px;min-height:40px;padding:8px 12px;border:1px solid #d4ad45;border-radius:999px;background:#081a3af2;color:#fff;font:700 14px/1.2 Inter,system-ui,sans-serif;cursor:pointer;box-shadow:0 8px 24px #0005}
      .legend-cms-panel h2{margin:0 0 4px;font-size:19px}.legend-cms-panel small{display:block;color:#b8c6dc;margin-bottom:14px;overflow-wrap:anywhere}
      .legend-cms-group{display:grid;gap:7px;margin:12px 0}.legend-cms-group label{font-size:12px;font-weight:800;color:#e2d5b8}
      .legend-cms-row{display:grid;grid-template-columns:1fr 1fr;gap:8px}
      .legend-cms-theme{display:grid;grid-template-columns:repeat(2,1fr);gap:8px}
      .legend-cms-theme label{font-size:11px;font-weight:800}.legend-cms-theme input{width:100%;height:36px;border:0;background:transparent}
      .legend-cms-favicon-preview{display:block;width:64px;height:64px;object-fit:contain;border-radius:12px;background:#fff;padding:6px;border:1px solid #50617e}.legend-cms-favicon button{width:100%;padding:10px 12px;border:1px solid #50617e;border-radius:10px;background:#142c50;color:#fff;text-align:center}
      .legend-cms-panel button{cursor:pointer}.legend-cms-inline-help{margin:8px 0 14px;padding:10px 12px;border:1px solid #344766;border-radius:10px;background:#10284a;color:#e7eef8}.legend-cms-menu{display:grid;gap:10px}.legend-cms-menu button,.legend-cms-panel section>button{padding:13px;border:1px solid #50617e;border-radius:12px;background:#142c50;color:#fff;text-align:left}.legend-cms-panel input,.legend-cms-panel textarea,.legend-cms-panel select{width:100%;min-width:0;max-width:100%;color:#f7f6f2;background:#142c50;border:1px solid #50617e;border-radius:8px;padding:8px}.legend-cms-panel :focus-visible{outline:2px solid #f0cf78;outline-offset:3px}
      .legend-cms-panel input[type=checkbox]{width:auto}.legend-cms-panel input[type=color]{min-height:40px;padding:4px}.legend-cms-panel button:disabled{opacity:.45;cursor:default}
      .legend-cms-navigation{margin:0 0 20px}.legend-cms-tabs{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:6px}.legend-cms-tabs button{min-height:40px;padding:8px 4px;border:1px solid #344766;border-radius:8px;background:transparent;color:#c9d5e7;font:600 12px/1.3 Inter,system-ui,sans-serif}.legend-cms-tabs button[aria-pressed=true]{background:#e6c77e;color:#10213e;border-color:#e6c77e}
      #legend-cms-status{flex-basis:100%;font-size:12px;color:#c9d5e7;order:1}.legend-cms-panel p{font-size:13px;line-height:1.6;color:#b8c6dc}.legend-cms-layer-list{display:grid;gap:6px}.legend-cms-layer{display:flex;gap:4px;min-width:0}.legend-cms-layer button{min-width:0;padding:10px;border:1px solid #344766;background:#142c50;border-radius:8px;color:#f7f6f2;text-align:left;font-size:12px;overflow-wrap:anywhere}.legend-cms-layer button:first-child{flex:1}.legend-cms-layer button[aria-pressed=true]{border-color:#e6c77e}.legend-cms-section-layer{align-items:stretch}.legend-cms-layer-drag{display:grid;place-items:center;width:28px;flex:0 0 28px;border:1px solid #344766;border-radius:8px;color:#d4ad45;cursor:grab;user-select:none}.legend-cms-layer-dragging{opacity:.55}.legend-cms-layer-drop{outline:1px solid #d4ad45;outline-offset:2px}.legend-cms-search-preview{padding:16px;border:1px solid #344766;border-radius:12px;overflow-wrap:anywhere}.legend-cms-search-preview strong{color:#e6c77e}
      .legend-cms-media-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:10px}.legend-cms-media-card{display:grid;gap:7px;min-width:0;padding:10px;border:1px solid #344766;border-radius:12px;background:#10284a}.legend-cms-media-card img,.legend-cms-media-card video{display:block;width:100%;aspect-ratio:4/3;object-fit:cover;border-radius:8px;background:#07152d}.legend-cms-media-card strong,.legend-cms-media-card small{overflow-wrap:anywhere}.legend-cms-media-card button{padding:9px;border:1px solid #50617e;border-radius:8px;background:#142c50;color:#fff}
      .legend-cms-component-list{display:grid;gap:8px;margin-top:12px}.legend-cms-component-row{display:grid;grid-template-columns:minmax(0,1fr) repeat(3,auto);gap:7px;align-items:start;padding:10px;border:1px solid #344766;border-radius:10px;background:#10284a}.legend-cms-component-row>div{min-width:0}.legend-cms-component-row strong,.legend-cms-component-row small{display:block;overflow-wrap:anywhere}.legend-cms-component-row button{padding:7px 9px}.cms-reusable-instance{min-width:0}.legend-cms-reusable-missing{padding:12px;border:1px dashed #c98e8e;border-radius:8px;background:#2b1717;color:#f6dede}
      .legend-cms-collaboration-roster,.legend-cms-collaboration-comments{display:grid;gap:8px;margin:10px 0 16px}.legend-cms-collaborator,.legend-cms-comment{display:grid;gap:6px;padding:10px 12px;border:1px solid #344766;border-radius:10px;background:#10284a}.legend-cms-collaborator small,.legend-cms-comment small{margin:0}.legend-cms-comment[data-depth="1"]{margin-left:18px;border-left:3px solid #d4ad45}.legend-cms-comment-head{display:flex;gap:8px;justify-content:space-between;align-items:center}.legend-cms-comment-head span{text-transform:capitalize;font-size:11px;color:#d4ad45}
      .legend-cms-signal-presets{display:grid;grid-template-columns:1fr;gap:7px;margin:10px 0 16px}.legend-cms-signal-presets>div{padding:10px 12px;border:1px solid #3e765d;border-radius:10px;background:#0d2b25;color:#d8f4e3;font-size:12px}
      .legend-cms-signal-diagnostics{display:grid;gap:8px;margin:10px 0 14px;padding:10px 12px;border:1px solid #344766;border-radius:10px;background:#0d213e}.legend-cms-signal-diagnostic{margin:0!important;padding:8px 10px;border-radius:8px}.legend-cms-signal-ok{border:1px solid #3e765d;background:#0d2b25;color:#d8f4e3!important}.legend-cms-signal-error{border:1px solid #a95858;background:#35191c;color:#ffdede!important}.legend-cms-signal-history{padding:7px 9px;border-left:3px solid #50617e;font-size:12px;color:#dce6f4;overflow-wrap:anywhere}
      .legend-cms-ai-summary{padding:10px 12px;border:1px solid #d4ad45;border-radius:10px;background:#10284a;color:#f7f6f2}.legend-cms-ai-operation{margin:7px 0;padding:9px 11px;border-left:3px solid #d4ad45;background:#0d213e;color:#dce6f4;font-size:12px;overflow-wrap:anywhere}
      .legend-cms-motion-row{display:grid;gap:8px;margin:10px 0;padding:10px 12px;border:1px solid #344766;border-radius:10px;background:#10284a}.legend-cms-motion-row .legend-cms-group{margin:4px 0}.legend-cms-motion-row>.legend-cms-row{align-items:end}
      .legend-cms-quality-list{display:grid;gap:8px;margin:10px 0 18px}.legend-cms-quality-item{display:grid;grid-template-columns:auto minmax(0,1fr);gap:9px;align-items:start;padding:10px 12px;border:1px solid #344766;border-radius:10px;background:#10284a}.legend-cms-quality-item strong{font-size:10px;letter-spacing:.08em;color:#e6c77e}.legend-cms-quality-item span{font-size:12px;line-height:1.45;color:#f7f6f2}.legend-cms-quality-error{border-color:#e6a6a6}.legend-cms-quality-warning{border-color:#e6c77e}.legend-cms-quality-ok{padding:10px 12px;border:1px solid #3e765d;border-radius:10px;color:#d8f4e3;background:#0d2b25}
      .cms-extra-image{display:block;margin-left:auto;margin-right:auto;height:auto}
      @media(max-width:800px){html{max-width:100%;overflow-x:hidden}body.legend-cms-editing{width:100%;max-width:100%;grid-template-columns:minmax(0,1fr);grid-template-rows:minmax(0,55fr) minmax(0,45fr);overflow-x:hidden}body.legend-cms-editing.legend-cms-panel-hidden{grid-template-rows:minmax(0,1fr)}.legend-cms-preview{width:100%;max-width:100%;overflow-x:hidden;overscroll-behavior-x:none;touch-action:pan-y}.legend-cms-preview>*:not(.legend-cms-grid-overlay):not(.legend-cms-selection-frame){max-width:100%;min-width:0}.legend-cms-panel{width:100%;max-width:100%;min-width:0;overflow-x:hidden;border-top:2px solid #d4ad45}.legend-cms-panel-toggle{top:max(8px,env(safe-area-inset-top));right:8px}}
    `;
    document.head.appendChild(style);
  }

  function buildEditor() {
    injectEditorStyles();
    const preview = document.createElement('div');
    preview.className = 'legend-cms-preview';
    preview.setAttribute('role', 'region');
    preview.setAttribute('aria-label', 'Website preview');
    preview.tabIndex = 0;
    Array.from(document.body.children).forEach(child => {
      if (!['SCRIPT', 'STYLE'].includes(child.tagName)) preview.appendChild(child);
    });
    document.body.appendChild(preview);
    document.body.classList.add('legend-cms-editing');

    const panel = document.createElement('aside');
    panel.className = 'legend-cms-editor legend-cms-panel';
    panel.setAttribute('aria-labelledby', 'legend-cms-heading');
    panel.innerHTML = `
      <h2 id="legend-cms-heading">Website studio</h2>
      <small id="legend-cms-selected-label">Select content on the page</small>
      <div id="legend-cms-inline-help" class="legend-cms-inline-help" hidden><span>Single click selects. Use the gold Move control to position. Resize only from the selected border edges or corners. Double-click text, or choose Edit text, to type.</span><button id="legend-cms-edit-text" type="button">Edit text</button></div>
      <div id="legend-cms-code-group" class="legend-cms-group" hidden>
        <button id="legend-cms-edit-code" type="button">Edit code in modal</button>
        <small>Custom HTML, CSS, and browser JavaScript are previewed inside a sandboxed block. Resize the block directly on the page.</small>
      </div>
      <div id="legend-cms-image-group" class="legend-cms-group" hidden>
        <label for="legend-cms-image">Replace image</label>
        <input id="legend-cms-image" type="file" accept="image/jpeg,image/png,image/webp">
      </div>
      <div class="legend-cms-row">
        <div class="legend-cms-group"><label for="legend-cms-scale">Text scale</label><input id="legend-cms-scale" type="number" min="0" step="any" value="1"></div>
        <div class="legend-cms-group"><label for="legend-cms-width">Width %</label><input id="legend-cms-width" type="number" min="0" step="any" value="100"></div>
      </div>
      <div class="legend-cms-row">
        <div class="legend-cms-group"><label for="legend-cms-height">Height px</label><input id="legend-cms-height" type="number" min="0" step="any" placeholder="Auto"></div>
        <div class="legend-cms-group"><label for="legend-cms-align">Alignment</label><select id="legend-cms-align"><option value="">Default</option><option value="left">Left</option><option value="center">Center</option><option value="right">Right</option><option value="start">Start</option><option value="end">End</option><option value="justify">Justify</option></select></div>
      </div>
      <div class="legend-cms-row">
        <div class="legend-cms-group"><label for="legend-cms-padding-top">Top spacing</label><input id="legend-cms-padding-top" type="number" min="0" step="any" value="0"></div>
        <div class="legend-cms-group"><label for="legend-cms-padding-bottom">Bottom spacing</label><input id="legend-cms-padding-bottom" type="number" min="0" step="any" value="0"></div>
      </div>
      <div class="legend-cms-group"><label><input id="legend-cms-hidden" type="checkbox"> Hide selected content</label></div>
      <div class="legend-cms-group"><label>Site colors</label>
        <div class="legend-cms-theme">
          <label>Navy<input data-theme-key="navy" type="color" value="#102b62"></label>
          <label>Deep navy<input data-theme-key="navyDeep" type="color" value="#081a3a"></label>
          <label>Gold<input data-theme-key="gold" type="color" value="#d4ad45"></label>
          <label>Bright gold<input data-theme-key="goldStrong" type="color" value="#f0cf78"></label><label>Surface<input data-theme-key="surface" type="color" value="#ffffff"></label><label>Text<input data-theme-key="text" type="color" value="#101a35"></label><label>Muted<input data-theme-key="muted" type="color" value="#667085"></label><label>Font family<input data-theme-key="fontFamily" type="text" value=""></label>
        </div>
      </div>
    `;
    document.body.appendChild(panel);

    const panelToggle = document.createElement('button');
    panelToggle.type = 'button';
    panelToggle.id = 'legend-cms-panel-toggle';
    panelToggle.className = 'legend-cms-editor legend-cms-panel-toggle';
    panelToggle.textContent = 'Full-page canvas';
    panelToggle.setAttribute('aria-controls', 'legend-cms-heading');
    panelToggle.setAttribute('aria-expanded', 'true');
    panelToggle.addEventListener('click', () => {
      const hidden = document.body.classList.toggle('legend-cms-panel-hidden');
      panelToggle.textContent = hidden ? 'Open controls' : 'Full-page canvas';
      panelToggle.setAttribute('aria-expanded', hidden ? 'false' : 'true');
      const refresh = () => { refreshScaledElements(); updateDirectCanvasUi(); };
      if (typeof requestAnimationFrame === 'function') requestAnimationFrame(refresh);
      else refresh();
    });
    document.body.appendChild(panelToggle);
    window.addEventListener('beforeunload', event => {
      if (!dirty) return;
      event.preventDefault();
      event.returnValue = '';
    });

    const bar = document.createElement('div');
    bar.className = 'legend-cms-editor legend-cms-bar';
    bar.innerHTML = `
      <button id="legend-cms-save">Save draft</button><button class="primary" id="legend-cms-publish">Publish</button>
      <span id="legend-cms-status" role="status" aria-live="polite">Draft editor</span>
      <input id="legend-cms-extra-image" type="file" accept="image/jpeg,image/png,image/webp" hidden>
      <button id="legend-cms-up">Section ↑</button>
      <button id="legend-cms-down">Section ↓</button>
      <button id="legend-cms-reset">Reset selected</button><button id="legend-cms-remove">Delete selected</button>
      <button id="legend-cms-exit">Exit</button>
    `;
    panel.insertBefore(bar, panel.firstChild);
    enhanceEditor(panel, preview);
    refreshScaledElements();
    if (typeof ResizeObserver !== 'undefined') new ResizeObserver(() => { refreshScaledElements(); updateDirectCanvasUi(); }).observe(preview);
    syncEditorControls();

    document.addEventListener('click', event => {
      const target = editorSelectionTarget(event.target);
      if (!target || target.closest('.legend-cms-editor')) return;
      if (target !== selected) setSelected(target);
      if (target.tagName === 'A' || target.tagName === 'BUTTON') event.preventDefault();
      event.stopPropagation();
    }, true);
    document.addEventListener('dblclick', event => {
      const target = editorSelectionTarget(event.target);
      if (!target || target.closest('.legend-cms-editor') || !isInlineEditable(target)) return;
      if (target !== selected) setSelected(target);
      activateInlineEditing(target);
      target.focus?.({ preventScroll: true });
      event.preventDefault();
      event.stopPropagation();
    }, true);
    document.getElementById('legend-cms-edit-text')?.addEventListener('click', () => {
      if (!selected || !isInlineEditable(selected)) return;
      activateInlineEditing(selected);
      selected.focus?.({ preventScroll: true });
    });

    ['legend-cms-scale','legend-cms-width','legend-cms-height','legend-cms-padding-top','legend-cms-padding-bottom','legend-cms-offset-x','legend-cms-offset-y','legend-cms-align','legend-cms-hidden']
      .forEach(id => document.getElementById(id)?.addEventListener('input', updateSelectedFromControls));

    document.getElementById('legend-cms-image')?.addEventListener('change', e => {
      const file = e.target.files?.[0];
      if (!selected || !(selected instanceof HTMLImageElement)) return;
      const imageTarget = selected;
      readImage(file, dataUrl => {
        if (selected !== imageTarget) return;
        checkpoint();
        const ov = selectedOverride();
        if (!ov) return;
        ov.imageDataUrl = dataUrl;
        selected.src = mediaUrl(dataUrl);
        markDirty();
      });
    });

    document.getElementById('legend-cms-favicon')?.addEventListener('change', e => {
      const file = e.target.files?.[0];
      readImage(file, dataUrl => {
        checkpoint();
        documentState.faviconImageDataUrl = dataUrl;
        applyFavicon(dataUrl);
        syncFaviconControls();
        markDirty();
      });
    });
    document.getElementById('legend-cms-favicon-remove')?.addEventListener('click', () => {
      if (!documentState.faviconImageDataUrl) return;
      checkpoint();
      markDeleted('site:favicon');
      documentState.faviconImageDataUrl = null;
      applyFavicon(null);
      syncFaviconControls();
      markDirty();
    });
    syncFaviconControls();

    document.querySelectorAll('[data-theme-key]').forEach(input => {
      const key = input.dataset.themeKey;
      const themeVariables = { navy: '--web-navy', navyDeep: '--web-navy-deep', gold: '--web-gold', goldStrong: '--web-gold-strong',surface:'--web-surface',text:'--web-ink',muted:'--web-muted',fontFamily:'--web-font' };
      const currentColor = documentState.theme?.[key]
        || getComputedStyle(document.documentElement).getPropertyValue(themeVariables[key]).trim();
      if (input.type !== 'color' || /^#[0-9a-f]{6}$/i.test(currentColor)) input.value = currentColor;
      input.addEventListener('input', () => {
        checkpoint();
        documentState.theme[key] = input.value;
        applyTheme(documentState.theme);
        markDirty();
      });
    });

    document.getElementById('legend-cms-save')?.addEventListener('click', chooseDraft);
    document.getElementById('legend-cms-publish')?.addEventListener('click', () => save(true));
    document.getElementById('legend-cms-extra-image')?.addEventListener('change', e => addImage(e.target.files?.[0]));
    document.getElementById('legend-cms-up')?.addEventListener('click', () => moveSelectedSection(-1));
    document.getElementById('legend-cms-down')?.addEventListener('click', () => moveSelectedSection(1));
    document.getElementById('legend-cms-remove')?.addEventListener('click', () => {
      if (!selected) return;
      const serviceCard = businessServiceCardFor(selected);
      if (serviceCard) {
        if (serviceCard.dataset.cmsExtraId) { setSelected(serviceCard); removeSelected(); return; }
        checkpoint(); const ov = ensureOverride(serviceCard.dataset.cmsId); ov.hidden = true; serviceCard.hidden = true; setSelected(null); markDirty(); return;
      }
      if (selected.dataset.cmsExtraId) { removeSelected(); return; }
      checkpoint(); const ov = selectedOverride(); ov.hidden = true; selected.hidden = true; setSelected(null); markDirty();
    });
    document.getElementById('legend-cms-reset')?.addEventListener('click', removeSelected);
    document.getElementById('legend-cms-exit')?.addEventListener('click', () => {
      if (dirty && !confirm('Exit with unsaved changes?')) return;
      const url = new URL(location.href);
      url.searchParams.delete('legendEdit');
      location.href = url.toString();
    });
  }

  async function loadEditor() {
    try {
      const url = new URL(`${API_BASE}/api/website-content/manage`);
      url.searchParams.set('ticket', editorTicket);
      const response = await fetch(url, { cache: 'no-store' });
      if (!response.ok) throw new Error('This edit session has expired.');
      const payload = await response.json();
      if (payload.siteKey && payload.siteKey !== SITE_KEY) throw new Error('This edit session belongs to a different website. Open it from your profile.');
      bindBusiness(payload);
      managementPayload = payload;
      ctaCatalog = Array.isArray(payload.ctaCatalog?.options) ? payload.ctaCatalog.options : [];
      if (customPage) {
        const pages = normalizeDocument(payload.document).pages;
        if (!pages[customPage]) throw new Error('This page is not part of the authorized website draft.');
        document.querySelector('main').replaceChildren();
      }
      prepareDom();
      revision = payload.revision;
      namedDrafts = payload.drafts || [];
      applyDocument(payload.document || {});
      preservePreviewNavigation();
      signalCatalog = Array.isArray(payload.signalCatalog?.events) && Array.isArray(payload.signalCatalog?.matchingFields)
        ? payload.signalCatalog : null;
      buildEditor();
      installPageSelector();
      renderSignalControls();
      const publishButton = document.getElementById('legend-cms-publish'); if (publishButton && payload.capabilities?.canPublish === false) { publishButton.disabled = true; publishButton.title = 'An owner must publish this draft.'; }
      document.documentElement.hidden = false;
    } catch (error) {
      unavailable(error);
      console.error('[legend-cms]', error);
      alert(error?.message || 'Unable to open website editor.');
    }
  }

  if (renderInput) {
    bindBusiness(renderInput);
    prepareDom();
    injectContentStyles();
    applyDocument(renderInput.document || {});
    document.documentElement.hidden = false;
    window.LEGEND_PUBLIC_CMS_RENDER_COMPLETE = true;
    if (!renderInput.server) {
      if (!editorMode) void startPublicRuntime();
      window.addEventListener('resize', refreshResponsiveOverrides);
      if (document.fonts?.ready) document.fonts.ready.then(refreshScaledElements);
    }
    return;
  }

  document.addEventListener('DOMContentLoaded', async () => {
    injectContentStyles();
    if (editorMode) await loadEditor();
    else {
      try { await loadPublic(); await startPublicRuntime(); }
      catch (error) { unavailable(error); }
    }
    window.addEventListener('resize', refreshResponsiveOverrides);
    if (document.fonts?.ready) document.fonts.ready.then(refreshScaledElements);
  }, { once: true });
})();
