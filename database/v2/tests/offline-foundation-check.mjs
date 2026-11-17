// Structural checks only: no SQL execution, database connections or cloud access.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const sql = await readFile(new URL('../migrations/0001_catalog_schedule_foundation.sql', import.meta.url), 'utf8');
const blocked = await readFile(new URL('../blocked/0002_pending_slot_candidate.sql', import.meta.url), 'utf8');
const baseline = await readFile(new URL('../../../tests/PublicChecks/BaselinePublic.sql', import.meta.url), 'utf8');
const names = ['makeup_artists', 'services', 'artist_availability', 'blocked_slots'];
assert.match(sql, /\bBEGIN\s*;/i);
assert.match(sql, /\bCOMMIT\s*;/i);
assert.doesNotMatch(sql, /\b(?:INSERT|CREATE\s+POLICY|GRANT)\b/i);
for (const name of names) {
  assert.match(baseline, new RegExp(`CREATE TABLE public\\.${name}\\s*\\(`, 'i'));
  assert.match(sql, new RegExp(`CREATE TABLE public\\.${name}\\s*\\(`, 'i'));
  assert.match(sql, new RegExp(`ALTER TABLE public\\.${name} ENABLE ROW LEVEL SECURITY`, 'i'));
  assert.match(sql, new RegExp(`ALTER TABLE public\\.${name} FORCE ROW LEVEL SECURITY`, 'i'));
  assert.match(sql, new RegExp(`public\\.${name}_id_seq`, 'i'));
}
assert.equal((sql.match(/FROM PUBLIC, anon, authenticated, service_role/g) ?? []).length, 2);
assert.match(blocked, /BEGIN;\s*DO \$\$ BEGIN RAISE EXCEPTION[\s\S]*?END \$\$;[\s\S]*?ALTER TABLE/i);
assert.match(blocked, /'Pending Payment'/);
console.log('PASS: offline foundation structure and blocked booking fragment (SQL not executed)');
