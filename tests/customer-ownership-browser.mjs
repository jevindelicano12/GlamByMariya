// Separate synthetic Customers in one fresh, loopback-only in-memory Demo.
// Requires the test-only Phase3BFixtures startup hook in FIXTURE_BUILD.
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { spawn } from 'node:child_process';
import { mkdir, mkdtemp, readFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import path from 'node:path';

const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright-core');
const project = path.resolve('.');
const bin = process.env.FIXTURE_BUILD || path.join(process.env.LOCALAPPDATA, 'Temp/opencode/ownership-fixture-build');
const parent = path.join(process.env.LOCALAPPDATA, 'Temp/opencode');
await mkdir(parent, { recursive: true });
const root = await mkdtemp(path.join(parent, 'customer-ownership-'));
const server = spawn('dotnet', [path.join(bin, 'GlamByMariya.Web.dll'), '--urls=http://127.0.0.1:0', `--contentRoot=${root}`,
  `--webroot=${path.join(project, 'Frontend/GlamByMariya.Web/wwwroot')}`, '--Database:Provider=Demo', '--DemoAccounts:Presentation=true',
  '--Authentication:DurableSessionEnforcement=false', '--DemoGallery:Enabled=false', '--DemoCatalog:Enabled=false', '--DemoAvatar:Enabled=false',
  '--WebsiteContent:DemoTextEditing=false', '--WebsiteContent:DemoPersistentText=false', '--Supabase:Url=', '--Supabase:AnonKey=',
  '--Supabase:ServiceKey=', '--SupabaseStorage:ServiceKey=', '--FileStorage:Provider=Local', '--B2:Bucket=', '--B2:Endpoint=',
  '--B2:KeyId=', '--B2:AppKey=', '--Email:BrevoApiKey=', '--Email:SmtpHost='],
  { env: { ...process.env, PORT: '', ASPNETCORE_ENVIRONMENT: 'Development', DOTNET_ENVIRONMENT: 'Development',
    DOTNET_STARTUP_HOOKS: path.join(bin, 'Phase3BFixtures.dll'), PHASE3B_FIXTURE_ROOT: root, OWNERSHIP_BROWSER_FIXTURES: 'true' },
    stdio: ['ignore', 'pipe', 'pipe'] });
let browser, checks = 0;
const check = (condition, message) => { assert.ok(condition, message); console.log(`PASS ${++checks}: ${message}`); };

try {
  const base = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(Error('isolated Demo startup timeout')), 30000);
    server.once('error', error => { clearTimeout(timer); reject(error); });
    server.once('exit', code => { clearTimeout(timer); reject(Error(`isolated Demo exited ${code}`)); });
    server.stderr.resume();
    server.stdout.on('data', chunk => { const m = String(chunk).match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/); if (m) { clearTimeout(timer); resolve(m[1]); } });
  });
  const ids = JSON.parse(await readFile(path.join(root, 'fixtures.json'), 'utf8'));
  const aId = ids['ownership-a'], bId = ids['ownership-b'];
  assert.ok(aId && bId && aId !== bId, 'distinct fixture bookings');
  browser = await chromium.launch({ executablePath: process.env.BROWSER_EXECUTABLE || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', headless: true });
  async function context() {
    const ctx = await browser.newContext();
    await ctx.route('**/*', route => new URL(route.request().url()).origin === base ? route.continue() : route.abort());
    return ctx;
  }
  async function login(email, password) {
    const ctx = await context(), page = await ctx.newPage();
    await page.goto(base + '/Account/Login');
    await page.fill('[name=email]', email); await page.fill('[name=password]', password);
    await Promise.all([page.waitForURL(u => !u.pathname.includes('/Account/Login')), page.locator('#auth-form button.button').click()]);
    return { ctx, page };
  }
  const a = await login('presentation-customer@example.test', 'Customer123!');
  const b = await login('ownership-b@example.test', 'SyntheticCustomerB123!');
  check(a.ctx !== b.ctx && a.page.context() !== b.page.context(), 'Customers use independently authenticated browser contexts');
  const aCookie = (await a.ctx.cookies()).find(cookie => cookie.name === '.AspNetCore.Cookies');
  const bCookie = (await b.ctx.cookies()).find(cookie => cookie.name === '.AspNetCore.Cookies');
  check(Boolean(aCookie && bCookie && aCookie.value !== bCookie.value), 'Customer authentication cookies are distinct');
  const empty = await context();
  check((await empty.cookies()).length === 0, 'fresh anonymous context has no Customer cookies');

  async function state(user, id, label) {
    const response = await user.page.goto(`${base}/Bookings/Details/${id}`);
    assert.equal(response.status(), 200, `${label} own details must be accessible`);
    const header = await user.page.locator('.page-heading').innerText();
    const details = await user.page.locator('.two-column > section').first().innerText();
    const forms = await user.page.locator('form[action="/Bookings/Cancel"], form[action="/Bookings/Reschedule"]').count();
    assert.match(header, /Confirmed/);
    assert.match(details, new RegExp(`FIXTURE-ownership-${label.toLowerCase()}`));
    assert.equal(forms, 2, `${label} confirmed booking supports cancellation and rescheduling`);
    return JSON.stringify({ header, details, forms });
  }
  const initialA = await state(a, aId, 'A'), initialB = await state(b, bId, 'B');
  async function capture(tag) {
    const deadline = Date.now() + 5000;
    while (Date.now() < deadline) {
      try {
        const artifact = JSON.parse(await readFile(path.join(root, 'ownership-state.json'), 'utf8'));
        if (artifact.tag === tag) return JSON.stringify(artifact.snapshot);
      } catch (error) { if (error.code !== 'ENOENT' && !(error instanceof SyntaxError)) throw error; }
      await new Promise(resolve => setTimeout(resolve, 10));
    }
    throw Error(`Timed out waiting for the exact request's state snapshot: ${tag}`);
  }
  const baselineTag = randomUUID();
  await a.ctx.request.get(`${base}/Bookings/Details/${aId}`, { headers: { 'X-Ownership-Test-Case': baselineTag } });
  const initialStoredState = await capture(baselineTag);
  check(true, 'Customer A sees own confirmed booking, payment, and action forms (HTTP 200)');
  check(true, 'Customer B sees own confirmed booking, payment, and action forms (HTTP 200)');
  for (const [user, id, label] of [[a, aId, 'A'], [b, bId, 'B']]) {
    const confirmation = await user.ctx.request.get(`${base}/Bookings/Confirmation/${id}`, { maxRedirects: 0 });
    check(confirmation.status() === 200 && (await confirmation.text()).includes('Confirmed'), `Confirmed ${label} own confirmation: HTTP 200`);
  }
  async function unchanged(label, tag = null) {
    if (tag === null) {
      tag = randomUUID();
      await a.ctx.request.get(`${base}/Bookings/Details/${aId}`, { headers: { 'X-Ownership-Test-Case': tag } });
    }
    const storedState = await capture(tag);
    check(storedState === initialStoredState, `${label}: full in-memory booking and payment snapshots unchanged`);
    const afterA = await state(a, aId, 'A'), afterB = await state(b, bId, 'B');
    check(afterA === initialA && afterB === initialB, `${label}: both owners see unchanged status, appointment, payment and action forms`);
  }
  async function denial(user, method, route, fields, label) {
    const token = await user.page.locator('input[name="__RequestVerificationToken"]').first().inputValue();
    const tag = randomUUID(), headers = { 'X-Ownership-Test-Case': tag };
    const response = method === 'GET'
      ? await user.ctx.request.get(base + route, { maxRedirects: 0, headers })
      : await user.ctx.request.post(base + route, { maxRedirects: 0, headers, form: { ...fields, __RequestVerificationToken: token } });
    check(response.status() === 404, `${label}: HTTP 404`);
    await unchanged(label, tag);
  }
  for (const [attacker, target, label] of [[a, bId, 'A → B'], [b, aId, 'B → A']]) {
    await denial(attacker, 'GET', `/Bookings/Details/${target}`, {}, `${label} details`);
    await denial(attacker, 'GET', `/Bookings/Confirmation/${target}`, {}, `${label} confirmation`);
    await denial(attacker, 'POST', '/Bookings/Cancel', { id: target, confirmed: 'true', reason: 'forged' }, `${label} cancel`);
    await denial(attacker, 'POST', '/Bookings/Reschedule', { id: target, start: '2030-01-01T08:00' }, `${label} reschedule`);
    await denial(attacker, 'POST', '/Bookings/ConfirmReschedule', { id: target, quoteId: randomUUID(), agreed: 'true' }, `${label} reschedule confirmation`);
  }
  for (const [slug, status] of [['pending', 'Pending Payment'], ['rejected', 'Rejected'], ['cancelled', 'Cancelled'], ['completed', 'Completed'], ['no-show', 'No-show'], ['incomplete', 'Incomplete']]) {
    const first = ids[`ownership-${slug}-a`], second = ids[`ownership-${slug}-b`];
    assert.ok(first && second && first !== second, `${status} fixture IDs`);
    for (const [user, ownId, label] of [[a, first, 'A'], [b, second, 'B']]) {
      const details = await user.ctx.request.get(`${base}/Bookings/Details/${ownId}`, { maxRedirects: 0 });
      const html = await details.text();
      check(details.status() === 200 && html.includes(status) && html.includes(`FIXTURE-ownership-${slug}-${label.toLowerCase()}`), `${status} ${label} own details: HTTP 200 and correct status/payment`);
      const confirmation = await user.ctx.request.get(`${base}/Bookings/Confirmation/${ownId}`, { maxRedirects: 0 });
      check(confirmation.status() === 200 && (await confirmation.text()).includes(status), `${status} ${label} own confirmation: HTTP 200`);
      for (const [operation, fields, message] of [
        ['Cancel', { confirmed: 'true', reason: 'synthetic' }, 'Confirm the cancellation of a future confirmed appointment.'],
        ['Reschedule', { start: '2030-01-01T08:00' }, 'Only future confirmed appointments can be rescheduled.']
      ]) {
        const token = await user.page.locator('input[name="__RequestVerificationToken"]').first().inputValue();
        const tag = randomUUID();
        const result = await user.ctx.request.post(`${base}/Bookings/${operation}`, { maxRedirects: 0,
          headers: { 'X-Ownership-Test-Case': tag }, form: { id: ownId, ...fields, __RequestVerificationToken: token } });
        check(result.status() === 302 && new URL(result.headers().location, base).pathname === `/Bookings/Details/${ownId}`, `${status} ${label} own ${operation}: 302 to own details`);
        const after = await user.ctx.request.get(`${base}/Bookings/Details/${ownId}`);
        check(after.status() === 200 && (await after.text()).includes(message), `${status} ${label} own ${operation}: exact state restriction shown`);
        await unchanged(`${status} ${label} rejected own ${operation}`, tag);
      }
    }
    for (const [attacker, target, label] of [[a, second, 'A → B'], [b, first, 'B → A']]) {
      await denial(attacker, 'GET', `/Bookings/Details/${target}`, {}, `${status} ${label} details`);
      await denial(attacker, 'GET', `/Bookings/Confirmation/${target}`, {}, `${status} ${label} confirmation`);
      await denial(attacker, 'POST', '/Bookings/Cancel', { id: target, confirmed: 'true', reason: 'forged' }, `${status} ${label} cancel`);
      await denial(attacker, 'POST', '/Bookings/Reschedule', { id: target, start: '2030-01-01T08:00' }, `${status} ${label} reschedule`);
      await denial(attacker, 'POST', '/Bookings/ConfirmReschedule', { id: target, quoteId: randomUUID(), agreed: 'true' }, `${status} ${label} reschedule confirmation`);
    }
  }
  await denial(a, 'GET', `/Bookings/Details/${randomUUID()}`, {}, 'A unknown route ID');
  await denial(b, 'POST', '/Bookings/Cancel', { id: randomUUID(), confirmed: 'true' }, 'B unknown submitted ID');

  // Role checks are separate from the cross-Customer ownership assertions.
  const admin = await login('presentation-admin@example.test', 'Admin123!');
  const owner = await login('presentation-owner@example.test', 'Owner123!');
  async function get(ctx, route) { return ctx.request.get(base + route, { maxRedirects: 0 }); }
  function redirectedTo(response, target) { return response.status() === 302 && new URL(response.headers().location, base).pathname === target; }
  check((await get(admin.ctx, '/Admin/Appointments')).status() === 200, 'Admin may access appointments (HTTP 200)');
  check((await get(owner.ctx, '/Admin/Appointments')).status() === 200, 'SuperAdmin may access appointments (HTTP 200)');
  check(redirectedTo(await get(a.ctx, '/Admin/Appointments'), '/Account/AccessDenied'), 'Customer denied Admin appointments (302 AccessDenied)');
  check(redirectedTo(await get(admin.ctx, '/Bookings/Details/' + aId), '/Account/AccessDenied'), 'Admin denied Customer booking endpoint (302 AccessDenied)');
  check(redirectedTo(await get(a.ctx, '/WebsiteContent'), '/Account/AccessDenied'), 'Customer denied Website Content (302 AccessDenied)');
  check(redirectedTo(await get(admin.ctx, '/WebsiteContent'), '/Account/AccessDenied'), 'Admin denied Website Content (302 AccessDenied)');
  check((await get(owner.ctx, '/WebsiteContent')).status() === 200, 'SuperAdmin may access Website Content (HTTP 200)');
  check(redirectedTo(await get(empty, '/Bookings/Details/' + aId), '/Account/Login'), 'anonymous booking request requires login (302 Login)');
  check(redirectedTo(await get(empty, '/WebsiteContent'), '/Account/Login'), 'anonymous Website Content request requires login (302 Login)');
  await unchanged('role-boundary requests');
  // Positive controls run only after all denial snapshots, using the same real
  // forms and valid antiforgery tokens; they mutate only these synthetic fixtures.
  const date = new Date(); date.setDate(date.getDate() + 140);
  const requested = `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}T08:00`;
  const aToken = await a.page.locator('form[action="/Bookings/Reschedule"] input[name="__RequestVerificationToken"]').inputValue();
  const ownOffer = await a.ctx.request.post(base + '/Bookings/Reschedule', { maxRedirects: 0,
    form: { id: aId, start: requested, __RequestVerificationToken: aToken } });
  check(ownOffer.status() === 200 && /Current approved total/.test(await ownOffer.text()), 'own reschedule reaches the review page (HTTP 200)');
  check(await state(b, bId, 'B') === initialB, 'own reschedule offer leaves the other Customer booking unchanged');
  const afterOfferTag = randomUUID();
  await a.ctx.request.get(`${base}/Bookings/Details/${aId}`, { headers: { 'X-Ownership-Test-Case': afterOfferTag } });
  const afterOffer = JSON.parse(await capture(afterOfferTag));
  const bToken = await b.page.locator('form[action="/Bookings/Cancel"] input[name="__RequestVerificationToken"]').inputValue();
  const ownCancel = await b.ctx.request.post(base + '/Bookings/Cancel', { maxRedirects: 0,
    form: { id: bId, confirmed: 'true', __RequestVerificationToken: bToken } });
  check(ownCancel.status() === 302 && new URL(ownCancel.headers().location, base).pathname === `/Bookings/Details/${bId}`, 'own cancellation redirects to own details (HTTP 302)');
  await b.page.goto(`${base}/Bookings/Details/${bId}`);
  check((await b.page.locator('.page-heading').innerText()).includes('Cancelled'), 'own cancellation changes synthetic target status');
  const afterCancelTag = randomUUID();
  await a.ctx.request.get(`${base}/Bookings/Details/${aId}`, { headers: { 'X-Ownership-Test-Case': afterCancelTag } });
  check(JSON.stringify(JSON.parse(await capture(afterCancelTag))[0]) === JSON.stringify(afterOffer[0]), 'own cancellation preserves the other Customer complete booking and payment');
  console.log(`ALL ${checks} CUSTOMER OWNERSHIP BROWSER CHECKS PASSED; isolated root: ${root}`);
} finally {
  if (browser) await browser.close();
  server.kill();
}
