// Phase 6: early / regular / after-hours times, shared by the single-booking and package calendars.
// The copy comes from the form's data attributes (set from BookingWindow on the server).
(() => {
  const copy = form => ({
    earlyLabel: form?.dataset.earlyLabel || 'Early booking (needs artist approval)',
    regularLabel: 'Regular hours',
    lateLabel: 'Late / after-hours booking',
    afterHoursNote: form?.dataset.afterHoursNote || '',
    earlyNote: form?.dataset.earlyNote || ''
  });

  // Draws the slot buttons with a heading before each group when early or late/after-hours times exist.
  const render = (container, form, slots, makeButton) => {
    const text = copy(form);
    const grouped = slots.some(slot => slot.kind === 'early' || slot.afterHours);
    let kind = '';
    for (const slot of slots) {
      const current = slot.kind === 'early' ? 'early' : slot.afterHours ? 'late' : 'regular';
      if (grouped && current !== kind) {
        const heading = document.createElement('p');
        heading.className = `time-slot-group ${current}`;
        heading.textContent = current === 'early' ? text.earlyLabel : current === 'late' ? text.lateLabel : text.regularLabel;
        container.append(heading);
        kind = current;
      }
      const button = makeButton(slot);
      if (slot.kind === 'early') button.classList.add('early');
      if (slot.afterHours) { button.classList.add('after-hours'); button.title = `${button.title} · after hours`; }
      container.append(button);
    }
  };

  // Popup notice shown when the customer selects a time that extends past closing hours.
  let lateModal, lateAck, lateContinue, lateCancel, lateBody, onLateConfirm, onLateCancel;
  const ensureLateModal = () => {
    if (lateModal) return lateModal;
    lateModal = document.createElement('div');
    lateModal.className = 'travel-modal';
    lateModal.id = 'after-hours-modal';
    lateModal.hidden = true;
    lateModal.setAttribute('role', 'dialog');
    lateModal.setAttribute('aria-modal', 'true');
    lateModal.setAttribute('aria-labelledby', 'after-hours-title');
    lateModal.innerHTML = `<div class="travel-modal-card"><div class="eyebrow">AFTER-HOURS NOTICE</div><h2 id="after-hours-title">Extra Hours Past Closing</h2><div id="after-hours-description"><p data-after-hours-copy></p><p><strong>Only this extra payment is handled personally outside the system.</strong> Any additional fee for extra hours past closing is agreed and settled directly with your makeup artist — it is not included in your online booking total or down payment.</p></div><label class="travel-policy-check"><input type="checkbox" id="after-hours-ack" /> <span>I understand that any extra payment for hours past closing is arranged personally with the makeup artist outside the system.</span></label><div class="travel-modal-actions"><button type="button" class="button secondary" data-late-cancel>Cancel</button><button type="button" class="button" data-late-confirm disabled>I Understand &amp; Continue</button></div></div>`;
    document.body.append(lateModal);
    lateBody = lateModal.querySelector('[data-after-hours-copy]');
    lateAck = lateModal.querySelector('#after-hours-ack');
    lateContinue = lateModal.querySelector('[data-late-confirm]');
    lateCancel = lateModal.querySelector('[data-late-cancel]');
    lateAck.addEventListener('change', () => { lateContinue.disabled = !lateAck.checked; });
    lateContinue.addEventListener('click', () => {
      if (!lateAck.checked) return;
      lateModal.hidden = true;
      onLateConfirm?.();
    });
    const dismiss = () => { lateModal.hidden = true; onLateCancel?.(); };
    lateCancel.addEventListener('click', dismiss);
    lateModal.addEventListener('click', e => { if (e.target === lateModal) dismiss(); });
    lateModal.addEventListener('keydown', e => { if (e.key === 'Escape') { e.preventDefault(); dismiss(); } });
    return lateModal;
  };

  let lastPromptedStart = '';

  const formatClock = totalMinutes => {
    const mins = ((totalMinutes % 1440) + 1440) % 1440;
    const h24 = Math.floor(mins / 60), m = mins % 60;
    const suffix = h24 >= 12 ? 'PM' : 'AM';
    const h12 = h24 % 12 || 12;
    return `${h12}:${String(m).padStart(2, '0')} ${suffix}`;
  };

  // The note under the chosen time, and the submit label for an early request.
  // Regular-hour bookings whose service duration finishes past closing also show a gentle heads-up.
  const choose = (form, slot, durationMinutes) => {
    const text = copy(form);
    const submit = form?.querySelector('[type="submit"]');
    if (submit) {
      submit.dataset.defaultLabel ??= submit.textContent;
      submit.textContent = slot?.kind === 'early' ? 'Send early booking request' : submit.dataset.defaultLabel;
    }
    if (form) form.dataset.earlyRequest = String(slot?.kind === 'early');
    let overtimeLine = '';
    const closeHour = Number(form?.dataset.closeHour);
    const dur = Number(durationMinutes);
    if (!slot?.afterHours && slot?.start && Number.isFinite(closeHour) && dur > 0) {
      const m = /T(\d{2}):(\d{2})/.exec(slot.start);
      if (m) {
        const endMins = Number(m[1]) * 60 + Number(m[2]) + dur;
        if (endMins > closeHour * 60) {
          overtimeLine = `Estimated finish: ${formatClock(endMins)} (past ${formatClock(closeHour * 60)} closing — any extra after-hours fee is arranged directly with the artist).`;
        }
      }
    }
    const activePicker = form?.classList.contains('is-mixed')
      ? form.querySelector('#mixed-time-quote')
      : form?.querySelector('.availability-picker [data-time-status], .availability-picker .time-slots');
    let inlineNote = form?.querySelector('[data-overtime-note]');
    if (!inlineNote && activePicker) {
      inlineNote = document.createElement('p');
      inlineNote.className = 'flexible-note';
      inlineNote.dataset.overtimeNote = '';
      inlineNote.setAttribute('role', 'status');
      activePicker.after(inlineNote);
    } else if (inlineNote && activePicker && inlineNote.previousElementSibling !== activePicker) {
      activePicker.after(inlineNote);
    }
    if (inlineNote) {
      inlineNote.textContent = overtimeLine;
      inlineNote.hidden = !overtimeLine;
    }
    let note = form?.querySelector('[data-flexible-note]');
    if (!note && form) {
      note = document.createElement('p'); note.className = 'flexible-note'; note.dataset.flexibleNote = ''; note.setAttribute('role', 'status');
      submit?.before(note);
    }
    if (!note) return;
    const lines = [];
    if (slot?.kind === 'early' && text.earlyNote) lines.push(text.earlyNote);
    if (slot?.afterHours && text.afterHoursNote) lines.push(text.afterHoursNote);
    if (overtimeLine) lines.push(overtimeLine);
    note.textContent = lines.join(' ');
    note.hidden = lines.length === 0;
    // A time ending after closing needs the customer's agreement to the after-hours terms.
    let terms = form?.querySelector('[data-after-hours-terms]');
    if (!terms && form && slot?.afterHours) {
      terms = document.createElement('label'); terms.className = 'after-hours-terms'; terms.dataset.afterHoursTerms = '';
      const box = document.createElement('input'); box.type = 'checkbox'; box.name = 'afterHoursAccepted'; box.value = 'true';
      const labelText = document.createElement('span'); labelText.append('I understand and agree to the ');
      const link = document.createElement('a'); link.href = '/Home/Terms#after-hours'; link.target = '_blank'; link.rel = 'noopener'; link.textContent = 'after-hours terms';
      labelText.append(link, '.'); terms.append(box, labelText); note.after(terms);
    }
    if (terms) {
      const box = terms.querySelector('input');
      terms.hidden = !slot?.afterHours; box.required = !!slot?.afterHours;
      if (!slot?.afterHours) box.checked = false;
    }
    if (!slot?.afterHours) { lastPromptedStart = ''; return; }
    if (slot.start && slot.start !== lastPromptedStart) {
      lastPromptedStart = slot.start;
      const modal = ensureLateModal();
      lateBody.textContent = text.afterHoursNote || 'This booking extends past regular closing hours and may require an extra after-hours payment.';
      const box = terms?.querySelector('input');
      lateAck.checked = Boolean(box?.checked);
      lateContinue.disabled = !lateAck.checked;
      onLateConfirm = () => { if (box) box.checked = true; };
      onLateCancel = () => {
        lastPromptedStart = '';
        if (box) box.checked = false;
        const startInput = form?.querySelector('[name="start"]');
        if (startInput && !startInput.disabled) { startInput.value = ''; startInput.dispatchEvent(new Event('change', { bubbles: true })); }
        form?.querySelectorAll('.time-slot[aria-pressed="true"]').forEach(b => b.setAttribute('aria-pressed', 'false'));
      };
      modal.hidden = false;
      lateAck.focus();
    }
  };

  window.FlexibleTimes = { render, choose };
})();
