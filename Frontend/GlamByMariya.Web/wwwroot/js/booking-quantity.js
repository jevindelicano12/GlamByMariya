(() => {
  // The owner sets the most people per booking (Business settings, 1 to 20); the page passes it on the
  // people field. Without a page (unit tests) the limit is 20.
  const field = typeof document !== 'undefined' ? document.querySelector('input[name="people"][data-max-people]') : null;
  const configured = Number(field?.dataset.maxPeople);
  const maximumPeople = Number.isSafeInteger(configured) && configured >= 1 && configured <= 20 ? configured : 20;
  const parse = raw => {
    const value = String(raw ?? '').trim();
    if (!/^\d+$/.test(value)) return { valid: false, message: `Enter a whole number from 1 to ${maximumPeople}.` };
    const people = Number(value);
    if (!Number.isSafeInteger(people) || people < 1 || people > maximumPeople)
      return { valid: false, message: `Enter a whole number from 1 to ${maximumPeople}.` };
    return { valid: true, people };
  };
  const forBookingType = (type, raw) => type === 'individual'
    ? { valid: true, people: 1, package: 'auto' }
    : { ...parse(raw), package: 'group' };

  // A number above the limit cannot stay in the field: it changes to the limit and says why.
  if (field) {
    const note = document.getElementById('people-limit');
    field.addEventListener('input', () => {
      const digits = field.value.replace(/\D/g, '');
      if (digits !== field.value) field.value = digits;
      if (digits && Number(digits) > maximumPeople) {
        field.value = String(maximumPeople);
        if (note) { note.textContent = `The most you can book is ${maximumPeople} people. Changed to ${maximumPeople}.`; note.hidden = false; }
      } else if (note) { note.hidden = true; note.textContent = ''; }
    }, true);
  }

  const api = Object.freeze({ maximumPeople, parse, forBookingType });
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  if (typeof window !== 'undefined') window.BookingQuantity = api;
})();
