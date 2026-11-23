// Synthetic loopback Demo only; requires PLAYWRIGHT_MODULE and a local browser.
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { spawn } from 'node:child_process';
import { mkdir } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import path from 'node:path';
import { admin, owner } from './presentation-test-accounts.mjs';

const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright-core');
const app = path.resolve(process.argv[2] || 'Frontend/GlamByMariya.Web/bin/Release/net10.0');
const web = path.resolve(process.argv[3] || 'Frontend/GlamByMariya.Web/wwwroot');
const root = path.join(process.env.LOCALAPPDATA, 'Temp', 'opencode', 'sidebar-test-' + randomUUID());
const data = path.join(process.env.LOCALAPPDATA, 'GlamByMariya', 'sidebar-test-' + randomUUID());
await mkdir(root, { recursive: true });
const server = spawn('dotnet', [path.join(app, 'GlamByMariya.Web.dll'), '--urls=http://127.0.0.1:0', `--contentRoot=${root}`, `--webroot=${web}`,
  '--Database:Provider=Demo', '--Authentication:DurableSessionEnforcement=false', '--DemoGallery:Enabled=true', `--DemoGallery:Root=${path.join(data, 'gallery')}`,
  '--WebsiteContent:DemoTextEditing=true', '--WebsiteContent:DemoPersistentText=true', `--WebsiteContent:DemoRoot=${path.join(data, 'content')}`,
  '--Supabase:Url=', '--Supabase:AnonKey=', '--SupabaseStorage:ServiceKey=', '--FileStorage:Provider=Local', '--B2:Bucket=', '--B2:Endpoint=',
  '--B2:KeyId=', '--B2:AppKey=', '--Email:BrevoApiKey=', '--Email:SmtpHost='],
  { env: { ...process.env, PORT: '', ASPNETCORE_ENVIRONMENT: 'Development', DOTNET_ENVIRONMENT: 'Development' }, stdio: ['ignore', 'pipe', 'pipe'] });
let browser;
try {
  const base = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(Error('Demo startup timeout')), 30000);
    server.stdout.on('data', b => { const match = String(b).match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/); if (match) { clearTimeout(timer); resolve(match[1]); } });
    server.stderr.resume(); server.once('exit', code => { clearTimeout(timer); reject(Error('Demo exited: ' + code)); });
  });
  browser = await chromium.launch({ headless: true, executablePath: process.env.BROWSER_EXECUTABLE });
  for (const [role, account] of [['admin', admin], ['owner', owner]]) {
    const context = await browser.newContext();
    await context.route('**/*', route => new URL(route.request().url()).origin === base ? route.continue() : route.abort());
    const page = await context.newPage();
    await page.goto(base + '/Account/Login');
    await page.locator('[name=email]').fill(account.email); await page.locator('[name=password]').fill(account.password);
    await Promise.all([page.waitForURL(base + '/Admin'), page.locator('#auth-form button.button').click()]);
    const nav = page.locator('#staff-mobile-menu nav');
    const link = name => nav.locator('a').filter({ hasText: name });
    async function openMenu(mobile) { if (mobile && !await page.locator('#staff-mobile-menu').evaluate(e => e.classList.contains('is-open'))) await page.locator('#staff-mobile-menu .menu-toggle').click(); }
    async function active(name, mobile) {
      await openMenu(mobile);
      await page.waitForFunction(() => { const m = document.querySelector('#staff-mobile-menu'); return !m || !matchMedia('(max-width: 850px)').matches || m.classList.contains('is-open'); });
      // A click may return while the next document is still loading. Wait for
      // the expected route's active link before asserting the full nav state.
      await link(name).locator('xpath=self::a[@aria-current="page"]').waitFor({ state: 'attached' });
      assert.equal(await nav.locator('a.selected').count(), 1, `${role} has one active link`);
      assert.equal(await nav.locator('a[aria-current="page"]').count(), 1);
      const item = link(name); assert.equal(await item.getAttribute('aria-current'), 'page', name);
      assert.equal(await item.evaluate(e => getComputedStyle(e).backgroundColor), 'rgb(166, 83, 107)');
      assert.equal(await item.evaluate(e => getComputedStyle(e).color), 'rgb(255, 255, 255)');
      assert.equal(await item.locator('span').evaluate(e => getComputedStyle(e).color), 'rgb(255, 255, 255)');
      const visible = await item.evaluate((e, mobile) => { const c = mobile ? e.closest('nav') : document.querySelector('#studio-sidebar'); const a = e.getBoundingClientRect(), b = c.getBoundingClientRect(); return a.top >= b.top - 1 && a.bottom <= b.bottom + 1; }, mobile);
      assert.ok(visible, `${name} is visible in its scroll container`);
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
    }
    for (const [width, height] of [[1440, 900], [1366, 768], [1280, 720], [390, 844], [360, 740]]) {
      const mobile = width <= 850;
      await page.setViewportSize({ width, height });
      await page.goto(base + '/Admin'); await active('Overview', mobile);
      await link('Appointments').click(); await active('Appointments', mobile);
      await link('Sales Reports').click(); await active('Sales Reports', mobile);
      await link('Payments').click(); await active('Payments', mobile);
      await link('Business Settings').click(); await active('Business Settings', mobile);
      await link('Account settings').click(); await active('Account settings', mobile);
      const pageScroll = await page.evaluate(async () => { window.scrollTo(0, 180); const before = window.scrollY; window.dispatchEvent(new PageTransitionEvent('pageshow')); await new Promise(requestAnimationFrame); await new Promise(requestAnimationFrame); return [before, window.scrollY]; });
      assert.equal(pageScroll[1], pageScroll[0], 'sidebar restore must not move the main page');
      if (role === 'owner') {
        await link('Website Content Management').click(); await active('Website Content Management', mobile);
        const container = mobile ? nav : page.locator('#studio-sidebar');
        await container.evaluate(e => { e.scrollTop = e.scrollHeight; });
        const before = await container.evaluate(e => e.scrollTop);
        await link('Gallery Management').click(); await active('Gallery Management', mobile);
        assert.equal(await link('Sales Reports').getAttribute('aria-current'), null);
        const after = await container.evaluate(e => e.scrollTop);
        assert.ok(Math.abs(after - before) <= 4, `sidebar position changed: ${before} -> ${after}`);
        await page.screenshot({ path: path.join(root, `gallery-${width}.png`) });
        await link('Account settings').click(); await active('Account settings', mobile);
        await link('Website Content Management').click(); await active('Website Content Management', mobile);
        await page.reload(); await active('Website Content Management', mobile);
        await page.goBack(); await active('Account settings', mobile);
        await page.goForward(); await active('Website Content Management', mobile);
        await page.goto(base + '/DemoGallery'); await active('Gallery Management', mobile);
      } else {
        await page.reload(); await active('Account settings', mobile);
        await page.goBack(); await active('Business Settings', mobile);
        await page.goForward(); await active('Account settings', mobile);
        await page.goto(base + '/Admin/Reports'); await active('Sales Reports', mobile);
        await page.screenshot({ path: path.join(root, `reports-${width}.png`) });
      }
      await link('Overview').focus();
      assert.equal(await link('Overview').evaluate(e => document.activeElement === e), true, 'keyboard focus');
      await Promise.all([page.waitForURL(base + '/Admin'), page.keyboard.press('Enter')]);
      await active('Overview', mobile);
      console.log(`PASS ${role} ${width}x${height}: navigation, active route, scroll, history, refresh and keyboard`);
    }
    await page.setViewportSize({ width: 360, height: 400 });
    await page.goto(base + '/Account/Settings'); await active('Account settings', true);
    assert.ok(await nav.evaluate(e => e.scrollHeight > e.clientHeight && e.scrollTop > 0), 'short mobile menu scrolls to the active lower item');
    console.log(`PASS ${role}: short mobile menu scrolls to its lower active item`);
    await context.close();
  }
  console.log('Screenshots and isolated content root:', root);
} finally {
  await browser?.close();
  if (server.exitCode === null) await new Promise(resolve => { server.once('exit', resolve); server.kill(); });
}
