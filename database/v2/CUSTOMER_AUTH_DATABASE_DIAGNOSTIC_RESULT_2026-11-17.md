# Approved database diagnostic — one run, stopped on timeout

Date: November 17, 2026. Project: `hqllkbjhufhvyooacajy`.

The owner approved the previously prepared standalone diagnostic, not the Customer
application or any Auth/email operation. The supervisor and linked runner,
configuration builder, readiness SQL and redaction code were reviewed before use.
The diagnostic rebuilt successfully with zero warnings/errors; all 57 offline
checks passed, including the unchanged readiness SQL hash.

Exactly **one hosted diagnostic run** was invoked:

```powershell
.\tools\Run-CustomerAuthDatabaseDiagnostic.ps1 -OwnerApproved
```

No second run, alternative endpoint, extra hosted probe or Customer operation
followed the failure. Diagnostic implementation and owner configuration were not
modified during this execution.

## Observed result

| Stage | Result |
|---|---|
| Configuration | Project/account pins and private ACL guards passed. Configured account was the restricted project-qualified `glam_v2_backend` Session Pooler account, database `postgres`, port 5432. Live server identity was not verified. |
| Effective settings | `VerifyFull`, system trust, 15-second connection timeout, 30-second command timeout, pool minimum 0 / maximum 2, GSS Prefer, SSL negotiation Postgres. No settings changed. |
| Pool dispatch | Passed in **11 ms**; physical opening started. No evidence of waiting for an exhausted local pool. |
| DNS | Passed in **120 ms**. No hostname/address recorded. |
| TCP | Passed in **96 ms**. |
| TLS / certificate validation | **No TLS event observed**. Successful TLS negotiation, hostname verification and chain validation were not established. This is not a certificate-rejection result. |
| Physical opening / PostgreSQL authentication | Physical opening remained unfinished. Authentication/bootstrap completion was not established; no PostgreSQL SQLSTATE was reported. |
| Cold pool acquisition | **Failed after 15,035 ms**. |
| Warm pool acquisition | Not attempted; only one cold borrow attempt occurred. |
| Identity SELECT | Not attempted. |
| Readiness SELECT | Not attempted. |
| Read-only transaction / ROLLBACK | Not reached; no transaction was begun. |
| Exception | Category `timeout`; outer `NpgsqlException`, cause `TimeoutException`, code `None`. |
| Shutdown | Child exited with code **1**; 90-second hard deadline not reached. Local verification found **zero** approved diagnostic child processes and no TCP listener on 7144. |

The prepared 75-second overall cancellation and 90-second process ceiling remained
in place; the earlier 15-second connection timeout ended this run first.

## Sanitized evidence

Elapsed milliseconds are relative to the diagnostic's own monotonic clock, not
individual protocol durations unless explicitly labeled `duration_ms`.

```text
DBCONFIG|project_pins=Passed|tls=VerifyFull|timeout_s=15|command_timeout_s=30|pool_min=0|pool_max=2|gss=Prefer|ssl_negotiation=Postgres|root=SystemTrust
DBDIAG|stage=ColdPoolAcquire|status=Started|elapsed_ms=77|duration_ms=1|category=None|type=None|cause=None|code=None
DBDIAG|stage=PoolDispatch|status=Started|elapsed_ms=79|duration_ms=0|category=None|type=None|cause=None|code=None
DBDIAG|stage=PoolDispatch|status=Passed|elapsed_ms=90|duration_ms=11|category=None|type=None|cause=None|code=None
DBDIAG|stage=PhysicalOpen|status=Started|elapsed_ms=90|duration_ms=0|category=None|type=None|cause=None|code=None
DBDIAG|stage=Dns|status=Started|elapsed_ms=106|duration_ms=0|category=None|type=None|cause=None|code=None
DBDIAG|stage=Dns|status=Passed|elapsed_ms=226|duration_ms=120|category=None|type=None|cause=None|code=None
DBDIAG|stage=Tcp|status=Started|elapsed_ms=237|duration_ms=0|category=None|type=None|cause=None|code=None
DBDIAG|stage=Tcp|status=Passed|elapsed_ms=333|duration_ms=96|category=None|type=None|cause=None|code=None
DBDIAG|stage=ColdPoolAcquire|status=Failed|elapsed_ms=15112|duration_ms=15035|category=timeout|type=NpgsqlException|cause=TimeoutException|code=None
DBDIAG|stage=PhysicalOpen|status=Unfinished|elapsed_ms=15117|duration_ms=15027|category=None|type=None|cause=None|code=None
Diagnostic child stopped; exit code=1; hard deadline reached=False. No automatic retry.
```

## Evidence versus inference / smallest next step

**Established:** the timeout occurred during cold physical connection opening,
after successful TCP connection, not during the identity/readiness SELECT or warm
pool borrowing. No TLS handshake event was observed.

**Most likely class of cause:** a PostgreSQL encryption/startup negotiation stall
after TCP and before observed TLS. Optional GSS negotiation under the unchanged
`Prefer` setting is a candidate, not a proven cause. The trace contains no wire
payload and cannot identify the exact request/response or rule out missing TLS
instrumentation or an intermediary/server stall. It does not establish a wrong
password, missing grant, failed readiness predicate or invalid certificate.

No proven configuration correction is justified yet. The smallest candidate
experiment for **separate owner authorization** is a diagnostic-only
`GssEncryptionMode=Disable` comparison, retaining `VerifyFull`, the existing trust
source, restricted credentials and 15/30-second timeouts. This would not disable
TLS, but it is **not applied or executed here** and must not be called a confirmed
fix. Do not change passwords, grants, pool sizes or readiness SQL based on this
trace.

No hosted data/schema mutation, migration, role/permission change, Supabase Auth
request, Customer registration/login or email operation was invoked. No
configuration, credentials, certificate/trust material, UI, V1, GitHub or Render
change was made. The sole new workspace file is this sanitized evidence document.
