# Flexible down payment (Phase 7) — short design

Date: 2026-11-22. Approved by the owner in chat ("Aprubado").

## Intent
The customer chooses how much to pay online: at least the down payment (50%, the owner's
setting) and at most the full booking total. Today the live site always charges exactly 50%.

## Rules
- Allowed: `required down payment <= amount <= final total`, in pesos with at most two decimals.
- Refusal copy: "Enter a down payment between ₱{required:N2} and ₱{total:N2}."
- The payment step offers: "Pay the {percent}% down payment" (default), "Pay in full", and
  "Another amount" (editable, same limits).
- Xendit charges the chosen amount (new booking, package, and "Pay down payment" after an
  early request is approved). The balance is still collected after the appointment.
- Unchanged: confirmation needs at least the down payment; completion records only the remaining
  cash balance; cancellation refunds verified payments minus the ₱200 fee.
- `payment_adjustments` is a different feature (admin corrections) and is not touched.

## Approaches
1. Choose the amount on our payment step, invoice that amount (chosen).
2. Let Xendit ask for the amount — not possible: a Xendit invoice has a fixed amount.

## Changes
- `Money.RequireDeposit(amount, required, total)` replaces the exact-amount check (C#, 5 places).
- Migration 0021: `mvc_payment_guard` accepts a down payment from the required amount up to the total.
- Xendit invoices use the recorded down-payment amount.
- Booking form: the three choices are shown on the live site too (`data-deposit-choice`).

## Tests
- `--flexible-deposit-only`: minimum, full and in-between accepted; below minimum and above total
  refused with the copy above; single, package and early request keep the chosen amount.
- DatabaseChecks `--flexible`: the guard accepts full and in-between, refuses below/above.
