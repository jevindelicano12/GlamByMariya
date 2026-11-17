import assert from 'node:assert/strict';
import { admin, owner, customer } from './presentation-test-accounts.mjs';
import { spawn } from 'node:child_process';
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
for (const file of ['Frontend/GlamByMariya.Web/Views/Home/Index.cshtml', 'Frontend/GlamByMariya.Web/Views/Home/About.cshtml', 'Frontend/GlamByMariya.Web/Views/Shared/_PublishedGallery.cshtml']) {
  const source = await readFile(path.join(root, file), 'utf8');
  assert.match(source, /src="@Url\.Action\("Image", "WebsiteImages"/);
}
console.log('PASS public image views use explicit image URLs (static check)');
const parent = path.join(process.env.LOCALAPPDATA, 'Temp', 'opencode');
await mkdir(parent, { recursive: true });
const temp = await mkdtemp(path.join(parent, 'website-content-screen-'));
const env = { PATH:process.env.PATH, SystemRoot:process.env.SystemRoot, USERPROFILE:temp, HOME:temp, TEMP:temp, TMP:temp,
  APPDATA:temp, LOCALAPPDATA:temp, ASPNETCORE_ENVIRONMENT:'Development', DOTNET_ENVIRONMENT:'Development', PORT:'',
  DemoAccounts__Presentation:'true', GLAM_DEMO_ADMIN_PASSWORD:admin.password, GLAM_DEMO_OWNER_PASSWORD:owner.password, GLAM_DEMO_CUSTOMER_PASSWORD:customer.password };
const app = spawn('dotnet', [path.join(root, 'Frontend/GlamByMariya.Web/bin/Release/net10.0/GlamByMariya.Web.dll'), '--urls=http://127.0.0.1:0',
  `--contentRoot=${temp}`, `--webroot=${path.join(root, 'Frontend/GlamByMariya.Web/wwwroot')}`, '--Database:Provider=Demo', '--Supabase:Url=',
  '--Supabase:AnonKey=', '--SupabaseStorage:ServiceKey=', '--Email:BrevoApiKey=', '--Email:SmtpHost=',
  '--B2:Bucket=', '--B2:Endpoint=', '--B2:KeyId=', '--B2:AppKey=' ], { cwd:temp, env, stdio:['ignore','pipe','pipe'] });
let chrome;
try {
  const base = await new Promise((resolve,reject)=>{const t=setTimeout(()=>reject(Error('app timeout')),30000);app.on('exit',()=>reject(Error('app exited')));app.on('error',reject);app.stderr.resume();app.stdout.on('data',c=>{const m=String(c).match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/);if(m){clearTimeout(t);resolve(m[1]);}})});
  const client=()=>{const cookies=new Map();return async (url,form)=>{const h={};if(cookies.size)h.Cookie=[...cookies].map(([k,v])=>`${k}=${v}`).join('; ');if(form)h['Content-Type']='application/x-www-form-urlencoded';const r=await fetch(base+url,{method:form?'POST':'GET',headers:h,body:form?new URLSearchParams(form):undefined,redirect:'manual'});for(const c of r.headers.getSetCookie()){const s=c.split(';')[0],i=s.indexOf('=');cookies.set(s.slice(0,i),s.slice(i+1))}return {status:r.status,location:r.headers.get('location'),body:await r.text()}}};
  const guest=client();assert.equal((await guest('/WebsiteContent')).status,302);
  assert.equal((await guest('/WebsiteContent/DraftImage/00000000-0000-0000-0000-000000000001')).status,302);
  const gallery=await guest('/Home/Gallery');assert.equal(gallery.status,200);assert.match(gallery.body,/Approved makeup photos will appear here/);
  assert.equal((await guest('/WebsiteImages/Image/00000000-0000-0000-0000-000000000001')).status,404);
  console.log('PASS public gallery placeholder and disabled image endpoint');
  for(const [email,password,role] of [[customer.email,customer.password,'Customer'],[admin.email,admin.password,'Admin'],[owner.email,owner.password,'SuperAdmin']]){
    const c=client(),login=await c('/Account/Login');const token=login.body.match(/name="__RequestVerificationToken"[^>]*value="([^"]+)"/)?.[1];assert.ok(token);
    assert.equal((await c('/Account/Login',{email,password,__RequestVerificationToken:token})).status,302);
    const page=await c('/WebsiteContent');assert.equal(page.status,role==='SuperAdmin'?200:302,role);
    assert.equal((await c('/WebsiteContent/DraftImage/00000000-0000-0000-0000-000000000001')).status,role==='SuperAdmin'?404:302,role+' draft image');
    if(role==='SuperAdmin'){assert.match(page.body,/Editing is not available yet/);assert.match(page.body,/<fieldset disabled>/);assert.doesNotMatch(page.body,/<button[^>]*>\s*(Save Draft|Publish|Preview)/i);assert.equal((await c('/WebsiteContent/Save',{})).status,400);assert.equal((await c('/WebsiteContent/Preview')).status,503)}
    console.log(`PASS ${role} authorization`);
  }
  chrome=spawn('C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',['--headless=new','--disable-gpu','--no-first-run','--remote-debugging-port=51736',`--user-data-dir=${path.join(temp,'chrome')}`,'about:blank'],{stdio:['ignore','ignore','pipe']});
  await new Promise((resolve,reject)=>{const t=setTimeout(()=>reject(Error('Chrome timeout')),20000);chrome.on('exit',()=>reject(Error('Chrome exited')));chrome.stderr.on('data',c=>{if(String(c).includes('DevTools listening on')){clearTimeout(t);resolve()}})});
  const target=await(await fetch('http://127.0.0.1:51736/json/new?about:blank',{method:'PUT'})).json();const ws=new WebSocket(target.webSocketDebuggerUrl);await new Promise((r,j)=>{ws.onopen=r;ws.onerror=j});
  let id=0;const pending=new Map(),events=new Map();ws.onmessage=e=>{const m=JSON.parse(e.data);if(m.id){const p=pending.get(m.id);pending.delete(m.id);m.error?p.reject(Error(m.error.message)):p.resolve(m.result)}else if(events.has(m.method)){events.get(m.method)();events.delete(m.method)}};
  const send=(method,params={})=>new Promise((resolve,reject)=>{const n=++id;pending.set(n,{resolve,reject});ws.send(JSON.stringify({id:n,method,params}))});
  const evaluate=async expression=>{const r=await send('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(r.exceptionDetails)throw Error(r.exceptionDetails.text);return r.result.value};
  await send('Page.enable');const go=async url=>{const loaded=new Promise(r=>events.set('Page.loadEventFired',r));await send('Page.navigate',{url:base+url});await loaded};
  await go('/Account/Login');let loaded=new Promise(r=>events.set('Page.loadEventFired',r));await evaluate(`document.querySelector('[name=email]').value=${JSON.stringify(owner.email)};document.querySelector('[name=password]').value=${JSON.stringify(owner.password)};document.querySelector('#auth-form').requestSubmit()`);await loaded;
  for(const width of [1440,390,320]){await send('Emulation.setDeviceMetricsOverride',{width,height:900,deviceScaleFactor:1,mobile:width<600});await go('/WebsiteContent');const v=await evaluate(`({width:innerWidth,scroll:document.documentElement.scrollWidth,title:document.querySelector('h1').innerText,notice:document.querySelector('.website-content-notice').innerText})`);assert.equal(v.width,width);assert.ok(v.scroll<=width,`overflow ${v.scroll}`);assert.equal(v.title,'Website Content Management');assert.match(v.notice,/not available yet/);const shot=await send('Page.captureScreenshot',{format:'png'});await writeFile(path.join(temp,`editor-${width}.png`),Buffer.from(shot.data,'base64'));console.log(`PASS owner screen ${width}px`)}
  console.log(`Screenshots retained: ${temp}`);ws.close();
} finally { if(chrome)chrome.kill();app.kill(); }
