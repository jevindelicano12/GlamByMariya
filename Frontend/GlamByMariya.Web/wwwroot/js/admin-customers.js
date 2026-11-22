// Admin → Customers: changing the sort applies it right away (the Apply button stays for no-JavaScript use).
(() => {
  const bar = document.querySelector('.customers-bar');
  if (!bar) return;
  bar.classList.add('is-live');
  bar.querySelector('select[name="sort"]')?.addEventListener('change', () => bar.submit());
})();
