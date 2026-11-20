# Travel-aware availability — design

Date: 2026-11-21. Approach A chosen by the owner in chat. Builds on the booking
location pin (docs/superpowers/specs/2026-11-20-booking-location-pin-design.md).

## Intent

One makeup artist travels between home-service appointments. Today the
calendar only blocks makeup time plus a 45-minute preparation buffer, so two
bookings far apart can be placed back to back and the artist cannot make it.
The owner wants the time offered to a customer to depend on where that
customer is: the customer pins the meet-up location first, then sees only the
start times the artist can actually reach, given every other booking that day.

## Behaviour (as the owner described it)

- The customer pins the location **before** choosing a time. Times are
  computed for that pin.
- The artist is at the customer's pin at the chosen start time (travel to a
  day's first booking is the artist's own concern and blocks nothing before
  the start).
- A booking blocks its service time plus the existing preparation buffer.
- Between any two consecutive bookings on a day (not only the second one),
  the travel time from the earlier booking's pin to the later booking's pin
  must also fit. Example: 7:00 booking ends at 9:00 (with buffer); a new pin
  1 hour away can start at 10:00 at the earliest.
- This also works backwards: a new booking must leave enough time to reach
  the next booking of the day from the new pin.
- The artist (owner/admin) can set a **current location**, e.g. "I'm in
  Novaliches"; otherwise the **home base** is used. It matters when there is
  no earlier booking that day and the day is today: the artist needs travel
  time from where they are now.
- The owner and the artist see the estimated travel between consecutive
  bookings in the admin day view.

## Goals

1. Booking step 2 order: pin first; the calendar and time list stay disabled
   until a pin is set; changing the pin reloads the times.
2. Availability for a date and pin follows the rules above.
3. The same rules are enforced again when the booking is saved (and when a
   reschedule is approved), so a stale page cannot double-book travel.
4. Business Settings: average travel speed (km/h, default 15) and minimum
   travel (minutes, default 30). Artist location card: home base pin, current
   location pin (set on the map or "Use my current location"), "Back to home
   base".
5. Admin day/calendar view shows "≈ N min travel" between consecutive
   bookings.

## Non-goals

- Live traffic, road routing or any paid/external routing service.
- Telling the customer where other customers are or which bookings caused a
  time to be unavailable.
- Multiple artists.
- Charging for travel (transportation stays a separate actual expense).

## Anti-goals

- Offering a time the artist cannot reach.
- Breaking existing bookings: bookings without a pin still count, using the
  minimum travel time.
- Slow pages: availability for a month must stay one request with in-process
  math (no per-slot network calls).

## Constraints

- Backend C# only; SQL migrations for schema; browser JS for the map.
- Travel estimate (approach A): straight-line (haversine) distance × road
  factor 1.3 ÷ owner-set speed, rounded **up** to 15 minutes, never below the
  owner-set minimum. Bookings or artist locations without a pin use the
  minimum.
- The existing database slot check (`mvc_check_slot`) stays as is (it guards
  service + buffer overlap); travel rules are enforced in C# inside the save
  transaction, under a per-artist advisory lock so two simultaneous bookings
  cannot both pass.
- Existing copy saying travel is "not estimated" must change.

## Approaches

- **A. Distance ÷ owner-set speed (chosen):** free, no key, always available,
  easy to explain; an estimate, made conservative by the owner's settings.
- B. Free road routing (OSRM/OpenRouteService): real roads but no traffic,
  rate limits and outages block booking.
- C. Google Routes with traffic: most accurate; needs the owner's billing
  account and API key, per-request cost.

## Design

1. **Schema (migration):** `business_settings.travel_speed_kmh` (numeric,
   default 15, 5–60) and `travel_min_minutes` (int, default 30, 0–180);
   `makeup_artists.home_lat/home_lng`, `current_lat/current_lng`
   (numeric(9,6), Metro Manila box, both-or-none), `current_updated_at`.
2. **Core (C#):** `TravelEstimate.Minutes(from?, to?, speedKmh, minMinutes)`
   and an availability rule that, for a date, the day's bookings (with their
   pins), the new pin, the duration and buffer, returns the valid start times
   — applying "previous end + buffer + travel(prev→new) ≤ start" and
   "start + duration + buffer + travel(new→next) ≤ next start", and for today
   with no earlier booking "now + travel(artist location→new) ≤ start".
3. **Availability endpoint** takes `latitude`/`longitude`; without them it
   returns no times and says "Pin your location to see available times."
4. **Save path:** `BookingService.Create` / `SubmitBooking` re-run the rule
   under a per-artist advisory lock (database) or the existing gate (demo);
   refusal message: "That time no longer fits the artist's travel. Choose
   another time." Reschedule approval re-runs it too.
5. **Booking form:** location block moves above the calendar in step 2; the
   calendar shows a hint and stays disabled until pinned.
6. **Admin:** Business Settings fields; Artist location card on Settings;
   travel lines in the day view.

## Owner decisions (after the builder check)

- **Override:** the rule is strict for customers. The owner (SuperAdmin)
  may create/approve a booking or reschedule that fails the travel rule only
  after a warning and an "I'm sure the artist can make it" checkbox; the
  override is recorded. Ordinary Admins cannot override.
- **Current location** stays in effect until the artist changes it or presses
  "Back to home base" (no automatic expiry). `current_updated_at` is shown
  next to it ("set 3 days ago") so a stale location is visible.

## Builder calls (marked as such; correct if wrong)

- **Owner-blocked time** (`blocked_slots`) is pure busy time: no pin, no
  travel in or out; handled by the existing overlap check.
- **Which bookings count** as neighbours: the same set that holds a slot
  today (`HoldsReservation`: Pending Payment within its window, Confirmed,
  InProgress), same calendar day only (Asia/Manila). For "today", the latest
  earlier booking of the day that is InProgress or Completed is where the
  artist is, ahead of the current location.
- **No pin** (old bookings, or an artist with no home/current location):
  travel to/from it is the minimum travel time, on both sides.
- **Reschedule:** the moved booking is excluded from its own neighbour list
  and checked only against its new neighbours.
- **Existing conflicts** between other bookings never block a new booking;
  only the new booking's own previous/next gaps are checked.
- **Neighbour pins in the database path** come from a new backend-only DB
  function (the existing `mvc_occupied_intervals` stays as is); coordinates
  never reach the browser.
- **Now:** the store's Asia/Manila `Now`; combined with any existing
  minimum-notice rule by taking the later time.
- **Start times** stay on the existing slot grid: travel is added and the
  next valid grid slot is offered.
- **Duration** is the booking's total quoted duration (packages included)
  plus its own preparation buffer; neighbours use their own buffer snapshot.
- **Admin travel lines** are computed live from current settings; a pinless
  side shows "≈ N min (no pin)".

## Left to the builder

Exact layout of the settings card and day-view travel lines, how the demo
store keeps the artist location, and test fixtures.
