// Loopback-only staging-mode checks. No database or provider is contacted.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdir, mkdtemp, copyFile, readFile } from 'node:fs/promises';
import http from 'node:http';
import path from 'node:path';

const project = path.resolve('.'), parent = path.join(process.env.LOCALAPPDATA, 'Temp/opencode');
await mkdir(parent, { recursive: true });
const root = await mkdtemp(path.join(parent, 'public-staging-http-'));
await copyFile(path.join(project, 'Frontend/GlamByMariya.Web/appsettings.json'), path.join(root, 'appsettings.json'));
const bin = process.env.PUBLIC_ONLY_BUILD || path.join(parent, 'public-only-build');
const dll = path.join(bin, 'GlamByMariya.Web.dll');
const webroot = process.env.PUBLIC_ONLY_WEBROOT || path.join(project, 'Frontend/GlamByMariya.Web/wwwroot');
// Only OS/runtime essentials and the staging blueprint's explicit variables.
const env = Object.fromEntries(['PATH', 'SystemRoot', 'WINDIR', 'LOCALAPPDATA', 'TEMP', 'TMP', 'USERPROFILE', 'HOME'].filter(k => process.env[k]).map(k => [k, process.env[k]]));
Object.assign(env, { ASPNETCORE_ENVIRONMENT: 'Production', DOTNET_ENVIRONMENT: 'Production', PublicSite__InformationOnly: 'true', Supabase__Url: '' });
const server = spawn('dotnet', [dll, '--urls=http://127.0.0.1:0', `--contentRoot=${root}`,
  `--webroot=${webroot}`], { env, stdio: ['ignore', 'pipe', 'pipe'] });
let checks = 0;
const check = (value, label) => { assert.ok(value, label); console.log(`PASS ${++checks}: ${label}`); };
try {
  const port = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(Error('staging host startup timed out')), 30000);
    server.once('error', reject); server.once('exit', code => reject(Error(`staging host exited ${code}`)));
    server.stderr.resume(); server.stdout.on('data', chunk => { const m = String(chunk).match(/Now listening on: http:\/\/127\.0\.0\.1:(\d+)/); if (m) { clearTimeout(timer); resolve(Number(m[1])); } });
  });
  function request(method, pathname, headers = {}) {
    return new Promise((resolve, reject) => {
      const req = http.request({ hostname: '127.0.0.1', port, path: pathname, method,
        headers: { Host: 'audit.example.invalid', ...headers } }, res => {
        let body = ''; res.setEncoding('utf8'); res.on('data', data => body += data);
        res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, body }));
      });
      req.setTimeout(10000, () => req.destroy(Error('loopback request timeout'))); req.on('error', reject); req.end();
    });
  }
  const proxy = { 'X-Forwarded-Proto': 'https', 'X-Forwarded-For': '203.0.113.25' };
  const health = await request('GET', '/healthz', proxy);
  check(health.status === 200 && JSON.parse(health.body).status === 'ok', 'environment-only Production staging starts with repository config and no provider secrets');
  check(/^max-age=\d+/.test(health.headers['strict-transport-security'] || ''), 'trusted loopback proxy HTTPS produces HSTS');
  check(!('set-cookie' in health.headers), 'health endpoint issues no cookies');
  const plain = await request('GET', '/healthz');
  check(plain.status === 200 && !plain.headers['strict-transport-security'], 'plain HTTP is not misidentified as HTTPS');
  const chain = await request('GET', '/healthz', { 'X-Forwarded-Proto': 'https,http', 'X-Forwarded-For': '198.51.100.5,203.0.113.25' });
  check(!chain.headers['strict-transport-security'], 'only nearest forwarded protocol is applied');
  for (const [method, pathname, status] of [['GET', '/', 200], ['GET', '/Home/About', 200], ['GET', '/Home/Services', 200],
    ['GET', '/Home/Gallery', 200], ['GET', '/Home/Contact', 200], ['GET', '/favicon.svg', 200],
    ['GET', '/Account/Login', 404], ['GET', '/Bookings/Proof/00000000-0000-0000-0000-000000000001', 404],
    ['GET', '/Admin', 404], ['GET', '/Payments', 404], ['POST', '/Account/Login', 404], ['POST', '/Bookings/Create', 404],
    ['POST', '/Admin/Action', 404], ['POST', '/healthz', 404]]) {
    const r = await request(method, pathname, { ...proxy, Cookie: '.AspNetCore.Cookies=previous-session' });
    assert.equal(r.status, status, `${method} ${pathname}`);
    assert.ok(/^max-age=\d+/.test(r.headers['strict-transport-security'] || ''), `HSTS ${pathname}`);
    assert.equal(r.headers['x-frame-options'], 'DENY'); assert.equal(r.headers['x-content-type-options'], 'nosniff');
    assert.equal(r.headers['referrer-policy'], 'strict-origin-when-cross-origin');
    assert.ok(r.headers['content-security-policy-report-only']); assert.ok(!r.headers['content-security-policy']);
    assert.ok(!r.headers['set-cookie'], `${pathname} must not issue session cookies`);
    if (status === 404) assert.equal(r.body, '');
  }
  check(true, 'public, static, denied GET and POST responses retain security headers and issue no cookies');
  for (const asset of ['/favicon.svg', '/images/botanical.svg', '/css/site.css', '/css/availability.css', '/css/payment-qr.css',
    '/css/booking-refinement.css', '/css/catalog-images.css', '/css/branding.css', '/css/landing.css', '/css/workspace-redesign.css',
    '/css/customer-beauty.css', '/css/typography-system.css', '/css/public-refinement.css', '/js/site.js']) {
    const r = await request('GET', asset); assert.equal(r.status, 200, `public asset ${asset}`); assert.ok(r.body.length, `nonempty ${asset}`);
  }
  for (const asset of ['/css/account-settings.css', '/js/booking-quantity.js', '/js/availability-response.js']) {
    const r = await request('GET', asset); assert.equal(r.status, 404, `private asset ${asset}`); assert.equal(r.body, '');
  }
  check(true, 'all allowlisted public assets load and account/booking assets remain denied');
  const blueprint = await readFile(path.join(project, 'render.information-only.staging.yaml'), 'utf8');
  check(blueprint.includes('PublicSite__InformationOnly') && blueprint.includes('Dockerfile.information-only.staging') &&
    !/ConnectionStrings__|Database__|SupabaseStorage__|BrevoApiKey|CertificatePfx/i.test(blueprint), 'separate staging blueprint contains no database or provider credentials');
  const invalid = spawn('dotnet', [dll, '--urls=http://127.0.0.1:0', `--contentRoot=${root}`,
    `--webroot=${webroot}`],
    { env: { ...env, ForwardedHeaders__KnownProxies: 'not-an-ip' }, stdio: ['ignore', 'pipe', 'pipe'] });
  let invalidOutput = ''; invalid.stdout.on('data', chunk => invalidOutput += chunk); invalid.stderr.resume();
  const invalidCode = await new Promise((resolve, reject) => { invalid.once('error', reject); invalid.once('exit', resolve); });
  check(invalidCode !== 0 && !invalidOutput.includes('Now listening on:'), 'invalid trusted-proxy configuration refuses startup');
  console.log(`ALL ${checks} PUBLIC-ONLY STAGING HTTP CHECKS PASSED; root: ${root}`);
} finally { server.kill(); }
