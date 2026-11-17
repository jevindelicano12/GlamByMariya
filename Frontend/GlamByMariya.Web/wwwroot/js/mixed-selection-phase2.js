(() => {
  const form = document.querySelector('#mixed-selection');
  if (!form) return;
  const booking = form.closest('#booking-form');
  const mode = booking?.querySelector('#mixed-mode');
  const count = booking ? booking.elements.people : form.querySelector('#mixed-count'), countError = form.querySelector('#mixed-count-error');
  const cards = form.querySelector('#mixed-people'), template = document.querySelector('#mixed-person-template');
  const estimate = booking?.querySelector('#mixed-estimate-inline');
  const totals = (estimate || form).querySelector('#mixed-totals'), status = (estimate || form).querySelector('#mixed-status');
  const motion = matchMedia('(prefers-reduced-motion: no-preference)');
  motion.addEventListener('change', () => { if (!motion.matches) totals.getAnimations().forEach(animation => animation.cancel()); });
  if (booking) count.setAttribute('aria-describedby', `${count.getAttribute('aria-describedby') || ''} mixed-count-error`.trim());
  const money = value => new Intl.NumberFormat('en-PH', { style: 'currency', currency: 'PHP' }).format(value);
  let version = 0, request;
  const requestedCount = new URLSearchParams(location.search).get('people');
  if (!booking && requestedCount && /^[0-9]{1,2}$/.test(requestedCount) && Number(requestedCount) >= 2 && Number(requestedCount) <= 20) count.value = requestedCount;
  const clearEstimate = () => {
    totals.textContent = 'Choose a service for each person to calculate your estimate.';
    cards.querySelectorAll('.mixed-person').forEach(card => { card.querySelector('.mixed-person-total').textContent = card.querySelector('.mixed-service').value ? 'Price will appear when the group estimate is valid.' : 'Choose a service to see this person’s price.'; });
  };
  const quote = async () => {
    const revision = ++version; request?.abort();
    if (booking) { booking.dataset.mixedQuoteState = 'loading'; booking.dispatchEvent(new Event('mixed-selection-invalidated')); }
    totals.getAnimations().forEach(animation => animation.cancel());
    if (booking && !mode.checked) return;
    const fields = [...cards.querySelectorAll('.mixed-person')];
    let invalid = false;
    const people = fields.map(card => {
      const select = card.querySelector('.mixed-service'), error = card.querySelector('.mixed-error');
      const missing = !select.value;
      select.setAttribute('aria-invalid', String(missing)); error.textContent = missing ? 'Choose a makeup service for this person.' : '';
      invalid ||= missing;
      return { serviceId: select.value, addOnIds: [...card.querySelectorAll('.mixed-addons input:checked')].map(a => a.value) };
    });
    if (countError.textContent || invalid || fields.length < 2) { if (booking) { booking.dataset.mixedQuoteState = 'invalid'; booking.dispatchEvent(new Event('mixed-quote-state')); } clearEstimate(); status.textContent = ''; return; }
    clearEstimate(); status.textContent = 'Calculating your group estimate…';
    request = new AbortController();
    try {
      const response = await fetch(form.dataset.quoteUrl, { method: 'POST', signal: request.signal, headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': (booking || form).querySelector('[name=__RequestVerificationToken]').value }, body: JSON.stringify(people) });
      if (response.redirected || !response.headers.get('content-type')?.includes('application/json')) throw new Error('Please sign in again and retry.');
      const result = await response.json();
      if (!response.ok) throw new Error(result.error || 'Please check your selections and retry.');
      if (revision !== version) return;
      if (!Array.isArray(result.people) || result.people.length !== fields.length || !['serviceTotal', 'addOnTotal', 'durationMinutes', 'finalTotal', 'requiredDownPayment'].every(key => Number.isFinite(result[key]) && result[key] >= 0) || !result.people.every(person => person.service && Number.isFinite(person.service.price) && person.service.price >= 0 && Array.isArray(person.addOns) && person.addOns.every(addon => Number.isFinite(addon.price) && addon.price >= 0) && Number.isFinite(person.durationMinutes) && person.durationMinutes >= 0)) throw new Error('The price response was incomplete. Please retry.');
      result.people.forEach((person, i) => { fields[i].querySelector('.mixed-person-total').textContent = `Service ${money(person.service.price)} · Add-ons ${money(person.addOns.reduce((sum, a) => sum + a.price, 0))} · ${person.durationMinutes} min`; });
      totals.textContent = `Services ${money(result.serviceTotal)} · Add-ons ${money(result.addOnTotal)} · Makeup ${result.durationMinutes} min · Estimated total ${money(result.finalTotal)} · Minimum down payment ${money(result.requiredDownPayment)}`;
      if (motion.matches) {
        totals.getAnimations().forEach(animation => animation.cancel());
        totals.animate([{ backgroundColor: '#f1d9dc' }, { backgroundColor: 'transparent' }], { duration: 300, easing: 'ease-out' });
      }
      status.textContent = 'Estimate calculated from current catalog prices.';
      if (booking) { booking.mixedPeople = people; booking.dataset.mixedQuoteState = 'ready'; booking.dispatchEvent(new Event('mixed-quote-state')); }
    } catch (error) { if (revision === version && error.name !== 'AbortError') { if (booking) { booking.dataset.mixedQuoteState = 'error'; booking.dispatchEvent(new Event('mixed-quote-state')); } clearEstimate(); status.textContent = `Unable to calculate your estimate. ${error.message}`; } }
  };
  const resize = () => {
    if (booking && !mode.checked) return;
    const valid = /^[0-9]{1,2}$/.test(count.value) && Number(count.value) >= 2 && Number(count.value) <= 20;
    count.setAttribute('aria-invalid', String(!valid)); countError.textContent = valid ? '' : 'Enter a whole number from 2 to 20.';
    if (!valid) { quote(); return; }
    while (cards.children.length > Number(count.value)) cards.lastElementChild.remove();
    while (cards.children.length < Number(count.value)) {
      const fragment = template.content.cloneNode(true), card = fragment.querySelector('.mixed-person'), n = cards.children.length + 1;
      card.querySelector('legend').textContent = `Person ${n}`;
      const select = card.querySelector('.mixed-service'), error = card.querySelector('.mixed-error');
      error.id = `mixed-service-error-${n}`; select.setAttribute('aria-describedby', error.id);
      cards.append(fragment);
    }
    quote();
  };
  count.addEventListener('input', resize);
  cards.addEventListener('change', quote);
  if (!booking) { form.addEventListener('submit', event => event.preventDefault()); resize(); }
  else {
    const syncMode = () => {
      const active = mode.checked && booking.querySelector('[name="bookingType"]:checked')?.value === 'package';
      if (!active) { mode.checked = false; ++version; request?.abort(); cards.replaceChildren(); clearEstimate(); status.textContent = ''; countError.textContent = ''; count.removeAttribute('aria-invalid'); }
      booking.classList.toggle('is-mixed', active);
      form.hidden = !active;
      estimate.hidden = !active;
      form.querySelectorAll('input, select').forEach(control => { control.disabled = !active; });
      if (active) resize();
    };
    mode.addEventListener('change', syncMode);
    booking.querySelectorAll('[name="bookingType"]').forEach(input => input.addEventListener('change', syncMode));
    syncMode();
  }
})();
