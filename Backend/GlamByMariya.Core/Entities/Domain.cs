namespace GlamByMariya.Core.Entities;

public class CatalogItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Price { get; set; }
    public decimal? GroupPrice { get; set; }
    public int GroupMinimumPeople { get; set; } = 4;
    public int Minutes { get; set; }
    public bool Active { get; set; } = true;
    public bool Archived { get; set; }
    public string Category { get; set; } = "Signature";
    public string VisualWord { get; set; } = "";
    // Most times this service may appear in one package booking; null = no limit.
    public int? MaxPerPackage { get; set; }
}
public class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? Suffix { get; set; }
    public DateOnly? Birthday { get; set; }
    public int Age { get; set; }
    public string Gender { get; set; } = "";
    public string Address { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "Customer";
    // When the account was created (Admin → Customers "Customer since"); null when the store does not keep it.
    public DateTime? CreatedAt { get; set; }
    public bool IsActive { get; set; } = true;
}
public record PriceSnapshot(Guid ItemId, string Name, decimal Price, int Minutes, int Quantity = 1);
public class Booking
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? StoredReference { get; set; }
    public string Reference => StoredReference ?? "LM-" + Id.ToString("N")[..8].ToUpperInvariant();
    // "Package" only when the group rate actually applied; a group below the minimum pays the regular rate.
    public string BookingType => MixedPeople.Count > 0 ? "Mixed-service" : Package != "group" ? "Individual"
        : GroupPrice.HasValue && People >= GroupMinimumPeople ? "Same-Service Package" : "Group booking (regular rate)";
    public string ServiceDescription => MixedPeople.Count > 0 ? "Mixed-service group" : Service.Name;
    public Guid CustomerId { get; set; }
    // When the customer made this booking ("Time of booking"); null when the store does not keep it.
    public DateTime? CreatedAt { get; set; }
    public string CustomerName { get; set; } = "";
    public PriceSnapshot Service { get; set; } = new(Guid.Empty, "", 0, 0);
    public List<PriceSnapshot> AddOns { get; set; } = [];
    public IReadOnlyList<MixedPersonQuote> MixedPeople { get; init; } = Array.Empty<MixedPersonQuote>();
    public Guid? MixedSubmissionId { get; init; }
    public MixedSubmissionSnapshot? MixedSubmission { get; init; }
    public DateTime Start { get; set; }
    public int? BufferMinutesSnapshot { get; set; }
    public int People { get; set; } = 1;
    public string Package { get; set; } = "auto";
    public decimal? GroupPrice { get; set; }
    public int GroupMinimumPeople { get; set; } = 4;
    public decimal AppliedPricePerPerson { get; set; }
    public DateTime End => Start.AddMinutes(MixedPeople.Count > 0 ? MixedPeople.Sum(x => x.DurationMinutes) : Service.Minutes * People + AddOns.Sum(x => x.Minutes * x.Quantity));
    public string Address { get; set; } = "";
    public string Area { get; set; } = "";
    // The customer's exact map pin (Metro Manila or Rizal); older bookings have none.
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    // Set when the SuperAdmin approved a reschedule that fails the travel rule ("I'm sure the artist can make it").
    public Guid? TravelOverrideBy { get; set; }
    public DateTime? TravelOverrideAt { get; set; }
    public decimal TravelFee { get; set; }
    public decimal EarlyFeeSnapshot { get; set; }
    public decimal LateFeeSnapshot { get; set; }
    public DateTime? TransportationPolicyAcknowledgedAt { get; set; }
    public decimal DownPaymentPercentSnapshot { get; set; } = 50;
    public decimal RequiredDownPaymentSnapshot { get; set; }
    public decimal OriginalTotal => MixedPeople.Count > 0 ? MixedPeople.Sum(x => x.Service.Price) : Service.Price * People;
    public decimal GroupDiscount => MixedPeople.Count > 0 ? 0 : (Service.Price - (AppliedPricePerPerson > 0 ? AppliedPricePerPerson : Service.Price)) * People;
    // TravelFee is retained as an informational/future snapshot, but is not collected online.
    public decimal InitialBookingTotal => OriginalTotal - GroupDiscount + (MixedPeople.Count > 0 ? MixedPeople.Sum(p => p.AddOns.Sum(a => a.Price * a.Quantity)) : AddOns.Sum(x => x.Price * x.Quantity)) + EarlyFeeSnapshot + LateFeeSnapshot;
    public decimal Total => ApprovedReschedules.LastOrDefault()?.Quote.ProposedTotal ?? InitialBookingTotal;
    public decimal RequiredDownPayment => RequiredDownPaymentSnapshot > 0 ? RequiredDownPaymentSnapshot : Money.Half(Total);
    public decimal BalanceAfterDeposit => Total - RequiredDownPayment;
    public decimal VerifiedPaid { get; set; }
    public decimal RemainingBalance => Math.Max(0, Total - VerifiedPaid);
    public string PaymentStatus { get; set; } = "Pending Verification";
    public string Status { get; set; } = "Pending Payment";
    // Flexible booking: an early request waits for the artist ("Awaiting Approval"); after-hours runs past closing.
    public bool IsEarlyRequest { get; set; }
    public bool AfterHours { get; set; }
    // When an unpaid Pending Payment booking stops holding its slot. Null means no deadline (older records).
    public DateTime? PaymentDueAt { get; set; }
    public string Note { get; set; } = "";
    public DateTime? RequestedStart { get; set; }
    public Guid? StaffCancelledBy { get; set; }
    public DateTime? StaffCancelledAt { get; set; }
    public string? StaffCancellationReason { get; set; }
    public Guid? CompletedBy { get; set; }
    public DateTime? CompletedAt { get; set; }
    public RescheduleQuote? RescheduleOffer { get; set; }
    public RescheduleConsent? PendingReschedule { get; set; }
    public List<RescheduleConsent> RescheduleConsents { get; } = [];
    public List<ApprovedReschedule> ApprovedReschedules { get; } = [];
    public decimal CancellationFee { get; set; }
    public decimal RefundDue { get; set; }
    public decimal Refunded { get; set; }
    public string RefundReference { get; set; } = "";
    public List<BookingHistoryEntry> History { get; } = [];
}
public class Payment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BookingId { get; set; }
    public decimal Amount { get; set; }
    public string Method { get; set; } = "";
    public string Reference { get; set; } = "";
    public string? ProofPath { get; set; }
    public string? ProofKey { get; set; }
    public bool Verified { get; set; }
    // Null means the store does not expose an authoritative review state.
    public string? ReviewStatus { get; set; }
    public Guid? VerifiedBy { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public int Revision { get; set; }
    public long? PersistentReviewRevision { get; set; }
    public string? PersistentReviewVersion { get; set; }
    public List<PaymentProofRevision> ProofHistory { get; } = [];
}
public record BookingHistoryEntry(Guid ActorId, string ActorName, string Action, string Reason, DateTime At, string Status, DateTime Start, DateTime? RequestedStart);
public record RescheduleQuote(Guid Id, Guid BookingId, int BookingRevision, DateTime OriginalStart, DateTime ProposedStart, int DurationMinutes, int BufferMinutes, decimal InitialTotal, decimal CurrentTotal, decimal ProposedTotal, decimal EarlyFee, decimal LateFee);
public record RescheduleConsent(RescheduleQuote Quote, Guid CustomerId, DateTime ConsentedAt);
public record ApprovedReschedule(RescheduleQuote Quote, RescheduleConsent Consent, Guid ApprovedBy, DateTime ApprovedAt);
public record PaymentProofRevision(string Reference, string? ProofPath, string? ProofKey, DateTime ChangedAt);
public record Notification(Guid UserId, string Message, DateTime Created);
public record AuditLog(Guid ActorId, string Event, DateTime Created);
public record DemoAdminActivity(Guid ActorId, string ActorName, string Role, string Action, string TargetType, Guid TargetId, DateTimeOffset OccurredAt);
public record AvailabilityBlock(Guid Id, DateTime From, DateTime To, string Reason);
public record ScheduleWindow(DateTime From, DateTime To);
public class BusinessSettings
{
    public string TimeZone { get; set; } = "Asia/Manila";
    public string PolicyVersion { get; set; } = "booking-v1";
    public string TravelReminderVersion { get; set; } = "travel-v1";
    public int OpenHour { get; set; } = 8;
    public int CloseHour { get; set; } = 18;
    public bool EarlyFeeEnabled { get; set; }
    public int EarlyStartHour { get; set; }
    public decimal EarlyFee { get; set; }
    public bool LateFeeEnabled { get; set; }
    public decimal LateFee { get; set; }
    public int? LateStartHour { get; set; }
    public int LateEndHour { get; set; } = 24;
    public int BufferMinutes { get; set; } = 45;
    public decimal TravelFee { get; set; } = 250;
    public decimal DownPaymentPercent { get; set; } = 50;
    // Travel rule (Phase 5): average speed between bookings and the minimum travel between two places.
    public decimal TravelSpeedKmh { get; set; } = 15;
    public int TravelMinimumMinutes { get; set; } = 30;
    // Flexible booking (Phase 6): earliest early-request start and latest booking end; null = opening/closing hour.
    public int? EarlyRequestStartHour { get; set; }
    public int? AfterHoursEndHour { get; set; }
    public string ServiceAreas { get; set; } = "Quezon City,Makati,Manila,Pasig,Taguig,Mandaluyong";
    public string PaymentInstructions { get; set; } = "Demo only — do not send real money. Contact the artist for verified GCash, Maya or bank account details.";
}

// The artist's home pin (where the day starts) and current pin (none = at home), Asia/Manila local update time.
public sealed record ArtistLocation(decimal? HomeLat, decimal? HomeLng, decimal? CurrentLat, decimal? CurrentLng, DateTime? CurrentUpdatedAt);
