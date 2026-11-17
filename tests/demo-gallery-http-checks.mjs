// Uses only synthetic data and retains its unique local folders; no cleanup deletes files.
import assert from 'node:assert/strict';
import { admin as adminAccount, owner as ownerAccount, customer as customerAccount } from './presentation-test-accounts.mjs';
import { spawn } from 'node:child_process';
import { mkdir } from 'node:fs/promises';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import http from 'node:http';
import { deflateSync } from 'node:zlib';

const repo = path.resolve('.');
const project = path.resolve(process.argv[2] || 'Frontend/GlamByMariya.Web/bin/Release/net10.0');
const base = 'http://127.0.0.1:5198';
const root = path.join(process.env.LOCALAPPDATA, 'Temp', 'opencode', `gallery-test-${randomUUID()}`);
const storage = process.argv[3] || path.join(process.env.LOCALAPPDATA, 'GlamByMariya', `synthetic-${randomUUID()}`);
await mkdir(root, { recursive: true });
console.log('Retained synthetic test storage:', storage, 'content root:', root);
let server;
async function start() {
  server = spawn('dotnet', [path.join(project, 'GlamByMariya.Web.dll'), `--urls=${base}`, `--contentRoot=${root}`,
    `--webroot=${path.join(repo, 'Frontend/GlamByMariya.Web/wwwroot')}`, '--Database:Provider=Demo', '--Authentication:DurableSessionEnforcement=false',
    '--DemoGallery:Enabled=true', `--DemoGallery:Root=${storage}`, '--Supabase:Url=', '--Supabase:AnonKey=',
    '--SupabaseStorage:ServiceKey=', '--FileStorage:Provider=Local', '--B2:Bucket=', '--B2:Endpoint=', '--B2:KeyId=',
    '--B2:AppKey=', '--Email:BrevoApiKey=', '--Email:SmtpHost='],
    { env: { ...process.env, PORT: '', ASPNETCORE_ENVIRONMENT: 'Development', DOTNET_ENVIRONMENT: 'Development' }, stdio: ['ignore', 'pipe', 'pipe'] });
  await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Startup timed out')), 30000);
    let logs = '';
    server.stdout.on('data', b => { logs += b; if (logs.includes(`Now listening on: ${base}`)) { clearTimeout(timer); resolve(); } });
    server.stderr.on('data', b => { logs += b; });
    server.once('exit', code => { clearTimeout(timer); reject(new Error(`Server exited ${code}: ${logs}`)); });
  });
}
async function stop() {
  if (server && server.exitCode === null) await new Promise(resolve => { server.once('exit', resolve); server.kill(); });
}
async function request(jar, url, form, headers = {}) {
  const response = await fetch(base + url, { redirect: 'manual', headers: { cookie: [...jar].map(([k,v]) => `${k}=${v}`).join('; '), ...headers }, ...(form ? { method:'POST', body: form } : {}) });
  for (const c of response.headers.getSetCookie()) { const pair = c.split(';')[0], n = pair.indexOf('='); jar.set(pair.slice(0,n), pair.slice(n+1)); }
  return { status:response.status, location:response.headers.get('location'), type:response.headers.get('content-type'), body:await response.text() };
}
function token(html) { const t = html.match(/name="__RequestVerificationToken"[^>]*value="([^"]+)"/)?.[1]; assert.ok(t, 'antiforgery token'); return t; }
async function login(email, password) {
  const jar = new Map(), page = await request(jar, '/Account/Login');
  const r = await request(jar, '/Account/Login', new URLSearchParams({ email, password, __RequestVerificationToken:token(page.body) }));
  assert.equal(r.status, 302); return jar;
}
function upload(csrf, bytes, name = 'synthetic.png') {
  const form = new FormData(); form.set('__RequestVerificationToken',csrf); form.set('altText','Synthetic test photo');
  form.set('caption','Synthetic only'); form.set('photo',new Blob([bytes],{type:'image/png'}),name); return form;
}
try {
  await start();
  for (const url of ['/', '/Home/About', '/Home/Services', '/Home/Gallery', '/Home/Contact']) assert.equal((await request(new Map(),url)).status,200,url);
  const owner = await login(ownerAccount.email,ownerAccount.password);
  const admin = await login(adminAccount.email,adminAccount.password);
  const customer = await login(customerAccount.email,customerAccount.password);
  assert.equal((await request(owner,'/Admin/Users')).status,200);
  assert.equal((await request(admin,'/Admin')).status,200);
  assert.equal((await request(customer,'/Bookings')).status,200);
  const foreignHost = await new Promise((resolve,reject) => {
    const r = http.get(base + '/DemoGallery', { headers: { Host:'example.test:5198' } }, response => { response.resume(); resolve(response.statusCode); });
    r.on('error',reject);
  });
  assert.equal(foreignHost,403);
  const page = await request(owner,'/DemoGallery'); assert.equal(page.status,200);
  const csrf = token(page.body);
  function chunk(type, data) {
    const name = Buffer.from(type), bytes = Buffer.concat([name,data]); let crc = 0xffffffff;
    for (const byte of bytes) { crc ^= byte; for (let i=0;i<8;i++) crc = (crc >>> 1) ^ (0xedb88320 & -(crc & 1)); }
    const length = Buffer.alloc(4), sum = Buffer.alloc(4); length.writeUInt32BE(data.length); sum.writeUInt32BE((crc ^ 0xffffffff) >>> 0);
    return Buffer.concat([length,bytes,sum]);
  }
  const header = Buffer.alloc(13); header.writeUInt32BE(1,0); header.writeUInt32BE(1,4); header[8]=8; header[9]=2;
  const png = Buffer.concat([Buffer.from('89504e470d0a1a0a','hex'),chunk('IHDR',header),chunk('IDAT',deflateSync(Buffer.from([0,200,100,120]))),chunk('IEND',Buffer.alloc(0))]);
  for (const jar of [admin,customer,new Map()]) {
    const denied = await request(jar,'/DemoGallery/Upload',upload(csrf,png));
    assert.equal(denied.status,302); assert.match(denied.location,/Account\/(AccessDenied|Login)/);
  }
  assert.ok(!(await request(owner,'/DemoGallery')).body.includes('Synthetic test photo'), 'unauthorized upload stored');
  await request(owner,'/DemoGallery/Upload',upload(csrf,Buffer.from('<svg/>'),'bad.png'));
  await request(owner,'/DemoGallery/Upload',upload(csrf,Buffer.alloc(5_000_001),'large.png'));
  assert.ok(!(await request(owner,'/DemoGallery')).body.includes('Synthetic test photo'), 'invalid upload stored');
  await request(owner,'/DemoGallery/Upload',upload(csrf,png,'../../unsafe.png'));
  const afterUpload = await request(owner,'/DemoGallery');
  assert.ok(afterUpload.body.includes('Synthetic test photo'), 'upload failed: ' + afterUpload.body.match(/<div class="alert error"[^>]*>[^<]*/)?.[0]);
  const gallery = await request(new Map(),'/Home/Gallery');
  assert.ok(gallery.body.includes('Synthetic test photo'), 'uploaded photo missing');
  const src = gallery.body.match(/src="(\/WebsiteImages\/Image\/[a-f0-9-]+)"/)?.[1]; assert.ok(src, 'gallery URL');
  assert.equal((await request(new Map(),src)).type,'image/png');
  const id = src.split('/').pop();
  const deniedHide = await request(admin,'/DemoGallery/Visibility',new URLSearchParams({id,visible:'false',__RequestVerificationToken:csrf}));
  assert.equal(deniedHide.status,302); assert.match(deniedHide.location,/Account\/AccessDenied/);
  assert.equal((await request(new Map(),src)).status,200,'unauthorized hide');
  assert.equal((await request(owner,'/DemoGallery/Visibility',new URLSearchParams({id,visible:'false',__RequestVerificationToken:csrf}))).status,302);
  assert.equal((await request(new Map(),src)).status,404,'hidden photo public');
  assert.equal((await request(owner,'/DemoGallery/Visibility',new URLSearchParams({id,visible:'true',__RequestVerificationToken:csrf}))).status,302);
  await stop(); await start();
  assert.ok((await request(new Map(),'/Home/Gallery')).body.includes('Synthetic test photo'),'photo missing after restart');
  assert.equal((await request(new Map(),src)).type,'image/png');
  console.log('PASS public pages, three roles, loopback Host guard, authorized upload, invalid and unauthorized rejection, visibility, image and restart persistence');
} finally { await stop(); }
