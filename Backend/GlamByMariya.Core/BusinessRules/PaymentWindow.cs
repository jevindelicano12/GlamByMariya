using GlamByMariya.Core.Entities;

namespace GlamByMariya.Core.BusinessRules;

// An unpaid booking holds its time slot for at most 15 minutes. After that it
// no longer blocks other customers and can no longer be confirmed.
public static class PaymentWindow
{
    public const string ExpiredStatus = "Expired";
    public static readonly TimeSpan Length = TimeSpan.FromMinutes(15);

    public static DateTime DueAt(DateTime createdAt) => createdAt.Add(Length);

    public static bool IsExpired(Booking booking, DateTime now) =>
        booking.Status == "Pending Payment" && booking.PaymentDueAt is { } due && now >= due;
}
