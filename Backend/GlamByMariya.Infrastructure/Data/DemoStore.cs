using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using System.Security.Cryptography;

namespace GlamByMariya.Infrastructure.Data;

// Singleton demo implementation. All writes and consistent reads use Gate.
// Replace these repository implementations with EF Core when persistence is required.
public class DemoStore : IStudioStore
{
    private readonly DemoCatalogSnapshot? catalogSnapshot;
    public bool IsPersistent => false;
    public DateTime Now => DateTime.Now;
    public object Gate { get; } = new();
    public List<CatalogItem> Services { get; } = [
        new() { Name = "Soft Glam", Description = "Fresh, luminous skin and softly defined eyes. Your everyday beauty, elevated.", Price = 600, GroupPrice = 525, GroupMinimumPeople = 4, Minutes = 60, Category = "The everyday edit" },
        new() { Name = "Glamour Makeup", Description = "Sculpted definition, statement eyes and a flawless finish that lasts all night.", Price = 750, Minutes = 90, Category = "The occasion edit" },
        new() { MaxPerPackage = 1, Name = "Bridal Makeup", Description = "An exquisite, camera-ready look made for your most unforgettable day. Priced for one bride.", Price = 1500, Minutes = 120, Category = "The bridal edit" }
    ];
    public List<CatalogItem> AddOns { get; } = [
        new() { Name = "Lash application", Description = "A little extra definition", Price = 250, Minutes = 15 },
        new() { Name = "Hair styling", Description = "Complete your look", Price = 800, Minutes = 45 },
        new() { Name = "Touch-up kit", Description = "Stay fresh throughout the day", Price = 350, Minutes = 0 }
    ];
    public List<Booking> Bookings { get; } = [];
    public List<Payment> Payments { get; } = [];
    public List<AppUser> Users { get; } = [];
    public List<Notification> Notifications { get; } = [];
    public List<AuditLog> AuditLogs { get; } = [];
    private readonly List<DemoAdminActivity> adminActivity = [];
    public IReadOnlyList<DemoAdminActivity> AdminActivity() { lock (Gate) return adminActivity.ToArray(); }
    public void RecordAdminActivity(Guid actor, string action, string targetType, Guid targetId)
    {
        lock (Gate)
        {
            var user = Users.SingleOrDefault(u => u.Id == actor && u.IsActive && u.Role is Roles.Admin or Roles.SuperAdmin);
            if (user != null) adminActivity.Add(new(actor, user.Name, user.Role, action, targetType, targetId, DateTimeOffset.UtcNow));
        }
    }
    public List<AvailabilityBlock> Blocks { get; } = [];
    public BusinessSettings Settings { get; } = new();
    public DemoStore(DemoCatalogSnapshot? catalogSnapshot = null, string[]? passwords = null, bool presentationAccounts = false)
    {
        this.catalogSnapshot = catalogSnapshot;
        if (catalogSnapshot != null)
        {
            var loaded = catalogSnapshot.LoadOrInitialize(Services, AddOns);
            Services.Clear(); Services.AddRange(loaded.Services);
            AddOns.Clear(); AddOns.AddRange(loaded.AddOns);
        }
        // No seeded accounts by default: create them like a real system (owner setup,
        // admin invite, customer registration). Presentation accounts are opt-in only.
        if (!presentationAccounts) return;
        passwords ??= Enumerable.Range(0, 3).Select(_ => Convert.ToHexString(RandomNumberGenerator.GetBytes(32))).ToArray();
        if (passwords.Length != 3 || passwords.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Three presentation account passwords are required.", nameof(passwords));
        AddUser("Demo", "Admin", "presentation-admin@example.test", passwords[0], "Admin", 35, "Others", "Address on file");
        AddUser("Demo", "Owner", "presentation-owner@example.test", passwords[1], "SuperAdmin", 35, "Others", "Address on file");
        var customer = AddUser("Demo", "Customer", "presentation-customer@example.test", passwords[2], "Customer", 28, "Others", "24 Sample Street, Demo Barangay");
        var service = Services.FirstOrDefault(x => x.Active && !x.Archived);
        if (service != null)
        {
            var booking = new Booking { CustomerId = customer.Id, CreatedAt = Now, CustomerName = customer.Name, Service = new(service.Id, service.Name, service.Price, service.Minutes), Start = DateTime.Today.AddDays(2).AddHours(10), BufferMinutesSnapshot = Settings.BufferMinutes, Address = "24 Sample Street", Area = "Quezon City", Latitude = 14.6760m, Longitude = 121.0437m, TravelFee = Settings.TravelFee, PaymentDueAt = null };
            Bookings.Add(booking);
            Payments.Add(new() { BookingId = booking.Id, Amount = booking.RequiredDownPayment, Method = "GCash", Reference = "DEMO-REFERENCE-001" });
        }
    }
    /// <summary>Generates realistic local-demo bookings and payments from the active catalog (opt-in only; never used in production).</summary>
    public void SeedSampleTransactions(int count = 16)
    {
        var activeServices = Services.Where(x => x.Active && !x.Archived).ToList();
        if (activeServices.Count == 0) return;
        var areas = Settings.ServiceAreas.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (areas.Length == 0) areas = ["Quezon City", "Makati", "Pasig", "Taguig"];
        var firstNames = new[] { "Camille", "Bianca", "Sofia", "Katrina", "Andrea", "Mikaela", "Clarisse", "Danica", "Patricia", "Hannah", "Tricia", "Nicole" };
        var lastNames = new[] { "Santos", "Reyes", "Cruz", "Bautista", "Garcia", "Mendoza", "Torres", "Villanueva", "Ramos", "Navarro", "Aquino", "Castro" };
        var streets = new[] { "14 Katipunan Ave., Loyola Heights", "88 Shaw Blvd., Kapitolyo", "22 Jupiter St., Bel-Air", "51 Tomas Morato Ave., Sacred Heart", "9 Bonifacio High St., Fort Bonifacio", "105 Ortigas Ave., Ugong" };
        var methods = new[] { "GCash", "Maya", "Bank Transfer" };
        var statuses = new[] { "Confirmed", "Completed", "Pending Payment", "Confirmed", "Completed", "Pending Payment", "Cancelled", "Rejected", "Expired", BookingService.EarlyRequestStatus };
        var rng = new Random(20261125);
        var customers = new List<AppUser>();
        for (var i = 0; i < 8; i++)
        {
            var fn = firstNames[i];
            var ln = lastNames[(i * 3 + 1) % lastNames.Length];
            var email = $"{fn.ToLowerInvariant()}.{ln.ToLowerInvariant()}@demo.test";
            var existing = Users.FirstOrDefault(u => u.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
            customers.Add(existing ?? AddUser(fn, ln, email, "SamplePass123!", Roles.Customer, 22 + (i % 12), "Female", $"{streets[i % streets.Length]}, {areas[i % areas.Length]}"));
        }
        var reviewerId = Users.FirstOrDefault(u => u.Role is Roles.SuperAdmin or Roles.Admin)?.Id;
        for (var i = 0; i < count; i++)
        {
            var svc = activeServices[rng.Next(activeServices.Count)];
            var customer = customers[rng.Next(customers.Count)];
            var status = statuses[i % statuses.Length];
            var area = areas[rng.Next(areas.Length)];
            var people = i % 4 == 0 ? rng.Next(2, 5) : 1;
            var isPast = status is "Completed" or "Expired" or "Cancelled" or "Rejected";
            var dayOffset = isPast ? -rng.Next(3, 120) : rng.Next(1, 24);
            var startHour = status == BookingService.EarlyRequestStatus ? 5 : rng.Next(Settings.OpenHour, Math.Max(Settings.OpenHour + 1, Settings.CloseHour - 2));
            var start = DateTime.Today.AddDays(dayOffset).AddHours(startHour);
            var createdAt = isPast ? start.AddDays(-rng.Next(2, 10)) : Now.AddHours(-rng.Next(1, 72));
            var isPaid = status is "Confirmed" or "Completed";
            var booking = new Booking
            {
                CustomerId = customer.Id,
                CreatedAt = createdAt,
                CustomerName = customer.Name,
                Service = new(svc.Id, svc.Name, svc.Price, svc.Minutes),
                People = people,
                Start = start,
                BufferMinutesSnapshot = Settings.BufferMinutes,
                Address = streets[rng.Next(streets.Length)],
                Area = area,
                Latitude = 14.55m + (decimal)rng.NextDouble() * 0.16m,
                Longitude = 121.00m + (decimal)rng.NextDouble() * 0.08m,
                TravelFee = Settings.TravelFee,
                Status = status,
                PaymentStatus = isPaid ? "Verified" : status == "Rejected" ? "Rejected" : "Pending Verification",
                PaidAt = isPaid ? createdAt.AddMinutes(12) : null,
                ArtistAcceptedAt = isPaid ? createdAt.AddMinutes(30) : null,
                CompletedAt = status == "Completed" ? start.AddMinutes(svc.Minutes) : null,
                PaymentDueAt = status == "Pending Payment" ? Now.AddMinutes(15) : null
            };
            Bookings.Add(booking);
            var amount = status == "Completed" ? booking.Total : booking.RequiredDownPayment;
            Payments.Add(new Payment
            {
                BookingId = booking.Id,
                Amount = amount,
                Method = methods[rng.Next(methods.Length)],
                Reference = $"TXN-{createdAt:yyyyMMdd}-{1000 + i}",
                Verified = isPaid,
                ReviewStatus = isPaid ? "Verified" : status == "Rejected" ? "Rejected" : null,
                VerifiedAt = isPaid ? createdAt.AddMinutes(25) : null,
                VerifiedBy = isPaid ? reviewerId : null
            });
        }
    }
    public AppUser AddUser(string firstName, string lastName, string email, string password, string role = "Customer", int age = 0, string gender = "", string address = "")
    {
        var user = new AppUser { Name = $"{firstName} {lastName}".Trim(), FirstName = firstName, LastName = lastName, Age = age, Gender = gender, Address = address, Email = email, Role = role, CreatedAt = Now };
        user.PasswordHash = new PasswordHasher<AppUser>().HashPassword(user, password);
        Users.Add(user);
        return user;
    }
    public AppUser AddCustomerWithPasswordHash(string firstName, string lastName, string email, string passwordHash, int age, string gender, string address, DateOnly? birthday = null, string? suffix = null)
    {
        if (string.IsNullOrWhiteSpace(passwordHash)) throw new InvalidOperationException("Registration expired. Please register again.");
        var error = RegistrationRules.ValidateProfile(firstName, lastName, email, age, gender, address, enforceLegacyAge: false, enforceAddress: false);
        if (error != null) throw new InvalidOperationException(error);
        if (Users.Any(x => x.Email.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("This email is already registered.");
        var user = new AppUser { Name = $"{firstName.Trim()} {lastName.Trim()}", FirstName = firstName.Trim(), LastName = lastName.Trim(), Age = age, Gender = gender, Address = address.Trim(), Email = email.Trim(), Role = Roles.Customer, CreatedAt = Now, PasswordHash = passwordHash, Birthday = birthday, Suffix = string.IsNullOrEmpty(suffix) ? null : suffix };
        Users.Add(user);
        return user;
    }
    public AppUser CreateElevatedUser(string firstName, string lastName, string email, string password, string role, int age, string gender, string address)
    {
        if (role is not ("Admin" or "SuperAdmin")) throw new InvalidOperationException("Invalid elevated role.");
        if (Users.Any(x => x.Email.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("This email is already registered.");
        return AddUser(firstName, lastName, email, password, role, age, gender, address);
    }
    public void SaveCatalog(string kind, CatalogItem item, Guid actor)
    {
        var list = kind == "service" ? Services : AddOns;
        if (!list.Any(x => x.Id == item.Id)) list.Add(item);
        PersistCatalog();
    }
    public void DeleteCatalog(string kind, CatalogItem item, Guid actor) { (kind == "service" ? Services : AddOns).Remove(item); PersistCatalog(); }
    private void PersistCatalog()
    {
        if (catalogSnapshot == null) return;
        try { catalogSnapshot.Save(Services, AddOns); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            var previous = catalogSnapshot.LoadOrInitialize([], []);
            Services.Clear(); Services.AddRange(previous.Services);
            AddOns.Clear(); AddOns.AddRange(previous.AddOns);
            throw new InvalidOperationException("Demo catalog save could not be confirmed; reload before retrying.", e);
        }
    }
    public void SaveSettings(BusinessSettings settings, Guid actor) { }
    public void AddBlock(AvailabilityBlock block, Guid actor)
    {
        lock (Gate)
        {
            if (block.From >= block.To || string.IsNullOrWhiteSpace(block.Reason) ||
                Bookings.Any(b => (b.Status is "Confirmed" or "Pending Payment") && block.From < b.End.AddMinutes(b.BufferMinutesSnapshot ?? Settings.BufferMinutes) && block.To > b.Start))
                throw new InvalidOperationException("Enter a valid block that does not overlap a reserved appointment.");
            Blocks.Add(block);
        }
    }
    public void RemoveBlock(Guid id, Guid actor) => Blocks.RemoveAll(x => x.Id == id);
    public void RecordAudit(Guid actor, string message) => AuditLogs.Add(new(actor, message, Now));
    public Booking? FindBooking(Guid id) => Bookings.FirstOrDefault(b => b.Id == id);
    private ArtistLocation artistLocation = new(null, null, null, null, null);
    public ArtistLocation GetArtistLocation() { lock (Gate) return artistLocation; }
    public void SetArtistHome(decimal lat, decimal lng)
    {
        if (!BookingLocation.IsValid(lat, lng)) throw new InvalidOperationException(BookingLocation.ArtistMessage);
        lock (Gate) artistLocation = artistLocation with { HomeLat = lat, HomeLng = lng };
    }
    public void SetArtistCurrent(decimal? lat, decimal? lng)
    {
        if ((lat, lng) is not (null, null) && !BookingLocation.IsValid(lat, lng)) throw new InvalidOperationException(BookingLocation.ArtistMessage);
        lock (Gate) artistLocation = artistLocation with { CurrentLat = lat, CurrentLng = lng, CurrentUpdatedAt = lat is null ? null : Now };
    }
    // Pending Payment inside its window, Confirmed and InProgress, as the database's mvc_travel_stops.
    public IReadOnlyList<TravelAppointment> TravelStops(DateTime from, DateTime until, Guid? exclude)
    {
        lock (Gate)
            return Bookings.Where(b => b.Id != exclude && (b.Status is "Confirmed" or "InProgress" || (b.Status == "Pending Payment" && !PaymentWindow.IsExpired(b, Now))))
                .Select(b => new TravelAppointment(b.Start, b.End, b.BufferMinutesSnapshot ?? Settings.BufferMinutes, b.Latitude, b.Longitude))
                .Where(s => s.Start < until && s.End.AddMinutes(s.BufferMinutes) > from)
                .OrderBy(s => s.Start).ToList();
    }
    public IReadOnlyList<TravelAppointment> FinishedStops(DateTime from, DateTime until)
    {
        lock (Gate)
            return Bookings.Where(b => b.Status is "Completed" or "InProgress")
                .Select(b => new TravelAppointment(b.Start, b.End, b.BufferMinutesSnapshot ?? Settings.BufferMinutes, b.Latitude, b.Longitude))
                .Where(s => s.Start < until && s.End > from).OrderBy(s => s.End).ToList();
    }
    public void SetAdminActive(Guid admin, bool active, Guid actor)
    {
        lock (Gate)
        {
            if (!Users.Any(u => u.Id == actor && u.IsActive && u.Role == Roles.SuperAdmin))
                throw new InvalidOperationException("Only the Super Admin can disable or enable administrators.");
            var target = Users.SingleOrDefault(u => u.Id == admin && u.Role == Roles.Admin)
                ?? throw new InvalidOperationException("Only Admin accounts can be disabled or enabled.");
            target.IsActive = active;
        }
    }
}
