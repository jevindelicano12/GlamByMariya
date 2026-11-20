using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GlamByMariya.Infrastructure.Data;

// Read projections join the original bigint-keyed tables to stable UUID lookup
// keys. The original primary/foreign keys and all pre-existing rows remain intact.
public sealed class PublicMakeupDbContext(DbContextOptions<PublicMakeupDbContext> options) : MakeupDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("public");
        var views=new Dictionary<Type,string>
        {
            [typeof(UserRow)]="mvc_users",[typeof(RoleRow)]="mvc_roles",[typeof(CategoryRow)]="catalog_categories",
            [typeof(ServiceRow)]="mvc_services",[typeof(AddonRow)]="makeup_addons",[typeof(AreaRow)]="service_areas",
            [typeof(SettingsRow)]="business_settings",[typeof(MethodRow)]="payment_methods",[typeof(AssetRow)]="media_assets",
            [typeof(ImageRow)]="mvc_catalog_images",[typeof(BookingRow)]="mvc_booking_summary",[typeof(BookingAddonRow)]="mvc_booking_addons",
            [typeof(PaymentRow)]="mvc_payments",[typeof(NotificationRow)]="notifications",[typeof(AuditRow)]="audit_logs",
            [typeof(BlockRow)]="mvc_blocks",[typeof(RescheduleRow)]="mvc_reschedules",[typeof(ClosureRow)]="mvc_closures",
            [typeof(RefundRow)]="refunds",[typeof(PublicArtistRow)]="makeup_artists",[typeof(PublicWeeklyHoursRow)]="artist_availability"
        };
        foreach(var (type,name) in views) model.Entity(type).ToView(name,"public");
        model.Entity<GlamByMariya.Infrastructure.Services.InvitationRow>().ToTable("admin_invitations", "public");
        foreach(var entity in model.Model.GetEntityTypes())
            foreach(var property in entity.GetProperties())
            {
                property.SetColumnName(System.Text.RegularExpressions.Regex.Replace(property.Name,"([a-z0-9])([A-Z])","$1_$2").ToLowerInvariant());
                if(property.ClrType==typeof(decimal) || property.ClrType==typeof(decimal?)) { property.SetPrecision(14); property.SetScale(2); }
            }
        // The booking pin is numeric(9,6) (0013), not money.
        model.Entity<BookingRow>().Property(x=>x.LocationLat).HasPrecision(9,6);
        model.Entity<BookingRow>().Property(x=>x.LocationLng).HasPrecision(9,6);
    }
}
public sealed class PublicArtistRow { public long Id { get; set; } public Guid AppId { get; set; } public string FullName { get; set; }=""; public bool IsActive { get; set; } }
public sealed class PublicWeeklyHoursRow { public long Id { get; set; } public long ArtistId { get; set; } public int Weekday { get; set; } public TimeOnly StartTime { get; set; } public TimeOnly EndTime { get; set; } }

public sealed class PublicMakeupDesignFactory : IDesignTimeDbContextFactory<PublicMakeupDbContext>
{
    public PublicMakeupDbContext CreateDbContext(string[] args)
    {
        // Scaffolding builds the model only and never needs to contact Supabase.
        return new(new DbContextOptionsBuilder<PublicMakeupDbContext>().UseNpgsql("Host=127.0.0.1;Database=design_only;Username=design_only").Options);
    }
}
