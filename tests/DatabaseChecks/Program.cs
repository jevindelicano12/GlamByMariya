// Phase 2 database checks. Run only through tools/test-db.sh, which starts a throwaway
// postgres:17 container on 127.0.0.1 with a random marker:
//   tools/test-db.sh tests/DatabaseChecks [--migrator] [--schema] [--access]
using Npgsql;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;

if (args.Length < 3 || !int.TryParse(args[0], out var port) || port is < 1024 or > 65535 ||
    !Guid.TryParseExact(args[1], "N", out var marker) || marker == Guid.Empty)
    throw new Exception("Run via tools/test-db.sh; a fresh marked throwaway PostgreSQL is required.");
var sections = args.Skip(3).ToHashSet();
bool Run(string section) => sections.Count == 0 || sections.Contains(section);

var builder = new NpgsqlDataSourceBuilder($"Host=127.0.0.1;Port={port};Username=postgres;Database=postgres;SSL Mode=Disable;GSS Encryption Mode=Disable;Pooling=false;Timeout=15;Command Timeout=120");
async Task Guard(NpgsqlConnection physical)
{
    // The random marker proves this is the container tools/test-db.sh just started.
    await using var command = new NpgsqlCommand("""
        SELECT current_database()='postgres' AND current_user IN ('postgres','glam_app')
          AND current_setting('glam.docker_test',true)='on'
          AND current_setting('glam.payment_review_marker',true)=@marker
        """, physical);
    command.Parameters.AddWithValue("marker", marker.ToString("N"));
    if ((bool?)await command.ExecuteScalarAsync() != true) throw new System.Security.SecurityException("Unsafe PostgreSQL physical connection.");
}
builder.UsePhysicalConnectionInitializer(c => Guard(c).GetAwaiter().GetResult(), Guard);
await using var source = builder.Build();
// The app's own login (member of makeup_backend, no RLS bypass), as on Supabase.
var appBuilder = new NpgsqlDataSourceBuilder($"Host=127.0.0.1;Port={port};Username=glam_app;Database=postgres;SSL Mode=Disable;GSS Encryption Mode=Disable;Pooling=false;Timeout=15;Command Timeout=120");
appBuilder.UsePhysicalConnectionInitializer(c => Guard(c).GetAwaiter().GetResult(), Guard);
await using var appSource = appBuilder.Build();

var root = Directory.GetCurrentDirectory();
var migrations = Path.Combine(root, "database", "migrations");
int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); checks++; Console.WriteLine("PASS: " + name); }
async Task<object?> Scalar(string sql)
{
    await using var c = await source.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand(sql, c);
    return await cmd.ExecuteScalarAsync();
}
async Task Exec(string sql)
{
    await using var c = await source.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand(sql, c);
    await cmd.ExecuteNonQueryAsync();
}
async Task ResetPublic() => await Exec("DROP SCHEMA public CASCADE; CREATE SCHEMA public; GRANT ALL ON SCHEMA public TO public;");
async Task<string> Refused(Func<Task> action)
{
    try { await action(); } catch (InvalidOperationException e) { return e.Message; }
    return "";
}

var tempRoot = Path.GetFullPath(args[2]);
var http = new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };
var env = new TestEnv { ContentRootPath = tempRoot };
static void AppRetry(Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder npgsql) => npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null);
PublicStudioStore NewStore(Dictionary<string, string?>? settings = null, NpgsqlDataSource? as_ = null)
{
    var config = new ConfigurationBuilder().AddInMemoryCollection(settings ?? new()).Build();
    var options = new DbContextOptionsBuilder<PublicMakeupDbContext>();
    // Same retry setting as the app (Program.cs), so transactions are tested the way they run.
    options.UseNpgsql(as_ ?? source, AppRetry);
    return new PublicStudioStore(new PublicMakeupDbContext(options.Options), http, env, config, new GlamByMariya.Infrastructure.Integrations.LocalAppFileStore(env));
}

if (Run("--migrator"))
{
    var temp = Path.Combine(Path.GetTempPath(), "glam-migrator-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    try
    {
        await ResetPublic();
        File.WriteAllText(Path.Combine(temp, "0001_a.sql"), "CREATE TABLE public.first_table(id int);");
        File.WriteAllText(Path.Combine(temp, "0002_b.sql"), "CREATE TABLE public.second_table(id int);");
        var applied = await SqlMigrator.ApplyAsync(source, temp);
        var versions = (long)(await Scalar("SELECT count(*) FROM public.schema_migrations WHERE version IN (1,2)"))!;
        Check(applied.SequenceEqual(["0001_a.sql", "0002_b.sql"]) && versions == 2, "applies 0001 and 0002 in order and records both");
        Check((await SqlMigrator.ApplyAsync(source, temp)).Count == 0, "second run applies nothing");
        Check(SqlMigrator.Checksum("a\r\nb") == SqlMigrator.Checksum("a\nb"), "CRLF and LF give the same checksum");

        File.WriteAllText(Path.Combine(temp, "0001_a.sql"), "CREATE TABLE public.first_table(id bigint);");
        Check((await Refused(() => SqlMigrator.ApplyAsync(source, temp))).Contains("0001_a.sql"), "an edited applied file is refused by name");
        File.WriteAllText(Path.Combine(temp, "0001_a.sql"), "CREATE TABLE public.first_table(id int);");

        File.WriteAllText(Path.Combine(temp, "0004_d.sql"), "SELECT 1;");
        Check((await Refused(() => SqlMigrator.ApplyAsync(source, temp))) != "", "a gap is refused");
        File.Delete(Path.Combine(temp, "0004_d.sql"));

        File.WriteAllText(Path.Combine(temp, "0003_c.sql"), "CREATE TABLE public.third_table(id int); SELECT broken syntax here;");
        var failed = false;
        try { await SqlMigrator.ApplyAsync(source, temp); } catch (PostgresException) { failed = true; }
        Check(failed && await Scalar("SELECT to_regclass('public.third_table')::text") is DBNull
              && (long)(await Scalar("SELECT count(*) FROM public.schema_migrations WHERE version=3"))! == 0,
            "a failing file leaves nothing behind");

        await ResetPublic();
        await SqlMigrator.ApplyAsync(source, migrations);
        Check(await Scalar("SELECT to_regclass('public.reservations')::text") is not DBNull, "real folder builds the base tables");
    }
    finally { Directory.Delete(temp, true); }
    Console.WriteLine($"All {checks} migrator checks passed.");
}

if (Run("--schema"))
{
    var before = checks;
    await ResetPublic();
    await SqlMigrator.ApplyAsync(source, migrations);
    // Every relation and function the app reads in database mode (mixed bookings and
    // website content excluded: they stay switched off there).
    string[] relations = ["account_identities", "account_preferences", "admin_invitations", "admin_users", "artist_availability", "audit_logs",
        "blocked_slots", "business_settings", "catalog_categories", "catalog_images", "customers", "data_protection_keys", "email_codes",
        "email_code_cooldowns", "makeup_addons", "makeup_artists", "media_assets", "notifications", "payment_adjustments", "payment_methods",
        "payment_revisions", "payments", "refunds", "reschedule_requests", "reservation_addons", "reservation_closures", "reservation_history",
        "reservations", "service_areas", "services", "mvc_blocks", "mvc_booking_addons", "mvc_booking_summary", "mvc_catalog_images",
        "mvc_closures", "mvc_payments", "mvc_reschedules", "mvc_roles", "mvc_services", "mvc_users", "payment_webhook_events"];
    var missing = new List<string>();
    foreach (var name in relations)
        if (await Scalar($"SELECT to_regclass('public.{name}')::text") is DBNull) missing.Add(name);
    foreach (var name in new[] { "mvc_assert_role", "mvc_check_slot" })
        if ((long)(await Scalar($"SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='public' AND p.proname='{name}'"))! == 0) missing.Add(name + "()");
    Check(missing.Count == 0, "every object the app reads exists" + (missing.Count > 0 ? ": missing " + string.Join(", ", missing) : ""));
    var people = (long)(await Scalar("SELECT (SELECT count(*) FROM public.customers)+(SELECT count(*) FROM public.admin_users)+(SELECT count(*) FROM public.makeup_artists)+(SELECT count(*) FROM public.reservations)"))!;
    Check(people == 0, "seed has no people and no bookings");
    var catalog = (string)(await Scalar("""
        SELECT (SELECT count(*) FROM public.services WHERE is_active AND NOT is_archived)||','||(SELECT count(*) FROM public.makeup_addons)||','||
               (SELECT count(*) FROM public.service_areas WHERE is_active)||','||(SELECT count(*) FROM public.payment_methods)||','||
               (SELECT count(*) FROM public.business_settings WHERE id=1)
        """))!;
    Check(catalog == "3,3,6,5,1", $"seed has the catalog (services,add-ons,areas,methods,settings = {catalog})");
    var store = NewStore();
    Check(store.Services.Count(x => x.Active && !x.Archived) == 3 && store.AddOns.Count == 3 && store.Settings.OpenHour == 8 && store.Settings.BufferMinutes == 45,
        "the app reads the catalog and settings through its views");
    Console.WriteLine($"All {checks - before} schema checks passed.");
}

if (Run("--rules"))
{
    var before = checks;
    await ResetPublic();
    await SqlMigrator.ApplyAsync(source, migrations);
    var store = NewStore();
    var owner = store.CreateInitialSuperAdmin("Store", "Owner", "owner@example.test", "OwnerPass123");
    var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<GlamByMariya.Core.Entities.AppUser>();
    var ana = store.AddCustomerWithPasswordHash("Ana", "Cruz", "ana@example.test", hasher.HashPassword(new(), "AnaPass123"), 30, "Female", "24 Synthetic Street, Quezon City");
    var bea = store.AddCustomerWithPasswordHash("Bea", "Reyes", "bea@example.test", hasher.HashPassword(new(), "BeaPass123"), 30, "Female", "25 Synthetic Street, Quezon City");
    var soft = store.Services.Single(x => x.Name == "Soft Glam");
    var day = DateTime.Today.AddDays(3);
    const string overlap = "This slot overlaps a reserved appointment. Please choose another time.";
    GlamByMariya.Core.Entities.Booking Book(PublicStudioStore s, Guid customer, DateTime start, string reference)
    {
        var quote = GlamByMariya.Core.BusinessRules.BookingPricing.Calculate(soft, [], 1, s.Settings.TravelFee, "auto", s.Settings.DownPaymentPercent);
        return s.SubmitBooking(customer, soft.Id, [], start, "24 Synthetic Street, Quezon City", "Quezon City", quote.RequiredDownPayment, "GCash", reference, null,
            true, true, 1, "auto", quote.FinalTotal, quote.RequiredDownPayment, latitude: 14.5547m, longitude: 121.0244m);
    }
    async Task<string> Status(Guid booking) => (string)(await Scalar($"SELECT status FROM public.reservations WHERE app_id='{booking}'"))!;
    async Task Confirm(Guid booking) => await Exec($"""
        BEGIN; SELECT set_config('makeup.actor_id','{owner.Id}',true);
        UPDATE public.payments SET status='Paid', verification_status='Verified', verified_by='{owner.Id}', verified_at=now()
         WHERE reservation_id=(SELECT id FROM public.reservations WHERE app_id='{booking}');
        UPDATE public.reservations SET status='Confirmed' WHERE app_id='{booking}'; COMMIT;
        """);
    // Test setup only: move a booking in time without the reschedule workflow.
    async Task MoveStart(Guid booking, string fromNow) => await Exec($"""
        SET session_replication_role = replica;
        UPDATE public.reservations SET start_at=now()+interval '{fromNow}', end_at=now()+interval '{fromNow}'+(end_at-start_at),
          reserved_until=now()+interval '{fromNow}'+(reserved_until-start_at) WHERE app_id='{booking}';
        SET session_replication_role = origin;
        """);
    string Refusal(Action action) { try { action(); } catch (InvalidOperationException e) { return e.Message; } return ""; }

    var first = Book(store, ana.Id, day.AddHours(10), "RULES-001");
    Check(await Status(first.Id) == "Pending Payment", "a new booking waits for payment");
    Check(Refusal(() => Book(NewStore(), bea.Id, day.AddHours(10), "RULES-002")) == overlap, "pending payment holds the slot");
    Check(await Scalar($"SELECT payment_expires_at FROM public.reservations WHERE app_id='{first.Id}'") is DBNull, "a submitted GCash claim has no payment deadline (it waits for the owner's review)");

    await Exec($"UPDATE public.reservations SET payment_expires_at=now()-interval '1 minute' WHERE app_id='{first.Id}'");
    var second = Book(NewStore(), bea.Id, day.AddHours(10), "RULES-003");
    Check(await Status(first.Id) == "Expired" && await Status(second.Id) == "Pending Payment", "an overdue unpaid booking frees its slot at once");

    var racers = new[] { ana.Id, bea.Id };
    using var barrier = new Barrier(2);
    var results = await Task.WhenAll(racers.Select((customer, i) => Task.Run(() =>
    {
        var s = NewStore(); barrier.SignalAndWait();
        try { Book(s, customer, day.AddHours(14), $"RACE-00{i}"); return ""; } catch (InvalidOperationException e) { return e.Message; }
    })));
    var raced = (long)(await Scalar($"SELECT count(*) FROM public.reservations WHERE start_at=(SELECT start_at FROM public.reservations WHERE reference LIKE 'LM-%' ORDER BY id DESC LIMIT 1)"))!;
    Check(results.Count(r => r == "") == 1 && results.Count(r => r == overlap) == 1 && raced == 1, "two customers booking at the same moment: one wins");

    NewStore().CancelBooking(second.Id, bea.Id, true);
    var pendingFee = await Scalar($"SELECT cancellation_fee FROM public.reservation_closures WHERE reservation_id=(SELECT id FROM public.reservations WHERE app_id='{second.Id}')");
    Check(await Status(second.Id) == "Cancelled" && (pendingFee is DBNull || (decimal)pendingFee == 0), "customer cancels unpaid booking free");

    var early = Book(NewStore(), ana.Id, day.AddHours(8), "RULES-004");
    await Confirm(early.Id);
    await MoveStart(early.Id, "24 hours 5 minutes");
    NewStore().CancelBooking(early.Id, ana.Id, true);
    var fee = (decimal)(await Scalar($"SELECT cancellation_fee FROM public.reservation_closures WHERE reservation_id=(SELECT id FROM public.reservations WHERE app_id='{early.Id}')"))!;
    var late = Book(NewStore(), bea.Id, day.AddHours(16).AddMinutes(15), "RULES-005"); // 16:15: 30 minutes of travel after the 14:00 booking
    await Confirm(late.Id);
    await MoveStart(late.Id, "23 hours 59 minutes");
    var dbRefused = "";
    try { await Exec($"UPDATE public.reservations SET status='Cancelled', cancellation_confirmed_at=now() WHERE app_id='{late.Id}'"); }
    catch (PostgresException e) { dbRefused = e.MessageText; }
    Check(fee == 200 && dbRefused.Contains("24 hours"), "confirmed booking 24h away cancels with 200, 23h59 is refused");

    var overdue = Book(NewStore(), ana.Id, day.AddDays(1).AddHours(10), "RULES-006");
    var fresh = Book(NewStore(), bea.Id, day.AddDays(1).AddHours(14), "RULES-007");
    await Exec($"UPDATE public.reservations SET payment_expires_at=now()-interval '1 minute' WHERE app_id='{overdue.Id}'");
    Check(NewStore().ExpireOverduePayments() == 1 && await Status(overdue.Id) == "Expired" && await Status(fresh.Id) == "Pending Payment", "expiry job marks only overdue bookings");

    var invites = new GlamByMariya.Infrastructure.Services.PostgresInvitationService(new PublicMakeupDbContext(new DbContextOptionsBuilder<PublicMakeupDbContext>().UseNpgsql(source, AppRetry).Options), store, TimeProvider.System);
    Check(invites.InviteWithToken("new.admin@example.test", owner.Id).Invite.Email == "new.admin@example.test", "invitation by the owner is stored");
    Console.WriteLine($"All {checks - before} booking rule checks passed.");
}

if (Run("--owner"))
{
    var before = checks;
    await ResetPublic();
    await SqlMigrator.ApplyAsync(source, migrations);
    var store = NewStore();
    store.CreateInitialSuperAdmin("Maria", "Santos", "maria@example.test", "OwnerPass123");
    var artists = (string)(await Scalar("SELECT count(*)||':'||coalesce(string_agg(full_name, ','),'') FROM public.makeup_artists WHERE is_active"))!;
    Check(artists == "1:Maria Santos", $"owner setup creates the one artist with the owner's name ({artists})");
    var calendarStore = NewStore();
    var calendar = new GlamByMariya.Infrastructure.Services.BookingService(calendarStore, calendarStore, calendarStore, calendarStore, calendarStore).GetAvailability(DateTime.Today.AddMonths(1), 60, latitude: 14.5547m, longitude: 121.0244m);
    Check(calendar.Days.Any(d => d.Status == "available"), "booking calendar works without Database:ArtistId");
    var again = "";
    try { NewStore().CreateInitialSuperAdmin("Other", "Owner", "other.owner@example.test", "OwnerPass123"); } catch (InvalidOperationException e) { again = e.Message; }
    var after = (long)(await Scalar("SELECT count(*) FROM public.makeup_artists"))!;
    Check(again != "" && after == 1, "second owner setup is refused and adds no artist");
    Console.WriteLine($"All {checks - before} owner checks passed.");
}

if (Run("--access"))
{
    var before = checks;
    await ResetPublic();
    await SqlMigrator.ApplyAsync(source, migrations);
    var open = (await Scalar("""
        SELECT string_agg(c.relname, ', ') FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
        WHERE n.nspname='public' AND c.relkind IN ('r','v','m','p')
          AND (has_table_privilege('anon', c.oid, 'SELECT,INSERT,UPDATE,DELETE') OR has_table_privilege('authenticated', c.oid, 'SELECT,INSERT,UPDATE,DELETE'))
        """)) as string ?? "";
    Check(open == "", "anon and authenticated cannot read or write any public table" + (open == "" ? "" : ": " + open));
    var functions = (await Scalar("""
        SELECT string_agg(p.proname, ', ') FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
        WHERE n.nspname='public' AND (has_function_privilege('anon', p.oid, 'EXECUTE') OR has_function_privilege('authenticated', p.oid, 'EXECUTE'))
        """)) as string ?? "";
    Check(functions == "", "anon and authenticated cannot run public functions" + (functions == "" ? "" : ": " + functions));
    var recorded = (long)(await Scalar("SELECT count(*) FROM public.schema_migrations"))!;
    var cron = (long)(await Scalar("SELECT count(*) FROM pg_extension WHERE extname='pg_cron'"))!;
    Check(recorded == Directory.GetFiles(migrations, "*.sql").Length && cron == 0, "all migrations apply without pg_cron (local) and are recorded");
    Console.WriteLine($"All {checks - before} access checks passed.");
}

if (Run("--app-role"))
{
    // Everything the running app does, done as glam_app instead of the superuser.
    var before = checks;
    await ResetPublic();
    await SqlMigrator.ApplyAsync(source, migrations);
    PublicStudioStore App() => NewStore(as_: appSource);
    var owner = App().CreateInitialSuperAdmin("Maria", "Santos", "maria@example.test", "OwnerPass123");
    Check(owner.Role == "SuperAdmin", "the owner setup runs with the app login");
    async Task<string> AsApp(string sql)
    {
        try { await using var c = await appSource.OpenConnectionAsync(); await using var cmd = new NpgsqlCommand(sql, c); await cmd.ExecuteNonQueryAsync(); return ""; }
        catch (PostgresException e) { return e.MessageText; }
    }
    var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<GlamByMariya.Core.Entities.AppUser>();
    var customer = App().AddCustomerWithPasswordHash("Ana", "Cruz", "ana@example.test", hasher.HashPassword(new(), "AnaPass123"), 30, "Female", "24 Synthetic Street, Quezon City");
    var app = App();
    var soft = app.Services.Single(x => x.Name == "Soft Glam");
    var quote = GlamByMariya.Core.BusinessRules.BookingPricing.Calculate(soft, [], 1, app.Settings.TravelFee, "auto", app.Settings.DownPaymentPercent);
    var booking = app.SubmitBooking(customer.Id, soft.Id, [], DateTime.Today.AddDays(3).AddHours(10), "24 Synthetic Street, Quezon City", "Quezon City",
        quote.RequiredDownPayment, "GCash", "APP-ROLE-1", null, true, true, 1, "auto", quote.FinalTotal, quote.RequiredDownPayment, latitude: 14.5547m, longitude: 121.0244m);
    Check(App().FindBooking(booking.Id) != null, "the app login can register a customer and book");
    Check(App().ExpireOverduePayments() == 0, "the app login can run the expiry function");
    App().CancelBooking(booking.Id, customer.Id, true);
    Check((string)(await Scalar($"SELECT status FROM public.reservations WHERE app_id='{booking.Id}'"))! == "Cancelled", "the app login can cancel a booking");
    var invites = new GlamByMariya.Infrastructure.Services.PostgresInvitationService(new PublicMakeupDbContext(new DbContextOptionsBuilder<PublicMakeupDbContext>().UseNpgsql(appSource, AppRetry).Options), App(), TimeProvider.System);
    var inviteError = "";
    try { invites.InviteWithToken("new.admin@example.test", owner.Id); } catch (Exception e) { inviteError = e.GetBaseException().Message; }
    Check(inviteError == "", "the app login can store an admin invitation" + (inviteError == "" ? "" : ": " + inviteError));
    var prefs = await AsApp($"INSERT INTO public.account_preferences(user_id, verified_email) VALUES('{customer.Id}', 'ana@example.test'); SELECT * FROM public.account_preferences;");
    Check(prefs == "", "the app login can save account preferences" + (prefs == "" ? "" : ": " + prefs));
    var keys = await AsApp("INSERT INTO public.data_protection_keys(\"FriendlyName\", \"Xml\") VALUES('key-1', '<key/>'); SELECT * FROM public.data_protection_keys;");
    Check(keys == "", "the app login can store Data Protection keys" + (keys == "" ? "" : ": " + keys));
    var codes = await AsApp("SELECT count(*) FROM public.email_codes; SELECT count(*) FROM public.email_code_cooldowns;");
    Check(codes == "", "the app login can read email codes" + (codes == "" ? "" : ": " + codes));
    await Exec("CREATE FUNCTION public.zz_added_later() RETURNS int LANGUAGE sql AS 'SELECT 1'");
    var later = (bool)(await Scalar("SELECT has_function_privilege('anon', 'public.zz_added_later()', 'EXECUTE') OR has_function_privilege('authenticated', 'public.zz_added_later()', 'EXECUTE')"))!;
    Check(!later, "a function added by a later migration is not callable by anon or authenticated");
    Console.WriteLine($"All {checks - before} app role checks passed.");
}

if (Run("--xendit"))
{
    // Xendit deposits, run as the app login: invoice attached, webhook paid/duplicate/late/expired.
    var before = checks;
    await ResetPublic();
    await SqlMigrator.ApplyAsync(source, migrations);
    PublicStudioStore App() => NewStore(as_: appSource);
    App().CreateInitialSuperAdmin("Maria", "Santos", "maria@example.test", "OwnerPass123");
    var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<GlamByMariya.Core.Entities.AppUser>();
    var customer = App().AddCustomerWithPasswordHash("Ana", "Cruz", "ana@example.test", hasher.HashPassword(new(), "AnaPass123"), 30, "Female", "24 Synthetic Street, Quezon City");
    var soft = App().Services.Single(x => x.Name == "Soft Glam");
    GlamByMariya.Core.Entities.Booking BookXendit(DateTime start, string reference)
    {
        var s = App();
        var quote = GlamByMariya.Core.BusinessRules.BookingPricing.Calculate(soft, [], 1, s.Settings.TravelFee, "auto", s.Settings.DownPaymentPercent);
        return s.SubmitBooking(customer.Id, soft.Id, [], start, "24 Synthetic Street, Quezon City", "Quezon City", quote.RequiredDownPayment, "Xendit", reference, null,
            true, true, 1, "auto", quote.FinalTotal, quote.RequiredDownPayment, latitude: 14.5547m, longitude: 121.0244m);
    }
    async Task<string> Status(Guid booking) => (string)(await Scalar($"SELECT status FROM public.reservations WHERE app_id='{booking}'"))!;
    var day = DateTime.Today.AddDays(4);
    var paid = BookXendit(day.AddHours(10), "XENDIT-TEST-1");
    App().AttachInvoice(paid.Id, "inv_test_1", "https://checkout-staging.xendit.co/web/inv_test_1");
    var url = (string)(await Scalar($"SELECT checkout_url FROM public.payments WHERE provider_invoice_id='inv_test_1'"))!;
    Check(await Status(paid.Id) == "Pending Payment" && url.EndsWith("inv_test_1"), "a Xendit booking waits for payment with its invoice attached");
    Check(App().PendingCheckoutUrl(paid.Id) == url, "the app can show the unpaid booking's Xendit payment link again");
    var dueStore = App(); var dueAt = dueStore.FindBooking(paid.Id)?.PaymentDueAt;
    Check(dueAt is { } d && d > dueStore.Now.AddMinutes(13) && d <= dueStore.Now.AddMinutes(16), $"an unpaid Xendit booking is held for 15 minutes (due {dueAt}, now {dueStore.Now})");
    var wrong = "";
    try { App().XenditPaid("inv_test_1", paid.RequiredDownPayment - 1, "inv_test_1:PAID"); } catch (InvalidOperationException e) { wrong = e.Message; }
    Check(wrong.Contains("does not match") && await Status(paid.Id) == "Pending Payment", "a wrong paid amount does not confirm the booking");
    Check(App().XenditPaid("inv_test_1", paid.RequiredDownPayment, "inv_test_1:PAID") == "confirmed" && await Status(paid.Id) == "Confirmed",
        "the paid webhook confirms the booking");
    Check(App().PendingCheckoutUrl(paid.Id) == null, "a paid booking no longer offers the payment link");
    Check((string)(await Scalar($"SELECT status||'/'||verification_status FROM public.payments WHERE provider_invoice_id='inv_test_1'"))! == "Paid/Verified",
        "the deposit is recorded as paid and verified");
    Check(App().XenditPaid("inv_test_1", paid.RequiredDownPayment, "inv_test_1:PAID") == "duplicate", "the same webhook event twice is ignored");
    Check(App().XenditPaid("inv_test_1", paid.RequiredDownPayment, "inv_test_1:SETTLED") == "already", "a later SETTLED event changes nothing");
    var late = BookXendit(day.AddHours(14), "XENDIT-TEST-2");
    App().AttachInvoice(late.Id, "inv_test_2", "https://checkout-staging.xendit.co/web/inv_test_2");
    await Exec($"UPDATE public.reservations SET payment_expires_at=now()-interval '1 minute' WHERE app_id='{late.Id}'");
    Check(App().XenditExpired("inv_test_2", "inv_test_2:EXPIRED") == "noted" && await Status(late.Id) == "Expired", "the expired webhook releases the unpaid booking");
    Check(App().PendingCheckoutUrl(late.Id) == null, "an expired booking no longer offers the payment link");
    Check(App().XenditPaid("inv_test_2", late.RequiredDownPayment, "inv_test_2:PAID") == "late" && await Status(late.Id) == "Expired",
        "a payment after expiry is recorded for refund and does not confirm");
    var unknown = "";
    try { App().XenditPaid("inv_missing", 100, "inv_missing:PAID"); } catch (InvalidOperationException e) { unknown = e.Message; }
    Check(unknown.Contains("Unknown Xendit invoice"), "an unknown invoice is refused");
    Console.WriteLine($"All {checks - before} Xendit database checks passed.");
}

if (Run("--staff-actions"))
{
    // The owner (SuperAdmin) cancels or completes appointments in database mode, run as the app login.
    var before = checks;
    await ResetPublic();
    await SqlMigrator.ApplyAsync(source, migrations);
    PublicStudioStore App() => NewStore(as_: appSource);
    var owner = App().CreateInitialSuperAdmin("Maria", "Santos", "maria@example.test", "OwnerPass123");
    var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<GlamByMariya.Core.Entities.AppUser>();
    var customer = App().AddCustomerWithPasswordHash("Ana", "Cruz", "ana@example.test", hasher.HashPassword(new(), "AnaPass123"), 30, "Female", "24 Synthetic Street, Quezon City");
    var soft = App().Services.Single(x => x.Name == "Soft Glam");
    var n = 0;
    GlamByMariya.Core.Entities.Booking Confirmed(DateTime start)
    {
        var s = App(); n++;
        var quote = GlamByMariya.Core.BusinessRules.BookingPricing.Calculate(soft, [], 1, s.Settings.TravelFee, "auto", s.Settings.DownPaymentPercent);
        var b = s.SubmitBooking(customer.Id, soft.Id, [], start, "24 Synthetic Street, Quezon City", "Quezon City", quote.RequiredDownPayment, "Xendit", $"XENDIT-STAFF-{n}", null,
            true, true, 1, "auto", quote.FinalTotal, quote.RequiredDownPayment, latitude: 14.5547m, longitude: 121.0244m);
        App().AttachInvoice(b.Id, $"inv_staff_{n}", $"https://checkout-staging.xendit.co/web/inv_staff_{n}");
        App().XenditPaid($"inv_staff_{n}", b.RequiredDownPayment, $"inv_staff_{n}:PAID");
        return b;
    }
    // Moves an appointment in time (tests only): the guard forbids this outside an approved reschedule.
    async Task Move(Guid booking, string startsIn, string endsIn) => await Exec($"""
        SET session_replication_role=replica;
        UPDATE public.reservations SET start_at=now()+interval '{startsIn}', end_at=now()+interval '{endsIn}', reserved_until=now()+interval '{endsIn}' WHERE app_id='{booking}';
        SET session_replication_role=origin;
        """);
    async Task<string> Status(Guid booking) => (string)(await Scalar($"SELECT status FROM public.reservations WHERE app_id='{booking}'"))!;
    string Error(Action action) { try { action(); return ""; } catch (InvalidOperationException e) { return e.Message; } }
    var day = DateTime.Today.AddDays(4);

    var soon = Confirmed(day.AddHours(10));
    await Move(soon.Id, "3 hours", "4 hours");
    var customerTry = Error(() => App().CancelBooking(soon.Id, customer.Id, true));
    Check(customerTry != "" && await Status(soon.Id) == "Confirmed", "a customer still cannot cancel within 24 hours");
    var noReason = Error(() => App().ManageBooking(soon.Id, "cancel", owner.Id, " ", 0, ""));
    Check(noReason.Contains("reason") && await Status(soon.Id) == "Confirmed", "the owner must give a cancellation reason");
    var cancelled = Error(() => App().ManageBooking(soon.Id, "cancel", owner.Id, "Artist is sick", 0, ""));
    Check(cancelled == "" && await Status(soon.Id) == "Cancelled", "the owner can cancel a confirmed appointment within 24 hours" + (cancelled == "" ? "" : ": " + cancelled));
    var closure = (string)(await Scalar($"SELECT cancellation_fee::int||'/'||verified_paid_snapshot::int FROM public.reservation_closures z JOIN public.reservations r ON r.id=z.reservation_id WHERE r.app_id='{soon.Id}'"))!;
    Check(closure == $"0/{(int)soon.RequiredDownPayment}", $"a business cancellation has no fee and the whole down payment is due back ({closure})");
    var shown = App().FindBooking(soon.Id);
    Check(shown?.StaffCancelledAt != null && shown.StaffCancelledBy == owner.Id && shown.StaffCancellationReason == "Artist is sick" && shown.RefundDue == soon.RequiredDownPayment,
        "booking details show the business cancellation, its reason and the refund due");

    var forged = Confirmed(day.AddHours(14));
    var forgedError = "";
    try { await Exec($"UPDATE public.reservations SET status='Cancelled', cancellation_confirmed_at=now(), staff_cancelled_by='{customer.Id}', staff_cancelled_at=now(), staff_cancellation_reason='x' WHERE app_id='{forged.Id}'"); }
    catch (PostgresException e) { forgedError = e.MessageText; }
    Check(forgedError.Contains("SuperAdmin") && await Status(forged.Id) == "Confirmed", "the database refuses a business cancellation not made by the active SuperAdmin");

    var future = Confirmed(day.AddDays(1).AddHours(10));
    var early = Error(() => App().ManageBooking(future.Id, "complete", owner.Id, "", 0, ""));
    Check(early != "" && await Status(future.Id) == "Confirmed", "an appointment that has not ended cannot be completed");

    var done = Confirmed(day.AddDays(1).AddHours(14));
    await Move(done.Id, "-3 hours", "-2 hours");
    var completed = Error(() => App().ManageBooking(done.Id, "complete", owner.Id, "", 0, ""));
    Check(completed == "" && await Status(done.Id) == "Completed", "the owner can complete an ended appointment" + (completed == "" ? "" : ": " + completed));
    var balance = (string)(await Scalar($"SELECT p.method||'/'||p.status||'/'||p.amount::int FROM public.payments p JOIN public.reservations r ON r.id=p.reservation_id WHERE r.app_id='{done.Id}' AND p.purpose='balance'"))!;
    Check(balance == $"Cash/Paid/{(int)(done.Total - done.RequiredDownPayment)}", $"completing records the remaining balance as received in cash ({balance})");
    var after = App().FindBooking(done.Id);
    Check(after?.CompletedAt != null && after.CompletedBy == owner.Id, "booking details show who completed the appointment and when");
    Console.WriteLine($"All {checks - before} staff action database checks passed.");
}

if (Run("--website-content"))
{
    // Website text drafts and publishing, run as the app login through the real repository.
    var before = checks;
    await ResetPublic();
    await SqlMigrator.ApplyAsync(source, migrations);
    var owner = NewStore(as_: appSource).CreateInitialSuperAdmin("Maria", "Santos", "maria@example.test", "OwnerPass123");
    var repo = new GlamByMariya.Infrastructure.Data.PostgresWebsiteContentRepository(appSource);
    var first = await repo.CreateDraft(owner.Id, default);
    Check(first.Revision == 1 && first.Status == "draft", "a new draft starts at revision 1");
    Check((await repo.CreateDraft(owner.Id, default)).Id == first.Id, "creating a draft again returns the existing draft");
    var text = new GlamByMariya.Core.BusinessRules.WebsiteTextInput("Glam at home", "Makeup that comes to you", "Meet Mariya", "Bio", "Home service", "Quezon City", "Call me",
        [new("Instagram", "https://instagram.com/glam")], [new("Do you travel?", "Yes, within Metro Manila.")]);
    var saved = await repo.SaveDraft(owner.Id, first.Id, first.Revision, text, default);
    var read = await repo.ReadDraft(default);
    Check(saved.Revision == 2 && read!.Text.HeroHeadline == "Glam at home" && read.Text.AboutHeading == "Meet Mariya"
        && read.Text.Links!.Single().Label == "Instagram" && read.Text.Faqs!.Single().Question == "Do you travel?", "a saved draft reads back its text, link and FAQ");
    var stale = "";
    try { await repo.SaveDraft(owner.Id, first.Id, 1, text, default); } catch (InvalidOperationException e) { stale = e.Message; }
    Check(stale != "", "saving with an old revision is refused");
    await repo.Publish(owner.Id, first.Id, saved.Revision, default);
    Check((await repo.ReadPublished(default))!.Text.AboutHeading == "Meet Mariya" && await repo.ReadDraft(default) == null, "publishing makes the draft the live version");
    var next = await repo.CreateDraft(owner.Id, default);
    Check(next.Id != first.Id && next.Text.AboutHeading == "Meet Mariya" && next.Text.Links!.Count == 1 && next.Text.Faqs!.Count == 1,
        "a new draft copies the published text, links and FAQs");
    var duplicate = "";
    try { await Exec("INSERT INTO public.website_content_versions(id,status) VALUES(gen_random_uuid(),'draft')"); } catch (PostgresException e) { duplicate = e.SqlState ?? ""; }
    Check(duplicate == "23505", "a second draft row is refused by the database");
    string Key() => "website-content/" + Guid.NewGuid().ToString("N") + ".jpg";
    var writer = (GlamByMariya.Infrastructure.Services.IWebsiteAssetWriter)repo;
    var assetA = await writer.AddAsset(owner.Id, Key(), "image/jpeg", 1234, default);
    var heroKey = Key();
    var assetHero = await writer.AddAsset(owner.Id, heroKey, "image/jpeg", 2000, default);
    var assetB = await writer.AddAsset(owner.Id, Key(), "image/jpeg", 3000, default);
    Check(assetA != Guid.Empty && assetA != assetHero, "adding an image record returns a new id");
    var badKey = "";
    try { await writer.AddAsset(owner.Id, "other/x.jpg", "image/jpeg", 10, default); } catch (PostgresException e) { badKey = e.SqlState ?? ""; }
    Check(badKey == "23514", "an image key outside website-content/ is refused by the database");
    var heroId = Guid.NewGuid();
    var imageList = new List<GlamByMariya.Infrastructure.Services.WebsiteImageReference> {
        new(heroId, assetHero, "hero", "Mariya at work", "", 0, true),
        new(Guid.NewGuid(), assetA, "gallery", "Bridal look", "Bride", 1, true),
        new(Guid.NewGuid(), assetB, "gallery", "Evening look", "", 2, true) };
    await repo.ReplaceDraftImages(owner.Id, next.Id, next.Revision, imageList, default);
    Check((await repo.ReadDraftImages(next.Id, default)).Count == 3, "a draft keeps its hero and two gallery images");
    var staleImages = "";
    try { await repo.ReplaceDraftImages(owner.Id, next.Id, next.Revision, imageList, default); } catch (InvalidOperationException e) { staleImages = e.Message; }
    Check(staleImages != "", "replacing images with an old revision is refused");
    Check((await repo.FindDraftImage(heroId, default))?.ObjectKey == heroKey, "a draft image can be found before publishing");
    await repo.Publish(owner.Id, next.Id, next.Revision + 1, default);
    Check((await repo.ReadPublishedImages(default)).Count == 3, "publishing makes the draft images live");
    Check((await repo.FindPublishedImage(heroId, default))?.ObjectKey == heroKey, "a published image is found by its id");
    Check(await repo.FindDraftImage(heroId, default) == null, "a published image is no longer a draft image");
    var nextDraft = await repo.CreateDraft(owner.Id, default);
    Check((await repo.ReadDraftImages(nextDraft.Id, default)).Count == 3, "a new draft copies the published images");
    var recorded = (long)(await Scalar("SELECT count(*) FROM public.schema_migrations"))!;
    Check(recorded == Directory.GetFiles(migrations, "*.sql").Length, "every migration file is recorded");
    Console.WriteLine($"All {checks - before} website content database checks passed.");
}

if (Run("--booking-location"))
{
    // A booking can carry the customer's exact pin; the database keeps it paired and inside Metro Manila.
    var before = checks;
    await ResetPublic();
    await SqlMigrator.ApplyAsync(source, migrations);
    PublicStudioStore App() => NewStore(as_: appSource);
    App().CreateInitialSuperAdmin("Maria", "Santos", "maria@example.test", "OwnerPass123");
    var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<GlamByMariya.Core.Entities.AppUser>();
    var customer = App().AddCustomerWithPasswordHash("Ana", "Cruz", "ana@example.test", hasher.HashPassword(new(), "AnaPass123"), 30, "Female", "24 Synthetic Street, Quezon City");
    var soft = App().Services.Single(x => x.Name == "Soft Glam");
    var quote = GlamByMariya.Core.BusinessRules.BookingPricing.Calculate(soft, [], 1, App().Settings.TravelFee, "auto", App().Settings.DownPaymentPercent);
    var booking = App().SubmitBooking(customer.Id, soft.Id, [], DateTime.Today.AddDays(4).AddHours(10), "24 Synthetic Street, Quezon City", "Quezon City", quote.RequiredDownPayment, "Xendit", "XENDIT-LOC-1", null,
        true, true, 1, "auto", quote.FinalTotal, quote.RequiredDownPayment, latitude: 14.5547m, longitude: 121.0244m);
    var readBack = App().FindBooking(booking.Id)!;
    Check(readBack.Latitude == 14.5547m && readBack.Longitude == 121.0244m, "a booking submitted with a pin reads back its pin");
    Check(await Refused(() => Task.FromResult(App().SubmitBooking(customer.Id, soft.Id, [], DateTime.Today.AddDays(5).AddHours(10), "24 Synthetic Street, Quezon City", "Quezon City", quote.RequiredDownPayment, "Xendit", "XENDIT-LOC-2", null,
        true, true, 1, "auto", quote.FinalTotal, quote.RequiredDownPayment))) == "Pin your exact location inside Metro Manila or Rizal.", "the store refuses a booking without a pin");
    async Task<string> InsertWithoutPin()
    {
        try
        {
            await Exec($"""
                INSERT INTO public.reservations(app_id, app_managed, snapshot_source, reference, customer_id, artist_id, service_id, start_at, location, area_id, booking_type, people,
                  addons_total_snapshot, addons_minutes_snapshot, policy_accepted_at, policy_version, travel_reminder_acknowledged_at, travel_reminder_version, status)
                SELECT gen_random_uuid(), true, 'booking_creation', 'LOC-NOPIN', r.customer_id, r.artist_id, r.service_id, r.start_at + interval '3 days', '24 Synthetic Street, Quezon City', r.area_id, 'individual', 1,
                  0, 0, now(), r.policy_version, now(), r.travel_reminder_version, 'Pending Payment'
                FROM public.reservations r WHERE r.app_id='{booking.Id}'
                """);
            return "";
        }
        catch (PostgresException e) { return e.MessageText; }
    }
    Check(await InsertWithoutPin() == "Pin your exact location inside Metro Manila.", "the database refuses a new app booking without a pin");
    async Task<string> Update(string set)
    {
        try { await Exec($"UPDATE public.reservations SET {set} WHERE app_id='{booking.Id}'"); return ""; }
        catch (PostgresException e) { return e.SqlState ?? e.MessageText; }
    }
    async Task<string> Pin() => (string)(await Scalar($"SELECT coalesce(location_lat::text,'null')||'/'||coalesce(location_lng::text,'null') FROM public.mvc_booking_summary WHERE id='{booking.Id}'"))!;
    Check(await Update("location_lat=14.5547, location_lng=121.0244") == "", "the guard allows setting the pin on an existing booking");
    Check(await Pin() == "14.554700/121.024400", "the booking summary shows the pin (" + await Pin() + ")");
    Check(await Update("location_lat=14.6, location_lng=NULL") == "23514", "a latitude without a longitude is refused");
    Check(await Update("location_lat=15.5, location_lng=121.0244") == "23514", "a pin north of Metro Manila is refused");
    Check(await Update("location_lat=14.5547, location_lng=121.7") == "23514", "a pin east of Rizal is refused");
    Check(await Update("location_lat=14.5236, location_lng=121.2856") == "", "a pin in Tanay, Rizal is accepted");
    Check(await Update("location_lat=14.20, location_lng=120.85") == "", "the south-west corner of Metro Manila is accepted");
    Check(await Update("location_lat=NULL, location_lng=NULL") == "" && await Pin() == "null/null", "a booking can have no pin");
    var legacy = App().FindBooking(booking.Id)!;
    Check(legacy.Latitude == null && legacy.Longitude == null, "an older booking without a pin still loads");
    var recorded = (long)(await Scalar("SELECT count(*) FROM public.schema_migrations"))!;
    Check(recorded == Directory.GetFiles(migrations, "*.sql").Length, "every migration file is recorded");
    Console.WriteLine($"All {checks - before} booking location database checks passed.");
}

if (Run("--travel"))
{
    // Travel settings, the artist's home and current pins, and the neighbour bookings the travel rule reads.
    var before = checks;
    await ResetPublic();
    await SqlMigrator.ApplyAsync(source, migrations);
    PublicStudioStore App() => NewStore(as_: appSource);
    var owner = App().CreateInitialSuperAdmin("Maria", "Santos", "maria@example.test", "OwnerPass123");
    var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<GlamByMariya.Core.Entities.AppUser>();
    var customer = App().AddCustomerWithPasswordHash("Ana", "Cruz", "ana@example.test", hasher.HashPassword(new(), "AnaPass123"), 30, "Female", "24 Synthetic Street, Quezon City");

    var defaults = App().Settings;
    Check(defaults.TravelSpeedKmh == 15m && defaults.TravelMinimumMinutes == 30, $"travel settings default to 15 km/h and 30 minutes ({defaults.TravelSpeedKmh}/{defaults.TravelMinimumMinutes})");
    var editor = App(); var edited = editor.Settings; edited.TravelSpeedKmh = 20.5m; edited.TravelMinimumMinutes = 45;
    editor.SaveSettings(edited, owner.Id);
    var saved = App().Settings;
    Check(saved.TravelSpeedKmh == 20.5m && saved.TravelMinimumMinutes == 45, "saving the business settings keeps the travel speed and minimum");
    async Task<string> State(string sql)
    {
        try { await Exec(sql); return ""; } catch (PostgresException e) { return e.SqlState ?? e.MessageText; }
    }
    Check(await State("UPDATE public.business_settings SET travel_speed_kmh=61 WHERE id=1") == "23514", "a travel speed of 61 km/h is refused");
    Check(await State("UPDATE public.business_settings SET travel_speed_kmh=4.9 WHERE id=1") == "23514", "a travel speed under 5 km/h is refused");
    Check(await State("UPDATE public.business_settings SET travel_min_minutes=181 WHERE id=1") == "23514" && await State("UPDATE public.business_settings SET travel_min_minutes=-1 WHERE id=1") == "23514",
        "a travel minimum outside 0-180 minutes is refused");
    var tooFast = App(); var fast = tooFast.Settings; fast.TravelSpeedKmh = 61;
    var storeRefusal = "";
    try { tooFast.SaveSettings(fast, owner.Id); } catch (InvalidOperationException e) { storeRefusal = (e.InnerException as PostgresException)?.SqlState ?? ""; }
    Check(storeRefusal == "23514" && App().Settings.TravelSpeedKmh == 20.5m, "the store cannot save a travel speed of 61 km/h");

    var none = App().GetArtistLocation();
    Check(none == new GlamByMariya.Core.Entities.ArtistLocation(null, null, null, null, null), "a new artist has no home or current pin");
    App().SetArtistHome(14.6760m, 121.0437m);
    App().SetArtistCurrent(14.5547m, 121.0244m);
    var pinned = App().GetArtistLocation();
    Check(pinned.HomeLat == 14.6760m && pinned.HomeLng == 121.0437m && pinned.CurrentLat == 14.5547m && pinned.CurrentLng == 121.0244m && pinned.CurrentUpdatedAt != null,
        "the artist's home and current pins read back");
    App().SetArtistCurrent(null, null);
    var cleared = App().GetArtistLocation();
    Check(cleared.CurrentLat == null && cleared.CurrentLng == null && cleared.CurrentUpdatedAt == null && cleared.HomeLat == 14.6760m && cleared.HomeLng == 121.0437m,
        "clearing the current pin keeps the home pin");
    Check(await Refused(() => { App().SetArtistHome(15.5m, 121.0437m); return Task.CompletedTask; }) != "" && App().GetArtistLocation().HomeLat == 14.6760m,
        "the store refuses a home pin outside Metro Manila");
    Check(await Refused(() => { App().SetArtistCurrent(14.5547m, null); return Task.CompletedTask; }) != "", "the store refuses half a current pin");
    Check(await State("UPDATE public.makeup_artists SET home_lat=14.6, home_lng=NULL") == "23514", "the database refuses a home latitude without a longitude");
    Check(await State("UPDATE public.makeup_artists SET current_lat=14.5547, current_lng=121.7") == "23514", "the database refuses a current pin east of Rizal");

    var soft = App().Services.Single(x => x.Name == "Soft Glam");
    var quote = GlamByMariya.Core.BusinessRules.BookingPricing.Calculate(soft, [], 1, App().Settings.TravelFee, "auto", App().Settings.DownPaymentPercent);
    var day = DateTime.Today.AddDays(4);
    var n = 0;
    GlamByMariya.Core.Entities.Booking Book(int hour, decimal lat, decimal lng) => App().SubmitBooking(customer.Id, soft.Id, [], day.AddHours(hour), "24 Synthetic Street, Quezon City", "Quezon City",
        quote.RequiredDownPayment, "Xendit", $"XENDIT-TRAVEL-{++n}", null, true, true, 1, "auto", quote.FinalTotal, quote.RequiredDownPayment, latitude: lat, longitude: lng);
    var afternoon = Book(13, 14.7046m, 121.0567m);
    var morning = Book(10, 14.5547m, 121.0244m);
    var late = Book(16, 14.6000m, 121.0000m);
    var all = App().TravelStops(day, day.AddDays(1), null);
    Check(all.Count == 3, $"every booking holding a slot that day is a neighbour ({all.Count})");
    await Exec($"UPDATE public.reservations SET payment_expires_at=now()-interval '1 minute' WHERE app_id='{late.Id}'");
    App().ExpireOverduePayments();
    var stops = App().TravelStops(day, day.AddDays(1), null);
    Check(stops.Count == 2 && stops[0].Start == day.AddHours(10) && stops[1].Start == day.AddHours(13),
        "the two pinned bookings of the day come back in time order without the expired one");
    Check(stops[0].End == day.AddHours(10).AddMinutes(soft.Minutes) && stops[0].BufferMinutes == 45 && stops[0].Latitude == 14.5547m && stops[0].Longitude == 121.0244m
        && stops[1].Latitude == 14.7046m && stops[1].Longitude == 121.0567m, "each neighbour carries its Manila-local times, buffer and pin");
    var others = App().TravelStops(day, day.AddDays(1), morning.Id);
    Check(others.Count == 1 && others[0].Start == day.AddHours(13), "the booking being moved is left out");
    Check(App().TravelStops(day.AddDays(1), day.AddDays(2), null).Count == 0, "another day has no neighbours");

    // Task 3: saving re-runs the travel rule under a per-artist lock. Settings here: 20.5 km/h, minimum 45,
    // so Makati to Fairview takes 75 minutes; a Makati 10:00 booking (60 min + 45 buffer) needs 13:00 from Fairview.
    const decimal mkLat = 14.5547m, mkLng = 121.0244m, fvLat = 14.7046m, fvLng = 121.0567m;
    Check(GlamByMariya.Core.BusinessRules.TravelPlanner.Minutes(mkLat, mkLng, fvLat, fvLng, 20.5m, 45) == 75, "fixture: Makati to Fairview takes 75 minutes at 20.5 km/h");
    var bea = App().AddCustomerWithPasswordHash("Bea", "Reyes", "bea@example.test", hasher.HashPassword(new(), "BeaPass123"), 30, "Female", "25 Synthetic Street, Quezon City");
    GlamByMariya.Core.Entities.Booking BookAt(PublicStudioStore s, Guid who, DateTime start, decimal lat, decimal lng, string method = "Xendit") => s.SubmitBooking(who, soft.Id, [], start, "24 Synthetic Street, Quezon City", "Quezon City",
        quote.RequiredDownPayment, method, $"TRAVEL-SAVE-{++n}", null, true, true, 1, "auto", quote.FinalTotal, quote.RequiredDownPayment, latitude: lat, longitude: lng);
    var gapDay = day.AddDays(1);
    var stalePage = App(); _ = stalePage.Settings; // the second customer's page was loaded before the first booking was saved
    BookAt(App(), customer.Id, gapDay.AddHours(10), mkLat, mkLng);
    var staleRefusal = await Refused(() => { BookAt(stalePage, bea.Id, gapDay.AddHours(11).AddMinutes(45), fvLat, fvLng); return Task.CompletedTask; });
    Check(staleRefusal == GlamByMariya.Core.BusinessRules.TravelPlanner.RefusalMessage, $"a second booking for the same gap is refused with the travel message ({staleRefusal})");
    Check(BookAt(App(), bea.Id, gapDay.AddHours(13), fvLat, fvLng).Start == gapDay.AddHours(13), "13:00 from Fairview still fits after the Makati booking");

    var raceDay = day.AddDays(2);
    using var travelBarrier = new Barrier(2);
    var raced = await Task.WhenAll(new[] { (customer.Id, raceDay.AddHours(10), mkLat, mkLng), (bea.Id, raceDay.AddHours(11).AddMinutes(45), fvLat, fvLng) }.Select(x => Task.Run(() =>
    {
        var s = App(); _ = s.Settings; travelBarrier.SignalAndWait();
        try { BookAt(s, x.Item1, x.Item2, x.Item3, x.Item4); return ""; } catch (InvalidOperationException e) { return e.Message; }
    })));
    var raceCount = (long)(await Scalar($"SELECT count(*) FROM public.reservations WHERE start_at >= ('{raceDay:yyyy-MM-dd}'::timestamp AT TIME ZONE 'Asia/Manila') AND start_at < ('{raceDay.AddDays(1):yyyy-MM-dd}'::timestamp AT TIME ZONE 'Asia/Manila')"))!;
    Check(raced.Count(r => r == "") == 1 && raced.Count(r => r == GlamByMariya.Core.BusinessRules.TravelPlanner.RefusalMessage) == 1 && raceCount == 1,
        $"two customers saving the same gap at once: only one booking is saved ({string.Join(" | ", raced)}; {raceCount})");

    // Reschedule approval re-runs the rule without the moved booking; only the SuperAdmin may override.
    async Task Confirm(Guid booking) => await Exec($"""
        BEGIN; SELECT set_config('makeup.actor_id','{owner.Id}',true);
        UPDATE public.payments SET status='Paid', verification_status='Verified', verified_by='{owner.Id}', verified_at=now()
         WHERE reservation_id=(SELECT id FROM public.reservations WHERE app_id='{booking}');
        UPDATE public.reservations SET status='Confirmed' WHERE app_id='{booking}'; COMMIT;
        """);
    var staff = App().AddUser("Staff", "Member", "staff@example.test", "StaffPass123", "Admin", 30, "Female", "26 Synthetic Street, Quezon City");
    var moveDay = day.AddDays(3);
    var moving = BookAt(App(), customer.Id, moveDay.AddHours(9), mkLat, mkLng, "GCash");
    await Confirm(moving.Id);
    App().RequestReschedule(moving.Id, customer.Id, moveDay.AddHours(11));
    App().ManageBooking(moving.Id, "reschedule", owner.Id, "", 0, "");
    Check(App().FindBooking(moving.Id)!.Start == moveDay.AddHours(11), "approving 9:00 to 11:00 does not count the booking itself");
    BookAt(App(), bea.Id, moveDay.AddHours(15), fvLat, fvLng);
    App().RequestReschedule(moving.Id, customer.Id, moveDay.AddHours(12).AddMinutes(15));
    var strict = await Refused(() => { App().ManageBooking(moving.Id, "reschedule", owner.Id, "", 0, ""); return Task.CompletedTask; });
    Check(strict == GlamByMariya.Core.BusinessRules.TravelPlanner.RefusalMessage && App().FindBooking(moving.Id)!.Start == moveDay.AddHours(11),
        $"approval refuses 12:15 before a 15:00 Fairview booking ({strict})");
    var byAdmin = await Refused(() => { App().ManageBooking(moving.Id, "reschedule", staff.Id, "", 0, "", travelOverride: true); return Task.CompletedTask; });
    Check(byAdmin == GlamByMariya.Infrastructure.Services.BookingService.TravelOverrideRefused, $"an Admin cannot override the travel rule ({byAdmin})");
    App().ManageBooking(moving.Id, "reschedule", owner.Id, "", 0, "", travelOverride: true);
    var overridden = (string)(await Scalar($"SELECT (start_at AT TIME ZONE 'Asia/Manila')::text||'/'||coalesce(travel_override_by::text,'none')||'/'||(travel_override_at IS NOT NULL) FROM public.reservations WHERE app_id='{moving.Id}'"))!;
    Check(overridden == $"{moveDay.AddHours(12).AddMinutes(15):yyyy-MM-dd HH:mm:ss}/{owner.Id}/true", $"the SuperAdmin override approves and records who and when ({overridden})");
    App().RequestReschedule(moving.Id, customer.Id, moveDay.AddHours(9));
    App().ManageBooking(moving.Id, "reschedule", owner.Id, "", 0, "");
    var unset = (string)(await Scalar($"SELECT (start_at AT TIME ZONE 'Asia/Manila')::text||'/'||(travel_override_by IS NULL)||'/'||(travel_override_at IS NULL) FROM public.reservations WHERE app_id='{moving.Id}'"))!;
    Check(unset == $"{moveDay.AddHours(9):yyyy-MM-dd HH:mm:ss}/true/true", $"a later approval that fits clears the travel override ({unset})");

    // Today: an unpinned artist needs the minimum (45 here); after a finished booking, the artist starts from its pin.
    var manila = App().Now;
    if (manila.TimeOfDay > TimeSpan.FromHours(3) && manila.TimeOfDay < TimeSpan.FromHours(22))
    {
        await Exec("UPDATE public.makeup_artists SET home_lat=NULL, home_lng=NULL, current_lat=NULL, current_lng=NULL, current_updated_at=NULL");
        Check(!GlamByMariya.Infrastructure.Services.BookingService.TravelFits(App(), manila.AddMinutes(40), 60, 45, fvLat, fvLng, null)
            && GlamByMariya.Infrastructure.Services.BookingService.TravelFits(App(), manila.AddMinutes(50), 60, 45, fvLat, fvLng, null),
            "today an unpinned artist needs now plus the minimum travel");
        App().SetArtistCurrent(mkLat, mkLng);
        var finished = BookAt(App(), bea.Id, day.AddDays(5).AddHours(10), fvLat, fvLng, "GCash");
        await Exec($"""
            SET session_replication_role = replica;
            UPDATE public.reservations SET start_at=now()-interval '2 hours', end_at=now()-interval '1 hour', reserved_until=now()-interval '15 minutes', status='Completed' WHERE app_id='{finished.Id}';
            SET session_replication_role = origin;
            """);
        var finishedStops = App().FinishedStops(manila.Date, manila.Date.AddDays(1));
        Check(finishedStops.Count == 1 && finishedStops[0].Latitude == fvLat && finishedStops[0].Longitude == fvLng, $"today's completed booking is a finished stop with its pin ({finishedStops.Count})");
        Check(GlamByMariya.Infrastructure.Services.BookingService.TravelFits(App(), manila.AddMinutes(50), 60, 45, fvLat, fvLng, null)
            && !GlamByMariya.Infrastructure.Services.BookingService.TravelFits(App(), manila.AddMinutes(50), 60, 45, mkLat, mkLng, null),
            "after a completed Fairview booking the artist starts from Fairview, not the current Makati pin");
    }
    else Console.WriteLine("SKIP: same-day travel checks need a Manila clock between 03:00 and 22:00");

    const string signature = "public.mvc_travel_stops(timestamptz,timestamptz,uuid)";
    var privileges = (string)(await Scalar($"""
        SELECT has_function_privilege('anon','{signature}','EXECUTE')||'/'||has_function_privilege('authenticated','{signature}','EXECUTE')||'/'||
               has_function_privilege('makeup_backend','{signature}','EXECUTE')||'/'||(SELECT p.prosecdef FROM pg_proc p WHERE p.oid='{signature}'::regprocedure)||'/'||
               EXISTS(SELECT 1 FROM pg_proc p, aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a WHERE p.oid='{signature}'::regprocedure AND a.grantee=0 AND a.privilege_type='EXECUTE')
        """))!;
    Check(privileges == "false/false/true/false/false", $"only the backend role may run mvc_travel_stops, as invoker ({privileges})");
    var overrides = await State($"UPDATE public.reservations SET travel_override_by='{owner.Id}' WHERE app_id='{morning.Id}'");
    Check(overrides == "23514", $"a travel override needs both who and when ({overrides})");
    var recorded = (long)(await Scalar("SELECT count(*) FROM public.schema_migrations"))!;
    Check(recorded == Directory.GetFiles(migrations, "*.sql").Length, "every migration file is recorded");
    Console.WriteLine($"All {checks - before} travel database checks passed.");
}

if (Run("--flexible"))
{
    // Phase 6: early-request hours, package limits, the Awaiting Approval status and its transitions.
    var before = checks;
    await ResetPublic();
    await SqlMigrator.ApplyAsync(source, migrations);
    PublicStudioStore App() => NewStore(as_: appSource);
    var owner = App().CreateInitialSuperAdmin("Maria", "Santos", "maria@example.test", "OwnerPass123");
    var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<GlamByMariya.Core.Entities.AppUser>();
    var customer = App().AddCustomerWithPasswordHash("Ana", "Cruz", "ana@example.test", hasher.HashPassword(new(), "AnaPass123"), 30, "Female", "24 Synthetic Street, Quezon City");
    async Task<string> State(string sql)
    {
        try { await Exec(sql); return ""; } catch (PostgresException e) { return e.SqlState + " " + e.MessageText; }
    }

    var fresh = App().Settings;
    Check(fresh.EarlyRequestStartHour == null && fresh.AfterHoursEndHour == null, "a fresh database has no early or latest hour (opening hours apply)");
    Check(App().Services.Single(x => x.Name == "Bridal Makeup").MaxPerPackage == 1 && App().Services.Single(x => x.Name == "Soft Glam").MaxPerPackage == null,
        "Bridal Makeup is limited to 1 per package; Soft Glam has no limit");
    Check((await State("UPDATE public.business_settings SET early_request_start_hour=9 WHERE id=1")).StartsWith("23514"), "an early start after opening is refused");
    Check((await State("UPDATE public.business_settings SET after_hours_end_hour=17 WHERE id=1")).StartsWith("23514"), "a latest end before closing is refused");
    var editor = App(); var edited = editor.Settings; edited.EarlyRequestStartHour = 4; edited.AfterHoursEndHour = 22;
    editor.SaveSettings(edited, owner.Id);
    Check(App().Settings.EarlyRequestStartHour == 4 && App().Settings.AfterHoursEndHour == 22, "saving the business settings keeps the early and latest hours");

    var soft = App().Services.Single(x => x.Name == "Soft Glam");
    var quote = GlamByMariya.Core.BusinessRules.BookingPricing.Calculate(soft, [], 1, App().Settings.TravelFee, "auto", App().Settings.DownPaymentPercent);
    var day = DateTime.Today.AddDays(5);
    var n = 0;
    GlamByMariya.Core.Entities.Booking Book(double hour) => App().SubmitBooking(customer.Id, soft.Id, [], day.AddHours(hour), "24 Synthetic Street, Quezon City", "Quezon City",
        quote.RequiredDownPayment, "Xendit", $"XENDIT-FLEX-{++n}", null, true, true, 1, "auto", quote.FinalTotal, quote.RequiredDownPayment, latitude: 14.5547m, longitude: 121.0244m);
    // Test setup only: turn a booking into an untouched early request (the store learns to insert these in Task 3).
    async Task MakeRequest(Guid booking, string createdAgo = "0 minutes") => await Exec($"""
        SET session_replication_role = replica;
        UPDATE public.reservations SET status='Awaiting Approval', is_early_request=true, payment_expires_at=NULL, created_at=now()-interval '{createdAgo}' WHERE app_id='{booking}';
        SET session_replication_role = origin;
        """);
    async Task<string> Status(Guid booking) => (string)(await Scalar($"SELECT status FROM public.reservations WHERE app_id='{booking}'"))!;

    var request = Book(5);
    Check(App().FindBooking(request.Id) is { IsEarlyRequest: true, Status: "Awaiting Approval" }, "an early request reads back as Awaiting Approval");
    var rival = Book(5.5);
    Check(await Status(rival.Id) == "Awaiting Approval", "a request holds no time: another customer can request an overlapping early time");
    Check(await State($"BEGIN; SELECT set_config('makeup.actor_id','{owner.Id}',true); UPDATE public.reservations SET status='Pending Payment' WHERE app_id='{request.Id}'; COMMIT;") == "",
        "the first request is approved");
    var approveTaken = await State($"BEGIN; SELECT set_config('makeup.actor_id','{owner.Id}',true); UPDATE public.reservations SET status='Pending Payment' WHERE app_id='{rival.Id}'; COMMIT;");
    Check(approveTaken.Contains("overlaps a reserved appointment") && await Status(rival.Id) == "Awaiting Approval", $"approving a request whose time was taken is refused ({approveTaken})");

    var second = Book(9);
    await MakeRequest(second.Id);
    Check(await State($"BEGIN; SELECT set_config('makeup.actor_id','{owner.Id}',true); UPDATE public.reservations SET status='Pending Payment' WHERE app_id='{second.Id}'; COMMIT;") == "",
        "approving a free request moves it to Pending Payment");
    var minutes = Convert.ToDouble(await Scalar($"SELECT extract(epoch FROM payment_expires_at-now())/60 FROM public.reservations WHERE app_id='{second.Id}'"));
    Check(minutes is > 14 and <= 15.1, $"approval starts the 15-minute payment hold ({minutes:F1} min)");
    var blocked = "";
    try { Book(9); } catch (InvalidOperationException e) { blocked = e.Message; }
    Check(blocked.Contains("overlaps"), "an approved request blocks its time for other bookings");

    var stale = Book(13);
    await MakeRequest(stale.Id, "8 days");
    await Exec("SELECT public.mvc_expire_overdue()");
    Check(await Status(stale.Id) == "Rejected" && (string)(await Scalar($"SELECT notes FROM public.reservations WHERE app_id='{stale.Id}'"))! == "The artist could not confirm this early time.",
        "a request unanswered for 7 days is closed as Rejected");
    var storeRequest = Book(4.5);
    Check(storeRequest is { Status: "Awaiting Approval", IsEarlyRequest: true } && await Scalar($"SELECT payment_expires_at FROM public.reservations WHERE app_id='{storeRequest.Id}'") is DBNull,
        "the store books a time before opening as an early request with no payment deadline");
    var evening = Book(17.5);
    Check(evening is { Status: "Pending Payment", AfterHours: true, IsEarlyRequest: false }, "the store flags a session ending after closing as after-hours");
    var flags = await Scalar($"SELECT is_early_request::text||'/'||after_hours::text FROM public.mvc_booking_summary WHERE id='{second.Id}'");
    Check((string)flags! == "true/false", $"the booking summary shows the request and after-hours flags ({flags})");
    // Task 4: a mixed package (1 Bridal + 2 Soft Glam + 3 Glamour) on the live schema.
    var bridal = App().Services.Single(x => x.Name == "Bridal Makeup");
    var glam = App().Services.Single(x => x.Name == "Glamour Makeup");
    var package = new[] { bridal, soft, soft, glam, glam, glam }.Select(x => new GlamByMariya.Core.Entities.MixedPersonSelection(x.Id, Array.Empty<Guid>())).ToArray();
    var packageTotal = bridal.Price + 2 * soft.Price + 3 * glam.Price;
    var packageMinutes = bridal.Minutes + 2 * soft.Minutes + 3 * glam.Minutes;
    var packageDeposit = GlamByMariya.Core.Entities.Money.Percentage(packageTotal, App().Settings.DownPaymentPercent);
    var packageDay = DateTime.Today.AddDays(6);
    var mixed = App().SubmitPackage(customer.Id, package, packageDay.AddHours(9), "24 Synthetic Street, Quezon City", "Quezon City", packageDeposit, "Xendit", "XENDIT-PACKAGE-1",
        true, true, packageTotal, packageDeposit, 14.5547m, 121.0244m);
    var stored = App().FindBooking(mixed.Id)!;
    Check(stored.MixedPeople.Count == 6 && stored.Total == packageTotal && (stored.End - stored.Start).TotalMinutes == packageMinutes,
        $"a mixed package stores six people, the summed total and duration ({stored.MixedPeople.Count}, {stored.Total}, {(stored.End - stored.Start).TotalMinutes})");
    Check((string)(await Scalar($"SELECT booking_type FROM public.reservations WHERE app_id='{mixed.Id}'"))! == "mixed", "the package is saved as a mixed booking");
    var twoBridal = "";
    try { App().SubmitPackage(customer.Id, [package[0], package[0], package[1]], packageDay.AddHours(15), "24 Synthetic Street, Quezon City", "Quezon City", packageDeposit, "Xendit", "XENDIT-PACKAGE-2",
        true, true, packageTotal, packageDeposit, 14.5547m, 121.0244m); }
    catch (InvalidOperationException e) { twoBridal = e.Message; }
    Check(twoBridal == "Only 1 Bridal Makeup is allowed per package booking.", $"the store refuses two Bridal in one package ({twoBridal})");
    var sneak = await State($"""
        BEGIN;
        INSERT INTO public.reservation_people(reservation_id, position, service_id, service_name_snapshot, price_snapshot, minutes_snapshot)
        SELECT r.id, 7, s.id, s.name, s.price, s.duration_minutes FROM public.reservations r, public.services s WHERE r.app_id='{mixed.Id}' AND s.name='Bridal Makeup';
        COMMIT;
        """);
    Check(sneak.Contains("package"), $"the database refuses a person added outside the booking (second Bridal) ({sneak})");

    // Task 5: the admin approves or declines through the store.
    string FlexRefusal(Action action) { try { action(); } catch (InvalidOperationException e) { return e.Message; } return ""; }
    var taken = FlexRefusal(() => App().ManageBooking(storeRequest.Id, "approve-request", owner.Id, "", 0, ""));
    Check(taken.Contains("overlaps") && await Status(storeRequest.Id) == "Awaiting Approval", $"the store refuses to approve a request whose time is taken, leaving it unchanged ({taken})");
    var laterDay = DateTime.Today.AddDays(7);
    GlamByMariya.Core.Entities.Booking BookOn(DateTime at) => App().SubmitBooking(customer.Id, soft.Id, [], at, "24 Synthetic Street, Quezon City", "Quezon City",
        quote.RequiredDownPayment, "Xendit", $"XENDIT-FLEX-{++n}", null, true, true, 1, "auto", quote.FinalTotal, quote.RequiredDownPayment, latitude: 14.5547m, longitude: 121.0244m);
    var toApprove = BookOn(laterDay.AddHours(5));
    App().ManageBooking(toApprove.Id, "approve-request", owner.Id, "", 0, "");
    var approvedMinutes = Convert.ToDouble(await Scalar($"SELECT extract(epoch FROM payment_expires_at-now())/60 FROM public.reservations WHERE app_id='{toApprove.Id}'"));
    Check(await Status(toApprove.Id) == "Pending Payment" && approvedMinutes is > 14 and <= 15.1, $"the store approval starts the 15-minute payment hold ({approvedMinutes:F1} min)");
    Check(Convert.ToInt32(await Scalar($"SELECT count(*) FROM public.notifications n JOIN public.account_identities a ON a.id=n.user_id JOIN public.reservations r ON r.id=n.reservation_id WHERE r.app_id='{toApprove.Id}' AND a.customer_id IS NOT NULL AND n.message LIKE '%Your early booking request was approved. Pay the down payment within 15 minutes to keep this time.'")) == 1,
        "the customer gets the approval notification once");
    Check(FlexRefusal(() => App().ManageBooking(toApprove.Id, "approve-request", owner.Id, "", 0, "")) == GlamByMariya.Infrastructure.Services.BookingService.EarlyRequestDecidedMessage, "a decided request cannot be approved twice");
    var toDecline = BookOn(laterDay.AddDays(1).AddHours(5));
    Check(FlexRefusal(() => App().ManageBooking(toDecline.Id, "decline-request", owner.Id, "", 0, "")) == "Enter a reason for declining this early booking request.", "declining needs a reason");
    App().ManageBooking(toDecline.Id, "decline-request", owner.Id, "Fully booked that morning.", 0, "");
    Check(await Status(toDecline.Id) == "Rejected" && (string)(await Scalar($"SELECT notes FROM public.reservations WHERE app_id='{toDecline.Id}'"))! == "Your early booking request was declined: Fully booked that morning.",
        "a declined request is Rejected with the reason the customer sees");
    Console.WriteLine($"All {checks - before} flexible booking database checks passed.");
}

sealed class TestEnv : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "DatabaseChecks";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = "";
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    public string WebRootPath { get; set; } = "";
    public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
}
