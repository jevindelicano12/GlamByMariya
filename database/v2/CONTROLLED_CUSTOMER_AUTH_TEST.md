# Controlled real V2 Customer test — preparation, not authorization

**Code ready for owner configuration and separate isolated-test approval. Real
Supabase/provider success is NOT verified.** No hosted Auth request, SQL, account,
permission change, migration, deployment or Brevo operation was performed here.

Owner now confirms 0001/0002/0004 revision 2 applied, all ten hosted checks passed,
restricted roles verified and temporary owner membership removed. These are
owner-reported facts, not fresh remote observations by this development task.
Owner additionally confirms the corrected V2 reference
`hqllkbjhufhvyooacajy`, restricted Session Pooler login with TLS, privately saved
backend password, and publishable-key comparison **MATCH**. Do not repeat those
checks or rotate working credentials to proceed.
0003 remains unapplied. Do not rerun migrations/provisioning or the local runner
against hosted Supabase.

## What works and what stays closed

The actual MVC controller, registration/retry services, provider adapter and
restricted PostgreSQL adapters remain in use. Registration is Customer-only,
browser-bound and stores a protected profile/hash. OTP resolves `/auth/v1/user`
before one atomic Customer/binding/generation commit. It then requires a separate
password login. Login again verifies `/user`, existing immutable binding, active/
lock state, credential epoch, password version and session generation.

Every authenticated request revalidates the database security snapshot. The
provider identity is verified remotely at OTP/login/password proof, then compared
to the immutable DB binding on requests; there is **no remote `/user` request on
every page**. Supabase tokens/passwords/OTP are not retained in the app cookie.
Logout advances the database generation and removes this browser's cookie. It
invalidates all local Customer tickets, not GoTrue's outstanding tokens.

Normal durable-auth startup still throws. Only the explicit
`v2-customer-auth-test` profile in environment `V2CustomerAuthTest` can start:

- One real loopback HTTPS listener, exact Host `127.0.0.1`, local peer required.
  No Render PORT, wildcard/multiple URLs, endpoint overrides or forwarded-header
  trust. Do not expose it via a tunnel, reverse proxy, container port or LAN.
- Only the configured synthetic email can register, verify/resend, log in or
  recover. Existing cookies for a different email are rejected.
- Original guest Login/Register/Verify/Forgot/Reset Razor forms and pink assets.
  Successful login goes to `/auth-test/session`, an authenticated no-store JSON
  probe, **not** Bookings. Authenticated GET Account pages redirect to that probe.
  To start another registration/recovery flow, sign out first.
- Booking, payment, Admin/SuperAdmin, invitation, profile/preferences/avatar,
  catalog/settings and database/storage commands are unavailable. Unknown routes
  return 404 before controller execution. Staff provisioning remains a separate
  private owner operation; no staff credential belongs to this host.
- No `mvc_*` views, business/settings/preferences rows, payment tables or DB key
  table are required or created for this auth-only test. The existing
  `PublicStudioStore` supplies the guest footer clock using `Studio:TimeZone`;
  security/registration queries use 0004's tables and revocation function directly.
  This does **not** make full normal MVC/booking startup ready.

### Expected limits — stop rather than invent a repair

The real provider's rejection/consumption semantics have not been verified.
An invalid/expired provider OTP, rate-limited resend, transport timeout, lost commit
acknowledgement or identity mismatch can fence the attempt. Do not automatically
resubmit it, delete it, replace its subject or create another hosted test account.
A malformed local code is rejected before the provider, but an actual rejected
provider OTP is not assumed safe to replay. This conservative behavior must be
resolved separately before a general public-auth release.

Password uncertainty leaves Pending/ReconciliationRequired and denies tickets.
There is no operator repair endpoint or automatic cleanup job. A 503 is not proof
of rollback. Preserve only sanitized outcome/state evidence and reconcile under
separate authorization; never broaden backend rights.

## Owner configuration — prepare without starting the host

Private staging and offline-check results are recorded in
`CUSTOMER_AUTH_OWNER_CONFIGURATION.md`. The external file now contains private
owner configuration; do not display it. It is not imported or auto-loaded and
authorizes no launch or hosted contact.

Use a **new V2-only launch environment/secret store**, not a V1 `.env`, shell,
saved browser profile, connection string, PFX or user-secrets collection. Do not
paste values in chat, command history, source, a launchSettings file or logs.
The project has its own V2 UserSecretsId, but this custom environment does not
automatically load Development user-secrets: securely inject environment values.

Verify the V2 dashboard project reference independently first. The two project
pins must equal it; do not derive both from an unreviewed old connection string.

| Environment variable | Required value / source |
|---|---|
| `DOTNET_ENVIRONMENT`, `ASPNETCORE_ENVIRONMENT` | Both `V2CustomerAuthTest` |
| `Deployment__Profile` | `v2-customer-auth-test` |
| `Deployment__V2ProjectId`, `Database__ProjectId` | Same independently verified **NEW V2** 20-letter reference |
| `ASPNETCORE_URLS` | `https://127.0.0.1:7144` (one unoccupied port >=1024) |
| `AllowedHosts` | `127.0.0.1` |
| `PublicSite__InformationOnly` | `false` **only in this isolated test process** |
| `Authentication__DurableSessionEnforcement` | `true` **only in this isolated test process** |
| `Authentication__RestrictLegacyFallback` | `true` |
| `Authentication__IdentityLinkingEnabled` | `false` |
| `Authentication__IntegrationCustomerEmail` | One new owner-controlled synthetic mailbox, not used in V1 or existing V2 accounts |
| `Database__Provider`, `Database__Schema` | `PostgreSql`, `public` |
| `Database__Host` | Verified V2 Direct `db.<v2-ref>.supabase.co`, or its verified Session pooler hostname |
| `Database__Port`, `Database__Name` | `5432`, `postgres` (not transaction pooler 6543) |
| `Database__Username` | Direct: `glam_v2_backend`; Session pooler: `glam_v2_backend.<v2-ref>` |
| `Database__Password` | Restricted backend LOGIN password from the owner's secure store |
| `Database__RootCertificate` | Optional verified Supabase CA file if system trust is insufficient; never disable TLS verification |
| `Database__MaxPoolSize` | `2` for this single isolated process (allowed 1–6); budget all other clients |
| `Studio__TimeZone` | `Asia/Manila` |
| `Supabase__Url` | Exactly `https://<v2-ref>.supabase.co`, no trailing slash/path |
| `Supabase__AnonKey` | V2 public **publishable** key or matching legacy `anon` JWT; never secret/service_role |
| `DataProtection__KeyRingPath` | Precreated private absolute folder **outside source**, e.g. `%LOCALAPPDATA%\GlamByMariyaV2\CustomerAuthKeys` expanded to its full path |
| `DataProtection__CertificatePfxBase64` | Owner-generated RSA private-key PFX, base64, delivered as a process secret |
| `DataProtection__CertificatePassword` | Separate strong PFX password secret |
| `Kestrel__Certificates__Default__Path` / `...__Password` | Browser-trusted local HTTPS certificate/PFX and secret, stored outside source, valid for `127.0.0.1`; alternatively an already authorized suitable default certificate |
| `Email__DeliveryDisabled` | `true`; blocks legacy app mail, **not Supabase Auth's OTP delivery** |
| `FileStorage__Provider` | `Local`; no file-writing workflow is exposed |

Do not set `ConnectionStrings__MakeupDatabase` alongside the split DB values:
it takes precedence. If using it instead, it must be the complete reviewed
restricted Direct/Session connection and pass the same V2 pin/username/port checks.
Connection TLS is forced to **VerifyFull**, with sanitized diagnostics, a bounded
pool and no error-detail/password persistence. Do not add bypass flags.

Unset `PORT`, custom `Kestrel__Endpoints__*`, staff/service keys, Storage keys,
Brevo/SMTP/B2 credentials and Demo/website Demo feature flags. Do not attach
privileged credentials merely because an older feature had them.

**Role provisioning is not credential establishment.** If glam_v2_backend still
has no usable LOGIN password, credential establishment is a separate authorized
owner action, not a reason to substitute postgres or service_role.
For this owner-confirmed setup the credential is already established; do not
repeat that provisioning step.

### Safe offline synthetic-email entry

The earlier chat-only entry procedure fails under Windows PowerShell 5.1 at
`File.Replace(source, target, $null)`: PowerShell converts `$null` to an empty
backup-path string, causing `ArgumentException` (`0x80070057`) before replacement.
This was reproduced on disposable synthetic files; a Gmail plus-address passes
format validation. The corrected script passes
`[System.Management.Automation.Language.NullString]::Value` instead.

From the workspace, in a local PowerShell window without recording/transcription:

```powershell
.\tools\Set-CustomerAuthEmail.ps1
```

Enter the address only at the hidden prompt, never as a command argument or in
chat. The script refuses **any** nonempty allowlist, including the same address.
It replaces only the quoted email value, retains UTF-8 BOM presence and target
ACLs, checks project/profile pins and concurrent changes, and reports only a
sanitized failure stage. If post-commit verification fails, stop: the entry may
already be saved; do not retry or overwrite it. Close the shell afterward.
Editing this file does not import configuration or activate authentication.

Local synthetic-only verification (never opens the owner configuration):

```powershell
.\tools\Set-CustomerAuthEmail.ps1 -SelfTest
```

### Persistent Data Protection

The owner must precreate the external key directory with access restricted to
the test process identity and authorized recovery administrators (Windows ACL;
on Unix a private directory, e.g. mode 0700). No junctions/symlinks, source folder,
OneDrive/public share or wwwroot. The app encrypts XML signing keys with the RSA
certificate and uses the stable application discriminator
`GlamByMariya.V2.CustomerAuth.<v2-ref>`. The private certificate must be currently
valid for >7 days; use an appropriate longer-lived test certificate.

Keep **both** the key directory and private certificate/password safely backed
up separately. Reuse them across restart; never regenerate/delete them per run.
Certificate rotation/old-key decryption requires a separate plan. Losing either
material invalidates cookies and prevents pending protected profiles from being
decrypted; restarting with a fresh key ring is not a recovery procedure.

The TLS certificate and Data Protection certificate serve different purposes.
In particular, Windows Schannel needs an OS-backed TLS private key; the DP PFX
is intentionally loaded ephemerally and is not an automatic Kestrel certificate.
Do not disable browser/TLS validation to make the test work.

### Local certificate preparation completed

The local-only `tools/CustomerAuthCertificates` utility prepared separate
one-year RSA certificates and encrypted PFX files under the existing private
external `CustomerAuthOwner\certificates` folder:

- `customer-auth-dp.pfx`: Data Protection private material.
- `customer-auth-https.pfx`: HTTPS private material, IP SAN `127.0.0.1` and server-auth usage.
- `customer-auth-https.cer`: **public certificate only**, for owner-reviewed trust.

Only the four DP PFX/password and HTTPS path/password fields were populated.
Passwords were generated locally and never printed. Existing backend/API values,
Pooler endpoint and file ACLs were preserved; no application/trust store/provider
was accessed. The key ring directory remains empty until approved host startup.
Do not rerun preparation: it refuses existing material rather than overwriting it.

Preparation utility verification (no owner credentials or providers):

```powershell
dotnet restore tools/CustomerAuthCertificates/CustomerAuthCertificates.csproj --source . --ignore-failed-sources
dotnet run --project tools/CustomerAuthCertificates/CustomerAuthCertificates.csproj --no-restore -- --self-test
```

The explicit `--prepare` mode was already run locally. It creates private files
and modifies the external configuration, so it is **not** a read-only test.
It never imports certificates into a Windows store, starts a host or uses a DB.
Its config update is atomic, with exact-field/ACL checks and no backup secret copy.
Any interrupted/partial preparation is BLOCKED for private reconciliation, not
permission to delete existing files or overwrite signing material.

**Remaining Windows action:** review and approve trusting just the generated
V2 local HTTPS **public** `.cer` in the current user's Trusted Root store. This is
a Windows trust change; it has **not** been performed automatically. If approved,
run locally without exporting/printing material:

```powershell
$publicCert = Join-Path $env:LOCALAPPDATA 'GlamByMariyaV2\CustomerAuthOwner\certificates\customer-auth-https.cer'
Import-Certificate -FilePath $publicCert -CertStoreLocation Cert:\CurrentUser\Root | Out-Null
```

Do not import the DP certificate, use LocalMachine trust, trust V1 material or
bypass TLS validation. Record a V2-only removal plan for this local trust after
testing; do not remove any other certificate. Browser trust/actual local HTTPS
handshake still need owner verification during the separately approved test.

Startup checks restricted backend role/capabilities using SELECT only and warms
the local encrypted key ring; it makes no provider Auth call or DB write.
Do not start even this read-only hosted check until separately authorized.

## Supabase settings the owner must verify

Record the existing V2 settings without changing anything during this preparation:

1. Email/password provider and signups available **for the separately approved
   synthetic test**, with **Confirm email enabled**. If currently disabled, its
   enablement requires separate authorization; do not open public registration
   as a side effect of local preparation.
2. Confirmation email includes the six-digit `{{ .Token }}`, not only a link.
   The MVC UI/adapter requires **6 ASCII digits**. Confirm token length/expiry
   (e.g. 10 minutes, within the app's 30-minute attempt lifetime).
3. Delivery can reach the authorized mailbox. Built-in Supabase mail may restrict
   recipients; choose an allowed owner/team mailbox or separately approve another
   delivery arrangement. No Brevo/SMTP configuration change is part of this task.
4. Existing provider rate limits permit one signup and one resend, with the
   required cooldown (app minimum 60 seconds). Respect actual limits; stop on 429.
5. If Site URL/redirect allowlist is needed, use only the chosen exact isolated
   HTTPS origin; no wildcard/production/V1 callback. This flow submits email OTP
   directly and does not use a JS session/PKCE/OAuth callback.
6. Provider CAPTCHA or additional required factors are not implemented in these
   existing forms. If required, **BLOCKED** pending integration/explicit test
   authorization; do not silently weaken security settings.
7. Recovery email `{{ .Token }}`/password update policies need verification only
   if those additional actions are included in the later test approval.

## Exact future test — only after explicit authorization

If startup is blocked by the recorded Npgsql timeout, use the separately scoped
preparation in `CUSTOMER_AUTH_DATABASE_DIAGNOSTIC.md`. Its offline/local modes
do not launch this host. The hosted diagnostic still needs explicit owner
approval and does not authorize Customer registration or OTP operations.

Shortest safe path: approve local HTTPS trust → privately choose/set one synthetic
email and confirm the OTP settings above → approve the named one-Customer test →
securely inject the external JSON as **process-only** environment variables and
run the existing loopback profile → signup/resend/OTP → password login →
same-key restart → second-browser login → logout/revocation. Do not upload this
configuration or local certificates to Render.

The custom environment does not auto-load this JSON. Use a fresh, V2-only
launching process, no inherited provider/service keys, no transcripts, no variable
dumps, and no user/machine-wide environment writes. Do not launch while preparing
or merely confirming provider settings. Hosted password/recovery tests remain a
separate scope.

Approval must name project reference, the single synthetic email, machine/local
origin, time window and allowed writes: durable signup attempt, one provider
signup, at most one planned resend, OTP confirmation, Customer/binding/generation
commit, password logins and logout generation revocation. No staff, schema,
booking, payment, cleanup/deletion or provider administration is included.

1. Owner supplies 0004 success/cleanup evidence and confirms no conflicting
   provider/local account for this new address. Retain recovery evidence. Do not
   rerun the “empty post-migration” check after legitimate Customer creation.
2. Build locally. After injecting the isolated environment securely, launch from
   the workspace with launch settings disabled:
   ```powershell
   dotnet run --project Frontend/GlamByMariya.Web/GlamByMariya.Web.csproj --no-launch-profile
   ```
   No database/storage command. Abort on failed pins, missing permissions, bad
   certificate/key directory or unsupported settings. Do not “repair” grants.
3. In a new dedicated browser profile, open
   `https://127.0.0.1:7144/Account/Register`. Confirm the unchanged pink form.
   Register **once** with synthetic details and a unique strong password. Record
   only accepted/rejected and sanitized attempt state; never password, OTP,
   session/refresh token, cookie or headers.
4. Confirm no authenticated app session before OTP. Test the immediate resend
   cooldown if desired (no provider call). After the actual cooldown, submit
   **one** planned resend from the same Verify page and use only the latest code.
   Enter it once in the original browser within both expiry windows.
5. Expect redirect to Login, not automatic authentication. Independently sign in
   with the synthetic password. `/auth-test/session` must return
   `status=authenticated, role=Customer`. Cookie must be
   `__Host-GlamV2.CustomerAuthTest`, HttpOnly, Secure, Strict, Path=/, no Domain.
   It is a 30-minute non-sliding **session cookie** (no browser-close persistence
   guarantee); application restart must not invalidate an otherwise valid ticket.
6. Restart the **same** local app with the same environment, certificate and key
   directory, then reload the probe. It must still authenticate against current
   database generation/epoch/binding. Use a second dedicated browser profile to
   log in to the **same account**; do not register another account.
7. From browser A's same-origin developer console, sign out with the real
   antiforgery token; do not print/copy token material:
   ```javascript
   const proof = await fetch('/auth-test/session', { cache: 'no-store' }).then(r => r.json());
   await fetch('/Account/Logout', {
     method: 'POST',
     headers: { 'X-CSRF-TOKEN': proof.csrfToken },
     body: new URLSearchParams()
   });
   location.assign('/Account/Login');
   ```
   Both A and B must now be denied/redirected on the probe. A fresh password login
   may issue a ticket at the newer generation. Record sanitized outcomes only.
8. Owner's separately authorized read-only evidence should show exactly one
   matching Customer/identity, matching provider subject, Ready epoch, increased
   generation and Consumed attempt; no Admin/SuperAdmin/reservation/payment
   creation. No account-lock mutation is included: lock/disable behaviors have
   already been exercised locally. They need extra approval if tested hosted.
9. Stop the local process and remove the test environment from the launching
   process. Keep signing material private and retain the synthetic state for
   reconciliation. Do not delete the hosted user/rows, reset generations or
   reopen public auth. Record the result before proposing the next phase.

Stop immediately for a timeout, mismatched subject, unexpected count/role, invalid
OTP/provider rejection, uncertain commit, 429, failed key persistence or failed
revocation. Do not repeat the provider operation or swap to elevated credentials.
Password change/recovery are locally tested but are additional hosted writes,
not implicitly included in this one-account basic lifecycle test.

## Local evidence and exact source changes

- `dotnet build Frontend/GlamByMariya.Web/GlamByMariya.Web.csproj`: succeeded.
- Customer integration profile checks: **45** synthetic configuration/provider
  assertions plus a real local HTTPS/Razor smoke without opening a DB connection.
- **68** real MVC/HTTPS/PostgreSQL checks: normal controller DI and actual
  PublicStudioStore, original views, antiforgery, Customer binding, concurrency,
  OTP/resend, login/logout, lock/disable, password/recovery uncertainty, expiry,
  encrypted key/profile/cookie restart and guarded surface.
- **120** migration/permission checks on disposable PostgreSQL **18.6**, using
  0001/0002/unchanged 0004 only. Final retained/stopped test cluster:
  `temp/v2-customer-auth-integration-20261116-4/data`. Earlier failed/timeout
  fixture directories were not overwritten; no original Demo/fixture was used.
- **248** isolated HTTP fake-query/provider checks and **56** signup-retry checks
  passed again. The retry project emits an existing EF Relational version-conflict
  warning; the focused integration build succeeds without warnings after build.
- Hosted PostgreSQL 17.11/provider settings/delivery/cookies remain **unverified
  by this task**. No hosted requests or credentials were used.

Files changed in this preparation (generated bin/obj/temp artifacts excluded):

1. `Frontend/GlamByMariya.Web/Program.cs`
2. `Frontend/GlamByMariya.Web/Controllers/AccountController.cs`
3. `Frontend/GlamByMariya.Web/Security/CustomerAuthIntegrationProfile.cs` (new)
4. `Frontend/GlamByMariya.Web/Security/V2DeploymentConfiguration.cs`
5. `Frontend/GlamByMariya.Web/Security/PersistentCustomerAuthentication.cs`
6. `Frontend/GlamByMariya.Web/Security/StudioCookieAuthentication.cs`
7. `Backend/GlamByMariya.Infrastructure/Data/PostgresStudioStore.cs`
8. `Backend/GlamByMariya.Infrastructure/Integrations/SupabaseAuthService.cs`
9. `tests/CustomerAuthenticationIntegrationChecks/CustomerIntegrationProfileChecks.cs` (new)
10. `tests/CustomerAuthenticationIntegrationChecks/CustomerMvcChecks.cs`
11. `tests/CustomerAuthenticationIntegrationChecks/FakeCustomerSupabase.cs`
12. `tests/CustomerAuthenticationIntegrationChecks/Program.cs`
13. `tests/CustomerAuthenticationIntegrationChecks/Run-Disposable.ps1`
14. `tests/CustomerAuthenticationIntegrationChecks/README.md`
15. `database/v2/CUSTOMER_AUTHENTICATION_INTEGRATION.md`
16. `database/v2/CONTROLLED_CUSTOMER_AUTH_TEST.md` (this file)

No Razor, CSS, JavaScript, application-secret configuration, migration, V1,
GitHub snapshot or deployment file was changed.
