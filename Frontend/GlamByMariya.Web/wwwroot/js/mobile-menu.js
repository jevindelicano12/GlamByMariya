// Hamburger menus (guest, customer, staff). A real <button> replaces the old <details>/<summary>
// toggle, which iPhone Safari did not open reliably. Closes on ✕, backdrop, Esc, a link, or desktop width.
(() => {
  document.querySelectorAll('[data-mobile-menu]').forEach(menu => {
    const button = menu.querySelector('.menu-toggle');
    if (!button) return;
    const panel = document.getElementById(button.getAttribute('aria-controls'));
    const mobile = matchMedia(menu.dataset.mobileMenu);
    const isOpen = () => menu.classList.contains('is-open');
    const set = open => {
      if (open === isOpen()) return;
      menu.classList.toggle('is-open', open);
      button.setAttribute('aria-expanded', String(open));
      button.setAttribute('aria-label', open ? 'Close menu' : 'Open menu');
      document.documentElement.classList.toggle('menu-open', open);
      menu.dispatchEvent(new CustomEvent('menu:toggle', { detail: { open } }));
    };
    menu.mobileMenu = { set, isOpen, toggle: () => set(!isOpen()) };
    button.addEventListener('click', () => set(!isOpen()));
    menu.querySelectorAll('[data-menu-close]').forEach(el => el.addEventListener('click', () => set(false)));
    panel?.addEventListener('click', event => { if (event.target.closest('a')) set(false); });
    document.addEventListener('keydown', event => {
      if (event.key === 'Escape' && isOpen()) { set(false); button.focus(); }
    });
    const sync = () => { if (!mobile.matches) set(false); };
    if (mobile.addEventListener) mobile.addEventListener('change', sync); else mobile.addListener?.(sync);
    // Coming back with the Back button can restore a page whose menu was left open.
    window.addEventListener('pageshow', () => set(false));
  });
})();
