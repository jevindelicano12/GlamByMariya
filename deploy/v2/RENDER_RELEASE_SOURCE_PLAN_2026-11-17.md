# V2 information-only Render release-source plan

Status: **prepared for owner decisions, not committed/pushed/built/deployed**.
No application source, Dockerfile, ignore file, deployment configuration, private
configuration, credentials or hosted resources were modified by this preparation.

## Exact build scope and candidate identity

The existing Dockerfile builds only these projects:

- `Frontend/GlamByMariya.Web/GlamByMariya.Web.csproj`
- `Backend/GlamByMariya.Infrastructure/GlamByMariya.Infrastructure.csproj`
- `Backend/GlamByMariya.Core/GlamByMariya.Core.csproj`

Retain their C# source, Razor views, appsettings.json, public wwwroot assets,
vendored libraries/licenses and project-required authored SQL inputs under
`Frontend/` and `Backend/`. All three project references exist.
`MakeupBookingDatabase.sql` and `Backend/.../Data/Sql/*.sql` are explicit project
content inputs; they were classified/screened as authored SQL, not database dump
exports. They are not executed by the information-only host or Dockerfile.

Release controls: `Dockerfile`, `.dockerignore`, `.gitignore`,
`deploy/v2/render.yaml`. Root solution/wrapper projects, root migration scripts,
`database/`, `tests/`, `tools/`, local diagnostics, staging images, temp files,
build caches, user-secrets and owner configuration are not release build inputs.
Do not copy the whole working directory or rely solely on ignore rules.

Selection excludes bin/obj, App_Data, .git, node_modules, temp/tmp,
environment-specific appsettings, .env files, secrets/credentials JSON,
private-key/certificate/container files, database backups/dumps and local DB files.
Reparse paths are rejected. Certificates are excluded as requested, including the
otherwise-public CA; the resulting build blocker is recorded below.

The candidate has **236 files**. This is a different selection from the original
236-file inventory: `.gitignore` was added and the public CA was omitted.

- Original inventory SHA-256:
  `469FE09CD7E0B61CCB1647002582DDE900F00B6C5288F891E72CDE8CD6F6458D`
- Current candidate SHA-256:
  `030CE31D1D8D0A1BA11519A9D507C81DA68D2F3CCBC3858A4AE121077F27039D`

Fingerprints hash sorted relative-path/SHA-256 records joined with LF. Per-file
records were computed in memory; no source export or persisted per-file manifest
was created. Before commit, materialize/review that exact file list and record its
hash, source revision, build recipe, resolved dependencies/base-image digests and
resulting cloud image/deployment identity. The old staging image has no established
correspondence to this source and must not stand in for a fresh source build.

## Local secret screening

All 236 selected files (2,023,517 bytes) were screened; no selected files were
skipped. Thirteen rule groups checked private keys, JWTs, GitHub/Brevo/AWS/Google/
payment/Slack/SendGrid credential patterns, credential URIs, JSON/XML secrets
and bcrypt hashes. UTF-8 and relevant UTF-16 views were checked. Results:

- Known-secret rule findings: **0**
- Dump-marker files: **0**
- Certificate files selected: **0**
- Secret-named assignment flags: **2**, both manually reviewed as public rate-limit
  policy identifiers (`ForgotPassword` and `ResetPassword`), not credentials
- Supplementary short secret-named string assignments: **0**

Gitleaks and TruffleHog are not installed; neither was downloaded. This completed
local pattern screening is **not a guarantee** against unknown/obfuscated secrets,
all possible dumps, or private data in image metadata. No Git history exists
locally to screen. JPG portfolio/inspiration assets require owner content/privacy/
licensing approval; their README identifies them as stock inspiration, not completed
customer appointments. Text README/ignore files were also pattern-screened.
Owner must approve the screening method or separately authorize another local
scanner before treating this as final publication clearance.

## Concrete packaging decision: public CA

`Dockerfile:27` explicitly copies:
`Frontend/GlamByMariya.Web/App_Data/Certificates/supabase-prod-ca-2021.crt`.
The selected package excludes it under the instruction to exclude certificates.
Building the unchanged Dockerfile from this selection would therefore fail.

Owner must approve **either** retaining this one public, non-secret CA as a narrow
exception **or** a separately reviewed release-packaging change that removes the
information-only image's unused CA copy. Do not include private certificates/PFX/
keys, weaken database TLS, change authentication or silently modify the Dockerfile.
No such change/exception was performed here.

## Render cloud-build configuration

The three projects target net10.0. The existing multi-stage SDK/ASP.NET 10 Dockerfile
is structurally consistent with their references and publish paths. Cloud build
requires external access, after authorization, to:

- Dockerfile frontend `docker/dockerfile:1`
- `mcr.microsoft.com/dotnet/sdk:10.0` and `.../aspnet:10.0`
- NuGet (AWSSDK.S3, Magick.NET, EF Core/Data Protection/Design, Npgsql provider and
  transitive dependencies)
- The base distribution's APT repositories for `libgssapi-krb5-2` and dependencies

No separate Node/npm build is specified; browser libraries are vendored.
Mutable base/frontend tags and missing packages.lock.json mean byte-for-byte
dependency reproducibility is not asserted. They do not by themselves prevent a
controlled cloud build; record the resolved inputs in release provenance.
No cloud build or network request was made in this preparation.

`Program.cs` binds Render PORT on `0.0.0.0`, validates configuration, and returns
into `PublicOnlySite` before database/provider/Customer authentication wiring.
The public host registers MVC, an emptied in-memory store and catalog image
support, not functional booking/payment/email/provider services. Only allowlisted
GET/HEAD pages/assets are accessible; private routes and mutation methods return
404. Existing pink views and business rules remain unchanged.

Health contract remains:
HTTP 200 with `service=glam-by-mariya-v2`, `mode=information-only`.
AllowedHosts still rejects absent/blank/wildcard/malformed values. Keep Production,
`v2-public-only`, information-only=true, durable-auth=false and email-disabled=true.
No provider/database/Brevo/private signing configuration belongs on this service.
Render supplies PORT; `/healthz` is public liveness, not DB/Auth readiness.

## GitHub and owner decisions

The active workspace has **no .git metadata**. The owner reports an older private
V2 GitHub snapshot, but its URL/default branch/release branch are not available in
the inspected local evidence. No remote access, Git init, branch, commit or push
was performed. Do not invent a URL, clone V1, or create another repository.

Owner decisions needed:

1. Public-CA exception versus release-packaging change.
2. Exact existing private V2 repository URL and approved target/release branch.
3. Approval of final source manifest and secret-screening clearance.
4. Review draft service `glam-by-mariya-v2-public`, Singapore/free and unused name;
   confirm the actual allocated Render hostname for AllowedHosts. The draft name
   is not proof of a reserved hostname. Approve any custom domain separately.
5. Approve the public pages, services/prices or intentional empty states, gallery/
   stock-photo representations, contact details and information-only messaging.
   Do not advertise working account/booking/payment functionality.

After these decisions, prepare the clean release export and reviewed commit.
**GitHub push, Render service/Blueprint creation and cloud build/deployment each
remain separately authorization-gated**; creating a Blueprint may trigger a build.
Keep previews/auto-deploy off and never attach V1 resources/environment groups.
After an approved build/deploy, verify health identity, pages/assets, private-route/
POST denials and sanitized logs on that exact release.

## Scope boundary

Information-only release preparation can continue without fixing Npgsql.
Functional Customer/booking release remains blocked by the database timeout,
uncompleted controlled authentication lifecycle and other private workflow gates.
No database diagnostics, Customer registration/OTP/login, emails, migrations,
PayMongo, travel-fee automation, GitHub or Render operations were performed here.
