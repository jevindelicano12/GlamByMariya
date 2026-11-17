# One approved GSS-disabled diagnostic — timeout persists

Date: November 17, 2026. V2 project: `hqllkbjhufhvyooacajy`.

## Authorization and isolation

Exactly **one** hosted diagnostic was executed under the new owner approval:

```powershell
.\tools\Run-CustomerAuthDatabaseDiagnostic.ps1 -OwnerApproved -DisableGss
```

The existing tool did not support a per-run GSS override. The smallest
diagnostic-only change added `-DisableGss` / `--disable-gss`. It clones the
validated connection builder in memory and sets `GssEncryptionMode.Disable`.
Before network execution it removes only that key from comparison clones and
requires all remaining settings to be equivalent. The original builder/file is
not modified; no change was made to MVC or the shared connection normalizer.

Offline verification passed **65** C# checks and **8** supervisor-filter checks.
Tests cover unchanged input, restricted target/account, password, database, TLS,
CA configuration, timeout/pool values, unchanged SQL hash and output redaction.
The local-only override check also passed before the hosted run.

Effective settings during the sole hosted run:

- Correct V2 project and restricted project-qualified `glam_v2_backend` account.
- `SSL Mode=VerifyFull`; existing **system trust**; SSL negotiation `Postgres`.
- **Only GSS encryption negotiation changed: Prefer → Disable.**
- Connection timeout **15 seconds**; command timeout **30 seconds**.
- Pool minimum **0**, maximum **2**, one fresh data source.
- Overall cancellation **75 seconds**, forced-stop ceiling **90 seconds**.
- No validation callbacks replaced; no credential/trust/configuration changes.

## Observed results

| Stage | Result |
|---|---|
| Project/account configuration and private ACL checks | Passed; live database identity was not established. |
| Pool dispatch | Passed, **11 ms**. |
| DNS | Passed, **102 ms**. |
| TCP | Passed, **176 ms**. |
| TLS negotiation/certificate validation | **No TLS event observed**. Neither successful validation nor certificate rejection was established. |
| Physical opening/authentication/bootstrap | Physical opening unfinished; authentication/bootstrap completion not established. No PostgreSQL SQLSTATE reported. |
| Cold pool acquisition | **Failed after 15,029 ms**. |
| Warm pool acquisition | Not attempted. |
| Restricted identity SELECT | Not attempted. |
| Unchanged readiness SELECT | Not attempted. |
| Transaction / ROLLBACK | Not reached; no transaction began. |
| Exception | `timeout`; `NpgsqlException` wrapping `TimeoutException`; code `None`. |
| Exit/deadlines | Exit code **1**, before the overall cancellation or forced-stop deadlines. |
| Shutdown | Supervisor stopped; local verification found **zero diagnostic children** and no port 7144 listener. |

The first cold borrow failed. No warm borrow, retry, alternate endpoint,
additional hosted probe, Customer app launch or Customer operation followed it.

## Sanitized stage evidence

```text
DBCONFIG|project_pins=Passed|tls=VerifyFull|timeout_s=15|command_timeout_s=30|pool_min=0|pool_max=2|gss=Disable|ssl_negotiation=Postgres|root=SystemTrust
DBDIAG|stage=ColdPoolAcquire|status=Started|elapsed_ms=79|duration_ms=2|category=None|type=None|cause=None|code=None
DBDIAG|stage=PoolDispatch|status=Started|elapsed_ms=81|duration_ms=0|category=None|type=None|cause=None|code=None
DBDIAG|stage=PoolDispatch|status=Passed|elapsed_ms=92|duration_ms=11|category=None|type=None|cause=None|code=None
DBDIAG|stage=PhysicalOpen|status=Started|elapsed_ms=92|duration_ms=0|category=None|type=None|cause=None|code=None
DBDIAG|stage=Dns|status=Started|elapsed_ms=109|duration_ms=0|category=None|type=None|cause=None|code=None
DBDIAG|stage=Dns|status=Passed|elapsed_ms=211|duration_ms=102|category=None|type=None|cause=None|code=None
DBDIAG|stage=Tcp|status=Started|elapsed_ms=216|duration_ms=0|category=None|type=None|cause=None|code=None
DBDIAG|stage=Tcp|status=Passed|elapsed_ms=392|duration_ms=176|category=None|type=None|cause=None|code=None
DBDIAG|stage=ColdPoolAcquire|status=Failed|elapsed_ms=15107|duration_ms=15029|category=timeout|type=NpgsqlException|cause=TimeoutException|code=None
DBDIAG|stage=PhysicalOpen|status=Unfinished|elapsed_ms=15113|duration_ms=15021|category=None|type=None|cause=None|code=None
Diagnostic child stopped; exit code=1; hard deadline reached=False. No automatic retry.
```

## Interpretation / next safe action

**Disabling GSS did not resolve the earlier opening timeout:** the earlier cold
open failed at 15,035 ms; this run failed at 15,029 ms with the same exception
category and no observed TLS event. This comparison does not support applying a
GSS change to MVC as a fix.

The remaining evidence points to physical/protocol negotiation after TCP and
before observed TLS, not a readiness SQL or local pool queue failure. The exact
cause is still unknown; absent TLS events alone cannot prove the precise wire
stage or exclude an instrumentation gap. No evidence justifies changing
passwords, grants, certificates, timeouts or readiness SQL.

The smallest evidence-supported next investigation, **only under new approval**,
is a credential-free PostgreSQL SSLRequest response check at the same pinned
endpoint, to distinguish an encryption-request response stall from entry into
TLS. That probe was **not implemented or executed here**. Do not run another
broad diagnostic or start registration automatically.

## Changes and safety

Files changed:

1. `tools/Run-CustomerAuthDatabaseDiagnostic.ps1`
2. `tools/CustomerAuthDatabaseDiagnostic/Program.cs`
3. `tools/CustomerAuthDatabaseDiagnostic/OfflineChecks.cs`
4. This sanitized evidence document.

Post-run comparison confirmed the owner configuration's **bytes and ACL unchanged**.
Private passwords, certificates and CA configuration were not modified. No MVC
authentication/shared connection configuration change, hosted data/schema write,
migration, grant, role change, Supabase Auth request, Customer registration/login,
OTP/Brevo email, V1 access, GitHub operation or Render deployment occurred.
