(() => {
  const form = document.querySelector('#booking-form');
  if (!form?.querySelector('#mixed-mode')) return;
  const active = () => form.classList.contains('is-mixed');
  const submit = form.querySelector('[type="submit"]'), deposit = form.elements.deposit;
  const review = form.querySelector('#mixed-review'), people = form.querySelector('#mixed-review-people');
  const totals = form.querySelector('#mixed-review-totals');
  // A package needs the customer's pin too (the server refuses it without one), so the map moves
  // with the area and address into the package step, above its calendar.
  const locationFields = form.querySelector('.booking-details > .panel:nth-child(2) .form-grid');
  const locationMap = form.querySelector('#booking-location');
  const locationHome = document.createComment('Service location');
  locationFields.before(locationHome);
  const money = amount => new Intl.NumberFormat('en-PH', { style: 'currency', currency: 'PHP' }).format(amount);
  let submitting = false;
  // Add-ons are paid on top of the service prices, so the summary shows their amount and who has them.
  const addOnSummary = q => {
    const who = q.people.map((p, i) => p.addOns.length ? `Person ${i + 1}: ${p.addOns.map(a => `${a.name} × ${a.quantity}`).join(', ')}` : null).filter(Boolean);
    return who.length ? `${money(q.addOnTotal)} (${who.join('; ')})` : money(0);
  };
  const message = text => form.dispatchEvent(new CustomEvent('booking-submission-message', { detail: { message: text } }));
  const payment = () => {
    if (!active() || !form.mixedSlot) return;
    const q = form.mixedSlot.quote, choice = form.querySelector('[name="paymentChoice"]:checked').value;
    deposit.min = q.requiredDownPayment.toFixed(2); deposit.max = q.finalTotal.toFixed(2);
    deposit.readOnly = choice !== 'custom';
    if (choice === 'minimum') deposit.value = deposit.min;
    if (choice === 'full') deposit.value = deposit.max;
    form.querySelector('#summary-balance').textContent = deposit.value && deposit.validity.valid ? `${money(q.finalTotal - Number(deposit.value))} after claimed payment` : 'Enter a valid payment';
  };
  const render = () => {
    if (!active()) { review.hidden = true; return; }
    const slot = form.mixedSlot;
    submit.disabled = submitting || !slot;
    review.hidden = !slot;
    if (!slot) return;
    const q = slot.quote;
    form.elements.expectedTotal.value = q.finalTotal.toFixed(2);
    form.elements.expectedMinimum.value = q.requiredDownPayment.toFixed(2);
    form.elements.mixedSelection.value = slot.selection;
    form.querySelector('#mixed-review-count').textContent = `${q.people.length} people`;
    people.replaceChildren();
    q.people.forEach((person, index) => {
      const article = document.createElement('article'), heading = document.createElement('h4'), service = document.createElement('p'), addons = document.createElement('p'), total = document.createElement('strong');
      heading.textContent = `Person ${index + 1}`;
      service.textContent = `${person.service.name} · ${money(person.service.price)}`;
      addons.textContent = person.addOns.map(a => `${a.name} × ${a.quantity} · ${money(a.price * a.quantity)}`).join(', ') || 'No add-ons';
      total.textContent = `${money(person.total)} · ${person.durationMinutes} min`;
      article.append(heading, service, addons, total); people.append(article);
    });
    totals.replaceChildren();
    for (const [label, value] of [['Makeup start', slot.start.replace('T', ' ')], ['Combined makeup', `${q.durationMinutes} minutes`], ['Preparation buffer', `${slot.bufferMinutes} minutes`], ['Early fee', money(q.earlyFee)], ['Late fee', money(q.lateFee)], ['Booking total', money(q.finalTotal)], ['Minimum down payment', money(q.requiredDownPayment)]]) {
      const dt = document.createElement('dt'), dd = document.createElement('dd'); dt.textContent = label; dd.textContent = value; totals.append(dt, dd);
    }
    const summary = { name: 'Mixed-service group', people: q.people.length, service: 'Individual prices', 'group-price': q.groupDiscount > 0 ? 'Applied to each person' : 'Not applied', original: money(q.serviceTotal + q.groupDiscount), discount: '−' + money(q.groupDiscount), addons: addOnSummary(q), 'early-fee': money(q.earlyFee), 'late-fee': money(q.lateFee), total: money(q.finalTotal), 'down-payment': money(q.requiredDownPayment), start: slot.start.replace('T', ' '), duration: `${q.durationMinutes} min` };
    const finish = new Date(slot.start + ':00Z'); finish.setUTCMinutes(finish.getUTCMinutes() + q.durationMinutes);
    summary.finish = finish.toISOString().slice(0, 16).replace('T', ' ');
    Object.entries(summary).forEach(([key, value]) => { form.querySelector(`#summary-${key}`).textContent = value; });
    payment();
  };
  const mode = () => {
    const mixed = active();
    form.action = mixed ? form.dataset.mixedCreateUrl : form.dataset.createUrl;
    if (mixed) form.querySelector('.mixed-location').append(...[locationFields, locationMap].filter(Boolean));
    else locationHome.after(...[locationFields, locationMap].filter(Boolean));
    form.querySelector('.availability-picker [name="start"]').disabled = mixed;
    const legacyStatus = form.querySelector('[data-legacy-submission-status]');
    if (legacyStatus) legacyStatus.hidden = mixed;
    render();
  };
  form.querySelector('#mixed-mode').addEventListener('change', mode);
  form.querySelectorAll('[name="bookingType"]').forEach(el => el.addEventListener('change', mode));
  form.addEventListener('mixed-slot-changed', render);
  form.addEventListener('booking-step-shown', render);
  form.addEventListener('change', event => { if (event.target.name === 'paymentChoice') payment(); });
  form.addEventListener('input', event => { if (event.target === deposit) payment(); });
  form.addEventListener('submit', async event => {
    if (!active() || event.defaultPrevented) return;
    event.preventDefault();
    if (submitting) return;
    const slot = form.mixedSlot;
    if (!slot || slot.selection !== JSON.stringify(form.mixedPeople)) { message('Your selection changed. Choose an available makeup start time again.'); return; }
    if (!form.reportValidity()) return;
    submitting = true; form.dataset.submissionState = 'submitting'; form.setAttribute('aria-busy', 'true'); submit.disabled = true;
    message(form.dataset.earlyRequest === 'true' ? 'Sending your early booking request…' : form.dataset.xendit === 'true' ? 'Saving your booking and opening the secure Xendit payment page…' : 'Submitting your booking and payment claim for review…');
    try {
      const response = await fetch(form.dataset.mixedCreateUrl, { method: 'POST', headers: { Accept: 'application/json' }, body: new FormData(form) });
      if (response.redirected || !response.headers.get('content-type')?.includes('application/json')) throw new Error('Your session may have expired. Sign in again before submitting.');
      const result = await response.json();
      if (!response.ok || !result.confirmationUrl) throw new Error(result.error || 'Booking was not submitted. Review your details and retry.');
      location.assign(result.confirmationUrl);
    } catch (error) {
      message(error.message || 'Submission could not be confirmed. Retry with the same form; do not pay again.');
      submitting = false; form.dataset.submissionState = 'idle'; form.setAttribute('aria-busy', 'false'); render();
    }
  });
  mode();
})();
