// FAKE state-machine test only. There is no cryptography, PostgreSQL, backend
// identity primitive, separate process, durable storage or real transaction.
import assert from 'node:assert/strict';

class FakeAuthority {
  now = 0;
  next = 0;
  challenges = new Map();
  authorizations = new Map();
  sessions = new Map([['a', { login: 'login-a', key: 'key-a1', expiry: 10, revoked: false }],
                      ['b', { login: 'login-b', key: 'key-b1', expiry: 10, revoked: false }]]);
  rows = new Map();

  closeBackend(backend) {
    for (const [id, authorization] of this.authorizations)
      if (authorization.backend === backend) this.authorizations.delete(id);
  }

  issue(customer, backend, operation, payload, requestId = 'one') {
    const session = this.sessions.get(customer);
    if (!session || session.revoked || this.now >= session.expiry) throw Error('session denied');
    const challenge = `fake-challenge-${++this.next}`;
    const tuple = { customer, login: session.login, key: session.key, backend, operation, payload, requestId, challenge };
    this.challenges.set(challenge, { tuple, consumed: false, expiresAt: Math.min(this.now + 2, session.expiry) });
    return { ...tuple }; // Pretend signature, NOT cryptographic proof.
  }

  // Pretend durable phase separate from operation rollback. This assumes a
  // protected backend-bound continuation that does NOT yet exist in PostgreSQL.
  consume(assertion, backend, operation, payload, requestId = 'one') {
    const row = this.challenges.get(assertion.challenge);
    const session = this.sessions.get(assertion.customer);
    if (!row || row.consumed || this.now >= row.expiresAt || !session || session.revoked || this.now >= session.expiry ||
        assertion.key !== session.key || assertion.login !== session.login ||
        backend !== row.tuple.backend || assertion.backend !== backend ||
        operation !== row.tuple.operation || assertion.operation !== operation ||
        payload !== row.tuple.payload || assertion.payload !== payload ||
        requestId !== row.tuple.requestId || assertion.requestId !== requestId ||
        assertion.customer !== row.tuple.customer || assertion.key !== row.tuple.key || assertion.login !== row.tuple.login)
      throw Error('assertion denied');
    row.consumed = true;
    const permit = `fake-permit-${++this.next}`;
    this.authorizations.set(permit, { backend, operation, payload, requestId, customer: assertion.customer, key: assertion.key, used: false });
    return permit;
  }

  run(permit, backend, operation, payload, key, fail = false, loseAck = false) {
    const auth = this.authorizations.get(permit);
    if (!auth || auth.used || auth.backend !== backend || auth.operation !== operation || auth.payload !== payload || auth.requestId !== key)
      throw Error('operation denied');
    const session = this.sessions.get(auth.customer);
    if (session.revoked || session.key !== auth.key || this.now >= session.expiry) throw Error('session denied');
    auth.used = true; // Pretend this cannot be rolled back or transferred.
    const actorKey = `${auth.customer}:${key}`;
    const existing = this.rows.get(actorKey);
    if (existing) {
      if (existing.payload !== payload) throw Error('replay mismatch');
      return existing.id;
    }
    if (fail) throw Error('booking rollback');
    const id = `fake-booking-${key}`;
    this.rows.set(actorKey, { payload, id });
    if (loseAck) throw Error('unknown commit');
    return id;
  }
}

const f = new FakeAuthority();
const first = f.issue('a', 'backend-a1', 'create', 'payload-one');
assert.throws(() => f.consume(first, 'backend-a2', 'create', 'payload-one'), /denied/); // reconnect
assert.throws(() => f.consume({ ...first, customer: 'b' }, 'backend-a1', 'create', 'payload-one'), /denied/);
assert.throws(() => f.consume(first, 'backend-a1', 'read', 'payload-one'), /denied/);
assert.throws(() => f.consume(first, 'backend-a1', 'create', 'changed'), /denied/);
assert.throws(() => f.consume(first, 'backend-a1', 'create', 'payload-one', 'another-submission'), /denied/);
const permit = f.consume(first, 'backend-a1', 'create', 'payload-one');
assert.throws(() => f.consume(first, 'backend-a1', 'create', 'payload-one'), /denied/);
assert.throws(() => f.run(permit, 'backend-a1', 'create', 'payload-one', 'another-submission'), /denied/);
assert.throws(() => f.run(permit, 'backend-a1', 'create', 'payload-one', 'one', true), /rollback/);
assert.throws(() => f.consume(first, 'backend-a1', 'create', 'payload-one'), /denied/); // rollback does not unconsume
assert.throws(() => f.run(permit, 'backend-a1', 'create', 'payload-one', 'one'), /denied/);
const retry = f.issue('a', 'backend-a2', 'create', 'payload-one');
const retryPermit = f.consume(retry, 'backend-a2', 'create', 'payload-one');
assert.throws(() => f.run(retryPermit, 'backend-a2', 'create', 'payload-one', 'one', false, true), /unknown commit/);
const afterUnknown = f.issue('a', 'backend-a2', 'create', 'payload-one');
assert.equal(f.run(f.consume(afterUnknown, 'backend-a2', 'create', 'payload-one'), 'backend-a2', 'create', 'payload-one', 'one'), 'fake-booking-one');
assert.equal(f.rows.size, 1);
const changedRetry = f.issue('a', 'backend-a2', 'create', 'different-payload');
assert.throws(() => f.run(f.consume(changedRetry, 'backend-a2', 'create', 'different-payload'), 'backend-a2', 'create', 'different-payload', 'one'), /replay mismatch/);
const crashed = f.issue('b', 'backend-b0', 'create', 'abandoned');
const crashedPermit = f.consume(crashed, 'backend-b0', 'create', 'abandoned');
f.closeBackend('backend-b0'); // pretend crash immediately after consumption
assert.throws(() => f.consume(crashed, 'backend-b0', 'create', 'abandoned'), /denied/);
assert.throws(() => f.run(crashedPermit, 'backend-b1', 'create', 'abandoned', 'one'), /denied/);
const race = f.issue('b', 'backend-b1', 'read', 'booking-id');
const attempts = await Promise.allSettled([0, 1].map(() => Promise.resolve().then(() => f.consume(race, 'backend-b1', 'read', 'booking-id'))));
assert.equal(attempts.filter(x => x.status === 'fulfilled').length, 1);
const oldKey = f.issue('a', 'backend-a2', 'read', 'booking-id');
const admittedOld = f.issue('a', 'backend-a2', 'read', 'other-id');
const oldPermit = f.consume(admittedOld, 'backend-a2', 'read', 'other-id');
f.sessions.get('a').key = 'key-a2';
assert.throws(() => f.consume(oldKey, 'backend-a2', 'read', 'booking-id'), /denied/);
assert.throws(() => f.run(oldPermit, 'backend-a2', 'read', 'other-id', 'one'), /session denied/);
const revoked = f.issue('a', 'backend-a2', 'read', 'booking-id');
const admittedBeforeRevocation = f.issue('a', 'backend-a2', 'create', 'pending');
const revokedPermit = f.consume(admittedBeforeRevocation, 'backend-a2', 'create', 'pending');
f.sessions.get('a').revoked = true;
assert.throws(() => f.consume(revoked, 'backend-a2', 'read', 'booking-id'), /denied/);
assert.throws(() => f.run(revokedPermit, 'backend-a2', 'create', 'pending', 'one'), /session denied/);
f.sessions.get('a').revoked = false;
const challengeExpired = f.issue('a', 'backend-a2', 'read', 'booking-id');
f.now = 2;
assert.throws(() => f.consume(challengeExpired, 'backend-a2', 'read', 'booking-id'), /denied/);
f.now = 0;
const expired = f.issue('a', 'backend-a2', 'read', 'booking-id');
f.now = 10;
assert.throws(() => f.consume(expired, 'backend-a2', 'read', 'booking-id'), /denied/);
console.log('PASS FAKE signature-protocol state model: binding, one-use, rollback, retry, rotation and revocation');
console.log('No cryptography, PostgreSQL backend identity, durable consumption or cross-process behavior was verified.');
