// Local Demo-mode checks only. No production configuration or external services.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdir, mkdtemp } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
import { admin, owner, customer } from './presentation-test-accounts.mjs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
const temp = path.join(process.env.LOCALAPPDATA, 'Temp', 'opencode');
await mkdir(temp, { recursive: true });
const content = await mkdtemp(path.join(temp, 'makeup-guest-checks-'));
const env = { PATH: process.env.PATH, SystemRoot: process.env.SystemRoot, USERPROFILE: content, HOME: content,
  TEMP: content, TMP: content, APPDATA: content, LOCALAPPDATA: content, DOTNET_CLI_HOME: content,
  DOTNET_CLI_TELEMETRY_OPTOUT: '1', ASPNETCORE_ENVIRONMENT: 'Development', DOTNET_ENVIRONMENT: 'Development',
  ASPNETCORE_FORWARDEDHEADERS_ENABLED: 'false', PORT: '', DemoAccounts__Presentation: 'true',
  GLAM_DEMO_CUSTOMER_PASSWORD: customer.password, GLAM_DEMO_ADMIN_PASSWORD: admin.password, GLAM_DEMO_OWNER_PASSWORD: owner.password };
const app = spawn('dotnet', [path.join(process.env.UI_CHECK_BUILD || path.join(root, 'Frontend/GlamByMariya.Web/bin/Release/net10.0'), 'GlamByMariya.Web.dll'),
  '--urls=http://127.0.0.1:0', `--contentRoot=${content}`, `--webroot=${path.join(root, 'Frontend/GlamByMariya.Web/wwwroot')}`,
  '--Database:Provider=Demo', '--Supabase:Url=', '--Supabase:AnonKey=', '--SupabaseStorage:ServiceKey=',
  '--B2:Bucket=', '--B2:Endpoint=', '--B2:KeyId=', '--B2:AppKey=', '--Email:BrevoApiKey=', '--Email:SmtpHost=' ],
  { cwd: content, env, stdio: ['ignore', 'pipe', 'pipe'] });
let checks = 0;
function check(value, label) { assert.ok(value, label); checks++; console.log(`PASS: ${label}`); }
try {
  const port = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Demo startup timeout')), 30000);
    app.on('exit', () => { clearTimeout(timer); reject(new Error('Demo app exited')); });
    app.on('error', reject);
    app.stdout.on('data', chunk => { const match = String(chunk).match(/Now listening on: http:\/\/127\.0\.0\.1:(\d+)/); if (match) { clearTimeout(timer); resolve(match[1]); } });
    app.stderr.resume();
  });
  const base = `http://127.0.0.1:${port}`;
  function session(ip) {
    const cookies = new Map();
    return async (route, form) => {
      const headers = { 'X-Forwarded-For': ip };
      if (cookies.size) headers.Cookie = [...cookies].map(([k, v]) => `${k}=${v}`).join('; ');
      if (form) headers['Content-Type'] = 'application/x-www-form-urlencoded';
      const response = await fetch(base + route, { method: form ? 'POST' : 'GET', headers,
        body: form ? new URLSearchParams(form) : undefined, redirect: 'manual', signal: AbortSignal.timeout(10000) });
      for (const item of response.headers.getSetCookie()) { const pair = item.split(';')[0], i = pair.indexOf('='); cookies.set(pair.slice(0, i), pair.slice(i + 1)); }
      return { status: response.status, location: response.headers.get('location'), body: await response.text() };
    };
  }
  const token = body => { const match = body.match(/name="__RequestVerificationToken"[^>]*value="([^"]+)"/); assert.ok(match, 'antiforgery token'); return match[1]; };
  const guest = session('198.51.100.10');
  for (const route of ['/', '/Account/Login', '/Account/Register', '/Account/ForgotPassword', '/Account/ResetPassword?email=synthetic%40example.test', '/Account/Verify?email=synthetic%40example.test']) {
    const page = await guest(route);
    check(page.status === 200 && !page.body.includes('id="studio-sidebar"') && page.body.includes('class="guest-brand"')
      && page.body.includes('href="/Account/Login"') && page.body.includes('href="/Account/Register"')
      && !page.body.includes('My appointments') && !page.body.includes('Manage administrators')
      && !page.body.includes('customer@lumiere.demo') && !page.body.includes('admin@lumiere.demo'), `guest ${route} has branded header and no sidebar`);
  }
  const landing = await guest('/');
  check(!/href="\/Bookings(?:\/|\?|")/.test(landing.body) && !/href="\/Admin(?:\/|\?|")/.test(landing.body), 'guest landing does not expose authenticated navigation links');
  const registerPage = await guest('/Account/Register');
  check(/<select[^>]*name="gender"[^>]*required/.test(registerPage.body)
    && /<option value="" disabled selected="selected">Select gender<\/option>/.test(registerPage.body)
    && !registerPage.body.includes('Select (optional)'), 'gender select has required disabled placeholder');
  check(registerPage.body.includes('Email verification is not configured') && registerPage.body.includes('no email will be sent'), 'unconfigured registration explains that no email can be sent');
  const form = { firstName: 'Synthetic', lastName: 'Visitor', age: '25', birthday: '1995-01-15', address: '123 Example Street', password: 'Test9' + randomUUID(), returnUrl: '' };
  const missing = await guest('/Account/Register', { ...form, email: 'missinggender@example.test', gender: '', __RequestVerificationToken: token(registerPage.body) });
  check(missing.status === 200 && missing.body.includes('Select a gender.') && !missing.location, 'server rejects registration without gender');
  const invalid = await guest('/Account/Register', { ...form, email: 'invalidgender@example.test', gender: 'Invalid', __RequestVerificationToken: token(registerPage.body) });
  check(invalid.status === 200 && invalid.body.includes('Choose a valid option for gender.')
    && /<option value="" disabled selected="selected">Select gender<\/option>/.test(invalid.body), 'server rejects forged gender value and keeps placeholder selected');
  const otherError = await guest('/Account/Register', { ...form, firstName: '', email: 'othererror@example.test', gender: 'Female', __RequestVerificationToken: token(registerPage.body) });
  check(otherError.status === 200 && /<option value="Female" selected="selected">Female<\/option>/.test(otherError.body), 'valid gender remains selected when another field fails');
  for (const [index, gender] of ['Male', 'Female', 'Others'].entries()) {
    const client = session(`198.51.100.${20 + index}`), page = await client('/Account/Register');
    const email = `gender${index}${Date.now()}@example.test`;
    const result = await client('/Account/Register', { ...form, email, gender, __RequestVerificationToken: token(page.body) });
    check(result.status === 200 && result.body.includes('no email was sent') && !result.location, `${gender} signup fails closed without an email provider`);
    const loginPage = await client('/Account/Login');
    const login = await client('/Account/Login', { email, password: form.password, __RequestVerificationToken: token(loginPage.body) });
    check(login.status === 200 && login.body.includes('Email or password is incorrect.'), 'unverified signup creates no account');
  }
  const noProvider = session('198.51.100.29');
  const verify = await noProvider('/Account/Verify?email=unconfigured%40example.test');
  const resend = await noProvider('/Account/Resend', { email: 'unconfigured@example.test', __RequestVerificationToken: token(verify.body) });
  check(resend.status === 200 && resend.body.includes('no email was sent') && !resend.body.includes('Supabase accepted'), 'resend reports unavailable email delivery without a false provider claim');
  check(!existsSync(path.join(content, 'App_Data', 'EmailCodes')), 'signup did not create a local code outbox');
  for (const [index, email, password, role] of [
    [30, customer.email, customer.password, 'Customer'],
    [31, admin.email, admin.password, 'Admin'],
    [32, owner.email, owner.password, 'SuperAdmin']]) {
    const client = session(`198.51.100.${index}`), page = await client('/Account/Login');
    const login = await client('/Account/Login', { email, password, __RequestVerificationToken: token(page.body), returnUrl: '' });
    check(login.status === 302, `${role} can sign in locally`);
    const home = await client('/');
    check(role === 'Customer' ? home.body.includes('My appointments') && !home.body.includes('Manage administrators')
      : home.body.includes('Makeup Services') && (role === 'SuperAdmin' ? home.body.includes('Manage administrators') : !home.body.includes('Manage administrators')), `${role} receives only appropriate navigation`);
  }
  const invite = await guest('/Account/AcceptInvite?email=synthetic%40example.test&token=synthetic');
  check(invite.body.includes('Administrator invitation acceptance is temporarily unavailable') && !invite.body.includes('name="token"'), 'Admin invitation containment remains visible');
  console.log(`ALL GUEST AND REGISTRATION CHECKS PASSED (${checks})`);
} finally {
  if (app.exitCode === null) { const exited = once(app, 'exit'); app.kill(); await exited; }
}
