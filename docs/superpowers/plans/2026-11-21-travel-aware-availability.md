# Travel-aware Availability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Offer a customer only the start times the artist can reach from the neighbouring bookings of that day (and, for today, from the artist's location), estimated from map distance and an owner-set speed.

**Architecture:** A pure C# rule in Core computes travel minutes and checks a candidate start against the day's neighbours. `BookingService.GetAvailability` applies it per 15-minute slot for the customer's pin, and the save/reschedule paths re-check it under a per-artist lock. A migration adds the travel settings, the artist's home/current location and an override record, plus a backend-only function returning neighbour pins.

**Tech Stack:** .NET 10 ASP.NET Core MVC (C#), PostgreSQL 17/Supabase, Leaflet 1.9.4 (already vendored).

**Spec:** `docs/superpowers/specs/2026-11-21-travel-aware-availability-design.md` (builds on `docs/superpowers/specs/2026-11-20-booking-location-pin-design.md`)

## Global Constraints

- Backend C# only; SQL migrations; browser JS for maps.
- Travel minutes = ceil-to-15( haversine km × 1.3 ÷ speed km/h × 60 ), never below the minimum; any missing pin ⇒ the minimum.
- Defaults: speed 15 km/h (allowed 5–60), minimum 30 minutes (allowed 0–180).
- Slot grid stays 15 minutes; neighbours = bookings that hold a slot (`HoldsReservation`) on the same Asia/Manila day; owner blocks are pure busy time.
- Refusal copy: "That time no longer fits the artist's travel. Choose another time." No-pin availability copy: "Pin your location to see available times."
- Customers never receive other bookings' coordinates or the reason a time is unavailable beyond the existing generic reason.
- Override: SuperAdmin only, with the checkbox "I'm sure the artist can make it", recorded on the booking.
- One branch per task, chained from `phase-5/travel-availability-design`; tests before each commit; push only on the owner's "push na".

## Review Focus

1. A day with three or more bookings: the new slot must fit both gaps (previous and next), not just one (Task 1 test with three neighbours).
2. Neighbour without a pin or artist with no location → minimum travel, no exception (Task 1 test).
3. Two customers booking the same gap at once → only one succeeds (Task 3 DB test with two stores in parallel or sequential stale-page simulation).
4. Rescheduling a booking must not treat itself as a neighbour (Task 3 test).
5. Time zone: a booking near midnight UTC belongs to the Manila day it starts in (Task 1/3 tests use Manila-local times).

---

### Task 1: The travel rule (pure C#)

Branch: `phase-5/travel-rule`.

**Files:**
- Create: `Backend/GlamByMariya.Core/BusinessRules/TravelPlanner.cs`
- Test: `tests/Program.cs` (new section `--travel-only`)

**Interfaces:**
- Produces `public sealed record TravelStop(DateTime Start, DateTime End, int BufferMinutes, decimal? Latitude, decimal? Longitude);`
- `public static class TravelPlanner`:
  - `int Minutes(decimal? fromLat, decimal? fromLng, decimal? toLat, decimal? toLng, decimal speedKmh, int minimumMinutes)`
  - `bool Fits(DateTime start, int durationMinutes, int bufferMinutes, decimal lat, decimal lng, IReadOnlyList<TravelStop> sameDayStops, (decimal? Lat, decimal? Lng)? artistNow, DateTime now, decimal speedKmh, int minimumMinutes)` — previous stop = latest stop ending at or before `start`; next = earliest stop starting at or after `start + duration`; rule: `prev.End + prev.Buffer + Minutes(prev→new) ≤ start`, `start + duration + buffer + Minutes(new→next) ≤ next.Start`; if no previous stop and `start.Date == now.Date`: `now + Minutes(artistNow→new) ≤ start`. Overlaps are not this rule's job (return true for them; the existing overlap check rejects them).
  - `public const decimal RoadFactor = 1.3m; public const string RefusalMessage = "That time no longer fits the artist's travel. Choose another time.";`

- [ ] **Step 1: Failing tests** (`--travel-only`, all times Manila-local):
  - `Minutes`: Makati (14.5547,121.0244) → Fairview (14.7046,121.0567), 15 km/h, min 30 ⇒ **90** (≈17.0 km × 1.3 ÷ 15 ≈ 88.6 min ⇒ 90); same point ⇒ 30; a null on either side ⇒ 30; Makati → Fairview at 60 km/h ⇒ 30.
  - `Fits` (new pin Fairview, duration 60, buffer 45): previous stop 7:00–8:00 buffer 45 at Makati ⇒ 10:00 false, 10:15 true. Add a next stop at 13:00 in Makati ⇒ 10:15 false (ends 11:15 + 45 + 90 = 13:30). Stops at 7:00 and 15:00 (both Makati) ⇒ 10:15 true and 12:00 false (12:00+60+45+90 = 15:15).
  - Today with no previous stop: `now` 9:00, artistNow Novaliches (14.7290,121.0370) → Fairview is under the minimum ⇒ 30 ⇒ 9:15 false, 9:30 true; the same check on tomorrow's date ignores artistNow (9:00 true if within hours).
- [ ] **Step 2: Run** `dotnet run --project tests -- --travel-only` — FAIL (type missing).
- [ ] **Step 3: Implement** `TravelPlanner` (haversine with Earth radius 6371 km; decimals in, doubles inside, int minutes out).
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit.**

### Task 2: Settings, artist location and neighbour data

Branch: `phase-5/travel-schema` (from Task 1).

**Files:**
- Create: `database/migrations/0015_travel_availability.sql`
- Modify: `Backend/GlamByMariya.Core/Entities/Domain.cs` (`BusinessSettings.TravelSpeedKmh` decimal = 15, `TravelMinimumMinutes` int = 30; new `ArtistLocation(decimal? HomeLat, decimal? HomeLng, decimal? CurrentLat, decimal? CurrentLng, DateTime? CurrentUpdatedAt)`), `IStudioStore` (`ArtistLocation GetArtistLocation()`, `void SetArtistHome(decimal lat, decimal lng)`, `void SetArtistCurrent(decimal? lat, decimal? lng)` — null clears = back to home, `IReadOnlyList<TravelStop> TravelStops(DateTime from, DateTime until, Guid? exclude)`), `DemoStore`, `PostgresStudioStore`/`PublicStudioStore`, settings read/write (Business Settings save path)
- Test: `tests/DatabaseChecks/Program.cs` (`--travel`), `tests/Program.cs` (`--travel-only`, demo store)

**Interfaces:**
- 0015: `business_settings.travel_speed_kmh numeric(4,1) NOT NULL DEFAULT 15 CHECK 5–60`, `travel_min_minutes int NOT NULL DEFAULT 30 CHECK 0–180`; `makeup_artists.home_lat/home_lng/current_lat/current_lng numeric(9,6)` (Metro Manila box and both-or-none, same values as 0013), `current_updated_at timestamptz`; `reservations.travel_override_by uuid`, `travel_override_at timestamptz` (both-or-none); function `public.mvc_travel_stops(p_from timestamptz, p_until timestamptz, p_excluded uuid)` returning `start_at, end_at, buffer_minutes, location_lat, location_lng` for reservations that hold a slot (same status rule as `mvc_occupied_intervals`), `SECURITY INVOKER`, EXECUTE granted only to `makeup_backend` (revoke from PUBLIC, anon, authenticated).
- Consumes Task 1's `TravelStop`.

- [ ] **Step 1: Failing tests** — DB `--travel`: defaults read back as 15/30; setting speed 61 → 23514; home/current pins round-trip via the store; `SetArtistCurrent(null,null)` clears current and keeps home; `TravelStops` returns two pinned bookings of a day in order, excludes a given booking id and excludes an Expired one; `has_function_privilege('anon', 'public.mvc_travel_stops(timestamptz,timestamptz,uuid)', 'EXECUTE')` is false. Demo `--travel-only`: the demo store implements the same members with the same results for its seed booking.
- [ ] **Step 2: Run** both — FAIL.
- [ ] **Step 3: Implement** the migration, entity fields, store members and settings persistence.
- [ ] **Step 4: Run** the sections and the full DatabaseChecks suite — PASS.
- [ ] **Step 5: Commit.**

### Task 3: Apply the rule to availability, booking and reschedule

Branch: `phase-5/travel-enforcement` (from Task 2).

**Files:**
- Modify: `Backend/GlamByMariya.Infrastructure/Services/BookingService.cs` (`GetAvailability`, `Create`, reschedule approval path), `PublicStudioStore.SubmitBooking` and reschedule SQL path (per-artist `pg_advisory_xact_lock(hashtext('travel:' || artist_id))` before re-checking), `Frontend/GlamByMariya.Web/Controllers/BookingsController.cs` (`Availability` and `ValidateSlot` take `decimal? latitude, decimal? longitude`), `AdminController` reschedule approval (`bool travelOverride`)
- Test: `tests/Program.cs` (`--travel-only`), `tests/DatabaseChecks/Program.cs` (`--travel`)

**Interfaces:**
- `GetAvailability(DateTime month, int duration, Guid? exclude = null, decimal? latitude = null, decimal? longitude = null, CancellationToken cancellationToken = default)`: without a pin, every slot is unavailable with reason "Pin your location to see available times."; with a pin, a slot that passes the existing checks but fails `TravelPlanner.Fits` is unavailable with the existing generic unavailable reason (no travel detail).
- `Create(...)` and `SubmitBooking(...)` throw `InvalidOperationException(TravelPlanner.RefusalMessage)` when the rule fails at save time.
- Reschedule approval: re-runs the rule for the moved booking (excluding itself); SuperAdmin may pass `travelOverride: true` to approve anyway, which sets `travel_override_by/at`; Admin cannot.

- [ ] **Step 1: Failing tests** — demo: availability for a day with a Makati booking at 7:00 and a Fairview pin hides 9:00–10:00 and shows 10:15; without a pin every slot says the pin message; a stale-page `Create` at 9:45 is refused with the refusal message; reschedule of the 7:00 booking does not count itself; SuperAdmin override approves and records who/when; Admin override is refused. DB: two `SubmitBooking` calls for the same gap from different store instances — the second is refused with the refusal message.
- [ ] **Step 2: Run** — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `--travel-only`, `--booking-location-only`, `--xendit-only`, `--payment-window-only`, `--reschedule-policy-only`, `--persistent-reschedule-only`, `--staff-cancellation-completion-only`, `--boundary-regression-only`, and the full DatabaseChecks suite — PASS.
- [ ] **Step 5: Commit.**

### Task 4: Booking form — pin first, then times

Branch: `phase-5/travel-form` (from Task 3).

**Files:**
- Modify: `Frontend/GlamByMariya.Web/Views/Bookings/Create.cshtml` (location block above the calendar in step 2; calendar/time list disabled with the hint "Pin your location to see available times." until pinned), `wwwroot/js/booking-location.js` (on `booking-location-changed` with a valid pin, reload availability), the availability/validate fetch code in `wwwroot/js/site.js` (send `latitude`/`longitude`), copy that says travel is "not estimated" in the booking views and JS (replace with "Times include travel from the artist's other appointments that day.")
- Test: `tests/Program.cs` (`--travel-only` source checks)

**Interfaces:** consumes Task 3's endpoint parameters.

- [ ] **Step 1: Failing tests** — source checks: the location block precedes the calendar in step 2; the fetch URLs include `latitude` and `longitude`; the hint text exists; no booking view or booking JS contains "travel is not estimated"/"Travel is not estimated"/"travel not estimated".
- [ ] **Step 2: Run** — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the section, `--booking-location-only`, `--xendit-only`; then the controller checks it in the local demo browser (pin → times appear; moving the pin far changes times).
- [ ] **Step 5: Commit.**

### Task 5: Admin — settings, artist location, travel lines, override

Branch: `phase-5/travel-admin` (from Task 4).

**Files:**
- Modify: `Views/Admin/Settings.cshtml` + `AdminController.Settings` POST (speed, minimum with the allowed ranges); new partial `Views/Admin/_ArtistLocation.cshtml` + `wwwroot/js/artist-location.js` (map with home and current pins; "Use my current location"; "Save as current location"; "Set as home base"; "Back to home base"; shows "Current location set {N} ago"); `AdminController` actions `SetArtistHome`, `SetArtistCurrent` (Admin or SuperAdmin; antiforgery); day view in `Views/Admin/_AppointmentCalendar.cshtml` shows "≈ N min travel" between consecutive bookings ("(no pin)" suffix when a side has none); reschedule approval form shows the travel warning and, for SuperAdmin only, the checkbox "I'm sure the artist can make it" (`travelOverride`)
- Test: `tests/Program.cs` (`--travel-only`)

**Interfaces:** consumes Task 2's store members and Task 3's `travelOverride`.

- [ ] **Step 1: Failing tests** — settings POST saves 20/45 and rejects 70/200 with a message; `SetArtistCurrent` outside Metro Manila is refused; customer role cannot call the artist-location actions (403); the day view source renders the travel line; the override checkbox renders only for SuperAdmin.
- [ ] **Step 2: Run** — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the section, `--catalog-settings-only`, `--accounts-only`, `--staff-cancellation-completion-only`; controller browser check of Settings, the artist location card and the day view in the local demo.
- [ ] **Step 5: Commit.**

### Task 6: Release

- [ ] Release the booking location pin first if not yet live (its own runbook: viewdef check, 0013, deploy + pinned booking, 0014).
- [ ] Owner says "push na" → apply 0015 (execute_sql + migrator checksum) before deploying this code (0015 only adds nullable/defaulted columns and a function, so the old app keeps working); fast-forward `main`; deploy; set the artist's home base in Settings; check availability changes with the pin on the live booking form.
