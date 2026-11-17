// Isolated public store validation. Never targets Supabase.
// node tests/public-store-checks.mjs <native-postgres-bin-dir>
import { resolve, join } from 'node:path';
import { mkdtemp, mkdir, writeFile } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import { createServer } from 'node:net';
import { randomUUID } from 'node:crypto';
if(!process.argv[2]) throw new Error('Provide native PostgreSQL bin directory.');
if(process.argv.length>4 || (process.argv[3] && process.argv[3]!=='--mvc-booking')) throw new Error('Unknown test mode.');
const bin=resolve(process.argv[2]);
const exe=name=>join(bin,process.platform==='win32'?name+'.exe':name);
const base=join(process.env.LOCALAPPDATA??process.env.TMPDIR??'.','Temp','opencode');
await mkdir(base,{recursive:true});
const directory=await mkdtemp(join(base,'makeup-public-store-'));
const data=join(directory,'data');
const log=join(directory,'postgres.log');
const listener=createServer();
await new Promise((resolve,reject)=>{listener.once('error',reject);listener.listen(0,'127.0.0.1',resolve);});
const port=listener.address().port;
const marker=randomUUID().replaceAll('-','');
const artifacts=join(directory,'artifacts');
await mkdir(artifacts);
await writeFile(join(artifacts,'owner-marker'),marker,{flag:'wx'});
await new Promise(resolve=>listener.close(resolve));
function run(command,args){return new Promise((resolve,reject)=>{const child=spawn(command,args,{stdio:'inherit',shell:false});child.once('error',reject);child.once('exit',code=>resolve(code??1));});}
let started=false;
try{
  if(await run(exe('initdb'),['-D',data,'-U','postgres','-A','trust','--no-locale','--encoding=UTF8'])!==0) throw new Error('initdb failed');
  if(await run(exe('pg_ctl'),['-D',data,'-l',log,'-o',`-h 127.0.0.1 -p ${port} -c glam.payment_review_marker=${marker}`,'-w','start'])!==0) throw new Error('pg_ctl start failed');
  started=true;
  process.exitCode=await run('dotnet',['run','--project','tests/PublicStoreChecks/PublicStoreChecks.csproj','-c','PublicStoreValidation','--',String(port),marker,artifacts,...(process.argv[3]?[process.argv[3]]:[])]);
}catch(e){console.error(e.message);process.exitCode=1;}
finally{if(started) await run(exe('pg_ctl'),['-D',data,'-m','fast','-w','stop']); console.log('Isolated logs: '+directory);}
