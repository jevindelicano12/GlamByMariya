# Customer-auth role prerequisites — proposal, not hosted authorization

## Current decision: local revision 2 supports SQL Editor provisioning

The investigation below described the original 0004 predicate. It is now
superseded by the narrowly scoped, locally tested revision documented in
`0004_ROLE_MEMBERSHIP_REVISION.md`. **No hosted change has been made.**

The backend still joins no roles and remains non-elevated/non-bypass/NOINHERIT.
Its only new allowed incoming row is ADMIN-only management by existing
non-superuser CREATEROLE `postgres`, granted by genuine bootstrap OID 10 named
`supabase_admin`, with SET/INHERIT both false. All other backend membership rows
remain rejected. API paths through ADMIN, SET and INHERIT are explicitly rejected.
The administrator can self-grant later; this is the existing trusted database
administrator, not a restricted runtime identity.

**Recommended proposal:** `prerequisites/customer_auth_roles_sql_editor.sql`.
It creates only the two restricted roles with PASSWORD NULL, validates exactly
the expected creator rows, and grants no object/API permissions. Provider
intervention is no longer inherently needed. The provider-only proposal is
retained unchanged as an alternative; no SQL Editor escalation is proposed.

Local revision evidence: 29 compatibility checks and all 46 real MVC checks
passed on a SQL Editor-created-role schema; the existing 120 migration/permission,
46 MVC, 248 isolated HTTP, 56 signup-retry and 17 comparison checks also passed.
Only PostgreSQL 18.6 was available, not 17.11. See the revision record for limits,
hashes, temporary owner-grant cleanup and the one separately approvable next action.

## Historical pre-revision investigation (superseded, retained for comparison)

## Finding

The owner reports a successful new-V2 foundation preflight on **PostgreSQL
17.11**, with zero Customers/reservations and absent target auth roles/objects.
This task made **no hosted connection or change**.

**Ordinary SQL Editor provisioning does not satisfy unchanged 0004.**
PostgreSQL 16+ automatically grants a non-superuser CREATEROLE creator an
ADMIN-only membership in every role it creates, recorded under the bootstrap
superuser. On Supabase that grantor is `supabase_admin`. The creator cannot
remove that row: an ordinary REVOKE can only warn and leave it; impersonating
the grantor is denied. NOINHERIT does not remove membership rows.

0004 rejects every membership involving `glam_v2_backend`, regardless of the
membership's INHERIT/SET flags. Thus even the automatic
`backend -> postgres; ADMIN=true, INHERIT=false, SET=false` row blocks it.
This is a known contract mismatch, not merely an unknown password/setup issue.
The earlier non-superuser migration test created its runtime roles as a local
superuser; it did not test non-superuser **role creation**.

No migration/application code or membership exception was changed here.

## Required resulting roles

| Attribute | `glam_v2_backend` | `glam_v2_authz_owner` |
| --- | --- | --- |
| LOGIN | true | false |
| INHERIT | false | false |
| SUPERUSER / BYPASSRLS | false / false | false / false |
| CREATEDB / CREATEROLE / REPLICATION | all false | all false |
| Member of other roles | none | none |
| Incoming memberships | **none at all**, including creator management | only the designated `postgres` migration operator |
| Prerequisite password | NULL | NULL |
| Own tables / receive object permissions at this stage | no | no |

The definer owner can have a provider-granted ADMIN-only management row to
`postgres`, with INHERIT/SET both false. This is allowed by 0004's existing
exception when the later migration runs as `postgres`. ADMIN is not harmless:
the trusted administrator can deliberately grant itself capabilities. It belongs
only to the existing administrative operator, never runtime/API roles.

`PASSWORD NULL` disables password authentication, not every possible HBA method.
No password is generated, requested or included in either proposal. Backend
credential establishment is a separate future secure administrative action.
With the strict zero-membership backend contract, ordinary `postgres` may lack
ADMIN authority to set its password; that path also needs provider confirmation.

## Smallest role-only route preserving current 0004

The provider must be willing and able to execute the separately approved,
transactional proposal:

`prerequisites/customer_auth_roles_provider_only.sql`

Its essential commands, **for a genuine provider superuser only**, are:

```sql
CREATE ROLE glam_v2_backend LOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS
    NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD NULL;
CREATE ROLE glam_v2_authz_owner NOLOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS
    NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD NULL;
GRANT glam_v2_authz_owner TO postgres
    WITH ADMIN TRUE, INHERIT FALSE, SET FALSE;
```

Use the full guarded file, not these bare commands. It rejects existing roles,
the wrong operator/version, extra memberships or incompatible attributes.
It creates no tables/schemas, grants no application privileges and runs no
migration. A superuser does not acquire PostgreSQL's automatic creator membership,
so the backend remains membership-free.

This is **not executable by customer SQL Editor `postgres`**, and we must not
try to grant SUPERUSER, `supabase_admin`, BYPASSRLS or extra roles to make it so.
Supabase documents that customer `postgres` cannot become `supabase_admin`.
Provider support availability is unverified; the proposal can be submitted for
separate owner/support approval, not treated as a self-service recipe.

If the provider cannot perform this, there is no tested role-only SQL Editor
fix for the current contract. A separately approved, minimal membership-check
compatibility revision would be a different action; it was not implemented.

`prerequisites/customer_auth_roles_review.sql` reproduces the ordinary creator
case and ends in **ROLLBACK**. It is not an alternative provisioner, and must
not be changed to COMMIT as a workaround. No hosted execution is authorized.

## Later owner transfer — separate migration-window approval

After provider prerequisites and migration approval, `postgres` can use its
owner-role ADMIN-only management row to make a temporary, self-granted capability:

```sql
GRANT glam_v2_authz_owner TO postgres
    WITH ADMIN FALSE, INHERIT TRUE, SET TRUE;
-- Only the separately approved migration/ownership transfers belong here.
REVOKE glam_v2_authz_owner FROM postgres GRANTED BY postgres;
```

INHERIT is needed to create functions in schemas already owned by the definer
role; SET is needed for ownership transfer. This grants no backend membership.
Cleanup removes only the `postgres`-granted row. The provider-granted ADMIN-only
row remains and is not an active INHERIT/SET capability. A generic REVOKE must not
be interpreted as removing the provider's row. Verify both effective capabilities
false and inspect grantors/options after cleanup.

## Defaults and effective privileges

Role creation itself does not create application tables or apply table/function
creator defaults. When 0004 eventually creates objects as `postgres`, the relevant
defaults are **postgres's** global/schema defaults, not `supabase_admin`'s or the
future function owner's. Changing an object's owner does not clear existing ACLs.
0004's explicit object revocations must still clear creator/API/PUBLIC grants.
No ALTER DEFAULT PRIVILEGES or broad managed-object revocation is proposed here.

The actual hosted default-ACL rows were not supplied with the summary, so their
exact grants remain unverified. Do not change `supabase_admin` defaults, move
extensions or revoke managed PUBLIC CONNECT/TEMPORARY/public-schema USAGE.
Those changes are unnecessary for this role prerequisite and can break services.

NOINHERIT also does not cancel PUBLIC object rights. Supabase documents
extension-specific PUBLIC rights (notably pg_net queues/functions) that may expose
more than intended. Review **metadata/effective rights only**, never queued
headers or secret values, before assigning a usable backend credential. Do not
claim these roles have zero database-wide access merely because no grants were
added. No API role or `authenticator` may reach either target role by INHERIT/SET.

## Local verification

`tests/CustomerAuthRolePrerequisiteChecks/Run-Disposable.ps1`:

- **17 assertion groups passed** on a fresh SCRAM/loopback PostgreSQL 18 cluster.
- Real separate non-superuser `postgres` connection with CREATEROLE and no
  BYPASSRLS, not a superuser pretending to be a restricted role.
- Reproduced bootstrap ADMIN rows, ineffective ordinary REVOKE, denied grantor
  impersonation, and the exact unchanged 0004 membership predicate rejecting them.
- Tested provider-only creation against that same predicate; no backend member,
  no API reachability, and only the owner management row remained.
- Tested temporary owner capability, function ownership transfer and grantor-
  specific cleanup, plus creator/PUBLIC default ACL survival and object revocation.
- No 0004/0003 or foundation migration was executed. Test-only probes rolled back;
  the provider proposal's two local roles remain in the stopped disposable cluster.

Only PostgreSQL 18 binaries were installed; the Docker daemon was unavailable.
No PostgreSQL 17.11 execution or managed Supabase behavior was tested. The
reproduced automatic-membership rule is explicitly documented for PostgreSQL 17.
The cluster is retained/stopped at
`temp/v2-role-prerequisites-20261116-3/data`; temporary password/bootstrap files
were removed. Reruns require a new exact path in the wrapper.

## Decision and next action

**Conditional provider-only proposal ready to submit; SQL Editor role
provisioning and migration 0004 remain blocked.** Compatibility is locally
demonstrated, but Supabase support must confirm the administrative path,
membership behavior and later credential management.

**One next action:** ask the owner to authorize submitting the guarded provider-
only role proposal to Supabase Support for feasibility/approval, explicitly
excluding migration execution, credentials, application activation and payments.

## Sources checked November 16, 2026

- PostgreSQL 17: [role attributes / automatic creator grant](https://www.postgresql.org/docs/17/role-attributes.html),
  [role membership](https://www.postgresql.org/docs/17/role-membership.html),
  [REVOKE](https://www.postgresql.org/docs/17/sql-revoke.html).
- Supabase: [managed role membership cannot be revoked by postgres](https://supabase.com/docs/guides/troubleshooting/cant-revoke-a-role-membership-granted-by-supabase_admin),
  [creator defaults](https://supabase.com/docs/guides/troubleshooting/cant-change-the-default-privileges-of-supabase_admin-in-the-public-schema),
  [PUBLIC privileges](https://supabase.com/docs/guides/troubleshooting/custom-role-inherits-privileges-that-were-not-explicitly-granted-ddaa1c).
