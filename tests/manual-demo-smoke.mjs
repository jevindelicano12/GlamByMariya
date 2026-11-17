// Isolated synthetic HTTP smoke checks. Never accepts an external server URL.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdir } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { deflateSync } from 'node:zlib';
import path from 'node:path';
import { owner, admin, customer } from './presentation-test-accounts.mjs';

const project = path.resolve('.');
const id = randomUUID();
const root = path.join(process.env.LOCALAPPDATA, 'Temp', 'opencode', `manual-smoke-${id}`);
const data = path.join(process.env.LOCALAPPDATA, 'GlamByMariya', `manual-smoke-${id}`);
await mkdir(root, { recursive: true });
const server = spawn('dotnet', [path.join(process.env.UI_CHECK_BUILD || path.join(project, 'Frontend/GlamByMariya.Web/bin/Release/net10.0'), 'GlamByMariya.Web.dll'), '--urls=http://127.0.0.1:0',
  `--contentRoot=${root}`, `--webroot=${path.join(project, 'Frontend/GlamByMariya.Web/wwwroot')}`, '--Database:Provider=Demo',
  '--Authentication:DurableSessionEnforcement=false', '--DemoGallery:Enabled=true', `--DemoGallery:Root=${path.join(data, 'gallery')}`,
  '--WebsiteContent:DemoTextEditing=true', '--Supabase:Url=', '--Supabase:AnonKey=', '--Supabase:ServiceKey=',
  '--SupabaseStorage:ServiceKey=', '--FileStorage:Provider=Local', '--B2:Bucket=', '--B2:Endpoint=', '--B2:KeyId=',
  '--B2:AppKey=', '--Email:BrevoApiKey=', '--Email:SmtpHost=' ],
{ env: { ...process.env, PORT: '', ASPNETCORE_ENVIRONMENT: 'Development', DOTNET_ENVIRONMENT: 'Development' }, stdio: ['ignore', 'pipe', 'pipe'] });
try {
  const base = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(Error('Synthetic Demo startup timed out')), 30000);
    server.on('error', reject); server.once('exit', code => reject(Error(`Synthetic Demo exited: ${code}`)));
    server.stderr.resume(); server.stdout.on('data', chunk => { const match = String(chunk).match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/); if (match) { clearTimeout(timer); resolve(match[1]); } });
  });
  const client = () => {
    const cookies = new Map();
    return async (url, body) => {
      const response = await fetch(base + url, { redirect: 'manual', headers: { cookie: [...cookies].map(([k,v]) => `${k}=${v}`).join('; ') },
        ...(body ? { method: 'POST', body } : {}), signal: AbortSignal.timeout(10000) });
      for (const c of response.headers.getSetCookie()) { const pair = c.split(';')[0], n = pair.indexOf('='); cookies.set(pair.slice(0,n), pair.slice(n+1)); }
      return { status: response.status, location: response.headers.get('location'), body: await response.text() };
    };
  };
  const token = html => { const value = html.match(/name="__RequestVerificationToken"[^>]*value="([^"]+)"/)?.[1]; assert.ok(value, 'antiforgery token'); return value; };
  async function login(account) {
    const req = client(), page = await req('/Account/Login');
    const result = await req('/Account/Login', new URLSearchParams({ email: account.email, password: account.password, __RequestVerificationToken: token(page.body) }));
    assert.equal(result.status, 302); return req;
  }
  const customerReq = await login(customer), adminReq = await login(admin), ownerReq = await login(owner);
  console.log('PASS customer, admin and SuperAdmin login');
  const settings = await ownerReq('/Admin/Settings'); assert.equal(settings.status, 200);
  const feeForm = new URLSearchParams({ __RequestVerificationToken: token(settings.body), earlyFeeEnabled: 'true', earlyStartHour: '5', earlyFee: '123',
    lateFeeEnabled: 'true', lateStartHour: '17', lateEndHour: '23', lateFee: '234' });
  assert.equal((await adminReq('/Admin/FeeSettings', feeForm)).status, 302); // authorization redirect
  assert.equal((await ownerReq('/Admin/FeeSettings', feeForm)).status, 302);
  assert.match((await ownerReq('/Admin/Settings')).body, /Demo fee settings updated/);
  console.log('PASS SuperAdmin fee settings and Admin restriction');
  const create = await customerReq('/Bookings/Create'); assert.equal(create.status, 200);
  const serviceId = create.body.match(/name="serviceId" value="([^"]+)"/)?.[1];
  const addOnId = create.body.match(/data-addon-id="([^"]+)"/)?.[1]; assert.ok(serviceId && addOnId);
  const today = create.body.match(/data-today="([^"]+)"/)?.[1]; assert.ok(today);
  const [year, month] = today.split('-').map(Number);
  const nextMonth = `${month === 12 ? year + 1 : year}-${String(month === 12 ? 1 : month + 1).padStart(2,'0')}`;
  const date = `${nextMonth}-15`;
  const query = new URLSearchParams({ serviceId, people: '4', package: 'group', start: `${date}T17:00` });
  query.append('addOnIds', addOnId); query.append('addOnIds', addOnId);
  const quoted = await customerReq('/Bookings/Quote?' + query); assert.equal(quoted.status, 200);
  const quote = JSON.parse(quoted.body); assert.equal(quote.lateFee, 234); assert.equal(quote.people, 4);
  assert.ok(quote.extrasTotal > 0 && quote.requiredDownPayment > 0);
  const early = new URLSearchParams(query); early.set('start', `${date}T05:00`);
  const earlyQuote = JSON.parse((await customerReq('/Bookings/Quote?' + early)).body); assert.equal(earlyQuote.earlyFee, 123); assert.equal(earlyQuote.lateFee, 0);
  console.log('PASS early/late quote, group pricing, add-on quantities and down payment');
  const params = new URLSearchParams(query); params.delete('start'); params.set('month', nextMonth);
  const calendar = JSON.parse((await customerReq('/Bookings/Availability?' + params)).body);
  const slot = calendar.days.flatMap(d => d.slots).find(s => s.start === `${date}T17:00` && s.available); assert.ok(slot, 'synthetic slot available');
  function fields() { return { serviceId, people: '4', package: 'group', start: slot.start, address: 'Synthetic street address', area: 'Quezon City',
    deposit: String(quote.requiredDownPayment), expectedTotal: String(quote.finalTotal), expectedMinimum: String(quote.requiredDownPayment),
    method: 'GCash', reference: `SMOKE-${id}`, agreed: 'true', travelAcknowledged: 'true', __RequestVerificationToken: token(create.body) }; }
  const noProof = new FormData(); for (const [k,v] of Object.entries(fields())) noProof.set(k,v); noProof.append('addOnIds',addOnId); noProof.append('addOnIds',addOnId);
  assert.match((await customerReq('/Bookings/Create', noProof)).location, /\/Bookings\/Create/);
  function chunk(type, bytes) { const n=Buffer.from(type), b=Buffer.concat([n,bytes]); let crc=0xffffffff; for (const x of b) { crc^=x; for(let i=0;i<8;i++) crc=(crc>>>1)^((crc&1)?0xedb88320:0); } const len=Buffer.alloc(4), sum=Buffer.alloc(4); len.writeUInt32BE(bytes.length); sum.writeUInt32BE((crc^0xffffffff)>>>0); return Buffer.concat([len,b,sum]); }
  const header=Buffer.alloc(13); header.writeUInt32BE(1,0); header.writeUInt32BE(1,4); header[8]=8; header[9]=2;
  const png=Buffer.concat([Buffer.from('89504e470d0a1a0a','hex'),chunk('IHDR',header),chunk('IDAT',deflateSync(Buffer.from([0,32,64,96]))),chunk('IEND',Buffer.alloc(0))]);
  const before = (await customerReq('/Bookings')).body;
  const form = new FormData(); for (const [k,v] of Object.entries(fields())) form.set(k,v); form.append('addOnIds',addOnId); form.append('addOnIds',addOnId);
  form.set('proof', new Blob([png], { type: 'image/png' }), 'synthetic.png');
  assert.equal((await customerReq('/Bookings/Create', form)).location, '/Bookings');
  const after = (await customerReq('/Bookings')).body;
  const ids = html => [...html.matchAll(/href="\/Bookings\/Details\/([a-f0-9-]+)"/g)].map(m=>m[1]);
  const added = ids(after).filter(x => !ids(before).includes(x)); assert.equal(added.length, 1);
  const details = await adminReq('/Admin/Details/' + added[0]); assert.equal(details.status, 200); assert.match(details.body, /Pending Payment/); assert.match(details.body, /View private proof/);
  const recheck = await customerReq('/Bookings/ValidateSlot?' + query); assert.equal(JSON.parse(recheck.body).available, false);
  assert.equal((await adminReq('/Admin/Appointments?mode=calendar')).status, 200);
  const verifyForm = details.body.match(/<form[^>]*>\s*<input[^>]*name="id"[^>]*>\s*<input[^>]*name="paymentId"[^>]*>\s*<input[^>]*name="paymentRevision"[^>]*>[\s\S]*?<button[^>]*value="verify"[\s\S]*?<\/form>/)?.[0];
  assert.ok(verifyForm, 'non-mixed verification form contains reviewed payment and revision');
  const paymentId = verifyForm.match(/name="paymentId" value="([^"]+)"/)?.[1];
  const paymentRevision = verifyForm.match(/name="paymentRevision" value="([^"]+)"/)?.[1];
  assert.ok(paymentId && paymentRevision !== undefined);
  const action = new URLSearchParams({ id: added[0], operation: 'verify', paymentId, paymentRevision, __RequestVerificationToken: token(verifyForm) });
  assert.equal((await adminReq('/Admin/Action', action)).status, 302);
  assert.match((await adminReq('/Admin/Details/' + added[0])).body, /Confirmed/);
  console.log('PASS required proof, booking, pending reservation, calendar and Admin payment verification');
  for (const [req, url] of [[customerReq,'/Account/Settings'], [ownerReq,'/DemoGallery'], [ownerReq,'/WebsiteContent'], [customerReq,'/Home/Gallery']]) assert.equal((await req(url)).status, 200, url);
  console.log('PASS Customer profile, Gallery and Website Content pages');
  console.log('Synthetic smoke storage retained:', data, root);
} finally { if (server.exitCode === null) await new Promise(resolve => { server.once('exit', resolve); server.kill(); }); }
