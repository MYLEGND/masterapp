(() => {
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

  function syncViewportOffsets(){
    const root = document.documentElement;
    if (!root) return;

    const mobile = !!window.matchMedia?.("(max-width: 900px)")?.matches;
    const safeMargin = mobile ? 10 : 24;
    const header = document.querySelector("header .navbar");
    const headerBottom = header ? Math.max(0, Math.ceil(header.getBoundingClientRect().bottom)) : 0;
    const areaStart = Math.max(safeMargin, headerBottom + safeMargin);
    const safeHeight = Math.max(280, window.innerHeight - areaStart - safeMargin);
    const safeCenter = areaStart + (safeHeight / 2);

    root.style.setProperty("--legend-modal-safe-margin", `${safeMargin}px`);
    root.style.setProperty("--legend-modal-clearance-top", `${headerBottom}px`);
    root.style.setProperty("--legend-modal-area-start", `${areaStart}px`);
    root.style.setProperty("--legend-modal-safe-height", `${safeHeight}px`);
    root.style.setProperty("--legend-modal-safe-center", `${safeCenter}px`);
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

  if (document.readyState === "loading"){
    document.addEventListener("DOMContentLoaded", syncViewportOffsets, { once: true });
  } else {
    syncViewportOffsets();
  }
  window.addEventListener("resize", scheduleViewportOffsets, { passive: true });
  window.addEventListener("scroll", scheduleViewportOffsets, { passive: true });

  window.LegendModal = api;
})();
