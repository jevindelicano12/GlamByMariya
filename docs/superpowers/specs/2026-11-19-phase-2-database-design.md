# Phase 2: Supabase PostgreSQL database — design

Date: 2026-11-19. Branch chain: starts from `phase-1/step-9c-booking-queries`.

## Intent

The app must run on the real Supabase PostgreSQL database, with the Phase 1
booking rules enforced by the database as well as by the C# code. This is the
base for Phase 3 (Xendit demo payments) and Phase 4 (Render hosting).
Supabase is used for PostgreSQL and Storage only; login stays local
(email + password hash in our tables, Brevo email codes).

## What exists today

- The app's database mode (`Database:Schema=public`, `PublicStudioStore`) was
  written against an older Supabase project. Its schema is built in layers:
  a legacy base (customers, admin_users, makeup_artists, artist_availability,
  blocked_slots, services, reservations, payments, reservation_history), then
  `Data/Sql/PublicIntegration.sql` and six later SQL payloads applied by EF
  migrations in `Migrations/Public`.
- The legacy base DDL exists only as a test reconstruction:
  `tests/PublicChecks/BaselinePublic.sql` (with fake seed rows).
  `tests/PublicStoreChecks` builds base + PublicIntegration on a local
  loopback PostgreSQL and runs store checks against it.
- `database/v2/migrations/0001–0006` is a different design. It is not what the
  app uses. Only `0005_email_codes.sql` and `0006_customer_birthday_suffix.sql`
  are needed by the current code.
- Supabase project `glam-by-mariya-v2` (ap-northeast-1) has about 13 public
  tables from v2 0001/0002/0004 plus leftovers of the removed Supabase Auth
  (2 `auth.users`, `account_security_generations`, `auth_mvc_signup_attempts`).
  Data: 1 test customer, no bookings. No storage buckets. `pg_cron` not
  installed. Security advisor flags `public.rls_auto_enable()` (SECURITY
  DEFINER, callable by anon/authenticated).
- The code uses about 40 public objects. Not defined by any SQL in the repo:
  `mvc_occupied_intervals` (used for availability), the `reservation_mixed_*`
  and `mixed_service_classifications` tables (mixed bookings, switched off in
  database mode), and `website_content_*` (the Postgres website-content
  repository is not wired up in `Program.cs`).
- Docker Desktop is installed on the owner's PC; no local `psql`.

## Goals

1. **One ordered set of SQL migrations** that builds, on an empty database,
   every object the app needs in database mode. Replaces the legacy-base +
   EF-payload layering. Lives in `database/migrations/` as `NNNN_name.sql`.
2. **Phase 1 rules in the database:**
   - `Pending Payment` holds the slot in every overlap check
     (`mvc_travel_overlap` exclusion constraint, `mvc_check_slot`, block guard)
     — today these list only Pending/Confirmed/InProgress.
   - 30-minute payment window: `reservations.payment_expires_at`, a new
     `Expired` status, and the transition `Pending Payment → Expired`
     releasing the slot.
   - `Pending Payment → Cancelled` by the customer, no fee.
   - Confirmed cancellation: fixed PHP 200 fee, allowed only until 24 hours
     before the start (matches `CancellationPolicy`).
   - Exact deposit: down payment equals the required snapshot (already exact
     in `DownPaymentPolicy.sql`; keep it, check it matches the C# rule).
3. **`mvc_occupied_intervals` defined in the repo**, including Pending Payment
   inside its window and the buffer, matching `BookingService.HoldsReservation`.
4. **Scheduled jobs with `pg_cron`:** expire unpaid bookings every minute;
   delete used/expired email codes daily.
5. **A migration command** `--database-migrate` that applies the SQL files in
   order, each in its own transaction, records them in
   `public.schema_migrations(version, name, checksum, applied_at)`, refuses a
   changed checksum or a gap, and never runs EF migrations. The EF
   `Migrations/Public` folder and its `--database-migrate` path are retired.
6. **Local test database in Docker:** a script starts `postgres:17` on
   127.0.0.1 with a random port and a marker setting, applies all migrations,
   and runs the database test suites (`PublicStoreChecks`, `PostgresChecks`,
   `WebsiteContentPostgresChecks` where still relevant) plus new checks for
   the Goal 2 rules, including two sessions booking the same slot at once.
7. **Least-privilege access:** the app connects as a non-owner login role
   that is a member of `makeup_backend` (the role the existing RLS policies
   trust). No grants to `anon` or `authenticated`. Revoke
   `rls_auto_enable()` from them.
8. **Private Storage bucket `app-files`** for payment proofs and images.
9. **Apply to `glam-by-mariya-v2`** only after the Docker suite passes:
   back up, clear the old public objects and Supabase Auth leftovers,
   apply the migrations, create the owner with
   `--database-create-superadmin`, run a smoke test.
10. **App code that only database mode needs:** the database store's
    `ExpireOverduePayments` returns the job's result instead of 0 (or the
    status is read as Expired); any C# check that still says database
    availability is "not verified" for the normal booking path is removed
    once the Docker tests cover it.

## Non-goals

- Xendit, payment review/verify in database mode (Phase 3 replaces manual
  review with the webhook), Render (Phase 4).
- Mixed-service bookings in database mode (stay switched off).
- Website content editing in database mode (stays file-based Demo only).
- Importing any old V1 data.
- Supabase Auth.

## Anti-goals

- Changing `glam-by-mariya-v2` before the full Docker suite passes.
- Two migration systems at once (EF history plus SQL files).
- Any table readable or writable through Supabase's public API
  (`anon` / `authenticated`).
- Seeded accounts or demo bookings in the migrations.

## Constraints

- Supabase PostgreSQL 17; the app connects through the Session pooler.
  Free plan: 2 active projects (both used), so testing is local Docker,
  not a third project.
- `btree_gist`, `pgcrypto`, `pg_cron` are available on Supabase.
- Windows 11 + Git Bash/PowerShell; Docker Desktop.
- Each step on its own branch chained from the previous one; tests pass
  before each commit; ask before pushing.

## Approaches considered

1. **New single baseline from the schema the app uses (chosen by the owner).**
   Fold the legacy base, PublicIntegration and the later payloads into one
   clean ordered set, add the Phase 1 rules, test in Docker, then reset
   `glam-by-mariya-v2`. Costs: the 1 test customer and the Auth leftovers
   are deleted. Gains: one schema that matches the code, no legacy
   backfills or seed rows.
2. Extend the hosted v2 0001/0002 foundation. Nothing deleted, but 0002 uses
   numeric keys and a different shape from what the code expects, so most
   tables would need rework anyway.
3. Restore the paused old project "makeup". Brings V1 data we decided not to
   import.

## Decisions taken in this design (builder's calls, owner can change)

- Migration order:
  `0001_base` (legacy base tables, clean, no seed),
  `0002_public_integration` (PublicIntegration without legacy backfills and
  seed rows; AccountPreferences; ProfileAvatars; RolesAndInvitations;
  DataProtectionKeys; DownPaymentPolicy; TransportationFeePolicy),
  `0003_email_codes` (from v2 0005), `0004_customer_birthday_suffix`
  (from v2 0006), `0005_phase1_rules`, `0006_occupied_intervals`,
  `0007_jobs`, `0008_access` (roles, grants, revokes),
  `0009_catalog_seed` (business baseline: categories, the three services,
  add-ons, service areas, payment methods and business settings — no
  artist, no accounts, no bookings). Written new for the public
  tables: `Data/Sql/ProductionSeed.sql` targets the old `makeup` schema
  (`search_path=makeup`, a `roles` table) and cannot be reused as is; only its
  values (names, prices, minutes, areas) are copied. The owner edits the
  catalog later from the Catalog page.
- `AuthIdentityTracking.sql` and `AdminInvitationProvisioning.sql` are left
  out: no current code reads their columns (checked by grep).
- `admin_invitations.invited_by` references `account_identities(id)`, and
  the code passes the admin's `app_id`. Account identity ids equal `app_id`
  (PublicIntegration inserts `id = app_id`), so the FK holds; a Docker test
  proves it.
- Artist model: the business has exactly one makeup artist, and that is the
  owner (decided by the owner, 2026-11-19). `PublicStudioStore.ResolveArtist`
  needs exactly one active `makeup_artists` row (or `Database:ArtistId`),
  otherwise booking stops with an error. The seed does **not** create it;
  `--database-create-superadmin` creates the owner account and, in the same
  transaction, the single artist row with the owner's first and last name.
  It refuses if an active artist already exists.
- Expiry job runs every minute; it sets `Expired` on `Pending Payment`
  rows whose `payment_expires_at < now()` and writes history.
- Backup of the hosted project before reset: `pg_dump` of the public schema
  through Docker's `pg_dump`, saved outside the repo.

## Context from the owner

- This system is a demo for the owner's client. Payments stay in Xendit test
  mode; the hosted database holds demo data only.
- One makeup artist only: the owner.

## Left to the builder

- Exact column list of `0001_base` (start from `BaselinePublic.sql`, drop the
  `MakeupBookingSystem` table and seed rows).
- Which legacy backfill statements in PublicIntegration are no-ops on an
  empty database and can be removed versus kept for safety.
- Test names and the Docker script's exact shape.

## Steps (each its own branch)

- `phase-2/step-1-migrations`: Goals 1, 3 (and 2 except jobs), files only.
- `phase-2/step-2-docker-tests`: Goal 6 and new rule checks.
- `phase-2/step-3-migrate-command`: Goal 5, retire EF migrations, and the
  owner setup command also creates the single artist row.
- `phase-2/step-4-jobs-and-access`: Goals 4, 7 and code for Goal 10.
- `phase-2/step-5-hosted`: Goals 8, 9 (asks the owner before touching
  Supabase).
