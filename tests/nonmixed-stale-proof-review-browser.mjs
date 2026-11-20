// Fresh loopback-only Demo. REVIEW_BROWSER_FIXTURES writes private snapshots;
// there is no fixture HTTP endpoint or production authentication bypass.
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { spawn } from 'node:child_process';
import { mkdir, mkdtemp, readFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import path from 'node:path';

const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright-core');
const project = path.resolve('.'), bin = process.env.FIXTURE_BUILD || path.join(process.env.LOCALAPPDATA, 'Temp/opencode/ownership-fixture-build');
const parent = path.join(process.env.LOCALAPPDATA, 'Temp/opencode'); await mkdir(parent, { recursive: true });
const root = await mkdtemp(path.join(parent, 'nonmixed-stale-proof-'));
const server = spawn('dotnet', [path.join(bin, 'GlamByMariya.Web.dll'), '--urls=http://127.0.0.1:0', `--contentRoot=${root}`,
  `--webroot=${path.join(project, 'Frontend/GlamByMariya.Web/wwwroot')}`, '--Database:Provider=Demo', '--DemoAccounts:Presentation=false',
  '--Authentication:DurableSessionEnforcement=false', '--DemoGallery:Enabled=false', '--DemoCatalog:Enabled=false', '--DemoAvatar:Enabled=false',
  '--WebsiteContent:DemoTextEditing=false', '--WebsiteContent:DemoPersistentText=false', '--Supabase:Url=', '--Supabase:AnonKey=',
  '--Supabase:ServiceKey=', '--SupabaseStorage:ServiceKey=', '--FileStorage:Provider=Local', '--B2:Bucket=', '--B2:Endpoint=',
  '--B2:KeyId=', '--B2:AppKey=', '--Email:BrevoApiKey=', '--Email:SmtpHost='],
  { env: { ...process.env, PORT: '', ASPNETCORE_ENVIRONMENT: 'Development', DOTNET_ENVIRONMENT: 'Development',
    DOTNET_STARTUP_HOOKS: path.join(bin, 'Phase3BFixtures.dll'), PHASE3B_FIXTURE_ROOT: root,
    OWNERSHIP_BROWSER_FIXTURES: 'true', REVIEW_BROWSER_FIXTURES: 'true' }, stdio: ['ignore', 'pipe', 'pipe'] });
let browser, checks = 0;
const check = (value, name) => { assert.ok(value, name); console.log(`PASS ${++checks}: ${name}`); };
const message = 'The payment claim changed. Reload and inspect the latest proof before verifying.';

try {
  const base = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(Error('isolated Demo startup timeout')), 30000);
    server.once('error', reject); server.once('exit', code => reject(Error(`isolated Demo exited ${code}`)));
    server.stderr.resume(); server.stdout.on('data', chunk => { const m = String(chunk).match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/); if (m) { clearTimeout(timer); resolve(m[1]); } });
  });
  const ids = JSON.parse(await readFile(path.join(root, 'fixtures.json'), 'utf8'));
  const individual = ids['ownership-pending-a'], mixed = ids['mixed-unverified'];
  browser = await chromium.launch({ executablePath: process.env.BROWSER_EXECUTABLE || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', headless: true });
  async function context() { const ctx = await browser.newContext(); await ctx.route('**/*', r => new URL(r.request().url()).origin === base ? r.continue() : r.abort()); return ctx; }
  async function login(email, password) {
    const ctx = await context(), page = await ctx.newPage(); await page.goto(base + '/Account/Login');
    await page.fill('[name=email]', email); await page.fill('[name=password]', password);
    await Promise.all([page.waitForURL(u => !u.pathname.includes('/Account/Login')), page.locator('#auth-form button.button').click()]);
    return { ctx, page };
  }
  const customer = await login('customer@glambymariya.demo', 'Customer123!');
  const admin = await login('admin@glambymariya.demo', 'Admin123!');
  const owner = await login('owner@glambymariya.demo', 'Owner123!');
  const anonymous = await context();
  check(customer.ctx !== admin.ctx && admin.ctx !== owner.ctx, 'Customer, Admin and SuperAdmin use separate browser contexts');
  async function capture(tag) {
    const deadline = Date.now() + 5000;
    while (Date.now() < deadline) {
      try { const s = JSON.parse(await readFile(path.join(root, 'review-state.json'), 'utf8')); if (s.tag === tag) { delete s.tag; return s; } }
      catch (e) { if (e.code !== 'ENOENT' && !(e instanceof SyntaxError)) throw e; }
      await new Promise(resolve => setTimeout(resolve, 10));
    }
    throw Error('Timed out waiting for the matching snapshot');
  }
  async function snapshot() {
    const tag = randomUUID(); const r = await customer.ctx.request.get(`${base}/Bookings/Details/${individual}`, { headers: { 'X-Ownership-Test-Case': tag } });
    assert.equal(r.status(), 200); return capture(tag);
  }
  const initial = await snapshot();
  const getBooking = (s, id) => s.bookings.find(x => x.Id === id);
  const getPayment = (s, id) => s.payments.find(x => x.BookingId === id);
  const originalPayment = getPayment(initial, individual);
  assert.equal(getBooking(initial, individual).Status, 'Pending Payment'); assert.equal(originalPayment.Verified, false);
  const adminPage = await admin.page.goto(`${base}/Admin/Details/${individual}`); assert.equal(adminPage.status(), 200);
  const verifyForm = admin.page.locator('form').filter({ has: admin.page.locator('button[value="verify"]') });
  const stale = await verifyForm.evaluate(form => Object.fromEntries(new FormData(form))); stale.operation = 'verify';
  check(stale.paymentId === originalPayment.Id && Number(stale.paymentRevision) === originalPayment.Revision, 'non-mixed form carries exact reviewed payment and revision');
  const proofUrl = await admin.page.locator('.payment-box a[href*="/Admin/Proof/"]').getAttribute('href');
  const oldProof = await admin.ctx.request.get(base + proofUrl); assert.equal(oldProof.status(), 200); const oldBytes = await oldProof.body();
  async function post(ctx, fields) {
    const tag = randomUUID(); const r = await ctx.request.post(base + '/Admin/Action', { maxRedirects: 0,
      headers: { 'X-Ownership-Test-Case': tag }, form: fields });
    return { r, state: await capture(tag) };
  }
  async function rejected(fields, label) {
    const { r, state } = await post(admin.ctx, fields);
    check(r.status() === 302 && new URL(r.headers().location, base).pathname === `/Admin/Details/${individual}`, `${label}: redirects to details`);
    check(JSON.stringify(state) === JSON.stringify(initial), `${label}: complete booking, payment, history, notification and audit state unchanged`);
    const page = await admin.ctx.request.get(`${base}/Admin/Details/${individual}`);
    check(page.status() === 200 && (await page.text()).includes(message), `${label}: exact reload-and-inspect error`);
  }
  const missingRevision = { ...stale }; delete missingRevision.paymentRevision;
  await rejected(missingRevision, 'missing revision');
  await rejected({ ...stale, paymentRevision: 'not-a-number' }, 'malformed revision');
  await rejected({ ...stale, paymentRevision: String(originalPayment.Revision + 1) }, 'wrong revision');
  const missingPayment = { ...stale }; delete missingPayment.paymentId;
  await rejected(missingPayment, 'missing payment ID');
  await rejected({ ...stale, paymentId: randomUUID() }, 'wrong payment ID');

  const csrf = await post(admin.ctx, { ...stale, __RequestVerificationToken: 'invalid' });
  check(csrf.r.status() === 400 && JSON.stringify(csrf.state) === JSON.stringify(initial), 'invalid antiforgery token returns 400 without mutation');
  const customerPage = await customer.page.goto(`${base}/Bookings/Details/${individual}`);
  assert.equal(customerPage.status(), 200);
  const customerToken = await customer.page.locator('form[action="/Bookings/CorrectProof"] [name="__RequestVerificationToken"]').inputValue();
  const forbidden = await post(customer.ctx, { ...stale, __RequestVerificationToken: customerToken });
  check(forbidden.r.status() === 302 && new URL(forbidden.r.headers().location, base).pathname === '/Account/AccessDenied' && JSON.stringify(forbidden.state) === JSON.stringify(initial), 'Customer verification attempt is denied without mutation');
  const notSignedIn = await post(anonymous, stale);
  check(notSignedIn.r.status() === 302 && new URL(notSignedIn.r.headers().location, base).pathname === '/Account/Login' && JSON.stringify(notSignedIn.state) === JSON.stringify(initial), 'anonymous verification requires login without mutation');

  const replacement = await readFile(path.join(root, 'App_Data', 'Proofs', 'ownership-a.png'));
  async function correct(id, reference) {
    await customer.page.goto(`${base}/Bookings/Details/${id}`);
    const form = customer.page.locator('form[action="/Bookings/CorrectProof"]');
    await form.locator('[name=reference]').fill(reference); await form.locator('[name=proof]').setInputFiles({ name: 'synthetic.png', mimeType: 'image/png', buffer: replacement });
    const tag = randomUUID(); await customer.page.setExtraHTTPHeaders({ 'X-Ownership-Test-Case': tag });
    let r; try { [r] = await Promise.all([customer.page.waitForResponse(x => x.request().method() === 'POST' && new URL(x.url()).pathname === '/Bookings/CorrectProof'), form.locator('button').click()]); await customer.page.waitForLoadState('load'); }
    finally { await customer.page.setExtraHTTPHeaders({}); }
    check(r.status() === 302 && (await customer.page.locator('body').innerText()).includes('Payment proof updated.'), 'owner correction succeeds through actual browser form');
    return capture(tag);
  }
  const corrected = await correct(individual, 'SYNTHETIC-REVIEW-REPLACEMENT');
  const replacementPayment = getPayment(corrected, individual);
  check(replacementPayment.Revision === originalPayment.Revision + 1 && !replacementPayment.Verified && replacementPayment.Reference === 'SYNTHETIC-REVIEW-REPLACEMENT', 'correction advances revision and leaves replacement unverified');
  const newProof = await admin.ctx.request.get(base + proofUrl);
  check(newProof.status() === 200 && !(await newProof.body()).equals(oldBytes), 'Admin can reload different replacement proof');
  const staleAttempt = await post(admin.ctx, stale);
  check(staleAttempt.r.status() === 302 && new URL(staleAttempt.r.headers().location, base).pathname === `/Admin/Details/${individual}`, 'pre-correction form is rejected with redirect');
  check(JSON.stringify(staleAttempt.state) === JSON.stringify(corrected), 'stale form preserves all bookings, payments, histories, notifications and audit');
  const staleDetails = await admin.ctx.request.get(`${base}/Admin/Details/${individual}`);
  check((await staleDetails.text()).includes(message), 'stale form shows exact reload-and-inspect error');

  await admin.page.goto(`${base}/Admin/Details/${individual}`);
  const currentForm = admin.page.locator('form').filter({ has: admin.page.locator('button[value="verify"]') });
  const current = await currentForm.evaluate(form => Object.fromEntries(new FormData(form))); current.operation = 'verify';
  check(current.paymentId === replacementPayment.Id && Number(current.paymentRevision) === replacementPayment.Revision, 'reloaded form carries current payment and revision');
  const accepted = await post(admin.ctx, current);
  const acceptedBooking = getBooking(accepted.state, individual), acceptedPayment = getPayment(accepted.state, individual);
  check(accepted.r.status() === 302 && acceptedBooking.Status === 'Confirmed' && acceptedPayment.Verified && acceptedPayment.Revision === replacementPayment.Revision && acceptedPayment.Reference === replacementPayment.Reference, 'current revision verifies and confirms the correct replacement');
  check(JSON.stringify(accepted.state.bookings.filter(x => x.Id !== individual)) === JSON.stringify(corrected.bookings.filter(x => x.Id !== individual)) && JSON.stringify(accepted.state.payments.filter(x => x.BookingId !== individual)) === JSON.stringify(corrected.payments.filter(x => x.BookingId !== individual)), 'current verification preserves unrelated bookings and payments');

  await owner.page.goto(`${base}/Admin/Details/${mixed}`);
  const mixedForm = owner.page.locator('form[data-decision="verify"]'); await mixedForm.locator('[name=reviewed]').check();
  const mixedStale = await mixedForm.evaluate(form => Object.fromEntries(new FormData(form)));
  mixedStale.operation = 'verify';
  const mixedBefore = await snapshot(), mixedOriginal = getPayment(mixedBefore, mixed);
  check(mixedStale.paymentId === mixedOriginal.Id && Number(mixedStale.paymentRevision) === mixedOriginal.Revision, 'mixed form retains existing reviewed revision');
  const mixedCorrected = await correct(mixed, 'SYNTHETIC-MIXED-REPLACEMENT');
  check(getPayment(mixedCorrected, mixed).Revision === mixedOriginal.Revision + 1, 'mixed correction advances revision');
  const mixedAttempt = await post(owner.ctx, mixedStale);
  check(mixedAttempt.r.status() === 302 && JSON.stringify(mixedAttempt.state) === JSON.stringify(mixedCorrected), 'mixed stale form remains rejected with complete state preserved');
  const mixedDetails = await owner.ctx.request.get(`${base}/Admin/Details/${mixed}`);
  check((await mixedDetails.text()).includes(message) && getBooking(mixedAttempt.state, mixed).Status === 'Pending Payment' && !getPayment(mixedAttempt.state, mixed).Verified, 'mixed stale form reports reload error and remains unverified');
  console.log(`ALL ${checks} NON-MIXED STALE PROOF REVIEW BROWSER CHECKS PASSED; isolated root: ${root}`);
} finally { if (browser) await browser.close(); server.kill(); }
