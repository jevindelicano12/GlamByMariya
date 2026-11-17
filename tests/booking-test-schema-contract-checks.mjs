// Offline fixture/design checks: does not connect to PostgreSQL, register an
// adapter, validate live metadata, or prove a SQL migration will execute.
import assert from 'node:assert/strict';
import { readFile, readdir } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { validateFixtureMetadata } from './booking-test-schema-guard.mjs';
import { validatePinnedHashes } from './BookingTestPostgresRunner/hash-preflight.mjs';
import { validateFakeInventory } from './BookingTestPostgresRunner/fake-inventory.mjs';

const valid = { host: '127.0.0.1', port: 5432, database: 'glam_auth_mvc_workflow_test',
  user: 'postgres', schema: 'booking_test', sentinel: true,
  tables: ['fixture_runs', 'artists', 'conflict_reservations'] };
assert.equal(validateFixtureMetadata(valid), true);
for (const patch of [{ host: 'localhost' }, { host: 'example.test' }, { port: 5433 },
  { database: 'postgres' }, { user: 'other' }, { schema: 'public' },
  { schema: 'booking_test;DROP SCHEMA public' }, { sentinel: false }, { tables: [] },
  { tables: ['fixture_runs', 'artists'] }, { tables: [...valid.tables, 'unexpected'] },
  { tables: ['fixture_runs', 'artists', 'artists'] }, { tables: null }]) {
  assert.throws(() => validateFixtureMetadata({ ...valid, ...patch }));
}

const fixture = await readFile(new URL('../docs/booking-test-conflict-schema-candidate.sql', import.meta.url), 'utf8');
const correction = await readFile(new URL('../docs/public-booking-conflict-correction-candidate.sql', import.meta.url), 'utf8');
const booking = await readFile(new URL('../docs/booking-test-booking-foundation-candidate.sql', import.meta.url), 'utf8');
for (const sql of [fixture, correction, booking]) {
  assert.match(sql, /BEGIN;\s*DO \$\$ BEGIN RAISE EXCEPTION/s, 'candidate must unconditionally abort before DDL');
  assert.match(sql, /EXCLUDE USING gist/i);
  assert.match(sql, /tstzrange\(start_at,\s*(?:coalesce\(reserved_until,\s*end_at\)|reserved_until),\s*'\[\)'\)/i);
}
assert.match(fixture, /WHERE \(status IN \('Pending Payment','Confirmed'\)\)/);
assert.match(fixture, /REFERENCES public\.account_identities\(id\) ON DELETE RESTRICT/);
assert.match(fixture, /REVOKE ALL ON SCHEMA booking_test FROM PUBLIC/);
assert.match(correction, /WHERE \(status IN \('Pending','Pending Payment','Confirmed','InProgress'\)\)/);
assert.match(booking, /UNIQUE\(customer_id,\s*submission_id\)/);
assert.match(booking, /WHERE\(status IN \('Pending Payment','Confirmed'\)\)/);
assert.match(booking, /REFERENCES booking_test\.synthetic_customers\(id\) ON DELETE RESTRICT/);
assert.match(booking, /PRIMARY KEY\(booking_id,addon_id\)/);
assert.match(booking, /IF NEW\.snapshot_sealed THEN RAISE EXCEPTION/);
assert.match(booking, /NEW\.extras_total<>line_total/);
assert.match(booking, /NEW\.service_minutes::bigint\*NEW\.people\+line_minutes/);
assert.match(booking, /CREATE CONSTRAINT TRIGGER require_sealed_booking[\s\S]*?DEFERRABLE INITIALLY DEFERRED/);
assert.match(booking, /Invalid service snapshot/);
assert.match(booking, /Invalid add-on snapshot/);
assert.match(booking, /NEW\.status IS DISTINCT FROM OLD\.status/);
assert.match(booking, /BEFORE INSERT OR UPDATE OR DELETE OR TRUNCATE ON booking_test\.schedule_blocks/);
assert.match(booking, /FROM booking_test\.settings WHERE id=1 FOR UPDATE/);
const grants = await readFile(new URL('../docs/booking-test-runtime-grants-candidate.sql', import.meta.url), 'utf8');
assert.match(grants, /BEGIN;\s*DO \$\$ BEGIN RAISE EXCEPTION/s);
assert.match(grants, /CREATE ROLE booking_test_runtime NOLOGIN NOSUPERUSER/);
assert.doesNotMatch(grants, /GRANT\s+(?:SELECT|INSERT|UPDATE|DELETE|ALL)\s+ON/i);
const rls = await readFile(new URL('../docs/booking-test-ownership-rls-candidate.sql', import.meta.url), 'utf8');
assert.match(rls, /BEGIN;\s*DO \$\$ BEGIN RAISE EXCEPTION/s);
assert.match(rls, /CASE session_user::text/);
assert.match(rls, /ELSE NULL::uuid/);
assert.match(rls, /FORCE ROW LEVEL SECURITY/g);
assert.match(rls, /REVOKE ALL ON FUNCTION booking_test\.authenticated_fixture_customer\(\) FROM PUBLIC/);
assert.doesNotMatch(rls, /current_setting\s*\(/i);
const logins = await readFile(new URL('../docs/booking-test-fixture-logins-candidate.sql', import.meta.url), 'utf8');
assert.match(logins, /BEGIN;\s*DO \$\$ BEGIN RAISE EXCEPTION/s);
assert.match(logins, /CREATE ROLE booking_test_customer_a NOLOGIN INHERIT NOSUPERUSER/);
assert.match(logins, /CREATE ROLE booking_test_customer_b NOLOGIN INHERIT NOSUPERUSER/);
assert.match(logins, /SET FALSE/);
assert.doesNotMatch(logins, /PASSWORD\s+'[^']+'/i);
const sessionSql = await readFile(new URL('../docs/booking-test-session-boundary-candidate.sql', import.meta.url), 'utf8');
assert.match(sessionSql, /BEGIN;\s*DO \$\$ BEGIN RAISE EXCEPTION/s);
assert.match(sessionSql, /proof_digest bytea NOT NULL/);
assert.match(sessionSql, /pg_catalog\.sha256\(p_proof\)/);
assert.match(sessionSql, /SECURITY DEFINER SET search_path=pg_catalog/);
assert.equal((sessionSql.match(/SECURITY DEFINER SET search_path=pg_catalog/g) ?? []).length, 3);
assert.equal((sessionSql.match(/SECURITY DEFINER/gi) ?? []).length, 3);
assert.match(sessionSql, /REVOKE ALL ON FUNCTION booking_test\.issue_synthetic_session\(name,bytea,timestamptz\) FROM PUBLIC/);
assert.match(sessionSql, /REVOKE ALL ON FUNCTION booking_test\.revoke_synthetic_session\(bytea\) FROM PUBLIC/);
assert.match(sessionSql, /REVOKE ALL ON FUNCTION booking_test\.lock_synthetic_session\(bytea\) FROM PUBLIC/);
assert.doesNotMatch(sessionSql, /GRANT\s+EXECUTE/i);
const manifest = JSON.parse(await readFile(new URL('./BookingTestPersistenceChecks/synthetic-fixture-manifest.json', import.meta.url), 'utf8'));
assert.equal(manifest.target, '127.0.0.1:5432/glam_auth_mvc_workflow_test');
assert.equal(manifest.schema, 'booking_test');
assert.equal(manifest.expected.total, manifest.service.price + manifest.addon.price * manifest.addon.quantity);
assert.equal(manifest.expected.durationMinutes, manifest.service.minutes + manifest.addon.minutes * manifest.addon.quantity);
assert.equal(manifest.expected.reservedMinutes, manifest.expected.durationMinutes + manifest.settings.bufferMinutes);
assert.equal(manifest.expected.minimum, manifest.expected.total * manifest.settings.downPaymentPercent / 100);
assert.notEqual(manifest.identities[0].id, manifest.identities[1].id);
assert.notEqual(manifest.submissionId, manifest.competingSubmissionId);
const runner = fileURLToPath(new URL('./BookingTestPostgresRunner/runner.mjs', import.meta.url));
const testBytes = Buffer.from('offline candidate only');
const testHash = createHash('sha256').update(testBytes).digest('hex').toUpperCase();
const testEntry = { name: 'candidate', expected: testHash, bytes: testBytes };
assert.equal(validatePinnedHashes([testEntry]), true);
assert.equal(validatePinnedHashes([{ ...testEntry, bytes: Buffer.from('changed') }]), false);
assert.equal(validatePinnedHashes([{ ...testEntry, expected: testHash.toLowerCase() }]), false);
assert.equal(validatePinnedHashes([testEntry, testEntry]), false);
assert.equal(validatePinnedHashes([]), false);
// Small fake subset only: not an inspection of actual DB roles or grants.
const fakeInventory = { roles: ['booking_test_customer_a', 'booking_test_customer_b', 'booking_test_runtime'], objects: ['bookings', 'booking_addons'], grants: [] };
assert.equal(validateFakeInventory(fakeInventory), true);
assert.equal(validateFakeInventory({ ...fakeInventory, roles: [...fakeInventory.roles, 'unexpected_role'] }), false);
assert.equal(validateFakeInventory({ ...fakeInventory, objects: [...fakeInventory.objects, 'unexpected_object'] }), false);
assert.equal(validateFakeInventory({ ...fakeInventory, objects: [...fakeInventory.objects, 'unexpected_function'] }), false);
assert.equal(validateFakeInventory({ ...fakeInventory, grants: ['unexpected_grant'] }), false);
assert.equal(validateFakeInventory({ ...fakeInventory, roles: ['booking_test_customer_a', 'booking_test_customer_a', 'booking_test_runtime'] }), false);
assert.equal(validateFakeInventory({ ...fakeInventory, surprise: true }), false);
const localArgs = ['--offline-preflight', '127.0.0.1', '5432', 'glam_auth_mvc_workflow_test', 'booking_test', 'booking_test_runtime'];
assert.equal(spawnSync(process.execPath, [runner], { encoding: 'utf8' }).status, 1);
assert.equal(spawnSync(process.execPath, [runner, ...localArgs], { encoding: 'utf8' }).status, 0);
assert.equal(spawnSync(process.execPath, [runner, ...localArgs.slice(0, 1), 'example.test', ...localArgs.slice(2)], { encoding: 'utf8' }).status, 1);
assert.equal(spawnSync(process.execPath, [runner, '--run', ...localArgs.slice(1)], { encoding: 'utf8' }).status, 1);
const create = await readFile(new URL('./BookingTestPersistenceChecks/BookingTestCreateRepository.cs', import.meta.url), 'utf8');
const read = await readFile(new URL('./BookingTestPersistenceChecks/BookingTestReadRepository.cs', import.meta.url), 'utf8');
const boundary = await readFile(new URL('./BookingTestPersistenceChecks/BookingTestProtectedBoundary.cs', import.meta.url), 'utf8');
const sessionsSource = await readFile(new URL('./BookingTestPersistenceChecks/BookingTestDatabaseSessionRepository.cs', import.meta.url), 'utf8');
const connectionsSource = await readFile(new URL('./BookingTestPersistenceChecks/SyntheticCustomerConnections.cs', import.meta.url), 'utf8');
const appSource = await readFile(new URL('./BookingTestPersistenceChecks/SyntheticBookingApplication.cs', import.meta.url), 'utf8');
assert.match(boundary, /RequireImplemented\(\)\s*=>\s*\r?\n\s*throw new InvalidOperationException/);
assert.ok(create.indexOf('BookingTestProtectedBoundary.RequireImplemented();') < create.indexOf('CreateOnceAsync(customer, request'));
assert.ok(read.indexOf('BookingTestProtectedBoundary.RequireImplemented();') < read.indexOf('connections.Create(customer)'));
assert.match(sessionsSource, /IssueAsync\([^]*?\{\s*BookingTestProtectedBoundary\.RequireImplemented\(\);/);
assert.match(sessionsSource, /private static async Task ExecuteAsync\([^]*?\{\s*BookingTestProtectedBoundary\.RequireImplemented\(\);/);
assert.match(connectionsSource, /internal NpgsqlConnection Create\([^]*?\{\s*BookingTestProtectedBoundary\.RequireImplemented\(\);/);
assert.match(create, /EnsureCustomerAsync\([^]*?\{\s*BookingTestProtectedBoundary\.RequireImplemented\(\);/);
assert.match(appSource, /=> create\.CreateAsync\(customer, request, ct\)/);
assert.match(appSource, /await read\.FindOwnAsync\(customer, bookingId, ct\)/);
assert.doesNotMatch(sessionsSource, /Console\.|ILogger|LogInformation|LogWarning|LogError|ActivitySource/);
assert.match(sessionsSource, /Parameters\.Add\("proof", NpgsqlDbType\.Bytea\)\.Value = proof/);
assert.match(sessionsSource, /CryptographicOperations\.ZeroMemory\(proof\)/);
assert.doesNotMatch(sessionsSource, /new NpgsqlCommand\(\$[^\n]*proof/i);
const issuerSource = await readFile(new URL('./BookingTestPersistenceChecks/SyntheticBookingSessions.cs', import.meta.url), 'utf8');
assert.doesNotMatch(issuerSource, /Console\.|ILogger|LogInformation|LogWarning|LogError|ActivitySource/);
// Inventory known direct command/open paths in this test project only. This
// cannot detect reflection, indirect calls, provider diagnostics or other apps.
const allowedCommandFiles = new Set(['BookingTestCreateRepository.cs', 'BookingTestReadRepository.cs', 'BookingTestDatabaseSessionRepository.cs']);
const fixtureDir = new URL('./BookingTestPersistenceChecks/', import.meta.url);
for (const name of await readdir(fixtureDir)) {
  if (!name.endsWith('.cs')) continue;
  const source = await readFile(new URL(name, fixtureDir), 'utf8');
  if (/\bNpgsqlCommand\b|\.OpenAsync\s*\(|\.ExecuteNonQueryAsync\s*\(/.test(source))
    assert.ok(allowedCommandFiles.has(name), `Unexpected test project command path: ${name}`);
}
// Deliberately document the outstanding blocker, not a security pass:
assert.match(create, /INSERT INTO booking_test\.bookings/);
assert.match(create, /FROM booking_test\.services/);
assert.match(read, /FROM booking_test\.bookings/);
assert.match(sessionSql, /p_proof bytea/);
const lock = create.indexOf('SELECT id FROM booking_test.settings WHERE id=1 FOR UPDATE');
const replay = create.indexOf('existing = await ReplayAsync', lock);
const quote = create.indexOf('var quote = ValidateAndQuote', lock);
assert.ok(lock >= 0 && replay > lock && quote > replay, 'post-lock replay precedes catalog quote');
assert.match(create, /throw new BookingTestCommitUnknownException\(\)/);
console.log('PASS offline test-schema allowlist, missing-table cases and guarded conflict SQL source contracts');
console.log('No SQL parsing/execution, DI, real adapter or PostgreSQL behavior verified.');
