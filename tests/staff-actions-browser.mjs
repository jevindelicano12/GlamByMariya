// Starts an isolated Demo with an in-process, test-only startup hook; no fixture endpoint.
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { spawn } from 'node:child_process';
import { mkdtemp, mkdir, readFile } from 'node:fs/promises';
import path from 'node:path';
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright-core');
const project = path.resolve('.'), bin = process.env.FIXTURE_BUILD || path.join(process.env.LOCALAPPDATA, 'Temp/opencode/phase3b-fixture-build');
const parent = path.join(process.env.LOCALAPPDATA, 'Temp/opencode'); await mkdir(parent, {recursive:true});
const root = await mkdtemp(path.join(parent, 'staff-actions-browser-'));
const server = spawn('dotnet', [path.join(bin,'GlamByMariya.Web.dll'), '--urls=http://127.0.0.1:0', `--contentRoot=${root}`, `--webroot=${path.join(project,'Frontend/GlamByMariya.Web/wwwroot')}`, '--Database:Provider=Demo', '--DemoAccounts:Presentation=false', '--Authentication:DurableSessionEnforcement=false', '--DemoGallery:Enabled=false', '--DemoCatalog:Enabled=false', '--DemoAvatar:Enabled=false', '--WebsiteContent:DemoTextEditing=false', '--WebsiteContent:DemoPersistentText=false', '--Supabase:Url=', '--Supabase:AnonKey=', '--Supabase:ServiceKey=', '--FileStorage:Provider=Local', '--B2:Bucket=', '--Email:BrevoApiKey=', '--Email:SmtpHost='], {env:{...process.env,PORT:'',ASPNETCORE_ENVIRONMENT:'Development',DOTNET_ENVIRONMENT:'Development',DOTNET_STARTUP_HOOKS:path.join(bin,'Phase3BFixtures.dll'),PHASE3B_FIXTURE_ROOT:root},stdio:['ignore','pipe','pipe']});
let browser, checks=0;
const check=(ok,label)=>{assert.ok(ok,label); console.log('PASS',++checks,label);};
try {
  const base=await new Promise((resolve,reject)=>{const timer=setTimeout(()=>reject(Error('startup timed out')),30000);server.once('exit',c=>reject(Error(`server exited ${c}`)));server.stderr.on('data',d=>process.stderr.write(d));server.stdout.on('data',d=>{const m=String(d).match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/);if(m){clearTimeout(timer);resolve(m[1]);}});});
  const ids=JSON.parse(await readFile(path.join(root,'fixtures.json'),'utf8'));
  const dates=JSON.parse(await readFile(path.join(root,'fixture-dates.json'),'utf8'));
  browser=await chromium.launch({executablePath:process.env.BROWSER_EXECUTABLE || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',headless:true});
  async function login(email,password){const ctx=await browser.newContext({viewport:{width:1440,height:900},reducedMotion:'reduce'});await ctx.route('**/*',r=>new URL(r.request().url()).origin===base?r.continue():r.abort());const p=await ctx.newPage();await p.goto(base+'/Account/Login');await p.fill('[name=email]',email);await p.fill('[name=password]',password);await Promise.all([p.waitForURL(u=>!u.pathname.includes('/Account/Login')),p.locator('#auth-form button.button').click()]);return p;}
  const owner=await login('owner@lumiere.demo','Owner123!'), customer=await login('customer@lumiere.demo','Customer123!'), admin=await login('admin@lumiere.demo','Admin123!');
  const open=async(p,id,role='Admin')=>p.goto(`${base}/${role}/Details/${id}`);
  const post=async(p,id,fields,token)=>p.context().request.post(base+'/Admin/Action',{form:{__RequestVerificationToken:token ?? await p.locator('[name=__RequestVerificationToken]').first().inputValue(),id,...fields},maxRedirects:0});
  const submit=async(p,button)=>{await Promise.all([p.waitForResponse(r=>r.url().endsWith('/Admin/Action')&&r.request().method()==='POST'),button.click()]);await p.waitForLoadState('load');};
  async function screens(p,label){for(const width of [1440,768,390,320]){await p.setViewportSize({width,height:900});await p.screenshot({path:path.join(root,`${label}-${width}.png`),fullPage:true});check(await p.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),`${label} ${width}px overflow`);}await p.setViewportSize({width:1440,height:900});}
  for(const type of ['individual','same','mixed']){
    let id=ids[`${type}-past`]; await open(owner,id);
    const before=await owner.locator(type==='mixed'?'.mixed-payment-claim':'.payment-box').innerText();
    const form=owner.locator('[data-decision=manual-complete]'); check(await form.count()===1,`${type} eligible completion visible`);
    await post(owner,id,{operation:'complete'});await owner.reload();check((await owner.locator('main').innerText()).includes('Confirm that all makeup services'),`${type} completion requires confirmation`);
    if(type==='individual') await screens(owner,'completion-form');
    await form.locator('[name=confirmed]').focus(); await owner.keyboard.press('Space'); check(await form.locator('[name=confirmed]').isChecked(),`${type} completion keyboard confirmation`);
    await submit(owner,form.locator('button'));
    check((await owner.locator('main').innerText()).includes('Manually marked completed on') && (await owner.locator('main').innerText()).includes('Business Owner'),`${type} completed with actor and time`);
    check(await owner.locator(type==='mixed'?'.mixed-payment-claim':'.payment-box').innerText()===before,`${type} payment record unchanged`);
    await open(customer,id,'Bookings');check((await customer.locator('main').innerText()).includes('Appointment completed'),`${type} customer sees completion`);
    if(type==='mixed') await screens(customer,'completed-customer');
    await open(owner,id);await post(owner,id,{operation:'complete',confirmed:'true'});await owner.reload();check((await owner.locator('main').innerText()).includes('Only confirmed appointments'),`${type} duplicate completion rejected`);
    id=ids[`${type}-future`];await open(owner,id);await post(owner,id,{operation:'complete',confirmed:'true'});await owner.reload();check((await owner.locator('main').innerText()).includes('Only confirmed appointments'),`${type} premature completion rejected`);
    id=ids[`${type}-reject`];await open(owner,id);await post(owner,id,{operation:'complete',confirmed:'true'});await owner.reload();check((await owner.locator('main').innerText()).includes('Only confirmed appointments'),`${type} rejected completion denied`);
    id=ids[`${type}-cancel`];await open(owner,id);const proof=await owner.locator('a[href*="/Admin/Proof/"]').first().getAttribute('href');const prior=await owner.locator(type==='mixed'?'.mixed-payment-claim':'.payment-box').innerText();
    await post(owner,id,{operation:'cancel',reason:'No confirmation'});await owner.reload();check((await owner.locator('main').innerText()).includes('Confirm the cancellation'),`${type} cancellation requires confirmation`);
    const cancel=owner.locator('[data-decision=staff-cancel]');await cancel.locator('textarea').fill('Synthetic business reason');await cancel.locator('[name=confirmed]').check();await submit(owner,cancel.locator('button'));
    check((await owner.locator('main').innerText()).includes('Synthetic business reason') && await owner.locator(type==='mixed'?'.mixed-payment-claim':'.payment-box').innerText()===prior,`${type} confirmed cancellation preserves payment and reason`);
    check((await owner.context().request.get(base+proof)).status()===200,`${type} proof retained`);
    const [cy,cm,cd]=dates[`${type}-cancel`].slice(0,10).split('-').map(Number);await owner.goto(`${base}/Admin/Appointments?mode=calendar&year=${cy}&month=${cm}&day=${cd}`);
    check((await owner.locator(`[data-calendar-booking="${id}"]`).innerText()).includes('No pending/confirmed reservation held'),`${type} cancelled reservation released`);
    await open(owner,id);
    await post(owner,id,{operation:'cancel',reason:'duplicate',confirmed:'true'});await owner.reload();check((await owner.locator('main').innerText()).includes('Only pending or confirmed'),`${type} duplicate cancellation rejected`);
    await post(owner,id,{operation:'complete',confirmed:'true'});await owner.reload();check((await owner.locator('main').innerText()).includes('Only confirmed appointments'),`${type} cancelled completion denied`);
    await open(customer,id,'Bookings');check((await customer.locator('main').innerText()).includes('Mariya will contact you about verified payments'),`${type} customer cancellation notice`);
    if(type==='same') await screens(customer,'cancelled-customer');
  }
  for (const type of ['individual','same','mixed']) {
    const id=ids[`${type}-future`]; await open(owner,id);
    const forged=await post(owner,id,{operation:'cancel',reason:'forged',confirmed:'true'},'forged-token');
    check(forged.status()===400,`${type} forged antiforgery token rejected`);
    check((await post(owner,id,{operation:'complete',confirmed:'true'},'forged-token')).status()===400,`${type} completion rejects forged antiforgery`);
    await open(admin,ids['individual-future']);
    const adminToken=await admin.locator('[name=__RequestVerificationToken]').first().inputValue();
    check([302,403].includes((await post(admin,id,{operation:'cancel',reason:'forged',confirmed:'true'},adminToken)).status()),`${type} ordinary Admin cancellation denied`);
    check([302,403].includes((await post(admin,id,{operation:'complete',confirmed:'true'},adminToken)).status()),`${type} ordinary Admin completion denied`);
    await open(customer,ids['individual-future'],'Bookings');
    const customerToken=await customer.locator('[name=__RequestVerificationToken]').first().inputValue();
    check([302,403].includes((await post(customer,id,{operation:'cancel',reason:'forged',confirmed:'true'},customerToken)).status()),`${type} Customer staff cancellation denied`);
    check([302,403].includes((await post(customer,id,{operation:'complete',confirmed:'true'},customerToken)).status()),`${type} Customer completion denied`);
    await open(owner,id);check((await owner.locator('main').innerText()).includes('Confirmed'),`${type} unauthorized attempts do not cancel`);
  }
  const unverified=ids['mixed-unverified'];await open(owner,unverified);
  const cancelUnverified=owner.locator('[data-decision=staff-cancel]');await cancelUnverified.locator('textarea').fill('Synthetic unverified cancellation');await cancelUnverified.locator('[name=confirmed]').check();await submit(owner,cancelUnverified.locator('button'));
  let receipt=owner.locator('[data-decision=verify-cancelled-receipt]');check(await receipt.count()===1 && (await owner.locator('main').innerText()).includes('Unverified'),'cancelled receipt starts unverified');
  await screens(owner,'cancelled-receipt');
  const paymentId=await receipt.locator('[name=paymentId]').inputValue(), paymentRevision=await receipt.locator('[name=paymentRevision]').inputValue();
  await post(owner,unverified,{operation:'verify-cancelled-receipt',paymentId,paymentRevision,reviewed:'true'},'forged-token').then(r=>check(r.status()===400,'receipt verification rejects forged antiforgery'));
  await open(admin,ids['individual-future']);const receiptAdminToken=await admin.locator('[name=__RequestVerificationToken]').first().inputValue();
  await post(admin,unverified,{operation:'verify-cancelled-receipt',paymentId,paymentRevision,reviewed:'true'},receiptAdminToken).then(r=>check([302,403].includes(r.status()),'ordinary Admin receipt verification denied'));
  await open(customer,ids['individual-future'],'Bookings');const receiptCustomerToken=await customer.locator('[name=__RequestVerificationToken]').first().inputValue();
  await post(customer,unverified,{operation:'verify-cancelled-receipt',paymentId,paymentRevision,reviewed:'true'},receiptCustomerToken).then(r=>check([302,403].includes(r.status()),'Customer receipt verification denied'));
  await post(owner,unverified,{operation:'verify-cancelled-receipt',paymentId,paymentRevision,reviewed:'false'});await owner.reload();check((await owner.locator('main').innerText()).includes('Inspect the available payment proof'),'receipt verification requires review');
  receipt=owner.locator('[data-decision=verify-cancelled-receipt]');await receipt.locator('[name=reviewed]').focus();await owner.keyboard.press('Space');await submit(owner,receipt.locator('button'));
  await owner.reload();check((await owner.locator('main').innerText()).includes('Verified') && await owner.locator('.admin-mixed-details').getAttribute('data-status')==='Cancelled' && await owner.locator('[data-decision=verify-cancelled-receipt]').count()===0,'verified receipt leaves appointment cancelled');
  await post(owner,unverified,{operation:'verify-cancelled-receipt',paymentId,paymentRevision,reviewed:'true'});await owner.reload();check((await owner.locator('main').innerText()).includes('already verified'),'duplicate receipt verification rejected');
  await owner.goto(base+'/Admin/Activity');const activity=await owner.locator('main').innerText();check(activity.includes('Appointment completed')&&activity.includes('Appointment cancelled by SuperAdmin'), 'activity logs contain both actions');
  check(activity.includes('Cancelled booking receipt verified'),'receipt review recorded in activity log');
  console.log(`ALL ${checks} STAFF BROWSER CHECKS PASSED; artifacts: ${root}`);
} finally {await browser?.close();server.kill();}
