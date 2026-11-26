using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Services;

namespace GlamByMariya.Infrastructure.Data;

// Request-scoped read models in PostgreSQL mode; singleton lists only in Demo mode.
// Database writes are explicit commands, never inferred from edits to these lists.
public interface IStudioStore : IServiceRepository, IAddOnRepository, IBookingRepository, IPaymentRepository
{
    object Gate { get; }
    bool IsPersistent { get; }
    DateTime Now { get; }
    List<AppUser> Users { get; }
    // Admin → Customers: when each booking was made and each customer account was created (business time).
    // Stores that do not record these return none, and the page shows no time rather than a guess.
    IReadOnlyDictionary<Guid, DateTime> BookingCreatedTimes() =>
        Bookings.Where(b => b.CreatedAt.HasValue).ToDictionary(b => b.Id, b => b.CreatedAt!.Value);
    IReadOnlyDictionary<Guid, DateTime> CustomerCreatedTimes() =>
        Users.Where(u => u.CreatedAt.HasValue).ToDictionary(u => u.Id, u => u.CreatedAt!.Value);
    List<Notification> Notifications { get; }
    List<AuditLog> AuditLogs { get; }
    // Owner → Audit Logs: the newest audit rows (database mode reads more than the 100 in AuditLogs).
    IReadOnlyList<AuditLog> RecentAuditLogs(int max) => AuditLogs.OrderByDescending(x => x.Created).Take(max).ToList();
    List<AvailabilityBlock> Blocks { get; }
    BusinessSettings Settings { get; }
    AppUser AddUser(string firstName, string lastName, string email, string password, string role = "Customer", int age = 0, string gender = "", string address = "");
    // Customer registration after the email code: the password was validated and hashed before it was held.
    AppUser AddCustomerWithPasswordHash(string firstName, string lastName, string email, string passwordHash, int age, string gender, string address, DateOnly? birthday = null, string? suffix = null);
    // Elevated roles only (Admin, SuperAdmin). Never called with Customer and
    // never with browser-supplied role input; callers are bootstrap/invite flows.
    AppUser CreateElevatedUser(string firstName, string lastName, string email, string password, string role, int age, string gender, string address);
    void SaveCatalog(string kind, CatalogItem item, Guid actor);
    void DeleteCatalog(string kind, CatalogItem item, Guid actor);
    void SaveSettings(BusinessSettings settings, Guid actor);
    void AddBlock(AvailabilityBlock block, Guid actor);
    void RemoveBlock(Guid id, Guid actor);
    void RecordAudit(Guid actor, string message);
    // SuperAdmin-only Disable/Enable of an Admin account. Never a SuperAdmin or a customer.
    void SetAdminActive(Guid admin, bool active, Guid actor);
    // SuperAdmin-only (Admin → User accounts). Edits the profile of a Customer or Admin, or turns the
    // account off/on. "Delete" is this soft switch so bookings and payments keep their owner.
    // Never targets a SuperAdmin.
    void UpdateUserByOwner(Guid user, string firstName, string lastName, int age, string gender, string address, Guid actor);
    void SetUserActive(Guid user, bool active, Guid actor);
    // Permanent delete of a Customer or Admin with no booking/payment history. Accounts with
    // history are refused (deactivate them instead) so past records keep their owner.
    void DeleteUser(Guid user, Guid actor);
    // One booking by id (customers only see their own in database mode). Use this
    // instead of scanning Bookings, which loads every reservation in database mode.
    Booking? FindBooking(Guid id);
    // Travel rule (Phase 5): the artist's home and current pins. Pins must be inside Metro Manila or Rizal;
    // SetArtistCurrent(null, null) clears the current pin (the artist is back home).
    ArtistLocation GetArtistLocation();
    void SetArtistHome(decimal lat, decimal lng);
    void SetArtistCurrent(decimal? lat, decimal? lng);
    IReadOnlyList<CustomerAddress> ListCustomerAddresses(Guid customerId) => [];
    void SaveCustomerAddress(Guid customerId, string addressText, string area, decimal? latitude = null, decimal? longitude = null) { }
    // Bookings holding a slot between from and until (Asia/Manila local), in start order, without exclude.
    IReadOnlyList<TravelAppointment> TravelStops(DateTime from, DateTime until, Guid? exclude);
    // InProgress or Completed bookings between from and until (Asia/Manila local): where the artist already is today.
    IReadOnlyList<TravelAppointment> FinishedStops(DateTime from, DateTime until);
}

public interface IPersistentStudioStore : IStudioStore
{
    Booking SubmitBooking(Guid customer, Guid service, Guid[] extras, DateTime start, string address, string area,
        decimal deposit, string method, string reference, string? proof, bool agreed, bool travelAcknowledged, int people, string package,
        decimal? expectedTotal = null, decimal? expectedMinimum = null, decimal? latitude = null, decimal? longitude = null);
    void CancelBooking(Guid booking, Guid customer, bool confirmed, string? reason = null);
    // Marks unpaid bookings whose 15-minute window ended as Expired; returns how many.
    int ExpireOverduePayments();
    // Xendit: link the deposit row to its invoice; apply webhook events (see 0010_xendit.sql).
    void AttachInvoice(Guid booking, string invoiceId, string checkoutUrl);
    string XenditPaid(string invoiceId, decimal amount, string eventKey);
    string XenditExpired(string invoiceId, string eventKey);
    // The Xendit payment page of a booking still waiting for its down payment, or null.
    string? PendingCheckoutUrl(Guid booking);
    // travelOverride: the SuperAdmin approves a reschedule that fails the travel rule (recorded on the booking).
    void ManageBooking(Guid booking, string action, Guid admin, string reason, decimal amount, string reference, Guid? paymentId = null, long? reviewRevision = null, string? reviewVersion = null, bool travelOverride = false);
    List<Payment> ReviewPayments(Guid booking);
    void RequestReschedule(Guid booking, Guid customer, DateTime start);
    void CorrectPayment(Guid booking, Guid customer, string reference, string? proof);
    string? CatalogImageKey(string kind, Guid id, string slot);
    void SaveCatalogImage(string kind, Guid id, string slot, byte[] bytes);
    void RemoveCatalogImage(string kind, Guid id, string slot);
    (byte[] Bytes, string ContentType)? ReadAsset(string? key);
}

