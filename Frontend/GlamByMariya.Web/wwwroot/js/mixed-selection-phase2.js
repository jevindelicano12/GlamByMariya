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
  const templateOptions = [...template.content.querySelectorAll('.mixed-service option')];
  const syncLimitedOptions = () => {
    const selects = [...cards.querySelectorAll('.mixed-service')];
    const counts = new Map();
    selects.forEach(s => { if (s.value) counts.set(s.value, (counts.get(s.value) || 0) + 1); });
    selects.forEach(select => {
      const current = select.value;
      const desired = templateOptions.filter(opt => {
        const max = Number(opt.dataset.max || 0);
        if (!opt.value || !max) return true;
        const usedByOthers = (counts.get(opt.value) || 0) - (current === opt.value ? 1 : 0);
        return usedByOthers < max;
      });
      const existingValues = [...select.options].map(o => o.value).join(',');
      const desiredValues = desired.map(o => o.value).join(',');
      if (existingValues !== desiredValues) {
        select.replaceChildren(...desired.map(opt => opt.cloneNode(true)));
        select.value = current;
      }
    });
  };
  const quote = async () => {
    const revision = ++version; request?.abort();
    if (booking) { booking.dataset.mixedQuoteState = 'loading'; booking.dispatchEvent(new Event('mixed-selection-invalidated')); }
    totals.getAnimations().forEach(animation => animation.cancel());
    if (booking && !mode.checked) return;
    syncLimitedOptions();
    const fields = [...cards.querySelectorAll('.mixed-person')];
    const seen = new Map();
    const missingPeople = [], duplicatePeople = [];
    let invalid = false;
    const people = fields.map((card, index) => {
      const select = card.querySelector('.mixed-service'), error = card.querySelector('.mixed-error'), meta = card.querySelector('.person-accordion-meta');
      const pickedCard = card.querySelector('.selected-service-card');
      const option = select.selectedOptions?.[0];
      const max = Number(option?.dataset.max || 0);
      const addOnIds = [...card.querySelectorAll('.mixed-addons input:checked')].map(a => a.value);
      let errText = '';
      let isDuplicate = false;
      if (!select.value) {
        errText = 'Choose a makeup service for this person.';
        missingPeople.push(`Person ${index + 1}`);
      } else {
        const nextCount = (seen.get(select.value) || 0) + 1;
        seen.set(select.value, nextCount);
        if (max > 0 && nextCount > max) {
          isDuplicate = true;
          duplicatePeople.push(`Person ${index + 1}`);
          const name = option?.dataset.name || option?.textContent || 'This service';
          errText = /bridal/i.test(name) ? 'Bridal make up is already chosen choose another' : `${name} is already chosen choose another`;
        }
      }
      select.setAttribute('aria-invalid', String(Boolean(errText)));
      error.textContent = errText;
      if (errText) { invalid = true; if (isDuplicate) card.open = true; }
      if (pickedCard) {
        const showPicked = Boolean(select.value && !isDuplicate);
        pickedCard.hidden = !showPicked;
        if (showPicked) {
          pickedCard.querySelector('[data-picked-name]').textContent = option?.dataset.name || '';
          pickedCard.querySelector('[data-picked-price]').textContent = `₱${option?.dataset.price || ''} · ${option?.dataset.minutes || ''} min`;
          pickedCard.querySelector('[data-picked-desc]').textContent = option?.dataset.description || '';
        }
      }
      if (meta) {
        meta.classList.toggle('is-error', isDuplicate);
        meta.classList.toggle('is-empty', !select.value);
        meta.textContent = !select.value
          ? 'Not selected'
          : isDuplicate
            ? 'Already chosen'
            : `${option?.dataset.name || 'Selected'} · ₱${option?.dataset.price || ''}${addOnIds.length ? ` · +${addOnIds.length}` : ''}`;
      }
      return { serviceId: select.value, addOnIds };
    });
    if (countError.textContent || invalid || fields.length < 2) {
      if (booking) { booking.dataset.mixedQuoteState = 'invalid'; booking.dispatchEvent(new Event('mixed-quote-state')); }
      clearEstimate();
      status.textContent = countError.textContent || (missingPeople.length ? `Still missing a service: ${missingPeople.join(', ')}.` : duplicatePeople.length ? `Choose another look for: ${duplicatePeople.join(', ')}.` : '');
      return;
    }
    clearEstimate(); status.textContent = 'Calculating your group estimate…';
    request = new AbortController();
    try {
      const response = await fetch(form.dataset.quoteUrl, { method: 'POST', signal: request.signal, headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': (booking || form).querySelector('[name=__RequestVerificationToken]').value }, body: JSON.stringify(people) });
      if (response.redirected || !response.headers.get('content-type')?.includes('application/json')) throw new Error('Please sign in again and retry.');
      const result = await response.json();
      if (!response.ok) throw new Error(result.error || 'Please check your selections and retry.');
      if (revision !== version) return;
      if (!Array.isArray(result.people) || result.people.length !== fields.length || !['serviceTotal', 'addOnTotal', 'durationMinutes', 'finalTotal', 'requiredDownPayment'].every(key => Number.isFinite(result[key]) && result[key] >= 0) || !result.people.every(person => person.service && Number.isFinite(person.service.price) && person.service.price >= 0 && Array.isArray(person.addOns) && person.addOns.every(addon => Number.isFinite(addon.price) && addon.price >= 0) && Number.isFinite(person.durationMinutes) && person.durationMinutes >= 0)) throw new Error('The price response was incomplete. Please retry.');
      result.people.forEach((person, i) => { fields[i].querySelector('.mixed-person-total').textContent = `Service ${Number.isFinite(person.regularPrice) && person.regularPrice > person.service.price ? `${money(person.service.price)} group price (regular ${money(person.regularPrice)})` : money(person.service.price)} · Add-ons ${money(person.addOns.reduce((sum, a) => sum + a.price, 0))} · ${person.durationMinutes} min`; });
      totals.textContent = `Services ${money(result.serviceTotal)}${result.groupDiscount > 0 ? ` (Group discount −${money(result.groupDiscount)})` : ''} · Add-ons ${money(result.addOnTotal)} · Makeup ${result.durationMinutes} min · Estimated total ${money(result.finalTotal)} · Minimum down payment ${money(result.requiredDownPayment)}`;
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
    const maxPeople = window.BookingQuantity?.maximumPeople || 20;
    const valid = /^[0-9]{1,2}$/.test(count.value) && Number(count.value) >= 2 && Number(count.value) <= maxPeople;
    count.setAttribute('aria-invalid', String(!valid)); countError.textContent = valid ? '' : `Enter a whole number from 2 to ${maxPeople}.`;
    if (!valid) { quote(); return; }
    while (cards.children.length > Number(count.value)) cards.lastElementChild.remove();
    while (cards.children.length < Number(count.value)) {
      const fragment = template.content.cloneNode(true), card = fragment.querySelector('.mixed-person'), n = cards.children.length + 1;
      const title = card.querySelector('.person-accordion-title') || card.querySelector('legend');
      if (title) title.textContent = `Person ${n}`;
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
      const isPackage = booking.querySelector('[name="bookingType"]:checked')?.value === 'package';
      mode.checked = isPackage;
      const active = isPackage;
      if (!active) { ++version; request?.abort(); cards.replaceChildren(); clearEstimate(); status.textContent = ''; countError.textContent = ''; count.removeAttribute('aria-invalid'); }
      else if (!/^[0-9]{1,2}$/.test(count.value) || Number(count.value) < 2) { count.value = '2'; }
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
