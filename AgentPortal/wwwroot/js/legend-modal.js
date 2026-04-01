(() => {
  const api = {};

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
    document.querySelectorAll(".modal-backdrop").forEach((node) => node.remove());
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
    ["colsModal","shortcutsModal","remindersModal","cmdModal","bulkModal","callTaskModal","importModal"]
      .forEach(id => document.getElementById(id)?.classList.remove("open"));
  }

  api.ensureInBody = ensureInBody;
  api.bind = bind;
  api.reconcile = reconcile;
  api.hide = hide;
  api.closeLegacyExecutionOverlays = closeLegacyExecutionOverlays;

  window.LegendModal = api;
})();
