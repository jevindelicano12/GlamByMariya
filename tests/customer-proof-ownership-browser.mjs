// Fresh loopback Demo; only synthetic images and two independent Customer contexts.
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
const root = await mkdtemp(path.join(parent, 'customer-proof-ownership-'));
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
const check = (condition, label) => { assert.ok(condition, label); console.log(`PASS ${++checks}: ${label}`); };

try {
  const base = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(Error('isolated Demo startup timeout')), 30000);
    server.once('error', error => { clearTimeout(timer); reject(error); });
    server.once('exit', code => { clearTimeout(timer); reject(Error(`isolated Demo exited ${code}`)); });
    server.stderr.resume();
    server.stdout.on('data', chunk => { const m = String(chunk).match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/); if (m) { clearTimeout(timer); resolve(m[1]); } });
  });
  const ids = JSON.parse(await readFile(path.join(root, 'fixtures.json'), 'utf8'));
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
  const aCookie = (await a.ctx.cookies()).find(c => c.name === '.AspNetCore.Cookies');
  const bCookie = (await b.ctx.cookies()).find(c => c.name === '.AspNetCore.Cookies');
  check(a.ctx !== b.ctx && aCookie && bCookie && aCookie.value !== bCookie.value, 'separate authenticated Customer contexts and cookies');
  await a.page.goto(base + '/Bookings/Create'); await b.page.goto(base + '/Bookings/Create');
  const aToken = await a.page.locator('input[name="__RequestVerificationToken"]').first().inputValue();
  const bToken = await b.page.locator('input[name="__RequestVerificationToken"]').first().inputValue();
  check(aToken.length > 0 && bToken.length > 0 && aToken !== bToken, 'independent antiforgery tokens are not shared');

  async function capture(tag) {
    const deadline = Date.now() + 5000;
    while (Date.now() < deadline) {
      try {
        const artifact = JSON.parse(await readFile(path.join(root, 'ownership-state.json'), 'utf8'));
        if (artifact.tag === tag) return artifact.snapshot;
      } catch (error) { if (error.code !== 'ENOENT' && !(error instanceof SyntaxError)) throw error; }
      await new Promise(resolve => setTimeout(resolve, 10));
    }
    throw Error('Timed out waiting for the tagged fixture snapshot');
  }
  const baselineTag = randomUUID();
  const baselineResponse = await a.ctx.request.get(`${base}/Bookings/Details/${ids['ownership-a']}`, { headers: { 'X-Ownership-Test-Case': baselineTag } });
  assert.equal(baselineResponse.status(), 200);
  const baseline = await capture(baselineTag), baselineText = JSON.stringify(baseline);
  const fixture = label => {
    const bookingId = ids[`ownership-${label}`];
    const item = baseline.find(x => x.booking.Id.toLowerCase() === bookingId.toLowerCase());
    assert.ok(item && item.payments.length === 1, `one synthetic ${label} payment`);
    const payment = item.payments[0];
    const proofPath = path.resolve(payment.ProofPath), allowed = path.resolve(root, 'App_Data', 'Proofs');
    assert.equal(path.dirname(proofPath).toLowerCase(), allowed.toLowerCase(), 'fixture proof inside isolated root');
    return { bookingId, paymentId: payment.Id, proofPath, paymentReference: payment.Reference,
      customerName: item.booking.CustomerName, address: item.booking.Address };
  };
  const ownA = fixture('a'), ownB = fixture('b');
  const bytesA = await readFile(ownA.proofPath), bytesB = await readFile(ownB.proofPath);
  check(ownA.bookingId !== ownB.bookingId && ownA.paymentId !== ownB.paymentId && !bytesA.equals(bytesB), 'fixtures have distinct booking IDs, payment IDs and proof bytes');

  async function own(user, item, bytes, label) {
    const result = await user.ctx.request.get(`${base}/Bookings/Proof/${item.paymentId}`, { maxRedirects: 0 });
    check(result.status() === 200 && result.headers()['content-type'] === 'image/png' && (await result.body()).equals(bytes), `${label}: own proof returns HTTP 200, image/png and exact owner bytes`);
    check((result.headers()['cache-control'] || '').includes('no-store') && result.headers()['x-content-type-options'] === 'nosniff', `${label}: own proof is no-store and nosniff`);
  }
  await own(a, ownA, bytesA, 'A'); await own(b, ownB, bytesB, 'B');
  const pageA = await a.page.goto(`${base}/Bookings/Proof/${ownA.paymentId}`);
  check(pageA.status() === 200 && pageA.headers()['content-type'] === 'image/png', 'A opens own proof via direct browser URL');
  const pageB = await b.page.goto(`${base}/Bookings/Proof/${ownB.paymentId}`);
  check(pageB.status() === 200 && pageB.headers()['content-type'] === 'image/png', 'B opens own proof via direct browser URL');

  async function deny(user, targetUrl, victim, victimBytes, label, directBrowser = false) {
    const tag = randomUUID();
    let response;
    if (directBrowser) {
      const result = await user.page.evaluate(async ({ url, caseTag }) => {
        const r = await fetch(url, { headers: { 'X-Ownership-Test-Case': caseTag }, redirect: 'manual' });
        return { status: r.status, headers: Object.fromEntries(r.headers.entries()), bytes: Array.from(new Uint8Array(await r.arrayBuffer())) };
      }, { url: base + targetUrl, caseTag: tag });
      response = { status: () => result.status, headers: () => result.headers, body: async () => Buffer.from(result.bytes) };
    } else response = await user.ctx.request.get(base + targetUrl, { maxRedirects: 0, headers: { 'X-Ownership-Test-Case': tag } });
    const body = await response.body(), headers = response.headers();
    check(response.status() === 404 && headers['content-type'] === undefined, `${label}: HTTP 404 with no content type`);
    const disclosed = Buffer.from(body).toString('utf8') + JSON.stringify(headers);
    check(body.length === 0 && !body.includes(victimBytes) && !disclosed.includes(victim.proofPath) && !disclosed.includes(path.basename(victim.proofPath)) &&
      !disclosed.includes(victim.paymentReference) && !disclosed.includes(victim.customerName) && !disclosed.includes(victim.address) &&
      !disclosed.includes(victim.bookingId) && !disclosed.includes(victim.paymentId), `${label}: no image bytes, paths, filenames or victim metadata disclosed`);
    check(JSON.stringify(await capture(tag)) === baselineText, `${label}: all synthetic booking and payment snapshots unchanged`);
    await own(a, ownA, bytesA, `${label} after denial A`); await own(b, ownB, bytesB, `${label} after denial B`);
    check((await readFile(ownA.proofPath)).equals(bytesA) && (await readFile(ownB.proofPath)).equals(bytesB), `${label}: both original proof files unchanged`);
  }
  await deny(a, `/Bookings/Proof/${ownB.paymentId}`, ownB, bytesB, 'A → B direct browser proof', true);
  await deny(b, `/Bookings/Proof/${ownA.paymentId}`, ownA, bytesA, 'B → A direct browser proof', true);
  const alter = id => (id[0] === '0' ? '1' : '0') + id.slice(1);
  const alteredA = alter(ownA.paymentId), alteredB = alter(ownB.paymentId);
  assert.ok(!baseline.some(x => x.payments.some(p => [alteredA, alteredB].includes(p.Id.toLowerCase()))), 'altered IDs do not name a fixture payment');
  await deny(a, `/Bookings/Proof/${alteredB}`, ownB, bytesB, 'A altered payment ID');
  await deny(b, `/Bookings/Proof/${alteredA}`, ownA, bytesA, 'B altered payment ID');
  await deny(a, `/Bookings/Proof/${ownB.paymentId}?bookingId=${ownA.bookingId}`, ownB, bytesB, 'A substitutes own booking ID with B payment');
  await deny(b, `/Bookings/Proof/${ownA.paymentId}?bookingId=${ownB.bookingId}`, ownA, bytesA, 'B substitutes own booking ID with A payment');
  // Customer Proof has no bookingId argument: an unrelated query parameter is
  // ignored, not evidence that the endpoint validates a booking/payment pair.
  const ignoredA = await a.ctx.request.get(`${base}/Bookings/Proof/${ownA.paymentId}?bookingId=${ownB.bookingId}`, { maxRedirects: 0 });
  check(ignoredA.status() === 200 && (await ignoredA.body()).equals(bytesA), 'A own payment remains owner-scoped even with unrelated bookingId query');
  const ignoredB = await b.ctx.request.get(`${base}/Bookings/Proof/${ownB.paymentId}?bookingId=${ownA.bookingId}`, { maxRedirects: 0 });
  check(ignoredB.status() === 200 && (await ignoredB.body()).equals(bytesB), 'B own payment remains owner-scoped even with unrelated bookingId query');
  console.log(`ALL ${checks} CUSTOMER PROOF OWNERSHIP BROWSER CHECKS PASSED; isolated root: ${root}`);
} finally {
  if (browser) await browser.close();
  server.kill();
}
