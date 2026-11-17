# Local release evidence — November 17, 2026

## One authorized offline container attempt

Image tag: `glam-info-staging:buildkit-offline-c942a5e8`

Exact local image ID:
`sha256:0ebffc3bf4273308924d293c0b393fe99b4b2d5202c29355fdb57f827c232f90`

- Linux/amd64 and the expected entrypoint were verified before launch.
- Exactly one temporary container was started in this task, with `--pull=never`,
  `--rm`, `--network=none`, no mounts or injected credentials, Production,
  `v2-public-only`, information-only enabled, durable sessions disabled,
  email delivery disabled and `AllowedHosts=127.0.0.1`.
- The saved isolation checker failed before HTTP execution. Therefore health,
  public/static routes and private GET/POST denials were **not tested in Linux**.
  Docker process startup is not proof of successful HTTP application startup.
- Available logs were screened without exporting raw contents. No exception,
  credential/token/private-key/email or provider-activity patterns were detected.
  This bounded pattern scan is not proof against every possible log exposure.
- The temporary container was stopped and its removal verified. No restart or
  additional container was attempted. Existing images and unrelated containers
  were not modified. No build, pull, external connection or hosted operation ran.

## Demonstrated local checker defect and fix

The prior 37-check suite did not include Docker's root-level `Name` field.
PowerShell's XML adapter resolves `$container.Name` to that JSON child rather
than to `XmlElement.Name`, causing a false rejection of otherwise valid input.
A local synthetic reproduction accepted the minimal fixture and rejected the
same fixture with Docker's `Name` field.

`tools/Test-DockerSmokeIsolation.ps1` now calls `$container.get_Name()` directly.
`tests/DockerSmokeIsolationChecks.ps1` preserves all original cases and adds four
regressions: valid Docker Name, realistic metadata, and actual/null mounts with
Docker Name. **All 41 offline checks passed.** Restrictions on real mounts,
null/missing/ambiguous mount data, image identity, running state, network mode
and auto-removal were not relaxed.

No application, authentication, database, Dockerfile, ignore-rule or Render
configuration change was made. The fix was not followed by another container run.

## Release-source preparation

A read-only candidate inventory selected Backend/Frontend source, Dockerfile,
`.dockerignore`, `deploy/v2/render.yaml` and the sole public CA exception.
Build outputs, private App_Data, local/private configuration and key-file
categories were excluded. Reparse paths were not followed.

- Candidate files: **236**
- Text files screened: **230**
- Oversized text files skipped: **0**
- Reparse paths encountered: **0**
- Selected credential-pattern findings: **0**
- Forbidden private-file categories in the candidate: **0**

Rules screened for private-key markers, JWT literals, GitHub/Brevo/AWS credential
patterns, credential-bearing PostgreSQL URIs and nonempty JSON secret fields.
No matched values, private owner configuration or raw logs were exported.
Binary-file contents were not secret-screened. No clean export or release commit
was created; this is not full secret clearance or proof that the whole workspace
is safe to publish.

In-memory inventory fingerprint (sorted relative paths and SHA-256 file hashes):
`469FE09CD7E0B61CCB1647002582DDE900F00B6C5288F891E72CDE8CD6F6458D`

## Remaining gates / shortest next step

1. Separately authorize one corrected offline container HTTP smoke; the completed
   attempt was not retried. Use the saved checker and freshly verify the exact image.
2. Establish approved image/current-source provenance or separately authorize the
   required current-source build. The tested image is **not claimed to match**
   the latest V2 source.
3. Review the clean source/export manifest, full secret scan and release commit;
   approve public content, unused V2 service, hostname/AllowedHosts and release
   settings. GitHub push and Render creation/deployment remain separately gated.
4. Functional Customer release still requires the separately approved bounded
   database diagnostic and successful controlled authentication lifecycle.
   No database diagnostic, registration, OTP, email or migration ran here.

The information-only release must keep authentication, booking and payment
disabled. Existing Windows publish/52-check evidence and the prior diagnostic
supervision work are preserved; they were not repeated in this task.
