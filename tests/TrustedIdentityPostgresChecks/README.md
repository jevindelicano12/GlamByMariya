# V2 trusted identity PostgreSQL checks

These checks target only a fresh, local PostgreSQL 18 cluster at
`127.0.0.1:55433`, with its data directory at
`temp/v2-proof-state-20261116-fresh/data`. They use synthetic email addresses, provider subject
UUIDs and account IDs. They do not call Supabase, connect to a hosted database,
or enable the application gates.

The live runner refuses a different server address, port, database directory,
database name, existing target database, or pre-existing test roles. It requires
the generated test-only password in `GLAM_V2_IDENTITY_TEST_PASSWORD`. It creates
one test database and isolated local test roles, then applies V2 migrations
0001/0002, the inactive identity candidate, a synthetic payment/proof fixture
and the test-only correction and staging-intent commands. It never applies migration 0003. It retains the synthetic
database after a successful run; stop the local test server when finished.

To run offline claim and source-contract checks:

```powershell
dotnet run --project tests/TrustedIdentityPostgresChecks/TrustedIdentityPostgresChecks.csproj -- --offline
```

The fixed target above was used on November 16 and retained, with its password
removed. Do not reset, overwrite or reuse it. A future run requires a separately
named fresh cluster and a reviewed update to the runner's fixed directory, a
free loopback-only port, restricted filesystem ACLs, a random 32-byte hexadecimal
test-only password, SCRAM authentication and a fresh database/roles. The
runner verifies server directory, port, version and emptiness before creating
objects. Stop only the newly created cluster after the test.

This fixture is a security test harness, not an approved deployment migration.
The isolated-only HTTP host uses a per-run fake-provider credential, the
application's prepared request-session accessor and ASP.NET Core durable-cookie
handler. It tests missing, tampered, expired, duplicate-role and revoked tickets
against a synthetic database operation. The host and its endpoints are not
registered in the application and do not verify Supabase.

It verifies the narrow backend role has no direct table rights, synthetic
claims are compared with PostgreSQL records, and profile writes and proof
corrections serialize with session revocation in both orders. It also checks
ownership, revisions, rollback, simultaneous corrections, append-only history,
verified evidence, slot holds, and a test-only request-bound HTTP adapter.
The proof assets and their validation flag are synthetic; they do not establish
real private-storage ownership, file validation or a production schema.
The separate test-only staging candidate can create a durable pending intent
after checking the identity generation, reservation owner and expected revision.
Its key, normalized-content hash and intended association are recorded, but no
object is uploaded or bound to a correction by that operation. The earlier
standalone fake-object staging method still does not check the database generation:
the static fake cookie snapshot demonstrates its limitation. Neither staging
method is registered in the application or connected to correction.
There is no verified promotion, atomic consumption, authorized proof read, or
reconciliation worker; pending intents never become correction-eligible.
The SQL fixture accepts an explicit trusted-backend quarantine request from
pending to reconciliation_required under identity, reservation, payment and
staging locks. This is retry-safe but not wired to storage outcomes; all other
state transitions are blocked by a trigger. The backend must not mistake an
explicit quarantine request for independent storage verification.
The existing storage interface cannot confirm deletion when cloud cleanup fails.
It does not test Supabase's remote session verification, production cookie
issuance, every account lifecycle writer, real reservation workflows, or actual
payment storage and operations. A fabricated in-process principal with matching claims is accepted
by the isolated-only adapter; neither the parser nor SQL authenticates those
claims. The public adapter is hard-gated and cannot be activated by configuration.
The V2 runtime and payment gates must remain closed until a real host-to-database
trust boundary and those additional paths pass equivalent isolated tests.
