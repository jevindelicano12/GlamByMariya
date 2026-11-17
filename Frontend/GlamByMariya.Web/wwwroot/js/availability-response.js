(function (root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.AvailabilityResponse = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  const calendarStatuses = new Set(['available', 'limited', 'full', 'unavailable', 'past']);
  const datePattern = /^\d{4}-\d{2}-\d{2}$/;
  const monthPattern = /^\d{4}-\d{2}$/;

  function calendar(data, requestedMonth) {
    const valid = data && monthPattern.test(data.month) && data.month === requestedMonth
      && datePattern.test(data.today) && Number.isInteger(data.durationMinutes) && data.durationMinutes > 0
      && Number.isInteger(data.bufferMinutes) && data.bufferMinutes >= 0 && Array.isArray(data.days)
      && data.days.length > 0 && data.days.every(day => day && datePattern.test(day.date)
        && day.date.startsWith(`${requestedMonth}-`) && calendarStatuses.has(day.status)
        && Number.isInteger(day.availableCount) && day.availableCount >= 0 && Array.isArray(day.slots));
    if (!valid) throw new Error('Availability returned an invalid calendar. Please retry.');
    return data;
  }

  function slot(data) {
    if (!data || typeof data.available !== 'boolean' || typeof data.message !== 'string')
      throw new Error('Availability returned an invalid time validation. Please retry.');
    return data;
  }

  function requestGate() {
    let current = 0;
    return { begin: () => ++current, isCurrent: version => version === current };
  }

  function selectionKey(params, start) {
    return `${params.toString()}|${start}`;
  }

  function announceRefresh(background, hasLiveCalendar) {
    return !background || !hasLiveCalendar;
  }

  return { calendar, slot, requestGate, selectionKey, announceRefresh };
});
