using System.Security.Claims;
using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GlamByMariya.Infrastructure.Data;

// Public-schema adapter: reads reuse base view projections; writes target the
// original bigint-keyed tables via stable app_id lookup keys. Original primary
// keys, references and legacy rows are never rewritten.
public sealed class PublicStudioStore(
    PublicMakeupDbContext pdb,
    IHttpContextAccessor httpAccessor,
    IWebHostEnvironment env,
    IConfiguration config,
    IAppFileStore files) : PostgresStudioStore(pdb, httpAccessor, env, config, files)
{
    private Guid? ActorId => Guid.TryParse(httpAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    // shortcut: requires unfiltered table visibility; replace with a reviewed scoped database contract before activation.
    internal void EnsureMixedReadVisibility()
    {
        if (pdb.Database.CurrentTransaction == null ||
            !pdb.Database.SqlQueryRaw<bool>("""
                SELECT current_setting('transaction_isolation') IN ('repeatable read','serializable')
                  AND NOT EXISTS (
                    SELECT 1
                    FROM unnest(ARRAY[
                      'public.reservations','public.customers','public.admin_users','public.account_identities',
                      'public.reservation_mixed_snapshots','public.reservation_mixed_people',
                      'public.reservation_mixed_person_addons','public.mixed_service_classifications',
                      'public.services','public.payments','public.payment_revisions'
                    ]) AS required(name)
                    LEFT JOIN pg_class c ON c.oid=to_regclass(required.name)
                    WHERE c.oid IS NULL OR c.relkind NOT IN ('r','p')
                       OR NOT has_table_privilege(current_user,c.oid,'SELECT')
                       OR row_security_active(c.oid)
                  ) AS "Value"
                """).Single())
            throw new InvalidOperationException("Mixed booking read visibility is unavailable.");
    }

    private static string NewReference(Guid appId) => "LM-" + appId.ToString("N")[..8].ToUpperInvariant();

    private long CustomerBigId(Guid appId) =>
        pdb.Database.SqlQueryRaw<long>("SELECT id AS \"Value\" FROM public.customers WHERE app_id = {0}", appId).SingleOrDefault()
        is long id && id != 0 ? id : throw new InvalidOperationException("Customer record not found.");

    private long ServiceBigId(Guid appId) =>
        pdb.Database.SqlQueryRaw<long>("SELECT id AS \"Value\" FROM public.services WHERE app_id = {0}", appId).SingleOrDefault()
        is long id && id != 0 ? id : throw new InvalidOperationException("Service not found.");

    internal List<ScheduleWindow> OccupiedIntervals(DateTime from, DateTime until, Guid? exclude)
    {
        try
        {
            var (artist, _) = ResolveArtist();
            var excluded = new NpgsqlParameter("excluded", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)exclude ?? DBNull.Value };
            var rows = pdb.Database.SqlQueryRaw<OccupiedInterval>(
                """SELECT occupied_from AS "From", occupied_until AS "Until" FROM public.mvc_occupied_intervals({0},{1},{2},@excluded)""",
                artist, Utc(from), Utc(until), excluded).ToList();
            return rows.Select(r => new ScheduleWindow(Local(r.From), Local(r.Until))).ToList();
        }
        catch (Exception e) when (e is PostgresException or NpgsqlException)
        {
            throw new InvalidOperationException("Availability cannot be verified. Please try again later.", e);
        }
    }
    // Admin → Customers: when each booking was made and each customer account was created.
    public override IReadOnlyDictionary<Guid, DateTime> BookingCreatedTimes() =>
        pdb.Database.SqlQueryRaw<CreatedTime>("""SELECT app_id AS "Id", created_at AS "At" FROM public.reservations WHERE app_id IS NOT NULL""")
            .AsEnumerable().ToDictionary(r => r.Id, r => Local(r.At));
    public override IReadOnlyDictionary<Guid, DateTime> CustomerCreatedTimes() =>
        pdb.Database.SqlQueryRaw<CreatedTime>("""SELECT app_id AS "Id", created_at AS "At" FROM public.customers WHERE app_id IS NOT NULL""")
            .AsEnumerable().ToDictionary(r => r.Id, r => Local(r.At));
    private sealed class CreatedTime
    {
        public Guid Id { get; set; }
        public DateTime At { get; set; }
    }
    private sealed class OccupiedInterval
    {
        public DateTime From { get; set; }
        public DateTime Until { get; set; }
    }

    // Travel settings (0015) are read here so the legacy makeup schema's settings row stays unchanged.
    protected override BusinessSettings LoadSettings()
    {
        var value = base.LoadSettings();
        var travel = pdb.Database.SqlQueryRaw<TravelSettingsRow>(
            """SELECT travel_speed_kmh AS "Speed", travel_min_minutes AS "Minimum" FROM public.business_settings WHERE id=1""").AsEnumerable().Single();
        value.TravelSpeedKmh = travel.Speed;
        value.TravelMinimumMinutes = travel.Minimum;
        // Flexible booking hours (0020); null means opening/closing hour.
        var hours = pdb.Database.SqlQueryRaw<FlexibleHoursRow>(
            """SELECT early_request_start_hour AS "Early", after_hours_end_hour AS "Latest" FROM public.business_settings WHERE id=1""").AsEnumerable().Single();
        value.EarlyRequestStartHour = hours.Early;
        value.AfterHoursEndHour = hours.Latest;
        return value;
    }
    private sealed class FlexibleHoursRow
    {
        public int? Early { get; set; }
        public int? Latest { get; set; }
    }
    private sealed class TravelSettingsRow
    {
        public decimal Speed { get; set; }
        public int Minimum { get; set; }
    }

    public override ArtistLocation GetArtistLocation()
    {
        var (artist, _) = ResolveArtist();
        var row = pdb.Database.SqlQueryRaw<ArtistLocationRow>(
            """SELECT home_lat AS "HomeLat", home_lng AS "HomeLng", current_lat AS "CurrentLat", current_lng AS "CurrentLng", current_updated_at AS "CurrentUpdatedAt" FROM public.makeup_artists WHERE id={0}""",
            artist).AsEnumerable().Single();
        return new(row.HomeLat, row.HomeLng, row.CurrentLat, row.CurrentLng, row.CurrentUpdatedAt is { } at ? Local(at) : null);
    }
    private sealed class ArtistLocationRow
    {
        public decimal? HomeLat { get; set; }
        public decimal? HomeLng { get; set; }
        public decimal? CurrentLat { get; set; }
        public decimal? CurrentLng { get; set; }
        public DateTime? CurrentUpdatedAt { get; set; }
    }

    public override void SetArtistHome(decimal lat, decimal lng)
    {
        if (!BookingLocation.IsValid(lat, lng)) throw new InvalidOperationException(BookingLocation.ArtistMessage);
        Transaction(ActorId, () =>
        {
            var (artist, _) = ResolveArtist();
            pdb.Database.ExecuteSqlInterpolated($"UPDATE public.makeup_artists SET home_lat={lat}, home_lng={lng} WHERE id={artist}");
        });
    }

    // Null clears the current pin: the artist is back home.
    public override void SetArtistCurrent(decimal? lat, decimal? lng)
    {
        if ((lat, lng) is not (null, null) && !BookingLocation.IsValid(lat, lng)) throw new InvalidOperationException(BookingLocation.ArtistMessage);
        Transaction(ActorId, () =>
        {
            var (artist, _) = ResolveArtist();
            if (lat is { } y && lng is { } x)
                pdb.Database.ExecuteSqlInterpolated($"UPDATE public.makeup_artists SET current_lat={y}, current_lng={x}, current_updated_at=now() WHERE id={artist}");
            else
                pdb.Database.ExecuteSqlRaw("UPDATE public.makeup_artists SET current_lat=NULL, current_lng=NULL, current_updated_at=NULL WHERE id={0}", artist);
        });
    }

    public override IReadOnlyList<TravelAppointment> TravelStops(DateTime from, DateTime until, Guid? exclude)
    {
        try
        {
            var excluded = new NpgsqlParameter("excluded", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)exclude ?? DBNull.Value };
            var rows = pdb.Database.SqlQueryRaw<TravelStopRow>(
                """SELECT start_at AS "Start", end_at AS "End", buffer_minutes AS "BufferMinutes", location_lat AS "Lat", location_lng AS "Lng" FROM public.mvc_travel_stops({0},{1},@excluded)""",
                Utc(from), Utc(until), excluded).ToList();
            return rows.Select(r => new TravelAppointment(Local(r.Start), Local(r.End), r.BufferMinutes, r.Lat, r.Lng)).OrderBy(x => x.Start).ToList();
        }
        catch (Exception e) when (e is PostgresException or NpgsqlException)
        {
            throw new InvalidOperationException("Availability cannot be verified. Please try again later.", e);
        }
    }
    // Only times and pins leave the database, as in mvc_travel_stops.
    public override IReadOnlyList<TravelAppointment> FinishedStops(DateTime from, DateTime until)
    {
        try
        {
            var rows = pdb.Database.SqlQueryRaw<TravelStopRow>(
                """SELECT start_at AS "Start", end_at AS "End", buffer_minutes_snapshot AS "BufferMinutes", location_lat AS "Lat", location_lng AS "Lng" FROM public.reservations WHERE status IN ('InProgress','Completed') AND start_at < {1} AND end_at > {0}""",
                Utc(from), Utc(until)).ToList();
            return rows.Select(r => new TravelAppointment(Local(r.Start), Local(r.End), r.BufferMinutes, r.Lat, r.Lng)).OrderBy(x => x.End).ToList();
        }
        catch (Exception e) when (e is PostgresException or NpgsqlException)
        {
            throw new InvalidOperationException("Availability cannot be verified. Please try again later.", e);
        }
    }
    private sealed class TravelStopRow
    {
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public int BufferMinutes { get; set; }
        public decimal? Lat { get; set; }
        public decimal? Lng { get; set; }
    }

    // Serializes travel checks per artist until the transaction ends, so two saves that each fit
    // on their own cannot both pass (the overlap trigger alone does not see travel).
    private void LockTravel(long artistId) =>
        pdb.Database.ExecuteSqlRaw("SELECT pg_advisory_xact_lock(hashtext('travel:' || {0}::text))", artistId);

    private (long Id, Guid AppId) ResolveArtist()
    {
        var configured = config["Database:ArtistId"];
        if (Guid.TryParse(configured, out var appId))
        {
            var id = pdb.Database.SqlQueryRaw<long>("SELECT id AS \"Value\" FROM public.makeup_artists WHERE app_id = {0} AND is_active", appId).SingleOrDefault();
            if (id == 0) throw new InvalidOperationException("The configured artist is not active.");
            return (id, appId);
        }
        var rows = pdb.Set<PublicArtistRow>().AsNoTracking().Where(a => a.IsActive).OrderBy(a => a.Id).Take(2).ToList();
        if (rows.Count != 1) throw new InvalidOperationException("Two artists are active (Angela Cruz, Bianca Reyes). Configure Database:ArtistId with the business's artist app_id; no artist was chosen automatically.");
        return (rows[0].Id, rows[0].AppId);
    }

    private long ReservationBigId(Guid appId) =>
        pdb.Database.SqlQueryRaw<long>("SELECT id AS \"Value\" FROM public.reservations WHERE app_id = {0}", appId).SingleOrDefault()
        is long id && id != 0 ? id : throw new InvalidOperationException("Booking not found.");

    protected override Guid Artist() => ResolveArtist().AppId;

    protected override void Admin(Guid id) =>
        pdb.Database.ExecuteSqlRaw("SELECT public.mvc_assert_role({0}, 'Admin')", id);

    protected override BookingRow LockBooking(Guid id)
    {
        pdb.Database.ExecuteSqlRaw("SELECT id FROM public.reservations WHERE app_id = {0} FOR UPDATE", id);
        return pdb.Set<BookingRow>().AsNoTracking().SingleOrDefault(x => x.Id == id)
            ?? throw new InvalidOperationException("Booking not found.");
    }

    protected override string Friendly(PostgresException e)
    {
        if (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            if (e.ConstraintName is "ux_users_email" or "account_identities_login_key" or null)
            {
                // account_identities partial unique index has an auto name; check message fallback.
                if ((e.MessageText.Contains("account_identities", StringComparison.OrdinalIgnoreCase) && e.MessageText.Contains("login", StringComparison.OrdinalIgnoreCase)) || e.ConstraintName == "ux_users_email")
                    return "This email is already registered.";
            }
            if (e.ConstraintName is "reservations_reference_key" or "ux_mvc_payment_reference" or "ux_mvc_refund_reference")
                return "That transaction reference already exists. Refresh and check your records before retrying.";
            return "That record already exists. Refresh and check your records before retrying.";
        }
        return base.Friendly(e);
    }

    public override AppUser AddUser(string firstName, string lastName, string email, string password, string role = "Customer", int age = 0, string gender = "", string address = "")
    {
        var error = RegistrationRules.Validate(firstName, lastName, email, age, gender, address, password);
        if (error != null) throw new InvalidOperationException(error);
        if (role is not ("Customer" or "Admin")) throw new InvalidOperationException("Invalid account role.");
        var user = new AppUser
        {
            FirstName = firstName.Trim(), LastName = lastName.Trim(), Name = $"{firstName.Trim()} {lastName.Trim()}",
            Email = email.Trim(), Age = age, Gender = gender, Address = address.Trim(), Role = role
        };
        user.PasswordHash = new PasswordHasher<AppUser>().HashPassword(user, password);
        Transaction(ActorId, () =>
        {
            if (role == "Customer")
            {
                pdb.Database.ExecuteSqlInterpolated($"INSERT INTO public.customers(app_id, full_name, first_name, last_name, age_at_registration, gender, address, email, password_hash, is_active) VALUES({user.Id}, {user.Name}, {user.FirstName}, {user.LastName}, {age}, {gender}, {user.Address}, {user.Email}, {user.PasswordHash}, true)");
            }
            else
            {
                if (user.Email.Length > 50) throw new InvalidOperationException("Admin email must be 50 characters or fewer (legacy username limit).");
                pdb.Database.ExecuteSqlInterpolated($"INSERT INTO public.admin_users(app_id, username, email, first_name, last_name, age_at_registration, gender, address, password_hash, role, is_active) VALUES({user.Id}, {user.Email}, {user.Email}, {user.FirstName}, {user.LastName}, {age}, {gender}, {user.Address}, {user.PasswordHash}, 'Admin', true)");
            }
        });
        return user;
    }

    public override AppUser AddCustomerWithPasswordHash(string firstName, string lastName, string email, string passwordHash, int age, string gender, string address, DateOnly? birthday = null, string? suffix = null)
    {
        if (string.IsNullOrWhiteSpace(passwordHash)) throw new InvalidOperationException("Registration expired. Please register again.");
        var error = RegistrationRules.ValidateProfile(firstName, lastName, email, age, gender, address, enforceLegacyAge: false);
        if (error != null) throw new InvalidOperationException(error);
        var user = new AppUser
        {
            FirstName = firstName.Trim(), LastName = lastName.Trim(), Name = $"{firstName.Trim()} {lastName.Trim()}",
            Email = email.Trim(), Age = age, Gender = gender, Address = address.Trim(), Role = "Customer", PasswordHash = passwordHash,
            Birthday = birthday, Suffix = string.IsNullOrEmpty(suffix) ? null : suffix
        };
        var storedSuffix = user.Suffix ?? "";
        // Requires customers.birthday and customers.suffix (database/v2/migrations/0006_customer_birthday_suffix.sql).
        Transaction(ActorId, () => pdb.Database.ExecuteSqlInterpolated($"INSERT INTO public.customers(app_id, full_name, first_name, last_name, age_at_registration, gender, address, email, password_hash, is_active, birthday, suffix) VALUES({user.Id}, {user.Name}, {user.FirstName}, {user.LastName}, {age}, {gender}, {user.Address}, {user.Email}, {user.PasswordHash}, true, {birthday}, {storedSuffix})"));
        return user;
    }

    public override AppUser CreateElevatedUser(string firstName, string lastName, string email, string password, string role, int age, string gender, string address)
        => CreateElevatedUserCore(firstName, lastName, email, password, role, age, gender, address, false);

    public AppUser CreateInitialSuperAdmin(string firstName, string lastName, string email, string password)
        => CreateElevatedUserCore(firstName, lastName, email, password, "SuperAdmin", 35, "", "Address on file", true);

    private AppUser CreateElevatedUserCore(string firstName, string lastName, string email, string password, string role, int age, string gender, string address, bool initialOwner)
    {
        if (role is not ("Admin" or "SuperAdmin")) throw new InvalidOperationException("Invalid elevated role.");
        var error = RegistrationRules.Validate(firstName, lastName, email, age, gender, address, password);
        if (error != null) throw new InvalidOperationException(error);
        if (email.Trim().Length > 50) throw new InvalidOperationException("Admin email must be 50 characters or fewer (legacy username limit).");
        var user = new AppUser
        {
            FirstName = firstName.Trim(),
            LastName = lastName.Trim(),
            Name = $"{firstName.Trim()} {lastName.Trim()}",
            Email = email.Trim(),
            Age = age,
            Gender = gender,
            Address = address.Trim(),
            Role = role,
        };
        user.PasswordHash = new PasswordHasher<AppUser>().HashPassword(user, password);
        Transaction(ActorId, () =>
        {
            if (initialOwner)
            {
                // Serialize competing bootstrap commands until the insert commits.
                pdb.Database.ExecuteSqlRaw("SELECT pg_advisory_xact_lock(718430512, 1)");
                var restricted = pdb.Database.SqlQueryRaw<bool>("""
                    SELECT (row_security_active('public.admin_users'::regclass)
                        OR row_security_active('public.customers'::regclass)
                        OR row_security_active('public.account_identities'::regclass))
                       -- The app login (member of makeup_backend) sees every row through the
                       -- unrestricted backend policies, so the counts below are complete.
                       AND NOT pg_has_role(current_user, 'makeup_backend', 'MEMBER') AS "Value"
                    """).Single();
                if (restricted) throw new InvalidOperationException("Initial SuperAdmin setup requires unfiltered account visibility.");
                var existing = pdb.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM public.admin_users WHERE role='SuperAdmin'").Single();
                if (existing > 0) throw new InvalidOperationException("A SuperAdmin already exists. Initial setup is permanently disabled.");
                if (FindUser(email) != null) throw new InvalidOperationException("That email already exists. No account was overwritten or reactivated.");
                // The owner is the business's only makeup artist: create that artist row in the same transaction.
                if (pdb.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM public.makeup_artists WHERE is_active").Single() > 0)
                    throw new InvalidOperationException("An active makeup artist already exists. Initial setup is permanently disabled.");
                pdb.Database.ExecuteSqlInterpolated($"INSERT INTO public.makeup_artists(full_name, is_active) VALUES({user.Name}, true)");
            }
            pdb.Database.ExecuteSqlInterpolated($"INSERT INTO public.admin_users(app_id, username, email, first_name, last_name, age_at_registration, gender, address, password_hash, role, is_active) VALUES({user.Id}, {user.Email}, {user.Email}, {user.FirstName}, {user.LastName}, {age}, {gender}, {user.Address}, {user.PasswordHash}, {role}, true)");
        });
        return user;
    }

    public override void SaveCatalog(string kind, CatalogItem item, Guid actor) => Transaction(actor, () =>
    {
        Admin(actor);
        var category = pdb.Set<CategoryRow>().SingleOrDefault(x => x.Name.ToLower() == item.Category.ToLower());
        var categoryId = category?.Id ?? Guid.NewGuid();
        if (category == null) pdb.Database.ExecuteSqlInterpolated($"INSERT INTO public.catalog_categories(id, name) VALUES({categoryId}, {item.Category})");
        if (kind == "service")
        {
            var exists = pdb.Database.SqlQueryRaw<long>("SELECT count(*)::bigint AS \"Value\" FROM public.services WHERE app_id = {0}", item.Id).Single() > 0;
            if (!exists) pdb.Database.ExecuteSqlInterpolated($"INSERT INTO public.services(app_id, category_id, name, description, price, duration_minutes, group_price, group_minimum_people, visual_word, is_active, is_archived) VALUES({item.Id}, {categoryId}, {item.Name}, {item.Description}, {item.Price}, {item.Minutes}, {item.GroupPrice}, {item.GroupMinimumPeople}, {item.VisualWord}, {item.Active}, {item.Archived})");
            else pdb.Database.ExecuteSqlInterpolated($"UPDATE public.services SET category_id={categoryId}, name={item.Name}, description={item.Description}, price={item.Price}, duration_minutes={item.Minutes}, group_price={item.GroupPrice}, group_minimum_people={item.GroupMinimumPeople}, visual_word={item.VisualWord}, is_active={item.Active}, is_archived={item.Archived} WHERE app_id={item.Id}");
            // Package limit (0020): null = no limit.
            pdb.Database.ExecuteSqlInterpolated($"UPDATE public.services SET max_per_package={item.MaxPerPackage} WHERE app_id={item.Id}");
        }
        else if (kind == "addon")
        {
            var exists = pdb.Set<AddonRow>().Any(x => x.Id == item.Id);
            if (!exists) pdb.Database.ExecuteSqlInterpolated($"INSERT INTO public.makeup_addons(id, category_id, name, description, price, minutes, visual_word, is_active, is_archived) VALUES({item.Id}, {categoryId}, {item.Name}, {item.Description}, {item.Price}, {item.Minutes}, {item.VisualWord}, {item.Active}, {item.Archived})");
            else pdb.Database.ExecuteSqlInterpolated($"UPDATE public.makeup_addons SET category_id={categoryId}, name={item.Name}, description={item.Description}, price={item.Price}, minutes={item.Minutes}, visual_word={item.VisualWord}, is_active={item.Active}, is_archived={item.Archived} WHERE id={item.Id}");
        }
        else throw new InvalidOperationException("Invalid catalog type.");
    });

    public override void DeleteCatalog(string kind, CatalogItem item, Guid actor) => Transaction(actor, () =>
    {
        Admin(actor);
        if (kind == "service")
        {
            pdb.Database.ExecuteSqlRaw("SELECT id FROM public.services WHERE app_id = {0} FOR UPDATE", item.Id);
            var serviceId = ServiceBigId(item.Id);
            var referenced = pdb.Database.SqlQueryRaw<long>("SELECT count(*)::bigint AS \"Value\" FROM public.reservations WHERE service_id = {0}", serviceId).Single() > 0;
            if (referenced) pdb.Database.ExecuteSqlInterpolated($"UPDATE public.services SET is_active=false, is_archived=true WHERE app_id={item.Id}");
            else pdb.Database.ExecuteSqlInterpolated($"DELETE FROM public.services WHERE app_id={item.Id}");
        }
        else if (kind == "addon")
        {
            pdb.Database.ExecuteSqlRaw("SELECT id FROM public.makeup_addons WHERE id = {0} FOR UPDATE", item.Id);
            var referenced = pdb.Database.SqlQueryRaw<long>("SELECT count(*)::bigint AS \"Value\" FROM public.reservation_addons WHERE addon_id = {0}", item.Id).Single() > 0;
            if (referenced) pdb.Database.ExecuteSqlInterpolated($"UPDATE public.makeup_addons SET is_active=false, is_archived=true WHERE id={item.Id}");
            else pdb.Database.ExecuteSqlInterpolated($"DELETE FROM public.makeup_addons WHERE id={item.Id}");
        }
        else throw new InvalidOperationException("Invalid catalog type.");
    });

    public override void SaveSettings(BusinessSettings value, Guid actor) => Transaction(actor, () =>
    {
        Admin(actor);
        var names = value.ServiceAreas.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length == 0 || names.Any(x => x.Length > 100)) throw new InvalidOperationException("Provide service area names of at most 100 characters.");
        pdb.Database.ExecuteSqlInterpolated($"UPDATE public.business_settings SET time_zone={value.TimeZone}, open_hour={value.OpenHour}, close_hour={value.CloseHour}, buffer_minutes={value.BufferMinutes}, down_payment_percent={value.DownPaymentPercent}, travel_fee_informational={value.TravelFee}, payment_instructions={value.PaymentInstructions}, travel_speed_kmh={value.TravelSpeedKmh}, travel_min_minutes={value.TravelMinimumMinutes}, early_request_start_hour={value.EarlyRequestStartHour}, after_hours_end_hour={value.AfterHoursEndHour} WHERE id=1");
        pdb.Database.ExecuteSqlRaw("UPDATE public.service_areas SET is_active=false");
        foreach (var name in names)
            pdb.Database.ExecuteSqlInterpolated($"INSERT INTO public.service_areas(name) VALUES({name}) ON CONFLICT DO NOTHING");
        foreach (var name in names)
            pdb.Database.ExecuteSqlInterpolated($"UPDATE public.service_areas SET is_active=true WHERE lower(name)=lower({name})");
    });

    public override void AddBlock(AvailabilityBlock block, Guid actor) => Transaction(actor, () =>
    {
        Admin(actor);
        var (artistId, _) = ResolveArtist();
        var from = Utc(block.From); var to = Utc(block.To);
        pdb.Database.ExecuteSqlInterpolated($"INSERT INTO public.blocked_slots(app_id, artist_id, start_at, end_at, reason) VALUES({block.Id}, {artistId}, {from}, {to}, {block.Reason})");
    });

    public override void RemoveBlock(Guid id, Guid actor) => Transaction(actor, () =>
    {
        Admin(actor);
        pdb.Database.ExecuteSqlInterpolated($"DELETE FROM public.blocked_slots WHERE app_id={id}");
    });

    public override void RecordAudit(Guid actor, string message) => Transaction(actor, () =>
        pdb.Database.ExecuteSqlInterpolated($"INSERT INTO public.audit_logs(actor_id, entity_type, event) VALUES({actor}, 'application', {message})"));

    public override void SetAdminActive(Guid admin, bool active, Guid actor) => Transaction(actor, () =>
    {
        if (pdb.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM public.admin_users WHERE app_id={actor} AND role='SuperAdmin' AND is_active").Single() == 0)
            throw new InvalidOperationException("Only the Super Admin can disable or enable administrators.");
        // Role 'Admin' only: a SuperAdmin (the owner) can never be disabled here. The new
        // security stamp marks the change; the cookie check ends sessions via is_active.
        if (pdb.Database.ExecuteSqlInterpolated($"UPDATE public.admin_users SET is_active={active}, security_stamp=gen_random_uuid() WHERE app_id={admin} AND role='Admin'") == 0)
            throw new InvalidOperationException("Only Admin accounts can be disabled or enabled.");
    });

    public override void ResetPassword(string email, string newPassword)
    {
        var passwordError = PasswordPolicy.ValidateNewPassword(newPassword);
        if (passwordError != null) throw new InvalidOperationException(passwordError);
        var existing = FindUser(email) ?? throw new InvalidOperationException("No account uses that login. No password was changed.");
        var tmp = new AppUser { FirstName = existing.FirstName, LastName = existing.LastName };
        var hash = new PasswordHasher<AppUser>().HashPassword(tmp, newPassword);
        Transaction(existing.Id, () =>
        {
            // Reset/change only rotates credentials and the security stamp.
            // is_active is deliberately never touched: a disabled account that
            // resets its password must remain disabled (no silent reactivation).
            var updated = pdb.Database.ExecuteSqlInterpolated($"UPDATE public.customers SET password_hash={hash}, security_stamp=gen_random_uuid() WHERE app_id={existing.Id}");
            if (updated == 0)
                pdb.Database.ExecuteSqlInterpolated($"UPDATE public.admin_users SET password_hash={hash}, security_stamp=gen_random_uuid() WHERE app_id={existing.Id}");
        });
    }

    public override Booking SubmitBooking(Guid customer, Guid service, Guid[] extras, DateTime start, string address, string area, decimal deposit, string method, string reference, string? proof, bool agreed, bool travelAcknowledged, int people, string package, decimal? expectedTotal = null, decimal? expectedMinimum = null, decimal? latitude = null, decimal? longitude = null)
    {
        if (!BookingLocation.IsValid(latitude, longitude)) throw new InvalidOperationException(BookingLocation.Message);
        if (!agreed || !travelAcknowledged) throw new InvalidOperationException("Read and accept the Transportation Fee Policy before submitting payment.");
        if (package == "auto" && people != 1) throw new InvalidOperationException("Individual bookings must have exactly one person.");
        BookingService.ValidatePayment(method, reference);
        Booking? created = null;
        for (var attempt = 0; attempt < 3 && created == null; attempt++)
        {
            var appId = Guid.NewGuid();
            try
            {
                Transaction(customer, () =>
                {
                    pdb.Database.ExecuteSqlRaw("SELECT id FROM public.business_settings WHERE id=1 FOR SHARE");
                    pdb.Database.ExecuteSqlInterpolated($"SELECT id FROM public.services WHERE app_id = {service} FOR SHARE");
                    foreach (var extra in extras.Distinct().Order()) pdb.Database.ExecuteSqlInterpolated($"SELECT id FROM public.makeup_addons WHERE id = {extra} FOR SHARE");
                    settings = null;
                    var selected = Services.FirstOrDefault(x => x.Id == service && x.Active && !x.Archived) ?? throw new InvalidOperationException("Choose an active service.");
                    var selectedExtras = PreparedPersistentBooking.Enabled ? PreparedPersistentBooking.Resolve(extras, people, AddOns) : extras.Distinct().Select(x => AddOns.FirstOrDefault(a => a.Id == x && a.Active && !a.Archived) ?? throw new InvalidOperationException("An add-on is no longer available.")).ToList();
            var quote = BookingPricing.Calculate(selected, selectedExtras, people, Settings.TravelFee, package, Settings.DownPaymentPercent);
                    BookingService.ValidateExpectedQuote(quote, expectedTotal, expectedMinimum);
                    PreparedPersistentBooking.ValidateClaim(deposit, quote);
                    var coverage = pdb.Set<AreaRow>().SingleOrDefault(x => x.IsActive && x.Name.ToLower() == area.Trim().ToLower()) ?? throw new InvalidOperationException("The address must be within the service area.");
                    var (artistId, _) = ResolveArtist();
                    LockTravel(artistId);
                    if (!BookingService.TravelFits(this, start, quote.DurationMinutes, Settings.BufferMinutes, latitude, longitude, null, Settings))
                        throw new InvalidOperationException(TravelPlanner.RefusalMessage);
                    var customerId = CustomerBigId(customer);
                    var serviceId = ServiceBigId(service);
                    var utc = Utc(start);
                    var type = package == "group" ? "package" : "individual";
                    var extrasTotal = selectedExtras.Sum(x => x.Price);
                    var extrasMinutes = selectedExtras.Sum(x => x.Minutes);
                    var bookingRef = NewReference(appId);
                    // Phase 6: an early time is a request awaiting the artist (the guard skips the hold); after-hours is flagged.
                    var early = BookingWindow.KindOf(start, Settings) == SlotKind.Early;
                    var afterHours = BookingWindow.IsAfterHours(start, quote.DurationMinutes, Settings);
                     pdb.Database.ExecuteSqlInterpolated($"INSERT INTO public.reservations(app_id, app_managed, snapshot_source, reference, customer_id, artist_id, service_id, start_at, location, area_id, booking_type, people, addons_total_snapshot, addons_minutes_snapshot, down_payment_percent_snapshot, required_down_payment_snapshot, policy_accepted_at, policy_version, travel_reminder_acknowledged_at, travel_reminder_version, status, location_lat, location_lng, is_early_request, after_hours) VALUES({appId}, true, 'booking_creation', {bookingRef}, {customerId}, {artistId}, {serviceId}, {utc}, {address.Trim()}, {coverage.Id}, {type}, {people}, {extrasTotal}, {extrasMinutes}, {Settings.DownPaymentPercent}, {quote.RequiredDownPayment}, now(), {Settings.PolicyVersion}, now(), {Settings.TravelReminderVersion}, {(early ? BookingService.EarlyRequestStatus : "Pending Payment")}, {latitude}, {longitude}, {early}, {afterHours})");
                    var reservationId = pdb.Database.SqlQueryRaw<long>("SELECT id AS \"Value\" FROM public.reservations WHERE app_id = {0}", appId).Single();
                    foreach (var group in selectedExtras.GroupBy(x => x.Id))
                        if (PreparedPersistentBooking.Enabled) pdb.Database.ExecuteSqlInterpolated($"INSERT INTO public.reservation_addons(reservation_id, addon_id, quantity) VALUES({reservationId}, {group.Key}, {group.Count()})");
                        else pdb.Database.ExecuteSqlInterpolated($"INSERT INTO public.reservation_addons(reservation_id, addon_id) VALUES({reservationId}, {group.Key})");
                    var paymentMethod = pdb.Set<MethodRow>().SingleOrDefault(x => x.Name == method && x.IsActive && x.AcceptsDeposits) ?? throw new InvalidOperationException("Use GCash, Maya or Bank Transfer for your deposit.");
                    var asset = ProofAsset(proof, customer);
                    pdb.Database.ExecuteSqlInterpolated($"INSERT INTO public.payments(reservation_id, app_id, app_managed, purpose, amount, method, reference, status, verification_status, proof_asset_id, submitted_by) VALUES({reservationId}, {Guid.NewGuid()}, true, 'down_payment', {deposit}, {method}, {reference.Trim()}, 'Pending', 'Pending Verification', {asset}, {customer})");
                    _ = paymentMethod;
                    // A manual claim (GCash, Maya, bank) waits for the owner's review, so it never expires; only an
                    // unpaid Xendit checkout keeps the 15-minute hold.
                    if (method != "Xendit") pdb.Database.ExecuteSqlInterpolated($"UPDATE public.reservations SET payment_expires_at = NULL WHERE id = {reservationId}");
                });
                created = Bookings.Single(x => x.Id == appId);
            }
            catch (InvalidOperationException e) when (e.InnerException is PostgresException pg && pg.ConstraintName == "reservations_reference_key" && attempt < 2)
            {
                continue; // Rare short-reference collision: retry with a fresh booking identity.
            }
        }
        return created ?? throw new InvalidOperationException("Could not create the booking. Please retry.");
    }

    // Phase 6: one booking for a mixed package (e.g. 1 Bridal + 2 Soft Glam + 3 Glamour). Each person pays
    // their service's regular price plus add-ons; the admin's per-package limits apply (checked here and
    // again by the database at commit). Same locks, travel rule and early/after-hours rules as SubmitBooking.
    public Booking SubmitPackage(Guid customer, IReadOnlyList<MixedPersonSelection> people, DateTime start, string address, string area,
        decimal deposit, string method, string reference, bool agreed, bool travelAcknowledged, decimal expectedTotal, decimal expectedMinimum,
        decimal latitude, decimal longitude)
    {
        if (!BookingLocation.IsValid(latitude, longitude)) throw new InvalidOperationException(BookingLocation.Message);
        if (!agreed || !travelAcknowledged) throw new InvalidOperationException("Read and accept the Transportation Fee Policy before submitting payment.");
        BookingService.ValidatePayment(method, reference);
        Booking? created = null;
        for (var attempt = 0; attempt < 3 && created == null; attempt++)
        {
            var appId = Guid.NewGuid();
            Guid? repeated = null;
            try
            {
                Transaction(customer, () =>
                {
                    pdb.Database.ExecuteSqlRaw("SELECT id FROM public.business_settings WHERE id=1 FOR SHARE");
                    settings = null;
                    var services = Services; var addOns = AddOns;
                    var chosen = people.Select(p => services.FirstOrDefault(s => s.Id == p.ServiceId && s.Active && !s.Archived)
                        ?? throw new InvalidOperationException("Choose an active service for every person.")).ToList();
                    if (PackageRules.Check(chosen) is { } refused) throw new InvalidOperationException(refused);
                    var extras = people.Select(p => p.AddOnIds.Distinct().Select(id => addOns.FirstOrDefault(a => a.Id == id && a.Active && !a.Archived)
                        ?? throw new InvalidOperationException("An add-on is no longer available.")).ToList()).ToList();
                    var prices = chosen.Select(s => PackageRules.PricePerPerson(s, chosen.Count)).ToList();
                    var total = prices.Sum() + extras.Sum(e => e.Sum(a => a.Price));
                    var minutes = chosen.Sum(s => s.Minutes) + extras.Sum(e => e.Sum(a => a.Minutes));
                    var required = Money.Percentage(total, Settings.DownPaymentPercent);
                    if (expectedTotal != total || expectedMinimum != required) throw new InvalidOperationException("The booking price or minimum payment changed. Please review the updated quote.");
                    Money.RequireDeposit(deposit, required, total);
                    var coverage = pdb.Set<AreaRow>().SingleOrDefault(x => x.IsActive && x.Name.ToLower() == area.Trim().ToLower()) ?? throw new InvalidOperationException("The address must be within the service area.");
                    var (artistId, _) = ResolveArtist();
                    LockTravel(artistId);
                    if (!BookingService.TravelFits(this, start, minutes, Settings.BufferMinutes, latitude, longitude, null, Settings))
                        throw new InvalidOperationException(TravelPlanner.RefusalMessage);
                    var early = BookingWindow.KindOf(start, Settings) == SlotKind.Early;
                    var afterHours = BookingWindow.IsAfterHours(start, minutes, Settings);
                    // A request holds no time, so nothing else stops the same early package being sent twice
                    // (a double tap or a resubmitted page): under the travel lock, reuse the one just sent.
                    if (early)
                    {
                        repeated = pdb.Database.SqlQuery<Guid>($"""
                            SELECT app_id AS "Value" FROM public.reservations
                            WHERE customer_id={CustomerBigId(customer)} AND start_at={Utc(start)} AND status={BookingService.EarlyRequestStatus}
                              AND booking_type='mixed' AND people={people.Count} AND package_total_snapshot={total}
                              AND created_at > now() - interval '10 minutes'
                            ORDER BY created_at DESC LIMIT 1
                            """).AsEnumerable().Cast<Guid?>().FirstOrDefault();
                        if (repeated != null) return;
                    }
                    var bookingRef = NewReference(appId);
                    pdb.Database.ExecuteSqlInterpolated($"INSERT INTO public.reservations(app_id, app_managed, snapshot_source, reference, customer_id, artist_id, service_id, start_at, location, area_id, booking_type, people, addons_total_snapshot, addons_minutes_snapshot, package_total_snapshot, package_minutes_snapshot, down_payment_percent_snapshot, required_down_payment_snapshot, policy_accepted_at, policy_version, travel_reminder_acknowledged_at, travel_reminder_version, status, location_lat, location_lng, is_early_request, after_hours) VALUES({appId}, true, 'booking_creation', {bookingRef}, {CustomerBigId(customer)}, {artistId}, {ServiceBigId(chosen[0].Id)}, {Utc(start)}, {address.Trim()}, {coverage.Id}, 'mixed', {people.Count}, 0, 0, {total}, {minutes}, {Settings.DownPaymentPercent}, {required}, now(), {Settings.PolicyVersion}, now(), {Settings.TravelReminderVersion}, {(early ? BookingService.EarlyRequestStatus : "Pending Payment")}, {latitude}, {longitude}, {early}, {afterHours})");
                    var reservationId = pdb.Database.SqlQueryRaw<long>("SELECT id AS \"Value\" FROM public.reservations WHERE app_id = {0}", appId).Single();
                    for (var i = 0; i < chosen.Count; i++)
                    {
                        var personId = pdb.Database.SqlQuery<long>($"INSERT INTO public.reservation_people(reservation_id, position, service_id, service_name_snapshot, price_snapshot, minutes_snapshot) VALUES({reservationId}, {i + 1}, {ServiceBigId(chosen[i].Id)}, {chosen[i].Name}, {prices[i]}, {chosen[i].Minutes}) RETURNING id AS \"Value\"").AsEnumerable().Single();
                        foreach (var extra in extras[i])
                            pdb.Database.ExecuteSqlInterpolated($"INSERT INTO public.reservation_people_addons(person_id, addon_id, name_snapshot, price_snapshot, minutes_snapshot) VALUES({personId}, {extra.Id}, {extra.Name}, {extra.Price}, {extra.Minutes})");
                    }
                    pdb.Database.ExecuteSqlInterpolated($"INSERT INTO public.payments(reservation_id, app_id, app_managed, purpose, amount, method, reference, status, verification_status, proof_asset_id, submitted_by) VALUES({reservationId}, {Guid.NewGuid()}, true, 'down_payment', {deposit}, {method}, {reference.Trim()}, 'Pending', 'Pending Verification', {(Guid?)null}, {customer})");
                    if (method != "Xendit") pdb.Database.ExecuteSqlInterpolated($"UPDATE public.reservations SET payment_expires_at = NULL WHERE id = {reservationId}");
                });
                var id = repeated ?? appId;
                created = Bookings.Single(x => x.Id == id);
            }
            catch (InvalidOperationException e) when (e.InnerException is PostgresException pg && pg.ConstraintName == "reservations_reference_key" && attempt < 2)
            {
                continue;
            }
        }
        return created ?? throw new InvalidOperationException("Could not create the booking. Please retry.");
    }

    // Package people for the given bookings (Phase 6), in position order with their add-ons.
    protected override IReadOnlyDictionary<Guid, IReadOnlyList<MixedPersonQuote>> PackagePeople(IReadOnlyCollection<Guid> bookingIds)
    {
        if (bookingIds.Count == 0) return new Dictionary<Guid, IReadOnlyList<MixedPersonQuote>>();
        var ids = bookingIds.ToArray();
        var people = pdb.Database.SqlQueryRaw<PackagePersonRow>("""
            SELECT r.app_id AS "BookingId", p.id AS "PersonId", p.position AS "Position", s.app_id AS "ServiceId", p.service_name_snapshot AS "Name", p.price_snapshot AS "Price", p.minutes_snapshot AS "Minutes", s.price AS "CatalogPrice", s.group_price AS "CatalogGroupPrice"
              FROM public.reservation_people p JOIN public.reservations r ON r.id = p.reservation_id JOIN public.services s ON s.id = p.service_id
             WHERE r.app_id = ANY({0})
            """, ids).ToList();
        var personIds = people.Select(p => p.PersonId).ToArray();
        var extras = (personIds.Length == 0 ? [] : pdb.Database.SqlQueryRaw<PackageAddonRow>("""
            SELECT person_id AS "PersonId", addon_id AS "AddonId", name_snapshot AS "Name", price_snapshot AS "Price", minutes_snapshot AS "Minutes"
              FROM public.reservation_people_addons WHERE person_id = ANY({0})
            """, personIds).ToList()).ToLookup(a => a.PersonId);
        return people.GroupBy(p => p.BookingId).ToDictionary(g => g.Key, g => (IReadOnlyList<MixedPersonQuote>)g.OrderBy(p => p.Position).Select(p =>
        {
            var addOns = extras[p.PersonId].Select(a => new PriceSnapshot(a.AddonId, a.Name, a.Price, a.Minutes)).ToArray();
            // The regular price is not stored per person: a person who paid the service's current group price
            // shows the current regular price beside it. After a later catalog price change it is left out.
            decimal? regular = p.CatalogGroupPrice == p.Price && p.CatalogPrice > p.Price ? p.CatalogPrice : null;
            return new MixedPersonQuote(new PriceSnapshot(p.ServiceId, p.Name, p.Price, p.Minutes), addOns, p.Price + addOns.Sum(a => a.Price), p.Minutes + addOns.Sum(a => a.Minutes)) { RegularPrice = regular };
        }).ToList());
    }
    private sealed class PackagePersonRow
    {
        public Guid BookingId { get; set; } public long PersonId { get; set; } public int Position { get; set; } public Guid ServiceId { get; set; }
        public string Name { get; set; } = ""; public decimal Price { get; set; } public int Minutes { get; set; }
        public decimal CatalogPrice { get; set; } public decimal? CatalogGroupPrice { get; set; }
    }
    private sealed class PackageAddonRow
    {
        public long PersonId { get; set; } public Guid AddonId { get; set; } public string Name { get; set; } = ""; public decimal Price { get; set; } public int Minutes { get; set; }
    }

    public override int ExpireOverduePayments() =>
        Transaction(null, () => pdb.Database.SqlQueryRaw<int>("SELECT public.mvc_expire_overdue() AS \"Value\"").AsEnumerable().Single());

    public override void AttachInvoice(Guid booking, string invoiceId, string checkoutUrl) => Transaction(null, () =>
    {
        var updated = pdb.Database.ExecuteSqlInterpolated($"""
            UPDATE public.payments SET provider='xendit', provider_invoice_id={invoiceId}, checkout_url={checkoutUrl}, provider_status='PENDING'
            WHERE purpose='down_payment' AND method='Xendit' AND provider_invoice_id IS NULL
              AND reservation_id=(SELECT id FROM public.reservations WHERE app_id={booking})
            """);
        if (updated != 1) throw new InvalidOperationException("The Xendit invoice could not be linked to this booking.");
    });

    public override string XenditPaid(string invoiceId, decimal amount, string eventKey) =>
        Transaction(null, () => pdb.Database.SqlQuery<string>($"SELECT public.mvc_xendit_paid({invoiceId}, {amount}, {eventKey}) AS \"Value\"").AsEnumerable().Single());

    // The summary view has no payment deadline, so one booking reads it from its reservation;
    // booking details then show "Complete your payment by …".
    public override Booking? FindBooking(Guid id)
    {
        var booking = base.FindBooking(id);
        if (booking is { Status: "Pending Payment", PaymentDueAt: null } && PaymentExpiresAt(id) is { } due)
            booking.PaymentDueAt = Local(due);
        if (booking is { Status: "Cancelled" or "Completed" })
        {
            var outcome = pdb.Database.SqlQuery<StaffOutcome>($"""
                SELECT staff_cancelled_by AS "CancelledBy", staff_cancelled_at AS "CancelledAt", staff_cancellation_reason AS "Reason",
                       completed_by AS "CompletedBy", completed_at AS "CompletedAt"
                FROM public.reservations WHERE app_id = {id}
                """).AsEnumerable().FirstOrDefault();
            if (outcome != null)
            {
                booking.StaffCancelledBy = outcome.CancelledBy;
                booking.StaffCancelledAt = outcome.CancelledAt is { } c ? Local(c) : null;
                booking.StaffCancellationReason = outcome.Reason;
                booking.CompletedBy = outcome.CompletedBy;
                booking.CompletedAt = outcome.CompletedAt is { } d ? Local(d) : null;
            }
        }
        return booking;
    }

    private sealed record StaffOutcome(Guid? CancelledBy, DateTime? CancelledAt, string? Reason, Guid? CompletedBy, DateTime? CompletedAt);

    internal DateTime? PaymentExpiresAt(Guid booking) =>
        pdb.Database.SqlQuery<DateTime?>($"""
            SELECT payment_expires_at AS "Value" FROM public.reservations WHERE app_id = {booking}
            """).AsEnumerable().FirstOrDefault();

    public override string? PendingCheckoutUrl(Guid booking) =>
        pdb.Database.SqlQuery<string>($"""
            SELECT p.checkout_url AS "Value" FROM public.payments p JOIN public.reservations r ON r.id = p.reservation_id
            WHERE r.app_id = {booking} AND r.status = 'Pending Payment' AND p.provider = 'xendit'
              AND p.provider_status = 'PENDING' AND p.checkout_url IS NOT NULL
            LIMIT 1
            """).AsEnumerable().FirstOrDefault();

    public override string XenditExpired(string invoiceId, string eventKey) =>
        Transaction(null, () => pdb.Database.SqlQuery<string>($"SELECT public.mvc_xendit_expired({invoiceId}, {eventKey}) AS \"Value\"").AsEnumerable().Single());

    public override void CancelBooking(Guid booking, Guid customer, bool confirmed) => Transaction(customer, () =>
    {
        var b = LockBooking(booking);
        if (!confirmed || b.CustomerId != customer) throw new InvalidOperationException("Confirm cancellation of your own appointment.");
        var decision = CancellationPolicy.Evaluate(b.Status, Local(b.StartsAt), Now,
            CancellationPolicy.AcceptedAt(b.ArtistAcceptedAt is { } accepted ? Local(accepted) : null, b.PaidAt is { } paidAt ? Local(paidAt) : null));
        if (!decision.Allowed) throw new InvalidOperationException(decision.Reason);
        var note = decision.Fee > 0 ? $"Customer confirmed cancellation and the PHP {decision.Fee:N0} deduction." : b.Status == BookingService.EarlyRequestStatus ? "Customer withdrew the early booking request."
            : b.Status == "Confirmed" ? "Customer cancelled within the free cancellation window. No fee; the full payment is refunded." : "Customer cancelled before payment. No fee.";
        pdb.Database.ExecuteSqlInterpolated($"UPDATE public.reservations SET status='Cancelled', cancellation_confirmed_at=now(), notes={note} WHERE app_id={booking}");
    });

    public override void RequestReschedule(Guid booking, Guid customer, DateTime start) => Transaction(customer, () =>
    {
        var b = LockBooking(booking);
        if (b.CustomerId != customer) throw new InvalidOperationException("This is not your booking.");
        pdb.Database.ExecuteSqlInterpolated($"UPDATE public.reschedule_requests SET status='Superseded' WHERE reservation_id=(SELECT id FROM public.reservations WHERE app_id={booking}) AND status='Pending'");
        var utc = Utc(start);
        var reservationId = ReservationBigId(booking);
        pdb.Database.ExecuteSqlInterpolated($"INSERT INTO public.reschedule_requests(reservation_id, requested_by, requested_start) VALUES({reservationId}, {customer}, {utc})");
        Notify(booking, $"Reschedule requested for {start:MMM d, yyyy h:mm tt}. Original slot stays reserved until approval.");
    });

    public override void CorrectPayment(Guid booking, Guid customer, string reference, string? proof) => Transaction(customer, () =>
    {
        var b = LockBooking(booking);
        var reservationId = ReservationBigId(booking);
        var p = pdb.Database.SqlQueryRaw<long>("SELECT id AS \"Value\" FROM public.payments WHERE reservation_id = {0} AND purpose = 'down_payment'", reservationId).SingleOrDefault();
        if (p == 0) throw new InvalidOperationException("Deposit not found.");
        var row = pdb.Set<PaymentRow>().AsNoTracking().Single(x => x.BookingId == booking && x.Purpose == "down_payment");
        if (b.CustomerId != customer || b.Status != "Pending Payment" || row.Status == "Verified") throw new InvalidOperationException("Only unverified pending deposits can be corrected.");
        var methodName = pdb.Database.SqlQueryRaw<string>("SELECT method AS \"Value\" FROM public.payments WHERE id = {0}", p).Single();
        BookingService.ValidatePayment(methodName, reference);
        var asset = proof == null ? row.ProofAssetId : ProofAsset(proof, customer);
        pdb.Database.ExecuteSqlInterpolated($"UPDATE public.payments SET reference={reference.Trim()}, proof_asset_id={asset}, status='Pending', verification_status='Pending Verification' WHERE id={p}");
        Notify(booking, "Customer updated payment proof for review.");
    });

    public override void ManageBooking(Guid booking, string action, Guid admin, string reason, decimal amount, string reference, Guid? paymentId = null, long? reviewRevision = null, string? reviewVersion = null, bool travelOverride = false) => Transaction(admin, () =>
    {
        Admin(admin);
        if (travelOverride && action != "reschedule") throw new InvalidOperationException("A travel override applies only to a reschedule approval.");
        if (travelOverride && pdb.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM public.admin_users WHERE app_id={admin} AND role='SuperAdmin' AND is_active").Single() == 0)
            throw new InvalidOperationException(BookingService.TravelOverrideRefused);
        if (action is "reschedule" or "approve-request") LockTravel(ResolveArtist().Id); // before the booking row, same order as SubmitBooking
        var b = LockBooking(booking);
        if (action is "verify" or "approve") CheckPaymentReview(booking, paymentId, reviewRevision, reviewVersion, b.Status == "Confirmed");
        var reservationId = ReservationBigId(booking);
        switch (action)
        {
            case "verify":
            case "approve":
                if (b.Status == "Confirmed" && b.VerifiedPaid >= b.RequiredDownPayment) return;
                if (b.Status != "Pending Payment" || (action == "approve" && (b.VerifiedPaid < b.RequiredDownPayment || b.VerifiedPaid > b.FinalTotal)))
                    throw new InvalidOperationException("Verify the required deposit before approval.");
                pdb.Database.ExecuteSqlInterpolated($"UPDATE public.payments SET status='Paid', verification_status='Verified', verified_by={admin}, verified_at=now() WHERE reservation_id={reservationId} AND purpose='down_payment' AND status<>'Paid'");
                pdb.Database.ExecuteSqlInterpolated($"UPDATE public.reservations SET status='Confirmed', reviewed_by={admin}, reviewed_at=now(), notes='{b.DownPaymentPercentSnapshot:N0}% down payment verified. Please have clean, moisturized skin and a well-lit space ready.' WHERE app_id={booking}");
                break;
            case "proof":
                if (b.Status != "Pending Payment" || string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Provide payment correction instructions for a pending booking.");
                pdb.Database.ExecuteSqlInterpolated($"UPDATE public.payments SET verification_status='Correction Requested', review_note={reason} WHERE reservation_id={reservationId} AND purpose='down_payment'");
                pdb.Database.ExecuteSqlInterpolated($"UPDATE public.reservations SET notes={reason} WHERE app_id={booking}");
                Notify(booking, reason);
                break;
            case "reject":
                if (b.Status != "Pending Payment" || string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Provide a reason to reject a pending booking.");
                pdb.Database.ExecuteSqlInterpolated($"UPDATE public.reservations SET status='Rejected', notes={reason}, reviewed_by={admin}, reviewed_at=now() WHERE app_id={booking}");
                break;
            case "reschedule":
            case "decline":
                if (action == "decline" && string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Provide a decline reason.");
                var overridden = action == "reschedule" && RescheduleNeedsOverride(booking, b, reservationId, travelOverride);
                var status = action == "reschedule" ? "Approved" : "Declined";
                if (pdb.Database.ExecuteSqlInterpolated($"UPDATE public.reschedule_requests SET status={status}, reason={reason}, reviewed_by={admin}, reviewed_at=now() WHERE reservation_id={reservationId} AND status='Pending'") == 0)
                    throw new InvalidOperationException("No reschedule request to review.");
                if (action == "decline") pdb.Database.ExecuteSqlInterpolated($"UPDATE public.reservations SET notes={reason} WHERE app_id={booking}");
                if (overridden) pdb.Database.ExecuteSqlInterpolated($"UPDATE public.reservations SET travel_override_by={admin}, travel_override_at=now() WHERE app_id={booking}");
                else if (action == "reschedule") pdb.Database.ExecuteSqlInterpolated($"UPDATE public.reservations SET travel_override_by=NULL, travel_override_at=NULL WHERE app_id={booking} AND travel_override_by IS NOT NULL");
                break;
            case "cancel":
                // Business cancellation: no fee, allowed until the appointment starts. The database
                // checks the SuperAdmin role and records a closure with the whole verified payment due back.
                if (b.Status is not ("Pending Payment" or "Confirmed")) throw new InvalidOperationException("Only pending or confirmed appointments can be cancelled by the business.");
                if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Enter a reason for cancelling this appointment.");
                if (Local(b.StartsAt) <= Now) throw new InvalidOperationException("This appointment has already started and can no longer be cancelled.");
                var cancelNote = "Glam by Mariya has cancelled this appointment: " + reason.Trim() + ". Mariya will contact you about returning your down payment.";
                pdb.Database.ExecuteSqlInterpolated($"UPDATE public.reservations SET status='Cancelled', cancellation_confirmed_at=now(), staff_cancelled_by={admin}, staff_cancelled_at=now(), staff_cancellation_reason={reason.Trim()}, notes={cancelNote} WHERE app_id={booking}");
                break;
            case "complete":
                // The remaining balance is paid in cash to the artist; record it as received, then complete.
                if (b.Status != "Confirmed") throw new InvalidOperationException("Only confirmed appointments can be marked completed.");
                if (Local(b.EndsAt) > Now) throw new InvalidOperationException("Only appointments whose makeup services have ended can be marked completed.");
                var balance = b.FinalTotal - b.VerifiedPaid;
                if (balance > 0)
                    pdb.Database.ExecuteSqlInterpolated($"INSERT INTO public.payments(reservation_id, app_id, app_managed, purpose, amount, method, reference, status, verification_status, verified_by, verified_at, paid_at) VALUES({reservationId}, {Guid.NewGuid()}, true, 'balance', {balance}, 'Cash', {"CASH-" + b.Reference}, 'Paid', 'Verified', {admin}, now(), now())");
                pdb.Database.ExecuteSqlInterpolated($"UPDATE public.reservations SET status='Completed', completed_by={admin}, completed_at=now(), reviewed_by={admin}, reviewed_at=now(), notes='Your appointment was marked completed by Glam by Mariya. Thank you!' WHERE app_id={booking}");
                break;
            case "noshow":
            case "incomplete":
                var outcome = action == "noshow" ? "No-show" : "Incomplete";
                if (b.Status != "Confirmed" || string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Provide a reason for a past confirmed appointment.");
                pdb.Database.ExecuteSqlInterpolated($"UPDATE public.reservations SET status={outcome}, notes={reason}, reviewed_by={admin}, reviewed_at=now() WHERE app_id={booking}");
                break;
            case "refund":
                if (string.IsNullOrWhiteSpace(reference) || reference.Length > 150 || amount <= 0) throw new InvalidOperationException("Enter the actual refund amount and transfer reference.");
                var closure = pdb.Database.SqlQueryRaw<Guid>("SELECT id AS \"Value\" FROM public.reservation_closures WHERE reservation_id = {0}", reservationId).SingleOrDefault();
                if (closure == Guid.Empty) throw new InvalidOperationException("No refund obligation exists.");
                var bank = pdb.Set<MethodRow>().Single(x => x.Name == "Bank Transfer").Id;
                pdb.Database.ExecuteSqlInterpolated($"INSERT INTO public.refunds(closure_id, method_id, amount, transfer_reference, sent_at, recorded_by) VALUES({closure}, {bank}, {amount}, {reference.Trim()}, now(), {admin})");
                Notify(booking, $"Manual refund recorded: ₱{amount:N2}.");
                break;
            case "approve-request":
                // Accepting an early time: the guard gives the customer 15 minutes to pay (the status trigger
                // notifies the customer with the notes) and
                // refuses the change if the span now overlaps another booking.
                if (b.Status != BookingService.EarlyRequestStatus) throw new InvalidOperationException(BookingService.EarlyRequestDecidedMessage);
                if (!BookingService.TravelFits(this, Local(b.StartsAt), (int)(b.EndsAt - b.StartsAt).TotalMinutes, b.BufferMinutesSnapshot, b.LocationLat, b.LocationLng, booking))
                    throw new InvalidOperationException(TravelPlanner.RefusalMessage);
                pdb.Database.ExecuteSqlInterpolated($"UPDATE public.reservations SET status='Pending Payment', notes={BookingService.EarlyRequestApprovedMessage}, reviewed_by={admin}, reviewed_at=now() WHERE app_id={booking}");
                break;
            case "accept":
                // The artist accepts a paid booking; the guard allows it once, while Confirmed, for active staff.
                if (!(b.Status == "Confirmed" && b.ArtistAcceptedAt == null && b.PaidAt is { } paid && Now < Local(paid).Add(CancellationPolicy.AutoAcceptAfterPayment)))
                    throw new InvalidOperationException(BookingService.AcceptNotWaitingMessage);
                pdb.Database.ExecuteSqlInterpolated($"UPDATE public.reservations SET artist_accepted_at=now(), notes={BookingService.ArtistAcceptedMessage}, reviewed_by={admin}, reviewed_at=now() WHERE app_id={booking}");
                Notify(booking, BookingService.ArtistAcceptedMessage);
                break;
            case "decline-request":
                if (b.Status != BookingService.EarlyRequestStatus) throw new InvalidOperationException(BookingService.EarlyRequestDecidedMessage);
                if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Enter a reason for declining this early booking request.");
                var declined = string.Format(BookingService.EarlyRequestDeclinedMessage, reason.Trim());
                pdb.Database.ExecuteSqlInterpolated($"UPDATE public.reservations SET status='Rejected', notes={declined}, reviewed_by={admin}, reviewed_at=now() WHERE app_id={booking}");
                break;
            default: throw new InvalidOperationException("Unknown booking action.");
        }
    });

    // The travel rule for the pending reschedule's new time, without the moved booking.
    // True when it fails and the SuperAdmin chose to override; throws when it fails without one.
    private bool RescheduleNeedsOverride(Guid booking, BookingRow b, long reservationId, bool travelOverride)
    {
        var requested = pdb.Database.SqlQueryRaw<DateTime>(
            "SELECT requested_start AS \"Value\" FROM public.reschedule_requests WHERE reservation_id={0} AND status='Pending'", reservationId).AsEnumerable().FirstOrDefault();
        if (requested == default) return false; // no pending request: the update below reports it
        var duration = (int)(b.EndsAt - b.StartsAt).TotalMinutes;
        if (BookingService.TravelFits(this, Local(requested), duration, b.BufferMinutesSnapshot, b.LocationLat, b.LocationLng, booking)) return false;
        if (!travelOverride) throw new InvalidOperationException(TravelPlanner.RefusalMessage);
        return true;
    }

    protected override void Notify(Guid booking, string message)
    {
        var reservationId = ReservationBigId(booking);
        var reference = pdb.Database.SqlQueryRaw<string>("SELECT reference AS \"Value\" FROM public.reservations WHERE id = {0}", reservationId).Single();
        pdb.Database.ExecuteSqlInterpolated($"INSERT INTO public.notifications(user_id, reservation_id, message) SELECT a.id, {reservationId}, {reference + ": " + message} FROM public.account_identities a LEFT JOIN public.customers c ON c.id=a.customer_id LEFT JOIN public.admin_users u ON u.id=a.admin_id WHERE a.customer_id=(SELECT customer_id FROM public.reservations WHERE id={reservationId}) OR u.is_active=true");
    }

    protected override void AddAsset(Guid id, string key, string purpose, byte[] bytes, Guid uploader)
    {
        var mime = ImageType(bytes);
        files.Save(key, bytes);
        try
        {
            pdb.Database.ExecuteSqlInterpolated($"INSERT INTO public.media_assets(id, object_key, purpose, content_type, byte_length, uploaded_by) VALUES({id}, {key}, {purpose}, {mime}, {bytes.Length}, {uploader})");
        }
        catch { files.Delete(key); throw; }
    }

    public override void SaveCatalogImage(string kind, Guid id, string slot, byte[] bytes) => SaveImage(bytes, "CatalogImages", asset =>
    {
        if (kind == "service")
        {
            var serviceId = ServiceBigId(id);
            pdb.Database.ExecuteSqlInterpolated($"INSERT INTO public.catalog_images(service_id, slot, asset_id) VALUES({serviceId}, {slot}, {asset}) ON CONFLICT(service_id, slot) DO UPDATE SET asset_id=excluded.asset_id");
        }
        else pdb.Database.ExecuteSqlInterpolated($"INSERT INTO public.catalog_images(addon_id, slot, asset_id) VALUES({id}, {slot}, {asset}) ON CONFLICT(addon_id, slot) DO UPDATE SET asset_id=excluded.asset_id");
    });

    public override void RemoveCatalogImage(string kind, Guid id, string slot) => Transaction(ActorId, () =>
    {
        Admin(ActorId ?? Guid.Empty);
        var key = CatalogImageKey(kind, id, slot);
        if (kind == "service")
        {
            var serviceId = ServiceBigId(id);
            pdb.Database.ExecuteSqlInterpolated($"DELETE FROM public.catalog_images WHERE service_id={serviceId} AND slot={slot}");
        }
        else pdb.Database.ExecuteSqlInterpolated($"DELETE FROM public.catalog_images WHERE addon_id={id} AND slot={slot}");
        files.Delete(key);
    });
}
