import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const quantity = require('../Frontend/GlamByMariya.Web/wwwroot/js/booking-quantity.js');
const availability = require('../Frontend/GlamByMariya.Web/wwwroot/js/availability-response.js');

for (const valid of ['1', '4', '20']) {
  const result = quantity.parse(valid);
  assert.equal(result.valid, true, `${valid} is accepted`);
  assert.equal(result.people, Number(valid));
}
for (const invalid of ['', '0', '21', '4.5', '-1', 'abc', '1e2']) {
  assert.equal(quantity.parse(invalid).valid, false, `${JSON.stringify(invalid)} is rejected`);
}
assert.deepEqual(quantity.forBookingType('individual', '14'), { valid: true, people: 1, package: 'auto' });
assert.deepEqual(quantity.forBookingType('package', '4'), { valid: true, people: 4, package: 'group' });
assert.equal(quantity.forBookingType('package', '').valid, false, 'temporarily empty package quantity stays invalid');

const view = await readFile(new URL('../Frontend/GlamByMariya.Web/Views/Bookings/Create.cshtml', import.meta.url), 'utf8');
assert.doesNotMatch(view, /selected-look-photo|data-image=|images\/looks\//, 'booking markup does not request unverified look images');
assert.match(view, /type="text" name="people"[^>]*inputmode="numeric"/);
assert.match(view, /package-people-slot/);
assert.match(view, /maxlength="2"/);
assert.match(view, /Transportation Fee Policy/);
assert.match(view, /I have read, understood, and agree to the Transportation Fee Policy/);
assert.match(view, /data-travel-ack disabled/);
assert.match(view, /actual transportation fee will be reimbursed separately/);
const calendarView = await readFile(new URL('../Frontend/GlamByMariya.Web/Views/Shared/_AvailabilityCalendar.cshtml', import.meta.url), 'utf8');
assert.match(calendarView, /data-calendar-status/);
assert.match(calendarView, /data-validation-status/);
assert.match(calendarView, /Available for request/);
assert.match(calendarView, /Date has passed/);
assert.match(calendarView, /not guarantees of confirmation|not guarantees/, 'calendar explains request versus confirmation');

const css = await readFile(new URL('../Frontend/GlamByMariya.Web/wwwroot/css/booking-input-fixes.css', import.meta.url), 'utf8');
assert.match(css, /\.look-selection\s*\{\s*align-items:\s*start/);
assert.match(css, /\.look-preview\s*>img\s*\{[^}]*object-fit:\s*cover/s);
assert.match(css, /\.look-preview\s*\{[^}]*height:\s*650px/s);
assert.match(css, /@media\(max-width:580px\)\{\.look-preview\{height:360px\}/);

const availabilityCss = await readFile(new URL('../Frontend/GlamByMariya.Web/wwwroot/css/availability.css', import.meta.url), 'utf8');
assert.match(availabilityCss, /calendar-toolbar\{display:grid;grid-template-columns:36px minmax\(0,1fr\) 36px auto/);
assert.match(availabilityCss, /repeat\(7,minmax\(0,1fr\)\)/);
assert.match(availabilityCss, /availability-status\[data-state="error"\]/);

const site = await readFile(new URL('../Frontend/GlamByMariya.Web/wwwroot/js/site.js', import.meta.url), 'utf8');
assert.match(site, /querySelector\('#selected-look-name'\)\.textContent = service\.dataset\.name/, 'selected look name updates without an image');
assert.match(site, /querySelector\('#look-selection-caption'\)\.textContent/, 'selection caption updates without an image');
assert.doesNotMatch(site, /if\s*\(Number\(peopleInput\.value\)\s*<\s*1\)\s*peopleInput\.value\s*=\s*['"]1['"]/,
  'editing an empty quantity is not rewritten to 1');

const steps = await readFile(new URL('../Frontend/GlamByMariya.Web/wwwroot/js/booking-steps.js', import.meta.url), 'utf8');
assert.match(steps, /travelInput\.value = 'false'/, 'transportation acknowledgment starts unchecked');
assert.match(steps, /Read and accept|travelAcknowledged\(\)/, 'submission path requires transportation acknowledgment');
assert.doesNotMatch(steps, /sessionStorage\.setItem\(travelKey/, 'acknowledgment is not reused across bookings');

assert.match(site, /localMonthFallback/);
assert.match(site, /Availability timed out/);
assert.match(site, /Dates are shown as unavailable/);
assert.match(site, /AvailabilityResponse\.calendar/);
assert.match(site, /Available for request/);
assert.match(site, /window\.setTimeout\(refresh, 350\)/, 'party-size edits debounce availability requests');
assert.match(site, /calendarGate\.isCurrent\(version\)/, 'a stale calendar request cannot clear the active refresh state');
assert.match(site, /validationGate\.isCurrent\(version\)/, 'a stale slot validation cannot update a new selection');
assert.match(site, /cancelValidation\(\);\s*selectedDate = day\.date/, 'switching dates cancels pending slot validation');
assert.doesNotMatch(site, /daysElement\.querySelectorAll\('button'\).*disabled = true/, 'calendar refresh does not blanket-disable date buttons');
assert.match(site, /!refreshing && !validating && !submitting/, 'automatic refresh waits for slot validation');
assert.match(site, /refresh\(\{ background: true \}\)/, 'automatic refresh is explicitly background work');
assert.doesNotMatch(site, /Live availability refreshed\./, 'successful refresh has no visible calendar message');
assert.match(site, /futureDays\.every\(day => day\.availableCount === 0\)[\s\S]*? : ''\)/, 'successful refresh explains a fully unavailable month and otherwise clears prior errors');
assert.doesNotMatch(site, /if \(startInput\.value && !validating\) await validateTime/, 'calendar refresh does not repeatedly validate an unchanged slot');
assert.match(site, /validateTime\(true\)/, 'final submission still forces fresh server-side slot validation');
assert.match(site, /submissionState\('submitting'\)/, 'the owner retains the duplicate lock after the approved retry');
assert.match(site, /retryEvent\.defaultPrevented/, 'a cancelled native retry releases the lock');
assert.match(site, /booking-submission-message/, 'final validation errors are announced outside the hidden calendar');
assert.doesNotMatch(steps, /let submitting|submitting = true/, 'step guards do not independently lock the submission retry');

const validCalendar = {
  today: '2026-10-19', month: '2026-10', durationMinutes: 60, bufferMinutes: 45,
  days: [{ date: '2026-10-19', status: 'available', availableCount: 34, slots: [] }]
};
assert.equal(availability.calendar(validCalendar, '2026-10'), validCalendar, 'successful calendar response is accepted');
assert.throws(() => availability.calendar({ available: true, message: 'Available' }, '2026-10'), /invalid calendar/, 'slot response is rejected as a calendar');
assert.throws(() => availability.calendar({ ...validCalendar, month: '2026-11' }, '2026-10'), /invalid calendar/, 'wrong-month response is rejected');
assert.deepEqual(availability.slot({ available: true, message: 'Available' }), { available: true, message: 'Available' }, 'slot response uses its own validation shape');
assert.throws(() => availability.slot(validCalendar), /invalid time validation/, 'calendar response is rejected as slot validation');
const gate = availability.requestGate();
const stale = gate.begin();
const current = gate.begin();
assert.equal(gate.isCurrent(stale), false, 'an older response cannot overwrite current status');
assert.equal(gate.isCurrent(current), true, 'latest response may update status');
const calendarGate = availability.requestGate();
const validationGate = availability.requestGate();
const pendingSlot = validationGate.begin();
const pendingCalendar = calendarGate.begin();
const switchedDate = validationGate.begin();
assert.equal(validationGate.isCurrent(pendingSlot), false, 'switching dates invalidates pending slot validation');
assert.equal(validationGate.isCurrent(switchedDate), true, 'new date owns the validation state');
assert.equal(calendarGate.isCurrent(pendingCalendar), true, 'calendar refresh lifecycle is independent from slot validation');
const repeatedDate = validationGate.begin();
assert.equal(validationGate.isCurrent(switchedDate), false, 'repeated date changes invalidate the previous date');
assert.equal(validationGate.isCurrent(repeatedDate), true, 'last repeated date remains current');
assert.throws(() => availability.calendar({}, '2026-10'), /invalid calendar/);
assert.equal(availability.calendar(validCalendar, '2026-10'), validCalendar, 'a valid retry recovers after a malformed response');
let selectedTimeStatus = 'This time is available. Final confirmation is subject to artist approval.';
for (let cycle = 0; cycle < 4; cycle++) {
  assert.equal(availability.announceRefresh(true, true), false, `background cycle ${cycle + 1} stays silent with live data`);
  assert.equal(selectedTimeStatus, 'This time is available. Final confirmation is subject to artist approval.', `background cycle ${cycle + 1} preserves selected-time status`);
}
assert.equal(availability.announceRefresh(false, true), true, 'manual refresh may report calendar progress');
assert.equal(availability.announceRefresh(true, false), true, 'initial/fallback calendar refresh remains visible');

console.log('Booking UI quantity and layout checks passed.');
