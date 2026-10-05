(() => {
  'use strict';

  const publishedData = document.getElementById('legend-cms-published-document');
  const renderInput = window.LEGEND_PUBLIC_CMS_RENDER_INPUT || (publishedData ? JSON.parse(publishedData.textContent) : null);
  const context = window.LEGEND_PUBLIC_CMS_CONTEXT || (renderInput?.business ? {
    siteKey: 'business',
    apiBase: renderInput.runtime?.apiBase || '',
    businessId: renderInput.business.id,
    trackingAsset: renderInput.runtime?.trackingAsset || '/legend-public-tracking.js',
    metaSignalAsset: renderInput.runtime?.metaSignalAsset || '/legend-public-meta-signal-intelligence.js',
    openAiMeasurementAsset: renderInput.runtime?.openAiMeasurementAsset || '/legend-public-openai-measurement.js'
  } : null);
  if (!context || !context.siteKey || typeof context.apiBase !== 'string') return;

  // Protect deliberately uses an empty base for its same-origin CMS authority.
  const API_BASE = (context.apiBase.trim() || location.origin).replace(/\/$/, '');
  let SITE_KEY = String(context.siteKey).toLowerCase();
  const AGENT_SLUG = context.agentSlug || '';
  let BUSINESS_ID = context.businessId || '';
  const params = new URLSearchParams(location.search);
  const requestedPage = SITE_KEY === 'business' && (params.has('legendEdit') || location.pathname.startsWith('/business-preview')) ? params.get('cmsPage') : null;
  const customPage = requestedPage && /^\/(?:[a-z0-9_-]+\/?)*$/.test(requestedPage) ? requestedPage.replace(/\/$/, '') || '/' : null;
  let activeEditorRoute = customPage;
  let pageKey = (customPage ? customPage.slice(1).replace(/\//g, '-') || 'home' : null) || renderInput?.pageKey || document.body?.dataset?.pageKey
    || location.pathname.replace(/^\/+|\/+$/g, '').replace(/[^a-z0-9]+/gi, '-')?.toLowerCase()
    || 'home';

  const editorTicket = params.get('legendEdit') || '';
  const materializeMode = !!editorTicket && params.get('legendMaterialize') === '1';
  const editorMode = !!editorTicket && !materializeMode;
  const studioIsolationMode = editorMode || materializeMode;
  if (studioIsolationMode) {
    window.LEGEND_WEBSITE_STUDIO_MODE = true;
    // Block production form mutations from the browser while Studio is active.
    // Website Studio save/publish uses explicit fetch calls outside form submit.
    document.addEventListener('submit', event => {
      if (event.target?.closest?.('.legend-cms-editor')) return;
      event.preventDefault();
      event.stopImmediatePropagation();
    }, true);
  }
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
  let documentState = { version: 3, faviconImageDataUrl: null, breakpoints: defaultBreakpoints(), shell:{header:[],footer:[]}, reusableComponents:{}, collections:{}, theme:{}, store:{enabled:false,navigationLabel:'Store',cartIcon:'cart',cartIconSizePx:28}, pages:{} };
  let legacyMigration = null;
  let signalCatalog = null;
  let ctaCatalog = [];
  let managementPayload = null;
  let storeContext = null;
  let storePreviewActive = false;
  let collaborationReplyTo = null;
  let collectionData = new Map();
  let fullDataCatalogLoaded = false;
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
  let activeEditorPanel = 'content';
  let dirty = false;
  let sourceEditorDirty = false;
  let sourceEditorBaseNode = null;
  let sourceEditorBaseFingerprint = null;
  let canonicalSourceDocument = null;
  let canonicalSourceRevision = null;
  let templateRepairPending = false;
  let materializationSavePending = false;
  let persistedDocumentState = null;
  let autoSaveTimer = null;
  let checkpointBaseline = null;
  let suppressHistoryCapture = false;
  let sharedPresentationIndex = new Map();
  let sharedPresentationIndexReady = false;
  const originals = new WeakMap();
  const scaledElements = new Map();
  const animationRuntime = new WeakMap();
  const styleProperties = ['textAlign','fontSize','width','maxWidth','height','minHeight','maxHeight','position','left','top','overflow','paddingTop','paddingBottom','objectPosition','color','backgroundColor','backgroundImage','fontFamily','fontWeight','lineHeight','letterSpacing','paddingLeft','paddingRight','borderRadius','objectFit','gridColumn','minWidth','overflowWrap','display','flexDirection','gap','gridTemplateColumns','alignItems','justifyContent','flexWrap','marginTop','marginBottom','marginLeft','marginRight','borderWidth','borderColor','borderStyle','opacity','textTransform','textDecoration','aspectRatio','boxShadow'];

  function rememberOriginal(el) {
    if (!originals.has(el)) originals.set(el, {
      text: el.textContent, markup: el.innerHTML, src: el.getAttribute('src'), href: el.getAttribute('href'), alt: el.getAttribute('alt'), hidden: el.hidden,
      style: Object.fromEntries(styleProperties.map(key => [key, el.style[key] || '']))
    });
    return originals.get(el);
  }

  function cloneCanonicalValue(value) {
    if (value == null || typeof value !== 'object') return value;
    if (typeof globalThis.structuredClone === 'function') return globalThis.structuredClone(value);
    return JSON.parse(JSON.stringify(value));
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

  function constrainHorizontalStyleRecord(style) {
    if (!style || typeof style !== 'object') return;
    const rawWidth = Number(style.widthPercent);
    const hasWidth = Number.isFinite(rawWidth) && rawWidth > 0;
    if (hasWidth) style.widthPercent = Math.min(100, rawWidth);
    const rawOffset = Number(style.offsetXPercent);
    if (Number.isFinite(rawOffset)) {
      // Position is independent from stored width. Rendering consumes the
      // remaining section width so content can move freely without creating
      // horizontal page overflow.
      style.offsetXPercent = Math.max(0, Math.min(95, rawOffset));
    }
  }

  function constrainCompositionGeometry(model) {
    if (!model || typeof model !== 'object') return;
    constrainHorizontalStyleRecord(model.style);
    if (model.breakpointStyles && typeof model.breakpointStyles === 'object')
      Object.values(model.breakpointStyles).forEach(constrainHorizontalStyleRecord);
  }

  function constrainDocumentGeometry(doc) {
    const visit = nodes => (nodes || []).forEach(node => {
      constrainCompositionGeometry(node);
      visit(node?.children);
    });
    visit(doc?.shell?.header);
    visit(doc?.shell?.footer);
    Object.values(doc?.pages || {}).forEach(page => visit(page?.composition));
    Object.values(doc?.reusableComponents || {}).forEach(component => visit(component?.composition));
    constrainCompositionGeometry(doc?.store?.storeNavigation);
    constrainCompositionGeometry(doc?.store?.cartNavigation);
    return doc;
  }

  function isRuntimeShellChromeNode(node) {
    if (!node || typeof node !== 'object') return false;
    const classes=String(node.className || '').split(/\s+/).filter(Boolean);
    return String(node.tag || '').toLowerCase()==='button' &&
      classes.includes('nav-toggle') &&
      !node.actionKey && !node.systemKey && !node.systemBinding && !node.href;
  }

  function wordsFromResourceName(value) {
    if (typeof value !== 'string' || !value.trim() || value.startsWith('data:')) return '';
    let path=value.trim();
    try {
      const url=new URL(path,location.origin);
      path=url.pathname;
    } catch { /* Keep a relative/static path as-is. */ }
    let name=path.split('/').filter(Boolean).pop() || '';
    try { name=decodeURIComponent(name); } catch { }
    name=name.replace(/\.[a-z0-9]{1,8}$/i,'').replace(/[-_]+/g,' ').replace(/\s+/g,' ').trim();
    if(!name) return '';
    return name.split(' ').map(word=>word ? word[0].toUpperCase()+word.slice(1) : '').join(' ').slice(0,160);
  }

  function defaultImageAlt(node) {
    if (!node || typeof node !== 'object') return 'Website image';
    // Explicit empty alt means decorative. Preserve that author decision.
    if (node.alt != null) return String(node.alt).trim();
    for (const candidate of [node.title,node.text]) {
      if (typeof candidate === 'string' && candidate.trim()) return candidate.trim().slice(0,160);
    }
    return wordsFromResourceName(node.mediaUrl) || 'Website image';
  }

  function defaultNavigationLabel(route,title) {
    if(typeof title==='string' && title.trim()) return title.trim().slice(0,120);
    if(route==='/') return 'Home';
    const segment=String(route||'/').split('/').filter(Boolean).pop() || 'Page';
    let value=segment;
    try { value=decodeURIComponent(value); } catch { }
    value=value.replace(/[-_]+/g,' ').replace(/\s+/g,' ').trim();
    if(!value) return 'Page';
    return value.split(' ').map(word=>word ? word[0].toUpperCase()+word.slice(1) : '').join(' ').slice(0,120);
  }

  function isRetiredTemplateDecorationNode(node) {
    if (!node || typeof node !== 'object') return false;
    const classes=String(node.className || '').split(/\s+/).filter(Boolean);
    const hasMeaning=
      String(node.text || '').trim() ||
      String(node.title || '').trim() ||
      node.actionKey || node.href || node.alt ||
      node.mediaAssetId || node.mediaUrl ||
      node.systemKey || node.systemBinding || node.syncSourceId ||
      node.dataBinding ||
      (Array.isArray(node.signals) && node.signals.length) ||
      (Array.isArray(node.children) && node.children.length);
    if(hasMeaning) return false;
    if(classes.includes('icon') || classes.includes('halo') || classes.includes('hero-mark')) return true;
    return node.type==='text' && String(node.tag || '').toLowerCase()==='span' && classes.length===0;
  }

  function canonicalizePassiveLinkNode(node) {
    if (!node || isRuntimeShellChromeNode(node) || node.type!=='link' || node.actionKey || node.dataBinding?.target==='href') return node;
    const href=String(node.href || '').trim();
    if (href && href!=='#') return node;
    // A destination-less anchor is presentation, not navigation. Converting it
    // here repairs already-persisted early-v3 placeholder links instead of
    // teaching readiness checks to ignore dead interactions.
    node.type='text';
    node.tag='span';
    node.actionKey=null;
    node.href=null;
    node.target=null;
    node.signals=[];
    return node;
  }


  const LEGEND_ATTRIBUTION_URL='https://www.mylegnd.com/';

  function enforceLegendAttributionNode(node) {
    if (!node || typeof node !== 'object') return node;
    const classes=String(node.className || '').split(/\s+/).filter(Boolean);
    if (classes.includes('legend-platform-attribution')) {
      node.type='link';
      node.tag='a';
      node.text='Legend®';
      node.href=LEGEND_ATTRIBUTION_URL;
      node.target='_self';
    } else if (classes.includes('legend-platform-attribution-powered-label')) {
      node.text='Powered by';
    } else if (classes.includes('legend-platform-attribution-designed-label')) {
      node.text='Website Designed by';
    }
    return node;
  }

  function normalizeCompositionNodes(input) {
    if (!Array.isArray(input)) return [];
    return input
      .filter(node => node && typeof node === 'object')
      .map(node => {
        const normalized={
          ...node,
          children: normalizeCompositionNodes(node.children),
          style: node.style && typeof node.style === 'object' ? node.style : {},
          breakpointStyles: node.breakpointStyles && typeof node.breakpointStyles === 'object' ? node.breakpointStyles : {},
          layout: node.layout && typeof node.layout === 'object' ? node.layout : {},
          breakpointLayouts: node.breakpointLayouts && typeof node.breakpointLayouts === 'object' ? node.breakpointLayouts : {},
          animations: Array.isArray(node.animations) ? node.animations : [],
          signals: Array.isArray(node.signals) ? node.signals : []
        };
        if(normalized.type==='image') normalized.alt=defaultImageAlt(normalized);
        enforceLegendAttributionNode(normalized);
        canonicalizePassiveLinkNode(normalized);
        return normalized;
      })
      // Menu toggles are reconstructed runtime chrome. They were accidentally
      // persisted by early v3 materialization and have no website destination.
      .filter(node => !isRuntimeShellChromeNode(node))
      // Repair early v3 drafts that persisted presentation-only template wrappers
      // after their SVG/pseudo-element contents were deliberately excluded.
      .filter(node => !isRetiredTemplateDecorationNode(node));
  }

  const MOBILE_HEADER_GEOMETRY_FIELDS=[
    'widthPercent','heightPx','offsetXPercent','offsetYPx',
    'minWidthPx','maxWidthPx','minHeightPx','maxHeightPx',
    'marginTop','marginBottom','marginLeft','marginRight'
  ];

  function mobileHeaderChromeKind(node) {
    if (!node || typeof node!=='object') return null;
    const tag=String(node.tag || '').toLowerCase();
    const classes=new Set(String(node.className || '').split(/\s+/).filter(Boolean));
    if(node.systemKey==='primary_navigation' || (tag==='nav' && classes.has('nav'))) return 'navigation';
    if(node.systemBinding==='business_name' ||
       classes.has('brand') || classes.has('brand-wordmark') || classes.has('business-brand-banner')) return 'brand';
    if(tag==='header' || classes.has('site-header')) return 'frame';
    return null;
  }

  function canonicalizeMobileHeaderChrome(node) {
    const kind=mobileHeaderChromeKind(node);
    if(!kind) return node;
    node.breakpointStyles ||= {};
    const mobile=node.breakpointStyles.mobile && typeof node.breakpointStyles.mobile==='object'
      ? {...node.breakpointStyles.mobile}
      : {};
    MOBILE_HEADER_GEOMETRY_FIELDS.forEach(field=>delete mobile[field]);
    if(kind==='brand' && positiveNumber(mobile.fontScale))
      mobile.fontScale=Math.min(1.35,Number(mobile.fontScale));
    if(kind==='navigation' && positiveNumber(mobile.fontScale))
      mobile.fontScale=Math.min(1,Number(mobile.fontScale));
    if(Object.keys(mobile).length) node.breakpointStyles.mobile=mobile;
    else delete node.breakpointStyles.mobile;

    if(kind==='frame' || kind==='navigation'){
      node.breakpointLayouts ||= {};
      node.breakpointLayouts.mobile={mode:'free',direction:'column'};
    }
    return node;
  }

  function applyCanonicalHeaderTypographyDefaults(node) {
    if (!node || typeof node!=='object') return node;
    node.style ||= {};
    const tag=String(node.tag || '').toLowerCase();
    const isBrandTitle=
      node.systemBinding==='business_name' ||
      (SITE_KEY==='legend' && tag==='strong' && String(node.text || '').trim()==='LEGEND®');
    if(isBrandTitle){
      if(!positiveNumber(node.style.fontScale)) node.style.fontScale=3.5;
      if(!positiveNumber(node.style.fontWeight)) node.style.fontWeight=800;
      node.breakpointStyles ||= {};
      node.breakpointStyles.mobile ||= {};
      node.breakpointStyles.tablet ||= {};
      if(!positiveNumber(node.breakpointStyles.mobile.fontScale)) node.breakpointStyles.mobile.fontScale=1.35;
      if(!positiveNumber(node.breakpointStyles.mobile.fontWeight)) node.breakpointStyles.mobile.fontWeight=800;
      if(!positiveNumber(node.breakpointStyles.tablet.fontScale)) node.breakpointStyles.tablet.fontScale=1.8;
      if(!positiveNumber(node.breakpointStyles.tablet.fontWeight)) node.breakpointStyles.tablet.fontWeight=800;
    }
    if(node.systemKey==='primary_navigation'){
      if(!positiveNumber(node.style.fontScale)) node.style.fontScale=1.6;
      if(!positiveNumber(node.style.fontWeight)) node.style.fontWeight=800;
      node.breakpointStyles ||= {};
      node.breakpointStyles.mobile ||= {};
      node.breakpointStyles.tablet ||= {};
      if(!positiveNumber(node.breakpointStyles.mobile.fontScale)) node.breakpointStyles.mobile.fontScale=1;
      if(!positiveNumber(node.breakpointStyles.mobile.fontWeight)) node.breakpointStyles.mobile.fontWeight=800;
      if(!positiveNumber(node.breakpointStyles.tablet.fontScale)) node.breakpointStyles.tablet.fontScale=1.15;
      if(!positiveNumber(node.breakpointStyles.tablet.fontWeight)) node.breakpointStyles.tablet.fontWeight=800;
    }
    return node;
  }

  function normalizePageCompositionNodes(input) {
    // Global website chrome has one source: shell.header / shell.footer.
    // Repair stale drafts by refusing page-local copies of those authorities.
    return normalizeCompositionNodes(input).filter(node=>{
      const tag=String(node?.tag || '').toLowerCase();
      const classes=new Set(String(node?.className || '').split(/\s+/).filter(Boolean));
      if(tag==='header' || tag==='footer') return false;
      if(node?.systemKey==='primary_navigation') return false;
      if(classes.has('site-header') || classes.has('site-footer')) return false;
      return true;
    });
  }

  function normalizeHeaderComposition(input) {
    const roots=normalizeCompositionNodes(input);
    let primarySeen=false;
    const clean=nodes => {
      const result=[];
      for(const node of nodes || []) {
        canonicalizeMobileHeaderChrome(node);
        applyCanonicalHeaderTypographyDefaults(node);
        const classes=String(node?.className || '').split(/\s+/).filter(Boolean);
        const primary=node?.systemKey==='primary_navigation';
        const templateNav=!primary && String(node?.tag || '').toLowerCase()==='nav' && classes.includes('nav');
        if(templateNav && SITE_KEY==='business') continue;
        if(primary) {
          if(primarySeen) continue;
          primarySeen=true;
          // Page links are a projection of versioned page navigation metadata,
          // not a second persisted list inside the shell.
          if(SITE_KEY==='business') node.children=[];
          else node.children=clean(node.children);
        } else {
          node.children=clean(node.children);
        }
        result.push(node);
      }
      return result;
    };
    return clean(roots);
  }

  function normalizeControlPresentation(input) {
    const value=input && typeof input==='object' ? input : {};
    return {
      style:value.style && typeof value.style==='object' ? {...value.style} : {},
      breakpointStyles:value.breakpointStyles && typeof value.breakpointStyles==='object' ? cloneCanonicalValue(value.breakpointStyles) : {},
      layout:value.layout && typeof value.layout==='object' ? {...value.layout} : {mode:'free',direction:'column'},
      breakpointLayouts:value.breakpointLayouts && typeof value.breakpointLayouts==='object' ? cloneCanonicalValue(value.breakpointLayouts) : {},
      animations:Array.isArray(value.animations) ? cloneCanonicalValue(value.animations) : []
    };
  }

  function cleanBusinessPreviewTitle(value) {
    if (typeof value !== 'string') return null;
    if (SITE_KEY !== 'business') return value;
    const suffix=' | Business website preview';
    return value.endsWith(suffix) ? value.slice(0,-suffix.length) : value;
  }

  function normalizeDocument(input) {
    const pages = {};
    for (const [key,value] of Object.entries(input?.pages && typeof input.pages === 'object' ? input.pages : {})) {
      if (!value || typeof value !== 'object') continue;
      const rawRoute = normalizePageRoute(key);
      const route = canonicalSiteRoute(rawRoute);
      if (!route) continue;
      // Protect v2 could persist the browser-owned /a/{slug}/... path. V3 owns
      // route identity independent of agent URL scope, so collapse those rows
      // into the one canonical page key and never serialize the prefixed copy.
      if (pages[route] && rawRoute !== route) continue;
      const title=cleanBusinessPreviewTitle(value.title);
      const navigation=value.navigation && typeof value.navigation === 'object'
        ? {...value.navigation}
        : {showInNavigation:true,order:0,isDeleted:false};
      navigation.showInNavigation=navigation.showInNavigation!==false;
      navigation.isDeleted=navigation.isDeleted===true;
      navigation.order=Number.isFinite(Number(navigation.order)) ? Number(navigation.order) : 0;
      if(navigation.showInNavigation && !navigation.isDeleted && !String(navigation.label||'').trim())
        navigation.label=defaultNavigationLabel(route,title);
      pages[route] = {
        title,
        description: typeof value.description === 'string' ? value.description : null,
        navigation,
        dynamicBinding: value.dynamicBinding && typeof value.dynamicBinding === 'object' ? {...value.dynamicBinding} : null,
        systemTemplateKey: typeof value.systemTemplateKey === 'string' ? value.systemTemplateKey : null,
        composition: normalizePageCompositionNodes(value.composition)
      };
    }

    const reusableComponents={};
    for(const [id,value] of Object.entries(input?.reusableComponents && typeof input.reusableComponents === 'object' ? input.reusableComponents : {})){
      if(!value || typeof value!=='object') continue;
      reusableComponents[id]={
        id,
        name:typeof value.name==='string'?value.name:id,
        kind:value.kind==='block'?'block':'section',
        composition:normalizeCompositionNodes(value.composition)
      };
    }

    const normalized = {
      version:3,
      faviconImageDataUrl:typeof input?.faviconImageDataUrl==='string'?input.faviconImageDataUrl:null,
      breakpoints:normalizeBreakpoints(input?.breakpoints),
      shell:{
        header:normalizeHeaderComposition(input?.shell?.header),
        footer:normalizeCompositionNodes(input?.shell?.footer)
      },
      reusableComponents,
      collections:input?.collections && typeof input.collections==='object' ? cloneCanonicalValue(input.collections) : {},
      theme:input?.theme && typeof input.theme==='object' ? {...input.theme} : {},
      store:{
        enabled:input?.store?.enabled===true,
        navigationLabel:typeof input?.store?.navigationLabel==='string' && input.store.navigationLabel.trim()
          ? input.store.navigationLabel.trim().slice(0,40) : 'Store',
        cartIcon:['cart','bag','basket'].includes(String(input?.store?.cartIcon||'').toLowerCase())
          ? String(input.store.cartIcon).toLowerCase() : 'cart',
        cartIconSizePx:Number.isFinite(Number(input?.store?.cartIconSizePx))
          ? Math.max(16,Math.min(96,Number(input.store.cartIconSizePx))) : 28,
        storeNavigation:normalizeControlPresentation(input?.store?.storeNavigation),
        cartNavigation:normalizeControlPresentation(input?.store?.cartNavigation)
      },
      pages
    };
    synchronizeCanonicalSharedPresentation(normalized);
    return constrainDocumentGeometry(normalized);
  }

  function canonicalSharedPresentationKey(node) {
    if (!node || typeof node !== 'object') return null;
    if (node.type==='form' && node.systemKey==='canonical_inquiry') return 'form:canonical_inquiry';
    return null;
  }

  function copyCanonicalSharedPresentation(target, source) {
    if (!target || !source || target===source) return;
    for (const key of ['className','text','title','style','breakpointStyles','layout','breakpointLayouts','animations','fieldLabels','fieldPresentations']) {
      if (Object.hasOwn(source,key)) target[key]=cloneCanonicalValue(source[key]);
      else delete target[key];
    }
  }

  function buildCanonicalSharedPresentationIndex(doc) {
    const groups=new Map();
    if(!doc || typeof doc!=='object') return groups;
    const visit=nodes=>walkComposition(nodes,node=>{
      const key=canonicalSharedPresentationKey(node);
      if(!key) return;
      if(!groups.has(key)) groups.set(key,[]);
      groups.get(key).push(node);
    });
    Object.values(doc.pages || {}).forEach(page=>visit(page?.composition || []));
    return groups;
  }

  function rebuildCanonicalSharedPresentationIndex(doc=documentState) {
    sharedPresentationIndex=buildCanonicalSharedPresentationIndex(doc);
    sharedPresentationIndexReady=doc===documentState;
    return sharedPresentationIndex;
  }

  function synchronizeCanonicalSharedPresentation(doc, preferredId=null) {
    if (!doc || typeof doc!=='object') return doc;
    const groups=doc===documentState && sharedPresentationIndexReady
      ? sharedPresentationIndex
      : buildCanonicalSharedPresentationIndex(doc);
    for (const nodes of groups.values()) {
      if (nodes.length < 2) continue;
      const source=nodes.find(node=>node.id===preferredId) || nodes[0];
      nodes.forEach(node=>copyCanonicalSharedPresentation(node,source));
    }
    return doc;
  }


  function normalizePageRoute(value) {
    if (typeof value !== 'string') return null;
    let route=value.trim().toLowerCase();
    if (!route.startsWith('/')) route='/'+route;
    route=route.replace(/\/+$/,'') || '/';
    if (!/^\/(?:[a-z0-9_-]+\/?)*$/.test(route) || route.length>160 || route.includes('..')) return null;
    return route;
  }

  function protectCanonicalPathname(pathname) {
    if (SITE_KEY !== 'protect') return pathname;
    const prefixes = [];
    const pagePrefix = String(context.pagePrefix || '').trim().replace(/\/+$/, '');
    if (pagePrefix && pagePrefix !== '/') prefixes.push(pagePrefix);
    const agentSlug = String(managementPayload?.agentSlug || AGENT_SLUG || '').trim();
    if (agentSlug) prefixes.push('/a/' + encodeURIComponent(agentSlug));
    for (const prefix of [...new Set(prefixes)].sort((left, right) => right.length - left.length)) {
      if (pathname === prefix) return '/';
      if (pathname.startsWith(prefix + '/')) return pathname.slice(prefix.length) || '/';
    }
    return pathname;
  }

  function canonicalSiteRoute(value) {
    const normalized = normalizePageRoute(value);
    if (!normalized) return null;
    const scoped = protectCanonicalPathname(normalized);
    return normalizePageRoute(scoped) || normalized;
  }

  function currentPageRoute() {
    const browserPath = (editorMode && activeEditorRoute) || customPage || location.pathname.replace(/^\/business-preview/, '').replace(/\/$/, '') || '/';
    return canonicalSiteRoute(browserPath) || '/';
  }

  function pageState() {
    documentState.pages ||= {};
    const route=currentPageRoute();
    if(!documentState.pages[route]){
      documentState.pages[route]={
        title:null,
        description:null,
        navigation:{label:defaultNavigationLabel(route,null),showInNavigation:true,order:0,isDeleted:false},
        dynamicBinding:null,
        composition:[]
      };
    }
    const page=documentState.pages[route];
    page.composition ||= [];
    page.navigation ||= {showInNavigation:true,order:0,isDeleted:false};
    return page;
  }

  function pageUsesSystemTemplate(page = pageState()) {
    return SITE_KEY === 'protect' &&
      typeof page?.systemTemplateKey === 'string' &&
      page.systemTemplateKey.startsWith('protect_template:');
  }

  function containsProtectedRuntimeForm(nodes) {
    let found=false;
    walkComposition(nodes,node=>{
      if(String(node?.systemKey || '').startsWith('protect_runtime_form:')) {
        found=true;
        return false;
      }
    });
    return found;
  }

  function applyTemplateBackedCompositionPage() {
    const page=pageState();
    const mounted=new Map();
    walkComposition(page.composition || [],node=>{
      const el=findEditableElement(node.id);
      if(el) mounted.set(node.id,el);
    });
    const ids=new Set();
    walkComposition(page.composition || [],node=>ids.add(node.id));
    document.querySelectorAll('main [data-cms-id]').forEach(el=>{
      if(ids.has(el.dataset.cmsId) || el.closest('form[data-form-key]') || el.querySelector('form[data-form-key]')) return;
      // Omitted free presentation stays omitted on reload. Native form controls
      // are executable template state and never deleted by this reconciliation.
      el.remove();
    });
    const reconcile=(nodes,parent)=>{
      for(const node of nodes || []) {
        let el=mounted.get(node.id);
        // Only the server template can mount executable protected forms.
        if(!el && String(node.systemKey || '').startsWith('protect_runtime_form:')) continue;
        el ||= buildCompositionNode({...node,children:[]});
        if(!el) continue;
        el.dataset.cmsCompositionId=node.id;
        applyCompositionNode(el,node);
        if(el.tagName==='FORM') applyFormFieldPresentations(el,node);
        // Move the original nodes, retaining native controls, state and listeners.
        // Unrepresented runtime inputs remain in their native parent.
        if(parent && el!==parent) parent.appendChild(el);
        reconcile(node.children,el);
      }
    };
    reconcile(page.composition,document.querySelector('main'));
  }

  function usesCanonicalComposition() {
    return legacyMigration == null;
  }

  function walkComposition(nodes, visit, parent = null) {
    for (const node of Array.isArray(nodes) ? nodes : []) {
      if (!node || typeof node !== 'object') continue;
      if (visit(node, parent) === false) return false;
      if (walkComposition(node.children, visit, node) === false) return false;
    }
    return true;
  }

  function allCanonicalRootSets() {
    const roots=[
      {scope:'page',nodes:pageState().composition},
      {scope:'shell.header',nodes:documentState.shell?.header || []},
      {scope:'shell.footer',nodes:documentState.shell?.footer || []}
    ];
    for(const [id,component] of Object.entries(documentState.reusableComponents || {}))
      roots.push({scope:'component:'+id,nodes:component?.composition || []});
    return roots;
  }

  function compositionEntry(id) {
    let found=null;
    for(const root of allCanonicalRootSets()){
      walkComposition(root.nodes,(node,parent)=>{
        if(node.id!==id) return;
        found={node,parent,root};
        return false;
      });
      if(found) break;
    }
    return found;
  }

  function compositionNode(id) {
    return compositionEntry(id)?.node || null;
  }

  function compositionChildren(parent, root = null) {
    if(parent) return parent.children ||= [];
    return root?.nodes || pageState().composition;
  }

  function removeCompositionNode(id) {
    let removed=null;
    const removeFrom=nodes=>{
      if(!Array.isArray(nodes)) return false;
      const index=nodes.findIndex(node=>node?.id===id);
      if(index>=0){ [removed]=nodes.splice(index,1); return true; }
      return nodes.some(node=>removeFrom(node?.children));
    };
    for(const root of allCanonicalRootSets()) if(removeFrom(root.nodes)) break;
    return removed;
  }

  function safeId(value) {
    return String(value || '')
      .toLowerCase()
      .replace(/[^a-z0-9_.:-]+/g, '-')
      .replace(/^-+|-+$/g, '')
      .slice(0, 160);
  }

  function isSharedShellElement(el) {
    return !!el?.closest?.('.site-header,.site-footer');
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
    if (el.dataset.cmsSection) return true;
    return !['DIV','ARTICLE','HEADER','FOOTER'].includes(el.tagName);
  }

  function editorSelectionTarget(node) {
    if (!node?.closest) return null;
    const locked=node.closest('[data-cms-locked="true"]');
    if(locked){
      const parent=locked.parentElement?.closest?.('[data-cms-editable="true"]');
      return parent instanceof HTMLElement ? parent : null;
    }
    const entity = node.closest('[data-business-name][data-cms-editable="true"],[data-business-field][data-cms-editable="true"]');
    if (entity instanceof HTMLElement) return entity;
    const anchor = node.closest('a[data-cms-editable="true"]');
    if (anchor instanceof HTMLElement) return anchor;
    const candidate = node.closest('[data-cms-editable="true"]');
    return candidate instanceof HTMLElement ? candidate : null;
  }

  function prepareDom() {
    const roots = [
      document.querySelector('main'),
      document.querySelector('.site-header'),
      document.querySelector('.nav'),
      document.querySelector('.site-footer')
    ].filter(Boolean);

    // Missing alt is corrected at the shared presentation boundary before the
    // DOM can be materialized into canonical v3. Explicit alt="" remains a
    // valid decorative-image decision.
    document.querySelectorAll('main img').forEach(image=>{
      if(image.hasAttribute('alt')) return;
      image.setAttribute('alt',defaultImageAlt({
        type:'image',
        title:image.getAttribute('aria-label') || image.getAttribute('title'),
        mediaUrl:image.getAttribute('src')
      }));
    });

    // Stable system-form identities are assigned before generic DOM IDs.
    document.querySelectorAll('[data-legend-public-inquiry-form]').forEach((mount,index)=>{
      mount.dataset.cmsId = index ? 'form.canonical_inquiry.' + (index + 1) : 'form.canonical_inquiry';
      mount.dataset.cmsEditable = 'true';
      mount.dataset.cmsSystemForm = 'canonical_inquiry';
      rememberOriginal(mount);
    });
    if (SITE_KEY === 'protect') {
      document.querySelectorAll('form[data-form-key]:not([data-website-inquiry])').forEach((form,index)=>{
        const formKey=safeId(form.dataset.formKey || form.id || ('runtime-' + (index + 1))) || ('runtime-' + (index + 1));
        form.dataset.cmsId='runtime.form.'+formKey;
        form.dataset.cmsEditable='true';
        form.dataset.cmsSystemForm='protect_runtime_form:'+formKey;
        rememberOriginal(form);
      });
    }

    const sections = [...document.querySelectorAll(sectionCandidates), ...document.querySelectorAll('.site-header,.site-footer')];
    sections.forEach((section, index) => {
      if (!section.dataset.cmsSection) {
        section.dataset.cmsSection = section.matches('.site-header') ? 'shell.header' : section.matches('.site-footer') ? 'shell.footer' : `${pageKey}.section.${index + 1}`;
      }
      section.dataset.cmsId = `section:${section.dataset.cmsSection}`;
      section.dataset.cmsEditable = 'true';
      rememberOriginal(section);
    });

    let counter = 0, addedCounter = 0;
    roots.forEach(root => {
      root.querySelectorAll('h1,h2,h3,h4,h5,p,li,a,button,label,small,strong,span,img,video,div,article,header,footer,form,fieldset').forEach(el => {
        if (!canEditElement(el)) return;
        if (!['IMG','VIDEO','A','DIV','ARTICLE','HEADER','FOOTER'].includes(el.tagName) && el.children.length > 0) return;
        if (!el.dataset.cmsId) {
          const legacy = !el.closest('.brand,.brand-wordmark') && !['VIDEO','DIV','ARTICLE','HEADER','FOOTER'].includes(el.tagName) && (el.tagName === 'IMG' || el.children.length === 0);
          const index = legacy ? ++counter : `node${++addedCounter}`;
          const shellPrefix = el.closest('.site-header') ? 'shell.header' : el.closest('.site-footer') ? 'shell.footer' : pageKey;
          el.dataset.cmsId = `${shellPrefix}.${safeId(el.tagName)}.node.${index}`;

        }
        rememberOriginal(el);
        if (isDirectCanvasSelectable(el)) el.dataset.cmsEditable = 'true';
        else delete el.dataset.cmsEditable;
        if ((el.tagName === 'A' || el.tagName === 'BUTTON') && !el.dataset.cmsAction) {
          el.dataset.cmsAction = el.dataset.cta || el.getAttribute('href') || 'action';
        }
      });
    });
    document.querySelectorAll('main form:not([data-cms-composition-id]), main input:not([type="hidden"]):not([type="password"]), main select, main textarea').forEach((node, index) => {
      if (node.closest('[data-cms-locked="true"]')) return;
      node.dataset.cmsId ||= `signal:${pageKey}.${safeId(node.tagName)}.${safeId(node.id || node.name || 'field')}.${index}`;
      node.dataset.cmsFieldKey ||= safeId(node.name || node.id || ('field-'+index));
      node.dataset.cmsSignalOnly = 'true'; node.dataset.cmsEditable = 'true';
      rememberOriginal(node);
    });
  }

  function selectedSignalContext() {
    if (!selected || legacyMigration) return null;
    const fieldKey=['INPUT','SELECT','TEXTAREA','BUTTON'].includes(selected.tagName) && selected.dataset?.cmsFieldKey
      ? formFieldKey(selected)
      : null;
    const form=fieldKey ? selected.closest?.('form[data-cms-composition-id]') : null;
    const elementId=fieldKey
      ? form?.dataset?.cmsCompositionId
      : (selected.dataset.cmsCompositionId || selected.dataset.cmsId || null);
    if(!elementId) return null;
    const model=compositionNode(elementId);
    if(!model) return null;
    const bindings=fieldKey
      ? (model.fieldSignals?.[fieldKey] || [])
      : (model.signals || []);
    return {elementId,fieldKey,model,bindings};
  }

  function selectedSignalElementId() {
    return selectedSignalContext()?.elementId || null;
  }

  async function persistSelectedSignals(nextSignals) {
    const context=selectedSignalContext();
    if(!context) return false;
    const status=document.getElementById('legend-cms-status');

    if(dirty){
      if(status) status.textContent='Saving design changes before updating Analytics mapping…';
      const saved=await save(false);
      if(!saved || dirty) return false;
    }

    const response=await fetch(`${API_BASE}/api/website-content/manage/signals`,{
      method:'POST',
      headers:{'Content-Type':'application/json'},
      body:JSON.stringify({
        ticket:editorTicket,
        expectedRevision:revision,
        pagePath:currentPageRoute(),
        elementId:context.elementId,
        fieldKey:context.fieldKey,
        signals:nextSignals
      })
    });
    const payload=await response.json().catch(()=>({}));
    if(!response.ok){
      if((response.status===401 || response.status===403) &&
          showEditorAuthorizationRecovery('Website Studio authorization expired while updating this Analytics mapping.'))
        return false;
      if(status) status.textContent=payload.message || payload.error || `Signal update failed (${response.status}).`;
      return false;
    }
    if(payload.source!=='website_signal_configuration'){
      if(status) status.textContent='Signal update response was invalid.';
      return false;
    }

    documentState=normalizeDocument(payload.document || documentState);
    revision=payload.revision ?? revision;
    canonicalSourceDocument=null;
    canonicalSourceRevision=null;
    dirty=false;
    applyDocument(documentState);

    let refreshed=findEditableElement(context.elementId);
    if(context.fieldKey && refreshed){
      refreshed=[...refreshed.querySelectorAll('input,select,textarea,button[data-cms-field-key]')]
        .find(control=>formFieldKey(control)===context.fieldKey) || null;
    }
    if(refreshed) setSelected(refreshed);
    else setSelected(null);
    if(status) status.textContent='Analytics mapping saved to the canonical draft.';
    return true;
  }

  async function mutateSelectedSignals(bindingId, mutation) {
    const context=selectedSignalContext();
    if(!context) return false;
    const next=cloneCanonicalValue(context.bindings || []);
    const binding=bindingId ? next.find(value=>value.id===bindingId) : null;
    if(bindingId && !binding) return false;
    mutation(binding,next);
    return persistSelectedSignals(next);
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
    const context=selectedSignalContext();
    if (!binding?.id || !context?.elementId) return;
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
          elementId:context.elementId,
          bindingId:binding.id,
          fieldKey:context.fieldKey
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
    const context=selectedSignalContext();
    if (!binding?.id || !context?.elementId) return;
    if (!await ensureSavedForSignalInspection(binding.id)) return;
    renderSignalDiagnosticMessage(binding.id, 'Loading destination health and published delivery evidence…');
    try {
      const url = new URL(`${API_BASE}/api/website-content/manage/signals/health`);
      url.searchParams.set('ticket', editorTicket);
      url.searchParams.set('pagePath', currentPageRoute());
      url.searchParams.set('elementId',context.elementId);
      url.searchParams.set('bindingId',binding.id);
      if(context.fieldKey) url.searchParams.set('fieldKey',context.fieldKey);
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

  async function ensureSignalCatalog() {
    if(signalCatalog) return signalCatalog;
    try{
      const payload=await creativeWorkspaceRequest('manage/signal-catalog');
      signalCatalog=Array.isArray(payload?.events) && Array.isArray(payload?.matchingFields) ? payload : null;
    }catch(error){
      console.error('[legend-cms] signal catalog',error);
      signalCatalog=null;
    }
    return signalCatalog;
  }

  function renderSignalControls() {
    const host=document.getElementById('legend-cms-signal-controls');
    if(!host) return;
    host.replaceChildren();
    const paragraph=text=>{const node=document.createElement('p');node.textContent=text;host.appendChild(node);};
    if(!selected){paragraph('Select a button, form, field, or section on the page.');return;}
    if(!signalCatalog){paragraph('The event catalog could not be loaded. Reopen the editor to try again.');return;}

    const context=selectedSignalContext();
    if(!context){paragraph('This element does not expose canonical signal configuration.');return;}

    const type=selected.tagName;
    const triggers=['viewed'];
    if(['A','BUTTON'].includes(type)) triggers.push('click');
    if(type==='FORM') triggers.push('form_started','submit_attempt','submission_saved');
    if(['INPUT','SELECT','TEXTAREA'].includes(type)) triggers.push('field_started','validation_failed');
    if(type==='INPUT' && selected.type==='tel') triggers.push('field_completed');
    if(selected.dataset.cmsSection) triggers.push('scroll_threshold');

    const candidates=signalCatalog.events.filter(option=>
      !option.requiresServerOutcome &&
      option.triggers.some(trigger=>triggers.includes(trigger)));
    const bindings=context.bindings || [];
    paragraph(bindings.length
      ? `${bindings.length} interaction mapping${bindings.length===1?'':'s'}`
      : 'No signal. This element has no configured marketing event.');
    if(!signalCatalog.runtimeEnabled)
      paragraph('Delivery is not activated for this release. You can prepare and save mappings.');

    const managedActionKey=selected.dataset.websiteActionKey;
    const managedAction=(managedActionKey && availableCtaOptions().find(option=>option.key===managedActionKey)) || null;
    if(type==='FORM' && selected.matches?.('[data-website-inquiry]')){
      const title=document.createElement('strong');title.textContent='Automatic form analytics';host.appendChild(title);
      const help=document.createElement('p');
      help.textContent='No mapping is required. The shared Protect Website runtime automatically tracks the canonical inquiry lifecycle, and the backend owns the confirmed Lead outcome.';
      host.appendChild(help);
      const automatic=document.createElement('div');automatic.className='legend-cms-signal-presets';
      for(const name of ['LeadFormStart','ContactInputStarted','PhoneFieldCompleted','RequiredContactFieldsCompleted','SubmitAttempt','Lead']){
        const option=signalCatalog.events.find(value=>value.name===name);
        if(!option) continue;
        const row=document.createElement('div');
        row.textContent=`${name} · automatic · ${option.requiresServerOutcome?'verified server outcome':'analytics + eligible configured destinations'}`;
        automatic.appendChild(row);
      }
      host.appendChild(automatic);
    }else if(managedAction){
      const title=document.createElement('strong');title.textContent='Automatic action analytics';host.appendChild(title);
      const help=document.createElement('p');
      help.textContent=`${managedAction.label || managedAction.key} is already wired by the shared action contract: ${managedAction.analyticsEventName || 'cta_click'}. No manual mapping is required.`;
      host.appendChild(help);
    }

    const addSelect=(labelText,values,value,action)=>{
      const label=document.createElement('label');label.className='legend-cms-group';label.textContent=labelText;
      const select=document.createElement('select');
      for(const [key,text] of values){const option=document.createElement('option');option.value=key;option.textContent=text;select.appendChild(option);}
      select.value=value;
      select.addEventListener('change',()=>void action(select.value));
      label.appendChild(select);host.appendChild(label);return select;
    };

    for(const binding of bindings){
      const definition=signalCatalog.events.find(x=>x.name===binding.eventName);
      addSelect('Send',
        [['off','Do not send'],['analytics','Analytics only'],['destinations','Analytics + configured destinations']],
        binding.deliveryMode==='meta'?'destinations':binding.deliveryMode,
        value=>mutateSelectedSignals(binding.id,draft=>{draft.deliveryMode=value;}));

      const available=candidates.flatMap(option=>
        option.triggers.filter(trigger=>triggers.includes(trigger))
          .map(trigger=>[option.name+':'+trigger,`${option.displayLabel || option.name} · ${trigger.replaceAll('_',' ')}`]));
      addSelect('Event and trigger',available,binding.eventName+':'+binding.trigger,
        value=>mutateSelectedSignals(binding.id,draft=>{
          const [name,trigger]=value.split(':');
          draft.eventName=name;
          draft.trigger=trigger;
          draft.matchingFields=[];
          draft.actionKey=signalCatalog.events.find(x=>x.name===name)?.actionKey || null;
        }));

      const onceLabel=document.createElement('label');
      const once=document.createElement('input');once.type='checkbox';once.checked=binding.oncePerSession;
      once.addEventListener('change',()=>void mutateSelectedSignals(binding.id,draft=>{draft.oncePerSession=once.checked;}));
      onceLabel.append(once,document.createTextNode(' Once per session'));host.appendChild(onceLabel);

      if(definition?.requiresServerOutcome){
        paragraph('Sent only after the backend confirms this outcome. Customer matching requires advertising consent.');
        for(const field of signalCatalog.matchingFields){
          const label=document.createElement('label'),input=document.createElement('input');
          input.type='checkbox';input.checked=binding.matchingFields?.includes(field);
          input.addEventListener('change',()=>void mutateSelectedSignals(binding.id,draft=>{
            const fields=new Set(draft.matchingFields || []);
            input.checked ? fields.add(field) : fields.delete(field);
            draft.matchingFields=[...fields];
          }));
          label.append(input,document.createTextNode(' Match approved '+field));host.appendChild(label);
        }
      }

      const diagnostics=document.createElement('div');
      diagnostics.className='legend-cms-signal-diagnostics';
      diagnostics.dataset.signalDiagnostics=binding.id;
      const intro=document.createElement('p');
      intro.textContent='Destination health and delivery evidence have not been checked for this mapping.';
      diagnostics.appendChild(intro);
      const actions=document.createElement('div');actions.className='legend-cms-row';
      const testButton=document.createElement('button');testButton.type='button';testButton.textContent='Run private test';
      testButton.dataset.signalTest=binding.id;testButton.addEventListener('click',()=>void runSignalDryRun(binding));
      const healthButton=document.createElement('button');healthButton.type='button';healthButton.textContent='Refresh delivery history';
      healthButton.dataset.signalHealth=binding.id;healthButton.addEventListener('click',()=>void loadSignalHealth(binding));
      actions.append(testButton,healthButton);diagnostics.appendChild(actions);host.appendChild(diagnostics);

      const remove=document.createElement('button');remove.type='button';remove.textContent='Remove mapping';
      remove.addEventListener('click',()=>void persistSelectedSignals(bindings.filter(value=>value.id!==binding.id)));
      host.appendChild(remove);
    }

    const add=document.createElement('button');add.type='button';add.textContent='Add advanced custom mapping';
    const automaticContract=(type==='FORM' && selected.matches?.('[data-website-inquiry]')) || !!managedAction;
    add.hidden=automaticContract;
    add.disabled=automaticContract || bindings.length>=8 || !candidates.length;
    add.addEventListener('click',()=>{
      const option=candidates.flatMap(candidate=>
        candidate.triggers.filter(trigger=>
          triggers.includes(trigger) && !bindings.some(binding=>binding.trigger===trigger))
          .map(trigger=>({event:candidate,trigger})))[0];
      if(!option){paragraph('All supported triggers for this element are already mapped.');return;}
      const next=cloneCanonicalValue(bindings);
      next.push({
        id:crypto.randomUUID().replaceAll('-',''),
        eventName:option.event.name,
        actionKey:option.event.actionKey,
        trigger:option.trigger,
        deliveryMode:'off',
        oncePerSession:true,
        matchingFields:[]
      });
      void persistSelectedSignals(next);
    });
    host.appendChild(add);
  }

  function applyTheme(theme) {
    const root = document.documentElement;
    const strings = {
      navy:'--web-navy', navyDeep:'--web-navy-deep', gold:'--web-gold', goldStrong:'--web-gold-strong',
      surface:'--web-surface', text:'--web-ink', muted:'--web-muted', fontFamily:'--web-font',
      surfaceElevated:'--web-surface-elevated', surfaceMuted:'--web-surface-muted',
      borderColor:'--web-border-color', shadowSoft:'--web-shadow-soft', shadowStrong:'--web-shadow-strong'
    };
    const pixels = {
      borderRadius:'--web-radius', displaySize:'--web-display-size', h1Size:'--web-h1-size',
      h2Size:'--web-h2-size', h3Size:'--web-h3-size', bodySize:'--web-body-size', smallSize:'--web-small-size',
      sectionSpace:'--web-section-space', contentGap:'--web-content-gap', contentMaxWidth:'--web-content-max',
      wideMaxWidth:'--web-wide-max', narrowMaxWidth:'--web-narrow-max', gutter:'--web-gutter',
      cardRadius:'--web-card-radius', buttonRadius:'--web-button-radius', inputRadius:'--web-input-radius',
      borderWidth:'--web-border-width', navHeight:'--web-nav-height'
    };
    const milliseconds = {
      motionFastMs:'--web-motion-fast', motionStandardMs:'--web-motion-standard', motionSlowMs:'--web-motion-slow'
    };
    if (theme?.navy) root.style.setProperty('--web-navy-royal', theme.navy);
    else root.style.removeProperty('--web-navy-royal');
    Object.entries(strings).forEach(([key, cssVar]) => {
      if (theme?.[key]) root.style.setProperty(cssVar, theme[key]);
      else root.style.removeProperty(cssVar);
    });
    Object.entries(pixels).forEach(([key, cssVar]) => {
      const value=Number(theme?.[key]);
      if(Number.isFinite(value) && value>=0) root.style.setProperty(cssVar,`${value}px`);
      else root.style.removeProperty(cssVar);
    });
    Object.entries(milliseconds).forEach(([key, cssVar]) => {
      const value=Number(theme?.[key]);
      if(Number.isFinite(value) && value>=0) root.style.setProperty(cssVar,`${value}ms`);
      else root.style.removeProperty(cssVar);
    });
    const lineHeight=Number(theme?.bodyLineHeight);
    if(Number.isFinite(lineHeight) && lineHeight>0) root.style.setProperty('--web-body-line-height',String(lineHeight));
    else root.style.removeProperty('--web-body-line-height');
    const base=Number(theme?.fontSize);
    if(Number.isFinite(base) && base>0) root.style.setProperty('--web-base-font-size',`${base}px`);
    else root.style.removeProperty('--web-base-font-size');
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

  function canonicalResponsiveRole(model, el = null) {
    const type=String(model?.type || '').toLowerCase();
    const tag=String(model?.tag || el?.tagName || '').toLowerCase();
    const classes=new Set(String(model?.className || el?.className || '').split(/\s+/).filter(Boolean));
    if(el?.closest?.('.site-header,.site-footer')) return 'shell';
    if(classes.has('eyebrow') || classes.has('kicker')) return 'kicker';
    if(classes.has('hero-copy') || classes.has('section-head')) return 'narrative';
    if(classes.has('actions')) return 'actions';
    if(classes.has('hero-mark') || classes.has('media') || classes.has('visual')) return 'media';
    if(classes.has('card-grid') || classes.has('steps') || classes.has('standard-grid')) return 'collection';
    if(classes.has('statement') || classes.has('pullquote') || classes.has('testimonial') || classes.has('proof') || classes.has('trust') || tag==='blockquote') return 'proof';
    if(type==='heading' || /^h[1-6]$/.test(tag)) return 'heading';
    if(type==='cta' || type==='link' || tag==='a' || tag==='button') return 'action';
    if(type==='image' || type==='video' || tag==='img' || tag==='video') return 'media';
    if(type==='form' || tag==='form') return 'form';
    if(type==='text') return 'copy';
    if(type==='section') return 'section';
    if(type==='container') return 'group';
    return 'content';
  }

  function canonicalResponsiveBodyElement(el) {
    return !!el?.closest?.('main') && !el.closest?.('.site-header,.site-footer');
  }

  function applyCanonicalResponsiveStyle(model, el, key, style, responsive) {
    if(!key) return style;
    const explicit=responsive && typeof responsive==='object' ? responsive : {};
    const has=field=>Object.hasOwn(explicit,field);
    const role=canonicalResponsiveRole(model,el);

    // Breakpoint values are authoritative when explicitly authored. Responsive
    // defaults only fill fields that are absent at the active breakpoint, so a
    // mobile edit never rewrites or masks desktop/base presentation.
    if(role==='shell'){
      const mobileShellChrome=key==='mobile' && (
        mobileHeaderChromeKind(model) ||
        el?.matches?.('.site-header,.brand,.brand-wordmark,.business-brand-banner,.nav') ||
        el?.closest?.('.nav,.brand,.brand-wordmark,.business-brand-banner')
      );
      if(key==='mobile'){
        if(!has('widthPercent')) delete style.widthPercent;
        if(!has('heightPx')) delete style.heightPx;
        if(!has('offsetXPercent')) style.offsetXPercent=0;
        if(!has('offsetYPx')) style.offsetYPx=0;
        if(mobileShellChrome){
          MOBILE_HEADER_GEOMETRY_FIELDS.forEach(field=>delete style[field]);
          style.offsetXPercent=0;
          style.offsetYPx=0;
        }
      }
      const brand=model?.systemBinding==='business_name' ||
        (SITE_KEY==='legend' && String(model?.tag || '').toLowerCase()==='strong' && String(model?.text || '').trim()==='LEGEND®');
      if(brand){
        const ceiling=key==='mobile' ? 1.35 : key==='tablet' ? 1.8 : 3.5;
        if(key==='mobile' || !has('fontScale'))
          style.fontScale=positiveNumber(style.fontScale) ? Math.min(ceiling,Number(style.fontScale)) : ceiling;
      }
      if(model?.systemKey==='primary_navigation'){
        const ceiling=key==='mobile' ? 1 : key==='tablet' ? 1.15 : 1.6;
        if(key==='mobile' || !has('fontScale'))
          style.fontScale=positiveNumber(style.fontScale) ? Math.min(ceiling,Number(style.fontScale)) : ceiling;
      }
      return style;
    }

    if(!canonicalResponsiveBodyElement(el)) return style;

    if(key==='mobile'){
      const flowRole=['section','kicker','heading','narrative','copy','proof','actions','action','form','collection','group','content'].includes(role);
      const mediaRole=role==='media';

      if(flowRole || mediaRole){
        if(!has('offsetXPercent')) style.offsetXPercent=0;
        if(!has('offsetYPx')) style.offsetYPx=0;
        if(!has('widthPercent')) style.widthPercent=100;
        if(role!=='embed' && !has('heightPx')) delete style.heightPx;
        if(!has('minWidthPx')) delete style.minWidthPx;
        if(!has('marginTop')) style.marginTop=0;
        if(!has('marginBottom')) style.marginBottom=0;
        if(!has('marginLeft')) style.marginLeft=0;
        if(!has('marginRight')) style.marginRight=0;
        if(!has('minHeightPx')) delete style.minHeightPx;
        if(!mediaRole && !has('maxWidthPx')) delete style.maxWidthPx;
        if(!mediaRole && !has('maxHeightPx')) delete style.maxHeightPx;
      } else {
        if(!has('offsetXPercent')) style.offsetXPercent=0;
        if(!has('offsetYPx')) style.offsetYPx=0;
      }
      if(!has('paddingLeft') && spacingNumber(style.paddingLeft)) style.paddingLeft=Math.min(28,Number(style.paddingLeft));
      if(!has('paddingRight') && spacingNumber(style.paddingRight)) style.paddingRight=Math.min(28,Number(style.paddingRight));
      if(!has('paddingTop') && spacingNumber(style.paddingTop)) style.paddingTop=Math.min(40,Number(style.paddingTop));
      if(!has('paddingBottom') && spacingNumber(style.paddingBottom)) style.paddingBottom=Math.min(40,Number(style.paddingBottom));
      if(!has('fontSize') && spacingNumber(style.fontSize)){
        const ceiling=role==='heading' ? 54 : role==='kicker' ? 14 : role==='action' ? 20 : 22;
        style.fontSize=Math.min(ceiling,Number(style.fontSize));
      }
      if(!has('fontScale') && positiveNumber(style.fontScale)){
        const ceiling=role==='heading' ? 1.35 : role==='kicker' ? 1.05 : role==='action' ? 1.05 : 1.15;
        style.fontScale=Math.min(ceiling,Number(style.fontScale));
      }
      if(mediaRole){
        if(!has('maxWidthPx')){
          const requestedMax=positiveNumber(style.maxWidthPx) ? Number(style.maxWidthPx) : 560;
          style.maxWidthPx=Math.min(560,requestedMax);
        }
        if(!has('maxHeightPx') && positiveNumber(style.maxHeightPx))
          style.maxHeightPx=Math.min(520,Number(style.maxHeightPx));
      }
    } else if(key==='tablet'){
      if(!has('offsetXPercent')) style.offsetXPercent=0;
      if(!has('offsetYPx')) style.offsetYPx=0;
      if(role==='action' && !has('widthPercent') && positiveNumber(style.widthPercent) && Number(style.widthPercent)<35)
        style.widthPercent=100;
    }

    return style;
  }

  function effectiveStyle(model, el = null) {
    const base = model?.style && typeof model.style === 'object' ? model.style : {};
    const key = activeBreakpoint();
    const responsive = key && model?.breakpointStyles && typeof model.breakpointStyles[key] === 'object' ? model.breakpointStyles[key] : null;
    const style = responsive ? { ...base, ...responsive } : { ...base };
    applyCanonicalResponsiveStyle(model,el,key,style,responsive);
    // Stored width and in-section position are independent. Rendering constrains
    // the effective width to the remaining section space, so movement stays free
    // without creating horizontal page overflow.
    const width = positiveNumber(style.widthPercent) ? Math.min(100, Number(style.widthPercent)) : null;
    if (width != null) style.widthPercent = width;
    if (style.offsetXPercent != null && Number.isFinite(Number(style.offsetXPercent)))
      style.offsetXPercent = Math.max(0, Math.min(95, Number(style.offsetXPercent)));
    return style;
  }

  function effectiveLayout(model, el = null) {
    const base = model?.layout && typeof model.layout === 'object' ? model.layout : { mode:'free' };
    const key = activeBreakpoint();
    const responsive = key && model?.breakpointLayouts && typeof model.breakpointLayouts[key] === 'object' ? model.breakpointLayouts[key] : null;
    const layout=responsive ? { ...base, ...responsive } : { ...base };
    if(key==='mobile' && (
      mobileHeaderChromeKind(model)==='frame' ||
      mobileHeaderChromeKind(model)==='navigation' ||
      el?.matches?.('.site-header,.nav')
    )) return {mode:'free',direction:'column'};
    if(!key || !canonicalResponsiveBodyElement(el)) return layout;
    const explicit=responsive && typeof responsive==='object' ? responsive : {};
    const has=field=>Object.hasOwn(explicit,field);
    const hasChildren=Array.isArray(model?.children) && model.children.length>0;

    if(key==='mobile' && hasChildren){
      if(!has('mode') && (layout.mode==='free' || !layout.mode)){
        layout.mode='stack';
        layout.direction='column';
      } else if(layout.mode==='grid'){
        if(!has('columns')) layout.columns=1;
      } else if(layout.mode==='stack'){
        if(!has('direction')) layout.direction='column';
      } else if(layout.mode==='flex' && !has('direction')){
        layout.direction='column';
      }
      if(layout.mode!=='free' && !has('gapPx')) layout.gapPx=Math.max(12,Math.min(28,Number(layout.gapPx)||18));
      if((layout.mode==='stack' || (layout.mode==='flex' && layout.direction!=='row')) && !has('alignItems')) layout.alignItems='stretch';
      if(layout.mode==='flex' && !has('wrap')) layout.wrap='nowrap';
    } else if(key==='tablet' && hasChildren){
      if(layout.mode==='grid' && !has('columns')) layout.columns=Math.min(2,Math.max(1,Number(layout.columns)||2));
      if(layout.mode==='flex' && layout.direction==='row' && !has('wrap')) layout.wrap='wrap';
    } else if(key==='desktop' && hasChildren){
      if(layout.mode==='grid' && !has('columns')) layout.columns=Math.min(4,Math.max(1,Number(layout.columns)||4));
      if(layout.mode==='flex' && layout.direction==='row' && !has('wrap')) layout.wrap='wrap';
    }
    return layout;
  }

  function applyCanonicalResponsiveAttributes(el, model) {
    if(!el) return;
    if(!canonicalResponsiveBodyElement(el)){
      delete el.dataset.legendContentRole;
      delete el.dataset.legendResponsiveFlow;
      return;
    }
    const role=canonicalResponsiveRole(model,el);
    el.dataset.legendContentRole=role;
    const flow=Array.isArray(model?.children) && model.children.length>0 &&
      ['section','container'].includes(String(model?.type || '').toLowerCase());
    if(flow) el.dataset.legendResponsiveFlow='true';
    else delete el.dataset.legendResponsiveFlow;
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
  function editingStyle(model, create = false) {
    if (!model) return null;
    if (editorBreakpointKey === 'base') {
      if (create) model.style ||= {};
      return model.style || {};
    }
    if (create) { model.breakpointStyles ||= {}; model.breakpointStyles[editorBreakpointKey] ||= {}; }
    return model.breakpointStyles?.[editorBreakpointKey] || {};
  }

  function editingLayout(model, create = false) {
    if (!model) return null;
    if (editorBreakpointKey === 'base') {
      if (create) model.layout ||= { mode:'free', direction:'column' };
      return model.layout || { mode:'free', direction:'column' };
    }
    if (create) { model.breakpointLayouts ||= {}; model.breakpointLayouts[editorBreakpointKey] ||= {}; }
    return model.breakpointLayouts?.[editorBreakpointKey] || {};
  }

  function forEachWebsiteModel(action) {
    const visit=nodes=>walkComposition(nodes,node=>action(node));
    visit(documentState.shell?.header || []);
    visit(documentState.shell?.footer || []);
    Object.values(documentState.pages || {}).forEach(page=>visit(page?.composition || []));
    Object.values(documentState.reusableComponents || {}).forEach(component=>visit(component?.composition || []));
    action(documentState.store?.storeNavigation);
    action(documentState.store?.cartNavigation);
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
    document.querySelectorAll('[data-editor-viewport]').forEach(button=>{
      const target=button.dataset.editorViewport;
      const pressed=target === (editorBreakpointKey==='mobile' ? 'mobile' : 'base');
      button.setAttribute('aria-pressed',pressed?'true':'false');
      button.classList.toggle('primary',pressed);
    });
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
    refreshResponsiveComposition();
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
    const sectionLocked = !!el.dataset.cmsSection;
    const horizontalOffset = !sectionLocked && Number.isFinite(Number(style.offsetXPercent))
      ? Math.max(0, Math.min(95, Number(style.offsetXPercent))) : 0;
    if (!sectionLocked && positiveNumber(style.widthPercent)) {
      const requestedWidth = Math.min(100, Number(style.widthPercent));
      const availableWidth = Math.max(5, 100 - horizontalOffset);
      el.style.width = `${Math.min(requestedWidth, availableWidth)}%`;
      el.style.maxWidth = `${availableWidth}%`;
    }
    if (positiveNumber(style.heightPx)) {
      if (sectionLocked) {
        // A manually resized section is the canvas boundary itself. Use an
        // explicit height so shrinking actually reclaims empty space; never
        // turn the section into an internal scroll container.
        el.style.minHeight = '0';
        el.style.height = `${style.heightPx}px`;
        el.style.overflow = 'visible';
      } else {
        el.style.height = `${style.heightPx}px`;
        // Ordinary website content never becomes its own scroll container.
        // Code frames remain clipped to their explicit sandbox frame.
        el.style.overflow = (el.classList.contains('legend-cms-embed') || el.classList.contains('legend-legacy-migration-code')) ? 'hidden' : 'visible';
      }
    }
    const hasOffsetX = !sectionLocked && style.offsetXPercent != null && Number.isFinite(Number(style.offsetXPercent));
    const hasOffsetY = !sectionLocked && style.offsetYPx != null && Number.isFinite(Number(style.offsetYPx));
    if (hasOffsetX || hasOffsetY) {
      el.style.position = 'relative';
      if (hasOffsetX) el.style.left = `${Number(style.offsetXPercent)}%`;
      if (hasOffsetY) el.style.top = `${Number(style.offsetYPx)}px`;
    }
    if (spacingNumber(style.paddingTop)) el.style.paddingTop = `${style.paddingTop}px`;
    if (spacingNumber(style.paddingBottom)) el.style.paddingBottom = `${style.paddingBottom}px`;
    ['color','backgroundColor','fontFamily','fontWeight','objectFit','borderColor','borderStyle','textTransform','textDecoration'].forEach(key => { if (style[key] != null && style[key] !== '') el.style[key] = style[key]; });
    if (style.backgroundGradient) el.style.backgroundImage=style.backgroundGradient;
    else if (style.backgroundColor) el.style.backgroundImage = 'none';
    ['fontSize','paddingLeft','paddingRight','borderRadius','borderWidth','minWidthPx','maxWidthPx','minHeightPx','maxHeightPx'].forEach(key => {
      if (spacingNumber(style[key])) {
        const cssKey={minWidthPx:'minWidth',maxWidthPx:'maxWidth',minHeightPx:'minHeight',maxHeightPx:'maxHeight'}[key] || key;
        el.style[cssKey]=`${style[key]}px`;
      }
    });
    if (Number.isFinite(Number(style.letterSpacing)))
      el.style.letterSpacing=`${Number(style.letterSpacing)}px`;
    ['marginTop','marginBottom','marginLeft','marginRight'].forEach(key => { if(Number.isFinite(Number(style[key]))) el.style[key]=`${Number(style[key])}px`; });
    if (positiveNumber(style.lineHeight)) el.style.lineHeight = String(style.lineHeight);
    if (style.opacity != null && Number.isFinite(Number(style.opacity))) el.style.opacity=String(style.opacity);
    if (positiveNumber(style.aspectRatio)) el.style.aspectRatio=String(style.aspectRatio);
    if (style.boxShadow) el.style.boxShadow=style.boxShadow;
    if (style.objectPosition && (el instanceof HTMLImageElement || el instanceof HTMLVideoElement)) el.style.objectPosition = style.objectPosition;
  }

  function setContentText(el, text, preserveWhitespace = false) {
    if (preserveWhitespace) el.dataset.cmsPreserveWhitespace = 'true';
    else delete el.dataset.cmsPreserveWhitespace;
    if (!el.children.length || !document.createTreeWalker) { el.textContent = text; return; }
    const walker = document.createTreeWalker(el, 4); const nodes = []; let node;
    while ((node = walker.nextNode())) if (node.textContent.trim() && !node.parentElement.closest('input,select,textarea,svg,i,[aria-hidden="true"]')) nodes.push(node);
    if (nodes.length) { nodes[0].textContent = text; nodes.slice(1).forEach(node => { node.textContent = ''; }); }
    else el.appendChild(document.createTextNode(text));
  }


  function compositionNodeForElement(el, create = true) {
    if (!el?.dataset?.cmsId || legacyMigration) return null;
    const id=el.dataset.cmsCompositionId || el.dataset.cmsId;
    const node=compositionNode(id);
    if (node && create) {
      node.style ||= {};
      node.signals ||= [];
      node.fieldSignals ||= {};
      node.children ||= [];
    }
    return node;
  }

  function storeControlPresentationForElement(el, create = true) {
    if (!el || legacyMigration) return null;
    const key=el.dataset?.legendStoreNav;
    if (key!=='store' && key!=='cart') return null;
    documentState.store ||= {};
    const field=key==='store' ? 'storeNavigation' : 'cartNavigation';
    if(create) documentState.store[field] ||= normalizeControlPresentation(null);
    return documentState.store[field] || null;
  }

  function formFieldKey(el) {
    if(!el) return null;
    const raw=el.dataset?.cmsFieldKey || el.getAttribute?.('name') || el.id || '';
    return safeId(raw);
  }

  function formFieldPresentationForElement(el, create = true) {
    if(!el || legacyMigration) return null;
    const form=el.closest?.('form[data-cms-composition-id]');
    if(!form || form===el) return null;
    const key=formFieldKey(el);
    if(!key) return null;
    const formNode=compositionNode(form.dataset.cmsCompositionId);
    if(!formNode || !(formNode.type==='form' || formNode.type==='experience' || String(formNode.systemKey || '').startsWith('protect_runtime_form:'))) return null;
    if(create){
      formNode.fieldPresentations ||= {};
      formNode.fieldPresentations[key] ||= normalizeControlPresentation(null);
    }
    return formNode.fieldPresentations?.[key] || null;
  }

  function editableWebsiteModelForElement(el, create = true) {
    return formFieldPresentationForElement(el, create) ||
      storeControlPresentationForElement(el, create) ||
      compositionNodeForElement(el, create);
  }

  function editableCompositionNodeForElement(el, create = true) {
    return compositionNodeForElement(el, create);
  }

  function isInlineEditable(el) {
    if (!el || el.dataset.cmsSignalOnly || el.dataset.cmsSection) return false;
    // Entity-owned names and commerce controls expose presentation editing only.
    // Their text/destination remains owned by the Business Profile / Store contract.
    if (el.hasAttribute?.('data-business-name') || el.hasAttribute?.('data-business-field') || el.dataset.legendStoreNav ||
        el.classList?.contains('legend-platform-attribution') ||
        el.classList?.contains('legend-platform-attribution-powered-label') ||
        el.classList?.contains('legend-platform-attribution-designed-label')) return false;
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
    const model = editableCompositionNodeForElement(el, false);
    if (model) {
      const value = inlineTextValue(el);
      model.text = value;
      setContentText(el, value, true);
    }
    el.removeAttribute?.('contenteditable');
    el.classList.remove('legend-cms-inline-editing');
    if (inlineEditNode === el) inlineEditNode = null;
    inlineEditCheckpointed = false;
    updateDirectCanvasUi();
  }

  function activateInlineEditing(el) {
    if (!isInlineEditable(el)) return;
    if (inlineEditNode && inlineEditNode !== el) deactivateInlineEditing(inlineEditNode);
    inlineEditNode = el;
    el.setAttribute('contenteditable', 'plaintext-only');
    el.setAttribute('spellcheck', 'true');
    el.classList.add('legend-cms-inline-editing');
    updateDirectCanvasUi();
    if (el.dataset.cmsInlineBound === 'true') return;
    el.dataset.cmsInlineBound = 'true';
    el.addEventListener('beforeinput', () => {
      checkpoint();
      inlineEditCheckpointed = true;
    });
    el.addEventListener('input', () => {
      if (!inlineEditCheckpointed) checkpoint();
      inlineEditCheckpointed = false;
      const model = editableCompositionNodeForElement(el);
      if (!model) return;
      const value = inlineTextValue(el);
      model.text = value;
      el.dataset.cmsPreserveWhitespace = 'true';
      markDirty();
      updateDirectCanvasUi();
    });
    el.addEventListener('blur', () => {
      if (inlineEditNode !== el) return;
      deactivateInlineEditing(el);
    });
  }

  const defaultCodeBlock = '<!doctype html><html lang="en"><head><meta name="viewport" content="width=device-width,initial-scale=1"><style>:root{--navy:#102b62;--navy-deep:#081a3a;--gold:#d4ad45;--gold-strong:#f0cf78;--ink:#101a35;--muted:#5b6680;--surface:#f7f8fb;--line:rgba(16,43,98,.14)}*{box-sizing:border-box}body{margin:0;background:linear-gradient(180deg,#fff,var(--surface));color:var(--ink);font:500 16px/1.6 system-ui,-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif;padding:clamp(14px,4vw,28px)}main{position:relative;overflow:hidden;max-width:760px;margin:auto;background:#fff;border:1px solid var(--line);border-radius:18px;padding:clamp(22px,5vw,38px);box-shadow:0 14px 38px rgba(8,26,58,.09)}main:before{content:"";position:absolute;inset:0 auto auto 0;width:100%;height:2px;background:linear-gradient(90deg,var(--gold),transparent 78%)}small{display:block;color:var(--gold);font-size:10px;font-weight:850;letter-spacing:.16em;text-transform:uppercase;margin-bottom:10px}h2{margin:0 0 10px;color:var(--navy-deep);font-size:clamp(27px,6vw,42px);line-height:1.06;letter-spacing:-.035em;overflow-wrap:break-word}p{margin:0;color:var(--muted);overflow-wrap:break-word}</style></head><body><main><small>Custom content</small><h2>Build something distinctive.</h2><p>Edit this sandboxed block with responsive, accessible presentation that belongs to this website.</p></main></body></html>';

  function secureEmbedSource(source) {
    const policy="default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src data: https:; media-src data: https:; font-src data:; connect-src 'none'; form-action 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'";
    const meta='<meta http-equiv="Content-Security-Policy" content="'+policy+'">';
    if(/<head(?:\s[^>]*)?>/i.test(source)) return source.replace(/<head(?:\s[^>]*)?>/i,match=>match+meta);
    if(/<html(?:\s[^>]*)?>/i.test(source)) return source.replace(/<html(?:\s[^>]*)?>/i,match=>match+'<head>'+meta+'</head>');
    return '<!doctype html><html><head>'+meta+'</head><body>'+source+'</body></html>';
  }

  function renderCodePreview(el, extra) {
    const frame = el?.querySelector?.('iframe[data-cms-code-frame]');
    if (!frame) return;
    const source = extra?.text?.trim() ? extra.text : defaultCodeBlock;
    frame.src = 'data:text/html;charset=utf-8,' + encodeURIComponent(secureEmbedSource(source));
  }

  function compositionMediaAssetId(value) {
    if (!value) return null;
    try {
      const url = new URL(value, API_BASE);
      const match = url.pathname.match(/^\/api\/website-content\/media\/([a-f0-9-]{36})$/i);
      return match ? match[1] : null;
    } catch { return null; }
  }

  // READ-ONLY PRE-V3 COMPATIBILITY BOUNDARY.
  // Used only to render/migrate persisted historical JSON and old immutable versions.
  // These helpers never mutate documentState and are never reachable from writable editor controls.
  function legacyMigrationPageState() {
    const pages=legacyMigration?.pages && typeof legacyMigration.pages==='object' ? legacyMigration.pages : {};
    const route=currentPageRoute();
    return legacyPageForRoute(route) || pages[pageKey] || (route==='/' ? pages.home : null) ||
      {elements:{},sectionOrder:{},extras:[],navigation:{showInNavigation:true,order:0,isDeleted:false}};
  }

  function legacyMigrationExtraById(id) {
    const page=legacyMigrationPageState();
    return (page.extras || []).find(extra=>extra?.id===id) ||
      (legacyMigration?.extras || []).find(extra=>extra?.id===id) || null;
  }

  // BEGIN ONE-WAY LEGACY V2 -> CANONICAL V3 MIGRATION.
  // These helpers only render the separately supplied read-only legacyMigration
  // envelope long enough to materialize the effective website into v3. They never
  // write legacy fields into documentState, Site Source, drafts, or published v3.
  function legacyMigrationRecordForElement(el) {
    if (!el || !legacyMigration) return null;
    if (el.dataset.cmsExtraId && !el.dataset.cmsExtraField)
      return legacyMigrationExtraById(el.dataset.cmsExtraId);
    if (isSharedShellElement(el))
      return legacyMigration.elements?.[el.dataset.cmsId] || null;
    return legacyMigrationPageState().elements?.[el.dataset.cmsId] ||
      legacyMigration.elements?.[el.dataset.cmsId] || null;
  }

  function legacyMigrationReusableDefinition(instance) {
    return instance?.type==='reusable' && instance.syncSourceId
      ? legacyMigration?.reusableComponents?.[instance.syncSourceId] || null
      : null;
  }

  function legacyRecordAsCanonicalModel(record) {
    const model=cloneCanonicalValue(record || {});
    if(!model.mediaUrl) model.mediaUrl=model.imageDataUrl || model.videoUrl || null;
    delete model.imageDataUrl;
    delete model.videoUrl;
    delete model.placement;
    delete model.templateSectionId;
    delete model.sectionId;
    return model;
  }

  function applyLegacyMigrationRecord(el, record) {
    applyCompositionNode(el, legacyRecordAsCanonicalModel(record));
  }

  function renderLegacyMigrationReusableInstance(wrapper,instance) {
    if(!wrapper || !instance) return;
    wrapper.replaceChildren();
    const definition=legacyMigrationReusableDefinition(instance);
    if(!definition) return;
    const values=Array.isArray(definition.extras)?definition.extras:[];
    const root=values.find(value=>value.sectionId==='component.root') || values[0] || null;
    const sectionMap=new Map([['component.root',wrapper]]);
    if(definition.kind==='section' && root?.type==='section'){
      sectionMap.set('extra:'+root.id,wrapper);
      applyStyle(wrapper,{...effectiveStyle(root),...effectiveStyle(instance)});
      applyLayout(wrapper,{...effectiveLayout(root),...effectiveLayout(instance)});
    }else{
      applyStyle(wrapper,effectiveStyle(instance));
      applyLayout(wrapper,effectiveLayout(instance));
    }
    for(const item of values.filter(value=>value.type==='section' && value!==root)){
      const parent=sectionMap.get(item.sectionId)||wrapper;
      const node=buildLegacyExtraNode(item,false,instance.id);
      node.classList.add('legend-cms-reusable-child');
      parent.appendChild(node);
      sectionMap.set('extra:'+item.id,node);
      applyStyle(node,effectiveStyle(item));
      applyLayout(node,effectiveLayout(item));
    }
    for(const item of values.filter(value=>value.type!=='section')){
      if(definition.kind==='block' && root && item!==root) continue;
      const parent=sectionMap.get(item.sectionId)||wrapper;
      const node=buildLegacyExtraNode(item,false,instance.id);
      node.classList.add('legend-cms-reusable-child');
      parent.appendChild(node);
      applyLegacyMigrationRecord(node,item);
    }
  }

  function createLegacyMigrationExtra(extra) {
    const section=extra.type==='section'
      ? document.querySelector('main')
      : document.querySelector('[data-cms-section="'+CSS.escape(extra.sectionId)+'"]');
    if(!section) return null;
    if(extra.type==='reusable'){
      const definition=legacyMigrationReusableDefinition(extra);
      const el=document.createElement(definition?.kind==='section'?'section':'div');
      el.className='legend-legacy-migration-node cms-reusable-instance';
      el.dataset.cmsExtraId=extra.id;
      el.dataset.cmsId='extra:'+extra.id;
      if(extra.hidden===true) el.hidden=true;
      section.appendChild(el);
      renderLegacyMigrationReusableInstance(el,extra);
      return el;
    }
    const el=buildLegacyExtraNode(extra,true);
    section.appendChild(el);
    applyLegacyMigrationRecord(el,extra);
    return el;
  }

  function applyLegacyMigrationSectionOrder() {
    const order=legacyMigrationPageState().sectionOrder || {};
    const sections=pageLayerSections();
    const original=new Map(sections.map((section,index)=>[section,index]));
    sections.sort((left,right)=>{
      const a=order[left.dataset.cmsSection];
      const b=order[right.dataset.cmsSection];
      return (Number.isFinite(Number(a))?Number(a):original.get(left))-
        (Number.isFinite(Number(b))?Number(b):original.get(right));
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

  function applyLegacyMigrationPreview() {
    if(!legacyMigration) return;
    const page=legacyMigrationPageState();
    document.querySelectorAll('.legend-legacy-migration-node').forEach(node=>node.remove());

    const extras=[...(legacyMigration.extras || []),...(page.extras || [])];
    extras.filter(extra=>extra.type==='section').forEach(createLegacyMigrationExtra);
    extras.filter(extra=>extra.type!=='section').forEach(createLegacyMigrationExtra);

    for(const [id,legacyRecord] of Object.entries(legacyMigration.elements || {}))
      applyLegacyMigrationRecord(findEditableElement(id),legacyRecord);
    for(const [id,legacyRecord] of Object.entries(page.elements || {}))
      applyLegacyMigrationRecord(findEditableElement(id),legacyRecord);

    applyLegacyMigrationSectionOrder();
    for(const [id,legacyRecord] of Object.entries(page.elements || {}))
      applyLegacyMigrationPlacement(findEditableElement(id),legacyRecord.placement);
    for(const extra of extras)
      applyLegacyMigrationPlacement(document.querySelector('[data-cms-id="extra:'+CSS.escape(extra.id)+'"]'),extra.placement);
  }

  function cleanCompositionClassName(el) {
    return [...(el?.classList || [])]
      .filter(name => name && !['legend-cms-inline-editing','legend-cms-selected','legend-cms-snap-x','legend-cms-snap-y'].includes(name))
      .join(' ') || null;
  }

  function canonicalMaterializationIds(excludePageRoute = currentPageRoute(), includeShell = true) {
    const used=new Set();
    const addNodes=nodes=>walkComposition(nodes,node=>{const id=safeId(node?.id);if(id)used.add(id);});
    if(includeShell){
      addNodes(documentState.shell?.header || []);
      addNodes(documentState.shell?.footer || []);
    }
    for(const [route,page] of Object.entries(documentState.pages || {}))
      if(normalizePageRoute(route)!==normalizePageRoute(excludePageRoute)) addNodes(page?.composition || []);
    for(const component of Object.values(documentState.reusableComponents || {}))
      addNodes(component?.composition || []);
    return used;
  }

  function createMaterializationIdentityContext(excludePageRoute = currentPageRoute(), includeShell = true) {
    const pending=new Map();
    document.querySelectorAll('[data-cms-id]').forEach(el=>{
      const id=safeId(el.dataset.cmsId);
      if(id) pending.set(id,(pending.get(id)||0)+1);
    });
    return {used:canonicalMaterializationIds(excludePageRoute,includeShell),pending};
  }

  function claimMaterializationId(preferred, identity) {
    const base=safeId(preferred) || 'node';
    let candidate=base, suffix=2;
    while(identity.used.has(candidate) || (identity.pending.get(candidate)||0)>0)
      candidate=safeId(base.slice(0,Math.max(1,154-String(suffix).length))+'.m'+suffix++);
    identity.used.add(candidate);
    return candidate;
  }

  function materializedNodeId(el, fallbackId, identity) {
    const existing=safeId(el.dataset.cmsId);
    if(existing){
      const remaining=Math.max(0,(identity.pending.get(existing)||0)-1);
      if(remaining) identity.pending.set(existing,remaining); else identity.pending.delete(existing);
      if(!identity.used.has(existing)){
        identity.used.add(existing);
        return existing;
      }
    }
    const id=claimMaterializationId(fallbackId || pageKey + '.' + safeId(el.tagName) + '.node',identity);
    el.dataset.cmsId=id;
    el.dataset.cmsEditable='true';
    rememberOriginal(el);
    return id;
  }

  function materializeCompositionNode(el, fallbackId = null, identity = null) {
    if (!(el instanceof HTMLElement) || el.closest('.legend-cms-editor')) return null;
    identity ||= createMaterializationIdentityContext();
    // Template decoration is deliberately presentation-only. Persisting SVG/icon
    // wrappers as ordinary v3 nodes creates empty colored boxes after the SVG is
    // filtered from the safe composition grammar.
    if (el.matches?.('[data-cms-decoration="true"]')) return null;
    // Menu toggles are runtime shell chrome, not editable website links. Keeping
    // them in v3 created invalid link nodes with no action/destination and made
    // publish fail. Runtime recreates and wires this control from primary nav.
    if (el.matches?.('[data-public-nav-toggle],.nav-toggle')) return null;
    const tag = el.tagName.toLowerCase();
    if (['script','style','noscript','input','select','textarea'].includes(tag)) return null;

    const id = materializedNodeId(el, fallbackId, identity);
    const model = legacyMigrationRecordForElement(el) || {};
    const actionKey = model.actionKey || el.dataset.websiteActionKey || null;
    const extra = el.dataset.cmsExtraId ? legacyMigrationExtraById(el.dataset.cmsExtraId) : null;

    if (el.matches?.('[data-legend-public-inquiry-form]') ||
        (tag === 'form' && el.matches('[data-website-inquiry]'))) {
      return {
        id, type:'form', tag:'form', className:cleanCompositionClassName(el),
        title:el.querySelector?.('legend')?.textContent || extra?.title || 'Send an inquiry',
        text:el.querySelector?.('button[type="submit"]')?.textContent || extra?.text || 'Send inquiry',
        systemKey:'canonical_inquiry', signals:cloneCanonicalValue(model.signals || extra?.signals || []),
        style:cloneCanonicalValue(model.style || extra?.style || {}),
        breakpointStyles:cloneCanonicalValue(model.breakpointStyles || extra?.breakpointStyles || {}),
        layout:cloneCanonicalValue(model.layout || extra?.layout || {}),
        breakpointLayouts:cloneCanonicalValue(model.breakpointLayouts || extra?.breakpointLayouts || {}),
        animations:cloneCanonicalValue(model.animations || extra?.animations || []),
        dataBinding:null,
        children:[]
      };
    }

    if (tag === 'form' && SITE_KEY === 'protect' && el.dataset.formKey) {
      const formKey=safeId(el.dataset.formKey || el.id || id) || 'runtime';
      const runtimeNode={
        id, type:'container', tag:'div', className:cleanCompositionClassName(el),
        systemKey:'protect_runtime_form:'+formKey,
        signals:[],
        style:cloneCanonicalValue(model.style || {}),
        breakpointStyles:cloneCanonicalValue(model.breakpointStyles || {}),
        layout:cloneCanonicalValue(model.layout || {}),
        breakpointLayouts:cloneCanonicalValue(model.breakpointLayouts || {}),
        animations:cloneCanonicalValue(model.animations || []),
        dataBinding:null,
        children:[]
      };
      runtimeNode.fieldLabels=cloneCanonicalValue(model.fieldLabels || {});
      runtimeNode.fieldPresentations=cloneCanonicalValue(model.fieldPresentations || {});
      for(const control of el.querySelectorAll('input:not([type="hidden"])[name],select[name],textarea[name]')) {
        const key=safeId(control.dataset.cmsFieldKey || control.name);
        if(!key) continue;
        runtimeNode.fieldPresentations[key] ||= {style:{},breakpointStyles:{},layout:{},breakpointLayouts:{}};
      }
      let childIndex=0;
      for(const child of [...el.children].filter(child =>
        child instanceof HTMLElement &&
        !['input','select','textarea','script','style','noscript'].includes(child.tagName.toLowerCase()))) {
        const childNode=materializeCompositionNode(child,id+'.child.'+(++childIndex),identity);
        if(childNode) runtimeNode.children.push(childNode);
      }
      return runtimeNode;
    }

    if (extra?.type === 'code' && el.classList.contains('legend-legacy-migration-code')) {
      return {
        id, type:'embed', tag:'div', className:cleanCompositionClassName(el),
        text:extra.text || defaultCodeBlock, signals:cloneCanonicalValue(extra.signals || []),
        style:cloneCanonicalValue(extra.style || {}), breakpointStyles:cloneCanonicalValue(extra.breakpointStyles || {}),
        layout:cloneCanonicalValue(extra.layout || {}), breakpointLayouts:cloneCanonicalValue(extra.breakpointLayouts || {}),
        animations:cloneCanonicalValue(extra.animations || []), dataBinding:cloneCanonicalValue(extra.dataBinding || null), children:[]
      };
    }

    if (extra?.type === 'reusable' && extra.syncSourceId) {
      return {
        id, type:'reusable', tag:'div', className:cleanCompositionClassName(el),
        syncSourceId:extra.syncSourceId, signals:[],
        style:cloneCanonicalValue(extra.style || {}), breakpointStyles:cloneCanonicalValue(extra.breakpointStyles || {}),
        layout:cloneCanonicalValue(extra.layout || {}), breakpointLayouts:cloneCanonicalValue(extra.breakpointLayouts || {}),
        animations:cloneCanonicalValue(extra.animations || []), dataBinding:cloneCanonicalValue(extra.dataBinding || null), children:[]
      };
    }

    const runtimeFormAncestor = SITE_KEY === 'protect'
      ? el.closest?.('form[data-form-key]:not([data-website-inquiry])')
      : null;
    const runtimePresentationControl = !!runtimeFormAncestor && runtimeFormAncestor !== el &&
      (tag === 'a' || tag === 'button');
    const rawHref = tag === 'a' ? el.getAttribute('href') : model.href;
    const dynamicHref = model.dataBinding?.target === 'href' || extra?.dataBinding?.target === 'href';
    const unmanagedInteractiveControl =
      (tag === 'button' && !actionKey) ||
      (tag === 'a' && !actionKey && !dynamicHref && (!rawHref || rawHref === '#'));
    const presentationOnlyControl = runtimePresentationControl || unmanagedInteractiveControl;

    let type = 'text';
    if (tag === 'section') type='section';
    else if (['div','article','header','footer','nav','ul','ol','fieldset'].includes(tag)) type='container';
    else if (/^h[1-6]$/.test(tag)) type='heading';
    else if (tag === 'a' || tag === 'button') type=presentationOnlyControl ? 'text' : (actionKey ? 'cta' : 'link');
    else if (tag === 'img') type='image';
    else if (tag === 'video') type='video';

    // Runtime/UI-only controls are presentation projections only. Their actual
    // behavior stays with the server/runtime owner. Materialization must never
    // manufacture a publishable link from an unmanaged button or empty anchor.
    const projectedTag = presentationOnlyControl ? 'span' : tag;
    const node = {
      id, type, tag:projectedTag, className:cleanCompositionClassName(el),
      actionKey:presentationOnlyControl ? null : (actionKey || null),
      href:presentationOnlyControl ? null : (rawHref || null),
      target:presentationOnlyControl ? null : ((tag === 'a' ? el.getAttribute('target') : model.target) || null),
      alt:(tag === 'img' || tag === 'video')
        ? (el.hasAttribute('alt') ? el.getAttribute('alt') : (model.alt ?? null))
        : null,
      hidden:el.hidden === true ? true : (model.hidden === false ? false : null),
      signals:presentationOnlyControl ? [] : cloneCanonicalValue(model.signals || []),
      style:cloneCanonicalValue(model.style || {}),
      breakpointStyles:cloneCanonicalValue(model.breakpointStyles || {}),
      layout:cloneCanonicalValue(model.layout || {}),
      breakpointLayouts:cloneCanonicalValue(model.breakpointLayouts || {}),
      animations:cloneCanonicalValue(model.animations || []),
      dataBinding:presentationOnlyControl ? null : cloneCanonicalValue(model.dataBinding || null),
      children:[]
    };

    if (tag === 'nav' && (el.id === 'primary-nav' || el.hasAttribute('data-public-nav')))
      node.systemKey='primary_navigation';
    const boundName = el.hasAttribute('data-business-name')
      ? el
      : ((type==='text' || type==='heading') ? el.querySelector?.('[data-business-name]') : null);
    const boundField = el.hasAttribute('data-business-field')
      ? el
      : ((type==='text' || type==='heading') ? el.querySelector?.('[data-business-field]') : null);
    if (boundName) node.systemBinding='business_name';
    else if (boundField)
      node.systemBinding='business_field:' + boundField.getAttribute('data-business-field');

    if (type === 'image' || type === 'video') {
      const raw = type === 'image'
        ? (model.imageDataUrl || el.getAttribute('src') || '')
        : (model.videoUrl || el.getAttribute('src') || '');
      node.mediaAssetId = compositionMediaAssetId(raw);
      node.mediaUrl = raw || null;
    }

    const childElements = [...el.children].filter(child =>
      child instanceof HTMLElement &&
      !['svg','i','script','style','noscript'].includes(child.tagName.toLowerCase()) &&
      !child.closest('.legend-cms-editor'));

    if (['section','container','cta','link'].includes(type) && childElements.length) {
      let childIndex = 0;
      for (const child of childElements) {
        const childNode = materializeCompositionNode(child, id + '.child.' + (++childIndex));
        if (childNode) node.children.push(childNode);
      }
      const directText = [...el.childNodes]
        .filter(child => child.nodeType === 3 && String(child.textContent || '').trim())
        .map(child => String(child.textContent || '').replace(/\r/g,''))
        .join(' ')
        .trim();
      if (directText) node.children.unshift({
        id:claimMaterializationId(id + '.text',identity),type:'text',tag:'span',text:directText,className:null,
        signals:[],style:{},breakpointStyles:{},layout:{},breakpointLayouts:{},animations:[],children:[]
      });
    } else if (!['image','video','section','container'].includes(type)) {
      node.text = inlineTextValue(el);
    }

    return node;
  }

  function materializeCurrentPageComposition(identity = createMaterializationIdentityContext()) {
    const main = document.querySelector('main');
    if (!main) return [];
    const nodes = [];
    let index = 0;
    for (const child of [...main.children]) {
      const node = materializeCompositionNode(child, pageKey + '.root.' + (++index), identity);
      if (node) nodes.push(node);
    }
    return nodes;
  }

  function materializeCurrentShell(identity = createMaterializationIdentityContext(currentPageRoute(), false)) {
    const header=document.querySelector('.site-header');
    const footer=document.querySelector('.site-footer');
    const headerNode=header ? materializeCompositionNode(header,'shell.header',identity) : null;
    const footerNode=footer ? materializeCompositionNode(footer,'shell.footer',identity) : null;
    return {
      header:headerNode ? [headerNode] : [],
      footer:footerNode ? [footerNode] : []
    };
  }

  function safeCompositionTag(node) {
    const type=String(node?.type || 'text');
    const tag=String(node?.tag || '').toLowerCase();
    const allowed={
      section:['section'],container:['div','article','header','footer','nav','ul','ol','fieldset'],
      heading:['h1','h2','h3','h4','h5','h6'],text:['p','span','small','strong','li','label','blockquote'],
      cta:['a','button'],link:['a','button'],image:['img'],video:['video'],form:['form'],experience:['form'],embed:['div'],spacer:['div'],reusable:['div']
    }[type] || ['div'];
    return allowed.includes(tag) ? tag : allowed[0];
  }

  function applyFormFieldPresentations(form,node) {
    if(!form || !node) return;
    const presentations=node.fieldPresentations ||= {};
    const labels=node.fieldLabels || {};
    const controls=[...form.querySelectorAll('input:not([type="hidden"])[name],select[name],textarea[name],button[data-cms-field-key]')];
    controls.forEach((control,index)=>{
      const key=safeId(control.dataset.cmsFieldKey || control.getAttribute('name') || control.id || (control.matches('button[type="submit"]')?'submit':'field-'+index));
      if(!key) return;
      if(String(node.systemKey || '').startsWith('protect_runtime_form:') && !Object.hasOwn(presentations,key)) {
        presentations[key]=normalizeControlPresentation(null);
        if(editorMode) { templateRepairPending=true; dirty=true; }
      }
      control.dataset.cmsFieldKey=key;
      control.dataset.cmsSignalOnly='true';
      control.dataset.cmsEditable='true';
      const presentation=presentations[key];
      if(presentation){
        applyStyle(control,effectiveStyle(presentation));
        applyLayout(control,effectiveLayout(presentation));
      }
      if(control.matches('button[type="submit"]') && labels[key]) setContentText(control,labels[key],true);
      const label=control.labels?.[0] || control.closest('label');
      if(label && labels[key]) setContentText(label,labels[key],true);
    });
  }

  function experienceValue(raw) {
    if(raw == null) return null;
    if(typeof raw==='boolean' || typeof raw==='number') return raw;
    const text=String(raw);
    const numeric=Number(text);
    return text!=='' && Number.isFinite(numeric) ? numeric : text;
  }

  function experienceTruthy(value) {
    if(typeof value==='boolean') return value;
    if(typeof value==='number') return value!==0;
    return ['true','yes','on','1'].includes(String(value ?? '').trim().toLowerCase());
  }

  function evaluateExperienceExpression(expression, answers, calculations, stack=new Set()) {
    if(!expression || typeof expression!=='object') return null;
    const values=Array.isArray(expression.values)
      ? expression.values.map(item=>evaluateExperienceExpression(item,answers,calculations,stack))
      : [];
    const number=value=>{ const parsed=Number(value); return Number.isFinite(parsed)?parsed:0; };
    const compare=()=>{
      if(values.length<2) return 0;
      const a=Number(values[0]), b=Number(values[1]);
      if(Number.isFinite(a)&&Number.isFinite(b)) return a===b?0:(a>b?1:-1);
      return String(values[0]??'').localeCompare(String(values[1]??''),undefined,{sensitivity:'accent'});
    };
    switch(String(expression.op||'value')){
      case 'value': return expression.value ?? null;
      case 'ref': {
        let key=String(expression.ref||'');
        if(key.startsWith('answer.')) key=key.slice(7);
        if(key.startsWith('calc.')){
          const calcKey=key.slice(5);
          if(stack.has(calcKey) || !calculations?.[calcKey]) return null;
          stack.add(calcKey);
          try{return evaluateExperienceExpression(calculations[calcKey],answers,calculations,stack);}
          finally{stack.delete(calcKey);}
        }
        return Object.hasOwn(answers,key)?answers[key]:null;
      }
      case 'add': return values.reduce((sum,value)=>sum+number(value),0);
      case 'subtract': return values.length?values.slice(1).reduce((sum,value)=>sum-number(value),number(values[0])):0;
      case 'multiply': return values.length?values.reduce((sum,value)=>sum*number(value),1):0;
      case 'divide': return values.length?values.slice(1).reduce((sum,value)=>number(value)===0?NaN:sum/number(value),number(values[0])):0;
      case 'min': return values.length?Math.min(...values.map(number)):0;
      case 'max': return values.length?Math.max(...values.map(number)):0;
      case 'round': {
        const digits=Math.max(0,Math.min(6,Math.trunc(number(values[1]))||0));
        const factor=10**digits; return Math.round(number(values[0])*factor)/factor;
      }
      case 'percent': return number(values[0])/100;
      case 'equals': return compare()===0;
      case 'not_equals': return compare()!==0;
      case 'greater_than': return compare()>0;
      case 'less_than': return compare()<0;
      case 'greater_or_equal': return compare()>=0;
      case 'less_or_equal': return compare()<=0;
      case 'and': return values.every(experienceTruthy);
      case 'or': return values.some(experienceTruthy);
      case 'not': return !experienceTruthy(values[0]);
      case 'if': return experienceTruthy(values[0])?values[1]:values[2];
      case 'coalesce': return values.find(value=>value!=null && String(value)!=='') ?? null;
      case 'concat': return values.map(value=>String(value??'')).join('');
      case 'lookup': {
        const key=String(values[0]??'');
        return expression.map && Object.hasOwn(expression.map,key) ? expression.map[key] : null;
      }
      default: return null;
    }
  }

  function experienceAnswers(form, definition) {
    const result={};
    for(const control of definition?.controls || []){
      if(['button','cta'].includes(control.type)) continue;
      const controls=[...form.querySelectorAll('[data-cms-field-key="'+CSS.escape(control.key)+'"]')];
      if(!controls.length) continue;
      if(control.type==='checkbox') result[control.key]=!!controls[0].checked;
      else if(['radio','choice'].includes(control.type)){
        const selected=controls.find(input=>input.checked);
        result[control.key]=selected?.value ?? '';
      }else result[control.key]=experienceValue(controls[0].value);
    }
    return result;
  }

  function formatExperienceResult(value, format) {
    const number=Number(value);
    if(!Number.isFinite(number)) return String(value ?? '');
    if(format==='currency') return new Intl.NumberFormat(undefined,{style:'currency',currency:'USD',maximumFractionDigits:0}).format(number);
    if(format==='percent') return number.toLocaleString(undefined,{maximumFractionDigits:2})+'%';
    if(format==='integer') return Math.round(number).toLocaleString();
    return number.toLocaleString(undefined,{maximumFractionDigits:2});
  }

  function buildNativeExperience(node) {
    const definition=node.experience || {};
    const form=document.createElement('form');
    form.className='legend-native-experience';
    form.dataset.websiteExperienceForm='';
    form.dataset.websiteExperienceId=node.id;
    form.dataset.submitCapability=definition.submitCapability || '';
    form.dataset.formKey='experience:'+node.id;
    form.noValidate=false;
    if(editorMode) form.dataset.preview='';

    const header=document.createElement('header');
    header.className='legend-experience-header';
    if(node.title){
      const heading=document.createElement('h2');
      heading.textContent=node.title;
      header.appendChild(heading);
    }
    if(node.text){
      const copy=document.createElement('p');
      copy.textContent=node.text;
      header.appendChild(copy);
    }
    if(header.childNodes.length) form.appendChild(header);

    const progress=document.createElement('div');
    progress.className='legend-experience-progress';
    const progressBar=document.createElement('span');
    progress.appendChild(progressBar);
    form.appendChild(progress);

    const body=document.createElement('div');
    body.className='legend-experience-body';
    form.appendChild(body);

    const controlHosts=new Map();
    const buildControl=control=>{
      const wrapper=document.createElement('div');
      wrapper.className='legend-experience-control';
      wrapper.dataset.experienceControl=control.key;

      if(control.type==='button' || control.type==='cta'){
        const action=control.action || {};
        const element=control.type==='cta' ? document.createElement('a') : document.createElement('button');
        if(element.tagName==='BUTTON') element.type=action.type==='submit' && definition.submitCapability==='lead_capture' ? 'submit' : 'button';
        else element.href='#';
        element.className='btn '+(action.type==='back'?'secondary':'primary');
        element.textContent=control.label || (action.type==='submit'?'Submit':'Continue');
        element.dataset.experienceAction=action.type || (control.type==='cta'?'cta':'next');
        element.dataset.experienceControlKey=control.key;
        element.dataset.cmsFieldKey=control.key;
        if(action.targetStep) element.dataset.experienceTargetStep=action.targetStep;
        if(action.type==='cta' && action.actionKey) element.dataset.websiteActionKey=action.actionKey;
        wrapper.appendChild(element);
        return wrapper;
      }

      const label=document.createElement('label');
      label.className='legend-experience-field';
      if(control.label){
        const title=document.createElement('span');
        title.className='legend-experience-label';
        title.textContent=control.label;
        label.appendChild(title);
      }

      const applyCommon=input=>{
        input.name=control.key;
        input.dataset.cmsFieldKey=control.key;
        if(control.required) input.required=true;
        if(control.placeholder) input.placeholder=control.placeholder;
        if(control.maxLength) input.maxLength=Number(control.maxLength);
        if(control.min!=null) input.min=String(control.min);
        if(control.max!=null) input.max=String(control.max);
        if(control.step!=null) input.step=String(control.step);
      };

      if(control.type==='textarea'){
        const input=document.createElement('textarea');
        input.rows=4; applyCommon(input);
        if(control.defaultValue!=null) input.value=String(control.defaultValue);
        label.appendChild(input);
      }else if(control.type==='select'){
        const input=document.createElement('select'); applyCommon(input);
        if(!control.required){const empty=document.createElement('option');empty.value='';empty.textContent='Select';input.appendChild(empty);}
        for(const option of control.options || []){const el=document.createElement('option');el.value=option.value;el.textContent=option.label;input.appendChild(el);}
        if(control.defaultValue!=null) input.value=String(control.defaultValue);
        label.appendChild(input);
      }else if(control.type==='radio' || control.type==='choice'){
        const group=document.createElement('div');
        group.className='legend-experience-options';
        for(const option of control.options || []){
          const optionLabel=document.createElement('label');
          optionLabel.className='legend-experience-option';
          const input=document.createElement('input');
          input.type='radio'; applyCommon(input); input.value=option.value;
          if(control.defaultValue!=null && String(control.defaultValue)===String(option.value)) input.checked=true;
          const text=document.createElement('span'); text.textContent=option.label;
          optionLabel.append(input,text); group.appendChild(optionLabel);
        }
        label.appendChild(group);
      }else{
        const input=document.createElement('input');
        const types={email:'email',tel:'tel',number:'number',currency:'number',range:'range',checkbox:'checkbox',date:'date',text:'text'};
        input.type=types[control.type] || 'text'; applyCommon(input);
        if(control.type==='currency') input.inputMode='decimal';
        if(control.type==='tel') input.inputMode='tel';
        if(control.type==='checkbox') input.checked=control.defaultValue===true;
        else if(control.defaultValue!=null) input.value=String(control.defaultValue);
        label.appendChild(input);
      }

      if(control.helpText){
        const help=document.createElement('small');
        help.className='legend-experience-help';
        help.textContent=control.helpText;
        label.appendChild(help);
      }
      wrapper.appendChild(label);
      return wrapper;
    };

    const controlsByKey=new Map((definition.controls || []).map(control=>[control.key,control]));
    const steps=Array.isArray(definition.steps) && definition.steps.length
      ? definition.steps
      : [{key:'main',title:null,description:null,controlKeys:[...controlsByKey.keys()]}];
    const stepHosts=new Map();

    for(const step of steps){
      const section=document.createElement('section');
      section.className='legend-experience-step';
      section.dataset.experienceStep=step.key;
      if(step.title){const title=document.createElement('h3');title.textContent=step.title;section.appendChild(title);}
      if(step.description){const copy=document.createElement('p');copy.className='legend-experience-step-copy';copy.textContent=step.description;section.appendChild(copy);}
      const grid=document.createElement('div');grid.className='legend-experience-grid';section.appendChild(grid);
      for(const key of step.controlKeys || []){
        const control=controlsByKey.get(key);
        if(!control) continue;
        const wrapper=buildControl(control);
        controlHosts.set(key,wrapper);
        grid.appendChild(wrapper);
      }
      stepHosts.set(step.key,section);
      body.appendChild(section);
    }

    const resultHost=document.createElement('div');
    resultHost.className='legend-experience-results';
    for(const result of definition.results || []){
      const row=document.createElement('div'); row.className='legend-experience-result';
      const label=document.createElement('span'); label.textContent=result.label || result.key;
      const output=document.createElement('strong'); output.dataset.experienceResult=result.key;
      row.append(label,output); resultHost.appendChild(row);
    }
    if(resultHost.childNodes.length) form.appendChild(resultHost);

    const status=document.createElement('p');
    status.setAttribute('role','status'); status.setAttribute('aria-live','polite');
    status.className='legend-experience-status';
    form.appendChild(status);

    let currentStep=steps[0]?.key || null;
    const visibleStepKeys=answers=>steps
      .filter(step=>!step.visibleWhen || experienceTruthy(evaluateExperienceExpression(step.visibleWhen,answers,definition.calculations || {})))
      .map(step=>step.key);

    const render=()=>{
      const answers=experienceAnswers(form,definition);
      const visibleSteps=visibleStepKeys(answers);
      if(!visibleSteps.includes(currentStep)) currentStep=visibleSteps[0] || null;
      for(const [key,section] of stepHosts) section.hidden=key!==currentStep;
      for(const control of definition.controls || []){
        const host=controlHosts.get(control.key);
        if(!host) continue;
        host.hidden=!!control.visibleWhen && !experienceTruthy(evaluateExperienceExpression(control.visibleWhen,answers,definition.calculations || {}));
      }
      for(const result of definition.results || []){
        const output=form.querySelector('[data-experience-result="'+CSS.escape(result.key)+'"]');
        if(!output) continue;
        const value=evaluateExperienceExpression(result.expression,answers,definition.calculations || {});
        output.textContent=formatExperienceResult(value,result.format);
      }
      const index=Math.max(0,visibleSteps.indexOf(currentStep));
      progress.hidden=visibleSteps.length<=1;
      progressBar.style.width=visibleSteps.length ? (((index+1)/visibleSteps.length)*100)+'%' : '0%';
      progress.setAttribute('aria-label',visibleSteps.length?('Step '+(index+1)+' of '+visibleSteps.length):'');
    };

    form.addEventListener('click',event=>{
      const button=event.target.closest?.('[data-experience-action]');
      if(!button || button.dataset.experienceAction==='cta') return;
      const action=button.dataset.experienceAction;
      if(action==='submit' && definition.submitCapability==='lead_capture') return;
      event.preventDefault();
      if(action==='reset'){form.reset();currentStep=steps[0]?.key || null;render();return;}
      const answers=experienceAnswers(form,definition);
      const visible=visibleStepKeys(answers);
      const index=visible.indexOf(currentStep);
      if(button.dataset.experienceTargetStep && visible.includes(button.dataset.experienceTargetStep))
        currentStep=button.dataset.experienceTargetStep;
      else if(action==='back') currentStep=visible[Math.max(0,index-1)] || currentStep;
      else if(action==='next' || action==='submit') currentStep=visible[Math.min(visible.length-1,index+1)] || currentStep;
      render();
      form.scrollIntoView?.({behavior:'smooth',block:'nearest'});
    });

    form.addEventListener('input',render);
    form.addEventListener('change',render);
    applyFormFieldPresentations(form,node);
    queueMicrotask(render);
    return form;
  }

  function buildCanonicalInquiryForm(node) {
    const el=document.createElement('form');
    el.id='website_inquiry_'+node.id;
    el.className='public-form legend-cms-inquiry-form';
    el.dataset.websiteInquiry='';
    el.dataset.formKey='website_inquiry';
    el.setAttribute('action','/api/website-inquiries/public');
    el.setAttribute('method','post');

    const fieldset=document.createElement('fieldset');
    if(editorMode){el.dataset.preview='';fieldset.disabled=true;}
    const legend=document.createElement('legend');
    legend.textContent=node.title || 'Send an inquiry';
    const grid=document.createElement('div');
    grid.className='public-form-grid';

    const field=(labelText,name,type='text',attrs={})=>{
      const key=safeId(name);
      const label=document.createElement('label');
      label.textContent=node.fieldLabels?.[key] || labelText;
      const input=name==='Message' ? document.createElement('textarea') : document.createElement('input');
      input.dataset.cmsFieldKey=key;
      if(name!=='Message') input.type=type;
      input.name=name;
      input.required=true;
      if(name==='Message') input.rows=5;
      Object.entries(attrs).forEach(([key,value])=>input.setAttribute(key,value));
      label.appendChild(input);
      return label;
    };

    grid.append(
      field('First Name','FirstName','text',{autocomplete:'given-name',maxlength:'120'}),
      field('Last Name','LastName','text',{autocomplete:'family-name',maxlength:'120'}),
      field('Phone Number','Phone','tel',{inputmode:'tel',autocomplete:'tel',maxlength:'64'}),
      field('Email','Email','email',{autocomplete:'email',maxlength:'254'})
    );
    const message=field('Message','Message','text',{maxlength:'12000'});
    message.className='public-form-full';
    grid.appendChild(message);

    const consentLabel=document.createElement('label');
    consentLabel.className='public-form-consent public-form-full';
    const consent=document.createElement('input');
    consent.type='checkbox';
    consent.name='consent';
    consent.dataset.cmsFieldKey='consent';
    consent.required=true;
    const consentText=document.createElement('span');
    consentText.textContent=node.fieldLabels?.consent || 'I agree to share this inquiry with this website.';
    consentLabel.append(consent,consentText);
    grid.appendChild(consentLabel);

    const submit=document.createElement('button');
    submit.type='submit';
    submit.dataset.cmsFieldKey='submit';
    submit.className='btn primary';
    submit.textContent=node.fieldLabels?.submit || node.text || 'Send inquiry';
    const status=document.createElement('p');
    status.setAttribute('role','status');
    status.setAttribute('aria-live','polite');

    fieldset.append(legend,grid,submit);
    el.append(fieldset,status);
    applyFormFieldPresentations(el,node);
    return el;
  }

  function buildCanonicalEmbed(node) {
    const el=document.createElement('div');
    el.className='legend-cms-embed';
    const frame=document.createElement('iframe');
    frame.dataset.cmsCodeFrame='true';
    frame.title='Custom code block';
    frame.setAttribute('sandbox','allow-scripts');
    frame.setAttribute('referrerpolicy','no-referrer');
    frame.setAttribute('loading','lazy');
    el.appendChild(frame);
    renderCodePreview(el,node);
    return el;
  }

  function buildCompositionNode(node) {
    if (!node?.id) return null;
    let el;
    if (node.type === 'reusable') {
      const definition = reusableDefinition(node);
      el = document.createElement(definition?.kind === 'section' ? 'section' : 'div');
      el.className = 'cms-reusable-instance';
      el.dataset.cmsId = node.id;
      el.dataset.cmsCompositionId = node.id;
      el.dataset.cmsEditable = 'true';
      if (node.hidden === true) el.hidden = true;
      renderReusableInstance(el, node);
    } else if (node.type === 'form') {
      el = buildCanonicalInquiryForm(node);
    } else if (node.type === 'experience') {
      el = buildNativeExperience(node);
    } else if (node.type === 'embed') {
      el = buildCanonicalEmbed(node);
    } else {
      el = document.createElement(safeCompositionTag(node));
      if (node.className) el.className = node.className;
      if (node.type === 'image') {
        const source = node.mediaAssetId ? API_BASE + '/api/website-content/media/' + node.mediaAssetId : node.mediaUrl;
        if (source) el.src = mediaUrl(source);
        el.alt = node.alt || '';
      } else if (node.type === 'video') {
        const source = node.mediaAssetId ? API_BASE + '/api/website-content/media/' + node.mediaAssetId : node.mediaUrl;
        if (source) el.src = mediaUrl(source);
        el.playsInline = true;
        el.preload = node.videoLoop === true ? 'auto' : 'metadata';
      } else if (node.type === 'cta' || node.type === 'link') {
        if (node.actionKey) el.dataset.websiteActionKey = node.actionKey;
        if (el.tagName === 'A' && node.href && safeUrl(node.href)) el.setAttribute('href',node.href);
        if (el.tagName === 'A') el.target = node.target === '_blank' ? '_blank' : '_self';
      }

      if (node.systemKey === 'primary_navigation') {
        el.id='primary-nav';
        el.classList.add('nav');
        el.setAttribute('data-public-nav','');
        el.dataset.cmsBehaviorLocked='true';
      }
      if (node.systemBinding === 'business_name') el.setAttribute('data-business-name','');
      else if (String(node.systemBinding || '').startsWith('business_field:'))
        el.setAttribute('data-business-field',String(node.systemBinding).slice('business_field:'.length));

      const children = Array.isArray(node.children) ? node.children : [];
      if (children.length) {
        for (const child of children) {
          const childElement = buildCompositionNode(child);
          if (childElement) el.appendChild(childElement);
        }
      } else if (node.text != null && !['IMG','VIDEO','FORM'].includes(el.tagName)) {
        setContentText(el,String(node.text),true);
      }
    }

    if (!el) return null;
    if (node.className) {
      const existing=[...el.classList];
      el.className=[...new Set([...existing,...String(node.className).split(/\s+/).filter(Boolean)])].join(' ');
    }
    el.dataset.cmsCompositionId=node.id;
    el.dataset.cmsId=node.id;
    el.dataset.cmsEditable='true';
    if(el.tagName==='FORM') applyFormFieldPresentations(el,node);
    if (node.type === 'section' || node.tag === 'header' || node.tag === 'footer') el.dataset.cmsSection=node.id;
    rememberOriginal(el);
    applyCompositionNode(el,node);
    return el;
  }

  function renderCanonicalCompositionPage() {
    rebuildCanonicalSharedPresentationIndex(documentState);
    // Template runtimes own their DOM and event listeners. All structural editor
    // paths must preserve that mounted runtime and apply presentation only.
    if(pageUsesSystemTemplate()) { applyTemplateBackedCompositionPage(); return; }
    const main=document.querySelector('main');
    if(!main) return;
    main.replaceChildren();
    const roots=pageState().composition || [];
    for(const node of roots){
      const element=buildCompositionNode(node);
      if(element) main.appendChild(element);
    }
    // buildCompositionNode constructs descendants before the root is mounted.
    // Reapply the same canonical presentation once the page graph is attached
    // so responsive role/flow detection is correct on first paint, not only
    // after a resize or editor breakpoint refresh.
    for(const root of roots)
      walkComposition([root],node=>applyCompositionNode(findEditableElement(node.id),node));
  }

  function ensureCanonicalNavigationToggle() {
    const header=document.querySelector('.site-header');
    const nav=header?.querySelector('#primary-nav,[data-public-nav]');
    if(!header || !nav) return;
    let toggle=header.querySelector('[data-public-nav-toggle]');
    if(!toggle){
      toggle=document.createElement('button');
      toggle.type='button';
      toggle.className='nav-toggle';
      toggle.dataset.publicNavToggle='';
      toggle.dataset.cmsLocked='true';
      toggle.setAttribute('aria-controls','primary-nav');
      toggle.setAttribute('aria-expanded','false');
      toggle.textContent='Menu';
      header.insertBefore(toggle,nav);
    }
    if(toggle.dataset.legendRuntimeBound==='true') return;
    toggle.dataset.legendRuntimeBound='true';
    const close=()=>{nav.dataset.open='false';toggle.setAttribute('aria-expanded','false');};
    toggle.addEventListener('click',()=>{
      const open=nav.dataset.open==='true';
      nav.dataset.open=open?'false':'true';
      toggle.setAttribute('aria-expanded',open?'false':'true');
    });
    nav.addEventListener('click',event=>{if(event.target.closest?.('a')) close();});
  }

  function renderCanonicalShell() {
    const renderRoot=(selector,nodes,requiredClass)=>{
      const existing=document.querySelector(selector);
      const rendered=(nodes || []).map(buildCompositionNode).filter(Boolean);
      if(rendered.length===0){
        existing?.remove();
        return;
      }
      rendered[0].classList.add(requiredClass);
      if(existing) existing.replaceWith(...rendered);
      else {
        const main=document.querySelector('main');
        if(requiredClass==='site-header') main?.before(...rendered);
        else main?.after(...rendered);
      }
    };
    renderRoot('.site-header',documentState.shell?.header || [],'site-header');
    renderRoot('.site-footer',documentState.shell?.footer || [],'site-footer');
    ensureCanonicalNavigationToggle();
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

  function refreshBrowserAgentWorkspace() {
    const status=document.getElementById('legend-cms-browser-agent-status');
    if(!status) return;
    const selectedId=selected?.dataset?.cmsCompositionId || selected?.dataset?.cmsId || null;
    const actionCount=Array.isArray(ctaCatalog)?ctaCatalog.filter(option=>option?.managed!==false).length:0;
    status.textContent=[
      'Browser-managed workspace',
      'site='+SITE_KEY,
      'page='+currentPageRoute(),
      'revision='+(revision ?? 'unsaved'),
      'selected='+(selectedId || 'none'),
      'canonicalActions='+actionCount,
      'externalAiApi=false'
    ].join(' · ');
  }

  async function creativeWorkspaceRequest(path,{method='GET',body=null,query=null}={}) {
    const url=new URL(API_BASE+'/api/website-content/'+path);
    if(query) for(const [key,value] of Object.entries(query)) if(value!=null) url.searchParams.set(key,String(value));
    if(method==='GET') url.searchParams.set('ticket',editorTicket);
    const response=await fetch(url,{method,cache:'no-store',headers:body?{'Content-Type':'application/json'}:undefined,body:body?JSON.stringify(body):undefined});
    const payload=await response.json().catch(()=>({}));
    if(!response.ok){
      const error=new Error(payload.message || payload.error || ('Website Studio request failed ('+response.status+').'));
      error.status=response.status; error.payload=payload; throw error;
    }
    return payload;
  }

  function applyCreativeMutationVisuals(operations,payload,selectedId=null) {
    const changes=payload?.changes || {};
    const changedScopes=Array.isArray(payload?.changedScopes)?payload.changedScopes:[];
    const route=currentPageRoute();
    const currentPageChanged=changedScopes.some(scope=>scope===route || String(scope).startsWith(route+'#')) || !!changes.pages?.[route];
    let renderPage=false, renderShell=false, refreshReusable=false, metadata=false, responsive=false;

    if(!Array.isArray(operations)){
      renderPage=!!changes.pages?.[route];
      renderShell=!!changes.shellHeader || !!changes.shellFooter;
      refreshReusable=Object.keys(changes.reusableComponents || {}).length>0 || (changes.removedComponents || []).length>0;
      metadata=renderPage;
      responsive=!!changes.theme || !!changes.breakpoints;
    }else{
      for(const operation of operations){
        switch(operation?.type){
          case 'setTheme': applyTheme(documentState.theme); responsive=true; break;
          case 'setFavicon': applyFavicon(documentState.faviconImageDataUrl); break;
          case 'setBreakpoints': responsive=true; break;
          case 'setStorePresentation': applyStoreNavigation(); break;
          case 'replaceShellHeader':
          case 'replaceShellFooter': renderShell=true; break;
          case 'upsertReusable':
          case 'removeReusable': refreshReusable=true; break;
          case 'createPage':
          case 'removePage':
          case 'movePageRoute':
          case 'updatePage':
            if(operation.pagePath===route || operation.targetPath===route) { renderPage=true; metadata=true; }
            break;
          case 'insertNode':
          case 'insertRecipe':
          case 'insertCapability':
          case 'removeNode':
          case 'moveNode':
            if(currentPageChanged) renderPage=true;
            break;
          case 'replaceNode':
          case 'setApprovedCapability': {
            if(!currentPageChanged) break;
            const id=operation.nodeId;
            const model=id ? compositionNode(id) : null;
            const el=id ? findEditableElement(id) : null;
            if(pageUsesSystemTemplate() || !model || !el) { renderPage=true; break; }
            const replacement=buildCompositionNode(model);
            if(!replacement){renderPage=true;break;}
            el.replaceWith(replacement);
            break;
          }
        }
      }
    }

    if(renderShell) renderCanonicalShell();
    if(renderPage) renderCanonicalCompositionPage();
    if(refreshReusable) refreshReusableInstances();
    if(metadata){
      const page=pageState();
      document.title=page.title ?? originalTitle;
      const description=document.querySelector('meta[name="description"]');
      if(description) description.setAttribute('content',page.description ?? originalDescription);
      syncPageControls();
    }
    if(responsive) refreshResponsiveComposition();
    preservePreviewNavigation();
    if(selectedId) setSelected(findEditableElement(selectedId));
    else updateDirectCanvasUi();
  }

  function mergeCreativeMutationDelta(payload,operations=null) {
    const selectedId=selected?.dataset?.cmsCompositionId || selected?.dataset?.cmsId || null;
    documentState=applyCreativeMutationDeltaToState(documentState,payload);
    rebuildCanonicalSharedPresentationIndex(documentState);
    persistedDocumentState=cloneCanonicalValue(documentState);
    revision=payload?.revision ?? revision;
    namedDrafts=payload?.drafts || namedDrafts;
    dirty=false;
    canonicalSourceDocument=null;
    canonicalSourceRevision=null;
    sourceEditorBaseNode=null;
    sourceEditorBaseFingerprint=null;
    applyCreativeMutationVisuals(operations,payload,selectedId);
    refreshBrowserAgentWorkspace();
    return payload;
  }

  async function creativeApplyMutationBatch(operations,options={}) {
    if(!Array.isArray(operations) || !operations.length) throw new Error('At least one website mutation is required.');
    const payload=await creativeWorkspaceRequest('manage/mutations',{
      method:'POST',
      body:{
        ticket:editorTicket,
        expectedRevision:revision,
        operations,
        draftId:options?.draftId || null,
        draftName:options?.draftName || null
      }
    });
    return mergeCreativeMutationDelta(payload,operations);
  }

  async function creativeApplyDesignPlan(plan) {
    if(!plan || typeof plan!=='object') throw new Error('A website design plan is required.');
    const payload=await creativeWorkspaceRequest('manage/design-plan',{
      method:'POST',
      body:{ticket:editorTicket,expectedRevision:revision,plan}
    });
    return mergeCreativeMutationDelta(payload,null);
  }

  function installCreativeAgentWorkspaceApi() {
    if(!editorMode) return;
    const api={
      schema:'legend-creative-browser/v1',
      getSiteSummary:()=>creativeWorkspaceRequest('manage/agent/summary'),
      getPageOutline:page=>creativeWorkspaceRequest('manage/agent/page-outline',{query:{page:page || currentPageRoute()}}),
      getNode:id=>creativeWorkspaceRequest('manage/agent/node',{query:{id}}),
      listRecipes:()=>creativeWorkspaceRequest('manage/agent/recipes'),
      getFullContract:()=>creativeWorkspaceRequest('manage/agent/contract'),
      listMedia:(query={})=>creativeWorkspaceRequest('manage/media',{query}),
      listBusinessData:async()=>{await ensureBusinessDataCatalog();return {dataCatalog:managementPayload?.dataCatalog || [],collections:[...collectionData.values()]};},
      applyMutationBatch:creativeApplyMutationBatch,
      applyDesignPlan:creativeApplyDesignPlan,
      runQuality:async()=>({
        server:await creativeWorkspaceRequest('manage/agent/design-quality'),
        rendered:{page:currentPageRoute(),viewport:responsiveViewportWidth(),checks:liveQualityChecks()}
      }),
      inspectConversionPath:async()=>{
        const quality=await creativeWorkspaceRequest('manage/agent/design-quality');
        return quality?.design?.conversionPaths || quality?.design?.ConversionPaths || [];
      },
      current:()=>({siteKey:SITE_KEY,page:currentPageRoute(),revision,selectedId:sourceSelectedNodeId()})
    };
    Object.defineProperty(window,'LEGEND_WEBSITE_STUDIO_AGENT',{value:Object.freeze(api),configurable:true});
  }

  function openBrowserAgentSource(scope='site') {
    const sourceScope=document.getElementById('legend-cms-source-scope');
    if(sourceScope){
      sourceScope.value=scope==='selection' && sourceSelectedNodeId() ? 'selection' : 'site';
      sourceEditorDirty=false;
    }
    showPanel('source');
    document.getElementById('legend-cms-site-source')?.focus();
  }

  function renderMotionControls() {
    const host = document.getElementById('legend-cms-motion-controls');
    if (!host?.replaceChildren) return;
    host.replaceChildren();
    const status = document.getElementById('legend-cms-motion-status');
    if (!selected || selected.dataset.cmsSignalOnly) { if (status) status.textContent='Select a page element or added block.'; return; }
    const existing = compositionNodeForElement(selected, false);
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
      const remove=document.createElement('button'); remove.type='button'; remove.textContent='Remove'; remove.addEventListener('click',()=>{ const ov=selectedWebsiteModel(); mutate(()=>ov.animations=(ov.animations||[]).filter(item=>item.id!==binding.id)); });
      actions.append(preview,remove); row.appendChild(actions); host.appendChild(row);
    }
    const add=document.createElement('button'); add.type='button'; add.textContent='Add motion interaction'; add.disabled=bindings.length>=8;
    add.addEventListener('click',()=>{ const ov=selectedWebsiteModel(); checkpoint(); ov.animations ||= []; ov.animations.push({id:crypto.randomUUID().replaceAll('-',''),trigger:'view',effect:'fade',durationMs:400,delayMs:0,distancePx:24,easing:'ease',once:true}); markDirty(); renderMotionControls(); });
    host.appendChild(add);
  }
  function findEditableElement(id) {
    const direct = document.querySelector(`[data-cms-id="${CSS.escape(id)}"]`);
    if (direct) return direct;
    // Read-only translation of historical label-derived identifiers. Keep the
    // saved identity once matched; never generate another label-derived ID.
    const legacy = String(id).match(/^(.*)\.([a-z][a-z0-9]*)\.[^.]+\.(node\d+|\d+)$/);
    if (!legacy) return null;
    const structural = `${legacy[1]}.${legacy[2]}.node.${legacy[3]}`;
    const node = document.querySelector(`[data-cms-id="${CSS.escape(structural)}"]`);
    if (node) node.dataset.cmsId = id;
    return node;
  }

  function applyCompositionNode(el, model) {
    if (el?.dataset.cmsSignalOnly && el.tagName!=='FORM') return;
    if (!el || !model) return;
    if (model.actionKey) el.dataset.websiteActionKey = model.actionKey;
    else if (model.href != null) delete el.dataset.websiteActionKey;
    if (model.hidden === true) el.hidden = true;
    else if (model.hidden === false) el.hidden = false;

    const original = rememberOriginal(el);
    const entityBound = el.hasAttribute?.('data-business-name') || el.hasAttribute?.('data-business-field');
    const commerceControl = !!el.dataset.legendStoreNav;
    if (el instanceof HTMLImageElement) {
      const media = model.mediaAssetId ? API_BASE + '/api/website-content/media/' + model.mediaAssetId : model.mediaUrl;
      el.src = media ? mediaUrl(media) : (original.src || '');
    } else if (!entityBound && !commerceControl && !el.dataset.cmsSection && !['DIV','ARTICLE','HEADER','FOOTER','FORM'].includes(el.tagName)) {
      const compositionHasChildren = !!el.dataset.cmsCompositionId && Array.isArray(model.children) && model.children.length > 0;
      if (!compositionHasChildren) setContentText(el, model.text != null ? model.text : (original.text || ''), model.text != null);
    }

    if (el.tagName === 'A' && !commerceControl) {
      const href = model.href != null && safeUrl(model.href) ? model.href : original.href;
      if (href) el.setAttribute('href', href); else el.removeAttribute('href');
      el.target = model.target === '_blank' ? '_blank' : '_self'; el.rel = 'noopener noreferrer';
    }
    if (model.alt != null && el.tagName === 'IMG') el.alt = model.alt;
    if (el.tagName === 'VIDEO') {
      const media = model.mediaAssetId ? API_BASE + '/api/website-content/media/' + model.mediaAssetId : model.mediaUrl;
      if (media && safeUrl(media, true)) el.src = mediaUrl(media);
      const looped=model.videoLoop === true;
      el.loop=looped;
      el.autoplay=looped;
      el.controls=!looped;
      el.playsInline=true;
      // Never strip or mute the uploaded audio track. Audible autoplay remains
      // subject to the browser's user-gesture policy, but loop mode has no
      // playback chrome and retains the source audio exactly as uploaded.
      el.muted=false;
      el.preload=looped ? 'auto' : 'metadata';
    }
    applyDataBinding(el, model.dataBinding);
    applyCanonicalResponsiveAttributes(el,model);
    applyStyle(el, effectiveStyle(model,el));
    applyLayout(el, effectiveLayout(model,el));
    applyAnimations(el, model.animations);
  }

  function buildLegacyExtraNode(extra, editable = true, idPrefix = '') {
    let el;
    if (extra.type === 'image') {
      el = document.createElement('img');
      el.src = mediaUrl(extra.imageDataUrl || ''); el.alt = ''; el.className = 'legend-legacy-migration-node legend-legacy-migration-image';
    } else if (extra.type === 'section') {
      el = document.createElement('section');
      if (editable) el.dataset.cmsSection = `extra:${extra.id}`;
      el.className = 'legend-legacy-migration-node legend-legacy-migration-section';
      if (extra.templateSectionId) {
        const template=document.querySelector(`[data-cms-section="${CSS.escape(extra.templateSectionId)}"]:not(.legend-legacy-migration-section)`);
        if (template) {
          el.className=['legend-legacy-migration-node','legend-legacy-migration-section',...template.classList].filter((value,index,array)=>value && array.indexOf(value)===index).join(' ');
          el.innerHTML=template.innerHTML;
          let childIndex=0;
          el.querySelectorAll('h1,h2,h3,h4,h5,p,li,a,button,label,small,strong,span,img,video,div,article').forEach(child=>{
            if (!canEditElement(child)) return;
            if (!['IMG','VIDEO','A','DIV','ARTICLE'].includes(child.tagName) && child.children.length>0) return;
            child.dataset.cmsId = `extra:${extra.id}:node:${++childIndex}`;
            child.dataset.cmsEditable='true';
            rememberOriginal(child);
          });
        }
      }
    } else if (extra.type === 'video') {
      el = document.createElement('video'); el.controls = true; el.preload = 'metadata';
      if (safeUrl(extra.videoUrl, true)) el.src = mediaUrl(extra.videoUrl); el.className = 'legend-legacy-migration-node';
    } else if (extra.type === 'card') {
      el = document.createElement('article'); el.className = 'legend-legacy-migration-node card legend-legacy-migration-card';
      const heading = document.createElement('h3'); setContentText(heading, extra.title || 'New service', true);
      const copy = document.createElement('p'); setContentText(copy, extra.text || '', true);
      if (editable) for (const [node, field] of [[heading, 'title'], [copy, 'text']]) {
        node.dataset.cmsExtraId = extra.id; node.dataset.cmsExtraField = field;
        node.dataset.cmsId = `extra:${extra.id}:${field}`; node.dataset.cmsEditable = 'true';
      }
      el.append(heading, copy);
    } else if (extra.type === 'button') {
      el = document.createElement('a'); el.textContent = extra.text || 'New button';
      if (safeUrl(extra.href)) el.href = extra.href; el.className = 'legend-legacy-migration-node btn primary';
    } else if (extra.type === 'form') {
      el = document.createElement('form');
      el.id = `website_inquiry_${extra.id}`;
      el.className = 'legend-legacy-migration-node public-form legend-legacy-migration-form';
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
      el = document.createElement('div'); el.className = 'legend-legacy-migration-node legend-legacy-migration-code';
      const frame = document.createElement('iframe'); frame.dataset.cmsCodeFrame = 'true'; frame.title = 'Custom code block';
      frame.setAttribute('sandbox', 'allow-scripts');
      frame.setAttribute('referrerpolicy', 'no-referrer'); frame.setAttribute('loading', 'lazy');
      el.appendChild(frame); renderCodePreview(el, extra);
    } else {
      el = document.createElement('p'); el.textContent = extra.text || ''; el.className = 'legend-legacy-migration-node legend-legacy-migration-node-text';
    }
    if (editable) {
      el.dataset.cmsExtraId = extra.id; el.dataset.cmsId = `extra:${extra.id}`; el.dataset.cmsEditable = 'true';
    } else if (idPrefix) el.dataset.cmsReusableChild = `${idPrefix}:${extra.id}`;
    return el;
  }

  function reusableDefinition(instance) {
    return instance?.type==='reusable' && instance.syncSourceId
      ? documentState.reusableComponents?.[instance.syncSourceId] || null
      : null;
  }

  function reusableDefinitionClone(node, instanceId) {
    const copy=cloneCanonicalValue(node);
    const originalId=copy.id;
    copy.id=instanceId+'.'+originalId;
    copy.signals=[];
    copy.fieldSignals={};
    copy.children=(copy.children || []).map(child=>reusableDefinitionClone(child,instanceId));
    return copy;
  }

  function renderReusableInstance(wrapper, instance) {
    if(!wrapper || !instance) return;
    wrapper.replaceChildren();
    wrapper.dataset.cmsReusableId=instance.syncSourceId || '';
    const definition=reusableDefinition(instance);
    if(!definition){
      if(editorMode){
        const missing=document.createElement('p');
        missing.className='legend-cms-reusable-missing';
        missing.textContent='Reusable component is unavailable. Restore its definition or remove this instance.';
        wrapper.appendChild(missing);
      }
      applyStyle(wrapper,effectiveStyle(instance));
      applyLayout(wrapper,effectiveLayout(instance));
      applyAnimations(wrapper,instance.animations);
      return;
    }

    for(const definitionNode of definition.composition || []){
      const rendered=buildCompositionNode(reusableDefinitionClone(definitionNode,instance.id));
      if(!rendered) continue;
      rendered.classList.add('legend-cms-reusable-child');
      rendered.querySelectorAll?.('[data-cms-editable]').forEach(child=>{
        child.dataset.cmsLocked='true';
        delete child.dataset.cmsEditable;
        delete child.dataset.cmsCompositionId;
      });
      rendered.dataset.cmsLocked='true';
      delete rendered.dataset.cmsEditable;
      delete rendered.dataset.cmsCompositionId;
      wrapper.appendChild(rendered);
    }
    applyStyle(wrapper,effectiveStyle(instance));
    applyLayout(wrapper,effectiveLayout(instance));
    applyAnimations(wrapper,instance.animations);
  }

  function selectedCompositionSource() {
    const id=selected?.dataset?.cmsCompositionId;
    return id ? compositionNode(id) : null;
  }

  function containsProtectedSystemNode(node) {
    if(!node) return false;
    if(node.type==='form' || node.systemKey || node.systemBinding ||
       (Array.isArray(node.signals) && node.signals.length > 0) ||
       Object.values(node.fieldSignals || {}).some(bindings=>Array.isArray(bindings) && bindings.length > 0)) return true;
    return (node.children || []).some(containsProtectedSystemNode);
  }

  function cloneReusableDefinitionNode(node, componentId, path='root') {
    const copy=cloneCanonicalValue(node);
    copy.id=componentId+'.'+path;
    copy.signals=[];
    copy.syncSourceId=null;
    delete copy.hidden;
    copy.children=(node.children || []).map((child,index)=>
      cloneReusableDefinitionNode(child,componentId,path+'.'+(index+1)));
    return copy;
  }

  function captureReusableDefinition(name, existingId=null) {
    const source=selectedCompositionSource();
    if(!source || source.type==='reusable' || containsProtectedSystemNode(source)) return null;
    const componentId=existingId || crypto.randomUUID().replaceAll('-','');
    return {
      id:componentId,
      name:(name || 'Reusable component').trim().slice(0,120),
      kind:source.type==='section' ? 'section' : 'block',
      composition:[cloneReusableDefinitionNode(source,componentId)]
    };
  }

  function componentInUse(componentId) {
    let used=false;
    const inspect=nodes=>walkComposition(nodes,node=>{
      if(node.type==='reusable' && node.syncSourceId===componentId){used=true;return false;}
    });
    inspect(documentState.shell?.header || []);
    inspect(documentState.shell?.footer || []);
    Object.values(documentState.pages || {}).forEach(page=>inspect(page?.composition || []));
    return used;
  }

  function refreshReusableInstances(componentId=null) {
    document.querySelectorAll('.cms-reusable-instance[data-cms-reusable-id]').forEach(wrapper=>{
      const instance=compositionNode(wrapper.dataset.cmsCompositionId || wrapper.dataset.cmsId);
      if(!instance || (componentId && instance.syncSourceId!==componentId)) return;
      renderReusableInstance(wrapper,instance);
    });
    updateDirectCanvasUi();
  }

  function renderReusableComponents() {
    const host=document.getElementById('legend-cms-component-list');
    const status=document.getElementById('legend-cms-component-status');
    if(!host?.replaceChildren) return;
    host.replaceChildren();

    const definitions=Object.values(documentState.reusableComponents || {})
      .sort((a,b)=>(a.name || '').localeCompare(b.name || ''));

    for(const definition of definitions){
      const row=document.createElement('div');
      row.className='legend-cms-component-row';
      const info=document.createElement('div');
      const title=document.createElement('strong');
      title.textContent=definition.name || definition.id;
      const meta=document.createElement('small');
      meta.textContent=(definition.kind || 'block')+' · '+definition.id;
      info.append(title,meta);

      const insert=document.createElement('button');
      insert.type='button';
      insert.textContent='Insert';
      insert.addEventListener('click',()=>{
        const section=selectedSection || document.querySelector('main [data-cms-section]');
        const parentId=section?.dataset?.cmsCompositionId;
        const parent=parentId ? compositionNode(parentId) : null;
        if(!parent){
          if(status) status.textContent='Select a canonical section before inserting a reusable component.';
          return;
        }
        checkpoint();
        const id=(crypto.randomUUID ? crypto.randomUUID() : String(Date.now())).replaceAll('-','');
        const instance={
          id,
          type:'reusable',
          tag:'div',
          syncSourceId:definition.id,
          signals:[],
          style:{widthPercent:100},
          breakpointStyles:{},
          layout:{mode:'free',direction:'column'},
          breakpointLayouts:{},
          animations:[],
          children:[]
        };
        parent.children ||= [];
        parent.children.push(instance);
        renderCanonicalCompositionPage();
        setSelected(findEditableElement(id));
        markDirty();
        renderReusableComponents();
      });

      const update=document.createElement('button');
      update.type='button';
      update.textContent='Update from selected';
      const source=selectedCompositionSource();
      update.disabled=!source || source.type==='reusable' || containsProtectedSystemNode(source);
      update.addEventListener('click',()=>{
        const next=captureReusableDefinition(definition.name,definition.id);
        if(!next){
          if(status) status.textContent='Select a non-system canonical block or section to update this component.';
          return;
        }
        checkpoint();
        documentState.reusableComponents[definition.id]=next;
        refreshReusableInstances(definition.id);
        markDirty();
        renderReusableComponents();
      });

      const remove=document.createElement('button');
      remove.type='button';
      remove.textContent='Delete';
      remove.disabled=componentInUse(definition.id);
      remove.title=remove.disabled ? 'Remove every instance before deleting this reusable definition.' : '';
      remove.addEventListener('click',()=>{
        if(componentInUse(definition.id)) return;
        checkpoint();
        delete documentState.reusableComponents[definition.id];
        markDirty();
        renderReusableComponents();
      });

      row.append(info,insert,update,remove);
      host.appendChild(row);
    }

    if(!definitions.length){
      const empty=document.createElement('p');
      empty.textContent='No reusable components yet.';
      host.appendChild(empty);
    }
  }

  function pageLayerSections() {
    return Array.from(document.querySelectorAll('[data-cms-section]')).filter(section => {
      if (section.matches('.site-header,.site-footer')) return false;
      const parentSection = section.parentElement?.closest?.('[data-cms-section]');
      return !parentSection;
    });
  }

  function syncSectionOrderFromDom() {
    const roots=pageState().composition ||= [];
    const byId=new Map(roots.map(node=>[node.id,node]));
    const ordered=pageLayerSections().map(section=>byId.get(section.dataset.cmsCompositionId)).filter(Boolean);
    const orderedIds=new Set(ordered.map(node=>node.id));
    pageState().composition=[...ordered,...roots.filter(node=>!orderedIds.has(node.id))];
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

  let businessNavigationDrag = null;

  function syncBusinessNavigationOrder(nav) {
    const links=[...nav.querySelectorAll('[data-legend-page-nav="true"]')];
    links.forEach((link,index)=>{
      const route=normalizePageRoute(link.dataset.legendPageRoute);
      if (!route) return;
      const page=ensurePageRecord(route);
      page.navigation ||= {showInNavigation:true,order:0,isDeleted:false};
      page.navigation.order=index * 10;
    });
    markDirty();
    refreshPageSelector();
    renderPageManager();
  }

  function installBusinessNavigationEditor(nav) {
    if (!editorMode || !nav || nav.dataset.cmsPageOrderWired === 'true') return;
    nav.dataset.cmsPageOrderWired='true';

    nav.addEventListener('pointerdown',event=>{
      const link=event.target.closest?.('[data-legend-page-nav="true"]');
      if (!link || (event.button !== undefined && event.button !== 0)) return;
      businessNavigationDrag={link,pointerId:event.pointerId,changed:false,checkpointed:false};
      link.setPointerCapture?.(event.pointerId);
      event.preventDefault();
      event.stopPropagation();
    });

    nav.addEventListener('pointermove',event=>{
      const drag=businessNavigationDrag;
      if (!drag || (drag.pointerId != null && event.pointerId != null && drag.pointerId !== event.pointerId)) return;
      const siblings=[...nav.querySelectorAll('[data-legend-page-nav="true"]')];
      const target=siblings.find(candidate=>{
        if (candidate===drag.link) return false;
        const rect=candidate.getBoundingClientRect();
        return event.clientX >= rect.left && event.clientX <= rect.right;
      });
      if (!target) return;
      const rect=target.getBoundingClientRect();
      const before=event.clientX < rect.left + rect.width / 2;
      const reference=before ? target : target.nextSibling;
      if (reference === drag.link || (!reference && drag.link === nav.lastElementChild)) return;
      if (!drag.checkpointed) { checkpoint(); drag.checkpointed=true; }
      nav.insertBefore(drag.link,reference);
      drag.changed=true;
      event.preventDefault();
      event.stopPropagation();
    });

    const finish=event=>{
      const drag=businessNavigationDrag;
      if (!drag || (drag.pointerId != null && event.pointerId != null && drag.pointerId !== event.pointerId)) return;
      businessNavigationDrag=null;
      if (drag.changed) syncBusinessNavigationOrder(nav);
      event.preventDefault();
      event.stopPropagation();
    };
    nav.addEventListener('pointerup',finish);
    nav.addEventListener('pointercancel',finish);
  }

  function applyBusinessPageNavigation() {
    if (SITE_KEY !== 'business') return;
    const header=document.querySelector('.site-header');
    const nav=header?.querySelector('#primary-nav.nav,[data-public-nav].nav,.nav[data-public-nav]') || document.querySelector('#primary-nav.nav,[data-public-nav].nav,.nav[data-public-nav]');
    if (!nav) return;
    // Business navigation is projected exclusively from canonical page metadata.
    // Remove stale preview/template nav copies instead of merging them.
    header?.querySelectorAll('nav.nav,[data-public-nav]').forEach(candidate=>{if(candidate!==nav) candidate.remove();});

    // One authority only: the template route catalog plus this website's
    // versioned page metadata. Never merge the already-rendered DOM back into
    // the catalog; doing so lets stale/default links survive beside managed
    // pages and creates duplicate banner tabs.
    const entries=websitePageEntries(false)
      .filter(value=>value.showInNavigation!==false && !value.deleted && !value.parentPath)
      .sort((a,b)=>(Number(a.order)||0)-(Number(b.order)||0) || a.route.localeCompare(b.route));

    nav.querySelectorAll('[data-legend-page-nav="true"],a:not([data-legend-store-nav])').forEach(node=>node.remove());
    const current=currentPageRoute();
    entries.forEach(entry=>{
      const link=document.createElement(editorMode ? 'button' : 'a');
      if (editorMode) link.type='button';
      else link.href=location.pathname.startsWith('/business-preview') ? editorUrlForRoute(entry.route).toString() : entry.route;
      link.textContent=entry.label;
      link.dataset.legendPageNav='true';
      link.dataset.legendPageRoute=entry.route;
      link.dataset.businessRoute=entry.route==='/'?'home':entry.route.replace(/^\//,'');
      const actionKey = `business_${link.dataset.businessRoute}`;
      if (ctaCatalog.some(option => option.key === actionKey)) link.dataset.websiteActionKey = actionKey;
      link.dataset.cmsLocked='true';
      if (entry.route===current) link.setAttribute('aria-current','page');
      if (editorMode) {
        link.title='Drag to reorder · double-click to edit this page';
        link.addEventListener('dblclick',event=>{
          event.preventDefault();
          event.stopPropagation();
          void navigateToEditorPage(entry.route);
        },true);
      }
      nav.appendChild(link);
    });
    if (editorMode) installBusinessNavigationEditor(nav);
  }

  function applyDocument(doc) {
    documentState=normalizeDocument(doc);
    rebuildCanonicalSharedPresentationIndex(documentState);
    applyTheme(documentState.theme);
    applyFavicon(documentState.faviconImageDataUrl);

    const metadata=pageState();
    document.title=metadata.title ?? originalTitle;
    const description=document.querySelector('meta[name="description"]');
    if(description) description.setAttribute('content',metadata.description ?? originalDescription);

    if(legacyMigration){
      applyLegacyMigrationPreview();
    }else{
      document.querySelectorAll('.legend-legacy-migration-node').forEach(node=>{scaledElements.delete(node);node.remove();});
      renderCanonicalShell();
      const page=pageState();
      if(pageUsesSystemTemplate(page)){
        const liveRuntime=document.querySelector('form[data-form-key]:not([data-website-inquiry])');
        if(liveRuntime && !containsProtectedRuntimeForm(page.composition)){
          // Repair early v3 drafts that flattened an executable form into generic
          // content. Re-materialize presentation from the still-mounted server
          // template, while the form execution stays outside WebsiteContentDocument.
          const previous=new Map();
          walkComposition(page.composition || [],node=>previous.set(node.id,node));
          page.composition=materializeCurrentPageComposition();
          walkComposition(page.composition,node=>{
            const saved=previous.get(node.id);
            if(!saved) return;
            for(const key of ['text','title','style','breakpointStyles','layout','breakpointLayouts','animations','fieldLabels','fieldPresentations'])
              if(saved[key] != null) node[key]=cloneCanonicalValue(saved[key]);
          });
          templateRepairPending=true;
          dirty=true;
        }
        applyTemplateBackedCompositionPage();
      }else{
        renderCanonicalCompositionPage();
      }
    }

    if(SITE_KEY==='business' && (managementPayload || renderInput))
      bindBusiness(managementPayload || renderInput);
    applyBusinessPageNavigation();
    applyStoreNavigation();
    try {
      window.dispatchEvent(new CustomEvent('legend:website-content-rendered', {
        detail:{siteKey:SITE_KEY,page:currentPageRoute(),editor:editorMode,materialize:materializeMode}
      }));
    } catch {}
  }

  function refreshResponsiveComposition() {
    if(legacyMigration){
      const page=legacyMigrationPageState();
      for(const [id,model] of Object.entries(legacyMigration.elements || {}))
        applyLegacyMigrationRecord(findEditableElement(id),model);
      for(const [id,model] of Object.entries(page.elements || {}))
        applyLegacyMigrationRecord(findEditableElement(id),model);
      for(const extra of [...(legacyMigration.extras || []),...(page.extras || [])]){
        const node=document.querySelector('[data-cms-id="extra:'+CSS.escape(extra.id)+'"]');
        if(extra.type==='reusable') renderLegacyMigrationReusableInstance(node,extra);
        else applyLegacyMigrationRecord(node,extra);
      }
    }else{
      for(const root of allCanonicalRootSets())
        walkComposition(root.nodes,node=>applyCompositionNode(findEditableElement(node.id),node));
      const store=document.querySelector('[data-legend-store-nav="store"]');
      const cart=document.querySelector('[data-legend-store-nav="cart"]');
      if(store) applyCompositionNode(store,documentState.store?.storeNavigation || {});
      if(cart) applyCompositionNode(cart,documentState.store?.cartNavigation || {});
      refreshReusableInstances();
    }
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
    document.querySelectorAll('[data-business-field]').forEach(el => {
      const field=el.dataset.businessField;
      const facts=payload.facts || {};
      const value = business[field] ?? (field === 'contactEmail' ? facts.contactEmail : field === 'contactPhone' ? facts.phone : null);
      el.textContent = value || ''; el.hidden = !value;
    });

  }

  function effectiveStoreLabel() {
    return documentState.store?.navigationLabel?.trim() || storeContext?.label?.trim() || 'Store';
  }

  function storeIsEnabled() {
    return documentState.store?.enabled === true && storeContext?.enabled === true && !!storeContext?.storefrontUrl;
  }

  function effectiveCartIcon() {
    const value=String(documentState.store?.cartIcon || storeContext?.cartIcon || 'cart').toLowerCase();
    return ['cart','bag','basket'].includes(value) ? value : 'cart';
  }

  function effectiveCartIconSize() {
    const value=Number(documentState.store?.cartIconSizePx ?? storeContext?.cartIconSizePx ?? 28);
    return Number.isFinite(value) ? Math.max(16,Math.min(96,value)) : 28;
  }

  function createStoreCartIcon(iconKey=effectiveCartIcon(), size=effectiveCartIconSize()) {
    const svg = document.createElementNS('http://www.w3.org/2000/svg','svg');
    svg.setAttribute('viewBox','0 0 24 24');
    svg.setAttribute('width',String(size));
    svg.setAttribute('height',String(size));
    svg.style.width=`${size}px`;
    svg.style.height=`${size}px`;
    svg.setAttribute('fill','none');
    svg.setAttribute('aria-hidden','true');
    svg.classList.add('legend-store-cart-icon');
    svg.style.stroke='currentColor';
    svg.style.strokeWidth='2';
    svg.style.strokeLinecap='round';
    svg.style.strokeLinejoin='round';

    const addPath = d => {
      const path=document.createElementNS(svg.namespaceURI,'path');
      path.setAttribute('d',d);
      svg.appendChild(path);
    };
    if (iconKey === 'bag') {
      addPath('M5 8h14l-1 12H6L5 8Z');
      addPath('M9 8a3 3 0 0 1 6 0');
    } else if (iconKey === 'basket') {
      addPath('M4 10h16l-1.5 10h-13L4 10Z');
      addPath('M8 10l4-6 4 6');
      addPath('M9 13v4M12 13v4M15 13v4');
    } else {
      // Default is exactly Parfait's canonical public cart glyph.
      addPath('M6.5 6.5h15l-1.8 8.2a2 2 0 0 1-2 1.6H9.2a2 2 0 0 1-2-1.7L5.7 3.8H3');
      const first = document.createElementNS(svg.namespaceURI,'circle'); first.setAttribute('cx','9.8'); first.setAttribute('cy','20'); first.setAttribute('r','1.2');
      const second = document.createElementNS(svg.namespaceURI,'circle'); second.setAttribute('cx','17.6'); second.setAttribute('cy','20'); second.setAttribute('r','1.2');
      svg.append(first,second);
    }
    return svg;
  }

  function applyStoreNavigation() {
    document.querySelectorAll('[data-legend-store-nav]').forEach(node => node.remove());
    if (!storeIsEnabled()) return;
    const nav=document.querySelector('#primary-nav.nav,[data-public-nav].nav,.nav[data-public-nav]');
    if (!nav) return;

    const cluster=document.createElement('span');
    cluster.className='legend-store-nav-cluster';
    cluster.dataset.legendStoreNav='cluster';

    const store=document.createElement('a');
    store.href=storeContext.storefrontUrl;
    store.textContent=effectiveStoreLabel();
    store.dataset.legendStoreNav='store';
    store.dataset.cmsId=`${pageKey}.commerce.store-nav`;
    if(editorMode) store.dataset.cmsEditable='true';
    store.dataset.websiteAnalyticsEvent='cta_click';
    store.dataset.websiteBindingId='commerce_store_nav';
    store.dataset.cta='commerce_store';
    store.dataset.websiteActionKey='commerce_store';

    const cart=document.createElement('a');
    cart.href=storeContext.cartUrl;
    cart.dataset.legendStoreNav='cart';
    cart.dataset.cmsId=`${pageKey}.commerce.cart-nav`;
    if(editorMode) cart.dataset.cmsEditable='true';
    cart.dataset.websiteAnalyticsEvent='cta_click';
    cart.dataset.websiteBindingId='commerce_cart_nav';
    cart.dataset.cta='commerce_cart';
    cart.dataset.websiteActionKey='commerce_cart';
    cart.classList.add('legend-store-cart');
    cart.setAttribute('aria-label','Shopping cart');
    cart.appendChild(createStoreCartIcon());
    const count=document.createElement('span');
    count.className='pf-cart-count';
    count.id='pfStoreCartCount';
    count.textContent='0';
    count.setAttribute('aria-hidden','true');
    cart.appendChild(count);

    cluster.append(store,cart);
    nav.appendChild(cluster);
    applyCompositionNode(store,documentState.store?.storeNavigation || {});
    applyCompositionNode(cart,documentState.store?.cartNavigation || {});
    updateStoreCartCount();
  }

  function updateStoreCartCount() {
    const count=document.getElementById('pfStoreCartCount');
    if (!count || !storeContext?.businessKey) return;
    const key=`legendCommerceCart:${String(storeContext.businessKey).toLowerCase()}`;
    let items=[];
    try {
      const raw=window.localStorage?.getItem?.(key);
      const parsed=raw ? JSON.parse(raw) : [];
      if (Array.isArray(parsed)) items=parsed;
    } catch {}
    const total=items.reduce((sum,item)=>sum+Math.max(0,Number(item?.quantity)||0),0);
    count.textContent=String(total);
    count.hidden=total<1;
  }

  function publishedRouteCatalogEntries() {
    const entries=new Map();
    const catalog=Array.isArray(renderInput?.pageCatalog) ? renderInput.pageCatalog : (context.pages || []);
    catalog.forEach((page,index) => {
      const route=canonicalSiteRoute(page?.route || page?.path);
      if (!route) return;
      entries.set(route,{
        route,
        label:page.label || route,
        nativeRoute:page.template !== false,
        showInNavigation:page.showInNavigation !== false,
        parentPath:normalizePageRoute(page.parentPath),
        order:Number.isFinite(Number(page.order)) ? Number(page.order) : index * 10,
        deleted:page.deleted === true
      });
    });
    return entries;
  }

  function legacyPageForRoute(route) {
    if(!legacyMigration?.pages) return null;
    const direct=legacyMigration.pages[route] || (route==='/' ? legacyMigration.pages.home : null);
    if(direct) return direct;
    for(const [rawPath,page] of Object.entries(legacyMigration.pages))
      if(canonicalSiteRoute(rawPath)===route) return page;
    return null;
  }

  function websitePageEntries(includeDeleted = true) {
    const entries=publishedRouteCatalogEntries();
    for(const [rawPath,page] of Object.entries(documentState.pages || {})){
      const route=canonicalSiteRoute(rawPath);
      if(!route || !page || typeof page!=='object') continue;
      const previous=entries.get(route);
      const navigation=page.navigation || {};
      const legacy=legacyPageForRoute(route);
      entries.set(route,{
        route,
        label:SITE_KEY==='business' ? (navigation.label || previous?.label || page.title || route) : (page.title || navigation.label || previous?.label || route),
        nativeRoute:previous?.nativeRoute===true,
        legacyTemplatePath:normalizePageRoute(legacy?.templatePath),
        deleted:navigation.isDeleted===true,
        showInNavigation:navigation.showInNavigation!==false,
        parentPath:normalizePageRoute(navigation.parentPath),
        order:Number.isFinite(Number(navigation.order))?Number(navigation.order):0
      });
    }
    return [...entries.values()]
      .filter(entry=>includeDeleted || !entry.deleted)
      .sort((a,b)=>a.order-b.order || a.route.localeCompare(b.route));
  }

  function ensurePageRecord(route) {
    documentState.pages ||= {};
    if(!documentState.pages[route]){
      documentState.pages[route]={
        title:null,
        description:null,
        navigation:{showInNavigation:true,order:0,isDeleted:false},
        dynamicBinding:null,
        composition:[]
      };
    }
    const page=documentState.pages[route];
    page.composition ||= [];
    page.navigation ||= {showInNavigation:true,order:0,isDeleted:false};
    return page;
  }

  function editorUrlForRoute(route, materialize = false) {
    route=canonicalSiteRoute(route); if(!route) return null;
    const entry=websitePageEntries(true).find(value=>value.route===route);
    const url=new URL(managementPayload?.editorBaseUrl || location.origin);
    if (SITE_KEY==='business') {
      const nativePublishedRoute=Array.isArray(context.pages) && context.pages.some(page=>canonicalSiteRoute(page.path)===route) && (!legacyMigration || !entry?.legacyTemplatePath || entry.legacyTemplatePath===route);
      url.pathname='/business-preview/' + (nativePublishedRoute ? route.replace(/^\//,'') : '');
      url.searchParams.set('businessId',BUSINESS_ID);
      if (!nativePublishedRoute) url.searchParams.set('cmsPage',route);
    } else {
      const prefix = SITE_KEY === 'protect' ? (managementPayload?.agentSlug ? `/a/${encodeURIComponent(managementPayload.agentSlug)}` : context.pagePrefix || '') : '';
      url.pathname=prefix + (route==='/'?'/':route);
    }
    if(editorTicket) url.searchParams.set('legendEdit',editorTicket);
    if(materialize) url.searchParams.set('legendMaterialize','1');
    return url;
  }

  async function navigateToEditorPage(route,{replaceHistory=false}={}) {
    if (route === '__store__') { openStorePreview(); return; }
    closeStorePreview();
    route=normalizePageRoute(route); if(!route) return;
    if (saving) { const status=document.getElementById('legend-cms-status'); if(status) status.textContent='Wait for the current save to finish, then choose a page.'; return; }
    if(sourceEditorDirty) {
      const status=document.getElementById('legend-cms-status');
      if(status) status.textContent='Apply or discard Selected Source before opening another page.';
      showPanel('source'); return;
    }
    if (dirty) { const saved=await save(false); if(!saved || dirty) return; }

    // Canonical v3 Business pages are already loaded in the one site graph.
    // Switching pages is therefore a local workspace operation, not another
    // /manage bootstrap. Protect keeps navigation reloads where the server must
    // mount a route-specific executable runtime/template.
    if(editorMode && SITE_KEY==='business' && documentState.pages?.[route]){
      activeEditorRoute=route;
      pageKey=route==='/'?'home':route.slice(1).replace(/\//g,'-');
      setSelected(null);
      sourceEditorDirty=false;
      sourceEditorBaseNode=null;
      sourceEditorBaseFingerprint=null;
      canonicalSourceDocument=null;
      canonicalSourceRevision=null;
      applyDocument(documentState);
      refreshPageSelector();
      renderPageManager();
      syncPageControls();
      preservePreviewNavigation();
      const url=editorUrlForRoute(route);
      if(url){
        const method=replaceHistory?'replaceState':'pushState';
        history[method]({legendStudioRoute:route},'',url.toString());
      }
      refreshBrowserAgentWorkspace();
      return;
    }

    const url=editorUrlForRoute(route);
    if(url) location.assign(url.toString());
  }

  function currentMaterializedPage() {
    const identity=createMaterializationIdentityContext(currentPageRoute(),false);
    return {
      route:currentPageRoute(),
      title:pageState().title ?? originalTitle,
      description:pageState().description ?? originalDescription,
      systemTemplateKey:pageState().systemTemplateKey || null,
      composition:materializeCurrentPageComposition(identity),
      shell:materializeCurrentShell(identity)
    };
  }

  async function requestMaterializedPage(route) {
    if(route===currentPageRoute()) return currentMaterializedPage();
    const url=editorUrlForRoute(route,true);
    if(!url) throw new Error('Unable to resolve page '+route+' for Site Source migration.');
    return await new Promise((resolve,reject)=>{
      const frame=document.createElement('iframe');
      frame.hidden=true;
      frame.setAttribute('aria-hidden','true');
      frame.src=url.toString();
      const timeout=setTimeout(()=>finish(new Error('Timed out while materializing '+route+'.')),20000);
      const onMessage=event=>{
        if(event.origin!==location.origin || event.source!==frame.contentWindow) return;
        if(event.data?.type!=='legend-site-materialized-page') return;
        if(normalizePageRoute(event.data.route)!==route) return;
        finish(null,event.data);
      };
      const finish=(error,value)=>{
        clearTimeout(timeout); window.removeEventListener('message',onMessage); frame.remove();
        error ? reject(error) : resolve(value);
      };
      window.addEventListener('message',onMessage);
      document.body.appendChild(frame);
    });
  }

  function materializeLegacyReusableDefinitions() {
    const result={};
    const definitions=legacyMigration?.reusableComponents || {};

    const convert=(extra,componentId)=>{
      const type=extra?.type==='section'?'section'
        : extra?.type==='button'?(extra.actionKey?'cta':'link')
        : extra?.type==='image'?'image'
        : extra?.type==='video'?'video'
        : extra?.type==='code'?'embed'
        : extra?.type==='reusable'?'reusable'
        : 'text';
      return {
        id:componentId+'.'+String(extra.id || crypto.randomUUID()).replace(/[^a-zA-Z0-9_.:-]/g,'-'),
        type,
        tag:type==='section'?'section':type==='cta'||type==='link'?'a':type==='image'?'img':type==='video'?'video':'div',
        text:extra.text ?? null,
        title:extra.title ?? null,
        actionKey:extra.actionKey ?? null,
        href:extra.href ?? null,
        target:extra.target ?? null,
        alt:extra.alt ?? null,
        mediaAssetId:compositionMediaAssetId(extra.imageDataUrl || extra.videoUrl),
        mediaUrl:(extra.imageDataUrl || extra.videoUrl) ?? null,
        syncSourceId:extra.syncSourceId ?? null,
        signals:[],
        style:cloneCanonicalValue(extra.style || {}),
        breakpointStyles:cloneCanonicalValue(extra.breakpointStyles || {}),
        layout:cloneCanonicalValue(extra.layout || {}),
        breakpointLayouts:cloneCanonicalValue(extra.breakpointLayouts || {}),
        animations:cloneCanonicalValue(extra.animations || []),
        dataBinding:cloneCanonicalValue(extra.dataBinding || null),
        children:[]
      };
    };

    for(const [componentId,definition] of Object.entries(definitions)){
      const extras=Array.isArray(definition?.extras)?definition.extras:[];
      const nodes=new Map(extras.map(extra=>[extra.id,convert(extra,componentId)]));
      const roots=[];
      for(const extra of extras){
        const node=nodes.get(extra.id); if(!node) continue;
        const sectionId=String(extra.sectionId || '');
        const parentId=sectionId.startsWith('extra:') ? sectionId.slice(6) : null;
        const parent=parentId ? nodes.get(parentId) : null;
        if(parent) parent.children.push(node);
        else roots.push(node);
      }
      result[componentId]={
        id:componentId,
        name:definition?.name || componentId,
        kind:definition?.kind==='block'?'block':'section',
        composition:roots
      };
    }
    return result;
  }

  async function materializeCanonicalSite() {
    if(!legacyMigration) return true;
    const status=document.getElementById('legend-cms-status');
    const entries=websitePageEntries(false).filter(entry=>!entry.deleted);
    const routes=[...new Set([currentPageRoute(),...entries.map(entry=>entry.route).filter(Boolean)])];
    const snapshots=[];

    for(const route of routes){
      if(status) status.textContent='Preparing canonical Site Source · '+(snapshots.length+1)+'/'+routes.length;
      snapshots.push(await requestMaterializedPage(route));
    }

    const next=normalizeDocument(documentState);
    const firstShell=snapshots.find(snapshot=>snapshot?.shell)?.shell;
    next.shell={
      header:normalizeCompositionNodes(firstShell?.header),
      footer:normalizeCompositionNodes(firstShell?.footer)
    };
    next.reusableComponents=materializeLegacyReusableDefinitions();

    for(const snapshot of snapshots){
      const route=normalizePageRoute(snapshot.route);
      if(!route) throw new Error('Materialized page route was invalid.');
      const page=next.pages[route] || {navigation:{showInNavigation:true,order:0,isDeleted:false},composition:[]};
      const routeEntry=entries.find(entry=>entry.route===route);
      const snapshotTitle=cleanBusinessPreviewTitle(snapshot.title);
      page.title=snapshotTitle ?? page.title;
      page.description=snapshot.description ?? page.description;
      page.systemTemplateKey=snapshot.systemTemplateKey ?? page.systemTemplateKey ?? null;
      page.navigation ||= {showInNavigation:true,order:0,isDeleted:false};
      if(!String(page.navigation.label||'').trim())
        page.navigation.label=routeEntry?.label || defaultNavigationLabel(route,page.title);
      page.composition=normalizeCompositionNodes(snapshot.composition);
      next.pages[route]=page;
    }

    next.version=3;
    legacyMigration=null;
    materializationSavePending=true;
    documentState=normalizeDocument(next);
    dirty=true;
    const saved=await save(false);
    if(!saved || dirty) throw new Error('Canonical Site Source migration could not be saved.');
    applyDocument(documentState);
    if(status) status.textContent='Site Source ready · legacy mutation authority deleted';
    return true;
  }
  // END ONE-WAY LEGACY V2 -> CANONICAL V3 MIGRATION.

  function renderPageManager() {
    const host=document.getElementById('legend-cms-page-list');
    if (!host?.replaceChildren) return;
    host.replaceChildren();
    const current=currentPageRoute();
    const entries=websitePageEntries(true);
    for (const entry of entries) {
      const row=document.createElement('div'); row.className='legend-cms-page-row';
      const open=document.createElement('button'); open.type='button'; open.textContent=entry.route;
      open.disabled=entry.deleted; open.setAttribute('aria-current',String(entry.route===current));
      open.addEventListener('click',()=>void navigateToEditorPage(entry.route)); row.appendChild(open);

      if (SITE_KEY==='business') {
        const label=document.createElement('input');
        label.type='text'; label.maxLength=120; label.value=entry.label; label.setAttribute('aria-label',`Navigation label for ${entry.route}`);
        label.disabled=entry.deleted;
        label.addEventListener('change',event=>{
          const value=String(event.target.value||'').trim().slice(0,120);
          if(!value){ event.target.value=entry.label; return; }
          checkpoint();
          const page=ensurePageRecord(entry.route);
          page.navigation.label=value;
          markDirty();
          applyBusinessPageNavigation();
          refreshPageSelector();
          renderPageManager();
        });
        row.appendChild(label);

        const visible=document.createElement('input');
        visible.type='checkbox'; visible.checked=entry.showInNavigation!==false && !entry.deleted;
        visible.disabled=entry.deleted;
        visible.setAttribute('aria-label',`Show ${entry.label} in navigation`);
        visible.addEventListener('change',event=>{
          checkpoint();
          const page=ensurePageRecord(entry.route);
          page.navigation.showInNavigation=event.target.checked;
          markDirty();
          applyBusinessPageNavigation();
          refreshPageSelector();
        });
        row.appendChild(visible);

        const remove=document.createElement('button'); remove.type='button';
        const home=entry.route==='/';
        remove.disabled=home;
        remove.textContent=entry.deleted?'Restore':'Delete';
        remove.title=home?'The home page is required. Rename its navigation label or hide it from navigation instead.':'';
        remove.addEventListener('click',async()=>{
          if(home) return;
          checkpoint();
          const page=ensurePageRecord(entry.route);
          page.navigation.isDeleted=!page.navigation.isDeleted;
          if(page.navigation.isDeleted) page.navigation.showInNavigation=false;
          markDirty();
          applyBusinessPageNavigation();
          refreshPageSelector();
          renderPageManager();
          if(page.navigation.isDeleted && entry.route===current) await navigateToEditorPage('/');
        });
        row.appendChild(remove);
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
    if (storeIsEnabled()) {
      const option=document.createElement('option');
      option.value='__store__';
      option.textContent=effectiveStoreLabel();
      select.appendChild(option);
    }
    if (storePreviewActive && storeIsEnabled()) select.value='__store__';
    else if ([...select.options].some(option=>option.value===current)) select.value=current;
  }

  function installPageSelector() {
    const panel=document.querySelector('.legend-cms-panel'); if(!panel) return;
    const label=document.createElement('label'); label.className='legend-cms-group'; label.textContent='Website page';
    const select=document.createElement('select'); select.id='legend-cms-page-select'; select.setAttribute('aria-label','Website page'); label.appendChild(select);
    select.addEventListener('change',()=>void navigateToEditorPage(select.value));
    panel.insertBefore(label,panel.querySelector('.legend-cms-navigation'));

    const storeGroup=document.createElement('div');
    storeGroup.id='legend-cms-store-controls';
    storeGroup.className='legend-cms-group legend-cms-store-controls';
    label.after(storeGroup);
    renderStoreControls();

    refreshPageSelector();
    syncPageControls();
  }

  function renderStoreControls() {
    const host=document.getElementById('legend-cms-store-controls');
    if (!host) return;
    host.replaceChildren();

    const enabled=storeIsEnabled();
    const action=document.createElement('button');
    action.type='button';
    action.id='legend-cms-store-toggle';
    action.textContent=enabled?'Remove Store':'Add Store';
    action.addEventListener('click',()=>void updateStore(!enabled));
    if (!enabled) {
      host.appendChild(action);
      return;
    }

    const settings=document.createElement('div');
    settings.className='legend-cms-store-settings';

    const label=document.createElement('label');
    label.textContent='Navigation name';
    const input=document.createElement('input');
    input.id='legend-cms-store-label';
    input.type='text';
    input.maxLength=40;
    input.value=effectiveStoreLabel();
    input.placeholder='Store or Shop';
    input.addEventListener('change',()=>void updateStore(true,input.value,effectiveCartIcon(),effectiveCartIconSize()));
    label.appendChild(input);

    const iconLabel=document.createElement('label');
    iconLabel.textContent='Cart icon';
    const icon=document.createElement('select');
    icon.id='legend-cms-store-cart-icon';
    for(const [value,text] of [['cart','Cart · Parfait'],['bag','Shopping bag'],['basket','Basket']]) {
      const option=document.createElement('option'); option.value=value; option.textContent=text; icon.appendChild(option);
    }
    icon.value=effectiveCartIcon();
    icon.addEventListener('change',()=>void updateStore(true,effectiveStoreLabel(),icon.value,effectiveCartIconSize()));
    iconLabel.appendChild(icon);

    const sizeLabel=document.createElement('label');
    sizeLabel.textContent='Cart icon size';
    const size=document.createElement('input');
    size.id='legend-cms-store-cart-size';
    size.type='number'; size.min='16'; size.max='96'; size.step='1';
    size.value=String(effectiveCartIconSize());
    size.addEventListener('change',()=>void updateStore(true,effectiveStoreLabel(),effectiveCartIcon(),Number(size.value)));
    sizeLabel.appendChild(size);
    settings.append(label,iconLabel,sizeLabel);
    host.appendChild(settings);

    const actions=document.createElement('div');
    actions.className='legend-cms-store-actions';
    const manage=document.createElement('button');
    manage.type='button';
    manage.id='legend-cms-manage-store';
    manage.textContent='Manage products';
    manage.disabled=!storeContext?.managerUrl;
    manage.addEventListener('click',openStoreManager);
    actions.append(action,manage);
    host.insertBefore(actions,settings);
  }

  async function updateStore(enabled,labelValue=null,cartIconValue=null,cartIconSizeValue=null) {
    if (!editorMode || saving) return;
    const status=document.getElementById('legend-cms-status');
    if(status) status.textContent=enabled?'Setting up your store…':'Removing Store page…';
    try {
      const response=await fetch(`${API_BASE}/api/website-content/manage/store/${enabled?'enable':'remove'}`,{
        method:'POST',
        headers:{'Content-Type':'application/json'},
        body:JSON.stringify({ticket:editorTicket,expectedRevision:revision,navigationLabel:labelValue || effectiveStoreLabel(),cartIcon:cartIconValue || effectiveCartIcon(),cartIconSizePx:Number.isFinite(Number(cartIconSizeValue))?Number(cartIconSizeValue):effectiveCartIconSize()})
      });
      const payload=await response.json().catch(()=>({}));
      if(!response.ok) throw new Error(payload.message || payload.error || `Store update failed (${response.status})`);
      revision=payload.revision;
      documentState=normalizeDocument(payload.document || documentState);
      storeContext=payload.store || null;
      dirty=false;
      applyStoreNavigation();
      if(!storeIsEnabled()) closeStorePreview();
      refreshPageSelector();
      renderStoreControls();
      refreshHistoryControls();
      if(status) status.textContent=enabled?'Store ready in this draft. Publish when you want it public.':'Store page removed. Commerce data is preserved.';
    } catch(error) {
      if(status) status.textContent=error?.message || 'Store update failed.';
    }
  }

  function storePreviewUrl() {
    if (!storeContext) return null;
    return storeContext.previewUrl || storeContext.storefrontUrl || null;
  }

  function openStorePreview() {
    if (!storeIsEnabled() || !editorPreview) return;
    storePreviewActive=true;
    setSelected(null);
    let overlay=editorPreview.querySelector('.legend-cms-store-preview');
    if(!overlay) {
      overlay=document.createElement('div');
      overlay.className='legend-cms-editor legend-cms-store-preview';
      const bar=document.createElement('div'); bar.className='legend-cms-store-preview-bar';
      const title=document.createElement('strong'); title.textContent='Store preview';
      const manage=document.createElement('button'); manage.type='button'; manage.textContent='Manage Store'; manage.addEventListener('click',openStoreManager);
      bar.append(title,manage);
      const frame=document.createElement('iframe'); frame.className='legend-cms-store-preview-frame'; frame.title='Store preview';
      overlay.append(bar,frame);
      editorPreview.appendChild(overlay);
    }
    const frame=overlay.querySelector('iframe');
    const url=storePreviewUrl();
    if(frame && url && frame.src!==new URL(url,location.href).href) frame.src=url;
    overlay.hidden=false;
    refreshPageSelector();
  }

  function closeStorePreview() {
    storePreviewActive=false;
    const overlay=editorPreview?.querySelector?.('.legend-cms-store-preview');
    if(overlay) overlay.hidden=true;
  }

  function openStoreManager() {
    if(!storeContext?.managerUrl) return;
    let modal=document.querySelector('.legend-cms-store-manager');
    if(!modal) {
      modal=document.createElement('div');
      modal.className='legend-cms-editor legend-cms-store-manager';
      modal.setAttribute('role','dialog');
      modal.setAttribute('aria-modal','true');
      modal.setAttribute('aria-label','Manage Store');
      const shell=document.createElement('div'); shell.className='legend-cms-store-manager-shell';
      const top=document.createElement('div'); top.className='legend-cms-store-manager-top';
      const title=document.createElement('strong'); title.textContent='Manage Store';
      const close=document.createElement('button'); close.type='button'; close.textContent='Close'; close.addEventListener('click',()=>{modal.hidden=true;});
      top.append(title,close);
      const frame=document.createElement('iframe'); frame.className='legend-cms-store-manager-frame'; frame.title='Manage Store workspace';
      shell.append(top,frame); modal.appendChild(shell); document.body.appendChild(modal);
    }
    const frame=modal.querySelector('iframe');
    if(frame) frame.src=storeContext.managerUrl;
    modal.hidden=false;
  }
  function preservePreviewNavigation() {
    if (renderInput || SITE_KEY !== 'business' || !location.pathname.startsWith('/business-preview')) return;
    const routes=new Set(websitePageEntries(false).map(entry=>entry.route));
    document.querySelectorAll('a[href]').forEach(el => {
      const url=new URL(el.getAttribute('href'),location.origin);
      if(url.origin!==location.origin || el.getAttribute('href').startsWith('#')) return;
      const route=canonicalSiteRoute(url.searchParams.get('cmsPage') || url.pathname.replace(/^\/business-preview/,'') || '/');
      if(!routes.has(route)) return;
      const scoped=editorUrlForRoute(route);
      scoped.hash=url.hash;
      el.href=scoped.toString();
    });
  }
  function canonicalProtectedEditCorrection() {
    return managementPayload?.agentContract?.protectedEditCorrection ||
      'CANONICAL CORRECTION REQUIRED: preserve the existing stable node ID and component type; restore the protected component from the current canonical draft and change only allowed presentation.';
  }

  function clearCanonicalProtectionViolation() {
    for (const warning of document.querySelectorAll('[data-canonical-protection-warning]')) {
      warning.hidden = true;
      warning.textContent = '';
    }
    delete window.LEGEND_WEBSITE_STUDIO_PROTECTION_VIOLATION;
  }

  function showCanonicalProtectionViolation(message, correction = canonicalProtectedEditCorrection()) {
    const safeMessage = String(message || 'Website Studio rejected a protected edit.');
    const safeCorrection = String(correction || canonicalProtectedEditCorrection());
    const text = 'CANONICAL PROTECTION BLOCKED THIS EDIT: ' + safeMessage + '\n\nGPT REDIRECT: ' + safeCorrection;
    window.LEGEND_WEBSITE_STUDIO_PROTECTION_VIOLATION = Object.freeze({
      message: safeMessage,
      correction: safeCorrection
    });
    let warnings = [...document.querySelectorAll('[data-canonical-protection-warning]')];
    if (!warnings.length) {
      const warning = document.createElement('div');
      warning.dataset.canonicalProtectionWarning = 'true';
      warning.className = 'legend-cms-protection-warning';
      warning.setAttribute('role', 'alert');
      document.body.prepend(warning);
      warnings = [warning];
    }
    for (const warning of warnings) {
      warning.hidden = false;
      warning.textContent = text;
    }
    document.documentElement.hidden = false;
    return text;
  }

  function editorAuthorizationUrl() {
    const raw = typeof context.editorAuthorizationUrl === 'string' ? context.editorAuthorizationUrl.trim() : '';
    if (!raw) return null;
    try {
      const url = new URL(raw, location.origin);
      const localHttp = url.protocol === 'http:' && ['localhost','127.0.0.1'].includes(url.hostname);
      if (url.protocol !== 'https:' && !localHttp) return null;
      return url.href;
    } catch {
      return null;
    }
  }

  function showEditorAuthorizationRecovery(message = 'Website Studio needs a fresh authorized session.') {
    const href = editorAuthorizationUrl();
    if (!href) return false;
    window.LEGEND_WEBSITE_EDITOR_REAUTHORIZE_URL = href;
    let host = document.querySelector('[data-legend-editor-reauthorize-panel]');
    if (!host) {
      host = document.createElement('aside');
      host.dataset.legendEditorReauthorizePanel = 'true';
      host.setAttribute('role', 'alert');
      host.style.cssText = 'position:fixed;z-index:2147483646;left:max(12px,env(safe-area-inset-left));right:max(12px,env(safe-area-inset-right));top:max(12px,env(safe-area-inset-top));max-width:720px;margin:0 auto;padding:16px 18px;border:1px solid #d4ad45;border-radius:14px;background:#081a32;color:#f7f6f2;box-shadow:0 18px 60px rgba(0,0,0,.35);font:600 14px/1.45 system-ui,sans-serif';
      const copy = document.createElement('p');
      copy.style.margin = '0 0 12px';
      const link = document.createElement('a');
      link.dataset.legendEditorReauthorize = 'true';
      link.textContent = 'Reauthorize Website Studio';
      link.style.cssText = 'display:inline-flex;align-items:center;justify-content:center;min-height:44px;padding:10px 14px;border-radius:10px;background:#d4ad45;color:#081a32;text-decoration:none;font-weight:800';
      host.append(copy, link);
      document.body.prepend(host);
    }
    host.querySelector('p').textContent = message + ' Continue through the normal Agent Portal sign-in/approval flow; the portal will mint a new scoped editor ticket.';
    host.querySelector('[data-legend-editor-reauthorize]').href = href;
    document.documentElement.hidden = false;
    return true;
  }

  function unavailable(error) {
    if (SITE_KEY === 'business') {
      document.body.replaceChildren();
      const main = document.createElement('main');
      main.textContent = error.message || 'This website is unavailable.';
      document.body.appendChild(main);
    }
    document.documentElement.hidden = false;
  }

  let publicRuntimeStarted = false;
  let publicRuntimeStarting = false;
  let publicRuntimeRetryCount = 0;
  let publicRuntimeRetryTimer = null;

  function loadRuntimeScript(src) {
    return new Promise((resolve, reject) => {
      if (!src) { resolve(); return; }

      const absoluteSrc = new URL(src, location.origin).href;
      let script = [...document.scripts].find(candidate => candidate.src === absoluteSrc);

      const bind = node => {
        node.addEventListener('load', () => {
          node.dataset.legendRuntimeLoaded = 'true';
          delete node.dataset.legendRuntimeFailed;
          resolve();
        }, { once: true });
        node.addEventListener('error', () => {
          node.dataset.legendRuntimeFailed = 'true';
          node.remove();
          reject(new Error(`Runtime script failed to load: ${absoluteSrc}`));
        }, { once: true });
      };

      if (script) {
        if (script.dataset.legendRuntimeLoaded === 'true') {
          resolve();
          return;
        }
        if (script.dataset.legendRuntimeFailed === 'true') {
          script.remove();
          script = null;
        } else {
          bind(script);
          return;
        }
      }

      script = document.createElement('script');
      script.src = absoluteSrc;
      script.async = true;
      bind(script);
      document.head.appendChild(script);
    });
  }

  function schedulePublicRuntimeRetry() {
    if (publicRuntimeStarted || publicRuntimeRetryCount >= 3 || publicRuntimeRetryTimer) return;
    publicRuntimeRetryCount += 1;
    const delayMs = Math.min(15000, publicRuntimeRetryCount * 3000);
    publicRuntimeRetryTimer = window.setTimeout(() => {
      publicRuntimeRetryTimer = null;
      void startPublicRuntime();
    }, delayMs);
  }

  function initializeMetaPixel(pixelId) {
    if (!pixelId || typeof window === 'undefined') return;
    if (window.LegendAnalytics?.measurementConsent?.isAllowed?.() !== true) return;
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
      const option = key && options.find(candidate => candidate.key === key);
      if (!option) return;
      element.dataset.websiteActionKey = option.key;
      element.dataset.websiteBindingId = element.dataset.cmsId || option.key;
      element.dataset.websiteAnalyticsEvent = option.analyticsEventName || 'cta_click';
      element.dataset.websiteBehaviorKey = option.behaviorKey || 'cta_click';
      if (option.runtimeAction) element.dataset.websiteRuntimeAction = option.runtimeAction;
      else delete element.dataset.websiteRuntimeAction;
      if (option.metaIntentEventName) element.dataset.websiteMetaIntent = option.metaIntentEventName;
      else delete element.dataset.websiteMetaIntent;
      if (element.tagName === 'A' && option.href && safeUrl(option.href)) {
        element.href = option.href;
        element.target = option.openInNewTab === true ? '_blank' : '_self';
        element.rel = 'noopener noreferrer';
      }
    });
  }

  function installPublishedSignalBindings() {
    if (editorMode || renderInput?.server) return;
    const analytics = window.LegendAnalytics;
    if (!analytics || typeof analytics.trackBinding !== 'function') return;

    const allowedTriggers = new Set([
      'viewed', 'click', 'form_started', 'submit_attempt',
      'field_started', 'validation_failed', 'field_completed', 'scroll_threshold'
    ]);
    const page = pageState();
    const candidates = [];
    if (!legacyMigration) {
      walkComposition(page.composition,node=>{
        const element=findEditableElement(node.id);
        candidates.push({id:node.id,model:node,node:element});
        if(element && node.fieldSignals && typeof node.fieldSignals==='object'){
          for(const [fieldKey,signals] of Object.entries(node.fieldSignals)){
            const control=[...element.querySelectorAll('input,select,textarea,button[data-cms-field-key]')]
              .find(candidate=>formFieldKey(candidate)===fieldKey);
            if(control) candidates.push({
              id:node.id+':field:'+fieldKey,
              model:{signals},
              node:control
            });
          }
        }
      });
    } else {
      const legacyPage=legacyMigrationPageState();
      candidates.push(
        ...Object.entries(legacyPage.elements || {}).map(([id, model]) => ({ id, model, node:findEditableElement(id) })),
        ...(legacyPage.extras || []).map(extra => ({
          id:'extra:'+extra.id,
          model:extra,
          node:document.querySelector('[data-cms-id="extra:'+CSS.escape(extra.id)+'"]')
        }))
      );
    }
    window.__legendWebsiteSignalBindingsCleanup?.();
    const cleanups = [];

    const emit = (binding, elementId) => {
      if (!binding || binding.deliveryMode === 'off' || !allowedTriggers.has(binding.trigger)) return;
      analytics.trackBinding(binding, {
        elementId,
        pagePath: currentPageRoute(),
        source: 'website_signal_binding'
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
      if (!candidate.node || !Array.isArray(candidate.model?.signals)) continue;
      for (const binding of candidate.model.signals) {
        if (!binding?.id || !binding.eventName || binding.deliveryMode === 'off' || !allowedTriggers.has(binding.trigger)) continue;
        // Managed actions enrich the existing source envelope instead of creating
        // a second event for this visual binding.
        const managedForm = candidate.node.matches?.('form') ? candidate.node : candidate.node.closest?.('form');
        const managedAction = candidate.node.matches?.('[data-website-action-key],[data-cta]');
        if (window.LegendAnalytics?.registerBinding &&
            ((managedForm && ['form_started','submit_attempt','field_started','validation_failed','field_completed'].includes(binding.trigger)) ||
             (managedAction && binding.trigger === 'click'))) {
          cleanups.push(window.LegendAnalytics.registerBinding(candidate.node, binding, candidate.id));
          continue;
        }
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

    window.__legendWebsiteSignalBindingsCleanup = () => cleanups.forEach(cleanup => cleanup());
  }

  async function startPublicRuntime() {
    if (publicRuntimeStarted || publicRuntimeStarting || editorMode || renderInput?.server) return;
    if (!['legend','business'].includes(SITE_KEY)) return;
    if (!context.trackingAsset) return;
    if (SITE_KEY === 'business' && location.pathname.startsWith('/business-preview')) return;

    // A linked storefront retains the published shell CMS for content/actions,
    // while the server commerce bootstrap owns the one tracking/provider runtime.
    const reuseCommerceRuntime = window.LEGEND_ANALYTICS_CONFIG?.runtimeOwner === 'commerce';
    publicRuntimeStarting = true;
    let payload;
    try {
      const runtimeUrl = new URL(`${API_BASE}/api/website-content/public/runtime`);
      runtimeUrl.searchParams.set('siteKey', SITE_KEY);
      const response = await fetch(runtimeUrl, { cache: 'no-store' });
      if (!response.ok) throw new Error('Public website runtime is unavailable.');

      payload = await response.json();
      ctaCatalog = Array.isArray(payload.ctaCatalog?.options) ? payload.ctaCatalog.options : [];
      applyRuntimeActionContracts();

      const analytics = payload.analytics || {};
      if (!analytics.endpoint || !Array.isArray(analytics.allowedBrowserEvents)) {
        throw new Error('Canonical analytics runtime configuration is unavailable.');
      }

      if (!reuseCommerceRuntime) {
        window.LEGEND_ANALYTICS_CONFIG = {
          ...analytics,
          siteKey: SITE_KEY,
          publishedVersionId: payload.publishedVersionId || null
        };
        document.body.dataset.pageKey ||= pageKey;

        // Analytics is foundational. Load it before any optional advertising or
        // measurement projection so provider failures cannot suppress traffic.
        const trackingAsset = context.trackingAsset || '/legend-public-tracking.js';
        await loadRuntimeScript(trackingAsset);
        if (window.__legendTrackingInitialized !== true || typeof window.LegendAnalytics?.track !== 'function') {
          // A downloaded script can still throw during execution. Remove that failed
          // attempt so the retry can execute it again after tracker cleanup.
          const source = new URL(trackingAsset, location.origin).href;
          [...document.scripts].find(script => script.src === source)?.remove();
          throw new Error('Canonical analytics tracker did not initialize.');
        }
      } else if (window.__legendTrackingInitialized !== true || typeof window.LegendAnalytics?.track !== 'function') {
        throw new Error('The owning commerce analytics runtime is unavailable.');
      }
      publicRuntimeStarted = true;
      publicRuntimeRetryCount = 0;
      if (publicRuntimeRetryTimer) {
        window.clearTimeout(publicRuntimeRetryTimer);
        publicRuntimeRetryTimer = null;
      }
    } catch (error) {
      console.error('[legend-public-runtime]', error);
      schedulePublicRuntimeRetry();
      return;
    } finally {
      publicRuntimeStarting = false;
    }

    installPublishedSignalBindings();
    if (reuseCommerceRuntime) return;
    const meta = payload?.meta || {};
    try {
      initializeMetaPixel(meta.pixelId);
    } catch (error) {
      console.error('[legend-public-meta-runtime]', error);
    }

    const openai = payload?.openai || {};
    if (openai.enabled && openai.pixelId && context.openAiMeasurementAsset) {
      try {
        await loadRuntimeScript(context.openAiMeasurementAsset || '/legend-public-openai-measurement.js');
        await window.LegendOpenAiMeasurement?.configure?.({ pixelId: openai.pixelId });
      } catch (error) {
        console.error('[legend-public-openai-runtime]', error);
      }
    }

    if (context.metaSignalAsset) {
      try {
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
        }
      } catch (error) {
        console.error('[legend-public-meta-signal-runtime]', error);
      }
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
      if (!response.ok) {
        if (SITE_KEY === 'business') throw new Error('This business website is unavailable.');
        document.documentElement.hidden = false;
        return;
      }
      const payload = await response.json();
      storeContext = payload.store || null;
      legacyMigration = payload.legacyMigration || null;
      ctaCatalog = Array.isArray(payload.ctaCatalog?.options) ? payload.ctaCatalog.options : ctaCatalog;
      if (payload.businessName) {
        document.querySelectorAll('[data-business-name]').forEach(element => {
          element.textContent = payload.businessName;
        });
      }
      bindBusiness(payload);
      prepareDom();
      applyDocument(payload.document || {});
      applyRuntimeActionContracts();
      if (SITE_KEY === 'protect' && window.__legendTrackingInitialized === true)
        installPublishedSignalBindings();
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

  function markDirty() {
    const selectedForm=selected?.closest?.('form[data-cms-composition-id]');
    const preferredId=selectedForm?.dataset?.cmsCompositionId || selected?.dataset?.cmsCompositionId || null;
    synchronizeCanonicalSharedPresentation(documentState,preferredId);
    if(checkpointBaseline && !suppressHistoryCapture){
      const undo=buildCreativeMutationOperations(documentState,checkpointBaseline);
      const redo=buildCreativeMutationOperations(checkpointBaseline,documentState);
      if(undo.length || redo.length){
        undoStack.push({undo,redo});
        if(undoStack.length>120) undoStack.shift();
        redoStack.length=0;
      }
    }
    checkpointBaseline=null;
    dirty = true;
    refreshHistoryControls();
    const status = document.getElementById('legend-cms-status');
    if (status) status.textContent = 'Saving changes…';
    if (activeEditorPanel === 'source' && !sourceEditorDirty) void refreshSiteSourceEditor();
    if (editorMode) {
      clearTimeout(autoSaveTimer);
      autoSaveTimer = setTimeout(() => { if (dirty && !saving) void save(false); }, 900);
    }
  }

  function setSelected(el, { openContent = false } = {}) {
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
    if (activeEditorPanel === 'motion') renderMotionControls();
    if (activeEditorPanel === 'data') renderDataControls();
    if (activeEditorPanel === 'source' && !sourceEditorDirty) {
      const scope=document.getElementById('legend-cms-source-scope');
      if(scope && el?.dataset?.cmsCompositionId) scope.value='selection';
      void refreshSiteSourceEditor();
    }
    if (activeEditorPanel === 'gpt') refreshBrowserAgentWorkspace();
    if (openContent) showPanel('content');
    refreshLayers();
    updateDirectCanvasUi();
  }

  function selectedWebsiteModel(create = true) {
    const model = editableWebsiteModelForElement(selected, create);
    const composition=selected?.dataset?.cmsCompositionId ? compositionNodeForElement(selected,create) : null;
    if (create && composition && selected?.dataset.websiteActionKey && !Object.hasOwn(composition, 'actionKey'))
      composition.actionKey = selected.dataset.websiteActionKey;
    return model;
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
    if (!selectionFrame || !editorPreview || !selected || (selected.dataset.cmsSignalOnly && !formFieldPresentationForElement(selected,false)) || selected.closest('.legend-cms-editor')) {
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
    selectionFrame.dataset.textEditing = inlineEditNode === selected ? 'true' : 'false';
    if (directGesture) positionGridOverlay(directGesture.section);
  }

  function lockPreviewHorizontalScroll() {
    if (!editorPreview || editorPreview.scrollLeft === 0) return;
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
      if (!selected || (selected.dataset.cmsSignalOnly && !formFieldPresentationForElement(selected,false)) || inlineEditNode === selected) return false;
      const section = selectedSection || currentSectionFor(selected);
      const parent = selected.parentElement;
      if (!section || !parent) return false;
      if (mode === 'move' && selected.dataset.cmsSection) return false;
      const selectedRect = selected.getBoundingClientRect();
      const sectionRect = section.getBoundingClientRect();
      const parentRect = parent.getBoundingClientRect();
      const model = selectedWebsiteModel();
      if (!model) return false;
      const gestureStyle = editingStyle(model, true);
      checkpoint();
      const measuredWidthPercent = parentRect.width > 0 ? Math.min(100, selectedRect.width / parentRect.width * 100) : 100;
      const startWidthPercent = positiveNumber(gestureStyle.widthPercent) ? Math.min(100, Number(gestureStyle.widthPercent)) : measuredWidthPercent;
      if (mode === 'move' && !positiveNumber(gestureStyle.widthPercent)) gestureStyle.widthPercent = startWidthPercent;
      directGesture = {
        mode, edge, target: selected, section, parent,
        startX: event.clientX, startY: event.clientY,
        selectedRect, sectionRect, parentRect,
        startWidthPercent,
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
      const model = selectedWebsiteModel();
      if (!model) return;
      const style = gesture.style;
      const sectionWidth = gesture.sectionRect.width || gesture.parentRect.width || 1;
      const parentWidth = gesture.parentRect.width || sectionWidth || 1;
      let nextDx = dx;
      let nextDy = dy;
      gridOverlay.classList.remove('legend-cms-snap-x','legend-cms-snap-y');

      if (gesture.mode === 'move') {
        // Direct manipulation is continuous: no grid snap, no center magnet.
        // The section boundary is the only containment rule.
        const minDx = gesture.sectionRect.left - gesture.selectedRect.left;
        const maxDx = gesture.sectionRect.right - gesture.selectedRect.right;
        const minDy = gesture.sectionRect.top - gesture.selectedRect.top;
        const maxDy = gesture.sectionRect.bottom - gesture.selectedRect.bottom;
        nextDx = Math.max(minDx, Math.min(maxDx, nextDx));
        nextDy = Math.max(minDy, Math.min(maxDy, nextDy));
        style.offsetXPercent = Math.round((gesture.startOffsetXPercent + nextDx / parentWidth * 100) * 1000) / 1000;
        style.offsetYPx = Math.round((gesture.startOffsetYPx + nextDy) * 1000) / 1000;
      } else {
        if (gesture.mode === 'resize-x' || gesture.mode === 'resize-xy') {
          const fromLeft = gesture.edge.includes('left');
          const widthDelta = (fromLeft ? -dx : dx) / parentWidth * 100;
          const rawWidth = gesture.startWidthPercent + widthDelta;
          style.widthPercent = Math.max(5, Math.min(100, Math.round(rawWidth * 1000) / 1000));
          if (fromLeft && !gesture.target.dataset.cmsSection) {
            style.offsetXPercent = Math.round((gesture.startOffsetXPercent + dx / parentWidth * 100) * 1000) / 1000;
          }
        }
        if (gesture.mode === 'resize-y' || gesture.mode === 'resize-xy') {
          const fromTop = gesture.edge.includes('top');
          const heightDelta = fromTop ? -dy : dy;
          style.heightPx = Math.max(24, Math.round((gesture.startHeightPx + heightDelta) * 1000) / 1000);
          // Whole sections stay in document flow. Resizing their top edge must
          // never create a relative top offset (which leaves phantom space).
          if (fromTop && !gesture.target.dataset.cmsSection) {
            style.offsetYPx = Math.round((gesture.startOffsetYPx + dy) * 1000) / 1000;
          }
        }
      }
      const constrainedWidth = positiveNumber(style.widthPercent)
        ? Math.min(100, Number(style.widthPercent))
        : Math.min(100, gesture.startWidthPercent || 100);
      style.widthPercent = constrainedWidth;
      if (Number.isFinite(Number(style.offsetXPercent))) {
        style.offsetXPercent = gesture.mode === 'move'
          ? Math.max(0, Math.min(95, Number(style.offsetXPercent)))
          : Math.max(0, Math.min(Math.max(0, 100 - constrainedWidth), Number(style.offsetXPercent)));
      }
      gesture.changed = true;
      applyCompositionNode(selected, model);
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
      lockPreviewHorizontalScroll();
      updateDirectCanvasUi();
    }, { passive: true });
    preview.addEventListener('wheel', event => {
      if (Math.abs(event.deltaX) > 0 || event.shiftKey) event.preventDefault();
    }, { passive: false });
    window.addEventListener('resize', () => {
      lockPreviewHorizontalScroll();
      refreshResponsiveComposition();
    });
    lockPreviewHorizontalScroll();
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

    const selectedFieldPresentation=selected?.dataset?.cmsSignalOnly ? formFieldPresentationForElement(selected,true) : null;
    document.querySelectorAll('[data-cms-view="content"] input,[data-cms-view="content"] textarea,[data-cms-view="content"] select,[data-cms-view="appearance"] input,[data-cms-view="appearance"] select,[data-cms-view="layout"] input,[data-cms-view="layout"] select').forEach(control => { control.disabled = !selected || (!!selected.dataset.cmsSignalOnly && !selectedFieldPresentation); });
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
    const selectedNode = selected.dataset.cmsCompositionId ? compositionNode(selected.dataset.cmsCompositionId) : null;
    const isCode = selectedNode?.type === 'embed';
    if (inlineHelp) inlineHelp.hidden = !isInlineEditable(selected);
    if (imageGroup) imageGroup.hidden = !isImage;
    if (codeGroup) codeGroup.hidden = !isCode;

    const ov = selectedWebsiteModel(false) || {};
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
    const canonicalForm = ov.type === 'form' || String(ov.systemKey || '').startsWith('protect_runtime_form:');
    const protectedMappings = (Array.isArray(ov.signals) && ov.signals.length > 0) ||
      Object.values(ov.fieldSignals || {}).some(bindings=>Array.isArray(bindings) && bindings.length > 0);
    const duplicateButton = document.getElementById('legend-cms-duplicate');
    if (duplicateButton) {
      const immutableShell = selected.matches?.('.site-header,.site-footer');
      duplicateButton.textContent = serviceCard ? 'Duplicate service' : selected.dataset.cmsSection ? 'Duplicate section' : 'Duplicate selected';
      duplicateButton.disabled = !!selected.dataset.cmsSignalOnly || canonicalForm || immutableShell;
      duplicateButton.title = immutableShell ? 'The website banner and footer are shared shell authorities and cannot be duplicated.'
        : canonicalForm ? 'Protected runtime forms cannot be duplicated.' : '';
    }
    const removeButton = document.getElementById('legend-cms-remove');
    if (removeButton) {
      const immutableShell = isSharedShellElement(selected);
      const protectedSemantic = !!selected.dataset.cmsSignalOnly || !!ov.systemKey || !!ov.systemBinding || protectedMappings || canonicalForm;
      const kind = serviceCard ? 'service'
        : selected.dataset.cmsSection ? 'section'
        : isCode ? 'code block'
        : selected.tagName === 'IMG' ? 'image'
        : selected.tagName === 'VIDEO' ? 'video'
        : ov.type === 'experience' ? 'interactive experience'
        : canonicalForm ? 'form'
        : ['INPUT','SELECT','TEXTAREA'].includes(selected.tagName) ? 'field'
        : ['A','BUTTON'].includes(selected.tagName) ? 'button'
        : ['DIV','ARTICLE','HEADER','FOOTER'].includes(selected.tagName) ? 'block'
        : 'element';
      removeButton.textContent = immutableShell
        ? 'Global shell · cannot delete'
        : protectedSemantic
          ? 'Protected wiring · cannot delete'
          : `Delete ${kind}`;
      removeButton.disabled = immutableShell || protectedSemantic;
      removeButton.title = immutableShell
        ? 'The website banner and footer are global shell authorities shared by every page and cannot be deleted.'
        : protectedSemantic
          ? 'This component owns canonical platform behavior. Rename, restyle, or reposition it without changing/removing its backend identity.'
          : '';
    }
    const sectionSelected = !!selected.dataset.cmsSection;
    if (scale) scale.value = String(editStyle?.fontScale ?? 1);
    if (width) width.value = sectionSelected ? '100' : displayNumber(editStyle?.widthPercent ?? (Number.isFinite(actualWidth) ? actualWidth : 100));
    if (height) height.value = editStyle?.heightPx != null ? displayNumber(editStyle.heightPx) : '';
    if (top) top.value = displayNumber(editStyle?.paddingTop ?? (parseFloat(computed.paddingTop) || 0));
    if (bottom) bottom.value = displayNumber(editStyle?.paddingBottom ?? (parseFloat(computed.paddingBottom) || 0));
    if (offsetX) offsetX.value = sectionSelected ? '0' : displayNumber(editStyle?.offsetXPercent ?? 0);
    if (offsetY) offsetY.value = displayNumber(editStyle?.offsetYPx ?? 0);
    if (align) align.value = editStyle?.textAlign ?? computed.textAlign ?? '';
    if (scale) scale.disabled = isImage || isCode;
    if (width) width.disabled = sectionSelected;
    if (offsetX) offsetX.disabled = sectionSelected;
    const values = { href: ov.href ?? rememberOriginal(selected).href ?? '', alt: ov.alt ?? selected.getAttribute('alt') ?? '' };
    Object.entries(values).forEach(([key,value]) => { const input = document.getElementById(`legend-cms-${key}`); if(input) input.value = value; });
    document.querySelectorAll('[data-style-key]').forEach(input => { const key = input.dataset.styleKey; input.value = ['color','backgroundColor'].includes(key) ? colorHex(editStyle?.[key] || computed[key]) : editStyle?.[key] ?? (input.type === 'number' ? parseFloat(computed[key]) || '' : computed[key] || ''); });
    const isCommerceControl = !!selected.dataset.legendStoreNav;
    const linkGroup = document.getElementById('legend-cms-link-group'); if (linkGroup) linkGroup.hidden = selected.tagName !== 'A' || isCommerceControl;
    if (selected.tagName === 'A' && !isCommerceControl) syncCtaControls(ov, values.href);
    const videoGroup = document.getElementById('legend-cms-video-group');
    if (videoGroup) videoGroup.hidden = selected.tagName !== 'VIDEO';
    const videoLoop = document.getElementById('legend-cms-video-loop');
    if (videoLoop) videoLoop.checked = selected.tagName === 'VIDEO' && ov.videoLoop === true;
    const layoutMode = document.getElementById('legend-cms-layout-mode'); if (layoutMode) layoutMode.value = editLayout?.mode || 'free';
    const layoutDirection = document.getElementById('legend-cms-layout-direction'); if (layoutDirection) layoutDirection.value = editLayout?.direction || 'column';
    const layoutGap = document.getElementById('legend-cms-layout-gap'); if (layoutGap) layoutGap.value = editLayout?.gapPx ?? '';
    const layoutColumns = document.getElementById('legend-cms-layout-columns'); if (layoutColumns) layoutColumns.value = editLayout?.columns ?? '';
    const layoutMin = document.getElementById('legend-cms-layout-min'); if (layoutMin) layoutMin.value = editLayout?.minItemWidthPx ?? '';
    const layoutAlign = document.getElementById('legend-cms-layout-align'); if (layoutAlign) layoutAlign.value = editLayout?.alignItems || '';
    const layoutJustify = document.getElementById('legend-cms-layout-justify'); if (layoutJustify) layoutJustify.value = editLayout?.justifyContent || '';
    const layoutWrap = document.getElementById('legend-cms-layout-wrap'); if (layoutWrap) layoutWrap.value = editLayout?.wrap || '';
    if (hidden) {
      hidden.checked = ov.hidden === true || selected.hidden;
      hidden.disabled = !!selected.dataset.cmsSignalOnly || !!ov.systemKey || !!ov.systemBinding || protectedMappings || canonicalForm;
    }
    if (targetInput) targetInput.disabled = !!ov.actionKey;
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
    if (selected.dataset.cmsSection && (field === 'widthPercent' || field === 'offsetXPercent')) {
      control.value = field === 'widthPercent' ? '100' : '0';
      control.setCustomValidity('');
      return;
    }
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
    const ov = selectedWebsiteModel();
    if (!ov) return;
    if (control.id === 'legend-cms-hidden') {
      ov.hidden = control.checked;
      selected.hidden = control.checked;
    } else {
      const style = editingStyle(ov, true);
      if (field) {
        if (control.value === '') delete style[field];
        else if (field === 'widthPercent') {
          style.widthPercent = Math.min(100, Number(control.value));
          control.value = String(style.widthPercent);
          if (Number.isFinite(Number(style.offsetXPercent)))
            style.offsetXPercent = Math.max(0, Math.min(95, Number(style.offsetXPercent)));
        } else if (field === 'offsetXPercent') {
          style.offsetXPercent = Math.max(0, Math.min(95, Number(control.value)));
          control.value = String(style.offsetXPercent);
        } else style[field] = Number(control.value);
      } else if (control.id === 'legend-cms-align') {
        if (control.value) style.textAlign = control.value;
        else delete style.textAlign;
      } else return;
      applyCompositionNode(selected, ov);
    }
    updateDirectCanvasUi();
    markDirty();
    syncSelectedSourcePresentationFromCanvas();
  }

  async function uploadMedia(file) {
    if (!file) return null;
    const status = document.getElementById('legend-cms-status');
    if (status) status.textContent = 'Uploading media…';
    try {
      const body = new FormData();
      body.append('ticket', editorTicket);
      body.append('file', file, file.name || 'website-media');
      // Never set Content-Type for FormData. The browser owns the multipart boundary.
      const response = await fetch(`${API_BASE}/api/website-content/manage/media`, {
        method: 'POST',
        body,
        credentials: 'omit',
        cache: 'no-store',
        headers: { Accept: 'application/json' }
      });
      const asset = await response.json();
      if (!response.ok || !asset?.id || !asset?.url || !asset?.contentType)
        throw new Error(asset?.message || asset?.error || 'Upload failed.');
      if (status) status.textContent = 'Media uploaded; save your draft to retain placement';
      return asset;
    } catch (error) { if (status) status.textContent = error.message; return null; }
  }

  async function uploadImageAsset(file) {
    if (!file) return null;
    const asset = await uploadMedia(file);
    if (!asset) return null;
    if (!String(asset.contentType || '').startsWith('image/')) {
      alert('Choose an image file.');
      return null;
    }
    return asset;
  }

  function moveSelectedSection(delta) {
    if (legacyMigration)
      throw new Error('Legacy website content is read-only until canonical materialization completes.');
    if (!selectedSection || selectedSection.matches('.site-header,.site-footer') || !selectedSection.dataset.cmsCompositionId) return;

    const roots=pageState().composition || [];
    const id=selectedSection.dataset.cmsCompositionId;
    const index=roots.findIndex(node=>node.id===id);
    const target=index+delta;
    if(index<0 || target<0 || target>=roots.length) return;

    checkpoint();
    const [node]=roots.splice(index,1);
    roots.splice(target,0,node);
    renderCanonicalCompositionPage();
    setSelected(findEditableElement(id));
    markDirty();
    refreshLayers();
  }

  async function addImage(file) {
    if(!selectedSection){
      alert('Select content inside the section where you want the new image.');
      return;
    }
    const asset=await uploadImageAsset(file);
    if(!asset) return;
    const parentId=selectedSection?.dataset?.cmsCompositionId;
    const parent=parentId ? compositionNode(parentId) : null;
    if(!parent){alert('Select a canonical section before adding an image.');return;}
    checkpoint();
    const id=freshStableId();
    const node={
      id,type:'image',tag:'img',className:null,
      mediaAssetId:asset.id,alt:'',
      signals:[],style:{widthPercent:70,paddingTop:16,paddingBottom:16},
      breakpointStyles:{},layout:{mode:'free',direction:'column'},breakpointLayouts:{},animations:[],children:[]
    };
    parent.children ||= [];
    parent.children.push(node);
    renderCanonicalCompositionPage();
    setSelected(findEditableElement(id));
    markDirty();
  }

  function removeSelected() {
    if(!selected || selected.dataset.cmsSignalOnly || !selected.dataset.cmsCompositionId) return;
    const entry=compositionEntry(selected.dataset.cmsCompositionId);
    const current=entry?.node;
    if(!current) return;
    if(current.systemKey || current.systemBinding || (Array.isArray(current.signals) && current.signals.length > 0) || current.type==='form'){
      const message='This component has protected platform wiring and cannot be deleted or replaced.';
      showCanonicalProtectionViolation(message);
      alert(message+' '+canonicalProtectedEditCorrection());
      return;
    }
    checkpoint();
    if(!removeCompositionNode(current.id)) return;
    if(entry.root?.scope?.startsWith('shell.')) renderCanonicalShell();
    else renderCanonicalCompositionPage();
    setSelected(null);
    markDirty();
  }

  function jsonEquivalent(left,right) {
    return JSON.stringify(left)===JSON.stringify(right);
  }

  function creativeNodeForMutation(node) {
    const copy=cloneCanonicalValue(node || {});
    delete copy.signals;
    delete copy.fieldSignals;
    delete copy.systemKey;
    delete copy.systemBinding;
    delete copy.unexpectedFields;
    copy.children=[];
    if(copy.experience && typeof copy.experience==='object') {
      copy.experience=cloneCanonicalValue(copy.experience);
      delete copy.experience.submitCapability;
    }
    return copy;
  }

  function creativeNodeComparable(node) {
    const copy=creativeNodeForMutation(node);
    delete copy.children;
    return copy;
  }

  function flattenCreativeNodes(nodes,scope,pagePath=null,reusableComponentId=null,parentId=null,map=new Map()) {
    (nodes || []).forEach((node,index)=>{
      if(!node?.id) return;
      map.set(node.id,{node,parentId,index,scope,pagePath,reusableComponentId});
      flattenCreativeNodes(node.children || [],scope,pagePath,reusableComponentId,node.id,map);
    });
    return map;
  }

  function mutationLocation(entry) {
    return {
      scope:entry.scope,
      pagePath:entry.pagePath,
      reusableComponentId:entry.reusableComponentId,
      parentId:entry.parentId,
      index:entry.index
    };
  }

  function pushInsertMutation(operations,entry) {
    const node=entry.node;
    const location=mutationLocation(entry);

    if(node.type==='form' && node.systemKey==='canonical_inquiry') {
      operations.push({
        type:'insertCapability',
        ...location,
        capabilityKey:'contact.inquiry.submit',
        instanceKey:node.id,
        node:creativeNodeForMutation(node),
        content:{title:node.title || 'Send an inquiry',submit:node.text || 'Send inquiry'}
      });
      return;
    }

    if(String(node.systemKey || '').startsWith('protect_runtime_form:')) {
      operations.push({
        type:'insertCapability',
        ...location,
        capabilityKey:'runtime.'+node.systemKey,
        instanceKey:node.id
      });
      return;
    }

    // New creative subtrees are emitted one semantic node at a time. This keeps
    // protected capability nodes out of ordinary insertNode payloads while still
    // preserving exact hierarchy/order in one mutation transaction.
    const projected=creativeNodeForMutation(node);
    projected.children=[];
    operations.push({type:'insertNode',...location,node:projected});

    if(node.type==='experience' && node.experience?.submitCapability==='lead_capture') {
      operations.push({
        type:'setApprovedCapability',
        nodeId:node.id,
        capabilityKey:'experience.lead_capture'
      });
    }

    (node.children || []).forEach((child,index)=>{
      pushInsertMutation(operations,{
        node:child,
        parentId:node.id,
        index,
        scope:entry.scope,
        pagePath:entry.pagePath,
        reusableComponentId:entry.reusableComponentId
      });
    });
  }

  function diffCreativeNodeTrees(beforeNodes,afterNodes,scope,pagePath,reusableComponentId,operations) {
    const before=flattenCreativeNodes(beforeNodes,scope,pagePath,reusableComponentId);
    const after=flattenCreativeNodes(afterNodes,scope,pagePath,reusableComponentId);

    // Remove only the highest removed ancestor; the server removes its subtree atomically.
    for(const [id,entry] of before) {
      if(after.has(id)) continue;
      if(entry.parentId && !after.has(entry.parentId)) continue;
      operations.push({type:'removeNode',nodeId:id});
    }

    // Insert only the highest new ancestor; its new descendants travel with it.
    for(const [id,entry] of after) {
      if(before.has(id)) continue;
      if(entry.parentId && !before.has(entry.parentId)) continue;
      pushInsertMutation(operations,entry);
    }

    for(const [id,next] of after) {
      const previous=before.get(id);
      if(!previous) continue;
      const moved=
        previous.scope!==next.scope ||
        previous.pagePath!==next.pagePath ||
        previous.reusableComponentId!==next.reusableComponentId ||
        previous.parentId!==next.parentId ||
        previous.index!==next.index;
      if(moved) operations.push({type:'moveNode',nodeId:id,...mutationLocation(next)});

      if(!jsonEquivalent(creativeNodeComparable(previous.node),creativeNodeComparable(next.node)))
        operations.push({type:'replaceNode',nodeId:id,node:creativeNodeForMutation(next.node)});
    }
  }

  function pageMetadataProjection(page) {
    return {
      title:page?.title ?? null,
      description:page?.description ?? null,
      navigation:cloneCanonicalValue(page?.navigation || {}),
      dynamicBinding:cloneCanonicalValue(page?.dynamicBinding || null)
    };
  }

  function pageMoveIdentity(page) {
    return JSON.stringify({
      systemTemplateKey:page?.systemTemplateKey || null,
      composition:page?.composition || []
    });
  }

  function storePresentationProjection(store) {
    return {
      navigationLabel:store?.navigationLabel || 'Store',
      cartIcon:store?.cartIcon || 'cart',
      cartIconSizePx:Number(store?.cartIconSizePx) || 28,
      storeNavigation:cloneCanonicalValue(store?.storeNavigation || {}),
      cartNavigation:cloneCanonicalValue(store?.cartNavigation || {})
    };
  }

  function buildCreativeMutationOperations(beforeInput,afterInput) {
    const before=normalizeDocument(beforeInput || {});
    const after=normalizeDocument(afterInput || {});
    const operations=[];

    if(!jsonEquivalent(before.theme,after.theme))
      operations.push({type:'setTheme',theme:cloneCanonicalValue(after.theme)});
    if(!jsonEquivalent(before.breakpoints,after.breakpoints))
      operations.push({type:'setBreakpoints',breakpoints:cloneCanonicalValue(after.breakpoints)});
    if(before.faviconImageDataUrl!==after.faviconImageDataUrl)
      operations.push({type:'setFavicon',faviconImageDataUrl:after.faviconImageDataUrl ?? null});
    if(!jsonEquivalent(storePresentationProjection(before.store),storePresentationProjection(after.store)))
      operations.push({type:'setStorePresentation',store:cloneCanonicalValue(after.store)});

    diffCreativeNodeTrees(before.shell?.header || [],after.shell?.header || [],'shell.header',null,null,operations);
    diffCreativeNodeTrees(before.shell?.footer || [],after.shell?.footer || [],'shell.footer',null,null,operations);

    const beforePaths=new Set(Object.keys(before.pages || {}));
    const afterPaths=new Set(Object.keys(after.pages || {}));
    const removed=[...beforePaths].filter(path=>!afterPaths.has(path));
    const added=[...afterPaths].filter(path=>!beforePaths.has(path));
    const movedSources=new Set(), movedTargets=new Set();
    for(const source of removed) {
      const identity=pageMoveIdentity(before.pages[source]);
      const target=added.find(path=>!movedTargets.has(path) && pageMoveIdentity(after.pages[path])===identity);
      if(!target) continue;
      operations.push({type:'movePageRoute',pagePath:source,targetPath:target});
      movedSources.add(source); movedTargets.add(target);
      if(!jsonEquivalent(pageMetadataProjection(before.pages[source]),pageMetadataProjection(after.pages[target])))
        operations.push({type:'updatePage',pagePath:target,page:pageMetadataProjection(after.pages[target])});
      diffCreativeNodeTrees(before.pages[source]?.composition || [],after.pages[target]?.composition || [],'page',target,null,operations);
    }

    for(const path of removed) if(!movedSources.has(path))
      operations.push({type:'removePage',pagePath:path});

    for(const path of added) {
      if(movedTargets.has(path)) continue;
      const page=after.pages[path];
      operations.push({
        type:'createPage',pagePath:path,
        page:{...pageMetadataProjection(page),composition:[]}
      });
      diffCreativeNodeTrees([],page?.composition || [],'page',path,null,operations);
    }

    for(const path of [...afterPaths].filter(path=>beforePaths.has(path))) {
      const previous=before.pages[path], next=after.pages[path];
      if(!jsonEquivalent(pageMetadataProjection(previous),pageMetadataProjection(next)))
        operations.push({type:'updatePage',pagePath:path,page:pageMetadataProjection(next)});
      diffCreativeNodeTrees(previous?.composition || [],next?.composition || [],'page',path,null,operations);
    }

    const beforeComponents=before.reusableComponents || {};
    const afterComponents=after.reusableComponents || {};
    for(const id of Object.keys(beforeComponents))
      if(!Object.prototype.hasOwnProperty.call(afterComponents,id))
        operations.push({type:'removeReusable',reusableComponentId:id});
    for(const [id,component] of Object.entries(afterComponents))
      if(!Object.prototype.hasOwnProperty.call(beforeComponents,id) || !jsonEquivalent(beforeComponents[id],component))
        operations.push({type:'upsertReusable',reusableComponent:cloneCanonicalValue(component)});

    return operations;
  }

  function applyCreativeMutationDeltaToState(baseInput,payload) {
    const changes=payload?.changes || {};
    const next=cloneCanonicalValue(baseInput || documentState);
    next.pages ||= {};
    next.reusableComponents ||= {};
    next.shell ||= {header:[],footer:[]};
    for(const path of Array.isArray(changes.removedPages)?changes.removedPages:[]) delete next.pages[path];
    for(const [path,page] of Object.entries(changes.pages || {})) next.pages[path]=page;
    for(const id of Array.isArray(changes.removedComponents)?changes.removedComponents:[]) delete next.reusableComponents[id];
    for(const [id,component] of Object.entries(changes.reusableComponents || {})) next.reusableComponents[id]=component;
    if(changes.theme) next.theme=changes.theme;
    if(changes.breakpoints) next.breakpoints=changes.breakpoints;
    if(changes.faviconChanged===true) next.faviconImageDataUrl=changes.faviconImageDataUrl ?? null;
    if(changes.shellHeader) next.shell.header=changes.shellHeader;
    if(changes.shellFooter) next.shell.footer=changes.shellFooter;
    if(changes.store) next.store=changes.store;
    return normalizeDocument(next);
  }

  let saving = false;
  async function save(publish = false, namedDraft = null) {
    if (saving) return false;
    clearTimeout(autoSaveTimer);
    const status=document.getElementById('legend-cms-status');

    if (publish && sourceEditorDirty) {
      if(status) status.textContent='Apply or discard your Selected Source changes before publishing. Nothing was published.';
      showPanel('source');
      return false;
    }
    if (legacyMigration && !materializationSavePending) {
      if(status) status.textContent='Legacy website content is read-only until canonical materialization completes.';
      return false;
    }
    if (publish && dirty) {
      const draftSaved=await save(false);
      if(!draftSaved || dirty) return false;
      return save(true);
    }

    saving=true;
    let saved=false;
    try {
      if(publish) {
        if(status) status.textContent='Publishing…';
        const response=await fetch(API_BASE+'/api/website-content/manage/publish',{
          method:'POST',
          headers:{'Content-Type':'application/json'},
          body:JSON.stringify({ticket:editorTicket,expectedRevision:revision})
        });
        const payload=await response.json().catch(()=>({}));
        if(!response.ok) throw Object.assign(new Error(payload.message || payload.error || ('Publish failed ('+response.status+').')),{status:response.status,payload});
        revision=payload.revision ?? revision;
        if(payload.document){
          documentState=normalizeDocument(payload.document);
          persistedDocumentState=cloneCanonicalValue(documentState);
          applyDocument(documentState);
        }
        dirty=false;
        saved=true;
        if(status) status.textContent='Published';
        return true;
      }

      const selectedForm=selected?.closest?.('form[data-cms-composition-id]');
      synchronizeCanonicalSharedPresentation(documentState,selectedForm?.dataset?.cmsCompositionId || selected?.dataset?.cmsCompositionId || null);
      const submittedState=normalizeDocument(cloneCanonicalValue(documentState));
      const submitted=JSON.stringify(submittedState);

      // The complete-document write exists only for the one-way legacy -> v3
      // materialization boundary. Normal Website Studio authoring uses mutations.
      if(materializationSavePending || !persistedDocumentState) {
        if(status) status.textContent='Saving canonical v3 materialization…';
        const response=await fetch(API_BASE+'/api/website-content/manage',{
          method:'POST',
          headers:{'Content-Type':'application/json'},
          body:JSON.stringify({ticket:editorTicket,document:submittedState,expectedRevision:revision,...(namedDraft || {})})
        });
        const payload=await response.json().catch(()=>({}));
        if(!response.ok) throw Object.assign(new Error(payload.message || payload.error || ('Save failed ('+response.status+').')),{status:response.status,payload});
        const serverState=normalizeDocument(payload.document || submittedState);
        const changedDuringSave=JSON.stringify(documentState)!==submitted;
        persistedDocumentState=cloneCanonicalValue(serverState);
        revision=payload.revision ?? revision;
        namedDrafts=payload.drafts || namedDrafts;
        materializationSavePending=false;
        if(!changedDuringSave){ documentState=serverState; applyDocument(documentState); }
        dirty=changedDuringSave;
        saved=true;
        if(status) status.textContent=changedDuringSave?'Draft saved; newer edits remain unsaved':'Draft saved';
        return true;
      }

      const operations=buildCreativeMutationOperations(persistedDocumentState,submittedState);
      if(status) status.textContent=operations.length ? 'Saving changed website scopes…' : namedDraft ? 'Saving named draft…' : 'Saved';
      if(operations.length===0 && !namedDraft){
        dirty=false; saved=true; return true;
      }

      const payload=await creativeWorkspaceRequest('manage/mutations',{
        method:'POST',
        body:{
          ticket:editorTicket,
          expectedRevision:revision,
          operations,
          draftId:namedDraft?.draftId || null,
          draftName:namedDraft?.draftName || null
        }
      });
      const serverState=applyCreativeMutationDeltaToState(submittedState,payload);
      const changedDuringSave=JSON.stringify(documentState)!==submitted;
      persistedDocumentState=cloneCanonicalValue(serverState);
      revision=payload.revision ?? revision;
      namedDrafts=payload.drafts || namedDrafts;
      canonicalSourceDocument=null;
      canonicalSourceRevision=null;
      if(!changedDuringSave){
        documentState=serverState;
        applyCreativeMutationVisuals(operations,payload,selected?.dataset?.cmsCompositionId || selected?.dataset?.cmsId || null);
      }
      dirty=changedDuringSave;
      saved=true;
      if(status) status.textContent=changedDuringSave?'Changed scopes saved; newer edits remain unsaved':'Draft saved';
    } catch(error) {
      if((error?.status===401 || error?.status===403) &&
          showEditorAuthorizationRecovery('Website Studio authorization expired before this change could be saved.')) return false;
      const payload=error?.payload || {};
      if(payload.canonicalProtectionViolation===true)
        showCanonicalProtectionViolation(payload.message || error.message,payload.correction);
      if(status) status.textContent=payload.message || error?.message || 'Save failed';
    } finally {
      saving=false;
      if(saved && dirty){
        clearTimeout(autoSaveTimer);
        autoSaveTimer=setTimeout(()=>{if(dirty&&!saving) void save(false);},900);
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
  function checkpoint() {
    if(suppressHistoryCapture) return;
    checkpointBaseline=cloneCanonicalValue(documentState);
  }
  // History stores reversible canonical mutation batches, never duplicate site
  // documents. If an edit is still local, flush it once so the inverse applies
  // against the same persisted v3 authority.
  async function restoreCanonicalV3History(from, to, direction='undo') {
    if (!from.length || saving) return;
    if(dirty){
      const saved=await save(false);
      if(!saved || dirty) return;
    }
    const entry=from.pop();
    const operations=direction==='redo' ? entry.redo : entry.undo;
    if(!Array.isArray(operations) || !operations.length){ refreshHistoryControls(); return; }
    suppressHistoryCapture=true;
    try{
      await creativeApplyMutationBatch(operations);
      to.push(entry);
      setSelected(null);
    }catch(error){
      from.push(entry);
      const status=document.getElementById('legend-cms-status');
      if(status) status.textContent=error?.payload?.message || error?.message || 'History operation could not be applied.';
    }finally{
      suppressHistoryCapture=false;
      checkpointBaseline=null;
      refreshHistoryControls();
    }
  }
  function safeUrl(value, media = false) {
    if (typeof value !== 'string' || !value.trim() || /[\u0000-\u0020\\]/.test(value) || /(?:legendEdit|ticket)=/i.test(value)) return false;
    if (!media && (value.startsWith('#') || (value.startsWith('/') && !value.startsWith('//')))) return true;
    try { const url = new URL(value); return media ? url.protocol === 'https:' : ['https:','mailto:','tel:'].includes(url.protocol); } catch { return false; }
  }
  function applyLegacyMigrationPlacement(el, placement) {
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
  async function ensureBusinessDataCatalog() {
    if(SITE_KEY!=='business' || fullDataCatalogLoaded) return;
    const payload=await creativeWorkspaceRequest('manage/data-catalog');
    managementPayload ||= {};
    managementPayload.dataCatalog=Array.isArray(payload?.dataCatalog)?payload.dataCatalog:[];
    updateCollectionData(payload);
    fullDataCatalogLoaded=true;
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
    const model=selectedWebsiteModel?.() || null;
    const binding=model?.dataBinding || null;
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
  function sourceFindNode(nodes,id) {
    for(const node of nodes || []){
      if(node?.id===id) return node;
      const child=sourceFindNode(node?.children,id); if(child) return child;
    }
    return null;
  }

  function sourceReplaceNode(nodes,id,replacement) {
    for(let index=0;index<(nodes || []).length;index++){
      const node=nodes[index];
      if(node?.id===id){ nodes[index]=replacement; return true; }
      if(sourceReplaceNode(node?.children,id,replacement)) return true;
    }
    return false;
  }

  function sourceSelectedNodeId() {
    return selected?.dataset?.cmsCompositionId || selected?.closest?.('form[data-cms-composition-id]')?.dataset?.cmsCompositionId || null;
  }

  function sourceFindNodeInDocument(sourceDocument,id) {
    if(!sourceDocument || !id) return null;
    return sourceFindNode(sourceDocument.shell?.header,id) ||
      sourceFindNode(sourceDocument.shell?.footer,id) ||
      (sourceDocument.pages || []).map(page=>sourceFindNode(page?.composition,id)).find(Boolean) ||
      Object.values(sourceDocument.reusableComponents || {}).map(component=>sourceFindNode(component?.composition,id)).find(Boolean) ||
      null;
  }

  function syncSelectedSourcePresentationFromCanvas() {
    const textarea=document.getElementById('legend-cms-site-source');
    const scope=document.getElementById('legend-cms-source-scope');
    const selectedNodeId=sourceSelectedNodeId();
    if(!textarea || textarea.readOnly || sourceEditorDirty || scope?.value!=='selection' || !selectedNodeId) return;
    const current=editableCompositionNodeForElement(selected,false);
    if(!current) return;
    let projected;
    try{ projected=JSON.parse(textarea.value); }
    catch{ return; }
    if(projected?.id!==selectedNodeId) return;

    // Keep Selected Source visually synchronized with Canvas presentation while
    // retaining the server-projected protected-authority omissions. This is a
    // view synchronization only; persistence still flows through canonical save.
    for(const key of ['text','className','style','breakpointStyles','layout','breakpointLayouts','animations','hidden','alt','mediaAssetId','mediaUrl'])
    {
      if(Object.hasOwn(current,key)) projected[key]=cloneCanonicalValue(current[key]);
      else delete projected[key];
    }
    textarea.value=JSON.stringify(projected,null,2);
    sourceEditorBaseNode=cloneCanonicalValue(projected);
    renderSourceHighlight();
  }

  async function loadCanonicalSourceSnapshot() {
    const url=new URL(`${API_BASE}/api/website-content/manage/source`);
    url.searchParams.set('ticket',editorTicket);
    const response=await fetch(url,{cache:'no-store'});
    const payload=await response.json().catch(()=>({}));
    if(!response.ok){
      if((response.status===401 || response.status===403) &&
          showEditorAuthorizationRecovery('Website Studio authorization expired while loading canonical Source.'))
        return null;
      throw new Error(payload.message || payload.error || `Unable to load canonical Source (${response.status}).`);
    }
    if(payload.requiresMaterialization===true)
      throw new Error('Materialize this website into canonical v3 before editing Source.');
    if(typeof payload.text!=='string' || !payload.text.trim())
      throw new Error('Canonical Source projection is unavailable.');
    const sourceDocument=JSON.parse(payload.text);
    canonicalSourceDocument=sourceDocument;
    canonicalSourceRevision=payload.revision;
    return {revision:payload.revision,sourceDocument,text:payload.text,sourceMap:payload.sourceMap || {}};
  }


  function sourceToneForLine(line) {
    const key=/^\s*"([^"]+)"\s*:/.exec(String(line || ''))?.[1] || '';
    if (['text','title','alt','href','description','label'].includes(key)) return 'content';
    if (['color','backgroundColor'].includes(key)) return 'color';
    if (['widthPercent','heightPx','fontSize','fontScale','lineHeight','letterSpacing','paddingTop','paddingBottom','paddingLeft','paddingRight','offsetXPercent','offsetYPx','borderRadius','objectPosition','marginTop','marginBottom','marginLeft','marginRight','borderWidth','minWidthPx','maxWidthPx','minHeightPx','maxHeightPx','aspectRatio','opacity'].includes(key)) return 'size';
    if (['style','breakpointStyles','fontFamily','fontWeight','textAlign','objectFit','borderColor','borderStyle','textTransform','textDecoration','backgroundGradient','boxShadow','fieldPresentations','fieldLabels'].includes(key)) return 'style';
    if (['layout','breakpointLayouts','mode','direction','gapPx','columns','minItemWidthPx','alignItems','justifyContent','wrap'].includes(key)) return 'layout';
    if (['mediaAssetId','mediaUrl','faviconImageDataUrl'].includes(key)) return 'media';
    if (['animations','trigger','effect','durationMs','delayMs','distancePx','easing','once','hidden','target','actionKey'].includes(key)) return 'behavior';
    if (['id','systemKey','systemBinding','signals'].includes(key)) return 'protected';
    if (['type','tag','className','dataBinding','syncSourceId'].includes(key)) return 'structure';
    return 'default';
  }

  function renderSourceHighlight() {
    const textarea=document.getElementById('legend-cms-site-source');
    const highlight=document.getElementById('legend-cms-source-highlight');
    if(!textarea || !highlight?.replaceChildren) return;
    highlight.replaceChildren();
    const lines=String(textarea.value || '').replace(/\r/g,'').split('\n');
    lines.forEach((line,index)=>{
      const span=document.createElement('span');
      const tone=sourceToneForLine(line);
      span.className='legend-cms-source-'+tone;
      span.dataset.tone=tone;
      span.textContent=line || ' ';
      highlight.appendChild(span);
      if(index<lines.length-1) highlight.appendChild(document.createTextNode('\n'));
    });
    highlight.scrollTop=textarea.scrollTop;
    highlight.scrollLeft=textarea.scrollLeft;
  }

  function sourceEditorDispatchInput(textarea) {
    textarea.dispatchEvent(new Event('input',{bubbles:true}));
  }

  function sourceEditorIndentSelection(textarea, outdent=false) {
    const value=textarea.value;
    const start=textarea.selectionStart ?? 0;
    const end=textarea.selectionEnd ?? start;
    const lineStart=value.lastIndexOf('\n',Math.max(0,start-1))+1;
    const nextBreak=value.indexOf('\n',end);
    const lineEnd=nextBreak<0 ? value.length : nextBreak;
    const block=value.slice(lineStart,lineEnd);
    const lines=block.split('\n');
    const transformed=lines.map(line=>{
      if(!outdent) return '  '+line;
      if(line.startsWith('  ')) return line.slice(2);
      if(line.startsWith('\t') || line.startsWith(' ')) return line.slice(1);
      return line;
    }).join('\n');
    textarea.setRangeText(transformed,lineStart,lineEnd,'select');
    const removedStart=outdent ? Math.min(2,(block.match(/^\s{1,2}/)?.[0] || '').length) : 0;
    const delta=transformed.length-block.length;
    textarea.setSelectionRange(
      Math.max(lineStart,start+(outdent?-removedStart:2)),
      Math.max(lineStart,end+delta));
    sourceEditorDispatchInput(textarea);
  }

  function handleSourceEditorKeydown(event) {
    const textarea=event.currentTarget;
    if(!(textarea instanceof HTMLTextAreaElement) || textarea.readOnly) return;

    if(event.key==='Tab'){
      event.preventDefault();
      const start=textarea.selectionStart ?? 0;
      const end=textarea.selectionEnd ?? start;
      const selected=textarea.value.slice(start,end);
      if(start!==end && selected.includes('\n')){
        sourceEditorIndentSelection(textarea,event.shiftKey);
        return;
      }

      if(event.shiftKey){
        const lineStart=textarea.value.lastIndexOf('\n',Math.max(0,start-1))+1;
        const before=textarea.value.slice(lineStart,start);
        const removable=/^(?:  |\t| )/.exec(before)?.[0] || '';
        if(removable){
          textarea.setRangeText('',lineStart,lineStart+removable.length,'preserve');
          textarea.setSelectionRange(
            Math.max(lineStart,start-removable.length),
            Math.max(lineStart,end-removable.length));
          sourceEditorDispatchInput(textarea);
        }
        return;
      }

      textarea.setRangeText('  ',start,end,'end');
      sourceEditorDispatchInput(textarea);
      return;
    }

    if(event.key==='Enter'){
      event.preventDefault();
      const start=textarea.selectionStart ?? 0;
      const end=textarea.selectionEnd ?? start;
      const value=textarea.value;
      const lineStart=value.lastIndexOf('\n',Math.max(0,start-1))+1;
      const before=value.slice(lineStart,start);
      const indent=/^\s*/.exec(before)?.[0] || '';
      const extra=/[\{\[]\s*$/.test(before) ? '  ' : '';
      textarea.setRangeText('\n'+indent+extra,start,end,'end');
      sourceEditorDispatchInput(textarea);
    }
  }

  function syncSourceEditingMode() {
    const textarea=document.getElementById('legend-cms-site-source');
    const scope=document.getElementById('legend-cms-source-scope');
    const apply=document.getElementById('legend-cms-source-apply');
    const reload=document.getElementById('legend-cms-source-reload');
    if(!textarea || !scope) return false;
    const editable=scope.value==='selection' && !!sourceSelectedNodeId();
    textarea.readOnly=!editable;
    textarea.setAttribute('aria-readonly',String(!editable));
    textarea.dataset.sourceMode=editable?'selection':'master';
    if(apply){ apply.disabled=!editable; apply.hidden=!editable; }
    if(reload) reload.textContent=editable?'Discard selected source edits':'Refresh Master Source';
    return editable;
  }

  async function refreshSiteSourceEditor(force=false) {
    const textarea=document.getElementById('legend-cms-site-source');
    const scope=document.getElementById('legend-cms-source-scope');
    const label=document.getElementById('legend-cms-source-location');
    const status=document.getElementById('legend-cms-source-status');
    if(!textarea || (sourceEditorDirty && !force)) return;

    if(dirty){
      if(status) status.textContent='Saving canvas changes before refreshing canonical Source…';
      const saved=await save(false);
      if(!saved || dirty){
        if(status) status.textContent='Save or reconcile canvas changes before opening Source.';
        return;
      }
    }

    const selectedId=sourceSelectedNodeId();
    if(scope && scope.value==='selection' && !selectedId) scope.value='site';
    const mode=scope?.value || 'site';

    if(mode==='selection' && selectedId){
      try{
        const snapshot=await creativeWorkspaceRequest('manage/agent/node',{query:{id:selectedId}});
        sourceEditorBaseNode=cloneCanonicalValue(snapshot.node);
        sourceEditorBaseFingerprint=snapshot.fingerprint || null;
        canonicalSourceRevision=snapshot.revision ?? revision;
        textarea.value=JSON.stringify(sourceEditorBaseNode,null,2);
        if(label) label.textContent='Selected source · '+(snapshot.location?.pagePath || currentPageRoute())+' · #'+selectedId;
      }catch(error){
        clearCanonicalProtectionViolation();
        if(status) status.textContent=error?.message || 'Selected Source could not be loaded.';
        return;
      }
    }else{
      let snapshot;
      try{ snapshot=await loadCanonicalSourceSnapshot(); }
      catch(error){
        clearCanonicalProtectionViolation();
        if(status) status.textContent=error?.message || 'Canonical Source could not be loaded.';
        return;
      }
      if(!snapshot) return;
      sourceEditorBaseNode=null;
      sourceEditorBaseFingerprint=null;
      textarea.value=snapshot.text;
      if(label) label.textContent='Master Source · entire website';
      if(selectedId){
        const marker='"id": "'+selectedId+'"';
        const index=textarea.value.indexOf(marker);
        if(index>=0) textarea.setSelectionRange(index,index+marker.length);
      }
    }

    sourceEditorDirty=false;
    const editable=syncSourceEditingMode();
    renderSourceHighlight();
    if(status) status.textContent=editable
      ? 'Selected Source is scoped to one canonical node. Protected backend authority is not part of this write surface.'
      : 'Master Source is synchronized from the server and read only.';
    if(force) clearCanonicalProtectionViolation();
  }

  async function applySiteSource() {
    const textarea=document.getElementById('legend-cms-site-source');
    const scope=document.getElementById('legend-cms-source-scope');
    const status=document.getElementById('legend-cms-source-status');
    const selectedNodeId=sourceSelectedNodeId();
    if(!textarea) return;
    if(scope?.value!=='selection' || !selectedNodeId || textarea.readOnly){
      if(status) status.textContent='Master Source is read only. Select a canvas component to edit Selected Source.';
      syncSourceEditingMode();
      renderSourceHighlight();
      return;
    }

    let replacementNode;
    try{ replacementNode=JSON.parse(textarea.value); }
    catch(error){
      clearCanonicalProtectionViolation();
      if(status) status.textContent=error?.message || 'Selected Source syntax is invalid.';
      return;
    }
    if(replacementNode?.id!==selectedNodeId){
      const message='Selected Source must keep the stable node ID '+selectedNodeId+'.';
      if(status) status.textContent=message;
      showCanonicalProtectionViolation(message);
      return;
    }
    if(!sourceEditorBaseFingerprint){
      if(status) status.textContent='Refresh Selected Source before applying this edit.';
      return;
    }

    if(dirty){
      if(status) status.textContent='Saving existing canvas edits before applying Selected Source…';
      const saved=await save(false);
      if(!saved || dirty) return;
    }

    clearCanonicalProtectionViolation();
    if(status) status.textContent='Applying this node through the canonical mutation authority…';
    try{
      await creativeApplyMutationBatch([{
        type:'replaceNode',
        nodeId:selectedNodeId,
        node:replacementNode,
        expectedFingerprint:sourceEditorBaseFingerprint
      }]);
      sourceEditorDirty=false;
      await refreshSiteSourceEditor(true);
      if(status) status.textContent='Applied one scoped node mutation. Protected events/forms/backend wiring were not writable.';
    }catch(error){
      const payload=error?.payload || {};
      if(payload.canonicalProtectionViolation===true)
        showCanonicalProtectionViolation(payload.message || error.message,payload.correction);
      else
        clearCanonicalProtectionViolation();
      if(error?.status===409 && payload.error==='scope_revision_conflict')
        if(status) status.textContent='This selected component changed elsewhere. Your Source text is preserved; refresh and reconcile the newer node.';
      else if(status) status.textContent=payload.message || error?.message || 'Selected Source could not be applied.';
    }
  }

  function showPanel(name) {
    activeEditorPanel = name;
    document.querySelectorAll('[data-cms-view]').forEach(view => { view.hidden = view.dataset.cmsView !== name; });
    document.querySelectorAll('[data-open]').forEach(button => button.setAttribute('aria-pressed', String(button.dataset.open === name)));
    if (name === 'layers') refreshLayers();
    if (name === 'media') void refreshMediaLibrary();
    if (name === 'source') {
      const scope=document.getElementById('legend-cms-source-scope');
      if(scope && sourceSelectedNodeId()) scope.value='selection';
      void refreshSiteSourceEditor();
    }
    if (name === 'components') renderReusableComponents();
    if (name === 'data') void ensureBusinessDataCatalog().then(renderDataControls).catch(error=>{
      const status=document.getElementById('legend-cms-data-status');
      if(status) status.textContent=error?.message || 'Unable to load business data.';
    });
    if (name === 'gpt') refreshBrowserAgentWorkspace();
    if (name === 'motion') renderMotionControls();
    if (name === 'page') syncPageControls();
    if (name === 'signals') void ensureSignalCatalog().then(renderSignalControls);
    if (name === 'quality') void refreshQualityInspector();
    if (name === 'collaboration') void refreshCollaboration();
  }


  function collaborationSelectedElementId() {
    if (!selected) return null;
    return selected.dataset.cmsCompositionId || selected.dataset.cmsId || null;
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
      const node=selectedWebsiteModel();
      if(!node) return;
      checkpoint();
      node.mediaAssetId=asset.id;
      delete node.mediaUrl;
      if(isImage) node.alt ||= asset.name || '';
      applyCompositionNode(selected,node);
      syncEditorControls();
      markDirty();
      return;
    }

    const section=selectedSection || document.querySelector('main [data-cms-section]');
    const parentId=section?.dataset?.cmsCompositionId;
    const parent=parentId ? compositionNode(parentId) : null;
    if(!parent){alert('Select a canonical section before inserting media.');return;}

    checkpoint();
    const id=freshStableId();
    const node={
      id,type:isImage?'image':'video',tag:isImage?'img':'video',
      className:null,
      mediaAssetId:asset.id,
      alt:isImage?(asset.name||''):null,
      videoLoop:isVideo?false:null,
      signals:[],style:{widthPercent:isImage?70:100},breakpointStyles:{},
      layout:{mode:'free',direction:'column'},breakpointLayouts:{},animations:[],children:[]
    };
    parent.children ||= [];
    parent.children.push(node);
    renderCanonicalCompositionPage();
    setSelected(findEditableElement(id));
    markDirty();
  }

  function parseComputedRgb(value) {
    const match=String(value || '').match(/rgba?\(\s*([\d.]+)[, ]+\s*([\d.]+)[, ]+\s*([\d.]+)(?:\s*[,/]\s*([\d.]+))?/i);
    if(!match) return null;
    return {r:Number(match[1]),g:Number(match[2]),b:Number(match[3]),a:match[4]==null?1:Number(match[4])};
  }

  function relativeLuminance(rgb) {
    const channel=value=>{
      const normalized=Math.max(0,Math.min(255,value))/255;
      return normalized<=.03928 ? normalized/12.92 : Math.pow((normalized+.055)/1.055,2.4);
    };
    return .2126*channel(rgb.r)+.7152*channel(rgb.g)+.0722*channel(rgb.b);
  }

  function effectiveBackgroundColor(el) {
    let current=el;
    while(current && current!==document.documentElement){
      const parsed=parseComputedRgb(getComputedStyle(current).backgroundColor);
      if(parsed && parsed.a>.08) return parsed;
      current=current.parentElement;
    }
    return parseComputedRgb(getComputedStyle(document.body).backgroundColor) || {r:255,g:255,b:255,a:1};
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
      if (!image.hasAttribute('alt'))
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
      const overflowX=getComputedStyle(node).overflowX;
      const visiblyClipped=overflowX==='hidden' || overflowX==='clip';
      if (!visiblyClipped && Number(node.scrollWidth) > Number(node.clientWidth) + 1)
        checks.push({ code:'live_horizontal_overflow', severity:'warning', message:'Rendered content overflows its visible width.', elementId:node.dataset.cmsId || null });
    });

    const headingLevels=[];
    main?.querySelectorAll('h1,h2,h3,h4,h5,h6').forEach(node=>{
      if(node.hidden || node.getClientRects().length===0) return;
      const level=Number(node.tagName.slice(1));
      const previous=headingLevels.at(-1);
      if(previous && level>previous+1)
        checks.push({code:'live_heading_skip',severity:'info',message:`Heading hierarchy jumps from H${previous} to H${level}.`,elementId:node.dataset.cmsId || null});
      headingLevels.push(level);
    });

    const textNodes=main ? [...main.querySelectorAll('h1,h2,h3,h4,h5,h6,p,li,a,button,label,small,blockquote,span')] : [];
    let contrastWarnings=0, typeWarnings=0, lineWarnings=0;
    for(const node of textNodes){
      if(node.closest('.legend-cms-editor') || node.hidden || node.getClientRects().length===0 || !node.textContent?.trim()) continue;
      const style=getComputedStyle(node);
      const fontSize=parseFloat(style.fontSize);
      if(Number.isFinite(fontSize) && fontSize<14 && typeWarnings++<8)
        checks.push({code:'live_type_too_small',severity:'warning',message:`Rendered text is ${Math.round(fontSize)}px; increase it for comfortable reading.`,elementId:node.dataset.cmsId || null});
      const width=node.getBoundingClientRect().width;
      if(Number.isFinite(fontSize) && fontSize>0 && width/fontSize>46 && node.textContent.trim().length>120 && lineWarnings++<6)
        checks.push({code:'live_line_length',severity:'info',message:'This text block is visually wide. Shorter line length may improve scanning.',elementId:node.dataset.cmsId || null});
      if(contrastWarnings<8){
        const fg=parseComputedRgb(style.color);
        const bg=effectiveBackgroundColor(node);
        if(fg && bg && fg.a>.8){
          const high=Math.max(relativeLuminance(fg),relativeLuminance(bg));
          const low=Math.min(relativeLuminance(fg),relativeLuminance(bg));
          const ratio=(high+.05)/(low+.05);
          const bold=Number(style.fontWeight)>=700;
          const threshold=fontSize>=24 || (bold && fontSize>=18.66) ? 3 : 4.5;
          if(ratio<threshold){
            contrastWarnings++;
            checks.push({code:'live_contrast_low',severity:'warning',message:`Text contrast is about ${ratio.toFixed(1)}:1; strengthen foreground/background contrast.`,elementId:node.dataset.cmsId || null});
          }
        }
      }
    }

    let touchWarnings=0;
    main?.querySelectorAll('a,button,input[type="button"],input[type="submit"]').forEach(node=>{
      if(node.hidden || node.getClientRects().length===0 || touchWarnings>=8) return;
      const rect=node.getBoundingClientRect();
      if(rect.width<44 || rect.height<44){
        touchWarnings++;
        checks.push({code:'live_touch_target_small',severity:'warning',message:`Interactive target is ${Math.round(rect.width)}×${Math.round(rect.height)}px; target at least 44×44px.`,elementId:node.dataset.cmsId || node.id || null});
      }
    });

    const sectionNodes=main ? [...main.children].filter(node=>node.matches?.('section,.section,.page-hero,.cta,.container-narrow')) : [];
    sectionNodes.forEach(section=>{
      if(section.hidden) return;
      const meaningful=section.querySelector('h1,h2,h3,p,img,video,a,button,form,[data-website-experience-form]');
      if(!meaningful && !section.textContent?.trim())
        checks.push({code:'live_empty_section',severity:'warning',message:'A visible section has no meaningful content.',elementId:section.dataset.cmsId || section.dataset.cmsSection || null});
      const computed=getComputedStyle(section);
      const top=parseFloat(computed.paddingTop), bottom=parseFloat(computed.paddingBottom);
      if(Number.isFinite(top) && Number.isFinite(bottom) && top<20 && bottom<20)
        checks.push({code:'live_section_rhythm_tight',severity:'info',message:'This section has very little vertical breathing room.',elementId:section.dataset.cmsId || section.dataset.cmsSection || null});
    });

    const firstSection=sectionNodes.find(node=>!node.hidden);
    if(firstSection && !firstSection.querySelector('a[data-website-action-key],button[data-website-action-key],form,[data-submit-capability]'))
      checks.push({code:'live_first_decision_no_action',severity:'info',message:'The opening section has no approved action or conversion capability. Confirm that this is intentional.',elementId:firstSection.dataset.cmsId || firstSection.dataset.cmsSection || null});

    main?.querySelectorAll('img:not([hidden])').forEach(image=>{
      if(image.complete && image.currentSrc && (!image.naturalWidth || !image.naturalHeight))
        checks.push({code:'live_image_render_failed',severity:'warning',message:'An image source did not render successfully.',elementId:image.dataset.cmsId || image.id || null});
    });

    if(responsiveViewportWidth()<=767){
      const toggle=document.querySelector('.nav-toggle');
      if(!toggle || toggle.getClientRects().length===0)
        checks.push({code:'live_mobile_nav_toggle_missing',severity:'error',message:'The mobile primary navigation has no usable Menu trigger.'});
      else{
        const rect=toggle.getBoundingClientRect();
        if(rect.width<44 || rect.height<44)
          checks.push({code:'live_mobile_nav_touch_target',severity:'warning',message:'The mobile Menu trigger is smaller than 44×44px.'});
      }
    }

    let animationCount=0;
    walkComposition(pageState().composition || [],node=>{animationCount+=(node.animations || []).length;});
    if(animationCount>18)
      checks.push({code:'live_animation_excess',severity:'warning',message:`This page has ${animationCount} motion bindings. Reduce motion to keep the experience focused.`});

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
    savedHost.textContent='Checking structural, design, and conversion quality…';
    if (savedMeta) savedMeta.textContent='Saved canonical quality (server) · loading';
    try {
      const payload=await creativeWorkspaceRequest('manage/agent/design-quality');
      const structural=Array.isArray(payload?.structural?.checks)?payload.structural.checks:[];
      const design=Array.isArray(payload?.design?.checks)?payload.design.checks:[];
      const combined=[...structural,...design];
      renderQualityChecks(savedHost,combined,'No structural, design, or conversion issues detected by the server checks.');
      const errors=combined.filter(check=>check.severity==='error').length;
      const warnings=combined.filter(check=>check.severity==='warning').length;
      const paths=Array.isArray(payload?.design?.conversionPaths)?payload.design.conversionPaths:[];
      if(savedMeta) savedMeta.textContent=`Saved canonical quality · revision ${payload.revision} · ${errors} errors · ${warnings} warnings · ${paths.length} conversion path${paths.length===1?'':'s'} reviewed`;
    } catch(error) {
      renderQualityChecks(savedHost,[{severity:'error',message:error?.message || 'Unable to inspect the saved draft.'}],'');
      if(savedMeta) savedMeta.textContent='Saved canonical quality (server) · unavailable';
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
        restore.addEventListener('click',()=>{setSelected(section);checkpoint();const value=selectedWebsiteModel();if(!value)return;value.hidden=false;section.hidden=false;markDirty();syncEditorControls();showPanel('layers');});
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
    const choices = {
      fontWeight:['100','200','300','400','500','600','700','800','900'],
      objectFit:['cover','contain','fill','none','scale-down'],
      borderStyle:['none','solid','dashed','dotted','double'],
      textTransform:['none','uppercase','lowercase','capitalize'],
      textDecoration:['none','underline','line-through','overline']
    };
    const keys=['backgroundColor','borderRadius','objectFit','marginLeft','marginRight',
      'borderWidth','borderColor','borderStyle','opacity','minWidthPx','maxWidthPx',
      'minHeightPx','maxHeightPx','aspectRatio','backgroundGradient','boxShadow','objectPosition'];
    return keys.map(key => {
      const label = key.replace(/([A-Z])/g, ' $1');
      const isColor=['color','backgroundColor','borderColor'].includes(key);
      let control;
      if(choices[key]) control=`<select data-style-key="${key}"><option value="">Default</option>${choices[key].map(value => `<option value="${value}">${value}</option>`).join('')}</select>`;
      else if(['fontFamily','backgroundGradient','boxShadow','objectPosition'].includes(key))
        control=`<input data-style-key="${key}" type="text" maxlength="500" placeholder="${key==='fontFamily'?'system-ui, Arial, sans-serif':''}">`;
      else control=`<input data-style-key="${key}" type="${isColor?'text':'number'}" step="any">`;
      return `<label class="legend-cms-group">${label}${control}${isColor ? `<button type="button" data-color-reset="${key}">Use inherited color</button>` : ''}</label>`;
    }).join('');
  }

  function freshStableId() {
    return (crypto.randomUUID ? crypto.randomUUID() : String(Date.now())+Math.random())
      .replaceAll('-','').replace('.','');
  }

  function cloneCanonicalNodeFresh(node,{offsetY=0}={}) {
    const copy=cloneCanonicalValue(node);
    const rewrite=current=>{
      current.id=freshStableId();
      // Duplicating presentation never duplicates hidden analytics/provider wiring.
      // New custom mappings are added only through the canonical Analytics controls.
      current.signals=[];
      current.fieldSignals={};
      (current.children || []).forEach(rewrite);
    };
    rewrite(copy);
    if(offsetY){
      copy.style ||= {};
      copy.style.offsetYPx=(Number(copy.style.offsetYPx)||0)+offsetY;
    }
    return copy;
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
      if(SITE_KEY!=='business') return;
      const route=normalizePageRoute(document.getElementById('legend-cms-page-slug')?.value);
      if(!route || route==='/' || websitePageEntries(true).some(entry=>entry.route===route)){
        alert('Enter a unique website route such as /team or /services/commercial.');
        return;
      }
      checkpoint();
      const label=(document.getElementById('legend-cms-page-nav-label')?.value ||
        route.split('/').filter(Boolean).at(-1) || 'Page').trim();
      const sectionId=freshStableId();
      const textId=freshStableId();
      documentState.pages[route]={
        title:label,
        description:'',
        navigation:{label,showInNavigation:true,parentPath:null,order:websitePageEntries(true).length*10,isDeleted:false},
        dynamicBinding:null,
        composition:[{
          id:sectionId,type:'section',tag:'section',className:'section',signals:[],
          style:{},breakpointStyles:{},layout:{mode:'stack',direction:'column'},breakpointLayouts:{},animations:[],
          children:[{
            id:textId,type:'heading',tag:'h1',text:label,signals:[],
            style:{},breakpointStyles:{},layout:{mode:'free',direction:'column'},breakpointLayouts:{},animations:[],children:[]
          }]
        }]
      };
      markDirty();
      await navigateToEditorPage(route);
    });

    document.getElementById('legend-cms-page-duplicate')?.addEventListener('click',async()=>{
      if(SITE_KEY!=='business') return;
      const sourceRoute=currentPageRoute();
      const target=normalizePageRoute(document.getElementById('legend-cms-page-slug')?.value);
      if(!target || target===sourceRoute || websitePageEntries(true).some(entry=>entry.route===target)){
        alert('Enter a unique route for the duplicate.');
        return;
      }
      checkpoint();
      const source=cloneCanonicalValue(pageState());
      source.composition=(source.composition || []).map(node=>cloneCanonicalNodeFresh(node));
      source.navigation={
        ...(source.navigation || {}),
        label:(source.navigation?.label || source.title || 'Copy')+' copy',
        isDeleted:false,
        order:websitePageEntries(true).length*10
      };
      documentState.pages[target]=source;
      markDirty();
      await navigateToEditorPage(target);
    });

    document.getElementById('legend-cms-page-rename')?.addEventListener('click',async()=>{
      if(SITE_KEY!=='business') return;
      const sourceRoute=currentPageRoute();
      const target=normalizePageRoute(document.getElementById('legend-cms-page-slug')?.value);
      if(sourceRoute==='/' || !target || target==='/' || target===sourceRoute ||
        websitePageEntries(true).some(entry=>entry.route===target)){
        alert('Enter a unique route. The home page route cannot be renamed.');
        return;
      }
      checkpoint();
      documentState.pages[target]=documentState.pages[sourceRoute];
      delete documentState.pages[sourceRoute];
      markDirty();
      await navigateToEditorPage(target);
    });

    document.getElementById('legend-cms-page-delete')?.addEventListener('click',async()=>{
      if(SITE_KEY!=='business') return;
      const route=currentPageRoute();
      if(route==='/') return;
      checkpoint();
      const page=ensurePageRecord(route);
      page.navigation.isDeleted=!page.navigation.isDeleted;
      if(page.navigation.isDeleted) page.navigation.showInNavigation=false;
      markDirty();
      if(page.navigation.isDeleted) await navigateToEditorPage('/');
      else syncPageControls();
    });

    document.getElementById('legend-cms-duplicate').addEventListener('click',()=>{
      if(!selected || selected.dataset.cmsSignalOnly || !selected.dataset.cmsCompositionId) return;
      const serviceCard=businessServiceCardFor(selected);
      const selectedId=serviceCard?.dataset?.cmsCompositionId || selected.dataset.cmsCompositionId;
      const entry=compositionEntry(selectedId);
      if(!entry?.node) return;
      if(entry.node.type==='form' || entry.node.systemKey){
        alert('Protected system components cannot be duplicated. Duplicate the surrounding content instead.');
        return;
      }
      checkpoint();
      const copy=cloneCanonicalNodeFresh(entry.node,{offsetY:16});
      const siblings=compositionChildren(entry.parent,entry.root);
      const index=siblings.findIndex(node=>node.id===entry.node.id);
      siblings.splice(index+1,0,copy);
      if(entry.root?.scope?.startsWith('shell.')) renderCanonicalShell();
      else renderCanonicalCompositionPage();
      setSelected(findEditableElement(copy.id));
      markDirty();
    });

    document.addEventListener('keydown', event => {
      if (event.key === 'Escape' && inlineEditNode) {
        event.preventDefault();
        deactivateInlineEditing(inlineEditNode);
        selected?.focus?.({ preventScroll: true });
        return;
      }
      if (!(event.ctrlKey || event.metaKey) || event.altKey) return;
      const key = event.key.toLowerCase();
      if (key === 's') { event.preventDefault(); chooseDraft(); return; }
      if (event.target.closest?.('input,textarea,select,[contenteditable="true"]')) return;
      if (key === 'z' || key === 'y') {
        event.preventDefault();
        if (key === 'y' || event.shiftKey) void restoreCanonicalV3History(redoStack, undoStack,'redo');
        else void restoreCanonicalV3History(undoStack, redoStack,'undo');
      }
    });
    refreshHistoryControls();
  }
  function availableCtaOptions() {
    const items = [];
    const seen = new Set();
    const push = option => {
      if (!option?.choiceKey || !option?.href || option.href === '#' || !safeUrl(option.href) || seen.has(option.choiceKey)) return;
      seen.add(option.choiceKey);
      items.push(option);
    };

    for (const option of ctaCatalog || []) {
      if (!option?.key || !option?.href || option.href === '#' || !safeUrl(option.href)) continue;
      if (['focus_form','submit_form'].includes(option.runtimeAction) && !document.querySelector('form[data-form-key]')) continue;
      if (option.runtimeAction === 'add_current_product' && !document.querySelector('#spAddToCart[data-product-id]')) continue;
      const phrases = [...new Set([
        option.defaultText,
        ...(Array.isArray(option.textVariants) ? option.textVariants : [])
      ].filter(value => typeof value === 'string' && value.trim()).map(value => value.trim()))];
      (phrases.length ? phrases : [option.label || option.key]).forEach((text, index) => {
        push({
          ...option,
          managed: true,
          kind: 'managed',
          actionKey: option.key,
          choiceKey: `managed:${option.key}:${index}`,
          label: text,
          defaultText: text
        });
      });
    }

    const pageEntries = new Map();
    for (const page of context.pages || []) {
      if (typeof page.path !== 'string') continue;
      const path = page.path.replace(/\/$/, '') || '/';
      pageEntries.set(path, page.label || page.path);
    }
    for (const [path, page] of Object.entries(documentState.pages || {})) {
      const navigation = page?.navigation || {};
      pageEntries.set(path, navigation.label || page?.title || pageEntries.get(path) || path);
    }
    for (const [path, label] of pageEntries) {
      push({
        key: 'page:' + path,
        choiceKey: 'page:' + path,
        group: 'Website pages',
        label,
        defaultText: label,
        href: path,
        managed: false,
        kind: 'page'
      });
    }
    document.querySelectorAll('[data-cms-section][id]').forEach(section => {
      const id = section.id?.trim(); if (!id) return;
      const label = section.querySelector('h1,h2,h3')?.textContent?.trim() || id;
      push({
        key: 'section:' + id,
        choiceKey: 'section:' + id,
        group: 'This page',
        label,
        defaultText: label,
        href: '#' + encodeURIComponent(id),
        managed: false,
        kind: 'section'
      });
    });
    return items;
  }

  function syncCtaControls(model, currentHref) {
    const select = document.getElementById('legend-cms-action');
    const custom = document.getElementById('legend-cms-custom-link');
    const wiring = document.getElementById('legend-cms-action-wiring');
    if (!select) return;
    const options = availableCtaOptions();
    select.replaceChildren();

    const choose = document.createElement('option');
    choose.value = '';
    choose.textContent = 'Choose a CTA or destination…';
    select.appendChild(choose);

    const presets = options.filter(option => option.managed);
    const groups = new Map();
    for (const option of presets) {
      if (!groups.has(option.group)) groups.set(option.group, []);
      groups.get(option.group).push(option);
    }
    for (const [groupName, values] of groups) {
      const group = document.createElement('optgroup');
      group.label = `${String(groupName || 'Action').toUpperCase()} — Automatic analytics`;
      values.forEach(option => {
        const node = document.createElement('option');
        node.value = option.choiceKey;
        node.textContent = option.defaultText;
        group.appendChild(node);
      });
      select.appendChild(group);
    }

    const pages = options.filter(option => option.kind === 'page');
    if (pages.length) {
      const group = document.createElement('optgroup'); group.label = 'WEBSITE PAGES';
      pages.forEach(option => {
        const node = document.createElement('option'); node.value = option.choiceKey; node.textContent = option.label; group.appendChild(node);
      });
      select.appendChild(group);
    }
    const sections = options.filter(option => option.kind === 'section');
    if (sections.length) {
      const group = document.createElement('optgroup'); group.label = 'THIS PAGE';
      sections.forEach(option => {
        const node = document.createElement('option'); node.value = option.choiceKey; node.textContent = option.label; group.appendChild(node);
      });
      select.appendChild(group);
    }

    const other = document.createElement('optgroup'); other.label = 'OTHER';
    const customOption = document.createElement('option'); customOption.value = 'custom'; customOption.textContent = 'Custom Link / Custom Action…'; other.appendChild(customOption);
    select.appendChild(other);

    const key = model?.actionKey || selected?.dataset.websiteActionKey;
    const byKey = key ? options.find(option => option.managed && option.actionKey === key) : null;
    const byHref = !key && currentHref ? options.find(option => !option.managed && option.href === currentHref) : null;
    const selectedOption = byKey || byHref || null;
    const hasCustomHref = currentHref && currentHref !== '#';
    select.value = selectedOption?.choiceKey || (hasCustomHref ? 'custom' : '');
    const lockedManaged = !!key && !!byKey && (!!model?.systemKey || !!model?.systemBinding || (Array.isArray(model?.signals) && model.signals.length>0));
    select.disabled = lockedManaged;
    if (custom) custom.hidden = lockedManaged || select.value !== 'custom';
    if (wiring) {
      wiring.textContent = lockedManaged
        ? `Protected wiring: ${selectedOption.defaultText || selectedOption.label || key}. This system/signal-bound action identity cannot change; presentation remains editable.`
        : selectedOption?.managed
        ? `Managed action: choose any approved catalog action for this CTA instance. Destination and analytics resolve canonically from the selected action.`
        : selectedOption
          ? 'Navigation only. This links to an existing page or section and does not create a second CTA wiring contract.'
          : select.value === 'custom'
            ? 'Custom URL. Use a grouped CTA above when you want the existing automatic analytics contract.'
            : 'Choose an action, then customize the visible text. Its destination and analytics stay connected.';
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
    const composition = block?.dataset?.cmsCompositionId ? compositionNode(block.dataset.cmsCompositionId) : null;
    const sourceNode = composition?.type === 'embed' ? composition : null;
    if (!block || !sourceNode) return;
    clearTimeout(autoSaveTimer);
    const dialog = document.createElement('dialog');
    dialog.className = 'legend-cms-editor legend-cms-code-dialog';
    const title = document.createElement('h2'); title.textContent = 'Edit code block';
    const help = document.createElement('p'); help.textContent = 'HTML, CSS, and browser JavaScript run only inside this sandboxed block. Save to preview it on the page before publishing.';
    const textarea = document.createElement('textarea');
    textarea.className = 'legend-cms-code-source';
    textarea.setAttribute('aria-label', 'Code block source');
    textarea.spellcheck = false;
    textarea.value = sourceNode.text || defaultCodeBlock;
    const status = document.createElement('p'); status.setAttribute('role','status');
    const actions = document.createElement('div'); actions.className = 'legend-cms-code-actions';
    const saveButton = document.createElement('button'); saveButton.type = 'button'; saveButton.textContent = 'Save & preview';
    const cancelButton = document.createElement('button'); cancelButton.type = 'button'; cancelButton.textContent = 'Cancel';
    saveButton.addEventListener('click', () => {
      if (textarea.value.length > 100000) { status.textContent = 'Code blocks can contain up to 100,000 characters.'; return; }
      checkpoint();
      sourceNode.text = textarea.value;
      renderCodePreview(block, sourceNode);
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

  function addCompositionBlock(type) {
    const sectionEl = selectedSection || document.querySelector('[data-cms-section]');
    if (!sectionEl && type !== 'section') return;
    if (type === 'form' && document.querySelector('form[data-website-inquiry]')) {
      alert('This page already has its canonical inquiry form. Select it to restyle or reposition it.');
      return;
    }

    checkpoint();
    const id=(crypto.randomUUID ? crypto.randomUUID() : String(Date.now())).replaceAll('-','');
    const map={button:'cta',code:'embed'};
    const nodeType=map[type] || type;
    const tags={section:'section',text:'p',cta:'a',video:'video',form:'form',experience:'form',embed:'div'}; 
    const defaultClasses={
      section:'section',
      cta:'btn primary',
      image:null,
      form:'public-form legend-cms-inquiry-form',
      experience:'legend-native-experience',
      embed:'legend-cms-embed'
    };
    const node={
      id,type:nodeType,tag:tags[nodeType] || 'div',className:defaultClasses[nodeType] || null,
      videoLoop:nodeType==='video'?false:null,
      text:nodeType==='cta'?'Button':nodeType==='text'?'Your text':nodeType==='form'?'Send inquiry':nodeType==='embed'?defaultCodeBlock:'',
      title:nodeType==='form'?'Send an inquiry':null,
      actionKey:null,href:null,target:nodeType==='cta'?'_self':null,
      systemKey:nodeType==='form'?'canonical_inquiry':null,
      signals:[],style:nodeType==='embed'?{widthPercent:100,heightPx:320}:['form','experience'].includes(nodeType)?{widthPercent:100}:{},
      breakpointStyles:{},layout:{mode:'free',direction:'column'},breakpointLayouts:{},animations:[],children:[],
      experience:nodeType==='experience'?{
        kind:'form',submitCapability:null,
        steps:[{key:'main',title:'Interactive experience',description:'Customize questions, logic, calculations, and results in Selected Source.',controlKeys:['choice']}],
        controls:[{key:'choice',type:'choice',label:'Choose an option',required:true,options:[{value:'option_a',label:'Option A'},{value:'option_b',label:'Option B'}]}],
        calculations:{},results:[]
      }:null
    };

    if(nodeType==='section'){
      const roots=pageState().composition ||= [];
      const selectedId=sectionEl?.dataset?.cmsCompositionId;
      const index=selectedId ? roots.findIndex(value=>value.id===selectedId) : -1;
      if(index>=0) roots.splice(index+1,0,node); else roots.push(node);
    } else {
      const flowContainer=selectedFlowContainer(sectionEl);
      const parentId=flowContainer?.dataset?.cmsCompositionId || sectionEl?.dataset?.cmsCompositionId;
      const parent=parentId ? compositionNode(parentId) : (pageState().composition || []).find(value=>value.type==='section');
      if(!parent){ alert('Select a section before adding content.'); return; }
      parent.children ||= [];
      parent.children.push(node);
    }

    renderCanonicalCompositionPage();
    const created=document.querySelector('[data-cms-id="'+CSS.escape(id)+'"]');
    setSelected(created,{openContent:nodeType==='cta'});
    markDirty();
    if(nodeType==='cta') document.getElementById('legend-cms-action')?.focus();
    if(nodeType==='embed') openCodeEditor();
  }

  function addBlock(type) {
    addCompositionBlock(type);
  }
  function enhanceEditor(panel, preview) {
    const content = document.createElement('div'); content.dataset.cmsView = 'content';
    Array.from(panel.children).filter(el => !el.classList.contains('legend-cms-bar')).forEach(el => content.appendChild(el));
    panel.appendChild(content);
    const navigation = document.createElement('nav');
    navigation.className = 'legend-cms-navigation'; navigation.setAttribute('aria-label', 'Website editing tools');
    navigation.innerHTML = `<div class="legend-cms-device-switch" role="group" aria-label="Editing viewport"><button type="button" data-editor-viewport="base" aria-pressed="true">Desktop</button><button type="button" data-editor-viewport="mobile" aria-pressed="false">Mobile</button></div><small class="legend-cms-device-note">Mobile edits are stored only in the mobile breakpoint and never write into Desktop/Base.</small><div class="legend-cms-tabs legend-cms-primary-tabs"><button type="button" data-open="gpt" data-agent-action="open-workspace">GPT Workspace</button><button type="button" data-open="source" data-agent-action="open-source">Source</button><button type="button" data-open="media" data-agent-action="open-media">Media</button><button type="button" data-open="publish" data-agent-action="open-publish">Publish</button><button type="button" data-open="advanced">Advanced</button></div>`;
    panel.insertBefore(navigation, content);
    const tools = document.createElement('div'); tools.innerHTML = `
      <section data-cms-view="add" hidden><h2>Add a block</h2><p>Add to the selected section, then position and resize it directly on the page.</p><div class="legend-cms-menu"><button data-add="text">Text</button><button data-add="button">Button / link</button><button id="legend-cms-new-image">Image</button><button data-add="video">Video</button><button data-add="form">Inquiry form</button><button data-add="experience">Interactive experience</button><button data-add="code">Code / embed</button><button data-add="section">Section</button></div></section>
      <section data-cms-view="appearance" hidden><h2>Appearance</h2>${appearanceFields()}<button id="legend-cms-container">Select section container</button></section>
      <section data-cms-view="layout" hidden><h2>Responsive layout</h2><p>Edit Desktop/Base or explicitly target one breakpoint. Breakpoint values inherit only fields you leave unset.</p><div class="legend-cms-inline-help" id="legend-cms-responsive-policy"><strong>Canonical responsive hierarchy</strong><br>Desktop/Base and Mobile are independent authoring surfaces for page content. On Mobile, the global public header, brand fit, Menu trigger, and primary navigation geometry are platform shell chrome: width, offsets, height, and layout are canonicalized so navigation cannot collapse, drift off-canvas, or wrap vertically. Colors, copy, and bounded typography remain editable. Other explicit Mobile presentation is preserved within normal validated bounds.</div><label class="legend-cms-group">Editing breakpoint<select id="legend-cms-breakpoint"></select></label><div class="legend-cms-row"><label class="legend-cms-group">Custom name<input id="legend-cms-breakpoint-label" type="text" maxlength="80" placeholder="Large tablet"></label><label class="legend-cms-group">Key<input id="legend-cms-breakpoint-key" type="text" maxlength="40" placeholder="large-tablet"></label></div><div class="legend-cms-row"><label class="legend-cms-group">Min px<input id="legend-cms-breakpoint-min" type="number" min="0" max="10000" value="900"></label><label class="legend-cms-group">Max px<input id="legend-cms-breakpoint-max" type="number" min="0" max="10000" placeholder="No maximum"></label></div><div class="legend-cms-row"><button id="legend-cms-breakpoint-add" type="button">Add breakpoint</button><button id="legend-cms-breakpoint-remove" type="button">Remove custom breakpoint</button></div><hr><label class="legend-cms-group">Container behavior<select id="legend-cms-layout-mode"><option value="free">Free Canvas</option><option value="stack">Stack</option><option value="grid">Grid</option><option value="flex">Flex / Auto Layout</option></select></label><div class="legend-cms-row"><label class="legend-cms-group">Direction<select id="legend-cms-layout-direction"><option value="column">Column</option><option value="row">Row</option></select></label><label class="legend-cms-group">Gap px<input id="legend-cms-layout-gap" type="number" min="0" max="240" step="any"></label></div><div class="legend-cms-row"><label class="legend-cms-group">Grid columns<input id="legend-cms-layout-columns" type="number" min="1" max="12"></label><label class="legend-cms-group">Min item width px<input id="legend-cms-layout-min" type="number" min="1" max="4000"></label></div><div class="legend-cms-row"><label class="legend-cms-group">Align items<select id="legend-cms-layout-align"><option value="">Default</option><option value="start">Start</option><option value="center">Center</option><option value="end">End</option><option value="stretch">Stretch</option></select></label><label class="legend-cms-group">Justify<select id="legend-cms-layout-justify"><option value="">Default</option><option value="start">Start</option><option value="center">Center</option><option value="end">End</option><option value="space-between">Space between</option><option value="space-around">Space around</option><option value="space-evenly">Space evenly</option></select></label></div><label class="legend-cms-group">Wrap<select id="legend-cms-layout-wrap"><option value="">Default</option><option value="nowrap">No wrap</option><option value="wrap">Wrap</option></select></label><p>Selection, movement, and resizing are separate actions: click content to select it, drag the gold Move control to position it, and drag only the border edges or corners to resize. When Mobile is selected, these controls write only to the mobile breakpoint; Desktop/Base remains unchanged.</p><div class="legend-cms-row"><label class="legend-cms-group">X offset %<input id="legend-cms-offset-x" type="number" step="any" value="0"></label><label class="legend-cms-group">Y offset px<input id="legend-cms-offset-y" type="number" step="any" value="0"></label></div><button id="legend-cms-undo">Undo</button><button id="legend-cms-redo">Redo</button></section>
      <section data-cms-view="layers" hidden><h2>Sections</h2><p>Drag only whole page sections to reorder them. Edit headings, buttons, fields, and other content directly on the page so this list stays clean and short.</p><label class="legend-cms-group">Find section<input id="legend-cms-layer-search" type="search" placeholder="Search sections"></label><div id="legend-cms-layers" class="legend-cms-layer-list"></div></section>
      <section data-cms-view="media" hidden><h2>Media library</h2><p>Browse media already owned by this website scope. Reusing an asset does not copy the file or create another storage record.</p><div class="legend-cms-row"><label class="legend-cms-group">Search<input id="legend-cms-media-search" type="search" placeholder="Name or file type"></label><label class="legend-cms-group">Type<select id="legend-cms-media-kind"><option value="all">All media</option><option value="image">Images</option><option value="video">Videos</option></select></label></div><input id="legend-cms-media-upload" type="file" data-agent-action="upload-media" accept="image/*,video/*,.heic,.heif,.avif,.mov,.m4v,.webm"><button id="legend-cms-media-refresh" type="button" data-agent-action="refresh-media">Refresh library</button><small id="legend-cms-media-status" role="status"></small><div id="legend-cms-media-grid" class="legend-cms-media-grid"></div></section>\n      <section data-cms-view="components" hidden><h2>Reusable components</h2><p>Save any non-system canonical block or section once, then insert synchronized references. Protected platform components cannot be copied into reusable content.</p><label class="legend-cms-group">Component name<input id="legend-cms-component-name" type="text" maxlength="120" placeholder="Hero, testimonial, contact band"></label><button id="legend-cms-component-save" type="button">Save selected as component</button><small id="legend-cms-component-status" role="status"></small><div id="legend-cms-component-list" class="legend-cms-component-list"></div></section>\n      <section data-cms-view="data" hidden><h2>Dynamic CMS</h2><p id="legend-cms-data-unavailable" hidden>Scoped business data is available only on Business websites.</p><div id="legend-cms-data-business"><h3>Selected content binding</h3><label class="legend-cms-group">Source<select id="legend-cms-data-source"></select></label><div class="legend-cms-row"><label class="legend-cms-group">Field<select id="legend-cms-data-field"></select></label><label class="legend-cms-group">Apply as<select id="legend-cms-data-target"><option value="text">Text</option><option value="image">Image URL</option><option value="href">Link destination</option></select></label></div><div class="legend-cms-row"><button id="legend-cms-data-bind" type="button">Bind selected</button><button id="legend-cms-data-clear" type="button">Clear binding</button></div><small id="legend-cms-data-status" role="status"></small><hr><h3>Dynamic page</h3><p>Use an existing list source to generate one published route per item. The preview choice below is local editor state only.</p><label class="legend-cms-group">List source<select id="legend-cms-dynamic-source"></select></label><div class="legend-cms-row"><label class="legend-cms-group">Route key field<select id="legend-cms-dynamic-key"></select></label><label class="legend-cms-group">Route pattern<input id="legend-cms-dynamic-pattern" type="text" placeholder="/products/{item}"></label></div><label class="legend-cms-group">Preview item<select id="legend-cms-dynamic-preview"></select></label><div class="legend-cms-row"><button id="legend-cms-dynamic-apply" type="button">Apply dynamic page</button><button id="legend-cms-dynamic-clear" type="button">Make page static</button></div><small id="legend-cms-dynamic-status" role="status"></small></div></section>\n      <section data-cms-view="page" hidden><h2>Pages & search appearance</h2><p>Page structure and SEO stay in the same versioned website document.</p><div id="legend-cms-page-list" class="legend-cms-page-list"></div><p id="legend-cms-page-fixed-notice" hidden>LEGEND and Protect currently expose only their real published route catalog. Arbitrary route creation stays disabled until their shared route-manifest publication layer is connected.</p><div id="legend-cms-page-business-tools"><div class="legend-cms-row"><label class="legend-cms-group">Navigation label<input id="legend-cms-page-nav-label" type="text" maxlength="120"></label><label class="legend-cms-group">Route / slug<input id="legend-cms-page-slug" type="text" maxlength="160"></label></div><div class="legend-cms-row"><label class="legend-cms-group">Parent page<select id="legend-cms-page-parent"></select></label><label class="legend-cms-group">Navigation order<input id="legend-cms-page-order" type="number" step="1"></label></div><label class="legend-cms-group"><input id="legend-cms-page-nav-visible" type="checkbox"> Show in public navigation</label><div class="legend-cms-menu"><button id="legend-cms-page-create" type="button">Add page</button><button id="legend-cms-page-duplicate" type="button">Duplicate page</button><button id="legend-cms-page-rename" type="button">Rename / move route</button><button id="legend-cms-page-delete" type="button">Delete page</button></div></div><hr><label class="legend-cms-group">Page title<input id="legend-cms-page-title" type="text" maxlength="200"></label><label class="legend-cms-group">Search description<textarea id="legend-cms-page-description" rows="4" maxlength="500"></textarea></label><div class="legend-cms-search-preview"><strong id="legend-cms-search-title"></strong><p id="legend-cms-search-description"></p></div></section>
      <section data-cms-view="theme" id="legend-cms-theme-view" hidden><h2>Site theme</h2><p>One palette, typography system, and browser icon for every page of this website.</p><div class="legend-cms-group legend-cms-favicon"><label for="legend-cms-favicon">Browser favicon</label><img id="legend-cms-favicon-preview" class="legend-cms-favicon-preview" alt=""><input id="legend-cms-favicon" type="file" accept="image/*,.heic,.heif,.avif"><small>A valid image file. The shared media authority verifies the file bytes; this is scoped to this website and becomes public only when the website is published.</small><button id="legend-cms-favicon-remove" type="button">Use LEGEND fallback favicon</button></div></section>`;
    panel.appendChild(tools);
    const sourceView=document.createElement('section'); sourceView.dataset.cmsView='source'; sourceView.hidden=true;
    sourceView.innerHTML='<h2>Canonical Source</h2><p>Master Source is a read-only, color-guided inspection of the canonical v3 graph. Selected Source is the normal editable code surface: click, select, type, paste, use Tab / Shift+Tab to indent or outdent, and Enter to keep indentation. Canvas, Selected Source, drafts, validation, and publish all resolve to the same stable node identities.</p><label class="legend-cms-group">Scope<select id="legend-cms-source-scope"><option value="site">Master Source · read only</option><option value="selection">Selected Source · editable</option></select></label><small id="legend-cms-source-location">Master Source · entire website · read only</small><div class="legend-cms-source-key" aria-label="Source color guide"><span data-tone="content">Content</span><span data-tone="style">Typography & style</span><span data-tone="color">Color</span><span data-tone="size">Size & spacing</span><span data-tone="layout">Layout & responsive</span><span data-tone="media">Media</span><span data-tone="behavior">Behavior</span><span data-tone="structure">Authorable structure</span><span data-tone="protected">Protected identity</span></div><div class="legend-cms-source-editor"><pre id="legend-cms-source-highlight" class="legend-cms-source-highlight" aria-hidden="true"></pre><textarea id="legend-cms-site-source" class="legend-cms-site-source" data-agent-surface="site-source" rows="28" spellcheck="false" autocapitalize="off" autocomplete="off" autocorrect="off" wrap="soft" aria-label="Canonical source"></textarea></div><small id="legend-cms-source-status" role="status">Master Source is synchronized and read only.</small><div class="legend-cms-protection-warning" data-canonical-protection-warning role="alert" hidden></div><div class="legend-cms-row"><button id="legend-cms-source-apply" type="button" data-agent-action="apply-source">Apply selected source</button><button id="legend-cms-source-reload" type="button" data-agent-action="reload-source">Refresh from canonical graph</button></div>';
    tools.appendChild(sourceView);

    const publishView=document.createElement('section'); publishView.dataset.cmsView='publish'; publishView.hidden=true;
    publishView.innerHTML='<h2>Publish</h2><p>The same immutable publish authority validates canonical CTAs, media ownership, forms, navigation, responsive structure, and website versioning before anything becomes live.</p><div class="legend-cms-row"><button id="legend-cms-publish-save-draft" type="button" data-agent-action="save-draft">Save named draft</button><button id="legend-cms-publish-now" type="button" data-agent-action="publish">Publish current draft</button></div><button id="legend-cms-publish-quality" type="button" data-agent-action="quality-preflight">Run quality preflight</button><small id="legend-cms-publish-note">Publishing never bypasses the canonical action/event catalogs.</small>';
    tools.appendChild(publishView);

    const advanced=document.createElement('section'); advanced.dataset.cmsView='advanced'; advanced.hidden=true;
    advanced.innerHTML='<h2>Advanced controls</h2><p>Precision tools remain available without crowding the everyday workflow.</p><div class="legend-cms-menu"><button data-open="content">Selected content</button><button data-open="add">Add blocks</button><button data-open="appearance">Design</button><button data-open="layout">Responsive</button><button data-open="layers">Layers</button><button data-open="components">Components</button><button data-open="data">Dynamic data</button><button data-open="motion">Motion</button><button data-open="signals">Analytics</button><button data-open="quality">Quality</button><button data-open="collaboration">Collaborate</button><button data-open="theme">Site theme</button><button data-open="page">Pages & SEO</button></div>';
    tools.appendChild(advanced);
    const signals = document.createElement('section'); signals.dataset.cmsView = 'signals'; signals.hidden = true;
    signals.innerHTML = '<h2>Analytics & Meta</h2><p>Standard page engagement, managed buttons, and the canonical inquiry form are wired automatically from the shared Protect Website analytics and Meta authorities. Select content to review that wiring. Advanced custom mappings are only for non-standard interactions.</p><div id="legend-cms-signal-controls"></div>';
    tools.appendChild(signals);
    const motion = document.createElement('section'); motion.dataset.cmsView='motion'; motion.hidden=true;
    motion.innerHTML='<h2>Motion & interactions</h2><p>Declarative visual motion only. These effects never create analytics, leads, bookings, purchases, or other business outcomes.</p><small id="legend-cms-motion-status">Select an element to configure motion.</small><div id="legend-cms-motion-controls"></div>';
    tools.appendChild(motion);

    const gpt = document.createElement('section'); gpt.dataset.cmsView='gpt'; gpt.hidden=true;
    gpt.id='legend-cms-browser-agent-workspace';
    gpt.dataset.agentWorkspace='browser-only';
    gpt.dataset.externalAiApi='false';
    gpt.innerHTML='<h2>GPT Browser Workspace</h2><p>Use an authorized browser session to let GPT operate this exact Website Studio. No website content is sent to OpenAI by this application and no OpenAI API key is used here.</p><div class="legend-cms-agent-contract"><strong>Canonical operating contract</strong><pre id="legend-cms-agent-contract-script"></pre></div><small id="legend-cms-browser-agent-status" role="status"></small><div class="legend-cms-protection-warning" data-canonical-protection-warning role="alert" hidden></div><div class="legend-cms-menu"><button id="legend-cms-agent-master-source" type="button" data-agent-action="master-source">Inspect Master Source</button><button id="legend-cms-agent-selection-source" type="button" data-agent-action="selection-source">Edit selected source</button><button id="legend-cms-agent-media" type="button" data-agent-action="media-library">Open Media</button><button id="legend-cms-agent-quality" type="button" data-agent-action="quality-preflight">Run Quality</button><button id="legend-cms-agent-publish" type="button" data-agent-action="publish-workspace">Open Publish</button></div><p><strong>For browser agents:</strong> stable component IDs are exposed as <code>data-cms-id</code>. Master Source is inspection-only. Make source-code changes only through Selected Source, then validate and save through the canonical authority.</p>';
    const agentScript=gpt.querySelector('#legend-cms-agent-contract-script');
    if(agentScript) agentScript.textContent=managementPayload?.agentContract?.promptTemplate || 'Canonical GPT operating contract unavailable; do not modify this website until the server contract is loaded.';
    tools.appendChild(gpt);

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
    const links = document.createElement('div'); links.innerHTML = `<div id="legend-cms-link-group" class="legend-cms-group" hidden><label for="legend-cms-action">CTA / link</label><select id="legend-cms-action"></select><small>Choose an action. Visible text and styling can change freely without changing its destination or analytics.</small><small id="legend-cms-action-wiring"></small><div id="legend-cms-custom-link"><label for="legend-cms-href">Custom destination</label><input id="legend-cms-href" type="url" placeholder="https://…"></div><label><input id="legend-cms-target" type="checkbox"> Open in a new tab</label></div><div id="legend-cms-video-group" class="legend-cms-group" hidden><label for="legend-cms-video-file">Replace video from this website's media library</label><input id="legend-cms-video-file" type="file" accept="video/*,.mov,.m4v,.webm"><label><input id="legend-cms-video-loop" type="checkbox"> Loop continuously · hide playback controls</label><small>Loop mode preserves the uploaded audio track. Browsers may require the first user gesture before audible autoplay begins.</small></div><label class="legend-cms-group">Image description<input id="legend-cms-alt" type="text"></label>`;
    content.appendChild(links);
    panel.querySelectorAll('[data-open]').forEach(button => button.addEventListener('click', () => showPanel(button.dataset.open)));
    document.getElementById('legend-cms-agent-master-source')?.addEventListener('click',()=>openBrowserAgentSource('site'));
    document.getElementById('legend-cms-agent-selection-source')?.addEventListener('click',()=>openBrowserAgentSource('selection'));
    document.getElementById('legend-cms-agent-media')?.addEventListener('click',()=>showPanel('media'));
    document.getElementById('legend-cms-agent-quality')?.addEventListener('click',()=>{showPanel('quality');void refreshQualityInspector();});
    document.getElementById('legend-cms-agent-publish')?.addEventListener('click',()=>showPanel('publish'));

    const sourceTextarea=document.getElementById('legend-cms-site-source');
    sourceTextarea?.addEventListener('keydown',handleSourceEditorKeydown);
    sourceTextarea?.addEventListener('input',()=>{
      if(sourceTextarea.readOnly){
        sourceEditorDirty=false;
        void refreshSiteSourceEditor(true);
        return;
      }
      sourceEditorDirty=true;
      renderSourceHighlight();
      const status=document.getElementById('legend-cms-source-status');
      if(status) status.textContent='Selected Source has unapplied changes · canvas remains on the last validated canonical node.';
    });
    sourceTextarea?.addEventListener('scroll',()=>{
      const highlight=document.getElementById('legend-cms-source-highlight');
      if(highlight){highlight.scrollTop=sourceTextarea.scrollTop;highlight.scrollLeft=sourceTextarea.scrollLeft;}
    });
    document.getElementById('legend-cms-source-scope')?.addEventListener('change',()=>{
      sourceEditorDirty=false;
      void refreshSiteSourceEditor(true);
    });
    document.getElementById('legend-cms-source-apply')?.addEventListener('click',()=>void applySiteSource());
    document.getElementById('legend-cms-source-reload')?.addEventListener('click',()=>{
      sourceEditorDirty=false;
      void refreshSiteSourceEditor(true);
    });
    document.getElementById('legend-cms-publish-save-draft')?.addEventListener('click',chooseDraft);
    document.getElementById('legend-cms-publish-now')?.addEventListener('click',()=>void save(true));
    document.getElementById('legend-cms-publish-quality')?.addEventListener('click',()=>{
      showPanel('quality');
      void refreshQualityInspector();
    });
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
      checkpoint(); const collection=ensureCollectionForSource(sourceKey,[field]); const model=selectedWebsiteModel(); if(!collection || !model) return;
      model.dataBinding={collectionId:collection.id,field,target}; applyCompositionNode(selected,model); markDirty(); renderDataControls();
    });
    document.getElementById('legend-cms-data-clear')?.addEventListener('click',()=>{
      const model=selectedWebsiteModel(); if(!model?.dataBinding) return; checkpoint(); delete model.dataBinding; applyCompositionNode(selected,model); markDirty(); renderDataControls();
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
      if(!pageState().dynamicBinding) return; checkpoint(); pageState().dynamicBinding=null; dynamicCollectionItem=null; refreshResponsiveComposition(); markDirty(); renderDataControls();
    });
    document.getElementById('legend-cms-dynamic-preview')?.addEventListener('change',event=>{
      const binding=pageState().dynamicBinding; const projection=binding?collectionData.get(binding.collectionId):null;
      const item=(projection?.items||[]).find(value=>value.key===event.target.value);
      dynamicCollectionItem=item?{collectionId:binding.collectionId,key:item.key,fields:item.fields}:null; refreshResponsiveComposition(); renderDataControls();
    });
    document.getElementById('legend-cms-collaboration-add')?.addEventListener('click',()=>void createCollaborationComment(false));
    document.getElementById('legend-cms-collaboration-page')?.addEventListener('click',()=>void createCollaborationComment(true));
    document.getElementById('legend-cms-component-save')?.addEventListener('click',()=>{
      const status=document.getElementById('legend-cms-component-status');
      const name=document.getElementById('legend-cms-component-name')?.value?.trim();
      const definition=captureReusableDefinition(name);
      if(!definition){ if(status) status.textContent='Select a non-system canonical block or section. Protected platform components cannot become reusable content.'; return; }
      checkpoint(); documentState.reusableComponents ||= {}; documentState.reusableComponents[definition.id]=definition;
      if(status) status.textContent=`Saved ${definition.name}.`; markDirty(); renderReusableComponents();
    });
    document.getElementById('legend-cms-media-refresh')?.addEventListener('click',()=>void refreshMediaLibrary());
    document.getElementById('legend-cms-media-search')?.addEventListener('input',()=>void refreshMediaLibrary());
    document.getElementById('legend-cms-media-kind')?.addEventListener('change',()=>void refreshMediaLibrary());
    document.getElementById('legend-cms-media-upload')?.addEventListener('change',async event=>{ const file=event.target.files?.[0]; if(!file) return; const asset=await uploadMedia(file); event.target.value=''; if(asset) await refreshMediaLibrary(); });
    document.getElementById('legend-cms-quality-refresh')?.addEventListener('click', () => void refreshQualityInspector());
    document.getElementById('legend-cms-breakpoint')?.addEventListener('change', event => { editorBreakpointKey=event.target.value; applyBreakpointPreview(); syncBreakpointControls(); });
    panel.querySelectorAll('[data-editor-viewport]').forEach(button=>button.addEventListener('click',()=>{
      editorBreakpointKey=button.dataset.editorViewport==='mobile' ? 'mobile' : 'base';
      applyBreakpointPreview();
      syncBreakpointControls();
    }));
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
      checkpoint(); const key=current.key; documentState.breakpoints=documentState.breakpoints.filter(value=>value.key!==key);
      forEachWebsiteModel(model=>{ if(model?.breakpointStyles) delete model.breakpointStyles[key]; if(model?.breakpointLayouts) delete model.breakpointLayouts[key]; });
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
      if(!selected) return; checkpoint(); const model=selectedWebsiteModel(); if(!model) return; const layout=editingLayout(model,true); const value=convert(event.target.value); if(value==null) delete layout[field]; else layout[field]=value; applyCompositionNode(selected,model); updateDirectCanvasUi(); markDirty();
    }));
    installStudioControls(panel);
    document.getElementById('legend-cms-action').addEventListener('change', event => {
      if (!selected || selected.tagName !== 'A') return;
      const ov = selectedWebsiteModel(); if (!ov) return;
      const actionIdentityLocked=!!ov.systemKey || !!ov.systemBinding || (Array.isArray(ov.signals) && ov.signals.length>0);
      if(actionIdentityLocked && ov.actionKey){ syncEditorControls(); return; }
      checkpoint();
      const option = availableCtaOptions().find(candidate => candidate.choiceKey === event.target.value);
      if (!option) {
        if (event.target.value === 'custom') {
          delete ov.actionKey;
          delete selected.dataset.websiteActionKey;
          document.getElementById('legend-cms-custom-link').hidden = false;
        } else {
          delete ov.actionKey;
          delete selected.dataset.websiteActionKey;
          ov.href = '';
          ov.target = '_self';
          document.getElementById('legend-cms-custom-link').hidden = true;
        }
        applyCompositionNode(selected, ov); syncEditorControls(); markDirty();
        return;
      }
      if (option.managed) {
        ov.actionKey = option.actionKey;
        selected.dataset.websiteActionKey=option.actionKey;
      } else {
        delete ov.actionKey;
        delete selected.dataset.websiteActionKey;
      }
      ov.href = option.href; ov.target = option.openInNewTab ? '_blank' : '_self';
      if (selected.dataset.cmsCompositionId) {
        ov.text = option.defaultText || option.label;
        setContentText(selected, ov.text, true);
      }
      applyCompositionNode(selected, ov); syncEditorControls(); markDirty();
    });
    panel.querySelectorAll('[data-add]').forEach(button => button.addEventListener('click', () => addBlock(button.dataset.add)));
    document.getElementById('legend-cms-new-image').addEventListener('click', () => document.getElementById('legend-cms-image-upload').click());
    document.getElementById('legend-cms-edit-code')?.addEventListener('click', openCodeEditor);
    document.getElementById('legend-cms-container').addEventListener('click', () => { if (selectedSection) setSelected(selectedSection); });
    panel.querySelectorAll('[data-style-key]').forEach(input => input.addEventListener('input', () => {
      if (!selected) return;
      const key=input.dataset.styleKey;
      const numeric=input.type==='number' || key==='fontWeight';
      const value=numeric ? Number(input.value) : input.value;
      if(numeric && input.value!==''){
        if(!Number.isFinite(value)) return;
        const allowNegative=['letterSpacing','marginTop','marginBottom','marginLeft','marginRight'].includes(key);
        if(!allowNegative && value<0) return;
        if(['fontSize','lineHeight','aspectRatio'].includes(key) && value===0) return;
        if(key==='opacity' && (value<0 || value>1)) return;
        if(key==='fontWeight' && (value<100 || value>900)) return;
      }
      checkpoint();
      const ov=selectedWebsiteModel(); if(!ov) return;
      const style=editingStyle(ov,true);
      if(input.value==='') delete style[key]; else style[key]=value;
      applyStyle(selected,effectiveStyle(ov));
      updateDirectCanvasUi();
      syncSelectedSourcePresentationFromCanvas();
      syncEditorControls();
      markDirty();
    }));
    panel.querySelectorAll('[data-color-reset]').forEach(button => button.addEventListener('click', () => {
      if (!selected) return;
      checkpoint();
      const ov=selectedWebsiteModel(); if(!ov) return;
      const style=editingStyle(ov,true);
      delete style[button.dataset.colorReset];
      applyStyle(selected,effectiveStyle(ov));
      syncEditorControls();
      markDirty();
    }));
    ['href','alt'].forEach(key => document.getElementById(`legend-cms-${key}`).addEventListener('input', event => { if (!selected) return; const value = event.target.value; if (key === 'href' && !safeUrl(value)) { event.target.setCustomValidity('Enter a supported URL.'); return; } event.target.setCustomValidity(''); checkpoint(); const ov = selectedWebsiteModel(); if (key === 'href' && ov.actionKey) { syncEditorControls(); return; } ov[key] = value; if (key === 'href') { const action = document.getElementById('legend-cms-action'); if (action) action.value = 'custom'; const custom = document.getElementById('legend-cms-custom-link'); if (custom) custom.hidden = false; const wiring = document.getElementById('legend-cms-action-wiring'); if (wiring) wiring.textContent = 'Custom link. Preset actions above are the backend-wired choices.'; } applyCompositionNode(selected, ov); markDirty(); }));
    document.getElementById('legend-cms-video-file').addEventListener('change', async event => {
      const video = selected;
      if (video?.tagName !== 'VIDEO' || !video.dataset.cmsCompositionId) return;
      const asset = await uploadMedia(event.target.files?.[0]);
      event.target.value='';
      if (!asset || selected !== video || !String(asset.contentType).startsWith('video/')) return;
      checkpoint();
      const node = selectedWebsiteModel();
      if(!node) return;
      node.mediaAssetId = asset.id;
      delete node.mediaUrl;
      applyCompositionNode(video, node);
      syncEditorControls();
      markDirty();
    });
    document.getElementById('legend-cms-video-loop')?.addEventListener('input', event => {
      if(selected?.tagName!=='VIDEO') return;
      const ov=selectedWebsiteModel(); if(!ov) return;
      checkpoint();
      ov.videoLoop=event.target.checked === true;
      applyCompositionNode(selected,ov);
      markDirty();
    });
    document.getElementById('legend-cms-target').addEventListener('input', event => { if (!selected) return; checkpoint(); const ov = selectedWebsiteModel(); ov.target = event.target.checked ? '_blank' : '_self'; ov.href ||= rememberOriginal(selected).href; applyCompositionNode(selected, ov); markDirty(); });
    document.getElementById('legend-cms-undo').addEventListener('click', () => restoreCanonicalV3History(undoStack, redoStack));
    document.getElementById('legend-cms-redo').addEventListener('click', () => restoreCanonicalV3History(redoStack, undoStack));
    installDirectCanvasControls(preview);
    showPanel('gpt');
  }

  function injectContentStyles() { const style = document.createElement('style'); style.textContent = `
      html,body{max-width:100%}
      body{overflow-x:clip}
      main,main>*{min-width:0;max-width:100%;box-sizing:border-box}
      main *{min-width:0;box-sizing:border-box}
      main :is(h1,h2,h3,h4,h5,p,li,a,button,label,small,strong,span){max-width:100%;overflow-wrap:anywhere;word-break:normal;white-space:normal}
      main img,main video,main iframe,main form{max-width:100%}
      [data-cms-editable="true"]{min-width:0;max-width:100%;box-sizing:border-box;overflow-wrap:anywhere}
      [data-cms-id][hidden]{display:none}
      .cms-layout-frame{display:grid;grid-template-columns:repeat(12,minmax(0,1fr));gap:clamp(8px,2vw,24px);width:100%;min-width:0;max-width:100%}
      .cms-layout-frame>*{grid-column:var(--cms-column,1) / span var(--cms-span,12);max-width:100%;min-width:0;overflow-wrap:anywhere}
      .legend-legacy-migration-section{padding:clamp(24px,5vw,64px);min-height:120px;max-width:100%;overflow-x:clip}
      .legend-legacy-migration-node video,video.legend-legacy-migration-node{max-width:100%;height:auto}
      .legend-cms-embed,.legend-legacy-migration-code{display:block;width:100%;max-width:100%;height:320px;min-height:72px;overflow:hidden;background:#fff}
      .legend-cms-embed iframe,.legend-legacy-migration-code iframe{display:block;width:100%;max-width:100%;height:100%;border:0;background:#fff}
      .legend-store-nav-cluster{display:flex;align-items:center;gap:clamp(8px,1vw,14px);margin-left:auto;flex:0 0 auto;white-space:nowrap}
      .legend-store-nav-cluster>a{display:inline-flex;align-items:center;justify-content:center}
      .legend-store-cart{position:relative}
      .legend-store-cart .pf-cart-count{position:absolute;right:-8px;top:-8px;display:grid;place-items:center;min-width:17px;height:17px;padding:0 4px;border-radius:999px;background:var(--web-gold,#d4ad45);color:var(--web-navy-deep,#07152d);font:800 10px/1 Inter,system-ui,sans-serif}
      .legend-store-cart-icon{display:block}
      @media(max-width:767px){
        html,body{width:100%;max-width:100%;overflow-x:hidden;overscroll-behavior-x:none}
        body{touch-action:pan-y pinch-zoom}
        main,main>section,main>.section,main>.page-hero,main>.cta,main>.legal-page-wrap,main>.quote-page,main>.container-narrow,main>.training-page{width:100%;max-width:100%;min-width:0;overflow-x:clip}
        main *{max-width:100%;box-sizing:border-box}
        .cms-layout-frame>*{grid-column:1 / -1}
      }
`; document.head.appendChild(style); }

  function injectEditorStyles() {
    const style = document.createElement('style');
    style.textContent = `
      .legend-cms-selected{outline:none}
      [data-cms-editable="true"]{cursor:pointer}
      .legend-cms-inline-editing{cursor:text;user-select:text;caret-color:currentColor}
      .legend-cms-preview .legend-cms-embed iframe,.legend-cms-preview .legend-legacy-migration-code iframe{pointer-events:none}
      .legend-cms-grid-overlay{position:absolute;z-index:2147482000;pointer-events:none;border:1px solid #d4ad454d;background-color:#081a3a08;background-image:linear-gradient(to right,#d4ad4526 1px,transparent 1px),linear-gradient(to bottom,#d4ad4517 1px,transparent 1px);background-size:calc(100% / 12) 100%,100% 24px}
      .legend-cms-grid-overlay::before,.legend-cms-grid-overlay::after{content:"";position:absolute;pointer-events:none;opacity:0;background:#f0cf78;box-shadow:0 0 0 1px #081a3a66}
      .legend-cms-grid-overlay::before{left:50%;top:0;bottom:0;width:1px;transform:translateX(-.5px)}
      .legend-cms-grid-overlay::after{top:50%;left:0;right:0;height:1px;transform:translateY(-.5px)}
      .legend-cms-grid-overlay.legend-cms-snap-x::before,.legend-cms-grid-overlay.legend-cms-snap-y::after{opacity:1}
      .legend-cms-selection-frame{position:absolute;z-index:2147482500;pointer-events:none;border:1px solid #d4ad45;box-shadow:0 0 0 1px #081a3a26}
      .legend-cms-move-handle{position:absolute;left:8px;top:8px;z-index:2;pointer-events:auto;touch-action:none;min-width:48px!important;min-height:30px!important;padding:5px 10px!important;border:1px solid #081a3a!important;border-radius:999px!important;background:#d4ad45!important;color:#081a3a!important;font:800 11px/1 Inter,system-ui,sans-serif!important;letter-spacing:.02em;cursor:grab!important;box-shadow:0 4px 12px #0004!important}
      .legend-cms-move-handle:active{cursor:grabbing!important}
      .legend-cms-selection-frame[data-section-selected="true"] .legend-cms-move-handle{display:none}
      .legend-cms-selection-frame[data-text-editing="true"] .legend-cms-move-handle,
      .legend-cms-selection-frame[data-text-editing="true"] .legend-cms-edge-handle{display:none!important;pointer-events:none!important}
      .legend-cms-store-controls{display:grid;gap:6px;margin:4px 0 8px;padding:0;border:0;background:transparent;color:var(--web-surface,#fff)}
      #legend-cms-store-toggle{justify-self:start;width:auto;min-height:32px!important;padding:6px 11px!important;border:1px solid color-mix(in srgb,var(--web-gold,#d4ad45) 58%,transparent)!important;border-radius:8px!important;background:color-mix(in srgb,var(--web-navy-deep,#07152d) 86%,var(--web-gold,#d4ad45) 14%)!important;color:var(--web-surface,#fff)!important;font-weight:800}
      .legend-cms-store-settings{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr);gap:6px;padding:7px;border:1px solid #344766;border-radius:9px;background:#0d213e}.legend-cms-store-settings label{display:grid;gap:4px;margin:0;font-size:10px;font-weight:800;color:#cbd7e8}.legend-cms-store-actions{display:grid;grid-template-columns:1fr 1fr;gap:6px}.legend-cms-store-actions button{min-height:32px!important;padding:6px 8px!important;border-color:#50617e!important;background:#142c50!important;color:#fff!important}.legend-cms-store-controls input,.legend-cms-store-controls select{min-height:32px!important;padding:5px 7px!important;border-color:#50617e!important;background:#142c50!important;color:#fff!important}
      .legend-cms-store-preview{position:absolute!important;inset:0!important;z-index:2147482400!important;display:grid!important;grid-template-rows:auto minmax(0,1fr)!important;background:var(--web-surface,#fff)!important;color:var(--web-ink,#101a35)!important;pointer-events:auto!important}
      .legend-cms-store-preview[hidden]{display:none!important}.legend-cms-store-preview-bar{display:flex;align-items:center;justify-content:space-between;gap:12px;padding:8px 10px;border-bottom:1px solid color-mix(in srgb,var(--web-gold,#d4ad45) 50%,transparent);background:var(--web-surface,#fff)}.legend-cms-store-preview-frame{width:100%;height:100%;border:0;background:var(--web-surface,#fff)}
      .legend-cms-store-manager{position:fixed!important;inset:0!important;z-index:2147483640!important;display:grid!important;place-items:center!important;padding:2vmin!important;background:#0009!important;pointer-events:auto!important}.legend-cms-store-manager[hidden]{display:none!important}.legend-cms-store-manager-shell{width:min(98vw,1600px);height:96dvh;display:grid;grid-template-rows:auto minmax(0,1fr);overflow:hidden;border-radius:16px;background:var(--web-surface,#fff);box-shadow:0 24px 70px #0008}.legend-cms-store-manager-top{display:flex;align-items:center;justify-content:space-between;padding:10px 14px;border-bottom:1px solid color-mix(in srgb,var(--web-gold,#d4ad45) 45%,transparent);color:var(--web-ink,#101a35)}.legend-cms-store-manager-frame{width:100%;height:100%;border:0;background:var(--web-surface,#fff)}
      .legend-cms-edge-handle{position:absolute;pointer-events:auto;touch-action:none;margin:0;padding:0;border:0!important;border-radius:0!important;background:transparent!important;box-shadow:none!important;min-width:0!important;min-height:0!important}
      .legend-cms-edge-top,.legend-cms-edge-bottom{left:10px;right:10px;height:12px;cursor:ns-resize}
      .legend-cms-edge-top{top:-6px}.legend-cms-edge-bottom{bottom:-6px}
      .legend-cms-edge-left,.legend-cms-edge-right{top:10px;bottom:10px;width:12px;cursor:ew-resize}
      .legend-cms-edge-left{left:-6px}.legend-cms-edge-right{right:-6px}
      .legend-cms-corner-nw,.legend-cms-corner-ne,.legend-cms-corner-se,.legend-cms-corner-sw{width:14px;height:14px}
      .legend-cms-corner-nw{left:-7px;top:-7px;cursor:nwse-resize}.legend-cms-corner-ne{right:-7px;top:-7px;cursor:nesw-resize}.legend-cms-corner-se{right:-7px;bottom:-7px;cursor:nwse-resize}.legend-cms-corner-sw{left:-7px;bottom:-7px;cursor:nesw-resize}
      .legend-cms-edge-handle:hover{background:#d4ad451f!important}
      body.legend-cms-editing{display:block;height:100dvh;min-height:0;margin:0;overflow:hidden}
      .legend-cms-preview{width:100vw;max-width:none;min-width:100vw;min-height:0;height:100dvh;overflow-y:auto;overflow-x:clip;overscroll-behavior-x:none;touch-action:pan-y pinch-zoom;position:relative;transform:translateZ(0)}
      .legend-cms-editor{font-family:Inter,system-ui,sans-serif;box-sizing:border-box}
      .legend-cms-editor *{box-sizing:border-box}
      .legend-cms-editor [hidden]{display:none}
      .legend-cms-preview,.legend-cms-panel{scrollbar-width:none}
      .legend-cms-preview::-webkit-scrollbar,.legend-cms-panel::-webkit-scrollbar{display:none}
      .legend-cms-bar{display:flex;flex-wrap:wrap;align-items:center;gap:8px;padding:14px 0;background:#081a3a;color:#fff;border-bottom:1px solid #344766;margin:0 0 12px;position:sticky;top:-20px;z-index:2}
      .legend-cms-bar button{min-height:38px;border-radius:8px;padding:8px 12px;border:1px solid #50617e;background:#142c50;color:#fff;font-weight:650}
      .legend-cms-bar .primary{background:#d4ad45;color:#081a3a}
      .legend-cms-panel{position:fixed;z-index:2147483000;top:0;right:0;width:min(24rem,92vw);min-width:20rem;min-height:0;height:100dvh;overflow:auto;background:#081a3a;color:#f7f6f2;border:1px solid #d4ad45;border-radius:0;padding:20px;padding-bottom:max(20px,env(safe-area-inset-bottom));box-shadow:-18px 0 42px #0005}
      body.legend-cms-panel-hidden .legend-cms-panel{display:none}
      .legend-cms-draft-dialog{width:min(500px,calc(100vw - 32px));height:auto;max-height:calc(100dvh - 32px);border-radius:16px}.legend-cms-draft-dialog::backdrop{background:#0009}.legend-cms-draft-dialog label{display:grid;gap:8px;margin:16px 0}.legend-cms-draft-dialog button{padding:10px 16px;margin-right:8px}
      .legend-cms-code-dialog{width:min(980px,calc(100vw - 32px));height:min(78dvh,760px);max-height:calc(100dvh - 32px);display:grid;grid-template-rows:auto auto minmax(220px,1fr) auto auto;gap:12px;padding:20px;border:1px solid #d4ad45;border-radius:16px;background:#081a3a;color:#f7f6f2}.legend-cms-code-dialog::backdrop{background:#000a}.legend-cms-code-dialog h2,.legend-cms-code-dialog p{margin:0}.legend-cms-code-source{width:100%;min-width:0;min-height:220px;resize:none;padding:14px;border:1px solid #50617e;border-radius:10px;background:#07152d;color:#f7f6f2;font:13px/1.5 ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;tab-size:2}.legend-cms-code-actions{display:flex;gap:10px;justify-content:flex-end}.legend-cms-code-actions button,#legend-cms-code-group button{padding:10px 14px;border:1px solid #50617e;border-radius:10px;background:#142c50;color:#fff;font-weight:700}
      .legend-cms-panel-toggle{position:fixed;z-index:2147483000;top:max(10px,env(safe-area-inset-top));right:10px;width:40px;height:40px;min-width:40px;min-height:40px;padding:0;display:grid;place-items:center;border:1px solid #d4ad45;border-radius:999px;background:#081a3af2;color:#fff;cursor:pointer;box-shadow:0 8px 24px #0005}
      .legend-cms-panel-toggle svg{width:20px;height:20px;fill:none;stroke:currentColor;stroke-width:2;stroke-linecap:round;stroke-linejoin:round}
      .legend-cms-sheet-handle{display:none}
      .legend-cms-panel h2{margin:0 0 3px;font-size:19px}.legend-cms-panel small{display:block;color:#b8c6dc;margin-bottom:8px;overflow-wrap:anywhere}
      .legend-cms-control-heading{display:block;margin:7px 0 1px;color:#e6c77e;font-size:11px;letter-spacing:.06em;text-transform:uppercase}
      .legend-cms-group{display:grid;gap:5px;margin:6px 0}.legend-cms-group label{font-size:12px;font-weight:800;color:#e2d5b8}
      .legend-cms-row{display:grid;grid-template-columns:1fr 1fr;gap:8px}
      .legend-cms-theme{display:grid;grid-template-columns:repeat(2,1fr);gap:8px}
      .legend-cms-theme label{font-size:11px;font-weight:800}.legend-cms-theme input{width:100%;height:36px;border:0;background:transparent}
      .legend-cms-favicon-preview{display:block;width:64px;height:64px;object-fit:contain;border-radius:12px;background:#fff;padding:6px;border:1px solid #50617e}.legend-cms-favicon button{width:100%;padding:10px 12px;border:1px solid #50617e;border-radius:10px;background:#142c50;color:#fff;text-align:center}
      .legend-cms-panel button{cursor:pointer}.legend-cms-inline-help{margin:5px 0 8px;padding:8px 10px;border:1px solid #344766;border-radius:10px;background:#10284a;color:#e7eef8}.legend-cms-menu{display:grid;gap:5px}.legend-cms-menu button,.legend-cms-panel section>button{padding:9px 10px;border:1px solid #50617e;border-radius:12px;background:#142c50;color:#fff;text-align:left}.legend-cms-panel input,.legend-cms-panel textarea,.legend-cms-panel select{width:100%;min-width:0;max-width:100%;color:#f7f6f2;background:#142c50;border:1px solid #50617e;border-radius:8px;padding:8px}.legend-cms-panel :focus-visible{outline:2px solid #f0cf78;outline-offset:3px}
      .legend-cms-panel input[type=checkbox]{width:auto}.legend-cms-panel input[type=color]{min-height:40px;padding:4px}.legend-cms-panel button:disabled{opacity:.45;cursor:default}
      .legend-cms-navigation{margin:0 0 10px}.legend-cms-tabs{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:6px}.legend-cms-tabs button{min-height:40px;padding:8px 4px;border:1px solid #344766;border-radius:8px;background:transparent;color:#c9d5e7;font:600 12px/1.3 Inter,system-ui,sans-serif}.legend-cms-tabs button[aria-pressed=true]{background:#e6c77e;color:#10213e;border-color:#e6c77e}
      #legend-cms-status{flex-basis:100%;font-size:12px;color:#c9d5e7;order:1}.legend-cms-panel p{font-size:13px;line-height:1.6;color:#b8c6dc}.legend-cms-layer-list{display:grid;gap:6px}.legend-cms-layer{display:flex;gap:4px;min-width:0}.legend-cms-layer button{min-width:0;padding:10px;border:1px solid #344766;background:#142c50;border-radius:8px;color:#f7f6f2;text-align:left;font-size:12px;overflow-wrap:anywhere}.legend-cms-layer button:first-child{flex:1}.legend-cms-layer button[aria-pressed=true]{border-color:#e6c77e}.legend-cms-section-layer{align-items:stretch}.legend-cms-layer-drag{display:grid;place-items:center;width:28px;flex:0 0 28px;border:1px solid #344766;border-radius:8px;color:#d4ad45;cursor:grab;user-select:none}.legend-cms-layer-dragging{opacity:.55}.legend-cms-layer-drop{outline:1px solid #d4ad45;outline-offset:2px}.legend-cms-search-preview{padding:16px;border:1px solid #344766;border-radius:12px;overflow-wrap:anywhere}.legend-cms-search-preview strong{color:#e6c77e}
      .legend-cms-media-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:10px}.legend-cms-media-card{display:grid;gap:7px;min-width:0;padding:10px;border:1px solid #344766;border-radius:12px;background:#10284a}.legend-cms-media-card img,.legend-cms-media-card video{display:block;width:100%;aspect-ratio:4/3;object-fit:cover;border-radius:8px;background:#07152d}.legend-cms-media-card strong,.legend-cms-media-card small{overflow-wrap:anywhere}.legend-cms-media-card button{padding:9px;border:1px solid #50617e;border-radius:8px;background:#142c50;color:#fff}
      .legend-cms-component-list{display:grid;gap:8px;margin-top:12px}.legend-cms-component-row{display:grid;grid-template-columns:minmax(0,1fr) repeat(3,auto);gap:7px;align-items:start;padding:10px;border:1px solid #344766;border-radius:10px;background:#10284a}.legend-cms-component-row>div{min-width:0}.legend-cms-component-row strong,.legend-cms-component-row small{display:block;overflow-wrap:anywhere}.legend-cms-component-row button{padding:7px 9px}.cms-reusable-instance{min-width:0}.legend-cms-reusable-missing{padding:12px;border:1px dashed #c98e8e;border-radius:8px;background:#2b1717;color:#f6dede}
      .legend-cms-collaboration-roster,.legend-cms-collaboration-comments{display:grid;gap:8px;margin:10px 0 16px}.legend-cms-collaborator,.legend-cms-comment{display:grid;gap:6px;padding:10px 12px;border:1px solid #344766;border-radius:10px;background:#10284a}.legend-cms-collaborator small,.legend-cms-comment small{margin:0}.legend-cms-comment[data-depth="1"]{margin-left:18px;border-left:3px solid #d4ad45}.legend-cms-comment-head{display:flex;gap:8px;justify-content:space-between;align-items:center}.legend-cms-comment-head span{text-transform:capitalize;font-size:11px;color:#d4ad45}
      .legend-cms-signal-presets{display:grid;grid-template-columns:1fr;gap:7px;margin:10px 0 16px}.legend-cms-signal-presets>div{padding:10px 12px;border:1px solid #3e765d;border-radius:10px;background:#0d2b25;color:#d8f4e3;font-size:12px}
      .legend-cms-signal-diagnostics{display:grid;gap:8px;margin:10px 0 14px;padding:10px 12px;border:1px solid #344766;border-radius:10px;background:#0d213e}.legend-cms-signal-diagnostic{margin:0!important;padding:8px 10px;border-radius:8px}.legend-cms-signal-ok{border:1px solid #3e765d;background:#0d2b25;color:#d8f4e3!important}.legend-cms-signal-error{border:1px solid #a95858;background:#35191c;color:#ffdede!important}.legend-cms-signal-history{padding:7px 9px;border-left:3px solid #50617e;font-size:12px;color:#dce6f4;overflow-wrap:anywhere}
      .legend-cms-ai-summary{padding:10px 12px;border:1px solid #d4ad45;border-radius:10px;background:#10284a;color:#f7f6f2}.legend-cms-ai-operation{margin:7px 0;padding:9px 11px;border-left:3px solid #d4ad45;background:#0d213e;color:#dce6f4;font-size:12px;overflow-wrap:anywhere}.legend-cms-protection-warning{white-space:pre-wrap;margin:10px 0;padding:12px 14px;border:1px solid #ff6b6b;border-left:5px solid #ff4d4d;border-radius:10px;background:#35191c;color:#ff8f8f!important;font-weight:800;line-height:1.45;overflow-wrap:anywhere}
      .legend-cms-device-switch{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:6px;margin:0 0 5px}.legend-cms-device-switch button{min-height:36px;font-weight:850}.legend-cms-device-switch button[aria-pressed="true"]{border-color:#d4ad45;background:#d4ad45;color:#081a3a}.legend-cms-device-note{display:block;margin:0 0 8px;color:#b8c5d8}.legend-cms-primary-tabs{grid-template-columns:repeat(5,minmax(0,1fr))}.legend-cms-primary-tabs button{font-weight:800}.legend-cms-agent-contract{min-width:0;max-width:100%;overflow:hidden;padding:12px 14px;border:1px solid #d4ad45;border-radius:12px;background:#10284a;color:#f7f6f2}.legend-cms-agent-contract strong{display:block;margin-bottom:8px}.legend-cms-agent-contract pre{display:block;width:100%;max-width:100%;min-width:0;margin:0;white-space:pre-wrap;overflow-wrap:anywhere;word-break:break-word;font:500 12px/1.55 ui-monospace,SFMono-Regular,Menlo,Monaco,Consolas,monospace;color:inherit}.legend-cms-agent-contract ul{margin:8px 0 0;padding-left:20px;display:grid;gap:6px}
      .legend-cms-source-key{display:flex;flex-wrap:wrap;gap:5px;margin:8px 0}.legend-cms-source-key span{padding:4px 7px;border:1px solid currentColor;border-radius:999px;background:#07162b;font:800 9px/1.2 Inter,system-ui,sans-serif;letter-spacing:.025em}.legend-cms-source-key [data-tone="content"],.legend-cms-source-content{color:#78e2a7}.legend-cms-source-key [data-tone="style"],.legend-cms-source-style{color:#7fb5ff}.legend-cms-source-key [data-tone="color"],.legend-cms-source-color{color:#ff8fa8}.legend-cms-source-key [data-tone="size"],.legend-cms-source-size{color:#ffb86b}.legend-cms-source-key [data-tone="layout"],.legend-cms-source-layout{color:#6fdce8}.legend-cms-source-key [data-tone="media"],.legend-cms-source-media{color:#c4a7ff}.legend-cms-source-key [data-tone="behavior"],.legend-cms-source-behavior{color:#d9a6ff}.legend-cms-source-key [data-tone="structure"],.legend-cms-source-structure{color:#9bd67d}.legend-cms-source-key [data-tone="protected"],.legend-cms-source-protected{color:#f0cf78}.legend-cms-source-default{color:#dce6f4}
      .legend-cms-source-editor{position:relative;width:100%;min-height:52vh;border:1px solid #3f5271;border-radius:10px;background:#07162b;overflow:hidden}.legend-cms-source-highlight,.legend-cms-site-source{width:100%;min-height:52vh;margin:0;padding:14px;font:500 13px/1.6 ui-monospace,SFMono-Regular,Menlo,Monaco,Consolas,monospace;tab-size:2;white-space:pre-wrap;overflow:auto;overflow-wrap:anywhere;word-break:normal}.legend-cms-source-highlight{position:absolute;inset:0;pointer-events:none;background:#07162b}.legend-cms-source-highlight span{display:inline}.legend-cms-site-source{position:relative;z-index:1;resize:vertical;border:0!important;border-radius:0!important;background:#07162b!important;color:#dce6f4!important;-webkit-text-fill-color:currentColor;caret-color:#fff;line-height:1.6}.legend-cms-site-source::selection{background:#4f77aa66}.legend-cms-site-source:focus{outline:2px solid #d4ad45;outline-offset:-2px}.legend-cms-site-source[readonly]{cursor:default;caret-color:transparent;background:transparent!important;color:transparent!important;-webkit-text-fill-color:transparent}.legend-cms-source-editor:has(.legend-cms-site-source[readonly]){border-color:#344766}.legend-cms-source-editor:has(.legend-cms-site-source:not([readonly])){border-color:#d4ad45;box-shadow:inset 0 0 0 1px color-mix(in srgb,#d4ad45 22%,transparent)}.legend-cms-source-editor:has(.legend-cms-site-source:not([readonly])) .legend-cms-source-highlight{display:none}
      [data-cms-view="publish"]{gap:12px}[data-cms-view="advanced"] .legend-cms-menu{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:8px}
      .legend-cms-motion-row{display:grid;gap:8px;margin:10px 0;padding:10px 12px;border:1px solid #344766;border-radius:10px;background:#10284a}.legend-cms-motion-row .legend-cms-group{margin:4px 0}.legend-cms-motion-row>.legend-cms-row{align-items:end}
      .legend-cms-quality-list{display:grid;gap:8px;margin:10px 0 18px}.legend-cms-quality-item{display:grid;grid-template-columns:auto minmax(0,1fr);gap:9px;align-items:start;padding:10px 12px;border:1px solid #344766;border-radius:10px;background:#10284a}.legend-cms-quality-item strong{font-size:10px;letter-spacing:.08em;color:#e6c77e}.legend-cms-quality-item span{font-size:12px;line-height:1.45;color:#f7f6f2}.legend-cms-quality-error{border-color:#e6a6a6}.legend-cms-quality-warning{border-color:#e6c77e}.legend-cms-quality-ok{padding:10px 12px;border:1px solid #3e765d;border-radius:10px;color:#d8f4e3;background:#0d2b25}
      .legend-cms-image,.legend-legacy-migration-image{display:block;margin-left:auto;margin-right:auto;height:auto}
      @media(max-width:800px){
        html{max-width:100%;overflow-x:hidden}
        body.legend-cms-editing{width:100%;max-width:100%;overflow:hidden}
        .legend-cms-preview{width:100%;max-width:100%;height:100dvh;overflow-y:auto;overflow-x:hidden;overscroll-behavior-x:none;touch-action:pan-y pinch-zoom;padding-top:0}
        .legend-cms-preview>*:not(.legend-cms-grid-overlay):not(.legend-cms-selection-frame){max-width:100%;min-width:0}
        .legend-cms-panel{
          --legend-cms-sheet-compact:min(46dvh,430px);
          --legend-cms-sheet-expanded:min(88dvh,calc(100dvh - 12px));
          top:0;right:0;bottom:auto;width:100%;max-width:100%;min-width:0;
          height:var(--legend-cms-sheet-height,var(--legend-cms-sheet-compact));max-height:none;
          overflow-y:auto;overflow-x:hidden;
          overscroll-behavior:contain;-webkit-overflow-scrolling:touch;
          border:0;border-bottom:1px solid #d4ad45;border-radius:0 0 14px 14px;
          padding:max(6px,env(safe-area-inset-top)) 10px 10px;
          box-shadow:0 14px 32px #0007;
          transition:height .2s ease;
        }
        body.legend-cms-panel-expanded .legend-cms-panel{--legend-cms-sheet-height:var(--legend-cms-sheet-expanded)}
        body.legend-cms-sheet-dragging .legend-cms-panel{transition:none}
        .legend-cms-panel>*{min-width:0;max-width:100%}
        .legend-cms-sheet-handle{
          position:sticky;top:0;z-index:8;display:grid;place-items:center;
          width:72px;min-height:22px;margin:0 auto 2px;padding:5px 0;border:0!important;
          background:transparent!important;color:#aab8cf!important;touch-action:none;cursor:ns-resize
        }
        .legend-cms-sheet-handle::before{content:"";display:block;width:38px;height:4px;border-radius:999px;background:currentColor}
        .legend-cms-panel-toggle{
          top:max(6px,env(safe-area-inset-top));right:8px;
          width:34px;height:34px;min-width:34px;min-height:34px;padding:0;max-width:none
        }
        .legend-cms-panel h2{font-size:16px;margin:0 74px 2px 0}
        .legend-cms-panel>small{margin:0 74px 6px 0;font-size:11px}
        .legend-cms-bar{
          display:grid;grid-template-columns:repeat(5,minmax(0,1fr));
          position:sticky;top:calc(-1 * max(6px,env(safe-area-inset-top)));
          z-index:5;gap:4px;padding:5px 0 6px;margin:0 0 7px;
          background:#081a3af7;border-bottom:1px solid #344766;
        }
        .legend-cms-bar button{
          width:100%;min-width:0;min-height:32px;padding:5px 3px;border-radius:7px;
          font-size:11px;line-height:1.05;white-space:normal
        }
        .legend-cms-bar #legend-cms-status{
          grid-column:1/-1;min-height:0;margin:0;padding:1px 3px;
          font-size:10px;line-height:1.2
        }
        .legend-cms-primary-tabs{
          display:flex;grid-template-columns:none;gap:5px;overflow-x:auto;
          margin:0 0 7px;padding:0 0 2px;scrollbar-width:none
        }
        .legend-cms-primary-tabs::-webkit-scrollbar{display:none}
        .legend-cms-primary-tabs button{
          flex:0 0 auto;min-height:32px;padding:6px 9px;font-size:11px
        }
        .legend-cms-menu{grid-template-columns:repeat(3,minmax(0,1fr));gap:5px}
        .legend-cms-menu button,.legend-cms-panel section>button{
          min-height:34px;padding:7px 6px;border-radius:8px;font-size:11px;
          line-height:1.15;text-align:center
        }
        .legend-cms-row{grid-template-columns:repeat(2,minmax(0,1fr));gap:6px}
        .legend-cms-theme{grid-template-columns:repeat(2,minmax(0,1fr));gap:6px}
        .legend-cms-group{gap:5px;margin:7px 0}
        .legend-cms-inline-help{margin:5px 0 8px;padding:7px 8px;font-size:11px}
        .legend-cms-agent-contract{padding:8px 9px;border-radius:9px}
        .legend-cms-agent-contract pre{max-height:26dvh;overflow:auto;overscroll-behavior:contain;font-size:10px;line-height:1.4}
        .legend-cms-site-source{min-height:28dvh;font-size:10px;padding:9px}
        .legend-cms-protection-warning{margin:6px 0;padding:8px 9px;font-size:10px}
      }
    `;
    document.head.appendChild(style);
  }

  function buildEditor() {
    if (legacyMigration)
      throw new Error('Website Studio cannot open writable controls until canonical materialization completes.');
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
      <button id="legend-cms-sheet-handle" class="legend-cms-sheet-handle" type="button" aria-label="Expand Website Studio controls"></button>
      <h2 id="legend-cms-heading">Website studio</h2>
      <small id="legend-cms-selected-label">Select content on the page</small>
      <div id="legend-cms-inline-help" class="legend-cms-inline-help" hidden><span>Single click selects and opens Content. Use the gold Move control to position. Resize only from the selected border edges or corners. Double-click text, or choose Edit text, to type.</span><button id="legend-cms-edit-text" type="button">Edit text</button></div>
      <div id="legend-cms-code-group" class="legend-cms-group" hidden>
        <button id="legend-cms-edit-code" type="button">Edit code in modal</button>
        <small>Custom HTML, CSS, and browser JavaScript are previewed inside a sandboxed block. Resize the block directly on the page.</small>
      </div>
      <div id="legend-cms-image-group" class="legend-cms-group" hidden>
        <label for="legend-cms-image">Replace image</label>
        <input id="legend-cms-image" type="file" accept="image/*,.heic,.heif,.avif">
      </div>
      <strong class="legend-cms-control-heading">Typography</strong>
      <div class="legend-cms-row">
        <label class="legend-cms-group">Font family<input data-style-key="fontFamily" type="text" list="legend-cms-font-options" maxlength="160" placeholder="Inherited / system font"></label>
        <label class="legend-cms-group">Font size px<input data-style-key="fontSize" type="number" min="1" step="any" placeholder="Inherited"></label>
      </div>
      <datalist id="legend-cms-font-options"><option value="Inter"><option value="Arial"><option value="Helvetica"><option value="Georgia"><option value="Times New Roman"><option value="Verdana"><option value="Trebuchet MS"><option value="Courier New"><option value="system-ui"></datalist>
      <div class="legend-cms-row">
        <label class="legend-cms-group">Weight / thickness<select data-style-key="fontWeight"><option value="">Inherited</option><option>100</option><option>200</option><option>300</option><option>400</option><option>500</option><option>600</option><option>700</option><option>800</option><option>900</option></select></label>
        <label class="legend-cms-group">Line height<input data-style-key="lineHeight" type="number" min="0.1" step="any" placeholder="Inherited"></label>
      </div>
      <div class="legend-cms-row">
        <label class="legend-cms-group">Letter spacing px<input data-style-key="letterSpacing" type="number" step="any" placeholder="Inherited"></label>
        <label class="legend-cms-group">Text color<input data-style-key="color" type="text" maxlength="120" placeholder="#ffffff or inherited"></label>
      </div>
      <div class="legend-cms-row">
        <label class="legend-cms-group">Text transform<select data-style-key="textTransform"><option value="">Inherited</option><option value="none">None</option><option value="uppercase">Uppercase</option><option value="lowercase">Lowercase</option><option value="capitalize">Capitalize</option></select></label>
        <label class="legend-cms-group">Decoration<select data-style-key="textDecoration"><option value="">Inherited</option><option value="none">None</option><option value="underline">Underline</option><option value="line-through">Line through</option><option value="overline">Overline</option></select></label>
      </div>
      <strong class="legend-cms-control-heading">Size & position</strong>
      <div class="legend-cms-row">
        <div class="legend-cms-group"><label for="legend-cms-scale">Text scale</label><input id="legend-cms-scale" type="number" min="0.05" step="any" value="1"></div>
        <div class="legend-cms-group"><label for="legend-cms-width">Width %</label><input id="legend-cms-width" type="number" min="0" max="100" step="any" value="100"></div>
      </div>
      <div class="legend-cms-row">
        <div class="legend-cms-group"><label for="legend-cms-height">Height px</label><input id="legend-cms-height" type="number" min="0" step="any" placeholder="Auto"></div>
        <div class="legend-cms-group"><label for="legend-cms-align">Alignment</label><select id="legend-cms-align"><option value="">Default</option><option value="left">Left</option><option value="center">Center</option><option value="right">Right</option><option value="start">Start</option><option value="end">End</option><option value="justify">Justify</option></select></div>
      </div>
      <strong class="legend-cms-control-heading">Spacing</strong>
      <div class="legend-cms-row">
        <div class="legend-cms-group"><label for="legend-cms-padding-top">Padding top</label><input id="legend-cms-padding-top" type="number" min="0" step="any" value="0"></div>
        <div class="legend-cms-group"><label for="legend-cms-padding-bottom">Padding bottom</label><input id="legend-cms-padding-bottom" type="number" min="0" step="any" value="0"></div>
      </div>
      <div class="legend-cms-row">
        <label class="legend-cms-group">Padding left<input data-style-key="paddingLeft" type="number" min="0" step="any"></label>
        <label class="legend-cms-group">Padding right<input data-style-key="paddingRight" type="number" min="0" step="any"></label>
      </div>
      <div class="legend-cms-row">
        <label class="legend-cms-group">Margin top<input data-style-key="marginTop" type="number" step="any"></label>
        <label class="legend-cms-group">Margin bottom<input data-style-key="marginBottom" type="number" step="any"></label>
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
    panelToggle.setAttribute('aria-controls', 'legend-cms-heading');

    const panelToggleIcon = hidden => hidden
      ? '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M5 19V5h14v14H5Z"/><path d="M9 12h6"/><path d="m12 9 3 3-3 3"/></svg>'
      : '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M5 19V5h14v14H5Z"/><path d="M9 12h6"/><path d="m12 15-3-3 3-3"/></svg>';
    const syncPanelToggle = () => {
      const hidden=document.body.classList.contains('legend-cms-panel-hidden');
      panelToggle.innerHTML=panelToggleIcon(hidden);
      panelToggle.setAttribute('aria-label',hidden ? 'Open Website Studio controls' : 'Hide Website Studio controls');
      panelToggle.setAttribute('title',hidden ? 'Open controls' : 'Hide controls');
      panelToggle.setAttribute('aria-expanded',hidden ? 'false' : 'true');
    };
    const refreshPanelCanvas = () => {
      const refresh = () => { refreshScaledElements(); updateDirectCanvasUi(); };
      if (typeof requestAnimationFrame === 'function') requestAnimationFrame(refresh);
      else refresh();
    };
    panelToggle.addEventListener('click', () => {
      document.body.classList.toggle('legend-cms-panel-hidden');
      if(document.body.classList.contains('legend-cms-panel-hidden'))
        document.body.classList.remove('legend-cms-panel-expanded');
      syncPanelToggle();
      refreshPanelCanvas();
    });
    syncPanelToggle();
    document.body.appendChild(panelToggle);

    const sheetHandle=panel.querySelector('#legend-cms-sheet-handle');
    const mobileSheet=()=>window.matchMedia?.('(max-width: 800px)').matches === true;
    const syncSheetHandle=()=>{
      if(!sheetHandle) return;
      const expanded=document.body.classList.contains('legend-cms-panel-expanded');
      sheetHandle.setAttribute('aria-label',expanded ? 'Collapse Website Studio controls' : 'Expand Website Studio controls');
      sheetHandle.setAttribute('aria-expanded',expanded ? 'true' : 'false');
    };
    const setSheetExpanded=expanded=>{
      document.body.classList.toggle('legend-cms-panel-expanded',!!expanded);
      panel.style.removeProperty('--legend-cms-sheet-height');
      syncSheetHandle();
      refreshPanelCanvas();
    };
    let sheetDrag=null;
    sheetHandle?.addEventListener('click',()=>{ if(mobileSheet()) setSheetExpanded(!document.body.classList.contains('legend-cms-panel-expanded')); });
    sheetHandle?.addEventListener('pointerdown',event=>{
      if(!mobileSheet() || event.button!==0) return;
      sheetDrag={startY:event.clientY,startHeight:panel.getBoundingClientRect().height};
      document.body.classList.add('legend-cms-sheet-dragging');
      sheetHandle.setPointerCapture?.(event.pointerId);
      event.preventDefault();
    });
    sheetHandle?.addEventListener('pointermove',event=>{
      if(!sheetDrag) return;
      const viewport=Math.max(320,Number(window.innerHeight)||document.documentElement.clientHeight||800);
      const compact=Math.min(viewport*.46,430);
      const expanded=Math.min(viewport*.88,viewport-12);
      const height=Math.max(compact,Math.min(expanded,sheetDrag.startHeight+(event.clientY-sheetDrag.startY)));
      panel.style.setProperty('--legend-cms-sheet-height',height+'px');
      event.preventDefault();
    });
    const finishSheetDrag=event=>{
      if(!sheetDrag) return;
      const viewport=Math.max(320,Number(window.innerHeight)||document.documentElement.clientHeight||800);
      const compact=Math.min(viewport*.46,430);
      const expanded=Math.min(viewport*.88,viewport-12);
      const height=panel.getBoundingClientRect().height;
      const expand=height>(compact+expanded)/2;
      sheetDrag=null;
      document.body.classList.remove('legend-cms-sheet-dragging');
      sheetHandle?.releasePointerCapture?.(event.pointerId);
      setSheetExpanded(expand);
    };
    sheetHandle?.addEventListener('pointerup',finishSheetDrag);
    sheetHandle?.addEventListener('pointercancel',finishSheetDrag);
    syncSheetHandle();
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
      <input id="legend-cms-image-upload" type="file" accept="image/*,.heic,.heif,.avif" hidden>
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
      const alreadySelected = target === selected;
      if (!alreadySelected) setSelected(target,{openContent:true});
      else if (inlineEditNode !== target && event.detail === 1) showPanel('content');
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

    document.getElementById('legend-cms-image')?.addEventListener('change', async e => {
      const file = e.target.files?.[0];
      const imageTarget = selected;
      if (!imageTarget?.dataset?.cmsCompositionId || !(imageTarget instanceof HTMLImageElement)) return;
      const asset=await uploadImageAsset(file);
      e.target.value='';
      if(!asset || selected!==imageTarget) return;
      checkpoint();
      const node=selectedWebsiteModel();
      if(!node) return;
      node.mediaAssetId=asset.id;
      delete node.mediaUrl;
      imageTarget.src=mediaUrl(asset.url);
      markDirty();
    });

    document.getElementById('legend-cms-favicon')?.addEventListener('change', async e => {
      const asset=await uploadImageAsset(e.target.files?.[0]);
      e.target.value='';
      if(!asset) return;
      checkpoint();
      documentState.faviconImageDataUrl=asset.url;
      applyFavicon(asset.url);
      syncFaviconControls();
      markDirty();
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
    document.getElementById('legend-cms-image-upload')?.addEventListener('change', async e => { await addImage(e.target.files?.[0]); e.target.value=''; });
    document.getElementById('legend-cms-up')?.addEventListener('click', () => moveSelectedSection(-1));
    document.getElementById('legend-cms-down')?.addEventListener('click', () => moveSelectedSection(1));
    document.getElementById('legend-cms-remove')?.addEventListener('click', () => {
      if (!selected || isSharedShellElement(selected)) return;
      const current=selectedWebsiteModel(false);
      if (current && (current.systemKey || current.systemBinding ||
          (Array.isArray(current.signals) && current.signals.length>0) ||
          Object.values(current.fieldSignals || {}).some(bindings=>Array.isArray(bindings) && bindings.length>0) ||
          current.type === 'form')) return;
      const serviceCard=businessServiceCardFor(selected);
      if(serviceCard?.dataset?.cmsCompositionId) setSelected(serviceCard);
      removeSelected();
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
      if (!response.ok) {
        if ((response.status===401 || response.status===403) &&
            showEditorAuthorizationRecovery('This Website Studio authorization is missing or expired.')) return;
        throw new Error('This edit session has expired.');
      }
      const payload = await response.json();
      if (payload.siteKey && payload.siteKey !== SITE_KEY) {
        if (showEditorAuthorizationRecovery('This Website Studio authorization belongs to a different website scope.')) return;
        throw new Error('This edit session belongs to a different website. Open it from your profile.');
      }
      bindBusiness(payload);
      managementPayload = payload;
      storeContext = payload.store || null;
      legacyMigration = payload.legacyMigration || null;
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
      persistedDocumentState=legacyMigration ? null : cloneCanonicalValue(documentState);

      if(materializeMode){
        const snapshot=currentMaterializedPage();
        window.parent?.postMessage({type:'legend-site-materialized-page',...snapshot},location.origin);
        document.documentElement.hidden=false;
        return;
      }

      if(legacyMigration && payload.capabilities?.compositionV3===true){
        await materializeCanonicalSite();
      }

      if(!materializeMode && templateRepairPending && !legacyMigration){
        const repaired=await save(false);
        if(!repaired) throw new Error(document.getElementById('legend-cms-status')?.textContent || 'Protected form presentation could not be repaired safely. Publishing remains blocked.');
        templateRepairPending=false;
      }

      preservePreviewNavigation();
      signalCatalog = Array.isArray(payload.signalCatalog?.events) && Array.isArray(payload.signalCatalog?.matchingFields)
        ? payload.signalCatalog : null;
      buildEditor();
      installCreativeAgentWorkspaceApi();
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

  window.addEventListener('storage', event => {
    if (!storeContext?.businessKey) return;
    const key=`legendCommerceCart:${String(storeContext.businessKey).toLowerCase()}`;
    if (!event.key || event.key === key) updateStoreCartCount();
  });
  window.addEventListener('parfait-cart-updated', updateStoreCartCount);

  if (renderInput) {
    bindBusiness(renderInput);
    storeContext = renderInput.store || null;
    legacyMigration = renderInput.legacyMigration || null;
    prepareDom();
    injectContentStyles();
    applyDocument(renderInput.document || {});
    document.documentElement.hidden = false;
    window.LEGEND_PUBLIC_CMS_RENDER_COMPLETE = true;
    if (!renderInput.server) {
      if (!editorMode) void startPublicRuntime();
      window.addEventListener('resize', refreshResponsiveComposition);
      if (document.fonts?.ready) document.fonts.ready.then(refreshScaledElements);
    }
    return;
  }

  if(editorMode && SITE_KEY==='business') {
    window.addEventListener('popstate',()=>{
      const current=new URL(location.href);
      const route=normalizePageRoute(current.searchParams.get('cmsPage') || '/');
      if(route && documentState.pages?.[route] && route!==currentPageRoute())
        void navigateToEditorPage(route,{replaceHistory:true});
    });
  }

  document.addEventListener('DOMContentLoaded', async () => {
    injectContentStyles();
    if (editorMode || materializeMode) await loadEditor();
    else {
      try { await loadPublic(); await startPublicRuntime(); }
      catch (error) { unavailable(error); }
    }
    window.addEventListener('resize', refreshResponsiveComposition);
    if (document.fonts?.ready) document.fonts.ready.then(refreshScaledElements);
  }, { once: true });
})();
