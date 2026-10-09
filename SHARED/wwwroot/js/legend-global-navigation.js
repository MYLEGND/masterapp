(() => {
  const breakpoint = 840;
  const navigationClosers = new Set();

  const isMobileViewport = () => window.innerWidth <= breakpoint;

  // Native same-tab navigation must keep its source control connected until the
  // browser completes the anchor/form activation. Reparenting or hiding the
  // command surface from a microtask can run before that default action.
  const leavesCurrentDocument = control => {
    if (!control) return false;

    if (control.tagName === 'A') {
      const href = (control.getAttribute('href') || '').trim();
      const target = (control.getAttribute('target') || '').trim().toLowerCase();
      if (!href || href.startsWith('#') || href.toLowerCase().startsWith('javascript:')) return false;
      if (control.hasAttribute('download')) return false;
      return !target || target === '_self';
    }

    if (control.tagName === 'BUTTON') {
      return control.type === 'submit' && Boolean(control.form);
    }

    return false;
  };

  const dismissAfterActivation = callback => window.setTimeout(callback, 0);

  const closeAllNavigation = except => {
    navigationClosers.forEach(entry => {
      if (entry.nav !== except) entry.close();
    });
  };

  document.querySelectorAll('[data-legend-global-nav]').forEach(nav => {
    const toggle = nav.querySelector('[data-legend-nav-toggle]');
    const container = nav.querySelector(':scope > .container-fluid');
    const left = nav.querySelector('.navbar-left');
    const right = nav.querySelector('.navbar-right');
    const drawer = document.getElementById('exploreDrawer');
    const overlay = document.getElementById('exploreOverlay');
    if (!toggle || !container) return;

    const panel = document.createElement('div');
    panel.className = 'legend-mobile-nav-panel';
    panel.setAttribute('data-legend-mobile-nav-panel', '');
    panel.setAttribute('aria-label', 'Navigation commands');
    container.appendChild(panel);

    const movable = [drawer, left, right].filter(Boolean);
    const origins = new Map(movable.map(node => [node, {
      parent: node.parentNode,
      next: node.nextSibling
    }]));
    let mounted = false;
    const restoreNode = node => {
      const origin = origins.get(node);
      if (!origin?.parent) return;
      if (origin.next && origin.next.parentNode === origin.parent) origin.parent.insertBefore(node, origin.next);
      else origin.parent.appendChild(node);
    };

    const mountMobilePanel = () => {
      if (mounted) return;
      // The compact mobile menu owns both command rows and the Quick Find
      // subview. Nothing is flattened or duplicated; the canonical nodes move
      // into one panel and return to their exact desktop origins at breakpoint.
      [left, right, drawer].filter(Boolean).forEach(node => panel.appendChild(node));
      mounted = true;
      nav.setAttribute('data-legend-mobile-nav-integrated', '');
      nav.classList.remove('mobile-explore-open');
      overlay?.classList.remove('open');
      drawer?.classList.remove('open');
      drawer?.setAttribute('aria-hidden', 'true');
    };

    const restoreDesktopNavigation = () => {
      if (!mounted) return;
      movable.slice().reverse().forEach(restoreNode);
      mounted = false;
      nav.removeAttribute('data-legend-mobile-nav-integrated');
      panel.replaceChildren();
    };

    const close = ({ restoreFocus = false } = {}) => {
      const wasOpen = nav.classList.contains('mobile-open');
      nav.classList.remove('mobile-open', 'mobile-explore-open');
      toggle.setAttribute('aria-expanded', 'false');
      drawer?.classList.remove('open');
      drawer?.setAttribute('aria-hidden', 'true');
      overlay?.classList.remove('open');
      overlay?.setAttribute('aria-hidden', 'true');
      if (wasOpen && restoreFocus) {
        try { toggle.focus({ preventScroll: true }); } catch { toggle.focus(); }
      }
    };

    const open = () => {
      closeAllNavigation(nav);
      mountMobilePanel();
      window.LegendModal?.refreshViewportOffsets?.();
      nav.classList.add('mobile-open');
      nav.classList.remove('mobile-explore-open');
      toggle.setAttribute('aria-expanded', 'true');
      drawer?.classList.remove('open');
      drawer?.setAttribute('aria-hidden', 'true');
      overlay?.classList.remove('open');
      overlay?.setAttribute('aria-hidden', 'true');
      panel.scrollTop = 0;
    };

    const syncResponsiveState = () => {
      if (isMobileViewport()) mountMobilePanel();
      else {
        close();
        restoreDesktopNavigation();
      }
    };

    toggle.addEventListener('click', event => {
      event.preventDefault();
      if (nav.classList.contains('mobile-open')) close({ restoreFocus: true });
      else open();
    });

    panel.addEventListener('click', event => {
      const control = event.target.closest?.('a, .explore-item, button');
      if (!control || control === toggle || control.matches('[data-bs-toggle], [aria-controls="exploreDrawer"], [data-legend-explore-close]')) return;

      // Same-tab links and form submissions own their own teardown through page
      // navigation/pagehide. Do not touch their DOM before native activation.
      if (leavesCurrentDocument(control)) return;

      // Delegated modal/action controls stay in the current document, so dismiss
      // only on the next task after every click handler/default action has run.
      dismissAfterActivation(() => {
        if (nav.isConnected) close();
      });
    });

    document.addEventListener('click', event => {
      if (!nav.classList.contains('mobile-open')) return;
      if (nav.contains(event.target)) return;
      close();
    });

    window.addEventListener('resize', syncResponsiveState, { passive: true });
    window.visualViewport?.addEventListener('resize', syncResponsiveState, { passive: true });
    nav.__legendOpenNavigation = open;
    nav.__legendCloseNavigation = close;
    navigationClosers.add({ nav, close });
    close();
    syncResponsiveState();
  });

  /*
   * Canonical Explore drawer owner for AgentPortal + ClientApp.
   * Layouts provide only content/markup; all open/close, scroll locking,
   * filtering and dismissal behavior lives here so hosts cannot drift.
   */
  const trigger = document.getElementById('exploreTrigger');
  const drawer = document.getElementById('exploreDrawer');
  const overlay = document.getElementById('exploreOverlay');
  const search = document.getElementById('exploreSearch');
  const list = document.getElementById('exploreList');

  if (!trigger || !drawer || !overlay || !list) return;

  const closeControl = drawer.querySelector('[data-legend-explore-close]');
  const scrollOwner = 'legend-explore-drawer';

  const isMobile = () => window.innerWidth <= breakpoint;

  const syncResponsiveState = () => {
    if (closeControl) closeControl.hidden = !isMobile();
    if (isMobile()) {
      window.LegendModal?.unlockPageScroll?.(scrollOwner);
      overlay.classList.remove('open');
      overlay.setAttribute('aria-hidden', 'true');
      return;
    }
    if (!drawer.classList.contains('open')) {
      window.LegendModal?.unlockPageScroll?.(scrollOwner);
      return;
    }
    window.LegendModal?.unlockPageScroll?.(scrollOwner);
  };

  const setAriaState = isOpen => {
    trigger.setAttribute('aria-expanded', isOpen ? 'true' : 'false');
    drawer.setAttribute('aria-hidden', isOpen ? 'false' : 'true');
    overlay.setAttribute('aria-hidden', isOpen ? 'false' : 'true');
  };

  const closeDrawer = ({ restoreFocus = false } = {}) => {
    const ownerNav = trigger.closest('[data-legend-global-nav]');
    const wasOpen = drawer.classList.contains('open');

    if (isMobile() && ownerNav?.hasAttribute('data-legend-mobile-nav-integrated')) {
      ownerNav.classList.remove('mobile-explore-open');
      drawer.classList.remove('open');
      setAriaState(false);
      overlay.classList.remove('open');
      overlay.setAttribute('aria-hidden', 'true');
      if (wasOpen && restoreFocus && trigger.isConnected) {
        try { trigger.focus({ preventScroll: true }); } catch { trigger.focus(); }
      }
      return;
    }

    drawer.classList.remove('open');
    overlay.classList.remove('open');
    setAriaState(false);
    window.LegendModal?.unlockPageScroll?.(scrollOwner);
    if (wasOpen && restoreFocus && trigger.isConnected) {
      try { trigger.focus({ preventScroll: true }); } catch { trigger.focus(); }
    }
  };

  const openDrawer = () => {
    const ownerNav = trigger.closest('[data-legend-global-nav]');
    if (isMobile() && ownerNav?.hasAttribute('data-legend-mobile-nav-integrated')) {
      ownerNav.__legendOpenNavigation?.();
      ownerNav.classList.add('mobile-explore-open');
      drawer.classList.add('open');
      setAriaState(true);
      overlay.classList.remove('open');
      overlay.setAttribute('aria-hidden', 'true');
      const mobilePanel = ownerNav.querySelector('[data-legend-mobile-nav-panel]');
      if (mobilePanel) mobilePanel.scrollTop = 0;
      return;
    }

    closeAllNavigation(ownerNav);
    window.LegendModal?.refreshViewportOffsets?.();
    drawer.classList.add('open');
    overlay.classList.add('open');
    setAriaState(true);
    syncResponsiveState();

    if (search && window.innerWidth > breakpoint) {
      window.setTimeout(() => {
        try { search.focus({ preventScroll: true }); } catch { search.focus(); }
      }, 80);
    }
  };

  trigger.addEventListener('click', event => {
    event.preventDefault();
    if (drawer.classList.contains('open')) closeDrawer({ restoreFocus: true });
    else openDrawer();
  });

  closeControl?.addEventListener('click', () => closeDrawer({ restoreFocus: true }));
  overlay.addEventListener('click', () => closeDrawer({ restoreFocus: true }));

  document.addEventListener('keydown', event => {
    if (event.key === 'Escape' && drawer.classList.contains('open')) {
      event.preventDefault();
      closeDrawer({ restoreFocus: true });
    }
  });

  list.addEventListener('click', event => {
    const item = event.target.closest('.explore-item');
    if (!item) return;

    const ownerNav = trigger.closest('[data-legend-global-nav]');
    if (leavesCurrentDocument(item)) return;

    if (isMobile() && ownerNav?.hasAttribute('data-legend-mobile-nav-integrated')) {
      dismissAfterActivation(() => ownerNav.__legendCloseNavigation?.());
      return;
    }

    dismissAfterActivation(() => closeDrawer());
  });

  search?.addEventListener('input', () => {
    const query = (search.value || '').trim().toLowerCase();
    list.querySelectorAll('.explore-item').forEach(item => {
      item.hidden = !item.textContent.toLowerCase().includes(query);
    });
  });

  window.addEventListener('resize', syncResponsiveState, { passive: true });

  window.addEventListener('pagehide', () => {
    window.LegendModal?.unlockPageScroll?.(scrollOwner);
  });

  closeDrawer();
  syncResponsiveState();
})();
