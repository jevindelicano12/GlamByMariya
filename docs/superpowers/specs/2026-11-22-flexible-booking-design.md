# Flexible booking: early requests, after-hours, mixed group packages — design

Date: 2026-11-22. Agreed with the owner in chat (Filipino); this restates it.

## Intent

Real customers book groups and odd hours. Today the live site only takes one service per
booking, only inside the artist's opening hours (8:00–18:00), and a group longer than the day
is refused. The owner wants three things:

1. **Early booking request.** When choosing a time, the customer also sees earlier times,
   before opening, marked as an early booking. Choosing one sends a *request*; the artist or
   admin confirms it. Only after confirmation does the customer pay.
2. **After-hours.** A booking may run past closing. Past closing there is an extra charge that
   the artist and customer agree between themselves (the system does not compute it); the
   customer is told before booking.
3. **Mixed group package.** One booking can hold several people with different services, e.g.
   1 Bridal + 2 Soft Glam + 3 Glamour. The admin decides, per service, how many times it may
   appear in one package booking (owner's example: Bridal at most once; Soft Glam and Glamour
   any number).

And the payment rule stays: **an unpaid booking holds its time for 15 minutes; if it is not
paid in 15 minutes, the temporary hold on the time/date is released.**

## Goals

1. Time picker shows two groups for a date: *Early booking (needs artist approval)* and
   *Regular* times. Picking an early time makes the booking a request.
2. Early request flow: customer submits (no payment) → admin sees it in Appointments with
   Approve / Decline → on approve the customer gets the Xendit payment link (in the app and by
   email) and the 15-minute hold starts → paid ⇒ Confirmed; not paid in 15 min ⇒ released.
   Decline ⇒ request closed, customer notified, nothing to refund.
3. After-hours: times whose session (plus buffer) ends after closing are offered up to an
   admin-set latest end time. The booking form and summary show: "This booking runs past
   closing (6:00 PM). An extra after-hours fee applies, to be agreed with the artist." The
   booking total does not include it. Admin details show the same flag.
4. Mixed package: the customer adds people one by one, each with a service (and add-ons). The
   quote enforces each service's admin-set "most per package booking"; the total duration is
   the sum; the whole booking must fit between the earliest early time and the latest end time.
5. Admin settings: Business Settings gets *Earliest early-booking start* (hour) and *Latest
   booking end* (hour). Catalog gets, per service, *Most per package booking* (blank = no
   limit; Bridal set to 1 on release).
6. Works on the live site (Supabase/Postgres + Xendit), with the same pin, travel rule and
   15-minute payment rule as today's single bookings.

## Non-goals

- Computing or collecting the after-hours fee (agreed and paid outside the system).
- An early-booking surcharge in the total.
- Multiple artists.
- Changing the existing single-service booking or group-rate (same service, group price) flow
  except to add the early/after-hours times.

## Anti-goals

- A booking holding a slot forever: unpaid ⇒ released at 15 minutes after the
  payment link is issued; a request never blocks the calendar (it holds nothing until approved).
- Charging a customer whose early request is later declined.
- Letting a package exceed an admin limit (e.g. two Bridal) through a stale page or crafted
  request — limits are re-checked on save.
- Offering a time the artist cannot reach (travel rule still applies to all kinds).

## Constraints

- Backend C# only; schema via SQL migrations (next: 0020+); browser JS for the form.
- Live DB guard (`mvc_reservation_guard`, last 0019) owns status transitions; new statuses and
  the request→payment transition must be added there.
- Xendit invoice created only when payment is due (after approval for early requests); the
  checkout still closes 1 minute before the 15-minute hold ends.
- Persistent store today refuses mixed bookings; per-person rows need new tables.

## Approaches

- **A. Extend the live booking model (chosen).** Add per-person rows and a request status to
  the live schema, port the demo mixed quote, add the early/after-hours windows to
  availability. One booking = one reservation, as now; admin and customer pages already show
  one booking.
- B. Model a package as several linked single bookings. Simpler schema, but payment, travel,
  cancellation and the 15-minute hold would have to coordinate across rows — fragile.
- C. Keep groups same-service only and add a free-text "other people" note. Cheap, but does
  not let the admin limit Bridal or price each person.

## Design

1. **Schema (migration 0020):**
   - `business_settings.early_start_hour` (int, starts equal to the open hour, ≤ open hour) and
     `latest_end_hour` (int, starts equal to the close hour, ≥ close hour, ≤ 24); admin-set.
   - `services.max_per_package` (int, null = no limit, ≥ 1).
   - `reservation_people` (reservation_id, position, service_id, name/price/minutes snapshots,
     add-on snapshots via a child table) for mixed packages.
   - `reservations.is_early_request` (bool) and status `Awaiting Approval`; guard transitions
     `Awaiting Approval → Pending Payment` (approve, sets `payment_expires_at = now + 15 min`),
     `Awaiting Approval → Declined` (with reason), and the same slot check on approve.
   - `reservations.after_hours` (bool, set at insert when the session + buffer ends after close).
2. **Availability:** for a date, slots run from `early_start_hour` to the last start whose
   session + buffer ends by `latest_end_hour`; each slot carries `kind` = early | regular and
   `afterHours` flag. Travel rule unchanged.
3. **Quote:** single and mixed quotes check per-service `max_per_package`, and that the session
   + buffer fits between `early_start_hour` and `latest_end_hour` (replaces today's
   open–close check, keeping its friendly "book up to N" message).
4. **Booking form:** step 1 adds "Package with different services" (add person rows; each row:
   service + add-ons; counts limited by admin). Step 2 time list groups Early / Regular and
   labels after-hours times. Early choice ⇒ submit button reads "Send early booking request".
5. **Admin:** Appointments shows "Early request — waiting for your approval" with Approve /
   Decline (reason). Details lists every person and service. Settings and Catalog get the new
   fields.
6. **Customer:** My appointments shows request status; on approval, a "Pay down payment"
   button (Xendit) and an email with the link; the 15-minute countdown is shown.

## Owner decisions (from chat)

- Early booking is chosen in the time picker and must be confirmed by the artist/admin.
- After-hours fee is negotiated between artist and customer, not computed.
- Admin sets per service how many may appear in one package; owner's example: Bridal once,
  Soft Glam and Glamour any number (e.g. 1 Bridal + 2 Soft Glam + 3 Glamour).
- Unpaid for 15 minutes ⇒ the temporary hold on the time is released.

- **A request does NOT hold the time** while it waits for approval. Others may book or request
  the same time; when the admin approves, the time is checked again and approval is refused if
  it is no longer free (the admin then declines or offers another time).
- **Early and after-hours limits are whatever the owner/admin sets** in Settings; the system
  imposes no fixed hours.
- **Once the admin accepts an early request, its whole time is blocked at once** for every other
  booking, regular ones included. Example: an accepted early request from 4:00 AM to 1:00 PM
  blocks 4:00 AM–1:00 PM (plus its preparation buffer), so no regular booking can take 8:00 AM–
  1:00 PM that day. The block starts at acceptance (status Pending Payment); if the customer then
  does not pay within 15 minutes it is released, and once paid it stays (Confirmed).

## Builder calls (marked; correct if wrong)

- **Pay after approval** for early requests (owner agreed); no refunds needed on decline.
- **Statuses:** new `Awaiting Approval`; a declined request reuses the existing `Rejected`
  status with the admin's reason. Requests insert with no payment deadline; approval moves
  them to `Pending Payment` with `payment_expires_at = now + 15 min`, creates the Xendit
  invoice then (checkout 14 min), and if Xendit fails the booking stays `Pending Payment` and
  the existing "Pay" retry link is used. A request left unanswered for 7 days, or whose time has
  passed, is closed automatically by the existing expiry job (`Rejected`, "The artist could not
  confirm this early time.").
- **Approval re-checks** slot overlap and the travel rule; Admin and SuperAdmin may approve
  ("artist/admin").
- **Initial values:** earliest early start = opening hour and latest end = closing hour, so
  nothing changes until the admin sets them; validated `early ≤ open < close ≤ latest ≤ 24`,
  no crossing midnight. Bridal's per-package limit is set to 1 on release (owner's example);
  others blank (no limit).
- **After-hours** means the makeup session ends after closing; a booking can be both early and
  after-hours. The "group too long" check uses the full early-to-latest window.
- **Mixed pricing:** each person pays their service's regular price plus add-ons (as the demo
  does today); the down payment is the usual percent of the total. The same-service group
  rate stays only on single-service group bookings; `max_per_package` applies to mixed
  packages. Total people capped at the existing 20.
- **Schema:** live has no mixed tables (only `reservation_addons`), so 0020 adds
  `reservation_people` (+ per-person add-ons). The existing `PreparedPersistentBooking` mixed
  read model is reused where it fits; the old `makeup` schema path is not extended.
- Single-service bookings can also be early requests and after-hours. Reschedule, the ₱200
  cancellation fee and the 24-hour cutoff apply to packages as to single bookings.
- **Notifications:** in-app notification plus email through the existing Brevo sender for
  approval (with the payment link) and decline.
- **Rollout:** 0020 is additive (new columns default to today's behaviour), applied before the
  code deploy; the demo store mirrors requests and approval so the demo works end to end.
- The 15-minute hold applies to every unpaid Xendit booking, including approved requests.

## Left to the builder

Exact layout of the person rows and time groups, email wording, test fixtures, and how the
demo store mirrors the new fields.
