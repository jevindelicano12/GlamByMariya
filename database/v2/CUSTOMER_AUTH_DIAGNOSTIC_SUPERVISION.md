# Diagnostic supervision correction — local preparation only

The approved consolidated run was **not executed**: pre-execution inspection found
that the old remaining-duration argument started its timer after managed startup
and configuration loading. Native launch could also occur with an exhausted
budget, and per-child waits were not independent whole-tree supervision.

## Corrected boundary

- `Run-CustomerAuthDatabaseDiagnostic.ps1` launches the standalone .NET supervisor
  **before reading owner configuration**. Network modes require supervision;
  local configuration/self-test modes remain offline.
- The supervisor computes one absolute, cross-process monotonic deadline at
  **75 seconds**, before starting the configuration-reading PowerShell worker.
  Runtime startup, configuration, native psql, protocol/TLS and managed opening/
  queries consume the same budget; no child gets a fresh remaining-duration timer.
- At 75 seconds the independent supervisor signals cancellation. PowerShell
  checks the deadline/event before either child launch and while waiting; the
  managed runner creates its cancellation source before configuration loading
  from that same deadline and checks it again before each stage.
- A separate timer in the supervisor calls `TerminateJobObject` at **90 seconds**,
  independently of the worker's execution, blocking I/O or exception handling.
  This is an OS termination request, subject to normal Windows scheduling and
  termination latency—not a hard-real-time guarantee.
- The worker is created **suspended**, assigned to a Windows Job Object, and only
  then resumed. Assignment failure terminates the suspended worker without running
  it. Descendants inherit containment; breakaway is not enabled.
- `KILL_ON_JOB_CLOSE` also terminates the contained tree if the supervisor is
  interrupted. Normal completion/failure terminates any orphan descendants and
  checks the job's active-process count reaches zero.
- The outer launcher also bounds its wait for the supervisor to 90 seconds
  (including supervisor startup). On expiration it kills the supervisor, invoking
  job-close cleanup. It has no unbounded exit/output-drain waits.
- Expired/cancelled stage budgets throw; they are never clamped to a positive
  allowance. Native stage backstop remains 45 seconds inside the global budget.
  Cleanup waits in the worker are bounded; a stuck worker cannot defeat the
  independent job termination.

No SQL, restricted identity/target, credentials, certificate trust, MVC connection
settings, GSS comparison behavior, pool bounds or retry behavior was changed.
Connection/command settings remain **15/30 seconds** and TLS remains **VerifyFull**.
The new supervisor clears inherited provider/hooks settings and applies a fixed
output whitelist; it never receives the owner configuration or native password.
The configuration-reading worker retains the existing ACL/pin checks.

## Offline verification

```powershell
dotnet build tools/CustomerAuthDatabaseDiagnostic/CustomerAuthDatabaseDiagnostic.csproj --no-restore
dotnet tools/CustomerAuthDatabaseDiagnostic/bin/Debug/net10.0/CustomerAuthDatabaseDiagnostic.dll --self-test
.\tools\Run-CustomerAuthDatabaseDiagnostic.ps1 -SelfTest
dotnet tools/CustomerAuthDatabaseDiagnostic/bin/Debug/net10.0/CustomerAuthDatabaseDiagnostic.dll --supervision-self-test
```

Supervision fixtures use shortened **test-only** deadlines and harmless local
processes. They check exhaustion before launch, cancellation after simulated
configuration work, independent hard-stop of a noncooperative three-generation
tree, orphan cleanup on normal exit, failure without retry, output redaction,
the actual PowerShell budget/wait functions, and interruption of the supervisor.
Fixture files contain only local test PIDs/markers. Cleanup is best-effort:
if Windows retains a transient lock after verified process shutdown, the harness
reports deferred cleanup and leaves that dedicated test directory, without retry.
No owner configuration, DNS, socket, psql login, Customer app or provider is used.

Local results: the diagnostic build passed with zero warnings/errors, all **72**
existing offline checks passed, PowerShell synthetic filter/budget checks passed,
and **13** supervision checks passed (including the real PowerShell functions
and supervisor-interruption cleanup). No hosted diagnostic was executed.

An intermittent verification failure exposed a fixture PID-file sharing race.
The test reader now permits concurrent fixture writes and ignores unterminated
PID records; fixed phase/check labels identify any later test failure without
printing raw exceptions. PowerShell fixture startup headroom is test-only:
production connection/command and whole-run limits remain 15/30 and 75/90 seconds.
Repeated runs passed all supervision assertions; one run then encountered a
temporary-file cleanup IOException after the assertions passed. That housekeeping
condition is now reported separately, not mistaken for a supervision-test failure.

Production 75/90 limits are fixed in `DiagnosticSupervisor.Run`; test durations
are not exposed as hosted-command options. The approval-gated command is unchanged,
but **this correction does not authorize or execute it**. Repeat the mandatory
safety inspection and obtain separate owner authorization before a hosted run.
