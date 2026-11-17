# 0004 local revision 2 — PostgreSQL 17 creator membership

Status: locally implemented/tested only; **no hosted execution approval**.
0004 is reported unapplied, so its existing file is revised rather than adding
a migration that would depend on an unexecutable 0004.

## Exact correction

Previous SHA256:
`E39719CA61665A4A7B4FDBFDE08BA63395316F98608DC6F9072FCDD563100927`

Revised SHA256:
`7A089ACC1B64447B3ED5D24142A73C2D966EA7D9B572489267B28594987DE84A`

The old membership check rejected every row involving the backend role, even
PostgreSQL's automatic creator-management row. The only new backend exception is:

- Target role: `glam_v2_backend`.
- Member: the existing `postgres` role, with CREATEROLE and not SUPERUSER.
- Grantor: bootstrap **OID 10**, named `supabase_admin`, with SUPERUSER.
- `admin_option=true`, `inherit_option=false`, `set_option=false`.

Missing identities/attributes produce a rejection, not SQL NULL acceptance.
Backend membership in any other role, additional members/grantors, self-granted
rows, or enabled SET/INHERIT options still fail. The pre-existing exception for
the current migration operator's definer-owner membership is unchanged.

A new recursive check rejects API-role paths to `postgres`, backend or definer
owner through **INHERIT, SET or ADMIN**, including ADMIN-only chains that could
self-grant later. `authenticator` is included alongside anon/authenticated/
service_role; API SUPERUSER/CREATEROLE is rejected. The existing final effective
table/column/sequence/function/schema privilege checks now also cover an existing
`authenticator` role. No API access is granted.

No account/credential/session function, profile or booking constraint, index,
RLS policy, runtime grant, protected-cookie implementation, UI or activation gate
is changed. This is a migration prerequisite correction, not a second identity
boundary.

## Why ADMIN is acceptable only for this administrator

ADMIN-only does **not** mean powerless. `postgres` can grant itself SET/INHERIT,
administer the backend password and grant memberships to other roles. Local tests
explicitly demonstrate that self-grant and the revised migration refusing the
resulting capability-bearing membership.

The approved trust model already trusts the database/schema-owning `postgres`
administrator: it can alter application tables, policies and functions. This
change does not sandbox it, and must not be sold as doing so. It does not extend
that authority to the backend, Customer, staff application roles or API roles.
The guard checks a migration-time snapshot; a malicious administrator changing
grants afterward remains outside the runtime security boundary.

## Recommended hosted role-only proposal — separate approval required

Use **only** `prerequisites/customer_auth_roles_sql_editor.sql`, as the NEW V2
SQL Editor's non-superuser `postgres`, after owner approval of this revision and
this role-only action. It:

1. Checks the operator, PostgreSQL 17+, genuine bootstrap identity, empty target
   role names, no automatic SET/INHERIT self-grant setting, and no API administrative
   path to postgres.
2. Creates restricted backend LOGIN and owner NOLOGIN roles with **PASSWORD NULL**.
3. Requires exactly the two expected bootstrap ADMIN-only rows and role attributes;
   otherwise rolls back. It makes no existing-role repair or managed-grant change.
4. Returns role attributes and membership grantors/options, then commits only role
   provisioning. It runs no migration, object grant or authentication workflow.

NULL disables password authentication, not every possible HBA method. No password
is requested or embedded. Do not grant either role to API roles or attempt to
remove the bootstrap ADMIN-only rows. Do not change managed default privileges.

Later, under a distinct migration-window approval, owner-capability setup is:

```sql
GRANT glam_v2_authz_owner TO postgres
    WITH ADMIN FALSE, INHERIT TRUE, SET TRUE;
-- Only the separately approved revised migration belongs here.
REVOKE glam_v2_authz_owner FROM postgres GRANTED BY postgres;
```

The temporary owner row is removable; the bootstrap ADMIN-only rows remain.
Do **not** self-grant backend SET/INHERIT. Secret establishment, migration execution
and application activation are separate actions, not included in this proposal.

## Compared with the provider-only alternative

`prerequisites/customer_auth_roles_provider_only.sql` is retained unchanged.
It requires the genuine provider superuser and avoids the backend creator ADMIN
row; ordinary postgres then has no backend-management ADMIN authority. It can
therefore require provider assistance again for backend credential administration.
The SQL Editor route matches documented PostgreSQL/Supabase behavior and existing
administrator trust, without that provider dependency. No provider intervention
is inherently required by the revised contract. Unexpected grantor IDs, managed
hooks, grant options or operator privileges must still cause a stop—not a broader
exception or elevated runtime role.

## Results and limitations

Fresh local PostgreSQL **18.6**, SCRAM and loopback only:

- **29** role-compatibility checks passed: real non-superuser CREATEROLE creation,
  altered flags, extra/wrong members, synthetic wrong-grantor input against the
  exact migration predicate, NULL fail-closed behavior, API ADMIN/SET/INHERIT and
  transitive escalation, administrator self-grant, actual revised DDL and cleanup.
- **46** actual MVC checks passed on that SQL Editor-created-role schema.
- Separately, the existing **46 MVC + 120 migration/permission checks passed**
  against revised 0004 on another fresh cluster.
- **17** provider-only/historical creator-behavior checks passed for comparison.
- **248** isolated HTTP and **56** signup-retry checks passed.

The wrong-grantor ADMIN-only case uses a synthetic catalog CTE against the exact
membership expression because PostgreSQL prevents a creator from granting ADMIN
back to its own grantor. Physical extra self-granted membership is also tested.
No system catalog was modified.

PostgreSQL 17 binaries were unavailable; Docker's daemon was unavailable and was
not started. **No actual PostgreSQL 17.11 or hosted Supabase execution occurred.**
The owner-reported hosted foundation preflight is not silently reused as approval
for this changed hash or new role proposal. Actual hosted default ACL rows,
provider hooks and operator capabilities still need their own evidence.

All new clusters are retained/stopped, private password/bootstrap files removed.
Payment 0003, runtime gates, the original Demo, V1 and cloud resources were not
touched. The provider-only proposal remains a fallback, not a recommendation to
escalate SQL Editor postgres.

**One next owner action:** approve running only the guarded SQL Editor role
proposal on NEW V2, with returned attribute/membership evidence; do not approve
migration execution, passwords, registration/login or activation in that action.
