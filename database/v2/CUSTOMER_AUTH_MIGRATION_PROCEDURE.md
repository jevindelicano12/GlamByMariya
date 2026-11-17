# Customer-auth 0004: permission contract and future migration procedure

**Local validation only. This is not approval to connect to or change hosted
Supabase.** Production authentication, booking and payment gates remain closed.

**Local revision 2:** 0004 now recognizes only the genuine bootstrap ADMIN-only
backend-management row for existing `postgres` (no INHERIT/SET), and rejects API
paths through ADMIN, SET or INHERIT. The guarded SQL Editor role-only proposal is
locally tested but requires separate owner approval. See
`0004_ROLE_MEMBERSHIP_REVISION.md` and `CUSTOMER_AUTH_ROLE_PREREQUISITES.md`.
Provider-only provisioning remains an alternative; no hosted action is approved.

**Execution package after role provisioning:** the owner now reports both roles
created with the expected restricted attributes and bootstrap ADMIN-only rows.
Use `0004_EXECUTION_READINESS.md` for the exact hashed-file approval, mandatory
fresh operator/ACL checks, temporary owner capability, failure cleanup and the
read-only post-migration script. Do not repeat role creation in step 3.
The final safety/recovery decision there is **BLOCKED** until fresh hosted
preflight and actual backup/restore evidence are supplied. Cleanup verification
in `tests/0004_owner_membership_cleanup_verification.sql` is independent of auth
tables and must pass after success, rollback or reconnection; migration rollback
does not remove the separately committed temporary membership.

## Ordering and applicability

Explicit auth manifest:

1. `0001_catalog_schedule_foundation.sql`
2. `0002_customer_reservation_foundation.sql`
3. `0004_customer_auth_compatibility.sql`

0003 is a separate payment candidate, **not a dependency**. Never glob the
migrations directory. On the reported hosted V2 foundation, do not rerun 0001/0002:
only 0004 would be considered, after separate inspection and approval.

0004 is one transaction with lock/statement timeouts and an advisory migration
lock. It rejects existing auth objects, unexpected Customer policies and occupied
foundation Customers. It neither deletes data nor fabricates provider subjects,
profiles or passwords for old rows. Existing Customer data requires a separate
mapping migration; do not erase it to satisfy this preflight. Catalog/schedule
data can remain. Reservation tables, indexes and the buffered exclusion are not
altered.

It is intentionally one-shot, not `IF NOT EXISTS` drift concealment. Applying the
same file again raises an error and leaves existing rows/schema unchanged.
Repeatability means the same file works on another clean 0001/0002 foundation,
not that a migration should be rerun over itself. No EF history or hosted
migration ledger is silently changed.

## Added database contract

- `customers`: adds `app_id`, `is_active`, `locked_until`, `first_name`,
  `last_name`, `age_at_registration`, `gender`, `address`, `password_hash`,
  `security_stamp`, `birthday`, `suffix`; makes email required. Includes
  nonempty/bounded profile checks, supported gender/suffix, nonfuture birthday
  in Manila, nonnegative derived age, nonzero UUID, normalized-email uniqueness,
  and unique `(id,app_id)`. No legacy 13–120 age rule is reintroduced.
- `admin_users`: minimum ownership/security projection required by authoritative
  reads; restricted Admin/SuperAdmin role, inactive default. No staff seed,
  invitation/provisioning function, public role input or first-user elevation.
- `account_identities`: exactly one unique physical owner; composite owner/UUID
  foreign keys; globally unique normalized login and Supabase subject; consistent
  Unknown/Supabase states; positive epoch. Common initialization triggers on
  Customers/staff ensure UUID and login conflicts also serialize across roles.
- `account_security_generations`: unique retained identity FK and positive bigint.
  Insert must start at 1; updates advance by exactly 1 only through the definer
  role. Runtime has no delete/reset privilege.
- `auth_mvc_signup_attempts`: protected profile, 32-byte browser-secret hash,
  expected subject, expiry capped at 30 minutes, operation/consumption fences,
  verification limit 5, resend time, unique operation and unresolved email.
  Consumed attempts and established subjects cannot be rewritten; uncertainty
  remains reserved even after expiry pending operator reconciliation.
- Private trigger functions:
  `initialize_identity()`, `guard_owner()`, `guard_identity_security()`,
  `guard_generation()`, `guard_signup_attempt()`. Seven triggers in total.
  Owner keys/logins and provider bindings are immutable. Credential transitions
  allow Ready→Pending with epoch+1, Pending→Ready/ReconciliationRequired at the
  same epoch, or Pending→ReconciliationRequired with epoch+1. There is no unsafe
  SQL operator repair path from ReconciliationRequired.
- The only callable runtime function is
  `v2_authz_api.revoke_my_sessions(uuid,uuid,text,bigint,bigint)`: validates the
  Customer, provider, active/lock status, epoch and generation under generation →
  Customer → staff → identity locks, then advances the generation. It accepts
  only a genuine `glam_v2_backend` database session. SQL arguments are not proof
  of browser identity: the existing protected request accessor supplies them.

The existing MVC transaction consumes the claimed attempt, inserts the Customer,
promotes only its newly initialized Unknown identity and inserts generation 1
atomically. No second authentication system or local password login was added.

## Effective runtime permissions

`glam_v2_backend` must be LOGIN, NOINHERIT, NOSUPERUSER, NOBYPASSRLS,
NOCREATEDB, NOCREATEROLE, NOREPLICATION, not an object owner, and join no roles.
The sole incoming-membership exception is non-superuser CREATEROLE `postgres`
with ADMIN true, INHERIT/SET false, granted by bootstrap OID 10 named
`supabase_admin` with SUPERUSER. This recognizes existing administrator trust,
not confinement of postgres. It is a trusted application-server credential,
never a browser/Supabase JWT role.

It receives SELECT on the five private auth tables so authoritative ownership and
uniqueness reads cannot hide collisions. Mutations are **column-scoped**:

| Object | Backend mutation privileges |
| --- | --- |
| `customers` | INSERT(`app_id,email,first_name,last_name,full_name,age_at_registration,gender,address,password_hash,is_active,suffix,birthday`); UPDATE(`password_hash,security_stamp`) |
| `admin_users` | UPDATE(`app_id`) only for row locking; immutable-key trigger prevents changing its value |
| `account_identities` | UPDATE(`auth_state,supabase_user_id,credential_epoch,credential_sync_state`) on Customer identities only; **no INSERT** |
| `account_security_generations` | INSERT(`account_id,generation`); UPDATE(`generation`) for row locking; trigger forbids direct backend generation changes |
| `auth_mvc_signup_attempts` | INSERT(`attempt_id,browser_secret_hash,normalized_email,encrypted_profile,state,expires_at`); UPDATE(`state,provider_subject,next_resend_at,operation_id,consumed_operation_id,verification_attempts`) |
| `customers_id_seq` | USAGE only |
| `v2_authz_api.revoke_my_sessions` | EXECUTE only |

No runtime staff INSERT, staff sequence usage, private-function access, DDL,
DELETE, TRUNCATE, trigger creation, identity-owner rewrite or reservation/catalog
mutation grant is added. Customer insertion additionally requires the matching
consumed attempt via RLS. Generation insertion requires a Ready bound Customer.

`glam_v2_authz_owner` is NOLOGIN, NOINHERIT and non-elevated/non-bypass. It owns
only the two auth schemas/functions, not private tables. It has SELECT on owners,
identities and generations; identity-initialization INSERT; and the minimum
column UPDATE rights needed for locks/revocation. It remains subject to forced
RLS. Only the dedicated **current migration operator** may receive its management
membership. Any INHERIT/SET capability used for non-superuser object-ownership
transfer is temporary; revoke that operator-granted row afterward. A provider-
granted ADMIN-only row can remain without INHERIT/SET. No API/runtime role may
obtain it. See the role-prerequisite document for grantor-specific cleanup.

`anon`, `authenticated`, `service_role` and `PUBLIC` receive **no private table,
column, sequence or auth-function permissions** and no auth RLS policies. This
also denies a BYPASSRLS `service_role`: bypass does not override missing ACLs.
The migration revokes existing table and column grants, including creator defaults,
and rejects remaining effective inherited or SET ROLE-reachable access.
The role-independent PUBLIC probe is tested. Auth schemas deny API usage;
PUBLIC/default function EXECUTE is explicitly revoked. Existing shared public
schema USAGE may remain; CREATE is revoked from API/runtime/PUBLIC roles.

These permissions authorize a trusted server, not tenant-specific database
sessions. Backend compromise remains a trust-boundary failure; public claims or
client-settable PostgreSQL settings are never an actor proof. This migration
does not globally rewrite Supabase managed role memberships or default privileges.
Future migrations must repeat explicit ACL/RLS hardening for their own objects.

## Local evidence

Run `tests/CustomerAuthenticationIntegrationChecks/Run-Disposable.ps1` from the
workspace root after selecting a fresh exact allowlisted cluster directory.
The runner uses only 0001, 0002 and 0004—no auth fixture, identity candidate or
payment SQL.

- **46** actual MVC/HTTP + PostgreSQL checks passed with fake Supabase.
- **120** migration/permission checks passed, including actual restricted-role
  connections, BYPASSRLS API denial, PUBLIC column-grant removal, inherited and
  NOINHERIT/SET ROLE rejection, non-superuser migration/ownership transfer,
  safe ordering/reapplication, retained data, duplicate subject/role ownership,
  concurrent revocation, rollback and Pending Payment buffer exclusion.
- Tested on fresh PostgreSQL 18 clusters. The second successful database uses a
  non-superuser migration operator, not the runtime login. No hosted result is
  implied.
- Revision 2 additionally passed 29 role-compatibility checks and all 46 MVC checks
  against roles actually created by non-superuser postgres, with no hosted access.

## Future hosted procedure — do not execute without separate approval

1. Obtain **read-only target inspection approval** for the NEW V2 project only.
   Verify project reference/host/database, server major version, backups/restore
   route, the six foundation definitions and forced RLS, btree_gist namespace,
   reservation exclusion, empty Customers, absent auth objects, and migration
   history. Verify all ACLs/column ACLs, PUBLIC grants, creator default ACLs,
   database/schema/object owners, role attributes and membership options,
   including roles reachable through INHERIT or SET ROLE. Do not inspect V1.
   Local proof is PostgreSQL 18; compatibility with the actual hosted major must
   be confirmed (the ACL checks use PostgreSQL 17+ MAINTAIN privilege syntax).
2. Produce a reviewed preflight record and separate **0004 execution approval**.
   Resolve any collision, occupied Customer table, unexpected policy, API-reachable
   grant or ownership issue first. Do not disable RLS, clear data, grant BYPASSRLS
   to runtime, or substitute `service_role` to make preflight pass.
3. **Already completed per owner; do not repeat role creation.** For historical
   provisioning only, the separately approved `prerequisites/customer_auth_roles_sql_editor.sql`
   provisions both roles with PASSWORD NULL,
   validates expected bootstrap memberships and enables no object access.
   Credential establishment is a separate secure action: do not put a password
   in saved SQL, this migration, source, command transcript or browser. The migration
   operator must own/can alter foundation Customers, authoritatively read them
   despite forced RLS, manage ACLs, create schemas/functions and transfer function
   ownership. For a non-superuser operator, explicitly grant only that trusted
   operator membership in `glam_v2_authz_owner` with INHERIT/SET as required.
   API/runtime memberships remain forbidden. The original provider-only proposal
   remains an alternative if this exact guarded SQL Editor path is unavailable.
4. Pause writers, retain runtime gates and verify the approved file hash:
   `Get-FileHash database/v2/migrations/0004_customer_auth_compatibility.sql -Algorithm SHA256`.
   Use a reviewed PostgreSQL client connection configuration and error-stop mode
   to apply **that exact file once**. Example shape, not an instruction to run:
   `psql "service=<approved-v2-migration-service>" -X -v ON_ERROR_STOP=1 -f database/v2/migrations/0004_customer_auth_compatibility.sql`.
   The file owns its transaction; do not execute a directory glob, 0001/0002 again,
   0003, legacy EF initializers or the local test runner on hosted Supabase.
5. Confirm commit, record file hash/version in the agreed migration ledger, and
   revoke any temporary operator membership in the definer owner. Verify exact
   table/column/sequence/function grants, function owners/search paths, triggers,
   all ten tables' forced RLS, absent API access and unchanged reservation
   exclusion. If the file errors, stop and confirm rollback; explicitly ROLLBACK
   an aborted live session before committing grantor-specific cleanup. A client
   disconnect is not proof of rollback or of no running backend. Confirm outcome
   independently, verify the two bootstrap rows and four negative capabilities,
   and investigate before any retry; never drop or recreate populated auth objects.
6. Keep production gates closed. Full normal application startup still needs MVC
   compatibility views/settings and protected persisted Data Protection keys.
   A later separately approved controlled integration host can exercise real
   signup/OTP/password/recovery with synthetic emails, actual backend credentials
   and HTTPS/key persistence. Verify `/user`, duplicate/expired OTP and resend
   semantics and operator reconciliation before considering runtime activation.

No database-only migration can verify Supabase delivery, token semantics or
production cookie behavior. No hosted application or migration is authorized by
these local results.
