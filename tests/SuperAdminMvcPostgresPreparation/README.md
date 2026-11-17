# SuperAdmin MVC/PostgreSQL candidate — preparation only

**Not approved or executed.** This is a separately gated test host, not the normal application, a migration or a Render deployment. No existing authorization or provider test database may be reused.

## Target and review gates

- The only target is `127.0.0.1:5432/glam_admin_mvc_candidate_test`, owned by the restricted test-only `glam_authz_test` role. No database-name override is accepted.
- Obtain separate approval for the exact database creation, `schema.test-only.sql` (including its sentinel), and the runner's synthetic fixture writes and execution. Independently verify server identity, that the new name is unused, the role flags/memberships and ownership, and the full scripts before approval.
- The schema rejects a wrong database, role, owner, non-loopback connection, elevated role, memberships, nonempty public schema and several other unexpected objects. Preflight checks the sentinel, table ownership and column shapes, constraint counts, empty fixture tables and other sessions. Its inventory is not exhaustive and constraint counts do not prove expressions.
- The runner takes a cooperative advisory lease and checks sessions once. Unrelated clients can ignore the lease or connect later: arrange an operator-controlled exclusive window.
- Enter the test-only role password interactively; it is never a command-line argument. The password and synthetic HTTP test nonce remain in process memory. The short-lived test host binds loopback only and requires the unpredictable nonce on each request; other processes running under the operator's account remain outside that protection.
- Three synthetic admin identities are inserted and retained. A rerun rejects nonempty fixtures. A failure after fixture commit can leave partial test outcomes; preserve evidence and do not reset, rerun, or redirect this harness at another database.
- The host uses the actual MVC Admin controller, candidate cookie validation, PostgreSQL roster reader, authorization transaction and Razor view. It issues **synthetic test-only cookies**, not real Supabase proof or an application login. A fake in-memory store supplies only layout/preferences and the host's persistent-store gate; the controller receives the actual public adapter and authorization/roster operations use guarded PostgreSQL. No normal application startup runs.
- Lost acknowledgement is simulated by throwing after a successful real transaction commit. It does not reproduce an actual network failure or demonstrate how unknown commit outcomes behave under a server crash.

## Proposed commands — do not run without approvals

Confirm PostgreSQL 18 paths and the server administrator first. The operator supplies passwords interactively; never put them in the command line.

Create the new database with the restricted owner:

```powershell
& 'C:\Program Files\PostgreSQL\18\bin\createdb.exe' -h 127.0.0.1 -p 5432 -U postgres -W -T template0 -O glam_authz_test glam_admin_mvc_candidate_test
```

Apply the reviewed test-only schema:

```powershell
& 'C:\Program Files\PostgreSQL\18\bin\psql.exe' -X -h 127.0.0.1 -p 5432 -U glam_authz_test -W -d glam_admin_mvc_candidate_test -v ON_ERROR_STOP=1 -f 'tests/SuperAdminMvcPostgresPreparation/schema.test-only.sql'
```

After separate approval for synthetic writes and execution:

```powershell
$env:GLAM_ADMIN_MVC_TEST_WRITES = 'YES'
dotnet run --project tests/SuperAdminMvcPostgresPreparation/SuperAdminMvcPostgresPreparation.csproj -- --exercise
```

Offline checks use `-- --offline` and do not open a database. The prepared live checks cover SuperAdmin roster, role denial, stale generation and refresh, atomic disable and cookie rejection, and simulated lost acknowledgement followed by authoritative refresh. Tests do not verify real Supabase, the full production schema, cross-writer enforcement, production authentication or Render.
