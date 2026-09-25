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
  const defaultBreakpoints = [
    { id: 'tablet', label: 'Tablet', maxWidthPx: 1024 },
    { id: 'mobile', label: 'Mobile', maxWidthPx: 640 }
  ];
  let documentState = { version: 1, faviconImageDataUrl: null, elements: {}, sectionOrder: {}, extras: [], theme: {}, breakpoints: defaultBreakpoints.map(x => ({...x})) };
  let signalCatalog = null;
  let ctaCatalog = [];
  let componentCatalog = [];
  let motionCatalog = null;
  let mediaLibraryAssets = [];
  let savedQualityReport = null;
  let currentDesignBreakpoint = 'base';
  let selected = null;
  const selectedElements = new Set();
  let selectedSection = null;
  let editorPreview = null;
  let selectionFrame = null;
  let gridOverlay = null;
  let marqueeOverlay = null;
  let marqueeGesture = null;
  let directGesture = null;
  let inlineEditNode = null;
  let inlineEditCheckpointed = false;
  let dirty = false;
  let autoSaveTimer = null;
  const originals = new WeakMap();
  const scaledElements = new Map();
  const motionBindings = new WeakMap();
  const styleProperties = ['textAlign', 'fontSize', 'width', 'maxWidth', 'minWidth', 'height', 'minHeight', 'maxHeight', 'position', 'left', 'right', 'top', 'bottom', 'overflow', 'paddingTop', 'paddingBottom', 'objectPosition', 'color', 'backgroundColor', 'backgroundImage', 'fontFamily', 'fontWeight', 'lineHeight', 'letterSpacing', 'paddingLeft', 'paddingRight', 'borderRadius', 'objectFit', 'gridColumn', 'overflowWrap', 'marginTop', 'marginRight', 'marginBottom', 'marginLeft', 'opacity', 'transform', 'zIndex', 'aspectRatio', 'display', 'gridTemplateColumns', 'gridTemplateRows', 'columnGap', 'rowGap', 'flexDirection', 'alignItems', 'justifyContent', 'flexWrap'];

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

  const editableTextTags = new Set(['H1','H2','H3','H4','H5','P','LI','BUTTON','LABEL','SMALL','STRONG','SPAN','BLOCKQUOTE']);
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
      reusableComponents: input?.reusableComponents && typeof input.reusableComponents === 'object' ? input.reusableComponents : {},
      breakpoints: Array.isArray(input?.breakpoints) && input.breakpoints.length
        ? input.breakpoints
            .filter(item => item && typeof item.id === 'string' && Number.isFinite(Number(item.maxWidthPx)))
            .map(item => ({ id: safeId(item.id), label: String(item.label || item.id).slice(0, 80), maxWidthPx: Number(item.maxWidthPx) }))
            .filter(item => item.id)
            .sort((a,b) => b.maxWidthPx - a.maxWidthPx)
        : defaultBreakpoints.map(item => ({...item})),
      pages
    };
  }

  function breakpointById(id) {
    return (documentState.breakpoints || []).find(item => item.id === id) || null;
  }

  function responsiveChain() {
    const breakpoints = [...(documentState.breakpoints || [])].sort((a,b) => b.maxWidthPx - a.maxWidthPx);
    const forced = currentDesignBreakpoint !== 'base' ? breakpointById(currentDesignBreakpoint) : null;
    const width = forced?.maxWidthPx ?? (Number.isFinite(window.innerWidth) ? window.innerWidth : Number.MAX_SAFE_INTEGER);
    return breakpoints.filter(item => width <= item.maxWidthPx);
  }

  function resolvedVariant(override) {
    if (!override) return { style: {}, layout: {}, hidden: undefined };
    const resolved = {
      ...override,
      style: { ...(override.style || {}) },
      layout: { ...(override.layout || {}) }
    };
    for (const breakpoint of responsiveChain()) {
      const variant = override.responsive?.[breakpoint.id];
      if (!variant) continue;
      if (variant.hidden !== undefined && variant.hidden !== null) resolved.hidden = variant.hidden;
      Object.assign(resolved.style, variant.style || {});
      Object.assign(resolved.layout, variant.layout || {});
    }
    return resolved;
  }

  function editableVariant(override, create = true) {
    if (!override) return null;
    if (currentDesignBreakpoint === 'base') return override;
    override.responsive ||= {};
    if (!override.responsive[currentDesignBreakpoint] && create) {
      override.responsive[currentDesignBreakpoint] = { style: {}, layout: {} };
    }
    const variant = override.responsive[currentDesignBreakpoint] || null;
    if (variant) {
      variant.style ||= {};
      variant.layout ||= {};
    }
    return variant;
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
    if (selectionItems().length > 1) { paragraph('Analytics & Meta mappings are edited one element at a time. Select one item from the canvas or Layers.'); return; }
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

  function renderMotionControls() {
    const host = document.getElementById('legend-cms-motion-controls');
    if (!host) return;
    host.replaceChildren();
    const note = text => { const p=document.createElement('p'); p.textContent=text; host.appendChild(p); };
    if (!selected) { note('Select one element or component to configure motion.'); return; }
    if (selectionItems().length > 1) { note('Motion is edited one element at a time.'); return; }
    if (!motionCatalog) { note('The motion catalog could not be loaded. Reopen the editor to try again.'); return; }

    const override = selectedOverride();
    if (!override) return;
    override.interactions ||= [];
    const interactions = override.interactions;
    note(interactions.length ? `${interactions.length} motion interaction${interactions.length===1?'':'s'}` : 'No motion is configured for this element.');

    const optionSelect = (labelText, options, value, onChange) => {
      const label=document.createElement('label');label.className='legend-cms-group';label.textContent=labelText;
      const select=document.createElement('select');
      for(const item of options||[]){const option=document.createElement('option');option.value=item.key;option.textContent=item.label||item.key;select.appendChild(option);}
      select.value=value||select.options[0]?.value||'';
      select.addEventListener('change',()=>{checkpoint();onChange(select.value);markDirty();renderMotionControls();});
      label.appendChild(select);return label;
    };
    const numberInput = (labelText, value, min, max, step, onInput) => {
      const label=document.createElement('label');label.className='legend-cms-group';label.textContent=labelText;
      const input=document.createElement('input');input.type='number';input.min=String(min);input.max=String(max);input.step=String(step);input.value=value??'';
      input.addEventListener('change',()=>{const numeric=Number(input.value);if(!Number.isFinite(numeric))return;checkpoint();onInput(numeric);markDirty();});
      label.appendChild(input);return label;
    };

    for(const interaction of interactions){
      const card=document.createElement('div');card.className='legend-cms-motion-card';
      card.append(
        optionSelect('Trigger',motionCatalog.triggers,interaction.trigger,value=>interaction.trigger=value),
        optionSelect('Effect',motionCatalog.effects,interaction.effect,value=>interaction.effect=value),
        optionSelect('Easing',motionCatalog.easings,interaction.easing,value=>interaction.easing=value)
      );
      if(interaction.effect==='slide') card.append(optionSelect('Direction',motionCatalog.directions,interaction.direction||'up',value=>interaction.direction=value));
      card.append(
        numberInput('Duration ms',interaction.durationMs??500,50,10000,10,value=>interaction.durationMs=value),
        numberInput('Delay ms',interaction.delayMs??0,0,10000,10,value=>interaction.delayMs=value)
      );
      if(interaction.effect==='slide') card.append(numberInput('Distance px',interaction.distancePx??32,0,2000,1,value=>interaction.distancePx=value));
      if(['scale','rotate','blur'].includes(interaction.effect)) card.append(numberInput(
        interaction.effect==='scale'?'Start scale':interaction.effect==='rotate'?'Rotation °':'Blur px',
        interaction.amount??(interaction.effect==='scale'?.92:interaction.effect==='rotate'?-8:12),
        interaction.effect==='scale'?.01:-2000,
        interaction.effect==='scale'?5:2000,
        .01,
        value=>interaction.amount=value
      ));
      const onceLabel=document.createElement('label');const once=document.createElement('input');once.type='checkbox';once.checked=interaction.once!==false;
      once.addEventListener('change',()=>{checkpoint();interaction.once=once.checked;markDirty();});onceLabel.append(once,document.createTextNode(' Play once when applicable'));card.appendChild(onceLabel);
      const actions=document.createElement('div');actions.className='legend-cms-row';
      const preview=document.createElement('button');preview.type='button';preview.textContent='Preview';preview.addEventListener('click',()=>playMotion(selected,interaction));
      const remove=document.createElement('button');remove.type='button';remove.textContent='Remove';remove.addEventListener('click',()=>{checkpoint();override.interactions=interactions.filter(item=>item.id!==interaction.id);markDirty();renderMotionControls();});
      actions.append(preview,remove);card.appendChild(actions);host.appendChild(card);
    }

    const add=document.createElement('button');add.type='button';add.textContent='Add motion';
    add.disabled=interactions.length>=Number(motionCatalog.maxInteractionsPerElement||8);
    add.addEventListener('click',()=>{
      checkpoint();
      override.interactions.push({
        id:crypto.randomUUID().replaceAll('-',''),
        trigger:motionCatalog.triggers?.[0]?.key||'enter-view',
        effect:motionCatalog.effects?.[0]?.key||'fade',
        durationMs:500,delayMs:0,easing:'ease-out',once:true,direction:'up',distancePx:32
      });
      markDirty();renderMotionControls();
    });
    host.appendChild(add);
    if(prefersReducedMotion()) note('Reduced Motion is enabled on this device. Motion remains configured but preview/playback is suppressed.');
  }

  function applyTheme(theme) {
    const root = document.documentElement;
    const stringMap = {
      navy: '--web-navy',
      navyDeep: '--web-navy-deep',
      gold: '--web-gold',
      goldStrong: '--web-gold-strong',
      surface: '--web-surface',
      text: '--web-ink',
      muted: '--web-muted',
      fontFamily: '--web-font',
      headingFontFamily: '--web-heading-font'
    };
    const pxMap = {
      fontSize: '--web-body-size',
      h1SizePx: '--web-h1-size',
      h2SizePx: '--web-h2-size',
      h3SizePx: '--web-h3-size',
      pagePaddingPx: '--web-page-pad',
      sectionPaddingPx: '--web-section-pad-y',
      contentGapPx: '--web-content-gap',
      buttonRadiusPx: '--web-button-radius'
    };
    if (theme?.navy) root.style.setProperty('--web-navy-royal', theme.navy);
    else root.style.removeProperty('--web-navy-royal');

    Object.entries(stringMap).forEach(([key, cssVar]) => {
      if (theme?.[key]) root.style.setProperty(cssVar, theme[key]);
      else root.style.removeProperty(cssVar);
    });
    Object.entries(pxMap).forEach(([key, cssVar]) => {
      const value = Number(theme?.[key]);
      if (Number.isFinite(value) && value >= 0) root.style.setProperty(cssVar, `${value}px`);
      else root.style.removeProperty(cssVar);
    });

    const lineHeight = Number(theme?.lineHeight);
    if (Number.isFinite(lineHeight) && lineHeight > 0) root.style.setProperty('--web-body-line-height', String(lineHeight));
    else root.style.removeProperty('--web-body-line-height');

    const headingWeight = Number(theme?.headingWeight);
    if (Number.isInteger(headingWeight) && headingWeight >= 100 && headingWeight <= 900) root.style.setProperty('--web-heading-weight', String(headingWeight));
    else root.style.removeProperty('--web-heading-weight');

    const radius = Number(theme?.borderRadius);
    if (Number.isFinite(radius) && radius >= 0) {
      root.style.setProperty('--web-radius-lg', `${radius}px`);
      root.style.setProperty('--web-radius-md', `${Math.round(radius * .64 * 100) / 100}px`);
      root.style.setProperty('--web-radius-sm', `${Math.round(radius * .43 * 100) / 100}px`);
    } else {
      root.style.removeProperty('--web-radius-lg');
      root.style.removeProperty('--web-radius-md');
      root.style.removeProperty('--web-radius-sm');
    }

    const shadows = {
      none: 'none',
      subtle: '0 12px 32px rgba(8,26,58,.08)',
      medium: '0 24px 70px rgba(8,26,58,.12)',
      strong: '0 32px 90px rgba(8,26,58,.24)'
    };
    if (theme?.shadowPreset && shadows[theme.shadowPreset]) root.style.setProperty('--web-shadow', shadows[theme.shadowPreset]);
    else root.style.removeProperty('--web-shadow');
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
    if (positiveNumber(style.widthPercent)) el.style.width = `${style.widthPercent}%`;
    if (positiveNumber(style.heightPx)) {
      el.style.height = `${style.heightPx}px`;
      el.style.overflow = el.classList.contains('cms-extra-code') ? 'hidden' : 'auto';
    }
    if (positiveNumber(style.minWidthPx)) el.style.minWidth = `${style.minWidthPx}px`;
    if (positiveNumber(style.maxWidthPx)) el.style.maxWidth = `${style.maxWidthPx}px`;
    else if (positiveNumber(style.widthPercent) && style.widthPercent <= 100) el.style.maxWidth = '100%';
    if (positiveNumber(style.minHeightPx)) el.style.minHeight = `${style.minHeightPx}px`;
    if (positiveNumber(style.maxHeightPx)) el.style.maxHeight = `${style.maxHeightPx}px`;
    if (positiveNumber(style.aspectRatio)) el.style.aspectRatio = String(style.aspectRatio);

    const explicitPosition = ['relative','absolute','sticky','fixed'].includes(style.positionMode) ? style.positionMode : null;
    const hasOffsetX = style.offsetXPercent != null && Number.isFinite(Number(style.offsetXPercent));
    const hasOffsetY = style.offsetYPx != null && Number.isFinite(Number(style.offsetYPx));
    const horizontalAnchor = ['left','center','right','stretch'].includes(style.horizontalAnchor) ? style.horizontalAnchor : null;
    const verticalAnchor = ['top','center','bottom','stretch'].includes(style.verticalAnchor) ? style.verticalAnchor : null;
    const hasAnchors = !!horizontalAnchor || !!verticalAnchor;
    if (explicitPosition) el.style.position = explicitPosition;
    else if (hasOffsetX || hasOffsetY || hasAnchors) el.style.position = 'relative';

    const transforms = [];
    if (horizontalAnchor === 'left') {
      el.style.left = `${Number(style.insetLeftPx) || 0}px`;
      el.style.right = '';
    } else if (horizontalAnchor === 'right') {
      el.style.right = `${Number(style.insetRightPx) || 0}px`;
      el.style.left = '';
    } else if (horizontalAnchor === 'center') {
      el.style.left = '50%';
      el.style.right = '';
      transforms.push('translateX(-50%)');
    } else if (horizontalAnchor === 'stretch') {
      el.style.left = `${Number(style.insetLeftPx) || 0}px`;
      el.style.right = `${Number(style.insetRightPx) || 0}px`;
      el.style.width = 'auto';
    } else if (hasOffsetX) {
      el.style.left = `${Number(style.offsetXPercent)}%`;
    }

    if (verticalAnchor === 'top') {
      el.style.top = `${Number(style.insetTopPx) || 0}px`;
      el.style.bottom = '';
    } else if (verticalAnchor === 'bottom') {
      el.style.bottom = `${Number(style.insetBottomPx) || 0}px`;
      el.style.top = '';
    } else if (verticalAnchor === 'center') {
      el.style.top = '50%';
      el.style.bottom = '';
      transforms.push('translateY(-50%)');
    } else if (verticalAnchor === 'stretch') {
      el.style.top = `${Number(style.insetTopPx) || 0}px`;
      el.style.bottom = `${Number(style.insetBottomPx) || 0}px`;
      el.style.height = 'auto';
    } else if (hasOffsetY) {
      el.style.top = `${Number(style.offsetYPx)}px`;
    }

    if (spacingNumber(style.paddingTop)) el.style.paddingTop = `${style.paddingTop}px`;
    if (spacingNumber(style.paddingBottom)) el.style.paddingBottom = `${style.paddingBottom}px`;
    ['color','backgroundColor','fontFamily','fontWeight','objectFit'].forEach(key => { if (style[key]) el.style[key] = style[key]; });
    if (style.backgroundColor) el.style.backgroundImage = 'none';
    ['fontSize','letterSpacing','paddingLeft','paddingRight','borderRadius'].forEach(key => { if (spacingNumber(style[key])) el.style[key] = `${style[key]}px`; });
    for (const key of ['marginTop','marginRight','marginBottom','marginLeft']) {
      if (style[key] != null && Number.isFinite(Number(style[key]))) el.style[key] = `${Number(style[key])}px`;
    }
    if (positiveNumber(style.lineHeight)) el.style.lineHeight = String(style.lineHeight);
    if (style.objectPosition && el instanceof HTMLImageElement) el.style.objectPosition = style.objectPosition;
    if (style.opacity != null && Number.isFinite(Number(style.opacity))) el.style.opacity = String(style.opacity);
    if (Number.isInteger(Number(style.zIndex))) el.style.zIndex = String(style.zIndex);
    if (style.rotationDeg != null && Number.isFinite(Number(style.rotationDeg))) transforms.push(`rotate(${Number(style.rotationDeg)}deg)`);
    if (positiveNumber(style.scaleX) || positiveNumber(style.scaleY)) transforms.push(`scale(${positiveNumber(style.scaleX) ? Number(style.scaleX) : 1}, ${positiveNumber(style.scaleY) ? Number(style.scaleY) : 1})`);
    if (transforms.length) el.style.transform = transforms.join(' ');
  }

  function applyLayout(el, layout) {
    if (!el) return;
    delete el.dataset.cmsLayoutMode;
    if (!layout || !layout.mode || layout.mode === 'flow') return;
    const mode = layout.mode;
    el.dataset.cmsLayoutMode = mode;
    if (mode === 'grid') {
      el.style.display = 'grid';
      el.style.gridTemplateColumns = `repeat(${Math.max(1, Number(layout.columns) || 12)}, minmax(0, 1fr))`;
      if (positiveNumber(layout.rows)) el.style.gridTemplateRows = `repeat(${Number(layout.rows)}, minmax(0, auto))`;
    } else if (mode === 'flex' || mode === 'stack') {
      el.style.display = 'flex';
      el.style.flexDirection = mode === 'stack' ? 'column' : (layout.direction === 'column' ? 'column' : 'row');
      el.style.flexWrap = layout.wrap === false ? 'nowrap' : 'wrap';
    } else if (mode === 'free') {
      el.style.position ||= 'relative';
    }
    if (layout.columnGap != null && Number.isFinite(Number(layout.columnGap))) el.style.columnGap = `${Number(layout.columnGap)}px`;
    if (layout.rowGap != null && Number.isFinite(Number(layout.rowGap))) el.style.rowGap = `${Number(layout.rowGap)}px`;
    if (['start','center','end','stretch','baseline'].includes(layout.alignItems)) el.style.alignItems = layout.alignItems;
    if (['start','center','end','space-between','space-around','space-evenly'].includes(layout.justifyContent)) el.style.justifyContent = layout.justifyContent;
    if (['visible','hidden','clip','auto','scroll'].includes(layout.overflow)) el.style.overflow = layout.overflow;
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


  function reusableDefinition(id) {
    return id ? documentState.reusableComponents?.[id] || null : null;
  }

  function reusableMember(definitionId, localId) {
    const definition = reusableDefinition(definitionId);
    if (!definition) return null;
    if (localId === '__root__') return definition;
    return (definition.components || []).find(component => component.id === localId) || null;
  }

  function overrideForElement(el, create = true) {
    if (!el?.dataset?.cmsId) return null;
    if (el.dataset.cmsReusableDefinitionId) {
      return reusableMember(el.dataset.cmsReusableDefinitionId, el.dataset.cmsReusableLocalId) || null;
    }
    if (el.dataset.cmsExtraId) return pageState().extras.find(x => x.id === el.dataset.cmsExtraId) || null;
    return create ? ensureOverride(el.dataset.cmsId) : pageState().elements[el.dataset.cmsId] || null;
  }

  function applyReusableMemberElement(el, override) {
    if (!el || !override) return;
    const field = el.dataset.cmsExtraField;
    if (field === 'title') { setContentText(el, override.title || '', true); return; }
    if (field === 'text') { setContentText(el, override.text || '', true); return; }
    applyElementOverride(el, override);
  }

  function syncReusableMemberDom(el, override) {
    if (!editorMode || !el?.dataset?.cmsReusableDefinitionId || el.dataset.cmsReusableSyncing === 'true') return;
    const definitionId = el.dataset.cmsReusableDefinitionId;
    const localId = el.dataset.cmsReusableLocalId;
    const field = el.dataset.cmsExtraField || '';
    document.querySelectorAll('[data-cms-reusable-definition-id]').forEach(other => {
      if (other === el ||
          other.dataset.cmsReusableDefinitionId !== definitionId ||
          other.dataset.cmsReusableLocalId !== localId ||
          (other.dataset.cmsExtraField || '') !== field)
        return;
      other.dataset.cmsReusableSyncing = 'true';
      try {
        applyReusableMemberElement(other, override);
      } finally {
        delete other.dataset.cmsReusableSyncing;
      }
    });
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
    if (!el) return;
    const override = overrideForElement(el, false);
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
      const override = overrideForElement(el);
      if (!override) return;
      const value = inlineTextValue(el);
      if (el.dataset.cmsExtraField === 'title') override.title = value;
      else override.text = value;
      el.dataset.cmsPreserveWhitespace = 'true';
      syncReusableMemberDom(el, override);
      markDirty();
      updateDirectCanvasUi();
    });
    el.addEventListener('blur', () => {
      if (inlineEditNode !== el) return;
      const override = overrideForElement(el, false);
      if (override) {
        const value = inlineTextValue(el);
        if (el.dataset.cmsExtraField === 'title') override.title = value;
        else override.text = value;
        setContentText(el, value, true);
        syncReusableMemberDom(el, override);
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

  function prefersReducedMotion() {
    try { return window.matchMedia?.('(prefers-reduced-motion: reduce)')?.matches === true; }
    catch { return false; }
  }

  function motionKeyframes(interaction) {
    const distance = positiveNumber(Number(interaction?.distancePx)) ? Number(interaction.distancePx) : 32;
    const amount = Number(interaction?.amount);
    if (interaction?.effect === 'slide') {
      const direction = interaction.direction || 'up';
      const start = direction === 'up' ? `translateY(${distance}px)`
        : direction === 'down' ? `translateY(-${distance}px)`
        : direction === 'left' ? `translateX(${distance}px)`
        : `translateX(-${distance}px)`;
      return [{ opacity: .001, transform: start }, { opacity: 1, transform: 'translate(0,0)' }];
    }
    if (interaction?.effect === 'scale') {
      const start = Number.isFinite(amount) && amount > 0 && amount <= 5 ? amount : .92;
      return [{ opacity: .001, transform: `scale(${start})` }, { opacity: 1, transform: 'scale(1)' }];
    }
    if (interaction?.effect === 'rotate') {
      const degrees = Number.isFinite(amount) ? amount : -8;
      return [{ opacity: .001, transform: `rotate(${degrees}deg)` }, { opacity: 1, transform: 'rotate(0deg)' }];
    }
    if (interaction?.effect === 'blur') {
      const pixels = Number.isFinite(amount) && amount >= 0 ? amount : 12;
      return [{ opacity: .001, filter: `blur(${pixels}px)` }, { opacity: 1, filter: 'blur(0px)' }];
    }
    return [{ opacity: .001 }, { opacity: 1 }];
  }

  function playMotion(el, interaction) {
    if (!el || prefersReducedMotion() || typeof el.animate !== 'function') return null;
    try {
      return el.animate(motionKeyframes(interaction), {
        duration: Math.max(50, Math.min(10000, Number(interaction.durationMs) || 500)),
        delay: Math.max(0, Math.min(10000, Number(interaction.delayMs) || 0)),
        easing: interaction.easing || 'ease-out',
        fill: 'none'
      });
    } catch { return null; }
  }

  function clearMotionBinding(el) {
    const current = motionBindings.get(el);
    if (!current) return;
    for (const cleanup of current.cleanups || []) {
      try { cleanup(); } catch {}
    }
    motionBindings.delete(el);
  }

  function bindMotion(el, interactions) {
    if (!el || editorMode || renderInput?.server) return;
    const items = Array.isArray(interactions) ? interactions : [];
    const signature = JSON.stringify(items);
    const existing = motionBindings.get(el);
    if (existing?.signature === signature) return;
    clearMotionBinding(el);
    if (!items.length || prefersReducedMotion()) return;

    const cleanups = [];
    for (const interaction of items) {
      if (!interaction?.trigger || !interaction?.effect) continue;
      if (interaction.trigger === 'load') {
        const timer = setTimeout(() => playMotion(el, interaction), 0);
        cleanups.push(() => clearTimeout(timer));
      } else if (interaction.trigger === 'hover') {
        const handler = () => playMotion(el, interaction);
        el.addEventListener('mouseenter', handler);
        cleanups.push(() => el.removeEventListener('mouseenter', handler));
      } else if (interaction.trigger === 'click') {
        const handler = () => playMotion(el, interaction);
        el.addEventListener('click', handler);
        cleanups.push(() => el.removeEventListener('click', handler));
      } else if (interaction.trigger === 'enter-view') {
        if (typeof IntersectionObserver === 'function') {
          let observer = null;
          observer = new IntersectionObserver(entries => {
            for (const entry of entries) {
              if (!entry.isIntersecting) continue;
              playMotion(el, interaction);
              if (interaction.once !== false) observer?.unobserve(el);
            }
          }, { threshold: .15 });
          observer.observe(el);
          cleanups.push(() => observer.disconnect());
        } else {
          const timer = setTimeout(() => playMotion(el, interaction), 0);
          cleanups.push(() => clearTimeout(timer));
        }
      }
    }
    motionBindings.set(el, { signature, cleanups });
  }

  function applyElementOverride(el, override) {
    if (el?.dataset.cmsSignalOnly) return;
    if (!el || !override) return;
    const resolved = resolvedVariant(override);
    if (editorMode) {
      if (override.editorLocked === true) {
        el.dataset.cmsLocked = 'true';
        el.classList.add('legend-cms-locked');
      } else {
        delete el.dataset.cmsLocked;
        el.classList.remove('legend-cms-locked');
      }
    }
    if (override.actionKey) el.dataset.websiteActionKey = override.actionKey;
    else delete el.dataset.websiteActionKey;
    if (resolved.hidden === true) el.hidden = true;
    else if (resolved.hidden === false) el.hidden = false;

    if (el instanceof HTMLImageElement) {
      if (override.imageDataUrl) el.src = mediaUrl(override.imageDataUrl);
      if (override.isDecorative === true) {
        el.alt = '';
        el.setAttribute('aria-hidden', 'true');
      } else {
        el.removeAttribute('aria-hidden');
        if (override.alt != null) el.alt = override.alt;
      }
    } else if (override.text != null && !el.dataset.cmsSection && !['DIV','ARTICLE','HEADER','FOOTER'].includes(el.tagName)) {
      setContentText(el, override.text, true);
    }

    if (override.href != null && el.tagName === 'A' && safeUrl(override.href)) { el.href = override.href; el.target = override.target === '_blank' ? '_blank' : '_self'; el.rel = 'noopener noreferrer'; }
    if (override.videoUrl && el.tagName === 'VIDEO' && safeUrl(override.videoUrl, true)) el.src = mediaUrl(override.videoUrl);
    if (override.type === 'code' && el.querySelector?.('iframe[data-cms-code-frame]')) renderCodePreview(el, override);
    applyStyle(el, resolved.style);
    applyLayout(el, resolved.layout);
    bindMotion(el, override.interactions);
    syncReusableMemberDom(el, override);
  }

  function createExtraElement(extra) {
    let el;
    if (extra.type === 'heading') {
      el = document.createElement('h2');
      setContentText(el, extra.text || 'Your heading', true);
      el.className = 'cms-extra cms-extra-heading';
    } else if (extra.type === 'quote') {
      el = document.createElement('blockquote');
      setContentText(el, extra.text || 'Your quote', true);
      el.className = 'cms-extra cms-extra-quote';
    } else if (extra.type === 'divider') {
      el = document.createElement('hr');
      el.className = 'cms-extra cms-extra-divider';
    } else if (extra.type === 'spacer') {
      el = document.createElement('div');
      el.className = 'cms-extra cms-extra-spacer';
      el.setAttribute('aria-hidden', 'true');
    } else if (extra.type === 'shape') {
      el = document.createElement('div');
      el.className = 'cms-extra cms-extra-shape';
      el.setAttribute('aria-hidden', 'true');
    } else if (extra.type === 'container') {
      el = document.createElement('div');
      el.className = 'cms-extra cms-extra-container';
      el.setAttribute('role', 'group');
    } else if (extra.type === 'image') {
      el = document.createElement('img');
      el.src = mediaUrl(extra.imageDataUrl || '');
      el.alt = '';
      el.className = 'cms-extra cms-extra-image';
    } else if (extra.type === 'section') {
      el = document.createElement('section');
      el.dataset.cmsSection = `extra:${extra.id}`;
      el.className = 'cms-extra cms-extra-section';
    } else if (extra.type === 'video') {
      el = document.createElement('video');
      el.controls = true;
      el.preload = 'metadata';
      if (safeUrl(extra.videoUrl, true)) el.src = mediaUrl(extra.videoUrl);
      el.className = 'cms-extra';
    } else if (extra.type === 'card') {
      el = document.createElement('article');
      el.className = 'cms-extra card cms-extra-card';
      const heading = document.createElement('h3');
      setContentText(heading, extra.title || 'New service', true);
      const copy = document.createElement('p');
      setContentText(copy, extra.text || '', true);
      for (const [node, field] of [[heading, 'title'], [copy, 'text']]) {
        node.dataset.cmsExtraId = extra.id;
        node.dataset.cmsExtraField = field;
        node.dataset.cmsId = `extra:${extra.id}:${field}`;
        node.dataset.cmsEditable = 'true';
      }
      el.append(heading, copy);
    } else if (extra.type === 'group') {
      el = document.createElement('div');
      el.className = 'cms-extra cms-extra-group';
      el.setAttribute('role', 'group');
    } else if (extra.type === 'button') {
      el = document.createElement('a');
      el.textContent = extra.text || 'New button';
      if (safeUrl(extra.href)) el.href = extra.href;
      el.className = 'cms-extra btn primary';
    } else if (extra.type === 'code') {
      el = document.createElement('div');
      el.className = 'cms-extra cms-extra-code';
      const frame = document.createElement('iframe');
      frame.dataset.cmsCodeFrame = 'true';
      frame.title = 'Custom code block';
      frame.setAttribute('sandbox', 'allow-scripts allow-forms allow-modals allow-popups');
      frame.setAttribute('referrerpolicy', 'no-referrer');
      frame.setAttribute('loading', 'lazy');
      el.appendChild(frame);
      renderCodePreview(el, extra);
    } else {
      el = document.createElement('p');
      el.textContent = extra.text || '';
      el.className = 'cms-extra cms-extra-text';
    }
    el.dataset.cmsExtraId = extra.id;
    el.dataset.cmsId = `extra:${extra.id}`;
    el.dataset.cmsEditable = 'true';
    return el;
  }

  function reusableLocalId(value) {
    const raw = String(value || '');
    return raw.startsWith('extra:') ? raw.slice('extra:'.length) : raw;
  }

  function remapReusableNode(node, definitionId, localId, instanceId) {
    const nodes = [node, ...node.querySelectorAll('[data-cms-editable="true"]')];
    for (const current of nodes) {
      delete current.dataset.cmsExtraId;
      current.dataset.cmsReusableDefinitionId = definitionId;
      current.dataset.cmsReusableLocalId = localId;
      current.dataset.cmsReusableInstanceId = instanceId;
      const field = current.dataset.cmsExtraField || 'root';
      current.dataset.cmsId = `reusable:${instanceId}:${localId}:${field}`;
      current.dataset.cmsEditable = 'true';
    }
  }

  function placeReusableComponent(node, component, root, nodesById) {
    const placement = component.placement || {};
    const container = placement.containerId ? nodesById.get(reusableLocalId(placement.containerId)) : null;
    const destination = container || root;
    const before = placement.beforeId ? nodesById.get(reusableLocalId(placement.beforeId)) : null;
    destination.insertBefore(node, before?.parentElement === destination && before !== node ? before : null);
    if (placement.flow !== true && placement.column) {
      node.style.setProperty('--cms-column', String(Math.max(1, Math.min(24, Number(placement.column) || 1))));
      node.style.setProperty('--cms-span', String(Math.max(1, Math.min(24, Number(placement.span) || 1))));
    }
  }

  function renderReusableInstance(wrapper, instance) {
    const definition = reusableDefinition(instance.reusableDefinitionId);
    if (!definition) {
      wrapper.dataset.cmsReusableMissing = 'true';
      if (editorMode) wrapper.textContent = 'Missing synced component definition';
      return;
    }

    wrapper.dataset.cmsReusableDefinitionIdRef = definition.id;
    wrapper.dataset.cmsReusableName = definition.name || 'Synced component';

    const root = document.createElement('div');
    root.className = 'cms-reusable-content';
    root.dataset.cmsReusableDefinitionId = definition.id;
    root.dataset.cmsReusableLocalId = '__root__';
    root.dataset.cmsReusableInstanceId = instance.id;
    root.dataset.cmsId = `reusable:${instance.id}:__root__`;
    root.dataset.cmsEditable = 'true';
    root.setAttribute('role', 'group');
    wrapper.appendChild(root);
    applyElementOverride(root, definition);

    const nodesById = new Map();
    for (const component of definition.components || []) {
      const node = createExtraElement(component);
      remapReusableNode(node, definition.id, component.id, instance.id);
      nodesById.set(component.id, node);
      root.appendChild(node);
    }
    for (const component of definition.components || []) {
      const node = nodesById.get(component.id);
      if (!node) continue;
      placeReusableComponent(node, component, root, nodesById);
      applyElementOverride(node, component);
      node.querySelectorAll('[data-cms-reusable-definition-id]').forEach(child => {
        if (child.dataset.cmsExtraField) applyReusableMemberElement(child, component);
      });
    }
  }

  function createExtra(extra) {
    const section = extra.type === 'section'
      ? document.querySelector('main')
      : document.querySelector(`[data-cms-section="${CSS.escape(extra.sectionId)}"]`);
    if (!section) return null;

    const el = extra.type === 'reusable'
      ? document.createElement('div')
      : createExtraElement(extra);

    if (extra.type === 'reusable') {
      el.className = 'cms-extra cms-extra-reusable';
      el.dataset.cmsExtraId = extra.id;
      el.dataset.cmsId = `extra:${extra.id}`;
      el.dataset.cmsEditable = 'true';
      el.setAttribute('role', 'group');
    }

    section.appendChild(el);
    applyElementOverride(el, extra);
    if (extra.type === 'reusable') renderReusableInstance(el, extra);
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

  function refreshResponsiveOverrides() {
    Object.entries(pageState().elements).forEach(([id, override]) => {
      applyElementOverride(document.querySelector(`[data-cms-id="${CSS.escape(id)}"]`), override);
    });
    pageState().extras.forEach(extra => {
      applyElementOverride(document.querySelector(`[data-cms-id="extra:${CSS.escape(extra.id)}"]`), extra);
    });
    document.querySelectorAll('[data-cms-reusable-definition-id]').forEach(el => {
      const override = overrideForElement(el, false);
      if (override) applyReusableMemberElement(el, override);
    });
    updateDirectCanvasUi();
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
        const inquiryForm = document.querySelector('form[data-website-inquiry][data-form-key]');
        window.LEGEND_PUBLIC_META_SESSION = window.metaSignalIntelligence.createLandingSession({
          ...meta,
          siteKey: SITE_KEY,
          quoteType: SITE_KEY === 'business' ? 'business' : 'legend',
          pageKey,
          effectivePageKey: pageKey,
          pageVariant: SITE_KEY + '_website',
          pageMode: 'site_mode',
          formId: inquiryForm?.dataset.formKey || '',
          requiredContactFields: inquiryForm ? ['FirstName','LastName','Phone','Email'] : []
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

  function selectionItems() {
    return [...selectedElements].filter(el => el?.isConnected !== false);
  }

  function refreshSelectionClasses() {
    document.querySelectorAll('.legend-cms-selected,.legend-cms-multi-selected').forEach(el => {
      el.classList.remove('legend-cms-selected','legend-cms-multi-selected');
    });
    const items = selectionItems();
    for (const el of items) el.classList.add(items.length > 1 ? 'legend-cms-multi-selected' : 'legend-cms-selected');
    if (selected && items.includes(selected)) selected.classList.add('legend-cms-selected');
  }

  function setSelected(el, mode = 'replace') {
    const previous = selected;
    if (inlineEditNode && (mode !== 'replace' || previous !== el)) deactivateInlineEditing(inlineEditNode);

    if (!el) {
      selectedElements.clear();
      selected = null;
    } else if (mode === 'toggle') {
      if (selectedElements.has(el)) selectedElements.delete(el);
      else selectedElements.add(el);
      selected = selectedElements.has(el) ? el : selectionItems().at(-1) || null;
    } else if (mode === 'add') {
      selectedElements.add(el);
      selected = el;
    } else {
      selectedElements.clear();
      selectedElements.add(el);
      selected = el;
    }

    const items = selectionItems();
    const sections = new Set(items.map(currentSectionFor).filter(Boolean));
    selectedSection = sections.size === 1 ? [...sections][0] : currentSectionFor(selected);
    refreshSelectionClasses();

    if (selected) {
      selected.draggable = false;
      if (items.length === 1) activateInlineEditing(selected);
    } else if (previous) {
      deactivateInlineEditing(previous);
    }

    syncEditorControls();
    renderSignalControls();
    showPanel('content');
    refreshLayers();
    updateDirectCanvasUi();
  }

  function selectedOverride() {
    return overrideForElement(selected);
  }

  function commonSelectionParent() {
    const items = selectionItems();
    if (items.length < 2) return null;
    const parent = items[0].parentElement;
    return parent && items.every(el => el.parentElement === parent) ? parent : null;
  }

  function commonSelectionIsEditable() {
    return selectionItems().every(el => {
      const ov = overrideForElement(el, false);
      return ov?.editorLocked !== true && el.dataset.cmsLocked !== 'true';
    });
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

  function selectionBounds() {
    const rects = selectionItems()
      .filter(el => !el.dataset.cmsSignalOnly && !el.closest('.legend-cms-editor'))
      .map(previewRelativeRect)
      .filter(Boolean);
    if (!rects.length) return null;
    const left = Math.min(...rects.map(rect => rect.left));
    const top = Math.min(...rects.map(rect => rect.top));
    const right = Math.max(...rects.map(rect => rect.left + rect.width));
    const bottom = Math.max(...rects.map(rect => rect.top + rect.height));
    return { left, top, width: right - left, height: bottom - top };
  }

  function updateDirectCanvasUi() {
    const items = selectionItems();
    if (!selectionFrame || !editorPreview || !items.length) {
      if (selectionFrame) selectionFrame.hidden = true;
      if (gridOverlay && !directGesture) gridOverlay.hidden = true;
      return;
    }
    const rect = selectionBounds();
    if (!rect) { selectionFrame.hidden = true; return; }
    selectionFrame.hidden = false;
    selectionFrame.style.left = `${rect.left}px`;
    selectionFrame.style.top = `${rect.top}px`;
    selectionFrame.style.width = `${Math.max(rect.width, 1)}px`;
    selectionFrame.style.height = `${Math.max(rect.height, 1)}px`;
    selectionFrame.dataset.sectionSelected = items.length === 1 && selected?.dataset.cmsSection ? 'true' : 'false';
    selectionFrame.dataset.multiSelected = items.length > 1 ? 'true' : 'false';
    if (directGesture) positionGridOverlay(directGesture.section);
  }

  function installDirectCanvasControls(preview) {
    editorPreview = preview;
    gridOverlay = document.createElement('div');
    gridOverlay.className = 'legend-cms-grid-overlay';
    gridOverlay.hidden = true;
    gridOverlay.setAttribute('aria-hidden', 'true');
    marqueeOverlay = document.createElement('div');
    marqueeOverlay.className = 'legend-cms-marquee';
    marqueeOverlay.hidden = true;
    marqueeOverlay.setAttribute('aria-hidden', 'true');
    selectionFrame = document.createElement('div');
    selectionFrame.className = 'legend-cms-selection-frame';
    selectionFrame.hidden = true;
    selectionFrame.innerHTML = `
      <button type="button" class="legend-cms-move-handle" data-cms-gesture="move" aria-label="Move selected block on grid" title="Move on grid">Move</button>
      <button type="button" class="legend-cms-resize-handle legend-cms-resize-x" data-cms-gesture="resize-x" aria-label="Resize selected block width" title="Resize width"></button>
      <button type="button" class="legend-cms-resize-handle legend-cms-resize-y" data-cms-gesture="resize-y" aria-label="Resize selected block height" title="Resize height"></button>
      <button type="button" class="legend-cms-resize-handle legend-cms-resize-xy" data-cms-gesture="resize-xy" aria-label="Resize selected block width and height" title="Resize width and height"></button>`;
    preview.appendChild(gridOverlay);
    preview.appendChild(marqueeOverlay);
    preview.appendChild(selectionFrame);

    const previewPoint = event => {
      const rect = preview.getBoundingClientRect();
      return {
        x: event.clientX - rect.left + preview.scrollLeft,
        y: event.clientY - rect.top + preview.scrollTop
      };
    };

    const updateMarquee = event => {
      if (!marqueeGesture) return;
      const point = previewPoint(event);
      const left = Math.min(marqueeGesture.start.x, point.x);
      const top = Math.min(marqueeGesture.start.y, point.y);
      const right = Math.max(marqueeGesture.start.x, point.x);
      const bottom = Math.max(marqueeGesture.start.y, point.y);
      marqueeGesture.rect = { left, top, width: right - left, height: bottom - top };
      marqueeOverlay.hidden = false;
      marqueeOverlay.style.left = `${left}px`;
      marqueeOverlay.style.top = `${top}px`;
      marqueeOverlay.style.width = `${right-left}px`;
      marqueeOverlay.style.height = `${bottom-top}px`;
    };

    const finishMarquee = () => {
      if (!marqueeGesture) return;
      const gesture = marqueeGesture;
      marqueeGesture = null;
      marqueeOverlay.hidden = true;
      const rect = gesture.rect;
      if (!rect || rect.width < 4 || rect.height < 4) return;
      const matches = [...preview.querySelectorAll('[data-cms-editable="true"]')].filter(el => {
        if (el.closest('.legend-cms-editor') || el.dataset.cmsSignalOnly || el.dataset.cmsLocked === 'true' || el.hidden) return false;
        if (el.dataset.cmsSection) return false;
        if (['DIV','ARTICLE','HEADER','FOOTER'].includes(el.tagName) && !el.classList.contains('cms-extra-group')) return false;
        const interactiveAncestor = el.parentElement?.closest?.('a[data-cms-editable="true"],button[data-cms-editable="true"]');
        if (interactiveAncestor) return false;
        const item = previewRelativeRect(el);
        if (!item) return false;
        return item.left < rect.left + rect.width && item.left + item.width > rect.left &&
          item.top < rect.top + rect.height && item.top + item.height > rect.top;
      });
      selectedElements.clear();
      if (gesture.additive) gesture.initial.forEach(el => selectedElements.add(el));
      matches.forEach(el => selectedElements.add(el));
      selected = selectionItems().at(-1) || null;
      const sections = new Set(selectionItems().map(currentSectionFor).filter(Boolean));
      selectedSection = sections.size === 1 ? [...sections][0] : currentSectionFor(selected);
      refreshSelectionClasses();
      syncEditorControls();
      renderSignalControls();
      refreshLayers();
      updateDirectCanvasUi();
      if (selected) showPanel('content');
    };

    preview.addEventListener('pointerdown', event => {
      if (event.button !== undefined && event.button !== 0) return;
      if (event.target.closest?.('[data-cms-editable="true"],.legend-cms-editor,[data-cms-gesture]')) return;
      const additive = event.shiftKey || event.metaKey || event.ctrlKey;
      marqueeGesture = {
        start: previewPoint(event),
        rect: null,
        additive,
        initial: additive ? new Set(selectionItems()) : new Set()
      };
      event.preventDefault();
    });
    window.addEventListener('pointermove', updateMarquee, { passive: false });
    window.addEventListener('pointerup', finishMarquee);
    window.addEventListener('pointercancel', finishMarquee);

    const startGesture = event => {
      const handle = event.target.closest?.('[data-cms-gesture]');
      if (!handle || !selected || selectionItems().length !== 1 || selected.dataset.cmsSignalOnly) return;
      const mode = handle.dataset.cmsGesture;
      if (mode === 'move' && selected.dataset.cmsSection) return;
      const section = selectedSection || currentSectionFor(selected);
      const parent = selected.parentElement;
      if (!section || !parent) return;
      const selectedRect = selected.getBoundingClientRect();
      const sectionRect = section.getBoundingClientRect();
      const parentRect = parent.getBoundingClientRect();
      const parentOverride = overrideForElement(parent, false);
      const freeCanvas = resolvedVariant(parentOverride).layout?.mode === 'free';
      const override = selectedOverride();
      if (!override) return;
      const resolvedStyle = resolvedVariant(override).style || {};
      const variant = editableVariant(override);
      variant.style ||= {};
      checkpoint();
      directGesture = {
        mode, target: selected, section, parent,
        startX: event.clientX, startY: event.clientY,
        selectedRect, sectionRect, parentRect, freeCanvas,
        startWidthPercent: positiveNumber(resolvedStyle.widthPercent) ? Number(resolvedStyle.widthPercent) : (parentRect.width > 0 ? selectedRect.width / parentRect.width * 100 : 100),
        startHeightPx: positiveNumber(resolvedStyle.heightPx) ? Number(resolvedStyle.heightPx) : Math.max(selectedRect.height, 24),
        startOffsetXPercent: Number.isFinite(Number(resolvedStyle.offsetXPercent)) ? Number(resolvedStyle.offsetXPercent) : 0,
        startOffsetYPx: Number.isFinite(Number(resolvedStyle.offsetYPx)) ? Number(resolvedStyle.offsetYPx) : 0,
        changed: false
      };
      gridOverlay.hidden = false;
      gridOverlay.classList.remove('legend-cms-snap-x','legend-cms-snap-y');
      positionGridOverlay(section);
      handle.setPointerCapture?.(event.pointerId);
      event.preventDefault();
      event.stopPropagation();
    };

    selectionFrame.addEventListener('pointerdown', startGesture);
    window.addEventListener('pointermove', event => {
      const gesture = directGesture;
      if (!gesture || selected !== gesture.target) return;
      const dx = event.clientX - gesture.startX;
      const dy = event.clientY - gesture.startY;
      const override = selectedOverride();
      if (!override) return;
      const variant = editableVariant(override);
      variant.style ||= {};
      const style = variant.style;
      const sectionWidth = gesture.sectionRect.width || gesture.parentRect.width || 1;
      const parentWidth = gesture.parentRect.width || sectionWidth || 1;
      const cell = sectionWidth / 12;
      const verticalStep = 24;
      let snappedDx = dx;
      let snappedDy = dy;
      gridOverlay.classList.remove('legend-cms-snap-x','legend-cms-snap-y');

      if (gesture.mode === 'move') {
        if (cell > 0) {
          const desiredLeft = gesture.selectedRect.left + dx;
          const gridLeft = gesture.sectionRect.left + Math.round((desiredLeft - gesture.sectionRect.left) / cell) * cell;
          snappedDx = gridLeft - gesture.selectedRect.left;
        }
        snappedDy = Math.round(dy / verticalStep) * verticalStep;
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
        if (gesture.freeCanvas) {
          style.positionMode = 'absolute';
          style.offsetXPercent = Math.round(((gesture.selectedRect.left + snappedDx - gesture.parentRect.left) / parentWidth * 100) * 1000) / 1000;
          style.offsetYPx = Math.round((gesture.selectedRect.top + snappedDy - gesture.parentRect.top) * 1000) / 1000;
        } else {
          style.offsetXPercent = Math.round((gesture.startOffsetXPercent + snappedDx / parentWidth * 100) * 1000) / 1000;
          style.offsetYPx = Math.round((gesture.startOffsetYPx + snappedDy) * 1000) / 1000;
        }
      } else {
        if (gesture.mode === 'resize-x' || gesture.mode === 'resize-xy') {
          const rawWidth = gesture.startWidthPercent + dx / parentWidth * 100;
          const snappedWidth = cell > 0 ? Math.round((rawWidth / 100 * parentWidth) / cell) * cell / parentWidth * 100 : rawWidth;
          style.widthPercent = Math.max(5, Math.min(100, Math.round(snappedWidth * 1000) / 1000));
        }
        if (gesture.mode === 'resize-y' || gesture.mode === 'resize-xy') {
          style.heightPx = Math.max(24, Math.round((gesture.startHeightPx + dy) / verticalStep) * verticalStep);
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
    preview.addEventListener('scroll', updateDirectCanvasUi, { passive: true });
    window.addEventListener('resize', () => { refreshScaledElements(); refreshResponsiveOverrides(); updateDirectCanvasUi(); });
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
    const decorative = document.getElementById('legend-cms-decorative');
    const lockButton = document.getElementById('legend-cms-lock');
    const renameButton = document.getElementById('legend-cms-layer-rename');
    const removeButton = document.getElementById('legend-cms-remove');
    const selectionCount = selectionItems().length;
    const multi = selectionCount > 1;

    document.querySelectorAll('[data-cms-view="content"] input,[data-cms-view="content"] textarea,[data-cms-view="content"] select,[data-cms-view="appearance"] input,[data-cms-view="appearance"] select,[data-cms-view="layout"] input,[data-cms-view="layout"] select').forEach(control => { control.disabled = !selected || !!selected.dataset.cmsSignalOnly || multi; });
    if (!selected) {
      if (title) title.textContent = 'Select content on the page';
      if (inlineHelp) inlineHelp.hidden = true;
      if (imageGroup) imageGroup.hidden = true;
      if (codeGroup) codeGroup.hidden = true;
      if (lockButton) { lockButton.disabled = true; lockButton.textContent = 'Lock selected'; }
      if (renameButton) renameButton.disabled = true;
      if (removeButton) { removeButton.disabled = true; removeButton.textContent = 'Delete selected'; }
      syncMultiSelectionControls();
      return;
    }

    if (multi) {
      const breakpoint = currentDesignBreakpoint === 'base' ? 'Desktop / base' : breakpointById(currentDesignBreakpoint)?.label || currentDesignBreakpoint;
      if (title) title.textContent = `${selectionCount} elements selected · ${breakpoint}`;
      if (inlineHelp) inlineHelp.hidden = true;
      if (imageGroup) imageGroup.hidden = true;
      if (codeGroup) codeGroup.hidden = true;
      if (lockButton) { lockButton.disabled = true; lockButton.textContent = 'Lock selected'; }
      if (renameButton) renameButton.disabled = true;
      if (removeButton) { removeButton.disabled = false; removeButton.textContent = `Delete ${selectionCount} selected`; }
      syncMultiSelectionControls();
      return;
    }

    if (title) {
      const breakpoint = currentDesignBreakpoint === 'base' ? 'Desktop / base' : breakpointById(currentDesignBreakpoint)?.label || currentDesignBreakpoint;
      const shared = selected.dataset.cmsReusableDefinitionId ? ' · Synced edit' : '';
      title.textContent = `${elementLabel(selected)} · ${breakpoint}${shared}`;
    }
    const isImage = selected instanceof HTMLImageElement;
    const extra = selected.dataset.cmsExtraId ? pageState().extras.find(x => x.id === selected.dataset.cmsExtraId) : null;
    const selectedCapability = selectedComponentCapability();
    const isCode = selectedCapability?.type === 'code';
    if (inlineHelp) inlineHelp.hidden = !isInlineEditable(selected);
    if (imageGroup) imageGroup.hidden = !isImage;
    if (codeGroup) codeGroup.hidden = !isCode;

    const ov = selectedOverride() || extra || pageState().elements[selected.dataset.cmsId] || {};
    const variant = editableVariant(ov, false);
    const resolved = resolvedVariant(ov);
    const variantStyle = variant?.style || {};
    const resolvedStyle = resolved.style || {};
    const reusableRoot = selected.dataset.cmsReusableDefinitionId && selected.dataset.cmsReusableLocalId === '__root__';
    if (lockButton) {
      lockButton.disabled = !!reusableRoot;
      lockButton.textContent = ov.editorLocked === true ? 'Unlock selected' : 'Lock selected';
    }
    if (renameButton) renameButton.disabled = false;
    const computed = getComputedStyle(selected);
    const parentStyle = selected.parentElement ? getComputedStyle(selected.parentElement) : null;
    const parentWidth = selected.parentElement
      ? selected.parentElement.clientWidth - (parseFloat(parentStyle.paddingLeft) || 0) - (parseFloat(parentStyle.paddingRight) || 0) : 0;
    const actualWidth = parentWidth > 0 ? parseFloat(computed.width) / parentWidth * 100 : 100;
    const displayNumber = value => String(Math.round(value * 1000) / 1000);
    const targetInput = document.getElementById('legend-cms-target'); if (targetInput) targetInput.checked = (ov.target ?? selected.getAttribute('target')) === '_blank';
    const serviceCard = businessServiceCardFor(selected);
    const duplicateButton = document.getElementById('legend-cms-duplicate'); if (duplicateButton) duplicateButton.textContent = serviceCard ? 'Duplicate service' : 'Duplicate block';
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
    if (scale) scale.value = String(variantStyle.fontScale ?? resolvedStyle.fontScale ?? 1);
    if (width) width.value = displayNumber(variantStyle.widthPercent ?? resolvedStyle.widthPercent ?? (Number.isFinite(actualWidth) ? actualWidth : 100));
    if (height) height.value = variantStyle.heightPx != null ? displayNumber(variantStyle.heightPx) : resolvedStyle.heightPx != null ? displayNumber(resolvedStyle.heightPx) : '';
    if (top) top.value = displayNumber(variantStyle.paddingTop ?? resolvedStyle.paddingTop ?? (parseFloat(computed.paddingTop) || 0));
    if (bottom) bottom.value = displayNumber(variantStyle.paddingBottom ?? resolvedStyle.paddingBottom ?? (parseFloat(computed.paddingBottom) || 0));
    if (offsetX) offsetX.value = displayNumber(variantStyle.offsetXPercent ?? resolvedStyle.offsetXPercent ?? 0);
    if (offsetY) offsetY.value = displayNumber(variantStyle.offsetYPx ?? resolvedStyle.offsetYPx ?? 0);
    if (align) align.value = variantStyle.textAlign ?? resolvedStyle.textAlign ?? computed.textAlign ?? '';
    if (scale) scale.disabled = isImage || isCode;
    const values = { href: ov.href ?? rememberOriginal(selected).href ?? '', alt: ov.alt ?? selected.getAttribute('alt') ?? '', videoUrl: ov.videoUrl ?? selected.getAttribute('src') ?? '' };
    Object.entries(values).forEach(([key,value]) => { const input = document.getElementById(`legend-cms-${key}`); if(input) input.value = value; });
    if (decorative) decorative.checked = ov.isDecorative === true;
    document.querySelectorAll('[data-style-key]').forEach(input => {
      const key = input.dataset.styleKey;
      const value = variantStyle[key] ?? resolvedStyle[key];
      input.value = ['color','backgroundColor'].includes(key) ? colorHex(value || computed[key]) : value ?? (input.type === 'number' ? parseFloat(computed[key]) || '' : computed[key] || '');
    });
    document.querySelectorAll('[data-color-hex]').forEach(input => {
      const key = input.dataset.colorHex;
      input.value = colorHex(variantStyle[key] ?? resolvedStyle[key] ?? computed[key]);
    });
    document.querySelectorAll('[data-geometry-key]').forEach(input => {
      const key = input.dataset.geometryKey;
      const value = variantStyle[key] ?? resolvedStyle[key];
      input.value = value ?? '';
    });
    const linkGroup = document.getElementById('legend-cms-link-group'); if (linkGroup) linkGroup.hidden = selected.tagName !== 'A';
    if (selected.tagName === 'A') syncCtaControls(ov, values.href);
    const videoGroup = document.getElementById('legend-cms-video-group'); if (videoGroup) videoGroup.hidden = selected.tagName !== 'VIDEO';
    if (hidden) hidden.checked = (variant?.hidden ?? resolved.hidden) === true;
    syncLayoutControls(ov);
    syncMultiSelectionControls();
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
    const variant = editableVariant(ov);
    if (control.id === 'legend-cms-hidden') {
      variant.hidden = control.checked;
    } else {
      variant.style ||= {};
      if (field) {
        if (control.value === '') delete variant.style[field];
        else variant.style[field] = Number(control.value);
      } else if (control.id === 'legend-cms-align') {
        if (control.value) variant.style.textAlign = control.value;
        else delete variant.style.textAlign;
      } else return;
    }
    applyElementOverride(selected, ov);
    updateDirectCanvasUi();
    markDirty();
  }

  function formatMediaBytes(value) {
    const bytes = Number(value) || 0;
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(bytes < 10240 ? 1 : 0)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }

  function renderMediaLibrary() {
    const host = document.getElementById('legend-cms-media-grid');
    const status = document.getElementById('legend-cms-media-status');
    if (!host) return;
    host.replaceChildren();
    const filter = (document.getElementById('legend-cms-media-search')?.value || '').trim().toLowerCase();
    const items = mediaLibraryAssets.filter(asset => {
      if (!filter) return true;
      return String(asset.sourceName || '').toLowerCase().includes(filter) ||
        String(asset.contentType || '').toLowerCase().includes(filter);
    });
    if (status) status.textContent = `${items.length} of ${mediaLibraryAssets.length} asset${mediaLibraryAssets.length === 1 ? '' : 's'}`;
    if (!items.length) {
      const empty = document.createElement('p');
      empty.textContent = mediaLibraryAssets.length ? 'No media matches this search.' : 'No media has been uploaded to this website yet.';
      host.appendChild(empty);
      return;
    }

    for (const asset of items) {
      const card = document.createElement('article');
      card.className = 'legend-cms-media-card';
      const preview = asset.contentType?.startsWith('video/') ? document.createElement('video') : document.createElement('img');
      preview.className = 'legend-cms-media-preview';
      preview.src = mediaUrl(asset.url);
      if (preview.tagName === 'VIDEO') {
        preview.muted = true;
        preview.preload = 'metadata';
      } else {
        preview.alt = asset.sourceName ? `Media preview: ${asset.sourceName}` : 'Website media preview';
        preview.loading = 'lazy';
      }
      const name = document.createElement('strong');
      name.textContent = asset.sourceName || (asset.contentType?.startsWith('video/') ? 'Video' : 'Image');
      const meta = document.createElement('small');
      meta.textContent = [asset.contentType, formatMediaBytes(asset.sizeBytes)].filter(Boolean).join(' · ');
      const use = document.createElement('button');
      use.type = 'button';
      use.textContent = asset.contentType?.startsWith('video/') ? 'Use video' : 'Use image';
      use.addEventListener('click', () => useMediaAsset(asset));
      card.append(preview, name, meta, use);
      host.appendChild(card);
    }
  }

  async function loadMediaLibrary() {
    const status = document.getElementById('legend-cms-media-status');
    if (status) status.textContent = 'Loading media…';
    try {
      const url = new URL(`${API_BASE}/api/website-content/manage/media`);
      url.searchParams.set('ticket', editorTicket);
      const response = await fetch(url, { cache: 'no-store' });
      const payload = await response.json().catch(() => ({}));
      if (!response.ok || !Array.isArray(payload.assets)) throw new Error(payload.message || payload.error || 'Media library unavailable.');
      mediaLibraryAssets = payload.assets;
      renderMediaLibrary();
    } catch (error) {
      mediaLibraryAssets = [];
      if (status) status.textContent = error?.message || 'Media library unavailable.';
      renderMediaLibrary();
    }
  }

  function useMediaAsset(asset) {
    if (!asset?.url || typeof asset.contentType !== 'string') return;
    const isImage = asset.contentType.startsWith('image/');
    const isVideo = asset.contentType.startsWith('video/');
    if (!isImage && !isVideo) return;

    if (selectionItems().length === 1 && isImage && selected instanceof HTMLImageElement) {
      checkpoint();
      const override = selectedOverride();
      if (!override) return;
      override.imageDataUrl = asset.url;
      applyElementOverride(selected, override);
      markDirty();
      syncEditorControls();
      showPanel('content');
      return;
    }
    if (selectionItems().length === 1 && isVideo && selected?.tagName === 'VIDEO') {
      checkpoint();
      const override = selectedOverride();
      if (!override) return;
      override.videoUrl = asset.url;
      applyElementOverride(selected, override);
      markDirty();
      syncEditorControls();
      showPanel('content');
      return;
    }

    const section = selectedSection || currentSectionFor(selected) || document.querySelector('[data-cms-section]');
    if (!section) {
      alert('Select a section before adding media.');
      return;
    }
    checkpoint();
    const type = isImage ? 'image' : 'video';
    const extra = {
      id: crypto.randomUUID ? crypto.randomUUID() : String(Date.now()),
      sectionId: section.dataset.cmsSection,
      type,
      style: isImage ? { widthPercent: 70 } : { widthPercent: 100 },
      ...(isImage ? { imageDataUrl: asset.url, alt: asset.sourceName || '' } : { videoUrl: asset.url })
    };
    const container = selectedFlowContainer(section);
    if (container) {
      extra.placement = {
        sectionId: section.dataset.cmsSection,
        containerId: container.dataset.cmsId,
        beforeId: null,
        flow: true,
        column: 1,
        span: 12
      };
    }
    pageState().extras.push(extra);
    const el = createExtra(extra);
    if (extra.placement) applyPlacement(el, extra.placement);
    setSelected(el);
    markDirty();
    showPanel('content');
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

  function resetBaselineElement(el) {
    if (!el?.dataset?.cmsId) return;
    delete pageState().elements[el.dataset.cmsId];
    const original = rememberOriginal(el);
    el.hidden = original.hidden;
    if (el instanceof HTMLImageElement) el.src = original.src || '';
    else if (!el.dataset.cmsSection && !['DIV','ARTICLE','HEADER','FOOTER'].includes(el.tagName)) setContentText(el, original.text);
    if (original.href != null) el.setAttribute('href', original.href);
    if (original.src != null) el.setAttribute('src', original.src);
    applyStyle(el, null);
    applyLayout(el, null);
    delete el.dataset.cmsLocked;
    el.classList.remove('legend-cms-locked');
  }

  function cascadeDeleteExtra(extraId) {
    const containerId = `extra:${extraId}`;
    const extra = pageState().extras.find(item => item.id === extraId);
    if (!extra) return;
    const descendantExtraIds = pageState().extras
      .filter(item => item.id !== extraId && item.placement?.containerId === containerId)
      .map(item => item.id);
    descendantExtraIds.forEach(cascadeDeleteExtra);

    Object.entries(pageState().elements).forEach(([id, override]) => {
      if (override?.placement?.containerId !== containerId) return;
      const node = document.querySelector(`[data-cms-id="${CSS.escape(id)}"]`);
      if (node) {
        override.hidden = true;
        override.placement = null;
        applyElementOverride(node, override);
      }
    });

    pageState().extras = pageState().extras.filter(item =>
      item.id !== extraId &&
      !(extra.type === 'section' && (item.sectionId === `extra:${extraId}` || item.placement?.sectionId === `extra:${extraId}`))
    );
    const node = document.querySelector(`[data-cms-id="extra:${CSS.escape(extraId)}"]`);
    if (node) { scaledElements.delete(node); node.remove(); }
  }

  function deleteSelection() {
    const items = selectionItems();
    if (!items.length || !commonSelectionIsEditable()) return;
    checkpoint();
    const handledExtras = new Set();

    for (const el of items) {
      if (el.dataset.cmsSignalOnly) {
        delete pageState().elements[el.dataset.cmsId];
        continue;
      }
      if (el.dataset.cmsExtraId) {
        const id = el.dataset.cmsExtraId;
        if (!handledExtras.has(id)) {
          handledExtras.add(id);
          cascadeDeleteExtra(id);
        }
        continue;
      }
      const override = overrideForElement(el);
      if (!override) continue;
      override.hidden = true;
      applyElementOverride(el, override);
    }

    setSelected(null);
    refreshLayers();
    markDirty();
  }

  function removeSelected() {
    if (!selected || selectionItems().length !== 1) return;
    checkpoint();
    if (selected.dataset.cmsSignalOnly) { delete pageState().elements[selected.dataset.cmsId]; markDirty(); renderSignalControls(); return; }
    if (selected.dataset.cmsExtraId) {
      cascadeDeleteExtra(selected.dataset.cmsExtraId);
      setSelected(null);
      markDirty();
      return;
    }
    resetBaselineElement(selected);
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
      if (payload.quality) savedQualityReport = payload.quality;
      dirty = changedDuringSave;
      if (!document.querySelector('[data-cms-view="quality"]')?.hidden) renderQualityPanel();
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
    baselineNodes.forEach(({ el, parent, next }) => { if (el.dataset.cmsSignalOnly) return; if (parent) parent.insertBefore(el, next?.parentElement === parent ? next : null); const original = rememberOriginal(el); el.hidden = original.hidden; if (!el.dataset.cmsSection && !['DIV','ARTICLE','HEADER','FOOTER'].includes(el.tagName)) { setContentText(el, original.text); } if (original.href != null) el.setAttribute('href',original.href); if (original.src != null) el.setAttribute('src',original.src); applyStyle(el, null); applyLayout(el, null); delete el.dataset.cmsLocked; el.classList.remove('legend-cms-locked'); });
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
    if (placement.flow === true && container && (container === section || section.contains(container)) && !el.contains(container) &&
        (container === section || !container.closest(lockedSelector))) {
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
  function liveQualityIssues() {
    const root = editorPreview || document;
    const issues = [];
    const add = (code, severity, category, scope, message) => issues.push({ code, severity, category, scope, message });
    const visible = element => !element.hidden && element.getAttribute?.('aria-hidden') !== 'true';

    const images = [...root.querySelectorAll('img')].filter(image => !image.closest('.legend-cms-editor') && visible(image));
    for (const image of images) {
      const override = overrideForElement(image, false);
      const decorative = override?.isDecorative === true || image.getAttribute('aria-hidden') === 'true';
      const alt = (override?.alt ?? image.getAttribute('alt') ?? '').trim();
      if (!decorative && !alt)
        add('live_image_alt_missing','warning','Accessibility',elementLabel(image),'Add image description text or mark this image decorative.');
    }

    const headings = [...root.querySelectorAll('main h1,main h2,main h3,main h4,main h5,main h6')]
      .filter(heading => !heading.closest('.legend-cms-editor') && visible(heading));
    const h1s = headings.filter(heading => heading.tagName === 'H1');
    if (h1s.length === 0) add('live_h1_missing','warning','Accessibility',pageKey,'Add one clear H1 page heading.');
    if (h1s.length > 1) add('live_h1_multiple','info','Accessibility',pageKey,`This page renders ${h1s.length} H1 headings. Confirm the heading hierarchy is intentional.`);
    let previousLevel = null;
    for (const heading of headings) {
      const level = Number(heading.tagName.slice(1));
      if (previousLevel && level > previousLevel + 1)
        add('live_heading_level_jump','warning','Accessibility',elementLabel(heading),`Heading level jumps from H${previousLevel} to H${level}. Use a logical heading order.`);
      previousLevel = level;
    }

    const controls = [...root.querySelectorAll('input:not([type="hidden"]):not([type="submit"]):not([type="button"]),select,textarea')]
      .filter(control => !control.closest('.legend-cms-editor') && visible(control));
    for (const control of controls) {
      const labelled = !!control.getAttribute('aria-label') || !!control.getAttribute('aria-labelledby') || !!control.closest('label') ||
        (!!control.id && !!root.querySelector(`label[for="${CSS.escape(control.id)}"]`));
      if (!labelled)
        add('live_form_label_missing','warning','Accessibility',control.name || control.id || control.tagName,'Add a visible label or accessible name for this field.');
    }

    const actions = [...root.querySelectorAll('a[href],button')]
      .filter(action => !action.closest('.legend-cms-editor') && visible(action));
    for (const action of actions) {
      const name = (action.getAttribute('aria-label') || action.textContent || '').trim();
      if (!name) add('live_action_name_missing','warning','Accessibility',action.getAttribute('href') || action.tagName,'Give this action visible text or an accessible name.');
    }

    const ids = new Map();
    [...root.querySelectorAll('[id]')].filter(element => !element.closest('.legend-cms-editor')).forEach(element => {
      if (!element.id) return;
      if (!ids.has(element.id)) ids.set(element.id, []);
      ids.get(element.id).push(element);
    });
    for (const [id, matches] of ids) if (matches.length > 1)
      add('live_duplicate_id','warning','Accessibility',id,`This rendered page contains ${matches.length} elements with the same id. IDs must be unique.`);

    if (editorPreview && editorPreview.clientWidth > 0 && editorPreview.scrollWidth > editorPreview.clientWidth + 2)
      add('live_horizontal_overflow','warning','Performance','Current viewport','The rendered page is wider than the active viewport. Check positioned elements, fixed widths, and custom code.');

    return issues;
  }

  function qualityCount(report, key) {
    const pascal = key[0].toUpperCase() + key.slice(1);
    return Number(report?.[key] ?? report?.[pascal] ?? 0) || 0;
  }

  function renderQualityIssueList(host, issues, emptyText) {
    host.replaceChildren();
    if (!issues?.length) {
      const empty = document.createElement('p');
      empty.className = 'legend-cms-quality-empty';
      empty.textContent = emptyText;
      host.appendChild(empty);
      return;
    }
    for (const issue of issues) {
      const card = document.createElement('article');
      card.className = `legend-cms-quality-issue legend-cms-quality-${issue.severity || 'info'}`;
      const heading = document.createElement('strong');
      heading.textContent = `${issue.category || 'Quality'} · ${String(issue.severity || 'info').toUpperCase()}`;
      const scope = document.createElement('small');
      scope.textContent = issue.scope || 'Website';
      const message = document.createElement('p');
      message.textContent = issue.message || issue.code || 'Review this item.';
      card.append(heading, scope, message);
      host.appendChild(card);
    }
  }

  function renderQualityPanel() {
    const serverSummary = document.getElementById('legend-cms-quality-server-summary');
    const serverHost = document.getElementById('legend-cms-quality-server');
    const liveSummary = document.getElementById('legend-cms-quality-live-summary');
    const liveHost = document.getElementById('legend-cms-quality-live');
    if (!serverHost || !liveHost) return;

    const serverIssues = savedQualityReport?.issues || savedQualityReport?.Issues || [];
    const errorCount = qualityCount(savedQualityReport,'errorCount');
    const warningCount = qualityCount(savedQualityReport,'warningCount');
    const infoCount = qualityCount(savedQualityReport,'infoCount');
    if (serverSummary) serverSummary.textContent = savedQualityReport
      ? `${errorCount} blocking · ${warningCount} warning · ${infoCount} review`
      : 'Save the draft to refresh authoritative server checks.';
    renderQualityIssueList(serverHost, serverIssues, 'Saved draft has no server-detected quality issues.');

    const liveIssues = liveQualityIssues();
    if (liveSummary) liveSummary.textContent = `${liveIssues.filter(issue=>issue.severity==='warning').length} warning · ${liveIssues.filter(issue=>issue.severity==='info').length} review`;
    renderQualityIssueList(liveHost, liveIssues, 'The current rendered page passes the available live checks.');
  }

  function showPanel(name) {
    document.querySelectorAll('[data-cms-view]').forEach(view => { view.hidden = view.dataset.cmsView !== name; });
    document.querySelectorAll('[data-open]').forEach(button => button.setAttribute('aria-pressed', String(button.dataset.open === name)));
    if (name === 'layers') refreshLayers();
    if (name === 'page') syncPageControls();
    if (name === 'motion') renderMotionControls();
    if (name === 'media') void loadMediaLibrary();
    if (name === 'quality') renderQualityPanel();
  }

  function elementLabel(el) {
    const pageExtra = el.dataset.cmsExtraId ? pageState().extras.find(item => item.id === el.dataset.cmsExtraId) : null;
    const definition = el.dataset.cmsReusableDefinitionId ? reusableDefinition(el.dataset.cmsReusableDefinitionId)
      : pageExtra?.type === 'reusable' ? reusableDefinition(pageExtra.reusableDefinitionId) : null;
    const kind = pageExtra?.type === 'reusable' ? 'Synced'
      : el.dataset.cmsReusableDefinitionId ? 'Synced member'
      : el.dataset.cmsSection ? 'Section'
      : ({ A: 'Link', IMG: 'Image', VIDEO: 'Video', H1: 'Heading', H2: 'Heading', H3: 'Heading', P: 'Text' }[el.tagName] || 'Block');
    const override = overrideForElement(el, false);
    const custom = String(override?.editorLabel || '').trim().slice(0,64);
    const label = definition?.name || custom || (el.getAttribute('alt') || el.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 64);
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
      const value = overrideForElement(el, false);
      select.textContent = label + (el.hidden ? ' · Hidden' : '') + (value?.editorLocked ? ' · Locked' : '');
      select.setAttribute('aria-pressed', String(el === selected));
      select.addEventListener('click', () => { setSelected(el); el.scrollIntoView?.({ block: 'nearest', behavior: 'smooth' }); });
      row.appendChild(select);
      const lock = document.createElement('button'); lock.type = 'button'; lock.textContent = value?.editorLocked ? 'Unlock' : 'Lock';
      lock.setAttribute('aria-label', `${value?.editorLocked ? 'Unlock' : 'Lock'} ${label}`);
      lock.addEventListener('click', () => {
        setSelected(el); checkpoint(); const current = selectedOverride(); if (!current) return;
        current.editorLocked = current.editorLocked !== true;
        applyElementOverride(el, current); markDirty(); syncEditorControls(); refreshLayers();
      });
      row.appendChild(lock);
      if (el.hidden) {
        const restore = document.createElement('button'); restore.type = 'button'; restore.textContent = 'Show';
        restore.setAttribute('aria-label', `Show ${label}`);
        restore.addEventListener('click', () => {
          setSelected(el); checkpoint(); const value = selectedOverride();
          if (!value) return; const variant = editableVariant(value); variant.hidden = false; applyElementOverride(el, value); markDirty(); syncEditorControls(); showPanel('layers');
        });
        row.appendChild(restore);
      }
      list.appendChild(row);
    });
    if (!list.children.length) { const empty = document.createElement('p'); empty.textContent = 'No matching content on this page.'; list.appendChild(empty); }
  }

  function setElementPlacement(el, placement) {
    const override = overrideForElement(el);
    if (!override) return null;
    override.placement = placement ? { ...placement } : null;
    return override;
  }

  function freeCanvasParent(parent) {
    if (!parent) return false;
    const override = overrideForElement(parent, false);
    return resolvedVariant(override).layout?.mode === 'free';
  }

  function groupSelection() {
    const items = selectionItems();
    if (items.length < 2 || !commonSelectionIsEditable()) return;
    const parent = commonSelectionParent();
    if (!parent) { alert('Select elements that share the same container before grouping.'); return; }
    const section = currentSectionFor(items[0]);
    if (!section || !items.every(item => currentSectionFor(item) === section)) {
      alert('A group must stay inside one section.');
      return;
    }

    const parentId = parent.dataset?.cmsId || null;
    const inLegacyFrame = parent.classList?.contains('cms-layout-frame');
    if (!parentId && !inLegacyFrame) {
      alert('Select elements inside a managed website container before grouping.');
      return;
    }

    const parentRect = parent.getBoundingClientRect();
    const rects = items.map(el => ({ el, rect: el.getBoundingClientRect() }));
    const left = Math.min(...rects.map(item => item.rect.left));
    const top = Math.min(...rects.map(item => item.rect.top));
    const right = Math.max(...rects.map(item => item.rect.right));
    const bottom = Math.max(...rects.map(item => item.rect.bottom));
    const unionWidth = Math.max(1, right - left);
    const unionHeight = Math.max(1, bottom - top);
    const parentFree = freeCanvasParent(parent);
    const siblings = [...parent.children];
    const selectedIndices = items.map(item => siblings.indexOf(item)).filter(index => index >= 0);
    const lastIndex = selectedIndices.length ? Math.max(...selectedIndices) : -1;
    const nextOutside = siblings.slice(lastIndex + 1).find(node => node.dataset?.cmsId && !selectedElements.has(node)) || null;
    const firstRecord = overrideForElement(items[0], false);
    const existingPlacement = firstRecord?.placement || null;

    checkpoint();
    const id = crypto.randomUUID();
    const group = {
      id,
      type: 'group',
      sectionId: section.dataset.cmsSection,
      editorLabel: 'Group',
      style: {},
      layout: { mode: parentFree ? 'free' : 'flow' },
      placement: inLegacyFrame
        ? {
            sectionId: section.dataset.cmsSection,
            beforeId: nextOutside?.dataset.cmsId || null,
            containerId: null,
            flow: false,
            column: existingPlacement?.column || 1,
            span: existingPlacement?.span || 12
          }
        : {
            sectionId: section.dataset.cmsSection,
            beforeId: nextOutside?.dataset.cmsId || null,
            containerId: parentId,
            flow: true,
            column: 1,
            span: 12
          }
    };

    if (parentFree) {
      group.style.positionMode = 'absolute';
      group.style.offsetXPercent = parentRect.width > 0 ? Math.round(((left - parentRect.left) / parentRect.width * 100) * 1000) / 1000 : 0;
      group.style.offsetYPx = Math.round((top - parentRect.top) * 1000) / 1000;
      group.style.widthPercent = parentRect.width > 0 ? Math.max(1, Math.round((unionWidth / parentRect.width * 100) * 1000) / 1000) : 100;
      group.style.heightPx = Math.max(24, Math.round(unionHeight * 1000) / 1000);
      group.style.minHeightPx = group.style.heightPx;
    }

    pageState().extras.push(group);
    const groupEl = createExtra(group);
    if (!groupEl) return;
    applyPlacement(groupEl, group.placement);

    for (const { el, rect } of rects) {
      const override = setElementPlacement(el, {
        sectionId: section.dataset.cmsSection,
        beforeId: null,
        containerId: `extra:${id}`,
        flow: true,
        column: 1,
        span: 12
      });
      if (!override) continue;
      if (parentFree) {
        const variant = editableVariant(override);
        variant.style ||= {};
        variant.style.positionMode = 'absolute';
        variant.style.offsetXPercent = Math.round(((rect.left - left) / unionWidth * 100) * 1000) / 1000;
        variant.style.offsetYPx = Math.round((rect.top - top) * 1000) / 1000;
        variant.style.widthPercent = Math.max(1, Math.round((rect.width / unionWidth * 100) * 1000) / 1000);
      }
      applyPlacement(el, override.placement);
      applyElementOverride(el, override);
    }

    setSelected(groupEl);
    markDirty();
  }

  function ungroupSelected() {
    if (!selected?.dataset.cmsExtraId) return;
    const group = pageState().extras.find(item => item.id === selected.dataset.cmsExtraId);
    if (group?.type !== 'group') return;
    const groupEl = selected;
    const destinationParent = groupEl.parentElement;
    const children = [...groupEl.children].filter(node => node.dataset?.cmsEditable === 'true');
    if (!destinationParent || !children.length) return;

    const parentRect = destinationParent.getBoundingClientRect();
    const childRects = children.map(el => ({ el, rect: el.getBoundingClientRect() }));
    const parentFree = freeCanvasParent(destinationParent);
    const destination = group.placement || {
      sectionId: group.sectionId,
      beforeId: null,
      containerId: destinationParent.dataset?.cmsId || null,
      flow: !!destinationParent.dataset?.cmsId,
      column: 1,
      span: 12
    };

    checkpoint();
    for (const { el, rect } of childRects) {
      const override = setElementPlacement(el, {
        ...destination,
        beforeId: destination.beforeId || null
      });
      if (!override) continue;
      if (parentFree) {
        const variant = editableVariant(override);
        variant.style ||= {};
        variant.style.positionMode = 'absolute';
        variant.style.offsetXPercent = parentRect.width > 0 ? Math.round(((rect.left - parentRect.left) / parentRect.width * 100) * 1000) / 1000 : 0;
        variant.style.offsetYPx = Math.round((rect.top - parentRect.top) * 1000) / 1000;
        variant.style.widthPercent = parentRect.width > 0 ? Math.max(1, Math.round((rect.width / parentRect.width * 100) * 1000) / 1000) : 100;
      } else {
        const variant = editableVariant(override, false);
        if (variant?.style?.positionMode === 'absolute') {
          delete variant.style.positionMode;
          delete variant.style.offsetXPercent;
          delete variant.style.offsetYPx;
        }
      }
      applyPlacement(el, override.placement);
      applyElementOverride(el, override);
    }

    pageState().extras = pageState().extras.filter(item => item.id !== group.id);
    groupEl.remove();
    selectedElements.clear();
    children.forEach(el => selectedElements.add(el));
    selected = children.at(-1) || null;
    const sections = new Set(children.map(currentSectionFor).filter(Boolean));
    selectedSection = sections.size === 1 ? [...sections][0] : currentSectionFor(selected);
    refreshSelectionClasses();
    syncEditorControls();
    renderSignalControls();
    refreshLayers();
    updateDirectCanvasUi();
    markDirty();
  }

  function alignSelection(action) {
    const items = selectionItems();
    const parent = commonSelectionParent();
    if (items.length < 2 || !parent || !commonSelectionIsEditable()) return;
    if (!freeCanvasParent(parent)) {
      alert('Align and distribute are available inside a Free Canvas container. Change the parent container to Free Canvas first.');
      return;
    }
    const parentRect = parent.getBoundingClientRect();
    if (!parentRect.width) return;
    const rows = items.map(el => ({ el, rect: el.getBoundingClientRect() }));
    const left = Math.min(...rows.map(item => item.rect.left));
    const right = Math.max(...rows.map(item => item.rect.right));
    const top = Math.min(...rows.map(item => item.rect.top));
    const bottom = Math.max(...rows.map(item => item.rect.bottom));
    const centerX = (left + right) / 2;
    const centerY = (top + bottom) / 2;

    const targets = new Map(rows.map(item => [item.el, { left: item.rect.left, top: item.rect.top }]));
    if (action === 'left') rows.forEach(item => targets.get(item.el).left = left);
    if (action === 'right') rows.forEach(item => targets.get(item.el).left = right - item.rect.width);
    if (action === 'center-x') rows.forEach(item => targets.get(item.el).left = centerX - item.rect.width / 2);
    if (action === 'top') rows.forEach(item => targets.get(item.el).top = top);
    if (action === 'bottom') rows.forEach(item => targets.get(item.el).top = bottom - item.rect.height);
    if (action === 'center-y') rows.forEach(item => targets.get(item.el).top = centerY - item.rect.height / 2);

    if (action === 'distribute-x' && rows.length >= 3) {
      const sorted = [...rows].sort((a,b) => a.rect.left - b.rect.left);
      const total = sorted.reduce((sum,item) => sum + item.rect.width, 0);
      const gap = (right - left - total) / (sorted.length - 1);
      let cursor = left;
      sorted.forEach(item => { targets.get(item.el).left = cursor; cursor += item.rect.width + gap; });
    }
    if (action === 'distribute-y' && rows.length >= 3) {
      const sorted = [...rows].sort((a,b) => a.rect.top - b.rect.top);
      const total = sorted.reduce((sum,item) => sum + item.rect.height, 0);
      const gap = (bottom - top - total) / (sorted.length - 1);
      let cursor = top;
      sorted.forEach(item => { targets.get(item.el).top = cursor; cursor += item.rect.height + gap; });
    }

    checkpoint();
    for (const item of rows) {
      const override = overrideForElement(item.el);
      if (!override) continue;
      const variant = editableVariant(override);
      variant.style ||= {};
      variant.style.positionMode = 'absolute';
      variant.style.offsetXPercent = Math.round(((targets.get(item.el).left - parentRect.left) / parentRect.width * 100) * 1000) / 1000;
      variant.style.offsetYPx = Math.round((targets.get(item.el).top - parentRect.top) * 1000) / 1000;
      applyElementOverride(item.el, override);
    }
    updateDirectCanvasUi();
    syncEditorControls();
    markDirty();
  }

  function syncMultiSelectionControls() {
    const items = selectionItems();
    const parent = commonSelectionParent();
    const editable = commonSelectionIsEditable();
    const group = document.getElementById('legend-cms-group-selection');
    const ungroup = document.getElementById('legend-cms-ungroup-selection');
    const saveReusable = document.getElementById('legend-cms-save-reusable');
    const detachReusable = document.getElementById('legend-cms-detach-reusable');
    if (group) group.disabled = !(items.length >= 2 && parent && editable);
    const selectedExtra = selected?.dataset.cmsExtraId ? pageState().extras.find(item => item.id === selected.dataset.cmsExtraId) : null;
    if (ungroup) ungroup.disabled = !(items.length === 1 && selectedExtra?.type === 'group');
    if (saveReusable) saveReusable.disabled = !(items.length === 1 && ['group','container'].includes(selectedExtra?.type) && !selected?.dataset.cmsReusableDefinitionId);
    const reusableWrapper = selected?.closest?.('.cms-extra-reusable');
    if (detachReusable) detachReusable.disabled = !(items.length === 1 && (selectedExtra?.type === 'reusable' || reusableWrapper?.dataset.cmsExtraId));
    const canAlign = items.length >= 2 && parent && editable && freeCanvasParent(parent);
    document.querySelectorAll('[data-align-selection]').forEach(button => {
      const distribute = button.dataset.alignSelection?.startsWith('distribute');
      button.disabled = !canAlign || (distribute && items.length < 3);
    });
    document.querySelectorAll('[data-z-action]').forEach(button => { button.disabled = items.length !== 1; });
  }

  function selectSiblingLayers() {
    if (!selected) return false;
    const parent = selected.parentElement;
    if (!parent) return false;
    const siblings = [...parent.children].filter(el =>
      el.dataset?.cmsEditable === 'true' &&
      el.dataset.cmsLocked !== 'true' &&
      !el.hidden &&
      !el.dataset.cmsSignalOnly
    );
    if (!siblings.length) return false;
    selectedElements.clear();
    siblings.forEach(el => selectedElements.add(el));
    selected = siblings.at(-1) || null;
    selectedSection = currentSectionFor(selected);
    refreshSelectionClasses();
    syncEditorControls();
    renderSignalControls();
    refreshLayers();
    updateDirectCanvasUi();
    return true;
  }

  function nudgeSelection(dx, dy) {
    const items = selectionItems();
    if (!items.length || !commonSelectionIsEditable()) return false;
    const parent = items.length > 1 ? commonSelectionParent() : items[0].parentElement;
    if (!parent || !items.every(el => el.parentElement === parent) || !freeCanvasParent(parent)) return false;
    const parentRect = parent.getBoundingClientRect();
    if (!parentRect.width) return false;
    checkpoint();
    for (const el of items) {
      const rect = el.getBoundingClientRect();
      const override = overrideForElement(el);
      if (!override) continue;
      const variant = editableVariant(override);
      variant.style ||= {};
      variant.style.positionMode = 'absolute';
      variant.style.offsetXPercent = Math.round(((rect.left - parentRect.left + dx) / parentRect.width * 100) * 1000) / 1000;
      variant.style.offsetYPx = Math.round((rect.top - parentRect.top + dy) * 1000) / 1000;
      applyElementOverride(el, override);
    }
    updateDirectCanvasUi();
    syncEditorControls();
    markDirty();
    return true;
  }

  function adjustSelectedZ(action) {
    if (!selected) return;
    const parent = selected.parentElement;
    if (!parent) return;
    const siblings = [...parent.children].filter(node => node.dataset?.cmsEditable === 'true');
    const values = siblings.map(node => {
      const ov = overrideForElement(node, false);
      return Number(resolvedVariant(ov).style?.zIndex) || 0;
    });
    const base = selectedOverride();
    if (!base) return;
    const variant = editableVariant(base);
    variant.style ||= {};
    const current = Number(resolvedVariant(base).style?.zIndex) || 0;
    checkpoint();
    if (action === 'front') variant.style.zIndex = Math.min(10000, Math.max(0, ...values) + 1);
    else if (action === 'back') variant.style.zIndex = Math.max(-10000, Math.min(0, ...values) - 1);
    else if (action === 'forward') variant.style.zIndex = Math.min(10000, current + 1);
    else if (action === 'backward') variant.style.zIndex = Math.max(-10000, current - 1);
    applyElementOverride(selected, base);
    syncEditorControls();
    markDirty();
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
      const sourceExtra = selected.dataset.cmsExtraId ? pageState().extras.find(x => x.id === selected.dataset.cmsExtraId) : null;
      if (sourceExtra?.type === 'code') {
        checkpoint();
        const copy = JSON.parse(JSON.stringify(sourceExtra));
        copy.id = crypto.randomUUID();
        copy.sectionId = selectedSection.dataset.cmsSection;
        copy.signals = [];
        pageState().extras.push(copy);
        const created = createExtra(copy);
        if (copy.placement) applyPlacement(created, copy.placement);
        setSelected(created);
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
    for (const key of ['fontFamily','headingFontFamily']) {
      const fontInput = panel.querySelector(`[data-theme-key="${key}"]`);
      if (!fontInput?.replaceWith) continue;
      const select = document.createElement('select'); select.dataset.themeKey = key;
      for (const value of ['inherit','system-ui','serif','sans-serif','monospace','Georgia','Arial']) {
        const option = document.createElement('option'); option.value = value; option.textContent = value; select.appendChild(option);
      }
      fontInput.replaceWith(select);
    }
    document.addEventListener('keydown', event => {
      const editingText = event.target.closest?.('input,textarea,select,[contenteditable="true"]');
      const command = (event.ctrlKey || event.metaKey) && !event.altKey;
      const key = event.key.toLowerCase();

      if (command && key === 's') { event.preventDefault(); chooseDraft(); return; }
      if (editingText) return;

      if (event.key === 'Escape') { if (selectionItems().length) { event.preventDefault(); setSelected(null); } return; }
      if (command && key === 'a') { if (selectSiblingLayers()) event.preventDefault(); return; }
      if (command && key === 'g') {
        event.preventDefault();
        if (event.shiftKey) ungroupSelected();
        else groupSelection();
        return;
      }
      if (command && (key === 'z' || key === 'y')) {
        event.preventDefault();
        if (key === 'y' || event.shiftKey) restoreHistory(redoStack, undoStack);
        else restoreHistory(undoStack, redoStack);
        return;
      }
      if (event.key === 'Delete' || event.key === 'Backspace') {
        if (selectionItems().length) { event.preventDefault(); deleteSelection(); }
        return;
      }
      const arrows = { ArrowLeft: [-1,0], ArrowRight: [1,0], ArrowUp: [0,-1], ArrowDown: [0,1] };
      const delta = arrows[event.key];
      if (delta) {
        const step = event.shiftKey ? 10 : 1;
        if (nudgeSelection(delta[0] * step, delta[1] * step)) event.preventDefault();
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

  function openCodeEditor() {
    const block = selected;
    const extra = overrideForElement(block, false);
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
      syncReusableMemberDom(block, extra);
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

  const reusableInstanceGeometryKeys = new Set([
    'widthPercent','heightPx','offsetXPercent','offsetYPx',
    'minWidthPx','maxWidthPx','minHeightPx','maxHeightPx',
    'marginTop','marginRight','marginBottom','marginLeft',
    'zIndex','positionMode','horizontalAnchor','verticalAnchor',
    'insetLeftPx','insetRightPx','insetTopPx','insetBottomPx','aspectRatio'
  ]);

  function cloneValue(value) {
    return value == null ? value : JSON.parse(JSON.stringify(value));
  }

  function splitReusableStyle(style) {
    const shared = {}, instance = {};
    for (const [key, value] of Object.entries(style || {})) {
      (reusableInstanceGeometryKeys.has(key) ? instance : shared)[key] = cloneValue(value);
    }
    return { shared, instance };
  }

  function splitReusableResponsive(responsive) {
    const shared = {}, instance = {};
    for (const [breakpoint, variant] of Object.entries(responsive || {})) {
      const split = splitReusableStyle(variant?.style);
      const sharedVariant = { style: split.shared, layout: cloneValue(variant?.layout || {}) };
      const instanceVariant = { style: split.instance, layout: {} };
      if (variant?.hidden !== undefined) instanceVariant.hidden = variant.hidden;
      if (Object.keys(sharedVariant.style).length || Object.keys(sharedVariant.layout).length) shared[breakpoint] = sharedVariant;
      if (Object.keys(instanceVariant.style).length || instanceVariant.hidden !== undefined) instance[breakpoint] = instanceVariant;
    }
    return { shared, instance };
  }

  function extraDescendantIds(rootId) {
    const result = new Set();
    const queue = [`extra:${rootId}`];
    while (queue.length) {
      const containerId = queue.shift();
      for (const extra of pageState().extras) {
        if (result.has(extra.id) || extra.placement?.containerId !== containerId) continue;
        result.add(extra.id);
        queue.push(`extra:${extra.id}`);
      }
    }
    return result;
  }

  function hasBaselineDescendants(rootId, extraIds) {
    const validContainers = new Set([`extra:${rootId}`, ...[...extraIds].map(id => `extra:${id}`)]);
    return Object.values(pageState().elements).some(override => validContainers.has(override?.placement?.containerId));
  }

  function cloneDefinitionComponents(rootId, descendantIds) {
    const source = pageState().extras.filter(extra => descendantIds.has(extra.id));
    const idMap = new Map(source.map(extra => [extra.id, crypto.randomUUID()]));
    return source.map(extra => {
      const copy = cloneValue(extra);
      copy.id = idMap.get(extra.id);
      copy.sectionId = '__reusable__';
      delete copy.reusableDefinitionId;
      copy.placement ||= {};
      copy.placement.sectionId = '__reusable__';
      const oldContainer = copy.placement.containerId;
      if (oldContainer === `extra:${rootId}`) copy.placement.containerId = null;
      else if (oldContainer?.startsWith('extra:')) {
        const mapped = idMap.get(oldContainer.slice('extra:'.length));
        copy.placement.containerId = mapped ? `extra:${mapped}` : null;
      }
      if (copy.placement.beforeId) {
        const before = reusableLocalId(copy.placement.beforeId);
        const mappedBefore = idMap.get(before);
        copy.placement.beforeId = mappedBefore ? `extra:${mappedBefore}` : null;
      }
      return copy;
    });
  }

  function openSaveReusableDialog() {
    const rootEl = selected;
    const root = rootEl?.dataset.cmsExtraId ? pageState().extras.find(item => item.id === rootEl.dataset.cmsExtraId) : null;
    if (!root || !['group','container'].includes(root.type)) {
      alert('Select an added Group or Container to save it as a synced component.');
      return;
    }
    const descendantIds = extraDescendantIds(root.id);
    if (hasBaselineDescendants(root.id, descendantIds)) {
      alert('This container includes template-owned content. Convert or duplicate that content into added components before saving it as a synced component.');
      return;
    }
    if (document.getElementById('legend-cms-reusable-dialog')) return;
    clearTimeout(autoSaveTimer);

    const dialog = document.createElement('dialog');
    dialog.id = 'legend-cms-reusable-dialog';
    dialog.className = 'legend-cms-editor legend-cms-draft-dialog';
    const title = document.createElement('h2'); title.textContent = 'Save synced component';
    const help = document.createElement('p'); help.textContent = 'Content, design, layout, motion, and signals stay synced. Each inserted instance keeps its own page placement and size.';
    const label = document.createElement('label'); label.textContent = 'Component name';
    const input = document.createElement('input'); input.type = 'text'; input.maxLength = 100; input.required = true; input.value = root.editorLabel || 'Reusable component'; label.appendChild(input);
    const feedback = document.createElement('p'); feedback.setAttribute('role','status');
    const actions = document.createElement('div'); actions.className = 'legend-cms-code-actions';
    const saveButton = document.createElement('button'); saveButton.type='button'; saveButton.textContent='Save synced component';
    const cancel = document.createElement('button'); cancel.type='button'; cancel.textContent='Cancel'; cancel.addEventListener('click',()=>dialog.close());
    saveButton.addEventListener('click',()=>{
      const name=input.value.trim();
      if(!name){input.reportValidity();return;}
      const ok=saveSelectedAsReusable(name, descendantIds);
      if(ok)dialog.close(); else feedback.textContent='This selection could not be converted safely.';
    });
    actions.append(saveButton,cancel);
    dialog.append(title,help,label,feedback,actions);
    dialog.addEventListener('close',()=>{dialog.remove();if(dirty)autoSaveTimer=setTimeout(()=>save(false),900);});
    document.body.appendChild(dialog);dialog.showModal();input.focus();
  }

  function saveSelectedAsReusable(name, knownDescendantIds = null) {
    const rootEl = selected;
    const root = rootEl?.dataset.cmsExtraId ? pageState().extras.find(item => item.id === rootEl.dataset.cmsExtraId) : null;
    if (!root || !['group','container'].includes(root.type)) return false;
    const descendantIds = knownDescendantIds || extraDescendantIds(root.id);
    if (hasBaselineDescendants(root.id, descendantIds)) return false;
    const unsupported = pageState().extras.some(item => descendantIds.has(item.id) && ['reusable','section'].includes(item.type));
    if (unsupported) {
      alert('A synced component cannot contain another synced component or a page Section. Detach/nest ordinary Containers instead.');
      return false;
    }

    checkpoint();
    const definitionId = crypto.randomUUID();
    const split = splitReusableStyle(root.style);
    const responsive = splitReusableResponsive(root.responsive);
    const definition = {
      id: definitionId,
      name: String(name || 'Reusable component').trim().slice(0,100),
      rootType: root.type,
      signals: cloneValue(root.signals || []),
      interactions: cloneValue(root.interactions || []),
      style: split.shared,
      layout: cloneValue(root.layout || {}),
      responsive: responsive.shared,
      components: cloneDefinitionComponents(root.id, descendantIds),
      updatedUtc: new Date().toISOString()
    };
    documentState.reusableComponents ||= {};
    documentState.reusableComponents[definitionId] = definition;

    const instance = {
      id: root.id,
      type: 'reusable',
      reusableDefinitionId: definitionId,
      sectionId: root.sectionId,
      editorLocked: root.editorLocked,
      editorLabel: definition.name,
      placement: cloneValue(root.placement || null),
      style: split.instance,
      layout: {},
      responsive: responsive.instance,
      signals: [],
      interactions: []
    };
    const rootIndex = pageState().extras.findIndex(item => item.id === root.id);
    pageState().extras = pageState().extras.filter(item => !descendantIds.has(item.id));
    const replacementIndex = pageState().extras.findIndex(item => item.id === root.id);
    if (replacementIndex >= 0) pageState().extras[replacementIndex] = instance;
    else pageState().extras.splice(Math.max(0, rootIndex), 0, instance);

    applyDocument(documentState);
    renderComponentCatalog();
    const rebuilt = document.querySelector(`[data-cms-id="extra:${CSS.escape(instance.id)}"]`);
    setSelected(rebuilt);
    markDirty();
    return true;
  }

  function insertReusableComponent(definitionId) {
    const definition = reusableDefinition(definitionId);
    if (!definition) return;
    const section = selectedSection || document.querySelector('[data-cms-section]');
    if (!section) return;
    checkpoint();
    const extra = {
      id: crypto.randomUUID(),
      type: 'reusable',
      reusableDefinitionId: definitionId,
      sectionId: section.dataset.cmsSection,
      editorLabel: definition.name,
      style: {},
      layout: {},
      responsive: {},
      signals: [],
      interactions: []
    };
    const canUseSelectedContainer = selected && !selected.dataset.cmsReusableDefinitionId && !selected.closest?.('.cms-extra-reusable');
    const container = canUseSelectedContainer ? selectedFlowContainer(section) : null;
    if (container) extra.placement = { sectionId: section.dataset.cmsSection, containerId: container.dataset.cmsId, beforeId: null, flow: true, column: 1, span: 12 };
    pageState().extras.push(extra);
    const el=createExtra(extra);
    if(extra.placement)applyPlacement(el,extra.placement);
    setSelected(el);
    markDirty();
  }

  function mergeReusableResponsive(definitionResponsive, instanceResponsive) {
    const result = cloneValue(definitionResponsive || {});
    for (const [breakpoint, variant] of Object.entries(instanceResponsive || {})) {
      result[breakpoint] ||= { style:{}, layout:{} };
      result[breakpoint].style = { ...(result[breakpoint].style || {}), ...(variant?.style || {}) };
      result[breakpoint].layout = { ...(result[breakpoint].layout || {}), ...(variant?.layout || {}) };
      if (variant?.hidden !== undefined) result[breakpoint].hidden = variant.hidden;
    }
    return result;
  }

  function detachReusableInstance() {
    const instanceEl=selected?.dataset.cmsExtraId ? selected : selected?.closest?.('.cms-extra-reusable');
    const instance=instanceEl?.dataset.cmsExtraId ? pageState().extras.find(item=>item.id===instanceEl.dataset.cmsExtraId) : null;
    if(instance?.type!=='reusable')return;
    const definition=reusableDefinition(instance.reusableDefinitionId);
    if(!definition)return;
    checkpoint();

    const root={
      id:instance.id,
      type:['group','container'].includes(definition.rootType)?definition.rootType:'group',
      sectionId:instance.sectionId,
      editorLocked:instance.editorLocked,
      editorLabel:instance.editorLabel || definition.name,
      placement:cloneValue(instance.placement||null),
      style:{...(cloneValue(definition.style||{})),...(cloneValue(instance.style||{}))},
      layout:cloneValue(definition.layout||{}),
      responsive:mergeReusableResponsive(definition.responsive,instance.responsive),
      signals:cloneValue(definition.signals||[]),
      interactions:cloneValue(definition.interactions||[])
    };

    const idMap=new Map((definition.components||[]).map(component=>[component.id,crypto.randomUUID()]));
    const clones=(definition.components||[]).map(component=>{
      const copy=cloneValue(component);
      const oldId=component.id;
      copy.id=idMap.get(oldId);
      copy.sectionId=instance.sectionId;
      copy.placement=cloneValue(component.placement||{});
      copy.placement.sectionId=instance.sectionId;
      const oldContainer=copy.placement.containerId;
      if(!oldContainer) {
        copy.placement.containerId=`extra:${root.id}`;
        copy.placement.flow=true;
      } else if(oldContainer.startsWith('extra:')) {
        const mapped=idMap.get(oldContainer.slice('extra:'.length));
        copy.placement.containerId=mapped?`extra:${mapped}`:`extra:${root.id}`;
      }
      if(copy.placement.beforeId){
        const mappedBefore=idMap.get(reusableLocalId(copy.placement.beforeId));
        copy.placement.beforeId=mappedBefore?`extra:${mappedBefore}`:null;
      }
      return copy;
    });

    const index=pageState().extras.findIndex(item=>item.id===instance.id);
    if(index<0)return;
    pageState().extras.splice(index,1,root,...clones);
    applyDocument(documentState);
    const rebuilt=document.querySelector(`[data-cms-id="extra:${CSS.escape(root.id)}"]`);
    setSelected(rebuilt);
    markDirty();
  }

  function addBlock(type) {
    const section = selectedSection || document.querySelector('[data-cms-section]');
    if (!section && type !== 'section') return;
    const action = type === 'button' ? preferredCtaOption() : null;
    if (type === 'button' && !action) { alert('Configure a working website action before adding this button.'); return; }
    checkpoint();
    const defaultText = type === 'button' ? action.defaultText || action.label
      : type === 'text' ? 'Your text'
      : type === 'heading' ? 'Your heading'
      : type === 'quote' ? 'Your quote'
      : type === 'code' ? defaultCodeBlock
      : '';
    const defaultStyle = type === 'code' ? { widthPercent: 100, heightPx: 320 }
      : type === 'spacer' ? { widthPercent: 100, heightPx: 48 }
      : type === 'shape' ? { widthPercent: 25, heightPx: 120, borderRadius: 16 }
      : type === 'container' ? { widthPercent: 100, minHeightPx: 160 }
      : type === 'divider' ? { widthPercent: 100 }
      : {};
    const extra = {
      id: crypto.randomUUID(),
      type,
      sectionId: section?.dataset.cmsSection || `${pageKey}.root`,
      text: defaultText,
      style: defaultStyle,
      layout: type === 'container' ? { mode: 'flow' } : {}
    };
    if (type === 'button') {
      extra.href = action.href;
      extra.target = action.openInNewTab ? '_blank' : '_self';
      if (action.managed) extra.actionKey = action.key;
      const container = selectedFlowContainer(section);
      if (container) extra.placement = { sectionId: section.dataset.cmsSection, containerId: container.dataset.cmsId, beforeId: null, flow: true, column: 1, span: 12 };
    }
    pageState().extras.push(extra); const el = createExtra(extra); if (extra.placement) applyPlacement(el, extra.placement); setSelected(el); markDirty();
    if (type === 'code') openCodeEditor();
  }
  function renderComponentCatalog() {
    const host = document.getElementById('legend-cms-add-components');
    if (!host) return;
    host.replaceChildren();
    if (!Array.isArray(componentCatalog) || !componentCatalog.length) {
      const unavailable = document.createElement('p');
      unavailable.textContent = 'Component catalog unavailable. Reopen the editor.';
      host.appendChild(unavailable);
      return;
    }
    let group = '';
    for (const item of componentCatalog) {
      if (!item?.type || !item?.label || item.directAdd === false) continue;
      if (item.group && item.group !== group) {
        group = item.group;
        const heading = document.createElement('small');
        heading.className = 'legend-cms-component-group';
        heading.textContent = group;
        host.appendChild(heading);
      }
      const button = document.createElement('button');
      button.type = 'button';
      button.textContent = item.label;
      if (item.type === 'image') {
        button.id = 'legend-cms-new-image';
        button.addEventListener('click', () => document.getElementById('legend-cms-extra-image')?.click());
      } else {
        button.dataset.add = item.type;
        button.addEventListener('click', () => addBlock(item.type));
      }
      host.appendChild(button);
    }

    const definitions = Object.values(documentState.reusableComponents || {})
      .filter(definition => definition?.id && definition?.name)
      .sort((a,b) => String(a.name).localeCompare(String(b.name)));
    if (definitions.length) {
      const heading = document.createElement('small');
      heading.className = 'legend-cms-component-group';
      heading.textContent = 'Synced components';
      host.appendChild(heading);
      for (const definition of definitions) {
        const button = document.createElement('button');
        button.type = 'button';
        button.dataset.addReusable = definition.id;
        button.textContent = definition.name;
        button.title = 'Insert a synced instance. Shared edits update every instance; placement stays local.';
        button.addEventListener('click', () => insertReusableComponent(definition.id));
        host.appendChild(button);
      }
    }
  }

  function applyBreakpointPreview(preview) {
    if (!preview) return;
    const breakpoint = currentDesignBreakpoint === 'base' ? null : breakpointById(currentDesignBreakpoint);
    preview.style.width = breakpoint ? `min(100%, ${breakpoint.maxWidthPx}px)` : '100%';
    preview.style.maxWidth = breakpoint ? `${breakpoint.maxWidthPx}px` : '';
    preview.style.marginInline = breakpoint ? 'auto' : '';
    preview.dataset.cmsBreakpoint = breakpoint?.id || 'base';
    const note = document.getElementById('legend-cms-breakpoint-note');
    if (note) note.textContent = breakpoint
      ? `${breakpoint.label} · ≤ ${breakpoint.maxWidthPx}px · inherits larger layouts until you override them`
      : 'Desktop / base · changes become the default for every viewport unless a smaller breakpoint overrides them.';
  }

  function eachWebsiteOverride(callback) {
    const visitPage = page => {
      Object.values(page?.elements || {}).forEach(callback);
      (page?.extras || []).forEach(callback);
    };
    Object.values(documentState.elements || {}).forEach(callback);
    (documentState.extras || []).forEach(callback);
    Object.values(documentState.pages || {}).forEach(visitPage);
  }

  function removeBreakpointOverrides(id) {
    eachWebsiteOverride(override => {
      if (override?.responsive) delete override.responsive[id];
    });
  }

  function openBreakpointManager(preview) {
    if (document.getElementById('legend-cms-breakpoint-dialog')) return;
    clearTimeout(autoSaveTimer);
    const dialog = document.createElement('dialog');
    dialog.id = 'legend-cms-breakpoint-dialog';
    dialog.className = 'legend-cms-editor legend-cms-breakpoint-dialog';
    const title = document.createElement('h2'); title.textContent = 'Responsive breakpoints';
    const help = document.createElement('p'); help.textContent = 'Base is the desktop/default design. Smaller breakpoints inherit larger rules and only store what you change.';
    const rows = document.createElement('div'); rows.className = 'legend-cms-breakpoint-list';
    const working = (documentState.breakpoints || []).map(item => ({...item}));

    const renderRows = () => {
      rows.replaceChildren();
      working.sort((a,b) => b.maxWidthPx - a.maxWidthPx).forEach(item => {
        const row = document.createElement('div'); row.className = 'legend-cms-breakpoint-row';
        const label = document.createElement('input'); label.type = 'text'; label.maxLength = 80; label.value = item.label || item.id; label.setAttribute('aria-label','Breakpoint label');
        const width = document.createElement('input'); width.type = 'number'; width.min = '320'; width.max = '2560'; width.step = '1'; width.value = String(item.maxWidthPx); width.setAttribute('aria-label','Maximum viewport width');
        const remove = document.createElement('button'); remove.type = 'button'; remove.textContent = 'Remove'; remove.disabled = working.length <= 1;
        label.addEventListener('input', () => { item.label = label.value.slice(0,80); });
        width.addEventListener('input', () => { const value=Number(width.value); if(Number.isFinite(value)) item.maxWidthPx=Math.max(320,Math.min(2560,Math.round(value))); });
        remove.addEventListener('click', () => { const index=working.findIndex(x=>x.id===item.id); if(index>=0&&working.length>1){working.splice(index,1);renderRows();} });
        row.append(label,width,remove); rows.appendChild(row);
      });
    };
    renderRows();

    const add = document.createElement('button'); add.type='button'; add.textContent='Add breakpoint';
    add.disabled = working.length >= 6;
    add.addEventListener('click', () => {
      if (working.length >= 6) return;
      const id='custom-' + crypto.randomUUID().replaceAll('-','').slice(0,10);
      const previous=Math.min(...working.map(x=>Number(x.maxWidthPx)||1024));
      working.push({id,label:'Custom',maxWidthPx:Math.max(320,previous-120)});
      renderRows();
      add.disabled = working.length >= 6;
    });

    const actions=document.createElement('div');actions.className='legend-cms-code-actions';
    const saveButton=document.createElement('button');saveButton.type='button';saveButton.textContent='Save breakpoints';
    const cancel=document.createElement('button');cancel.type='button';cancel.textContent='Cancel';
    cancel.addEventListener('click',()=>dialog.close());
    saveButton.addEventListener('click',()=>{
      const cleaned=working
        .map(item=>({id:safeId(item.id),label:String(item.label||item.id).trim().slice(0,80)||item.id,maxWidthPx:Math.max(320,Math.min(2560,Math.round(Number(item.maxWidthPx)||640)))}))
        .filter(item=>item.id)
        .sort((a,b)=>b.maxWidthPx-a.maxWidthPx);
      if(!cleaned.length)return;
      checkpoint();
      const activeIds=new Set(cleaned.map(item=>item.id));
      for(const old of documentState.breakpoints||[])if(!activeIds.has(old.id))removeBreakpointOverrides(old.id);
      documentState.breakpoints=cleaned;
      if(currentDesignBreakpoint!=='base'&&!activeIds.has(currentDesignBreakpoint))currentDesignBreakpoint='base';
      markDirty();
      dialog.close();
      installBreakpointControls(preview);
      refreshResponsiveOverrides();
      syncEditorControls();
    });
    actions.append(saveButton,cancel);
    dialog.append(title,help,rows,add,actions);
    dialog.addEventListener('close',()=>{dialog.remove();if(dirty)autoSaveTimer=setTimeout(()=>save(false),900);});
    document.body.appendChild(dialog);
    dialog.showModal();
  }

  function installBreakpointControls(preview) {
    const select = document.getElementById('legend-cms-breakpoint');
    if (!select) return;
    select.replaceChildren();
    const base = document.createElement('option');
    base.value = 'base';
    base.textContent = 'Desktop / base';
    select.appendChild(base);
    for (const breakpoint of documentState.breakpoints || []) {
      const option = document.createElement('option');
      option.value = breakpoint.id;
      option.textContent = `${breakpoint.label} (≤ ${breakpoint.maxWidthPx}px)`;
      select.appendChild(option);
    }
    if (!['base', ...(documentState.breakpoints || []).map(item => item.id)].includes(currentDesignBreakpoint)) currentDesignBreakpoint = 'base';
    select.value = currentDesignBreakpoint;
    if (select.dataset.bound !== 'true') {
      select.dataset.bound = 'true';
      select.addEventListener('change', () => {
        currentDesignBreakpoint = select.value;
        applyBreakpointPreview(preview);
        refreshResponsiveOverrides();
        syncEditorControls();
        updateDirectCanvasUi();
      });
    }
    const manage = document.getElementById('legend-cms-manage-breakpoints');
    if (manage && manage.dataset.bound !== 'true') {
      manage.dataset.bound = 'true';
      manage.addEventListener('click', () => openBreakpointManager(preview));
    }
    applyBreakpointPreview(preview);
  }

  function selectedComponentCapability() {
    if (!selected || !Array.isArray(componentCatalog)) return null;
    let type = null;
    if (selected.dataset.cmsReusableDefinitionId) {
      const definition = reusableDefinition(selected.dataset.cmsReusableDefinitionId);
      if (selected.dataset.cmsReusableLocalId === '__root__') type = definition?.rootType || 'group';
      else type = reusableMember(selected.dataset.cmsReusableDefinitionId, selected.dataset.cmsReusableLocalId)?.type || null;
    }
    if (!type && selected.dataset.cmsExtraId) {
      type = pageState().extras.find(item => item.id === selected.dataset.cmsExtraId)?.type || null;
    }
    type ||= selected.dataset.cmsSection ? 'section'
      : selected instanceof HTMLImageElement ? 'image'
      : selected.tagName === 'VIDEO' ? 'video'
      : ['A','BUTTON'].includes(selected.tagName) ? 'button'
      : selected.tagName === 'ARTICLE' ? 'card'
      : ['DIV','HEADER','FOOTER'].includes(selected.tagName) ? 'container'
      : 'text';
    return componentCatalog.find(item => item.type === type) || null;
  }

  function syncLayoutControls(override) {
    const variant = editableVariant(override, false);
    const resolved = resolvedVariant(override);
    const own = variant?.layout || {};
    const layout = resolved.layout || {};
    const values = {
      'legend-cms-layout-mode': own.mode ?? layout.mode ?? 'flow',
      'legend-cms-layout-columns': own.columns ?? layout.columns ?? 12,
      'legend-cms-layout-rows': own.rows ?? layout.rows ?? '',
      'legend-cms-layout-column-gap': own.columnGap ?? layout.columnGap ?? '',
      'legend-cms-layout-row-gap': own.rowGap ?? layout.rowGap ?? '',
      'legend-cms-layout-direction': own.direction ?? layout.direction ?? 'row',
      'legend-cms-layout-align': own.alignItems ?? layout.alignItems ?? 'stretch',
      'legend-cms-layout-justify': own.justifyContent ?? layout.justifyContent ?? 'start',
      'legend-cms-layout-overflow': own.overflow ?? layout.overflow ?? 'visible'
    };
    for (const [id, value] of Object.entries(values)) {
      const control = document.getElementById(id);
      if (control) control.value = String(value);
    }
    const wrap = document.getElementById('legend-cms-layout-wrap');
    if (wrap) wrap.checked = own.wrap ?? layout.wrap ?? true;
    const capability = selectedComponentCapability();
    const modes = Array.isArray(capability?.layoutModes) && capability.layoutModes.length ? capability.layoutModes : ['flow'];
    const modeSelect = document.getElementById('legend-cms-layout-mode');
    if (modeSelect) {
      const current = own.mode ?? layout.mode ?? 'flow';
      modeSelect.replaceChildren();
      const labels = {flow:'Flow / template',grid:'Grid',flex:'Flex',stack:'Stack',free:'Free Canvas'};
      for (const mode of modes) {
        const option = document.createElement('option'); option.value = mode; option.textContent = labels[mode] || mode; modeSelect.appendChild(option);
      }
      modeSelect.value = modes.includes(current) ? current : modes[0];
    }
    const isContainer = capability?.canContainChildren === true;
    document.querySelectorAll('[data-layout-control]').forEach(control => { control.disabled = !isContainer; });
  }

  function updateGeometryFromControls(event) {
    if (!selected) return;
    const base = selectedOverride();
    if (!base) return;
    const variant = editableVariant(base);
    variant.style ||= {};
    const style = variant.style;
    const control = event.target;
    const key = control.dataset.geometryKey;
    if (!key) return;
    checkpoint();

    if (control.value === '') delete style[key];
    else if (['positionMode','horizontalAnchor','verticalAnchor'].includes(key)) style[key] = control.value;
    else {
      const value = Number(control.value);
      if (!Number.isFinite(value)) return;
      const signed = ['marginTop','marginRight','marginBottom','marginLeft','rotationDeg','zIndex','insetLeftPx','insetRightPx','insetTopPx','insetBottomPx'].includes(key);
      if (!signed && value < 0) return;
      if (key === 'opacity' && (value < 0 || value > 1)) return;
      if (['scaleX','scaleY','aspectRatio'].includes(key) && value <= 0) return;
      style[key] = key === 'zIndex' ? Math.trunc(value) : value;
    }
    applyElementOverride(selected, base);
    updateDirectCanvasUi();
    markDirty();
  }

  function updateLayoutFromControls(event) {
    if (!selected) return;
    const base = selectedOverride();
    if (!base) return;
    const variant = editableVariant(base);
    variant.layout ||= {};
    const layout = variant.layout;
    const control = event.target;
    checkpoint();
    const map = {
      'legend-cms-layout-mode': 'mode',
      'legend-cms-layout-columns': 'columns',
      'legend-cms-layout-rows': 'rows',
      'legend-cms-layout-column-gap': 'columnGap',
      'legend-cms-layout-row-gap': 'rowGap',
      'legend-cms-layout-direction': 'direction',
      'legend-cms-layout-align': 'alignItems',
      'legend-cms-layout-justify': 'justifyContent',
      'legend-cms-layout-overflow': 'overflow'
    };
    const key = map[control.id];
    if (control.id === 'legend-cms-layout-wrap') layout.wrap = control.checked;
    else if (key) {
      if (control.value === '') delete layout[key];
      else layout[key] = ['columns','rows','columnGap','rowGap'].includes(key) ? Number(control.value) : control.value;
    } else return;
    if (key === 'mode' && control.value === 'free') {
      variant.style ||= {};
      if (!positiveNumber(variant.style.heightPx) && !positiveNumber(variant.style.minHeightPx)) {
        variant.style.minHeightPx = Math.max(120, Math.round(selected.getBoundingClientRect?.().height || 320));
      }
    }
    applyElementOverride(selected, base);
    updateDirectCanvasUi();
    markDirty();
  }

  function enhanceEditor(panel, preview) {
    document.querySelectorAll('[data-cms-id]').forEach(el => baselineNodes.set(el.dataset.cmsId, { el, parent: el.parentElement, next: el.nextSibling }));
    const content = document.createElement('div'); content.dataset.cmsView = 'content';
    Array.from(panel.children).filter(el => !el.classList.contains('legend-cms-bar')).forEach(el => content.appendChild(el));
    panel.appendChild(content);
    const navigation = document.createElement('nav');
    navigation.className = 'legend-cms-navigation'; navigation.setAttribute('aria-label', 'Website editing tools');
    navigation.innerHTML = `<div class="legend-cms-tabs"><button type="button" data-open="content">Content</button><button type="button" data-open="add">Add blocks</button><button type="button" data-open="media">Media</button><button type="button" data-open="appearance">Design</button><button type="button" data-open="layout">Position</button><button type="button" data-open="motion">Motion</button><button type="button" data-open="layers">Layers</button><button type="button" data-open="theme">Site theme</button><button type="button" data-open="page">Page & SEO</button><button type="button" data-open="quality">Quality</button></div>`;
    panel.insertBefore(navigation, content);
    const tools = document.createElement('div'); tools.innerHTML = `
      <section data-cms-view="add" hidden><h2>Add a component</h2><p>The available components come from the shared server capability registry. Add one, then position and resize it directly on the page.</p><div id="legend-cms-add-components" class="legend-cms-menu"></div></section>
      <section data-cms-view="media" hidden><h2>Media library</h2><p>Browse and reuse media already owned by this website. Uploads continue through the existing scoped media authority.</p><label class="legend-cms-group">Find media<input id="legend-cms-media-search" type="search" placeholder="Search file name or type"></label><label class="legend-cms-group">Upload media<input id="legend-cms-media-upload" type="file" accept="image/jpeg,image/png,image/webp,video/mp4,video/webm"></label><p id="legend-cms-media-status" role="status" aria-live="polite"></p><div id="legend-cms-media-grid" class="legend-cms-media-grid"></div></section>
      <section data-cms-view="appearance" hidden><h2>Appearance</h2>${appearanceFields()}<button id="legend-cms-container">Select section container</button></section>
      <section data-cms-view="motion" hidden><h2>Motion & interactions</h2><p>Motion is stored with the selected element and uses the shared runtime. It never requires page-specific animation code.</p><div id="legend-cms-motion-controls"></div></section>
      <section data-cms-view="layout" hidden><h2>Position & layout</h2><p>Move and resize on the canvas. For containers, choose Flow, Grid, Flex, Stack, or Free Canvas here. Layout changes are saved at the active breakpoint.</p><div class="legend-cms-row"><label class="legend-cms-group">X offset %<input id="legend-cms-offset-x" type="number" step="any" value="0"></label><label class="legend-cms-group">Y offset px<input id="legend-cms-offset-y" type="number" step="any" value="0"></label></div><div class="legend-cms-row"><label class="legend-cms-group">Min width px<input data-geometry-key="minWidthPx" type="number" min="0" max="10000" step="any" placeholder="None"></label><label class="legend-cms-group">Max width px<input data-geometry-key="maxWidthPx" type="number" min="0" max="10000" step="any" placeholder="None"></label></div><div class="legend-cms-row"><label class="legend-cms-group">Min height px<input data-geometry-key="minHeightPx" type="number" min="0" max="10000" step="any" placeholder="None"></label><label class="legend-cms-group">Max height px<input data-geometry-key="maxHeightPx" type="number" min="0" max="10000" step="any" placeholder="None"></label></div><div class="legend-cms-row"><label class="legend-cms-group">Aspect ratio<input data-geometry-key="aspectRatio" type="number" min="0.05" max="20" step="any" placeholder="Auto"></label><label class="legend-cms-group">Opacity<input data-geometry-key="opacity" type="number" min="0" max="1" step="0.01" placeholder="1"></label></div><div class="legend-cms-row"><label class="legend-cms-group">Rotate °<input data-geometry-key="rotationDeg" type="number" min="-3600" max="3600" step="any" placeholder="0"></label><label class="legend-cms-group">Layer / z-index<input data-geometry-key="zIndex" type="number" min="-10000" max="10000" step="1" placeholder="Auto"></label></div><div class="legend-cms-row"><label class="legend-cms-group">Scale X<input data-geometry-key="scaleX" type="number" min="0.01" max="20" step="any" placeholder="1"></label><label class="legend-cms-group">Scale Y<input data-geometry-key="scaleY" type="number" min="0.01" max="20" step="any" placeholder="1"></label></div><label class="legend-cms-group">Position mode<select data-geometry-key="positionMode"><option value="">Automatic</option><option value="flow">Flow</option><option value="relative">Relative</option><option value="absolute">Absolute</option><option value="sticky">Sticky</option><option value="fixed">Fixed to viewport</option></select></label><div class="legend-cms-row"><label class="legend-cms-group">Horizontal anchor<select data-geometry-key="horizontalAnchor"><option value="">None</option><option value="left">Left</option><option value="center">Center</option><option value="right">Right</option><option value="stretch">Stretch</option></select></label><label class="legend-cms-group">Vertical anchor<select data-geometry-key="verticalAnchor"><option value="">None</option><option value="top">Top</option><option value="center">Center</option><option value="bottom">Bottom</option><option value="stretch">Stretch</option></select></label></div><div class="legend-cms-row"><label class="legend-cms-group">Left inset px<input data-geometry-key="insetLeftPx" type="number" min="-10000" max="10000" step="any"></label><label class="legend-cms-group">Right inset px<input data-geometry-key="insetRightPx" type="number" min="-10000" max="10000" step="any"></label></div><div class="legend-cms-row"><label class="legend-cms-group">Top inset px<input data-geometry-key="insetTopPx" type="number" min="-10000" max="10000" step="any"></label><label class="legend-cms-group">Bottom inset px<input data-geometry-key="insetBottomPx" type="number" min="-10000" max="10000" step="any"></label></div><div class="legend-cms-row"><label class="legend-cms-group">Margin top<input data-geometry-key="marginTop" type="number" min="-2000" max="2000" step="any"></label><label class="legend-cms-group">Margin right<input data-geometry-key="marginRight" type="number" min="-2000" max="2000" step="any"></label></div><div class="legend-cms-row"><label class="legend-cms-group">Margin bottom<input data-geometry-key="marginBottom" type="number" min="-2000" max="2000" step="any"></label><label class="legend-cms-group">Margin left<input data-geometry-key="marginLeft" type="number" min="-2000" max="2000" step="any"></label></div><label class="legend-cms-group">Container layout<select id="legend-cms-layout-mode" data-layout-control></select></label><div class="legend-cms-row"><label class="legend-cms-group">Columns<input id="legend-cms-layout-columns" data-layout-control type="number" min="1" max="24" value="12"></label><label class="legend-cms-group">Rows<input id="legend-cms-layout-rows" data-layout-control type="number" min="1" max="24" placeholder="Auto"></label></div><div class="legend-cms-row"><label class="legend-cms-group">Column gap<input id="legend-cms-layout-column-gap" data-layout-control type="number" min="0" max="500" step="any"></label><label class="legend-cms-group">Row gap<input id="legend-cms-layout-row-gap" data-layout-control type="number" min="0" max="500" step="any"></label></div><label class="legend-cms-group">Direction<select id="legend-cms-layout-direction" data-layout-control><option value="row">Row</option><option value="column">Column</option></select></label><label class="legend-cms-group">Align items<select id="legend-cms-layout-align" data-layout-control><option value="start">Start</option><option value="center">Center</option><option value="end">End</option><option value="stretch">Stretch</option><option value="baseline">Baseline</option></select></label><label class="legend-cms-group">Distribute<select id="legend-cms-layout-justify" data-layout-control><option value="start">Start</option><option value="center">Center</option><option value="end">End</option><option value="space-between">Space between</option><option value="space-around">Space around</option><option value="space-evenly">Space evenly</option></select></label><label><input id="legend-cms-layout-wrap" data-layout-control type="checkbox" checked> Wrap children</label><label class="legend-cms-group">Overflow<select id="legend-cms-layout-overflow" data-layout-control><option value="visible">Visible</option><option value="hidden">Hidden</option><option value="clip">Clip</option><option value="auto">Auto</option><option value="scroll">Scroll</option></select></label><div class="legend-cms-group"><strong>Selection</strong><div class="legend-cms-row"><button id="legend-cms-group-selection" type="button" disabled>Group selection</button><button id="legend-cms-ungroup-selection" type="button" disabled>Ungroup</button></div><div class="legend-cms-row"><button id="legend-cms-save-reusable" type="button" disabled>Save as synced</button><button id="legend-cms-detach-reusable" type="button" disabled>Detach synced</button></div><div class="legend-cms-align-grid"><button type="button" data-align-selection="left" disabled>Align left</button><button type="button" data-align-selection="center-x" disabled>Center horizontally</button><button type="button" data-align-selection="right" disabled>Align right</button><button type="button" data-align-selection="top" disabled>Align top</button><button type="button" data-align-selection="center-y" disabled>Center vertically</button><button type="button" data-align-selection="bottom" disabled>Align bottom</button><button type="button" data-align-selection="distribute-x" disabled>Distribute horizontally</button><button type="button" data-align-selection="distribute-y" disabled>Distribute vertically</button></div><small>Align and distribute require elements inside the same Free Canvas container. Grouping works in any shared managed container.</small></div><div class="legend-cms-row legend-cms-z-actions"><button type="button" data-z-action="front">Bring to front</button><button type="button" data-z-action="back">Send to back</button><button type="button" data-z-action="forward">Bring forward</button><button type="button" data-z-action="backward">Send backward</button></div><button id="legend-cms-undo">Undo</button><button id="legend-cms-redo">Redo</button></section>
      <section data-cms-view="layers" hidden><h2>Page layers</h2><p>Select, find, or restore content—even when it is hidden.</p><label class="legend-cms-group">Find content<input id="legend-cms-layer-search" type="search" placeholder="Search this page"></label><div id="legend-cms-layers" class="legend-cms-layer-list"></div></section>
      <section data-cms-view="page" hidden><h2>Page & search appearance</h2><p>Saved with this page's draft and applied on publication.</p><label class="legend-cms-group">Page title<input id="legend-cms-page-title" type="text" maxlength="200"></label><label class="legend-cms-group">Search description<textarea id="legend-cms-page-description" rows="4" maxlength="500"></textarea></label><div class="legend-cms-search-preview"><strong id="legend-cms-search-title"></strong><p id="legend-cms-search-description"></p></div></section>
      <section data-cms-view="quality" hidden><h2>SEO, accessibility & performance</h2><p>Saved draft checks come from the server authority. Live checks inspect the current rendered canvas and never override publish authority.</p><div class="legend-cms-quality-summary"><strong>Saved draft checks</strong><span id="legend-cms-quality-server-summary"></span></div><div id="legend-cms-quality-server" class="legend-cms-quality-list"></div><div class="legend-cms-quality-summary"><strong>Live page checks</strong><span id="legend-cms-quality-live-summary"></span><button id="legend-cms-quality-refresh" type="button">Run live checks</button></div><div id="legend-cms-quality-live" class="legend-cms-quality-list"></div></section>
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
    const links = document.createElement('div'); links.innerHTML = `<div id="legend-cms-link-group" class="legend-cms-group" hidden><label for="legend-cms-action">Button action</label><select id="legend-cms-action"></select><small>Select a working action already connected to this website. You can edit the button wording in Content at any time.</small><div id="legend-cms-custom-link"><label for="legend-cms-href">Custom destination</label><input id="legend-cms-href" type="url" placeholder="https://…"></div><label><input id="legend-cms-target" type="checkbox"> Open in a new tab</label></div><div id="legend-cms-video-group" class="legend-cms-group" hidden><label for="legend-cms-videoUrl">HTTPS video URL</label><input id="legend-cms-videoUrl" type="url"><label for="legend-cms-video-file">Upload video</label><input id="legend-cms-video-file" type="file" accept="video/mp4,video/webm"></div>`;
    content.appendChild(links);
    panel.querySelectorAll('[data-open]').forEach(button => button.addEventListener('click', () => showPanel(button.dataset.open)));
    renderComponentCatalog();
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
    document.getElementById('legend-cms-edit-code')?.addEventListener('click', openCodeEditor);
    document.getElementById('legend-cms-quality-refresh')?.addEventListener('click', renderQualityPanel);
    document.getElementById('legend-cms-media-search')?.addEventListener('input', renderMediaLibrary);
    document.getElementById('legend-cms-media-upload')?.addEventListener('change', async event => {
      const file = event.target.files?.[0];
      if (!file) return;
      const url = await uploadMedia(file);
      event.target.value = '';
      if (url) await loadMediaLibrary();
    });
    document.getElementById('legend-cms-group-selection')?.addEventListener('click', groupSelection);
    document.getElementById('legend-cms-ungroup-selection')?.addEventListener('click', ungroupSelected);
    document.getElementById('legend-cms-save-reusable')?.addEventListener('click', openSaveReusableDialog);
    document.getElementById('legend-cms-detach-reusable')?.addEventListener('click', detachReusableInstance);
    document.querySelectorAll('[data-align-selection]').forEach(button => button.addEventListener('click', () => alignSelection(button.dataset.alignSelection)));
    document.getElementById('legend-cms-container').addEventListener('click', () => { if (selectedSection) setSelected(selectedSection); });
    panel.querySelectorAll('[data-style-key]').forEach(input => input.addEventListener('input', () => {
      if (!selected) return;
      const value = input.type === 'number' || input.dataset.styleKey === 'fontWeight' ? Number(input.value) : input.value;
      if (input.type === 'number' && input.value !== '' && (!Number.isFinite(value) || (input.dataset.styleKey !== 'letterSpacing' && value < 0) || (['fontSize','lineHeight'].includes(input.dataset.styleKey) && value === 0))) return;
      checkpoint();
      const ov = selectedOverride();
      const variant = editableVariant(ov);
      variant.style ||= {};
      if (input.value === '') delete variant.style[input.dataset.styleKey];
      else variant.style[input.dataset.styleKey] = value;
      applyElementOverride(selected, ov);
      markDirty();
    }));
    panel.querySelectorAll('[data-color-hex]').forEach(input => input.addEventListener('change', () => {
      if (!selected) return;
      if (!/^#[a-f0-9]{6}$/i.test(input.value)) { input.setCustomValidity('Enter a six-digit hex color, such as #000000.'); input.reportValidity(); return; }
      input.setCustomValidity(''); checkpoint(); const ov = selectedOverride(); const variant = editableVariant(ov); variant.style ||= {}; variant.style[input.dataset.colorHex] = input.value.toLowerCase();
      applyElementOverride(selected, ov); syncEditorControls(); markDirty();
    }));
    panel.querySelectorAll('[data-color-reset]').forEach(button => button.addEventListener('click', () => {
      if (!selected) return; checkpoint(); const ov = selectedOverride(); const variant = editableVariant(ov); if (variant.style) delete variant.style[button.dataset.colorReset];
      applyElementOverride(selected, ov); syncEditorControls(); markDirty();
    }));
    ['href','videoUrl','alt'].forEach(key => document.getElementById(`legend-cms-${key}`).addEventListener('input', event => { if (!selected) return; const value = event.target.value; if (key !== 'alt' && !safeUrl(value, key === 'videoUrl')) { event.target.setCustomValidity('Enter a supported URL.'); return; } event.target.setCustomValidity(''); checkpoint(); const ov = selectedOverride(); ov[key] = value; if (key === 'alt' && value.trim()) ov.isDecorative = false; if (key === 'href') { delete ov.actionKey; const action = document.getElementById('legend-cms-action'); if (action) action.value = 'custom'; const custom = document.getElementById('legend-cms-custom-link'); if (custom) custom.hidden = false; } applyElementOverride(selected, ov); syncEditorControls(); markDirty(); }));
    document.getElementById('legend-cms-decorative')?.addEventListener('change', event => {
      if (!selected || !(selected instanceof HTMLImageElement)) return;
      checkpoint();
      const ov = selectedOverride(); if (!ov) return;
      ov.isDecorative = event.target.checked;
      if (ov.isDecorative) ov.alt = '';
      applyElementOverride(selected, ov);
      syncEditorControls();
      markDirty();
    });
    document.getElementById('legend-cms-video-file').addEventListener('change', async event => { const video = selected; if (video?.tagName !== 'VIDEO') return; const url = await uploadMedia(event.target.files?.[0]); if (!url || selected !== video) return; checkpoint(); const ov = selectedOverride(); ov.videoUrl = url; applyElementOverride(video, ov); syncEditorControls(); markDirty(); });
    document.getElementById('legend-cms-target').addEventListener('input', event => { if (!selected) return; checkpoint(); const ov = selectedOverride(); ov.target = event.target.checked ? '_blank' : '_self'; ov.href ||= rememberOriginal(selected).href; applyElementOverride(selected, ov); markDirty(); });
    document.getElementById('legend-cms-undo').addEventListener('click', () => restoreHistory(undoStack, redoStack));
    document.getElementById('legend-cms-redo').addEventListener('click', () => restoreHistory(redoStack, undoStack));
    installDirectCanvasControls(preview);
    showPanel('content');
  }

  function injectContentStyles() { const style = document.createElement('style'); style.textContent = `      [data-cms-id][hidden]{display:none}.cms-layout-frame{display:grid;grid-template-columns:repeat(12,minmax(0,1fr));gap:clamp(8px,2vw,24px);width:100%;min-width:0}.cms-layout-frame>*{grid-column:var(--cms-column,1) / span var(--cms-span,12);max-width:100%;min-width:0;overflow-wrap:anywhere}.cms-extra-section{padding:clamp(24px,5vw,64px);min-height:120px}.cms-extra-container,.cms-extra-group{position:relative;min-width:0}.cms-extra-heading,.cms-extra-quote{max-width:100%}.cms-extra-divider{border:0;border-top:1px solid currentColor;opacity:.45}.cms-extra-spacer{display:block;pointer-events:auto}.cms-extra-shape{display:block;background:var(--web-gold);min-width:24px;min-height:24px}.cms-extra video,video.cms-extra{max-width:100%;height:auto}.cms-extra-code{display:block;width:100%;height:320px;min-height:72px;overflow:hidden;background:#fff}.cms-extra-code iframe{display:block;width:100%;height:100%;border:0;background:#fff}@media(max-width:600px){.cms-layout-frame>*{grid-column:1 / -1}}
`; document.head.appendChild(style); }

  function injectEditorStyles() {
    const style = document.createElement('style');
    style.textContent = `
      .legend-cms-selected{outline:3px solid #f0cf78;outline-offset:4px}
      [data-cms-editable="true"]{cursor:pointer}
      .legend-cms-inline-editing{cursor:text;user-select:text;caret-color:currentColor}
      .legend-cms-locked{outline:2px dashed #7d8ba3!important;outline-offset:4px!important;cursor:not-allowed!important}
      .legend-cms-preview .cms-extra-code iframe{pointer-events:none}
      .legend-cms-grid-overlay{position:absolute;z-index:2147482000;pointer-events:none;border:1px solid #d4ad45a0;background-image:linear-gradient(to right,#d4ad454d 1px,transparent 1px),linear-gradient(to bottom,#d4ad4538 1px,transparent 1px);background-size:calc(100% / 12) 100%,100% 24px;box-shadow:inset 0 0 0 1px #081a3a24}
      .legend-cms-marquee{position:absolute;z-index:2147482400;pointer-events:none;border:1px solid #4cc9f0;background:#4cc9f026;box-shadow:0 0 0 1px #081a3a66 inset}
      .legend-cms-grid-overlay::before,.legend-cms-grid-overlay::after{content:"";position:absolute;pointer-events:none;background:#4cc9f0b8}
      .legend-cms-grid-overlay::before{left:50%;top:0;bottom:0;width:2px;transform:translateX(-1px)}
      .legend-cms-grid-overlay::after{top:50%;left:0;right:0;height:2px;transform:translateY(-1px)}
      .legend-cms-grid-overlay.legend-cms-snap-x::before,.legend-cms-grid-overlay.legend-cms-snap-y::after{background:#f0cf78;box-shadow:0 0 0 2px #081a3a99}
      .legend-cms-selection-frame{position:absolute;z-index:2147482500;pointer-events:none;border:2px solid #d4ad45;box-shadow:0 0 0 1px #081a3a80}
      .legend-cms-move-handle,.legend-cms-resize-handle{position:absolute;pointer-events:auto;touch-action:none;border:1px solid #d4ad45;background:#081a3a;color:#fff;box-shadow:0 3px 12px #0005}
      .legend-cms-move-handle{left:0;top:-38px;min-height:32px;padding:6px 10px;border-radius:9px;font:700 12px/1 Inter,system-ui,sans-serif;cursor:move}
      .legend-cms-selection-frame[data-section-selected="true"] .legend-cms-move-handle{display:none}
      .legend-cms-selection-frame[data-multi-selected="true"] .legend-cms-move-handle,.legend-cms-selection-frame[data-multi-selected="true"] .legend-cms-resize-handle{display:none}
      .legend-cms-multi-selected{outline:2px solid #4cc9f0;outline-offset:3px}
      .legend-cms-resize-handle{width:28px;height:28px;padding:0;border-radius:50%}
      .legend-cms-resize-x{right:-15px;top:50%;transform:translateY(-50%);cursor:ew-resize}
      .legend-cms-resize-y{left:50%;bottom:-15px;transform:translateX(-50%);cursor:ns-resize}
      .legend-cms-resize-xy{right:-15px;bottom:-15px;cursor:nwse-resize}
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
      .legend-cms-code-dialog{width:min(980px,calc(100vw - 32px));height:min(78dvh,760px);max-height:calc(100dvh - 32px);display:grid;grid-template-rows:auto auto minmax(220px,1fr) auto auto;gap:12px;padding:20px;border:1px solid #d4ad45;border-radius:16px;background:#081a3a;color:#f7f6f2}.legend-cms-code-dialog::backdrop{background:#000a}.legend-cms-code-dialog h2,.legend-cms-code-dialog p{margin:0}.legend-cms-code-source{width:100%;min-width:0;min-height:220px;resize:none;padding:14px;border:1px solid #50617e;border-radius:10px;background:#07152d;color:#f7f6f2;font:13px/1.5 ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;tab-size:2}.legend-cms-code-actions{display:flex;gap:10px;justify-content:flex-end}.legend-cms-code-actions button,#legend-cms-code-group button{padding:10px 14px;border:1px solid #50617e;border-radius:10px;background:#142c50;color:#fff;font-weight:700}
      .legend-cms-breakpoint-dialog{width:min(720px,calc(100vw - 32px));max-height:calc(100dvh - 32px);padding:20px;border:1px solid #d4ad45;border-radius:16px;background:#081a3a;color:#f7f6f2}.legend-cms-breakpoint-dialog::backdrop{background:#000a}.legend-cms-breakpoint-list{display:grid;gap:8px;margin:16px 0}.legend-cms-breakpoint-row{display:grid;grid-template-columns:minmax(0,1fr) 140px auto;gap:8px}.legend-cms-breakpoint-row input,.legend-cms-breakpoint-row button,.legend-cms-breakpoint-dialog>button{min-width:0;padding:10px;border:1px solid #50617e;border-radius:9px;background:#142c50;color:#fff}
      .legend-cms-panel-toggle{position:fixed;z-index:2147483000;top:max(10px,env(safe-area-inset-top));right:10px;min-height:40px;padding:8px 12px;border:1px solid #d4ad45;border-radius:999px;background:#081a3af2;color:#fff;font:700 14px/1.2 Inter,system-ui,sans-serif;cursor:pointer;box-shadow:0 8px 24px #0005}
      .legend-cms-panel h2{margin:0 0 4px;font-size:19px}.legend-cms-panel small{display:block;color:#b8c6dc;margin-bottom:14px;overflow-wrap:anywhere}.legend-cms-breakpoint-control{position:sticky;top:46px;z-index:3;padding:10px;border:1px solid #344766;border-radius:10px;background:#0b1e3a}.legend-cms-breakpoint-control small{margin:0}.legend-cms-preview[data-cms-breakpoint]:not([data-cms-breakpoint="base"]){box-shadow:0 0 0 1px #d4ad45 inset;background:#eef2f7}
      .legend-cms-group{display:grid;gap:7px;margin:12px 0}.legend-cms-group label{font-size:12px;font-weight:800;color:#e2d5b8}
      .legend-cms-row{display:grid;grid-template-columns:1fr 1fr;gap:8px}
      .legend-cms-theme{display:grid;grid-template-columns:repeat(2,1fr);gap:8px}
      .legend-cms-theme label{font-size:11px;font-weight:800}.legend-cms-theme input{width:100%;height:36px;border:0;background:transparent}
      .legend-cms-favicon-preview{display:block;width:64px;height:64px;object-fit:contain;border-radius:12px;background:#fff;padding:6px;border:1px solid #50617e}.legend-cms-favicon button{width:100%;padding:10px 12px;border:1px solid #50617e;border-radius:10px;background:#142c50;color:#fff;text-align:center}
      .legend-cms-panel button{cursor:pointer}.legend-cms-inline-help{margin:8px 0 14px;padding:10px 12px;border:1px solid #344766;border-radius:10px;background:#10284a;color:#e7eef8}.legend-cms-menu{display:grid;gap:10px}.legend-cms-menu button,.legend-cms-panel section>button{padding:13px;border:1px solid #50617e;border-radius:12px;background:#142c50;color:#fff;text-align:left}.legend-cms-panel input,.legend-cms-panel textarea,.legend-cms-panel select{width:100%;min-width:0;max-width:100%;color:#f7f6f2;background:#142c50;border:1px solid #50617e;border-radius:8px;padding:8px}.legend-cms-panel :focus-visible{outline:2px solid #f0cf78;outline-offset:3px}
      .legend-cms-panel input[type=checkbox]{width:auto}.legend-cms-panel input[type=color]{min-height:40px;padding:4px}.legend-cms-panel button:disabled{opacity:.45;cursor:default}.legend-cms-media-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:10px}.legend-cms-media-card{display:grid;gap:7px;min-width:0;padding:9px;border:1px solid #344766;border-radius:12px;background:#0b1e3a}.legend-cms-media-preview{display:block;width:100%;aspect-ratio:4/3;object-fit:cover;border-radius:8px;background:#07152d}.legend-cms-media-card strong,.legend-cms-media-card small{overflow-wrap:anywhere}.legend-cms-media-card small{margin:0}.legend-cms-media-card button{padding:9px;border:1px solid #50617e;border-radius:8px;background:#142c50;color:#fff}.legend-cms-motion-card{display:grid;gap:4px;margin:12px 0;padding:12px;border:1px solid #344766;border-radius:12px;background:#0b1e3a}.legend-cms-quality-summary{display:flex;flex-wrap:wrap;align-items:center;gap:8px;margin:16px 0 8px;padding:10px;border:1px solid #344766;border-radius:10px;background:#10284a}.legend-cms-quality-summary strong{margin-right:auto}.legend-cms-quality-summary span{font-size:12px;color:#c9d5e7}.legend-cms-quality-list{display:grid;gap:8px}.legend-cms-quality-issue{margin:0;padding:10px;border:1px solid #344766;border-radius:10px;background:#0d2342}.legend-cms-quality-issue strong,.legend-cms-quality-issue small{display:block}.legend-cms-quality-issue p{margin:6px 0 0}.legend-cms-quality-warning{border-color:#d4ad45}.legend-cms-quality-error{border-color:#ef7b7b}.legend-cms-quality-empty{padding:10px;border:1px dashed #344766;border-radius:10px}.legend-cms-align-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:6px}.legend-cms-align-grid button{min-width:0;padding:9px;border:1px solid #344766;border-radius:8px;background:#142c50;color:#fff}
      .legend-cms-navigation{margin:0 0 20px}.legend-cms-tabs{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:6px}.legend-cms-tabs button{min-height:40px;padding:8px 4px;border:1px solid #344766;border-radius:8px;background:transparent;color:#c9d5e7;font:600 12px/1.3 Inter,system-ui,sans-serif}.legend-cms-tabs button[aria-pressed=true]{background:#e6c77e;color:#10213e;border-color:#e6c77e}
      #legend-cms-status{flex-basis:100%;font-size:12px;color:#c9d5e7;order:1}.legend-cms-panel p{font-size:13px;line-height:1.6;color:#b8c6dc}.legend-cms-layer-list{display:grid;gap:6px}.legend-cms-layer{display:flex;gap:4px;min-width:0}.legend-cms-layer button{min-width:0;padding:10px;border:1px solid #344766;background:#142c50;border-radius:8px;color:#f7f6f2;text-align:left;font-size:12px;overflow-wrap:anywhere}.legend-cms-layer button:first-child{flex:1}.legend-cms-layer button[aria-pressed=true]{border-color:#e6c77e}.legend-cms-search-preview{padding:16px;border:1px solid #344766;border-radius:12px;overflow-wrap:anywhere}.legend-cms-search-preview strong{color:#e6c77e}
      .cms-extra-image{display:block;margin-left:auto;margin-right:auto;height:auto}
      @media(max-width:800px){body.legend-cms-editing{grid-template-columns:minmax(0,1fr);grid-template-rows:minmax(0,55fr) minmax(0,45fr)}body.legend-cms-editing.legend-cms-panel-hidden{grid-template-rows:minmax(0,1fr)}.legend-cms-panel{border-top:2px solid #d4ad45}.legend-cms-panel-toggle{top:max(8px,env(safe-area-inset-top));right:8px}.legend-cms-move-handle{min-height:38px;padding:8px 12px;top:-44px}.legend-cms-resize-handle{width:34px;height:34px}.legend-cms-resize-x{right:-18px}.legend-cms-resize-y{bottom:-18px}.legend-cms-resize-xy{right:-18px;bottom:-18px}}
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
      <label class="legend-cms-group legend-cms-breakpoint-control" for="legend-cms-breakpoint">Responsive canvas<select id="legend-cms-breakpoint"></select><small id="legend-cms-breakpoint-note"></small><button id="legend-cms-manage-breakpoints" type="button">Manage breakpoints</button></label>
      <small id="legend-cms-selected-label">Select content on the page</small>
      <p id="legend-cms-inline-help" class="legend-cms-inline-help" hidden>Type directly on the selected page text. Highlight, replace, or delete words on the canvas; use this panel for controls and actions.</p>
      <div class="legend-cms-row"><button id="legend-cms-lock" type="button" disabled>Lock selected</button><button id="legend-cms-layer-rename" type="button" disabled>Rename layer</button></div>
      <div id="legend-cms-code-group" class="legend-cms-group" hidden>
        <button id="legend-cms-edit-code" type="button">Edit code in modal</button>
        <small>Custom HTML, CSS, and browser JavaScript are previewed inside a sandboxed block. Resize the block directly on the page.</small>
      </div>
      <div id="legend-cms-image-group" class="legend-cms-group" hidden>
        <label for="legend-cms-image">Replace image</label>
        <input id="legend-cms-image" type="file" accept="image/jpeg,image/png,image/webp">
        <label for="legend-cms-alt">Image description<input id="legend-cms-alt" type="text" maxlength="500" placeholder="Describe meaningful image content"></label>
        <label><input id="legend-cms-decorative" type="checkbox"> Decorative image — intentionally ignored by screen readers</label>
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
          <label>Bright gold<input data-theme-key="goldStrong" type="color" value="#f0cf78"></label>
          <label>Surface<input data-theme-key="surface" type="color" value="#ffffff"></label>
          <label>Text<input data-theme-key="text" type="color" value="#101a35"></label>
          <label>Muted<input data-theme-key="muted" type="color" value="#667085"></label>
          <label>Body font<input data-theme-key="fontFamily" type="text" value=""></label>
          <label>Heading font<input data-theme-key="headingFontFamily" type="text" value=""></label>
          <label>Heading weight<input data-theme-key="headingWeight" type="number" min="100" max="900" step="100"></label>
          <label>Body size px<input data-theme-key="fontSize" type="number" min="10" max="32" step="any"></label>
          <label>Body line height<input data-theme-key="lineHeight" type="number" min="0.8" max="3" step="0.05"></label>
          <label>H1 size px<input data-theme-key="h1SizePx" type="number" min="20" max="180" step="any"></label>
          <label>H2 size px<input data-theme-key="h2SizePx" type="number" min="18" max="140" step="any"></label>
          <label>H3 size px<input data-theme-key="h3SizePx" type="number" min="14" max="96" step="any"></label>
          <label>Page side padding px<input data-theme-key="pagePaddingPx" type="number" min="0" max="240" step="any"></label>
          <label>Section vertical padding px<input data-theme-key="sectionPaddingPx" type="number" min="16" max="320" step="any"></label>
          <label>Content gap px<input data-theme-key="contentGapPx" type="number" min="0" max="120" step="any"></label>
          <label>Card radius px<input data-theme-key="borderRadius" type="number" min="0" max="120" step="any"></label>
          <label>Button radius px<input data-theme-key="buttonRadiusPx" type="number" min="0" max="999" step="any"></label>
          <label>Shadow<select data-theme-key="shadowPreset"><option value="">Template default</option><option value="none">None</option><option value="subtle">Subtle</option><option value="medium">Medium</option><option value="strong">Strong</option></select></label>
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
      const refresh = () => { refreshScaledElements(); refreshResponsiveOverrides(); updateDirectCanvasUi(); };
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
    installBreakpointControls(preview);
    refreshScaledElements();
    if (typeof ResizeObserver !== 'undefined') new ResizeObserver(() => { refreshScaledElements(); refreshResponsiveOverrides(); updateDirectCanvasUi(); }).observe(preview);
    syncEditorControls();

    document.addEventListener('click', event => {
      const target = (event.target.tagName === 'IMG' ? event.target.closest('[data-cms-editable="true"]') : event.target.closest('a[data-cms-editable="true"]')) || event.target.closest('[data-cms-editable="true"]');
      if (!target || target.closest('.legend-cms-editor') || target.dataset.cmsLocked === 'true') return;
      const additive = event.shiftKey || event.metaKey || event.ctrlKey;
      const alreadySelected = selectedElements.has(target);
      if (additive) setSelected(target, 'toggle');
      else if (!alreadySelected || selectionItems().length > 1) setSelected(target);
      else activateInlineEditing(target);
      if (target.tagName === 'A' || target.tagName === 'BUTTON') event.preventDefault();
      event.stopPropagation();
      if (!additive && selectionItems().length === 1 && isInlineEditable(target)) target.focus?.({ preventScroll: true });
    }, true);

    ['legend-cms-scale','legend-cms-width','legend-cms-height','legend-cms-padding-top','legend-cms-padding-bottom','legend-cms-offset-x','legend-cms-offset-y','legend-cms-align','legend-cms-hidden']
      .forEach(id => document.getElementById(id)?.addEventListener('input', updateSelectedFromControls));
    ['legend-cms-layout-mode','legend-cms-layout-columns','legend-cms-layout-rows','legend-cms-layout-column-gap','legend-cms-layout-row-gap','legend-cms-layout-direction','legend-cms-layout-align','legend-cms-layout-justify','legend-cms-layout-wrap','legend-cms-layout-overflow']
      .forEach(id => document.getElementById(id)?.addEventListener('input', updateLayoutFromControls));
    document.querySelectorAll('[data-geometry-key]').forEach(control => control.addEventListener('input', updateGeometryFromControls));

    document.getElementById('legend-cms-lock')?.addEventListener('click', () => {
      if (!selected) return;
      checkpoint();
      const ov = selectedOverride(); if (!ov) return;
      ov.editorLocked = ov.editorLocked !== true;
      applyElementOverride(selected, ov);
      syncEditorControls();
      refreshLayers();
      markDirty();
    });
    document.getElementById('legend-cms-layer-rename')?.addEventListener('click', () => {
      if (!selected) return;
      const current = selectedOverride(); if (!current) return;
      const isReusableRoot = selected.dataset.cmsReusableDefinitionId && selected.dataset.cmsReusableLocalId === '__root__';
      const initial = isReusableRoot ? reusableDefinition(selected.dataset.cmsReusableDefinitionId)?.name : current.editorLabel;
      const next = window.prompt?.('Layer name', initial || elementLabel(selected).replace(/^[^·]+·\s*/,'')) ?? null;
      if (next === null) return;
      checkpoint();
      const clean = String(next).trim().slice(0,80);
      if (isReusableRoot) {
        const definition = reusableDefinition(selected.dataset.cmsReusableDefinitionId);
        if (definition) {
          definition.name = clean || 'Reusable component';
          definition.updatedUtc = new Date().toISOString();
          renderComponentCatalog();
          document.querySelectorAll(`[data-cms-reusable-definition-id-ref="${CSS.escape(definition.id)}"]`).forEach(wrapper => {
            wrapper.dataset.cmsReusableName = definition.name;
          });
        }
      } else current.editorLabel = clean || null;
      syncEditorControls(); refreshLayers(); markDirty();
    });
    document.querySelectorAll('[data-z-action]').forEach(button => button.addEventListener('click', () => adjustSelectedZ(button.dataset.zAction)));

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
        applyElementOverride(imageTarget, ov);
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
      const themeVariables = {
        navy:'--web-navy',navyDeep:'--web-navy-deep',gold:'--web-gold',goldStrong:'--web-gold-strong',
        surface:'--web-surface',text:'--web-ink',muted:'--web-muted',fontFamily:'--web-font',
        headingFontFamily:'--web-heading-font',headingWeight:'--web-heading-weight',fontSize:'--web-body-size',
        lineHeight:'--web-body-line-height',h1SizePx:'--web-h1-size',h2SizePx:'--web-h2-size',h3SizePx:'--web-h3-size',
        pagePaddingPx:'--web-page-pad',sectionPaddingPx:'--web-section-pad-y',contentGapPx:'--web-content-gap',
        borderRadius:'--web-radius-lg',buttonRadiusPx:'--web-button-radius'
      };
      const stored = documentState.theme?.[key];
      const fallback = themeVariables[key]
        ? getComputedStyle(document.documentElement).getPropertyValue(themeVariables[key]).trim()
        : key === 'shadowPreset' ? 'medium' : '';
      if (input.type === 'number') {
        const numeric = stored ?? parseFloat(fallback);
        input.value = Number.isFinite(Number(numeric)) ? String(numeric) : '';
      } else {
        const current = stored ?? fallback;
        if (input.type !== 'color' || /^#[0-9a-f]{6}$/i.test(current)) input.value = current || '';
      }
      input.addEventListener('input', () => {
        const value = input.type === 'number' ? Number(input.value) : input.value;
        if (input.type === 'number' && input.value !== '' && !Number.isFinite(value)) return;
        checkpoint();
        if (input.value === '') delete documentState.theme[key];
        else documentState.theme[key] = value;
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
      if (selectionItems().length > 1) { deleteSelection(); return; }
      const serviceCard = businessServiceCardFor(selected);
      if (serviceCard) {
        if (serviceCard.dataset.cmsExtraId) { setSelected(serviceCard); deleteSelection(); return; }
        checkpoint(); const ov = ensureOverride(serviceCard.dataset.cmsId); ov.hidden = true; applyElementOverride(serviceCard, ov); setSelected(null); markDirty(); return;
      }
      deleteSelection();
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
      componentCatalog = Array.isArray(payload.componentCatalog?.options) ? payload.componentCatalog.options : [];
      motionCatalog = payload.motionCatalog && Array.isArray(payload.motionCatalog.triggers) && Array.isArray(payload.motionCatalog.effects)
        ? payload.motionCatalog : null;
      if (customPage) {
        const pages = normalizeDocument(payload.document).pages;
        if (!pages[customPage]) throw new Error('This page is not part of the authorized website draft.');
        document.querySelector('main').replaceChildren();
      }
      prepareDom();
      revision = payload.revision;
      namedDrafts = payload.drafts || [];
      savedQualityReport = payload.quality || payload.readiness?.quality || null;
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
      window.addEventListener('resize', () => { refreshScaledElements(); refreshResponsiveOverrides(); });
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
    window.addEventListener('resize', () => { refreshScaledElements(); refreshResponsiveOverrides(); });
    if (document.fonts?.ready) document.fonts.ready.then(refreshScaledElements);
  }, { once: true });
})();
