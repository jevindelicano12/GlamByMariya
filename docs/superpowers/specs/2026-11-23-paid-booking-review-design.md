# Paid booking review, refund rules and test fixes — short design

Date: 2026-11-23. Decided by the owner in chat (preview shown and approved).

## Intent
A Xendit payment no longer means the artist has accepted the booking. The artist reviews every paid
booking and can still reject it. Customers get clear refund rules, and early requests whose time is
taken are closed automatically so nobody waits for nothing.

## Rules
- Paid on Xendit: the booking holds its time (database status stays `Confirmed`) and shows
  "Paid · waiting for the artist" until the artist accepts. New column `reservations.artist_accepted_at`.
- Admin **Confirm booking** sets `artist_accepted_at`. Admin **Reject** is the existing business
  cancellation (SuperAdmin, reason required): full refund, no ₱200 fee.
- No action within 24 hours of payment: the booking counts as accepted at payment + 24 h (computed;
  no background job).
- Customer cancels a paid booking: free before acceptance and within 1 hour after acceptance;
  ₱200 is deducted after that. Customers cannot cancel online within 3 hours of the start
  (was 24 hours). Unpaid bookings and early requests stay free.
- When a booking is paid, every `Awaiting Approval` early request overlapping its time (plus
  preparation) is rejected with "This time is already booked. Please choose another date or time."
- Refunds stay manual: the owner records the sent refund (amount + reference) and the customer gets
  an email "Your refund of ₱X has been sent (reference …)". The rejection also emails the customer.

## Approaches
1. Keep `Confirmed` and add an acceptance timestamp (chosen): every slot, travel, reschedule and
   finance rule keeps working; only the fee/cutoff rules and the Xendit-paid function change.
2. New status "Awaiting Confirmation": cleaner name, but ~10 database functions list statuses and
   would all need rewriting, with a high regression risk right before the demo.

## Changes
- Migration 0022: `artist_accepted_at` (+ backfill existing confirmed/finished bookings as accepted),
  `mvc_booking_summary` exposes it and the first paid time; `mvc_reservation_guard` 3-hour cutoff and
  set-once acceptance by staff on a `Confirmed` booking; `mvc_reservation_event` fee rule;
  `mvc_xendit_paid` rejects overlapping early requests.
- C#: `CancellationPolicy` (3 h cutoff, accepted/1 h rule), `Booking.ArtistAcceptedAt/PaidAt`,
  admin Confirm/Reject panel (replaces the manual proof review for Xendit bookings), overview notice,
  customer status text, refund and rejection emails, demo parity.

## Fixes from the live test (same branch)
1. Unknown pages show a friendly 404 page instead of a blank one.
2. Wrong party size says "Choose 1–20 people." instead of an add-on message.
3. Early request confirmation says the down payment is paid after approval, not "now".
4. Early request payment status reads "Not due yet" instead of "Pending Verification".
5. Sales Reports with "From" after "To" shows a message instead of a blank 400 page.
6. (covered above) No manual proof review on Xendit bookings.
