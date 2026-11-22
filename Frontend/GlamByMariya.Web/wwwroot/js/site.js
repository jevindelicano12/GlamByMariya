// Send each form once. A double click or a second Enter used to post sign-in and sign-up twice:
// the second sign-in failed its antiforgery check (HTTP 400) and the second sign-up showed the
// empty form again. Runs on document, after a form's own handler, so a submit that handler
// cancelled (the booking form checks availability first) is not counted. Opt out with
// data-submit-guard="off"; forms that open a new tab are left alone.
document.addEventListener('submit', event => {
  const form = event.target;
  if (!(form instanceof HTMLFormElement) || event.defaultPrevented) return;
  if (form.dataset.submitGuard === 'off' || (form.target && form.target !== '_self')) return;
  if (form.dataset.submitGuard === 'sent') { event.preventDefault(); return; }
  form.dataset.submitGuard = 'sent';
  form.setAttribute('aria-busy', 'true');
  // A download or a stalled request must not lock the form for good.
  window.setTimeout(() => { delete form.dataset.submitGuard; form.removeAttribute('aria-busy'); }, 15000);
});
// Coming back with the browser's Back button restores the page as it was; let it submit again.
window.addEventListener('pageshow', event => {
  if (event.persisted) document.querySelectorAll('form[data-submit-guard="sent"]').forEach(form => { delete form.dataset.submitGuard; form.removeAttribute('aria-busy'); });
});

// Enhance every password field once, including reset and account settings forms.
document.querySelectorAll('input[type="password"]').forEach((input, index) => {
  if (!input.id) input.id = `studio-password-${index}`;
  let wrapper = input.closest('.password-field');
  if (!wrapper) {
    wrapper = document.createElement('span');
    wrapper.className = 'password-field';
    input.before(wrapper);
    wrapper.append(input);
  }
  if (!wrapper.querySelector('[data-password-toggle]')) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'password-toggle';
    button.dataset.passwordToggle = input.id;
    wrapper.append(button);
  }
});
document.querySelectorAll('[data-password-toggle]').forEach(toggle => {
  const input = document.getElementById(toggle.dataset.passwordToggle);
  if (!input) return;
  if (toggle.dataset.initialized) return;
  toggle.dataset.initialized = 'true';
  toggle.setAttribute('aria-controls', input.id);
  // One SVG avoids overlapping open/closed icons and conflicting display rules.
  const paint = () => {
    const masked = input.type === 'password';
    toggle.innerHTML = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12Z"/><circle cx="12" cy="12" r="3"/>${masked ? '' : '<path d="m3 3 18 18"/>'}</svg>`;
    toggle.setAttribute('aria-label', masked ? 'Show password' : 'Hide password');
    toggle.setAttribute('aria-pressed', String(!masked));
    toggle.title = masked ? 'Show password' : 'Hide password';
  };
  toggle.addEventListener('click', () => {
    input.type = input.type === 'password' ? 'text' : 'password';
    paint();
  });
  paint();
});
document.querySelectorAll('.account-menu').forEach(menu => {
  const summary = menu.querySelector('summary');
  const panel = menu.querySelector('.account-menu-panel');
  let closing = null;
  const close = () => {
    if (!menu.open || closing) return;
    if (!panel?.animate || matchMedia('(prefers-reduced-motion: reduce)').matches) { menu.open = false; return; }
    closing = panel.animate([{ opacity: 1, transform: 'translateY(0)' }, { opacity: 0, transform: 'translateY(-4px)' }],
      { duration: 120, easing: 'ease-in', fill: 'forwards' });
    closing.finished.then(() => { menu.open = false; closing = null; }).catch(() => {});
  };
  summary?.addEventListener('click', event => {
    if (closing) { event.preventDefault(); closing.cancel(); closing = null; }
    else if (menu.open) { event.preventDefault(); close(); }
  });
  document.addEventListener('click', event => {
    if (!menu.contains(event.target)) close();
  });
  menu.addEventListener('keydown', event => {
    if (event.key === 'Escape') { close(); summary?.focus(); }
  });
});
const customerFilters = document.querySelector('[data-customer-filters]');
if (customerFilters) {
  const cards = [...document.querySelectorAll('[data-customer-appointment]')];
  const count = document.querySelector('[data-customer-filter-count]');
  customerFilters.querySelectorAll('button').forEach(button => button.addEventListener('click', () => {
    let shown = 0;
    cards.forEach(card => { const visible = button.dataset.filter === 'all' || card.dataset.customerAppointment === button.dataset.filter; card.hidden = !visible; if (visible) shown++; });
    customerFilters.querySelectorAll('button').forEach(item => item.setAttribute('aria-pressed', String(item === button)));
    count.textContent = `${shown} appointment(s) shown`;
  }));
  customerFilters.hidden = false;
}
document.querySelectorAll('.catalog-image-block input[type="file"]').forEach(input => {
  const preview = document.createElement('img');
  preview.className = 'catalog-selected-preview';
  preview.alt = 'Selected catalog image preview';
  preview.hidden = true;
  input.parentElement.appendChild(preview);
  let current;
  input.addEventListener('change', () => {
    if (current) URL.revokeObjectURL(current);
    current = null;
    const file = input.files[0];
    if (!file || !['image/jpeg', 'image/png'].includes(file.type) || file.size > 5000000) { preview.hidden = true; preview.removeAttribute('src'); return; }
    current = URL.createObjectURL(file); preview.src = current; preview.hidden = false;
  });
});
const appointmentSearch = document.querySelector('[data-appointment-search]');
if (appointmentSearch) {
  const rows = [...document.querySelectorAll('[data-appointment-row]')];
  const search = appointmentSearch.querySelector('[data-search]');
  const from = appointmentSearch.querySelector('[data-from]');
  const to = appointmentSearch.querySelector('[data-to]');
  const count = document.querySelector('[data-appointment-count]');
  const filter = () => {
    const query = search.value.trim().toLocaleLowerCase();
    let visible = 0;
    rows.forEach(row => {
      const match = row.dataset.searchText.toLocaleLowerCase().includes(query) &&
        (!from.value || row.dataset.date >= from.value) && (!to.value || row.dataset.date <= to.value);
      row.hidden = !match;
      if (match) visible++;
    });
    count.textContent = `${visible} appointment(s) shown`;
  };
  [search, from, to].forEach(input => input.addEventListener('input', filter));
  appointmentSearch.querySelector('[data-clear]').addEventListener('click', () => { search.value = from.value = to.value = ''; filter(); });
  appointmentSearch.hidden = false;
}
const staffMenu = document.querySelector('#staff-mobile-menu');
if (staffMenu) {
  const mobile = matchMedia('(max-width: 850px)');
  const sidebar = document.querySelector('#studio-sidebar');
  const nav = staffMenu.querySelector('nav');
  const saveScroll = (element, key) => { try { sessionStorage.setItem(key, String(element.scrollTop)); } catch { } };
  const restoreScroll = (element, key) => {
    try { const saved = sessionStorage.getItem(key); if (saved !== null) element.scrollTop = Math.max(0, Number(saved) || 0); } catch { }
    const active = nav.querySelector('a[aria-current="page"]');
    if (!active) return;
    const bounds = element.getBoundingClientRect(), link = active.getBoundingClientRect();
    if (link.top < bounds.top) element.scrollTop += link.top - bounds.top;
    else if (link.bottom > bounds.bottom) element.scrollTop += link.bottom - bounds.bottom;
  };
  sidebar.addEventListener('scroll', () => { if (!mobile.matches) saveScroll(sidebar, 'glam-staff-sidebar-scroll'); }, { passive: true });
  nav.addEventListener('scroll', () => { if (mobile.matches && staffMenu.open) saveScroll(nav, 'glam-staff-menu-scroll'); }, { passive: true });
  staffMenu.addEventListener('toggle', () => { if (mobile.matches && staffMenu.open) requestAnimationFrame(() => restoreScroll(nav, 'glam-staff-menu-scroll')); });
  window.addEventListener('pagehide', () => { if (!mobile.matches) saveScroll(sidebar, 'glam-staff-sidebar-scroll'); });
  window.addEventListener('pageshow', () => { if (!mobile.matches) requestAnimationFrame(() => restoreScroll(sidebar, 'glam-staff-sidebar-scroll')); });
  const syncMenu = () => { staffMenu.open = !mobile.matches; };
  mobile.addEventListener('change', syncMenu);
  syncMenu();
}
const sidebarToggle = document.querySelector('#sidebar-toggle');
if (sidebarToggle) {
  document.querySelectorAll('.sidebar nav a').forEach(link => {
    link.title = link.textContent.trim();
    link.setAttribute('aria-label', link.textContent.trim());
  });
  const syncSidebar = () => {
    const collapsed = document.body.classList.contains('sidebar-collapsed');
    sidebarToggle.setAttribute('aria-expanded', String(!collapsed));
    sidebarToggle.setAttribute('aria-label', collapsed ? 'Expand sidebar' : 'Collapse sidebar');
    sidebarToggle.title = collapsed ? 'Expand sidebar' : 'Collapse sidebar';
  };
  sidebarToggle.addEventListener('click', () => {
    if (matchMedia('(max-width: 850px)').matches) { staffMenu.open = !staffMenu.open; return; }
    document.body.classList.toggle('sidebar-collapsed');
    try { localStorage.setItem('lumiere-sidebar', document.body.classList.contains('sidebar-collapsed') ? 'collapsed' : 'expanded'); } catch { }
    syncSidebar();
  });
  syncSidebar();
}
const bookingForm = document.querySelector('#booking-form');
if (bookingForm) {
  const paymentMethod = bookingForm.querySelector('[name="method"]');
  const money = value => new Intl.NumberFormat('en-PH', { style: 'currency', currency: 'PHP', minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(value);
  const peopleInput = bookingForm.elements.people;
  const packageField = bookingForm.elements.package;
  const packagePeopleField = bookingForm.querySelector('.package-people-field');
  const packagePeopleSlot = bookingForm.querySelector('.package-people-slot');
  const deposit = document.querySelector('#deposit');
  const flexible = bookingForm.dataset.flexible === 'true';
  const addonValues = bookingForm.querySelector('#addon-values');
  const addonQuantities = [...bookingForm.querySelectorAll('.addon-quantity')];
  const addonFields = addonQuantities.map((input, index) => {
    let card = input.closest('.choice.addon');
    let controls = input.closest('.addon-quantity-control');
    if (!controls) {
      const replacement = document.createElement('div');
      replacement.className = card.className;
      replacement.dataset.enhancedLegacy = 'true';
      card.replaceWith(replacement);
      while (card.firstChild) replacement.append(card.firstChild);
      card = replacement;
      controls = document.createElement('div'); controls.className = 'addon-quantity-control';
      const label = document.createElement('label'); label.textContent = 'Quantity';
      input.id = input.id || `addon-quantity-${index}`; label.htmlFor = input.id;
      const stepper = document.createElement('div'); stepper.className = 'addon-stepper';
      for (const [direction, symbol, action] of [['down', '−', 'Decrease'], ['up', '+', 'Increase']]) {
        const button = document.createElement('button'); button.type = 'button'; button.dataset.quantityStep = direction;
        button.setAttribute('aria-label', `${action} quantity for ${input.dataset.name}`); button.textContent = symbol;
        stepper.append(button); if (direction === 'down') stepper.append(input);
      }
      controls.append(label, stepper); card.append(controls);
    }
    const hint = document.createElement('span'); hint.className = 'addon-limit'; hint.id = `addon-limit-${index}`;
    const error = document.createElement('span'); error.className = 'addon-error'; error.id = `addon-error-${index}`; error.setAttribute('role', 'status');
    const subtotal = document.createElement('span'); subtotal.className = 'addon-subtotal';
    controls.append(hint, error); card.append(subtotal);
    input.setAttribute('aria-describedby', [input.getAttribute('aria-describedby'), hint.id, error.id].filter(Boolean).join(' '));
    input.disabled = false;
    return { input, controls, hint, error, subtotal };
  });
  const addonTotal = document.createElement('p'); addonTotal.className = 'addon-total';
  if (addonFields.length) bookingForm.querySelector('.extras-panel').append(addonTotal);
  let currentQuote = null;
  const updatePayment = () => {
    if (!currentQuote || !flexible) return;
    const choice = bookingForm.querySelector('[name="paymentChoice"]:checked')?.value;
    deposit.min = Number(currentQuote.requiredDownPayment).toFixed(2);
    deposit.max = Number(currentQuote.finalTotal).toFixed(2);
    deposit.readOnly = choice !== 'custom';
    if (choice === 'minimum') deposit.value = deposit.min;
    if (choice === 'full') deposit.value = deposit.max;
    const value = Number(deposit.value);
    document.querySelector('#summary-balance').textContent = deposit.value && deposit.validity.valid ? money(Math.max(0, Number(currentQuote.finalTotal) - value)) + ' after claimed payment' : '—';
  };
  const syncAddons = people => {
    if (!flexible) return;
    addonValues.replaceChildren();
    for (const { input, controls, hint, error, subtotal } of addonFields) {
      input.max = String(people);
      const count = Number(input.value);
      const valid = input.value !== '' && input.validity.valid && Number.isInteger(count) && count >= 0 && count <= people;
      hint.textContent = `Maximum: ${people} ${people === 1 ? 'person' : 'people'}`;
      const text = valid ? '' : input.value !== '' && Number.isInteger(count) && count > people
        ? `You selected ${input.value} ${input.dataset.name} add-ons, but your booking has only ${people} ${people === 1 ? 'person' : 'people'}. Choose ${people} or fewer.`
        : `Enter a whole number from 0 to ${people} for ${input.dataset.name}.`;
      if (error.textContent !== text) error.textContent = text;
      input.setAttribute('aria-invalid', String(!valid));
      subtotal.textContent = valid ? `Subtotal: ${money(Number(input.dataset.price) * count)}` : 'Subtotal unavailable';
      controls.querySelector('[data-quantity-step="down"]').disabled = input.value === '' || !Number.isInteger(count) || count <= 0;
      controls.querySelector('[data-quantity-step="up"]').disabled = input.value === '' || !Number.isInteger(count) || count < 0 || count >= people;
      if (!valid) continue;
      for (let i = 0; i < count; i++) {
        const hidden = document.createElement('input'); hidden.type = 'hidden'; hidden.name = 'addOnIds'; hidden.value = input.dataset.addonId; addonValues.append(hidden);
      }
    }
  };
  bookingForm.addEventListener('click', event => {
    const button = event.target.closest('[data-quantity-step]');
    if (!button || button.disabled) return;
    const input = button.closest('.addon-quantity-control').querySelector('.addon-quantity');
    input.value = String(Number(input.value) + (button.dataset.quantityStep === 'up' ? 1 : -1));
    input.dispatchEvent(new Event('input', { bubbles: true }));
    input.dispatchEvent(new Event('change', { bubbles: true }));
  });
  const selectedAddons = () => bookingForm.querySelectorAll('input[name="addOnIds"]:checked, input[type="hidden"][name="addOnIds"]');
  const updateMakeupTimes = () => {
    const start = bookingForm.querySelector('[name="start"]')?.value || '';
    const minutes = Number(bookingForm.dataset.serviceMinutes);
    const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/.exec(start);
    document.querySelector('#summary-start').textContent = match ? start.replace('T', ' ') : 'Choose a time';
    if (!match || !Number.isInteger(minutes) || minutes <= 0 || bookingForm.dataset.priceState !== 'ready') {
      document.querySelector('#summary-finish').textContent = 'Unavailable'; return;
    }
    // Wall-clock arithmetic: do not convert the studio time to the browser's zone.
    const finish = new Date(Date.UTC(+match[1], +match[2] - 1, +match[3], +match[4], +match[5] + minutes));
    document.querySelector('#summary-finish').textContent = Number.isNaN(finish.getTime()) ? 'Unavailable' : finish.toISOString().slice(0, 16).replace('T', ' ');
  };
  bookingForm.querySelector('[name="start"]')?.addEventListener('change', updateMakeupTimes);
  bookingForm.addEventListener('booking-price-updated', updateMakeupTimes);
  const pricingStatus = document.querySelector('#pricing-status');
  const submitButton = bookingForm.querySelector('[type="submit"]');
  let pricingVersion = 0;
  let pricingRequest;
  const update = async () => {
    if (bookingForm.querySelector('#mixed-mode')?.checked && bookingForm.querySelector('[name="bookingType"]:checked')?.value === 'package') {
      ++pricingVersion; pricingRequest?.abort(); currentQuote = null;
      if (bookingForm.mixedSlot) return;
      bookingForm.elements.expectedTotal.value = ''; bookingForm.elements.expectedMinimum.value = '';
      peopleInput.setCustomValidity('');
      bookingForm.dataset.priceState = 'invalid'; submitButton.disabled = true;
      pricingStatus.textContent = '';
      bookingForm.dispatchEvent(new Event('booking-price-state-changed'));
      return;
    }
    const service = bookingForm.querySelector('[name="serviceId"]:checked');
    if (!service) return;
    document.querySelector('#selected-look-name').textContent = service.dataset.name;
    document.querySelector('#look-selection-caption').textContent = `${service.dataset.name} · ${service.dataset.minutes} minutes`;
    const bookingType = bookingForm.querySelector('[name="bookingType"]:checked')?.value || 'individual';
    const quantity = window.BookingQuantity.forBookingType(bookingType, peopleInput.value);
    if (quantity.package === 'auto') {
      peopleInput.value = '1';
      peopleInput.setCustomValidity('');
      packageField.value = 'auto';
    } else {
      packageField.value = 'group';
      if (!quantity.valid) {
        pricingVersion++;
        pricingRequest?.abort();
        pricingRequest = null;
        currentQuote = null;
        bookingForm.elements.expectedTotal.value = '';
        bookingForm.elements.expectedMinimum.value = '';
        peopleInput.setCustomValidity(quantity.message);
        bookingForm.dataset.priceState = 'invalid';
        submitButton.disabled = true;
        deposit.value = '';
        pricingStatus.textContent = quantity.message;
        bookingForm.querySelectorAll('.summary-row strong').forEach(el => { el.textContent = '—'; });
        bookingForm.dispatchEvent(new Event('booking-price-state-changed'));
        return;
      }
      peopleInput.setCustomValidity('');
    }
    syncAddons(quantity.people);
    const invalidAddon = addonFields.find(field => field.input.getAttribute('aria-invalid') === 'true');
    if (invalidAddon) { pricingVersion++; pricingRequest?.abort(); currentQuote = null; bookingForm.dataset.priceState = 'invalid'; submitButton.disabled = true; bookingForm.elements.expectedTotal.value = ''; bookingForm.elements.expectedMinimum.value = ''; addonTotal.textContent = 'Add-on total unavailable until quantities are corrected.'; pricingStatus.textContent = invalidAddon.error.textContent; bookingForm.dispatchEvent(new Event('booking-price-state-changed')); return; }
    const version = ++pricingVersion;
    pricingRequest?.abort();
    pricingRequest = new AbortController();
    bookingForm.dataset.priceState = 'loading';
    bookingForm.elements.expectedTotal.value = '';
    bookingForm.elements.expectedMinimum.value = '';
    updateMakeupTimes();
    submitButton.disabled = true;
    pricingStatus.textContent = 'Calculating your discounted price and down payment…';
    if (addonFields.length) addonTotal.textContent = 'Calculating add-on total…';
    bookingForm.dispatchEvent(new Event('booking-price-state-changed'));
    try {
      const params = new URLSearchParams({ serviceId: service.value, people: String(quantity.people), package: quantity.package });
      if (bookingForm.elements.start.value) params.set('start', bookingForm.elements.start.value);
      selectedAddons().forEach(a => params.append('addOnIds', a.value));
      const response = await fetch(`${bookingForm.dataset.quoteUrl}?${params}`, { cache: 'no-store', signal: pricingRequest.signal, headers: { Accept: 'application/json' } });
      if (response.redirected || !response.headers.get('content-type')?.includes('application/json')) throw new Error('Sign in again to calculate your price.');
      const quote = await response.json();
      if (!response.ok) throw new Error(quote.error || 'Unable to calculate your price.');
      if (version !== pricingVersion) return;
      const values = { '#summary-name': quote.service, '#summary-people': quote.people,
        '#summary-service': money(quote.regularPrice), '#summary-group-price': quote.groupPrice == null ? 'Not offered' : money(quote.groupPrice) + ` (${quote.groupMinimumPeople}+ people)`,
        '#summary-original': money(quote.originalTotal), '#summary-discount': '−' + money(quote.groupDiscount),
        '#summary-addons': money(quote.extrasTotal), '#summary-early-fee': money(quote.earlyFee), '#summary-late-fee': money(quote.lateFee),
        '#summary-total': money(quote.finalTotal), '#summary-down-payment': money(quote.requiredDownPayment),
        '#summary-balance': money(quote.remainingBalance), '#summary-duration': quote.durationMinutes + ' min' };
      Object.entries(values).forEach(([selector, value]) => { document.querySelector(selector).textContent = value; });
      currentQuote = quote;
      if (addonFields.length) addonTotal.textContent = `Add-on total: ${money(quote.extrasTotal)}`;
      bookingForm.elements.expectedTotal.value = Number(quote.finalTotal).toFixed(2);
      bookingForm.elements.expectedMinimum.value = Number(quote.requiredDownPayment).toFixed(2);
      bookingForm.dataset.serviceMinutes = String(quote.durationMinutes);
      if (flexible) updatePayment(); else deposit.value = Number(quote.requiredDownPayment).toFixed(2);
      if (flexible) document.querySelector('#summary-addons').textContent = addonQuantities.filter(a => Number(a.value) > 0).map(a => `${a.dataset.name} × ${a.value}: ${money(Number(a.dataset.price) * Number(a.value))}`).join(', ') || 'None';
      else document.querySelector('#summary-addons').textContent = [...bookingForm.querySelectorAll('[name="addOnIds"]:checked')].map(a => `${a.closest('label').querySelector('strong').textContent} × 1: ${money(Number(a.dataset.price))}`).join(', ') || 'None';
      document.querySelector('#look-selection-caption').textContent = `${quote.service} · ${quote.people} people · ${quote.durationMinutes} minutes`;
      pricingStatus.textContent = `Final total ${money(quote.finalTotal)} · Pay ${quote.downPaymentPercent}%: ${money(quote.requiredDownPayment)} · Balance: ${money(quote.remainingBalance)}`;
      bookingForm.dataset.priceState = 'ready';
      bookingForm.dispatchEvent(new Event('booking-price-updated'));
    } catch (error) {
      if (version === pricingVersion && error.name !== 'AbortError') {
        bookingForm.dataset.priceState = 'error';
        const noGroupRate = error.message === 'This service does not have a package rate configured.';
        // A group too long for the artist's day is a choice to change, not a hiccup to retry.
        const tooLongForDay = /longer than the artist's day/.test(error.message || '');
        pricingStatus.textContent = noGroupRate
          ? `Group pricing is not available for ${service.dataset.name}. Choose Individual Booking for one person, choose a service with a group rate, or contact the artist.`
          : tooLongForDay ? error.message
          : `Pricing unavailable: ${error.message} Please try again.`;
        if (addonFields.length) addonTotal.textContent = noGroupRate ? 'Add-on total is unavailable until a valid booking price is available.' : 'Add-on total unavailable. Please retry the quote.';
        bookingForm.dispatchEvent(new Event('booking-price-state-changed'));
        // Do not leave an old quote displayed as if it applied to the new selection.
        bookingForm.querySelectorAll('.summary-row strong').forEach(el => { el.textContent = '—'; });
      }
    }
  };
  const pricingInput = '[name="serviceId"], [name="addOnIds"], [name="package"], [name="bookingType"]';
  bookingForm.addEventListener('change', event => {
    if (event.target.name === 'paymentChoice') { updatePayment(); return; }
    if (event.target.classList.contains('addon-quantity')) { update(); return; }
    if (event.target.name === 'bookingType') return; // syncBookingType performs the single mode/price update below.
    if (event.target.name === 'start' || event.target.matches(pricingInput)) update();
  });
  bookingForm.addEventListener('input', event => { if (event.target.name === 'people' || event.target.classList.contains('addon-quantity')) update(); if (event.target === deposit) updatePayment(); });
  const bookingTypeInputs = bookingForm.querySelectorAll('[name="bookingType"]');
  bookingForm.querySelector('#mixed-mode')?.addEventListener('change', () => {
    addonQuantities.forEach(input => { input.value = '0'; });
    bookingForm.querySelectorAll('input[type="checkbox"][name="addOnIds"]').forEach(input => { input.checked = false; });
    addonValues.replaceChildren();
    update();
  });
  const mixedLink = bookingForm.querySelector('#mixed-selection-link');
  if (mixedLink) {
    const base = mixedLink.href;
    const updateMixedLink = () => { const url = new URL(base); if (/^[0-9]{1,2}$/.test(peopleInput.value) && Number(peopleInput.value) >= 2 && Number(peopleInput.value) <= 20) url.searchParams.set('people', peopleInput.value); mixedLink.href = url.href; };
    peopleInput.addEventListener('input', updateMixedLink);
    bookingForm.querySelectorAll('[name="bookingType"]').forEach(input => input.addEventListener('change', () => queueMicrotask(updateMixedLink)));
    updateMixedLink();
  }
  let wasPackage = false;
  let bookingTypeInitialized = false;
  let lastPackagePeople = '';
  const typeNotice = document.createElement('p');
  typeNotice.className = 'help';
  typeNotice.setAttribute('role', 'status');
  bookingForm.querySelector('.booking-type').insertAdjacentElement('afterend', typeNotice);
  peopleInput.addEventListener('focus', () => {
    if (!peopleInput.disabled && peopleInput.value) requestAnimationFrame(() => peopleInput.select());
  });
  const syncBookingType = () => {
    const isPackage = bookingForm.querySelector('[name="bookingType"]:checked')?.value === 'package';
    if (bookingTypeInitialized && isPackage !== wasPackage) {
      addonQuantities.forEach(input => { input.value = '0'; });
      bookingForm.querySelectorAll('input[type="checkbox"][name="addOnIds"]').forEach(input => { input.checked = false; });
      typeNotice.textContent = 'Your add-ons were reset because you changed your booking type.';
    }
    if (isPackage && !wasPackage) peopleInput.value = lastPackagePeople;
    if (!isPackage && wasPackage) {
      const previousPackageQuantity = window.BookingQuantity.parse(peopleInput.value);
      if (previousPackageQuantity.valid) lastPackagePeople = String(previousPackageQuantity.people);
    }
    packagePeopleField.hidden = !isPackage;
    packagePeopleSlot.classList.toggle('is-package', isPackage);
    packagePeopleField.setAttribute('aria-hidden', String(!isPackage));
    peopleInput.required = isPackage;
    peopleInput.disabled = !isPackage;
    if (!isPackage) peopleInput.value = '1';
    wasPackage = isPackage;
    bookingTypeInitialized = true;
    update();
  };
  bookingTypeInputs.forEach(input => input.addEventListener('change', syncBookingType));
  window.addEventListener('focus', update);
  syncBookingType();
}

document.querySelectorAll('.availability-picker').forEach(picker => {
  const form = picker.closest('form');
  const calendar = picker.querySelector('.availability-calendar');
  const startInput = picker.querySelector('[name="start"]');
  const submit = form.querySelector('button[type="submit"]') || form.querySelector('button:not([type="button"])');
  const daysElement = picker.querySelector('[data-calendar-days]');
  const slotsElement = picker.querySelector('[data-time-slots]');
  const calendarStatusElement = picker.querySelector('[data-calendar-status]');
  const validationStatusElement = picker.querySelector('[data-validation-status]');
  const prev = picker.querySelector('[data-month-prev]');
  const next = picker.querySelector('[data-month-next]');
  const statusNames = { available: 'Available for request', limited: 'Limited availability', full: 'Fully booked', unavailable: 'Unavailable', past: 'Date has passed' };
  let today = picker.dataset.today;
  let month = today.slice(0, 7);
  let selectedDate = '';
  let snapshot = null;
  // Until the first availability answer arrives, say so instead of showing every day as "No slots".
  let firstLoad = true;
  const calendarGate = window.AvailabilityResponse.requestGate();
  const validationGate = window.AvailabilityResponse.requestGate();
  let calendarController = null;
  let validationController = null;
  let allowSubmit = false;
  let submitting = false;
  let refreshing = false;
  let validating = false;
  let validatedSelectionKey = '';
  let submissionVersion = 0;
  let retryEvent = null;
  const submitStatus = document.createElement('p');
  submitStatus.dataset.legacySubmissionStatus = '';
  if (form.id === 'booking-form') {
    submitStatus.className = 'help';
    submitStatus.setAttribute('role', 'status');
    submit.insertAdjacentElement('afterend', submitStatus);
  }
  // This picker owns submission, including the single requestSubmit retry.
  // Step navigation only supplies synchronous guards; it must not lock the retry.
  const syncSubmit = () => {
    if (form.classList.contains('is-mixed')) return;
    // The home-service booking needs the customer's map pin before it can be submitted.
    const unpinned = form.id === 'booking-form' && !!form.elements.latitude && (!form.elements.latitude.value || !form.elements.longitude.value);
    submit.disabled = submitting || validating || !startInput.value
      || (form.id === 'booking-form' && form.dataset.priceState !== 'ready') || unpinned;
    if (form.id === 'booking-form') {
      submitStatus.textContent = submitting ? 'Checking or submitting your booking…'
        : validating ? 'Checking your selected time…'
        : !startInput.value ? 'Choose an available date and time before submitting.'
        : unpinned ? 'Pin your exact location inside Metro Manila or Rizal.'
        : form.dataset.priceState === 'loading' ? 'Calculating your current price…'
        : form.dataset.priceState !== 'ready' ? document.querySelector('#pricing-status').textContent : '';
    }
  };
  const submissionState = state => {
    form.dataset.submissionState = state;
    submitting = state !== 'idle';
    form.setAttribute('aria-busy', String(submitting));
    syncSubmit();
  };
  // Keep date strings in studio local time; never convert them through UTC.
  const dateObject = date => new Date(Number(date.slice(0, 4)), Number(date.slice(5, 7)) - 1, Number(date.slice(8, 10) || 1));
  const shiftMonth = (value, offset) => {
    const date = dateObject(value);
    date.setMonth(date.getMonth() + offset);
    return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}`;
  };
  const localMonthFallback = () => {
    const first = dateObject(month);
    const days = new Date(first.getFullYear(), first.getMonth() + 1, 0).getDate();
    return {
      today,
      month,
      durationMinutes: 0,
      bufferMinutes: 0,
      days: Array.from({ length: days }, (_, index) => {
        const date = `${month}-${String(index + 1).padStart(2, '0')}`;
        return { date, status: date < today ? 'past' : 'unavailable', availableCount: 0, slots: [] };
      })
    };
  };
  const selectionParams = () => {
    const params = new URLSearchParams();
    if (picker.dataset.bookingId) params.set('bookingId', picker.dataset.bookingId);
    else {
       const service = form.querySelector('[name="serviceId"]:checked');
       if (!service) throw new Error('Choose a service to view availability.');
       if (form.querySelector('.addon-quantity[aria-invalid="true"]')) throw new Error('Correct the add-on quantities to view availability.');
      params.set('serviceId', service.value);
      const packageType = form.elements.package.value === 'group' ? 'package' : 'individual';
      const quantity = window.BookingQuantity.forBookingType(packageType, form.elements.people.value);
      if (!quantity.valid) throw new Error(quantity.message);
      params.set('people', String(quantity.people));
      params.set('package', quantity.package);
      form.querySelectorAll('input[name="addOnIds"]:checked, input[type="hidden"][name="addOnIds"]').forEach(a => params.append('addOnIds', a.value));
      // Travel-aware times depend on where the customer pins their location.
      const pinLat = form.elements.latitude?.value, pinLng = form.elements.longitude?.value;
      if (pinLat && pinLng) { params.set('latitude', pinLat); params.set('longitude', pinLng); }
    }
    return params;
  };
  const setStatus = (element, text, state = '') => {
    element.textContent = text;
    element.dataset.state = state;
  };
  const calendarMessage = (text, state = '') => setStatus(calendarStatusElement, text, state);
  const validationMessage = (text, state = '') => setStatus(validationStatusElement, text, state);
  const submissionMessage = (text, state = 'error') => {
    validationMessage(text, state);
    // The calendar is hidden on the payment step, so also announce failures there.
    form.dispatchEvent(new CustomEvent('booking-submission-message', { detail: { message: text } }));
  };
  const clearTime = () => { window.FlexibleTimes?.choose(form, null); startInput.value = ''; startInput.dispatchEvent(new Event('change', { bubbles: true })); validatedSelectionKey = ''; validationMessage('Choose an available date, then a time.'); submit.disabled = true; };
  const beginCalendarRequest = () => {
    calendarController?.abort();
    calendarController = new AbortController();
    return { version: calendarGate.begin(), signal: calendarController.signal };
  };
  const cancelValidation = () => {
    validationController?.abort();
    validationController = null;
    validationGate.begin();
    validating = false;
    if (form.dataset.submissionState === 'validating') {
      ++submissionVersion;
      submissionState('idle');
      submissionMessage('Availability check cancelled. Review your selection and submit again.');
    }
    syncSubmit();
  };
  const beginValidationRequest = () => {
    validationController?.abort();
    validationController = new AbortController();
    const version = validationGate.begin();
    validating = true;
    submit.disabled = true;
    return { version, signal: validationController.signal };
  };
  const getJson = async (path, params, signal) => {
    const timeout = new AbortController();
    let timer;
    try {
      const combined = AbortSignal.any ? AbortSignal.any([signal, timeout.signal]) : signal;
      const request = (async () => {
        const response = await fetch(`${path}?${params}`, { signal: combined, cache: 'no-store', headers: { Accept: 'application/json' } });
        if (response.redirected || !response.headers.get('content-type')?.includes('application/json')) throw new Error('Your session may have expired. Sign in again to check availability.');
        const data = await response.json();
        if (!response.ok) throw new Error(data.error || 'Availability could not be checked. Please retry.');
        return data;
      })();
      const timeoutError = new Promise((_, reject) => {
        timer = window.setTimeout(() => { timeout.abort(); reject(new Error('Availability timed out. Please retry.')); }, 10000);
      });
      return await Promise.race([request, timeoutError]);
    } catch (error) {
      if (error.name === 'AbortError' && !signal.aborted) throw new Error('Availability timed out. Please retry.');
      throw error;
    } finally {
      window.clearTimeout(timer);
    }
  };
  const renderSlots = () => {
    slotsElement.replaceChildren();
    const day = snapshot?.days.find(d => d.date === selectedDate);
    picker.querySelector('[data-slots-label]').textContent = day ? dateObject(day.date).toLocaleDateString('en-PH', { weekday: 'short', month: 'short', day: 'numeric' }) : 'Choose an available date';
    if (!day) return;
    const makeButton = slot => {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'time-slot';
      button.textContent = slot.label;
      button.disabled = !slot.available;
      button.dataset.start = slot.start;
       button.title = slot.available ? `${slot.label} — Available for request` : slot.reason;
      button.setAttribute('aria-label', button.title);
      button.setAttribute('aria-pressed', String(startInput.value === slot.start));
      button.addEventListener('click', () => {
         startInput.value = slot.start;
         startInput.dispatchEvent(new Event('change', { bubbles: true }));
        window.FlexibleTimes?.choose(form, slot);
        renderSlots();
        validateTime();
      });
      return button;
    };
    if (window.FlexibleTimes) window.FlexibleTimes.render(slotsElement, form, day.slots, makeButton);
    else day.slots.forEach(slot => slotsElement.append(makeButton(slot)));
  };
  const renderDays = () => {
    daysElement.replaceChildren();
    picker.querySelector('[data-month-label]').textContent = dateObject(month).toLocaleDateString('en-PH', { month: 'long', year: 'numeric' });
    prev.disabled = month <= today.slice(0, 7);
    next.disabled = month >= shiftMonth(today.slice(0, 7), 12);
    for (let i = 0; i < dateObject(month).getDay(); i++) {
      const spacer = document.createElement('span');
      spacer.setAttribute('aria-hidden', 'true');
      daysElement.append(spacer);
    }
    for (const day of snapshot.days) {
      const button = document.createElement('button');
      button.type = 'button';
      const status = snapshot.durationMinutes > 0 && day.date >= today && !day.availableCount && day.slots.length === 0 ? 'unavailable' : day.status;
      button.className = `calendar-day ${status}`;
      button.dataset.date = day.date;
      button.disabled = day.availableCount === 0;
      button.setAttribute('aria-pressed', String(day.date === selectedDate));
      if (day.date === today) button.setAttribute('aria-current', 'date');
      const number = document.createElement('span');
      number.textContent = Number(day.date.slice(-2));
      const indicator = document.createElement('small');
       // Before the customer pins a location nothing is loading: the days wait for the pin.
       const awaitingPin = calendar.classList.contains('is-locked') && status !== 'past';
       const waiting = firstLoad && snapshot.durationMinutes === 0 && status !== 'past';
       indicator.textContent = day.availableCount > 0 ? `${day.availableCount} request slots` : status === 'full' ? 'Fully booked' : status === 'past' ? 'Date has passed' : awaitingPin ? 'Pin first' : waiting ? 'Loading…' : 'No slots';
      button.append(number, indicator);
      const reason = day.date >= today && !day.availableCount
        ? awaitingPin ? 'Pin your location to see available times.'
        : snapshot.durationMinutes === 0 ? 'Live availability is unavailable; use Refresh to retry.' : day.slots.length === 0 ? 'The session and preparation buffer do not fit within booking hours.' : 'No times fit around booking hours, reservations or blocked time.'
        : '';
      button.title = `${day.date}: ${statusNames[status]}${day.availableCount ? `, ${day.availableCount} available start times` : ''}${reason ? ` ${reason}` : ''}`;
      button.setAttribute('aria-label', button.title);
      button.addEventListener('click', () => {
        cancelValidation();
        selectedDate = day.date;
        clearTime();
        renderDays();
        renderSlots();
        validationMessage('Choose an available time for this date.');
      });
      daysElement.append(button);
    }
    picker.querySelector('[data-duration-label]').textContent = snapshot.durationMinutes > 0
      ? `${snapshot.durationMinutes} min session + ${snapshot.bufferMinutes} min buffer`
      : firstLoad ? 'Checking availability…' : 'Live availability is unavailable';
  };
  const validateTime = async (force = false) => {
    if (!startInput.value) return false;
    const start = startInput.value;
    const date = selectedDate;
    const params = selectionParams();
    const key = window.AvailabilityResponse.selectionKey(params, start);
    if (!force && key === validatedSelectionKey) { syncSubmit(); return true; }
    const { version, signal } = beginValidationRequest();
    validationMessage('Checking your selected time…');
    try {
      params.set('start', start);
      const data = await getJson(picker.dataset.validationUrl, params, signal);
      if (!validationGate.isCurrent(version) || selectedDate !== date || startInput.value !== start) return false;
      const result = window.AvailabilityResponse.slot(data);
      if (!result.available) {
        const slot = snapshot?.days.find(d => d.date === selectedDate)?.slots.find(s => s.start === start);
        if (slot) { slot.available = false; slot.reason = result.message; }
        clearTime();
        renderSlots();
        validationMessage(result.message, 'error');
        return false;
      }
      validatedSelectionKey = key;
      syncSubmit();
      validationMessage(result.message, 'valid');
      return true;
    } catch (error) {
      if (error.name !== 'AbortError' && validationGate.isCurrent(version) && selectedDate === date && startInput.value === start) {
        validatedSelectionKey = '';
        validationMessage(error.message, 'error');
      }
      return false;
    } finally {
      if (validationGate.isCurrent(version)) { validating = false; syncSubmit(); }
    }
  };
  // The home-service booking shows times only after the customer pins their location,
  // because the times account for the artist's travel from her other appointments.
  const needsPin = () => form.id === 'booking-form' && !!form.elements.latitude && !picker.dataset.bookingId;
  const pinned = () => !needsPin() || !!(form.elements.latitude.value && form.elements.longitude.value);
  const pinKey = () => pinned() && needsPin() ? `${form.elements.latitude.value}|${form.elements.longitude.value}` : '';
  let lastPinKey = pinKey();
  const pinHint = 'Pin your location to see available times.';
  const lockCalendar = () => {
    calendarController?.abort();
    calendarGate.begin();
    refreshing = false;
    calendar.classList.add('is-locked');
    [daysElement, slotsElement, prev, next, picker.querySelector('[data-refresh]')].forEach(element => { if (element) { element.inert = true; } });
    snapshot = localMonthFallback();
    selectedDate = '';
    renderDays();
    renderSlots();
    cancelValidation();
    clearTime();
    validationMessage('');
    calendarMessage(pinHint);
  };
  const unlockCalendar = () => {
    calendar.classList.remove('is-locked');
    [daysElement, slotsElement, prev, next, picker.querySelector('[data-refresh]')].forEach(element => { if (element) { element.inert = false; } });
  };
  const refresh = async ({ background = false } = {}) => {
    if (form.classList.contains('is-mixed')) return;
    if (submitting) return;
    if (!pinned()) { lockCalendar(); return; }
    const { version, signal } = beginCalendarRequest();
    const requestedMonth = month;
    const hadLiveCalendar = Boolean(snapshot?.durationMinutes > 0);
    const announce = window.AvailabilityResponse.announceRefresh(background, hadLiveCalendar);
    refreshing = true;
    calendar.setAttribute('aria-busy', 'true');
    if (announce) calendarMessage('Checking live availability…');
    try {
      const params = selectionParams();
      params.set('month', requestedMonth);
      const data = await getJson(picker.dataset.availabilityUrl, params, signal);
      if (!calendarGate.isCurrent(version) || month !== requestedMonth) return;
      const result = window.AvailabilityResponse.calendar(data, requestedMonth);
      snapshot = result;
      firstLoad = false;
      today = result.today;
      const day = result.days.find(d => d.date === selectedDate);
      const lostTime = Boolean(startInput.value && !day?.slots.some(s => s.start === startInput.value && s.available));
      if (lostTime) {
        cancelValidation();
        clearTime();
        validationMessage('Your selected time is no longer available. Please choose another slot.', 'error');
      }
      if (day && day.availableCount === 0) selectedDate = '';
      renderDays();
      renderSlots();
      if (!lostTime) {
        const futureDays = result.days.filter(day => day.date >= today);
        calendarMessage(futureDays.length && futureDays.every(day => day.availableCount === 0)
          ? `No available dates this month for a ${result.durationMinutes}-minute session plus ${result.bufferMinutes}-minute preparation buffer. Booking hours, existing reservations or blocked time may prevent it from fitting.` : '');
      }
      if (!startInput.value && !lostTime && !validationStatusElement.textContent)
        validationMessage(selectedDate ? 'Choose an available time for this date.' : 'Choose an available date, then a time.');
     } catch (error) {
       if (error.name !== 'AbortError' && calendarGate.isCurrent(version) && month === requestedMonth) {
         firstLoad = false;
         if (!snapshot || snapshot.durationMinutes === 0) {
           snapshot = localMonthFallback();
           selectedDate = '';
           cancelValidation();
           clearTime();
           renderDays();
           renderSlots();
         }
         calendarMessage(`${error.message || 'Availability could not be retrieved.'} ${snapshot.durationMinutes > 0 ? 'Current dates remain shown.' : 'Dates are shown as unavailable.'} Use Refresh to retry.`, 'error');
       }
     } finally {
       if (calendarGate.isCurrent(version)) {
         refreshing = false;
         calendar.setAttribute('aria-busy', 'false');
       }
    }
  };
  const navigateMonth = offset => {
    const target = shiftMonth(month, offset);
    if (target < today.slice(0, 7) || target > shiftMonth(today.slice(0, 7), 12)) return;
    month = target;
    cancelValidation();
    selectedDate = '';
    clearTime();
    refresh();
  };
  prev.addEventListener('click', () => navigateMonth(-1));
  next.addEventListener('click', () => navigateMonth(1));
  picker.querySelector('[data-refresh]').addEventListener('click', () => refresh());
   let quantityTimer;
   const invalidateSelectionCalendar = () => {
     calendarController?.abort();
     calendarGate.begin();
     refreshing = false;
     snapshot = localMonthFallback();
     selectedDate = '';
     renderDays();
     renderSlots();
     calendarMessage('Updating availability for your selection…');
     cancelValidation();
     clearTime();
   };
   form.addEventListener('change', event => {
     if (event.target.name === 'people') return; // The debounced input handler owns this change.
     if (event.target.matches('[name="serviceId"], [name="addOnIds"], [name="package"], [name="bookingType"], .addon-quantity')) {
       window.clearTimeout(quantityTimer);
       invalidateSelectionCalendar(); refresh();
     }
   });
   form.addEventListener('input', event => {
      if (event.target.name !== 'people' && !event.target.classList.contains('addon-quantity')) return;
      invalidateSelectionCalendar();
     window.clearTimeout(quantityTimer);
     quantityTimer = window.setTimeout(refresh, 350);
   });
  form.addEventListener('booking-price-state-changed', syncSubmit);
  form.addEventListener('booking-location-changed', () => {
    syncSubmit();
    if (!needsPin()) return;
    const key = pinKey();
    if (key === lastPinKey) return;
    lastPinKey = key;
    // A moved pin changes the travel the times allow for, so the chosen time is dropped and reloaded.
    if (!key) { lockCalendar(); return; }
    unlockCalendar();
    invalidateSelectionCalendar();
    refresh();
  });
  form.addEventListener('booking-price-updated', () => { if (startInput.value && !submitting) validateTime(); else syncSubmit(); });
  form.addEventListener('booking-submission-cancel', cancelValidation);
  form.addEventListener('submit', async event => {
    if (form.classList.contains('is-mixed')) return;
    if (allowSubmit) {
      // Consume the permit synchronously: further submit events are duplicates.
      allowSubmit = false;
      retryEvent = event;
      if (!event.defaultPrevented) submissionState('submitting');
      return;
    }
    if (event.defaultPrevented) return;
    event.preventDefault();
    if (submitting) return;
    if (form.id === 'booking-form' && form.dataset.priceState !== 'ready') { submissionMessage('Wait for a valid, current price before submitting.'); return; }
    if (!startInput.value) { submissionMessage('Choose an available date and time before submitting.'); return; }
    const attempt = ++submissionVersion;
    submissionState('validating');
    submissionMessage('Checking availability before submitting…', '');
    // An older calendar refresh must not change the selected slot mid-submit.
    calendarController?.abort();
    calendarGate.begin();
    refreshing = false;
    calendar.setAttribute('aria-busy', 'false');
    try {
      const selection = () => window.AvailabilityResponse.selectionKey(selectionParams(), startInput.value)
        + '|' + (form.querySelector('[name="deposit"]')?.value || '');
      const key = selection();
      if (!await validateTime(true)) {
        if (attempt === submissionVersion) submissionMessage(validationStatusElement.textContent || 'Availability could not be checked. Please retry.');
        return;
      }
      if (attempt !== submissionVersion) return;
      if (key !== selection() || (form.id === 'booking-form' && form.dataset.priceState !== 'ready'))
        throw new Error('Your selection or price changed. Review your booking and submit again.');
      if (!form.reportValidity()) { submissionMessage('Check the highlighted fields before submitting.'); return; }
      retryEvent = null;
      allowSubmit = true;
      // Re-run native validation and the step guards; retain the antiforgery token.
      try { form.requestSubmit(event.submitter || submit); }
      finally { allowSubmit = false; }
      if (!retryEvent || retryEvent.defaultPrevented) {
        submissionState('idle');
        submissionMessage('Submission was cancelled. Check your booking and try again.');
      }
    } catch (error) {
      if (attempt === submissionVersion) submissionMessage(error.message || 'Availability could not be checked. Please retry.');
    } finally {
      if (attempt === submissionVersion && form.dataset.submissionState !== 'submitting') submissionState('idle');
    }
  });
  window.addEventListener('pageshow', event => {
    if (event.persisted) {
      ++submissionVersion;
      cancelValidation();
      validatedSelectionKey = '';
      submissionState('idle');
    }
  });
  submissionState('idle');
  calendar.hidden = false;
  picker.querySelector('[data-date-fallback]').hidden = true;
  startInput.type = 'hidden';
  startInput.required = false;
  submit.disabled = true;
  snapshot = localMonthFallback();
  renderDays();
  renderSlots();
  if (pinned()) { calendarMessage('Checking live availability…'); refresh(); } else lockCalendar();
  window.setInterval(() => { if (!document.hidden && !refreshing && !validating && !submitting) refresh({ background: true }); }, 15000);
  document.addEventListener('visibilitychange', () => { if (!document.hidden && !validating) refresh({ background: true }); });
  window.addEventListener('focus', () => { if (!refreshing && !validating) refresh({ background: true }); });
});
