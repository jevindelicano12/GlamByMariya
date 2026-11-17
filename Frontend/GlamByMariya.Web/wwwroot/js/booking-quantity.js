(() => {
  const maximumPeople = 20;
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

  const api = Object.freeze({ maximumPeople, parse, forBookingType });
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  if (typeof window !== 'undefined') window.BookingQuantity = api;
})();
