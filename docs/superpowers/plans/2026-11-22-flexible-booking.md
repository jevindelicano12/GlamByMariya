# Flexible Booking Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let customers send early-booking requests the admin approves, book past closing (fee agreed offline), and book one package mixing services within admin-set per-service limits — on the live site.

**Architecture:** A pure Core rule (`BookingWindow`, `PackageRules`) decides early / regular / after-hours and package limits. The live schema (migration 0020) adds settings, a per-service package limit, an `Awaiting Approval` status, request/after-hours flags and per-person package rows; the guard trigger owns the new transitions. `BookingService` uses the rules for availability, quotes, create and approval; the Xendit invoice for a request is made at approval. Views/JS show grouped times, the after-hours note, package rows and admin approve/decline.

**Tech Stack:** .NET 10 ASP.NET Core MVC, EF Core + raw SQL on Supabase Postgres, Xendit test mode, Brevo email, Leaflet, vanilla JS. Tests: `tests/WorkflowChecks.csproj` sections (`dotnet run --project tests -- --<section>-only`) and Docker `tools/test-db.sh tests/DatabaseChecks`.

**Spec:** `docs/superpowers/specs/2026-11-22-flexible-booking-design.md`

## Global Constraints

- Backend C# only; schema only via `database/migrations/NNNN_*.sql` (this plan: `0020_flexible_booking.sql`); browser JS for the form.
- An unpaid booking holds its time for 15 minutes (`PaymentWindow.Length`); the Xendit checkout lasts 14 minutes (`XenditClient.InvoiceSeconds`). Unchanged.
- A request (`Awaiting Approval`) holds NO time; on approval it becomes `Pending Payment` with `payment_expires_at = now + 15 min` and from then blocks its whole span plus buffer for every booking.
- Early and after-hours limits are only what the admin sets: `EarlyRequestStartHour` starts equal to `OpenHour`, `AfterHoursEndHour` starts equal to `CloseHour`; validate `early ≤ open < close ≤ latest ≤ 24`.
- After-hours = the makeup session ends after `CloseHour`. Customer copy: "This booking runs past closing ({close}). An extra after-hours fee applies, to be agreed with the artist." The fee is never added to totals.
- Early slot group label: "Early booking (needs artist approval)". Early submit button: "Send early booking request".
- Declined request = existing `Rejected` status with the admin's reason. Unanswered request auto-closes after 7 days or when its start passes: `Rejected`, note "The artist could not confirm this early time."
- `max_per_package` applies to mixed packages; null = no limit; Bridal set to 1 on release. Max 20 people (`BookingPricing.MaximumPeople`).
- Mixed price: each person's regular service price + add-ons; down payment = settings percent of total.
- Approve/decline: roles Admin and SuperAdmin. Approval re-checks slot overlap and the travel rule.
- Notifications: in-app + email (`BrevoEmailSender.SendAsync`) on approval (with payment link) and on decline.
- Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; no push without the owner's "push na".

## Review Focus

1. Two requests for the same early time: both stay `Awaiting Approval`; approving the second after the first is paid fails with the slot message (Task 5 test).
2. Approved request left unpaid 15 minutes: released by the expiry job, slot bookable again (Task 2 DB test, Task 5 demo test).
3. A package with two Bridal from a stale page or crafted POST: refused on save, not only in the quote (Task 4 tests).
4. Admin sets `AfterHoursEndHour` below `CloseHour` or early after open: refused with a friendly message (Task 6 test).
5. Existing single bookings and today's settings unchanged after 0020 (defaults equal open/close): availability for a 1-person Soft Glam day is identical before/after (Task 3 test).

---

### Task 1: Core booking window and package rules

**Files:**
- Create: `Backend/GlamByMariya.Core/BusinessRules/BookingWindow.cs`, `Backend/GlamByMariya.Core/BusinessRules/PackageRules.cs`
- Modify: `Backend/GlamByMariya.Core/Entities/Domain.cs` (BusinessSettings, CatalogItem, Booking), `Backend/GlamByMariya.Core/Entities/Availability.cs`
- Test: `tests/Program.cs` new section `--flexible-rules-only`

**Interfaces:**
- Produces:
  - `BusinessSettings.EarlyRequestStartHour : int?`, `BusinessSettings.AfterHoursEndHour : int?` (null = same as open/close).
  - `CatalogItem.MaxPerPackage : int?`.
  - `Booking.IsEarlyRequest : bool`, `Booking.AfterHours : bool`; status string `"Awaiting Approval"`.
  - `public enum SlotKind { Regular, Early }`; `AvailableTimeSlot(string Start, string Label, bool Available, string? Reason, string Kind = "regular", bool AfterHours = false)`.
  - `static class BookingWindow`: `int EarliestHour(BusinessSettings s)`, `int LatestHour(BusinessSettings s)`, `SlotKind KindOf(DateTime start, BusinessSettings s)`, `bool IsAfterHours(DateTime start, int durationMinutes, BusinessSettings s)`, `bool Fits(DateTime start, int durationMinutes, int bufferMinutes, BusinessSettings s)`, `string? ValidateSettings(int open, int close, int? early, int? latest)` (null = ok), `const string AfterHoursNote = "This booking runs past closing ({0}). An extra after-hours fee applies, to be agreed with the artist."`, `const string EarlyGroupLabel = "Early booking (needs artist approval)"`.
  - `static class PackageRules`: `string? Check(IReadOnlyList<CatalogItem> chosenServicesPerPerson)` returns the first error, e.g. `"Only 1 Bridal Makeup is allowed per package booking."`, else null.

- [ ] **Step 1: Write failing checks** (`--flexible-rules-only`), settings open 8 / close 18 / buffer 45:
  - early null/latest null ⇒ `EarliestHour == 8`, `LatestHour == 18`; `KindOf(07:00)` with early 4 ⇒ `Early`; `KindOf(08:00)` ⇒ `Regular`.
  - `IsAfterHours(17:00, 120)` ⇒ true; `IsAfterHours(16:00, 120)` ⇒ false (ends exactly 18:00).
  - early 4, latest 22: `Fits(04:00, 540, 45)` ⇒ true (4 AM–1 PM example); `Fits(03:45, 60, 45)` ⇒ false; `Fits(21:00, 60, 45)` ⇒ false.
  - `ValidateSettings(8,18,9,null)` ⇒ "Early booking cannot start after opening."; `(8,18,null,17)` ⇒ "Latest end cannot be before closing."; `(8,18,4,22)` ⇒ null.
  - `PackageRules.Check([bridal, bridal, soft])` with Bridal max 1 ⇒ `"Only 1 Bridal Makeup is allowed per package booking."`; `[bridal, soft, soft, glam, glam, glam]` ⇒ null; 21 people ⇒ `"Choose between 2 and 20 people."`.
- [ ] **Step 2:** Run `dotnet run --project tests -- --flexible-rules-only` → fails (types missing).
- [ ] **Step 3:** Implement the members above. Add the new settings/catalog/booking properties to `DemoStore` seed (Bridal `MaxPerPackage = 1`).
- [ ] **Step 4:** Run the section → all pass; run `--travel-rule-only`, `--reservation-only` → still pass.
- [ ] **Step 5:** Commit "Core rules for early, after-hours and package limits".

### Task 2: Migration 0020 and the live store mapping

**Files:**
- Create: `database/migrations/0020_flexible_booking.sql`
- Modify: `Backend/GlamByMariya.Infrastructure/Data/PublicStudioStore.cs` (settings load/save, catalog load/save, booking read), `Backend/GlamByMariya.Infrastructure/Data/MakeupDbContext.cs` / `PublicMakeupDbContext.cs` rows as needed
- Test: `tests/DatabaseChecks/Program.cs` new section "flexible booking database checks"

**Interfaces:**
- Consumes: Task 1 properties.
- Produces (SQL):
  - `business_settings.early_request_start_hour int NULL`, `after_hours_end_hour int NULL`, CHECK `(early IS NULL OR early BETWEEN 0 AND open_hour) AND (latest IS NULL OR latest BETWEEN close_hour AND 24)`.
  - `services.max_per_package int NULL CHECK (max_per_package >= 1)`; `UPDATE services SET max_per_package = 1 WHERE name = 'Bridal Makeup'`.
  - `reservations.is_early_request boolean NOT NULL DEFAULT false`, `reservations.after_hours boolean NOT NULL DEFAULT false`, status `'Awaiting Approval'` allowed.
  - `reservation_people(id bigserial, reservation_id bigint FK, position int, service_id FK, service_name_snapshot, price_snapshot numeric(14,2), minutes_snapshot int, UNIQUE(reservation_id, position))`, `reservation_people_addons(person_id FK, addon_id FK, name_snapshot, price_snapshot, minutes_snapshot)`; RLS on, backend-only grants like 0008.
  - `mvc_reservation_guard` = 0019 guard plus: INSERT with `NEW.is_early_request` ⇒ status must be `'Awaiting Approval'`, `payment_expires_at := NULL`, no `mvc_check_slot`; UPDATE `Awaiting Approval → Pending Payment` ⇒ `payment_expires_at := clock_timestamp() + interval '15 minutes'` and `mvc_check_slot(...)`; `Awaiting Approval → Rejected` needs a reason.
  - `mvc_expire_overdue()` also sets `Awaiting Approval` rows to `Rejected` (note "The artist could not confirm this early time.") when `created_at < now() - 7 days` or `start_at <= now()`.
  - Occupied-interval and travel-stop functions keep excluding `Awaiting Approval` (verify; no change expected).
- Produces (C#): `PublicStudioStore` reads/writes the new columns; `Booking.IsEarlyRequest/AfterHours` populated.

- [ ] **Step 1: Write failing DB checks:** a request insert stays `Awaiting Approval` with null deadline and does not block an overlapping regular booking; approving sets a 15-minute deadline and then blocks the span; two requests for the same time can both exist, approving the second after the first is approved fails with the overlap message; a 7-day-old request becomes `Rejected` via `mvc_expire_overdue()`; Bridal `max_per_package = 1`; a fresh DB's settings have both new hours null.
- [ ] **Step 2:** `bash tools/test-db.sh tests/DatabaseChecks` → new checks fail.
- [ ] **Step 3:** Write 0020 (guard copied from 0019 with the additions) and the store mapping.
- [ ] **Step 4:** DB checks pass; `bash tools/run-all-checks.sh` → OVERALL: PASS.
- [ ] **Step 5:** Commit "Schema for early requests, after-hours and package people (0020)".

### Task 3: Availability, quote and single-booking create

**Files:**
- Modify: `Backend/GlamByMariya.Infrastructure/Services/BookingService.cs` (GetAvailability slot loop ~L300-330, `RequireFitsInDay`, `SlotIssue`, `Create`), `Backend/GlamByMariya.Infrastructure/Data/PublicStudioStore.cs` `SubmitBooking` (insert `is_early_request`, `after_hours`, status), `DemoStore` mirror
- Test: `tests/Program.cs` section `--flexible-booking-only`

**Interfaces:**
- Consumes: Task 1 `BookingWindow`, Task 2 columns.
- Produces: availability slots carry `Kind` ("early"/"regular") and `AfterHours`; `Booking Create(...)` for an early start returns a booking with `Status == "Awaiting Approval"`, `IsEarlyRequest == true`, `PaymentDueAt == null`, no Xendit invoice; `RequireFitsInDay` uses `BookingWindow.EarliestHour/LatestHour`.

- [ ] **Step 1: Failing checks (demo store):** with early 4 / latest 22, Nov-30 Soft Glam 1-person availability includes 04:00 with Kind "early" and 17:30 with AfterHours true; with both null the slot list equals today's (8:00 first, last start 16:15 for 60 min + 45 buffer) — Review Focus 5; `Create` at 05:00 ⇒ `Awaiting Approval`, `PaymentDueAt == null`, and `CheckSlot(05:00, 60)` still passes for another customer (request holds nothing); `Create` at 17:00 for 120 min ⇒ `AfterHours == true`, total unchanged; 10-person Soft Glam fits when latest is 22 (no "longer than the artist's day").
- [ ] **Step 2:** Run → fail. **Step 3:** Implement. **Step 4:** Section passes plus `--travel-rule-only`, `--travel-form-only`, `--reservation-only`, `--payment-window-only`. **Step 5:** Commit.

### Task 4: Mixed packages on the live store

**Files:**
- Modify: `BookingService.cs` (`QuoteMixed`, `CreateMixed`, `GetMixedAvailability` — remove the "not available in persistent storage" refusals, enforce `PackageRules.Check`, use the booking window, take `latitude/longitude` and the travel rule), `IStudioStore.cs` + `PublicStudioStore.cs` new `Booking SubmitPackage(Guid customer, IReadOnlyList<MixedPersonSelection> people, DateTime start, string address, string area, decimal deposit, string method, string reference, bool agreed, bool travelAcknowledged, decimal expectedTotal, decimal expectedMinimum, decimal latitude, decimal longitude)` writing `reservation_people` rows in the same transaction and advisory lock as `SubmitBooking`; booking read fills `Booking.MixedPeople`.
- Modify: `Frontend/GlamByMariya.Web/Controllers/BookingsController.cs` (`MixedQuote/MixedAvailability/MixedValidateSlot/CreateMixed` accept the pin; Xendit path like `CreateWithXendit`)
- Test: `--flexible-booking-only` (demo) and DatabaseChecks

**Interfaces:** Consumes Task 1 `PackageRules`, Task 3 window behaviour. Produces `SubmitPackage` and package bookings readable with `MixedPeople` on live.

- [ ] **Step 1: Failing checks:** quote 1 Bridal + 2 Soft + 3 Glam ⇒ total = 1500 + 2×600 + 3×750, duration = 120+120+270, deposit 50%; 2 Bridal refused in quote AND in `CreateMixed`/`SubmitPackage` (Review Focus 3); a package needs a pin and passes the travel rule; DB check: package insert writes 6 `reservation_people` rows and reads back.
- [ ] **Step 2–4:** fail → implement → pass (+ run-all-checks). **Step 5:** Commit.

### Task 5: Approve / decline early requests and pay after approval

**Files:**
- Modify: `BookingService.AdminAction` + `PublicStudioStore.ManageBooking` (actions `"approve-request"`, `"decline-request"`), `AdminController` (POST action wiring, Admin+SuperAdmin), `BookingsController` (customer "Pay down payment" for an approved request creates the Xendit invoice if none and redirects; `PendingCheckoutUrl` reused), email via `BrevoEmailSender`.
- Test: `--flexible-booking-only`, DatabaseChecks

**Interfaces:** Consumes Task 2 transitions. Produces `AdminAction(booking, "approve-request" | "decline-request", adminId, reason, ...)`.

- [ ] **Step 1: Failing checks:** approve ⇒ `Pending Payment`, deadline now+15, slot now blocked (4:00–13:00 example blocks a regular 08:00 booking); approve when the slot was taken ⇒ refused with the overlap message, request unchanged (Review Focus 1); approve re-checks travel; decline needs a reason ⇒ `Rejected`; customer notification text "Your early booking request was approved. Pay the down payment within 15 minutes to keep this time." and decline text "Your early booking request was declined: {reason}"; unpaid 15 min after approval ⇒ `Expired`, slot free (Review Focus 2); a plain Admin may approve.
- [ ] **Step 2–4:** fail → implement → pass. **Step 5:** Commit.

### Task 6: Screens — booking form, admin settings, catalog, approvals

**Files:**
- Modify: `Views/Bookings/Create.cshtml`, `wwwroot/js/site.js` (time groups from `Kind`, after-hours note from `AfterHours`, early submit label, package rows with per-service limit), `Views/Bookings/Details.cshtml` + `Index.cshtml` (request status, pay button after approval), `Views/Admin/Settings.cshtml` + `AdminController.Settings` (two hour fields, `BookingWindow.ValidateSettings` messages), `Views/Admin/Catalog.cshtml` + save action (Max per package), `Views/Admin/Details.cshtml` + `_AppointmentCalendar.cshtml` + Appointments list (Early request badge, Approve / Decline with reason, after-hours flag, per-person list).
- Test: `--flexible-screens-only` (source/controller checks), browser check on the local demo.

- [ ] **Step 1: Failing checks:** Settings POST with latest 17 ⇒ TempData error "Latest end cannot be before closing." (Review Focus 4); Catalog save stores Max per package; Create view contains the early group label and the after-hours note format; Details shows Approve/Decline only for `Awaiting Approval` and only to Admin/SuperAdmin.
- [ ] **Step 2–4:** fail → implement → pass; on the local demo: set early 4 / latest 22, book 1 Bridal + 2 Soft + 3 Glam at 04:00 as customer ⇒ request; approve as owner ⇒ pay button appears; a regular 08:00 booking that day is refused.
- [ ] **Step 5:** Commit.

### Task 7: Release (needs the owner's "push na")

- [ ] Apply 0020 on live (execute_sql + migrator checksum) before deploying; verify Bridal `max_per_package = 1`, settings hours null.
- [ ] Merge to `main`, push (Render auto-deploys), `/healthz` 200.
- [ ] Owner sets early/latest hours in Settings; live check as customer: early request appears in admin, approve, pay link shown.
