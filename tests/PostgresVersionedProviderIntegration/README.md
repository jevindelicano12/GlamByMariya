# Isolated PostgreSQL provider verification

The owner reports that the isolated target has retained fixtures from an incomplete run. Preserve them; do not rerun `--exercise`. This is not a production migration or an application-host test. The target is **only** `127.0.0.1:5432/glam_provider_snapshot_test`; neither authorization test database is accepted. The test requires explicit approval for role/database creation, the exact schema file (which writes a sentinel), and the runner's synthetic fixture writes and execution. Preserve all existing databases and Demo data.

## Prerequisites and safety

- Independently verify server identity and confirm the target database and `glam_provider_denied` role names are unused; stop on any ambiguity. The role namespace is server-wide. Never substitute a production role, database, password or server.
- Existing `glam_authz_test` must be a test-only login, own the new database, have no superuser, CREATEROLE, replication, BYPASSRLS or role memberships. Database creation by a separate administrator avoids granting it CREATEDB.
- The new `glam_provider_denied` must be a test-only login without superuser, CREATEDB, CREATEROLE, replication, BYPASSRLS or role memberships. Both roles need test-only passwords. The schema grants it USAGE and SELECT on the sentinel and three account tables, **not** the generation table. The runner verifies real SQLSTATE 42501 before testing fail-closed behavior.
- The schema requires an empty target with no unexpected schemas, objects, extensions, default ACLs or publications, verifies exact target/role/owner/loopback, then creates the minimal tables and sentinel in one transaction. It copies the prior isolated suite's table shapes and constraints, not a production migration.
- The runner validates fixed target and role on every physical connection, exact five-table column shapes, table ownership, constraint counts, sentinel, initially empty fixture tables and no other sessions, and holds a cooperative advisory lease. The session check is point-in-time and the lease cannot prevent unrelated clients. Arrange an operator-controlled exclusive window.
- Fixture rows are committed once, retained after success or failure, and never deleted. A rerun rejects nonempty fixtures. If anything fails, stop and retain evidence; do not reset or retry on either existing authorization database.
- Preflight inventories and constraint counts are not exhaustive schema/privilege verification. The guards cannot independently prove server identity; inspect the server and exact commands first. Passwords are read interactively without echo, but remain in process memory.

## Proposed operator commands — only after separate approvals

Run from the repository root in an interactive PowerShell terminal. PostgreSQL 18 paths and administrator privileges must first be verified locally. These commands prompt for passwords; never put them on the command line.

Create the separate restricted denial-test role (server-wide action):

```powershell
& 'C:\Program Files\PostgreSQL\18\bin\createuser.exe' -h 127.0.0.1 -p 5432 -U postgres -W --login --no-superuser --no-createdb --no-createrole --no-replication -P glam_provider_denied
```

Create the fixed database with the existing restricted test role as owner:

```powershell
& 'C:\Program Files\PostgreSQL\18\bin\createdb.exe' -h 127.0.0.1 -p 5432 -U postgres -W -T template0 -O glam_authz_test glam_provider_snapshot_test
```

Apply the reviewed test-only schema and sentinel:

```powershell
& 'C:\Program Files\PostgreSQL\18\bin\psql.exe' -X -h 127.0.0.1 -p 5432 -U glam_authz_test -W -d glam_provider_snapshot_test -v ON_ERROR_STOP=1 -f 'tests/PostgresVersionedProviderIntegration/schema.test-only.sql'
```

After separate approval for five committed synthetic customer/identity fixtures and four generation rows:

```powershell
$env:GLAM_PROVIDER_SNAPSHOT_WRITES = 'YES'
dotnet run --project tests/PostgresVersionedProviderIntegration/PostgresVersionedProviderIntegration.csproj -- --exercise
```

The runner prompts separately for the owner and denied-role passwords. The opt-in is not a password. To inspect offline only, run the same project with `-- --offline`; this does not open a database.

## Retained-fixture read-only diagnostic

After separate approval for a connection and read-only diagnostics, verify the server and arrange an exclusive window, then run from the repository root in an interactive terminal:

```powershell
$env:GLAM_PROVIDER_SNAPSHOT_READS = 'YES'
dotnet run --project tests/PostgresVersionedProviderIntegration/PostgresVersionedProviderIntegration.csproj -- --diagnose-readonly
```

The diagnostic requires its own opt-in and both hidden test-role passwords. It applies the same fixed-target connection initializers and table-shape/owner/sentinel/session preflight, but does not require empty fixture tables. The preflight temporarily takes a cooperative advisory lock; it does not update stored records. It starts a read-only transaction, deliberately triggers a division-by-zero error using a `SELECT`, rolls back, and tests the restricted-role generation query for expected SQLSTATE `42501`. It performs no fixture writes, grants, schema changes or cleanup. It does not reproduce the original `25006` write attempt and does not prove the cause of the earlier run.

Unexpected errors report only a fixed operation-stage label, whether the permission query had not started, was in progress, or had completed, and a validated PostgreSQL SQLSTATE when available. No server error text, password, SQL parameter or account identifier is printed. The diagnostic does not test provider fail-closed behavior or complete the original suite. Expected `42501` is a pass only for the direct permission query, not for connection initialization.

The former `--diagnose-25006` mode was invalid and has been removed: PostgreSQL permits its `ROW EXCLUSIVE` table lock in a read-only transaction, so expecting `25006` was incorrect. It does not explain the original exercise failure. No replacement write attempt against retained fixtures is prepared.

## Retained-fixture continuation

The owner-reported nine exercise passes cover projection, account/session states and the standalone read-only write rejection. The successful read-only diagnostic independently covers the direct PostgreSQL `42501` denial and rollback after a different error. The original exercise's **provider fail-closed on denial** and **disposed data-source fail-closed** assertions remain unverified.

After separate approval for this exact read-only continuation and an exclusive test window, run from the repository root:

```powershell
$env:GLAM_PROVIDER_SNAPSHOT_CONTINUE = 'YES'
dotnet run --project tests/PostgresVersionedProviderIntegration/PostgresVersionedProviderIntegration.csproj -- --continue-readonly
```

Both test-role passwords are prompted without echo. This separate opt-in does not permit fixture writing. Fixed target and role initializers, sentinel, exact table shape/ownership, constraint counts and cooperative session preflight still apply. A read-only snapshot checks all five expected customer and identity UUIDs, links, synthetic metadata, fixture counts and generation rows, the absence of extra accounts, and effective privileges. The originally locked fixture must still be locked; the original test set its lock for only one day, so an expired lock causes a safe stop. Never extend it or reset fixtures to make the test pass.

The continuation first confirms the owner can validate the active synthetic ticket. It then observes the real PostgreSQL provider transport fail closed with the restricted role and independently executes the **exact SQL captured from that provider call** as the restricted role, requiring PostgreSQL `42501`. The provider deliberately sanitizes underlying database exceptions, so this demonstrates the same query's denial on a separate guarded connection; it cannot directly prove the sanitized provider call's precise SQLSTATE. Finally it tests fail-closed behavior using a disposed data source. No real Supabase provider, application login or Supabase session is used.

Only `SELECT`, `SET TRANSACTION READ ONLY`, transaction control and the existing advisory preflight are used. No fixtures, permissions or schema are changed. Diagnostic output contains fixed stages and validated SQLSTATEs only. Preflight is not an exhaustive integrity/privilege inventory; the session check is point-in-time and the advisory lock cannot prevent unrelated clients. This continuation has offline coverage only until separately approved and run.

## Scope and limitations

The query needs `customers` and `admin_users` account/status/lock/hash/login fields, `account_identities` ownership/login/auth/subject/sync/epoch fields, and `account_security_generations(account_id,generation)`. The sentinel and indexes/constraints are isolation and fixture safeguards, not provider inputs. Five customer fixtures exercise active, disabled, locked, absent-generation and mismatched-login identities; the active fixture also checks a stale ticket. There are no real credentials or Supabase calls.

The live runner tests the actual provider transport and projection, a real permission-denied query and fail-closed provider outcome, and a disposed-data-source failure. It separately verifies that PostgreSQL rejects a write in an explicitly read-only repeatable-read transaction. **That separate probe does not instrument or prove the provider transport's own transaction mode**; its `SET TRANSACTION READ ONLY` remains a source-level property requiring separate instrumentation/review. Disposing a data source simulates local unavailability, not a network outage or server crash. No concurrency, real Supabase, application login, full production schema, RLS or deployment behavior is established.
