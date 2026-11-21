// Fresh, loopback-only Demo and browser contexts; no external providers or user data.
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { spawn } from 'node:child_process';
import { mkdir, mkdtemp, writeFile } from 'node:fs/promises';
import { deflateSync } from 'node:zlib';
import { randomUUID } from 'node:crypto';
import path from 'node:path';

const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright-core');
const project = path.resolve('.'), binary = process.env.UI_CHECK_BUILD || path.join(project, 'Frontend/GlamByMariya.Web/bin/Release/net10.0');
const parent = path.join(process.env.LOCALAPPDATA, 'Temp/opencode'); await mkdir(parent, { recursive: true });
const temp = await mkdtemp(path.join(parent, 'phase3b-checks-'));
const server = spawn('dotnet', [path.join(binary, 'GlamByMariya.Web.dll'), '--urls=http://127.0.0.1:0', `--contentRoot=${temp}`, `--webroot=${path.join(project, 'Frontend/GlamByMariya.Web/wwwroot')}`,
  '--Database:Provider=Demo', '--DemoAccounts:Presentation=true', '--Authentication:DurableSessionEnforcement=false', '--DemoGallery:Enabled=false', '--DemoCatalog:Enabled=false', '--WebsiteContent:DemoTextEditing=false', '--WebsiteContent:DemoPersistentText=false', '--Supabase:Url=', '--Supabase:AnonKey=', '--Supabase:ServiceKey=', '--SupabaseStorage:ServiceKey=', '--FileStorage:Provider=Local', '--B2:Bucket=', '--B2:Endpoint=', '--B2:KeyId=', '--B2:AppKey=', '--Email:BrevoApiKey=', '--Email:SmtpHost='],
{ env: { ...process.env, PORT: '', ASPNETCORE_ENVIRONMENT: 'Development', DOTNET_ENVIRONMENT: 'Development' }, stdio: ['ignore', 'pipe', 'pipe'] });
let browser, base, checks = 0;
const check = (condition, label) => { assert.ok(condition, label); console.log('PASS', ++checks, label); };
function chunk(type, data) { const bytes = Buffer.concat([Buffer.from(type), data]); let crc = 0xffffffff; for (const x of bytes) { crc ^= x; for (let i = 0; i < 8; i++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0); } const n = Buffer.alloc(4), c = Buffer.alloc(4); n.writeUInt32BE(data.length); c.writeUInt32BE((crc ^ 0xffffffff) >>> 0); return Buffer.concat([n, bytes, c]); }
const header = Buffer.alloc(13); header.writeUInt32BE(64); header.writeUInt32BE(64, 4); header[8] = 8; header[9] = 2;
const png = Buffer.concat([Buffer.from('89504e470d0a1a0a', 'hex'), chunk('IHDR', header), chunk('IDAT', deflateSync(Buffer.alloc(64 * (1 + 64 * 3), 0))), chunk('IEND', Buffer.alloc(0))]);
const proof = { name: 'synthetic.png', mimeType: 'image/png', buffer: png };
const errors = [];
try {
  base = await new Promise((resolve, reject) => { const timer = setTimeout(() => reject(Error('Demo startup timeout')), 30000); server.on('error', reject); server.once('exit', code => reject(Error(`Demo exited ${code}`))); server.stderr.resume(); server.stdout.on('data', bytes => { const match = String(bytes).match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/); if (match) { clearTimeout(timer); resolve(match[1]); } }); });
  browser = await chromium.launch({ executablePath: process.env.BROWSER_EXECUTABLE || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', headless: true });
  async function context() { const c = await browser.newContext({ viewport: { width: 1440, height: 900 }, reducedMotion: 'reduce' }); await c.route('**/*', r => new URL(r.request().url()).origin === base ? r.continue() : r.abort()); return c; }
  async function login(c, email, password) { const p = await c.newPage(); p.on('pageerror', e => errors.push(e.message)); await p.goto(base + '/Account/Login'); await p.fill('[name=email]', email); await p.fill('[name=password]', password); await Promise.all([p.waitForURL(u => !u.pathname.includes('/Account/Login')), p.locator('#auth-form button.button').click()]); return p; }
  const customerContext = await context(), ownerContext = await context(), adminContext = await context(), anon = await context();
  const customer = await login(customerContext, 'presentation-customer@example.test', 'Customer123!');
  const owner = await login(ownerContext, 'presentation-owner@example.test', 'Owner123!');
  const admin = await login(adminContext, 'presentation-admin@example.test', 'Admin123!');
  async function screenshots(page, label) {
    for (const width of [1440, 768, 390, 320]) {
      await page.setViewportSize({ width, height: 900 }); await page.screenshot({ path: path.join(temp, `${label}-${width}.png`), fullPage: true });
      const geometry = await page.evaluate(() => ({ width: innerWidth, scroll: document.documentElement.scrollWidth, bad: [...document.querySelectorAll('main *')].filter(x => x.getBoundingClientRect().right > innerWidth + 1).slice(0, 4).map(x => x.outerHTML.slice(0, 150)) }));
      check(geometry.scroll <= geometry.width, `${label} ${width}px has no horizontal overflow: ${JSON.stringify(geometry)}`);
    }
    await page.setViewportSize({ width: 1440, height: 900 });
  }
  async function makeBooking(day) {
    await customer.goto(base + '/Bookings/Create'); await customer.locator('[name=bookingType][value=package]').check(); await customer.locator('[name=people]').fill('3'); await customer.locator('#mixed-mode').check();
    for (const [i, name] of ['Bridal Makeup', 'Glamour Makeup', 'Soft Glam'].entries()) { const select = customer.locator('.mixed-person').nth(i).locator('select'); await select.selectOption({ label: await select.locator('option').filter({ hasText: name }).textContent() }); }
    await customer.locator('.mixed-person').first().locator('.mixed-addons input').first().check();
    await customer.waitForFunction(() => document.querySelector('#booking-form').dataset.mixedQuoteState === 'ready'); await customer.locator('[data-step-next]').click();
    await customer.waitForFunction(() => document.querySelectorAll('#mixed-calendar .calendar-day').length > 0); await customer.locator('[data-mixed-next]').click();
    await customer.locator(`#mixed-calendar [data-date$="-${String(day).padStart(2, '0')}"]`).click(); await customer.locator('#mixed-calendar .time-slot:not(:disabled)').first().click();
    await customer.waitForFunction(() => Boolean(document.querySelector('#booking-form').mixedSlot));
    const slot = await customer.evaluate(() => document.querySelector('#booking-form').mixedSlot.start);
    await customer.locator('[name=address]').fill('Synthetic Phase 3B service address, Quezon City'); await customer.locator('[data-step-next]').click();
    await customer.locator('#travel-policy-ack').check(); await customer.locator('[data-travel-ack]').click();
    await customer.locator('[name=reference]').fill('PHASE3B-' + randomUUID()); await customer.locator('[name=proof]').setInputFiles(proof); await customer.locator('#payment-proof-preview').waitFor({ state: 'visible' });
    await customer.locator('[name=agreed]').check(); await customer.locator('#booking-form [type=submit]').click(); await customer.locator('#confirmation-ack').check();
    await Promise.all([customer.waitForURL('**/Bookings/Confirmation/*'), customer.locator('#confirmation-submit').click()]);
    const id = new URL(customer.url()).pathname.split('/').pop(), reference = await customer.locator('.booking-reference').innerText();
    return { id, reference, slot, customerDetails: base + '/Bookings/Details/' + id, adminDetails: base + '/Admin/Details/' + id };
  }
  async function token(p) { return p.locator('input[name=__RequestVerificationToken]').first().inputValue(); }
  const denied = response => response.status() === 403 || (response.status() === 302 && /AccessDenied/i.test(response.headers().location || ''));
  async function postAction(p, booking, fields) { return p.context().request.post(base + '/Admin/Action', { form: { __RequestVerificationToken: await token(p), id: booking.id, ...fields }, maxRedirects: 0 }); }
  async function submitAction(p, button) { await Promise.all([p.waitForResponse(r => r.url().endsWith('/Admin/Action') && r.request().method() === 'POST'), button.click()]); await p.waitForLoadState('load'); }
  async function requestTime(booking, dateDay, inspectQuote = false) {
    await customer.goto(booking.customerDetails); await customer.locator('[data-month-next]').click();
    await customer.locator(`[data-calendar-days] [data-date$="-${String(dateDay).padStart(2, '0')}"]:not(:disabled)`).click();
    await customer.locator('[data-time-slots] button:not(:disabled)').first().click();
    await customer.waitForFunction(() => document.querySelector('[data-validation-status]').dataset.state === 'valid');
    const proposed = await customer.locator('[name=start]').inputValue();
    await Promise.all([customer.waitForResponse(r => r.url().endsWith('/Bookings/Reschedule') && r.request().method() === 'POST'), customer.locator('form[action="/Bookings/Reschedule"] button[type=submit]').click()]);
    await customer.locator('form[action="/Bookings/ConfirmReschedule"]').waitFor({ state: 'visible' });
    if (inspectQuote) {
      const text = await customer.locator('main').innerText();
      check(/Current approved total/.test(text) && /Proposed total/.test(text) && /Difference/.test(text) && /200\.00/.test(text), 'Customer reviews actual increased price and fee before consenting: ' + text);
      await screenshots(customer, 'customer-reschedule-review');
      const withoutConsent = await customerContext.request.post(base + '/Bookings/ConfirmReschedule', { form: { __RequestVerificationToken: await token(customer), id: booking.id, quoteId: await customer.locator('[name=quoteId]').inputValue() }, maxRedirects: 0 });
      check(withoutConsent.status() === 302, 'missing Customer consent rejected');
    }
    await customer.locator('form[action="/Bookings/ConfirmReschedule"] [name=agreed]').check();
    await Promise.all([customer.waitForResponse(r => r.url().endsWith('/Bookings/ConfirmReschedule') && r.request().method() === 'POST'), customer.locator('form[action="/Bookings/ConfirmReschedule"] button').click()]);
    await customer.waitForLoadState('load');
    return proposed;
  }
  const first = await makeBooking(1); check((await customer.textContent('main')).includes('Unverified'), 'Customer submits mixed booking, payment remains unverified');
  await owner.goto(base + '/Admin/Appointments?mode=list'); let row = owner.locator(`[data-appointment-row]`).filter({ hasText: first.reference });
  check(await row.count() === 1 && /Mixed-service/.test(await row.innerText()) && /3 people/.test(await row.innerText()) && /285 min/.test(await row.innerText()), 'SuperAdmin appointment list shows mixed reference, people and duration');
  await screenshots(owner, 'appointment-list');
  const [year, month, day] = first.slot.slice(0, 10).split('-').map(Number);
  await owner.goto(`${base}/Admin/Appointments?mode=calendar&year=${year}&month=${month}&day=${day}`);
  const entry = owner.locator(`[data-calendar-booking="${first.id}"]`);
  check(await entry.count() === 1 && /285 min/.test(await entry.innerText()) && /Preparation buffer until/.test(await entry.innerText()), 'combined calendar displays complete mixed occupancy and buffer');
  await screenshots(owner, 'appointment-calendar');
  await owner.goto(first.adminDetails);
  check(await owner.locator('.mixed-review-people article').count() === 3 && /Add-on total/.test(await owner.locator('.mixed-review').innerText()), 'SuperAdmin sees full immutable per-person breakdown');
  const proofUrl = await owner.locator('.private-proof a').getAttribute('href'); const proofResponse = await ownerContext.request.get(base + proofUrl);
  check(proofResponse.status() === 200 && proofResponse.headers()['content-type'].startsWith('image/') && /no-store/.test(proofResponse.headers()['cache-control']), 'authorized private proof is image and not cacheable');
  const picture = await owner.locator('.private-proof img').evaluate(img => ({ complete: img.complete, width: img.naturalWidth })); check(picture.complete && picture.width > 0, 'proof image renders in Admin review');
  await screenshots(owner, 'admin-detail');
  const customerProof = await customerContext.request.get(base + proofUrl, { maxRedirects: 0 }); check([302, 403].includes(customerProof.status()), 'Customer cannot access Admin proof endpoint');
  const anonymousProof = await anon.request.get(base + proofUrl, { maxRedirects: 0 }); check(anonymousProof.status() === 302, 'anonymous user cannot access private proof');
  const adminProof = await adminContext.request.get(base + proofUrl, { maxRedirects: 0 }); check(denied(adminProof), 'ordinary Admin cannot view mixed private proof');
  check(denied(await adminContext.request.get(first.adminDetails, { maxRedirects: 0 })), 'ordinary Admin cannot access mixed details');
  check((await ownerContext.request.get(base + proofUrl.replace(`bookingId=${first.id}`, 'bookingId=' + randomUUID()), { maxRedirects: 0 })).status() === 404, 'proof must match its booking association');
  await admin.goto(base + '/Admin/Appointments?mode=list'); row = admin.locator('[data-appointment-row]').filter({ hasText: first.reference }); check(/SuperAdmin review required/.test(await row.innerText()), 'ordinary Admin sees restricted metadata without review access');
  await admin.goto(base + '/Admin/Payments'); check(!(await admin.textContent('main')).includes(first.reference), 'ordinary Admin payment list does not leak restricted mixed claims');
  const claim = owner.locator('.mixed-payment-claim'), paymentId = await claim.getAttribute('data-payment-id'), revision = await claim.getAttribute('data-payment-revision');
  const noToken = await ownerContext.request.post(base + '/Admin/Action', { form: { id: first.id, operation: 'verify', paymentId, paymentRevision: revision, reviewed: 'true' }, maxRedirects: 0 }); check(noToken.status() === 400, 'mixed review action requires antiforgery');
  const adminAction = await postAction(admin, first, { operation: 'verify', paymentId, paymentRevision: revision, reviewed: 'true' }); check(denied(adminAction), 'ordinary Admin cannot verify mixed payment even by forged POST');
  const beforeReview = await postAction(owner, first, { operation: 'approve' }); check(beforeReview.status() === 302, 'unverified approval request safely redirects');
  await owner.reload(); check((await owner.textContent('main')).includes('Verify at least the required down payment'), 'unverified approval remains blocked');
  const staleRevision = await postAction(owner, first, { operation: 'verify', paymentId, paymentRevision: '99', reviewed: 'true' }); check(staleRevision.status() === 302, 'stale review safely rejected');
  await owner.reload(); check((await owner.textContent('main')).includes('claim changed'), 'stale proof revision asks reviewer to reload');
  const missingReview = await postAction(owner, first, { operation: 'verify', paymentId, paymentRevision: revision }); check(missingReview.status() === 302, 'missing review acknowledgement safely rejected');
  await owner.reload(); check((await owner.textContent('main')).includes('Confirm that you checked'), 'manual transfer review acknowledgement enforced on server');
  await owner.locator('[data-decision=verify] input[name=reviewed]').focus(); await owner.keyboard.press('Space');
  await submitAction(owner, owner.locator('[data-decision=verify] button'));
  check((await owner.textContent('main')).includes('Verified') && await owner.locator('.admin-mixed-details').getAttribute('data-status') === 'Confirmed', 'SuperAdmin verifies actual claim and confirms booking in one operation');
  check((await owner.textContent('main')).includes('Business Owner') && (await owner.textContent('main')).includes('Reviewed by'), 'reviewer and review time are shown');
  const duplicate = await postAction(owner, first, { operation: 'verify', paymentId, paymentRevision: revision, reviewed: 'true' }); check(duplicate.status() === 302, 'duplicate verification handled safely');
  await owner.reload(); check((await owner.textContent('main')).includes('already verified'), 'duplicate verification is explicitly rejected');
  const blockedCancel = await postAction(owner, first, { operation: 'cancel', reason: 'Synthetic staff request' }); check(blockedCancel.status() === 302, 'unconfirmed staff cancellation safely rejected');
  await owner.reload(); check((await owner.textContent('main')).includes('Confirm the cancellation') && await owner.locator('.admin-mixed-details').getAttribute('data-status') === 'Confirmed', 'unconfirmed cancellation preserves reservation');
  await screenshots(owner, 'staff-cancellation-form');
  const staffPending = await makeBooking(19); await owner.goto(staffPending.adminDetails);
  const cancelForm = owner.locator('[data-decision=staff-cancel]'); await cancelForm.locator('textarea').fill('Synthetic staff cancellation'); await cancelForm.locator('[name=confirmed]').check();
  await submitAction(owner, cancelForm.locator('button'));
  check(await owner.locator('.admin-mixed-details').getAttribute('data-status') === 'Cancelled' && (await owner.textContent('main')).includes('Unverified'), 'staff cancellation retains unverified claim');
  await customer.goto(staffPending.customerDetails);
  check((await customer.textContent('main')).includes('Mariya will contact you about verified payments and any applicable refund'), 'Customer sees manual refund coordination notice');
  await screenshots(customer, 'staff-cancellation-customer');
  const rejected = await makeBooking(3); await owner.goto(rejected.adminDetails);
  const rejectForm = owner.locator('[data-decision=reject]'); await rejectForm.locator('textarea').fill('Synthetic schedule rejection'); await rejectForm.locator('[type=checkbox]').check();
  await submitAction(owner, rejectForm.locator('button'));
  check(await owner.locator('.admin-mixed-details').getAttribute('data-status') === 'Rejected' && (await owner.textContent('main')).includes('Synthetic schedule rejection'), 'SuperAdmin rejects with reason and preserves history');
  const [ry, rm, rd] = rejected.slot.slice(0, 10).split('-').map(Number); await owner.goto(`${base}/Admin/Appointments?mode=calendar&year=${ry}&month=${rm}&day=${rd}`);
  check((await owner.locator(`[data-calendar-booking="${rejected.id}"]`).innerText()).includes('No pending/confirmed reservation held'), 'rejected calendar entry no longer reserves time');
  await customer.goto(base + '/Bookings/Create');
  const availability = await customer.evaluate(async ({ slot }) => { const f = document.querySelector('#booking-form'), token = f.querySelector('[name=__RequestVerificationToken]').value; const ids = [...f.querySelectorAll('[name=serviceId]')].map(x => x.value); const people = [{serviceId:ids[0],addOnIds:[]},{serviceId:ids[0],addOnIds:[]}]; const r = await fetch('/Bookings/MixedAvailability', {method:'POST',headers:{'Content-Type':'application/json','RequestVerificationToken':token},body:JSON.stringify({people,month:slot.slice(0,7)})}); return (await r.json()).days.flatMap(d=>d.slots).find(s=>s.start===slot)?.available; }, rejected);
  check(availability === true, 'rejected reservation is actually available to a new request');
  const proposed = await requestTime(first, 5); await owner.goto(first.adminDetails);
  if (!await owner.locator('[data-decision=reschedule]').count()) console.log('RESCHEDULE_DIAGNOSTIC', proposed, await customer.locator('main').innerText(), await owner.locator('main').innerText());
  check(/original slot remains reserved/i.test(await owner.textContent('main')) && (await owner.locator('.admin-mixed-details').getAttribute('data-start')).startsWith(first.slot), 'Customer reschedule request preserves original reservation');
  await screenshots(owner, 'reschedule-request');
  await owner.locator('[data-decision=reschedule] input[type=checkbox]').check(); await submitAction(owner, owner.locator('[data-decision=reschedule] button'));
  if (!(await owner.locator('.admin-mixed-details').getAttribute('data-start')).startsWith(proposed)) console.log('APPROVAL_DIAGNOSTIC', proposed, await owner.locator('main').innerText());
  check((await owner.locator('.admin-mixed-details').getAttribute('data-start')).startsWith(proposed) && /reschedule/i.test(await owner.textContent('main')), 'SuperAdmin approves available mixed replacement and records history');
  const competingRequest = await requestTime(first, 7); const competitor = await makeBooking(7);
  await owner.goto(first.adminDetails); await owner.locator('[data-decision=reschedule] input[type=checkbox]').check(); await submitAction(owner, owner.locator('[data-decision=reschedule] button'));
  check((await owner.locator('.admin-mixed-details').getAttribute('data-start')).startsWith(proposed) && /available|overlap/i.test(await owner.textContent('main')), 'conflicting reschedule rejected; original retained');
  await owner.goto(competitor.adminDetails); await owner.locator('[data-decision=reject] textarea').fill('Release synthetic competing slot'); await owner.locator('[data-decision=reject] input[type=checkbox]').check(); await submitAction(owner, owner.locator('[data-decision=reject] button'));
  await owner.goto(first.adminDetails); await owner.locator('[data-decision=reschedule] input[type=checkbox]').check(); await submitAction(owner, owner.locator('[data-decision=reschedule] button'));
  check((await owner.locator('.admin-mixed-details').getAttribute('data-start')).startsWith(competingRequest), 'reschedule succeeds after competing reservation is released');
  await customer.goto(first.customerDetails); await screenshots(customer, 'customer-details');
  const cancel = customer.locator('form[action="/Bookings/Cancel"]'); await cancel.locator('[name=reason]').fill('Synthetic customer cancellation'); await cancel.locator('[name=confirmed]').check();
  await Promise.all([customer.waitForResponse(r => r.url().endsWith('/Bookings/Cancel') && r.request().method() === 'POST'), cancel.locator('button').click()]); await customer.waitForLoadState('load');
  check((await customer.textContent('main')).includes('Cancelled') && (await customer.textContent('main')).includes('Synthetic customer cancellation'), 'Customer cancellation records reason and releases confirmed booking');
  const raceA = await makeBooking(9), raceB = await makeBooking(11);
  for (const booking of [raceA, raceB]) {
    await owner.goto(booking.adminDetails);
    await owner.locator('[data-decision=verify] input[name=reviewed]').check();
    await submitAction(owner, owner.locator('[data-decision=verify] button'));
    check(await owner.locator('.admin-mixed-details').getAttribute('data-status') === 'Confirmed', 'synthetic competing booking verified');
  }
  const raceStartA = await requestTime(raceA, 13), raceStartB = await requestTime(raceB, 13);
  check(raceStartA === raceStartB, 'two independent Customer requests propose the same unreserved time');
  await owner.goto(raceA.adminDetails);
  const raceToken = await token(owner), raceQuotes = [];
  for (const booking of [raceA, raceB]) { await owner.goto(booking.adminDetails); raceQuotes.push(await owner.locator('[data-decision=reschedule] [name=rescheduleQuoteId]').inputValue()); }
  const simultaneous = await Promise.all([raceA, raceB].map((booking, i) => ownerContext.request.post(base + '/Admin/Action', { form: { __RequestVerificationToken: raceToken, id: booking.id, operation: 'reschedule', requestedStart: raceStartA, rescheduleQuoteId: raceQuotes[i] }, maxRedirects: 0 })));
  check(simultaneous.every(r => r.status() === 302), 'concurrent Admin changes receive normal action responses');
  const starts = [];
  for (const booking of [raceA, raceB]) { await owner.goto(booking.adminDetails); starts.push(await owner.locator('.admin-mixed-details').getAttribute('data-start')); }
  check(starts.filter(value => value.startsWith(raceStartA)).length === 1, 'concurrent Admin reschedules reserve the same target only once');
  const feeBooking = await makeBooking(15); await owner.goto(feeBooking.adminDetails);
  await owner.locator('[data-decision=verify] input[name=reviewed]').check(); await submitAction(owner, owner.locator('[data-decision=verify] button'));
  await owner.goto(base + '/Admin/Settings');
  const feeSettings = await ownerContext.request.post(base + '/Admin/FeeSettings', { form: { __RequestVerificationToken: await token(owner), earlyFeeEnabled: 'true', earlyStartHour: '5', earlyFee: '200', lateFeeEnabled: 'false', lateStartHour: '17', lateEndHour: '23', lateFee: '0' }, maxRedirects: 0 });
  check(feeSettings.status() === 302, 'synthetic SuperAdmin fee settings saved in isolated Demo');
  const feeStart = await requestTime(feeBooking, 16, true);
  check(feeStart.endsWith('T05:00'), 'early-fee start selected');
  await owner.goto(feeBooking.adminDetails); check((await owner.textContent('main')).includes('200.00'), 'SuperAdmin sees consented fee difference');
  await owner.locator('[data-decision=reschedule] input[type=checkbox]').check(); await submitAction(owner, owner.locator('[data-decision=reschedule] button'));
  check((await owner.locator('.admin-mixed-details').getAttribute('data-start')).startsWith(feeStart), 'SuperAdmin approves consented fee-changing reschedule');
  await customer.goto(feeBooking.customerDetails);
  check((await customer.textContent('main')).includes('Your rescheduling request has been approved. If your updated appointment has additional charges or payment differences, please coordinate directly with Glam by Mariya for payment or refund arrangements.'), 'Customer sees exact manual-payment reminder after approval');
  await owner.goto(base + '/Admin/Activity?category=Booking'); const activity = await owner.textContent('main');
  check(/Payment verified and appointment confirmed/.test(activity) && /Appointment rejected/.test(activity) && /Reschedule approved/.test(activity), 'SuperAdmin Activity Logs contain successful staff decisions');
  check(/in memory|not complete, durable or tamper-resistant/.test(activity), 'Activity Logs disclose non-durable Demo history');
  check(errors.length === 0, 'no uncaught browser exceptions: ' + errors.join('; '));
  console.log(`ALL ${checks} PHASE 3B BROWSER CHECKS PASSED; screenshots: ${temp}`);
} finally { if (browser) await browser.close(); server.kill(); }
