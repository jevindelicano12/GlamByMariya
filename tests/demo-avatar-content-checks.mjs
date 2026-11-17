// Isolated synthetic Demo only. Test folders are intentionally retained.
import assert from 'node:assert/strict';
import { admin as adminAccount, owner as ownerAccount, customer as customerAccount } from './presentation-test-accounts.mjs';
import { spawn } from 'node:child_process';
import { mkdir } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { deflateSync } from 'node:zlib';
import path from 'node:path';
const app = path.resolve(process.argv[2] || 'Frontend/GlamByMariya.Web/bin/Release/net10.0');
const web = path.resolve(process.argv[3] || 'Frontend/GlamByMariya.Web/wwwroot');
const parent = path.join(process.env.LOCALAPPDATA,'GlamByMariya','avatar-test-'+randomUUID());
const root = path.join(process.env.LOCALAPPDATA,'Temp','opencode','avatar-test-'+randomUUID());
await mkdir(root,{recursive:true});
const base='http://127.0.0.1:5199'; let server;
async function start() {
 server=spawn('dotnet',[path.join(app,'GlamByMariya.Web.dll'),`--urls=${base}`,`--contentRoot=${root}`,`--webroot=${web}`,'--Database:Provider=Demo','--Authentication:DurableSessionEnforcement=false','--WebsiteContent:DemoTextEditing=true','--DemoGallery:Enabled=true',`--DemoGallery:Root=${path.join(parent,'gallery')}`,'--DemoAvatar:Enabled=true',`--DemoAvatar:Root=${path.join(parent,'avatars')}`,'--Supabase:Url=','--Supabase:AnonKey=','--SupabaseStorage:ServiceKey=','--FileStorage:Provider=Local','--B2:Bucket=','--B2:Endpoint=','--B2:KeyId=','--B2:AppKey=','--Email:BrevoApiKey=','--Email:SmtpHost='],{env:{...process.env,PORT:'',ASPNETCORE_ENVIRONMENT:'Development',DOTNET_ENVIRONMENT:'Development'},stdio:['ignore','pipe','pipe']});
 await new Promise((resolve,reject)=>{let out='';const timer=setTimeout(()=>reject(Error('startup timeout '+out)),30000);server.stdout.on('data',b=>{out+=b;if(out.includes(`Now listening on: ${base}`)){clearTimeout(timer);resolve()}});server.stderr.on('data',b=>out+=b);server.once('exit',c=>{clearTimeout(timer);reject(Error('startup exit '+c+' '+out))});});
}
async function stop(){if(server&&server.exitCode===null)await new Promise(resolve=>{server.once('exit',resolve);server.kill()})}
async function request(jar,url,form){const r=await fetch(base+url,{redirect:'manual',headers:{cookie:[...jar].map(([k,v])=>`${k}=${v}`).join('; ')},...(form?{method:'POST',body:form}:{})});for(const c of r.headers.getSetCookie()){const x=c.split(';')[0],i=x.indexOf('=');jar.set(x.slice(0,i),x.slice(i+1));}return {status:r.status,location:r.headers.get('location'),type:r.headers.get('content-type'),body:await r.text()}}
function token(s){const t=s.match(/name="__RequestVerificationToken"[^>]*value="([^"]+)"/)?.[1];assert.ok(t);return t}
async function login(email,password){const jar=new Map(),p=await request(jar,'/Account/Login');const r=await request(jar,'/Account/Login',new URLSearchParams({email,password,__RequestVerificationToken:token(p.body)}));assert.equal(r.status,302);return jar}
function chunk(type,data){const n=Buffer.from(type),b=Buffer.concat([n,data]);let crc=0xffffffff;for(const x of b){crc^=x;for(let i=0;i<8;i++)crc=(crc>>>1)^(0xedb88320&-(crc&1))}const len=Buffer.alloc(4),sum=Buffer.alloc(4);len.writeUInt32BE(data.length);sum.writeUInt32BE((crc^0xffffffff)>>>0);return Buffer.concat([len,b,sum])}
const hdr=Buffer.alloc(13);hdr.writeUInt32BE(1,0);hdr.writeUInt32BE(1,4);hdr[8]=8;hdr[9]=2;
const png=Buffer.concat([Buffer.from('89504e470d0a1a0a','hex'),chunk('IHDR',hdr),chunk('IDAT',deflateSync(Buffer.from([0,180,80,90]))),chunk('IEND',Buffer.alloc(0))]);
function upload(csrf,data){const f=new FormData();f.set('__RequestVerificationToken',csrf);f.set('avatar',new Blob([data],{type:'image/png'}),'../../synthetic.png');return f}
try {
 await start();const admin=await login(adminAccount.email,adminAccount.password),owner=await login(ownerAccount.email,ownerAccount.password),customer=await login(customerAccount.email,customerAccount.password),guest=new Map();
 let settings=await request(admin,'/Account/Settings');assert.match(settings.body,/This account&#x27;s picture stays on this computer|This account's picture stays on this computer/);let csrf=token(settings.body);
 assert.equal((await request(guest,'/Account/DemoAvatar')).status,302);
 assert.equal((await request(owner,'/Account/DemoAvatar')).status,404);
 await request(admin,'/Account/UploadAvatar',upload(csrf,Buffer.from('<svg/>')));assert.equal((await request(admin,'/Account/DemoAvatar')).status,404);
 await request(admin,'/Account/UploadAvatar',upload(csrf,Buffer.alloc(2*1024*1024+1)));assert.equal((await request(admin,'/Account/DemoAvatar')).status,404);
 const noCsrf=new FormData();noCsrf.set('avatar',new Blob([png]),'test.png');assert.equal((await request(admin,'/Account/UploadAvatar',noCsrf)).status,400);
 await request(admin,'/Account/UploadAvatar',upload(csrf,png));assert.equal((await request(admin,'/Account/DemoAvatar')).type,'image/png');assert.equal((await request(owner,'/Account/DemoAvatar')).status,404);
 assert.equal((await request(admin,'/WebsiteContent')).status,302);let page=await request(owner,'/WebsiteContent');assert.match(page.body,/reset when the application stops/);
 let t=token(page.body);assert.equal((await request(owner,'/WebsiteContent/CreateDraftForm',new URLSearchParams({__RequestVerificationToken:t}))).status,302);
 page=await request(owner,'/WebsiteContent');let id=page.body.match(/name="DraftId"[^>]*value="([^"]+)"/)?.[1],rev=page.body.match(/name="Revision"[^>]*value="([^"]+)"/)?.[1];assert.ok(id&&rev);
 assert.equal((await request(owner,'/WebsiteContent/SaveForm',new URLSearchParams({DraftId:id,Revision:rev,HeroHeadline:'Synthetic headline',HeroDescription:'Synthetic introduction',AboutHeading:'Synthetic about',AboutBiography:'Synthetic biography',__RequestVerificationToken:token(page.body)}))).status,302);
 assert.match((await request(owner,'/WebsiteContent/Preview')).body,/Synthetic biography/);assert.doesNotMatch((await request(guest,'/Home/About')).body,/Synthetic biography/);
 page=await request(owner,'/WebsiteContent');rev=page.body.match(/name="Revision"[^>]*value="([^"]+)"/)[1];assert.equal((await request(owner,'/WebsiteContent/PublishForm',new URLSearchParams({draftId:id,revision:rev,__RequestVerificationToken:token(page.body)}))).status,302);assert.match((await request(guest,'/Home/About')).body,/Synthetic biography/);
 assert.equal((await request(customer,'/Bookings/Create')).status,200);assert.equal((await request(admin,'/DemoGallery')).status,302);assert.equal((await request(owner,'/DemoGallery')).status,200);
 await stop();await start();const next=await login(adminAccount.email,adminAccount.password);assert.equal((await request(next,'/Account/DemoAvatar')).type,'image/png');assert.match((await request(next,'/Account/Settings')).body,/\/Account\/DemoAvatar/);assert.doesNotMatch((await request(new Map(),'/Home/About')).body,/Synthetic biography/);
 console.log('PASS demo avatar validation, authorization, role isolation, restart persistence; owner text draft/preview/publication and restart reset; navigation');
 console.log('Retained isolated storage:',parent,'content root:',root);
} finally {await stop()}
