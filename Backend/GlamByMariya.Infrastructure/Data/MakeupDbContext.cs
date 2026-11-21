using Microsoft.EntityFrameworkCore;

namespace GlamByMariya.Infrastructure.Data;

// Explicit database read models keep the existing MVC/domain models unchanged.
// Writes use parameterized SQL commands because the schema's triggers own
// snapshots, generated prices, status histories and reservation synchronization.
public class MakeupDbContext : DbContext
{
    public MakeupDbContext(DbContextOptions<MakeupDbContext> options) : base(options) { }
    protected MakeupDbContext(DbContextOptions options) : base(options) { }
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("makeup");
        model.Entity<UserRow>().ToTable("app_users");
        model.Entity<RoleRow>().ToTable("roles");
        model.Entity<CategoryRow>().ToTable("catalog_categories");
        model.Entity<ServiceRow>().ToTable("makeup_services");
        model.Entity<AddonRow>().ToTable("makeup_addons");
        model.Entity<AreaRow>().ToTable("service_areas");
        model.Entity<SettingsRow>().ToTable("business_settings");
        model.Entity<MethodRow>().ToTable("payment_methods");
        model.Entity<AssetRow>().ToTable("media_assets");
        model.Entity<ImageRow>().ToTable("catalog_images");
        model.Entity<BookingRow>().ToView("booking_financial_summary");
        model.Entity<BookingRow>().Ignore(x => x.LocationLat).Ignore(x => x.LocationLng).Ignore(x => x.IsEarlyRequest).Ignore(x => x.AfterHours);
        model.Entity<ServiceRow>().Ignore(x => x.MaxPerPackage);
        model.Entity<BookingAddonRow>().ToTable("booking_addons");
        model.Entity<PaymentRow>().ToTable("payments");
        model.Entity<NotificationRow>().ToTable("notifications");
        model.Entity<AuditRow>().ToTable("audit_logs");
        model.Entity<BlockRow>().ToView("availability_blocks");
        model.Entity<RescheduleRow>().ToTable("reschedule_requests");
        model.Entity<ClosureRow>().ToTable("booking_closures");
        model.Entity<RefundRow>().ToTable("refunds");
        foreach (var entity in model.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(System.Text.RegularExpressions.Regex.Replace(property.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant());
                if (property.ClrType == typeof(decimal) || property.ClrType == typeof(decimal?))
                { property.SetPrecision(14); property.SetScale(2); }
            }
        }
    }
}

public class RoleRow { public Guid Id { get; set; } public string Name { get; set; } = ""; }
public class UserRow
{
    public Guid Id { get; set; } public Guid RoleId { get; set; }
    public string FirstName { get; set; } = ""; public string LastName { get; set; } = "";
    public short AgeAtRegistration { get; set; } public string Gender { get; set; } = "";
    public string Address { get; set; } = ""; public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = ""; public bool IsActive { get; set; }
    public Guid SecurityStamp { get; set; } public DateTime? LockedUntil { get; set; }
}
public class CategoryRow { public Guid Id { get; set; } public string Name { get; set; } = ""; }
public class ServiceRow
{
    public Guid Id { get; set; } public Guid CategoryId { get; set; }
    public string Name { get; set; } = ""; public string Description { get; set; } = "";
    public decimal Price { get; set; } public int Minutes { get; set; }
    public decimal? GroupPrice { get; set; } public short GroupMinimumPeople { get; set; }
    // Public schema only (0020): most per package booking; the legacy makeup schema ignores it.
    public int? MaxPerPackage { get; set; }
    public string VisualWord { get; set; } = ""; public bool IsActive { get; set; } public bool IsArchived { get; set; }
}
public class AddonRow
{
    public Guid Id { get; set; } public Guid CategoryId { get; set; }
    public string Name { get; set; } = ""; public string Description { get; set; } = "";
    public decimal Price { get; set; } public int Minutes { get; set; }
    public string VisualWord { get; set; } = ""; public bool IsActive { get; set; } public bool IsArchived { get; set; }
}
public class AreaRow { public Guid Id { get; set; } public string Name { get; set; } = ""; public bool IsActive { get; set; } }
public class SettingsRow
{
    public short Id { get; set; } public string TimeZone { get; set; } = "Asia/Manila";
    public short OpenHour { get; set; } public short CloseHour { get; set; } public int BufferMinutes { get; set; }
    public decimal TravelFeeInformational { get; set; } public decimal DownPaymentPercent { get; set; } = 50; public string PaymentInstructions { get; set; } = "";
    public string PolicyVersion { get; set; } = ""; public string TravelReminderVersion { get; set; } = "";
}
public class MethodRow
{
    public Guid Id { get; set; } public string Name { get; set; } = "";
    public bool IsActive { get; set; } public bool AcceptsDeposits { get; set; }
}
public class AssetRow { public Guid Id { get; set; } public string ObjectKey { get; set; } = ""; public string ContentType { get; set; } = ""; }
public class ImageRow { public Guid Id { get; set; } public Guid? ServiceId { get; set; } public Guid? AddonId { get; set; } public string Slot { get; set; } = ""; public Guid AssetId { get; set; } }
public class BookingRow
{
    public string Reference { get; set; } = "";
    public Guid Id { get; set; } public Guid CustomerId { get; set; } public Guid ArtistId { get; set; }
    public string CustomerNameSnapshot { get; set; } = ""; public Guid ServiceId { get; set; }
    public string ServiceNameSnapshot { get; set; } = ""; public decimal RegularPriceSnapshot { get; set; }
    public int ServiceMinutesSnapshot { get; set; } public decimal? GroupPriceSnapshot { get; set; }
    public short GroupMinimumSnapshot { get; set; } public string BookingType { get; set; } = "";
    public short People { get; set; } public decimal AppliedPricePerPerson { get; set; }
    public decimal FinalTotal { get; set; } public decimal RequiredDownPayment { get; set; } public decimal DownPaymentPercentSnapshot { get; set; } public decimal RequiredDownPaymentSnapshot { get; set; }
    public int DurationMinutes { get; set; } public DateTime StartsAt { get; set; } public DateTime EndsAt { get; set; }
    public int BufferMinutesSnapshot { get; set; } public string TimeZoneSnapshot { get; set; } = "Asia/Manila";
    public string Address { get; set; } = ""; public string AreaNameSnapshot { get; set; } = "";
    public decimal TravelFeeInformationalSnapshot { get; set; } public string Status { get; set; } = "";
    public DateTime? TransportationPolicyAcknowledgedAt { get; set; }
    public string Note { get; set; } = ""; public decimal VerifiedPaid { get; set; }
    public string PaymentStatus { get; set; } = ""; public DateTime? RequestedStart { get; set; }
    public decimal CancellationFee { get; set; } public decimal RefundDue { get; set; } public decimal Refunded { get; set; }
    // Only the public schema's mvc_booking_summary carries the pin (0013); the makeup schema ignores it.
    public decimal? LocationLat { get; set; } public decimal? LocationLng { get; set; }
    // Public schema only (0020): early request and after-hours flags.
    public bool IsEarlyRequest { get; set; } public bool AfterHours { get; set; }
}
public class BookingAddonRow { public Guid Id { get; set; } public Guid BookingId { get; set; } public Guid AddonId { get; set; } public string NameSnapshot { get; set; } = ""; public decimal PriceSnapshot { get; set; } public int MinutesSnapshot { get; set; } }
public class PaymentRow
{
    public Guid Id { get; set; } public Guid BookingId { get; set; } public Guid MethodId { get; set; }
    public decimal Amount { get; set; } public string Reference { get; set; } = "";
    public string Purpose { get; set; } = ""; public string Status { get; set; } = "";
    public Guid? ProofAssetId { get; set; } public Guid? VerifiedBy { get; set; } public DateTime? VerifiedAt { get; set; }
}
public class NotificationRow { public Guid Id { get; set; } public Guid UserId { get; set; } public string Message { get; set; } = ""; public DateTime CreatedAt { get; set; } }
public class AuditRow { public Guid Id { get; set; } public Guid? ActorId { get; set; } public string EntityType { get; set; } = ""; public string Event { get; set; } = ""; public DateTime CreatedAt { get; set; } }
public class BlockRow { public Guid Id { get; set; } public Guid ArtistId { get; set; } public DateTime FromAt { get; set; } public DateTime ToAt { get; set; } public string Reason { get; set; } = ""; }
public class RescheduleRow { public Guid Id { get; set; } public Guid BookingId { get; set; } public string Status { get; set; } = ""; }
public class ClosureRow { public Guid Id { get; set; } public Guid BookingId { get; set; } }
public class RefundRow { public Guid Id { get; set; } public Guid ClosureId { get; set; } public string TransferReference { get; set; } = ""; public DateTime SentAt { get; set; } }
