namespace GlamByMariya.Core.BusinessRules;

// Customer self-cancellation rules, shared by the Demo and database paths.
// - Pending Payment (not yet paid) or an early request awaiting approval: free, any time before the appointment starts.
// - Confirmed: fixed PHP 200 fee, allowed only until 24 hours before the start.
// - Anything else, or a Confirmed booking inside the cut-off: not allowed.
public static class CancellationPolicy
{
    public const decimal ConfirmedFee = 200m;
    public static readonly TimeSpan ConfirmedCutoff = TimeSpan.FromHours(24);

    public sealed record Decision(bool Allowed, decimal Fee, string? Reason);

    public static Decision Evaluate(string status, DateTime start, DateTime now)
    {
        if (start <= now)
            return new(false, 0, "This appointment has already started and can no longer be cancelled.");
        return status switch
        {
            "Pending Payment" => new(true, 0, null),
            "Awaiting Approval" => new(true, 0, null), // an early request the customer withdraws (nothing paid)
            "Confirmed" when start - now >= ConfirmedCutoff => new(true, ConfirmedFee, null),
            "Confirmed" => new(false, 0, "Cancellations close 24 hours before the appointment. Please contact Glam by Mariya."),
            _ => new(false, 0, "Only pending or confirmed appointments can be cancelled.")
        };
    }

    // The fee is only ever deducted from verified payments; it never creates a debt.
    public static decimal RefundDue(decimal fee, decimal verifiedPaid) => Math.Max(0, verifiedPaid - fee);
}
