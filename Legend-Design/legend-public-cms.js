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
  let documentState = { version: 1, faviconImageDataUrl: null, elements: {}, sectionOrder: {}, extras: [], theme: {} };
  let signalCatalog = null;
  let ctaCatalog = [];
  let selected = null;
  let selectedSection = null;
  let dirty = false;
  let autoSaveTimer = null;
  const originals = new WeakMap();
  const scaledElements = new Map();
  const styleProperties = ['textAlign', 'fontSize', 'width', 'maxWidth', 'paddingTop', 'paddingBottom', 'objectPosition', 'color', 'backgroundColor', 'backgroundImage', 'fontFamily', 'fontWeight', 'lineHeight', 'letterSpacing', 'paddingLeft', 'paddingRight', 'borderRadius', 'objectFit', 'gridColumn', 'minWidth', 'overflowWrap'];

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
      version: 1,
      faviconImageDataUrl: typeof input?.faviconImageDataUrl === 'string' ? input.faviconImageDataUrl : null,
      elements: input?.elements && typeof input.elements === 'object' ? input.elements : {},
      sectionOrder: input?.sectionOrder && typeof input.sectionOrder === 'object' ? input.sectionOrder : {},
      extras: Array.isArray(input?.extras) ? input.extras : [],
      theme: input?.theme && typeof input.theme === 'object' ? input.theme : {},
      pages
    };
  }

  function pageState() {
    documentState.pages ||= {};
    const pathname = customPage || location.pathname.replace(/^\/business-preview/, '').replace(/\/$/, '') || '/';
    const routeKey = pathname;
    if (!documentState.pages[routeKey]) {
      const belongs = id => id.startsWith(`${pageKey}.`) || id.startsWith(`section:${pageKey}.`);
      const elements = Object.fromEntries(Object.entries(documentState.elements).filter(([id]) => belongs(id)));
      const sectionOrder = Object.fromEntries(Object.entries(documentState.sectionOrder).filter(([id]) => belongs(id)));
      const extras = documentState.extras.filter(extra => belongs(extra.sectionId || ''));
      documentState.pages[routeKey] = { elements, sectionOrder, extras };
      Object.keys(elements).forEach(id => delete documentState.elements[id]);
      Object.keys(sectionOrder).forEach(id => delete documentState.sectionOrder[id]);
      documentState.extras = documentState.extras.filter(extra => !extras.includes(extra));
    }
    const page = documentState.pages[routeKey]; page.elements ||= {}; page.sectionOrder ||= {}; page.extras ||= [];
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
    if (editableInteractiveTags.has(el.tagName)) {
      return true;
    }
    return false;
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
        el.dataset.cmsEditable = 'true';
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
      const remove = document.createElement('button'); remove.type = 'button'; remove.textContent = 'Remove mapping';
      remove.addEventListener('click', () => { checkpoint(); overrides.signals = bindings.filter(x => x.id !== binding.id); markDirty(); renderSignalControls(); }); host.appendChild(remove);
    }
    const add = document.createElement('button'); add.type = 'button'; add.textContent = 'Add interaction mapping';
    add.disabled = bindings.length >= 8 || !candidates.length;
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

  function applyElementOverride(el, override) {
    if (el?.dataset.cmsSignalOnly) return;
    if (!el || !override) return;
    if (override.actionKey) el.dataset.websiteActionKey = override.actionKey;
    else delete el.dataset.websiteActionKey;
    if (override.hidden === true) el.hidden = true;
    else if (override.hidden === false) el.hidden = false;

    if (el instanceof HTMLImageElement) {
      if (override.imageDataUrl) el.src = mediaUrl(override.imageDataUrl);
    } else if (override.text != null && !el.dataset.cmsSection && !['DIV','ARTICLE','HEADER','FOOTER'].includes(el.tagName)) {
      setContentText(el, override.text, true);
    }

    if (override.href != null && el.tagName === 'A' && safeUrl(override.href)) { el.href = override.href; el.target = override.target === '_blank' ? '_blank' : '_self'; el.rel = 'noopener noreferrer'; }
    if (override.alt != null && el.tagName === 'IMG') el.alt = override.alt;
    if (override.videoUrl && el.tagName === 'VIDEO' && safeUrl(override.videoUrl, true)) el.src = mediaUrl(override.videoUrl);
    applyStyle(el, override.style);
  }

  function createExtra(extra) {
    const section = extra.type === 'section' ? document.querySelector('main') : document.querySelector(`[data-cms-section="${CSS.escape(extra.sectionId)}"]`);
    if (!section) return null;
    let el;
    if (extra.type === 'image') {
      el = document.createElement('img');
      el.src = mediaUrl(extra.imageDataUrl || '');
      el.alt = '';
      el.className = 'cms-extra cms-extra-image';
    } else if (extra.type === 'section') {
      el = document.createElement('section'); el.dataset.cmsSection = `extra:${extra.id}`; el.className = 'cms-extra cms-extra-section';
    } else if (extra.type === 'video') {
      el = document.createElement('video'); el.controls = true; el.preload = 'metadata'; if (safeUrl(extra.videoUrl, true)) el.src = mediaUrl(extra.videoUrl); el.className = 'cms-extra';
    } else if (extra.type === 'card') {
      el = document.createElement('article'); el.className = 'cms-extra card cms-extra-card';
      const heading = document.createElement('h3'); setContentText(heading, extra.title || 'New service', true);
      const copy = document.createElement('p'); setContentText(copy, extra.text || '', true);
      for (const [node, field] of [[heading, 'title'], [copy, 'text']]) {
        node.dataset.cmsExtraId = extra.id; node.dataset.cmsExtraField = field;
        node.dataset.cmsId = `extra:${extra.id}:${field}`; node.dataset.cmsEditable = 'true';
      }
      el.append(heading, copy);
    } else if (extra.type === 'button') {
      el = document.createElement('a'); el.textContent = extra.text || 'New button'; if (safeUrl(extra.href)) el.href = extra.href; el.className = 'cms-extra btn primary';
    } else {
      el = document.createElement('p');
      el.textContent = extra.text || '';
      el.className = 'cms-extra cms-extra-text';
    }
    el.dataset.cmsExtraId = extra.id;
    el.dataset.cmsId = `extra:${extra.id}`;
    el.dataset.cmsEditable = 'true';
    section.appendChild(el);
    applyElementOverride(el, extra);
    return el;
  }

  function applyDocument(doc) {
    documentState = normalizeDocument(doc);
    applyTheme(documentState.theme);
    applyFavicon(documentState.faviconImageDataUrl);
    const metadata = pageState();
    document.title = metadata.title ?? originalTitle;
    const description = document.querySelector('meta[name="description"]');
    if (description) description.setAttribute('content', metadata.description ?? originalDescription);

    Object.entries(pageState().elements).forEach(([id, override]) => {
      const el = document.querySelector(`[data-cms-id="${CSS.escape(id)}"]`);
      applyElementOverride(el, override);
    });

    const sections = Array.from(document.querySelectorAll('[data-cms-section]')).filter(section => !section.matches('.site-header,.site-footer'));
    sections.sort((a,b) => {
      const ai = pageState().sectionOrder[a.dataset.cmsSection] ?? sections.indexOf(a);
      const bi = pageState().sectionOrder[b.dataset.cmsSection] ?? sections.indexOf(b);
      return ai - bi;
    });
    const parentGroups = new Map();
    sections.forEach(section => {
      const parent = section.parentElement;
      if (!parent) return;
      if (!parentGroups.has(parent)) parentGroups.set(parent, []);
      parentGroups.get(parent).push(section);
    });
    parentGroups.forEach(group => group.forEach(section => section.parentElement.appendChild(section)));

    document.querySelectorAll('.cms-extra').forEach(x => { scaledElements.delete(x); x.remove(); });
    pageState().extras.filter(x => x.type === 'section').forEach(createExtra);
    pageState().extras.filter(x => x.type !== 'section').forEach(createExtra);
    Object.entries(pageState().elements).forEach(([id, ov]) => applyPlacement(document.querySelector(`[data-cms-id="${CSS.escape(id)}"]`), ov.placement));
    pageState().extras.forEach(extra => applyPlacement(document.querySelector(`[data-cms-id="extra:${CSS.escape(extra.id)}"]`), extra.placement));
  }

  function bindBusiness(payload) {
    const business = payload.business;
    if (SITE_KEY !== 'business') return;
    if (!business?.id || !business.displayName) throw new Error('This business website is unavailable.');
    BUSINESS_ID = business.id;
    document.querySelectorAll('[data-business-name]').forEach(el => { el.textContent = business.displayName; });
    document.querySelectorAll('[data-business-field]').forEach(el => { const value = business[el.dataset.businessField]; el.textContent = value || ''; el.hidden = !value; });

  }
  function installPageSelector(payload) {
    const panel = document.querySelector('.legend-cms-panel');
    if (!panel) return;
    const label = document.createElement('label'); label.className = 'legend-cms-group'; label.textContent = 'Website page';
    const select = document.createElement('select'); select.id = 'legend-cms-page-select'; select.setAttribute('aria-label', 'Website page'); label.appendChild(select);
    const prefix = SITE_KEY === 'protect' ? (payload.agentSlug ? `/a/${encodeURIComponent(payload.agentSlug)}` : context.pagePrefix || '') : '';
    const current = customPage || (SITE_KEY === 'business' ? location.pathname.replace(/^\/business-preview/, '') : location.pathname.slice(prefix.length)) || '/';
    const normalize = path => path.replace(/\/$/, '') || '/';
    const entries = new Map();
    for (const page of context.pages || []) {
      if (typeof page.path === 'string' && /^\/(?:[a-z0-9_-]+\/?)*$/i.test(page.path)) entries.set(normalize(page.path), { label: page.label || page.path, template: true });
    }
    // Business imported/custom routes are part of this authorized website document,
    // even when they have no template or navigation link.
    if (SITE_KEY === 'business') for (const [path, page] of Object.entries(documentState.pages)) {
      if (/^\/(?:[a-z0-9_-]+\/?)*$/i.test(path)) entries.set(normalize(path), { ...entries.get(normalize(path)), label: page.title || entries.get(normalize(path))?.label || path });
    }
    if (!entries.has(normalize(current))) entries.set(normalize(current), { label: document.title || current, template: !customPage });
    for (const [path, page] of entries) {
      const option = document.createElement('option'); option.value = path; option.textContent = page.label; select.appendChild(option);
    }
    select.value = normalize(current);
    select.addEventListener('change', async () => {
      const route = select.value; if (!entries.has(route)) return;
      select.disabled = true;
      try {
        if (saving) { document.getElementById('legend-cms-status').textContent = 'Wait for the current save to finish, then choose a page.'; return; }
        if (dirty) { const saved = await save(false); if (!saved || dirty) return; }
        const url = new URL(location.origin);
        if (SITE_KEY === 'business') {
          url.pathname = '/business-preview/' + (entries.get(route).template ? route.replace(/^\//, '') : '');
          url.searchParams.set('businessId', BUSINESS_ID);
          if (!entries.get(route).template) url.searchParams.set('cmsPage', route);
        } else url.pathname = prefix + (route === '/' ? '/' : route);
        url.searchParams.set('legendEdit', editorTicket);
        location.assign(url.toString());
      } finally { select.value = normalize(current); select.disabled = false; }
    });
    panel.insertBefore(label, panel.querySelector('.legend-cms-navigation'));
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
        window.LEGEND_PUBLIC_META_SESSION = window.metaSignalIntelligence.createLandingSession({
          ...meta,
          siteKey: SITE_KEY,
          quoteType: SITE_KEY === 'business' ? 'business' : 'legend',
          pageKey,
          effectivePageKey: pageKey,
          pageVariant: SITE_KEY + '_website',
          pageMode: 'site_mode',
          formId: document.querySelector('form[data-form-key]')?.dataset.formKey || '',
          requiredContactFields: SITE_KEY === 'business' ? ['email'] : []
        });
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
    document.querySelectorAll('.legend-cms-selected').forEach(x => x.classList.remove('legend-cms-selected'));
    selected = el;
    selectedSection = currentSectionFor(el);
    if (selected) { selected.classList.add('legend-cms-selected'); selected.draggable = !selected.dataset.cmsSection && !selected.dataset.cmsSignalOnly; }
    syncEditorControls();
    renderSignalControls();
    showPanel('content');
    refreshLayers();
  }

  function selectedOverride() {
    if (!selected?.dataset?.cmsId) return null;
    if (selected.dataset.cmsExtraId) {
      return pageState().extras.find(x => x.id === selected.dataset.cmsExtraId) || null;
    }
    return ensureOverride(selected.dataset.cmsId);
  }

  function syncEditorControls() {
    const title = document.getElementById('legend-cms-selected-label');
    const text = document.getElementById('legend-cms-text');
    const imageGroup = document.getElementById('legend-cms-image-group');
    const textGroup = document.getElementById('legend-cms-text-group');
    const scale = document.getElementById('legend-cms-scale');
    const width = document.getElementById('legend-cms-width');
    const top = document.getElementById('legend-cms-padding-top');
    const bottom = document.getElementById('legend-cms-padding-bottom');
    const align = document.getElementById('legend-cms-align');
    const hidden = document.getElementById('legend-cms-hidden');

    document.querySelectorAll('[data-cms-view="content"] input,[data-cms-view="content"] textarea,[data-cms-view="content"] select,[data-cms-view="appearance"] input,[data-cms-view="appearance"] select,[data-cms-view="layout"] input,[data-cms-view="layout"] select').forEach(control => { control.disabled = !selected || !!selected.dataset.cmsSignalOnly; });
    if (!selected) {
      if (title) title.textContent = 'Select content on the page';
      if (textGroup) textGroup.hidden = true;
      if (imageGroup) imageGroup.hidden = true;
      return;
    }

    if (title) title.textContent = elementLabel(selected);
    const isImage = selected instanceof HTMLImageElement;
    if (textGroup) textGroup.hidden = isImage || !!selected.dataset.cmsSection || ['VIDEO','DIV','ARTICLE','HEADER','FOOTER'].includes(selected.tagName);
    if (imageGroup) imageGroup.hidden = !isImage;

    const ov = selected.dataset.cmsExtraId
      ? pageState().extras.find(x => x.id === selected.dataset.cmsExtraId) || {}
      : pageState().elements[selected.dataset.cmsId] || {};
    const computed = getComputedStyle(selected);
    const parentStyle = selected.parentElement ? getComputedStyle(selected.parentElement) : null;
    const parentWidth = selected.parentElement
      ? selected.parentElement.clientWidth - (parseFloat(parentStyle.paddingLeft) || 0) - (parseFloat(parentStyle.paddingRight) || 0) : 0;
    const actualWidth = parentWidth > 0 ? parseFloat(computed.width) / parentWidth * 100 : 100;
    const displayNumber = value => String(Math.round(value * 1000) / 1000);
    const targetInput = document.getElementById('legend-cms-target'); if (targetInput) targetInput.checked = (ov.target ?? selected.getAttribute('target')) === '_blank';
    if (text && !isImage) text.value = selected.dataset.cmsExtraField === 'title' ? ov.title ?? selected.textContent ?? '' : ov.text ?? selected.textContent ?? '';
    const serviceCard = businessServiceCardFor(selected);
    const duplicateButton = document.getElementById('legend-cms-duplicate'); if (duplicateButton) duplicateButton.textContent = serviceCard ? 'Duplicate service' : 'Duplicate block';
    const removeButton = document.getElementById('legend-cms-remove');
    if (removeButton) {
      const kind = serviceCard ? 'service'
        : selected.dataset.cmsSection ? 'section'
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
    if (scale) scale.value = String(ov.style?.fontScale ?? 1);
    if (width) width.value = displayNumber(ov.style?.widthPercent ?? (Number.isFinite(actualWidth) ? actualWidth : 100));
    if (top) top.value = displayNumber(ov.style?.paddingTop ?? (parseFloat(computed.paddingTop) || 0));
    if (bottom) bottom.value = displayNumber(ov.style?.paddingBottom ?? (parseFloat(computed.paddingBottom) || 0));
    if (align) align.value = ov.style?.textAlign ?? computed.textAlign ?? '';
    if (scale) scale.disabled = isImage;
    const values = { href: ov.href ?? rememberOriginal(selected).href ?? '', alt: ov.alt ?? selected.getAttribute('alt') ?? '', videoUrl: ov.videoUrl ?? selected.getAttribute('src') ?? '' };
    Object.entries(values).forEach(([key,value]) => { const input = document.getElementById(`legend-cms-${key}`); if(input) input.value = value; });
    document.querySelectorAll('[data-style-key]').forEach(input => { const key = input.dataset.styleKey; input.value = ['color','backgroundColor'].includes(key) ? colorHex(ov.style?.[key] || computed[key]) : ov.style?.[key] ?? (input.type === 'number' ? parseFloat(computed[key]) || '' : computed[key] || ''); });
    document.querySelectorAll('[data-color-hex]').forEach(input => { input.value = colorHex(ov.style?.[input.dataset.colorHex] || computed[input.dataset.colorHex]); });
    const linkGroup = document.getElementById('legend-cms-link-group'); if (linkGroup) linkGroup.hidden = selected.tagName !== 'A';
    if (selected.tagName === 'A') syncCtaControls(ov, values.href);
    const videoGroup = document.getElementById('legend-cms-video-group'); if (videoGroup) videoGroup.hidden = selected.tagName !== 'VIDEO';
    if (hidden) hidden.checked = ov.hidden === true || selected.hidden;
  }

  function updateSelectedFromControls(event) {
    if (!selected) return;
    const control = event.target;
    if (control.validity?.badInput) return;
    const fields = {
      'legend-cms-scale': 'fontScale', 'legend-cms-width': 'widthPercent',
      'legend-cms-padding-top': 'paddingTop', 'legend-cms-padding-bottom': 'paddingBottom'
    };
    const field = fields[control.id];
    if (field && control.value !== '') {
      const value = Number(control.value);
      const valid = field.startsWith('padding') ? spacingNumber(value) : positiveNumber(value);
      if (!valid) { control.setCustomValidity('Enter a finite ' + (field.startsWith('padding') ? 'nonnegative' : 'positive') + ' number.'); return; }
    }
    control.setCustomValidity('');
    checkpoint();
    const ov = selectedOverride();
    if (!ov) return;
    if (control.id === 'legend-cms-text' && !(selected instanceof HTMLImageElement) && !selected.dataset.cmsSection && !['DIV','ARTICLE','HEADER','FOOTER'].includes(selected.tagName)) {
      if (selected.dataset.cmsExtraField === 'title') ov.title = control.value;
      else ov.text = control.value;
      setContentText(selected, control.value, true);
    } else if (control.id === 'legend-cms-hidden') {
      ov.hidden = control.checked;
      selected.hidden = control.checked;
    } else {
      ov.style ||= {};
      if (field) {
        if (control.value === '') delete ov.style[field];
        else ov.style[field] = Number(control.value);
      } else if (control.id === 'legend-cms-align') {
        if (control.value) ov.style.textAlign = control.value;
        else delete ov.style.textAlign;
      } else return;
      applyStyle(selected, ov.style);
    }
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
    if (!selectedSection) return;
    checkpoint();
    const parent = selectedSection.parentElement;
    if (!parent) return;
    const sections = Array.from(parent.children).filter(x => x.dataset?.cmsSection);
    const index = sections.indexOf(selectedSection);
    const target = index + delta;
    if (index < 0 || target < 0 || target >= sections.length) return;
    if (delta < 0) parent.insertBefore(selectedSection, sections[target]);
    else parent.insertBefore(sections[target], selectedSection);
    Array.from(parent.children)
      .filter(x => x.dataset?.cmsSection)
      .forEach((section, i) => {
        pageState().sectionOrder[section.dataset.cmsSection] = i;
      });
    markDirty();
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
    if (selected.dataset.cmsSignalOnly) { delete pageState().elements[selected.dataset.cmsId]; markDirty(); renderSignalControls(); return; }
    if (selected.dataset.cmsExtraId) {
      const removedId = selected.dataset.cmsExtraId;
      const removedSection = selected.dataset.cmsSection;
      pageState().extras = pageState().extras.filter(x => x.id !== removedId && (!removedSection || (x.sectionId !== removedSection && x.placement?.sectionId !== removedSection)));
      const removedNode = document.querySelector(`[data-cms-id="extra:${CSS.escape(removedId)}"]`) || selected;
      scaledElements.delete(removedNode);
      removedNode.remove();
      setSelected(null);
      markDirty();
      return;
    }
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
    let saved = false;
    try {
      const response = await fetch(`${API_BASE}/api/website-content/${publish ? 'manage/publish' : 'manage'}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ticket: editorTicket, document: JSON.parse(submitted), expectedRevision: revision, ...(namedDraft || {}) })
      });
      if (!response.ok) { const error = await response.json().catch(() => ({})); throw new Error(error.message || error.error || `Save failed (${response.status})`); }
      const payload = await response.json();
      const changedDuringSave = JSON.stringify(documentState) !== submitted;
      if (!changedDuringSave) documentState = normalizeDocument(payload.document || documentState);
      revision = payload.revision ?? revision;
      namedDrafts = payload.drafts || namedDrafts;
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
  function checkpoint() { undoStack.push(JSON.stringify(documentState)); if (undoStack.length > 80) undoStack.shift(); redoStack.length = 0; }
  function restoreHistory(from, to) {
    if (!from.length) return;
    to.push(JSON.stringify(documentState));
    baselineNodes.forEach(({ el, parent, next }) => { if (el.dataset.cmsSignalOnly) return; if (parent) parent.insertBefore(el, next?.parentElement === parent ? next : null); const original = rememberOriginal(el); el.hidden = original.hidden; if (!el.dataset.cmsSection && !['DIV','ARTICLE','HEADER','FOOTER'].includes(el.tagName)) { setContentText(el, original.text); } if (original.href != null) el.setAttribute('href',original.href); if (original.src != null) el.setAttribute('src',original.src); applyStyle(el, null); });
    applyDocument(JSON.parse(from.pop())); setSelected(null); markDirty();
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
  function showPanel(name) {
    document.querySelectorAll('[data-cms-view]').forEach(view => { view.hidden = view.dataset.cmsView !== name; });
    document.querySelectorAll('[data-open]').forEach(button => button.setAttribute('aria-pressed', String(button.dataset.open === name)));
    if (name === 'layers') refreshLayers();
    if (name === 'page') syncPageControls();
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
    document.querySelectorAll('[data-cms-editable="true"]').forEach(el => {
      if (el.closest('.legend-cms-editor')) return;
      const label = elementLabel(el);
      if (filter && !label.toLowerCase().includes(filter)) return;
      const row = document.createElement('div'); row.className = 'legend-cms-layer';
      const select = document.createElement('button'); select.type = 'button';
      select.textContent = label + (el.hidden ? ' · Hidden' : '');
      select.setAttribute('aria-pressed', String(el === selected));
      select.addEventListener('click', () => { setSelected(el); el.scrollIntoView?.({ block: 'nearest', behavior: 'smooth' }); });
      row.appendChild(select);
      if (el.hidden) {
        const restore = document.createElement('button'); restore.type = 'button'; restore.textContent = 'Show';
        restore.setAttribute('aria-label', `Show ${label}`);
        restore.addEventListener('click', () => {
          setSelected(el); checkpoint(); const value = selectedOverride();
          if (!value) return; value.hidden = false; el.hidden = false; markDirty(); syncEditorControls(); showPanel('layers');
        });
        row.appendChild(restore);
      }
      list.appendChild(row);
    });
    if (!list.children.length) { const empty = document.createElement('p'); empty.textContent = 'No matching content on this page.'; list.appendChild(empty); }
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
        checkpoint(); pageState()[key] = event.target.value; updateSearchPreview(); markDirty();
      });
    }
    document.getElementById('legend-cms-duplicate').addEventListener('click', () => {
      if (!selected || !selectedSection) return;
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
          placement: { sectionId: selectedSection.dataset.cmsSection, containerId: grid.dataset.cmsId, beforeId: null, flow: true, column: 1, span: 12 }
        };
        pageState().extras.push(copy);
        const created = createExtra(copy); applyPlacement(created, copy.placement); setSelected(created.querySelector('h3') || created); markDirty();
        return;
      }
      if (selected.dataset.cmsSection || !['P','H1','H2','H3','H4','H5','A','IMG','VIDEO'].includes(selected.tagName)) return;
      checkpoint();
      const original = selected.dataset.cmsExtraId ? pageState().extras.find(x => x.id === selected.dataset.cmsExtraId) : pageState().elements[selected.dataset.cmsId];
      const copy = JSON.parse(JSON.stringify(original || {}));
      copy.id = crypto.randomUUID(); copy.sectionId = selectedSection.dataset.cmsSection;
      copy.type = ({ A: 'button', IMG: 'image', VIDEO: 'video' }[selected.tagName] || 'text');
      copy.text = selected.textContent; copy.hidden = false; copy.style ||= {};
      if (copy.type === 'button') { copy.href = original?.href ?? rememberOriginal(selected).href; copy.target = selected.getAttribute('target'); }
      if (copy.type === 'image') { copy.imageDataUrl = original?.imageDataUrl ?? rememberOriginal(selected).src; copy.alt = selected.getAttribute('alt'); }
      if (copy.type === 'video') copy.videoUrl = original?.videoUrl ?? rememberOriginal(selected).src;
      pageState().extras.push(copy); setSelected(createExtra(copy)); markDirty();
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
    if (!select) return;
    const options = availableCtaOptions();
    select.replaceChildren();
    const customOption = document.createElement('option'); customOption.value = 'custom'; customOption.textContent = 'Custom destination…'; select.appendChild(customOption);
    options.forEach(option => {
      const node = document.createElement('option'); node.value = option.key; node.textContent = (option.group ? option.group + ' · ' : '') + option.label; select.appendChild(node);
    });
    const byKey = override?.actionKey ? options.find(option => option.key === override.actionKey) : null;
    const byHref = !byKey ? options.find(option => option.href === currentHref) : null;
    select.value = (byKey || byHref)?.key || 'custom';
    if (custom) custom.hidden = select.value !== 'custom';
  }

  function selectedFlowContainer(section) {
    let node = selected;
    while (node && node !== section) {
      if (['DIV','ARTICLE'].includes(node.tagName) && node.dataset?.cmsId && !node.closest(lockedSelector)) return node;
      node = node.parentElement;
    }
    return null;
  }

  function addBlock(type) {
    const section = selectedSection || document.querySelector('[data-cms-section]');
    if (!section && type !== 'section') return;
    const action = type === 'button' ? preferredCtaOption() : null;
    if (type === 'button' && !action) { alert('Configure a working website action before adding this button.'); return; }
    checkpoint();
    const extra = { id: crypto.randomUUID(), type, sectionId: section?.dataset.cmsSection || `${pageKey}.root`, text: type === 'button' ? action.defaultText || action.label : type === 'text' ? 'Your text' : '', style: {} };
    if (type === 'button') {
      extra.href = action.href;
      extra.target = action.openInNewTab ? '_blank' : '_self';
      if (action.managed) extra.actionKey = action.key;
      const container = selectedFlowContainer(section);
      if (container) extra.placement = { sectionId: section.dataset.cmsSection, containerId: container.dataset.cmsId, beforeId: null, flow: true, column: 1, span: 12 };
    }
    pageState().extras.push(extra); const el = createExtra(extra); if (extra.placement) applyPlacement(el, extra.placement); setSelected(el); markDirty();
  }
  function enhanceEditor(panel, preview) {
    document.querySelectorAll('[data-cms-id]').forEach(el => baselineNodes.set(el.dataset.cmsId, { el, parent: el.parentElement, next: el.nextSibling }));
    const content = document.createElement('div'); content.dataset.cmsView = 'content';
    Array.from(panel.children).filter(el => !el.classList.contains('legend-cms-bar')).forEach(el => content.appendChild(el));
    panel.appendChild(content);
    const navigation = document.createElement('nav');
    navigation.className = 'legend-cms-navigation'; navigation.setAttribute('aria-label', 'Website editing tools');
    navigation.innerHTML = `<div class="legend-cms-tabs"><button type="button" data-open="content">Content</button><button type="button" data-open="add">Add blocks</button><button type="button" data-open="appearance">Design</button><button type="button" data-open="layout">Position</button><button type="button" data-open="layers">Layers</button><button type="button" data-open="theme">Site theme</button><button type="button" data-open="page">Page & SEO</button></div>`;
    panel.insertBefore(navigation, content);
    const tools = document.createElement('div'); tools.innerHTML = `
      <section data-cms-view="add" hidden><h2>Add a block</h2><p>Added to your selected section.</p><div class="legend-cms-menu"><button data-add="text">Text</button><button data-add="button">Button / link</button><button id="legend-cms-new-image">Image</button><button data-add="video">Video</button><button data-add="section">Section</button></div></section>
      <section data-cms-view="appearance" hidden><h2>Appearance</h2>${appearanceFields()}<button id="legend-cms-container">Select section container</button></section>
      <section data-cms-view="layout" hidden><h2>Arrange</h2><p>Drag above or below another block to move it within that layout. Use the column controls to place content in a separate responsive grid.</p><button id="legend-cms-drag">Enable drag</button><label class="legend-cms-group">Destination section<select id="legend-cms-destination"></select></label><label class="legend-cms-group">Column<input id="legend-cms-column" type="number" min="1" max="12" value="1"></label><label class="legend-cms-group">Column span<input id="legend-cms-span" type="number" min="1" max="12" value="12"></label><button id="legend-cms-place">Place block</button><button id="legend-cms-undo">Undo</button><button id="legend-cms-redo">Redo</button></section>
      <section data-cms-view="layers" hidden><h2>Page layers</h2><p>Select, find, or restore content—even when it is hidden.</p><label class="legend-cms-group">Find content<input id="legend-cms-layer-search" type="search" placeholder="Search this page"></label><div id="legend-cms-layers" class="legend-cms-layer-list"></div></section>
      <section data-cms-view="page" hidden><h2>Page & search appearance</h2><p>Saved with this page's draft and applied on publication.</p><label class="legend-cms-group">Page title<input id="legend-cms-page-title" type="text" maxlength="200"></label><label class="legend-cms-group">Search description<textarea id="legend-cms-page-description" rows="4" maxlength="500"></textarea></label><div class="legend-cms-search-preview"><strong id="legend-cms-search-title"></strong><p id="legend-cms-search-description"></p></div></section>
      <section data-cms-view="theme" id="legend-cms-theme-view" hidden><h2>Site theme</h2><p>One palette, typography system, and browser icon for every page of this website.</p><div class="legend-cms-group legend-cms-favicon"><label for="legend-cms-favicon">Browser favicon</label><img id="legend-cms-favicon-preview" class="legend-cms-favicon-preview" alt=""><input id="legend-cms-favicon" type="file" accept="image/jpeg,image/png,image/webp"><small>PNG, JPEG, or WebP. This is scoped to this website and becomes public only when the website is published.</small><button id="legend-cms-favicon-remove" type="button">Use LEGEND fallback favicon</button></div></section>`;
    panel.appendChild(tools);
    const signals = document.createElement('section'); signals.dataset.cmsView = 'signals'; signals.hidden = true;
    signals.innerHTML = '<h2>Analytics & Meta</h2><p>Choose what this interaction means. Draft changes take effect when published.</p><div id="legend-cms-signal-controls"></div>';
    tools.appendChild(signals);


    const layoutView = tools.querySelector('[data-cms-view="layout"]');
    ['legend-cms-up','legend-cms-down','legend-cms-reset'].forEach(id => { const button = document.getElementById(id); if (button && layoutView) layoutView.appendChild(button); });
    const selectedDelete = document.getElementById('legend-cms-remove');
    if (selectedDelete && content) {
      selectedDelete.disabled = true;
      selectedDelete.setAttribute('aria-label', 'Delete selected website element');
      content.appendChild(selectedDelete);
    }
    ['legend-cms-undo', 'legend-cms-redo'].forEach(id => panel.querySelector('.legend-cms-bar').appendChild(document.getElementById(id)));
    const duplicate = document.createElement('button'); duplicate.id = 'legend-cms-duplicate'; duplicate.type = 'button'; duplicate.textContent = 'Duplicate block'; layoutView.appendChild(duplicate);
    const theme = content.querySelector('.legend-cms-theme'); if (theme) document.getElementById('legend-cms-theme-view').appendChild(theme.parentElement);
    const links = document.createElement('div'); links.innerHTML = `<div id="legend-cms-link-group" class="legend-cms-group" hidden><label for="legend-cms-action">Button action</label><select id="legend-cms-action"></select><small>Select a working action already connected to this website. You can edit the button wording in Content at any time.</small><div id="legend-cms-custom-link"><label for="legend-cms-href">Custom destination</label><input id="legend-cms-href" type="url" placeholder="https://…"></div><label><input id="legend-cms-target" type="checkbox"> Open in a new tab</label></div><div id="legend-cms-video-group" class="legend-cms-group" hidden><label for="legend-cms-videoUrl">HTTPS video URL</label><input id="legend-cms-videoUrl" type="url"><label for="legend-cms-video-file">Upload video</label><input id="legend-cms-video-file" type="file" accept="video/mp4,video/webm"></div><label class="legend-cms-group">Image description<input id="legend-cms-alt" type="text"></label>`;
    content.appendChild(links);
    panel.querySelectorAll('[data-open]').forEach(button => button.addEventListener('click', () => { showPanel(button.dataset.open); if (button.dataset.open === 'layout') { const select = document.getElementById('legend-cms-destination'); select.replaceChildren(); document.querySelectorAll('[data-cms-section]').forEach(section => { const option = document.createElement('option'); option.value = section.dataset.cmsSection; option.textContent = section.querySelector('h1,h2,h3')?.textContent || section.dataset.cmsSection; select.appendChild(option); }); } }));
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
    ['href','videoUrl','alt'].forEach(key => document.getElementById(`legend-cms-${key}`).addEventListener('input', event => { if (!selected) return; const value = event.target.value; if (key !== 'alt' && !safeUrl(value, key === 'videoUrl')) { event.target.setCustomValidity('Enter a supported URL.'); return; } event.target.setCustomValidity(''); checkpoint(); const ov = selectedOverride(); ov[key] = value; if (key === 'href') { delete ov.actionKey; const action = document.getElementById('legend-cms-action'); if (action) action.value = 'custom'; const custom = document.getElementById('legend-cms-custom-link'); if (custom) custom.hidden = false; } applyElementOverride(selected, ov); markDirty(); }));
    document.getElementById('legend-cms-video-file').addEventListener('change', async event => { const video = selected; if (video?.tagName !== 'VIDEO') return; const url = await uploadMedia(event.target.files?.[0]); if (!url || selected !== video) return; checkpoint(); const ov = selectedOverride(); ov.videoUrl = url; applyElementOverride(video, ov); syncEditorControls(); markDirty(); });
    document.getElementById('legend-cms-target').addEventListener('input', event => { if (!selected) return; checkpoint(); const ov = selectedOverride(); ov.target = event.target.checked ? '_blank' : '_self'; ov.href ||= rememberOriginal(selected).href; applyElementOverride(selected, ov); markDirty(); });
    document.getElementById('legend-cms-undo').addEventListener('click', () => restoreHistory(undoStack, redoStack));
    document.getElementById('legend-cms-redo').addEventListener('click', () => restoreHistory(redoStack, undoStack));
    function place(sectionId, column, span, beforeId, containerId, flow = false) { if (!selected || selected.dataset.cmsSection) return; checkpoint(); const ov = selectedOverride(); column = Math.max(1, Math.min(12, Math.round(column) || 1)); span = Math.max(1, Math.min(13-column, Math.round(span) || 12)); ov.placement = { sectionId, column, span, beforeId, containerId, flow };  applyPlacement(selected, ov.placement); selectedSection = currentSectionFor(selected); markDirty(); }
    document.getElementById('legend-cms-place').addEventListener('click', () => place(document.getElementById('legend-cms-destination').value, Number(document.getElementById('legend-cms-column').value), Number(document.getElementById('legend-cms-span').value)));
    document.getElementById('legend-cms-drag').addEventListener('click', () => { if (selected && !selected.dataset.cmsSection) { selected.draggable = true; document.getElementById('legend-cms-status').textContent = 'Drag the selected block onto a section'; } });
    preview.addEventListener('dragstart', event => { const el = event.target.closest('[data-cms-editable="true"]'); if (!el || el.dataset.cmsSection || el.dataset.cmsSignalOnly || el.closest(lockedSelector)) { event.preventDefault(); return; } setSelected(el); event.dataTransfer.setData('text/plain', el.dataset.cmsId); preview.classList.add('legend-cms-dragging'); });
    preview.addEventListener('dragover', event => { const section = event.target.closest('[data-cms-section]'); if (selected && section && !selected.contains(section)) { event.preventDefault(); document.querySelectorAll('.legend-cms-drop').forEach(x => x.classList.remove('legend-cms-drop')); section.classList.add('legend-cms-drop'); } });
    preview.addEventListener('drop', event => {
      event.preventDefault();
      const section = event.target.closest('[data-cms-section]');
      const sourceId = event.dataTransfer?.getData('text/plain');
      if (!section || !selected || sourceId !== selected.dataset.cmsId || selected.contains(section) || selected.dataset.cmsSignalOnly) { clearDrag(); return; }
      let target = event.target.closest('[data-cms-id]');
      if (target === selected || selected.contains(target)) { clearDrag(); return; }
      let container = target?.parentElement;
      while (container && container !== section && !['DIV','ARTICLE','SECTION','HEADER','FOOTER'].includes(container.tagName)) { target = container; container = container.parentElement; }
      if (target === section || !container || !section.contains(container)) { container = section; target = null; }
      if (!container.dataset.cmsId || container.closest(lockedSelector)) { clearDrag(); return; }
      // The lower half means after the target. Persist the next editable sibling,
      // not a transient pointer coordinate, so reload and publication agree.
      let before = target;
      if (target && event.clientY >= target.getBoundingClientRect().top + target.getBoundingClientRect().height / 2) {
        before = target.nextElementSibling;
        while (before && (!before.dataset.cmsId || before === selected)) before = before.nextElementSibling;
      }
      place(section.dataset.cmsSection, 1, 12, before?.dataset.cmsId, container.dataset.cmsId, true);
      clearDrag();
    });
    function clearDrag() { preview.classList.remove('legend-cms-dragging'); document.querySelectorAll('.legend-cms-drop').forEach(x => x.classList.remove('legend-cms-drop')); }
    preview.addEventListener('dragend', clearDrag);
    showPanel('content');
  }

  function injectContentStyles() { const style = document.createElement('style'); style.textContent = `      [data-cms-id][hidden]{display:none}.cms-layout-frame{display:grid;grid-template-columns:repeat(12,minmax(0,1fr));gap:clamp(8px,2vw,24px);width:100%;min-width:0}.cms-layout-frame>*{grid-column:var(--cms-column,1) / span var(--cms-span,12);max-width:100%;min-width:0;overflow-wrap:anywhere}.cms-extra-section{padding:clamp(24px,5vw,64px);min-height:120px}.cms-extra video,video.cms-extra{max-width:100%;height:auto}@media(max-width:600px){.cms-layout-frame>*{grid-column:1 / -1}}
`; document.head.appendChild(style); }

  function injectEditorStyles() {
    const style = document.createElement('style');
    style.textContent = `
      .legend-cms-selected{outline:3px solid #f0cf78;outline-offset:4px}
      [data-cms-editable="true"]{cursor:pointer}
      body.legend-cms-editing{display:grid;grid-template-columns:minmax(0,1fr) minmax(20rem,24rem);height:100dvh;min-height:0;margin:0;overflow:hidden}
      body.legend-cms-editing.legend-cms-panel-hidden{grid-template-columns:minmax(0,1fr)}
      .legend-cms-preview{min-width:0;min-height:0;height:100%;overflow:auto;position:relative;transform:translateZ(0)}
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
      .legend-cms-panel-toggle{position:fixed;z-index:2147483000;top:max(10px,env(safe-area-inset-top));right:10px;min-height:40px;padding:8px 12px;border:1px solid #d4ad45;border-radius:999px;background:#081a3af2;color:#fff;font:700 14px/1.2 Inter,system-ui,sans-serif;cursor:pointer;box-shadow:0 8px 24px #0005}
      .legend-cms-panel h2{margin:0 0 4px;font-size:19px}.legend-cms-panel small{display:block;color:#b8c6dc;margin-bottom:14px;overflow-wrap:anywhere}
      .legend-cms-group{display:grid;gap:7px;margin:12px 0}.legend-cms-group label{font-size:12px;font-weight:800;color:#e2d5b8}
      .legend-cms-row{display:grid;grid-template-columns:1fr 1fr;gap:8px}
      .legend-cms-theme{display:grid;grid-template-columns:repeat(2,1fr);gap:8px}
      .legend-cms-theme label{font-size:11px;font-weight:800}.legend-cms-theme input{width:100%;height:36px;border:0;background:transparent}
      .legend-cms-favicon-preview{display:block;width:64px;height:64px;object-fit:contain;border-radius:12px;background:#fff;padding:6px;border:1px solid #50617e}.legend-cms-favicon button{width:100%;padding:10px 12px;border:1px solid #50617e;border-radius:10px;background:#142c50;color:#fff;text-align:center}
      .legend-cms-panel button{cursor:pointer}.legend-cms-menu{display:grid;gap:10px}.legend-cms-menu button,.legend-cms-panel section>button{padding:13px;border:1px solid #50617e;border-radius:12px;background:#142c50;color:#fff;text-align:left}.legend-cms-panel input,.legend-cms-panel textarea,.legend-cms-panel select{width:100%;min-width:0;max-width:100%;color:#f7f6f2;background:#142c50;border:1px solid #50617e;border-radius:8px;padding:8px}.legend-cms-panel :focus-visible{outline:2px solid #f0cf78;outline-offset:3px}.legend-cms-drop{outline:2px dashed #d4ad45;background-image:repeating-linear-gradient(90deg,transparent 0,transparent calc(8.333% - 1px),#d4ad4560 calc(8.333% - 1px),#d4ad4560 8.333%)}
      .legend-cms-panel input[type=checkbox]{width:auto}.legend-cms-panel input[type=color]{min-height:40px;padding:4px}.legend-cms-panel button:disabled{opacity:.45;cursor:default}
      .legend-cms-navigation{margin:0 0 20px}.legend-cms-tabs{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:6px}.legend-cms-tabs button{min-height:40px;padding:8px 4px;border:1px solid #344766;border-radius:8px;background:transparent;color:#c9d5e7;font:600 12px/1.3 Inter,system-ui,sans-serif}.legend-cms-tabs button[aria-pressed=true]{background:#e6c77e;color:#10213e;border-color:#e6c77e}
      #legend-cms-status{flex-basis:100%;font-size:12px;color:#c9d5e7;order:1}.legend-cms-panel p{font-size:13px;line-height:1.6;color:#b8c6dc}.legend-cms-layer-list{display:grid;gap:6px}.legend-cms-layer{display:flex;gap:4px;min-width:0}.legend-cms-layer button{min-width:0;padding:10px;border:1px solid #344766;background:#142c50;border-radius:8px;color:#f7f6f2;text-align:left;font-size:12px;overflow-wrap:anywhere}.legend-cms-layer button:first-child{flex:1}.legend-cms-layer button[aria-pressed=true]{border-color:#e6c77e}.legend-cms-search-preview{padding:16px;border:1px solid #344766;border-radius:12px;overflow-wrap:anywhere}.legend-cms-search-preview strong{color:#e6c77e}
      .cms-extra-image{display:block;margin-left:auto;margin-right:auto;height:auto}
      @media(max-width:800px){body.legend-cms-editing{grid-template-columns:minmax(0,1fr);grid-template-rows:minmax(0,55fr) minmax(0,45fr)}body.legend-cms-editing.legend-cms-panel-hidden{grid-template-rows:minmax(0,1fr)}.legend-cms-panel{border-top:2px solid #d4ad45}.legend-cms-panel-toggle{top:max(8px,env(safe-area-inset-top));right:8px}}
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
      <div id="legend-cms-text-group" class="legend-cms-group" hidden>
        <label for="legend-cms-text">Content</label>
        <textarea id="legend-cms-text" rows="5"></textarea>
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
        <div class="legend-cms-group"><label for="legend-cms-padding-top">Top spacing</label><input id="legend-cms-padding-top" type="number" min="0" step="any" value="0"></div>
        <div class="legend-cms-group"><label for="legend-cms-padding-bottom">Bottom spacing</label><input id="legend-cms-padding-bottom" type="number" min="0" step="any" value="0"></div>
      </div>
      <div class="legend-cms-group"><label for="legend-cms-align">Alignment</label><select id="legend-cms-align"><option value="">Default</option><option value="left">Left</option><option value="center">Center</option><option value="right">Right</option><option value="start">Start</option><option value="end">End</option><option value="justify">Justify</option></select></div>
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
    panelToggle.textContent = 'Preview full page';
    panelToggle.setAttribute('aria-controls', 'legend-cms-heading');
    panelToggle.setAttribute('aria-expanded', 'true');
    panelToggle.addEventListener('click', () => {
      const hidden = document.body.classList.toggle('legend-cms-panel-hidden');
      panelToggle.textContent = hidden ? 'Open editor' : 'Preview full page';
      panelToggle.setAttribute('aria-expanded', hidden ? 'false' : 'true');
      if (hidden) setSelected(null);
      if (typeof requestAnimationFrame === 'function') requestAnimationFrame(refreshScaledElements);
      else refreshScaledElements();
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
    if (typeof ResizeObserver !== 'undefined') new ResizeObserver(refreshScaledElements).observe(preview);
    syncEditorControls();

    document.addEventListener('click', event => {
      const target = (event.target.tagName === 'IMG' ? event.target.closest('[data-cms-editable="true"]') : event.target.closest('a[data-cms-editable="true"]')) || event.target.closest('[data-cms-editable="true"]');
      if (!target || target.closest('.legend-cms-editor')) return;
      event.preventDefault();
      event.stopPropagation();
      setSelected(target);
    }, true);

    ['legend-cms-text','legend-cms-scale','legend-cms-width','legend-cms-padding-top','legend-cms-padding-bottom','legend-cms-align','legend-cms-hidden']
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
      installPageSelector(payload);
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
      window.addEventListener('resize', refreshScaledElements);
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
    window.addEventListener('resize', refreshScaledElements);
    if (document.fonts?.ready) document.fonts.ready.then(refreshScaledElements);
  }, { once: true });
})();
