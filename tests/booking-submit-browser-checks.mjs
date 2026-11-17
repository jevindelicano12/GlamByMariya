// Real Edge/Chromium regression. No project dependency is installed.
// Build Release first, then set PLAYWRIGHT_MODULE to a temporary playwright-core
// installation (package directory), and optionally BROWSER_EXECUTABLE.
// This script starts its OWN loopback-only Demo server with cloud integrations
// disabled and a temporary content root. It never accepts an external base URL.
import assert from 'node:assert/strict';
import { admin as adminAccount, customer, owner } from './presentation-test-accounts.mjs';
import { createRequire } from 'node:module';
import { mkdtemp, mkdir, rm } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import path from 'node:path';
import os from 'node:os';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright-core');
const root = fileURLToPath(new URL('../', import.meta.url));
const tempParent = process.env.LOCALAPPDATA ? path.join(process.env.LOCALAPPDATA, 'Temp', 'opencode') : os.tmpdir();
await mkdir(tempParent, { recursive: true });
const contentRoot = await mkdtemp(path.join(tempParent, 'booking-submit-78f-'));
const env = { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', DOTNET_ENVIRONMENT: 'Development' };
// CLI overrides also take precedence over development User Secrets.
const server = spawn('dotnet', [path.join(process.env.UI_CHECK_BUILD || path.join(root, 'Frontend/GlamByMariya.Web/bin/Release/net10.0'), 'GlamByMariya.Web.dll'),
  '--urls=http://127.0.0.1:0', `--contentRoot=${contentRoot}`, `--webroot=${path.join(root, 'Frontend/GlamByMariya.Web/wwwroot')}`,
  '--Database:Provider=Demo', '--Supabase:Url=', '--Supabase:AnonKey=',
  '--SupabaseStorage:ServiceKey=', '--B2:Bucket=', '--B2:Endpoint=', '--B2:KeyId=', '--B2:AppKey=',
  '--Email:BrevoApiKey=', '--Email:SmtpHost='
], { cwd: contentRoot, env, stdio: ['ignore', 'pipe', 'pipe'] });
let browser;
let passed = 0;
const check = (condition, message) => { assert.ok(condition, message); passed++; console.log(`PASS: ${message}`); };
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
try {
  const base = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Isolated Demo startup timed out.')), 30000);
    server.on('error', error => { clearTimeout(timer); reject(error); });
    server.on('exit', () => { clearTimeout(timer); reject(new Error('Isolated Demo exited during startup.')); });
    server.stdout.on('data', chunk => {
      const match = chunk.toString().match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/);
      if (match) { clearTimeout(timer); resolve(match[1]); }
    });
    // Consume stderr without printing configuration, provider errors or secrets.
    server.stderr.resume();
  });
  browser = await chromium.launch({ headless: true,
    ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : { channel: 'msedge' }) });
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
  await context.route('**/*', route => {
    const url = new URL(route.request().url());
    return url.origin === base
      ? route.continue() : route.abort();
  });
  const login = await context.newPage();
  await login.goto(base + '/Account/Login');
  await login.fill('[name="email"]', customer.email);
  await login.fill('[name="password"]', customer.password);
  await Promise.all([login.waitForURL(base + '/Bookings'), login.locator('#auth-form button.button').click()]);
  const beforeLinks = await login.locator('a[href^="/Bookings/Details/"]').evaluateAll(links => links.map(a => a.getAttribute('href')));
  await login.close();
  const errors = [];
  let posts = [];
  const prepare = async label => {
    const page = await context.newPage();
    page.on('pageerror', error => errors.push(error.message));
    page.on('request', request => {
      if (request.method() === 'POST' && new URL(request.url()).pathname === '/Bookings/Create') posts.push(request);
    });
    await page.goto(base + '/Bookings/Create');
    await page.waitForFunction(() => document.querySelector('#booking-form').dataset.priceState === 'ready');
    await page.locator('[data-step-next]').click();
    await page.locator('textarea[name="address"]').fill('78F Isolated Demo Test Street, Quezon City');
    await page.locator('[data-calendar-days] button:not([disabled])').nth(1).click();
    await page.locator('[data-time-slots] button:not([disabled])').first().click();
    await page.waitForFunction(() => document.querySelector('[data-validation-status]').dataset.state === 'valid');
    await page.locator('[data-step-next]').click();
    await page.locator('#travel-fee-modal:not([hidden])').waitFor();
    await page.locator('#travel-policy-ack').check();
    await page.locator('[data-travel-ack]').click();
    await page.fill('[name="reference"]', `LOCAL-78F-${label}-${Date.now()}`);
    await page.locator('[name="proof"]').setInputFiles({ name: 'synthetic-proof.png', mimeType: 'image/png', buffer: await page.screenshot() });
    await page.locator('#payment-proof-preview').waitFor({ state: 'visible' });
    await page.locator('[name="agreed"]').check();
    await page.waitForFunction(() => !document.querySelector('#booking-form button[type="submit"]').disabled);
    return page;
  };
  const submit = async page => { await page.locator('#booking-form button[type="submit"]').click(); if (await page.locator('#booking-confirmation').isVisible()) { await page.locator('#confirmation-ack').check(); await page.locator('#confirmation-submit').click(); } };
  const idle = page => page.waitForFunction(() => document.querySelector('#booking-form').dataset.submissionState === 'idle');
  const message = page => page.locator('.booking-step-message').innerText();
  const failure = async (label, response, expected) => {
    const page = await prepare(label);
    await page.route('**/Bookings/ValidateSlot?*', route => route.fulfill(response));
    await submit(page); await idle(page);
    check((await message(page)).match(expected), `${label}: clear payment-step error`);
    check(await page.locator('#booking-form button[type="submit"]').isEnabled(), `${label}: retry enabled`);
    check(posts.length === 0, `${label}: no booking POST`);
    await page.unroute('**/Bookings/ValidateSlot?*');
    let retried = false;
    await page.route('**/Bookings/ValidateSlot?*', route => {
      retried = true;
      return route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ error: 'Retry reached validation safely.' }) });
    });
    await submit(page); await idle(page);
    check(retried && /Retry reached/.test(await message(page)), `${label}: normal button actually retries validation`);
    await page.close();
  };
  await failure('http-error', { status: 503, contentType: 'application/json', body: JSON.stringify({ error: 'Test availability unavailable. Please retry.' }) }, /unavailable/i);
  await failure('malformed-response', { status: 200, contentType: 'application/json', body: '{}' }, /invalid time validation/i);
  await failure('expired-session', { status: 200, contentType: 'text/html', body: '<html>Sign in</html>' }, /session may have expired/i);

  {
    const page = await prepare('motion');
    await page.emulateMedia({ reducedMotion: 'no-preference' });
    await page.evaluate(() => { document.querySelector('#booking-form').dataset.submissionState = 'submitting'; });
    const normal = await page.locator('#booking-form button[type="submit"]').evaluate(button => ({
      content: getComputedStyle(button, '::after').content,
      animation: getComputedStyle(button, '::after').animationName
    }));
    check(normal.content !== 'none' && normal.animation === 'booking-processing', 'processing indicator is shown during submission');
    await page.emulateMedia({ reducedMotion: 'reduce' });
    check(await page.locator('#booking-form button[type="submit"]').evaluate(button =>
      getComputedStyle(button, '::after').animationName === 'none'), 'reduced motion stops the processing animation');
    await page.evaluate(() => { document.querySelector('#booking-form').dataset.submissionState = 'idle'; });
    await page.keyboard.press('Tab');
    check(await page.evaluate(() => {
      const target = document.activeElement;
      return target && target.matches(':focus-visible') && getComputedStyle(target).outlineStyle !== 'none';
    }), 'keyboard navigation exposes a visible focus indicator');
    await page.close();
  }
  {
    const page = await prepare('timeout');
    await page.route('**/Bookings/ValidateSlot?*', async route => {
      await pause(11000); await route.abort().catch(() => {});
    });
    await submit(page);
    await page.waitForFunction(() => document.querySelector('#booking-form').dataset.submissionState === 'idle', null, { timeout: 15000 });
    check(/timed out/i.test(await message(page)), 'timeout: clear error after real ten-second deadline');
    check(await page.locator('#booking-form button[type="submit"]').isEnabled(), 'timeout: retry enabled');
    await page.unroute('**/Bookings/ValidateSlot?*');
    await page.route('**/Bookings/ValidateSlot?*', route => route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ error: 'Retry after timeout reached validation.' }) }));
    await submit(page); await idle(page);
    check(/Retry after timeout/.test(await message(page)), 'timeout: normal button retries after deadline');
    await page.close();
  }
  {
    const page = await prepare('unavailable');
    await page.route('**/Bookings/ValidateSlot?*', route => route.fulfill({ contentType: 'application/json', body: JSON.stringify({ available: false, message: 'This time is no longer available. Choose another slot.' }) }));
    await submit(page); await idle(page);
    check(/no longer available/i.test(await message(page)), 'unavailable slot: visible error');
    check(await page.locator('[name="start"]').inputValue() === '', 'unavailable slot: stale time cleared');
    check(await page.locator('#booking-form button[type="submit"]').isDisabled(), 'unavailable slot: requires a new time');
    await page.close();
  }
  {
    const page = await prepare('cancel');
    let release;
    const held = new Promise(resolve => { release = resolve; });
    await page.route('**/Bookings/ValidateSlot?*', async route => {
      await held; await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ available: true, message: 'Available' }) }).catch(() => {});
    });
    await submit(page);
    await page.waitForFunction(() => document.querySelector('#booking-form').dataset.submissionState === 'validating');
    await page.locator('[data-step-back]').click(); await idle(page);
    release(); await pause(300);
    check(await page.locator('#booking-form').getAttribute('data-current-step') === 'schedule', 'back cancels pending submission without stale navigation');
    check(await page.locator('#booking-form button[type="submit"]').isEnabled(), 'cancelled validation restores form state');
    await page.close();
  }
  {
    const page = await prepare('price-change');
    await page.route('**/Bookings/ValidateSlot?*', async route => {
      await page.locator('[name="deposit"]').evaluate(input => { input.value = '0.01'; });
      await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ available: true, message: 'Available' }) });
    });
    await submit(page); await idle(page);
    check(/price changed/i.test(await message(page)), 'price changed during validation: blocked with a visible error');
    await page.close();
  }
  {
    const page = await prepare('invalid');
    await page.fill('[name="reference"]', ''); await submit(page);
    check(/payment reference/i.test(await message(page)), 'native invalid field: visible error');
    check(await page.locator('[name="reference"]').evaluate(input => !input.validity.valid), 'native required-field validation retained');
    await page.fill('[name="reference"]', 'LOCAL-78F-invalid');
    await page.locator('[name="travelAcknowledged"]').evaluate(input => { input.value = 'false'; });
    await submit(page);
    check(await page.locator('#travel-fee-modal').isVisible(), 'missing transportation acknowledgement: modal guard retained');
    await page.close();
  }
  {
    const page = await prepare('prevented-retry');
    await page.evaluate(() => document.querySelector('#booking-form').addEventListener('submit', event => {
      if (event.target.dataset.submissionState === 'submitting') event.preventDefault();
    }));
    await submit(page); await idle(page);
    check(/cancelled/i.test(await message(page)), 'prevented native retry: visible error');
    check(await page.locator('#booking-form button[type="submit"]').isEnabled(), 'prevented native retry: lock released');
    await page.close();
  }
  check(posts.length === 0, 'all invalid, failed, cancelled and timed-out paths sent zero booking POSTs');

  {
    const page = await prepare('real-submit');
    let validations = 0;
    await page.route('**/Bookings/ValidateSlot?*', async route => {
      validations++; const response = await route.fetch(); await pause(500); await route.fulfill({ response });
    });
    await page.evaluate(() => {
      const form = document.querySelector('#booking-form');
      let repeated = false;
      form.addEventListener('submit', event => {
        if (!repeated && !event.defaultPrevented && form.dataset.submissionState === 'submitting') {
          repeated = true;
          for (let i = 0; i < 5; i++) form.requestSubmit(form.querySelector('button[type="submit"]'));
        }
      });
    });
    const response = page.waitForResponse(r => r.request().method() === 'POST' && new URL(r.url()).pathname === '/Bookings/Create');
    await submit(page);
    await page.waitForFunction(() => document.querySelector('#booking-form')?.dataset.submissionState === 'validating');
    await page.evaluate(() => {
      const form = document.querySelector('#booking-form');
      for (let i = 0; i < 8; i++) {
        form.querySelector('button[type="submit"]').click();
        form.requestSubmit(form.querySelector('button[type="submit"]'));
      }
    });
    const result = await response;
    await page.waitForURL(base + '/Bookings');
    check(result.status() === 302, 'normal JavaScript Submit button: server accepts booking POST');
    check(Boolean(result.headers()['content-security-policy-report-only']) && !result.headers()['content-security-policy'], 'booking response retains Report-Only CSP without enforcement');
    check(validations === 1, 'rapid submissions share one forced server availability validation');
    check(posts.length === 1, 'rapid clicks and requestSubmit calls produce exactly one booking POST');
    const payload = posts[0].postDataBuffer().toString();
    check(/name="__RequestVerificationToken"/.test(payload), 'real POST retains antiforgery token');
    check(/name="travelAcknowledged"\r\n\r\ntrue/.test(payload), 'real POST retains transportation acknowledgement');
    const links = await page.locator('a[href^="/Bookings/Details/"]').evaluateAll(items => items.map(a => a.getAttribute('href')));
    const added = links.filter(link => !beforeLinks.includes(link));
    check(added.length === 1, 'Demo booking history contains exactly one new booking');
    await page.goto(base + added[0]);
    check((await page.locator('main').innerText()).includes('Pending Payment'), 'created Demo booking follows existing Pending Payment policy');
    const csrf = await context.request.post(base + '/Bookings/Create', { form: { reference: 'LOCAL-78F-NO-TOKEN' } });
    check(csrf.status() === 400, 'booking POST without antiforgery token is rejected');

    // The same availability handler serves rescheduling; verify that path too,
    // using only the newly created, isolated in-memory booking.
    const adminContext = await browser.newContext();
    const admin = await adminContext.newPage();
    await admin.goto(base + '/Account/Login');
    await admin.fill('[name="email"]', adminAccount.email);
    await admin.fill('[name="password"]', adminAccount.password);
    await Promise.all([admin.waitForURL(base + '/Admin'), admin.locator('#auth-form button.button').click()]);
    await admin.goto(base + added[0].replace('/Bookings/', '/Admin/'));
    const menu = admin.locator('.account-menu');
    await menu.locator('summary').click();
    check(await menu.evaluate(element => element.open), 'Admin account menu opens');
    await admin.keyboard.press('Escape');
    await admin.waitForFunction(() => !document.querySelector('.account-menu').open);
    check(await menu.locator('summary').evaluate(element => document.activeElement === element), 'Escape closes the menu and returns focus');
    await menu.locator('summary').click();
    await menu.locator('summary').click();
    await admin.waitForFunction(() => !document.querySelector('.account-menu').open);
    check(true, 'account menu closes after its transition');
    await menu.locator('summary').click();
    await admin.locator('h1').click();
    await admin.waitForFunction(() => !document.querySelector('.account-menu').open);
    check(true, 'clicking outside closes the account menu');
    await admin.emulateMedia({ reducedMotion: 'reduce' });
    await menu.locator('summary').click();
    await admin.keyboard.press('Escape');
    check(!(await menu.evaluate(element => element.open)), 'reduced-motion menu closes immediately');
    await admin.emulateMedia({ reducedMotion: 'no-preference' });
    const verify = admin.locator('form[action="/Admin/Action"] button[value="verify"]');
    check(await verify.locator('xpath=ancestor::form').locator('.staff-action-status').count() === 1,
      'Admin action includes the accessible processing status');
    await verify.evaluate(button => button.form.addEventListener('submit', event => event.preventDefault(), { once: true }));
    await verify.click();
    await pause(50);
    await admin.waitForFunction(() => !document.querySelector('form[action="/Admin/Action"]')?.hasAttribute('aria-busy'));
    check(await verify.isEnabled(), 'cancelled Admin submission restores the action');
    let adminPosts = 0, adminPayload = '', releaseAdmin;
    const adminGate = new Promise(resolve => { releaseAdmin = resolve; });
    await verify.evaluate(button => {
      const frame = document.createElement('iframe');
      frame.name = 'synthetic-admin-response';
      frame.hidden = true;
      document.body.append(frame);
      button.form.target = frame.name;
    });
    await admin.route('**/Admin/Action', async route => {
      adminPosts++; adminPayload = route.request().postData() || '';
      await adminGate; await route.continue();
    });
    const adminResponse = admin.waitForResponse(r => r.request().method() === 'POST' && new URL(r.url()).pathname === '/Admin/Action');
    await verify.click({ noWaitAfter: true });
    await admin.getByText('Sending request…', { exact: true }).waitFor();
    await verify.evaluate(button => button.form.requestSubmit(button));
    check(await verify.isDisabled() && await verify.locator('xpath=ancestor::form').getAttribute('aria-busy') === 'true',
      'Admin action announces a pending request and disables its controls');
    check(!await admin.locator('.alert.success').isVisible(), 'Admin does not show success while a request is pending');
    releaseAdmin();
    await adminResponse;
    await admin.reload();
    check(adminPosts === 1 && /operation=verify/.test(adminPayload) && /__RequestVerificationToken=/.test(adminPayload),
      'repeated Admin submission sends one request with action and antiforgery');
    const ownerContext = await browser.newContext();
    const ownerPage = await ownerContext.newPage();
    await ownerPage.goto(base + '/Account/Login');
    await ownerPage.fill('[name="email"]', owner.email);
    await ownerPage.fill('[name="password"]', owner.password);
    await Promise.all([ownerPage.waitForURL(base + '/Admin'), ownerPage.locator('#auth-form button.button').click()]);
    await ownerPage.locator('.account-menu summary').click();
    check(await ownerPage.getByText('System owner', { exact: true }).isVisible(), 'SuperAdmin retains the shared account menu');
    await ownerContext.close();
    const touchContext = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
    await touchContext.route('**/*', route => new URL(route.request().url()).origin === base ? route.continue() : route.abort());
    const touch = await touchContext.newPage();
    await touch.goto(base + '/Account/Login');
    await touch.fill('[name="email"]', customer.email);
    await touch.fill('[name="password"]', customer.password);
    await Promise.all([touch.waitForURL(base + '/Bookings'), touch.locator('#auth-form button.button').tap()]);
    await touch.locator('.account-menu summary').tap();
    check(await touch.locator('.account-menu').evaluate(menu => menu.open), 'emulated mobile touch opens the Customer account menu');
    await touch.locator('.account-menu summary').tap();
    await touch.waitForFunction(() => !document.querySelector('.account-menu').open);
    check(true, 'emulated mobile touch closes the Customer account menu');
    await touchContext.close();
    await page.reload();
    const reschedule = page.locator('form[action="/Bookings/Reschedule"]');
    await reschedule.locator('[data-calendar-days] button:not([disabled])').nth(2).click();
    await reschedule.locator('[data-time-slots] button:not([disabled])').first().click();
    await page.waitForFunction(() => document.querySelector('form[action="/Bookings/Reschedule"] [data-validation-status]').dataset.state === 'valid');
    let reschedulePosts = 0;
    page.on('request', r => { if (r.method() === 'POST' && new URL(r.url()).pathname === '/Bookings/Reschedule') reschedulePosts++; });
    const rescheduled = page.waitForResponse(r => r.request().method() === 'POST' && new URL(r.url()).pathname === '/Bookings/Reschedule');
    await reschedule.locator('button[type="submit"]').click();
    check((await rescheduled).status() === 200 && reschedulePosts === 1, 'shared lifecycle: reschedule button shows server quote');
    await page.locator('form[action="/Bookings/ConfirmReschedule"] [name=agreed]').check();
    const consent = page.waitForResponse(r => r.request().method() === 'POST' && new URL(r.url()).pathname === '/Bookings/ConfirmReschedule');
    await page.locator('form[action="/Bookings/ConfirmReschedule"] button').click();
    check((await consent).status() === 302, 'shared lifecycle: explicit Customer consent submitted');
    await page.getByText('Your request and consent were recorded. The original appointment remains reserved until SuperAdmin approval.', { exact: true }).waitFor();
    check(true, 'shared lifecycle: reschedule request preserves original appointment');
    await adminContext.close();
  }
  check(errors.length === 0, 'no uncaught browser JavaScript errors');
  console.log(`Booking submission browser checks: ${passed} passed.`);
} finally {
  await browser?.close();
  if (server.exitCode === null) { const exited = once(server, 'exit'); server.kill(); await exited; }
  // Only the unique temporary root created by this test is removed.
  await rm(contentRoot, { recursive: true, force: true });
}
