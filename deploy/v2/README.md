# V2 Render and Brevo preparation — NOT deployed

## Scope and readiness

Only the **information-only public site** is prepared for a future new Render
service. It uses the existing pink views and public routes, without database,
Auth, Storage, email, account, booking, payment or administrator services.
Services/prices, gallery and contact content may still be empty or placeholders:
the owner must approve the public content before launch. This is **not** a
functional booking-system deployment.

Owner now confirms applied 0001/0002/0004, working restricted Session Pooler login,
saved credentials and API-key MATCH for `hqllkbjhufhvyooacajy`. Do not repeat them.
Those successes do not make the full booking host deployable: normal MVC
compatibility views/settings, real-provider lifecycle and booking/payment/private
proof workflows are not approved. Local HTTPS/DP certificate files have been
prepared externally; the owner confirms local HTTPS trust. Real-Customer testing
is blocked by the recorded Npgsql opening timeout, not by information-only mode.
Do not apply 0003, execute legacy database setup commands, enable durable
authentication, or connect test-only SQL to the runtime to make startup pass.

## Prepared files

- `deploy/v2/render.yaml`: draft new service `glam-by-mariya-v2-public`, Docker
  context at the repository root, no migration/seed hooks, no shared environment
  groups or integration credentials. Auto-deploys and preview environments are
  disabled. Singapore/free is a draft choice for owner review; free instances
  can sleep and have ephemeral filesystems.
- `deploy/v2/future-integrations.env.template`: reference-only placeholders for
  later approved integration. **Do not import it into the public service.**
- The V2 public deployment profile refuses startup if information-only mode is
  removed, email is enabled, durable authentication is enabled, or provider
  configuration is attached. Removing the deployment profile is not an approved
  route to enable the booking host.

**Creating/syncing a Render Blueprint can perform its first deployment even
when `autoDeployTrigger` is off. Do not create/sync it during preparation.**
Never point this Blueprint at a V1 service: Render can update an existing service
whose name matches. Check that the chosen name is unused before future creation.

## Source isolation and secrets

The active workspace is not a Git repository. The owner reports that GitHub
already contains an earlier clean V2 snapshot; this task has not inspected or
updated that remote. No remote, branch, service or account was created here.
Before any future push:

1. Owner confirms the existing **private V2-only repository**, its URL and a
   reviewed release commit containing the current candidate. Do not create another
   repository or assume the earlier snapshot contains these changes.
2. Review a clean V2 source export; do not clone V1 deployment configuration or
   copy its working directory, credentials, user-secrets, environment groups,
   certificates with private keys, DB dumps, uploads or local test clusters.
   The public Supabase CA certificate is not a private project credential.
3. Exclude `temp`, `tmp`, `App_Data` (except the public CA), `.env*`, private keys,
   bin/obj, local tooling and generated output. Inspect the proposed source
   manifest and run the owner's approved secret scanner before commit/push.
   Ignore rules are not proof that a source tree contains no secrets.
4. Future Render configuration must inherit only this **new** repository.
   Leave repository root directory blank; use the draft Blueprint path
   `deploy/v2/render.yaml`. No V1 repository URL is embedded in it.

The Dockerfile copies Backend and Frontend only; `.dockerignore` additionally
excludes nested build outputs, private App_Data, local configurations, secrets,
deployment templates and local fixture clusters. Credentials are runtime-only,
never Docker build arguments or committed files.

## Environment-variable names

### Public-only Render service (no provider credentials)

- `ASPNETCORE_ENVIRONMENT`
- `Deployment__Profile`
- `PublicSite__InformationOnly`
- `Authentication__DurableSessionEnforcement`
- `Email__DeliveryDisabled`
- `AllowedHosts`
- `PORT` (Render supplies it; do not set a conflicting URL/port)

Set AllowedHosts to the exact **new** service hostname (no URL scheme/path/port).
Add custom hostnames with semicolons only after separately approving them.
Do not use V1's hostname or attach a shared environment group.

### Future integration reference — not required or allowed on the public service

| Purpose | Names |
| --- | --- |
| PostgreSQL credential, choose one method | `ConnectionStrings__MakeupDatabase` **or** `Database__Host`, `Database__Port`, `Database__Name`, `Database__Username`, `Database__Password` |
| PostgreSQL safety/settings | `Database__Provider`, `Database__Schema`, `Database__ProjectId`, `Database__RootCertificate`, `Database__MaxPoolSize` |
| Supabase Auth | `Supabase__Url`, `Supabase__AnonKey` |
| Private Storage | `FileStorage__Provider`, `SupabaseStorage__ServiceKey` |
| New Brevo HTTPS sender | `Email__BrevoApiKey`, `Email__From`, `Email__DeliveryDisabled` |
| Future production cookie keys | `DataProtection__CertificatePfxBase64`, `DataProtection__CertificatePassword` |
| Approved proxy trust | `ForwardedHeaders__KnownProxies` |

`DATABASE_URL`, `BREVO_API_KEY` and `SUPABASE_URL` are not aliases in this
application; use the exact .NET double-underscore names above.
There is no configurable bucket name in the current adapter: it uses private
`app-files`. This is not permission to create it or upload any real object.
Auth uses the anon key, **not** the Storage/server key. Future credentials must
come from V2 only. PostgreSQL target checks require the V2 project reference and
enforce TLS VerifyFull; validate the public CA and runtime role before use.

## Brevo preparation

The existing adapter posts plain-text messages to Brevo's HTTPS transactional
API, using an `api-key` header and the configured sender. Display name is
Glam by Mariya. No Brevo template ID, SMTP credentials or API endpoint override
is required. The adapter uses a bounded timeout and excludes response bodies,
API keys and recipient details from its failure messages/logs.

`Email__DeliveryDisabled=true` prevents direct adapter sends and workflow sends,
including development SMTP/outbox fallback. It defaults to the legacy behavior
when omitted, so retain it explicitly in every preparation environment.
The public host does not register any email service at all.

Later owner actions, only after explicit approval:

1. Create a separate Brevo account/workspace/configuration for V2 and obtain
   transactional-email access. Generate a **new dedicated key**, not a V1 key.
2. Choose a V2 sender and an owner-controlled new domain/subdomain. Follow
   Brevo's current domain-authentication instructions (including DKIM and
   DMARC, and SPF only as directed). Do not replace V1 DNS or sender settings.
   Sender verification may itself send mail; defer it until sends are approved.
3. Store the key only in the new environment's secret manager; never this
   template or chat. Keep delivery disabled and do not attach the key to the
   public-only Render service.
4. After integration gates pass, separately approve one controlled recipient
   and test message, then check acceptance, delivery, spam and bounce events.
   An API success means acceptance, not delivery. A timeout may have delivered;
   do not automatically retry and duplicate a message.

Application Brevo settings do not configure Supabase Auth's email provider.
Any later GoTrue SMTP/email configuration is a separate V2 provider decision;
do not modify it or V1 SMTP as part of this preparation.

## Local verification (no hosted connections or real sends)

From the V2 workspace root:

```powershell
dotnet publish Frontend/GlamByMariya.Web/GlamByMariya.Web.csproj -c Release -o temp/v2-render-preparation-publish
dotnet run --project tests/V2DeploymentChecks/V2DeploymentChecks.csproj -- temp/v2-render-preparation-publish
dotnet run --project tests/EmailSenderChecks/EmailSenderChecks.csproj
```

The deployment tests launch only their own published child processes with a
cleared environment, Production mode, synthetic values and no provider secrets.
They verify PORT binding, the public routes/assets, private-route and mutation
denials, no session cookie and fail-closed startup. Email tests use fake HTTP.
Existing local Demo processes are not stopped or reconfigured.

When a local Linux Docker engine is available, these commands build/run a
local candidate only (not Render). No credentials are needed:

```powershell
docker build --pull -t glam-by-mariya-v2-public:local .
docker run --rm --name glam-v2-public-check -p 127.0.0.1:18080:8080 -e PORT=8080 -e ASPNETCORE_ENVIRONMENT=Production -e Deployment__Profile=v2-public-only -e PublicSite__InformationOnly=true -e Authentication__DurableSessionEnforcement=false -e Email__DeliveryDisabled=true -e AllowedHosts=127.0.0.1 glam-by-mariya-v2-public:local
```

Check `/healthz`, the public pages and the denied routes below from another
terminal. Stop only `glam-v2-public-check` when finished. A successful local
`dotnet publish` does not prove the Linux image/native dependencies work.

## Future deployment verification — after owner approval only

### Current concrete blockers and minimum deployment sequence

**Information-only Render:** source/published-host checks are local evidence,
not a deploy approval. Still needed: current reviewed V2 source/secret-scan
manifest and release commit (GitHub is an older snapshot), a Linux Docker
build/runtime smoke, approved public content, unused V2 service name/plan/region,
and exact service `AllowedHosts`. No new database work is needed for this mode.

**Customer authentication on Render:** BLOCKED. Do not copy the loopback profile
to Render: it intentionally rejects Production/Render PORT/non-loopback requests.
The normal durable-auth gate stays closed. The Free information-only plan has
ephemeral storage and cannot persist a local signing ring reliably. A future
auth deployment needs separately approved persistent encrypted keys, trusted
Render proxy/TLS-offload validation and a public authentication surface, after
the real local Customer lifecycle passes. Do not remove these guards just to boot.

After separate push/deploy approval, the minimal **information-only** sequence is:

1. Review the current V2-only source manifest and secret scan; exclude all owner
   configuration, PFX/key material and `temp`. Approve/update the reviewed V2
   release commit; do not use an unverified older snapshot or touch V1.
2. Complete the local Linux Docker build/smoke from the approved source. Windows
   publish checks alone do not prove the Linux image, package installation or
   native runtime dependencies. No Docker pull/build was performed in this task.
3. Review `deploy/v2/render.yaml`: repository-root context, Dockerfile, new unused
   service, no pre-deploy hooks/migrations, previews/auto-deploy off. Blueprint
   creation/sync may itself trigger a deployment; approve that separately.
4. Set only Production, `v2-public-only`, information-only=true,
   durable-auth=false, email-disabled=true and exact new AllowedHosts from the
   Blueprint. Render supplies PORT. Never import the local owner JSON, future
   integration template, certificates, database/API/Storage/Brevo credentials or
   shared environment groups. Render edge TLS needs none of the local PFX files.
5. Only after deploy approval, create/deploy the new service and verify health,
   public pages/assets, private route/POST denial and no cookies/provider calls.
   Keep auto-deploys off and record the release/environment for V2-only rollback.

The preparation code now rejects staff Supabase and B2 keys and private signing
directory settings on the information-only profile. Deployment tests also fetch
every asset referenced by the actual `_PublicOnlyLayout`; the full authenticated
layout's scripts/styles are not a reason to broaden the public allowlist.

Earlier preparation results (November 16, 2026): Release and framework-dependent
`linux-x64` publish succeeded; 33 local published-host deployment checks passed.
The Brevo adapter/workflow suite passed using fake HTTP only, including the
delivery-disable and missing-configuration guards. Linux Docker runtime testing
is blocked because the installed Docker client cannot reach a Linux engine.
Existing compiler/EF version-resolution warnings were observed in the email test
build; no dependency changes were made in this deployment-preparation task.
The Blueprint fields were checked against Render's published specification,
not validated by an authenticated Render API or service creation.

Latest local readiness run: Release publish to
`temp/v2-render-readiness-20261116-1` succeeded and **44** published-host checks
passed (clean child environments, no provider credentials). All actual public
layout assets, mutation/private-route denial, credential rejection and rejection
of the loopback Customer profile on Render were verified. The certificate utility
self-tests and local external preparation also passed, without provider access,
Windows trust import or starting the Customer host. Existing infrastructure
compiler warnings remain; no dependency upgrade or database retest was needed.

1. Confirm the source commit, unused V2 service name, new repo and service-local
   environment. No V1 domain, repo, resource or environment group may be linked.
2. Keep the Docker start command at its default entrypoint. Do not configure
   pre-deploy commands or database/storage setup. Render TLS terminates at its
   proxy; PORT must bind on `0.0.0.0`. The application does this already.
3. Verify `GET /healthz` is HTTP 200 with `service=glam-by-mariya-v2` and
   `mode=information-only`. This is a liveness check, **not** DB/Auth readiness.
4. Review `/`, `/Home/About`, `/Home/Services`, `/Home/Gallery`, `/Home/Contact`
   on desktop/mobile. Confirm approved pink assets, owner-approved content and
   no broken/incorrect V1 links. Do not present this as a booking launch.
5. Confirm account, booking, administrator and private-file routes return 404;
   all POST requests must return 404. Confirm no auth cookie and no emails,
   database connections, provider requests or private uploads occur.
6. Inspect safe logs and security headers. Verify host filtering and HTTPS.
   Keep default proxy trust; do not clear trust lists or blindly enable all
   forwarded headers. Establish Render's trusted proxy addresses before adding
   `ForwardedHeaders__KnownProxies`, then test spoofed forwarding separately.

## Rollback (new V2 resource only)

- Record the last verified **V2 public-only** source commit and environment.
- If a later release is bad, keep auto-deploys off and roll back the new V2
  service to that known-good release. Recheck mode and blocked routes.
- A code rollback does not restore environment values: explicitly restore the
  public-only profile, disabled email and credential-free configuration.
- Before the first good release exists, suspend the **new V2 service** rather
  than routing traffic to an unverified booking host.
- Do not alter V1, its DNS, databases, keys or live service as a fallback. No
  schema rollback is needed: this template performs no database migration.

## Exact next owner action

Confirm the **existing private V2 repository**, unused Render service name,
region/plan, exact hostname and information-only content. Do not create another
repository or create/sync the Blueprint yet. Arrange a local Linux Docker build
and approved clean-source secret review; push/service creation/initial deployment
still need separate explicit authorization.

November 17 review: current source published successfully to
`temp/v2-render-review-20261117-1`; **51** isolated information-only deployment
checks passed. A concrete release-guard gap was fixed: missing/blank/wildcard or
malformed `AllowedHosts` now fail startup, and an unapproved Host header is tested
as HTTP 400. Existing pink assets and private-route/POST denials remain unchanged.
Six existing infrastructure compiler warnings were observed, with no build errors.
The installed Docker client still cannot reach its local Linux engine; no image
pull/build was attempted. No source/build failure remains in the Windows
information-only checks, but Linux-container behavior is not thereby proven.

This release path does **not** need the Customer DB connection to be repaired.
Keep its configuration credential-free and public authentication/booking/payment
closed. The separately prepared connectivity review and sole next approved test
are in `database/v2/NPGSQL_TIMEOUT_ROOT_CAUSE_REVIEW_2026-11-17.md`; do not import
that private diagnostic configuration into Render.

Latest supervision-fix preparation: a fresh offline Release publish and **52**
information-only checks passed, including approved/unapproved Host headers,
account/booking/private-route denial and mutation denial. Filename-category
screening of the 154-file publish output found no owner-config, private-key or
diagnostic-tool categories. This is **not** a full secret scan or source-export
approval. The local Linux Docker engine remains unavailable; no registry access,
Docker build, GitHub push or Render operation occurred.

Remaining release gates: restore local Linux Docker availability and complete the
approved build/runtime smoke; review the clean V2 source manifest/full secret
scan/release commit; approve public content, unused V2 service and exact
`AllowedHosts`; then obtain separate push/deploy authorization. Authentication,
booking and payment stay closed. Information-only release can proceed independently
of the separately gated database diagnostic.

Reference: [Render Blueprint specification](https://render.com/docs/blueprint-spec)
(reviewed for Docker paths, PORT health checks and `autoDeployTrigger` semantics).
