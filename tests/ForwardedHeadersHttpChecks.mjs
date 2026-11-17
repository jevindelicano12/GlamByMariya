// Actual application HTTP checks; no browser or npm dependency required.
// Run after: dotnet build MakeupSystem.csproj -c Release
// Starts only owned loopback Demo servers, with temporary content roots and no
// Supabase, storage or email integration. Never accepts an external base URL.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdtemp, mkdir, rm } from 'node:fs/promises';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
const tempParent = process.env.LOCALAPPDATA ? path.join(process.env.LOCALAPPDATA, 'Temp', 'opencode') : os.tmpdir();
await mkdir(tempParent, { recursive: true });
let checks = 0;
function check(condition, message) { assert.ok(condition, message); checks++; console.log(`PASS: ${message}`); }

async function start(environment) {
  const contentRoot = await mkdtemp(path.join(tempParent, 'forwarded-headers-78g-'));
  const server = spawn('dotnet', [path.join(root, 'Frontend/GlamByMariya.Web/bin/Release/net10.0/GlamByMariya.Web.dll'),
    '--urls=http://127.0.0.1:0', `--contentRoot=${contentRoot}`, `--webroot=${path.join(root, 'Frontend/GlamByMariya.Web/wwwroot')}`,
    '--Database:Provider=Demo', '--Supabase:Url=', '--Supabase:AnonKey=', '--SupabaseStorage:ServiceKey=',
    '--B2:Bucket=', '--B2:Endpoint=', '--B2:KeyId=', '--B2:AppKey=', '--Email:BrevoApiKey=', '--Email:SmtpHost='
  ], { cwd: contentRoot, env: { ...process.env, ASPNETCORE_ENVIRONMENT: environment, DOTNET_ENVIRONMENT: environment,
    ASPNETCORE_FORWARDEDHEADERS_ENABLED: 'false', PORT: '' }, stdio: ['ignore', 'pipe', 'pipe'] });
  const stop = async () => {
    if (server.exitCode === null && !server.killed) { const exited = once(server, 'exit'); server.kill(); await exited; }
    // This is only the unique temporary directory created above, never repository data.
    await rm(contentRoot, { recursive: true, force: true });
  };
  try {
    const port = await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('Local Demo startup timed out.')), 30000);
      server.on('error', error => { clearTimeout(timer); reject(error); });
      server.on('exit', () => { clearTimeout(timer); reject(new Error('Local Demo exited during startup.')); });
      server.stdout.on('data', chunk => {
        const match = chunk.toString().match(/Now listening on: http:\/\/127\.0\.0\.1:(\d+)/);
        if (match) { clearTimeout(timer); resolve(Number(match[1])); }
      });
      server.stderr.resume(); // Never print raw startup/configuration details.
    });
    const get = (requestPath, headers = {}) => new Promise((resolve, reject) => {
      // Host is deliberately non-localhost to avoid HSTS's localhost exclusion.
      // The TCP connection ALWAYS targets this owned loopback server.
      const request = http.get({ hostname: '127.0.0.1', port, path: requestPath,
        headers: { Host: 'audit.example.invalid', ...headers } }, response => {
        let body = '';
        response.on('data', chunk => { body += chunk; });
        response.on('end', () => resolve({ status: response.statusCode, headers: response.headers, body }));
      });
      request.setTimeout(10000, () => request.destroy(new Error('Local HTTP request timed out.')));
      request.on('error', reject);
    });
    return { get, stop };
  } catch (error) { await stop(); throw error; }
}

for (const environment of ['Production', 'Development']) {
  const server = await start(environment);
  try {
    const secureHeaders = { 'X-Forwarded-Proto': 'https', 'X-Forwarded-For': '203.0.113.25' };
    const health = await server.get('/healthz', secureHeaders);
    const loginRedirect = await server.get('/Admin', secureHeaders);
    check(health.status === 200, `${environment}: trusted HTTPS proxy health request succeeds`);
    check(loginRedirect.status === 302 && loginRedirect.headers.location === 'https://audit.example.invalid/Account/Login?ReturnUrl=%2FAdmin',
      `${environment}: authentication redirect uses trusted HTTPS scheme`);
    console.log(`EVIDENCE: ${environment}, forwarded HTTPS, redirect scheme=https, HSTS=${health.headers['strict-transport-security'] || 'absent'}`);
    if (environment === 'Production') {
      check(/^max-age=\d+/.test(health.headers['strict-transport-security'] || ''), 'Production: trusted forwarded HTTPS emits HSTS');
      check(Boolean(loginRedirect.headers['strict-transport-security']), 'Production: HTTPS authentication redirect also carries HSTS');
    } else {
      check(!health.headers['strict-transport-security'] && !loginRedirect.headers['strict-transport-security'], 'Development: no HSTS even for forwarded HTTPS');
    }
    // Follow a logical HTTPS redirect through the simulated proxy, not the DNS
    // hostname. No request ever leaves loopback.
    const destination = new URL(loginRedirect.headers.location);
    const login = await server.get(destination.pathname + destination.search, secureHeaders);
    check(login.status === 200 && !login.headers.location, `${environment}: one login redirect terminates without a loop`);
    check(Boolean(login.headers['content-security-policy-report-only']) && !login.headers['content-security-policy'], `${environment}: CSP remains Report-Only`);
    check(login.headers['x-frame-options'] === 'DENY' && login.headers['x-content-type-options'] === 'nosniff', `${environment}: security headers preserved`);
    check(login.body.includes('name="__RequestVerificationToken"'), `${environment}: antiforgery form rendering preserved`);
    const plain = await server.get('/healthz');
    check(plain.status === 200 && !plain.headers['strict-transport-security'], `${environment}: plain HTTP does not acquire HSTS`);
    const plainRedirect = await server.get('/Admin');
    check(plainRedirect.status === 302 && plainRedirect.headers.location.startsWith('http://audit.example.invalid/'), `${environment}: absent forwarded scheme is not invented`);
    const spoofHost = await server.get('/Admin', { ...secureHeaders, 'X-Forwarded-Host': 'attacker.example.invalid' });
    check(spoofHost.headers.location === loginRedirect.headers.location, `${environment}: unsupported forwarded Host cannot replace redirect origin`);
    const localhost = await server.get('/healthz', { ...secureHeaders, Host: 'localhost' });
    check(!localhost.headers['strict-transport-security'], `${environment}: default localhost HSTS exclusion preserved`);
    if (environment === 'Production') {
      for (const [requestPath, expectedStatus] of [['/favicon.svg', 200], ['/missing-78g', 404], ['/Home/Error', 500]]) {
        const response = await server.get(requestPath, secureHeaders);
        check(response.status === expectedStatus && Boolean(response.headers['strict-transport-security']), `Production: ${requestPath} retains status and HTTPS HSTS`);
      }
    }
  } finally { await server.stop(); }
}
console.log(`ALL FORWARDED HEADERS HTTP CHECKS PASSED (${checks} checks)`);
