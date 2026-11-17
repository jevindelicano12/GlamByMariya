# Phase 2.17G runner (implemented, execution disarmed)

This runner builds and performs offline safeguard preflight only. It does not read application configuration, user secrets, environment variables, or a connection string. It has no default endpoint. `--preflight` makes no network calls. `--execute` is an explicit opt-in but currently always fails at the source-level `ExecutionArmed = false` gate, before password input or any provider use.

## Why execution remains disabled

A host name, private address, dedicated database name, user name, or database marker supplied alongside a connection string is not independently verifiable proof that the server is disposable. The current repository has no independently pinned isolation authority, endpoint allow-list, or signed attestation from a separately provisioned environment. Accordingly, no combination of command-line options can arm this runner. The current private-IP/port/name validations are additional rejection checks, not an isolation proof. An authorized follow-up must establish and independently review the isolated endpoint and its trust/attestation mechanism, then separately review any change to the closed gate.

The runner rejects port 5432 (ordinary local PostgreSQL), port 56432 (paused cluster), all ports other than the planned dedicated 56433, loopback/unspecified addresses, DNS names, Supabase/Render host patterns, and database/user names other than the dedicated synthetic identifiers. Explicit settings are parsed only in execution mode; no app/environment fallback exists. Password is intended for stdin only, never a command-line argument or log, and the gate currently closes before it is read.

## Runtime suite plan

`RuntimeIntegrationSuite.cs` contains a staged future suite. It bootstraps a hand-built synthetic subset, applies the checked-in `AuthIdentityTracking.sql` and `AdminInvitationProvisioning.sql` payloads, then invokes the actual `PublicStudioStore.CreatePendingInvitedAdmin` and `ConfirmAdminProvisioning` methods. The fixture now seeds existing identities before installing its fixture-only account-sync trigger, matching the relevant production initialization order and avoiding duplicate identity inserts.

This is **SQL-payload plus store-method coverage**, not a full EF migration-chain test: it does not execute the `PublicIntegration` or `RolesAndInvitations` migration classes, inspect EF migration history, reproduce the full public schema, or test production RLS/policies, grants, audit/touch triggers, or deployment permissions. Passing these tests would not establish those properties. The runtime suite is not entered while the gate is closed and has not been run against PostgreSQL.

Historical assertions snapshot synthetic Customer/Admin/SuperAdmin account and identity fields before the identity/provisioning SQL and compare IDs/app IDs, hashes, roles, active state, email, and identity relationships afterward. Rejection paths compare full account/identity snapshots. Runtime plans also cover a consumed invitation belonging to a different synthetic account, FK/unique constraints, duplicate provider UUIDs, exact/conflicting repeats, forced rollback, and two-session races. These remain unverified until a separately authorized isolated run.

No runtime DB test execution has been performed. This project is not permission to apply migrations or use the prepared fixture. Setup commits fixture and payload steps separately; an interrupted run may leave a partially initialized target. There is deliberately no automatic cleanup. Only an independently verified disposable instance may be discarded, by its operator; never run cleanup against an arbitrary endpoint. A separately authorized test operator must first create a new ephemeral Linux VM (or equivalently audited isolated environment) with private non-routed networking, no cloud/application credentials, a dedicated instance, database, and role, and provide a reviewed independent isolation authority. Do not use the Windows service, the paused cluster, Supabase, or Render.

The runner project pins `Microsoft.EntityFrameworkCore.Relational` 10.0.13 for test-project-only alignment with the application model. This version was present in the local NuGet cache at preparation time. No application dependency version is changed. A future offline build must use `--no-restore`; resolve any remaining package graph conflict before interpreting runtime results.

## Offline checks

Run `dotnet run --project tests/AdminProvisioningPostgresRunner/AdminProvisioningPostgresRunner.csproj -- --preflight` for local parsing/safeguard tests. This mode does not use a database driver. Unknown/missing arguments, forbidden ports/hosts, DNS names, mismatched database/user, and missing isolation receipt are rejected. Passing preflight is not proof of database isolation and does not arm execution.
