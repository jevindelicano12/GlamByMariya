# V2 Npgsql timeout — consolidated root-cause review and next diagnostic

November 17, 2026. Deadline: November 22. Target: `hqllkbjhufhvyooacajy`.
**Preparation only: no hosted request, Customer host, registration, OTP, migration
or provider configuration change was performed in this review.**
The owner's Globe Unli 5G hotspot remains the intended network. No different
network, VPN, TLS bypass or longer connection timeout is required by this plan.

## Finding

The best-supported failure location is **physical PostgreSQL connection opening
after TCP but before an observed TLS handshake**. The exact request that stalls
is still unknown. The 15-second failure is not evidence of a slow readiness query,
local pool exhaustion, a wrong password or a certificate rejection.

GSS disabling did not help (15,035 ms versus 15,029 ms). Do not apply it to MVC as
a claimed fix. A new loopback-only test demonstrated that this machine's .NET
TLS stack validates a synthetic CA, accepts the correct IP SAN, rejects a wrong
hostname, and emits the TLS events used by the diagnostic. That makes a generally
broken Schannel/event listener less plausible, without proving remote TLS works.

## Evidence inventory

- Previous hosted runs: DNS 120/102 ms, TCP 96/176 ms, pool dispatch 11 ms;
  `NpgsqlException -> TimeoutException` during cold physical opening; no TLS event,
  completed borrow, identity/readiness SELECT or transaction.
- Owner-reported psql login/TLS success. The exact historical invocation and its
  effective options/timing were **not retained**; this is an important comparison
  gap, not permission to inspect shell history, `.pgpass`, V1 stores or private
  global service profiles.
- Installed native client: psql/libpq **18.6**, bundled OpenSSL **3.5.9**.
  The installed libpq binary contains the SSL negotiation, GSS, CA, SNI and channel
  binding environment-option names used by the prepared control.
- Current standalone runtime: **Npgsql 10.0.0**, target net10.0, **.NET 10.0.14
  x64**, Windows. Metadata confirms the observer's DNS/TCP/TLS events are enabled
  at its configured level. No driver package/runtime substitution was made.
- Shared `DatabaseConfiguration` forces VerifyFull, 15-second open / 30-second
  command timeout, pool 0–2, no error detail/password persistence. Existing DB CA
  selection is system trust. Password and project-qualified username are passed
  as builder values, not assembled into an unescaped URI.
- `Program.cs` creates one singleton data source. `Ready` awaits opening, then a
  separate exact SELECT. There is no synchronous wait, existing pool holder or
  EF execution strategy in this startup path. The original token is `None`;
  DP/file-key warmup and HTTPS hosting occur later.

## Prioritized root-cause matrix

“Contradicted” refers to the proposed cause of the **observed timeout**, not proof
that the subsystem can never fail. Unknown candidates remain unknown.

| Priority / candidate | Evidence status | Evidence and remaining distinction |
|---|---|---|
| P1 — PostgreSQL SSLRequest/encryption negotiation stalls | **Supported as failure class; exact cause unknown** | TCP completed quickly, then physical opening consumed 15 s without TLS events. The SSLRequest response has never been measured. |
| P1 — psql and Npgsql were not actually compared with identical effective inputs | **Cannot determine** | Historical psql flags, exact endpoint, CA source, environment and duration are absent. Replay both from the same private configuration rather than assume equality. |
| P1 — wrong/current Session Pooler endpoint or unhealthy/variable pooler worker | **Cannot determine** | Host suffix, project-qualified account and port match the recorded V2 contract; exact historical host/backends/health are not freshly established. Do not change region/endpoint speculatively. |
| P1 — mobile-path payload handling, transient interference or MTU/DPI effects | **Cannot determine** | TCP success does not establish PostgreSQL packet exchange. Prior psql success is not a simultaneous control. Use the same Globe connection; no network replacement is prescribed. |
| P2 — a blanket DNS, IPv4 or TCP-5432 block | **Contradicted for recorded runs** | DNS and TCP passed; previous resolution returned IPv4 addresses. This does not eliminate per-address/transient behavior. No new ping/DNS/TCP-only probe is needed. |
| P2 — SSL direct/Postgres mode, SNI, ALPN or client-hello differences | **Cannot determine** | Npgsql uses Postgres negotiation. Historical psql mode is unknown. OpenSSL and Schannel are concrete different TLS implementations; ALPN/client-hello bytes are not claimed identical. Do not switch to Direct without evidence. |
| P2 — trusted roots, chain/name validation or TLS versions differ | **Cannot determine remotely; not the observed rejection category** | Npgsql selects system trust; psql's historical CA source is unknown. No remote HandshakeStart/AuthenticationException was recorded. Local strict chain/name validation succeeds, but the Supabase chain was not tested here. |
| P2 — optional GSS encryption is the sole cause | **Contradicted by comparison** | Disable produced essentially the same timeout. No production GSS change is justified. Other startup/authentication mechanisms are not thereby proven. |
| P2 — Windows/psql environment or hidden startup files alter behavior | **Cannot determine historically** | psql may inherit PG options/service/profile settings; the managed supervisor clears them. New control uses `-X -w`, cleared environment, explicit settings and an isolated empty native profile. No V1 profile is read. |
| P2 — bad restricted password, username, SASL or channel binding | **Cannot determine current server authentication; lower support as pre-TLS cause** | Owner established credentials; current username/pins are checked, but historical password equivalence is not independently observed. TLS-before-password has not completed. No authentication SQLSTATE was received. |
| P2 — hosted Session Pooler capacity/backend availability | **Cannot determine** | A TCP listener does not prove backend capacity. No capacity SQLSTATE was received. Do not increase local pool size or change roles. |
| P3 — local Npgsql pool exhaustion/leak | **Contradicted for these runs** | Fresh process/source, minimum 0, first cold borrow, dispatch 11 ms, physical opening started. Another process cannot hold this local pool. |
| P3 — application DI, synchronization-context deadlock or EF retries | **Contradicted as necessary cause** | The standalone runner reproduces the timeout without MVC/DI/EF. Startup readiness uses async awaits and a direct Npgsql command. |
| P3 — readiness SQL, grants, RLS, migration or query cancellation | **Contradicted as the recorded timeout location; readiness still unverified** | Neither SELECT was reached. SQL hash remains pinned; the command's 30 s budget never started. No migration/grant change is justified. |
| P3 — caller/75 s cancellation caused the 15 s failure | **Contradicted** | Original startup token `None`; diagnostic budget 75 s; observed cause `TimeoutException`, not an outer cancellation at that budget. |
| P3 — unsupported .NET target, x86 mismatch, missing TLS engine | **Contradicted for a generic incompatibility** | Npgsql net10 loads/runs on .NET 10.0.14 x64; TCP and local strict TLS work. This does not rule out a specific driver/remote interoperability defect. |
| P3 — Npgsql 10-specific implementation/regression | **Cannot determine** | Exact driver version is known; a fresh standalone repro exists. No local defect or approved remote regression comparison proves a driver bug. No online advisory/source fetch or speculative package downgrade was performed. |
| P3 — universally missing TLS telemetry | **Contradicted locally; case-specific gap not eliminated** | Real loopback SslStream handshake emits observed events at the configured levels. Absence on the hosted run remains evidence, not a wire capture. |
| P3 — owner HTTPS/DP certificates or cookie protection | **Contradicted as this database timeout cause** | Standalone diagnostic never loads them or hosts MVC. DB TLS trust is a separate setting. Owner certificates/keys were not regenerated or reused. |

## psql versus Npgsql: concrete comparison

| Item | Earlier successful psql | Failing Npgsql / verified local setting |
|---|---|---|
| Endpoint/port/project/user | Owner reports correct V2 restricted login; exact invocation not retained | Split owner fields; Session Pooler 5432, postgres, restricted project-qualified user, V2 pins checked |
| Password | Owner reports success; not copied/read from historical stores | Saved private external field; no trim/interpolation; not printed or persisted by the diagnostic |
| TLS mode | “Verified TLS” owner report; exact `sslmode` token not available | Explicit VerifyFull; no accept-all validation callback |
| CA source | Unknown | System trust; not the HTTPS/DP PFX |
| Encryption negotiation | Unknown | Postgres SSL negotiation; Prefer and Disable GSS runs both failed |
| SNI / channel binding | Historical settings unknown | DNS target preserved; current driver channel binding Prefer |
| Transport/TLS library | Current installed libpq 18.6 / OpenSSL 3.5.9 | Npgsql 10 / .NET 10.0.14 / Windows Schannel |
| Startup environment | Historical PG variables, service files and rc flags unknown | Cleared child environment, explicit owner file; no inherited PG/telemetry hooks |
| Timeout | Historical value/duration unknown; psql may have allowed longer | 15 s open, 30 s command; no increases proposed |
| Pool/query path | psql is not a Npgsql local pool; historical SELECT unknown | One fresh source, 0–2 pool, exact readiness query only after opening |

“Both use TLS” is not proof of equal trust sources, startup packets, endpoints,
timings or credentials. The proposed control below establishes a new, comparable
baseline; it does not rewrite the historical facts.

## One consolidated hosted diagnostic — prepared, NOT authorized/executed here

After separate approval only:

```powershell
.\tools\Run-CustomerAuthDatabaseDiagnostic.ps1 -OwnerApproved -Consolidated
```

Use the **same machine and Globe hotspot**, with no concurrent owner config edits
or Customer app. Project/account/private-ACL pins are rechecked. Close editors;
the supervisor rejects configuration/ACL changes between clients and after the run.

One supervised sequence, stopping on the **first failure**, with no retry/fallback:

1. **Native control once.** Run only the reviewed local psql 18 executable, with
   no connection string/password in arguments, no startup rc, no prompts/history,
   cleared environment and an empty owner/SYSTEM-only native profile outside
   source. Set the exact saved endpoint/account/database/password, verify-full,
   current CA source (`system` when blank), Postgres negotiation, GSS disable, SNI,
   UTF8 and channel-binding Prefer. Do not read V1/default client keys or service
   profiles. psql runs `BEGIN READ ONLY; SET LOCAL statement_timeout=30000;` one
   boolean restricted-identity/database/read-only SELECT, then `ROLLBACK`.
   No PGOPTIONS startup override is added. Native phase backstop is 45 s
   (15 s connection + 30 s statement), within the overall budget.
2. **Credential-free protocol/TLS preflight once.** Connect to the same configured
   endpoint. Send exactly the standard eight-byte PostgreSQL SSLRequest; require
   the one-byte `S` response, with separate timing. Then perform native .NET TLS
   with the same hostname and configured trust source, strict chain/name checking,
   no custom validation callback and no version forcing. A refusal, EOF,
   unexpected reply or timeout stops the run. This socket sends **no PostgreSQL
   startup/authentication message, username, password or SQL** and is closed.
   Connect + SSLRequest + TLS share the existing 15 s open budget.
3. **Existing managed check once.** Keep the previously tested diagnostic-only
   GSS-disable setting; no other connection setting changes. One cold borrow,
   one sequential warm borrow with same-session proof, strict TLS observations,
   then `BEGIN READ ONLY`, the existing identity SELECT, the unchanged shared
   readiness SELECT once, and `ROLLBACK`. No MVC/Auth/email services exist here.

The **whole sequence** uses one absolute 75 s cancellation deadline and an
independent 90 s Windows Job Object termination timer, not 75/90 s per subtest.
The supervisor starts the configuration-reading worker suspended, assigns it to
the job, then resumes it. Native psql and managed children are sequential
descendants of that worker, all contained in the job. Interruption closes the job
and kills descendants. See `CUSTOMER_AUTH_DIAGNOSTIC_SUPERVISION.md` for the local
correction prompted by the subsequently blocked pre-execution safety inspection;
no hosted run was performed by that correction.
Monotonic elapsed timestamps restart in the managed child; compare durations,
not elapsed timestamps across different processes.

Approval must cover one native DB login, one credential-free SSL/TLS socket and
at most two managed pool borrows (normally one managed physical session). There
are at most three application SELECTs: native identity, managed identity and
managed readiness, plus normal driver metadata/session-reset traffic. Two
read-only transactions are rolled back. `SET LOCAL` is session-only, not a
persistent timeout/configuration change. Normal library attempts over resolved
addresses remain bounded; there is no application retry or alternate endpoint.

**No packet dump, certificate contents, endpoint, credentials, email, token,
raw exception/stack or child stderr is exported.** The native password exists
only in that child's process environment. Empty native-profile directories are
removed; unexpected contents are left private for owner review, never dumped.
No owner file/password/trust store/schema/role/provider resource is modified.

### Outcome decisions

- Native strict baseline fails: stop; historical psql success was not reproduced
  under explicit comparable settings. Do not automatically reinterpret it as a
  password defect or weaken CA verification.
- Native passes but SSLRequest does not return `S`: stop with a concrete protocol
  response boundary. Host/backend selection and library/path behavior remain
  candidates; do not blame TLS certificate validation yet.
- SSLRequest passes but .NET TLS fails: stop with a genuine TLS/chain/name stage;
  inspect only the sanitized category before proposing a trust/protocol fix.
- Native and strict .NET preflight pass but Npgsql still fails: now prioritize
  driver encryption/startup/auth/bootstrap behavior; no further CA/GSS guessing.
- Managed opening passes: identity and unchanged readiness decide the next layer.
  Passing this still does not authorize Customer startup, signup or email.

## Local results / actual fixes

- **72** offline protocol/configuration/query/redaction checks passed.
- Supervisor self-tests cover native target pins, secret-only process environment,
  inherited-profile isolation, output filtering and consolidated stage labels.
- Local protocol/TLS tests passed: exact SSLRequest encoding, strict synthetic-CA
  validation, real TLS telemetry, hostname rejection and no false PostgreSQL
  login claim. Only a public synthetic test CA was written and removed; private
  test keys were memory/temporary OS-backed objects, not owner files.
- Runtime/client metadata review completed without network calls.
- The only production source fix in this review is unrelated to the timeout:
  public-only deployment now rejects absent/blank/wildcard/malformed AllowedHosts.
  Release publish and **51** information-only deployment checks passed.
- Linux Docker engine is unavailable locally; no image registry, package server,
  Supabase, Render, GitHub or other hosted request was made.

No proven application connection-setting defect was found to fix. The remaining
failure boundary requires the consolidated approved run, not another auth system,
password reset, grant change, network replacement or longer timeout.
