// Owns a fresh loopback-only Demo and fresh Chromium contexts. No external providers.
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { spawn } from 'node:child_process';
import { mkdir, mkdtemp, readdir, writeFile } from 'node:fs/promises';
import { deflateSync } from 'node:zlib';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright-core');
const project = path.resolve('.'), binary = process.env.UI_CHECK_BUILD || path.join(project, 'Frontend/GlamByMariya.Web/bin/Release/net10.0');
const parent = path.join(process.env.LOCALAPPDATA, 'Temp/opencode'); await mkdir(parent, { recursive: true });
const temp = await mkdtemp(path.join(parent, 'phase3a-checks-'));
const server = spawn('dotnet', [path.join(binary, 'GlamByMariya.Web.dll'), '--urls=http://127.0.0.1:0', `--contentRoot=${temp}`, `--webroot=${path.join(project, 'Frontend/GlamByMariya.Web/wwwroot')}`,
  '--Database:Provider=Demo', '--DemoAccounts:Presentation=false', '--Authentication:DurableSessionEnforcement=false', '--DemoGallery:Enabled=false', '--DemoCatalog:Enabled=false', '--WebsiteContent:DemoTextEditing=false', '--WebsiteContent:DemoPersistentText=false', '--Supabase:Url=', '--Supabase:AnonKey=', '--Supabase:ServiceKey=', '--SupabaseStorage:ServiceKey=', '--FileStorage:Provider=Local', '--B2:Bucket=', '--B2:Endpoint=', '--B2:KeyId=', '--B2:AppKey=', '--Email:BrevoApiKey=', '--Email:SmtpHost='],
  { env: { ...process.env, PORT: '', ASPNETCORE_ENVIRONMENT: 'Development', DOTNET_ENVIRONMENT: 'Development' }, stdio: ['ignore', 'pipe', 'pipe'] });
let browser, base, checks = 0;
const check = (value, label) => { assert.ok(value, label); console.log('PASS', ++checks, label); };
function chunk(type, data) { const bytes = Buffer.concat([Buffer.from(type), data]); let crc = 0xffffffff; for (const x of bytes) { crc ^= x; for (let i = 0; i < 8; i++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0); } const n = Buffer.alloc(4), c = Buffer.alloc(4); n.writeUInt32BE(data.length); c.writeUInt32BE((crc ^ 0xffffffff) >>> 0); return Buffer.concat([n, bytes, c]); }
const header = Buffer.alloc(13); header.writeUInt32BE(64); header.writeUInt32BE(64, 4); header[8] = 8; header[9] = 2;
const png = Buffer.concat([Buffer.from('89504e470d0a1a0a', 'hex'), chunk('IHDR', header), chunk('IDAT', deflateSync(Buffer.alloc(64 * (1 + 64 * 3), 0))), chunk('IEND', Buffer.alloc(0))]);
const proof = { name: 'synthetic-payment.png', mimeType: 'image/png', buffer: png };
const errors = [];
try {
  base = await new Promise((resolve, reject) => { const timer = setTimeout(() => reject(Error('Startup timeout')), 30000); server.on('error', reject); server.once('exit', c => reject(Error(`Demo exited ${c}`))); server.stderr.resume(); server.stdout.on('data', b => { const m = String(b).match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/); if (m) { clearTimeout(timer); resolve(m[1]); } }); });
  browser = await chromium.launch({ executablePath: process.env.BROWSER_EXECUTABLE || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', headless: true });
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, reducedMotion: 'reduce' });
  await context.route('**/*', r => new URL(r.request().url()).origin === base ? r.continue() : r.abort());
  async function login(ctx, email = 'customer@lumiere.demo', password = 'Customer123!') { const p = await ctx.newPage(); await p.goto(base + '/Account/Login'); await p.fill('[name=email]', email); await p.fill('[name=password]', password); await Promise.all([p.waitForURL(u => !u.pathname.includes('/Account/Login')), p.locator('#auth-form button.button').click()]); return p; }
  const page = await login(context); page.on('pageerror', e => errors.push(e.message));
  let mixedPosts = 0;
  page.on('request', r => { if (r.method() === 'POST' && r.url().endsWith('/Bookings/CreateMixed')) mixedPosts++; });
  let dayNumber = 1;
  async function prepare(count = 3, choice = 'minimum') {
    await page.goto(base + '/Bookings/Create');
    await page.locator('[name=bookingType][value=package]').check();
    await page.locator('[name=people]').fill(String(count)); await page.locator('#mixed-mode').check();
    const names = ['Bridal Makeup', 'Glamour Makeup', 'Soft Glam'];
    for (let i = 0; i < count; i++) { const card = page.locator('.mixed-person').nth(i); await card.locator('select').selectOption({ label: await card.locator('option').filter({ hasText: names[i % 3] }).textContent() }); }
    await page.locator('.mixed-person').first().locator('.mixed-addons input').first().check();
    await page.waitForFunction(() => document.querySelector('#booking-form').dataset.mixedQuoteState === 'ready');
    await page.locator('[data-step-next]').click();
    await page.waitForFunction(() => document.querySelectorAll('#mixed-calendar .calendar-day').length > 0);
    // Use the following month, away from the seeded bookings.
    await page.locator('[data-mixed-next]').click();
    const targetDay = String(dayNumber++).padStart(2, '0');
    await page.locator(`#mixed-calendar .calendar-day[data-date$="-${targetDay}"]`).click();
    await page.locator('#mixed-calendar .time-slot:not(:disabled)').first().click();
    await page.waitForFunction(() => !!document.querySelector('#booking-form').mixedSlot);
    await page.locator('[name=address]').fill('Phase 3A synthetic service address, Quezon City');
    await page.locator('[data-step-next]').click();
    await page.locator('#travel-policy-ack').check(); await page.locator('[data-travel-ack]').click();
    await page.locator('[name=paymentChoice][value=' + choice + ']').check();
    if (choice === 'custom') await page.locator('[name=deposit]').fill(await page.evaluate(() => (Number(document.querySelector('[name=expectedMinimum]').value) + 100).toFixed(2)));
    await page.locator('[name=reference]').fill('P3A-' + randomUUID());
    await page.locator('[name=proof]').setInputFiles(proof); await page.locator('#payment-proof-preview').waitFor({ state: 'visible' });
    await page.locator('[name=agreed]').check();
    check(await page.locator('#mixed-review-people article').count() === count, `${count}-person Step 1 → 2 → 3 summary`);
    return page.evaluate(() => { const f = document.querySelector('#booking-form'); return Object.fromEntries(new FormData(f)); });
  }
  async function screenshot(name) { await page.screenshot({ path: path.join(temp, name + '.png'), fullPage: true }); }
  async function receipt() { await page.locator('#booking-form [type=submit]').click(); await page.locator('#confirmation-ack').check(); await Promise.all([page.waitForURL('**/Bookings/Confirmation/*'), page.locator('#confirmation-submit').click()]); }
  const three = await prepare();
  for (const width of [1440, 768, 390, 320]) { await page.setViewportSize({ width, height: 1000 }); await screenshot('payment-' + width); const sizes = await page.evaluate(() => [innerWidth, document.documentElement.scrollWidth]); if (sizes[1] > sizes[0]) console.log('OVERFLOW', temp, sizes, await page.evaluate(() => [...document.querySelectorAll('body *')].filter(e => e.getBoundingClientRect().right > innerWidth + 1).map(e => [e.tagName, e.className, e.getBoundingClientRect().width, e.getBoundingClientRect().right]).slice(0, 30))); check(sizes[1] <= sizes[0], `Step 3 no horizontal overflow at ${width}`); }
  await page.setViewportSize({ width: 1440, height: 1000 });
  // Keyboard agreement and final action, including rapid repeated submit activation.
  await page.locator('#booking-form [type=submit]').focus(); await page.keyboard.press('Enter');
  await page.locator('#confirmation-ack').focus(); await page.keyboard.press('Space');
  await Promise.all([page.waitForURL('**/Bookings/Confirmation/*'), page.locator('#confirmation-submit').dblclick()]);
  const confirmation = page.url();
  check(mixedPosts === 1, 'double-click final action sends exactly one booking POST');
  check((await page.textContent('body')).includes('Unverified — awaiting Admin review'), 'confirmation explicitly unverified');
  check((await page.textContent('body')).includes('Pending Payment'), 'confirmation pending booking, not confirmed');
  check(await page.locator('.mixed-review-people article').count() === 3, 'confirmation retains all three people');
  for (const width of [1440, 768, 390, 320]) { await page.setViewportSize({ width, height: 1000 }); await screenshot('confirmation-' + width); check(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `confirmation no overflow ${width}`); }
  await Promise.all([page.waitForURL('**/Bookings/Details/*'), page.locator('a:has-text("View appointment details")').click()]);
  check(await page.locator('.mixed-review-people article').count() === 3, 'Customer details retain all people');
  check((await page.textContent('body')).includes('Verified amount paid'), 'details distinguish claimed from verified amount');
  for (const width of [1440, 768, 390, 320]) { await page.setViewportSize({ width, height: 1000 }); await screenshot('details-' + width); check(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `details no overflow ${width}`); }
  const proofLink = await page.locator('a:has-text("View private payment proof")').getAttribute('href');
  check((await context.request.get(base + proofLink)).headers()['content-type'].startsWith('image/'), 'Customer can read own normalized private proof');
  await page.goto(base + '/Bookings'); check((await page.textContent('body')).includes('Mixed-service group · 3 people'), 'mixed booking appears in appointments list');
  async function post(fields, options = {}, ctx = context) { const f = { ...fields, ...options }; delete f.proof; const file = options.proof === null ? {} : { proof: { name: options.proof?.name || 'claim.png', mimeType: options.proof?.mimeType || 'image/png', buffer: options.proof?.buffer || png } }; const r = await ctx.request.post(base + '/Bookings/CreateMixed', { multipart: { ...f, ...file }, headers: { Accept: 'application/json' }, maxRedirects: 0 }); return { status: r.status(), text: await r.text() }; }
  const proofCount = async () => (await readdir(path.join(temp, 'App_Data/Proofs')).catch(() => [])).length;
  const beforeReplay = await proofCount();
  const replay = await post(three);
  check(replay.status === 200 && JSON.parse(replay.text).confirmationUrl === new URL(confirmation).pathname, 'repeated HTTP returns same receipt');
  check(await proofCount() === beforeReplay, 'repeated HTTP leaves no duplicate proof file');
  const alternatePng = Buffer.concat([Buffer.from('89504e470d0a1a0a', 'hex'), chunk('IHDR', header), chunk('IDAT', deflateSync(Buffer.alloc(64 * (1 + 64 * 3), 0), { level: 0 })), chunk('IEND', Buffer.alloc(0))]);
  const equivalentProof = await post(three, { proof: { buffer: alternatePng } });
  check(equivalentProof.status === 200 && JSON.parse(equivalentProof.text).confirmationUrl === new URL(confirmation).pathname, 'different PNG encoding with identical pixels returns original receipt');
  const differentPng = Buffer.concat([Buffer.from('89504e470d0a1a0a', 'hex'), chunk('IHDR', header), chunk('IDAT', deflateSync(Buffer.concat(Array.from({length:64}, () => Buffer.concat([Buffer.from([0]), Buffer.alloc(64 * 3, 255)]))))), chunk('IEND', Buffer.alloc(0))]);
  const differentProof = await post(three, { proof: { buffer: differentPng } });
  check(differentProof.status === 400 && /Submission ID was already used for different booking details/.test(differentProof.text), 'different proof pixels receive the specific mismatch error');
  const changedAddress = await post(three, { address: 'Different synthetic address' });
  check(changedAddress.status === 400 && /Submission ID was already used for different booking details/.test(changedAddress.text), 'changed address receives the specific mismatch error');
  check(await proofCount() === beforeReplay, 'matching and rejected retries leave no additional staged proof');
  check((await context.request.get(base + proofLink)).status() === 200, 'rejected retries preserve original proof');
  await page.setViewportSize({ width: 1440, height: 1000 });
  const five = await prepare(5, 'custom'); await receipt(); check(await page.locator('.mixed-review-people article').count() === 5, 'five-person custom payment reaches receipt');
  check((await page.textContent('main')).includes(Number(five.deposit).toLocaleString('en-US', { minimumFractionDigits: 2 })), 'five-person custom claimed amount preserved');
  const full = await prepare(3, 'full'); check(full.deposit === full.expectedTotal, 'full payment equals server quote'); await receipt(); check((await page.textContent('body')).includes('Unverified — awaiting Admin review'), 'full payment remains unverified');
  const invalid = await prepare(3);
  const beforeInvalid = await proofCount();
  for (const [label, overrides, expected] of [
    ['missing proof', { proof: null }, /Upload a payment/],
    ['forged MIME and filename', { proof: { buffer: Buffer.from('<script>invalid</script>'), name: 'fake.jpg', mimeType: 'image/jpeg' } }, /valid|decodable/],
    ['oversized proof', { proof: { buffer: Buffer.alloc(5000001) } }, /5 MB/],
    ['missing agreement', { agreed: 'false' }, /accept/],
    ['missing travel agreement', { travelAcknowledged: 'false' }, /accept/],
    ['below minimum', { deposit: '1' }, /Choose an amount/],
    ['over total', { deposit: '999999' }, /Choose an amount/],
    ['fractional cent', { deposit: String(Number(invalid.deposit) + 0.001) }, /Choose an amount/],
    ['unapproved payment method', { method: 'Cash' }, /GCash|Maya|Bank/],
    ['stale total', { expectedTotal: '1' }, /price.*changed/],
    ['expired time', { mixedStart: '2020-01-01T08:00' }, /month|future|available/],
    ['bad selection', { mixedSelection: '{invalid' }, /valid service/],
    ['missing submission identifier', { submissionId: '' }, /Reload|Check the date/],
    ['unknown service', { mixedSelection: JSON.stringify([{ serviceId: randomUUID(), addOnIds: [] }, { serviceId: randomUUID(), addOnIds: [] }]) }, /active service/],
    ['off-grid time', { mixedStart: invalid.mixedStart.slice(0, 14) + '07' }, /available/],
  ]) { const r = await post(invalid, overrides); check(r.status === 400 && expected.test(r.text), `${label} rejected with useful error: ${r.status} ${r.text}`); }
  check(await proofCount() === beforeInvalid, 'all rejected submissions clean stored proof files');
  // Client errors and preview are exercised in the actual browser too.
  await page.locator('[name=proof]').setInputFiles({ name: 'fake.jpg', mimeType: 'image/jpeg', buffer: Buffer.from('not an image') });
  await page.waitForFunction(() => !document.querySelector('#payment-proof').validity.valid);
  check((await page.locator('#payment-proof-meta').textContent()).includes('not a JPG'), 'browser explains invalid image contents');
  await page.locator('[name=proof]').setInputFiles({ name: 'large.png', mimeType: 'image/png', buffer: Buffer.alloc(5000001) });
  check((await page.locator('#payment-proof-meta').textContent()).includes('5 MB'), 'browser explains oversized proof');
  await page.locator('[name=proof]').setInputFiles(proof); await page.locator('#payment-proof-preview').waitFor({ state: 'visible' });
  await page.locator('[name=agreed]').uncheck(); await page.locator('#booking-form [type=submit]').click();
  check(!await page.locator('#booking-confirmation').isVisible(), 'missing agreement does not open confirmation');
  // Simultaneous distinct submissions for the same slot: one reservation only.
  const race = await Promise.all([post(invalid, { submissionId: randomUUID(), reference: 'RACE-A-' + randomUUID() }), post(invalid, { submissionId: randomUUID(), reference: 'RACE-B-' + randomUUID() })]);
  check(race.filter(r => r.status === 200).length === 1 && race.filter(r => r.status === 400).length === 1, 'atomic competing requests produce one winner');
  const conflicted = await post(invalid, { submissionId: randomUUID(), reference: randomUUID() }); check(conflicted.status === 400 && /available|reserved/.test(conflicted.text), 'stale conflicting slot rejected');
  await page.locator('[name=agreed]').check();
  await page.locator('#booking-form [type=submit]').click(); await page.locator('#confirmation-ack').check();
  await Promise.all([page.waitForResponse(r => r.url().endsWith('/Bookings/CreateMixed') && r.status() === 400), page.locator('#confirmation-submit').click()]);
  await page.waitForFunction(() => document.querySelector('#booking-form').dataset.submissionState === 'idle');
  check(page.url().includes('/Bookings/Create') && /available|reserved/.test(await page.locator('.booking-step-message').textContent()), 'browser shows rejected-slot error, never a receipt');
  const duplicateFields = { ...invalid, submissionId: randomUUID(), reference: randomUUID(), mixedStart: invalid.mixedStart.replace(/-\d\dT/, '-25T') };
  const parallel = await Promise.all([post(duplicateFields), post(duplicateFields)]);
  check(parallel.every(r => r.status === 200) && parallel[0].text === parallel[1].text, 'simultaneous duplicate HTTP requests return one booking');
  const jpg = await page.screenshot({ type: 'jpeg' });
  const jpeg = await post(duplicateFields, { submissionId: randomUUID(), reference: randomUUID(), mixedStart: duplicateFields.mixedStart.replace('-25T', '-27T'), proof: { name: 'proof.txt', mimeType: 'text/plain', buffer: jpg } });
  check(jpeg.status === 200, 'actual JPG accepted by decoded content, not claimed extension or MIME');
  const noCsrf = await post(invalid, { __RequestVerificationToken: '' }); check(noCsrf.status === 400, 'missing antiforgery rejected');
  const anon = await browser.newContext(); const anonPost = await post(invalid, {}, anon); check(anonPost.status === 302, 'anonymous submission denied');
  check((await anon.request.get(base + proofLink, { maxRedirects: 0 })).status() === 302, 'anonymous proof access denied');
  const admin = await browser.newContext(); await login(admin, 'admin@lumiere.demo', 'Admin123!'); const adminPost = await post(invalid, {}, admin); check(adminPost.status === 302 || adminPost.status === 403, 'Admin cannot submit Customer booking');
  // Storage failure: make the Proofs directory unwritable by replacing it with a file in this isolated root.
  const { rename, unlink } = await import('node:fs/promises'); const folder = path.join(temp, 'App_Data/Proofs');
  await rename(folder, folder + '.saved'); await writeFile(folder, 'synthetic storage fault');
  try { const failed = await post(duplicateFields, { submissionId: randomUUID(), mixedStart: duplicateFields.mixedStart.replace('-25T', '-26T'), reference: randomUUID() }); check(failed.status === 503, 'storage failure rejects request, no receipt'); }
  finally { await unlink(folder); await rename(folder + '.saved', folder); }
  for (const [count, day, choice] of [[1, '18', 'minimum'], [4, '19', 'full']]) {
    await page.goto(base + '/Bookings/Create');
    if (count > 1) { await page.locator('[name=bookingType][value=package]').check(); await page.locator('[name=people]').fill(String(count)); }
    await page.waitForFunction(() => document.querySelector('#booking-form').dataset.priceState === 'ready');
    await page.locator('[data-step-next]').click();
    if (await page.locator('#booking-form').getAttribute('data-current-step') !== 'schedule') console.log('LEGACY_STEP', count, await page.evaluate(() => ({ step: document.querySelector('#booking-form').dataset.currentStep, message: document.querySelector('.booking-step-message').textContent, invalid: [...document.querySelectorAll('#booking-form input, #booking-form select, #booking-form textarea')].filter(e => e.willValidate && !e.validity.valid).map(e => [e.name, e.validationMessage, e.value]) })));
    await page.locator('[data-month-next]').click();
    await page.locator(`[data-calendar-days] [data-date$="-${day}"]:not(:disabled)`).click();
    await page.locator('[data-time-slots] button:not(:disabled)').first().click();
    await page.locator('[name=address]').fill('Legacy regression synthetic address, Quezon City');
    await page.waitForFunction(() => document.querySelector('[data-validation-status]').dataset.state === 'valid' && document.querySelector('#booking-form').dataset.priceState === 'ready');
    await page.locator('[data-step-next]').click(); await page.locator('#travel-policy-ack').check(); await page.locator('[data-travel-ack]').click();
    await page.locator(`[name=paymentChoice][value=${choice}]`).check(); await page.locator('[name=reference]').fill('LEGACY-' + randomUUID());
    await page.locator('[name=proof]').setInputFiles(proof); await page.locator('#payment-proof-preview').waitFor({ state: 'visible' }); await page.locator('[name=agreed]').check();
    await page.locator('#booking-form [type=submit]').click(); await page.locator('#confirmation-ack').check();
    const result = page.waitForResponse(r => r.url().endsWith('/Bookings/Create') && r.request().method() === 'POST');
    await Promise.all([page.waitForURL(base + '/Bookings'), page.locator('#confirmation-submit').click()]);
    check((await result).status() === 302 && (await page.textContent('main')).includes('Pending Payment'), `${count === 1 ? 'Individual' : 'Same-Service group'} real browser proof-backed submission`);
  }
  check(errors.length === 0, 'no browser JavaScript exceptions: ' + errors.join('; '));
  console.log(`ALL ${checks} PHASE 3A CHECKS PASSED. Screenshots and isolated storage: ${temp}`);
} finally { if (browser) await browser.close(); server.kill(); }
