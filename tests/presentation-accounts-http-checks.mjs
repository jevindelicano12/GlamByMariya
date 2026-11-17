// Isolated presentation identities; generated passwords never leave this process's environment.
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { mkdir, writeFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import path from 'node:path';
import { admin, owner, customer } from './presentation-test-accounts.mjs';

const app = path.resolve(process.argv[2] || 'Frontend/GlamByMariya.Web/bin/Release/net10.0');
const web = path.resolve(process.argv[3] || 'Frontend/GlamByMariya.Web/wwwroot');
const root = path.join(process.env.LOCALAPPDATA, 'Temp', 'opencode', 'presentation-test-' + randomUUID());
const data = path.join(process.env.LOCALAPPDATA, 'GlamByMariya', 'presentation-test-' + randomUUID());
const avatars = path.join(data, 'avatars');
await mkdir(root, { recursive: true });
await mkdir(avatars, { recursive: true });
// Recreate the existing role-based pointer format in isolated storage.
const bytes = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6cWQAAAAASUVORK5CYII=', 'base64');
for (const [i, key] of ['admin', 'owner', 'customer'].entries()) {
  const filename = String(i + 1).padStart(32, '0') + '.png';
  await writeFile(path.join(avatars, filename), bytes);
  await writeFile(path.join(avatars, key + '.txt'), filename);
}
for (const [value, message] of [['short', /8[-–]128/], ['abcdefgh', /letter.*number/i], [admin.password, /different local password/i]]) {
  const rejected = spawnSync('dotnet', [path.join(app, 'GlamByMariya.Web.dll'), '--urls=http://127.0.0.1:0', `--contentRoot=${root}`, `--webroot=${web}`,
    '--Database:Provider=Demo', '--Supabase:Url=', '--Supabase:AnonKey=', '--SupabaseStorage:ServiceKey=', '--FileStorage:Provider=Local', '--B2:Bucket=', '--B2:Endpoint=', '--B2:KeyId=', '--B2:AppKey=', '--Email:BrevoApiKey=', '--Email:SmtpHost='],
    { env: { ...process.env, GLAM_DEMO_CUSTOMER_PASSWORD: value, PORT: '', ASPNETCORE_ENVIRONMENT: 'Development', DOTNET_ENVIRONMENT: 'Development' }, encoding: 'utf8', timeout: 30000 });
  assert.notEqual(rejected.status, 0);
  assert.match(rejected.stderr, message);
}
console.log('PASS invalid and duplicate configured passwords fail closed');
const server = spawn('dotnet', [path.join(app, 'GlamByMariya.Web.dll'), '--urls=http://127.0.0.1:0', `--contentRoot=${root}`, `--webroot=${web}`,
  '--Database:Provider=Demo', '--Authentication:DurableSessionEnforcement=false', '--DemoGallery:Enabled=true',
  `--DemoGallery:Root=${path.join(data, 'gallery')}`, '--DemoAvatar:Enabled=true', `--DemoAvatar:Root=${avatars}`,
  '--Supabase:Url=', '--Supabase:AnonKey=', '--SupabaseStorage:ServiceKey=', '--FileStorage:Provider=Local',
  '--B2:Bucket=', '--B2:Endpoint=', '--B2:KeyId=', '--B2:AppKey=', '--Email:BrevoApiKey=', '--Email:SmtpHost='],
  { env: { ...process.env, PORT: '', ASPNETCORE_ENVIRONMENT: 'Development', DOTNET_ENVIRONMENT: 'Development' }, stdio: ['ignore', 'pipe', 'pipe'] });
try {
  const base = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(Error('Isolated startup timed out')), 30000);
    server.stdout.on('data', b => { const match = String(b).match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/); if (match) { clearTimeout(timer); resolve(match[1]); } });
    server.stderr.resume();
    server.once('exit', code => { clearTimeout(timer); reject(Error('Isolated startup exited: ' + code)); });
  });
  async function req(jar, url, form) {
    const r = await fetch(base + url, { redirect: 'manual', headers: { cookie: [...jar].map(([k, v]) => `${k}=${v}`).join('; ') }, ...(form ? { method: 'POST', body: new URLSearchParams(form) } : {}) });
    for (const cookie of r.headers.getSetCookie()) { const pair = cookie.split(';')[0], i = pair.indexOf('='); jar.set(pair.slice(0, i), pair.slice(i + 1)); }
    return { status: r.status, location: r.headers.get('location'), type: r.headers.get('content-type'), body: Buffer.from(await r.arrayBuffer()) };
  }
  const token = body => { const t = body.toString().match(/name="__RequestVerificationToken"[^>]*value="([^"]+)"/)?.[1]; assert.ok(t); return t; };
  async function login(account, password = account.password) {
    const jar = new Map(), form = await req(jar, '/Account/Login');
    const result = await req(jar, '/Account/Login', { email: account.email, password, __RequestVerificationToken: token(form.body) });
    return { jar, result };
  }
  for (const [account, name, target] of [[customer, 'Jevin', '/Bookings'], [admin, 'Jhames', '/Admin'], [owner, 'Maria', '/Admin']]) {
    const { jar, result } = await login(account);
    assert.equal(result.status, 302); assert.equal(result.location, target);
    const settings = await req(jar, '/Account/Settings'); assert.match(settings.body.toString(), new RegExp(`value="${name}"`));
    const dashboard = await req(jar, target); assert.equal(dashboard.status, 200);
    if (name === 'Jevin') assert.doesNotMatch(dashboard.body.toString(), /Local testing information/);
    else assert.match(dashboard.body.toString(), /Local testing information[\s\S]*Payment verification is simulated[\s\S]*reset when the application stops|Local testing information[\s\S]*reset when the application stops[\s\S]*Payment verification is simulated/);
    const avatar = await req(jar, '/Account/DemoAvatar'); assert.equal(avatar.type, 'image/png'); assert.deepEqual(avatar.body, bytes);
    console.log('PASS', name, 'login, role dashboard, display name, existing avatar pointer');
  }
  const invalid = await login(customer, customer.password + 'wrong');
  assert.notEqual(invalid.result.location, '/Bookings');
  assert.notEqual((await req(invalid.jar, '/Bookings')).status, 200);
  console.log('PASS invalid password rejected');
  console.log('Retained isolated test storage:', data);
} finally {
  if (server.exitCode === null) await new Promise(resolve => { server.once('exit', resolve); server.kill(); });
}
