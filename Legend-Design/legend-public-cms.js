(() => {
  'use strict';

  const context = window.LEGEND_PUBLIC_CMS_CONTEXT || null;
  if (!context || !context.siteKey || !context.apiBase) return;

  const API_BASE = String(context.apiBase).replace(/\/$/, '');
  const SITE_KEY = String(context.siteKey).toLowerCase();
  const AGENT_SLUG = context.agentSlug || '';
  const pageKey = document.body?.dataset?.pageKey
    || location.pathname.replace(/^\/+|\/+$/g, '').replace(/[^a-z0-9]+/gi, '-')?.toLowerCase()
    || 'home';

  const params = new URLSearchParams(location.search);
  const editorTicket = params.get('legendEdit') || '';
  const editorMode = !!editorTicket;
  let documentState = { version: 1, elements: {}, sectionOrder: {}, extras: [], theme: {} };
  let selected = null;
  let selectedSection = null;
  let dirty = false;

  const lockedSelector = [
    '.brand',
    '.brand-wordmark',
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
    return {
      version: 1,
      elements: input?.elements && typeof input.elements === 'object' ? input.elements : {},
      sectionOrder: input?.sectionOrder && typeof input.sectionOrder === 'object' ? input.sectionOrder : {},
      extras: Array.isArray(input?.extras) ? input.extras : [],
      theme: input?.theme && typeof input.theme === 'object' ? input.theme : {}
    };
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
    if (el.matches(lockedSelector) || el.closest('.brand,[data-cms-locked="true"]')) return false;
    if (el.tagName === 'IMG') return true;
    if (editableTextTags.has(el.tagName)) return true;
    if (editableInteractiveTags.has(el.tagName)) {
      return el.children.length === 0;
    }
    return false;
  }

  function prepareDom() {
    const roots = [
      document.querySelector('main'),
      document.querySelector('.nav'),
      document.querySelector('.site-footer')
    ].filter(Boolean);

    const sections = Array.from(document.querySelectorAll(sectionCandidates));
    sections.forEach((section, index) => {
      if (!section.dataset.cmsSection) {
        section.dataset.cmsSection = `${pageKey}.section.${index + 1}`;
      }
    });

    let counter = 0;
    roots.forEach(root => {
      root.querySelectorAll('h1,h2,h3,h4,h5,p,li,a,button,label,small,strong,span,img').forEach(el => {
        if (!canEditElement(el)) return;
        if (el.tagName !== 'IMG' && el.children.length > 0) return;
        if (!el.dataset.cmsId) {
          counter += 1;
          const semantic = el.dataset.cta || el.getAttribute('href') || el.textContent || el.tagName;
          el.dataset.cmsId = `${pageKey}.${safeId(el.tagName)}.${safeId(semantic).slice(0,50) || counter}.${counter}`;
        }
        el.dataset.cmsEditable = 'true';
        if ((el.tagName === 'A' || el.tagName === 'BUTTON') && !el.dataset.cmsAction) {
          el.dataset.cmsAction = el.dataset.cta || el.getAttribute('href') || 'action';
        }
      });
    });
  }

  function applyTheme(theme) {
    const root = document.documentElement;
    const map = {
      navy: '--navy',
      navyDeep: '--navy-deep',
      gold: '--gold',
      goldStrong: '--gold-strong',
      surface: '--surface'
    };
    Object.entries(map).forEach(([key, cssVar]) => {
      if (theme?.[key]) root.style.setProperty(cssVar, theme[key]);
    });
  }

  function applyStyle(el, style) {
    if (!el || !style) return;
    if (style.textAlign) el.style.textAlign = style.textAlign;
    if (style.fontScale) el.style.fontSize = `${style.fontScale}em`;
    if (style.widthPercent) {
      el.style.width = `${style.widthPercent}%`;
      el.style.maxWidth = `${style.widthPercent}%`;
    }
    if (style.paddingTop != null) el.style.paddingTop = `${style.paddingTop}px`;
    if (style.paddingBottom != null) el.style.paddingBottom = `${style.paddingBottom}px`;
    if (style.objectPosition && el instanceof HTMLImageElement) {
      el.style.objectPosition = style.objectPosition;
    }
  }

  function applyElementOverride(el, override) {
    if (!el || !override) return;
    if (override.hidden === true) el.hidden = true;
    else if (override.hidden === false) el.hidden = false;

    if (el instanceof HTMLImageElement) {
      if (override.imageDataUrl) el.src = override.imageDataUrl;
    } else if (override.text != null) {
      el.textContent = override.text;
    }

    applyStyle(el, override.style);
  }

  function createExtra(extra) {
    const section = document.querySelector(`[data-cms-section="${CSS.escape(extra.sectionId)}"]`);
    if (!section) return null;
    let el;
    if (extra.type === 'image') {
      el = document.createElement('img');
      el.src = extra.imageDataUrl || '';
      el.alt = '';
      el.className = 'cms-extra cms-extra-image';
    } else {
      el = document.createElement('p');
      el.textContent = extra.text || '';
      el.className = 'cms-extra cms-extra-text';
    }
    el.dataset.cmsExtraId = extra.id;
    el.dataset.cmsId = `extra:${extra.id}`;
    el.dataset.cmsEditable = 'true';
    section.appendChild(el);
    applyStyle(el, extra.style);
    return el;
  }

  function applyDocument(doc) {
    documentState = normalizeDocument(doc);
    applyTheme(documentState.theme);

    Object.entries(documentState.elements).forEach(([id, override]) => {
      const el = document.querySelector(`[data-cms-id="${CSS.escape(id)}"]`);
      applyElementOverride(el, override);
    });

    const sections = Array.from(document.querySelectorAll('[data-cms-section]'));
    sections.sort((a,b) => {
      const ai = documentState.sectionOrder[a.dataset.cmsSection] ?? sections.indexOf(a);
      const bi = documentState.sectionOrder[b.dataset.cmsSection] ?? sections.indexOf(b);
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

    document.querySelectorAll('.cms-extra').forEach(x => x.remove());
    documentState.extras.forEach(createExtra);
  }

  async function loadPublic() {
    const url = new URL(`${API_BASE}/api/website-content/public/${encodeURIComponent(SITE_KEY)}`);
    if (AGENT_SLUG) url.searchParams.set('agentSlug', AGENT_SLUG);
    try {
      const response = await fetch(url, { cache: 'no-store' });
      if (!response.ok) return;
      const payload = await response.json();
      applyDocument(payload.document || {});
    } catch {
      // Public content remains fully usable from canonical defaults.
    }
  }

  function currentSectionFor(el) {
    return el?.closest?.('[data-cms-section]') || null;
  }

  function ensureOverride(id) {
    if (!documentState.elements[id]) {
      documentState.elements[id] = { style: {} };
    }
    if (!documentState.elements[id].style) documentState.elements[id].style = {};
    return documentState.elements[id];
  }

  function markDirty() {
    dirty = true;
    const status = document.getElementById('legend-cms-status');
    if (status) status.textContent = 'Unsaved changes';
  }

  function setSelected(el) {
    document.querySelectorAll('.legend-cms-selected').forEach(x => x.classList.remove('legend-cms-selected'));
    selected = el;
    selectedSection = currentSectionFor(el);
    if (selected) selected.classList.add('legend-cms-selected');
    syncEditorControls();
  }

  function selectedOverride() {
    if (!selected?.dataset?.cmsId) return null;
    if (selected.dataset.cmsExtraId) {
      return documentState.extras.find(x => x.id === selected.dataset.cmsExtraId) || null;
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

    if (!selected) {
      if (title) title.textContent = 'Select content on the page';
      if (textGroup) textGroup.hidden = true;
      if (imageGroup) imageGroup.hidden = true;
      return;
    }

    if (title) title.textContent = selected.dataset.cmsId || selected.tagName;
    const isImage = selected instanceof HTMLImageElement;
    if (textGroup) textGroup.hidden = isImage;
    if (imageGroup) imageGroup.hidden = !isImage;

    const ov = selectedOverride() || { style: {} };
    if (text && !isImage) text.value = ov.text ?? selected.textContent ?? '';
    if (scale) scale.value = String(ov.style?.fontScale ?? 1);
    if (width) width.value = String(ov.style?.widthPercent ?? 100);
    if (top) top.value = String(ov.style?.paddingTop ?? 0);
    if (bottom) bottom.value = String(ov.style?.paddingBottom ?? 0);
    if (align) align.value = ov.style?.textAlign ?? '';
    if (hidden) hidden.checked = ov.hidden === true || selected.hidden;
  }

  function updateSelectedFromControls() {
    if (!selected) return;
    const ov = selectedOverride();
    if (!ov) return;
    ov.style ||= {};

    if (!(selected instanceof HTMLImageElement)) {
      const text = document.getElementById('legend-cms-text')?.value ?? '';
      ov.text = text;
      selected.textContent = text;
    }

    const scale = Number(document.getElementById('legend-cms-scale')?.value || 1);
    const width = Number(document.getElementById('legend-cms-width')?.value || 100);
    const top = Number(document.getElementById('legend-cms-padding-top')?.value || 0);
    const bottom = Number(document.getElementById('legend-cms-padding-bottom')?.value || 0);
    const align = document.getElementById('legend-cms-align')?.value || '';
    const hidden = !!document.getElementById('legend-cms-hidden')?.checked;

    ov.style.fontScale = scale;
    ov.style.widthPercent = width;
    ov.style.paddingTop = top;
    ov.style.paddingBottom = bottom;
    ov.style.textAlign = align || null;
    ov.hidden = hidden;

    selected.hidden = hidden;
    applyStyle(selected, ov.style);
    markDirty();
  }

  function readImage(file, callback) {
    if (!file) return;
    if (!/^image\/(jpeg|png|webp)$/i.test(file.type)) {
      alert('Use a JPEG, PNG, or WebP image.');
      return;
    }
    if (file.size > 2_500_000) {
      alert('Use an image smaller than 2.5 MB.');
      return;
    }
    const reader = new FileReader();
    reader.onload = () => callback(String(reader.result || ''));
    reader.readAsDataURL(file);
  }

  function moveSelectedSection(delta) {
    if (!selectedSection) return;
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
        documentState.sectionOrder[section.dataset.cmsSection] = i;
      });
    markDirty();
  }

  function addText() {
    if (!selectedSection) {
      alert('Select content inside the section where you want the new text.');
      return;
    }
    const extra = {
      id: crypto.randomUUID ? crypto.randomUUID() : String(Date.now()),
      sectionId: selectedSection.dataset.cmsSection,
      type: 'text',
      text: 'New text block',
      style: { fontScale: 1, widthPercent: 100, paddingTop: 12, paddingBottom: 12 }
    };
    documentState.extras.push(extra);
    const el = createExtra(extra);
    setSelected(el);
    markDirty();
  }

  function addImage(file) {
    if (!selectedSection) {
      alert('Select content inside the section where you want the new image.');
      return;
    }
    readImage(file, dataUrl => {
      const extra = {
        id: crypto.randomUUID ? crypto.randomUUID() : String(Date.now()),
        sectionId: selectedSection.dataset.cmsSection,
        type: 'image',
        imageDataUrl: dataUrl,
        style: { widthPercent: 70, paddingTop: 16, paddingBottom: 16 }
      };
      documentState.extras.push(extra);
      const el = createExtra(extra);
      setSelected(el);
      markDirty();
    });
  }

  function removeSelected() {
    if (!selected) return;
    if (selected.dataset.cmsExtraId) {
      documentState.extras = documentState.extras.filter(x => x.id !== selected.dataset.cmsExtraId);
      selected.remove();
      setSelected(null);
      markDirty();
      return;
    }
    delete documentState.elements[selected.dataset.cmsId];
    location.reload();
  }

  async function save() {
    const status = document.getElementById('legend-cms-status');
    if (status) status.textContent = 'Saving…';
    try {
      const response = await fetch(`${API_BASE}/api/website-content/manage`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ticket: editorTicket, document: documentState })
      });
      if (!response.ok) throw new Error(`Save failed (${response.status})`);
      const payload = await response.json();
      documentState = normalizeDocument(payload.document || documentState);
      dirty = false;
      if (status) status.textContent = 'Saved live';
    } catch (error) {
      if (status) status.textContent = error?.message || 'Save failed';
    }
  }

  function injectEditorStyles() {
    const style = document.createElement('style');
    style.textContent = `
      .legend-cms-selected{outline:3px solid #f0cf78!important;outline-offset:4px!important}
      [data-cms-editable="true"]{cursor:pointer}
      .legend-cms-editor{position:fixed;z-index:2147483000;font-family:Inter,system-ui,sans-serif}
      .legend-cms-bar{left:18px;right:18px;bottom:max(16px,env(safe-area-inset-bottom));display:flex;align-items:center;gap:10px;padding:10px 12px;background:#081a3af2;color:#fff;border:1px solid #d4ad45;border-radius:18px;box-shadow:0 20px 70px #0007}
      .legend-cms-bar button{min-height:42px;border-radius:999px;padding:8px 14px;border:1px solid #d4ad45;background:#102b62;color:#fff;font-weight:800}
      .legend-cms-bar .primary{background:#d4ad45;color:#081a3a}
      .legend-cms-panel{top:16px;right:16px;width:min(360px,calc(100vw - 32px));max-height:calc(100dvh - 110px);overflow:auto;background:#fff;color:#101a35;border:1px solid #d4ad45;border-radius:20px;padding:18px;box-shadow:0 24px 80px #0006}
      .legend-cms-panel h3{margin:0 0 4px;font-size:19px}.legend-cms-panel small{display:block;color:#667085;margin-bottom:14px;word-break:break-all}
      .legend-cms-group{display:grid;gap:7px;margin:12px 0}.legend-cms-group label{font-size:12px;font-weight:800;color:#344054}
      .legend-cms-group textarea,.legend-cms-group select,.legend-cms-group input[type="number"]{width:100%;border:1px solid #cbd5e1;border-radius:10px;padding:9px}
      .legend-cms-row{display:grid;grid-template-columns:1fr 1fr;gap:8px}
      .legend-cms-theme{display:grid;grid-template-columns:repeat(2,1fr);gap:8px}
      .legend-cms-theme label{font-size:11px;font-weight:800}.legend-cms-theme input{width:100%;height:36px;border:0;background:transparent}
      .cms-extra-image{display:block;margin-left:auto;margin-right:auto;height:auto}
      @media(max-width:720px){.legend-cms-panel{top:auto;bottom:84px;max-height:48dvh}.legend-cms-bar{overflow-x:auto;white-space:nowrap}}
    `;
    document.head.appendChild(style);
  }

  function buildEditor() {
    injectEditorStyles();

    const panel = document.createElement('aside');
    panel.className = 'legend-cms-editor legend-cms-panel';
    panel.innerHTML = `
      <h3>Website Editor</h3>
      <small id="legend-cms-selected-label">Select content on the page</small>
      <div id="legend-cms-text-group" class="legend-cms-group" hidden>
        <label>Content</label>
        <textarea id="legend-cms-text" rows="5"></textarea>
      </div>
      <div id="legend-cms-image-group" class="legend-cms-group" hidden>
        <label>Replace image</label>
        <input id="legend-cms-image" type="file" accept="image/jpeg,image/png,image/webp">
      </div>
      <div class="legend-cms-row">
        <div class="legend-cms-group"><label>Scale</label><input id="legend-cms-scale" type="number" min=".6" max="2.4" step=".05" value="1"></div>
        <div class="legend-cms-group"><label>Width %</label><input id="legend-cms-width" type="number" min="20" max="100" step="1" value="100"></div>
      </div>
      <div class="legend-cms-row">
        <div class="legend-cms-group"><label>Top spacing</label><input id="legend-cms-padding-top" type="number" min="0" max="240" value="0"></div>
        <div class="legend-cms-group"><label>Bottom spacing</label><input id="legend-cms-padding-bottom" type="number" min="0" max="240" value="0"></div>
      </div>
      <div class="legend-cms-group"><label>Alignment</label><select id="legend-cms-align"><option value="">Default</option><option value="left">Left</option><option value="center">Center</option><option value="right">Right</option></select></div>
      <div class="legend-cms-group"><label><input id="legend-cms-hidden" type="checkbox"> Hide selected content</label></div>
      <hr>
      <div class="legend-cms-group"><label>Site colors</label>
        <div class="legend-cms-theme">
          <label>Navy<input data-theme-key="navy" type="color" value="#102b62"></label>
          <label>Deep navy<input data-theme-key="navyDeep" type="color" value="#081a3a"></label>
          <label>Gold<input data-theme-key="gold" type="color" value="#d4ad45"></label>
          <label>Bright gold<input data-theme-key="goldStrong" type="color" value="#f0cf78"></label>
        </div>
      </div>
    `;
    document.body.appendChild(panel);

    const bar = document.createElement('div');
    bar.className = 'legend-cms-editor legend-cms-bar';
    bar.innerHTML = `
      <button class="primary" id="legend-cms-save">Save Live</button>
      <span id="legend-cms-status">Live editor</span>
      <button id="legend-cms-add-text">Add Text</button>
      <button id="legend-cms-add-image">Add Image</button>
      <input id="legend-cms-extra-image" type="file" accept="image/jpeg,image/png,image/webp" hidden>
      <button id="legend-cms-up">Section ↑</button>
      <button id="legend-cms-down">Section ↓</button>
      <button id="legend-cms-remove">Reset / Remove</button>
      <button id="legend-cms-exit">Exit</button>
    `;
    document.body.appendChild(bar);

    document.addEventListener('click', event => {
      const target = event.target.closest('[data-cms-editable="true"]');
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
      readImage(file, dataUrl => {
        const ov = selectedOverride();
        if (!ov) return;
        ov.imageDataUrl = dataUrl;
        selected.src = dataUrl;
        markDirty();
      });
    });

    document.querySelectorAll('[data-theme-key]').forEach(input => {
      const key = input.dataset.themeKey;
      if (documentState.theme?.[key]) input.value = documentState.theme[key];
      input.addEventListener('input', () => {
        documentState.theme[key] = input.value;
        applyTheme(documentState.theme);
        markDirty();
      });
    });

    document.getElementById('legend-cms-save')?.addEventListener('click', save);
    document.getElementById('legend-cms-add-text')?.addEventListener('click', addText);
    document.getElementById('legend-cms-add-image')?.addEventListener('click', () => document.getElementById('legend-cms-extra-image')?.click());
    document.getElementById('legend-cms-extra-image')?.addEventListener('change', e => addImage(e.target.files?.[0]));
    document.getElementById('legend-cms-up')?.addEventListener('click', () => moveSelectedSection(-1));
    document.getElementById('legend-cms-down')?.addEventListener('click', () => moveSelectedSection(1));
    document.getElementById('legend-cms-remove')?.addEventListener('click', removeSelected);
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
      applyDocument(payload.document || {});
      buildEditor();
    } catch (error) {
      console.error('[legend-cms]', error);
      alert(error?.message || 'Unable to open website editor.');
    }
  }

  document.addEventListener('DOMContentLoaded', async () => {
    prepareDom();
    await loadPublic();
    if (editorMode) await loadEditor();
  }, { once: true });
})();
