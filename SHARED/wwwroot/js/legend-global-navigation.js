(() => {
  const breakpoint = 840;

  document.querySelectorAll('[data-legend-global-nav]').forEach(nav => {
    const toggle = nav.querySelector('[data-legend-nav-toggle]');
    const groups = Array.from(nav.querySelectorAll('.navbar-left, .navbar-right'));
    if (!toggle) return;

    const close = () => {
      nav.classList.remove('mobile-open');
      toggle.setAttribute('aria-expanded', 'false');
    };

    toggle.addEventListener('click', () => {
      const isOpen = nav.classList.toggle('mobile-open');
      toggle.setAttribute('aria-expanded', isOpen ? 'true' : 'false');
    });

    groups.forEach(group => {
      group.querySelectorAll('a, button:not([data-bs-toggle])').forEach(control => {
        control.addEventListener('click', close);
      });
    });

    document.addEventListener('click', event => {
      if (nav.classList.contains('mobile-open') && !nav.contains(event.target)) close();
    });

    window.addEventListener('resize', () => {
      if (window.innerWidth > breakpoint) close();
    });

    close();
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

  const setAriaState = isOpen => {
    trigger.setAttribute('aria-expanded', isOpen ? 'true' : 'false');
    drawer.setAttribute('aria-hidden', isOpen ? 'false' : 'true');
    overlay.setAttribute('aria-hidden', isOpen ? 'false' : 'true');
  };

  const closeDrawer = ({ restoreFocus = false } = {}) => {
    const wasOpen = drawer.classList.contains('open');
    drawer.classList.remove('open');
    overlay.classList.remove('open');
    setAriaState(false);
    if (window.innerWidth <= breakpoint) window.LegendModal?.unlockPageScroll?.(scrollOwner);
    if (wasOpen && restoreFocus && trigger.isConnected) {
      try { trigger.focus({ preventScroll: true }); } catch { trigger.focus(); }
    }
  };

  const openDrawer = () => {
    window.LegendModal?.refreshViewportOffsets?.();
    drawer.classList.add('open');
    overlay.classList.add('open');
    setAriaState(true);
    if (window.innerWidth <= breakpoint) window.LegendModal?.lockPageScroll?.(scrollOwner);

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

  window.addEventListener('resize', () => {
    if (window.innerWidth > breakpoint) window.LegendModal?.unlockPageScroll?.(scrollOwner);
  });

  window.addEventListener('pagehide', () => {
    window.LegendModal?.unlockPageScroll?.(scrollOwner);
  });

  closeDrawer();
})();
