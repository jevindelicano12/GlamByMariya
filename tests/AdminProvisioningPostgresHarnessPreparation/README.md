# Phase 2.17F — PostgreSQL harness preparation (not executed)

**Preparation only. This directory is not an executable database test harness yet. No provider or connection code is present; these checks are static-only. Do not execute the fixture or SQL as part of this phase.**

## Findings and required order

Checked-in prerequisite order is:

1. `20261015183900_PublicIntegration`: establishes `public.account_identities`, local Customer/Admin relationships, app UUIDs, and the identity lookup key.
2. `20261017090000_RolesAndInvitations`: establishes `public.admin_invitations(id, email, invited_by, expires_at, accepted_at, ...)` and the invitation UUID key.
3. `20261019160000_AuthIdentityTracking`: establishes `supabase_user_id`, `auth_state` (`Unknown` default), and unique partial index `ux_account_identities_supabase_user`.
4. `20261027170000_AdminInvitationProvisioning`: establishes `admin_provisioning_state`, `admin_invitation_id`, the invitation FK, state/correlation checks, and `ux_account_identities_admin_invitation`.

This is checked-in migration ordering, not evidence of deployed migration history. `PublicStudioStore` expects the same column and state names, verifies `accepted_at`, normalized emails, `identity.admin_id -> admin_users.id`, `admin_users.app_id == identity.id`, `role='Admin'`, the inactive Pending state, and both unique indexes. The store additionally requires all Phase 2.17D constraints/indexes to be installed and validated before writing. No schema change is proposed in this phase.

`SyntheticFixture.sql` models only the relevant public relationships in a newly created schema and uses reserved `.invalid` emails, deterministic synthetic UUIDs, and non-credential strings. It is not a substitute for running the actual prerequisite migrations and is not to be used against an existing schema. In a separately approved test, first establish a new isolated database, bootstrap the fixture, then apply/check the prerequisite migration artifacts in order; seed pending data only after the provisioning schema exists.

## Safety gate design for a future runtime runner

The current Windows environment cannot prove that an arbitrary PostgreSQL endpoint is disposable merely from a connection string. Therefore this preparation intentionally includes **no Npgsql reference, connection factory, app-settings loader, environment-variable lookup, database command, or migration runner**. A source-only check cannot provide runtime identity assurance, so no attempt to connect is authorized here.

For a later separately approved runner, require all of the following before submission of any SQL:

- A new, explicitly provisioned disposable VM or equivalent isolated PostgreSQL instance, not the ordinary Windows PostgreSQL service and not the paused cluster.
- Private, non-routed networking with no port forwarding or access to Supabase/Render; use a dedicated disposable instance and database, not merely a new database in an existing cluster.
- Connection parameters supplied explicitly to that runner for that one invocation. Never use application configuration, user-secrets, `.env`, process environment fallback, or a production connection string. Do not put passwords in command-line arguments or logs; use a one-time interactive secret channel after approval.
- Exact allow-listed isolated endpoint plus dedicated database/user and an independently provisioned instance marker/nonce. Reject hostnames matching Supabase/Render, every non-approved endpoint, ports `5432` and `56432`, and all implicit/default ports. Require an additional server-side identity check before fixture or migration SQL.
- A fresh instance with no application data, no mounted application secrets, no shared volumes, no outbound route, and teardown performed only by its operator after collecting test results.

These checks are defense in depth, not proof that an untrusted operator-supplied endpoint is safe. If the independent isolation attestation and endpoint allow-list are unavailable, the runtime harness must refuse to connect. Do not try the paused cluster, local service, cloud database, or production application configuration as a fallback.

## Planned PostgreSQL runtime cases

| Runtime case (not run) | Synthetic assertion |
| --- | --- |
| Correct prerequisite migration order | Migration IDs and required objects present before applying Phase 2.17D; provisioning fails closed when prerequisites are absent. |
| Additive schema installation | New fields/constraints/index only; existing table rows and unrelated columns remain. |
| Historical account preservation | Synthetic Customer, inactive Admin, and active SuperAdmin retain IDs, roles, hashes, and active values. |
| Default provisioning state | Existing identities read `NotApplicable`; invitation correlation remains null. |
| Foreign-key enforcement | Unknown invitation UUID rejected; existing invitation reference accepted. |
| Unique invitation association | A second identity cannot claim the same invitation. |
| Unique provider UUID assignment | A provider UUID already linked to another identity is rejected. |
| Pending-to-confirmed activation | Matching consumed invitation links the UUID, sets `Confirmed`/`Supabase`, activates only the mapped Admin. |
| Incorrect invitation/account correlation | Wrong invitation or identity/admin app UUID association causes no state change. |
| Wrong Admin role | Customer or SuperAdmin row cannot be activated through this Admin operation. |
| Unconsumed invitation | `accepted_at IS NULL` is rejected without partial writes. |
| Mismatched email | Invitation, identity login, local Admin email, and supplied normalized email must agree. |
| Duplicate provider subjects | Two accounts competing for one UUID yield at most one confirmed association. |
| Exact-repeat idempotency | Same complete, already-confirmed association succeeds without changing data. |
| Conflicting repeated confirmation | Different invitation, provider UUID, email, role, or account state is rejected. |
| Transaction rollback | Force second conditional update/final assertion to fail; identity link and account activation both remain unchanged. |
| Concurrent confirmation attempts | Separate sessions race on same invitation and on same provider UUID; assert one valid winner and no partial loser. |

The runtime suite must invoke the actual `PublicStudioStore.ConfirmAdminProvisioning` method, not merely reimplement its SQL. It must verify schema/catalog state and snapshots before/after each test, use isolated synthetic rows only, and report server/database identity with secrets redacted. The two-session race requires separate connections with barriers; it must not use automatic retries.

## Static-only check

`Program.cs` reads repository source and fixture files and checks migration ordering, schema/store naming, and scenario coverage. It has no PostgreSQL driver and cannot test SQL parsing, execution, locking, rollback, constraints, or concurrency. Compiling/running this source check is not a PostgreSQL test.

## Windows isolation recommendation

Do not restart the stopped disposable cluster: its prior shared-memory/initialization errors make reuse an unverified risk. The ordinary Windows service and ports `5432` and `56432` are explicitly excluded. For runtime verification, request separate approval to use a newly created ephemeral Linux VM with a private, non-routed virtual network (or another independently audited disposable environment), a dedicated instance identity and non-default port, and no production/cloud credentials. Do not create or start that VM as part of Phase 2.17F.
