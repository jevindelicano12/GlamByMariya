using GlamByMariya.Core.Entities;

namespace GlamByMariya.Web.ViewModels;

// One payment on the customer's Payment transactions page. At is the exact time used for sorting:
// when the payment was verified, or when the booking (and its payment) was submitted.
public sealed record CustomerPaymentRow(Payment Payment, Booking Booking, DateTime? At)
{
    public string Purpose => Payment.Method == "Cash" && Payment.Reference.StartsWith("CASH-", StringComparison.Ordinal) ? "Balance" : "Down payment";

    public (string Label, string Tone) Status =>
        Payment.Verified ? ("Paid", "paid")
        : Payment.ReviewStatus == "Correction Requested" ? ("Correction requested", "attention")
        : Booking.Status is "Expired" or "Cancelled" or "Rejected" ? ("Not completed", "closed")
        : ("Pending", "pending");

    // Refund details belong to the booking, so they are shown on its down payment row only.
    public string? RefundNote =>
        Purpose != "Down payment" || Booking.RefundDue <= 0 ? null
        : Booking.Refunded >= Booking.RefundDue ? $"Refunded ₱{Booking.Refunded:N2}"
        : Booking.Refunded > 0 ? $"Refunded ₱{Booking.Refunded:N2} of ₱{Booking.RefundDue:N2}"
        : $"Refund pending ₱{Booking.RefundDue:N2}";
}
