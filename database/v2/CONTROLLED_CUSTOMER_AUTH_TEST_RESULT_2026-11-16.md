# Controlled real Customer authentication test — stopped at startup

Date: November 16, 2026. Owner authorized one V2-only lifecycle test.
Target: `hqllkbjhufhvyooacajy`; origin: `https://127.0.0.1:7144`.

## Observed results

| Requirement | Evidence / result |
|---|---|
| Local project and restricted-account configuration | PASS: project pins, Supabase URL, Session Pooler target, configured `glam_v2_backend` login and isolated environment/profile matched. This is not proof of a successful live database connection. |
| Allowlist | Populated; valid format; exactly one matching configuration field. Value withheld. |
| Isolation configuration | PASS locally: forbidden credentials/endpoint/proxy overrides absent; Demo features disabled; loopback URL and expected flags matched. |
| Protected paths | PASS: external locations, owner/SYSTEM-only ACLs and no inspected reparse points. |
| Certificates | PASS locally: both encrypted private PFX files loaded with RSA keys and suitable validity. Configured DP material matched its external PFX. HTTPS PFX matched the previously reviewed public certificate and exact CurrentUser Root entry, with IP SAN and server-authentication purpose. No trust changes. |
| Build | PASS: `dotnet build Frontend/GlamByMariya.Web/GlamByMariya.Web.csproj --no-restore --nologo -v:q`; zero warnings/errors. |
| Isolated startup | Attempted once with a cleared child environment, private configuration injected into that process only, no launch profile and no database/storage command. Child exited with code **1**, without the safety timeout. |
| Live restricted database identity | **NOT VERIFIED** by this run. Startup permits only the existing read-only readiness check; its success was not established. |
| Live HTTPS and route isolation | **NOT VERIFIED**. The HTTPS probe failed; the application had exited. Do not interpret this as proof of a certificate defect. No route requests followed the failure. |
| Registration / Customer creation | **NOT ATTEMPTED**. |
| Brevo signup OTP delivery / resend | **NOT ATTEMPTED**. |
| OTP verification | **NOT ATTEMPTED**. |
| Two-browser login / restart persistence | **NOT ATTEMPTED**. |
| Logout / session revocation | **NOT ATTEMPTED**. |
| Hosted writes | No registration, provider Auth mutation, application-table mutation or email-send operation was invoked. Startup may have attempted its restricted read-only database readiness connection/queries; successful execution was not established. |
| Shutdown | Confirmed: original child process absent and no TCP listener on port 7144. External key directory remained empty. No automatic restart. |

## Failure boundary and evidence limits

The exact startup cause is **not established**. Standard output/error were captured
only in supervisor memory and discarded without retaining a sanitized startup
reason. Raw private diagnostics were not printed or written to a log. The only
retained startup result is exit code 1; no database, TLS or provider cause should
be inferred from it.

Two independent browser contexts and runtime route isolation were not established.
Browser tooling setup did not submit any form or contact the V2 application.

Private configuration, certificate/trust material, migrations, roles, V1, GitHub,
Render and SMTP settings were unchanged. No authentication environment was
persisted to the user or machine. No UI/source behavior was changed.

## Exact next safe step

Separately authorize a narrow **read-only startup/readiness diagnosis**, retaining
only a sanitized failure category before discarding raw process output. Do not
resubmit registration, change grants, bypass TLS, regenerate keys or attempt the
real lifecycle until that blocker is resolved and the stopped test is resumed
explicitly.

## Follow-up: one authorized diagnostic startup

The owner subsequently authorized **one** startup diagnosis, without registration,
OTP, email or hosted mutations. That one attempt was performed and stopped:

```text
V2AUTH|stage=RestrictedDatabaseReadiness|category=timeout|type=NpgsqlException|cause=TimeoutException|code=None
```

- Isolated configuration and project pins remained unchanged. A cleared child
  environment contained only the owner configuration and necessary OS variables;
  no user/machine environment or credential store was updated.
- Execution reached restricted database readiness after configuration validation,
  authentication/service registration, DP certificate setup and host construction.
- The underlying exception now propagates to the startup-only sanitized reporter
  instead of being swallowed by `Ready`. Request-time calls still fail closed.
- Exit code: **1**. Approximate full supervised attempt duration: **15.5 seconds**,
  consistent with the existing 15-second connection timeout; this is not a captured
  connection-versus-command timing trace.
- A separate **single credential-free DNS/TCP check**, not another application
  startup, resolved the configured endpoint and connected to TCP 5432 in roughly
  **0.1 seconds**. No endpoint/address, credentials or SQL were output; no query was
  submitted by that probe.
- Data Protection warmup and HTTPS binding were not reached. The key directory
  remained empty. This attempt provides no evidence of an HTTPS certificate fault.
- Shutdown confirmed: the child exited and **port 7144 was not listening**.
  No authentication form request, provider mutation or hosted write was invoked.

### What this establishes—and does not

The actual failure category is a **restricted database readiness timeout**, with
`NpgsqlException` wrapping `TimeoutException`, not the previous generic
schema/permission failure. DNS/basic TCP reachability worked at the subsequent
probe time. That does not prove PostgreSQL/TLS/authentication negotiation or
the readiness SELECT succeeded, nor establish the timeout's exact underlying
network/server/protocol cause. No PostgreSQL SQLSTATE was retained.

The smallest justified source correction is preserving the original startup
exception and reporting only fixed stage/category/type/code fields; that is now
implemented. **No password, endpoint, certificate, grant, schema, timeout or TLS
change is justified by this evidence alone**, and none was made.

The next narrow diagnostic, if separately approved, should distinguish database
connection/TLS/login completion from readiness-query execution, using the same
verified target, account and strict TLS settings with sanitized timings. Do not
blindly increase timeouts, rotate working credentials, bypass readiness, change
privileges or rerun migrations. A resumed controlled lifecycle still requires
successful startup and explicit authorization; registration was not retried.

### Local verification and changed files

`dotnet run --project tests/CustomerAuthenticationIntegrationChecks/CustomerAuthenticationIntegrationChecks.csproj --no-restore -- --startup-diagnostics`
passed **21 synthetic stage/category/redaction checks**. This mode starts no host,
reads no owner configuration and opens no database connection.

Changed:

- `Frontend/GlamByMariya.Web/Program.cs`
- `Frontend/GlamByMariya.Web/Security/PersistentCustomerAuthentication.cs`
- `Frontend/GlamByMariya.Web/Security/CustomerAuthStartupDiagnostics.cs` (new)
- `tests/CustomerAuthenticationIntegrationChecks/Program.cs`
- `tests/CustomerAuthenticationIntegrationChecks/CustomerStartupDiagnosticChecks.cs` (new)
- This evidence document.

The private run directory contains only a sanitized result JSON, not raw process
output or secrets. No application retry or real Customer operation followed the
diagnostic failure.
