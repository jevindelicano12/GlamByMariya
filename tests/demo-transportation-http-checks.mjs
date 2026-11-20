// Build Release first, then run with Node. No npm dependencies or external base
// URL: owns an isolated loopback Demo server and a unique temporary data root.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdtemp, mkdir } from 'node:fs/promises';
import { deflateSync } from 'node:zlib';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
const parent = process.env.LOCALAPPDATA ? path.join(process.env.LOCALAPPDATA, 'Temp', 'opencode') : os.tmpdir();
await mkdir(parent, { recursive: true });
const contentRoot = await mkdtemp(path.join(parent, 'transportation-78i-'));
const server = spawn('dotnet', [path.join(root, 'Frontend/GlamByMariya.Web/bin/Release/net10.0/GlamByMariya.Web.dll'),
  '--urls=http://127.0.0.1:0', `--contentRoot=${contentRoot}`, `--webroot=${path.join(root, 'Frontend/GlamByMariya.Web/wwwroot')}`,
  '--Database:Provider=Demo', '--Supabase:Url=', '--Supabase:AnonKey=', '--SupabaseStorage:ServiceKey=',
  '--B2:Bucket=', '--B2:Endpoint=', '--B2:KeyId=', '--B2:AppKey=', '--Email:BrevoApiKey=', '--Email:SmtpHost='
], { cwd: contentRoot, env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', DOTNET_ENVIRONMENT: 'Development',
  ASPNETCORE_FORWARDEDHEADERS_ENABLED: 'false', PORT: '' }, stdio: ['ignore', 'pipe', 'pipe'] });
let checks = 0;
function check(value, message) { assert.ok(value, message); checks++; console.log(`PASS: ${message}`); }
try {
  const base = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Local Demo startup timed out.')), 30000);
    server.on('error', error => { clearTimeout(timer); reject(error); });
    server.on('exit', () => { clearTimeout(timer); reject(new Error('Local Demo exited during startup.')); });
    server.stdout.on('data', chunk => {
      const match = chunk.toString().match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/);
      if (match) { clearTimeout(timer); resolve(match[1]); }
    });
    server.stderr.resume();
  });
  const customer = new Map(), admin = new Map();
  async function request(jar, requestPath, form) {
    const url = new URL(requestPath, base);
    assert.equal(url.origin, base, 'Requests must stay on the owned loopback server.');
    const response = await fetch(url, { redirect: 'manual', signal: AbortSignal.timeout(10000),
      headers: { Cookie: [...jar].map(([name, value]) => `${name}=${value}`).join('; ') },
      ...(form ? { method: 'POST', body: form instanceof FormData ? form : new URLSearchParams(form) } : {}) });
    for (const cookie of response.headers.getSetCookie()) {
      const pair = cookie.split(';')[0], index = pair.indexOf('=');
      const name = pair.slice(0, index), value = pair.slice(index + 1);
      if (value) jar.set(name, value); else jar.delete(name);
    }
    return { status: response.status, location: response.headers.get('location'), body: await response.text() };
  }
  function token(html) {
    const match = html.match(/name="__RequestVerificationToken"[^>]*value="([^"]+)"/);
    assert.ok(match, 'Expected antiforgery token in local Demo form.'); return match[1];
  }
  async function signIn(jar, email, password, expected) {
    const page = await request(jar, '/Account/Login');
    const response = await request(jar, '/Account/Login', { email, password, __RequestVerificationToken: token(page.body) });
    check(response.status === 302 && response.location === expected, 'Demo account signs in to its existing dashboard');
  }
  const ids = html => [...html.matchAll(/href="\/Bookings\/Details\/([a-f0-9-]+)"/g)].map(match => match[1]);
  const status = html => html.match(/<span>Transportation policy<\/span><strong>([^<]+)<\/strong>/)?.[1];
  await signIn(customer, 'customer@glambymariya.demo', 'Customer123!', '/Bookings');
  await signIn(admin, 'admin@glambymariya.demo', 'Admin123!', '/Admin');
  const before = ids((await request(customer, '/Bookings')).body);
  const history = new Map();
  for (const id of before) history.set(id, status((await request(admin, '/Admin/Details/' + id)).body));
  const create = await request(customer, '/Bookings/Create');
  const serviceId = create.body.match(/name="serviceId" value="([^"]+)"/)[1];
  const today = create.body.match(/data-today="([^"]+)"/)[1];
  const [year, month] = today.split('-').map(Number);
  const nextMonth = `${month === 12 ? year + 1 : year}-${String(month === 12 ? 1 : month + 1).padStart(2, '0')}`;
  const query = new URLSearchParams({ serviceId, people: '1', package: 'auto' });
  const quote = JSON.parse((await request(customer, '/Bookings/Quote?' + query)).body);
  const calendar = JSON.parse((await request(customer, '/Bookings/Availability?' + query + '&month=' + nextMonth)).body);
  const slot = calendar.days.flatMap(day => day.slots).find(value => value.available);
  assert.ok(slot, 'Isolated Demo must have a future available slot.');
  const form = { serviceId, people: '1', package: 'auto', start: slot.start, address: '78I Isolated Demo Street', area: 'Quezon City',
    deposit: String(quote.requiredDownPayment), expectedTotal: String(quote.finalTotal), expectedMinimum: String(quote.requiredDownPayment), method: 'GCash', reference: 'LOCAL-78I-HTTP-ACK', agreed: 'true',
    travelAcknowledged: 'true', __RequestVerificationToken: token(create.body) };
  function chunk(type, bytes) {
    const data = Buffer.concat([Buffer.from(type), bytes]); let crc = 0xffffffff;
    for (const byte of data) { crc ^= byte; for (let i = 0; i < 8; i++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0); }
    const length = Buffer.alloc(4), checksum = Buffer.alloc(4);
    length.writeUInt32BE(bytes.length); checksum.writeUInt32BE((crc ^ 0xffffffff) >>> 0);
    return Buffer.concat([length, data, checksum]);
  }
  const header = Buffer.alloc(13); header.writeUInt32BE(1, 0); header.writeUInt32BE(1, 4); header[8] = 8; header[9] = 2;
  const png = Buffer.concat([Buffer.from('89504e470d0a1a0a', 'hex'), chunk('IHDR', header), chunk('IDAT', deflateSync(Buffer.from([0, 32, 64, 96]))), chunk('IEND', Buffer.alloc(0))]);
  function withProof(fields) {
    const result = new FormData();
    for (const [key, value] of Object.entries(fields)) result.set(key, value);
    result.set('proof', new Blob([png], { type: 'image/png' }), 'synthetic.png');
    return result;
  }
  const accepted = await request(customer, '/Bookings/Create', withProof(form));
  check(accepted.status === 302 && accepted.location === '/Bookings', 'Acknowledged Demo booking is accepted by the actual controller');
  const after = ids((await request(customer, '/Bookings')).body);
  const added = after.filter(id => !before.includes(id));
  check(added.length === 1, 'Exactly one new Demo booking is created');
  const details = await request(admin, '/Admin/Details/' + added[0]);
  check(details.status === 200 && status(details.body) === 'Acknowledged', 'Actual Admin booking details show Transportation policy: Acknowledged');
  check(details.body.includes('Separate actual expense'), 'Admin still shows transportation reimbursement as a separate expense');
  for (const [id, original] of history)
    check(status((await request(admin, '/Admin/Details/' + id)).body) === original, 'Historical Demo acknowledgment display is not backfilled or changed');
  const retryPage = await request(customer, '/Bookings/Create');
  const rejected = await request(customer, '/Bookings/Create', withProof({ ...form, reference: 'LOCAL-78I-HTTP-NO-ACK',
    travelAcknowledged: 'false', __RequestVerificationToken: token(retryPage.body) }));
  check(rejected.status === 302 && rejected.location.startsWith('/Bookings/Create'), 'Actual controller rejects missing transportation acknowledgment');
  const error = await request(customer, rejected.location);
  check(error.body.includes('Read and accept the Transportation Fee Policy'), 'Rejected acknowledgment produces the existing clear validation message');
  check(JSON.stringify(ids((await request(customer, '/Bookings')).body)) === JSON.stringify(after), 'Rejected submission creates no extra booking');
  console.log(`ALL DEMO TRANSPORTATION HTTP CHECKS PASSED (${checks} checks)`);
} finally {
  if (server.exitCode === null && !server.killed) { const exited = once(server, 'exit'); server.kill(); await exited; }
  console.log('Retained isolated test content root:', contentRoot);
}
