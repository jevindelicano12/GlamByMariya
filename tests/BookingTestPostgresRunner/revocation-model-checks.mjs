// In-memory protocol illustration only: no Npgsql, process boundary or SQL.
import assert from 'node:assert/strict';

// Separate fake identities and proofs; not an implementation of DB auth.
const fakeProofs = new Map([['login-a', 'proof-a'], ['login-b', 'proof-b']]);
const fakeAuthorize = (login, proof) => fakeProofs.get(login) === proof && typeof proof === 'string';
assert.equal(fakeAuthorize('login-a', 'proof-a'), true);
assert.equal(fakeAuthorize('login-b', 'proof-b'), true);
assert.equal(fakeAuthorize('login-a', 'proof-b'), false);
assert.equal(fakeAuthorize('login-b', 'proof-a'), false);
assert.equal(fakeAuthorize('unknown', 'proof-a'), false);

let tail = Promise.resolve();
async function exclusive(action) {
  const previous = tail;
  let release;
  tail = new Promise(resolve => { release = resolve; });
  await previous;
  try { return await action(); } finally { release(); }
}
let active = true, now = 0, expiry = 10;
const rows = new Map();
async function create(key, payload, proof, beforeCommit = async () => {}, loseAck = false) {
  return exclusive(async () => {
    if (!active || now >= expiry || proof !== 'synthetic-proof') throw new Error('denied');
    const existing = rows.get(key);
    if (existing) {
      if (existing.payload !== payload) throw new Error('replay mismatch');
      return existing.id;
    }
    await beforeCommit();
    if (!active || now >= expiry) throw new Error('denied');
    const id = `fake-${key}`;
    rows.set(key, { id, payload });
    if (loseAck) throw new Error('unknown commit');
    return id;
  });
}
const revoke = () => exclusive(async () => { active = false; });

let signalEntered, signalContinue;
const entered = new Promise(resolve => { signalEntered = resolve; });
const unblock = new Promise(resolve => { signalContinue = resolve; });
const booking = create('one', 'payload', 'synthetic-proof', async () => { signalEntered(); await unblock; });
await entered;
let revoked = false;
const revocation = revoke().then(() => { revoked = true; });
await Promise.resolve();
assert.equal(revoked, false, 'FAKE revocation waits while booking owns lock');
signalContinue();
assert.equal(await booking, 'fake-one');
await revocation;
assert.equal(revoked, true);
await assert.rejects(create('two', 'payload', 'synthetic-proof'), /denied/);
assert.equal(rows.size, 1, 'FAKE revocation before start prevents write');

active = true;
await assert.rejects(create('two', 'payload', 'wrong-proof'), /denied/);
await assert.rejects(create('two', 'payload', 'synthetic-proof', async () => { now = expiry; }), /denied/);
assert.equal(rows.size, 1, 'FAKE expiry before commit leaves no row');
now = 0;
await assert.rejects(create('two', 'payload', 'synthetic-proof', async () => { throw new Error('rollback'); }), /rollback/);
assert.equal(rows.size, 1, 'FAKE transaction error leaves no row');
await assert.rejects(create('two', 'payload', 'synthetic-proof', async () => {}, true), /unknown commit/);
assert.equal(await create('two', 'payload', 'synthetic-proof'), 'fake-two');
await assert.rejects(create('two', 'changed', 'synthetic-proof'), /replay mismatch/);
assert.equal(rows.size, 2, 'FAKE same-key recovery does not create another row');
console.log('PASS FAKE revocation ordering, proof, expiry, rollback and ambiguous-commit replay model');
console.log('No database, real transaction, cross-process ordering or durability was tested.');
