document.querySelectorAll('.demo-image-form').forEach(form => {
  const input = form.querySelector('input[type="file"]');
  const preview = form.querySelector('[data-selected-preview]');
  let current;
  input.addEventListener('change', () => {
    if (current) URL.revokeObjectURL(current);
    current = null;
    const file = input.files[0];
    if (!file || !['image/jpeg', 'image/png'].includes(file.type) || file.size > 5000000) { preview.hidden = true; preview.removeAttribute('src'); return; }
    current = URL.createObjectURL(file);
    preview.src = current;
    preview.hidden = false;
  });
});
