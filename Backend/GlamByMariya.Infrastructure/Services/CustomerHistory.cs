using GlamByMariya.Core.Entities;

namespace GlamByMariya.Infrastructure.Services;

// Admin → Customers. "Time of booking" is when the customer made a booking (not the appointment date);
// "Customer since" is when the account was created. Times come from the store; none are invented.
public sealed record CustomerListRow(AppUser Customer, Booking? Latest, DateTime? LatestBookedAt, DateTime? Since);
public sealed record CustomerBookingRow(Booking Booking, DateTime? BookedAt);
public sealed record CustomerDetail(AppUser Customer, DateTime? Since, decimal TotalPaid, IReadOnlyList<CustomerBookingRow> Bookings);

public static class CustomerHistory
{
    public static IReadOnlyList<CustomerListRow> List(IEnumerable<AppUser> users, IEnumerable<Booking> bookings,
        IReadOnlyDictionary<Guid, DateTime> bookedAt, IReadOnlyDictionary<Guid, DateTime> since, string? search, string? sort)
    {
        var byCustomer = bookings.ToLookup(b => b.CustomerId);
        var text = search?.Trim();
        var rows = users.Where(u => u.Role == Roles.Customer)
            .Where(u => string.IsNullOrEmpty(text) || u.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || u.Email.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Select(u =>
            {
                var latest = Newest(byCustomer[u.Id], bookedAt).FirstOrDefault();
                return new CustomerListRow(u, latest, latest != null && bookedAt.TryGetValue(latest.Id, out var at) ? at : null,
                    since.TryGetValue(u.Id, out var joined) ? joined : null);
            });
        return (sort == "since"
            ? rows.OrderBy(r => r.Since == null).ThenByDescending(r => r.Since).ThenBy(r => r.Customer.Name)
            : rows.OrderBy(r => r.Latest == null).ThenByDescending(r => r.LatestBookedAt ?? DateTime.MinValue)
                .ThenByDescending(r => r.Latest?.Start).ThenBy(r => r.Customer.Name)).ToList();
    }

    public static CustomerDetail? Detail(Guid id, IEnumerable<AppUser> users, IEnumerable<Booking> bookings,
        IReadOnlyDictionary<Guid, DateTime> bookedAt, IReadOnlyDictionary<Guid, DateTime> since)
    {
        var customer = users.FirstOrDefault(u => u.Id == id && u.Role == Roles.Customer);
        if (customer == null) return null;
        var mine = Newest(bookings.Where(b => b.CustomerId == id), bookedAt).ToList();
        return new CustomerDetail(customer, since.TryGetValue(id, out var joined) ? joined : null,
            mine.Sum(b => Math.Max(0, b.VerifiedPaid - b.Refunded)),
            mine.Select(b => new CustomerBookingRow(b, bookedAt.TryGetValue(b.Id, out var at) ? at : null)).ToList());
    }

    // Newest time of booking first; bookings without a recorded time follow, latest appointment first.
    private static IEnumerable<Booking> Newest(IEnumerable<Booking> bookings, IReadOnlyDictionary<Guid, DateTime> bookedAt) =>
        bookings.OrderBy(b => !bookedAt.ContainsKey(b.Id))
            .ThenByDescending(b => bookedAt.TryGetValue(b.Id, out var at) ? at : DateTime.MinValue)
            .ThenByDescending(b => b.Start);
}
