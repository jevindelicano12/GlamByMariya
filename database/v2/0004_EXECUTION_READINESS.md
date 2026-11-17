# Migration 0004 revision 2 — execution approval package

**Final safety/recovery recommendation: BLOCKED for execution approval.**
The artifact passes the local review, but current hosted preflight and actual
backup/restore evidence have not been supplied. This supersedes the earlier
conditional PASS; it is not a discovered change to the approved migration.
No hosted query, permission change or migration was performed in this review.

## Exact artifact

`database/v2/migrations/0004_customer_auth_compatibility.sql`

Revision: **local revision 2**, byte-for-byte matching the recorded tested hash:

`7A089ACC1B64447B3ED5D24142A73C2D966EA7D9B572489267B28594987DE84A`

It matches `0004_ROLE_MEMBERSHIP_REVISION.md`. Do not reformat, edit, combine
inside another transaction, or substitute the pre-revision file.

The reported LOGIN/NOLOGIN, NOINHERIT, non-elevated/non-bypass roles match. The
reported ADMIN-only postgres memberships match, subject to the required genuine
grantor **OID 10 / supabase_admin / SUPERUSER**. Neither role joins any role.
Existing postgres remains a trusted database administrator capable of self-grant.

## Compatibility and permissions review

- Explicit dependencies are **0001 and 0002 only**. Do not rerun them on hosted
  V2. **0003 is neither required nor authorized**; no payment object is created.
- PostgreSQL 17.11 supports the migration's membership options/catalog fields,
  recursive queries, MAINTAIN privilege checks, RLS, identity columns and
  PL/pgSQL features. No known 17.11 syntax incompatibility was identified.
  Execution evidence is PostgreSQL 18.6, not a 17.11 or managed-hosting test.
- 0004 is transactional, one-shot, with timeouts, an advisory lock and fail-closed
  foundation, role, membership and effective-access checks. Existing Customer
  data or conflicting auth objects stop it; no reset/backfill is attempted.
- Tables remain owned by the migration/table administrator (`postgres` for this
  procedure). Both new auth schemas and all six functions are owned by restricted
  NOLOGIN `glam_v2_authz_owner`; forced RLS remains enabled.
- Backend receives five-table security reads, exact registration/credential
  column rights, Customer sequence USAGE and only the revocation API function.
  It receives no staff provisioning, identity INSERT, general UPDATE, deletion,
  schema CREATE or booking/payment grants.
- Creator defaults apply when postgres creates objects; ownership transfer does
  **not** erase them. 0004 explicitly clears table/column/sequence/function
  permissions and PUBLIC function execution before narrow grants. Its final
  checks reject residual inherited/effective API access, including authenticator.
  It does not change managed/global default privileges or assume those are empty.
- Supabase-specific object-management hooks, actual current ACLs and execution
  operator rights are not independently observed here. Failures must stop and
  roll back, not trigger broader privileges or changes to managed defaults.

The previous local evidence remains: 29 role checks + 46 MVC checks on SQL
Editor-created-role schema, separately 120 migration/permission + 46 MVC checks,
17 provider comparison, 248 isolated HTTP and 56 signup-retry checks.

### Final verification correction and local proof

The original post-verifier inventoried triggers/policies/constraints/indexes but
could silently omit a dropped object; it also omitted function bodies and
untrusted SET-reachable privilege checks. Only the verifier/procedure/tests were
corrected. **The migration and its approved SHA-256 were not changed.**

Compared with 0001/0002 + approved 0004, the expected complete shape is:

| Object | Count / scope |
|---|---|
| Tables / columns | 10 / 82; four tables and 44 columns added by 0004 |
| Auth schemas / functions | 2 / 6; all six source bodies match 0004 exactly |
| User triggers / policies | 7 / 17, including timing, targets, enabled state, roles and predicates |
| Non-NOT-NULL constraints | 68 total; 48 on the five private tables |
| Indexes | 28 total; 18 on the five private tables, including constraint-backed indexes |

The verifier now compares per-table/schema counts AND definition fingerprints,
rejecting missing, changed and extra objects. Full definitions are returned for
inspection; the MD5 fingerprints are comparison aids, not artifact approval
hashes. Column nullability uses `attnotnull`; PG18-only NOT NULL catalog rows are
excluded. Function bodies/config/signatures/security attributes, policy
permissiveness, partial-index predicates, constraint validation and internal
constraint-trigger enablement are checked. Backend AND definer effective column
rights are compared in both directions; API checks include SET-reachable roles.

Run the offline test only locally:
`python database/v2/tests/check_0004_verifier.py`.
It copies the retained stopped **synthetic** PG18 fixture, starts no listener,
creates no roles and applies no migrations. Fake rows and a pre-existing fixture
fault trigger are removed only in rolled-back transactions on the copy.
The ten-section clean baseline, **24 structural/privilege fault cases**, context,
nonempty-count and payment-object failures, plus independent cleanup checks pass.
Cleanup tests use the exact A/C blocks below, a committed grant, an actual
transaction error/rollback, reconnection, an inactive self-grant and absent auth
objects. Latest evidence: `temp/0004-verifier-checks-2362dbe8/`.
This is PG18.6 proof, not a fresh hosted check or a PG17 execution result.
`pg_get_*` formatting/search-path differences on 17 may cause a safe mismatch;
review the returned definitions rather than accepting/rebaselining hosted drift.

Complete original exclusion, still untouched by 0004:

```sql
EXCLUDE USING gist
  (artist_id WITH =, tstzrange(start_at, reserved_until, '[)') WITH &&)
  WHERE (status IN ('Pending','Pending Payment','Confirmed','InProgress'))
```

Its type, validation, nondeferrable state, supporting valid/ready/live GiST index,
keys/operators, half-open bound, **reserved_until**, and complete predicate must
match the saved hosted preflight definition. Source preservation is verified;
current hosted preservation cannot be claimed without that before/after evidence.

## Recovery evidence required before approval

The owner must supply evidence identifying the **new V2 project reference** and:

- An existing completed, recoverable pre-change backup/recovery point (UTC time,
  identifier and retention/expiry); not just “backups enabled” or “tables empty”.
  It must predate the temporary owner grant, or explicitly account for restoring
  that grant and independently checking/revoking it afterward.
- Actual project plan and recovery method: daily backup, PITR with a confirmed
  recoverable time range, or an independently held consistent export. Free-tier
  backup availability must not be assumed. An export needs documented schema,
  data, extension, ownership, ACL and custom-role coverage and restore order;
  a generic schema dump alone is not a tested recovery route.
- Named authorized restorer, access confirmed without sharing credentials,
  approved downtime/data-loss tolerance (RTO/RPO), restore destination/procedure,
  and existing rehearsal evidence or explicit owner acceptance of an untested
  restore. A real restore requires **separate approval**.
- A plan to recheck provider-managed/custom roles, passwords, memberships,
  object owners/ACLs, RLS, foundation/exclusion, auth/payment absence or approved
  restored state, migration ledger and closed runtime gates after restoration.
  Keep writers paused until outcome and cleanup are established.

[Supabase backup documentation](https://supabase.com/docs/guides/platform/backups)
was reviewed, not this project's backup configuration. Daily backups are plan
dependent; PITR is separate. Restoration can cause downtime and discard later
changes. Database backups do not contain Storage API object bytes, and restored
custom-role passwords may require reset. Database rollback/restore is not an undo
for outside email/provider effects; none are authorized here. No backup, export,
restore rehearsal, hosted resource inspection or restore was performed.

## Mandatory pre-execution conditions

Before any temporary grant or migration, the owner/operator must:

1. Confirm the dashboard's **NEW glam-by-mariya-v2 project reference**, not merely
   a database name of postgres. Supply the recovery evidence above, approved window,
   paused application writers and unchanged runtime gates.
2. Recompute the file hash locally:
   `Get-FileHash database/v2/migrations/0004_customer_auth_compatibility.sql -Algorithm SHA256`.
3. Run/review only `database/v2/tests/0004_hosted_read_only_preflight.sql` afresh.
   Foundation definitions, full exclusion definition, authoritative zero counts,
   absent auth/payment objects, history and role state must still match.
   Do not mistake an RLS-filtered zero or unreadable history for evidence.
4. In sections 01/02/03/04/20, confirm the intended current/session operator is
   postgres, with CREATEROLE, authoritative forced-RLS reads, ownership/control
   of foundation Customers and its sequence, public-schema ACL control, database
   CREATE for new schemas and PL/pgSQL USAGE. The operator must be able to create
   functions in and transfer them to the definer-owned schemas.
5. Review default/column/PUBLIC/inherited ACLs and API administrative paths.
   Current postgres-owned object defaults may be broad but must be revocable by
   that operator. Unexpected managed ownership, unrevocable grants, API paths into
   trusted roles or unresolved default-ACL evidence is **BLOCKED**.
6. Both target roles already exist: **do not run either role-creation proposal
   again**. Confirm only the two expected bootstrap ADMIN-only rows exist and no
   backend/owner SET or INHERIT capability is currently enabled for postgres.
   Confirm the bootstrap grantor is OID 10, named supabase_admin, with SUPERUSER.
   Run `tests/0004_owner_membership_cleanup_verification.sql`; all its checks must
   be true before A, as well as after C. It depends only on role catalogs, not 0004.

   Read-only bootstrap identity check:

   ```sql
   SELECT oid, rolname, rolsuper
   FROM pg_catalog.pg_roles WHERE rolname='supabase_admin';
   ```

If a condition is unknown or fails, the decision for that execution is
**BLOCKED**. Return the evidence; do not try the migration as a privilege probe.

Owner-reported, not freshly rechecked here: hosted 17.11, 0001/0002 applied, six
forced-RLS foundation tables, previous zero Customer/reservation counts and both
restricted roles/bootstrap memberships. Current project identity, occupancy,
definitions/exclusion, ACLs/defaults/operator capability, active sessions,
collisions/history and recoverable backup remain fresh hosted/owner checks.

## Exact future SQL Editor procedure — only after separate approval

Approval must cover the temporary owner-role capability and cleanup as well as
the exact hashed migration. It must exclude credentials, additional role creation,
payment 0003, provider Auth calls and application activation.

### A. Enable only the temporary owner capability

Run this complete block as the same verified postgres operator:

```sql
BEGIN;
DO $$
BEGIN
    IF current_user <> 'postgres' OR session_user <> 'postgres'
       OR NOT EXISTS (SELECT 1 FROM pg_roles
           WHERE rolname=current_user AND rolcreaterole AND NOT rolsuper)
       OR pg_has_role('postgres','glam_v2_backend','SET')
       OR pg_has_role('postgres','glam_v2_backend','USAGE')
       OR pg_has_role('postgres','glam_v2_authz_owner','SET')
       OR pg_has_role('postgres','glam_v2_authz_owner','USAGE') THEN
        RAISE EXCEPTION 'Unexpected operator or pre-existing runtime capability';
    END IF;
END $$;
GRANT glam_v2_authz_owner TO postgres
    WITH ADMIN FALSE, INHERIT TRUE, SET TRUE;
DO $$
BEGIN
    IF NOT pg_has_role('postgres','glam_v2_authz_owner','SET')
       OR NOT pg_has_role('postgres','glam_v2_authz_owner','USAGE')
       OR NOT EXISTS (
         SELECT 1 FROM pg_auth_members m
         WHERE m.roleid=(SELECT oid FROM pg_roles WHERE rolname='glam_v2_authz_owner')
           AND m.member=(SELECT oid FROM pg_roles WHERE rolname='postgres')
           AND m.grantor=(SELECT oid FROM pg_roles WHERE rolname='postgres')
           AND NOT m.admin_option AND m.inherit_option AND m.set_option) THEN
        RAISE EXCEPTION 'Temporary definer-owner capability was not established';
    END IF;
END $$;
COMMIT;
```

No backend membership or object grant is permitted. The existing provider-granted
ADMIN-only rows are not modified or removed.

### B. Execute exactly the approved file once

Paste/run the **entire unmodified** `0004_customer_auth_compatibility.sql`,
including its own BEGIN and COMMIT. Do not wrap it in another transaction or
append it to any migration/provisioning directory batch. Confirm success/commit
from the client; a returned click or partial result is not proof.

The temporary owner GRANT in A is a **separate committed transaction**:
an error/rollback in B does **not** remove it. Cleanup below is mandatory after
success, error, timeout or interruption. If B's outcome is uncertain, do not
replay it. A browser disconnect does not prove the server stopped; the authorized
operator must identify the migration backend and resolve any still-running/open
transaction before cleanup or outcome inspection. Cancellation/termination, if
needed, requires separate operator authorization and proof of rollback afterward.
If active state cannot be resolved, keep gates/writers closed and escalate.

### C. Always remove the operator-granted owner capability

If B errored and the session is still in an aborted transaction, run `ROLLBACK;`
first as a separate command. As the verified current/session postgres operator,
commit cleanup separately from all verification:

```sql
BEGIN;
REVOKE glam_v2_authz_owner FROM postgres GRANTED BY postgres;
COMMIT;
```

Then, in a new read-only request:

```sql
SELECT
    pg_has_role('postgres','glam_v2_authz_owner','SET') AS owner_set,
    pg_has_role('postgres','glam_v2_authz_owner','USAGE') AS owner_inherit,
    pg_has_role('postgres','glam_v2_backend','SET') AS backend_set,
    pg_has_role('postgres','glam_v2_backend','USAGE') AS backend_inherit;
```

**All four values must be false.** This removes the temporary postgres-granted
row only. The supabase_admin-granted ADMIN-only management rows intentionally
remain. Never impersonate supabase_admin, revoke its management grants, grant
backend SET/INHERIT, disable RLS or broaden privileges to fix an error.

Independently run `database/v2/tests/0004_owner_membership_cleanup_verification.sql`.
Every `checks` value must be true. In addition to the four effective negatives it
requires exactly the two original physical bootstrap rows, correct grantor
OID/name/attributes/options, restricted role attributes and **no** postgres-granted
row, even one with all options false. It does not reference auth objects, so it
can be used when 0004 rolled back or has not been applied.

If the client disconnects, reconnect as authorized postgres, resolve outstanding
B transaction state, then perform C before other application work. If cleanup or
outcome cannot be verified, record **BLOCKED**; do not claim deployment completion.

### Failure/recovery decisions

| Observed outcome | Required response |
|---|---|
| A fails or its commit is uncertain | Stop; inspect role catalogs from an independent authorized session. Roll back an aborted transaction, remove any self-granted row, verify C. No B until reapproved. |
| B fails before commit | Confirm rollback/backend completion; C is still mandatory because A committed separately. Preserve logs and rerun read-only foundation/collision checks; do not replay without review. |
| B commit is uncertain, client timed out/disconnected | No replay and no “rollback succeeded” claim. Resolve active backend, perform C, use exact object checks/ledger/client evidence to classify committed vs absent; ambiguity is BLOCKED. |
| B committed, post-check fails | ROLLBACK cannot undo the committed migration. Keep writers/gates closed; C and preserve evidence. No ad-hoc DOWN SQL, drops or resets. Separately approve a forward correction or restore after its project-wide impact is assessed. |
| C fails or cannot be checked | Unresolved administrative capability: BLOCKED even if B passed. Reconnect/escalate to the authorized operator; never remove bootstrap grants or widen runtime privileges. |
| Restore is selected later | Separate approval, approved recovery point, pause writers and accept loss/downtime; recheck membership cleanup, secrets/configuration boundaries and all foundation/RLS/ACL/exclusion evidence before reopening anything. |

## Read-only post-migration verification

After a confirmed B commit and C cleanup, run only:

`database/v2/tests/0004_post_migration_verification.sql`

Return all ten section/evidence rows. Required outcomes:

All `*_ok`, `all_*`, exact routine-count and required-role-presence checks must be
true; all mismatch/violation arrays and API administrative/elevated-role arrays
must be empty. Four postgres capabilities and forbidden sequence/schema rights
must be false; Customer sequence USAGE must be true. Project identity, unchanged
history/ledger and the saved full exclusion comparison remain human checks.
Incomplete/truncated result exports are not evidence of a pass.

1. Correct project/operator/version; all **ten** public application tables enable
   and force RLS. Private table owners remain postgres, not the runtime/definer.
2. Customers, reservations and all four new private tables are authoritatively
   empty. No Customer, Admin or SuperAdmin application account is seeded.
3. Original restricted role attributes and exactly the expected two bootstrap
   ADMIN-only rows remain; all four postgres SET/INHERIT values are false.
4. Both auth schemas and all six functions belong to glam_v2_authz_owner.
   Function signatures/return types/security-definer settings match; all six use
   only `search_path=pg_catalog`. Only initializer/revocation are SECURITY DEFINER.
5. Seven expected enabled triggers and seventeen expected policies match the
   approved file. No policy targets API roles or PUBLIC.
6. Backend effective column rights exactly match the authoritative permission
   table; broad mutation violations are empty. Customer sequence USAGE is true,
   its SELECT/UPDATE false, and all staff-sequence rights false. Only revocation
   function EXECUTE is true; no private schema access or schema CREATE.
7. All API administrative/table/column/function/schema/sequence violation arrays
   are empty. PUBLIC/default/inherited rights are included in effective checks.
8. Identity ownership/subject/login uniqueness, positive security state, attempt
   fences and all expected valid/ready indexes match 0004. Compare the **complete**
   reservation exclusion definition byte-for-byte with the preflight snapshot:
   GiST artist equality, half-open start_at→reserved_until overlap and Pending,
   Pending Payment, Confirmed, InProgress predicates must be unchanged.
9. Payment tables remain absent. No Auth API operation or runtime login check is
   part of this migration verification; do not call the revocation function.
10. Record project reference, approved hash/revision, operator, time, commit,
    cleanup and complete verification evidence in the agreed deployment ledger.
    0004 does not automatically write Supabase or EF migration history; do not
    invent a migration-history insertion command as part of this approval.

## Short execution checklist (future separate approval only)

1. Receive current V2 preflight, independent clean-membership result, complete
   exclusion snapshot and acceptable recoverable-backup/restore evidence.
2. Approve the exact hash, A/B/C window and operator; pause writers, keep all
   runtime gates closed, confirm no competing migration transaction.
3. A: establish only temporary owner capability, confirm commit and exact
   postgres-granted ADMIN=false / INHERIT=true / SET=true row.
4. B: run only the unchanged 0004 file once, confirm its transaction outcome.
5. C: always commit grantor-specific cleanup, then verify independently; after
   errors, roll back aborted transactions first; after disconnect, resolve backend.
6. After B commit, export all ten post-check sections, compare full exclusion
   and ledger, and record commit/cleanup/evidence. No 0003 or workflow activation.

Stop for any unknown prerequisite, wrong target/hash/operator, occupied table,
collision, changed definition/ownership/ACL/membership, permission/timeout error,
unresolved transaction, incomplete evidence or failed cleanup/post-check.

Any mismatch/error/inaccessible evidence means **BLOCKED** pending investigation.
Do not rerun, drop or reset populated objects. Migration-only completion still
does not make normal MVC startup or real-provider authentication operational:
compatibility views/settings, protected persisted keys and separate provider
workflow verification remain gated prerequisites.
