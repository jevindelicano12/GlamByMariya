(() => {
  const input = document.getElementById('payment-proof'), preview = document.getElementById('payment-proof-preview');
  const meta = document.getElementById('payment-proof-meta');
  if (!input || !preview) return;
  let url, revision = 0;
  input.setAttribute('aria-describedby', 'payment-proof-meta');
  input.addEventListener('change', async () => {
    const current = ++revision;
    if (url) URL.revokeObjectURL(url);
    preview.hidden = true; preview.removeAttribute('src'); input.setCustomValidity('');
    const file = input.files?.[0];
    if (meta) meta.textContent = '';
    if (!file) return;
    const fail = text => { input.setCustomValidity(text); input.setAttribute('aria-invalid', 'true'); if (meta) meta.textContent = text; };
    if (file.size > 5000000) { fail('Choose a JPG or PNG up to 5 MB.'); return; }
    input.setCustomValidity('Wait for the image to be checked.');
    try {
      const bytes = new Uint8Array(await file.slice(0, 8).arrayBuffer());
      if (current !== revision) return;
      const jpg = bytes[0] === 255 && bytes[1] === 216 && bytes[2] === 255;
      const png = [137, 80, 78, 71, 13, 10, 26, 10].every((value, i) => bytes[i] === value);
      if (!jpg && !png) { fail('This file is not a JPG or PNG image. Choose a payment screenshot.'); return; }
      url = URL.createObjectURL(file); preview.src = url;
      await preview.decode();
      if (current !== revision) return;
      preview.hidden = false; input.setCustomValidity(''); input.removeAttribute('aria-invalid');
      if (meta) meta.textContent = `${file.name} · ${(file.size / 1000000).toFixed(2)} MB · Ready for private review, not verified payment.`;
    } catch { if (current === revision) fail('The image could not be read. Choose a valid JPG or PNG screenshot.'); }
  });
})();
