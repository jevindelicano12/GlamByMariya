namespace GlamByMariya.Core.BusinessRules;

// Customer self-cancellation rules, shared by the Demo and database paths.
// - Pending Payment (not yet paid) or an early request awaiting approval: free, any time before the appointment starts.
// - Confirmed: fixed PHP 200 fee, allowed only until 24 hours before the start.
// - Anything else, or a Confirmed booking inside the cut-off: not allowed.
public static class CancellationPolicy
{
    public const decimal ConfirmedFee = 200m;
    // Customers cannot cancel a confirmed appointment online within 3 hours of the start (was 24 hours).
    public static readonly TimeSpan ConfirmedCutoff = TimeSpan.FromHours(3);
    // A paid booking can be cancelled for free until 1 hour after the artist accepted it.
    public static readonly TimeSpan FreeAfterAcceptance = TimeSpan.FromHours(1);
    // Without the artist's action, a paid booking counts as accepted 24 hours after payment.
    public static readonly TimeSpan AutoAcceptAfterPayment = TimeSpan.FromHours(24);
    public const string CutoffMessage = "Cancellations close 3 hours before the appointment. Please contact Glam by Mariya.";

    public sealed record Decision(bool Allowed, decimal Fee, string? Reason);

    // When the artist accepted the booking, or (no action) 24 hours after the down payment was paid.
    public static DateTime? AcceptedAt(DateTime? artistAcceptedAt, DateTime? paidAt) =>
        artistAcceptedAt ?? paidAt?.Add(AutoAcceptAfterPayment);

    // acceptedAt: see AcceptedAt; null means nothing was paid or accepted, so no fee applies.
    public static Decision Evaluate(string status, DateTime start, DateTime now, DateTime? acceptedAt = null)
    {
        if (start <= now)
            return new(false, 0, "This appointment has already started and can no longer be cancelled.");
        return status switch
        {
            "Pending Payment" => new(true, 0, null),
            "Awaiting Approval" => new(true, 0, null), // an early request the customer withdraws (nothing paid)
            "Confirmed" when start - now < ConfirmedCutoff => new(false, 0, CutoffMessage),
            "Confirmed" => new(true, acceptedAt is { } accepted && now >= accepted + FreeAfterAcceptance ? ConfirmedFee : 0, null),
            _ => new(false, 0, "Only pending or confirmed appointments can be cancelled.")
        };
    }

    // The fee is only ever deducted from verified payments; it never creates a debt.
    public static decimal RefundDue(decimal fee, decimal verifiedPaid) => Math.Max(0, verifiedPaid - fee);
}
