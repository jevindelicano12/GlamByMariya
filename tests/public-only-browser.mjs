// Two fresh loopback hosts: public-only and an independent synthetic Demo.
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { spawn } from 'node:child_process';
import { mkdir, mkdtemp } from 'node:fs/promises';
import path from 'node:path';

const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright-core');
const parent = path.join(process.env.LOCALAPPDATA, 'Temp/opencode'); await mkdir(parent, { recursive: true });
const screenshots = await mkdtemp(path.join(parent, 'public-only-screens-'));
const project = path.resolve('.'), bin = process.env.PUBLIC_ONLY_BUILD || path.join(parent, 'public-only-build');
const dll = path.join(bin, 'GlamByMariya.Web.dll'), webroot = path.join(project, 'Frontend/GlamByMariya.Web/wwwroot');
const processes = []; let browser, checks = 0;
const check = (ok, label) => { assert.ok(ok, label); console.log(`PASS ${++checks}: ${label}`); };
async function start(mode) {
  const root = await mkdtemp(path.join(parent, `public-boundary-${mode}-`));
  const args = [dll, '--urls=http://127.0.0.1:0', `--contentRoot=${root}`, `--webroot=${webroot}`,
    `--Database:Provider=${mode === 'public' ? 'PostgreSql' : 'Demo'}`, '--DemoAccounts:Presentation=false', '--Authentication:DurableSessionEnforcement=false',
    '--DemoGallery:Enabled=false', '--DemoCatalog:Enabled=false', '--DemoAvatar:Enabled=false', '--WebsiteContent:DemoTextEditing=false',
    '--WebsiteContent:DemoPersistentText=false', '--Supabase:Url=', '--Supabase:AnonKey=', '--SupabaseStorage:ServiceKey=',
    '--FileStorage:Provider=Local', '--Email:BrevoApiKey=', '--Email:SmtpHost=', `--PublicSite:InformationOnly=${mode === 'public'}`];
  const environment = mode === 'public' ? 'Production' : 'Development';
  const server = spawn('dotnet', args, { env: { ...process.env, PORT: '', ASPNETCORE_ENVIRONMENT: environment, DOTNET_ENVIRONMENT: environment }, stdio: ['ignore', 'pipe', 'pipe'] });
  processes.push(server);
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(Error(`${mode} startup timeout`)), 30000);
    server.once('error', reject); server.once('exit', code => reject(Error(`${mode} exited ${code}`))); server.stderr.resume();
    server.stdout.on('data', chunk => { const m = String(chunk).match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/); if (m) { clearTimeout(timer); resolve(m[1]); } });
  });
}
try {
  const [base, demo] = await Promise.all([start('public'), start('demo')]);
  browser = await chromium.launch({ executablePath: process.env.BROWSER_EXECUTABLE || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', headless: true });
  const ctx = await browser.newContext();
  await ctx.route('**/*', r => [base, demo].includes(new URL(r.request().url()).origin) ? r.continue() : r.abort());
  const page = await ctx.newPage();
  const login = await page.goto(demo + '/Account/Login'); assert.equal(login.status(), 200);
  for (const asset of ['/css/account-settings.css', '/js/booking-quantity.js', '/js/availability-response.js'])
    assert.ok((await page.content()).includes(asset), `normal Demo retains ${asset}`);
  await page.fill('[name=email]', 'customer@lumiere.demo'); await page.fill('[name=password]', 'Customer123!');
  await Promise.all([page.waitForURL(u => !u.pathname.includes('/Account/Login')), page.locator('#auth-form button.button').click()]);
  check((await (await ctx.request.get(demo + '/Bookings')).text()).includes('My appointments'), 'normal isolated Demo authentication and booking page still work');
  const anonymous = await browser.newContext();
  const authenticated = [];
  for (const [email, password] of [['customer@lumiere.demo', 'Customer123!'], ['admin@lumiere.demo', 'Admin123!'], ['owner@lumiere.demo', 'Owner123!']]) {
    const source = await browser.newContext(), loginPage = await source.newPage();
    await loginPage.goto(demo + '/Account/Login'); await loginPage.fill('[name=email]', email); await loginPage.fill('[name=password]', password);
    await Promise.all([loginPage.waitForURL(u => !u.pathname.includes('/Account/Login')), loginPage.locator('#auth-form button.button').click()]);
    const authCookie = (await source.cookies(demo)).find(c => c.name.includes('AspNetCore.Cookies'));
    assert.ok(authCookie, 'actual authenticated Demo cookie captured');
    const target = await browser.newContext(); await target.addCookies([{ name: authCookie.name, value: authCookie.value, url: base }]);
    authenticated.push(target);
  }
  for (const client of [anonymous.request, ...authenticated.map(x => x.request)]) {
    for (const url of ['/Account/Login', '/Account/Register', '/Account/Settings', '/Bookings', '/Bookings/Create',
      '/Bookings/Details/00000000-0000-0000-0000-000000000001', '/Bookings/Proof/00000000-0000-0000-0000-000000000001',
      '/Admin', '/Admin/Appointments', '/Admin/Details/00000000-0000-0000-0000-000000000001', '/Admin/Proof/00000000-0000-0000-0000-000000000001',
      '/Payments', '/WebsiteContent', '/WebsiteImages/Image/00000000-0000-0000-0000-000000000001', '/DemoGallery', '/CatalogImages/Image',
      '/App_Data/Proofs/test.png', '/images/looks/soft-glam.jpg', '/unknown']) {
      const res = await client.get(base + url, { maxRedirects: 0 }); assert.equal(res.status(), 404, `GET ${url}`);
      assert.equal((await res.body()).length, 0, `GET ${url} must not expose data`);
    }
    for (const url of ['/Account/Login', '/Account/Register', '/Account/Logout', '/Bookings/Create', '/Bookings/CreateMixed', '/Bookings/CorrectProof',
      '/Bookings/Cancel', '/Admin/Action', '/Admin/Settings', '/Payments', '/WebsiteContent/Publish', '/Home/Index', '/healthz']) {
      const res = await client.post(base + url, { maxRedirects: 0, form: { id: '00000000-0000-0000-0000-000000000001' } });
      assert.equal(res.status(), 404, `POST ${url}`); assert.equal((await res.body()).length, 0);
    }
  }
  check(true, 'unauthenticated and prior Customer, Admin and SuperAdmin cookies cannot access private or transactional routes');
  for (const url of ['/', '/Home', '/Home/Index', '/Home/About', '/Home/Services', '/Home/Gallery', '/Home/Contact', '/healthz']) {
    const res = await ctx.request.get(base + url); assert.equal(res.status(), 200, `GET ${url}`);
    if (url !== '/healthz') {
      const text = await res.text(); assert.ok(!/href="\/(?:Account|Bookings|Admin)(?:\/|\?|")/i.test(text), `no transactional links on ${url}`);
      assert.ok(!text.includes('DEMO-REFERENCE-001') && !text.includes('24 Sample Street'));
      assert.ok(!text.includes('Book Your') && !text.includes('Book Now'));
    }
  }
  check(true, 'all public pages and health work without private data or transaction links');
  const unwantedAssets = ['/css/account-settings.css', '/js/booking-quantity.js', '/js/availability-response.js'];
  const requestedAssets = [];
  page.on('request', request => { const url = new URL(request.url()); if (url.origin === base && unwantedAssets.includes(url.pathname)) requestedAssets.push(url.pathname); });
  for (const url of ['/', '/Home', '/Home/Index', '/Home/About', '/Home/Services', '/Home/Gallery', '/Home/Contact']) {
    assert.equal((await page.goto(base + url, { waitUntil: 'networkidle' })).status(), 200);
    const html = await page.content();
    for (const asset of unwantedAssets) assert.ok(!html.includes(asset), `${url} must not reference ${asset}`);
  }
  assert.deepEqual(requestedAssets, [], 'public pages must not request account or booking assets');
  check(true, 'public pages do not request account or booking assets; normal Demo retains them');
  for (const width of [320, 390, 768, 1440]) {
    await page.setViewportSize({ width, height: 850 });
    for (const url of ['/', '/Home/About', '/Home/Services', '/Home/Gallery', '/Home/Contact']) {
      assert.equal((await page.goto(base + url)).status(), 200);
      const size = await page.evaluate(() => ({ viewport: document.documentElement.clientWidth, scroll: document.documentElement.scrollWidth }));
      assert.ok(size.scroll <= size.viewport, `${url} at ${width}: ${JSON.stringify(size)}`);
      const nav = width <= 1050 ? page.locator('nav[aria-label="Mobile navigation"]') : page.locator('nav[aria-label="Website"]');
      if (width <= 1050) await page.locator('details.public-menu summary').click();
      assert.ok(await nav.getByRole('link', { name: 'Contact', exact: true }).isVisible());
      if (url === '/' && (width === 390 || width === 1440)) await page.screenshot({ path: path.join(screenshots, `home-${width}.png`), fullPage: true });
    }
    check(true, `public navigation and layout at ${width}px`);
  }
  check((await (await ctx.request.get(base + '/Home/Contact')).text()).includes('Public contact details will be available once provided by the owner.'), 'unapproved contact details remain a placeholder');
  console.log(`ALL ${checks} PUBLIC-ONLY CHECKS PASSED; screenshots: ${screenshots}`);
} finally { if (browser) await browser.close(); for (const process of processes) process.kill(); }
