(() => {
  if (window.LegendMobilePlatform) return;

  const media = window.matchMedia("(max-width: 840px)");
  const root = document.documentElement;
  const tokenUrl = "/design/legend-design.tokens.json";
  let tokens = null;

  const read = (group, key) => tokens?.[group]?.[key];
  const px = value => Number.isFinite(Number(value)) ? `${Number(value)}px` : null;
  const seconds = value => Number.isFinite(Number(value)) ? `${Number(value)}s` : null;

  function setVar(name, value) {
    if (value == null || value === "") return;
    root.style.setProperty(name, value);
  }

  function color(name) {
    const value = read("colors", name);
    return value?.light || value?.dark || null;
  }

  function applyTokens(next) {
    tokens = next;
    const spacing = next?.spacing || {};
    const radii = next?.radii || {};
    const sizes = next?.sizes || {};
    const motion = next?.motion || {};

    setVar("--legend-mobile-page-horizontal", px(spacing.pageHorizontal));
    setVar("--legend-mobile-page-top", px(spacing.pageTop));
    setVar("--legend-mobile-page-bottom", px(spacing.pageBottom));
    setVar("--legend-mobile-micro", px(spacing.micro));
    setVar("--legend-mobile-xs", px(spacing.xs));
    setVar("--legend-mobile-sm", px(spacing.sm));
    setVar("--legend-mobile-md", px(spacing.md));
    setVar("--legend-mobile-card-radius", px(radii.card));
    setVar("--legend-mobile-control-radius", px(radii.control));
    setVar("--legend-mobile-sheet-radius", px(radii.sheet));
    setVar("--legend-mobile-tap-target", px(sizes.minimumTapTarget));
    setVar("--legend-mobile-control-height", px(sizes.controlHeight));
    setVar("--legend-mobile-motion-quick", seconds(motion.quickSeconds));
    setVar("--legend-mobile-motion-standard", seconds(motion.standardSeconds));
    setVar("--legend-mobile-motion-entrance", seconds(motion.entranceSeconds));
    setVar("--legend-mobile-canvas", color("canvas"));
    setVar("--legend-mobile-surface", color("surface"));
    setVar("--legend-mobile-surface-elevated", color("surfaceElevated"));
    setVar("--legend-mobile-midnight", color("midnight"));
    setVar("--legend-mobile-navy", color("navy"));
    setVar("--legend-mobile-gold", color("gold"));
    setVar("--legend-mobile-text-primary", color("textPrimary"));
    setVar("--legend-mobile-text-secondary", color("textSecondary"));
    root.dataset.legendDesignSource = next?.sourceOfTruth || "Legend-Design/legend-design.tokens.json";
    root.dispatchEvent(new CustomEvent("legend:mobiletokens", { detail: { source: root.dataset.legendDesignSource } }));
  }

  async function loadTokens() {
    try {
      const response = await fetch(tokenUrl, { credentials: "same-origin", cache: "force-cache" });
      if (!response.ok) throw new Error(`LEGEND design tokens unavailable: ${response.status}`);
      applyTokens(await response.json());
    } catch (error) {
      console.error("LEGEND mobile presentation could not load the shared design authority.", error);
    }
  }

  function syncViewport() {
    const viewport = window.visualViewport;
    const height = viewport?.height || window.innerHeight;
    setVar("--legend-mobile-viewport-height", `${Math.max(0, height)}px`);
  }

  function isMobile() {
    return media.matches;
  }

  function decorateModal(surface) {
    if (!surface || surface.nodeType !== 1) return;
    if (!isMobile()) {
      surface.removeAttribute("data-legend-mobile-sheet");
      surface.querySelectorAll("[data-legend-mobile-sheet-panel],[data-legend-mobile-sheet-scroll]")
        .forEach(node => {
          node.removeAttribute("data-legend-mobile-sheet-panel");
          node.removeAttribute("data-legend-mobile-sheet-scroll");
        });
      return;
    }

    if (surface.matches(".modal")) {
      surface.setAttribute("data-legend-mobile-sheet", "");
      const panel = surface.querySelector(":scope > .modal-dialog > .modal-content");
      panel?.setAttribute("data-legend-mobile-sheet-panel", "");
      const body = panel?.querySelector(":scope > .modal-body");
      body?.setAttribute("data-legend-mobile-sheet-scroll", "");
      return;
    }

    const semanticDialog = surface.matches("[role='dialog'],[role='alertdialog']")
      ? surface
      : surface.querySelector("[role='dialog'],[role='alertdialog']");
    if (semanticDialog) {
      const nestedPanel = semanticDialog.querySelector(
        ":scope > .modal-content, :scope > [class*='-panel'], :scope > [class*='-dialog'], :scope > [class*='-card'], :scope > [class*='-window']"
      );
      const panel = nestedPanel || semanticDialog;
      surface.setAttribute("data-legend-mobile-sheet", "");
      panel.setAttribute("data-legend-mobile-sheet-panel", "");

      const header = panel.querySelector(":scope > .modal-header, :scope > [class*='-header'], :scope > [class*='-head']");
      const scroll = panel.querySelector(":scope > .modal-body, :scope > [data-dialog-body], :scope > [class*='-body'], :scope > [class*='-content']");
      const footer = panel.querySelector(":scope > .modal-footer, :scope > [class*='-footer'], :scope > [class*='-foot']");
      header?.setAttribute("data-legend-mobile-sheet-header", "");
      scroll?.setAttribute("data-legend-mobile-sheet-scroll", "");
      footer?.setAttribute("data-legend-mobile-sheet-footer", "");

      panel.querySelectorAll(
        "button[class*='close'], [role='button'][class*='close'], button[aria-label*='close' i], button[aria-label*='dismiss' i], [data-bs-dismiss='modal']"
      ).forEach(control => control.setAttribute("data-legend-mobile-sheet-close", ""));
    }
  }

  function decorateExistingDialogs() {
    document.querySelectorAll(".modal,[role='dialog'],[role='alertdialog']").forEach(node => {
      const surface = node.matches(".modal") ? node : (node.closest("[data-legend-modal-surface]") || node);
      decorateModal(surface);
    });
  }

  function syncMode() {
    if (isMobile()) {
      root.setAttribute("data-legend-mobile", "1");
    } else {
      root.removeAttribute("data-legend-mobile");
    }
    syncViewport();
    decorateExistingDialogs();
    root.dispatchEvent(new CustomEvent("legend:mobilemodechange", { detail: { mobile: isMobile() } }));
  }

  media.addEventListener?.("change", syncMode);
  window.visualViewport?.addEventListener("resize", syncViewport, { passive: true });
  window.visualViewport?.addEventListener("scroll", syncViewport, { passive: true });
  window.addEventListener("resize", syncViewport, { passive: true });

  syncMode();
  loadTokens();

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", syncMode, { once: true });
  }

  window.LegendMobilePlatform = {
    breakpoint: 840,
    isMobile,
    syncMode,
    syncViewport,
    decorateModal,
    get tokens() { return tokens; }
  };
})();
