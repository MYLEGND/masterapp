(() => {
  'use strict';

  // Single navigation controller for every public website and Studio shell.
  // Events are delegated: Website Studio may replace the complete header
  // without orphaning listeners or installing a competing menu controller.
  if (window.__legendPublicNavigationInstalled) return;
  window.__legendPublicNavigationInstalled = true;

  function controls(header) {
    if (!header) return null;
    const nav = header.querySelector('[data-public-nav]');
    const toggle = header.querySelector('[data-public-nav-toggle]');
    return nav && toggle ? { nav, toggle } : null;
  }

  function sync(header = document.querySelector('.site-header')) {
    const current = controls(header);
    if (!current) return;
    const { nav, toggle } = current;
    const expanded = nav.dataset.open === 'true';
    nav.dataset.open = expanded ? 'true' : 'false';
    toggle.setAttribute('aria-controls', nav.id || 'primary-nav');
    toggle.setAttribute('aria-expanded', String(expanded));
  }

  function close(header, restoreFocus = false) {
    const current = controls(header);
    if (!current) return;
    const { nav, toggle } = current;
    const wasOpen = nav.dataset.open === 'true';
    nav.dataset.open = 'false';
    toggle.setAttribute('aria-expanded', 'false');
    if (wasOpen && restoreFocus) {
      try { toggle.focus({ preventScroll: true }); }
      catch { toggle.focus(); }
    }
  }

  function closeAll(restoreFocus = false) {
    document.querySelectorAll('.site-header').forEach(header => close(header, restoreFocus));
  }

  function open(header) {
    const current = controls(header);
    if (!current) return;
    const { nav, toggle } = current;
    nav.dataset.open = 'true';
    nav.scrollTop = 0;
    toggle.setAttribute('aria-expanded', 'true');
  }

  document.addEventListener('click', event => {
    const element = event.target?.closest?.('*');
    if (!element) return;
    const toggle = element.closest('[data-public-nav-toggle]');
    const header = toggle?.closest('.site-header');
    if (header && controls(header)?.toggle === toggle) {
      event.preventDefault();
      const expanded = controls(header).nav.dataset.open === 'true';
      if (expanded) close(header, true);
      else open(header);
      return;
    }
    const link = element.closest('[data-public-nav] a[href]');
    if (link) {
      close(link.closest('.site-header'));
      return;
    }
    document.querySelectorAll('.site-header').forEach(other => {
      if (!other.contains(element)) close(other);
    });
  });

  document.addEventListener('keydown', event => {
    if (event.key === 'Escape') closeAll(true);
  });

  window.addEventListener('resize', () => {
    document.querySelectorAll('.site-header').forEach(header => {
      const width = header.closest('.legend-cms-preview')?.clientWidth || window.innerWidth;
      if (width > 980) close(header);
    });
  }, { passive: true });

  // This is the same authority the CMS asks to reconcile after a shell edit.
  window.LegendPublicNavigation = Object.freeze({ sync, close: closeAll });
  if (document.readyState === 'loading')
    document.addEventListener('DOMContentLoaded', () => sync(), { once: true });
  else sync();
})();
