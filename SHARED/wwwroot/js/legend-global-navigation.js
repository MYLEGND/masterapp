(() => {
  const breakpoint = 840;
  const navigationClosers = new Set();

  const isMobileViewport = () => window.innerWidth <= breakpoint;

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
    const scrollOwner = 'legend-global-navigation';

    const restoreNode = node => {
      const origin = origins.get(node);
      if (!origin?.parent) return;
      if (origin.next && origin.next.parentNode === origin.parent) origin.parent.insertBefore(node, origin.next);
      else origin.parent.appendChild(node);
    };

    const mountMobilePanel = () => {
      if (mounted) return;
      // The mobile menu has one owner and one scroll region. Explore content is
      // moved into the same panel rather than opening a second sheet on top.
      [drawer, left, right].filter(Boolean).forEach(node => panel.appendChild(node));
      mounted = true;
      nav.setAttribute('data-legend-mobile-nav-integrated', '');
      overlay?.classList.remove('open');
      drawer?.classList.remove('open');
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
      nav.classList.remove('mobile-open');
      toggle.setAttribute('aria-expanded', 'false');
      window.LegendModal?.unlockPageScroll?.(scrollOwner);
      if (wasOpen && restoreFocus) {
        try { toggle.focus({ preventScroll: true }); } catch { toggle.focus(); }
      }
    };

    const open = () => {
      closeAllNavigation(nav);
      mountMobilePanel();
      window.LegendModal?.refreshViewportOffsets?.();
      nav.classList.add('mobile-open');
      toggle.setAttribute('aria-expanded', 'true');
      panel.scrollTop = 0;
      window.LegendModal?.lockPageScroll?.(scrollOwner);
    };

    const syncResponsiveState = () => {
      if (isMobileViewport()) {
        mountMobilePanel();
        if (nav.classList.contains('mobile-open')) window.LegendModal?.lockPageScroll?.(scrollOwner);
      } else {
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
      const control = event.target.closest?.('a, .explore-item, button:not([data-bs-toggle])');
      if (!control || control === toggle) return;
      // Keep dropdown toggles interactive inside the command sheet; navigation
      // actions dismiss the sheet and restore page scrolling.
      close();
    });

    document.addEventListener('click', event => {
      if (!nav.classList.contains('mobile-open')) return;
      if (nav.contains(event.target)) return;
      close();
    });

    window.addEventListener('resize', syncResponsiveState, { passive: true });
    window.visualViewport?.addEventListener('resize', syncResponsiveState, { passive: true });
    window.addEventListener('pagehide', () => window.LegendModal?.unlockPageScroll?.(scrollOwner));

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
    if (!drawer.classList.contains('open')) {
      window.LegendModal?.unlockPageScroll?.(scrollOwner);
      return;
    }
    if (isMobile()) window.LegendModal?.lockPageScroll?.(scrollOwner);
    else window.LegendModal?.unlockPageScroll?.(scrollOwner);
  };

  const setAriaState = isOpen => {
    trigger.setAttribute('aria-expanded', isOpen ? 'true' : 'false');
    drawer.setAttribute('aria-hidden', isOpen ? 'false' : 'true');
    overlay.setAttribute('aria-hidden', isOpen ? 'false' : 'true');
  };

  const closeDrawer = ({ restoreFocus = false } = {}) => {
    const ownerNav = trigger.closest('[data-legend-global-nav]');
    if (isMobile() && ownerNav?.hasAttribute('data-legend-mobile-nav-integrated')) {
      ownerNav.__legendCloseNavigation?.({ restoreFocus });
      return;
    }
    const wasOpen = drawer.classList.contains('open');
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
    if (isMobile()) {
      ownerNav?.__legendOpenNavigation?.();
      return;
    }
    closeAllNavigation(ownerNav);
    window.LegendModal?.refreshViewportOffsets?.();
    drawer.classList.add('open');
    overlay.classList.add('open');
    setAriaState(true);
    syncResponsiveState();

    // Desktop can take search focus immediately. On phones, avoid forcing the
    // virtual keyboard over the newly opened sheet.
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
    if (event.target.closest('.explore-item')) closeDrawer();
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
