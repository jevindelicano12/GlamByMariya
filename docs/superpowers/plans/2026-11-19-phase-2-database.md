# Phase 2 Database Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Run the app on Supabase PostgreSQL from one ordered SQL migration set, with the Phase 1 booking rules enforced in the database, tested first in a local Docker PostgreSQL.

**Architecture:** SQL files in `database/migrations/NNNN_name.sql` are applied in order by a small `SqlMigrator` (Infrastructure) that records each file in `public.schema_migrations`. The same migrator is used by the app's `--database-migrate` command and by the database test projects, which run against a throwaway `postgres:17` container started by `tools/test-db.sh`. EF migrations in `Migrations/Public` are retired.

**Tech Stack:** .NET 10, Npgsql, PostgreSQL 17 (Supabase), Docker Desktop, Git Bash.

**Spec:** `docs/superpowers/specs/2026-11-19-phase-2-database-design.md`

## Global Constraints

- One branch per task, chained: each task branches from the previous task's branch. First branch starts from `phase-2/step-0-design`.
- Run the task's tests and the full local suite (`run-all-checks.sh`, see Task 1) before each commit. Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Push only when the owner says so.
- Never connect to Supabase project `glam-by-mariya-v2` before Task 5, and in Task 5 only after the owner confirms the reset in chat.
- No seeded accounts or bookings in any migration. No grants to `anon` or `authenticated`.
- Migration files contain no `BEGIN`/`COMMIT`; the migrator owns the transaction.
- Payment window 30 minutes; cancellation fee PHP 200 for Confirmed, allowed only while `start - now >= 24 hours`; Pending Payment cancels free; down payment must equal `required_down_payment_snapshot`.
- The only makeup artist is the owner; `--database-create-superadmin` creates that artist row.

## Review Focus

1. A new booking on a slot held by an unpaid booking whose 30 minutes ran out but which the job has not marked yet must succeed (the slot check expires overdue rows first). Test in Task 3.
2. A migration file checked out with CRLF (Windows) and LF (Render/Linux) must give the same checksum, so a deploy never refuses an unchanged file. Test in Task 1.
3. Running `--database-migrate` twice must be a no-op the second time, not an error. Test in Task 1.
4. Two customers booking the same slot at the same moment: exactly one booking exists afterwards and the other customer gets the normal "This slot overlaps…" message, not a raw PostgreSQL error. Test in Task 3.
5. Cancelling a Confirmed booking exactly 24 hours before start is allowed with fee 200; at 23 h 59 min it is refused, in the database as in `CancellationPolicy`. Test in Task 3.

---

### Task 1: SQL migrator, Docker test database, base tables

Branch: `phase-2/step-1-migrator` (from `phase-2/step-0-design`).

**Files:**
- Create: `Backend/GlamByMariya.Infrastructure/Data/SqlMigrator.cs`
- Create: `database/migrations/0001_base.sql`
- Create: `tools/test-db.sh`
- Create: `tests/DatabaseChecks/DatabaseChecks.csproj`, `tests/DatabaseChecks/Program.cs`
- Modify: `MakeupSystem.slnx` (add the test project)

**Interfaces:**
- Produces: `public static class SqlMigrator { public static Task<IReadOnlyList<string>> ApplyAsync(NpgsqlDataSource source, string directory, CancellationToken ct = default); public static string Checksum(string fileText); }` — returns the file names applied in this run.
- Produces: table `public.schema_migrations(version int PRIMARY KEY, name text NOT NULL, checksum text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now())`.
- Produces: `tools/test-db.sh <dotnet-project-path> [extra args]` — starts `postgres:17` on `127.0.0.1:<random port>` with `-c glam.payment_review_marker=<marker>`, trust auth, waits for `pg_isready`, creates an empty temp dir containing `owner-marker` with the marker, runs `dotnet run --project <path> -- <port> <marker> <tempdir> [extra args]`, then removes the container and temp dir whatever the result. This matches the argument contract `PublicStoreChecks` and `PostgresChecks` already use.
- Produces: scratch runner copied into the repo as `tools/run-all-checks.sh` (the current scratch version) plus a line that runs `tools/test-db.sh tests/DatabaseChecks`.

Migrator rules: files match `^(\d{4})_([a-z0-9_]+)\.sql$`; versions must be 1..n with no gap or duplicate; checksum = SHA-256 hex of the text with `\r\n` replaced by `\n`; a recorded version whose checksum differs, or whose file is missing, stops with an `InvalidOperationException` naming the file; each pending file runs in its own transaction together with its `schema_migrations` insert; the whole run holds `pg_advisory_lock(718430512, 2)`.

`0001_base.sql`: the legacy base tables from `tests/PublicChecks/BaselinePublic.sql` (admin_users, makeup_artists, artist_availability, blocked_slots, customers, services, reservations + its two indexes, payments, reservation_history) plus `CREATE EXTENSION IF NOT EXISTS btree_gist` and `pgcrypto`. Without the `MakeupBookingSystem` table and without any INSERT.

- [ ] **Step 1:** In `tests/DatabaseChecks/Program.cs`, section `--migrator`, write checks against a temp migrations folder:
  - `"applies 0001 and 0002 in order and records both"` — `ApplyAsync` returns `["0001_a.sql","0002_b.sql"]`; `schema_migrations` has versions 1,2.
  - `"second run applies nothing"` — returns empty list.
  - `"CRLF and LF give the same checksum"` — `Checksum("a\r\nb") == Checksum("a\nb")`.
  - `"an edited applied file is refused by name"` — change `0001_a.sql`; exception message contains `0001_a.sql`.
  - `"a gap is refused"` — folder with 0001 and 0003 only → exception.
  - `"a failing file leaves nothing behind"` — `0002` has invalid SQL; table it would create does not exist and version 2 is not recorded.
  - `"real folder builds the base tables"` — apply `database/migrations`; `to_regclass('public.reservations')` is not null.
  Use the same loopback/marker guard and empty-database check as `tests/PostgresChecks/Program.cs`; drop and recreate the `public` schema between sub-checks.
- [ ] **Step 2:** Run `tools/test-db.sh tests/DatabaseChecks --migrator`. Expected: build error (no `SqlMigrator`) or first check failing.
- [ ] **Step 3:** Implement `SqlMigrator` and `0001_base.sql`.
- [ ] **Step 4:** Run `tools/test-db.sh tests/DatabaseChecks --migrator`. Expected: `All 7 migrator checks passed.` Then `tools/run-all-checks.sh` → `OVERALL: PASS`.
- [ ] **Step 5:** Commit "Add SQL migrator and Docker test database (Phase 2, step 1)".

### Task 2: Full schema and catalog seed

Branch: `phase-2/step-2-schema` (from step 1).

**Files:**
- Create: `database/migrations/0002_public_integration.sql`, `0003_email_codes.sql`, `0004_customer_birthday_suffix.sql`, `0009_catalog_seed.sql` (numbers 0005–0008 come in Tasks 3–4; until then add them as empty placeholder files with a one-line comment so versions stay contiguous, and fill them in later tasks — the migrator only checks contiguity. Filling a placeholder changes its checksum, which is fine only because no shared database has these files yet; nothing is applied to Supabase before Task 5).
- Modify: `tests/PublicStoreChecks/Program.cs`, `tests/PostgresChecks/Program.cs` (build the database with `SqlMigrator.ApplyAsync(..., "database/migrations")` instead of `BaselinePublic.sql` + `PublicIntegration.sql`).
- Delete: `tests/PublicChecks/BaselinePublic.sql` usage (keep the file only if another test still reads it; grep first).

**Interfaces:**
- Consumes: `SqlMigrator.ApplyAsync`.
- Produces: every public table, view and function the code reads (list in the spec, "What exists today"), except the mixed-booking and website-content tables.

`0002_public_integration.sql` = concatenation, in this order, of `Data/Sql/PublicIntegration.sql`, the SQL bodies of `Migrations/Public/20261016150000_AccountPreferences.cs` and `20261016160000_ProfileAvatars.cs`, `RolesAndInvitations.sql`, `DataProtectionKeys.sql`, `DownPaymentPolicy.sql`, `TransportationFeePolicy.sql`. Remove: `SET LOCAL` lines duplicated after the first, statements that backfill or seed legacy rows (UPDATE … FROM legacy tables, INSERT of fixed category/artist rows), and anything that only matters for an existing V1 database. Keep `CREATE EXTENSION`, all DDL, functions, triggers, views, RLS policies and grants to `makeup_backend`. Leave out `AuthIdentityTracking.sql` and `AdminInvitationProvisioning.sql`.
`0003` and `0004` = `database/v2/migrations/0005_email_codes.sql` and `0006_customer_birthday_suffix.sql` without their `BEGIN;`/`COMMIT;`.
`0009_catalog_seed.sql`: categories (The everyday edit, The occasion edit, The bridal edit, Signature), services Soft Glam 600/60 min/group 525 from 4 people, Glamour Makeup 750/90, Bridal Makeup 1500/120, add-ons Lash application 250/15, Hair styling 800/45, Touch-up kit 350/0, service areas Quezon City, Makati, Manila, Pasig, Taguig, Mandaluyong, payment methods GCash/Maya/Bank Transfer (deposits) and Cash (no deposits), one `business_settings` row id 1 with Asia/Manila, 8–18, buffer 45, down payment 50. Values copied from `Data/Sql/ProductionSeed.sql` and `DemoStore`; no artist, no accounts.

- [ ] **Step 1:** Add to `tests/DatabaseChecks` section `--schema`: `"every object the app reads exists"` — for each name in a fixed list (the ~40 objects from the spec minus mixed/website-content) assert `to_regclass` or `to_regprocedure` is not null; `"seed has no people and no bookings"` — counts of customers, admin_users, makeup_artists, reservations are 0; `"seed has the catalog"` — 3 services, 3 add-ons, 6 areas, 4 payment methods, settings row 1.
- [ ] **Step 2:** Run `tools/test-db.sh tests/DatabaseChecks --schema`. Expected: FAIL on the object list.
- [ ] **Step 3:** Write the migration files; switch `PublicStoreChecks` and `PostgresChecks` to the migrator. Their checks that relied on legacy seed rows (`LEGACY-001`, "Angela Cruz", two artists) must create the rows they need in their own setup.
- [ ] **Step 4:** Run `tools/test-db.sh tests/DatabaseChecks --schema`, `tools/test-db.sh tests/PublicStoreChecks`, `tools/test-db.sh tests/PostgresChecks`, then `tools/run-all-checks.sh`. Expected: all pass.
- [ ] **Step 5:** Commit "Build the full public schema from ordered migrations (Phase 2, step 2)".

### Task 3: Phase 1 booking rules in the database

Branch: `phase-2/step-3-booking-rules` (from step 2).

**Files:**
- Fill: `database/migrations/0005_phase1_rules.sql`, `0006_occupied_intervals.sql`
- Modify: `Backend/GlamByMariya.Infrastructure/Data/IStudioStore.cs` (`IPersistentStudioStore`), `PublicStudioStore.cs`, `PostgresStudioStore.cs`, `Services/BookingService.cs`
- Test: `tests/PublicStoreChecks/Program.cs` (new section at the end, uses its existing booking setup)

**Interfaces:**
- Produces SQL: column `reservations.payment_expires_at timestamptz`; status `'Expired'` in `reservations_status_check`; `public.mvc_expire_overdue() RETURNS int`; `public.mvc_occupied_intervals(aid bigint, from_at timestamptz, to_at timestamptz, excluded uuid) RETURNS TABLE(occupied_from timestamptz, occupied_until timestamptz)` with the same signature and columns `PublicStudioStore.OccupiedIntervals` already calls (check line ~62 and match it exactly).
- Produces C#: `int ExpireOverduePayments();` on `IPersistentStudioStore`. `PublicStudioStore` runs `SELECT public.mvc_expire_overdue()`; legacy `PostgresStudioStore` returns 0. `BookingService.ExpireOverduePayments()` calls it when the store is `IPersistentStudioStore`.

`0005_phase1_rules.sql` changes:
- `mvc_travel_overlap` dropped and recreated with `WHERE (status IN ('Pending','Pending Payment','Confirmed','InProgress'))`.
- Insert trigger (the one raising 'New requests must await payment verification') sets `NEW.payment_expires_at := clock_timestamp() + interval '30 minutes'`.
- `mvc_expire_overdue()`: `UPDATE public.reservations SET status='Expired' WHERE status='Pending Payment' AND payment_expires_at <= clock_timestamp()`, returns the row count. History rows come from the existing history trigger.
- `mvc_check_slot`: `PERFORM public.mvc_expire_overdue();` first, and `'Pending Payment'` added to the overlapping-status list. Also add it to the block guard and every other status list that means "holds the slot" (grep `'Pending','Confirmed','InProgress'` across 0002).
- `mvc_reservation_guard` transitions add: `Pending Payment → Cancelled` (needs `cancellation_confirmed_at`), `Pending Payment → Expired` (only when `payment_expires_at <= clock_timestamp()`); `Confirmed → Cancelled` refused when `OLD.start_at - clock_timestamp() < interval '24 hours'` with message 'Cancellations close 24 hours before the appointment'.
- `reservation_closures` CHECK: `kind='cancellation' AND cancellation_fee IN (0,200)`. The closure trigger writes fee 200 only when the cancelled booking was Confirmed, 0 when it was Pending Payment. Expired creates no closure.
- The deposit rule in `mvc_payment_guard` already requires equality with the snapshot: keep it.

C# mapping: wherever `PublicStudioStore` inserts a reservation or updates status, an exclusion violation (`SqlState 23P01`) or the slot function's 'Artist is unavailable…' exception is rethrown as `InvalidOperationException("This slot overlaps a reserved appointment. Please choose another time.")`.

- [ ] **Step 1:** Write checks in the new `PublicStoreChecks` section:
  - `"pending payment holds the slot"` — second booking on the same start is refused with the overlap message.
  - `"an overdue unpaid booking frees its slot at once"` — set the first booking's `payment_expires_at` to `now() - 1 minute` by SQL, book the same slot again → succeeds; first booking status is `Expired`.
  - `"two customers booking at the same moment: one wins"` — two tasks with a `Barrier` call `SubmitBooking` for the same slot; exactly one returns, the other throws `InvalidOperationException` with the overlap message; one reservation row.
  - `"customer cancels unpaid booking free"` — `CancelBooking` on Pending Payment → status Cancelled, no closure row or closure fee 0.
  - `"confirmed booking 24h away cancels with 200, 23h59 is refused"` — set start by SQL to `now()+24h+1min` then cancel → fee 200; second booking at `now()+23h59m` → refused with the 24-hour message.
  - `"expiry job marks only overdue bookings"` — one overdue, one not; `ExpireOverduePayments()` returns 1.
  - `"invitation by the owner is stored"` — `PostgresInvitationService.InviteWithToken(email, owner.Id)` succeeds (proves `invited_by` FK with `app_id`).
- [ ] **Step 2:** Run `tools/test-db.sh tests/PublicStoreChecks`. Expected: the new checks fail (placeholders 0005/0006 are empty).
- [ ] **Step 3:** Write 0005, 0006 and the C# changes.
- [ ] **Step 4:** Run `tools/test-db.sh tests/PublicStoreChecks`, `tools/test-db.sh tests/DatabaseChecks --migrator --schema`, `tools/run-all-checks.sh`. Expected: all pass.
- [ ] **Step 5:** Commit "Enforce the Phase 1 booking rules in the database (Phase 2, step 3)".

### Task 4: Jobs, access, migrate command, owner and artist

Branch: `phase-2/step-4-jobs-access` (from step 3).

**Files:**
- Fill: `database/migrations/0007_jobs.sql`, `0008_access.sql`
- Modify: `Backend/GlamByMariya.Infrastructure/Data/DatabaseSetup.cs` (`--database-migrate`, `--database-create-superadmin`), `PublicStudioStore.cs` (`CreateInitialSuperAdmin`)
- Delete: `Backend/GlamByMariya.Infrastructure/Migrations/Public/` (all files) and the EF `MigrateAsync` path; keep `PublicMakeupDbContext` (it maps the views for reads).
- Test: `tests/DatabaseChecks/Program.cs` section `--access`, `tests/PublicStoreChecks` (owner/artist)

**Interfaces:**
- Consumes: `SqlMigrator.ApplyAsync`, `mvc_expire_overdue()`.
- Produces: `--database-migrate` = `SqlMigrator.ApplyAsync(dataSource, Path.Combine(AppContext.BaseDirectory, "database", "migrations"))`, printing each applied file or "Database is up to date."; the web project copies `database/migrations/*.sql` to its output (csproj `Content` with `Link="database/migrations/%(Filename)%(Extension)"`, `CopyToOutputDirectory=PreserveNewest`, `CopyToPublishDirectory=PreserveNewest`) so the Docker image has them.
- Produces: `PublicStudioStore.CreateInitialSuperAdmin(firstName, lastName, email, password)` also inserts one `makeup_artists` row (`full_name = first + ' ' + last`, `is_active = true`) in the same transaction, and refuses when an active artist exists.

`0007_jobs.sql`: if `pg_available_extensions` has `pg_cron`, `CREATE EXTENSION IF NOT EXISTS pg_cron` and schedule `glam-expire-unpaid` every minute (`SELECT public.mvc_expire_overdue()`) and `glam-clean-email-codes` daily at 03:00 UTC (delete email codes older than 1 day, using the table from 0003). Otherwise `RAISE NOTICE` and continue (local Docker has no pg_cron). Re-running must not create duplicate jobs (unschedule by name first).
`0008_access.sql`: revoke all on all tables, sequences and functions in `public` from `anon` and `authenticated` when those roles exist; revoke execute on `public.rls_auto_enable()` from them when the function exists; `ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON TABLES FROM anon, authenticated` (guarded); create login-less role `makeup_backend` if missing (already in 0002, keep idempotent). The app's own login role is created by the owner in Task 5, not in a migration (it carries a password).

- [ ] **Step 1:** Write checks: `--access` in DatabaseChecks — create roles `anon` and `authenticated` before migrating, then `"anon cannot read any public table"` (`has_table_privilege('anon', t, 'SELECT')` false for every table), `"jobs migration runs without pg_cron"`; in PublicStoreChecks — `"owner setup creates the one artist"` (after `CreateInitialSuperAdmin`, exactly one active artist named with the owner's name, and booking works without `Database:ArtistId`), `"second owner setup is refused"`; in WorkflowChecks `--accounts-only` add `"migrate command uses the SQL migrator, not EF"` (source of `DatabaseSetup.cs` contains `SqlMigrator.ApplyAsync` and not `MigrateAsync`).
- [ ] **Step 2:** Run them. Expected: FAIL.
- [ ] **Step 3:** Write 0007, 0008 and the C# changes; delete `Migrations/Public`.
- [ ] **Step 4:** Run all three database test runs and `tools/run-all-checks.sh`; also `docker build .` succeeds and the image contains `/app/database/migrations/0009_catalog_seed.sql` (`docker run --rm --entrypoint ls <image> /app/database/migrations`).
- [ ] **Step 5:** Commit "Add jobs, access rules and the SQL migrate command (Phase 2, step 4)".

### Task 5: Apply to Supabase `glam-by-mariya-v2`

Branch: `phase-2/step-5-hosted` (from step 4). Ask the owner in chat before Step 2, quoting exactly what will be deleted.

**Files:**
- Create: `database/HOSTED_SETUP.md` (the exact steps below, for repeating on a new project)

- [ ] **Step 1:** Back up through the Supabase connector: export the 1 customer row and the list of public objects to `%LOCALAPPDATA%\GlamByMariya\backup-2026-11-19.json` (outside the repo).
- [ ] **Step 2 (after the owner's yes):** Reset with the connector's `execute_sql`: drop every object in `public` (`DROP SCHEMA public CASCADE; CREATE SCHEMA public; GRANT USAGE ON SCHEMA public TO postgres, anon, authenticated, service_role;` — keep Supabase's default schema grants, revoke table access later by 0008), delete the 2 rows in `auth.users`, drop `public.rls_auto_enable` if still present.
- [ ] **Step 3:** The owner runs, in their own terminal with `Database__Password` set as a user secret (never in chat): `dotnet run --project Frontend/GlamByMariya.Web -- --database-migrate`. Expected output lists 0001–0009. Verify with the connector: `SELECT version,name FROM public.schema_migrations` → 9 rows; `get_advisors` security → no anon access findings.
- [ ] **Step 4:** Create the private bucket: `INSERT INTO storage.buckets(id,name,public) VALUES('app-files','app-files',false) ON CONFLICT DO NOTHING`.
- [ ] **Step 5:** The owner creates the app login role (`CREATE ROLE glam_app LOGIN PASSWORD '<their choice>' IN ROLE makeup_backend NOBYPASSRLS`) in the Supabase SQL editor, then runs `--database-create-superadmin` with their name, email and password from user secrets.
- [ ] **Step 6:** Smoke test locally against Supabase with `Database__Provider=PostgreSql`, `Database__Schema=public`: owner logs in, catalog shows 3 services, a customer registers (Brevo key needed; skip and note if absent), books a slot, the slot disappears for a second customer, the booking expires after 30 minutes (or call `mvc_expire_overdue()` after moving `payment_expires_at`).
- [ ] **Step 7:** Commit `HOSTED_SETUP.md`: "Document the Supabase setup for Phase 2 (Phase 2, step 5)".
