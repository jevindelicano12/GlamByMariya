// Offline-only preflight for a future isolated PostgreSQL runner. It never
// connects to PostgreSQL and never applies SQL. Live execution is deliberately
// hard-disabled until identity and privilege boundaries have been reviewed.
import { readFile } from 'node:fs/promises';
import { validatePinnedHashes } from './hash-preflight.mjs';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const expectedHash = '1C0DB1639C33B648412B27A7F6BCAA7A8F198E29B5CA77B1D202686333F73F5B';
const pinnedInputs = [
  ['../../docs/booking-test-ownership-rls-candidate.sql', '22ECF893748D3885B49DC125C4DC89C400A8730E460FB9AEE94DFA05C5A5008E'],
  ['../../docs/booking-test-runtime-grants-candidate.sql', 'CB55A12E86F1E99602FDC66C68BA57385369F6738E1B5C25EDC7A48503186119'],
  ['../../docs/booking-test-fixture-logins-candidate.sql', '21483A553814D39C7A0CD713F4D8D7BB7F79D47B976167A6E58024A7C4D06A39'],
  ['../../docs/booking-test-session-boundary-candidate.sql', '8E767CD3BD937D20243955D65ADE5CC9B139DB7EB6DD49C3790AE634DB484541']
];
const [mode, host, port, database, schema, role] = process.argv.slice(2);
if (!['--offline-preflight', '--offline-security-checks'].includes(mode) || process.argv.length !== 8 ||
    host !== '127.0.0.1' || port !== '5432' || database !== 'glam_auth_mvc_workflow_test' ||
    schema !== 'booking_test' || role !== 'booking_test_runtime') {
  console.error('Disabled: only explicit offline modes for the exact local test target are accepted. Live execution is not implemented.');
  process.exit(1);
}
const inputs = [['../../docs/booking-test-booking-foundation-candidate.sql', expectedHash], ...pinnedInputs];
const entries = await Promise.all(inputs.map(async ([path, expected]) => ({
  name: path, expected, bytes: await readFile(new URL(path, import.meta.url))
})));
if (!validatePinnedHashes(entries)) {
  console.error('A candidate revision changed; review and update the preflight pin.');
  process.exit(1);
}
const fixture = JSON.parse(await readFile(new URL('../BookingTestPersistenceChecks/synthetic-fixture-manifest.json', import.meta.url), 'utf8'));
if (fixture.status !== 'proposal-only-no-provisioning' || fixture.target !== `${host}:${port}/${database}` || fixture.schema !== schema) {
  console.error('Fixture manifest target or status is invalid.');
  process.exit(1);
}
console.log(`Offline preflight only: target and pinned SQL source hashes match. No connection or integration tests were performed.`);
if (mode === '--offline-security-checks') {
  const model = fileURLToPath(new URL('./revocation-model-checks.mjs', import.meta.url));
  const modelResult = spawnSync(process.execPath, [model], { stdio: 'inherit' });
  if (modelResult.error || modelResult.status !== 0) {
    console.error('Offline revocation model checks failed.');
    process.exit(1);
  }
  const signatureModel = fileURLToPath(new URL('./signature-protocol-model-checks.mjs', import.meta.url));
  const signatureResult = spawnSync(process.execPath, [signatureModel], { stdio: 'inherit' });
  if (signatureResult.error || signatureResult.status !== 0) {
    console.error('Offline signature protocol model checks failed.');
    process.exit(1);
  }
  const checks = fileURLToPath(new URL('../BookingTestPersistenceChecks/bin/Release/net10.0/BookingTestPersistenceChecks.dll', import.meta.url));
  const result = spawnSync('dotnet', [checks], { stdio: 'inherit' });
  if (result.error || result.status !== 0) {
    console.error('Offline security checks failed or are not built. Build the Release solution first.');
    process.exit(1);
  }
  console.log('Offline synthetic security checks only; live integration remains disabled.');
}
