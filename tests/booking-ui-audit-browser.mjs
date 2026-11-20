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
  chrome=spawn('C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', ['--headless=new','--disable-gpu','--no-first-run',
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
  const widths=[320,375,390,414,430,768,1024,1440,1920];
  const shot=async name=>{const image=await send('Page.captureScreenshot',{format:'png'});await writeFile(path.join(temp,name),Buffer.from(image.data,'base64'));};
  const luminance = rgb => { const c=rgb.match(/\d+/g).slice(0,3).map(Number).map(x=>{const v=x/255;return v<=.04045?v/12.92:((v+.055)/1.055)**2.4});return c[0]*.2126+c[1]*.7152+c[2]*.0722; };
  await navigate('/Account/Login');
  await submit(`document.querySelector('[name=email]').value='owner@glambymariya.demo';document.querySelector('[name=password]').value='Owner123!';document.querySelector('#auth-form').requestSubmit()`);
  await navigate('/Admin/Settings');
  const settings = await evalJs(`(async()=>{const token=document.querySelector('input[name=__RequestVerificationToken]').value;const body=new URLSearchParams({__RequestVerificationToken:token,earlyFeeEnabled:'true',earlyStartHour:'5',earlyFee:'123',lateFeeEnabled:'true',lateStartHour:'17',lateEndHour:'23',lateFee:'234'});const r=await fetch('/Admin/FeeSettings',{method:'POST',body});return r.ok})()`);
  assert.equal(settings,true);
  await submit(`document.querySelector('form[action="/Account/Logout"]').requestSubmit()`);
  await submit(`document.querySelector('[name=email]').value='customer@glambymariya.demo';document.querySelector('[name=password]').value='Customer123!';document.querySelector('#auth-form').requestSubmit()`);
  await navigate('/Bookings/Create');
  await evalJs(`new Promise((resolve,reject)=>{const f=document.querySelector('#booking-form');if(f.dataset.priceState==='ready')return resolve();const t=setTimeout(()=>reject(Error('Quote timeout')),10000);f.addEventListener('booking-price-updated',()=>{clearTimeout(t);resolve()},{once:true})})`);
  const initial = await evalJs(`(()=>{const f=document.querySelector('#booking-form');return {start:f.elements.start.value,total:f.elements.expectedTotal.value,minimum:f.elements.expectedMinimum.value,legacy:!!f.querySelector('[data-enhanced-legacy]')}})()`);
  assert.equal(initial.start,''); assert.ok(Number(initial.total)>0 && Number(initial.minimum)>0);
  for(const width of [1440,390]) {
    await viewport(width);
    const navigation=await evalJs(`(()=>{const f=document.querySelector('#booking-form');f.querySelector('[data-step-next]').click();const next=f.dataset.currentStep;f.querySelector('[data-step-back]').click();return {next,back:f.dataset.currentStep,total:f.elements.expectedTotal.value}})()`);
    assert.deepEqual(navigation,{next:'schedule',back:'look',total:initial.total});
  }
  console.log('PASS individual initial server quote and Next/Back without a date at desktop and mobile');
  const packageError = await evalJs(`(async()=>{
    const f=document.querySelector('#booking-form'),find=name=>[...f.querySelectorAll('[name=serviceId]')].find(x=>x.dataset.name===name);
    find('Glamour Makeup').click();f.querySelector('[name=bookingType][value=package]').click();
    f.elements.people.value='5';f.elements.people.dispatchEvent(new Event('input',{bubbles:true}));
    const q=[...f.querySelectorAll('.addon-quantity')].find(x=>/hair/i.test(x.dataset.name));q.value='1';q.dispatchEvent(new Event('input',{bubbles:true}));
    for(let i=0;i<200&&f.dataset.priceState!=='error';i++)await new Promise(r=>setTimeout(r,50));
    const params=new URLSearchParams({serviceId:find('Glamour Makeup').value,people:'5',package:'group'});params.append('addOnIds',q.dataset.addonId);
    const response=await fetch(f.dataset.quoteUrl+'?'+params);return {state:f.dataset.priceState,status:response.status,server:await response.json(),message:document.querySelector('#pricing-status').textContent,next:f.querySelector('[data-step-next]').disabled,addon:f.querySelector('.addon-total').textContent,id:find('Glamour Makeup').value};
  })()`);
  assert.equal(packageError.state,'error');assert.equal(packageError.status,400);assert.match(packageError.server.error,/no.*package rate|does not have a package rate/i);assert.equal(packageError.next,true);
  assert.match(packageError.message,/Glamour Makeup.*Individual Booking.*service with a group rate.*contact the artist/i);assert.doesNotMatch(packageError.message,/refresh/i);assert.match(packageError.addon,/valid booking price/i);
  for(const width of [1440,390]){await viewport(width);const bounds=await evalJs(`(async()=>{const p=document.querySelector('#pricing-status');await new Promise(r=>setTimeout(r,400));p.scrollIntoView({block:'center',behavior:'instant'});await new Promise(r=>setTimeout(r,200));const b=p.getBoundingClientRect();return {right:b.right,top:b.top,bottom:b.bottom,height:innerHeight,viewport:innerWidth,scroll:document.documentElement.scrollWidth,visible:b.height>0}})()`);assert.ok(bounds.visible&&bounds.top>=0&&bounds.bottom<=bounds.height&&bounds.right<=bounds.viewport&&bounds.scroll<=bounds.viewport,JSON.stringify(bounds));await shot(`package-unavailable-${width}.png`)}
  const recovery=await evalJs(`(async()=>{const f=document.querySelector('#booking-form'),find=name=>[...f.querySelectorAll('[name=serviceId]')].find(x=>x.dataset.name===name),wait=async()=>{for(let i=0;i<200&&f.dataset.priceState!=='ready';i++)await new Promise(r=>setTimeout(r,50));if(f.dataset.priceState!=='ready')throw Error('Recovery quote timeout')};find('Soft Glam').click();await wait();const soft={total:f.elements.expectedTotal.value,addon:f.querySelector('.addon-total').textContent,disabled:f.querySelector('[data-step-next]').disabled};const params=new URLSearchParams({serviceId:find('Soft Glam').value,people:'5',package:'group'});params.append('addOnIds',[...f.querySelectorAll('.addon-quantity')].find(x=>/hair/i.test(x.dataset.name)).dataset.addonId);const quote=await (await fetch(f.dataset.quoteUrl+'?'+params)).json();f.querySelector('[data-step-next]').click();soft.step=f.dataset.currentStep;f.querySelector('[data-step-back]').click();find('Glamour Makeup').click();f.querySelector('[name=bookingType][value=individual]').click();await wait();const individual={total:f.elements.expectedTotal.value,disabled:f.querySelector('[data-step-next]').disabled};return {soft,quote,individual}})()`);
  assert.deepEqual(recovery.soft,{total:'3425.00',addon:'Add-on total: ₱800.00',disabled:false,step:'schedule'});assert.equal(recovery.quote.groupDiscount,375);assert.equal(recovery.quote.extrasTotal,800);assert.equal(recovery.quote.appliedPricePerPerson,525);
  assert.deepEqual(recovery.individual,{total:'750.00',disabled:false});
  console.log('PASS missing Glamour group rate explains valid alternatives; Soft Glam package and Glamour individual recover');
  await submit(`document.querySelector('form[action="/Account/Logout"]').requestSubmit()`);
  await submit(`document.querySelector('[name=email]').value='owner@glambymariya.demo';document.querySelector('[name=password]').value='Owner123!';document.querySelector('#auth-form').requestSubmit()`);
  await navigate('/Admin/Catalog');
  const configured=await evalJs(`(async()=>{const form=[...document.querySelectorAll('form[action="/Admin/SaveGroupPricing"]')].find(x=>x.querySelector('[name=id]').value==='${packageError.id}');if(!form)throw Error('Synthetic Glamour pricing form missing');const body=new URLSearchParams(new FormData(form));body.set('groupPrice','700');body.set('groupMinimumPeople','4');const response=await fetch(form.action,{method:'POST',body});return response.ok})()`);
  assert.equal(configured,true);
  await submit(`document.querySelector('form[action="/Account/Logout"]').requestSubmit()`);
  await submit(`document.querySelector('[name=email]').value='customer@glambymariya.demo';document.querySelector('[name=password]').value='Customer123!';document.querySelector('#auth-form').requestSubmit()`);
  await navigate('/Bookings/Create');
  const configuredQuote=await evalJs(`(async()=>{const f=document.querySelector('#booking-form'),wait=async()=>{for(let i=0;i<200&&f.dataset.priceState!=='ready';i++)await new Promise(r=>setTimeout(r,50));if(f.dataset.priceState!=='ready')throw Error('Configured quote timeout')};await wait();const g=[...f.querySelectorAll('[name=serviceId]')].find(x=>x.dataset.name==='Glamour Makeup');g.click();f.querySelector('[name=bookingType][value=package]').click();f.elements.people.value='5';f.elements.people.dispatchEvent(new Event('input',{bubbles:true}));const q=[...f.querySelectorAll('.addon-quantity')].find(x=>/hair/i.test(x.dataset.name));q.value='1';q.dispatchEvent(new Event('input',{bubbles:true}));await wait();const params=new URLSearchParams({serviceId:g.value,people:'5',package:'group'});params.append('addOnIds',q.dataset.addonId);const quote=await(await fetch(f.dataset.quoteUrl+'?'+params)).json();f.querySelector('[data-step-next]').click();return {quote,step:f.dataset.currentStep,display:f.elements.expectedTotal.value,addon:f.querySelector('.addon-total').textContent}})()`);
  assert.equal(configuredQuote.quote.appliedPricePerPerson,700);assert.equal(configuredQuote.quote.groupDiscount,250);assert.equal(configuredQuote.quote.extrasTotal,800);assert.equal(configuredQuote.display,'4300.00');assert.equal(configuredQuote.step,'schedule');assert.equal(configuredQuote.addon,'Add-on total: ₱800.00');
  await evalJs(`(async()=>{const f=document.querySelector('#booking-form');f.querySelector('[data-step-back]').click();f.querySelector('[name=bookingType][value=individual]').click();const q=[...f.querySelectorAll('.addon-quantity')].find(x=>/hair/i.test(x.dataset.name));q.value='0';q.dispatchEvent(new Event('input',{bubbles:true}));[...f.querySelectorAll('[name=serviceId]')].find(x=>x.dataset.name==='Soft Glam').click();for(let i=0;i<200&&f.dataset.priceState!=='ready';i++)await new Promise(r=>setTimeout(r,50));if(f.dataset.priceState!=='ready')throw Error('Reset quote timeout')})()`);
  console.log('PASS synthetic owner-configured group rate produces correct discount, add-on total and Next');
  const finishPolicy=await evalJs(`document.body.textContent.includes('a package starting during regular hours finishes makeup after')`);
  if(finishPolicy) {
    const boundary=await evalJs(`(async()=>{const f=document.querySelector('#booking-form'),p=f.querySelector('.availability-picker'),wait=async(fn,label)=>{for(let i=0;i<240;i++){if(fn())return;await new Promise(r=>setTimeout(r,50))}throw Error(label)};f.querySelector('[name=bookingType][value=package]').click();f.elements.people.value='5';f.elements.people.dispatchEvent(new Event('input',{bubbles:true}));await wait(()=>f.dataset.priceState==='ready','group quote');f.querySelector('[data-step-next]').click();p.querySelector('[data-month-next]').click();const t=p.dataset.today,d=new Date(Number(t.slice(0,4)),Number(t.slice(5,7)),15),date=d.getFullYear()+'-'+String(d.getMonth()+1).padStart(2,'0')+'-15';await wait(()=>p.querySelector('[data-date="'+date+'"]:not([disabled])'),'date');p.querySelector('[data-date="'+date+'"]').click();const results=[];for(const time of ['13:00','13:15']){const start=date+'T'+time;await wait(()=>p.querySelector('[data-start="'+start+'"]:not([disabled])'),'slot '+time);p.querySelector('[data-start="'+start+'"]').click();await wait(()=>f.dataset.priceState==='ready'&&f.elements.start.value===start&&p.querySelector('[data-validation-status]').dataset.state==='valid','quote '+time);results.push({late:document.querySelector('#summary-late-fee').textContent,total:f.elements.expectedTotal.value,minimum:f.elements.expectedMinimum.value})}return results})()`);
    assert.match(boundary[0].late,/0\.00/);assert.match(boundary[1].late,/234\.00/);assert.equal(Number(boundary[1].total),Number(boundary[0].total)+234);assert.equal(Number(boundary[1].minimum),Number(boundary[1].total)/2);
    for(const width of [1440,390,320]){await viewport(width);const layout=await evalJs(`(()=>({width:innerWidth,scroll:document.documentElement.scrollWidth,help:[...document.querySelectorAll('.summary .help')].some(x=>x.textContent.includes('finishes makeup after'))}))()`);assert.ok(layout.scroll<=layout.width&&layout.help)}
    await evalJs(`(async()=>{const f=document.querySelector('#booking-form');f.querySelector('[data-month-prev]').click();f.querySelector('[data-step-back]').click();f.querySelector('[name=bookingType][value=individual]').click();for(let i=0;i<240&&f.dataset.priceState!=='ready';i++)await new Promise(r=>setTimeout(r,50));if(f.dataset.priceState!=='ready')throw Error('Boundary reset failed')})()`);
    console.log('PASS package makeup finishing at/after regular closing updates server quote at desktop and mobile');
  }
  const addonChecks = await evalJs(`(async()=>{
    const f=document.querySelector('#booking-form'),next=f.querySelector('[data-step-next]');
    const wait=async()=>{for(let i=0;i<200;i++){if(f.dataset.priceState==='ready')return;await new Promise(r=>setTimeout(r,50))}throw Error('Add-on quote timeout: '+f.dataset.priceState)};
    const input=(el,value)=>{el.value=value;el.dispatchEvent(new Event('input',{bubbles:true}))};
    const snapshot=q=>({value:q.value,max:q.max,invalid:q.getAttribute('aria-invalid'),error:q.closest('.choice').querySelector('.addon-error').textContent,described:q.getAttribute('aria-describedby'),disabled:next.disabled,up:q.closest('.addon-quantity-control').querySelector('[data-quantity-step=up]').disabled,down:q.closest('.addon-quantity-control').querySelector('[data-quantity-step=down]').disabled,state:f.dataset.priceState,total:f.elements.expectedTotal.value,line:q.closest('.choice').querySelector('.addon-subtotal').textContent,combined:f.querySelector('.addon-total').textContent});
    const hair=[...f.querySelectorAll('.addon-quantity')].find(q=>/hair/i.test(q.dataset.name));
    const touch=[...f.querySelectorAll('.addon-quantity')].find(q=>/touch/i.test(q.dataset.name));
    f.querySelector('[name=bookingType][value=package]').click();input(f.elements.people,'5');await wait();
    input(hair,'5');await wait();input(touch,'5');await wait();const valid=snapshot(hair);
    input(hair,'6');const over=snapshot(hair);
    const params=new URLSearchParams({serviceId:f.querySelector('[name=serviceId]:checked').value,people:'5',package:'group'});for(let i=0;i<6;i++)params.append('addOnIds',hair.dataset.addonId);const server=await fetch(f.dataset.quoteUrl+'?'+params);const serverError=await server.json();
    input(hair,'5');await wait();const corrected=snapshot(hair);
    input(f.elements.people,'3');const reduced={hair:snapshot(hair),touch:snapshot(touch)};
    input(hair,'0');input(touch,'0');await wait();const zero=snapshot(hair);
    f.querySelector('[name=bookingType][value=individual]').click();await wait();input(hair,'2');const individual=snapshot(hair);
    input(hair,'');const empty=snapshot(hair);input(hair,'1.5');const decimal=snapshot(hair);input(hair,'-1');const negative=snapshot(hair);
    input(hair,'0');await wait();const restored=snapshot(hair);return {valid,over,serverStatus:server.status,serverError:serverError.error,corrected,reduced,zero,individual,empty,decimal,negative,restored};
  })()`);
  assert.equal(addonChecks.valid.total,'8375.00');assert.equal(addonChecks.valid.up,true);assert.match(addonChecks.valid.line,/4,000\.00/);assert.match(addonChecks.valid.combined,/5,750\.00/);
  assert.equal(addonChecks.over.value,'6');assert.equal(addonChecks.over.invalid,'true');assert.equal(addonChecks.over.disabled,true);assert.match(addonChecks.over.error,/selected 6 Hair styling.*only 5 people.*5 or fewer/i);assert.match(addonChecks.over.described,/addon-error/);
  assert.equal(addonChecks.serverStatus,400);assert.match(addonChecks.serverError,/quantity cannot exceed/i);
  assert.equal(addonChecks.corrected.invalid,'false');assert.equal(addonChecks.corrected.disabled,false);assert.equal(addonChecks.corrected.total,'8375.00');
  assert.equal(addonChecks.reduced.hair.invalid,'true');assert.equal(addonChecks.reduced.touch.invalid,'true');assert.equal(addonChecks.reduced.hair.value,'5');assert.equal(addonChecks.zero.disabled,false);assert.equal(addonChecks.zero.down,true);
  for(const invalid of [addonChecks.individual,addonChecks.empty,addonChecks.decimal,addonChecks.negative]) { assert.equal(invalid.invalid,'true');assert.equal(invalid.disabled,true); }
  assert.equal(addonChecks.restored.disabled,false);assert.equal(addonChecks.restored.total,initial.total);
  console.log('PASS quantity limits, immediate inline errors, server rejection, subtotals and quote recovery');
  await evalJs(`(()=>{const f=document.querySelector('#booking-form');f.querySelector('[name=bookingType][value=package]').click();f.elements.people.value='5';f.elements.people.dispatchEvent(new Event('input',{bubbles:true}));const q=[...f.querySelectorAll('.addon-quantity')].find(x=>/hair/i.test(x.dataset.name));q.value='6';q.dispatchEvent(new Event('input',{bubbles:true}));q.closest('.choice').scrollIntoView({block:'center'})})()`);
  for (const width of [1440,390]) {
    await viewport(width);
    const errorLayout=await evalJs(`(()=>{const q=[...document.querySelectorAll('.addon-quantity')].find(x=>/hair/i.test(x.dataset.name)),card=q.closest('.choice'),e=card.querySelector('.addon-error');card.scrollIntoView({block:'center'});const r=e.getBoundingClientRect(),description=(card.querySelector('.addon-description')||card.querySelector(':scope > span')).getBoundingClientRect(),control=q.closest('.addon-quantity-control').getBoundingClientRect();return {visible:r.width>0&&r.height>0,right:r.right,viewport:innerWidth,scroll:document.documentElement.scrollWidth,size:parseFloat(getComputedStyle(e).fontSize),descriptionBottom:description.bottom,controlTop:control.top}})()`);
    assert.ok(errorLayout.visible && errorLayout.right<=errorLayout.viewport && errorLayout.scroll<=errorLayout.viewport && errorLayout.size>=14);
    if(width<580)assert.ok(errorLayout.controlTop>=errorLayout.descriptionBottom,'mobile quantity controls do not overlap add-on description');
    await shot(`addon-invalid-${width}.png`);
  }
  await evalJs(`(async()=>{const f=document.querySelector('#booking-form'),q=[...f.querySelectorAll('.addon-quantity')].find(x=>/hair/i.test(x.dataset.name));q.value='0';q.dispatchEvent(new Event('input',{bubbles:true}));f.querySelector('[name=bookingType][value=individual]').click();for(let i=0;i<200&&f.dataset.priceState!=='ready';i++)await new Promise(r=>setTimeout(r,50));if(f.dataset.priceState!=='ready')throw Error('Reset quote timeout')})()`);
  for(const width of widths) {
    await viewport(width);
    const switched = await evalJs(`(async()=>{
      const f=document.querySelector('#booking-form'),p=f.querySelector('.availability-picker'),inputs=[...f.querySelectorAll('.addon-quantity')];
      const wait=async(fn,label)=>{for(let i=0;i<240;i++){if(fn())return;await new Promise(r=>setTimeout(r,50))}throw Error(label+': '+f.dataset.priceState+' '+p.querySelector('[data-calendar-status]').textContent)};
      const change=(el,value)=>{el.value=value;el.dispatchEvent(new Event('input',{bubbles:true}))};
      f.querySelector('[name=bookingType][value=package]').click();change(f.elements.people,'5');inputs.forEach(q=>change(q,'4'));
      await wait(()=>f.dataset.priceState==='ready'&&p.querySelector('[data-duration-label]').textContent.includes('540 min'), 'group quote/calendar');
      const group={people:f.elements.people.value,values:inputs.map(q=>q.value),total:f.elements.expectedTotal.value,minutes:f.dataset.serviceMinutes,calendar:p.querySelector('[data-duration-label]').textContent};
      f.querySelector('[data-step-next]').click();await wait(()=>f.dataset.currentStep==='schedule','schedule');
      const day=p.querySelector('[data-calendar-days] button:not([disabled])');if(!day)throw Error('Long package has no available test date');day.click();await wait(()=>p.querySelector('[data-time-slots] button:not([disabled])'),'slot');p.querySelector('[data-time-slots] button:not([disabled])').click();
      await wait(()=>f.dataset.priceState==='ready'&&p.querySelector('[data-validation-status]').dataset.state==='valid'&&!f.querySelector('[data-step-next]').disabled,'slot quote');
      f.querySelector('[data-step-next]').click();const modal=document.querySelector('#travel-fee-modal');await wait(()=>!modal.hidden||f.dataset.currentStep==='payment','payment step');if(!modal.hidden){modal.querySelector('#travel-policy-ack').click();modal.querySelector('[data-travel-ack]').click()}
      const payment={step:f.dataset.currentStep,people:document.querySelector('#summary-people').textContent,duration:document.querySelector('#summary-duration').textContent,addons:document.querySelector('#summary-addons').textContent};
      f.querySelector('[data-step-back]').click();f.querySelector('[data-step-back]').click();
      f.querySelector('[name=bookingType][value=individual]').click();await wait(()=>f.dataset.priceState==='ready'&&p.querySelector('[data-duration-label]').textContent.includes('60 min'),'individual quote/calendar');
      const individual={people:f.elements.people.value,values:inputs.map(q=>q.value),max:inputs.map(q=>q.max),state:f.dataset.priceState,expected:f.elements.expectedTotal.value,minutes:f.dataset.serviceMinutes,start:f.elements.start.value,hidden:f.querySelectorAll('#addon-values [name=addOnIds]').length,notice:document.querySelector('.booking-type + .help').textContent};
      change(inputs[0],'1');await wait(()=>f.dataset.priceState==='ready','individual addon');f.querySelector('[name=bookingType][value=package]').click();
      const reenter={values:inputs.map(q=>q.value),people:f.elements.people.value};await wait(()=>f.dataset.priceState==='ready','package again');f.querySelector('[name=bookingType][value=individual]').click();await wait(()=>f.dataset.priceState==='ready','individual again');
      return {group,payment,individual,reenter,overflow:document.documentElement.scrollWidth>innerWidth};
    })()`);
    assert.deepEqual(switched.group,{people:'5',values:['4','4','4'],total:'8225.00',minutes:'540',calendar:'540 min session + 45 min buffer'});
    assert.equal(switched.payment.step,'payment');assert.equal(switched.payment.people,'5');assert.equal(switched.payment.duration,'540 min');assert.match(switched.payment.addons,/× 4/);
    assert.deepEqual(switched.individual,{people:'1',values:['0','0','0'],max:['1','1','1'],state:'ready',expected:'600.00',minutes:'60',start:'',hidden:0,notice:'Your add-ons were reset because you changed your booking type.'});
    assert.deepEqual(switched.reenter,{values:['0','0','0'],people:'5'});assert.equal(switched.overflow,false);
    console.log(`PASS ${width}px repeated type switches, availability and all three steps`);
  }
  const calendarMatrix=await evalJs(`(async()=>{const f=document.querySelector('#booking-form'),p=f.querySelector('.availability-picker'),inputs=[...f.querySelectorAll('.addon-quantity')],today=p.dataset.today,d=new Date(Number(today.slice(0,4)),Number(today.slice(5,7)),15),month=d.getFullYear()+'-'+String(d.getMonth()+1).padStart(2,'0');const query=async(people,pack,counts)=>{const args=new URLSearchParams({serviceId:f.querySelector('[name=serviceId]:checked').value,people:String(people),package:pack,month});inputs.forEach((q,i)=>{for(let n=0;n<(counts[i]||0);n++)args.append('addOnIds',q.dataset.addonId)});const response=await fetch('/Bookings/Availability?'+args),body=await response.json();if(!response.ok)throw Error(body.error);return {duration:body.durationMinutes,buffer:body.bufferMinutes,count:body.days.find(x=>x.date.endsWith('-15')).availableCount}};return [await query(1,'auto',[]),await query(1,'auto',[0,1]),await query(5,'group',[]),await query(5,'group',[0,1]),await query(5,'group',[4,4,4])]})()`);
  assert.deepEqual(calendarMatrix.map(x=>x.duration),[60,105,300,345,540]);assert.ok(calendarMatrix.every(x=>x.buffer===45));assert.ok(calendarMatrix.every((x,i)=>i===0||x.count<=calendarMatrix[i-1].count));
  console.log('PASS individual and five-person calendar durations and available slots match server');
  const tooLong = await evalJs(`(async()=>{
    const f=document.querySelector('#booking-form'),p=f.querySelector('.availability-picker'),wait=async fn=>{for(let i=0;i<240;i++){if(fn())return;await new Promise(r=>setTimeout(r,50))}throw Error('Long booking availability timeout')};
    f.querySelector('[name=bookingType][value=package]').click();f.elements.people.value='10';f.elements.people.dispatchEvent(new Event('input',{bubbles:true}));for(const q of f.querySelectorAll('.addon-quantity')){q.value='10';q.dispatchEvent(new Event('input',{bubbles:true}))}
    await wait(()=>f.dataset.priceState==='ready'&&p.querySelector('[data-calendar-status]').textContent.includes('No available dates'));
    const label=p.querySelector('[data-duration-label]').textContent,status=p.querySelector('[data-calendar-status]').textContent,validationStatus=p.querySelector('[data-validation-status]').textContent;
    const date=[...p.querySelectorAll('[data-calendar-days] button')].find(b=>b.dataset.date>=p.dataset.today);const title=date.title;
    const today=p.dataset.today,d=new Date(Number(today.slice(0,4)),Number(today.slice(5,7)),15),future=d.getFullYear()+'-'+String(d.getMonth()+1).padStart(2,'0')+'-15T05:00';const params=new URLSearchParams({serviceId:f.querySelector('[name=serviceId]:checked').value,people:'10',package:'group',start:future});f.querySelectorAll('#addon-values [name=addOnIds]').forEach(x=>params.append('addOnIds',x.value));const validation=await(await fetch('/Bookings/ValidateSlot?'+params)).json();
    return {label,status,title,validationStatus,validation};
  })()`);
  assert.equal(tooLong.label,'1200 min session + 45 min buffer');assert.match(tooLong.status,/No available dates.*1200-minute.*45-minute.*hours/i);assert.match(tooLong.title,/do not fit within booking hours/i);assert.match(tooLong.validationStatus,/Choose an available date/);assert.equal(tooLong.validation.available,false);assert.match(tooLong.validation.message,/finish by 23:00/);
  await evalJs(`document.querySelector('[data-step-next]').click()`);
  for(const width of widths) {
    await viewport(width);
    const display=await evalJs(`(async()=>{const e=document.querySelector('[data-calendar-status]');await new Promise(r=>setTimeout(r,300));e.scrollIntoView({block:'center',behavior:'instant'});await new Promise(r=>setTimeout(r,150));const b=e.getBoundingClientRect(),days=[...document.querySelectorAll('[data-calendar-days] .calendar-day')];return {step:document.querySelector('#booking-form').dataset.currentStep,top:b.top,bottom:b.bottom,height:innerHeight,right:b.right,width:innerWidth,scroll:document.documentElement.scrollWidth,spans:days.filter(d=>getComputedStyle(d).gridColumnEnd!=='auto').length,overflow:days.filter(d=>d.querySelector('small').scrollWidth>d.clientWidth).length}})()`);
    assert.equal(display.step,'schedule');assert.ok(display.top>=0&&display.bottom<=display.height&&display.right<=display.width&&display.scroll<=display.width&&display.spans===0&&display.overflow===0,JSON.stringify(display));
    await shot(`calendar-no-fit-${width}.png`);
  }
  await evalJs(`(async()=>{const f=document.querySelector('#booking-form'),p=f.querySelector('.availability-picker');f.querySelector('[data-step-back]').click();f.querySelector('[name=bookingType][value=individual]').click();for(let i=0;i<240&&(f.dataset.priceState!=='ready'||!p.querySelector('[data-duration-label]').textContent.includes('60 min'));i++)await new Promise(r=>setTimeout(r,50));if(f.dataset.priceState!=='ready')throw Error('Reset after long booking failed')})()`);
  console.log('PASS too-long package explains unavailable dates and server refuses outside hours');
  const stale = await evalJs(`(async()=>{
    const f=document.querySelector('#booking-form'),p=f.querySelector('.availability-picker'),q=f.querySelector('.addon-quantity'),original=window.fetch;
    const wait=async(fn,label)=>{for(let i=0;i<240;i++){if(fn())return;await new Promise(r=>setTimeout(r,50))}throw Error(label)};
    let held=false,release;window.fetch=async(url,...args)=>{if(String(url).includes('/Bookings/Availability?')&&!held){held=true;const response=await original(url,...args);const body=await response.text();await new Promise(r=>release=r);return new Response(body,{status:200,headers:{'Content-Type':'application/json'}})}return original(url,...args)};
    q.value='1';q.dispatchEvent(new Event('input',{bubbles:true}));await wait(()=>!!release,'Delayed calendar did not start');
    q.value='0';q.dispatchEvent(new Event('input',{bubbles:true}));const blocked=[...p.querySelectorAll('[data-calendar-days] button')].every(b=>b.disabled);release();
    await wait(()=>f.dataset.priceState==='ready'&&p.querySelector('[data-duration-label]').textContent.includes('60 min')&&p.querySelector('.availability-calendar').getAttribute('aria-busy')==='false','Fresh calendar timeout');await new Promise(r=>setTimeout(r,600));window.fetch=original;
    return {blocked,duration:p.querySelector('[data-duration-label]').textContent,quantity:q.value};
  })()`);
  assert.deepEqual(stale,{blocked:true,duration:'60 min session + 45 min buffer',quantity:'0'});
  console.log('PASS delayed obsolete calendar response cannot replace current selection');
  const conflict=await evalJs(`(async()=>{const f=document.querySelector('#booking-form'),p=f.querySelector('.availability-picker');f.querySelector('[data-step-next]').click();const wait=async fn=>{for(let i=0;i<200;i++){if(fn())return;await new Promise(r=>setTimeout(r,50))}throw Error('Conflict calendar timeout')};const today=p.dataset.today,d=new Date(Number(today.slice(0,4)),Number(today.slice(5,7))-1,Number(today.slice(8,10))+2),date=d.getFullYear()+'-'+String(d.getMonth()+1).padStart(2,'0')+'-'+String(d.getDate()).padStart(2,'0');const nextMonth=date.slice(0,7)!==today.slice(0,7);if(nextMonth)p.querySelector('[data-month-next]').click();await wait(()=>p.querySelector('[data-date="'+date+'"]:not([disabled])'));p.querySelector('[data-date="'+date+'"]').click();await wait(()=>p.querySelector('[data-start="'+date+'T10:00"]'));const disabled=p.querySelector('[data-start="'+date+'T10:00"]').disabled;if(nextMonth)p.querySelector('[data-month-prev]').click();f.querySelector('[data-step-back]').click();return disabled})()`);
  assert.equal(conflict,true);
  console.log('PASS browser blocks the seeded conflicting appointment time');
  await evalJs(`document.querySelector('[data-step-next]').click();document.querySelector('[data-month-next]').click()`);
  for (const width of [1440,390]) {
  await viewport(width);
  const feeResult=await evalJs(`(async()=>{const f=document.querySelector('#booking-form'),picker=f.querySelector('.availability-picker');const wait=async fn=>{for(let i=0;i<200;i++){if(fn())return;await new Promise(r=>setTimeout(r,50))}throw Error('Timed out waiting for calendar or quote')};const today=picker.dataset.today;const d=new Date(Number(today.slice(0,4)),Number(today.slice(5,7)),15);const date=d.getFullYear()+'-'+String(d.getMonth()+1).padStart(2,'0')+'-15';await wait(()=>picker.querySelector('[data-date="'+date+'"]:not([disabled])'));picker.querySelector('[data-date="'+date+'"]').click();const result=[];for(const hour of ['05:00','10:00','17:00']){const time=date+'T'+hour;await wait(()=>picker.querySelector('[data-start="'+time+'"]:not([disabled])'));picker.querySelector('[data-start="'+time+'"]').click();await wait(()=>f.dataset.priceState==='ready' && picker.querySelector('[data-validation-status]').dataset.state==='valid' && f.elements.start.value===time);result.push({early:document.querySelector('#summary-early-fee').textContent,late:document.querySelector('#summary-late-fee').textContent,total:f.elements.expectedTotal.value,minimum:f.elements.expectedMinimum.value})}return result})()`);
  assert.match(feeResult[0].early,/123\.00/);assert.match(feeResult[0].late,/0\.00/);assert.match(feeResult[1].early,/0\.00/);assert.match(feeResult[1].late,/0\.00/);assert.match(feeResult[2].late,/234\.00/);
  assert.equal(Number(feeResult[0].total),Number(feeResult[1].total)+123);assert.equal(Number(feeResult[2].total),Number(feeResult[1].total)+234);
  assert.equal(Number(feeResult[2].minimum),Number(feeResult[2].total)*0.5);
  const payment=await evalJs(`(async()=>{const f=document.querySelector('#booking-form'),next=f.querySelector('[data-step-next]'),modal=document.querySelector('#travel-fee-modal');for(let i=0;i<200&&next.disabled;i++)await new Promise(r=>setTimeout(r,50));if(next.disabled)throw Error('Next stayed disabled: '+f.querySelector('.booking-next-status').textContent);next.click();for(let i=0;i<200&&modal.hidden&&f.dataset.currentStep!=='payment';i++)await new Promise(r=>setTimeout(r,50));if(!modal.hidden){modal.querySelector('#travel-policy-ack').click();modal.querySelector('[data-travel-ack]').click()}return {step:f.dataset.currentStep,total:f.elements.expectedTotal.value,minimum:f.elements.expectedMinimum.value,deposit:f.elements.deposit.value,proof:f.elements.proof.required}})()`);
  assert.deepEqual(payment,{step:'payment',total:feeResult[2].total,minimum:feeResult[2].minimum,deposit:feeResult[2].minimum,proof:true});
  const refreshed=await evalJs(`(async()=>{const f=document.querySelector('#booking-form');f.querySelector('[name=paymentChoice][value=custom]').click();const value=(Number(f.elements.expectedMinimum.value)+1).toFixed(2);f.elements.deposit.value=value;f.elements.deposit.dispatchEvent(new Event('input',{bubbles:true}));window.dispatchEvent(new Event('focus'));for(let i=0;i<200&&(f.dataset.priceState!=='ready'||f.querySelector('[type=submit]').disabled);i++)await new Promise(r=>setTimeout(r,50));const form=f.querySelector('.booking-details').getBoundingClientRect(),summary=f.querySelector('.summary').getBoundingClientRect();return {value:f.elements.deposit.value,expected:value,disabled:f.querySelector('[type=submit]').disabled,form:{left:form.left,top:form.top,width:form.width},summary:{left:summary.left,top:summary.top,width:summary.width},scroll:document.documentElement.scrollWidth,viewport:innerWidth}})()`);
  assert.equal(refreshed.value,refreshed.expected);assert.equal(refreshed.disabled,false);assert.ok(refreshed.scroll<=refreshed.viewport);
  if(width===390) {
    for(const size of widths) {
      await viewport(size);
      const dimensions=await evalJs(`({width:innerWidth,scroll:document.documentElement.scrollWidth})`);
      assert.ok(dimensions.scroll<=dimensions.width,`payment overflow at ${size}`);
    }
    await viewport(width);
    console.log('PASS payment step has no horizontal overflow at all nine requested widths');
  }
  if(width>850){assert.ok(refreshed.form.left<refreshed.summary.left);assert.ok(Math.abs(refreshed.form.top-refreshed.summary.top)<2,'payment columns are aligned');}
  await shot(`payment-${width}.png`);
  await evalJs(`document.querySelector('[name=paymentChoice][value=minimum]').click()`);
  await evalJs(`document.querySelector('[data-step-back]').click()`);
  console.log(`PASS ${width}px calendar fees and payment-step total, minimum and required proof`);
  }
  await evalJs(`document.querySelector('[data-step-back]').click()`);
  console.log('PASS real calendar early, normal and late selections recalculate totals and down payments');
  if (initial.legacy) {
    const legacy = await evalJs(`(async()=>{const f=document.querySelector('#booking-form'),q=f.querySelector('.addon-quantity');const ready=new Promise((r,j)=>{const t=setTimeout(()=>j(Error('Quote timeout')),10000);f.addEventListener('booking-price-updated',()=>{clearTimeout(t);r()},{once:true})});q.value='1';q.dispatchEvent(new Event('change',{bubbles:true}));await ready;f.querySelector('[data-step-next]').click();return {state:f.dataset.priceState,step:f.dataset.currentStep,start:f.elements.start.value,quantity:q.value}})()`);
    assert.deepEqual(legacy,{state:'ready',step:'schedule',start:'',quantity:'1'});
    console.log('PASS older compiled view with current JavaScript quotes add-ons and advances');
  } else {
  const interactive=await evalJs(`(async()=>{const f=document.querySelector('#booking-form'); const group=f.querySelector('[name=bookingType][value=package]'); group.click(); const people=f.elements.people; people.value='4'; const ready=()=>new Promise((r,j)=>{const t=setTimeout(()=>j(Error('Quote timeout')),10000);f.addEventListener('booking-price-updated',()=>{clearTimeout(t);r()},{once:true})}); let done=ready();people.dispatchEvent(new Event('input',{bubbles:true}));await done;const input=f.querySelector('.addon-quantity');const up=f.querySelector('[data-quantity-step=up]'),down=f.querySelector('[data-quantity-step=down]');done=ready();up.click();await done;done=ready();up.click();await done;done=ready();down.click();await done;done=ready();up.click();await done;return {quantity:input.value,hidden:f.querySelectorAll('#addon-values [name=addOnIds]').length,price:f.dataset.priceState,total:document.querySelector('#summary-total').textContent}})()`);
  assert.deepEqual(interactive,{quantity:'2',hidden:2,price:'ready',total:'₱2,600.00'}); console.log('PASS plus/minus preserve group quote, total and add-on bindings');
  const invalid=await evalJs(`(()=>{const f=document.querySelector('#booking-form'),q=f.querySelector('.addon-quantity');q.value='5';q.dispatchEvent(new Event('change',{bubbles:true}));f.querySelector('[data-step-next]').click();return {state:f.dataset.priceState,step:f.dataset.currentStep,message:f.querySelector('.booking-next-status').textContent,value:q.value}})()`);
  assert.equal(invalid.state,'invalid');assert.equal(invalid.step,'look');assert.equal(invalid.value,'5');assert.match(invalid.message,/selected 5.*4 people/i);
  await evalJs(`(async()=>{const f=document.querySelector('#booking-form'),q=f.querySelector('.addon-quantity');const ready=new Promise(r=>f.addEventListener('booking-price-updated',r,{once:true}));q.value='2';q.dispatchEvent(new Event('change',{bubbles:true}));await ready;f.querySelector('[data-step-next]').click();if(f.dataset.currentStep!=='schedule')throw Error('Group Next blocked');f.querySelector('[data-step-back]').click()})()`);
  console.log('PASS invalid add-on quantity is explained and valid group Next/Back works');
  const failedQuote=await evalJs(`(async()=>{const f=document.querySelector('#booking-form'),q=f.querySelector('.addon-quantity'),original=window.fetch;window.fetch=(url,...args)=>String(url).includes('/Bookings/Quote')?Promise.resolve(new Response(JSON.stringify({error:'Test quote temporarily unavailable.'}),{status:503,headers:{'Content-Type':'application/json'}})):original(url,...args);q.value='1';q.dispatchEvent(new Event('change',{bubbles:true}));for(let i=0;i<100&&f.dataset.priceState!=='error';i++)await new Promise(r=>setTimeout(r,50));f.querySelector('[data-step-next]').click();const result={state:f.dataset.priceState,step:f.dataset.currentStep,message:f.querySelector('.booking-next-status').textContent};window.fetch=original;const ready=new Promise(r=>f.addEventListener('booking-price-updated',r,{once:true}));q.value='2';q.dispatchEvent(new Event('change',{bubbles:true}));await ready;return result})()`);
  assert.equal(failedQuote.state,'error');assert.equal(failedQuote.step,'look');assert.match(failedQuote.message,/Test quote temporarily unavailable/);
  console.log('PASS failed server quote blocks Next with a clear message and retry works');
  await send('Page.bringToFront');
  const focus=await evalJs(`(()=>{const b=document.querySelector('[data-quantity-step=up]');b.focus();return {focused:document.activeElement===b,type:b.type,tabIndex:b.tabIndex}})()`);
  assert.deepEqual(focus,{focused:true,type:'button',tabIndex:0});
  console.log('PASS quantity controls are focusable native buttons');
  for(const width of widths){
    await viewport(width);
    const data=await evalJs(`(()=>{const btn=document.querySelector('[data-step-next]'),q=document.querySelector('.addon-quantity'),a=document.querySelector('.booking-type-option'),step=q.closest('.addon-quantity-control');const style=getComputedStyle(btn),input=getComputedStyle(q),rect=q.getBoundingClientRect();return {viewport:innerWidth,scroll:document.documentElement.scrollWidth,button:[style.color,style.backgroundColor],input:{width:rect.width,height:rect.height,value:q.value,visible:input.display!=='none'},buttons:[...step.querySelectorAll('button')].map(b=>({label:b.getAttribute('aria-label'),width:b.getBoundingClientRect().width,height:b.getBoundingClientRect().height})),option:getComputedStyle(a.querySelector('span')).display}})()`);
    assert.ok(data.scroll<=data.viewport,`booking overflow at ${width}: ${data.scroll}`);assert.ok(data.input.width>=56 && data.input.height>=44 && data.input.visible && data.input.value==='2');assert.ok(data.buttons.every(b=>b.label && b.width>=44 && b.height>=44));assert.equal(data.option,'grid');
    const [fg,bg]=data.button.map(luminance); assert.ok((Math.max(fg,bg)+.05)/(Math.min(fg,bg)+.05)>=4.5,`primary button contrast at ${width}`);
    if(width===1440 || width===390){await evalJs(`document.querySelector('.extras-panel').scrollIntoView({block:'start'})`);await shot(`booking-${width}.png`)}
    console.log(`PASS booking ${width}px: no horizontal overflow, visible quantity and aligned options`);
  }
  for(const width of [1440,390]){
    await viewport(width);
    await evalJs(`document.querySelector('.booking-type').scrollIntoView({block:'center'})`);
    await shot(`booking-choices-${width}.png`);
    const dialog=await evalJs(`(()=>{const d=document.querySelector('#booking-confirmation');d.showModal();const r=d.getBoundingClientRect();return {width:r.width,height:r.height,viewport:innerWidth,screenHeight:innerHeight,open:d.open}})()`);
    assert.ok(dialog.open && dialog.width<=dialog.viewport && dialog.height<=dialog.screenHeight,'confirmation fits viewport');
    await shot(`confirmation-${width}.png`);
    await evalJs(`document.querySelector('#booking-confirmation').close()`);
  }
  console.log('PASS booking choices and unsubmitted confirmation dialog fit desktop and mobile');
  await submit(`document.querySelector('form[action="/Account/Logout"]').requestSubmit()`);
  await submit(`document.querySelector('[name=email]').value='admin@glambymariya.demo';document.querySelector('[name=password]').value='Admin123!';document.querySelector('#auth-form').requestSubmit()`);
  const ax = await send('Accessibility.getFullAXTree');
  const names = ax.nodes.filter(n=>!n.ignored).map(n=>n.name?.value).filter(Boolean);
  assert.ok(names.some(n=>String(n).startsWith('Appointment GBM-')) && names.includes('Reference') && names.includes('Customer') && names.includes('Payment'), 'appointment field labels remain in the accessibility tree');
  console.log('PASS appointment row and field labels are present in Chromium accessibility tree');
  // The Demo does not persist a correction state. Apply only the server-rendered
  // badge text/class to this synthetic DOM to exercise its responsive styling.
  await evalJs(`(()=>{const b=document.querySelector('[data-appointment-row] > div:nth-child(5) .badge');b.textContent='Correction requested';b.classList.add('payment-correction')})()`);
  const correctionAx=await send('Accessibility.getFullAXTree');
  assert.ok(correctionAx.nodes.some(n=>!n.ignored && n.name?.value==='Correction requested'),'correction text is present in the accessibility tree');
  for(const width of widths){
    await viewport(width);
    const data=await evalJs(`(()=>{const label=document.querySelector('.stat>span'),row=document.querySelector('[data-appointment-row]'),field=row?.querySelector('.appointment-field-label'),badge=row.querySelector('.payment-correction'),rect=badge.getBoundingClientRect(),style=getComputedStyle(badge);return {viewport:innerWidth,scroll:document.documentElement.scrollWidth,headings:[...document.querySelectorAll('main h1')].length,labelSize:parseFloat(getComputedStyle(label).fontSize),rowLabel:row?.getAttribute('aria-label'),field:field?.textContent,fieldDisplay:field&&getComputedStyle(field).display,filter:document.querySelector('.appointment-filters a').getBoundingClientRect().height,badge:{left:rect.left,right:rect.right,fg:style.color,bg:style.backgroundColor}}})()`);
    assert.ok(data.scroll<=data.viewport,`admin overflow at ${width}: ${data.scroll}`); assert.equal(data.headings,1); assert.ok(data.labelSize>=14);assert.match(data.rowLabel,/Appointment/);assert.equal(data.field,'Reference');assert.ok(data.filter>=44);
    assert.ok(data.badge.left>=0 && data.badge.right<=data.viewport,`correction badge clipped at ${width}`);
    const [fg,bg]=[data.badge.fg,data.badge.bg].map(luminance);assert.ok((Math.max(fg,bg)+.05)/(Math.min(fg,bg)+.05)>=4.5,`correction badge contrast at ${width}`);
    if(width===1440 || width===390){await evalJs(`document.querySelector('.admin-appointments').scrollIntoView({block:'start'})`);await shot(`admin-${width}.png`)}
    console.log(`PASS admin ${width}px: headings, readable statistics, labeled rows and touch targets`);
  }
  const adminDetails=await evalJs(`document.querySelector('[data-appointment-row] .appointment-details').getAttribute('href')`);
  await navigate(adminDetails);
  for(const width of widths) {
    await viewport(width);
    const layout=await evalJs(`({width:innerWidth,scroll:document.documentElement.scrollWidth,action:!!document.querySelector('form[action="/Admin/Action"] button')})`);
    assert.ok(layout.scroll<=layout.width && layout.action,`admin details at ${width}: ${JSON.stringify(layout)}`);
  }
  console.log('PASS Admin reservation and payment review fit all nine requested widths');
  await submit(`document.querySelector('form[action="/Account/Logout"]').requestSubmit()`);
  await submit(`document.querySelector('[name=email]').value='owner@glambymariya.demo';document.querySelector('[name=password]').value='Owner123!';document.querySelector('#auth-form').requestSubmit()`);
  for(const width of widths) {
    await viewport(width);
    const layout=await evalJs(`({width:innerWidth,scroll:document.documentElement.scrollWidth,menu:!!document.querySelector('.account-menu summary')})`);
    assert.ok(layout.scroll<=layout.width && layout.menu,`SuperAdmin dashboard at ${width}`);
  }
  await navigate(adminDetails);
  for(const width of widths) {
    await viewport(width);
    const layout=await evalJs(`({width:innerWidth,scroll:document.documentElement.scrollWidth,action:!!document.querySelector('form[action="/Admin/Action"] button')})`);
    assert.ok(layout.scroll<=layout.width && layout.action,`SuperAdmin review at ${width}`);
  }
  console.log('PASS SuperAdmin dashboard and review fit all nine requested widths');
  await submit(`document.querySelector('form[action="/Account/Logout"]').requestSubmit()`);
  for(const width of widths) {
    await viewport(width);
    const layout=await evalJs(`({width:innerWidth,scroll:document.documentElement.scrollWidth,email:!!document.querySelector('[name=email]')})`);
    assert.ok(layout.scroll<=layout.width && layout.email,`login at ${width}`);
  }
  await submit(`document.querySelector('[name=email]').value='customer@glambymariya.demo';document.querySelector('[name=password]').value='Customer123!';document.querySelector('#auth-form').requestSubmit()`);
  await navigate('/Account/Settings');
  for(const width of widths) {
    await viewport(width);
    const layout=await evalJs(`({width:innerWidth,scroll:document.documentElement.scrollWidth,heading:!!document.querySelector('main h1')})`);
    assert.ok(layout.scroll<=layout.width && layout.heading,`Customer account at ${width}`);
  }
  console.log('PASS login and Customer account fit all nine requested widths');
  await navigate('/Bookings');
  const customerDetails=await evalJs(`document.querySelector('a[href^="/Bookings/Details/"]').getAttribute('href')`);
  await navigate(customerDetails);
  for(const width of widths) {
    await viewport(width);
    const layout=await evalJs(`({width:innerWidth,scroll:document.documentElement.scrollWidth,payment:document.body.textContent.includes('Payment submissions')})`);
    assert.ok(layout.scroll<=layout.width && layout.payment,`Customer booking details at ${width}`);
  }
  console.log('PASS Customer booking details fit all nine requested widths');
  console.log('Browser screenshots retained:',temp); ws.close();
  }
} finally { if(chrome && chrome.exitCode===null) chrome.kill(); if(server.exitCode===null) server.kill(); }
