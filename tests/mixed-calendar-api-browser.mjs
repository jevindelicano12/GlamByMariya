// Read-only browser checks against a synthetic in-memory Demo; no user data.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdir, mkdtemp, writeFile } from 'node:fs/promises';
import path from 'node:path';

const project = path.resolve('.');
const parent = path.join(process.env.LOCALAPPDATA, 'Temp', 'opencode');
await mkdir(parent, { recursive: true });
const temp = await mkdtemp(path.join(parent, 'booking-ui-audit-'));
const binary = process.env.UI_CHECK_BUILD || path.join(project, 'Frontend/GlamByMariya.Web/bin/Release/net10.0');
const server = spawn('dotnet', [path.join(binary, 'GlamByMariya.Web.dll'), '--urls=http://127.0.0.1:0', `--contentRoot=${temp}`,
  `--webroot=${path.join(project, 'Frontend/GlamByMariya.Web/wwwroot')}`, '--Database:Provider=Demo', '--DemoAccounts:Presentation=false',
  '--Authentication:DurableSessionEnforcement=false', '--DemoGallery:Enabled=false', '--DemoCatalog:Enabled=false',
  '--WebsiteContent:DemoTextEditing=false', '--WebsiteContent:DemoPersistentText=false', '--Supabase:Url=', '--Supabase:AnonKey=',
  '--Supabase:ServiceKey=', '--SupabaseStorage:ServiceKey=', '--FileStorage:Provider=Local', '--B2:Bucket=', '--B2:Endpoint=',
  '--B2:KeyId=', '--B2:AppKey=', '--Email:BrevoApiKey=', '--Email:SmtpHost='],
{ env: { ...process.env, PORT:'', ASPNETCORE_ENVIRONMENT:'Development', DOTNET_ENVIRONMENT:'Development' }, stdio:['ignore','pipe','pipe'] });
let chrome;
try {
  const base = await new Promise((resolve,reject) => {
    const timer=setTimeout(()=>reject(Error('Demo startup timed out')),30000);
    server.on('error',reject); server.once('exit',code=>reject(Error(`Demo exited ${code}`))); server.stderr.resume();
    server.stdout.on('data',chunk=>{ const m=String(chunk).match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/); if(m){clearTimeout(timer);resolve(m[1]);} });
  });
  chrome=spawn('C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', ['--headless=new','--disable-gpu','--no-first-run','--disable-backgrounding-occluded-windows','--disable-renderer-backgrounding',
    '--remote-debugging-port=0',`--user-data-dir=${path.join(temp,'chrome')}`,'about:blank'],{stdio:['ignore','ignore','pipe']});
  const port=await new Promise((resolve,reject)=>{const timer=setTimeout(()=>reject(Error('Chrome startup timed out')),30000);chrome.once('exit',()=>reject(Error('Chrome exited')));chrome.stderr.on('data',chunk=>{const m=String(chunk).match(/DevTools listening on ws:\/\/127\.0\.0\.1:(\d+)\//);if(m){clearTimeout(timer);resolve(m[1]);}})});
  const target=await (await fetch(`http://127.0.0.1:${port}/json/new?about:blank`,{method:'PUT'})).json();
  const ws=new WebSocket(target.webSocketDebuggerUrl); await new Promise((resolve,reject)=>{ws.onopen=resolve;ws.onerror=reject});
  let sequence=0; const pending=new Map(), events=new Map();
  ws.onmessage=e=>{const m=JSON.parse(e.data);if(m.id){const p=pending.get(m.id);pending.delete(m.id);m.error?p.reject(Error(m.error.message)):p.resolve(m.result)}else if(events.has(m.method)){events.get(m.method)();events.delete(m.method)}};
  const send=(method,params={})=>new Promise((resolve,reject)=>{const id=++sequence;pending.set(id,{resolve,reject});ws.send(JSON.stringify({id,method,params}))});
  const evalJs=async expression=>{const r=await send('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(r.exceptionDetails)throw Error(r.exceptionDetails.exception?.description || r.exceptionDetails.text);return r.result.value};
  await send('Page.enable');
  await send('Emulation.setEmulatedMedia',{features:[{name:'prefers-reduced-motion',value:'reduce'}]});
  const navigate=async url=>{const loaded=new Promise(r=>events.set('Page.loadEventFired',r));await send('Page.navigate',{url:base+url});await loaded};
  const submit=async expression=>{const loaded=new Promise(r=>events.set('Page.loadEventFired',r));await evalJs(expression);await loaded};
  const viewport=async width=>send('Emulation.setDeviceMetricsOverride',{width,height:900,deviceScaleFactor:1,mobile:width<600});
  const shot=async name=>{const image=await send('Page.captureScreenshot',{format:'png'});await writeFile(path.join(temp,name),Buffer.from(image.data,'base64'));};
  const luminance = rgb => { const c=rgb.match(/\d+/g).slice(0,3).map(Number).map(x=>{const v=x/255;return v<=.04045?v/12.92:((v+.055)/1.055)**2.4});return c[0]*.2126+c[1]*.7152+c[2]*.0722; };
  const anonymous=await fetch(base+'/Bookings/MixedAvailability',{method:'POST',headers:{'content-type':'application/json'},body:'{}',redirect:'manual'});
  assert.notEqual(anonymous.status,200);
  await navigate('/Account/Login');
  await submit(`document.querySelector('[name=email]').value='customer@glambymariya.demo';document.querySelector('[name=password]').value='Customer123!';document.querySelector('#auth-form').requestSubmit()`);
  await navigate('/Bookings/Create');
  const result=await evalJs(`(async()=>{
    const f=document.querySelector('#booking-form'),token=f.querySelector('[name=__RequestVerificationToken]').value;
    const services=[...f.querySelectorAll('[name=serviceId]')];
    const people=services.slice(0,3).map(s=>({serviceId:s.value,addOnIds:[]}));
    const post=async(path,body,csrf=true)=>{const r=await fetch('/Bookings/'+path,{method:'POST',headers:{'content-type':'application/json',...(csrf?{'RequestVerificationToken':token}:{})},body:JSON.stringify(body)});return {status:r.status,data:r.headers.get('content-type')?.includes('json')?await r.json():null}};
    const today=document.querySelector('.availability-picker').dataset.today;
    const calendar=await post('MixedAvailability',{people,month:today.slice(0,7)});
    const day=calendar.data.days.find(d=>d.date>today&&d.availableCount>0),slot=day?.slots.find(s=>s.available);
    if(!slot)throw Error('No available slot in isolated demo');
    const valid=await post('MixedValidateSlot',{people,start:slot.start});
    const invalid=await post('MixedValidateSlot',{people,start:day.date+'T00:00'});
    const alternatives=await post('MixedAlternatives',{people,after:today});
    const bad=await post('MixedAvailability',{people:people.slice(0,1),month:today.slice(0,7)});
    const noCsrf=await post('MixedAvailability',{people,month:today.slice(0,7)},false);
    return {calendar:calendar.status,duration:calendar.data.durationMinutes,buffer:calendar.data.bufferMinutes,valid,invalid,alternatives,bad:bad.status,noCsrf:noCsrf.status};
  })()`);
  assert.equal(result.calendar,200);assert.equal(result.duration,270);assert.ok(result.buffer>=0);
  assert.equal(result.valid.data.available,true);assert.equal(result.valid.data.quote.durationMinutes,270);
  assert.equal(result.invalid.data.available,false);assert.equal(result.alternatives.status,200);assert.ok(result.alternatives.data.dates.length<=3);assert.ok(result.alternatives.data.dates.every(d=>d.slots.some(s=>s.available)));
  assert.equal(result.bad,400);assert.equal(result.noCsrf,400);
  console.log('PASS mixed calendar, slot validation, alternatives, invalid party and CSRF',JSON.stringify(result));
} finally { if(chrome && chrome.exitCode===null) chrome.kill(); if(server.exitCode===null) server.kill(); }