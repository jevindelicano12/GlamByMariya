// Phase 6: early / regular / after-hours times, shared by the single-booking and package calendars.
// The copy comes from the form's data attributes (set from BookingWindow on the server).
(() => {
  const copy = form => ({
    earlyLabel: form?.dataset.earlyLabel || 'Early booking (needs artist approval)',
    regularLabel: 'Regular hours',
    afterHoursNote: form?.dataset.afterHoursNote || '',
    earlyNote: form?.dataset.earlyNote || ''
  });

  // Draws the slot buttons with a heading before each group; headings only appear when the day has early times.
  const render = (container, form, slots, makeButton) => {
    const text = copy(form);
    const grouped = slots.some(slot => slot.kind === 'early');
    let kind = '';
    for (const slot of slots) {
      const current = slot.kind === 'early' ? 'early' : 'regular';
      if (grouped && current !== kind) {
        const heading = document.createElement('p');
        heading.className = `time-slot-group ${current}`;
        heading.textContent = current === 'early' ? text.earlyLabel : text.regularLabel;
        container.append(heading);
        kind = current;
      }
      const button = makeButton(slot);
      if (slot.kind === 'early') button.classList.add('early');
      if (slot.afterHours) { button.classList.add('after-hours'); button.title = `${button.title} · after hours`; }
      container.append(button);
    }
  };

  // The note under the chosen time, and the submit label for an early request.
  const choose = (form, slot) => {
    const text = copy(form);
    const submit = form?.querySelector('[type="submit"]');
    if (submit) {
      submit.dataset.defaultLabel ??= submit.textContent;
      submit.textContent = slot?.kind === 'early' ? 'Send early booking request' : submit.dataset.defaultLabel;
    }
    if (form) form.dataset.earlyRequest = String(slot?.kind === 'early');
    let note = form?.querySelector('[data-flexible-note]');
    if (!note && form) {
      note = document.createElement('p'); note.className = 'flexible-note'; note.dataset.flexibleNote = ''; note.setAttribute('role', 'status');
      submit?.before(note);
    }
    if (!note) return;
    const lines = [];
    if (slot?.kind === 'early' && text.earlyNote) lines.push(text.earlyNote);
    if (slot?.afterHours && text.afterHoursNote) lines.push(text.afterHoursNote);
    note.textContent = lines.join(' ');
    note.hidden = lines.length === 0;
    // A time ending after closing needs the customer's agreement to the after-hours terms.
    let terms = form?.querySelector('[data-after-hours-terms]');
    if (!terms && form && slot?.afterHours) {
      terms = document.createElement('label'); terms.className = 'after-hours-terms'; terms.dataset.afterHoursTerms = '';
      const box = document.createElement('input'); box.type = 'checkbox'; box.name = 'afterHoursAccepted'; box.value = 'true';
      const text = document.createElement('span'); text.append('I understand and agree to the ');
      const link = document.createElement('a'); link.href = '/Home/Terms#after-hours'; link.target = '_blank'; link.rel = 'noopener'; link.textContent = 'after-hours terms';
      text.append(link, '.'); terms.append(box, text); note.after(terms);
    }
    if (terms) {
      const box = terms.querySelector('input');
      terms.hidden = !slot?.afterHours; box.required = !!slot?.afterHours;
      if (!slot?.afterHours) box.checked = false;
    }
  };

  window.FlexibleTimes = { render, choose };
})();
