// Loopback-only synthetic account checks; never prints passwords or cookies.
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright-core');
const base = process.env.QA_BASE || 'http://127.0.0.1:5203';
if (!/^http:\/\/(127\.0\.0\.1|localhost):\d+$/.test(base)) throw Error('Only an explicit loopback preview is permitted.');
const secrets = process.env.QA_CREDENTIAL_FILE ? JSON.parse((await readFile(process.env.QA_CREDENTIAL_FILE,'utf8')).replace(/^\uFEFF/,'')) : null;
let accounts = secrets ? [
  {role:'Customer',email:secrets.Customer.Email ?? 'presentation-customer@example.test',password:secrets.Customer.Password ?? secrets.Customer,home:'/Bookings/Create'},
  {role:'Admin',email:secrets.Admin.Email ?? 'presentation-admin@example.test',password:secrets.Admin.Password ?? secrets.Admin,home:'/Admin/Appointments'},
  {role:'SuperAdmin',email:secrets.SuperAdmin.Email ?? 'presentation-owner@example.test',password:secrets.SuperAdmin.Password ?? secrets.SuperAdmin,home:'/Admin/Appointments'}
] : [];
if (!secrets && process.env.QA_USE_DEFAULT_DEMO === 'true') {
  const source=await readFile('Data/DemoStore.cs','utf8');
  accounts=[['Customer','Alex','Morgan','/Bookings/Create'],['Admin','Makeup','Artist','/Admin/Appointments'],['SuperAdmin','Business','Owner','/Admin/Appointments']].map(([role,first,last,home])=>{
    const match=source.match(new RegExp(`AddUser\\("${first}", "${last}", "([^"]+)", "([^"]+)"`));
    if(!match) throw Error('Synthetic default account fixture was not found.');
    return {role,email:match[1],password:match[2],home};
  });
}
if(accounts.length!==3) throw Error('Supply a protected credential file or select the local default-Demo diagnostic.');
const browser=await chromium.launch({executablePath:process.env.BROWSER_EXECUTABLE || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',headless:true});
let count=0;const check=(v,label)=>{assert.ok(v,label);console.log('PASS',++count,label);};
const denied=r=>r.status()===403 || (r.status()===302 && /(?:Login|AccessDenied)/i.test(r.headers().location || ''));
try {
  for(const a of accounts){
    a.context=await browser.newContext({viewport:{width:390,height:850},reducedMotion:'reduce'});a.page=await a.context.newPage();
    await a.page.goto(base+'/Account/Login');
    await a.page.locator('[name=email]').fill(a.email); await a.page.locator('[name=password]').fill(a.password);
    await Promise.all([a.page.waitForURL(u=>!u.pathname.includes('/Account/Login')),a.page.locator('#auth-form button.button').click()]);
    check((await a.context.cookies()).some(c=>c.name.includes('AspNetCore.Cookies')),`${a.role} signs in with authentication cookie`);
    const authorized=await a.context.request.get(base+a.home,{maxRedirects:0});check(authorized.status()===200,`${a.role} authorized page available after login`);
  }
  const [customer,admin,owner]=accounts;
  check(denied(await customer.context.request.get(base+'/Admin/Appointments',{maxRedirects:0})),'Customer denied Admin appointments');
  check(denied(await admin.context.request.get(base+'/Admin/Users',{maxRedirects:0})),'Admin denied SuperAdmin roster');
  check(denied(await admin.context.request.get(base+'/Admin/Activity',{maxRedirects:0})),'Admin denied SuperAdmin Activity Logs');
  check(denied(await admin.context.request.get(base+'/Bookings/Create',{maxRedirects:0})),'Admin denied Customer booking');
  check(denied(await owner.context.request.get(base+'/Bookings/Create',{maxRedirects:0})),'SuperAdmin denied Customer booking');
  check((await owner.context.request.get(base+'/Admin/Payments',{maxRedirects:0})).status()===200,'SuperAdmin payment management available');
  check((await owner.context.request.get(base+'/Admin/Users',{maxRedirects:0})).status()===200,'SuperAdmin roster available');
  await customer.page.goto(base+'/Bookings');
  const details=await customer.page.locator('a[href*="/Bookings/Details/"]').first().getAttribute('href');
  const bookingId=details.split('/').pop();
  for(const a of [customer,admin,owner]) { await a.page.goto(base+'/Account/Settings'); a.token=await a.page.locator('input[name=__RequestVerificationToken]').first().inputValue(); }
  for(const a of [customer,admin]) {
    const action=await a.context.request.post(base+'/Admin/Action',{form:{__RequestVerificationToken:a.token,id:bookingId,operation:'cancel',reason:'synthetic unauthorized test',confirmed:'true'},maxRedirects:0});
    check(denied(action),`${a.role} denied staff cancellation POST`);
  }
  const staffBooking=await owner.context.request.post(base+'/Bookings/Cancel',{form:{__RequestVerificationToken:owner.token,id:bookingId,confirmed:'true'},maxRedirects:0});
  check(denied(staffBooking),'SuperAdmin denied Customer cancellation POST');
  for(const a of accounts){
    await a.page.goto(base+'/Account/Settings');
    await Promise.all([a.page.waitForURL(u=>!u.pathname.includes('/Account/Settings')),a.page.locator('form[action="/Account/Logout"] button').last().click()]);
    check(denied(await a.context.request.get(base+a.home,{maxRedirects:0})),`${a.role} logout ends authorized session`);
    for(const other of accounts.filter(x=>x!==a && !x.loggedOut)) check((await other.context.request.get(base+other.home,{maxRedirects:0})).status()===200,`${a.role} logout leaves ${other.role} separate session active`);
    await a.page.goto(base+'/Account/Login');await a.page.locator('[name=email]').fill(a.email);await a.page.locator('[name=password]').fill(a.password);
    await Promise.all([a.page.waitForURL(u=>!u.pathname.includes('/Account/Login')),a.page.locator('#auth-form button.button').click()]);
    check((await a.context.request.get(base+a.home,{maxRedirects:0})).status()===200,`${a.role} can sign in again`);a.loggedOut=true;
  }
  console.log(`ALL ${count} LOGIN AND SESSION CHECKS PASSED.`);
} finally {await browser.close();}
