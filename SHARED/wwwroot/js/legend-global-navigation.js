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
})();
