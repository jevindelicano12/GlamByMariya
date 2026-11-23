(() => {
  const form = document.querySelector('#booking-form');
  if (!form) return;

  const steps = ['look', 'schedule', 'payment'];
  const panels = form.querySelectorAll('.booking-details > .panel');
  if (panels.length !== 3) return;
  panels[0].dataset.bookingStep = 'look';
  panels[1].dataset.bookingStep = 'schedule';
  panels[2].dataset.bookingStep = 'payment';
  form.querySelector('.summary').dataset.bookingStep = 'payment';
  const sections = [...form.querySelectorAll('[data-booking-step]')];
  const progress = form.querySelector('.booking-progress');
  const actions = form.querySelector('.booking-step-actions');
  const back = form.querySelector('[data-step-back]');
  const next = form.querySelector('[data-step-next]');
  const message = form.querySelector('.booking-step-message');
  const start = form.querySelector('[name="start"]');
  const mixed = () => !!form.querySelector('#mixed-mode')?.checked && form.querySelector('[name="bookingType"]:checked')?.value === 'package';
  // The customer's map pin (booking-location.js) must be set before payment and submit.
  const notPinned = () => !!form.elements.latitude && !(form.elements.latitude.value && form.elements.longitude.value);
  let current = 0;
  let navigation = 0;
  let checkingNavigation = false;
  const nextStatus = document.createElement('p');
  nextStatus.className = 'booking-next-status';
  nextStatus.setAttribute('role', 'status');
  actions.insertAdjacentElement('beforebegin', nextStatus);
  const syncNext = () => {
    next.disabled = checkingNavigation || (mixed() ? form.dataset.mixedQuoteState !== 'ready' || (current === 1 && !form.mixedSlot) : form.dataset.priceState !== 'ready');
    nextStatus.textContent = checkingNavigation ? 'Checking your selected time…'
      : form.dataset.priceState !== 'ready' ? document.querySelector('#pricing-status').textContent : '';
    nextStatus.hidden = current === 2 || !nextStatus.textContent;
  };
  form.addEventListener('booking-price-state-changed', syncNext);
  form.addEventListener('booking-price-updated', syncNext);
  form.addEventListener('mixed-quote-state', syncNext);
  form.addEventListener('mixed-selection-invalidated', syncNext);
  form.addEventListener('mixed-slot-changed', syncNext);
  let waitingForTravelAcknowledgement = false;
  const confirmation = document.querySelector('#booking-confirmation');
  const confirmationAck = document.querySelector('#confirmation-ack');
  const confirmationButton = document.querySelector('#confirmation-submit');
  let confirmed = false;
  let confirmationOpener;
  form.addEventListener('input', event => { if (event.target !== confirmationAck) confirmed = false; });
  form.addEventListener('change', () => { confirmed = false; });
  confirmationAck?.addEventListener('change', () => { confirmationButton.disabled = !confirmationAck.checked; });
  document.querySelector('#confirmation-back')?.addEventListener('click', () => confirmation.close());
  confirmation?.addEventListener('close', () => confirmationOpener?.focus());
  confirmationButton?.addEventListener('click', () => {
    if (!confirmationAck.checked) return;
    confirmed = true;
    confirmation.close();
    form.requestSubmit();
  });
  const showConfirmation = submitter => {
    const summary = document.querySelector('#confirmation-summary');
    summary.replaceChildren();
    const value = selector => document.querySelector(selector)?.textContent || '—';
    const fields = [
      ['Makeup package', value('#summary-name')], ['Number of people', value('#summary-people')],
      ['Add-ons and quantities', value('#summary-addons')], ['Makeup start', (mixed() ? form.elements.mixedStart.value : start.value).replace('T', ' ')],
      ['Service location', `${form.querySelector('[name="address"]:not(:disabled)').value}, ${form.querySelector('[name="area"]:not(:disabled)').value}`],
      ...(mixed() ? [] : [['Location', 'Pinned ✓']]),
      ['Estimated makeup duration', value('#summary-duration')], ['Makeup finish', value('#summary-finish')],
      ['Early booking surcharge', value('#summary-early-fee')], ...(document.querySelector('#summary-late-row')?.hidden ? [] : [['Late booking surcharge', value('#summary-late-fee')]]), ['Booking total', value('#summary-total')],
      // An early request charges nothing now: the down payment is paid after the artist approves the time.
      [form.dataset.earlyRequest === 'true' ? 'Down payment (paid after the artist approves)' : 'Down payment to pay now', `₱${form.elements.deposit.value}`],
      ['Balance after your appointment', value('#summary-balance')], ['Transportation fee', 'Separate; paid to the artist on arrival']
    ];
    if (mixed()) form.mixedSlot.quote.people.forEach((person, i) => fields.push([`Person ${i + 1}`, `${person.service.name} · ${Number.isFinite(person.regularPrice) && person.regularPrice > person.service.price ? `Regular ₱${person.regularPrice.toFixed(2)} → Group ₱${person.service.price.toFixed(2)}` : `₱${person.service.price.toFixed(2)}`}; ${person.addOns.map(a => `${a.name} × ${a.quantity} · ₱${(a.price * a.quantity).toFixed(2)}`).join(', ') || 'No add-ons'}; ${person.durationMinutes} min`]));
    for (const [label, text] of fields) { const dt = document.createElement('dt'); const dd = document.createElement('dd'); dt.textContent = label; dd.textContent = text; summary.append(dt, dd); }
    confirmationAck.checked = false; confirmationButton.disabled = true;
    confirmationOpener = submitter || document.activeElement;
    confirmation.showModal();
    confirmationAck.focus();
  };
  const travelModal = document.querySelector('#travel-fee-modal');
  const travelAck = travelModal?.querySelector('[data-travel-ack]');
  const travelCancel = travelModal?.querySelector('[data-travel-cancel]');
  const travelCheckbox = travelModal?.querySelector('#travel-policy-ack');
  const travelAcknowledged = () => travelInput?.value === 'true';
  const travelInput = form.querySelector('[name="travelAcknowledged"]');
  let travelModalReturnFocus = null;
  if (travelInput) { travelInput.disabled = false; travelInput.value = 'false'; }
  const acknowledgeTravel = () => {
    if (!travelCheckbox?.checked) { travelCheckbox?.reportValidity(); return; }
    if (travelInput) travelInput.value = 'true';
    travelModal.hidden = true;
    waitingForTravelAcknowledgement = false;
    setHash(2);
    show(2);
    travelModalReturnFocus?.focus({ preventScroll: true });
  };
  const cancelTravel = () => {
    if (travelCheckbox) travelCheckbox.checked = false;
    if (travelAck) travelAck.disabled = true;
    travelModal.hidden = true;
    waitingForTravelAcknowledgement = false;
    setHash(1, true);
    show(1);
    travelModalReturnFocus?.focus({ preventScroll: true });
  };
  travelAck?.addEventListener('click', acknowledgeTravel);
  travelCancel?.addEventListener('click', cancelTravel);
  travelCheckbox?.addEventListener('change', () => { travelAck.disabled = !travelCheckbox.checked; });
  travelModal?.addEventListener('click', event => { if (event.target === travelModal) cancelTravel(); });
  travelModal?.addEventListener('keydown', event => {
    if (event.key === 'Escape') { event.preventDefault(); cancelTravel(); return; }
    if (event.key !== 'Tab') return;
    const focusable = [...travelModal.querySelectorAll('button, input')].filter(control => !control.disabled);
    if (!focusable.length) return;
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
    else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
  });

  const setHash = (index, replace = false) => {
    const hash = `#${steps[index]}`;
    if (location.hash !== hash) history[replace ? 'replaceState' : 'pushState'](null, '', hash);
  };
  const notify = text => {
    message.textContent = text;
    message.hidden = !text;
  };
  const show = (index, focus = true) => {
    current = index;
    form.dataset.currentStep = steps[index];
    sections.forEach(section => { section.hidden = section.dataset.bookingStep !== steps[index] || (section.id === 'mixed-selection' && !mixed()) || (section.id === 'mixed-calendar' && !mixed()) || (mixed() && (section.classList.contains('extras-panel') || section === panels[1])); });
    progress.querySelectorAll('a').forEach((link, i) => {
      if (i === index) link.setAttribute('aria-current', 'step');
      else link.removeAttribute('aria-current');
      link.classList.toggle('is-complete', i < index);
    });
    back.hidden = index === 0;
    next.hidden = index === 2 || (mixed() && !form.dataset.mixedCreateUrl && index === 1);
    const estimate = form.querySelector('#mixed-estimate-inline'); if (estimate) estimate.hidden = !mixed() || index !== 0;
    syncNext();
    next.textContent = index === 0 ? 'Next: your moment →' : 'Next: payment & review →';
    form.querySelector('[data-step-count]').textContent = `Step ${index + 1} of 3`;
    form.dispatchEvent(new Event('booking-step-shown'));
    if (focus) {
      const heading = sections.find(section => !section.hidden).querySelector('h2, h3');
      heading.tabIndex = -1;
      heading.focus({ preventScroll: true });
      progress.scrollIntoView({ block: 'start', behavior: 'auto' });
    }
  };
  const validate = index => {
    const controls = sections.filter(section => !section.hidden && section.dataset.bookingStep === steps[index])
      .flatMap(section => [...section.querySelectorAll('input, select, textarea')]);
    const invalid = controls.find(control => control.willValidate && !control.validity.valid);
    if (invalid) {
      show(index, false);
      setHash(index, true);
      invalid.reportValidity();
      return false;
    }
    if (index === 0 && !form.querySelector('[name="serviceId"]:checked')) {
      notify('Choose a look before continuing.');
      return false;
    }
    return true;
  };
  const slotParams = () => {
    const params = new URLSearchParams({ start: start.value });
    const packageType = form.elements.package.value === 'group' ? 'package' : 'individual';
    const quantity = window.BookingQuantity.forBookingType(packageType, form.elements.people.value);
    if (!quantity.valid) throw new Error(quantity.message);
    params.set('people', String(quantity.people));
    params.set('package', quantity.package);
    const service = form.querySelector('[name="serviceId"]:checked');
    if (service) params.set('serviceId', service.value);
    form.querySelectorAll('input[name="addOnIds"]:checked, input[type="hidden"][name="addOnIds"]').forEach(input => params.append('addOnIds', input.value));
    const pinLat = form.elements.latitude?.value, pinLng = form.elements.longitude?.value;
    if (pinLat && pinLng) { params.set('latitude', pinLat); params.set('longitude', pinLng); }
    return params;
  };
  const navigate = async (target, fromHistory = false) => {
    if (form.dataset.submissionState === 'submitting') return;
    // Older running previews have no mixed submission endpoint in their compiled view.
    if (mixed() && !form.dataset.mixedCreateUrl && target > 1) { notify('Mixed-service payment is not available in this preview.'); return; }
    if (mixed() && target === 2) {
      if (notPinned()) {
        notify('Pin your exact location inside Metro Manila or Rizal.');
        document.querySelector('#booking-location-map')?.scrollIntoView({ block: 'center' });
        return;
      }
      if (current !== 1 || !form.mixedSlot || form.mixedSlot.selection !== JSON.stringify(form.mixedPeople)) { notify('Choose an available makeup start time before continuing.'); return; }
      if (!validate(1)) return;
      if (!travelAcknowledged()) { travelModalReturnFocus = next; travelModal.hidden = false; travelModal.focus(); return; }
      setHash(2, fromHistory); show(2); return;
    }
    if (mixed() && target === 1 && current === 0) {
      if (form.dataset.mixedQuoteState !== 'ready') { notify('Choose a valid service for every person and wait for the estimate.'); return; }
      setHash(1, fromHistory); show(1); return;
    }
    form.dispatchEvent(new Event('booking-submission-cancel'));
    const version = ++navigation;
    checkingNavigation = false;
    syncNext();
    notify('');
    if (target > current + 1) {
      setHash(current, true);
      notify('Continue through each step to complete your booking.');
      return;
    }
    if (target > current) {
      if (form.dataset.priceState !== 'ready') {
        setHash(current, true);
        notify(form.dataset.priceState === 'loading' ? 'Wait for a valid price and down payment calculation before continuing.' : document.querySelector('#pricing-status').textContent || 'Unable to calculate your price. Please check your selection.');
        return;
      }
      if (!validate(0) || (target === 2 && !validate(1))) {
        setHash(current, true);
        return;
      }
      if (target === 2) {
        if (!start.value) {
          setHash(current, true);
          notify('Choose an available date and time before continuing.');
          const picker = form.querySelector('.availability-picker');
          picker.tabIndex = -1;
          picker.focus();
          return;
        }
        if (notPinned()) {
          setHash(current, true);
          notify('Pin your exact location inside Metro Manila or Rizal.');
          document.querySelector('#booking-location-map')?.scrollIntoView({ block: 'center' });
          return;
        }
        const params = slotParams();
        checkingNavigation = true;
        syncNext();
        next.textContent = 'Checking your time…';
        try {
          const validationUrl = form.querySelector('.availability-picker').dataset.validationUrl;
          const response = await fetch(`${validationUrl}?${params}`, {
            cache: 'no-store', headers: { Accept: 'application/json' }
          });
          if (response.redirected || !response.headers.get('content-type')?.includes('application/json')) {
            throw new Error('Your session may have expired. Sign in again to check availability.');
          }
          const result = await response.json();
          if (version !== navigation) return;
          if (params.toString() !== slotParams().toString()) throw new Error('Your selection changed. Check your date and time again.');
          if (!response.ok || !result.available) throw new Error(result.error || result.message || 'Please choose another available time.');
          if (!validate(1)) return;
        } catch (error) {
          if (version === navigation) {
            setHash(current, true);
            notify(error.message || 'Availability could not be checked. Please try again.');
          }
          return;
        } finally {
          if (version === navigation) {
            checkingNavigation = false;
            syncNext();
            next.textContent = 'Next: payment & review →';
          }
        }
      }
    }
    if (version !== navigation) return;
    if (target === 2 && current === 1 && travelModal && !travelAcknowledged()) {
       waitingForTravelAcknowledgement = true;
       travelModalReturnFocus = next;
       if (travelCheckbox) travelCheckbox.checked = false;
       if (travelAck) travelAck.disabled = true;
       travelModal.hidden = false;
       travelModal.focus();
       return;
    }
    setHash(target, fromHistory);
    show(target);
  };

  next.addEventListener('click', () => navigate(current + 1));
  back.addEventListener('click', () => navigate(current - 1));
  progress.addEventListener('click', event => {
    const link = event.target.closest('a');
    if (!link) return;
    event.preventDefault();
    navigate(steps.indexOf(link.hash.slice(1)));
  });
  window.addEventListener('hashchange', () => {
    const target = steps.indexOf(location.hash.slice(1));
    navigate(target < 0 ? 0 : target, true);
  });

  // Reveal the first invalid field synchronously, before native validation focuses it.
  let revealingInvalid = false;
  form.addEventListener('invalid', event => {
    if (revealingInvalid) return;
    const section = event.target.closest('[data-booking-step]');
    if (!section) return;
    revealingInvalid = true;
    ++navigation;
    checkingNavigation = false;
    syncNext();
    const index = steps.indexOf(section.dataset.bookingStep);
    show(index, false);
    setHash(index, true);
    const field = event.target;
    message.dataset.invalidField = field.name;
    notify(field.name === 'deposit' ? `Enter a payment between ₱${field.min} and ₱${field.max}, with up to two decimal places.`
      : field.name === 'proof' ? 'Upload a payment screenshot before submitting.'
      : field.name === 'agreed' ? 'Agree to the booking policies before submitting.'
      : field.name === 'reference' ? 'Enter your payment reference before submitting.'
      : 'Check the highlighted fields before submitting.');
    queueMicrotask(() => { revealingInvalid = false; });
  }, true);
  const clearCorrectedError = event => {
    const name = event.target.name === 'paymentChoice' ? 'deposit' : event.target.name;
    if (message.dataset.invalidField === name && form.elements[name]?.validity.valid) {
      delete message.dataset.invalidField;
      notify('');
    }
  };
  form.addEventListener('input', clearCorrectedError);
  form.addEventListener('change', clearCorrectedError);
  form.addEventListener('keydown', event => {
    if (current < 2 && event.key === 'Enter' && event.target.matches('input:not([type="file"]), select')) {
      event.preventDefault();
      if (!next.disabled) navigate(current + 1);
    }
  });
  form.addEventListener('submit', event => {
    if (mixed() && !form.dataset.mixedCreateUrl) { event.preventDefault(); event.stopImmediatePropagation(); notify('Mixed-service payment is not available in this preview.'); return; }
    if (current < 2) {
      event.preventDefault();
      event.stopImmediatePropagation();
      navigate(current + 1);
    } else if (!travelAcknowledged()) {
      event.preventDefault();
      event.stopImmediatePropagation();
      travelModalReturnFocus = form.querySelector('[data-step-next]');
      if (travelCheckbox) travelCheckbox.checked = false;
      if (travelAck) travelAck.disabled = true;
      travelModal.hidden = false;
      travelModal.focus();
    } else if (!(mixed() ? form.elements.mixedStart.value : start.value)) {
      event.preventDefault();
      event.stopImmediatePropagation();
      ++navigation;
      show(1);
      setHash(1, true);
      notify('Choose an available date and time before submitting.');
    } else if (notPinned()) {
      event.preventDefault();
      event.stopImmediatePropagation();
      ++navigation;
      show(1);
      setHash(1, true);
      notify('Pin your exact location inside Metro Manila or Rizal.');
    } else if (!confirmed && confirmation?.showModal) {
      event.preventDefault();
      event.stopImmediatePropagation();
      showConfirmation(event.submitter);
    }
  }, true);
  form.querySelector('#mixed-mode')?.addEventListener('change', () => show(current, false));
  form.querySelectorAll('[name="bookingType"]').forEach(input => input.addEventListener('change', () => show(current, false)));
  // site.js owns the validation/retry/duplicate lock; keep errors visible on payment.
  // Once a valid pin is set, drop the stale pin reminder.
  form.addEventListener('booking-location-changed', () => {
    if (!notPinned() && message.textContent === 'Pin your exact location inside Metro Manila or Rizal.') notify('');
  });
  form.addEventListener('booking-submission-message', event => notify(event.detail.message));

  form.classList.add('has-booking-steps');
  progress.hidden = false;
  actions.hidden = false;
  // A fresh page has not yet validated any step, even when opened with a deep link.
  setHash(0, true);
  show(0, false);
})();
