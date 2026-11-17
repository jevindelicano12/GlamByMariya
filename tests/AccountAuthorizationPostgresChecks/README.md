# Isolated account-authorization PostgreSQL test — not yet approved

The original `glam_authz_sprint3` contains retained evidence from a partial test run; do not reset or rerun its exercise. A separate `glam_authz_sprint3_retry` is proposed but has not been created. Do not run the commands below without separate approval for creation, schema execution and synthetic writes. Do not use production credentials or databases.

Prerequisites: independently confirm the server's identity and loopback address and the exact dedicated database and test role before execution. The owner reports that `glam_authz_test` owns the database and has no elevated role flags or memberships. The updated guards check ownership, login, selected privilege flags and direct memberships; they are not proof of server identity or every possible object and privilege. The account must use a test-only password.

After separately approved preflight confirms the retry name is unused and the restricted test role may create and own it, the proposed commands from the repository root are:

```powershell
& 'C:\Program Files\PostgreSQL\18\bin\createdb.exe' -h 127.0.0.1 -p 5432 -U glam_authz_test -W -T template0 glam_authz_sprint3_retry
& 'C:\Program Files\PostgreSQL\18\bin\psql.exe' -X -h 127.0.0.1 -p 5432 -U glam_authz_test -W -d glam_authz_sprint3_retry -v ON_ERROR_STOP=1 -v test_database=glam_authz_sprint3_retry -f 'tests/AccountAuthorizationPostgresChecks/schema.test-only.sql'
```

The operator supplies the test-only password interactively. The SQL accepts only the two named isolated databases and requires the actual database to match `test_database`; if omitted it defaults to the original. It rejects mismatched ownership, role flags or membership, non-loopback connections, and several classes of unexpected schemas and objects; this inventory is not exhaustive. It revokes PUBLIC access inside the selected database. Review its exact permissions and target guard before approval. It does not alter the separate, intentionally aborting production schema candidate.

Only after separately approving the synthetic independent commits, set `GLAM_AUTHZ_SPRINT3_WRITES=YES` in the operator's process environment, then run the command below from an interactive terminal. The runner prompts for the test-only password with input hidden; it does not accept a password environment variable or redirected input.

```powershell
dotnet run --project tests/AccountAuthorizationPostgresChecks/AccountAuthorizationPostgresChecks.csproj -- --exercise --retry
```

The runner accepts only its original fixed target or `--retry` for the fixed retry target; it has no arbitrary database override. It fixes host, port and role, disables pooling, checks the selected database, ownership, role privileges, membership and sentinel on every physical connection, and retains its synthetic records. Before fixture writes it checks all five table names, owners, column types/nullability/identity, constraint counts and empty fixture tables. It also checks for other sessions and holds a PostgreSQL advisory lease for cooperating runners. That session check is a point-in-time observation; unrelated clients can ignore the advisory lease and connect later. Arrange an operator-controlled exclusive test window. Constraint counts do not verify constraint expressions or prove schema compatibility; those and the catalog queries require real PostgreSQL review. The password remains in managed process memory while Npgsql needs it; a compromised terminal or process is outside this protection. The exercise uses a fake authenticated principal, not Supabase or cookie middleware. Fault wrappers model a database exception and lost commit acknowledgement; they do not reproduce a real network failure. The runner cannot prove a server restart occurred.

Only after a separately approved, operator-confirmed server restart, retain the opt-in and run interactively:

```powershell
dotnet run --project tests/AccountAuthorizationPostgresChecks/AccountAuthorizationPostgresChecks.csproj -- --after-restart --retry
```

The follow-up prompts again and reads the exact retained generation. It does not itself restart anything. Unset the opt-in afterward. No cleanup is authorized or automated; propose any eventual database or role removal separately, with explicit target identification and approval.

These are unexecuted candidate commands. Before live use, review the adapter's trust boundary, serialized lock ordering across all writers, schema constraints, privileges and restore/recreation policy. Neither these tests nor the candidate schema authorize activation or Render deployment.
