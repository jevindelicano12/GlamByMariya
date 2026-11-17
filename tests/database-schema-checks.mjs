// Optional, isolated database-design checks; does not connect the MVC application.
// Install outside the repository:
// npm install --prefix "$env:LOCALAPPDATA\Temp\opencode\makeup-schema-validation" @electric-sql/pglite
// node tests/database-schema-checks.mjs "$env:LOCALAPPDATA\Temp\opencode\makeup-schema-validation"
// Alternatively install PGlite in your own external test workspace and pass its path.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const external = resolve(process.argv[2] ?? '.');
const require = createRequire(pathToFileURL(resolve(external, 'schema-test.cjs')));
const { PGlite } = await import(pathToFileURL(require.resolve('@electric-sql/pglite')));
const { btree_gist } = await import(pathToFileURL(require.resolve('@electric-sql/pglite/contrib/btree_gist')));
const db = new PGlite({ extensions: { btree_gist } });
let checks = 0;
const admin = '20000000-0000-0000-0000-000000000001';
const customer = '20000000-0000-0000-0000-000000000002';
const pending = '81000001-0000-0000-0000-000000000001';
const group = '81000002-0000-0000-0000-000000000002';
const cancelled = '81000003-0000-0000-0000-000000000003';
const gcash = '70000000-0000-0000-0000-000000000001';
const soft = '40000000-0000-0000-0000-000000000001';
const area = '60000000-0000-0000-0000-000000000001';
function check(value, label) {
  assert.ok(value, label);
  console.log(`PASS: ${label}`);
  checks++;
}
async function row(sql) { return (await db.query(sql)).rows[0]; }
async function rejects(sql, pattern, label) {
  await db.exec('BEGIN; SET LOCAL search_path=makeup,public;');
  let error;
  try { await db.exec(sql); await db.exec('COMMIT'); } catch (e) { error = e; }
  await db.exec('ROLLBACK');
  check(error && pattern.test(error.message), `${label}${error ? ` (${error.message})` : ''}`);
}
async function transaction(sql) {
  await db.exec(`BEGIN; SET LOCAL search_path=makeup,public; ${sql} COMMIT;`);
}
function newBooking(id, timeSql, extra = '') {
  return `INSERT INTO bookings(id,customer_id,artist_id,service_id,starts_at,address,area_id,
    policy_accepted_at,policy_version,travel_reminder_acknowledged_at,travel_reminder_version${extra ? ',booking_type,people' : ''})
    VALUES('${id}','${customer}','${admin}','${soft}',${timeSql},'24 Sample Street, Demo Barangay','${area}',
    now(),'booking-v1',now(),'travel-v1'${extra});`;
}
function deposit(id, ref, amount = 300) {
  return `INSERT INTO payments(booking_id,method_id,purpose,amount,reference,submitted_by)
    VALUES('${id}','${gcash}','down_payment',${amount},'${ref}','${customer}');`;
}
const future = days => `(((now() AT TIME ZONE 'Asia/Manila')::date+${days}+time '10:00') AT TIME ZONE 'Asia/Manila')`;

try {
  const script = await readFile(new URL('../MakeupBookingDatabase.sql', import.meta.url), 'utf8');
  await db.exec(script);
  check(true, 'Complete DDL, seeds, deferred constraints and every sample SELECT execute');
  await db.exec('SET search_path=makeup,public');
  check(Number((await row("SELECT count(*) AS n FROM pg_tables WHERE schemaname='makeup'")).n) === 22, 'All 22 required design tables exist');
  check((await row('SELECT bool_and(relrowsecurity) AS ok FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=\'makeup\' AND c.relkind=\'r\'')).ok, 'Every table has RLS enabled');
  check((await row('SELECT bool_and(NOT is_active) AS ok FROM app_users')).ok, 'Seed accounts are disabled');
  let b = await row(`SELECT * FROM booking_financial_summary WHERE id='${group}'`);
  check(b.final_total === '2350.00' && b.required_down_payment === '1175.00' && b.duration_minutes === 255, 'Four-person package prices extras once and scales service duration');
  check(b.group_discount === '300.00' && b.travel_fee_informational_snapshot === '0.00', 'Unknown transportation expense snapshots as zero and remains excluded from total, discount and down payment');
  check(b.payment_status === 'Down Payment Verified' && b.remaining_balance === '1175.00', 'Financial view derives verified paid and remaining balance');
  b = await row(`SELECT * FROM booking_financial_summary WHERE id='${cancelled}'`);
  check(b.cancellation_fee === '200.00' && b.refund_due === '100.00' && b.refunded === '100.00' && b.payment_status === 'Refunded', 'Cancellation fee and refund ledger match actual rules');
  await db.exec('UPDATE app_users SET is_active=true');

  await transaction(`SELECT confirm_booking('${group}','${admin}'); SELECT confirm_booking('${group}','${admin}');`);
  check(Number((await row(`SELECT count(*) AS n FROM payments WHERE booking_id='${group}'`)).n) === 1, 'Repeated verification is idempotent');
  await db.exec(`UPDATE makeup_services SET price=900,group_price=700 WHERE id='${soft}'`);
  check((await row(`SELECT final_total FROM bookings WHERE id='${group}'`)).final_total === '2350.00', 'Catalog price edit preserves historical total');
  await db.exec(`UPDATE makeup_services SET price=600,group_price=525 WHERE id='${soft}'`);
  await rejects(`UPDATE bookings SET applied_price_per_person=600 WHERE id='${group}';`, /immutable/, 'Frozen quote cannot be overwritten');

  await transaction(`UPDATE reschedule_requests SET status='Approved',reviewed_by='${admin}',reviewed_at=now() WHERE booking_id='${group}' AND status='Pending';`);
  check((await row(`SELECT b.starts_at=r.requested_start AS ok FROM bookings b JOIN reschedule_requests r ON r.booking_id=b.id WHERE b.id='${group}'`)).ok, 'Approved reschedule atomically moves booking');
  check((await row(`SELECT b.starts_at=e.starts_at AND b.reserved_until=e.ends_at AS ok FROM bookings b JOIN artist_schedule_entries e ON e.booking_id=b.id WHERE b.id='${group}'`)).ok, 'Reschedule moves reservation including buffer');
  check((await row(`SELECT delivery_status FROM notifications WHERE booking_id='${group}' AND kind='reminder'`)).delivery_status === 'Cancelled', 'Old-slot reminder is cancelled after rescheduling');
  await rejects(`UPDATE bookings SET starts_at=starts_at+interval '1 day' WHERE id='${group}';`, /approved reschedule/, 'Direct slot edits require reschedule approval');

  await rejects(`INSERT INTO artist_schedule_entries(artist_id,kind,starts_at,ends_at,reason) SELECT artist_id,'block',starts_at,ends_at,'Conflict' FROM bookings WHERE id='${group}';`, /overlaps/, 'Block cannot overlap confirmed appointment');
  await rejects(`DELETE FROM artist_schedule_entries WHERE booking_id='${group}';`, /Release the reservation/, 'Confirmed reservation cannot be manually deleted');
  await rejects(`INSERT INTO refunds(closure_id,method_id,amount,transfer_reference,sent_at,recorded_by)
    SELECT id,'${gcash}',1,'OVER-REFUND',now(),'${admin}' FROM booking_closures WHERE booking_id='${cancelled}';`, /exceeds/, 'Refunds cannot exceed remaining obligation');
  await rejects(`UPDATE payments SET amount=1 WHERE booking_id='${group}';`, /immutable/, 'Verified payments cannot be edited');
  await rejects(`INSERT INTO payment_adjustments(booking_id,payment_id,amount,reason,recorded_by)
    SELECT booking_id,id,-9999,'Invalid correction','${admin}' FROM payments WHERE booking_id='${group}';`, /between zero/, 'Adjustments cannot make paid amount negative');
  await rejects(`UPDATE bookings SET status='Completed' WHERE id='${group}';`, /has not ended/, 'Cannot complete a future appointment');
  await rejects(`UPDATE bookings SET status='Cancelled' WHERE id='${group}';`, /explicitly confirmed/, 'Cancellation requires explicit confirmation');
  await rejects(`UPDATE bookings SET status='Confirmed' WHERE id='${pending}';`, /Verify the deposit/, 'Cannot confirm an unverified deposit');

  const overlap = '82000001-0000-0000-0000-000000000001';
  await transaction(`${newBooking(overlap, future(10))} ${deposit(overlap, 'OVERLAP-A')}`);
  await transaction(`SELECT confirm_booking('${overlap}','${admin}');`);
  const conflict = '82000002-0000-0000-0000-000000000002';
  // Create an initially non-conflicting pending request; another confirmation
  // then occupies that slot, reproducing the real pending-request race.
  const conflict2 = '82000003-0000-0000-0000-000000000003';
  await transaction(`${newBooking(conflict, future(11))} ${deposit(conflict, 'OVERLAP-B')}`);
  await transaction(`${newBooking(conflict2, future(11))} ${deposit(conflict2, 'OVERLAP-C')}`);
  await transaction(`SELECT confirm_booking('${conflict}','${admin}');`);
  await rejects(`SELECT confirm_booking('${conflict2}','${admin}');`, /unavailable|overlap/, 'Second pending confirmation cannot claim reserved slot');
  check((await row(`SELECT status FROM payments WHERE booking_id='${conflict2}'`)).status === 'Pending Verification', 'Failed confirmation rolls back payment verification');
  await rejects(`${newBooking('82000004-0000-0000-0000-000000000004', `(SELECT starts_at+interval '75 minutes' FROM bookings WHERE id='${overlap}')`)}
    ${deposit('82000004-0000-0000-0000-000000000004','BUFFER')}`, /unavailable/, 'Travel buffer prevents near-overlapping appointments');
  await rejects(`${newBooking('82000005-0000-0000-0000-000000000005', future(12))}`, /exactly one/, 'Booking without down-payment submission fails at commit');
  await rejects(`${newBooking('82000006-0000-0000-0000-000000000006', future(12))}
    ${deposit('82000006-0000-0000-0000-000000000006','UNDERPAY',299)}`, /exact recorded down payment/, 'Deposit must equal the required down payment exactly');
  await rejects(`${newBooking('82000007-0000-0000-0000-000000000007', future(12))}
    ${deposit('82000007-0000-0000-0000-000000000007',' demo-reference-001 ')}`, /ux_payment_reference/, 'Payment references are unique after trim and case normalization');
  await rejects(`${newBooking('82000008-0000-0000-0000-000000000008', future(12), ",'individual',2")}
    ${deposit('82000008-0000-0000-0000-000000000008','INDIVIDUAL',600)}`, /check constraint/, 'Individual bookings enforce one person');

  await transaction(`UPDATE payments SET reference='CORRECTED-REFERENCE' WHERE booking_id='${pending}';`);
  check(Number((await row(`SELECT count(*) AS n FROM payment_revisions r JOIN payments p ON p.id=r.payment_id WHERE p.booking_id='${pending}'`)).n) === 1, 'Proof/reference corrections preserve revisions');
  await rejects(`DELETE FROM audit_logs;`, /append-only/, 'Audit evidence is append-only');
  await rejects(`DELETE FROM makeup_services WHERE id='${soft}';`, /foreign key/, 'Referenced service deletion is restricted');

  await db.exec(`UPDATE makeup_services SET price=600.01 WHERE id='${soft}'`);
  const odd = '82000009-0000-0000-0000-000000000009';
  await transaction(`${newBooking(odd, future(13))} ${deposit(odd,'ODD-CENT',300.01)}`);
  b = await row(`SELECT required_down_payment,balance_after_deposit FROM bookings WHERE id='${odd}'`);
  check(b.required_down_payment === '300.01' && b.balance_after_deposit === '300.00', 'Odd-cent 50 percent rounding matches C# Money.Half');
  await transaction(`UPDATE bookings SET status='Rejected',note='Outside route after manual review',reviewed_by='${admin}',reviewed_at=now() WHERE id='${odd}';`);
  b = await row(`SELECT cancellation_fee,refund_due FROM booking_closures WHERE booking_id='${odd}'`);
  check(b.cancellation_fee === '0.00' && b.refund_due === '0.00', 'Unverified rejected payments create no fee or imaginary refund');

  await db.exec(`UPDATE makeup_services SET price=600 WHERE id='${soft}'`);
  const past = '82000010-0000-0000-0000-000000000010';
  await transaction(`${newBooking(past,future(14))} ${deposit(past,'FINAL-DEPOSIT')}
    SELECT confirm_booking('${past}','${admin}');`);
  // Owner-only fixture setup to simulate passage of time without waiting days.
  // Production operations never disable triggers. Re-enable BEFORE testing writes.
  await db.exec(`BEGIN; ALTER TABLE bookings DISABLE TRIGGER booking_guard;
    UPDATE bookings SET starts_at=starts_at-interval '30 days',ends_at=ends_at-interval '30 days',
      reserved_until=reserved_until-interval '30 days' WHERE id='${past}';
    SET CONSTRAINTS ALL IMMEDIATE;
    ALTER TABLE bookings ENABLE TRIGGER booking_guard; COMMIT;`);
  await rejects(`UPDATE bookings SET status='Completed' WHERE id='${past}';`, /full verified balance/, 'Past appointment cannot complete without full payment');
  await transaction(`INSERT INTO payments(booking_id,method_id,purpose,amount,reference,status,submitted_by,verified_by,verified_at)
    VALUES('${past}','70000000-0000-0000-0000-000000000004','balance',300,'FINAL-CASH','Verified','${admin}','${admin}',now());
    UPDATE bookings SET status='Completed' WHERE id='${past}';`);
  b = await row(`SELECT status,payment_status,remaining_balance FROM booking_financial_summary WHERE id='${past}'`);
  check(b.status==='Completed' && b.payment_status==='Paid in Full' && Number(b.remaining_balance)===0, 'Exact verified final Cash balance and completion commit together');
  check(Number((await row(`SELECT count(*) AS n FROM artist_schedule_entries WHERE booking_id='${past}'`)).n)===0, 'Completion releases active calendar reservation');

  // Owner-configurable down payment policy (default 50%, editable 1–100).
  await db.exec("UPDATE business_settings SET down_payment_percent=40");
  const policy = '82000013-0000-0000-0000-000000000013';
  await transaction(`${newBooking(policy, future(16))} ${deposit(policy, 'POLICY-40', 240)}`);
  b = await row(`SELECT down_payment_percent,required_down_payment,balance_after_deposit FROM bookings WHERE id='${policy}'`);
  check(b.down_payment_percent === '40.00' && b.required_down_payment === '240.00' && b.balance_after_deposit === '360.00', 'Booking snapshots the configured 40% policy and derives its balance');
  await rejects(`UPDATE bookings SET down_payment_percent=50 WHERE id='${policy}';`, /immutable/, 'Recorded down payment policy is frozen per booking');
  await rejects(`${newBooking('82000014-0000-0000-0000-000000000014', future(16))}
    ${deposit('82000014-0000-0000-0000-000000000014','OLD-50-AMOUNT',300)}`, /exact recorded down payment/, 'Stale 50% deposit amount is rejected under a 40% policy');
  await rejects(`UPDATE business_settings SET down_payment_percent=101`, /whole number between 1 and 100/, 'Down payment percentage is validated between 1 and 100');
  await rejects(`${newBooking('82000015-0000-0000-0000-000000000015', future(17))}
    ${deposit('82000015-0000-0000-0000-000000000015','OVERPAY-40',240.01)}`, /exact recorded down payment/, 'One-cent overpayment is rejected');
  await db.exec("UPDATE business_settings SET down_payment_percent=50,buffer_minutes=45,travel_fee_informational=250");

  const extras = '82000011-0000-0000-0000-000000000011';
  await rejects(`${newBooking(extras,future(15))}
    INSERT INTO booking_addons(booking_id,addon_id) VALUES('${extras}','50000000-0000-0000-0000-000000000001');
    ${deposit(extras,'MISMATCH')}`, /aggregates/, 'Extra-line snapshots must match quoted aggregate at commit');
  const cash = '82000012-0000-0000-0000-000000000012';
  await rejects(`${newBooking(cash,future(15))}
    INSERT INTO payments(booking_id,method_id,purpose,amount,reference,submitted_by)
    VALUES('${cash}','70000000-0000-0000-0000-000000000004','down_payment',300,'CASH-DEPOSIT','${customer}');`, /GCash, Maya or Bank/, 'Cash cannot be used for down payments');
  await rejects(`INSERT INTO media_assets(object_key,purpose,content_type,byte_length,uploaded_by)
    VALUES('invalid.svg','catalog','image/svg+xml',100,'${admin}');`, /check constraint/, 'Media metadata rejects unsupported image types');
  const before = await row(`SELECT reserved_until,created_at FROM bookings WHERE id='${group}'`);
  await db.exec('UPDATE business_settings SET buffer_minutes=90,travel_fee_informational=500');
  check((await row(`SELECT buffer_minutes_snapshot=45 AND travel_fee_informational_snapshot=0 AS ok FROM bookings WHERE id='${group}'`)).ok, 'Settings changes preserve the booked buffer and zero unknown-transportation snapshot');
  await db.exec(`UPDATE bookings SET note=note,created_at=now()-interval '1 year' WHERE id='${group}'`);
  const after = await row(`SELECT reserved_until,created_at,updated_at>created_at AS touched FROM bookings WHERE id='${group}'`);
  check(+new Date(before.created_at)===+new Date(after.created_at) && after.touched, 'Creation timestamp is preserved and update timestamp advances automatically');
  console.log(`All ${checks} isolated PostgreSQL schema checks passed.`);
} catch (error) {
  console.error(`FAIL: ${error.message}`);
  if (error.where) console.error(error.where);
  process.exitCode = 1;
} finally {
  await db.close();
}
