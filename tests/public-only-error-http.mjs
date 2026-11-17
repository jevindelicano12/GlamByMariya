// Test-only hosting startup injects faults; no diagnostic endpoint exists in the app.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdir, mkdtemp } from 'node:fs/promises';
import http from 'node:http';
import path from 'node:path';

const project = path.resolve('.'), parent = path.join(process.env.LOCALAPPDATA, 'Temp/opencode');
await mkdir(parent, { recursive: true });
const root = await mkdtemp(path.join(parent, 'public-error-check-'));
const bin = process.env.PUBLIC_ONLY_ERROR_BUILD || path.join(parent, 'public-only-error-build');
const env = Object.fromEntries(['PATH', 'SystemRoot', 'WINDIR', 'LOCALAPPDATA', 'TEMP', 'TMP', 'USERPROFILE', 'HOME'].filter(k => process.env[k]).map(k => [k, process.env[k]]));
Object.assign(env, { ASPNETCORE_ENVIRONMENT: 'Production', DOTNET_ENVIRONMENT: 'Production', PublicSite__InformationOnly: 'true',
  DOTNET_STARTUP_HOOKS: path.join(bin, 'PublicOnlyErrorFixtures.dll') });
const server = spawn('dotnet', [path.join(bin, 'GlamByMariya.Web.dll'), '--urls=http://127.0.0.1:0', `--contentRoot=${root}`,
  `--webroot=${path.join(project, 'Frontend/GlamByMariya.Web/wwwroot')}`], { env, stdio: ['ignore', 'pipe', 'pipe'] });
try {
  const port = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(Error('error fixture startup timed out')), 30000);
    server.once('error', reject); server.once('exit', code => reject(Error(`error fixture exited ${code}`)));
    server.stderr.resume(); server.stdout.on('data', chunk => { const m = String(chunk).match(/Now listening on: http:\/\/127\.0\.0\.1:(\d+)/); if (m) { clearTimeout(timer); resolve(Number(m[1])); } });
  });
  const get = (url, test) => new Promise((resolve, reject) => {
    const req = http.get({ hostname: '127.0.0.1', port, path: url, headers: { Host: 'audit.example.invalid', 'X-Forwarded-Proto': 'https', ...(test ? { 'X-Public-Error-Test': test } : {}) } }, res => {
      let body = ''; res.setEncoding('utf8'); res.on('data', data => body += data); res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, body }));
      res.on('error', reject);
    });
    req.setTimeout(10000, () => req.destroy(Error('request timeout'))); req.on('error', reject);
  });
  const regular = await get('/Home/About', 'normal');
  assert.equal(regular.status, 500); assert.equal(regular.body, 'An unexpected error occurred. Please try again.');
  assert.equal(regular.headers['x-frame-options'], 'DENY'); assert.equal(regular.headers['x-content-type-options'], 'nosniff');
  assert.ok(/^max-age=\d+/.test(regular.headers['strict-transport-security'] || ''));
  assert.ok(!/PRIVATE-EXCEPTION-CANARY|stack|InvalidOperationException/i.test(regular.body));
  console.log('PASS: injected application error returns generic 500 with security headers');
  const fail = await get('/Home/About', 'handler-failure').catch(error => ({ connectionError: error.code }));
  assert.ok(fail.connectionError || fail.status === 500, 'handler write failure must not succeed');
  assert.ok(!/PRIVATE-EXCEPTION-CANARY|SYNTHETIC-WRITE-FAILURE|InvalidOperationException|stack/i.test(fail.body || ''));
  console.log(`PASS: injected handler write failure fails closed without diagnostic body (${fail.connectionError || fail.status})`);
  for (const url of ['/Home/Error', '/Admin', '/Bookings', '/Account/Login']) {
    const r = await get(url, 'normal'); assert.equal(r.status, 404); assert.equal(r.body, '');
  }
  assert.equal((await get('/healthz')).status, 200);
  console.log('PASS: direct error and private routes remain 404; health remains available');
} finally { server.kill(); }
