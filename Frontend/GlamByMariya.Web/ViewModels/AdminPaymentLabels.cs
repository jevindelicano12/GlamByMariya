using GlamByMariya.Core.Entities;

namespace GlamByMariya.Web.ViewModels;

// Payment wording on the owner pages. An unpaid booking that expired, was cancelled or was
// rejected is "not paid", not a claim waiting for review. With Xendit on, an unpaid pending
// booking is waiting for the customer to finish checkout.
public static class AdminPaymentLabels
{
    public static string Booking(Booking b, bool online) =>
        b.PaymentStatus != "Pending Verification" ? b.PaymentStatus : Unpaid(b.Status, online) ?? b.PaymentStatus;

    public static string Payment(Payment p, Booking b, bool online) =>
        p.Verified ? "Verified" : Unpaid(b.Status, online) ?? "Unverified — awaiting review";

    // Bookings that no longer hold their time are left off the calendar counts.
    public static bool OnCalendar(Booking b) => b.Status is not ("Expired" or "Rejected" or "Cancelled");

    private static string? Unpaid(string status, bool online) => status switch
    {
        "Expired" => online ? "Not paid — checkout expired" : "Not paid — expired",
        "Cancelled" or "Rejected" => "Not paid",
        "Pending Payment" when online => "Waiting for Xendit payment",
        _ => null,
    };
}
