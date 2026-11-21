(() => {
  const form = document.querySelector('form.website-content-editor');
  if (!form) return;
  let dirty = false;
  const mark = () => { dirty = true; };
  form.addEventListener('input', mark);
  form.addEventListener('change', mark);
  form.addEventListener('submit', () => { dirty = false; });
  window.addEventListener('beforeunload', event => {
    if (!dirty) return;
    event.preventDefault();
    event.returnValue = '';
  });
})();
