namespace GlamByMariya.Core.Entities;

public static class Money
{
    public static decimal Round(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
    public static decimal Half(decimal total) => Round(total * 0.5m);
    public static decimal Percentage(decimal total, decimal percent) => Round(total * percent / 100m);
    // Phase 7: the customer pays at least the down payment and at most the booking total (same rule
    // as the database payment guard); the Xendit invoice charges the chosen amount.
    public static string DepositRangeMessage(decimal required, decimal total) => $"Enter a down payment between ₱{required:N2} and ₱{total:N2}.";
    public static void RequireDeposit(decimal amount, decimal required, decimal total)
    {
        if (Round(amount) != amount || amount < required || amount > total)
            throw new InvalidOperationException(DepositRangeMessage(required, total));
    }
}

public record BookingQuote(string Service, int People, string Package, decimal RegularPrice,
    decimal? GroupPrice, int GroupMinimumPeople, decimal AppliedPricePerPerson, decimal OriginalTotal,
    decimal GroupDiscount, decimal ExtrasTotal, decimal TravelFee, decimal FinalTotal,
    decimal RequiredDownPayment, decimal RemainingBalance, decimal DownPaymentPercent, int DurationMinutes, decimal EarlyFee = 0, decimal LateFee = 0);

public record MixedPersonSelection(Guid ServiceId, IReadOnlyList<Guid> AddOnIds);
public record MixedPersonQuote(PriceSnapshot Service, IReadOnlyList<PriceSnapshot> AddOns, decimal Total, int DurationMinutes);
public record MixedBookingQuote(IReadOnlyList<MixedPersonQuote> People, decimal ServiceTotal, decimal AddOnTotal,
    decimal GroupDiscount, decimal EarlyFee, decimal LateFee, decimal FinalTotal, decimal RequiredDownPayment,
    decimal DownPaymentPercent, int DurationMinutes);
public record MixedDateAlternatives(IReadOnlyList<AvailableDate> Dates, DateTime CheckedThrough, int DaysChecked);
// Phase 6: packages carry the customer's pin so times include the artist's travel.
public record MixedAvailabilityRequest(MixedPersonSelection[] People, string Month, decimal? Latitude = null, decimal? Longitude = null);
public record MixedAlternativesRequest(MixedPersonSelection[] People, string After);
public record MixedSlotRequest(MixedPersonSelection[] People, string Start, decimal? Latitude = null, decimal? Longitude = null);
public record MixedSubmissionSnapshot(int BufferMinutes, decimal EarlyFee, decimal LateFee,
    decimal Total, decimal Minimum, decimal ClaimedAmount, string Method, string Reference, DateTime SubmittedAt,
    string PolicyVersion, string TravelReminderVersion, string RequestFingerprint);
