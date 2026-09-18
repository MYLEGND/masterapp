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
    setVar("--legend-mobile-compact-control-height", px(sizes.compactControlHeight));
    setVar("--legend-mobile-control-height", px(sizes.controlHeight));
    setVar("--legend-mobile-prominent-control-height", px(sizes.prominentControlHeight));
    setVar("--legend-mobile-card-content", px(spacing.cardContent));
    setVar("--legend-mobile-micro", px(spacing.micro));
    setVar("--legend-mobile-tiny", px(spacing.tiny));
    setVar("--legend-mobile-capsule-radius", px(radii.capsule));
    setVar("--legend-mobile-display-size", px(next?.typography?.display?.size));
    setVar("--legend-mobile-title-size", px(next?.typography?.title?.size));
    setVar("--legend-mobile-section-size", px(next?.typography?.section?.size));
    setVar("--legend-mobile-body-size", px(next?.typography?.body?.size));
    setVar("--legend-mobile-supporting-size", px(next?.typography?.supporting?.size));
    setVar("--legend-mobile-label-size", px(next?.typography?.label?.size));
    setVar("--legend-mobile-motion-quick", seconds(motion.quickSeconds));
    setVar("--legend-mobile-motion-standard", seconds(motion.standardSeconds));
    setVar("--legend-mobile-motion-entrance", seconds(motion.entranceSeconds));
    setVar("--legend-mobile-canvas", color("canvas"));
    setVar("--legend-mobile-surface", color("surface"));
    setVar("--legend-mobile-surface-elevated", color("surfaceElevated"));
    setVar("--legend-mobile-midnight", color("midnight"));
    setVar("--legend-mobile-navy", color("navy"));
    setVar("--legend-mobile-on-navy", color("onNavy"));
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

  function syncBodySheetState() {
    const anyOpen = document.querySelector("[data-legend-mobile-sheet-open]");
    document.body?.classList.toggle("legend-mobile-sheet-open", Boolean(anyOpen) && isMobile());
  }

  function decorateModal(surface) {
    if (!surface || surface.nodeType !== 1) return;
    if (!isMobile()) {
      surface.removeAttribute("data-legend-mobile-sheet");
      surface.removeAttribute("data-legend-mobile-sheet-open");
      surface.querySelectorAll("[data-legend-mobile-sheet-panel],[data-legend-mobile-sheet-scroll],[data-legend-mobile-sheet-header],[data-legend-mobile-sheet-footer],[data-legend-mobile-sheet-close]")
        .forEach(node => {
          node.removeAttribute("data-legend-mobile-sheet-panel");
          node.removeAttribute("data-legend-mobile-sheet-scroll");
          node.removeAttribute("data-legend-mobile-sheet-scroll-self");
          node.removeAttribute("data-legend-mobile-sheet-header");
          node.removeAttribute("data-legend-mobile-sheet-footer");
          node.removeAttribute("data-legend-mobile-sheet-close");
        });
      syncBodySheetState();
      return;
    }

    if (surface.matches(".modal")) {
      surface.setAttribute("data-legend-mobile-sheet", "");
      surface.toggleAttribute("data-legend-mobile-sheet-open", surface.classList.contains("show"));
      const panel = surface.querySelector(":scope > .modal-dialog > .modal-content");
      panel?.setAttribute("data-legend-mobile-sheet-panel", "");
      const body = panel?.querySelector(":scope > .modal-body");
      body?.setAttribute("data-legend-mobile-sheet-scroll", "");
      syncBodySheetState();
      return;
    }

    const semanticDialog = surface.matches("[role='dialog'],[role='alertdialog']")
      ? surface
      : surface.querySelector("[role='dialog'],[role='alertdialog']");
    if (semanticDialog) {
      if (semanticDialog.matches(".drawer.crm-qv-shell")) {
        surface.setAttribute("data-legend-mobile-sheet", "");
        surface.toggleAttribute("data-legend-mobile-sheet-open", surface.getAttribute("aria-hidden") === "false");
        semanticDialog.setAttribute("data-legend-mobile-sheet-panel", "");
        semanticDialog.querySelector(":scope > .dhead")?.setAttribute("data-legend-mobile-sheet-header", "");
        semanticDialog.querySelector(":scope > .dbody")?.setAttribute("data-legend-mobile-sheet-scroll", "");
        syncBodySheetState();
        return;
      }
      const nestedPanel = semanticDialog.querySelector(
        ":scope > .modal-content, :scope > [class*='-panel'], :scope > [class*='-dialog'], :scope > [class*='-card'], :scope > [class*='-window']"
      );
      const panel = nestedPanel || semanticDialog;
      surface.setAttribute("data-legend-mobile-sheet", "");
      panel.setAttribute("data-legend-mobile-sheet-panel", "");

      const header = panel.querySelector(":scope > .modal-header, :scope > [data-dialog-header], :scope > [class*='-header'], :scope > [class*='-head']");
      const scroll = panel.querySelector(":scope > .modal-body, :scope > [data-dialog-body], :scope > [class*='-body'], :scope > [class*='-content'], :scope > [class*='-main'], :scope > [class*='-doc'], :scope > [class*='-form']");
      const footer = panel.querySelector(":scope > .modal-footer, :scope > [data-dialog-footer], :scope > [class*='-footer'], :scope > [class*='-foot']");
      header?.setAttribute("data-legend-mobile-sheet-header", "");
      if (scroll) {
        scroll.setAttribute("data-legend-mobile-sheet-scroll", "");
      } else {
        panel.setAttribute("data-legend-mobile-sheet-scroll-self", "");
      }
      footer?.setAttribute("data-legend-mobile-sheet-footer", "");

      panel.querySelectorAll(
        "button[class*='close'], [role='button'][class*='close'], button[aria-label*='close' i], button[aria-label*='dismiss' i], [data-bs-dismiss='modal']"
      ).forEach(control => control.setAttribute("data-legend-mobile-sheet-close", ""));

      if (surface.hasAttribute("hidden")) {
        surface.setAttribute("data-legend-mobile-hidden-controlled", "1");
      }
      const explicitOpen =
        surface.classList.contains("open") ||
        surface.classList.contains("visible") ||
        surface.classList.contains("is-open") ||
        surface.classList.contains("show") ||
        surface.getAttribute("aria-hidden") === "false";
      const hiddenControlledOpen =
        surface.getAttribute("data-legend-mobile-hidden-controlled") === "1" &&
        !surface.hasAttribute("hidden");
      const overlayControlledOpen =
        surface !== semanticDialog &&
        !surface.hasAttribute("hidden") &&
        window.getComputedStyle(surface).display !== "none";
      surface.toggleAttribute(
        "data-legend-mobile-sheet-open",
        explicitOpen || hiddenControlledOpen || overlayControlledOpen
      );
      syncBodySheetState();
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
