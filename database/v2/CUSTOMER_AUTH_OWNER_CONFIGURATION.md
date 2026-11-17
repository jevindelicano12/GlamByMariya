# Owner configuration preparation — no launch / no hosted contact

Status: **V2 target, restricted backend login/password and API-key MATCH are
owner-confirmed. External certificate configuration is prepared; local TLS trust,
synthetic email, OTP settings and real MVC lifecycle remain pending. Not cleared
to launch.** Correct project reference: `hqllkbjhufhvyooacajy`.
The full `CONTROLLED_CUSTOMER_AUTH_TEST.md` was read and checked against
`CustomerAuthIntegrationProfile`, `DatabaseConfiguration`, startup and the
restricted-role readiness query. No hosted checks/writes are required again for
the completed database/key configuration.

## Prepared locally

External root: `%LOCALAPPDATA%\GlamByMariyaV2\CustomerAuthOwner`

- `config\secrets.json`: private flat environment-variable configuration, populated
  by the owner and local certificate preparation. Never display its contents.
- `keys\`: empty persistent Data Protection destination.
- `certificates\`: separate encrypted DP/HTTPS PFX files and a public HTTPS `.cer`,
  prepared locally by `tools/CustomerAuthCertificates`; no Windows trust import.
- `README.txt`: private handling instructions; no launcher.

The root has inheritance disabled and only owner + SYSTEM access; subfolders and
the template inherit that ACL. No existing owner folder was overwritten.
This root is **outside the source workspace/Git**, rather than an in-repo ignored
folder. Existing `.gitignore` additionally excludes `secrets.json` and private
certificate file extensions if accidentally copied; never rely on ignore for
secrecy or copy the material into source/publish output.

The JSON is not executable and not automatically loaded by ASP.NET Core.
Nothing has been imported into a shell, user/machine environment, launch profile
or running process. Its future secure process-scoped import/launch needs separate
authorization. Do not dot-source JSON, dump environment values, use transcripts,
or commit a filled template. No credential is requested in chat.

## Exact items, purpose and accepted format

All template values are strings; use `true`/`false` for boolean configuration.
The complete variable table remains in `CONTROLLED_CUSTOMER_AUTH_TEST.md`.

| Item | Required format / purpose |
|---|---|
| `DOTNET_ENVIRONMENT`, `ASPNETCORE_ENVIRONMENT` | Both `V2CustomerAuthTest`; prevents Production/Development activation |
| `Deployment__Profile` | Exactly `v2-customer-auth-test` |
| `Deployment__V2ProjectId`, `Database__ProjectId` | Identical NEW V2 reference matching `^[a-z]{20}$`, independently verified by owner |
| `ASPNETCORE_URLS`, `AllowedHosts` | `https://127.0.0.1:7144`, `127.0.0.1`; one loopback HTTPS URL, no path/query/fragment/credentials, port >=1024 |
| `PublicSite__InformationOnly` | `false`, staged for this isolated process only; does not change the public service |
| `Authentication__DurableSessionEnforcement`, `Authentication__RestrictLegacyFallback` | Both `true` for the isolated test |
| `Authentication__IdentityLinkingEnabled` | `false`; no automatic email-based linking |
| `Authentication__IntegrationCustomerEmail` | One trimmed valid mailbox address, <=254 characters, owner-controlled and unused for V1/existing V2 accounts |
| `Database__Provider`, `Database__Schema` | Exactly `PostgreSql`, `public` |
| `Database__Host`, `Database__Username` | Direct: `db.<v2-ref>.supabase.co` + `glam_v2_backend`; Session pooler: verified V2 `*.pooler.supabase.com` + `glam_v2_backend.<v2-ref>` |
| `Database__Port`, `Database__Name` | `5432`, `postgres`; database name is NOT the application login role |
| `Database__Password` | Nonblank actual restricted-role password, not a placeholder; owner-only secret, never output |
| `Database__RootCertificate` | Blank if system trust suffices, otherwise an absolute path to an independently verified Supabase CA file; TLS stays VerifyFull |
| `Database__MaxPoolSize` | Integer 1–6, staged as `2`; budget every process/client |
| `Studio__TimeZone` | Exactly `Asia/Manila`; guest footer clock without business settings tables |
| `Supabase__Url` | Exactly `https://<same-v2-ref>.supabase.co`, no trailing slash |
| `Supabase__AnonKey` | NEW V2 public publishable key (`sb_publishable_...`) or legacy JWT classified `role=anon`, `ref=<same-v2-ref>`; not secret/service_role |
| `DataProtection__KeyRingPath` | Absolute private external `keys` directory, not a drive root, source/wwwroot, symlink/junction or shared/synced folder |
| `DataProtection__CertificatePfxBase64`, `DataProtection__CertificatePassword` | Valid private RSA PFX encoded as base64 and its nonblank password; certificate valid now and for >7 days |
| `Kestrel__Certificates__Default__Path`, `Kestrel__Certificates__Default__Password` | Absolute external HTTPS PFX path and password for the future host; private-key access, Server Authentication usage, IP SAN `127.0.0.1`, valid dates and browser trust required |
| `Email__DeliveryDisabled`, `FileStorage__Provider` | `true`, `Local`; no legacy mail/cloud-file workflow |
| `DemoAccounts__Presentation`, `DemoGallery__Enabled`, `DemoCatalog__Enabled`, `DemoAvatar__Enabled`, `WebsiteContent__DemoTextEditing`, `WebsiteContent__DemoPersistentText` | All `false` |

Unset `ConnectionStrings__MakeupDatabase` when using the split fields (it otherwise
takes precedence), `PORT`, custom `Kestrel__Endpoints__*`, staff/service/Storage
keys, Brevo/SMTP/B2 credentials and unrelated inherited launch configuration.
Do not change the normal public service's configuration.

## V2 identity and restricted backend status

Owner confirmed the corrected V2 reference `hqllkbjhufhvyooacajy`, working
restricted `glam_v2_backend` login on the Session Pooler with verified TLS, saved
backend password and publishable-key comparison MATCH. The four project-dependent
fields were corrected earlier. These are owner-confirmed hosted facts, not a
new remote observation by this preparation. Existing appsettings, V1 material
and credential stores must not be used as an alternative.

Owner must compare the new V2 dashboard project reference with both pins, Auth
URL and reviewed Direct/Session endpoint. Publishable keys are opaque: their
project cannot be proven by decoding or prefix alone; obtain them from that same
verified project. Legacy JWT payload classification is not signature validation.
Do not request a token, DNS/HTTP probe or DB login to resolve this in preparation.

Hosted LOGIN/restricted attributes are owner-confirmed. Local application tests
previously authenticated as the restricted backend against disposable PostgreSQL,
and startup explicitly requires current role `glam_v2_backend`, LOGIN/NOINHERIT,
no elevation/membership, narrow capabilities and forced RLS. That proves the
application contract, **not current hosted password or pooler login success**.

The role-only proposal originally used `PASSWORD NULL`; the owner has since
established and authenticated the backend password. No current password/verifier
was read or retested here. The procedure below is historical/future reference,
not an outstanding action for this setup.

### Separate approval for credential establishment

If no independently known working backend credential exists, request approval
for **only setting/rotating glam_v2_backend's password on the verified NEW V2
project**, with no attribute, membership, ownership, ACL or schema changes.

The authorized V2 administrative operator must use a secure role-specific password
workflow, such as psql's interactive `\password glam_v2_backend` over verified TLS,
with echo/transcripts/recording disabled and provider logging policy reviewed.
Generate the unique secret in an approved password manager, not a saved SQL
literal or command-line argument. Do not use the dashboard's project-wide postgres
password reset as a substitute. Never copy administrator credentials into the app.
If provider restrictions prevent this, stop and ask its authorized administrator;
do not elevate the backend or grant temporary owner membership.

A later separately authorized read-only connection check must authenticate **as
glam_v2_backend**, establish its actual current/session role and unchanged
restricted attributes, and verify existing 0004 capabilities. This task authorizes
neither credential mutation nor that hosted connection.

## HTTPS and Data Protection readiness

Read-only Windows checks found no TCP listener on 7144 and no reported IPv4/IPv6
excluded TCP range containing it. No socket was bound and no connection made.
This is a point-in-time availability check, not a reservation or proof that a
future listener/TLS handshake will succeed.

The key ring directory is still empty; separate one-year RSA DP/HTTPS certificates
were generated locally into encrypted PFX files in the private external folder.
Only the four certificate configuration fields were populated. Exact preservation
of other configuration text and private-file ACLs was checked. No V1 certificate
or Windows certificate store was read/reused/modified. HTTPS has IP SAN 127.0.0.1
and server-auth usage; **browser trust/handshake are pending**, not proven merely
by creating the PFX.

Owner must preserve the prepared material and complete:

1. Privately back up the new DP PFX and its generated password using an approved
   owner recovery store. It is not a browser-trusted TLS certificate.
2. Review/approve current-user trust for only the new HTTPS public `.cer`, using
   the procedure in `CONTROLLED_CUSTOMER_AUTH_TEST.md`. No trust change has been
   executed here. A localhost-only DNS certificate is insufficient. Windows
   Schannel needs OS-backed TLS key access; the ephemeral DP loader is not a TLS
   installer. Never disable validation or silently reuse V1 material.
3. Persistent/recoverable key directory plus the DP certificate and password.
   Retain existing keys across restart; never regenerate/delete signing material
   to bypass an error. Keep backups private and the decryption material separately
   protected. No Data Protection signing keys are generated until approved startup.

## Supabase OTP — owner verification only

- Confirm the NEW V2 email/password provider/test signup availability and
  **Confirm email enabled**. Changing disabled settings requires authorization.
- Confirmation template must deliver `{{ .Token }}` as **six ASCII digits**;
  a confirmation link alone is insufficient. Confirm provider length setting.
- Record expiry: recommend 600 seconds, within the app's 1,800-second attempt
  lifetime; account for delivery delay. Never assume the provider default.
- Confirm the one allowed mailbox can receive V2 delivery and rate limits allow
  one signup/one planned resend; app minimum resend interval is 60 seconds.
  Respect stricter provider limits; no actual email/resend test is authorized yet.
- Exact isolated HTTPS origin only if URL settings are needed; no wildcard,
  production or V1 callback. CAPTCHA/additional factors unsupported by the forms
  block the test; do not weaken them without approval.
- Do not add/change Brevo/SMTP here. Recovery/password updates are outside the
  basic test authorization unless explicitly included later.

The email field can be filled offline. An allowlist entry creates **no** hosted
account or database row and does not import/activate configuration.

## Checks and next action

Only source/template consistency, ACL/empty-folder metadata, JSON structure,
missing-value status and local listener/excluded-port checks are performed in
this phase. The prior `--profile` test mode was intentionally **not run**: it
calls `SurfaceSmoke` and starts a local HTTPS MVC host.

Earlier 37-field template checks passed. Current certificate utility self-tests
cover PFX/RSA/SAN/EKU, exact-field preservation, atomic config commit, ACL retention
and refusal of stale/missing/ambiguous input. External preparation completed with
private ACLs retained. No environment was imported and no real-provider host
started. Do not rerun the prior `--profile` suite merely to check configuration:
it intentionally starts its own synthetic HTTPS MVC host.

**Single next owner action:** review and approve current-user trust for the newly
generated V2-only HTTPS public certificate, following the controlled test runbook.
After that, the synthetic email/OTP settings and explicit hosted-test approval
remain necessary; they do not require reopening the completed database work.
