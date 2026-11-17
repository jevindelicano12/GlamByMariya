// node tests/connection-checks.mjs "C:/Program Files/PostgreSQL/18/bin"
// Creates a disposable loopback cluster, never reads production credentials.
import { resolve, join } from 'node:path';
import { mkdtemp, mkdir } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import { createServer } from 'node:net';
if (!process.argv[2]) throw new Error('Supply native PostgreSQL bin directory.');
const bin = resolve(process.argv[2]);
const exe = name => join(bin, process.platform === 'win32' ? name + '.exe' : name);
const base = join(process.env.LOCALAPPDATA ?? process.env.TMPDIR ?? '.', 'Temp', 'opencode');
await mkdir(base, { recursive: true });
const directory = await mkdtemp(join(base, 'makeup-connection-checks-'));
const data = join(directory, 'data');
const listener = createServer();
await new Promise((resolve, reject) => { listener.once('error', reject); listener.listen(0, '127.0.0.1', resolve); });
const port = listener.address().port;
await new Promise(resolve => listener.close(resolve));
const run = (command, args) => new Promise((resolve, reject) => {
  const child = spawn(command, args, { stdio: 'inherit', shell: false });
  child.once('error', reject); child.once('exit', code => resolve(code ?? 1));
});
let started = false;
try {
  if (await run(exe('initdb'), ['-D', data, '-U', 'postgres', '-A', 'trust', '--no-locale', '--encoding=UTF8'])) throw new Error('initdb failed');
  if (await run(exe('pg_ctl'), ['-D', data, '-l', join(directory, 'postgres.log'), '-o', `-h 127.0.0.1 -p ${port}`, '-w', 'start'])) throw new Error('pg_ctl failed');
  started = true;
  process.exitCode = await run('dotnet', ['run', '--project', 'tests/ConnectionChecks/ConnectionChecks.csproj', '-c', 'ConnectionValidation', '--', String(port)]);
} finally {
  if (started) await run(exe('pg_ctl'), ['-D', data, '-m', 'fast', '-w', 'stop']);
  console.log('Isolated logs: ' + directory);
}
