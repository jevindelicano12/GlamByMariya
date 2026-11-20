// Isolated loopback Demo only; the existing fixture hook writes private snapshots.
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { spawn } from 'node:child_process';
import { mkdir, mkdtemp, readFile, readdir, stat } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import path from 'node:path';

const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright-core');
const project = path.resolve('.'), bin = process.env.FIXTURE_BUILD || path.join(process.env.LOCALAPPDATA, 'Temp/opencode/ownership-fixture-build');
const parent = path.join(process.env.LOCALAPPDATA, 'Temp/opencode'); await mkdir(parent, { recursive: true });
const root = await mkdtemp(path.join(parent, 'proof-concurrency-'));
const server = spawn('dotnet', [path.join(bin, 'GlamByMariya.Web.dll'), '--urls=http://127.0.0.1:0', `--contentRoot=${root}`,
  `--webroot=${path.join(project, 'Frontend/GlamByMariya.Web/wwwroot')}`, '--Database:Provider=Demo', '--DemoAccounts:Presentation=false',
  '--Authentication:DurableSessionEnforcement=false', '--DemoGallery:Enabled=false', '--DemoCatalog:Enabled=false', '--DemoAvatar:Enabled=false',
  '--WebsiteContent:DemoTextEditing=false', '--WebsiteContent:DemoPersistentText=false', '--Supabase:Url=', '--Supabase:AnonKey=',
  '--Supabase:ServiceKey=', '--SupabaseStorage:ServiceKey=', '--FileStorage:Provider=Local', '--B2:Bucket=', '--B2:Endpoint=',
  '--B2:KeyId=', '--B2:AppKey=', '--Email:BrevoApiKey=', '--Email:SmtpHost='],
  { env: { ...process.env, PORT: '', ASPNETCORE_ENVIRONMENT: 'Development', DOTNET_ENVIRONMENT: 'Development',
    DOTNET_STARTUP_HOOKS: path.join(bin, 'Phase3BFixtures.dll'), PHASE3B_FIXTURE_ROOT: root,
    OWNERSHIP_BROWSER_FIXTURES: 'true', REVIEW_BROWSER_FIXTURES: 'true' }, stdio: ['ignore', 'pipe', 'pipe'] });
let browser;
try {
  const base = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(Error('isolated Demo startup timeout')), 30000);
    server.once('error', reject); server.once('exit', code => reject(Error(`isolated Demo exited ${code}`)));
    server.stderr.resume(); server.stdout.on('data', chunk => { const m = String(chunk).match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/); if (m) { clearTimeout(timer); resolve(m[1]); } });
  });
  const ids = JSON.parse(await readFile(path.join(root, 'fixtures.json'), 'utf8'));
  browser = await chromium.launch({ executablePath: process.env.BROWSER_EXECUTABLE || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', headless: true });
  async function login(email, password) {
    const ctx = await browser.newContext(); await ctx.route('**/*', r => new URL(r.request().url()).origin === base ? r.continue() : r.abort());
    const page = await ctx.newPage(); await page.goto(base + '/Account/Login'); await page.fill('[name=email]', email); await page.fill('[name=password]', password);
    await Promise.all([page.waitForURL(u => !u.pathname.includes('/Account/Login')), page.locator('#auth-form button.button').click()]);
    return { ctx, page };
  }
  const a = await login('customer@glambymariya.demo', 'Customer123!'), b = await login('customer@glambymariya.demo', 'Customer123!');
  const admin = await login('admin@glambymariya.demo', 'Admin123!');
  async function snapshot(id) {
    const tag = randomUUID(); const r = await a.ctx.request.get(`${base}/Bookings/Details/${id}`, { headers: { 'X-Ownership-Test-Case': tag } }); assert.equal(r.status(), 200);
    for (let i = 0; i < 200; i++) { try { const s = JSON.parse(await readFile(path.join(root, 'review-state.json'), 'utf8')); if (s.tag === tag) { delete s.tag; return s; } } catch (e) { if (e.code !== 'ENOENT' && !(e instanceof SyntaxError)) throw e; } await new Promise(r => setTimeout(r, 10)); }
    throw Error('snapshot timed out');
  }
  const payment = (s, id) => s.payments.find(p => p.BookingId === id);
  async function form(user, id, selector) {
    assert.equal((await user.page.goto(`${base}/${selector === 'verify' ? 'Admin' : 'Bookings'}/Details/${id}`)).status(), 200);
    const f = selector === 'verify' ? user.page.locator('form').filter({ has: user.page.locator('button[value="verify"]') }) : user.page.locator('form[action="/Bookings/CorrectProof"]');
    assert.equal(await f.count(), 1); const values = await f.evaluate(x => Object.fromEntries(new FormData(x)));
    if (selector === 'verify') values.operation = 'verify';
    delete values.proof; return values;
  }
  const proofA = await readFile(path.join(root, 'App_Data/Proofs/ownership-a.png'));
  const proofB = await readFile(path.join(root, 'App_Data/Proofs/ownership-b.png'));
  const proofDir = path.join(root, 'App_Data/Proofs');
  async function correct(user, fields, ref, bytes) {
    const r = await user.ctx.request.post(base + '/Bookings/CorrectProof', { maxRedirects: 0, multipart: { ...fields, reference: ref, proof: { name: 'synthetic.png', mimeType: 'image/png', buffer: bytes } } });
    const details = await user.ctx.request.get(base + r.headers().location); const text = await details.text();
    return { status: r.status(), success: text.includes('Payment proof updated.'), stale: text.includes('Reload') && text.includes('proof') };
  }
  async function verify(user, fields) {
    const r = await user.ctx.request.post(base + '/Admin/Action', { maxRedirects: 0, form: fields });
    const text = await (await user.ctx.request.get(base + r.headers().location)).text();
    return { status: r.status(), success: text.includes('Booking updated successfully.'), stale: text.includes('The payment claim changed.') };
  }
  const id = ids['ownership-pending-a'];
  for (let i = 0; i < 8; i++) {
    const before = await snapshot(id), p = payment(before, id), filesBefore = await readdir(proofDir);
    const [fa, fb] = await Promise.all([form(a, id, 'correct'), form(b, id, 'correct')]);
    const refs = [`RACE-${i}-A`, `RACE-${i}-B`];
    const order = [];
    const [ra, rb] = await Promise.all([correct(a, fa, refs[0], proofA).then(x => (order.push('A'), x)), correct(b, fb, refs[1], proofB).then(x => (order.push('B'), x))]);
    const after = await snapshot(id), current = payment(after, id), filesAfter = await readdir(proofDir);
    const added = filesAfter.filter(x => !filesBefore.includes(x));
    const existing = await stat(current.ProofPath).then(() => true, () => false);
    console.log(JSON.stringify({ trial: i + 1, responses: [ra, rb], responseCompletionOrder: order, oldRevision: p.Revision, revision: current.Revision, reference: current.Reference, history: current.ProofHistory.slice(-2).map(x => ({ reference: x.Reference, path: x.ProofPath })), finalPath: current.ProofPath, finalFileExists: existing, addedFiles: added }));
    assert.equal(ra.status, 302); assert.equal(rb.status, 302); assert.ok(existing);
    if (process.env.REPRO_BEFORE_FIX === 'true') { assert.ok(ra.success && rb.success); assert.equal(current.Revision, p.Revision + 2); assert.equal(added.length, 2); }
    else {
      assert.equal(Number(ra.success) + Number(rb.success), 1); assert.ok(ra.stale || rb.stale);
      assert.equal(current.Revision, p.Revision + 1); assert.equal(current.ProofHistory.length, p.ProofHistory.length + 1);
      assert.equal(current.Reference, ra.success ? refs[0] : refs[1]);
      assert.equal(current.ProofHistory.at(-1).Reference, p.Reference); assert.equal(current.ProofHistory.at(-1).ProofPath, p.ProofPath);
      assert.ok(await stat(p.ProofPath).then(() => true, () => false));
      assert.equal(added.length, 1); assert.equal(path.basename(current.ProofPath), added[0]);
      assert.equal(after.notifications.length - before.notifications.length, 3); assert.equal(after.audit.length - before.audit.length, 1);
      assert.equal(JSON.stringify(after.bookings.filter(x => x.Id !== id)), JSON.stringify(before.bookings.filter(x => x.Id !== id)));
      assert.equal(JSON.stringify(after.payments.filter(x => x.BookingId !== id)), JSON.stringify(before.payments.filter(x => x.BookingId !== id)));
    }
  }
  if (process.env.REPRO_BEFORE_FIX !== 'true') {
    const before = await snapshot(id), p = payment(before, id), f = await form(a, id, 'correct');
    const missing = { ...f }; delete missing.paymentRevision;
    const bytes = await readFile(p.ProofPath), files = await readdir(proofDir);
    for (const fields of [{ ...f, paymentRevision: String(p.Revision - 1) }, missing, { ...f, paymentRevision: 'invalid' }]) {
      const result = await correct(a, fields, 'EXPLICIT-STALE', proofA); const after = await snapshot(id);
      assert.ok(!result.success && result.stale); assert.deepEqual(after, before); assert.deepEqual(await readdir(proofDir), files); assert.deepEqual(await readFile(p.ProofPath), bytes);
    }
    const fresh = await correct(a, await form(a, id, 'correct'), 'FRESH-RELOAD', proofA);
    assert.ok(fresh.success); assert.equal(payment(await snapshot(id), id).Revision, p.Revision + 1);
    console.log('PASS stale, missing and malformed revisions preserve complete snapshot and files; fresh correction succeeds');
  }
  const raceId = ids['individual-unverified'], initial = await snapshot(id), old = payment(initial, raceId);
  const [customerForm, adminForm] = await Promise.all([form(a, raceId, 'correct'), form(admin, raceId, 'verify')]);
  const [correction, verification] = await Promise.all([correct(a, customerForm, 'RACE-ADMIN-NEW', proofA), verify(admin, adminForm)]);
  const final = await snapshot(id), finalPayment = payment(final, raceId), booking = final.bookings.find(x => x.Id === raceId);
  console.log(JSON.stringify({ adminRace: { correction, verification, initialRevision: old.Revision, finalRevision: finalPayment.Revision, reference: finalPayment.Reference, verified: finalPayment.Verified, status: booking.Status } }));
  assert.ok((correction.success && verification.stale && !finalPayment.Verified && booking.Status === 'Pending Payment') || (!correction.success && verification.success && finalPayment.Verified && booking.Status === 'Confirmed'));
  console.log(`PASS concurrent correction vs Admin verification; isolated root: ${root}`);
} finally { if (browser) await browser.close(); server.kill(); }
