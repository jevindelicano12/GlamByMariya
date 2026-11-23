(() => {
  const form = document.querySelector('#booking-form'), panel = form?.querySelector('#mixed-calendar');
  if (!panel) return;
  const days = panel.querySelector('[data-mixed-days]'), slots = panel.querySelector('[data-mixed-slots]');
  const suggestions = panel.querySelector('[data-mixed-alternatives]'), status = panel.querySelector('#mixed-calendar-status');
  const timeStatus = panel.querySelector('#mixed-time-status'), price = panel.querySelector('#mixed-time-quote');
  const monthLabel = panel.querySelector('[data-mixed-month]'), heading = panel.querySelector('[data-mixed-slots-heading]');
  const previous = panel.querySelector('[data-mixed-prev]'), next = panel.querySelector('[data-mixed-next]');
  const startInput = form.querySelector('#mixed-start');
  const token = form.querySelector('[name=__RequestVerificationToken]').value;
  const money = value => new Intl.NumberFormat('en-PH', { style: 'currency', currency: 'PHP' }).format(value);
  const date = text => new Date(Number(text.slice(0, 4)), Number(text.slice(5, 7)) - 1, Number(text.slice(8, 10) || 1));
  const shift = (month, n) => { const d = date(month); d.setMonth(d.getMonth() + n); return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`; };
  let today = panel.dataset.today, month = today.slice(0, 7), snapshot, selectedDate = '', selectedStart = '', revision = 0, controllers = [];
  const active = () => form.querySelector('#mixed-mode')?.checked && form.dataset.currentStep === 'schedule';
  const invalidate = () => { window.FlexibleTimes?.choose(form, null); revision++; controllers.forEach(c => c.abort()); controllers = []; panel.setAttribute('aria-busy', 'false'); selectedStart = ''; if (startInput) startInput.value = ''; form.mixedSlot = null; form.dispatchEvent(new Event('mixed-slot-changed')); price.textContent = ''; timeStatus.textContent = ''; suggestions.replaceChildren(); };
  const post = async (url, body) => {
    const controller = new AbortController(); controllers.push(controller);
    const response = await fetch(url, { method: 'POST', signal: controller.signal, cache: 'no-store', headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token }, body: JSON.stringify(body) });
    if (response.redirected || !response.headers.get('content-type')?.includes('application/json')) throw new Error('Please sign in again to check availability.');
    const data = await response.json(); if (!response.ok) throw new Error(data.error || 'Availability could not be checked.'); return data;
  };
  const showSlots = day => {
    slots.replaceChildren();
    heading.textContent = day ? `Makeup start times · ${date(day.date).toLocaleDateString('en-PH', { weekday: 'short', month: 'short', day: 'numeric' })}` : 'Choose a date';
    if (!day) return;
    const makeButton = slot => {
      const button = document.createElement('button'); button.type = 'button'; button.className = 'time-slot'; button.textContent = slot.label;
      button.disabled = !slot.available; button.title = slot.available ? 'Available for request' : slot.reason || 'Unavailable';
      button.setAttribute('aria-pressed', String(selectedStart === slot.start)); button.dataset.start = slot.start;
      button.addEventListener('click', () => validate(slot.start)); return button;
    };
    if (window.FlexibleTimes) window.FlexibleTimes.render(slots, form, day.slots, makeButton);
    else day.slots.forEach(slot => slots.append(makeButton(slot)));
  };
  // Phase 6: the package's pin goes with every availability and slot check (the travel rule).
  const pin = () => {
    const latitude = Number(form.querySelector('[name="latitude"]')?.value), longitude = Number(form.querySelector('[name="longitude"]')?.value);
    return latitude && longitude ? { latitude, longitude } : {};
  };
  const render = () => {
    days.replaceChildren(); monthLabel.textContent = date(month).toLocaleDateString('en-PH', { month: 'long', year: 'numeric' });
    previous.disabled = month <= today.slice(0, 7); next.disabled = month >= shift(today.slice(0, 7), 12);
    for (let i = 0; i < date(month).getDay(); i++) days.append(document.createElement('span'));
    for (const day of snapshot.days) {
      const button = document.createElement('button'); button.type = 'button'; button.className = `calendar-day ${day.status}`;
      button.dataset.date = day.date; button.disabled = day.date < today; button.setAttribute('aria-pressed', String(day.date === selectedDate));
      const friendly = date(day.date).toLocaleDateString('en-PH', { weekday: 'long', month: 'long', day: 'numeric' });
      if (day.availableCount) button.setAttribute('aria-label', `${friendly}: ${day.availableCount} available start times`);
      else { button.setAttribute('aria-label', `${friendly}: no available start times. Select to see why.`); button.title = 'No start time fits this date; select for details and alternatives.'; }
      const number = document.createElement('span'), label = document.createElement('small'); number.textContent = Number(day.date.slice(-2)); label.textContent = day.availableCount ? `${day.availableCount} times` : 'No times'; button.append(number, label);
      button.addEventListener('click', () => chooseDay(day)); days.append(button);
    }
    panel.querySelector('#mixed-occupied').textContent = `Total occupied: ${snapshot.durationMinutes} min makeup + ${snapshot.bufferMinutes} min preparation = ${snapshot.durationMinutes + snapshot.bufferMinutes} min.`;
  };
  const validate = async start => {
    invalidate(); const key = revision, people = form.mixedPeople; selectedStart = start; showSlots(snapshot?.days.find(d => d.date === selectedDate));
    [...slots.querySelectorAll('button')].find(button => button.dataset.start === start)?.focus({ preventScroll: true });
    timeStatus.textContent = 'Checking this makeup start time…';
    try {
      const result = await post(panel.dataset.validationUrl, { people, start, ...pin() });
      if (key !== revision || !active()) return;
      if (!result.available || !result.quote) { selectedStart = ''; timeStatus.textContent = result.message || 'This time is no longer available.'; showSlots(snapshot.days.find(d => d.date === selectedDate)); return; }
      const q = result.quote; timeStatus.textContent = result.message;
      const chosen = snapshot?.days.find(d => d.date === selectedDate)?.slots.find(s => s.start === start);
      status.textContent = `Selected makeup start: ${chosen?.label ?? start.slice(11)}, ${date(selectedDate).toLocaleDateString('en-PH', { weekday: 'short', month: 'short', day: 'numeric' })}.`;
      price.textContent = `Services ${money(q.serviceTotal)} · Add-ons ${money(q.addOnTotal)} · Early fee ${money(q.earlyFee)}${q.lateFee > 0 ? ` · Late fee ${money(q.lateFee)}` : ''} · Total ${money(q.finalTotal)} · Minimum down payment ${money(q.requiredDownPayment)}`;
      if (startInput) startInput.value = start;
      window.FlexibleTimes?.choose(form, chosen);
      form.mixedSlot = { start, quote: q, bufferMinutes: result.bufferMinutes, selection: JSON.stringify(people) };
      form.dispatchEvent(new Event('mixed-slot-changed'));
    } catch (error) { if (key === revision && error.name !== 'AbortError') { selectedStart = ''; timeStatus.textContent = error.message; showSlots(snapshot.days.find(d => d.date === selectedDate)); } }
  };
  const findAlternatives = async after => {
    if (!panel.dataset.alternativesUrl) return; // the live site has no alternative-date search for packages
    const key = revision, people = form.mixedPeople; suggestions.textContent = 'Searching the next 30 days…';
    try {
      const result = await post(panel.dataset.alternativesUrl, { people, after });
      if (key !== revision || !active()) return;
      suggestions.replaceChildren(); const title = document.createElement('h3'); title.textContent = 'Alternative dates'; suggestions.append(title);
      if (!result.dates.length) { const empty = document.createElement('p'); empty.textContent = `No alternatives found through ${result.checkedThrough.slice(0, 10)} (${result.daysChecked} days checked).`; suggestions.append(empty); return; }
      for (const day of result.dates) {
        const button = document.createElement('button'); button.type = 'button'; button.className = 'mixed-suggestion'; button.dataset.date = day.date;
        const labels = day.slots.filter(s => s.available).map(s => s.label), shown = labels.slice(0, 3).join(', ');
        button.textContent = `${date(day.date).toLocaleDateString('en-PH', { weekday: 'short', month: 'short', day: 'numeric' })} · ${shown}${labels.length > 3 ? ` +${labels.length - 3} more` : ''}`;
        button.addEventListener('click', () => { month = day.date.slice(0, 7); refresh(day.date); }); suggestions.append(button);
      }
    } catch (error) { if (key === revision && error.name !== 'AbortError') suggestions.textContent = `Could not find alternatives: ${error.message}`; }
  };
  const chooseDay = day => {
    invalidate(); selectedDate = day.date; render(); showSlots(day);
    [...days.querySelectorAll('button')].find(button => button.dataset.date === day.date)?.focus({ preventScroll: true });
    const reasons = [...new Set(day.slots.map(s => s.reason).filter(Boolean))];
    if (day.availableCount) { status.textContent = `Choose a makeup start time.${reasons.length ? ` Some times are unavailable: ${reasons.join(' ')}` : ''}`; return; }
    status.textContent = reasons.length ? `No start time fits. ${reasons.join(' ')}` : 'The makeup session and preparation do not fit within booking hours.';
    findAlternatives(day.date);
  };
  const refresh = async (select = '') => {
    invalidate(); snapshot = null; selectedDate = ''; days.replaceChildren(); slots.replaceChildren(); panel.querySelector('#mixed-occupied').textContent = ''; heading.textContent = 'Choose a date';
    const key = revision, requested = month, people = form.mixedPeople; status.textContent = 'Checking availability…'; panel.setAttribute('aria-busy', 'true');
    try {
      const data = await post(panel.dataset.availabilityUrl, { people, month: requested, ...pin() });
      if (key !== revision || !active() || requested !== month) return;
      if (!Array.isArray(data.days) || data.month !== requested || !Number.isFinite(data.durationMinutes) || !Number.isFinite(data.bufferMinutes)) throw new Error('The availability response was incomplete.');
      snapshot = data; today = data.today; status.textContent = 'Choose a date to see makeup start times.'; render(); panel.setAttribute('aria-busy', 'false');
      if (select) { const day = data.days.find(d => d.date === select); if (day) chooseDay(day); }
    } catch (error) { if (key === revision && error.name !== 'AbortError') status.textContent = `Availability could not be loaded: ${error.message}`; }
    finally { if (key === revision) panel.setAttribute('aria-busy', 'false'); }
  };
  previous.addEventListener('click', () => { month = shift(month, -1); refresh(); });
  next.addEventListener('click', () => { month = shift(month, 1); refresh(); });
  form.addEventListener('mixed-selection-invalidated', () => { invalidate(); snapshot = null; days.replaceChildren(); slots.replaceChildren(); panel.querySelector('#mixed-occupied').textContent = ''; });
  form.addEventListener('booking-location-changed', () => { if (active() && form.dataset.mixedQuoteState === 'ready') refresh(); });
  form.addEventListener('booking-step-shown', () => { if (active() && form.dataset.mixedQuoteState === 'ready') refresh(); });
})();
