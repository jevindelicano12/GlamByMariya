# Hosted Customer-auth preflight — BLOCKED pending hosted evidence

Date: November 16, 2026. Target: **NEW `glam-by-mariya-v2` only**.
Scope: SELECT/metadata inspection only; no execution authorization for 0004.

## Access result

No Supabase database query integration is available in this session. The existing
browser relay is unreachable. No hosted query was executed, no credentials were
sought/read, and no Auth API call, login or registration was attempted.
The fallback script is prepared, not a hosted test result.

## Manual SQL Editor script

`database/v2/tests/0004_hosted_read_only_preflight.sql`

1. Open Supabase yourself and select the **NEW** `glam-by-mariya-v2` project.
   Confirm its project reference against your new-V2 record. Do not use V1.
   A database name such as `postgres` cannot distinguish projects.
2. Open SQL Editor using the intended migration operator role. Paste/run **only
   the preflight file**, not migration 0004, an earlier migration, or a directory
   of SQL. The file is one SELECT with metadata CTEs and read-only count/history
   SELECTs; it contains no DDL/DML, settings changes or SET ROLE.
3. Export/copy **all 20 section/evidence rows**, including full JSON cells. Share
   the results and any exact error, but no credentials, connection strings,
   API keys, account records or migration SQL statements.
4. Stop after SELECT results. Do not provision missing roles, grant membership,
   alter policies or try migration 0004 to test whether it works.

Counts run only when metadata proves the inspector has SELECT and RLS is not
filtering its view. Otherwise the script reports an unknown/blocked count rather
than interpreting an RLS-filtered zero as an empty table. History reads only
version/name columns from known ledgers, never the stored SQL statements.

## Current completion report

| Requested finding | Evidence/status |
| --- | --- |
| Hosted PostgreSQL version | **Unverified**. 0004 uses PostgreSQL 17+ MAINTAIN privilege syntax; local validation was PostgreSQL 18. |
| Six foundation tables/columns/constraints | **Unverified hosted**. Script compares all baseline column types/nullability/defaults and lists expected/actual constraint definitions and all indexes for review. |
| Customer/reservation counts | **Unknown**, not assumed zero. Both authoritative counts are included in the script. |
| Auth object conflicts | **Unverified**. Script checks target tables/indexes/sequence, auth schemas/functions, added Customer columns and named constraints. |
| Role/ownership compatibility | **Unverified**. Both restricted roles must already exist before executing 0004, with the documented attributes and membership restrictions. No creation is authorized now. |
| Effective permissions and RLS | **Unverified**. Script inspects forced RLS, policies/triggers, PUBLIC/column/default ACLs and API roles' inherited/SET ROLE-reachable table/sequence privileges. |
| Migration history | **Unverified**. Script returns known-ledger version/name records and possible alternative ledger names. Missing history alone does not prove manually applied SQL is absent. |
| Payment 0003 | Not executed here. Hosted absence remains unverified; payment object/history markers are included for inspection only. |
| Reservation protection | **Source review passed**: 0004 does not alter the reservation table, exclusion, indexes or grants. Hosted GiST/expression/predicate must still match 0002, including Pending Payment and the `[start_at,reserved_until)` buffer. |
| SQL Editor execution privileges | **Unverified**. Its actual current/session roles, role-management flag, schema CREATE, authoritative visibility and owner capabilities must be inspected. Merely using SQL Editor does not confer superuser or ownership-transfer privileges. |

## Exact readiness blockers

1. No verified hosted target/session or SELECT results are available.
2. Foundation definitions, authoritative empty Customer/reservation counts, absent
   auth objects and 0004 deployment history have not been confirmed.
3. Restricted-role provisioning state, effective memberships/ACLs and the intended
   migration operator's DDL/ownership-transfer authority have not been confirmed.
4. Managed Supabase restrictions cannot be proven by generic CREATEROLE/owner
   catalog flags alone. If SQL Editor's role cannot provision the restricted
   roles, authoritatively read forced-RLS Customers, alter the Customer table/
   sequence/public-schema ACLs, or create/transfer functions to the definer owner,
   the documented procedure needs an appropriately authorized administrative
   provisioning/execution path. Do not weaken runtime roles or RLS as a workaround.

The script does not trial DDL or managed role operations. A non-superuser operator
needs the documented INHERIT/SET capability for `glam_v2_authz_owner` to create
objects in its schemas and transfer function ownership; runtime/API memberships
remain forbidden. Temporary operator membership may only be arranged later,
under separate approval, and must be removed after the future migration.

**Decision: BLOCKED for separate execution approval until this evidence is
returned and reviewed.** This means missing verification, not an observed hosted
schema defect. No migration/application gate should be changed.

Reviewed migration SHA256:
`E39719CA61665A4A7B4FDBFDE08BA63395316F98608DC6F9072FCDD563100927`

Authoritative future procedure:
`database/v2/CUSTOMER_AUTH_MIGRATION_PROCEDURE.md`.
Migration and application source were not changed during this preflight task.
