using System.Data;
using System.Security.Claims;
using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GlamByMariya.Infrastructure.Data;

public class PostgresStudioStore(MakeupDbContext db, IHttpContextAccessor http,
    IWebHostEnvironment environment, IConfiguration configuration, IAppFileStore files) : IPersistentStudioStore
{
    public object Gate { get; } = new(); // Request-local compatibility only; PostgreSQL owns concurrency.
    public bool IsPersistent => true;
    protected Guid? Actor => Guid.TryParse(http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    private bool CustomerRequest => http.HttpContext?.User.IsInRole("Customer") == true;
    private string Zone => configuration["Studio:TimeZone"] ?? Settings.TimeZone;
    public DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(Zone));
    protected DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById(Zone));
    protected DateTime Utc(DateTime local)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(Zone);
        var value = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(value) || zone.IsAmbiguousTime(value)) throw new InvalidOperationException("Choose an unambiguous business-local appointment time.");
        return TimeZoneInfo.ConvertTimeToUtc(value, zone);
    }
    protected BusinessSettings? settings;
    public BusinessSettings Settings => settings ??= LoadSettings();
    private BusinessSettings LoadSettings()
    {
        var s = db.Set<SettingsRow>().AsNoTracking().Single(x => x.Id == 1);
        return new() { TimeZone = s.TimeZone, OpenHour = s.OpenHour, CloseHour = s.CloseHour, BufferMinutes = s.BufferMinutes,
            TravelFee = s.TravelFeeInformational, DownPaymentPercent = s.DownPaymentPercent, PaymentInstructions = s.PaymentInstructions, PolicyVersion = s.PolicyVersion,
            TravelReminderVersion = s.TravelReminderVersion,
            ServiceAreas = string.Join(',', db.Set<AreaRow>().AsNoTracking().Where(a => a.IsActive).OrderBy(a => a.Name).Select(a => a.Name)) };
    }
    public List<CatalogItem> Services => (from s in db.Set<ServiceRow>().AsNoTracking()
        join c in db.Set<CategoryRow>() on s.CategoryId equals c.Id
        select new CatalogItem { Id=s.Id,Name=s.Name,Description=s.Description,Price=s.Price,Minutes=s.Minutes,
            GroupPrice=s.GroupPrice,GroupMinimumPeople=s.GroupMinimumPeople,Category=c.Name,VisualWord=s.VisualWord,Active=s.IsActive,Archived=s.IsArchived }).ToList();
    public List<CatalogItem> AddOns => (from s in db.Set<AddonRow>().AsNoTracking()
        join c in db.Set<CategoryRow>() on s.CategoryId equals c.Id
        select new CatalogItem { Id=s.Id,Name=s.Name,Description=s.Description,Price=s.Price,Minutes=s.Minutes,
            Category=c.Name,VisualWord=s.VisualWord,Active=s.IsActive,Archived=s.IsArchived }).ToList();
    private IQueryable<BookingRow> VisibleBookings => CustomerRequest
        ? db.Set<BookingRow>().AsNoTracking().Where(b => b.CustomerId == Actor)
        : db.Set<BookingRow>().AsNoTracking();
    public List<Booking> Bookings => LoadBookings(VisibleBookings.OrderByDescending(x => x.StartsAt));
    // One row plus its own add-ons and refunds; customers still only see their own bookings.
    public virtual Booking? FindBooking(Guid id) => LoadBookings(VisibleBookings.Where(b => b.Id == id)).SingleOrDefault();
    private List<Booking> LoadBookings(IQueryable<BookingRow> query)
    {
            var rows = query.ToList();
            var ids = rows.Select(x => x.Id).ToArray();
            var extras = db.Set<BookingAddonRow>().AsNoTracking().Where(x => ids.Contains(x.BookingId)).ToList().ToLookup(x => x.BookingId);
            var quantityLines = PreparedPersistentBooking.Enabled ? PreparedPersistentBooking.ReadLines(db, ids, db is PublicMakeupDbContext) : null;
            var transfers = (from c in db.Set<ClosureRow>() join r in db.Set<RefundRow>() on c.Id equals r.ClosureId
                where ids.Contains(c.BookingId) orderby r.SentAt descending select new { c.BookingId,r.TransferReference }).ToList().ToLookup(x=>x.BookingId);
            return rows.Select(b => new Booking { Id=b.Id,StoredReference=b.Reference,CustomerId=b.CustomerId,CustomerName=b.CustomerNameSnapshot,
                Service=new(b.ServiceId,b.ServiceNameSnapshot,b.RegularPriceSnapshot,b.ServiceMinutesSnapshot),
                AddOns=quantityLines != null ? quantityLines.GetValueOrDefault(b.Id) ?? [] : extras[b.Id].Select(a=>new PriceSnapshot(a.AddonId,a.NameSnapshot,a.PriceSnapshot,a.MinutesSnapshot)).ToList(),
                Start=Local(b.StartsAt),BufferMinutesSnapshot=b.BufferMinutesSnapshot,People=b.People,Package=b.BookingType=="package"?"group":"auto",
                GroupPrice=b.GroupPriceSnapshot,GroupMinimumPeople=b.GroupMinimumSnapshot,AppliedPricePerPerson=b.AppliedPricePerPerson,
                DownPaymentPercentSnapshot=b.DownPaymentPercentSnapshot,RequiredDownPaymentSnapshot=b.RequiredDownPaymentSnapshot,
                Address=b.Address,Area=b.AreaNameSnapshot,TravelFee=b.TravelFeeInformationalSnapshot,TransportationPolicyAcknowledgedAt=b.TransportationPolicyAcknowledgedAt,VerifiedPaid=b.VerifiedPaid,
                PaymentStatus=b.PaymentStatus,Status=b.Status,Note=b.Note,RequestedStart=b.RequestedStart.HasValue?Local(b.RequestedStart.Value):null,
                CancellationFee=b.CancellationFee,RefundDue=b.RefundDue,Refunded=b.Refunded,RefundReference=transfers[b.Id].FirstOrDefault()?.TransferReference??"" }).ToList();
    }
    public List<Payment> Payments
    {
        get
        {
            var query = db.Set<PaymentRow>().AsNoTracking();
            if (CustomerRequest) query=query.Where(p=>db.Set<BookingRow>().Any(b=>b.Id==p.BookingId && b.CustomerId==Actor));
            var rows=(from p in query join m in db.Set<MethodRow>() on p.MethodId equals m.Id
                join a in db.Set<AssetRow>() on p.ProofAssetId equals a.Id into assets from a in assets.DefaultIfEmpty()
                select new { p,Method=m.Name,Key=a==null?null:a.ObjectKey }).ToList();
            return rows.Select(x=>new Payment { Id=x.p.Id,BookingId=x.p.BookingId,Amount=x.p.Amount,Method=x.Method,
                 Reference=x.p.Reference,ProofPath=x.Key==null?null:AssetPath(x.Key),ProofKey=x.Key,Verified=x.p.Status=="Verified",
                 ReviewStatus=x.p.Status,VerifiedBy=x.p.VerifiedBy,VerifiedAt=x.p.VerifiedAt.HasValue?Local(x.p.VerifiedAt.Value):null }).ToList();
        }
    }
    public List<Payment> ReviewPayments(Guid booking)
    {
        // Payment, displayed proof key, and revision count must come from one snapshot.
        var sql = db is PublicMakeupDbContext ? """
            SELECT v.id AS "Id", v.booking_id AS "BookingId", v.amount AS "Amount",
                   m.name AS "Method", v.reference AS "Reference", v.status AS "Status",
                   a.object_key AS "ProofKey", v.verified_by AS "VerifiedBy", v.verified_at AS "VerifiedAt",
                   (SELECT count(*) FROM public.payment_revisions r WHERE r.payment_id=p.id) AS "Revision", p.xmin::text AS "Version"
            FROM public.mvc_payments v JOIN public.payments p ON p.app_id=v.id
            JOIN public.payment_methods m ON m.id=v.method_id
            LEFT JOIN public.media_assets a ON a.id=v.proof_asset_id
            WHERE v.booking_id={0}
            """ : """
            SELECT p.id AS "Id", p.booking_id AS "BookingId", p.amount AS "Amount",
                   m.name AS "Method", p.reference AS "Reference", p.status AS "Status",
                   a.object_key AS "ProofKey", p.verified_by AS "VerifiedBy", p.verified_at AS "VerifiedAt",
                   (SELECT count(*) FROM makeup.payment_revisions r WHERE r.payment_id=p.id) AS "Revision", p.xmin::text AS "Version"
            FROM makeup.payments p JOIN makeup.payment_methods m ON m.id=p.method_id
            LEFT JOIN makeup.media_assets a ON a.id=p.proof_asset_id
            WHERE p.booking_id={0}
            """;
        return db.Database.SqlQueryRaw<ReviewPaymentRow>(sql, booking).ToList().Select(p => new Payment {
            Id=p.Id, BookingId=p.BookingId, Amount=p.Amount, Method=p.Method, Reference=p.Reference,
            ProofKey=p.ProofKey, ProofPath=p.ProofKey==null?null:AssetPath(p.ProofKey),
            Verified=p.Status=="Verified", ReviewStatus=p.Status, VerifiedBy=p.VerifiedBy,
            VerifiedAt=p.VerifiedAt.HasValue?Local(p.VerifiedAt.Value):null, PersistentReviewRevision=p.Revision, PersistentReviewVersion=p.Version
        }).ToList();
    }
    public sealed class ReviewPaymentRow
    {
        public Guid Id { get; set; }
        public Guid BookingId { get; set; }
        public decimal Amount { get; set; }
        public string Method { get; set; } = "";
        public string Reference { get; set; } = "";
        public string Status { get; set; } = "";
        public string? ProofKey { get; set; }
        public Guid? VerifiedBy { get; set; }
        public DateTime? VerifiedAt { get; set; }
        public long Revision { get; set; }
        public string Version { get; set; } = "";
    }
    public List<AppUser> Users
    {
        get
        {
            var query=db.Set<UserRow>().AsNoTracking();
            if (CustomerRequest) query=query.Where(x=>x.Id==Actor);
            return (from u in query join r in db.Set<RoleRow>() on u.RoleId equals r.Id
                select new AppUser { Id=u.Id,FirstName=u.FirstName,LastName=u.LastName,Name=u.FirstName+" "+u.LastName,
                    Age=u.AgeAtRegistration,Gender=u.Gender,Address=u.Address,Email=u.Email,PasswordHash=u.PasswordHash,Role=r.Name,IsActive=u.IsActive }).ToList();
        }
    }
    public AppUser? FindUser(string email) => (from u in db.Set<UserRow>().AsNoTracking() join r in db.Set<RoleRow>() on u.RoleId equals r.Id
        where u.Email.ToLower()==email.Trim().ToLower()
        select new AppUser { Id=u.Id,FirstName=u.FirstName,LastName=u.LastName,Name=u.FirstName+" "+u.LastName,Age=u.AgeAtRegistration,
            Gender=u.Gender,Address=u.Address,Email=u.Email,PasswordHash=u.PasswordHash,Role=r.Name,IsActive=u.IsActive && (u.LockedUntil==null || u.LockedUntil<=DateTime.UtcNow) }).SingleOrDefault();
    public List<Notification> Notifications => db.Set<NotificationRow>().AsNoTracking().Where(x=>x.UserId==Actor)
        .OrderByDescending(x=>x.CreatedAt).Take(100).ToList().Select(x=>new Notification(x.UserId,x.Message,Local(x.CreatedAt))).ToList();
    public List<AuditLog> AuditLogs => db.Set<AuditRow>().AsNoTracking().OrderByDescending(x=>x.CreatedAt).Take(100)
        .ToList().Select(x=>new AuditLog(x.ActorId??Guid.Empty,$"{x.EntityType}: {x.Event}",Local(x.CreatedAt))).ToList();
    public List<AvailabilityBlock> Blocks => db.Set<BlockRow>().AsNoTracking().Where(x=>x.ArtistId==Artist())
        .ToList().Select(x=>new AvailabilityBlock(x.Id,Local(x.FromAt),Local(x.ToAt),x.Reason)).ToList();
    protected virtual Guid Artist()
    {
        var configured=configuration["Database:ArtistId"];
        if (Guid.TryParse(configured,out var artist)) return artist;
        var artists=(from u in db.Set<UserRow>() join r in db.Set<RoleRow>() on u.RoleId equals r.Id
            where r.Name=="Admin" && u.IsActive orderby u.Id select u.Id).Take(2).ToList();
        if (artists.Count!=1) throw new InvalidOperationException("Configure Database:ArtistId for the business's active makeup artist (or create one admin first).");
        return artists[0];
    }
    protected T Transaction<T>(Guid? actor, Func<T> command)
    {
        using var transaction=db.Database.BeginTransaction(IsolationLevel.ReadCommitted);
        try
        {
            db.Database.ExecuteSqlInterpolated($"SELECT set_config('makeup.actor_id',{(actor.HasValue?actor.Value.ToString():"")},true)");
            var result=command();
            transaction.Commit(); // Deferred constraints are part of this operation.
            settings=null;
            return result;
        }
        catch (PostgresException e)
        {
            transaction.Rollback();
            throw new InvalidOperationException(Friendly(e),e);
        }
    }
    protected void Transaction(Guid? actor, Action command) => Transaction(actor,()=>{ command(); return true; });
    protected virtual string Friendly(PostgresException e) => e.SqlState switch
    {
        PostgresErrorCodes.UniqueViolation => e.ConstraintName=="ux_users_email" ? "This email is already registered." : "That transaction reference or request already exists. Refresh and check your records before retrying.",
        PostgresErrorCodes.ForeignKeyViolation => "This record is still referenced. Archive it instead of deleting it.",
        PostgresErrorCodes.CheckViolation or PostgresErrorCodes.NotNullViolation => "The submitted data does not meet the booking or payment requirements. Refresh and check all fields.",
        PostgresErrorCodes.ExclusionViolation => "This slot overlaps a reserved appointment. Please choose another time.",
        PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected => "Another update was made at the same time. Refresh and retry.",
        PostgresErrorCodes.RaiseException => e.MessageText,
        _ => "The database could not save this change. Please refresh and try again."
    };
    protected virtual void Admin(Guid id) => db.Database.ExecuteSqlInterpolated($"SELECT makeup.assert_role({id},'Admin')");
    protected virtual BookingRow LockBooking(Guid id)
    {
        db.Database.ExecuteSqlInterpolated($"SELECT id FROM makeup.bookings WHERE id={id} FOR UPDATE");
        return db.Set<BookingRow>().AsNoTracking().SingleOrDefault(x=>x.Id==id) ?? throw new InvalidOperationException("Booking not found.");
    }
    public virtual AppUser AddUser(string firstName,string lastName,string email,string password,string role="Customer",int age=0,string gender="",string address="")
    {
        var error=RegistrationRules.Validate(firstName,lastName,email,age,gender,address,password);
        if(error!=null) throw new InvalidOperationException(error);
        if(role is not ("Customer" or "Admin")) throw new InvalidOperationException("Invalid account role.");
        var user=new AppUser { FirstName=firstName.Trim(),LastName=lastName.Trim(),Name=$"{firstName.Trim()} {lastName.Trim()}",
            Email=email.Trim(),Age=age,Gender=gender,Address=address.Trim(),Role=role };
        user.PasswordHash=new PasswordHasher<AppUser>().HashPassword(user,password);
        InsertLegacyUser(user,role,age,gender);
        return user;
    }
    public virtual AppUser AddCustomerWithPasswordHash(string firstName,string lastName,string email,string passwordHash,int age,string gender,string address,DateOnly? birthday=null,string? suffix=null)
    {
        if(string.IsNullOrWhiteSpace(passwordHash)) throw new InvalidOperationException("Registration expired. Please register again.");
        var error=RegistrationRules.ValidateProfile(firstName,lastName,email,age,gender,address,enforceLegacyAge:false);
        if(error!=null) throw new InvalidOperationException(error);
        var user=new AppUser { FirstName=firstName.Trim(),LastName=lastName.Trim(),Name=$"{firstName.Trim()} {lastName.Trim()}",
            Email=email.Trim(),Age=age,Gender=gender,Address=address.Trim(),Role="Customer",PasswordHash=passwordHash };
        InsertLegacyUser(user,"Customer",age,gender);
        return user;
    }
    private void InsertLegacyUser(AppUser user,string role,int age,string gender) =>
        Transaction(Actor,()=>db.Database.ExecuteSqlInterpolated($"""
            INSERT INTO makeup.app_users(id,role_id,first_name,last_name,age_at_registration,gender,address,email,password_hash)
            SELECT {user.Id},id,{user.FirstName},{user.LastName},{age},{gender},{user.Address},{user.Email},{user.PasswordHash}
            FROM makeup.roles WHERE name={role}
            """));
    public virtual AppUser CreateElevatedUser(string firstName,string lastName,string email,string password,string role,int age,string gender,string address)
    {
        if(role is not ("Admin" or "SuperAdmin")) throw new InvalidOperationException("Invalid elevated role.");
        if(role == "SuperAdmin") throw new InvalidOperationException("The legacy makeup schema stores Customer and Admin only. Use the public schema for SuperAdmin.");
        return AddUser(firstName,lastName,email,password,role,age,gender,address);
    }
    public virtual void SaveCatalog(string kind,CatalogItem item,Guid actor) => Transaction(actor,()=>
    {
        Admin(actor);
        var category=db.Set<CategoryRow>().SingleOrDefault(x=>x.Name.ToLower()==item.Category.ToLower());
        var categoryId=category?.Id??Guid.NewGuid();
        if(category==null) db.Database.ExecuteSqlInterpolated($"INSERT INTO makeup.catalog_categories(id,name) VALUES({categoryId},{item.Category})");
        if(kind=="service") db.Database.ExecuteSqlInterpolated($"""
            INSERT INTO makeup.makeup_services(id,category_id,name,description,price,minutes,group_price,group_minimum_people,visual_word,is_active,is_archived)
            VALUES({item.Id},{categoryId},{item.Name},{item.Description},{item.Price},{item.Minutes},{item.GroupPrice},{item.GroupMinimumPeople},{item.VisualWord},{item.Active},{item.Archived})
            ON CONFLICT(id) DO UPDATE SET category_id=excluded.category_id,name=excluded.name,description=excluded.description,
              price=excluded.price,minutes=excluded.minutes,group_price=excluded.group_price,group_minimum_people=excluded.group_minimum_people,
              visual_word=excluded.visual_word,is_active=excluded.is_active,is_archived=excluded.is_archived
            """);
        else if(kind=="addon") db.Database.ExecuteSqlInterpolated($"""
            INSERT INTO makeup.makeup_addons(id,category_id,name,description,price,minutes,visual_word,is_active,is_archived)
            VALUES({item.Id},{categoryId},{item.Name},{item.Description},{item.Price},{item.Minutes},{item.VisualWord},{item.Active},{item.Archived})
            ON CONFLICT(id) DO UPDATE SET category_id=excluded.category_id,name=excluded.name,description=excluded.description,
              price=excluded.price,minutes=excluded.minutes,visual_word=excluded.visual_word,is_active=excluded.is_active,is_archived=excluded.is_archived
            """);
        else throw new InvalidOperationException("Invalid catalog type.");
    });
    public virtual void DeleteCatalog(string kind,CatalogItem item,Guid actor) => Transaction(actor,()=>
    {
        Admin(actor);
        if(kind=="service")
        {
            db.Database.ExecuteSqlInterpolated($"SELECT id FROM makeup.makeup_services WHERE id={item.Id} FOR UPDATE");
            if(db.Set<BookingRow>().Any(x=>x.ServiceId==item.Id)) db.Database.ExecuteSqlInterpolated($"UPDATE makeup.makeup_services SET is_active=false,is_archived=true WHERE id={item.Id}");
            else db.Database.ExecuteSqlInterpolated($"DELETE FROM makeup.makeup_services WHERE id={item.Id}");
        }
        else if(kind=="addon")
        {
            db.Database.ExecuteSqlInterpolated($"SELECT id FROM makeup.makeup_addons WHERE id={item.Id} FOR UPDATE");
            if(db.Set<BookingAddonRow>().Any(x=>x.AddonId==item.Id)) db.Database.ExecuteSqlInterpolated($"UPDATE makeup.makeup_addons SET is_active=false,is_archived=true WHERE id={item.Id}");
            else db.Database.ExecuteSqlInterpolated($"DELETE FROM makeup.makeup_addons WHERE id={item.Id}");
        }
        else throw new InvalidOperationException("Invalid catalog type.");
    });
    public virtual void SaveSettings(BusinessSettings value,Guid actor) => Transaction(actor,()=>
    {
        Admin(actor);
        var names=value.ServiceAreas.Split(',').Select(x=>x.Trim()).Where(x=>x.Length>0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(names.Length==0 || names.Any(x=>x.Length>100)) throw new InvalidOperationException("Provide service area names of at most 100 characters.");
        db.Database.ExecuteSqlInterpolated($"""
            UPDATE makeup.business_settings SET open_hour={value.OpenHour},close_hour={value.CloseHour},buffer_minutes={value.BufferMinutes},down_payment_percent={value.DownPaymentPercent},
              travel_fee_informational={value.TravelFee},payment_instructions={value.PaymentInstructions} WHERE id=1
            """);
        db.Database.ExecuteSqlRaw("UPDATE makeup.service_areas SET is_active=false");
        foreach(var name in names)
            db.Database.ExecuteSqlInterpolated($"INSERT INTO makeup.service_areas(name) VALUES({name}) ON CONFLICT(lower(name)) DO UPDATE SET is_active=true");
    });
    public virtual void AddBlock(AvailabilityBlock block,Guid actor) => Transaction(actor,()=>
    {
        Admin(actor); var artist=Artist(); var from=Utc(block.From); var to=Utc(block.To);
        db.Database.ExecuteSqlInterpolated($"INSERT INTO makeup.artist_schedule_entries(id,artist_id,kind,starts_at,ends_at,reason,created_by) VALUES({block.Id},{artist},'block',{from},{to},{block.Reason},{actor})");
    });
    public virtual void RemoveBlock(Guid id,Guid actor) => Transaction(actor,()=>
    {
        Admin(actor); db.Database.ExecuteSqlInterpolated($"DELETE FROM makeup.artist_schedule_entries WHERE id={id} AND kind='block'");
    });
    public virtual void RecordAudit(Guid actor,string message) => Transaction(actor,()=>
        db.Database.ExecuteSqlInterpolated($"INSERT INTO makeup.audit_logs(actor_id,entity_type,event) VALUES({actor},'application',{message})"));

    // The legacy makeup schema has no SuperAdmin, so nobody there may disable admins.
    public virtual void SetAdminActive(Guid admin,bool active,Guid actor) =>
        throw new InvalidOperationException("Admin Disable/Enable needs the public schema, where the SuperAdmin lives.");

    public virtual void ResetPassword(string email, string newPassword)
    {
        var passwordError = PasswordPolicy.ValidateNewPassword(newPassword);
        if (passwordError != null) throw new InvalidOperationException(passwordError);
        var existing = FindUser(email) ?? throw new InvalidOperationException("No account uses that email. No password was changed.");
        var tmp = new AppUser { FirstName = existing.FirstName, LastName = existing.LastName };
        var hash = new PasswordHasher<AppUser>().HashPassword(tmp, newPassword);
        Transaction(existing.Id, () =>
            db.Database.ExecuteSqlInterpolated($"UPDATE makeup.app_users SET password_hash={hash}, security_stamp=gen_random_uuid() WHERE id={existing.Id}"));
    }

    public virtual Booking SubmitBooking(Guid customer,Guid service,Guid[] extras,DateTime start,string address,string area,decimal deposit,string method,string reference,string? proof,bool agreed,bool travelAcknowledged,int people,string package,decimal? expectedTotal=null,decimal? expectedMinimum=null)
    {
        if(!agreed || !travelAcknowledged) throw new InvalidOperationException("Read and accept the Transportation Fee Policy before submitting payment.");
        if(package=="auto" && people!=1) throw new InvalidOperationException("Individual bookings must have exactly one person.");
        BookingService.ValidatePayment(method,reference);
        var id=Guid.NewGuid();
        Transaction(customer,()=>
        {
            db.Database.ExecuteSqlInterpolated($"SELECT makeup.assert_role({customer},'Customer')");
            // Lock configuration inputs so quote aggregates and trigger snapshots
            // cannot disagree with a simultaneous catalog/settings edit.
            db.Database.ExecuteSqlRaw("SELECT id FROM makeup.business_settings WHERE id=1 FOR SHARE");
            db.Database.ExecuteSqlInterpolated($"SELECT id FROM makeup.makeup_services WHERE id={service} FOR SHARE");
            foreach(var extra in extras.Distinct().Order()) db.Database.ExecuteSqlInterpolated($"SELECT id FROM makeup.makeup_addons WHERE id={extra} FOR SHARE");
            settings=null;
            var selected=Services.FirstOrDefault(x=>x.Id==service && x.Active && !x.Archived)??throw new InvalidOperationException("Choose an active service.");
            var selectedExtras=PreparedPersistentBooking.Enabled ? PreparedPersistentBooking.Resolve(extras, people, AddOns) : extras.Distinct().Select(x=>AddOns.FirstOrDefault(a=>a.Id==x && a.Active && !a.Archived)??throw new InvalidOperationException("An add-on is no longer available.")).ToList();
            var quote=BookingPricing.Calculate(selected,selectedExtras,people,Settings.TravelFee,package,Settings.DownPaymentPercent);
            BookingService.ValidateExpectedQuote(quote, expectedTotal, expectedMinimum);
            if (PreparedPersistentBooking.Enabled) PreparedPersistentBooking.ValidateClaim(deposit, quote);
            else if(deposit!=quote.RequiredDownPayment) throw new InvalidOperationException($"The required {Settings.DownPaymentPercent:N0}% down payment is ₱{quote.RequiredDownPayment:N2}. Refresh your price.");
            var coverage=db.Set<AreaRow>().SingleOrDefault(x=>x.IsActive && x.Name.ToLower()==area.Trim().ToLower())??throw new InvalidOperationException("The address must be within the service area.");
            var artist=Artist(); var utc=Utc(start); var type=package=="group"?"package":"individual";
            var extrasTotal=selectedExtras.Sum(x=>x.Price); var extrasMinutes=selectedExtras.Sum(x=>x.Minutes);
            db.Database.ExecuteSqlInterpolated($"""
                INSERT INTO makeup.bookings(id,customer_id,artist_id,service_id,starts_at,address,area_id,booking_type,people,addons_total_snapshot,addons_minutes_snapshot,
                  policy_accepted_at,policy_version,travel_reminder_acknowledged_at,travel_reminder_version)
                VALUES({id},{customer},{artist},{service},{utc},{address.Trim()},{coverage.Id},{type},{people},{extrasTotal},{extrasMinutes},
                  now(),{Settings.PolicyVersion},now(),{Settings.TravelReminderVersion})
                """);
            foreach(var group in selectedExtras.GroupBy(x => x.Id))
                if (PreparedPersistentBooking.Enabled) db.Database.ExecuteSqlInterpolated($"INSERT INTO makeup.booking_addons(booking_id,addon_id,quantity) VALUES({id},{group.Key},{group.Count()})");
                else db.Database.ExecuteSqlInterpolated($"INSERT INTO makeup.booking_addons(booking_id,addon_id) VALUES({id},{group.Key})");
            var paymentMethod=db.Set<MethodRow>().SingleOrDefault(x=>x.Name==method && x.IsActive && x.AcceptsDeposits)??throw new InvalidOperationException("Use GCash, Maya or Bank Transfer for your deposit.");
            var asset=ProofAsset(proof,customer);
            db.Database.ExecuteSqlInterpolated($"""
                INSERT INTO makeup.payments(booking_id,method_id,purpose,amount,reference,proof_asset_id,submitted_by)
                VALUES({id},{paymentMethod.Id},'down_payment',{deposit},{reference.Trim()},{asset},{customer})
                """);
        });
        return Bookings.Single(x=>x.Id==id);
    }
    // The legacy makeup schema has no Expired status; only the public schema expires bookings.
    public virtual int ExpireOverduePayments() => 0;
    public virtual void CancelBooking(Guid booking,Guid customer,bool confirmed) => Transaction(customer,()=>
    {
        var b=LockBooking(booking);
        if(!confirmed || b.CustomerId!=customer) throw new InvalidOperationException("Confirm cancellation of your own appointment.");
        var decision=CancellationPolicy.Evaluate(b.Status,Local(b.StartsAt),Now);
        if(!decision.Allowed) throw new InvalidOperationException(decision.Reason);
        var note=decision.Fee>0 ? $"Customer confirmed cancellation and the PHP {decision.Fee:N0} deduction." : "Customer cancelled before payment. No fee.";
        db.Database.ExecuteSqlInterpolated($"UPDATE makeup.bookings SET status='Cancelled',cancellation_confirmed_at=now(),note={note} WHERE id={booking}");
    });
    public virtual void RequestReschedule(Guid booking,Guid customer,DateTime start) => Transaction(customer,()=>
    {
        var b=LockBooking(booking);
        if(b.CustomerId!=customer) throw new InvalidOperationException("This is not your booking.");
        db.Database.ExecuteSqlInterpolated($"UPDATE makeup.reschedule_requests SET status='Superseded' WHERE booking_id={booking} AND status='Pending'");
        var utc=Utc(start);
        db.Database.ExecuteSqlInterpolated($"INSERT INTO makeup.reschedule_requests(booking_id,requested_by,original_start,requested_start) VALUES({booking},{customer},{b.StartsAt},{utc})");
        Notify(booking,$"Reschedule requested for {start:MMM d, yyyy h:mm tt}. Original slot stays reserved until approval.");
    });
    public virtual void CorrectPayment(Guid booking,Guid customer,string reference,string? proof) => Transaction(customer,()=>
    {
        var b=LockBooking(booking);
        var p=db.Set<PaymentRow>().Single(x=>x.BookingId==booking && x.Purpose=="down_payment");
        if(b.CustomerId!=customer || b.Status!="Pending Payment" || p.Status=="Verified") throw new InvalidOperationException("Only unverified pending deposits can be corrected.");
        var method=db.Set<MethodRow>().Single(x=>x.Id==p.MethodId).Name;
        BookingService.ValidatePayment(method,reference);
        var asset=proof==null?p.ProofAssetId:ProofAsset(proof,customer);
        db.Database.ExecuteSqlInterpolated($"UPDATE makeup.payments SET reference={reference.Trim()},proof_asset_id={asset},status='Pending Verification' WHERE id={p.Id}");
        Notify(booking,"Customer updated payment proof for review.");
    });
    public virtual void ManageBooking(Guid booking,string action,Guid admin,string reason,decimal amount,string reference, Guid? paymentId = null, long? reviewRevision = null, string? reviewVersion = null) => Transaction(admin,()=>
    {
        Admin(admin); var b=LockBooking(booking);
        if (action is "verify" or "approve") CheckPaymentReview(booking, paymentId, reviewRevision, reviewVersion, b.Status == "Confirmed");
        switch(action)
        {
            case "verify":
                db.Database.ExecuteSqlInterpolated($"SELECT makeup.confirm_booking({booking},{admin})"); break;
            case "approve":
                if(b.Status=="Confirmed" && b.VerifiedPaid>=b.RequiredDownPayment) return;
                if(b.Status!="Pending Payment" || (PreparedPersistentBooking.Enabled ? b.VerifiedPaid < b.RequiredDownPayment || b.VerifiedPaid > b.FinalTotal : b.VerifiedPaid != b.RequiredDownPayment)) throw new InvalidOperationException("Verify the required deposit before approval.");
                db.Database.ExecuteSqlInterpolated($"SELECT makeup.confirm_booking({booking},{admin})"); break;
            case "proof":
                if(b.Status!="Pending Payment" || string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Provide payment correction instructions for a pending booking.");
                db.Database.ExecuteSqlInterpolated($"UPDATE makeup.payments SET status='Correction Requested',review_note={reason} WHERE booking_id={booking} AND purpose='down_payment'");
                db.Database.ExecuteSqlInterpolated($"UPDATE makeup.bookings SET note={reason} WHERE id={booking}");
                Notify(booking,reason); break;
            case "reject":
                if(b.Status!="Pending Payment" || string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Provide a reason to reject a pending booking.");
                db.Database.ExecuteSqlInterpolated($"UPDATE makeup.bookings SET status='Rejected',note={reason},reviewed_by={admin},reviewed_at=now() WHERE id={booking}"); break;
            case "reschedule":
            case "decline":
                if(action=="decline" && string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Provide a decline reason.");
                var status=action=="reschedule"?"Approved":"Declined";
                if(db.Database.ExecuteSqlInterpolated($"UPDATE makeup.reschedule_requests SET status={status},reason={reason},reviewed_by={admin},reviewed_at=now() WHERE booking_id={booking} AND status='Pending'")==0)
                    throw new InvalidOperationException("No reschedule request to review.");
                if(action=="decline") db.Database.ExecuteSqlInterpolated($"UPDATE makeup.bookings SET note={reason} WHERE id={booking}");
                break;
            case "complete":
                // The legacy adapter contract has no explicit confirmation and only
                // checks staff, not SuperAdmin. Keep it closed until the authorized
                // completion contract and database transition are implemented.
                throw new InvalidOperationException("Persistent appointment completion is not available.");
            case "noshow":
            case "incomplete":
                var outcome=action=="noshow"?"No-show":"Incomplete";
                if(b.Status!="Confirmed" || string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Provide a reason for a past confirmed appointment.");
                db.Database.ExecuteSqlInterpolated($"UPDATE makeup.bookings SET status={outcome},note={reason},reviewed_by={admin},reviewed_at=now() WHERE id={booking}"); break;
            case "refund":
                if(string.IsNullOrWhiteSpace(reference) || reference.Length>150 || amount<=0) throw new InvalidOperationException("Enter the actual refund amount and transfer reference.");
                var closure=db.Set<ClosureRow>().SingleOrDefault(x=>x.BookingId==booking)??throw new InvalidOperationException("No refund obligation exists.");
                var method=db.Set<MethodRow>().Single(x=>x.Name=="Bank Transfer").Id;
                db.Database.ExecuteSqlInterpolated($"INSERT INTO makeup.refunds(closure_id,method_id,amount,transfer_reference,sent_at,recorded_by) VALUES({closure.Id},{method},{amount},{reference.Trim()},now(),{admin})");
                Notify(booking,$"Manual refund recorded: ₱{amount:N2}."); break;
            default: throw new InvalidOperationException("Unknown booking action.");
        }
    });
    protected void CheckPaymentReview(Guid booking, Guid? paymentId, long? reviewRevision, string? reviewVersion, bool alreadyConfirmed)
    {
        const string stale = "The payment claim changed or could not be verified. Reload and inspect the latest payment proof before verifying.";
        if (paymentId == null || paymentId == Guid.Empty || reviewRevision == null || reviewRevision < 0) throw new InvalidOperationException(stale);
        var locked = db is PublicMakeupDbContext
            ? db.Database.SqlQueryRaw<Guid>("SELECT p.app_id AS \"Value\" FROM public.payments p JOIN public.reservations r ON r.id=p.reservation_id WHERE r.app_id={0} AND p.app_id={1} AND p.purpose='down_payment' FOR UPDATE OF p", booking, paymentId.Value).ToList()
            : db.Database.SqlQueryRaw<Guid>("SELECT id AS \"Value\" FROM makeup.payments WHERE booking_id={0} AND id={1} AND purpose='down_payment' FOR UPDATE", booking, paymentId.Value).ToList();
        if (locked.Count != 1 || locked[0] != paymentId.Value) throw new InvalidOperationException(stale);
        var payment = ReviewPayments(booking).SingleOrDefault(p => p.Id == paymentId.Value);
        BookingService.ValidatePersistentPaymentReview(payment, paymentId, reviewRevision, reviewVersion, alreadyConfirmed);
    }
    protected virtual void Notify(Guid booking,string message) => db.Database.ExecuteSqlInterpolated($"""
        INSERT INTO makeup.notifications(user_id,booking_id,message,delivery_status,sent_at)
        SELECT u.id,b.id,b.reference||': '||{message},'Sent',now() FROM makeup.bookings b CROSS JOIN makeup.app_users u
        JOIN makeup.roles r ON r.id=u.role_id WHERE b.id={booking} AND (u.id=b.customer_id OR (r.name='Admin' AND u.is_active))
        """);

    // Files live in IAppFileStore (local disk for development, Supabase Storage
    // in production); DB rows retain immutable per-upload object keys either way.
    protected string AssetPath(string key)
    {
        var root=Path.GetFullPath(Path.Combine(environment.ContentRootPath,"App_Data"));
        var path=Path.GetFullPath(Path.Combine(root,key.Replace('/',Path.DirectorySeparatorChar)));
        if(!path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid private asset location.");
        return path;
    }
    protected Guid? ProofAsset(string? path,Guid user)
    {
        if(path==null) return null;
        var key="Proofs/"+Path.GetFileName(path);
        if(Path.GetFullPath(path)!=AssetPath(key)) throw new InvalidOperationException("Invalid proof upload location.");
        var bytes=File.ReadAllBytes(path); var id=Guid.NewGuid();
        AddAsset(id,key,"payment_proof",bytes,user); return id;
    }
    protected virtual void AddAsset(Guid id,string key,string purpose,byte[] bytes,Guid uploader)
    {
        var mime=ImageType(bytes);
        files.Save(key,bytes);
        try
        {
            db.Database.ExecuteSqlInterpolated($"INSERT INTO makeup.media_assets(id,object_key,purpose,content_type,byte_length,uploaded_by) VALUES({id},{key},{purpose},{mime},{bytes.Length},{uploader})");
        }
        catch { files.Delete(key); throw; }
    }
    protected static string ImageType(byte[] bytes)
    {
        if(bytes.Length is <1 or >5000000) throw new InvalidOperationException("Choose a JPG or PNG image up to 5 MB.");
        if(bytes.Length>8 && bytes.Take(8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10})) return "image/png";
        if(bytes.Length>3 && bytes[0]==255 && bytes[1]==216 && bytes[2]==255) return "image/jpeg";
        throw new InvalidOperationException("Upload a valid JPG or PNG image.");
    }
    public string? CatalogImageKey(string kind,Guid id,string slot)
    {
        var images=db.Set<ImageRow>().AsNoTracking().Where(x=>x.Slot==slot);
        images=kind=="service"?images.Where(x=>x.ServiceId==id):images.Where(x=>x.AddonId==id);
        return (from i in images join a in db.Set<AssetRow>() on i.AssetId equals a.Id select a.ObjectKey).SingleOrDefault();
    }
    public string? PaymentQrKey(string method) => (from m in db.Set<MethodRow>() join a in db.Set<AssetRow>() on m.QrAssetId equals a.Id where m.Name==method select a.ObjectKey).SingleOrDefault();
    public (byte[] Bytes,string ContentType)? ReadAsset(string? key) => files.Read(key);
    protected void SaveImage(byte[] bytes,string folder,Action<Guid> associate)
    {
        var mime=ImageType(bytes); var actor=Actor??throw new InvalidOperationException("Sign in as admin.");
        var id=Guid.NewGuid(); var key=$"{folder}/{id:N}{(mime=="image/png"?".png":".jpg")}";
        try { Transaction(actor,()=>{ Admin(actor); AddAsset(id,key,folder=="CatalogImages"?"catalog":"payment_qr",bytes,actor); associate(id); }); }
        catch { files.Delete(key); throw; }
    }
    public virtual void SaveCatalogImage(string kind,Guid id,string slot,byte[] bytes) => SaveImage(bytes,"CatalogImages",asset=>
    {
        if(kind=="service") db.Database.ExecuteSqlInterpolated($"INSERT INTO makeup.catalog_images(service_id,slot,asset_id) VALUES({id},{slot},{asset}) ON CONFLICT(service_id,slot) DO UPDATE SET asset_id=excluded.asset_id");
        else db.Database.ExecuteSqlInterpolated($"INSERT INTO makeup.catalog_images(addon_id,slot,asset_id) VALUES({id},{slot},{asset}) ON CONFLICT(addon_id,slot) DO UPDATE SET asset_id=excluded.asset_id");
    });
    public virtual void RemoveCatalogImage(string kind,Guid id,string slot) => Transaction(Actor,()=>
    {
        Admin(Actor??Guid.Empty);
        var key=CatalogImageKey(kind,id,slot);
        if(kind=="service") db.Database.ExecuteSqlInterpolated($"DELETE FROM makeup.catalog_images WHERE service_id={id} AND slot={slot}");
        else db.Database.ExecuteSqlInterpolated($"DELETE FROM makeup.catalog_images WHERE addon_id={id} AND slot={slot}");
        files.Delete(key);
    });
    public virtual void SavePaymentQr(string method,byte[] bytes) => SaveImage(bytes,"PaymentQr",asset=>
    {
        if(db.Database.ExecuteSqlInterpolated($"UPDATE makeup.payment_methods SET qr_asset_id={asset} WHERE name={method} AND accepts_deposits")==0)
            throw new InvalidOperationException("Unsupported QR payment method.");
    });
    public virtual void RemovePaymentQr(string method) => Transaction(Actor,()=>
    {
        Admin(Actor??Guid.Empty);
        var key=PaymentQrKey(method);
        db.Database.ExecuteSqlInterpolated($"UPDATE makeup.payment_methods SET qr_asset_id=NULL WHERE name={method} AND accepts_deposits");
        files.Delete(key);
    });
}
