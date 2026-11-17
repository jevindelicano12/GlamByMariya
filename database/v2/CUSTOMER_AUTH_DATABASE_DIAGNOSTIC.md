# Npgsql readiness isolation — prepared, NOT executed against hosted services

Prepared November 17, 2026; deadline November 22, 2026.

For the subsequent comprehensive evidence review and **one consolidated next
diagnostic plan**, see `NPGSQL_TIMEOUT_ROOT_CAUSE_REVIEW_2026-11-17.md`.
The earlier modes/results below remain historical; do not repeat them as a
substitute for that separately approved native/protocol/managed comparison.
No new hosted run was performed by the review.

Subsequent pre-execution inspection blocked the consolidated run because the
old duration handoff/per-child waits did not guarantee whole-run supervision.
The local-only correction, fixed deadlines and offline process-tree tests are
documented in `CUSTOMER_AUTH_DIAGNOSTIC_SUPERVISION.md`. No hosted run is authorized
or performed by that correction; the mandatory safety review still precedes one.

## Inspected path and established local facts

`Frontend/GlamByMariya.Web/Program.cs` registers one singleton
`NpgsqlDataSource.Create(connection)`, builds the host, then calls
`PersistentCustomerAuthentication.Ready(CancellationToken.None, throwOnFailure: true)`.
`Ready` awaits **pool/connection acquisition first**, then separately awaits
`ExecuteScalarAsync` for the readiness SELECT. There is no EF retry policy in this
path, synchronous `.Wait()`/`.Result`, held transaction, or concurrent startup
borrower. File-based DP warmup and HTTPS binding occur later. An earlier process
cannot retain this new process's local Npgsql pool; hosted pooler capacity is a
different matter and has not been established.

The SELECT is now shared verbatim in
`Backend/GlamByMariya.Infrastructure/Data/CustomerAuthReadinessSql.cs`; MVC still
uses the same query. Its normalized SHA-256 is pinned by the offline test:
`2AB16C17D467F5C04EBBAFF706D5DCB9F63D1C85164D01E5AF283460CD7AAD74`.
It inspects role attributes, membership, RLS, columns, privileges, sequence usage
and the existence/EXECUTE privilege of the revocation function. **It does not
invoke that function or perform DML.**

The standalone diagnostic links the actual `DatabaseConfiguration.cs`, shared
SELECT and whitelisted exception classifier; it does not reference/start the Web
host or implement another connection-string normalizer. Npgsql remains **10.0.0**.

Local-only configuration inspection passed for:

- Project pins `hqllkbjhufhvyooacajy`, isolated environment/profile, restricted
  project-qualified Session Pooler account, port 5432 and database `postgres`.
- No connection-string override; existing password present, never displayed.
- `VerifyFull`, **15-second connection / 30-second command** timeouts unchanged.
- One source, pooling enabled, minimum 0 / maximum 2, multiplexing off.
- Error details/parameter logging/password persistence disabled.
- Existing DB trust source: **system trust**, not the local HTTPS or DP PFX.
- Driver defaults: GSS encryption **Prefer**, SSL negotiation **Postgres**.
- External owner configuration remains private with owner/SYSTEM ACLs.

These fields match the recorded owner-verified target/account. The earlier private
psql invocation, its trust source, GSS behavior and actual password equivalence
were not independently inspected. A previous psql success does not prove that
Npgsql's transport negotiation follows the identical path.

## Hypothesis, not a finding

The previous roughly 15.5-second timeout is consistent with connection opening's
15-second budget rather than the 30-second command budget. With a fresh pool and
no concurrent borrower, PostgreSQL transport/TLS/login/bootstrap is more suspect
than local pool starvation. This is **not proof**: the previous output did not
separate the two awaits. No GSS, TLS, password, endpoint or timeout change is
justified yet.

## Offline/local-only modes already exercised

```powershell
dotnet restore tools/CustomerAuthDatabaseDiagnostic/CustomerAuthDatabaseDiagnostic.csproj --source . --ignore-failed-sources
dotnet run --project tools/CustomerAuthDatabaseDiagnostic/CustomerAuthDatabaseDiagnostic.csproj --no-restore -- --self-test
.\tools\Run-CustomerAuthDatabaseDiagnostic.ps1 -SelfTest
dotnet run --project tools/CustomerAuthDatabaseDiagnostic/CustomerAuthDatabaseDiagnostic.csproj --no-build --no-restore -- --check-local
```

Self-tests do not read the owner configuration, resolve DNS, open sockets or start
a host. `--check-local` reads only local configuration/ACL/file metadata and
constructs connection settings; it never creates a data source or connection.
Default/unknown arguments and the supervisor without approval refuse execution.

## Exact hosted diagnostic requiring separate approval

**Not executed in this preparation.** Approval must cover one standalone run on
the verified V2 target with the existing restricted password:

```powershell
# ONLY after separate approval; not a Customer-app launch.
.\tools\Run-CustomerAuthDatabaseDiagnostic.ps1 -OwnerApproved
```

The supervisor clears inherited environment/telemetry/provider overrides and
starts one configuration-reading worker inside a Windows Job Object. The worker
starts one managed diagnostic child in this original mode; the consolidated
native/protocol sequence is documented in the linked review. The managed child
receives no credentials in arguments or environment and reads the existing private
file directly. No config, passwords, certificates or output logs are written.
Only whitelisted output lines are shown; raw stderr and logging payloads are discarded.

Bounded sequence:

1. Recheck private paths/ACLs, exact project/account pins and effective settings.
2. Build **one fresh source**, retain the original 15/30-second timeouts, and make
   one cold borrow. Passive .NET event observation separates DNS, TCP and TLS
   spans; Npgsql's fixed event IDs separate pool dispatch and physical opening.
   No socket payload is captured and no certificate-validation callback is
   replaced. Require an observed successful TLS handshake and exactly one
   initial physical-open event before SQL; absent instrumentation fails closed.
3. Return the cold connection, make **one** sequential warm borrow, and require
   reuse of the same physical session (compare in memory, never print its PID).
   If the driver reconnects unexpectedly, stop; do not borrow again.
4. Execute `BEGIN READ ONLY`, one boolean SELECT verifying `current_user`,
   `session_user`, database and transaction read-only state, then the **exact
   shared readiness SELECT once**, followed by `ROLLBACK`. Npgsql also performs
   its normal read-only metadata loading and session-reset traffic.
5. Dispose connections/source and exit. Stop at the first failure; no retry loop,
   alternate endpoint, elevated role, auth API, HTTP listener, migration,
   Customer/account lookup, email or business-table write.

At most two sequential pool borrows, normally one physical session; an unexpected
warm reconnect is detected and fails the run. Normal driver attempts across the
endpoint's resolved addresses remain unchanged. A **75-second overall cancellation
budget** bounds this diagnostic without changing connection/command timeout values;
an independent supervisor terminates the entire contained worker/descendant tree
after **90 seconds** if necessary. The absolute budget starts before configuration;
the launcher also bounds supervisor lifetime. See the supervision correction note.

## Interpreting the evidence without overclaiming

- Records contain fixed stages/statuses, elapsed/duration milliseconds, whitelisted
  exception categories/types/codes; no host/address, username value, SQL, email,
  token, certificate or logging payload.
- DNS/TCP/TLS failure events are distinct. Missing events are **not PASS**; pending
  spans at failure are `Unfinished`, not an asserted failure of that substage.
  Overlapping network activities are correlated internally without exporting IDs.
- Cold acquisition includes physical opening; `PoolDispatch` measures time until
  the driver begins that opening, not an independently proven queue bottleneck.
  Warm acquisition/reuse is a separate measured step in a fresh local pool; it
  cannot measure another process's pool or prove hosted pooler capacity.
- Successful TLS is **not successful PostgreSQL authentication**. A completed
  cold open proves startup/authentication/driver initialization completed; the
  restricted-identity SELECT then proves the actual role/database.
- Npgsql has no public login-only completion event. A stall after TLS but before
  physical open completes is explicitly `PostTlsLoginOrBootstrap`: do not claim
  to have separated password authentication from driver bootstrap without further
  evidence. A PostgreSQL authentication SQLSTATE, if present, is classified.
- `timeout` is distinct from `canceled`; the original startup token was `None`.
  The diagnostic's own total budget may cause cancellation and is not evidence of
  a caller canceling the original startup.
- A successful `Complete` record is not sufficient by itself: the supervisor
  must also report child exit code 0 with no later failure/forced deadline.

Runtime event availability and hosted success remain unverified. Offline tests
check the installed runtime's event metadata and Npgsql event IDs, event
correlation, false-PASS prevention, unchanged SQL hash, guards and redaction.

## Preparation results / changed files

- **57** new C# offline checks passed.
- **7** PowerShell supervisor output-filter checks passed.
- **21** existing startup diagnostic/redaction regression checks passed.
- Both unapproved/default execution paths refused before configuration/network
  execution; local-only owner configuration checking passed.
- Web build passed with six existing infrastructure warnings; no host launched.
- No hosted diagnostic, DNS lookup, socket connection, Customer app, provider API,
  OTP/email, migration or hosted write was executed during this preparation.

Source/docs changed:

1. `Backend/GlamByMariya.Infrastructure/Data/CustomerAuthReadinessSql.cs` (new)
2. `Frontend/GlamByMariya.Web/Security/PersistentCustomerAuthentication.cs`
3. `Frontend/GlamByMariya.Web/Security/CustomerAuthStartupDiagnostics.cs`
4. `tools/CustomerAuthDatabaseDiagnostic/CustomerAuthDatabaseDiagnostic.csproj` (new)
5. `tools/CustomerAuthDatabaseDiagnostic/Program.cs` (new)
6. `tools/CustomerAuthDatabaseDiagnostic/Observers.cs` (new)
7. `tools/CustomerAuthDatabaseDiagnostic/OfflineChecks.cs` (new)
8. `tools/Run-CustomerAuthDatabaseDiagnostic.ps1` (new)
9. This document (new)
10. `database/v2/CONTROLLED_CUSTOMER_AUTH_TEST.md` (diagnostic-preparation link)

Owner configuration, credentials, TLS options, timeout values, certificates,
trust stores, UI, V1, roles, schema, GitHub and Render were unchanged.
