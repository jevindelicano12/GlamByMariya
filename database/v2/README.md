# V2 public-schema migration plan

**Status:** The owner reports 0001 and 0002 applied to the new V2 project.
0003 is a closed, partial candidate for review, not a functional application
database or authorization to deploy. No connection to verify the reported
state was made.

Customer authentication now has a locally validated compatibility migration,
**0004**, depending only on 0001/0002. Its actual MVC tests no longer use the
identity candidate or auth fixture. Production gates remain closed; see
`CUSTOMER_AUTH_MIGRATION_PROCEDURE.md` for the permission contract and the
separately approved future hosted procedure.

The workspace currently has no Git repository. These files are suitable for
version control, but they are not committed or tracked; do not interpret their
numbering as evidence of deployment or approval.

## Files and ordering

| Order | File | Status and dependencies |
| --- | --- | --- |
| 1 | `migrations/0001_catalog_schedule_foundation.sql` | Applied per owner; do not rerun. |
| 2 | `migrations/0002_customer_reservation_foundation.sql` | Applied per owner; do not rerun. |
| 3 | `migrations/0003_payment_ledger_candidate.sql` | Closed payment ledger candidate only; no runtime authorization or working Customer/Admin workflow. Requires 0002 and prior security review. |
| 4 | `migrations/0004_customer_auth_compatibility.sql` | Locally validated auth-only compatibility; depends on 0001/0002, **not 0003**. Not applied hosted; empty Customer foundation required. |
| — | `blocked/0002_pending_slot_candidate.sql` | Historical illustration, **not a migration to run**. Do not include in an automatic runner. |
| test | `tests/foundation_disposable.sql` | Synthetic, rollback-only assertions intended for an explicitly disposable local database after 0001; neither a migration nor proof of Supabase behavior. |
| test | `tests/customer_reservation_disposable.sql` | Synthetic rollback-only 0002 checks, not a live or authentication test. |
| test | `tests/payment_ledger_disposable.sql` | Synthetic rollback-only 0003 checks; does not test an authorized payment workflow. |

0002 is intentionally only the legacy-compatible numeric-key baseline and
buffered reservation exclusion; it has no auth linkage, payment table, storage,
proof correction, mixed-service lines, business-rule triggers or runtime
policies. It does not make workflows safe or compatible. No accounts,
reservations, payments, images or seeds are imported. Review target collisions,
extension namespace/ownership, role memberships and inherited/default privileges
before applying anything. Do not run legacy EF migrations or `--database-init`
against V2 as a substitute for this plan.

Use explicit migration manifests, never a directory glob. The tested auth path
is **0001 → 0002 → 0004**, leaving payment 0003 unapplied. The reported hosted
foundation must not rerun 0001/0002; 0004 requires its own preflight and approval.

## RLS, roles and dependencies

0001 explicitly enables and forces RLS on all four tables and revokes all
table and sequence rights from `PUBLIC`, `anon`, `authenticated` and `service_role`. It creates
**no policies and no runtime grants**: ordinary non-bypass roles are denied,
including public catalog reads. FORCE does not constrain superusers or roles
with BYPASSRLS; privileged migration/service credentials are not browser
credentials. Verify inherited role memberships, other grants and default
privileges during the later deployment review.

Before runtime is enabled, decide whether catalog reads use a restricted
server-side backend or a narrowly filtered anonymous read-only policy/view;
artist phone numbers, blocked-slot reasons and unpublished catalog information
must not leak. Customer, staff, payment, evidence and booking tables need
default-deny RLS, owner-scoped customer access, explicitly authorized staff
operations and separately designed private object-storage access. Row policies
and column grants must be matched to each command and independently tested; no
`USING (true)` blanket browser policy. Define a non-owner, NOBYPASSRLS,
least-privilege server role only after the trusted request/actor boundary is
approved; do not grant it to `anon` or `authenticated`. Database roles,
Supabase auth roles, and application Customer/Admin/SuperAdmin roles are
distinct. Do not rely on client-settable `makeup.actor_id` as proof of identity.

The old `PublicIntegration.sql` creates a broad `makeup_backend` policy and
grants and assumes a pre-existing inspected schema; `BackendPermissions.sql`
targets the different `makeup` schema. Neither is a reviewed V2 grant plan.
Views such as `mvc_users` expose password hashes to their authorized invoker;
their privileges and `security_invoker` behavior must be reviewed. Triggers,
functions, default function EXECUTE, sequences, object owners, role memberships
and storage policies are part of the same review.

## Compatibility and remaining design work

The four initial tables do **not** yet satisfy `PublicStudioStore` or its EF
model. A reviewed subsequent public-schema baseline must define:

0002's exclusion protects overlapping reservations for Pending, Pending Payment,
Confirmed and InProgress, including `reserved_until`; Rejected releases the
range. It does **not** prevent conflicts with `blocked_slots` or weekly
availability, validate status transitions, or establish trusted inputs.
Do not grant runtime writes until these protections and authorization are
designed and tested together. Exclusion assumes correct buffer snapshots; no
trigger currently computes or freezes them. The existing adapter inserts without
an `end_at` value because its legacy trigger fills it; 0002 alone cannot run it.
The single required `service_id` is legacy baseline only and must be reconciled
with approved mixed-service parent/line design before mixed persistence.

Do not apply the legacy exact-deposit trigger: it would reject valid amounts
above the minimum. 0002 does not implement payment schema or proof correction;
minimum-to-total amounts, locked version checks, immutable verified evidence,
proof ownership, atomic updates and stale-review concurrency remain blocking
requirements, not guarantees of 0002.

0003 proposes an immutable claim and append-only, serialized payment revisions.
The claim checks the supplied minimum and total snapshots, and locks the current
pending reservation to compare its total. Revision numbers must be consecutive,
so two writers using the same observed revision cannot both append; verified
evidence cannot be revised. This is a closed schema experiment, not an
authorized correction or verification workflow. It grants no runtime access.
The supplied minimum is **not** authenticated against business settings;
reservation total and status can still be changed by privileged writers later;
proof UUIDs have no media foreign key or storage/ownership validation; actor
identity, Admin authority, atomic confirmation, immutable pricing, error mapping,
and role-specific operations are not enforced. No direct table grants should be
added until these gaps and version-token handling are resolved.
The existing persistent adapter uses a different payment/revision model and is
not wired to 0003; the UI and runtime remain unchanged. Check default privileges
and inherited memberships for *all* roles, not only the roles explicitly revoked.
Because 0003 does not change 0002, existing slot exclusion continues to apply;
it does not fix blocked-slot concurrency or enforce reservation transitions.

1. Customers and staff (`customers`, `admin_users`), stable `app_id` UUIDs,
   exactly-one-owner `account_identities`, normalized unique logins and the
   relationship to a verified Supabase subject. Choose FK/delete and duplicate
   recovery behavior; do not import V1 records.
2. The rest of the catalog: category, service UUID and group rate, add-ons,
   service areas, business settings, payment methods, and private media
   metadata. `PublicIntegration.sql` contains legacy seed rows and backfills,
   so it is a reference, not an approved clean baseline. Any future business
   seed must be separately approved.
3. Reservation header and snapshots, customer/artist/service FKs, quantities,
   consent, amounts, payment/revision/adjustment, history, closures, refunds,
   reschedules and audit records. `PreparedPersistentBooking.Enabled` is
   hard-coded false. Its activation and schema are not approved.
4. Versioned ordered migrations, transaction ownership, preflight, empty-state
   assumptions, upgrade path, rollback/forward-fix and EF migration-history
   strategy. The existing `Backend/GlamByMariya.Infrastructure/Migrations/Public`
   migrations are ordered by their timestamps but extend the old baseline.
   Direct SQL alongside EF history without an agreed baseline can cause EF to
   rerun incompatible DDL. 0001 does not insert EF history.

### Booking and finance acceptance gates

* Pending Payment must hold the artist slot as well as Pending, Confirmed and
  InProgress, including the stored buffer/travel boundary and half-open ranges.
  The old `no_artist_overlap`, `mvc_travel_overlap`, `mvc_check_slot` and
  block guard omit `Pending Payment`. The blocked fragment is **not a complete
  fix**: reconcile all checks, old constraints, updates, reschedules and
  artist/availability block concurrency in one tested design. Run parallel
  inserts and block-versus-booking tests, not just sequential tests.
* `BookingPricing` snapshots whole-percent down payment settings (1–100) and
  uses peso rounding; `PreparedPersistentBooking.ValidateClaim` accepts an
  amount from the required minimum through the final total. The legacy
  `DownPaymentPolicy.sql` and `PublicIntegration.sql` instead require exact
  equality. Do not apply them unchanged. Define authoritative calculation,
  line-item totals, optional fees, rounding, snapshot immutability, proof
  validation and atomic confirmation.
* Customer corrections must be owner-scoped, pending and unverified; staff
  correction requests need reasons, verified evidence stays immutable, and
  revision history and stale-review/concurrency checks must survive races.
  Verified adjustments/refunds require staff authorization and audit. Private
  proof objects must never become public.
* Mixed bookings require sealed per-person service and add-on snapshots,
  correct totals/durations, up to 20 people and at most one verified bridal
  service. Preserve booking/line identity, ownership, group-price rules and
  atomicity, not just a header. See `PreparedPersistentBooking.ReconstructMixed`
  and `tests/PublicStoreChecks/MixedSnapshotStructureChecks.cs`. Persistent
  mixed reads/writes are gated; do not turn them on.
* Validate booking ownership, add-on quantity, double submission, immutable
  money, reschedule retaining the original slot until approval, and admin
  review races at the database boundary with synthetic concurrent sessions.

### Authentication lifecycle decisions (blocked)

`AuthIdentityTracking.sql` only adds a nullable provider subject and defaults
to `Unknown`; it does not establish identity ownership or secure a session.
`AdminInvitationProvisioning.sql` adds correlated provisioning states, but
does not approve the full lifecycle. Decide and test verified provider-to-local
linking and uniqueness, registration retries/idempotency, invitation expiry
and one-time consumption, inactive pending admin provisioning, provider
uncertainty/reconciliation, initial SuperAdmin bootstrap, disabling and last
SuperAdmin protection, password reset/change, credential uncertainty, session
generation and revocation, recovery, and fail-closed cross-instance behavior.
Do not treat Unknown, failed or ambiguous provider outcomes as verified.
`Frontend/GlamByMariya.Web/Program.cs` explicitly rejects durable session
enforcement until password, ticket issuance and recovery are verified; do not
remove that gate. `AccountSessionGuardChecks` are guard-unit checks, not proof
of integrated cookie issuance or durable storage.

## Validation before any later deployment

1. Review the intended target and confirm it is the new project, its existing
   schema/roles/default privileges, extension availability and backups. No
   target inspection was performed in this preparation.
2. On a disposable **local** PostgreSQL instance only, create an empty database
   named `glam_v2_disposable` and synthetic `anon`/`authenticated`/`service_role` roles. As a
   local disposable superuser, apply 0001 once with errors stopping execution,
   then execute `tests/foundation_disposable.sql`. Check that rerunning 0001
   fails without altering pre-existing data. Explicitly test all four tables
   and sequences under actual non-owner roles, including inherited grants and
   both direct SQL and API access. Test owner/BYPASSRLS behavior separately.
   The SQL test's name/loopback guard is defense in depth, not proof a database
   is disposable; verify the target independently.
3. Run security and business integration tests against disposable synthetic
   data after the later migrations exist. Include concurrent sessions and both
   allowed and denied Customer/Admin/SuperAdmin operations. Never reuse old
   credentials, accounts, payment proofs or production connections.
4. Obtain explicit security and lifecycle approval, then separately authorize
   deployment. This document does not authorize it.

Existing offline schema contract checks refer to `docs/` candidate files omitted
from the V2 copy, so they cannot be run unchanged. The local SQL test does not
exercise RLS through actual role sessions or validate booking/auth behavior.
