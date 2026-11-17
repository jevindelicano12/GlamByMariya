// Inspect cross-preview session behavior without printing cookie or password values.
import {createRequire} from 'node:module';
import {readFile} from 'node:fs/promises';
import assert from 'node:assert/strict';
const require=createRequire(import.meta.url), {chromium}=require(process.env.PLAYWRIGHT_MODULE || 'playwright-core');
const credentials=JSON.parse((await readFile(process.env.QA_NEW_FILE,'utf8')).replace(/^\uFEFF/,''));
const source=await readFile('Data/DemoStore.cs','utf8');
const match=source.match(/AddUser\("Alex", "Morgan", "([^"]+)", "([^"]+)"/);
assert.ok(match,'Default synthetic Customer fixture missing');
const browser=await chromium.launch({executablePath:process.env.BROWSER_EXECUTABLE || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',headless:true});
try {
  const ctx=await browser.newContext(), page=await ctx.newPage();
  async function login(base,email,password){await page.goto(base+'/Account/Login');await page.locator('[name=email]').fill(email);await page.locator('[name=password]').fill(password);await Promise.all([page.waitForURL(u=>!u.pathname.includes('/Account/Login')),page.locator('#auth-form button.button').click()]);}
  await login('http://127.0.0.1:5203',credentials.Customer.Email,credentials.Customer.Password);
  assert.equal((await ctx.request.get('http://127.0.0.1:5203/Bookings/Create',{maxRedirects:0})).status(),200);
  await login('http://127.0.0.1:5202',match[1],match[2]);
  assert.equal((await ctx.request.get('http://127.0.0.1:5202/Bookings/Create',{maxRedirects:0})).status(),200);
  const again=await ctx.request.get('http://127.0.0.1:5203/Bookings/Create',{maxRedirects:0});
  console.log(again.status()===302 && /Login/.test(again.headers().location || '') ? 'CONFIRMED: signing in on 5202 replaced or invalidated the 5203 session in the same browser profile.' : `5203 session after 5202 login: HTTP ${again.status()}; cookie interference not established by this check.`);
  await login('http://localhost:5203',credentials.Customer.Email,credentials.Customer.Password);
  assert.equal((await ctx.request.get('http://localhost:5203/Bookings/Create',{maxRedirects:0})).status(),200);
  assert.equal((await ctx.request.get('http://127.0.0.1:5202/Bookings/Create',{maxRedirects:0})).status(),200);
  console.log('CONFIRMED: localhost:5203 and 127.0.0.1:5202 remain signed in together in this test.');
} finally {await browser.close();}
