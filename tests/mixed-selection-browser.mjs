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
  await navigate('/Account/Login');
  await submit(`document.querySelector('[name=email]').value='customer@glambymariya.demo';document.querySelector('[name=password]').value='Customer123!';document.querySelector('#auth-form').requestSubmit()`);
  await navigate('/Bookings/Create');
  await evalJs(`document.querySelector('[name=bookingType][value=package]').click();document.querySelector('[name=people]').value='3';document.querySelector('[name=people]').dispatchEvent(new Event('input',{bubbles:true}));document.querySelector('#mixed-mode').click()`);
  assert.equal(await evalJs(`document.querySelectorAll('.mixed-person').length`),3);
  const outcome=await evalJs(`(async()=>{const f=document.querySelector('#mixed-selection');if(!f)throw Error('Mixed page missing');const wait=async()=>{for(let i=0;i<150;i++){if(document.querySelector('#mixed-status').textContent.includes('Estimate calculated'))return;await new Promise(r=>setTimeout(r,50))}throw Error('Quote failed: '+document.querySelector('#mixed-status').textContent)};const count=document.querySelector('[name=people]');count.value='3';count.dispatchEvent(new Event('input',{bubbles:true}));const cards=[...document.querySelectorAll('.mixed-person')];for(const [i,name] of ['Bridal Makeup','Glamour Makeup','Soft Glam'].entries()){const s=cards[i].querySelector('select');s.value=[...s.options].find(o=>o.textContent.includes(name)).value;s.dispatchEvent(new Event('change',{bubbles:true}))}cards[0].querySelector('.mixed-addons input').click();cards[1].querySelectorAll('.mixed-addons input')[1].click();await wait();const three=document.querySelector('#mixed-totals').textContent;const people=cards.map(x=>x.querySelector('.mixed-person-total').textContent);count.value='5';count.dispatchEvent(new Event('input',{bubbles:true}));const five=[...document.querySelectorAll('.mixed-person')];for(const card of five.slice(3)){const s=card.querySelector('select');s.value=s.options[1].value;s.dispatchEvent(new Event('change',{bubbles:true}))}await wait();const fiveTotal=document.querySelector('#mixed-totals').textContent;count.value='2';count.dispatchEvent(new Event('input',{bubbles:true}));await wait();const two=document.querySelector('#mixed-totals').textContent;count.value='1.5';count.dispatchEvent(new Event('input',{bubbles:true}));return {three,people,fiveTotal,two,countError:document.querySelector('#mixed-count-error').textContent,button:document.querySelector('[data-step-next]').disabled,status:document.querySelector('#mixed-estimate-inline').textContent}})()`);
  console.log('MIXED',JSON.stringify(outcome));
  assert.match(outcome.three,/3,900\.00/);assert.match(outcome.three,/1,950\.00/);assert.match(outcome.three,/330 min/);assert.equal(outcome.people.length,3);assert.match(outcome.fiveTotal,/5,100\.00/);assert.match(outcome.two,/3,300\.00/);assert.match(outcome.countError,/whole number/);assert.equal(outcome.button,true);
  const changed=await evalJs(`(async()=>{const count=document.querySelector('[name=people]');count.value='2';count.dispatchEvent(new Event('input',{bubbles:true}));const s=document.querySelector('.mixed-person select');s.value=[...s.options].find(o=>o.textContent.includes('Soft Glam')).value;s.dispatchEvent(new Event('change',{bubbles:true}));for(let i=0;i<150;i++){if(document.querySelector('#mixed-status').textContent.includes('Estimate calculated'))return document.querySelector('#mixed-totals').textContent;await new Promise(r=>setTimeout(r,50))}throw Error('Changed quote timeout')})()`);
  assert.match(changed,/2,400\.00/);
  const invalid=await evalJs(`(async()=>{const f=document.querySelector('#mixed-selection'),count=document.querySelector('[name=people]');count.value='2';count.dispatchEvent(new Event('input',{bubbles:true}));const s=document.querySelector('.mixed-person select');s.value='';s.dispatchEvent(new Event('change',{bubbles:true}));const error=s.closest('fieldset').querySelector('.mixed-error').textContent;const token=document.querySelector('#booking-form [name=__RequestVerificationToken]').value;const r=await fetch(f.dataset.quoteUrl,{method:'POST',headers:{'Content-Type':'application/json','RequestVerificationToken':token},body:JSON.stringify([{serviceId:'00000000-0000-0000-0000-000000000000',addOnIds:[]},{serviceId:'00000000-0000-0000-0000-000000000000',addOnIds:[]}])});return {error,invalid:s.getAttribute('aria-invalid'),status:r.status}})()`);
  assert.match(invalid.error,/Choose a makeup service/);assert.equal(invalid.invalid,'true');assert.equal(invalid.status,400);
  const guard=await evalJs(`(()=>{const f=document.querySelector('#booking-form');f.querySelector('a[href="#schedule"]').click();const step=f.dataset.currentStep;location.hash='#payment';f.dispatchEvent(new Event('submit',{bubbles:true,cancelable:true}));return {step,after:f.dataset.currentStep,message:f.querySelector('.booking-step-message').textContent,estimateVisible:!f.querySelector('#mixed-estimate-inline').hidden}})()`);
  assert.equal(guard.step,'look');assert.equal(guard.after,'look');assert.match(guard.message,/valid service/);assert.equal(guard.estimateVisible,true);
  for(const width of [1440,768,390,320]){await viewport(width);await evalJs(`document.querySelector('#mixed-selection').scrollIntoView()`);const geometry=await evalJs(`({width:innerWidth,scroll:document.documentElement.scrollWidth})`);assert.ok(geometry.scroll<=geometry.width);await shot(`mixed-${width}.png`)}
  const fiveAll=await evalJs(`(async()=>{
    const count=document.querySelector('[name=people]');count.value='5';count.dispatchEvent(new Event('input',{bubbles:true}));
    const cards=[...document.querySelectorAll('.mixed-person')];cards.forEach(c=>c.querySelectorAll('.mixed-addons input').forEach(a=>{a.checked=false}));
    for(const [i,name] of ['Soft Glam','Glamour Makeup','Bridal Makeup','Soft Glam','Glamour Makeup'].entries()) { const s=cards[i].querySelector('select');s.value=[...s.options].find(o=>o.textContent.includes(name)).value;s.dispatchEvent(new Event('change',{bubbles:true}));cards[i].querySelector('.mixed-addons input').click(); }
    for(let i=0;i<150;i++){if(document.querySelector('#mixed-status').textContent.includes('Estimate calculated'))return {total:document.querySelector('#mixed-totals').textContent,people:cards.map(c=>c.querySelector('.mixed-person-total').textContent)};await new Promise(r=>setTimeout(r,50))}throw Error('five person quote timeout');
  })()`);
  assert.match(fiveAll.total,/5,450\.00/);assert.match(fiveAll.total,/495 min/);assert.match(fiveAll.total,/2,725\.00/);assert.equal(fiveAll.people.filter(p=>p.includes('250.00')).length,5);
  console.log('PASS five distinct per-person service and add-on selections',fiveAll.total);
  assert.equal(await evalJs(`document.querySelector('#mixed-totals').getAnimations().length`),0);
  await send('Emulation.setEmulatedMedia',{features:[{name:'prefers-reduced-motion',value:'no-preference'}]});
  const motion=await evalJs(`(async()=>{document.querySelector('.mixed-addons input').click();for(let i=0;i<150;i++){if(document.querySelector('#mixed-status').textContent.includes('Estimate calculated'))return {animations:document.querySelector('#mixed-totals').getAnimations().length,total:document.querySelector('#mixed-totals').textContent};await new Promise(r=>setTimeout(r,10))}throw Error('motion quote timeout')})()`);
  assert.equal(motion.animations,1);assert.match(motion.total,/5,200\.00/);
  await send('Emulation.setEmulatedMedia',{features:[{name:'prefers-reduced-motion',value:'reduce'}]});
  const reduced=await evalJs(`(async()=>{document.querySelector('.mixed-addons input').click();for(let i=0;i<150;i++){if(document.querySelector('#mixed-status').textContent.includes('Estimate calculated'))return {animations:document.querySelector('#mixed-totals').getAnimations().length,total:document.querySelector('#mixed-totals').textContent};await new Promise(r=>setTimeout(r,10))}throw Error('reduced quote timeout')})()`);
  assert.equal(reduced.animations,0);assert.match(reduced.total,/5,450\.00/);
  for(const width of [1440,768,390,320]){await viewport(width);await evalJs(`document.querySelector('#mixed-estimate-inline').scrollIntoView({block:'center'})`);await shot(`inline-${width}-valid-estimate.png`)}
  console.log('PASS price-update feedback and reduced-motion alternative');
  const switches=await evalJs(`(async()=>{
    const f=document.querySelector('#booking-form'),mode=f.querySelector('#mixed-mode'),person=f.elements.people;
    const wait=async()=>{for(let i=0;i<150;i++){if(f.dataset.priceState==='ready')return;await new Promise(r=>setTimeout(r,50))}throw Error('legacy quote timeout '+document.querySelector('#pricing-status').textContent)};
    const snapshots=[];
    for(let n=0;n<3;n++){
      mode.click();await wait();
      snapshots.push({where:'same',mixedCards:f.querySelectorAll('.mixed-person').length,hidden:f.querySelectorAll('#addon-values input').length,total:f.elements.expectedTotal.value,qty:[...f.querySelectorAll('.addon-quantity')].map(x=>x.value)});
      const a=f.querySelector('.addon-quantity');a.value='1';a.dispatchEvent(new Event('input',{bubbles:true}));await wait();snapshots.push({where:'sameAddon',total:f.elements.expectedTotal.value,hidden:f.querySelectorAll('#addon-values input').length});
      mode.click();snapshots.push({where:'mixed',hidden:f.querySelectorAll('#addon-values input').length,total:f.elements.expectedTotal.value,qty:[...f.querySelectorAll('.addon-quantity')].map(x=>x.value),mixedCards:f.querySelectorAll('.mixed-person').length,selected:f.querySelectorAll('.mixed-service option:checked:not([value=""])').length});
      f.querySelector('[name=bookingType][value=individual]').click();await wait();snapshots.push({where:'individual',mixedCards:f.querySelectorAll('.mixed-person').length,hidden:f.querySelectorAll('#addon-values input').length,total:f.elements.expectedTotal.value,qty:[...f.querySelectorAll('.addon-quantity')].map(x=>x.value)});
      f.querySelector('[name=bookingType][value=package]').click();await wait(); mode.click();
    }
    return snapshots;
  })()`);
  for(const s of switches){if(s.where==='sameAddon'){assert.equal(s.hidden,1);assert.equal(s.total,'2875.00');continue}assert.equal(s.hidden,0,JSON.stringify(s));assert.ok(s.qty.every(q=>q==='0'),JSON.stringify(s));if(s.where==='mixed'){assert.equal(s.total,'');assert.equal(s.selected,0)}else {assert.equal(s.mixedCards,0);assert.equal(s.total,s.where==='same'?'2625.00':'600.00')}}
  console.log('PASS three mixed / same-service / individual switch cycles');
  await navigate('/Bookings/Create');
  await evalJs(`document.querySelector('[name=bookingType][value=package]').click();document.querySelector('[name=people]').value='2';document.querySelector('[name=people]').dispatchEvent(new Event('input',{bubbles:true}));document.querySelector('#mixed-mode').click()`);
  const labels=await evalJs(`(()=>{const f=document.querySelector('#booking-form'), fields=[f.elements.people,f.querySelector('#mixed-mode'),...f.querySelectorAll('#mixed-selection input, #mixed-selection select')];return {unlabelled:fields.filter(x=>!x.labels?.length && !x.getAttribute('aria-label')).map(x=>x.outerHTML),countDescribed:f.elements.people.getAttribute('aria-describedby'),services:[...f.querySelectorAll('.mixed-service')].map(x=>({description:x.getAttribute('aria-describedby'),invalid:x.getAttribute('aria-invalid'),error:document.getElementById(x.getAttribute('aria-describedby'))?.textContent})),status:document.querySelector('#mixed-status').getAttribute('role'),countRole:document.querySelector('#mixed-count-error').getAttribute('role') }})()`);
  assert.deepEqual(labels.unlabelled,[]);assert.match(labels.countDescribed,/mixed-count-error/);assert.ok(labels.services.every(x=>x.invalid==='true' && x.error.includes('Choose a makeup service')));assert.equal(labels.status,'status');assert.equal(labels.countRole,'status');
  const ax=await send('Accessibility.getFullAXTree');assert.ok(ax.nodes.some(n=>n.role?.value==='combobox' && n.name?.value?.includes('Makeup service')));assert.ok(ax.nodes.some(n=>n.role?.value==='checkbox' && n.name?.value?.includes('Lash application')));
  console.log('PASS labels, live error associations and Chromium accessibility tree');
  for(const width of [1440,768,390,320]){
    await viewport(width);
    for(const [name,selector] of [['top','.booking-progress'],['cards','#mixed-selection'],['actions','.booking-step-actions']]){await evalJs(`document.querySelector('${selector}').scrollIntoView({block:'start'})`);await shot(`inline-${width}-${name}.png`)}
    const layout=await evalJs(`(()=>{const w=document.documentElement.clientWidth;return {scroll:document.documentElement.scrollWidth,width:w,bad:[...document.querySelectorAll('#mixed-selection input,#mixed-selection select,#mixed-estimate-inline')].filter(e=>{let r=e.getBoundingClientRect();return r.width>0&&(r.left < -1 || r.right > w+1)}).length}})()`);
    assert.ok(layout.scroll<=layout.width,JSON.stringify(layout));assert.equal(layout.bad,0,JSON.stringify(layout));
  }
  console.log('PASS complete Step 1 screenshots and control bounds at four widths');
  console.log('PASS browser mixed quote and responsive sizes',temp);
} finally { if(chrome && chrome.exitCode===null) chrome.kill(); if(server.exitCode===null) server.kill(); }
