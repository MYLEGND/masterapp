(() => {
  if (window.LegendModal) return;
  const api = {};
  let viewportSyncFrame = 0;
  const SHIM_BACKDROP_ATTR = "data-bootstrap-shim-backdrop";

  function dispatchModalEvent(el, name){
    if (!el) return null;
    const evt = new Event(name, { bubbles: true, cancelable: true });
    el.dispatchEvent(evt);
    return evt;
  }

  function managedBackdrops(){
    return Array.from(document.querySelectorAll(`.modal-backdrop.legend-managed-backdrop, .modal-backdrop[${SHIM_BACKDROP_ATTR}="1"]`));
  }

  function ensureShimBackdrop(){
    let backdrop = document.querySelector(`.modal-backdrop[${SHIM_BACKDROP_ATTR}="1"]`);
    if (backdrop) return backdrop;

    backdrop = document.createElement("div");
    backdrop.className = "modal-backdrop fade show legend-managed-backdrop";
    backdrop.setAttribute(SHIM_BACKDROP_ATTR, "1");
    document.body.appendChild(backdrop);
    return backdrop;
  }

  function installBootstrapModalShim(){
    const existingModalApi = window.bootstrap?.Modal;
    if (
      existingModalApi &&
      typeof existingModalApi.getOrCreateInstance === "function" &&
      typeof existingModalApi.getInstance === "function"
    ){
      return;
    }

    class ModalShim {
      constructor(el){
        this._element = el;
        this._visible = el?.classList.contains("show") || false;
        if (el) el.__legendBootstrapModal = this;
      }

      show(){
        const el = this._element;
        if (!el || this._visible) return;
        const showEvt = dispatchModalEvent(el, "show.bs.modal");
        if (showEvt?.defaultPrevented) return;

        ensureShimBackdrop();
        document.body.classList.add("modal-open");
        document.body.classList.add("legend-bootstrap-modal-open");
        el.style.display = "block";
        el.removeAttribute("aria-hidden");
        el.setAttribute("aria-modal", "true");
        if (!el.getAttribute("role")) el.setAttribute("role", "dialog");
        el.classList.add("show");
        this._visible = true;
        dispatchModalEvent(el, "shown.bs.modal");
      }

      hide(){
        const el = this._element;
        if (!el || !this._visible) return;
        const hideEvt = dispatchModalEvent(el, "hide.bs.modal");
        if (hideEvt?.defaultPrevented) return;

        el.classList.remove("show");
        el.style.display = "none";
        el.setAttribute("aria-hidden", "true");
        el.removeAttribute("aria-modal");
        this._visible = false;

        if (!document.querySelector(".modal.show")){
          managedBackdrops().forEach(node => node.remove());
          document.body.classList.remove("modal-open");
          document.body.classList.remove("legend-bootstrap-modal-open");
          document.body.style.removeProperty("padding-right");
        }

        dispatchModalEvent(el, "hidden.bs.modal");
      }

      toggle(){
        if (this._visible) this.hide();
        else this.show();
      }

      dispose(){
        this.hide();
        if (this._element) delete this._element.__legendBootstrapModal;
      }

      static getInstance(el){
        return el?.__legendBootstrapModal || null;
      }

      static getOrCreateInstance(el){
        return ModalShim.getInstance(el) || new ModalShim(el);
      }
    }

    window.bootstrap = window.bootstrap || {};
    window.bootstrap.Modal = ModalShim;

    document.addEventListener("click", (event) => {
      const dismiss = event.target.closest?.('[data-bs-dismiss="modal"]');
      if (!dismiss) return;
      const modalEl = dismiss.closest(".modal");
      if (!modalEl) return;
      event.preventDefault();
      ModalShim.getOrCreateInstance(modalEl).hide();
    });

    document.addEventListener("keydown", (event) => {
      if (event.key !== "Escape") return;
      const openModals = Array.from(document.querySelectorAll(".modal.show"));
      const top = openModals[openModals.length - 1];
      if (!top) return;
      ModalShim.getOrCreateInstance(top).hide();
    });
  }

  function ensureInBody(modalId){
    const all = Array.from(document.querySelectorAll(`[id="${modalId}"]`));
    if (!all.length) return null;
    const latest = all[all.length - 1];
    all.forEach((node) => {
      if (node === latest) return;
      try{
        if (window.bootstrap){
          const inst = bootstrap.Modal.getInstance(node);
          inst?.dispose();
        }
      }catch{}
      node.remove();
    });
    if (latest.parentElement !== document.body){
      document.body.appendChild(latest);
    }
    return latest;
  }

  const surfaces = new WeakSet();
  let header;
  let footer;
  let content;
  let impersonation;

  function writeVariable(name, value){
    const style = document.documentElement.style;
    if (style.getPropertyValue(name) !== value) style.setProperty(name, value);
  }

  function registerDialog(dialog){
    if (!dialog || dialog.nodeType !== 1) return;
    let surface = dialog.matches('.modal') ? dialog : null;
    if (!surface){
      for (let node = dialog; node && node !== document.body; node = node.parentElement){
        if (window.getComputedStyle(node).position === 'fixed'){ surface = node; break; }
      }
    }
    if (!surface) return;
    if (!surfaces.has(surface)){
      surfaces.add(surface);
      surface.setAttribute('data-legend-modal-surface', '');
    }
    if (dialog !== surface) dialog.setAttribute('data-legend-modal-panel', '');
    surface.querySelectorAll(':scope > .modal-dialog, :scope > [class*="-panel"], :scope > [class*="-dialog"], :scope > [class*="-card"], :scope > [class*="-window"]').forEach(panel => {
      panel.setAttribute('data-legend-modal-panel', '');
    });
    surface.querySelectorAll('.modal-content').forEach(panel => panel.setAttribute('data-legend-modal-panel', ''));
  }

  function registerDialogs(node){
    if (node.nodeType !== 1) return;
    if (node.matches('.modal, [role="dialog"], [role="alertdialog"]')) registerDialog(node);
    node.querySelectorAll('.modal, [role="dialog"], [role="alertdialog"]').forEach(registerDialog);
  }

  function syncViewportOffsets(){
    const root = document.documentElement;
    if (!root || !header) return;
    const viewport = window.visualViewport;
    const viewportTop = viewport?.offsetTop || 0;
    const viewportBottom = viewportTop + (viewport?.height || window.innerHeight);
    let top = viewportTop;
    for (const element of [header, impersonation]){
      if (!element) continue;
      const rect = element.getBoundingClientRect();
      if (rect.height > 0 && rect.bottom > viewportTop && rect.top < viewportBottom)
        top = Math.max(top, Math.min(viewportBottom, rect.bottom));
    }
    if (content){
      const rect = content.getBoundingClientRect();
      if (rect.top > top && rect.top < viewportBottom) top = rect.top;
    }
    let bottom = viewportBottom;
    if (footer){
      const rect = footer.getBoundingClientRect();
      if (rect.height > 0 && rect.top < viewportBottom && rect.bottom > viewportTop)
        bottom = Math.max(top, rect.top);
    }
    const nominalMargin = window.matchMedia?.('(max-width: 900px)')?.matches ? 10 : 24;
    const safeMargin = Math.min(nominalMargin, Math.max(0, (bottom - top) / 4));
    const areaStart = top + safeMargin;
    const safeHeight = Math.max(0, bottom - top - 2 * safeMargin);
    const areaEnd = Math.max(0, window.innerHeight - (bottom - safeMargin));
    writeVariable('--legend-modal-safe-margin', `${safeMargin}px`);
    writeVariable('--legend-modal-clearance-top', `${top}px`);
    writeVariable('--legend-modal-area-start', `${areaStart}px`);
    writeVariable('--legend-modal-area-end', `${areaEnd}px`);
    writeVariable('--legend-modal-safe-height', `${safeHeight}px`);
    writeVariable('--legend-modal-safe-center', `${areaStart + safeHeight / 2}px`);
    root.setAttribute('data-legend-modal-region', '');
  }

  function observeContentRegion(){
    header = document.querySelector('body > header');
    footer = document.querySelector('body > footer.footer');
    content = document.querySelector('body > .layout-content');
    impersonation = document.querySelector('body > .impersonation-banner');
    if (!header) return;
    registerDialogs(document.body);
    syncViewportOffsets();
    if (window.ResizeObserver){
      const observer = new ResizeObserver(scheduleViewportOffsets);
      [header, footer, content, impersonation].filter(Boolean).forEach(element => observer.observe(element));
    }
    const mutations = new MutationObserver(records => {
      let changed = false;
      for (const record of records){
        if (record.type === 'childList'){
          record.addedNodes.forEach(node => { registerDialogs(node); });
          if (surfaces.has(record.target)) registerDialog(record.target);
          changed = changed || record.addedNodes.length > 0 || record.removedNodes.length > 0;
        } else if (surfaces.has(record.target)) changed = true;
      }
      if (changed) scheduleViewportOffsets();
    });
    mutations.observe(document.body, { subtree: true, childList: true, attributes: true, attributeFilter: ['class', 'hidden'] });
    document.addEventListener('show.bs.modal', event => {
      registerDialogs(event.target);
      syncViewportOffsets();
    });
  }

  function scheduleViewportOffsets(){
    if (viewportSyncFrame) return;
    viewportSyncFrame = window.requestAnimationFrame(() => {
      viewportSyncFrame = 0;
      syncViewportOffsets();
    });
  }

  function promoteBackdrop(backdropZ){
    if (!backdropZ) return;
    const backdrops = Array.from(document.querySelectorAll(".modal-backdrop.show"));
    const top = backdrops[backdrops.length - 1];
    if (!top) return;
    top.style.zIndex = String(backdropZ);
    top.classList.add("legend-managed-backdrop");
  }

  function reconcile(){
    if (document.querySelector(".modal.show")) return;
    document.body.classList.remove("legend-bootstrap-modal-open");
    document.body.classList.remove("modal-open");
    document.body.style.removeProperty("padding-right");
    managedBackdrops().forEach((node) => node.remove());
  }

  function bind(modalId, options = {}){
    const modalEl = ensureInBody(modalId) || document.getElementById(modalId);
    if (!modalEl || modalEl.dataset.legendModalBound === "1") return modalEl;
    modalEl.dataset.legendModalBound = "1";

    const modalZ = Number(options.modalZ || 0);
    const backdropZ = Number(options.backdropZ || 0);
    const onHidden = typeof options.onHidden === "function" ? options.onHidden : null;

    modalEl.addEventListener("show.bs.modal", () => {
      closeLegacyExecutionOverlays();
      if (modalZ > 0) modalEl.style.zIndex = String(modalZ);
      document.body.classList.add("legend-bootstrap-modal-open");
      window.setTimeout(() => promoteBackdrop(backdropZ), 0);
    });

    modalEl.addEventListener("shown.bs.modal", () => {
      if (modalZ > 0) modalEl.style.zIndex = String(modalZ);
      promoteBackdrop(backdropZ);
    });

    modalEl.addEventListener("hidden.bs.modal", () => {
      if (modalZ > 0) modalEl.style.removeProperty("z-index");
      if (onHidden) onHidden();
      reconcile();
    });

    return modalEl;
  }

  function hide(modalId){
    const modalEl = document.getElementById(modalId);
    if (!modalEl || !window.bootstrap) return;
    const inst = bootstrap.Modal.getInstance(modalEl);
    inst?.hide();
  }

  function closeLegacyExecutionOverlays(){
    const legacyBackdrop = document.getElementById("modalBackdrop");
    legacyBackdrop?.classList.remove("open");
    ["colsModal","shortcutsModal","remindersModal","cmdModal","bulkModal","callTaskModal","importModal","performanceModal","myDayModal"]
      .forEach(id => document.getElementById(id)?.classList.remove("open"));
  }

  api.ensureInBody = ensureInBody;
  api.bind = bind;
  api.refreshViewportOffsets = syncViewportOffsets;
  api.reconcile = reconcile;
  api.hide = hide;
  api.closeLegacyExecutionOverlays = closeLegacyExecutionOverlays;

  installBootstrapModalShim();

  // Shared layouts load this owner after their banner and content. Initialize
  // before page scripts can auto-open a dialog, even while DOMContentLoaded waits.
  if (document.querySelector('body > header') || document.readyState !== "loading"){
    observeContentRegion();
  } else {
    document.addEventListener("DOMContentLoaded", observeContentRegion, { once: true });
  }
  window.visualViewport?.addEventListener("resize", scheduleViewportOffsets, { passive: true });
  window.visualViewport?.addEventListener("scroll", scheduleViewportOffsets, { passive: true });
  window.addEventListener("resize", scheduleViewportOffsets, { passive: true });
  window.addEventListener("scroll", scheduleViewportOffsets, { passive: true });

  window.LegendModal = api;
})();
