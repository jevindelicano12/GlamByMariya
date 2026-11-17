// Fresh loopback-only Demo with test-only synthetic Customers and local images.
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { spawn } from 'node:child_process';
import { mkdir, mkdtemp, readFile, readdir } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import path from 'node:path';

const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright-core');
const project = path.resolve('.');
const bin = process.env.FIXTURE_BUILD || path.join(process.env.LOCALAPPDATA, 'Temp/opencode/ownership-fixture-build');
const parent = path.join(process.env.LOCALAPPDATA, 'Temp/opencode'); await mkdir(parent, { recursive: true });
const root = await mkdtemp(path.join(parent, 'customer-proof-correction-'));
const proofDir = path.join(root, 'App_Data', 'Proofs');
const server = spawn('dotnet', [path.join(bin, 'GlamByMariya.Web.dll'), '--urls=http://127.0.0.1:0', `--contentRoot=${root}`,
  `--webroot=${path.join(project, 'Frontend/GlamByMariya.Web/wwwroot')}`, '--Database:Provider=Demo', '--DemoAccounts:Presentation=false',
  '--Authentication:DurableSessionEnforcement=false', '--DemoGallery:Enabled=false', '--DemoCatalog:Enabled=false', '--DemoAvatar:Enabled=false',
  '--WebsiteContent:DemoTextEditing=false', '--WebsiteContent:DemoPersistentText=false', '--Supabase:Url=', '--Supabase:AnonKey=',
  '--Supabase:ServiceKey=', '--SupabaseStorage:ServiceKey=', '--FileStorage:Provider=Local', '--B2:Bucket=', '--B2:Endpoint=',
  '--B2:KeyId=', '--B2:AppKey=', '--Email:BrevoApiKey=', '--Email:SmtpHost='],
  { env: { ...process.env, PORT: '', ASPNETCORE_ENVIRONMENT: 'Development', DOTNET_ENVIRONMENT: 'Development',
    DOTNET_STARTUP_HOOKS: path.join(bin, 'Phase3BFixtures.dll'), PHASE3B_FIXTURE_ROOT: root, OWNERSHIP_BROWSER_FIXTURES: 'true' },
    stdio: ['ignore', 'pipe', 'pipe'] });
let browser, checks = 0;
const check = (ok, label) => { assert.ok(ok, label); console.log(`PASS ${++checks}: ${label}`); };

try {
  const base = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(Error('isolated Demo startup timeout')), 30000);
    server.once('error', e => { clearTimeout(timer); reject(e); });
    server.once('exit', code => { clearTimeout(timer); reject(Error(`isolated Demo exited ${code}`)); });
    server.stderr.resume(); server.stdout.on('data', chunk => {
      const match = String(chunk).match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/);
      if (match) { clearTimeout(timer); resolve(match[1]); }
    });
  });
  const ids = JSON.parse(await readFile(path.join(root, 'fixtures.json'), 'utf8'));
  browser = await chromium.launch({ executablePath: process.env.BROWSER_EXECUTABLE || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', headless: true });
  async function login(email, password) {
    const ctx = await browser.newContext();
    await ctx.route('**/*', route => new URL(route.request().url()).origin === base ? route.continue() : route.abort());
    const page = await ctx.newPage(); await page.goto(base + '/Account/Login');
    await page.fill('[name=email]', email); await page.fill('[name=password]', password);
    await Promise.all([page.waitForURL(u => !u.pathname.includes('/Account/Login')), page.locator('#auth-form button.button').click()]);
    return { ctx, page };
  }
  const a = await login('customer@lumiere.demo', 'Customer123!'), b = await login('ownership-b@example.test', 'SyntheticCustomerB123!');
  const aCookie = (await a.ctx.cookies()).find(c => c.name === '.AspNetCore.Cookies');
  const bCookie = (await b.ctx.cookies()).find(c => c.name === '.AspNetCore.Cookies');
  check(a.ctx !== b.ctx && aCookie && bCookie && aCookie.value !== bCookie.value, 'independent authenticated contexts and cookies');
  for (const [user, label] of [[a, 'a'], [b, 'b']]) {
    const response = await user.page.goto(`${base}/Bookings/Details/${ids[`ownership-pending-${label}`]}`);
    assert.equal(response.status(), 200); assert.equal(await user.page.locator('form[action="/Bookings/CorrectProof"]').count(), 1);
  }
  const token = async user => user.page.locator('form[action="/Bookings/CorrectProof"] input[name="__RequestVerificationToken"]').inputValue();
  const tokenA = await token(a), tokenB = await token(b);
  check(tokenA.length > 0 && tokenB.length > 0 && tokenA !== tokenB, 'independent antiforgery tokens from each own correction form');

  async function capture(tag) {
    const deadline = Date.now() + 5000;
    while (Date.now() < deadline) {
      try { const record = JSON.parse(await readFile(path.join(root, 'ownership-state.json'), 'utf8')); if (record.tag === tag) return record.snapshot; }
      catch (e) { if (e.code !== 'ENOENT' && !(e instanceof SyntaxError)) throw e; }
      await new Promise(resolve => setTimeout(resolve, 10));
    }
    throw Error('Timed out waiting for tagged fixture snapshot');
  }
  const baselineTag = randomUUID();
  assert.equal((await a.ctx.request.get(`${base}/Bookings/Details/${ids['ownership-pending-a']}`, { headers: { 'X-Ownership-Test-Case': baselineTag } })).status(), 200);
  const baseline = await capture(baselineTag), baselineText = JSON.stringify(baseline);
  function fixture(key) {
    const bookingId = ids[key], entry = baseline.find(x => x.booking.Id.toLowerCase() === bookingId.toLowerCase());
    assert.ok(entry && entry.payments.length === 1, `one payment for ${key}`);
    const payment = entry.payments[0], proofPath = path.resolve(payment.ProofPath);
    assert.equal(path.dirname(proofPath).toLowerCase(), path.resolve(proofDir).toLowerCase());
    return { bookingId, paymentId: payment.Id, payment, proofPath, name: entry.booking.CustomerName, address: entry.booking.Address };
  }
  const ownA = fixture('ownership-pending-a'), ownB = fixture('ownership-pending-b');
  const originalA = await readFile(ownA.proofPath), originalB = await readFile(ownB.proofPath);
  const replacementA = await readFile(fixture('ownership-a').proofPath), replacementB = await readFile(fixture('ownership-b').proofPath);
  check(ownA.bookingId !== ownB.bookingId && ownA.paymentId !== ownB.paymentId && !originalA.equals(originalB) && !originalA.equals(replacementA) && !originalB.equals(replacementB), 'distinct pending bookings, payments and synthetic proof files');
  check(!ownA.payment.Verified && !ownB.payment.Verified && baseline.find(x => x.booking.Id === ownA.bookingId).booking.Status === 'Pending Payment' && baseline.find(x => x.booking.Id === ownB.bookingId).booking.Status === 'Pending Payment', 'both targets satisfy existing correction rules');
  const baselineFiles = (await readdir(proofDir)).sort();
  async function original(user, item, bytes, label) {
    const result = await user.ctx.request.get(`${base}/Bookings/Proof/${item.paymentId}`, { maxRedirects: 0 });
    check(result.status() === 200 && result.headers()['content-type'] === 'image/png' && (await result.body()).equals(bytes), `${label}: owner retrieves unchanged original proof`);
  }
  await original(a, ownA, originalA, 'A baseline'); await original(b, ownB, originalB, 'B baseline');

  async function unchanged(tag, label) {
    check(JSON.stringify(await capture(tag)) === baselineText, `${label}: all booking and payment snapshots unchanged`);
    check(JSON.stringify((await readdir(proofDir)).sort()) === JSON.stringify(baselineFiles), `${label}: no staged proof retained or existing file deleted`);
    check((await readFile(ownA.proofPath)).equals(originalA) && (await readFile(ownB.proofPath)).equals(originalB), `${label}: both original proof files unchanged`);
    await original(a, ownA, originalA, `${label} A`); await original(b, ownB, originalB, `${label} B`);
  }
  async function denied(user, attackerToken, bookingId, victim, extra, label, expectedStatus = 404) {
    const tag = randomUUID();
    const response = await user.ctx.request.post(base + '/Bookings/CorrectProof', { maxRedirects: 0,
      headers: { 'X-Ownership-Test-Case': tag }, multipart: { id: bookingId, reference: `ATTEMPT-${randomUUID()}`,
        __RequestVerificationToken: attackerToken, ...extra, proof: { name: 'synthetic.png', mimeType: 'image/png', buffer: replacementA } } });
    const body = await response.body(); const visible = body.toString('utf8') + JSON.stringify(response.headers());
    check(response.status() === expectedStatus, `${label}: exact HTTP ${expectedStatus}`);
    check(body.length === 0 && response.headers()['content-type'] === undefined, `${label}: empty response body and no Content-Type`);
    check(!visible.includes(victim.proofPath) && !visible.includes(path.basename(victim.proofPath)) && !visible.includes(victim.payment.Reference)
      && !visible.includes(victim.name) && !visible.includes(victim.address) && !visible.includes(victim.bookingId) && !visible.includes(victim.paymentId)
      && !body.includes(victim === ownA ? originalA : originalB), `${label}: no victim proof or metadata exposed`);
    await unchanged(tag, label);
  }
  await denied(a, tokenA, ownB.bookingId, ownB, {}, 'A → B correction');
  await denied(b, tokenB, ownA.bookingId, ownA, {}, 'B → A correction');
  const alter = id => (id[0] === '0' ? '1' : '0') + id.slice(1);
  const alteredA = alter(ownA.bookingId), alteredB = alter(ownB.bookingId);
  assert.ok(!baseline.some(x => [alteredA, alteredB].includes(x.booking.Id.toLowerCase())), 'altered IDs do not identify a fixture booking');
  await denied(a, tokenA, alteredB, ownB, {}, 'A altered booking ID');
  await denied(b, tokenB, alteredA, ownA, {}, 'B altered booking ID');
  await denied(a, tokenA, ownB.paymentId, ownB, {}, 'A submits B payment ID as booking ID');
  await denied(b, tokenB, ownA.paymentId, ownA, {}, 'B submits A payment ID as booking ID');
  await denied(a, tokenA, ownB.bookingId, ownB, { paymentId: ownA.paymentId }, 'A own payment ID with B booking ID');
  await denied(b, tokenB, ownA.bookingId, ownA, { paymentId: ownB.paymentId }, 'B own payment ID with A booking ID');
  await denied(a, tokenB, ownA.bookingId, ownB, {}, 'A submits B antiforgery token', 400);
  await denied(b, '', ownB.bookingId, ownA, {}, 'B omits antiforgery token', 400);

  // A valid owner cannot correct a confirmed/verified booking. This is a
  // same-owner status denial, not the cross-Customer 404 behavior.
  const statusTag = randomUUID(), confirmedA = ids['ownership-a'];
  const badStatus = await a.ctx.request.post(base + '/Bookings/CorrectProof', { maxRedirects: 0,
    headers: { 'X-Ownership-Test-Case': statusTag }, multipart: { id: confirmedA, reference: 'STATUS-TEST', __RequestVerificationToken: tokenA,
      proof: { name: 'synthetic.png', mimeType: 'image/png', buffer: replacementA } } });
  check(badStatus.status() === 302 && new URL(badStatus.headers().location, base).pathname === `/Bookings/Details/${confirmedA}`, 'owner confirmed correction redirects to own details');
  const statusPage = await a.ctx.request.get(`${base}/Bookings/Details/${confirmedA}`);
  check(statusPage.status() === 200 && (await statusPage.text()).includes('Only unverified pending deposits can be corrected.'), 'owner confirmed correction shows exact status restriction');
  await unchanged(statusTag, 'confirmed status rejection');

  async function submitOwn(user, item, original, replacement, label, otherId, priorState) {
    await user.page.goto(`${base}/Bookings/Details/${item.bookingId}`);
    const form = user.page.locator('form[action="/Bookings/CorrectProof"]');
    await form.locator('[name=reference]').fill(`NEW-${label}`);
    await form.locator('[name=proof]').setInputFiles({ name: 'synthetic.png', mimeType: 'image/png', buffer: replacement });
    if (otherId) await form.evaluate((element, value) => { const input = document.createElement('input'); input.type = 'hidden'; input.name = 'paymentId'; input.value = value; element.append(input); }, otherId);
    const tag = randomUUID(); await user.page.setExtraHTTPHeaders({ 'X-Ownership-Test-Case': tag });
    let result;
    try { [result] = await Promise.all([user.page.waitForResponse(r => r.request().method() === 'POST' && new URL(r.url()).pathname === '/Bookings/CorrectProof'), form.locator('button').click()]); await user.page.waitForLoadState('load'); }
    finally { await user.page.setExtraHTTPHeaders({}); }
    check(result.status() === 302 && new URL(result.headers().location, base).pathname === `/Bookings/Details/${item.bookingId}`, `${label} own correction: exact 302 to own details`);
    check((await user.page.locator('body').innerText()).includes('Payment proof updated.'), `${label} own correction: exact success message`);
    const after = await capture(tag), entry = after.find(x => x.booking.Id === item.bookingId), p = entry.payments[0];
    check(entry.booking.Status === 'Pending Payment' && p.Reference === `NEW-${label}` && p.Revision === item.payment.Revision + 1 && !p.Verified && p.ProofHistory.length === item.payment.ProofHistory.length + 1 && p.ProofHistory.at(-1).ProofPath === item.proofPath && p.ProofHistory.at(-1).Reference === item.payment.Reference, `${label} own correction updates only permitted claim fields and retains proof history`);
    const previous = priorState.find(x => x.booking.Id === item.bookingId);
    const { History: newHistory, ...newBooking } = entry.booking, { History: oldHistory, ...oldBooking } = previous.booking;
    const withoutProofChanges = ({ Reference, ProofPath, Revision, ProofHistory, ...other }) => other;
    check(JSON.stringify(newBooking) === JSON.stringify(oldBooking) && newHistory.length === oldHistory.length + 1 &&
      JSON.stringify(withoutProofChanges(p)) === JSON.stringify(withoutProofChanges(previous.payments[0])), `${label} own correction preserves all other target booking and payment fields`);
    check(JSON.stringify(after.filter(x => x.booking.Id !== item.bookingId)) === JSON.stringify(priorState.filter(x => x.booking.Id !== item.bookingId)), `${label} own correction leaves every other booking and payment unchanged`);
    const newPath = path.resolve(p.ProofPath); assert.equal(path.dirname(newPath).toLowerCase(), path.resolve(proofDir).toLowerCase());
    const newBytes = await readFile(newPath), fetched = await user.ctx.request.get(`${base}/Bookings/Proof/${p.Id}`, { maxRedirects: 0 });
    check(newPath !== item.proofPath && !newBytes.equals(original) && fetched.status() === 200 && fetched.headers()['content-type'] === 'image/png' && (await fetched.body()).equals(newBytes), `${label} own corrected proof is retrieved from its new synthetic file`);
    check((await readFile(item.proofPath)).equals(original), `${label} prior synthetic proof remains available for history`);
    return after;
  }
  const afterA = await submitOwn(a, ownA, originalA, replacementA, 'A', null, baseline);
  await submitOwn(b, ownB, originalB, replacementB, 'B', ownA.paymentId, afterA);
  console.log(`ALL ${checks} CUSTOMER PROOF CORRECTION BROWSER CHECKS PASSED; isolated root: ${root}`);
} finally {
  if (browser) await browser.close();
  server.kill();
}
