# Booking Location Pin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Customers pin their exact location on a map when booking; the pin is stored and shown to the owner with an "Open in Google Maps" button.

**Architecture:** Migration 0013 adds nullable `location_lat/location_lng` to reservations (required for new app-managed bookings) and exposes them in the booking summary view. C# carries them through `SubmitBooking`/`BookingService.Create` with one shared validator. The browser shows a Leaflet/OpenStreetMap map on booking step 2 and read-only maps on the details pages.

**Tech Stack:** .NET 10 ASP.NET Core MVC (C#), PostgreSQL 17/Supabase, Leaflet 1.9.4 (served from `wwwroot/lib/leaflet`), OpenStreetMap tiles and Nominatim search.

**Spec:** `docs/superpowers/specs/2026-11-20-booking-location-pin-design.md`

## Global Constraints

- Backend is C# only; the map runs in the browser.
- Metro Manila box: latitude 14.20–14.95, longitude 120.85–121.25 (inclusive); stored as `numeric(9,6)`.
- Validation message: "Pin your exact location inside Metro Manila."
- Google link: `https://www.google.com/maps/dir/?api=1&destination={lat},{lng}` (invariant culture, 6 decimals).
- Nominatim: only on an explicit search, `countrycodes=ph`, at most one request per second.
- Tiles show "© OpenStreetMap contributors" attribution.
- One branch per task, chained from `phase-5/booking-map-design`; tests before each commit; push only on the owner's "push na".

## Review Focus

1. Customer denies GPS permission or GPS fails → a clear message, and the pin can still be dragged (Task 3 test: script handles the geolocation error branch).
2. A search returns nothing or the search service fails → "No match found; drag the pin instead" and no booking error (Task 3 test: script source contains the no-result branch).
3. A posted pin outside Metro Manila or only one coordinate (tampered form) → refused with the message above (Task 2 tests).
4. Old bookings with no pin → details pages say "No pinned location" and do not break (Task 4 test).
5. Decimal formatting under a non-English server culture → the Google link and hidden inputs use a dot, not a comma (Task 2/4 tests use invariant formatting).

---

### Task 1: Store the pin in the database (migration 0013)

Branch: `phase-5/booking-location-schema`.

**Files:**
- Create: `database/migrations/0013_booking_location.sql`
- Test: `tests/DatabaseChecks/Program.cs` (new section `--booking-location`)
- Modify: every existing `SubmitBooking(...)` call in `tests/DatabaseChecks/Program.cs` and `Backend/GlamByMariya.Infrastructure/Data/PublicSmokeTest.cs` only if Task 2's signature is already in place — in this task, insert test reservations through the store as today and set the pin columns by SQL where needed.

**Interfaces:**
- Produces: columns `public.reservations.location_lat numeric(9,6)`, `location_lng numeric(9,6)`; check `(location_lat IS NULL) = (location_lng IS NULL)` and the Metro Manila box; `mvc_booking_summary` gains `location_lat`, `location_lng` as its last two columns (CREATE OR REPLACE VIEW appending columns; copy the current definition from the last one in `0002_public_integration.sql` and append).
- The insert guard is NOT changed in this task (Task 2 makes the pin required for new bookings, once all callers can pass one).

- [ ] **Step 1: Failing tests** — `--booking-location`: after migrating, an UPDATE setting lat 14.5547/lng 121.0244 on a booking succeeds and `SELECT location_lat, location_lng FROM public.mvc_booking_summary` returns them; setting lat only → check violation (23514); lat 15.5 → 23514; full DatabaseChecks suite still passes.
- [ ] **Step 2: Run** `bash tools/test-db.sh tests/DatabaseChecks --booking-location` — expected FAIL (column does not exist).
- [ ] **Step 3: Write the migration.**
- [ ] **Step 4: Run** the section and the full suite — PASS.
- [ ] **Step 5: Commit.**

### Task 2: Carry and require the pin in C#

Branch: `phase-5/booking-location-domain` (from Task 1).

**Files:**
- Create: `Backend/GlamByMariya.Core/BusinessRules/BookingLocation.cs`
- Create: `database/migrations/0014_booking_location_required.sql` (insert guard requires both for new app-managed bookings; copy the current `mvc_reservation_guard` from `0011_owner_cancel_complete.sql` and add the check in the INSERT branch: `RAISE EXCEPTION 'Pin your exact location inside Metro Manila.'`)
- Modify: `Backend/GlamByMariya.Core/Entities/Domain.cs` (`Booking`), `Backend/GlamByMariya.Infrastructure/Data/MakeupDbContext.cs` (`BookingRow`), the row→Booking mapping in `PostgresStudioStore.cs`, `IStudioStore.SubmitBooking`, `PostgresStudioStore.SubmitBooking`, `PublicStudioStore.SubmitBooking` (INSERT includes the two columns), `DemoStore` booking creation, `BookingService.Create`
- Modify callers: `tests/DatabaseChecks/Program.cs`, `Backend/GlamByMariya.Infrastructure/Data/PublicSmokeTest.cs`, any other `SubmitBooking`/`Create` caller the build reports
- Test: `tests/Program.cs` (new section `--booking-location-only`), `tests/DatabaseChecks/Program.cs` (`--booking-location`)

**Interfaces:**
- Produces `public static class BookingLocation { public const decimal MinLat = 14.20m, MaxLat = 14.95m, MinLng = 120.85m, MaxLng = 121.25m; public const string Message = "Pin your exact location inside Metro Manila."; public static bool IsValid(decimal? lat, decimal? lng); public static string GoogleMapsUrl(decimal lat, decimal lng); }` (`GoogleMapsUrl` formats with `CultureInfo.InvariantCulture`, `F6`).
- `Booking.Latitude`, `Booking.Longitude` (`decimal?`); `BookingRow.LocationLat`, `LocationLng` mapped to `location_lat`, `location_lng`.
- `SubmitBooking(..., decimal? expectedTotal = null, decimal? expectedMinimum = null, decimal? latitude = null, decimal? longitude = null)`; `BookingService.Create(..., decimal? latitude = null, decimal? longitude = null)` — both throw `InvalidOperationException(BookingLocation.Message)` when `!BookingLocation.IsValid(latitude, longitude)` for a new booking.
- Test pin for callers: `14.5547m, 121.0244m`.

- [ ] **Step 1: Failing tests** — `--booking-location-only` (demo store): `IsValid(14.5547m,121.0244m)` true; `(14.5547m,null)` false; `(15.5m,121m)` false; `(14.20m,120.85m)` true (inclusive); `GoogleMapsUrl(14.5547m,121.0244m)` == `https://www.google.com/maps/dir/?api=1&destination=14.554700,121.024400` also under `CultureInfo.CurrentCulture = new("fil-PH")` and `new("de-DE")`; `Create` without a pin is refused with the message; with the test pin the booking keeps Latitude/Longitude. DB `--booking-location`: a booking submitted through `PublicStudioStore` with the test pin reads back the pin via `FindBooking`; a direct INSERT without pins after 0014 is refused.
- [ ] **Step 2: Run** both — FAIL.
- [ ] **Step 3: Implement** the validator, entity/row fields, mapping, store/service parameters, migration 0014, and update callers with the test pin.
- [ ] **Step 4: Run** `--booking-location-only`, `--xendit-only`, `--accounts-only`, `--exact-deposit-only`, `--payment-window-only`, `--staff-cancellation-completion-only`, `--boundary-regression-only`, and the full DatabaseChecks suite — PASS.
- [ ] **Step 5: Commit.**

### Task 3: Pin the location on the booking form

Branch: `phase-5/booking-location-form` (from Task 2).

**Files:**
- Create: `Frontend/GlamByMariya.Web/wwwroot/lib/leaflet/` (Leaflet 1.9.4 `leaflet.js`, `leaflet.css`, `images/` — the official release files; the controller obtains them with the owner's permission before dispatch)
- Create: `Frontend/GlamByMariya.Web/wwwroot/js/booking-location.js`
- Modify: `Frontend/GlamByMariya.Web/Views/Bookings/Create.cshtml` (step 2 map block, hidden inputs `latitude`/`longitude`, script/css includes), `Frontend/GlamByMariya.Web/Controllers/BookingsController.cs` (`Create` POST and `CreateWithXendit` accept and pass `decimal? latitude, decimal? longitude`), `wwwroot/js/booking-steps.js` and `booking-steps-phase2.js` (review row `['Location', 'Pinned ✓']`; submit stays disabled until both hidden inputs have values)
- Test: `tests/Program.cs` (`--booking-location-only`)

**Interfaces:**
- Consumes Task 2's `BookingService.Create(..., latitude, longitude)` and `BookingLocation.Message`.
- Area centres (in `booking-location.js`): Quezon City 14.6760,121.0437; Makati 14.5547,121.0244; Manila 14.5995,120.9842; Pasig 14.5764,121.0851; Taguig 14.5176,121.0509; Mandaluyong 14.5794,121.0359; default Metro Manila 14.5995,120.9842. A changed area re-centres the map only while no pin is set.
- Buttons: "Use my current location" (geolocation; on error show "We couldn't get your location. Drag the pin instead.") and a search box ("Search a landmark or street" → Nominatim `https://nominatim.openstreetmap.org/search?format=json&limit=1&countrycodes=ph&q=...`; no result or failure shows "No match found; drag the pin instead"). Clicking the map or dragging the marker sets the pin; values written with 6 decimals.

- [ ] **Step 1: Failing tests** — source checks: Create.cshtml has hidden inputs named `latitude` and `longitude`, references `~/lib/leaflet/leaflet.js` and `~/js/booking-location.js`; the script contains `countrycodes=ph`, the two error messages, the attribution "© OpenStreetMap contributors", and a one-request-per-second guard; both step scripts contain `'Pinned ✓'`; controller passes `latitude`/`longitude` to `workflow.Create`. Controller test: posting without a pin returns the form with `BookingLocation.Message`.
- [ ] **Step 2: Run** — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the section, `--xendit-only`, `--transportation-only`, `--boundary-regression-only`; then start the app locally in Demo mode (`.claude/launch.json` / `preview_start`) and check in the browser that the map renders, dragging sets the hidden inputs, and submit is blocked without a pin.
- [ ] **Step 5: Commit.**

### Task 4: Show the pin on the details pages

Branch: `phase-5/booking-location-details` (from Task 3).

**Files:**
- Create: `Frontend/GlamByMariya.Web/Views/Shared/_BookingLocationMap.cshtml` (model `Booking`), `wwwroot/js/booking-location-view.js`
- Modify: `Views/Admin/Details.cshtml`, `Views/Bookings/Details.cshtml` (render the partial after the address)
- Test: `tests/Program.cs` (`--booking-location-only`)

**Interfaces:**
- Consumes `Booking.Latitude/Longitude`, `BookingLocation.GoogleMapsUrl`.
- Partial: with a pin → a read-only map (`data-lat`, `data-lng` in invariant culture) and an "Open in Google Maps" link (`target="_blank" rel="noopener"`); without a pin → "No pinned location".

- [ ] **Step 1: Failing tests** — render the partial (or source-check) for a booking with the test pin: contains `14.554700` and the Google URL; for a booking with no pin: contains "No pinned location" and no map element; both details views include the partial.
- [ ] **Step 2: Run** — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the section and `--accounts-only`, `--staff-cancellation-completion-only`; check both pages in the local Demo app.
- [ ] **Step 5: Commit.**

### Task 5: Release

- [ ] Owner says "push na" → push the branches, then release in this exact order:
  - (a) Before applying anything, compare `pg_get_viewdef('public.mvc_booking_summary')` on Supabase with the last definition in `database/migrations/0002_public_integration.sql`.
  - (b) Apply 0013 only (execute_sql + record the migrator checksum, as for 0011/0012).
  - (c) Fast-forward `main` and deploy (Manual Deploy if auto-deploy does not fire); check `/healthz` and make one booking with a pin.
  - (d) Only then apply 0014 (same checksum recording).
  - Why this order: with 0014 and the old app every booking fails; with the new app and no 0013 every bookings page returns 500.
  - Rollback note: after 0014 is applied, rolling the app back to a version without the pin requires first re-applying 0011's `mvc_reservation_guard` (removing the pin check).
- [ ] Live check: the booking form shows the map; an existing booking shows "No pinned location"; a new booking (owner pays) shows its pin and the Google Maps button in admin.
