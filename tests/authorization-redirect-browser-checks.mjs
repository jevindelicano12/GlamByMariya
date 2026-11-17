// Build Release first. Set PLAYWRIGHT_MODULE to a temporary playwright-core
// package directory and optionally BROWSER_EXECUTABLE. No project npm install.
// Owns a loopback-only Demo server and temporary content root; never targets an
// external application URL or enables a production integration.
import assert from 'node:assert/strict';
import { admin, owner, customer } from './presentation-test-accounts.mjs';
import { createRequire } from 'node:module';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdtemp, mkdir, rm } from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright-core');
const root = fileURLToPath(new URL('../', import.meta.url));
const parent = process.env.LOCALAPPDATA ? path.join(process.env.LOCALAPPDATA, 'Temp', 'opencode') : os.tmpdir();
await mkdir(parent, { recursive: true });
const contentRoot = await mkdtemp(path.join(parent, 'authorization-78h-'));
const server = spawn('dotnet', [path.join(root, 'Frontend/GlamByMariya.Web/bin/Release/net10.0/GlamByMariya.Web.dll'),
  '--urls=http://127.0.0.1:0', `--contentRoot=${contentRoot}`, `--webroot=${path.join(root, 'Frontend/GlamByMariya.Web/wwwroot')}`,
  '--Database:Provider=Demo', '--Supabase:Url=', '--Supabase:AnonKey=', '--SupabaseStorage:ServiceKey=',
  '--B2:Bucket=', '--B2:Endpoint=', '--B2:KeyId=', '--B2:AppKey=', '--Email:BrevoApiKey=', '--Email:SmtpHost='
], { cwd: contentRoot, env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', DOTNET_ENVIRONMENT: 'Development',
  ASPNETCORE_FORWARDEDHEADERS_ENABLED: 'false', PORT: '' }, stdio: ['ignore', 'pipe', 'pipe'] });
let browser;
let checks = 0;
const check = (condition, message) => { assert.ok(condition, message); checks++; console.log(`PASS: ${message}`); };
const accounts = {
  Customer: [customer.email, customer.password, '/Bookings'],
  Admin: [admin.email, admin.password, '/Admin'],
  SuperAdmin: [owner.email, owner.password, '/Admin']
};
try {
  const base = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Isolated Demo startup timed out.')), 30000);
    server.on('error', error => { clearTimeout(timer); reject(error); });
    server.on('exit', () => { clearTimeout(timer); reject(new Error('Isolated Demo exited during startup.')); });
    server.stdout.on('data', chunk => {
      const match = chunk.toString().match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/);
      if (match) { clearTimeout(timer); resolve(match[1]); }
    });
    server.stderr.resume();
  });
  browser = await chromium.launch({ headless: true,
    ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : { channel: 'msedge' }) });
  const contexts = {};
  const errors = [];
  async function context() {
    const ctx = await browser.newContext();
    await ctx.route('**/*', route => {
      const url = new URL(route.request().url());
      return url.origin === base ? route.continue() : route.abort();
    });
    return ctx;
  }
  async function token(ctx, url = '/Account/Login') {
    const response = await ctx.request.get(base + url);
    const match = (await response.text()).match(/name="__RequestVerificationToken"[^>]*value="([^"]+)"/);
    assert.ok(match, 'Expected antiforgery token in local form.');
    return match[1];
  }
  async function signIn(ctx, role, returnUrl = '') {
    const [email, password] = accounts[role];
    return ctx.request.post(base + '/Account/Login', { maxRedirects: 0,
      form: { email, password, returnUrl, __RequestVerificationToken: await token(ctx) } });
  }
  async function follow(ctx, response) {
    const seen = new Set();
    let redirects = 0;
    while (response.status() >= 300 && response.status() < 400 && response.headers().location) {
      const next = new URL(response.headers().location, base);
      assert.equal(next.origin, base, 'Every redirect must stay on the owned Demo server.');
      if (seen.has(next.href) || redirects >= 4) return { response, loop: true, redirects };
      seen.add(next.href); redirects++;
      response = await ctx.request.get(next.href, { maxRedirects: 0 });
    }
    return { response, loop: false, redirects };
  }
  const anon = await context();
  for (const role of ['Admin', 'Customer', 'SuperAdmin']) {
    contexts[role] = await context();
    const login = await signIn(contexts[role], role);
    check(login.status() === 302 && login.headers().location === accounts[role][2], `${role}: normal login destination preserved`);
  }

  // This assertion reproduces the original loop before the production fix.
  const denied = await follow(contexts.Admin, await contexts.Admin.request.get(base + '/Admin/Users', { maxRedirects: 0 }));
  console.log(`EVIDENCE: Admin /Admin/Users: loop=${denied.loop}, redirects=${denied.redirects}, finalStatus=${denied.response.status()}`);
  check(!denied.loop && denied.response.status() === 403, 'Admin: restricted page terminates with HTTP 403 instead of looping');

  const challenge = await anon.request.get(base + '/Admin/Users', { maxRedirects: 0 });
  const challengeUrl = new URL(challenge.headers().location, base);
  check(challenge.status() === 302 && challengeUrl.pathname === '/Account/Login' && challengeUrl.searchParams.get('ReturnUrl') === '/Admin/Users', 'Anonymous: restricted page still challenges to Login with local return URL');
  check((await follow(anon, challenge)).response.status() === 200, 'Anonymous: login challenge terminates without a loop');
  for (const role of ['Customer', 'Admin']) {
    const ctx = contexts[role];
    const result = await follow(ctx, await ctx.request.get(base + '/Admin/Users', { maxRedirects: 0 }));
    const html = await result.response.text();
    check(!result.loop && result.response.status() === 403, `${role}: Users access denied with stable HTTP 403`);
    check(/<h1>Access denied<\/h1>/.test(html) && !html.includes('Invite an administrator'), `${role}: friendly error page, no administrator management data`);
    check((result.response.headers()['cache-control'] || '').includes('no-store'), `${role}: denial response is not cached`);
    check((result.response.headers()['content-type'] || '').includes('text/html')
      && result.response.headers()['x-frame-options'] === 'DENY'
      && result.response.headers()['x-content-type-options'] === 'nosniff', `${role}: safe HTML denial response retains security headers`);
    check(Boolean(result.response.headers()['content-security-policy-report-only']) && !result.response.headers()['content-security-policy'], `${role}: denied page keeps Report-Only CSP`);
    for (const action of ['Invite', 'RevokeInvite']) {
      const blocked = await follow(ctx, await ctx.request.post(base + '/Admin/' + action, { maxRedirects: 0,
        form: { __RequestVerificationToken: await token(ctx, accounts[role][2]), email: 'unused@example.test', id: '00000000-0000-0000-0000-000000000000' } }));
      check(!blocked.loop && blocked.response.status() === 403, `${role}: SuperAdmin-only ${action} POST remains forbidden`);
    }
  }
  const owner = await contexts.SuperAdmin.request.get(base + '/Admin/Users', { maxRedirects: 0 });
  check(owner.status() === 200 && (await owner.text()).includes('Invite an administrator'), 'SuperAdmin: administrator management still accessible');
  const directDenied = await anon.request.get(base + '/Account/AccessDenied?returnUrl=https%3A%2F%2Fattacker.example.invalid', { maxRedirects: 0 });
  check(directDenied.status() === 403 && !(await directDenied.text()).includes('attacker.example.invalid'), 'Direct denial page: safe HTTP 403, no untrusted return URL reflected');

  const unsafe = ['https://attacker.example.invalid/', 'http://attacker.example.invalid/', '//attacker.example.invalid/',
    '/\\attacker.example.invalid', '\\attacker.example.invalid', '~//attacker.example.invalid', 'javascript:alert(1)',
    '/%2f%2fattacker.example.invalid', '/%5cattacker.example.invalid', '/%00', '/%0d%0aLocation:evil',
    '/Account/Login?returnUrl=/Admin/Users'];
  for (const role of ['Customer', 'Admin', 'SuperAdmin']) {
    const ctx = contexts[role], dashboard = accounts[role][2];
    for (const value of unsafe) {
      const result = await ctx.request.get(base + '/Account/Login?returnUrl=' + encodeURIComponent(value), { maxRedirects: 0 });
      check(result.status() === 302 && result.headers().location === dashboard, `${role}: unsafe return URL rejected (${unsafe.indexOf(value) + 1})`);
    }
  }
  for (const value of ['/Admin/Users', '/admin/users?sort=name', '/Admin/%55sers', '/x/../Admin/Users', '/Admin/Reports/../Users']) {
    const result = await contexts.Admin.request.get(base + '/Account/Login?returnUrl=' + encodeURIComponent(value), { maxRedirects: 0 });
    check(result.status() === 302 && result.headers().location === '/Admin', 'Admin: forbidden return destination falls back to dashboard');
  }
  for (const [role, value, expected] of [
    ['Customer', '/Bookings?status=Confirmed', '/Bookings?status=Confirmed'],
    ['Customer', '/Admin/Reports', '/Bookings'], ['Admin', '/Bookings', '/Admin'],
    ['Admin', '/Admin/Reports?view=payments', '/Admin/Reports?view=payments'],
    ['SuperAdmin', '/Admin/Users', '/Admin/Users'], ['SuperAdmin', '/Bookings', '/Admin'],
    ['Customer', '/Home/Index', '/Home/Index']
  ]) {
    const result = await contexts[role].request.get(base + '/Account/Login?returnUrl=' + encodeURIComponent(value), { maxRedirects: 0 });
    check(result.status() === 302 && result.headers().location === expected, `${role}: permitted/cross-role return behavior preserved`);
  }
  const externalPost = await signIn(contexts.Admin, 'Admin', 'https://attacker.example.invalid/');
  check(externalPost.status() === 302 && externalPost.headers().location === '/Admin', 'Login POST rejects external return URL');
  const normalPost = await signIn(contexts.Admin, 'Admin', '/Admin/Reports?view=payments');
  check(normalPost.status() === 302 && normalPost.headers().location === '/Admin/Reports?view=payments', 'Login POST preserves permitted local return destination and query');

  const anonymousPage = await anon.newPage();
  const anonymousResponse = await anonymousPage.goto(base + '/Admin/Users');
  check(anonymousResponse.status() === 200 && new URL(anonymousPage.url()).pathname === '/Account/Login', 'Anonymous: browser reaches Login without a redirect loop');
  check(await anonymousPage.locator('#auth-form').isVisible(), 'Anonymous: browser renders the normal login form');
  await anonymousPage.close();

  for (const role of ['Customer', 'Admin', 'SuperAdmin']) {
    const ctx = await context(), page = await ctx.newPage();
    page.on('pageerror', error => errors.push(error.message));
    const [email, password, dashboard] = accounts[role];
    await page.goto(base + '/Account/Login?returnUrl=%2FAdmin%2FUsers');
    await page.fill('[name="email"]', email); await page.fill('[name="password"]', password);
    await Promise.all([page.waitForURL(base + (role === 'SuperAdmin' ? '/Admin/Users' : dashboard)), page.locator('#auth-form button.button').click()]);
    check(true, `${role}: browser login uses role-safe return destination`);
    const response = await page.goto(base + '/Admin/Users');
    check(response.status() === (role === 'SuperAdmin' ? 200 : 403), `${role}: browser Users navigation has correct final status, no redirect loop`);
    if (role !== 'SuperAdmin') {
      check(await page.getByRole('heading', { name: 'Access denied', exact: true }).isVisible(), `${role}: browser renders readable denied page`);
      await page.getByRole('link', { name: 'Return to dashboard', exact: true }).click();
      check(new URL(page.url()).pathname === dashboard, `${role}: denied-page recovery link is safe`);
    }
    await page.goto(base + '/Account/Settings');
    await page.locator('form[action="/Account/Logout"] button').last().click(); await page.waitForURL(/\/Account\/Login/);
    check(!(await ctx.cookies()).some(cookie => cookie.name === '.AspNetCore.Cookies'), `${role}: logout removes authentication cookie`);
    const afterLogout = await ctx.request.get(base + '/Admin/Users', { maxRedirects: 0 });
    check(afterLogout.status() === 302 && new URL(afterLogout.headers().location, base).pathname === '/Account/Login', `${role}: logout restores anonymous Login challenge`);
    await ctx.close();
  }
  const logoutNoToken = await contexts.Admin.request.post(base + '/Account/Logout', { maxRedirects: 0 });
  check(logoutNoToken.status() === 400, 'Logout without antiforgery token remains rejected');
  check(errors.length === 0, 'No uncaught browser JavaScript errors');
  console.log(`ALL AUTHORIZATION REDIRECT CHECKS PASSED (${checks} checks)`);
} finally {
  await browser?.close();
  if (server.exitCode === null && !server.killed) { const exited = once(server, 'exit'); server.kill(); await exited; }
  await rm(contentRoot, { recursive: true, force: true });
}
