// Synthetic data only; test storage is retained and never shared with the demo.
import assert from 'node:assert/strict';
import { admin as adminAccount, owner as ownerAccount, customer as customerAccount } from './presentation-test-accounts.mjs';
import { spawn } from 'node:child_process';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { randomUUID, createHash } from 'node:crypto';
import { deflateSync } from 'node:zlib';
import path from 'node:path';
const app=path.resolve(process.argv[2] || 'Frontend/GlamByMariya.Web/bin/Release/net10.0'), web=path.resolve(process.argv[3] || 'Frontend/GlamByMariya.Web/wwwroot');
const parent=path.join(process.env.LOCALAPPDATA,'GlamByMariya','catalog-test-'+randomUUID());
const root=path.join(process.env.LOCALAPPDATA,'Temp','opencode','catalog-test-'+randomUUID());
await mkdir(root,{recursive:true});
const base='http://127.0.0.1:5190'; let server;
async function start(){server=spawn('dotnet',[path.join(app,'GlamByMariya.Web.dll'),`--urls=${base}`,`--contentRoot=${root}`,`--webroot=${web}`,'--Database:Provider=Demo','--Authentication:DurableSessionEnforcement=false','--DemoGallery:Enabled=true',`--DemoGallery:Root=${path.join(parent,'gallery')}`,'--DemoCatalog:Enabled=true',`--DemoCatalog:Root=${path.join(parent,'catalog')}`,'--Supabase:Url=','--Supabase:AnonKey=','--SupabaseStorage:ServiceKey=','--FileStorage:Provider=Local','--B2:Bucket=','--B2:Endpoint=','--B2:KeyId=','--B2:AppKey=','--Email:BrevoApiKey=','--Email:SmtpHost='],{env:{...process.env,PORT:'',ASPNETCORE_ENVIRONMENT:'Development',DOTNET_ENVIRONMENT:'Development'},stdio:['ignore','pipe','pipe']});await new Promise((resolve,reject)=>{let out='';const timer=setTimeout(()=>reject(Error('startup timeout '+out)),30000);server.stdout.on('data',b=>{out+=b;if(out.includes(`Now listening on: ${base}`)){clearTimeout(timer);resolve()}});server.stderr.on('data',b=>out+=b);server.once('exit',c=>{clearTimeout(timer);reject(Error('startup exit '+c+' '+out))})})}
async function stop(){if(server&&server.exitCode===null)await new Promise(resolve=>{server.once('exit',resolve);server.kill()})}
async function req(j,p,f){const r=await fetch(base+p,{redirect:'manual',headers:{cookie:[...j].map(([k,v])=>`${k}=${v}`).join('; ')},...(f?{method:'POST',body:f}:{})});for(const c of r.headers.getSetCookie()){let v=c.split(';')[0],i=v.indexOf('=');j.set(v.slice(0,i),v.slice(i+1))}let bytes=Buffer.from(await r.arrayBuffer());return {status:r.status,loc:r.headers.get('location'),type:r.headers.get('content-type'),body:bytes.toString(),hash:createHash('sha256').update(bytes).digest('hex')}}
function token(html){const t=html.match(/name="__RequestVerificationToken"[^>]*value="([^"]+)"/)?.[1];assert.ok(t);return t}
async function login(email,password){let j=new Map(),page=await req(j,'/Account/Login');assert.equal((await req(j,'/Account/Login',new URLSearchParams({email,password,__RequestVerificationToken:token(page.body)}))).status,302);return j}
function itemId(html,name){const start=html.indexOf(`<h3>${name}</h3>`);assert.ok(start>=0,'missing '+name);const id=html.slice(start).match(/name="id" type="hidden" value="([a-f0-9-]+)"/)?.[1];assert.ok(id,'missing id for '+name);return id}
function form(data,bytes){let f=new FormData();for(const [k,v] of Object.entries(data))f.set(k,v);if(bytes)f.set('image',new Blob([bytes],{type:'image/png'}),'../../unsafe.png');return f}
function chunk(type,data){const n=Buffer.from(type),b=Buffer.concat([n,data]);let crc=0xffffffff;for(const x of b){crc^=x;for(let i=0;i<8;i++)crc=(crc>>>1)^(0xedb88320&-(crc&1))}const len=Buffer.alloc(4),sum=Buffer.alloc(4);len.writeUInt32BE(data.length);sum.writeUInt32BE((crc^0xffffffff)>>>0);return Buffer.concat([len,b,sum])}
function png(r,g,b){let h=Buffer.alloc(13);h.writeUInt32BE(1,0);h.writeUInt32BE(1,4);h[8]=8;h[9]=2;return Buffer.concat([Buffer.from('89504e470d0a1a0a','hex'),chunk('IHDR',h),chunk('IDAT',deflateSync(Buffer.from([0,r,g,b]))),chunk('IEND',Buffer.alloc(0))])}
try {
 await start();let admin=await login(adminAccount.email,adminAccount.password),customer=await login(customerAccount.email,customerAccount.password),owner=await login(ownerAccount.email,ownerAccount.password);
 let page=await req(admin,'/Admin/Catalog'),csrf=token(page.body),service=itemId(page.body,'Soft Glam'),addon=itemId(page.body,'Lash application');
 const baseForm={__RequestVerificationToken:csrf,kind:'service',id:service,name:'Soft Glam',description:'Synthetic updated',price:'700',minutes:'60',active:'true',category:'Synthetic'};
 assert.equal((await req(admin,'/Admin/SaveCatalog',form({...baseForm,__RequestVerificationToken:''}))).status,400);
 assert.equal((await req(customer,'/Admin/SaveCatalog',form(baseForm))).status,302);
 assert.equal((await req(admin,'/Admin/SaveCatalog',form(baseForm,png(220,20,20)))).status,302);
 assert.equal((await req(admin,'/Admin/SaveGroupPricing',new URLSearchParams({__RequestVerificationToken:csrf,id:service,groupPrice:'500',groupMinimumPeople:'4'}))).status,302);
 let image=`/CatalogImages/Image?kind=service&id=${service}`;let first=await req(new Map(),image);assert.equal(first.type,'image/png');
 assert.equal((await req(admin,'/Admin/SaveCatalog',form({__RequestVerificationToken:csrf,kind:'addon',id:addon,name:'Synthetic addon',price:'300',minutes:'15',active:'true'},png(30,30,180)))).status,302);
 const addonImage=`/CatalogImages/Image?kind=addon&id=${addon}`;const addonHash=(await req(new Map(),addonImage)).hash;
 assert.equal((await req(owner,'/Admin/SaveCatalog',form({__RequestVerificationToken:token((await req(owner,'/Admin/Catalog')).body),kind:'service',name:'Synthetic created',price:'900',minutes:'45',active:'true'}))).status,302);
 page=await req(admin,'/Admin/Catalog');let created=itemId(page.body,'Synthetic created');assert.notEqual(created,service);
 let quote=await req(customer,`/Bookings/Quote?serviceId=${service}&people=1&package=auto`);assert.equal(JSON.parse(quote.body).regularPrice,700);
 await stop();await start();admin=await login(adminAccount.email,adminAccount.password);customer=await login(customerAccount.email,customerAccount.password);page=await req(admin,'/Admin/Catalog');
 assert.equal(itemId(page.body,'Soft Glam'),service);assert.equal(itemId(page.body,'Synthetic addon'),addon);assert.equal(itemId(page.body,'Synthetic created'),created);
 assert.equal((await req(new Map(),image)).hash,first.hash);assert.equal((await req(new Map(),addonImage)).hash,addonHash);let servicesPage=await req(new Map(),'/Home/Services');assert.match(servicesPage.body,new RegExp(`<img[^>]+src="/CatalogImages/Image\\?kind=service&amp;id=${service}`));
 csrf=token(page.body);assert.equal((await req(admin,'/Admin/SaveCatalog',form({...baseForm,__RequestVerificationToken:csrf},png(20,180,20)))).status,302);
 let replaced=await req(new Map(),image);assert.notEqual(replaced.hash,first.hash);assert.equal((await req(new Map(),addonImage)).hash,addonHash);assert.equal((await req(new Map(),`/CatalogImages/Image?kind=addon&id=${service}`)).status,404);
 await req(admin,'/Admin/SaveCatalog',form({...baseForm,__RequestVerificationToken:csrf},Buffer.from('<svg/>')));assert.equal((await req(new Map(),image)).hash,replaced.hash,'invalid file replaced current image');
 await stop();await start();assert.equal((await req(new Map(),image)).hash,replaced.hash);customer=await login(customerAccount.email,customerAccount.password);quote=await req(customer,`/Bookings/Quote?serviceId=${service}&people=1&package=auto`);assert.equal(JSON.parse(quote.body).regularPrice,700);
 let group=await req(customer,`/Bookings/Quote?serviceId=${service}&people=4&package=group`);assert.equal(JSON.parse(group.body).appliedPricePerPerson,500);
 admin=await login(adminAccount.email,adminAccount.password);page=await req(admin,'/Admin/Catalog');csrf=token(page.body);
 assert.equal((await req(admin,'/Admin/SaveCatalog',form({__RequestVerificationToken:csrf,kind:'service',id:created,name:'Synthetic created',price:'900',minutes:'45',active:'true'},png(20,20,220)))).status,302);
 assert.equal((await req(new Map(),`/CatalogImages/Image?kind=service&id=${created}`)).status,200);
 assert.equal((await req(admin,'/Admin/DeleteCatalog',new URLSearchParams({__RequestVerificationToken:csrf,kind:'service',id:created}))).status,302);
 assert.equal((await req(new Map(),`/CatalogImages/Image?kind=service&id=${created}`)).status,404);
 await stop();await start();admin=await login(adminAccount.email,adminAccount.password);page=await req(admin,'/Admin/Catalog');assert.ok(!page.body.includes('Synthetic created'));assert.equal((await req(new Map(),`/CatalogImages/Image?kind=service&id=${created}`)).status,404);
 const pointer=path.join(parent,'catalog','media',`service-${service.replaceAll('-','')}-card.json`);const original=await readFile(pointer,'utf8');const mismatched=JSON.parse(original);mismatched.Id=created;await writeFile(pointer,JSON.stringify(mismatched));assert.equal((await req(new Map(),image)).status,404,'mismatched pointer accepted');await writeFile(pointer,'{broken');assert.equal((await req(new Map(),image)).status,404);assert.equal(await readFile(pointer,'utf8'),'{broken');assert.ok(original.includes(service));
 console.log('PASS synthetic catalog IDs, edit/create, addon edit, image replacement, role/antiforgery, quotes and restart persistence');console.log('Retained isolated test folders:',parent,root);
} finally {await stop()}
